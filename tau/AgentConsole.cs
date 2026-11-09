// ---------------------------------------------------------------------------
//  tau - コンソール上で動く AI エージェント
//
//  agentweb (Web UI 版) とは独立した単一ファイル実装です。
//  コードは agentweb を参考にしていますが、共通化はしていません。
//  HTTP サーバ / Web UI を持たず、標準入出力だけで対話します。
//
//  ビルド: csc.exe /nologo /out:tau.exe AgentConsole.cs
//  実行  : tau.exe [オプション]
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace Tau
{
    // =======================================================================
    //  エントリポイント
    // =======================================================================
    class Program
    {
        static void Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { }
            try { Console.InputEncoding = Encoding.UTF8; }
            catch { }

            string configPath = "";
            string sessionArg = "";
            bool help = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = (args[i] ?? "").Trim();
                if (a == "--config" && i + 1 < args.Length) configPath = args[++i].Trim();
                else if (a.StartsWith("--config=")) configPath = a.Substring("--config=".Length);
                else if (a == "--session" && i + 1 < args.Length) sessionArg = args[++i].Trim();
                else if (a.StartsWith("--session=")) sessionArg = a.Substring("--session=".Length);
                else if (a == "--help" || a == "-h" || a == "/?") help = true;
                else if (a == "--list") { ListSessions(configPath); return; }
            }

            if (help) { PrintHelp(); return; }

            Config cfg = new Config(configPath);
            Chat chat = new Chat(cfg);
            chat.Run(sessionArg);
        }

        internal static void PrintHelp()
        {
            Console.WriteLine("tau - コンソール上で動く AI エージェント");
            Console.WriteLine();
            Console.WriteLine("使用法: tau.exe [オプション]");
            Console.WriteLine("  --config <path>    設定ファイル(agentweb.json)のパス");
            Console.WriteLine("  --session <id>     既存セッションを開いて続きから対話する");
            Console.WriteLine("  --list             保存済みセッションの一覧を表示して終了");
            Console.WriteLine("  --help, -h, /?     このヘルプを表示");
            Console.WriteLine();
            Console.WriteLine("対話中に使えるコマンド:");
            Console.WriteLine("  /help        ヘルプを表示");
            Console.WriteLine("  /new         新しいセッションを開始");
            Console.WriteLine("  /sessions    保存済みセッションの一覧");
            Console.WriteLine("  /open <id>   指定したセッションを開く");
            Console.WriteLine("  /model [名]  モデルの表示・切り替え");
            Console.WriteLine("  /tools       ツール実行の on/off を切り替え");
            Console.WriteLine("  /clear       画面を消す");
            Console.WriteLine("  /exit, /quit 終了 (Ctrl+D でも終了)");
        }

        static void ListSessions(string configPath)
        {
            // 設定を読み込んでおく(agentweb と同じ設定ファイルを参照していることを確認するため)
            new Config(configPath);
            SessionStore store = new SessionStore();
            List<object> list = store.List();
            if (list.Count == 0)
            {
                Console.WriteLine("保存済みセッションはありません。");
                Console.WriteLine("  保存先: " + store.DataDir);
                return;
            }
            Console.WriteLine("保存済みセッション (" + store.DataDir + ")");
            Console.WriteLine();
            for (int i = 0; i < list.Count; i++)
            {
                Dictionary<string, object> s = list[i] as Dictionary<string, object>;
                if (s == null) continue;
                Console.WriteLine("  " + Json.AsString(Get(s, "id")) + "  " + Json.AsString(Get(s, "title")));
            }
        }

        static object Get(Dictionary<string, object> d, string k)
        {
            if (d != null && d.ContainsKey(k)) return d[k];
            return null;
        }
    }
}

namespace Tau
{
    // =======================================================================
    //  設定
    //
    //  agentweb と同じファイルを参照する(独自の設定は持たない)。
    //  優先度: 環境変数 < agentweb.json
    //  保存先: %APPDATA%\agentweb\agentweb.json (Linux では ~/.config/agentweb/agentweb.json)
    // =======================================================================
    class Config
    {
        public string ApiBaseUrl;          // OpenAI 互換 /chat/completions の URL
        public string SecretKey;           // API キー
        public string Model;               // 選択中のモデル名
        public List<string> Models = new List<string>();   // 選択できるモデル名の一覧

        public bool AllowTools;            // ツール実行を許可するか
        public bool EmitReasoning;         // 思考(reasoning)を表示するか
        public int MaxToolsPerTurn;        // 1 ターンで実行するツールの上限
        public int ToolTimeoutSec;         // ツールのタイムアウト(秒)
        public int ContextWindow;          // コンテキスト上限(推定トークン)
        public int ReserveTokens;          // 応答用に確保するトークン
        public int KeepRecentTokens;       // 要約せず残す直近トークン数

        public string ProxyUrl;
        public string ProxyUser;
        public string ProxyPass;
        public bool UseSystemProxy;

        public string SystemPrompt;
        public string ConfigPath;          // 設定ファイルのパス(保存先)

        public Config(string configPathArg)
        {
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

            // モデル一覧は既定では 1 件(選択中のモデルだけ)
            Models.Add(Model);

            SystemPrompt = Env("AI_SYSTEM_PROMPT",
                "あなたはコンソール上で動作する AI エージェントです。"
                + " ユーザーの質問に日本語で簡潔に回答してください。"
                + "\n\nこのプログラムは利用者のローカルマシン上で動作しており、"
                + " あなたの指示で実際にツールを実行できます。"
                + " ファイルの調査や操作、コマンドの実行が必要な依頼には、"
                + " 積極的にツール(shell/read_file/write_file/list_dir)を使い、"
                + " 自分で実際に確認・実行してから結果を報告してください。"
                + " 危険・不可逆な操作を行う前にはユーザーに確認すること。"
                + "\n出力はコンソールに表示されるため、Markdown の装飾は最小限にし、"
                + " 読みやすいプレーンテキストで答えてください。");

            ConfigPath = (configPathArg != null && configPathArg.Trim().Length > 0)
                ? configPathArg.Trim()
                : Env("AGENTWEB_CONFIG", "");

            ApplyConfigFile();
        }

        // ---------------- 設定ファイル ----------------

        // agentweb と同じ設定ファイルを参照する
        public static string DefaultConfigPath()
        {
            return Path.Combine(AppDataDir(), "agentweb.json");
        }

        // agentweb と同じデータディレクトリ(セッションの保存先)
        public static string AppDataDir()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData))
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(home)) home = ".";
                return Path.Combine(home, ".config", "agentweb");
            }
            return Path.Combine(appData, "agentweb");
        }

        void ApplyConfigFile()
        {
            string path = ConfigPath;
            if (path == null || path.Trim().Length == 0)
                path = DefaultConfigPath();

            ConfigPath = path;
            if (!File.Exists(path))
            {
                // 設定ファイルが無くても環境変数だけで動く。保存時に作成する。
                return;
            }

            try
            {
                object o = Json.Parse(File.ReadAllText(path, Encoding.UTF8));
                ApplyDict(o as Dictionary<string, object>);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[config] 設定ファイルの読み込みに失敗しました: " + ex.Message);
            }
        }

        void ApplyDict(Dictionary<string, object> m)
        {
            if (m == null) return;

            if (Has(m, "llmApiUrl")) ApiBaseUrl = Json.AsString(Get(m, "llmApiUrl"));
            if (Has(m, "llmApiKey")) SecretKey = Json.AsString(Get(m, "llmApiKey"));
            if (Has(m, "model")) Model = Json.AsString(Get(m, "model"));
            if (Has(m, "models")) ApplyModels(Get(m, "models"));
            if (Has(m, "allowTools")) AllowTools = AsBool(Get(m, "allowTools"));
            if (Has(m, "emitReasoning")) EmitReasoning = AsBool(Get(m, "emitReasoning"));
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

            // 選択中のモデルが一覧に無ければ先頭へ寄せる
            if (Models.Count > 0 && !Models.Contains(Model)) Model = Models[0];
        }

        // モデル一覧(名前の配列)を適用する。文字列でも {"name": ...} でも受け付ける。
        void ApplyModels(object raw)
        {
            List<object> arr = raw as List<object>;
            if (arr == null) return;

            List<string> list = new List<string>();
            for (int i = 0; i < arr.Count; i++)
            {
                string name = "";
                Dictionary<string, object> e = arr[i] as Dictionary<string, object>;
                if (e != null) name = Json.AsString(Get(e, "name"));
                else name = Json.AsString(arr[i]);
                name = name.Trim();
                if (name.Length == 0) continue;
                if (!list.Contains(name)) list.Add(name);
            }
            if (list.Count == 0) return;
            Models = list;
            if (!Models.Contains(Model)) Model = Models[0];
        }

        public Dictionary<string, object> ToDict()
        {
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["llmApiUrl"] = ApiBaseUrl;
            m["llmApiKey"] = SecretKey;
            m["model"] = Model;
            m["models"] = ModelsToJson();
            m["allowTools"] = AllowTools;
            m["emitReasoning"] = EmitReasoning;
            m["maxToolsPerTurn"] = MaxToolsPerTurn;
            m["toolTimeoutSec"] = ToolTimeoutSec;
            m["contextWindow"] = ContextWindow;
            m["reserveTokens"] = ReserveTokens;
            m["keepRecentTokens"] = KeepRecentTokens;
            m["proxyUrl"] = ProxyUrl;
            m["proxyUser"] = ProxyUser;
            m["proxyPass"] = ProxyPass;
            m["useSystemProxy"] = UseSystemProxy;
            m["systemPrompt"] = SystemPrompt;
            return m;
        }

        List<object> ModelsToJson()
        {
            List<object> arr = new List<object>();
            for (int i = 0; i < Models.Count; i++) arr.Add(Models[i]);
            return arr;
        }

        // 設定をファイルへ保存する(/model での切り替えを次回も維持するため)
        //
        // agentweb と同じ設定ファイルを共有しているので、tau が知らないキー
        // (port / theme / bindPath / openBrowserOnStart など agentweb 専用の設定)を
        // 失わないよう、既存ファイルの内容を読み込んでから上書きする。
        public void Save()
        {
            try
            {
                Dictionary<string, object> m = ToDict();

                // 既存ファイルのうち tau が扱わないキーを引き継ぐ
                try
                {
                    if (File.Exists(ConfigPath))
                    {
                        object o = Json.Parse(File.ReadAllText(ConfigPath, Encoding.UTF8));
                        Dictionary<string, object> old = o as Dictionary<string, object>;
                        if (old != null)
                        {
                            foreach (KeyValuePair<string, object> kv in old)
                                if (!m.ContainsKey(kv.Key)) m[kv.Key] = kv.Value;
                        }
                    }
                }
                catch { }

                string dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(ConfigPath, Json.ToString(m), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Console.WriteLine("[config] 保存に失敗しました: " + ex.Message);
            }
        }

        // ---------------- 小物 ----------------

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
            if (v == null || v.Trim().Length == 0) return def;
            return v.Trim();
        }

        static int SafeInt(string s, int def)
        {
            int n;
            if (int.TryParse(s, out n)) return n;
            return def;
        }
    }
}

namespace Tau
{
    // =======================================================================
    //  最小 JSON パーサ / シリアライザ (外部ライブラリ不要の自己完結実装)
    // =======================================================================
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
                    throw new Exception("invalid object key");
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':')
                    throw new Exception("expected ':'");
                i++;
                d[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new Exception("unexpected end of object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; break; }
                throw new Exception("expected ',' or '}'");
            }
            return d;
        }

        static List<object> ParseArray(string s, ref int i)
        {
            List<object> a = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']')
            {
                i++; return a;
            }
            while (true)
            {
                a.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new Exception("unexpected end of array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; break; }
                throw new Exception("expected ',' or ']'");
            }
            return a;
        }

        static string ParseString(string s, ref int i)
        {
            i++; // "
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
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
                        if (i + 4 <= s.Length)
                        {
                            int code;
                            if (int.TryParse(s.Substring(i, 4),
                                    System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out code))
                                sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new Exception("unterminated string");
        }

        static object ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (Char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' ||
                                    s[i] == 'E' || s[i] == '-' || s[i] == '+')) i++;
            string t = s.Substring(start, i - start);
            int n;
            if (int.TryParse(t, System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture, out n))
                return n;
            long l;
            if (long.TryParse(t, System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out l))
                return l;
            double dbl;
            if (double.TryParse(t, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out dbl))
                return dbl;
            return 0;
        }

        // ---------------- シリアライズ ----------------

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
            if (o is bool) { sb.Append(((bool)o) ? "true" : "false"); return; }
            if (o is int) { sb.Append(((int)o).ToString(System.Globalization.CultureInfo.InvariantCulture)); return; }
            if (o is long) { sb.Append(((long)o).ToString(System.Globalization.CultureInfo.InvariantCulture)); return; }
            if (o is double) { sb.Append(((double)o).ToString("R", System.Globalization.CultureInfo.InvariantCulture)); return; }

            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d != null)
            {
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

            List<object> a = o as List<object>;
            if (a != null)
            {
                sb.Append('[');
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(sb, a[i]);
                }
                sb.Append(']');
                return;
            }

            // それ以外は文字列として扱う
            WriteString(sb, o.ToString());
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u" + ((int)c).ToString("x4",
                                System.Globalization.CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------- 取り出し ----------------

        public static string AsString(object o)
        {
            if (o == null) return "";
            if (o is string) return (string)o;
            if (o is bool) return ((bool)o) ? "true" : "false";
            return o.ToString();
        }

        public static int AsInt(object o)
        {
            if (o == null) return 0;
            if (o is int) return (int)o;
            if (o is long) return (int)(long)o;
            if (o is double) return (int)(double)o;
            if (o is bool) return ((bool)o) ? 1 : 0;
            string t = AsString(o).Trim();
            int v;
            if (int.TryParse(t, out v)) return v;
            return 0;
        }
    }
}

namespace Tau
{
    // =======================================================================
    //  セッションの永続化
    //
    //  agentweb とまったく同じ場所・同じ形式で読み書きする。
    //    %APPDATA%\agentweb\sessions\<id>.json
    //    %APPDATA%\agentweb\sessions\_state.json  (最後に開いたセッション)
    //
    //  これにより agentweb で作った会話を tau で開いて続きから対話でき、
    //  tau で作った会話を agentweb で開くこともできる。
    // =======================================================================
    class SessionStore
    {
        string sessionsDir;

        public SessionStore()
        {
            sessionsDir = Path.Combine(Config.AppDataDir(), "sessions");
            try { Directory.CreateDirectory(sessionsDir); }
            catch { }
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
            try { File.WriteAllText(FileOf(id), Json.ToString(s), Encoding.UTF8); }
            catch (Exception ex) { Console.WriteLine("[session] 保存に失敗しました: " + ex.Message); }
        }

        public List<object> List()
        {
            List<object> list = new List<object>();
            try
            {
                string[] files = Directory.GetFiles(sessionsDir, "*.json");
                Array.Sort(files);
                Array.Reverse(files);   // 新しいものが先頭に来やすいよう逆順
                foreach (string f in files)
                {
                    // _state.json (最後に開いたセッション / 下書き) は一覧に含めない
                    if (Path.GetFileName(f).StartsWith("_")) continue;
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

        // ---------------- 最後に開いていたセッション (agentweb と共有) ----------------

        string StateFile()
        {
            return Path.Combine(sessionsDir, "_state.json");
        }

        Dictionary<string, object> LoadState()
        {
            try
            {
                string f = StateFile();
                if (!File.Exists(f)) return new Dictionary<string, object>();
                object o = Json.Parse(File.ReadAllText(f, Encoding.UTF8));
                Dictionary<string, object> st = o as Dictionary<string, object>;
                return st ?? new Dictionary<string, object>();
            }
            catch { return new Dictionary<string, object>(); }
        }

        void SaveState(Dictionary<string, object> st)
        {
            try { File.WriteAllText(StateFile(), Json.ToString(st), Encoding.UTF8); }
            catch { }
        }

        public string LastSessionId()
        {
            Dictionary<string, object> st = LoadState();
            if (!st.ContainsKey("lastSession")) return "";
            string id = Json.AsString(st["lastSession"]).Trim();
            if (id.Length == 0) return "";
            if (!File.Exists(FileOf(id))) return "";   // 削除済みなら無視
            return id;
        }

        public void SetLastSessionId(string id)
        {
            if (id == null || id.Trim().Length == 0) return;
            Dictionary<string, object> st = LoadState();
            st["lastSession"] = Sanitize(id);
            SaveState(st);
        }
    }
}

namespace Tau
{
    // =======================================================================
    //  社内プロキシ
    //  - proxyUrl 設定時: そのプロキシを使用(認証ユーザー指定で credential 付与)
    //  - 未設定: useSystemProxy=1 なら OS のシステムプロキシ、=0 なら直接接続
    // =======================================================================
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
        }
    }

    // =======================================================================
    //  LLM API クライアント (OpenAI 互換)
    // =======================================================================
    class LlmResp
    {
        public string Content = "";
        public string Reasoning = "";
        public List<object> ToolCalls;
        public int TotalTokens;
    }

    class LlmCancelledException : Exception
    {
        public LlmCancelledException() : base("cancelled") { }
    }

    static class LlmClient
    {
        // 処理中のリクエスト(Ctrl+C で中断するため)
        static volatile HttpWebRequest current;

        // TLS の設定段階。
        //   0 = SystemDefault (OS に任せる) を試す
        //   1 = 明示的に TLS 1.2 以上を指定する
        static int tlsStage;
        static readonly object tlsLock = new object();

        // TLS のバージョンを設定する。
        //
        // 何もしないと、.NET Framework 4.6.1 以前をターゲットにしたアプリでは
        // 既定が Ssl3|Tls (TLS 1.0) 相当になり、TLS 1.2 以上を要求する API
        // (OpenAI 系・DeepSeek 系・多くのプロキシ) で
        //   「SSL/TLS のセキュリティで保護されているチャネルを作成できませんでした
        //    (status=SecureChannelFailure)」
        // になる。
        //
        // SystemDefault は列挙値としては存在するが、ターゲットフレームワークが
        // 4.7 未満だと OS 既定が使われず TLS 1.0 のままになることがある。
        // そのため「SystemDefault で失敗したら TLS 1.2 を明示して再試行する」
        // という二段構えにする。
        public static void ApplyTls()
        {
            lock (tlsLock)
            {
                if (tlsStage == 0)
                {
                    // 第 1 段: OS に任せる(Windows 10 なら TLS 1.2、11 なら 1.3)
                    try
                    {
                        object sd = Enum.Parse(typeof(SecurityProtocolType), "SystemDefault");
                        if (sd != null)
                        {
                            ServicePointManager.SecurityProtocol = (SecurityProtocolType)sd;
                            return;
                        }
                    }
                    catch { }
                    tlsStage = 1;   // SystemDefault が無い環境はすぐ第 2 段へ
                }

                // 第 2 段: TLS 1.2 以上を明示する
                try
                {
                    SecurityProtocolType proto = SecurityProtocolType.Tls12;
                    // Tls13 は .NET Framework 4.8 以降にしか無いため、名前で確認する
                    try
                    {
                        object t13 = Enum.Parse(typeof(SecurityProtocolType), "Tls13");
                        if (t13 != null) proto |= (SecurityProtocolType)t13;
                    }
                    catch { }
                    ServicePointManager.SecurityProtocol = proto;
                }
                catch { }
            }
        }

        // TLS のハンドシェイク失敗を受けて、次の段へ進める。
        // 進めた場合は true(呼び出し側は同じ要求を 1 度だけ再試行する)。
        public static bool EscalateTls()
        {
            lock (tlsLock)
            {
                if (tlsStage == 0)
                {
                    tlsStage = 1;      // 次は TLS 1.2 を明示する
                    return true;
                }
                return false;          // すでに TLS 1.2 を指定済みなら諦める
            }
        }

        // TLS ハンドシェイク失敗かどうか
        public static bool IsTlsFailure(WebException wex)
        {
            if (wex == null) return false;
            if (wex.Status == WebExceptionStatus.SecureChannelFailure) return true;
            if (wex.Status == WebExceptionStatus.TrustFailure) return false;  // 証明書の問題
            // 内部例外に TLS/SslStream 系が含まれていればハンドシェイク失敗とみなす
            Exception e = wex.InnerException;
            int depth = 0;
            while (e != null && depth < 4)
            {
                string n = e.GetType().FullName ?? "";
                if (n.IndexOf("Authentication", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("SslStream", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Tls", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                e = e.InnerException;
                depth++;
            }
            return false;
        }

        public static void Cancel()
        {
            HttpWebRequest r = current;
            if (r != null)
            {
                try { r.Abort(); } catch { }
            }
        }

        static void Setup(HttpWebRequest request, Config cfg, byte[] payload)
        {
            ApplyTls();
            ProxyHelper.Apply(request, cfg);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "text/event-stream";
            request.Headers["Authorization"] = "Bearer " + cfg.SecretKey;
            request.ContentLength = payload.Length;
            request.Timeout = 180000;
            request.ReadWriteTimeout = 180000;

            // SSE は応答が長く続くため、次の設定が重要になる。
            //  - Expect: 100-continue は一部のプロキシ/サーバで
            //    「受信時に予期しないエラー」の原因になるので無効化する。
            //  - HTTP/1.1 で KeepAlive を有効にし、接続を使い回す。
            //  - 途中で切れないよう、バッファリングを小さめにする必要は無いが、
            //    Nagle を無効にして小さなチャンクを即座に送る。
            request.ServicePoint.Expect100Continue = false;
            request.KeepAlive = true;
            request.ProtocolVersion = HttpVersion.Version11;
            request.ServicePoint.UseNagleAlgorithm = false;
            // 応答がチャンク形式で届く場合に備える
            request.SendChunked = false;
            request.AllowReadStreamBuffering = false;
            request.AllowWriteStreamBuffering = true;
        }

        // WebException の内容を、原因が分かる形に整形する
        public static string DescribeWebException(WebException wex)
        {
            if (wex == null) return "";
            StringBuilder sb = new StringBuilder();
            sb.Append(wex.Message);
            sb.Append(" (status=").Append(wex.Status).Append(")");

            // HTTP ステータスが返っている場合は本文も読む(原因が書かれていることが多い)
            if (wex.Response != null)
            {
                try
                {
                    HttpWebResponse resp = wex.Response as HttpWebResponse;
                    if (resp != null) sb.Append(" [HTTP ").Append((int)resp.StatusCode).Append("]");
                    using (Stream rs = wex.Response.GetResponseStream())
                    {
                        string body = ToolExecutor.DecodeBytesPublic(ToolExecutor.ReadAllBytesPublic(rs));
                        if (body.Length > 0)
                        {
                            if (body.Length > 400) body = body.Substring(0, 400) + "...";
                            sb.Append(" ").Append(body);
                        }
                    }
                }
                catch { }
            }

            // 内部例外(TLS ハンドシェイク失敗など)は原因がここに出る
            Exception inner = wex.InnerException;
            int depth = 0;
            while (inner != null && depth < 3)
            {
                sb.Append(" <- ").Append(inner.Message);
                inner = inner.InnerException;
                depth++;
            }
            return sb.ToString();
        }

        // API に送るメッセージから、保存専用メタデータ(ts/steps/think)を取り除く
        static List<object> ApiMessages(List<object> messages)
        {
            List<object> apiMessages = new List<object>();
            foreach (object o in messages)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (m == null) { apiMessages.Add(o); continue; }
                Dictionary<string, object> c = new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> kv in m)
                    if (kv.Key != "ts" && kv.Key != "steps" && kv.Key != "think") c[kv.Key] = kv.Value;
                apiMessages.Add(c);
            }
            return apiMessages;
        }

        static Dictionary<string, object> BuildBody(Config cfg, List<object> messages, bool useTools, bool stream)
        {
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["model"] = cfg.Model;
            body["messages"] = ApiMessages(messages);
            body["temperature"] = 0.7;
            if (stream) body["stream"] = true;
            if (useTools && cfg.AllowTools)
            {
                body["tools"] = ToolExecutor.Definitions();
                body["tool_choice"] = "auto";
            }
            return body;
        }

        // ---- 非ストリーミング呼び出し(要約など) ----
        public static LlmResp CallOnce(Config cfg, List<object> messages, bool useTools)
        {
            try
            {
                return CallOnceInternal(cfg, messages, useTools);
            }
            catch (WebException wex)
            {
                if (IsTlsFailure(wex) && EscalateTls())
                    return CallOnceInternal(cfg, messages, useTools);
                throw;
            }
        }

        static LlmResp CallOnceInternal(Config cfg, List<object> messages, bool useTools)
        {
            Dictionary<string, object> body = BuildBody(cfg, messages, useTools, false);
            byte[] payload = Encoding.UTF8.GetBytes(Json.ToString(body));

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(cfg.ApiBaseUrl);
            Setup(request, cfg, payload);
            request.Accept = "application/json";

            current = request;
            string responseBody;
            try
            {
                using (Stream s = request.GetRequestStream())
                    s.Write(payload, 0, payload.Length);
                using (HttpWebResponse resp = (HttpWebResponse)request.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    responseBody = sr.ReadToEnd();
            }
            catch (WebException)
            {
                throw;   // TLS 失敗の再試行をラッパーが判断できるよう、そのまま投げる
            }
            finally
            {
                current = null;
            }

            return ParseOnce(responseBody);
        }

        static LlmResp ParseOnce(string responseBody)
        {
            object parsed = Json.Parse(responseBody);
            Dictionary<string, object> root = parsed as Dictionary<string, object>;
            if (root == null) throw new Exception("LLM API の応答形式が不正です");

            if (root.ContainsKey("error"))
            {
                Dictionary<string, object> ed = root["error"] as Dictionary<string, object>;
                if (ed != null && ed.ContainsKey("message"))
                    throw new Exception("LLM API エラー: " + Json.AsString(ed["message"]));
                throw new Exception("LLM API エラー: " + Json.ToString(root["error"]));
            }

            if (!root.ContainsKey("choices") || !(root["choices"] is List<object>) ||
                ((List<object>)root["choices"]).Count == 0)
                throw new Exception("LLM API の応答に choices がありません");

            Dictionary<string, object> first =
                ((List<object>)root["choices"])[0] as Dictionary<string, object>;
            if (first == null || !first.ContainsKey("message"))
                throw new Exception("LLM API の応答に message がありません");

            Dictionary<string, object> msg = first["message"] as Dictionary<string, object>;
            if (msg == null) throw new Exception("LLM API の応答に message がありません");

            LlmResp r = new LlmResp();
            r.Content = msg.ContainsKey("content") ? Json.AsString(msg["content"]) : "";
            if (msg.ContainsKey("reasoning_content")) r.Reasoning = Json.AsString(msg["reasoning_content"]);
            else if (msg.ContainsKey("reasoning")) r.Reasoning = Json.AsString(msg["reasoning"]);

            if (root.ContainsKey("usage") && root["usage"] is Dictionary<string, object>)
            {
                Dictionary<string, object> u = (Dictionary<string, object>)root["usage"];
                if (u.ContainsKey("total_tokens")) r.TotalTokens = Json.AsInt(u["total_tokens"]);
            }
            return r;
        }

        // ---- ストリーミング呼び出し ----
        class StreamAcc
        {
            public string Id = "";
            public string Name = "";
            public StringBuilder Args = new StringBuilder();
        }

        // TLS ハンドシェイクに失敗したら、設定を一段上げて 1 度だけ再試行する。
        // (SystemDefault が効かない環境では TLS 1.2 を明示すると通ることが多い)
        public static LlmResp CallStream(Config cfg, List<object> messages, bool useTools,
                                         Action<string> contentCb, Action<string> reasoningCb)
        {
            try
            {
                return CallStreamOnce(cfg, messages, useTools, contentCb, reasoningCb);
            }
            catch (WebException wex)
            {
                if (IsTlsFailure(wex) && EscalateTls())
                {
                    ConsoleUi.Info("TLS の設定を変更して再試行します…");
                    return CallStreamOnce(cfg, messages, useTools, contentCb, reasoningCb);
                }
                throw;
            }
        }

        static LlmResp CallStreamOnce(Config cfg, List<object> messages, bool useTools,
                                      Action<string> contentCb, Action<string> reasoningCb)
        {
            Dictionary<string, object> body = BuildBody(cfg, messages, useTools, true);
            byte[] payload = Encoding.UTF8.GetBytes(Json.ToString(body));

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(cfg.ApiBaseUrl);
            Setup(request, cfg, payload);

            StringBuilder content = new StringBuilder();
            Dictionary<int, StreamAcc> acc = new Dictionary<int, StreamAcc>();

            current = request;
            try
            {
                using (Stream s = request.GetRequestStream())
                    s.Write(payload, 0, payload.Length);

                using (HttpWebResponse resp = (HttpWebResponse)request.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string ctype = (resp.ContentType == null) ? "" : resp.ContentType;
                    if (ctype.IndexOf("event-stream", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        // プロキシ等が stream:true を無視して通常の JSON を返した場合
                        string bodyText = sr.ReadToEnd();
                        ParseNonStreamJson(bodyText, content, acc, cfg, contentCb, reasoningCb);
                    }
                    else
                    {
                        bool sawDone = false;   // [DONE] を受け取ったか(正常終了の目印)
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            if (line.Length == 0) continue;
                            if (!line.StartsWith("data:")) continue;
                            string data = line.Substring(5).Trim();
                            if (data == "[DONE]") { sawDone = true; break; }

                            object parsed;
                            try { parsed = Json.Parse(data); }
                            catch { continue; }

                            Dictionary<string, object> root = parsed as Dictionary<string, object>;
                            if (root == null || !root.ContainsKey("choices") ||
                                !(root["choices"] is List<object>)) continue;
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
                                    content.Append(t);
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
                                StreamAcc cur;
                                if (!acc.ContainsKey(0)) { cur = new StreamAcc(); acc[0] = cur; }
                                else cur = acc[0];
                                if (fc.ContainsKey("name") && fc["name"] != null) cur.Name += Json.AsString(fc["name"]);
                                if (fc.ContainsKey("arguments") && fc["arguments"] != null) cur.Args.Append(Json.AsString(fc["arguments"]));
                            }
                            if (delta.ContainsKey("tool_calls") && delta["tool_calls"] is List<object>)
                            {
                                foreach (object tco in (List<object>)delta["tool_calls"])
                                {
                                    Dictionary<string, object> tc = tco as Dictionary<string, object>;
                                    if (tc == null) continue;
                                    int idx = tc.ContainsKey("index") ? Json.AsInt(tc["index"]) : acc.Count;
                                    StreamAcc cur;
                                    if (!acc.ContainsKey(idx)) { cur = new StreamAcc(); acc[idx] = cur; }
                                    else cur = acc[idx];
                                    if (tc.ContainsKey("id") && tc["id"] != null) cur.Id = Json.AsString(tc["id"]);
                                    Dictionary<string, object> fn =
                                        (tc.ContainsKey("function")) ? tc["function"] as Dictionary<string, object> : null;
                                    if (fn != null)
                                    {
                                        if (fn.ContainsKey("name") && fn["name"] != null) cur.Name += Json.AsString(fn["name"]);
                                        if (fn.ContainsKey("arguments") && fn["arguments"] != null) cur.Args.Append(Json.AsString(fn["arguments"]));
                                    }
                                }
                            }
                        }

                        // [DONE] を受け取らずにストリームが終わった場合は、
                        // 応答が途中で切れている(通信断・サーバ側のタイムアウト等)。
                        // 黙って途中までの本文を確定させない。
                        if (!sawDone && content.Length > 0)
                        {
                            throw new Exception("LLM API(stream)エラー: 応答が途中で切断されました"
                                + "(受信済み " + content.Length + " 文字)。"
                                + " もう一度お試しください。");
                        }
                    }
                }
            }
            catch (WebException wex)
            {
                if (wex.Status == WebExceptionStatus.RequestCanceled) throw new LlmCancelledException();
                throw;   // TLS 失敗の再試行をラッパーが判断できるよう、そのまま投げる
            }
            catch (IOException iex)
            {
                // ストリーム読み取り中の切断(「受信時に予期しないエラー」など)
                throw new Exception("LLM API(stream)エラー: 接続が切断されました: " + iex.Message
                    + " (受信済み " + content.Length + " 文字)"
                    + "。ネットワーク/プロキシのタイムアウトや、サーバ側の応答中断が考えられます。");
            }
            finally
            {
                current = null;
            }

            List<object> toolCalls = new List<object>();
            List<int> idxs = new List<int>(acc.Keys);
            idxs.Sort();
            foreach (int ix in idxs)
            {
                StreamAcc c = acc[ix];
                if (c.Name.Length == 0) continue;
                Dictionary<string, object> fn = new Dictionary<string, object>();
                fn["name"] = c.Name;
                fn["arguments"] = c.Args.ToString();
                Dictionary<string, object> call = new Dictionary<string, object>();
                call["id"] = c.Id.Length > 0 ? c.Id : "call_" + ix;
                call["type"] = "function";
                call["function"] = fn;
                toolCalls.Add(call);
            }

            LlmResp r = new LlmResp();
            r.Content = content.ToString();
            r.ToolCalls = (toolCalls.Count > 0) ? toolCalls : null;
            return r;
        }

        static string DeltaText(Dictionary<string, object> delta, string key)
        {
            if (delta == null || !delta.ContainsKey(key)) return "";
            object v = delta[key];
            if (v is string) return (string)v;
            return "";
        }

        // 非SSE(通常の JSON)応答を解釈する。stream:true が無視された場合のフォールバック。
        static void ParseNonStreamJson(string body, StringBuilder contentOut,
                                       Dictionary<int, StreamAcc> acc, Config cfg,
                                       Action<string> contentCb, Action<string> reasoningCb)
        {
            if (body == null || body.Length == 0) return;
            object o;
            try { o = Json.Parse(body); }
            catch { return; }

            Dictionary<string, object> root = o as Dictionary<string, object>;
            if (root == null) return;
            if (root.ContainsKey("error"))
            {
                Dictionary<string, object> ed = root["error"] as Dictionary<string, object>;
                throw new Exception("LLM API エラー: "
                    + (ed != null && ed.ContainsKey("message") ? Json.AsString(ed["message"]) : Json.ToString(root["error"])));
            }
            if (!root.ContainsKey("choices") || !(root["choices"] is List<object>)) return;
            List<object> choices = (List<object>)root["choices"];
            if (choices.Count == 0) return;
            Dictionary<string, object> first = choices[0] as Dictionary<string, object>;
            if (first == null || !first.ContainsKey("message")) return;
            Dictionary<string, object> msg = first["message"] as Dictionary<string, object>;
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
                    StreamAcc cur = new StreamAcc();
                    if (tc.ContainsKey("id") && tc["id"] != null) cur.Id = Json.AsString(tc["id"]);
                    Dictionary<string, object> fn =
                        (tc.ContainsKey("function")) ? tc["function"] as Dictionary<string, object> : null;
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
            }
            else if (msg.ContainsKey("function_call") && msg["function_call"] is Dictionary<string, object>)
            {
                Dictionary<string, object> fc = (Dictionary<string, object>)msg["function_call"];
                StreamAcc cur = new StreamAcc();
                cur.Name = fc.ContainsKey("name") ? Json.AsString(fc["name"]) : "";
                if (fc.ContainsKey("arguments"))
                {
                    object av = fc["arguments"];
                    cur.Args.Append(av is string ? (string)av : Json.ToString(av));
                }
                acc[0] = cur;
            }
        }
    }
}

namespace Tau
{
    // =======================================================================
    //  ツール実行 (Function Calling)
    // =======================================================================
    static class ToolExecutor
    {
        const int MAX_OUTPUT = 32000;   // ツール結果として LLM に返す最大文字数

        static object Get(Dictionary<string, object> d, string key)
        {
            if (d != null && d.ContainsKey(key)) return d[key];
            return null;
        }

        static string NormalizeName(string name)
        {
            if (name == null) return "";
            switch (name.Trim().ToLowerInvariant())
            {
                case "shell": case "cmd": case "execute": case "run": case "exec":
                case "run_command": case "execute_command": case "command":
                    return "shell";
                case "read_file": case "read": case "cat": case "readfile":
                    return "read_file";
                case "write_file": case "write": case "writefile":
                    return "write_file";
                case "list_dir": case "ls": case "dir": case "list_directory": case "listdir":
                    return "list_dir";
                case "curl": case "http_request": case "http": case "fetch":
                    return "curl";
                default:
                    return name;
            }
        }

        // プロキシが function.name を返さない場合に、引数の形からツール名を推定する
        public static string InferName(Dictionary<string, object> fn, string argsJson)
        {
            if (fn != null && fn.ContainsKey("name"))
            {
                string nm = Json.AsString(fn["name"]);
                if (nm != null && nm.Trim().Length > 0) return NormalizeName(nm);
            }
            Dictionary<string, object> args = null;
            try { args = Json.Parse(argsJson) as Dictionary<string, object>; }
            catch { }
            if (args == null) args = new Dictionary<string, object>();

            if (args.ContainsKey("cmd")) return "shell";
            if (args.ContainsKey("content") && args.ContainsKey("path")) return "write_file";
            if (args.ContainsKey("pattern")) return "list_dir";
            if (args.ContainsKey("path")) return "read_file";
            return "shell";
        }

        public static string Execute(string name, string argsJson, Config cfg)
        {
            Dictionary<string, object> args = null;
            try { args = Json.Parse(argsJson) as Dictionary<string, object>; }
            catch { }
            if (args == null) args = new Dictionary<string, object>();

            if (name == null || name.Trim().Length == 0) name = InferName(null, argsJson);
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
                    return Curl(cfg, Json.AsString(Get(args, "url")),
                        Json.AsString(Get(args, "method")),
                        args.ContainsKey("headers") ? args["headers"] : null,
                        Json.AsString(Get(args, "body")));
                default:
                    return Err("unknown tool: " + name
                        + " (利用可能: shell/read_file/write_file/list_dir/curl)");
            }
        }

        // ---------------- ツール定義 ----------------

        static Dictionary<string, object> Func(string name, string desc,
                                               string[] required, string[][] props)
        {
            Dictionary<string, object> f = new Dictionary<string, object>();
            f["name"] = name;
            f["description"] = desc;

            Dictionary<string, object> params_ = new Dictionary<string, object>();
            params_["type"] = "object";
            Dictionary<string, object> p = new Dictionary<string, object>();
            for (int i = 0; i < props.Length; i++)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["type"] = props[i][1];
                d["description"] = props[i][2];
                p[props[i][0]] = d;
            }
            params_["properties"] = p;
            params_["required"] = new List<object>(required);
            f["parameters"] = params_;

            Dictionary<string, object> tool = new Dictionary<string, object>();
            tool["type"] = "function";
            tool["function"] = f;
            return tool;
        }

        public static List<object> Definitions()
        {
            List<object> tools = new List<object>();

            tools.Add(Func("shell",
                "ローカルマシン上でコマンドを実行します。"
                + " 標準出力・標準エラー・終了コードを返します。"
                + " ファイル/ディレクトリ操作やプログラム実行など任意のシェルコマンドに使えます。",
                new string[] { "cmd" },
                new string[][] { new string[] { "cmd", "string", "実行するコマンド文字列(例: dir /b)" } }));

            tools.Add(Func("read_file",
                "指定したファイルの内容をテキストで読み取ります。",
                new string[] { "path" },
                new string[][] { new string[] { "path", "string", "読み取るファイルのパス" } }));

            tools.Add(Func("write_file",
                "指定したパスへテキストを書き込みます(上書き)。",
                new string[] { "path", "content" },
                new string[][] {
                    new string[] { "path", "string", "書き込むファイルのパス" },
                    new string[] { "content", "string", "書き込む内容" }
                }));

            tools.Add(Func("list_dir",
                "ディレクトリ内のファイルとサブディレクトリの一覧を返します。",
                new string[] { "path" },
                new string[][] {
                    new string[] { "path", "string", "一覧を取得するディレクトリのパス" },
                    new string[] { "pattern", "string", "ファイルの絞り込みパターン(例: *.txt)。省略時は *" }
                }));

            tools.Add(Func("curl",
                "HTTP/HTTPS リクエストを送信し、応答を返します。"
                + " 設定済みのプロキシがあれば経由します。",
                new string[] { "url" },
                new string[][] {
                    new string[] { "url", "string", "リクエスト先の URL" },
                    new string[] { "method", "string", "HTTP メソッド(例: GET, POST)。省略時は body の有無で自動" },
                    new string[] { "headers", "object", "追加の HTTP ヘッダー" },
                    new string[] { "body", "string", "リクエストボディ" }
                }));

            return tools;
        }

        // ---------------- 各ツールの実装 ----------------

        static string Shell(string cmd, int timeoutSec)
        {
            if (cmd == null || cmd.Trim().Length == 0) return Err("cmd が空です");
            try
            {
                bool win = Path.DirectorySeparatorChar == '\\';
                ProcessStartInfo psi = win
                    ? new ProcessStartInfo("cmd.exe", "/c " + cmd)
                    : new ProcessStartInfo("/bin/sh", "-c " + cmd);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                Process p = Process.Start(psi);
                System.Threading.Tasks.Task<byte[]> so = CopyToBytesAsync(p.StandardOutput.BaseStream);
                System.Threading.Tasks.Task<byte[]> se = CopyToBytesAsync(p.StandardError.BaseStream);

                if (!p.WaitForExit(timeoutSec * 1000))
                {
                    try { p.Kill(); } catch { }
                    return Err("実行がタイムアウトしました(" + timeoutSec + "秒): " + cmd);
                }
                p.WaitForExit();

                Dictionary<string, object> d = new Dictionary<string, object>();
                d["ok"] = true;
                d["exitCode"] = p.ExitCode;
                d["stdout"] = Clip(DecodeBytes(so.Result));
                d["stderr"] = Clip(DecodeBytes(se.Result));
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
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["ok"] = true;
                d["content"] = Clip(DecodeBytes(File.ReadAllBytes(path)));
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
                StringBuilder list = new StringBuilder();
                List<object> entries = new List<object>();

                string[] dirs = Directory.GetDirectories(path);
                foreach (string den in dirs)
                {
                    string name = Path.GetFileName(den);
                    list.Append("[DIR ]  ").Append(name).Append("/\n");
                    Dictionary<string, object> e = new Dictionary<string, object>();
                    e["type"] = "dir"; e["name"] = name; e["path"] = den;
                    entries.Add(e);
                }

                string[] files = Directory.GetFiles(path, pattern);
                foreach (string f in files)
                {
                    string name = Path.GetFileName(f);
                    long size = new FileInfo(f).Length;
                    list.Append("[FILE]  ").Append(name).Append("  ").Append(HumanSize(size)).Append("\n");
                    Dictionary<string, object> e = new Dictionary<string, object>();
                    e["type"] = "file"; e["name"] = name; e["size"] = size; e["path"] = f;
                    entries.Add(e);
                }

                Dictionary<string, object> d = new Dictionary<string, object>();
                d["ok"] = true;
                d["path"] = Path.GetFullPath(path);
                d["total"] = dirs.Length + files.Length;
                d["list"] = list.ToString();
                d["entries"] = entries;
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

        static string Curl(Config cfg, string url, string method, object headersObj, string body)
        {
            if (url == null || url.Trim().Length == 0) return Err("url が指定されていません");
            if (method == null || method.Trim().Length == 0)
                method = (body != null && body.Length > 0) ? "POST" : "GET";
            method = method.Trim().ToUpperInvariant();

            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url.Trim());
                LlmClient.ApplyTls();   // HTTPS 先でも TLS 1.2 以上を使えるようにする
                ProxyHelper.Apply(request, cfg);
                request.Method = method;
                request.Timeout = cfg.ToolTimeoutSec * 1000;
                request.ReadWriteTimeout = cfg.ToolTimeoutSec * 1000;

                Dictionary<string, object> headers = headersObj as Dictionary<string, object>;
                if (headers != null)
                {
                    foreach (KeyValuePair<string, object> kv in headers)
                    {
                        string v = Json.AsString(kv.Value);
                        if (String.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) request.ContentType = v;
                        else if (String.Equals(kv.Key, "Accept", StringComparison.OrdinalIgnoreCase)) request.Accept = v;
                        else if (String.Equals(kv.Key, "User-Agent", StringComparison.OrdinalIgnoreCase)) request.UserAgent = v;
                        else request.Headers[kv.Key] = v;
                    }
                }

                if (body != null && body.Length > 0)
                {
                    if (request.ContentType == null) request.ContentType = "application/json";
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
                    if (wex.Response == null) return Err("curl 通信エラー: " + wex.Message);
                    using (Stream rs = wex.Response.GetResponseStream())
                        respBody = DecodeBytes(ReadAllBytes(rs));
                    status = ((HttpWebResponse)wex.Response).StatusCode;
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

        // ---------------- 文字コード判定 ----------------

        // エラー応答の本文読み取り用に外部から使えるようにしたもの
        public static string DecodeBytesPublic(byte[] data) { return DecodeBytes(data); }
        public static byte[] ReadAllBytesPublic(Stream s) { return ReadAllBytes(s); }

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
                if (cp > 0 && cp != 65001) return Encoding.GetEncoding(cp).GetString(data);
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
            if (s.Length > MAX_OUTPUT) s = s.Substring(0, MAX_OUTPUT) + "\n...[出力が長いため省略]";
            return s;
        }

        static string Err(string message)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["error"] = message;
            return Json.ToString(d);
        }
    }
}

namespace Tau
{
    // =======================================================================
    //  会話コンテキスト
    // =======================================================================
    class SessionCtx
    {
        public string Id;
        public string Title = "";
        public string Summary = "";
        public int ContextStart = 0;
        public int LastTotalTokens = 0;   // 直前の API 応答 usage.total_tokens
        public List<object> Messages = new List<object>();
    }

    // =======================================================================
    //  エージェント本体
    //
    //  1 ターン: LLM 呼び出し → ツール実行 → 再問い合わせ … を最終回答まで繰り返す
    // =======================================================================
    class Agent
    {
        const int MAX_TURNS = 10;   // 1 ターンのツール反復上限

        readonly Config cfg;

        public Agent(Config cfg)
        {
            this.cfg = cfg;
        }

        // ---------------- メッセージ生成 ----------------

        static Dictionary<string, object> Msg(string role, string content)
        {
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["role"] = role;
            m["content"] = content;
            m["ts"] = DateTime.Now.ToString("yyyy/MM/dd HH:mm");
            return m;
        }

        // ツール実行記録を添えたメッセージ(agentweb と同じ形式で保存する)
        // 注意: 4 引数版へ委譲すると相互再帰で StackOverflow になるため、ここで組み立てる。
        static Dictionary<string, object> Msg(string role, string content, List<object> steps)
        {
            Dictionary<string, object> m = Msg(role, content);
            if (steps != null && steps.Count > 0) m["steps"] = steps;
            return m;
        }

        static Dictionary<string, object> Msg(string role, string content, List<object> steps, string think)
        {
            Dictionary<string, object> m = Msg(role, content);
            if (steps != null && steps.Count > 0) m["steps"] = steps;
            if (think != null && think.Trim().Length > 0) m["think"] = think;
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

        static Dictionary<string, object> AsstWithToolCalls(LlmResp resp)
        {
            Dictionary<string, object> asst = new Dictionary<string, object>();
            asst["role"] = "assistant";
            if (resp.Content != null && resp.Content.Length > 0) asst["content"] = resp.Content;
            asst["tool_calls"] = resp.ToolCalls;
            return asst;
        }

        static Dictionary<string, object> Get(Dictionary<string, object> d, string key)
        {
            if (d != null && d.ContainsKey(key)) return d[key] as Dictionary<string, object>;
            return null;
        }

        static string ThinkOf(StringBuilder sb)
        {
            if (sb == null || sb.Length == 0) return null;
            return sb.ToString();
        }

        // ツール実行の内容を 1 行にまとめる(表示・保存用)
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

        static string ArgStr(Dictionary<string, object> d, string k)
        {
            if (d == null || !d.ContainsKey(k)) return "";
            return Json.AsString(d[k]).Trim();
        }

        // ---------------- コンパクション ----------------

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

        static string Clip(string s, int max)
        {
            if (s == null) return "";
            if (s.Length > max) return s.Substring(0, max) + "...";
            return s;
        }

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

                if (role == "user") sb.Append("ユーザー: ").Append(content).Append("\n");
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
                        foreach (object tc in (List<object>)m["tool_calls"])
                        {
                            Dictionary<string, object> c = tc as Dictionary<string, object>;
                            Dictionary<string, object> fn = (c == null) ? null : (Get(c, "function"));
                            string nm = (fn != null) ? Json.AsString(Get(fn, "name")) : "";
                            string ar = (fn != null) ? Json.AsString(Get(fn, "arguments")) : "";
                            sb.Append("[ツール呼び出し] ").Append(nm).Append(" args=")
                              .Append(Clip(ar, 300)).Append("\n");
                        }
                    }
                    else sb.Append("アシスタント: ").Append(content).Append("\n");
                }
            }
            return sb.ToString();
        }

        static string Summarize(Config cfg, string prevSummary, List<object> span)
        {
            try
            {
                List<object> body = new List<object>();
                body.Add(Msg("system",
                    "あなたはチャット履歴の要約専門のアシスタントです。"
                    + "後で会話の文脈として再利用できるよう、判断・実施した操作とその結果・ユーザーの要望・"
                    + "ファイルパス・重要な事実を漏らさず、日本語で簡潔な箇条書きに要約してください。"
                    + "前置きや「以下が要約です」のような文言は省き、要約本文のみを出力してください。"));

                StringBuilder ub = new StringBuilder();
                if (prevSummary != null && prevSummary.Trim().Length > 0)
                    ub.Append("【前回までの要約】\n").Append(prevSummary).Append("\n\n");
                ub.Append("【今回新たに要約する会話】\n").Append(RenderTranscript(span));
                body.Add(Msg("user", ub.ToString()));

                LlmResp resp = LlmClient.CallOnce(cfg, body, false);   // 要約用はツール不要
                if (resp.Content != null && resp.Content.Trim().Length > 0) return resp.Content.Trim();
                return prevSummary;
            }
            catch
            {
                return prevSummary;
            }
        }

        // トークン超過を検知したら、最近分だけ残し古い分を要約して置換する
        void MaybeCompact(Config cfg, SessionCtx ctx)
        {
            if (ctx.ContextStart >= ctx.Messages.Count) return;
            long threshold = cfg.ContextWindow - cfg.ReserveTokens;
            if (threshold <= 0) return;

            long active = TokensOf(Msg("system", ComposeSystem(cfg.SystemPrompt, ctx.Summary)));
            for (int i = ctx.ContextStart; i < ctx.Messages.Count; i++) active += TokensOf(ctx.Messages[i]);

            if (active <= threshold && ctx.LastTotalTokens <= threshold) return;

            long kept = 0;
            int cut = ctx.Messages.Count;
            for (int i = ctx.Messages.Count - 1; i >= 0; i--)
            {
                long t = TokensOf(ctx.Messages[i]);
                if (kept + t > cfg.KeepRecentTokens) { cut = i; break; }
                kept += t;
                cut = i;
            }
            while (cut < ctx.Messages.Count && !IsValidCutStart(ctx.Messages[cut])) cut++;
            if (cut <= ctx.ContextStart) return;
            if (cut >= ctx.Messages.Count) return;

            List<object> span = ctx.Messages.GetRange(ctx.ContextStart, cut - ctx.ContextStart);
            ConsoleUi.Info("コンテキスト整理(要約)中…");
            ctx.Summary = Summarize(cfg, ctx.Summary, span);
            ctx.ContextStart = cut;
            ctx.LastTotalTokens = 0;
        }

        // ---------------- エージェント実行 ----------------

        // 戻り値: 最終回答。emit 系のコールバックでストリーミング表示する。
        public string RunStream(SessionCtx ctx, string userMessage,
                                Action<SessionCtx> save,
                                Action<string> onContent,
                                Action<string> onReasoning,
                                Action<string, string> onTool,
                                Func<bool> cancelled)
        {
            ctx.Messages.Add(Msg("user", userMessage));
            List<object> turnSteps = new List<object>();
            StringBuilder turnThink = new StringBuilder();

            MaybeCompact(cfg, ctx);
            if (save != null) save(ctx);

            try
            {
                for (int turn = 0; turn < MAX_TURNS; turn++)
                {
                    if (cancelled != null && cancelled())
                    {
                        ctx.Messages.Add(Msg("assistant", "⚠ 処理を中断しました。", turnSteps, ThinkOf(turnThink)));
                        return "⚠ 処理を中断しました。";
                    }

                    List<object> eff = BuildEffectiveMessages(cfg, ctx);
                    LlmResp resp;
                    try
                    {
                        resp = LlmClient.CallStream(cfg, eff, cfg.AllowTools,
                            delegate (string d) { if (onContent != null) onContent(d); },
                            delegate (string d) { turnThink.Append(d); if (onReasoning != null) onReasoning(d); });
                    }
                    catch (LlmCancelledException)
                    {
                        ctx.Messages.Add(Msg("assistant", "⚠ 処理を中断しました。", turnSteps, ThinkOf(turnThink)));
                        return "⚠ 処理を中断しました。";
                    }
                    if (resp.TotalTokens > 0) ctx.LastTotalTokens = resp.TotalTokens;

                    if (resp.ToolCalls != null && resp.ToolCalls.Count > 0)
                    {
                        ctx.Messages.Add(AsstWithToolCalls(resp));
                        int n = Math.Min(resp.ToolCalls.Count, cfg.MaxToolsPerTurn);

                        for (int k = 0; k < n; k++)
                        {
                            if (cancelled != null && cancelled()) break;

                            Dictionary<string, object> call = resp.ToolCalls[k] as Dictionary<string, object>;
                            if (call == null) continue;

                            string id = Json.AsString(Get(call, "id"));
                            if (id.Length == 0) id = "call_" + k;
                            call["id"] = id;

                            Dictionary<string, object> fn = Get(call, "function");
                            string name = "";
                            string argsJson = "{}";
                            if (fn != null)
                            {
                                name = Json.AsString(Get(fn, "name"));
                                object argsVal = fn.ContainsKey("arguments") ? fn["arguments"] : null;
                                if (argsVal is string) argsJson = (string)argsVal;
                                else if (argsVal != null) argsJson = Json.ToString(argsVal);
                            }
                            if (name == null || name.Trim().Length == 0) name = ToolExecutor.InferName(fn, argsJson);
                            if (fn != null && name != null && name.Trim().Length > 0) fn["name"] = name;

                            string detail = ToolDetail(name, argsJson);
                            string step = "ツール実行: " + (name.Length > 0 ? name : "?")
                                + (detail.Length > 0 ? ("  " + detail) : "");
                            turnSteps.Add(step);
                            if (onTool != null) onTool(name, detail);

                            string result;
                            try { result = ToolExecutor.Execute(name, argsJson, cfg); }
                            catch (Exception ex) { result = "{\"error\": \"" + Escape(ex.Message) + "\"}"; }

                            ctx.Messages.Add(ToolMsg(id, name, result));
                        }
                        if (save != null) save(ctx);
                        continue;
                    }

                    string final = resp.Content;
                    ctx.Messages.Add(Msg("assistant", final, turnSteps, ThinkOf(turnThink)));
                    return final;
                }
                throw new Exception("LLM が最終回答を出せませんでした(ツール反復が多すぎます)");
            }
            finally
            {
                if (save != null) save(ctx);
            }
        }

        static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }
    }
}

namespace Tau
{
    // =======================================================================
    //  コンソール表示
    //
    //  ANSI エスケープが使える環境では色を使い、使えなければ素のテキストにする。
    // =======================================================================
    static class ConsoleUi
    {
        static bool color = DetectColor();

        static bool DetectColor()
        {
            try
            {
                if (Environment.GetEnvironmentVariable("NO_COLOR") != null) return false;
                // Windows 10 以降 / Unix 系は概ね ANSI が使える。
                // リダイレクト時は色を付けない。
                if (Console.IsOutputRedirected) return false;
                return true;
            }
            catch { return false; }
        }

        static void Write(string code, string s)
        {
            if (color) Console.Write("\u001b[" + code + "m" + s + "\u001b[0m");
            else Console.Write(s);
        }

        public static void Dim(string s) { Write("90", s); }
        public static void Muted(string s) { Write("37", s); }
        public static void Accent(string s) { Write("36", s); }
        public static void Danger(string s) { Write("31", s); }
        public static void Bold(string s) { Write("1", s); }
        public static void Think(string s) { Write("35", s); }

        public static void Info(string s)
        {
            Console.Write("  ");
            Dim("· " + s);
            Console.Write("\n");
        }

        // ツール実行の 1 行表示
        public static void Tool(string name, string detail)
        {
            Console.Write("  ");
            Dim("▶ ツール実行: ");
            Accent(name + (detail.Length > 0 ? ("  " + detail) : ""));
            Console.Write("\n");
        }

        public static void Banner(Config cfg, SessionCtx ctx, bool resumed)
        {
            Console.WriteLine();
            Bold("tau");
            Muted(" - コンソール AI エージェント");
            Console.WriteLine();
            Console.WriteLine("  Model  : " + cfg.Model
                + (cfg.Models.Count > 1 ? ("  (登録 " + cfg.Models.Count + " 件)") : ""));
            Console.WriteLine("  Tools  : " + (cfg.AllowTools ? "on" : "off"));
            Console.WriteLine("  Config : " + cfg.ConfigPath);
            if (ctx != null)
                Console.WriteLine("  Session: " + ctx.Id + "  (" + ctx.Title + ")"
                    + (resumed ? "  ← 続きから再開" : ""));
            Console.WriteLine();
            Dim("  /help でコマンド一覧、/exit で終了");
            Console.WriteLine();
        }
    }

    // =======================================================================
    //  対話ループ
    // =======================================================================
    class Chat
    {
        readonly Config cfg;
        readonly SessionStore store;
        readonly Agent agent;

        SessionCtx ctx;
        bool cancelled;

        public Chat(Config cfg)
        {
            this.cfg = cfg;
            this.store = new SessionStore();
            this.agent = new Agent(cfg);
        }

        // ---------------- セッションの読み込み ----------------

        // resume=true なら前回のセッションを開き、false なら必ず新規作成する。
        SessionCtx LoadOrCreate(string wantId, bool resume)
        {
            string id = "";
            if (wantId != null && wantId.Trim().Length > 0) id = wantId.Trim();
            // 再開指定のときだけ「最後に開いていたセッション」を使う。
            // /new では使わない(使うと直前と同じセッションが開き直されてしまう)。
            if (id.Length == 0 && resume) id = store.LastSessionId();

            if (id.Length > 0)
            {
                Dictionary<string, object> meta = store.Load(id);
                if (meta != null) return FromMeta(id, meta);
            }

            // 新規作成
            SessionCtx c = new SessionCtx();
            c.Id = store.NewId();
            c.Title = "新規会話";
            store.Save(c.Id, c.Messages, c.Title);
            store.SetLastSessionId(c.Id);
            return c;
        }

        SessionCtx FromMeta(string id, Dictionary<string, object> meta)
        {
            SessionCtx c = new SessionCtx();
            c.Id = id;
            c.Title = meta.ContainsKey("title") ? Json.AsString(meta["title"]) : "新規会話";
            c.Summary = meta.ContainsKey("summary") ? Json.AsString(meta["summary"]) : "";
            c.ContextStart = meta.ContainsKey("contextStart") ? Json.AsInt(meta["contextStart"]) : 0;
            if (meta.ContainsKey("messages") && meta["messages"] is List<object>)
                c.Messages = (List<object>)meta["messages"];
            return c;
        }

        void Save(SessionCtx c)
        {
            store.Save(c.Id, c.Messages, c.Title, c.Summary, c.ContextStart);
        }

        // 最初のユーザー発言からタイトルを作る
        void AutoTitle(SessionCtx c, string userMessage)
        {
            if (c.Title != null && c.Title != "新規会話" && c.Title.Trim().Length > 0) return;
            string t = (userMessage ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (t.Length == 0) return;
            if (t.Length > 40) t = t.Substring(0, 40) + "…";
            c.Title = t;
        }

        // ---------------- 実行 ----------------

        public void Run(string sessionArg)
        {
            ctx = LoadOrCreate(sessionArg, true);
            bool resumed = ctx.Messages.Count > 0;
            ConsoleUi.Banner(cfg, ctx, resumed);

            if (resumed) PrintHistory(ctx);

            while (true)
            {
                ConsoleUi.Accent("> ");
                string line;
                try { line = Console.ReadLine(); }
                catch { break; }

                if (line == null) break;   // Ctrl+D / EOF
                line = line.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("/"))
                {
                    if (HandleCommand(line)) continue;
                    break;   // /exit
                }

                Turn(line);
            }

            Console.WriteLine();
            ConsoleUi.Dim("終了します。セッション: " + ctx.Id);
        }

        // コマンドを処理する。true なら継続、false なら終了。
        bool HandleCommand(string line)
        {
            string[] parts = line.Split(new char[] { ' ' }, 2);
            string cmd = parts[0].ToLowerInvariant();
            string arg = (parts.Length > 1) ? parts[1].Trim() : "";

            switch (cmd)
            {
                case "/exit":
                case "/quit":
                case "/q":
                    return false;

                case "/help":
                case "/?":
                    Program.PrintHelp();
                    return true;

                case "/clear":
                case "/cls":
                    try { Console.Clear(); } catch { }
                    ConsoleUi.Banner(cfg, ctx, false);
                    return true;

                case "/new":
                    ctx = LoadOrCreate("", false);   // 必ず新しいセッションを作る
                    ConsoleUi.Info("新しいセッションを開始しました: " + ctx.Id);
                    return true;

                case "/sessions":
                    ShowSessions();
                    return true;

                case "/open":
                    if (arg.Length == 0) { ConsoleUi.Danger("使い方: /open <セッションID>\n"); return true; }
                    {
                        Dictionary<string, object> meta = store.Load(arg);
                        if (meta == null) { ConsoleUi.Danger("そのセッションは存在しません: " + arg + "\n"); return true; }
                        ctx = FromMeta(arg, meta);
                        store.SetLastSessionId(ctx.Id);
                        ConsoleUi.Info("セッションを開きました: " + ctx.Id + "  (" + ctx.Title + ")");
                        PrintHistory(ctx);
                    }
                    return true;

                case "/model":
                    if (arg.Length == 0)
                    {
                        Console.WriteLine("  現在のモデル: " + cfg.Model);
                        Console.WriteLine("  登録: " + string.Join(", ", cfg.Models.ToArray()));
                        return true;
                    }
                    if (!cfg.Models.Contains(arg))
                    {
                        ConsoleUi.Danger("登録されていないモデルです: " + arg + "\n");
                        Console.WriteLine("  登録: " + string.Join(", ", cfg.Models.ToArray()));
                        return true;
                    }
                    cfg.Model = arg;
                    cfg.Save();
                    ConsoleUi.Info("モデルを切り替えました: " + cfg.Model);
                    return true;

                case "/tools":
                    cfg.AllowTools = !cfg.AllowTools;
                    cfg.Save();
                    ConsoleUi.Info("ツール実行: " + (cfg.AllowTools ? "on" : "off"));
                    return true;

                default:
                    ConsoleUi.Danger("不明なコマンドです: " + cmd + "\n");
                    ConsoleUi.Dim("  /help でコマンド一覧を表示します\n");
                    return true;
            }
        }

        void ShowSessions()
        {
            List<object> list = store.List();
            if (list.Count == 0) { ConsoleUi.Info("保存済みセッションはありません"); return; }
            Console.WriteLine("  保存済みセッション (" + store.DataDir + ")");
            for (int i = 0; i < list.Count; i++)
            {
                Dictionary<string, object> s = list[i] as Dictionary<string, object>;
                if (s == null) continue;
                string id = Json.AsString(s.ContainsKey("id") ? s["id"] : "");
                string title = Json.AsString(s.ContainsKey("title") ? s["title"] : "");
                int count = s.ContainsKey("count") ? Json.AsInt(s["count"]) : 0;
                string mark = (id == ctx.Id) ? "* " : "  ";
                Console.WriteLine(mark + id + "  " + title + "  (" + count + " 件)");
            }
        }

        // 過去の会話を表示する(続きから再開したとき)
        void PrintHistory(SessionCtx c)
        {
            Console.WriteLine();
            ConsoleUi.Dim("---- これまでの会話 ----");
            Console.WriteLine();
            foreach (object o in c.Messages)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (m == null) continue;
                string role = m.ContainsKey("role") ? Json.AsString(m["role"]) : "";
                string content = m.ContainsKey("content") ? Json.AsString(m["content"]) : "";

                if (role == "user")
                {
                    ConsoleUi.Accent("> ");
                    Console.WriteLine(content);
                }
                else if (role == "assistant" && content.Trim().Length > 0)
                {
                    if (m.ContainsKey("think"))
                    {
                        ConsoleUi.Think("  [思考] " + Json.AsString(m["think"]) + "\n");
                        Console.WriteLine();
                    }
                    Console.WriteLine(content);
                    Console.WriteLine();
                }
                else if (role == "tool")
                {
                    string nn = m.ContainsKey("name") ? Json.AsString(m["name"]) : "";
                    ConsoleUi.Dim("  [ツール結果] " + nn + "\n");
                }
            }
            ConsoleUi.Dim("---- ここから続きを入力できます ----");
            Console.WriteLine();
        }

        // 1 ターン実行する
        void Turn(string userMessage)
        {
            cancelled = false;
            AutoTitle(ctx, userMessage);

            bool printedThink = false;
            bool printedContent = false;

            Console.WriteLine();
            try
            {
                string reply = agent.RunStream(ctx, userMessage,
                    delegate (SessionCtx c) { Save(c); },
                    delegate (string d)
                    {
                        // 思考の直後に本文が来たら改行を入れて見やすくする
                        if (printedThink && !printedContent) Console.WriteLine();
                        if (!printedContent) printedContent = true;
                        Console.Write(d);
                    },
                    delegate (string d)
                    {
                        if (!printedThink)
                        {
                            printedThink = true;
                            Console.Write("  ");
                            ConsoleUi.Dim("[思考] ");
                        }
                        ConsoleUi.Think(d);
                    },
                    delegate (string name, string detail) { ConsoleUi.Tool(name, detail); },
                    delegate () { return cancelled; });

                if (!printedContent && reply != null && reply.Length > 0)
                {
                    if (printedThink) Console.WriteLine();
                    Console.WriteLine(reply);
                }
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                string msg;
                WebException wex = ex as WebException;
                if (wex != null) msg = LlmClient.DescribeWebException(wex);
                else msg = ex.Message;
                ConsoleUi.Danger("⚠ エラー: " + msg + "\n");

                // TLS ハンドシェイク失敗は、利用者が対処しやすいよう補足を出す
                if (wex != null && LlmClient.IsTlsFailure(wex))
                {
                    ConsoleUi.Dim("  TLS のハンドシェイクに失敗しました。\n");
                    ConsoleUi.Dim("  ・接続先: " + cfg.ApiBaseUrl + "\n");
                    ConsoleUi.Dim("  ・.NET Framework 4.6.1 以前をターゲットにしていると、\n");
                    ConsoleUi.Dim("    既定が TLS 1.0 のままになり、TLS 1.2 以上を要求する API に\n");
                    ConsoleUi.Dim("    接続できません。tau は TLS 1.2 を明示して再試行済みです。\n");
                    ConsoleUi.Dim("  ・プロキシ経由の場合は、プロキシ側の TLS 対応も確認してください。\n");
                }
            }
        }
    }
}
