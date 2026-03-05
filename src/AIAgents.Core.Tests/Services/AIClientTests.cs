using System.Net;
using AIAgents.Core.Configuration;
using AIAgents.Core.Models;
using AIAgents.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;

namespace AIAgents.Core.Tests.Services;

/// <summary>
/// Tests for the AIClient service covering HTTP transport, response parsing,
/// token tracking, and error handling.
/// </summary>
public sealed class AIClientTests
{
    private readonly AIOptions _defaultOptions = new()
    {
        Provider = "OpenAI",
        Model = "gpt-4o",
        ApiKey = "sk-test-key",
        Endpoint = "https://api.openai.com",
        MaxTokens = 4096,
        Temperature = 0.3
    };

    private (AIClient client, Mock<HttpMessageHandler> handler) CreateClient(
        AIOptions? options = null,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? responseBody = null)
    {
        var opts = options ?? _defaultOptions;

        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody ?? CompletionJson())
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("AIClient")).Returns(httpClient);

        var client = new AIClient(factoryMock.Object, opts, NullLogger<AIClient>.Instance);
        return (client, handlerMock);
    }

    private static string CompletionJson(
        string content = "Hello world",
        int promptTokens = 100,
        int completionTokens = 50,
        string model = "gpt-4o")
    {
        return $$"""
        {
            "id": "chatcmpl-test",
            "object": "chat.completion",
            "choices": [
                {
                    "index": 0,
                    "message": { "role": "assistant", "content": "{{content}}" },
                    "finish_reason": "stop"
                }
            ],
            "usage": {
                "prompt_tokens": {{promptTokens}},
                "completion_tokens": {{completionTokens}},
                "total_tokens": {{promptTokens + completionTokens}}
            }
        }
        """;
    }

    private static string ResponsesJson(
        string content = "Hello from responses",
        int inputTokens = 120,
        int outputTokens = 60,
        string id = "resp_test_123")
    {
        return $$"""
        {
            "id": "{{id}}",
            "object": "response",
            "output": [
                {
                    "type": "message",
                    "role": "assistant",
                    "content": [
                        { "type": "output_text", "text": "{{content}}" }
                    ]
                }
            ],
            "usage": {
                "input_tokens": {{inputTokens}},
                "output_tokens": {{outputTokens}}
            }
        }
        """;
    }

    private static string ResponsesJsonWithDirectOutputText(
        string content = "Direct output text",
        int inputTokens = 50,
        int outputTokens = 20,
        string id = "resp_direct_123")
    {
        return $$"""
        {
            "id": "{{id}}",
            "object": "response",
            "output": [
                { "type": "output_text", "text": "{{content}}" }
            ],
            "usage": {
                "input_tokens": {{inputTokens}},
                "output_tokens": {{outputTokens}}
            }
        }
        """;
    }

    [Fact]
    public async Task CompleteAsync_ValidResponse_ReturnsContentAndUsage()
    {
        var (client, _) = CreateClient(responseBody: CompletionJson("Test output", 200, 100));

        var result = await client.CompleteAsync("system", "user");

        Assert.Equal("Test output", result.Content);
        Assert.NotNull(result.Usage);
        Assert.Equal(200, result.Usage!.InputTokens);
        Assert.Equal(100, result.Usage.OutputTokens);
        Assert.Equal(300, result.Usage.TotalTokens);
        Assert.Equal("gpt-4o", result.Usage.Model);
        Assert.True(result.Usage.EstimatedCost > 0);
    }

    [Fact]
    public async Task CompleteAsync_ImplicitStringConversion_Works()
    {
        var (client, _) = CreateClient(responseBody: CompletionJson("Implicit test"));

        var result = await client.CompleteAsync("system", "user");

        // Test implicit conversion
        string asString = result;
        Assert.Equal("Implicit test", asString);
    }

    [Fact]
    public async Task CompleteAsync_NoUsageData_ReturnsNullUsage()
    {
        var responseJson = """
        {
            "id": "chatcmpl-test",
            "choices": [
                {
                    "index": 0,
                    "message": { "role": "assistant", "content": "No usage response" },
                    "finish_reason": "stop"
                }
            ]
        }
        """;

        var (client, _) = CreateClient(responseBody: responseJson);

        var result = await client.CompleteAsync("system", "user");

        Assert.Equal("No usage response", result.Content);
        Assert.Null(result.Usage);
    }

    [Fact]
    public async Task CompleteAsync_EmptyContent_ThrowsInvalidOperation()
    {
        var responseJson = """
        {
            "id": "chatcmpl-test",
            "choices": [
                {
                    "index": 0,
                    "message": { "role": "assistant", "content": "" },
                    "finish_reason": "stop"
                }
            ]
        }
        """;

        var (client, _) = CreateClient(responseBody: responseJson);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CompleteAsync("system", "user"));
    }

    [Fact]
    public async Task CompleteAsync_HttpError_ThrowsHttpRequestException()
    {
        var (client, _) = CreateClient(
            statusCode: HttpStatusCode.InternalServerError,
            responseBody: "Server Error");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.CompleteAsync("system", "user"));
    }

    [Fact]
    public async Task CompleteAsync_AzureOpenAI_UsesApiKeyHeader()
    {
        var options = new AIOptions
        {
            Provider = "AzureOpenAI",
            Model = "gpt-4o",
            ApiKey = "azure-key",
            Endpoint = "https://myresource.openai.azure.com",
            MaxTokens = 4096,
            Temperature = 0.3
        };

        HttpRequestMessage? capturedRequest = null;
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CompletionJson())
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("AIClient")).Returns(httpClient);

        var client = new AIClient(factoryMock.Object, options, NullLogger<AIClient>.Instance);
        await client.CompleteAsync("system", "user");

        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest!.Headers.Contains("api-key"));
        Assert.Contains("openai/deployments/gpt-4o", capturedRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task CompleteAsync_OpenAI_UsesBearerAuth()
    {
        HttpRequestMessage? capturedRequest = null;
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CompletionJson())
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("AIClient")).Returns(httpClient);

        var client = new AIClient(factoryMock.Object, _defaultOptions, NullLogger<AIClient>.Instance);
        await client.CompleteAsync("system", "user");

        Assert.NotNull(capturedRequest);
        Assert.Equal("Bearer", capturedRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("sk-test-key", capturedRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task CompleteAsync_WithOptions_OverridesDefaults()
    {
        string? capturedBody = null;
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>(async (req, _) =>
            {
                capturedBody = await req.Content!.ReadAsStringAsync();
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CompletionJson())
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("AIClient")).Returns(httpClient);

        var client = new AIClient(factoryMock.Object, _defaultOptions, NullLogger<AIClient>.Instance);
        await client.CompleteAsync("system", "user",
            new Interfaces.AICompletionOptions { MaxTokens = 8192, Temperature = 0.7 });

        Assert.NotNull(capturedBody);
        Assert.Contains("8192", capturedBody!);
        Assert.Contains("0.7", capturedBody);
    }

    [Fact]
    public async Task CompleteAsync_TokenCost_CalculatedCorrectly()
    {
        // GPT-4o: $2.50/M input, $10.00/M output
        var (client, _) = CreateClient(responseBody: CompletionJson("test", 1000, 500));

        var result = await client.CompleteAsync("system", "user");

        Assert.NotNull(result.Usage);
        // Expected: (1000 * 2.50 / 1M) + (500 * 10.00 / 1M) = 0.0025 + 0.005 = 0.0075
        Assert.Equal(0.0075m, result.Usage!.EstimatedCost);
    }

    [Fact]
    public async Task CompleteAsync_DefaultEndpoint_UsesOpenAI()
    {
        var options = new AIOptions
        {
            Provider = "OpenAI",
            Model = "gpt-4o",
            ApiKey = "sk-test",
            Endpoint = null,
            MaxTokens = 4096,
            Temperature = 0.3
        };

        HttpRequestMessage? capturedRequest = null;
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CompletionJson())
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("AIClient")).Returns(httpClient);

        var client = new AIClient(factoryMock.Object, options, NullLogger<AIClient>.Instance);
        await client.CompleteAsync("system", "user");

        Assert.Contains("api.openai.com", capturedRequest!.RequestUri!.ToString());
        Assert.Contains("/v1/chat/completions", capturedRequest.RequestUri.ToString());
    }

    [Fact]
    public async Task CompleteAsync_OpenAIPremiumModel_UsesResponsesApiEndpoint()
    {
        var options = new AIOptions
        {
            Provider = "OpenAI",
            Model = "gpt-5",
            ApiKey = "sk-test",
            Endpoint = "https://api.openai.com",
            MaxTokens = 4096,
            Temperature = 0.3
        };

        HttpRequestMessage? capturedRequest = null;
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponsesJson("Premium OK"))
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("AIClient")).Returns(httpClient);

        var client = new AIClient(factoryMock.Object, options, NullLogger<AIClient>.Instance);
        var result = await client.CompleteAsync("system", "user");

        Assert.Equal("Premium OK", result.Content);
        Assert.NotNull(capturedRequest);
        Assert.Contains("/v1/responses", capturedRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task CompleteAsync_OpenAIPremiumModel_ParsesResponsesUsage()
    {
        var options = new AIOptions
        {
            Provider = "OpenAI",
            Model = "gpt-5",
            ApiKey = "sk-test",
            Endpoint = "https://api.openai.com",
            MaxTokens = 4096,
            Temperature = 0.3
        };

        var (client, _) = CreateClient(
            options: options,
            responseBody: ResponsesJson("Usage check", 321, 123));

        var result = await client.CompleteAsync("system", "user");

        Assert.Equal("Usage check", result.Content);
        Assert.NotNull(result.Usage);
        Assert.Equal(321, result.Usage!.InputTokens);
        Assert.Equal(123, result.Usage.OutputTokens);
        Assert.Equal(444, result.Usage.TotalTokens);
        Assert.Equal("gpt-5", result.Usage.Model);
    }

    [Fact]
    public async Task CompleteAsync_OpenAIPremiumModel_ClampsMaxOutputTokensToMinimum()
    {
        var options = new AIOptions
        {
            Provider = "OpenAI",
            Model = "gpt-5",
            ApiKey = "sk-test",
            Endpoint = "https://api.openai.com",
            MaxTokens = 8,
            Temperature = 0.3
        };

        string? capturedBody = null;
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>(async (req, _) =>
            {
                capturedBody = await req.Content!.ReadAsStringAsync();
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponsesJson("Clamp check"))
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("AIClient")).Returns(httpClient);

        var client = new AIClient(factoryMock.Object, options, NullLogger<AIClient>.Instance);
        await client.CompleteAsync("system", "user", new Interfaces.AICompletionOptions { MaxTokens = 5 });

        Assert.NotNull(capturedBody);
        Assert.Contains("\"max_output_tokens\":16", capturedBody!);
    }

    [Fact]
    public async Task CompleteAsync_OpenAIPremiumModel_ParsesDirectOutputTextBlocks()
    {
        var options = new AIOptions
        {
            Provider = "OpenAI",
            Model = "gpt-5",
            ApiKey = "sk-test",
            Endpoint = "https://api.openai.com",
            MaxTokens = 4096,
            Temperature = 0.3
        };

        var (client, _) = CreateClient(
            options: options,
            responseBody: ResponsesJsonWithDirectOutputText("Direct OK", 44, 22));

        var result = await client.CompleteAsync("system", "user");

        Assert.Equal("Direct OK", result.Content);
        Assert.NotNull(result.Usage);
        Assert.Equal(44, result.Usage!.InputTokens);
        Assert.Equal(22, result.Usage.OutputTokens);
    }
}
