using System.Text;
using System.Text.Json;
using ECAssistant.LLM;
using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine;
using ECAssistant.LLM.Server;
using Xunit;

namespace ECAssistant.LLM.Tests.Vision;

/// <summary>
/// FULL-SYSTEM vision E2E (Category=E2E, excluded from unit runs):
/// loads a real vision model (main gguf + mmproj projector) into a real
/// ECAssistantLLM server, then answers questions about a real image.
///
/// Skipped when the vision model files are not present, so unit runs stay fast:
///   - env ECA_VISION_MODEL   (main .gguf)      default: ~/ECAssistant/llm/models/Qwen2.5-VL-7B-Instruct-Q4_K_M.gguf
///   - env ECA_VISION_MMPROJ  (mmproj .gguf)    default: ~/ECAssistant/llm/models/mmproj-Qwen2.5-VL-7B-Instruct-f16.gguf
/// </summary>
[Trait("Category","E2E")]
public class VisionInferenceE2ETests
{
    private const string TestImageBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAUAAAADICAIAAAAWZq/8AAACW0lEQVR4nOzVAQnAMADEwN+of8utjsCdh5BzB1T9A7IEDGEChjABQ5iAIUzAECZgCBMwhAkYwgQMYQKGMAFDmIAhTMAQJmAIEzCECRjCBAxhAoYwAUOYgCFMwBAmYAgTMIQJGMIEDGEChjABQ5iAIUzAECZgCBMwhAkYwgQMYQKGMAFDmIAhTMAQJmAIEzCECRjCBAxhAoYwAUOYgCFMwBAmYAgTMIQJGMIEDGEChjABQ5iAIUzAECZgCBMwhAkYwgQMYQKGMAFDmIAhTMAQJmAIEzCECRjCBAxhAoYwAUOYgCFMwBAmYAgTMIQJGMIEDGEChjABQ5iAIUzAECZgCBMwhAkYwgQMYQKGMAFDmIAhTMAQJmAIEzCECRjCBAxhAoYwAUOYgCFMwBAmYAgTMIQJGMIEDGEChjABQ5iAIUzAECZgCBMwhAkYwgQMYQKGMAFDmIAhTMAQJmAIEzCECRjCBAxhAoYwAUOYgCFMwBAmYAgTMIQJGMIEDGEChjABQ5iAIUzAECZgCBMwhAkYwgQMYQKGMAFDmIAhTMAQJmAIEzCECRjCBAxhAoYwAUOYgCFMwBAmYAj7tjugyYEhTMAQJmAIEzCECRjCBAxhAoYwAUOYgCFMwBAmYAgTMIQJGMIEDGEChjABQ5iAIUzAECZgCBMwhAkYwgQMYQKGMAFDmIAhTMAQJmAIEzCECRjCBAxhAoYwAUOYgCFMwBAmYAgTMIQJGMIEDGEChjABQ5iAIUzAECZgCBMwhAkYwgQMYQKGMAFDmIAhTMAQJmAIEzCECRjCBAxhAoawBwAA//829+EwAAAABklEQVQDAAtJA5DFCpqnAAAAAElFTkSuQmCC"; // 320x200 PNG: top half red, bottom half blue

    private static string DefaultModel =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "ECAssistant", "llm", "models", "Qwen2.5-VL-7B-Instruct-Q4_K_M.gguf");

    private static string DefaultMmproj =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "ECAssistant", "llm", "models", "mmproj-Qwen2.5-VL-7B-Instruct-f16.gguf");

    [Fact]
    public async Task Server_AnswersQuestionsAboutRealImage()
    {
        var modelPath = Environment.GetEnvironmentVariable("ECA_VISION_MODEL") ?? DefaultModel;
        var mmprojPath = Environment.GetEnvironmentVariable("ECA_VISION_MMPROJ") ?? DefaultMmproj;
        if (!File.Exists(modelPath) || !File.Exists(mmprojPath))
        {
            // Install the vision model first (installer downloads model + mmproj together)
            return; // xunit has no dynamic skip without Skip attribute pre-2.9; guarded by run scripts
        }

        var port = 8422;
        var logger = new ServerLogger(LogLevel.Warn, Path.Combine(Path.GetTempPath(), "eca-vision-e2e.log"));
        logger.DisableConsole();

        var config = new LlmServerConfig
        {
            Server = new ServerSection { Host = "localhost", Port = port, ShutdownOnLastClient = false },
            Models = new List<ModelConfig>
            {
                new()
                {
                    Id = "main-vision",
                    Path = modelPath,
                    MmprojPath = mmprojPath,
                    GpuLayers = 99,
                    ContextSize = 4096,
                    Threads = -1
                }
            }
        };

        var modelHost = new MultiModelHost(config, logger);
        modelHost.LoadAll(); // E2E: load the vision model eagerly (Program.cs does the same)
        var scheduler = new InferenceScheduler(logger);
        var vramBudget = new VramBudget(config);
        var sessionRegistry = new SessionRegistry(modelHost, scheduler, config, logger);
        using var cts = new CancellationTokenSource();
        var clientManager = new ClientManager(sessionRegistry, config, logger, () => cts.Cancel());
        var server = new LlmHttpServer(config, modelHost, sessionRegistry, scheduler, vramBudget, clientManager, logger, cts);

        var serverTask = Task.Run(() => server.RunAsync(cts.Token));
        try
        {
            // Wait for readiness
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var ready = false;
            for (var i = 0; i < 120 && !ready; i++)
            {
                try
                {
                    using var h = await http.GetAsync($"http://localhost:{port}/eca/health");
                    ready = h.IsSuccessStatusCode;
                }
                catch { /* not up yet */ }
                if (!ready) await Task.Delay(1000);
            }
            Assert.True(ready, "vision server did not become ready in time");

            // Ask about the image (OpenAI image_url format, data URI)
            var body = JsonSerializer.Serialize(new
            {
                model = "main-vision",
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = "The image has two horizontal halves of different colors. Name both colors." },
                            new { type = "image_url", image_url = new { url = "data:image/png;base64," + TestImageBase64 } }
                        }
                    }
                },
                max_tokens = 100
            });

            using var resp = await http.PostAsync($"http://localhost:{port}/v1/chat/completions",
                new StringContent(body, Encoding.UTF8, "application/json"));

            Assert.True(resp.IsSuccessStatusCode, $"vision completion failed: {(int)resp.StatusCode}");
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var answer = doc.RootElement.GetProperty("choices")[0]
                .GetProperty("message").GetProperty("content").GetString() ?? "";

            // The model must identify both colors of the image
            Assert.Contains("red", answer, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("blue", answer, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            cts.Cancel();
            try { await serverTask; } catch { /* shutdown */ }
        }
    }
}
