using AIAgents.Functions.Functions;

namespace AIAgents.Functions.Tests.Functions;

/// <summary>
/// Tests for CopilotBridgeWebhook — static/internal methods that don't require
/// full DI or HTTP infrastructure.
/// </summary>
public sealed class CopilotBridgeWebhookTests
{
    // ========== EXTRACT WORK ITEM ID TESTS ==========

    [Fact]
    public void ExtractWorkItemId_FromTitle_ReturnsId()
    {
        var result = CopilotBridgeWebhook.ExtractWorkItemId("[US-12345] Implement feature", "");
        Assert.Equal(12345, result);
    }

    [Fact]
    public void ExtractWorkItemId_FromBody_ReturnsId()
    {
        var result = CopilotBridgeWebhook.ExtractWorkItemId("Some PR title", "This implements US-67890 from ADO");
        Assert.Equal(67890, result);
    }

    [Fact]
    public void ExtractWorkItemId_TitleTakesPrecedence()
    {
        // Should match the first occurrence (in title)
        var result = CopilotBridgeWebhook.ExtractWorkItemId("[US-111] Feature", "Related to US-222");
        Assert.Equal(111, result);
    }

    [Fact]
    public void ExtractWorkItemId_NoMatch_ReturnsNull()
    {
        var result = CopilotBridgeWebhook.ExtractWorkItemId("Fix typo in readme", "No work item reference here");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractWorkItemId_EmptyInputs_ReturnsNull()
    {
        var result = CopilotBridgeWebhook.ExtractWorkItemId("", "");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractWorkItemId_CaseInsensitive()
    {
        var result = CopilotBridgeWebhook.ExtractWorkItemId("us-42 feature", "");
        Assert.Equal(42, result);
    }

    [Fact]
    public void ExtractWorkItemId_LargeId()
    {
        var result = CopilotBridgeWebhook.ExtractWorkItemId("[US-999999] Big project", "");
        Assert.Equal(999999, result);
    }

    [Fact]
    public void ExtractWorkItemId_IdInMiddleOfBody()
    {
        var result = CopilotBridgeWebhook.ExtractWorkItemId(
            "Copilot implementation",
            "This PR implements the changes for US-54321 as described in the plan.");
        Assert.Equal(54321, result);
    }

    // ========== READINESS GATING TESTS ==========

    [Fact]
    public void IsReadyToReconcile_Opened_AlwaysFalse()
    {
        var result = CopilotBridgeWebhook.IsReadyToReconcile(
            action: "opened",
            prTitle: "Feature implementation");

        Assert.False(result);
    }

    [Fact]
    public void IsReadyToReconcile_Edited_WipFalse_True()
    {
        var result = CopilotBridgeWebhook.IsReadyToReconcile(
            action: "edited",
            prTitle: "Feature implementation");

        Assert.True(result);
    }

    [Fact]
    public void IsReadyToReconcile_Synchronize_WipTrue_False()
    {
        var result = CopilotBridgeWebhook.IsReadyToReconcile(
            action: "synchronize",
            prTitle: "[WIP] Feature implementation");

        Assert.False(result);
    }

    [Fact]
    public void IsReadyToReconcile_ReadyForReview_WipFalse_True()
    {
        var result = CopilotBridgeWebhook.IsReadyToReconcile(
            action: "ready_for_review",
            prTitle: "Feature implementation");

        Assert.True(result);
    }

    // ========== CHECKPOINT ENFORCEMENT TESTS ==========

    [Fact]
    public void ParseRequiredAdoCheckpoints_Empty_UsesDefaults()
    {
        var checkpoints = CopilotBridgeWebhook.ParseRequiredAdoCheckpoints("");

        Assert.Equal(3, checkpoints.Count);
        Assert.Contains("LastAgent", checkpoints);
        Assert.Contains("CurrentAIAgent", checkpoints);
        Assert.Contains("CompletionComment", checkpoints);
    }

    [Fact]
    public void ParseRequiredAdoCheckpoints_Aliases_Normalized()
    {
        var checkpoints = CopilotBridgeWebhook.ParseRequiredAdoCheckpoints("last_agent, current-agent, comment");

        Assert.Equal(3, checkpoints.Count);
        Assert.Contains("LastAgent", checkpoints);
        Assert.Contains("CurrentAIAgent", checkpoints);
        Assert.Contains("CompletionComment", checkpoints);
    }

    [Fact]
    public void EvaluateRequiredCheckpointStatus_AllPresent_Passes()
    {
        var required = new[] { "LastAgent", "CurrentAIAgent", "CompletionComment" };

        var (passed, missing) = CopilotBridgeWebhook.EvaluateRequiredCheckpointStatus(
            required,
            lastAgentUpdated: true,
            currentAgentUpdated: true,
            completionCommentAdded: true);

        Assert.True(passed);
        Assert.Empty(missing);
    }

    [Fact]
    public void EvaluateRequiredCheckpointStatus_MissingCurrentAndComment_Fails()
    {
        var required = new[] { "LastAgent", "CurrentAIAgent", "CompletionComment" };

        var (passed, missing) = CopilotBridgeWebhook.EvaluateRequiredCheckpointStatus(
            required,
            lastAgentUpdated: true,
            currentAgentUpdated: false,
            completionCommentAdded: false);

        Assert.False(passed);
        Assert.Equal(2, missing.Count);
        Assert.Contains("CurrentAIAgent", missing);
        Assert.Contains("CompletionComment", missing);
    }
}
