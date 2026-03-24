using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AIAgents.Functions.Functions;

/// <summary>
/// Lightweight Function-auth probe used by the dashboard login screen to verify
/// the supplied dashboard key before allowing access.
/// </summary>
public sealed class ValidateDashboardKey
{
    private readonly ILogger<ValidateDashboardKey> _logger;

    public ValidateDashboardKey(ILogger<ValidateDashboardKey> logger)
    {
        _logger = logger;
    }

    [Function("ValidateDashboardKey")]
    public IActionResult Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "validate-dashboard-key")] HttpRequest req)
    {
        _logger.LogDebug("Dashboard key validation request received");
        return new OkObjectResult(new DashboardKeyValidationResponse { Valid = true });
    }
}

internal sealed class DashboardKeyValidationResponse
{
    public bool Valid { get; init; }
}
