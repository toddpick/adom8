using AIAgents.Core.Configuration;
using AIAgents.Core.Constants;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.WebApi.Patch;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIAgents.Core.Services;

/// <summary>
/// Azure DevOps client using the official SDK (Microsoft.TeamFoundationServer.Client).
/// The VssConnection is created once and reused for the lifetime of the service.
/// </summary>
public sealed class AzureDevOpsClient : IAzureDevOpsClient, IDisposable
{
    private readonly AzureDevOpsOptions _options;
    private readonly ILogger<AzureDevOpsClient> _logger;
    private readonly Lazy<VssConnection> _connection;

    public AzureDevOpsClient(
        IOptions<AzureDevOpsOptions> options,
        ILogger<AzureDevOpsClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _connection = new Lazy<VssConnection>(() =>
        {
            var credentials = new VssBasicCredential(string.Empty, _options.Pat);
            return new VssConnection(new Uri(_options.OrganizationUrl), credentials);
        });
    }

    public async Task<StoryWorkItem> GetWorkItemAsync(int workItemId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching work item {WorkItemId}", workItemId);

        var client = await _connection.Value.GetClientAsync<WorkItemTrackingHttpClient>(cancellationToken);

        var workItem = await client.GetWorkItemAsync(
            workItemId,
            expand: WorkItemExpand.All,
            cancellationToken: cancellationToken);

        return MapToStoryWorkItem(workItem);
    }

    public async Task UpdateWorkItemStateAsync(int workItemId, string newState, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Updating work item {WorkItemId} state to '{NewState}'", workItemId, newState);

        var patchDocument = new JsonPatchDocument
        {
            new JsonPatchOperation
            {
                Operation = Operation.Replace,
                Path = "/fields/System.State",
                Value = newState
            }
        };

        var client = await _connection.Value.GetClientAsync<WorkItemTrackingHttpClient>(cancellationToken);
        await client.UpdateWorkItemAsync(patchDocument, workItemId, cancellationToken: cancellationToken);
    }

    public async Task AddWorkItemCommentAsync(int workItemId, string comment, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Adding comment to work item {WorkItemId}", workItemId);

        if (await TryAddCommentViaApiAsync(workItemId, comment, cancellationToken))
        {
            return;
        }

        var patchDocument = new JsonPatchDocument
        {
            new JsonPatchOperation
            {
                Operation = Operation.Add,
                Path = "/fields/System.History",
                Value = comment
            }
        };

        var client = await _connection.Value.GetClientAsync<WorkItemTrackingHttpClient>(cancellationToken);
        await client.UpdateWorkItemAsync(patchDocument, workItemId, cancellationToken: cancellationToken);
    }

    private async Task<bool> TryAddCommentViaApiAsync(int workItemId, string comment, CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = CreateAdoHttpClient();
            var payload = JsonSerializer.Serialize(new { text = comment });

            // Build the absolute URL: {orgUrl}/{project}/_apis/wit/workItems/{id}/comments
            var baseUrl = _options.OrganizationUrl.TrimEnd('/');
            var project = Uri.EscapeDataString(_options.Project);
            var requestUrl = $"{baseUrl}/{project}/_apis/wit/workItems/{workItemId}/comments?api-version=7.1-preview.4";

            using var response = await httpClient.PostAsync(
                requestUrl,
                new StringContent(payload, Encoding.UTF8, "application/json"),
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            _logger.LogWarning(
                "Comments API rejected comment for WI-{WorkItemId} (status: {StatusCode}); falling back to System.History",
                workItemId,
                response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Comments API failed for WI-{WorkItemId}; falling back to System.History",
                workItemId);
            return false;
        }
    }

    public async Task UpdateWorkItemFieldAsync(
        int workItemId,
        string fieldPath,
        object value,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Updating field '{FieldPath}' on work item {WorkItemId}", fieldPath, workItemId);

        var patchDocument = new JsonPatchDocument
        {
            new JsonPatchOperation
            {
                Operation = Operation.Replace,
                Path = fieldPath,
                Value = value
            }
        };

        var client = await _connection.Value.GetClientAsync<WorkItemTrackingHttpClient>(cancellationToken);
        await client.UpdateWorkItemAsync(patchDocument, workItemId, cancellationToken: cancellationToken);
    }

    public async Task UpdateWorkItemFieldsAsync(
        int workItemId,
        IDictionary<string, object> fieldUpdates,
        CancellationToken cancellationToken = default)
    {
        if (fieldUpdates.Count == 0) return;

        _logger.LogDebug("Updating {Count} fields on work item {WorkItemId}", fieldUpdates.Count, workItemId);

        var patchDocument = new JsonPatchDocument();
        foreach (var (path, value) in fieldUpdates)
        {
            patchDocument.Add(new JsonPatchOperation
            {
                Operation = Operation.Replace,
                Path = path,
                Value = value
            });
        }

        var client = await _connection.Value.GetClientAsync<WorkItemTrackingHttpClient>(cancellationToken);
        await client.UpdateWorkItemAsync(patchDocument, workItemId, cancellationToken: cancellationToken);
    }

    public async Task<int> CreateWorkItemAsync(
        string title,
        string description,
        string state,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating work item: '{Title}' with state '{State}'", title, state);

        var patchDocument = new JsonPatchDocument
        {
            new JsonPatchOperation
            {
                Operation = Operation.Add,
                Path = "/fields/System.Title",
                Value = title
            },
            new JsonPatchOperation
            {
                Operation = Operation.Add,
                Path = "/fields/System.Description",
                Value = description
            },
            new JsonPatchOperation
            {
                Operation = Operation.Add,
                Path = "/fields/System.State",
                Value = state
            },
            new JsonPatchOperation
            {
                Operation = Operation.Add,
                Path = CustomFieldNames.Paths.AutonomyLevel,
                Value = "3"
            },
            new JsonPatchOperation
            {
                Operation = Operation.Add,
                Path = CustomFieldNames.Paths.MinimumReviewScore,
                Value = 85
            }
        };

        var client = await _connection.Value.GetClientAsync<WorkItemTrackingHttpClient>(cancellationToken);
        var workItem = await client.CreateWorkItemAsync(
            patchDocument,
            _options.Project,
            "User Story",
            cancellationToken: cancellationToken);

        var workItemId = workItem.Id ?? 0;
        _logger.LogInformation("Created work item {WorkItemId}: '{Title}'", workItemId, title);
        return workItemId;
    }

    public async Task<WorkItemSupportingArtifacts> DownloadSupportingArtifactsAsync(
        int workItemId,
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        var workItem = await GetWorkItemAsync(workItemId, cancellationToken);
        var supportingAttachments = workItem.Attachments
            .Where(a => a.IsImage || a.IsDocument)
            .ToList();

        if (supportingAttachments.Count == 0)
        {
            return new WorkItemSupportingArtifacts();
        }

        var outputDir = Path.Combine(repositoryPath, ".ado", "stories", $"US-{workItemId}", "documents");
        Directory.CreateDirectory(outputDir);

        var imagePaths = new List<string>();
        var documentPaths = new List<string>();
        var allPaths = new List<string>();
        using var httpClient = CreateAdoHttpClient();

        foreach (var attachment in supportingAttachments)
        {
            try
            {
                using var response = await httpClient.GetAsync(attachment.Url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Failed to download attachment for WI-{WorkItemId}: {Url} (status {StatusCode})",
                        workItemId, attachment.Url, response.StatusCode);
                    continue;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length == 0)
                {
                    continue;
                }

                var fileName = EnsureSupportingFileName(attachment.FileName, response.Content.Headers.ContentType?.MediaType, attachment.IsImage);
                var fullPath = GetUniquePath(Path.Combine(outputDir, fileName));
                await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken);

                var relativePath = Path.GetRelativePath(repositoryPath, fullPath).Replace('\\', '/');
                allPaths.Add(relativePath);

                if (attachment.IsImage)
                {
                    imagePaths.Add(relativePath);
                }
                else
                {
                    documentPaths.Add(relativePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to materialize attachment for WI-{WorkItemId}: {Url}",
                    workItemId, attachment.Url);
            }
        }

        _logger.LogInformation(
            "Materialized {Count} supporting attachment(s) for WI-{WorkItemId} ({Images} images, {Documents} documents)",
            allPaths.Count, workItemId, imagePaths.Count, documentPaths.Count);

        return new WorkItemSupportingArtifacts
        {
            StoryDocumentsFolder = Path.GetRelativePath(repositoryPath, outputDir).Replace('\\', '/'),
            ImagePaths = imagePaths,
            DocumentPaths = documentPaths,
            AllPaths = allPaths
        };
    }

    public void Dispose()
    {
        if (_connection.IsValueCreated)
        {
            _connection.Value.Dispose();
        }
    }

    private static StoryWorkItem MapToStoryWorkItem(WorkItem workItem)
    {
        var fields = workItem.Fields;
        var attachments = ExtractAttachments(workItem, fields);

        return new StoryWorkItem
        {
            Id = workItem.Id ?? 0,
            Title = GetField<string>(fields, "System.Title") ?? "Untitled",
            Description = GetField<string>(fields, "System.Description"),
            AcceptanceCriteria = GetField<string>(fields, "Microsoft.VSTS.Common.AcceptanceCriteria"),
            State = GetField<string>(fields, "System.State") ?? "New",
            AssignedTo = GetIdentityField(fields, "System.AssignedTo"),
            AreaPath = GetField<string>(fields, "System.AreaPath"),
            IterationPath = GetField<string>(fields, "System.IterationPath"),
            StoryPoints = GetField<double?>(fields, "Microsoft.VSTS.Scheduling.StoryPoints") is double sp
                ? (int)sp : null,
            Tags = GetField<string>(fields, "System.Tags")?
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList() ?? [],
            Attachments = attachments,
            CreatedDate = GetField<DateTime>(fields, "System.CreatedDate"),
            ChangedDate = GetField<DateTime>(fields, "System.ChangedDate"),

            // AI Input Fields
            AutonomyLevel = ParseAutonomyLevel(
                GetField<object>(fields, CustomFieldNames.AutonomyLevel)?.ToString()
                ?? GetField<double?>(fields, "Custom.AIAutonomyLevel")?.ToString()),
            MinimumReviewScore = GetField<double?>(fields, CustomFieldNames.MinimumReviewScore) is double mrs
                ? (int)mrs : 85,

            // Per-Story Model Override Fields
            AIModelTier = GetField<string>(fields, CustomFieldNames.ModelTier),
            AIPlanningModel = GetField<string>(fields, CustomFieldNames.PlanningModel),
            AICodingModel = GetField<string>(fields, CustomFieldNames.CodingModel),
            AITestingModel = GetField<string>(fields, CustomFieldNames.TestingModel),
            AIReviewModel = GetField<string>(fields, CustomFieldNames.ReviewModel),
            AIDocumentationModel = GetField<string>(fields, CustomFieldNames.DocumentationModel),
            AICodingProvider = GetField<string>(fields, CustomFieldNames.CodingProvider),

            // AI Output Fields (written by agents, readable for dashboards/queries)
            AITokensUsed = GetField<double?>(fields, CustomFieldNames.TokensUsed) is double tu
                ? (int)tu : null,
            AICost = GetField<string>(fields, CustomFieldNames.Cost),
            AIComplexity = GetField<string>(fields, CustomFieldNames.Complexity),
            AIModel = GetField<string>(fields, CustomFieldNames.Model),
            AIReviewScore = GetField<double?>(fields, CustomFieldNames.ReviewScore) is double rs
                ? (int)rs : null,
            AIProcessingTime = GetField<double?>(fields, CustomFieldNames.ProcessingTime) is double pt
                ? (decimal)pt : null,
            AIFilesGenerated = GetField<double?>(fields, CustomFieldNames.FilesGenerated) is double fg
                ? (int)fg : null,
            AITestsGenerated = GetField<double?>(fields, CustomFieldNames.TestsGenerated) is double tg
                ? (int)tg : null,
            AIPRNumber = GetField<double?>(fields, CustomFieldNames.PRNumber) is double prn
                ? (int)prn : null,
            AILastAgent = GetField<string>(fields, CustomFieldNames.LastAgent),
            CurrentAIAgent = GetField<string>(fields, CustomFieldNames.CurrentAIAgent),
            AICriticalIssues = GetField<double?>(fields, CustomFieldNames.CriticalIssues) is double ci
                ? (int)ci : null,
            AIDeploymentDecision = GetField<string>(fields, CustomFieldNames.DeploymentDecision)
        };
    }

    private static T? GetField<T>(IDictionary<string, object> fields, string key)
    {
        return fields.TryGetValue(key, out var value) && value is T typed ? typed : default;
    }

    private static IReadOnlyList<WorkItemAttachment> ExtractAttachments(
        WorkItem workItem,
        IDictionary<string, object> fields)
    {
        var map = new Dictionary<string, WorkItemAttachment>(StringComparer.OrdinalIgnoreCase);

        void AddAttachment(string? url, string? fileName)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;

            var normalizedUrl = WebUtility.HtmlDecode(url.Trim());
            if (map.ContainsKey(normalizedUrl))
                return;

            var resolvedName = string.IsNullOrWhiteSpace(fileName)
                ? GetFileNameFromUrl(normalizedUrl)
                : fileName;

            map[normalizedUrl] = new WorkItemAttachment
            {
                Url = normalizedUrl,
                FileName = resolvedName,
                IsImage = LooksLikeImage(normalizedUrl, resolvedName),
                IsDocument = LooksLikeDocument(normalizedUrl, resolvedName)
            };
        }

        if (workItem.Relations is not null)
        {
            foreach (var rel in workItem.Relations)
            {
                if (!string.Equals(rel.Rel, "AttachedFile", StringComparison.OrdinalIgnoreCase) &&
                    (rel.Url?.Contains("/_apis/wit/attachments/", StringComparison.OrdinalIgnoreCase) != true))
                {
                    continue;
                }

                object? nameObj = null;
                rel.Attributes?.TryGetValue("name", out nameObj);
                AddAttachment(rel.Url, nameObj?.ToString());
            }
        }

        var description = GetField<string>(fields, "System.Description");
        foreach (var imageUrl in ExtractImageUrls(description))
        {
            AddAttachment(imageUrl, null);
        }

        var acceptance = GetField<string>(fields, "Microsoft.VSTS.Common.AcceptanceCriteria");
        foreach (var imageUrl in ExtractImageUrls(acceptance))
        {
            AddAttachment(imageUrl, null);
        }

        return map.Values.ToList();
    }

    private static IEnumerable<string> ExtractImageUrls(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return [];

        var matches = Regex.Matches(html, "<img[^>]*src=[\"'](?<src>[^\"']+)[\"'][^>]*>", RegexOptions.IgnoreCase);
        return matches
            .Select(m => m.Groups["src"].Value)
            .Where(src => !string.IsNullOrWhiteSpace(src));
    }

    private static bool LooksLikeImage(string? url, string? fileName)
    {
        var target = (fileName ?? url ?? string.Empty).ToLowerInvariant();
        return target.EndsWith(".png") ||
               target.EndsWith(".jpg") ||
               target.EndsWith(".jpeg") ||
               target.EndsWith(".gif") ||
               target.EndsWith(".webp") ||
               target.EndsWith(".svg") ||
               target.EndsWith(".bmp");
    }

    private static bool LooksLikeDocument(string? url, string? fileName)
    {
        var target = (fileName ?? url ?? string.Empty).ToLowerInvariant();
        return target.EndsWith(".pdf") ||
               target.EndsWith(".md") ||
               target.EndsWith(".txt") ||
               target.EndsWith(".doc") ||
               target.EndsWith(".docx") ||
               target.EndsWith(".rtf") ||
               target.EndsWith(".xls") ||
               target.EndsWith(".xlsx") ||
               target.EndsWith(".csv") ||
               target.EndsWith(".ppt") ||
               target.EndsWith(".pptx") ||
               target.EndsWith(".json") ||
               target.EndsWith(".yml") ||
               target.EndsWith(".yaml");
    }

    private static string GetFileNameFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var query = uri.Query;
            var fileNameMatch = Regex.Match(query, @"(?:\?|&)fileName=([^&]+)", RegexOptions.IgnoreCase);
            if (fileNameMatch.Success)
            {
                return Uri.UnescapeDataString(fileNameMatch.Groups[1].Value);
            }

            var segment = uri.Segments.LastOrDefault()?.Trim('/');
            if (!string.IsNullOrWhiteSpace(segment))
            {
                return segment;
            }
        }

        return $"attachment-{Guid.NewGuid():N}.png";
    }

    private HttpClient CreateAdoHttpClient()
    {
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{_options.Pat}"));
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        return client;
    }

    private static string EnsureSupportingFileName(string fileName, string? contentType, bool isImage)
    {
        var safeName = string.Concat(fileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = $"attachment-{Guid.NewGuid():N}";
        }

        if (!Path.HasExtension(safeName))
        {
            safeName += contentType?.ToLowerInvariant() switch
            {
                "image/jpeg" => ".jpg",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                "image/svg+xml" => ".svg",
                "image/bmp" => ".bmp",
                "application/pdf" => ".pdf",
                "text/plain" => ".txt",
                "text/markdown" => ".md",
                "application/json" => ".json",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
                "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
                _ => ".png"
            };

            if (!isImage && safeName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                safeName = Path.ChangeExtension(safeName, ".bin");
            }
        }

        return safeName;
    }

    private static string GetUniquePath(string fullPath)
    {
        if (!File.Exists(fullPath))
            return fullPath;

        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(fullPath);
        var extension = Path.GetExtension(fullPath);

        for (var index = 1; index <= 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{baseName}-{index}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(directory, $"{baseName}-{Guid.NewGuid():N}{extension}");
    }

    private static string? GetIdentityField(IDictionary<string, object> fields, string key)
    {
        if (!fields.TryGetValue(key, out var value)) return null;

        return value switch
        {
            IdentityRef identity => identity.DisplayName,
            string s => s,
            _ => value?.ToString()
        };
    }

    /// <summary>
    /// Parses an autonomy level from either a numeric picklist value (1-5)
    /// or legacy picklist text (e.g., "3 - Review &amp; Pause"). Returns default 3 when invalid.
    /// Also handles legacy integer field values that come through as doubles.
    /// </summary>
    internal static int ParseAutonomyLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 3;

        // Try parsing leading integer (handles "3 - Review & Pause" and plain "3")
        var span = value.AsSpan().TrimStart();
        var end = 0;
        while (end < span.Length && char.IsDigit(span[end])) end++;

        if (end > 0 && int.TryParse(span[..end], out var level) && level >= 1 && level <= 5)
            return level;

        // Fallback: try parsing as double (legacy integer field returned as JSON number)
        if (double.TryParse(value, out var d) && d >= 1 && d <= 5)
            return (int)d;

        return 3; // Default
    }
}
