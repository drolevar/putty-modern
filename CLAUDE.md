# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

A WinUI 3 front end for PuTTY: a C# app (.NET 11, NativeAOT) on top of a native engine DLL built from the **unmodified** PuTTY core, which is the `external/putty` submodule ([drolevar/putty](https://github.com/drolevar/putty), a synchronized mirror of upstream, pinned at `29dd4b17`).

**State (2026-11-10): early scaffolding.** Only the submodule (staged, not committed), `.gitignore` and `LICENSE` exist. There are no managed projects, native engine, tests or CI yet, so there are no build/lint/test commands beyond the native spike below. "Planned architecture" is design intent agreed in discussion, not code.

## Toolchain (verified on this machine)

- .NET SDK `11.0.100-rc.3.26522.148` (the default here). Visual Studio 2026 Enterprise 18.11, MSVC 14.51, Windows SDKs 10.0.26100.0 and 10.0.28000.0.
- MSVC has **x64/x86 targets only**; the ARM64 tools are not installed, so native builds are x64-only for now.
- `cmake` is not on PATH. Use VS's bundled CMake 4.3.1: `C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe` (Ninja is in the sibling `..\Ninja\` folder).
- `winapp` CLI is installed (`winapp new --list` shows the WinUI templates; the template pack `Microsoft.WindowsAppSDK.WinUI.CSharp.Templates` 0.0.7-alpha is installed globally).

## Building PuTTY's libraries (native spike)

```
cmake -S external/putty -B artifacts/spike/putty-x64 -G "Visual Studio 18 2026" -A x64
cmake --build artifacts/spike/putty-x64 --config Release --parallel 8 --target utils crypto network settings eventloop sshclient otherbackends guiterminal
```

Both steps work (use the full CMake path above). Configure takes about 100 s because the VS generator turns every compile probe (AES-NI, SHA-NI, CLMUL, ARM) into an MSBuild run; Ninja is untested. The build had no compiler warnings or errors and leaves static libraries in `artifacts/spike/putty-x64/Release/` (the targets above plus `handle-io`, `sshcommon`, `logging`, `all-backends` and three `object_lib_HAVE_*` CPU-feature libraries). **Not done yet:** linking an engine DLL against them, which will show the full list of symbols a front end must supply.

- Always build out-of-tree into `artifacts/` (git-ignored). Never build inside `external/putty`. A bash `cd` into it persists across tool calls in this environment, so use absolute paths.
- `plink` in `external/putty/CMakeLists.txt` is the template for which libraries an SSH client links. `guiterminal` bundles root `terminal/`, `ldisc.c`, `config.c`, `dialog.c` **plus** the Windows GUI files (`windows/dialog.c`, `controls.c`, ...), so link order matters if our engine defines its own `dlg_*` functions.

## Constraints that are not obvious

1. **PuTTY's CMake must be the top-level project.** It uses `${CMAKE_SOURCE_DIR}` about 96 times (including `charset/` and `cmake/gitcommit.cmake`). Never `add_subdirectory(external/putty)` from our own CMake; build it as a separate configure (for example via `ExternalProject`) and link its static libraries.
2. **No C++ file can include PuTTY headers.** `ssh.h` uses `new` as a vtable member name (lines 757, 834, 890, 1097). Code that talks to PuTTY (Seat, TermWin, LogPolicy, key translation) must be C. C++ (the Direct2D/DirectWrite renderer) sits behind a C interface.
3. **Keep `external/putty` pristine** so it can track upstream. Hooks the UI needs must live in our code or a fork branch. Example: the sketch's session inspector needs key-exchange, cipher and host-key fingerprint, and `putty.h` has no accessor for them.
4. **The core is single-threaded with global state** (timer queue, callback queue, PRNG, handle-wait list). `windows/handle-wait.c` asserts at most 64 waitable handles, and `cleanup_exit` terminates the process. Both matter once many sessions share one process.

## NativeAOT + WinUI 3 on .NET 11 (known issues as of 2026-11-10)

- Prefer an **unpackaged** app (`WindowsPackageType=None`, self-contained Windows App SDK, as DockingPanels' sample does) and package separately with `winapp package`.
- WindowsAppSDK [#6897](https://github.com/microsoft/WindowsAppSDK/issues/6897): single-project MSIX plus `PublishAot` on the .NET 11 SDK produces no package. It is fixed but unreleased; the workaround is `UseOptimizedPublish=false`.
- WindowsAppSDK [#6394](https://github.com/microsoft/WindowsAppSDK/issues/6394): `.xbf`/`.pri` missing from NativeAOT output, fixed in `Microsoft.Windows.SDK.BuildTools.MSIX` 1.7.260930100 or later. Pin it (latest stable is 1.7.261102100).
- Latest stable Windows App SDK is 2.6.5. `Digi21.WinUI.Docking` 1.6.0 is built against 1.8.251106002 (`net8.0-windows10.0.19041.0`) and does not declare AOT compatibility. Whether it works on 2.x and under `PublishAot` is **untested**; verify before building on it.

## Planned architecture

```
external/putty/   submodule (pristine)
native/           our C/C++ only: CMakeLists, CMakePresets, engine/
src/              PuTTY.Interop, PuTTY.App (WinUI 3, PublishAot), ViewModels
tests/
```

- **Layers:** PuTTY core (C) -> engine DLL (C glue plus C++ renderer, flat C ABI) -> `PuTTY.Interop` -> WinUI 3 app. Interop uses `LibraryImport`, `[assembly: DisableRuntimeMarshalling]`, blittable signatures, `[UnmanagedCallersOnly]` callbacks and `SafeHandle`s. The engine starts as a DLL; static linking is a later option.
- **What gets replaced vs reused** (see the PuTTY map below): replace the `windows/` GUI group (`window.c`, `dialog.c`, `controls.c`, `config.c`, `putty.c`, `select-gui.c`, ...). Reuse the platform layer (`network.c`, `handle-io.c`, `storage.c`, `noise.c`, `serial.c`, `conpty.c`, `agent-client.c`, `sharing.c`, `gss.c`, `utils/`).
- **Threading:** all sessions on the UI thread. A hidden message window handles `WM_NETEVENT` and timers (`winselgui_set_hwnd`, `windows/utils/gui-timing.c`), and `request_callback_notifications` feeds the dispatcher queue. Replace `handle-wait.c` with thread-pool waits.
- **Terminal:** implement `TermWin` (24 methods) natively onto a swap chain, and `Seat` (about 29 methods; defaults in `stubs/null-seat.c`). Port key translation from `windows/window.c` (`TranslateKey`, lines 4150-4822).
- **Settings UI:** a generic renderer over `config.c`'s control tree (about 210 controls, 32 pages, 37 `dlg_*` hooks) plus a presentation overlay. The sketch covers only 17 of the 32 pages (no Telnet, Rlogin, SUPDUP, Selection/Copy or the 11 SSH sub-pages).
- **Build:** the native build is driven from MSBuild through a NoTargets wrapper project. Managed side: `.slnx`, central package management, SDK pinned in `global.json`, `UseArtifactsOutput`.

## External references (outside this repo)

- Design sketch: Figma file `UMKaxI9tppR43aaqCONBPu` (frames "PuTTY · Docked workspace" 9:2, "Session configuration" 9:3, "Terminal preferences" 9:4).
- Docking library source for reading (a reference clone, not part of this repo): `D:\Projects\DockingPanels`. Use the NuGet package `Digi21.WinUI.Docking` in the app, behind your own interface.
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
