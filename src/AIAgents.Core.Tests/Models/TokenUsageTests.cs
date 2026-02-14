using AIAgents.Core.Models;

namespace AIAgents.Core.Tests.Models;

/// <summary>
/// Tests for TokenUsageData, StoryTokenUsage, AgentTokenUsage,
/// and TokenCostCalculator.
/// </summary>
public sealed class TokenUsageTests
{
    #region TokenCostCalculator Tests

    [Theory]
    [InlineData("gpt-4o", 1000, 500, 0.0075)]        // 1K*2.50/1M + 500*10/1M
    [InlineData("gpt-4o-mini", 1000, 500, 0.00045)]   // 1K*0.15/1M + 500*0.60/1M
    [InlineData("claude-sonnet-4-20250514", 1000, 500, 0.0105)] // 1K*3/1M + 500*15/1M
    [InlineData("claude-3-opus", 1000, 500, 0.0525)]  // 1K*15/1M + 500*75/1M
    [InlineData("claude-3-haiku", 1000, 500, 0.000875)]// 1K*0.25/1M + 500*1.25/1M
    public void Calculate_KnownModels_ReturnsCorrectCost(string model, int input, int output, decimal expected)
    {
        var result = TokenCostCalculator.Calculate(model, input, output);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Calculate_UnknownModel_FallsBackToGpt4oPricing()
    {
        // Unknown model should use GPT-4o rates ($2.50/$10.00)
        var result = TokenCostCalculator.Calculate("unknown-model-xyz", 1000, 500);
        var gpt4oResult = TokenCostCalculator.Calculate("gpt-4o", 1000, 500);

        Assert.Equal(gpt4oResult, result);
    }

    [Fact]
    public void Calculate_ZeroTokens_ReturnsZero()
    {
        Assert.Equal(0m, TokenCostCalculator.Calculate("gpt-4o", 0, 0));
    }

    [Fact]
    public void Calculate_CaseInsensitive_MatchesModel()
    {
        var lower = TokenCostCalculator.Calculate("gpt-4o", 1000, 500);
        var upper = TokenCostCalculator.Calculate("GPT-4O", 1000, 500);

        Assert.Equal(lower, upper);
    }

    [Fact]
    public void Calculate_LargeTokenCounts_CalculatesCorrectly()
    {
        // 1M input tokens at GPT-4o = $2.50
        var result = TokenCostCalculator.Calculate("gpt-4o", 1_000_000, 0);
        Assert.Equal(2.50m, result);
    }

    #endregion

    #region StoryTokenUsage.ClassifyComplexity Tests

    [Theory]
    [InlineData(0, "XS")]
    [InlineData(4999, "XS")]
    [InlineData(5000, "S")]
    [InlineData(14999, "S")]
    [InlineData(15000, "M")]
    [InlineData(29999, "M")]
    [InlineData(30000, "L")]
    [InlineData(59999, "L")]
    [InlineData(60000, "XL")]
    [InlineData(1000000, "XL")]
    public void ClassifyComplexity_ReturnsCorrectBucket(int totalTokens, string expected)
    {
        Assert.Equal(expected, StoryTokenUsage.ClassifyComplexity(totalTokens));
    }

    #endregion

    #region StoryTokenUsage.RecordUsage Tests

    [Fact]
    public void RecordUsage_SingleCall_TracksCorrectly()
    {
        var usage = new StoryTokenUsage();

        usage.RecordUsage("Planning", new TokenUsageData
        {
            InputTokens = 500,
            OutputTokens = 300,
            TotalTokens = 800,
            EstimatedCost = 0.005m,
            Model = "gpt-4o"
        });

        Assert.Equal(500, usage.TotalInputTokens);
        Assert.Equal(300, usage.TotalOutputTokens);
        Assert.Equal(800, usage.TotalTokens);
        Assert.Equal(0.005m, usage.TotalCost);
        Assert.Equal("XS", usage.Complexity);
        Assert.Single(usage.Agents);
        Assert.Equal(1, usage.Agents["Planning"].CallCount);
    }

    [Fact]
    public void RecordUsage_MultipleCallsSameAgent_Accumulates()
    {
        var usage = new StoryTokenUsage();

        usage.RecordUsage("Coding", new TokenUsageData
        {
            InputTokens = 1000, OutputTokens = 500, TotalTokens = 1500,
            EstimatedCost = 0.01m, Model = "gpt-4o"
        });
        usage.RecordUsage("Coding", new TokenUsageData
        {
            InputTokens = 2000, OutputTokens = 1000, TotalTokens = 3000,
            EstimatedCost = 0.02m, Model = "gpt-4o"
        });

        Assert.Equal(3000, usage.TotalInputTokens);
        Assert.Equal(1500, usage.TotalOutputTokens);
        Assert.Equal(4500, usage.TotalTokens);
        Assert.Equal(0.03m, usage.TotalCost);
        Assert.Equal(2, usage.Agents["Coding"].CallCount);
    }

    [Fact]
    public void RecordUsage_MultipleAgents_TracksSeperately()
    {
        var usage = new StoryTokenUsage();

        usage.RecordUsage("Planning", new TokenUsageData
        {
            InputTokens = 500, OutputTokens = 300, TotalTokens = 800,
            EstimatedCost = 0.005m, Model = "gpt-4o"
        });
        usage.RecordUsage("Coding", new TokenUsageData
        {
            InputTokens = 2000, OutputTokens = 1000, TotalTokens = 3000,
            EstimatedCost = 0.02m, Model = "gpt-4o-mini"
        });
        usage.RecordUsage("Testing", new TokenUsageData
        {
            InputTokens = 1500, OutputTokens = 800, TotalTokens = 2300,
            EstimatedCost = 0.015m, Model = "gpt-4o"
        });

        Assert.Equal(3, usage.Agents.Count);
        Assert.Equal(6100, usage.TotalTokens);
        Assert.Equal(0.04m, usage.TotalCost);
        Assert.Equal("S", usage.Complexity); // 6100 tokens → "S" (5K-15K)
    }

    [Fact]
    public void RecordUsage_NullUsage_NoOp()
    {
        var usage = new StoryTokenUsage();
        usage.RecordUsage("Planning", null);

        Assert.Equal(0, usage.TotalTokens);
        Assert.Empty(usage.Agents);
    }

    [Fact]
    public void RecordUsage_UpdatesModelToLatest()
    {
        var usage = new StoryTokenUsage();

        usage.RecordUsage("Coding", new TokenUsageData
        {
            InputTokens = 100, OutputTokens = 50, TotalTokens = 150,
            EstimatedCost = 0.001m, Model = "gpt-4o"
        });
        usage.RecordUsage("Coding", new TokenUsageData
        {
            InputTokens = 100, OutputTokens = 50, TotalTokens = 150,
            EstimatedCost = 0.001m, Model = "gpt-4o-mini"
        });

        // The model should be the last one used
        Assert.Equal("gpt-4o-mini", usage.Agents["Coding"].Model);
    }

    [Fact]
    public void RecordUsage_ComplexityUpdatesProgressively()
    {
        var usage = new StoryTokenUsage();

        // Start at XS
        usage.RecordUsage("Agent", new TokenUsageData
        {
            InputTokens = 2000, OutputTokens = 1000, TotalTokens = 3000,
            EstimatedCost = 0.01m, Model = "gpt-4o"
        });
        Assert.Equal("XS", usage.Complexity);

        // Move to S (total = 8000)
        usage.RecordUsage("Agent", new TokenUsageData
        {
            InputTokens = 3000, OutputTokens = 2000, TotalTokens = 5000,
            EstimatedCost = 0.02m, Model = "gpt-4o"
        });
        Assert.Equal("S", usage.Complexity);

        // Move to M (total = 23000)
        usage.RecordUsage("Agent", new TokenUsageData
        {
            InputTokens = 10000, OutputTokens = 5000, TotalTokens = 15000,
            EstimatedCost = 0.05m, Model = "gpt-4o"
        });
        Assert.Equal("M", usage.Complexity);
    }

    #endregion

    #region AgentTokenUsage Tests

    [Fact]
    public void AgentTokenUsage_DefaultValues_AreZero()
    {
        var agentUsage = new AgentTokenUsage();

        Assert.Equal(0, agentUsage.InputTokens);
        Assert.Equal(0, agentUsage.OutputTokens);
        Assert.Equal(0, agentUsage.TotalTokens);
        Assert.Equal(0m, agentUsage.EstimatedCost);
        Assert.Equal(0, agentUsage.CallCount);
        Assert.Equal("", agentUsage.Model);
    }

    #endregion

    #region TokenUsageData Tests

    [Fact]
    public void TokenUsageData_InitProperties_SetCorrectly()
    {
        var data = new TokenUsageData
        {
            InputTokens = 1000,
            OutputTokens = 500,
            TotalTokens = 1500,
            EstimatedCost = 0.01m,
            Model = "claude-3-opus"
        };

        Assert.Equal(1000, data.InputTokens);
        Assert.Equal(500, data.OutputTokens);
        Assert.Equal(1500, data.TotalTokens);
        Assert.Equal(0.01m, data.EstimatedCost);
        Assert.Equal("claude-3-opus", data.Model);
    }

    #endregion
}
