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
    static class MarkdownRenderer
    {
        // ANSI コード
        const string RESET = "\u001b[0m";
        const string BOLD = "\u001b[1m";
        const string DIM = "\u001b[2m";
        const string ITALIC = "\u001b[3m";
        const string UNDERLINE = "\u001b[4m";
        const string RED = "\u001b[31m";
        const string GREEN = "\u001b[32m";
        const string YELLOW = "\u001b[33m";
        const string BLUE = "\u001b[34m";
        const string MAGENTA = "\u001b[35m";
        const string CYAN = "\u001b[36m";
        const string GRAY = "\u001b[90m";

        static bool useColor = true;

        public static void Configure(bool color)
        {
            useColor = color;
        }

        static string W(string code)
        {
            return useColor ? code : "";
        }

        static string R()
        {
            return useColor ? RESET : "";
        }

        // Markdown テキストを整形して出力する
        public static void Render(string md)
        {
            if (md == null) return;
            string[] lines = md.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            int i = 0;
            while (i < lines.Length)
            {
                string line = lines[i];

                // コードブロック
                if (IsFence(line))
                {
                    string lang = FenceLang(line);
                    i++;
                    StringBuilder code = new StringBuilder();
                    while (i < lines.Length && !IsFence(lines[i]))
                    {
                        code.Append(lines[i]).Append('\n');
                        i++;
                    }
                    if (i < lines.Length) i++;   // 閉じフェンスを飛ばす
                    RenderCodeBlock(code.ToString(), lang);
                    continue;
                }

                // 表(| ... |)
                if (IsTableRow(line) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
                {
                    List<string> rows = new List<string>();
                    List<string> align = ParseSeparator(lines[i + 1]);
                    rows.Add(line);
                    i += 2;
                    while (i < lines.Length && IsTableRow(lines[i]))
                    {
                        rows.Add(lines[i]);
                        i++;
                    }
                    RenderTable(rows, align);
                    continue;
                }

                // 水平線
                if (IsHorizontalRule(line))
                {
                    RenderRule();
                    i++;
                    continue;
                }

                // 見出し
                int level;
                string head = HeadingText(line, out level);
                if (head != null)
                {
                    RenderHeading(head, level);
                    i++;
                    continue;
                }

                // 引用
                if (line.TrimStart().StartsWith(">"))
                {
                    StringBuilder quote = new StringBuilder();
                    while (i < lines.Length && lines[i].TrimStart().StartsWith(">"))
                    {
                        string q = lines[i].TrimStart().Substring(1).TrimStart();
                        quote.Append(q).Append('\n');
                        i++;
                    }
                    RenderQuote(quote.ToString());
                    continue;
                }

                // 箇条書き(インデントで深さを表現する)
                if (IsBullet(line))
                {
                    int depth = IndentDepth(line);
                    string body = BulletText(line);
                    Console.WriteLine("  " + new string(' ', depth * 2)
                        + W(CYAN) + "・" + R() + Inline(body));
                    i++;
                    continue;
                }

                // 番号付きリスト
                int num;
                string numBody;
                if (NumberedText(line, out num, out numBody))
                {
                    int depth = IndentDepth(line);
                    Console.WriteLine("  " + new string(' ', depth * 2)
                        + W(CYAN) + num + ". " + R() + Inline(numBody));
                    i++;
                    continue;
                }

                // 空行
                if (line.Trim().Length == 0)
                {
                    Console.WriteLine();
                    i++;
                    continue;
                }

                // 通常の段落
                Console.WriteLine(Inline(line));
                i++;
            }
        }

        // ---------------- 見出し ----------------

        static string HeadingText(string line, out int level)
        {
            level = 0;
            string t = line.TrimStart();
            int n = 0;
            while (n < t.Length && t[n] == '#') n++;
            if (n < 1 || n > 6) return null;
            if (n < t.Length && t[n] != ' ') return null;
            level = n;
            return t.Substring(n).Trim();
        }

        static void RenderHeading(string text, int level)
        {
            string color = (level == 1) ? CYAN : (level == 2 ? BLUE : MAGENTA);
            Console.WriteLine();
            Console.WriteLine(W(BOLD) + W(color) + Inline(text) + R());
            if (level == 1)
                Console.WriteLine(W(GRAY) + new string('=', Math.Min(60, VisibleLength(text) + 4)) + R());
            else if (level == 2)
                Console.WriteLine(W(GRAY) + new string('-', Math.Min(60, VisibleLength(text) + 4)) + R());
        }

        // ---------------- コード ----------------

        static bool IsFence(string line)
        {
            string t = line.TrimStart();
            return t.StartsWith("```") || t.StartsWith("~~~");
        }

        static string FenceLang(string line)
        {
            string t = line.TrimStart();
            return t.Substring(3).Trim();
        }

        static void RenderCodeBlock(string code, string lang)
        {
            Console.WriteLine();

            string[] codeLines = code.Replace("\r\n", "\n").Split('\n');
            int width = 0;
            for (int i = 0; i < codeLines.Length; i++)
                width = Math.Max(width, VisibleLength(codeLines[i]));
            if (width < 8) width = 8;

            // 上枠(言語名があれば枠内に表示する)
            string top;
            if (lang.Length > 0)
            {
                string label = " " + lang + " ";
                int rest = width + 2 - VisibleLength(label);
                if (rest < 0) rest = 0;
                top = "  ┌" + label + new string('─', rest) + "┐";
            }
            else
            {
                top = "  ┌" + new string('─', width + 2) + "┐";
            }
            Console.WriteLine(W(GRAY) + top + R());
            for (int i = 0; i < codeLines.Length; i++)
            {
                if (i == codeLines.Length - 1 && codeLines[i].Length == 0) break;
                Console.WriteLine(W(GRAY) + "  │" + R() + " " + W(GREEN) + codeLines[i]
                    + new string(' ', width - VisibleLength(codeLines[i])) + R() + " "
                    + W(GRAY) + "│" + R());
            }
            Console.WriteLine(W(GRAY) + "  └" + new string('─', width + 2) + "┘" + R());
            Console.WriteLine();
        }

        // ---------------- 表 ----------------

        static bool IsTableRow(string line)
        {
            string t = line.Trim();
            return t.Length > 2 && t.StartsWith("|") && t.EndsWith("|");
        }

        static bool IsTableSeparator(string line)
        {
            string t = line.Trim();
            if (!IsTableRow(t)) return false;
            foreach (char c in t)
                if (c != '|' && c != '-' && c != ':' && c != ' ' && c != '\t') return false;
            return t.IndexOf('-') >= 0;
        }

        static List<string> SplitRow(string line)
        {
            string t = line.Trim();
            if (t.StartsWith("|")) t = t.Substring(1);
            if (t.EndsWith("|")) t = t.Substring(0, t.Length - 1);

            List<string> cells = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool inCode = false;
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (c == '`') inCode = !inCode;
                if (c == '|' && !inCode)
                {
                    cells.Add(cur.ToString().Trim());
                    cur.Length = 0;
                }
                else cur.Append(c);
            }
            cells.Add(cur.ToString().Trim());
            return cells;
        }

        static List<string> ParseSeparator(string line)
        {
            List<string> cells = SplitRow(line);
            List<string> align = new List<string>();
            foreach (string c in cells)
            {
                bool left = c.StartsWith(":");
                bool right = c.EndsWith(":");
                if (left && right) align.Add("center");
                else if (right) align.Add("right");
                else align.Add("left");
            }
            return align;
        }

        static void RenderTable(List<string> rows, List<string> align)
        {
            List<List<string>> cells = new List<List<string>>();
            for (int i = 0; i < rows.Count; i++)
                cells.Add(SplitRow(rows[i]));

            int cols = 0;
            for (int i = 0; i < cells.Count; i++)
                cols = Math.Max(cols, cells[i].Count);
            if (cols == 0) return;

            // 列幅(表示上の長さで計算)
            int[] widths = new int[cols];
            for (int r = 0; r < cells.Count; r++)
                for (int c = 0; c < cols; c++)
                {
                    string v = (c < cells[r].Count) ? cells[r][c] : "";
                    widths[c] = Math.Max(widths[c], VisibleLength(StripMarkdown(v)));
                }
            for (int c = 0; c < cols; c++)
                widths[c] = Math.Max(widths[c], 1);

            Console.WriteLine();

            // 罫線
            string top = "  ┌" + BuildRule(widths, '┬') + "┐";
            string mid = "  ├" + BuildRule(widths, '┼') + "┤";
            string bot = "  └" + BuildRule(widths, '┴') + "┘";

            Console.WriteLine(W(GRAY) + top + R());

            for (int r = 0; r < cells.Count; r++)
            {
                if (r == 1) Console.WriteLine(W(GRAY) + mid + R());   // ヘッダー区切り

                StringBuilder sb = new StringBuilder();
                sb.Append(W(GRAY)).Append("  │").Append(R());
                for (int c = 0; c < cols; c++)
                {
                    string raw = (c < cells[r].Count) ? cells[r][c] : "";
                    string plain = StripMarkdown(raw);
                    int pad = widths[c] - VisibleLength(plain);
                    string a = (c < align.Count) ? align[c] : "left";

                    int leftPad = 0, rightPad = pad;
                    if (a == "right") { leftPad = pad; rightPad = 0; }
                    else if (a == "center") { leftPad = pad / 2; rightPad = pad - leftPad; }

                    sb.Append(' ').Append(new string(' ', leftPad));
                    // ヘッダー行は太字
                    if (r == 0) sb.Append(W(BOLD)).Append(W(CYAN));
                    sb.Append(Inline(raw));
                    if (r == 0) sb.Append(R());
                    sb.Append(new string(' ', rightPad)).Append(' ');
                    sb.Append(W(GRAY)).Append("│").Append(R());
                }
                Console.WriteLine(sb.ToString());
            }

            Console.WriteLine(W(GRAY) + bot + R());
            Console.WriteLine();
        }

        static string BuildRule(int[] widths, char junction)
        {
            StringBuilder sb = new StringBuilder();
            for (int c = 0; c < widths.Length; c++)
            {
                if (c > 0) sb.Append(junction);
                sb.Append(new string('─', widths[c] + 2));
            }
            return sb.ToString();
        }

        // ---------------- その他 ----------------

        static bool IsHorizontalRule(string line)
        {
            string t = line.Trim();
            if (t.Length < 3) return false;
            char first = t[0];
            if (first != '-' && first != '*' && first != '_') return false;
            for (int i = 0; i < t.Length; i++)
                if (t[i] != first && t[i] != ' ') return false;
            return true;
        }

        static void RenderRule()
        {
            int width = 60;
            try { int w = Console.WindowWidth; if (w > 4) width = Math.Min(60, w - 4); } catch { }
            Console.WriteLine(W(GRAY) + new string('─', width) + R());
        }

        static void RenderQuote(string text)
        {
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i == lines.Length - 1 && lines[i].Length == 0) break;
                Console.WriteLine(W(GRAY) + "  │ " + R() + W(ITALIC) + Inline(lines[i]) + R());
            }
        }

        static bool IsBullet(string line)
        {
            string t = line.TrimStart();
            if (t.Length < 2) return false;
            char c = t[0];
            if (c != '-' && c != '*' && c != '+') return false;
            return t[1] == ' ';
        }

        static string BulletText(string line)
        {
            string t = line.TrimStart();
            return t.Substring(2).Trim();
        }

        // 箇条書きのインデント(先頭の空白数)から深さを求める
        static int IndentDepth(string line)
        {
            int n = 0;
            while (n < line.Length && (line[n] == ' ' || line[n] == '\t'))
            {
                n += (line[n] == '\t') ? 2 : 1;
            }
            return Math.Min(6, n / 2);
        }

        static bool NumberedText(string line, out int num, out string body)
        {
            num = 0;
            body = "";
            string t = line.TrimStart();
            int i = 0;
            while (i < t.Length && char.IsDigit(t[i])) i++;
            if (i == 0) return false;
            if (i >= t.Length || (t[i] != '.' && t[i] != ')')) return false;
            if (i + 1 >= t.Length || t[i + 1] != ' ') return false;
            int n;
            if (!int.TryParse(t.Substring(0, i), out n)) return false;
            num = n;
            body = t.Substring(i + 2).Trim();
            return true;
        }

        // ---------------- インライン書式 ----------------

        // インラインの Markdown(太字・斜体・コード・リンク)を ANSI に変換する
        public static string Inline(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];

                // インラインコード `code`
                if (c == '`')
                {
                    int end = s.IndexOf('`', i + 1);
                    if (end > i)
                    {
                        string code = s.Substring(i + 1, end - i - 1);
                        sb.Append(W(YELLOW)).Append(code).Append(R());
                        i = end + 1;
                        continue;
                    }
                }

                // 太字 **text** / __text__
                if ((c == '*' || c == '_') && i + 1 < s.Length && s[i + 1] == c)
                {
                    string marker = new string(c, 2);
                    int end = s.IndexOf(marker, i + 2);
                    if (end > i + 1)
                    {
                        string inner = s.Substring(i + 2, end - i - 2);
                        sb.Append(W(BOLD)).Append(Inline(inner)).Append(R());
                        i = end + 2;
                        continue;
                    }
                }

                // 斜体 *text* / _text_
                if (c == '*' || c == '_')
                {
                    int end = s.IndexOf(c, i + 1);
                    if (end > i)
                    {
                        string inner = s.Substring(i + 1, end - i - 1);
                        if (inner.Length > 0 && inner.IndexOf(' ') != 0)
                        {
                            sb.Append(W(ITALIC)).Append(Inline(inner)).Append(R());
                            i = end + 1;
                            continue;
                        }
                    }
                }

                // 取り消し線 ~~text~~
                if (c == '~' && i + 1 < s.Length && s[i + 1] == '~')
                {
                    int end = s.IndexOf("~~", i + 2);
                    if (end > i + 1)
                    {
                        string inner = s.Substring(i + 2, end - i - 2);
                        sb.Append(W(DIM)).Append(Inline(inner)).Append(R());
                        i = end + 2;
                        continue;
                    }
                }

                // リンク [text](url)
                if (c == '[')
                {
                    int close = s.IndexOf(']', i + 1);
                    if (close > i && close + 1 < s.Length && s[close + 1] == '(')
                    {
                        int paren = s.IndexOf(')', close + 2);
                        if (paren > close)
                        {
                            string text = s.Substring(i + 1, close - i - 1);
                            string url = s.Substring(close + 2, paren - close - 2);
                            sb.Append(W(UNDERLINE)).Append(W(BLUE)).Append(Inline(text)).Append(R());
                            if (url.Length > 0)
                                sb.Append(' ').Append(W(GRAY)).Append("(").Append(url).Append(")").Append(R());
                            i = paren + 1;
                            continue;
                        }
                    }
                }

                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        // Markdown の記号を除いた表示用テキスト(幅の計算用)
        public static string StripMarkdown(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '`')
                {
                    int end = s.IndexOf('`', i + 1);
                    if (end > i) { sb.Append(s.Substring(i + 1, end - i - 1)); i = end + 1; continue; }
                }
                if ((c == '*' || c == '_') && i + 1 < s.Length && s[i + 1] == c)
                {
                    string marker = new string(c, 2);
                    int end = s.IndexOf(marker, i + 2);
                    if (end > i + 1) { sb.Append(s.Substring(i + 2, end - i - 2)); i = end + 2; continue; }
                }
                if (c == '*' || c == '_')
                {
                    int end = s.IndexOf(c, i + 1);
                    if (end > i) { sb.Append(s.Substring(i + 1, end - i - 1)); i = end + 1; continue; }
                }
                if (c == '~' && i + 1 < s.Length && s[i + 1] == '~')
                {
                    int end = s.IndexOf("~~", i + 2);
                    if (end > i + 1) { sb.Append(s.Substring(i + 2, end - i - 2)); i = end + 2; continue; }
                }
                if (c == '[')
                {
                    int close = s.IndexOf(']', i + 1);
                    if (close > i && close + 1 < s.Length && s[close + 1] == '(')
                    {
                        int paren = s.IndexOf(')', close + 2);
                        if (paren > close)
                        {
                            sb.Append(s.Substring(i + 1, close - i - 1));
                            i = paren + 1;
                            continue;
                        }
                    }
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        // ANSI エスケープを除いた表示上の長さ。
        // 全角文字は 2 桁として数える。
        public static int VisibleLength(string s)
        {
            if (s == null) return 0;
            int n = 0;
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                // ANSI エスケープシーケンスをスキップ
                if (c == '\u001b' && i + 1 < s.Length && s[i + 1] == '[')
                {
                    i += 2;
                    while (i < s.Length && !(s[i] >= '@' && s[i] <= '~')) i++;
                    if (i < s.Length) i++;
                    continue;
                }
                n += IsWide(c) ? 2 : 1;
                i++;
            }
            return n;
        }

        // 全角(2 桁幅)かどうか
        public static bool IsWide(char c)
        {
            if (c < 0x1100) return false;
            return (c <= 0x115F)          // ハングル字母
                || (c >= 0x2E80 && c <= 0xA4CF)   // CJK 部首・漢字・かな
                || (c >= 0xA960 && c <= 0xA97F)
                || (c >= 0xAC00 && c <= 0xD7A3)   // ハングル音節
                || (c >= 0xF900 && c <= 0xFAFF)   // CJK 互換漢字
                || (c >= 0xFE30 && c <= 0xFE6F)   // CJK 互換形
                || (c >= 0xFF00 && c <= 0xFF60)   // 全角英数
                || (c >= 0xFFE0 && c <= 0xFFE6);
        }
    }

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
}
