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

#include <stddef.h>
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

/*
 * Gives the session a terminal emulator with the given size in character cells. From then on
 * the remote side's output goes to the emulator instead of the host's `output` callback, and
 * keyboard input (see pe_set_key_target) goes through PuTTY's key translation and line
 * discipline to the connection. There is no renderer yet: the emulator has a null TermWin.
 * PE_ERR_STATE if the session is closed or already has a terminal.
 */
PE_API int32_t pe_session_enable_terminal(pe_session *session, int32_t columns, int32_t rows);

/*
 * Reports from the terminal emulator, called on the engine thread. Set with
 * pe_session_set_terminal_events; any member may be NULL. `title` text is UTF-8 and valid only
 * during the call. `scrollbar` gives the scrollback (total lines, first visible line, lines
 * on screen).
 */
typedef struct pe_terminal_events {
    void (*title)(void *user, const char *utf8_title);
    void (*bell)(void *user);
    void (*scrollbar)(void *user, int32_t total, int32_t start, int32_t page);
} pe_terminal_events;

PE_API int32_t pe_session_set_terminal_events(pe_session *session, const pe_terminal_events *events);

/*
 * Gives the session's terminal a drawing surface. The terminal is created if it is not there yet,
 * and its size becomes as many whole character cells as fit the surface in the given font; the
 * server is told. Draws to an offscreen bitmap (tests, headless rendering) or into a XAML
 * SwapChainPanel (`panel` is its IUnknown). PE_ERR_FAILED if the font is not installed or the
 * surface cannot be created; PE_ERR_STATE if the session already has a view.
 */
PE_API int32_t pe_session_attach_offscreen_view(pe_session *session, int32_t width_px, int32_t height_px,
                                                const wchar_t *font_family, float size_px);
PE_API int32_t pe_session_attach_panel_view(pe_session *session, void *panel, int32_t width_px,
                                            int32_t height_px, const wchar_t *font_family, float size_px);

/* The surface changed size (or the DPI scale did: pass the new physical size). */
PE_API int32_t pe_session_resize_view(pe_session *session, int32_t width_px, int32_t height_px);

/* A different font or size; the terminal is refitted to the surface. */
PE_API int32_t pe_session_set_font(pe_session *session, const wchar_t *font_family, float size_px);

typedef struct pe_terminal_geometry {
    int32_t columns;
    int32_t rows;
    int32_t cell_width;         /* pixels; 0 without a view */
    int32_t cell_height;
} pe_terminal_geometry;

PE_API int32_t pe_session_terminal_geometry(pe_session *session, pe_terminal_geometry *out);

/* Offscreen views only: one pixel of the last finished frame, 0x00RRGGBB. For tests. */
PE_API int32_t pe_diag_session_read_pixel(pe_session *session, int32_t x, int32_t y, uint32_t *rgb);

/*
 * Local echo and local line editing, the two Terminal settings that decide whether the line
 * discipline buffers typing until Enter. Each is PE_LOCAL_AUTO (the protocol decides: on for
 * Raw, off for SSH and, once negotiated, Telnet), PE_LOCAL_ON or PE_LOCAL_OFF. The session needs
 * a terminal.
 */
#define PE_LOCAL_AUTO 0
#define PE_LOCAL_ON   1
#define PE_LOCAL_OFF  2
PE_API int32_t pe_session_set_local_echo_edit(pe_session *session, int32_t echo, int32_t edit);

/*
 * Names the session whose terminal has keyboard focus, or NULL if none does. While one does,
 * a message hook on the engine thread takes keyboard messages and turns them into terminal
 * input exactly as PuTTY's own window would; with none it does nothing. The session must have
 * a terminal. The target is cleared when the session closes and by pe_shutdown.
 */
PE_API int32_t pe_set_key_target(pe_session *session);

/* ----- Warnings that belong to no session ------------------------------------------ */

/* What PuTTY's core reports through its process-level callbacks. */
#define PE_WARNING_NONFATAL       0   /* a problem that does not stop anything, e.g. the random seed
                                         file could not be written */
#define PE_WARNING_OLD_KEY_FORMAT 1   /* a loaded SSH-2 private key uses the old, not fully
                                         tamperproof file format */

/*
 * Called on the engine thread with UTF-8 text that is valid only during the call. Without a
 * handler warnings go to the debugger only. The handler is dropped by pe_shutdown().
 */
typedef void (*pe_warning_fn)(void *user, int32_t kind, const char *message);

PE_API int32_t pe_set_warning_handler(pe_warning_fn handler, void *user);

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

/* Makes PuTTY's core raise a warning of the given PE_WARNING_* kind, as it would itself. */
PE_API int32_t pe_diag_raise_warning(int32_t kind);

/*
 * Draws `text` (1 to 64 characters, one cell each, spaces left empty) with the terminal renderer
 * onto an offscreen bitmap and reports what ended up where. Needs no window and no engine.
 */
typedef struct pe_diag_render_result {
    int32_t cell_width;         /* pixels */
    int32_t cell_height;
    int32_t cells;              /* how many characters were drawn */
    int32_t ink[64];            /* per cell: pixels that differ from the background */
    uint32_t corner_rgb;        /* 0x00RRGGBB of pixel (0, 0) */
} pe_diag_render_result;

PE_API int32_t pe_diag_render_text(const wchar_t *font_family, float size_px, const wchar_t *text,
                                   uint32_t fg_rgb, uint32_t bg_rgb, int32_t bold,
                                   pe_diag_render_result *out);

#ifdef __cplusplus
}
#endif

#endif /* PUTTY_ENGINE_H */
