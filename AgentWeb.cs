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

        // 設定ファイル(agentweb.json)から上書き適用する。
        // 保存先はセッションと同じ %APPDATA%\agentweb\agentweb.json。
        // 旧配置(実行ディレクトリ)にしか無い場合は一度だけ移行する。
        // 優先度: 環境変数 < 設定ファイル
        void ApplyConfigFile()
        {
            string path = ConfigPath;
            if (path == null || path.Trim().Length == 0)
            {
                path = DefaultConfigPath();
                try
                {
                    string oldPath = Path.Combine(AppDir(), "agentweb.json");
                    if (!File.Exists(path) && File.Exists(oldPath))
                    {
                        string dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        File.Copy(oldPath, path, true);
                        Console.WriteLine("[config] 設定ファイルを移行しました: " + oldPath + " → " + path);
                    }
                }
                catch { }
            }
            ConfigPath = path;               // ファイルが無くても保存先として確定しておく
            if (!File.Exists(path)) return;

            try
            {
                object o = Json.Parse(File.ReadAllText(path, Encoding.UTF8));
                ApplyDict(o as Dictionary<string, object>);
            }
            catch { }
        }

        // 辞書(dict)から設定を適用する。設定ファイル読込と Web UI からの保存で共用。
        public void ApplyDict(Dictionary<string, object> m)
        {
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
            if (Has(m, "emitReasoning")) EmitReasoning = AsBool(Get(m, "emitReasoning"));
        }

        // 現在の設定を JSON 用の辞書にする(Web UI / 設定ファイル保存で共用)
        public Dictionary<string, object> ToJsonDict()
        {
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["port"] = Port;
            m["llmApiUrl"] = ApiBaseUrl;
            m["llmApiKey"] = SecretKey;
            m["model"] = Model;
            m["allowTools"] = AllowTools;
            m["emitReasoning"] = EmitReasoning;
            m["maxToolsPerTurn"] = MaxToolsPerTurn;
            m["toolTimeoutSec"] = ToolTimeoutSec;
            m["contextWindow"] = ContextWindow;
            m["reserveTokens"] = ReserveTokens;
            m["keepRecentTokens"] = KeepRecentTokens;
            m["useSystemProxy"] = UseSystemProxy;
            m["proxyUrl"] = ProxyUrl;
            m["proxyUser"] = ProxyUser;
            m["proxyPass"] = ProxyPass;
            m["systemPrompt"] = SystemPrompt;
            m["bindPath"] = BindPath;
            m["openBrowserOnStart"] = OpenBrowserOnStart;
            return m;
        }

        // 現在の設定を設定ファイルへ保存する(Web UI から呼ぶ)
        public void Save()
        {
            string path = ConfigPath;
            if (path == null || path.Trim().Length == 0) path = DefaultConfigPath();
            ConfigPath = path;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, Json.ToString(ToJsonDict()), new UTF8Encoding(false));
        }

        // 設定・セッションの保存先 (%APPDATA%\agentweb)
        public static string AppDataDir()
        {
            string d = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (d == null || d.Length == 0) d = Path.GetTempPath();
            return Path.Combine(d, "agentweb");
        }

        // 既定の設定ファイルパス (%APPDATA%\agentweb\agentweb.json)
        public static string DefaultConfigPath()
        {
            return Path.Combine(AppDataDir(), "agentweb.json");
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
                return ServeIndexHtml();
            if (req.Method == "GET" && path.StartsWith("/static/"))
                return StaticFile(path.Substring("/static/".Length));
            if (req.Method == "GET" && path == "/favicon.ico")
                return RawResponse(204, new byte[0], "image/x-icon");
            if (req.Method == "GET" && path == "/api/health")
                return HandleHealth();
            if (req.Method == "GET" && path == "/api/config")
                return HandleGetConfig();
            if (req.Method == "POST" && path == "/api/config")
                return HandleSetConfig(req);
            if (req.Method == "GET" && path == "/api/tools")
                return HandleTools();
            if (req.Method == "GET" && path == "/api/diag/tools")
                return HandleDiagTools();
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
            if (req.Method == "POST" && path.StartsWith("/api/sessions/") && path.EndsWith("/autotitle"))
            {
                string aid = path.Substring("/api/sessions/".Length);
                if (aid.EndsWith("/autotitle")) aid = aid.Substring(0, aid.Length - "/autotitle".Length);
                return HandleAutoTitle(aid);
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

        // Web UI は外部ファイル index.html をその都度読み込んで返す（埋め込みなし）。
        HttpResponse ServeIndexHtml()
        {
            string full = Path.Combine(config.BindPath, "index.html");
            try
            {
                if (File.Exists(full))
                {
                    byte[] data = File.ReadAllBytes(full);
                    return RawResponse(200, data, "text/html; charset=utf-8");
                }
            }
            catch (Exception ex)
            {
                return TextResponse(500, "index.html read error: " + ex.Message, "text/plain; charset=utf-8");
            }
            return TextResponse(500,
                "index.html が見つかりません。aw.exe と同じフォルダに index.html を置いてください。\n検索パス: " + full,
                "text/plain; charset=utf-8");
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
        // 設定を返す (Web UI の設定画面用)
        HttpResponse HandleGetConfig()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = true;
            d["config"] = config.ToJsonDict();
            d["path"] = config.ConfigPath;
            return JsonResponse(200, d);
        }

        // 設定を保存する (Web UI の設定画面用)。保存後は即時反映される。
        HttpResponse HandleSetConfig(HttpRequest req)
        {
            if (req.Body != null && req.Body.Length > 0)
            {
                try
                {
                    object o = Json.Parse(Encoding.UTF8.GetString(req.Body));
                    Dictionary<string, object> m = o as Dictionary<string, object>;
                    if (m != null) config.ApplyDict(m);
                }
                catch (Exception ex)
                {
                    Dictionary<string, object> e = new Dictionary<string, object>();
                    e["ok"] = false;
                    e["message"] = "設定の解析に失敗しました: " + ex.Message;
                    return JsonResponse(400, e);
                }
            }
            try { config.Save(); }
            catch (Exception ex)
            {
                Dictionary<string, object> e = new Dictionary<string, object>();
                e["ok"] = false;
                e["message"] = "設定の保存に失敗しました: " + ex.Message;
                return JsonResponse(500, e);
            }
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = true;
            d["config"] = config.ToJsonDict();
            d["path"] = config.ConfigPath;
            return JsonResponse(200, d);
        }

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

        // ---------------- /api/diag/tools (モデルの function calling 対応確認) ----------------
        HttpResponse HandleDiagTools()
        {
            try { return JsonResponse(200, LlmClient.ProbeTools(config)); }
            catch (Exception ex) { return JsonResponse(502, MakeError("llm_error", "確認に失敗しました: " + ex.Message)); }
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
            try { config.Save(); } catch { }   // 設定ファイルにも保存
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
                    if (m.ContainsKey("steps")) mv["steps"] = m["steps"];   // ツール実行の記録
                    if (m.ContainsKey("think")) mv["think"] = m["think"];   // 思考(reasoning)の記録
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

        // ---------------- セッション名の自動設定 ----------------
        HttpResponse HandleAutoTitle(string id)
        {
            if (busy)
                return JsonResponse(409, MakeError("busy", "応答中は名前を自動設定できません"));

            List<object> messages = null;
            string summary = "";
            int ctxStart = 0;
            lock (sessionLock)
            {
                Dictionary<string, object> meta = store.Load(id);
                if (meta == null)
                    return JsonResponse(404, MakeError("not_found", "セッションが見つかりません"));
                summary = meta.ContainsKey("summary") ? Json.AsString(meta["summary"]) : "";
                ctxStart = meta.ContainsKey("contextStart") ? Json.AsInt(meta["contextStart"]) : 0;
                object ms = meta.ContainsKey("messages") ? meta["messages"] : null;
                if (ms is List<object>) messages = (List<object>)ms;
            }

            if (messages == null || messages.Count == 0)
                return JsonResponse(400, MakeError("empty", "会話がまだありません"));

            string source = TitleSourceText(messages, summary);
            string sys = "あなたは会話ログに短いタイトルを付けるアシスタントです。"
                       + "会話全体の主題を表す簡潔なタイトルを1つだけ出力してください。"
                       + "出力はタイトル文字列のみとし、説明・前置き・引用符・句点・コードブロックは禁止です。"
                       + "日本語で20文字以内を目安にしてください。";

            string title;
            try { title = LlmClient.CompleteText(config, sys, source); }
            catch (Exception ex) { return JsonResponse(502, MakeError("llm_error", "タイトル生成に失敗しました: " + ex.Message)); }

            title = SanitizeTitle(title);
            if (title.Length == 0)
                return JsonResponse(502, MakeError("llm_error", "タイトルを生成できませんでした"));

            lock (sessionLock)
            {
                store.Save(id, messages, title, summary, ctxStart);
            }

            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = true;
            d["session"] = id;
            d["title"] = title;
            return JsonResponse(200, d);
        }

        // タイトル生成用に会話ログを短くまとめる(直近 12 件 + 要約)
        static string TitleSourceText(List<object> messages, string summary)
        {
            StringBuilder sb = new StringBuilder();
            if (summary != null && summary.Trim().Length > 0)
                sb.Append("これまでの要約:\n").Append(summary.Trim()).Append("\n\n");
            int start = Math.Max(0, messages.Count - 12);
            for (int i = start; i < messages.Count; i++)
            {
                Dictionary<string, object> m = messages[i] as Dictionary<string, object>;
                if (m == null) continue;
                string role = m.ContainsKey("role") ? Json.AsString(m["role"]) : "";
                string content = m.ContainsKey("content") ? Json.AsString(m["content"]) : "";
                if (content.Trim().Length == 0) continue;
                string label = (role == "user") ? "ユーザー" : ((role == "assistant") ? "アシスタント" : "ツール");
                string c = content.Trim();
                if (c.Length > 400) c = c.Substring(0, 400) + "…";
                sb.Append(label).Append(": ").Append(c).Append("\n");
            }
            return sb.ToString();
        }

        // LLM 出力をタイトル文字列に整形する
        static string SanitizeTitle(string s)
        {
            if (s == null) return "";
            s = s.Replace("```", " ").Trim();
            int nl = s.IndexOf('\n');
            if (nl >= 0) s = s.Substring(0, nl);
            s = s.Trim();
            string[] prefixes = new string[] { "タイトル:", "タイトル：", "Title:", "title:" };
            for (int i = 0; i < prefixes.Length; i++)
            {
                if (s.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase))
                {
                    s = s.Substring(prefixes[i].Length).Trim();
                    break;
                }
            }
            s = s.Trim('"', '\'', '「', '」', '『', '』', '*', '#', ' ', '\t', '\r', '。', '：', ':');
            if (s.Length > 40) s = s.Substring(0, 40) + "…";
            return s;
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
            // 設定ファイルと同じ場所 (%APPDATA%\agentweb) の sessions 配下に置く
            sessionsDir = Path.Combine(Config.AppDataDir(), "sessions");
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
            List<object> turnSteps = new List<object>();   // このターンのツール実行記録
            StringBuilder turnThink = new StringBuilder(); // このターンの思考(reasoning_content)

            // トークン超過時は古い文脈を要約して置換(コンパクション)
            MaybeCompact(cfg, ctx);
            if (save != null) save(ctx); // 要約/開始位置を永続化

            try
            {
                for (int turn = 0; turn < MAX_TURNS; turn++)
                {
                    if (AgentControl.CancelRequested) return CancelReply(ctx, turnSteps, ThinkOf(cfg, turnThink));

                    List<object> eff = BuildEffectiveMessages(cfg, ctx);
                    LlmResp resp;
                    try
                    {
                        resp = CallOnce(cfg, eff, cfg.AllowTools);
                    }
                    catch (AgentCancelledException)
                    {
                        return CancelReply(ctx, turnSteps, ThinkOf(cfg, turnThink));
                    }
                    if (resp.TotalTokens > 0) ctx.LastTotalTokens = resp.TotalTokens;
                    if (cfg.EmitReasoning && resp.Reasoning != null && resp.Reasoning.Length > 0)
                        turnThink.Append(resp.Reasoning);

                    if (resp.ToolCalls != null && resp.ToolCalls.Count > 0)
                    {
                        // assistant の tool_calls メッセージを完全履歴に保存
                        ctx.Messages.Add(AsstWithToolCalls(resp));

                        int n = Math.Min(resp.ToolCalls.Count, cfg.MaxToolsPerTurn);
                        for (int k = 0; k < n; k++)
                        {
                            if (AgentControl.CancelRequested) return CancelReply(ctx, turnSteps, ThinkOf(cfg, turnThink));

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

                            string ndetail = ToolDetail(name, argsJson);
                            AddStep(turnSteps, "ツール実行: " + (name.Length > 0 ? name : "?") + (ndetail.Length > 0 ? ("  " + ndetail) : ""));
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
                    ctx.Messages.Add(Msg("assistant", final, turnSteps, ThinkOf(cfg, turnThink)));
                    return final;
                }
                throw new Exception("LLM が最終回答を出せませんでした(ツール反復が多すぎます)");
            }
            finally
            {
                if (save != null) save(ctx);
            }
        }

        // 蓄積した思考を、保存する文字列に変換する(未設定・EmitReasoning 無効なら null)
        static string ThinkOf(Config cfg, StringBuilder sb)
        {
            if (cfg == null || !cfg.EmitReasoning || sb == null || sb.Length == 0) return null;
            return sb.ToString();
        }

        static string CancelReply(SessionCtx ctx, List<object> steps)
        {
            return CancelReply(ctx, steps, null);
        }

        static string CancelReply(SessionCtx ctx, List<object> steps, string think)
        {
            string msg = "⚠ 処理を中断しました。";
            ctx.Messages.Add(Msg("assistant", msg, steps, think));
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

        // ツール実行の記録を添えたメッセージ(UI でリロード後も表示できるよう保存する)
        static Dictionary<string, object> Msg(string role, string content, List<object> steps)
        {
            return Msg(role, content, steps, null);
        }

        // 思考(reasoning_content)も添えたメッセージ。
        // steps と同じく保存専用メタデータで、LLM API へは送らない。
        static Dictionary<string, object> Msg(string role, string content, List<object> steps, string think)
        {
            Dictionary<string, object> m = Msg(role, content, steps);
            if (think != null && think.Trim().Length > 0) m["think"] = think;
            return m;
        }

        // ツール実行の記録を、進捗ログとターン記録の両方へ追加する
        static void AddStep(List<object> steps, string s)
        {
            if (steps != null) steps.Add(s);
            AgentProgress.Add(s);
        }

        // 単発のテキスト生成(セッション名の自動生成など)を外部から呼ぶための公開ラッパ
        public static string CompleteText(Config cfg, string systemPrompt, string userText)
        {
            List<object> msgs = new List<object>();
            msgs.Add(Msg("system", systemPrompt));
            msgs.Add(Msg("user", userText));
            LlmResp r = CallOnce(cfg, msgs, false);
            return (r == null) ? "" : r.Content;
        }

        // モデルが function calling に対応しているかを確認する(ツールは実行しない)
        public static Dictionary<string, object> ProbeTools(Config cfg)
        {
            List<object> msgs = new List<object>();
            msgs.Add(Msg("system",
                "あなたはツールを利用できるエージェントです。依頼には必ずツールを呼び出して実行してください。"
                + " 本文だけを返してはいけません。"));
            msgs.Add(Msg("user", "shell ツールを使って `echo ok` を実行してください。"));
            LlmResp r = CallOnce(cfg, msgs, true);

            int n = (r == null || r.ToolCalls == null) ? 0 : r.ToolCalls.Count;
            List<object> names = new List<object>();
            if (n > 0)
            {
                foreach (object o in r.ToolCalls)
                {
                    Dictionary<string, object> c = o as Dictionary<string, object>;
                    Dictionary<string, object> fn = (c != null && c.ContainsKey("function"))
                        ? (c["function"] as Dictionary<string, object>) : null;
                    if (fn != null && fn.ContainsKey("name")) names.Add(Json.AsString(fn["name"]));
                }
            }
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = true;
            d["model"] = cfg.Model;
            d["supported"] = (n > 0);
            d["toolCalls"] = n;
            d["names"] = names;
            d["content"] = (r == null) ? "" : r.Content;
            return d;
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

        // JSON 文字列用のエスケープ(制御文字 < 0x20 も \u00XX にする)
        static string JsonEscape(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c == '\b') sb.Append("\\b");
                else if (c == '\f') sb.Append("\\f");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static string ArgStr(Dictionary<string, object> a, string k)
        {
            if (a != null && a.ContainsKey(k)) return Json.AsString(a[k]);
            return "";
        }

        // ツール引数から表示用の詳細(ファイル名/フルコマンド/URL等)を作る
        static string ToolDetail(string name, string argsJson)
        {
            if (argsJson == null || argsJson.Length == 0) return "";
            Dictionary<string, object> a = null;
            try { a = Json.Parse(argsJson) as Dictionary<string, object>; }
            catch { }
            if (a == null) return "";

            string cmd = ArgStr(a, "cmd");
            string path = ArgStr(a, "path");
            string pattern = ArgStr(a, "pattern");
            string url = ArgStr(a, "url");
            string method = ArgStr(a, "method");

            if (name == "shell") return cmd;
            if (name == "read_file" || name == "write_file" || name == "list_dir")
                return pattern.Length > 0 ? (path + "  [" + pattern + "]") : path;
            if (name == "curl") return (method.Length > 0 ? (method + " ") : "") + url;
            if (cmd.Length > 0) return cmd;
            if (path.Length > 0) return path;
            if (url.Length > 0) return url;
            return "";
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
                    if (kv.Key != "ts" && kv.Key != "steps" && kv.Key != "think") c[kv.Key] = kv.Value;   // 保存用メタデータは送らない
                apiMessages.Add(c);
            }
            body["messages"] = apiMessages;
            body["temperature"] = 0.7;
            if (useTools && cfg.AllowTools)
            {
                body["tools"] = ToolExecutor.Definitions();
                body["tool_choice"] = "auto";
            }

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
                                      Action<string> contentCb, Action<string> reasoningCb,
                                      List<object> toolCallsOut)
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
                    if (kv.Key != "ts" && kv.Key != "steps" && kv.Key != "think") c[kv.Key] = kv.Value;   // 保存用メタデータは送らない
                apiMessages.Add(c);
            }
            body["messages"] = apiMessages;
            body["temperature"] = 0.7;
            body["stream"] = true;
            if (useTools && cfg.AllowTools)
            {
                List<object> defs = ToolExecutor.Definitions();
                body["tools"] = defs;
                body["tool_choice"] = "auto";
                Console.WriteLine("[agent] stream: model=" + cfg.Model + " tools=on defs=" + defs.Count);
            }
            else
            {
                Console.WriteLine("[agent] stream: tools=off (allowTools=" + cfg.AllowTools + ")");
            }

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
                    bool loggedFirst = false;
                    string ctype = (resp.ContentType == null) ? "" : resp.ContentType;
                    if (ctype.IndexOf("event-stream", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        // プロキシ等が stream:true を無視して通常の JSON を返した場合
                        string bodyText = sr.ReadToEnd();
                        Console.WriteLine("[agent] stream: 非SSE応答(" + ctype + ") を JSON として解釈");
                        ParseNonStreamJson(bodyText, finalContent, acc, cfg, contentCb, reasoningCb);
                    }

                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (AgentControl.CancelRequested) { try { request.Abort(); } catch { } break; }
                        if (line.Length == 0) continue;
                        if (!line.StartsWith("data:")) continue;
                        string data = line.Substring(5).Trim();
                        if (data == "[DONE]") break;
                        if (!loggedFirst)
                        {
                            loggedFirst = true;
                            Console.WriteLine("[agent] stream first data: "
                                + (data.Length > 500 ? data.Substring(0, 500) + "..." : data));
                        }
                        object parsed;
                        try { parsed = Json.Parse(data); }
                        catch { continue; }
                        Dictionary<string, object> root = parsed as Dictionary<string, object>;
                        if (root == null || !root.ContainsKey("choices") || !(root["choices"] is List<object>)) continue;
                        List<object> choices = (List<object>)root["choices"];
                        if (choices.Count == 0) continue;
                        Dictionary<string, object> ch = choices[0] as Dictionary<string, object>;
                        if (ch == null || !ch.ContainsKey("delta")) continue;
                        if (ch.ContainsKey("finish_reason") && ch["finish_reason"] != null)
                            Console.WriteLine("[agent] finish_reason=" + Json.AsString(ch["finish_reason"]));
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
                        if (cfg.EmitReasoning)
                        {
                            string rt = DeltaText(delta, "reasoning_content");
                            if (rt.Length == 0) rt = DeltaText(delta, "reasoning");
                            if (rt.Length > 0 && reasoningCb != null) reasoningCb(rt);
                        }
                        if (delta.ContainsKey("function_call") && delta["function_call"] is Dictionary<string, object>)
                        {
                            Dictionary<string, object> fc = (Dictionary<string, object>)delta["function_call"];
                            StreamToolAcc cur0;
                            if (!acc.ContainsKey(0)) { cur0 = new StreamToolAcc(); acc[0] = cur0; }
                            else cur0 = acc[0];
                            if (fc.ContainsKey("name") && fc["name"] != null) cur0.Name += Json.AsString(fc["name"]);
                            if (fc.ContainsKey("arguments") && fc["arguments"] != null) cur0.Args.Append(Json.AsString(fc["arguments"]));
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

            Console.WriteLine("[agent] stream done: contentLen=" + finalContent.Length
                + " toolCallFragments=" + acc.Count
                + " toolCalls=" + (toolCallsOut == null ? 0 : toolCallsOut.Count));
            if (cfg.AllowTools && finalContent.Length > 0 && (toolCallsOut == null || toolCallsOut.Count == 0))
                Console.WriteLine("[agent] 注意: 本文のみでツール未使用でした。"
                    + " モデル(" + cfg.Model + ")が function calling 非対応、"
                    + " または API/プロキシが tools を無視している可能性があります。"
                    + " ツール対応モデル(gpt-4o-mini 等)でお試しください。");

            LlmResp r = new LlmResp();
            r.Content = finalContent.ToString();
            r.ToolCalls = (toolCallsOut != null && toolCallsOut.Count > 0) ? toolCallsOut : null;
            return r;
        }

        // delta から文字列フィールドを取り出す(文字列以外・欠落は空文字)
        static string DeltaText(Dictionary<string, object> delta, string key)
        {
            if (delta == null || !delta.ContainsKey(key)) return "";
            object v = delta[key];
            if (v == null) return "";
            if (v is string) return (string)v;
            return "";
        }

        // 非SSE(通常の JSON)応答を解釈する。stream:true が無視された場合のフォールバック。
        static void ParseNonStreamJson(string body, StringBuilder contentOut,
                                       Dictionary<int, StreamToolAcc> acc, Config cfg,
                                       Action<string> contentCb, Action<string> reasoningCb)
        {
            if (body == null || body.Length == 0) return;
            object o;
            try { o = Json.Parse(body); }
            catch (Exception ex) { Console.WriteLine("[agent] JSON解析失敗: " + ex.Message); return; }

            Dictionary<string, object> root = o as Dictionary<string, object>;
            if (root == null) return;
            if (!root.ContainsKey("choices") || !(root["choices"] is List<object>))
            {
                Console.WriteLine("[agent] choices がありません(エラー応答の可能性)");
                return;
            }
            List<object> choices = (List<object>)root["choices"];
            if (choices.Count == 0) return;
            Dictionary<string, object> ch = choices[0] as Dictionary<string, object>;
            if (ch == null) return;

            if (ch.ContainsKey("finish_reason") && ch["finish_reason"] != null)
                Console.WriteLine("[agent] finish_reason=" + Json.AsString(ch["finish_reason"]));

            Dictionary<string, object> msg = (ch.ContainsKey("message")) ? (ch["message"] as Dictionary<string, object>) : null;
            if (msg == null) return;

            string content = msg.ContainsKey("content") ? Json.AsString(msg["content"]) : "";
            if (content.Length > 0)
            {
                contentOut.Append(content);
                if (contentCb != null) contentCb(content);
            }
            if (cfg.EmitReasoning)
            {
                string rc = "";
                if (msg.ContainsKey("reasoning_content")) rc = Json.AsString(msg["reasoning_content"]);
                else if (msg.ContainsKey("reasoning")) rc = Json.AsString(msg["reasoning"]);
                if (rc.Length > 0 && reasoningCb != null) reasoningCb(rc);
            }

            if (msg.ContainsKey("tool_calls") && msg["tool_calls"] is List<object>)
            {
                List<object> tcs = (List<object>)msg["tool_calls"];
                for (int i = 0; i < tcs.Count; i++)
                {
                    Dictionary<string, object> tc = tcs[i] as Dictionary<string, object>;
                    if (tc == null) continue;
                    StreamToolAcc cur = new StreamToolAcc();
                    if (tc.ContainsKey("id") && tc["id"] != null) cur.Id = Json.AsString(tc["id"]);
                    Dictionary<string, object> fn = (tc.ContainsKey("function")) ? (tc["function"] as Dictionary<string, object>) : null;
                    if (fn != null)
                    {
                        if (fn.ContainsKey("name") && fn["name"] != null) cur.Name = Json.AsString(fn["name"]);
                        if (fn.ContainsKey("arguments"))
                        {
                            object av = fn["arguments"];
                            cur.Args.Append(av is string ? (string)av : Json.ToString(av));
                        }
                    }
                    acc[i] = cur;
                }
                Console.WriteLine("[agent] 非SSE: tool_calls=" + tcs.Count);
            }
            else if (msg.ContainsKey("function_call") && msg["function_call"] is Dictionary<string, object>)
            {
                Dictionary<string, object> fc = (Dictionary<string, object>)msg["function_call"];
                StreamToolAcc cur = new StreamToolAcc();
                cur.Name = fc.ContainsKey("name") ? Json.AsString(fc["name"]) : "";
                if (fc.ContainsKey("arguments"))
                {
                    object av = fc["arguments"];
                    cur.Args.Append(av is string ? (string)av : Json.ToString(av));
                }
                acc[0] = cur;
                Console.WriteLine("[agent] 非SSE: function_call=" + cur.Name);
            }
        }

        class StreamToolAcc
        {
            public string Id = "";
            public string Name = "";
            public StringBuilder Args = new StringBuilder();
        }

        // ストリーミング付きエージェント実行。emit(イベント名, dataJSON) で SSE を配信。
        // イベント: content(応答トークン) / reasoning(思考トークン) / tool(ツール実行) / toolend / done(完了)
        public static void RunAgentStream(Config cfg, SessionCtx ctx, string userMessage,
                                          Action<string, string> emit, Action<SessionCtx> save)
        {
            ctx.Messages.Add(Msg("user", userMessage));
            AgentProgress.Clear();
            AgentControl.Reset();
            List<object> turnSteps = new List<object>();   // このターンのツール実行記録
            StringBuilder turnThink = new StringBuilder(); // このターンの思考(reasoning_content)
            MaybeCompact(cfg, ctx);
            if (save != null) save(ctx);

            try
            {
                for (int turn = 0; turn < MAX_TURNS; turn++)
                {
                    if (AgentControl.CancelRequested)
                    {
                        ctx.Messages.Add(Msg("assistant", "⚠ 処理を中断しました。", turnSteps, ThinkOf(cfg, turnThink)));
                        if (emit != null) emit("done", "{\"stopped\":true}");
                        return;
                    }

                    List<object> eff = BuildEffectiveMessages(cfg, ctx);
                    List<object> toolCallsOut = new List<object>();
                    LlmResp resp = CallOnceStream(cfg, eff, cfg.AllowTools,
                        delegate (string d)
                        {
                            if (emit != null) emit("content", "{ \"t\" : \"" + JsonEscape(d) + "\" }");
                        },
                        delegate (string d)
                        {
                            turnThink.Append(d);   // リロード後も表示できるよう思考を保存する
                            if (emit != null) emit("reasoning", "{ \"t\" : \"" + JsonEscape(d) + "\" }");
                        },
                        toolCallsOut);
                    if (resp.TotalTokens > 0) ctx.LastTotalTokens = resp.TotalTokens;

                    bool hasTools = (resp.ToolCalls != null && resp.ToolCalls.Count > 0);
                    if (!hasTools && (resp.Content == null || resp.Content.Trim().Length == 0))
                        Console.WriteLine("[agent] 警告: ツール呼び出しも本文も空です"
                            + "(モデルがツール非対応 / tools 未送信 / API 形式不一致の可能性)");

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
                            Console.WriteLine("[agent] stream tool_call[" + k + "] name=" + name
                                + " args=" + (argsJson.Length > 300 ? argsJson.Substring(0, 300) : argsJson));
                            string tdetail = ToolDetail(name, argsJson);
                            AddStep(turnSteps, "ツール実行: " + name + (tdetail.Length > 0 ? ("  " + tdetail) : ""));
                            if (emit != null) emit("tool", "{ \"name\" : \"" + JsonEscape(name) + "\", \"detail\" : \"" + JsonEscape(tdetail) + "\" }");
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
                    ctx.Messages.Add(Msg("assistant", final, turnSteps, ThinkOf(cfg, turnThink)));
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
            if (msg.ContainsKey("reasoning_content")) r.Reasoning = Json.AsString(msg["reasoning_content"]);
            else if (msg.ContainsKey("reasoning"))     r.Reasoning = Json.AsString(msg["reasoning"]);

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
        public string Reasoning;           // reasoning_content (思考。セッションに保存してリロード後も表示する)
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
