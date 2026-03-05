using System.Text.Json;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using Microsoft.Extensions.Logging;

namespace AIAgents.Core.Services;

/// <summary>
/// Provides access to a story's working directory and state file.
/// State is persisted as .ado/stories/US-{id}/state.json with atomic writes.
/// </summary>
public sealed class StoryContext : IStoryContext
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger<StoryContext> _logger;

    public int WorkItemId { get; }
    public string StoryDirectory { get; }

    internal StoryContext(int workItemId, string repositoryPath, ILogger<StoryContext> logger)
    {
        WorkItemId = workItemId;
        _logger = logger;
        var root = string.IsNullOrWhiteSpace(repositoryPath)
            ? Path.Combine(Path.GetTempPath(), "ado-agent-stories")
            : repositoryPath;
        StoryDirectory = Path.Combine(root, ".ado", "stories", $"US-{workItemId}");

        Directory.CreateDirectory(StoryDirectory);
    }

    public async Task<StoryState> LoadStateAsync(CancellationToken cancellationToken = default)
    {
        var stateFile = Path.Combine(StoryDirectory, "state.json");

        if (!File.Exists(stateFile))
        {
            _logger.LogDebug("No state file found for US-{WorkItemId}, creating default", WorkItemId);
            var defaultState = new StoryState
            {
                WorkItemId = WorkItemId,
                CurrentState = "New"
            };
            await SaveStateAsync(defaultState, cancellationToken);
            return defaultState;
        }

        var json = await File.ReadAllTextAsync(stateFile, cancellationToken);
        StoryState? state;
        try
        {
            state = JsonSerializer.Deserialize<StoryState>(json, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Corrupted state file for US-{WorkItemId}, resetting to default", WorkItemId);
            var defaultState = new StoryState
            {
                WorkItemId = WorkItemId,
                CurrentState = "New"
            };
            await SaveStateAsync(defaultState, cancellationToken);
            return defaultState;
        }

        if (state is null)
        {
            _logger.LogWarning("Failed to deserialize state for US-{WorkItemId}, resetting", WorkItemId);
            var defaultState = new StoryState
            {
                WorkItemId = WorkItemId,
                CurrentState = "New"
            };
            await SaveStateAsync(defaultState, cancellationToken);
            return defaultState;
        }

        _logger.LogDebug("Loaded state for US-{WorkItemId}: {CurrentState}", WorkItemId, state.CurrentState);
        return state;
    }

    public async Task SaveStateAsync(StoryState state, CancellationToken cancellationToken = default)
    {
        state.UpdatedAt = DateTime.UtcNow;

        var stateFile = Path.Combine(StoryDirectory, "state.json");
        var tempFile = $"{stateFile}.{Guid.NewGuid():N}.tmp";

        try
        {
            var json = JsonSerializer.Serialize(state, s_jsonOptions);
            await File.WriteAllTextAsync(tempFile, json, cancellationToken);
            File.Move(tempFile, stateFile, overwrite: true);

            _logger.LogDebug("Saved state for US-{WorkItemId}: {CurrentState}", WorkItemId, state.CurrentState);
        }
        catch
        {
            // Clean up temp file on failure
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { /* best effort */ }
            }
            throw;
        }
    }

    public async Task WriteArtifactAsync(string relativePath, string content, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(StoryDirectory, relativePath);
        var directory = Path.GetDirectoryName(fullPath);

        if (directory is not null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(fullPath, content, cancellationToken);
        _logger.LogDebug("Wrote artifact: US-{WorkItemId}/{RelativePath}", WorkItemId, relativePath);
    }

    public async Task<string?> ReadArtifactAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(StoryDirectory, relativePath);

        if (!File.Exists(fullPath))
        {
            return null;
        }

        return await File.ReadAllTextAsync(fullPath, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        // No unmanaged resources, but interface requires implementation.
        // Could flush pending state in future if needed.
        return ValueTask.CompletedTask;
    }
}
