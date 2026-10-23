// ============================================================================
//  AgentWeb.cs
//  -----------------
//  対象: Windows に同梱されている csc.exe (C# 5 / .NET Framework 4.5)
//  外部ライブラリ非依存 (BCL のみを使用 / 追加アセンブリ参照なし)
//
//  これは 組込み HTTP サーバ + OpenAI 互換 LLM API クライアント + 簡易 JSON
//  エンジン からなる、単一ファイルの AI エージェント WEB サーバです。
//
//  起動後:  http://localhost:8000/  にアクセスすると AI エージェントの
//          Web UI が表示されます。
//
//  構成(1ファイル):
//    - Program         : エントリポイント / 設定読み込み / サーバ起動
//    - HttpServer      : TcpListener ベースの最小 HTTP サーバ
//    - HttpRequest/HttpResponse : リクエスト/レスポンス抽象
//    - LlmClient       : HttpWebRequest による LLM API (チャット) 呼び出し
//    - Json            : 最小 JSON パーサ / シリアライザ (自己完結)
//
//  コンパイル:  csc /nologo /out:aw.exe AgentWeb.cs
//  実行:        aw.exe
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentWeb
{
    class Program
    {
        static HttpServer _server;

        static void Main(string[] args)
        {
            string configPath = "";
            int port = -1;
            bool help = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = (args[i] ?? "").Trim();
                if (a == "--config" && i + 1 < args.Length) configPath = args[++i].Trim();
                else if (a.StartsWith("--config=")) configPath = a.Substring("--config=".Length);
                else if (a == "--help" || a == "-h" || a == "/?") help = true;
                else if (a.StartsWith("--port=")) int.TryParse(a.Substring("--port=".Length), out port);
                else { int p; if (int.TryParse(a.TrimStart(':'), out p)) port = p; }
            }

            if (help) { PrintHelp(); return; }

            Config cfg = new Config(configPath);
            if (port <= 0) port = cfg.Port;
            if (port <= 0) port = 8000;

            _server = new HttpServer(port, cfg);

            RunConsole(port, cfg);
        }

        static void PrintHelp()
        {
            Console.WriteLine("agentweb - 自己完結型ローカルWeb UIを備えたAIエージェント");
            Console.WriteLine("使用法: aw.exe [オプション]");
            Console.WriteLine("  --config <path>   設定ファイル(agentweb.json)のパス");
            Console.WriteLine("  --port <n>        ポート番号 (既定 8000)");
        }

        static void RunConsole(int port, Config cfg)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("agentweb (console)");
            Console.WriteLine("  Listen : http://localhost:" + port + "/");
            Console.WriteLine("  API URL: " + cfg.ApiBaseUrl);
            Console.WriteLine("  Model  : " + cfg.Model);
            Console.WriteLine("  Config : " + cfg.ConfigPath);
            Console.WriteLine("  Stop   : Ctrl+C");

            Thread t = new Thread(delegate()
            {
                try { _server.Run(); }
                catch (Exception ex) { Console.WriteLine("FATAL: " + ex.Message); }
            });
            t.IsBackground = false;
            t.Start();

            if (cfg.OpenBrowserOnStart)
            {
                try { Thread.Sleep(400); Process.Start("http://localhost:" + port + "/"); }
                catch { }
            }

            t.Join();
        }
    }

    // ------------------------------------------------------------------------
    //  設定 (環境変数 で上書き可能)
    // ------------------------------------------------------------------------
    class Config
    {
        public int Port;
        public string ApiBaseUrl;          // OpenAI 互換 /chat/completions の Base URL
        public string SecretKey;           // API キー
        public string ProxyUrl;            // 社内プロキシ URL (例: http://proxy:8080)
        public string ProxyUser;           // プロキシ認証ユーザー(任意)
        public string ProxyPass;           // プロキシ認証パスワード(任意)
        public bool UseSystemProxy;        // システムプロキシを使うか
        public string Model;               // モデル名
        public bool EmitReasoning;         // reasoning_content を表示するか
        public bool AllowTools;            // LLM によるツール実行を許可するか
        public int MaxToolsPerTurn;        // 1 ターンで同時実行できるツールの最大数
        public int ToolTimeoutSec;         // ツール実行のタイムアウト(秒)
        public int ContextWindow;          // モデルのコンテキスト上限(推定トークン)
        public int ReserveTokens;          // 応答用に確保するトークン
        public int KeepRecentTokens;       // 要約せず残す直近トークン数
        public string SystemPrompt;        // AI エージェント の システムプロンプト
        public string BindPath;            // 公開ディレクトリ (Web UI 静的ファイル)
        public string ConfigPath;          // 設定ファイル(agentweb.json)のパス
        public bool OpenBrowserOnStart;    // 起動時にブラウザを開くか

        public Config() : this("") { }

        public Config(string configPathArg)
        {
            Port = 8000;
            ApiBaseUrl = Env("LLM_API_URL", "https://api.openai.com/v1/chat/completions");
            SecretKey = Env("LLM_API_KEY", "");
            ProxyUrl = Env("LLM_PROXY_URL", "");
            ProxyUser = Env("LLM_PROXY_USERNAME", "");
            ProxyPass = Env("LLM_PROXY_PASSWORD", "");
            UseSystemProxy = Env("LLM_USE_SYSTEM_PROXY", "1") == "1";
            Model = Env("LLM_MODEL", "gpt-4o-mini");
            EmitReasoning = Env("LLM_EMIT_REASONING", "1") == "1";
            AllowTools = Env("ALLOW_TOOLS", "1") == "1";
            MaxToolsPerTurn = SafeInt(Env("MAX_TOOLS_PER_TURN", "16"), 16);
            ToolTimeoutSec = SafeInt(Env("TOOL_TIMEOUT_SEC", "30"), 30);
            ContextWindow = SafeInt(Env("LLM_CONTEXT_WINDOW", "64000"), 64000);
            ReserveTokens = SafeInt(Env("LLM_RESERVE_TOKENS", "12000"), 12000);
            KeepRecentTokens = SafeInt(Env("LLM_KEEP_RECENT_TOKENS", "20000"), 20000);
            BindPath = Env("AI_BIND_PATH", AppDir());
            SystemPrompt = Env(
                "AI_SYSTEM_PROMPT",
                "あなたは親切な AI エージェントです。ユーザーの質問に日本語で簡潔に回答してください。"
                + " Markdown を使って、見出し・箇条書き・コードブロックを適宜活用してください。"
                + "\n\nこのサーバは利用者のローカルマシン(Windows)上で動作しており、あなたの指示で実際にツールを実行できます。"
                + " ファイルの調査や操作、コマンドの実行が必要な依頼には、積極的にツール(shell/read_file/write_file/list_dir)を使い、"
                + " 自分で実際に確認・実行してから結果を報告してください。危険・不可逆な操作を行う前にはユーザーに確認すること。"
                + "\nディレクトリ一覧を報告するときは、ツールのJSONをそのまま貼り付けず、ファイル/フォルダの名前を並べた読みやすいリスト(または表)にまとめてください。"
            );

            ConfigPath = (configPathArg != null && configPathArg.Trim().Length > 0)
                ? configPathArg.Trim()
                : Env("AGENTWEB_CONFIG", "");
            ApplyConfigFile();
        }

        // 設定ファイル(agentweb.json)から上書き適用する
        // 既定は実行ディレクトリの agentweb.json。優先度: 環境変数 < 設定ファイル
        void ApplyConfigFile()
        {
            string path = ConfigPath;
            if (path == null || path.Trim().Length == 0)
            {
                try { path = Path.Combine(AppDir(), "agentweb.json"); } catch { path = "agentweb.json"; }
            }
            if (!File.Exists(path)) return;
            ConfigPath = path;

            try
            {
                object o = Json.Parse(File.ReadAllText(path, Encoding.UTF8));
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (m == null) return;

                if (Has(m, "port")) Port = Json.AsInt(Get(m, "port"));
                if (Has(m, "llmApiUrl")) ApiBaseUrl = Json.AsString(Get(m, "llmApiUrl"));
                if (Has(m, "llmApiKey")) SecretKey = Json.AsString(Get(m, "llmApiKey"));
                if (Has(m, "model")) Model = Json.AsString(Get(m, "model"));
                if (Has(m, "allowTools")) AllowTools = AsBool(Get(m, "allowTools"));
                if (Has(m, "maxToolsPerTurn")) MaxToolsPerTurn = Json.AsInt(Get(m, "maxToolsPerTurn"));
                if (Has(m, "toolTimeoutSec")) ToolTimeoutSec = Json.AsInt(Get(m, "toolTimeoutSec"));
                if (Has(m, "contextWindow")) ContextWindow = Json.AsInt(Get(m, "contextWindow"));
                if (Has(m, "reserveTokens")) ReserveTokens = Json.AsInt(Get(m, "reserveTokens"));
                if (Has(m, "keepRecentTokens")) KeepRecentTokens = Json.AsInt(Get(m, "keepRecentTokens"));
                if (Has(m, "preserveRecentTokens")) KeepRecentTokens = Json.AsInt(Get(m, "preserveRecentTokens"));
                if (Has(m, "proxyUrl")) ProxyUrl = Json.AsString(Get(m, "proxyUrl"));
                if (Has(m, "proxyUser")) ProxyUser = Json.AsString(Get(m, "proxyUser"));
                if (Has(m, "proxyPass")) ProxyPass = Json.AsString(Get(m, "proxyPass"));
                if (Has(m, "useSystemProxy")) UseSystemProxy = AsBool(Get(m, "useSystemProxy"));
                if (Has(m, "systemPrompt")) SystemPrompt = Json.AsString(Get(m, "systemPrompt"));
                if (Has(m, "bindPath")) BindPath = Json.AsString(Get(m, "bindPath"));
                if (Has(m, "openBrowserOnStart")) OpenBrowserOnStart = AsBool(Get(m, "openBrowserOnStart"));
            }
            catch { }
        }

        // 設定ファイルを再読み込みする(トレイメニューから呼ぶ)。
        // ポート変更のみ再起動が必要(その他は即時反映)。
        public void Reload()
        {
            ApplyConfigFile();
        }

        static bool AsBool(object o)
        {
            if (o is bool) return (bool)o;
            string s = Json.AsString(o).Trim().ToLowerInvariant();
            return s == "true" || s == "1" || s == "on" || s == "yes";
        }

        static bool Has(Dictionary<string, object> m, string k)
        {
            return m != null && m.ContainsKey(k);
        }
        static object Get(Dictionary<string, object> m, string k)
        {
            return Has(m, k) ? m[k] : null;
        }

        static string Env(string name, string def)
        {
            string v = Environment.GetEnvironmentVariable(name);
            if (v == null || v.Trim().Length == 0)
                return def;
            return v.Trim();
        }

        static int SafeInt(string s, int def)
        {
            int n;
            if (int.TryParse(s, out n))
                return n;
            return def;
        }

        static string AppDir()
        {
            try
            {
                string d = AppDomain.CurrentDomain.BaseDirectory;
                return d;
            }
            catch
            {
                return ".";
            }
        }
    }

    // ------------------------------------------------------------------------
    //  最小 HTTP サーバ (TcpListener ベース。管理者権限 / URLACL 不要)
    // ------------------------------------------------------------------------
    class HttpServer
    {
        Config config;
        TcpListener listener;
        volatile bool running;
        // セッション管理 (履歴は Windows AppData 配下に永続化)
        SessionStore store = new SessionStore();
        object sessionLock = new object();
        // 送信中かどうか (単一利用者前提のためサーバ側で一元管理)
        volatile bool busy;
        volatile string busySession;

        // 組込み Web UI (index.html)。embed_ui.py により base64 文字列として埋め込まれる。
        const string EmbeddedIndexHtml = "PCFkb2N0eXBlIGh0bWw+CjxodG1sIGxhbmc9ImphIj4KPGhlYWQ+CjxtZXRhIGNoYXJzZXQ9InV0Zi04IiAvPgo8bWV0YSBuYW1lPSJ2aWV3cG9ydCIgY29udGVudD0id2lkdGg9ZGV2aWNlLXdpZHRoLCBpbml0aWFsLXNjYWxlPTEsIHZpZXdwb3J0LWZpdD1jb3ZlciIgLz4KPG1ldGEgbmFtZT0idGhlbWUtY29sb3IiIGNvbnRlbnQ9IiMwOTA5MGIiIC8+Cjx0aXRsZT5hZ2VudHdlYjwvdGl0bGU+CjxzY3JpcHQ+CnRyeXt2YXIgX190PWxvY2FsU3RvcmFnZS5nZXRJdGVtKCJhaWFnZW50LnRoZW1lIil8fCJkYXJrIjtkb2N1bWVudC5kb2N1bWVudEVsZW1lbnQuc2V0QXR0cmlidXRlKCJkYXRhLXRoZW1lIixfX3QpO31jYXRjaChlKXt9Cjwvc2NyaXB0Pgo8c3R5bGU+CiAgOnJvb3QgewogICAgY29sb3Itc2NoZW1lOiBkYXJrOwogICAgZm9udC1mYW1pbHk6IC1hcHBsZS1zeXN0ZW0sIEJsaW5rTWFjU3lzdGVtRm9udCwgIlNlZ29lIFVJIiwgc2Fucy1zZXJpZjsKICAgIGJhY2tncm91bmQ6ICMwOTA5MGI7CiAgICBjb2xvcjogI2U4ZThlODsKICAgIC0tYmc6ICMwOTA5MGI7CiAgICAtLXBhbmVsOiAjMWExYTFhOwogICAgLS1wYW5lbC0yOiAjMjQyNDI0OwogICAgLS1wYW5lbC0zOiAjMmUyZTJlOwogICAgLS1ib3JkZXI6ICMzYTNhM2E7CiAgICAtLXRleHQ6ICNlOGU4ZTg7CiAgICAtLW11dGVkOiAjOWNhM2FmOwogICAgLS1hY2NlbnQ6ICM2MGE1ZmE7CiAgICAtLWFjY2VudC1kaW06ICMxYzNhNWU7CiAgICAtLWRhbmdlcjogI2Y4NzE3MTsKICAgIC0tcmFkaXVzOiA2cHg7CiAgfQogICogeyBib3gtc2l6aW5nOiBib3JkZXItYm94OyB9CiAgaHRtbCwgYm9keSB7IGhlaWdodDogMTAwJTsgfQogIGJvZHkgewogICAgbWFyZ2luOiAwOwogICAgZGlzcGxheTogZmxleDsKICAgIGZsZXgtZGlyZWN0aW9uOiBjb2x1bW47CiAgICBtaW4taGVpZ2h0OiAxMDBkdmg7CiAgfQoKICAvKiAtLS0tLS0tLS0tIHRvcCBiYXIgLS0tLS0tLS0tLSAqLwogIGhlYWRlciB7CiAgICBkaXNwbGF5OiBmbGV4OwogICAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICAgIGdhcDogMTJweDsKICAgIHBhZGRpbmc6IDEwcHggMThweDsKICAgIGJvcmRlci1ib3R0b206IDFweCBzb2xpZCB2YXIoLS1ib3JkZXIpOwogICAgYmFja2dyb3VuZDogdmFyKC0tcGFuZWwpOwogIH0KICAubG9nbyB7CiAgICB3aWR0aDogMzRweDsgaGVpZ2h0OiAzNHB4OwogICAgYm9yZGVyLXJhZGl1czogOXB4OwogICAgYmFja2dyb3VuZDogIzA5MDkwYjsKICAgIGRpc3BsYXk6IGdyaWQ7IHBsYWNlLWl0ZW1zOiBjZW50ZXI7CiAgICBmbGV4OiBub25lOwogIH0KICAubG9nbyBzdmcgeyB3aWR0aDogMjRweDsgaGVpZ2h0OiAyNHB4OyB9CiAgLmhlYWQtdGl0bGUgeyBkaXNwbGF5OiBmbGV4OyBmbGV4LWRpcmVjdGlvbjogY29sdW1uOyBsaW5lLWhlaWdodDogMS4yOyB9CiAgLmhlYWQtdGl0bGUgLnQgeyBmb250LXNpemU6IDE1cHg7IGZvbnQtd2VpZ2h0OiA2NTA7IGxldHRlci1zcGFjaW5nOiAwOyB9CiAgLmhlYWQtdGl0bGUgLnMgeyBmb250LXNpemU6IDEycHg7IGNvbG9yOiB2YXIoLS1tdXRlZCk7IH0KICAuaGVhZC1hY3Rpb25zIHsgbWFyZ2luLWxlZnQ6IGF1dG87IGRpc3BsYXk6IGZsZXg7IGdhcDogOHB4OyBhbGlnbi1pdGVtczogY2VudGVyOyB9CiAgLmJhZGdlIHsKICAgIGZvbnQtc2l6ZTogMTJweDsgY29sb3I6IHZhcigtLW11dGVkKTsKICAgIGJhY2tncm91bmQ6IHZhcigtLXBhbmVsLTIpOwogICAgYm9yZGVyOiAxcHggc29saWQgdmFyKC0tYm9yZGVyKTsKICAgIGJvcmRlci1yYWRpdXM6IDk5OXB4OwogICAgcGFkZGluZzogNHB4IDEycHg7CiAgfQogIC5iYWRnZSBiIHsgY29sb3I6IHZhcigtLXRleHQpOyBmb250LXdlaWdodDogNjAwOyB9CgogIC8qIOODqeOCpOODiOODhuODvOODniAqLwogIDpyb290W2RhdGEtdGhlbWU9ImxpZ2h0Il0gewogICAgY29sb3Itc2NoZW1lOiBsaWdodDsKICAgIGJhY2tncm91bmQ6ICNmNGY1Zjc7CiAgICBjb2xvcjogIzFhMWMxZjsKICAgIC0tYmc6ICNmNGY1Zjc7CiAgICAtLXBhbmVsOiAjZmZmZmZmOwogICAgLS1wYW5lbC0yOiAjZWNlY2VlOwogICAgLS1wYW5lbC0zOiAjZTJlM2U3OwogICAgLS1ib3JkZXI6ICNkNWQ3ZGM7CiAgICAtLXRleHQ6ICMxYTFjMWY7CiAgICAtLW11dGVkOiAjNmI3MjgwOwogICAgLS1hY2NlbnQ6ICMyNTYzZWI7CiAgICAtLWFjY2VudC1kaW06ICNkYmVhZmU7CiAgICAtLWRhbmdlcjogI2RjMjYyNjsKICB9CiAgOnJvb3RbZGF0YS10aGVtZT0ibGlnaHQiXSAubXNnLnVzZXIgLmJ1YmJsZSB7IGJhY2tncm91bmQ6ICNjZmU0ZmI7IGNvbG9yOiAjMGIxMjIwOyB9CiAgOnJvb3RbZGF0YS10aGVtZT0ibGlnaHQiXSAubXNnLnVzZXIgLnRzIHsgY29sb3I6IHJnYmEoMTEsMTgsMzIsLjQ1KTsgfQogIDpyb290W2RhdGEtdGhlbWU9ImxpZ2h0Il0gYnV0dG9uLnNlbmQgeyBjb2xvcjogI2ZmZmZmZjsgfQogIDpyb290W2RhdGEtdGhlbWU9ImxpZ2h0Il0gLm1kIHByZSB7IGJhY2tncm91bmQ6ICNmMGYxZjQ7IH0KICA6cm9vdFtkYXRhLXRoZW1lPSJsaWdodCJdIC5sb2dvIHsgYmFja2dyb3VuZDogI2U4ZWFlZTsgfQogIDpyb290W2RhdGEtdGhlbWU9ImxpZ2h0Il0gLmxvZ28gc3ZnIHsgZmlsbDogIzI1NjNlYjsgfQogIC5sYXlvdXQgeyBkaXNwbGF5OiBmbGV4OyBmbGV4OiAxOyBtaW4taGVpZ2h0OiAwOyB9CiAgLnNpZGViYXIgewogICAgd2lkdGg6IDI1MHB4OwogICAgZmxleDogbm9uZTsKICAgIGJvcmRlci1yaWdodDogMXB4IHNvbGlkIHZhcigtLWJvcmRlcik7CiAgICBiYWNrZ3JvdW5kOiB2YXIoLS1wYW5lbCk7CiAgICBkaXNwbGF5OiBmbGV4OwogICAgZmxleC1kaXJlY3Rpb246IGNvbHVtbjsKICAgIG1pbi1oZWlnaHQ6IDA7CiAgfQogIC5zaWRlYmFyLWhlYWQgewogICAgZGlzcGxheTogZmxleDsKICAgIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAgICBqdXN0aWZ5LWNvbnRlbnQ6IHNwYWNlLWJldHdlZW47CiAgICBwYWRkaW5nOiAxMnB4IDE0cHg7CiAgICBib3JkZXItYm90dG9tOiAxcHggc29saWQgdmFyKC0tYm9yZGVyKTsKICB9CiAgLnNpZGViYXItdGl0bGUgeyBmb250LXNpemU6IDEzcHg7IGZvbnQtd2VpZ2h0OiA2NTA7IGNvbG9yOiB2YXIoLS1tdXRlZCk7IGxldHRlci1zcGFjaW5nOiAuMDJlbTsgfQogIC5uZXctYnRuIHsKICAgIHdpZHRoOiAyOHB4OyBoZWlnaHQ6IDI4cHg7CiAgICBib3JkZXI6IDFweCBzb2xpZCB2YXIoLS1ib3JkZXIpOwogICAgYm9yZGVyLXJhZGl1czogdmFyKC0tcmFkaXVzKTsKICAgIGJhY2tncm91bmQ6IHZhcigtLXBhbmVsLTIpOwogICAgY29sb3I6IHZhcigtLXRleHQpOwogICAgZm9udDogaW5oZXJpdDsgZm9udC1zaXplOiAxNnB4OyBsaW5lLWhlaWdodDogMTsKICAgIGN1cnNvcjogcG9pbnRlcjsKICAgIGRpc3BsYXk6IGdyaWQ7IHBsYWNlLWl0ZW1zOiBjZW50ZXI7CiAgfQogIC5uZXctYnRuOmhvdmVyIHsgYmFja2dyb3VuZDogdmFyKC0tcGFuZWwtMyk7IH0KICAuc2Vzc2lvbi1saXN0IHsgZmxleDogMTsgb3ZlcmZsb3cteTogYXV0bzsgcGFkZGluZzogNnB4OyBtaW4taGVpZ2h0OiAwOyB9CiAgLnNlc3Npb24tbGlzdDo6LXdlYmtpdC1zY3JvbGxiYXIgeyB3aWR0aDogMTBweDsgfQogIC5zZXNzaW9uLWxpc3Q6Oi13ZWJraXQtc2Nyb2xsYmFyLXRodW1iIHsgYmFja2dyb3VuZDogdmFyKC0tYm9yZGVyKTsgYm9yZGVyLXJhZGl1czogNXB4OyB9CiAgLnNlc3Npb24taXRlbSB7CiAgICBkaXNwbGF5OiBmbGV4OwogICAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICAgIGdhcDogOHB4OwogICAgcGFkZGluZzogOXB4IDEwcHg7CiAgICBib3JkZXItcmFkaXVzOiB2YXIoLS1yYWRpdXMpOwogICAgY3Vyc29yOiBwb2ludGVyOwogICAgY29sb3I6IHZhcigtLW11dGVkKTsKICAgIG1hcmdpbi1ib3R0b206IDJweDsKICB9CiAgLnNlc3Npb24taXRlbTpob3ZlciB7IGJhY2tncm91bmQ6IHZhcigtLXBhbmVsLTIpOyBjb2xvcjogdmFyKC0tdGV4dCk7IH0KICAuc2Vzc2lvbi1pdGVtLmFjdGl2ZSB7IGJhY2tncm91bmQ6IHZhcigtLXBhbmVsLTIpOyBjb2xvcjogdmFyKC0tdGV4dCk7IGJveC1zaGFkb3c6IGluc2V0IDJweCAwIDAgdmFyKC0tYWNjZW50KTsgfQogIC5zZXNzaW9uLWl0ZW0gLnMtdHh0IHsKICAgIGZsZXg6IDE7IG1pbi13aWR0aDogMDsKICAgIGZvbnQtc2l6ZTogMTNweDsKICAgIHdoaXRlLXNwYWNlOiBub3dyYXA7CiAgICBvdmVyZmxvdzogaGlkZGVuOwogICAgdGV4dC1vdmVyZmxvdzogZWxsaXBzaXM7CiAgfQogIC5zZXNzaW9uLWl0ZW0gLnMtZGVsLCAuc2Vzc2lvbi1pdGVtIC5zLXJlbiB7CiAgICBmbGV4OiBub25lOwogICAgd2lkdGg6IDIwcHg7IGhlaWdodDogMjBweDsKICAgIGJvcmRlcjogbm9uZTsKICAgIGJvcmRlci1yYWRpdXM6IDRweDsKICAgIGJhY2tncm91bmQ6IHRyYW5zcGFyZW50OwogICAgY29sb3I6IHZhcigtLW11dGVkKTsKICAgIGZvbnQ6IGluaGVyaXQ7IGZvbnQtc2l6ZTogMTRweDsgbGluZS1oZWlnaHQ6IDE7CiAgICBjdXJzb3I6IHBvaW50ZXI7CiAgICBkaXNwbGF5OiBncmlkOyBwbGFjZS1pdGVtczogY2VudGVyOwogICAgb3BhY2l0eTogMDsKICB9CiAgLnNlc3Npb24taXRlbTpob3ZlciAucy1kZWwsIC5zZXNzaW9uLWl0ZW06aG92ZXIgLnMtcmVuIHsgb3BhY2l0eTogMTsgfQogIC5zZXNzaW9uLWl0ZW0gLnMtZGVsOmhvdmVyIHsgYmFja2dyb3VuZDogdmFyKC0tcGFuZWwtMyk7IGNvbG9yOiB2YXIoLS1kYW5nZXIpOyB9CiAgLnNlc3Npb24taXRlbSAucy1yZW46aG92ZXIgeyBiYWNrZ3JvdW5kOiB2YXIoLS1wYW5lbC0zKTsgY29sb3I6IHZhcigtLWFjY2VudCk7IH0KICAuc2Vzc2lvbi1pdGVtIC5zLWVkaXQgewogICAgZmxleDogMTsgbWluLXdpZHRoOiAwOwogICAgd2lkdGg6IDEwMCU7CiAgICBiYWNrZ3JvdW5kOiB2YXIoLS1wYW5lbC0zKTsKICAgIGNvbG9yOiB2YXIoLS10ZXh0KTsKICAgIGJvcmRlcjogMXB4IHNvbGlkIHZhcigtLWFjY2VudCk7CiAgICBib3JkZXItcmFkaXVzOiA0cHg7CiAgICBmb250OiBpbmhlcml0OyBmb250LXNpemU6IDEzcHg7CiAgICBwYWRkaW5nOiAycHggNnB4OwogICAgb3V0bGluZTogbm9uZTsKICB9CgogIC8qIC0tLS0tLS0tLS0gY2hhdCAtLS0tLS0tLS0tICovCiAgbWFpbiB7CiAgICBmbGV4OiAxOwogICAgb3ZlcmZsb3cteTogYXV0bzsKICAgIC8qIHN1YnRsZSBzY3JvbGxiYXIgKi8KICB9CiAgbWFpbjo6LXdlYmtpdC1zY3JvbGxiYXIgeyB3aWR0aDogMTJweDsgfQogIG1haW46Oi13ZWJraXQtc2Nyb2xsYmFyLXRodW1iIHsgYmFja2dyb3VuZDogdmFyKC0tYm9yZGVyKTsgYm9yZGVyLXJhZGl1czogNnB4OyB9CiAgLmNoYXQgewogICAgbWF4LXdpZHRoOiA4NjBweDsKICAgIG1hcmdpbjogMCBhdXRvOwogICAgcGFkZGluZzogMjRweCAyMHB4IDQwcHg7CiAgICBkaXNwbGF5OiBmbGV4OwogICAgZmxleC1kaXJlY3Rpb246IGNvbHVtbjsKICAgIGdhcDogMjBweDsKICB9CgogIC8qIG1lc3NhZ2Ugcm93cyAqLwogIC5tc2cgeyBkaXNwbGF5OiBmbGV4OyBnYXA6IDEycHg7IGFsaWduLWl0ZW1zOiBmbGV4LXN0YXJ0OyB9CiAgLm1zZy51c2VyIHsgZmxleC1kaXJlY3Rpb246IHJvdy1yZXZlcnNlOyB9CiAgLmJ1YmJsZSB7CiAgICBtYXgtd2lkdGg6IDc4JTsKICAgIHBhZGRpbmc6IDdweCAxMHB4OwogICAgYm9yZGVyLXJhZGl1czogdmFyKC0tcmFkaXVzKTsKICAgIGZvbnQtc2l6ZTogMTJweDsKICAgIGxpbmUtaGVpZ2h0OiAxLjU1OwogICAgd29yZC13cmFwOiBicmVhay13b3JkOwogICAgb3ZlcmZsb3ctd3JhcDogYW55d2hlcmU7CiAgfQogIC50cyB7CiAgICBmb250LXNpemU6IDEwcHg7CiAgICBjb2xvcjogdmFyKC0tbXV0ZWQpOwogICAgb3BhY2l0eTogLjg1OwogICAgbWFyZ2luLXRvcDogNHB4OwogIH0KICAubXNnLnVzZXIgLnRzIHsgdGV4dC1hbGlnbjogcmlnaHQ7IGNvbG9yOiByZ2JhKDExLDE4LDMyLC41NSk7IG9wYWNpdHk6IDE7IH0KICAubXNnLmFzc2lzdGFudCAudHMgeyB0ZXh0LWFsaWduOiBsZWZ0OyB9CiAgLm1zZy5hc3Npc3RhbnQgLmJ1YmJsZSB7CiAgICBiYWNrZ3JvdW5kOiB2YXIoLS1wYW5lbCk7CiAgICBib3JkZXI6IDFweCBzb2xpZCB2YXIoLS1ib3JkZXIpOwogICAgY29sb3I6IHZhcigtLXRleHQpOwogIH0KICAubXNnLnVzZXIgLmJ1YmJsZSB7CiAgICBiYWNrZ3JvdW5kOiB2YXIoLS1hY2NlbnQpOwogICAgYm9yZGVyOiAxcHggc29saWQgdHJhbnNwYXJlbnQ7CiAgICBjb2xvcjogIzBiMTIyMDsKICAgIGZvbnQtd2VpZ2h0OiA1MDA7CiAgICB3aGl0ZS1zcGFjZTogcHJlLXdyYXA7CiAgfQoKICAvKiBtYXJrZG93biAqLwogIC5tZCB7IGZvbnQtc2l6ZTogMTJweDsgfQogIC5tZCA+IDpmaXJzdC1jaGlsZCB7IG1hcmdpbi10b3A6IDA7IH0KICAubWQgPiA6bGFzdC1jaGlsZCB7IG1hcmdpbi1ib3R0b206IDA7IH0KICAubWQgaDEsIC5tZCBoMiwgLm1kIGgzLCAubWQgaDQgeyBjb2xvcjogdmFyKC0tdGV4dCk7IGxpbmUtaGVpZ2h0OiAxLjM7IG1hcmdpbjogMThweCAwIDhweDsgfQogIC5tZCBoMSB7IGZvbnQtc2l6ZTogMjBweDsgfSAubWQgaDIgeyBmb250LXNpemU6IDE3cHg7IH0gLm1kIGgzIHsgZm9udC1zaXplOiAxNXB4OyB9CiAgLm1kIHAgeyBtYXJnaW46IDhweCAwOyB9CiAgLm1kIHVsLCAubWQgb2wgeyBtYXJnaW46IDhweCAwOyBwYWRkaW5nLWxlZnQ6IDIycHg7IH0KICAubWQgbGkgeyBtYXJnaW46IDNweCAwOyB9CiAgLm1kIGEgeyBjb2xvcjogdmFyKC0tYWNjZW50KTsgfQogIC5tZCBjb2RlIHsKICAgIGJhY2tncm91bmQ6IHZhcigtLXBhbmVsLTMpOwogICAgYm9yZGVyOiAxcHggc29saWQgdmFyKC0tYm9yZGVyKTsKICAgIGJvcmRlci1yYWRpdXM6IDRweDsKICAgIHBhZGRpbmc6IDJweCA1cHg7CiAgICBmb250LWZhbWlseTogIlNGIE1vbm8iLCAiQ2FzY2FkaWEgQ29kZSIsIENvbnNvbGFzLCBtb25vc3BhY2U7CiAgICBmb250LXNpemU6IDEzcHg7CiAgfQogIC5tZCBwcmUgewogICAgYmFja2dyb3VuZDogIzExMTExNDsKICAgIGJvcmRlcjogMXB4IHNvbGlkIHZhcigtLWJvcmRlcik7CiAgICBib3JkZXItcmFkaXVzOiB2YXIoLS1yYWRpdXMpOwogICAgcGFkZGluZzogMTJweDsKICAgIG92ZXJmbG93LXg6IGF1dG87CiAgfQogIC5tZCBwcmUgY29kZSB7IGJhY2tncm91bmQ6IG5vbmU7IGJvcmRlcjogbm9uZTsgcGFkZGluZzogMDsgZm9udC1zaXplOiAxM3B4OyBsaW5lLWhlaWdodDogMS41OyB9CiAgLm1kIGJsb2NrcXVvdGUgewogICAgbWFyZ2luOiAxMHB4IDA7IHBhZGRpbmc6IDJweCAxNHB4OwogICAgYm9yZGVyLWxlZnQ6IDNweCBzb2xpZCB2YXIoLS1hY2NlbnQpOwogICAgY29sb3I6IHZhcigtLW11dGVkKTsKICB9CiAgLm1lcm1haWQgewogICAgYmFja2dyb3VuZDogdmFyKC0tcGFuZWwtMik7CiAgICBib3JkZXI6IDFweCBzb2xpZCB2YXIoLS1ib3JkZXIpOwogICAgYm9yZGVyLXJhZGl1czogdmFyKC0tcmFkaXVzKTsKICAgIG1hcmdpbjogMTBweCAwOwogICAgcGFkZGluZzogOHB4OwogICAgb3ZlcmZsb3cteDogYXV0bzsKICAgIHRleHQtYWxpZ246IGNlbnRlcjsKICB9CiAgLm1lcm1haWQgc3ZnIHsgbWF4LXdpZHRoOiAxMDAlOyBoZWlnaHQ6IGF1dG87IH0KICAubWVybWFpZC1sb2FkaW5nIHsgZm9udC1zaXplOiAxMnB4OyBjb2xvcjogdmFyKC0tbXV0ZWQpOyBwYWRkaW5nOiAxMnB4OyB9CiAgLm1lcm1haWQtZmFsbGJhY2sgeyBmb250LXNpemU6IDEycHg7IHRleHQtYWxpZ246IGxlZnQ7IGJhY2tncm91bmQ6IHZhcigtLXBhbmVsLTMpOyBwYWRkaW5nOiA4cHg7IGJvcmRlci1yYWRpdXM6IDRweDsgd2hpdGUtc3BhY2U6IHByZS13cmFwOyB9CiAgLm1lcm1haWQtbm90ZSB7IGZvbnQtc2l6ZTogMTFweDsgY29sb3I6IHZhcigtLWRhbmdlcik7IG1hcmdpbi10b3A6IDRweDsgfQogIC5tZCB0YWJsZSB7IGJvcmRlci1jb2xsYXBzZTogY29sbGFwc2U7IG1hcmdpbjogMTBweCAwOyB9CiAgLm1kIHRoLCAubWQgdGQgeyBib3JkZXI6IDFweCBzb2xpZCB2YXIoLS1ib3JkZXIpOyBwYWRkaW5nOiA2cHggMTBweDsgfQogIC5tZCB0aCB7IGJhY2tncm91bmQ6IHZhcigtLXBhbmVsLTIpOyB9CiAgLm1kIGhyIHsgYm9yZGVyOiBub25lOyBib3JkZXItdG9wOiAxcHggc29saWQgdmFyKC0tYm9yZGVyKTsgbWFyZ2luOiAxNnB4IDA7IH0KCiAgLyogdHlwaW5nIGluZGljYXRvciAqLwogIC50eXBpbmcgeyBkaXNwbGF5OiBpbmxpbmUtZmxleDsgZ2FwOiA0cHg7IHBhZGRpbmc6IDZweCAycHg7IH0KICAudHlwaW5nIHNwYW4gewogICAgd2lkdGg6IDdweDsgaGVpZ2h0OiA3cHg7IGJvcmRlci1yYWRpdXM6IDUwJTsKICAgIGJhY2tncm91bmQ6IHZhcigtLW11dGVkKTsKICAgIGFuaW1hdGlvbjogYmxpbmsgMS4ycyBpbmZpbml0ZTsKICB9CiAgLnR5cGluZyBzcGFuOm50aC1jaGlsZCgyKSB7IGFuaW1hdGlvbi1kZWxheTogLjJzOyB9CiAgLnR5cGluZyBzcGFuOm50aC1jaGlsZCgzKSB7IGFuaW1hdGlvbi1kZWxheTogLjRzOyB9CiAgLnByb2dyZXNzIHsgbWFyZ2luLXRvcDogOXB4OyBkaXNwbGF5OiBmbGV4OyBmbGV4LWRpcmVjdGlvbjogY29sdW1uOyBnYXA6IDNweDsgfQogIC5wc3RlcCB7IGZvbnQtc2l6ZTogMTFweDsgY29sb3I6IHZhcigtLWFjY2VudCk7IH0KICBAa2V5ZnJhbWVzIGJsaW5rIHsgMCUsODAlLDEwMCUgeyBvcGFjaXR5Oi4yNSB9IDQwJSB7IG9wYWNpdHk6MSB9IH0KCiAgLyogZXJyb3IgKi8KICAuZXJyIHsgY29sb3I6IHZhcigtLWRhbmdlcik7IGZvbnQtc2l6ZTogMTNweDsgcGFkZGluZzogNHB4IDJweDsgfQoKICAvKiAtLS0tLS0tLS0tIGNvbXBvc2VyIC0tLS0tLS0tLS0gKi8KICBmb290ZXIgewogICAgYm9yZGVyLXRvcDogMXB4IHNvbGlkIHZhcigtLWJvcmRlcik7CiAgICBiYWNrZ3JvdW5kOiB2YXIoLS1wYW5lbCk7CiAgICBwYWRkaW5nOiAxNHB4IDE4cHggY2FsYygxNHB4ICsgZW52KHNhZmUtYXJlYS1pbnNldC1ib3R0b20pKTsKICB9CiAgLmNvbXBvc2VyIHsKICAgIG1heC13aWR0aDogOTQwcHg7CiAgICBtYXJnaW46IDAgYXV0bzsKICAgIGRpc3BsYXk6IGZsZXg7CiAgICBhbGlnbi1pdGVtczogZmxleC1lbmQ7CiAgICBnYXA6IDEwcHg7CiAgICBmbGV4LXdyYXA6IHdyYXA7CiAgfQogIC5jb21wb3Nlci1iYXIgewogICAgbWF4LXdpZHRoOiA5NDBweDsKICAgIG1hcmdpbjogOHB4IGF1dG8gMDsKICAgIGRpc3BsYXk6IGZsZXg7CiAgICBhbGlnbi1pdGVtczogY2VudGVyOwogICAgZ2FwOiA4cHg7CiAgICBmbGV4LXdyYXA6IHdyYXA7CiAgICBmb250LXNpemU6IDExcHg7CiAgICBjb2xvcjogdmFyKC0tbXV0ZWQpOwogIH0KICAuY29tcG9zZXItYmFyIC5iYWRnZSB7IGZvbnQtc2l6ZTogMTBweDsgcGFkZGluZzogMnB4IDdweDsgfQogIC50b29scy10b2dnbGUgeyBjdXJzb3I6IHBvaW50ZXI7IGZvbnQ6IGluaGVyaXQ7IH0KICAudGhlbWUtbGluayB7CiAgICBjdXJzb3I6IHBvaW50ZXI7CiAgICBmb250OiBpbmhlcml0OwogICAgYm9yZGVyOiAxcHggc29saWQgdmFyKC0tYm9yZGVyKTsKICAgIGJvcmRlci1yYWRpdXM6IHZhcigtLXJhZGl1cyk7CiAgICBiYWNrZ3JvdW5kOiB2YXIoLS1wYW5lbC0yKTsKICAgIGNvbG9yOiB2YXIoLS10ZXh0KTsKICAgIHBhZGRpbmc6IDJweCA5cHg7CiAgfQogIC50aGVtZS1saW5rOmhvdmVyIHsgYmFja2dyb3VuZDogdmFyKC0tcGFuZWwtMyk7IH0KICAuc2lkZWJhci5oaWRkZW4geyBkaXNwbGF5OiBub25lOyB9CiAgLnNpZGUtYWN0aW9ucyB7IGRpc3BsYXk6IGZsZXg7IGdhcDogNHB4OyBhbGlnbi1pdGVtczogY2VudGVyOyB9CiAgLmN0bC14IHsgd2lkdGg6IDI0cHg7IGhlaWdodDogMjRweDsgZm9udC1zaXplOiAxNHB4OyB9CiAgLmZhYiB7CiAgICBwb3NpdGlvbjogZml4ZWQ7CiAgICB0b3A6IDEwcHg7IGxlZnQ6IDEwcHg7CiAgICB6LWluZGV4OiA1MDsKICAgIHdpZHRoOiAzNHB4OyBoZWlnaHQ6IDM0cHg7CiAgICBib3JkZXI6IDFweCBzb2xpZCB2YXIoLS1ib3JkZXIpOwogICAgYm9yZGVyLXJhZGl1czogdmFyKC0tcmFkaXVzKTsKICAgIGJhY2tncm91bmQ6IHJnYmEoMCwwLDAsLjQ1KTsgICAvKiDljYrpgI/mmI4gKi8KICAgIGNvbG9yOiB2YXIoLS10ZXh0KTsKICAgIGZvbnQtc2l6ZTogMTZweDsgbGluZS1oZWlnaHQ6IDE7CiAgICBjdXJzb3I6IHBvaW50ZXI7CiAgICBkaXNwbGF5OiBub25lOyAgICAgICAgICAgICAgICAgLyog44K144Kk44OJ44OQ44O86Z2e6KGo56S65pmC44Gr44Gu44G/IGFwcGx5U2lkZWJhciDjgYzooajnpLogKi8KICAgIHBsYWNlLWl0ZW1zOiBjZW50ZXI7CiAgICBvcGFjaXR5OiAuNTU7CiAgICBiYWNrZHJvcC1maWx0ZXI6IGJsdXIoMnB4KTsKICB9CiAgLmZhYjpob3ZlciB7IG9wYWNpdHk6IDE7IH0KICA6cm9vdFtkYXRhLXRoZW1lPSJsaWdodCJdIC5mYWIgeyBiYWNrZ3JvdW5kOiByZ2JhKDI1NSwyNTUsMjU1LC42KTsgfQogIC5jb21wb3Nlci1sZWZ0IHsKICAgIGRpc3BsYXk6IGZsZXg7CiAgICBmbGV4LWRpcmVjdGlvbjogY29sdW1uOwogICAgZ2FwOiA0cHg7CiAgICBmbGV4OiBub25lOwogICAgcGFkZGluZy1ib3R0b206IDRweDsKICB9CiAgLmN0bCB7CiAgICB3aWR0aDogMzZweDsgaGVpZ2h0OiAyOHB4OwogICAgYm9yZGVyOiAxcHggc29saWQgdmFyKC0tYm9yZGVyKTsKICAgIGJvcmRlci1yYWRpdXM6IHZhcigtLXJhZGl1cyk7CiAgICBiYWNrZ3JvdW5kOiB2YXIoLS1wYW5lbC0yKTsKICAgIGNvbG9yOiB2YXIoLS1tdXRlZCk7CiAgICBmb250LXNpemU6IDE0cHg7IGxpbmUtaGVpZ2h0OiAxOwogICAgY3Vyc29yOiBwb2ludGVyOwogICAgZGlzcGxheTogZ3JpZDsgcGxhY2UtaXRlbXM6IGNlbnRlcjsKICB9CiAgLmN0bDpob3ZlciB7IGJhY2tncm91bmQ6IHZhcigtLXBhbmVsLTMpOyBjb2xvcjogdmFyKC0tdGV4dCk7IH0KICAuY3RsLnN0b3AgeyBjb2xvcjogdmFyKC0tZGFuZ2VyKTsgfQogIHRleHRhcmVhIHsKICAgIHdpZHRoOiAxMDAlOwogICAgcmVzaXplOiBub25lOwogICAgYmFja2dyb3VuZDogdmFyKC0tcGFuZWwtMik7CiAgICBib3JkZXI6IDFweCBzb2xpZCB2YXIoLS1ib3JkZXIpOwogICAgYm9yZGVyLXJhZGl1czogdmFyKC0tcmFkaXVzKTsKICAgIGNvbG9yOiB2YXIoLS10ZXh0KTsKICAgIGZvbnQ6IGluaGVyaXQ7CiAgICBmb250LXNpemU6IDE1cHg7CiAgICBsaW5lLWhlaWdodDogMS41OwogICAgcGFkZGluZzogMTJweCAxNHB4OwogICAgbWluLWhlaWdodDogNTZweDsKICAgIG1heC1oZWlnaHQ6IDIwMHB4OwogICAgb3V0bGluZTogbm9uZTsKICB9CiAgdGV4dGFyZWE6OnBsYWNlaG9sZGVyIHsgY29sb3I6ICM2YjcyODA7IH0KICB0ZXh0YXJlYTpmb2N1cyB7IGJvcmRlci1jb2xvcjogdmFyKC0tYWNjZW50KTsgfQogIGJ1dHRvbi5zZW5kIHsKICAgIGZsZXg6IG5vbmU7CiAgICBoZWlnaHQ6IDUwcHg7IG1pbi13aWR0aDogNjRweDsKICAgIGJvcmRlcjogbm9uZTsKICAgIGJvcmRlci1yYWRpdXM6IHZhcigtLXJhZGl1cyk7CiAgICBiYWNrZ3JvdW5kOiB2YXIoLS1hY2NlbnQpOwogICAgY29sb3I6ICMwYjEyMjA7CiAgICBmb250OiBpbmhlcml0OwogICAgZm9udC13ZWlnaHQ6IDY1MDsKICAgIGN1cnNvcjogcG9pbnRlcjsKICAgIGRpc3BsYXk6IGdyaWQ7IHBsYWNlLWl0ZW1zOiBjZW50ZXI7CiAgfQogIGJ1dHRvbi5zZW5kOmRpc2FibGVkIHsgb3BhY2l0eTogLjQ1OyBjdXJzb3I6IG5vdC1hbGxvd2VkOyB9CiAgYnV0dG9uLnNlbmQ6aG92ZXI6bm90KDpkaXNhYmxlZCkgeyBiYWNrZ3JvdW5kOiAjN2ViM2ZiOyB9CiAgYnV0dG9uLmdob3N0IHsKICAgIGJvcmRlcjogMXB4IHNvbGlkIHZhcigtLWJvcmRlcik7CiAgICBiYWNrZ3JvdW5kOiB2YXIoLS1wYW5lbC0yKTsKICAgIGNvbG9yOiB2YXIoLS1tdXRlZCk7CiAgICBoZWlnaHQ6IDM4cHg7IHBhZGRpbmc6IDAgMTRweDsKICAgIGJvcmRlci1yYWRpdXM6IHZhcigtLXJhZGl1cyk7CiAgICBmb250OiBpbmhlcml0OyBmb250LXNpemU6IDEzcHg7CiAgICBjdXJzb3I6IHBvaW50ZXI7CiAgfQogIGJ1dHRvbi5naG9zdDpob3ZlciB7IGJhY2tncm91bmQ6IHZhcigtLXBhbmVsLTMpOyBjb2xvcjogdmFyKC0tdGV4dCk7IH0KICAuY29tcG9zZXIgLnNpZGUgeyBkaXNwbGF5OiBmbGV4OyBmbGV4LWRpcmVjdGlvbjogY29sdW1uOyBhbGlnbi1pdGVtczogc3RyZXRjaDsgZ2FwOiA4cHg7IGZsZXg6IDE7IG1pbi13aWR0aDogMDsgfQoKICBAbWVkaWEgKG1heC13aWR0aDogNzAwcHgpIHsKICAgIC5sYXlvdXQgeyBmbGV4LWRpcmVjdGlvbjogY29sdW1uOyB9CiAgICAuc2lkZWJhciB7IHdpZHRoOiAxMDAlOyBib3JkZXItcmlnaHQ6IG5vbmU7IGJvcmRlci1ib3R0b206IDFweCBzb2xpZCB2YXIoLS1ib3JkZXIpOyB9CiAgICAuc2Vzc2lvbi1saXN0IHsgZGlzcGxheTogZmxleDsgZmxleC13cmFwOiBub3dyYXA7IG92ZXJmbG93LXg6IGF1dG87IHBhZGRpbmc6IDZweDsgfQogICAgLnNlc3Npb24taXRlbSB7IGZsZXg6IG5vbmU7IG1pbi13aWR0aDogMTMwcHg7IH0KICB9CiAgQG1lZGlhIChtYXgtd2lkdGg6IDU2MHB4KSB7CiAgICAuYnViYmxlIHsgbWF4LXdpZHRoOiA4OCU7IH0KICB9Cjwvc3R5bGU+CjwvaGVhZD4KPGJvZHk+CiAgPGJ1dHRvbiB0eXBlPSJidXR0b24iIGlkPSJzaWRlVG9nZ2xlIiBjbGFzcz0iZmFiIiB0aXRsZT0i44K144Kk44OJ44OQ44O844KS6KGo56S6Ij7imLA8L2J1dHRvbj4KICA8ZGl2IGNsYXNzPSJsYXlvdXQiPgogICAgPGFzaWRlIGlkPSJzaWRlYmFyIiBjbGFzcz0ic2lkZWJhciI+CiAgICAgIDxkaXYgY2xhc3M9InNpZGViYXItaGVhZCI+CiAgICAgICAgPHNwYW4gY2xhc3M9InNpZGViYXItdGl0bGUiPlNlc3Npb25zPC9zcGFuPgogICAgICAgIDxkaXYgY2xhc3M9InNpZGUtYWN0aW9ucyI+CiAgICAgICAgICA8YnV0dG9uIHR5cGU9ImJ1dHRvbiIgaWQ9InNpZGVDbG9zZSIgY2xhc3M9ImN0bCBjdGwteCIgdGl0bGU9IuOCteOCpOODieODkOODvOOCkumWieOBmOOCiyI+w5c8L2J1dHRvbj4KICAgICAgICAgIDxidXR0b24gdHlwZT0iYnV0dG9uIiBpZD0ibmV3QnRuIiBjbGFzcz0ibmV3LWJ0biIgdGl0bGU9IuaWsOimj+OCu+ODg+OCt+ODp+ODsyI+77yLPC9idXR0b24+CiAgICAgICAgPC9kaXY+CiAgICAgIDwvZGl2PgogICAgICA8ZGl2IGlkPSJzZXNzaW9uTGlzdCIgY2xhc3M9InNlc3Npb24tbGlzdCI+PC9kaXY+CiAgICA8L2FzaWRlPgogICAgPG1haW4gaWQ9Im1haW4iPgogICAgICA8ZGl2IGNsYXNzPSJjaGF0IiBpZD0iY2hhdCI+PC9kaXY+CiAgICA8L21haW4+CiAgPC9kaXY+CgogIDxmb290ZXI+CiAgICA8ZGl2IGNsYXNzPSJjb21wb3NlciI+CiAgICAgIDxkaXYgY2xhc3M9InNpZGUiPgogICAgICAgIDx0ZXh0YXJlYSBpZD0iaW5wdXQiIHJvd3M9IjEiIHBsYWNlaG9sZGVyPSJBSSDjgqjjg7zjgrjjgqfjg7Pjg4jjgavos6rllY/jgZnjgovigKYgIChFbnRlciDjgafpgIHkv6EgLyBTaGlmdCtFbnRlciDjgafmlLnooYwpIj48L3RleHRhcmVhPgogICAgICA8L2Rpdj4KICAgICAgPGJ1dHRvbiB0eXBlPSJidXR0b24iIGNsYXNzPSJzZW5kIiBpZD0ic2VuZEJ0biIgZGlzYWJsZWQgdGl0bGU9IumAgeS/oSI+CiAgICAgICAgPHN2ZyB3aWR0aD0iMjAiIGhlaWdodD0iMjAiIHZpZXdCb3g9IjAgMCAyNCAyNCIgZmlsbD0iY3VycmVudENvbG9yIiBhcmlhLWhpZGRlbj0idHJ1ZSI+CiAgICAgICAgICA8cGF0aCBkPSJNMy40IDIwLjQgMjIgMTIgMy40IDMuNiAzLjQgMTAgMTUgMTIgMy40IDE0WiIvPgogICAgICAgIDwvc3ZnPgogICAgICA8L2J1dHRvbj4KICAgIDwvZGl2PgogICAgPGRpdiBjbGFzcz0iY29tcG9zZXItYmFyIj4KICAgICAgPHNwYW4gY2xhc3M9InN0YXR1cyIgaWQ9ImNvbm4iPmNvbm5lY3RpbmfigKY8L3NwYW4+CiAgICAgIDxzcGFuIGNsYXNzPSJiYWRnZSI+bW9kZWwgPGIgaWQ9Im1vZGVsTGFiZWwiPuKAlDwvYj48L3NwYW4+CiAgICAgIDxidXR0b24gdHlwZT0iYnV0dG9uIiBjbGFzcz0iYmFkZ2UgdG9vbHMtdG9nZ2xlIiBpZD0idG9vbHNCYWRnZSIgdGl0bGU9IuODhOODvOODq+Wun+ihjOOCkuWIh+OCiuabv+OBiCI+dG9vbHMg4oCUPC9idXR0b24+CiAgICAgIDxidXR0b24gdHlwZT0iYnV0dG9uIiBjbGFzcz0idGhlbWUtbGluayIgaWQ9InRoZW1lQnRuIiB0aXRsZT0i44OG44O844Oe5YiH5pu/Ij5jb2xvcnNjaGVtZSBkYXJrPC9idXR0b24+CiAgICA8L2Rpdj4KICA8L2Zvb3Rlcj4KCjxzY3JpcHQ+CiJ1c2Ugc3RyaWN0IjsKCi8qIC0tLS0tLS0tLS0gdGlueSBtYXJrZG93biByZW5kZXJlciAoWFNTLXNhZmU6IGVzY2FwZXMgdGhlbiBmb3JtYXRzKSAtLS0tLS0tLS0tICovCmZ1bmN0aW9uIGVzY2FwZUh0bWwocykgewogIHJldHVybiBzLnJlcGxhY2UoLyYvZywiJmFtcDsiKS5yZXBsYWNlKC88L2csIiZsdDsiKS5yZXBsYWNlKC8+L2csIiZndDsiKQogICAgICAgICAgLnJlcGxhY2UoLyIvZywiJnF1b3Q7IikucmVwbGFjZSgvJy9nLCImIzM5OyIpOwp9CmZ1bmN0aW9uIHJlbmRlck1hcmtkb3duKHNyYykgewogIC8vIG1lcm1haWQg44OW44Ot44OD44Kv44Gv44Ko44K544Kx44O844OX5YmN44Gr6YCA6YG/KOODl+ODrOODvOOCueODm+ODq+ODgOWMlikKICB2YXIgbWVybWFpZFNyYyA9IFtdOwogIHNyYyA9IFN0cmluZyhzcmMgfHwgIiIpLnJlcGxhY2UoL2BgYG1lcm1haWRccypcbihbXHNcU10qPylgYGAvZ2ksIGZ1bmN0aW9uKF8sIGNvZGUpewogICAgbWVybWFpZFNyYy5wdXNoKGNvZGUucmVwbGFjZSgvXG4kLywgIiIpKTsKICAgIHJldHVybiAiXHUwMDAwTSIgKyAobWVybWFpZFNyYy5sZW5ndGggLSAxKSArICJcdTAwMDAiOwogIH0pOwogIHNyYyA9IGVzY2FwZUh0bWwoc3JjIHx8ICIiKTsKICAvLyBjb2RlIGJsb2NrcwogIHNyYyA9IHNyYy5yZXBsYWNlKC9gYGAoW0EtWmEtejAtOV8rLV0qKVxuKFtcc1xTXSo/KWBgYC9nLCBmdW5jdGlvbihfLCBsYW5nLCBjb2RlKXsKICAgIHJldHVybiAiPHByZT48Y29kZT4iICsgY29kZS5yZXBsYWNlKC9cbiQvLCAiIikgKyAiPC9jb2RlPjwvcHJlPiI7CiAgfSk7CiAgLy8gaW5saW5lIGNvZGUKICBzcmMgPSBzcmMucmVwbGFjZSgvYChbXmBcbl0rKWAvZywgIjxjb2RlPiQxPC9jb2RlPiIpOwogIC8vIGhlYWRpbmdzCiAgc3JjID0gc3JjLnJlcGxhY2UoL14jIyMjIyMgKC4qKSQvZ20sICI8aDY+JDE8L2g2PiIpOwogIHNyYyA9IHNyYy5yZXBsYWNlKC9eIyMjIyMgKC4qKSQvZ20sICI8aDU+JDE8L2g1PiIpOwogIHNyYyA9IHNyYy5yZXBsYWNlKC9eIyMjIyAoLiopJC9nbSwgIjxoND4kMTwvaDQ+Iik7CiAgc3JjID0gc3JjLnJlcGxhY2UoL14jIyMgKC4qKSQvZ20sICI8aDM+JDE8L2gzPiIpOwogIHNyYyA9IHNyYy5yZXBsYWNlKC9eIyMgKC4qKSQvZ20sICI8aDI+JDE8L2gyPiIpOwogIHNyYyA9IHNyYy5yZXBsYWNlKC9eIyAoLiopJC9nbSwgIjxoMT4kMTwvaDE+Iik7CiAgLy8gbGlua3MKICBzcmMgPSBzcmMucmVwbGFjZSgvXFsoW15cXV0rKVxdXCgoaHR0cHM/OlwvXC9bXilcc10rKVwpL2csICc8YSBocmVmPSIkMiIgdGFyZ2V0PSJfYmxhbmsiIHJlbD0ibm9vcGVuZXIiPiQxPC9hPicpOwogIC8vIGJvbGQgLyBpdGFsaWMKICBzcmMgPSBzcmMucmVwbGFjZSgvXCpcKihbXipdKylcKlwqL2csICI8c3Ryb25nPiQxPC9zdHJvbmc+Iik7CiAgc3JjID0gc3JjLnJlcGxhY2UoLyhefFteKl0pXCooW14qXG5dKylcKi9nLCAiJDE8ZW0+JDI8L2VtPiIpOwogIC8vIGJsb2NrcXVvdGUKICBzcmMgPSBzcmMucmVwbGFjZSgvXiZndDsgKC4qKSQvZ20sIGZ1bmN0aW9uKF8sIHQpeyByZXR1cm4gIjxibG9ja3F1b3RlPiIgKyB0ICsgIjwvYmxvY2txdW90ZT4iOyB9KTsKICAvLyBocgogIHNyYyA9IHNyYy5yZXBsYWNlKC9eLS0tKyQvZ20sICI8aHIvPiIpOwogIC8vIGxpc3RzICh1bm9yZGVyZWQvb3JkZXJlZCkgLSBsaSDjgpLlv4XjgZogdWwvb2wg44Gn5Zuy44KACiAgc3JjID0gc3JjLnJlcGxhY2UoL15bIFx0XSpbLSorXVsgXHRdKyguKikkL2dtLCAiPGxpIGNsYXNzPVwidVwiPiQxPC9saT4iKTsKICBzcmMgPSBzcmMucmVwbGFjZSgvXlsgXHRdKlxkK1wuWyBcdF0rKC4qKSQvZ20sICI8bGkgY2xhc3M9XCJvXCI+JDE8L2xpPiIpOwogIHNyYyA9IHNyYy5yZXBsYWNlKC8oKD86PGxpIGNsYXNzPVwidVwiPi4qPzxcL2xpPlsgXHRdKlxuPykrKS9nLCAiPHVsPiQxPC91bD4iKTsKICBzcmMgPSBzcmMucmVwbGFjZSgvKCg/OjxsaSBjbGFzcz1cIm9cIj4uKj88XC9saT5bIFx0XSpcbj8pKykvZywgIjxvbD4kMTwvb2w+Iik7CiAgc3JjID0gc3JjLnJlcGxhY2UoLzxsaSBjbGFzcz1cInVcIj4vZywgIjxsaT4iKTsKICBzcmMgPSBzcmMucmVwbGFjZSgvPGxpIGNsYXNzPVwib1wiPi9nLCAiPGxpPiIpOwogIC8vIHRhYmxlcyAgKE1hcmtkb3duIOihqDogfCBhIHwgYiB8ICArIHNlcGFyYXRvcikKICBzcmMgPSBzcmMucmVwbGFjZSgvKCg/Ol5cfFteXG5dKlx8WyBcdF0qJFxuPykrKS9nbSwgZnVuY3Rpb24oYmxvY2spewogICAgdmFyIGxpbmVzID0gYmxvY2sudHJpbSgpLnNwbGl0KCJcbiIpLm1hcChmdW5jdGlvbihsKXsgcmV0dXJuIGwudHJpbSgpOyB9KQogICAgICAuZmlsdGVyKGZ1bmN0aW9uKGwpeyByZXR1cm4gL15cfC4qXHwkLy50ZXN0KGwpOyB9KTsKICAgIGlmIChsaW5lcy5sZW5ndGggPCAyKSByZXR1cm4gYmxvY2s7CiAgICB2YXIgY2VsbHMgPSBmdW5jdGlvbihsKXsgcmV0dXJuIGwucmVwbGFjZSgvXlx8fFx8JC9nLCIiKS5zcGxpdCgifCIpLm1hcChmdW5jdGlvbihjKXsgcmV0dXJuIGMudHJpbSgpOyB9KTsgfTsKICAgIHZhciBpc1NlcCA9IGZ1bmN0aW9uKGwpeyByZXR1cm4gL1tcc3w6LV0rLy50ZXN0KGwpICYmIGwuaW5kZXhPZigiLSIpID49IDAgJiYgIS9bXlxzfDotXS8udGVzdChsKTsgfTsKICAgIHZhciBoZWFkZXIgPSBjZWxscyhsaW5lc1swXSk7CiAgICB2YXIgc3RhcnQgPSAobGluZXMubGVuZ3RoID4gMSAmJiBpc1NlcChsaW5lc1sxXSkpID8gMSA6IDA7CiAgICB2YXIgdGhlYWQgPSAiPHRoZWFkPjx0cj4iICsgaGVhZGVyLm1hcChmdW5jdGlvbihjKXsgcmV0dXJuICI8dGg+IiArIGMgKyAiPC90aD4iOyB9KS5qb2luKCIiKSArICI8L3RyPjwvdGhlYWQ+IjsKICAgIHZhciB0Ym9keSA9ICI8dGJvZHk+IjsKICAgIGZvciAodmFyIHQgPSBzdGFydCArIDE7IHQgPCBsaW5lcy5sZW5ndGg7IHQrKyl7CiAgICAgIHZhciBjcyA9IGNlbGxzKGxpbmVzW3RdKTsKICAgICAgaWYgKGNzLmxlbmd0aCA+IDApIHRib2R5ICs9ICI8dHI+IiArIGNzLm1hcChmdW5jdGlvbihjKXsgcmV0dXJuICI8dGQ+IiArIGMgKyAiPC90ZD4iOyB9KS5qb2luKCIiKSArICI8L3RyPiI7CiAgICB9CiAgICB0Ym9keSArPSAiPC90Ym9keT4iOwogICAgcmV0dXJuICI8dGFibGU+IiArIHRoZWFkICsgdGJvZHkgKyAiPC90YWJsZT4iOwogIH0pOwogIC8vIG1lcm1haWQg44OX44Os44O844K544Ob44Or44OA44KS5a6f5L2TKGRpdinjgbjmiLvjgZnjgILooYzpoK3jgYwgJzwnIOOBquOBruOBp+auteiQveWMluOBp+WbsuOBvuOCjOOBquOBhAogIHNyYyA9IHNyYy5yZXBsYWNlKC9cdTAwMDBNKFxkKylcdTAwMDAvZywgZnVuY3Rpb24oXywgaSl7CiAgICByZXR1cm4gJzxkaXYgY2xhc3M9Im1lcm1haWQiIGRhdGEtYjY0PSInICsgYjY0ZShtZXJtYWlkU3JjW3BhcnNlSW50KGksMTApXSkgKyAnIj48L2Rpdj4nOwogIH0pOwogIC8vIHBhcmFncmFwaHMgKGdyb3VwIGNvbnNlY3V0aXZlIG5vbi1saXN0L3RhZyBsaW5lcykKICBzcmMgPSBzcmMucmVwbGFjZSgvXihbXjxdW15cbl0qKSQvZ20sIGZ1bmN0aW9uKG0peyByZXR1cm4gIjxwPiIrbSsiPC9wPiI7IH0pOwogIHJldHVybiBzcmMucmVwbGFjZSgvXG57Mix9L2csICJcbiIpLnRyaW0oKTsKfQoKLyogLS0tLS0tLS0tLSBNZXJtYWlkIOODrOODs+ODgOODquODs+OCsCAtLS0tLS0tLS0tICovCmZ1bmN0aW9uIGI2NGUocyl7IHRyeXsgcmV0dXJuIGJ0b2EodW5lc2NhcGUoZW5jb2RlVVJJQ29tcG9uZW50KHMpKSk7IH1jYXRjaChlKXsgcmV0dXJuIGJ0b2Eocyk7IH0gfQpmdW5jdGlvbiBkZWNvZGVCNjQocyl7IHRyeXsgcmV0dXJuIGRlY29kZVVSSUNvbXBvbmVudChlc2NhcGUoYXRvYihzKSkpOyB9Y2F0Y2goZSl7IHJldHVybiBhdG9iKHMpOyB9IH0KCnZhciBfX21lcm1haWRTdGF0ZSA9IHsgbG9hZGVkOmZhbHNlLCBsb2FkaW5nOmZhbHNlIH07CmZ1bmN0aW9uIHNob3dNZXJtYWlkRXJyb3IoZWwpewogIHZhciBzcmMgPSBkZWNvZGVCNjQoZWwuZ2V0QXR0cmlidXRlKCJkYXRhLWI2NCIpfHwiIik7CiAgZWwuaW5uZXJIVE1MID0gJzxwcmUgY2xhc3M9Im1lcm1haWQtZmFsbGJhY2siPicgKyBlc2NhcGVIdG1sKHNyYykgKyAnPC9wcmU+JyArCiAgICAgICAgICAgICAgICAgJzxkaXYgY2xhc3M9Im1lcm1haWQtbm90ZSI+JiM5ODg4OyBNZXJtYWlkIOihqOekuuOBq+WkseaVl+OBl+OBvuOBl+OBnyhDRE7jgavmjqXntprjgafjgY3jgb7jgZvjgpPjgafjgZfjgZ8p44CCPC9kaXY+JzsKICBlbC5jbGFzc0xpc3QuYWRkKCJkb25lIik7Cn0KZnVuY3Rpb24gbG9hZE1lcm1haWQoY2IpewogIGlmICh3aW5kb3cubWVybWFpZCkgeyBjYigpOyByZXR1cm47IH0KICBpZiAoX19tZXJtYWlkU3RhdGUubG9hZGluZykgeyBzZXRUaW1lb3V0KGZ1bmN0aW9uKCl7IGxvYWRNZXJtYWlkKGNiKTsgfSwgMjUwKTsgcmV0dXJuOyB9CiAgX19tZXJtYWlkU3RhdGUubG9hZGluZyA9IHRydWU7CiAgdmFyIHMgPSBkb2N1bWVudC5jcmVhdGVFbGVtZW50KCJzY3JpcHQiKTsKICBzLnNyYyA9ICJodHRwczovL2Nkbi5qc2RlbGl2ci5uZXQvbnBtL21lcm1haWRAMTAvZGlzdC9tZXJtYWlkLm1pbi5qcyI7CiAgcy5vbmxvYWQgPSBmdW5jdGlvbigpeyBfX21lcm1haWRTdGF0ZS5sb2FkZWQgPSB0cnVlOyBfX21lcm1haWRTdGF0ZS5sb2FkaW5nID0gZmFsc2U7IGNiKCk7IH07CiAgcy5vbmVycm9yID0gZnVuY3Rpb24oKXsKICAgIF9fbWVybWFpZFN0YXRlLmxvYWRpbmcgPSBmYWxzZTsKICAgIHZhciBlbHMgPSBkb2N1bWVudC5xdWVyeVNlbGVjdG9yQWxsKCIubWVybWFpZFtkYXRhLWI2NF06bm90KC5kb25lKSIpOwogICAgQXJyYXkucHJvdG90eXBlLmZvckVhY2guY2FsbChlbHMsIGZ1bmN0aW9uKGUpeyBzaG93TWVybWFpZEVycm9yKGUpOyB9KTsKICB9OwogIGRvY3VtZW50LmhlYWQuYXBwZW5kQ2hpbGQocyk7Cn0KZnVuY3Rpb24gcnVuTWVybWFpZCgpewogIHZhciBlbHMgPSBkb2N1bWVudC5xdWVyeVNlbGVjdG9yQWxsKCIubWVybWFpZFtkYXRhLWI2NF06bm90KC5kb25lKSIpOwogIGlmICghZWxzLmxlbmd0aCkgcmV0dXJuOwogIHZhciB0aGVtZSA9IGRvY3VtZW50LmRvY3VtZW50RWxlbWVudC5nZXRBdHRyaWJ1dGUoImRhdGEtdGhlbWUiKSA9PT0gImxpZ2h0IiA/ICJkZWZhdWx0IiA6ICJkYXJrIjsKICBBcnJheS5wcm90b3R5cGUuZm9yRWFjaC5jYWxsKGVscywgZnVuY3Rpb24oZWwpewogICAgZWwuY2xhc3NMaXN0LmFkZCgiZG9uZSIpOwogICAgZWwuaW5uZXJIVE1MID0gJzxkaXYgY2xhc3M9Im1lcm1haWQtbG9hZGluZyI+5Zuz44KS44Os44Oz44OA44Oq44Oz44Kw5Lit4oCmPC9kaXY+JzsKICB9KTsKICBsb2FkTWVybWFpZChmdW5jdGlvbigpewogICAgaWYgKHdpbmRvdy5tZXJtYWlkKSB7IHRyeSB7IG1lcm1haWQuaW5pdGlhbGl6ZSh7IHN0YXJ0T25Mb2FkOmZhbHNlLCB0aGVtZTogdGhlbWUgfSk7IH0gY2F0Y2goZSl7fSB9CiAgICBBcnJheS5wcm90b3R5cGUuZm9yRWFjaC5jYWxsKGVscywgZnVuY3Rpb24oZWwpewogICAgICB2YXIgc3JjVGV4dCA9IGRlY29kZUI2NChlbC5nZXRBdHRyaWJ1dGUoImRhdGEtYjY0Iil8fCIiKTsKICAgICAgKGZ1bmN0aW9uKGVsbSwgc3JjKXsKICAgICAgICB2YXIgaWQgPSAibW0iICsgTWF0aC5yYW5kb20oKS50b1N0cmluZygzNikuc2xpY2UoMiw4KTsKICAgICAgICB0cnkgewogICAgICAgICAgdmFyIHByID0gbWVybWFpZC5yZW5kZXIoaWQsIHNyYyk7CiAgICAgICAgICBpZiAocHIgJiYgcHIudGhlbikgcHIudGhlbihmdW5jdGlvbihyZXMpeyBlbG0uaW5uZXJIVE1MID0gcmVzLnN2ZzsgfSkuY2F0Y2goZnVuY3Rpb24oKXsgc2hvd01lcm1haWRFcnJvcihlbG0pOyB9KTsKICAgICAgICAgIGVsc2UgeyBlbG0uaW5uZXJIVE1MID0gcHI7IH0KICAgICAgICB9IGNhdGNoKGUpeyBzaG93TWVybWFpZEVycm9yKGVsbSk7IH0KICAgICAgfSkoZWwsIHNyY1RleHQpOwogICAgfSk7CiAgfSk7Cn0KCi8qIC0tLS0tLS0tLS0gRE9NIGhlbHBlcnMgLS0tLS0tLS0tLSAqLwp2YXIgY2hhdEVsID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoImNoYXQiKTsKdmFyIGlucHV0RWwgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgiaW5wdXQiKTsKdmFyIHNlbmRCdG4gPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgic2VuZEJ0biIpOwp2YXIgc2lkZVRvZ2dsZSA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCJzaWRlVG9nZ2xlIik7CnZhciB0b29sc0JhZGdlID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoInRvb2xzQmFkZ2UiKTsKdmFyIGFsbG93VG9vbHMgPSB0cnVlOwpmdW5jdGlvbiB1cGRhdGVUb29sc0JhZGdlKCkgewogIGlmICghdG9vbHNCYWRnZSkgcmV0dXJuOwogIHRvb2xzQmFkZ2UuaW5uZXJIVE1MID0gInRvb2xzIDxiPiIgKyAoYWxsb3dUb29scyA/ICJvbiIgOiAib2ZmIikgKyAiPC9iPiI7CiAgdG9vbHNCYWRnZS5zdHlsZS5jb2xvciA9IGFsbG93VG9vbHMgPyAiIiA6ICIjZjg3MTcxIjsKICB0b29sc0JhZGdlLnRpdGxlID0gIuODhOODvOODq+Wun+ihjOOCkiIgKyAoYWxsb3dUb29scyA/ICLnhKHlirnljJYiIDogIuacieWKueWMliIpOwp9CnZhciBjb25uRWwgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgiY29ubiIpOwp2YXIgbW9kZWxFbCA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCJtb2RlbExhYmVsIik7CgpmdW5jdGlvbiBlbXB0eVN0YXRlKHNob3cpIHsKICBpZiAoc2hvdykgewogICAgY2hhdEVsLmlubmVySFRNTCA9ICIiOyAvLyDliJ3mnJ/nirbmhYvjga/nqbrnmb0KICB9IGVsc2UgewogICAgY2hhdEVsLmlubmVySFRNTCA9ICIiOwogIH0KfQoKZnVuY3Rpb24gZm10Tm93KCkgewogIHZhciBkID0gbmV3IERhdGUoKTsKICBmdW5jdGlvbiBwKHgpeyByZXR1cm4gKHggPCAxMCA/ICIwIiA6ICIiKSArIHg7IH0KICByZXR1cm4gZC5nZXRGdWxsWWVhcigpICsgIi8iICsgcChkLmdldE1vbnRoKCkrMSkgKyAiLyIgKyBwKGQuZ2V0RGF0ZSgpKQogICAgICAgKyAiICIgKyBwKGQuZ2V0SG91cnMoKSkgKyAiOiIgKyBwKGQuZ2V0TWludXRlcygpKTsKfQoKZnVuY3Rpb24gYnViYmxlKHJvbGUsIHRleHQsIHRzKSB7CiAgdmFyIHJvdyA9IGRvY3VtZW50LmNyZWF0ZUVsZW1lbnQoImRpdiIpOwogIHJvdy5jbGFzc05hbWUgPSAibXNnICIgKyByb2xlOwogIHZhciBiID0gZG9jdW1lbnQuY3JlYXRlRWxlbWVudCgiZGl2Iik7CiAgYi5jbGFzc05hbWUgPSAiYnViYmxlIjsKICBiLmlubmVySFRNTCA9IHRleHQ7CiAgaWYgKHRzKSB7CiAgICB2YXIgdHNwID0gZG9jdW1lbnQuY3JlYXRlRWxlbWVudCgiZGl2Iik7CiAgICB0c3AuY2xhc3NOYW1lID0gInRzIjsKICAgIHRzcC50ZXh0Q29udGVudCA9IHRzOwogICAgYi5hcHBlbmRDaGlsZCh0c3ApOwogIH0KICByb3cuYXBwZW5kQ2hpbGQoYik7CiAgY2hhdEVsLmFwcGVuZENoaWxkKHJvdyk7CiAgcmV0dXJuIGI7Cn0KCmZ1bmN0aW9uIHVzZXJNc2codGV4dCwgdHMpIHsKICB2YXIgZWwgPSBidWJibGUoInVzZXIiLCBlc2NhcGVIdG1sKHRleHQpLCB0cyB8fCBmbXROb3coKSk7CiAgcmV0dXJuIGVsOwp9CmZ1bmN0aW9uIGFzc2lzdGFudE1zZyhtZCwgdHMpIHsKICB2YXIgZWwgPSBidWJibGUoImFzc2lzdGFudCIsICc8ZGl2IGNsYXNzPSJtZCI+JyArIHJlbmRlck1hcmtkb3duKG1kKSArICc8L2Rpdj4nLCB0cyB8fCBmbXROb3coKSk7CiAgcmV0dXJuIGVsOwp9CmZ1bmN0aW9uIGFkZFR5cGluZygpIHsKICB2YXIgcm93ID0gZG9jdW1lbnQuY3JlYXRlRWxlbWVudCgiZGl2Iik7CiAgcm93LmNsYXNzTmFtZSA9ICJtc2cgYXNzaXN0YW50IjsKICByb3cuaWQgPSAidHlwaW5nIjsKICByb3cuaW5uZXJIVE1MID0gJzxkaXYgY2xhc3M9ImJ1YmJsZSI+PGRpdiBjbGFzcz0idHlwaW5nIj48c3Bhbj48L3NwYW4+PHNwYW4+PC9zcGFuPjxzcGFuPjwvc3Bhbj48L2Rpdj48ZGl2IGNsYXNzPSJwcm9ncmVzcyIgaWQ9InByb2dyZXNzQm94Ij48L2Rpdj48L2Rpdj4nOwogIGNoYXRFbC5hcHBlbmRDaGlsZChyb3cpOwogIHNjcm9sbEJvdHRvbSgpOwogIHJldHVybiByb3c7Cn0KZnVuY3Rpb24gc2Nyb2xsQm90dG9tKCkgewogIHZhciBtID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoIm1haW4iKTsKICBtLnNjcm9sbFRvcCA9IG0uc2Nyb2xsSGVpZ2h0OwogIHJ1bk1lcm1haWQoKTsKfQpmdW5jdGlvbiByZXNpemVJbnB1dCgpIHsKICBpbnB1dEVsLnN0eWxlLmhlaWdodCA9ICJhdXRvIjsKICBpbnB1dEVsLnN0eWxlLmhlaWdodCA9IE1hdGgubWluKGlucHV0RWwuc2Nyb2xsSGVpZ2h0LCAxODApICsgInB4IjsKfQoKLyogLS0tLS0tLS0tLSBjaGF0IHN0YXRlIC0tLS0tLS0tLS0gKi8KdmFyIGJ1c3kgPSBmYWxzZTsKdmFyIGhhc01lc3NhZ2VzID0gZmFsc2U7CnZhciBzZXJ2ZXJCdXN5ID0gZmFsc2U7ICAgLy8g44K144O844OQ5YG044Gu5Yem55CG5Lit44OV44Op44KwICgvYXBpL2hlYWx0aCDjga4gYnVzeSkKdmFyIGJ1c3lUaW1lciA9IG51bGw7ICAgICAvLyDlh6bnkIbkuK3jgrnjg4bjg7zjgr/jgrnjga7jg53jg7zjg6rjg7PjgrDjgr/jgqTjg57jg7wKdmFyIHNlc3Npb25MaXN0RWwgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgic2Vzc2lvbkxpc3QiKTsKdmFyIG5ld0J0biA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCJuZXdCdG4iKTsKCi8vIOePvuWcqOOBruOCu+ODg+OCt+ODp+ODs0lEICjjg63jg7zjgqvjg6vjgavkv53mjIHjgIHjgrXjg7zjg5DnrqHnkIbjga7jgrvjg4Pjgrfjg6fjg7PjgajntJDku5jjgZEpCnZhciBzZXNzaW9uSWQgPSBsb2NhbFN0b3JhZ2UuZ2V0SXRlbSgiYWlhZ2VudC5zZXNzaW9uIikgfHwgIiI7CgpmdW5jdGlvbiBlbXB0eUNoYXQoKSB7IGNoYXRFbC5pbm5lckhUTUwgPSAiIjsgaGFzTWVzc2FnZXMgPSBmYWxzZTsgZW1wdHlTdGF0ZSh0cnVlKTsgfQoKZnVuY3Rpb24gcmVuZGVyU2Vzc2lvbkxpc3Qoc2Vzc2lvbnMsIHNlbGVjdGVkKSB7CiAgc2Vzc2lvbkxpc3RFbC5pbm5lckhUTUwgPSAiIjsKICBzZXNzaW9ucy5mb3JFYWNoKGZ1bmN0aW9uKHMpewogICAgdmFyIGNudCA9IChzLmNvdW50IHx8IDApOwogICAgdmFyIGl0ZW0gPSBkb2N1bWVudC5jcmVhdGVFbGVtZW50KCJkaXYiKTsKICAgIGl0ZW0uY2xhc3NOYW1lID0gInNlc3Npb24taXRlbSIgKyAocy5pZCA9PT0gc2VsZWN0ZWQgPyAiIGFjdGl2ZSIgOiAiIik7CiAgICBpdGVtLnRpdGxlID0gcy50aXRsZSB8fCAi5Lya6KmxIjsKICAgIGl0ZW0uYWRkRXZlbnRMaXN0ZW5lcigiY2xpY2siLCBmdW5jdGlvbigpeyBzZWxlY3RTZXNzaW9uKHMuaWQpOyB9KTsKCiAgICB2YXIgdHh0ID0gZG9jdW1lbnQuY3JlYXRlRWxlbWVudCgiZGl2Iik7CiAgICB0eHQuY2xhc3NOYW1lID0gInMtdHh0IjsKICAgIHR4dC50ZXh0Q29udGVudCA9IChzLnRpdGxlIHx8ICLkvJroqbEiKSArIChjbnQgPiAwID8gIiDCtyAiICsgY250IDogIiIpOwoKICAgIHZhciByZW4gPSBkb2N1bWVudC5jcmVhdGVFbGVtZW50KCJidXR0b24iKTsKICAgIHJlbi5jbGFzc05hbWUgPSAicy1yZW4iOwogICAgcmVuLnRpdGxlID0gIuWQjeWJjeOCkuWkieabtCI7CiAgICByZW4udGV4dENvbnRlbnQgPSAi4pyOIjsKICAgIHJlbi5hZGRFdmVudExpc3RlbmVyKCJjbGljayIsIGZ1bmN0aW9uKGV2KXsKICAgICAgZXYuc3RvcFByb3BhZ2F0aW9uKCk7CiAgICAgIHN0YXJ0UmVuYW1lKHMuaWQsIHMudGl0bGUgfHwgIiIsIGl0ZW0sIHR4dCk7CiAgICB9KTsKCiAgICB2YXIgZGVsID0gZG9jdW1lbnQuY3JlYXRlRWxlbWVudCgiYnV0dG9uIik7CiAgICBkZWwuY2xhc3NOYW1lID0gInMtZGVsIjsKICAgIGRlbC50aXRsZSA9ICLjgZPjga7jgrvjg4Pjgrfjg6fjg7PjgpLliYrpmaQiOwogICAgZGVsLnRleHRDb250ZW50ID0gIsOXIjsKICAgIGRlbC5hZGRFdmVudExpc3RlbmVyKCJjbGljayIsIGZ1bmN0aW9uKGV2KXsKICAgICAgZXYuc3RvcFByb3BhZ2F0aW9uKCk7CiAgICAgIGRlbGV0ZVNlc3Npb24ocy5pZCk7CiAgICB9KTsKCiAgICBpdGVtLmFwcGVuZENoaWxkKHR4dCk7CiAgICBpdGVtLmFwcGVuZENoaWxkKHJlbik7CiAgICBpdGVtLmFwcGVuZENoaWxkKGRlbCk7CiAgICBzZXNzaW9uTGlzdEVsLmFwcGVuZENoaWxkKGl0ZW0pOwogIH0pOwp9CgovLyDjgrXjgqTjg4njg5Djg7zlhoXjgafjgr/jgqTjg4jjg6vjgpLjgqTjg7Pjg6njgqTjg7Pnt6jpm4bjgZnjgosKZnVuY3Rpb24gc3RhcnRSZW5hbWUoaWQsIGN1cnJlbnRUaXRsZSwgaXRlbSwgdHh0KSB7CiAgdmFyIGlucHV0ID0gZG9jdW1lbnQuY3JlYXRlRWxlbWVudCgiaW5wdXQiKTsKICBpbnB1dC5jbGFzc05hbWUgPSAicy1lZGl0IjsKICBpbnB1dC52YWx1ZSA9IGN1cnJlbnRUaXRsZTsKICB0eHQucmVwbGFjZVdpdGgoaW5wdXQpOwogIGlucHV0LmZvY3VzKCk7CiAgaW5wdXQuc2VsZWN0KCk7CgogIHZhciBjb21taXQgPSBmdW5jdGlvbigpewogICAgdmFyIHYgPSBpbnB1dC52YWx1ZS50cmltKCk7CiAgICBpZiAodi5sZW5ndGggPT09IDApIHYgPSAi5paw6KaP5Lya6KmxIjsKICAgIGZldGNoKCIvYXBpL3Nlc3Npb25zLyIgKyBlbmNvZGVVUklDb21wb25lbnQoaWQpICsgIi9yZW5hbWUiLCB7CiAgICAgIG1ldGhvZDogIlBPU1QiLAogICAgICBoZWFkZXJzOiB7ICJDb250ZW50LVR5cGUiOiAiYXBwbGljYXRpb24vanNvbiIgfSwKICAgICAgYm9keTogSlNPTi5zdHJpbmdpZnkoeyB0aXRsZTogdiB9KQogICAgfSkuY2F0Y2goZnVuY3Rpb24oKXt9KS50aGVuKGZ1bmN0aW9uKCl7IHJlZnJlc2hTZXNzaW9ucygpOyB9KTsKICB9OwogIGlucHV0LmFkZEV2ZW50TGlzdGVuZXIoImtleWRvd24iLCBmdW5jdGlvbihlKXsKICAgIGlmIChlLmtleSA9PT0gIkVudGVyIikgeyBlLnByZXZlbnREZWZhdWx0KCk7IGNvbW1pdCgpOyB9CiAgICBlbHNlIGlmIChlLmtleSA9PT0gIkVzY2FwZSIpIHsgcmVmcmVzaFNlc3Npb25zKCk7IH0KICB9KTsKICBpbnB1dC5hZGRFdmVudExpc3RlbmVyKCJibHVyIiwgY29tbWl0KTsKfQoKZnVuY3Rpb24gZGVsZXRlU2Vzc2lvbihpZCkgewogIGZldGNoKCIvYXBpL3Nlc3Npb25zLyIgKyBlbmNvZGVVUklDb21wb25lbnQoaWQpLCB7IG1ldGhvZDogIkRFTEVURSIgfSkKICAgIC5jYXRjaChmdW5jdGlvbigpe30pCiAgICAudGhlbihmdW5jdGlvbigpeyByZWZyZXNoU2Vzc2lvbnMoKTsgfSk7Cn0KCi8vIOWPluW+l+a4iOOBv+ODoeODg+OCu+ODvOOCuCBbe3JvbGUsY29udGVudH0sLi4uXSDjgpLooajnpLrjgavlj43mmKAKZnVuY3Rpb24gbG9hZFNlc3Npb24obWVzc2FnZXMpIHsKICBoYXNNZXNzYWdlcyA9IGZhbHNlOwogIGNoYXRFbC5pbm5lckhUTUwgPSAiIjsKICBpZiAoIW1lc3NhZ2VzIHx8IG1lc3NhZ2VzLmxlbmd0aCA9PT0gMCkgeyBlbXB0eVN0YXRlKHRydWUpOyByZXR1cm47IH0KICBlbXB0eVN0YXRlKGZhbHNlKTsgaGFzTWVzc2FnZXMgPSB0cnVlOwogIG1lc3NhZ2VzLmZvckVhY2goZnVuY3Rpb24obSl7CiAgICBpZiAobS5yb2xlID09PSAidXNlciIpIHVzZXJNc2cobS5jb250ZW50LCBtLnRzKTsKICAgIGVsc2UgaWYgKG0ucm9sZSA9PT0gImFzc2lzdGFudCIpIGFzc2lzdGFudE1zZyhtLmNvbnRlbnQsIG0udHMpOwogIH0pOwogIGlmIChzZXJ2ZXJCdXN5KSBlbnN1cmVUeXBpbmcoKTsKICBzY3JvbGxCb3R0b20oKTsKfQoKZnVuY3Rpb24gZmV0Y2hTZXNzaW9uKGlkKSB7CiAgZmV0Y2goIi9hcGkvc2Vzc2lvbnMvIiArIGVuY29kZVVSSUNvbXBvbmVudChpZCkpCiAgICAudGhlbihmdW5jdGlvbihyKXsgcmV0dXJuIHIuanNvbigpLmNhdGNoKGZ1bmN0aW9uKCl7IHJldHVybiBudWxsOyB9KTsgfSkKICAgIC50aGVuKGZ1bmN0aW9uKGQpewogICAgICBpZiAoZCAmJiBkLm9rKSBsb2FkU2Vzc2lvbihkLm1lc3NhZ2VzIHx8IFtdKTsKICAgICAgZWxzZSBlbXB0eUNoYXQoKTsKICAgIH0pCiAgICAuY2F0Y2goZnVuY3Rpb24oKXsgZW1wdHlDaGF0KCk7IH0pOwp9CgpmdW5jdGlvbiBzZWxlY3RTZXNzaW9uKGlkKSB7CiAgaWYgKCFpZCB8fCBidXN5KSByZXR1cm47CiAgc2Vzc2lvbklkID0gaWQ7CiAgbG9jYWxTdG9yYWdlLnNldEl0ZW0oImFpYWdlbnQuc2Vzc2lvbiIsIGlkKTsKICBmZXRjaFNlc3Npb24oaWQpOwp9CgpmdW5jdGlvbiByZWZyZXNoU2Vzc2lvbnMoKSB7CiAgZmV0Y2goIi9hcGkvc2Vzc2lvbnMiKQogICAgLnRoZW4oZnVuY3Rpb24ocil7IHJldHVybiByLmpzb24oKS5jYXRjaChmdW5jdGlvbigpeyByZXR1cm4gbnVsbDsgfSk7IH0pCiAgICAudGhlbihmdW5jdGlvbihkKXsKICAgICAgaWYgKCFkIHx8ICFkLm9rKSB7IGVtcHR5Q2hhdCgpOyByZXR1cm47IH0KICAgICAgdmFyIHNlc3Npb25zID0gZC5zZXNzaW9ucyB8fCBbXTsKICAgICAgdmFyIGV4aXN0cyA9IHNlc3Npb25zLnNvbWUoZnVuY3Rpb24ocyl7IHJldHVybiBzLmlkID09PSBzZXNzaW9uSWQ7IH0pOwogICAgICB2YXIgdGFyZ2V0ID0gc2Vzc2lvbklkOwogICAgICBpZiAoIWV4aXN0cykgewogICAgICAgIGlmIChzZXNzaW9ucy5sZW5ndGggPiAwKSB0YXJnZXQgPSBzZXNzaW9uc1swXS5pZDsgLy8g5pyA6L+R44Gu44K744OD44K344On44Oz44G4CiAgICAgICAgZWxzZSB7IGNyZWF0ZVNlc3Npb24odHJ1ZSk7IHJldHVybjsgfSAgICAgICAgICAgICAgLy8gMeOBpOOCgueEoeOBkeOCjOOBsOS9nOaIkAogICAgICB9CiAgICAgIHJlbmRlclNlc3Npb25MaXN0KHNlc3Npb25zLCB0YXJnZXQpOwogICAgICBzZXNzaW9uSWQgPSB0YXJnZXQ7CiAgICAgIGxvY2FsU3RvcmFnZS5zZXRJdGVtKCJhaWFnZW50LnNlc3Npb24iLCB0YXJnZXQpOwogICAgICBmZXRjaFNlc3Npb24odGFyZ2V0KTsKICAgIH0pCiAgICAuY2F0Y2goZnVuY3Rpb24oKXsgZW1wdHlDaGF0KCk7IH0pOwp9CgpmdW5jdGlvbiBjcmVhdGVTZXNzaW9uKHNpbGVudCkgewogIGZldGNoKCIvYXBpL3Nlc3Npb25zIiwgeyBtZXRob2Q6ICJQT1NUIiB9KQogICAgLnRoZW4oZnVuY3Rpb24ocil7IHJldHVybiByLmpzb24oKS5jYXRjaChmdW5jdGlvbigpeyByZXR1cm4gbnVsbDsgfSk7IH0pCiAgICAudGhlbihmdW5jdGlvbihkKXsKICAgICAgaWYgKGQgJiYgZC5vaykgewogICAgICAgIHNlc3Npb25JZCA9IGQuc2Vzc2lvbjsKICAgICAgICBsb2NhbFN0b3JhZ2Uuc2V0SXRlbSgiYWlhZ2VudC5zZXNzaW9uIiwgc2Vzc2lvbklkKTsKICAgICAgICBlbXB0eUNoYXQoKTsKICAgICAgICByZWZyZXNoU2Vzc2lvbnMoKTsKICAgICAgfQogICAgfSk7Cn0KCnZhciBzZW5kQnRuSFRNTCA9IHNlbmRCdG4uaW5uZXJIVE1MOyAvLyDlhYPjga7pgIHkv6HjgqLjgqTjgrPjg7MKZnVuY3Rpb24gc2V0U2VuZEFzU3RvcChiKSB7CiAgaWYgKGIpIHsKICAgIHNlbmRCdG4uaW5uZXJIVE1MID0gIuKWoCI7CiAgICBzZW5kQnRuLnN0eWxlLmZvbnRTaXplID0gIjE4cHgiOwogICAgc2VuZEJ0bi50aXRsZSA9ICLlh6bnkIbjgpLkuK3mraIiOwogIH0gZWxzZSB7CiAgICBzZW5kQnRuLmlubmVySFRNTCA9IHNlbmRCdG5IVE1MOwogICAgc2VuZEJ0bi5zdHlsZS5mb250U2l6ZSA9ICIiOwogICAgc2VuZEJ0bi50aXRsZSA9ICLpgIHkv6EiOwogIH0KfQpmdW5jdGlvbiBzZXRCdXN5KGIpIHsKICBidXN5ID0gYjsKICBzZW5kQnRuLmRpc2FibGVkID0gZmFsc2U7ICAgLy8gYnVzeeS4reOBr+mAgeS/oeODnOOCv+ODs+OCkuOAjOWBnOatouOAjeOBqOOBl+OBpuS9v+OBhgogIHNlbmRCdG4uc3R5bGUub3BhY2l0eSA9ICIxIjsKICBzZXRTZW5kQXNTdG9wKGIpOwp9CgpmdW5jdGlvbiBoaWRlVHlwaW5nKCkgewogIHZhciB0ID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoInR5cGluZyIpOwogIGlmICh0KSB0LnJlbW92ZSgpOwp9CmZ1bmN0aW9uIGVuc3VyZVR5cGluZygpIHsKICBpZiAoc2VydmVyQnVzeSAmJiAhZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoInR5cGluZyIpKSBhZGRUeXBpbmcoKTsKfQpmdW5jdGlvbiByZW5kZXJQcm9ncmVzcyhzdGVwcykgewogIHZhciBib3ggPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgicHJvZ3Jlc3NCb3giKTsKICBpZiAoIWJveCkgcmV0dXJuOwogIHZhciBhcnIgPSBzdGVwcyB8fCBbXTsKICB2YXIgaHRtbCA9ICIiOwogIGZvciAodmFyIGkgPSAwOyBpIDwgYXJyLmxlbmd0aDsgaSsrKQogICAgaHRtbCArPSAnPGRpdiBjbGFzcz0icHN0ZXAiPicgKyBlc2NhcGVIdG1sKFN0cmluZyhhcnJbaV0pKSArICc8L2Rpdj4nOwogIGJveC5pbm5lckhUTUwgPSBodG1sOwp9CmZ1bmN0aW9uIHN0YXJ0QnVzeVBvbGwoKSB7CiAgaWYgKCFidXN5VGltZXIpIGJ1c3lUaW1lciA9IHNldEludGVydmFsKHBvbGxTZXJ2ZXJCdXN5LCAyNTAwKTsKfQpmdW5jdGlvbiBzdG9wQnVzeVBvbGwoKSB7CiAgaWYgKGJ1c3lUaW1lcikgeyBjbGVhckludGVydmFsKGJ1c3lUaW1lcik7IGJ1c3lUaW1lciA9IG51bGw7IH0KfQoKLy8g44K144O844OQ44Gu5Yem55CG5Lit44K544OG44O844K/44K544KS44Od44O844Oq44Oz44Kw44GX44CB5a6M5LqG44GX44Gf44KJ5Y+N5pig44GZ44KLCmZ1bmN0aW9uIHBvbGxTZXJ2ZXJCdXN5KCkgewogIGZldGNoKCIvYXBpL2hlYWx0aCIpLnRoZW4oZnVuY3Rpb24ocil7IHJldHVybiByLmpzb24oKS5jYXRjaChmdW5jdGlvbigpeyByZXR1cm4gbnVsbDsgfSk7IH0pCiAgICAudGhlbihmdW5jdGlvbihkKXsKICAgICAgdmFyIHdhcyA9IHNlcnZlckJ1c3k7CiAgICAgIHNlcnZlckJ1c3kgPSAhIShkICYmIGQuYnVzeSk7CiAgICAgIGlmIChzZXJ2ZXJCdXN5KSB7CiAgICAgICAgc2V0QnVzeSh0cnVlKTsKICAgICAgICBlbnN1cmVUeXBpbmcoKTsKICAgICAgICByZW5kZXJQcm9ncmVzcyhkICYmIGQucHJvZ3Jlc3MpOwogICAgICAgIHN0YXJ0QnVzeVBvbGwoKTsKICAgICAgICBjb25uRWwudGV4dENvbnRlbnQgPSAi5b+c562U5Lit4oCmIjsKICAgICAgICBjb25uRWwuc3R5bGUuY29sb3IgPSAiI2ZiYmYyNCI7CiAgICAgIH0gZWxzZSB7CiAgICAgICAgc3RvcEJ1c3lQb2xsKCk7CiAgICAgICAgaWYgKHdhcykgewogICAgICAgICAgc2V0QnVzeShmYWxzZSk7CiAgICAgICAgICBoaWRlVHlwaW5nKCk7CiAgICAgICAgICBjb25uRWwudGV4dENvbnRlbnQgPSAiY29ubmVjdGVkIjsKICAgICAgICAgIGNvbm5FbC5zdHlsZS5jb2xvciA9ICIjNGFkZTgwIjsKICAgICAgICAgIHJlZnJlc2hTZXNzaW9ucygpOyAvLyDlrozkuobliIbjgpLoqq3jgb/ovrzjgb8KICAgICAgICB9CiAgICAgIH0KICAgIH0pCiAgICAuY2F0Y2goZnVuY3Rpb24oKXt9KTsgLy8g44Ko44Op44O85pmC44KC44Od44O844Oq44Oz44Kw44KS57aZ57aaCn0KCi8vIOi1t+WLleaZgjog44K144O844OQ54q25oWLKOODouODh+ODq+ODu+ODhOODvOODq+ODu0FQSeOCreODvOODu2J1c3kp44KS5Y+W5b6X44GX44Gm44GL44KJ6KGo56S6CmZ1bmN0aW9uIGluaXRTZXJ2ZXJTdGF0ZSgpIHsKICBmZXRjaCgiL2FwaS9oZWFsdGgiKS50aGVuKGZ1bmN0aW9uKHIpeyByZXR1cm4gci5qc29uKCkuY2F0Y2goZnVuY3Rpb24oKXsgcmV0dXJuIG51bGw7IH0pOyB9KQogICAgLnRoZW4oZnVuY3Rpb24oZCl7CiAgICAgIGlmIChkICYmIGQub2spIHsKICAgICAgICBtb2RlbEVsLnRleHRDb250ZW50ID0gZC5tb2RlbCB8fCAi4oCUIjsKICAgICAgICBhbGxvd1Rvb2xzID0gIShkLmFsbG93VG9vbHMgPT09IGZhbHNlKTsKICAgICAgICB1cGRhdGVUb29sc0JhZGdlKCk7CiAgICAgICAgc2VydmVyQnVzeSA9ICEhKGQuYnVzeSk7CiAgICAgICAgaWYgKHNlcnZlckJ1c3kpIHsKICAgICAgICAgIHNldEJ1c3kodHJ1ZSk7CiAgICAgICAgICBpZiAoIWRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCJ0eXBpbmciKSkgYWRkVHlwaW5nKCk7CiAgICAgICAgICByZW5kZXJQcm9ncmVzcyhkICYmIGQucHJvZ3Jlc3MpOwogICAgICAgICAgc3RhcnRCdXN5UG9sbCgpOwogICAgICAgICAgY29ubkVsLnRleHRDb250ZW50ID0gIuW/nOetlOS4reKApiI7CiAgICAgICAgICBjb25uRWwuc3R5bGUuY29sb3IgPSAiI2ZiYmYyNCI7CiAgICAgICAgfSBlbHNlIGlmIChkLmFwaUtleSA9PT0gIm5vdC1zZXQiKSB7CiAgICAgICAgICBjb25uRWwudGV4dENvbnRlbnQgPSAiTExNX0FQSV9LRVkg5pyq6Kit5a6aIjsKICAgICAgICAgIGNvbm5FbC5zdHlsZS5jb2xvciA9ICIjZmJiZjI0IjsKICAgICAgICB9IGVsc2UgewogICAgICAgICAgY29ubkVsLnRleHRDb250ZW50ID0gImNvbm5lY3RlZCI7CiAgICAgICAgICBjb25uRWwuc3R5bGUuY29sb3IgPSAiIzRhZGU4MCI7CiAgICAgICAgfQogICAgICB9IGVsc2UgewogICAgICAgIGNvbm5FbC50ZXh0Q29udGVudCA9ICJjb25uZWN0ZWQiOwogICAgICAgIGNvbm5FbC5zdHlsZS5jb2xvciA9ICIjNGFkZTgwIjsKICAgICAgfQogICAgICByZWZyZXNoU2Vzc2lvbnMoKTsgLy8g44K144O844OQ54q25oWL56K65a6a5b6M44Gr44K744OD44K344On44Oz44KS6Kqt44G/6L6844G/CiAgICB9KQogICAgLmNhdGNoKGZ1bmN0aW9uKCl7IGNvbm5FbC50ZXh0Q29udGVudCA9ICJjb25uZWN0ZWQiOyBjb25uRWwuc3R5bGUuY29sb3IgPSAiIzRhZGU4MCI7IHJlZnJlc2hTZXNzaW9ucygpOyB9KTsKfQoKZnVuY3Rpb24gc2VuZCgpIHsKICB2YXIgdGV4dCA9IGlucHV0RWwudmFsdWUudHJpbSgpOwogIGlmICghdGV4dCB8fCBidXN5KSByZXR1cm47CiAgaWYgKCFoYXNNZXNzYWdlcykgeyBlbXB0eVN0YXRlKGZhbHNlKTsgaGFzTWVzc2FnZXMgPSB0cnVlOyB9CiAgdXNlck1zZyh0ZXh0KTsKICBpbnB1dEVsLnZhbHVlID0gIiI7IHJlc2l6ZUlucHV0KCk7CiAgc2Nyb2xsQm90dG9tKCk7CiAgc2V0QnVzeSh0cnVlKTsKICBjb25uRWwudGV4dENvbnRlbnQgPSAi5b+c562U5Lit4oCmIjsgY29ubkVsLnN0eWxlLmNvbG9yID0gIiNmYmJmMjQiOwoKICB2YXIgYnViYmxlRWwgPSBhc3Npc3RhbnRNc2coIiIpOyAgICAgICAgICAgICAgICAvLyDnqbrjga4gYXNzaXN0YW50IOWQueOBjeWHuuOBlwogIHZhciBtZEVsID0gYnViYmxlRWwucXVlcnlTZWxlY3RvcigiLm1kIik7CiAgdmFyIHByb2cgPSBkb2N1bWVudC5jcmVhdGVFbGVtZW50KCJkaXYiKTsgICAgICAgLy8g44OE44O844Or6YCy5o2XCiAgcHJvZy5jbGFzc05hbWUgPSAicHJvZ3Jlc3MiOwogIHByb2cuc3R5bGUuZGlzcGxheSA9ICJub25lIjsKICBidWJibGVFbC5hcHBlbmRDaGlsZChwcm9nKTsKCiAgdmFyIGZ1bGxUZXh0ID0gIiI7CiAgdmFyIGxhc3RQYWludCA9IDA7CiAgZnVuY3Rpb24gcGFpbnQoZm9yY2UpIHsKICAgIHZhciBub3cgPSBEYXRlLm5vdygpOwogICAgaWYgKCFmb3JjZSAmJiBub3cgLSBsYXN0UGFpbnQgPCA0MCkgcmV0dXJuOwogICAgbGFzdFBhaW50ID0gbm93OwogICAgbWRFbC5pbm5lckhUTUwgPSByZW5kZXJNYXJrZG93bihmdWxsVGV4dCk7CiAgICBzY3JvbGxCb3R0b20oKTsKICB9CgogIGZldGNoKCIvYXBpL2NoYXQvc3RyZWFtIiwgewogICAgbWV0aG9kOiAiUE9TVCIsCiAgICBoZWFkZXJzOiB7ICJDb250ZW50LVR5cGUiOiAiYXBwbGljYXRpb24vanNvbiIgfSwKICAgIGJvZHk6IEpTT04uc3RyaW5naWZ5KHsgbWVzc2FnZTogdGV4dCwgc2Vzc2lvbjogc2Vzc2lvbklkIH0pCiAgfSkudGhlbihmdW5jdGlvbihyZXNwKXsKICAgIGlmICghcmVzcC5vayB8fCAhcmVzcC5ib2R5KSB0aHJvdyBuZXcgRXJyb3IoIkhUVFAgIiArIHJlc3Auc3RhdHVzKTsKICAgIHZhciByZWFkZXIgPSByZXNwLmJvZHkuZ2V0UmVhZGVyKCk7CiAgICB2YXIgZGVjb2RlciA9IG5ldyBUZXh0RGVjb2RlcigpOwogICAgdmFyIGJ1ZiA9ICIiOwoKICAgIGZ1bmN0aW9uIGhhbmRsZShldiwgZGF0KSB7CiAgICAgIHZhciBkID0gbnVsbDsgdHJ5IHsgZCA9IEpTT04ucGFyc2UoZGF0KTsgfSBjYXRjaChlKXt9CiAgICAgIGlmIChldiA9PT0gImNvbnRlbnQiICYmIGQpIHsgZnVsbFRleHQgKz0gKGQudCB8fCAiIik7IHBhaW50KGZhbHNlKTsgfQogICAgICBlbHNlIGlmIChldiA9PT0gInRvb2wiICYmIGQpIHsKICAgICAgICBwcm9nLnN0eWxlLmRpc3BsYXkgPSAiYmxvY2siOwogICAgICAgIHByb2cuaW5uZXJIVE1MID0gJzxkaXYgY2xhc3M9InBzdGVwIj7ilrYg44OE44O844Or5a6f6KGMOiAnICsgZXNjYXBlSHRtbChTdHJpbmcoZC5uYW1lfHwiIikpICsgJzwvZGl2Pic7CiAgICAgICAgc2Nyb2xsQm90dG9tKCk7CiAgICAgIH0KICAgICAgZWxzZSBpZiAoZXYgPT09ICJlcnJvciIgJiYgZCkgewogICAgICAgIGJ1YmJsZUVsLmlubmVySFRNTCA9ICc8ZGl2IGNsYXNzPSJtZCI+PGRpdiBjbGFzcz0iZXJyIj7imqAgJyArIGVzY2FwZUh0bWwoU3RyaW5nKGQubWVzc2FnZXx8IuOCqOODqeODvCIpKSArICc8L2Rpdj48L2Rpdj4nOwogICAgICB9CiAgICAgIGVsc2UgaWYgKGV2ID09PSAiZG9uZSIgJiYgZCkgewogICAgICAgIGlmIChkLnNlc3Npb24pIHsgc2Vzc2lvbklkID0gZC5zZXNzaW9uOyBsb2NhbFN0b3JhZ2Uuc2V0SXRlbSgiYWlhZ2VudC5zZXNzaW9uIiwgc2Vzc2lvbklkKTsgfQogICAgICAgIHJlZnJlc2hTZXNzaW9ucygpOwogICAgICB9CiAgICB9CgogICAgZnVuY3Rpb24gcHVtcCgpIHsKICAgICAgcmV0dXJuIHJlYWRlci5yZWFkKCkudGhlbihmdW5jdGlvbihyZXMpewogICAgICAgIGlmIChyZXMuZG9uZSkgeyBwYWludCh0cnVlKTsgcmV0dXJuOyB9CiAgICAgICAgYnVmICs9IGRlY29kZXIuZGVjb2RlKHJlcy52YWx1ZSwge3N0cmVhbTp0cnVlfSk7CiAgICAgICAgdmFyIGlkeDsKICAgICAgICB3aGlsZSAoKGlkeCA9IGJ1Zi5pbmRleE9mKCJcblxuIikpID49IDApIHsKICAgICAgICAgIHZhciBibG9jayA9IGJ1Zi5zbGljZSgwLCBpZHgpOyBidWYgPSBidWYuc2xpY2UoaWR4ICsgMik7CiAgICAgICAgICB2YXIgZXYgPSBudWxsLCBkYXQgPSBudWxsOwogICAgICAgICAgYmxvY2suc3BsaXQoIlxuIikuZm9yRWFjaChmdW5jdGlvbihsKXsKICAgICAgICAgICAgaWYgKGwuaW5kZXhPZigiZXZlbnQ6IikgPT09IDApIGV2ID0gbC5zbGljZSg2KS50cmltKCk7CiAgICAgICAgICAgIGVsc2UgaWYgKGwuaW5kZXhPZigiZGF0YToiKSA9PT0gMCkgZGF0ID0gbC5zbGljZSg1KS50cmltKCk7CiAgICAgICAgICB9KTsKICAgICAgICAgIGlmIChkYXQgIT09IG51bGwgJiYgIShldiA9PT0gImVuZCIpKSBoYW5kbGUoZXYsIGRhdCk7CiAgICAgICAgfQogICAgICAgIHJldHVybiBwdW1wKCk7CiAgICAgIH0pOwogICAgfQogICAgcmV0dXJuIHB1bXAoKTsKICB9KS50aGVuKGZ1bmN0aW9uKCl7IHBhaW50KHRydWUpOyB9KQogICAgLmNhdGNoKGZ1bmN0aW9uKGVycil7CiAgICAgIGJ1YmJsZUVsLmlubmVySFRNTCA9ICc8ZGl2IGNsYXNzPSJtZCI+PGRpdiBjbGFzcz0iZXJyIj7imqAg6YCa5L+h44Ko44Op44O8OiAnICsgZXNjYXBlSHRtbChTdHJpbmcoZXJyKSkgKyAnPC9kaXY+PC9kaXY+JzsKICAgICAgcmVmcmVzaFNlc3Npb25zKCk7CiAgICAgIGNvbm5FbC50ZXh0Q29udGVudCA9ICJkaXNjb25uZWN0ZWQiOyBjb25uRWwuc3R5bGUuY29sb3IgPSAiI2Y4NzE3MSI7CiAgICB9KQogICAgLnRoZW4oZnVuY3Rpb24oKXsgc3RvcEJ1c3lQb2xsKCk7IHNlcnZlckJ1c3kgPSBmYWxzZTsgc2V0QnVzeShmYWxzZSk7IHJlZnJlc2hTZXNzaW9ucygpOyBjb25uRWwudGV4dENvbnRlbnQ9ImNvbm5lY3RlZCI7IGNvbm5FbC5zdHlsZS5jb2xvcj0iIzRhZGU4MCI7IH0pOwp9CgovKiAtLS0tLS0tLS0tIGV2ZW50cyAtLS0tLS0tLS0tICovCnNlbmRCdG4uYWRkRXZlbnRMaXN0ZW5lcigiY2xpY2siLCBmdW5jdGlvbigpewogIGlmIChidXN5KSB7IGZldGNoKCIvYXBpL2NhbmNlbCIsIHsgbWV0aG9kOiAiUE9TVCIgfSkuY2F0Y2goZnVuY3Rpb24oKXt9KTsgfQogIGVsc2UgeyBzZW5kKCk7IH0KfSk7CmlucHV0RWwuYWRkRXZlbnRMaXN0ZW5lcigia2V5ZG93biIsIGZ1bmN0aW9uKGUpewogIGlmIChlLmtleSA9PT0gIkVudGVyIiAmJiAhZS5zaGlmdEtleSkgeyBlLnByZXZlbnREZWZhdWx0KCk7IHNlbmQoKTsgfQp9KTsKaW5wdXRFbC5hZGRFdmVudExpc3RlbmVyKCJpbnB1dCIsIHJlc2l6ZUlucHV0KTsKaW5wdXRFbC5hZGRFdmVudExpc3RlbmVyKCJmb2N1cyIsIGZ1bmN0aW9uKCl7IGlmICghaGFzTWVzc2FnZXMpIGNvbm5FbC50ZXh0Q29udGVudCA9ICJjb25uZWN0ZWQiOyB9KTsKbmV3QnRuLmFkZEV2ZW50TGlzdGVuZXIoImNsaWNrIiwgZnVuY3Rpb24oKXsgY3JlYXRlU2Vzc2lvbihmYWxzZSk7IH0pOwoKLyogLS0tLS0tLS0tLSB0b29scyBvbi9vZmYg5YiH5pu/IC0tLS0tLS0tLS0gKi8KdG9vbHNCYWRnZS5hZGRFdmVudExpc3RlbmVyKCJjbGljayIsIGZ1bmN0aW9uKCl7CiAgZmV0Y2goIi9hcGkvdG9vbHMiLCB7CiAgICBtZXRob2Q6ICJQT1NUIiwKICAgIGhlYWRlcnM6IHsgIkNvbnRlbnQtVHlwZSI6ICJhcHBsaWNhdGlvbi9qc29uIiB9LAogICAgYm9keTogSlNPTi5zdHJpbmdpZnkoeyBhbGxvdzogIWFsbG93VG9vbHMgfSkKICB9KS50aGVuKGZ1bmN0aW9uKHIpeyByZXR1cm4gci5qc29uKCkuY2F0Y2goZnVuY3Rpb24oKXsgcmV0dXJuIG51bGw7IH0pOyB9KQogICAgLnRoZW4oZnVuY3Rpb24oZCl7IGlmIChkICYmIGQub2spIHsgYWxsb3dUb29scyA9ICEhZC5hbGxvd1Rvb2xzOyB1cGRhdGVUb29sc0JhZGdlKCk7IH0gfSk7Cn0pOwoKLyogLS0tLS0tLS0tLSDjgrXjgqTjg4njg5Djg7zooajnpLov6Z2e6KGo56S6IC0tLS0tLS0tLS0gKi8KZnVuY3Rpb24gYXBwbHlTaWRlYmFyKHNob3cpIHsKICB2YXIgc2IgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgic2lkZWJhciIpOwogIHZhciBmYWIgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgic2lkZVRvZ2dsZSIpOwogIGlmIChzYikgc2IuY2xhc3NMaXN0LnRvZ2dsZSgiaGlkZGVuIiwgIXNob3cpOwogIGlmIChmYWIpIGZhYi5zdHlsZS5kaXNwbGF5ID0gc2hvdyA/ICJub25lIiA6ICJibG9jayI7IC8vIOmdnuihqOekuuaZguOBruOBv+ODj+ODs+ODkOODvOOCrOODvOOCkuihqOekugogIHRyeXsgbG9jYWxTdG9yYWdlLnNldEl0ZW0oImFpYWdlbnQuc2lkZWJhciIsIHNob3cgPyAiMSIgOiAiMCIpOyB9Y2F0Y2goZSl7fQp9CmZ1bmN0aW9uIGluaXRTaWRlYmFyKCkgewogIHZhciB2ID0gIjEiOwogIHRyeXsgdiA9IGxvY2FsU3RvcmFnZS5nZXRJdGVtKCJhaWFnZW50LnNpZGViYXIiKSB8fCAiMSI7IH1jYXRjaChlKXt9CiAgYXBwbHlTaWRlYmFyKHYgIT09ICIwIik7Cn0Kc2lkZVRvZ2dsZS5hZGRFdmVudExpc3RlbmVyKCJjbGljayIsIGZ1bmN0aW9uKCl7IGFwcGx5U2lkZWJhcih0cnVlKTsgfSk7ICAgLy8g44OP44Oz44OQ44O844Ks44O84oaS6KGo56S6CmRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCJzaWRlQ2xvc2UiKS5hZGRFdmVudExpc3RlbmVyKCJjbGljayIsIGZ1bmN0aW9uKCl7IGFwcGx5U2lkZWJhcihmYWxzZSk7IH0pOyAvLyDDl+KGkumdnuihqOekugoKLyogLS0tLS0tLS0tLSDjg4bjg7zjg57liIfmm78gLS0tLS0tLS0tLSAqLwooZnVuY3Rpb24oKXsKICB2YXIgYnRuID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoInRoZW1lQnRuIik7CiAgZnVuY3Rpb24gYXBwbHlJY29uKCl7CiAgICB2YXIgdCA9IGRvY3VtZW50LmRvY3VtZW50RWxlbWVudC5nZXRBdHRyaWJ1dGUoImRhdGEtdGhlbWUiKTsKICAgIHZhciBpc0xpZ2h0ID0gKHQgPT09ICJsaWdodCIpOwogICAgYnRuLnRleHRDb250ZW50ID0gImNvbG9yc2NoZW1lICIgKyAoaXNMaWdodCA/ICJsaWdodCIgOiAiZGFyayIpOyAvLyDnj77lnKjjga7nirbmhYsKICAgIGJ0bi50aXRsZSA9IGlzTGlnaHQgPyAi44OA44O844Kv44Gr5YiH5pu/IiA6ICLjg6njgqTjg4jjgavliIfmm78iOwogIH0KICBmdW5jdGlvbiBzZXRUaGVtZSh0KXsKICAgIGRvY3VtZW50LmRvY3VtZW50RWxlbWVudC5zZXRBdHRyaWJ1dGUoImRhdGEtdGhlbWUiLCB0KTsKICAgIGxvY2FsU3RvcmFnZS5zZXRJdGVtKCJhaWFnZW50LnRoZW1lIiwgdCk7CiAgICBhcHBseUljb24oKTsKICB9CiAgYnRuLmFkZEV2ZW50TGlzdGVuZXIoImNsaWNrIiwgZnVuY3Rpb24oKXsKICAgIHZhciBjdXIgPSBkb2N1bWVudC5kb2N1bWVudEVsZW1lbnQuZ2V0QXR0cmlidXRlKCJkYXRhLXRoZW1lIikgfHwgImRhcmsiOwogICAgc2V0VGhlbWUoY3VyID09PSAibGlnaHQiID8gImRhcmsiIDogImxpZ2h0Iik7CiAgfSk7CiAgYXBwbHlJY29uKCk7Cn0pKCk7CgovKiAtLS0tLS0tLS0tIGluaXQgLS0tLS0tLS0tLSAqLwpyZXNpemVJbnB1dCgpOwppbnB1dEVsLmZvY3VzKCk7CmVtcHR5U3RhdGUodHJ1ZSk7CnNlbmRCdG4uZGlzYWJsZWQgPSBmYWxzZTsKaW5pdFNpZGViYXIoKTsKaW5pdFNlcnZlclN0YXRlKCk7IC8vIOOCteODvOODkOeKtuaFi+OCkuWPjeaYoOOBl+OBpOOBpOOCu+ODg+OCt+ODp+ODs+OCkuiqreOBv+i+vOOCgAo8L3NjcmlwdD4KPC9ib2R5Pgo8L2h0bWw+Cg==";
        static string uiHtml;
        static HttpServer()
        {
            uiHtml = Encoding.UTF8.GetString(Convert.FromBase64String(EmbeddedIndexHtml));
        }

        public HttpServer(int port, Config cfg)
        {
            config = cfg;
            listener = new TcpListener(IPAddress.Loopback, port);
            running = true;
        }

        public void Stop()
        {
            running = false;
            try { listener.Stop(); } catch { }
        }

        public void Run()
        {
            listener.Start();
            while (running)
            {
                TcpClient client = null;
                try
                {
                    client = listener.AcceptTcpClient();
                    client.NoDelay = true;
                    Thread t = new Thread(new ParameterizedThreadStart(HandleClient));
                    t.IsBackground = true;
                    t.Start(client);
                }
                catch (SocketException)
                {
                    if (running) throw;
                }
                catch (Exception)
                {
                    if (client != null) { try { client.Close(); } catch { } }
                }
            }
        }

        void HandleClient(object state)
        {
            TcpClient client = (TcpClient)state;
            try
            {
                using (NetworkStream stream = client.GetStream())
                {
                    HttpRequest req = ReadRequest(stream);
                    if (req == null) return;

                    if (req.Method == "POST" && req.Path == "/api/chat/stream")
                    {
                        HandleChatStream(stream, req);
                        return;
                    }

                    HttpResponse res;
                    try
                    {
                        res = Route(req);
                    }
                    catch (Exception ex)
                    {
                        res = JsonResponse(500, MakeError("server_error", ex.Message));
                    }

                    WriteResponse(stream, res);
                }
            }
            catch (Exception)
            {
                // 接続断などは無視
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        // ---------------- ルーティング ----------------
        HttpResponse Route(HttpRequest req)
        {
            string path = req.Path;

            if (req.Method == "GET" && (path == "/" || path == ""))
                return TextResponse(200, uiHtml, "text/html; charset=utf-8");
            if (req.Method == "GET" && path.StartsWith("/static/"))
                return StaticFile(path.Substring("/static/".Length));
            if (req.Method == "GET" && path == "/favicon.ico")
                return RawResponse(204, new byte[0], "image/x-icon");
            if (req.Method == "GET" && path == "/api/health")
                return HandleHealth();
            if (req.Method == "GET" && path == "/api/tools")
                return HandleTools();
            if (req.Method == "POST" && path == "/api/tools")
                return HandleSetTools(req);
            if (req.Method == "GET" && path == "/api/sessions")
                return HandleListSessions();
            if (req.Method == "POST" && path == "/api/sessions")
                return HandleCreateSession();
            if (req.Method == "GET" && path.StartsWith("/api/sessions/"))
                return HandleGetSession(Path.GetFileName(path));
            if (req.Method == "DELETE" && path.StartsWith("/api/sessions/"))
                return HandleDeleteSession(Path.GetFileName(path));
            if (req.Method == "POST" && path.StartsWith("/api/sessions/") && path.EndsWith("/rename"))
            {
                string rid = path.Substring("/api/sessions/".Length);
                if (rid.EndsWith("/rename")) rid = rid.Substring(0, rid.Length - "/rename".Length);
                return HandleRenameSession(req, rid);
            }
            if (req.Method == "POST" && path == "/api/chat")
                return HandleChat(req);
            if (req.Method == "POST" && path == "/api/cancel")
            {
                AgentControl.RequestCancel();
                Dictionary<string, object> cc = new Dictionary<string, object>();
                cc["ok"] = true;
                cc["cancelled"] = true;
                return JsonResponse(200, cc);
            }

            return TextResponse(404, "Not Found", "text/plain; charset=utf-8");
        }

        HttpResponse StaticFile(string name)
        {
            // パストラバーサル対策
            string clean = name.Replace("\\", "/");
            if (clean.Contains(".."))
                return TextResponse(400, "Bad Request", "text/plain; charset=utf-8");

            string full = Path.Combine(config.BindPath, clean);
            if (!File.Exists(full))
                return TextResponse(404, "Not Found: " + name, "text/plain; charset=utf-8");

            byte[] data;
            try
            {
                data = File.ReadAllBytes(full);
            }
            catch (Exception ex)
            {
                return TextResponse(500, "Read error: " + ex.Message, "text/plain; charset=utf-8");
            }
            return RawResponse(200, data, ContentTypeFor(clean));
        }

        // ---------------- /api/chat ----------------
        HttpResponse HandleChat(HttpRequest req)
        {
            if (req.Body == null || req.Body.Length == 0)
                return JsonResponse(400, MakeError("bad_request", "empty body"));

            object payload;
            try
            {
                payload = Json.Parse(Encoding.UTF8.GetString(req.Body));
            }
            catch (Exception ex)
            {
                return JsonResponse(400, MakeError("bad_json", ex.Message));
            }

            Dictionary<string, object> map = payload as Dictionary<string, object>;
            if (map == null)
                return JsonResponse(400, MakeError("bad_request", "body must be a JSON object"));

            string message = ""; string sessionId = ""; bool clearOnly = false;
            if (map.ContainsKey("message")) message = Json.AsString(map["message"]);
            if (map.ContainsKey("session")) sessionId = Json.AsString(map["session"]);
            if (map.ContainsKey("clear")) clearOnly = true;

            // セッション ID が無ければ新規作成
            if (sessionId.Length == 0)
                sessionId = store.NewId();

            // Clear: 指定セッションの履歴を空にする
            if (clearOnly)
            {
                lock (sessionLock)
                {
                    store.Save(sessionId, new List<object>(), "新規会話");
                }
                Dictionary<string, object> clr = new Dictionary<string, object>();
                clr["ok"] = true;
                clr["cleared"] = true;
                clr["session"] = sessionId;
                return JsonResponse(200, clr);
            }

            if (message.Trim().Length == 0)
                return JsonResponse(400, MakeError("bad_request", "message is required"));

            // セッション文脈を復元(完全履歴 + 要約 + 文脈開始位置)
            SessionCtx ctx = new SessionCtx();
            ctx.Id = sessionId;
            lock (sessionLock)
            {
                Dictionary<string, object> meta = store.Load(sessionId);
                if (meta != null)
                {
                    ctx.Title = meta.ContainsKey("title") ? Json.AsString(meta["title"]) : "";
                    ctx.Summary = meta.ContainsKey("summary") ? Json.AsString(meta["summary"]) : "";
                    ctx.ContextStart = meta.ContainsKey("contextStart") ? Json.AsInt(meta["contextStart"]) : 0;
                    object ms = meta.ContainsKey("messages") ? meta["messages"] : null;
                    if (ms is List<object>) ctx.Messages = (List<object>)ms;
                }
            }

            string reply;
            try
            {
                busy = true;
                busySession = sessionId;
                reply = LlmClient.RunAgent(config, ctx, message, delegate(SessionCtx c)
                {
                    // コンパクション/ツール進行の都度、完全履歴と要約を永続化
                    lock (sessionLock)
                    {
                        if (c.Title.Length == 0) c.Title = ShortTitle(message);
                        store.Save(c.Id, c.Messages, c.Title, c.Summary, c.ContextStart);
                    }
                });
            }
            catch (Exception ex)
            {
                return JsonResponse(502, MakeError("llm_error", ex.Message));
            }
            finally
            {
                busy = false;
                busySession = "";
            }

            // 最終確定保存(タイトルの自動付与含む)
            lock (sessionLock)
            {
                if (ctx.Title.Length == 0)
                    ctx.Title = ShortTitle(message);
                store.Save(sessionId, ctx.Messages, ctx.Title, ctx.Summary, ctx.ContextStart);
            }

            Dictionary<string, object> ok = new Dictionary<string, object>();
            ok["ok"] = true;
            ok["model"] = config.Model;
            ok["reply"] = reply;
            ok["tools"] = config.AllowTools;
            ok["session"] = sessionId;
            ok["title"] = ctx.Title;
            return JsonResponse(200, ok);
        }

        static string ShortTitle(string s)
        {
            s = s.Trim();
            if (s.Length <= 30) return s;
            return s.Substring(0, 30) + "…";
        }

        // ---------------- /api/chat/stream (SSE ストリーミング) ----------------
        void HandleChatStream(NetworkStream stream, HttpRequest req)
        {
            string message = ""; string sessionId = "";
            if (req.Body != null && req.Body.Length > 0)
            {
                try
                {
                    object o = Json.Parse(Encoding.UTF8.GetString(req.Body));
                    Dictionary<string, object> m = o as Dictionary<string, object>;
                    if (m != null)
                    {
                        if (m.ContainsKey("message")) message = Json.AsString(m["message"]);
                        if (m.ContainsKey("session")) sessionId = Json.AsString(m["session"]);
                    }
                }
                catch { }
            }
            if (sessionId.Length == 0) sessionId = store.NewId();

            if (message.Trim().Length == 0)
            {
                WriteSseHead(stream);
                WriteSseEvent(stream, "error", "{ \"message\" : \"message is required\" }");
                WriteSseEnd(stream);
                return;
            }

            SessionCtx ctx = new SessionCtx();
            ctx.Id = sessionId;
            lock (sessionLock)
            {
                Dictionary<string, object> meta = store.Load(sessionId);
                if (meta != null)
                {
                    ctx.Title = meta.ContainsKey("title") ? Json.AsString(meta["title"]) : "";
                    ctx.Summary = meta.ContainsKey("summary") ? Json.AsString(meta["summary"]) : "";
                    ctx.ContextStart = meta.ContainsKey("contextStart") ? Json.AsInt(meta["contextStart"]) : 0;
                    object ms = meta.ContainsKey("messages") ? meta["messages"] : null;
                    if (ms is List<object>) ctx.Messages = (List<object>)ms;
                }
            }

            busy = true;
            busySession = sessionId;
            AgentProgress.Clear();
            WriteSseHead(stream);

            try
            {
                LlmClient.RunAgentStream(config, ctx, message,
                    delegate (string ev, string data) { WriteSseEventSafe(stream, ev, data); },
                    delegate (SessionCtx c)
                    {
                        lock (sessionLock)
                        {
                            if (c.Title.Length == 0) c.Title = ShortTitle(message);
                            store.Save(c.Id, c.Messages, c.Title, c.Summary, c.ContextStart);
                        }
                    });
            }
            catch (AgentCancelledException) { }
            catch (Exception ex)
            {
                WriteSseEventSafe(stream, "error", "{ \"message\" : " + JsonStr(ex.Message) + " }");
            }
            finally
            {
                busy = false;
                busySession = "";
                lock (sessionLock)
                {
                    if (ctx.Title.Length == 0) ctx.Title = ShortTitle(message);
                    store.Save(sessionId, ctx.Messages, ctx.Title, ctx.Summary, ctx.ContextStart);
                }
                WriteSseEnd(stream);
            }
        }

        void WriteSseHead(NetworkStream stream)
        {
            string h = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream; charset=utf-8\r\n"
                     + "Cache-Control: no-cache\r\nConnection: close\r\n\r\n";
            byte[] b = Encoding.UTF8.GetBytes(h);
            try { stream.Write(b, 0, b.Length); stream.Flush(); } catch { }
        }
        void WriteSseEvent(NetworkStream stream, string ev, string data)
        {
            string s = "event: " + ev + "\r\ndata: " + data + "\r\n\r\n";
            byte[] b = Encoding.UTF8.GetBytes(s);
            try { stream.Write(b, 0, b.Length); stream.Flush(); } catch { }
        }
        void WriteSseEventSafe(NetworkStream stream, string ev, string data)
        {
            WriteSseEvent(stream, ev, data); // 例外は WriteSseEvent 内で握る
        }
        void WriteSseEnd(NetworkStream stream)
        {
            string s = "event: end\r\ndata: {}\r\n\r\n";
            byte[] b = Encoding.UTF8.GetBytes(s);
            try { stream.Write(b, 0, b.Length); stream.Flush(); } catch { }
        }

        static string JsonStr(string s)
        {
            return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r")
                .Replace("\t", "\\t") + "\"";
        }

        // ---------------- /api/health ----------------
        HttpResponse HandleHealth()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = true;
            d["server"] = "agentweb (csc.exe / C#5)";
            d["model"] = config.Model;
            d["apiKey"] = (config.SecretKey.Length > 0) ? "set" : "not-set";
            d["apiBaseUrl"] = config.ApiBaseUrl;
            d["allowTools"] = config.AllowTools;
            d["dataDir"] = store.DataDir;
            d["busy"] = busy;
            if (busy && busySession != null) d["busySession"] = busySession;
            d["progress"] = AgentProgress.Snapshot();
            d["proxy"] = (config.ProxyUrl != null && config.ProxyUrl.Trim().Length > 0)
                ? config.ProxyUrl.Trim()
                : (config.UseSystemProxy ? "system" : "off");
            return JsonResponse(200, d);
        }

        // ---------------- /api/tools ----------------
        HttpResponse HandleTools()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = true;
            d["allowTools"] = config.AllowTools;
            d["tools"] = ToolExecutor.Definitions();
            return JsonResponse(200, d);
        }

        // 実行時にツールの有効/無効を切り替える (config.AllowTools を変更)
        HttpResponse HandleSetTools(HttpRequest req)
        {
            bool allow = true;
            if (req.Body != null && req.Body.Length > 0)
            {
                try
                {
                    object o = Json.Parse(Encoding.UTF8.GetString(req.Body));
                    Dictionary<string, object> m = o as Dictionary<string, object>;
                    if (m != null && m.ContainsKey("allow"))
                    {
                        string v = Json.AsString(m["allow"]).Trim().ToLowerInvariant();
                        allow = (v == "true" || v == "1" || v == "on");
                    }
                }
                catch { }
            }
            config.AllowTools = allow;
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = true;
            d["allowTools"] = config.AllowTools;
            return JsonResponse(200, d);
        }

        // ---------------- /api/sessions ----------------
        HttpResponse HandleListSessions()
        {
            List<Dictionary<string, object>> list;
            lock (sessionLock)
            {
                list = store.List();
            }
            // 更新日時が新しい順
            list.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
            {
                string ta = (a != null && a.ContainsKey("updatedAt")) ? Json.AsString(a["updatedAt"]) : "";
                string tb = (b != null && b.ContainsKey("updatedAt")) ? Json.AsString(b["updatedAt"]) : "";
                return tb.CompareTo(ta);
            });

            List<object> sos = new List<object>();
            foreach (Dictionary<string, object> x in list)
                sos.Add(x);

            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = true;
            d["dataDir"] = store.DataDir;
            d["sessions"] = sos;
            return JsonResponse(200, d);
        }

        HttpResponse HandleCreateSession()
        {
            string id = store.NewId();
            lock (sessionLock)
            {
                store.Save(id, new List<object>(), "新規会話");
            }
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = true;
            d["session"] = id;
            d["title"] = "新規会話";
            return JsonResponse(200, d);
        }

        HttpResponse HandleGetSession(string id)
        {
            List<object> view = new List<object>();
            Dictionary<string, object> meta;
            lock (sessionLock)
            {
                meta = store.Load(id);
                List<object> msgs = store.MessagesOf(id);
                foreach (object o in msgs)
                {
                    Dictionary<string, object> m = o as Dictionary<string, object>;
                    if (m == null) continue;
                    string role = m.ContainsKey("role") ? Json.AsString(m["role"]) : "";
                    if (role == "tool") continue;
                    bool hasToolCalls = m.ContainsKey("tool_calls");
                    string content = m.ContainsKey("content") ? Json.AsString(m["content"]) : "";
                    if (role == "assistant" && (hasToolCalls || content.Length == 0)) continue;
                    Dictionary<string, object> mv = new Dictionary<string, object>();
                    mv["role"] = role;
                    mv["content"] = content;
                    mv["ts"] = (m.ContainsKey("ts")) ? Json.AsString(m["ts"]) : "";
                    view.Add(mv);
                }
            }

            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = true;
            d["id"] = id;
            d["title"] = (meta != null && meta.ContainsKey("title")) ? Json.AsString(meta["title"]) : "新規会話";
            d["messages"] = view;
            return JsonResponse(200, d);
        }

        HttpResponse HandleDeleteSession(string id)
        {
            lock (sessionLock)
            {
                store.Delete(id);
            }
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = true;
            d["deleted"] = id;
            return JsonResponse(200, d);
        }

        HttpResponse HandleRenameSession(HttpRequest req, string id)
        {
            if (req.Body == null || req.Body.Length == 0)
                return JsonResponse(400, MakeError("bad_request", "empty body"));
            string title = "";
            try
            {
                object o = Json.Parse(Encoding.UTF8.GetString(req.Body));
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (m != null && m.ContainsKey("title")) title = Json.AsString(m["title"]);
            }
            catch { }

            bool ok;
            lock (sessionLock)
            {
                ok = store.Rename(id, title);
            }
            if (!ok)
                return JsonResponse(404, MakeError("not_found", "セッションが見つかりません"));
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = true;
            d["session"] = id;
            d["title"] = (title.Trim().Length == 0) ? "新規会話" : title.Trim();
            return JsonResponse(200, d);
        }

        // ---------------- リクエスト読み取り ----------------
        HttpRequest ReadRequest(NetworkStream stream)
        {
            stream.ReadTimeout = 15000;
            byte[] headBytes = ReadUntil(stream, new byte[] { 13, 10, 13, 10 }, 64 * 1024);
            if (headBytes == null)
                return null;

            string head = Encoding.UTF8.GetString(headBytes);
            string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || lines[0].Length == 0)
                return null;

            string[] reqLine = lines[0].Split(' ');
            if (reqLine.Length < 2)
                return null;

            HttpRequest req = new HttpRequest();
            req.Method = reqLine[0];
            req.Path = reqLine[1];
            req.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;
                int idx = line.IndexOf(':');
                if (idx > 0)
                {
                    string k = line.Substring(0, idx).Trim();
                    string v = line.Substring(idx + 1).Trim();
                    req.Headers[k] = v;
                }
            }

            string cl = req.Headers.ContainsKey("Content-Length") ? req.Headers["Content-Length"] : "";
            int length = 0;
            if (cl != null && cl.Length > 0)
                int.TryParse(cl, out length);
            if (length > 0)
            {
                if (length > 8 * 1024 * 1024)
                    throw new Exception("request too large");
                req.Body = ReadExactly(stream, length);
            }
            return req;
        }

        static byte[] ReadUntil(NetworkStream stream, byte[] terminator, int max)
        {
            MemoryStream ms = new MemoryStream();
            int match = 0;
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) break;
                ms.WriteByte((byte)b);
                if (b == terminator[match])
                {
                    match++;
                    if (match == terminator.Length)
                        break;
                }
                else
                {
                    match = (b == terminator[0]) ? 1 : 0;
                }
                if (ms.Length > max)
                    throw new Exception("header too large");
            }
            return ms.ToArray();
        }

        static byte[] ReadExactly(NetworkStream stream, int count)
        {
            byte[] buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buf, read, count - read);
                if (n <= 0) break;
                read += n;
            }
            return buf;
        }

        // ---------------- レスポンス送信 ----------------
        void WriteResponse(NetworkStream stream, HttpResponse res)
        {
            string reason = ReasonFor(res.Status);
            StringBuilder sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(res.Status).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Content-Type: ").Append(res.ContentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(res.Body.Length).Append("\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            sb.Append("\r\n");
            byte[] header = Encoding.UTF8.GetBytes(sb.ToString());
            stream.Write(header, 0, header.Length);
            stream.Write(res.Body, 0, res.Body.Length);
            stream.Flush();
        }

        static string ReasonFor(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 404: return "Not Found";
                case 500: return "Internal Server Error";
                case 502: return "Bad Gateway";
                default: return "OK";
            }
        }

        static string ContentTypeFor(string file)
        {
            file = file.ToLower();
            if (file.EndsWith(".html")) return "text/html; charset=utf-8";
            if (file.EndsWith(".css")) return "text/css; charset=utf-8";
            if (file.EndsWith(".js")) return "application/javascript; charset=utf-8";
            if (file.EndsWith(".png")) return "image/png";
            if (file.EndsWith(".jpg") || file.EndsWith(".jpeg")) return "image/jpeg";
            if (file.EndsWith(".gif")) return "image/gif";
            if (file.EndsWith(".svg")) return "image/svg+xml";
            if (file.EndsWith(".json")) return "application/json; charset=utf-8";
            if (file.EndsWith(".txt")) return "text/plain; charset=utf-8";
            if (file.EndsWith(".ico")) return "image/x-icon";
            return "application/octet-stream";
        }

        static HttpResponse StaticResponse(int status, Dictionary<string, object> obj)
        {
            return JsonResponse(status, obj);
        }

        static HttpResponse TextResponse(int status, string text, string contentType)
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            HttpResponse r = new HttpResponse();
            r.Status = status;
            r.ContentType = contentType;
            r.Body = b;
            return r;
        }

        static HttpResponse RawResponse(int status, byte[] body, string contentType)
        {
            HttpResponse r = new HttpResponse();
            r.Status = status;
            r.ContentType = contentType;
            r.Body = body;
            return r;
        }

        static HttpResponse JsonResponse(int status, object obj)
        {
            string json = Json.ToString(obj);
            return TextResponse(status, json, "application/json; charset=utf-8");
        }

        static Dictionary<string, object> MakeError(string code, string message)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = false;
            d["error"] = code;
            d["message"] = message;
            return d;
        }
    }

    class HttpRequest
    {
        public string Method;
        public string Path;
        public Dictionary<string, string> Headers;
        public byte[] Body;
    }

    class HttpResponse
    {
        public int Status;
        public string ContentType;
        public byte[] Body;
    }


    // ------------------------------------------------------------------------
    //  セッションストア (Windows AppData 配下に JSON で永続化)
    //   保存先:  %APPDATA%\agentweb\sessions\<sessionId>.json
    // ------------------------------------------------------------------------
    class SessionStore
    {
        string sessionsDir;

        public SessionStore()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (appData == null || appData.Length == 0)
                appData = Path.GetTempPath();
            sessionsDir = Path.Combine(appData, "agentweb", "sessions");
            Directory.CreateDirectory(sessionsDir);
        }

        public string DataDir
        {
            get { return sessionsDir; }
        }

        static string Sanitize(string id)
        {
            if (id == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char c in id)
                if (Char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            string clean = sb.ToString();
            return (clean.Length == 0) ? "default" : clean;
        }

        string FileOf(string id)
        {
            return Path.Combine(sessionsDir, Sanitize(id) + ".json");
        }

        public string NewId()
        {
            return "s" + DateTime.Now.ToString("yyyyMMddHHmmss")
                   + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        // セッションメタデータ全体を読み込む(無ければ null)
        public Dictionary<string, object> Load(string id)
        {
            string f = FileOf(id);
            if (!File.Exists(f)) return null;
            try
            {
                object o = Json.Parse(File.ReadAllText(f, Encoding.UTF8));
                return o as Dictionary<string, object>;
            }
            catch { return null; }
        }

        // 会話履歴(messages)のみ取り出す
        public List<object> MessagesOf(string id)
        {
            Dictionary<string, object> meta = Load(id);
            if (meta != null && meta.ContainsKey("messages") && meta["messages"] is List<object>)
                return (List<object>)meta["messages"];
            return new List<object>();
        }

        public void Save(string id, List<object> messages, string title)
        {
            Save(id, messages, title, "", 0);
        }

        public void Save(string id, List<object> messages, string title, string summary, int contextStart)
        {
            Dictionary<string, object> meta = Load(id);
            string createdAt = (meta != null && meta.ContainsKey("createdAt"))
                ? Json.AsString(meta["createdAt"]) : "";
            if (createdAt.Length == 0) createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

            Dictionary<string, object> s = new Dictionary<string, object>();
            s["id"] = Sanitize(id);
            s["title"] = (title == null || title.Trim().Length == 0) ? "新規会話" : title;
            s["createdAt"] = createdAt;
            s["updatedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            s["summary"] = (summary == null || summary.Trim().Length == 0) ? "" : summary;
            s["contextStart"] = (contextStart < 0) ? 0 : contextStart;
            s["messages"] = (messages == null) ? new List<object>() : messages;
            File.WriteAllText(FileOf(id), Json.ToString(s), Encoding.UTF8);
        }

        public List<Dictionary<string, object>> List()
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            try
            {
                foreach (string f in Directory.GetFiles(sessionsDir, "*.json"))
                {
                    try
                    {
                        object o = Json.Parse(File.ReadAllText(f, Encoding.UTF8));
                        Dictionary<string, object> s = o as Dictionary<string, object>;
                        if (s == null) continue;
                        int count = 0;
                        if (s.ContainsKey("messages") && s["messages"] is List<object>)
                            count = ((List<object>)s["messages"]).Count;
                        s["count"] = count;
                        list.Add(s);
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        public void Delete(string id)
        {
            string f = FileOf(id);
            if (File.Exists(f))
            {
                try { File.Delete(f); } catch { }
            }
        }

        public bool Rename(string id, string title)
        {
            Dictionary<string, object> meta = Load(id);
            if (meta == null) return false;
            string t = (title == null || title.Trim().Length == 0) ? "新規会話" : title.Trim();
            meta["title"] = t;
            meta["updatedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            File.WriteAllText(FileOf(id), Json.ToString(meta), Encoding.UTF8);
            return true;
        }
    }

    // ------------------------------------------------------------------------
    //  LLM API クライアント (OpenAI 互換 /chat/completions)
    //  外部ライブラリは使わず HttpWebRequest のみ。TLS1.2 を明示設定。
    // ------------------------------------------------------------------------
    // ------------------------------------------------------------------------
    //  LLM API クライアント (OpenAI 互換 /chat/completions) + エージェントループ
    //  - ツール(Function Calling)に対応: LLM が tool_calls を返したら実行して
    //    結果を messages に返し、最終回答が出るまでループする。
    //  - 外部ライブラリ不使用。TLS1.2 を明示設定。
    // ------------------------------------------------------------------------
    // ------------------------------------------------------------------------
    //  セッション文脈 (pi 方式のコンパクション用)
    //  - Messages   : 完全な履歴(lossless。ディスクに保存される)
    //  - Summary    : 古い部分を要約したテキスト
    //  - ContextStart: LLM へ送る直接メッセージの開始位置(Messages 内の添字)
    //    LLM が参照する文脈 = [System + 要約] + Messages[ContextStart..]
    // ------------------------------------------------------------------------
    class SessionCtx
    {
        public string Id;
        public string Title = "";
        public string Summary = "";
        public int ContextStart = 0;
        public int LastTotalTokens = 0;   // 直前の API 応答 usage.total_tokens
        public List<object> Messages = new List<object>();
    }

    // エージェント進行状況(busy中にWeb UIへツール実行などを表示する用)
    static class AgentProgress
    {
        static List<object> log = new List<object>();
        static object lck = new object();

        public static void Clear()
        {
            lock (lck) { log.Clear(); }
        }
        public static void Add(string s)
        {
            lock (lck)
            {
                log.Add(s);
                if (log.Count > 40) log.RemoveAt(0); // 上限
            }
        }
        public static List<object> Snapshot()
        {
            lock (lck) { return new List<object>(log); }
        }
    }

    // エージェントの停止制御(thinking中に Web UI から中止する用)
    static class AgentControl
    {
        public static volatile bool CancelRequested;
        public static volatile HttpWebRequest CurrentRequest; // 処理中の LLM リクエスト

        public static void Reset() { CancelRequested = false; CurrentRequest = null; }

        public static void RequestCancel()
        {
            CancelRequested = true;
            HttpWebRequest r = CurrentRequest;
            if (r != null)
            {
                try { r.Abort(); } catch { }
            }
        }
    }

    // キャンセルによる中断であることを示す例外
    class AgentCancelledException : Exception
    {
        public AgentCancelledException() : base("cancelled") { }
    }

    static class LlmClient
    {
        // 1 ターンのツール反復上限
        const int MAX_TURNS = 10;

        // セッション文脈に user を追加し、pi方式のコンパクションを行いながら
        // ツール実行ループを回して最終テキストを返す。
        public static string RunAgent(Config cfg, SessionCtx ctx, string userMessage, Action<SessionCtx> save)
        {
            ctx.Messages.Add(Msg("user", userMessage));
            AgentProgress.Clear();
            AgentControl.Reset();

            // トークン超過時は古い文脈を要約して置換(コンパクション)
            MaybeCompact(cfg, ctx);
            if (save != null) save(ctx); // 要約/開始位置を永続化

            try
            {
                for (int turn = 0; turn < MAX_TURNS; turn++)
                {
                    if (AgentControl.CancelRequested) return CancelReply(ctx);

                    List<object> eff = BuildEffectiveMessages(cfg, ctx);
                    LlmResp resp;
                    try
                    {
                        resp = CallOnce(cfg, eff, cfg.AllowTools);
                    }
                    catch (AgentCancelledException)
                    {
                        return CancelReply(ctx);
                    }
                    if (resp.TotalTokens > 0) ctx.LastTotalTokens = resp.TotalTokens;

                    if (resp.ToolCalls != null && resp.ToolCalls.Count > 0)
                    {
                        // assistant の tool_calls メッセージを完全履歴に保存
                        ctx.Messages.Add(AsstWithToolCalls(resp));

                        int n = Math.Min(resp.ToolCalls.Count, cfg.MaxToolsPerTurn);
                        for (int k = 0; k < n; k++)
                        {
                            if (AgentControl.CancelRequested) return CancelReply(ctx);

                            Dictionary<string, object> call = resp.ToolCalls[k] as Dictionary<string, object>;
                            if (call == null) { continue; }

                            if (!call.ContainsKey("function") || !(call["function"] is Dictionary<string, object>))
                            {
                                Console.WriteLine("[agent] !! tool_call 構造異常: " + Json.ToString(call));
                            }

                            string id = Json.AsString(Get(call, "id"));
                            if (id == null || id.Length == 0) id = "call_" + k;
                            call["id"] = id; // エコー(assistant)と tool 応答の id を一致させる

                            Dictionary<string, object> fn = Get(call, "function") as Dictionary<string, object>;
                            string name = "";
                            string argsJson = "{}";
                            if (fn != null)
                            {
                                name = Json.AsString(Get(fn, "name"));
                                object argsVal = (fn.ContainsKey("arguments")) ? fn["arguments"] : null;
                                if (argsVal is string)      argsJson = (string)argsVal;
                                else if (argsVal != null)   argsJson = Json.ToString(argsVal);
                            }
                            else
                            {
                                Console.WriteLine("[agent] !! function が無い tool_call: " + Json.ToString(call));
                            }

                            if (name == null || name.Trim().Length == 0)
                                name = ToolExecutor.InferName(fn, argsJson);
                            if (fn != null && name != null && name.Trim().Length > 0) fn["name"] = name;

                            AgentProgress.Add("ツール実行: " + (name.Length > 0 ? name : "?"));
                            Console.WriteLine("[agent] tool_call[" + k + "] id=" + id + " name=" + name);
                            Console.WriteLine("[agent]   args=" + (argsJson.Length > 400 ? argsJson.Substring(0, 400) : argsJson));

                            string result;
                            try
                            {
                                result = ToolExecutor.Execute(name, argsJson, cfg);
                            }
                            catch (Exception ex)
                            {
                                result = "{\"error\": \"" + Escape(ex.Message) + "\"}";
                            }
                            Console.WriteLine("[agent]   result=" + (result.Length > 200 ? result.Substring(0, 200) + "..." : result));
                            ctx.Messages.Add(ToolMsg(id, name, result));
                        }
                        if (save != null) save(ctx);
                        continue;
                    }

                    // 最終回答
                    string final = resp.Content;
                    ctx.Messages.Add(Msg("assistant", final));
                    return final;
                }
                throw new Exception("LLM が最終回答を出せませんでした(ツール反復が多すぎます)");
            }
            finally
            {
                if (save != null) save(ctx);
            }
        }

        static string CancelReply(SessionCtx ctx)
        {
            string msg = "⚠ 処理を中断しました。";
            ctx.Messages.Add(Msg("assistant", msg));
            return msg;
        }

        static Dictionary<string, object> AsstWithToolCalls(LlmResp resp)
        {
            Dictionary<string, object> asst = new Dictionary<string, object>();
            asst["role"] = "assistant";
            if (resp.Content != null && resp.Content.Length > 0)
                asst["content"] = resp.Content;
            asst["tool_calls"] = resp.ToolCalls;
            return asst;
        }

        // ---- コンパクション(pi方式) ----

        static long TokensOf(object o)
        {
            if (o == null) return 0;
            try { return Json.ToString(o).Length / 2 + 1; }
            catch { return 0; }
        }

        static string ComposeSystem(string systemPrompt, string summary)
        {
            if (summary == null || summary.Trim().Length == 0) return systemPrompt;
            return systemPrompt + "\n\n[これまでの会話の要約]\n" + summary;
        }

        // LLM が参照する文脈 = [system + 要約] + Messages[ContextStart..]
        static List<object> BuildEffectiveMessages(Config cfg, SessionCtx ctx)
        {
            List<object> eff = new List<object>();
            eff.Add(Msg("system", ComposeSystem(cfg.SystemPrompt, ctx.Summary)));
            if (ctx.ContextStart < 0) ctx.ContextStart = 0;
            for (int i = ctx.ContextStart; i < ctx.Messages.Count; i++)
                eff.Add(ctx.Messages[i]);
            return eff;
        }

        // ターン境界: user または最終回答の assistant のみ切ってよい
        // (tool 結果や tool_calls の途中では切らない)
        static bool IsValidCutStart(object o)
        {
            Dictionary<string, object> m = o as Dictionary<string, object>;
            if (m == null) return false;
            string role = m.ContainsKey("role") ? Json.AsString(m["role"]) : "";
            if (role == "tool") return false;
            if (role == "user") return true;
            if (role == "assistant")
            {
                if (m.ContainsKey("tool_calls") && m["tool_calls"] != null) return false;
                string c = m.ContainsKey("content") ? Json.AsString(m["content"]) : "";
                return c.Trim().Length > 0;
            }
            return false;
        }

        // トークン超過を検知したら、最近分だけ残し古い分を要約して置換する
        static void MaybeCompact(Config cfg, SessionCtx ctx)
        {
            if (ctx.ContextStart >= ctx.Messages.Count) return;
            long threshold = cfg.ContextWindow - cfg.ReserveTokens;
            if (threshold <= 0) return;

            long active = TokensOf(Msg("system", ComposeSystem(cfg.SystemPrompt, ctx.Summary)));
            for (int i = ctx.ContextStart; i < ctx.Messages.Count; i++)
                active += TokensOf(ctx.Messages[i]);

            if (active <= threshold && ctx.LastTotalTokens <= threshold) return; // 超過していない

            // 末尾から keepRecentTokens 分だけ残す cut(古い側の保持開始位置)を決める
            long kept = 0;
            int cut = ctx.Messages.Count;
            for (int i = ctx.Messages.Count - 1; i >= 0; i--)
            {
                long t = TokensOf(ctx.Messages[i]);
                if (kept + t > cfg.KeepRecentTokens) { cut = i; break; }
                kept += t;
                cut = i;
            }
            // ターン境界を守る(必要なら保持側を新しくして分割を避ける)
            while (cut < ctx.Messages.Count && !IsValidCutStart(ctx.Messages[cut]))
                cut++;
            if (cut <= ctx.ContextStart) return;   // 要約できる新しい範囲が無い
            if (cut >= ctx.Messages.Count) return;  // 全体を残す(削れない)

            List<object> span = ctx.Messages.GetRange(ctx.ContextStart, cut - ctx.ContextStart);
            string newSummary = Summarize(cfg, ctx.Summary, span);
            ctx.Summary = newSummary;
            ctx.ContextStart = cut;
            ctx.LastTotalTokens = 0;
            Console.WriteLine("[agent] コンパクション: " + span.Count + " 通を要約, ContextStart=" + cut);
        }

        static string Clip(string s, int max)
        {
            if (s == null) return "";
            if (s.Length > max) return s.Substring(0, max) + "...";
            return s;
        }

        // 完全履歴の一部を、LLM が読めるテキストに展開する
        static string RenderTranscript(List<object> span)
        {
            StringBuilder sb = new StringBuilder();
            foreach (object o in span)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (m == null) continue;
                string role = m.ContainsKey("role") ? Json.AsString(m["role"]) : "";
                string content = m.ContainsKey("content") ? Json.AsString(m["content"]) : "";
                if (role == "system") continue;
                if (role == "user")
                {
                    sb.Append("ユーザー: ").Append(content).Append("\n");
                }
                else if (role == "tool")
                {
                    string nn = m.ContainsKey("name") ? Json.AsString(m["name"]) : "";
                    sb.Append("[ツール結果] ").Append(nn).Append(": ")
                      .Append(Clip(Json.AsString(m["content"]), 500)).Append("\n");
                }
                else if (role == "assistant")
                {
                    if (m.ContainsKey("tool_calls") && m["tool_calls"] is List<object>)
                    {
                        List<object> tcs = (List<object>)m["tool_calls"];
                        foreach (object tc in tcs)
                        {
                            Dictionary<string, object> c = tc as Dictionary<string, object>;
                            Dictionary<string, object> fn = (c == null) ? null : (Get(c, "function") as Dictionary<string, object>);
                            string nm = (fn != null) ? Json.AsString(Get(fn, "name")) : "";
                            string ar = (fn != null) ? Json.AsString(Get(fn, "arguments")) : "";
                            sb.Append("[ツール呼び出し] ").Append(nm).Append(" args=")
                              .Append(Clip(ar, 300)).Append("\n");
                        }
                    }
                    else
                    {
                        sb.Append("アシスタント: ").Append(content).Append("\n");
                    }
                }
            }
            return sb.ToString();
        }

        // 古いメッセージを LLM で要約する(前回要約は反復コンテキストとして渡す)
        static string Summarize(Config cfg, string prevSummary, List<object> span)
        {
            try
            {
                AgentProgress.Add("コンテキスト整理(要約)中…");
                List<object> body = new List<object>();
                body.Add(Msg("system",
                    "あなたはチャット履歴の要約専門のアシスタントです。"
                    + "後で会話の文脈として再利用できるよう、判断・実施した操作とその結果・ユーザーの要望・ファイルパス・重要な事実を"
                    + "漏らさず、日本語で簡潔な箇条書きに要約してください。前置きや「以下が要約です」のような文言は省き、要約本文のみを出力してください。"));

                StringBuilder ub = new StringBuilder();
                if (prevSummary != null && prevSummary.Trim().Length > 0)
                    ub.Append("【前回までの要約】\n").Append(prevSummary).Append("\n\n");
                ub.Append("【今回新たに要約する会話】\n").Append(RenderTranscript(span));
                body.Add(Msg("user", ub.ToString()));

                LlmResp resp = CallOnce(cfg, body, false); // 要約用はツール不要
                if (resp.Content != null && resp.Content.Trim().Length > 0)
                    return resp.Content.Trim();
                return prevSummary;
            }
            catch
            {
                return prevSummary;
            }
        }

        static Dictionary<string, object> Get(Dictionary<string, object> d, string key)
        {
            if (d != null && d.ContainsKey(key))
                return d[key] as Dictionary<string, object>;
            return null;
        }

        static Dictionary<string, object> Msg(string role, string content)
        {
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["role"] = role;
            m["content"] = content;
            m["ts"] = DateTime.Now.ToString("yyyy/MM/dd HH:mm");
            return m;
        }

        static Dictionary<string, object> ToolMsg(string id, string name, string result)
        {
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["role"] = "tool";
            m["tool_call_id"] = id;
            m["name"] = name;
            m["content"] = result;
            return m;
        }

        static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        // ---- 単発 API 呼び出し ----
        static LlmResp CallOnce(Config cfg, List<object> messages, bool useTools)
        {
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

            Dictionary<string, object> body = new Dictionary<string, object>();
            body["model"] = cfg.Model;
            // 保存用のメタデータ(ts 等)は API に送らない
            List<object> apiMessages = new List<object>();
            foreach (object o in messages)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (m == null) { apiMessages.Add(o); continue; }
                Dictionary<string, object> c = new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> kv in m)
                    if (kv.Key != "ts") c[kv.Key] = kv.Value;
                apiMessages.Add(c);
            }
            body["messages"] = apiMessages;
            body["temperature"] = 0.7;
            if (useTools && cfg.AllowTools)
                body["tools"] = ToolExecutor.Definitions();

            string json = Json.ToString(body);
            byte[] payload = Encoding.UTF8.GetBytes(json);

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(cfg.ApiBaseUrl);
            ApplyProxy(request, cfg);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "application/json";
            request.Headers["Authorization"] = "Bearer " + cfg.SecretKey;
            request.ContentLength = payload.Length;
            request.Timeout = 180000;
            request.ReadWriteTimeout = 180000;

            // リクエストをワーカースレッドで実行し、キャンセルをポーリングして即中断できるようにする
            // (HttpWebRequest.Abort はランタイムにより遮断しない場合があるため、待機を abort に頼らない)
            AgentControl.CurrentRequest = request;
            LlmResp result = null;
            Exception error = null;
            ManualResetEvent done = new ManualResetEvent(false);

            Thread worker = new Thread(delegate()
            {
                try
                {
                    using (Stream s = request.GetRequestStream())
                        s.Write(payload, 0, payload.Length);

                    string responseBody;
                    HttpStatusCode status;
                    try
                    {
                        using (HttpWebResponse resp = (HttpWebResponse)request.GetResponse())
                        {
                            status = resp.StatusCode;
                            using (Stream rs = resp.GetResponseStream())
                                responseBody = ReadAll(rs);
                        }
                    }
                    catch (WebException wex)
                    {
                        if (AgentControl.CancelRequested)
                        {
                            error = new AgentCancelledException();
                            return;
                        }
                        string detail = "";
                        if (wex.Response != null)
                        {
                            using (Stream rs = wex.Response.GetResponseStream())
                                detail = ReadAll(rs);
                        }
                        error = new Exception("LLM API エラー: " + (detail.Length > 0 ? detail : wex.Message));
                        return;
                    }
                    if (status != HttpStatusCode.OK)
                    {
                        error = new Exception("LLM API HTTPエラー(" + (int)status + "): " + responseBody);
                        return;
                    }
                    result = ParseOnce(responseBody);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    done.Set();
                }
            });
            worker.IsBackground = true;
            worker.Start();

            try
            {
                while (!done.WaitOne(150))
                {
                    if (AgentControl.CancelRequested)
                    {
                        try { request.Abort(); } catch { }
                        throw new AgentCancelledException();
                    }
                }
            }
            finally
            {
                AgentControl.CurrentRequest = null;
            }

            if (error != null) throw error;
            return result;
        }

        // ---- ストリーミング版 LLM API 呼び出し (stream:true) ----
        // content: 生成トークンをリアルタイムに contentCb へ配信。
        // tool_calls: ストリーミング断片を組立てて toolCallsOut へ集約。
        static LlmResp CallOnceStream(Config cfg, List<object> messages, bool useTools,
                                      Action<string> contentCb, List<object> toolCallsOut)
        {
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

            Dictionary<string, object> body = new Dictionary<string, object>();
            body["model"] = cfg.Model;
            List<object> apiMessages = new List<object>();
            foreach (object o in messages)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (m == null) { apiMessages.Add(o); continue; }
                Dictionary<string, object> c = new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> kv in m)
                    if (kv.Key != "ts") c[kv.Key] = kv.Value;
                apiMessages.Add(c);
            }
            body["messages"] = apiMessages;
            body["temperature"] = 0.7;
            body["stream"] = true;
            if (useTools && cfg.AllowTools)
                body["tools"] = ToolExecutor.Definitions();

            string json = Json.ToString(body);
            byte[] payload = Encoding.UTF8.GetBytes(json);

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(cfg.ApiBaseUrl);
            ProxyHelper.Apply(request, cfg);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "text/event-stream";
            request.Headers["Authorization"] = "Bearer " + cfg.SecretKey;
            request.ContentLength = payload.Length;
            request.Timeout = 180000;
            request.ReadWriteTimeout = 180000;

            StringBuilder finalContent = new StringBuilder();
            Dictionary<int, StreamToolAcc> acc = new Dictionary<int, StreamToolAcc>();

            AgentControl.CurrentRequest = request;
            try
            {
                using (Stream s = request.GetRequestStream())
                    s.Write(payload, 0, payload.Length);

                using (HttpWebResponse resp = (HttpWebResponse)request.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (AgentControl.CancelRequested) { try { request.Abort(); } catch { } break; }
                        if (line.Length == 0) continue;
                        if (!line.StartsWith("data:")) continue;
                        string data = line.Substring(5).Trim();
                        if (data == "[DONE]") break;
                        object parsed;
                        try { parsed = Json.Parse(data); }
                        catch { continue; }
                        Dictionary<string, object> root = parsed as Dictionary<string, object>;
                        if (root == null || !root.ContainsKey("choices") || !(root["choices"] is List<object>)) continue;
                        List<object> choices = (List<object>)root["choices"];
                        if (choices.Count == 0) continue;
                        Dictionary<string, object> ch = choices[0] as Dictionary<string, object>;
                        if (ch == null || !ch.ContainsKey("delta")) continue;
                        Dictionary<string, object> delta = ch["delta"] as Dictionary<string, object>;
                        if (delta == null) continue;

                        if (delta.ContainsKey("content") && delta["content"] != null)
                        {
                            string t = Json.AsString(delta["content"]);
                            if (t.Length > 0)
                            {
                                finalContent.Append(t);
                                if (contentCb != null) contentCb(t);
                            }
                        }
                        if (delta.ContainsKey("tool_calls") && delta["tool_calls"] is List<object>)
                        {
                            List<object> tcs = (List<object>)delta["tool_calls"];
                            foreach (object tco in tcs)
                            {
                                Dictionary<string, object> tc = tco as Dictionary<string, object>;
                                if (tc == null) continue;
                                int idx = tc.ContainsKey("index") ? Json.AsInt(tc["index"]) : acc.Count;
                                StreamToolAcc cur;
                                if (!acc.ContainsKey(idx)) { cur = new StreamToolAcc(); acc[idx] = cur; }
                                else cur = acc[idx];
                                if (tc.ContainsKey("id") && tc["id"] != null) cur.Id = Json.AsString(tc["id"]);
                                object fnObj = tc.ContainsKey("function") ? tc["function"] : null;
                                Dictionary<string, object> fn = fnObj as Dictionary<string, object>;
                                if (fn != null)
                                {
                                    if (fn.ContainsKey("name") && fn["name"] != null) cur.Name += Json.AsString(fn["name"]);
                                    if (fn.ContainsKey("arguments") && fn["arguments"] != null) cur.Args.Append(Json.AsString(fn["arguments"]));
                                }
                            }
                        }
                    }
                }
            }
            catch (WebException wex)
            {
                if (!AgentControl.CancelRequested)
                    throw new Exception("LLM API(stream)エラー: " + wex.Message);
            }
            finally
            {
                AgentControl.CurrentRequest = null;
            }

            if (toolCallsOut != null)
            {
                List<int> idxs = new List<int>(acc.Keys);
                idxs.Sort();
                foreach (int ix in idxs)
                {
                    StreamToolAcc c = acc[ix];
                    if (c.Name.Length == 0) continue;
                    Dictionary<string, object> fn = new Dictionary<string, object>();
                    fn["name"] = c.Name;
                    fn["arguments"] = c.Args.ToString();
                    Dictionary<string, object> call = new Dictionary<string, object>();
                    call["id"] = c.Id.Length > 0 ? c.Id : "call_" + ix;
                    call["type"] = "function";
                    call["function"] = fn;
                    toolCallsOut.Add(call);
                }
            }

            LlmResp r = new LlmResp();
            r.Content = finalContent.ToString();
            r.ToolCalls = (toolCallsOut != null && toolCallsOut.Count > 0) ? toolCallsOut : null;
            return r;
        }

        class StreamToolAcc
        {
            public string Id = "";
            public string Name = "";
            public StringBuilder Args = new StringBuilder();
        }

        // ストリーミング付きエージェント実行。emit(イベント名, dataJSON) で SSE を配信。
        // イベント: content(応答トークン) / tool(ツール実行) / toolend / done(完了)
        public static void RunAgentStream(Config cfg, SessionCtx ctx, string userMessage,
                                          Action<string, string> emit, Action<SessionCtx> save)
        {
            ctx.Messages.Add(Msg("user", userMessage));
            AgentProgress.Clear();
            AgentControl.Reset();
            MaybeCompact(cfg, ctx);
            if (save != null) save(ctx);

            try
            {
                for (int turn = 0; turn < MAX_TURNS; turn++)
                {
                    if (AgentControl.CancelRequested)
                    {
                        ctx.Messages.Add(Msg("assistant", "⚠ 処理を中断しました。"));
                        if (emit != null) emit("done", "{\"stopped\":true}");
                        return;
                    }

                    List<object> eff = BuildEffectiveMessages(cfg, ctx);
                    List<object> toolCallsOut = new List<object>();
                    LlmResp resp = CallOnceStream(cfg, eff, cfg.AllowTools,
                        delegate (string d)
                        {
                            if (emit != null) emit("content", "{ \"t\" : \"" + Escape(d) + "\" }");
                        },
                        toolCallsOut);
                    if (resp.TotalTokens > 0) ctx.LastTotalTokens = resp.TotalTokens;

                    if (resp.ToolCalls != null && resp.ToolCalls.Count > 0)
                    {
                        ctx.Messages.Add(AsstWithToolCalls(resp));
                        int n = Math.Min(resp.ToolCalls.Count, cfg.MaxToolsPerTurn);
                        for (int k = 0; k < n; k++)
                        {
                            if (AgentControl.CancelRequested) { return; }
                            Dictionary<string, object> call = resp.ToolCalls[k] as Dictionary<string, object>;
                            if (call == null) continue;
                            string id = Json.AsString(Get(call, "id")); if (id.Length == 0) id = "call_" + k;
                            call["id"] = id;
                            Dictionary<string, object> fn = Get(call, "function") as Dictionary<string, object>;
                            string name = (fn != null) ? Json.AsString(Get(fn, "name")) : "";
                            string argsJson = "{}";
                            if (fn != null)
                            {
                                object av = (fn.ContainsKey("arguments")) ? fn["arguments"] : null;
                                if (av is string) argsJson = (string)av;
                                else if (av != null) argsJson = Json.ToString(av);
                            }
                            if (name.Length == 0 && fn != null) name = ToolExecutor.InferName(fn, argsJson);
                            if (fn != null && name.Length > 0) fn["name"] = name;
                            AgentProgress.Add("ツール実行: " + name);
                            if (emit != null) emit("tool", "{ \"name\" : \"" + Escape(name) + "\" }");
                            string result;
                            try { result = ToolExecutor.Execute(name, argsJson, cfg); }
                            catch (Exception ex) { result = "{\"error\": \"" + Escape(ex.Message) + "\"}"; }
                            ctx.Messages.Add(ToolMsg(id, name, result));
                            if (save != null) save(ctx);
                        }
                        if (emit != null) emit("toolend", "{}");
                        continue;
                    }

                    string final = resp.Content;
                    ctx.Messages.Add(Msg("assistant", final));
                    if (emit != null) emit("done", "{ \"session\" : \"" + Escape(ctx.Id) + "\" }");
                    return;
                }
            }
            finally { }
            if (emit != null) emit("error", "{ \"message\" : \"tool loop exceeded\" }");
        }

        // 社内プロキシを適用する
        //  - LLM_PROXY_URL 設定時: そのプロキシを使用(認証ユーザー指定で credential 付与)
        //  - 未設定: LLM_USE_SYSTEM_PROXY=1 ならOSのシステムプロキシ、=0 なら直接接続
        static void ApplyProxy(HttpWebRequest request, Config cfg)
        {
            ProxyHelper.Apply(request, cfg);
        }

        static string ReadAll(Stream s)
        {
            using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }

        // レスポンスから content と tool_calls を取り出す
        static LlmResp ParseOnce(string responseBody)
        {
            object parsed = Json.Parse(responseBody);

            Dictionary<string, object> root = parsed as Dictionary<string, object>;
            if (root == null)
                throw new Exception("LLM API の応答形式が不正です");

            if (root.ContainsKey("error"))
            {
                object err = root["error"];
                Dictionary<string, object> ed = err as Dictionary<string, object>;
                if (ed != null && ed.ContainsKey("message"))
                    throw new Exception("LLM API エラー: " + Json.AsString(ed["message"]));
                throw new Exception("LLM API エラー: " + Json.ToString(err));
            }

            object chObj;
            if (!root.TryGetValue("choices", out chObj) || !(chObj is List<object>) || ((List<object>)chObj).Count == 0)
                throw new Exception("LLM API の応答に choices がありません");

            List<object> choices = (List<object>)chObj;
            Dictionary<string, object> first = choices[0] as Dictionary<string, object>;
            if (first == null || !first.ContainsKey("message"))
                throw new Exception("LLM API の応答に message がありません");

            Dictionary<string, object> msg = first["message"] as Dictionary<string, object>;
            if (msg == null)
                throw new Exception("LLM API の応答に message がありません");

            string finish = first.ContainsKey("finish_reason") ? Json.AsString(first["finish_reason"]) : "?";
            Console.WriteLine("[agent] finish_reason=" + finish + "  message_keys=" + string.Join(",", msg.Keys));

            LlmResp r = new LlmResp();
            r.Content = msg.ContainsKey("content") ? Json.AsString(msg["content"]) : "";

            // usage を取得(トークン実測。プロキシが返さなければ 0)
            if (root.ContainsKey("usage") && root["usage"] is Dictionary<string, object>)
            {
                Dictionary<string, object> u = (Dictionary<string, object>)root["usage"];
                r.TotalTokens = u.ContainsKey("total_tokens") ? Json.AsInt(u["total_tokens"]) : 0;
                r.PromptTokens = u.ContainsKey("prompt_tokens") ? Json.AsInt(u["prompt_tokens"]) : 0;
            }

            // 標準: message.tool_calls (配列)
            if (msg.ContainsKey("tool_calls") && msg["tool_calls"] is List<object>)
            {
                r.ToolCalls = (List<object>)msg["tool_calls"];
                // このプロキシは function.name を空で返すことがある。
                // 同じ message の legacy function_call にある名前で補完する。
                if (msg.ContainsKey("function_call") && msg["function_call"] is Dictionary<string, object>)
                {
                    string fnName = Json.AsString(Get((Dictionary<string, object>)msg["function_call"], "name"));
                    if (fnName != null && fnName.Trim().Length > 0)
                    {
                        foreach (object o in r.ToolCalls)
                        {
                            Dictionary<string, object> c = o as Dictionary<string, object>;
                            Dictionary<string, object> f = (c == null) ? null : (Get(c, "function") as Dictionary<string, object>);
                            if (f == null) continue;
                            string n = Json.AsString(Get(f, "name"));
                            if (n == null || n.Trim().Length == 0)
                                f["name"] = fnName;
                        }
                    }
                }
            }
            // 旧形式: message.function_call (単一) にも対応
            else if (msg.ContainsKey("function_call") && msg["function_call"] is Dictionary<string, object>)
            {
                Dictionary<string, object> fc = (Dictionary<string, object>)msg["function_call"];
                Dictionary<string, object> fn = new Dictionary<string, object>();
                fn["name"] = Json.AsString(Get(fc, "name"));
                object av = (fc.ContainsKey("arguments")) ? fc["arguments"] : null;
                fn["arguments"] = (av == null) ? "{}" : (av is string ? (string)av : Json.ToString(av));

                Dictionary<string, object> call = new Dictionary<string, object>();
                call["id"] = "call_0";
                call["type"] = "function";
                call["function"] = fn;

                List<object> calls = new List<object>();
                calls.Add(call);
                r.ToolCalls = calls;
            }
            return r;
        }
    }

    class LlmResp
    {
        public string Content;
        public List<object> ToolCalls;     // Dictionary<string,object> の List
        public int TotalTokens;            // usage.total_tokens (プロキシが返せば)
        public int PromptTokens;           // usage.prompt_tokens
    }

    // ------------------------------------------------------------------------
    //  ツール実行器。
    //  注意: ローカルホスト専用の自分用サーバであることを前提に、任意の
    //  コマンド実行を許可しています。ALLOW_TOOLS=0 で無効化できます。
    // ------------------------------------------------------------------------
    // プロキシ適用の共通ヘルパー (LLM 通信と curl ツールの両方で使用)
    static class ProxyHelper
    {
        public static void Apply(HttpWebRequest request, Config cfg)
        {
            if (cfg.ProxyUrl != null && cfg.ProxyUrl.Trim().Length > 0)
            {
                WebProxy p = new WebProxy(cfg.ProxyUrl.Trim());
                if (cfg.ProxyUser != null && cfg.ProxyUser.Length > 0)
                    p.Credentials = new NetworkCredential(cfg.ProxyUser, cfg.ProxyPass);
                else if (cfg.ProxyPass != null && cfg.ProxyPass.Length > 0)
                    p.Credentials = new NetworkCredential("", cfg.ProxyPass);
                else
                    p.UseDefaultCredentials = true; // 社内(Windows統合認証)プロキシ向け
                request.Proxy = p;
            }
            else if (!cfg.UseSystemProxy)
            {
                request.Proxy = null; // 直接接続(システムプロキシを無視)
            }
            // それ以外は既定(システムプロキシ)のまま
        }
    }

    static class ToolExecutor
    {
        const int MAX_OUTPUT = 32000; // ツール結果として LLM に返す最大文字数

        static string NormalizeName(string name)
        {
            if (name == null) return "";
            string n = name.Trim().ToLowerInvariant();
            switch (n)
            {
                case "shell": case "cmd": case "execute": case "run": case "exec":
                case "run_command": case "execute_command": case "command":
                    return "shell";
                case "read_file": case "read": case "cat": case "readfile":
                    return "read_file";
                case "write_file": case "write": case "writefile":
                    return "write_file";
                case "list_dir": case "ls": case "dir": case "list_directory":
                case "listdir":
                    return "list_dir";
                case "curl": case "http_request": case "http": case "fetch":
                    return "curl";
                default:
                    return name; // 未知の名前はそのまま(下で unknown tool)
            }
        }

        // プロキシが function.name を返さない場合に、引数の形からツール名を推定する。
        public static string InferName(Dictionary<string, object> fn, string argsJson)
        {
            if (fn != null && fn.ContainsKey("name"))
            {
                string nm = Json.AsString(fn["name"]);
                if (nm != null && nm.Trim().Length > 0)
                    return NormalizeName(nm);
            }
            // 引数を解釈して形から推定
            Dictionary<string, object> args = null;
            try
            {
                object o = Json.Parse(argsJson);
                args = o as Dictionary<string, object>;
            }
            catch { }
            if (args == null) args = new Dictionary<string, object>();

            if (args.ContainsKey("cmd")) return "shell";
            if (args.ContainsKey("content") && args.ContainsKey("path")) return "write_file";
            if (args.ContainsKey("pattern")) return "list_dir";
            if (args.ContainsKey("path")) return "read_file";
            return "shell"; // どれにも当てはまらなければ shell と仮定(ローカル専用のため)
        }

        public static string Execute(string name, string argsJson, Config cfg)
        {
            Dictionary<string, object> args = null;
            try
            {
                object o = Json.Parse(argsJson);
                args = o as Dictionary<string, object>;
            }
            catch { /* 引数が JSON でなければ空扱い */ }
            if (args == null) args = new Dictionary<string, object>();

            // 名前が空なら引数から推定
            if (name == null || name.Trim().Length == 0)
                name = InferName(null, argsJson);
            name = NormalizeName(name);

            switch (name)
            {
                case "shell":
                    return Shell(Json.AsString(Get(args, "cmd")), cfg.ToolTimeoutSec);
                case "read_file":
                    return ReadFile(Json.AsString(Get(args, "path")));
                case "write_file":
                    return WriteFile(Json.AsString(Get(args, "path")), Json.AsString(Get(args, "content")));
                case "list_dir":
                    return ListDir(Json.AsString(Get(args, "path")), Json.AsString(Get(args, "pattern")));
                case "curl":
                    return Curl(cfg,
                        Json.AsString(Get(args, "url")),
                        Json.AsString(Get(args, "method")),
                        args.ContainsKey("headers") ? args["headers"] : null,
                        Json.AsString(Get(args, "body")));
                default:
                    Console.WriteLine("[agent] !! unknown tool name='" + name + "' args=" + argsJson);
                    return "{\"error\": \"unknown tool: " + name + " (利用可能: shell/read_file/write_file/list_dir)\"}";
            }
        }

        static object Get(Dictionary<string, object> d, string key)
        {
            if (d.ContainsKey(key)) return d[key];
            return null;
        }

        public static List<object> Definitions()
        {
            List<object> tools = new List<object>();
            tools.Add(Func("shell",
                "Windows ホスト上でコマンドプロンプト(cmd.exe)のコマンドを実行します。"
                + " 標準出力・標準エラー・終了コードを返します。"
                + " ファイル/ディレクトリ操作やプログラム実行など任意のシェルコマンドに使えます。",
                new string[] { "cmd" },
                new string[][] {
                    new string[] { "cmd", "string", "実行するコマンド文字列(例: dir /b)" }
                }));
            tools.Add(Func("read_file",
                "指定したファイルの内容をテキストで読み取ります。",
                new string[] { "path" },
                new string[][] {
                    new string[] { "path", "string", "読み取るファイルの絶対パスまたは相対パス" }
                }));
            tools.Add(Func("write_file",
                "指定したパスにテキスト内容を書き込みます(上書き)。新規作成にも使用。",
                new string[] { "path", "content" },
                new string[][] {
                    new string[] { "path", "string", "書き込むファイルのパス" },
                    new string[] { "content", "string", "書き込む内容" }
                }));
            tools.Add(Func("list_dir",
                "指定ディレクトリのエントリ(ファイル/フォルダ)一覧を返します。",
                new string[] { "path" },
                new string[][] {
                    new string[] { "path", "string", "一覧を取得するディレクトリのパス" },
                    new string[] { "pattern", "string", "任意。ワイルドカードフィルタ(例: *.cs)。省略可" }
                }));
            tools.Add(Func("curl",
                "HTTP/HTTPS リクエストを送るツール(curl 等価)。指定URLへ GET/POST 等を送り、ステータスコードとレスポンス本文を返します。"
                + " 社内プロキシが設定(LLM_PROXY_URL)されていれば、自動でそのプロキシを経由します。",
                new string[] { "url" },
                new string[][] {
                    new string[] { "url", "string", "リクエスト先の完全 URL(例: https://example.com/api)" },
                    new string[] { "method", "string", "HTTPメソッド(GET/POST/PUT/DELETE等)。省略時は GET(ボディがあれば POST)" },
                    new string[] { "headers", "object", "追加ヘッダーの JSON オブジェクト(例: {\"Authorization\": \"Bearer xyz\"})" },
                    new string[] { "body", "string", "リクエストボディ文字列(JSON 等)。省略可" }
                }));
            return tools;
        }

        static Dictionary<string, object> Func(string name, string desc, string[] required, string[][] props)
        {
            Dictionary<string, object> parameters = new Dictionary<string, object>();
            parameters["type"] = "object";
            Dictionary<string, object> properties = new Dictionary<string, object>();
            foreach (string[] p in props)
            {
                Dictionary<string, object> pd = new Dictionary<string, object>();
                pd["type"] = p[1];
                pd["description"] = p[2];
                properties[p[0]] = pd;
            }
            parameters["properties"] = properties;
            parameters["required"] = new List<object>(required);

            Dictionary<string, object> fn = new Dictionary<string, object>();
            fn["name"] = name;
            fn["description"] = desc;
            fn["parameters"] = parameters;

            Dictionary<string, object> tool = new Dictionary<string, object>();
            tool["type"] = "function";
            tool["function"] = fn;
            return tool;
        }

        // ---- 実装 ----

        static string Ok(Dictionary<string, object> d)
        {
            d["ok"] = true;
            return Json.ToString(d);
        }
        static string Err(string message)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = false;
            d["error"] = message;
            return Json.ToString(d);
        }

        static string Shell(string cmd, int timeoutSec)
        {
            if (cmd == null || cmd.Trim().Length == 0) return Err("cmd が空です");
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c " + cmd);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                // エンコーディングは固定せず生バイトを採取し、自動判定する
                psi.StandardOutputEncoding = null;

                Process p = Process.Start(psi);
                System.Threading.Tasks.Task<byte[]> so = CopyToBytesAsync(p.StandardOutput.BaseStream);
                System.Threading.Tasks.Task<byte[]> se = CopyToBytesAsync(p.StandardError.BaseStream);

                if (!p.WaitForExit(timeoutSec * 1000))
                {
                    try { p.Kill(); } catch { }
                    return Err("実行がタイムアウトしました(" + timeoutSec + "秒): " + cmd);
                }
                p.WaitForExit();
                string stdout = DecodeBytes(so.Result);
                string stderr = DecodeBytes(se.Result);

                Dictionary<string, object> d = new Dictionary<string, object>();
                d["ok"] = true;
                d["exitCode"] = p.ExitCode;
                d["stdout"] = Clip(stdout);
                d["stderr"] = Clip(stderr);
                return Json.ToString(d);
            }
            catch (Exception ex)
            {
                return Err("shell 実行失敗: " + ex.Message);
            }
        }

        static string ReadFile(string path)
        {
            if (path == null || path.Length == 0) return Err("path が指定されていません");
            try
            {
                if (!File.Exists(path)) return Err("ファイルが存在しません: " + path);
                string txt = DecodeBytes(File.ReadAllBytes(path));
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["ok"] = true;
                d["content"] = Clip(txt);
                return Json.ToString(d);
            }
            catch (Exception ex)
            {
                return Err("read_file 失敗: " + ex.Message);
            }
        }

        static string WriteFile(string path, string content)
        {
            if (path == null || path.Length == 0) return Err("path が指定されていません");
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (dir != null && dir.Length > 0 && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, content == null ? "" : content, Encoding.UTF8);
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["ok"] = true;
                d["written"] = (content == null ? "" : content).Length;
                d["path"] = Path.GetFullPath(path);
                return Json.ToString(d);
            }
            catch (Exception ex)
            {
                return Err("write_file 失敗: " + ex.Message);
            }
        }

        static string ListDir(string path, string pattern)
        {
            if (path == null || path.Length == 0) path = ".";
            if (pattern == null || pattern.Length == 0) pattern = "*";
            try
            {
                List<object> entries = new List<object>();
                List<string> lines = new List<string>();
                string[] dirs = Directory.GetDirectories(path);
                foreach (string den in dirs)
                {
                    string name = Path.GetFileName(den);
                    lines.Add("[DIR ]  " + name + "/");
                    Dictionary<string, object> e = new Dictionary<string, object>();
                    e["type"] = "dir"; e["name"] = name; e["path"] = den;
                    entries.Add(e);
                }
                string[] files = Directory.GetFiles(path, pattern);
                foreach (string f in files)
                {
                    string name = Path.GetFileName(f);
                    long size = new FileInfo(f).Length;
                    lines.Add("[FILE]  " + name + "  " + HumanSize(size));
                    Dictionary<string, object> e = new Dictionary<string, object>();
                    e["type"] = "file"; e["name"] = name; e["size"] = size; e["path"] = f;
                    entries.Add(e);
                }

                string list = "";
                foreach (string l in lines)
                    list += l + "\n";

                Dictionary<string, object> d = new Dictionary<string, object>();
                d["ok"] = true;
                d["path"] = Path.GetFullPath(path);
                d["total"] = dirs.Length + files.Length;
                d["list"] = list;   // 読みやすいテキスト一覧
                d["entries"] = entries; // 構造化データ
                return Json.ToString(d);
            }
            catch (Exception ex)
            {
                return Err("list_dir 失敗: " + ex.Message);
            }
        }

        static string HumanSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.##") + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.##") + " GB";
        }

        // curl 等価: 設定済みプロキシ(LLM_PROXY_URL)を経由して HTTP/HTTPS 通信
        static string Curl(Config cfg, string url, string method, object headersObj, string body)
        {
            if (url == null || url.Trim().Length == 0) return Err("url が指定されていません");
            if (method == null || method.Trim().Length == 0)
                method = (body != null && body.Length > 0) ? "POST" : "GET";
            method = method.Trim().ToUpperInvariant();

            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url.Trim());
                ProxyHelper.Apply(request, cfg); // 社内プロキシ設定があれば使用
                request.Method = method;
                request.Timeout = cfg.ToolTimeoutSec * 1000;
                request.ReadWriteTimeout = cfg.ToolTimeoutSec * 1000;

                Dictionary<string, object> headers = headersObj as Dictionary<string, object>;
                if (headers != null)
                {
                    foreach (KeyValuePair<string, object> kv in headers)
                    {
                        string k = kv.Key;
                        string v = Json.AsString(kv.Value);
                        if (String.Equals(k, "Content-Type", StringComparison.OrdinalIgnoreCase)) request.ContentType = v;
                        else if (String.Equals(k, "Accept", StringComparison.OrdinalIgnoreCase)) request.Accept = v;
                        else if (String.Equals(k, "User-Agent", StringComparison.OrdinalIgnoreCase)) request.UserAgent = v;
                        else request.Headers[k] = v;
                    }
                }

                if (body != null && body.Length > 0)
                {
                    if (request.ContentType == null)
                        request.ContentType = "application/json";
                    byte[] data = Encoding.UTF8.GetBytes(body);
                    request.ContentLength = data.Length;
                    using (Stream s = request.GetRequestStream())
                        s.Write(data, 0, data.Length);
                }

                HttpStatusCode status;
                string respBody;
                try
                {
                    using (HttpWebResponse resp = (HttpWebResponse)request.GetResponse())
                    {
                        status = resp.StatusCode;
                        using (Stream rs = resp.GetResponseStream())
                            respBody = DecodeBytes(ReadAllBytes(rs));
                    }
                }
                catch (WebException wex)
                {
                    if (wex.Response != null)
                    {
                        try
                        {
                            using (Stream rs = wex.Response.GetResponseStream())
                                respBody = DecodeBytes(ReadAllBytes(rs));
                            status = ((HttpWebResponse)wex.Response).StatusCode;
                        }
                        catch { return Err("curl 通信エラー: " + wex.Message); }
                    }
                    else
                    {
                        return Err("curl 通信エラー: " + wex.Message);
                    }
                }

                Dictionary<string, object> d = new Dictionary<string, object>();
                d["ok"] = true;
                d["status"] = (int)status;
                d["url"] = url.Trim();
                d["body"] = Clip(respBody);
                return Json.ToString(d);
            }
            catch (Exception ex)
            {
                return Err("curl 失敗: " + ex.Message);
            }
        }

        static string ReadAll(Stream s)
        {
            using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }

        // ---- 文字コード自動判定 ----
        // BOM(UTF-8/16) -> 妥当なUTF-8 -> コンソール/システム既定(例: CP932) -> Latin-1
        static string DecodeBytes(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            try
            {
                if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                    return new UTF8Encoding(false).GetString(data, 3, data.Length - 3);
                if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
                    return Encoding.Unicode.GetString(data, 2, data.Length - 2);
                if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
                    return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
            }
            catch { }
            try
            {
                var u8 = new UTF8Encoding(false, true);
                return u8.GetString(data);
            }
            catch (DecoderFallbackException) { }
            try
            {
                int cp = Console.OutputEncoding.CodePage;
                if (cp > 0 && cp != 65001)
                    return Encoding.GetEncoding(cp).GetString(data);
            }
            catch { }
            try { return Encoding.Default.GetString(data); } catch { }
            try { return Encoding.GetEncoding(28591).GetString(data); } catch { }
            return Encoding.ASCII.GetString(data);
        }

        static System.Threading.Tasks.Task<byte[]> CopyToBytesAsync(Stream s)
        {
            return System.Threading.Tasks.Task.Run(delegate()
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            });
        }

        static byte[] ReadAllBytes(Stream s)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        static string Clip(string s)
        {
            if (s == null) return "";
            if (s.Length > MAX_OUTPUT)
                s = s.Substring(0, MAX_OUTPUT) + "\n...[出力が長いため省略]";
            return s;
        }
    }

    // ------------------------------------------------------------------------
    //  最小 JSON パーサ / シリアライザ (外部ライブラリ不要の自己完結実装)
    //  parse   => Dictionary<string,object> / List<object> / string / double / bool / null
    //  ToString => JSON 文字列
    // ------------------------------------------------------------------------
    static class Json
    {
        public static object Parse(string s)
        {
            int i = 0;
            object v = ParseValue(s, ref i);
            SkipWs(s, ref i);
            return v;
        }

        static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new Exception("unexpected end of JSON");
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': ParseLiteral(s, ref i, "true"); return true;
                case 'f': ParseLiteral(s, ref i, "false"); return false;
                case 'n': ParseLiteral(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && Char.IsWhiteSpace(s[i])) i++;
        }

        static void ParseLiteral(string s, ref int i, string lit)
        {
            if (i + lit.Length > s.Length || s.Substring(i, lit.Length) != lit)
                throw new Exception("invalid literal");
            i += lit.Length;
        }

        static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}')
            {
                i++; return d;
            }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"')
                    throw new Exception("expected string key");
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':')
                    throw new Exception("expected ':'");
                i++;
                object val = ParseValue(s, ref i);
                d[key] = val;
                SkipWs(s, ref i);
                if (i >= s.Length) throw new Exception("unexpected end");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; break; }
                throw new Exception("expected ',' or '}'");
            }
            return d;
        }

        static List<object> ParseArray(string s, ref int i)
        {
            List<object> l = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']')
            {
                i++; return l;
            }
            while (true)
            {
                object val = ParseValue(s, ref i);
                l.Add(val);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new Exception("unexpected end");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; break; }
                throw new Exception("expected ',' or ']'");
            }
            return l;
        }

        static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"') { i++; return sb.ToString(); }
                if (c == '\\')
                {
                    i++;
                    if (i >= s.Length) break;
                    char e = s[i];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 < s.Length)
                            {
                                string hex = s.Substring(i + 1, 4);
                                int code;
                                if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                {
                                    sb.Append((char)code);
                                    i += 4;
                                }
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                    i++;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            throw new Exception("unterminated string");
        }

        static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if (Char.IsDigit(c) || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E')
                    i++;
                else break;
            }
            string num = s.Substring(start, i - start);
            double d;
            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                return d;
            return num;
        }

        public static string ToString(object o)
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, o);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object o)
        {
            if (o == null) { sb.Append("null"); return; }
            if (o is string) { WriteString(sb, (string)o); return; }
            if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
            if (o is double) { sb.Append(((double)o).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (o is decimal) { sb.Append(((decimal)o).ToString(CultureInfo.InvariantCulture)); return; }
            if (o is int) { sb.Append(((int)o).ToString(CultureInfo.InvariantCulture)); return; }
            if (o is long) { sb.Append(((long)o).ToString(CultureInfo.InvariantCulture)); return; }
            if (o is float) { sb.Append(((float)o).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (o is Dictionary<string, object>)
            {
                Dictionary<string, object> d = (Dictionary<string, object>)o;
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in d)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    Write(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            if (o is List<object>)
            {
                List<object> l = (List<object>)o;
                sb.Append('[');
                for (int x = 0; x < l.Count; x++)
                {
                    if (x > 0) sb.Append(',');
                    Write(sb, l[x]);
                }
                sb.Append(']');
                return;
            }
            WriteString(sb, o.ToString());
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u" + ((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public static string AsString(object o)
        {
            if (o == null) return "";
            if (o is string) return (string)o;
            return o.ToString();
        }

        public static int AsInt(object o)
        {
            if (o == null) return 0;
            if (o is double) return (int)(double)o;
            if (o is int) return (int)o;
            if (o is long) return (int)(long)o;
            if (o is float) return (int)(float)o;
            string t = o.ToString();
            int v;
            if (int.TryParse(t, out v)) return v;
            return 0;
        }
    }
}
