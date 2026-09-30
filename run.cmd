@echo off
rem 启动 SnapLog 托盘应用（需要先跑 build.cmd 或 publish.cmd）。
rem 命令行模式直接把参数传进来即可，例如：
rem   run.cmd --selftest
rem   run.cmd --diagnose
rem   run.cmd --windows
setlocal

set "SCRIPT_DIR=%~dp0"

rem 优先用自包含版本，它不依赖机器上有没有装 .NET
set "EXE=%SCRIPT_DIR%publish\SnapLog.exe"

if not exist "%EXE%" set "EXE=%SCRIPT_DIR%src\SnapLog\bin\Release\net10.0-windows10.0.19041.0\SnapLog.exe"
if not exist "%EXE%" set "EXE=%SCRIPT_DIR%src\SnapLog\bin\Debug\net10.0-windows10.0.19041.0\SnapLog.exe"

if not exist "%EXE%" (
    echo [错误] 还没有构建。请先运行 build.cmd
    exit /b 1
)

rem 开发版（非自包含）需要能找到运行时
if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
    set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
    set "PATH=%USERPROFILE%\.dotnet;%PATH%"
)

rem 有命令行参数时留在当前控制台看输出，无参数则后台启动托盘
if "%~1"=="" (
    start "" "%EXE%"
) else (
    "%EXE%" %*
)
endlocal
