using System.Text.RegularExpressions;

namespace ADOm8.Onboarding.Services;

public interface IProjectIdService
{
    string GenerateProjectId(string adoOrg, string adoProject);
}

public sealed class ProjectIdService : IProjectIdService
{
    private static readonly Regex InvalidChars = new("[^a-z0-9-]", RegexOptions.Compiled);
    private static readonly Regex MultiDash = new("-+", RegexOptions.Compiled);

    public string GenerateProjectId(string adoOrg, string adoProject)
    {
        var orgSlug = Slugify(adoOrg.Replace("https://dev.azure.com/", string.Empty, StringComparison.OrdinalIgnoreCase));
        var projectSlug = Slugify(adoProject);
        var candidate = $"{projectSlug}-{orgSlug}".Trim('-');

        if (candidate.Length > 50)
        {
            candidate = candidate[..50].Trim('-');
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new ArgumentException("Unable to derive valid projectId from inputs.");
        }

        return candidate;
    }

    private static string Slugify(string value)
    {
        var lowered = value.ToLowerInvariant().Trim().Replace(' ', '-');
        var cleaned = InvalidChars.Replace(lowered, "-");
        return MultiDash.Replace(cleaned, "-").Trim('-');
    }
}
