# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

A WinUI 3 front end for PuTTY: a C# app (.NET 11, NativeAOT) on top of a native engine DLL built from the **unmodified** PuTTY core, which is the `external/putty` submodule ([drolevar/putty](https://github.com/drolevar/putty), a synchronized mirror of upstream, pinned at `29dd4b17`).

**State (2026-11-10): the skeleton works end to end; no product features yet.** Built and verified: the native engine DLL (`pe_abi_version`, `pe_probe`), `PuTTY.Interop`, and a `PuTTY.App` window that hosts the docking layout from the sketch and calls the engine, in both a normal build and a NativeAOT publish. Not built: sessions, terminal, Seat/TermWin glue, settings UI, CI, packaging. The only tests cover the managed/native interop contract. "Planned architecture" below is design intent, not code. Work is tracked in Beads (`bd ready`).

## Toolchain (verified on this machine)

- .NET SDK `11.0.100-rc.3.26522.148` (the default here). Visual Studio 2026 Enterprise 18.11, MSVC 14.51, Windows SDKs 10.0.26100.0 and 10.0.28000.0.
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

They load the real `puttyengine.dll`, so a test run builds the native engine first. Note the MTP-mode syntax: `--solution`/`--project` flags, not positional paths.

NativeAOT publish: `dotnet publish src/PuTTY.App -c Release -r win-x64` writes `artifacts/publish/PuTTY.App/release_win-x64/PuTTY.App.exe` (about 11 MB plus the Windows App SDK files). A `PublishAot` project produces no `.exe` from plain `dotnet build`; to run a non-AOT build use `dotnet build src/PuTTY.App -c Release -p:PublishAot=false` (it overwrites the same output folder).

Checking the UI: start the exe, then use `winapp ui search "placeholder" -a PuTTY.App` and `winapp ui screenshot -a PuTTY.App --output artifacts/spike/shot.png`, then close it. Check content, not just chrome: under NativeAOT the window titles can render while the content is missing.

- Why Ninja, measured for PuTTY's libraries from scratch: Ninja inside a VS developer environment configures in 25 s and builds in 51 s; the Visual Studio generator took 102 s and 107 s.
- Always build out-of-tree into `artifacts/` (git-ignored). Never build inside `external/putty`. A bash `cd` into it persists across tool calls in this environment, so use absolute paths.
- `plink` in `external/putty/CMakeLists.txt` is the template for which libraries an SSH client links. `guiterminal` bundles root `terminal/`, `ldisc.c`, `config.c`, `dialog.c` **plus** the Windows GUI files (`windows/dialog.c`, `controls.c`, ...), so link order matters if our engine defines its own `dlg_*` functions. By default the engine links only `utils`. `build\native.cmd x64 full` links PuTTY's whole SSH client and is **expected to fail**, printing the unresolved externals (currently 45) that a front end still has to supply; `docs/front-end-contract.md` groups them and says how to handle each. Re-run it as a progress check: it should end at zero.
- Shell traps in Git Bash: it rewrites MSVC-style `/flags` into paths (`dumpbin /nologo` fails), so `export MSYS_NO_PATHCONV=1`; and `cmd | tail` hides a failing exit code (a native build once reported exit 0 while failing), so use `set -o pipefail` and check `${PIPESTATUS[0]}`.

## Constraints that are not obvious

1. **PuTTY's CMake must be the top-level project.** It uses `${CMAKE_SOURCE_DIR}` about 96 times (including `charset/` and `cmake/gitcommit.cmake`). Never `add_subdirectory(external/putty)` from our own CMake; build it as a separate configure (for example via `ExternalProject`) and link its static libraries.
2. **No C++ file can include PuTTY headers.** `ssh.h` uses `new` as a vtable member name (lines 757, 834, 890, 1097). Code that talks to PuTTY (Seat, TermWin, LogPolicy, key translation) must be C. C++ (the Direct2D/DirectWrite renderer) sits behind a C interface.
3. **Keep `external/putty` pristine** so it can track upstream. Hooks the UI needs must live in our code or a fork branch. Example: the sketch's session inspector needs key-exchange, cipher and host-key fingerprint, and `putty.h` has no accessor for them.
4. **The core is single-threaded with global state** (timer queue, callback queue, PRNG, handle-wait list). `windows/handle-wait.c` asserts at most 64 waitable handles, and `cleanup_exit` terminates the process. Both matter once many sessions share one process.
5. **Never compile PuTTY code with `NDEBUG`** (`defs.h` errors out; assertions are part of its security model). `native/CMakeLists.txt` strips `/DNDEBUG` from the engine's flags. PuTTY's libraries are always built Release (`/MD`), so the engine always uses `/MD` too, even in its Debug configuration (mixing `/MD` and `/MDd` fails to link).
6. **The engine must define the functions PuTTY expects from a front end.** Linking `utils` alone needed only `modalfatalbox` and `cleanup_exit` (both in `native/engine/frontend.c`); the whole SSH client needs 45 more (30 of them the `dlg_*` config-box functions), listed in `docs/front-end-contract.md`. Do not link PuTTY's `guiterminal` library to make them go away: it bundles PuTTY's Win32 dialog code and silently hides what the engine must implement. The `Seat`, `TermWin` and `LogPolicy` obligations are runtime vtables the linker cannot check.

## NativeAOT + WinUI 3 on .NET 11 (verified on 11.0.100-rc.3 with Windows App SDK 2.6.5)

`PuTTY.App` is unpackaged (`WindowsPackageType=None`, self-contained Windows App SDK) and package-free by design; MSIX packaging is a separate step with `winapp package` (issue `putty-modern-apl`). The settings below live in `src/PuTTY.App/PuTTY.App.csproj` and each has a comment there; do not remove them without re-testing a NativeAOT publish **and** launching the result.

- **`UseOptimizedPublish=false`** (WindowsAppSDK [#6897](https://github.com/microsoft/WindowsAppSDK/issues/6897)): on the .NET 11 SDK, `PublishAot` turns on optimized publish, and XAML compilation then fails with `WMC1007`. This hits unpackaged apps too, not only single-project MSIX. The issue is fixed but unreleased.
- **`Microsoft.Windows.SDK.BuildTools.MSIX` pinned to 1.7.261102100** (WindowsAppSDK [#6394](https://github.com/microsoft/WindowsAppSDK/issues/6394), fixed from 1.7.260930100): Windows App SDK 2.6.5 pulls 1.7.251221100 transitively, so the pin relies on `CentralPackageTransitivePinningEnabled` in `Directory.Packages.props`. The publish output has no loose `.xbf` files (XAML is in `PuTTY.App.pri`) and the app starts fine.
- **`AllowUnsafeBlocks`** is required (CsWinRT error CsWinRT1030) because the docking library's types implement generic WinRT interfaces.
- **`Digi21.WinUI.Docking` 1.6.0** (built against Windows App SDK 1.8) restores and builds on 2.6.5 and net11 with 0 warnings, and the NativeAOT publish has 0 warnings. **But under NativeAOT the content of docked windows is silently dropped** (titles, tabs and buttons render; the window contents do not exist in the visual tree). The regular build is unaffected. The workaround is `src/PuTTY.App/TrimmerRoots.xml`, which preserves the `DockSite` type (about +1.3 MB; rooting the whole assembly costs +4.5 MB). **The cause is not understood.** Bisected on this app: rooting any one of `DockSite`, `DockingWindowContainer`, `ToolWindowContainer` or `DocumentContainer` fixes it; rooting the window types, `DocumentHost` or `SplitContainer` does not; preserving only individual methods does not; the library has no `IL2xxx`/`IL3xxx` warnings under `IsAotCompatible`; and building the library with `AllowUnsafeBlocks` and `IsAotCompatible` does not change the symptom. Check for it after any docking or trimming change by searching the UI Automation tree for window content, not just the chrome.
- The full write-up and the table of what was tried are in `docs/nativeaot.md` on the fork branch `nativeaot-readiness` ([drolevar/DockingPanels](https://github.com/drolevar/DockingPanels/tree/nativeaot-readiness)), together with a small library change (`AllowUnsafeBlocks`). The owner asked that any improvement to the library be done in that fork; nothing has been sent to the upstream repository, so ask before opening an issue or pull request there. The root-cause hunt is tracked in the Beads follow-up to `putty-modern-8nd`.
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
```

- **Layers:** PuTTY core (C) -> engine DLL (C glue plus C++ renderer, flat C ABI) -> `PuTTY.Interop` -> WinUI 3 app. Interop uses `LibraryImport`, `[assembly: DisableRuntimeMarshalling]`, blittable signatures, `[UnmanagedCallersOnly]` callbacks and `SafeHandle`s. The engine starts as a DLL; static linking is a later option.
- **What gets replaced vs reused** (see the PuTTY map below): replace the `windows/` GUI group (`window.c`, `dialog.c`, `controls.c`, `config.c`, `putty.c`, `select-gui.c`, ...). Reuse the platform layer (`network.c`, `handle-io.c`, `storage.c`, `noise.c`, `serial.c`, `conpty.c`, `agent-client.c`, `sharing.c`, `gss.c`, `utils/`).
- **Threading:** all sessions on the UI thread. A hidden message window handles `WM_NETEVENT` and timers (`winselgui_set_hwnd`, `windows/utils/gui-timing.c`), and `request_callback_notifications` feeds the dispatcher queue. Replace `handle-wait.c` with thread-pool waits.
- **Terminal:** implement `TermWin` (24 methods) natively onto a swap chain, and `Seat` (about 29 methods; defaults in `stubs/null-seat.c`). Port key translation from `windows/window.c` (`TranslateKey`, lines 4150-4822).
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
