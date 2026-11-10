/*
 * putty_engine.h - flat C ABI of the PuTTY engine DLL.
 *
 * This is the only interface the managed side (PuTTY.Interop) sees. Keep it
 * blittable: fixed-width integers, pointers, and function pointers. No PuTTY
 * types appear here, and PuTTY headers must not be included from C++.
 *
 * Threading: PuTTY's core is single-threaded with global state, so the engine
 * lives on ONE thread, the one that calls pe_init(). That thread must run a
 * Win32 message loop (the WinUI UI thread already does; tests call pe_pump()).
 * Every other pe_* call must come from the same thread and returns
 * PE_ERR_THREAD otherwise.
 */

#ifndef PUTTY_ENGINE_H
#define PUTTY_ENGINE_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(PUTTY_ENGINE_BUILD)
#  define PE_API __declspec(dllexport)
#else
#  define PE_API __declspec(dllimport)
#endif

/* Bump on any incompatible change to this header. */
#define PE_ABI_VERSION 1u

/* Result codes: 0 is success, errors are negative. */
#define PE_OK           0
#define PE_ERR_STATE   (-1)   /* wrong state, e.g. not initialised or already initialised */
#define PE_ERR_THREAD  (-2)   /* called from a thread other than the engine thread */
#define PE_ERR_FAILED  (-3)   /* an operating-system call failed */

PE_API uint32_t pe_abi_version(void);

/*
 * Smoke test that exercises PuTTY's Conf store (utils/conf.c) through the
 * linked static library. Returns 1 on success, 0 on failure.
 */
PE_API int32_t pe_probe(void);

/*
 * Starts the engine on the calling thread: creates its hidden message window and wires
 * PuTTY's timers, deferred callbacks and socket events to it. May be called again after
 * pe_shutdown(), including from a different thread.
 */
PE_API int32_t pe_init(void);

/* Closes all sockets, drains pending work and tears the engine down. */
PE_API int32_t pe_shutdown(void);

/*
 * Waits up to timeout_ms for window messages and dispatches whatever is queued. Hosts
 * that already run a message loop (the UI thread) never call this; it exists for threads
 * that have none, such as tests. Returns the number of messages dispatched, or a negative
 * PE_ERR_* code.
 */
PE_API int32_t pe_pump(uint32_t timeout_ms);

/* ----- Sessions ---------------------------------------------------------------------- */

/* Opaque handle to one connection. Valid from pe_session_open until pe_session_close. */
typedef struct pe_session pe_session;

#define PE_PROTO_RAW     0
#define PE_PROTO_TELNET  1
#define PE_PROTO_RLOGIN  2
#define PE_PROTO_SSH     3

/*
 * What the engine reports about a session. Every callback runs on the engine thread, with
 * the 'user' pointer given to pe_session_open. Any of them may be NULL. Strings are UTF-8
 * and valid only for the duration of the call.
 *
 * A callback may call pe_session_close for its own session; the engine defers the actual
 * teardown until it is safe, and stops delivering events for that session at once.
 */
typedef struct pe_session_events {
    /* Bytes from the remote side. is_stderr is non-zero for the error stream. */
    void (*output)(void *user, const void *data, uint32_t length, int32_t is_stderr);

    /* One line of PuTTY's event log, such as "Connecting to 127.0.0.1 port 22". */
    void (*log)(void *user, const char *text);

    /* A problem. is_fatal is non-zero if the connection cannot continue. */
    void (*error)(void *user, const char *message, int32_t is_fatal);

    /* The connection has ended. Delivered once; the session still has to be closed. */
    void (*closed)(void *user);
} pe_session_events;

/*
 * Opens a connection. The call returns at once; the connection completes asynchronously
 * and everything that happens is reported through 'events'. If it fails immediately the
 * 'error' callback has already been called and PE_ERR_FAILED is returned. The events
 * structure is copied, so it may live on the caller's stack.
 */
PE_API int32_t pe_session_open(const char *host, int32_t port, int32_t protocol,
                               const pe_session_events *events, void *user,
                               pe_session **out_session);

/* Sends bytes to the remote side. PE_ERR_STATE if the session is closed or invalid. */
PE_API int32_t pe_session_send(pe_session *session, const void *data, uint32_t length);

/* Returns 1 while the connection is up, 0 once it has ended, or a negative error code. */
PE_API int32_t pe_session_is_connected(pe_session *session);

/* Closes the connection and frees the session. The handle is invalid afterwards. */
PE_API int32_t pe_session_close(pe_session *session);

/* ----- Diagnostics, used by tests to check the event loop ------------------------- */

typedef struct pe_diag_counters {
    uint32_t callbacks_run;     /* diagnostic deferred callbacks that have run */
    uint32_t timers_fired;      /* diagnostic timers that have fired */
} pe_diag_counters;

/* Queues a deferred callback through PuTTY's top-level callback queue. */
PE_API int32_t pe_diag_queue_callback(void);

/* Schedules a PuTTY timer that fires after delay_ms. */
PE_API int32_t pe_diag_schedule_timer(uint32_t delay_ms);

/* Reads the counters. They are reset to zero by pe_init(). */
PE_API int32_t pe_diag_get_counters(pe_diag_counters *out);

#ifdef __cplusplus
}
#endif

#endif /* PUTTY_ENGINE_H */
