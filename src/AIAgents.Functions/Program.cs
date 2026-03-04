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
        services.Configure<RepositoryCapacityOptions>(configuration.GetSection(RepositoryCapacityOptions.SectionName));
        services.Configure<InputValidationOptions>(configuration.GetSection(InputValidationOptions.SectionName));
        services.Configure<CopilotOptions>(configuration.GetSection(CopilotOptions.SectionName));
        services.Configure<SaasOptions>(configuration.GetSection(SaasOptions.SectionName));

        // Application Insights — register BEFORE HTTP resilience handlers
        try
        {
            services.AddApplicationInsightsTelemetryWorkerService();
            services.ConfigureFunctionsApplicationInsights();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Application Insights initialization skipped: {ex.Message}");
        }

        // Named HTTP client for AI API calls with resilience pipeline.
        // Base URL and auth are set per-request by AIClient so that
        // per-agent model overrides can target different providers.
        // Circuit breaker trips after 5 consecutive failures, stays open 30s.
        services.AddHttpClient("AIClient")
        .AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(300);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(9);
            options.Retry.MaxRetryAttempts = 1;
            options.Retry.Delay = TimeSpan.FromSeconds(2);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(620);
            options.CircuitBreaker.FailureRatio = 0.8;
            options.CircuitBreaker.MinimumThroughput = 5;
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
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
            services.AddSingleton<IRepositorySizingService, GitHubRepositorySizingService>();
            services.AddSingleton<ICodebaseOnboardingService, GitHubCodebaseOnboardingService>();
        }
        else
        {
            services.AddSingleton<IRepositoryProvider, AzureDevOpsRepositoryProvider>();
            services.AddSingleton<IRepositorySizingService, NoOpRepositorySizingService>();
            services.AddSingleton<ICodebaseOnboardingService, NoOpCodebaseOnboardingService>();
        }

        // Activity logging
        services.AddSingleton<IActivityLogger, TableStorageActivityLogger>();

        // Agent task queue — abstracts Azure Storage Queue for testability
        services.AddSingleton<IAgentTaskQueue, AgentTaskQueue>();

        // GitHub API context service — provides file tree, content, and write operations
        // without cloning the repository to local disk.
        services.AddSingleton<IGitHubApiContextService, GitHubApiContextService>();

        // Codebase context provider (loads .agent/ docs for AI prompts)
        services.AddSingleton<ICodebaseContextProvider, CodebaseContextLoader>();

        // Input validation — security, length limits, prompt injection detection
        services.AddSingleton<IInputValidator, InputValidator>();

        // Copilot delegation tracking (Azure Table Storage)
        services.AddSingleton<ICopilotDelegationService, TableStorageCopilotDelegationService>();
        services.AddSingleton<IGitHubOrchestrationLauncherService, GitHubOrchestrationLauncherService>();

        // SaaS Mode — optional real-time callback reporting to adom8.dev dashboard
        // No-op when SaaS:Enabled is false (default for fully standalone deployments)
        services.AddSingleton<ISaasCallbackService, SaasCallbackService>();
        services.AddHttpClient("SaasCallback", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

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
