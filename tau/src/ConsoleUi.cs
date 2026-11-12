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
    static class ConsoleUi
    {
        static bool color = DetectColor();

        public static bool ColorEnabled { get { return color; } }

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
            Dim("▶ Tool: ");
            Accent(name + (detail.Length > 0 ? ("  " + detail) : ""));
            Console.Write("\n");
        }

        public static void Banner(Config cfg, SessionCtx ctx, bool resumed)
        {
            Console.WriteLine();
            Bold("tau");
            Muted(" - console AI agent");
            Console.WriteLine();
            Console.WriteLine("  Model  : " + cfg.Model);
            Console.WriteLine("  Tools  : " + (cfg.AllowTools ? "on" : "off"));
            Console.WriteLine("  Config : " + cfg.ConfigPath);
            if (ctx != null)
                Console.WriteLine("  Session: " + ctx.Id + "  (" + ctx.Title + ")"
                    + (resumed ? "  (resumed)" : ""));
            Console.WriteLine();
            Dim("  /help for commands, /exit to quit");
            Console.WriteLine();
        }
    }

    // =======================================================================
    //  対話ループ
    // =======================================================================
}
