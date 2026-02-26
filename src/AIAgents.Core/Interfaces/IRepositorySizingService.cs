using AIAgents.Core.Models;

namespace AIAgents.Core.Interfaces;

/// <summary>
/// Estimates repository size and determines whether clone-heavy execution should proceed.
/// </summary>
public interface IRepositorySizingService
{
    Task<RepositorySizingResult> EvaluateAsync(CancellationToken cancellationToken = default);
}
