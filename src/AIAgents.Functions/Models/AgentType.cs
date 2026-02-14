namespace AIAgents.Functions.Models;

/// <summary>
/// Identifies the type of agent to execute in the pipeline.
/// Order reflects the sequential pipeline flow.
/// </summary>
public enum AgentType
{
    /// <summary>
    /// Analyzes the story and creates an implementation plan.
    /// Transition: New → Story Planning
    /// </summary>
    Planning = 0,

    /// <summary>
    /// Generates source code based on the plan.
    /// Transition: Story Planning → AI Code
    /// </summary>
    Coding = 1,

    /// <summary>
    /// Generates test cases and test code.
    /// Transition: AI Code → AI Test
    /// </summary>
    Testing = 2,

    /// <summary>
    /// Reviews generated code for quality, security, and best practices.
    /// Transition: AI Test → AI Review
    /// </summary>
    Review = 3,

    /// <summary>
    /// Generates documentation for the changes.
    /// Transition: AI Review → AI Docs
    /// </summary>
    Documentation = 4,

    /// <summary>
    /// Handles merge, deployment, and autonomy-level decisions.
    /// Transition: AI Docs → Ready for QA / Deployed (based on autonomy level)
    /// </summary>
    Deployment = 5,

    /// <summary>
    /// Analyzes codebase, user stories, and git history to generate
    /// AI-optimized documentation in the .agent/ folder.
    /// Triggered via dashboard button, not through the normal story pipeline.
    /// </summary>
    CodebaseDocumentation = 6
}
