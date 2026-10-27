# agentweb

Windows に同梱の **`csc.exe`（C# 5 / .NET Framework 4.5）** でコンパイルできる、
**自己完結型のローカル Web UI を備えた AI エージェント**です。

- 組込み HTTP サーバ（`http://localhost:8000/`）+ Web UI
- OpenAI 互換 LLM API 連携 + ツール実行（shell / read_file / write_file / list_dir / curl）
- **コンソールアプリ**として常駐起動（`aw.exe` を実行するだけ）、スタートアップ登録も可能
- セッション管理（AppData 永続化・サイドバー）と pi 方式のコンパクション
- 各メッセージ右上の**コピーボタン**で内容をクリップボードへコピー（普段は非表示・ホバーで表示）
- 応答待ちの間は**3点ドットのアニメーション**を表示（thinking 非対応モデルでも動作中が分かる）
- 社内プロキシ対応 / ダーク・ライトテーマ / Mermaid 描画

## 構成

| ファイル | 役割 |
|---|---|
| `AgentWeb.cs` | 本体（単一ソース）。HTTP サーバ + LLM クライアント + JSON エンジン |
| `index.html` | Web UI（外部ファイル。`GET /` で直接配信。編集後は再ビルド不要） |
| `agentweb.json.example` | 設定ファイルのサンプル（実際の設定は `agentweb.json` にコピー） |
| `build.bat` | `csc.exe` で **コンソール版** `aw.exe` をビルド（CP932/CRLF） |
| `run.bat` | ビルド済み `aw.exe`（コンソール版）を起動する補助スクリプト |
| `check_consistency.py` | `AgentWeb.cs`（サーバ）と `index.html`（UI）の整合性チェック |
| `check.bat` | 上記チェックを実行する補助スクリプト（Windows） |
| `README.md` | 本ドキュメント |

> `build.bat` は **DOS(CRLF) + CP932(Shift-JIS)**、`AgentWeb.cs` は **UTF-8(BOM付き)** です。
> `index.html` は **`aw.exe` と同じフォルダ**に置いてください（`bindPath` で変更可能）。

### 外部依存ゼロの内訳
- **HTTP サーバ** … `System.Net.Sockets.TcpListener` による独自実装
  （`HttpListener` を使わないため **管理者権限 / `netsh urlacl` / IIS 不要**）
- **LLM API クライアント** … `System.Net.HttpWebRequest`
- **JSON** … 自前実装（追加アセンブリ不使用）

> 追加アセンブリ参照は**一切なし**（BCL のみ）。したがって `System.Windows.Forms` 等は使いません。

## ビルド手順（Windows / csc.exe）

```bat
cd agentweb
build.bat
:: （csc は自動検索。コンソール版 aw.exe を生成）
:: 生成後は aw.exe（または run.bat）で起動します
```

## 設定（agentweb.json）

設定ファイルは **セッションと同じ場所** に置かれます（無ければ環境変数/既定値）。

```
%APPDATA%\agentweb\agentweb.json          ← 設定
%APPDATA%\agentweb\sessions\<id>.json    ← セッション
```

- 旧バージョンで `aw.exe` と同じフォルダに置いていた `agentweb.json` は、
  初回起動時に自動で新しい場所へ移行されます（新しい場所にファイルが無い場合のみ）。
- 旧配置（`aw.exe` と同じフォルダ）のファイルは、**新しい場所にファイルが無い場合のみ**読み込まれます。
- **Web 画面の `⚙ settings` から編集・保存できます**（保存後は即時反映。ポート変更のみ再起動が必要）。

`agentweb.json.example` は設定項目のサンプルです。

```json
{
  "port": 8000,
  "llmApiUrl": "https://api.openai.com/v1/chat/completions",
  "llmApiKey": "YOUR_API_KEY",
  "model": "gpt-4o-mini",
  "allowTools": true,
  "emitReasoning": true,
  "maxToolsPerTurn": 16,
  "toolTimeoutSec": 30,
  "contextWindow": 64000,
  "reserveTokens": 12000,
  "keepRecentTokens": 20000,
  "useSystemProxy": true,
  "proxyUrl": "",
  "proxyUser": "",
  "proxyPass": "",
  "systemPrompt": "",
  "bindPath": "",
  "openBrowserOnStart": true
}
```

| キー | 既定 | 説明 |
|---|---|---|
| `port` | `8000` | 待ち受けポート |
| `llmApiUrl` | `https://api.openai.com/v1/chat/completions` | チャット API の完全 URL |
| `llmApiKey` | （空） | 必須。OpenAI 互換 API のキー |
| `model` | `gpt-4o-mini` | モデル名 |
| `allowTools` | `true` | ツール実行許可 |
| `emitReasoning` | `true` | モデルの思考(`reasoning_content`/`reasoning`)を UI に表示するか |
| `maxToolsPerTurn` | `16` | 1 ターンの同時ツール呼び出し上限 |
| `toolTimeoutSec` | `30` | シェル実行タイムアウト（秒）・curl タイムアウト |
| `contextWindow` | `64000` | コンテキスト上限（推定トークン） |
| `reserveTokens` | `12000` | 応答用に残すトークン |
| `keepRecentTokens` | `20000` | 要約せず残す直近トークン |
| `useSystemProxy` | `true` | `false` ならプロキシを使わず直接接続 |
| `proxyUrl` | （空） | 社内プロキシ URL（例 `http://proxy:8080`） |
| `proxyUser` / `proxyPass` | （空） | プロキシ認証（任意） |
| `systemPrompt` | 内蔵日本語プロンプト | AI エージェントの人格／振る舞い |
| `bindPath` | exe の場所 | 静的ファイル公開ディレクトリ |
| `openBrowserOnStart` | `true` | 起動時にブラウザで UI を開くか |

> 互換性：既存の環境変数（`LLM_API_KEY` 等）も引き続き既定値として使えますが、
> `agentweb.json` が優先されます。

## 実行（コンソール）

- `aw.exe` は **コンソールアプリ**です。実行するとコンソールに起動情報を表示し、
  `http://localhost:<port>/` で待ち受けます。
- 画面には接続先（URL）、使用モデル、設定ファイルのパスが表示されます。
- **`Ctrl+C`** で停止できます。
- `openBrowserOnStart`（既定 `true`）の場合、起動時に既定のブラウザで UI を開きます。
- オプション： `aw.exe --port 8080` / `--config <path>` / `--help`

### スタートアップ（自動起動）登録
Windows の**スタートアップフォルダ**に `aw.exe` へのショートカットを置くだけで、
ログオン時に自動起動できます（コンソールウィンドウが表示されます）。

## 整合性チェック（サーバ／UI）

`AgentWeb.cs`（サーバ）と `index.html`（UI）は別ファイルなので、片方だけ変更すると
API やイベント名の不一致で不具合になります。`check_consistency.py` が以下を自動検査します。

| 検査 | 内容 |
|---|---|
| エンドポイント | UI の `fetch()` パス・メソッドがサーバのルートに存在するか |
| SSE イベント | サーバが `emit` するイベント名を UI が処理しているか（`toolend` は任意） |
| SSE フィールド | `content`/`reasoning`/`tool` 等の JSON キーと UI の参照キーが一致するか |
| `/api/health` | レスポンスのキーを UI が正しく参照しているか |
| セッション API | 一覧・詳細・作成・自動命名のレスポンスキーが一致するか |
| `/api/tools` | `allowTools` などのキーが一致するか |
| 構成 | `index.html` が外部ファイルとして存在し、埋め込み(base64)が残っていないか |

```bat
python check_consistency.py
:: または
check.bat
```

- 終了コード: `0` = 問題なし（警告のみ）／`1` = 不整合あり
- `build.bat` は **Python が使える場合のみ自動で実行**し、不整合があるとビルドを中止します。
  - `py -3` → `python` の順に検出し、実際に起動できるか（`-c "import sys"`）まで確認します。
  - Python が無い / 実行できない場合（Microsoft Store 版のスタブ等）は**警告を出してスキップ**し、
    ビルドはそのまま続行します。

## HTTP エンドポイント

| メソッド / パス | 説明 |
|---|---|
| `GET /` | 対話 UI（`index.html`）を返す |
| `GET /static/<file>` | 静的ファイル |
| `GET /api/health` | 接続状態・モデル名・キー設定・ツール可否・プロキシを JSON で返す |
| `GET /api/tools` | 利用可能なツール定義（JSON Schema）を返す |
| `GET /api/diag/tools` | モデルが function calling に対応しているかを確認（ツールは実行しない） |
| `POST /api/tools` | `{allow:true|false}` でツール on/off を切り替え |
| `POST /api/cancel` | 処理中の応答を停止 |
| `POST /api/chat` | body: `{"message":"テキスト","session":"ID"}` → `{"ok":true,"reply":"..."}` |

## ツール実行（Function Calling）

サーバは OpenAI 互換の **tools（Function Calling）** を通じて、AI エージェントが
ローカルマシン上でツールを実行できます。`TcpListener(IPAddress.Loopback)` で
**ローカルループバックのみ**待ち受けます。

| ツール名 | 説明 | 引数 |
|---|---|---|
| `shell` | `cmd.exe` で任意コマンドを実行（stdout/stderr/終了コード） | `cmd` |
| `read_file` | ファイル内容を読み取る | `path` |
| `write_file` | ファイルへ書き込む | `path`, `content` |
| `list_dir` | ディレクトリ一覧を返す | `path`, `pattern`? |
| `curl` | HTTP/HTTPS リクエスト送信（`proxyUrl` 設定時は自動でプロキシ経由） | `url`, `method`?, `headers`?, `body`? |

> ツール実行時は UI に **ツール名と詳細**（`shell` はフルコマンド、ファイル系はパス/パターン、`curl` はメソッド+URL）を表示します。
> ツール実行の記録は**セッションに保存**され、ブラウザをリロードしても吹き出し内に復元されます。
> 表示は**吹き出しの上部（思考ブロックの外側）**に置かれ、応答完了後もそのまま残ります
> （会話の再描画はせず、思考ブロックも自動では折りたたみません）。

### セキュリティ上の注意
- **任意のコマンド実行を許可する**ため、リモート公開や複数利用者で使うのは危険です。
  自分のローカルマシン専用としてください。
- 無効化は `allowTools: false`（または実行中の Web UI で「tools」をクリックして on/off）。
- `shell` にはタイムアウト（`toolTimeoutSec`）と出力長制限を適用しています。

## トラブルシューティング（ツールが実行されない場合）

`aw.exe` はコンソール版なので、実行中のログがそのまま表示されます。ツールが呼ばれない場合は次を確認してください。

| ログ | 意味 / 対処 |
|---|---|
| `[agent] stream: tools=off (allowTools=False)` | ツールが無効。`agentweb.json` の `allowTools` を `true` に（UI の `tools` バッジでも切替可） |
| `[agent] stream: tools=on defs=5` | ツール定義は送信されている（正常） |
| `[agent] stream first data: {...}` | API の最初の応答。`tool_calls` が含まれるか確認 |
| `[agent] finish_reason=...` | `tool_calls` ならツール呼び出し、`stop` なら本文のみで終了 |
| `[agent] stream done: contentLen=... toolCalls=0` | ツール呼び出しが0件（モデルがツール非対応の可能性） |
| `[agent] 警告: ツール呼び出しも本文も空です` | tools 未送信 / モデル非対応 / API 形式不一致 |
| `[agent] stream: 非SSE応答(...)` | プロキシが `stream:true` を無視。JSON として解釈して続行 |

> モデルが function calling に対応していない場合、ツールは呼ばれません（本文のみの応答になります）。

### モデルがツールを呼ぶか確認する

```bat
curl http://localhost:8000/api/diag/tools
```

`"supported": true` なら function calling 対応です。`false` の場合は次のいずれかです。

- モデル自体が function calling 非対応（例: 一部の推論特化モデル・小規模ローカルモデル）
  → `gpt-4o-mini` などツール対応モデルでお試しください。
- API/プロキシが `tools` を無視している（社内プロキシで落とされている等）

リクエストには `tools` と `tool_choice: "auto"` を送信しています。

## 設定画面（Web UI）

画面下部の **`⚙ settings`** ボタンで設定モーダルを開き、`agentweb.json` の内容を
ブラウザから編集・保存できます（デザインは Pi Web のモーダルに合わせています）。

- 保存先のパス（`%APPDATA%\agentweb\agentweb.json`）はモーダル下部に表示されます
- 保存は `POST /api/config`、取得は `GET /api/config`
- 表示は localhost のみですが、API キーも画面に表示されます（`type="password"`）
- `allowTools`（tools バッジ）の切替も設定ファイルへ保存されます

## セッション管理（Windows AppData に永続化）

会話履歴は**セッション単位**で保存され、再起動後も保持されます。
保存先は **`%APPDATA%\agentweb\sessions\<セッションID>.json`** です。
Web UI のサイドバーで切替・新規作成・リネーム・削除ができます。
各項目に **⟳（内容から名前を自動設定）**・**✎（名前変更）**・**×（削除）** のボタンがあります
（削除時は確認ダイアログを表示）。

| メソッド / パス | 説明 |
|---|---|
| `GET /api/sessions` | 一覧（ID・タイトル・日時・件数） |
| `POST /api/sessions` | 新規作成 |
| `GET /api/sessions/<id>` | 会話取得 |
| `DELETE /api/sessions/<id>` | 削除 |
| `POST /api/sessions/<id>/rename` | タイトル変更 |
| `POST /api/sessions/<id>/autotitle` | 会話内容からタイトルを自動生成して設定（LLM 使用） |

## コンパクション（pi 方式のトークン管理）

会話が上限に近づくと、古いメッセージを削るのではなく **LLM で要約して置換**します。

- 発動: `推定トークン > contextWindow - reserveTokens`（または `usage.total_tokens` 超過）
- `keepRecentTokens` 分だけ残し、それ以前を**ターン境界**で要約
- LLM へは `[システム + 要約] + 最近メッセージ` を送る。**完全履歴はディスクに残る**（lossless）

## Web UI を編集する場合
`index.html` を編集するだけで反映されます（サーバはリクエストごとに `index.html` を読み込むため、再ビルド不要）。
ブラウザを再読み込みしてください。

## 公開運用の注意（公開リポジトリ向け）
- **API キー・社内 URL・サーバ情報をリポジトリへコミットしない**こと。
  実際の接続先・キーは **`.gitignore` 済みの `agentweb.json`** に置いてください
  （`agentweb.json.example` はプレースホルダのまま公開）。
- `shell` などのツール実行を含むため、公開時は利用者本人専用である旨を明記ください。

## C# 5 / .NET 4.5 対応メモ
- `$` 文字列補間・`?.`・`nameof` 等の新しい構文は不使用（C# 5 に完全準拠）
- コンソールアプリのため、起動情報・エラーは `Console`（標準出力）に表示されます


## 開発・コミット名義

このリポジトリのコミットは、AI コーディングエージェント **Pi Agent** 名義で行っています。

- **Author**: `Pi Agent <rfc@earendil.com>`

コミット時にこの名義を使う場合（リポジトリローカル設定）:
```bat
git config user.name "Pi Agent"
git config user.email "rfc@earendil.com"
```


## ライセンス
本プロジェクトは自由に利用・改変して構いません。
Web UI はダークカラー配色（#09090b / #1a1a1a / #e8e8e8 / #9ca3af / #3a3a3a / #60a5fa）です。

## デザイン (Pi Web 風テーマ)

UI の配色・形状は Pi Web の CSS 変数をそのまま踏襲しています。

| 変数 | ダーク | ライト |
| --- | --- | --- |
| `--bg` | `#1a1a1a` | `#ffffff` |
| `--panel` (`--bg-panel`) | `#242424` | `#f5f5f5` |
| `--panel-2` (`--bg-hover`) | `#2e2e2e` | `#eeeeee` |
| `--panel-3` (`--bg-selected`) | `#383838` | `#e8e8e8` |
| `--border` | `#3a3a3a` | `#e0e0e0` |
| `--text` | `#e8e8e8` | `#1a1a1a` |
| `--muted` (`--text-muted`) | `#9ca3af` | `#6b7280` |
| `--dim` (`--text-dim`) | `#6b7280` | `#9ca3af` |
| `--accent` | `#60a5fa` | `#2563eb` |
| `--user-bg` | `#1e293b` | `#eff6ff` |
| `--tool-bg` | `#1f2937` | `#f9fafb` |

- ユーザー発言: `--user-bg` 背景・`rgba(59,130,246,.2)` 枠・角丸 12px（Pi Web と同じ）
- アシスタント発言: 吹き出しなしのフラット表示（全文幅）
- ツール実行: `--tool-bg` のカード表示（等幅フォント）
- 本文は 14px / 行間 1.6、コードは等幅フォント
