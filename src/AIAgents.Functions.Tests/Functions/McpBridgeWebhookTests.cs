using AIAgents.Functions.Functions;

namespace AIAgents.Functions.Tests.Functions;

public sealed class McpBridgeWebhookTests
{
    [Theory]
    [InlineData("Planning", null, "Planning Agent")]
    [InlineData("Coding", null, "Coding Agent")]
    [InlineData("Testing", null, "Testing Agent")]
    [InlineData("Review", null, "Review Agent")]
    [InlineData("Documentation", null, "Documentation Agent")]
    [InlineData("Deployment", null, "Deployment Agent")]
    [InlineData("Deploy", null, "Deployment Agent")]
    [InlineData("NeedsInfo", null, null)]
    [InlineData("Done", null, null)]
    public void MapStage_KnownStages_ReturnsExpectedMapping(string stage, string expectedState, string? expectedAgent)
    {
        var mapping = McpBridgeWebhook.MapStage(stage);

        Assert.NotNull(mapping);
        Assert.Equal(expectedState, mapping!.State);
        Assert.Equal(expectedAgent, mapping.CurrentAgent);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData("  ")]
    public void MapStage_UnknownStages_ReturnsNull(string stage)
    {
        var mapping = McpBridgeWebhook.MapStage(stage);

        Assert.Null(mapping);
    }
}
