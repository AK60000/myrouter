using System;
using System.IO;
using System.Text.Json;

namespace myrouter.Models;

public class AppConfig
{
    public const int MinPort = 1;
    public const int MaxPort = 65535;
    public const string DefaultUpstreamUrl = "https://api.openai.com";
    public const int DefaultPort = 8080;
    public const string XApiKeyHeader = "x-api-key";

    public string UpstreamUrl { get; set; } = DefaultUpstreamUrl;
    public string UpstreamApiKey { get; set; } = "";
    public int Port { get; set; } = DefaultPort;
    public string ApiKey { get; set; } = "";
    public bool RequireAuth { get; set; } = true;
    public bool LogRequests { get; set; } = false;

    private static string ConfigPath => Path.Combine(
        AppContext.BaseDirectory, "myrouter.config.json");

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new AppConfig();
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch { /* ignore corrupt config */ }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            throw new IOException("保存配置失败: " + ex.Message, ex);
        }
    }
}
