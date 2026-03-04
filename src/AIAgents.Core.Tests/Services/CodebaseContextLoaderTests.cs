using AIAgents.Core.Configuration;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIAgents.Core.Tests.Services;

public sealed class CodebaseContextLoaderTests
{
    [Fact]
    public async Task LoadRelevantContextAsync_IncludesOrchestrationContractBeforeContextIndex()
    {
        var files = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [".agent/ORCHESTRATION_CONTRACT.md"] = "# Orchestration Contract\nRules",
            [".agent/CONTEXT_INDEX.md"] = "# Context Index\nOverview",
            [".agent/CODING_STANDARDS.md"] = "# Coding Standards",
            [".agent/TECH_STACK.md"] = "# Tech Stack"
        };

        var githubContext = new FakeGitHubApiContextService(files);
        var options = Options.Create(new CodebaseDocumentationOptions());
        var loader = new CodebaseContextLoader(githubContext, options, NullLogger<CodebaseContextLoader>.Instance);

        var context = await loader.LoadRelevantContextAsync("main", "Sample story", "No keywords");

        var contractIndex = context.IndexOf("<!-- ORCHESTRATION_CONTRACT.md -->", StringComparison.Ordinal);
        var contextIndex = context.IndexOf("<!-- CONTEXT_INDEX.md -->", StringComparison.Ordinal);

        Assert.True(contractIndex >= 0, "Expected ORCHESTRATION_CONTRACT.md section to be included.");
        Assert.True(contextIndex >= 0, "Expected CONTEXT_INDEX.md section to be included.");
        Assert.True(contractIndex < contextIndex, "Expected orchestration contract to be loaded before context index.");
    }

    private sealed class FakeGitHubApiContextService : IGitHubApiContextService
    {
        private readonly Dictionary<string, string?> _files;

        public FakeGitHubApiContextService(Dictionary<string, string?> files)
        {
            _files = files;
        }

        public Task<IReadOnlyList<string>> GetFileTreeAsync(string branch, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(_files.Keys.ToList());

        public Task<IReadOnlyDictionary<string, string?>> GetFileContentsAsync(
            string branch,
            IReadOnlyList<string> paths,
            CancellationToken ct = default)
        {
            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                var normalized = path.Replace('\\', '/');
                _files.TryGetValue(normalized, out var value);
                result[path] = value;
            }
            return Task.FromResult<IReadOnlyDictionary<string, string?>>(result);
        }

        public Task<string> GetRecentCommitSummaryAsync(string branch, int count = 20, CancellationToken ct = default)
            => Task.FromResult(string.Empty);

        public Task WriteFileAsync(string branch, string path, string content, string commitMessage, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task WriteFilesAsync(string branch, IReadOnlyDictionary<string, string> files, string commitMessage, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
