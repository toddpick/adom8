using AIAgents.Core.Configuration;
using AIAgents.Core.Interfaces;
using AIAgents.Core.Services;
using AIAgents.Functions.Agents;
using AIAgents.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

        // Named HTTP client for GitHub REST API calls — shared by all GitHub-facing services.
        // Uses IHttpClientFactory for proper socket management and handler rotation.
        services.AddHttpClient("GitHub")
            .ConfigureHttpClient((sp, client) =>
            {
                var ghOpts = sp.GetRequiredService<IOptions<GitHubOptions>>().Value;
                client.BaseAddress = new Uri("https://api.github.com/");
                client.Timeout = TimeSpan.FromSeconds(90);
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ghOpts.Token);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("AIAgents/1.0");
                client.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
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
            services.AddSingleton<IRepositoryProvider>(sp =>
                new GitHubRepositoryProvider(
                    sp.GetRequiredService<IOptions<GitHubOptions>>(),
                    sp.GetRequiredService<ILogger<GitHubRepositoryProvider>>(),
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient("GitHub")));
            services.AddSingleton<IRepositorySizingService>(sp =>
                new GitHubRepositorySizingService(
                    sp.GetRequiredService<IOptions<GitHubOptions>>(),
                    sp.GetRequiredService<IOptions<RepositoryCapacityOptions>>(),
                    sp.GetRequiredService<ILogger<GitHubRepositorySizingService>>(),
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient("GitHub")));
            services.AddSingleton<ICodebaseOnboardingService>(sp =>
                new GitHubCodebaseOnboardingService(
                    sp.GetRequiredService<IOptions<GitHubOptions>>(),
                    sp.GetRequiredService<IOptions<CodebaseDocumentationOptions>>(),
                    sp.GetRequiredService<ILogger<GitHubCodebaseOnboardingService>>()));
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
        services.AddSingleton<IGitHubApiContextService>(sp =>
            new GitHubApiContextService(
                sp.GetRequiredService<IOptions<GitHubOptions>>(),
                sp.GetRequiredService<ILogger<GitHubApiContextService>>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("GitHub")));

        // Codebase context provider (loads .agent/ docs for AI prompts)
        services.AddSingleton<ICodebaseContextProvider, CodebaseContextLoader>();

        // Input validation — security, length limits, prompt injection detection
        services.AddSingleton<IInputValidator, InputValidator>();

        // Copilot delegation tracking (Azure Table Storage)
        services.AddSingleton<ICopilotDelegationService, TableStorageCopilotDelegationService>();
        services.AddSingleton<IGitHubOrchestrationLauncherService>(sp =>
            new GitHubOrchestrationLauncherService(
                sp.GetRequiredService<IOptions<GitHubOptions>>(),
                sp.GetRequiredService<IOptions<CopilotOptions>>(),
                sp.GetRequiredService<ICopilotDelegationService>(),
                sp.GetRequiredService<ILogger<GitHubOrchestrationLauncherService>>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("GitHub")));

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
