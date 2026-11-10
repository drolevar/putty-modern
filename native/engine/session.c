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

    pe_session_events events;   /* a copy, so the caller's may live on the stack */
    void *user;

    bool closed_notified;       /* 'closed' is delivered once */
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
    if (s->backend)
        backend_free(s->backend);
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
