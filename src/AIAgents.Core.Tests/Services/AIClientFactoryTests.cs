using AIAgents.Core.Configuration;
using AIAgents.Core.Models;
using AIAgents.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AIAgents.Core.Tests.Services;

/// <summary>
/// Tests for AIClientFactory: provider auto-detection, per-agent config merging,
/// story overrides, tier resolution, and ProviderKeys credential lookup.
/// </summary>
public sealed class AIClientFactoryTests
{
    private readonly AIOptions _defaults = new()
    {
        Provider = "Claude",
        Model = "claude-sonnet-4-20250514",
        ApiKey = "sk-ant-test-key",
        MaxTokens = 4096,
        Temperature = 0.3
    };

    private AIClientFactory CreateFactory(AIOptions? options = null)
    {
        var opts = options ?? _defaults;
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient());
        return new AIClientFactory(
            httpFactory.Object,
            Options.Create(opts),
            NullLogger<AIClient>.Instance);
    }

    // ── DetectProviderFromModel ──────────────────────────────────────

    [Theory]
    [InlineData("claude-sonnet-4-20250514", "Claude")]
    [InlineData("claude-opus-4-20250514", "Claude")]
    [InlineData("claude-haiku-4.5", "Claude")]
    [InlineData("CLAUDE-SONNET-4.5-20250514", "Claude")]
    public void DetectProvider_Claude_Models(string model, string expected)
        => Assert.Equal(expected, AIClientFactory.DetectProviderFromModel(model));

    [Theory]
    [InlineData("gpt-4o", "OpenAI")]
    [InlineData("gpt-5-mini", "OpenAI")]
    [InlineData("gpt-5.1-codex", "OpenAI")]
    [InlineData("GPT-4.1", "OpenAI")]
    [InlineData("o1-preview", "OpenAI")]
    [InlineData("o3-mini", "OpenAI")]
    [InlineData("codex-mini-latest", "OpenAI")]
    public void DetectProvider_OpenAI_Models(string model, string expected)
        => Assert.Equal(expected, AIClientFactory.DetectProviderFromModel(model));

    [Theory]
    [InlineData("gemini-2.5-pro", "Google")]
    [InlineData("gemini-3-flash", "Google")]
    [InlineData("GEMINI-2.5-PRO", "Google")]
    public void DetectProvider_Google_Models(string model, string expected)
        => Assert.Equal(expected, AIClientFactory.DetectProviderFromModel(model));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("unknown-model")]
    [InlineData("llama-3")]
    public void DetectProvider_Returns_Null_For_Unknown(string? model)
        => Assert.Null(AIClientFactory.DetectProviderFromModel(model));

    // ── ResolveEffectiveOptions — global defaults ────────────────────

    [Fact]
    public void Resolve_Uses_Global_Defaults_When_No_Overrides()
    {
        var factory = CreateFactory();
        var effective = factory.ResolveEffectiveOptions("Planning");

        Assert.Equal("Claude", effective.Provider);
        Assert.Equal("claude-sonnet-4-20250514", effective.Model);
        Assert.Equal("sk-ant-test-key", effective.ApiKey);
    }

    // ── ResolveEffectiveOptions — per-agent config ───────────────────

    [Fact]
    public void Resolve_Applies_PerAgent_Config_Overrides()
    {
        var options = new AIOptions
        {
            Provider = "Claude",
            Model = "claude-sonnet-4-20250514",
            ApiKey = "sk-ant-test-key",
            AgentModels = new()
            {
                ["Review"] = new AgentAIProfile
                {
                    Provider = "OpenAI",
                    Model = "gpt-4o",
                    ApiKey = "sk-openai-key",
                    Endpoint = "https://api.openai.com/v1"
                }
            }
        };

        var factory = CreateFactory(options);
        var effective = factory.ResolveEffectiveOptions("Review");

        Assert.Equal("OpenAI", effective.Provider);
        Assert.Equal("gpt-4o", effective.Model);
        Assert.Equal("sk-openai-key", effective.ApiKey);
    }

    // ── ResolveEffectiveOptions — story model override + auto-detect ─

    [Fact]
    public void Resolve_AutoDetects_Provider_And_Looks_Up_ProviderKey()
    {
        var options = new AIOptions
        {
            Provider = "Claude",
            Model = "claude-sonnet-4-20250514",
            ApiKey = "sk-ant-test-key",
            ProviderKeys = new()
            {
                ["OpenAI"] = new ProviderKeyConfig
                {
                    ApiKey = "sk-openai-key",
                    Endpoint = "https://api.openai.com/v1"
                }
            }
        };

        var factory = CreateFactory(options);
        var overrides = new StoryModelOverrides
        {
            CodingModel = "gpt-4o"
        };
        var effective = factory.ResolveEffectiveOptions("Coding", overrides);

        Assert.Equal("OpenAI", effective.Provider);
        Assert.Equal("gpt-4o", effective.Model);
        Assert.Equal("sk-openai-key", effective.ApiKey);
        Assert.Equal("https://api.openai.com/v1", effective.Endpoint);
    }

    [Fact]
    public void Resolve_AutoDetects_Google_Provider()
    {
        var options = new AIOptions
        {
            Provider = "Claude",
            Model = "claude-sonnet-4-20250514",
            ApiKey = "sk-ant-test-key",
            ProviderKeys = new()
            {
                ["Google"] = new ProviderKeyConfig
                {
                    ApiKey = "AIza-google-key",
                    Endpoint = "https://generativelanguage.googleapis.com/v1beta/openai"
                }
            }
        };

        var factory = CreateFactory(options);
        var overrides = new StoryModelOverrides
        {
            DocumentationModel = "gemini-3-flash"
        };
        var effective = factory.ResolveEffectiveOptions("Documentation", overrides);

        Assert.Equal("Google", effective.Provider);
        Assert.Equal("gemini-3-flash", effective.Model);
        Assert.Equal("AIza-google-key", effective.ApiKey);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai", effective.Endpoint);
    }

    [Fact]
    public void Resolve_Same_Provider_Override_Does_Not_Switch()
    {
        // Story overrides model to a different Claude model — should NOT re-resolve keys
        var factory = CreateFactory();
        var overrides = new StoryModelOverrides
        {
            PlanningModel = "claude-opus-4-20250514"
        };
        var effective = factory.ResolveEffectiveOptions("Planning", overrides);

        Assert.Equal("Claude", effective.Provider);
        Assert.Equal("claude-opus-4-20250514", effective.Model);
        Assert.Equal("sk-ant-test-key", effective.ApiKey); // unchanged
    }

    [Fact]
    public void Resolve_Falls_Back_When_ProviderKeys_Missing()
    {
        // No ProviderKeys configured — should still override model but keep old key
        var factory = CreateFactory();
        var overrides = new StoryModelOverrides
        {
            CodingModel = "gpt-4o"
        };
        var effective = factory.ResolveEffectiveOptions("Coding", overrides);

        Assert.Equal("OpenAI", effective.Provider);
        Assert.Equal("gpt-4o", effective.Model);
        Assert.Equal("sk-ant-test-key", effective.ApiKey); // fallback — will fail at runtime, warning logged
    }

    [Fact]
    public void Resolve_Rewrites_Deprecated_Claude_Model_To_Alias()
    {
        var factory = CreateFactory();
        var overrides = new StoryModelOverrides
        {
            TestingModel = "claude-3-5-haiku-20241022"
        };

        var effective = factory.ResolveEffectiveOptions("Testing", overrides);

        Assert.Equal("Claude", effective.Provider);
        Assert.Equal("claude-3-5-haiku-latest", effective.Model);
    }

    // ── ResolveEffectiveOptions — tier + auto-detect ─────────────────

    [Fact]
    public void Resolve_Tier_Then_AutoDetect()
    {
        var options = new AIOptions
        {
            Provider = "Claude",
            Model = "claude-sonnet-4-20250514",
            ApiKey = "sk-ant-test-key",
            ModelTiers = new()
            {
                ["Economy"] = new()
                {
                    ["Testing"] = new AgentAIProfile { Model = "gpt-5-mini" }
                }
            },
            ProviderKeys = new()
            {
                ["OpenAI"] = new ProviderKeyConfig { ApiKey = "sk-openai-key" }
            }
        };

        var factory = CreateFactory(options);
        var overrides = new StoryModelOverrides { ModelTier = "Economy" };
        var effective = factory.ResolveEffectiveOptions("Testing", overrides);

        Assert.Equal("OpenAI", effective.Provider); // auto-detected from gpt-5-mini
        Assert.Equal("gpt-5-mini", effective.Model);
        Assert.Equal("sk-openai-key", effective.ApiKey);
    }

    // ── Resolution priority chain ───────────────────────────────────

    [Fact]
    public void Resolve_StoryPerAgent_Beats_Tier_Beats_Config()
    {
        var options = new AIOptions
        {
            Provider = "Claude",
            Model = "claude-sonnet-4-20250514",
            ApiKey = "sk-ant-test-key",
            AgentModels = new()
            {
                ["Coding"] = new AgentAIProfile { Model = "gpt-4.1" }
            },
            ModelTiers = new()
            {
                ["Premium"] = new()
                {
                    ["Coding"] = new AgentAIProfile { Model = "gpt-5.1-codex" }
                }
            },
            ProviderKeys = new()
            {
                ["OpenAI"] = new ProviderKeyConfig { ApiKey = "sk-openai-key" }
            }
        };

        var factory = CreateFactory(options);

        // Story has both tier AND per-agent override — per-agent wins
        var overrides = new StoryModelOverrides
        {
            ModelTier = "Premium",
            CodingModel = "claude-opus-4-20250514"
        };
        var effective = factory.ResolveEffectiveOptions("Coding", overrides);

        Assert.Equal("Claude", effective.Provider);
        Assert.Equal("claude-opus-4-20250514", effective.Model);
        Assert.Equal("sk-ant-test-key", effective.ApiKey); // Claude key, not OpenAI
    }
}
