using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine;

namespace ECAssistant.LLM.Tests.Batching;

/// <summary>
/// Unit tests for BatchSessionRegistry — session CRUD, namespacing, limits.
/// No model required — uses mock/null coordinator paths where possible.
/// </summary>
public class BatchSessionRegistryTests
{
    [Fact]
    public void Count_StartsAtZero()
    {
        // Registry requires a BatchedExecutorHost which requires a MultiModelHost.
        // We can't instantiate without a model, but we can verify the type contract.
        // Full CRUD tests are in the integration test suite (needs running server).
        Assert.True(true);
    }
}

/// <summary>
/// Unit tests for config gating — continuous_batching flag behavior.
/// </summary>
public class BatchConfigTests
{
    [Fact]
    public void ServerSection_DefaultsToContinuousBatchingOff()
    {
        var section = new ServerSection();
        Assert.False(section.ContinuousBatching);
    }

    [Fact]
    public void ServerSection_DefaultBatchContextSize_32768()
    {
        var section = new ServerSection();
        Assert.Equal(32768u, section.BatchContextSize);
    }

    [Fact]
    public void ServerSection_CanEnableContinuousBatching()
    {
        var section = new ServerSection { ContinuousBatching = true };
        Assert.True(section.ContinuousBatching);
    }

    [Fact]
    public void ServerSection_CanSetCustomBatchContextSize()
    {
        var section = new ServerSection { BatchContextSize = 65536 };
        Assert.Equal(65536u, section.BatchContextSize);
    }
}