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
        var proxyMemoryPath = Path.Combine(Path.GetTempPath(), "myrouter-smoke-memory.json");

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

                // /v1/chat/completions POST：LLM 记忆整理请求（body 带 x-memory-refine 标记）→ 返回整理结果
                if (path == "/v1/chat/completions" && ctx.Request.HttpMethod == "POST" &&
                    reqBody.Contains("x-memory-refine"))
                {
                    const string refineJson =
                        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"memories\\\":[\\\"用户喜欢喝咖啡\\\",\\\"用户关注天气\\\"]}\"}}]}";
                    var rBytes = System.Text.Encoding.UTF8.GetBytes(refineJson);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = rBytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(rBytes);
                    ctx.Response.Close();
                    continue;
                }

                // /v1/chat/completions POST：模拟上游 OpenAI 格式 SSE 流式响应
                // （echo 带 headers + body，供 /chat 与长期记忆用例断言）
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
        });

        // 先删残留文件再构造 ProxyServer——MemoryStore 构造时会 Load，
        // 后删的话内存里还留着旧条目（Case 19 会基于残留继续合并）
        File.Delete(proxyMemoryPath);
        var proxy = new ProxyServer(memoryPath: proxyMemoryPath, memoryRefineThreshold: 2);
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

        // ── Case 19: 长期记忆——短消息采集、LLM 整理、system 注入 ──
        await RunCase(proxy, http, "Long-term memory: collect, LLM-refine, inject",
            new AppConfig
            {
                UpstreamUrl = $"http://localhost:{upstreamPort}",
                Port = proxyPort,
                RequireAuth = false,
            }, async h =>
            {
                var post = (string body) => h.PostAsync($"http://localhost:{proxyPort}/chat",
                    new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

                // 1) 两条短消息（阈值=2）→ 第二次请求触发后台 LLM 整理
                var r1 = await post("{\"messages\":[{\"role\":\"user\",\"content\":\"我喜欢喝咖啡\"}]}");
                if (r1.StatusCode != HttpStatusCode.OK) return $"first chat failed: {r1.StatusCode}";
                await post("{\"messages\":[{\"role\":\"user\",\"content\":\"今天天气如何\"}]}");

                // 2) 轮询等待异步整理完成（mock 返回整理后的记忆列表）
                string json = "";
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < deadline)
                {
                    if (File.Exists(proxyMemoryPath))
                    {
                        json = File.ReadAllText(proxyMemoryPath);
                        if (json.Contains("用户喜欢喝咖啡") && json.Contains("用户关注天气")) break;
                    }
                    await Task.Delay(200);
                }
                if (!json.Contains("用户喜欢喝咖啡"))
                    return $"LLM refine result missing: {json[..Math.Min(200, json.Length)]}";
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.GetProperty("Pending").GetArrayLength() != 0)
                        return $"pending not cleared after refine: {json[..Math.Min(160, json.Length)]}";
                }

                // 3) 整理后的记忆注入下一次请求的 system
                var r3 = await post("{\"messages\":[{\"role\":\"user\",\"content\":\"介绍一下你自己\"}]}");
                var b3 = await r3.Content.ReadAsStringAsync();
                return b3.Contains("长期记忆") && b3.Contains("用户喜欢喝咖啡")
                    ? null
                    : $"memory not injected after refine: {b3[..Math.Min(300, b3.Length)]}";
            });

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
        upstreamTask.Wait(TimeSpan.FromSeconds(2));
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
