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
  --diag             接続診断(TLS/HTTP)を実行して終了
  --debug            デバッグ出力を有効にする（TAU_DEBUG=1 と同じ）
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

### TLS（HTTPS 接続）

`tau` は LLM API へ接続する前に **TLS のバージョンを明示**します。

.NET Framework のターゲットが 4.7 未満だと既定が TLS 1.0 相当になり、
TLS 1.2 以上を要求する API（OpenAI 系・DeepSeek 系など）で次のエラーになります。

```
⚠ エラー: LLM API(stream)エラー: 要求は中止されました:
SSL/TLS のセキュリティで保護されているチャネルを作成できませんでした
```

`ApplyTls()` は **`Tls12` を単独で指定**します。`SystemDefault` も `Tls13` も使いません。

| 項目 | 理由 |
|---|---|
| `SystemDefault`（値 0）を使わない | .NET Framework 4.7 未満では設定自体が拒否され、`SslProtocolType` エラーになる。設定できても実効値が TLS 1.0 のままになることがある |
| `Tls13` を使わない | Mono や古い .NET では解釈できず、同じ `SslProtocolType` エラーになる |
| `Tls12` を単独指定 | Windows 7 以降で必ず使えるため最も安全 |

### デバッグと接続診断

うまく接続できない場合は、**接続診断**を実行してください。

```
tau.exe --diag
```

環境情報（.NET / Mono / OS / 既定の TLS）、接続先の DNS、**TLS の各バージョンでの接続可否**、
実際の API 呼び出し結果を表示します。

```
  [TLS の接続試験]
    (TCP 接続 + TLS ハンドシェイクのみを試します。HTTP は送りません)

    SystemDefault : NG  AuthenticationException: ... <- ...CERTIFICATE_VERIFY_FAILED
    Tls12         : OK  Tls12 / Aes128
    Tls11         : NG  AuthenticationException: ... <- ...TLSV1_ALERT_PROTOCOL_VERSION
```

TLS の可否は **TCP 接続 + TLS ハンドシェイクだけ**で判定します（HTTP は送りません）。
`HEAD` を使うと HEAD 非対応のサーバで `501` が返り、TLS が使えるのに NG と誤表示するためです。

#### 出力の見方

| 表示 | 意味 |
|---|---|
| **API 呼び出し試験が OK** | 接続・認証は正常（これが最終判定） |
| `TLSV1_ALERT_PROTOCOL_VERSION` | サーバがその TLS バージョンを受け付けない |
| `CERTIFICATE_VERIFY_FAILED` | 証明書の検証に失敗（社内プロキシの傍受など） |
| `SslProtocolType` エラー | .NET がその値（`SystemDefault` 等）を解釈できない |
| `501 実装されていません` | HTTP メソッド（HEAD）が未対応 |

**`API 呼び出し試験` が `OK` なら、`tau` の接続は正常です。**
TLS の接続試験が NG でも、それは試験方法の差であり、
実際の `HttpWebRequest` は別経路で成功している場合があります。

さらに詳しく見たい場合は**デバッグ出力**を有効にします。

```
tau.exe --debug            (または環境変数 TAU_DEBUG=1)
```

リクエストの設定（TLS・プロキシ・HTTP バージョン・タイムアウト）、応答ヘッダ、
SSE の先頭行、例外の連鎖、再試行の判断が `stderr` に表示されます。

```
[debug] TLS: SystemDefault(0) -> Tls12(3072)
[debug]   URL      : https://...
[debug]   Proxy    : null(直接接続)
[debug]   HTTP     : 1.1
[debug] WebException: status=TrustFailure
[debug]   連鎖: System.Net.WebException: ... <- AuthenticationException: ...
```

エラー時は `TAU_DEBUG=1` の案内も表示されます。

### 通信エラーの表示

エラーは原因が分かる形で表示します。HTTP エラーの場合は**ステータスコードと本文**を、
TLS などの内部エラーは**内部例外のメッセージ**も併記します。

```
⚠ エラー: LLM API(stream)エラー: The remote server returned an error:
(401) Unauthorized. (status=ProtocolError) [HTTP 401]
{"error": {"message": "Invalid API key"}}
```

**応答が途中で切断された場合**（`[DONE]` を受け取らずにストリームが終わった場合）は、
途中までの本文を確定させずにエラーとして報告します。

```
⚠ エラー: LLM API(stream)エラー: 応答が途中で切断されました(受信済み 42 文字)。
  もう一度お試しください。
```

> `受信時に予期しないエラーが発生しました` のようなメッセージは、
> ストリーム読み取り中の切断（ネットワーク/プロキシのタイムアウト、サーバ側の応答中断）で発生します。
> `tau` は `Expect: 100-continue` を無効化し、HTTP/1.1 KeepAlive と Nagle 無効化を設定して
> 接続を安定させています。

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
| TLS | `SystemDefault`/`Tls13` を使わず `Tls12` 単独か |
| 診断 | `--diag` が TLS 各バージョンの接続可否を出すか |
| デバッグ | `--debug` / `TAU_DEBUG` で詳細が出るか |
| TLS 失敗 | `SecureChannelFailure` / `SslProtocolType` エラーを検出するか |
| TLS 除外 | 証明書エラー（`TrustFailure`）は再試行しないか |
| TLS 再試行 | ストリーミング/非ストリーミングの両方で再試行するか |
| TLS 適用 | LLM 呼び出しと `curl` の両方で設定しているか |
| HTTP エラー | ステータスコードとレスポンス本文が表示されるか |
| 途中切断 | 応答の途中切断を検出し、黙って確定しないか |
| `/new` | 毎回異なるセッションが作られるか |

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
