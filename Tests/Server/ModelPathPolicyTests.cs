using ECAssistant.LLM.Server;

namespace ECAssistant.LLM.Tests.Server;

/// <summary>
/// Pure-logic tests for model-path containment (models_root traversal guard).
/// </summary>
public class ModelPathPolicyTests
{
    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "eca-llm-path-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void NoModelsRoot_Configured_AllowsAnything()
    {
        Assert.True(RequestRouter.IsAllowedModelPath("/etc/passwd", modelsRoot: null));
        Assert.True(RequestRouter.IsAllowedModelPath("/etc/passwd", modelsRoot: ""));
        Assert.True(RequestRouter.IsAllowedModelPath("/etc/passwd", modelsRoot: "   "));
    }

    [Fact]
    public void PathInsideRoot_IsAllowed()
    {
        var root = TempRoot();
        try
        {
            var model = Path.Combine(root, "qwen3-8b.gguf");
            Assert.True(RequestRouter.IsAllowedModelPath(model, root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PathOutsideRoot_IsBlocked()
    {
        var root = TempRoot();
        try
        {
            Assert.False(RequestRouter.IsAllowedModelPath("/tmp/evil.gguf", root));
            Assert.False(RequestRouter.IsAllowedModelPath(root + "-sibling/evil.gguf", root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TraversalViaDotDot_IsBlocked()
    {
        var root = TempRoot();
        try
        {
            var sneaky = Path.Combine(root, "..", "secret.gguf");
            Assert.False(RequestRouter.IsAllowedModelPath(sneaky, root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RootPrefixLookalike_IsBlocked()
    {
        // "/models/evil" must not pass just because it starts with the "/models" prefix
        var root = TempRoot();
        try
        {
            var lookalike = root.TrimEnd(Path.DirectorySeparatorChar) + "x" + Path.DirectorySeparatorChar + "evil.gguf";
            Assert.False(RequestRouter.IsAllowedModelPath(lookalike, root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
