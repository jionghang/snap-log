@echo off
rem Publish a self-contained build: publish\SnapLog.exe runs without the .NET runtime
rem installed. Cost: the folder is roughly 140 MB.
rem
rem ASCII only on purpose: cmd.exe parses batch files using the console's OEM code page,
rem and non-ASCII comments were breaking parsing on some machines. Keep this file ASCII.
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

echo === publish self-contained build to publish\ ===
"%DOTNET_CMD%" publish src\SnapLog\SnapLog.csproj -c Release -r win-x64 --self-contained true -o publish
if not "%errorlevel%"=="0" (
    echo [error] publish failed.
    popd
    exit /b 1
)

echo.
echo Done. Run it directly, or copy the whole publish folder to another machine:
echo   %SCRIPT_DIR%publish\SnapLog.exe
popd
endlocal
