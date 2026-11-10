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
#include "terminal/terminal.h"
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
    pe_termwin termwin;         /* what the emulator draws through; headless until a view is attached */
    Terminal *term;
    Ldisc *ldisc;
    struct unicode_data ucsdata;
    pe_keyctx keys;             /* what key translation needs; see keys.c */
    pe_terminal_events term_events;     /* title, bell and scrollbar reports, set by the host */
    int32_t view_width, view_height;    /* pixels of the attached view */
    bool repaint_pending;

    pe_session_events events;   /* a copy, so the caller's may live on the stack */
    void *user;

    bool closed_notified;       /* 'closed' is delivered once */
    bool trusted;               /* what the backend last said; applied to the emulator when it exists */
    bool pending_close;         /* closing was requested from inside a callback */
    int callback_depth;         /* how many host callbacks are on the stack */

    pe_session *next;           /* the list of live sessions */
};

#define SESSION_FROM_SEAT(p) container_of(p, pe_session, seat)
#define SESSION_FROM_LOGPOLICY(p) container_of(p, pe_session, logpolicy)

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

/*
 * Trust sigils. The emulator marks lines written while it is "trusted", meaning PuTTY itself is
 * talking (a login prompt), so a remote program cannot fake one. Backends say when that ends
 * (Raw and Telnet at once, SSH after authentication). Remember the answer, because the emulator
 * may not exist yet when the backend says it.
 */
static void session_set_trust_status(Seat *seat, bool trusted)
{
    pe_session *s = SESSION_FROM_SEAT(seat);
    s->trusted = trusted;
    if (s->term)
        term_set_trust_status(s->term, trusted);
}

static bool session_get_cursor_position(Seat *seat, int *x, int *y)
{
    pe_session *s = SESSION_FROM_SEAT(seat);
    if (!s->term)
        return false;
    term_get_cursor_position(s->term, x, y);
    return true;
}

static bool session_get_window_pixel_size(Seat *seat, int *x, int *y)
{
    pe_session *s = SESSION_FROM_SEAT(seat);
    if (!s->termwin.renderer)
        return false;
    *x = s->view_width;
    *y = s->view_height;
    return true;
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
    .get_window_pixel_size = session_get_window_pixel_size,
    .stripctrl_new = nullseat_stripctrl_new,
    .set_trust_status = session_set_trust_status,
    .can_set_trust_status = nullseat_can_set_trust_status_yes,
    .has_mixed_input_stream = nullseat_has_mixed_input_stream_no,
    .verbose = nullseat_verbose_no,
    .interactive = nullseat_interactive_no, /* no prompting yet */
    .get_cursor_position = session_get_cursor_position,
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
    pe_termwin_destroy(&s->termwin);
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
    s->trusted = true;          /* until a backend says otherwise */
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
    pe_termwin_init(&s->termwin, s);
    s->term = term_init(s->conf, &s->ucsdata, &s->termwin.tw);
    pe_termwin_use_settings(&s->termwin, s->term, s->conf);
    term_set_trust_status(s->term, s->trusted);
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

/* ----- The view: the emulator's size, its renderer and what it reports ---------------------- */

/* Calls a host report unless the session is closing (see HOST_EVENT). */
#define TERMINAL_EVENT(s, fn, ...)                                       \
    do {                                                                    \
        if (!(s)->pending_close && (s)->term_events.fn) {                \
            (s)->callback_depth++;                                          \
            (s)->term_events.fn((s)->user, __VA_ARGS__);                 \
            (s)->callback_depth--;                                          \
        }                                                                   \
    } while (0)

#define TERMINAL_EVENT_NOARGS(s, fn)                                     \
    do {                                                                    \
        if (!(s)->pending_close && (s)->term_events.fn) {                \
            (s)->callback_depth++;                                          \
            (s)->term_events.fn((s)->user);                              \
            (s)->callback_depth--;                                          \
        }                                                                   \
    } while (0)

void pe_session_unthrottle(void *owner, size_t bufsize)
{
    pe_session *s = (pe_session *)owner;
    if (session_is_live(s) && s->backend)
        backend_unthrottle(s->backend, bufsize);
}

void pe_session_notify_title(void *owner, const char *utf8_title)
{
    pe_session *s = (pe_session *)owner;
    if (session_is_live(s))
        TERMINAL_EVENT(s, title, utf8_title);
}

void pe_session_notify_bell(void *owner)
{
    pe_session *s = (pe_session *)owner;
    if (session_is_live(s))
        TERMINAL_EVENT_NOARGS(s, bell);
}

void pe_session_notify_scrollbar(void *owner, int total, int start, int page)
{
    pe_session *s = (pe_session *)owner;
    if (session_is_live(s))
        TERMINAL_EVENT(s, scrollbar, total, start, page);
}

static void repaint_callback(void *ctx)
{
    pe_session *s = (pe_session *)ctx;
    if (!session_is_live(s))        /* it may have been closed since this was queued */
        return;
    s->repaint_pending = false;
    if (s->term) {
        term_invalidate(s->term);
        term_update(s->term);
    }
}

/* A repaint of everything, soon but not from inside the emulator's own drawing. */
void pe_session_schedule_repaint(void *owner)
{
    pe_session *s = (pe_session *)owner;
    if (!session_is_live(s) || s->repaint_pending)
        return;
    s->repaint_pending = true;
    queue_toplevel_callback(repaint_callback, s);
}

PE_API int32_t pe_session_set_terminal_events(pe_session *s, const pe_terminal_events *events)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!session_is_live(s) || !events)
        return PE_ERR_STATE;
    s->term_events = *events;
    return PE_OK;
}

/* After the font or the view size changed: how many cells fit, tell the emulator and the server, repaint. */
static void apply_view_geometry(pe_session *s)
{
    pe_renderer *r = s->termwin.renderer;
    int32_t cw = 0, ch = 0;
    pe_render_cell_size(r, &cw, &ch);
    int cols = cw > 0 ? s->view_width / cw : 1;
    int rows = ch > 0 ? s->view_height / ch : 1;
    if (cols < 1) cols = 1;
    if (rows < 1) rows = 1;
    term_size(s->term, rows, cols, conf_get_int(s->conf, CONF_savelines));
    if (s->backend)
        backend_size(s->backend, cols, rows);
    term_invalidate(s->term);
    term_update(s->term);
}

static int32_t attach_view(pe_session *s, pe_renderer *r, const wchar_t *font, float size_px,
                           int32_t width_px, int32_t height_px)
{
    if (!r)
        return PE_ERR_FAILED;
    if (!pe_render_set_font(r, font, size_px)) {
        pe_render_destroy(r);
        return PE_ERR_FAILED;
    }
    if (!s->term) {
        /* No terminal yet: start one at a placeholder size, apply_view_geometry corrects it. */
        int32_t rc = pe_session_enable_terminal(s, 80, 24);
        if (rc != PE_OK) {
            pe_render_destroy(r);
            return rc;
        }
    }
    s->view_width = width_px;
    s->view_height = height_px;
    pe_termwin_set_renderer(&s->termwin, r);
    apply_view_geometry(s);
    return PE_OK;
}

static bool view_usable(pe_session *s)
{
    return session_is_live(s) && !s->pending_close && s->term && s->termwin.renderer;
}

PE_API int32_t pe_session_attach_offscreen_view(pe_session *s, int32_t width_px, int32_t height_px,
                                                const wchar_t *font_family, float size_px)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!session_is_live(s) || s->pending_close || !s->backend || s->termwin.renderer || !font_family)
        return PE_ERR_STATE;
    return attach_view(s, pe_render_create_offscreen(width_px, height_px), font_family, size_px,
                       width_px, height_px);
}

PE_API int32_t pe_session_attach_panel_view(pe_session *s, void *panel, int32_t width_px,
                                            int32_t height_px, const wchar_t *font_family, float size_px)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!session_is_live(s) || s->pending_close || !s->backend || s->termwin.renderer || !font_family || !panel)
        return PE_ERR_STATE;
    return attach_view(s, pe_render_create_for_panel(panel, width_px, height_px), font_family, size_px,
                       width_px, height_px);
}

PE_API int32_t pe_session_resize_view(pe_session *s, int32_t width_px, int32_t height_px)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!view_usable(s))
        return PE_ERR_STATE;
    if (!pe_render_resize(s->termwin.renderer, width_px, height_px))
        return PE_ERR_FAILED;
    s->view_width = width_px;
    s->view_height = height_px;
    apply_view_geometry(s);
    return PE_OK;
}

PE_API int32_t pe_session_set_font(pe_session *s, const wchar_t *font_family, float size_px)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!view_usable(s) || !font_family)
        return PE_ERR_STATE;
    if (!pe_render_set_font(s->termwin.renderer, font_family, size_px))
        return PE_ERR_FAILED;
    apply_view_geometry(s);
    return PE_OK;
}

PE_API int32_t pe_session_terminal_geometry(pe_session *s, pe_terminal_geometry *out)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!session_is_live(s) || !s->term || !out)
        return PE_ERR_STATE;
    out->columns = s->term->cols;
    out->rows = s->term->rows;
    out->cell_width = 0;
    out->cell_height = 0;
    if (s->termwin.renderer)
        pe_render_cell_size(s->termwin.renderer, &out->cell_width, &out->cell_height);
    return PE_OK;
}

PE_API int32_t pe_diag_session_read_pixel(pe_session *s, int32_t x, int32_t y, uint32_t *rgb)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!view_usable(s) || !rgb)
        return PE_ERR_STATE;
    return pe_render_read_pixel(s->termwin.renderer, x, y, rgb) ? PE_OK : PE_ERR_FAILED;
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
