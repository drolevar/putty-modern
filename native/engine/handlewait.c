/*
 * handlewait.c - the engine's replacement for PuTTY's windows/handle-wait.c.
 *
 * PuTTY keeps a list of HANDLEs to wait for, each with a callback; a Windows front end's event loop asks for the
 * list (get_handle_wait_list), waits on it with MsgWaitForMultipleObjects and calls handle_wait_activate for the one
 * that fired. The engine has no loop of its own: the host's message loop (WinUI's) runs on the engine thread. Nothing
 * waited on the list, so everything built on it was dead: handle-io.c (pipes, files, the serial line, the local proxy
 * command and the OpenSSH agent's named pipe all go through its one ready_event), the ConPTY backend and Pageant.
 *
 * Here each wait is a thread-pool wait (RegisterWaitForSingleObject), which also lifts PuTTY's limit of
 * MAXIMUM_WAIT_OBJECTS handles. When a handle becomes signalled the pool posts PE_WM_HANDLE_WAIT to the engine's
 * window and the engine thread runs the callback; afterwards the wait is armed again, so a handle that is still
 * signalled fires again, which is the level-triggered behaviour of PuTTY's own loop. The callbacks therefore run on the
 * engine thread, like everything else of PuTTY's, and only a window message is posted from the pool.
 *
 * All five functions of handle-wait.c are defined here, so the linker never takes PuTTY's version from its library.
 * The list API (get_handle_wait_list and so on) is for loops of PuTTY's own that the engine does not link; it answers
 * with an empty list.
 */

#include "putty.h"
#include "putty_engine.h"
#include "engine_internal.h"

struct HandleWait {
    HANDLE handle;
    handle_wait_callback_fn_t callback;
    void *callback_ctx;
    uintptr_t id;               /* what the posted message carries; never reused, so a stale message finds nothing */
    HANDLE pool_wait;           /* the registration, NULL while not armed */
    bool in_callback;
    bool deleted;               /* delete_handle_wait was called from inside the callback */
    HandleWait *next;
};

static HandleWait *waits;
static uintptr_t next_id = 1;
static HWND target_window;      /* where the pool posts; written on the engine thread before any wait is armed */

static HandleWait *find_wait(uintptr_t id)
{
    for (HandleWait *hw = waits; hw; hw = hw->next) {
        if (hw->id == id)
            return hw;
    }
    return NULL;
}

/* Runs on a thread-pool thread: it posts a message and does nothing else. */
static VOID CALLBACK wait_fired(PVOID context, BOOLEAN timed_out)
{
    (void)timed_out;
    HWND window = target_window;
    if (window)
        PostMessageW(window, PE_WM_HANDLE_WAIT, (WPARAM)(uintptr_t)context, 0);
}

static void arm(HandleWait *hw)
{
    if (hw->pool_wait || hw->deleted || !target_window)
        return;
    if (!RegisterWaitForSingleObject(&hw->pool_wait, hw->handle, wait_fired, (PVOID)hw->id, INFINITE,
                                     WT_EXECUTEONLYONCE | WT_EXECUTEINWAITTHREAD))
        hw->pool_wait = NULL;
}

static void disarm(HandleWait *hw)
{
    if (hw->pool_wait) {
        /* INVALID_HANDLE_VALUE: wait until a callback that is running has finished. It only posts a message. */
        UnregisterWaitEx(hw->pool_wait, INVALID_HANDLE_VALUE);
        hw->pool_wait = NULL;
    }
}

HandleWait *add_handle_wait(HANDLE h, handle_wait_callback_fn_t callback, void *callback_ctx)
{
    HandleWait *hw = snew(HandleWait);
    memset(hw, 0, sizeof(*hw));
    hw->handle = h;
    hw->callback = callback;
    hw->callback_ctx = callback_ctx;
    hw->id = next_id++;
    hw->next = waits;
    waits = hw;
    arm(hw);
    return hw;
}

void delete_handle_wait(HandleWait *hw)
{
    disarm(hw);
    if (hw->in_callback) {
        hw->deleted = true;     /* freed when the callback returns */
        return;
    }

    for (HandleWait **link = &waits; *link; link = &(*link)->next) {
        if (*link == hw) {
            *link = hw->next;
            break;
        }
    }
    sfree(hw);
}

/* The engine window got PE_WM_HANDLE_WAIT. */
void pe_handle_wait_fire(uintptr_t id)
{
    HandleWait *hw = find_wait(id);
    if (!hw || hw->deleted)
        return;                 /* deleted since the message was posted */

    hw->pool_wait = NULL;       /* a WT_EXECUTEONLYONCE wait is over once it has fired */
    hw->in_callback = true;
    hw->callback(hw->callback_ctx);
    hw->in_callback = false;

    if (hw->deleted) {
        delete_handle_wait(hw);
        return;
    }
    arm(hw);                    /* still signalled: it fires again */
}

/* The engine starts: waits may be armed from now on, and any made before (none are) are armed. */
void pe_handle_waits_start(HWND window)
{
    target_window = window;
    for (HandleWait *hw = waits; hw; hw = hw->next)
        arm(hw);
}

/* The engine stops: no more messages, and every wait that is left is dropped. */
void pe_handle_waits_stop(void)
{
    for (HandleWait *hw = waits; hw; hw = hw->next)
        disarm(hw);
    target_window = NULL;
    while (waits) {
        HandleWait *hw = waits;
        waits = hw->next;
        sfree(hw);
    }
}

/* PuTTY's own event loops (not linked here) would wait on this list; the engine's waits are in the pool. */
HandleWaitList *get_handle_wait_list(void)
{
    HandleWaitList *list = snew(HandleWaitList);
    memset(list, 0, sizeof(*list));
    return list;
}

void handle_wait_activate(HandleWaitList *hwl, int index)
{
    (void)hwl;
    (void)index;
}

void handle_wait_list_free(HandleWaitList *hwl)
{
    sfree(hwl);
}
