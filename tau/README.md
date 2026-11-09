# tau — コンソール上で動く AI エージェント

`tau` は **コンソール（ターミナル）だけで動く AI エージェント**です。
同じリポジトリにある `agentweb`（Web UI 版）とは**独立した実装**で、コードの共通化はしていません
（`agentweb` のソースを参考に、必要な部分を単一ファイルに書き起こしています）。

HTTP サーバも Web UI も持たず、標準入出力だけで対話します。
**設定ファイルとセッションは `agentweb` と共有**するため、`agentweb` で続けた会話を
`tau` で開いて続きから対話できます（逆も同じです）。

---

## ビルドと実行（Windows）

```
cd tau
build.bat          … tau.exe をビルド（Windows 添付の csc.exe / 外部ライブラリ不要）
run.bat            … 起動（未ビルドなら自動でビルド）
tau.exe            … 直接起動
```

`build.bat` は `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` を探して
`/langversion:5` でコンパイルします。追加のインストールは不要です。

Linux / macOS では `mono` の `mcs` でビルドできます。

```
mcs -langversion:5 -out:tau.exe AgentConsole.cs
mono tau.exe
```

---

## 使い方

```
tau.exe [オプション]

  --config <path>    設定ファイル（agentweb.json）のパス
  --session <id>     既存セッションを開いて続きから対話する
  --list             保存済みセッションの一覧を表示して終了
  --help, -h, /?     ヘルプを表示
```

起動すると、そのまま入力を受け付けます。**前回開いていたセッションが自動で復元**され、
過去の会話が表示された状態から続きを入力できます。

```
> ファイル一覧を見せて
  [思考] まず一覧を見よう。
  ▶ ツール実行: list_dir  .

一覧を取得しました。完了です。
```

### 対話中のコマンド

| コマンド | 説明 |
|---|---|
| `/help` | ヘルプを表示 |
| `/new` | 新しいセッションを開始 |
| `/sessions` | 保存済みセッションの一覧 |
| `/open <id>` | 指定したセッションを開く |
| `/model` | 現在のモデルと登録一覧を表示 |
| `/model <名>` | モデルを切り替え（設定ファイルにも保存） |
| `/tools` | ツール実行の on/off を切り替え |
| `/clear` | 画面を消す |
| `/exit`, `/quit` | 終了（`Ctrl+D` でも終了） |

`Ctrl+C` で生成中の応答を中断できます。

---

## agentweb と同じファイルを参照する

`tau` は**独自の設定ファイルやデータディレクトリを持ちません**。
`agentweb` とまったく同じ場所・同じ形式のファイルを読み書きします。

| 種類 | パス |
|---|---|
| 設定 | `%APPDATA%\agentweb\agentweb.json` |
| セッション | `%APPDATA%\agentweb\sessions\<id>.json` |
| 最後に開いたセッション / 下書き | `%APPDATA%\agentweb\sessions\_state.json` |

Linux / macOS では `~/.config/agentweb/` が使われます
（`agentweb` の `AppDataDir()` と同じ解決方法）。

### 共有されるもの

- **API 接続先・API キー・モデル・モデル一覧**（`llmApiUrl` / `llmApiKey` / `model` / `models`）
- **ツール実行の可否**（`allowTools`）
- **コンテキスト上限・要約の設定**（`contextWindow` / `reserveTokens` / `keepRecentTokens`）
- **プロキシ設定**（`proxyUrl` / `proxyUser` / `proxyPass` / `useSystemProxy`）
- **システムプロンプト**（`systemPrompt`）
- **会話履歴**（`messages`。`ts` / `steps` / `think` も同じ形式で保存）

そのため、**`agentweb` で作った会話を `tau` で開いて続きから対話**でき、
`tau` で作った会話を `agentweb` の画面で開くこともできます。
`/model` での切り替えは `agentweb.json` に保存されるので、次回どちらのプログラムからでも同じモデルが使われます。

### 環境変数

`agentweb` と同じ名前の環境変数が使えます（設定ファイルがある場合はそちらが優先）。

```
LLM_API_URL / LLM_API_KEY / LLM_MODEL
ALLOW_TOOLS / LLM_EMIT_REASONING
MAX_TOOLS_PER_TURN / TOOL_TIMEOUT_SEC
LLM_CONTEXT_WINDOW / LLM_RESERVE_TOKENS / LLM_KEEP_RECENT_TOKENS
LLM_PROXY_URL / LLM_PROXY_USERNAME / LLM_PROXY_PASSWORD / LLM_USE_SYSTEM_PROXY
AI_SYSTEM_PROMPT
AGENTWEB_CONFIG
```

`agentweb.json` が無くても、環境変数だけで動作します。

---

## 設定項目

`agentweb` と同じスキーマです。`tau` が使う項目は次のとおりです。

```json
{
  "llmApiUrl": "https://api.openai.com/v1/chat/completions",
  "llmApiKey": "YOUR_API_KEY",
  "model": "gpt-4o-mini",
  "models": ["gpt-4o-mini", "gpt-4o"],
  "allowTools": true,
  "emitReasoning": true,
  "maxToolsPerTurn": 16,
  "toolTimeoutSec": 30,
  "contextWindow": 64000,
  "reserveTokens": 12000,
  "keepRecentTokens": 20000,
  "proxyUrl": "",
  "proxyUser": "",
  "proxyPass": "",
  "useSystemProxy": true,
  "systemPrompt": ""
}
```

- `models` は**モデル名の一覧**です。接続先・API キー・コンテキスト上限は全モデル共通の 1 つを使います。
- `agentweb` 側の `port` / `theme` / `bindPath` / `openBrowserOnStart` は `tau` では使いません
  （読み込んでも無視され、保存時も変更しません）。

---

## 機能

### ストリーミング表示

応答はトークン単位でそのまま表示されます。`emitReasoning` が有効なら
**思考（reasoning_content）** も `[思考]` として表示します。

ANSI エスケープが使える環境（Windows 10 以降のコンソール、Unix 系のターミナル）では色付きで表示し、
リダイレクト時や `NO_COLOR` 指定時は素のテキストになります。

### ツール実行（Function Calling）

`agentweb` と同じ 5 つのツールが使えます。

| ツール名 | 説明 | 引数 |
|---|---|---|
| `shell` | コマンドを実行（Windows は `cmd.exe`、Unix 系は `/bin/sh`） | `cmd` |
| `read_file` | ファイル内容を読み取る | `path` |
| `write_file` | ファイルへ書き込む | `path`, `content` |
| `list_dir` | ディレクトリ一覧を返す | `path`, `pattern`? |
| `curl` | HTTP/HTTPS リクエスト送信（プロキシ設定を反映） | `url`, `method`?, `headers`?, `body`? |

ツール実行は `▶ ツール実行: <name>  <detail>` として 1 行で表示され、
実行記録はセッションの `steps` に保存されます（`agentweb` の画面でも同じように表示されます）。

### コンテキストの自動整理

会話が `contextWindow - reserveTokens` を超えると、古い部分を LLM で要約し、
`summary` と `contextStart` を更新します（`agentweb` と同じ方式）。
要約はセッションに保存されるため、再開しても引き継がれます。

---

## テスト

`test_tau.py` がモック LLM を立てて `tau.exe` を実際に起動し、実行時の挙動を検証します
（`mono` と `mcs` が必要）。

```
python3 test_tau.py
```

| 検証 | 内容 |
|---|---|
| ストリーミング | 思考と本文が表示されるか |
| ツールループ | ツール実行 → 結果を LLM へ返して再問い合わせするか |
| 設定の共有 | `agentweb.json` を参照しているか |
| セッション形式 | `agentweb` と同じキー・同じ場所に保存されるか |
| `think` / `steps` | 保存され、再開時に復元されるか |
| `_state.json` | 最後のセッションが記録されるか |
| 再開 | `--session` で続きから開けるか（LLM を呼ばないか） |
| コマンド | `/model` `/tools` が効き、設定に保存されるか |
| エラー | API エラーで落ちずに表示して続行するか |

---

## ファイル構成

```
tau/
  AgentConsole.cs   本体（単一ファイル・外部ライブラリ不要）
  build.bat         ビルド（Windows / CP932・CRLF）
  run.bat           起動
  test_tau.py       実行時回帰テスト
  README.md         このファイル
```

`agentweb/`（`AgentWeb.cs` / `index.html` など）は**一切変更していません**。
