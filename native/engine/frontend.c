/*
 * frontend.c - process-level functions and data PuTTY's core expects a front end to define.
 *
 * Every PuTTY front end (windows/window.c, unix/window.c, the console tools) supplies these.
 * The linker's unresolved-symbol list is the authoritative contract; see
 * docs/front-end-contract.md. Per-session obligations (Seat, LogPolicy, TermWin) live
 * elsewhere because they are vtables the linker cannot check.
 */

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>

#include "putty.h"
#include "ssh.h"            /* share_can_be_*, old_keyfile_warning */

/*
 * The module handle of this DLL. PuTTY uses it to register window classes (the hidden timer
 * window in utils/gui-timing.c) and to find resources, so it must be the DLL's own handle,
 * not the host executable's. Set in DllMain before anything else runs.
 */
HINSTANCE hinst;

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        hinst = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

static void debug_message(const char *kind, const char *message)
{
    OutputDebugStringA("PuTTY engine ");
    OutputDebugStringA(kind);
    OutputDebugStringA(": ");
    OutputDebugStringA(message);
    OutputDebugStringA("\n");
}

/*
 * Called for unrecoverable, process-wide errors (for example out of memory,
 * from utils/out_of_memory.c). It must not return. A later version reports the
 * message to the host application before exiting.
 */
NORETURN void modalfatalbox(const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    char *msg = dupvprintf(fmt, ap);
    va_end(ap);

    fprintf(stderr, "PuTTY engine fatal error: %s\n", msg);
    debug_message("fatal error", msg);
    sfree(msg);
    abort();
}

/* Terminates the process; PuTTY expects this never to return. */
NORETURN void cleanup_exit(int code)
{
    exit(code);
}

/*
 * A non-fatal problem that is not tied to a session, for example the random seed file
 * could not be written. For now it goes to the debugger; the event-loop and Seat work
 * (slice 1, tasks 1b and 1c) forwards it to the host.
 */
void nonfatal(const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    char *msg = dupvprintf(fmt, ap);
    va_end(ap);

    debug_message("warning", msg);
    sfree(msg);
}

/* A loaded private key file uses an old format. Same forwarding plan as nonfatal(). */
void old_keyfile_warning(void)
{
    debug_message("warning", "a private key file uses an old format");
}

/*
 * Connection sharing policy. The GUI PuTTY (windows/window.c) can be both the first
 * connection to a host (upstream) and a later one that shares it (downstream), so do we.
 */
const bool share_can_be_downstream = true;
const bool share_can_be_upstream = true;
