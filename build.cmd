@echo off
rem Build SnapLog (requires the .NET 10 SDK).
rem
rem ASCII only on purpose: cmd.exe parses batch files using the console's OEM code page,
rem and non-ASCII comments were breaking parsing on some machines
rem ("'tnet?setlocal' is not recognized as an internal or external command").
rem Keep this file ASCII.
setlocal

set "SCRIPT_DIR=%~dp0"
pushd "%SCRIPT_DIR%"

rem Two dotnet installs are common here: C:\Program Files\dotnet may have only the
rem runtime (no SDK), while the SDK lives in %USERPROFILE%\.dotnet. PATH usually puts
rem the former first, and then `dotnet build` fails with "No .NET SDKs were found".
rem Prefer the user-profile install when it actually has an SDK.
set "DOTNET_CMD=dotnet"
if exist "%USERPROFILE%\.dotnet\sdk" (
    set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
    set "PATH=%USERPROFILE%\.dotnet;%PATH%"
    set "DOTNET_CMD=%USERPROFILE%\.dotnet\dotnet.exe"
)

rem A running tray app may hold the exe we are about to overwrite: the copy step
rem then fails after ten retries with a confusing MSB3027. We do not refuse up front
rem because another copy (an extracted release, say) does not lock this output - the
rem friendly hint is only printed when the build actually failed while one is running.
rem (findstr rather than find: find is shadowed by Git for Windows' unix find
rem  when this script is run from a Git Bash environment.)

echo === build Release ===
"%DOTNET_CMD%" build SnapLog.sln -c Release
rem -2147450725 (SDK not found) is negative, and "if errorlevel 1" only means ">= 1",
rem so a failed build used to be reported as success. Compare against 0 instead.
if not "%errorlevel%"=="0" (
    tasklist /fi "imagename eq SnapLog.exe" 2>nul | findstr /i /c:"SnapLog.exe" >nul
    if not errorlevel 1 (
        echo [error] build failed while SnapLog is running. If the error above is MSB3027
        echo         ^(file locked by the running app^), exit SnapLog from the tray menu
        echo         and run build.cmd again.
    ) else (
        echo [error] build failed.
    )
    popd
    exit /b 1
)

rem run.cmd prefers publish\SnapLog.exe; a stale self-contained build there
rem would shadow this fresh one.
if exist "%SCRIPT_DIR%publish\SnapLog.exe" (
    echo [warn] publish\SnapLog.exe exists and is NOT refreshed by this script.
    echo        run.cmd starts that one first - re-run publish.cmd, or delete the publish folder.
)

echo.
echo Build finished:
echo   %SCRIPT_DIR%src\SnapLog\bin\Release\net10.0-windows10.0.19041.0\SnapLog.exe
echo.
echo Next steps:
echo   1) sanity check:      SnapLog.exe --selftest
echo   2) start the tray app: run.cmd
popd
endlocal
