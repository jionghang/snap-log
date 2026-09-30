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

where dotnet >nul 2>nul
if errorlevel 1 (
    if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
        set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
        set "PATH=%USERPROFILE%\.dotnet;%PATH%"
    ) else (
        echo [error] dotnet not found. Install the .NET 10 SDK: https://dotnet.microsoft.com/download
        popd
        exit /b 1
    )
)

rem A running tray app may hold the exe we are about to overwrite: the copy step
rem then fails after ten retries with a confusing MSB3027. We do not refuse up front
rem because another copy (an extracted release, say) does not lock this output - the
rem friendly hint is only printed when the build actually failed while one is running.
rem (findstr rather than find: find is shadowed by Git for Windows' unix find
rem  when this script is run from a Git Bash environment.)

echo === build Release ===
dotnet build SnapLog.sln -c Release
if errorlevel 1 (
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
