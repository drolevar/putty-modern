cmake_minimum_required(VERSION 3.24)
project(puttyengine LANGUAGES C)

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

# PuTTY is always built Release (/MD, assertions stay on: PuTTY strips NDEBUG
# itself). The engine therefore always uses the /MD runtime too, even in its own
# Debug configuration; mixing /MD and /MDd objects fails to link (LNK2038).
set(PUTTY_CONFIG Release)
set(CMAKE_MSVC_RUNTIME_LIBRARY MultiThreadedDLL)

# PuTTY static libraries the engine links. Order matters for the linker; copy
# the order from the plink target in external/putty/CMakeLists.txt as this grows.
set(PUTTY_LIBS utils)

include(ExternalProject)

set(_putty_byproducts "")
foreach(lib IN LISTS PUTTY_LIBS)
  list(APPEND _putty_byproducts "${PUTTY_BINARY_DIR}/${PUTTY_CONFIG}/${lib}.lib")
endforeach()

set(_putty_generator_args CMAKE_GENERATOR "${CMAKE_GENERATOR}")
if(CMAKE_GENERATOR_PLATFORM)
  list(APPEND _putty_generator_args CMAKE_GENERATOR_PLATFORM "${CMAKE_GENERATOR_PLATFORM}")
endif()

ExternalProject_Add(putty_libs
  SOURCE_DIR "${PUTTY_SOURCE_DIR}"
  BINARY_DIR "${PUTTY_BINARY_DIR}"
  ${_putty_generator_args}
  INSTALL_COMMAND ""
  BUILD_COMMAND "${CMAKE_COMMAND}" --build <BINARY_DIR>
                --config ${PUTTY_CONFIG} --parallel --target ${PUTTY_LIBS}
  BUILD_BYPRODUCTS ${_putty_byproducts}
  BUILD_ALWAYS ON            # the inner build is incremental; this picks up submodule updates
  USES_TERMINAL_CONFIGURE ON
  USES_TERMINAL_BUILD ON)

foreach(lib IN LISTS PUTTY_LIBS)
  add_library(putty::${lib} STATIC IMPORTED GLOBAL)
  set_target_properties(putty::${lib} PROPERTIES
    IMPORTED_LOCATION "${PUTTY_BINARY_DIR}/${PUTTY_CONFIG}/${lib}.lib")
endforeach()

# ---------------------------------------------------------------------------
# Engine DLL: C glue that talks to PuTTY, behind a flat C ABI.
#
# PuTTY headers cannot be included from C++ (ssh.h uses "new" as a vtable
# member name), so everything that touches PuTTY types is C. C++ (the future
# Direct2D/DirectWrite renderer) must sit behind a C interface.
# ---------------------------------------------------------------------------
add_library(puttyengine SHARED
  engine/engine.c
  engine/frontend.c)

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

target_link_libraries(puttyengine PRIVATE
  putty::utils
  # PuTTY's platform_libraries on Windows
  advapi32 comdlg32 gdi32 imm32 ole32 shell32 user32 ws2_32 kernel32)
