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
import json, os, shutil, socket, subprocess, sys, tempfile, threading, time
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
    exe = os.path.join(tempfile.mkdtemp(prefix="tau_build_"), "tau.exe")
    print("=== tau 実行時回帰テスト ===")
    rc = subprocess.run(["mcs", "-langversion:5", "-out:" + exe, SRC],
                        capture_output=True, text=True)
    if rc.returncode != 0:
        bad("tau.exe のビルドに失敗しました:\n" + rc.stdout + rc.stderr)
        return 1
    ok("tau.exe をビルドしました")

    # agentweb と同じ場所を使う(テスト用に HOME を差し替える)
    work = tempfile.mkdtemp(prefix="tau_home_")
    appdata = os.path.join(work, ".config", "agentweb")
    os.makedirs(appdata, exist_ok=True)

    srv = HTTPServer(("127.0.0.1", 0), Mock)
    port = srv.server_address[1]
    threading.Thread(target=srv.serve_forever, daemon=True).start()

    cfg_path = os.path.join(appdata, "agentweb.json")
    with open(cfg_path, "w", encoding="utf-8") as f:
        json.dump({
            "llmApiUrl": "http://127.0.0.1:%d/v1/chat/completions" % port,
            "llmApiKey": "sk-test",
            "model": "mock-model",
            "models": ["mock-model", "other-model"],
            "allowTools": True,
            "emitReasoning": True,
            "contextWindow": 64000,
            # agentweb 専用の設定。tau が保存しても失われないこと。
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
        ok("agentweb の設定ファイル(agentweb.json)を参照しています")
    else:
        bad("設定ファイルのパスが想定と違います: " + out[:400])

    # ---- 2. セッションが agentweb と同じ形式で保存される ----
    sdir = os.path.join(appdata, "sessions")
    files = [f for f in os.listdir(sdir) if f.endswith(".json") and not f.startswith("_")]
    if files:
        ok("セッションが %s に保存されます" % sdir)
    else:
        bad("セッションが保存されていません")
        return 1

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
    if "これまでの会話" in out and "完了です" in out:
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
    if "ツール実行: off" in out and "ツール実行: on" in out:
        ok("/tools でツールの on/off を切り替えられます")
    else:
        bad("/tools が効いていません")
    with open(cfg_path, encoding="utf-8-sig") as f:
        saved = json.load(f)
    if saved.get("model") == "other-model":
        ok("モデルの切り替えが agentweb の設定ファイルに保存されます")
    else:
        bad("モデルが保存されていません: %r" % saved.get("model"))
    # agentweb 専用のキーが tau の保存で失われないこと
    kept = {"port": 8787, "theme": "light", "bindPath": ".",
            "openBrowserOnStart": False}
    missing = [k for k, v in kept.items() if saved.get(k) != v]
    if not missing:
        ok("agentweb 専用の設定(port/theme/bindPath/openBrowserOnStart)が保持されます")
    else:
        bad("agentweb 専用の設定が失われました: %r" % missing)

    # ---- 6. API エラーで落ちない ----
    bad_cfg = os.path.join(work, "bad.json")
    with open(bad_cfg, "w", encoding="utf-8") as f:
        json.dump({"llmApiUrl": "http://127.0.0.1:9/v1/chat/completions",
                   "llmApiKey": "k", "model": "m", "allowTools": False}, f)
    out = run_tau("こんにちは\n/exit\n", args=["--config", bad_cfg])
    if "エラー" in out and "終了します" in out:
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
    if "切断" in out:
        ok("途中で切断された応答はエラーとして報告されます")
    else:
        bad("切断が検出されません(途中までの本文を確定してしまう): " + out[-400:])
    tr_srv.shutdown(); tr_srv.server_close()

    # ---- 6d. /new は必ず別のセッションになる ----
    out = run_tau("/new\n/new\n/exit\n")
    ids = [ln.split(":")[-1].strip() for ln in out.splitlines()
           if "新しいセッションを開始しました" in ln]
    if len(ids) == 2 and ids[0] != ids[1]:
        ok("/new で毎回異なるセッションが作られます")
    else:
        bad("/new が同じセッションを開き直しています: %r" % ids)

    # ---- 7. TLS 設定 ----
    # .NET Framework 4.7 未満では既定が TLS 1.0 相当になり、
    # TLS 1.2 以上を要求する API で
    #   「SSL/TLS のセキュリティで保護されているチャネルを作成できませんでした」
    # になる。EnsureTls() が SystemDefault / Tls12 以上を設定していることを確認する。
    tls_src = open(SRC, encoding="utf-8").read()
    if "ApplyTls" in tls_src and "ServicePointManager.SecurityProtocol" in tls_src:
        ok("TLS バージョンを設定する処理(ApplyTls)があります")
    else:
        bad("ApplyTls がありません")
    if "SystemDefault" in tls_src:
        ok("第 1 段で OS に任せる SystemDefault を試します")
    else:
        bad("SystemDefault を使っていません")
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
    if "tlsStage < 2" in tls_src:
        ok("エスカレーションが 3 段階(無限再試行しない)")
    else:
        bad("エスカレーションの段数制御がありません")
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

    # ---- 8. --help ----
    out = run_tau("", args=["--help"])
    if "/model" in out and "/sessions" in out:
        ok("--help にコマンド一覧が表示されます")
    else:
        bad("--help の内容が想定と違います")

    srv.shutdown(); srv.server_close()
    shutil.rmtree(work, ignore_errors=True)

    print()
    print("結果: " + ("すべて OK" if not fail else "FAIL=%d :: %s" % (len(fail), " / ".join(fail))))
    return 1 if fail else 0

if __name__ == "__main__":
    sys.exit(main())
