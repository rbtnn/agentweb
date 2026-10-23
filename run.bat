@echo off
rem  コンソール版 aw.exe を起動します。
cd /d "%~dp0"
if not exist "aw.exe" (
    echo [ERROR] aw.exe がありません。先に build.bat を実行してください。
    pause
    exit /b 1
)
aw.exe %*
pause
