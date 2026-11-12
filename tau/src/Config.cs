// ---------------------------------------------------------------------------
//  tau - コンソール上で動く AI エージェント
//
//  agentweb (Web UI 版) とは独立した実装です。
//  コードは agentweb を参考にしていますが、共通化はしていません。
//  HTTP サーバ / Web UI を持たず、標準入出力だけで対話します。
//
//  ビルド: csc.exe /nologo /out:tau.exe src\*.cs
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Tau
{
    // =======================================================================
    //  設定
    //
    //  agentweb とは独立した設定ファイルを持つ。
    //  優先度: 環境変数 < tau.json
    //  保存先: %APPDATA%\tau\tau.json (Linux では ~/.config/tau/tau.json)
    // =======================================================================
    class Config
    {
        public string ApiBaseUrl;          // OpenAI 互換 /chat/completions の URL
        public string SecretKey;           // API キー
        public string Model;               // 使用するモデル名

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
        // コンソール入力のエンコーディング。
        //   "utf-8"     … UTF-8 (CP65001)
        //   "shift-jis" … Shift-JIS (CP932)。Windows の既定で、日本語入力が安定する
        //   "default"   … コンソール/OS の既定に任せる(変更しない)
        public string InputEncoding;
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


            InputEncoding = Env("TAU_INPUT_ENCODING", "shift-jis");

            SystemPrompt = Env("AI_SYSTEM_PROMPT",
                "You are an AI agent that runs in the console."
                + " Answer the user concisely."
                + "\n\nThis program runs on the user's local machine, so you can actually"
                + " run tools when instructed."
                + " For requests that need investigating or changing files, or running commands,"
                + " use the tools (shell/read_file/write_file/list_dir) and verify the result"
                + " yourself before reporting back."
                + " Ask the user before performing dangerous or irreversible operations."
                + "\nOutput is shown in a terminal, so keep Markdown decoration minimal"
                + " and reply in readable plain text.");

            ConfigPath = (configPathArg != null && configPathArg.Trim().Length > 0)
                ? configPathArg.Trim()
                : Env("TAU_CONFIG", "");

            ApplyConfigFile();
        }

        // ---------------- 設定ファイル ----------------

        // tau 独自の設定ファイル
        public static string DefaultConfigPath()
        {
            return Path.Combine(TauDataDir(), "tau.json");
        }

        // tau 独自のデータディレクトリ(設定とセッションの保存先)。
        // agentweb とは共有しない。
        public static string TauDataDir()
        {
            return AppDirUnder("tau");
        }

        static string AppDirUnder(string name)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData))
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(home)) home = ".";
                return Path.Combine(home, ".config", name);
            }
            return Path.Combine(appData, name);
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
                Console.WriteLine("[config] failed to load the config file: " + ex.Message);
            }
        }

        void ApplyDict(Dictionary<string, object> m)
        {
            if (m == null) return;

            if (Has(m, "llmApiUrl")) ApiBaseUrl = Json.AsString(Get(m, "llmApiUrl"));
            if (Has(m, "llmApiKey")) SecretKey = Json.AsString(Get(m, "llmApiKey"));
            if (Has(m, "model")) Model = Json.AsString(Get(m, "model"));
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
            if (Has(m, "inputEncoding")) InputEncoding = Json.AsString(Get(m, "inputEncoding"));
        }

        public Dictionary<string, object> ToDict()
        {
            Dictionary<string, object> m = new Dictionary<string, object>();
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
            m["proxyUrl"] = ProxyUrl;
            m["proxyUser"] = ProxyUser;
            m["proxyPass"] = ProxyPass;
            m["useSystemProxy"] = UseSystemProxy;
            m["systemPrompt"] = SystemPrompt;
            m["inputEncoding"] = InputEncoding;
            return m;
        }

        // 設定をファイルへ保存する(/model での切り替えを次回も維持するため)
        //
        // 手で追記した項目などを失わないよう、既存ファイルの内容を読み込んでから
        // 上書きする(tau が扱わないキーはそのまま引き継ぐ)。
        public void Save()
        {
            try
            {
                Dictionary<string, object> m = ToDict();

                // 既存ファイルのうち tau が扱わないキーを引き継ぐ
                // (手で追記した設定を失わないため)
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

                // 廃止したキーは保存時に取り除く(models は tau では使わない)
                m.Remove("models");

                string dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(ConfigPath, Json.ToString(m), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Console.WriteLine("[config] failed to save: " + ex.Message);
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
