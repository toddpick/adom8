using ADOm8.Onboarding.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        services.AddHttpClient();
        services.AddSingleton<IProjectIdService, ProjectIdService>();
        services.AddSingleton<IKeyVaultService, KeyVaultService>();
        services.AddSingleton<IAdoService, AdoService>();
        services.AddSingleton<IGitHubService, GitHubService>();
    })
    .Build();

host.Run();
