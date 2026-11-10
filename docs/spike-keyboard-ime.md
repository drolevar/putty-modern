# Spike: keyboard and IME input for a custom terminal control

Result of Beads issue `putty-modern-gck`, measured on 2026-11-10 with Windows App SDK 2.6.5, .NET
11 (rc.3 SDK) and PuTTY `29dd4b17`. The throwaway app is `spikes/keyboard-ime/` (not part of
`PuTTY.slnx`); `winapp`/`wsb` commands to rerun it are at the end.

## The question

PuTTY's `TranslateKey` (`windows/window.c`, lines 4150-4822) is driven by raw Win32 messages: it
takes `WM_KEYDOWN`/`WM_SYSKEYDOWN`/`WM_KEYUP`/`WM_SYSKEYUP` with their `wParam` (virtual key) and
`lParam` (repeat, scan code, extended flag, context code, previous state, transition), reads the
keyboard state with `GetKeyboardState`, and for ordinary keys calls `ToUnicodeEx` itself. It also
keeps its own state (compose key, the Alt+numeric-keypad accumulator) across the key-down and
key-up messages, and PuTTY deliberately never calls `TranslateMessage` for keys so that the
translation, including dead keys, stays under its control. A WinUI 3 terminal has to get
keystrokes, dead keys, AltGr, the numeric keypad and IME text into that function (or a
replacement for it) from a control that is not a `TextBox`.

## What was tested

Three designs, each compared with the raw message stream (`WM_KEYDOWN`, `WM_CHAR`, `WM_SYSCHAR`,
`WM_DEADCHAR`, recorded by a `WH_GETMESSAGE` hook) as ground truth:

| | Design | How text and keys arrive |
|---|---|---|
| A / A2 | XAML events on a focusable `UserControl` | `KeyDown`/`KeyUp` for keys, `CharacterReceived` for text. A handles every `KeyDown` (`Handled = true`); A2 handles only keys that are not text. |
| B | Thread message hook | A `WH_GETMESSAGE` hook on the UI thread sees each message before it is translated or dispatched, handles key messages itself (a faithful copy of `TranslateKey`'s `ToUnicodeEx` path) and replaces them with `WM_NULL`. |
| C | Proxy `TextBox` | A real `TextBox` holds focus and supplies text (and IME composition); keys that are not text are handled in `PreviewKeyDown`. |

The spike injects its own keystrokes with `SendInput` (scan codes, so the real keyboard pipeline
runs) on four installed layouts: en-US, nb-NO (dead keys and AltGr), ru-RU and uk-UA Enhanced.
27,048 keys, 7,020 steps and 67,956 events in the full run. It ran inside Windows Sandbox so the
injected keys cannot reach the host desktop; the app additionally refuses to send a key unless it is the
foreground window and the expected element has XAML focus.

## Findings

**Key identity: XAML is faithful.** For every key tested (48 letter, digit and punctuation keys under
none/Shift/AltGr/Ctrl/Alt/Ctrl+Alt; the ten navigation keys; F1-F12; the numeric keypad with
NumLock on and off; the modifiers themselves) the virtual key, scan code and extended-key flag in
XAML's `KeyDown` equal the raw `WM_KEYDOWN`, so the keypad-versus-cursor-block distinction that
`TranslateKey` needs (`VK_NUMPAD1` versus `VK_END` with the extended flag) survives. Exceptions:

- Tab arrives with scan code 0 (the others keep theirs).
- A Unicode key event (`VK_PACKET`, 0xE7) arrives as virtual key 0xFF.

**Text: all three designs match Windows' own translation.** On all four layouts the text each design
delivered was identical to the raw `WM_CHAR` stream for letters, Shift, CapsLock, auto-repeat,
Unicode packets (`é`, `日`, and an emoji as a surrogate pair), AltGr characters on Norwegian
(`@ £ $ € { [ ] } µ`) and dead keys: `¨`+`a`=`ä`, `¨`+`e`=`ë`, AltGr `´`+`a`=`á`, `´`+`e`=`é`,
dead key then Space gives the accent itself, dead key twice gives it twice, and a dead key
followed by a non-composing letter gives accent plus letter. In design B the hook swallowed every
key message with zero leaks to XAML, and calling `ToUnicodeEx` itself on every key message, down
and up, exactly as `TranslateKey` does, did not disturb dead-key state.

**What differs between the designs is everything that is not text:**

1. **A handled `KeyDown` suppresses `CharacterReceived`.** With design A (every `KeyDown`
   handled) no text key produced a character event at all. A terminal on XAML events must leave
   text keys unhandled and decide per key, before it knows whether the key makes text.
2. **`Alt+Space` cannot be kept from opening the system menu with XAML events.** XAML delivered
   the Alt and Space key-downs, `Handled = true` did not stop the `WM_SYSCHAR` that opens the menu,
   and the next two Esc presses went to the menu loop. PuTTY has a setting for exactly this
   (`CONF_alt_space`). Design B swallows `WM_SYSCHAR` itself and has no such problem.
3. **Alt+key text is not delivered as characters by XAML.** The raw stream has a `WM_SYSCHAR` for
   Alt+Q; `CharacterReceived` does not fire. A terminal that wants ESC plus the character must
   derive the character from the key event itself.
4. **Menu-like keys:** F10, a lone Alt tap, the Apps key and Shift+F10 all reached the XAML control
   as `KeyDown`. With the proxy `TextBox`, Shift+F10 took focus away from it (the harness had to
   restore focus; presumably its context menu) even though `ContextRequested`, `PreviewKeyDown` and `PreviewKeyUp` were handled.
5. **Alt+numeric keypad** (`Alt`+`0233` giving `é`) works in XAML (the character is delivered) but
   is not reproduced by a hook that swallows the key messages: Windows builds that character inside
   `TranslateMessage`. PuTTY has its own accumulator (`alt_numberpad_accumulator`) for exactly
   this, so a faithful port does not rely on Windows for it.

## What did not work or could not be tested

- **`CoreTextEditContext` is not an option.** `CoreTextServicesManager.GetForCurrentView()` needs a
  `CoreWindow`, which does not exist in a WinUI 3 desktop app, and the Windows SDK 10.0.26100
  headers have no HWND-based interop (`ICoreTextServicesManagerInterop` is not declared anywhere
  in the SDK or the Windows App SDK packages). A Windows App SDK maintainer says the same in
  discussion #3226. That removes the standard route for IME in a custom text control.
- **The island keyboard source gave nothing.** `InputKeyboardSource.GetForIsland` on the window's
  `ContentIsland` raised no events while XAML owned the input; not pursued.
- **IME composition itself.** See the IME section.
- **Injection fidelity.** In the Sandbox's remote session, injected scan codes were converted to
  virtual keys with the system layout, not the layout the thread had active (nb-NO scan `0x1B`
  gave `VK_OEM_6` instead of `VK_OEM_1`), and an injected right Alt did not produce the fake
  left Ctrl a real AltGr produces. The harness works around both by sending virtual keys it
  computes from the layout and by sending Ctrl then right Alt. This affects how keys were injected,
  not how any design received them, but it means a real keyboard on real hardware was not part of
  the run.

## NativeAOT

The spike was also published with `PublishAot=true` (`UseOptimizedPublish=false`, no warnings) and
run: a posted-message smoke test on the host, and the quick injected-key run (two layouts,
4,476 keys) in the Sandbox. The results are identical to the JIT build: the hook records the raw
messages, the XAML events fire, the ported `ToUnicodeEx` path produces the same text, and
swallowing leaves XAML with no key events. `[UnmanagedCallersOnly]` function pointers as hook
procedures work under NativeAOT, and so do the Win32 imports (`LibraryImport`).

One thing did **not** work and cost most of the AOT time: **a C# subclass of a WinUI control
crashed the AOT build at the first frame** with a stowed exception, surfaced as
`COMException 0x80040111 (ClassFactory cannot supply requested class)` naming
`Windows.ApplicationModel.LimitedAccessFeatures`. It happened with a code-only
`class TerminalKeyControl : UserControl` with no event handlers at all, whether it was declared in
XAML markup or created in code, with or without rooting the whole assembly, and never in the JIT
build. Bisecting by removing elements one at a time (hooks, TextBox, buttons, log, then the control)
isolated the control; replacing the subclass with a plain `UserControl` that has handlers attached
fixed it, with no other change. The message is misleading (it names a class that has nothing to do
with the control). This may be related to the open question of why docked-window content is lost
under NativeAOT (Beads `putty-modern-sqm`: the docking library is made of derived controls); not
investigated further here. For the terminal view it means: do not assume a C# subclass of a control
is safe under NativeAOT, test it early.

## IME: not verified

This is the one requirement the spike could not check.

- No CJK input method is installed on the host (only en-US, nb, ru and uk with basic typing).
- A Japanese input method could not be installed in the Sandbox either: its image points Windows
  Update at `127.0.0.1` on purpose, and after that was undone inside the disposable guest the
  `Language.Basic~~~ja-JP` capability still failed to stage (`0x800f0991`: the language package
  is built for 26100.1 and the image is at 26100.9711).
- What the spike did establish: `CoreTextEditContext` is unavailable (see above), and a
  `WH_CALLWNDPROC` hook can observe the sent `WM_IME_*` messages (none appeared, as expected without
  an IME).

Two routes remain, to be decided with a real IME:

1. **PuTTY's own legacy path.** Handle `WM_IME_STARTCOMPOSITION`, `WM_IME_COMPOSITION`
   (`ImmGetCompositionStringW(GCS_RESULTSTR)`), `WM_IME_CHAR` and `ImmSetCompositionWindow` the way
   `windows/window.c` does (lines 3286-3352 and 3504-3508), with the messages taken from the
   island's input window (class `InputSiteWindowClass`, where every key message landed) by
   subclassing it or with a `WH_CALLWNDPROC` hook. It needs no XAML text control. Unknown: whether the
   island's window takes part in the IMM32 compatibility layer.
2. **A proxy `TextBox`** that receives focus only while composing (or always), which would also supply
   the emoji panel, touch keyboard and voice typing: those arrive through a TSF text store that
   neither the hook nor plain XAML key events provide. In the spike it delivered all ordinary text
   correctly; its `TextCompositionStarted/Changed/Ended` events are wired up and logged but never
   fired. Its problems are non-text keys (Shift+F10 took focus away even with the events handled)
   and focus handling.

To test with a real IME on a machine that has one: run `KeyboardIme.exe` with no arguments, click
"Focus proxy TextBox", switch to the IME, type `a`, `ka`, Enter and a pinyin word, and read the log
(compositions show as `CompositionStarted/Changed/Ended`); then "Focus terminal control" and "Hook
swallows keys" to see which `WM_IME_*` messages and `VK_PROCESSKEY` key-downs the terminal path gets
(`sent` rows are the sent messages). `--selftest --layouts E0010411 --passes A2,B,C` does the same
scripted for the Japanese IME (`E0010411`; `E0040804` for Chinese).

## Recommendation

**Take keys from the raw message stream, in the engine, in C (design B).**

- Install a `WH_GETMESSAGE` hook on the UI thread when the engine starts a terminal. The app tells
  the engine which terminal has keyboard focus (XAML `GotFocus`/`LostFocus` → one call); only while
  one does, key messages go to the ported `TranslateKey` with `(message, wParam, lParam)` exactly as
  PuTTY's window procedure gets them, and the hook turns them into `WM_NULL`. `WM_CHAR`,
  `WM_SYSCHAR` and `WM_UNICHAR` go to the handlers `window.c` already has (surrogate pairs included).
  `VK_PROCESSKEY` and `VK_PACKET` key-downs are passed through, as PuTTY lets Windows translate those.
  With no terminal focused the hook does nothing (the spike showed no effect on other input).
- This is the only design without gaps in the findings above: nothing to re-synthesise (`lParam`,
  Tab's scan code, `VK_PACKET`), `Alt+Space` and the `WM_SYSCHAR` that opens the system menu can be
  swallowed under the control of `CONF_alt_space`, Alt+key characters are available, text keys do
  not have to be classified before it is known whether they produce text, and dead keys, AltGr,
  Unicode packets and CapsLock matched Windows' own translation on four layouts.
- A hook procedure written in C in the engine makes no managed call per keystroke, which also keeps it
  clear of the derived-control crash above.
- Keep the XAML `KeyDown`/`CharacterReceived` route only as a fallback for the few keys the hook
  cannot see; do not build on it.
- **IME:** decide between the two routes above once a real IME can be tested. Until then the terminal
  has correct text input for every non-IME layout, and IME input is the main open risk of slice 2.

Not covered by the spike: auto-repeat latency, key-up handling in the hook, accessibility (a
screen reader needs a UI Automation text provider for the terminal in any design), the touch
keyboard and emoji panel (they need a TSF text store, so route 2), and real hardware keyboards (all
keys were injected, with the layout workarounds described above).

## Rerunning

```
dotnet build spikes/keyboard-ime -c Release                     # normal build
dotnet publish spikes/keyboard-ime -c Release -r win-x64 -p:PublishAot=true -p:UseOptimizedPublish=false
KeyboardIme.exe                                                  # interactive: focus a target, type, read the log
KeyboardIme.exe --smoke --out DIR                                # safe anywhere: posts three messages to itself
KeyboardIme.exe --selftest --out DIR [--quick] [--layouts 0409,0414,0419,0422] [--passes A,A2,B,C] [--groups sweep,dead]
```

`--selftest` injects keys with `SendInput`: **run it only inside a disposable Windows Sandbox**
(add `--force-foreground` there; the service-started process is otherwise refused the foreground).
It refuses to start unless it is the foreground window, aborts if focus leaves the expected element,
releases every modifier on the way out and restores NumLock, CapsLock and the layout.

How it was run here, since `winapp` could not drive the Sandbox (its guest agent never became ready,
possibly because of the host's endpoint-protection software): start Windows Sandbox from the Start
menu, then from the host `wsb share --id <id> -f <host folder> -s C:\spike -w` and
`wsb exec --id <id> -r ExistingLogin -c "cmd /c C:\spike\app\KeyboardIme.exe --selftest ..."`.
`ExistingLogin` only worked for a Sandbox started by hand; the guest needs the .NET runtime unless the
spike is published self-contained. Results (`summary.md`, `results.json`) land in the shared folder.
