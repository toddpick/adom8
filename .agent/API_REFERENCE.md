# API Reference

## Azure Functions HTTP Endpoints

### OrchestratorWebhook
```
POST /api/webhook?code={function-key}
```
Receives Azure DevOps Service Hook payloads when work item state changes.

**Request Body:** ADO Service Hook payload (JSON)
```json
{
  "eventType": "workitem.updated",
  "resource": {
    "workItemId": 67,
    "fields": {
      "System.State": { "oldValue": "New", "newValue": "Story Planning" }
    }
  }
}
```

**Response:** 200 OK with queued agent info, or 400/422 for validation failures.

**State → Agent Mapping:**
| State | Agent Triggered |
|-------|----------------|
| Story Planning | Planning |
| AI Code | Coding |
| AI Test | Testing |
| AI Review | Review |
| AI Docs | Documentation |
| AI Deployment | Deployment |

---

### HealthCheck
```
GET /api/health
```
Anonymous. Returns component health status (60-second cache).

**Response:**
```json
{
  "status": "healthy|degraded|unhealthy",
  "checks": {
    "azureDevOps": { "status": "healthy" },
    "storageQueue": { "status": "healthy" },
    "aiProvider": { "status": "healthy" },
    "gitRepository": { "status": "healthy" }
  },
  "version": "1.0.0",
  "environment": "Production"
}
```

---

### EmergencyStop
```
GET  /api/emergency-stop    # Returns queue depths
POST /api/emergency-stop    # Clears all queues
```
Anonymous. Used by dashboard for monitoring and abort.

**GET Response:**
```json
{
  "queueDepth": 0,
  "poisonQueueDepth": 1,
  "status": "monitoring"
}
```

---

### GetCurrentStatus
```
GET /api/status
```
Anonymous. Returns full pipeline status for dashboard.

**Response:** `DashboardStatus` with stories, agent statuses, stats, recent activity from Table Storage.

---

### CodebaseIntelligence
```
POST /api/analyze-codebase?code={key}   # Trigger scan
GET  /api/codebase-intelligence?code={key}  # Get metadata
```

**POST Response:**
```json
{
  "workItemId": 0,
  "estimatedDuration": "10-15 minutes",
  "status": "queued"
}
```

---

## Internal Service Interfaces

### IAIClient
```csharp
Task<AICompletionResult> CompleteAsync(
    string systemPrompt,
    string userPrompt,
    AICompletionOptions? options,
    CancellationToken ct);
```

### IAzureDevOpsClient
```csharp
Task<StoryWorkItem> GetWorkItemAsync(int id, CancellationToken ct);
Task UpdateWorkItemAsync(int id, Dictionary<string, object> fields, CancellationToken ct);
Task AddCommentAsync(int id, string comment, CancellationToken ct);
```

### IGitOperations
```csharp
Task<string> EnsureBranchAsync(string branchName, CancellationToken ct);
Task CommitAndPushAsync(string repoPath, string message, CancellationToken ct);
Task WriteFileAsync(string repoPath, string relativePath, string content, CancellationToken ct);
Task<string> ReadFileAsync(string repoPath, string relativePath, CancellationToken ct);
Task<IEnumerable<string>> ListFilesAsync(string repoPath, CancellationToken ct);
```

### IAgentService
```csharp
Task<AgentResult> ExecuteAsync(AgentTask task, CancellationToken ct);
```

### IRepositoryProvider
```csharp
Task<int> CreatePullRequestAsync(string sourceBranch, string targetBranch,
    string title, string description, CancellationToken ct);
```
