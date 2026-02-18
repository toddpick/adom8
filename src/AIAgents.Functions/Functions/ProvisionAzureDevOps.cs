using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIAgents.Core.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIAgents.Functions.Functions;

/// <summary>
/// Provision required Azure DevOps artifacts for ADOm8.
/// POST /api/provision-ado
///
/// This endpoint is idempotent and safe to run multiple times.
/// It currently automates:
/// - Service Hook subscription to OrchestratorWebhook (work item state changes)
/// - Creation of required Custom.AI* fields (best effort)
/// - Validation of required pipeline states on User Story
/// </summary>
public sealed class ProvisionAzureDevOps
{
    private static readonly string[] RequiredStates =
    [
        "Story Planning",
        "AI Code",
        "AI Test",
        "AI Review",
        "AI Docs",
        "AI Deployment",
        "Code Review",
        "Needs Revision",
        "Agent Failed",
        "Ready for QA",
        "Ready for Deployment",
        "Deployed"
    ];

    private static readonly FieldDefinition[] RequiredFields =
    [
        new("Autonomy Level", "Custom.AutonomyLevel", "string"),
        new("AI Minimum Review Score", "Custom.AIMinimumReviewScore", "integer"),

        new("AI Model Tier", "Custom.AIModelTier", "string"),
        new("AI Planning Model", "Custom.AIPlanningModel", "string"),
        new("AI Coding Model", "Custom.AICodingModel", "string"),
        new("AI Testing Model", "Custom.AITestingModel", "string"),
        new("AI Review Model", "Custom.AIReviewModel", "string"),
        new("AI Documentation Model", "Custom.AIDocumentationModel", "string"),
        new("AI Coding Provider", "Custom.AICodingProvider", "string"),

        new("AI Tokens Used", "Custom.AITokensUsed", "integer"),
        new("AI Cost", "Custom.AICost", "string"),
        new("AI Complexity", "Custom.AIComplexity", "string"),
        new("AI Model", "Custom.AIModel", "string"),
        new("AI Review Score", "Custom.AIReviewScore", "integer"),
        new("AI Processing Time", "Custom.AIProcessingTime", "integer"),
        new("AI Files Generated", "Custom.AIFilesGenerated", "integer"),
        new("AI Tests Generated", "Custom.AITestsGenerated", "integer"),
        new("AI PR Number", "Custom.AIPRNumber", "integer"),
        new("AI Last Agent", "Custom.AILastAgent", "string"),
        new("AI Critical Issues", "Custom.AICriticalIssues", "integer"),
        new("AI Deployment Decision", "Custom.AIDeploymentDecision", "string")
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AzureDevOpsOptions _options;
    private readonly ILogger<ProvisionAzureDevOps> _logger;

    public ProvisionAzureDevOps(
        IHttpClientFactory httpClientFactory,
        IOptions<AzureDevOpsOptions> options,
        ILogger<ProvisionAzureDevOps> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    [Function("ProvisionAzureDevOps")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "provision-ado")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.OrganizationUrl) ||
            string.IsNullOrWhiteSpace(_options.Project) ||
            string.IsNullOrWhiteSpace(_options.Pat))
        {
            return new BadRequestObjectResult(new
            {
                success = false,
                summary = "Azure DevOps configuration is incomplete.",
                errors = new[]
                {
                    "Set AzureDevOps:OrganizationUrl, AzureDevOps:Project, and AzureDevOps:Pat in Function App settings."
                }
            });
        }

        var steps = new List<string>();
        var warnings = new List<string>();
        var errors = new List<string>();
        var createdFields = new List<string>();
        var existingFields = new List<string>();
        var missingStates = new List<string>();

        using var adoClient = CreateAdoClient();

        ProjectInfo? projectInfo;
        try
        {
            projectInfo = await GetProjectInfoAsync(adoClient, cancellationToken);
            steps.Add($"Connected to project '{projectInfo.Name}' ({projectInfo.Id}).");
            if (!string.IsNullOrWhiteSpace(projectInfo.ProcessTemplateName))
            {
                steps.Add($"Project process: {projectInfo.ProcessTemplateName}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Azure DevOps project details");
            return new ObjectResult(new
            {
                success = false,
                summary = "Failed to connect to Azure DevOps project.",
                errors = new[] { ex.Message }
            })
            { StatusCode = StatusCodes.Status500InternalServerError };
        }

        try
        {
            var webhookUrl = $"{req.Scheme}://{req.Host}/api/OrchestratorWebhook";
            var serviceHookCreated = await EnsureServiceHookAsync(
                adoClient,
                projectInfo,
                webhookUrl,
                cancellationToken);

            steps.Add(serviceHookCreated
                ? "Created Azure DevOps Service Hook for state-change events."
                : "Service Hook already configured.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not provision service hook");
            warnings.Add($"Service Hook setup failed: {ex.Message}");
        }

        foreach (var field in RequiredFields)
        {
            try
            {
                var exists = await FieldExistsAsync(adoClient, field.ReferenceName, cancellationToken);
                if (exists)
                {
                    existingFields.Add(field.ReferenceName);
                    continue;
                }

                var created = await TryCreateFieldAsync(adoClient, field, cancellationToken);
                if (created)
                {
                    createdFields.Add(field.ReferenceName);
                }
                else
                {
                    warnings.Add($"Could not create field {field.ReferenceName}. Check process permissions.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed ensuring field {Field}", field.ReferenceName);
                warnings.Add($"Field {field.ReferenceName}: {ex.Message}");
            }
        }

        try
        {
            var existingStateNames = await GetUserStoryStatesAsync(adoClient, projectInfo.Name, cancellationToken);
            missingStates.AddRange(RequiredStates.Where(s => !existingStateNames.Contains(s, StringComparer.OrdinalIgnoreCase)));

            if (missingStates.Count == 0)
            {
                steps.Add("All required User Story states are present.");
            }
            else
            {
                warnings.Add($"{missingStates.Count} required states are missing on User Story.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not validate User Story states");
            warnings.Add($"Could not verify User Story states: {ex.Message}");
        }

        if (createdFields.Count > 0)
        {
            steps.Add($"Created {createdFields.Count} missing custom fields.");
        }
        else
        {
            steps.Add("No custom field creation needed.");
        }

        var manualSteps = new List<string>();
        if (missingStates.Count > 0)
        {
            manualSteps.Add("Add the missing User Story states in Organization Settings → Boards → Process → User Story → States.");
        }

        manualSteps.Add("If custom fields were created but not visible on forms, add them to User Story layout groups (AI Agent Settings / AI Model Settings / AI Tracking).");

        var ready = errors.Count == 0 && missingStates.Count == 0;
        var summary = ready
            ? "Azure DevOps provisioning complete."
            : "Azure DevOps provisioning completed with manual follow-ups.";

        return new OkObjectResult(new
        {
            success = errors.Count == 0,
            ready,
            summary,
            project = new
            {
                projectInfo.Name,
                projectInfo.Id,
                projectInfo.ProcessTemplateName
            },
            fields = new
            {
                created = createdFields,
                existing = existingFields,
                required = RequiredFields.Select(f => f.ReferenceName)
            },
            states = new
            {
                required = RequiredStates,
                missing = missingStates
            },
            steps,
            warnings,
            errors,
            manualSteps
        });
    }

    private HttpClient CreateAdoClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(_options.OrganizationUrl.TrimEnd('/') + "/", UriKind.Absolute);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var patBytes = Encoding.ASCII.GetBytes($":{_options.Pat}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(patBytes));
        return client;
    }

    private async Task<ProjectInfo> GetProjectInfoAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"_apis/projects/{Uri.EscapeDataString(_options.Project)}?includeCapabilities=true&api-version=7.1-preview.4",
            cancellationToken);

        var json = await ReadJsonAsync(response, cancellationToken);
        var id = json?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Azure DevOps project id not found.");
        var name = json?["name"]?.GetValue<string>() ?? _options.Project;
        var processTemplateName = json?["capabilities"]?["processTemplate"]?["templateName"]?.GetValue<string>();

        return new ProjectInfo(id, name, processTemplateName);
    }

    private async Task<bool> EnsureServiceHookAsync(
        HttpClient client,
        ProjectInfo project,
        string webhookUrl,
        CancellationToken cancellationToken)
    {
        using var listResponse = await client.GetAsync(
            $"{Uri.EscapeDataString(project.Name)}/_apis/hooks/subscriptions?api-version=7.1-preview.1",
            cancellationToken);

        var listJson = await ReadJsonAsync(listResponse, cancellationToken);
        var subscriptions = listJson?["value"]?.AsArray() ?? new JsonArray();

        var existing = subscriptions.Any(subscription =>
        {
            var consumerUrl = subscription?["consumerInputs"]?["url"]?.GetValue<string>();
            var eventType = subscription?["eventType"]?.GetValue<string>();
            var projectId = subscription?["publisherInputs"]?["projectId"]?.GetValue<string>();

            return string.Equals(consumerUrl, webhookUrl, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(eventType, "workitem.updated", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(projectId, project.Id, StringComparison.OrdinalIgnoreCase);
        });

        if (existing)
        {
            return false;
        }

        var payload = new JsonObject
        {
            ["publisherId"] = "tfs",
            ["eventType"] = "workitem.updated",
            ["resourceVersion"] = "1.0",
            ["consumerId"] = "webHooks",
            ["consumerActionId"] = "httpRequest",
            ["publisherInputs"] = new JsonObject
            {
                ["projectId"] = project.Id,
                ["workItemType"] = "User Story",
                ["changedFields"] = "System.State"
            },
            ["consumerInputs"] = new JsonObject
            {
                ["url"] = webhookUrl,
                ["httpHeaders"] = "X-ADOm8-Source: service-hook"
            }
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{Uri.EscapeDataString(project.Name)}/_apis/hooks/subscriptions?api-version=7.1-preview.1")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        using var createResponse = await client.SendAsync(request, cancellationToken);
        _ = await ReadJsonAsync(createResponse, cancellationToken);
        return true;
    }

    private async Task<HashSet<string>> GetUserStoryStatesAsync(HttpClient client, string projectName, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"{Uri.EscapeDataString(projectName)}/_apis/wit/workitemtypes/User%20Story/states?api-version=7.1",
            cancellationToken);

        var json = await ReadJsonAsync(response, cancellationToken);
        var states = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = json?["value"]?.AsArray() ?? new JsonArray();

        foreach (var state in values)
        {
            var name = state?["name"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(name))
            {
                states.Add(name);
            }
        }

        return states;
    }

    private async Task<bool> FieldExistsAsync(HttpClient client, string referenceName, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"_apis/wit/fields/{Uri.EscapeDataString(referenceName)}?api-version=7.1",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"Field lookup failed for {referenceName}: {response.StatusCode} {errorContent}");
    }

    private async Task<bool> TryCreateFieldAsync(HttpClient client, FieldDefinition field, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["name"] = field.Name,
            ["referenceName"] = field.ReferenceName,
            ["type"] = field.Type,
            ["usage"] = "workItem"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "_apis/wit/fields?api-version=7.1")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return true;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogWarning("Create field failed for {Field}: {StatusCode} {Body}", field.ReferenceName, response.StatusCode, body);
        return false;
    }

    private static async Task<JsonObject?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Azure DevOps API failed ({(int)response.StatusCode}): {raw}");
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return JsonNode.Parse(raw)?.AsObject();
    }

    private sealed record FieldDefinition(string Name, string ReferenceName, string Type);

    private sealed record ProjectInfo(string Id, string Name, string? ProcessTemplateName);
}