/*
 * eventloop.c - runs PuTTY's event-driven core on the calling thread.
 *
 * PuTTY's core never blocks. It asks its front end for three things, and a GUI front end
 * (windows/window.c) provides them through one window, as we do:
 *
 *   - socket events:     WSAAsyncSelect posts WM_NETEVENT to the window selected with
 *                        winselgui_set_hwnd(); we hand those to winselgui_response().
 *   - timers:            utils/gui-timing.c owns a hidden window and SetTimer; we only
 *                        call setup_gui_timing().
 *   - deferred callbacks: callback.c queues work "to run from the top-level event loop"
 *                        and tells us (once, when the queue stops being empty) through
 *                        request_callback_notifications(). We post a message to run it.
 *
 * Everything runs on the thread that called pe_init(); PuTTY's core is single-threaded.
 */

#include "putty.h"
#include "putty_engine.h"
#include "engine_internal.h"

#define PE_WM_RUN_CALLBACKS (WM_APP + 100)
#define PE_MAX_DRAIN        1000    /* bound on callbacks run while shutting down */

static const wchar_t pe_window_class[] = L"PuTTYEngineMessageWindow";

static struct {
    bool initialised;
    DWORD thread_id;
    HWND hwnd;
    bool callbacks_posted;      /* a PE_WM_RUN_CALLBACKS message is already in flight */
} pe;

static pe_diag_counters diag;

/* The engine's single-thread rule, checked on every entry point after pe_init(). */
int32_t pe_check_engine_thread(void)
{
    if (!pe.initialised)
        return PE_ERR_STATE;
    if (GetCurrentThreadId() != pe.thread_id)
        return PE_ERR_THREAD;
    return PE_OK;
}

static void post_run_callbacks(void)
{
    if (!pe.callbacks_posted) {
        pe.callbacks_posted = true;
        PostMessageW(pe.hwnd, PE_WM_RUN_CALLBACKS, 0, 0);
    }
}

/* Called by callback.c when its queue goes from empty to non-empty. */
static void notify_callbacks_pending(void *ctx)
{
    (void)ctx;
    post_run_callbacks();
}

/*
 * run_toplevel_callbacks() runs ONE callback per call. Like PuTTY's own message loop we run
 * one per message and post again if more are waiting, so window messages interleave with a
 * long chain of callbacks instead of being starved by it.
 */
static void run_callbacks_message(void)
{
    pe.callbacks_posted = false;
    run_toplevel_callbacks();
    if (toplevel_callback_pending())
        post_run_callbacks();
}

static LRESULT CALLBACK engine_window_proc(HWND hwnd, UINT message, WPARAM wparam,
                                           LPARAM lparam)
{
    switch (message) {
      case WM_NETEVENT:
      case WM_DONE_WITH_SOCKET:
        winselgui_response(message, wparam, lparam);
        return 0;
      case PE_WM_RUN_CALLBACKS:
        run_callbacks_message();
        return 0;
    }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

/* Dispatches every message currently queued for this thread. Returns how many. */
static int32_t dispatch_pending_messages(void)
{
    int32_t count = 0;
    MSG msg;
    while (PeekMessageW(&msg, NULL, 0, 0, PM_REMOVE)) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
        count++;
    }
    return count;
}

/*
 * PuTTY's timer queue (utils/timing.c) is process-global and its only wake-up is the SetTimer on
 * the hidden window that setup_gui_timing() creates, which dies with the thread that created it.
 * A queue that still holds a timer from an earlier engine (every session's timers, up to the
 * cursor blink, outlive the session until run_timers drops them) is never re-announced to the new
 * window: schedule_timer() calls timer_change_notify() only when the new timer becomes the FIRST
 * one, so while a stale timer sits in front, no timer of the new engine ever fires. Visible as a
 * terminal that stopped repainting after its first update (the update cooldown is a timer), in
 * about half the runs of a test that restarts the engine. A host that starts one engine for the
 * life of the process would not see it, but the engine must not depend on that.
 * Running the queue once now deletes expired work and arms the new window for whatever remains.
 */
static void rearm_timers(void)
{
    unsigned long next;
    if (run_timers(GETTICKCOUNT(), &next))
        timer_change_notify(next);
}

PE_API int32_t pe_init(void)
{
    if (pe.initialised)
        return GetCurrentThreadId() == pe.thread_id ? PE_ERR_STATE : PE_ERR_THREAD;

    WNDCLASSEXW wc = { 0 };
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = engine_window_proc;
    wc.hInstance = hinst;
    wc.lpszClassName = pe_window_class;
    if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
        return PE_ERR_FAILED;

    /* A message-only window: it receives posted messages (WSAAsyncSelect uses PostMessage)
     * but is never shown and does not appear in window lists. */
    HWND hwnd = CreateWindowExW(0, pe_window_class, L"PuTTY engine", 0, 0, 0, 0, 0,
                                HWND_MESSAGE, NULL, hinst, NULL);
    if (!hwnd)
        return PE_ERR_FAILED;

    pe.hwnd = hwnd;
    pe.thread_id = GetCurrentThreadId();
    pe.callbacks_posted = false;
    memset(&diag, 0, sizeof(diag));

    /* The same start-up sequence as windows/window.c. */
    sk_init();
    setup_gui_timing();
    rearm_timers();
    /* What window.c does at start-up: without it a new Conf has protocol SSH but port 0. */
    settings_set_default_protocol(PROT_SSH);
    {
        const struct BackendVtable *ssh = backend_vt_from_proto(PROT_SSH);
        settings_set_default_port(ssh ? ssh->default_port : 0);
    }
    winselgui_set_hwnd(hwnd);
    request_callback_notifications(notify_callbacks_pending, NULL);

    pe.initialised = true;
    return PE_OK;
}

PE_API int32_t pe_shutdown(void)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;

    /*
     * Order matters. Finish queued work, close every socket, then drain messages so
     * WM_DONE_WITH_SOCKET empties select-gui.c's "moribund sockets" set: a stale entry
     * would make a later socket that reuses the same handle value ignore its events.
     */
    request_callback_notifications(NULL, NULL);
    for (int i = 0; i < PE_MAX_DRAIN && run_toplevel_callbacks(); i++)
        ;
    pe_configs_close_all();     /* settings dialogs the host forgot */
    pe_sessions_close_all();    /* sessions the host forgot; their handles become invalid */
    sk_cleanup();
    dispatch_pending_messages();
    for (int i = 0; i < PE_MAX_DRAIN && run_toplevel_callbacks(); i++)
        ;

    pe_keys_reset();
    winselgui_clear_hwnd();
    DestroyWindow(pe.hwnd);
    pe_warnings_reset();
    memset(&pe, 0, sizeof(pe));
    return PE_OK;
}

PE_API int32_t pe_pump(uint32_t timeout_ms)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;

    /* Returns at once if input is already queued, otherwise waits up to timeout_ms. */
    MsgWaitForMultipleObjectsEx(0, NULL, timeout_ms, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
    return dispatch_pending_messages();
}

/* ----- Diagnostics ---------------------------------------------------------------- */

static void diag_callback(void *ctx)
{
    (void)ctx;
    diag.callbacks_run++;
}

static void diag_timer(void *ctx, unsigned long now)
{
    (void)ctx;
    (void)now;
    diag.timers_fired++;
}

PE_API int32_t pe_diag_queue_callback(void)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    queue_toplevel_callback(diag_callback, NULL);
    return PE_OK;
}

PE_API int32_t pe_diag_schedule_timer(uint32_t delay_ms)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    /*
     * The context must be non-NULL: it identifies the timer's owner so that
     * expire_timer_context() can cancel it, and timing.c looks it up in a tree that
     * asserts on a NULL key.
     */
    schedule_timer((int)delay_ms, diag_timer, &diag);
    return PE_OK;
}

PE_API int32_t pe_diag_get_counters(pe_diag_counters *out)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    *out = diag;
    return PE_OK;
}
