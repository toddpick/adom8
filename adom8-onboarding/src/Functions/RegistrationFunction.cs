using System.Net;
using System.Text.Json;
using ADOm8.Onboarding.Models;
using ADOm8.Onboarding.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;

namespace ADOm8.Onboarding.Functions;

public sealed class RegistrationFunction
{
    private readonly IProjectIdService _projectIdService;

    public RegistrationFunction(IProjectIdService projectIdService)
    {
        _projectIdService = projectIdService;
    }

    [Function("RegisterProject")]
    public async Task<HttpResponseData> Register(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "projects/register")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        ProjectRegistrationRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<ProjectRegistrationRequest>(
                req.Body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            body = null;
        }

        if (body is null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid request body.");
            return bad;
        }

        var validationErrors = Validate(body).ToArray();
        if (validationErrors.Length > 0)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { errors = validationErrors });
            return bad;
        }

        var projectId = _projectIdService.GenerateProjectId(body.AdoOrg, body.AdoProject);
        var provisioningId = Guid.NewGuid().ToString("N");

        await client.ScheduleNewOrchestrationInstanceAsync(
            "ProvisioningOrchestrator",
            new ProvisioningInput(provisioningId, projectId, body),
            new StartOrchestrationOptions { InstanceId = provisioningId });

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new
        {
            provisioningId,
            statusUrl = $"/projects/{provisioningId}/status",
            projectId
        });

        return response;
    }

    private static IEnumerable<string> Validate(ProjectRegistrationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName)) yield return "displayName is required.";
        if (string.IsNullOrWhiteSpace(request.AdoOrg)) yield return "adoOrg is required.";
        if (string.IsNullOrWhiteSpace(request.AdoProject)) yield return "adoProject is required.";
        if (string.IsNullOrWhiteSpace(request.AdoPat)) yield return "adoPat is required.";
        if (string.IsNullOrWhiteSpace(request.GithubRepo)) yield return "githubRepo is required.";
        if (string.IsNullOrWhiteSpace(request.GithubPat)) yield return "githubPat is required.";
        if (request.DefaultAutonomyLevel is < 1 or > 5) yield return "defaultAutonomyLevel must be between 1 and 5.";
    }
}
