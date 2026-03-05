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

    private bool IsOpenAIResponsesModel
    {
        get
        {
            if (!_options.Provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(_options.Model))
            {
                return false;
            }

            var model = _options.Model.Trim().ToLowerInvariant();
            return model.StartsWith("gpt-5")
                || model.StartsWith("codex")
                || model.StartsWith("o1")
                || model.StartsWith("o3")
                || model.StartsWith("o4");
        }
    }

    public async Task<AICompletionResult> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        AICompletionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (IsOpenAIResponsesModel)
        {
            return await CompleteWithOpenAIResponsesApiAsync(systemPrompt, userPrompt, options, cancellationToken);
        }

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

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "AI API request failed ({StatusCode}): URL={Url}, Provider={Provider}, Model={Model}, Body={Body}",
                response.StatusCode, url, _options.Provider, _options.Model, errorBody);
            throw new HttpRequestException(
                $"AI API request failed ({(int)response.StatusCode} {response.StatusCode}) at URL '{url}' for provider '{_options.Provider}' model '{_options.Model}'. Response: {errorBody}",
                null,
                response.StatusCode);
        }

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

    private async Task<AICompletionResult> CompleteWithOpenAIResponsesApiAsync(
        string systemPrompt,
        string userPrompt,
        AICompletionOptions? options,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("AIClient");

        _logger.LogDebug(
            "Sending responses API request to {Provider} model {Model}, max_output_tokens={MaxTokens}",
            _options.Provider, _options.Model, options?.MaxTokens ?? _options.MaxTokens);

        var request = new HttpRequestMessage(HttpMethod.Post, BuildFullUrl());
        ConfigureAuth(request);

        var requestBody = BuildOpenAIResponsesRequestBody(systemPrompt, userPrompt, options);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody, _jsonOptions),
            Encoding.UTF8,
            "application/json");

        var response = await client.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "AI Responses API request failed ({StatusCode}): URL={Url}, Provider={Provider}, Model={Model}, Body={Body}",
                response.StatusCode, request.RequestUri, _options.Provider, _options.Model, errorBody);
            throw new HttpRequestException(
                $"AI API request failed ({(int)response.StatusCode} {response.StatusCode}) at URL '{request.RequestUri}' for provider '{_options.Provider}' model '{_options.Model}'. Response: {errorBody}",
                null,
                response.StatusCode);
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var (content, usage) = ParseOpenAIResponsesResponse(responseJson);

        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.LogWarning("AI responses API completion returned empty content");
            throw new InvalidOperationException("AI completion returned empty content.");
        }

        _logger.LogDebug("Responses API completion received: {CharCount} characters", content.Length);

        return new AICompletionResult
        {
            Content = content,
            Usage = usage
        };
    }

    // ──────────── Agentic Tool-Use Loop ────────────

    public async Task<AgenticResult> CompleteWithToolsAsync(
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<ToolDefinition> tools,
        Func<ToolCall, CancellationToken, Task<string>> toolExecutor,
        AgenticOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new AgenticOptions();
        if (IsOpenAIResponsesModel)
        {
            return await OpenAIResponsesAgenticLoopAsync(systemPrompt, userPrompt, tools, toolExecutor, options, cancellationToken);
        }

        return IsClaude
            ? await ClaudeAgenticLoopAsync(systemPrompt, userPrompt, tools, toolExecutor, options, cancellationToken)
            : await OpenAIAgenticLoopAsync(systemPrompt, userPrompt, tools, toolExecutor, options, cancellationToken);
    }

    private async Task<AgenticResult> OpenAIResponsesAgenticLoopAsync(
        string systemPrompt, string userPrompt,
        IReadOnlyList<ToolDefinition> tools,
        Func<ToolCall, CancellationToken, Task<string>> toolExecutor,
        AgenticOptions options, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("AIClient");
        var url = BuildFullUrl();

        var responseTools = tools.Select(t => new
        {
            type = "function",
            name = t.Name,
            description = t.Description,
            parameters = t.InputSchema
        }).ToArray();

        int totalInputTokens = 0, totalOutputTokens = 0;
        decimal totalCost = 0m;
        var toolCallLog = new List<ToolCallLog>();
        string? finalResponse = null;
        bool completedNaturally = false;
        int round = 0;
        string? previousResponseId = null;
        List<object>? nextInput = null;

        for (round = 1; round <= options.MaxRounds; round++)
        {
            _logger.LogInformation("OpenAI Responses agentic round {Round}/{MaxRounds}", round, options.MaxRounds);

            object requestBody;
            if (previousResponseId is null)
            {
                requestBody = new
                {
                    model = _options.Model,
                    input = new object[]
                    {
                        new { role = "system", content = new object[] { new { type = "input_text", text = systemPrompt } } },
                        new { role = "user", content = new object[] { new { type = "input_text", text = userPrompt } } }
                    },
                    tools = responseTools,
                    tool_choice = "auto",
                    max_output_tokens = NormalizeResponsesMaxOutputTokens(options.MaxTokens)
                };
            }
            else
            {
                requestBody = new
                {
                    model = _options.Model,
                    previous_response_id = previousResponseId,
                    input = nextInput,
                    tools = responseTools,
                    tool_choice = "auto",
                    max_output_tokens = NormalizeResponsesMaxOutputTokens(options.MaxTokens)
                };
            }

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            ConfigureAuth(request);
            request.Content = new StringContent(
                JsonSerializer.Serialize(requestBody, _jsonOptions),
                Encoding.UTF8,
                "application/json");

            var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Responses agentic round {Round} failed ({StatusCode}): {Body}",
                    round, response.StatusCode, errorBody);
                response.EnsureSuccessStatusCode();
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            previousResponseId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : previousResponseId;

            if (root.TryGetProperty("usage", out var usage))
            {
                var inTok = usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
                var outTok = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
                totalInputTokens += inTok;
                totalOutputTokens += outTok;
                totalCost += TokenCostCalculator.Calculate(_options.Model, inTok, outTok);
            }

            var (textParts, functionCalls) = ParseOpenAIResponsesOutput(root);

            _logger.LogInformation("Responses round {Round}: text_parts={TextCount}, function_calls={FunctionCalls}",
                round, textParts.Count, functionCalls.Count);

            if (functionCalls.Count > 0)
            {
                var toolCalls = functionCalls.Select(fc => (fc.callId, fc.name, fc.argumentsJson)).ToList();
                var toolResults = await ExecuteToolCallsAsync(toolCalls, toolExecutor, toolCallLog, round, cancellationToken);

                nextInput = toolResults.Select(tr => (object)new
                {
                    type = "function_call_output",
                    call_id = tr.id,
                    output = tr.result
                }).ToList();

                continue;
            }

            finalResponse = string.Join("\n", textParts.Where(p => !string.IsNullOrWhiteSpace(p)));
            completedNaturally = !string.IsNullOrWhiteSpace(finalResponse);
            LogAgenticCompletion(round, completedNaturally ? "completed" : "no_output", toolCallLog.Count, totalInputTokens + totalOutputTokens);
            break;
        }

        return BuildAgenticResult(finalResponse, round, completedNaturally, toolCallLog, totalInputTokens, totalOutputTokens, totalCost);
    }

    // ──────── Claude Agentic Loop (Anthropic Messages API) ────────

    private async Task<AgenticResult> ClaudeAgenticLoopAsync(
        string systemPrompt, string userPrompt,
        IReadOnlyList<ToolDefinition> tools,
        Func<ToolCall, CancellationToken, Task<string>> toolExecutor,
        AgenticOptions options, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("AIClient");
        var url = BuildFullUrl();

        var claudeTools = tools.Select(t => new
        {
            name = t.Name,
            description = t.Description,
            input_schema = t.InputSchema
        }).ToArray();

        var messages = new List<object>
        {
            new { role = "user", content = userPrompt }
        };

        int totalInputTokens = 0, totalOutputTokens = 0;
        decimal totalCost = 0m;
        var toolCallLog = new List<ToolCallLog>();
        string? finalResponse = null;
        bool completedNaturally = false;
        int round = 0;

        for (round = 1; round <= options.MaxRounds; round++)
        {
            _logger.LogInformation("Claude agentic round {Round}/{MaxRounds}", round, options.MaxRounds);

            var requestBody = new
            {
                model = _options.Model,
                system = systemPrompt,
                messages,
                tools = claudeTools,
                max_tokens = options.MaxTokens,
                temperature = options.Temperature
            };

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            ConfigureAuth(request);
            request.Content = new StringContent(
                JsonSerializer.Serialize(requestBody, _jsonOptions),
                Encoding.UTF8,
                "application/json");

            var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Agentic round {Round} failed ({StatusCode}): {Body}",
                    round, response.StatusCode, errorBody);
                response.EnsureSuccessStatusCode();
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            // Track tokens
            if (root.TryGetProperty("usage", out var usage))
            {
                var inTok = usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
                var outTok = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
                totalInputTokens += inTok;
                totalOutputTokens += outTok;
                totalCost += TokenCostCalculator.Calculate(_options.Model, inTok, outTok);
            }

            var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
            var contentBlocks = root.TryGetProperty("content", out var cb) ? cb : default;

            var textParts = new List<string>();
            var toolUseCalls = new List<(string id, string name, JsonElement input)>();

            if (contentBlocks.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in contentBlocks.EnumerateArray())
                {
                    var type = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                    if (type == "text")
                    {
                        var text = block.TryGetProperty("text", out var tt) ? tt.GetString() : null;
                        if (text != null) textParts.Add(text);
                    }
                    else if (type == "tool_use")
                    {
                        var id = block.TryGetProperty("id", out var tid) ? tid.GetString()! : "";
                        var name = block.TryGetProperty("name", out var tn) ? tn.GetString()! : "";
                        var input = block.TryGetProperty("input", out var ti) ? ti : default;
                        toolUseCalls.Add((id, name, input));
                    }
                }
            }

            _logger.LogInformation("Claude round {Round}: stop_reason={StopReason}, text_blocks={TextCount}, tool_use_blocks={ToolCount}",
                round, stopReason, textParts.Count, toolUseCalls.Count);

            if (stopReason == "tool_use" && toolUseCalls.Count > 0)
            {
                // Build assistant content blocks for the conversation
                var assistantContentBlocks = new List<object>();
                if (contentBlocks.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in contentBlocks.EnumerateArray())
                    {
                        var type = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                        if (type == "text")
                        {
                            assistantContentBlocks.Add(new
                            {
                                type = "text",
                                text = block.GetProperty("text").GetString()
                            });
                        }
                        else if (type == "tool_use")
                        {
                            assistantContentBlocks.Add(new
                            {
                                type = "tool_use",
                                id = block.GetProperty("id").GetString(),
                                name = block.GetProperty("name").GetString(),
                                input = JsonSerializer.Deserialize<object>(block.GetProperty("input").GetRawText(), _jsonOptions)
                            });
                        }
                    }
                }

                messages.Add(new { role = "assistant", content = assistantContentBlocks });

                // Execute tools and build result blocks
                var toolResults = await ExecuteToolCallsAsync(toolUseCalls, toolExecutor, toolCallLog, round, cancellationToken);

                messages.Add(new { role = "user", content = toolResults.Select(r => new
                {
                    type = "tool_result",
                    tool_use_id = r.id,
                    content = r.result
                }).ToArray() });
            }
            else
            {
                finalResponse = string.Join("\n", textParts);
                completedNaturally = stopReason == "end_turn";
                LogAgenticCompletion(round, stopReason, toolCallLog.Count, totalInputTokens + totalOutputTokens);
                break;
            }
        }

        return BuildAgenticResult(finalResponse, round, completedNaturally, toolCallLog, totalInputTokens, totalOutputTokens, totalCost);
    }

    // ──────── OpenAI / Gemini Agentic Loop (Chat Completions API) ────────

    private async Task<AgenticResult> OpenAIAgenticLoopAsync(
        string systemPrompt, string userPrompt,
        IReadOnlyList<ToolDefinition> tools,
        Func<ToolCall, CancellationToken, Task<string>> toolExecutor,
        AgenticOptions options, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("AIClient");
        var url = BuildFullUrl();

        // OpenAI tool format: { type: "function", function: { name, description, parameters } }
        var openAiTools = tools.Select(t => new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = t.InputSchema
            }
        }).ToArray();

        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userPrompt }
        };

        int totalInputTokens = 0, totalOutputTokens = 0;
        decimal totalCost = 0m;
        var toolCallLog = new List<ToolCallLog>();
        string? finalResponse = null;
        bool completedNaturally = false;
        int round = 0;

        for (round = 1; round <= options.MaxRounds; round++)
        {
            _logger.LogInformation("OpenAI agentic round {Round}/{MaxRounds}", round, options.MaxRounds);

            var requestBody = new
            {
                model = _options.Model,
                messages,
                tools = openAiTools,
                max_tokens = options.MaxTokens,
                temperature = options.Temperature
            };

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            ConfigureAuth(request);
            request.Content = new StringContent(
                JsonSerializer.Serialize(requestBody, _jsonOptions),
                Encoding.UTF8,
                "application/json");

            var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Agentic round {Round} failed ({StatusCode}): {Body}",
                    round, response.StatusCode, errorBody);
                response.EnsureSuccessStatusCode();
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            // Track tokens — OpenAI uses prompt_tokens / completion_tokens
            if (root.TryGetProperty("usage", out var usage))
            {
                var inTok = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
                var outTok = usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;
                totalInputTokens += inTok;
                totalOutputTokens += outTok;
                totalCost += TokenCostCalculator.Calculate(_options.Model, inTok, outTok);
            }

            // Parse the first choice
            var choice = root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                ? choices[0]
                : default;
            var finishReason = choice.ValueKind != JsonValueKind.Undefined && choice.TryGetProperty("finish_reason", out var fr)
                ? fr.GetString() : null;
            var message = choice.ValueKind != JsonValueKind.Undefined && choice.TryGetProperty("message", out var msg)
                ? msg : default;

            // Check for tool calls
            var hasToolCalls = message.ValueKind != JsonValueKind.Undefined
                && message.TryGetProperty("tool_calls", out var toolCallsEl)
                && toolCallsEl.ValueKind == JsonValueKind.Array
                && toolCallsEl.GetArrayLength() > 0;

            if (hasToolCalls)
            {
                var toolCallsEl2 = message.GetProperty("tool_calls");

                // Add the full assistant message (with tool_calls) to conversation
                var assistantMsg = JsonSerializer.Deserialize<object>(message.GetRawText(), _jsonOptions);
                messages.Add(assistantMsg!);

                // Execute each tool call
                var openAiToolCalls = new List<(string id, string name, JsonElement input)>();
                foreach (var tc in toolCallsEl2.EnumerateArray())
                {
                    var tcId = tc.TryGetProperty("id", out var tci) ? tci.GetString()! : "";
                    var fn = tc.TryGetProperty("function", out var fnEl) ? fnEl : default;
                    var tcName = fn.ValueKind != JsonValueKind.Undefined && fn.TryGetProperty("name", out var n)
                        ? n.GetString()! : "";
                    var tcArgs = fn.ValueKind != JsonValueKind.Undefined && fn.TryGetProperty("arguments", out var a)
                        ? a : default;
                    // OpenAI sends arguments as a string, parse to JsonElement
                    var argsStr = tcArgs.ValueKind == JsonValueKind.String ? tcArgs.GetString()! : "{}";
                    using var argsDoc = JsonDocument.Parse(argsStr);
                    openAiToolCalls.Add((tcId, tcName, argsDoc.RootElement.Clone()));
                }

                var toolResults = await ExecuteToolCallsAsync(openAiToolCalls, toolExecutor, toolCallLog, round, cancellationToken);

                // Add tool results as "tool" role messages (OpenAI format)
                foreach (var (id, result) in toolResults)
                {
                    messages.Add(new { role = "tool", tool_call_id = id, content = result });
                }
            }
            else
            {
                // Done — extract text content
                var textContent = message.ValueKind != JsonValueKind.Undefined && message.TryGetProperty("content", out var c)
                    ? c.GetString() : null;
                finalResponse = textContent;
                completedNaturally = finishReason == "stop";
                LogAgenticCompletion(round, finishReason, toolCallLog.Count, totalInputTokens + totalOutputTokens);
                break;
            }
        }

        return BuildAgenticResult(finalResponse, round, completedNaturally, toolCallLog, totalInputTokens, totalOutputTokens, totalCost);
    }

    // ──────── Shared Helpers ────────

    private async Task<List<(string id, string result)>> ExecuteToolCallsAsync(
        List<(string id, string name, JsonElement input)> toolCalls,
        Func<ToolCall, CancellationToken, Task<string>> toolExecutor,
        List<ToolCallLog> toolCallLog, int round,
        CancellationToken cancellationToken)
    {
        var results = new List<(string id, string result)>();

        foreach (var (id, name, input) in toolCalls)
        {
            var inputJson = input.ValueKind != JsonValueKind.Undefined ? input.GetRawText() : "{}";

            toolCallLog.Add(new ToolCallLog
            {
                ToolName = name,
                Input = inputJson.Length > 200 ? inputJson[..200] + "..." : inputJson,
                Round = round
            });

            _logger.LogInformation("Round {Round}: calling tool {ToolName}", round, name);

            string toolResult;
            try
            {
                toolResult = await toolExecutor(
                    new ToolCall { Id = id, Name = name, InputJson = inputJson },
                    cancellationToken);
            }
            catch (Exception ex)
            {
                toolResult = $"Error executing tool: {ex.Message}";
                _logger.LogWarning(ex, "Tool {ToolName} failed in round {Round}", name, round);
            }

            // Truncate very large results
            if (toolResult.Length > 15000)
                toolResult = toolResult[..15000] + "\n\n[Output truncated — file too large]";

            results.Add((id, toolResult));
        }

        return results;
    }

    private void LogAgenticCompletion(int round, string? stopReason, int toolCallCount, int totalTokens)
    {
        _logger.LogInformation(
            "Agentic loop finished after {Rounds} rounds (stop_reason={StopReason}), " +
            "{ToolCalls} tool calls, {TotalTokens} total tokens",
            round, stopReason, toolCallCount, totalTokens);
    }

    private AgenticResult BuildAgenticResult(string? finalResponse, int rounds, bool completedNaturally,
        List<ToolCallLog> toolCallLog, int totalInputTokens, int totalOutputTokens, decimal totalCost)
    {
        return new AgenticResult
        {
            FinalResponse = finalResponse,
            RoundsExecuted = rounds,
            CompletedNaturally = completedNaturally,
            ToolCalls = toolCallLog,
            TotalUsage = new TokenUsageData
            {
                InputTokens = totalInputTokens,
                OutputTokens = totalOutputTokens,
                TotalTokens = totalInputTokens + totalOutputTokens,
                EstimatedCost = totalCost,
                Model = _options.Model
            }
        };
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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

    private object BuildOpenAIResponsesRequestBody(string systemPrompt, string userPrompt, AICompletionOptions? options)
    {
        return new
        {
            model = _options.Model,
            input = new object[]
            {
                new { role = "system", content = new object[] { new { type = "input_text", text = systemPrompt } } },
                new { role = "user", content = new object[] { new { type = "input_text", text = userPrompt } } }
            },
            max_output_tokens = NormalizeResponsesMaxOutputTokens(options?.MaxTokens)
        };
    }

    private int NormalizeResponsesMaxOutputTokens(int? requestedMaxTokens)
    {
        var maxTokens = requestedMaxTokens ?? _options.MaxTokens;
        return Math.Max(16, maxTokens);
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

    private (string? content, TokenUsageData? usage) ParseOpenAIResponsesResponse(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var textParts = new List<string>();

        if (root.TryGetProperty("output_text", out var outputTextEl))
        {
            if (outputTextEl.ValueKind == JsonValueKind.String)
            {
                var outputText = outputTextEl.GetString();
                if (!string.IsNullOrWhiteSpace(outputText))
                {
                    textParts.Add(outputText);
                }
            }
            else if (outputTextEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in outputTextEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var text = item.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            textParts.Add(text);
                        }
                    }
                }
            }
        }

        if (root.TryGetProperty("output", out var outputEl) && outputEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in outputEl.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                if (string.Equals(type, "output_text", StringComparison.OrdinalIgnoreCase)
                    && item.TryGetProperty("text", out var directTextEl)
                    && directTextEl.ValueKind == JsonValueKind.String)
                {
                    var directText = directTextEl.GetString();
                    if (!string.IsNullOrWhiteSpace(directText))
                    {
                        textParts.Add(directText);
                    }
                }

                if (!string.Equals(type, "message", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (item.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in contentEl.EnumerateArray())
                    {
                        var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                        if (string.Equals(blockType, "output_text", StringComparison.OrdinalIgnoreCase)
                            && block.TryGetProperty("text", out var textEl)
                            && textEl.ValueKind == JsonValueKind.String)
                        {
                            textParts.Add(textEl.GetString() ?? string.Empty);
                        }
                    }
                }
            }
        }

        TokenUsageData? usage = null;
        if (root.TryGetProperty("usage", out var usageEl))
        {
            var inputTokens = usageEl.TryGetProperty("input_tokens", out var inTok) ? inTok.GetInt32() : 0;
            var outputTokens = usageEl.TryGetProperty("output_tokens", out var outTok) ? outTok.GetInt32() : 0;
            var totalTokens = inputTokens + outputTokens;
            usage = new TokenUsageData
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = totalTokens,
                EstimatedCost = TokenCostCalculator.Calculate(_options.Model, inputTokens, outputTokens),
                Model = _options.Model
            };
        }

        return (string.Join("\n", textParts.Where(t => !string.IsNullOrWhiteSpace(t))), usage);
    }

    private static (List<string> textParts, List<(string callId, string name, JsonElement argumentsJson)> functionCalls)
        ParseOpenAIResponsesOutput(JsonElement root)
    {
        var textParts = new List<string>();
        var functionCalls = new List<(string callId, string name, JsonElement argumentsJson)>();

        if (!root.TryGetProperty("output", out var outputEl) || outputEl.ValueKind != JsonValueKind.Array)
        {
            return (textParts, functionCalls);
        }

        foreach (var item in outputEl.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

            if ((string.Equals(type, "output_text", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                && item.TryGetProperty("text", out var directTextEl)
                && directTextEl.ValueKind == JsonValueKind.String)
            {
                var directText = directTextEl.GetString();
                if (!string.IsNullOrWhiteSpace(directText))
                {
                    textParts.Add(directText);
                }

                continue;
            }

            if (string.Equals(type, "message", StringComparison.OrdinalIgnoreCase))
            {
                if (item.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in contentEl.EnumerateArray())
                    {
                        var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                        if (string.Equals(blockType, "output_text", StringComparison.OrdinalIgnoreCase)
                            && block.TryGetProperty("text", out var textEl)
                            && textEl.ValueKind == JsonValueKind.String)
                        {
                            textParts.Add(textEl.GetString() ?? string.Empty);
                        }
                    }
                }

                continue;
            }

            if (string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase))
            {
                var callId = item.TryGetProperty("call_id", out var callIdEl) ? callIdEl.GetString() : null;
                var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                var argsString = item.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String
                    ? argsEl.GetString()
                    : "{}";

                if (string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                using var argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsString) ? "{}" : argsString);
                functionCalls.Add((callId!, name!, argsDoc.RootElement.Clone()));
            }
        }

        return (textParts, functionCalls);
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
            "OPENAI" when IsOpenAIResponsesModel => "/v1/responses",
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
