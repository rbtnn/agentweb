@echo off
setlocal enabledelayedexpansion

rem ---------------------------------------------------------------------------
rem  build.bat - Windows 添付の csc.exe (C#5 / .NET Framework 4.5) で
rem  AI Web サーバをコンパイルします（コンソール版）。外部ライブラリは使いません。
rem
rem  Web UI(index.html)は外部ファイルとして配信します(埋め込みなし)。
rem  aw.exe と同じフォルダに index.html を置いてください(編集後は再ビルド不要)。
rem
rem  csc が PATH にあれば使用し、無ければ .NET Framework 標準フォルダから探します。
rem ---------------------------------------------------------------------------

set "CSC="
where csc >nul 2>nul
if %errorlevel% equ 0 (
    set "CSC=csc"
) else (
    for %%D in (
        "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
        "%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
        "%WINDIR%\Microsoft.NET\Framework64\v3.5"
        "%WINDIR%\Microsoft.NET\Framework\v3.5"
        "%WINDIR%\Microsoft.NET\Framework64\v2.0.50727"
        "%WINDIR%\Microsoft.NET\Framework\v2.0.50727"
    ) do (
        if not defined CSC if exist "%%~D\csc.exe" set "CSC=%%~D\csc.exe"
    )
)

if not defined CSC (
    echo [ERROR] csc.exe が見つかりません。64bit .NET Framework をインストールしてください。
    echo         または、手動で csc を PATH に追加してください。
    echo         set PATH=C:\Windows\Microsoft.NET\Framework64\v4.0.30319;%%PATH%%
    exit /b 1
)

rem --- AgentWeb.cs と index.html の整合性チェック (Python が使える場合のみ) ---
rem   Python が無い / 実行できない(Store 版のスタブ等)場合はスキップしてビルドを続行します。
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
if defined PY (
    !PY! check_consistency.py
    if !errorlevel! neq 0 (
        echo [ERROR] AgentWeb.cs と index.html の整合性チェックに失敗しました。
        exit /b 1
    )
) else (
    echo [WARN] Python が見つからないため整合性チェックをスキップします。
)

echo [0/1] Using csc: %CSC%
echo [1/1] Compiling AgentWeb.cs (C#5 / .NET 4.5, console/exe) ...
"%CSC%" /nologo /optimize+ /target:exe /out:aw.exe AgentWeb.cs
if %errorlevel% neq 0 (
    echo [ERROR] コンパイルに失敗しました。
    exit /b 1
)

echo.
echo [OK] aw.exe を生成しました(コンソール版)。aw.exe または run.bat で起動してください。
endlocal
