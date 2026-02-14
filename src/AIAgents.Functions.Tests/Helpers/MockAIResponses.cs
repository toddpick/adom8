using System.Text.Json;

namespace AIAgents.Functions.Tests.Helpers;

/// <summary>
/// Provides canned AI API responses for deterministic testing.
/// All responses follow the OpenAI chat completion format.
/// </summary>
public static class MockAIResponses
{
    /// <summary>
    /// Wraps content in a standard OpenAI chat completion JSON envelope with usage data.
    /// </summary>
    public static string WrapInCompletion(string content, int promptTokens = 500, int completionTokens = 300, string model = "gpt-4o")
    {
        var response = new
        {
            id = "chatcmpl-test",
            @object = "chat.completion",
            created = 1700000000,
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content },
                    finish_reason = "stop"
                }
            },
            usage = new
            {
                prompt_tokens = promptTokens,
                completion_tokens = completionTokens,
                total_tokens = promptTokens + completionTokens
            }
        };

        return JsonSerializer.Serialize(response);
    }

    /// <summary>
    /// Returns a completion envelope with no usage data (tests graceful handling).
    /// </summary>
    public static string WrapInCompletionNoUsage(string content)
    {
        var response = new
        {
            id = "chatcmpl-test-nousage",
            @object = "chat.completion",
            created = 1700000000,
            model = "gpt-4o",
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content },
                    finish_reason = "stop"
                }
            }
        };

        return JsonSerializer.Serialize(response);
    }

    /// <summary>
    /// Returns a completion envelope with empty content (triggers error path).
    /// </summary>
    public static string EmptyContentCompletion()
    {
        var response = new
        {
            id = "chatcmpl-empty",
            @object = "chat.completion",
            created = 1700000000,
            model = "gpt-4o",
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = "" },
                    finish_reason = "stop"
                }
            },
            usage = new
            {
                prompt_tokens = 10,
                completion_tokens = 0,
                total_tokens = 10
            }
        };

        return JsonSerializer.Serialize(response);
    }

    #region Planning Agent Responses

    public static string ValidPlanningResponse => JsonSerializer.Serialize(new
    {
        problemAnalysis = "The story requires implementing a user registration feature with email verification.",
        technicalApproach = "Create a RegistrationService with email verification flow using SMTP.",
        affectedFiles = new[] { "src/Services/RegistrationService.cs", "src/Controllers/AuthController.cs", "src/Models/User.cs" },
        complexity = 8,
        architecture = "Clean architecture with service layer pattern",
        subTasks = new[] { "Create User model", "Implement RegistrationService", "Add AuthController endpoint", "Create email verification flow" },
        dependencies = new[] { "SMTP service", "Database migrations" },
        risks = new[] { "Email delivery reliability", "Token expiration handling" },
        assumptions = new[] { "SMTP server is available", "Database supports the schema" },
        testingStrategy = "Unit tests for service logic, integration tests for email flow"
    });

    public static string MalformedPlanningResponse => "This is not valid JSON at all - just plain text analysis";

    public static string PlanningResponseInCodeFences => $"```json\n{ValidPlanningResponse}\n```";

    #endregion

    #region Coding Agent Responses

    public static string ValidCodingResponse => JsonSerializer.Serialize(new[]
    {
        new
        {
            relativePath = "src/Services/RegistrationService.cs",
            content = "namespace MyApp.Services;\n\npublic class RegistrationService\n{\n    public Task<bool> RegisterAsync(string email) => Task.FromResult(true);\n}",
            isNew = true
        },
        new
        {
            relativePath = "src/Models/User.cs",
            content = "namespace MyApp.Models;\n\npublic record User(int Id, string Email, bool IsVerified);",
            isNew = true
        }
    });

    public static string MalformedCodingResponse => "Here is some code:\npublic class Foo { }";

    #endregion

    #region Testing Agent Responses

    public static string ValidTestingResponse => JsonSerializer.Serialize(new
    {
        testCases = new[]
        {
            new { name = "Register_ValidEmail_ReturnsTrue", type = "Unit", priority = "High", description = "Tests successful registration", expectedResult = "Returns true" },
            new { name = "Register_DuplicateEmail_ReturnsFalse", type = "Unit", priority = "High", description = "Tests duplicate email handling", expectedResult = "Returns false" }
        },
        testFiles = new[]
        {
            new
            {
                relativePath = "tests/RegistrationServiceTests.cs",
                content = "using Xunit;\n\npublic class RegistrationServiceTests\n{\n    [Fact]\n    public void Register_ValidEmail_ReturnsTrue() { Assert.True(true); }\n}",
                isNew = true
            }
        }
    });

    #endregion

    #region Review Agent Responses

    public static string ValidReviewResponse(int score = 85, string recommendation = "Approve") =>
        JsonSerializer.Serialize(new
        {
            score,
            recommendation,
            summary = "Code is well-structured with good separation of concerns.",
            criticalIssues = Array.Empty<object>(),
            highIssues = Array.Empty<object>(),
            mediumIssues = new[] { new { issue = "Consider adding input validation" } },
            lowIssues = new[] { new { issue = "Add XML documentation" } },
            positiveFindings = new[] { "Good use of dependency injection", "Clean naming conventions" }
        });

    public static string LowScoreReviewResponse => ValidReviewResponse(score: 40, recommendation: "Request Changes");

    public static string ReviewWithCriticalIssues => JsonSerializer.Serialize(new
    {
        score = 30,
        recommendation = "Reject",
        summary = "Critical security issues found.",
        criticalIssues = new[] { new { line = 42, issue = "SQL injection vulnerability", fix = "Use parameterized queries", code = "var sql = $\"SELECT * FROM users WHERE id = {id}\";" } },
        highIssues = new[] { new { line = 15, issue = "Hardcoded API key", fix = "Move to configuration" } },
        mediumIssues = Array.Empty<object>(),
        lowIssues = Array.Empty<object>(),
        positiveFindings = Array.Empty<string>()
    });

    #endregion

    #region Documentation Agent Responses

    public static string ValidDocumentationResponse => JsonSerializer.Serialize(new
    {
        overview = "User registration feature with email verification support.",
        changes = "## Changes\n- Added RegistrationService\n- Added User model",
        apiDocs = "## API\n### POST /api/auth/register\nRegisters a new user.",
        usageExamples = "```csharp\nawait registrationService.RegisterAsync(\"user@example.com\");\n```",
        breakingChanges = (string?)null,
        migrationGuide = (string?)null,
        configurationChanges = "Add SMTP settings to appsettings.json"
    });

    #endregion

    #region Sample Work Item Data

    public static Core.Models.StoryWorkItem SampleWorkItem(
        int id = 12345,
        string title = "Implement user registration",
        string? description = "As a user, I want to register with my email so I can access the system.",
        string? acceptanceCriteria = "- Users can register with email\n- Email verification is sent\n- Duplicate emails are rejected",
        string state = "Story Planning",
        int autonomyLevel = 3,
        int minimumReviewScore = 85)
    {
        return new Core.Models.StoryWorkItem
        {
            Id = id,
            Title = title,
            Description = description,
            AcceptanceCriteria = acceptanceCriteria,
            State = state,
            AssignedTo = "TestUser",
            AreaPath = "MyProject\\Features",
            IterationPath = "MyProject\\Sprint 1",
            StoryPoints = 5,
            Tags = ["registration", "auth"],
            CreatedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ChangedDate = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            AutonomyLevel = autonomyLevel,
            MinimumReviewScore = minimumReviewScore
        };
    }

    /// <summary>Work item with empty/null fields for edge case testing.</summary>
    public static Core.Models.StoryWorkItem EmptyWorkItem(int id = 99999) => new()
    {
        Id = id,
        Title = "Empty story",
        Description = null,
        AcceptanceCriteria = null,
        State = "New",
        Tags = [],
        CreatedDate = DateTime.UtcNow,
        ChangedDate = DateTime.UtcNow
    };

    #endregion

    #region Sample Story State

    public static Core.Models.StoryState SampleState(int workItemId = 12345, string currentState = "Story Planning")
    {
        return new Core.Models.StoryState
        {
            WorkItemId = workItemId,
            CurrentState = currentState,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = DateTime.UtcNow
        };
    }

    /// <summary>State after Planning + Coding + Testing + Review agents have completed.</summary>
    public static Core.Models.StoryState CompletedPipelineState(int reviewScore = 90, int prId = 42, int workItemId = 12345)
    {
        var state = new Core.Models.StoryState
        {
            WorkItemId = workItemId,
            CurrentState = "AI Deployment",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = DateTime.UtcNow
        };

        state.Agents["Planning"] = Core.Models.AgentStatus.Completed();
        state.Agents["Coding"] = Core.Models.AgentStatus.Completed();
        state.Agents["Testing"] = Core.Models.AgentStatus.Completed();
        state.Agents["Review"] = Core.Models.AgentStatus.Completed();
        state.Agents["Review"].AdditionalData = new Dictionary<string, object>
        {
            ["score"] = reviewScore,
            ["recommendation"] = "Approve"
        };
        state.Agents["Documentation"] = Core.Models.AgentStatus.Completed();
        state.Agents["Documentation"].AdditionalData = new Dictionary<string, object>
        {
            ["prId"] = prId
        };

        state.Artifacts.Code.Add("src/Services/RegistrationService.cs");
        state.Artifacts.Code.Add("src/Models/User.cs");
        state.Artifacts.Tests.Add("tests/RegistrationServiceTests.cs");

        state.TokenUsage.RecordUsage("Planning", new Core.Models.TokenUsageData
        {
            InputTokens = 2000, OutputTokens = 1500, TotalTokens = 3500,
            EstimatedCost = 0.02m, Model = "gpt-4o"
        });
        state.TokenUsage.RecordUsage("Coding", new Core.Models.TokenUsageData
        {
            InputTokens = 3000, OutputTokens = 2500, TotalTokens = 5500,
            EstimatedCost = 0.04m, Model = "gpt-4o"
        });

        return state;
    }

    #endregion
}
