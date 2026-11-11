/*
 * putty_engine.h - flat C ABI of the PuTTY engine DLL.
 *
 * This is the only interface the managed side (PuTTY.Interop) sees. Keep it
 * blittable: fixed-width integers, pointers, and function pointers. No PuTTY
 * types appear here, and PuTTY headers must not be included from C++.
 *
 * Threading: PuTTY's core is single-threaded with global state, so the engine
 * lives on ONE thread, the one that calls pe_init(). That thread must run a
 * Win32 message loop (the WinUI UI thread already does; tests call pe_pump()).
 * Every other pe_* call must come from the same thread and returns
 * PE_ERR_THREAD otherwise.
 */

#ifndef PUTTY_ENGINE_H
#define PUTTY_ENGINE_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(PUTTY_ENGINE_BUILD)
#  define PE_API __declspec(dllexport)
#else
#  define PE_API __declspec(dllimport)
#endif

/* Bump on any incompatible change to this header. */
#define PE_ABI_VERSION 1u

/* Result codes: 0 is success, errors are negative. */
#define PE_OK           0
#define PE_ERR_STATE   (-1)   /* wrong state, e.g. not initialised or already initialised */
#define PE_ERR_THREAD  (-2)   /* called from a thread other than the engine thread */
#define PE_ERR_FAILED  (-3)   /* an operating-system call failed */

PE_API uint32_t pe_abi_version(void);

/*
 * Smoke test that exercises PuTTY's Conf store (utils/conf.c) through the
 * linked static library. Returns 1 on success, 0 on failure.
 */
PE_API int32_t pe_probe(void);

/*
 * Starts the engine on the calling thread: creates its hidden message window and wires
 * PuTTY's timers, deferred callbacks and socket events to it. May be called again after
 * pe_shutdown(), including from a different thread.
 */
PE_API int32_t pe_init(void);

/* Closes all sockets, drains pending work and tears the engine down. */
PE_API int32_t pe_shutdown(void);

/*
 * Waits up to timeout_ms for window messages and dispatches whatever is queued. Hosts
 * that already run a message loop (the UI thread) never call this; it exists for threads
 * that have none, such as tests. Returns the number of messages dispatched, or a negative
 * PE_ERR_* code.
 */
PE_API int32_t pe_pump(uint32_t timeout_ms);

/* ----- Sessions ---------------------------------------------------------------------- */

/* Opaque handle to one connection. Valid from pe_session_open until pe_session_close. */
typedef struct pe_session pe_session;

#define PE_PROTO_RAW     0
#define PE_PROTO_TELNET  1
#define PE_PROTO_RLOGIN  2
#define PE_PROTO_SSH     3
/* Only ever reported by pe_session_summary (a saved session can be Serial or SUPDUP; the engine does not open
 * those through pe_session_open_ex, but pe_session_open_saved opens a saved one of any protocol). */
#define PE_PROTO_SERIAL  4
#define PE_PROTO_SUPDUP  5
#define PE_PROTO_OTHER   6

/*
 * What the engine reports about a session. Every callback runs on the engine thread, with
 * the 'user' pointer given to pe_session_open. Any of them may be NULL. Strings are UTF-8
 * and valid only for the duration of the call.
 *
 * A callback may call pe_session_close for its own session; the engine defers the actual
 * teardown until it is safe, and stops delivering events for that session at once.
 */
typedef struct pe_session_events {
    /* Bytes from the remote side. is_stderr is non-zero for the error stream. */
    void (*output)(void *user, const void *data, uint32_t length, int32_t is_stderr);

    /* One line of PuTTY's event log, such as "Connecting to 127.0.0.1 port 22". */
    void (*log)(void *user, const char *text);

    /* A problem. is_fatal is non-zero if the connection cannot continue. */
    void (*error)(void *user, const char *message, int32_t is_fatal);

    /* The connection has ended. Delivered once; the session still has to be closed. */
    void (*closed)(void *user);

    /*
     * The server (or PuTTY itself, for a key passphrase) wants answers: a user name, a password,
     * a passphrase, or the questions of keyboard-interactive authentication. `id` names this
     * question; answer it with pe_session_prompt_respond or pe_session_prompt_cancel, from inside
     * this callback or later (a dialog takes time). `count` may be 0: the text is then only to be
     * shown, and a respond with no answers is the "OK". prompts[i] is the text of question i and
     * echo[i] says whether the typed answer may be shown (non-zero) or must be hidden (0).
     * from_server is non-zero when the questions came from the server rather than from PuTTY. The
     * strings are valid only during the call. Only one question is open at a time; with no
     * handler, or while another is open, a question is refused and the connection fails safe.
     */
    void (*prompt)(void *user, uint64_t id, const char *name, const char *instruction,
                   int32_t from_server, int32_t count, const char *const *prompts, const int32_t *echo);

    /*
     * A question that needs a yes or no: an unknown or changed SSH host key, or a cipher or host key
     * algorithm below the configured warning threshold (`kind`, PE_CONFIRM_*). The text of the
     * dialog is given as `count` pieces, each with a type (PE_DIALOG_*) and a string; for a host key
     * it includes the fingerprints, and says whether the key is new or has changed. host, port and
     * key_type are empty or 0 when they do not apply. Answer with pe_session_confirm_respond. The
     * same rules as for `prompt`: one question at a time, answered inside the call or later, and no
     * handler means no.
     */
    void (*confirm)(void *user, uint64_t id, int32_t kind, const char *host, int32_t port,
                    const char *key_type, int32_t count, const int32_t *types, const char *const *texts);
} pe_session_events;

#define PE_CONFIRM_HOST_KEY          0   /* unknown or changed host key: accept, connect once, cancel */
#define PE_CONFIRM_WEAK_CRYPTO       1   /* an algorithm below the warning threshold: yes or no */
#define PE_CONFIRM_WEAK_CACHED_KEY   2   /* the best cached host key is below the threshold: yes or no */
#define PE_CONFIRM_LOG_FILE_EXISTS   3   /* the session log file exists (`host` is its path): overwrite, append, or cancel = no logging */

/* Types of the pieces of a confirmation dialog's text; the same values as PuTTY's SDT_*. */
#define PE_DIALOG_PARA              0   /* a paragraph */
#define PE_DIALOG_DISPLAY           1   /* text to show verbatim, such as a fingerprint */
#define PE_DIALOG_SCARY_HEADING     2   /* a warning headline (a changed host key) */
#define PE_DIALOG_TITLE             3   /* the title of the dialog */
#define PE_DIALOG_PROMPT            4   /* the question for a text prompt; a dialog has buttons instead */
#define PE_DIALOG_BATCH_ABORT       5   /* what a non-interactive run says; not needed in a dialog */
#define PE_DIALOG_MORE_INFO_KEY     6   /* a label in the "more info" view ... */
#define PE_DIALOG_MORE_INFO_SHORT   7   /* ... its one-line value ... */
#define PE_DIALOG_MORE_INFO_BLOB    8   /* ... or its multi-line value */

/* The answers to a confirmation. */
#define PE_ANSWER_CANCEL        0   /* refuse: the connection is abandoned */
#define PE_ANSWER_ACCEPT        1   /* yes; for a host key also store it in PuTTY's host key cache */
#define PE_ANSWER_CONNECT_ONCE  2   /* a host key: yes, this time only. For the other kinds, the same as ACCEPT. */
#define PE_ANSWER_OVERWRITE     3   /* a log file question only: replace the file */
#define PE_ANSWER_APPEND        4   /* a log file question only: add to the file. (CANCEL there means: no logging, the session goes on.) */

/*
 * Optional settings for a session; everything is optional. size is sizeof(pe_session_options),
 * so the structure can grow. Strings are UTF-8.
 */
typedef struct pe_session_options {
    uint32_t size;
    const char *username;      /* the SSH user name; NULL or empty lets the server ask (a prompt) */
    const char *key_file;      /* a PuTTY private key (.ppk) to authenticate with, or NULL */
    const char *command;       /* a remote command to run instead of a shell, or NULL */
    int32_t try_agent;         /* Pageant: -1 PuTTY's default (on), 0 off, 1 on */
    uint32_t flags;            /* PE_OPT_* */
} pe_session_options;

/*
 * Start from the user's own "Default Settings" in PuTTY's storage, as PuTTY does for a new session,
 * instead of the built-in defaults. Off by default so that nothing depends on the machine.
 */
#define PE_OPT_SAVED_DEFAULTS 1u

/*
 * Opens a connection. The call returns at once; the connection completes asynchronously
 * and everything that happens is reported through 'events'. If it fails immediately the
 * 'error' callback has already been called and PE_ERR_FAILED is returned. The events
 * structure is copied, so it may live on the caller's stack.
 */
PE_API int32_t pe_session_open(const char *host, int32_t port, int32_t protocol,
                               const pe_session_events *events, void *user,
                               pe_session **out_session);

/* As pe_session_open, with settings. `options` may be NULL. */
PE_API int32_t pe_session_open_ex(const char *host, int32_t port, int32_t protocol,
                                  const pe_session_options *options,
                                  const pe_session_events *events, void *user,
                                  pe_session **out_session);

/*
 * Opens a session saved in PuTTY's own storage (the registry), with all of its settings, as PuTTY
 * would: its host, port and protocol are the saved ones. `overrides` (may be NULL) replaces the user
 * name, key file, command or Pageant setting on top. PE_ERR_FAILED if there is no such session. The
 * storage is only read.
 */
PE_API int32_t pe_session_open_saved(const char *name, const pe_session_options *overrides,
                                     const pe_session_events *events, void *user,
                                     pe_session **out_session);

/*
 * Opens a new session with a copy of the settings of a session that is open (or has ended but is not closed yet):
 * the same host, port, protocol, user name and every setting, including what was changed in Change Settings. For
 * Reconnect and Duplicate, which cannot start from a saved name because the session may never have been saved.
 * PE_ERR_STATE if `like` is not a live session.
 */
PE_API int32_t pe_session_open_like(pe_session *like, const pe_session_events *events, void *user, pe_session **out_session);

/* Called once per saved session by pe_sessions_enumerate; `name` is UTF-8, valid during the call. */
typedef void (*pe_name_fn)(void *user, const char *name);

/*
 * Lists the sessions saved in PuTTY's storage, in storage order, including "Default Settings" if the
 * user saved one. Returns how many there were (0 for none), or a negative error code.
 */
PE_API int32_t pe_sessions_enumerate(pe_name_fn callback, void *user);

/*
 * Where an open session connects, as its settings now say: for opening the same place again (a workspace that comes back
 * after a restart) when the session was not opened from a saved name. Strings are UTF-8, always terminated; a serial
 * session has its line in `host`. `size` is set by the caller to sizeof(pe_session_endpoint). PE_ERR_STATE if the session
 * is not live.
 */
typedef struct pe_session_endpoint {
    int32_t size;
    int32_t protocol;               /* PE_PROTO_* */
    int32_t port;
    char host[256];
    char user_name[128];
    char key_file[512];
} pe_session_endpoint;

PE_API int32_t pe_session_get_endpoint(pe_session *session, pe_session_endpoint *out);

/*
 * What a saved session connects to, for the list: the host name (the serial line for a Serial session) as
 * UTF-8 into `host` (at most host_size-1 bytes, always terminated), the port, and the protocol as a PE_PROTO_*
 * value. The storage is only read. PE_ERR_FAILED if there is no such session.
 */
PE_API int32_t pe_session_summary(const char *name, char *host, int32_t host_size, int32_t *protocol, int32_t *port);

/*
 * Answers the question `id` that a `prompt` event raised: one UTF-8 answer per question (count
 * must match), or none for a text that only needed an OK. PE_ERR_STATE if that question is not
 * open (already answered, or the session ended), PE_ERR_FAILED for a wrong count.
 */
PE_API int32_t pe_session_prompt_respond(pe_session *session, uint64_t id, const char *const *answers,
                                         int32_t count);

/* Refuses the question `id`: the connection is abandoned, as when a user presses Cancel. */
PE_API int32_t pe_session_prompt_cancel(pe_session *session, uint64_t id);

/* Answers the question `id` that a `confirm` event raised with one of PE_ANSWER_*. */
PE_API int32_t pe_session_confirm_respond(pe_session *session, uint64_t id, int32_t answer);

/* Sends bytes to the remote side. PE_ERR_STATE if the session is closed or invalid. */
PE_API int32_t pe_session_send(pe_session *session, const void *data, uint32_t length);

/* Returns 1 while the connection is up, 0 once it has ended, or a negative error code. */
PE_API int32_t pe_session_is_connected(pe_session *session);

/* Closes the connection and frees the session. The handle is invalid afterwards. */
PE_API int32_t pe_session_close(pe_session *session);

/*
 * Ends the connection but keeps the session: its terminal with the screen and the scrollback stays, the host hears
 * `closed` (once), and nothing more can be sent. For a Disconnect command that leaves the tab as it is; Reconnect is
 * pe_session_open_like on this session, which must still be open then. Does nothing, and succeeds, when the
 * connection is over already. From inside one of the session's own events it takes effect a moment later.
 */
PE_API int32_t pe_session_disconnect(pe_session *session);

/*
 * Gives the session a terminal emulator with the given size in character cells. From then on
 * the remote side's output goes to the emulator instead of the host's `output` callback, and
 * keyboard input (see pe_set_key_target) goes through PuTTY's key translation and line
 * discipline to the connection. There is no renderer yet: the emulator has a null TermWin.
 * PE_ERR_STATE if the session is closed or already has a terminal.
 */
PE_API int32_t pe_session_enable_terminal(pe_session *session, int32_t columns, int32_t rows);

/*
 * Reports from the terminal emulator, called on the engine thread. Set with
 * pe_session_set_terminal_events; any member may be NULL. `title` text is UTF-8 and valid only
 * during the call. `scrollbar` gives the scrollback (total lines, first visible line, lines
 * on screen).
 */
typedef struct pe_terminal_events {
    void (*title)(void *user, const char *utf8_title);
    /* `indicator` is the session's "bell indicator" setting: 0 none, 1 flash the window, 2 steady (a few flashes). */
    void (*bell)(void *user, int32_t indicator);
    void (*scrollbar)(void *user, int32_t total, int32_t start, int32_t page);

    /*
     * Selected text for the system clipboard (UTF-16, not NUL-terminated, valid during the call).
     * The host owns the clipboard: this is where it is told to set it.
     */
    void (*clipboard_write)(void *user, const wchar_t *text, int32_t units);

    /*
     * The terminal wants to paste from the system clipboard (a paste key or button). The host reads
     * the clipboard, which may take a while, and answers with pe_session_paste.
     */
    void (*clipboard_request_paste)(void *user);

    /*
     * The terminal's cursor is drawn in another cell: its top-left corner in pixels of the view, and the size
     * of its cell. For a host that places something at the cursor (the box that receives IME composition, so
     * that the IME's candidate window opens there). Reported when the cell changes, not at every blink.
     */
    void (*cursor)(void *user, int32_t x, int32_t y, int32_t width, int32_t height);
} pe_terminal_events;

PE_API int32_t pe_session_set_terminal_events(pe_session *session, const pe_terminal_events *events);

/*
 * Gives the session's terminal a drawing surface. The terminal is created if it is not there yet,
 * and its size becomes as many whole character cells as fit the surface in the given font; the
 * server is told. Draws to an offscreen bitmap (tests, headless rendering) or into a XAML
 * SwapChainPanel (`panel` is its IUnknown). PE_ERR_FAILED if the font is not installed or the
 * surface cannot be created; PE_ERR_STATE if the session already has a view.
 */
PE_API int32_t pe_session_attach_offscreen_view(pe_session *session, int32_t width_px, int32_t height_px,
                                                const wchar_t *font_family, float size_px);
PE_API int32_t pe_session_attach_panel_view(pe_session *session, void *panel, int32_t width_px,
                                            int32_t height_px, const wchar_t *font_family, float size_px);

/* The surface changed size (or the DPI scale did: pass the new physical size). */
PE_API int32_t pe_session_resize_view(pe_session *session, int32_t width_px, int32_t height_px);

/* A different font or size; the terminal is refitted to the surface. */
PE_API int32_t pe_session_set_font(pe_session *session, const wchar_t *font_family, float size_px);

/*
 * The font the session's settings (Window > Appearance) ask for: the name as UTF-8 (the returned length; copied up to
 * `capacity` with a NUL when `name` is not NULL), the height (positive: points, negative: pixels) and whether it is
 * bold. The renderer takes its font from the host (pe_session_set_font); this is what the host should choose.
 */
PE_API int32_t pe_session_get_font(pe_session *session, char *name, int32_t capacity, int32_t *height, int32_t *bold);

typedef struct pe_terminal_geometry {
    int32_t columns;
    int32_t rows;
    int32_t cell_width;         /* pixels; 0 without a view */
    int32_t cell_height;
} pe_terminal_geometry;

PE_API int32_t pe_session_terminal_geometry(pe_session *session, pe_terminal_geometry *out);

/* ----- Mouse, scrolling and pasting ---------------------------------------------------- */

#define PE_MOUSE_DOWN 0
#define PE_MOUSE_UP   1
#define PE_MOUSE_MOVE 2

#define PE_BUTTON_LEFT   0
#define PE_BUTTON_MIDDLE 1
#define PE_BUTTON_RIGHT  2

#define PE_MOD_SHIFT 1
#define PE_MOD_CTRL  2
#define PE_MOD_ALT   4

/*
 * A pointer event over the terminal view, in view pixels. The engine turns it into what PuTTY's
 * window does: selection, word and line selection on double and triple click (time_ms is the
 * event's time in milliseconds on any steady clock), pasting with the configured button, and
 * mouse reporting to the server when the program asked for it. Move events with a button down
 * are drags. The host should capture the pointer while a button is down.
 */
PE_API int32_t pe_session_mouse(pe_session *session, int32_t kind, int32_t button, int32_t x_px,
                                int32_t y_px, int32_t modifiers, uint32_t time_ms);

/*
 * A wheel step: `delta` in units of 120 per notch, positive up (or right if `horizontal`). The
 * engine scrolls the scrollback or, if the program has asked for mouse reports, reports it.
 */
PE_API int32_t pe_session_wheel(pe_session *session, int32_t delta, int32_t horizontal, int32_t x_px,
                                int32_t y_px, int32_t modifiers);

#define PE_SCROLL_LINES    0   /* relative to now: amount lines, negative is up */
#define PE_SCROLL_ABSOLUTE 1   /* to the line `amount`, counted from the top of the scrollback */
#define PE_SCROLL_TOP      2
#define PE_SCROLL_BOTTOM   3

/* From the host's scrollbar. The scrollbar's state comes back through pe_terminal_events. */
PE_API int32_t pe_session_scroll(pe_session *session, int32_t mode, int32_t amount);

/*
 * Text the user typed that did not come as key messages: what an IME committed, the emoji panel, voice typing
 * (UTF-16, not NUL-terminated). It goes where typed characters go (the line discipline, the connection), as
 * window.c does with WM_CHAR text; a surrogate pair must arrive whole.
 */
PE_API int32_t pe_session_type_text(pe_session *session, const wchar_t *text, int32_t units);

/* Text to paste into the terminal, from the clipboard the host read (answers clipboard_request_paste). */
PE_API int32_t pe_session_paste(pe_session *session, const wchar_t *text, int32_t units);

/* Offscreen views only: one pixel of the last finished frame, 0x00RRGGBB. For tests. */
PE_API int32_t pe_diag_session_read_pixel(pe_session *session, int32_t x, int32_t y, uint32_t *rgb);

/*
 * Local echo and local line editing, the two Terminal settings that decide whether the line
 * discipline buffers typing until Enter. Each is PE_LOCAL_AUTO (the protocol decides: on for
 * Raw, off for SSH and, once negotiated, Telnet), PE_LOCAL_ON or PE_LOCAL_OFF. The session needs
 * a terminal.
 */
#define PE_LOCAL_AUTO 0
#define PE_LOCAL_ON   1
#define PE_LOCAL_OFF  2
PE_API int32_t pe_session_set_local_echo_edit(pe_session *session, int32_t echo, int32_t edit);

/*
 * Names the session whose terminal has keyboard focus, or NULL if none does. While one does,
 * a message hook on the engine thread takes keyboard messages and turns them into terminal
 * input exactly as PuTTY's own window would; with none it does nothing. The session must have
 * a terminal. The target is cleared when the session closes and by pe_shutdown.
 */
PE_API int32_t pe_set_key_target(pe_session *session);

/* ----- Warnings that belong to no session ------------------------------------------ */

/* What PuTTY's core reports through its process-level callbacks. */
#define PE_WARNING_NONFATAL       0   /* a problem that does not stop anything, e.g. the random seed
                                         file could not be written */
#define PE_WARNING_OLD_KEY_FORMAT 1   /* a loaded SSH-2 private key uses the old, not fully
                                         tamperproof file format */

/*
 * Called on the engine thread with UTF-8 text that is valid only during the call. Without a
 * handler warnings go to the debugger only. The handler is dropped by pe_shutdown().
 */
typedef void (*pe_warning_fn)(void *user, int32_t kind, const char *message);

PE_API int32_t pe_set_warning_handler(pe_warning_fn handler, void *user);

/* ----- Diagnostics, used by tests to check the event loop ------------------------- */

typedef struct pe_diag_counters {
    uint32_t callbacks_run;     /* diagnostic deferred callbacks that have run */
    uint32_t timers_fired;      /* diagnostic timers that have fired */
} pe_diag_counters;

/* Queues a deferred callback through PuTTY's top-level callback queue. */
PE_API int32_t pe_diag_queue_callback(void);

/* Schedules a PuTTY timer that fires after delay_ms. */
PE_API int32_t pe_diag_schedule_timer(uint32_t delay_ms);

/* Reads the counters. They are reset to zero by pe_init(). */
PE_API int32_t pe_diag_get_counters(pe_diag_counters *out);

/* Makes PuTTY's core raise a warning of the given PE_WARNING_* kind, as it would itself. */
PE_API int32_t pe_diag_raise_warning(int32_t kind);

/*
 * Draws `text` (1 to 64 characters, one cell each, spaces left empty) with the terminal renderer
 * onto an offscreen bitmap and reports what ended up where. Needs no window and no engine.
 */
typedef struct pe_diag_render_result {
    int32_t cell_width;         /* pixels */
    int32_t cell_height;
    int32_t cells;              /* how many characters were drawn */
    int32_t ink[64];            /* per cell: pixels that differ from the background */
    uint32_t corner_rgb;        /* 0x00RRGGBB of pixel (0, 0) */
} pe_diag_render_result;

PE_API int32_t pe_diag_render_text(const wchar_t *font_family, float size_px, const wchar_t *text,
                                   uint32_t fg_rgb, uint32_t bg_rgb, int32_t bold,
                                   pe_diag_render_result *out);

/*
 * Generates an Ed25519 key, saves it as a PuTTY private key file at `path` (encrypted with
 * `passphrase` if it is not NULL or empty) and writes the public key as an "ssh-ed25519 AAAA..."
 * line (NUL-terminated) into `public_line`. For tests of key authentication.
 */
PE_API int32_t pe_diag_generate_ed25519_key(const char *path_utf8, const char *passphrase_utf8,
                                            char *public_line, int32_t capacity);

/* ----- The settings dialog ----------------------------------------------------------------
 *
 * PuTTY describes its whole configuration box as a tree of groups of controls (config.c) and
 * drives it through small handler functions: the front end shows the controls, reports what the user
 * does, and PuTTY's handlers change the settings and the other controls in reply. The engine runs that
 * machinery and keeps the state of every control; the host draws them.
 *
 *   1. pe_config_open, then pe_config_describe: the groups and controls arrive through `group` and
 *      `control`. Every control has a stable integer id.
 *   2. pe_config_refresh: PuTTY sets every control from the settings; each one that changes is reported
 *      through `changed`, and the host reads its state with the getters.
 *   3. The user acts: pe_config_user_* tell PuTTY, whose handlers answer with more `changed` events.
 *   4. `end` says the dialog is over: pe_config_start_session opens a session with the edited settings
 *      (Open), or the host just closes the dialog (Cancel). A saved session is written by PuTTY itself,
 *      from the Save button of the Session page.
 */

typedef struct pe_config pe_config;

#define PE_CTRL_TEXT        0   /* a line of text */
#define PE_CTRL_EDITBOX     1   /* label and edit box; with PE_CTRLF_HAS_LIST a combo box */
#define PE_CTRL_RADIO       2   /* label and a group of radio buttons */
#define PE_CTRL_CHECKBOX    3
#define PE_CTRL_BUTTON      4
#define PE_CTRL_LISTBOX     5   /* a list box, or a drop-down list when list_height is 0 */
#define PE_CTRL_COLUMNS     6   /* the layout of the controls that follow: ncolumns, percentages */
#define PE_CTRL_FILESELECT  7   /* label, file name and a Browse button */
#define PE_CTRL_FONTSELECT  8   /* label, font description and a Change button */

#define PE_CTRLF_PASSWORD     0x001   /* edit box: hide what is typed */
#define PE_CTRLF_HAS_LIST     0x002   /* edit box: a combo box, with the list box functions */
#define PE_CTRLF_DRAGLIST     0x004   /* list box: the user can reorder it */
#define PE_CTRLF_HSCROLL      0x008   /* list box: a horizontal scroll bar if needed */
#define PE_CTRLF_DEFAULT      0x010   /* button: the default (Enter) */
#define PE_CTRLF_CANCEL       0x020   /* button: Cancel (Escape) */
#define PE_CTRLF_JUST_BUTTON  0x040   /* file selector: only a button, no file name shown */
#define PE_CTRLF_FOR_WRITING  0x080   /* file selector: choosing a file to write */
#define PE_CTRLF_WRAP         0x100   /* text: wraps over several lines */

#define PE_FILTER_ALL_FILES   0
#define PE_FILTER_KEY_FILES   1       /* .ppk */
#define PE_FILTER_DYNLIB      2
#define PE_FILTER_SOUND       3

typedef struct pe_ctrl_info {
    int32_t id;
    int32_t type;                 /* PE_CTRL_* */
    int32_t flags;                /* PE_CTRLF_* */
    int32_t group;                /* index of the group (a box on a page) the control is in */
    int32_t column_start;         /* which of the group's columns it starts in ... */
    int32_t column_span;          /* ... and how many it spans */
    int32_t percent_width;        /* edit box, list box: share of the width, 100 = label on its own line */
    int32_t list_height;          /* list box: lines; 0 means a drop-down list */
    int32_t multisel;             /* list box: 0 one selection, 1 individual items, 2 ranges */
    int32_t ncolumns;             /* radio: buttons per row; list box: tab-separated columns; PE_CTRL_COLUMNS: columns */
    const int32_t *percentages;   /* ncolumns widths in percent, or NULL */
    int32_t nbuttons;             /* radio */
    const char *const *buttons;   /* radio: the labels */
    const char *shortcuts;        /* radio: one letter per button, ' ' for none; others: NULL */
    int32_t shortcut;             /* the keyboard shortcut letter of the control, or 0 */
    const char *label;            /* UTF-8, may be NULL (a check box's label is its text) */
    const char *title;            /* file selector: title of the file dialog */
    int32_t filter;               /* file selector: PE_FILTER_* */
    int32_t align_next_to;        /* id of the control this one lines up with, or -1 */
} pe_ctrl_info;

/*
 * What the engine reports about a dialog. Every callback runs on the engine thread, inside the call
 * that caused it, and may be NULL. Strings are UTF-8 and valid only during the call. A callback may
 * close the dialog (pe_config_close defers the teardown until PuTTY has unwound).
 */
typedef struct pe_config_events {
    /* From pe_config_describe: a group of controls. path is the page ("Connection/SSH/Auth"), name the
     * group's short name and title its heading; both are empty for the heading of the page itself. */
    void (*group)(void *user, int32_t index, const char *path, const char *name, const char *title);
    void (*control)(void *user, const pe_ctrl_info *info);

    /* The value or the list of control `id` changed: read it again with the getters. */
    void (*changed)(void *user, int32_t id);
    void (*label_changed)(void *user, int32_t id, const char *text);
    /* A burst of changes is between begin = 1 and begin = 0: the host may postpone redrawing. */
    void (*update)(void *user, int32_t begin);
    /* Something to tell the user: is_error is non-zero for an error, zero for a warning. */
    void (*message)(void *user, const char *text, int32_t is_error);
    void (*beep)(void *user);
    /* The dialog wants the focus on control `id`. */
    void (*focus)(void *user, int32_t id);
    /* A colour chooser is wanted for control `id`, starting at r, g, b: answer with pe_config_colour_result. */
    void (*colour_request)(void *user, int32_t id, int32_t r, int32_t g, int32_t b);
    /* The dialog is over; value 1 means "go" (Open, or Apply), 0 means cancelled. */
    void (*end)(void *user, int32_t value);
} pe_config_events;

/* Creates a dialog for a new session, on the built-in defaults, or with PE_OPT_SAVED_DEFAULTS in `flags`
 * on the user's saved "Default Settings". */
PE_API int32_t pe_config_open(uint32_t flags, const pe_config_events *events, void *user, pe_config **out);

/*
 * Creates PuTTY's Change Settings dialog on a running session: the same box on a copy of that session's settings,
 * with what PuTTY leaves out mid-session left out and the default button saying Apply. The user's Apply arrives as
 * `end` with value 1; the host then calls pe_config_apply. PE_ERR_STATE if the session is gone or closing.
 * pe_config_start_session does not work on such a dialog.
 */
PE_API int32_t pe_config_open_for_session(pe_session *session, const pe_config_events *events, void *user, pe_config **out);

/*
 * Makes the edited settings of a dialog from pe_config_open_for_session the session's own, and passes them to the
 * terminal, line discipline, log and backend, as PuTTY's Windows front end does after its Change Settings box.
 * PE_ERR_STATE if the dialog is not of that kind or its session has gone.
 */
PE_API int32_t pe_config_apply(pe_config *config);

/* Reports every group and control through the events, in order. */
PE_API int32_t pe_config_describe(pe_config *config);

/* Has PuTTY set all the controls from the settings (what happens on first showing and after Load). */
PE_API int32_t pe_config_refresh(pe_config *config);

/* The page the host is showing, "Session" or "Connection/SSH"; "" or NULL when it shows none or all. PuTTY
 * asks whether a control is visible in one corner case (the Open button). */
PE_API int32_t pe_config_set_page(pe_config *config, const char *path);

/* Which control has the keyboard focus now (PuTTY asks which had it last). */
PE_API int32_t pe_config_user_focus(pe_config *config, int32_t id);

/* ----- What the user did ----- */
PE_API int32_t pe_config_user_set_text(pe_config *config, int32_t id, const char *utf8);      /* edit box */
PE_API int32_t pe_config_user_set_checked(pe_config *config, int32_t id, int32_t checked);
PE_API int32_t pe_config_user_set_radio(pe_config *config, int32_t id, int32_t button);
/* A list entry was selected (selected = 1) or unselected, by position; for a single-selection list that
 * replaces the selection. */
PE_API int32_t pe_config_user_select(pe_config *config, int32_t id, int32_t index, int32_t selected);
PE_API int32_t pe_config_user_action(pe_config *config, int32_t id);                          /* button; list double click */
/* The user dragged the entry at `from` of a re-orderable list (PE_CTRLF_DRAGLIST) to position `to`. */
PE_API int32_t pe_config_user_move(pe_config *config, int32_t id, int32_t from, int32_t to);
PE_API int32_t pe_config_user_set_file(pe_config *config, int32_t id, const char *utf8_path);
PE_API int32_t pe_config_user_set_font(pe_config *config, int32_t id, const char *name, int32_t height,
                                       int32_t bold, int32_t charset);
PE_API int32_t pe_config_colour_result(pe_config *config, int32_t id, int32_t ok, int32_t r, int32_t g, int32_t b);

/* ----- Reading the state of a control. The text getters write at most capacity bytes including the NUL
 * and return the length needed without it; pass NULL and 0 to ask for the size. ----- */
PE_API int32_t pe_config_get_text(pe_config *config, int32_t id, char *buffer, int32_t capacity);   /* edit box, text, current label */
PE_API int32_t pe_config_get_checked(pe_config *config, int32_t id);
PE_API int32_t pe_config_get_radio(pe_config *config, int32_t id);
PE_API int32_t pe_config_list_count(pe_config *config, int32_t id);
PE_API int32_t pe_config_list_item(pe_config *config, int32_t id, int32_t index, char *buffer, int32_t capacity,
                                   int32_t *item_id, int32_t *selected);
PE_API int32_t pe_config_get_file(pe_config *config, int32_t id, char *buffer, int32_t capacity);
PE_API int32_t pe_config_get_font(pe_config *config, int32_t id, char *name, int32_t capacity, int32_t *height,
                                  int32_t *bold, int32_t *charset);

/* Called once per installed font family by pe_fonts_enumerate; `name` is UTF-8, valid during the call. */
typedef void (*pe_font_fn)(void *user, const char *name);

/* Lists the installed font families (no duplicates, vertical "@" variants left out), unsorted, for a font
 * chooser. Returns how many were reported, or a negative error code. */
PE_API int32_t pe_fonts_enumerate(pe_font_fn callback, void *user);

/* Opens a session with the settings as edited (what Open does). The `events` and `user` are those of
 * pe_session_open. PE_ERR_FAILED if the settings name no host. */
PE_API int32_t pe_config_start_session(pe_config *config, const pe_session_events *events, void *user,
                                       pe_session **out_session);

/* Frees the dialog. Safe from inside one of its callbacks. */
PE_API int32_t pe_config_close(pe_config *config);
#ifdef __cplusplus
}
#endif

#endif /* PUTTY_ENGINE_H */
