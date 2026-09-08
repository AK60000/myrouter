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

    private readonly string _agentProfilePath;
    private readonly ConversationStore _conversations;

    public ProxyServer(string? agentProfilePath = null, string? conversationsPath = null)
    {
        _agentProfilePath = agentProfilePath ?? AppPaths.AgentFile;
        _conversations = new ConversationStore(conversationsPath);
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

    private static bool IsWebRequest(HttpContext ctx)
    {
        var p = ctx.Request.Path;
        if (HttpMethods.IsGet(ctx.Request.Method))
            return p.Equals("/") || p.Equals("/models") || p.Equals("/logo.png") || p.Equals("/agent")
                || p.Equals("/md/vendor.js") || p.Equals("/md/katex.css") || p.StartsWithSegments("/conversations");
        if (HttpMethods.IsPost(ctx.Request.Method))
            return p.Equals("/chat") || p.Equals("/agent") || p.StartsWithSegments("/conversations");
        return HttpMethods.IsDelete(ctx.Request.Method) && p.StartsWithSegments("/conversations");
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
        if (ctx.Request.Path.Equals("/md/vendor.js"))
        {
            await HandleStaticAsync(ctx, ".vendor.js", "text/javascript; charset=utf-8");
            return;
        }
        if (ctx.Request.Path.Equals("/md/katex.css"))
        {
            await HandleStaticAsync(ctx, ".katex.css", "text/css; charset=utf-8");
            return;
        }
        if (ctx.Request.Path.Equals("/agent"))
        {
            await HandleAgentAsync(ctx);
            return;
        }
        if (ctx.Request.Path.StartsWithSegments("/conversations"))
        {
            await HandleConversationsAsync(ctx);
            return;
        }

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(IndexHtml.Value ?? "<h1>myrouter</h1><p>Web 页面缺失</p>", ctx.RequestAborted);
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
        var png = LogoPng.Value;
        if (png is null)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }
        await SendStaticAsync(ctx, png, "image/png");
    }

    /// <summary>提供前端静态资源（第三方依赖脚本 / KaTeX 样式，字体内嵌），网页端零 CDN。</summary>
    private async Task HandleStaticAsync(HttpContext ctx, string suffix, string contentType)
    {
        var bytes = LoadEmbeddedResource(suffix);
        if (bytes is null)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }
        await SendStaticAsync(ctx, bytes, contentType);
    }

    /// <summary>发出带缓存头的静态资源响应（logo 转换结果与嵌入资源共用收尾）。</summary>
    private static async Task SendStaticAsync(HttpContext ctx, byte[] bytes, string contentType)
    {
        ctx.Response.ContentType = contentType;
        ctx.Response.Headers["Cache-Control"] = "public, max-age=86400";
        await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
    }

    /// <summary>
    /// 用户画像端点：GET 读 agent.md；POST 两种动作——
    /// body 带 content 直接保存；带 messages 让上游 LLM 合并现有画像+对话生成新版后写盘。
    /// </summary>
    private async Task HandleAgentAsync(HttpContext ctx)
    {
        if (HttpMethods.IsGet(ctx.Request.Method))
        {
            await WriteJson(ctx, new { content = ReadAgentProfile() });
            return;
        }

        var payload = await ReadBodyJsonAsync(ctx);
        if (payload is null)
        {
            await WriteChatError(ctx, 400, "请求体不是合法 JSON");
            return;
        }

        string? content;
        if (payload["messages"] is JsonArray msgs && msgs.Count > 0)
            content = await GenerateAgentProfileAsync(msgs, payload["model"]?.GetValue<string>());
        else if (payload["content"] is JsonValue cv && cv.TryGetValue<string>(out var s))
            content = s;
        else
        {
            await WriteChatError(ctx, 400, "需要 content（保存）或 messages（AI 生成）");
            return;
        }

        if (content is null)
        {
            await WriteChatError(ctx, (int)HttpStatusCode.BadGateway, "AI 画像生成失败（请检查上游配置与 key）");
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_agentProfilePath)!);
            File.WriteAllText(_agentProfilePath, content);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[agent-err] {ex.Message}");
            await WriteChatError(ctx, 500, "画像写入失败");
            return;
        }

        await WriteJson(ctx, new { content });
    }

    /// <summary>
    /// 会话端点：GET /conversations 列表 / GET /conversations/{id} 详情；
    /// POST /conversations 新建，POST /conversations/{id} 保存（title/messages 可选），
    /// POST /conversations/{id}/title 让 LLM 总结标题；DELETE /conversations/{id} 删除。
    /// 数据走 ConversationStore（.myrouter/conversations.json）。
    /// </summary>
    private async Task HandleConversationsAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        var id = path.StartsWith("/conversations") ? path["/conversations".Length..].Trim('/') : "";
        var isTitle = id.EndsWith("/title");
        if (isTitle) id = id[..^"/title".Length].Trim('/');

        if (HttpMethods.IsGet(ctx.Request.Method))
        {
            if (id.Length == 0)
            {
                await WriteJson(ctx, new { items = _conversations.List() });
                return;
            }
            var conv = _conversations.Get(id);
            if (conv is null)
            {
                await WriteChatError(ctx, 404, "会话不存在");
                return;
            }
            await WriteJson(ctx, new { id = conv.Id, title = conv.Title, messages = conv.Messages });
            return;
        }

        if (HttpMethods.IsDelete(ctx.Request.Method))
        {
            if (id.Length == 0 || !_conversations.Delete(id))
            {
                await WriteChatError(ctx, 404, "会话不存在");
                return;
            }
            await WriteJson(ctx, new { ok = true });
            return;
        }

        if (HttpMethods.IsPost(ctx.Request.Method))
        {
            var payload = await ReadBodyJsonAsync(ctx);
            if (id.Length == 0)
            {
                if (payload is not null)
                {
                    await WriteChatError(ctx, 400, "新建会话不需要请求体");
                    return;
                }
                await WriteJson(ctx, new { id = _conversations.Create().Id });
                return;
            }
            if (isTitle)
            {
                await SummarizeTitleAsync(ctx, id, payload);
                return;
            }
            if (payload is null)
            {
                await WriteChatError(ctx, 400, "请求体不是合法 JSON");
                return;
            }
            List<JsonObject>? messages = null;
            if (payload["messages"] is JsonArray msgs)
                messages = msgs.Select(m => (m as JsonObject) ?? new JsonObject()).ToList();
            string? title = payload["title"] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : null;
            var savedTitle = _conversations.Save(id, title, messages);
            if (savedTitle is null)
            {
                await WriteChatError(ctx, 404, "会话不存在");
                return;
            }
            await WriteJson(ctx, new { ok = true, id, title = savedTitle });
            return;
        }

        await WriteChatError(ctx, 405, "不支持的请求方法");
    }

    /// <summary>让 LLM 根据对话总结标题并保存，覆盖自动截取的临时标题。</summary>
    private async Task SummarizeTitleAsync(HttpContext ctx, string id, JsonObject? payload)
    {
        if (payload?["messages"] is not JsonArray msgs || msgs.Count == 0)
        {
            await WriteChatError(ctx, 400, "需要 messages（标题总结）");
            return;
        }
        var model = payload?["model"]?.GetValue<string>();
        var title = await AskUpstreamAsync(
            "为这段对话生成标题，不超过 20 字，不加标点与引号，直接输出标题。",
            MessagesToText(msgs), "x-title-refine", model);
        if (title is not null) title = StripThinking(title);   // 推理模型输出可能带思维链块，剥掉再取标题
        if (string.IsNullOrWhiteSpace(title))
        {
            await WriteChatError(ctx, (int)HttpStatusCode.BadGateway, "标题生成失败（请检查上游配置与 key）");
            return;
        }
        title = title.Trim().Trim('"', '\'', '「', '」', '《', '》', '“', '”', '，', '。');
        if (_conversations.Save(id, title, null) is null)
        {
            await WriteChatError(ctx, 404, "会话不存在");
            return;
        }
        await WriteJson(ctx, new { ok = true, title });
    }

    private static async Task WriteJson(HttpContext ctx, object obj)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(obj, JsonOpts.Web), ctx.RequestAborted);
    }

    /// <summary>让上游 LLM 合并现有画像与最新对话，输出新版 agent.md；失败返回 null。</summary>
    private async Task<string?> GenerateAgentProfileAsync(JsonArray messages, string? model)
    {
        const string sys = "你是用户画像维护器。合并现有画像与最新对话，输出新版 agent.md。\n" +
                           "只保留稳定事实（身份/偏好/习惯/环境）；丢弃寒暄与一次性指令；\n" +
                           "语义重复合并为一条陈述句；简练 markdown，≤30 行。仅输出画像内容，不要解释。";
        var existing = ReadAgentProfile();
        var user = "现有画像：\n" + (string.IsNullOrWhiteSpace(existing) ? "（无）" : existing) +
                   "\n\n最新对话：\n" + MessagesToText(messages);
        var text = await AskUpstreamAsync(sys, user, "x-agent-refine", model);
        if (string.IsNullOrWhiteSpace(text)) return null;
        // 输出侧同样剥离思维链：推理模型可能把思考写进 content（<thinking>/thinking…response），
        // 不剥则整个思维链被写进 agent.md，之后每次聊天都作为 system 注入，画像被污染
        text = StripThinking(text);
        return string.IsNullOrWhiteSpace(text) ? null : text;   // 只剩思维链 → 视为生成失败，不覆盖现有画像
    }

    /// <summary>调用上游非流式补全，返回 assistant 正文；失败返回 null。</summary>
    private async Task<string?> AskUpstreamAsync(string system, string user, string marker, string? model)
    {
        var payload = new JsonObject
        {
            ["stream"] = false,
            [marker] = true,   // 标记专用请求（上游一般忽略未知字段；mock 据此识别）
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
        };
        if (!string.IsNullOrWhiteSpace(model)) payload["model"] = model;   // 真实上游缺 model 会 400
        using var req = BuildUpstreamChatRequest(payload.ToJsonString(JsonOpts.Unsafe));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        cts.CancelAfter(_upstreamTimeout);   // 跟随 GUI 配置的上游超时（慢模型下固定 30s 会误杀）
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement.GetProperty("choices")[0]
                .GetProperty("message").GetProperty("content").GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch { return null; }   // 失败由调用方决定如何处理
    }

    private static string MessagesToText(JsonArray messages) =>
        string.Join("\n", messages.Select(m =>
        {
            var role = m?["role"]?.GetValue<string>() ?? "?";
            // 剥离思维链后入提炼输入：历史里可能混入早期版本未剔除的 <thinking>/thinking 块
            return $"[{role}] {StripThinking(ConversationStore.ContentToText(m?["content"]))}";
        }));

    /// <summary>剥离思维链标签：尖括号块与前端 splitThinking 同语义（&lt;thinking&gt;/&lt;think&gt;，大小写不敏感、多块、未闭合即剩余全剥）；
    /// 另兜底剥离早期版本混入历史的裸 thinking…response 块——必须闭合到 response 且带词边界，正文里孤立的 "thinking" 单词不会被误删。</summary>
    private static string StripThinking(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = StripTaggedThinking(text);
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"\bthinking[\s\S]*?\bresponse\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return text.Trim();
    }

    /// <summary>剥 &lt;thinking&gt;…&lt;/thinking&gt; 与 &lt;think&gt;…&lt;/think&gt; 块（与前端 splitThinking 同语义：取最早开标签、
    /// 对应最早闭标签；未闭合则剩余全部视为思维链丢弃）。</summary>
    private static string StripTaggedThinking(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lower = text.ToLowerInvariant();
        var i = 0;
        for (var guard = 0; guard < 16; guard++)   // 与前端一致的块数上限
        {
            var openA = lower.IndexOf("<thinking>", i, StringComparison.Ordinal);
            var openB = lower.IndexOf("<think>", i, StringComparison.Ordinal);
            var open = openA == -1 ? openB : openB == -1 ? openA : Math.Min(openA, openB);
            if (open == -1) { sb.Append(text, i, text.Length - i); break; }
            var openLen = open == openA ? "<thinking>".Length : "<think>".Length;
            sb.Append(text, i, open - i);

            var closeA = lower.IndexOf("</thinking>", open + openLen, StringComparison.Ordinal);
            var closeB = lower.IndexOf("</think>", open + openLen, StringComparison.Ordinal);
            var close = closeA == -1 ? closeB : closeB == -1 ? closeA : Math.Min(closeA, closeB);
            if (close == -1) break;   // 未闭合：剩余视为进行中的思维链，丢弃
            i = close + (close == closeA ? "</thinking>".Length : "</think>".Length);
        }
        return sb.ToString();
    }

    private static async Task<JsonObject?> ReadBodyJsonAsync(HttpContext ctx)
    {
        string bodyText;
        using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8))
            bodyText = await reader.ReadToEndAsync(ctx.RequestAborted);
        try { return JsonNode.Parse(bodyText) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private string ReadAgentProfile()
    {
        try
        {
            return File.Exists(_agentProfilePath) ? File.ReadAllText(_agentProfilePath) : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// Web 聊天端点：转发到上游 /v1/chat/completions，SSE 流式回传浏览器。
    /// 请求体 { model?, messages }；stream 固定 true，messages 原样透传（含 base64 多模态附件）。
    /// 上游 key 统一用 GUI 配置的 _upstreamKey，Web 端不再管理 API Key。
    /// </summary>
    private async Task HandleChatAsync(HttpContext ctx)
    {
        var payload = await ReadBodyJsonAsync(ctx);
        if (payload is null || !payload.ContainsKey("messages"))
        {
            await WriteChatError(ctx, 400, payload is null ? "请求体不是合法 JSON" : "请求体需要 messages 字段");
            return;
        }

        // stream 固定 true（前端按 SSE 解析）；apiKey 是旧的 Web 管理 key 字段，一律忽略
        payload.Remove("apiKey");
        payload["stream"] = true;

        // 用户画像：agent.md 存在则作为 system 消息注入（手写维护，AI 借此跨会话记得用户）
        InjectAgentProfile(payload);
        // 代码运行能力由前端 tools 声明（run_javascript）承担，此处无需额外注入

        var json = payload.ToJsonString(JsonOpts.Unsafe);
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
            // SSE 流式复制不受 _upstreamTimeout 总时长限制（长思考/长输出会话不能被整段掐断）：
            // 首字节仍由上面的 SendAsync 超时兜底，这里改为每个数据块之间的空闲超时——
            // 上游停顿超过 _upstreamTimeout 才判定超时
            await using var upstream = await resp.Content.ReadAsStreamAsync(timeoutCts.Token);
            var buffer = new byte[81920];
            while (true)
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                idleCts.CancelAfter(_upstreamTimeout);
                int read;
                try
                {
                    read = await upstream.ReadAsync(buffer, idleCts.Token);
                }
                catch (OperationCanceledException) when (idleCts.IsCancellationRequested && !ctx.RequestAborted.IsCancellationRequested)
                {
                    // 流式中途空闲超时：追加错误事件收尾（与整体超时同一出口）
                    Interlocked.Increment(ref _stats.Timeouts);
                    await WriteChatStreamError(ctx, "上游请求超时");
                    break;
                }
                if (read == 0) break;
                await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
            }
            TrackEnd(resp.StatusCode, 0);
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
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = status;
        await WriteJson(ctx, new { error = message });   // error 属性本就小写，camelCase 策略不影响
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

    /// <summary>注入用户画像：agent.md 存在则插入到现有 system 消息之后（用户提示词优先，画像补充其后）。</summary>
    private void InjectAgentProfile(JsonObject payload)
    {
        if (payload["messages"] is not JsonArray arr || arr.Count == 0) return;
        // 存量画像可能混入早期版本未剥离的思维链，注入前兜底剥一次，避免垃圾随每次聊天进入上下文
        var profile = StripThinking(ReadAgentProfile());
        if (profile.Length == 0) return;

        var insertAt = 0;
        while (insertAt < arr.Count && arr[insertAt]?["role"]?.GetValue<string>() == "system")
            insertAt++;
        arr.Insert(insertAt, new JsonObject
        {
            ["role"] = "system",
            ["content"] = profile,
        });
    }

    private static async Task WriteChatStreamError(HttpContext ctx, string message)
    {
        if (!ctx.Response.HasStarted)
        {
            await WriteChatError(ctx, (int)HttpStatusCode.BadGateway, message);
            return;
        }
        // SSE 中追加 [DONE] 前先发 error 事件，前端据此中断渲染并展示
        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { error = message }, JsonOpts.Unsafe)}\n\n", ctx.RequestAborted);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]?> EmbeddedCache = new();

    /// <summary>从程序集嵌入资源按文件名后缀读取（进程内缓存：静态资源每次请求都重新枚举/拷贝是浪费）。</summary>
    private static byte[]? LoadEmbeddedResource(string suffix) => EmbeddedCache.GetOrAdd(suffix, s =>
    {
        var asm = typeof(ProxyServer).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(s, StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;
        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    });

    /// <summary>首页 HTML（解码后缓存，不再每次请求重复 GetString）。</summary>
    private static readonly Lazy<string?> IndexHtml = new(() =>
        LoadEmbeddedResource("index.html") is { Length: > 0 } bytes ? Encoding.UTF8.GetString(bytes) : null);

    /// <summary>应用图标 → PNG（转换结果缓存，避免每次请求走 GDI+ 位图转换）。</summary>
    private static readonly Lazy<byte[]?> LogoPng = new(() =>
    {
        var ico = LoadEmbeddedResource("myrouter.ico");
        if (ico is null) return null;
        using var ms = new MemoryStream(ico);
        using var icon = new Icon(ms);
        using var bmp = icon.ToBitmap();
        using var outMs = new MemoryStream();
        bmp.Save(outMs, System.Drawing.Imaging.ImageFormat.Png);
        return outMs.ToArray();
    });

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
