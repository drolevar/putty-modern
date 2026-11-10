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
#include <string.h>

#include "putty.h"
#include "ssh.h"            /* share_can_be_*, old_keyfile_warning */
#include "putty_engine.h"
#include "engine_internal.h"

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
 * Warnings that are not tied to a session. PuTTY calls these from the engine thread; the host
 * registers a handler with pe_set_warning_handler() and gets the text there. Without one they
 * go to the debugger only.
 */
static struct {
    pe_warning_fn handler;
    void *user;
} warning_sink;

void pe_warnings_reset(void)
{
    memset(&warning_sink, 0, sizeof(warning_sink));
}

PE_API int32_t pe_set_warning_handler(pe_warning_fn handler, void *user)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    warning_sink.handler = handler;
    warning_sink.user = user;
    return PE_OK;
}

static void raise_warning(int32_t kind, const char *message)
{
    debug_message("warning", message);
    if (warning_sink.handler)
        warning_sink.handler(warning_sink.user, kind, message);
}

/* A non-fatal problem, for example the random seed file could not be written. */
void nonfatal(const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    char *msg = dupvprintf(fmt, ap);
    va_end(ap);

    raise_warning(PE_WARNING_NONFATAL, msg);
    sfree(msg);
}

/*
 * A loaded SSH-2 private key uses the old file format. The wording is the one PuTTY's console
 * front end prints (windows/console.c), on one line.
 */
void old_keyfile_warning(void)
{
    raise_warning(PE_WARNING_OLD_KEY_FORMAT,
                  "You are loading an SSH-2 private key which has an old version of the file "
                  "format. This means your key file is not fully tamperproof. Future versions "
                  "of PuTTY may stop supporting this private key format, so we recommend you "
                  "convert your key to the new format. Once the key is loaded into PuTTYgen, "
                  "you can perform this conversion simply by saving it again.");
}

PE_API int32_t pe_diag_raise_warning(int32_t kind)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (kind == PE_WARNING_NONFATAL)
        nonfatal("diagnostic warning %d", 42);
    else if (kind == PE_WARNING_OLD_KEY_FORMAT)
        old_keyfile_warning();
    else
        return PE_ERR_STATE;
    return PE_OK;
}

/*
 * Connection sharing policy. The GUI PuTTY (windows/window.c) can be both the first
 * connection to a host (upstream) and a later one that shares it (downstream), so do we.
 */
const bool share_can_be_downstream = true;
const bool share_can_be_upstream = true;
