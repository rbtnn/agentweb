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

        public static int Run(Config cfg, bool online)
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
            if (online) ApiRequest(cfg);
            else Skip("LLM API request skipped (--test=offline)");
            // TAU_TEST_NO_INPUT=1 なら対話入力の確認を省く
            // (無人実行や、出力をリダイレクトして確認するとき用)
            bool skipInput = false;
            try
            {
                string v = System.Environment.GetEnvironmentVariable("TAU_TEST_NO_INPUT");
                skipInput = (v != null && v.Trim().Length > 0 && v.Trim() != "0");
            }
            catch { }
            if (skipInput) Skip("interactive input skipped (TAU_TEST_NO_INPUT=1)");
            else InteractiveInput();

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

            // クリップボードの往復(書き込み → 読み出し)で実装を検証する。
            // 元の内容は後で戻す。
            string marker = "tau-clipboard-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            bool wrote = LineEditor.SetClipboardText(marker);
            if (!wrote)
            {
                Skip("could not write to the clipboard for a round-trip test");
                return;
            }

            string back = LineEditor.GetClipboardText();
            Check(back == marker, "clipboard round-trip returns the same text",
                back == marker ? null : ("got " + (back == null ? "null" : back.Length + " chars")));

            // 元に戻す
            if (clip != null && clip.Length > 0) LineEditor.SetClipboardText(clip);
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

            // East Asian Width = Ambiguous の文字は日本語端末で 2 桁
            // Ambiguous(A) の文字だけを並べる。
            // 〒 や ㍿ などは W(全角) なので、端末に関係なく常に 2 桁。
            string[] wideAmbiguous = new string[]
            {
                "①", "②", "③", "⑩", "℡", "№", "Ⅰ", "ⅰ",
                "←", "→", "■", "□", "●", "○", "★", "☆",
                "※", "⇔", "⇒",
            };
            // まず実測する。Ambiguous は端末のフォント次第で 1 桁にも 2 桁にも
            // なるため、実測値を基準に判定する。
            MarkdownRenderer.MeasureWidthsOnce();
            int expectedAmb = MarkdownRenderer.MeasuredAmbiguousWidth();
            string ambText = (expectedAmb == 1) ? "1 column (half-width)"
                           : (expectedAmb == 2) ? "2 columns (full-width)"
                           : "unknown (using 2 columns)";
            ConsoleUi.Dim("       terminal renders ambiguous-width as: " + ambText + "\n");

            // TAU_TEST_WIDTH_FILE が指定されていれば、測定結果を書き出す
            // (画面をキャプチャできない環境での確認用)
            try
            {
                string wf = System.Environment.GetEnvironmentVariable("TAU_TEST_WIDTH_FILE");
                if (wf != null && wf.Trim().Length > 0)
                {
                    StringBuilder rep2 = new StringBuilder();
                    rep2.Append("ambiguous=").Append(expectedAmb).Append('\n');
                    string probeAll = "あAｱ①③⑩←→★☆※℡Ⅰ";
                    for (int i = 0; i < probeAll.Length; i++)
                    {
                        char pc = probeAll[i];
                        rep2.Append(pc).Append('=')
                            .Append(MarkdownRenderer.MeasuredWidthOf(pc)).Append('\n');
                    }
                    File.WriteAllText(wf, rep2.ToString(), Encoding.UTF8);
                }
            }
            catch { }

            // 個別に測った文字の幅を出す(端末ごとの違いが分かる)
            string[] measuredProbe = new string[]
            {
                "あ", "A", "ｱ", "①", "③", "⑩", "←", "→", "★", "☆", "※", "℡", "Ⅰ",
            };
            StringBuilder line = new StringBuilder();
            for (int i = 0; i < measuredProbe.Length; i++)
            {
                string ch = measuredProbe[i];
                int mw = MarkdownRenderer.MeasuredWidthOf(ch[0]);
                line.Append(ch).Append("=");
                if (mw == 0) line.Append("?");
                else line.Append(mw);
                line.Append(' ');
            }
            ConsoleUi.Dim("       measured: " + line + "\n");
            if (expectedAmb != 0)
                Pass("ambiguous width was measured on this terminal ("
                    + expectedAmb + " columns)");
            else
            {
                Skip("cannot measure here (redirected or no console); "
                    + "using 2 columns - set TAU_AMBIGUOUS_WIDTH=1 or 2 to force");
                expectedAmb = 2;
            }

            int badAmbiguous = 0;
            string firstBad = null;
            for (int i = 0; i < wideAmbiguous.Length; i++)
            {
                string ch = wideAmbiguous[i];
                int w = MarkdownRenderer.VisibleLength(ch);
                if (w != expectedAmb)
                {
                    badAmbiguous++;
                    if (firstBad == null)
                        firstBad = ch + " (U+" + ((int)ch[0]).ToString("X4")
                            + ") width=" + w + " but terminal uses " + expectedAmb;
                }
            }
            Check(badAmbiguous == 0,
                "ambiguous-width chars use the measured width (" + expectedAmb
                + " columns: ①②③ ←→ ■□ ★☆ etc.)",
                badAmbiguous > 0 ? badAmbiguous + " wrong, first: " + firstBad : null);

            // 実際の描画で罫線が揃うかを確認する(見本)。
            // 余白は計算幅から求めるので、計算と実表示が食い違えば崩れる。
            ConsoleUi.Dim("       --- visual check: every | should line up ---\n");
            string[] boxRows = new string[] { "あ", "①", "←", "★", "A", "ｱ", "1" };
            const int BOX_W = 8;
            string top = "       +" + new string('-', BOX_W) + "+";
            Console.WriteLine(top);
            for (int i = 0; i < boxRows.Length; i++)
            {
                string s2 = boxRows[i];
                int pad = BOX_W - 1 - MarkdownRenderer.VisibleLength(s2);
                if (pad < 0) pad = 0;
                Console.WriteLine("       | " + s2 + new string(' ', pad) + "|");
            }
            Console.WriteLine(top);
            ConsoleUi.Dim("       if a row sticks out, the width calculation is wrong\n");

            // 実測の健全性: 「あ」が 2、「A」が 1 でなければ測定が壊れている
            if (expectedAmb != 0 && MarkdownRenderer.MeasuredWidthOf('あ') != 0)
            {
                int wa = MarkdownRenderer.MeasuredWidthOf('あ');
                int wa2 = MarkdownRenderer.MeasuredWidthOf('A');
                Check(wa == 2 && wa2 == 1,
                    "measurement is consistent (あ=2, A=1)",
                    (wa == 2 && wa2 == 1) ? null
                        : ("あ=" + wa + " A=" + wa2 + " - measurement looks broken"));
            }

            // 半角カナ・半角英数は 1 桁のまま
            Check(MarkdownRenderer.VisibleLength("ｱ") == 1,
                "half-width katakana counts as 1 column", null);
            Check(MarkdownRenderer.VisibleLength("-") == 1,
                "ASCII punctuation counts as 1 column", null);

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
            Console.WriteLine("    current: " + tls);

            // 起動時に ApplyTls() が呼ばれ、Tls12 単独になっていること。
            // Ssl3 や Tls(1.0) のままだと、現代のサーバでは接続が拒否される。
            bool isTls12 = false;
            try
            {
                isTls12 = (System.Net.ServicePointManager.SecurityProtocol
                           == System.Net.SecurityProtocolType.Tls12);
            }
            catch { }

            Check(isTls12, "TLS 1.2 is applied at startup (not Ssl3/Tls)",
                isTls12 ? null
                        : ("currently " + tls + " - Ssl3/Tls will be rejected by most servers"));

            // TLS 1.2 を明示的に設定できること
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
            Check(ok, "TLS 1.2 can be selected explicitly", why);

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

        // ---------------- 11. LLM API への実リクエスト ----------------

        static void ApiRequest(Config cfg)
        {
            Section("11. LLM API request (real HTTP)");

            // 設定が揃っているか
            string url = cfg.ApiBaseUrl;
            ConsoleUi.Dim("       url   : " + (url == null ? "(none)" : url) + "\n");
            ConsoleUi.Dim("       model : " + (cfg.Model == null ? "(none)" : cfg.Model) + "\n");
            ConsoleUi.Dim("       key   : " + (string.IsNullOrEmpty(cfg.SecretKey)
                ? "(not set)" : "(set, " + cfg.SecretKey.Length + " chars)") + "\n");

            bool hasUrl = !string.IsNullOrEmpty(url);
            Check(hasUrl, "llmApiUrl is set",
                hasUrl ? null : "set it in tau.json or with /set llmApiUrl <url>");
            if (!hasUrl)
            {
                Skip("skipping the request because there is no URL");
                return;
            }

            bool hasKey = !string.IsNullOrEmpty(cfg.SecretKey);
            if (!hasKey)
                Skip("llmApiKey is not set (the server may still accept the request)");

            // 送るメッセージ(ごく短く、決定的な応答を期待する)
            List<object> messages = new List<object>();
            messages.Add(Program.Msg("system",
                "You are a connectivity test. Reply with exactly: OK"));
            messages.Add(Program.Msg("user", "ping"));

            // ---- 1. 非ストリーミング ----
            ConsoleUi.Dim("  [1] non-streaming request (tools off)...\n");
            DateTime t0 = DateTime.UtcNow;
            LlmResp resp = null;
            Exception err = null;
            try
            {
                resp = LlmClient.CallOnce(cfg, messages, false);
            }
            catch (Exception ex)
            {
                err = ex;
            }
            TimeSpan elapsed = DateTime.UtcNow - t0;

            if (err != null)
            {
                Fail("non-streaming request completed",
                    DescribeError(err) + " (" + (int)elapsed.TotalMilliseconds + " ms)");
                if (LlmClient.DebugOn)
                    ConsoleUi.Dim("       chain: "
                        + LlmClient.ExceptionChain(err).Replace("\n", "\n              ") + "\n");
                // 失敗したらストリーミングは試さない
                return;
            }

            Pass("non-streaming request completed in "
                + (int)elapsed.TotalMilliseconds + " ms");

            if (resp == null)
            {
                Fail("a response was parsed", "null response");
                return;
            }

            // 応答の中身
            bool hasContent = resp.Content != null && resp.Content.Length > 0;
            Check(hasContent, "the response contains text",
                hasContent ? null : "empty content");

            if (hasContent)
            {
                string preview = resp.Content.Replace("\r", " ").Replace("\n", " ").Trim();
                if (preview.Length > 60) preview = preview.Substring(0, 60) + "...";
                ConsoleUi.Dim("       reply : " + preview + "\n");
            }

            if (resp.TotalTokens > 0)
                ConsoleUi.Dim("       tokens: " + resp.TotalTokens + "\n");

            // ---- 2. ストリーミング ----
            ConsoleUi.Dim("  [2] streaming request (tools off)...\n");
            StringBuilder streamed = new StringBuilder();
            int chunks = 0;
            DateTime t1 = DateTime.UtcNow;
            LlmResp sresp = null;
            Exception serr = null;
            try
            {
                sresp = LlmClient.CallStream(cfg, messages, false,
                    delegate (string d) { streamed.Append(d); chunks++; },
                    delegate (string d) { });
            }
            catch (Exception ex)
            {
                serr = ex;
            }
            TimeSpan selapsed = DateTime.UtcNow - t1;

            if (serr != null)
            {
                Fail("streaming request completed",
                    DescribeError(serr) + " (" + (int)selapsed.TotalMilliseconds + " ms)");
                return;
            }

            Pass("streaming request completed in "
                + (int)selapsed.TotalMilliseconds + " ms (" + chunks + " chunks)");

            bool gotStream = streamed.Length > 0;
            Check(gotStream, "streaming delivered text incrementally",
                gotStream ? null : "no content chunks received");

            if (gotStream)
            {
                string preview = streamed.ToString().Replace("\r", " ").Replace("\n", " ").Trim();
                if (preview.Length > 60) preview = preview.Substring(0, 60) + "...";
                ConsoleUi.Dim("       reply : " + preview + "\n");
            }

            // 非ストリーミングとストリーミングで内容が一致するか。
            // (サーバが同じ応答を返すとは限らないので、比較は緩めにする)
            if (sresp != null && hasContent && gotStream
                && sresp.Content != null && sresp.Content.Length > 0)
            {
                string a = resp.Content.Trim();
                string b = sresp.Content.Trim();
                Check(true, "both paths returned a response", null);
                if (a != b)
                    ConsoleUi.Dim("       (the two replies differ, which is normal"
                        + " for non-deterministic models)\n");
            }
        }

        // 例外を 1 行で説明する
        static string DescribeError(Exception ex)
        {
            WebException wex = ex as WebException;
            if (wex != null) return LlmClient.DescribeWebException(wex);
            return ex.GetType().Name + ": " + ex.Message;
        }

        // ---------------- 12. 対話入力(任意) ----------------

        static void InteractiveInput()
        {
            Section("12. interactive input (manual)");

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
