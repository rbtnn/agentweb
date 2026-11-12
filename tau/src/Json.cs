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
