using System.Text.Json;
using AIAgents.Functions.Models;

namespace AIAgents.Functions.Tests.Functions;

/// <summary>
/// Tests for webhook payload deserialization and state-to-agent mapping logic.
/// OrchestratorWebhook itself has a hard QueueClient dependency, so we test the
/// underlying parsing/mapping independently.
/// </summary>
public sealed class WebhookPayloadParsingTests
{
    /// <summary>
    /// Maps ADO work item states to expected agent types.
    /// This mirrors the static dictionary in OrchestratorWebhook.
    /// Only "Story Planning" is mapped — all other transitions are handled
    /// by direct EnqueueAsync calls within each agent (prevents double-dispatch).
    /// </summary>
    private static readonly Dictionary<string, AgentType> s_expectedMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Story Planning"] = AgentType.Planning
    };

    // ── Payload deserialization ──

    [Fact]
    public void Deserialize_FullPayload_ExtractsAllFields()
    {
        var json = """
        {
            "eventType": "workitem.updated",
            "resource": {
                "id": 100,
                "workItemId": 12345,
                "fields": {
                    "System.State": {
                        "oldValue": "New",
                        "newValue": "Story Planning"
                    }
                },
                "revision": {
                    "id": 12345,
                    "fields": {
                        "System.State": "Story Planning"
                    }
                }
            }
        }
        """;

        var payload = JsonSerializer.Deserialize<ServiceHookPayload>(json);

        Assert.NotNull(payload);
        Assert.Equal("workitem.updated", payload.EventType);
        Assert.NotNull(payload.Resource);
        Assert.Equal(100, payload.Resource.Id);
        Assert.Equal(12345, payload.Resource.WorkItemId);
        Assert.Equal("New", payload.Resource.Fields!.State!.OldValue);
        Assert.Equal("Story Planning", payload.Resource.Fields.State.NewValue);
    }

    [Fact]
    public void Deserialize_MinimalPayload_HandlesNullFields()
    {
        var json = """
        {
            "resource": {
                "id": 42
            }
        }
        """;

        var payload = JsonSerializer.Deserialize<ServiceHookPayload>(json);

        Assert.NotNull(payload);
        Assert.Null(payload.EventType);
        Assert.NotNull(payload.Resource);
        Assert.Equal(42, payload.Resource.Id);
        Assert.Null(payload.Resource.Fields);
        Assert.Null(payload.Resource.Revision);
    }

    [Fact]
    public void Deserialize_NullResource_ResultsInNullResource()
    {
        var json = """{ "eventType": "workitem.updated" }""";

        var payload = JsonSerializer.Deserialize<ServiceHookPayload>(json);

        Assert.NotNull(payload);
        Assert.Null(payload.Resource);
    }

    [Fact]
    public void Deserialize_RevisionFields_ExtractsState()
    {
        var json = """
        {
            "resource": {
                "id": 0,
                "revision": {
                    "id": 12345,
                    "fields": {
                        "System.State": "AI Code"
                    }
                }
            }
        }
        """;

        var payload = JsonSerializer.Deserialize<ServiceHookPayload>(json);

        Assert.NotNull(payload!.Resource!.Revision);
        Assert.Equal(12345, payload.Resource.Revision.Id);
        Assert.True(payload.Resource.Revision.Fields!.ContainsKey("System.State"));
        Assert.Equal("AI Code", payload.Resource.Revision.Fields["System.State"].GetString());
    }

    // ── State-to-agent mapping ──

    [Theory]
    [InlineData("Story Planning", AgentType.Planning)]
    public void StateMapping_KnownStates_MapToCorrectAgents(string state, AgentType expectedAgent)
    {
        Assert.True(s_expectedMapping.TryGetValue(state, out var agent));
        Assert.Equal(expectedAgent, agent);
    }

    [Theory]
    [InlineData("New")]
    [InlineData("Active")]
    [InlineData("Closed")]
    [InlineData("Code Review")]
    [InlineData("Ready for QA")]
    [InlineData("Deployed")]
    [InlineData("Needs Revision")]
    [InlineData("AI Code")]
    [InlineData("AI Test")]
    [InlineData("AI Review")]
    [InlineData("AI Docs")]
    public void StateMapping_UnknownStates_AreNotMapped(string state)
    {
        Assert.False(s_expectedMapping.ContainsKey(state));
    }

    [Fact]
    public void StateMapping_CaseInsensitive()
    {
        Assert.True(s_expectedMapping.TryGetValue("story planning", out _));
        Assert.True(s_expectedMapping.TryGetValue("STORY PLANNING", out _));
    }

    // ── Work item ID extraction logic ──

    [Fact]
    public void WorkItemId_PreferWorkItemIdField()
    {
        var json = """
        {
            "resource": {
                "id": 100,
                "workItemId": 12345,
                "revision": { "id": 99999 }
            }
        }
        """;

        var payload = JsonSerializer.Deserialize<ServiceHookPayload>(json)!;

        // Matches OrchestratorWebhook logic: workItemId > 0 ? workItemId : revision?.Id ?? id
        var workItemId = payload.Resource!.WorkItemId > 0
            ? payload.Resource.WorkItemId
            : payload.Resource.Revision?.Id ?? payload.Resource.Id;

        Assert.Equal(12345, workItemId);
    }

    [Fact]
    public void WorkItemId_FallsBackToRevisionId()
    {
        var json = """
        {
            "resource": {
                "id": 100,
                "workItemId": 0,
                "revision": { "id": 12345 }
            }
        }
        """;

        var payload = JsonSerializer.Deserialize<ServiceHookPayload>(json)!;

        var workItemId = payload.Resource!.WorkItemId > 0
            ? payload.Resource.WorkItemId
            : payload.Resource.Revision?.Id ?? payload.Resource.Id;

        Assert.Equal(12345, workItemId);
    }

    [Fact]
    public void WorkItemId_FallsBackToResourceId()
    {
        var json = """
        {
            "resource": {
                "id": 12345
            }
        }
        """;

        var payload = JsonSerializer.Deserialize<ServiceHookPayload>(json)!;

        var workItemId = payload.Resource!.WorkItemId > 0
            ? payload.Resource.WorkItemId
            : payload.Resource.Revision?.Id ?? payload.Resource.Id;

        Assert.Equal(12345, workItemId);
    }

    // ── Extension data ──

    [Fact]
    public void Deserialize_UnknownFields_CapturedInExtensionData()
    {
        var json = """
        {
            "eventType": "workitem.updated",
            "publisherId": "tfs",
            "scope": "all",
            "resource": {
                "id": 1,
                "unknownField": "test"
            }
        }
        """;

        var payload = JsonSerializer.Deserialize<ServiceHookPayload>(json)!;

        Assert.NotNull(payload.ExtensionData);
        Assert.Contains("publisherId", payload.ExtensionData.Keys);
        Assert.NotNull(payload.Resource!.ExtensionData);
        Assert.Contains("unknownField", payload.Resource.ExtensionData.Keys);
    }
}
