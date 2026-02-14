using AIAgents.Core.Configuration;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Services;
using AIAgents.Functions.Agents;
using AIAgents.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((context, services) =>
    {
        var configuration = context.Configuration;

        // Bind configuration sections to IOptions<T>
        services.Configure<AIOptions>(configuration.GetSection(AIOptions.SectionName));
        services.Configure<AzureDevOpsOptions>(configuration.GetSection(AzureDevOpsOptions.SectionName));
        services.Configure<GitOptions>(configuration.GetSection(GitOptions.SectionName));
        services.Configure<DeploymentOptions>(configuration.GetSection(DeploymentOptions.SectionName));
        services.Configure<GitHubOptions>(configuration.GetSection(GitHubOptions.SectionName));
        services.Configure<CodebaseDocumentationOptions>(configuration.GetSection(CodebaseDocumentationOptions.SectionName));

        // Application Insights — register BEFORE HTTP resilience handlers
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Named HTTP client for AI API calls with resilience pipeline.
        // Base URL and auth are set per-request by AIClient so that
        // per-agent model overrides can target different providers.
        services.AddHttpClient("AIClient")
        .AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(120);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.Delay = TimeSpan.FromSeconds(2);
        });

        // Core services
        services.AddSingleton<IAIClient, AIClient>();
        services.AddSingleton<IAIClientFactory, AIClientFactory>();
        services.AddSingleton<IAzureDevOpsClient, AzureDevOpsClient>();
        services.AddSingleton<IGitOperations, GitOperations>();
        services.AddSingleton<IStoryContextFactory, StoryContextFactory>();
        services.AddSingleton<ITemplateEngine, ScribanTemplateEngine>();

        // Repository provider — GitHub or Azure DevOps Repos (based on Git:Provider config)
        var gitProvider = configuration[$"{GitOptions.SectionName}:Provider"] ?? "GitHub";
        if (gitProvider.Equals("GitHub", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IRepositoryProvider, GitHubRepositoryProvider>();
        }
        else
        {
            services.AddSingleton<IRepositoryProvider, AzureDevOpsRepositoryProvider>();
        }

        // Activity logging
        services.AddSingleton<IActivityLogger, TableStorageActivityLogger>();

        // Codebase context provider (loads .agent/ docs for AI prompts)
        services.AddSingleton<ICodebaseContextProvider, CodebaseContextLoader>();

        // Agent services — keyed DI for dispatcher routing
        services.AddKeyedScoped<IAgentService, PlanningAgentService>("Planning");
        services.AddKeyedScoped<IAgentService, CodingAgentService>("Coding");
        services.AddKeyedScoped<IAgentService, TestingAgentService>("Testing");
        services.AddKeyedScoped<IAgentService, ReviewAgentService>("Review");
        services.AddKeyedScoped<IAgentService, DocumentationAgentService>("Documentation");
        services.AddKeyedScoped<IAgentService, DeploymentAgentService>("Deployment");
        services.AddKeyedScoped<IAgentService, CodebaseDocumentationAgentService>("CodebaseDocumentation");
    })
    .Build();

host.Run();
