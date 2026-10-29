#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""test_e2e.py - 実行時回帰テスト (Linux / macOS / WSL, mono + mcs が必要)

モック LLM サーバを立てて実際に aw.exe を起動し、実行時にしか検出できない不具合を検査する。
特に「同名オーバーロードの相互再帰 → StackOverflowException」はコンパイルでは分からないため、
実際に 1 ターン走らせて落ちないことを確認する。

検証項目:
  1. ストリーミング + reasoning が SSE で届き、セッションに think が保存される
  2. ツール呼び出しループが動き、steps と think が保存される
  3. 非ストリーミング /api/chat でも reasoning が保存される
  4. think / steps / ts が LLM API へ漏れていない

使い方:
    python3 test_e2e.py
    python3 test_e2e.py --keep     # 一時ディレクトリを残す
"""

import http.server
import json
import os
import shutil
import socket
import socketserver
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
CS_PATH = os.path.join(HERE, "AgentWeb.cs")

oks = []
fails = []


def ok(msg):
    oks.append(msg)
    print("[ OK ] " + msg)


def fail(msg):
    fails.append(msg)
    print("[FAIL] " + msg)


# --------------------------------------------------------------------------
#  モック LLM (OpenAI 互換)
# --------------------------------------------------------------------------

def _chunk(delta, finish=None):
    return {"id": "c", "object": "chat.completion.chunk", "created": 1, "model": "mock",
            "choices": [{"index": 0, "delta": delta, "finish_reason": finish}]}


class _Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    calls = 0
    leaks = []
    plain = False          # True なら非ストリーミングの JSON を返す
    lock = threading.Lock()

    def log_message(self, *a):
        pass

    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(n).decode("utf-8", "replace")
        try:
            req = json.loads(body or "{}")
        except ValueError:
            req = {}
        with _Handler.lock:
            _Handler.calls += 1
            seq = _Handler.calls
        for m in (req.get("messages") or []):
            if isinstance(m, dict):
                bad = [k for k in ("think", "steps", "ts") if k in m]
                if bad:
                    _Handler.leaks.append(sorted(bad))

        if self.plain:
            out = {"id": "c", "object": "chat.completion", "model": "mock",
                   "choices": [{"index": 0, "message": {
                       "role": "assistant",
                       "reasoning_content": "非ストリームの思考。",
                       "content": "非ストリーム回答。"}, "finish_reason": "stop"}],
                   "usage": {"total_tokens": 42, "prompt_tokens": 20}}
            b = json.dumps(out, ensure_ascii=False).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(b)))
            self.end_headers()
            self.wfile.write(b)
            self.wfile.flush()
            return

        if seq == 1:      # 1 回目はツール呼び出しを促す
            chunks = [
                _chunk({"role": "assistant"}),
                _chunk({"reasoning_content": "ツールを使うぞ。"}),
                _chunk({"tool_calls": [{"index": 0, "id": "call_1", "type": "function",
                                        "function": {"name": "list_dir",
                                                     "arguments": "{\"path\":\".\"}"}}]}),
                _chunk({}, "tool_calls"),
            ]
        else:
            chunks = [
                _chunk({"role": "assistant"}),
                _chunk({"reasoning_content": "結果をまとめる。"}),
                _chunk({"content": "完了しました。"}),
                _chunk({}, "stop"),
            ]
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Connection", "close")
        self.end_headers()
        for c in chunks:
            self.wfile.write(("data: " + json.dumps(c, ensure_ascii=False) + "\n\n").encode("utf-8"))
            self.wfile.flush()
        self.wfile.write(b"data: [DONE]\n\n")
        self.wfile.flush()


class _SlowHandler(http.server.BaseHTTPRequestHandler):
    """ゆっくりトークンを返すモック。生成中の途中経過の保存を検証するために使う。"""
    protocol_version = "HTTP/1.1"
    hits = []

    def log_message(self, *a):
        pass

    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        req = json.loads(self.rfile.read(n).decode("utf-8", "replace") or "{}")
        _SlowHandler.hits.append(req)
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Connection", "close")
        self.end_headers()

        def chunk(delta, fin=None):
            c = {"id": "c", "object": "chat.completion.chunk", "created": 1, "model": "mock",
                 "choices": [{"index": 0, "delta": delta, "finish_reason": fin}]}
            self.wfile.write(("data: " + json.dumps(c, ensure_ascii=False) + "\n\n").encode("utf-8"))
            self.wfile.flush()

        chunk({"role": "assistant"})
        chunk({"reasoning_content": "考え中その1。"}); time.sleep(0.4)
        chunk({"reasoning_content": "考え中その2。"}); time.sleep(0.4)
        chunk({"content": "前半の"}); time.sleep(0.6)
        chunk({"content": "本文です。"}); time.sleep(0.6)
        chunk({}, "stop")
        self.wfile.write(b"data: [DONE]\n\n")
        self.wfile.flush()


class _TagHandler(http.server.BaseHTTPRequestHandler):
    """複数モデルのテスト用。どのサーバが呼ばれたか(tag)を記録する。"""
    protocol_version = "HTTP/1.1"
    tag = "A"
    hits = []
    lock = threading.Lock()

    def log_message(self, *a):
        pass

    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(n).decode("utf-8", "replace")
        try:
            req = json.loads(body or "{}")
        except ValueError:
            req = {}
        with _TagHandler.lock:
            _TagHandler.hits.append({
                "tag": self.tag,
                "model": req.get("model"),
                "auth": self.headers.get("Authorization"),
            })
        out = {"id": "c", "object": "chat.completion", "model": req.get("model"),
               "choices": [{"index": 0, "message": {"role": "assistant",
                           "content": "from " + self.tag}, "finish_reason": "stop"}],
               "usage": {"total_tokens": 5}}
        b = json.dumps(out, ensure_ascii=False).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)
        self.wfile.flush()


class _Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


def _free_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    p = s.getsockname()[1]
    s.close()
    return p


def start_mock(plain=False):
    _Handler.plain = plain
    srv = _Server(("127.0.0.1", 0), _Handler)
    port = srv.server_address[1]
    t = threading.Thread(target=srv.serve_forever, daemon=True)
    t.start()
    return srv, port


# --------------------------------------------------------------------------
#  ヘルパ
# --------------------------------------------------------------------------

def start_slow_mock():
    srv = _Server(("127.0.0.1", 0), _SlowHandler)
    port = srv.server_address[1]
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv, port


def start_tag_mock(tag):
    """tag ごとに独立したモックサーバを立てる。"""
    cls = type("TagHandler_" + tag, (_TagHandler,), {"tag": tag})
    srv = _Server(("127.0.0.1", 0), cls)
    port = srv.server_address[1]
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv, port


def find_compiler():
    for name in ("csc", "mcs"):
        p = shutil.which(name)
        if p:
            return name, p
    return None, None


def http_json(url, data=None, method=None, timeout=30):
    req = urllib.request.Request(url, method=method)
    body = None
    if data is not None:
        body = json.dumps(data).encode("utf-8")
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, body, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def read_sse(url, data, timeout=60):
    """SSE を最後まで読み、[(event, obj)] を返す。"""
    req = urllib.request.Request(url)
    req.add_header("Content-Type", "application/json")
    body = json.dumps(data).encode("utf-8")
    events = []
    with urllib.request.urlopen(req, body, timeout=timeout) as r:
        cur = None
        for raw in r:
            line = raw.decode("utf-8", "replace").rstrip("\r\n")
            if line.startswith("event:"):
                cur = line[6:].strip()
            elif line.startswith("data:"):
                txt = line[5:].strip()
                try:
                    obj = json.loads(txt) if txt else {}
                except ValueError:
                    obj = {"_raw": txt}
                events.append((cur, obj))
    return events


def get_session(base, sid):
    return http_json(base + "/api/sessions/" + sid)


# --------------------------------------------------------------------------
#  本体
# --------------------------------------------------------------------------

def run(keep=False):
    kind, comp = find_compiler()
    if not comp:
        print("[SKIP] csc / mcs が無いため実行時テストをスキップします")
        return 0
    if not shutil.which("mono"):
        print("[SKIP] mono が無いため実行時テストをスキップします")
        return 0
    if not shutil.which("python3"):
        print("[SKIP] python3 が無いためモックサーバを起動できません")
        return 0

    tmp = tempfile.mkdtemp(prefix="agentweb_e2e_")
    exe = os.path.join(tmp, "aw.exe")
    try:
        cmd = [comp, "/nologo", "/target:exe", "/out:" + exe]
        if kind == "mcs":
            cmd.append("-langversion:5")
        cmd.append(CS_PATH)
        p = subprocess.run(cmd, cwd=HERE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        if p.returncode != 0:
            fail("ビルド失敗:\n" + p.stdout.decode("utf-8", "replace"))
            return 1
        ok("aw.exe をビルドしました (%s)" % kind)

        # --- ケース 1+2: ストリーミング + ツールループ ---
        mock, mport = start_mock(plain=False)
        port = _free_port()
        cfg = os.path.join(tmp, "agentweb.json")
        env = dict(os.environ)
        env.update({
            "LLM_API_URL": "http://127.0.0.1:%d/v1/chat/completions" % mport,
            "LLM_API_KEY": "test",
            "LLM_MODEL": "mock",
            "ALLOW_TOOLS": "1",
            "LLM_EMIT_REASONING": "1",
            "LLM_USE_SYSTEM_PROXY": "0",
        })
        # StackOverflow 時は大量のスタックトレースが出るため、stdout はファイルへ逃がす
        # (PIPE だとバッファが詰まってデッドロックする)
        log1 = open(os.path.join(tmp, "server1.log"), "wb")
        proc = subprocess.Popen(["mono", exe, "--config", cfg, "--port", str(port)],
                                env=env, stdout=log1, stderr=subprocess.STDOUT,
                                cwd=tmp)
        base = "http://127.0.0.1:%d" % port
        try:
            _wait_up(base)
            try:
                ev = read_sse(base + "/api/chat/stream", {"message": "テスト", "session": ""})
            except Exception as ex:
                fail("ストリーミングが異常終了しました（クラッシュの疑い）: %s" % ex)
                tail = _tail_log(os.path.join(tmp, "server1.log"))
                if tail:
                    fail("サーバログ末尾:\n" + tail)
                return 1
            names = [e for e, _ in ev]
            sid = ""
            for e, o in ev:
                if e == "done":
                    sid = o.get("session") or ""
            if not (names and names[-1] == "end" and "done" in names):
                fail("ストリーミングが完走しませんでした (イベント: %s)" % names)
                tail = _tail_log(os.path.join(tmp, "server1.log"))
                if tail:
                    fail("サーバログ末尾:\n" + tail)
                if "end" not in names:
                    fail("サーバが応答途中で落ちました（StackOverflow の疑い）")
                return 1
            ok("ストリーミングが完走しました (イベント: %s)" % ", ".join(names))
            ok("StackOverflow は発生しませんでした（ストリーミング + ツールループ）")
            if "tool" in names and "toolend" in names:
                ok("ツール呼び出しループが動作しました")
            else:
                fail("ツール呼び出しループが動作しませんでした")
            if sid:
                sess = get_session(base, sid)
                am = [m for m in sess["messages"] if m["role"] == "assistant"]
                if not am:
                    fail("セッションに assistant メッセージがありません")
                else:
                    a = am[0]
                    if a.get("think") == "ツールを使うぞ。結果をまとめる。":
                        ok("think の保存/復元が正しい: %r" % a["think"])
                    else:
                        fail("think が想定と違います: %r" % a.get("think"))
                    if a.get("steps"):
                        ok("steps の保存/復元が正しい: %r" % a["steps"])
                    else:
                        fail("steps が保存されていません")
                    if a.get("content") == "完了しました。":
                        ok("content が正しい")
                    else:
                        fail("content が想定と違います: %r" % a.get("content"))
        finally:
            proc.terminate()
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc.kill()
            log1.close()
            mock.shutdown()
            mock.server_close()

        if _Handler.leaks:
            fail("think/steps/ts が LLM API へ漏れました: %r" % _Handler.leaks[:3])
        else:
            ok("think/steps/ts は LLM API へ漏れていません")

        # --- ケース 3: 非ストリーミング ---
        _Handler.calls = 0
        _Handler.leaks = []
        mock2, mport2 = start_mock(plain=True)
        port2 = _free_port()
        cfg2 = os.path.join(tmp, "agentweb2.json")
        env2 = dict(env)
        env2["LLM_API_URL"] = "http://127.0.0.1:%d/v1/chat/completions" % mport2
        env2["ALLOW_TOOLS"] = "0"
        log2 = open(os.path.join(tmp, "server2.log"), "wb")
        proc2 = subprocess.Popen(["mono", exe, "--config", cfg2, "--port", str(port2)],
                                 env=env2, stdout=log2, stderr=subprocess.STDOUT,
                                 cwd=tmp)
        base2 = "http://127.0.0.1:%d" % port2
        try:
            _wait_up(base2)
            try:
                res = http_json(base2 + "/api/chat", {"message": "非ストリーム", "session": ""})
            except Exception as ex:
                fail("非ストリーミングが異常終了しました（クラッシュの疑い）: %s" % ex)
                tail = _tail_log(os.path.join(tmp, "server2.log"))
                if tail:
                    fail("サーバログ末尾:\n" + tail)
                return 1
            if res.get("reply") == "非ストリーム回答。":
                ok("/api/chat (非ストリーミング) が動作しました")
            else:
                fail("/api/chat の応答が想定と違います: %r" % res)
            sid2 = res.get("session") or ""
            if sid2:
                sess2 = get_session(base2, sid2)
                am2 = [m for m in sess2["messages"] if m["role"] == "assistant"]
                if am2 and am2[0].get("think") == "非ストリームの思考。":
                    ok("非ストリーミングでも think が保存されます")
                else:
                    fail("非ストリーミングの think が想定と違います: %r"
                         % (am2[0].get("think") if am2 else None))
        finally:
            proc2.terminate()
            try:
                proc2.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc2.kill()
            log2.close()
            mock2.shutdown()
            mock2.server_close()

        if _Handler.leaks:
            fail("非ストリーミングで think/steps/ts が漏れました: %r" % _Handler.leaks[:3])
        else:
            ok("非ストリーミングでもメタデータは漏れていません")

        # --- ケース 4: 複数モデル（名前の一覧だけ。接続先/キー/コンテキストは共通） ---
        _TagHandler.hits = []
        msa, mpa = start_tag_mock("A")
        port4 = _free_port()
        cfg4 = os.path.join(tmp, "agentweb4.json")
        # 接続先・キー・コンテキスト上限は共通の 1 つだけ。models は名前の配列。
        with open(cfg4, "w", encoding="utf-8") as f:
            json.dump({
                "port": 0,
                "llmApiUrl": "http://127.0.0.1:%d/v1/chat/completions" % mpa,
                "llmApiKey": "sk-A",
                "model": "modelA",
                "contextWindow": 64000,
                "allowTools": False,
                "emitReasoning": False,
                "models": ["modelA", "modelB"],
            }, f, ensure_ascii=False)
        env4 = dict(os.environ)
        for k in ("LLM_API_URL", "LLM_API_KEY", "LLM_MODEL",
                  "ALLOW_TOOLS", "LLM_EMIT_REASONING"):
            env4.pop(k, None)
        env4["LLM_USE_SYSTEM_PROXY"] = "0"
        log4 = open(os.path.join(tmp, "server4.log"), "wb")
        proc4 = subprocess.Popen(["mono", exe, "--config", cfg4, "--port", str(port4)],
                                 env=env4, stdout=log4, stderr=subprocess.STDOUT,
                                 cwd=tmp)
        base4 = "http://127.0.0.1:%d" % port4
        try:
            _wait_up(base4)
            h = http_json(base4 + "/api/health")
            if h.get("models") == ["modelA", "modelB"] and h.get("model") == "modelA":
                ok("/api/health がモデル名の一覧と選択中モデルを返します")
            else:
                fail("models / model が想定と違います: %r" % h)

            def ask():
                return http_json(base4 + "/api/chat", {"message": "hi", "session": ""})

            ask()
            hit = _TagHandler.hits[-1] if _TagHandler.hits else None
            if hit and hit["model"] == "modelA" and hit["auth"] == "Bearer sk-A":
                ok("modelA が共通の接続先/キーで呼ばれます")
            else:
                fail("modelA の呼び出しが想定と違います: %r" % hit)

            # 切り替え → 共通の接続先のまま、モデル名だけ変わる
            r = http_json(base4 + "/api/model", {"model": "modelB"})
            if r.get("model") == "modelB":
                ok("POST /api/model でモデル名を切り替えられます")
            else:
                fail("モデル切り替えの応答が想定と違います: %r" % r)
            ask()
            hit = _TagHandler.hits[-1] if _TagHandler.hits else None
            if hit and hit["model"] == "modelB" and hit["auth"] == "Bearer sk-A" \
                    and hit["tag"] == "A":
                ok("modelB も同じ接続先/キーで呼ばれます")
            else:
                fail("modelB の呼び出しが想定と違います: %r" % hit)

            c = http_json(base4 + "/api/config")["config"]
            if c.get("models") == ["modelA", "modelB"] and c.get("model") == "modelB":
                ok("/api/config がモデル名の一覧と選択中モデルを返します")
            else:
                fail("config の models / model が想定と違います: %r"
                     % {k: c.get(k) for k in ("models", "model")})
            if c.get("llmApiUrl", "").endswith(":%d/v1/chat/completions" % mpa) \
                    and c.get("contextWindow") == 64000:
                ok("モデルを切り替えても共通設定は変わりません")
            else:
                fail("共通設定が変わりました: %r" % {k: c.get(k) for k in
                                                    ("llmApiUrl", "contextWindow")})
            if "effectiveApiUrl" not in c and "effectiveContextWindow" not in c:
                ok("共通化に伴い effective* は返りません")
            else:
                fail("不要な effective* が残っています: %r" % c)

            # エラー系
            try:
                http_json(base4 + "/api/model", {"model": "nope"})
                fail("存在しないモデルで 404 になりません")
            except urllib.error.HTTPError as e:
                if e.code == 404:
                    ok("存在しないモデルは 404 になります")
                else:
                    fail("存在しないモデルのステータスが想定と違います: %d" % e.code)

            # 空文字 / キー欠落
            for bad in ({"model": ""}, {}):
                try:
                    http_json(base4 + "/api/model", bad)
                    fail("不正な model で 400 になりません: %r" % bad)
                except urllib.error.HTTPError as e:
                    if e.code == 400:
                        ok("不正な model(%r) は 400 になります" % bad)
                    else:
                        fail("不正な model のステータスが想定と違います: %d" % e.code)

            # 旧形式({"name":...})も受け付ける
            http_json(base4 + "/api/config", {"models": [{"name": "x1"}, {"name": "x2"}]})
            c = http_json(base4 + "/api/config")["config"]
            if c.get("models") == ["x1", "x2"]:
                ok("旧形式(models=[{name}])もモデル名として取り込まれます")
            else:
                fail("旧形式の取り込みが想定と違います: %r" % c.get("models"))

            # 保存形式は名前の配列
            saved = json.load(open(cfg4, encoding="utf-8"))
            if saved.get("models") == ["x1", "x2"]:
                ok("models はモデル名の配列で保存されます")
            else:
                fail("models の保存形式が想定と違います: %r" % saved.get("models"))
            if saved.get("model") in ("x1", "x2"):
                ok("選択中モデルも設定ファイルに保存されます")
            else:
                fail("選択中モデルが保存されていません: %r" % saved.get("model"))

            # 空配列では上書きしない
            http_json(base4 + "/api/config", {"models": []})
            c = http_json(base4 + "/api/config")["config"]
            if c.get("models") == ["x1", "x2"]:
                ok("空の models では上書きされません")
            else:
                fail("空の models で上書きされました: %r" % c.get("models"))

            if proc4.poll() is None:
                ok("複数モデルのテストでサーバが落ちませんでした")
            else:
                fail("複数モデルのテストでサーバが落ちました")
        finally:
            proc4.terminate()
            try:
                proc4.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc4.kill()
            log4.close()
            msa.shutdown()
            msa.server_close()

        # --- ケース 5: 再開（下書き・最後のセッション・配色・生成途中の途中経過） ---
        _SlowHandler.hits = []
        mss, mps = start_slow_mock()
        port5 = _free_port()
        cfg5 = os.path.join(tmp, "agentweb5.json")
        with open(cfg5, "w", encoding="utf-8") as f:
            json.dump({
                "llmApiUrl": "http://127.0.0.1:%d/v1/chat/completions" % mps,
                "llmApiKey": "k", "model": "mock",
                "allowTools": False, "emitReasoning": True, "theme": "dark",
            }, f, ensure_ascii=False)
        env5 = dict(os.environ)
        for k in ("LLM_API_URL", "LLM_API_KEY", "LLM_MODEL",
                  "ALLOW_TOOLS", "LLM_EMIT_REASONING"):
            env5.pop(k, None)
        env5["LLM_USE_SYSTEM_PROXY"] = "0"
        log5 = open(os.path.join(tmp, "server5.log"), "wb")
        proc5 = subprocess.Popen(["mono", exe, "--config", cfg5, "--port", str(port5)],
                                 env=env5, stdout=log5, stderr=subprocess.STDOUT,
                                 cwd=tmp)
        base5 = "http://127.0.0.1:%d" % port5

        def restart5():
            proc5.terminate()
            try:
                proc5.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc5.kill()
            return subprocess.Popen(["mono", exe, "--config", cfg5, "--port", str(port5)],
                                    env=env5, stdout=log5, stderr=subprocess.STDOUT,
                                    cwd=tmp)

        try:
            _wait_up(base5)

            # 配色はサーバ側に保存される
            c = http_json(base5 + "/api/config")["config"]
            if c.get("theme") == "dark":
                ok("config に theme があります")
            else:
                fail("config に theme がありません: %r" % c.get("theme"))
            http_json(base5 + "/api/config", {"theme": "light"})
            if http_json(base5 + "/api/config")["config"].get("theme") == "light":
                ok("theme がサーバ側に永続化されます")
            else:
                fail("theme が永続化されていません")

            # 下書き
            http_json(base5 + "/api/ui", {"draft": "書きかけのプロンプト"})
            if http_json(base5 + "/api/health").get("draft") == "書きかけのプロンプト":
                ok("下書きが保存され health から復元できます")
            else:
                fail("下書きが保存されていません")

            # 生成中の途中経過がサーバに残る（接続を途中で切って模擬）
            sid5 = ""
            def stream_abort():
                req = urllib.request.Request(base5 + "/api/chat/stream")
                req.add_header("Content-Type", "application/json")
                body = json.dumps({"message": "途中で切るテスト", "session": ""}).encode("utf-8")
                n = 0
                try:
                    with urllib.request.urlopen(req, body, timeout=30) as r:
                        for raw in r:
                            if raw.decode("utf-8", "replace").startswith("data:"):
                                n += 1
                                if n > 2:
                                    break      # ブラウザを閉じた状況を模擬
                except Exception:
                    pass

            th = threading.Thread(target=stream_abort, daemon=True)
            th.start()
            inflight = None
            for _ in range(80):
                h = http_json(base5 + "/api/health")
                if h.get("busy") and (h.get("turn") or {}).get("content"):
                    inflight = h
                    break
                time.sleep(0.1)
            if inflight:
                ok("生成中は /api/health の turn に途中の本文が入ります")
                sid5 = (inflight.get("turn") or {}).get("session") or ""
                if (inflight.get("turn") or {}).get("think", "").startswith("考え中"):
                    ok("生成中は turn に途中の思考が入ります")
                else:
                    fail("turn の思考が想定と違います: %r"
                         % (inflight.get("turn") or {}).get("think"))
                if sid5:
                    s5 = http_json(base5 + "/api/sessions/" + sid5)
                    if (s5.get("turn") or {}).get("content"):
                        ok("生成中でもセッション取得で turn が返ります")
                    else:
                        fail("生成中なのに turn が返りません: %r" % s5.get("turn"))
            else:
                fail("生成中の turn が取得できませんでした")
            th.join(timeout=40)

            # 完了後は turn が空になる
            for _ in range(100):
                h = http_json(base5 + "/api/health")
                if not h.get("busy"):
                    break
                time.sleep(0.2)
            t = h.get("turn") or {}
            if not t.get("content"):
                ok("生成完了後は turn が空になります")
            else:
                fail("生成完了後も turn が残っています: %r" % t)

            # 新規作成すると、そのセッションが「最後に開いていたセッション」になる
            created = http_json(base5 + "/api/sessions", {})
            new_id = created.get("session") or ""
            if new_id and http_json(base5 + "/api/health").get("lastSession") == new_id:
                ok("新規作成したセッションが lastSession になります")
            else:
                fail("新規作成が lastSession に反映されません: %r"
                     % http_json(base5 + "/api/health").get("lastSession"))
            if new_id and any(x["id"] == new_id for x in
                              (http_json(base5 + "/api/sessions").get("sessions") or [])):
                ok("新規作成したセッションが一覧に現れます")
            else:
                fail("新規作成したセッションが一覧にありません: %r" % new_id)

            # 最後に開いたセッション
            lst = http_json(base5 + "/api/sessions").get("sessions") or []
            ids = [x["id"] for x in lst]
            if ids and not any(i.startswith("_") for i in ids):
                ok("_state.json はセッション一覧に含まれません")
            else:
                fail("セッション一覧に内部ファイルが混ざっています: %r" % ids)
            # 途中で切ったテストのセッションを対象にする(他のケースのセッションと混ざらないように)
            last = sid5 or (ids[0] if ids else "")
            if last:
                http_json(base5 + "/api/sessions/" + last)   # GET で lastSession が記録される
                if http_json(base5 + "/api/health").get("lastSession") == last:
                    ok("最後に開いたセッションが記録されます")
                else:
                    fail("lastSession が記録されていません")

            # サーバ再起動後も復元できる
            proc5 = restart5()
            _wait_up(base5)
            h = http_json(base5 + "/api/health")
            if h.get("lastSession") == last:
                ok("再起動後も lastSession が残ります")
            else:
                fail("再起動後に lastSession が失われました: %r" % h.get("lastSession"))
            if http_json(base5 + "/api/config")["config"].get("theme") == "light":
                ok("再起動後も theme が残ります")
            else:
                fail("再起動後に theme が失われました")
            if last:
                s5 = http_json(base5 + "/api/sessions/" + last)
                msgs = s5.get("messages") or []
                am = [m for m in msgs if m.get("role") == "assistant"]
                if any(m.get("role") == "user" for m in msgs) and am:
                    ok("再起動後も会話(ユーザー/アシスタント)が残ります")
                else:
                    fail("再起動後に会話が失われました: %r" % msgs)
                if am and "本文" in (am[-1].get("content") or ""):
                    ok("途中で切っても応答本文が欠けていません")
                else:
                    fail("応答本文が欠けています: %r" % (am[-1].get("content") if am else None))
                if am and (am[-1].get("think") or "").startswith("考え中"):
                    ok("途中で切っても思考が保存されています")
                else:
                    fail("思考が保存されていません: %r"
                         % (am[-1].get("think") if am else None))
            if proc5.poll() is None:
                ok("再開テストでサーバが落ちませんでした")
            else:
                fail("再開テストでサーバが落ちました")
        finally:
            try:
                proc5.terminate()
                proc5.wait(timeout=10)
            except Exception:
                proc5.kill()
            log5.close()
            mss.shutdown()
            mss.server_close()

        return 1 if fails else 0
    finally:
        if keep:
            print("[INFO] 一時ディレクトリを残しました: " + tmp)
        else:
            shutil.rmtree(tmp, ignore_errors=True)


def _tail_log(path, n=15):
    try:
        with open(path, "rb") as f:
            lines = f.read().decode("utf-8", "replace").splitlines()
        return "\n".join(lines[-n:])
    except Exception:
        return ""


def _wait_up(base, timeout=30):
    end = time.time() + timeout
    while time.time() < end:
        try:
            http_json(base + "/api/health", timeout=3)
            return True
        except Exception:
            time.sleep(0.3)
    raise RuntimeError("サーバが起動しませんでした: " + base)


if __name__ == "__main__":
    keep = "--keep" in sys.argv[1:]
    print("=== AgentWeb 実行時回帰テスト (mono) ===")
    rc = run(keep=keep)
    print("")
    print("結果: OK=%d / FAIL=%d" % (len(oks), len(fails)))
    sys.exit(rc)
