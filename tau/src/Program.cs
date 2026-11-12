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
    //  エントリポイント
    // =======================================================================
    class Program
    {
        static void Main(string[] args)
        {
            // コンソールのエンコーディングを設定する。
            //
            // 重要: 出力だけ UTF-8 にして入力が CP932 のままだと、Windows では
            // コンソールの入出力コードページが食い違い、日本語入力が NUL 文字
            // (\u0000) に化けることがある(dotnet/runtime#43295)。
            // 入出力を同じコードページに揃えるのが安全なので、
            // 対話コンソールのときは両方 UTF-8 にする。
            // 設定ファイルを先に読んで、入力エンコーディングの指定を知る必要がある。
            // (引数解析の後で Config を作るため、ここでは既定値を仮に使い、
            //  Config 生成後に改めて適用する)
            SetupConsoleEncoding(null);

            string configPath = "";
            string sessionArg = "";
            bool help = false;
            bool diag = false;
            bool version = false;
            bool selfTest = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = (args[i] ?? "").Trim();
                if (a == "--config" && i + 1 < args.Length) configPath = args[++i].Trim();
                else if (a.StartsWith("--config=")) configPath = a.Substring("--config=".Length);
                else if (a == "--session" && i + 1 < args.Length) sessionArg = args[++i].Trim();
                else if (a.StartsWith("--session=")) sessionArg = a.Substring("--session=".Length);
                else if (a == "--help" || a == "-h" || a == "/?") help = true;
                else if (a == "--list") { ListSessions(configPath); return; }
                else if (a == "--debug") LlmClient.EnableDebug();
                else if (a == "--diag") diag = true;
                else if (a == "--version" || a == "-v") version = true;
                else if (a == "--test") selfTest = true;
            }

            if (version) { PrintVersion(); return; }
            if (help) { PrintHelp(); return; }

            Config cfg = new Config(configPath);

            // 設定ファイルの inputEncoding に従って、入力を設定し直す。
            SetupConsoleEncoding(cfg);

            // 入力バッファを広げる(254 文字の上限を回避する)。
            SetupInputBuffer();

            // 行編集(Emacs キーバインド)を使えるか判定する。
            LineEditor.Configure(cfg);

            // Markdown の色付けを、コンソールの色対応に合わせる
            MarkdownRenderer.Configure(ConsoleUi.ColorEnabled);

            // 端末が実際に何桁で描くかを、出力を始める前に測っておく。
            // (①②③ などは計算上 2 桁でも 1 桁で描く端末がある)
            MarkdownRenderer.MeasureWidthsOnce();

            // TLS 1.2 を最初に適用しておく。
            // .NET Framework の既定は Ssl3 | Tls のままで、現代のサーバでは
            // ほぼ必ず拒否される。要求のたびに設定するより先に固定しておく。
            LlmClient.ApplyTls();

            if (diag) { Diagnostics.Run(cfg); return; }

            if (selfTest)
            {
                int code = SelfTest.Run(cfg);
                try { Console.Out.Flush(); } catch { }
                Environment.ExitCode = code;
                return;
            }

            Chat chat = new Chat(cfg);
            chat.Run(sessionArg);
        }

        // 入力エンコーディングを「変更しない」のが原則だが、起動時点で UTF-8 に
        // なっていると日本語入力が壊れるため、コンソール既定に戻す。
        // コンソールの入出力エンコーディングを設定する。
        //
        // Windows では入出力のコードページが食い違うと日本語入力が壊れるため、
        // 対話コンソールのときは両方 UTF-8 に揃える。
        // 設定名 → Encoding を解決する。解決できなければ null。
        public static Encoding ResolveEncoding(string name)
        {
            if (name == null) return null;
            string n = name.Trim().ToLowerInvariant().Replace("_", "-").Replace(" ", "");
            switch (n)
            {
                case "":
                case "default":
                case "auto":
                    return null;   // 変更しない
                case "utf-8":
                case "utf8":
                case "65001":
                    return Encoding.UTF8;
                case "shift-jis":
                case "shiftjis":
                case "sjis":
                case "cp932":
                case "932":
                case "windows-31j":
                    try { return Encoding.GetEncoding(932); }
                    catch { return null; }
                default:
                    // 数値のコードページ指定も受け付ける
                    {
                        int cp;
                        if (int.TryParse(n, out cp))
                        {
                            try { return Encoding.GetEncoding(cp); }
                            catch { return null; }
                        }
                    }
                    return null;
            }
        }

        // コンソールの入出力エンコーディングを設定する。
        //
        // cfg が null の場合は既定(shift-jis)として扱う。
        // 出力は常に UTF-8(日本語の表示が化けない)。
        // 入力は cfg.InputEncoding に従う:
        //   - shift-jis … Windows のコンソール既定。日本語入力が安定する
        //   - utf-8     … コンソールが UTF-8 のとき
        //   - default   … 触らない
        // Console.ReadLine() は .NET Framework で既定 256 単位の入力バッファを使う。
        // この上限はバイト数ではなく「文字数」で判定され、256 には CR/LF も
        // 含まれるため 254 文字までしか読めず、それ以降は黙って切り捨てられる。
        // 全角・半角を問わず 254 文字で切れるため、日本語の長いプロンプトが
        // 途中で切れて送信される。
        //
        // Console.In を大きなバッファの StreamReader に差し替えると、
        // 既存の Console.ReadLine() 呼び出しすべてがその恩恵を受ける。
        // (コンソールの行編集・IME はそのまま使える)
        // テストから使うための薄いラッパ
        internal static Dictionary<string, object> Msg(string role, string content)
        {
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["role"] = role;
            m["content"] = content;
            return m;
        }

        // ------------------------------------------------------------------
        //  コンソールのカーソル桁位置を、端末に問い合わせずに取得する。
        //  Console.CursorLeft は Mono / 一部端末で ESC[6n を送って応答待ちに
        //  なるため、Windows では GetConsoleScreenBufferInfo を直接使う。
        // ------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SMALL_RECT
        {
            public short Left;
            public short Top;
            public short Right;
            public short Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct CONSOLE_SCREEN_BUFFER_INFO
        {
            public COORD dwSize;
            public COORD dwCursorPosition;
            public short wAttributes;
            public SMALL_RECT srWindow;
            public COORD dwMaximumWindowSize;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput,
            out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

        const int STD_OUTPUT_HANDLE = -11;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetStdHandle(int nStdHandle);

        // 現在のカーソル桁位置。取得できなければ -1。
        internal static int GetConsoleCursorColumn()
        {
            if (!IsWindows()) return -1;
            try
            {
                IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return -1;

                CONSOLE_SCREEN_BUFFER_INFO info;
                if (!GetConsoleScreenBufferInfo(h, out info)) return -1;
                return info.dwCursorPosition.X;
            }
            catch { return -1; }
        }

        internal static bool IsWindows()
        {
            try
            {
                int p = (int)Environment.OSVersion.Platform;
                return p == 1 || p == 2;
            }
            catch { return false; }
        }

        static void SetupInputBuffer()
        {
            const int READLINE_BUFFER_SIZE = 64 * 1024;   // 64KB

            try
            {
                // 大きなバッファで読む(254 文字の上限を回避する)。
                //
                // Console.In は Windows では ReadFile を使うが、
                // ENABLE_LINE_INPUT は ReadFile にも効くため、コンソールの
                // 行編集(Ctrl-V の貼り付け・矢印キー・IME)はそのまま働く。
                Stream stdin = Console.OpenStandardInput(READLINE_BUFFER_SIZE);
                Console.SetIn(new StreamReader(stdin, Console.InputEncoding,
                                               false, READLINE_BUFFER_SIZE));

                // 念のため行編集モードを有効にする
                LineEditor.EnsureConsoleLineInputPublic();
            }
            catch
            {
                // 設定できない環境では既定のままにする
            }
        }

        static void SetupConsoleEncoding(Config cfg)
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch { }

            // 入力がリダイレクトされている場合は触らない
            // (パイプやファイルからの入力はそのまま読める)
            bool redirected = false;
            try { redirected = Console.IsInputRedirected; }
            catch { }
            if (redirected) return;

            string want = (cfg != null && cfg.InputEncoding != null && cfg.InputEncoding.Trim().Length > 0)
                ? cfg.InputEncoding
                : "shift-jis";

            Encoding enc = ResolveEncoding(want);
            if (enc == null)
            {
                // "default" 指定、または解決できなかった場合は変更しない
                return;
            }

            try
            {
                Console.InputEncoding = enc;
            }
            catch
            {
                // 設定できない場合は何もしない(コンソール既定のまま)
            }

            // Windows では Console.InputEncoding を変えても、コンソールの
            // 実際のコードページ(GetConsoleCP)は変わらないことがある。
            // その状態で Console.ReadKey() を使うと日本語が壊れる
            // (例: 「あ」→ U+7E3A)。両者を一致させておく。
            try { SetConsoleCPIfPossible(enc.CodePage); } catch { }
        }

        // コンソールの実際のコードページを、指定した値に合わせる。
        // Windows 以外や失敗時は何もしない。
        static void SetConsoleCPIfPossible(int codePage)
        {
            if (codePage <= 0) return;
            try
            {
                int p = (int)Environment.OSVersion.Platform;
                if (p != 2 && p != 1) return;   // Windows 以外

                int current = LineEditor.ConsoleInputCodePagePublic();
                if (current == codePage) return;   // 既に一致している

                if (LineEditor.SetConsoleCP((uint)codePage))
                    LineEditor.MarkConsoleCodePage(codePage);
            }
            catch { }
        }

        // 文字列中の NUL 文字の個数を数える
        public static int CountNul(string s)
        {
            if (s == null) return 0;
            int n = 0;
            for (int i = 0; i < s.Length; i++)
                if (s[i] == '\0') n++;
            return n;
        }

        // 現在の入力エンコーディングを表示用に返す(診断用)
        public static string SafeInputEncoding()
        {
            try
            {
                Encoding e = Console.InputEncoding;
                return e.WebName + " (CP" + e.CodePage + ")"
                    + (Console.IsInputRedirected ? ", redirected" : "");
            }
            catch (Exception ex) { return "unknown (" + ex.Message + ")"; }
        }

        // 入力文字列から NUL 文字を取り除く。
        // コンソール設定が原因で \u0000 が混入しても、セッションを壊さないための保険。
        public static string SanitizeInput(string s)
        {
            if (s == null) return "";
            if (s.IndexOf('\0') < 0) return s;
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
                if (s[i] != '\0') sb.Append(s[i]);
            return sb.ToString();
        }

        // ビルド識別子。古い tau.exe を動かしていないか確認できるようにする。
        public const string BuildStamp = "2026-02-24 ambiguous-verify";

        internal static void PrintVersion()
        {
            Console.WriteLine("tau " + BuildStamp);
            string loc = "";
            try { loc = System.Reflection.Assembly.GetExecutingAssembly().Location; } catch { }
            Console.WriteLine("  exe   : " + loc);
            try
            {
                Console.WriteLine("  built : "
                    + File.GetLastWriteTimeUtc(loc).ToString("yyyy-MM-dd HH:mm:ss") + " UTC");
            }
            catch { }
        }

        internal static void PrintHelp()
        {
            Console.WriteLine("tau - AI agent that runs in the console");
            Console.WriteLine();
            Console.WriteLine("Usage: tau.exe [options]");
            Console.WriteLine("  --config <path>    Path to the config file (tau.json)");
            Console.WriteLine("  --session <id>     Open an existing session and continue it");
            Console.WriteLine("  --version          Show the build stamp and exit");
            Console.WriteLine("  --list             List saved sessions and exit");
            Console.WriteLine("  --diag             Run a connection diagnostic (TLS/HTTP) and exit");
            Console.WriteLine("  --test             Run the built-in self test and exit");
            Console.WriteLine("  --debug            Enable debug output (same as TAU_DEBUG=1)");
            Console.WriteLine("  --help, -h, /?     Show this help");
            Console.WriteLine();
            Console.WriteLine("Commands available while chatting:");
            Console.WriteLine("  /help            Show this help");
            Console.WriteLine("  /new             Start a new session");
            Console.WriteLine("  /sessions        List saved sessions");
            Console.WriteLine("  /open <id>       Open the given session");
            Console.WriteLine("  /delete <n>      Delete a session (numbers come from /sessions)");
            Console.WriteLine("  /clear           Clear the current session's history");
            Console.WriteLine("  /config          Show the current settings");
            Console.WriteLine("  /config edit     Edit settings interactively");
            Console.WriteLine("  /set <k> <v>     Change a single setting (e.g. /set model gpt-4o)");
            Console.WriteLine("  /model [name]    Show or switch the model");
            Console.WriteLine("  /tools           Toggle tool execution on/off");
            Console.WriteLine("  /exit, /quit     Exit (Ctrl+D also works)");
        }

        static void ListSessions(string configPath)
        {
            // 設定を読み込んでおく(保存先ディレクトリの作成も兼ねる)
            new Config(configPath);
            SessionStore store = new SessionStore();
            List<object> list = store.List();
            if (list.Count == 0)
            {
                Console.WriteLine("No saved sessions.");
                Console.WriteLine("  Location: " + store.DataDir);
                return;
            }
            Console.WriteLine("Saved sessions (" + store.DataDir + ")");
            Console.WriteLine();
            for (int i = 0; i < list.Count; i++)
            {
                Dictionary<string, object> s = list[i] as Dictionary<string, object>;
                if (s == null) continue;
                Console.WriteLine("  " + Json.AsString(Get(s, "id")) + "  " + Json.AsString(Get(s, "title")));
            }
        }

        internal static object Get(Dictionary<string, object> d, string k)
        {
            if (d != null && d.ContainsKey(k)) return d[k];
            return null;
        }
    }
}
