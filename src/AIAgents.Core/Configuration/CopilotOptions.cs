namespace AIAgents.Core.Configuration;

/// <summary>
/// Configuration options for GitHub Copilot coding agent integration.
/// Bound to the "Copilot" configuration section.
/// 
/// When enabled, the Coding agent can delegate coding work to GitHub Copilot's
/// coding agent instead of using the built-in agentic tool-use loop. Two modes:
/// <list type="bullet">
///   <item><c>Auto</c> (default) — delegates only when story points ≥ <see cref="ComplexityThreshold"/></item>
///   <item><c>Always</c> — sends ALL coding work to Copilot (no API key needed for the Coding agent)</item>
/// </list>
/// 
/// A webhook bridge (<c>CopilotBridgeWebhook</c>) catches Copilot's PR, reconciles
/// changes onto the pipeline branch, and resumes the sequential pipeline.
/// </summary>
public sealed class CopilotOptions
{
    public const string SectionName = "Copilot";

    /// <summary>
    /// Master switch — enables the Copilot coding agent as an alternative coding strategy.
    /// When false (default), all stories use the built-in agentic loop regardless of <see cref="Mode"/>.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Routing mode when <see cref="Enabled"/> is true.
    /// <list type="bullet">
    ///   <item><c>"Auto"</c> (default) — delegates to Copilot only when story points ≥ <see cref="ComplexityThreshold"/>;
    ///     simpler stories stay on the built-in agentic loop.</item>
    ///   <item><c>"Always"</c> — sends every story to Copilot regardless of complexity.
    ///     Ideal when you have a GitHub Copilot subscription but no AI API key for the Coding agent.</item>
    /// </list>
    /// </summary>
    public string Mode { get; init; } = "Auto";

    /// <summary>
    /// Story-point threshold at or above which complex stories are automatically delegated
    /// to Copilot. Only applies when <see cref="Mode"/> is <c>"Auto"</c>.
    /// Default: 8 (delegates 8+ SP stories, keeps ≤7 SP on the built-in loop).
    /// </summary>
    public int ComplexityThreshold { get; init; } = 8;

    /// <summary>
    /// Whether to auto-create an ephemeral GitHub Issue and assign it to <c>@copilot</c>
    /// to trigger the Copilot coding agent. The issue is automatically closed after
    /// the Copilot PR is received and reconciled.
    /// 
    /// Set to false if your organization doesn't allow GitHub Issues on the repository.
    /// When false, the pipeline pauses and logs a manual-action-required message.
    /// </summary>
    public bool CreateIssue { get; init; } = true;

    /// <summary>
    /// GitHub webhook secret used to validate <c>X-Hub-Signature-256</c> on incoming
    /// <c>pull_request</c> events from the Copilot bridge webhook.
    /// Generate with: <c>openssl rand -hex 32</c>
    /// </summary>
    public string? WebhookSecret { get; init; }

    /// <summary>
    /// Maximum minutes to wait for Copilot to produce a PR before falling back
    /// to the built-in agentic loop. Checked by the <c>CopilotTimeoutChecker</c>
    /// timer function every 5 minutes.
    /// Default: 30 minutes.
    /// </summary>
    public int TimeoutMinutes { get; init; } = 30;

    /// <summary>
    /// Whether to automatically close Copilot's PR after reconciling changes
    /// onto the pipeline branch. When true (default), Copilot's PR is closed with
    /// a comment explaining that changes were incorporated into the pipeline.
    /// </summary>
    public bool AutoCloseCopilotPr { get; init; } = true;

    /// <summary>
    /// GitHub agent to assign issues to. Controls which coding agent processes the work.
    /// <list type="bullet">
    ///   <item><c>"copilot"</c> (default) — GitHub Copilot coding agent</item>
    ///   <item><c>"claude"</c> — Anthropic Claude coding agent (partner agent)</item>
    ///   <item><c>"codex"</c> — OpenAI Codex coding agent (partner agent)</item>
    /// </list>
    /// Can be overridden per-story via the <c>Custom.AICodingProvider</c> ADO field.
    /// </summary>
    public string Model { get; init; } = "copilot";

    /// <summary>
    /// Enables strict Azure DevOps checkpoint enforcement for Copilot completion handoff.
    /// When enabled, the Copilot bridge verifies required work item updates before enqueueing Review.
    /// </summary>
    public bool CheckpointEnforcementEnabled { get; init; } = true;

    /// <summary>
    /// Controls behavior when checkpoint enforcement fails.
    /// When true (default), handoff fails hard and Review is not enqueued.
    /// </summary>
    public bool CheckpointFailHard { get; init; } = true;

    /// <summary>
    /// Comma-separated required ADO checkpoints for Copilot completion handoff.
    /// Supported values: LastAgent, CurrentAIAgent, CompletionComment.
    /// </summary>
    public string RequiredAdoCheckpoints { get; init; } = "LastAgent,CurrentAIAgent,CompletionComment";
}
