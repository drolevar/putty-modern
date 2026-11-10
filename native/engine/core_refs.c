/*
 * core_refs.c - only built with PUTTY_ENGINE_FULL.
 *
 * Takes the address of one entry point from each area of PuTTY's core so the
 * linker pulls those objects out of the static libraries. Whatever those
 * objects need but nothing defines is, by construction, the set of functions a
 * PuTTY front end must supply. The unresolved externals reported at link time
 * are the to-do list for the engine; it shrinks as they are implemented.
 */

#include "putty.h"

const void *const pe_core_refs[] = {
    (const void *)backends,          /* be_list.c: SSH, serial, Telnet, Rlogin, raw, SUPDUP */
    (const void *)load_settings,     /* settings.c: saved sessions */
    (const void *)term_init,         /* terminal/terminal.c: the emulator */
    (const void *)ldisc_create,      /* ldisc.c: line discipline */
    (const void *)setup_config_box,  /* config.c: the settings dialog description */
};
