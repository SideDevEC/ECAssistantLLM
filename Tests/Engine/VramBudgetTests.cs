using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine;

namespace ECAssistant.LLM.Tests.Engine;

/// <summary>
/// Pure-logic tests for VRAM budget reserve/release/exceed accounting.
/// </summary>
public class VramBudgetTests
{
    private static LlmServerConfig ConfigWithBudget(int? maxVramMb)
    {
        var config = new LlmServerConfig
        {
            Server = new ServerSection { Host = "localhost", Port = 8420, MaxVramMb = maxVramMb },
            Models = new List<ModelConfig> { new() { Id = "main", Path = "m.gguf" } }
        };
        return config;
    }

    [Fact]
    public void TryReserve_UnderBudget_Succeeds()
    {
        var budget = new VramBudget(ConfigWithBudget(1000));
        Assert.True(budget.TryReserve(400));
        Assert.Equal(400, budget.CurrentUsageMb);
        Assert.False(budget.IsExceeded);
    }

    [Fact]
    public void TryReserve_ExceedsBudget_Fails_AndDoesNotAccount()
    {
        var budget = new VramBudget(ConfigWithBudget(1000));
        Assert.True(budget.TryReserve(800));
        Assert.False(budget.TryReserve(800)); // would exceed
        Assert.Equal(800, budget.CurrentUsageMb); // failed reserve left no trace
    }

    [Fact]
    public void TryReserve_ExactlyToLimit_Succeeds_AndIsExceeded()
    {
        var budget = new VramBudget(ConfigWithBudget(1000));
        Assert.True(budget.TryReserve(1000));
        Assert.Equal(1000, budget.CurrentUsageMb);
        Assert.True(budget.IsExceeded);
    }

    [Fact]
    public void Release_ReducesUsage_AndNeverGoesNegative()
    {
        var budget = new VramBudget(ConfigWithBudget(1000));
        Assert.True(budget.TryReserve(600));

        budget.Release(200);
        Assert.Equal(400, budget.CurrentUsageMb);

        budget.Release(10_000); // over-release clamps to zero
        Assert.Equal(0, budget.CurrentUsageMb);
    }

    [Fact]
    public void Release_AllowsNewReservation()
    {
        var budget = new VramBudget(ConfigWithBudget(1000));
        Assert.True(budget.TryReserve(900));
        Assert.False(budget.TryReserve(200));

        budget.Release(900);
        Assert.True(budget.TryReserve(200));
        Assert.Equal(200, budget.CurrentUsageMb);
    }

    [Fact]
    public void UnlimitedBudget_AlwaysReserves()
    {
        var budget = new VramBudget(ConfigWithBudget(null));
        Assert.False(budget.IsExceeded);
        Assert.True(budget.TryReserve(double.MaxValue / 4));
        Assert.False(budget.IsExceeded);
    }
}
