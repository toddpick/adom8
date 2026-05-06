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

    /// <summary>
    /// Structured spec authored by the Planning agent. Captures the
    /// interfaces/components touched, expected behavior, and acceptance
    /// criteria. Rendered into SPEC.md alongside PLAN.md/TASKS.md.
    /// When null, SPEC.md is rendered from the existing plan fields only.
    /// </summary>
    public PlanningSpec? Spec { get; init; }

    /// <summary>
    /// Items the Planning agent could not resolve from the inputs it has
    /// (story description, CODEBASE_CONTEXT.md, referenced skill files).
    /// Rendered into AMBIGUITIES.md. An empty list is the well-defined case.
    /// </summary>
    public IReadOnlyList<PlanningAmbiguity> Ambiguities { get; init; } = [];
}

/// <summary>
/// Structured spec output from the Planning agent. Used to render SPEC.md
/// for the Coding agent to reference during implementation.
/// </summary>
public sealed record PlanningSpec
{
    /// <summary>One-paragraph summary of what is being built and why.</summary>
    public string Summary { get; init; } = "";

    /// <summary>Interfaces, classes, services, or UI components that will be
    /// added or modified.</summary>
    public IReadOnlyList<string> Components { get; init; } = [];

    /// <summary>Concrete behaviors the implementation must exhibit
    /// (e.g., "POST /api/x returns 201 with body Y").</summary>
    public IReadOnlyList<string> Behaviors { get; init; } = [];

    /// <summary>Acceptance criteria, normalized into checkable bullets.
    /// When empty, the rendered SPEC.md falls back to the raw story
    /// acceptance criteria.</summary>
    public IReadOnlyList<string> AcceptanceCriteria { get; init; } = [];

    /// <summary>Items the Coding agent should explicitly NOT do.</summary>
    public IReadOnlyList<string> NonGoals { get; init; } = [];
}

/// <summary>
/// A single ambiguity surfaced by the Planning agent: a thing it does not
/// know with confidence and could not derive from the available inputs.
/// </summary>
public sealed record PlanningAmbiguity
{
    /// <summary>Short label naming the ambiguity (e.g., "Auth scheme",
    /// "Retry policy").</summary>
    public required string Topic { get; init; }

    /// <summary>The unresolved question, framed for a human reviewer.</summary>
    public required string Question { get; init; }

    /// <summary>Where the gap lives: "story", "codebase", "skill", or
    /// "external".</summary>
    public string Source { get; init; } = "story";

    /// <summary>Working assumption to use if the Coding agent is allowed to
    /// proceed without clarification (autonomy ≥ 4). May be empty.</summary>
    public string Assumption { get; init; } = "";
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
