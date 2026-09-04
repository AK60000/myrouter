using System;
using System.IO;
using System.Net;
using System.Net.Http;
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
                var resp = $"upstream got {ctx.Request.HttpMethod} {path}{query}; headers=[{headers}]; body={reqBody}";
                var bytes = System.Text.Encoding.UTF8.GetBytes(resp);
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        var proxy = new ProxyServer();
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
            var req = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{proxyPort}/v1/chat/completions?stream=true&foo=bar");
            req.Headers.Add("x-api-key", "secret-key-123");
            req.Content = new StringContent("{\"model\":\"x\"}", System.Text.Encoding.UTF8, "application/json");
            var r = await h.SendAsync(req);
            var body = await r.Content.ReadAsStringAsync();
            // 期望: 路径 + query + body 都正确转发；x-api-key 不应被作为 Authorization 转发
            var ok = r.StatusCode == HttpStatusCode.OK
                && body.Contains("/v1/chat/completions")
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
        Console.WriteLine("\n冒烟测试完成。");
        return 0;
    }

    /// <summary>
    /// 重启 proxy（停旧实例 → 用给定配置启动），执行测试 lambda。
    /// test 返回 null = 通过；返回非 null = 失败原因。
    /// </summary>
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
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] {name}: {ex.Message}");
        }
    }
}
