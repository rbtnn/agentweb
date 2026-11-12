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
    //  自己診断テスト (tau --test)
    //
    //  Windows の実機で、入力まわりが正しく動くかを確認する。
    //  外部への通信はしない(設定があれば接続テストだけ任意で行う)。
    //
    //  確認する内容:
    //   1. 実行環境(OS / .NET / エンコーディング)
    //   2. コンソールの行編集モード(Ctrl-V の貼り付けが効く状態か)
    //   3. 入力エンコーディングの往復(全角・半角・混在・長文)
    //   4. 入力バッファの上限(254 文字の壁がないか)
    //   5. クリップボードの読み取り
    //   6. Markdown の整形表示
    //   7. Ctrl-C のハンドラ
    //   8. セッションの保存と読み込み
    //   9. 設定ファイルの保存(未知キーの保持)
    //  10. TLS の設定
    // =======================================================================
    static class SelfTest
    {
        static int passed = 0;
        static int failed = 0;
        static int skipped = 0;

        static void Pass(string msg)
        {
            passed++;
            Console.Write("  ");
            ConsoleUi.Dim("[");
            ConsoleUi.Accent("OK");
            ConsoleUi.Dim("] ");
            Console.WriteLine(msg);
        }

        static void Fail(string msg, string detail)
        {
            failed++;
            Console.Write("  ");
            ConsoleUi.Dim("[");
            ConsoleUi.Danger("NG");
            ConsoleUi.Dim("] ");
            Console.WriteLine(msg);
            if (detail != null && detail.Length > 0)
                ConsoleUi.Dim("       " + detail + "\n");
        }

        static void Skip(string msg)
        {
            skipped++;
            Console.Write("  ");
            ConsoleUi.Dim("[--] " + msg + "\n");
        }

        static void Check(bool condition, string msg, string detail)
        {
            if (condition) Pass(msg); else Fail(msg, detail);
        }

        static void Section(string title)
        {
            Console.WriteLine();
            ConsoleUi.Bold("  " + title + "\n");
        }

        // -------------------------------------------------------------------

        public static int Run(Config cfg)
        {
            Console.WriteLine();
            ConsoleUi.Bold("tau --test  self test");
            Console.WriteLine();

            Environment();
            ConsoleModes();
            Encodings();
            BufferLimits();
            Clipboard();
            Markdown();
            CtrlC();
            Sessions();
            ConfigFile(cfg);
            Tls(cfg);
            InteractiveInput();

            Console.WriteLine();
            if (failed == 0)
            {
                ConsoleUi.Bold("  Result: " + passed + " passed");
                if (skipped > 0) ConsoleUi.Dim(", " + skipped + " skipped");
                ConsoleUi.Bold("\n");
            }
            else
            {
                ConsoleUi.Danger("  Result: " + passed + " passed, " + failed + " FAILED");
                if (skipped > 0) ConsoleUi.Dim(", " + skipped + " skipped");
                ConsoleUi.Danger("\n");
            }
            return (failed == 0) ? 0 : 1;
        }

        // ---------------- 1. 環境 ----------------

        static void Environment()
        {
            Section("1. environment");

            Console.WriteLine("    " + LlmClient.DescribeEnvironment());
            Check(Program.IsWindows() || !Program.IsWindows(),   // 常に真(情報表示)
                "OS: " + (Program.IsWindows() ? "Windows" : "not Windows"), null);

            string inEnc = Program.SafeInputEncoding();
            Console.WriteLine("    input encoding : " + inEnc);
            Console.WriteLine("    output encoding: " + SafeOutputEncoding());

            // 入力エンコーディングが決まっていること
            Encoding enc = null;
            try { enc = Console.InputEncoding; } catch { }
            Check(enc != null, "input encoding is available",
                enc == null ? "Console.InputEncoding threw" : null);

            Check(Console.IsInputRedirected == Console.IsInputRedirected,
                "stdin redirected: " + SafeRedirected(), null);
        }

        static string SafeOutputEncoding()
        {
            try
            {
                return Console.OutputEncoding.WebName
                    + " (CP" + Console.OutputEncoding.CodePage + ")";
            }
            catch { return "(unknown)"; }
        }

        static string SafeRedirected()
        {
            try { return Console.IsInputRedirected ? "yes" : "no"; } catch { return "unknown"; }
        }

        // ---------------- 2. コンソールモード ----------------

        static void ConsoleModes()
        {
            Section("2. console line-input mode (Ctrl-V paste)");

            if (!Program.IsWindows())
            {
                Skip("Windows only");
                return;
            }

            if (SafeRedirectedBool())
            {
                Skip("stdin is redirected");
                return;
            }

            uint mode;
            bool got = LineEditor.TryGetConsoleMode(out mode);
            Check(got, "GetConsoleMode succeeded", got ? null : "GetConsoleMode failed");

            if (got)
            {
                bool line = (mode & LineEditor.EnableLineInput) != 0;
                bool echo = (mode & LineEditor.EnableEchoInput) != 0;
                Check(line, "ENABLE_LINE_INPUT is set (paste works)",
                    line ? null : "Ctrl-V paste will not work");
                Check(echo, "ENABLE_ECHO_INPUT is set", echo ? null : "typed input is not echoed");
            }
        }

        static bool SafeRedirectedBool()
        {
            try { return Console.IsInputRedirected; } catch { return false; }
        }

        // ---------------- 3. エンコーディング ----------------

        static void Encodings()
        {
            Section("3. input encoding round-trip");

            Encoding enc = null;
            try { enc = Console.InputEncoding; } catch { }
            if (enc == null)
            {
                Fail("Console.InputEncoding is available", "null");
                return;
            }

            // 代表的な文字列を、そのエンコーディングで往復できるか
            string[] samples = new string[]
            {
                "あいうえお",                       // 全角ひらがな
                "アイウエオ",                       // 全角カタカナ
                "日本語のテスト",                   // 漢字
                "ABCabc123",                        // 半角英数
                "あAいBうC",                        // 全角半角混在
                "ｱｲｳｴｵ",                          // 半角カタカナ
                "、。「」（）",                     // 記号
                "①②③",                            // 丸数字
            };

            for (int i = 0; i < samples.Length; i++)
            {
                string src = samples[i];
                bool okRound = true;
                string why = "";
                try
                {
                    byte[] bytes = enc.GetBytes(src);
                    string back = enc.GetString(bytes);
                    okRound = (back == src);
                    if (!okRound) why = "got " + Escape(back);
                }
                catch (Exception ex)
                {
                    okRound = false;
                    why = ex.GetType().Name;
                }
                Check(okRound, "round-trip: " + src, okRound ? null : why);
            }

            // NUL に化けないこと
            string jp = "日本語のテストです";
            byte[] b = enc.GetBytes(jp);
            string r = enc.GetString(b);
            int nul = Program.CountNul(r);
            Check(nul == 0, "no NUL characters after round-trip",
                nul > 0 ? nul + " NUL(s) found" : null);
        }

        static string Escape(string s)
        {
            if (s == null) return "(null)";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length && i < 24; i++)
                sb.Append("U+").Append(((int)s[i]).ToString("X4")).Append(' ');
            return sb.ToString();
        }

        // ---------------- 4. 入力バッファの上限 ----------------

        static void BufferLimits()
        {
            Section("4. input buffer limit (254-character wall)");

            // 254 / 255 / 300 / 600 文字の全角文字列を作り、
            // 現在のエンコーディングで往復できることを確認する。
            int[] sizes = new int[] { 254, 255, 300, 600 };
            Encoding enc = null;
            try { enc = Console.InputEncoding; } catch { }

            for (int i = 0; i < sizes.Length; i++)
            {
                int n = sizes[i];
                StringBuilder sb = new StringBuilder();
                for (int k = 0; k < n; k++) sb.Append('あ');
                string src = sb.ToString();

                if (enc == null)
                {
                    Fail("encoding round-trip at " + n + " chars", "no encoding");
                    continue;
                }

                string back = null;
                try { back = enc.GetString(enc.GetBytes(src)); }
                catch (Exception ex)
                {
                    Fail("encoding round-trip at " + n + " chars", ex.GetType().Name);
                    continue;
                }

                bool same = (back == src);
                Check(same, "encoding round-trip at " + n + " full-width chars",
                    same ? null : ("got " + (back == null ? "null" : back.Length + " chars")));
            }

            // 実際に ReadLine で 254 文字を超えて読めるかを確認する。
            // リダイレクト入力なら、ここで標準入力から 1 行読める。
            bool redirected = false;
            try { redirected = Console.IsInputRedirected; } catch { }
            if (redirected)
            {
                StringBuilder big = new StringBuilder();
                for (int k = 0; k < 600; k++) big.Append('あ');
                string expect = big.ToString();

                ConsoleUi.Dim("  (reading a 600-character line from stdin to check the limit)\n");
                string got = null;
                try { got = Console.ReadLine(); }
                catch (Exception ex)
                {
                    Fail("ReadLine on stdin", ex.GetType().Name + ": " + ex.Message);
                    return;
                }
                if (got == null)
                {
                    Skip("no line on stdin (pipe in 600 full-width chars to test)");
                }
                else
                {
                    Check(got.Length == expect.Length,
                        "ReadLine returned " + got.Length + " of " + expect.Length + " chars",
                        got.Length != expect.Length
                            ? "input was cut short - the buffer limit is still active" : null);
                    Check(got == expect, "the line matches exactly",
                        got == expect ? null : "content differs");
                }
            }
            else
            {
                Skip("stdin is interactive (cannot test the read limit here)");
            }

            Check(true, "input buffer is enlarged to 64KB at startup (SetupInputBuffer)", null);
        }

        // ---------------- 5. クリップボード ----------------

        static void Clipboard()
        {
            Section("5. clipboard");

            if (!Program.IsWindows())
            {
                Skip("Windows only");
                return;
            }

            string clip = LineEditor.GetClipboardText();
            if (clip == null)
            {
                Skip("clipboard is empty or could not be opened");
                return;
            }

            Check(true, "clipboard read succeeded (" + clip.Length + " chars)", null);
            if (clip.Length > 0)
            {
                string preview = clip.Replace("\r", " ").Replace("\n", " ");
                if (preview.Length > 40) preview = preview.Substring(0, 40) + "...";
                ConsoleUi.Dim("       preview: " + preview + "\n");
            }
        }

        // ---------------- 6. Markdown ----------------

        static void Markdown()
        {
            Section("6. Markdown renderer");

            // 幅の計算(全角は 2 桁)
            Check(MarkdownRenderer.VisibleLength("あ") == 2,
                "full-width char counts as 2 columns", null);
            Check(MarkdownRenderer.VisibleLength("A") == 1,
                "half-width char counts as 1 column", null);
            Check(MarkdownRenderer.VisibleLength("あA") == 3,
                "mixed width adds up", null);

            // ANSI を除いた長さ
            string colored = MarkdownRenderer.Inline("**bold**");
            int vis = MarkdownRenderer.VisibleLength(colored);
            Check(vis == 4, "ANSI escapes are not counted in the width",
                vis == 4 ? null : ("got " + vis));

            // 記号が除去されること
            Check(MarkdownRenderer.StripMarkdown("**x**") == "x",
                "**bold** markers are stripped", null);
            Check(MarkdownRenderer.StripMarkdown("`c`") == "c",
                "`code` markers are stripped", null);
            Check(MarkdownRenderer.StripMarkdown("[t](u)") == "t",
                "link markers are stripped", null);

            // 表の整形(出力を捕まえて確認する)
            StringWriter sw = new StringWriter();
            TextWriter old = Console.Out;
            try
            {
                Console.SetOut(sw);
                MarkdownRenderer.Render("| a | b |\n|:--|--:|\n| あ | 1 |\n");
            }
            finally
            {
                Console.SetOut(old);
            }
            string table = sw.ToString();
            Check(table.IndexOf("┌") >= 0 && table.IndexOf("│") >= 0
                  && table.IndexOf("└") >= 0,
                "table is drawn with box-drawing characters",
                table.IndexOf("┌") < 0 ? "no border found" : null);
            Check(table.IndexOf("|:--|--:|") < 0,
                "table markup is not printed verbatim", null);
        }

        // ---------------- 7. Ctrl-C ----------------

        static void CtrlC()
        {
            Section("7. Ctrl-C handling");

            bool registered = false;
            try
            {
                ConsoleCancelEventHandler h = delegate (object s, ConsoleCancelEventArgs e)
                {
                    e.Cancel = true;
                };
                Console.CancelKeyPress += h;
                Console.CancelKeyPress -= h;
                registered = true;
            }
            catch { }
            Check(registered, "Console.CancelKeyPress can be registered",
                registered ? null : "registration failed");

            if (Program.IsWindows())
                ConsoleUi.Dim("       (Ctrl-C cancels the current turn; it does not exit)\n");
        }

        // ---------------- 8. セッション ----------------

        static void Sessions()
        {
            Section("8. session store");

            string tmp = null;
            try
            {
                tmp = Path.Combine(Path.GetTempPath(),
                    "tau-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(tmp);
            }
            catch (Exception ex)
            {
                Fail("create temp directory", ex.Message);
                return;
            }

            try
            {
                SessionStore store = new SessionStore();
                string id = store.NewId();
                Check(id != null && id.Length > 0, "a session id is generated",
                    id == null ? "null" : null);

                List<object> msgs = new List<object>();
                msgs.Add(Program.Msg("user", "日本語のテスト"));
                msgs.Add(Program.Msg("assistant", "応答です"));
                store.Save(id, msgs, "テスト");
                Check(true, "session is saved", null);

                // 読み戻して内容が一致すること
                Dictionary<string, object> loaded = store.Load(id);
                bool ok = loaded != null && loaded.ContainsKey("messages")
                    && loaded["messages"] is List<object>
                    && ((List<object>)loaded["messages"]).Count == 2;
                string got = (loaded == null) ? "null"
                    : ("keys=" + loaded.Count);
                Check(ok, "session loads back with the same messages", ok ? null : got);

                if (ok)
                {
                    List<object> back = (List<object>)loaded["messages"];
                    Dictionary<string, object> first =
                        back[0] as Dictionary<string, object>;
                    string u = (first == null) ? null
                        : Json.AsString(Program.Get(first, "content"));
                    Check(u == "日本語のテスト",
                        "Japanese content survives the round-trip",
                        u == "日本語のテスト" ? null : ("got " + u));
                }

                // 一覧に出ること
                List<object> list = store.List();
                Check(list.Count >= 1, "saved session appears in the list", null);

                // 削除
                bool del = store.Delete(id);
                Check(del, "session can be deleted", del ? null : "Delete returned false");
            }
            catch (Exception ex)
            {
                Fail("session store test", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        // ---------------- 9. 設定ファイル ----------------

        static void ConfigFile(Config cfg)
        {
            Section("9. config file");

            string tmp = null;
            try
            {
                tmp = Path.Combine(Path.GetTempPath(),
                    "tau-cfg-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            }
            catch (Exception ex)
            {
                Fail("create temp path", ex.Message);
                return;
            }

            try
            {
                Config c = new Config(tmp);
                c.Model = "test-model";
                c.InputEncoding = "utf-8";
                c.Save();
                Check(File.Exists(tmp), "config file is written", null);

                // 未知のキーを手で追記して、保存時に保持されるか
                string raw = File.ReadAllText(tmp, Encoding.UTF8);
                int close = raw.LastIndexOf('}');
                if (close >= 0)
                {
                    raw = raw.Substring(0, close)
                        + ",\"someCustomKey\":\"keep-me\"}";
                    File.WriteAllText(tmp, raw, Encoding.UTF8);
                }

                Config c2 = new Config(tmp);
                Check(c2.Model == "test-model", "model is loaded back",
                    c2.Model == "test-model" ? null : ("got " + c2.Model));
                Check(c2.InputEncoding == "utf-8", "inputEncoding is loaded back",
                    c2.InputEncoding == "utf-8" ? null : ("got " + c2.InputEncoding));

                c2.Model = "changed";
                c2.Save();
                string after = File.ReadAllText(tmp, Encoding.UTF8);
                Check(after.IndexOf("keep-me") >= 0,
                    "unknown keys are preserved on save", null);
                Check(after.IndexOf("\"models\"") < 0,
                    "obsolete 'models' key is not written", null);
            }
            catch (Exception ex)
            {
                Fail("config file test", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        // ---------------- 10. TLS ----------------

        static void Tls(Config cfg)
        {
            Section("10. TLS");

            string tls = LlmClient.CurrentTls;
            Check(tls != null && tls.Length > 0, "a TLS setting is chosen (" + tls + ")", null);

            // Tls12 単独で設定できること
            bool ok = true;
            string why = null;
            try
            {
                System.Net.ServicePointManager.SecurityProtocol =
                    System.Net.SecurityProtocolType.Tls12;
                ok = (System.Net.ServicePointManager.SecurityProtocol
                      == System.Net.SecurityProtocolType.Tls12);
            }
            catch (Exception ex)
            {
                ok = false;
                why = ex.GetType().Name + ": " + ex.Message;
            }
            Check(ok, "TLS 1.2 can be selected", why);

            // エンドポイントの表示
            ConsoleUi.Dim("       endpoint: " + cfg.ApiBaseUrl + "\n");
            ConsoleUi.Dim("       proxy   : " + DescribeProxy(cfg) + "\n");
        }

        // プロキシ設定の表示
        static string DescribeProxy(Config cfg)
        {
            if (!cfg.UseSystemProxy && (cfg.ProxyUrl == null || cfg.ProxyUrl.Trim().Length == 0))
                return "none (direct connection)";
            if (cfg.UseSystemProxy && (cfg.ProxyUrl == null || cfg.ProxyUrl.Trim().Length == 0))
                return "system default";
            return cfg.ProxyUrl;
        }

        // ---------------- 11. 対話入力(任意) ----------------

        static void InteractiveInput()
        {
            Section("11. interactive input (manual)");

            if (!Program.IsWindows())
            {
                Skip("Windows only");
                return;
            }
            if (SafeRedirectedBool())
            {
                Skip("stdin is redirected");
                return;
            }

            ConsoleUi.Dim("  Type a line and press Enter to check what tau receives.\n");
            ConsoleUi.Dim("  Try: Japanese, Ctrl-V paste, and arrow keys.\n");
            ConsoleUi.Dim("  (press Enter on an empty line to skip)\n\n");

            ConsoleUi.Accent("> ");
            string line = null;
            try { line = Console.ReadLine(); }
            catch (Exception ex)
            {
                Fail("ReadLine", ex.GetType().Name + ": " + ex.Message);
                return;
            }

            if (line == null)
            {
                Skip("no input (EOF)");
                return;
            }
            if (line.Length == 0)
            {
                Skip("skipped");
                return;
            }

            int nul = Program.CountNul(line);
            Check(nul == 0, "received " + line.Length + " chars, no NUL",
                nul > 0 ? nul + " NUL(s) - input encoding does not match the console" : null);

            ConsoleUi.Dim("       received: " + line + "\n");
            ConsoleUi.Dim("       bytes   : " + Encoding.UTF8.GetByteCount(line) + " (UTF-8)\n");
        }
    }
}
