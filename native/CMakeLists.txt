cmake_minimum_required(VERSION 3.24)
project(puttyengine LANGUAGES C)

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

option(PUTTY_ENGINE_FULL
  "Link PuTTY's SSH client, backends and terminal libraries. EXPECTED TO FAIL TO LINK until the front-end contract is implemented: the unresolved externals are the to-do list (see docs/front-end-contract.md)."
  OFF)

# PuTTY static libraries the engine links (the plink target in
# external/putty/CMakeLists.txt shows what an SSH client needs). guimisc comes first: it
# supplies the socket event handling for a message-loop GUI (WSAAsyncSelect), and the
# linker takes the first definition it meets, so it wins over eventloop's console-style
# version of the same functions.
#
# PuTTY's guiterminal library is deliberately NOT used: it bundles the Win32 dialog code
# (windows/dialog.c, controls.c, jump-list.c, ...) alongside the portable terminal code,
# and the linker would silently satisfy the dlg_* functions from it. The portable files
# are compiled directly instead (PUTTY_TERMINAL_SOURCES below).
if(PUTTY_ENGINE_FULL)
  set(PUTTY_LIBS guimisc eventloop sshclient otherbackends settings network crypto utils)
else()
  set(PUTTY_LIBS utils)
endif()

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
set(ENGINE_SOURCES engine/engine.c engine/frontend.c)
if(PUTTY_ENGINE_FULL)
  # be_list.c is how every PuTTY program picks its backends: SSH, serial and the others.
  set(PUTTY_TERMINAL_SOURCES
    terminal/terminal.c terminal/bidi.c terminal/lineedit.c   # the emulator
    ldisc.c                                                   # line discipline
    config.c dialog.c)                                        # config-box description (platform-neutral)
  list(TRANSFORM PUTTY_TERMINAL_SOURCES PREPEND "${PUTTY_SOURCE_DIR}/")
  list(APPEND ENGINE_SOURCES engine/core_refs.c "${PUTTY_SOURCE_DIR}/be_list.c" ${PUTTY_TERMINAL_SOURCES})
  set_source_files_properties("${PUTTY_SOURCE_DIR}/be_list.c" PROPERTIES
    COMPILE_DEFINITIONS "SSH=1;SERIAL=1;OTHERBACKENDS=1;APPNAME=PuTTY")
endif()

add_library(puttyengine SHARED ${ENGINE_SOURCES})

add_dependencies(puttyengine putty_libs)

target_include_directories(puttyengine PRIVATE
  engine/include
  "${PUTTY_SOURCE_DIR}"
  "${PUTTY_SOURCE_DIR}/windows"
  "${PUTTY_SOURCE_DIR}/terminal"
  "${PUTTY_BINARY_DIR}/CMakeFiles")   # generated cmake.h / licence.h

# Same definitions PuTTY's own build applies (cmake/platforms/windows.cmake).
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
  advapi32 comdlg32 gdi32 imm32 ole32 shell32 user32 ws2_32 kernel32)
