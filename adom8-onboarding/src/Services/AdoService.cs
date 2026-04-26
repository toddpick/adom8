namespace ADOm8.Onboarding.Services;

public interface IAdoService
{
    Task ValidateProjectAccessAsync(string adoOrg, string adoProject, string pat, CancellationToken cancellationToken = default);
}

public sealed class AdoService : IAdoService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public AdoService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task ValidateProjectAccessAsync(string adoOrg, string adoProject, string pat, CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient();
        var token = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($":{pat}"));
        var organizationUrl = NormalizeOrganizationUrl(adoOrg);
        var projectSegment = Uri.EscapeDataString(adoProject);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{organizationUrl}/_apis/projects/{projectSegment}?api-version=7.1-preview.4");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);

        var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"ADO project validation failed with {(int)response.StatusCode}.");
        }
    }

    private static string NormalizeOrganizationUrl(string adoOrg)
    {
        var trimmed = adoOrg.Trim().TrimEnd('/');
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        return $"https://dev.azure.com/{trimmed}";
    }
}
