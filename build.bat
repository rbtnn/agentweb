@echo off
setlocal enabledelayedexpansion

rem ---------------------------------------------------------------------------
rem  build.bat - Windows 同梱の csc.exe (C#5 / .NET Framework 4.5) でこの
rem  AI Web サーバをコンパイルします。外部ライブラリは一切使いません。
rem
rem  Web UI は AgentWeb.cs に組み込み済みなので、別ファイルは不要です。
rem  UI を変更した場合は:   python embed_ui.py  を実行してから再ビルドして下さい。
rem
rem  csc は PATH にあれば使用し、無ければ .NET Framework 標準ディレクトリを
rem  自動検索します。
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
    echo [ERROR] csc.exe が見つかりません。64bit .NET Framework をインストールして下さい。
    echo         または、次のように手動で csc を PATH に追加して下さい。
    echo         set PATH=C:\Windows\Microsoft.NET\Framework64\v4.0.30319;%%PATH%%
    exit /b 1
)

echo [0/1] Using csc: %CSC%
echo [1/1] Compiling AgentWeb.cs (C#5 / .NET 4.5, GUI/winexe + TRAY) ...
"%CSC%" /nologo /optimize+ /target:winexe /define:TRAY /out:aw.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll AgentWeb.cs
if %errorlevel% neq 0 (
    echo [ERROR] コンパイルに失敗しました。
    exit /b 1
)

echo.
echo [OK] aw.exe を生成しました(タスクトレイ常駐UI)。次に run.bat を実行してください。
endlocal
