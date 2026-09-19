using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine;
using ECAssistant.LLM.Engine.Backends;
using ECAssistant.LLM.Models;
using ECAssistant.LLM.Server;
using Xunit;

namespace ECAssistant.LLM.Tests.E2E;

/// <summary>
/// Cross-OS model smoke test — runs our REAL server (in-process LLamaSharp path)
/// against a real GGUF on the CI runner (ubuntu/windows CPU; macOS arm64 Metal when
/// SMOKE_GPU_LAYERS > 0). Gated behind env vars so plain unit/E2E runs are unaffected:
///   SMOKE_MODEL_PATH   — required (absolute path to a local GGUF)
///   SMOKE_MMPROJ_PATH  — optional (enables a vision check)
///   SMOKE_GPU_LAYERS   — default 0 (CPU); 99 on macOS arm64 runners exercises Metal
/// Asserts: model loads, non-stream chat answers correctly, streaming emits deltas.
/// </summary>
public sealed class ModelSmokeE2E
{
    private static string? ModelPath
    {
        get { return Environment.GetEnvironmentVariable("SMOKE_MODEL_PATH"); }
    }
    private static string? MmprojPath
    {
        get
        {
            var p = Environment.GetEnvironmentVariable("SMOKE_MMPROJ_PATH");
            return string.IsNullOrWhiteSpace(p) ? null : p;
        }
    }

    private static int ContextSize
    {
        get { return int.TryParse(Environment.GetEnvironmentVariable("SMOKE_CONTEXT_SIZE"), out var c) && c > 0 ? c : 4096; }
    }

    private static int GpuLayers
    {
        get { return int.TryParse(Environment.GetEnvironmentVariable("SMOKE_GPU_LAYERS"), out var g) ? g : 0; }
    }

    private static LlmServerConfig Config(string modelPath, string? mmproj) => new()
    {
        Server = new ServerSection { Host = "127.0.0.1", Port = FindFreePort(), MaxSessions = 2, ShutdownOnLastClient = false },
        Models = new List<ModelConfig>
        {
            new()
            {
                Id = "smoke",
                Path = modelPath,
                MmprojPath = mmproj,
                GpuLayers = GpuLayers,
                ContextSize = (uint)ContextSize,
                Threads = -1
            }
        }
    };

    private static int FindFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Model_Loads_Chats_Streams()
    {
        var modelPath = ModelPath;
        if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
            return; // not enabled (local unit/E2E runs) — only runs when CI sets the env

        var port = FindFreePort();
        var config = Config(modelPath, MmprojPath);
        config.Server.Port = port;

        var logger = new ServerLogger(LogLevel.Error);
        // Model-path policy: model files must live INSIDE the server root — use the
        // model's own directory as root (matches the real deployment contract).
        var rootDir = Path.GetDirectoryName(Path.GetFullPath(modelPath))!;
        var modelHost = new MultiModelHost(config, logger, rootDir);
        var scheduler = new InferenceScheduler(logger);
        var vramBudget = new VramBudget(config);
        var sessionRegistry = new SessionRegistry(modelHost, scheduler, config, logger, vramBudget);
        var cts = new CancellationTokenSource();
        var clientManager = new ClientManager(sessionRegistry, config, logger, onLastClientDisconnected: null);
        using var server = new LlmHttpServer(config, modelHost, sessionRegistry, scheduler, vramBudget,
            clientManager, logger, cts);

        await modelHost.LoadAllAsync();
        var runTask = server.RunAsync(cts.Token);

        var cid = Guid.NewGuid().ToString("N");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        try
        {
            // Register the client first — inference endpoints require a registered identity
            var regBody = JsonSerializer.Serialize(new { client_name = "model-smoke-e2e", version = "1.0" });
            using var regReq = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/eca/clients")
            {
                Content = new StringContent(regBody, Encoding.UTF8, "application/json")
            };
            using var regResp = await http.SendAsync(regReq);
            regResp.EnsureSuccessStatusCode();
            using var regDoc = JsonDocument.Parse(await regResp.Content.ReadAsStringAsync());
            cid = regDoc.RootElement.GetProperty("client_id").GetString() ?? cid;

            // 1. Non-stream chat with a deterministic answer
            var answer = await ChatAsync(port, cid, "What is 2+2? Answer with just the number.", maxTokens: 120);
            Assert.False(string.IsNullOrWhiteSpace(answer), "chat returned empty content");
            Assert.Contains("4", answer);

            // 2. Streaming — at least one content delta arrives (retry: small CI
            //    runners occasionally emit an empty first generation)
            List<string> deltas = new();
            int dataLines = 0;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                (deltas, dataLines) = await StreamAsync(port, cid, "Say the word banana.", maxTokens: 60);
                if (deltas.Count > 0) break;
            }
            Assert.True(deltas.Count > 0, $"no streamed deltas after 3 attempts (data lines parsed: {dataLines})");
            Assert.Contains("banana", string.Concat(deltas), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            cts.Cancel();
            try { await runTask; } catch { /* cancellation path */ }
            cts.Dispose();
        }
    }

    private static async Task<string> ChatAsync(int port, string clientId, string question, int maxTokens)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var body = JsonSerializer.Serialize(new
        {
            model = "smoke",
            stream = false,
            max_tokens = maxTokens,
            messages = new[] { new { role = "user", content = question } }
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/v1/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Client-Id", clientId);
        using var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private static async Task<(List<string> Deltas, int DataLines)> StreamAsync(int port, string clientId, string question, int maxTokens)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var body = JsonSerializer.Serialize(new
        {
            model = "smoke",
            stream = true,
            max_tokens = maxTokens,
            messages = new[] { new { role = "user", content = question } }
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/v1/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Client-Id", clientId);
        using var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var deltas = new List<string>();
        var dataLineCount = 0;
        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            dataLineCount++;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            using var doc = JsonDocument.Parse(data);
            var choice = doc.RootElement.GetProperty("choices")[0];
            if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content))
            {
                var s = content.GetString();
                if (!string.IsNullOrEmpty(s)) deltas.Add(s);
            }
        }
        return (deltas, dataLineCount);
    }
}
