/*
 * engine_internal.h - shared between engine source files; not part of the C ABI.
 */

#ifndef PUTTY_ENGINE_INTERNAL_H
#define PUTTY_ENGINE_INTERNAL_H

#include <stdint.h>

/* PE_OK if the caller is on the engine thread, PE_ERR_STATE / PE_ERR_THREAD otherwise. */
int32_t pe_check_engine_thread(void);

/* Closes every session that is still open. Called by pe_shutdown before sockets are closed. */
void pe_sessions_close_all(void);

#endif /* PUTTY_ENGINE_INTERNAL_H */
