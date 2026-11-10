@echo off
setlocal
rem Builds the native engine (puttyengine.dll) with CMake + Ninja inside a Visual Studio
rem developer environment. Used by src\PuTTY.Native (so dotnet build covers native too) and
rem runnable by hand:
rem
rem   build\native.cmd [x64]          the engine (output: artifacts\native\x64\puttyengine.dll)
rem   build\native.cmd x64 full       link PuTTY's whole SSH client; EXPECTED TO FAIL, and the
rem                                   unresolved externals it prints are the to-do list
rem                                   (see docs\front-end-contract.md)
rem
rem Visual Studio supplies cl.exe, CMake and Ninja; nothing needs to be on PATH.

set "ARCH=%~1"
if "%ARCH%"=="" set "ARCH=x64"
if /i not "%ARCH%"=="x64" goto :unsupported

set "PRESET=%ARCH%"
if /i "%~2"=="full" set "PRESET=%ARCH%-full"

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" goto :novs
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSROOT=%%i"
if not defined VSROOT goto :novs

call "%VSROOT%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
set "CMAKEDIR=%VSROOT%\Common7\IDE\CommonExtensions\Microsoft\CMake"
set "PATH=%CMAKEDIR%\CMake\bin;%CMAKEDIR%\Ninja;%PATH%"

cd /d "%~dp0..\native"
if errorlevel 1 exit /b 1
cmake --preset %PRESET%
if errorlevel 1 exit /b 1
cmake --build --preset %PRESET%
exit /b %errorlevel%

:unsupported
echo error: unsupported architecture "%ARCH%" (only x64 is set up; the ARM64 MSVC tools are not installed) 1>&2
exit /b 2

:novs
echo error: Visual Studio with the C++ x64 build tools was not found (needed for cl.exe, CMake and Ninja) 1>&2
exit /b 1
