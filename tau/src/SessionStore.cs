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
    //  セッションの永続化
    //
    //  セッションは agentweb とは別に、tau 独自の場所へ保存する。
    //    %APPDATA%\tau\sessions\<id>.json
    //    %APPDATA%\tau\sessions\_state.json  (最後に開いたセッション)
    //
    //  ファイル形式は agentweb と同じ(互換)だが、保存先は共有しない。
    //  設定も tau 独自の tau.json を使う。
    // =======================================================================
    class SessionStore
    {
        string sessionsDir;

        public SessionStore()
        {
            // セッションは agentweb と共有せず、tau 独自の場所に保存する。
            //   %APPDATA%\tau\sessions\<id>.json
            sessionsDir = Path.Combine(Config.TauDataDir(), "sessions");
            try { Directory.CreateDirectory(sessionsDir); }
            catch { }
        }

        public string DataDir
        {
            get { return sessionsDir; }
        }

        static string Sanitize(string id)
        {
            if (id == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char c in id)
                if (Char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            string clean = sb.ToString();
            return (clean.Length == 0) ? "default" : clean;
        }

        string FileOf(string id)
        {
            return Path.Combine(sessionsDir, Sanitize(id) + ".json");
        }

        public string NewId()
        {
            return "s" + DateTime.Now.ToString("yyyyMMddHHmmss")
                   + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        public Dictionary<string, object> Load(string id)
        {
            string f = FileOf(id);
            if (!File.Exists(f)) return null;
            try
            {
                object o = Json.Parse(File.ReadAllText(f, Encoding.UTF8));
                return o as Dictionary<string, object>;
            }
            catch { return null; }
        }

        public List<object> MessagesOf(string id)
        {
            Dictionary<string, object> meta = Load(id);
            if (meta != null && meta.ContainsKey("messages") && meta["messages"] is List<object>)
                return (List<object>)meta["messages"];
            return new List<object>();
        }

        public void Save(string id, List<object> messages, string title)
        {
            Save(id, messages, title, "", 0);
        }

        public void Save(string id, List<object> messages, string title, string summary, int contextStart)
        {
            Dictionary<string, object> meta = Load(id);
            string createdAt = (meta != null && meta.ContainsKey("createdAt"))
                ? Json.AsString(meta["createdAt"]) : "";
            if (createdAt.Length == 0) createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

            Dictionary<string, object> s = new Dictionary<string, object>();
            s["id"] = Sanitize(id);
            s["title"] = (title == null || title.Trim().Length == 0) ? "New conversation" : title;
            s["createdAt"] = createdAt;
            s["updatedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            s["summary"] = (summary == null || summary.Trim().Length == 0) ? "" : summary;
            s["contextStart"] = (contextStart < 0) ? 0 : contextStart;
            s["messages"] = (messages == null) ? new List<object>() : messages;
            try { File.WriteAllText(FileOf(id), Json.ToString(s), Encoding.UTF8); }
            catch (Exception ex) { Console.WriteLine("[session] failed to save: " + ex.Message); }
        }

        public List<object> List()
        {
            List<object> list = new List<object>();
            try
            {
                string[] files = Directory.GetFiles(sessionsDir, "*.json");
                Array.Sort(files);
                Array.Reverse(files);   // 新しいものが先頭に来やすいよう逆順
                foreach (string f in files)
                {
                    // _state.json (最後に開いたセッション / 下書き) は一覧に含めない
                    if (Path.GetFileName(f).StartsWith("_")) continue;
                    try
                    {
                        object o = Json.Parse(File.ReadAllText(f, Encoding.UTF8));
                        Dictionary<string, object> s = o as Dictionary<string, object>;
                        if (s == null) continue;
                        int count = 0;
                        if (s.ContainsKey("messages") && s["messages"] is List<object>)
                            count = ((List<object>)s["messages"]).Count;
                        s["count"] = count;
                        list.Add(s);
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        // セッションを削除する。成功なら true。
        public bool Delete(string id)
        {
            if (id == null || id.Trim().Length == 0) return false;
            string f = FileOf(id);
            if (!File.Exists(f)) return false;
            try
            {
                File.Delete(f);

                // 「最後に開いていたセッション」が削除した ID を指していたら消す
                Dictionary<string, object> st = LoadState();
                if (st.ContainsKey("lastSession")
                    && Sanitize(Json.AsString(st["lastSession"])) == Sanitize(id))
                {
                    st.Remove("lastSession");
                    SaveState(st);
                }
                return true;
            }
            catch (Exception ex)
            {
                ConsoleUi.Danger("  Failed to delete: " + ex.Message + "\n");
                return false;
            }
        }

        // ---------------- 最後に開いていたセッション (tau 独自) ----------------

        string StateFile()
        {
            return Path.Combine(sessionsDir, "_state.json");
        }

        Dictionary<string, object> LoadState()
        {
            try
            {
                string f = StateFile();
                if (!File.Exists(f)) return new Dictionary<string, object>();
                object o = Json.Parse(File.ReadAllText(f, Encoding.UTF8));
                Dictionary<string, object> st = o as Dictionary<string, object>;
                return st ?? new Dictionary<string, object>();
            }
            catch { return new Dictionary<string, object>(); }
        }

        void SaveState(Dictionary<string, object> st)
        {
            try { File.WriteAllText(StateFile(), Json.ToString(st), Encoding.UTF8); }
            catch { }
        }

        public string LastSessionId()
        {
            Dictionary<string, object> st = LoadState();
            if (!st.ContainsKey("lastSession")) return "";
            string id = Json.AsString(st["lastSession"]).Trim();
            if (id.Length == 0) return "";
            if (!File.Exists(FileOf(id))) return "";   // 削除済みなら無視
            return id;
        }

        public void SetLastSessionId(string id)
        {
            if (id == null || id.Trim().Length == 0) return;
            Dictionary<string, object> st = LoadState();
            st["lastSession"] = Sanitize(id);
            SaveState(st);
        }
    }
}
