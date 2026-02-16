using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIAgents.Core.Configuration;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgents.Core.Services;

/// <summary>
/// Thin AI completion client that handles HTTP transport to both
/// Anthropic (Claude Messages API) and OpenAI-compatible endpoints.
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

    private bool IsClaude => _options.Provider.Equals("Claude", StringComparison.OrdinalIgnoreCase)
                          || _options.Provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase);

    public async Task<AICompletionResult> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        AICompletionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient("AIClient");

        _logger.LogDebug(
            "Sending completion request to {Provider} model {Model}, max_tokens={MaxTokens}",
            _options.Provider, _options.Model, options?.MaxTokens ?? _options.MaxTokens);

        var url = BuildFullUrl();
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        ConfigureAuth(request);

        var requestBody = IsClaude
            ? BuildClaudeRequestBody(systemPrompt, userPrompt, options)
            : BuildOpenAIRequestBody(systemPrompt, userPrompt, options);

        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

        string? content;
        TokenUsageData? usage;

        if (IsClaude)
        {
            (content, usage) = ParseClaudeResponse(responseJson);
        }
        else
        {
            (content, usage) = ParseOpenAIResponse(responseJson);
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.LogWarning("AI completion returned empty content");
            throw new InvalidOperationException("AI completion returned empty content.");
        }

        _logger.LogDebug("Completion received: {CharCount} characters", content.Length);

        return new AICompletionResult
        {
            Content = content,
            Usage = usage
        };
    }

    // ──────────── Request Body Builders ────────────

    private object BuildOpenAIRequestBody(string systemPrompt, string userPrompt, AICompletionOptions? options)
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

    private object BuildClaudeRequestBody(string systemPrompt, string userPrompt, AICompletionOptions? options)
    {
        // Anthropic Messages API: system is a top-level field, not a message role
        return new
        {
            model = _options.Model,
            system = systemPrompt,
            messages = new object[]
            {
                new { role = "user", content = userPrompt }
            },
            max_tokens = options?.MaxTokens ?? _options.MaxTokens,
            temperature = options?.Temperature ?? _options.Temperature
        };
    }

    // ──────────── Response Parsers ────────────

    private (string? content, TokenUsageData? usage) ParseOpenAIResponse(string responseJson)
    {
        var result = JsonSerializer.Deserialize<OpenAIChatResponse>(responseJson);
        var content = result?.Choices?.FirstOrDefault()?.Message?.Content;
        TokenUsageData? usage = null;

        try
        {
            if (result?.Usage is not null)
            {
                var inputTokens = result.Usage.PromptTokens;
                var outputTokens = result.Usage.CompletionTokens;
                var totalTokens = result.Usage.TotalTokens > 0
                    ? result.Usage.TotalTokens
                    : inputTokens + outputTokens;
                var cost = TokenCostCalculator.Calculate(_options.Model, inputTokens, outputTokens);

                usage = new TokenUsageData
                {
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    TotalTokens = totalTokens,
                    EstimatedCost = cost,
                    Model = _options.Model
                };

                _logger.LogDebug(
                    "Token usage: {Input} in / {Output} out / ${Cost:F6} est. cost ({Model})",
                    inputTokens, outputTokens, cost, _options.Model);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse token usage data — continuing without usage tracking");
        }

        return (content, usage);
    }

    private (string? content, TokenUsageData? usage) ParseClaudeResponse(string responseJson)
    {
        var result = JsonSerializer.Deserialize<ClaudeMessagesResponse>(responseJson);

        // Claude returns content as an array of content blocks
        var content = result?.Content?
            .Where(c => c.Type == "text")
            .Select(c => c.Text)
            .FirstOrDefault();

        TokenUsageData? usage = null;

        try
        {
            if (result?.Usage is not null)
            {
                var inputTokens = result.Usage.InputTokens;
                var outputTokens = result.Usage.OutputTokens;
                var totalTokens = inputTokens + outputTokens;
                var cost = TokenCostCalculator.Calculate(_options.Model, inputTokens, outputTokens);

                usage = new TokenUsageData
                {
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    TotalTokens = totalTokens,
                    EstimatedCost = cost,
                    Model = _options.Model
                };

                _logger.LogDebug(
                    "Token usage: {Input} in / {Output} out / ${Cost:F6} est. cost ({Model})",
                    inputTokens, outputTokens, cost, _options.Model);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Claude usage data — continuing without usage tracking");
        }

        return (content, usage);
    }

    // ──────────── URL & Auth ────────────

    /// <summary>
    /// Builds the full absolute URL for the completion endpoint.
    /// </summary>
    private string BuildFullUrl()
    {
        if (IsClaude)
        {
            var baseUri = !string.IsNullOrEmpty(_options.Endpoint)
                ? _options.Endpoint.TrimEnd('/')
                : "https://api.anthropic.com";
            return baseUri + "/v1/messages";
        }

        var openAiBase = !string.IsNullOrEmpty(_options.Endpoint)
            ? _options.Endpoint.TrimEnd('/')
            : "https://api.openai.com";

        var path = _options.Provider.ToUpperInvariant() switch
        {
            "AZUREOPENAI" => $"/openai/deployments/{_options.Model}/chat/completions?api-version=2024-08-01-preview",
            "GOOGLE" => "/chat/completions",  // Google's OpenAI-compatible endpoint already includes /v1beta/openai
            _ => "/v1/chat/completions"  // OpenAI, OpenRouter, LiteLLM, etc.
        };

        return openAiBase + path;
    }

    /// <summary>
    /// Sets the authentication header on the request based on the provider.
    /// </summary>
    private void ConfigureAuth(HttpRequestMessage request)
    {
        if (IsClaude)
        {
            // Anthropic uses x-api-key header + anthropic-version
            request.Headers.Add("x-api-key", _options.ApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
        }
        else if (_options.Provider.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Add("api-key", _options.ApiKey);
        }
        else
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }

    // ──────────── Response DTOs ────────────

    // OpenAI-compatible response format
    private sealed class OpenAIChatResponse
    {
        [JsonPropertyName("choices")]
        public List<OpenAIChoice>? Choices { get; init; }

        [JsonPropertyName("usage")]
        public OpenAIUsageData? Usage { get; init; }
    }

    private sealed class OpenAIChoice
    {
        [JsonPropertyName("message")]
        public OpenAIMessage? Message { get; init; }
    }

    private sealed class OpenAIMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; init; }
    }

    private sealed class OpenAIUsageData
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; init; }

        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; init; }

        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; init; }
    }

    // Anthropic Claude Messages API response format
    private sealed class ClaudeMessagesResponse
    {
        [JsonPropertyName("content")]
        public List<ClaudeContentBlock>? Content { get; init; }

        [JsonPropertyName("usage")]
        public ClaudeUsageData? Usage { get; init; }

        [JsonPropertyName("model")]
        public string? Model { get; init; }

        [JsonPropertyName("stop_reason")]
        public string? StopReason { get; init; }
    }

    private sealed class ClaudeContentBlock
    {
        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }
    }

    private sealed class ClaudeUsageData
    {
        [JsonPropertyName("input_tokens")]
        public int InputTokens { get; init; }

        [JsonPropertyName("output_tokens")]
        public int OutputTokens { get; init; }
    }
}
