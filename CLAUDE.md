# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

A WinUI 3 front end for PuTTY: a C# app (.NET 11, NativeAOT) on top of a native engine DLL built from the **unmodified** PuTTY core, which is the `external/putty` submodule ([drolevar/putty](https://github.com/drolevar/putty), a synchronized mirror of upstream, pinned at `29dd4b17`).

**State (2026-11-10): the skeleton works end to end; no product features yet.** Built and verified: the native engine DLL with its event loop and a headless session layer (`pe_session_*`: Seat and LogPolicy glue, raw and Telnet sessions tested against a loopback server), `PuTTY.Interop` (including `PuttySession`), and a `PuTTY.App` window that hosts the docking layout from the sketch and calls the engine, in both a normal build and a NativeAOT publish. A session can also get a terminal emulator (`pe_session_enable_terminal`, with a null `TermWin`: it runs, nothing is drawn) and keyboard input through a message hook and a port of PuTTY's `TranslateKey`. Not built: the terminal renderer (`TermWin`), SSH prompts and host-key confirmation, settings UI, CI, packaging. The only tests cover the managed/native interop contract (37 of them, stable over repeated runs). "Planned architecture" below is design intent, not code. Work is tracked in Beads (`bd ready`).

## Toolchain (verified on this machine)

- .NET SDK `11.0.100` (the final release of 2026-11-10, pinned in `global.json`; the older `11.0.100-rc.3.26522.148` is still installed but no longer selected). Visual Studio 2026 Enterprise 18.11, MSVC 14.51, Windows SDKs 10.0.26100.0 and 10.0.28000.0.
- MSVC has **x64/x86 targets only**; the ARM64 tools are not installed, so native builds are x64-only for now.
- `cmake` and `ninja` are not on PATH. Visual Studio bundles CMake 4.3.1 (`C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe`) and Ninja (sibling `..\Ninja\` folder); `build\native.cmd` uses them, so you rarely need the paths.
- `winapp` CLI is installed (`winapp new --list` shows the WinUI templates; the template pack `Microsoft.WindowsAppSDK.WinUI.CSharp.Templates` 0.0.7-alpha is installed globally).

## Build, publish, run

Everything: `dotnet build PuTTY.slnx -c Release` builds the native engine and the managed projects, and stages `puttyengine.dll` next to `PuTTY.App` (from clean about 80 s; a no-change rebuild about 19 s). Native only: `build\native.cmd` (x64), which finds Visual Studio with `vswhere`, enters its developer environment and runs CMake with Ninja; output is `artifacts/native/x64/puttyengine.dll`. Nothing needs to be on PATH.

How it is wired: `src/PuTTY.Native` is a `Microsoft.Build.NoTargets` project (SDK version in `global.json`) whose only job is to run `build\native.cmd`; any project that imports `build/NativeEngine.targets` (the app and the test project) gets a `ReferenceOutputAssembly=false` reference to it so it builds first, plus a `StageNativeEngine` target that adds the DLL as content (not a static `Exists()` item, which is false on a clean tree). `native/CMakeLists.txt` builds PuTTY itself as an ExternalProject and links its static libraries from one flat `lib/` folder. It requires a single-config generator (Ninja) and stops with an error under a multi-config one.

Tests (MSTest via `MSTest.Sdk`, running on Microsoft.Testing.Platform; versions and the `dotnet test` runner are set in `global.json`):

```
dotnet test --solution PuTTY.slnx -c Release
dotnet test --project tests/PuTTY.Interop.Tests -c Release --filter "Name=Probe_runs_PuTTY_Conf_inside_the_engine"   # one test
```

They load the real `puttyengine.dll`, so a test run builds the native engine first. Note the MTP-mode syntax: `--solution`/`--project` flags, not positional paths. Tests host the engine the way the app will: `tests/PuTTY.Interop.Tests/EngineThread.cs` starts a dedicated thread that owns one engine instance and pumps its messages, and a test runs engine calls on it through `Run`. Each test gets a fresh engine, and the assembly is `[DoNotParallelize]` because PuTTY's core has global state.

NativeAOT publish: `dotnet publish src/PuTTY.App -c Release -r win-x64` writes `artifacts/publish/PuTTY.App/release_win-x64/PuTTY.App.exe` (about 11 MB plus the Windows App SDK files). A `PublishAot` project produces no `.exe` from plain `dotnet build`; to run a non-AOT build use `dotnet build src/PuTTY.App -c Release -p:PublishAot=false` (it overwrites the same output folder).

Checking the UI: start the exe, then use `winapp ui search "placeholder" -a PuTTY.App` and `winapp ui screenshot -a PuTTY.App --output artifacts/spike/shot.png`, then close it. Check content, not just chrome: under NativeAOT the window titles can render while the content is missing.

- Why Ninja, measured for PuTTY's libraries from scratch: Ninja inside a VS developer environment configures in 25 s and builds in 51 s; the Visual Studio generator took 102 s and 107 s.
- Always build out-of-tree into `artifacts/` (git-ignored). Never build inside `external/putty`. A bash `cd` into it persists across tool calls in this environment, so use absolute paths.
- `plink` in `external/putty/CMakeLists.txt` is the template for which libraries an SSH client links. `guiterminal` bundles root `terminal/`, `ldisc.c`, `config.c`, `dialog.c` **plus** the Windows GUI files (`windows/dialog.c`, `controls.c`, ...), so link order matters if our engine defines its own `dlg_*` functions. The default engine links PuTTY's whole SSH client, backends, network code and portable terminal code (`terminal.c`, `bidi.c`, `lineedit.c`, `ldisc.c`, compiled directly), so `dotnet build` itself proves the front-end contract is met for all of that. `build\native.cmd x64 full` also compiles the config-box description (`config.c`, `dialog.c`) and is **expected to fail** with 31 unresolved externals (the 30 `dlg_*` functions and `show_ca_config_box`): the settings-UI to-do list, grouped in `docs/front-end-contract.md`. Re-run it as a progress check: it should end at zero.
- Shell traps in Git Bash: it rewrites MSVC-style `/flags` into paths (`dumpbin /nologo` fails), so `export MSYS_NO_PATHCONV=1`; and `cmd | tail` hides a failing exit code (a native build once reported exit 0 while failing), so use `set -o pipefail` and check `${PIPESTATUS[0]}`.

## Constraints that are not obvious

1. **PuTTY's CMake must be the top-level project.** It uses `${CMAKE_SOURCE_DIR}` about 96 times (including `charset/` and `cmake/gitcommit.cmake`). Never `add_subdirectory(external/putty)` from our own CMake; build it as a separate configure (for example via `ExternalProject`) and link its static libraries.
2. **No C++ file can include PuTTY headers.** `ssh.h` uses `new` as a vtable member name (lines 757, 834, 890, 1097). Code that talks to PuTTY (Seat, TermWin, LogPolicy, key translation) must be C. C++ (the Direct2D/DirectWrite renderer) sits behind a C interface.
3. **Keep `external/putty` pristine** so it can track upstream. Hooks the UI needs must live in our code or a fork branch. Example: the sketch's session inspector needs key-exchange, cipher and host-key fingerprint, and `putty.h` has no accessor for them.
4. **The core is single-threaded with global state** (timer queue, callback queue, PRNG, handle-wait list). `windows/handle-wait.c` asserts at most 64 waitable handles, and `cleanup_exit` terminates the process. Both matter once many sessions share one process.
5. **Never compile PuTTY code with `NDEBUG`** (`defs.h` errors out; assertions are part of its security model). `native/CMakeLists.txt` strips `/DNDEBUG` from the engine's flags. PuTTY's libraries are always built Release (`/MD`), so the engine always uses `/MD` too, even in its Debug configuration (mixing `/MD` and `/MDd` fails to link).
6. **The engine must define the functions PuTTY expects from a front end.** Linking `utils` alone needed only `modalfatalbox` and `cleanup_exit` (both in `native/engine/frontend.c`); the whole SSH client needed 14 more (printing, jump list, process-level), now defined in `frontend.c` or supplied by PuTTY's own stubs (`stubs/no-printing.c`, `windows/no-jump-list.c`), and the settings dialog still needs 31 (`docs/front-end-contract.md`). Do not link PuTTY's `guiterminal` library to make them go away: it bundles PuTTY's Win32 dialog code and silently hides what the engine must implement. Do not use `stubs/no-ca-config.c`: it is for programs without SSH code, and our engine links the real `ssh/ca-config.c`. The `Seat`, `TermWin` and `LogPolicy` obligations are runtime vtables the linker cannot check.
7. **PuTTY's assertions are live and mean it.** A crash like `Assertion failed ... tree234.c` kills the host process and is almost always misuse of a PuTTY API by our glue, not a PuTTY bug. Example seen: a timer scheduled with a `NULL` context (`schedule_timer(ms, fn, NULL)`) asserts in `run_timers`, because the context identifies the owner for `expire_timer_context`; timer contexts must be non-NULL. A second one: `conf_get_int(conf, CONF_tcp_keepalives)` asserts in `conf.c`, because that key is a boolean; the accessor must match the key's value type, so copy the call from `windows/window.c` or `windows/putty.c` instead of guessing (`conf_get_bool` here).

## NativeAOT + WinUI 3 on .NET 11 (verified on 11.0.100, first on rc.3, with Windows App SDK 2.6.5)

`PuTTY.App` is unpackaged (`WindowsPackageType=None`, self-contained Windows App SDK) and package-free by design; MSIX packaging is a separate step with `winapp package` (issue `putty-modern-apl`). The settings below live in `src/PuTTY.App/PuTTY.App.csproj` and each has a comment there; do not remove them without re-testing a NativeAOT publish **and** launching the result.

- **`UseOptimizedPublish=false`** (WindowsAppSDK [#6897](https://github.com/microsoft/WindowsAppSDK/issues/6897)): on the .NET 11 SDK, `PublishAot` turns on optimized publish, and XAML compilation then fails with `WMC1007`. This hits unpackaged apps too, not only single-project MSIX. The issue is fixed but unreleased. Re-tested on the 11.0.100 final SDK: removing the setting still fails (now as `MSB3030`, `PuTTY.App.pri` not found), so it stays.
- **`Microsoft.Windows.SDK.BuildTools.MSIX` pinned to 1.7.261102100** (WindowsAppSDK [#6394](https://github.com/microsoft/WindowsAppSDK/issues/6394), fixed from 1.7.260930100): Windows App SDK 2.6.5 pulls 1.7.251221100 transitively, so the pin relies on `CentralPackageTransitivePinningEnabled` in `Directory.Packages.props`. The publish output has no loose `.xbf` files (XAML is in `PuTTY.App.pri`) and the app starts fine.
- **`AllowUnsafeBlocks`** is required (CsWinRT error CsWinRT1030) because the docking library's types implement generic WinRT interfaces.
- **Under NativeAOT a WinUI class that managed code only ever receives from native code comes back as its nearest known base class** (cause of what used to be "the content of docked windows is silently dropped", Beads `putty-modern-sqm`, solved). The projection turns a native object into a managed one by its runtime class name; if the trimmer finds no use of that class, the lookup falls back to a base class, so `GetTemplateChild("PART_X") as ContentPresenter` is **null**, with no error. Measured in this app without any workaround: a `ContentPresenter` (from `XamlReader.Load`) came back as `FrameworkElement`, `Rectangle`, `Border` and `FontIcon` likewise, `Button` and `ToggleButton` and `ScrollViewer` as `ContentControl`/`ButtonBase`, `Canvas` as `Panel`, while `TextBlock`, `Grid` and `StackPanel` (constructed somewhere) were exact. `Digi21.WinUI.Docking` plugs its windows into a `ContentPresenter` it gets from its template, so the windows never appeared; its Close and pin buttons are `as Button` casts, so they did nothing. Constructing one instance of each such class in reachable code fixes it: `src/PuTTY.App/AotTypeAnchors.cs` (called from the `App` constructor) does that for the five classes the library casts to (`ContentPresenter`, `Rectangle`, `Border`, `Button`, `FontIcon`), and replaced the old `TrimmerRoots.xml` (a `DockSite` root), which fixed only `ContentPresenter` by accident: with it the Close button still did nothing. Verified on the 11.0.100 final SDK: no root plus the anchors gives 3 of 3 placeholders and a Close button that closes its window (invoked through UI Automation). **Whenever managed code casts a native object to a WinUI class it does not construct, add it to the anchors**, and check it by loading the class with `XamlReader.Load("<X xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'/>")` and looking at `GetType()`. The same applies to `FindName`, `VisualTreeHelper` and anything else that hands back an object created by native code. Not the cause, each tried: the library built from source against Windows App SDK 2.6.5 on net11, `IsAotCompatible`, wrapping every type in a trimmer root (`DockSite`, the containers, the whole assembly all "fixed" it only because they happen to keep some constructions alive).
- The write-up, including the original table of what was tried (which preserved types made the content appear), is in `docs/nativeaot.md` on the fork branch `nativeaot-readiness` ([drolevar/DockingPanels](https://github.com/drolevar/DockingPanels/tree/nativeaot-readiness)), together with a small library change (`AllowUnsafeBlocks`). The owner asked that any improvement to the library be done in that fork; nothing has been sent to the upstream repository, so ask before opening an issue or pull request there. A library-side fix (the same anchors inside `Digi21.WinUI.Docking`, so apps need nothing) is committed locally in the fork on branch `aot-template-part-types`, not pushed.
- **A C# subclass of a WinUI control must be `partial` for NativeAOT.** An unmarked one crashed the spike's AOT build at the first frame with `COMException 0x80040111 (ClassFactory cannot supply requested class)` naming `Windows.ApplicationModel.LimitedAccessFeatures`, which is misleading; the real signal was a **CsWinRT1028** warning ("isn't marked partial") that a filtered build log had hidden. Marking it `partial` fixes it (`spikes/keyboard-ime/SubclassProbe.cs`, `--probe-partial` / `--probe-plain`). **Read every CsWinRT warning in a NativeAOT publish**; do not grep the log for only `error|warning CS|IL`.
- WinUI template (`winapp new`) facts, if you scaffold another project: it targets net10 and is packaged by default, and it uses unpinned `Version="*"` references that must move into central package management.

## Planned architecture

```
external/putty/        submodule (pristine)                                   exists
native/                our C/C++ only: CMakeLists, CMakePresets, engine/      exists (C-ABI skeleton)
build/native.cmd       builds the native engine (CMake + Ninja, VS dev env)    exists
src/PuTTY.Native/      NoTargets project that runs build\native.cmd            exists
src/PuTTY.Interop/     LibraryImport declarations, AOT-compatible             exists
src/PuTTY.App/         WinUI 3 app, unpackaged, PublishAot, placeholder UI    exists
build/NativeEngine.targets  shared: build native first, stage the DLL          exists
tests/PuTTY.Interop.Tests/  MSTest, loads the real native engine               exists
spikes/keyboard-ime/   throwaway keyboard/IME experiment, not in PuTTY.slnx      exists
```

- **Layers:** PuTTY core (C) -> engine DLL (C glue plus C++ renderer, flat C ABI) -> `PuTTY.Interop` -> WinUI 3 app. Interop uses `LibraryImport`, `[assembly: DisableRuntimeMarshalling]`, blittable signatures and `[UnmanagedCallersOnly]` callbacks. Sessions are deliberately **not** `SafeHandle`s (see "Sessions" below). The engine starts as a DLL; static linking is a later option.
- **What gets replaced vs reused** (see the PuTTY map below): replace the `windows/` GUI group (`window.c`, `dialog.c`, `controls.c`, `config.c`, `putty.c`, `select-gui.c`, ...). Reuse the platform layer (`network.c`, `handle-io.c`, `storage.c`, `noise.c`, `serial.c`, `conpty.c`, `agent-client.c`, `sharing.c`, `gss.c`, `utils/`).
- **Threading (implemented in `native/engine/eventloop.c`):** the engine lives on the one thread that calls `pe_init`, which must run a Win32 message loop (the WinUI UI thread does; tests call `pe_pump`). A hidden message-only window receives `WM_NETEVENT` (forwarded to `winselgui_response`); `setup_gui_timing` supplies timers; `request_callback_notifications` posts a message that runs **one** deferred callback and re-posts while more are queued, like PuTTY's own loop, so window messages are not starved. Every other `pe_*` call from a different thread returns `PE_ERR_THREAD`. `pe_shutdown` closes sockets and drains messages before destroying the window (a stale entry in `select-gui.c`'s moribund-socket set would make a later socket that reuses a handle value ignore its events). Still to do: replace `handle-wait.c` with thread-pool waits.
- **Sessions (implemented in `native/engine/session.c`, managed side `src/PuTTY.Interop/PuttySession.cs`):** a `pe_session` owns a `Seat`, a `LogPolicy`, a `Conf` (from `load_open_settings(NULL, ...)` defaults for now), a `LogContext` and a `Backend`. Output, log lines, errors and close go to the host through the blittable `pe_session_events` table; the managed side turns them into `Output`/`Log`/`Error`/`Closed` events on the engine thread, and every `[UnmanagedCallersOnly]` callback catches all exceptions. Things that are easy to get wrong:
  - `PuttySession` is two-phase (construct, subscribe, then `Start()`) so the first lines of the event log are not lost: PuTTY logs "Connecting to ..." from inside `backend_init`.
  - `Closed` is raised **once**: PuTTY documents that `notify_remote_disconnect` may arrive more than once, even while `backend_connected()` is still true, so the glue ignores it until the backend says the connection is gone, and `connection_fatal` always closes; a flag makes whichever comes first the only notification.
  - A handler may dispose the session it is being called for. PuTTY is still on the stack inside the backend then, so `pe_session_close` from inside a callback marks the session `pending_close` (events are silenced at once) and queues the teardown with `queue_toplevel_callback`. A live-session list validates handles, so a stale `pe_session*` is rejected rather than dereferenced.
  - `PuttySession` is a plain `IDisposable`, not a `SafeHandle`: a `SafeHandle` is released on the finalizer thread, and the engine refuses calls from any thread but its own (`PE_ERR_THREAD`). A session that is never disposed is closed by `pe_shutdown` (`pe_sessions_close_all`), and disposing it afterwards is harmless.
  - Not done yet: `Seat` prompts (`get_userpass_input`), host-key and cipher confirmations (they fail safe through the `nullseat_*` defaults, slice 3), `Conf` is not yet configurable from the host. Warnings that belong to no session (`nonfatal()`, `old_keyfile_warning()`) are forwarded: `frontend.c` calls the handler set with `pe_set_warning_handler` (dropped by `pe_shutdown`, registered again by `PuttyEngine.Initialize`), which raises `PuttyEngine.Warning` on the engine thread; without a subscriber they only reach the debugger.
- **Renderer (`native/engine/render.cpp`, interface in `render.h`):** C++ (it cannot see PuTTY headers), Direct2D and DirectWrite, drawing a grid of cells one glyph per cell so fallback fonts cannot misalign it. Two targets with one drawing path: offscreen (a WIC bitmap, software, used by the tests: no window, no GPU) and a DXGI composition swap chain plugged into a XAML `SwapChainPanel` through `Microsoft.UI.Xaml.Media.DXInterop.ISwapChainPanelNative` (IID `63aad0b8-...`, not the UWP one). Panel drawing goes to a persistent canvas bitmap that is copied to the swap chain on each present, because a flip-model back buffer does not keep its contents and the emulator repaints only what changed. The panel path is not exercised until the terminal view exists (Beads `88o.5`). `pe_diag_render_text` / `PuttyEngine.Diagnostics.RenderText` draw text offscreen and report cell size and per-cell ink, which `RenderTests` use.
- **Terminal (implemented in `native/engine/termwin.c`, wired in `session.c`):** the emulator draws through a real `TermWin` onto the renderer; with no renderer attached (`pe_session_enable_terminal` alone) it answers everything and draws nothing. `pe_session_attach_offscreen_view` / `pe_session_attach_panel_view` create the surface, fit the terminal to the whole cells that fit (`term_size` + `backend_size`) and repaint; `pe_session_resize_view` and `pe_session_set_font` refit it; `pe_session_set_terminal_events` reports title, bell and scrollbar. The drawing code is `windows/window.c`'s `do_text_internal` and `wintw_draw_cursor` with GDI swapped for cell calls, so colour selection (reverse, bold as bright colour, cursor colours 260/261, truecolour, dim) matches PuTTY. `TerminalViewTests` read pixels back from an offscreen view after a loopback server sends text and escape sequences. Not done: clipboard and mouse (88o.6), double-height lines (drawn at normal size), the view in the app (88o.5). Two traps that each cost a debugging round:
  - **`init_ucs` links characters to the screen font.** It replaces characters the (never set) screen font's code page has with "direct to font" codes (`CSET_ACP`/`CSET_OEMCP` + index, 0xDC00-0xDDFF), which window.c's GDI path draws with ANSI calls. We draw Unicode, so `termwin.c` turns them back (`resolve_direct_font`). Without it every ASCII character is a missing-glyph box.
  - **Trust sigils.** The emulator starts "trusted" and draws a two-cell marker at the start of each line until the backend calls `seat_set_trust_status(false)` (Raw and Telnet do it at once, SSH after login). The seat must implement it (`session_set_trust_status`, which remembers the value because the emulator may be created after the backend says it) and answer `can_set_trust_status` yes, or the first two cells of every line are a solid block.
- **Keyboard input (implemented in `native/engine/keys.c`; the design came from the spike, `docs/spike-keyboard-ime.md`):** keys come from the raw message stream, not from XAML events. A `WH_GETMESSAGE` hook on the engine thread, in C, hands key messages to `pe_translate_key` (PuTTY's `TranslateKey` with a short list of deliberate changes, written at the top of the file; keep it diffable against `windows/window.c`) and replaces them with `WM_NULL`, only while the app has called `pe_set_key_target` (`PuttySession.SetKeyboardTarget`, from the terminal view's focus events) for a session that has a terminal; `VK_PROCESSKEY` and `VK_PACKET` key-downs pass through and `WM_CHAR` is taken as text. The tests (`KeyboardTests.cs`) inject nothing into the desktop: they post the messages Windows would to the engine thread, describe held modifiers with `SetKeyboardState`, activate a layout on that thread, and read the bytes that reach a loopback server. Two things that cost time: a Raw session edits the line locally by default (the line discipline holds typing until Enter), so tests call `SetLocalEchoAndEditing(Off, Off)`; and the engine sends UTF-8 (`CONF_line_codepage` is set when the terminal is enabled). On four layouts (en-US, nb-NO with dead keys and AltGr, ru-RU, uk-UA) this matched Windows' own translation for text, dead keys, AltGr, Unicode and CapsLock, and XAML cannot match it elsewhere: a handled `KeyDown` suppresses `CharacterReceived`, `Alt+Space` still opens the system menu, Alt+key characters are not delivered, Tab arrives with scan code 0 and `VK_PACKET` as 0xFF. `CoreTextEditContext` is not usable in WinUI 3 desktop (no HWND interop). **IME composition is not verified** (no IME on the host, none installable in the Sandbox); the two candidate routes (PuTTY's IMM32 handlers on the island's input window, or a proxy `TextBox`) are in the report.
- **Settings UI:** a generic renderer over `config.c`'s control tree (about 210 controls, 32 pages, 37 `dlg_*` hooks) plus a presentation overlay. The sketch covers only 17 of the 32 pages (no Telnet, Rlogin, SUPDUP, Selection/Copy or the 11 SSH sub-pages).
- **Build:** in place (see "Build, publish, run"): `PuTTY.slnx`, central package management (`Directory.Packages.props`), SDK pinned in `global.json`, `UseArtifactsOutput` into `artifacts/`, and the native build driven from MSBuild through `src/PuTTY.Native`.

## External references (outside this repo)

- Design sketch: Figma file `UMKaxI9tppR43aaqCONBPu` (frames "PuTTY · Docked workspace" 9:2, "Session configuration" 9:3, "Terminal preferences" 9:4).
- Docking library source (a working clone, not part of this repo): `D:\Projects\DockingPanels`, with `origin` = the upstream `digi21/DockingPanels` and `fork` = `drolevar/DockingPanels`; library work goes on a branch there (currently `nativeaot-readiness`). Commits in that clone use the GitHub no-reply address, because the fork is public and the global git email is a work address. The app uses the released NuGet package `Digi21.WinUI.Docking`.
- PuTTY internals: `D:\Projects\putty` is a clone of the upstream tree, and `D:\Projects\putty\CODEMAP.md` (untracked there) maps it. It was generated partly by small models and then spot-checked; treat unverified spots as unverified.

## Skills to use

Installed skills cover this stack: `dotnet-project-structure` (slnx, `Directory.Build.props`, central packages, `global.json`), `aot-trimming`, `dotnet-pinvoke`, `winapp-*` (setup, package, manifest, UI automation), and `mvvm-toolkit*`.


<!-- BEGIN BEADS INTEGRATION v:1 profile:minimal hash:1105d646 -->
## Beads Issue Tracker

This project uses **bd (beads)** for issue tracking. Run `bd prime` to see full workflow context and commands.

### Quick Reference

```bash
bd ready              # Find available work
bd show <id>          # View issue details
bd update <id> --claim  # Claim work
bd close <id>         # Complete work
```

### Rules

- Use `bd` for ALL task tracking — do NOT use TodoWrite, TaskCreate, or markdown TODO lists
- Run `bd prime` for detailed command reference and session close protocol
- Use `bd remember` for persistent knowledge — do NOT use MEMORY.md files

**Architecture in one line:** issues live in a local Dolt DB; sync uses `refs/dolt/data` on your git remote; `.beads/issues.jsonl` is a passive export. See https://github.com/gastownhall/beads/blob/main/docs/core-concepts/sync-concepts.md for details and anti-patterns.

## Agent Context Profiles

The managed Beads block is task-tracking guidance, not permission to override repository, user, or orchestrator instructions.

- **Conservative (default)**: Use `bd` for task tracking. Do not run git commits, git pushes, or Dolt remote sync unless explicitly asked. At handoff, report changed files, validation, and suggested next commands.
- **Minimal**: Keep tool instruction files as pointers to `bd prime`; use the same conservative git policy unless active instructions say otherwise.
- **Team-maintainer**: Only when the repository explicitly opts in, agents may close beads, run quality gates, commit, and push as part of session close. A current "do not commit" or "do not push" instruction still wins.

## Session Completion

This protocol applies when ending a Beads implementation workflow. It is subordinate to explicit user, repository, and orchestrator instructions.

1. **File issues for remaining work** - Create beads for anything that needs follow-up
2. **Run quality gates** (if code changed) - Tests, linters, builds
3. **Update issue status** - Close finished work, update in-progress items
4. **Handle git/sync by active profile**:
   ```bash
   # Conservative/minimal/default: report status and proposed commands; wait for approval.
   git status

   # Team-maintainer opt-in only, unless current instructions forbid it:
   git pull --rebase
   git push
   git status
   ```
5. **Hand off** - Summarize changes, validation, issue status, and any blocked sync/commit/push step

**Critical rules:**
- Explicit user or orchestrator instructions override this Beads block.
- Do not commit or push without clear authority from the active profile or the current user request.
- If a required sync or push is blocked, stop and report the exact command and error.
<!-- END BEADS INTEGRATION -->
