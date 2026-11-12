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
    static class LlmClient
    {
        // 処理中のリクエスト(Ctrl+C で中断するため)
        static volatile HttpWebRequest current;

        // ---- デバッグ出力 ----
        // 環境変数 TAU_DEBUG=1 で有効になる。既定は無効(通常利用では出さない)。
        static bool debugOn = (Environment.GetEnvironmentVariable("TAU_DEBUG") ?? "") == "1";

        public static bool DebugOn { get { return debugOn; } }

        public static void EnableDebug() { debugOn = true; }

        public static void Dbg(string msg)
        {
            if (!debugOn) return;
            Console.Error.WriteLine("[debug] " + msg);
        }

        // 例外の連鎖を 1 行にまとめる(型名とメッセージ)
        public static string ExceptionChain(Exception ex)
        {
            StringBuilder sb = new StringBuilder();
            Exception e = ex;
            int depth = 0;
            while (e != null && depth < 6)
            {
                if (depth > 0) sb.Append("\n           <- ");
                sb.Append(e.GetType().FullName).Append(": ").Append(e.Message);
                e = e.InnerException;
                depth++;
            }
            return sb.ToString();
        }

        // ---- TLS ----

        static readonly object tlsLock = new object();
        static bool tlsApplied;

        // 実行環境の TLS 関連情報をまとめる(デバッグ用)
        public static string DescribeEnvironment()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                sb.Append("runtime=").Append(Environment.Version);
                sb.Append(" mono=").Append(Type.GetType("Mono.Runtime") != null ? "yes" : "no");
                sb.Append(" os=").Append(Environment.OSVersion);
            }
            catch { }
            try
            {
                sb.Append(" SecurityProtocol=").Append(ServicePointManager.SecurityProtocol)
                  .Append("(").Append((int)ServicePointManager.SecurityProtocol).Append(")");
            }
            catch (Exception ex) { sb.Append(" SecurityProtocol=<").Append(ex.Message).Append(">"); }
            try
            {
                sb.Append(" enum=[").Append(string.Join(", ",
                    Enum.GetNames(typeof(SecurityProtocolType)))).Append("]");
            }
            catch { }
            return sb.ToString();
        }

        // TLS のバージョンを設定する。
        //
        // SystemDefault(値 0) は使わない。
        //  - .NET Framework 4.7 未満では設定自体が拒否され
        //    「指定された値は 'SslProtocolType' 列挙で有効ではありません」
        //    になる。
        //  - 設定できたとしても、4.6.1 以前をターゲットにしたアプリでは
        //    実効値が TLS 1.0 のままになる。
        //  - Mono でも受け付け方は環境依存で、実際の交渉は保証されない。
        //
        // Tls13 も使わない。
        //  - Mono や古い .NET では解釈できず、同じ SslProtocolType エラーになる。
        //
        // TLS 1.2 は Windows 7 以降で必ず使えるため、これを単独で指定するのが
        // 最も安全で確実。
        public static void ApplyTls()
        {
            lock (tlsLock)
            {
                string before = SafeCurrentTls();
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    tlsApplied = true;
                }
                catch (Exception ex)
                {
                    Dbg("TLS: failed to set Tls12: " + ex.Message);
                }
                Dbg("TLS: " + before + " -> " + SafeCurrentTls());
            }
        }

        static string SafeCurrentTls()
        {
            try
            {
                SecurityProtocolType p = ServicePointManager.SecurityProtocol;
                return p.ToString() + "(" + (int)p + ")";
            }
            catch { return "unknown"; }
        }

        // 現在の TLS 設定(エラー表示用)
        public static string CurrentTls { get { return SafeCurrentTls(); } }

        // TLS 1.2 の適用に成功したか
        public static bool TlsApplied { get { lock (tlsLock) { return tlsApplied; } } }

        // TLS を再設定できるか(呼び出し側は同じ要求を 1 度だけ再試行する)。
        //
        // Tls12 固定なので「上げる段」は無い。ただし要求前に ApplyTls() が
        // 呼ばれていない可能性があるため、未適用なら 1 度だけ再試行を許す。
        public static bool EscalateTls()
        {
            lock (tlsLock)
            {
                if (tlsApplied) return false;   // すでに適用済みなら再試行しない
                ApplyTls();
                return true;
            }
        }

        // TLS ハンドシェイク失敗かどうか
        public static bool IsTlsFailure(WebException wex)
        {
            if (wex == null) return false;
            if (wex.Status == WebExceptionStatus.SecureChannelFailure) return true;
            if (wex.Status == WebExceptionStatus.TrustFailure) return false;  // 証明書の問題

            // 内部例外に TLS/SslStream 系が含まれていればハンドシェイク失敗とみなす
            Exception e = wex.InnerException;
            int depth = 0;
            while (e != null && depth < 4)
            {
                string n = e.GetType().FullName ?? "";
                if (n.IndexOf("Authentication", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("SslStream", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Tls", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                // 実測: 無効なプロトコルを要求すると、WebException の Status は
                // ReceiveFailure で、内部例外が
                //   ArgumentException: 指定された値は 'SslProtocolType' 列挙で
                //                      有効ではありません。パラメーター名:sslProtocolType
                // になる。これも TLS の設定不備なので、一段上げて再試行する。
                if (e is ArgumentException)
                {
                    string m = e.Message ?? "";
                    if (m.IndexOf("SslProtocol", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.IndexOf("sslProtocolType", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
                e = e.InnerException;
                depth++;
            }
            return false;
        }

        public static void Cancel()
        {
            HttpWebRequest r = current;
            if (r != null)
            {
                try { r.Abort(); } catch { }
            }
        }

        static void Setup(HttpWebRequest request, Config cfg, byte[] payload)
        {
            ApplyTls();
            ProxyHelper.Apply(request, cfg);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "text/event-stream";
            request.Headers["Authorization"] = "Bearer " + cfg.SecretKey;
            request.ContentLength = payload.Length;
            request.Timeout = 180000;
            request.ReadWriteTimeout = 180000;

            // SSE は応答が長く続くため、次の設定が重要になる。
            //  - Expect: 100-continue は一部のプロキシ/サーバで
            //    「受信時に予期しないエラー」の原因になるので無効化する。
            //  - HTTP/1.1 で KeepAlive を有効にし、接続を使い回す。
            //  - 途中で切れないよう、バッファリングを小さめにする必要は無いが、
            //    Nagle を無効にして小さなチャンクを即座に送る。
            request.ServicePoint.Expect100Continue = false;
            request.KeepAlive = true;
            request.ProtocolVersion = HttpVersion.Version11;
            request.ServicePoint.UseNagleAlgorithm = false;
            // 応答がチャンク形式で届く場合に備える
            request.SendChunked = false;
            request.AllowReadStreamBuffering = false;
            request.AllowWriteStreamBuffering = true;

            if (DebugOn)
            {
                Dbg("--- request settings ---");
                Dbg("  URL      : " + cfg.ApiBaseUrl);
                Dbg("  Model    : " + cfg.Model);
                Dbg("  TLS      : " + CurrentTls);
                Uri u = new Uri(cfg.ApiBaseUrl);
                string proxyDesc;
                if (request.Proxy == null) proxyDesc = "null (direct connection)";
                else if (request.Proxy.IsBypassed(u)) proxyDesc = "direct connection (proxy bypassed)";
                else proxyDesc = request.Proxy.GetProxy(u).ToString();
                Dbg("  Proxy    : " + proxyDesc);
                Dbg("  useSystemProxy=" + cfg.UseSystemProxy
                    + " proxyUrl='" + (cfg.ProxyUrl ?? "") + "'");
                Dbg("  HTTP     : " + request.ProtocolVersion);
                Dbg("  Expect100: " + request.ServicePoint.Expect100Continue);
                Dbg("  KeepAlive: " + request.KeepAlive);
                Dbg("  Timeout  : " + request.Timeout + "ms / R/W " + request.ReadWriteTimeout + "ms");
                Dbg("  Body     : " + payload.Length + " bytes");
                Dbg("----------------------");
            }
        }

        // WebException の内容を、原因が分かる形に整形する
        public static string DescribeWebException(WebException wex)
        {
            if (wex == null) return "";
            StringBuilder sb = new StringBuilder();
            sb.Append(wex.Message);
            sb.Append(" (status=").Append(wex.Status).Append(")");

            // HTTP ステータスが返っている場合は本文も読む(原因が書かれていることが多い)
            if (wex.Response != null)
            {
                try
                {
                    HttpWebResponse resp = wex.Response as HttpWebResponse;
                    if (resp != null) sb.Append(" [HTTP ").Append((int)resp.StatusCode).Append("]");
                    using (Stream rs = wex.Response.GetResponseStream())
                    {
                        string body = ToolExecutor.DecodeBytesPublic(ToolExecutor.ReadAllBytesPublic(rs));
                        if (body.Length > 0)
                        {
                            if (body.Length > 400) body = body.Substring(0, 400) + "...";
                            sb.Append(" ").Append(body);
                        }
                    }
                }
                catch { }
            }

            // 内部例外(TLS ハンドシェイク失敗など)は原因がここに出る
            Exception inner = wex.InnerException;
            int depth = 0;
            while (inner != null && depth < 3)
            {
                sb.Append(" <- ").Append(inner.Message);
                inner = inner.InnerException;
                depth++;
            }
            return sb.ToString();
        }

        // API に送るメッセージから、保存専用メタデータ(ts/steps/think)を取り除く
        static List<object> ApiMessages(List<object> messages)
        {
            List<object> apiMessages = new List<object>();
            foreach (object o in messages)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (m == null) { apiMessages.Add(o); continue; }
                Dictionary<string, object> c = new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> kv in m)
                    if (kv.Key != "ts" && kv.Key != "steps" && kv.Key != "think") c[kv.Key] = kv.Value;
                apiMessages.Add(c);
            }
            return apiMessages;
        }

        static Dictionary<string, object> BuildBody(Config cfg, List<object> messages, bool useTools, bool stream)
        {
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["model"] = cfg.Model;
            body["messages"] = ApiMessages(messages);
            body["temperature"] = 0.7;
            if (stream) body["stream"] = true;
            if (useTools && cfg.AllowTools)
            {
                body["tools"] = ToolExecutor.Definitions();
                body["tool_choice"] = "auto";
            }
            return body;
        }

        // ---- 非ストリーミング呼び出し(要約など) ----
        public static LlmResp CallOnce(Config cfg, List<object> messages, bool useTools)
        {
            const int MAX_ATTEMPTS = 3;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return CallOnceInternal(cfg, messages, useTools);
                }
                catch (WebException wex)
                {
                    Dbg("WebException(CallOnce): status=" + wex.Status
                        + " (attempt " + attempt + "/" + MAX_ATTEMPTS + ")");

                    if (!IsRetryable(wex) || attempt >= MAX_ATTEMPTS) throw;

                    if (IsTlsFailure(wex) && EscalateTls())
                        ConsoleUi.Info("Changing the TLS setting and retrying...");
                    else
                        ConsoleUi.Info("Retrying the connection (" + attempt + "/" + (MAX_ATTEMPTS - 1) + ")...");

                    System.Threading.Thread.Sleep(500 * attempt);
                }
            }
        }

        static LlmResp CallOnceInternal(Config cfg, List<object> messages, bool useTools)
        {
            Dictionary<string, object> body = BuildBody(cfg, messages, useTools, false);
            byte[] payload = Encoding.UTF8.GetBytes(Json.ToString(body));

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(cfg.ApiBaseUrl);
            Setup(request, cfg, payload);
            request.Accept = "application/json";

            current = request;
            string responseBody;
            try
            {
                using (Stream s = request.GetRequestStream())
                    s.Write(payload, 0, payload.Length);
                using (HttpWebResponse resp = (HttpWebResponse)request.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    responseBody = sr.ReadToEnd();
            }
            catch (WebException)
            {
                throw;   // TLS 失敗の再試行をラッパーが判断できるよう、そのまま投げる
            }
            finally
            {
                current = null;
            }

            return ParseOnce(responseBody);
        }

        static LlmResp ParseOnce(string responseBody)
        {
            object parsed = Json.Parse(responseBody);
            Dictionary<string, object> root = parsed as Dictionary<string, object>;
            if (root == null) throw new Exception("LLM API returned a malformed response");

            if (root.ContainsKey("error"))
            {
                Dictionary<string, object> ed = root["error"] as Dictionary<string, object>;
                if (ed != null && ed.ContainsKey("message"))
                    throw new Exception("LLM API error: " + Json.AsString(ed["message"]));
                throw new Exception("LLM API error: " + Json.ToString(root["error"]));
            }

            if (!root.ContainsKey("choices") || !(root["choices"] is List<object>) ||
                ((List<object>)root["choices"]).Count == 0)
                throw new Exception("LLM API response has no choices");

            Dictionary<string, object> first =
                ((List<object>)root["choices"])[0] as Dictionary<string, object>;
            if (first == null || !first.ContainsKey("message"))
                throw new Exception("LLM API response has no message");

            Dictionary<string, object> msg = first["message"] as Dictionary<string, object>;
            if (msg == null) throw new Exception("LLM API response has no message");

            LlmResp r = new LlmResp();
            r.Content = msg.ContainsKey("content") ? Json.AsString(msg["content"]) : "";
            if (msg.ContainsKey("reasoning_content")) r.Reasoning = Json.AsString(msg["reasoning_content"]);
            else if (msg.ContainsKey("reasoning")) r.Reasoning = Json.AsString(msg["reasoning"]);

            if (root.ContainsKey("usage") && root["usage"] is Dictionary<string, object>)
            {
                Dictionary<string, object> u = (Dictionary<string, object>)root["usage"];
                if (u.ContainsKey("total_tokens")) r.TotalTokens = Json.AsInt(u["total_tokens"]);
            }
            return r;
        }

        // ---- ストリーミング呼び出し ----
        class StreamAcc
        {
            public string Id = "";
            public string Name = "";
            public StringBuilder Args = new StringBuilder();
        }

        // TLS ハンドシェイクに失敗したら、設定を一段上げて 1 度だけ再試行する。
        // (SystemDefault が効かない環境では TLS 1.2 を明示すると通ることが多い)
        public static LlmResp CallStream(Config cfg, List<object> messages, bool useTools,
                                         Action<string> contentCb, Action<string> reasoningCb)
        {
            // TLS / 接続の一時的な失敗は再試行する。
            // SecureChannelFailure は一時的なことも多いため、
            // TLS 設定を変えられなくても数回は試す。
            const int MAX_ATTEMPTS = 3;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return CallStreamOnce(cfg, messages, useTools, contentCb, reasoningCb);
                }
                catch (WebException wex)
                {
                    Dbg("WebException: status=" + wex.Status + " (attempt " + attempt + "/" + MAX_ATTEMPTS + ")");
                    Dbg("  chain: " + ExceptionChain(wex));

                    bool retryable = IsRetryable(wex);
                    Dbg("  IsTlsFailure=" + IsTlsFailure(wex) + " retryable=" + retryable);

                    if (!retryable || attempt >= MAX_ATTEMPTS) throw;

                    // TLS 設定をまだ変えられるなら変える
                    if (IsTlsFailure(wex) && EscalateTls())
                        ConsoleUi.Info("Changing the TLS setting and retrying...");
                    else
                        ConsoleUi.Info("Retrying the connection (" + attempt + "/" + (MAX_ATTEMPTS - 1) + ")...");

                    // 少し待ってから再試行する(指数バックオフ)
                    int wait = 500 * attempt;
                    System.Threading.Thread.Sleep(wait);
                }
            }
        }

        // 再試行する価値のある失敗か。
        //  - TLS/接続系の一時的な失敗 → 再試行する
        //  - HTTP エラー(401/429/500 など) → 再試行しても無駄なのでしない
        public static bool IsRetryable(WebException wex)
        {
            if (wex == null) return false;
            switch (wex.Status)
            {
                case WebExceptionStatus.SecureChannelFailure:
                case WebExceptionStatus.ConnectionClosed:
                case WebExceptionStatus.KeepAliveFailure:
                case WebExceptionStatus.ReceiveFailure:
                case WebExceptionStatus.SendFailure:
                case WebExceptionStatus.Timeout:
                case WebExceptionStatus.ConnectFailure:   // 接続拒否は再試行しても無駄なことが多いが1回は試す
                    return true;
                case WebExceptionStatus.TrustFailure:
                case WebExceptionStatus.ProtocolError:    // HTTP エラー
                    return false;
                default:
                    return false;
            }
        }

        static LlmResp CallStreamOnce(Config cfg, List<object> messages, bool useTools,
                                      Action<string> contentCb, Action<string> reasoningCb)
        {
            Dictionary<string, object> body = BuildBody(cfg, messages, useTools, true);
            byte[] payload = Encoding.UTF8.GetBytes(Json.ToString(body));

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(cfg.ApiBaseUrl);
            Setup(request, cfg, payload);

            StringBuilder content = new StringBuilder();
            Dictionary<int, StreamAcc> acc = new Dictionary<int, StreamAcc>();

            current = request;
            try
            {
                Dbg("connecting: " + cfg.ApiBaseUrl);
                using (Stream s = request.GetRequestStream())
                    s.Write(payload, 0, payload.Length);
                Dbg("sent (" + payload.Length + " bytes), waiting for response...");

                using (HttpWebResponse resp = (HttpWebResponse)request.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    if (DebugOn)
                    {
                        Dbg("response: HTTP " + (int)resp.StatusCode + " " + resp.StatusDescription);
                        Dbg("  ContentType  : " + resp.ContentType);
                        Dbg("  ContentLength: " + resp.ContentLength);
                        Dbg("  TransferEnc  : " + resp.Headers["Transfer-Encoding"]);
                        Dbg("  Protocol     : " + resp.ProtocolVersion);
                        Dbg("  Server       : " + resp.Server);
                    }

                    string ctype = (resp.ContentType == null) ? "" : resp.ContentType;
                    if (ctype.IndexOf("event-stream", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        // プロキシ等が stream:true を無視して通常の JSON を返した場合
                        string bodyText = sr.ReadToEnd();
                        ParseNonStreamJson(bodyText, content, acc, cfg, contentCb, reasoningCb);
                    }
                    else
                    {
                        bool sawDone = false;   // [DONE] を受け取ったか(正常終了の目印)
                        int lineNo = 0;
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            lineNo++;
                            if (DebugOn && lineNo <= 5)
                                Dbg("SSE[" + lineNo + "]: "
                                    + (line.Length > 160 ? line.Substring(0, 160) + "..." : line));
                            if (line.Length == 0) continue;
                            if (!line.StartsWith("data:")) continue;
                            string data = line.Substring(5).Trim();
                            if (data == "[DONE]") { sawDone = true; break; }

                            object parsed;
                            try { parsed = Json.Parse(data); }
                            catch { continue; }

                            Dictionary<string, object> root = parsed as Dictionary<string, object>;
                            if (root == null || !root.ContainsKey("choices") ||
                                !(root["choices"] is List<object>)) continue;
                            List<object> choices = (List<object>)root["choices"];
                            if (choices.Count == 0) continue;
                            Dictionary<string, object> ch = choices[0] as Dictionary<string, object>;
                            if (ch == null || !ch.ContainsKey("delta")) continue;
                            Dictionary<string, object> delta = ch["delta"] as Dictionary<string, object>;
                            if (delta == null) continue;

                            if (delta.ContainsKey("content") && delta["content"] != null)
                            {
                                string t = Json.AsString(delta["content"]);
                                if (t.Length > 0)
                                {
                                    content.Append(t);
                                    if (contentCb != null) contentCb(t);
                                }
                            }
                            if (cfg.EmitReasoning)
                            {
                                string rt = DeltaText(delta, "reasoning_content");
                                if (rt.Length == 0) rt = DeltaText(delta, "reasoning");
                                if (rt.Length > 0 && reasoningCb != null) reasoningCb(rt);
                            }
                            if (delta.ContainsKey("function_call") && delta["function_call"] is Dictionary<string, object>)
                            {
                                Dictionary<string, object> fc = (Dictionary<string, object>)delta["function_call"];
                                StreamAcc cur;
                                if (!acc.ContainsKey(0)) { cur = new StreamAcc(); acc[0] = cur; }
                                else cur = acc[0];
                                if (fc.ContainsKey("name") && fc["name"] != null) cur.Name += Json.AsString(fc["name"]);
                                if (fc.ContainsKey("arguments") && fc["arguments"] != null) cur.Args.Append(Json.AsString(fc["arguments"]));
                            }
                            if (delta.ContainsKey("tool_calls") && delta["tool_calls"] is List<object>)
                            {
                                foreach (object tco in (List<object>)delta["tool_calls"])
                                {
                                    Dictionary<string, object> tc = tco as Dictionary<string, object>;
                                    if (tc == null) continue;
                                    int idx = tc.ContainsKey("index") ? Json.AsInt(tc["index"]) : acc.Count;
                                    StreamAcc cur;
                                    if (!acc.ContainsKey(idx)) { cur = new StreamAcc(); acc[idx] = cur; }
                                    else cur = acc[idx];
                                    if (tc.ContainsKey("id") && tc["id"] != null) cur.Id = Json.AsString(tc["id"]);
                                    Dictionary<string, object> fn =
                                        (tc.ContainsKey("function")) ? tc["function"] as Dictionary<string, object> : null;
                                    if (fn != null)
                                    {
                                        if (fn.ContainsKey("name") && fn["name"] != null) cur.Name += Json.AsString(fn["name"]);
                                        if (fn.ContainsKey("arguments") && fn["arguments"] != null) cur.Args.Append(Json.AsString(fn["arguments"]));
                                    }
                                }
                            }
                        }

                        Dbg("SSE read finished: lines=" + lineNo + " sawDone=" + sawDone
                            + " contentLen=" + content.Length);

                        // [DONE] を受け取らずにストリームが終わった場合は、
                        // 応答が途中で切れている(通信断・サーバ側のタイムアウト等)。
                        // 黙って途中までの本文を確定させない。
                        if (!sawDone && content.Length > 0)
                        {
                            throw new Exception("LLM API (stream) error: the response was cut off"
                                + " (" + content.Length + " characters received)."
                                + " Please try again.");
                        }
                    }
                }
            }
            catch (WebException wex)
            {
                if (wex.Status == WebExceptionStatus.RequestCanceled) throw new LlmCancelledException();
                throw;   // TLS 失敗の再試行をラッパーが判断できるよう、そのまま投げる
            }
            catch (IOException iex)
            {
                // ストリーム読み取り中の切断(「受信時に予期しないエラー」など)
                throw new Exception("LLM API (stream) error: the connection was lost: " + iex.Message
                    + " (" + content.Length + " characters received)"
                    + ". A network or proxy timeout, or the server aborting the response, is likely.");
            }
            finally
            {
                current = null;
            }

            List<object> toolCalls = new List<object>();
            List<int> idxs = new List<int>(acc.Keys);
            idxs.Sort();
            foreach (int ix in idxs)
            {
                StreamAcc c = acc[ix];
                if (c.Name.Length == 0) continue;
                Dictionary<string, object> fn = new Dictionary<string, object>();
                fn["name"] = c.Name;
                fn["arguments"] = c.Args.ToString();
                Dictionary<string, object> call = new Dictionary<string, object>();
                call["id"] = c.Id.Length > 0 ? c.Id : "call_" + ix;
                call["type"] = "function";
                call["function"] = fn;
                toolCalls.Add(call);
            }

            LlmResp r = new LlmResp();
            r.Content = content.ToString();
            r.ToolCalls = (toolCalls.Count > 0) ? toolCalls : null;
            return r;
        }

        static string DeltaText(Dictionary<string, object> delta, string key)
        {
            if (delta == null || !delta.ContainsKey(key)) return "";
            object v = delta[key];
            if (v is string) return (string)v;
            return "";
        }

        // 非SSE(通常の JSON)応答を解釈する。stream:true が無視された場合のフォールバック。
        static void ParseNonStreamJson(string body, StringBuilder contentOut,
                                       Dictionary<int, StreamAcc> acc, Config cfg,
                                       Action<string> contentCb, Action<string> reasoningCb)
        {
            if (body == null || body.Length == 0) return;
            object o;
            try { o = Json.Parse(body); }
            catch { return; }

            Dictionary<string, object> root = o as Dictionary<string, object>;
            if (root == null) return;
            if (root.ContainsKey("error"))
            {
                Dictionary<string, object> ed = root["error"] as Dictionary<string, object>;
                throw new Exception("LLM API error: "
                    + (ed != null && ed.ContainsKey("message") ? Json.AsString(ed["message"]) : Json.ToString(root["error"])));
            }
            if (!root.ContainsKey("choices") || !(root["choices"] is List<object>)) return;
            List<object> choices = (List<object>)root["choices"];
            if (choices.Count == 0) return;
            Dictionary<string, object> first = choices[0] as Dictionary<string, object>;
            if (first == null || !first.ContainsKey("message")) return;
            Dictionary<string, object> msg = first["message"] as Dictionary<string, object>;
            if (msg == null) return;

            string content = msg.ContainsKey("content") ? Json.AsString(msg["content"]) : "";
            if (content.Length > 0)
            {
                contentOut.Append(content);
                if (contentCb != null) contentCb(content);
            }
            if (cfg.EmitReasoning)
            {
                string rc = "";
                if (msg.ContainsKey("reasoning_content")) rc = Json.AsString(msg["reasoning_content"]);
                else if (msg.ContainsKey("reasoning")) rc = Json.AsString(msg["reasoning"]);
                if (rc.Length > 0 && reasoningCb != null) reasoningCb(rc);
            }

            if (msg.ContainsKey("tool_calls") && msg["tool_calls"] is List<object>)
            {
                List<object> tcs = (List<object>)msg["tool_calls"];
                for (int i = 0; i < tcs.Count; i++)
                {
                    Dictionary<string, object> tc = tcs[i] as Dictionary<string, object>;
                    if (tc == null) continue;
                    StreamAcc cur = new StreamAcc();
                    if (tc.ContainsKey("id") && tc["id"] != null) cur.Id = Json.AsString(tc["id"]);
                    Dictionary<string, object> fn =
                        (tc.ContainsKey("function")) ? tc["function"] as Dictionary<string, object> : null;
                    if (fn != null)
                    {
                        if (fn.ContainsKey("name") && fn["name"] != null) cur.Name = Json.AsString(fn["name"]);
                        if (fn.ContainsKey("arguments"))
                        {
                            object av = fn["arguments"];
                            cur.Args.Append(av is string ? (string)av : Json.ToString(av));
                        }
                    }
                    acc[i] = cur;
                }
            }
            else if (msg.ContainsKey("function_call") && msg["function_call"] is Dictionary<string, object>)
            {
                Dictionary<string, object> fc = (Dictionary<string, object>)msg["function_call"];
                StreamAcc cur = new StreamAcc();
                cur.Name = fc.ContainsKey("name") ? Json.AsString(fc["name"]) : "";
                if (fc.ContainsKey("arguments"))
                {
                    object av = fc["arguments"];
                    cur.Args.Append(av is string ? (string)av : Json.ToString(av));
                }
                acc[0] = cur;
            }
        }
    }
}
