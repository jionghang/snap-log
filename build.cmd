@echo off
rem 一键构建 SnapLog。需要 .NET 10 SDK。
rem 如果 dotnet 不在 PATH 里，会自动尝试 %USERPROFILE%\.dotnet。
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

echo === 构建 Release ===
dotnet build SnapLog.sln -c Release
if errorlevel 1 (
    echo [错误] 构建失败。
    popd
    exit /b 1
)

echo.
echo 构建完成，可执行文件：
echo   %SCRIPT_DIR%src\SnapLog\bin\Release\net10.0-windows10.0.19041.0\SnapLog.exe
echo.
echo 下一步：
echo   1) 跑一次自检确认抓取和 OCR 正常： SnapLog.exe --selftest
echo   2) 启动托盘应用：                    SnapLog.exe
popd
endlocal
