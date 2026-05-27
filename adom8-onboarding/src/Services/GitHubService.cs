namespace ADOm8.Onboarding.Services;

public interface IGitHubService
{
    Task ValidateRepositoryAccessAsync(string repoUrl, string pat, string branch, CancellationToken cancellationToken = default);
}

public sealed class GitHubService : IGitHubService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public GitHubService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task ValidateRepositoryAccessAsync(string repoUrl, string pat, string branch, CancellationToken cancellationToken = default)
    {
        var (owner, repo) = ParseRepo(repoUrl);
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pat);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("adom8-onboarding");

        var repoResponse = await client.GetAsync($"https://api.github.com/repos/{owner}/{repo}", cancellationToken);
        if (!repoResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub repository validation failed with {(int)repoResponse.StatusCode}.");
        }

        var branchResponse = await client.GetAsync($"https://api.github.com/repos/{owner}/{repo}/branches/{branch}", cancellationToken);
        if (!branchResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub branch validation failed with {(int)branchResponse.StatusCode}.");
        }
    }

    private static (string owner, string repo) ParseRepo(string repoUrl)
    {
        string path;
        if (Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri))
        {
            path = uri.AbsolutePath;
        }
        else
        {
            path = repoUrl;
        }

        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ArgumentException("Invalid GitHub repository URL.");
        return (parts[0], parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1]);
    }
}
