/*
 * session.c - one PuTTY connection, as seen by the host.
 *
 * A session owns the three things PuTTY needs to run a connection: a Conf (the
 * settings), a LogContext and a Backend (SSH, Telnet, ...). PuTTY talks back to its front
 * end through two interfaces, both implemented here per session:
 *
 *   - Seat:      everything the backend says to its client (output, errors, lifecycle).
 *   - LogPolicy: where the event log goes.
 *
 * Both are vtables, so a missing method is a crash at run time, not a link error. Every
 * Seat method is therefore listed explicitly below; the ones we do not handle yet point at
 * PuTTY's own nullseat_* defaults, and the interactive ones (host-key confirmation,
 * password prompts) are deliberately left to fail safely until the prompt work in slice 3.
 *
 * Everything runs on the engine thread, so there is no locking.
 */

#include <string.h>

#include "putty.h"
#include "putty_engine.h"
#include "engine_internal.h"

struct pe_session {
    Seat seat;
    LogPolicy logpolicy;
    Conf *conf;
    LogContext *logctx;
    Backend *backend;
    char *realhost;

    /* Present once pe_session_enable_terminal has been called. */
    TermWin termwin;            /* a null TermWin: the emulator runs, nothing is drawn yet */
    Terminal *term;
    Ldisc *ldisc;
    struct unicode_data ucsdata;
    pe_keyctx keys;             /* what key translation needs; see keys.c */

    pe_session_events events;   /* a copy, so the caller's may live on the stack */
    void *user;

    bool closed_notified;       /* 'closed' is delivered once */
    bool pending_close;         /* closing was requested from inside a callback */
    int callback_depth;         /* how many host callbacks are on the stack */

    pe_session *next;           /* the list of live sessions */
};

#define SESSION_FROM_SEAT(p) container_of(p, pe_session, seat)
#define SESSION_FROM_LOGPOLICY(p) container_of(p, pe_session, logpolicy)
#define SESSION_FROM_TERMWIN(p) container_of(p, pe_session, termwin)

static pe_session *live_sessions;

/* ----- Handle validation ---------------------------------------------------------------
 *
 * Handles come from managed code, so a stale or wrong one must never reach PuTTY. Looking
 * the pointer up in the list of live sessions is cheap (there are few) and turns a
 * use-after-free into an error code.
 */
static bool session_is_live(const pe_session *session)
{
    for (const pe_session *s = live_sessions; s; s = s->next) {
        if (s == session)
            return true;
    }
    return false;
}

/* ----- Calling back into the host ------------------------------------------------------- */

/*
 * Runs a host callback unless the session is being closed. callback_depth lets
 * pe_session_close know it was called from inside one, which is when freeing the backend
 * immediately would pull it out from under PuTTY code that is still running.
 */
#define HOST_EVENT(s, fn, ...)                                           \
    do {                                                                    \
        if (!(s)->pending_close && (s)->events.fn) {                     \
            (s)->callback_depth++;                                          \
            (s)->events.fn((s)->user, __VA_ARGS__);                      \
            (s)->callback_depth--;                                          \
        }                                                                   \
    } while (0)

#define HOST_EVENT_NOARGS(s, fn)                                         \
    do {                                                                    \
        if (!(s)->pending_close && (s)->events.fn) {                     \
            (s)->callback_depth++;                                          \
            (s)->events.fn((s)->user);                                   \
            (s)->callback_depth--;                                          \
        }                                                                   \
    } while (0)

static void notify_closed_once(pe_session *s)
{
    if (s->closed_notified)
        return;
    s->closed_notified = true;
    HOST_EVENT_NOARGS(s, closed);
}

/* ----- Seat ------------------------------------------------------------------------------ */

static size_t session_output(Seat *seat, SeatOutputType type, const void *data, size_t len)
{
    pe_session *s = SESSION_FROM_SEAT(seat);
    if (s->term)
        return term_data(s->term, data, len);   /* the emulator keeps a backlog and throttles us */
    if (len > 0) {
        HOST_EVENT(s, output, data, (uint32_t)len, type == SEAT_OUTPUT_STDERR ? 1 : 0);
    }
    return 0;                   /* we never hold output back: no backlog */
}

/* An end-user seat treats the authentication banner as ordinary output. */
static size_t session_banner(Seat *seat, const void *data, size_t len)
{
    return session_output(seat, SEAT_OUTPUT_STDOUT, data, len);
}

/*
 * The connection has gone, or is worth re-checking. PuTTY documents that this may arrive
 * more than once, even before backend_connected() has changed, so check, and report once.
 */
static void session_notify_remote_disconnect(Seat *seat)
{
    pe_session *s = SESSION_FROM_SEAT(seat);
    if (s->backend && backend_connected(s->backend))
        return;
    notify_closed_once(s);
}

static void session_connection_fatal(Seat *seat, const char *message)
{
    pe_session *s = SESSION_FROM_SEAT(seat);
    HOST_EVENT(s, error, message, 1);
    /* A fatal error ends the connection whether or not the backend also says so. */
    notify_closed_once(s);
}

static void session_nonfatal(Seat *seat, const char *message)
{
    pe_session *s = SESSION_FROM_SEAT(seat);
    HOST_EVENT(s, error, message, 0);
}

static const SeatVtable session_seat_vt = {
    .output = session_output,
    .eof = nullseat_eof,                    /* remote EOF: if they close, so do we */
    .sent = nullseat_sent,
    .banner = session_banner,
    .get_userpass_input = nullseat_get_userpass_input,  /* aborts safely: prompts are slice 3 */
    .notify_session_started = nullseat_notify_session_started,
    .notify_remote_exit = nullseat_notify_remote_exit,
    .notify_remote_disconnect = session_notify_remote_disconnect,
    .connection_fatal = session_connection_fatal,
    .nonfatal = session_nonfatal,
    .update_specials_menu = nullseat_update_specials_menu,
    .get_ttymode = nullseat_get_ttymode,
    .set_busy_status = nullseat_set_busy_status,
    .confirm_ssh_host_key = nullseat_confirm_ssh_host_key,                  /* aborts: slice 3 */
    .confirm_weak_crypto_primitive = nullseat_confirm_weak_crypto_primitive,
    .confirm_weak_cached_hostkey = nullseat_confirm_weak_cached_hostkey,
    .prompt_descriptions = nullseat_prompt_descriptions,
    .is_utf8 = nullseat_is_always_utf8,
    .echoedit_update = nullseat_echoedit_update,
    .get_display = nullseat_get_display,
    .get_windowid = nullseat_get_windowid,
    .get_window_pixel_size = nullseat_get_window_pixel_size,
    .stripctrl_new = nullseat_stripctrl_new,
    .set_trust_status = nullseat_set_trust_status,
    .can_set_trust_status = nullseat_can_set_trust_status_no,
    .has_mixed_input_stream = nullseat_has_mixed_input_stream_no,
    .verbose = nullseat_verbose_no,
    .interactive = nullseat_interactive_no, /* no prompting yet */
    .get_cursor_position = nullseat_get_cursor_position,
};

/* ----- LogPolicy ------------------------------------------------------------------------- */

static void session_eventlog(LogPolicy *lp, const char *event)
{
    pe_session *s = SESSION_FROM_LOGPOLICY(lp);
    HOST_EVENT(s, log, event);
}

/* We do not log to files yet, so there is never an existing file to ask about. */
static int session_askappend(LogPolicy *lp, Filename *filename,
                             void (*callback)(void *ctx, int result), void *ctx)
{
    (void)lp;
    (void)filename;
    (void)callback;
    (void)ctx;
    return 0;                   /* cancel */
}

static void session_logging_error(LogPolicy *lp, const char *event)
{
    pe_session *s = SESSION_FROM_LOGPOLICY(lp);
    HOST_EVENT(s, error, event, 0);
}

static const LogPolicyVtable session_logpolicy_vt = {
    .eventlog = session_eventlog,
    .askappend = session_askappend,
    .logging_error = session_logging_error,
    .verbose = null_lp_verbose_no,
};

/* ----- TermWin (null) --------------------------------------------------------------------
 *
 * The emulator needs a TermWin to exist. This one draws nothing; the renderer replaces it in
 * slice 2. Every method is listed because it is a vtable: a missing one is a crash. The two
 * with obligations: request_resize must be answered with term_resize_request_completed, and
 * unthrottle must reach the backend.
 */

static bool nulltw_setup_draw_ctx(TermWin *tw) { (void)tw; return false; }
static void nulltw_draw_text(TermWin *tw, int x, int y, wchar_t *text, int len,
                             unsigned long attrs, int line_attrs, truecolour tc)
{ (void)tw; (void)x; (void)y; (void)text; (void)len; (void)attrs; (void)line_attrs; (void)tc; }
static void nulltw_draw_cursor(TermWin *tw, int x, int y, wchar_t *text, int len,
                               unsigned long attrs, int line_attrs, truecolour tc)
{ (void)tw; (void)x; (void)y; (void)text; (void)len; (void)attrs; (void)line_attrs; (void)tc; }
static void nulltw_draw_trust_sigil(TermWin *tw, int x, int y) { (void)tw; (void)x; (void)y; }
static int nulltw_char_width(TermWin *tw, int uc) { (void)tw; (void)uc; return 1; }
static void nulltw_free_draw_ctx(TermWin *tw) { (void)tw; }
static void nulltw_set_cursor_pos(TermWin *tw, int x, int y) { (void)tw; (void)x; (void)y; }
static void nulltw_set_raw_mouse_mode(TermWin *tw, bool enable) { (void)tw; (void)enable; }
static void nulltw_set_raw_mouse_mode_pointer(TermWin *tw, bool enable) { (void)tw; (void)enable; }
static void nulltw_set_scrollbar(TermWin *tw, int total, int start, int page)
{ (void)tw; (void)total; (void)start; (void)page; }
static void nulltw_bell(TermWin *tw, int mode) { (void)tw; (void)mode; }
static void nulltw_clip_write(TermWin *tw, int clipboard, wchar_t *text, int *attrs,
                              truecolour *colours, int len, bool must_deselect)
{ (void)tw; (void)clipboard; (void)text; (void)attrs; (void)colours; (void)len; (void)must_deselect; }
static void nulltw_clip_request_paste(TermWin *tw, int clipboard) { (void)tw; (void)clipboard; }
static void nulltw_refresh(TermWin *tw) { (void)tw; }
static void nulltw_request_resize(TermWin *tw, int w, int h)
{
    (void)w; (void)h;
    pe_session *s = SESSION_FROM_TERMWIN(tw);
    if (s->term)
        term_resize_request_completed(s->term);   /* we declined, which is allowed */
}
static void nulltw_set_title(TermWin *tw, const char *title, int codepage)
{ (void)tw; (void)title; (void)codepage; }
static void nulltw_set_icon_title(TermWin *tw, const char *icontitle, int codepage)
{ (void)tw; (void)icontitle; (void)codepage; }
static void nulltw_set_minimised(TermWin *tw, bool minimised) { (void)tw; (void)minimised; }
static void nulltw_set_maximised(TermWin *tw, bool maximised) { (void)tw; (void)maximised; }
static void nulltw_move(TermWin *tw, int x, int y) { (void)tw; (void)x; (void)y; }
static void nulltw_set_zorder(TermWin *tw, bool top) { (void)tw; (void)top; }
static void nulltw_palette_set(TermWin *tw, unsigned start, unsigned ncolours, const rgb *colours)
{ (void)tw; (void)start; (void)ncolours; (void)colours; }
static void nulltw_palette_get_overrides(TermWin *tw, Terminal *term) { (void)tw; (void)term; }
static void nulltw_unthrottle(TermWin *tw, size_t bufsize)
{
    pe_session *s = SESSION_FROM_TERMWIN(tw);
    if (s->backend)
        backend_unthrottle(s->backend, bufsize);
}

static const TermWinVtable null_termwin_vt = {
    .setup_draw_ctx = nulltw_setup_draw_ctx,
    .draw_text = nulltw_draw_text,
    .draw_cursor = nulltw_draw_cursor,
    .draw_trust_sigil = nulltw_draw_trust_sigil,
    .char_width = nulltw_char_width,
    .free_draw_ctx = nulltw_free_draw_ctx,
    .set_cursor_pos = nulltw_set_cursor_pos,
    .set_raw_mouse_mode = nulltw_set_raw_mouse_mode,
    .set_raw_mouse_mode_pointer = nulltw_set_raw_mouse_mode_pointer,
    .set_scrollbar = nulltw_set_scrollbar,
    .bell = nulltw_bell,
    .clip_write = nulltw_clip_write,
    .clip_request_paste = nulltw_clip_request_paste,
    .refresh = nulltw_refresh,
    .request_resize = nulltw_request_resize,
    .set_title = nulltw_set_title,
    .set_icon_title = nulltw_set_icon_title,
    .set_minimised = nulltw_set_minimised,
    .set_maximised = nulltw_set_maximised,
    .move = nulltw_move,
    .set_zorder = nulltw_set_zorder,
    .palette_set = nulltw_palette_set,
    .palette_get_overrides = nulltw_palette_get_overrides,
    .unthrottle = nulltw_unthrottle,
};

/* ----- Life cycle ----------------------------------------------------------------------- */

static int protocol_from_engine(int32_t protocol)
{
    switch (protocol) {
      case PE_PROTO_RAW:    return PROT_RAW;
      case PE_PROTO_TELNET: return PROT_TELNET;
      case PE_PROTO_RLOGIN: return PROT_RLOGIN;
      case PE_PROTO_SSH:    return PROT_SSH;
    }
    return -1;
}

static void session_unlink(pe_session *session)
{
    for (pe_session **pp = &live_sessions; *pp; pp = &(*pp)->next) {
        if (*pp == session) {
            *pp = session->next;
            return;
        }
    }
}

/* Frees the session for real. Never call this from inside one of its own callbacks. */
static void session_destroy(pe_session *s)
{
    session_unlink(s);
    pe_keys_session_closing(&s->keys);
    /* The order window.c uses: line discipline, then detach the emulator, then the backend. */
    if (s->ldisc)
        ldisc_free(s->ldisc);
    if (s->term)
        term_provide_backend(s->term, NULL);
    if (s->backend) {
        backend_free(s->backend);
        s->backend = NULL;      /* the null TermWin may still be asked to unthrottle */
    }
    if (s->term) {
        term_provide_logctx(s->term, NULL);
        term_free(s->term);
    }
    if (s->logctx)
        log_free(s->logctx);
    if (s->conf)
        conf_free(s->conf);
    sfree(s->realhost);
    sfree(s);
}

PE_API int32_t pe_session_open(const char *host, int32_t port, int32_t protocol,
                               const pe_session_events *events, void *user,
                               pe_session **out_session)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!host || !*host || port < 1 || port > 65535 || !events || !out_session)
        return PE_ERR_FAILED;
    int putty_protocol = protocol_from_engine(protocol);
    if (putty_protocol < 0)
        return PE_ERR_FAILED;
    const BackendVtable *vt = backend_vt_from_proto(putty_protocol);
    if (!vt)
        return PE_ERR_FAILED;   /* this protocol is not built into the engine */

    pe_session *s = snew(pe_session);
    memset(s, 0, sizeof(*s));
    s->seat.vt = &session_seat_vt;
    s->logpolicy.vt = &session_logpolicy_vt;
    s->events = *events;
    s->user = user;

    /* Built-in defaults only: loading the user's saved settings is slice 4. */
    s->conf = conf_new();
    load_open_settings(NULL, s->conf);
    conf_set_str(s->conf, CONF_host, host);
    conf_set_int(s->conf, CONF_port, port);
    conf_set_int(s->conf, CONF_protocol, putty_protocol);

    s->logctx = log_init(&s->logpolicy, s->conf);

    /* Linked in before the backend starts: it may call back before backend_init returns. */
    s->next = live_sessions;
    live_sessions = s;

    char *message = backend_init(vt, &s->seat, &s->backend, s->logctx, s->conf, host, port,
                               &s->realhost, conf_get_bool(s->conf, CONF_tcp_nodelay),
                               conf_get_bool(s->conf, CONF_tcp_keepalives));
    if (message) {
        HOST_EVENT(s, error, message, 1);
        sfree(message);
        s->backend = NULL;          /* backend_init leaves nothing to free on failure */
        session_destroy(s);
        return PE_ERR_FAILED;
    }

    *out_session = s;
    return PE_OK;
}

PE_API int32_t pe_session_enable_terminal(pe_session *s, int32_t columns, int32_t rows)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!session_is_live(s) || s->pending_close || !s->backend || s->term)
        return PE_ERR_STATE;
    if (columns < 1 || columns > 1000 || rows < 1 || rows > 1000)
        return PE_ERR_FAILED;

    /* A modern default: send UTF-8. Configurable once the settings slice lands. */
    conf_set_str(s->conf, CONF_line_codepage, "UTF-8");
    init_ucs(s->conf, &s->ucsdata);

    /* The order window.c uses (WinMain, then start_backend). */
    s->termwin.vt = &null_termwin_vt;
    s->term = term_init(s->conf, &s->ucsdata, &s->termwin);
    term_provide_logctx(s->term, s->logctx);
    term_size(s->term, rows, columns, conf_get_int(s->conf, CONF_savelines));
    term_setup_window_titles(s->term, s->realhost ? s->realhost : "");
    term_provide_backend(s->term, s->backend);
    s->ldisc = ldisc_create(s->conf, s->term, s->backend, &s->seat);
    backend_size(s->backend, columns, rows);

    s->keys.conf = s->conf;
    s->keys.term = s->term;
    s->keys.backend = s->backend;
    return PE_OK;
}

static int local_setting(int32_t value, bool *ok)
{
    switch (value) {
      case PE_LOCAL_AUTO: return AUTO;
      case PE_LOCAL_ON:   return FORCE_ON;
      case PE_LOCAL_OFF:  return FORCE_OFF;
    }
    *ok = false;
    return AUTO;
}

PE_API int32_t pe_session_set_local_echo_edit(pe_session *s, int32_t echo, int32_t edit)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!session_is_live(s) || s->pending_close || !s->ldisc)
        return PE_ERR_STATE;
    bool ok = true;
    int echo_setting = local_setting(echo, &ok);
    int edit_setting = local_setting(edit, &ok);
    if (!ok)
        return PE_ERR_FAILED;

    conf_set_int(s->conf, CONF_localecho, echo_setting);
    conf_set_int(s->conf, CONF_localedit, edit_setting);
    ldisc_configure(s->ldisc, s->conf);
    ldisc_echoedit_update(s->ldisc);
    return PE_OK;
}

pe_keyctx *pe_session_keyctx(pe_session *session)
{
    if (!session_is_live(session) || session->pending_close || !session->term)
        return NULL;
    return &session->keys;
}

PE_API int32_t pe_session_send(pe_session *session, const void *data, uint32_t length)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!session_is_live(session) || session->pending_close || !session->backend ||
        !backend_connected(session->backend))
        return PE_ERR_STATE;
    if (length > 0)
        backend_send(session->backend, (const char *)data, length);
    return PE_OK;
}

PE_API int32_t pe_session_is_connected(pe_session *session)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!session_is_live(session) || session->pending_close || !session->backend)
        return 0;
    return backend_connected(session->backend) ? 1 : 0;
}

static void deferred_destroy(void *ctx)
{
    pe_session *s = (pe_session *)ctx;
    if (session_is_live(s))     /* pe_shutdown may have closed it already */
        session_destroy(s);
}

PE_API int32_t pe_session_close(pe_session *session)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!session_is_live(session))
        return PE_ERR_STATE;
    if (session->pending_close)
        return PE_OK;           /* already on its way out */

    if (session->callback_depth > 0) {
        /*
         * Called from inside one of this session's own callbacks, which PuTTY may have
         * invoked from deep within the backend (raw_closing, for one). Freeing the backend
         * now would pull it out from under that code, so go quiet at once and let the
         * top-level callback queue do the teardown after PuTTY has unwound.
         */
        session->pending_close = true;
        queue_toplevel_callback(deferred_destroy, session);
        return PE_OK;
    }

    session_destroy(session);
    return PE_OK;
}

void pe_sessions_close_all(void)
{
    while (live_sessions)
        session_destroy(live_sessions);
}
