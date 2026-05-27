using ADOm8.Onboarding.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace ADOm8.Onboarding.Activities;

public sealed class GenerateCodebaseContextActivity
{
    private readonly ILogger<GenerateCodebaseContextActivity> _logger;

    public GenerateCodebaseContextActivity(ILogger<GenerateCodebaseContextActivity> logger)
    {
        _logger = logger;
    }

    [Function("GenerateCodebaseContext")]
    public Task Run([ActivityTrigger] ProvisioningInput input)
    {
        _logger.LogInformation("CODEBASE_CONTEXT generation requested for {ProjectId} using GitHub REST APIs only.", input.ProjectId);
        return Task.CompletedTask;
    }
}
