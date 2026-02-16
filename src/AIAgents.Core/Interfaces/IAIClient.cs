using AIAgents.Core.Models;

namespace AIAgents.Core.Interfaces;

/// <summary>
/// Thin AI completion client. Agents own all prompt engineering;
/// this interface only handles the API transport.
/// </summary>
public interface IAIClient
{
    /// <summary>
    /// Sends a completion request to the configured AI provider.
    /// </summary>
    Task<AICompletionResult> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        AICompletionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a multi-turn agentic conversation with tool use.
    /// Claude calls tools iteratively until it responds with end_turn.
    /// </summary>
    Task<AgenticResult> CompleteWithToolsAsync(
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<ToolDefinition> tools,
        Func<ToolCall, CancellationToken, Task<string>> toolExecutor,
        AgenticOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Per-request overrides for AI completion behavior.
/// </summary>
public sealed class AICompletionOptions
{
    public int? MaxTokens { get; init; }
    public double? Temperature { get; init; }
    public string? ResponseFormat { get; init; }
}

/// <summary>
/// Options for agentic (tool-use) conversations.
/// </summary>
public sealed class AgenticOptions
{
    /// <summary>Maximum tool-use rounds before stopping (default 15).</summary>
    public int MaxRounds { get; init; } = 15;

    /// <summary>Max tokens per AI response (default 8192).</summary>
    public int MaxTokens { get; init; } = 8192;

    /// <summary>Temperature (default 0.2).</summary>
    public double Temperature { get; init; } = 0.2;
}

/// <summary>
/// Defines a tool that the AI can call during an agentic conversation.
/// </summary>
public sealed class ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required object InputSchema { get; init; }
}

/// <summary>
/// Represents a tool call request from the AI.
/// </summary>
public sealed class ToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string InputJson { get; init; }
}

/// <summary>
/// Result of a multi-turn agentic conversation.
/// </summary>
public sealed class AgenticResult
{
    /// <summary>The final text response from the AI (if any).</summary>
    public string? FinalResponse { get; init; }

    /// <summary>Total rounds of tool use executed.</summary>
    public int RoundsExecuted { get; init; }

    /// <summary>Aggregated token usage across all rounds.</summary>
    public TokenUsageData? TotalUsage { get; init; }

    /// <summary>Whether the conversation ended normally (end_turn) vs hitting max rounds.</summary>
    public bool CompletedNaturally { get; init; }

    /// <summary>Log of all tool calls made during the conversation.</summary>
    public List<ToolCallLog> ToolCalls { get; init; } = [];
}

/// <summary>
/// Log entry for a single tool call.
/// </summary>
public sealed class ToolCallLog
{
    public required string ToolName { get; init; }
    public required string Input { get; init; }
    public int Round { get; init; }
}
