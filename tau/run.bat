@echo off
rem ---------------------------------------------------------------------------
rem  run.bat - tau.exe を起動します。未ビルドなら build.bat を実行します。
rem ---------------------------------------------------------------------------
cd /d "%~dp0"

rem ソースが新しい場合もビルドし直す(古い tau.exe を使わないように)
set "NEED_BUILD="
if not exist "tau.exe" (
    set "NEED_BUILD=1"
) else (
    for /f %%A in ('dir /b /o-d "src\*.cs" "tau.exe" 2^>nul') do (
        if not defined FIRST (
            set "FIRST=%%A"
        ) else (
            echo %%~xA| findstr /i ".cs" >nul && set "NEED_BUILD=1"
        )
    )
)

if defined NEED_BUILD (
    echo ビルドします...
    call build.bat
    if errorlevel 1 exit /b 1
)

tau.exe %*
