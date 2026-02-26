using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIAgents.Core.Configuration;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgents.Core.Services;

/// <summary>
/// API-only onboarding implementation that analyzes a GitHub repository using REST APIs
/// and writes compact .agent context artifacts without cloning.
/// </summary>
public sealed class GitHubCodebaseOnboardingService : ICodebaseOnboardingService
{
    private readonly GitHubOptions _gitHub;
    private readonly CodebaseDocumentationOptions _options;
    private readonly ILogger<GitHubCodebaseOnboardingService> _logger;
    private readonly HttpClient _httpClient;

    public GitHubCodebaseOnboardingService(
        IOptions<GitHubOptions> gitHubOptions,
        IOptions<CodebaseDocumentationOptions> options,
        ILogger<GitHubCodebaseOnboardingService> logger)
    {
        _gitHub = gitHubOptions.Value;
        _options = options.Value;
        _logger = logger;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(90)
        };
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _gitHub.Token);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AIAgents/1.0");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<CodebaseOnboardingExecutionResult> GenerateAndPublishAsync(
        bool incremental,
        bool includeGitHistory,
        CancellationToken cancellationToken = default)
    {
        var defaultBranch = await GetDefaultBranchAsync(cancellationToken);
        var headSha = await GetBranchHeadShaAsync(defaultBranch, cancellationToken);
        var tree = await GetRecursiveTreeAsync(headSha, cancellationToken);

        var files = tree
            .Where(n => string.Equals(n.Type, "blob", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var totalBytes = files.Sum(f => f.Size);
        var directories = BuildTopDirectories(files, depth: 3);
        var techFiles = SelectTechIndicatorFiles(files);
        var highValueFiles = SelectHighValueFiles(files);
        var fileContents = await FetchFileContentsAsync(defaultBranch, highValueFiles, cancellationToken);

        var commitSummary = await GetRecentCommitSummaryAsync(defaultBranch, includeGitHistory, cancellationToken);
        var priorMetadata = incremental ? await TryGetMetadataAsync(cancellationToken) : null;
        var scanCount = Math.Max(1, (priorMetadata?.UserStoriesReviewed ?? 0) + (incremental ? 1 : 0));

        var conventions = InferConventions(fileContents);
        var techStack = DetectTechStack(files, fileContents);
        var architecture = DetectArchitecture(files);
        var featureIndex = BuildFeatureIndex(files);
        var fileMap = BuildFileMap(files, directories, techFiles);

        var generatedOn = DateTime.UtcNow;
        var contextMarkdown = BuildCodebaseContextMarkdown(
            generatedOn,
            defaultBranch,
            files.Count,
            techStack,
            architecture,
            conventions,
            commitSummary,
            featureIndex.Keys.ToList(),
            directories,
            scanCount,
            treeTruncated: tree.Count >= 100_000);

        var metadata = new CodebaseAnalysisMetadata
        {
            LastAnalysis = generatedOn,
            LastCommitHash = headSha,
            FilesAnalyzed = files.Count,
            LinesOfCode = 0,
            UserStoriesReviewed = scanCount,
            CommitsAnalyzed = commitSummary.CommitCount,
            FeaturesDocumented = featureIndex.Count,
            LanguagesDetected = techStack.Languages,
            PrimaryFramework = techStack.Framework,
            DocumentationSizeKB = contextMarkdown.Length / 1024,
            FeaturesDocumentedList = featureIndex.Keys.OrderBy(k => k).ToList()
        };

        var initializationBundle = BuildInitializationBundleArtifact(metadata, defaultBranch, headSha, includeGitHistory, incremental);

        var artifacts = new Dictionary<string, string>
        {
            [".agent/CODEBASE_CONTEXT.md"] = contextMarkdown,
            [".agent/FILE_MAP.json"] = JsonSerializer.Serialize(fileMap, JsonOptions),
            [".agent/FEATURE_INDEX.json"] = JsonSerializer.Serialize(featureIndex, JsonOptions),
            [".agent/INITIALIZATION_BUNDLE.json"] = JsonSerializer.Serialize(initializationBundle, JsonOptions),
            [".agent/ONBOARDING_METADATA.json"] = JsonSerializer.Serialize(new
            {
                generatedOn,
                scanCount,
                source = "github-rest-api",
                branch = defaultBranch,
                headSha,
                filesScanned = files.Count,
                totalBytesScanned = totalBytes,
                treeNodeCount = tree.Count,
                treeTruncated = tree.Count >= 100_000,
                includeGitHistory,
                highValueFilesFetched = fileContents.Count
            }, JsonOptions),
            [".agent/metadata.json"] = JsonSerializer.Serialize(metadata, JsonOptions)
        };

        var published = 0;
        if (_options.ApiPublishEnabled)
        {
            foreach (var (path, content) in artifacts)
            {
                await PutFileContentAsync(path, content, defaultBranch, cancellationToken);
                published++;
            }
        }

        var summary = _options.ApiPublishEnabled
            ? $"API-only onboarding complete: scanned {files.Count:N0} files, published {published} .agent artifacts on {defaultBranch}."
            : $"API-only onboarding dry-run complete: scanned {files.Count:N0} files, publish disabled by configuration.";
        _logger.LogInformation("{Summary}", summary);

        return new CodebaseOnboardingExecutionResult
        {
            Branch = defaultBranch,
            HeadSha = headSha,
            FilesScanned = files.Count,
            TotalBytesScanned = totalBytes,
            ArtifactsPublished = published,
            ActiveAuthors = commitSummary.ActiveAuthors,
            LastCommitDateUtc = commitSummary.LastCommitUtc,
            Summary = summary,
            Metadata = metadata
        };
    }

    private static object BuildInitializationBundleArtifact(
        CodebaseAnalysisMetadata metadata,
        string branch,
        string headSha,
        bool includeGitHistory,
        bool incremental)
    {
        static object BuildCategory(string name, params string[] pointers)
            => new
            {
                name,
                pointers
            };

        return new
        {
            generatedAtUtc = DateTime.UtcNow,
            source = "github-rest-api",
            branch,
            headSha,
            includeGitHistory,
            incremental,
            summary = "Initialization bundle for downstream planning/coding/testing/review agents.",
            categories = new[]
            {
                BuildCategory("architecture", ".agent/CODEBASE_CONTEXT.md"),
                BuildCategory("structure", ".agent/FILE_MAP.json"),
                BuildCategory("conventions", ".agent/CODEBASE_CONTEXT.md"),
                BuildCategory("testing", ".agent/CODEBASE_CONTEXT.md"),
                BuildCategory("integrations", ".agent/FEATURE_INDEX.json", ".agent/ONBOARDING_METADATA.json"),
                BuildCategory("concerns", ".agent/CODEBASE_CONTEXT.md")
            },
            progress = new
            {
                filesAnalyzed = metadata.FilesAnalyzed,
                featuresDocumented = metadata.FeaturesDocumented,
                lastAnalysis = metadata.LastAnalysis,
                commitsAnalyzed = metadata.CommitsAnalyzed
            },
            traceability = new
            {
                features = metadata.FeaturesDocumentedList,
                languages = metadata.LanguagesDetected
            }
        };
    }

    public async Task<CodebaseAnalysisMetadata?> TryGetMetadataAsync(CancellationToken cancellationToken = default)
    {
        var defaultBranch = await GetDefaultBranchAsync(cancellationToken);
        var content = await TryGetFileContentAsync(".agent/metadata.json", defaultBranch, cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CodebaseAnalysisMetadata>(content);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string> GetDefaultBranchAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync($"repos/{_gitHub.Owner}/{_gitHub.Repo}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("default_branch").GetString() ?? "main";
    }

    private async Task<string> GetBranchHeadShaAsync(string branch, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync($"repos/{_gitHub.Owner}/{_gitHub.Repo}/branches/{branch}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("commit").GetProperty("sha").GetString()
               ?? throw new InvalidOperationException("Could not resolve branch head sha.");
    }

    private async Task<List<TreeNode>> GetRecursiveTreeAsync(string sha, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(
            $"repos/{_gitHub.Owner}/{_gitHub.Repo}/git/trees/{sha}?recursive=1",
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        var results = new List<TreeNode>();
        foreach (var node in doc.RootElement.GetProperty("tree").EnumerateArray())
        {
            var path = node.GetProperty("path").GetString() ?? string.Empty;
            var type = node.GetProperty("type").GetString() ?? string.Empty;
            var size = node.TryGetProperty("size", out var sizeNode) ? sizeNode.GetInt64() : 0L;
            results.Add(new TreeNode(path, type, size));
        }

        return results;
    }

    private static List<string> SelectTechIndicatorFiles(List<TreeNode> files)
    {
        var patterns = new[]
        {
            ".sln", ".csproj", "package.json", "requirements.txt", "pom.xml", "go.mod", "cargo.toml", ".fsproj"
        };

        return files
            .Select(f => f.Path)
            .Where(path => patterns.Any(pattern =>
                path.EndsWith(pattern, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(path), pattern, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p)
            .ToList();
    }

    private static List<string> SelectHighValueFiles(List<TreeNode> files)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files.Select(f => f.Path))
        {
            var name = Path.GetFileName(file);

            if (file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                || string.Equals(file, "package.json", StringComparison.OrdinalIgnoreCase)
                || string.Equals(file, "global.json", StringComparison.OrdinalIgnoreCase)
                || string.Equals(file, "README.md", StringComparison.OrdinalIgnoreCase)
                || string.Equals(file, ".editorconfig", StringComparison.OrdinalIgnoreCase)
                || string.Equals(file, ".prettierrc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(file, "docker-compose.yml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(file, ".agent/CODEBASE_CONTEXT.md", StringComparison.OrdinalIgnoreCase)
                || (file.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)
                    && (file.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                        || file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
                || (string.Equals(Path.GetDirectoryName(file)?.Replace('\\', '/'), string.Empty, StringComparison.Ordinal)
                    && name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(file);
            }
        }

        return results.OrderBy(p => p).ToList();
    }

    private async Task<Dictionary<string, string>> FetchFileContentsAsync(
        string branch,
        List<string> paths,
        CancellationToken cancellationToken)
    {
        var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var content = await TryGetFileContentAsync(path, branch, cancellationToken);
            if (content is not null)
            {
                contents[path] = content;
            }
        }

        return contents;
    }

    private async Task<string?> TryGetFileContentAsync(string path, string branch, CancellationToken cancellationToken)
    {
        var encodedPath = Uri.EscapeDataString(path).Replace("%2F", "/");
        var response = await _httpClient.GetAsync(
            $"repos/{_gitHub.Owner}/{_gitHub.Repo}/contents/{encodedPath}?ref={branch}",
            cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        var size = doc.RootElement.TryGetProperty("size", out var sizeNode) ? sizeNode.GetInt64() : 0;
        if (size > _options.ApiFileSizeLimitBytes)
        {
            _logger.LogDebug("Skipping large file {Path} ({Size} bytes)", path, size);
            return null;
        }

        if (!doc.RootElement.TryGetProperty("content", out var contentNode))
        {
            return null;
        }

        var base64 = contentNode.GetString();
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        var normalized = base64.Replace("\n", string.Empty).Replace("\r", string.Empty);
        var bytes = Convert.FromBase64String(normalized);
        return Encoding.UTF8.GetString(bytes);
    }

    private async Task PutFileContentAsync(string path, string content, string branch, CancellationToken cancellationToken)
    {
        var existingSha = await TryGetFileShaAsync(path, branch, cancellationToken);

        var payload = new Dictionary<string, object?>
        {
            ["message"] = existingSha is null
                ? "chore: add adom8 codebase context [skip ci]"
                : "chore: refresh adom8 codebase context [skip ci]",
            ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
            ["branch"] = branch
        };

        if (existingSha is not null)
        {
            payload["sha"] = existingSha;
        }

        var json = JsonSerializer.Serialize(payload);
        var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
        var encodedPath = Uri.EscapeDataString(path).Replace("%2F", "/");
        var response = await _httpClient.PutAsync(
            $"repos/{_gitHub.Owner}/{_gitHub.Repo}/contents/{encodedPath}",
            httpContent,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        _logger.LogDebug("Published {Path}: {BodyLength} bytes response", path, body.Length);
    }

    private async Task<string?> TryGetFileShaAsync(string path, string branch, CancellationToken cancellationToken)
    {
        var encodedPath = Uri.EscapeDataString(path).Replace("%2F", "/");
        var response = await _httpClient.GetAsync(
            $"repos/{_gitHub.Owner}/{_gitHub.Repo}/contents/{encodedPath}?ref={branch}",
            cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("sha", out var shaNode) ? shaNode.GetString() : null;
    }

    private async Task<CommitSummary> GetRecentCommitSummaryAsync(
        string branch,
        bool includeGitHistory,
        CancellationToken cancellationToken)
    {
        if (!includeGitHistory)
        {
            return new CommitSummary(0, 0, null, "Git history disabled for this run.");
        }

        var response = await _httpClient.GetAsync(
            $"repos/{_gitHub.Owner}/{_gitHub.Repo}/commits?sha={branch}&per_page=30",
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        var commitArray = doc.RootElement;
        var commitCount = commitArray.GetArrayLength();

        var authors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTime? lastCommitUtc = null;
        var styleHints = new List<string>();

        foreach (var commit in commitArray.EnumerateArray())
        {
            var message = commit.GetProperty("commit").GetProperty("message").GetString() ?? string.Empty;
            if (message.Contains(':') && message.IndexOf(' ') > 0)
            {
                styleHints.Add(message.Split('\n')[0]);
            }

            if (commit.TryGetProperty("author", out var authorNode) && authorNode.ValueKind == JsonValueKind.Object)
            {
                var login = authorNode.TryGetProperty("login", out var loginNode) ? loginNode.GetString() : null;
                if (!string.IsNullOrWhiteSpace(login)) authors.Add(login);
            }

            var dateString = commit.GetProperty("commit").GetProperty("author").GetProperty("date").GetString();
            if (DateTime.TryParse(dateString, out var commitDate))
            {
                if (lastCommitUtc is null || commitDate > lastCommitUtc)
                {
                    lastCommitUtc = commitDate;
                }
            }
        }

        var style = styleHints.Count == 0
            ? "Mixed commit message styles"
            : styleHints.Any(s => s.Contains("feat(", StringComparison.OrdinalIgnoreCase)
                                  || s.Contains("fix(", StringComparison.OrdinalIgnoreCase)
                                  || s.Contains("chore(", StringComparison.OrdinalIgnoreCase))
                ? "Mostly conventional commit style"
                : "PR squash / free-form commit messages";

        return new CommitSummary(commitCount, authors.Count, lastCommitUtc, style);
    }

    private static Dictionary<string, object> BuildFileMap(
        List<TreeNode> files,
        List<string> directories,
        List<string> techFiles)
    {
        var extensionGroups = files
            .GroupBy(f => Path.GetExtension(f.Path).ToLowerInvariant())
            .OrderByDescending(g => g.Count())
            .Take(30)
            .ToDictionary(
                g => string.IsNullOrWhiteSpace(g.Key) ? "(no-extension)" : g.Key,
                g => new
                {
                    count = g.Count(),
                    totalBytes = g.Sum(x => x.Size)
                });

        return new Dictionary<string, object>
        {
            ["generatedAtUtc"] = DateTime.UtcNow,
            ["totalFiles"] = files.Count,
            ["totalBytes"] = files.Sum(f => f.Size),
            ["topDirectories"] = directories,
            ["techIndicatorFiles"] = techFiles,
            ["extensionGroups"] = extensionGroups
        };
    }

    private static Dictionary<string, object> BuildFeatureIndex(List<TreeNode> files)
    {
        var buckets = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["authentication"] = new[] { "auth", "login", "identity", "sso", "oauth", "jwt" },
            ["billing"] = new[] { "billing", "payment", "invoice", "subscription", "stripe" },
            ["projects"] = new[] { "project", "workspace", "repo", "repository" },
            ["runs"] = new[] { "run", "execution", "pipeline", "job", "queue" },
            ["deployment"] = new[] { "deploy", "release", "workflow", "actions", "terraform", "infrastructure" },
            ["testing"] = new[] { "test", "spec", "fixture", "mock" },
            ["dashboard"] = new[] { "dashboard", "ui", "page", "component" }
        };

        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var allPaths = files.Select(f => f.Path.Replace('\\', '/')).ToList();

        foreach (var (feature, keywords) in buckets)
        {
            var matched = allPaths
                .Where(path => keywords.Any(keyword => path.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                .Take(200)
                .ToList();

            if (matched.Count > 0)
            {
                result[feature] = new
                {
                    fileCount = matched.Count,
                    samplePaths = matched.Take(25).ToList()
                };
            }
        }

        return result;
    }

    private static ConventionsSummary InferConventions(Dictionary<string, string> fileContents)
    {
        var conventions = new ConventionsSummary
        {
            Indentation = "Unknown",
            NamespaceStyle = "Unknown",
            Notes = new List<string>()
        };

        if (fileContents.TryGetValue(".editorconfig", out var editorConfig))
        {
            if (editorConfig.Contains("indent_style = space", StringComparison.OrdinalIgnoreCase))
            {
                conventions.Indentation = "Spaces";
            }
            else if (editorConfig.Contains("indent_style = tab", StringComparison.OrdinalIgnoreCase))
            {
                conventions.Indentation = "Tabs";
            }

            if (editorConfig.Contains("csharp_style_namespace_declarations = file_scoped", StringComparison.OrdinalIgnoreCase))
            {
                conventions.NamespaceStyle = "File-scoped namespaces";
            }
            else if (editorConfig.Contains("csharp_style_namespace_declarations = block_scoped", StringComparison.OrdinalIgnoreCase))
            {
                conventions.NamespaceStyle = "Block-scoped namespaces";
            }

            conventions.Notes.Add("Derived from .editorconfig");
        }

        return conventions;
    }

    private static TechStackSummary DetectTechStack(List<TreeNode> files, Dictionary<string, string> fileContents)
    {
        var paths = files.Select(f => f.Path).ToList();
        var language = paths.Any(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) ? "C#"
            : paths.Any(p => p.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)) ? "TypeScript"
            : "Mixed";

        var framework = paths.Any(p => p.Contains("AIAgents.Functions", StringComparison.OrdinalIgnoreCase))
            ? ".NET 8 Azure Functions"
            : paths.Any(p => p.Equals("next.config.mjs", StringComparison.OrdinalIgnoreCase))
                ? "Next.js"
                : "Unknown";

        var database = fileContents.Keys.Any(k => k.Contains("prisma", StringComparison.OrdinalIgnoreCase))
            || paths.Any(p => p.Contains("prisma", StringComparison.OrdinalIgnoreCase))
                ? "Prisma (database provider from schema)"
                : "Not detected";

        var testFramework = paths.Any(p => p.EndsWith("Tests.csproj", StringComparison.OrdinalIgnoreCase))
            ? "xUnit"
            : paths.Any(p => p.Contains("jest", StringComparison.OrdinalIgnoreCase)) ? "Jest" : "Not detected";

        var containerized = paths.Any(p => p.Contains("Dockerfile", StringComparison.OrdinalIgnoreCase)
                                           || p.EndsWith("docker-compose.yml", StringComparison.OrdinalIgnoreCase));

        var ci = paths.Any(p => p.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase))
            ? "GitHub Actions"
            : "Not detected";

        var languages = new List<string>();
        if (paths.Any(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))) languages.Add("C#");
        if (paths.Any(p => p.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase))) languages.Add("TypeScript");
        if (paths.Any(p => p.EndsWith(".py", StringComparison.OrdinalIgnoreCase))) languages.Add("Python");

        return new TechStackSummary
        {
            Language = language,
            Framework = framework,
            Database = database,
            TestFramework = testFramework,
            Containerized = containerized,
            CiCd = ci,
            Languages = languages
        };
    }

    private static ArchitectureSummary DetectArchitecture(List<TreeNode> files)
    {
        var paths = files.Select(f => f.Path.Replace('\\', '/')).ToList();
        var topFolders = paths
            .Select(path => path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();

        var csprojCount = paths.Count(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        var packageJsonCount = paths.Count(p => p.EndsWith("package.json", StringComparison.OrdinalIgnoreCase));

        var monorepo = csprojCount > 1 || packageJsonCount > 1 || topFolders.Count > 6;
        var hasBackend = csprojCount > 0;
        var hasFrontend = packageJsonCount > 0 || paths.Any(p => p.Contains("ado-agent-saas/src/app", StringComparison.OrdinalIgnoreCase));

        var apiEntry = paths.FirstOrDefault(p => p.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase)
                                                 && p.Contains("Functions", StringComparison.OrdinalIgnoreCase))
                       ?? paths.FirstOrDefault(p => p.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase));

        var testsPath = paths.FirstOrDefault(p => p.Contains("Tests", StringComparison.OrdinalIgnoreCase));

        return new ArchitectureSummary
        {
            IsMonorepo = monorepo,
            HasFrontendAndBackend = hasBackend && hasFrontend,
            ApiEntryPoint = apiEntry,
            TestsLocation = testsPath,
            TopFolders = topFolders
        };
    }

    private static List<string> BuildTopDirectories(List<TreeNode> files, int depth)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files.Select(f => f.Path.Replace('\\', '/')))
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1) continue;

            var segmentCount = Math.Min(depth, parts.Length - 1);
            for (var i = 1; i <= segmentCount; i++)
            {
                set.Add(string.Join('/', parts.Take(i)));
            }
        }

        return set.OrderBy(x => x).Take(500).ToList();
    }

    private static string BuildCodebaseContextMarkdown(
        DateTime generatedOn,
        string branch,
        int fileCount,
        TechStackSummary tech,
        ArchitectureSummary architecture,
        ConventionsSummary conventions,
        CommitSummary commit,
        List<string> features,
        List<string> directories,
        int scanCount,
        bool treeTruncated)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Codebase Context");
        sb.AppendLine();
        sb.AppendLine($"> Generated by adom8 Onboarding Agent on {generatedOn:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"> Source: GitHub API (no clone) | Branch: {branch} | Files scanned: {fileCount:N0} | Scan count: {scanCount}");
        if (treeTruncated)
        {
            sb.AppendLine("> Note: GitHub tree response may be truncated on very large repos; context reflects available API data.");
        }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        sb.AppendLine("## Tech Stack");
        sb.AppendLine();
        sb.AppendLine($"- **Primary Language**: {tech.Language}");
        sb.AppendLine($"- **Framework**: {tech.Framework}");
        sb.AppendLine($"- **Database**: {tech.Database}");
        sb.AppendLine($"- **Test Framework**: {tech.TestFramework}");
        sb.AppendLine($"- **CI/CD**: {tech.CiCd}");
        sb.AppendLine($"- **Containerized**: {(tech.Containerized ? "Yes" : "No")}");
        sb.AppendLine();

        sb.AppendLine("## Architecture");
        sb.AppendLine();
        sb.AppendLine($"- **Monorepo**: {(architecture.IsMonorepo ? "Yes" : "No")}");
        sb.AppendLine($"- **Frontend + Backend**: {(architecture.HasFrontendAndBackend ? "Yes" : "No")}");
        sb.AppendLine($"- **API Entry Point**: {architecture.ApiEntryPoint ?? "Not detected"}");
        sb.AppendLine($"- **Tests Location**: {architecture.TestsLocation ?? "Not detected"}");
        sb.AppendLine();

        sb.AppendLine("## Coding Conventions");
        sb.AppendLine();
        sb.AppendLine($"- **Indentation**: {conventions.Indentation}");
        sb.AppendLine($"- **Namespace Style**: {conventions.NamespaceStyle}");
        foreach (var note in conventions.Notes)
        {
            sb.AppendLine($"- **Note**: {note}");
        }
        sb.AppendLine();

        sb.AppendLine("## Repository Activity");
        sb.AppendLine();
        sb.AppendLine($"- **Last Commit**: {(commit.LastCommitUtc.HasValue ? commit.LastCommitUtc.Value.ToString("yyyy-MM-dd HH:mm:ss UTC") : "Unknown")}");
        sb.AppendLine($"- **Active Authors (last 30 commits)**: {commit.ActiveAuthors}");
        sb.AppendLine($"- **Commit Count Sample**: {commit.CommitCount}");
        sb.AppendLine($"- **Commit Style**: {commit.Style}");
        sb.AppendLine();

        sb.AppendLine("## Feature Areas");
        sb.AppendLine();
        if (features.Count == 0)
        {
            sb.AppendLine("- No dominant feature clusters detected from path analysis.");
        }
        else
        {
            foreach (var feature in features.OrderBy(f => f))
            {
                sb.AppendLine($"- {feature}");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Top Directories");
        sb.AppendLine();
        foreach (var directory in directories.Take(80))
        {
            sb.AppendLine($"- {directory}");
        }
        sb.AppendLine();

        sb.AppendLine("## Instructions for AI Agents");
        sb.AppendLine();
        sb.AppendLine("1. Read this file before planning or coding.");
        sb.AppendLine("2. Use FEATURE_INDEX.json to target files for story scope.");
        sb.AppendLine("3. Use FILE_MAP.json to avoid broad scans when selecting context.");
        sb.AppendLine("4. Regenerate onboarding artifacts after major architecture refactors.");

        return sb.ToString();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private sealed record TreeNode(string Path, string Type, long Size);
    private sealed record CommitSummary(int CommitCount, int ActiveAuthors, DateTime? LastCommitUtc, string Style);

    private sealed class ConventionsSummary
    {
        public required string Indentation { get; set; }
        public required string NamespaceStyle { get; set; }
        public required List<string> Notes { get; set; }
    }

    private sealed class TechStackSummary
    {
        public required string Language { get; init; }
        public required string Framework { get; init; }
        public required string Database { get; init; }
        public required string TestFramework { get; init; }
        public required bool Containerized { get; init; }
        public required string CiCd { get; init; }
        public required List<string> Languages { get; init; }
    }

    private sealed class ArchitectureSummary
    {
        public required bool IsMonorepo { get; init; }
        public required bool HasFrontendAndBackend { get; init; }
        public required string? ApiEntryPoint { get; init; }
        public required string? TestsLocation { get; init; }
        public required List<string> TopFolders { get; init; }
    }
}

/// <summary>
/// Fallback onboarding service when API-only provider is unavailable.
/// </summary>
public sealed class NoOpCodebaseOnboardingService : ICodebaseOnboardingService
{
    public Task<CodebaseOnboardingExecutionResult> GenerateAndPublishAsync(
        bool incremental,
        bool includeGitHistory,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("API-only codebase onboarding is not configured for this Git provider.");

    public Task<CodebaseAnalysisMetadata?> TryGetMetadataAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<CodebaseAnalysisMetadata?>(null);
}
