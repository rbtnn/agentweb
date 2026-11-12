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
                n += WidthOf(c);
                i++;
            }
            return n;
        }

        // East Asian Width が "Ambiguous"(A) の範囲。
        // 欧文端末では 1 桁、日本語端末では 2 桁で表示される文字。
        // ①②③ ←→ ■□ ★☆ ※ ℡ № Ⅰ ⅰ などが含まれる。
        // (開始, 終了) の組を並べる。私用領域は除いてある。
        static readonly int[] AmbiguousRanges = new int[]
        {
            0x00A1,0x00A1, 0x00A4,0x00A4, 0x00A7,0x00A8, 0x00AA,0x00AA,
            0x00AD,0x00AE, 0x00B0,0x00B4, 0x00B6,0x00BA, 0x00BC,0x00BF,
            0x00C6,0x00C6, 0x00D0,0x00D0, 0x00D7,0x00D8, 0x00DE,0x00E1,
            0x00E6,0x00E6, 0x00E8,0x00EA, 0x00EC,0x00ED, 0x00F0,0x00F0,
            0x00F2,0x00F3, 0x00F7,0x00FA, 0x00FC,0x00FC, 0x00FE,0x00FE,
            0x0101,0x0101, 0x0111,0x0111, 0x0113,0x0113, 0x011B,0x011B,
            0x0126,0x0127, 0x012B,0x012B, 0x0131,0x0133, 0x0138,0x0138,
            0x013F,0x0142, 0x0144,0x0144, 0x0148,0x014B, 0x014D,0x014D,
            0x0152,0x0153, 0x0166,0x0167, 0x016B,0x016B, 0x01CE,0x01CE,
            0x01D0,0x01D0, 0x01D2,0x01D2, 0x01D4,0x01D4, 0x01D6,0x01D6,
            0x01D8,0x01D8, 0x01DA,0x01DA, 0x01DC,0x01DC, 0x0251,0x0251,
            0x0261,0x0261, 0x02C4,0x02C4, 0x02C7,0x02C7, 0x02C9,0x02CB,
            0x02CD,0x02CD, 0x02D0,0x02D0, 0x02D8,0x02DB, 0x02DD,0x02DD,
            0x02DF,0x02DF, 0x0300,0x036F, 0x0391,0x03A1, 0x03A3,0x03A9,
            0x03B1,0x03C1, 0x03C3,0x03C9, 0x0401,0x0401, 0x0410,0x044F,
            0x0451,0x0451, 0x2010,0x2010, 0x2013,0x2016, 0x2018,0x2019,
            0x201C,0x201D, 0x2020,0x2022, 0x2024,0x2027, 0x2030,0x2030,
            0x2032,0x2033, 0x2035,0x2035, 0x203B,0x203B, 0x203E,0x203E,
            0x2074,0x2074, 0x207F,0x207F, 0x2081,0x2084, 0x20AC,0x20AC,
            0x2103,0x2103, 0x2105,0x2105, 0x2109,0x2109, 0x2113,0x2113,
            0x2116,0x2116, 0x2121,0x2122, 0x2126,0x2126, 0x212B,0x212B,
            0x2153,0x2154, 0x215B,0x215E, 0x2160,0x216B, 0x2170,0x2179,
            0x2189,0x2189, 0x2190,0x2199, 0x21B8,0x21B9, 0x21D2,0x21D2,
            0x21D4,0x21D4, 0x21E7,0x21E7, 0x2200,0x2200, 0x2202,0x2203,
            0x2207,0x2208, 0x220B,0x220B, 0x220F,0x220F, 0x2211,0x2211,
            0x2215,0x2215, 0x221A,0x221A, 0x221D,0x2220, 0x2223,0x2223,
            0x2225,0x2225, 0x2227,0x222C, 0x222E,0x222E, 0x2234,0x2237,
            0x223C,0x223D, 0x2248,0x2248, 0x224C,0x224C, 0x2252,0x2252,
            0x2260,0x2261, 0x2264,0x2267, 0x226A,0x226B, 0x226E,0x226F,
            0x2282,0x2283, 0x2286,0x2287, 0x2295,0x2295, 0x2299,0x2299,
            0x22A5,0x22A5, 0x22BF,0x22BF, 0x2312,0x2312, 0x2460,0x24E9,
            0x24EB,0x254B, 0x2550,0x2573, 0x2580,0x258F, 0x2592,0x2595,
            0x25A0,0x25A1, 0x25A3,0x25A9, 0x25B2,0x25B3, 0x25B6,0x25B7,
            0x25BC,0x25BD, 0x25C0,0x25C1, 0x25C6,0x25C8, 0x25CB,0x25CB,
            0x25CE,0x25D1, 0x25E2,0x25E5, 0x25EF,0x25EF, 0x2605,0x2606,
            0x2609,0x2609, 0x260E,0x260F, 0x261C,0x261C, 0x261E,0x261E,
            0x2640,0x2640, 0x2642,0x2642, 0x2660,0x2661, 0x2663,0x2665,
            0x2667,0x266A, 0x266C,0x266D, 0x266F,0x266F, 0x269E,0x269F,
            0x26BF,0x26BF, 0x26C6,0x26CD, 0x26CF,0x26D3, 0x26D5,0x26E1,
            0x26E3,0x26E3, 0x26E8,0x26E9, 0x26EB,0x26F1, 0x26F4,0x26F4,
            0x26F6,0x26F9, 0x26FB,0x26FC, 0x26FE,0x26FF, 0x273D,0x273D,
            0x2776,0x277F, 0x2B56,0x2B59, 0x3248,0x324F, 0xFE00,0xFE0F,
            0xFFFD,0xFFFD, 0x1F100,0x1F10A, 0x1F110,0x1F12D, 0x1F130,0x1F169,
            0x1F170,0x1F18D, 0x1F18F,0x1F190, 0x1F19B,0x1F1AC,
        };

        static bool IsAmbiguous(char c)
        {
            int cp = c;
            // 二分探索
            int lo = 0, hi = AmbiguousRanges.Length / 2 - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                int a = AmbiguousRanges[mid * 2];
                int b = AmbiguousRanges[mid * 2 + 1];
                if (cp < a) hi = mid - 1;
                else if (cp > b) lo = mid + 1;
                else return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        //  実際の端末での表示幅を測る
        //
        //  計算上 2 桁でも、フォントが 1 桁で描く端末もある(①②③ など)。
        //  その場合は計算と実表示が食い違い、罫線がずれる。
        //  文字を書いてカーソル位置を読み、実際に何桁進んだかを測る。
        // ------------------------------------------------------------------

        static Dictionary<int, int> measuredWidth = new Dictionary<int, int>();
        static bool measureAvailable = true;
        static bool measureDone = false;
        // 実測した Ambiguous の幅(0 = 不明, 1 = 半角, 2 = 全角)
        static int ambiguousMeasured = 0;

        // 端末で実際に測れるか(対話コンソールで、リダイレクトされていない)
        static bool CanMeasure()
        {
            if (!measureAvailable) return false;
            // Windows のコンソールでのみ測れる。
            // (他の環境ではカーソル位置を問い合わせずに取得できない)
            if (!Program.IsWindows()) return false;
            try
            {
                if (Console.IsOutputRedirected) return false;
                if (Console.IsInputRedirected) return false;
            }
            catch { return false; }

            // カーソル位置が取れるかを 1 度確認する
            return (CursorColumn() >= 0);
        }

        // 起動時に 1 度だけ、代表的な文字の実表示幅を測って覚えておく。
        // 描画のたびに測るとコンソールへの書き込みが増えるため。
        public static void MeasureWidthsOnce()
        {
            if (measureDone) return;
            measureDone = true;
            if (!CanMeasure()) { measureAvailable = false; return; }

            // 測る文字の一覧。
            //  - 基準となる全角(あ)と半角(A ｱ)
            //  - Ambiguous の各ブロックから代表を複数(丸数字・矢印・図形・
            //    星・記号・単位・ローマ数字・囲み文字)
            // 1 文字だけ測ると、その文字が特別扱いされている場合に誤判定する。
            string samples =
                "あ"                       // 全角の基準(W)
                + "Aｱ"                    // 半角の基準(Na / H)
                + "①②③④⑤⑥⑦⑧⑨⑩"      // 丸数字 U+2460..
                + "⑴⑵⑶"                  // 括弧付き数字 U+2474..
                + "ⅠⅡⅢⅰⅱⅲ"              // ローマ数字 U+2160..
                + "←→↑↓⇔⇒"              // 矢印 U+2190..
                + "■□▲△▼▽◆◇○●◎"      // 図形 U+25A0..
                + "★☆"                    // 星 U+2605..
                + "※〒"                    // 記号 U+203B / U+3012
                + "℡№"                    // U+2121 / U+2116
                + "㈱㈲㈹"                 // 囲み文字 U+3231..
                + "㌔㌘㍉㍑"               // 単位 U+3314..
                + "㊤㊥㊦"                 // 囲み漢字 U+32A4..
                + "〜"                     // 波ダッシュ U+301C(W)
                + "－"                     // 全角ハイフン U+FF0D(F)
                ;

            int measured = 0;
            int ambWide = 0;
            int ambNarrow = 0;

            try
            {
                for (int i = 0; i < samples.Length; i++)
                {
                    char c = samples[i];
                    int w = MeasureOne(c);
                    if (w <= 0) continue;

                    measuredWidth[(int)c] = w;
                    measured++;

                    if (IsAmbiguous(c))
                    {
                        if (w == 2) ambWide++;
                        else if (w == 1) ambNarrow++;
                    }
                }
            }
            catch
            {
                measureAvailable = false;
            }

            // Ambiguous の実幅を多数決で決める。
            // (ブロックによって描き方が違うフォントもある)
            if (ambWide > ambNarrow) ambiguousMeasured = 2;
            else if (ambNarrow > ambWide) ambiguousMeasured = 1;
            else ambiguousMeasured = 0;   // 判定できない

            if (Program.IsWindows() && LlmClient.DebugOn)
            {
                Console.Error.WriteLine("[width] measured=" + measured
                    + " ambiguous wide=" + ambWide + " narrow=" + ambNarrow
                    + " -> " + (ambiguousMeasured == 0 ? "unknown"
                                : ambiguousMeasured.ToString()));
            }
        }

        // 1 文字の実幅を測る。測れなければ 0。
        //
        // Console.CursorLeft は Mono や一部の端末で ESC[6n(カーソル位置問い合わせ)
        // を送って応答待ちになるため使わない。Windows では
        // GetConsoleScreenBufferInfo でカーソル位置を直接読む。
        static int MeasureOne(char c)
        {
            try
            {
                int before = CursorColumn();
                if (before < 0) return 0;

                Console.Write(c);
                int after = CursorColumn();
                if (after < 0) return 0;

                // 書いた文字を消してカーソルを元に戻す
                Console.Write("\b \b");

                int w = after - before;
                if (w >= 1 && w <= 2) return w;
                return 0;
            }
            catch { return 0; }
        }

        // 現在のカーソル桁位置。取得できなければ -1。
        // 端末に問い合わせを送らず、Windows API で直接読む。
        static int CursorColumn()
        {
            if (Program.IsWindows())
            {
                int col = Program.GetConsoleCursorColumn();
                if (col >= 0) return col;
            }

            // Windows 以外: Console.CursorLeft は問い合わせを送ることがあるので
            // 環境変数などで明示された場合のみ使う。
            return -1;
        }

        // 環境変数で強制する(測れない環境向け)
        //   TAU_AMBIGUOUS_WIDTH=1  … 1 桁(半角)として扱う
        //   TAU_AMBIGUOUS_WIDTH=2  … 2 桁(全角)として扱う
        static int ambiguousOverride = 0;

        static void ReadAmbiguousOverride()
        {
            try
            {
                string v = Environment.GetEnvironmentVariable("TAU_AMBIGUOUS_WIDTH");
                if (v != null)
                {
                    v = v.Trim();
                    if (v == "1" || v == "2") ambiguousOverride = (v == "1") ? 1 : 2;
                }
            }
            catch { }
        }

        // 測った結果から、Ambiguous の文字を全角とみなすか判断する。
        // 「あ」が 2 桁で「①」が 1 桁なら、この端末では 1 桁で描く。
        public static bool AmbiguousIsNarrow()
        {
            ReadAmbiguousOverride();
            if (ambiguousOverride != 0) return (ambiguousOverride == 1);

            // 実測した多数決の結果を使う
            if (ambiguousMeasured != 0) return (ambiguousMeasured == 1);

            // 測れていない場合は計算値(全角)を使う
            return false;
        }

        // 実測した Ambiguous の幅(0 = 不明, 1, 2)
        public static int MeasuredAmbiguousWidth()
        {
            ReadAmbiguousOverride();
            if (ambiguousOverride != 0) return ambiguousOverride;
            return ambiguousMeasured;
        }

        // 測った幅があればそれを返す。なければ計算値。
        static int WidthOf(char c)
        {
            // その文字を直接測っていればその値
            int w;
            if (measuredWidth.TryGetValue((int)c, out w)) return w;

            // Ambiguous は実測した多数決に従う(個別に測っていない文字も)
            if (IsAmbiguous(c))
            {
                int m = MeasuredAmbiguousWidth();
                if (m != 0) return m;
                return 2;   // 不明なら全角とみなす
            }

            return IsWide(c) ? 2 : 1;
        }

        // 全角(2 桁幅)かどうか。
        //
        // East Asian Width の W(全角)・F(半角相当の全角)に加えて、
        // A(Ambiguous) も全角として扱う。日本語のターミナルでは
        // ①②③ や ←→ が 2 桁で表示されるため、1 桁で計算すると
        // 表の桁がずれる。
        public static bool IsWide(char c)
        {
            int cp = c;

            // W / F : 常に 2 桁
            if (cp >= 0x1100 && cp <= 0x115F) return true;        // ハングル字母
            if (cp >= 0x2E80 && cp <= 0x303E) return true;        // CJK 部首・記号
            if (cp >= 0x3041 && cp <= 0x33FF) return true;        // かな・CJK 互換
            if (cp >= 0x3400 && cp <= 0x4DBF) return true;        // CJK 拡張 A
            if (cp >= 0x4E00 && cp <= 0x9FFF) return true;        // CJK 統合漢字
            if (cp >= 0xA000 && cp <= 0xA4CF) return true;        // イ文字
            if (cp >= 0xA960 && cp <= 0xA97F) return true;
            if (cp >= 0xAC00 && cp <= 0xD7A3) return true;        // ハングル音節
            if (cp >= 0xF900 && cp <= 0xFAFF) return true;        // CJK 互換漢字
            if (cp >= 0xFE10 && cp <= 0xFE19) return true;        // 縦書き形
            if (cp >= 0xFE30 && cp <= 0xFE6F) return true;        // CJK 互換形
            if (cp >= 0xFF01 && cp <= 0xFF60) return true;        // 全角英数
            if (cp >= 0xFFE0 && cp <= 0xFFE6) return true;        // 全角記号
            if (cp >= 0x1F300 && cp <= 0x1F64F) return true;      // 絵文字
            if (cp >= 0x1F900 && cp <= 0x1F9FF) return true;
            if (cp >= 0x20000 && cp <= 0x3FFFD) return true;      // CJK 拡張 B 以降

            // A : Ambiguous(日本語端末では 2 桁)
            if (IsAmbiguous(c)) return true;

            return false;
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
