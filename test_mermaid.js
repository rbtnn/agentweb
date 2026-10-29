// Mermaid 描画の回帰テスト (jsdom + 実際の mermaid バンドル)
//
//   node test_mermaid.js [mermaid.min.js のパス]
//
// 検証内容:
//   1. 正常な図が SVG として描画される
//   2. シンタックスエラーの図は <pre> のフォールバック表示になる
//   3. エラー時に mermaid が残す一時要素 (#d<id> / #i<id>) が DOM に残らない
//      (mermaid 10 系はコンテナ無しで render すると <body> 末尾に残骸を残す)
//
// mermaid のバンドルは CDN から取得するか、引数でローカルパスを渡してください。
const fs = require("fs");
const path = require("path");
const { JSDOM } = require("jsdom");

const mermaidPath = process.argv[2] || "/tmp/mm.js";
if (!fs.existsSync(mermaidPath)) {
  console.error("mermaid のバンドルが見つかりません: " + mermaidPath);
  console.error("  curl -sLo " + mermaidPath +
                " https://cdn.jsdelivr.net/npm/mermaid@10/dist/mermaid.min.js");
  process.exit(2);
}

const html = fs.readFileSync(path.join(__dirname, "index.html"), "utf8");
const mm = fs.readFileSync(mermaidPath, "utf8");

// 壊れた図(構文エラー)と正常な図を両方含む応答
const bad = "flowchart TD\n  A[開始 --> B{分岐}\n  B -->|yes| C\n  C --> E[";
const good = "flowchart TD\n  A[開始] --> B[終了]";
const md = "前置き\n\n```mermaid\n" + bad + "\n```\n\n中間\n\n```mermaid\n" +
           good + "\n```\n\n後書き";

const messages = {
  s1: [
    { role: "user", content: "図を描いて" },
    { role: "assistant", content: md }
  ]
};

function jsonFetch(url, opt) {
  opt = opt || {};
  const u = String(url), m = (opt.method || "GET").toUpperCase();
  let body = {};
  if (u === "/api/config" && m === "GET") {
    body = { ok: true, config: {
      model: "m", models: ["m"], theme: "dark", port: 8787,
      allowTools: true, emitReasoning: true, llmApiUrl: "u", llmApiKey: "k",
      maxToolsPerTurn: 16, toolTimeoutSec: 30, contextWindow: 64000,
      reserveTokens: 12000, keepRecentTokens: 20000, useSystemProxy: false,
      proxyUrl: "", proxyUser: "", proxyPass: "", openBrowserOnStart: true,
      bindPath: ".", systemPrompt: "sp"
    }};
  }
  else if (u === "/api/sessions" && m === "GET")
    body = { ok: true, sessions: [{ id: "s1", title: "t", count: 2 }] };
  else if (/^\/api\/sessions\/[^/]+$/.test(u))
    body = { ok: true, id: "s1", messages: messages.s1 };
  else if (u === "/api/health")
    body = { ok: true, busy: false, allowTools: true, model: "m", models: ["m"],
             lastSession: "s1", draft: "",
             turn: { session: "", content: "", think: "", steps: [] }, progress: [] };
  else if (u === "/api/tools") body = { ok: true, allowTools: true };
  else body = { ok: true };
  return Promise.resolve({ ok: true, json: () => Promise.resolve(body) });
}

const dom = new JSDOM(html, {
  runScripts: "dangerously",
  url: "http://localhost:8787/",
  pretendToBeVisual: true,
  beforeParse(w) {
    w.fetch = jsonFetch;
    w.confirm = () => true;
    w.alert = () => {};
    w.matchMedia = () => ({ matches: false, addListener() {}, removeListener() {} });
    w.HTMLElement.prototype.scrollIntoView = function () {};
    if (!w.structuredClone) w.structuredClone = (v) => JSON.parse(JSON.stringify(v));
    // jsdom は SVG のレイアウトを計算しないため、mermaid が必要とする API を近似値で補う
    const p = w.SVGElement && w.SVGElement.prototype;
    if (p) {
      p.getBBox = () => ({ x: 0, y: 0, width: 100, height: 20 });
      p.getComputedTextLength = () => 100;
      p.getScreenCTM = () => ({ a: 1, b: 0, c: 0, d: 1, e: 0, f: 0, inverse() { return this; } });
    }
    // CDN の代わりにローカルの mermaid を読み込む
    const orig = w.document.createElement.bind(w.document);
    w.document.createElement = function (tag) {
      const el = orig(tag);
      if (String(tag).toLowerCase() === "script") {
        let _src = "";
        Object.defineProperty(el, "src", {
          get: () => _src,
          set(v) {
            _src = v;
            if (/mermaid\.min\.js/.test(v)) {
              setTimeout(() => { w.eval(mm); if (el.onload) el.onload(); }, 0);
            }
          }
        });
      }
      return el;
    };
  }
});

const w = dom.window, d = w.document;
const fail = [];
const check = (n, c, e) => {
  if (c) console.log("[ OK ] " + n);
  else { console.log("[FAIL] " + n + (e ? " :: " + e : "")); fail.push(n); }
};
const tick = (ms) => new Promise((r) => setTimeout(r, ms));

(async () => {
  await tick(600);
  for (let i = 0; i < 60; i++) {
    if (!d.querySelectorAll(".mermaid-loading").length &&
        d.querySelectorAll(".mermaid svg").length >= 2) break;
    await tick(200);
  }
  await tick(500);

  const stray = [...d.querySelectorAll('[id^="dmm"]')];
  check("dmm で始まる要素が残らない", stray.length === 0, stray.map((e) => e.id).join(","));
  const strayI = [...d.querySelectorAll('[id^="imm"]')];
  check("imm で始まる要素も残らない", strayI.length === 0, strayI.map((e) => e.id).join(","));
  check("画面外ホストも残らない", d.querySelectorAll(".mermaid-host").length === 0,
        d.querySelectorAll(".mermaid-host").length);

  const chatHtml = d.getElementById("chat").innerHTML;
  check("本文に Syntax error の図が漏れない", !/Syntax error in text/.test(chatHtml));

  const blocks = [...d.querySelectorAll(".mermaid")];
  check("mermaid ブロックが 2 つ", blocks.length === 2, blocks.length);
  check("壊れた図はフォールバック表示(<pre>)", !!blocks[0].querySelector(".mermaid-fallback"),
        blocks[0].innerHTML.slice(0, 120));
  check("フォールバックに元のソースが出る",
        !!blocks[0].querySelector(".mermaid-fallback") &&
        blocks[0].querySelector(".mermaid-fallback").textContent.includes("flowchart TD"));
  check("正常な図は SVG で描画される", !!blocks[1].querySelector("svg"),
        blocks[1].innerHTML.slice(0, 120));

  const bodyKids = [...d.body.children].map((e) => e.id || e.className || e.tagName);
  check("body 直下に一時要素が溜まっていない",
        !bodyKids.some((x) => String(x).startsWith("dmm")), bodyKids.join("|"));

  console.log("");
  console.log(fail.length ? "FAIL=" + fail.length + " :: " + fail.join(" / ") : "すべて OK");
  process.exit(fail.length ? 1 : 0);
})();
