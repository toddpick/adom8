using System.Text.Json.Serialization;

namespace AIAgents.Core.Models;

/// <summary>
/// Request body for POST /api/analyze-codebase.
/// </summary>
public sealed class AnalyzeCodebaseRequest
{
    [JsonPropertyName("userStoryTimeframe")]
    public string UserStoryTimeframe { get; init; } = "6months";

    [JsonPropertyName("analysisDepth")]
    public string AnalysisDepth { get; init; } = "standard";

    [JsonPropertyName("includeGitHistory")]
    public bool IncludeGitHistory { get; init; } = true;

    [JsonPropertyName("incremental")]
    public bool Incremental { get; init; } = false;
}

/// <summary>
/// Response for POST /api/analyze-codebase.
/// </summary>
public sealed class AnalyzeCodebaseResponse
{
    [JsonPropertyName("workItemId")]
    public int WorkItemId { get; init; }

    [JsonPropertyName("estimatedDuration")]
    public string EstimatedDuration { get; init; } = "15 minutes";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "queued";
}

/// <summary>
/// Metadata stored in .agent/metadata.json tracking last analysis state.
/// </summary>
public sealed class CodebaseAnalysisMetadata
{
    [JsonPropertyName("lastAnalysis")]
    public DateTime? LastAnalysis { get; set; }

    [JsonPropertyName("lastCommitHash")]
    public string? LastCommitHash { get; set; }

    [JsonPropertyName("filesAnalyzed")]
    public int FilesAnalyzed { get; set; }

    [JsonPropertyName("linesOfCode")]
    public long LinesOfCode { get; set; }

    [JsonPropertyName("userStoriesReviewed")]
    public int UserStoriesReviewed { get; set; }

    [JsonPropertyName("commitsAnalyzed")]
    public int CommitsAnalyzed { get; set; }

    [JsonPropertyName("featuresDocumented")]
    public int FeaturesDocumented { get; set; }

    [JsonPropertyName("languagesDetected")]
    public List<string> LanguagesDetected { get; set; } = new();

    [JsonPropertyName("primaryFramework")]
    public string? PrimaryFramework { get; set; }

    [JsonPropertyName("documentationSizeKB")]
    public long DocumentationSizeKB { get; set; }

    [JsonPropertyName("featuresDocumentedList")]
    public List<string> FeaturesDocumentedList { get; set; } = new();
}

/// <summary>
/// Response for GET /api/codebase-intelligence.
/// </summary>
public sealed class CodebaseIntelligenceResponse
{
    [JsonPropertyName("lastAnalysis")]
    public DateTime? LastAnalysis { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "not_analyzed";

    [JsonPropertyName("stats")]
    public CodebaseAnalysisMetadata? Stats { get; init; }

    [JsonPropertyName("recommendReanalysis")]
    public bool RecommendReanalysis { get; init; }
}
