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
    class SessionCtx
    {
        public string Id;
        public string Title = "";
        public string Summary = "";
        public int ContextStart = 0;
        public int LastTotalTokens = 0;   // 直前の API 応答 usage.total_tokens
        public List<object> Messages = new List<object>();
    }

    // =======================================================================
    //  エージェント本体
    //
    //  1 ターン: LLM 呼び出し → ツール実行 → 再問い合わせ … を最終回答まで繰り返す
    // =======================================================================
}
