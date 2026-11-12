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
    class Chat
    {
        readonly Config cfg;
        readonly SessionStore store;
        readonly Agent agent;

        SessionCtx ctx;
        bool cancelled;          // 現在のターンを中止するか
        bool ctrlCPressed;       // Ctrl-C が押されたか
        bool waitingForInput;    // 入力待ちの最中か

        public Chat(Config cfg)
        {
            this.cfg = cfg;
            this.store = new SessionStore();
            this.agent = new Agent(cfg);
        }

        // ---------------- セッションの読み込み ----------------

        // resume=true なら前回のセッションを開き、false なら必ず新規作成する。
        SessionCtx LoadOrCreate(string wantId, bool resume)
        {
            string id = "";
            if (wantId != null && wantId.Trim().Length > 0) id = wantId.Trim();
            // 再開指定のときだけ「最後に開いていたセッション」を使う。
            // /new では使わない(使うと直前と同じセッションが開き直されてしまう)。
            if (id.Length == 0 && resume) id = store.LastSessionId();

            if (id.Length > 0)
            {
                Dictionary<string, object> meta = store.Load(id);
                if (meta != null) return FromMeta(id, meta);
            }

            // 新規作成
            SessionCtx c = new SessionCtx();
            c.Id = store.NewId();
            c.Title = "New conversation";
            store.Save(c.Id, c.Messages, c.Title);
            store.SetLastSessionId(c.Id);
            return c;
        }

        SessionCtx FromMeta(string id, Dictionary<string, object> meta)
        {
            SessionCtx c = new SessionCtx();
            c.Id = id;
            c.Title = meta.ContainsKey("title") ? Json.AsString(meta["title"]) : "New conversation";
            c.Summary = meta.ContainsKey("summary") ? Json.AsString(meta["summary"]) : "";
            c.ContextStart = meta.ContainsKey("contextStart") ? Json.AsInt(meta["contextStart"]) : 0;
            if (meta.ContainsKey("messages") && meta["messages"] is List<object>)
                c.Messages = (List<object>)meta["messages"];
            return c;
        }

        void Save(SessionCtx c)
        {
            store.Save(c.Id, c.Messages, c.Title, c.Summary, c.ContextStart);
        }

        // 最初のユーザー発言からタイトルを作る
        void AutoTitle(SessionCtx c, string userMessage)
        {
            if (c.Title != null && c.Title != "New conversation" && c.Title.Trim().Length > 0) return;
            string t = (userMessage ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (t.Length == 0) return;
            if (t.Length > 40) t = t.Substring(0, 40) + "…";
            c.Title = t;
        }

        // ---------------- 実行 ----------------

        public void Run(string sessionArg)
        {
            ctx = LoadOrCreate(sessionArg, true);
            bool resumed = ctx.Messages.Count > 0;
            ConsoleUi.Banner(cfg, ctx, resumed);

            if (resumed) PrintHistory(ctx);

            // Ctrl-C はプロセスを終了させず、現在のターンだけ中止する。
            // e.Cancel = true で既定の終了動作を抑止する。
            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;          // プロセスを終了しない
                ctrlCPressed = true;
                cancelled = true;         // 実行中のターンを中止する
                LlmClient.Cancel();       // 通信中なら HTTP リクエストを中断する

                // 入力待ちの途中なら、すぐ改行してプロンプトを出し直す
                if (waitingForInput)
                {
                    try { Console.Write("\n"); } catch { }
                }
            };

            int nulLost = 0;   // 連続して入力が NUL で失われた回数

            while (true)
            {
                string line;
                ctrlCPressed = false;
                if (LlmClient.DebugOn)
                    ConsoleUi.Dim("  [waiting for input]\n");
                waitingForInput = true;
                try
                {
                    line = LineEditor.ReadLineColored("> ", "36");
                }
                catch (Exception ex)
                {
                    waitingForInput = false;
                    // 入力の読み取りで例外が出たら、黙って終了せず原因を出す
                    ConsoleUi.Danger("\n⚠ Failed to read input: " + ex.GetType().Name
                        + ": " + ex.Message + "\n");
                    if (LlmClient.DebugOn)
                        ConsoleUi.Dim("  " + ex.ToString().Replace("\n", "\n  ") + "\n");
                    break;
                }
                waitingForInput = false;

                if (LlmClient.DebugOn)
                    ConsoleUi.Dim("  [input] length=" + (line == null ? -1 : line.Length)
                        + " bytes=" + (line == null ? -1 : Encoding.UTF8.GetByteCount(line))
                        + (line == null ? " (null)" : "") + "\n");

                if (ctrlCPressed)
                {
                    // 入力待ちの途中で Ctrl-C: 入力行を捨ててプロンプトに戻る
                    ctrlCPressed = false;
                    cancelled = false;
                    ConsoleUi.Dim("  (cancelled input)\n");
                    continue;
                }

                if (line == null) break;   // Ctrl-G / Ctrl-D / EOF

                // 入力が NUL 文字に化けていないか確認する。
                // Windows のコンソールで入力エンコーディングが合っていないと、
                // 日本語が NUL になり、SanitizeInput で全て消えてしまう。
                // そのまま黙って再入力待ちにすると「応答が返らない」ように見えるため、
                // 何が起きたかを表示する。
                int nulCount = Program.CountNul(line);
                if (nulCount > 0 && nulCount >= line.Length)
                {
                    nulLost++;
                    ConsoleUi.Danger("⚠ The input was lost (all " + line.Length
                        + " characters became NUL).\n");
                    ConsoleUi.Dim("  The console input encoding does not match.\n");
                    ConsoleUi.Dim("  Try one of the following:\n");
                    ConsoleUi.Dim("   - Run `chcp 65001` before starting tau (or run it in Windows Terminal)\n");
                    ConsoleUi.Dim("   - Set TAU_DEBUG=1 and check the reported input encoding\n");
                    ConsoleUi.Dim("   - Paste the text from a file:  tau --session <id> < input.txt\n");
                    ConsoleUi.Info("input encoding: " + Program.SafeInputEncoding());

                    if (nulLost >= 2)
                    {
                        // 何度も失われる場合は、ここで打ち切って原因を伝える
                        ConsoleUi.Danger("  The input has been lost " + nulLost
                            + " times in a row; stopping here.\n");
                        ConsoleUi.Dim("  Change the console code page (chcp 65001) and start tau again,\n");
                        ConsoleUi.Dim("  or write your text to a file and pipe it in:\n");
                        ConsoleUi.Dim("    tau.exe < input.txt\n");
                        break;
                    }
                    continue;
                }
                nulLost = 0;

                if (nulCount > 0)
                {
                    // 一部だけ NUL に化けた場合も、欠落を伝える
                    ConsoleUi.Danger("⚠ " + nulCount + " character(s) of the input became NUL and were dropped.\n");
                    ConsoleUi.Info("input encoding: " + Program.SafeInputEncoding());
                }

                line = Program.SanitizeInput(line);

                // Ctrl-V が SYN(0x16)として届いた場合は、クリップボードの
                // 内容に置き換える(コンソールの行編集が使えない環境向け)。
                if (line.IndexOf('\u0016') >= 0)
                {
                    string clip = LineEditor.GetClipboardText();
                    if (clip != null && clip.Length > 0)
                    {
                        // 貼り付け先が 1 行だけなので、改行は空白にする
                        clip = clip.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
                        line = line.Replace("\u0016", clip);
                    }
                    else
                    {
                        ConsoleUi.Danger("⚠ Ctrl-V: the clipboard is empty"
                            + " or could not be read.\n");
                        continue;
                    }
                }

                line = line.Trim();
                if (line.Length == 0) continue;   // 空行は何もしない

                if (line.StartsWith("/"))
                {
                    if (HandleCommand(line)) continue;
                    break;   // /exit
                }

                Turn(line);
            }

            Console.WriteLine();
            ConsoleUi.Dim("Bye. Session: " + ctx.Id);
        }

        // コマンドを処理する。true なら継続、false なら終了。
        bool HandleCommand(string line)
        {
            string[] parts = line.Split(new char[] { ' ' }, 2);
            string cmd = parts[0].ToLowerInvariant();
            string arg = (parts.Length > 1) ? parts[1].Trim() : "";

            switch (cmd)
            {
                case "/exit":
                case "/quit":
                case "/q":
                    return false;

                case "/help":
                case "/?":
                    Program.PrintHelp();
                    return true;

                case "/clear":
                case "/cls":
                    ClearSession();
                    return true;

                case "/new":
                    ctx = LoadOrCreate("", false);   // 必ず新しいセッションを作る
                    ConsoleUi.Info("Started a new session: " + ctx.Id);
                    return true;

                case "/sessions":
                    ShowSessions();
                    return true;

                case "/delete":
                case "/del":
                    DeleteSession(arg);
                    return true;

                case "/open":
                    if (arg.Length == 0) { ConsoleUi.Danger("Usage: /open <session id>\n"); return true; }
                    {
                        Dictionary<string, object> meta = store.Load(arg);
                        if (meta == null) { ConsoleUi.Danger("No such session: " + arg + "\n"); return true; }
                        ctx = FromMeta(arg, meta);
                        store.SetLastSessionId(ctx.Id);
                        ConsoleUi.Info("Opened session: " + ctx.Id + "  (" + ctx.Title + ")");
                        PrintHistory(ctx);
                    }
                    return true;

                case "/model":
                    if (arg.Length == 0)
                    {
                        Console.WriteLine("  Current model: " + cfg.Model);
                        ConsoleUi.Dim("  Change: /model <name>\n");
                        return true;
                    }
                    cfg.Model = arg;
                    cfg.Save();
                    ConsoleUi.Info("Switched model: " + cfg.Model);
                    return true;

                case "/tools":
                    cfg.AllowTools = !cfg.AllowTools;
                    cfg.Save();
                    ConsoleUi.Info("Tools: " + (cfg.AllowTools ? "on" : "off"));
                    return true;

                case "/config":
                case "/settings":
                    {
                        string sub = arg.Trim().ToLowerInvariant();
                        if (sub == "edit" || sub == "e")
                        {
                            Settings.Edit(cfg);
                        }
                        else if (sub.Length == 0)
                        {
                            Settings.Show(cfg);
                        }
                        else
                        {
                            ConsoleUi.Danger("Usage: /config [edit]\n");
                        }
                    }
                    return true;

                case "/set":
                    {
                        if (arg.Length == 0)
                        {
                            ConsoleUi.Dim("  Usage: /set <key> <value>\n");
                            ConsoleUi.Dim("  e.g. /set model gpt-4o\n");
                            ConsoleUi.Dim("      /set allowTools false\n");
                            ConsoleUi.Dim("      /set contextWindow 128000\n");
                            ConsoleUi.Dim("  See /config for the list of keys\n");
                            return true;
                        }
                        // "<key> <value>" を分割する(値に空白を含められるようにする)
                        int sp = arg.IndexOf(' ');
                        string key = (sp < 0) ? arg : arg.Substring(0, sp);
                        string value = (sp < 0) ? "" : arg.Substring(sp + 1);

                        Settings.Field f = Settings.Find(key);
                        if (f == null)
                        {
                            ConsoleUi.Danger("Unknown setting: " + key + "\n");
                            ConsoleUi.Dim("  Use /config to list the settings\n");
                            return true;
                        }
                        if (value.Trim().Length == 0 && f.Kind != "string" && f.Kind != "secret")
                        {
                            ConsoleUi.Dim("  Current value: " + Settings.Display(cfg, f) + "\n");
                            ConsoleUi.Dim("  Usage: /set " + f.Key + " <value>\n");
                            return true;
                        }
                        if (!Settings.Apply(cfg, f, value))
                        {
                            ConsoleUi.Danger("Invalid value: " + value + "\n");
                            ConsoleUi.Dim("  Current value: " + Settings.Display(cfg, f) + "\n");
                            return true;
                        }
                        cfg.Save();
                        ConsoleUi.Info(f.Key + " = " + Settings.Display(cfg, f) + "  (updated)");
                    }
                    return true;

                default:
                    ConsoleUi.Danger("Unknown command: " + cmd + "\n");
                    ConsoleUi.Dim("  Use /help to list the commands\n");
                    return true;
            }
        }

        // 一覧に番号を振って表示する(/delete で番号を指定できるようにするため)
        void ShowSessions()
        {
            List<object> list = store.List();
            if (list.Count == 0) { ConsoleUi.Info("No saved sessions"); return; }
            Console.WriteLine("  Saved sessions (" + store.DataDir + ")");
            for (int i = 0; i < list.Count; i++)
            {
                Dictionary<string, object> s = list[i] as Dictionary<string, object>;
                if (s == null) continue;
                string id = Json.AsString(s.ContainsKey("id") ? s["id"] : "");
                string title = Json.AsString(s.ContainsKey("title") ? s["title"] : "");
                int count = s.ContainsKey("count") ? Json.AsInt(s["count"]) : 0;
                string mark = (id == ctx.Id) ? "* " : "  ";
                Console.WriteLine(mark + (i + 1).ToString().PadLeft(2) + ". "
                    + id + "  " + title + "  (" + count + " msgs)");
            }
            Console.WriteLine();
            ConsoleUi.Dim("  * = current session. Delete with /delete <number|id>\n");
        }

        // 番号(1 始まり)または ID からセッション ID を解決する
        string ResolveSessionId(string arg)
        {
            string a = (arg ?? "").Trim();
            if (a.Length == 0) return "";

            int n;
            if (int.TryParse(a, out n))
            {
                List<object> list = store.List();
                if (n >= 1 && n <= list.Count)
                {
                    Dictionary<string, object> s = list[n - 1] as Dictionary<string, object>;
                    if (s != null && s.ContainsKey("id")) return Json.AsString(s["id"]);
                }
                return "";
            }
            return a;   // ID として扱う
        }

        // セッションを削除する
        // 現在のセッションの内容(会話履歴)をクリアする。
        // セッション自体(ID・タイトル)は残す。
        void ClearSession()
        {
            if (ctx.Messages.Count == 0)
            {
                ConsoleUi.Info("This session has no messages yet");
                return;
            }

            string ans = LineEditor.ReadLine("  Clear this session's history? ("
                + ctx.Messages.Count + " msgs)  [y/N]: ");
            ans = Program.SanitizeInput(ans).Trim().ToLowerInvariant();
            if (ans != "y" && ans != "yes")
            {
                ConsoleUi.Dim("  Cancelled\n");
                return;
            }

            // 履歴・要約・コンテキスト開始位置をリセットする
            ctx.Messages = new List<object>();
            ctx.Summary = "";
            ctx.ContextStart = 0;
            ctx.LastTotalTokens = 0;
            Save(ctx);

            ConsoleUi.Info("Cleared the session history: " + ctx.Id + "  (" + ctx.Title + ")");
            PrintHistory(ctx);
        }

        void DeleteSession(string arg)
        {
            if ((arg ?? "").Trim().Length == 0)
            {
                ConsoleUi.Danger("Specify the session to delete\n");
                ConsoleUi.Dim("  Usage: /delete <number|id>   (numbers come from /sessions)\n");
                return;
            }

            string id = ResolveSessionId(arg);
            if (id.Length == 0)
            {
                ConsoleUi.Danger("No session with that number: " + arg + "\n");
                ConsoleUi.Dim("  Check the numbers with /sessions\n");
                return;
            }

            Dictionary<string, object> meta = store.Load(id);
            if (meta == null)
            {
                ConsoleUi.Danger("No such session: " + arg + "\n");
                return;
            }

            string title = meta.ContainsKey("title") ? Json.AsString(meta["title"]) : "";

            // 誤操作を防ぐため確認する
            string ans = LineEditor.ReadLine("  Delete " + id + "  (" + title + ")?  [y/N]: ");
            ans = Program.SanitizeInput(ans).Trim().ToLowerInvariant();
            if (ans != "y" && ans != "yes")
            {
                ConsoleUi.Dim("  Cancelled\n");
                return;
            }

            bool wasCurrent = (id == ctx.Id);
            if (!store.Delete(id))
            {
                ConsoleUi.Danger("Could not delete: " + id + "\n");
                return;
            }
            ConsoleUi.Info("Deleted: " + id + "  (" + title + ")");

            // 表示中のセッションを消した場合は、別のセッションへ切り替える
            if (wasCurrent)
            {
                ctx = LoadOrCreate("", true);
                ConsoleUi.Info("Switched session: " + ctx.Id + "  (" + ctx.Title + ")");
                PrintHistory(ctx);
            }
        }

        // 過去の会話を表示する(続きから再開したとき)
        void PrintHistory(SessionCtx c)
        {
            Console.WriteLine();
            ConsoleUi.Dim("---- conversation so far ----");
            Console.WriteLine();
            foreach (object o in c.Messages)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (m == null) continue;
                string role = m.ContainsKey("role") ? Json.AsString(m["role"]) : "";
                string content = m.ContainsKey("content") ? Json.AsString(m["content"]) : "";

                if (role == "user")
                {
                    ConsoleUi.Accent("> ");
                    Console.WriteLine(content);
                }
                else if (role == "assistant")
                {
                    // 思考 → ツール実行 → 本文 の順に、それぞれ改行して表示する
                    if (m.ContainsKey("think"))
                    {
                        string think = Json.AsString(m["think"]);
                        if (think.Trim().Length > 0)
                            ConsoleUi.Think("  [thinking] " + think + "\n");
                    }
                    if (m.ContainsKey("steps") && m["steps"] is List<object>)
                    {
                        List<object> steps = (List<object>)m["steps"];
                        for (int i = 0; i < steps.Count; i++)
                        {
                            string st = Json.AsString(steps[i]);
                            if (st.Length > 0)
                                ConsoleUi.Dim("  ▶ " + st + "\n");
                        }
                    }
                    if (content.Trim().Length > 0)
                    {
                        MarkdownRenderer.Render(content);
                        Console.WriteLine();
                    }
                }
                else if (role == "tool")
                {
                    string nn = m.ContainsKey("name") ? Json.AsString(m["name"]) : "";
                    ConsoleUi.Dim("  [tool result] " + nn + "\n");
                }
            }
            ConsoleUi.Dim("---- continue below ----");
            Console.WriteLine();
        }

        // 1 ターン実行する
        // ストリーミング中の進行状況。同じ行を上書きする。
        static readonly string[] SPINNER = { "|", "/", "-", "\\" };
        int progressTick = 0;
        int progressLen = 0;

        void ShowProgress(int chars)
        {
            progressTick++;
            string spin = SPINNER[progressTick % SPINNER.Length];
            string msg;
            if (chars <= 0)
                msg = "  " + spin + " connecting...";
            else
                msg = "  " + spin + " receiving... " + chars + " chars";
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append('\r').Append(msg);
                int pad = progressLen - msg.Length;
                if (pad > 0) sb.Append(new string(' ', pad));
                Console.Write(sb.ToString());
                progressLen = msg.Length;
            }
            catch { }
        }

        void ClearProgress()
        {
            if (progressLen <= 0) return;
            try
            {
                Console.Write("\r" + new string(' ', progressLen) + "\r");
            }
            catch { }
            progressLen = 0;
        }

        void Turn(string userMessage)
        {
            cancelled = false;
            AutoTitle(ctx, userMessage);

            // 出力の種類。種類が変わったら改行を入れてから表示する。
            //   0 = なし, 1 = 思考, 2 = 本文
            // ツール実行は単独の行として出力し、そのあとは「なし」に戻す。
            const int KIND_NONE = 0, KIND_THINK = 1, KIND_CONTENT = 2;
            int kind = KIND_NONE;
            StringBuilder contentBuf = new StringBuilder();   // 本文(Markdown)
            progressLen = 0;
            progressTick = 0;

            Console.WriteLine();
            if (LlmClient.DebugOn)
                ConsoleUi.Dim("  sending " + userMessage.Length + " chars...\n");
            ShowProgress(0);
            try
            {
                string reply = agent.RunStream(ctx, userMessage,
                    delegate (SessionCtx c) { Save(c); },
                    delegate (string d)
                    {
                        // 本文はバッファする(Markdown として整形して表示するため)
                        if (kind != KIND_CONTENT)
                        {
                            if (kind != KIND_NONE) Console.WriteLine();
                            kind = KIND_CONTENT;
                        }
                        contentBuf.Append(d);
                        // Markdown として整形するため本文はバッファするが、
                        // 何も表示されないと「応答が無い」ように見えるので、
                        // 受信した文字数を進行状況として表示する。
                        ShowProgress(contentBuf.Length);
                    },
                    delegate (string d)
                    {
                        if (kind != KIND_THINK)
                        {
                            if (kind != KIND_NONE) Console.WriteLine();
                            kind = KIND_THINK;
                            Console.Write("  ");
                            ConsoleUi.Dim("[thinking] ");
                        }
                        ConsoleUi.Think(d);
                    },
                    delegate (string name, string detail)
                    {
                        ClearProgress();
                        // ツール実行は必ず単独の行にする
                        if (kind != KIND_NONE) Console.WriteLine();
                        ConsoleUi.Tool(name, detail);
                        kind = KIND_NONE;   // ツール行は改行済みなので次はそのまま始められる
                    },
                    delegate () { return cancelled; });

                ClearProgress();

                if (cancelled)
                {
                    // Ctrl-C で中止した。途中までの本文は出さず、その旨を伝える。
                    if (kind != KIND_NONE) Console.WriteLine();
                    ConsoleUi.Dim("  (cancelled)\n");
                    Console.WriteLine();
                    cancelled = false;
                    ctrlCPressed = false;
                    return;
                }

                // 本文を Markdown として整形して表示する
                string body = (contentBuf.Length > 0)
                    ? contentBuf.ToString()
                    : (reply ?? "");
                if (body.Trim().Length > 0)
                {
                    if (kind != KIND_NONE) Console.WriteLine();
                    MarkdownRenderer.Render(body);
                    Console.WriteLine();
                }
                else if (kind != KIND_NONE)
                {
                    Console.WriteLine();
                }
            }
            catch (Exception ex)
            {
                ClearProgress();
                Console.WriteLine();

                if (cancelled)
                {
                    // Ctrl-C による中止はエラーとして扱わない
                    ConsoleUi.Dim("  (cancelled)\n");
                    Console.WriteLine();
                    cancelled = false;
                    ctrlCPressed = false;
                    return;
                }

                string msg;
                WebException wex = ex as WebException;
                if (wex != null) msg = LlmClient.DescribeWebException(wex);
                else msg = ex.Message;
                ConsoleUi.Danger("⚠ Error: " + msg + "\n");

                // TLS ハンドシェイク失敗は、利用者が対処しやすいよう補足を出す
                if (wex != null && LlmClient.IsTlsFailure(wex))
                {
                    ConsoleUi.Dim("  The TLS handshake failed.\n");
                    ConsoleUi.Dim("  - Endpoint: " + cfg.ApiBaseUrl + "\n");
                    ConsoleUi.Dim("  - tau tried connecting with TLS 1.2 "
                        + (LlmClient.TlsApplied ? "(applied)" : "(could not be applied)") + ".\n");
                    ConsoleUi.Dim("  - If it still fails, one of the following is likely the cause:\n");
                    ConsoleUi.Dim("    1) TLS 1.2 is disabled in the OS\n");
                    ConsoleUi.Dim("       (Internet Options -> Advanced ->\n");
                    ConsoleUi.Dim("        enable \"Use TLS 1.2\")\n");
                    ConsoleUi.Dim("    2) Strong cryptography is disabled for .NET Framework\n");
                    ConsoleUi.Dim("       Set these DWORD values to 1:\n");
                    ConsoleUi.Dim("       HKLM\\SOFTWARE\\Microsoft\\.NETFramework\\v4.0.30319\n");
                    ConsoleUi.Dim("         SchUseStrongCrypto\n");
                    ConsoleUi.Dim("         SystemDefaultTlsVersions\n");
                    ConsoleUi.Dim("       (set the same values under WOW6432Node for 32-bit)\n");
                    ConsoleUi.Dim("    3) The proxy itself does not support TLS 1.2\n");
                    ConsoleUi.Dim("       (check proxyUrl / useSystemProxy in tau.json)\n");
                    ConsoleUi.Dim("  - To isolate the problem, run this in PowerShell:\n");
                    ConsoleUi.Dim("       [Net.ServicePointManager]::SecurityProtocol = 'Tls12'\n");
                    ConsoleUi.Dim("       Invoke-WebRequest " + cfg.ApiBaseUrl + " -Method Head\n");
                    ConsoleUi.Dim("  - For more detail, set TAU_DEBUG=1 and run again;\n");
                    ConsoleUi.Dim("    the actual TLS setting and the exception chain will be shown.\n");
                }

                // TAU_DEBUG=1 のときは、例外の連鎖と環境情報を必ず出す
                if (LlmClient.DebugOn)
                {
                    ConsoleUi.Dim("  --- debug ---\n");
                    ConsoleUi.Dim("  env: " + LlmClient.DescribeEnvironment() + "\n");
                    ConsoleUi.Dim("  TLS: " + LlmClient.CurrentTls + "\n");
                    ConsoleUi.Dim("  exception: " + LlmClient.ExceptionChain(ex).Replace("\n", "\n        ") + "\n");
                    ConsoleUi.Dim("  ----\n");
                }
            }
        }
    }
}
