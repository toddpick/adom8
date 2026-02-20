namespace AIAgents.Core.Constants;

/// <summary>
/// Shared Azure DevOps state and field value names used by the AI agent pipeline.
/// </summary>
public static class AIPipelineNames
{
    /// <summary>
    /// Single active Azure DevOps state used while AI agents are processing a story.
    /// </summary>
    public const string ProcessingState = "AI Agent";

    public static class CurrentAgentValues
    {
        public const string Planning = "Planning Agent";
        public const string Coding = "Coding Agent";
        public const string Testing = "Testing Agent";
        public const string Review = "Review Agent";
        public const string Documentation = "Documentation Agent";
        public const string Deployment = "Deployment Agent";
    }
}
