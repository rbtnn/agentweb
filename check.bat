@echo off
setlocal
rem  AgentWeb.cs(サーバ) と index.html(UI) の整合性チェック
cd /d "%~dp0"
where python >nul 2>nul
if %errorlevel% neq 0 (
    echo [ERROR] python が見つかりません。Python 3 をインストールしてください。
    exit /b 1
)
python check_consistency.py
exit /b %errorlevel%
