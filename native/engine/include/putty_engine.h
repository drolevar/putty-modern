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
