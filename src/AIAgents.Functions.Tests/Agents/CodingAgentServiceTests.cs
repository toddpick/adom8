using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using AIAgents.Functions.Agents;
using AIAgents.Functions.Models;
using AIAgents.Functions.Tests.Helpers;
using AIAgents.Functions.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AIAgents.Functions.Tests.Agents;

/// <summary>
/// Tests for CodingAgentService covering agentic tool-use loop,
/// state transitions, prompt building, and error handling.
/// </summary>
public sealed class CodingAgentServiceTests
{
    private readonly Mock<IAIClientFactory> _aiFactoryMock = new();
    private readonly Mock<IAIClient> _aiClientMock = new();
    private readonly Mock<IAzureDevOpsClient> _adoMock = new();
    private readonly Mock<IGitOperations> _gitMock = new();
    private readonly Mock<IStoryContextFactory> _contextFactoryMock = new();
    private readonly Mock<IStoryContext> _contextMock = new();
    private readonly Mock<ICodebaseContextProvider> _codebaseMock = new();
    private readonly Mock<IAgentTaskQueue> _taskQueueMock = new();
    private readonly Mock<IActivityLogger> _activityLoggerMock = new();

    private StoryState _capturedState = null!;

    public CodingAgentServiceTests()
    {
        _aiFactoryMock.Setup(f => f.GetClientForAgent("Coding", It.IsAny<StoryModelOverrides?>())).Returns(_aiClientMock.Object);
        _contextFactoryMock.Setup(f => f.Create(It.IsAny<int>(), It.IsAny<string>())).Returns(_contextMock.Object);
    }

    private CodingAgentService CreateService()
    {
        return new CodingAgentService(
            _aiFactoryMock.Object, _adoMock.Object, _gitMock.Object,
            _contextFactoryMock.Object, _codebaseMock.Object,
            NullLogger<CodingAgentService>.Instance, _taskQueueMock.Object,
            _activityLoggerMock.Object);
    }

    private void SetupHappyPath(AgenticResult? agenticResult = null)
    {
        var wi = MockAIResponses.SampleWorkItem();
        var state = MockAIResponses.SampleState(wi.Id, "AI Code");

        _adoMock.Setup(a => a.GetWorkItemAsync(wi.Id, It.IsAny<CancellationToken>())).ReturnsAsync(wi);
        _gitMock.Setup(g => g.EnsureBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(@"C:\repos\test");
        _gitMock.Setup(g => g.ListFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "src/Program.cs", "dashboard/index.html" });
        _gitMock.Setup(g => g.ReadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Console.WriteLine(\"Hello\");");
        _gitMock.Setup(g => g.WriteFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _contextMock.Setup(c => c.LoadStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(state);
        _contextMock.Setup(c => c.SaveStateAsync(It.IsAny<StoryState>(), It.IsAny<CancellationToken>()))
            .Callback<StoryState, CancellationToken>((s, _) => _capturedState = s).Returns(Task.CompletedTask);
        _contextMock.Setup(c => c.ReadArtifactAsync("PLAN.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync("# Plan\nModify `src/Program.cs`.\nSome plan content.");

        _aiClientMock.Setup(a => a.CompleteWithToolsAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ToolDefinition>>(),
                It.IsAny<Func<ToolCall, CancellationToken, Task<string>>>(),
                It.IsAny<AgenticOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(agenticResult ?? new AgenticResult
            {
                FinalResponse = "I've implemented the changes. Modified src/Program.cs to update the greeting.",
                RoundsExecuted = 3,
                CompletedNaturally = true,
                ToolCalls =
                [
                    new ToolCallLog { ToolName = "read_file", Input = "{\"path\":\"src/Program.cs\"}", Round = 1 },
                    new ToolCallLog { ToolName = "edit_file", Input = "{\"path\":\"src/Program.cs\",\"search\":\"Hello\",\"replace\":\"Hello World\"}", Round = 2 },
                    new ToolCallLog { ToolName = "read_file", Input = "{\"path\":\"src/Program.cs\"}", Round = 3 }
                ],
                TotalUsage = new TokenUsageData
                {
                    InputTokens = 5000, OutputTokens = 2000, TotalTokens = 7000,
                    EstimatedCost = 0.05m, Model = "claude-sonnet-4-20250514"
                }
            });

        // Simulate that the tool executor modifies files — but since we mock CompleteWithToolsAsync,
        // the real executor won't run. So WriteFileAsync won't be called by the executor.
        // The service checks toolExecutor.ModifiedFiles, which will be empty in tests
        // unless we structure tests differently. For integration-level behavior, we test state transitions.

        _codebaseMock.Setup(c => c.LoadRelevantContextAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("");
    }

    // ========== AGENTIC LOOP TESTS ==========

    [Fact]
    public async Task ExecuteAsync_CallsCompleteWithToolsAsync()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Coding });

        _aiClientMock.Verify(a => a.CompleteWithToolsAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.Is<IReadOnlyList<ToolDefinition>>(tools => tools.Count == 5),
            It.IsAny<Func<ToolCall, CancellationToken, Task<string>>>(),
            It.Is<AgenticOptions>(o => o.MaxRounds == 15),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_TransitionsToAITest()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Coding });

        Assert.Equal("AI Test", _capturedState.CurrentState);
        Assert.Equal("completed", _capturedState.Agents["Coding"].Status);
    }

    [Fact]
    public async Task ExecuteAsync_EnqueuesTestingAgent()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Coding });

        _taskQueueMock.Verify(q => q.EnqueueAsync(
            It.Is<AgentTask>(t => t.AgentType == AgentType.Testing), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_TracksAgenticModeInState()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Coding });

        Assert.Equal("agentic", _capturedState.Agents["Coding"].AdditionalData!["mode"]);
    }

    [Fact]
    public async Task ExecuteAsync_TracksTokenUsage()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Coding });

        Assert.True(_capturedState.TokenUsage.Agents.ContainsKey("Coding"));
        Assert.Equal(7000, _capturedState.TokenUsage.Agents["Coding"].TotalTokens);
    }

    [Fact]
    public async Task ExecuteAsync_PostsAdoComment()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Coding });

        _adoMock.Verify(a => a.AddWorkItemCommentAsync(12345,
            It.Is<string>(c => c.Contains("Agentic Loop") && c.Contains("Tool calls")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_TracksRoundCountInState()
    {
        SetupHappyPath();
        var service = CreateService();

        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Coding });

        Assert.Equal(3, _capturedState.Agents["Coding"].AdditionalData!["rounds"]);
    }

    [Fact]
    public async Task ExecuteAsync_NoPlan_StillRuns()
    {
        SetupHappyPath();
        _contextMock.Setup(c => c.ReadArtifactAsync("PLAN.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var service = CreateService();
        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Coding });

        Assert.Equal("AI Test", _capturedState.CurrentState);
    }

    [Fact]
    public async Task ExecuteAsync_ZeroFilesModified_StillTransitionsToTest()
    {
        // Even with no files modified, should proceed to testing
        SetupHappyPath(new AgenticResult
        {
            FinalResponse = "No changes were needed.",
            RoundsExecuted = 1,
            CompletedNaturally = true,
            ToolCalls = [],
            TotalUsage = new TokenUsageData { InputTokens = 100, OutputTokens = 50, TotalTokens = 150, Model = "test" }
        });

        var service = CreateService();
        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Coding });

        Assert.Equal("AI Test", _capturedState.CurrentState);
        _taskQueueMock.Verify(q => q.EnqueueAsync(
            It.Is<AgentTask>(t => t.AgentType == AgentType.Testing), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_MaxRoundsExceeded_StillCompletes()
    {
        SetupHappyPath(new AgenticResult
        {
            FinalResponse = null,
            RoundsExecuted = 25,
            CompletedNaturally = false,
            ToolCalls = [new ToolCallLog { ToolName = "read_file", Input = "{}", Round = 25 }],
            TotalUsage = new TokenUsageData { InputTokens = 50000, OutputTokens = 20000, TotalTokens = 70000, Model = "test" }
        });

        var service = CreateService();
        await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Coding });

        Assert.Equal("AI Test", _capturedState.CurrentState);
        Assert.False((bool)_capturedState.Agents["Coding"].AdditionalData!["completedNaturally"]);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsOk()
    {
        SetupHappyPath();
        var service = CreateService();

        var result = await service.ExecuteAsync(new AgentTask { WorkItemId = 12345, AgentType = AgentType.Coding });

        Assert.True(result.Success);
    }

    // ========== PROMPT BUILDING TESTS ==========

    [Fact]
    public void BuildSystemPrompt_ContainsToolInstructions()
    {
        var prompt = CodingAgentService.BuildSystemPrompt();

        Assert.Contains("read_file", prompt);
        Assert.Contains("write_file", prompt);
        Assert.Contains("edit_file", prompt);
        Assert.Contains("list_files", prompt);
        Assert.Contains("Do NOT modify test files", prompt);
    }

    [Fact]
    public void BuildUserPrompt_IncludesStoryDetails()
    {
        var wi = MockAIResponses.SampleWorkItem();
        var prompt = CodingAgentService.BuildUserPrompt(wi, "# Plan\nDo stuff", "src/Program.cs", "");

        Assert.Contains("12345", prompt);
        Assert.Contains(wi.Title, prompt);
        Assert.Contains("Do stuff", prompt);
        Assert.Contains("src/Program.cs", prompt);
    }

    [Fact]
    public void BuildUserPrompt_IncludesCodebaseContext()
    {
        var wi = MockAIResponses.SampleWorkItem();
        var prompt = CodingAgentService.BuildUserPrompt(wi, "Plan", "files", "Some context about the codebase");

        Assert.Contains("Some context about the codebase", prompt);
    }

    // ========== EXTRACTED REFERENCED FILES (kept from old tests) ==========

    [Fact]
    public void ExtractReferencedFiles_FindsFilesInPlan()
    {
        var plan = "We need to modify `src/Services/MyService.cs` and `src/Program.cs`.";
        var files = new List<string> { "src/Program.cs", "src/Services/MyService.cs", "src/Models/Unrelated.cs" };

        var result = CodingAgentService.ExtractReferencedFiles(plan, files);

        Assert.Contains("src/Program.cs", result);
        Assert.Contains("src/Services/MyService.cs", result);
        Assert.DoesNotContain("src/Models/Unrelated.cs", result);
    }

    [Fact]
    public void ExtractReferencedFiles_HandlesEmptyPlan()
    {
        var result = CodingAgentService.ExtractReferencedFiles("", new List<string> { "src/Program.cs" });
        Assert.Empty(result);
    }

    // ========== GET MAX ROUNDS FOR COMPLEXITY ==========

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 10)]
    [InlineData(3, 15)]
    [InlineData(5, 15)]
    [InlineData(8, 20)]
    [InlineData(13, 25)]
    public void GetMaxRoundsForComplexity_ScalesByStoryPoints(int points, int expectedRounds)
    {
        var state = new StoryState { CurrentState = "AI Code" };
        state.Decisions.Add(new Decision
        {
            Agent = "Planning",
            DecisionText = $"Estimated complexity: {points} story points",
            Rationale = "Test"
        });

        var result = CodingAgentService.GetMaxRoundsForComplexity(state);

        Assert.Equal(expectedRounds, result);
    }

    [Fact]
    public void GetMaxRoundsForComplexity_NoDecision_Returns15()
    {
        var state = new StoryState { CurrentState = "AI Code" };

        var result = CodingAgentService.GetMaxRoundsForComplexity(state);

        Assert.Equal(15, result);
    }

    [Fact]
    public void GetMaxRoundsForComplexity_NonPlanningDecision_Returns15()
    {
        var state = new StoryState { CurrentState = "AI Code" };
        state.Decisions.Add(new Decision
        {
            Agent = "Review",
            DecisionText = "Estimated complexity: 13 story points",
            Rationale = "Wrong agent"
        });

        var result = CodingAgentService.GetMaxRoundsForComplexity(state);

        Assert.Equal(15, result);
    }
}
