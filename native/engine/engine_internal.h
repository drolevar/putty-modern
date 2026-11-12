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

/* Text in PuTTY's encodings (text.c): `is_utf8` says whether PuTTY's side is UTF-8 or the ANSI code page. */
char *pe_text_to_utf8(const char *text, bool is_utf8);
char *pe_text_from_utf8(const char *utf8, bool want_utf8);

/* Opens a session from a Conf (copied, so the caller keeps its own): what the settings dialog's Open does. */
int32_t pe_session_open_conf(Conf *conf, const pe_session_events *events, void *user, pe_session **out_session);

/*
 * Changing the settings of a running session (session.c; used by configbox.c). _source gives a copy of the
 * session's Conf to edit and what PuTTY's box needs to know to open mid-session; _apply makes the edited Conf the
 * session's and passes it on to the terminal, the line discipline, the log and the backend. A session is named by
 * its pointer and a serial number that is never reused: PE_ERR_STATE if it has gone.
 */
int32_t pe_session_config_source(pe_session *s, Conf **conf_copy_out, int *protocol, int *cfginfo, uint64_t *serial);
int32_t pe_session_apply_conf(pe_session *s, uint64_t serial, Conf *conf);

/* Frees every settings dialog that is still open (configbox.c). Called by pe_shutdown. */
void pe_configs_close_all(void);

/* Closes every session that is still open. Called by pe_shutdown before sockets are closed. */
void pe_sessions_close_all(void);

/* Drops the warning handler. Called by pe_shutdown. */
void pe_warnings_reset(void);
void pe_log_reset(void);            /* frontend.c: drops the host's log handler (pe_shutdown) */

/* The engine's trace (frontend.c). Cheap when the host is not logging: nothing is formatted. Engine thread only. */
bool pe_log_enabled(int level);
void pe_log(int level, const char *fmt, ...);
void pe_fatal_reset(void);          /* frontend.c: drops the host's fatal handler (pe_shutdown) */
void pe_fatal_install(void);        /* frontend.c: the SIGABRT handler and the C runtime's abort behaviour (pe_init) */

/* handlewait.c: the engine's replacement for PuTTY's handle-wait.c, which runs the callbacks of handle-io.c and others. */
#define PE_WM_HANDLE_WAIT (WM_APP + 101)
void pe_handle_wait_fire(uintptr_t id);
void pe_handle_waits_start(HWND window);
void pe_handle_waits_stop(void);

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
    bool cursor_reported;           /* last_cursor_* is what the host was last told */
    int last_cursor_x, last_cursor_y;
    bool bold_font;
    bool bold_colours;
    int beep_ind;                   /* CONF_beep_ind: B_IND_DISABLED, B_IND_FLASH or B_IND_STEADY, told to the host with a bell */
    wchar_t bell_wavefile[MAX_PATH];    /* CONF_bell_wavefile, for BELL_WAVEFILE */
    bool wavefile_failed;           /* the wave file could not be played: the default sound is used instead */
    DWORD last_beep_time;           /* GetTickCount after the last MessageBeep-style bell */
    int cursor_x, cursor_y;
    bool raw_mouse_mode;
    int sb_total, sb_start, sb_page;
    uint32_t *shadow;               /* what is drawn, one code point per cell (0: none, a wide character's second cell), for pe_session_get_screen_text */
    int shadow_rows, shadow_cols;
} pe_termwin;

void pe_termwin_init(pe_termwin *w, void *owner);
void pe_termwin_use_settings(pe_termwin *w, Terminal *term, Conf *conf);
void pe_termwin_set_renderer(pe_termwin *w, pe_renderer *renderer);
void pe_termwin_destroy(pe_termwin *w);
/* The text on the screen as the host sees it drawn, rows separated by newlines, trailing blanks and blank rows trimmed; returns the length in UTF-16 units. */
int32_t pe_termwin_screen_text(const pe_termwin *w, wchar_t *buffer, int32_t capacity, int32_t *rows, int32_t *columns);

/* What the TermWin needs from, or reports to, its session. `owner` is the pe_session. (session.c) */
void pe_session_unthrottle(void *owner, size_t bufsize);
void pe_session_schedule_repaint(void *owner);
void pe_session_notify_title(void *owner, const char *utf8_title);
/* `indicator` is CONF_beep_ind (0 none, 1 flash, 2 steady): whether the host should draw attention to its window. */
void pe_session_notify_bell(void *owner, int indicator);
/* A message for the host that is not fatal (the event log shows it). */
void pe_session_notify_warning(void *owner, const char *text);
void pe_session_notify_scrollbar(void *owner, int total, int start, int page);
void pe_session_notify_clipboard_write(void *owner, const wchar_t *text, int units);
void pe_session_notify_paste_request(void *owner);
void pe_session_notify_cursor(void *owner, int x, int y, int width, int height);

#endif /* PUTTY_ENGINE_INTERNAL_H */
