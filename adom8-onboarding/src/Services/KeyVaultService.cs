using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

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
            ?? throw new InvalidOperationException("Onboarding:SharedKeyVaultUrl is required.");
        _secretClient = new SecretClient(new Uri(keyVaultUrl), new DefaultAzureCredential());
    }

    public async Task SetSecretAsync(string name, string value, CancellationToken cancellationToken = default)
        => await _secretClient.SetSecretAsync(name, value, cancellationToken);
}
