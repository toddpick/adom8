namespace AIAgents.Core.Models;

/// <summary>
/// Result of AI planning analysis, parsed from the AI response.
/// Includes a readiness assessment that acts as a quality gate
/// before the story proceeds to the Coding agent.
/// </summary>
public sealed record PlanningResult
{
    public required string ProblemAnalysis { get; init; }
    public required string TechnicalApproach { get; init; }
    public required IReadOnlyList<string> AffectedFiles { get; init; }
    public required int Complexity { get; init; }
    public required string Architecture { get; init; }
    public required IReadOnlyList<string> SubTasks { get; init; }
    public required IReadOnlyList<string> Dependencies { get; init; }
    public required IReadOnlyList<string> Risks { get; init; }
    public required IReadOnlyList<string> Assumptions { get; init; }
    public required string TestingStrategy { get; init; }

    /// <summary>
    /// Readiness assessment from the planning triage gate.
    /// When null, the story is assumed ready (backward compatibility).
    /// </summary>
    public PlanningReadiness? Readiness { get; init; }
}

/// <summary>
/// Triage assessment that determines whether a story is ready for coding.
/// The Planning agent evaluates 7 dimensions: Completeness, Complexity,
/// Ambiguity, Risk, Feasibility, Content Quality, and Unverified Assumptions.
/// </summary>
public sealed record PlanningReadiness
{
    /// <summary>Whether the story should proceed to coding.</summary>
    public required bool Proceed { get; init; }

    /// <summary>Overall readiness score 0-100.</summary>
    public int ReadinessScore { get; init; } = 100;

    /// <summary>Blocking issues that prevent the story from proceeding.</summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];

    /// <summary>Questions the analyst needs to answer before coding can begin.</summary>
    public IReadOnlyList<string> Questions { get; init; } = [];

    /// <summary>Unverified external API assumptions that require research or investigation.</summary>
    public IReadOnlyList<string> ResearchNeeded { get; init; } = [];

    /// <summary>Suggested story breakdown if the story is too complex.</summary>
    public IReadOnlyList<string> SuggestedBreakdown { get; init; } = [];

    /// <summary>Reason for the proceed/reject decision.</summary>
    public string Reason { get; init; } = "";
}
