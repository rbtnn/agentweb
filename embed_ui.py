#!/usr/bin/env python3
# index.html を base64 にエンコードして AgentWeb.cs に埋め込む。
# 実行:  python embed_ui.py
import base64, sys, os, re

def main():
    here = os.path.dirname(os.path.abspath(__file__))
    html_path = os.path.join(here, "index.html")
    cs_path   = os.path.join(here, "AgentWeb.cs")

    with open(html_path, "rb") as f:
        data = f.read()

    b64 = base64.b64encode(data).decode("ascii")

    # UTF-8 の BOM 付きで読み書きする(csc が日本語文字列を正しく読むため)
    with open(cs_path, "r", encoding="utf-8-sig") as f:
        cs = f.read()

    # 既存の base64 (またはプレースホルダ)を新しい base64 で置き換える
    pattern = re.compile(r'(const string EmbeddedIndexHtml = ")[A-Za-z0-9+/=]*(";)')
    if not pattern.search(cs):
        print("[ERROR] EmbeddedIndexHtml を検出できませんでした", file=sys.stderr)
        sys.exit(1)

    cs = pattern.sub(lambda m: m.group(1) + b64 + m.group(2), cs)
    with open(cs_path, "w", encoding="utf-8-sig") as f:
        f.write(cs)

    print("embedded %d bytes (utf-8) of index.html into AgentWeb.cs" % len(data))

if __name__ == "__main__":
    main()
