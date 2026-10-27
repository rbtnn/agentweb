#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
check_consistency.py
--------------------
AgentWeb.cs（サーバ）と index.html（Web UI）の整合性を検査します。

検査項目:
  1. エンドポイント : UI が fetch する API パスがサーバのルートに存在するか（メソッドも確認）
  2. SSE イベント    : サーバが emit するイベント名と UI が処理するイベント名が一致するか
  3. SSE フィールド  : SSE ペイロードの JSON キーと UI が参照するキーが一致するか
  4. /api/health     : レスポンスのキーを UI が正しく参照しているか
  5. セッション一覧  : /api/sessions の各要素のキーを UI が正しく参照しているか
  6. セッション詳細  : /api/sessions/<id> のキーとメッセージのキーが一致するか
  7. ツール設定      : /api/tools の allowTools キーが一致するか
  8. 構成            : index.html が外部ファイルとして存在し、埋め込み(base64)が残っていないか

使い方:
    python check_consistency.py [--quiet]

終了コード:
    0 = 問題なし（警告のみの場合も 0）
    1 = 不整合あり（FAIL）
"""

import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
CS_PATH = os.path.join(HERE, "AgentWeb.cs")
HTML_PATH = os.path.join(HERE, "index.html")

fails = []
warns = []
oks = []


def ok(msg):
    oks.append(msg)


def fail(msg):
    fails.append(msg)


def warn(msg):
    warns.append(msg)


def read_text(path, enc):
    with open(path, "r", encoding=enc) as f:
        return f.read()


# --------------------------------------------------------------------------
#  C# ヘルパ
# --------------------------------------------------------------------------
def extract_block(src, marker, open_ch="{", close_ch="}"):
    """marker の直後の '{' から対応する '}' までを返す。
    文字列/文字リテラル/コメント内の括弧は無視する（C# 用）。"""
    pos = src.find(marker)
    if pos < 0:
        return ""
    start = src.find(open_ch, pos + len(marker))
    if start < 0:
        return ""
    depth = 0
    i = start
    n = len(src)
    while i < n:
        c = src[i]
        if c == '"':
            i += 1
            while i < n and src[i] != '"':
                if src[i] == "\\":
                    i += 1
                i += 1
        elif c == "'":
            i += 1
            while i < n and src[i] != "'":
                if src[i] == "\\":
                    i += 1
                i += 1
        elif src.startswith("//", i):
            j = src.find("\n", i)
            i = n if j < 0 else j
        elif src.startswith("/*", i):
            j = src.find("*/", i)
            i = n if j < 0 else j + 1
        elif c == open_ch:
            depth += 1
        elif c == close_ch:
            depth -= 1
            if depth == 0:
                return src[start:i + 1]
        i += 1
    return src[start:]


def dict_keys(body, var="d"):
    """body 内の  var["key"] = ...  からキーを集める。"""
    return set(re.findall(r'\b' + re.escape(var) + r'\["([A-Za-z_][A-Za-z0-9_]*)"\]\s*=', body))


def server_routes(src):
    """if 条件中のパス判定（Route() と req.Path 直書きの両方）からルート定義を取り出す。"""
    routes = []
    for line in src.splitlines():
        st = line.strip()
        if not st.startswith("if"):
            continue
        if ".Path" not in line and "path" not in line:
            continue
        methods = set(re.findall(r'req\.Method == "([A-Z]+)"', line))
        exacts = re.findall(r'(?:req\.Path|path) == "([^"]*)"', line)
        prefixes = re.findall(r'(?:req\.Path|path)\.StartsWith\("([^"]*)"\)', line)
        suffixes = re.findall(r'(?:req\.Path|path)\.EndsWith\("([^"]*)"\)', line)

        for e in exacts:
            if not e:
                continue
            routes.append({
                "methods": methods,
                "regex": re.compile("^" + re.escape(e) + "$"),
                "text": e,
            })
        for p in prefixes:
            if suffixes:
                for s in suffixes:
                    routes.append({
                        "methods": methods,
                        "regex": re.compile("^" + re.escape(p) + ".*" + re.escape(s) + "$"),
                        "text": p + "*" + s,
                    })
            else:
                routes.append({
                    "methods": methods,
                    "regex": re.compile("^" + re.escape(p) + ".*$"),
                    "text": p + "*",
                })
    return routes


def server_sse_events(src):
    """emit("name", ...) からイベント名を集める。"""
    return set(re.findall(r'emit\(\s*"([A-Za-z_][A-Za-z0-9_]*)"\s*,', src))


def server_sse_fields(src):
    """emit(...) の JSON 文字列リテラル中のキー（\\"key\\"）を集める。"""
    fields = set()
    for m in re.finditer(r'emit\(\s*"[A-Za-z_][A-Za-z0-9_]*"\s*,\s*"', src):
        # 呼び出しの終わり（次の ');'）までを対象にする
        end = src.find(");", m.end())
        chunk = src[m.end(): end if end > 0 else m.end() + 400]
        fields.update(re.findall(r'\\"([A-Za-z_][A-Za-z0-9_]*)\\"\s*:', chunk))
    return fields


# --------------------------------------------------------------------------
#  JS ヘルパ
# --------------------------------------------------------------------------
def js_block(js, marker):
    return extract_block(js, marker, "{", "}")


def js_fields(body, var="d"):
    """body 内の var.field 参照を集める（プロパティ名のみ）。"""
    return set(re.findall(r'\b' + re.escape(var) + r'\.([A-Za-z_][A-Za-z0-9_]*)', body))


def client_fetches(html):
    """fetch(...) の第1引数から URL パターンを組み立てる。
    文字列リテラルを連結し、間に式があれば '*' を挿入する。
    戻り値: [(pattern, method), ...]"""
    out = []
    for m in re.finditer(r'fetch\s*\(', html):
        i = m.end()
        parts = []
        depth = 0
        j = i
        call_text = []
        while j < len(html):
            c = html[j]
            if c in "\"'":
                q = c
                k = j + 1
                buf = []
                while k < len(html) and html[k] != q:
                    if html[k] == "\\":
                        buf.append(html[k])
                        k += 1
                        if k < len(html):
                            buf.append(html[k])
                            k += 1
                        continue
                    buf.append(html[k])
                    k += 1
                parts.append("".join(buf))
                j = k + 1
                continue
            if c in "([":
                depth += 1
                j += 1
                continue
            if c in ")]":
                if depth == 0:
                    break
                depth -= 1
                j += 1
                continue
            if c == "," and depth == 0:
                break
            j += 1
        if not parts:
            continue
        # fetch 呼び出し全体（method 取得用）を切り出す
        end = html.find(")", j)
        call_text = html[i: end if end > 0 else i + 300]
        mm = re.search(r'method\s*:\s*"([A-Z]+)"', call_text)
        method = mm.group(1) if mm else "GET"

        pat = parts[0]
        for p in parts[1:]:
            pat += "*" + p
        out.append((pat, method))
    # 重複除去
    seen = set()
    uniq = []
    for pat, method in out:
        key = (pat, method)
        if key not in seen:
            seen.add(key)
            uniq.append((pat, method))
    return uniq


# --------------------------------------------------------------------------
#  検査
# --------------------------------------------------------------------------
def check():
    if not os.path.isfile(CS_PATH):
        fail("AgentWeb.cs が見つかりません: " + CS_PATH)
        return
    if not os.path.isfile(HTML_PATH):
        fail("index.html が見つかりません（外部配信に必須）: " + HTML_PATH)
        return

    cs = read_text(CS_PATH, "utf-8-sig")
    html = read_text(HTML_PATH, "utf-8")

    # --- 8. 構成 -----------------------------------------------------------
    if "EmbeddedIndexHtml" in cs or "FromBase64String" in cs:
        fail("AgentWeb.cs に UI 埋め込み(base64)が残っています（index.html は外部配信）")
    else:
        ok("AgentWeb.cs に UI 埋め込みは残っていません")

    if "Path.Combine(config.BindPath, \"index.html\")" in cs:
        ok("サーバは bindPath 配下の index.html を外部ファイルとして配信します")
    else:
        fail("ServeIndexHtml が index.html を外部ファイルとして読んでいません")

    # エラー応答（MakeError）のキーは全エンドポイント共通で許容する
    err_keys = dict_keys(extract_block(cs, "static Dictionary<string, object> MakeError(string code, string message)"))

    # --- 1. エンドポイント --------------------------------------------------
    routes = server_routes(cs)
    if not routes:
        fail("AgentWeb.cs からルート定義を抽出できませんでした")
    fetches = client_fetches(html)
    if not fetches:
        fail("index.html から fetch() を抽出できませんでした")

    used = set()
    for pat, method in fetches:
        sample = pat.replace("*", "sid")
        hit = None
        for r in routes:
            if r["regex"].match(sample):
                if not r["methods"] or method in r["methods"]:
                    hit = r
                    break
        if hit is None:
            fail("UI が呼ぶ %s %s に対応するサーバルートがありません" % (method, pat))
        else:
            used.add(hit["text"])

    if fetches:
        ok("エンドポイント: UI の fetch %d 件すべてがサーバのルートに一致" % len(fetches))

    unused = [r["text"] for r in routes if r["text"] not in used and r["text"] not in ("/", "/favicon.ico")]
    if unused:
        warn("UI から呼ばれていないサーバルート: " + ", ".join(sorted(set(unused))))

    # --- 2/3. SSE イベント・フィールド --------------------------------------
    s_events = server_sse_events(cs)
    handle_body = js_block(html, "function handle(ev, dat)")
    c_events = set(re.findall(r'ev === "([A-Za-z_][A-Za-z0-9_]*)"', handle_body))
    if not s_events:
        fail("サーバの SSE イベントを抽出できませんでした")
    if not c_events:
        fail("UI の SSE イベント処理を抽出できませんでした")

    # UI 側で処理しなくても支障がない（装飾用の）イベント
    OPTIONAL_EVENTS = set(["toolend"])
    missing = s_events - c_events
    missing_critical = missing - OPTIONAL_EVENTS
    missing_optional = missing & OPTIONAL_EVENTS
    if missing_critical:
        fail("サーバが送るが UI が処理しないイベント: " + ", ".join(sorted(missing_critical)))
    if missing_optional:
        warn("UI が処理しない（任意の）イベント: " + ", ".join(sorted(missing_optional)))
    extra = c_events - s_events
    if extra:
        warn("UI が処理するがサーバが送らないイベント: " + ", ".join(sorted(extra)))
    if not missing:
        ok("SSE イベント一致: " + ", ".join(sorted(s_events)))

    s_fields = server_sse_fields(cs)
    c_fields = js_fields(handle_body, "d")
    if not s_fields:
        fail("SSE ペイロードのキーを抽出できませんでした")
    miss_f = c_fields - s_fields
    if miss_f:
        fail("UI が参照するが SSE に含まれないキー: " + ", ".join(sorted(miss_f)))
    extra_f = s_fields - c_fields
    if extra_f:
        warn("SSE に含まれるが UI が参照しないキー: " + ", ".join(sorted(extra_f)))
    if not miss_f:
        ok("SSE フィールド一致: " + ", ".join(sorted(c_fields)))

    # --- 4. /api/health ----------------------------------------------------
    h_body = extract_block(cs, "HttpResponse HandleHealth()")
    s_health = dict_keys(h_body) | err_keys
    c_health = set()
    for marker in ("function initServerState()", "function pollServerBusy()"):
        c_health |= js_fields(js_block(html, marker), "d")
    miss_h = c_health - s_health
    if miss_h:
        fail("/api/health に無いキーを UI が参照: " + ", ".join(sorted(miss_h)))
    else:
        ok("/api/health フィールド一致: " + ", ".join(sorted(c_health)))

    # --- 5. セッション一覧 --------------------------------------------------
    s_sessions = dict_keys(extract_block(cs, "public void Save(string id, List<object> messages, string title, string summary"), "s")
    s_sessions |= dict_keys(extract_block(cs, "public List<Dictionary<string, object>> List()"), "s")
    c_sessions = js_fields(js_block(html, "function renderSessionList(sessions, selected)"), "s")
    c_sessions |= js_fields(js_block(html, "function refreshSessions()"), "s")
    miss_s = c_sessions - s_sessions
    if miss_s:
        fail("セッション一覧に無いキーを UI が参照: " + ", ".join(sorted(miss_s)))
    else:
        ok("セッション一覧フィールド一致: " + ", ".join(sorted(c_sessions)))

    # --- 6. セッション詳細（エンドポイント別） -------------------------------
    per_endpoint = [
        ("セッション詳細 GET /api/sessions/<id>", "HttpResponse HandleGetSession(string id)",
         ["function fetchSession(id)"]),
        ("セッション作成 POST /api/sessions", "HttpResponse HandleCreateSession()",
         ["function createSession(silent)"]),
        ("セッション一覧 GET /api/sessions", "HttpResponse HandleListSessions()",
         ["function refreshSessions()"]),
        ("名前の自動設定 POST /api/sessions/<id>/autotitle", "HttpResponse HandleAutoTitle(string id)",
         ["function autoTitle(id, btn)"]),
    ]
    for label, s_marker, js_markers in per_endpoint:
        s_keys = dict_keys(extract_block(cs, s_marker)) | err_keys
        c_keys = set()
        for jm in js_markers:
            c_keys |= js_fields(js_block(html, jm), "d")
        miss = c_keys - s_keys
        if miss:
            fail("%s に無いキーを UI が参照: %s" % (label, ", ".join(sorted(miss))))
        elif c_keys:
            ok("%s フィールド一致: %s" % (label, ", ".join(sorted(c_keys))))

    g_body = extract_block(cs, "HttpResponse HandleGetSession(string id)")
    s_msg = dict_keys(g_body, "mv")
    c_msg = js_fields(js_block(html, "function loadSession(messages)"), "m")
    miss_m = c_msg - s_msg
    if miss_m:
        fail("メッセージに無いキーを UI が参照: " + ", ".join(sorted(miss_m)))
    else:
        ok("メッセージフィールド一致: " + ", ".join(sorted(c_msg)))

    # --- 7. /api/tools -----------------------------------------------------
    t_body = extract_block(cs, "HttpResponse HandleSetTools(HttpRequest req)")
    s_tools = dict_keys(t_body) | err_keys
    c_tools = js_fields(js_block(html, "tool badge クリック"), "d") if False else set()
    # ツール切替のハンドラは無名関数のため、専用マーカーで抽出する
    m = re.search(r'toolsBadge\.addEventListener\("click",\s*function\s*\(\)\s*\{', html)
    if m:
        c_tools = js_fields(extract_block(html, "toolsBadge.addEventListener(\"click\", function()"), "d")
    miss_t = c_tools - s_tools
    if miss_t:
        fail("/api/tools に無いキーを UI が参照: " + ", ".join(sorted(miss_t)))
    elif c_tools:
        ok("/api/tools フィールド一致: " + ", ".join(sorted(c_tools)))
    else:
        warn("/api/tools の UI 参照を抽出できませんでした")


def main():
    quiet = "--quiet" in sys.argv[1:]
    check()

    if not quiet:
        print("=== AgentWeb.cs / index.html 整合性チェック ===")
        for m in oks:
            print("[ OK ] " + m)
        for m in warns:
            print("[WARN] " + m)
        for m in fails:
            print("[FAIL] " + m)
        print("")
        print("結果: OK=%d / WARN=%d / FAIL=%d" % (len(oks), len(warns), len(fails)))

    if fails:
        if quiet:
            for m in fails:
                print("[FAIL] " + m)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
