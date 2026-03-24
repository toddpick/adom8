using System.Net;
using System.Text;
using System.Text.Json;
using AIAgents.Core.Configuration;
using AIAgents.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIAgents.Core.Tests.Services;

public sealed class GitHubRepositoryProviderTests
{
    private static GitHubOptions CreateOptions() => new()
    {
        Owner = "contoso",
        Repo = "adom8",
        Token = "ghp_test"
    };

    [Fact]
    public async Task CreatePullRequestAsync_BlankTargetBranch_UsesRepositoryDefaultBranch()
    {
        var handler = new FakeGitHubHandler();
        handler.AddResponse(HttpMethod.Get, "/repos/contoso/adom8", () => Json(HttpStatusCode.OK, new { default_branch = "dev" }));
        handler.AddResponse(HttpMethod.Post, "/repos/contoso/adom8/pulls", requestBody =>
        {
            using var doc = JsonDocument.Parse(requestBody);
            Assert.Equal("feature/US-101", doc.RootElement.GetProperty("head").GetString());
            Assert.Equal("dev", doc.RootElement.GetProperty("base").GetString());
            return Json(HttpStatusCode.Created, new { number = 51 });
        });

        var provider = CreateProvider(handler);

        var prNumber = await provider.CreatePullRequestAsync(
            "feature/US-101",
            "",
            "US-101: Example",
            "Body");

        Assert.Equal(51, prNumber);
    }

    [Fact]
    public async Task CreatePullRequestAsync_InvalidConfiguredBaseBranch_FallsBackToRepositoryDefaultBranch()
    {
        var handler = new FakeGitHubHandler();
        handler.AddResponse(HttpMethod.Post, "/repos/contoso/adom8/pulls", requestBody =>
        {
            using var doc = JsonDocument.Parse(requestBody);
            var baseBranch = doc.RootElement.GetProperty("base").GetString();

            if (string.Equals(baseBranch, "main", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
                {
                    Content = new StringContent(
                        "{\"message\":\"Validation Failed\",\"errors\":[{\"resource\":\"PullRequest\",\"field\":\"base\",\"code\":\"invalid\"}]}",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            Assert.Equal("dev", baseBranch);
            return Json(HttpStatusCode.Created, new { number = 77 });
        });
        handler.AddResponse(HttpMethod.Get, "/repos/contoso/adom8", () => Json(HttpStatusCode.OK, new { default_branch = "dev" }));

        var provider = CreateProvider(handler);

        var prNumber = await provider.CreatePullRequestAsync(
            "feature/US-202",
            "main",
            "US-202: Example",
            "Body");

        Assert.Equal(77, prNumber);
        Assert.Equal(2, handler.CountRequests(HttpMethod.Post, "/repos/contoso/adom8/pulls"));
    }

    private static GitHubRepositoryProvider CreateProvider(FakeGitHubHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.github.com/")
        };

        return new GitHubRepositoryProvider(
            Options.Create(CreateOptions()),
            NullLogger<GitHubRepositoryProvider>.Instance,
            httpClient);
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, object payload)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
    }

    private sealed class FakeGitHubHandler : HttpMessageHandler
    {
        private readonly List<Route> _routes = [];
        private readonly List<RequestRecord> _requests = [];

        public void AddResponse(HttpMethod method, string pathAndQuery, Func<HttpResponseMessage> responseFactory)
        {
            _routes.Add(new Route(method, pathAndQuery, _ => responseFactory()));
        }

        public void AddResponse(HttpMethod method, string pathAndQuery, Func<string, HttpResponseMessage> responseFactory)
        {
            _routes.Add(new Route(method, pathAndQuery, responseFactory));
        }

        public int CountRequests(HttpMethod method, string pathAndQuery)
        {
            return _requests.Count(request =>
                request.Method == method &&
                string.Equals(request.PathAndQuery, pathAndQuery, StringComparison.Ordinal));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
            var requestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            _requests.Add(new RequestRecord(request.Method, pathAndQuery, requestBody));

            var route = _routes.FirstOrDefault(candidate =>
                candidate.Method == request.Method &&
                string.Equals(candidate.PathAndQuery, pathAndQuery, StringComparison.Ordinal));

            if (route is null)
            {
                throw new InvalidOperationException($"Unexpected GitHub request: {request.Method} {pathAndQuery}");
            }

            return route.ResponseFactory(requestBody);
        }

        private sealed record Route(HttpMethod Method, string PathAndQuery, Func<string, HttpResponseMessage> ResponseFactory);

        private sealed record RequestRecord(HttpMethod Method, string PathAndQuery, string Body);
    }
}
