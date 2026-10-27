#!/bin/sh
# check.sh - Linux / macOS / WSL 用の整合性チェック（check.bat の Linux 版）
#
#   AgentWeb.cs（サーバ）と index.html（UI）の整合性を検証し、
#   csc / mcs があれば C# コンパイル検証まで行います。
#   引数は check_consistency.py にそのまま渡します（--quiet, --no-compile）。

cd "$(dirname "$0")" || exit 1

PY=""
if command -v python3 >/dev/null 2>&1; then
    PY="python3"
elif command -v python >/dev/null 2>&1; then
    PY="python"
else
    echo "[ERROR] Python 3 が見つかりません。Python 3 をインストールしてください。" >&2
    exit 1
fi

exec "$PY" check_consistency.py "$@"
