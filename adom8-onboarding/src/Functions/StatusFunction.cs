using System.Net;
using ADOm8.Onboarding.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;

namespace ADOm8.Onboarding.Functions;

public sealed class StatusFunction
{
    [Function("GetProvisioningStatus")]
    public async Task<HttpResponseData> GetStatus(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "projects/{provisioningId}/status")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        string provisioningId)
    {
        var metadata = await client.GetInstanceAsync(provisioningId);
        var status = new ProvisioningStatus
        {
            ProvisioningId = provisioningId,
            Stage = metadata?.RuntimeStatus.ToString() ?? "not-found",
            ProgressPercent = metadata is null ? 0 : ResolveProgress(metadata.RuntimeStatus.ToString()),
            RuntimeStatus = metadata?.RuntimeStatus.ToString() ?? "not-found"
        };

        var response = req.CreateResponse(metadata is null ? HttpStatusCode.NotFound : HttpStatusCode.OK);
        await response.WriteAsJsonAsync(status);
        return response;
    }

    private static int ResolveProgress(string? status)
        => status switch
        {
            "Completed" => 100,
            "Running" => 45,
            "Pending" => 10,
            "Failed" => 100,
            _ => 0
        };
}
