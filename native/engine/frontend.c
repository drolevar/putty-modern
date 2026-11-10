/*
 * frontend.c - functions PuTTY's core calls but expects the front end to define.
 *
 * Every PuTTY front end (windows/window.c, unix/window.c, the console tools)
 * supplies these. This file starts minimal and grows as more PuTTY libraries
 * are linked; the linker's unresolved-symbol list is the authoritative contract.
 */

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>

#include "putty.h"

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
    OutputDebugStringA("PuTTY engine fatal error: ");
    OutputDebugStringA(msg);
    OutputDebugStringA("\n");
    free(msg);
    abort();
}

/* Terminates the process; PuTTY expects this never to return. */
NORETURN void cleanup_exit(int code)
{
    exit(code);
}
