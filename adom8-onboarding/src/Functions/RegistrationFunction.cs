using System.Net;
using System.Text.Json;
using ADOm8.Onboarding.Models;
using ADOm8.Onboarding.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
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
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "projects/register")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        var body = await JsonSerializer.DeserializeAsync<ProjectRegistrationRequest>(req.Body);
        if (body is null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid request body.");
            return bad;
        }

        var projectId = _projectIdService.GenerateProjectId(body.AdoOrg, body.AdoProject);
        var provisioningId = Guid.NewGuid().ToString("N");

        await client.ScheduleNewOrchestrationInstanceAsync(
            "ProvisioningOrchestrator",
            provisioningId,
            new ProvisioningInput(provisioningId, projectId, body));

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new
        {
            provisioningId,
            statusUrl = $"/projects/{provisioningId}/status",
            projectId
        });

        return response;
    }
}
