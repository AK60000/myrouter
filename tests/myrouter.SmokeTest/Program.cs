using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using myrouter.Models;
using myrouter.Services;

namespace myrouter.SmokeTest;

internal static class Program
{
    private static async Task<int> Main()
    {
        const int upstreamPort = 18999;
        const int proxyPort = 18998;
        var proxyAgentPath = Path.Combine(Path.GetTempPath(), "myrouter-smoke-agent.md");
        var proxyConvsPath = Path.Combine(Path.GetTempPath(), "myrouter-smoke-convs.json");

        using var upstream = new HttpListener();
        upstream.Prefixes.Add($"http://localhost:{upstreamPort}/");
        upstream.Start();
        var upstreamTask = Task.Run(async () =>
        {
            while (upstream.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await upstream.GetContextAsync(); }
                catch { return; }
                var path = ctx.Request.Url?.AbsolutePath ?? "";
                var query = ctx.Request.Url?.Query ?? "";
                var headers = string.Join(";", ctx.Request.Headers.AllKeys.Select(k => $"{k}=[{ctx.Request.Headers[k]}]"));
                var reqBody = "";
                if (ctx.Request.HasEntityBody)
                {
                    using var sr = new StreamReader(ctx.Request.InputStream);
                    reqBody = await sr.ReadToEndAsync();
                }

                // 整个请求处理包 try/catch：客户端断连导致的写失败（如 proxy 超时断开上游）或 mock 分支内部
                // 异常都必须隔离在单个请求内，不能终止 mock 线程——否则后续用例全部失败（曾实测踩过）
                // （分支代码保持原缩进，try 块作用域以末尾 catch 收口）
                try
                {

                // /slow 特殊路径：模拟上游响应缓慢，用于验证超时配置生效
                if (path == "/slow")
                {
                    await Task.Delay(3000);
                    var slowBytes = System.Text.Encoding.UTF8.GetBytes("slow-response");
                    ctx.Response.ContentLength64 = slowBytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(slowBytes);
                    ctx.Response.Close();
                    continue;
                }

                // /gzip 特殊路径：模拟上游返回 gzip 压缩响应
                if (path == "/gzip")
                {
                    var plain = "compressed-payload";
                    using var ms = new MemoryStream();
                    using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
                        gz.Write(System.Text.Encoding.UTF8.GetBytes(plain));
                    var gzBytes = ms.ToArray();
                    ctx.Response.AddHeader("Content-Encoding", "gzip");
                    ctx.Response.ContentLength64 = gzBytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(gzBytes);
                    ctx.Response.Close();
                    continue;
                }

                // /v1/chat/completions POST：AI 画像提炼请求（body 带 x-agent-refine 标记）→ 返回非流式画像
                // （真实上游缺 model 会 400，这里同样校验；思维链须在提炼前剥离，否则画像会被污染）
                if (path == "/v1/chat/completions" && ctx.Request.HttpMethod == "POST" &&
                    reqBody.Contains("x-agent-refine"))
                {
                    // "think" 子串同时覆盖 <thinking> 与 <think> 残留（两种标签都须在提炼前剥离，否则画像会被污染）
                    if (!reqBody.Contains("\"model\"") || reqBody.Contains("think"))
                    {
                        var eBytes = System.Text.Encoding.UTF8.GetBytes("{\"error\":\"model required or thinking not stripped\"}");
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.StatusCode = 400;
                        ctx.Response.ContentLength64 = eBytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(eBytes);
                        ctx.Response.Close();
                        continue;
                    }
                    // mock 返回带思维链块的画像：输出侧须剥离后才写盘，否则画像被污染（响应与磁盘都不该出现 thinking）
                    const string agentJson =
                        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"<thinking>用户偏好咖啡，近期在学后端</thinking>我是 Bobby，喜欢喝咖啡。\\n我在学 Spring Boot。\"}}]}";
                    var aBytes = System.Text.Encoding.UTF8.GetBytes(agentJson);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = aBytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(aBytes);
                    ctx.Response.Close();
                    continue;
                }

                // /v1/chat/completions POST：标题总结请求（body 带 x-title-refine 标记）→ 返回固定标题
                if (path == "/v1/chat/completions" && ctx.Request.HttpMethod == "POST" &&
                    reqBody.Contains("x-title-refine"))
                {
                    if (!reqBody.Contains("\"model\""))
                    {
                        var eBytes = System.Text.Encoding.UTF8.GetBytes("{\"error\":\"model is required\"}");
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.StatusCode = 400;
                        ctx.Response.ContentLength64 = eBytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(eBytes);
                        ctx.Response.Close();
                        continue;
                    }
                    // mock 返回带思维链块的标题：输出侧须剥离后才是干净标题
                    const string titleJson =
                        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"<thinking>这段对话的主角是小李</thinking>和小李的对话\"}}]}";
                    var tBytes = System.Text.Encoding.UTF8.GetBytes(titleJson);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = tBytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(tBytes);
                    ctx.Response.Close();
                    continue;
                }

                // /v1/chat/completions POST：模拟流式中途长时间停顿（body 带 x-idle-slow 标记）——
                // 首块立即发出，随后停 3 秒（> 1s 空闲超时）才补 [DONE]，用于验证 /chat 的空闲超时
                if (path == "/v1/chat/completions" && ctx.Request.HttpMethod == "POST" &&
                    reqBody.Contains("x-idle-slow"))
                {
                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.SendChunked = true;
                    try
                    {
                        await ctx.Response.OutputStream.WriteAsync(
                            System.Text.Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\n"));
                        ctx.Response.OutputStream.Flush();
                        await Task.Delay(3000);
                        await ctx.Response.OutputStream.WriteAsync(
                            System.Text.Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
                    }
                    catch { /* proxy 空闲超时断开上游连接后写失败是预期 */ }
                    try { ctx.Response.Close(); } catch { }   // Close 同样可能因断连抛，不能放 finally 打崩 mock 线程
                    continue;
                }

                // /v1/chat/completions POST：模拟上游 OpenAI 格式 SSE 流式响应
                // （echo 带 headers + body，供 /chat 用例断言）
                if (path == "/v1/chat/completions" && ctx.Request.HttpMethod == "POST")
                {
                    var sse =
                        "data: {\"echo\":{\"stream\":true,\"auth\":\"" + headers.Replace("\"", "'") + "\",\"body\":\"" + reqBody.Replace("\"", "'") + "\"},\"choices\":[{\"delta\":{\"reasoning_content\":\"让我想想，\"}}]}\n\n" +
                        "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"这个问题的答案很简单。\"}}]}\n\n" +
                        "data: {\"choices\":[{\"delta\":{\"content\":\"<thinking>先检查边界条件</thinking>你\"}}]}\n\n" +
                        "data: {\"choices\":[{\"delta\":{\"content\":\"好\"}}]}\n\n" +
                        "data: [DONE]\n\n";
                    var sseBytes = System.Text.Encoding.UTF8.GetBytes(sse);
                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.ContentLength64 = sseBytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(sseBytes);
                    ctx.Response.Close();
                    continue;
                }

                // /v1/models GET：模拟上游模型列表（带鉴权头回显，供 /models 用例断言）
                if (path == "/v1/models" && ctx.Request.HttpMethod == "GET")
                {
                    var modelsJson =
                        "{\"object\":\"list\",\"data\":[{\"id\":\"mock-model-a\"},{\"id\":\"mock-model-b\"}],\"auth\":\"" +
                        headers + "\"}";
                    var mBytes = System.Text.Encoding.UTF8.GetBytes(modelsJson);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = mBytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(mBytes);
                    ctx.Response.Close();
                    continue;
                }

                var resp = $"upstream got {ctx.Request.HttpMethod} {path}{query}; headers=[{headers}]; body={reqBody}";
                var bytes = System.Text.Encoding.UTF8.GetBytes(resp);
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
                }
                catch { /* 单个 mock 请求异常（断连写失败等）不影响主循环 */ }
            }
        });

        File.Delete(proxyAgentPath);   // 清残留，确保 Case 19 从干净状态开始
        File.Delete(proxyConvsPath);
        var proxy = new ProxyServer(agentProfilePath: proxyAgentPath, conversationsPath: proxyConvsPath);
        proxy.Log += Console.WriteLine;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var authCfg = new AppConfig
        {
            UpstreamUrl = $"http://localhost:{upstreamPort}",
            Port = proxyPort,
            RequireAuth = true,
            ApiKey = "secret-key-123",
        };
        var noAuthCfg = new AppConfig
        {
            UpstreamUrl = $"http://localhost:{upstreamPort}",
            Port = proxyPort,
            RequireAuth = false,
        };

        // ── Case 1: 鉴权失败（无 key） ──
        await RunCase(proxy, http, "No-auth request rejected with 401", authCfg, async h =>
        {
            var r = await h.GetAsync($"http://localhost:{proxyPort}/v1/models");
            var body = await r.Content.ReadAsStringAsync();
            return r.StatusCode == HttpStatusCode.Unauthorized
                ? null
                : $"should be 401, got {r.StatusCode} (body='{body}')";
        });

        // ── Case 2: Bearer 鉴权 + 路径透传 ──
        await RunCase(proxy, http, "Bearer auth + path passthrough", authCfg, async h =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{proxyPort}/v1/chat/completions");
            req.Headers.Add("Authorization", "Bearer secret-key-123");
            var r = await h.SendAsync(req);
            var body = await r.Content.ReadAsStringAsync();
            return r.StatusCode == HttpStatusCode.OK && body.Contains("/v1/chat/completions")
                ? null
                : $"status={r.StatusCode} body={body}";
        });

        // ── Case 3: x-api-key + POST body + query string ──
        await RunCase(proxy, http, "x-api-key + POST body + query forwarded correctly", authCfg, async h =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{proxyPort}/v1/embeddings?stream=true&foo=bar");
            req.Headers.Add("x-api-key", "secret-key-123");
            req.Content = new StringContent("{\"model\":\"x\"}", System.Text.Encoding.UTF8, "application/json");
            var r = await h.SendAsync(req);
            var body = await r.Content.ReadAsStringAsync();
            // 期望: 路径 + query + body 都正确转发；x-api-key 不应被作为 Authorization 转发
            var ok = r.StatusCode == HttpStatusCode.OK
                && body.Contains("/v1/embeddings")
                && body.Contains("stream=true")
                && body.Contains("foo=bar")
                && body.Contains("{\"model\":\"x\"}")
                && !body.Contains("Authorization=")              // 不该有 Authorization 头
                && body.Contains("x-api-key=[secret-key-123]"); // x-api-key 原样转发
            return ok ? null : $"status={r.StatusCode} body={body}";
        });

        // ── Case 4: 错误 key ──
        await RunCase(proxy, http, "Wrong key rejected with 401", authCfg, async h =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{proxyPort}/v1/models");
            req.Headers.Add("Authorization", "Bearer wrong-key");
            var r = await h.SendAsync(req);
            return r.StatusCode == HttpStatusCode.Unauthorized
                ? null
                : $"status={r.StatusCode}";
        });

        // ── Case 5: 鉴权关闭 ──
        await RunCase(proxy, http, "Auth disabled → request passes without key", noAuthCfg, async h =>
        {
            var r = await h.GetAsync($"http://localhost:{proxyPort}/anything");
            var body = await r.Content.ReadAsStringAsync();
            return r.StatusCode == HttpStatusCode.OK && body.Contains("/anything")
                ? null
                : $"status={r.StatusCode} body={body}";
        });

        // ── Case 6: 上游密钥替换客户端 Authorization ──
        await RunCase(proxy, http, "Upstream key replaces client's Authorization",
            new AppConfig
            {
                UpstreamUrl = $"http://localhost:{upstreamPort}",
                Port = proxyPort,
                RequireAuth = true,
                ApiKey = "local-key",
                UpstreamApiKey = "sk-real-upstream-key",
            }, async h =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{proxyPort}/v1/models");
                req.Headers.Add("Authorization", "Bearer local-key");
                var r = await h.SendAsync(req);
                var body = await r.Content.ReadAsStringAsync();
                // 期望: 上游看到 Authorization=[Bearer sk-real-upstream-key]，不是 local-key
                var ok = r.StatusCode == HttpStatusCode.OK
                    && body.Contains("Authorization=[Bearer sk-real-upstream-key]")
                    && !body.Contains("Authorization=[Bearer local-key]");
                return ok ? null : $"body={body}";
            });

        // ── Case 7: 上游密钥为空 → 仅用 x-api-key 鉴权，Authorization 原样透传给上游 ──
        await RunCase(proxy, http, "Pass-through mode (no upstream key) → all headers forwarded",
            new AppConfig
            {
                UpstreamUrl = $"http://localhost:{upstreamPort}",
                Port = proxyPort,
                RequireAuth = true,
                ApiKey = "local-key",
                UpstreamApiKey = "",
            }, async h =>
            {
                // 不发 Authorization（让 x-api-key 走本地鉴权路径），这样 Authorization 不会被本地消耗
                var req = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{proxyPort}/v1/models");
                req.Headers.TryAddWithoutValidation("x-api-key", "local-key");
                req.Headers.TryAddWithoutValidation("X-Custom-Upstream-Auth", "sk-client-original");
                var r = await h.SendAsync(req);
                var body = await r.Content.ReadAsStringAsync();
                // 期望: 上游看到自定义头被原样透传
                var ok = r.StatusCode == HttpStatusCode.OK
                    && body.Contains("X-Custom-Upstream-Auth=[sk-client-original]")
                    && body.Contains("x-api-key=[local-key]");   // x-api-key 也会被透传
                return ok ? null : $"body={body}";
            });

        // ── Case 12: 配置上游密钥时，本地鉴权用的 x-api-key 必须被剥离，不泄露给上游 ──
        await RunCase(proxy, http, "Upstream key set → client's x-api-key stripped before forwarding",
            new AppConfig
            {
                UpstreamUrl = $"http://localhost:{upstreamPort}",
                Port = proxyPort,
                RequireAuth = true,
                ApiKey = "local-key",
                UpstreamApiKey = "sk-real-upstream-key",
            }, async h =>
            {
                // 用 x-api-key 走本地鉴权；Authorization 不该出现（否则会带本地 key）
                var req = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{proxyPort}/v1/models");
                req.Headers.TryAddWithoutValidation("x-api-key", "local-key");
                var r = await h.SendAsync(req);
                var body = await r.Content.ReadAsStringAsync();
                // 期望: 上游收到 Authorization=[Bearer sk-real-upstream-key]，且完全没有 x-api-key
                var ok = r.StatusCode == HttpStatusCode.OK
                    && body.Contains("Authorization=[Bearer sk-real-upstream-key]")
                    && !body.Contains("x-api-key");
                return ok ? null : $"body={body}";
            });

        // ── Case 13: 上游 gzip 压缩响应原样透传（含 Content-Encoding 头） ──
        await RunCase(proxy, http, "Gzip upstream response passed through intact",
            new AppConfig
            {
                UpstreamUrl = $"http://localhost:{upstreamPort}",
                Port = proxyPort,
                RequireAuth = false,
            }, async h =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{proxyPort}/gzip");
                req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
                var r = await h.SendAsync(req);
                var body = await r.Content.ReadAsByteArrayAsync();
                // 期望: Content-Encoding: gzip 原样透传，body 仍是压缩字节（解压后 = compressed-payload）
                var enc = r.Content.Headers.ContentEncoding.ToString() ?? "";
                using var ms = new MemoryStream(body);
                using var gz = new GZipStream(ms, CompressionMode.Decompress);
                using var sr = new StreamReader(gz);
                var plain = await sr.ReadToEndAsync();
                var ok = r.StatusCode == HttpStatusCode.OK
                    && enc.Contains("gzip", StringComparison.OrdinalIgnoreCase)
                    && plain == "compressed-payload";
                return ok ? null : $"status={r.StatusCode} enc={enc} plain={plain}";
            });

        // ── Case 14: 上游超时配置生效 → 504 GatewayTimeout ──
        await RunCase(proxy, http, "Upstream timeout (1s) → 504",
            new AppConfig
            {
                UpstreamUrl = $"http://localhost:{upstreamPort}",
                Port = proxyPort,
                RequireAuth = false,
                UpstreamTimeoutSeconds = 1,
            }, async h =>
            {
                // mock 上游 /slow 延迟 3 秒，1 秒超时必然触发
                var r = await h.GetAsync($"http://localhost:{proxyPort}/slow");
                return r.StatusCode == HttpStatusCode.GatewayTimeout
                    ? null
                    : $"should be 504, got {r.StatusCode}";
            });

        // ── Case 15: Web 界面根路径本地服务（跳过鉴权），/v1/* 仍走鉴权代理 ──
        await RunCase(proxy, http, "GET / serves web page (no auth) while /v1/* still proxied",
            new AppConfig
            {
                UpstreamUrl = $"http://localhost:{upstreamPort}",
                Port = proxyPort,
                RequireAuth = true,
                ApiKey = "secret-key-123",
            }, async h =>
            {
                var page = await h.GetAsync($"http://localhost:{proxyPort}/");
                var pageBody = await page.Content.ReadAsStringAsync();
                if (page.StatusCode != HttpStatusCode.OK || !pageBody.Contains("<title>myrouter</title>"))
                    return $"web page should be 200 with marker, got {page.StatusCode} body='{pageBody[..Math.Min(200, pageBody.Length)]}'";
                // 不带 key 请求 /v1/* → 必须仍 401（证明根路径分流没有吞掉代理路径）
                var proxied = await h.GetAsync($"http://localhost:{proxyPort}/v1/models");
                return proxied.StatusCode == HttpStatusCode.Unauthorized
                    ? null
                    : $"proxied path should still require auth (401), got {proxied.StatusCode}";
            });

        // ── Case 16: /chat 无本地鉴权直接可用，转上游 SSE，且自动带 GUI 配置的上游 key ──
        await RunCase(proxy, http, "/chat streams SSE from upstream using configured upstream key",
            new AppConfig
            {
                UpstreamUrl = $"http://localhost:{upstreamPort}",
                Port = proxyPort,
                RequireAuth = true,
                ApiKey = "local-key",
                UpstreamApiKey = "sk-web-upstream-key",
            }, async h =>
            {
                // 不带任何本地鉴权头 → web 路径跳过鉴权
                var r = await h.PostAsync($"http://localhost:{proxyPort}/chat", new StringContent(
                    "{\"model\":\"m1\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}",
                    System.Text.Encoding.UTF8, "application/json"));
                var body = await r.Content.ReadAsStringAsync();
                var ct = r.Content.Headers.ContentType?.ToString() ?? "";
                var ok = r.StatusCode == HttpStatusCode.OK
                    && ct.Contains("text/event-stream")
                    && body.Contains("你") && body.Contains("[DONE]")
                    && body.Contains("\"stream\":true")                 // 后端自动补 stream=true
                    && body.Contains("reasoning_content")               // 字段思维链原样透传
                    && body.Contains("<thinking>")                      // 正文内嵌 thinking 标签原样透传
                    && body.Contains("'messages':[{'role':'user','content':'hi'}]")  // 请求体原样透传（代码运行能力由前端 tools 声明，服务端不注入）
                    && body.Contains("Authorization=[Bearer sk-web-upstream-key]"); // 用配置的上游 key
                if (!ok)
                    return $"status={r.StatusCode} ct={ct} body={body}";

                // 坏请求体（无 messages）→ 400 JSON error
                var bad = await h.PostAsync($"http://localhost:{proxyPort}/chat", new StringContent(
                    "{}", System.Text.Encoding.UTF8, "application/json"));
                var badBody = await bad.Content.ReadAsStringAsync();
                return bad.StatusCode == HttpStatusCode.BadRequest && badBody.Contains("error")
                    ? null
                    : $"bad request should be 400 JSON, got {bad.StatusCode} body='{badBody}'";
            });

        // ── Case 17: /models 免鉴权拉模型列表，key 用配置的上游 key ──
        await RunCase(proxy, http, "/models lists upstream models with configured key",
            new AppConfig
            {
                UpstreamUrl = $"http://localhost:{upstreamPort}",
                Port = proxyPort,
                RequireAuth = true,
                ApiKey = "local-key",
                UpstreamApiKey = "sk-model-key",
            }, async h =>
            {
                var r = await h.GetAsync($"http://localhost:{proxyPort}/models");
                var body = await r.Content.ReadAsStringAsync();
                var ok = r.StatusCode == HttpStatusCode.OK
                    && body.Contains("mock-model-a")
                    && body.Contains("Authorization=[Bearer sk-model-key]");
                return ok
                    ? null
                    : $"should list models with configured key, got {r.StatusCode} body='{body[..Math.Min(160, body.Length)]}'";
            });

        // ── Case 18: /logo.png 输出内嵌图标转的 PNG ──
        await RunCase(proxy, http, "/logo.png serves PNG derived from embedded ico",
            new AppConfig
            {
                UpstreamUrl = $"http://localhost:{upstreamPort}",
                Port = proxyPort,
                RequireAuth = true,
                ApiKey = "local-key",
            }, async h =>
            {
                var r = await h.GetAsync($"http://localhost:{proxyPort}/logo.png");
                var bytes = await r.Content.ReadAsByteArrayAsync();
                var ct = r.Content.Headers.ContentType?.ToString() ?? "";
                var isPng = bytes.Length > 8 &&
                            bytes[0] == 0x89 && bytes[1] == 0x50 &&
                            bytes[2] == 0x4E && bytes[3] == 0x47;
                return r.StatusCode == HttpStatusCode.OK && ct.Contains("image/png") && isPng
                    ? null
                    : $"status={r.StatusCode} ct={ct} len={bytes.Length} png={isPng}";
            });

        // ── Case 18.5: /md/vendor.js + /md/katex.css 提供前端第三方依赖（markdown-it + highlight.js + KaTeX） ──
        await RunCase(proxy, http, "/md/vendor.js + /md/katex.css serve frontend deps",
            new AppConfig
            {
                UpstreamUrl = $"http://localhost:{upstreamPort}",
                Port = proxyPort,
                RequireAuth = true,
                ApiKey = "local-key",
            }, async h =>
            {
                var v = await h.GetAsync($"http://localhost:{proxyPort}/md/vendor.js");
                var vBody = await v.Content.ReadAsStringAsync();
                var vCt = v.Content.Headers.ContentType?.ToString() ?? "";
                if (v.StatusCode != HttpStatusCode.OK || !vCt.Contains("javascript")
                    || !vBody.Contains("MarkdownIt") || !vBody.Contains("hljs") || !vBody.Contains("katex"))
                    return $"vendor failed: {v.StatusCode} ct={vCt} len={vBody.Length}";

                var k = await h.GetAsync($"http://localhost:{proxyPort}/md/katex.css");
                var kBody = await k.Content.ReadAsStringAsync();
                var kCt = k.Content.Headers.ContentType?.ToString() ?? "";
                return k.StatusCode == HttpStatusCode.OK && kCt.Contains("text/css")
                       && kBody.Contains("@font-face") && kBody.Contains("data:font/woff2;base64")
                    ? null
                    : $"katex.css failed: {k.StatusCode} ct={kCt} len={kBody.Length}";
            });

        // ── Case 19: 用户画像——agent.md 内容作为 system 注入（手写维护的静态记忆） ──
        await RunCase(proxy, http, "Agent profile: inject agent.md as system",
            new AppConfig
            {
                UpstreamUrl = $"http://localhost:{upstreamPort}",
                Port = proxyPort,
                RequireAuth = false,
            }, async h =>
            {
                File.WriteAllText(proxyAgentPath, "我是 Bobby，喜欢喝咖啡。<thinking>早期版本混入的思维链残留</thinking>");
                try
                {
                    var r = await h.PostAsync($"http://localhost:{proxyPort}/chat",
                        new StringContent("{\"messages\":[{\"role\":\"user\",\"content\":\"介绍一下你自己\"}]}",
                            System.Text.Encoding.UTF8, "application/json"));
                    var b = await r.Content.ReadAsStringAsync();
                    // 注入前须剥离存量思维链：system 里有画像正文、没有 thinking 残留。
                    // mock 的 SSE 会回显请求体（echo.body）并附带自身 thinking 内容（choices.delta），
                    // 因此只检查 echo 段（含注入的 system），不检查 choices 段
                    var echo = b.Split("choices")[0];
                    return r.StatusCode == HttpStatusCode.OK && b.Contains("我是 Bobby") && !echo.Contains("<thinking>")
                        ? null
                        : $"agent.md not injected: {b[..Math.Min(300, b.Length)]}";
                }
                finally { File.Delete(proxyAgentPath); }
            });

        // ── Case 20: /agent 端点——GET 读、POST 保存、POST AI 生成写盘 ──
        await RunCase(proxy, http, "/agent: read, save, AI-generate",
            new AppConfig
            {
                UpstreamUrl = $"http://localhost:{upstreamPort}",
                Port = proxyPort,
                RequireAuth = false,
            }, async h =>
            {
                var get = () => h.GetAsync($"http://localhost:{proxyPort}/agent");
                var post = (string body) => h.PostAsync($"http://localhost:{proxyPort}/agent",
                    new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

                // 1) GET 初始为空
                var r0 = await get();
                var b0 = await r0.Content.ReadAsStringAsync();
                if (r0.StatusCode != HttpStatusCode.OK || !b0.Contains("\"content\":\"\""))
                    return $"read empty failed: {r0.StatusCode} {b0[..Math.Min(120, b0.Length)]}";

                // 2) POST content 直接保存
                var r1 = await post("{\"content\":\"手写画像：我住在深圳。\"}");
                var b1 = await r1.Content.ReadAsStringAsync();
                if (r1.StatusCode != HttpStatusCode.OK || !b1.Contains("我住在深圳"))
                    return $"save failed: {r1.StatusCode} {b1[..Math.Min(160, b1.Length)]}";

                // 3) GET 验证保存已落盘
                var b2 = await (await get()).Content.ReadAsStringAsync();
                if (!b2.Contains("我住在深圳"))
                    return $"saved content not persisted: {b2[..Math.Min(160, b2.Length)]}";

                // 4) POST messages → AI 提炼（携带 model 与真实前端一致；消息里故意带 <thinking> 思维链块，
                //    mock 校验其已被服务端剥离后才放行），返回并覆盖文件
                var r3 = await post("{\"model\":\"mock-model-a\",\"messages\":[{\"role\":\"user\",\"content\":\"我最近在学 Spring Boot\"},{\"role\":\"assistant\",\"content\":\"我也喜欢<thinking>其实也很喜欢写代码</thinking>编程\"}]}");
                var b3 = await r3.Content.ReadAsStringAsync();
                if (r3.StatusCode != HttpStatusCode.OK || !b3.Contains("喜欢喝咖啡") || !b3.Contains("Spring Boot") || b3.Contains("<thinking>"))
                    return $"AI generate failed: {r3.StatusCode} {b3[..Math.Min(200, b3.Length)]}";
                var disk = File.ReadAllText(proxyAgentPath);
                if (!disk.Contains("喜欢喝咖啡") || disk.Contains("<thinking>"))
                    return $"generated profile not persisted: {disk[..Math.Min(160, disk.Length)]}";

                File.Delete(proxyAgentPath);   // 清理，不影响后续用例
                return null;
            });

        // ── Case 21: /conversations 会话——建/存(自动标题)/列表/读/删 ──
        await RunCase(proxy, http, "/conversations: create, save, list, get, delete",
            new AppConfig
            {
                UpstreamUrl = $"http://localhost:{upstreamPort}",
                Port = proxyPort,
                RequireAuth = false,
            }, async h =>
            {
                var list = () => h.GetAsync($"http://localhost:{proxyPort}/conversations");

                // 1) 初始列表为空
                var b0 = await (await list()).Content.ReadAsStringAsync();
                if (!b0.Contains("\"items\":[]"))
                    return $"initial list not empty: {b0[..Math.Min(120, b0.Length)]}";

                // 2) 新建会话
                var r1 = await h.PostAsync($"http://localhost:{proxyPort}/conversations", null);
                var b1 = await r1.Content.ReadAsStringAsync();
                string cid;
                using (var d = JsonDocument.Parse(b1))
                    cid = d.RootElement.GetProperty("id").GetString() ?? "";
                if (r1.StatusCode != HttpStatusCode.OK || cid.Length == 0)
                    return $"create failed: {r1.StatusCode} {b1[..Math.Min(120, b1.Length)]}";

                // 3) 保存消息（不带标题 → 自动取首条用户消息前 20 字）
                var r2 = await h.PostAsync($"http://localhost:{proxyPort}/conversations/{cid}",
                    new StringContent("{\"messages\":[{\"role\":\"user\",\"content\":\"你好，我叫小李\"},{\"role\":\"assistant\",\"content\":\"你好！\"}]}",
                        System.Text.Encoding.UTF8, "application/json"));
                var b2 = await r2.Content.ReadAsStringAsync();
                if (r2.StatusCode != HttpStatusCode.OK || !b2.Contains("你好，我叫小李"))
                    return $"save failed: {r2.StatusCode} {b2[..Math.Min(160, b2.Length)]}";

                // 4) 列表含自动标题元数据（Web 响应字段为 camelCase：title/id）
                var b3 = await (await list()).Content.ReadAsStringAsync();
                if (!b3.Contains("\"title\":\"你好，我叫小李\"") || !b3.Contains("\"id\":\"" + cid))
                    return $"list missing camelCase fields: {b3[..Math.Min(200, b3.Length)]}";

                // 4.5) LLM 总结标题覆盖自动截取（mock 返回固定标题；携带 model，与真实前端一致）
                var r4 = await h.PostAsync($"http://localhost:{proxyPort}/conversations/{cid}/title",
                    new StringContent("{\"model\":\"mock-model-a\",\"messages\":[{\"role\":\"user\",\"content\":\"你好，我叫小李\"}]}",
                        System.Text.Encoding.UTF8, "application/json"));
                var b4b = await r4.Content.ReadAsStringAsync();
                if (r4.StatusCode != HttpStatusCode.OK || !b4b.Contains("和小李的对话") || b4b.Contains("<thinking>"))
                    return $"title summarize failed: {r4.StatusCode} {b4b[..Math.Min(160, b4b.Length)]}";

                // 5) 读详情能拿回消息
                var b4 = await (await h.GetAsync($"http://localhost:{proxyPort}/conversations/{cid}"))
                    .Content.ReadAsStringAsync();
                if (!b4.Contains("\"role\":\"assistant\"") || !b4.Contains("你好！"))
                    return $"get detail failed: {b4[..Math.Min(200, b4.Length)]}";

                // 6) 删除后再查 404
                var r5 = await h.DeleteAsync($"http://localhost:{proxyPort}/conversations/{cid}");
                var b5 = await (await h.GetAsync($"http://localhost:{proxyPort}/conversations/{cid}"))
                    .Content.ReadAsStringAsync();
                if (r5.StatusCode != HttpStatusCode.OK || !b5.Contains("会话不存在"))
                    return $"delete failed: {r5.StatusCode} {b5[..Math.Min(120, b5.Length)]}";

                // 7) 列表为空
                var b6 = await (await list()).Content.ReadAsStringAsync();
                if (!b6.Contains("\"items\":[]"))
                    return $"list not empty after delete: {b6[..Math.Min(160, b6.Length)]}";

                return null;
            });

        // ── Case 21.5: ConversationStore.Load —— 旧格式磁盘文件反序列化（PascalCase + JsonObject 消息恢复） ──
        {
            var convsPath = Path.Combine(Path.GetTempPath(), "myrouter-smoke-load-convs.json");
            File.Delete(convsPath);
            File.WriteAllText(convsPath,
                """{"Conversations":[{"Id":"conv-load","Title":"旧会话","Messages":[{"role":"user","content":"你好"}],"CreatedAt":"2026-09-01T10:00:00","UpdatedAt":"2026-09-01T10:00:00"}]}""");
            try
            {
                var store = new ConversationStore(convsPath);
                var errs = new List<string>();
                var list = store.List();
                if (list.Count != 1 || list[0].Id != "conv-load" || list[0].Title != "旧会话" ||
                    list[0].UpdatedAt != "2026-09-01T10:00:00")
                    errs.Add($"列表恢复: {list.Count} 条 / UpdatedAt={(list.Count > 0 ? list[0].UpdatedAt : "(empty)")}");
                var c = store.Get("conv-load");
                if (c is null || c.Messages.Count != 1 ||
                    c.Messages[0]["role"]?.GetValue<string>() != "user" ||
                    c.Messages[0]["content"]?.GetValue<string>() != "你好" ||
                    c.CreatedAt != "2026-09-01T10:00:00" ||
                    c.UpdatedAt != "2026-09-01T10:00:00")
                    errs.Add($"消息/时间戳未恢复: msgs={c?.Messages.Count}, ca={c?.CreatedAt}, ua={c?.UpdatedAt}");
                Console.WriteLine(errs.Count == 0
                    ? "[OK] ConversationStore: load legacy file"
                    : $"[FAIL] ConversationStore load: {string.Join("; ", errs)}");
                if (errs.Count > 0) _failures++;
            }
            finally { File.Delete(convsPath); }
        }

        // ── Case 21.6: JsonOpts.Pretty round-trip —— 三处写入器统一编码策略不漂移 ──
        {
            var errs = new List<string>();

            // ConversationStore 写入 + 重读，中文标题/消息还原 + 磁盘含原字符（非 \uXXXX）。
            // 三个写入器（AppConfig / Companion / ConversationStore）都用 Pretty，
            // 编码策略从 Unsafe 漂到 default 会立即体现在磁盘文件上。
            var rtPath = Path.Combine(Path.GetTempPath(), "myrouter-smoke-roundtrip.json");
            File.Delete(rtPath);
            try
            {
                var s1 = new ConversationStore(rtPath);
                var c = s1.Create();
                var msgs = new List<System.Text.Json.Nodes.JsonObject>
                {
                    new() { ["role"] = "user", ["content"] = "你好 round-trip" },
                };
                s1.Save(c.Id, "中文标题", msgs);
                var s2 = new ConversationStore(rtPath);
                var c2 = s2.Get(c.Id);
                if (c2 is null || c2.Title != "中文标题" ||
                    c2.Messages.Count != 1 ||
                    c2.Messages[0]["content"]?.GetValue<string>() != "你好 round-trip")
                    errs.Add($"ConversationStore round-trip: title={c2?.Title}, msgs={c2?.Messages.Count}");
                var disk2 = File.ReadAllText(rtPath);
                if (!disk2.Contains("中文标题") || disk2.Contains("\\u4e2d\\u6587"))
                    errs.Add($"ConversationStore 中文被转义: {disk2}");
            }
            finally { File.Delete(rtPath); }

            Console.WriteLine(errs.Count == 0
                ? "[OK] JsonOpts.Pretty round-trip"
                : $"[FAIL] JsonOpts.Pretty round-trip: {string.Join("; ", errs)}");
            if (errs.Count > 0) _failures++;
        }

        // ── Case 21.7: ConversationStore.Load 容错 —— 单条损坏不应连累其它会话（R1-1 fix witness） ──
        {
            var corruptPath = Path.Combine(Path.GetTempPath(), "myrouter-smoke-corrupt-convs.json");
            File.Delete(corruptPath);
            // 三条会话：good-1 正常、corrupt 最后一条消息是 JSON 字符串（非对象，pre-diff 代码会跳过，
            // buggy Deserialize<Data> 会抛 catch 重置 下次 Save 抹掉全部）、good-2 正常。
            // 用 JsonNode 直接构造 JSON，避免源文件里的字符串转义反复出错。
            var root = new System.Text.Json.Nodes.JsonObject
            {
                ["Conversations"] = new System.Text.Json.Nodes.JsonArray
                {
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["Id"] = "good-1", ["Title"] = "正常1",
                        ["Messages"] = new System.Text.Json.Nodes.JsonArray
                        {
                            new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = "hi" },
                        },
                        ["CreatedAt"] = "2026-09-01T10:00:00",
                        ["UpdatedAt"] = "2026-09-01T10:00:00",
                    },
                    // corrupt: Messages 数组里有字符串而非对象（pre-diff 容错跳过；buggy Deserialize<Data> 抛）
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["Id"] = "corrupt", ["Title"] = "损坏",
                        ["Messages"] = new System.Text.Json.Nodes.JsonArray { "I am a JSON string" },
                        ["CreatedAt"] = "2026-09-01T10:00:00",
                        ["UpdatedAt"] = "2026-09-01T10:00:00",
                    },
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["Id"] = "good-2", ["Title"] = "正常2",
                        ["Messages"] = new System.Text.Json.Nodes.JsonArray
                        {
                            new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = "hi" },
                        },
                        ["CreatedAt"] = "2026-09-01T10:00:00",
                        ["UpdatedAt"] = "2026-09-01T10:00:00",
                    },
                },
            };
            File.WriteAllText(corruptPath, root.ToJsonString());
            try
            {
                var store = new ConversationStore(corruptPath);
                var list = store.List();
                var g1 = store.Get("good-1");
                var g2 = store.Get("good-2");
                // good-1 + good-2 共 2 条；corrupt 那条 Messages 不是对象数组，per-conv 跳过。
                // 同时验证 good 条目的 Title 与 Messages 内容真被恢复（不是只加载了 metadata）。
                var ok = list.Count == 2
                    && list.Any(m => m.Id == "good-1")
                    && list.Any(m => m.Id == "good-2")
                    && !list.Any(m => m.Id == "corrupt")
                    && g1?.Title == "正常1" && g1?.Messages.Count == 1 && g1?.Messages[0]["content"]?.GetValue<string>() == "hi"
                    && g2?.Title == "正常2" && g2?.Messages.Count == 1 && g2?.Messages[0]["content"]?.GetValue<string>() == "hi";
                Console.WriteLine(ok
                    ? "[OK] ConversationStore: skip corrupt entry, keep others"
                    : $"[FAIL] ConversationStore corruption tolerance: loaded {list.Count} entries (ids: {string.Join(",", list.Select(m => m.Id))})");
                if (!ok) _failures++;
            }
            finally { File.Delete(corruptPath); }
        }

        // ── Case 22: StripThinking 语义回归（与前端 splitThinking 对齐） ──
        await RunCase(proxy, http, "StripThinking: <think>/<thinking> tags, unclosed, no false-positive on bare 'thinking'",
            new AppConfig
            {
                UpstreamUrl = $"http://localhost:{upstreamPort}",
                Port = proxyPort,
                RequireAuth = false,
            }, async h =>
            {
                var strip = typeof(ProxyServer).GetMethod("StripThinking",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                string St(string s) => (string)strip.Invoke(null, new object[] { s })!;
                var cases = new (string input, string expect)[]
                {
                    ("我在 thinking 了很久才明白", "我在 thinking 了很久才明白"),   // 裸 thinking 无闭合 → 不误删（修复前会删到结尾）
                    ("unthinking 机器", "unthinking 机器"),                       // 词边界：不误伤普通单词
                    ("<think>悄悄想</think>正文", "正文"),                          // <think> 闭合 → 剥离（修复前不剥）
                    ("<thinking>a</thinking>正文", "正文"),                        // <thinking> 闭合 → 剥离
                    ("thinking 过程 response 正文", "正文"),                        // 裸 thinking…response 兜底剥离（早期历史）
                    ("<thinking>进行中", ""),                                     // 未闭合 → 剩余全剥（与前端 splitThinking 一致）
                    ("<THINKING>x</THINKING>ok", "ok"),                           // 大小写不敏感
                };
                foreach (var (input, expect) in cases)
                {
                    var got = St(input);
                    if (got != expect)
                        return $"strip thinking failed: '{input}' → '{got}', expect '{expect}'";
                }

                // 端到端：<think> 残留会被 mock 上游拒绝（校验 reqBody 含 "think"）→ 修复前 400、修复后通过
                var r = await h.PostAsync($"http://localhost:{proxyPort}/agent",
                    new StringContent(
                        "{\"model\":\"mock-model-a\",\"messages\":[{\"role\":\"user\",\"content\":\"我最近在学 Go\"},{\"role\":\"assistant\",\"content\":\"<think>其实我也在想</think>写 Go 挺顺手\"}]}",
                        System.Text.Encoding.UTF8, "application/json"));
                var b = await r.Content.ReadAsStringAsync();
                File.Delete(proxyAgentPath);   // 清理写盘画像，不影响后续用例
                // mock 返回的画像本身带 <thinking> 块：输出侧剥了才干净（响应与磁盘都不得含思维链）
                return r.StatusCode == HttpStatusCode.OK && b.Contains("喜欢喝咖啡") && !b.Contains("<thinking>")
                    ? null
                    : $"<think> strip failed: {r.StatusCode} {b[..Math.Min(200, b.Length)]}";
            });

        // ── Case 23: /chat 流式中途空闲超时 → 已开始的 SSE 追加错误事件（而非整体总时长掐断） ──
        await RunCase(proxy, http, "/chat idle timeout mid-stream → error event appended",
            new AppConfig
            {
                UpstreamUrl = $"http://localhost:{upstreamPort}",
                Port = proxyPort,
                RequireAuth = false,
                UpstreamTimeoutSeconds = 1,
            }, async h =>
            {
                var r = await h.PostAsync($"http://localhost:{proxyPort}/chat", new StringContent(
                    "{\"model\":\"m1\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"x-idle-slow\":true}",
                    System.Text.Encoding.UTF8, "application/json"));
                var body = await r.Content.ReadAsStringAsync();
                // 期望: 200 + 已收首块 + 超时错误事件收尾 + 无 [DONE]（流式不被整体总时长误杀）
                var ok = r.StatusCode == HttpStatusCode.OK
                    && body.Contains("你好")
                    && body.Contains("\"error\"")
                    && !body.Contains("[DONE]");
                return ok ? null : $"status={r.StatusCode} body={body}";
            });

        // ── Case 24: Companion——快照差值/历史裁剪/播报分支/静音（独立实例 + 临时文件，不碰真实数据） ──
        {
            var companionPath = Path.Combine(Path.GetTempPath(), "myrouter-smoke-companion.json");
            File.Delete(companionPath);
            var cProxy = new ProxyServer(
                Path.Combine(Path.GetTempPath(), "myrouter-smoke-companion-agent.md"),
                Path.Combine(Path.GetTempPath(), "myrouter-smoke-companion-convs.json"));
            try
            {
                var companion = new Companion(cProxy, companionPath);
                var errs = new List<string>();
                void Check(bool cond, string msg) { if (!cond) errs.Add(msg); }

                // 1) 时段分支：深夜只劝睡、白天问候
                Check(companion.BuildMessage(new DateTime(2026, 9, 8, 23, 30, 0), true) == "夜深了，早点休息，别让代理替你熬夜", "23点应劝睡");
                Check(companion.BuildMessage(new DateTime(2026, 9, 8, 0, 30, 0), true) == "夜深了，早点休息，别让代理替你熬夜", "0点应劝睡");
                Check(companion.BuildMessage(new DateTime(2026, 9, 8, 3, 0, 0), true) == "凌晨还在折腾？快去睡吧，明天再战", "3点应劝睡");
                Check(companion.BuildMessage(new DateTime(2026, 9, 8, 8, 0, 0), true) == "早上好。", "无请求时应只问候");
                Check(companion.BuildMessage(new DateTime(2026, 9, 8, 12, 0, 0), true) == "中午好。", "12点应中午好");
                Check(companion.BuildMessage(new DateTime(2026, 9, 8, 20, 0, 0), true) == "晚上好。", "20点应晚上好");

                // 2) 快照差值：基线后新增的计数才累计进当天，不重复
                cProxy.Stats.Requests = 0; cProxy.Stats.TokensIn = 0; cProxy.Stats.TokensOut = 0;
                companion.Snapshot(DateTime.Now);
                cProxy.Stats.Requests = 7; cProxy.Stats.TokensIn = 300; cProxy.Stats.TokensOut = 600;
                companion.Snapshot(DateTime.Now);
                var disk = File.ReadAllText(companionPath);
                var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                Check(disk.Contains($"\"{today}\"") && disk.Contains("\"Requests\": 7") && disk.Contains("\"Tokens\": 900"),
                    $"快照差值累计: {disk}");

                // 3) 历史裁剪：超 60 天的档期写入后立即被移除，且不误删今天
                cProxy.Stats.Requests = 8;
                companion.Snapshot(DateTime.Now.AddDays(-70));
                var oldKey = DateTime.Now.AddDays(-70).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                var disk3 = File.ReadAllText(companionPath);
                Check(!disk3.Contains($"\"{oldKey}\""), "70天前记录应被裁剪");
                Check(disk3.Contains($"\"{today}\""), "裁剪不应误删今天记录");
                cProxy.Stats.Requests = 9;
                companion.Snapshot(DateTime.Now);   // 今天 +1，同时把差值基线推到 9

                // 4) 本次运行无请求时播报昨天（快照差值把新增计数记入昨天）
                cProxy.Stats.Requests = 10;
                companion.Snapshot(DateTime.Now.AddDays(-1));   // 昨天记录 +1
                cProxy.Stats.Requests = 0;                      // 模拟本次运行 0 请求（字段直接写仅测试用）
                var yesterdayMsg = companion.BuildMessage(new DateTime(2026, 9, 8, 9, 0, 0), true);
                Check(yesterdayMsg.Contains("昨天默默跑了 1 个请求"), $"应播报昨天: {yesterdayMsg}");

                // 5) 有请求时播报本次运行统计（含错误/超时计数）
                cProxy.Stats.Requests = 5; cProxy.Stats.TokensIn = 100; cProxy.Stats.TokensOut = 200;
                cProxy.Stats.Errors = 1; cProxy.Stats.Timeouts = 2;
                var withStats = companion.BuildMessage(new DateTime(2026, 9, 8, 9, 0, 0), true);
                Check(withStats.Contains("5 个请求") && withStats.Contains("300 token") && withStats.Contains("3 个出问题"),
                    $"统计播报: {withStats}");

                // 6) 静音：不触发 Says
                var said = 0;
                companion.Says += _ => said++;
                companion.Speak("hello");
                Check(said == 1, "未静音时 Speak 应触发");
                companion.SetMuted(true);
                companion.Speak("hello2");
                Check(said == 1, "静音后 Speak 不应触发");
                companion.SetMuted(false);
                companion.Speak("hello3");
                Check(said == 2, "恢复后 Speak 应触发");

                Console.WriteLine(errs.Count == 0
                    ? "[OK] Companion: snapshot delta, trim, message branches, mute"
                    : $"[FAIL] Companion: {string.Join("; ", errs)}");
                if (errs.Count > 0) _failures++;
            }
            finally
            {
                cProxy.Dispose();
                File.Delete(companionPath);
            }
        }

        // ── Case 8: 前缀去重 ─ upstream=/v1, client=/v1/chat/completions → /v1/chat/completions ──
        await RunCase(proxy, http, "Prefix dedup: /v1 + /v1/chat/completions → /v1/chat/completions",
            new AppConfig
            {
                UpstreamUrl = $"http://localhost:{upstreamPort}/v1",
                Port = proxyPort,
                RequireAuth = false,
            }, async h =>
            {
                var r = await h.GetAsync($"http://localhost:{proxyPort}/v1/chat/completions");
                var body = await r.Content.ReadAsStringAsync();
                var ok = r.StatusCode == HttpStatusCode.OK
                    && body.Contains("upstream got GET /v1/chat/completions")
                    && !body.Contains("/v1/v1/");
                return ok ? null : $"status={r.StatusCode} body={body}";
            });

        // ── Case 9: 段级去重 ─ upstream=/api/v1, client=/v1/chat/completions → /api/v1/chat/completions ──
        await RunCase(proxy, http, "Segment dedup: /api/v1 + /v1/chat/completions → /api/v1/chat/completions",
            new AppConfig
            {
                UpstreamUrl = $"http://localhost:{upstreamPort}/api/v1",
                Port = proxyPort,
                RequireAuth = false,
            }, async h =>
            {
                var r = await h.GetAsync($"http://localhost:{proxyPort}/v1/chat/completions");
                var body = await r.Content.ReadAsStringAsync();
                return r.StatusCode == HttpStatusCode.OK
                    && body.Contains("upstream got GET /api/v1/chat/completions")
                    ? null
                    : $"status={r.StatusCode} body={body}";
            });

        // ── Case 10: 完全相等 ─ upstream=/v1/chat/completions, client=/v1/chat/completions ──
        await RunCase(proxy, http, "Exact match: /v1/chat/completions + /v1/chat/completions → /v1/chat/completions",
            new AppConfig
            {
                UpstreamUrl = $"http://localhost:{upstreamPort}/v1/chat/completions",
                Port = proxyPort,
                RequireAuth = false,
            }, async h =>
            {
                var r = await h.GetAsync($"http://localhost:{proxyPort}/v1/chat/completions");
                var body = await r.Content.ReadAsStringAsync();
                var ok = r.StatusCode == HttpStatusCode.OK
                    && body.Contains("upstream got GET /v1/chat/completions")
                    && !body.Contains("/v1/chat/completions/v1/");
                return ok ? null : $"status={r.StatusCode} body={body}";
            });

        // ── Case 11: 版本段不同 ─ upstream=/api/v2, client=/v1/chat/completions → /api/v2/v1/chat/completions ──
        // 验证：版本段不一致时不触发段级去重，原样拼
        await RunCase(proxy, http, "Version mismatch: /api/v2 + /v1/chat/completions → /api/v2/v1/chat/completions",
            new AppConfig
            {
                UpstreamUrl = $"http://localhost:{upstreamPort}/api/v2",
                Port = proxyPort,
                RequireAuth = false,
            }, async h =>
            {
                var r = await h.GetAsync($"http://localhost:{proxyPort}/v1/chat/completions");
                var body = await r.Content.ReadAsStringAsync();
                return r.StatusCode == HttpStatusCode.OK
                    && body.Contains("upstream got GET /api/v2/v1/chat/completions")
                    ? null
                    : $"status={r.StatusCode} body={body}";
            });

        await proxy.StopAsync();
        proxy.Dispose();
        upstream.Stop();
        try { upstreamTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* mock 线程异常已在循环内隔离，此处仅清理 */ }
        Console.WriteLine(_failures == 0
            ? "\n冒烟测试完成，全部通过。"
            : $"\n冒烟测试完成，{_failures} 个用例失败。");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 重启 proxy（停旧实例 → 用给定配置启动），执行测试 lambda。
    /// test 返回 null = 通过；返回非 null = 失败原因。
    /// </summary>
    private static int _failures;

    private static async Task RunCase(
        ProxyServer proxy, HttpClient http, string name, AppConfig cfg,
        Func<HttpClient, Task<string?>> test)
    {
        try
        {
            await proxy.StopAsync();
            await proxy.StartAsync(cfg);
            var fail = await test(http);
            Console.WriteLine(fail is null ? $"[OK] {name}" : $"[FAIL] {name}: {fail}");
            if (fail is not null) _failures++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] {name}: {ex.Message}");
            _failures++;
        }
    }
}
