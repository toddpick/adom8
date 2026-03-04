using System.Text.Json;
using AIAgents.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AIAgents.Functions.Agents;

/// <summary>
/// Executes tool calls from the agentic coding loop against the repository via GitHub REST API.
/// File writes are buffered in memory during the loop; the caller commits all changes atomically
/// via <see cref="IGitHubApiContextService.WriteFilesAsync"/> at the end.
/// Tools: read_file, write_file, edit_file, list_files, search_code
/// </summary>
public sealed class CodingToolExecutor
{
    private readonly IGitHubApiContextService _githubContext;
    private readonly string _branch;
    private readonly ILogger _logger;
    private readonly HashSet<string> _modifiedFiles = new(StringComparer.OrdinalIgnoreCase);

    // In-memory file buffer: holds pending writes (path → content)
    private readonly Dictionary<string, string> _writeBuffer = new(StringComparer.OrdinalIgnoreCase);

    // Read-through cache to avoid redundant API calls during the loop
    private readonly Dictionary<string, string?> _readCache = new(StringComparer.OrdinalIgnoreCase);

    public CodingToolExecutor(IGitHubApiContextService githubContext, string branch, ILogger logger)
    {
        _githubContext = githubContext;
        _branch = branch;
        _logger = logger;
    }

    /// <summary>Files that were modified or created during the agentic loop.</summary>
    public IReadOnlySet<string> ModifiedFiles => _modifiedFiles;

    /// <summary>
    /// All pending file writes buffered during the agentic loop.
    /// The caller should commit these atomically after the loop completes.
    /// </summary>
    public IReadOnlyDictionary<string, string> PendingWrites => _writeBuffer;

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
            Description = "Apply a search-and-replace edit to an existing file. The search text must match the actual file content (NOT including line numbers shown by read_file). Copy the exact text from the file for the search parameter. Line endings are normalized automatically.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Relative path to the file to edit" },
                    search = new { type = "string", description = "Exact text to find in the file. Do NOT include the line numbers from read_file output. Copy the actual code/text only." },
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
            "write_file" => WriteFile(input),
            "list_files" => await ListFilesAsync(input, cancellationToken),
            "search_code" => await SearchCodeAsync(input, cancellationToken),
            "edit_file" => await EditFileAsync(input, cancellationToken),
            _ => $"Unknown tool: {toolCall.Name}"
        };
    }

    private async Task<string> ReadFileAsync(JsonElement input, CancellationToken ct)
    {
        var path = input.GetProperty("path").GetString()!;

        // Check write buffer first (AI may have written a file and wants to re-read it)
        string? content;
        if (_writeBuffer.TryGetValue(path, out var buffered))
        {
            content = buffered;
        }
        else if (_readCache.TryGetValue(path, out var cached))
        {
            content = cached;
        }
        else
        {
            var results = await _githubContext.GetFileContentsAsync(_branch, new[] { path }, ct);
            results.TryGetValue(path, out content);
            _readCache[path] = content;
        }

        if (content is null)
            return $"Error: File not found: {path}";

        // Normalize line endings so AI sees consistent \n everywhere
        content = NormalizeLineEndings(content);

        // Add line numbers so AI can reference exact locations
        var lines = content.Split('\n');
        var numbered = new System.Text.StringBuilder();
        for (int i = 0; i < lines.Length; i++)
            numbered.AppendLine($"{i + 1,4}| {lines[i]}");

        _logger.LogDebug("Tool read_file: {Path} ({Lines} lines, {Length} chars)", path, lines.Length, content.Length);
        return numbered.ToString();
    }

    private string WriteFile(JsonElement input)
    {
        var path = input.GetProperty("path").GetString()!;
        var content = input.GetProperty("content").GetString()!;

        _writeBuffer[path] = content;
        _readCache[path] = content; // Keep read cache consistent
        _modifiedFiles.Add(path);

        _logger.LogInformation("Tool write_file (buffered): {Path} ({Length} chars)", path, content.Length);
        return $"Successfully wrote {content.Length} characters to {path}";
    }

    private async Task<string> ListFilesAsync(JsonElement input, CancellationToken ct)
    {
        var directory = input.TryGetProperty("directory", out var d) ? d.GetString() : null;
        var allFiles = await _githubContext.GetFileTreeAsync(_branch, ct);

        var filtered = string.IsNullOrEmpty(directory)
            ? allFiles
            : allFiles.Where(f => f.Replace('\\', '/').StartsWith(directory.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)).ToList();

        // Include any newly buffered files not yet committed
        foreach (var bufferedPath in _writeBuffer.Keys)
        {
            if (!filtered.Contains(bufferedPath, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(directory) ||
                    bufferedPath.Replace('\\', '/').StartsWith(directory.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    filtered = filtered.Append(bufferedPath).ToList();
                }
            }
        }

        if (!filtered.Any())
            return directory != null
                ? $"No files found matching directory: {directory}"
                : "Repository is empty.";

        _logger.LogDebug("Tool list_files: {Count} files (filter: {Dir})", filtered.Count(), directory ?? "(all)");

        var filteredList = filtered.ToList();
        var result = filteredList.Count > 200
            ? string.Join("\n", filteredList.Take(200)) + $"\n\n[... and {filteredList.Count - 200} more files]"
            : string.Join("\n", filteredList);

        return result;
    }

    private async Task<string> SearchCodeAsync(JsonElement input, CancellationToken ct)
    {
        var pattern = input.GetProperty("pattern").GetString()!;
        var extFilter = input.TryGetProperty("file_extension", out var ef) ? ef.GetString() : null;

        var allFiles = await _githubContext.GetFileTreeAsync(_branch, ct);

        if (!string.IsNullOrEmpty(extFilter))
            allFiles = allFiles.Where(f => f.EndsWith(extFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        var matches = new List<string>();
        var filesSearched = 0;

        foreach (var file in allFiles.Take(100)) // Cap to prevent excessive API calls
        {
            string? rawContent;
            if (_writeBuffer.TryGetValue(file, out var buffered))
                rawContent = buffered;
            else if (_readCache.TryGetValue(file, out var cached))
                rawContent = cached;
            else
            {
                var results = await _githubContext.GetFileContentsAsync(_branch, new[] { file }, ct);
                results.TryGetValue(file, out rawContent);
                _readCache[file] = rawContent;
            }

            if (rawContent is null) continue;
            filesSearched++;

            var content = NormalizeLineEndings(rawContent);
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

        // Prefer write buffer (in-flight changes), then read cache, then GitHub API
        string? content;
        if (_writeBuffer.TryGetValue(path, out var buffered))
            content = buffered;
        else if (_readCache.TryGetValue(path, out var cached))
            content = cached;
        else
        {
            var results = await _githubContext.GetFileContentsAsync(_branch, new[] { path }, ct);
            results.TryGetValue(path, out content);
            _readCache[path] = content;
        }

        if (content is null)
            return $"Error: File not found: {path}";

        // Normalize line endings in both content and search to prevent \r\n vs \n mismatches
        content = NormalizeLineEndings(content);
        search = NormalizeLineEndings(search);
        replace = NormalizeLineEndings(replace);

        if (!content.Contains(search))
        {
            // Provide helpful context: show nearby lines that partially match
            var firstLine = search.Split('\n')[0].Trim();
            var hints = new List<string>();
            if (!string.IsNullOrEmpty(firstLine))
            {
                var lines = content.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(firstLine, StringComparison.OrdinalIgnoreCase))
                        hints.Add($"  Line {i + 1}: {lines[i].TrimEnd()}");
                }
            }

            var hint = hints.Count > 0
                ? $"\nPartial matches for '{firstLine}':\n{string.Join("\n", hints.Take(5))}\nTip: Use read_file to see the exact content, then copy the exact text for search."
                : "\nTip: Use read_file to see the exact file content, then use the exact text (without line numbers) as the search string.";

            return $"Error: Search text not found in {path}. The search string ({search.Length} chars, {search.Split('\n').Length} lines) did not match any part of the file.{hint}";
        }

        var occurrences = CountOccurrences(content, search);
        var newContent = content.Replace(search, replace);
        _writeBuffer[path] = newContent;
        _readCache[path] = newContent;
        _modifiedFiles.Add(path);

        _logger.LogInformation("Tool edit_file (buffered): {Path} ({Occurrences} replacement(s))", path, occurrences);
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

    /// <summary>Normalize \r\n → \n to prevent Windows line-ending mismatches in AI tool calls.</summary>
    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n");
}
