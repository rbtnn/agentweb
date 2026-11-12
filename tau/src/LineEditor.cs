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
    //  Emacs 風のキーバインドで 1 行を編集する
    //
    //  Console.ReadLine() はコンソール側の行編集に任せるため、キー入力を
    //  アプリから制御できない。Emacs 風の操作を実現するには 1 文字ずつ
    //  Console.ReadKey() で読んで自前で描画する必要がある。
    //
    //  対話コンソール(入力がリダイレクトされていない)ときだけ使う。
    //  リダイレクト時は従来の Console.ReadLine() にフォールバックする。
    // =======================================================================
    static class LineEditor
    {
        public enum Mode
        {
            Single,   // 1 行だけ読む(Enter で確定)
            Multi     // 複数行。空行だけで確定(システムプロンプト用)
        }

        // 複数行入力の結果
        public class Result
        {
            public string Text = "";      // 入力されたテキスト
            public bool Cancelled;        // Ctrl-G / EOF で中止
            public bool NulLost;          // NUL で失われた行があった
        }

        // 自前の行編集を使えるか。
        //
        // Console.ReadKey() は Console.InputEncoding に従ってバイトを文字に変換する。
        // Shift-JIS(CP932) を指定していると、ReadKey が返す日本語が壊れる
        // (例: 「あ」→ U+7E3A)。そのため自前の行編集は UTF-8 入力のときだけ使う。
        // Shift-JIS のときは従来どおり Console.ReadLine() に任せる。
        static bool enabled = false;

        // 自前の行編集を有効にするか判定する。
        //
        // Console.ReadKey() は Console.InputEncoding に従ってバイトを文字に
        // 変換する。コンソールの実際のコードページ(GetConsoleCP)と
        // Console.InputEncoding が食い違っていると日本語が壊れる
        // (例: 「あ」→ U+7E3A)。起動時に SetConsoleCP で両者を一致させて
        // いるので、ここでは「一致しているか」だけを確認する。
        // 自前の行編集は使わない。
        //
        // Console.ReadKey() は Console.InputEncoding に従ってバイトを文字に
        // 変換する。コンソールの実際のコードページ(GetConsoleCP)と食い違うと
        // 日本語が壊れ(「あ」→ U+7E3A)、長い入力が途中で切れることもある。
        // 安定して動く Console.ReadLine() に任せる。
        //
        // 起動時に SetConsoleCP でコードページを一致させているので、将来
        // 復活させる場合はここで enabled = true にすればよい。ただし
        // 実機で長い日本語・IME の確認をしてから有効にすること。
        public static void Configure(Config cfg)
        {
            enabled = false;
        }

        internal static int ConsoleInputCodePagePublic()
        {
            return ConsoleInputCodePage();
        }

        internal static void MarkConsoleCodePage(int cp)
        {
            // SetConsoleCP で変更したので、以降は一致しているとみなす。
            // (ConsoleInputCodePage は実際の値を返すので特に何もしない)
        }

        // Windows の実際のコンソール入力コードページ(GetConsoleCP)。
        // Windows 以外や取得できない場合は 0。
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint GetConsoleCP();

        // コンソールのコードページを変更する(SetConsoleCP)。
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetConsoleCP(uint wCodePageID);

        static int ConsoleInputCodePage()
        {
            try
            {
                int p = (int)System.Environment.OSVersion.Platform;
                // PlatformID.Win32NT = 2, Win32Windows = 1
                if (p != 2 && p != 1) return 0;   // Windows 以外
                uint cp = GetConsoleCP();
                return (cp > 0) ? (int)cp : 0;
            }
            catch { return 0; }
        }

        static bool interactive()
        {
            return enabled;
        }

        // 自前の行編集が有効か
        public static bool Available { get { return enabled; } }

        // ---------------- 1 行の編集 ----------------

        // 色付きプロンプトで 1 行読む。
        public static string ReadLineColored(string prompt, string ansiColor)
        {
            if (!interactive())
            {
                ConsoleUi.Accent(prompt);
                try { return Console.ReadLine(); }
                catch { return null; }
            }
            // 色付きで表示する(幅は見た目と同じなのでプロンプト長で計算)
            ConsoleUi.Accent(prompt);
            bool cancelled;
            string result = EditLoop(prompt.Length, out cancelled);
            if (cancelled) return null;
            Console.Write("\n");
            return result;
        }

        // prompt を表示して 1 行読む。Ctrl-G / Ctrl-D(空行)で null。
        public static string ReadLine(string prompt)
        {
            if (!interactive()) return ReadLineFallback(prompt);

            Console.Write(prompt);
            bool cancelled;
            string result = EditLoop(prompt.Length, out cancelled);
            if (cancelled) return null;
            Console.Write("\n");
            return result;
        }

        // ------------------------------------------------------------------
        //  Windows のコンソールの行編集モードを整える。
        //
        //  Console.In は Windows では ReadFile を使う。ENABLE_LINE_INPUT は
        //  ReadFile にも効くため、これを有効にしておけば Ctrl-V の貼り付け・
        //  矢印キー・Home/End・IME がコンソール標準の機能として働く。
        // ------------------------------------------------------------------

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetStdHandle(int nStdHandle);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        internal const int STD_INPUT_HANDLE = -10;
        internal const uint EnableLineInput = 0x0002;
        internal const uint EnableEchoInput = 0x0004;
        internal const uint EnableProcessedInput = 0x0001;

        // 現在のコンソール入力モードを取得する(テスト用)
        internal static bool TryGetConsoleMode(out uint mode)
        {
            mode = 0;
            if (!Program.IsWindows()) return false;
            try
            {
                IntPtr h = GetStdHandle(STD_INPUT_HANDLE);
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return false;
                return GetConsoleMode(h, out mode);
            }
            catch { return false; }
        }

        // コンソールの行編集(貼り付けが効く状態)を有効にする
        internal static void EnsureConsoleLineInputPublic() { EnsureConsoleLineInput(); }

        static void EnsureConsoleLineInput()
        {
            if (!Program.IsWindows()) return;
            try
            {
                IntPtr h = GetStdHandle(STD_INPUT_HANDLE);
                uint mode;
                if (!GetConsoleMode(h, out mode)) return;
                uint want = mode | EnableLineInput | EnableEchoInput;
                if (want != mode) SetConsoleMode(h, want);
            }
            catch { }
        }


        static string ReadLineFallback(string prompt)
        {
            Console.Write(prompt);
            try
            {
                // Console.ReadLine() は Windows では ReadFile を使うが、
                // ENABLE_LINE_INPUT は ReadFile にも効くため、コンソールの
                // 行編集(Ctrl-V の貼り付け・矢印キー・IME)がそのまま働く。
                // 行編集モードを確実に有効にしてから読む。
                EnsureConsoleLineInput();
                return Console.ReadLine();
            }
            catch { return null; }
        }

        // クリップボードにテキストを書き込む(テストと将来の拡張用)。
        // 成功なら true。
        public static bool SetClipboardText(string text)
        {
            if (!Program.IsWindows()) return false;
            if (text == null) return false;

            bool opened = false;
            try
            {
                if (!OpenClipboard(IntPtr.Zero)) return false;
                opened = true;

                if (!EmptyClipboard()) return false;

                // UTF-16 の文字列を NUL 終端付きでコピーする
                int chars = text.Length + 1;
                IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(chars * 2));
                if (h == IntPtr.Zero) return false;

                IntPtr ptr = GlobalLock(h);
                if (ptr == IntPtr.Zero)
                {
                    GlobalFree(h);
                    return false;
                }

                try
                {
                    System.Runtime.InteropServices.Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                    // 終端の NUL
                    System.Runtime.InteropServices.Marshal.WriteInt16(ptr, text.Length * 2, 0);
                }
                finally
                {
                    GlobalUnlock(h);
                }

                // SetClipboardData が成功したら所有権はシステムに移る
                if (SetClipboardData(CF_UNICODETEXT, h) == IntPtr.Zero)
                {
                    GlobalFree(h);
                    return false;
                }
                return true;
            }
            catch { return false; }
            finally
            {
                if (opened)
                {
                    try { CloseClipboard(); } catch { }
                }
            }
        }

        // ------------------------------------------------------------------
        //  クリップボードからの貼り付け
        //
        //  コンソールの行編集が無効な環境では Ctrl-V が SYN(0x16)として
        //  届く。その場合はクリップボードを直接読んで貼り付ける。
        // ------------------------------------------------------------------

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        static extern bool CloseClipboard();

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr GetClipboardData(uint uFormat);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GlobalLock(IntPtr hMem);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalUnlock(IntPtr hMem);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GlobalFree(IntPtr hMem);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        static extern bool EmptyClipboard();

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        const uint CF_UNICODETEXT = 13;
        const uint GMEM_MOVEABLE = 0x0002;

        // クリップボードのテキストを取得する。取得できなければ null。
        public static string GetClipboardText()
        {
            if (!Program.IsWindows()) return null;

            bool opened = false;
            try
            {
                if (!OpenClipboard(IntPtr.Zero)) return null;
                opened = true;

                IntPtr handle = GetClipboardData(CF_UNICODETEXT);
                if (handle == IntPtr.Zero) return null;   // テキスト以外が入っている

                IntPtr ptr = GlobalLock(handle);
                if (ptr == IntPtr.Zero) return null;

                string text = null;
                try
                {
                    text = System.Runtime.InteropServices.Marshal.PtrToStringUni(ptr);
                }
                finally
                {
                    GlobalUnlock(handle);   // ロックは必ず解除する
                }
                return text;
            }
            catch { return null; }
            finally
            {
                // OpenClipboard が成功したときだけ閉じる
                if (opened)
                {
                    try { CloseClipboard(); } catch { }
                }
            }
        }

        // 複数行を読む。空行だけで確定。Ctrl-G で中止。
        public static Result ReadMultiLine(string prompt)
        {
            Result r = new Result();

            if (!interactive())
            {
                // 従来の動作(1 行ずつ ReadLine、空行で終了)
                Console.Write(prompt);
                StringBuilder sb = new StringBuilder();
                while (true)
                {
                    string ln;
                    try { ln = Console.ReadLine(); }
                    catch { r.Cancelled = true; break; }
                    if (ln == null) { if (sb.Length == 0) r.Cancelled = true; break; }

                    // NUL に化けた行を「空行」と誤判定しないよう先に確認する
                    int nul = Program.CountNul(ln);
                    if (nul > 0)
                    {
                        string cleaned = Program.SanitizeInput(ln);
                        if (cleaned.Length == 0)
                        {
                            ConsoleUi.Danger("  ⚠ This line became NUL and was lost ("
                                + ln.Length + " characters).\n");
                            ConsoleUi.Dim("    The console input encoding does not match.\n");
                            ConsoleUi.Info("input encoding: " + Program.SafeInputEncoding());
                            r.NulLost = true;
                            continue;   // やり直し
                        }
                        ConsoleUi.Danger("  ⚠ " + nul
                            + " character(s) of this line became NUL and were dropped.\n");
                        ln = cleaned;
                    }

                    if (ln.Length == 0) break;   // 空行で終了
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(ln);
                }
                r.Text = sb.ToString();
                return r;
            }

            StringBuilder all = new StringBuilder();
            int lineNo = 0;
            while (true)
            {
                lineNo++;
                Console.Write(lineNo == 1 ? prompt : "");
                bool cancelled;
                string line = EditLoop(0, out cancelled);
                if (cancelled) { r.Cancelled = true; break; }
                Console.Write("\n");

                int nul = Program.CountNul(line);
                if (nul > 0)
                {
                    string cleaned = Program.SanitizeInput(line);
                    if (cleaned.Length == 0)
                    {
                        ConsoleUi.Danger("  ⚠ This line became NUL and was lost ("
                            + line.Length + " characters).\n");
                        ConsoleUi.Dim("    The console input encoding does not match.\n");
                        ConsoleUi.Info("input encoding: " + Program.SafeInputEncoding());
                        r.NulLost = true;
                        lineNo--;
                        continue;   // やり直し
                    }
                    ConsoleUi.Danger("  ⚠ " + nul
                        + " character(s) of this line became NUL and were dropped.\n");
                    line = cleaned;
                }

                if (line.Length == 0) break;   // 空行で終了
                if (all.Length > 0) all.Append('\n');
                all.Append(line);
            }
            r.Text = all.ToString();
            return r;
        }

        // ---------------- 編集ループ ----------------

        // 1 行を編集して返す。cancelled=true なら Ctrl-G / Ctrl-D。
        static string EditLoop(int promptWidth, out bool cancelled)
        {
            cancelled = false;
            lastWidth = 0;
            StringBuilder buf = new StringBuilder();
            int pos = 0;              // カーソル位置(0..buf.Length)

            // キルリング(Ctrl-K で切った文字列。Ctrl-Y で貼り付け)
            string killRing = "";

            while (true)
            {
                ConsoleKeyInfo k = Console.ReadKey(true);

                bool ctrl = (k.Modifiers & ConsoleModifiers.Control) != 0;
                bool alt = (k.Modifiers & ConsoleModifiers.Alt) != 0;

                // 制御文字そのものが来た場合も Ctrl として扱う
                // (端末によっては Modifiers に Control が入らない)
                if (!ctrl && !alt && k.KeyChar != '\0' && (int)k.KeyChar < 0x20
                    && k.KeyChar != '\r' && k.KeyChar != '\n' && k.KeyChar != '\t')
                {
                    ctrl = true;
                    int code = (int)k.KeyChar;
                    // 制御文字 → 対応する文字キー
                    if (code >= 1 && code <= 26)
                    {
                        char letter = (char)('A' + (code - 1));
                        ConsoleKey mapped;
                        if (Enum.TryParse<ConsoleKey>(letter.ToString(), out mapped))
                        {
                            // Key を差し替える
                            k = new ConsoleKeyInfo(letter, mapped, false, false, true);
                        }
                    }
                }

                if (LlmClient.DebugOn)
                    Console.Error.WriteLine("[key] " + k.Key + " U+"
                        + ((int)k.KeyChar).ToString("X4") + " " + k.Modifiers
                        + " ctrl=" + (ctrl ? "1" : "0") + " alt=" + (alt ? "1" : "0"));

                // ---- Ctrl キー ----
                if (ctrl)
                {
                    switch (k.Key)
                    {
                        case ConsoleKey.H:          // Ctrl-H  Backspace
                        case ConsoleKey.Backspace:
                            if (pos > 0)
                            {
                                buf.Remove(pos - 1, 1);
                                pos--;
                                Redraw(buf.ToString(), pos, promptWidth);
                            }
                            continue;

                        case ConsoleKey.D:          // Ctrl-D  1文字削除
                            if (buf.Length == 0)
                            {
                                cancelled = true;   // 空行で Ctrl-D は中止(EOF)
                                return "";
                            }
                            if (pos < buf.Length)
                            {
                                buf.Remove(pos, 1);
                                Redraw(buf.ToString(), pos, promptWidth);
                            }
                            continue;

                        case ConsoleKey.A:          // Ctrl-A  行頭へ
                            pos = 0;
                            Redraw(buf.ToString(), pos, promptWidth);
                            continue;

                        case ConsoleKey.E:          // Ctrl-E  行末へ
                            pos = buf.Length;
                            Redraw(buf.ToString(), pos, promptWidth);
                            continue;

                        case ConsoleKey.B:          // Ctrl-B  1文字戻る
                            if (pos > 0) pos--;
                            Redraw(buf.ToString(), pos, promptWidth);
                            continue;

                        case ConsoleKey.F:          // Ctrl-F  1文字進む
                            if (pos < buf.Length) pos++;
                            Redraw(buf.ToString(), pos, promptWidth);
                            continue;

                        case ConsoleKey.K:          // Ctrl-K  カーソル以降を切る
                            if (pos < buf.Length)
                            {
                                killRing = buf.ToString(pos, buf.Length - pos);
                                buf.Remove(pos, buf.Length - pos);
                                Redraw(buf.ToString(), pos, promptWidth);
                            }
                            else killRing = "";
                            continue;

                        case ConsoleKey.U:          // Ctrl-U  カーソル以前を切る
                            if (pos > 0)
                            {
                                killRing = buf.ToString(0, pos);
                                buf.Remove(0, pos);
                                pos = 0;
                                Redraw(buf.ToString(), pos, promptWidth);
                            }
                            else killRing = "";
                            continue;

                        case ConsoleKey.W:          // Ctrl-W  直前の単語を切る
                            {
                                int start = WordStart(buf.ToString(), pos);
                                if (start < pos)
                                {
                                    killRing = buf.ToString(start, pos - start);
                                    buf.Remove(start, pos - start);
                                    pos = start;
                                    Redraw(buf.ToString(), pos, promptWidth);
                                }
                                else killRing = "";
                            }
                            continue;

                        case ConsoleKey.Y:          // Ctrl-Y  ペースト
                            if (killRing.Length > 0)
                            {
                                buf.Insert(pos, killRing);
                                pos += killRing.Length;
                                Redraw(buf.ToString(), pos, promptWidth);
                            }
                            continue;

                        case ConsoleKey.L:          // Ctrl-L  画面クリアして再描画
                            try { Console.Clear(); } catch { }
                            Redraw(buf.ToString(), pos, promptWidth);
                            continue;

                        case ConsoleKey.G:          // Ctrl-G  中止
                            cancelled = true;
                            Console.Write("\n");
                            return "";

                        case ConsoleKey.C:          // Ctrl-C  中断(行を破棄)
                            ConsoleUi.Danger("^C");
                            cancelled = true;
                            Console.Write("\n");
                            return "";

                        case ConsoleKey.T:          // Ctrl-T  文字の入れ替え
                            if (buf.Length >= 2 && pos > 0)
                            {
                                int i = (pos < buf.Length) ? pos : buf.Length - 1;
                                char c1 = buf[i - 1];
                                buf[i - 1] = buf[i];
                                buf[i] = c1;
                                if (pos < buf.Length) pos++;
                                Redraw(buf.ToString(), pos, promptWidth);
                            }
                            continue;

                        case ConsoleKey.P:          // Ctrl-P  履歴(未実装)として上キー扱い
                        case ConsoleKey.N:          // Ctrl-N  下キー扱い
                            continue;               // 履歴は無いので何もしない
                    }
                    continue;
                }

                // ---- Alt キー ----
                if (alt)
                {
                    switch (k.Key)
                    {
                        case ConsoleKey.B:          // Alt-B  単語単位で戻る
                            pos = WordStart(buf.ToString(), pos);
                            Redraw(buf.ToString(), pos, promptWidth);
                            continue;
                        case ConsoleKey.F:          // Alt-F  単語単位で進む
                            pos = WordEnd(buf.ToString(), pos);
                            Redraw(buf.ToString(), pos, promptWidth);
                            continue;
                        case ConsoleKey.D:          // Alt-D  次の単語を切る
                            {
                                int end = WordEnd(buf.ToString(), pos);
                                if (end > pos)
                                {
                                    killRing = buf.ToString(pos, end - pos);
                                    buf.Remove(pos, end - pos);
                                    Redraw(buf.ToString(), pos, promptWidth);
                                }
                            }
                            continue;
                        case ConsoleKey.Backspace:  // Alt-BS 前の単語を切る
                            {
                                int start = WordStart(buf.ToString(), pos);
                                if (start < pos)
                                {
                                    killRing = buf.ToString(start, pos - start);
                                    buf.Remove(start, pos - start);
                                    pos = start;
                                    Redraw(buf.ToString(), pos, promptWidth);
                                }
                            }
                            continue;
                    }
                    continue;
                }

                // ---- 通常キー ----
                switch (k.Key)
                {
                    case ConsoleKey.Enter:
                        return buf.ToString();

                    case ConsoleKey.Backspace:
                        if (pos > 0)
                        {
                            buf.Remove(pos - 1, 1);
                            pos--;
                            Redraw(buf.ToString(), pos, promptWidth);
                        }
                        continue;

                    case ConsoleKey.Delete:
                        if (pos < buf.Length)
                        {
                            buf.Remove(pos, 1);
                            Redraw(buf.ToString(), pos, promptWidth);
                        }
                        continue;

                    case ConsoleKey.LeftArrow:
                        if (pos > 0) pos--;
                        Redraw(buf.ToString(), pos, promptWidth);
                        continue;

                    case ConsoleKey.RightArrow:
                        if (pos < buf.Length) pos++;
                        Redraw(buf.ToString(), pos, promptWidth);
                        continue;

                    case ConsoleKey.Home:
                        pos = 0;
                        Redraw(buf.ToString(), pos, promptWidth);
                        continue;

                    case ConsoleKey.End:
                        pos = buf.Length;
                        Redraw(buf.ToString(), pos, promptWidth);
                        continue;

                    case ConsoleKey.Escape:
                        cancelled = true;
                        Console.Write("\n");
                        return "";
                }

                // 制御文字以外の文字を挿入
                char ch = k.KeyChar;
                if (ch != '\0' && !char.IsControl(ch))
                {
                    buf.Insert(pos, ch);
                    pos++;
                    Redraw(buf.ToString(), pos, promptWidth);
                }
            }
        }

        // ---------------- 単語境界 ----------------

        static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        // pos から見て、直前の単語の開始位置
        static int WordStart(string s, int pos)
        {
            int i = pos;
            while (i > 0 && !IsWordChar(s[i - 1])) i--;
            while (i > 0 && IsWordChar(s[i - 1])) i--;
            return i;
        }

        // pos から見て、次の単語の終了位置
        static int WordEnd(string s, int pos)
        {
            int i = pos;
            while (i < s.Length && !IsWordChar(s[i])) i++;
            while (i < s.Length && IsWordChar(s[i])) i++;
            return i;
        }

        // ---------------- 描画 ----------------

        // 行を描き直し、カーソルを pos の位置へ戻す。
        // promptWidth はプロンプトの表示幅(再描画時のカーソル計算に使う)。
        static int lastWidth = 0;   // 直前に描いた文字数(消去用)

        static void Redraw(string text, int pos, int promptWidth)
        {
            try
            {
                int width = 0;
                try { width = Console.WindowWidth; } catch { }
                // 取得できない・異常な値(0 や極端に大きい)のときは既定値にする
                if (width <= 0 || width > 10000) width = 80;

                int avail = Math.Max(1, width - 1 - promptWidth);

                // 画面幅に収まらない場合は、カーソルが見えるよう表示範囲をずらす
                int start = 0;
                if (text.Length > avail) start = Math.Max(0, pos - (avail - 1));
                int len = Math.Min(text.Length - start, avail);

                string shown = text.Substring(start, len);
                int shownPos = pos - start;

                // 行頭に戻り、プロンプトは既に表示済みなので、
                // その幅ぶん進めてから本文を描き直す。
                // (Console.SetCursorPosition は端末によってカーソル位置問い合わせを
                //  送ってしまうため使わない)
                Console.Write("\r");

                // プロンプトの分だけ進める(プロンプトは呼び出し側が表示済み)
                StringBuilder sb = new StringBuilder();
                if (promptWidth > 0) sb.Append(new string(' ', promptWidth));

                int erase = Math.Max(lastWidth, shown.Length);
                sb.Append(shown);
                sb.Append(new string(' ', Math.Max(0, erase - shown.Length)));

                // カーソルを目的の位置に置くため、一度末尾まで書いてから戻す
                sb.Append("\r");
                if (promptWidth > 0) sb.Append(new string(' ', promptWidth));
                if (shownPos > 0) sb.Append(new string(' ', shownPos));

                Console.Write(sb.ToString());
                lastWidth = shown.Length;
            }
            catch { }
        }
    }
}
