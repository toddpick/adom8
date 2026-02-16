using System.Text.Json;
using AIAgents.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AIAgents.Functions.Agents;

/// <summary>
/// Executes tool calls from the agentic coding loop against the actual repository.
/// Tools: read_file, write_file, list_files, search_code
/// </summary>
public sealed class CodingToolExecutor
{
    private readonly IGitOperations _gitOps;
    private readonly string _repoPath;
    private readonly ILogger _logger;
    private readonly HashSet<string> _modifiedFiles = new(StringComparer.OrdinalIgnoreCase);

    public CodingToolExecutor(IGitOperations gitOps, string repoPath, ILogger logger)
    {
        _gitOps = gitOps;
        _repoPath = repoPath;
        _logger = logger;
    }

    /// <summary>Files that were modified or created during the agentic loop.</summary>
    public IReadOnlySet<string> ModifiedFiles => _modifiedFiles;

    /// <summary>
    /// Returns the tool definitions for Claude's tool-use API.
    /// </summary>
    public static IReadOnlyList<ToolDefinition> GetToolDefinitions() =>
    [
        new ToolDefinition
        {
            Name = "read_file",
            Description = "Read the contents of a file in the repository. Returns the file content as text.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Relative path to the file (e.g., 'src/Services/MyService.cs')" }
                },
                required = new[] { "path" }
            }
        },
        new ToolDefinition
        {
            Name = "write_file",
            Description = "Write content to a file in the repository. Creates the file if it doesn't exist, overwrites if it does. Use this for both new files and full-file replacements.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Relative path to the file (e.g., 'src/Services/MyService.cs')" },
                    content = new { type = "string", description = "The complete file content to write" }
                },
                required = new[] { "path", "content" }
            }
        },
        new ToolDefinition
        {
            Name = "list_files",
            Description = "List all files in the repository, or files matching a directory prefix. Returns one file path per line.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    directory = new { type = "string", description = "Optional directory prefix to filter files (e.g., 'src/Services/'). Leave empty for all files." }
                },
                required = Array.Empty<string>()
            }
        },
        new ToolDefinition
        {
            Name = "search_code",
            Description = "Search for a text pattern across all files in the repository. Returns matching lines with file paths and line numbers. Case-insensitive.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    pattern = new { type = "string", description = "Text or pattern to search for across the codebase" },
                    file_extension = new { type = "string", description = "Optional file extension filter (e.g., '.cs', '.json'). Leave empty to search all files." }
                },
                required = new[] { "pattern" }
            }
        },
        new ToolDefinition
        {
            Name = "edit_file",
            Description = "Apply a search-and-replace edit to an existing file. The search text must exactly match text in the file. More precise than write_file for small changes.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Relative path to the file to edit" },
                    search = new { type = "string", description = "Exact text to find in the file (must match exactly)" },
                    replace = new { type = "string", description = "Text to replace the search text with" }
                },
                required = new[] { "path", "search", "replace" }
            }
        }
    ];

    /// <summary>
    /// Execute a tool call and return the result as a string.
    /// </summary>
    public async Task<string> ExecuteAsync(ToolCall toolCall, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(toolCall.InputJson);
        var input = doc.RootElement;

        return toolCall.Name switch
        {
            "read_file" => await ReadFileAsync(input, cancellationToken),
            "write_file" => await WriteFileAsync(input, cancellationToken),
            "list_files" => await ListFilesAsync(input, cancellationToken),
            "search_code" => await SearchCodeAsync(input, cancellationToken),
            "edit_file" => await EditFileAsync(input, cancellationToken),
            _ => $"Unknown tool: {toolCall.Name}"
        };
    }

    private async Task<string> ReadFileAsync(JsonElement input, CancellationToken ct)
    {
        var path = input.GetProperty("path").GetString()!;
        var content = await _gitOps.ReadFileAsync(_repoPath, path, ct);

        if (content is null)
            return $"Error: File not found: {path}";

        _logger.LogDebug("Tool read_file: {Path} ({Length} chars)", path, content.Length);
        return content;
    }

    private async Task<string> WriteFileAsync(JsonElement input, CancellationToken ct)
    {
        var path = input.GetProperty("path").GetString()!;
        var content = input.GetProperty("content").GetString()!;

        await _gitOps.WriteFileAsync(_repoPath, path, content, ct);
        _modifiedFiles.Add(path);

        _logger.LogInformation("Tool write_file: {Path} ({Length} chars)", path, content.Length);
        return $"Successfully wrote {content.Length} characters to {path}";
    }

    private async Task<string> ListFilesAsync(JsonElement input, CancellationToken ct)
    {
        var directory = input.TryGetProperty("directory", out var d) ? d.GetString() : null;
        var allFiles = await _gitOps.ListFilesAsync(_repoPath, ct);

        var filtered = string.IsNullOrEmpty(directory)
            ? allFiles
            : allFiles.Where(f => f.Replace('\\', '/').StartsWith(directory.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)).ToList();

        if (filtered.Count == 0)
            return directory != null
                ? $"No files found matching directory: {directory}"
                : "Repository is empty.";

        _logger.LogDebug("Tool list_files: {Count} files (filter: {Dir})", filtered.Count, directory ?? "(all)");

        // Limit output to avoid huge context
        var result = filtered.Count > 200
            ? string.Join("\n", filtered.Take(200)) + $"\n\n[... and {filtered.Count - 200} more files]"
            : string.Join("\n", filtered);

        return result;
    }

    private async Task<string> SearchCodeAsync(JsonElement input, CancellationToken ct)
    {
        var pattern = input.GetProperty("pattern").GetString()!;
        var extFilter = input.TryGetProperty("file_extension", out var ef) ? ef.GetString() : null;

        var allFiles = await _gitOps.ListFilesAsync(_repoPath, ct);

        if (!string.IsNullOrEmpty(extFilter))
            allFiles = allFiles.Where(f => f.EndsWith(extFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        var matches = new List<string>();
        var filesSearched = 0;

        foreach (var file in allFiles.Take(100)) // Cap to prevent excessive I/O
        {
            var content = await _gitOps.ReadFileAsync(_repoPath, file, ct);
            if (content is null) continue;
            filesSearched++;

            var lines = content.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add($"{file}:{i + 1}: {lines[i].Trim()}");
                    if (matches.Count >= 50) break; // Cap results
                }
            }
            if (matches.Count >= 50) break;
        }

        _logger.LogDebug("Tool search_code: '{Pattern}' → {Count} matches across {Files} files",
            pattern, matches.Count, filesSearched);

        if (matches.Count == 0)
            return $"No matches found for '{pattern}' across {filesSearched} files.";

        var result = string.Join("\n", matches);
        if (matches.Count >= 50)
            result += "\n\n[Results capped at 50 matches]";

        return result;
    }

    private async Task<string> EditFileAsync(JsonElement input, CancellationToken ct)
    {
        var path = input.GetProperty("path").GetString()!;
        var search = input.GetProperty("search").GetString()!;
        var replace = input.GetProperty("replace").GetString()!;

        var content = await _gitOps.ReadFileAsync(_repoPath, path, ct);
        if (content is null)
            return $"Error: File not found: {path}";

        if (!content.Contains(search))
            return $"Error: Search text not found in {path}. Make sure the search text matches exactly (including whitespace).";

        var occurrences = CountOccurrences(content, search);
        var newContent = content.Replace(search, replace);
        await _gitOps.WriteFileAsync(_repoPath, path, newContent, ct);
        _modifiedFiles.Add(path);

        _logger.LogInformation("Tool edit_file: {Path} ({Occurrences} replacement(s))", path, occurrences);
        return $"Successfully edited {path}: replaced {occurrences} occurrence(s).";
    }

    private static int CountOccurrences(string text, string search)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(search, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += search.Length;
        }
        return count;
    }
}
