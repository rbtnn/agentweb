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
    //  ツール実行 (Function Calling)
    // =======================================================================
    static class ToolExecutor
    {
        const int MAX_OUTPUT = 32000;   // ツール結果として LLM に返す最大文字数

        static object Get(Dictionary<string, object> d, string key)
        {
            if (d != null && d.ContainsKey(key)) return d[key];
            return null;
        }

        static string NormalizeName(string name)
        {
            if (name == null) return "";
            switch (name.Trim().ToLowerInvariant())
            {
                case "shell": case "cmd": case "execute": case "run": case "exec":
                case "run_command": case "execute_command": case "command":
                    return "shell";
                case "read_file": case "read": case "cat": case "readfile":
                    return "read_file";
                case "write_file": case "write": case "writefile":
                    return "write_file";
                case "list_dir": case "ls": case "dir": case "list_directory": case "listdir":
                    return "list_dir";
                case "curl": case "http_request": case "http": case "fetch":
                    return "curl";
                default:
                    return name;
            }
        }

        // プロキシが function.name を返さない場合に、引数の形からツール名を推定する
        public static string InferName(Dictionary<string, object> fn, string argsJson)
        {
            if (fn != null && fn.ContainsKey("name"))
            {
                string nm = Json.AsString(fn["name"]);
                if (nm != null && nm.Trim().Length > 0) return NormalizeName(nm);
            }
            Dictionary<string, object> args = null;
            try { args = Json.Parse(argsJson) as Dictionary<string, object>; }
            catch { }
            if (args == null) args = new Dictionary<string, object>();

            if (args.ContainsKey("cmd")) return "shell";
            if (args.ContainsKey("content") && args.ContainsKey("path")) return "write_file";
            if (args.ContainsKey("pattern")) return "list_dir";
            if (args.ContainsKey("path")) return "read_file";
            return "shell";
        }

        public static string Execute(string name, string argsJson, Config cfg)
        {
            Dictionary<string, object> args = null;
            try { args = Json.Parse(argsJson) as Dictionary<string, object>; }
            catch { }
            if (args == null) args = new Dictionary<string, object>();

            if (name == null || name.Trim().Length == 0) name = InferName(null, argsJson);
            name = NormalizeName(name);

            switch (name)
            {
                case "shell":
                    return Shell(Json.AsString(Get(args, "cmd")), cfg.ToolTimeoutSec);
                case "read_file":
                    return ReadFile(Json.AsString(Get(args, "path")));
                case "write_file":
                    return WriteFile(Json.AsString(Get(args, "path")), Json.AsString(Get(args, "content")));
                case "list_dir":
                    return ListDir(Json.AsString(Get(args, "path")), Json.AsString(Get(args, "pattern")));
                case "curl":
                    return Curl(cfg, Json.AsString(Get(args, "url")),
                        Json.AsString(Get(args, "method")),
                        args.ContainsKey("headers") ? args["headers"] : null,
                        Json.AsString(Get(args, "body")));
                default:
                    return Err("unknown tool: " + name
                        + " (available: shell/read_file/write_file/list_dir/curl)");
            }
        }

        // ---------------- ツール定義 ----------------

        static Dictionary<string, object> Func(string name, string desc,
                                               string[] required, string[][] props)
        {
            Dictionary<string, object> f = new Dictionary<string, object>();
            f["name"] = name;
            f["description"] = desc;

            Dictionary<string, object> params_ = new Dictionary<string, object>();
            params_["type"] = "object";
            Dictionary<string, object> p = new Dictionary<string, object>();
            for (int i = 0; i < props.Length; i++)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["type"] = props[i][1];
                d["description"] = props[i][2];
                p[props[i][0]] = d;
            }
            params_["properties"] = p;
            params_["required"] = new List<object>(required);
            f["parameters"] = params_;

            Dictionary<string, object> tool = new Dictionary<string, object>();
            tool["type"] = "function";
            tool["function"] = f;
            return tool;
        }

        public static List<object> Definitions()
        {
            List<object> tools = new List<object>();

            tools.Add(Func("shell",
                "Run a command on the local machine. "
                + "Returns stdout, stderr and the exit code. "
                + "Use it for any shell command, including file and directory operations "
                + "and running programs.",
                new string[] { "cmd" },
                new string[][] {
                    new string[] { "cmd", "string", "Command line to run (e.g. dir /b)" }
                }));

            tools.Add(Func("read_file",
                "Read the contents of a file as text.",
                new string[] { "path" },
                new string[][] {
                    new string[] { "path", "string", "Path of the file to read" }
                }));

            tools.Add(Func("write_file",
                "Write text to the given path, overwriting any existing file.",
                new string[] { "path", "content" },
                new string[][] {
                    new string[] { "path", "string", "Path of the file to write" },
                    new string[] { "content", "string", "Text to write" }
                }));

            tools.Add(Func("list_dir",
                "List the files and subdirectories in a directory.",
                new string[] { "path" },
                new string[][] {
                    new string[] { "path", "string", "Directory to list" },
                    new string[] { "pattern", "string",
                        "Glob pattern to filter files (e.g. *.txt). Defaults to *" }
                }));

            tools.Add(Func("curl",
                "Send an HTTP/HTTPS request and return the response. "
                + "Uses the configured proxy when one is set.",
                new string[] { "url" },
                new string[][] {
                    new string[] { "url", "string", "URL to request" },
                    new string[] { "method", "string",
                        "HTTP method (e.g. GET, POST). Defaults to POST when a body is given, otherwise GET" },
                    new string[] { "headers", "object", "Additional HTTP headers" },
                    new string[] { "body", "string", "Request body" }
                }));

            return tools;
        }

        // ---------------- 各ツールの実装 ----------------

        static string Shell(string cmd, int timeoutSec)
        {
            if (cmd == null || cmd.Trim().Length == 0) return Err("cmd is empty");
            try
            {
                bool win = Path.DirectorySeparatorChar == '\\';
                ProcessStartInfo psi = win
                    ? new ProcessStartInfo("cmd.exe", "/c " + cmd)
                    : new ProcessStartInfo("/bin/sh", "-c " + cmd);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                Process p = Process.Start(psi);
                System.Threading.Tasks.Task<byte[]> so = CopyToBytesAsync(p.StandardOutput.BaseStream);
                System.Threading.Tasks.Task<byte[]> se = CopyToBytesAsync(p.StandardError.BaseStream);

                if (!p.WaitForExit(timeoutSec * 1000))
                {
                    try { p.Kill(); } catch { }
                    return Err("timed out after " + timeoutSec + "s: " + cmd);
                }
                p.WaitForExit();

                Dictionary<string, object> d = new Dictionary<string, object>();
                d["ok"] = true;
                d["exitCode"] = p.ExitCode;
                d["stdout"] = Clip(DecodeBytes(so.Result));
                d["stderr"] = Clip(DecodeBytes(se.Result));
                return Json.ToString(d);
            }
            catch (Exception ex)
            {
                return Err("shell failed: " + ex.Message);
            }
        }

        static string ReadFile(string path)
        {
            if (path == null || path.Length == 0) return Err("path is not specified");
            try
            {
                if (!File.Exists(path)) return Err("file does not exist: " + path);
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["ok"] = true;
                d["content"] = Clip(DecodeBytes(File.ReadAllBytes(path)));
                return Json.ToString(d);
            }
            catch (Exception ex)
            {
                return Err("read_file failed: " + ex.Message);
            }
        }

        static string WriteFile(string path, string content)
        {
            if (path == null || path.Length == 0) return Err("path is not specified");
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (dir != null && dir.Length > 0 && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, content == null ? "" : content, Encoding.UTF8);
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["ok"] = true;
                d["written"] = (content == null ? "" : content).Length;
                d["path"] = Path.GetFullPath(path);
                return Json.ToString(d);
            }
            catch (Exception ex)
            {
                return Err("write_file failed: " + ex.Message);
            }
        }

        static string ListDir(string path, string pattern)
        {
            if (path == null || path.Length == 0) path = ".";
            if (pattern == null || pattern.Length == 0) pattern = "*";
            try
            {
                StringBuilder list = new StringBuilder();
                List<object> entries = new List<object>();

                string[] dirs = Directory.GetDirectories(path);
                foreach (string den in dirs)
                {
                    string name = Path.GetFileName(den);
                    list.Append("[DIR ]  ").Append(name).Append("/\n");
                    Dictionary<string, object> e = new Dictionary<string, object>();
                    e["type"] = "dir"; e["name"] = name; e["path"] = den;
                    entries.Add(e);
                }

                string[] files = Directory.GetFiles(path, pattern);
                foreach (string f in files)
                {
                    string name = Path.GetFileName(f);
                    long size = new FileInfo(f).Length;
                    list.Append("[FILE]  ").Append(name).Append("  ").Append(HumanSize(size)).Append("\n");
                    Dictionary<string, object> e = new Dictionary<string, object>();
                    e["type"] = "file"; e["name"] = name; e["size"] = size; e["path"] = f;
                    entries.Add(e);
                }

                Dictionary<string, object> d = new Dictionary<string, object>();
                d["ok"] = true;
                d["path"] = Path.GetFullPath(path);
                d["total"] = dirs.Length + files.Length;
                d["list"] = list.ToString();
                d["entries"] = entries;
                return Json.ToString(d);
            }
            catch (Exception ex)
            {
                return Err("list_dir failed: " + ex.Message);
            }
        }

        static string HumanSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.##") + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.##") + " GB";
        }

        static string Curl(Config cfg, string url, string method, object headersObj, string body)
        {
            if (url == null || url.Trim().Length == 0) return Err("url is not specified");
            if (method == null || method.Trim().Length == 0)
                method = (body != null && body.Length > 0) ? "POST" : "GET";
            method = method.Trim().ToUpperInvariant();

            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url.Trim());
                LlmClient.ApplyTls();   // HTTPS 先でも TLS 1.2 以上を使えるようにする
                ProxyHelper.Apply(request, cfg);
                request.Method = method;
                request.Timeout = cfg.ToolTimeoutSec * 1000;
                request.ReadWriteTimeout = cfg.ToolTimeoutSec * 1000;

                Dictionary<string, object> headers = headersObj as Dictionary<string, object>;
                if (headers != null)
                {
                    foreach (KeyValuePair<string, object> kv in headers)
                    {
                        string v = Json.AsString(kv.Value);
                        if (String.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) request.ContentType = v;
                        else if (String.Equals(kv.Key, "Accept", StringComparison.OrdinalIgnoreCase)) request.Accept = v;
                        else if (String.Equals(kv.Key, "User-Agent", StringComparison.OrdinalIgnoreCase)) request.UserAgent = v;
                        else request.Headers[kv.Key] = v;
                    }
                }

                if (body != null && body.Length > 0)
                {
                    if (request.ContentType == null) request.ContentType = "application/json";
                    byte[] data = Encoding.UTF8.GetBytes(body);
                    request.ContentLength = data.Length;
                    using (Stream s = request.GetRequestStream())
                        s.Write(data, 0, data.Length);
                }

                HttpStatusCode status;
                string respBody;
                try
                {
                    using (HttpWebResponse resp = (HttpWebResponse)request.GetResponse())
                    {
                        status = resp.StatusCode;
                        using (Stream rs = resp.GetResponseStream())
                            respBody = DecodeBytes(ReadAllBytes(rs));
                    }
                }
                catch (WebException wex)
                {
                    if (wex.Response == null) return Err("curl request error: " + wex.Message);
                    using (Stream rs = wex.Response.GetResponseStream())
                        respBody = DecodeBytes(ReadAllBytes(rs));
                    status = ((HttpWebResponse)wex.Response).StatusCode;
                }

                Dictionary<string, object> d = new Dictionary<string, object>();
                d["ok"] = true;
                d["status"] = (int)status;
                d["url"] = url.Trim();
                d["body"] = Clip(respBody);
                return Json.ToString(d);
            }
            catch (Exception ex)
            {
                return Err("curl failed: " + ex.Message);
            }
        }

        // ---------------- 文字コード判定 ----------------

        // エラー応答の本文読み取り用に外部から使えるようにしたもの
        public static string DecodeBytesPublic(byte[] data) { return DecodeBytes(data); }
        public static byte[] ReadAllBytesPublic(Stream s) { return ReadAllBytes(s); }

        static string DecodeBytes(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            try
            {
                if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                    return new UTF8Encoding(false).GetString(data, 3, data.Length - 3);
                if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
                    return Encoding.Unicode.GetString(data, 2, data.Length - 2);
                if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
                    return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
            }
            catch { }
            try
            {
                var u8 = new UTF8Encoding(false, true);
                return u8.GetString(data);
            }
            catch (DecoderFallbackException) { }
            try
            {
                int cp = Console.OutputEncoding.CodePage;
                if (cp > 0 && cp != 65001) return Encoding.GetEncoding(cp).GetString(data);
            }
            catch { }
            try { return Encoding.Default.GetString(data); } catch { }
            try { return Encoding.GetEncoding(28591).GetString(data); } catch { }
            return Encoding.ASCII.GetString(data);
        }

        static System.Threading.Tasks.Task<byte[]> CopyToBytesAsync(Stream s)
        {
            return System.Threading.Tasks.Task.Run(delegate()
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            });
        }

        static byte[] ReadAllBytes(Stream s)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        static string Clip(string s)
        {
            if (s == null) return "";
            if (s.Length > MAX_OUTPUT) s = s.Substring(0, MAX_OUTPUT) + "\n...[output truncated]";
            return s;
        }

        static string Err(string message)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["error"] = message;
            return Json.ToString(d);
        }
    }
}
