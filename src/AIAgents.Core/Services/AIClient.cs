using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIAgents.Core.Configuration;
using AIAgents.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgents.Core.Services;

/// <summary>
/// Thin AI completion client that handles HTTP transport to OpenAI-compatible endpoints.
/// All prompt engineering is owned by the agent services, not by this client.
/// Supports per-agent model overrides via <see cref="IAIClientFactory"/>.
/// </summary>
public sealed class AIClient : IAIClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AIOptions _options;
    private readonly ILogger _logger;

    /// <summary>
    /// Standard DI constructor — uses IOptions&lt;AIOptions&gt; (default config).
    /// </summary>
    public AIClient(
        IHttpClientFactory httpClientFactory,
        IOptions<AIOptions> options,
        ILogger<AIClient> logger)
        : this(httpClientFactory, options.Value, logger)
    {
    }

    /// <summary>
    /// Internal constructor used by <see cref="AIClientFactory"/> to create
    /// per-agent instances with merged configuration.
    /// </summary>
    internal AIClient(
        IHttpClientFactory httpClientFactory,
        AIOptions options,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        AICompletionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Use the resilience-enabled named client but set auth per-request
        // so each AIClient instance can target a different provider/model.
        var client = _httpClientFactory.CreateClient("AIClient");

        var requestBody = BuildRequestBody(systemPrompt, userPrompt, options);

        _logger.LogDebug(
            "Sending completion request to {Provider} model {Model}, max_tokens={MaxTokens}",
            _options.Provider, _options.Model, options?.MaxTokens ?? _options.MaxTokens);

        var url = BuildFullUrl();

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        ConfigureAuth(request);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        var response = await client.SendAsync(request, cancellationToken);

        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<ChatCompletionResponse>(responseJson);

        var content = result?.Choices?.FirstOrDefault()?.Message?.Content;

        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.LogWarning("AI completion returned empty content");
            throw new InvalidOperationException("AI completion returned empty content.");
        }

        _logger.LogDebug(
            "Completion received: {TokenCount} characters",
            content.Length);

        return content;
    }

    private object BuildRequestBody(string systemPrompt, string userPrompt, AICompletionOptions? options)
    {
        return new
        {
            model = _options.Model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            max_tokens = options?.MaxTokens ?? _options.MaxTokens,
            temperature = options?.Temperature ?? _options.Temperature
        };
    }

    /// <summary>
    /// Builds the full absolute URL for the completion endpoint,
    /// combining the base endpoint with the provider-specific path.
    /// </summary>
    private string BuildFullUrl()
    {
        var baseUri = !string.IsNullOrEmpty(_options.Endpoint)
            ? _options.Endpoint.TrimEnd('/')
            : "https://api.openai.com";

        var path = _options.Provider.ToUpperInvariant() switch
        {
            "AZUREOPENAI" => $"/openai/deployments/{_options.Model}/chat/completions?api-version=2024-08-01-preview",
            _ => "/v1/chat/completions"  // OpenAI, OpenRouter, LiteLLM, etc.
        };

        return baseUri + path;
    }

    /// <summary>
    /// Sets the authentication header on the request based on the provider.
    /// </summary>
    private void ConfigureAuth(HttpRequestMessage request)
    {
        if (_options.Provider.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Add("api-key", _options.ApiKey);
        }
        else
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }

    // Internal response DTOs for deserializing OpenAI-compatible responses
    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public List<Choice>? Choices { get; init; }
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")]
        public MessageContent? Message { get; init; }
    }

    private sealed class MessageContent
    {
        [JsonPropertyName("content")]
        public string? Content { get; init; }
    }
}
