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
    //  接続診断 (--diag)
    //
    //  実際に接続を試し、どこで失敗しているかを切り分ける。
    //   - 環境情報(.NET / Mono / OS / 既定の TLS)
    //   - TLS の各バージョンでの接続可否
    //   - プロキシ設定
    //   - HTTP 応答の内容
    // =======================================================================
    static class Diagnostics
    {
        public static void Run(Config cfg)
        {
            Console.WriteLine();
            ConsoleUi.Bold("tau --diag  connection diagnostic");
            Console.WriteLine();

            Console.WriteLine("  [environment]");
            Console.WriteLine("    " + LlmClient.DescribeEnvironment());
            Console.WriteLine("    input encoding : " + Program.SafeInputEncoding());
            try
            {
                Console.WriteLine("    output encoding: " + Console.OutputEncoding.WebName
                    + " (CP" + Console.OutputEncoding.CodePage + ")");
            }
            catch { }
            Console.WriteLine();

            Console.WriteLine("  [settings]");
            Console.WriteLine("    URL     : " + cfg.ApiBaseUrl);
            Console.WriteLine("    Model   : " + cfg.Model);
            Console.WriteLine("    Proxy   : " + (cfg.ProxyUrl != null && cfg.ProxyUrl.Trim().Length > 0
                                ? cfg.ProxyUrl : "(none)"));
            Console.WriteLine("    SysProxy: " + (cfg.UseSystemProxy ? "enabled" : "disabled (direct)"));
            Console.WriteLine("    API Key : " + (cfg.SecretKey.Length > 0 ? "set" : "not set"));
            Console.WriteLine("    Config  : " + cfg.ConfigPath);
            Console.WriteLine();

            Uri uri = null;
            try { uri = new Uri(cfg.ApiBaseUrl); }
            catch (Exception ex)
            {
                ConsoleUi.Danger("  Invalid URL: " + ex.Message + "\n");
                return;
            }

            Console.WriteLine("  [endpoint]");
            Console.WriteLine("    Host : " + uri.Host);
            Console.WriteLine("    Port : " + uri.Port);
            Console.WriteLine("    Path : " + uri.AbsolutePath);
            try
            {
                System.Net.IPAddress[] ips = System.Net.Dns.GetHostAddresses(uri.Host);
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < ips.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(ips[i]);
                }
                Console.WriteLine("    DNS  : " + sb);
            }
            catch (Exception ex)
            {
                ConsoleUi.Danger("    DNS  : cannot resolve: " + ex.Message + "\n");
            }
            Console.WriteLine();

            // --- TLS の各バージョンを試す ---
            Console.WriteLine("  [TLS handshake test]");
            Console.WriteLine("    (TCP connect + TLS handshake only; no HTTP is sent)");
            Console.WriteLine();

            // TLS の可否は TCP 接続 + SslStream のハンドシェイクだけで判定する。
            // HTTP のメソッド(HEAD 等)を使うと、サーバが HEAD 非対応の場合に
            // 501 が返り、TLS が使えるのに NG と誤表示してしまうため。
            string[] names = { "SystemDefault", "Tls12", "Tls13", "Tls11", "Tls" };
            bool anyOk = false;
            for (int i = 0; i < names.Length; i++)
            {
                string n = names[i];
                object v = null;
                try { v = Enum.Parse(typeof(SecurityProtocolType), n); }
                catch { }
                if (v == null)
                {
                    Console.WriteLine("    " + Pad(n) + ": (not available in this .NET)");
                    continue;
                }
                string result = ProbeTls(uri, cfg, (SecurityProtocolType)v);
                if (result.StartsWith("OK")) anyOk = true;
                Console.WriteLine("    " + Pad(n) + ": " + result);
            }
            Console.WriteLine();
            if (anyOk)
                ConsoleUi.Dim("    -> TLS handshakes succeed.\n");
            else
                ConsoleUi.Danger("    -> every TLS handshake failed.\n");
            Console.WriteLine();

            // --- 実際の API 呼び出し ---
            Console.WriteLine("  [API call test]");
            LlmClient.ApplyTls();
            Console.WriteLine("    TLS used : " + LlmClient.CurrentTls);

            List<object> msgs = new List<object>();
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["role"] = "user";
            m["content"] = "ping";
            msgs.Add(m);

            try
            {
                LlmResp resp = LlmClient.CallOnce(cfg, msgs, false);
                Console.WriteLine("    Result   : OK");
                Console.WriteLine("    Reply    : " + FirstLine(resp.Content));
            }
            catch (WebException wex)
            {
                ConsoleUi.Danger("    Result   : NG " + wex.Status + "\n");
                Console.WriteLine("    Detail   : " + LlmClient.DescribeWebException(wex));
                Console.WriteLine("    Chain    : " + LlmClient.ExceptionChain(wex));
            }
            catch (Exception ex)
            {
                ConsoleUi.Danger("    Result   : NG " + ex.GetType().Name + "\n");
                Console.WriteLine("    Detail   : " + ex.Message);
            }
            Console.WriteLine();

            // --- ストリーミング呼び出し試験(対話と同じ経路) ---
            Console.WriteLine("  [streaming call test]");
            Console.WriteLine("    (same path as the chat: Accept: text/event-stream, receives SSE)");
            try
            {
                StringBuilder got = new StringBuilder();
                int chunks = 0;
                LlmClient.CallStream(cfg, msgs, false,
                    delegate (string d) { chunks++; if (got.Length < 200) got.Append(d); },
                    delegate (string d) { });
                Console.WriteLine("    Result   : OK  (" + chunks + " chunks received)");
                Console.WriteLine("    Reply    : " + FirstLine(got.ToString()));
            }
            catch (WebException wex)
            {
                ConsoleUi.Danger("    Result   : NG " + wex.Status + "\n");
                Console.WriteLine("    Detail   : " + LlmClient.DescribeWebException(wex));
                Console.WriteLine("    Chain    : " + LlmClient.ExceptionChain(wex));
            }
            catch (Exception ex)
            {
                ConsoleUi.Danger("    Result   : NG " + ex.GetType().Name + "\n");
                Console.WriteLine("    Detail   : " + LlmClient.ExceptionChain(ex));
            }
            Console.WriteLine();

            ConsoleUi.Dim("  [how to read this]\n");
            ConsoleUi.Dim("    - If the API call test is OK, tau can connect and authenticate.\n");
            ConsoleUi.Dim("      A failing TLS handshake test only reflects certificate or protocol results;\n");
            ConsoleUi.Dim("      HttpWebRequest may still succeed through another path.\n");
            ConsoleUi.Dim("    - TLSV1_ALERT_PROTOCOL_VERSION = the server rejects that TLS version\n");
            ConsoleUi.Dim("    - CERTIFICATE_VERIFY_FAILED = certificate verification failed (e.g. a corporate proxy)\n");
            ConsoleUi.Dim("    - SslProtocolType error = .NET cannot interpret that value\n");
        }

        static string Pad(string s)
        {
            if (s == null) s = "";
            while (s.Length < 14) s += " ";
            return s;
        }

        // TCP 接続 → TLS ハンドシェイクだけを試す(HTTP は送らない)。
        // HTTP メソッドに依存せず、TLS そのものの可否を判定できる。
        static string ProbeTls(Uri uri, Config cfg, SecurityProtocolType proto)
        {
            System.Net.Sockets.TcpClient client = null;
            System.Net.Security.SslStream ssl = null;
            try
            {
                client = new System.Net.Sockets.TcpClient();
                System.Net.Sockets.TcpClient c = client;
                IAsyncResult ar = c.BeginConnect(uri.Host, uri.Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(15000))
                {
                    try { c.Close(); } catch { }
                    return "NG  connection timed out (15s)";
                }
                c.EndConnect(ar);

                ssl = new System.Net.Security.SslStream(client.GetStream(), false,
                    delegate(object sender, System.Security.Cryptography.X509Certificates.X509Certificate cert,
                             System.Security.Cryptography.X509Certificates.X509Chain chain,
                             System.Net.Security.SslPolicyErrors errors)
                    {
                        // 証明書の検証は行う(検証失敗はそのまま例外になる)
                        return errors == System.Net.Security.SslPolicyErrors.None;
                    });

                ssl.AuthenticateAsClient(uri.Host, null,
                    (System.Security.Authentication.SslProtocols)(int)proto, false);

                string ver = "?";
                try { ver = ssl.SslProtocol.ToString(); } catch { }
                return "OK  " + ver + " / " + ssl.CipherAlgorithm.ToString();
            }
            catch (Exception ex)
            {
                // 例外の型と最初のメッセージだけを出す
                string msg = ex.Message ?? "";
                if (msg.Length > 160) msg = msg.Substring(0, 160) + "...";
                string inner = (ex.InnerException != null) ? FirstLine(ex.InnerException.Message) : "";
                string detail = "NG  " + ex.GetType().Name + ": " + msg;
                if (inner.Length > 0 && inner != msg) detail += "  <- " + inner;
                return detail;
            }
            finally
            {
                try { if (ssl != null) ssl.Dispose(); } catch { }
                try { if (client != null) client.Close(); } catch { }
            }
        }

        static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOf('\n');
            if (i > 0) s = s.Substring(0, i);
            return (s.Length > 200) ? s.Substring(0, 200) + "..." : s;
        }
    }
}
