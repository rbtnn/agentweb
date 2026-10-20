# agentweb

Windows に同梱の **`csc.exe`（C# 5 / .NET Framework 4.5）** でコンパイルできる、
**自己完結型のローカル Web UI を備えた AI エージェント**です。

- 組込み HTTP サーバ（`http://localhost:8000/`）+ Web UI
- OpenAI 互換 LLM API 連携 + ツール実行（shell / read_file / write_file / list_dir / curl）
- **タスクトレイ常駐・コンソール非表示**（winexe）、スタートアップ登録も可能
- セッション管理（AppData 永続化・サイドバー）と pi 方式のコンパクション
- 社内プロキシ対応 / ダーク・ライトテーマ / Mermaid 描画

## 構成

| ファイル | 役割 |
|---|---|
| `AgentWeb.cs` | 本体（単一ソース）。HTTP サーバ + LLM クライアント + JSON エンジン。**Web UI を base64 で内包** |
| `index.html` | Web UI の元ソース（`embed_ui.py` で `.cs` に埋め込み） |
| `embed_ui.py` | `index.html` を base64 化して `AgentWeb.cs` に埋め込む |
| `agentweb.json.example` | 設定ファイルのサンプル（実際の設定は `agentweb.json` にコピー） |
| `build.bat` | `csc.exe` で `aw.exe` をビルド（CP932/CRLF） |
| `README.md` | 本ドキュメント |

> `build.bat` は **DOS(CRLF) + CP932(Shift-JIS)**、`AgentWeb.cs` は **UTF-8(BOM付き)** です。
> csc が日本語文字列を正しく読めるよう BOM を付けています。

### 外部依存ゼロの内訳
- **HTTP サーバ** … `System.Net.Sockets.TcpListener` による独自実装
  （`HttpListener` を使わないため **管理者権限 / `netsh urlacl` / IIS 不要**）
- **LLM API クライアント** … `System.Net.HttpWebRequest`
- **JSON** … 自前実装（追加アセンブリ不使用）
- **タスクトレイ** … `System.Windows.Forms`（.NET Framework 同梱アセンブリ。ビルド時参照のみ）

## ビルド手順（Windows / csc.exe）

```bat
cd agentweb
build.bat
:: （csc は自動検索。GUI/winexe + タスクトレイ版 aw.exe を生成）
```

## 設定（agentweb.json）

実行時に、**`aw.exe` と同じフォルダの `agentweb.json`** を読み込みます（無ければ環境変数/既定値）。
`agentweb.json.example` を `agentweb.json` にコピーして編集してください。

```json
{
  "port": 8000,
  "llmApiUrl": "https://api.openai.com/v1/chat/completions",
  "llmApiKey": "YOUR_API_KEY",
  "model": "gpt-4o-mini",
  "allowTools": true,
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

## 実行・タスクトレイ

- `aw.exe` は **GUI（winexe）** で、起動すると**コンソールは出さずタスクトレイに常駐**します。
  - トレイアイコンを **ダブルクリック** → ブラウザで `http://localhost:<port>/` を開く
  - **右クリックメニュー** →「ブラウザで開く」「設定ファイルを開く」「終了」
- ログは exe と同じフォルダの **`agentweb.log`** に出力されます。
- デバッグ時はコンソール表示できます： `aw.exe --console`
- その他オプション： `aw.exe --port 8080` / `--config <path>` / `--help`

### スタートアップ（自動起動）登録
タスクトレイ常駐なので、Windows の**スタートアップフォルダ**に `aw.exe` へのショートカットを
置く（またはレジストリの `Run` に登録）だけで、ログオン時に自動起動できます。

## HTTP エンドポイント

| メソッド / パス | 説明 |
|---|---|
| `GET /` | 対話 UI（`index.html`）を返す |
| `GET /static/<file>` | 静的ファイル |
| `GET /api/health` | 接続状態・モデル名・キー設定・ツール可否・プロキシを JSON で返す |
| `GET /api/tools` | 利用可能なツール定義（JSON Schema）を返す |
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

### セキュリティ上の注意
- **任意のコマンド実行を許可する**ため、リモート公開や複数利用者で使うのは危険です。
  自分のローカルマシン専用としてください。
- 無効化は `allowTools: false`（または実行中の Web UI で「tools」をクリックして on/off）。
- `shell` にはタイムアウト（`toolTimeoutSec`）と出力長制限を適用しています。

## セッション管理（Windows AppData に永続化）

会話履歴は**セッション単位**で保存され、再起動後も保持されます。
保存先は **`%APPDATA%\agentweb\sessions\<セッションID>.json`** です。
Web UI のサイドバーで切替・新規作成・リネーム・削除ができます。

| メソッド / パス | 説明 |
|---|---|
| `GET /api/sessions` | 一覧（ID・タイトル・日時・件数） |
| `POST /api/sessions` | 新規作成 |
| `GET /api/sessions/<id>` | 会話取得 |
| `DELETE /api/sessions/<id>` | 削除 |
| `POST /api/sessions/<id>/rename` | タイトル変更 |

## コンパクション（pi 方式のトークン管理）

会話が上限に近づくと、古いメッセージを削るのではなく **LLM で要約して置換**します。

- 発動: `推定トークン > contextWindow - reserveTokens`（または `usage.total_tokens` 超過）
- `keepRecentTokens` 分だけ残し、それ以前を**ターン境界**で要約
- LLM へは `[システム + 要約] + 最近メッセージ` を送る。**完全履歴はディスクに残る**（lossless）

## Web UI を編集する場合
1. `index.html` を編集
2. `python embed_ui.py` を実行（base64 化して `AgentWeb.cs` に埋め込み、UTF-8 BOM を維持）
3. `build.bat` で再ビルド

## 公開運用の注意（公開リポジトリ向け）
- **API キー・社内 URL・サーバ情報をリポジトリへコミットしない**こと。
  実際の接続先・キーは **`.gitignore` 済みの `agentweb.json`** に置いてください
  （`agentweb.json.example` はプレースホルダのまま公開）。
- `shell` などのツール実行を含むため、公開時は利用者本人専用である旨を明記ください。

## C# 5 / .NET 4.5 対応メモ
- `$` 文字列補間・`?.`・`nameof` 等の新しい構文は不使用（C# 5 に完全準拠）
- 通常は GUI 指向で `Console` 出力不要。`aw.exe --console` 時のみ表示


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
