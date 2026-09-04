using System;
using System.Net.Http;
using System.Threading.Tasks;
using myrouter.Models;
using myrouter.Services;

namespace myrouter.OpenRouterTest;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: dotnet run -- <upstream-key> [local-key] [model]");
            return 1;
        }

        var upstreamKey = args[0];
        var localKey = args.Length > 1 ? args[1] : "1234";
        var model = args.Length > 2 ? args[2] : "openai/gpt-4o-mini";
        const int port = 18997;

        var proxy = new ProxyServer();
        proxy.Log += msg => Console.WriteLine($"[proxy] {msg}");

        try
        {
            await proxy.StartAsync(new AppConfig
            {
                UpstreamUrl = "https://openrouter.ai/api",
                UpstreamApiKey = upstreamKey,
                Port = port,
                RequireAuth = true,
                ApiKey = localKey,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Proxy start failed: {ex.Message}");
            return 1;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        // ── Case A: 错误 local key ──
        Console.WriteLine("\n=== Case A: wrong local key (expect 401) ===");
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}/v1/chat/completions");
            req.Headers.Add("Authorization", "Bearer wrong-key");
            req.Content = new StringContent(
                $$"""{"model":"{{model}}","messages":[{"role":"user","content":"hi"}]}""",
                System.Text.Encoding.UTF8, "application/json");
            var r = await http.SendAsync(req);
            var body = await r.Content.ReadAsStringAsync();
            Console.WriteLine($"Status: {(int)r.StatusCode}");
            Console.WriteLine($"Body: {body}");
        }
        catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); }

        // ── Case B: 正确 local key + chat completion ──
        Console.WriteLine("\n=== Case B: correct local key + real chat completion ===");
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}/v1/chat/completions");
            req.Headers.Add("Authorization", $"Bearer {localKey}");
            req.Content = new StringContent(
                $$"""{"model":"{{model}}","messages":[{"role":"user","content":"Reply with exactly: OK"}],"max_tokens":20}""",
                System.Text.Encoding.UTF8, "application/json");
            Console.WriteLine($"→ POST http://localhost:{port}/v1/chat/completions");
            var r = await http.SendAsync(req);
            var body = await r.Content.ReadAsStringAsync();
            Console.WriteLine($"← Status: {(int)r.StatusCode} {r.ReasonPhrase}");
            Console.WriteLine($"← Body:\n{body}");
        }
        catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); }

        // ── Case C: GET /v1/models ──
        Console.WriteLine("\n=== Case C: GET /v1/models ===");
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{port}/v1/models");
            req.Headers.Add("Authorization", $"Bearer {localKey}");
            var r = await http.SendAsync(req);
            var body = await r.Content.ReadAsStringAsync();
            Console.WriteLine($"Status: {(int)r.StatusCode}");
            var preview = body.Length > 300 ? body.Substring(0, 300) + "..." : body;
            Console.WriteLine($"Body (first 300 chars): {preview}");
        }
        catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); }

        await proxy.StopAsync();
        proxy.Dispose();
        return 0;
    }
}