@echo off
rem Start the SnapLog tray app (run build.cmd or publish.cmd first).
rem
rem Console mode: pass the command line straight through, e.g.
rem   run.cmd --selftest
rem   run.cmd --diagnose
rem   run.cmd --windows
rem
rem ASCII only on purpose: cmd.exe parses batch files using the console's OEM code page,
rem and non-ASCII comments were breaking parsing on some machines. Keep this file ASCII.
setlocal

set "SCRIPT_DIR=%~dp0"

rem Prefer the self-contained build: it does not depend on the .NET runtime being installed.
set "EXE=%SCRIPT_DIR%publish\SnapLog.exe"

if not exist "%EXE%" set "EXE=%SCRIPT_DIR%src\SnapLog\bin\Release\net10.0-windows10.0.19041.0\SnapLog.exe"
if not exist "%EXE%" set "EXE=%SCRIPT_DIR%src\SnapLog\bin\Debug\net10.0-windows10.0.19041.0\SnapLog.exe"

if not exist "%EXE%" (
    echo [error] nothing built yet. Run build.cmd first.
    exit /b 1
)

rem The development build needs the runtime on PATH.
if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
    set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
    set "PATH=%USERPROFILE%\.dotnet;%PATH%"
)

rem Say which build is being launched: a stale exe elsewhere is the classic
rem "why is it still the old version" confusion.
for %%i in ("%EXE%") do echo [info] launching %%~fi ^(built %%~ti^)

rem With arguments: stay in this console so the output is visible.
rem Without arguments: start the tray app in the background.
if "%~1"=="" (
    start "" "%EXE%"
) else (
    "%EXE%" %*
)
endlocal
