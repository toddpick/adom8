using ADOm8.Onboarding.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace ADOm8.Onboarding.Activities;

public sealed class FinalizeRegistrationActivity
{
    private readonly ILogger<FinalizeRegistrationActivity> _logger;

    public FinalizeRegistrationActivity(ILogger<FinalizeRegistrationActivity> logger)
    {
        _logger = logger;
    }

    [Function("FinalizeRegistration")]
    public Task Run([ActivityTrigger] FinalizeInput input)
    {
        _logger.LogInformation("Finalize registration for {ProjectId}", input.Input.ProjectId);
        return Task.CompletedTask;
    }
}
