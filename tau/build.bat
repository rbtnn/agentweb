@echo off
setlocal enabledelayedexpansion

rem ---------------------------------------------------------------------------
rem  build.bat - tau.exe を Windows 添付の csc.exe (C#5 / .NET Framework 4.5) で
rem  コンパイルします。外部ライブラリは使いません。
rem
rem  tau はコンソール上で動く AI エージェントです(Web UI はありません)。
rem  設定とセッションは agentweb と同じファイルを共有します:
rem    %APPDATA%\agentweb\agentweb.json
rem    %APPDATA%\agentweb\sessions\<id>.json
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
    ) do (
        if not defined CSC if exist "%%~D\csc.exe" set "CSC=%%~D\csc.exe"
    )
)

if not defined CSC (
    echo [ERROR] csc.exe が見つかりません。.NET Framework をインストールしてください。
    echo         または、手動で csc を PATH に追加してください。
    exit /b 1
)

echo コンパイル中: src\*.cs
"%CSC%" /nologo /target:exe /out:tau.exe /langversion:5 /optimize+ src\*.cs
if errorlevel 1 (
    echo.
    echo [ERROR] コンパイルに失敗しました。
    exit /b 1
)

echo.
echo ビルド完了: tau.exe
echo   実行: tau.exe
echo   ヘルプ: tau.exe --help
endlocal
