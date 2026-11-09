@echo off
rem ---------------------------------------------------------------------------
rem  run.bat - tau.exe を起動します。未ビルドなら build.bat を実行します。
rem ---------------------------------------------------------------------------
cd /d "%~dp0"

if not exist "tau.exe" (
    echo tau.exe がありません。ビルドします...
    call build.bat
    if errorlevel 1 exit /b 1
)

tau.exe %*
