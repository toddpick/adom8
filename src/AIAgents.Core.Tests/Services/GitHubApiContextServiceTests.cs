using System.Net;
using System.Text.Json;
using AIAgents.Core.Configuration;
using AIAgents.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;

namespace AIAgents.Core.Tests.Services;

public sealed class GitHubApiContextServiceTests
{
    private static GitHubOptions CreateOptions() => new()
    {
        Owner = "toddpick",
        Repo = "adom8",
        Token = "ghp_test"
    };

    [Fact]
    public async Task GetFileTreeAsync_GitRefVisibleAfterInitial404_RetriesAndSucceeds()
    {
        var gitRefResponseCount = 0;
        var branchResponseCount = 0;

        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var path = request.RequestUri!.PathAndQuery;

                if (path.Contains("/git/ref/heads/feature%2FUS-149", StringComparison.OrdinalIgnoreCase))
                {
                    gitRefResponseCount++;
                    if (gitRefResponseCount == 1)
                    {
                        return new HttpResponseMessage(HttpStatusCode.NotFound)
                        {
                            Content = new StringContent("{\"message\":\"Branch not found\"}")
                        };
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        {
                          "object": {
                            "sha": "abc123def456"
                          }
                        }
                        """)
                    };
                }

                if (path.Contains("/branches/feature%2FUS-149", StringComparison.OrdinalIgnoreCase))
                {
                    branchResponseCount++;
                    return new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent("{\"message\":\"Branch endpoint not visible yet\"}")
                    };
                }

                if (path.Contains("/git/trees/abc123def456?recursive=1", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        {
                          "tree": [
                            { "path": "src/App.jsx", "type": "blob" },
                            { "path": "src", "type": "tree" }
                          ]
                        }
                        """)
                    };
                }

                throw new InvalidOperationException($"Unexpected GitHub API request: {path}");
            });

        var httpClient = new HttpClient(handlerMock.Object)
        {
            BaseAddress = new Uri("https://api.github.com/")
        };

        var service = new GitHubApiContextService(
            Options.Create(CreateOptions()),
            NullLogger<GitHubApiContextService>.Instance,
            httpClient);

        var files = await service.GetFileTreeAsync("feature/US-149");

        Assert.Equal(2, gitRefResponseCount);
        Assert.Equal(1, branchResponseCount);
        Assert.Single(files);
        Assert.Equal("src/App.jsx", files[0]);
    }

    [Fact]
    public async Task GetFileTreeAsync_BranchEndpointVisibleBeforeGitRef_Succeeds()
    {
        var gitRefResponseCount = 0;
        var branchResponseCount = 0;

        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var path = request.RequestUri!.PathAndQuery;

                if (path.Contains("/git/ref/heads/feature%2FUS-150", StringComparison.OrdinalIgnoreCase))
                {
                    gitRefResponseCount++;
                    return new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent("{\"message\":\"Git ref not found yet\"}")
                    };
                }

                if (path.Contains("/branches/feature%2FUS-150", StringComparison.OrdinalIgnoreCase))
                {
                    branchResponseCount++;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        {
                          "commit": {
                            "sha": "fed654cba321"
                          }
                        }
                        """)
                    };
                }

                if (path.Contains("/git/trees/fed654cba321?recursive=1", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        {
                          "tree": [
                            { "path": "src/Feature.cs", "type": "blob" }
                          ]
                        }
                        """)
                    };
                }

                throw new InvalidOperationException($"Unexpected GitHub API request: {path}");
            });

        var httpClient = new HttpClient(handlerMock.Object)
        {
            BaseAddress = new Uri("https://api.github.com/")
        };

        var service = new GitHubApiContextService(
            Options.Create(CreateOptions()),
            NullLogger<GitHubApiContextService>.Instance,
            httpClient);

        var files = await service.GetFileTreeAsync("feature/US-150");

        Assert.Equal(1, gitRefResponseCount);
        Assert.Equal(1, branchResponseCount);
        Assert.Single(files);
        Assert.Equal("src/Feature.cs", files[0]);
    }
}
