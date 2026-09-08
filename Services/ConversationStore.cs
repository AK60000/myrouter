using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace myrouter.Services;

/// <summary>
/// 会话存储：Web 聊天的多会话历史，持久化到 .myrouter/conversations.json。
/// 每个会话含 id/标题/消息列表/时间戳；上限 100 个，超出删最旧。
/// 前端管理当前会话（active），后端只存列表。
/// </summary>
public class ConversationStore
{
    public sealed class Conversation
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public List<JsonObject> Messages { get; set; } = new();
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
    }

    /// <summary>列表项元数据（不含消息体，供侧栏渲染）。</summary>
    public sealed class ConvMeta
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
    }

    private class Data
    {
        public List<Conversation> Conversations { get; set; } = new();
    }

    public const int MaxConversations = 100;
    private const int TitleMaxChars = 20;   // 与标题总结指令的一致上限（LLM 只产 ≤20 字，超了这里兜底截断）

    private readonly string _path;
    private readonly object _lock = new();
    private Data _data = new();

    public ConversationStore(string? path = null)
    {
        _path = path ?? AppPaths.ConversationsFile;
        Load();
    }

    /// <summary>全部会话元数据，按最近更新降序。</summary>
    public List<ConvMeta> List()
    {
        lock (_lock)
        {
            return _data.Conversations
                .OrderByDescending(c => c.UpdatedAt)
                .Select(c => new ConvMeta
                {
                    Id = c.Id,
                    Title = c.Title,
                    UpdatedAt = c.UpdatedAt,
                })
                .ToList();
        }
    }

    /// <summary>创建空会话（超上限时删最旧），返回新会话。</summary>
    public Conversation Create()
    {
        lock (_lock)
        {
            while (_data.Conversations.Count >= MaxConversations)
                _data.Conversations.Remove(_data.Conversations.OrderBy(c => c.UpdatedAt).First());
            var conv = new Conversation
            {
                Id = "conv-" + Guid.NewGuid().ToString("N")[..10],
                CreatedAt = DateTime.Now.ToString("s"),
                UpdatedAt = DateTime.Now.ToString("s"),
            };
            _data.Conversations.Add(conv);
            Save();
            return Clone(conv);
        }
    }

    public Conversation? Get(string id)
    {
        lock (_lock)
        {
            var c = _data.Conversations.FirstOrDefault(x => x.Id == id);
            return c is null ? null : Clone(c);
        }
    }

    /// <summary>保存会话（标题/消息可分别更新）。标题为空时自动取第一条用户消息前 20 字。
    /// 返回保存后的最终标题；会话不存在返回 null。</summary>
    public string? Save(string id, string? title, List<JsonObject>? messages)
    {
        lock (_lock)
        {
            var c = _data.Conversations.FirstOrDefault(x => x.Id == id);
            if (c is null) return null;
            if (messages is not null) c.Messages = messages;
            if (!string.IsNullOrWhiteSpace(title)) c.Title = title.Trim();
            if (string.IsNullOrWhiteSpace(c.Title) && c.Messages.Count > 0)
                c.Title = ExtractTitle(c.Messages);
            c.UpdatedAt = DateTime.Now.ToString("s");
            Save();
            return c.Title;
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var removed = _data.Conversations.RemoveAll(x => x.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    /// <summary>OpenAI 消息 content 字段转纯文本（字符串，或 [{type:text,…}] 数组取 text 拼接；其他形状返回空）。</summary>
    public static string ContentToText(JsonNode? content) => content switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray arr => string.Join(" ", arr.Select(p => p?["text"]?.GetValue<string>() ?? "")),
        _ => "",
    };

    private static string ExtractTitle(List<JsonObject> messages)
    {
        foreach (var m in messages)
        {
            if (m?["role"]?.GetValue<string>() != "user") continue;
            var text = ContentToText(m["content"]).Trim();
            if (text.Length > 0) return text.Length <= TitleMaxChars ? text : text[..TitleMaxChars];
        }
        return "";
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOpts.Pretty));
        }
        catch { /* 会话写失败不影响主流程 */ }
    }

    private void Load()
    {
        try
        {
            // 单条损坏不应连累全部会话：解析 Conversations 数组，逐条 Deserialize<Conversation>，
            // 失败的条目跳过。失败仍写满文件（Save 用 Pretty 重新覆盖），数据规模由 MaxConversations 控制。
            // 外层 catch 覆盖 FileNotFoundException / DirectoryNotFoundException / JSON 整体损坏，
            // 不需要先 File.Exists —— TOCTOU 且多一次 syscall。
            var raw = File.ReadAllText(_path);
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("Conversations", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            var loaded = new Data();
            foreach (var el in arr.EnumerateArray())
            {
                try
                {
                    var conv = el.Deserialize<Conversation>();
                    if (conv != null && !string.IsNullOrEmpty(conv.Id))
                        loaded.Conversations.Add(conv);
                }
                catch { /* 单条损坏跳过，其余继续 */ }
            }
            _data = loaded;
        }
        catch { _data = new Data(); }
    }

    private static Conversation Clone(Conversation c) => new()
    {
        Id = c.Id,
        Title = c.Title,
        Messages = c.Messages.Select(m => (JsonObject)m.DeepClone()).ToList(),
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt,
    };
}