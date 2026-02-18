# Developer Guidelines

Comprehensive guide for human developers working alongside AI agents in this codebase. Whether you're using Claude Code CLI, OpenAI Codex CLI, Cursor, GitHub Copilot, or coding manually — these guidelines ensure your code integrates seamlessly with AI-generated code.

---

> **💡 Quick Setup — If you have GitHub Copilot:** We highly recommend clicking the **"Scan & Document Codebase"** button on the ADOm8 dashboard instead of manually running the setup scripts below. This creates a work item that flows through the AI pipeline, where GitHub Copilot Coding Agent handles the heavy scanning and documentation work in its own environment — no timeout issues, even for large codebases. The manual script approach below is the fallback for teams without Copilot.

---

## 1. Working with AI-Generated Code

This codebase is **partially AI-generated**. Autonomous AI agents (Planning, Coding, Testing, Review, Documentation, Deployment) process Azure DevOps work items and produce code, tests, and documentation. Human developers also contribute — especially for complex features, architecture changes, and integrations.

### Why Consistency Matters

AI agents rely on established patterns to understand the codebase. When they encounter inconsistent code, they may:
- Misinterpret intent and generate incompatible code
- Duplicate functionality that already exists under a different name
- Break existing patterns when extending features

The `.agent/` folder is the **source of truth** for patterns and conventions. Both AI agents and human developers should reference it.

### The Golden Rule

> Code should look the same regardless of whether a human or AI wrote it. Follow the patterns in `.agent/` and the conventions below.

---

## 2. Before You Start Coding

### Required Reading (15 minutes)

| Step | File | Why |
|------|------|-----|
| 1 | `.agent/CONTEXT_INDEX.md` | Master overview — architecture, tech stack, key patterns |
| 2 | `.agent/FEATURES/{area}.md` | Deep dive into the area you're modifying |
| 3 | `.agent/CODING_STANDARDS.md` | Naming, DI, error handling, testing conventions |
| 4 | Azure DevOps completed stories | See how AI agents approached similar work |

### Check the Board

Before starting, check Azure DevOps for:
- **Active AI work** — ensure no agent is currently modifying files you plan to touch
- **Similar completed stories** — study the AI's approach in `.ado/stories/US-{id}/`
- **Existing plans** — a PlanningAgent may have already analyzed your feature

---

## 3. Using AI Coding Tools (Claude Code / Codex / Cursor / Copilot)

### The Problem

AI coding tools don't automatically know your codebase conventions. Without context, they generate generic code that may not match your patterns.

### The Solution

Always include `.agent/` context in your prompts. Use the helper script or structure prompts manually.

### Helper Script (Recommended)

```bash
# Automatically loads .agent/ context and detects available CLI
./scripts/ai-with-context.sh "add OAuth2 authentication to the API"

# Force a specific CLI
./scripts/ai-with-context.sh --tool codex "add OAuth2 authentication to the API"
./scripts/ai-with-context.sh --tool claude "add OAuth2 authentication to the API"
```

### Manual Prompt Structure

When not using the helper script, structure prompts like this:

```
Task: {what you're building}

Context:
- Review .agent/CONTEXT_INDEX.md
- Review .agent/FEATURES/{relevant-feature}.md
- Review .agent/CODING_STANDARDS.md

Requirements:
- Follow patterns documented above
- Match existing code style
- Use established interfaces/patterns
- Add tests with 80%+ coverage
- Document decisions

Constraints:
- Must implement I{Feature} interface if extending existing feature
- Use dependency injection patterns from other services
- Follow naming conventions from CODING_STANDARDS.md

Proceed with implementation.
```

---

## 4. Coding Standards Reference

### File Organization

```
src/
├── AIAgents.Core/              # Shared library (no Azure Functions dependency)
│   ├── Configuration/          # IOptions<T> classes (one per config section)
│   ├── Interfaces/             # All contracts (I{Service}.cs)
│   ├── Models/                 # DTOs, records, value objects
│   ├── Services/               # Interface implementations
│   └── Templates/              # Scriban .template.md files
├── AIAgents.Functions/         # Azure Functions app
│   ├── Agents/                 # Agent service implementations
│   ├── Functions/              # HTTP/Queue trigger functions
│   ├── Models/                 # Function-specific DTOs
│   └── Services/               # Function-specific services
├── AIAgents.Core.Tests/        # Core unit tests
└── AIAgents.Functions.Tests/   # Functions unit tests
```

### Naming Conventions

| Element | Convention | Example |
|---------|-----------|---------|
| Interfaces | `I` prefix + PascalCase | `IAIClient`, `IStoryContext` |
| Implementations | PascalCase, matches interface | `AIClient`, `StoryContext` |
| Agent services | `{Name}AgentService` | `PlanningAgentService` |
| Configuration | `{Feature}Options` | `DeploymentOptions`, `GitOptions` |
| Models | PascalCase, descriptive | `StoryState`, `AgentTask`, `CodeReviewResult` |
| Test classes | `{ClassUnderTest}Tests` | `AIClientTests`, `StoryContextTests` |
| Test methods | `Method_Scenario_ExpectedResult` | `ExecuteAsync_HappyPath_CompletesSuccessfully` |
| Config sections | Matches class constant | `DeploymentOptions.SectionName = "Deployment"` |

#### GOOD vs BAD

```csharp
// ✅ GOOD — follows conventions
public sealed class OAuth2AuthProvider : IAuthProvider
{
    private readonly ILogger<OAuth2AuthProvider> _logger;
    
    public OAuth2AuthProvider(ILogger<OAuth2AuthProvider> logger)
    {
        _logger = logger;
    }
}

// ❌ BAD — inconsistent naming, no interface, no DI
public class AuthHandler
{
    private static Logger log = new Logger();
    
    public AuthHandler() { }
}
```

### Dependency Injection Patterns

All services use constructor injection. Registration happens in `Program.cs`:

```csharp
// ✅ GOOD — standard singleton registration
services.AddSingleton<IMyService, MyService>();

// ✅ GOOD — keyed registration (for agent dispatch)
services.AddKeyedScoped<IAgentService, MyAgentService>("MyAgent");

// ✅ GOOD — configuration binding
services.Configure<MyOptions>(configuration.GetSection(MyOptions.SectionName));

// ❌ BAD — service locator, static access, manual construction
var service = new MyService(config["key"]);
ServiceLocator.Get<IMyService>();
```

### Interface-First Design

Every service has a matching interface in `AIAgents.Core/Interfaces/`:

```csharp
// ✅ GOOD — interface in Core, implementation can be in Core or Functions
public interface IMyService
{
    Task<Result> DoWorkAsync(int id, CancellationToken cancellationToken = default);
}

// Implementation
public sealed class MyService : IMyService { ... }
```

Existing interfaces to know about:

| Interface | Purpose |
|-----------|---------|
| `IAIClient` | AI completion (system prompt + user prompt → response) |
| `IAIClientFactory` | Per-agent AI client with model overrides |
| `IAzureDevOpsClient` | Work item CRUD, state transitions |
| `IGitOperations` | Branch, commit, push, read/write files |
| `IRepositoryProvider` | PR creation/merge (GitHub or Azure DevOps) |
| `IStoryContext` | State persistence per story (`.ado/stories/US-{id}/`) |
| `ITemplateEngine` | Scriban template rendering |
| `IAgentTaskQueue` | Queue next agent task |
| `IActivityLogger` | Azure Table activity logging |
| `ICodebaseContextProvider` | Load `.agent/` docs for AI prompts |
| `IAgentService` | Agent execution contract (keyed DI) |

### Error Handling

```csharp
// ✅ GOOD — structured logging, let exceptions propagate for retry
_logger.LogInformation("Starting {Agent} for WI-{WorkItemId}", "Planning", task.WorkItemId);

try
{
    var result = await _aiClient.CompleteAsync(systemPrompt, userPrompt, cancellationToken: ct);
    // ...
}
catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
{
    _logger.LogWarning(ex, "Rate limited for WI-{WorkItemId}, will retry", task.WorkItemId);
    throw; // Queue retry handles this
}

// ❌ BAD — swallowing exceptions, Console.WriteLine, generic catch
try { DoWork(); }
catch (Exception) { Console.WriteLine("error"); }
```

### Testing Requirements

- **Framework:** xUnit + Moq + coverlet.collector
- **Coverage target:** 80%+ per service
- **Test naming:** `Method_Scenario_ExpectedResult`
- **Mock pattern:** Constructor-injected mocks, `NullLogger<T>` for logging

```csharp
// ✅ GOOD — follows project test patterns
public sealed class MyServiceTests
{
    private readonly Mock<IDependency> _depMock = new();
    
    [Fact]
    public async Task DoWorkAsync_ValidInput_ReturnsExpectedResult()
    {
        _depMock.Setup(d => d.GetAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Item { Id = 42 });
        
        var sut = new MyService(_depMock.Object, NullLogger<MyService>.Instance);
        
        var result = await sut.DoWorkAsync(42);
        
        Assert.NotNull(result);
        Assert.Equal(42, result.Id);
    }
}
```

### Documentation Requirements

```csharp
// ✅ GOOD — XML docs on public members, references to story/decisions
/// <summary>
/// Implements OAuth2 with PKCE for enhanced security.
/// See US-145 and .ado/stories/US-145/DECISIONS.md for context.
/// </summary>
public sealed class OAuth2AuthProvider : IAuthProvider
{
    /// <summary>
    /// Authenticates a user using the OAuth2 authorization code flow with PKCE.
    /// </summary>
    /// <param name="authCode">The authorization code from the OAuth2 redirect.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An authenticated user session, or null if authentication failed.</returns>
    public async Task<UserSession?> AuthenticateAsync(
        string authCode,
        CancellationToken cancellationToken = default) { ... }
}

// ❌ BAD — no docs, AI can't infer intent
public class AuthHandler
{
    public async Task<object> Login(string code) { ... }
}
```

---

## 5. Working on Complex Features

### Workflow

#### Step 1: Create a User Story in Azure DevOps

Even if you're coding the feature yourself, create a story:

```
Title: Add OAuth2 authentication
Autonomy Level: 1 (Plan Only)
```

Set autonomy to **Plan Only** — the PlanningAgent will analyze the story and create a technical plan. Review the AI-generated `PLAN.md` in `.ado/stories/US-{id}/`. It often catches edge cases and identifies dependencies you might miss.

#### Step 2: Code Following the Plan

Use the AI plan as a guide while coding:

```bash
# Read the AI plan
cat .ado/stories/US-{id}/PLAN.md

# Code with AI CLI (auto-detects Claude Code or Codex)
./scripts/ai-with-context.sh "implement OAuth2 per PLAN.md in .ado/stories/US-{id}/"

# Or code manually following the plan
```

#### Step 3: Document Your Decisions

As you code, record decisions in `.ado/stories/US-{id}/DECISIONS.md`:

```markdown
# Decision: Use PKCE over implicit flow

**Agent:** Human (developer name)
**Rationale:** PKCE provides better security for public clients.  
Authorization code + PKCE is now the recommended OAuth2 flow per RFC 7636.
**Alternatives Considered:**
- Implicit flow (deprecated, less secure)
- Client credentials (server-to-server only, not applicable)
```

Future AI agents will read these decisions to understand **why** you made choices.

#### Step 4: Update `.agent/` Documentation

After completing your feature, update the relevant documentation:

```bash
# Add or update feature documentation
# File: .agent/FEATURES/authentication.md

# Include:
# - What you implemented
# - Key interfaces and classes
# - Configuration required
# - Code examples
# - Testing approach
```

The next AI agent working in this area will use YOUR documentation as reference.

---

## 6. Common Pitfalls

### ❌ Don't: Ignore Existing Patterns

```csharp
// Codebase uses IAuthProvider interface
// You create:
public class AuthHandler  // Wrong — doesn't implement interface
{
    public bool CheckAuth(string token) { ... }  // Wrong — sync, no CancellationToken
}
```

**Problem:** Pattern inconsistency breaks AI agent expectations. The next agent looking for authentication implementations will search for `IAuthProvider` and miss your code.

### ✅ Do: Follow Established Patterns

```csharp
// Match existing providers: SamlAuthProvider, LocalAuthProvider
public sealed class OAuth2AuthProvider : IAuthProvider
{
    public async Task<AuthResult> AuthenticateAsync(
        string credential,
        CancellationToken cancellationToken = default) { ... }
}
```

AI agents recognize this pattern and can extend it.

---

### ❌ Don't: Use Different Frameworks

```csharp
// Codebase uses xUnit, you add NUnit
[TestFixture]
public class AuthTests
{
    [Test]
    public void TestLogin() { ... }
}
```

**Problem:** Inconsistent test structure, build conflicts, confusion for AI agents.

### ✅ Do: Use Existing Frameworks

```csharp
// Match project convention: xUnit + Moq
public sealed class OAuth2AuthProviderTests
{
    [Fact]
    public async Task AuthenticateAsync_ValidCode_ReturnsSession() { ... }
    
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task AuthenticateAsync_InvalidCode_ReturnsNull(string? code) { ... }
}
```

Consistency > personal preference.

---

### ❌ Don't: Skip Documentation

```csharp
public class Svc
{
    public async Task<object> Run(Dictionary<string, object> p) { ... }
}
```

**Problem:** AI can't infer intent from code alone. Cryptic names and missing docs mean the next agent (or developer) wastes time reverse-engineering your code.

### ✅ Do: Document Intent

```csharp
/// <summary>
/// Implements OAuth2 with PKCE for enhanced security.
/// See US-145 and .ado/stories/US-145/DECISIONS.md for context.
/// </summary>
public sealed class OAuth2AuthProvider : IAuthProvider
{
    /// <inheritdoc />
    public async Task<AuthResult> AuthenticateAsync(
        string credential,
        CancellationToken cancellationToken = default)
    {
        // credential contains the OAuth2 authorization code
        // Exchange it for tokens via PKCE flow
        ...
    }
}
```

---

## 7. Working Alongside AI Agents

### Coordination

1. **Check Azure DevOps board** before starting work — see what agents are processing
2. **Avoid file conflicts** — if an agent is working on `src/Services/AuthService.cs`, wait or work on different files
3. **If potential conflict:** finish first, commit, push — AI agents adapt to your changes on next run
4. **Git workflow is identical** for humans and AI:
   - Feature branches (`feature/US-{id}`)
   - Pull requests with description
   - Merge to `main` after review

### Story State Machine

AI agents advance work items through these states:

```
Story Planning → AI Code → AI Test → AI Review → AI Docs → AI Deployment → Ready for QA
```

If you set **Autonomy Level 1 (Plan Only)**, the story stops after `Story Planning` with a plan for you to follow.

---

## 8. Code Review for AI-Generated PRs

### What to Check

- [ ] Follows `.agent/CODING_STANDARDS.md`?
- [ ] Matches existing patterns? (interfaces, DI, naming)
- [ ] Documentation adequate? (XML comments on public members)
- [ ] Tests comprehensive? (80%+ coverage, edge cases)
- [ ] References user story in comments?
- [ ] No hardcoded values? (uses `IOptions<T>` configuration)

### What NOT to Worry About

- **"AI code looks different"** — if it follows standards, accept it. Style differences are fine.
- **"AI didn't solve it my way"** — multiple valid approaches exist. Judge by correctness, not preference.
- **"AI used more abstractions"** — AI agents tend to create interfaces and patterns for extensibility. This is usually a feature, not a bug.

---

## 9. When to Use AI vs Code Yourself

### Let AI Handle (Full Autonomy or Auto-Merge)

| Task | Why |
|------|-----|
| Bug fixes | AI excels at isolated, well-defined fixes |
| UI tweaks | Low risk, easily verified |
| Adding tests | AI generates comprehensive test coverage |
| Documentation | AI writes consistent, thorough docs |
| Refactoring | AI follows patterns precisely |
| CRUD operations | Repetitive, pattern-following work |

### Code Yourself (Plan Only or Skip AI)

| Task | Why |
|------|-----|
| Architecture changes | Requires holistic understanding |
| New integrations | Third-party APIs need human judgment |
| Performance optimization | Requires profiling and benchmarking |
| Security-critical code | Needs human security review |
| Complex algorithms | Domain expertise required |
| Database migrations | Risk of data loss |

### Hybrid (AI Plans, You Code)

| Task | Why |
|------|-----|
| Medium-complexity features | AI plan catches edge cases, you implement |
| API changes | AI plans contract, you handle migration |
| Payment processing | AI designs architecture, you handle PCI compliance |
| Third-party integrations | AI plans interfaces, you handle vendor specifics |

---

## 10. Quick Reference Card

### Before Coding

```
□ Read .agent/CONTEXT_INDEX.md (5 min overview)
□ Read .agent/FEATURES/{area}.md (relevant feature docs)
□ Read .agent/CODING_STANDARDS.md (conventions)
□ Check Azure DevOps board (no agent conflicts)
□ Create user story (even for manual work)
□ Review existing patterns in similar code
```

### While Coding

```
□ Implement interfaces (I{Feature} pattern)
□ Use constructor DI (register in Program.cs)
□ Add XML comments on all public members
□ Use structured logging (ILogger<T>)
□ Follow naming: Method_Scenario_Result for tests
□ Write tests alongside code (80%+ coverage)
□ Record decisions in .ado/stories/US-{id}/DECISIONS.md
```

### After Coding

```
□ Update .agent/FEATURES/{area}.md with your additions
□ Ensure all tests pass (dotnet test src/AIAgents.sln)
□ PR description references user story
□ Request review from team
□ Verify no pattern inconsistencies
```

### AI CLI Pattern (Claude Code / Codex)

```bash
# Quick — use helper script (auto-detects Claude Code or Codex)
./scripts/ai-with-context.sh "your task description"

# Manual — include context in prompt
# 1. Reference .agent/CONTEXT_INDEX.md
# 2. Reference .agent/FEATURES/{area}.md
# 3. Reference .agent/CODING_STANDARDS.md
# 4. Specify your task clearly
# 5. Require tests and documentation
```

---

## Further Reading

- [SETUP.md](SETUP.md) — Infrastructure and deployment setup
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) — Diagnosis, common issues, and emergency procedures
- [DEMO_GUIDE.md](DEMO_GUIDE.md) — Live demo walkthrough
- [.agent/README.md](.agent/README.md) — AI documentation folder explained
- [scripts/README.md](scripts/README.md) — Helper scripts reference
- [src/AIAgents.Core.Tests/README.md](src/AIAgents.Core.Tests/README.md) — Core test documentation
- [src/AIAgents.Functions.Tests/README.md](src/AIAgents.Functions.Tests/README.md) — Functions test documentation
