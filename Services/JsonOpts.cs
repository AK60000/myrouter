using System.Text.Encodings.Web;
using System.Text.Json;

namespace myrouter.Services;

/// <summary>
/// 共享 JSON 序列化选项：中文原样输出（默认会转义成 \uXXXX，徒增转发体积且写盘不可读）。
/// </summary>
internal static class JsonOpts
{
    /// <summary>上游转发请求体与磁盘写入。</summary>
    public static readonly JsonSerializerOptions Unsafe = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Web API 响应统一 camelCase（匿名类型属性本就小写，不受影响）。</summary>
    public static readonly JsonSerializerOptions Web = new(Unsafe)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>磁盘写入：缩进 + 中文原样（AppConfig / Companion / ConversationStore 共用）。</summary>
    public static readonly JsonSerializerOptions Pretty = new(Unsafe)
    {
        WriteIndented = true,
    };
}