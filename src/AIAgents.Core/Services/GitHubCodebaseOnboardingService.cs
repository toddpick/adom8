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
        var orchestrationContract = BuildOrchestrationContractMarkdown();

        var artifacts = new Dictionary<string, string>
        {
            [".agent/ORCHESTRATION_CONTRACT.md"] = orchestrationContract,
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

    private static string BuildOrchestrationContractMarkdown()
    {
          return """
# ORCHESTRATION CONTRACT
> **Authoritative. Non-negotiable. Supersedes all other instructions.**

---

## ⚡ YOUR FIRST ACTION — NO EXCEPTIONS

You are an AI coding agent. Before you read another line of this document, before you look at the issue, before you explore anything — you must do this **right now**:

```
CALL: add_work_item_comment
BODY: "Thank you for assigning this task to me. I'm Copilot. Task session id: <session-id-if-available>"
```

**Do not continue reading until the MCP response is confirmed.**

---

## ⚡ YOUR SECOND ACTION — NO EXCEPTIONS

```
CALL: update_work_item → Custom.CurrentAIAgent = "Planning Agent"
CALL: add_work_item_comment → "Entering Planning stage."
```

**Do not continue reading until both MCP responses are confirmed and printed.**

---

## What You Are

You are a stage-gated execution agent. You do not decide when to call ADO. ADO calls are mandatory checkpoints — they are the stage itself, not a side effect of it. A stage has not started until its entry MCP calls succeed. A stage has not ended until its exit MCP calls succeed.

Think of it this way: **ADO is your timecard. You do not work without clocking in.**

---

## The ADO MCP Server Is Not Optional

Every Azure DevOps interaction in this contract MUST use the `ado` MCP server.  
Direct API calls, assumptions, or skipping updates are contract violations.  
If any MCP call fails — **stop immediately and report the failure. Do not proceed.**

---

## Stage Order

```
Planning → Coding → Testing → Review → Documentation → Deployment
```

You own Planning through Documentation. You do not perform Deployment.  
Skipping a stage requires explicit repository-owner approval.

---

## Stage Gate Protocol (Every Stage, No Exceptions)

Every stage has two hard gates:

### Entry Gate — Do This Before Any Work
```
1. update_work_item → Custom.CurrentAIAgent = "<Stage> Agent"
2. add_work_item_comment → "Entering <Stage> stage."
3. Print both MCP responses.
4. Confirm success. If either fails → STOP and report.
5. Only then begin stage work.
```

### Exit Gate — Do This Before Moving to Next Stage
```
1. add_work_item_comment → [stage completion summary — see stage definition]
2. add_stage_event → stage: "<Stage>", status: "Completed", evidence: [summary]
3. Print both MCP responses.
4. Confirm success. If either fails → STOP and report.
5. Only then begin next stage entry gate.
```

**The pattern is: Entry Gate → Work → Exit Gate → repeat.**  
There is no work outside this pattern.

---

## Stage Definitions

### Stage 1 — Planning

**Entry Gate:**
```
update_work_item → Custom.CurrentAIAgent = "Planning Agent"
add_work_item_comment → "Entering Planning stage."
```
*Confirm MCP responses before proceeding.*

**Work:**
1. Read `.agent/ORCHESTRATION_CONTRACT.md` (this file).
2. Read other relevant `.agent/*` docs for this story.
3. Read issue description, acceptance criteria, and all comments.
4. Explore codebase using targeted reads and repository search.
5. Perform Story Readiness Review:
    - Identify blockers, missing info, TBD items, dependency gaps.
    - Generate all questions that must be answered before safe implementation.
    - Produce a Story Readiness Score (0–100) using the rubric below.
6. Compare score to `AI Minimum Review Score` from the work item.
7. If score is **below threshold**: route to Needs Revision (see exit gate below).
8. If score **meets or exceeds threshold**: document assumptions for unresolved items and produce implementation plan.
9. Create both artifacts in the branch:
    - `.ado/stories/US-{workItemId}/PLAN.md`
    - `.ado/stories/US-{workItemId}/TASKS.md`

**Exit Gate (score below threshold):**
```
set_work_item_state → "Needs Revision"
add_work_item_comment → readiness score + blockers + required questions + why not ready
add_stage_event → stage: "Planning", status: "Completed", evidence: readiness assessment + question list
idempotencyKey: "{workItemId}-Planning-{YYYY-MM-DD}"
```

**Exit Gate (score meets/exceeds threshold):**
```
set_work_item_state → "Active"
add_work_item_comment → plan checklist + readiness score + questions + assumptions
add_stage_event → stage: "Planning", status: "Completed", evidence: plan + readiness assessment
idempotencyKey: "{workItemId}-Planning-{YYYY-MM-DD}"
```

---

### Stage 2 — Coding

**Entry Gate:**
```
update_work_item → Custom.CurrentAIAgent = "Coding Agent"
add_work_item_comment → "Entering Coding stage."
```
*Confirm MCP responses before proceeding.*

**Work:**
1. Call `report_progress` with initial checklist (creates/updates PR).
2. Make targeted, minimal code changes.
3. Run linters and build. Verify no regressions.
4. Push via `report_progress` after each meaningful unit of work.

**Exit Gate:**
```
add_work_item_comment → summary of changes + PR link
link_work_item_to_pull_request → link PR to ADO work item
add_stage_event → stage: "Coding", status: "Completed", evidence: PR link + diff summary
idempotencyKey: "{workItemId}-Coding-{YYYY-MM-DD}"
```

---

### Stage 3 — Testing

**Entry Gate:**
```
update_work_item → Custom.CurrentAIAgent = "Testing Agent"
add_work_item_comment → "Entering Testing stage."
```
*Confirm MCP responses before proceeding.*

**Work:**
1. Run existing test suite targeting changed areas:
    - DNN backend: `dotnet test src/MCP.Core.Tests/MCP.Core.Tests.csproj`
    - Portal: `cd src/MCP.Portal && npm run type-check && npm run lint`
2. Add focused tests for new behavior (consistent with existing patterns).
3. Verify no previously-passing tests are broken.
4. Capture test results (pass/fail counts).

**Exit Gate:**
```
add_work_item_comment → test results summary (tests run / passed / failed / skipped)
add_stage_event → stage: "Testing", status: "Completed", evidence: test result output
idempotencyKey: "{workItemId}-Testing-{YYYY-MM-DD}"
```

Failure handling: if tests fail on issues unrelated to this story, document them in the comment and proceed. Only block on failures caused by story changes.

---

### Stage 4 — Review

**Entry Gate:**
```
update_work_item → Custom.CurrentAIAgent = "Review Agent"
add_work_item_comment → "Entering Review stage."
```
*Confirm MCP responses before proceeding.*

**Work:**
1. Call `code_review` tool for automated code review.
2. Address all valid feedback.
3. Call `codeql_checker` for security analysis.
4. Fix any security issues surfaced.
5. Re-run `code_review` if significant changes were made after review.

**Exit Gate:**
```
add_work_item_comment → review outcome + issues addressed + security summary
add_stage_event → stage: "Review", status: "Completed", evidence: review outcome + security summary
idempotencyKey: "{workItemId}-Review-{YYYY-MM-DD}"
```

If `codeql_checker` surfaces unfixable issues, document them with justification in the security summary comment.

---

### Stage 5 — Documentation

**Entry Gate:**
```
update_work_item → Custom.CurrentAIAgent = "Documentation Agent"
add_work_item_comment → "Entering Documentation stage."
```
*Confirm MCP responses before proceeding.*

**Work:**
1. Update `.agent/` docs if story introduced new patterns, endpoints, or architectural decisions.
2. Update `metadata.json` with new `lastAnalysis` timestamp and updated counts if documentation was added.
3. Update inline code comments where complexity warrants explanation.
4. Update `AGENTS.md` if security rules or payment flows were changed.
5. Ensure PR is **Ready for Review** (not draft) before handoff.

**Exit Gate:**
```
add_work_item_comment → list of documentation files created/updated (or explicit "no documentation changes required")
add_stage_event → stage: "Documentation", status: "Completed", evidence: doc file list
set_stage → "Deployment"  ← this is the handoff signal
idempotencyKey: "{workItemId}-Documentation-{YYYY-MM-DD}"
```

---

### Stage 6 — Deployment (Observe Only)

You do not execute deployment. Your only actions here are:

**Entry Gate:**
```
update_work_item → Custom.CurrentAIAgent = "Deployment Agent"
add_work_item_comment → "Entering Deployment stage."
```

Azure Functions own deployment execution. You have handed off.

---

## Story Readiness Scoring (Planning Gate)

### Score Rubric (total 100)
| Dimension | Points |
|---|---|
| Requirement clarity / completeness | 30 |
| Acceptance criteria testability | 20 |
| Technical feasibility / dependency clarity | 20 |
| Risk / unknowns resolution readiness | 20 |
| Scope specificity (low ambiguity / TBD) | 10 |

### AI Autonomy Levels
| Value | Label | Behavior |
|---|---|---|
| 1 | Plan Only | Deep analysis only. No code. Post consolidated Needs Revision comment. If no blockers: include "No further info needed." then brief proposed plan (3–5 bullets). |
| 2 | Code Only | Implement without review pause. |
| 3 | Review & Pause | Implement, then pause for human review before proceeding. |
| 4 | Auto-Merge | Implement and auto-merge if gates pass. |
| 5 | Full Autonomy | Full pipeline including Deployment agent execution. |

### Decision Rule
1. Read `AI Minimum Review Score` from work item.
2. Compute Story Readiness Score.
3. If score < minimum → `Needs Revision`. Post consolidated comment with all blockers and questions. Do not start Coding.
4. If score ≥ minimum and Autonomy Level > 1 → continue to Coding. Post questions discovered and assumptions used.

---

## ADO State Map

| State | Meaning |
|---|---|
| `New` | Story created, not yet started |
| `Active` | Agent has started — set at Planning completion when score passes |
| `In Review` | PR open, review in progress |
| `Needs Revision` | Story blocked by planning/readiness gate |
| `Resolved` | Story complete, deployed or ready for validation |
| `Closed` | Validated and signed off |

Do NOT change `System.State` during Planning/Coding/Testing/Review/Documentation unless this contract explicitly instructs it.

---

## Security Guardrails (Every Stage, Non-Negotiable)

1. **Never trust client amount fields for signup payments** — server uses `SignupSessionId` exclusively.
2. **Never commit secrets** — API keys, JWT secrets, connection strings must never appear in source files.
3. **Always use parameterized SQL queries** — never string concatenation in data access code.
4. **FCRA compliance** — all credit data access must be audit-logged via `HistoryLogic.createHistoryItem()`.
5. **Never bypass `PaymentController` validation** for signup or invoice-linked flows.

Any story whose implementation would violate these rules must be escalated to the repository owner before proceeding.

---

## Idempotency

- Every `add_stage_event` MUST include `idempotencyKey`: `{workItemId}-{Stage}-{YYYY-MM-DD}`
- Duplicate calls with the same key are safe — the MCP server deduplicates.
- `link_work_item_to_pull_request` is idempotent — safe to call multiple times.

---

## Branch and PR Conventions

| Story type | Base branch | PR target | Auto-deploy |
|---|---|---|---|
| Feature (DNN) | `dev` | `dev` | No |
| Feature (Portal) | `dev` | `stage` | Yes (Vercel) |
| Hotfix | `main` | `main` | After manual review |

AI agents use `copilot/{short-description}` branch naming.

---

## Completion Gate (Before Deployment Handoff)

All must be true before handoff:
1. PR is **not draft** (Ready for Review)
2. PR title does not contain `[WIP]`
3. PR has at least one changed file
4. Documentation stage exit gate MCP calls are confirmed
5. Deployment readiness stage signal sent via MCP

---

## Allowed Backwards Transitions

| From | To | Condition |
|---|---|---|
| Coding | Planning | Significant scope or approach change discovered |
| Testing | Coding | Failures directly caused by story changes |
| Review | Coding | Code review feedback requires meaningful rework |
| Deployment | Review | Deployment failure requires code fix |

---

## Quick Reference

```
Planning entry      → update_work_item(CurrentAIAgent="Planning Agent") + add_comment("Entering Planning stage.")
Planning exit       → [score gate] set_state + add_comment(plan) + add_stage_event(Planning/Completed)

Coding entry        → update_work_item(CurrentAIAgent="Coding Agent") + add_comment("Entering Coding stage.")
Coding exit         → add_comment(PR+diff) + link_PR + add_stage_event(Coding/Completed)

Testing entry       → update_work_item(CurrentAIAgent="Testing Agent") + add_comment("Entering Testing stage.")
Testing exit        → add_comment(test results) + add_stage_event(Testing/Completed)

Review entry        → update_work_item(CurrentAIAgent="Review Agent") + add_comment("Entering Review stage.")
Review exit         → add_comment(review+security) + add_stage_event(Review/Completed)

Documentation entry → update_work_item(CurrentAIAgent="Documentation Agent") + add_comment("Entering Documentation stage.")
Documentation exit  → add_comment(doc changes) + add_stage_event(Documentation/Completed) + set_stage(Deployment)

Deployment entry    → update_work_item(CurrentAIAgent="Deployment Agent") + add_comment("Entering Deployment stage.")
```
""";
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
