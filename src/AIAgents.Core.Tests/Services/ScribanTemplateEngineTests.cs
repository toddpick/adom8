using AIAgents.Core.Interfaces;
using AIAgents.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIAgents.Core.Tests.Services;

/// <summary>
/// Tests for the ScribanTemplateEngine service.
/// Validates variable replacement, list iteration, and error handling.
/// </summary>
public sealed class ScribanTemplateEngineTests
{
    private readonly ITemplateEngine _engine;

    public ScribanTemplateEngineTests()
    {
        _engine = new ScribanTemplateEngine(NullLogger<ScribanTemplateEngine>.Instance);
    }

    [Fact]
    public async Task RenderAsync_PlanTemplate_ReplacesVariables()
    {
        // Arrange
        var model = new Dictionary<string, object?>
        {
            ["WORK_ITEM_ID"] = "US-12345",
            ["TITLE"] = "Test Story",
            ["STATE"] = "Story Planning",
            ["CREATED_DATE"] = "2026-01-01",
            ["DESCRIPTION"] = "A test description",
            ["ACCEPTANCE_CRITERIA"] = "Should work",
            ["PROBLEM_ANALYSIS"] = "Analysis text",
            ["TECHNICAL_APPROACH"] = "Approach text",
            ["AFFECTED_FILES"] = new List<string> { "file1.cs", "file2.cs" },
            ["COMPLEXITY"] = 5,
            ["ARCHITECTURE"] = "Clean architecture",
            ["SUBTASKS"] = new List<string> { "Task 1", "Task 2" },
            ["DEPENDENCIES"] = new List<string> { "Dep 1" },
            ["RISKS"] = new List<string> { "Risk 1" },
            ["ASSUMPTIONS"] = new List<string> { "Assume 1" },
            ["TESTING_STRATEGY"] = "Unit tests",
            ["TIMESTAMP"] = "2026-01-01T00:00:00Z"
        };

        // Act
        var result = await _engine.RenderAsync("PLAN.template.md", model);

        // Assert
        Assert.Contains("US-12345", result);
        Assert.Contains("Test Story", result);
        Assert.Contains("Analysis text", result);
        Assert.Contains("file1.cs", result);
        Assert.Contains("Task 1", result);
        Assert.Contains("Task 2", result);
    }

    [Fact]
    public async Task RenderAsync_TasksTemplate_RendersSubTasks()
    {
        var model = new Dictionary<string, object?>
        {
            ["WORK_ITEM_ID"] = "US-100",
            ["TITLE"] = "Test Tasks",
            ["SUBTASKS"] = new List<string> { "Create model", "Implement service", "Write tests" },
            ["TIMESTAMP"] = "2026-01-01T00:00:00Z"
        };

        var result = await _engine.RenderAsync("TASKS.template.md", model);

        Assert.Contains("US-100", result);
        Assert.Contains("Create model", result);
        Assert.Contains("Implement service", result);
        Assert.Contains("Write tests", result);
    }

    [Fact]
    public async Task RenderAsync_NonExistentTemplate_ThrowsFileNotFound()
    {
        var model = new Dictionary<string, object?>();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _engine.RenderAsync("DOES_NOT_EXIST.template.md", model));
    }

    [Fact]
    public async Task RenderAsync_EmptyModel_RendersWithoutError()
    {
        // Even with an empty model, the template engine should not throw.
        // Variables not in the model are rendered as empty strings.
        var model = new Dictionary<string, object?>();

        var result = await _engine.RenderAsync("PLAN.template.md", model);

        // Should produce output (template structure) even without values
        Assert.NotNull(result);
        Assert.True(result.Length > 0);
    }

    [Fact]
    public async Task RenderAsync_TestPlanTemplate_RendersTestCases()
    {
        var model = new Dictionary<string, object?>
        {
            ["WORK_ITEM_ID"] = "US-200",
            ["TITLE"] = "Test Plan Test",
            ["TEST_CASES"] = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["NAME"] = "Test_Success",
                    ["TYPE"] = "Unit",
                    ["PRIORITY"] = "High",
                    ["DESCRIPTION"] = "Verify success path",
                    ["EXPECTED_RESULT"] = "Returns true"
                }
            },
            ["TIMESTAMP"] = "2026-01-01T00:00:00Z"
        };

        var result = await _engine.RenderAsync("TEST_PLAN.template.md", model);

        Assert.Contains("US-200", result);
        Assert.Contains("Test_Success", result);
    }

    [Fact]
    public async Task RenderAsync_CodeReviewTemplate_RendersScoreAndIssues()
    {
        var model = new Dictionary<string, object?>
        {
            ["WORK_ITEM_ID"] = "US-300",
            ["SCORE"] = 85,
            ["RECOMMENDATION"] = "Approve",
            ["SUMMARY"] = "Good code quality",
            ["CRITICAL_COUNT"] = 0,
            ["CRITICAL_ISSUES"] = new List<Dictionary<string, object?>>(),
            ["HIGH_COUNT"] = 0,
            ["HIGH_ISSUES"] = new List<Dictionary<string, object?>>(),
            ["MEDIUM_COUNT"] = 1,
            ["MEDIUM_ISSUES"] = new List<Dictionary<string, object?>>
            {
                new() { ["ISSUE"] = "Consider caching" }
            },
            ["LOW_COUNT"] = 0,
            ["LOW_ISSUES"] = new List<Dictionary<string, object?>>(),
            ["POSITIVE_FINDINGS"] = new List<string> { "Good naming" },
            ["MODEL_NAME"] = "AI Review",
            ["TIMESTAMP"] = "2026-01-01T00:00:00Z"
        };

        var result = await _engine.RenderAsync("CODE_REVIEW.template.md", model);

        Assert.Contains("85", result);
        Assert.Contains("Approve", result);
        Assert.Contains("Consider caching", result);
    }

    [Fact]
    public async Task RenderAsync_DocumentationTemplate_RendersAllSections()
    {
        var model = new Dictionary<string, object?>
        {
            ["WORK_ITEM_ID"] = "US-400",
            ["TITLE"] = "Doc Test",
            ["TIMESTAMP"] = "2026-01-01T00:00:00Z",
            ["OVERVIEW"] = "Feature overview text",
            ["CHANGES"] = "## Changes list",
            ["API_DOCS"] = "## API Reference",
            ["USAGE_EXAMPLES"] = "```\ncode example\n```",
            ["BREAKING_CHANGES"] = (string?)null,
            ["MIGRATION_GUIDE"] = (string?)null,
            ["CONFIGURATION_CHANGES"] = "None"
        };

        var result = await _engine.RenderAsync("DOCUMENTATION.template.md", model);

        Assert.Contains("Feature overview text", result);
        Assert.Contains("API Reference", result);
    }

    [Fact]
    public async Task RenderAsync_NullValues_HandledGracefully()
    {
        var model = new Dictionary<string, object?>
        {
            ["WORK_ITEM_ID"] = "US-500",
            ["TITLE"] = null,
            ["STATE"] = null,
            ["CREATED_DATE"] = null,
            ["DESCRIPTION"] = null,
            ["ACCEPTANCE_CRITERIA"] = null,
            ["PROBLEM_ANALYSIS"] = null,
            ["TECHNICAL_APPROACH"] = null,
            ["AFFECTED_FILES"] = null,
            ["COMPLEXITY"] = null,
            ["ARCHITECTURE"] = null,
            ["SUBTASKS"] = null,
            ["DEPENDENCIES"] = null,
            ["RISKS"] = null,
            ["ASSUMPTIONS"] = null,
            ["TESTING_STRATEGY"] = null,
            ["TIMESTAMP"] = null
        };

        // Should not throw with null values
        var result = await _engine.RenderAsync("PLAN.template.md", model);
        Assert.NotNull(result);
    }
}
