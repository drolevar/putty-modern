/*
 * core_refs.c - keeps the engine honest about what it links.
 *
 * Takes the address of one entry point from each area of PuTTY's core so the
 * linker pulls those objects out of the static libraries. Whatever those
 * objects need but nothing defines is, by construction, the set of functions a
 * PuTTY front end must supply: a missing one is a link error here, not a crash
 * at run time. Delete a reference and the link would "succeed" while silently
 * dropping that part of PuTTY, so keep one per area.
 *
 * With PUTTY_ENGINE_FULL the config-box description is included too, which
 * needs the dlg_* functions (slice 4); that configuration is expected to fail
 * to link and its unresolved externals are the to-do list.
 */

#include "putty.h"

const void *const pe_core_refs[] = {
    (const void *)backends,          /* be_list.c: SSH, serial, Telnet, Rlogin, raw, SUPDUP */
    (const void *)load_settings,     /* settings.c: saved sessions */
    (const void *)term_init,         /* terminal/terminal.c: the emulator */
    (const void *)ldisc_create,      /* ldisc.c: line discipline */
#ifdef PUTTY_ENGINE_FULL
    (const void *)setup_config_box,  /* config.c: the settings dialog description */
#endif
};
