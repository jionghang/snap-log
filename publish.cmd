@echo off
rem 发布成"自包含"版本：publish 目录里的 SnapLog.exe 直接双击就能跑，
rem 目标机器不需要安装 .NET 运行时。代价是目录约 140MB。
setlocal

set "SCRIPT_DIR=%~dp0"
pushd "%SCRIPT_DIR%"

where dotnet >nul 2>nul
if errorlevel 1 (
    if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
        set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
        set "PATH=%USERPROFILE%\.dotnet;%PATH%"
    ) else (
        echo [错误] 找不到 dotnet。请先安装 .NET 10 SDK： https://dotnet.microsoft.com/download
        popd
        exit /b 1
    )
)

echo === 发布自包含版本到 publish\ ===
dotnet publish src\SnapLog\SnapLog.csproj -c Release -r win-x64 --self-contained true -o publish
if errorlevel 1 (
    echo [错误] 发布失败。
    popd
    exit /b 1
)

echo.
echo 完成。可直接运行或整个 publish 目录拷到别的机器：
echo   %SCRIPT_DIR%publish\SnapLog.exe
popd
endlocal
