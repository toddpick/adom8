using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIAgents.Core.Configuration;
using AIAgents.Core.Constants;
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
/// - Validation and best-effort creation of required pipeline states on User Story
/// </summary>
public sealed class ProvisionAzureDevOps
{
    private static readonly string[] CurrentAgentPicklistValues =
    [
        AIPipelineNames.CurrentAgentValues.Planning,
        AIPipelineNames.CurrentAgentValues.Coding,
        AIPipelineNames.CurrentAgentValues.Testing,
        AIPipelineNames.CurrentAgentValues.Review,
        AIPipelineNames.CurrentAgentValues.Documentation,
        AIPipelineNames.CurrentAgentValues.Deployment
    ];

    private const string CurrentAgentPicklistName = "ADOm8 Current AI Agent";
    private const string AutonomyLevelPicklistName = "ADOm8 AI Autonomy Level";
    private const string DefaultAutonomyLevel = "3";
    private const int DefaultMinimumReviewScore = 85;

    private static readonly string[] AutonomyLevelPicklistValues =
    [
        "1",
        "2",
        "3",
        "4",
        "5"
    ];

    private static readonly string[] RequiredStates =
    [
        AIPipelineNames.ProcessingState,
        "Code Review",
        "Needs Revision",
        "Agent Failed",
        "Ready for QA",
        "Ready for Deployment",
        "Deployed"
    ];

    private static readonly Dictionary<string, (string Category, string Color)> RequiredStateMetadata =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [AIPipelineNames.ProcessingState] = ("InProgress", "0078D4"),
            ["Code Review"] = ("InProgress", "107C10"),
            ["Needs Revision"] = ("InProgress", "D13438"),
            ["Agent Failed"] = ("InProgress", "A80000"),
            ["Ready for QA"] = ("Resolved", "107C10"),
            ["Ready for Deployment"] = ("Resolved", "0B6A0B"),
            ["Deployed"] = ("Completed", "0B6A0B")
        };

    private static readonly FieldDefinition[] RequiredFields =
    [
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
        try
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
            var additionalManualSteps = new List<string>();
            var currentAgentPicklistEnforced = false;
            var createdFields = new List<string>();
            var existingFields = new List<string>();
            var createdStates = new List<string>();
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
                    var fieldStatus = await GetFieldStatusAsync(adoClient, field.ReferenceName, cancellationToken);
                    if (fieldStatus.Exists)
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
                var currentAgentFieldStatus = await GetCurrentAgentProcessFieldStatusAsync(
                    adoClient,
                    projectInfo.ProcessTemplateName,
                    cancellationToken,
                    knownProcessId: projectInfo.ProcessTemplateId);

                if (!currentAgentFieldStatus.Exists)
                {
                    if (!string.IsNullOrWhiteSpace(projectInfo.ProcessTemplateName))
                    {
                        var createdCurrentAgentPicklist = await TryCreateCurrentAgentPicklistFieldAsync(
                            adoClient,
                            projectInfo.ProcessTemplateName,
                            warnings,
                            cancellationToken,
                            knownProcessId: projectInfo.ProcessTemplateId);

                        if (createdCurrentAgentPicklist)
                        {
                            currentAgentPicklistEnforced = true;
                            createdFields.Add(CustomFieldNames.CurrentAIAgent);
                            steps.Add("Created Current AI Agent picklist field.");
                            currentAgentFieldStatus = new FieldStatus(true, true, "picklistString");
                        }
                    }

                    if (!currentAgentFieldStatus.Exists)
                    {
                        warnings.Add("Current AI Agent field is missing. Create it as Picklist (string), not a textbox.");
                        additionalManualSteps.Add("Create 'Current AI Agent' as Picklist (string) with values: Planning Agent, Coding Agent, Testing Agent, Review Agent, Documentation Agent, Deployment Agent. Leave default blank.");
                    }
                }

                if (currentAgentFieldStatus.Exists && !currentAgentFieldStatus.IsPicklist)
                {
                    if (!string.IsNullOrWhiteSpace(projectInfo.ProcessTemplateName))
                    {
                        var convertedCurrentAgentPicklist = await TryCreateCurrentAgentPicklistFieldAsync(
                            adoClient,
                            projectInfo.ProcessTemplateName,
                            warnings,
                            cancellationToken,
                            knownProcessId: projectInfo.ProcessTemplateId);

                        if (convertedCurrentAgentPicklist)
                        {
                            currentAgentPicklistEnforced = true;
                            steps.Add("Converted Current AI Agent field to picklist.");
                            currentAgentFieldStatus = await GetCurrentAgentProcessFieldStatusAsync(
                                adoClient,
                                projectInfo.ProcessTemplateName,
                                cancellationToken,
                                knownProcessId: projectInfo.ProcessTemplateId);
                        }
                    }

                    if (!currentAgentFieldStatus.IsPicklist && !currentAgentPicklistEnforced)
                    {
                        warnings.Add("Current AI Agent exists as a plain text field (textbox). Configure it as a picklist on User Story so the form renders a dropdown.");
                        additionalManualSteps.Add("In Organization Settings → Boards → Process → User Story → Fields → Current AI Agent, configure allowed values: Planning Agent, Coding Agent, Testing Agent, Review Agent, Documentation Agent, Deployment Agent (default blank). If your process does not allow converting this field to picklist, recreate it as Picklist (string) with reference name Custom.CurrentAIAgent.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to validate Current AI Agent field type");
                warnings.Add($"Could not validate Current AI Agent field type: {ex.Message}");
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(projectInfo.ProcessTemplateName))
                {
                    var defaultsConfigured = await TryEnsureAutonomyAndReviewDefaultsAsync(
                        adoClient,
                        projectInfo.ProcessTemplateName,
                        warnings,
                        cancellationToken,
                        knownProcessId: projectInfo.ProcessTemplateId);

                    if (defaultsConfigured)
                    {
                        steps.Add($"Configured '{CustomFieldNames.AutonomyLevel}' as picklist with default '{DefaultAutonomyLevel}'.");
                        steps.Add($"Configured '{CustomFieldNames.MinimumReviewScore}' default to {DefaultMinimumReviewScore}.");
                    }
                    else
                    {
                        warnings.Add("Autonomy/Review defaults could not be fully enforced. See warnings for API details.");
                        additionalManualSteps.Add("In Organization Settings → Process → your inherited process → User Story → Fields, ensure 'AI Autonomy Level' (reference name Custom.AutonomyLevel) exists as Picklist (string) with values 1-5 and default 3.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enforce defaults for AI Autonomy Level and AI Minimum Review Score");
                warnings.Add($"Could not enforce defaults for Autonomy/Review fields: {ex.Message}");
            }

            try
            {
                var existingStateNames = await GetUserStoryStatesAsync(adoClient, projectInfo.Name, cancellationToken);
                var initialMissingStates = RequiredStates
                    .Where(s => !existingStateNames.Contains(s, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (initialMissingStates.Count > 0 && !string.IsNullOrWhiteSpace(projectInfo.ProcessTemplateName))
                {
                    var autoCreated = await TryCreateMissingStatesAsync(
                        adoClient,
                        projectInfo.ProcessTemplateName,
                        initialMissingStates,
                        warnings,
                        cancellationToken,
                        knownProcessId: projectInfo.ProcessTemplateId);

                    if (autoCreated.Count > 0)
                    {
                        createdStates.AddRange(autoCreated);
                    }

                    existingStateNames = await GetUserStoryStatesAsync(adoClient, projectInfo.Name, cancellationToken);
                }

                missingStates.AddRange(RequiredStates.Where(s => !existingStateNames.Contains(s, StringComparer.OrdinalIgnoreCase)));

                if (missingStates.Count == 0)
                {
                    steps.Add("All required User Story states are present.");
                }
                else
                {
                    warnings.Add($"{missingStates.Count} required states are missing on User Story.");
                }

                if (createdStates.Count > 0)
                {
                    steps.Add($"Created {createdStates.Count} missing User Story states.");
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
            manualSteps.Add("Configure 'Current AI Agent' as a picklist with values: Planning Agent, Coding Agent, Testing Agent, Review Agent, Documentation Agent, Deployment Agent. Leave default blank.");
            manualSteps.Add("For Azure Boards visualization, add 'Current AI Agent' to card fields and create card style rules per agent value.");
            manualSteps.AddRange(additionalManualSteps);

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
                    created = createdStates,
                    missing = missingStates
                },
                steps,
                warnings,
                errors,
                manualSteps
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in ProvisionAzureDevOps");
            return new ObjectResult(new
            {
                success = false,
                summary = "An unexpected error occurred during provisioning.",
                errors = new[] { ex.Message, ex.StackTrace }
            })
            { StatusCode = StatusCodes.Status500InternalServerError };
        }
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
        // templateTypeId is the process GUID — use it directly to avoid unreliable name-based lookups
        var processTemplateId = json?["capabilities"]?["processTemplate"]?["templateTypeId"]?.GetValue<string>();

        return new ProjectInfo(id, name, processTemplateName, processTemplateId);
    }

    private async Task<bool> EnsureServiceHookAsync(
        HttpClient client,
        ProjectInfo project,
        string webhookUrl,
        CancellationToken cancellationToken)
    {
        using var listResponse = await client.GetAsync(
            "_apis/hooks/subscriptions?api-version=7.1-preview.1",
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
            "_apis/hooks/subscriptions?api-version=7.1-preview.1")
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

    private async Task<List<string>> TryCreateMissingStatesAsync(
        HttpClient client,
        string processTemplateName,
        IReadOnlyCollection<string> missingStates,
        List<string> warnings,
        CancellationToken cancellationToken,
        string? knownProcessId = null)
    {
        var created = new List<string>();

        var processId = knownProcessId ?? await TryGetProcessIdAsync(client, processTemplateName, cancellationToken);
        if (string.IsNullOrWhiteSpace(processId))
        {
            warnings.Add($"Could not resolve process '{processTemplateName}' for automatic state creation.");
            return created;
        }

        var workItemTypeReferenceName = await TryGetProcessWorkItemTypeReferenceNameAsync(
            client,
            processId,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(workItemTypeReferenceName))
        {
            warnings.Add("Could not resolve User Story work item type reference in process for automatic state creation.");
            return created;
        }

        foreach (var stateName in missingStates)
        {
            if (!RequiredStateMetadata.TryGetValue(stateName, out var meta))
            {
                warnings.Add($"No metadata configured for state '{stateName}', skipped automatic creation.");
                continue;
            }

            var payload = new JsonObject
            {
                ["name"] = stateName,
                ["color"] = meta.Color,
                ["stateCategory"] = meta.Category
            };

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"_apis/work/processes/{Uri.EscapeDataString(processId)}/workItemTypes/{Uri.EscapeDataString(workItemTypeReferenceName)}/states?api-version=7.1-preview.1")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
            {
                created.Add(stateName);
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            warnings.Add($"State '{stateName}' was not created automatically ({(int)response.StatusCode}): {body}");
        }

        return created;
    }

    private async Task<string?> TryGetProcessIdAsync(HttpClient client, string processTemplateName, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("_apis/work/processes?api-version=7.1-preview.2", cancellationToken);
        var json = await ReadJsonAsync(response, cancellationToken);
        var values = json?["value"]?.AsArray() ?? new JsonArray();

        // Try exact name match first, then case-insensitive contains as fallback
        string? exactMatch = null;
        string? containsMatch = null;
        foreach (var item in values)
        {
            var name = item?["name"]?.GetValue<string>() ?? string.Empty;
            var itemId = item?["typeId"]?.GetValue<string>() ?? item?["id"]?.GetValue<string>();
            if (string.Equals(name, processTemplateName, StringComparison.OrdinalIgnoreCase))
            {
                exactMatch = itemId;
                break;
            }
            if (containsMatch is null && name.Contains(processTemplateName, StringComparison.OrdinalIgnoreCase))
            {
                containsMatch = itemId;
            }
        }

        return exactMatch ?? containsMatch;
    }

    private async Task<string?> TryGetProcessWorkItemTypeReferenceNameAsync(
        HttpClient client,
        string processId,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"_apis/work/processes/{Uri.EscapeDataString(processId)}/workItemTypes?api-version=7.1-preview.2",
            cancellationToken);

        var json = await ReadJsonAsync(response, cancellationToken);
        var values = json?["value"]?.AsArray() ?? new JsonArray();

        foreach (var item in values)
        {
            var name = item?["name"]?.GetValue<string>();
            if (!string.Equals(name, "User Story", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return item?["referenceName"]?.GetValue<string>();
        }

        return null;
    }

    private async Task<FieldStatus> GetCurrentAgentProcessFieldStatusAsync(
        HttpClient client,
        string? processTemplateName,
        CancellationToken cancellationToken,
        string? knownProcessId = null)
    {
        if (string.IsNullOrWhiteSpace(processTemplateName))
        {
            return FieldStatus.Missing;
        }

        var processId = knownProcessId ?? await TryGetProcessIdAsync(client, processTemplateName, cancellationToken);
        if (string.IsNullOrWhiteSpace(processId))
        {
            return FieldStatus.Missing;
        }

        var workItemTypeReferenceName = await TryGetProcessWorkItemTypeReferenceNameAsync(client, processId, cancellationToken);
        if (string.IsNullOrWhiteSpace(workItemTypeReferenceName))
        {
            return FieldStatus.Missing;
        }

        return await GetCurrentAgentProcessFieldStatusAsync(
            client,
            processId,
            workItemTypeReferenceName,
            cancellationToken);
    }

    private async Task<FieldStatus> GetCurrentAgentProcessFieldStatusAsync(
        HttpClient client,
        string processId,
        string workItemTypeReferenceName,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"_apis/work/processes/{Uri.EscapeDataString(processId)}/workItemTypes/{Uri.EscapeDataString(workItemTypeReferenceName)}/fields?api-version=7.1-preview.2",
            cancellationToken);

        var json = await ReadJsonAsync(response, cancellationToken);
        var values = json?["value"]?.AsArray() ?? new JsonArray();

        foreach (var item in values)
        {
            var referenceName = item?["referenceName"]?.GetValue<string>();
            if (!string.Equals(referenceName, CustomFieldNames.CurrentAIAgent, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fieldType = item?["type"]?.GetValue<string>() ?? string.Empty;
            var normalizedType = fieldType.Trim();
            var isPicklistByType = string.Equals(normalizedType, "picklistString", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(normalizedType, "picklist", StringComparison.OrdinalIgnoreCase);
            var hasPickList = item?["pickList"] is JsonObject;
            var allowedValues = item?["allowedValues"]?.AsArray();
            var hasAllowedValues = allowedValues is { Count: > 0 };
            var isPicklist = isPicklistByType && (hasPickList || hasAllowedValues);

            return new FieldStatus(true, isPicklist, normalizedType);
        }

        return FieldStatus.Missing;
    }

    private async Task<bool> TryCreateCurrentAgentPicklistFieldAsync(
        HttpClient client,
        string processTemplateName,
        List<string> warnings,
        CancellationToken cancellationToken,
        string? knownProcessId = null)
    {
        var processId = knownProcessId ?? await TryGetProcessIdAsync(client, processTemplateName, cancellationToken);
        if (string.IsNullOrWhiteSpace(processId))
        {
            warnings.Add($"Could not resolve process '{processTemplateName}' to create Current AI Agent picklist field.");
            return false;
        }

        var workItemTypeReferenceName = await TryGetProcessWorkItemTypeReferenceNameAsync(client, processId, cancellationToken);
        if (string.IsNullOrWhiteSpace(workItemTypeReferenceName))
        {
            warnings.Add("Could not resolve User Story work item type reference for Current AI Agent picklist creation.");
            return false;
        }

        var picklistId = await TryGetOrCreatePicklistIdAsync(client, warnings, cancellationToken);
        if (string.IsNullOrWhiteSpace(picklistId))
        {
            return false;
        }

        var globalFieldStatus = await GetFieldStatusAsync(client, CustomFieldNames.CurrentAIAgent, cancellationToken);
        if (!globalFieldStatus.Exists)
        {
            var ensuredGlobalBaseField = await TryEnsureCurrentAgentBaseFieldAsync(
                client,
                picklistId,
                warnings,
                cancellationToken);

            if (!ensuredGlobalBaseField)
            {
                warnings.Add("Could not create Current AI Agent base field definition.");
                return false;
            }
        }

        var payload = new JsonObject
        {
            ["referenceName"] = CustomFieldNames.CurrentAIAgent,
            ["name"] = "Current AI Agent",
            ["description"] = "Active AI owner for this story (blank means no AI agent currently working)",
            ["type"] = "picklistString",
            ["required"] = false,
            ["readOnly"] = false,
            ["allowGroups"] = false,
            ["defaultValue"] = string.Empty,
            ["allowedValues"] = new JsonArray(CurrentAgentPicklistValues.Select(v => JsonValue.Create(v)).ToArray()),
            ["pickList"] = new JsonObject
            {
                ["id"] = picklistId
            }
        };

        var attachPayload = new JsonObject
        {
            ["referenceName"] = CustomFieldNames.CurrentAIAgent,
            ["required"] = false,
            ["defaultValue"] = string.Empty
        };

        var created = await TryCreateProcessFieldAsync(
            client,
            processId,
            workItemTypeReferenceName,
            attachPayload,
            warnings,
            treatConflictAsSuccess: true,
            cancellationToken);

        if (!created)
        {
            return false;
        }

        var converted = await TryUpdateProcessFieldAsPicklistAsync(
            client,
            processId,
            workItemTypeReferenceName,
            payload,
            warnings,
            cancellationToken);

        if (converted)
        {
            var statusAfterConvert = await GetCurrentAgentProcessFieldStatusAsync(
                client,
                processId,
                workItemTypeReferenceName,
                cancellationToken);

            if (statusAfterConvert.IsPicklist)
            {
                return true;
            }

            warnings.Add($"Current AI Agent conversion API returned success but field type remains '{statusAfterConvert.Type}'. Retrying with delete/recreate.");
        }

        var removed = await TryDeleteProcessFieldAsync(
            client,
            processId,
            workItemTypeReferenceName,
            CustomFieldNames.CurrentAIAgent,
            warnings,
            cancellationToken);

        if (!removed)
        {
            return false;
        }

        var recreated = await TryCreateProcessFieldAsync(
            client,
            processId,
            workItemTypeReferenceName,
            payload,
            warnings,
            treatConflictAsSuccess: true,
            cancellationToken);

        if (!recreated)
        {
            return false;
        }

        return await TryUpdateProcessFieldAsPicklistAsync(
            client,
            processId,
            workItemTypeReferenceName,
            payload,
            warnings,
            cancellationToken);
    }

    private async Task<bool> TryEnsureAutonomyAndReviewDefaultsAsync(
        HttpClient client,
        string processTemplateName,
        List<string> warnings,
        CancellationToken cancellationToken,
        string? knownProcessId = null)
    {
        var processId = knownProcessId ?? await TryGetProcessIdAsync(client, processTemplateName, cancellationToken);
        if (string.IsNullOrWhiteSpace(processId))
        {
            warnings.Add($"Could not resolve process '{processTemplateName}' to enforce Autonomy/Review defaults.");
            return false;
        }

        var workItemTypeReferenceName = await TryGetProcessWorkItemTypeReferenceNameAsync(client, processId, cancellationToken);
        if (string.IsNullOrWhiteSpace(workItemTypeReferenceName))
        {
            warnings.Add("Could not resolve User Story work item type reference for Autonomy/Review defaults.");
            return false;
        }

        var autonomyPicklistId = await TryGetOrCreateAutonomyPicklistIdAsync(client, warnings, cancellationToken);
        if (string.IsNullOrWhiteSpace(autonomyPicklistId))
        {
            return false;
        }

        var ensuredAutonomyBaseField = await TryEnsureAutonomyBaseFieldAsync(
            client,
            autonomyPicklistId,
            warnings,
            cancellationToken);

        if (!ensuredAutonomyBaseField)
        {
            warnings.Add("Could not ensure global AI Autonomy Level base field as picklist. If Custom.AutonomyLevel exists as textbox/string, delete and recreate it as Picklist (string).");
            return false;
        }

        var autonomyPayload = new JsonObject
        {
            ["referenceName"] = CustomFieldNames.AutonomyLevel,
            ["name"] = "AI Autonomy Level",
            ["description"] = "Controls how far the AI pipeline runs automatically (1-5).",
            ["type"] = "picklistString",
            ["required"] = false,
            ["readOnly"] = false,
            ["allowGroups"] = false,
            ["defaultValue"] = DefaultAutonomyLevel,
            ["allowedValues"] = new JsonArray(AutonomyLevelPicklistValues.Select(v => JsonValue.Create(v)).ToArray()),
            ["pickList"] = new JsonObject
            {
                ["id"] = autonomyPicklistId
            }
        };

        var autonomyAttachPayload = new JsonObject
        {
            ["referenceName"] = CustomFieldNames.AutonomyLevel,
            ["required"] = false,
            ["defaultValue"] = DefaultAutonomyLevel
        };

        _ = await TryCreateProcessFieldAsync(
            client,
            processId,
            workItemTypeReferenceName,
            autonomyAttachPayload,
            warnings,
            treatConflictAsSuccess: true,
            cancellationToken);

        var autonomyUpdated = await TryUpdateProcessFieldAsync(
            client,
            processId,
            workItemTypeReferenceName,
            CustomFieldNames.AutonomyLevel,
            autonomyPayload,
            warnings,
            cancellationToken,
            "AI Autonomy Level");

        if (!autonomyUpdated)
        {
            var removed = await TryDeleteProcessFieldAsync(
                client,
                processId,
                workItemTypeReferenceName,
                CustomFieldNames.AutonomyLevel,
                warnings,
                cancellationToken);

            if (removed)
            {
                var recreated = await TryCreateProcessFieldAsync(
                    client,
                    processId,
                    workItemTypeReferenceName,
                    autonomyPayload,
                    warnings,
                    treatConflictAsSuccess: true,
                    cancellationToken);

                if (recreated)
                {
                    autonomyUpdated = await TryUpdateProcessFieldAsync(
                        client,
                        processId,
                        workItemTypeReferenceName,
                        CustomFieldNames.AutonomyLevel,
                        autonomyPayload,
                        warnings,
                        cancellationToken,
                        "AI Autonomy Level");
                }
            }
        }

        var autonomyProcessStatus = await GetProcessFieldStatusAsync(
            client,
            processId,
            workItemTypeReferenceName,
            CustomFieldNames.AutonomyLevel,
            cancellationToken);

        if (!autonomyProcessStatus.Exists)
        {
            warnings.Add("AI Autonomy Level process field is not attached to User Story after provisioning.");
            return false;
        }

        if (!autonomyProcessStatus.IsPicklist)
        {
            warnings.Add($"AI Autonomy Level field attached but not picklist (type: '{autonomyProcessStatus.Type}'). Azure DevOps kept textbox semantics.");
            return false;
        }

        var autonomyAttached = await TryIsProcessFieldAttachedAsync(
            client,
            processId,
            workItemTypeReferenceName,
            CustomFieldNames.AutonomyLevel,
            warnings,
            cancellationToken,
            "AI Autonomy Level");

        if (!autonomyAttached)
        {
            return false;
        }

        var minReviewPayload = new JsonObject
        {
            ["referenceName"] = CustomFieldNames.MinimumReviewScore,
            ["name"] = "AI Minimum Review Score",
            ["required"] = false,
            ["defaultValue"] = DefaultMinimumReviewScore
        };

        var minReviewAttachPayload = new JsonObject
        {
            ["referenceName"] = CustomFieldNames.MinimumReviewScore,
            ["required"] = false,
            ["defaultValue"] = DefaultMinimumReviewScore
        };

        _ = await TryCreateProcessFieldAsync(
            client,
            processId,
            workItemTypeReferenceName,
            minReviewAttachPayload,
            warnings,
            treatConflictAsSuccess: true,
            cancellationToken);

        var minReviewUpdated = await TryUpdateProcessFieldAsync(
            client,
            processId,
            workItemTypeReferenceName,
            CustomFieldNames.MinimumReviewScore,
            minReviewPayload,
            warnings,
            cancellationToken,
            "AI Minimum Review Score");

        return autonomyUpdated && minReviewUpdated;
    }

    private async Task<string?> TryGetOrCreatePicklistIdAsync(HttpClient client, List<string> warnings, CancellationToken cancellationToken)
    {
        using var listResponse = await client.GetAsync("_apis/work/processes/lists?api-version=7.1-preview.1", cancellationToken);
        var listJson = await ReadJsonAsync(listResponse, cancellationToken);
        var lists = listJson?["value"]?.AsArray() ?? new JsonArray();

        foreach (var list in lists)
        {
            var name = list?["name"]?.GetValue<string>();
            if (!string.Equals(name, CurrentAgentPicklistName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return list?["id"]?.GetValue<string>();
        }

        var payload = new JsonObject
        {
            ["name"] = CurrentAgentPicklistName,
            ["type"] = "String",
            ["items"] = new JsonArray(CurrentAgentPicklistValues.Select(v => JsonValue.Create(v)).ToArray())
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "_apis/work/processes/lists?api-version=7.1-preview.1")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            warnings.Add($"Could not create Current AI Agent picklist ({(int)response.StatusCode}): {body}");
            return null;
        }

        var createdJson = await ReadJsonAsync(response, cancellationToken);
        return createdJson?["id"]?.GetValue<string>();
    }

    private async Task<string?> TryGetOrCreateAutonomyPicklistIdAsync(HttpClient client, List<string> warnings, CancellationToken cancellationToken)
    {
        using var listResponse = await client.GetAsync("_apis/work/processes/lists?api-version=7.1-preview.1", cancellationToken);
        var listJson = await ReadJsonAsync(listResponse, cancellationToken);
        var lists = listJson?["value"]?.AsArray() ?? new JsonArray();

        foreach (var list in lists)
        {
            var name = list?["name"]?.GetValue<string>();
            if (!string.Equals(name, AutonomyLevelPicklistName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return list?["id"]?.GetValue<string>();
        }

        var payload = new JsonObject
        {
            ["name"] = AutonomyLevelPicklistName,
            ["type"] = "String",
            ["items"] = new JsonArray(AutonomyLevelPicklistValues.Select(v => JsonValue.Create(v)).ToArray())
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "_apis/work/processes/lists?api-version=7.1-preview.1")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            warnings.Add($"Could not create AI Autonomy Level picklist ({(int)response.StatusCode}): {body}");
            return null;
        }

        var createdJson = await ReadJsonAsync(response, cancellationToken);
        return createdJson?["id"]?.GetValue<string>();
    }

    private async Task<bool> TryCreateProcessFieldAsync(
        HttpClient client,
        string processId,
        string workItemTypeReferenceName,
        JsonObject payload,
        List<string> warnings,
        bool treatConflictAsSuccess,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"_apis/work/processes/{Uri.EscapeDataString(processId)}/workItemTypes/{Uri.EscapeDataString(workItemTypeReferenceName)}/fields?api-version=7.1-preview.2")
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
            return treatConflictAsSuccess;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        warnings.Add($"Could not create Current AI Agent process field with type '{payload["type"]?.GetValue<string>()}' ({(int)response.StatusCode}): {body}");
        return false;
    }

    private async Task<bool> TryUpdateProcessFieldAsPicklistAsync(
        HttpClient client,
        string processId,
        string workItemTypeReferenceName,
        JsonObject payload,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"_apis/work/processes/{Uri.EscapeDataString(processId)}/workItemTypes/{Uri.EscapeDataString(workItemTypeReferenceName)}/fields/{Uri.EscapeDataString(CustomFieldNames.CurrentAIAgent)}?api-version=7.1-preview.2")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        warnings.Add($"Could not update Current AI Agent process field to picklist ({(int)response.StatusCode}): {body}");
        return false;
    }

    private async Task<bool> TryUpdateProcessFieldAsync(
        HttpClient client,
        string processId,
        string workItemTypeReferenceName,
        string referenceName,
        JsonObject payload,
        List<string> warnings,
        CancellationToken cancellationToken,
        string fieldLabel)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"_apis/work/processes/{Uri.EscapeDataString(processId)}/workItemTypes/{Uri.EscapeDataString(workItemTypeReferenceName)}/fields/{Uri.EscapeDataString(referenceName)}?api-version=7.1-preview.2")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        warnings.Add($"Could not update {fieldLabel} process field ({(int)response.StatusCode}): {body}");
        return false;
    }

    private async Task<bool> TryDeleteProcessFieldAsync(
        HttpClient client,
        string processId,
        string workItemTypeReferenceName,
        string referenceName,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"_apis/work/processes/{Uri.EscapeDataString(processId)}/workItemTypes/{Uri.EscapeDataString(workItemTypeReferenceName)}/fields/{Uri.EscapeDataString(referenceName)}?api-version=7.1-preview.2");

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
        {
            return true;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        warnings.Add($"Could not delete existing Current AI Agent process field before recreation ({(int)response.StatusCode}): {body}");
        return false;
    }

    private async Task<FieldStatus> GetFieldStatusAsync(HttpClient client, string referenceName, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"_apis/wit/fields/{Uri.EscapeDataString(referenceName)}?api-version=7.1",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return FieldStatus.Missing;
        }

        if (response.IsSuccessStatusCode)
        {
            var json = await ReadJsonAsync(response, cancellationToken);
            var isPicklist = json?["isPicklist"]?.GetValue<bool>() ?? false;
            var type = json?["type"]?.GetValue<string>() ?? string.Empty;
            return new FieldStatus(true, isPicklist, type);
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

    private async Task<bool> TryEnsureCurrentAgentBaseFieldAsync(
        HttpClient client,
        string picklistId,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var picklistPayload = new JsonObject
        {
            ["name"] = "Current AI Agent",
            ["referenceName"] = CustomFieldNames.CurrentAIAgent,
            ["type"] = "string",
            ["usage"] = "workItem",
            ["isPicklist"] = true,
            ["pickList"] = new JsonObject
            {
                ["id"] = picklistId
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "_apis/wit/fields?api-version=7.1")
        {
            Content = new StringContent(picklistPayload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
        {
            var status = await GetFieldStatusAsync(client, CustomFieldNames.CurrentAIAgent, cancellationToken);
            if (status.Exists)
            {
                return true;
            }
        }
        else
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            warnings.Add($"Picklist-style base field create attempt failed ({(int)response.StatusCode}): {body}");
        }

        var fallbackCreated = await TryCreateFieldAsync(
            client,
            new FieldDefinition("Current AI Agent", CustomFieldNames.CurrentAIAgent, "string"),
            cancellationToken);

        if (!fallbackCreated)
        {
            return false;
        }

        var finalStatus = await GetFieldStatusAsync(client, CustomFieldNames.CurrentAIAgent, cancellationToken);
        return finalStatus.Exists;
    }

    private async Task<bool> TryEnsureAutonomyBaseFieldAsync(
        HttpClient client,
        string picklistId,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var currentStatus = await GetFieldStatusAsync(client, CustomFieldNames.AutonomyLevel, cancellationToken);
        if (currentStatus.Exists)
        {
            if (currentStatus.IsPicklist)
            {
                return true;
            }

            warnings.Add($"AI Autonomy Level base field exists with type '{currentStatus.Type}' (not picklist). Azure DevOps does not support in-place type conversion for global fields.");
            return false;
        }

        var picklistPayload = new JsonObject
        {
            ["name"] = "AI Autonomy Level",
            ["referenceName"] = CustomFieldNames.AutonomyLevel,
            ["type"] = "string",
            ["usage"] = "workItem",
            ["isPicklist"] = true,
            ["pickList"] = new JsonObject
            {
                ["id"] = picklistId
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "_apis/wit/fields?api-version=7.1")
        {
            Content = new StringContent(picklistPayload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request, cancellationToken);
        if (!(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict))
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            warnings.Add($"Could not create AI Autonomy Level base picklist field ({(int)response.StatusCode}): {body}");
            return false;
        }

        var finalStatus = await GetFieldStatusAsync(client, CustomFieldNames.AutonomyLevel, cancellationToken);
        if (finalStatus.Exists && finalStatus.IsPicklist)
        {
            return true;
        }

        warnings.Add($"AI Autonomy Level base field exists but is not picklist after create attempt (type: '{finalStatus.Type}').");
        return false;
    }

    private async Task<bool> TryIsProcessFieldAttachedAsync(
        HttpClient client,
        string processId,
        string workItemTypeReferenceName,
        string referenceName,
        List<string> warnings,
        CancellationToken cancellationToken,
        string fieldLabel)
    {
        using var response = await client.GetAsync(
            $"_apis/work/processes/{Uri.EscapeDataString(processId)}/workItemTypes/{Uri.EscapeDataString(workItemTypeReferenceName)}/fields/{Uri.EscapeDataString(referenceName)}?api-version=7.1-preview.2",
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        warnings.Add($"{fieldLabel} is not attached to User Story in process '{processId}' ({(int)response.StatusCode}): {body}");
        return false;
    }

    private async Task<FieldStatus> GetProcessFieldStatusAsync(
        HttpClient client,
        string processId,
        string workItemTypeReferenceName,
        string referenceName,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"_apis/work/processes/{Uri.EscapeDataString(processId)}/workItemTypes/{Uri.EscapeDataString(workItemTypeReferenceName)}/fields?api-version=7.1-preview.2",
            cancellationToken);

        var json = await ReadJsonAsync(response, cancellationToken);
        var values = json?["value"]?.AsArray() ?? new JsonArray();

        foreach (var item in values)
        {
            var itemReferenceName = item?["referenceName"]?.GetValue<string>();
            if (!string.Equals(itemReferenceName, referenceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fieldType = item?["type"]?.GetValue<string>() ?? string.Empty;
            var normalizedType = fieldType.Trim();
            var isPicklistByType = string.Equals(normalizedType, "picklistString", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(normalizedType, "picklistInteger", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(normalizedType, "picklist", StringComparison.OrdinalIgnoreCase);
            var hasPickList = item?["pickList"] is JsonObject;
            var allowedValues = item?["allowedValues"]?.AsArray();
            var hasAllowedValues = allowedValues is { Count: > 0 };
            var isPicklist = isPicklistByType && (hasPickList || hasAllowedValues);

            return new FieldStatus(true, isPicklist, normalizedType);
        }

        return FieldStatus.Missing;
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

    private readonly record struct FieldStatus(bool Exists, bool IsPicklist, string Type)
    {
        public static FieldStatus Missing => new(false, false, string.Empty);
    }

    private sealed record ProjectInfo(string Id, string Name, string? ProcessTemplateName, string? ProcessTemplateId = null);
}