@echo off
rem Publish a self-contained build: publish\SnapLog.exe runs without the .NET runtime
rem installed. Cost: the folder is roughly 140 MB.
rem
rem ASCII only on purpose: cmd.exe parses batch files using the console's OEM code page,
rem and non-ASCII comments were breaking parsing on some machines. Keep this file ASCII.
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

echo === publish self-contained build to publish\ ===
dotnet publish src\SnapLog\SnapLog.csproj -c Release -r win-x64 --self-contained true -o publish
if errorlevel 1 (
    echo [error] publish failed.
    popd
    exit /b 1
)

echo.
echo Done. Run it directly, or copy the whole publish folder to another machine:
echo   %SCRIPT_DIR%publish\SnapLog.exe
popd
endlocal
