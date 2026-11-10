# Front-end contract: what the engine must supply to link PuTTY's SSH client

Result of Beads issue `putty-modern-q59`, measured against PuTTY `29dd4b17` (`external/putty`).

## How this list was produced, and how to reproduce it

`build\native.cmd x64 full` builds the engine with `PUTTY_ENGINE_FULL=ON`. That links PuTTY's
SSH client, backends, settings, network and event-loop libraries, plus the portable terminal
files (`terminal/terminal.c`, `bidi.c`, `lineedit.c`, `ldisc.c`, `config.c`, `dialog.c`)
compiled directly, and `native/engine/core_refs.c` takes the address of one entry point from
each area so the linker pulls it all in. **The link is expected to fail**, and the unresolved
externals it prints are the functions a PuTTY front end must define. The default build
(`build\native.cmd`) is unaffected and stays green. The list shrinks as each item below is
implemented, so re-running the command is the progress check; it should end at zero.

Two choices shape the result and are worth knowing:

- **PuTTY's `guiterminal` library is not linked.** It bundles PuTTY's Win32 dialog code
  (`windows/dialog.c`, `controls.c`, `jump-list.c`, `printing.c`, ...) with the portable
  terminal code, and the linker silently satisfies the config-box functions from it. That
  hides exactly what we have to write, so the portable files are compiled directly instead.
- **`guimisc` (`windows/select-gui.c`) is linked before `eventloop`.** It provides the socket
  event handling for a message-loop GUI (`do_select` with `WSAAsyncSelect`, `done_with_socket`).
  Without it, `eventloop` supplies the console version, which needs `winselcli_event`.

## Link-time obligations: 45 symbols

`modalfatalbox` and `cleanup_exit` are already defined in `native/engine/frontend.c`.

### 1. Config-box rendering: 30 `dlg_*` functions (implement for real)

Declared in `dialog.h`, called by `config.c`. Reference implementation: `windows/controls.c`
(Win32). Ours will be backed by the XAML settings UI. Parameters are the `dlgcontrol` and
`dlgparam` handles PuTTY passes in, so these are the bridge between the C control tree and
the managed UI.

| Area | Functions |
|---|---|
| Checkbox | `dlg_checkbox_get`, `dlg_checkbox_set` |
| Edit box | `dlg_editbox_get`, `dlg_editbox_set`, `dlg_editbox_get_utf8`, `dlg_editbox_set_utf8` |
| Radio buttons | `dlg_radiobutton_get`, `dlg_radiobutton_set` |
| List box | `dlg_listbox_add`, `dlg_listbox_addwithid`, `dlg_listbox_clear`, `dlg_listbox_getid`, `dlg_listbox_index`, `dlg_listbox_issel`, `dlg_listbox_select` |
| File and font | `dlg_filesel_get`, `dlg_filesel_set`, `dlg_fontsel_get`, `dlg_fontsel_set` |
| Colour picker | `dlg_coloursel_start`, `dlg_coloursel_results` |
| Dialog state | `dlg_beep`, `dlg_end`, `dlg_error_msg`, `dlg_is_visible`, `dlg_label_change`, `dlg_last_focused`, `dlg_refresh`, `dlg_update_start`, `dlg_update_done` |

A few more `dlg_*` functions are declared in `dialog.h` but not referenced by the code linked
so far (for example `dlg_listbox_del`, `dlg_set_focus`, `dlg_text_set`); the settings UI may
need them.

### 2. Printing: 6 functions (stub for now)

`printer_start_enum`, `printer_get_name`, `printer_finish_enum`, `printer_start_job`,
`printer_job_data`, `printer_finish_job` (`putty.h`). Terminal "remote-controlled printing" and
the printer drop-down in the settings use them. Compile PuTTY's own `stubs/no-printing.c`
until printing is wanted; the real version is `windows/printing.c`.

### 3. Taskbar jump list: 3 functions (stub for now)

`add_session_to_jumplist`, `remove_session_from_jumplist`, `clear_jumplist`, called by
PuTTY's Windows `storage.c` when sessions are saved or deleted. The sketch shows no jump
list. Compile `windows/no-jump-list.c` for now; the real version is `windows/jump-list.c`.

### 4. Host-CA configuration box: 1 function (stub for now)

`show_ca_config_box` (`putty.h`), called from the settings. Use `stubs/no-ca-config.c` until
there is a UI for it; the real version is in `windows/dialog.c`.

### 5. Process-level definitions: 5 symbols

| Symbol | What it is | Reference |
|---|---|---|
| `hinst` | `HINSTANCE` global (`GetModuleHandle(NULL)`); needed by `utils/gui-timing.c` (the hidden timer window) and `message_box.c` | `windows/platform.h:239` |
| `nonfatal` | `void nonfatal(const char *fmt, ...)`: report a non-fatal error to the user. Called, for example, when the random seed cannot be written. Surface it through the host | `windows/window.c:5509` |
| `old_keyfile_warning` | `void old_keyfile_warning(void)`: warn that a loaded key file uses an old format | `ssh.h:2059` |
| `share_can_be_downstream`, `share_can_be_upstream` | `const bool` policy flags for connection sharing. The GUI PuTTY sets both to `true` | `windows/window.c:209` |

## Run-time obligations the linker cannot see

These are vtables, so nothing fails to link when they are missing; PuTTY simply crashes or
misbehaves at run time. They are the larger part of the work.

| Interface | Size | Notes |
|---|---|---|
| `Seat` | about 29 methods | Defaults for most are in `stubs/null-seat.c` (`nullseat_*`). Output, prompts, host-key confirmation, errors, session lifecycle |
| `TermWin` | 24 methods | The window surface the terminal draws on: `draw_text`, `draw_cursor`, clipboard, palette, title, scrollbar, resize, ... |
| `LogPolicy` | 5 methods | Event log output and the append/overwrite question |
| Timers | `timer_change_notify` | Provided by `utils/gui-timing.c` (hidden window plus `SetTimer`), which is why `hinst` is needed |
| Top-level callbacks | `request_callback_notifications` | Register a notifier that schedules `run_toplevel_callbacks` on the UI thread |
| Socket events | `winselgui_set_hwnd`, `WM_NETEVENT` | A window must receive the messages and call `select_result` |
| Handles | `windows/handle-wait.c` | Asserts at most 64 waitable handles; see `CLAUDE.md` |

## Suggested order

1. Items 2 to 4 (compile PuTTY's own stubs) and item 5: this should shrink the list from 45
   to the 30 `dlg_*` functions with little code.
2. A first `Seat`, `LogPolicy` and `TermWin` (plain output, no rendering), enough to open a
   Telnet or raw session; this is where the real engine work starts.
3. The 30 `dlg_*` functions together with the settings UI, since they only make sense there.
