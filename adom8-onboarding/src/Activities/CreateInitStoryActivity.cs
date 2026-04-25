using ADOm8.Onboarding.Models;
using Microsoft.Azure.Functions.Worker;

namespace ADOm8.Onboarding.Activities;

public sealed class CreateInitStoryActivity
{
    [Function("CreateInitStory")]
    public Task<StoryResult> Run([ActivityTrigger] ProvisioningInput input)
    {
        var storyId = 1042;
        var storyUrl = $"{input.Request.AdoOrg}/{input.Request.AdoProject}/_workitems/edit/{storyId}";
        var agentTabUrl = $"{input.Request.GithubRepo}/pulls?q=is:open+label:copilot";

        return Task.FromResult(new StoryResult(storyId, storyUrl, agentTabUrl));
    }
}
