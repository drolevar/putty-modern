cmake_minimum_required(VERSION 3.24)
project(puttyengine LANGUAGES C CXX)

# Single-config generators only (Ninja, see CMakePresets.json): PuTTY's libraries are
# located by a flat path below, which a multi-config generator would split per config.
if(CMAKE_CONFIGURATION_TYPES)
  message(FATAL_ERROR "Use a single-config generator (Ninja). Run build/native.cmd.")
endif()

# PuTTY refuses to compile with NDEBUG defined (external/putty/defs.h): its
# assertions are part of its security model. CMake's default Release flags add
# /DNDEBUG, so strip it from every optimised configuration, as PuTTY's own
# cmake/setup.cmake does.
foreach(cfg RELEASE RELWITHDEBINFO MINSIZEREL)
  string(REPLACE "/DNDEBUG" "" CMAKE_C_FLAGS_${cfg} "${CMAKE_C_FLAGS_${cfg}}")
  string(REPLACE "-DNDEBUG" "" CMAKE_C_FLAGS_${cfg} "${CMAKE_C_FLAGS_${cfg}}")
endforeach()

# ---------------------------------------------------------------------------
# PuTTY core
#
# PuTTY's own CMake assumes it is the top-level project (it uses
# CMAKE_SOURCE_DIR about 96 times), so it cannot be add_subdirectory()'d from
# here. Build it as an independent configure + build and link its static
# libraries. external/putty is a pristine mirror: never patch it from here.
# ---------------------------------------------------------------------------
cmake_path(SET PUTTY_SOURCE_DIR NORMALIZE "${CMAKE_CURRENT_LIST_DIR}/../external/putty")
set(PUTTY_BINARY_DIR "${CMAKE_BINARY_DIR}/putty")
set(PUTTY_LIB_DIR "${PUTTY_BINARY_DIR}/lib")      # every PuTTY static library lands here

# PuTTY is always built Release (/MD, assertions stay on: PuTTY strips NDEBUG
# itself). The engine therefore always uses the /MD runtime too, even if it is
# configured Debug; mixing /MD and /MDd objects fails to link (LNK2038).
set(CMAKE_MSVC_RUNTIME_LIBRARY MultiThreadedDLL)

# PuTTY static libraries the engine links (the plink target in
# external/putty/CMakeLists.txt shows what an SSH client needs). guimisc comes first: it
# supplies the socket event handling for a message-loop GUI (WSAAsyncSelect), and the
# linker takes the first definition it meets, so it wins over eventloop's console-style
# version of the same functions.
#
# PuTTY's guiterminal library is deliberately NOT used: it bundles the Win32 dialog code
# (windows/dialog.c, controls.c, jump-list.c, ...) alongside the portable terminal code,
# and the linker would silently satisfy the dlg_* functions from it. The portable files
# are compiled directly instead (PUTTY_CORE_SOURCES below).
set(PUTTY_LIBS guimisc eventloop sshclient otherbackends settings network keygen crypto utils)

include(ExternalProject)

set(_putty_byproducts "")
foreach(lib IN LISTS PUTTY_LIBS)
  list(APPEND _putty_byproducts "${PUTTY_LIB_DIR}/${lib}.lib")
endforeach()

ExternalProject_Add(putty_libs
  SOURCE_DIR "${PUTTY_SOURCE_DIR}"
  BINARY_DIR "${PUTTY_BINARY_DIR}"
  CMAKE_GENERATOR "${CMAKE_GENERATOR}"
  CMAKE_ARGS
    -DCMAKE_BUILD_TYPE=Release
    -DCMAKE_ARCHIVE_OUTPUT_DIRECTORY=${PUTTY_LIB_DIR}
    # Debug information inside the objects (/Z7, no per-library .pdb to keep around), so that the engine's own
    # puttyengine.pdb names PuTTY's functions in a crash dump. _INIT keeps the generator's /DWIN32 /D_WINDOWS.
    -DCMAKE_C_FLAGS_INIT=/Z7
  INSTALL_COMMAND ""
  BUILD_COMMAND "${CMAKE_COMMAND}" --build <BINARY_DIR> --target ${PUTTY_LIBS}
  BUILD_BYPRODUCTS ${_putty_byproducts}
  BUILD_ALWAYS ON            # the inner build is incremental; this picks up submodule updates
  USES_TERMINAL_CONFIGURE ON
  USES_TERMINAL_BUILD ON)

foreach(lib IN LISTS PUTTY_LIBS)
  add_library(putty::${lib} STATIC IMPORTED GLOBAL)
  set_target_properties(putty::${lib} PROPERTIES
    IMPORTED_LOCATION "${PUTTY_LIB_DIR}/${lib}.lib")
endforeach()

# ---------------------------------------------------------------------------
# Engine DLL: C glue that talks to PuTTY, behind a flat C ABI.
#
# PuTTY headers cannot be included from C++ (ssh.h uses "new" as a vtable
# member name), so everything that touches PuTTY types is C. C++ (the future
# Direct2D/DirectWrite renderer) must sit behind a C interface.
# ---------------------------------------------------------------------------

# PuTTY source files compiled directly into the engine (paths relative to external/putty).
set(PUTTY_CORE_SOURCES
  be_list.c                        # how a program picks its backends (SSH, serial, the others)
  terminal/terminal.c terminal/bidi.c terminal/lineedit.c   # the emulator
  ldisc.c                          # line discipline
  windows/unicode.c                # init_ucs (character sets), check_compose, the keyboard-layout helpers
  stubs/no-printing.c              # PuTTY's own stub: printing is out of scope
  windows/no-jump-list.c           # PuTTY's own stub: no taskbar jump list yet
  config.c dialog.c)               # the settings dialog's description and its control tree (engine/configbox.c is the front end)

set(ENGINE_SOURCES engine/engine.c engine/frontend.c engine/eventloop.c engine/session.c engine/keys.c engine/termwin.c engine/render.cpp engine/diag_keys.c engine/text.c engine/configbox.c engine/core_refs.c)

# Not stubs/no-ca-config.c: that is for programs WITHOUT SSH code; ours links ssh/ca-config.c,
# which provides the real has_ca_config_box and setup_ca_config_box.

list(TRANSFORM PUTTY_CORE_SOURCES PREPEND "${PUTTY_SOURCE_DIR}/")
list(APPEND ENGINE_SOURCES ${PUTTY_CORE_SOURCES})
set_source_files_properties("${PUTTY_SOURCE_DIR}/be_list.c" PROPERTIES
  COMPILE_DEFINITIONS "SSH=1;SERIAL=1;OTHERBACKENDS=1;APPNAME=PuTTY")

add_library(puttyengine SHARED ${ENGINE_SOURCES})

add_dependencies(puttyengine putty_libs)

target_include_directories(puttyengine PRIVATE
  engine/include
  "${PUTTY_SOURCE_DIR}"
  "${PUTTY_SOURCE_DIR}/windows"
  "${PUTTY_SOURCE_DIR}/terminal"
  "${PUTTY_BINARY_DIR}/CMakeFiles")   # generated cmake.h / licence.h

# Same definitions PuTTY's own build applies (cmake/platforms/windows.cmake).
# An implicit function declaration (C4013) in C means "returns int": it truncated a 64-bit pointer once and
# crashed. Never allow it.
if(MSVC)
  target_compile_options(puttyengine PRIVATE /we4013 /Z7)
  # puttyengine.pdb next to the DLL, in every configuration: a crash in the field (or in the Sandbox) is only
  # readable with it. /DEBUG turns off the linker's size optimisations, so ask for them again.
  target_link_options(puttyengine PRIVATE /DEBUG /OPT:REF /OPT:ICF)
endif()

target_compile_definitions(puttyengine PRIVATE
  PUTTY_ENGINE_BUILD
  HAVE_CMAKE_H
  _WINDOWS
  _CRT_SECURE_NO_WARNINGS
  _WINSOCK_DEPRECATED_NO_WARNINGS
  _ARM_WINAPI_PARTITION_DESKTOP_SDK_AVAILABLE)

list(TRANSFORM PUTTY_LIBS PREPEND "putty::" OUTPUT_VARIABLE _putty_link_targets)
target_link_libraries(puttyengine PRIVATE
  ${_putty_link_targets}
  # PuTTY's platform_libraries on Windows
  advapi32 comdlg32 gdi32 imm32 ole32 shell32 user32 ws2_32 kernel32
  # the bell sound file (PlaySoundW)
  winmm
  # the terminal renderer (render.cpp)
  d2d1 dwrite dxgi d3d11 windowscodecs uuid)
