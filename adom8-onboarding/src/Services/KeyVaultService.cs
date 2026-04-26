using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;

namespace ADOm8.Onboarding.Services;

public interface IKeyVaultService
{
    Task SetSecretAsync(string name, string value, CancellationToken cancellationToken = default);
}

public sealed class KeyVaultService : IKeyVaultService
{
    private readonly SecretClient _secretClient;

    public KeyVaultService(IConfiguration configuration)
    {
        var keyVaultUrl = configuration["Onboarding:SharedKeyVaultUrl"]
            ?? BuildVaultUrl(configuration["Onboarding:SharedKeyVaultName"]);
        _secretClient = new SecretClient(new Uri(keyVaultUrl), new DefaultAzureCredential());
    }

    public async Task SetSecretAsync(string name, string value, CancellationToken cancellationToken = default)
        => await _secretClient.SetSecretAsync(name, value, cancellationToken);

    private static string BuildVaultUrl(string? vaultName)
    {
        if (string.IsNullOrWhiteSpace(vaultName))
        {
            throw new InvalidOperationException("Onboarding:SharedKeyVaultUrl or Onboarding:SharedKeyVaultName is required.");
        }

        return $"https://{vaultName}.vault.azure.net/";
    }
}
