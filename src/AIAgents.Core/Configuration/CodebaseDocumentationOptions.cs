namespace AIAgents.Core.Configuration;

/// <summary>
/// Configuration for the CodebaseDocumentation agent.
/// Bound to the "CodebaseDocumentation" configuration section.
/// </summary>
public sealed class CodebaseDocumentationOptions
{
    public const string SectionName = "CodebaseDocumentation";

    /// <summary>Maximum files the scanner will index (avoids runaway cost on huge repos).</summary>
    public int MaxFilesToAnalyze { get; init; } = 10_000;

    /// <summary>Maximum completed user stories to pull from ADO.</summary>
    public int MaxUserStories { get; init; } = 500;

    /// <summary>Maximum git commits to analyze.</summary>
    public int MaxCommits { get; init; } = 1_000;

    /// <summary>Default look-back window for user stories and commits.</summary>
    public string DefaultTimeframe { get; init; } = "6months";

    /// <summary>
    /// Glob-style patterns of directories/files to exclude from scanning.
    /// </summary>
    public List<string> ExcludePatterns { get; init; } = new()
    {
        "node_modules/",
        "bin/",
        "obj/",
        ".git/",
        "packages/",
        ".vs/",
        ".idea/",
        "dist/",
        "build/",
        "*.min.js",
        "*.min.css",
        "*.dll",
        "*.exe",
        "*.png",
        "*.jpg",
        "*.gif",
        "*.ico",
        "*.svg",
        "*.woff",
        "*.woff2",
        "*.ttf",
        "*.eot"
    };

    /// <summary>
    /// Keywords used to cluster user stories into features.
    /// </summary>
    public List<string> FeatureDetectionKeywords { get; init; } = new()
    {
        "authentication", "auth", "login", "sso",
        "payment", "checkout", "billing", "invoice",
        "notification", "email", "sms", "push",
        "reporting", "dashboard", "analytics", "metrics",
        "user", "profile", "account", "registration",
        "search", "filter", "sort",
        "admin", "settings", "configuration",
        "api", "integration", "webhook",
        "deployment", "ci/cd", "pipeline",
        "database", "migration", "schema"
    };

    /// <summary>
    /// Folder in the repository root where generated documentation is committed.
    /// </summary>
    public string OutputFolder { get; init; } = ".agent";
}
