using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace myrouter.Services;

/// <summary>
/// 聊天长期记忆（Qwen 记忆系统的简化版，无 UI、纯自动）：
/// - 采集：/chat 里的短用户消息（3-80 字）进 pending 队列
/// - 整理：pending 攒够阈值 → 异步调上游 LLM 提炼（合并语义重复、删无关、改写为事实句），
///   结果写入 memories；整理失败静默保留等下次（规则只做字符串级去重兜底）
/// - 注入：memories 按序取前几条作为 system 消息注入下次请求
/// 数据存 memory.json（与配置同目录，含聊天片段，勿提交 git）
/// </summary>
public class MemoryStore
{
    private sealed class Data
    {
        public List<string> Memories { get; set; } = new();   // LLM 整理后的正式记忆
        public List<string> Pending { get; set; } = new();    // 待整理的原始片段
    }

    private const int MinTextLength = 3;
    private const int MaxTextLength = 80;
    private const int MaxMemories = 50;
    private const int InjectTop = 5;
    private const int InjectMaxChars = 800;

    /// <summary>LLM 整理器：输入（现有记忆, 待整理片段, 模型）→ 输出整理后的记忆；null = 失败。</summary>
    public Func<List<string>, List<string>, string?, CancellationToken, Task<List<string>?>>? Refiner { get; set; }

    private readonly string _path;
    private readonly int _refineThreshold;
    private readonly object _lock = new();
    private Data _data = new();
    private bool _refining;

    public MemoryStore(string? path = null, int refineThreshold = 8)
    {
        _path = path ?? AppPaths.MemoryFile;
        _refineThreshold = Math.Max(2, refineThreshold);
        Load();
    }

    /// <summary>采集：短用户消息进 pending（字符串级去重），并检查是否触发整理。</summary>
    public void Add(string text, string? model)
    {
        var norm = (text ?? "").Trim();
        if (norm.Length is < MinTextLength or > MaxTextLength) return;
        lock (_lock)
        {
            if (!_data.Pending.Contains(norm) && !_data.Memories.Contains(norm))
            {
                _data.Pending.Add(norm);
                Save();
            }
        }
        MaybeRefine(model);
    }

    /// <summary>待整理片段攒够阈值 → 后台异步调 LLM 整理（不阻塞聊天请求）。</summary>
    public void MaybeRefine(string? model)
    {
        List<string> existing;
        List<string> pending;
        lock (_lock)
        {
            if (_refining || _data.Pending.Count < _refineThreshold) return;
            _refining = true;
            existing = new List<string>(_data.Memories);
            pending = new List<string>(_data.Pending);
        }

        var refiner = Refiner;
        if (refiner is null)
        {
            _refining = false;
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await refiner(existing, pending, model, CancellationToken.None);
                if (result is { Count: > 0 })
                {
                    lock (_lock)
                    {
                        _data.Memories = result.Take(MaxMemories).ToList();
                        _data.Pending.Clear();
                        Save();
                    }
                }
            }
            catch { /* 整理失败静默保留，下次再试 */ }
            finally
            {
                _refining = false;
            }
        });
    }

    /// <summary>取活跃记忆用于注入（按存储顺序取前几条，总量限制）。</summary>
    public List<string> Top(int maxChars = InjectMaxChars)
    {
        lock (_lock)
        {
            var result = new List<string>();
            var used = 0;
            foreach (var m in _data.Memories)
            {
                if (result.Count >= InjectTop) break;
                if (used + m.Length + 2 > maxChars) continue;
                result.Add(m);
                used += m.Length + 2;
            }
            return result;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            var root = doc.RootElement;
            if (root.TryGetProperty("Memories", out var m) && m.ValueKind == JsonValueKind.Array)
                _data.Memories = m.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!).ToList();
            if (root.TryGetProperty("Pending", out var p) && p.ValueKind == JsonValueKind.Array)
                _data.Pending = p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!).ToList();
            else if (root.TryGetProperty("Entries", out var old))   // v1 格式迁移
                _data.Memories = old.EnumerateArray()
                    .Select(x => x.TryGetProperty("Text", out var t) ? t.GetString() : null)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .Select(t => t!).ToList();
        }
        catch
        {
            _data = new Data();
        }
    }

    // 中文原样写盘（默认会转义成 \uXXXX，文件没法直接读）
    private static readonly JsonSerializerOptions SaveJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, SaveJsonOptions));
        }
        catch { /* 记忆写失败不影响主流程 */ }
    }
}