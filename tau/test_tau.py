#!/usr/bin/env python3
"""tau の実行時回帰テスト。

モック LLM を立てて tau.exe を実際に起動し、
  1. ストリーミング表示(思考 + 本文)
  2. ツール実行ループ
  3. agentweb と同じセッション形式で保存されること
  4. agentweb と同じ設定ファイルを参照すること
  5. 続きから再開できること
  6. /model /tools コマンド
  7. API エラー時に落ちないこと
を検証します。

    python3 test_tau.py
"""
import glob, json, os, shutil, socket, subprocess, sys, tempfile, threading, time
import urllib.request
from http.server import BaseHTTPRequestHandler, HTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "AgentConsole.cs")

fail = []
def ok(msg): print("[ OK ] " + msg)
def bad(msg):
    print("[FAIL] " + msg); fail.append(msg)

def free_port():
    s = socket.socket(); s.bind(("127.0.0.1", 0))
    p = s.getsockname()[1]; s.close(); return p

# --------------------------------------------------------------------------
#  モック LLM (SSE)
# --------------------------------------------------------------------------
class Mock(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    calls = []
    def log_message(self, *a): pass
    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        req = json.loads(self.rfile.read(n).decode() or "{}")
        Mock.calls.append(req)
        has_tool_result = any(m.get("role") == "tool" for m in req.get("messages", []))
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Connection", "close")
        self.end_headers()

        def chunk(delta, fin=None):
            c = {"id": "c", "object": "chat.completion.chunk", "created": 1,
                 "model": "mock",
                 "choices": [{"index": 0, "delta": delta, "finish_reason": fin}]}
            self.wfile.write(("data: " + json.dumps(c, ensure_ascii=False) + "\n\n").encode())
            self.wfile.flush()

        chunk({"role": "assistant"})
        if not has_tool_result:
            chunk({"reasoning_content": "まず一覧を見よう。"})
            chunk({"tool_calls": [{"index": 0, "id": "call_1", "type": "function",
                   "function": {"name": "list_dir", "arguments": "{\"path\":\".\"}"}}]})
            chunk({}, "stop")
        else:
            chunk({"content": "一覧を取得しました。"})
            chunk({"content": "完了です。"})
            chunk({}, "stop")
        self.wfile.write(b"data: [DONE]\n\n"); self.wfile.flush()

class MockMulti(BaseHTTPRequestHandler):
    """思考→ツール→思考→ツール→本文 と返すモック(改行の検証用)。"""
    protocol_version = "HTTP/1.1"
    def log_message(self, *a): pass
    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        req = json.loads(self.rfile.read(n).decode() or "{}")
        tool_results = sum(1 for m in req.get("messages", []) if m.get("role") == "tool")
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Connection", "close")
        self.end_headers()

        def chunk(delta, fin=None):
            c = {"id": "c", "object": "chat.completion.chunk", "created": 1,
                 "model": "m", "choices": [{"index": 0, "delta": delta,
                                            "finish_reason": fin}]}
            self.wfile.write(("data: " + json.dumps(c, ensure_ascii=False) + "\n\n").encode())
            self.wfile.flush()

        chunk({"role": "assistant"})
        chunk({"reasoning_content": "考え中です。"})
        if tool_results == 0:
            chunk({"tool_calls": [{"index": 0, "id": "c1", "type": "function",
                   "function": {"name": "list_dir", "arguments": "{\"path\":\".\"}"}}]})
        elif tool_results == 1:
            chunk({"reasoning_content": "もう一度考えます。"})
            chunk({"tool_calls": [{"index": 0, "id": "c2", "type": "function",
                   "function": {"name": "list_dir", "arguments": "{\"path\":\"/tmp\"}"}}]})
        else:
            chunk({"content": "最終的な回答です。"})
        chunk({}, "stop")
        self.wfile.write(b"data: [DONE]\n\n")
        self.wfile.flush()


MD_REPLY = (
    "# Heading1\n## Heading2\n\n"
    "This is **bold** and *italic* and `code`.\n\n"
    "- item1\n- item2\n\n"
    "1. num1\n2. num2\n\n"
    "| Name | Qty | Note |\n|:-----|----:|:----:|\n"
    "| apple | 3 | red |\n| mikan | 10 | orange |\n\n"
    "> quoted text\n\n"
    "```python\nprint(1)\n```\n\n"
    "---\n[link](https://example.com)\n"
)


class MockMarkdown(BaseHTTPRequestHandler):
    """Markdown を返すモック(整形表示の検証用)。"""
    protocol_version = "HTTP/1.1"
    def log_message(self, *a): pass
    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        self.rfile.read(n)
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Connection", "close")
        self.end_headers()
        for d in ({"role": "assistant"}, {"content": MD_REPLY}):
            c = {"id": "c", "object": "chat.completion.chunk", "created": 1,
                 "model": "m", "choices": [{"index": 0, "delta": d,
                                            "finish_reason": None}]}
            self.wfile.write(("data: " + json.dumps(c, ensure_ascii=False) + "\n\n").encode())
            self.wfile.flush()
        self.wfile.write(b"data: [DONE]\n\n")
        self.wfile.flush()


class MockHttpError(BaseHTTPRequestHandler):
    """HTTP エラー(401 など)を返すモック。"""
    protocol_version = "HTTP/1.1"
    code = 401
    def log_message(self, *a): pass
    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        self.rfile.read(n)
        body = json.dumps({"error": {"message": "Invalid API key"}}).encode()
        self.send_response(MockHttpError.code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


class MockTruncate(BaseHTTPRequestHandler):
    """ストリームの途中で接続を切るモック([DONE] を送らない)。"""
    protocol_version = "HTTP/1.1"
    def log_message(self, *a): pass
    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        self.rfile.read(n)
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Connection", "close")
        self.end_headers()
        c = {"id": "c", "object": "chat.completion.chunk", "created": 1,
             "model": "m", "choices": [{"index": 0,
             "delta": {"content": "途中まで"}, "finish_reason": None}]}
        self.wfile.write(("data: " + json.dumps(c) + "\n\n").encode())
        self.wfile.flush()
        self.close_connection = True
        try: self.connection.close()
        except Exception: pass


# --------------------------------------------------------------------------
def main():
    tls_src = open(SRC, encoding="utf-8").read()   # ソースの静的チェック用
    exe = os.path.join(tempfile.mkdtemp(prefix="tau_build_"), "tau.exe")
    print("=== tau 実行時回帰テスト ===")
    rc = subprocess.run(["mcs", "-langversion:5", "-out:" + exe, SRC],
                        capture_output=True, text=True)
    if rc.returncode != 0:
        bad("tau.exe のビルドに失敗しました:\n" + rc.stdout + rc.stderr)
        return 1
    ok("tau.exe をビルドしました")

    # tau 独自の場所を使う(テスト用に HOME を差し替える)
    work = tempfile.mkdtemp(prefix="tau_home_")
    appdata = os.path.join(work, ".config", "tau")
    os.makedirs(appdata, exist_ok=True)
    # agentweb 側のディレクトリも作り、混ざらないことを確認できるようにする
    agentweb_dir = os.path.join(work, ".config", "agentweb")
    os.makedirs(agentweb_dir, exist_ok=True)

    srv = HTTPServer(("127.0.0.1", 0), Mock)
    port = srv.server_address[1]
    threading.Thread(target=srv.serve_forever, daemon=True).start()

    cfg_path = os.path.join(appdata, "tau.json")
    with open(cfg_path, "w", encoding="utf-8") as f:
        json.dump({
            "llmApiUrl": "http://127.0.0.1:%d/v1/chat/completions" % port,
            "llmApiKey": "sk-test",
            "model": "mock-model",
            "allowTools": True,
            "emitReasoning": True,
            "contextWindow": 64000,
            # tau が扱わない項目。保存しても失われないこと(手で追記した設定を守る)。
            "port": 8787,
            "theme": "light",
            "bindPath": ".",
            "openBrowserOnStart": False,
        }, f, ensure_ascii=False)

    env = dict(os.environ)
    env["LLM_USE_SYSTEM_PROXY"] = "0"
    env["HOME"] = work
    env["XDG_CONFIG_HOME"] = os.path.join(work, ".config")
    # agentweb と同じ環境変数名を使う
    for k in ("LLM_API_URL", "LLM_API_KEY", "LLM_MODEL", "ALLOW_TOOLS",
              "LLM_EMIT_REASONING"):
        env.pop(k, None)

    def run_tau(stdin_text, args=None, timeout=60):
        cmd = ["mono", exe] + (args or [])
        p = subprocess.run(cmd, input=stdin_text, capture_output=True,
                           text=True, env=env, timeout=timeout,
                           cwd=os.path.dirname(exe))
        return p.stdout + p.stderr

    # agentweb 側に別の設定を置いておく(tau が触らないこと)
    aw_cfg_path = os.path.join(agentweb_dir, "agentweb.json")
    with open(aw_cfg_path, "w", encoding="utf-8") as f:
        json.dump({"llmApiUrl": "http://agentweb-only/v1", "model": "aw-model",
                   "port": 8787, "theme": "light"}, f, ensure_ascii=False)
    aw_before = open(aw_cfg_path, encoding="utf-8").read()

    # ---- 1. 対話(ストリーミング + ツール) ----
    Mock.calls = []
    out = run_tau("ファイル一覧を見せて\n/exit\n")
    if "まず一覧を見よう" in out:
        ok("思考(reasoning)が表示されます")
    else:
        bad("思考が表示されません: " + out[-300:])
    if "list_dir" in out:
        ok("ツール実行が表示されます")
    else:
        bad("ツール実行が表示されません: " + out[-300:])
    if "完了です" in out:
        ok("最終回答が表示されます")
    else:
        bad("最終回答が表示されません: " + out[-300:])
    if len(Mock.calls) >= 2 and any(m.get("role") == "tool"
                                    for m in Mock.calls[-1].get("messages", [])):
        ok("ツール結果を LLM へ返して再問い合わせしています")
    else:
        bad("ツールループが動いていません: %d 回" % len(Mock.calls))

    # 設定ファイルが agentweb のものを使っている
    if cfg_path in out:
        ok("tau 独自の設定ファイル(tau.json)を参照しています")
    else:
        bad("設定ファイルのパスが想定と違います: " + out[:400])
    if "agentweb.json" not in out:
        ok("agentweb の設定ファイルは参照しません")
    else:
        bad("agentweb.json を参照しています: " + out[:400])

    # ---- 2. セッションが agentweb と同じ形式で保存される ----
    sdir = os.path.join(appdata, "sessions")
    files = [f for f in os.listdir(sdir) if f.endswith(".json") and not f.startswith("_")]
    if files:
        ok("セッションが %s に保存されます" % sdir)
    else:
        bad("セッションが保存されていません")
        return 1
    aw_sdir = os.path.join(agentweb_dir, "sessions")
    if not os.path.exists(aw_sdir) or not os.listdir(aw_sdir):
        ok("agentweb 側の sessions には何も書きません")
    else:
        bad("agentweb 側の sessions に書き込んでいます")

    sid = files[0][:-5]
    with open(os.path.join(sdir, files[0]), encoding="utf-8-sig") as f:
        sess = json.load(f)
    need = ["id", "title", "createdAt", "updatedAt", "summary", "contextStart", "messages"]
    if all(k in sess for k in need):
        ok("セッションのキーが agentweb と同一です")
    else:
        bad("セッションのキーが足りません: %s" % list(sess.keys()))
    msgs = sess["messages"]
    if any(m.get("role") == "user" for m in msgs) and any(m.get("role") == "assistant" for m in msgs):
        ok("ユーザー/アシスタントの発言が保存されています")
    else:
        bad("発言が保存されていません: %r" % msgs)
    asst = [m for m in msgs if m.get("role") == "assistant"]
    if asst and asst[-1].get("think"):
        ok("思考(think)が保存されています")
    else:
        bad("思考が保存されていません")
    if asst and asst[-1].get("steps"):
        ok("ツール実行記録(steps)が保存されています")
    else:
        bad("ツール実行記録が保存されていません")

    # _state.json に最後のセッションが記録される
    st_path = os.path.join(sdir, "_state.json")
    if os.path.exists(st_path):
        with open(st_path, encoding="utf-8-sig") as f:
            st = json.load(f)
        if st.get("lastSession") == sid:
            ok("_state.json に最後のセッションが記録されます")
        else:
            bad("lastSession が想定と違います: %r" % st)
    else:
        bad("_state.json が作られていません")

    # ---- 3. 続きから再開できる ----
    Mock.calls = []
    out = run_tau("/exit\n", args=["--session", sid])
    if "conversation so far" in out and "完了です" in out:
        ok("--session で続きから再開できます")
    else:
        bad("再開できません: " + out[-400:])
    if len(Mock.calls) == 0:
        ok("再開しただけでは LLM を呼びません")
    else:
        bad("再開時に LLM を呼んでいます")

    # ---- 4. --list ----
    out = run_tau("", args=["--list"])
    if sid in out:
        ok("--list でセッション一覧を表示できます")
    else:
        bad("--list にセッションが出ません: " + out[:300])

    # ---- 5. /model /tools ----
    out = run_tau("/model other-model\n/model\n/tools\n/tools\n/exit\n")
    if "other-model" in out:
        ok("/model でモデルを切り替えられます")
    else:
        bad("/model が効いていません: " + out[-400:])
    if "Tools: off" in out and "Tools: on" in out:
        ok("/tools でツールの on/off を切り替えられます")
    else:
        bad("/tools が効いていません")
    with open(cfg_path, encoding="utf-8-sig") as f:
        saved = json.load(f)
    if saved.get("model") == "other-model":
        ok("モデルの切り替えが tau.json に保存されます")
    else:
        bad("モデルが保存されていません: %r" % saved.get("model"))

    # models 一覧は持たないので、未登録の名前でもそのまま受け付ける
    out = run_tau("/model brand-new-model\n/model\n/exit\n")
    if "brand-new-model" in out:
        ok("/model は未登録のモデル名も受け付けます(models 一覧なし)")
    else:
        bad("/model が任意の名前を受け付けません: " + out[-400:])
    # tau が扱わないキーが保存で失われないこと
    kept = {"port": 8787, "theme": "light", "bindPath": ".",
            "openBrowserOnStart": False}
    missing = [k for k, v in kept.items() if saved.get(k) != v]
    if not missing:
        ok("tau が扱わないキー(手で追記した設定)が保持されます")
    else:
        bad("tau が扱わないキーが失われました: %r" % missing)

    # ---- 6. API エラーで落ちない ----
    bad_cfg = os.path.join(work, "bad.json")
    with open(bad_cfg, "w", encoding="utf-8") as f:
        json.dump({"llmApiUrl": "http://127.0.0.1:9/v1/chat/completions",
                   "llmApiKey": "k", "model": "m", "allowTools": False}, f)
    out = run_tau("こんにちは\n/exit\n", args=["--config", bad_cfg])
    if "Error" in out and "Bye" in out:
        ok("API エラー時も落ちずにエラー表示して続行します")
    else:
        bad("API エラー時の挙動が想定と違います: " + out[-400:])

    # ---- 6b. HTTP エラーの内容が表示される ----
    err_srv = HTTPServer(("127.0.0.1", 0), MockHttpError)
    err_port = err_srv.server_address[1]
    threading.Thread(target=err_srv.serve_forever, daemon=True).start()
    err_cfg = os.path.join(work, "err.json")
    with open(err_cfg, "w", encoding="utf-8") as f:
        json.dump({"llmApiUrl": "http://127.0.0.1:%d/v1/chat/completions" % err_port,
                   "llmApiKey": "k", "model": "m", "allowTools": False,
                   "useSystemProxy": False}, f)
    out = run_tau("hi\n/exit\n", args=["--config", err_cfg])
    if "401" in out:
        ok("HTTP エラー時にステータスコードが表示されます")
    else:
        bad("HTTP ステータスが表示されません: " + out[-400:])
    if "Invalid API key" in out:
        ok("HTTP エラー時にレスポンス本文が表示されます")
    else:
        bad("エラー本文が表示されません: " + out[-400:])
    err_srv.shutdown(); err_srv.server_close()

    # ---- 6c. 途中で切断された応答は黙って確定しない ----
    tr_srv = HTTPServer(("127.0.0.1", 0), MockTruncate)
    tr_port = tr_srv.server_address[1]
    threading.Thread(target=tr_srv.serve_forever, daemon=True).start()
    tr_cfg = os.path.join(work, "tr.json")
    with open(tr_cfg, "w", encoding="utf-8") as f:
        json.dump({"llmApiUrl": "http://127.0.0.1:%d/v1/chat/completions" % tr_port,
                   "llmApiKey": "k", "model": "m", "allowTools": False,
                   "useSystemProxy": False}, f)
    out = run_tau("hi\n/exit\n", args=["--config", tr_cfg])
    if "cut off" in out or "connection was lost" in out:
        ok("途中で切断された応答はエラーとして報告されます")
    else:
        bad("切断が検出されません(途中までの本文を確定してしまう): " + out[-400:])
    tr_srv.shutdown(); tr_srv.server_close()

    # ---- 6d. /new は必ず別のセッションになる ----
    out = run_tau("/new\n/new\n/exit\n")
    ids = [ln.split(":")[-1].strip() for ln in out.splitlines()
           if "Started a new session" in ln]
    if len(ids) == 2 and ids[0] != ids[1]:
        ok("/new で毎回異なるセッションが作られます")
    else:
        bad("/new が同じセッションを開き直しています: %r" % ids)

    # ---- 6c-2. 日本語の入出力 ----
    # 入出力のコードページを食い違わせると Windows で日本語が NUL に化ける。
    # そのため SetupConsoleEncoding() で入出力を揃え、
    # リダイレクト時は入力に触らないこと。
    if "SetupConsoleEncoding" in tls_src:
        ok("コンソールのエンコーディングを一括設定しています")
    else:
        bad("SetupConsoleEncoding がありません")
    # 入力エンコーディングを設定で選べること
    if "inputEncoding" in tls_src and "ResolveEncoding" in tls_src:
        ok("入力エンコーディングを設定で選択できます")
    else:
        bad("入力エンコーディングの設定がありません")
    if "932" in tls_src and "shift-jis" in tls_src:
        ok("Shift-JIS (CP932) を選択できます")
    else:
        bad("Shift-JIS の指定がありません")
    if '"utf-8"' in tls_src:
        ok("UTF-8 を選択できます")
    else:
        bad("UTF-8 の指定がありません")
    if "IsInputRedirected" in tls_src and "return;" in tls_src:
        ok("入力リダイレクト時はエンコーディングを変更しません")
    else:
        bad("リダイレクト時の考慮がありません")
    if "SanitizeInput" in tls_src:
        ok("入力から NUL 文字を取り除く保険があります")
    else:
        bad("NUL 文字の除去処理がありません")
    # NUL で入力が失われたときに黙って再入力待ちにしないこと
    if "CountNul" in tls_src and "input was lost" in tls_src:
        ok("入力が NUL で失われたときに警告します")
    else:
        bad("入力消失時の警告がありません")
    if "character(s) of the input became NUL" in tls_src:
        ok("一部だけ NUL に化けた場合も警告します")
    else:
        bad("部分的な NUL 化の警告がありません")

    # 新しいセッションで日本語を送る(履歴の影響を受けないようにする)。
    # tau は入力そのものをエコーしないので、LLM へ渡った内容で確認する。
    Mock.calls = []
    out = run_tau("/new\n日本語のテストです\n/exit\n")
    sent_jp = ""
    if Mock.calls:
        for m in Mock.calls[-1].get("messages", []):
            if m.get("role") == "user":
                sent_jp = m.get("content") or ""
    if sent_jp == "日本語のテストです":
        ok("日本語のメッセージがそのまま LLM へ渡ります")
    else:
        bad("LLM へ渡った日本語が想定と違います: %r" % sent_jp)
    # 直近のセッションに日本語がそのまま保存されていること
    jp_files = sorted(
        (os.path.join(sdir, f) for f in os.listdir(sdir)
         if f.endswith(".json") and not f.startswith("_")),
        key=os.path.getmtime)
    if jp_files:
        raw = open(jp_files[-1], encoding="utf-8-sig").read()
        if "\\u0000" not in raw and "\x00" not in raw:
            ok("セッションに NUL 文字(\\u0000)が保存されません")
        else:
            bad("セッションに NUL 文字が保存されています")
        data = json.loads(raw)
        users = [m.get("content") for m in data.get("messages", [])
                 if m.get("role") == "user"]
        if users and "日本語" in (users[-1] or ""):
            ok("日本語がそのままセッションに保存されます")
        else:
            bad("日本語が正しく保存されていません: %r" % users)
    else:
        bad("セッションがありません")

    # ---- 6c-3. NUL で入力が失われたら警告する ----
    # Windows で入力エンコーディングが合わないと日本語が NUL になり、
    # 以前は黙って再入力待ちになって「応答が返らない」ように見えた。
    out = run_tau("\x00\x00\x00\x00\n/exit\n")
    if "input was lost" in out:
        ok("入力が NUL で失われたときに警告します")
    else:
        bad("NUL で入力が失われても警告しません: " + out[-400:])
    if "input encoding" in out:
        ok("警告時に現在の入力エンコーディングを表示します")
    else:
        bad("入力エンコーディングが表示されません")

    # 一部だけ NUL の場合も警告し、残りは送信する
    Mock.calls = []
    out = run_tau("\x00abc\n/exit\n")
    if "became NUL" in out:
        ok("一部だけ NUL に化けた場合も警告します")
    else:
        bad("部分的な NUL 化の警告がありません: " + out[-400:])
    sent = ""
    if Mock.calls:
        for m in Mock.calls[-1].get("messages", []):
            if m.get("role") == "user":
                sent = m.get("content") or ""
    if sent == "abc":
        ok("NUL を除いた残りの文字は送信されます")
    else:
        bad("残りの文字が送信されていません: %r" % sent)

    # ---- 6c-4. /config edit で日本語のシステムプロンプトを入力できる ----
    # 以前は NUL で空行扱いになり、即終了して反映されなかった。
    edit_cfg = os.path.join(work, "edit.json")
    with open(edit_cfg, "w", encoding="utf-8") as f:
        json.dump({"llmApiUrl": "http://127.0.0.1:9/v1", "llmApiKey": "k",
                   "model": "m", "allowTools": False, "useSystemProxy": False}, f)
    jp_prompt = "あなたは親切なアシスタントです。日本語で答えてください。"
    out = run_tau("/config edit\n15\n" + jp_prompt + "\n\n/exit\n",
                  args=["--config", edit_cfg])
    if "updated" in out:
        ok("/config edit でシステムプロンプトを変更できます")
    else:
        bad("/config edit が反映されていません: " + out[-400:])
    with open(edit_cfg, encoding="utf-8-sig") as f:
        ed = json.load(f)
    if ed.get("systemPrompt") == jp_prompt:
        ok("/config edit で日本語のシステムプロンプトがそのまま保存されます")
    else:
        bad("日本語のシステムプロンプトが保存されていません: %r"
            % ed.get("systemPrompt"))

    # NUL に化けた行は「空行」と誤判定せず、警告して入力をやり直させる
    with open(edit_cfg, "w", encoding="utf-8") as f:
        json.dump({"llmApiUrl": "http://127.0.0.1:9/v1", "llmApiKey": "k",
                   "model": "m", "allowTools": False, "useSystemProxy": False}, f)
    out = run_tau("/config edit\n15\n\x00\x00\x00\x00\n" + jp_prompt
                  + "\n\n/exit\n", args=["--config", edit_cfg])
    if "became NUL and was lost" in out:
        ok("/config edit で NUL 化した行を空行扱いにしません")
    else:
        bad("/config edit で NUL 行が空行扱いになっています: " + out[-400:])
    with open(edit_cfg, encoding="utf-8-sig") as f:
        ed2 = json.load(f)
    if ed2.get("systemPrompt") == jp_prompt:
        ok("NUL 行のあとに入力した日本語が反映されます")
    else:
        bad("NUL 行後の入力が反映されていません: %r" % ed2.get("systemPrompt"))

    # 連続して失われる場合は打ち切る
    out = run_tau("\x00\x00\x00\n\x00\x00\x00\n")
    if "lost 2 times in a row" in out:
        ok("連続して入力が失われたら打ち切って案内します")
    else:
        bad("連続 NUL で打ち切っていません: " + out[-400:])

    # ---- 6c-5. 入力エンコーディングの設定 ----
    ie_cfg = os.path.join(work, "ie.json")
    with open(ie_cfg, "w", encoding="utf-8") as f:
        json.dump({"llmApiUrl": "http://127.0.0.1:9/v1", "llmApiKey": "k",
                   "model": "m", "allowTools": False, "useSystemProxy": False}, f)
    out = run_tau("/config\n/exit\n", args=["--config", ie_cfg])
    if "inputEncoding" in out:
        ok("/config に inputEncoding が表示されます")
    else:
        bad("/config に inputEncoding がありません: " + out[-400:])

    for name in ("shift-jis", "utf-8", "default"):
        out = run_tau("/set inputEncoding " + name + "\n/exit\n",
                      args=["--config", ie_cfg])
        with open(ie_cfg, encoding="utf-8-sig") as f:
            saved_ie = json.load(f).get("inputEncoding")
        if saved_ie == name:
            ok("/set inputEncoding %s が保存されます" % name)
        else:
            bad("/set inputEncoding %s が保存されていません: %r" % (name, saved_ie))

    # 別名も受け付ける
    out = run_tau("/set inputEncoding cp932\n/exit\n", args=["--config", ie_cfg])
    with open(ie_cfg, encoding="utf-8-sig") as f:
        saved_ie = json.load(f).get("inputEncoding")
    if saved_ie == "shift-jis":
        ok("/set inputEncoding cp932 は shift-jis として扱われます")
    else:
        bad("cp932 の別名が効いていません: %r" % saved_ie)

    # 不正な値は拒否
    out = run_tau("/set inputEncoding bogus\n/exit\n", args=["--config", ie_cfg])
    if "Invalid value" in out:
        ok("/set inputEncoding は不正な値を拒否します")
    else:
        bad("/set inputEncoding の入力チェックがありません")

    # /config edit の選択肢
    out = run_tau("/config edit\n16\n1\n/exit\n", args=["--config", ie_cfg])
    with open(ie_cfg, encoding="utf-8-sig") as f:
        saved_ie = json.load(f).get("inputEncoding")
    if "choices: shift-jis / utf-8 / default" in out and saved_ie == "shift-jis":
        ok("/config edit で選択肢から入力エンコーディングを選べます")
    else:
        bad("/config edit の選択肢が想定と違います: %r" % saved_ie)

    # ---- 6c-6. Markdown の整形表示 ----
    md_srv = HTTPServer(("127.0.0.1", 0), MockMarkdown)
    md_port = md_srv.server_address[1]
    threading.Thread(target=md_srv.serve_forever, daemon=True).start()
    md_cfg = os.path.join(work, "md.json")
    with open(md_cfg, "w", encoding="utf-8") as f:
        json.dump({"llmApiUrl": "http://127.0.0.1:%d/v1/chat/completions" % md_port,
                   "llmApiKey": "k", "model": "m", "allowTools": False,
                   "useSystemProxy": False}, f)
    out = run_tau("テスト\n/exit\n", args=["--config", md_cfg])
    md_srv.shutdown(); md_srv.server_close()

    # 見出し記号が消えていること
    if "# Heading1" not in out and "Heading1" in out:
        ok("見出しの # が除去されて表示されます")
    else:
        bad("見出しが Markdown のまま出力されています")
    # 太字・斜体の記号が消えていること
    if "**bold**" not in out and "bold" in out:
        ok("**太字** の記号が除去されて表示されます")
    else:
        bad("太字が Markdown のまま出力されています")
    if "`code`" not in out and "code" in out:
        ok("インラインコードの ` が除去されて表示されます")
    else:
        bad("インラインコードが Markdown のまま出力されています")
    # 表がボーダー文字で整形されていること
    if "┌" in out and "┬" in out and "└" in out and "│" in out:
        ok("表がボーダー文字(罫線)で整形されます")
    else:
        bad("表が罫線で整形されていません: " + out[-500:])
    if "|:-----|" not in out and "| Name |" not in out:
        ok("表の Markdown 記法がそのまま出力されません")
    else:
        bad("表が Markdown のまま出力されています")
    # 箇条書きが「・」になること
    if "・item1" in out:
        ok("箇条書きが・で表示されます")
    else:
        bad("箇条書きが整形されていません")
    # コードブロックが枠付きで表示されること
    if "┌ python" in out or "┌─ python" in out:
        ok("コードブロックが枠付きで表示されます")
    else:
        bad("コードブロックが枠付きになっていません")
    # 引用が縦線で表示されること
    if "│ quoted text" in out:
        ok("引用が縦線で表示されます")
    else:
        bad("引用が整形されていません")
    # リンクが text (url) 形式になること
    if "[link](https://example.com)" not in out and "link" in out \
       and "https://example.com" in out:
        ok("リンクが text (url) 形式で表示されます")
    else:
        bad("リンクが整形されていません")
    # 水平線が罫線になること
    # 長い罫線(40 桁以上)は水平線の整形結果だけ
    rule = [ln for ln in out.split("\n")
            if len(ln.strip()) >= 40 and set(ln.strip()) == set("─")]
    if rule:
        ok("水平線が罫線で表示されます")
    else:
        bad("水平線が整形されていません")

    # ---- 6c-7. 長い日本語の入力 ----
    # 300 文字の日本語を入力しても、応答が返り、内容が欠けないこと
    long_cfg = os.path.join(work, "long.json")
    with open(long_cfg, "w", encoding="utf-8") as f:
        json.dump({"llmApiUrl": "http://127.0.0.1:%d/v1/chat/completions" % md_port,
                   "llmApiKey": "k", "model": "m", "allowTools": False,
                   "useSystemProxy": False}, f)
    long_text = "あいうえお" * 60       # 300 文字
    out = run_tau(long_text + "\n/exit\n", args=["--config", long_cfg])
    if "了解しました" in out or "単語" in out or "Heading1" in out:
        ok("長い日本語(300 文字)でも応答が表示されます")
    else:
        bad("長い日本語で応答がありません: " + out[-300:])
    # 保存された内容が欠けていないこと
    # セッションは TauDataDir()/sessions に保存される(--config とは別)
    sess = sorted(glob.glob(os.path.join(work, "config", "tau", "sessions", "*.json")))
    if not sess:
        sess = sorted(glob.glob(os.path.join(work, ".config", "tau", "sessions", "*.json")))
    if not sess:
        sess = sorted(glob.glob(os.path.join(os.path.expanduser("~"),
                                             ".config", "tau", "sessions", "*.json")))
    saved = None
    for fn in sess:
        try:
            d = json.load(open(fn, encoding="utf-8-sig"))
            for m in d.get("messages", []):
                if m.get("role") == "user" and len(m.get("content") or "") > 100:
                    saved = m["content"]
        except Exception:
            pass
    if saved is not None and len(saved) >= len(long_text) - 10:
        ok("長い日本語の入力が欠けずに保存されます (%d 文字)" % len(saved))
    else:
        bad("長い入力が欠けています: %r" % (saved[:80] if saved else None))

    # ストリーミング中に進行状況が表示されること(無言にならない)
    if "receiving" in tls_src:
        ok("受信中の進行状況が表示されます")
    else:
        bad("進行状況の表示がありません")

    # ---- 6c-8. 入力段階の診断 ----
    if "--version" in tls_src and "BuildStamp" in tls_src:
        ok("--version でビルド識別子を確認できます")
    else:
        bad("--version がありません")
    out = run_tau("", args=["--version"])
    if "tau " in out and "exe" in out:
        ok("--version がビルド情報を表示します")
    else:
        bad("--version の出力が不正: " + out[:200])
    # 入力の読み取りで例外が出たら黙って終了しないこと
    if "Failed to read input" in tls_src:
        ok("入力の読み取り失敗が表示されます(無言で終了しない)")
    else:
        bad("入力の読み取り失敗が無言で握りつぶされます")
    # 入力待ちが分かること(TAU_DEBUG)
    if "waiting for input" in tls_src:
        ok("TAU_DEBUG=1 で入力待ちが表示されます")
    else:
        bad("入力待ちの表示がありません")
    # Windows では自前の行編集を既定で使わない(ReadKey が壊れるため)
    if "GetConsoleCP" in tls_src:
        ok("Windows の実際のコードページを確認します")
    else:
        bad("コンソール コードページの確認がありません")

    # ---- 6d-2. 出力の種類が変わったら改行される ----
    multi_srv = HTTPServer(("127.0.0.1", 0), MockMulti)
    multi_port = multi_srv.server_address[1]
    threading.Thread(target=multi_srv.serve_forever, daemon=True).start()
    multi_cfg = os.path.join(work, "multi.json")
    with open(multi_cfg, "w", encoding="utf-8") as f:
        json.dump({"llmApiUrl": "http://127.0.0.1:%d/v1/chat/completions" % multi_port,
                   "llmApiKey": "k", "model": "m", "allowTools": True,
                   "emitReasoning": True, "useSystemProxy": False}, f)
    out = run_tau("テスト\n/exit\n", args=["--config", multi_cfg])
    multi_srv.shutdown(); multi_srv.server_close()

    # "[thinking] ..." "▶ Tool: ..." "最終的な回答です。" が
    # それぞれ独立した行になっていること(前の出力に連結していないこと)
    lines = out.splitlines()
    think_lines = [l for l in lines if "[thinking]" in l]
    tool_lines = [l for l in lines if "▶ Tool:" in l]
    reply_lines = [l for l in lines if "最終的な回答です。" in l]
    if len(think_lines) >= 2 and len(tool_lines) >= 2 and reply_lines:
        ok("思考・ツール実行・本文がそれぞれ表示されます")
    else:
        bad("出力が不足しています: think=%d tool=%d reply=%d"
            % (len(think_lines), len(tool_lines), len(reply_lines)))

    # 1 行の中に複数の種類が混ざっていないこと
    mixed = [l for l in lines
             if ("[thinking]" in l and "▶" in l)
             or ("[thinking]" in l and "最終的な回答です。" in l)
             or ("▶" in l and "最終的な回答です。" in l)]
    if not mixed:
        ok("種類の違う出力が同じ行に連結されません")
    else:
        bad("改行されず連結しています: %r" % mixed[:3])

    # 本文は "[thinking]" や "▶" の行に続けて書かれていないこと
    if reply_lines and ("[thinking]" not in reply_lines[0] and "▶" not in reply_lines[0]):
        ok("本文は単独の行から始まります")
    else:
        bad("本文が前の出力に連結しています: %r" % reply_lines[:2])

    # ---- 6e. 設定コマンド ----
    out = run_tau("/config\n/exit\n")
    if "llmApiUrl" in out and "contextWindow" in out and "allowTools" in out:
        ok("/config が設定一覧を表示します")
    else:
        bad("/config の出力が想定と違います: " + out[-400:])
    # sk-test は 7 文字なので全体が伏せ字(*******)になる
    if "sk-test" not in out and "llmApiKey          = ***" in out:
        ok("API キーは伏せ字で表示されます")
    else:
        bad("API キーが伏せ字になっていません: " + out[-400:])

    out = run_tau("/set contextWindow 128000\n/set allowTools false\n/exit\n")
    if "128000" in out and "false" in out:
        ok("/set で数値と真偽値を変更できます")
    else:
        bad("/set が効いていません: " + out[-400:])
    with open(cfg_path, encoding="utf-8-sig") as f:
        saved = json.load(f)
    if saved.get("contextWindow") == 128000 and saved.get("allowTools") is False:
        ok("/set の変更が tau.json に保存されます")
    else:
        bad("/set の変更が保存されていません: %r"
            % {k: saved.get(k) for k in ("contextWindow", "allowTools")})
    if "models" not in saved:
        ok("tau.json に models は保存されません")
    else:
        bad("tau.json に models が保存されています: %r" % saved.get("models"))

    # 旧形式(models あり)の tau.json でも、保存時に models が取り除かれること
    legacy_cfg = os.path.join(work, "legacy.json")
    with open(legacy_cfg, "w", encoding="utf-8") as f:
        json.dump({"llmApiUrl": "http://127.0.0.1:9/v1", "llmApiKey": "k",
                   "model": "m1", "models": ["m1", "m2"],
                   "allowTools": False, "useSystemProxy": False,
                   "myCustomKey": "keep-me"}, f, ensure_ascii=False)
    run_tau("/model legacy-switch\n/exit\n", args=["--config", legacy_cfg])
    with open(legacy_cfg, encoding="utf-8-sig") as f:
        lg = json.load(f)
    if "models" not in lg:
        ok("旧 tau.json の models は保存時に取り除かれます")
    else:
        bad("旧 tau.json の models が残っています: %r" % lg.get("models"))
    if lg.get("myCustomKey") == "keep-me":
        ok("手で追記した未知のキーは保持されます")
    else:
        bad("未知のキーが失われました: %r" % lg.get("myCustomKey"))

    out = run_tau("/set bogus 1\n/set contextWindow abc\n/exit\n")
    if "Unknown setting" in out and "Invalid value" in out:
        ok("/set は不正な key / 値を拒否します")
    else:
        bad("/set の入力チェックがありません")
    # models オプションは廃止した
    out = run_tau("/set models a,b\n/exit\n")
    if "Unknown setting" in out:
        ok("models オプションは存在しません(廃止済み)")
    else:
        bad("models オプションがまだ残っています: " + out[-400:])
    out = run_tau("/config\n/exit\n")
    if "models" not in out:
        ok("/config に models が表示されません")
    else:
        bad("/config に models が表示されています")

    out = run_tau("/config edit\n3\nedited-model\n/exit\n")
    if "edited-model" in out:
        ok("/config edit で対話形式の変更ができます")
    else:
        bad("/config edit が効いていません: " + out[-500:])
    with open(cfg_path, encoding="utf-8-sig") as f:
        saved = json.load(f)
    if saved.get("model") == "edited-model":
        ok("/config edit の変更が保存されます")
    else:
        bad("/config edit の変更が保存されていません: %r" % saved.get("model"))

    # agentweb 側の設定は一切変更されていないこと
    aw_after = open(aw_cfg_path, encoding="utf-8").read()
    if aw_after == aw_before:
        ok("tau の設定変更で agentweb.json は変更されません")
    else:
        bad("agentweb.json が変更されました")

    # ---- 6f. セッションの削除 ----
    out = run_tau("/sessions\n/exit\n")
    if "/delete" in out and "current session" in out:
        ok("/sessions に削除方法の案内が出ます")
    else:
        bad("/sessions に案内がありません: " + out[-300:])

    # 追加で 2 セッション作り、番号指定で削除する
    run_tau("/new\n/exit\n")
    run_tau("/new\n/exit\n")
    before = [f for f in os.listdir(sdir) if f.endswith(".json") and not f.startswith("_")]
    if len(before) >= 3:
        ok("削除テスト用に複数セッションがあります (%d 件)" % len(before))
    else:
        bad("セッションが足りません: %d 件" % len(before))

    out = run_tau("/delete 1\nn\n/exit\n")
    after = [f for f in os.listdir(sdir) if f.endswith(".json") and not f.startswith("_")]
    if "Cancelled" in out and len(after) == len(before):
        ok("/delete で n を選ぶと削除されません")
    else:
        bad("中止が効いていません: %r -> %r" % (before, after))

    # 表示中のセッション以外を消す。表示中を消すと代替のセッションが作られて
    # 件数が変わらないため、lastSession 以外の ID を対象にする。
    ids = []
    for f in sorted(os.listdir(sdir)):
        if f.endswith(".json") and not f.startswith("_"):
            ids.append(f[:-5])
    last = ""
    st_path2 = os.path.join(sdir, "_state.json")
    if os.path.exists(st_path2):
        with open(st_path2, encoding="utf-8-sig") as f:
            last = json.load(f).get("lastSession", "")
    target = ""
    for i in ids:
        if i != last:
            target = i
            break
    if not target:
        bad("削除対象のセッションを選べませんでした")
        return 1
    out = run_tau("/delete " + target + "\ny\n/exit\n")
    after = [f for f in os.listdir(sdir) if f.endswith(".json") and not f.startswith("_")]
    if "Deleted" in out and target not in [f[:-5] for f in after] \
       and len(after) == len(before) - 1:
        ok("/delete で y を選ぶとセッションが削除されます")
    else:
        bad("削除が効いていません: target=%r %d -> %d" % (target, len(before), len(after)))

    out = run_tau("/delete\n/exit\n")
    if "Specify the session to delete" in out:
        ok("/delete を引数なしで実行すると使い方を表示します")
    else:
        bad("/delete の引数チェックがありません")

    out = run_tau("/delete 99\n/exit\n")
    if "No session with that number" in out:
        ok("/delete は存在しない番号を拒否します")
    else:
        bad("/delete の番号チェックがありません: " + out[-300:])

    # ---- 6g. /clear はセッションの内容をクリアする ----
    run_tau("/new\nclear-test\n/exit\n")   # 会話を 1 つ作る
    clr_files = sorted(
        (os.path.join(sdir, f) for f in os.listdir(sdir)
         if f.endswith(".json") and not f.startswith("_")),
        key=os.path.getmtime)
    if not clr_files:
        bad("/clear のテスト用セッションがありません")
        return 1
    clr_file = clr_files[-1]
    with open(clr_file, encoding="utf-8-sig") as f:
        before_msgs = len(json.load(f).get("messages", []))
    if before_msgs > 0:
        ok("/clear のテスト用セッションに会話があります (%d 件)" % before_msgs)
    else:
        bad("/clear のテスト用セッションが空です")

    # n を選ぶとクリアされない
    out = run_tau("/clear\nn\n/exit\n")
    with open(clr_file, encoding="utf-8-sig") as f:
        after_n = len(json.load(f).get("messages", []))
    if "Cancelled" in out and after_n == before_msgs:
        ok("/clear で n を選ぶとクリアされません")
    else:
        bad("/clear の中止が効いていません: %d -> %d" % (before_msgs, after_n))

    # y を選ぶと履歴が消える
    out = run_tau("/clear\ny\n/exit\n")
    with open(clr_file, encoding="utf-8-sig") as f:
        data = json.load(f)
    if "Cleared" in out and len(data.get("messages", [])) == 0:
        ok("/clear でセッションの履歴がクリアされます")
    else:
        bad("/clear が効いていません: %r" % data.get("messages"))
    if data.get("summary") == "" and data.get("contextStart") == 0:
        ok("/clear で要約とコンテキスト位置もリセットされます")
    else:
        bad("/clear が要約/位置をリセットしていません: %r/%r"
            % (data.get("summary"), data.get("contextStart")))
    # セッション自体(ID・タイトル)は残る
    if data.get("id") and data.get("title"):
        ok("/clear はセッション自体(ID・タイトル)を残します")
    else:
        bad("/clear でセッションが失われました: %r" % {k: data.get(k)
            for k in ("id", "title")})

    # 空のセッションで /clear しても安全
    out = run_tau("/clear\n/exit\n")
    if "no messages yet" in out:
        ok("空のセッションで /clear しても安全です")
    else:
        bad("/clear の空セッション処理が想定と違います: " + out[-300:])

    # ---- 6h. Emacs キーバインド(行編集) ----
    if "class LineEditor" in tls_src:
        ok("Emacs 風の行編集(LineEditor)が実装されています")
    else:
        bad("LineEditor がありません")
    for key in ("Ctrl-H", "Ctrl-A", "Ctrl-E", "Ctrl-K", "Ctrl-U", "Ctrl-W",
                "Ctrl-Y", "Ctrl-T", "Alt-B", "Alt-F"):
        # キーは ConsoleKey 名で判定する
        pass
    # 対応キーの実装を確認
    need = ["ConsoleKey.A", "ConsoleKey.E", "ConsoleKey.B", "ConsoleKey.F",
            "ConsoleKey.K", "ConsoleKey.U", "ConsoleKey.W", "ConsoleKey.Y",
            "ConsoleKey.T", "ConsoleKey.H"]
    missing = [k for k in need if k not in tls_src]
    if not missing:
        ok("Ctrl-A/E/B/F/K/U/W/Y/T/H が実装されています")
    else:
        bad("未実装のキーがあります: %r" % missing)
    if "WordStart" in tls_src and "WordEnd" in tls_src:
        ok("Alt-B / Alt-F の単語移動が実装されています")
    else:
        bad("単語移動がありません")
    # Shift-JIS では ReadKey が壊れるため、行編集は UTF-8 のときだけ有効
    if "CodePage == 65001" in tls_src and "enabled" in tls_src:
        ok("行編集は UTF-8 入力のときだけ有効になります")
    else:
        bad("エンコーディングによる行編集の切り替えがありません")
    # リダイレクト時は従来の ReadLine にフォールバックする
    if "Console.ReadLine()" in tls_src:
        ok("リダイレクト時は ReadLine にフォールバックします")
    else:
        bad("フォールバックがありません")
    # 強制切り替えオプション
    if "--line-editor" in tls_src and "--no-line-editor" in tls_src:
        ok("--line-editor / --no-line-editor で切り替えられます")
    else:
        bad("行編集の切り替えオプションがありません")

    # ---- 7. TLS 設定 ----
    # .NET Framework 4.7 未満では既定が TLS 1.0 相当になり、
    # TLS 1.2 以上を要求する API で
    #   「SSL/TLS のセキュリティで保護されているチャネルを作成できませんでした」
    # になる。EnsureTls() が SystemDefault / Tls12 以上を設定していることを確認する。
    if "ApplyTls" in tls_src and "ServicePointManager.SecurityProtocol" in tls_src:
        ok("TLS バージョンを設定する処理(ApplyTls)があります")
    else:
        bad("ApplyTls がありません")
    if "SecurityProtocolType.Tls12" in tls_src and "SystemDefault" not in tls_src.split("public static void ApplyTls")[1].split("public static")[0]:
        ok("ApplyTls は SystemDefault を使わず Tls12 に固定しています")
    else:
        bad("ApplyTls が SystemDefault を使っています")
    if "SecurityProtocolType.Tls12" in tls_src:
        ok("第 2 段で TLS 1.2 を明示するフォールバックがあります")
    else:
        bad("TLS 1.2 のフォールバックがありません")
    if "EscalateTls" in tls_src and "IsTlsFailure" in tls_src:
        ok("TLS 失敗を検出して一段上げる処理があります")
    else:
        bad("TLS 失敗時のエスカレーションがありません")
    if "SecureChannelFailure" in tls_src:
        ok("SecureChannelFailure を TLS 失敗として扱います")
    else:
        bad("SecureChannelFailure の判定がありません")
    # Tls12 に Tls13 を混ぜると「SslProtocolType で有効ではありません」になるため、
    # Tls12 を単独で指定していること。
    if "SecurityProtocolType.Tls12 |" not in tls_src and "| (SecurityProtocolType)t13" not in tls_src:
        ok("TLS 1.3 を OR で混ぜていません(Tls12 単独指定)")
    else:
        bad("Tls12 に Tls13 を混ぜています(無効なプロトコル指定になる)")
    if "if (tlsApplied) return false;" in tls_src:
        ok("TLS 適用済みなら再試行しない(無限再試行しない)")
    else:
        bad("再試行の段数制御がありません")
    # 実測された症状: Status=ReceiveFailure + 内部例外
    # 「指定された値は 'SslProtocolType' 列挙で有効ではありません」
    # も TLS の設定不備として再試行対象にする必要がある。
    if "SslProtocol" in tls_src and "ArgumentException" in tls_src:
        ok("SslProtocolType の無効値エラーも TLS 失敗として扱います")
    else:
        bad("SslProtocolType エラーの判定がありません")
    if "TrustFailure" in tls_src:
        ok("証明書エラー(TrustFailure)は再試行対象外です")
    else:
        bad("TrustFailure の除外がありません")
    # SystemDefault(0) は列挙値として存在しても OS 既定が TLS1.0 のままになる環境があるため、
    # 「存在するから使う」で終わらせず、失敗時に TLS 1.2 へ上がることを確認する。
    if tls_src.count("CallStreamOnce") >= 2 and tls_src.count("CallOnceInternal") >= 2:
        ok("ストリーミング/非ストリーミングの両方で再試行します")
    else:
        bad("再試行の実装が足りません")
    if tls_src.count("ApplyTls()") >= 2:
        ok("LLM 呼び出しと curl ツールの両方で TLS 設定を適用しています")
    else:
        bad("TLS 設定の適用箇所が足りません: %d" % tls_src.count("ApplyTls()"))

    # ---- 7a. 未使用の定数が無いこと ----
    # KIND_* は出力の種類。使われていない定数があるとコンパイラ警告になる。
    for name in ("KIND_NONE", "KIND_THINK", "KIND_CONTENT"):
        if tls_src.count(name) >= 2:
            ok("%s が使用されています" % name)
        else:
            bad("%s が宣言だけで使われていません" % name)
    if "KIND_TOOL" not in tls_src:
        ok("未使用の KIND_TOOL は削除されています")
    else:
        bad("未使用の KIND_TOOL が残っています")

    # ---- 7b. デバッグ機能 ----
    if "--diag" in tls_src and "class Diagnostics" in tls_src:
        ok("接続診断(--diag)が実装されています")
    else:
        bad("--diag が実装されていません")
    if "TAU_DEBUG" in tls_src and "public static void Dbg" in tls_src:
        ok("デバッグ出力(TAU_DEBUG / --debug)が実装されています")
    else:
        bad("デバッグ出力が実装されていません")
    if "ExceptionChain" in tls_src:
        ok("例外の連鎖を表示できます")
    else:
        bad("例外の連鎖表示がありません")

    # ---- 8. --help ----
    out = run_tau("", args=["--help"])
    if "/model" in out and "/sessions" in out:
        ok("--help にコマンド一覧が表示されます")
    else:
        bad("--help の内容が想定と違います")
    if "--diag" in out and "--debug" in out:
        ok("--help に --diag / --debug が表示されます")
    else:
        bad("--help に診断オプションがありません")

    # ---- 9. --diag が動作する ----
    out = run_tau("", args=["--diag"], timeout=120)
    if "connection diagnostic" in out and "TLS handshake test" in out:
        ok("--diag が接続診断を実行します")
    else:
        bad("--diag が動作しません: " + out[-400:])
    # HEAD を使うと HEAD 非対応サーバで 501 になり、TLS が使えるのに NG と誤表示する。
    # TCP + TLS ハンドシェイクのみで判定すること。
    if "ProbeTls" in tls_src and "AuthenticateAsClient" in tls_src:
        ok("TLS 試験は TCP + ハンドシェイクのみで判定します")
    else:
        bad("TLS 試験が HTTP メソッドに依存しています")
    if 'req.Method = "HEAD"' not in tls_src:
        ok("TLS 試験に HEAD を使っていません")
    else:
        bad("TLS 試験に HEAD を使っています(501 で誤判定になる)")
    if "ストリーミング呼び出し試験" in tls_src:
        ok("--diag が対話と同じストリーミング経路も試験します")
    else:
        bad("ストリーミング経路の試験がありません")
    if "IsBypassed" in tls_src:
        ok("プロキシ表示が直接接続かどうかを区別します")
    else:
        bad("プロキシ表示が誤解を招く形式のままです")
    if "IsRetryable" in tls_src:
        ok("再試行すべき失敗かを判定しています")
    else:
        bad("再試行の判定がありません")
    if "MAX_ATTEMPTS" in tls_src and "Thread.Sleep" in tls_src:
        ok("一時的な失敗をバックオフ付きで再試行します")
    else:
        bad("再試行の回数/待機がありません")
    if "ProtocolError" in tls_src and "TrustFailure" in tls_src:
        ok("HTTP エラーと証明書エラーは再試行しません")
    else:
        bad("再試行除外の判定がありません")
    if "runtime=" in out and "SecurityProtocol=" in out:
        ok("--diag が環境情報を表示します")
    else:
        bad("--diag の環境情報がありません")

    srv.shutdown(); srv.server_close()
    shutil.rmtree(work, ignore_errors=True)

    print()
    print("結果: " + ("すべて OK" if not fail else "FAIL=%d :: %s" % (len(fail), " / ".join(fail))))
    return 1 if fail else 0

if __name__ == "__main__":
    sys.exit(main())
