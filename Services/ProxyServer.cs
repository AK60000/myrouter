using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using myrouter.Models;

namespace myrouter.Services;

public class ProxyServer : IDisposable
{
    private const int StatusClientClosedRequest = 499; // nginx 私有码：客户端提前断开
    // 请求体防御性上限：LLM 请求体通常很小（含 base64 图片也就几 MB），512MB 足够且防内存耗尽
    private const long MaxRequestBodyBytes = 512L * 1024 * 1024;

    // 透明代理：必须原样透传上游响应（含 Content-Encoding/Content-Length），
    // 因此禁掉 HttpClient 的自动解压——自动解压会解掉 gzip 但保留压缩前长度，破坏响应语义。
    // Timeout 用 Infinite：超时按请求配置走 ForwardAsync 里的 linked CTS（HttpClient.Timeout 只能设一次）
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private TimeSpan _upstreamTimeout = TimeSpan.FromSeconds(AppConfig.DefaultUpstreamTimeoutSeconds);

    private WebApplication? _app;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    // StartAsync 时固定的转发配置（ForwardAsync 只依赖这些 + 请求本身）
    private bool _logRequests;
    private string? _upstreamKey;
    private string _origin = "";
    private string[] _upstreamSegments = [];

    public event Action<string>? Log;
    public bool IsRunning => _app is not null;

    public async Task StartAsync(AppConfig cfg)
    {
        lock (_lock)
        {
            if (_app is not null)
                throw new InvalidOperationException("服务已在运行");
            if (string.IsNullOrWhiteSpace(cfg.UpstreamUrl))
                throw new ArgumentException("上游 URL 不能为空");
            if (!Uri.TryCreate(cfg.UpstreamUrl, UriKind.Absolute, out var u) ||
                (u.Scheme != "http" && u.Scheme != "https"))
                throw new ArgumentException("上游 URL 格式不正确（需 http/https）");
            if (cfg.Port < AppConfig.MinPort || cfg.Port > AppConfig.MaxPort)
                throw new ArgumentException($"端口范围应为 {AppConfig.MinPort}-{AppConfig.MaxPort}");
            if (cfg.UpstreamTimeoutSeconds < AppConfig.MinUpstreamTimeoutSeconds ||
                cfg.UpstreamTimeoutSeconds > AppConfig.MaxUpstreamTimeoutSeconds)
                throw new ArgumentException($"上游超时范围应为 {AppConfig.MinUpstreamTimeoutSeconds}-{AppConfig.MaxUpstreamTimeoutSeconds} 秒");
            if (cfg.RequireAuth && string.IsNullOrEmpty(cfg.ApiKey))
                throw new ArgumentException("启用鉴权时必须设置 API Key");
        }

        var upstream = cfg.UpstreamUrl.TrimEnd('/');
        var apiKey = cfg.ApiKey;
        var requireAuth = cfg.RequireAuth;
        _logRequests = cfg.LogRequests;
        _upstreamKey = string.IsNullOrEmpty(cfg.UpstreamApiKey) ? null : cfg.UpstreamApiKey;
        _upstreamTimeout = TimeSpan.FromSeconds(cfg.UpstreamTimeoutSeconds);

        // 上游 origin 与 path 段是启动时固定值，预计算避免每请求重建
        var uri = new Uri(upstream);
        _origin = $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}";
        _upstreamSegments = (uri.AbsolutePath ?? "/")
            .TrimEnd('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        _cts = new CancellationTokenSource();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<KestrelServerOptions>(o =>
        {
            o.ListenLocalhost(cfg.Port);
            o.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
        });

        var app = builder.Build();

        var token = _cts.Token;

        app.Use(async (ctx, next) =>
        {
            foreach (var h in CorsHeaders)
                ctx.Response.Headers[h] = "*";
            ctx.Response.Headers["Access-Control-Max-Age"] = "86400";
            if (HttpMethods.IsOptions(ctx.Request.Method))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.NoContent;
                return;
            }
            await next();
        });

        app.Run(async ctx =>
        {
            if (token.IsCancellationRequested)
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                return;
            }

            if (requireAuth)
            {
                var provided = ExtractLocalKey(ctx);
                if (!ConstantTimeEquals(provided, apiKey))
                {
                    ctx.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    ctx.Response.Headers["WWW-Authenticate"] = "Bearer";
                    await ctx.Response.WriteAsync("Invalid API key");
                    var auth = ctx.Request.Headers.Authorization.FirstOrDefault();
                    var xApiKey = ctx.Request.Headers[AppConfig.XApiKeyHeader].FirstOrDefault();
                    var diag = (auth, xApiKey) switch
                    {
                        ({ Length: > 0 }, _) => $"Authorization 头长度 {auth.Length}",
                        (_, { Length: > 0 }) => $"x-api-key 头长度 {xApiKey.Length}",
                        _ => "两个鉴权头都没有",
                    };
                    Log?.Invoke($"[401] {ctx.Request.Method} {ctx.Request.Path}{ctx.Request.QueryString} - 鉴权失败 ({diag})");
                    return;
                }
            }

            try
            {
                await ForwardAsync(ctx, token);
            }
            catch (TimeoutException)
            {
                // 上游超时（ForwardAsync 里已区分：客户端断开走 OCE→499）
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = (int)HttpStatusCode.GatewayTimeout; // 504
                    await ctx.Response.WriteAsync("Upstream timeout");
                }
            }
            catch (OperationCanceledException)
            {
                // 响应已开始时不能改状态码，否则会抛 InvalidOperationException
                if (!ctx.Response.HasStarted)
                    ctx.Response.StatusCode = StatusClientClosedRequest;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[ERR] {ctx.Request.Method} {ctx.Request.Path} - {ex.Message}");
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = (int)HttpStatusCode.BadGateway;
                    await ctx.Response.WriteAsync($"Upstream error: {ex.Message}");
                }
            }
        });

        try
        {
            await app.StartAsync(token);
            lock (_lock) _app = app;
            Log?.Invoke($"服务已启动: http://localhost:{cfg.Port} -> {upstream}");
        }
        catch
        {
            await app.DisposeAsync();
            _cts?.Dispose();
            _cts = null;
            throw;
        }
    }

    public async Task StopAsync()
    {
        WebApplication? app;
        lock (_lock)
        {
            app = _app;
            _app = null;
        }
        if (app is null) return;
        try
        {
            _cts?.Cancel();
            await app.StopAsync(TimeSpan.FromSeconds(5));
            Log?.Invoke("服务已停止");
        }
        finally
        {
            await app.DisposeAsync();
            _cts?.Dispose();
            _cts = null;
        }
    }

    private static readonly string[] CorsHeaders =
    [
        "Access-Control-Allow-Origin",
        "Access-Control-Allow-Methods",
        "Access-Control-Allow-Headers",
        "Access-Control-Expose-Headers",
    ];

    private async Task ForwardAsync(HttpContext ctx, CancellationToken token)
    {
        var path = ctx.Request.Path.Value ?? "";
        var qs = ctx.Request.QueryString.Value ?? "";
        var url = BuildUpstreamUrl(path, qs);

        using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), url);

        if (HttpMethods.IsPost(ctx.Request.Method) ||
            HttpMethods.IsPut(ctx.Request.Method) ||
            HttpMethods.IsPatch(ctx.Request.Method) ||
            HttpMethods.IsDelete(ctx.Request.Method))
        {
            req.Content = new StreamContent(ctx.Request.Body);
            if (!string.IsNullOrEmpty(ctx.Request.ContentType))
                req.Content.Headers.TryAddWithoutValidation("Content-Type", ctx.Request.ContentType);
            // StreamContent 不知道长度，若不显式设置 Content-Length，
            // HttpClient 会改用 Transfer-Encoding: chunked 发送，部分上游不兼容。
            if (ctx.Request.ContentLength.HasValue)
                req.Content.Headers.ContentLength = ctx.Request.ContentLength.Value;
        }

        // RFC 7230 §6.1：Connection 头声明的 token 是逐跳头，必须剥离；解析一次供整个循环用
        var connectionTokens = ctx.Request.Headers.Connection;

        foreach (var h in ctx.Request.Headers)
        {
            if (IsHopByHop(h.Key, connectionTokens)) continue;
            if (h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            // 配置了上游密钥时，本地鉴权用的 x-api-key 不能透传给上游（会泄露本地 key）
            if (_upstreamKey is not null &&
                h.Key.Equals(AppConfig.XApiKeyHeader, StringComparison.OrdinalIgnoreCase)) continue;
            if (!req.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray()))
            {
                if (req.Content is not null)
                    req.Content.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
            }
        }

        if (_upstreamKey is not null)
        {
            // Authorization 是 typed property，用 TryAdd+Remove 在 body 存在时 .NET 会抛 "Misused header name"。
            // 直接赋值 typed property 最稳：自动处理移除+添加。
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _upstreamKey);
        }

        if (_logRequests)
            Log?.Invoke($"[-->] {ctx.Request.Method} {url}");

        // 每请求独立超时：linked CTS 让客户端断开(token)与超时两种取消可区分
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(_upstreamTimeout);
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

            ctx.Response.StatusCode = (int)resp.StatusCode;

            foreach (var h in resp.Headers)
                ctx.Response.Headers[h.Key] = h.Value.ToArray();
            foreach (var h in resp.Content.Headers)
                ctx.Response.Headers[h.Key] = h.Value.ToArray();
            ctx.Response.Headers.Remove("Transfer-Encoding");

            if (_logRequests)
                Log?.Invoke($"[<--] {ctx.Request.Method} {url} {resp.StatusCode}");

            await resp.Content.CopyToAsync(ctx.Response.Body, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
        {
            // 上游超时（区别于客户端断开）→ 转 TimeoutException，由外层返回 504
            throw new TimeoutException("上游请求超时");
        }
    }

    private static string? ExtractLocalKey(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrEmpty(auth))
        {
            const string bearer = "Bearer ";
            if (auth.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
                return auth.Substring(bearer.Length).Trim();
            return auth.Trim();
        }
        return ctx.Request.Headers[AppConfig.XApiKeyHeader].FirstOrDefault()?.Trim();
    }

    private static bool ConstantTimeEquals(string? a, string b)
    {
        if (string.IsNullOrEmpty(a)) return false;
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private static bool IsHopByHop(string header, StringValues connectionTokens) =>
        header.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) ||
        // RFC 7230 §6.1：Connection 头中声明的 token 也是逐跳头，必须随 Connection 一起剥离
        connectionTokens.Any(t => t is not null && t.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 构造上游 URL，自动处理上游配置 path 与客户端请求 path 的重叠。
    ///
    /// 规则（按 path 段、不区分大小写）：
    /// 1. 上游 path 全部段 == 客户端 path 的前 N 段（上游是客户端前缀）→ 用客户端 path
    ///    例：upstream=/v1, client=/v1/chat/completions → /v1/chat/completions
    /// 2. 上游 path 的后 K 段 == 客户端 path 的前 K 段（典型：版本段重叠）→ upstream + client[K:]
    ///    例：upstream=/api/v1, client=/v1/chat/completions → /api/v1/chat/completions
    /// 3. 无重叠 → upstream + client（保守直拼，不去重）
    /// </summary>
    private string BuildUpstreamUrl(string clientPath, string? clientQuery)
    {
        var clientSegments = (clientPath ?? "/")
            .TrimEnd('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        string[] finalSegments;
        if (SegmentsArePrefix(_upstreamSegments, clientSegments))
        {
            finalSegments = clientSegments;
        }
        else
        {
            var maxK = Math.Min(_upstreamSegments.Length, clientSegments.Length);
            var k = 0;
            while (k < maxK &&
                   clientSegments[k].Equals(_upstreamSegments[_upstreamSegments.Length - 1 - k], StringComparison.OrdinalIgnoreCase))
            {
                k++;
            }
            if (k > 0)
                finalSegments = _upstreamSegments.Concat(clientSegments.Skip(k)).ToArray();
            else
                finalSegments = _upstreamSegments.Concat(clientSegments).ToArray();
        }

        var path = finalSegments.Length == 0 ? "/" : "/" + string.Join("/", finalSegments);
        return _origin + path + (clientQuery ?? "");
    }

    private static bool SegmentsArePrefix(string[] upstream, string[] client)
    {
        if (client.Length < upstream.Length) return false;
        for (var i = 0; i < upstream.Length; i++)
            if (!client[i].Equals(upstream[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
        _http.Dispose();
    }
}
