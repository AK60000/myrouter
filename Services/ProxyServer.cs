using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>本次运行（自进程启动起）的转发统计，供陪伴面板播报。字段用 Interlocked 更新。</summary>
    public sealed class ProxyStats
    {
        public long Requests;
        public long Success;
        public long Errors;
        public long Timeouts;
        public long TokensIn;
        public long TokensOut;
    }

    private readonly ProxyStats _stats = new();
    public ProxyStats Stats => _stats;

    private readonly MemoryStore _memory;
    private string? _lastModel;   // 最近一次 /chat 用的模型，LLM 记忆整理复用

    public ProxyServer(string? memoryPath = null, int memoryRefineThreshold = 8)
    {
        _memory = new MemoryStore(memoryPath, memoryRefineThreshold);
        _memory.Refiner = RefineMemoriesAsync;
    }

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

        // Web 界面分流：根路径等本地路径由本机 UI 使用（跳过本地鉴权），其余路径照常走代理
        app.Use(async (ctx, next) =>
        {
            if (IsWebRequest(ctx))
            {
                await HandleWebAsync(ctx);
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

    // ── Web 界面（同端口根路径，本机 UI 使用，跳过本地鉴权） ──

    private static readonly JsonSerializerOptions ChatJsonOptions = new()
    {
        // 转发请求体中文原样输出（默认会转义成 \uXXXX，徒增体积且难调试）
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static bool IsWebRequest(HttpContext ctx)
    {
        var p = ctx.Request.Path;
        return HttpMethods.IsGet(ctx.Request.Method)
            ? p.Equals("/") || p.Equals("/models") || p.Equals("/logo.png")
            : HttpMethods.IsPost(ctx.Request.Method) && p.Equals("/chat");
    }

    private async Task HandleWebAsync(HttpContext ctx)
    {
        if (ctx.Request.Path.Equals("/chat"))
        {
            await HandleChatAsync(ctx);
            return;
        }
        if (ctx.Request.Path.Equals("/models"))
        {
            await HandleModelsAsync(ctx);
            return;
        }
        if (ctx.Request.Path.Equals("/logo.png"))
        {
            await HandleLogoAsync(ctx);
            return;
        }

        ctx.Response.ContentType = "text/html; charset=utf-8";
        var html = LoadEmbeddedResource("index.html") is { Length: > 0 } bytes
            ? Encoding.UTF8.GetString(bytes)
            : "<h1>myrouter</h1><p>Web 页面缺失</p>";
        await ctx.Response.WriteAsync(html, ctx.RequestAborted);
    }

    /// <summary>
    /// 模型列表端点：转上游 /v1/models。key 统一用配置的 _upstreamKey
    /// （Web 端不再管理 API Key）。Web 路径免本地鉴权。
    /// </summary>
    private async Task HandleModelsAsync(HttpContext ctx)
    {
        var apiKey = _upstreamKey;

        var url = BuildUpstreamUrl("/v1/models", null);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (apiKey is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        timeoutCts.CancelAfter(_upstreamTimeout);
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            var body = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
            if (resp.IsSuccessStatusCode)
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.OK;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync(body, ctx.RequestAborted);
            }
            else
            {
                await WriteChatError(ctx, (int)resp.StatusCode, body);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ctx.RequestAborted.IsCancellationRequested)
        {
            await WriteChatError(ctx, (int)HttpStatusCode.GatewayTimeout, "上游模型列表请求超时");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[models-err] {ex.Message}");
            await WriteChatError(ctx, (int)HttpStatusCode.BadGateway, ex.Message);
        }
    }

    /// <summary>把内嵌 myrouter.ico 转 PNG 输出，保证页面 logo 与应用图标完全一致。</summary>
    private async Task HandleLogoAsync(HttpContext ctx)
    {
        var ico = LoadEmbeddedResource("myrouter.ico");
        if (ico is null)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }
        byte[] png;
        using (var ms = new MemoryStream(ico))
        using (var icon = new Icon(ms))
        using (var bmp = icon.ToBitmap())
        using (var outMs = new MemoryStream())
        {
            bmp.Save(outMs, System.Drawing.Imaging.ImageFormat.Png);
            png = outMs.ToArray();
        }
        ctx.Response.ContentType = "image/png";
        ctx.Response.Headers["Cache-Control"] = "public, max-age=86400";
        await ctx.Response.Body.WriteAsync(png, ctx.RequestAborted);
    }

    /// <summary>
    /// Web 聊天端点：转发到上游 /v1/chat/completions，SSE 流式回传浏览器。
    /// 请求体 { model?, messages }；stream 固定 true，messages 原样透传（含 base64 多模态附件）。
    /// 上游 key 统一用 GUI 配置的 _upstreamKey，Web 端不再管理 API Key。
    /// </summary>
    private async Task HandleChatAsync(HttpContext ctx)
    {
        string bodyText;
        using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8))
            bodyText = await reader.ReadToEndAsync(ctx.RequestAborted);

        JsonObject payload;
        try
        {
            payload = (JsonNode.Parse(bodyText) as JsonObject)!;
            if (payload is null || !payload.ContainsKey("messages"))
            {
                await WriteChatError(ctx, 400, "请求体需要 messages 字段");
                return;
            }
        }
        catch (JsonException)
        {
            await WriteChatError(ctx, 400, "请求体不是合法 JSON");
            return;
        }

        // stream 固定 true（前端按 SSE 解析）；apiKey 是旧的 Web 管理 key 字段，一律忽略
        payload.Remove("apiKey");
        payload["stream"] = true;

        // 长期记忆：采集短用户消息 → 自行整理 → 注入活跃记忆为 system 上下文
        CollectAndInjectMemory(payload, DateTime.Now);

        var json = payload.ToJsonString(ChatJsonOptions);
        TrackStart(json.Length);
        using var req = BuildUpstreamChatRequest(json);

        if (_logRequests)
            Log?.Invoke($"[chat-->] {req.RequestUri}");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        timeoutCts.CancelAfter(_upstreamTimeout);
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
                TrackEnd(resp.StatusCode, 0);
                await WriteChatError(ctx, (int)resp.StatusCode, errBody);
                return;
            }

            ctx.Response.StatusCode = (int)HttpStatusCode.OK;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            await resp.Content.CopyToAsync(ctx.Response.Body, timeoutCts.Token);
            TrackEnd(resp.StatusCode, resp.Content.Headers.ContentLength ?? 0);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ctx.RequestAborted.IsCancellationRequested)
        {
            // 上游超时：响应可能已开始写 SSE，只能追加错误事件收尾
            Interlocked.Increment(ref _stats.Timeouts);
            await WriteChatStreamError(ctx, "上游请求超时");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[chat-err] {ex.Message}");
            await WriteChatStreamError(ctx, ex.Message);
        }
    }

    private static async Task WriteChatError(HttpContext ctx, int status, string message)
    {
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = message }, ChatJsonOptions), ctx.RequestAborted);
        }
    }

    /// <summary>构造转发上游 /v1/chat/completions 的 POST 请求（统一 key；json 为已序列化的请求体）。</summary>
    private HttpRequestMessage BuildUpstreamChatRequest(string json)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, BuildUpstreamUrl("/v1/chat/completions", null))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (_upstreamKey is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _upstreamKey);
        return req;
    }

    /// <summary>
    /// 长期记忆三步：记录最近模型 → 采集最后一条短用户消息（3-80 字符）→ 注入活跃记忆
    /// 为 system 上下文（跨会话记忆，AI 据此记得用户信息）。
    /// </summary>
    private void CollectAndInjectMemory(JsonObject payload, DateTime now)
    {
        // 记录最近使用的模型：LLM 记忆整理复用它
        if (payload.TryGetPropertyValue("model", out var mv) && mv is JsonValue mvv &&
            mvv.TryGetValue<string>(out var mstr) && !string.IsNullOrWhiteSpace(mstr))
            _lastModel = mstr;

        if (payload["messages"] is not JsonArray arr || arr.Count == 0) return;

        for (var i = arr.Count - 1; i >= 0; i--)
        {
            var msg = arr[i];
            if (msg?["role"]?.GetValue<string>() != "user") continue;
            if (msg["content"] is JsonValue v && v.TryGetValue<string>(out var s) &&
                s.Length is >= 3 and <= 80)
                _memory.Add(s, _lastModel);
            break; // 只取最后一条用户消息
        }

        var mems = _memory.Top();
        if (mems.Count == 0) return;
        // 注入位置：紧跟用户自己的 system 提示词之后（用户提示词优先，记忆补充其后）
        var insertAt = 0;
        while (insertAt < arr.Count && arr[insertAt]?["role"]?.GetValue<string>() == "system")
            insertAt++;
        arr.Insert(insertAt, new JsonObject
        {
            ["role"] = "system",
            ["content"] = "用户长期记忆（自动整理）：\n- " + string.Join("\n- ", mems),
        });
    }

    /// <summary>
    /// LLM 记忆整理器：把现有记忆 + 待整理片段发给上游，要求输出整理后的记忆 JSON。
    /// 非流式、短超时；失败返回 null（调用方静默保留原记忆）。
    /// </summary>
    private async Task<List<string>?> RefineMemoriesAsync(
        List<string> existing, List<string> pending, string? model, CancellationToken ct)
    {
        const string sys = "你是用户长期记忆整理器。合并现有记忆与新对话片段，输出精简后的记忆列表。\n" +
                           "只保留稳定事实（偏好/身份/习惯/环境）；丢弃寒暄、一次性指令、无信息量内容；\n" +
                           "语义重复的合并为一条；改写为简洁陈述句，每条≤80字；总数≤50条。\n" +
                           "仅输出 JSON：{\"memories\":[\"...\", ...]}，不要任何其他文字。";
        var user = "现有记忆：\n" +
                   (existing.Count == 0 ? "（无）" : string.Join("\n", existing.Select(m => "- " + m))) +
                   "\n\n新对话片段：\n" +
                   string.Join("\n", pending.Select(m => "- " + m));

        var payload = new JsonObject
        {
            ["stream"] = false,
            ["x-memory-refine"] = true,   // 标记整理请求（上游一般忽略未知字段；mock 据此识别）
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = sys },
                new JsonObject { ["role"] = "user", ["content"] = user }),
        };
        if (!string.IsNullOrWhiteSpace(model)) payload["model"] = model;

        using var req = BuildUpstreamChatRequest(payload.ToJsonString(ChatJsonOptions));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement
                .GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content)) return null;
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            var parsed = JsonNode.Parse(content[start..(end + 1)]) as JsonObject;
            var memories = parsed?["memories"] as JsonArray;
            if (memories is null) return null;
            var list = memories
                .Where(n => n is not null)
                .Select(n => n!.GetValue<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Where(s => s.Length <= 80)
                .ToList();
            return list.Count > 0 ? list : null;
        }
        catch
        {
            return null;   // 整理失败由调用方静默处理
        }
    }

    private static async Task WriteChatStreamError(HttpContext ctx, string message)
    {
        if (!ctx.Response.HasStarted)
        {
            await WriteChatError(ctx, (int)HttpStatusCode.BadGateway, message);
            return;
        }
        // SSE 中追加 [DONE] 前先发 error 事件，前端据此中断渲染并展示
        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { error = message }, ChatJsonOptions)}\n\n", ctx.RequestAborted);
    }

    /// <summary>从程序集嵌入资源按文件名后缀读取，返回原始字节（ico/html 等）。</summary>
    private static byte[]? LoadEmbeddedResource(string suffix)
    {
        var asm = typeof(ProxyServer).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;
        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private async Task ForwardAsync(HttpContext ctx, CancellationToken token)
    {
        var path = ctx.Request.Path.Value ?? "";
        var qs = ctx.Request.QueryString.Value ?? "";
        var url = BuildUpstreamUrl(path, qs);

        TrackStart(ctx.Request.ContentLength ?? 0);

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

            TrackEnd(resp.StatusCode, resp.Content.Headers.ContentLength ?? 0);

            if (_logRequests)
                Log?.Invoke($"[<--] {ctx.Request.Method} {url} {resp.StatusCode}");

            await resp.Content.CopyToAsync(ctx.Response.Body, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
        {
            // 上游超时（区别于客户端断开）→ 转 TimeoutException，由外层返回 504
            Interlocked.Increment(ref _stats.Timeouts);
            throw new TimeoutException("上游请求超时");
        }
    }

    /// <summary>转发统计：请求计数 + 输入 token 粗估（按字节/3，非精确值）</summary>
    private void TrackStart(long bodyLength)
    {
        Interlocked.Increment(ref _stats.Requests);
        if (bodyLength > 0) Interlocked.Add(ref _stats.TokensIn, bodyLength / 3);
    }

    /// <summary>转发统计：结果归类 + 输出 token 粗估（按字节/3，非精确值）</summary>
    private void TrackEnd(HttpStatusCode status, long outLength)
    {
        var code = (int)status;
        if (code is >= 200 and < 300) Interlocked.Increment(ref _stats.Success);
        else if (code >= 500) Interlocked.Increment(ref _stats.Errors);
        if (outLength > 0) Interlocked.Add(ref _stats.TokensOut, outLength / 3);
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
