/*
 * frontend.c - process-level functions and data PuTTY's core expects a front end to define.
 *
 * Every PuTTY front end (windows/window.c, unix/window.c, the console tools) supplies these.
 * The linker's unresolved-symbol list is the authoritative contract; see
 * docs/front-end-contract.md. Per-session obligations (Seat, LogPolicy, TermWin) live
 * elsewhere because they are vtables the linker cannot check.
 */

#include <assert.h>
#include <signal.h>
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

/* ----- The engine's trace ------------------------------------------------------------------ */

static struct {
    pe_log_fn handler;
    void *user;
    int32_t max_level;
} log_sink;

void pe_log_reset(void)
{
    memset(&log_sink, 0, sizeof(log_sink));
}

PE_API int32_t pe_set_log_handler(pe_log_fn handler, void *user, int32_t max_level)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    log_sink.handler = handler;
    log_sink.user = user;
    log_sink.max_level = max_level;
    return PE_OK;
}

bool pe_log_enabled(int level)
{
    return log_sink.handler && level <= log_sink.max_level;
}

void pe_log(int level, const char *fmt, ...)
{
    if (!pe_log_enabled(level))
        return;
    va_list ap;
    va_start(ap, fmt);
    char *msg = dupvprintf(fmt, ap);
    va_end(ap);
    log_sink.handler(log_sink.user, level, msg);
    sfree(msg);
}

static void debug_message(const char *kind, const char *message)
{
    OutputDebugStringA("PuTTY engine ");
    OutputDebugStringA(kind);
    OutputDebugStringA(": ");
    OutputDebugStringA(message);
    OutputDebugStringA("\n");
}

/* A fatal error or a warning: to the debugger as before, and to the host's trace when it is listening. */
static void trace_message(int level, const char *kind, const char *message)
{
    debug_message(kind, message);
    if (log_sink.handler && level <= log_sink.max_level)
        log_sink.handler(log_sink.user, level, message);
}

/* ----- Fatal errors ------------------------------------------------------------------------ */

/*
 * Some errors end the whole process, and with it every session: PuTTY's modalfatalbox (out of memory and the like),
 * an assertion (PuTTY's assertions are live and mean it), anything else that makes the C runtime abort. The host can
 * register a handler (pe_set_fatal_handler) that is called first, on the thread that hit the error, to say what happened,
 * keep what can be kept (the app saves its workspace, so the tabs come back at the next start) and tell the user.
 * It is called at most once, and what it does must not need the engine to be sound.
 */
static struct {
    pe_fatal_fn handler;
    void *user;
    bool reporting;
} fatal_sink;

void pe_fatal_reset(void)
{
    memset(&fatal_sink, 0, sizeof(fatal_sink));
}

PE_API int32_t pe_set_fatal_handler(pe_fatal_fn handler, void *user)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    fatal_sink.handler = handler;
    fatal_sink.user = user;
    return PE_OK;
}

/* Tells the host, once. Returns true if a handler was there to be told. */
static bool report_fatal(const char *text)
{
    if (!fatal_sink.handler || fatal_sink.reporting)
        return false;
    fatal_sink.reporting = true;
    fatal_sink.handler(fatal_sink.user, text);
    return true;
}

/*
 * An assertion or an abort in the C runtime ends in SIGABRT. The handler tells the host and then lets the abort go
 * on, so that a debugger or Windows Error Reporting still sees the crash. The assertion's own text goes to standard
 * error (see pe_fatal_install), which a GUI process has nowhere to show.
 */
static void abort_handler(int signal_number)
{
    (void)signal_number;
    signal(SIGABRT, SIG_DFL);
    report_fatal("The engine hit an internal error (an assertion of PuTTY's code failed, or the process was aborted); "
                 "the details went to the standard error stream. Run the app from a console to see them.");
}

/* Called by pe_init: no message box for an assertion or an abort (the host's handler and Windows's own reporting are the way), and the handler above. */
void pe_fatal_install(void)
{
    _set_error_mode(_OUT_TO_STDERR);
    _set_abort_behavior(_CALL_REPORTFAULT, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
    signal(SIGABRT, abort_handler);
}

/*
 * Called for unrecoverable, process-wide errors (for example out of memory, from utils/out_of_memory.c). It must not
 * return. With a host handler the host is told and the process ends at once with exit code 3 (no crash report: this
 * is PuTTY's own, expected way to die); without one it aborts.
 */
NORETURN void modalfatalbox(const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    char *msg = dupvprintf(fmt, ap);
    va_end(ap);

    fprintf(stderr, "PuTTY engine fatal error: %s\n", msg);
    trace_message(PE_LOG_ERROR, "fatal error", msg);
    signal(SIGABRT, SIG_DFL);       /* the abort below is not news after this */
    if (report_fatal(msg))
        TerminateProcess(GetCurrentProcess(), 3);
    sfree(msg);
    abort();
}

/* Terminates the process; PuTTY expects this never to return. */
NORETURN void cleanup_exit(int code)
{
    char text[96];
    snprintf(text, sizeof(text), "PuTTY's code asked the process to exit (code %d)", code);
    if (report_fatal(text))
        TerminateProcess(GetCurrentProcess(), (UINT)code);
    exit(code);
}

/* A test hook: a failed assertion in the engine's own code, the way PuTTY's assertions fail (NDEBUG is never defined), so the host's handling of an abort can be checked in a process of its own. */
PE_API int32_t pe_diag_assert(void)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    assert(!"a test assertion in the engine");
    return PE_OK;
}

/* A test hook: the path of a real fatal error, so the host's handling can be checked in a process of its own. */
PE_API int32_t pe_diag_fatal(const char *message)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    modalfatalbox("%s", message ? message : "test");
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
    trace_message(PE_LOG_WARN, "warning", message);
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
