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

rem While the tray app is running it holds SnapLog.exe, so the copy step fails
rem after ten retries with a confusing MSB3027. Say it plainly instead.
rem (findstr rather than find: find is shadowed by Git for Windows' unix find
rem  when this script is run from a Git Bash environment.)
tasklist /fi "imagename eq SnapLog.exe" 2>nul | findstr /i /c:"SnapLog.exe" >nul
if not errorlevel 1 (
    echo [error] SnapLog is running. Exit it from the tray menu, then run build.cmd again.
    popd
    exit /b 1
)

echo === build Release ===
dotnet build SnapLog.sln -c Release
if errorlevel 1 (
    echo [error] build failed.
    popd
    exit /b 1
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
