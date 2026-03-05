using System.Collections.Concurrent;
using System.Diagnostics;
using AIAgents.Core.Configuration;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Telemetry;
using AIAgents.Functions.Models;
using Azure.Storage.Queues;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgents.Functions.Functions;

/// <summary>
/// HTTP endpoint that reports system health by checking all critical components.
/// Results are cached for 60 seconds to avoid excessive overhead.
/// Returns 200 for healthy/degraded, 503 for unhealthy.
/// </summary>
public sealed class HealthCheck
{
    private readonly IAzureDevOpsClient _adoClient;
    private readonly IAIClient _aiClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<HealthCheck> _logger;
    private readonly TelemetryClient _telemetry;
    private readonly AIOptions _aiOptions;
    private readonly CopilotOptions _copilotOptions;

    // Cache health results for 60 seconds
    private static readonly ConcurrentDictionary<string, (HealthCheckResult Result, DateTime CachedAt)> s_cache = new();
    private static readonly TimeSpan s_cacheDuration = TimeSpan.FromSeconds(60);

    public HealthCheck(
        IAzureDevOpsClient adoClient,
        IAIClient aiClient,
        IConfiguration configuration,
        ILogger<HealthCheck> logger,
        TelemetryClient telemetry,
        IOptions<AIOptions> aiOptions,
        IOptions<CopilotOptions> copilotOptions)
    {
        _adoClient = adoClient;
        _aiClient = aiClient;
        _configuration = configuration;
        _logger = logger;
        _telemetry = telemetry;
        _aiOptions = aiOptions.Value;
        _copilotOptions = copilotOptions.Value;
    }

    [Function("HealthCheck")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        // Return cached result if fresh
        if (s_cache.TryGetValue("health", out var cached) &&
            DateTime.UtcNow - cached.CachedAt < s_cacheDuration)
        {
            return ToActionResult(cached.Result);
        }

        var checks = new Dictionary<string, ComponentCheck>();

        // Run checks in parallel where possible
        var adoTask = CheckAzureDevOpsAsync(cancellationToken);
        var queueTask = CheckStorageQueueAsync(cancellationToken);
        var configTask = Task.FromResult(CheckConfiguration());

        await Task.WhenAll(adoTask, queueTask, configTask);

        checks["azureDevOps"] = await adoTask;
        checks["storageQueue"] = await queueTask;
        checks["configuration"] = configTask.Result;

        // AI check — separate because it can be slow
        checks["aiApi"] = await CheckAiApiAsync(cancellationToken);

        // Git check — config-only (no actual clone)
        checks["git"] = CheckGitConfiguration();

        // Determine overall status
        var hasCriticalFailure = checks.Values.Any(c =>
            c.Status == "unhealthy" &&
            checks.First(kvp => kvp.Value == c).Key is "azureDevOps" or "storageQueue" or "configuration");

        var hasAnyUnhealthy = checks.Values.Any(c => c.Status is "unhealthy" or "degraded");

        var overallStatus = hasCriticalFailure ? "unhealthy"
            : hasAnyUnhealthy ? "degraded"
            : "healthy";

        var result = new HealthCheckResult
        {
            Status = overallStatus,
            Checks = checks,
            Environment = _configuration["ASPNETCORE_ENVIRONMENT"] ?? _configuration["environment"] ?? "unknown",
            Providers = BuildProviderInfo(checks)
        };

        // Cache the result
        s_cache.AddOrUpdate("health", (result, DateTime.UtcNow), (_, _) => (result, DateTime.UtcNow));

        _telemetry.TrackEvent(TelemetryEvents.HealthCheckCompleted, new Dictionary<string, string>
        {
            ["status"] = overallStatus
        });

        _logger.LogInformation("Health check completed: {Status}", overallStatus);

        return ToActionResult(result);
    }

    /// <summary>
    /// Clears the health check cache. Used by tests.
    /// </summary>
    internal static void ClearCache() => s_cache.Clear();

    private async Task<ComponentCheck> CheckAzureDevOpsAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Attempt to get a work item with ID 1 — will fail but proves connectivity
            // Actually, just calling GetWorkItemAsync proves the connection works.
            // Use a very small timeout via CancellationTokenSource.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            await _adoClient.GetWorkItemAsync(1, cts.Token);
            sw.Stop();

            return new ComponentCheck { Status = "healthy", ResponseTime = sw.ElapsedMilliseconds };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new ComponentCheck { Status = "unhealthy", ResponseTime = sw.ElapsedMilliseconds, Message = "Request timed out (5s)" };
        }
        catch (Exception ex)
        {
            sw.Stop();
            // A 404 (work item not found) still means ADO is reachable
            if (ex.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("404", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("TF401232", StringComparison.OrdinalIgnoreCase))
            {
                return new ComponentCheck { Status = "healthy", ResponseTime = sw.ElapsedMilliseconds, Message = "Connected (test WI not found, which is expected)" };
            }

            return new ComponentCheck { Status = "unhealthy", ResponseTime = sw.ElapsedMilliseconds, Message = $"Connection failed: {ex.Message}" };
        }
    }

    private async Task<ComponentCheck> CheckStorageQueueAsync(CancellationToken ct)
    {
        try
        {
            var connectionString = _configuration["AzureWebJobsStorage"];
            if (string.IsNullOrEmpty(connectionString))
            {
                return new ComponentCheck { Status = "unhealthy", Message = "AzureWebJobsStorage not configured" };
            }

            var taskQueue = new QueueClient(connectionString, "agent-tasks");
            var poisonQueue = new QueueClient(connectionString, "agent-tasks-poison");

            var taskProps = await taskQueue.GetPropertiesAsync(ct);
            var poisonProps = await poisonQueue.GetPropertiesAsync(ct);

            var messageCount = taskProps.Value.ApproximateMessagesCount;
            var poisonCount = poisonProps.Value.ApproximateMessagesCount;

            var status = poisonCount > 0 ? "degraded" : "healthy";
            var message = poisonCount > 0 ? $"{poisonCount} messages in poison queue" : null;

            return new ComponentCheck
            {
                Status = status,
                MessageCount = messageCount,
                PoisonMessageCount = poisonCount,
                Message = message
            };
        }
        catch (Exception ex)
        {
            return new ComponentCheck { Status = "unhealthy", Message = $"Queue access failed: {ex.Message}" };
        }
    }

    private async Task<ComponentCheck> CheckAiApiAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var apiKey = ResolvePrimaryProviderApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                return new ComponentCheck { Status = "degraded", Message = "AI API key not configured" };
            }

            // Send a minimal test request with low max_tokens to minimize cost while
            // still allowing reasoning-capable models (e.g., GPT-5 family) to return text.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            await _aiClient.CompleteAsync("Reply only with OK", "test", new AICompletionOptions { MaxTokens = 64 }, cts.Token);
            sw.Stop();

            var status = sw.ElapsedMilliseconds > 8000 ? "degraded" : "healthy";
            var message = sw.ElapsedMilliseconds > 8000 ? $"Slow response ({sw.ElapsedMilliseconds / 1000}s)" : null;

            return new ComponentCheck { Status = status, ResponseTime = sw.ElapsedMilliseconds, Message = message };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new ComponentCheck { Status = "degraded", ResponseTime = sw.ElapsedMilliseconds, Message = "Request timed out (10s)" };
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("empty content", StringComparison.OrdinalIgnoreCase))
        {
            sw.Stop();
            var status = sw.ElapsedMilliseconds > 8000 ? "degraded" : "healthy";
            var message = "AI API reachable (empty probe response)";
            return new ComponentCheck { Status = status, ResponseTime = sw.ElapsedMilliseconds, Message = message };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ComponentCheck { Status = "degraded", ResponseTime = sw.ElapsedMilliseconds, Message = $"API check failed: {ex.Message}" };
        }
    }

    private ComponentCheck CheckConfiguration()
    {
        var missingVars = new List<string>();

        if (!HasPrimaryProviderConfiguration())
        {
            missingVars.Add("AI__ApiKey");
        }

        var requiredVars = new Dictionary<string, string[]>
        {
            ["AzureDevOps__Pat"] = ["AzureDevOps:Pat", "AzureDevOps__Pat"],
            ["Git__Token"] = ["Git:Token", "Git__Token"]
        };

        foreach (var (name, keys) in requiredVars)
        {
            var found = keys.Any(k => !string.IsNullOrEmpty(_configuration[k]));
            if (!found)
            {
                missingVars.Add(name);
            }
        }

        if (missingVars.Count > 0)
        {
            return new ComponentCheck
            {
                Status = "unhealthy",
                Message = $"Missing required configuration: {string.Join(", ", missingVars)}",
                MissingVars = missingVars
            };
        }

        return new ComponentCheck { Status = "healthy" };
    }

    private ComponentCheck CheckGitConfiguration()
    {
        var gitToken = _configuration["Git:Token"] ?? _configuration["Git__Token"];
        var gitUrl = _configuration["Git:RepositoryUrl"] ?? _configuration["Git__RepositoryUrl"];

        if (string.IsNullOrEmpty(gitToken))
        {
            return new ComponentCheck { Status = "unhealthy", Message = "Git token not configured" };
        }

        if (string.IsNullOrEmpty(gitUrl))
        {
            return new ComponentCheck { Status = "unhealthy", Message = "Git repository URL not configured" };
        }

        return new ComponentCheck { Status = "healthy" };
    }

    /// <summary>
    /// Builds provider configuration info from AIOptions, CopilotOptions, and ProviderKeys.
    /// This tells the dashboard which AI providers are configured and what models they use.
    /// </summary>
    private ProviderInfo BuildProviderInfo(Dictionary<string, ComponentCheck> checks)
    {
        var aiStatus = checks.TryGetValue("aiApi", out var aiCheck) ? aiCheck.Status : "unknown";
        var hasPrimaryProviderConfiguration = HasPrimaryProviderConfiguration();

        // Primary AI provider
        var primaryProvider = new ProviderDetail
        {
            Name = _aiOptions.Provider ?? "Unknown",
            Model = _aiOptions.Model,
            Configured = hasPrimaryProviderConfiguration,
            Status = aiStatus
        };

        // Copilot provider
        var copilotDetail = new CopilotProviderDetail
        {
            Enabled = _copilotOptions.Enabled,
            Mode = _copilotOptions.Mode,
            Model = _copilotOptions.Model,
            Configured = _copilotOptions.Enabled
        };

        // Additional providers from ProviderKeys
        var additionalProviders = new List<ProviderDetail>();
        var knownProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Claude", "OpenAI", "Google", "Gemini"
        };

        // Detect which of the 4 providers are configured
        var primaryName = NormalizeProviderName(_aiOptions.Provider);

        foreach (var providerName in knownProviders)
        {
            // Skip if this is the primary provider
            if (string.Equals(providerName, primaryName, StringComparison.OrdinalIgnoreCase))
                continue;

            var isConfigured = _aiOptions.ProviderKeys?.Any(kvp =>
                string.Equals(NormalizeProviderName(kvp.Key), providerName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(kvp.Value?.ApiKey)) ?? false;

            additionalProviders.Add(new ProviderDetail
            {
                Name = providerName,
                Model = null,
                Configured = isConfigured,
                Status = isConfigured ? "configured" : "not_configured"
            });
        }

        return new ProviderInfo
        {
            Ai = primaryProvider,
            Copilot = copilotDetail,
            AdditionalProviders = additionalProviders.Count > 0 ? additionalProviders : null
        };
    }

    /// <summary>
    /// Normalizes provider names to consistent display names.
    /// </summary>
    private static string NormalizeProviderName(string? provider)
    {
        if (string.IsNullOrEmpty(provider)) return "Unknown";

        return provider.ToLowerInvariant() switch
        {
            "claude" or "anthropic" => "Claude",
            "openai" or "azureopenai" => "OpenAI",
            "google" or "gemini" => "Gemini",
            _ => provider
        };
    }

    private bool HasPrimaryProviderConfiguration()
    {
        return !string.IsNullOrWhiteSpace(ResolvePrimaryProviderApiKey());
    }

    private string? ResolvePrimaryProviderApiKey()
    {
        var configuredApiKey = _configuration["AI:ApiKey"] ?? _configuration["AI__ApiKey"];
        if (!string.IsNullOrWhiteSpace(configuredApiKey))
        {
            return configuredApiKey;
        }

        var configuredProvider = _configuration["AI:Provider"] ?? _configuration["AI__Provider"] ?? _aiOptions.Provider;
        var primaryProvider = NormalizeProviderName(configuredProvider);

        var providerAliases = primaryProvider switch
        {
            "Claude" => new[] { "Claude", "Anthropic" },
            "OpenAI" => new[] { "OpenAI", "AzureOpenAI" },
            "Gemini" => new[] { "Gemini", "Google" },
            _ => new[] { primaryProvider }
        };

        foreach (var alias in providerAliases)
        {
            var keyFromHierarchy = _configuration[$"AI:ProviderKeys:{alias}:ApiKey"];
            var keyFromFlat = _configuration[$"AI__ProviderKeys__{alias}__ApiKey"];
            var providerKey = keyFromHierarchy ?? keyFromFlat;
            if (!string.IsNullOrWhiteSpace(providerKey))
            {
                return providerKey;
            }
        }

        if (_aiOptions.ProviderKeys is null)
        {
            return null;
        }

        foreach (var providerEntry in _aiOptions.ProviderKeys)
        {
            if (string.Equals(NormalizeProviderName(providerEntry.Key), primaryProvider, StringComparison.OrdinalIgnoreCase))
            {
                return providerEntry.Value?.ApiKey;
            }
        }

        return null;
    }

    private static IActionResult ToActionResult(HealthCheckResult result)
    {
        var statusCode = result.Status == "unhealthy" ? 503 : 200;
        return new ObjectResult(result) { StatusCode = statusCode };
    }
}
