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
