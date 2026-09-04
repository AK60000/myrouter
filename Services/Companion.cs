using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace myrouter.Services;

/// <summary>
/// 昼夜陪伴：按本地时间生成问候/提醒（含深夜劝睡），结合代理运行统计播报，
/// 每日统计以增量方式持久化到 companion.json（与配置同目录，仅保留近期，自动裁剪）。
/// 只负责"说"内容；触发节奏由 MainForm 的 Timer 控制。
/// </summary>
public class Companion
{
    private sealed class DayRecord
    {
        public long Requests { get; set; }
        public long Tokens { get; set; }
    }

    private sealed class Data
    {
        public bool Muted { get; set; }
        public Dictionary<string, DayRecord> History { get; set; } = new();
    }

    private const int HistoryDays = 60; // 与 LLM 缓存窗口无关，只是历史档期上限

    private readonly ProxyServer _proxy;
    private readonly string _path;
    private Data _data = new();

    // 上次快照的计数基线：差值累计进当天，保证重启/跨天时统计不重复
    private long _lastRequests;
    private long _lastTokens;

    public event Action<string>? Says;

    /// <summary>唯一的说话出口：静音时不触发事件，由 UI 订阅 Says 渲染。</summary>
    public void Speak(string msg)
    {
        if (_data.Muted) return;
        Says?.Invoke(msg);
    }

    public Companion(ProxyServer proxy)
    {
        _proxy = proxy;
        _path = AppPaths.CompanionFile;
        Load();
    }

    public bool Muted => _data.Muted;

    public void SetMuted(bool muted)
    {
        _data.Muted = muted;
        Save();
    }

    /// <summary>把自上次调用以来新增的计数并入当天记录，并裁剪过期档期。</summary>
    public void Snapshot(DateTime now)
    {
        var s = _proxy.Stats;
        var dReq = s.Requests - _lastRequests;
        var dTok = (s.TokensIn + s.TokensOut) - _lastTokens;
        if (dReq > 0)
        {
            var key = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            if (!_data.History.TryGetValue(key, out var day))
            {
                day = new DayRecord();
                _data.History[key] = day;
            }
            day.Requests += dReq;
            day.Tokens += dTok;
            TrimHistory();
            Save();
        }
        _lastRequests = s.Requests;
        _lastTokens = s.TokensIn + s.TokensOut;
    }

    private void TrimHistory()
    {
        var cutoff = DateTime.Today.AddDays(-HistoryDays)
            .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var k in _data.History.Keys
                     .Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList())
            _data.History.Remove(k);
    }

    /// <summary>
    /// 生成一条说话内容。时段问候 +（可选）本次运行统计；深夜只劝睡不播数据。
    /// </summary>
    public string BuildMessage(DateTime now, bool withStats)
    {
        var hour = now.Hour;
        // 深夜：只劝睡，不播统计（简洁克制）
        if (hour is 23 or 0 or 1) return "夜深了，早点休息，别让代理替你熬夜 💤";
        if (hour is >= 2 and < 6) return "凌晨还在折腾？快去睡吧，明天再战 🛌";

        var greeting = hour switch
        {
            >= 6 and < 11 => "早上好 ☀️",
            >= 11 and < 13 => "中午好 🍚",
            >= 13 and < 18 => "下午好",
            _ => "晚上好 🌙",
        };

        var parts = new List<string> { greeting };

        if (withStats)
        {
            var s = _proxy.Stats;
            if (s.Requests > 0)
            {
                parts.Add($"本次运行已处理 {s.Requests} 个请求（约 {s.TokensIn + s.TokensOut} token）");
                if (s.Errors > 0 || s.Timeouts > 0)
                    parts.Add($"其中 {s.Errors + s.Timeouts} 个出问题");
            }
            else if (TryGetCount(DateTime.Today.AddDays(-1), out var yesterday))
            {
                parts.Add($"昨天默默跑了 {yesterday.Requests} 个请求");
            }
        }

        return string.Join("，", parts) + "。";
    }

    private bool TryGetCount(DateTime day, out DayRecord rec)
    {
        var key = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return _data.History.TryGetValue(key, out rec!);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
                _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(_path)) ?? new Data();
        }
        catch
        {
            _data = new Data();
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path,
                JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 记忆文件写失败不影响主流程 */ }
    }
}