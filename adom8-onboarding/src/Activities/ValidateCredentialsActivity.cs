using ADOm8.Onboarding.Models;
using ADOm8.Onboarding.Services;
using Microsoft.Azure.Functions.Worker;

namespace ADOm8.Onboarding.Activities;

public sealed class ValidateCredentialsActivity
{
    private readonly IAdoService _adoService;
    private readonly IGitHubService _gitHubService;

    public ValidateCredentialsActivity(IAdoService adoService, IGitHubService gitHubService)
    {
        _adoService = adoService;
        _gitHubService = gitHubService;
    }

    [Function("ValidateCredentials")]
    public async Task Run([ActivityTrigger] ProvisioningInput input)
    {
        await _adoService.ValidateProjectAccessAsync(input.Request.AdoOrg, input.Request.AdoProject, input.Request.AdoPat);
        await _gitHubService.ValidateRepositoryAccessAsync(input.Request.GithubRepo, input.Request.GithubPat, input.Request.PrimaryBranch);
    }
}
