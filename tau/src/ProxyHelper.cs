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
    static class ProxyHelper
    {
        public static void Apply(HttpWebRequest request, Config cfg)
        {
            if (cfg.ProxyUrl != null && cfg.ProxyUrl.Trim().Length > 0)
            {
                WebProxy p = new WebProxy(cfg.ProxyUrl.Trim());
                if (cfg.ProxyUser != null && cfg.ProxyUser.Length > 0)
                    p.Credentials = new NetworkCredential(cfg.ProxyUser, cfg.ProxyPass);
                else if (cfg.ProxyPass != null && cfg.ProxyPass.Length > 0)
                    p.Credentials = new NetworkCredential("", cfg.ProxyPass);
                else
                    p.UseDefaultCredentials = true; // 社内(Windows統合認証)プロキシ向け
                request.Proxy = p;
            }
            else if (!cfg.UseSystemProxy)
            {
                request.Proxy = null; // 直接接続(システムプロキシを無視)
            }
        }
    }

    // =======================================================================
    //  LLM API クライアント (OpenAI 互換)
    // =======================================================================
}
