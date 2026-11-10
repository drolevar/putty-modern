/*
 * engine_internal.h - shared between engine source files; not part of the C ABI.
 */

#ifndef PUTTY_ENGINE_INTERNAL_H
#define PUTTY_ENGINE_INTERNAL_H

#include <stdint.h>

#include "putty.h"
#include "putty_engine.h"

/* PE_OK if the caller is on the engine thread, PE_ERR_STATE / PE_ERR_THREAD otherwise. */
int32_t pe_check_engine_thread(void);

/* Closes every session that is still open. Called by pe_shutdown before sockets are closed. */
void pe_sessions_close_all(void);

/* Drops the warning handler. Called by pe_shutdown. */
void pe_warnings_reset(void);

/* ----- Keyboard input (keys.c) ----------------------------------------------------------- */

/*
 * What PuTTY's key translation needs about one terminal, and the state it keeps between key
 * messages. Lives inside the session; keys.c reads and writes it.
 */
typedef struct pe_keyctx {
    Conf *conf;
    Terminal *term;
    Backend *backend;

    int compose_state;              /* the compose key (Apps, or AltGr if configured) */
    int compose_keycode;
    int compose_char;
    unsigned alt_numberpad_accumulator;   /* Alt held, a character code typed on the keypad */
    wchar_t pending_surrogate;      /* the high half of a pair, waiting for its low half */
} pe_keyctx;

/* PuTTY's TranslateKey; see keys.c for what it returns and how it differs from window.c's. */
int pe_translate_key(pe_keyctx *ctx, UINT message, WPARAM wParam, LPARAM lParam,
                     unsigned char *output);

/* The key context of a live session that has a terminal, or NULL. (session.c) */
pe_keyctx *pe_session_keyctx(pe_session *session);

/* A session is going away: stop treating its terminal as the key target. (keys.c) */
void pe_keys_session_closing(pe_keyctx *ctx);

/* Removes the message hook and the key target. Called by pe_shutdown. (keys.c) */
void pe_keys_reset(void);

#endif /* PUTTY_ENGINE_INTERNAL_H */
