@echo off
setlocal enabledelayedexpansion
rem  AgentWeb.cs(サーバ) と index.html(UI) の整合性チェック
cd /d "%~dp0"

set "PY="
where py >nul 2>nul
if !errorlevel! equ 0 (
    py -3 -c "import sys" >nul 2>nul
    if !errorlevel! equ 0 set "PY=py -3"
)
if not defined PY (
    where python >nul 2>nul
    if !errorlevel! equ 0 (
        python -c "import sys" >nul 2>nul
        if !errorlevel! equ 0 set "PY=python"
    )
)
if not defined PY (
    echo [ERROR] Python が見つかりません。Python 3 をインストールしてください。
    exit /b 1
)

!PY! check_consistency.py
exit /b !errorlevel!
