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
    //  設定の編集 (プロンプトから)
    //
    //   /config            現在の設定を表示
    //   /config edit       対話形式で 1 項目ずつ変更する
    //   /set <key> <value> 1 項目だけ変更する
    //
    //  agentweb の設定画面に相当するものをコンソールで提供する。
    // =======================================================================
    static class Settings
    {
        // 編集できる項目の定義
        public class Field
        {
            public string Key;        // 設定ファイルのキー
            public string Label;      // 表示名
            public string Kind;       // "string" / "int" / "bool" / "secret" / "text" / "choice"
            public string Help;       // 補足
            public string[] Choices;  // Kind が "choice" のときの選択肢
        }

        public static readonly Field[] Fields = new Field[]
        {
            new Field { Key = "llmApiUrl", Label = "API URL", Kind = "string",
                Help = "OpenAI-compatible /chat/completions URL" },
            new Field { Key = "llmApiKey", Label = "API Key", Kind = "secret",
                Help = "API key (masked when displayed)" },
            new Field { Key = "model", Label = "Model", Kind = "string",
                Help = "Model name to use" },
            new Field { Key = "allowTools", Label = "Allow Tools", Kind = "bool",
                Help = "Allow tool execution (true/false)" },
            new Field { Key = "emitReasoning", Label = "Emit Reasoning", Kind = "bool",
                Help = "Show reasoning (true/false)" },
            new Field { Key = "contextWindow", Label = "Context Window", Kind = "int",
                Help = "Context window (estimated tokens)" },
            new Field { Key = "reserveTokens", Label = "Reserve Tokens", Kind = "int",
                Help = "Tokens reserved for the reply" },
            new Field { Key = "keepRecentTokens", Label = "Keep Recent Tokens", Kind = "int",
                Help = "Recent tokens kept without summarising" },
            new Field { Key = "maxToolsPerTurn", Label = "Max Tools Per Turn", Kind = "int",
                Help = "Maximum tools executed per turn" },
            new Field { Key = "toolTimeoutSec", Label = "Tool Timeout (sec)", Kind = "int",
                Help = "Tool execution timeout (seconds)" },
            new Field { Key = "useSystemProxy", Label = "Use System Proxy", Kind = "bool",
                Help = "Use the OS system proxy (true/false)" },
            new Field { Key = "proxyUrl", Label = "Proxy URL", Kind = "string",
                Help = "Proxy URL (empty means no proxy)" },
            new Field { Key = "proxyUser", Label = "Proxy User", Kind = "string",
                Help = "Proxy authentication user" },
            new Field { Key = "proxyPass", Label = "Proxy Pass", Kind = "secret",
                Help = "Proxy authentication password" },
            new Field { Key = "systemPrompt", Label = "System Prompt", Kind = "text",
                Help = "System prompt (multi-line)" },
            new Field { Key = "inputEncoding", Label = "Input Encoding", Kind = "choice",
                Help = "Console input encoding: shift-jis (cp932) / utf-8 / default",
                Choices = new string[] { "shift-jis", "utf-8", "default" } },
        };

        public static Field Find(string key)
        {
            if (key == null) return null;
            string k = key.Trim().ToLowerInvariant();
            for (int i = 0; i < Fields.Length; i++)
                if (Fields[i].Key.ToLowerInvariant() == k) return Fields[i];
            return null;
        }

        // 値を表示用の文字列にする
        public static string Display(Config cfg, Field f)
        {
            Dictionary<string, object> m = cfg.ToDict();
            if (!m.ContainsKey(f.Key)) return "";
            object v = m[f.Key];

            if (f.Kind == "secret")
            {
                string sv = Json.AsString(v);
                if (sv.Length == 0) return "(not set)";
                if (sv.Length <= 8) return new string('*', sv.Length);
                return sv.Substring(0, 3) + "..." + new string('*', Math.Min(8, sv.Length - 3));
            }
            if (f.Kind == "text")
            {
                string t = Json.AsString(v);
                if (t.Length > 80) t = t.Substring(0, 80) + "…";
                return t.Replace("\n", " / ");
            }
            return Json.AsString(v);
        }

        // 値を適用する。成功なら true。
        public static bool Apply(Config cfg, Field f, string raw)
        {
            if (raw == null) raw = "";
            switch (f.Kind)
            {
                case "bool":
                    {
                        string s = raw.Trim().ToLowerInvariant();
                        bool b;
                        if (s == "true" || s == "1" || s == "on" || s == "yes" || s == "y") b = true;
                        else if (s == "false" || s == "0" || s == "off" || s == "no" || s == "n") b = false;
                        else return false;
                        if (f.Key == "allowTools") cfg.AllowTools = b;
                        else if (f.Key == "emitReasoning") cfg.EmitReasoning = b;
                        else if (f.Key == "useSystemProxy") cfg.UseSystemProxy = b;
                        return true;
                    }
                case "choice":
                    {
                        if (f.Choices == null || f.Choices.Length == 0) return false;
                        string s2 = (raw ?? "").Trim().ToLowerInvariant().Replace("_", "-");
                        for (int i = 0; i < f.Choices.Length; i++)
                        {
                            if (f.Choices[i].ToLowerInvariant() == s2)
                            {
                                if (f.Key == "inputEncoding") cfg.InputEncoding = f.Choices[i];
                                return true;
                            }
                        }
                        // よく使う別名を受け付ける
                        Encoding enc = Program.ResolveEncoding(raw);
                        if (enc != null && f.Key == "inputEncoding")
                        {
                            cfg.InputEncoding = (enc.CodePage == 65001) ? "utf-8" : "shift-jis";
                            return true;
                        }
                        if (f.Key == "inputEncoding" && s2 == "default")
                        {
                            cfg.InputEncoding = "default";
                            return true;
                        }
                        return false;
                    }
                case "int":
                    {
                        int n;
                        if (!int.TryParse(raw.Trim(), out n)) return false;
                        if (n < 0) return false;
                        if (f.Key == "contextWindow") cfg.ContextWindow = n;
                        else if (f.Key == "reserveTokens") cfg.ReserveTokens = n;
                        else if (f.Key == "keepRecentTokens") cfg.KeepRecentTokens = n;
                        else if (f.Key == "maxToolsPerTurn") cfg.MaxToolsPerTurn = n;
                        else if (f.Key == "toolTimeoutSec") cfg.ToolTimeoutSec = n;
                        return true;
                    }
                default:
                    {
                        string s = raw;
                        if (f.Kind != "text") s = s.Trim();
                        if (f.Key == "llmApiUrl") cfg.ApiBaseUrl = s;
                        else if (f.Key == "llmApiKey") cfg.SecretKey = s;
                        else if (f.Key == "model")
                        {
                            if (s.Length == 0) return false;
                            cfg.Model = s;
                        }
                        else if (f.Key == "proxyUrl") cfg.ProxyUrl = s;
                        else if (f.Key == "proxyUser") cfg.ProxyUser = s;
                        else if (f.Key == "proxyPass") cfg.ProxyPass = s;
                        else if (f.Key == "systemPrompt")
                        {
                            if (s.Trim().Length == 0) return false;
                            cfg.SystemPrompt = s;
                        }
                        return true;
                    }
            }
        }

        // 現在の設定を表示する
        public static void Show(Config cfg)
        {
            Console.WriteLine("  Config file: " + cfg.ConfigPath);
            Console.WriteLine();
            for (int i = 0; i < Fields.Length; i++)
            {
                Field f = Fields[i];
                Console.WriteLine("    " + Pad(f.Key) + " = " + Display(cfg, f));
            }
            Console.WriteLine();
            ConsoleUi.Dim("  Change: /set <key> <value>   or   /config edit\n");
        }

        static string Pad(string s)
        {
            while (s.Length < 18) s += " ";
            return s;
        }

        // 対話形式で 1 項目ずつ変更する
        public static void Edit(Config cfg)
        {
            Console.WriteLine();
            ConsoleUi.Bold("  Edit settings");
            Console.WriteLine();
            for (int i = 0; i < Fields.Length; i++)
            {
                Field f = Fields[i];
                Console.WriteLine("    " + (i + 1).ToString().PadLeft(2) + ". "
                    + Pad(f.Key) + " = " + Display(cfg, f));
                ConsoleUi.Dim("        " + f.Help + "\n");
                if (f.Kind == "choice" && f.Choices != null)
                    ConsoleUi.Dim("        choices: " + string.Join(" / ", f.Choices) + "\n");
            }
            Console.WriteLine();
            ConsoleUi.Dim("  Enter the number of the field to change (Enter to cancel): ");
            string pick = Program.SanitizeInput(LineEditor.ReadLine("")).Trim();
            if (pick.Length == 0) { ConsoleUi.Dim("  Cancelled\n"); return; }

            // 数字だけを取り出す(前後の空白や制御文字が混ざっても許容する)
            string digits = "";
            for (int i = 0; i < pick.Length; i++)
                if (pick[i] >= '0' && pick[i] <= '9') digits += pick[i];
                else if (pick[i] != ' ' && pick[i] != '\t' && pick[i] != '\r'
                         && pick[i] != '\n' && pick[i] != '\0') { digits = ""; break; }
            if (digits.Length == 0) digits = pick.Trim();

            int idx;
            if (!int.TryParse(digits, out idx) || idx < 1 || idx > Fields.Length)
            {
                ConsoleUi.Danger("  Invalid number\n");
                if (LlmClient.DebugOn)
                {
                    ConsoleUi.Dim("  [pick] length=" + pick.Length + " bytes="
                        + Encoding.UTF8.GetByteCount(pick) + "\n");
                    StringBuilder codes = new StringBuilder();
                    for (int i = 0; i < pick.Length && i < 20; i++)
                        codes.Append("U+").Append(((int)pick[i]).ToString("X4")).Append(' ');
                    ConsoleUi.Dim("  [pick] chars: " + codes + "\n");
                    ConsoleUi.Dim("  [pick] range: 1-" + Fields.Length + "\n");
                }
                return;
            }

            Field field = Fields[idx - 1];
            Console.WriteLine();
            Console.WriteLine("  " + field.Key + "  (" + field.Help + ")");
            Console.WriteLine("  Current value: " + Display(cfg, field));

            string value;
            if (field.Kind == "text")
            {
                ConsoleUi.Dim("  Enter the new value (a blank line finishes; it cannot be empty):\n");
                LineEditor.Result mr = LineEditor.ReadMultiLine("> ");
                if (mr.Cancelled) { ConsoleUi.Dim("  Cancelled\n"); return; }
                value = mr.Text;
                if (value.Trim().Length == 0) { ConsoleUi.Dim("  Cancelled\n"); return; }
            }
            else if (field.Kind == "choice")
            {
                ConsoleUi.Dim("  Enter the new value (Enter to cancel):\n");
                for (int i = 0; i < field.Choices.Length; i++)
                    ConsoleUi.Dim("    " + (i + 1) + ". " + field.Choices[i] + "\n");
                ConsoleUi.Dim("  number or name: ");
                string raw = LineEditor.ReadLine("");
                if (raw == null) { ConsoleUi.Dim("  Cancelled\n"); return; }

                int nulCount = Program.CountNul(raw);
                if (nulCount > 0)
                {
                    ConsoleUi.Danger("  ⚠ The input became NUL and was lost.\n");
                    ConsoleUi.Info("input encoding: " + Program.SafeInputEncoding());
                    return;
                }
                raw = raw.Trim();

                // 番号でも名前でも受け付ける
                int pickNo;
                if (int.TryParse(raw, out pickNo) && pickNo >= 1 && pickNo <= field.Choices.Length)
                    raw = field.Choices[pickNo - 1];

                value = raw;
                if (value.Length == 0) { ConsoleUi.Dim("  Cancelled\n"); return; }
            }
            else
            {
                ConsoleUi.Dim("  Enter the new value (Enter to cancel): ");
                string raw = LineEditor.ReadLine("");
                if (raw == null) { ConsoleUi.Dim("  Cancelled\n"); return; }

                int nulCount = Program.CountNul(raw);
                if (nulCount > 0)
                {
                    string cleaned = Program.SanitizeInput(raw);
                    if (cleaned.Length == 0)
                    {
                        ConsoleUi.Danger("  ⚠ The input became NUL and was lost ("
                            + raw.Length + " characters).\n");
                        ConsoleUi.Dim("    The console input encoding does not match.\n");
                        ConsoleUi.Dim("    Try `chcp 65001` before starting tau,"
                            + " or use Windows Terminal.\n");
                        ConsoleUi.Info("input encoding: " + Program.SafeInputEncoding());
                        return;
                    }
                    ConsoleUi.Danger("  ⚠ " + nulCount
                        + " character(s) of the input became NUL and were dropped.\n");
                    raw = cleaned;
                }

                value = raw;
                if (value.Length == 0 && field.Kind != "string" && field.Kind != "secret")
                {
                    ConsoleUi.Dim("  Cancelled\n");
                    return;
                }
            }

            if (!Apply(cfg, field, value))
            {
                ConsoleUi.Danger("  Invalid value: " + value + "\n");
                return;
            }
            cfg.Save();
            ConsoleUi.Info(field.Key + " = " + Display(cfg, field) + "  (updated)");
        }
    }
}
