using ADOm8.Onboarding.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;

namespace ADOm8.Onboarding.Orchestrators;

public sealed class ProvisioningOrchestrator
{
    [Function("ProvisioningOrchestrator")]
    public async Task RunOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var input = context.GetInput<ProvisioningInput>()
            ?? throw new InvalidOperationException("Provisioning input is required.");

        var retry = TaskOptions.FromRetryPolicy(new RetryPolicy(3, TimeSpan.FromSeconds(5)));

        await context.CallActivityAsync("ValidateCredentials", input, retry);
        await context.CallActivityAsync("StoreSecrets", input, retry);
        var infraResult = await context.CallActivityAsync<InfraResult>("ProvisionInfrastructure", input, retry);
        var adoResult = await context.CallActivityAsync<AdoResult>("ConfigureAdo", input, retry);
        await context.CallActivityAsync("GenerateCodebaseContext", input, retry);
        var storyResult = await context.CallActivityAsync<StoryResult>("CreateInitStory", input, retry);

        await context.CallActivityAsync("FinalizeRegistration", new FinalizeInput(input, infraResult, adoResult, storyResult), retry);
    }
}
