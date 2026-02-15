using AIAgents.Core.Models;

namespace AIAgents.Core.Interfaces;

/// <summary>
/// Factory that resolves the correct <see cref="IAIClient"/> for a given agent,
/// allowing per-agent model/provider overrides. Agents that share the same
/// effective configuration receive the same cached <see cref="IAIClient"/> instance.
/// </summary>
public interface IAIClientFactory
{
    /// <summary>
    /// Returns an <see cref="IAIClient"/> configured for the specified agent.
    /// If the agent has per-agent overrides in <c>AI:AgentModels:{agentName}</c>,
    /// those settings are merged with the defaults. Otherwise the default
    /// <see cref="IAIClient"/> is returned.
    /// </summary>
    /// <param name="agentName">
    /// The agent name, matching the keyed DI key
    /// (e.g., "Planning", "Coding", "Testing", "Review", "Documentation").
    /// </param>
    /// <param name="storyOverrides">
    /// Optional per-story model overrides from a work item's custom fields.
    /// When provided, these take priority over config-level defaults.
    /// Resolution order: story per-agent field → story tier → config per-agent → global default.
    /// </param>
    IAIClient GetClientForAgent(string agentName, StoryModelOverrides? storyOverrides = null);
}
