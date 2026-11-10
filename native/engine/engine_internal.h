/*
 * engine_internal.h - shared between engine source files; not part of the C ABI.
 */

#ifndef PUTTY_ENGINE_INTERNAL_H
#define PUTTY_ENGINE_INTERNAL_H

#include <stdint.h>

#include "putty.h"
#include "putty_engine.h"
#include "render.h"

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

/* ----- The TermWin (termwin.c) ------------------------------------------------------------ */

/*
 * The TermWin the emulator draws through, drawing onto a renderer. With no renderer it answers
 * everything and draws nothing (a headless session). Embedded in the session; `owner` is the
 * pe_session, which the callbacks below take.
 */
typedef struct pe_termwin {
    TermWin tw;
    void *owner;
    Terminal *term;
    pe_renderer *renderer;          /* owned; NULL while headless */
    bool drawing;                   /* between setup_draw_ctx and free_draw_ctx */
    uint32_t colours[OSC4_NCOLOURS];    /* 0x00RRGGBB, indexed as the emulator's palette (OSC 4) */
    int cursor_type;
    bool bold_font;
    bool bold_colours;
    int cursor_x, cursor_y;
    bool raw_mouse_mode;
    int sb_total, sb_start, sb_page;
} pe_termwin;

void pe_termwin_init(pe_termwin *w, void *owner);
void pe_termwin_use_settings(pe_termwin *w, Terminal *term, Conf *conf);
void pe_termwin_set_renderer(pe_termwin *w, pe_renderer *renderer);
void pe_termwin_destroy(pe_termwin *w);

/* What the TermWin needs from, or reports to, its session. `owner` is the pe_session. (session.c) */
void pe_session_unthrottle(void *owner, size_t bufsize);
void pe_session_schedule_repaint(void *owner);
void pe_session_notify_title(void *owner, const char *utf8_title);
void pe_session_notify_bell(void *owner);
void pe_session_notify_scrollbar(void *owner, int total, int start, int page);
void pe_session_notify_clipboard_write(void *owner, const wchar_t *text, int units);
void pe_session_notify_paste_request(void *owner);

#endif /* PUTTY_ENGINE_INTERNAL_H */
