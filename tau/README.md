# tau — コンソール上で動く AI エージェント

`tau` は **コンソール（ターミナル）だけで動く AI エージェント**です。
同じリポジトリにある `agentweb`（Web UI 版）とは**独立した実装**で、コードの共通化はしていません
（`agentweb` のソースを参考に、必要な部分を単一ファイルに書き起こしています）。

HTTP サーバも Web UI も持たず、標準入出力だけで対話します。
`tau` が内部に持つメッセージ（ヘルプ・ツール定義・エラー表示など）は**英語**です。
**設定もセッションも `tau` 独自のファイル**を使い、`agentweb` とは共有しません。

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

  --config <path>    設定ファイル（tau.json）のパス
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
| `/sessions` | 保存済みセッションの一覧（番号付き） |
| `/open <id>` | 指定したセッションを開く |
| `/delete <番号\|ID>` | セッションを削除（確認あり） |
| `/model` | 現在のモデルと登録一覧を表示 |
| `/model <名>` | モデルを切り替え（設定ファイルにも保存） |
| `/config` | 現在の設定を表示 |
| `/config edit` | 対話形式で設定を変更 |
| `/set <key> <value>` | 設定を 1 項目だけ変更 |
| `/tools` | ツール実行の on/off を切り替え |
| `/clear` | 現在のセッションの会話履歴をクリア（確認あり） |
| `/exit`, `/quit` | 終了（`Ctrl+D` でも終了） |

### セッションの内容をクリア

`/clear` は**現在のセッションの会話履歴だけ**を消します。セッション自体（ID・タイトル）は残ります。

```
> /clear
  このセッションの会話履歴を消しますか？ (12 件)  [y/N]: y
  · Cleared the session history: s20261109...  (タイトル)
```

- `y` / `yes` 以外を入力すると中止します
- `messages` / `summary` / `contextStart` をリセットします（コンテキスト整理の状態も初期化）
- 会話が空のセッションでは何もしません（メッセージだけ表示）

> 以前の「画面を消す」動作は廃止しました。画面を消したい場合はコンソールの `cls`（Windows）/
> `clear`（Unix）を使ってください。

### セッションの削除

`/sessions` で番号付きの一覧が出ます。`*` が現在のセッションです。

```
> /sessions
  保存済みセッション (C:\Users\...\AppData\Roaming\tau\sessions)
   1. s20261109...  Mermaid矢印置換  (12 件)
*  2. s20261109...  テスト            (4 件)
   3. s20261109...  新規会話          (2 件)

  * = 現在のセッション。削除は /delete <番号|ID>
```

**番号**でも **ID** でも指定できます。誤操作を防ぐため確認があります。

```
> /delete 3
  削除しますか？ s20261109...  (新規会話)  [y/N]: y
  · 削除しました: s20261109...  (新規会話)
```

- `y` / `yes` 以外を入力すると中止します
- **表示中のセッションを削除した場合**は、自動的に別のセッション（無ければ新規）へ切り替わります
- 削除した ID は「最後に開いていたセッション」からも取り除かれます

`%APPDATA%\tau\sessions\` のファイルを直接消しても構いません（`_state.json` は残してください）。

`Ctrl+C` で生成中の応答を中断できます。

---

## 設定とデータの保存先

`tau` は `agentweb` とは独立した場所に保存します。

| 種類 | パス |
|---|---|
| 設定 | `%APPDATA%\tau\tau.json` |
| セッション | `%APPDATA%\tau\sessions\<id>.json` |
| 最後に開いたセッション | `%APPDATA%\tau\sessions\_state.json` |

Linux / macOS では `~/.config/tau/` が使われます。

> セッションの**ファイル形式**は `agentweb` と同じ（互換）ですが、
> **保存先は共有しません**。`agentweb` の設定・セッションには一切触れません。

### 環境変数

`agentweb` と同じ名前の環境変数が使えます（設定ファイルがある場合はそちらが優先）。

```
LLM_API_URL / LLM_API_KEY / LLM_MODEL
ALLOW_TOOLS / LLM_EMIT_REASONING
MAX_TOOLS_PER_TURN / TOOL_TIMEOUT_SEC
LLM_CONTEXT_WINDOW / LLM_RESERVE_TOKENS / LLM_KEEP_RECENT_TOKENS
LLM_PROXY_URL / LLM_PROXY_USERNAME / LLM_PROXY_PASSWORD / LLM_USE_SYSTEM_PROXY
AI_SYSTEM_PROMPT
TAU_CONFIG
```

`tau.json` が無くても、環境変数だけで動作します。

---

## 設定項目（`tau.json`）

`tau` が使う項目は次のとおりです。

```json
{
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
  "proxyUrl": "",
  "proxyUser": "",
  "proxyPass": "",
  "useSystemProxy": true,
  "systemPrompt": "",
  "inputEncoding": "shift-jis"
}
```

- モデルは `model` に**1 つだけ**指定します。`models`（モデル名の一覧）は `tau` では使いません。
  接続先・API キー・コンテキスト上限も共通の 1 つです。
- `/model <名前>` は任意のモデル名を受け付けます（事前登録は不要）。
- 既存の `tau.json` に `models` があっても無視され、保存時に取り除かれます
  （`agentweb` 側の `models` はそのまま残ります）。
- `tau` が扱わないキーを手で追記しても、保存時に失われません。

### プロンプトから設定を変更する

`tau.json` を直接編集しなくても、対話中に設定を変更できます。

| コマンド | 説明 |
|---|---|
| `/config` | 現在の設定を一覧表示（API キーは伏せ字） |
| `/config edit` | 番号を選んで 1 項目ずつ対話形式で変更 |
| `/set <key> <value>` | 1 項目だけ変更 |
| `/model [名]` | モデルの表示・切り替え |
| `/tools` | ツール実行の on/off を切り替え |

```
> /config
  設定ファイル: C:\Users\...\AppData\Roaming\tau\tau.json

    llmApiUrl          = https://api.openai.com/v1/chat/completions
    llmApiKey          = sk-...********
    model              = gpt-4o-mini
    allowTools         = true
    emitReasoning      = true
    contextWindow      = 64000
    ...

> /set model gpt-4o
  · model = gpt-4o  に変更しました

> /set contextWindow 128000
  · contextWindow = 128000  に変更しました

> /config edit
  1. llmApiUrl          = https://...
     OpenAI 互換の /chat/completions の URL
  2. llmApiKey          = sk-...********
     API キー(表示時は伏せ字)
  ...
  変更する項目の番号を入力 (Enter で中止): 3
  model  (使用するモデル名)
  現在値: gpt-4o
  新しい値を入力 (Enter で中止): gpt-4o-mini
  · model = gpt-4o-mini  に変更しました
```

変更は即座に `tau.json` へ保存され、次のターンから反映されます。
`systemPrompt` だけは**複数行**で入力できます（空行で終了）。

入力チェックも行います（不明な key、数値でない値、不正な真偽値は拒否）。

---

## 機能

### 日本語の入出力

出力は常に **UTF-8**（日本語の表示が化けない）。
**入力のエンコーディングは `inputEncoding` 設定で選べます。**

| `inputEncoding` | 内容 |
|---|---|
| **`shift-jis`**（既定） | Shift-JIS（CP932）。**Windows のコンソール既定で、日本語入力が安定** |
| `utf-8` | UTF-8（CP65001）。コンソールが UTF-8 のとき |
| `default` | コンソール/OS の既定に任せる（変更しない） |

```
tau.json:
  "inputEncoding": "shift-jis"
```

**Shift-JIS が既定です。** Windows のコンソールは CP932 で動いていることが多く、
入力側を UTF-8 にすると日本語が NUL 文字（`\u0000`）に化けてしまいます
（dotnet/runtime#43295）。出力は UTF-8 でも、入力は CP932 のままにしておくのが
最も安定します。

- 入力が**リダイレクト**されている場合は設定に関係なく触りません（パイプ・ファイル入力）
- 設定は `/set inputEncoding utf-8` または `/config edit` の **16. Input Encoding** で変更できます
- `cp932` / `sjis` / `65001` / コードページ番号などの別名も受け付けます

#### 入力が NUL に化けた場合

以前は NUL を黙って除去した結果、**入力が空になり何も表示せず再入力待ち**になって
「応答が返らない」ように見えることがありました。
現在はその場合に警告と対処方法を表示します。

```
> ⚠ The input was lost (all 30 characters became NUL).
  The console input encoding does not match.
  Try one of the following:
   - Run `chcp 65001` before starting tau (or run it in Windows Terminal)
   - Set TAU_DEBUG=1 and check the reported input encoding
   - Paste the text from a file:  tau --session <id> < input.txt
  · input encoding: utf-8 (CP65001)
```

一部だけ NUL に化けた場合は、**欠落した文字数を表示**したうえで、
残りの文字を送信します。

**`/config edit` の複数行入力（システムプロンプトなど）でも同じ対処をします。**
以前は NUL で空になった行を「入力終了の空行」と誤判定し、何も入力できていないのに
即終了して反映されない、という状態になっていました。現在は:

- NUL で全滅した行は**空行扱いにせず**、警告して入力をやり直してもらう
- 一部だけ NUL の行は、欠落を表示して残りを受け付ける

また、**連続して入力が失われる場合は打ち切って**対処方法を案内します（無限ループ防止）。

```
  The input has been lost 2 times in a row; stopping here.
  Change the console code page (chcp 65001) and start tau again,
  or write your text to a file and pipe it in:
    tau.exe < input.txt
```

`tau --diag` でも現在の入出力エンコーディングを確認できます。

```
  [environment]
    input encoding : utf-8 (CP65001)
    output encoding: utf-8 (CP65001)
```

### Markdown の整形表示

モデルの回答は Markdown として解釈し、**記号をそのまま出さずに色・太字・罫線に変換**して表示します。

| Markdown | 表示 |
|---|---|
| `# 見出し` / `## 見出し` | 太字＋色、下に `====` / `----` の下線 |
| `**太字**` / `*斜体*` | **太字** / *斜体*（ANSI） |
| `` `コード` `` | 黄色の文字 |
| ` ```コードブロック``` ` | 枠付き・緑色（言語名は枠の上辺に表示） |
| `- 項目` / `* 項目` | `・項目`（インデントは深さで表現） |
| `1. 項目` | `1. 項目`（そのまま、色付き） |
| `| A | B |` | **罫線で整形した表**（下記） |
| `> 引用` | 左に `│` を付けて斜体 |
| `---` | 横罫線 |
| `[text](url)` | `text (url)`（下線＋青、URL は灰色） |

表は罫線文字で枠を作り、**列幅を内容に合わせて自動調整**します。
`:---` `---:` `:---:` の指定で左寄せ・右寄せ・中央寄せになります。
**全角文字を 2 桁幅として計算**するので、日本語の表でも桁がずれません。

```
  ┌────────┬────────────┐
  │ 項目   │       説明 │
  ├────────┼────────────┤
  │ あ     │ あいうえお │
  │ あああ │         い │
  └────────┴────────────┘
```

`NO_COLOR=1` を設定するか、出力をリダイレクトすると色は付かず、罫線と字下げだけで整形されます。

### 動かないときの確認

まず **どのビルドを動かしているか** を確認してください。
古い `tau.exe` が残っていると、修正が反映されません。

```
tau.exe --version
```

`run.bat` はソースが新しい場合に自動でビルドし直すようにしました。

#### どこで止まっているかを調べる

```
set TAU_DEBUG=1
tau.exe
```

次のように、段階ごとに表示されます。

```
  [waiting for input]      ← 入力待ち(ここで止まるなら入力の問題)
>   [input] length=100     ← 入力を受け取った文字数
  sending 100 chars...     ← API へ送信
  / connecting...          ← 接続中
  - receiving... 45 chars  ← 受信中の進行状況
```

- `[waiting for input]` の後で何も出ない → **入力が返ってきていない**
- `[input] length=0` → 入力が空になっている(エンコーディング不一致)
- `sending ...` の後で止まる → **通信の問題**

`diag_input.bat` をダブルクリックすると、バージョン・コードページ・
環境変数・長い日本語入力のテストをまとめて実行できます。

#### 入力が返ってこない場合

Windows のコンソールでは `Console.ReadKey()` が IME と組み合わさると
入力が返らないことがあります。そのため **Windows では自前の行編集を
既定で使いません**（`Console.ReadLine()` に任せます）。
実際のコンソール コードページ（`GetConsoleCP`）も確認しています。

`inputEncoding` が実際のコンソールと合っていないと入力が壊れます。
`chcp` で表示されるコードページと `inputEncoding` を合わせてください。

```
chcp                 ← 現在のコードページを確認(932 など)
/set inputEncoding shift-jis
```

### 進行状況の表示

Markdown の整形には回答全体が必要なため、本文はいったんバッファして
完成後にまとめて表示します。そのままだと長い回答の間ずっと画面が止まって
「応答が無い」ように見えるため、**受信中の進行状況**を同じ行に表示します。

```
> 日本語の長いプロンプトです。...
  / connecting...  - receiving... 45 chars  \ receiving... 94 chars
```

- Enter を押した直後に `connecting...` が出ます（無言の時間を作りません）
- 受信が進むと文字数が増えます
- 表示が終わると進行状況の行は消え、整形された回答に置き換わります

`TAU_DEBUG=1` のときは送信した文字数も表示します。

### 入力（行編集）

入力は **`Console.ReadLine()`**（コンソール標準の行編集）に任せています。
`←` `→` `Home` `End` `Backspace` `Delete` などは、お使いのコンソールの
標準機能としてそのまま使えます。

> **自前の Emacs 風キーバインドは使っていません。**
> `Console.ReadKey()` で 1 文字ずつ読む方式は、コンソールの実際の
> コードページと `inputEncoding` が合っていないと日本語が壊れ
> （例: 「あ」→ U+7E3A）、**長い日本語が途中で切れて送信される**
> ことがありました。安定して動く `Console.ReadLine()` に任せています。
>
> 起動時に `SetConsoleCP` でコードページを `inputEncoding` に合わせる
> 処理は入れてあるので、将来復活させる土台はできています。ただし
> **実機で長い日本語と IME の確認が取れるまで有効にしない**方針です。

日本語が化ける場合は `inputEncoding` を実際のコンソールに合わせてください。

#### 長い入力が途中で切れる場合

`Console.ReadLine()` は .NET Framework で**既定 256 単位の入力バッファ**を使います。
この上限はバイト数ではなく**文字数**で判定され、256 には CR/LF も含まれるため
**254 文字**までしか読めません。それ以降はエラーも警告もなく黙って捨てられます。

全角・半角を問わず **254 文字**で切れるので、日本語の長いプロンプトが
途中で切れて送信されます。

> 実測例: `[input] length=254 bytes=532`
> 254 は**文字数**の上限で、532 バイト（UTF-8）は上限とは無関係です。

tau は起動時に `Console.In` を **64KB バッファの StreamReader** に差し替えて
この上限を回避しています。`Console.ReadLine()` を使うので、コンソールの
行編集・IME はそのまま使えます。

`TAU_DEBUG=1` で入力の文字数とバイト数が確認できます。

```
>   [input] length=128 bytes=256
```



```
chcp                          ← 現在のコードページを確認（932 など）
/set inputEncoding shift-jis  ← 932 のとき
/set inputEncoding utf-8      ← 65001（UTF-8）のとき
```

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
実行記録はセッションの `steps` に保存され、`/open` で再開したときにも表示されます。

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

### 一時的な失敗の自動再試行

接続が一時的に失敗することがあるため、**最大 3 回**まで自動で再試行します
（0.5 秒 → 1 秒 のバックオフ）。

```
  · 接続を再試行します(1/2)…
  · 接続を再試行します(2/2)…
再試行で成功
```

| 再試行する | 再試行しない |
|---|---|
| `SecureChannelFailure`（一時的なことも多い） | `ProtocolError`（HTTP 401/429/500 など） |
| `ConnectionClosed` / `KeepAliveFailure` | `TrustFailure`（証明書の問題） |
| `ReceiveFailure` / `SendFailure` / `Timeout` / `ConnectFailure` | |

HTTP エラーや証明書エラーは、繰り返しても結果が変わらないため再試行しません。

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

`--diag` は 3 種類の試験を行います。

| 試験 | 内容 |
|---|---|
| **TLS の接続試験** | TCP + TLS ハンドシェイクのみ（HTTP は送らない） |
| **API 呼び出し試験** | 非ストリーミング（`CallOnce`）で実際に API を呼ぶ |
| **ストリーミング呼び出し試験** | 対話と同じ経路（`CallStream` / `Accept: text/event-stream`） |

| 表示 | 意味 |
|---|---|
| **API 呼び出し試験が OK** | 接続・認証は正常（これが最終判定） |
| API は OK だがストリーミングが NG | ストリーミング固有の問題（プロキシが SSE を妨害等） |
| `TLSV1_ALERT_PROTOCOL_VERSION` | サーバがその TLS バージョンを受け付けない |
| `CERTIFICATE_VERIFY_FAILED` | 証明書の検証に失敗（社内プロキシの傍受など） |
| `SslProtocolType` エラー | .NET がその値（`SystemDefault` 等）を解釈できない |
| `501 実装されていません` | HTTP メソッド（HEAD）が未対応 |

> `Proxy` の表示が接続先 URL と同じ場合は、**プロキシを経由せず直接接続**しているという意味です
> （`IsBypassed=true`）。`tau` はその場合「直接接続(プロキシを経由しない)」と表示します。

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
| 設定の分離 | `tau.json` を参照し、`agentweb.json` に触れないか |
| `models` 廃止 | `models` を使わず、保存時に取り除かれるか |
| 日本語 | `InputEncoding` を変えず、日本語がそのまま保存されるか |
| `/clear` | 履歴だけ消え、セッション(ID・タイトル)は残るか |
| `/clear` 中止 | `n` でクリアされないか |
| NUL 対策 | 入力の NUL 文字を取り除くか |
| 入力消失 | NUL で全滅したときに警告するか |
| 一部 NUL | 欠落を伝え、残りを送信するか |
| `/config edit` の日本語 | 日本語のプロンプトが保存されるか |
| NUL 行 | 空行と誤判定せず、やり直させるか |
| 連続 NUL | 打ち切って案内するか |
| `inputEncoding` | shift-jis / utf-8 / default を選べるか |
| 別名 | `cp932` などを `shift-jis` として扱うか |
| Markdown | 見出し・太字・コードの記号が除去されるか |
| 表 | 罫線文字で整形されるか |
| 進行状況 | 受信中の進行状況が表示されるか |
| バージョン | `--version` でビルドを確認できるか |
| 入力診断 | 入力待ち・読み取り失敗が表示されるか |
| 長い入力 | 長い日本語でも応答が返り、内容が欠けないか |
| 行編集 | 自前の行編集が無効化され ReadLine を使うか |
| 入力バッファ | 254 文字の上限を回避しているか |
| 長い混在入力 | 全角半角混在の長い入力が欠けないか |
| 行編集の条件 | UTF-8 入力のときだけ有効になるか |
| フォールバック | リダイレクト時は ReadLine を使うか |
| `/model` | 任意のモデル名を受け付けるか |
| セッションの分離 | `%APPDATA%\tau\sessions` に保存し、agentweb 側を汚さないか |
| セッション形式 | `agentweb` と同じキーで保存されるか |
| `/config` | 設定一覧を表示し、API キーが伏せ字になるか |
| `/set` | 値の変更と保存、不正値の拒否ができるか |
| `/config edit` | 対話形式で変更・保存できるか |
| `/delete` | 番号/ID 指定で削除でき、中止もできるか |
| 表示中の削除 | 表示中のセッションを消すと別のセッションへ切り替わるか |
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
| 再試行 | 一時的な失敗をバックオフ付きで最大3回試すか |
| 再試行除外 | HTTP エラー・証明書エラーは再試行しないか |
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
