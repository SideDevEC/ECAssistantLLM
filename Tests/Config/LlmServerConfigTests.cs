using ECAssistant.LLM.Config;

namespace ECAssistant.LLM.Tests.Config;

/// <summary>
/// Config validation and JSON Save/Load round-trip tests.
/// </summary>
public class LlmServerConfigTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "eca-llm-config-tests", Guid.NewGuid().ToString("N"));

    public LlmServerConfigTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string TempFile(string name) => Path.Combine(_tempDir, name);

    [Fact]
    public void Load_ValidConfig_Succeeds()
    {
        var path = TempFile("valid.json");
        LlmServerConfig.Save(ValidConfig(), path);

        var (config, error) = LlmServerConfig.TryLoad(path);

        Assert.Null(error);
        Assert.NotNull(config);
        Assert.Equal(8420, config!.Server.Port);
        Assert.Equal(2, config.Models.Count);
    }

    [Fact]
    public void Save_Load_RoundTrip_PreservesValues()
    {
        var original = ValidConfig();
        original.Server.MaxVramMb = 4096;
        original.Server.ShutdownOnLastClient = false;
        original.Inference.Temperature = 0.7f;
        original.Logging.Level = "debug";
        original.Models[1].IsEmbedding = true;

        var path = TempFile("roundtrip.json");
        LlmServerConfig.Save(original, path);
        var (loaded, error) = LlmServerConfig.TryLoad(path);

        Assert.Null(error);
        Assert.NotNull(loaded);
        Assert.Equal(original.Server.Port, loaded!.Server.Port);
        Assert.Equal(original.Server.MaxVramMb, loaded.Server.MaxVramMb);
        Assert.Equal(original.Server.ShutdownOnLastClient, loaded.Server.ShutdownOnLastClient);
        Assert.Equal(original.Inference.Temperature, loaded.Inference.Temperature);
        Assert.Equal(original.Logging.Level, loaded.Logging.Level);
        Assert.Equal(original.Models.Count, loaded.Models.Count);
        for (int i = 0; i < original.Models.Count; i++)
        {
            Assert.Equal(original.Models[i].Id, loaded.Models[i].Id);
            Assert.Equal(original.Models[i].Path, loaded.Models[i].Path);
            Assert.Equal(original.Models[i].ContextSize, loaded.Models[i].ContextSize);
            Assert.Equal(original.Models[i].IsEmbedding, loaded.Models[i].IsEmbedding);
        }
    }

    [Fact]
    public void Load_InvalidPort_IsRejected()
    {
        var config = ValidConfig();
        config.Server.Port = 0;
        var path = TempFile("bad-port.json");
        LlmServerConfig.Save(config, path);

        var (loaded, error) = LlmServerConfig.TryLoad(path);
        Assert.Null(loaded);
        Assert.NotNull(error);
        Assert.Contains("Invalid port", error);
    }

    [Fact]
    public void Load_NoModels_IsRejected()
    {
        var config = ValidConfig();
        config.Models.Clear();
        var path = TempFile("no-models.json");
        LlmServerConfig.Save(config, path);

        var (loaded, error) = LlmServerConfig.TryLoad(path);
        Assert.Null(loaded);
        Assert.NotNull(error);
        Assert.Contains("No models", error);
    }

    [Fact]
    public void Load_DuplicateModelIds_AreRejected()
    {
        var config = ValidConfig();
        config.Models[1].Id = config.Models[0].Id;
        var path = TempFile("dup-ids.json");
        LlmServerConfig.Save(config, path);

        var (loaded, error) = LlmServerConfig.TryLoad(path);
        Assert.Null(loaded);
        Assert.NotNull(error);
        Assert.Contains("Duplicate model IDs", error);
    }

    [Fact]
    public void Load_ModelWithoutId_IsRejected()
    {
        var config = ValidConfig();
        config.Models[0].Id = "";
        var path = TempFile("no-id.json");
        LlmServerConfig.Save(config, path);

        var (loaded, error) = LlmServerConfig.TryLoad(path);
        Assert.Null(loaded);
        Assert.NotNull(error);
    }

    [Fact]
    public void Load_MissingFile_Throws()
    {
        var (loaded, error) = LlmServerConfig.TryLoad(TempFile("does-not-exist.json"));
        Assert.Null(loaded);
        Assert.NotNull(error);
    }

    [Fact]
    public void GenerateDefault_PassesValidation()
    {
        var path = TempFile("default.json");
        LlmServerConfig.Save(LlmServerConfig.GenerateDefault(), path);

        var (loaded, error) = LlmServerConfig.TryLoad(path);
        Assert.Null(error);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Models.Count);
    }

    private static LlmServerConfig ValidConfig() => new()
    {
        Server = new ServerSection { Host = "localhost", Port = 8420, MaxSessions = 4 },
        Models = new List<ModelConfig>
        {
            new() { Id = "main", Path = "models/main.gguf", ContextSize = 4096 },
            new() { Id = "embeddings", Path = "models/embed.gguf", ContextSize = 2048 }
        },
        Inference = new InferenceDefaults(),
        Logging = new LoggingSection()
    };
}
