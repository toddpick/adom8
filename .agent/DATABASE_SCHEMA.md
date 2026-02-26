# Database Schema

ADOm8 uses **Azure Table Storage** (NoSQL key-value store) — no relational database. There are two Table Storage tables: one for the activity log and one for Copilot delegation tracking. Story state is persisted in the Git repository as JSON files (not in a database).

## Azure Table Storage Tables

### 1. AgentActivity Table

**Purpose**: Stores agent activity log entries for the dashboard live feed.
**Managed by**: `src/AIAgents.Functions/Services/TableStorageActivityLogger.cs`

| Column | Type | Description |
|--------|------|-------------|
| `PartitionKey` | string | `"activity"` for log entries; `"meta"` for feed metadata |
| `RowKey` | string | Inverted tick count (`DateTime.MaxValue.Ticks - now.Ticks`) for reverse-chronological ordering |
| `Agent` | string | Agent name (e.g., `"Planning"`, `"Coding"`) |
| `WorkItemId` | int | ADO work item ID (0 for standalone operations) |
| `Message` | string | Human-readable activity message |
| `Level` | string | `"info"`, `"warning"`, or `"error"` |
| `Timestamp_Utc` | string | ISO-8601 UTC timestamp |
| `Tokens` | int | Token count for this activity (0 if not applicable) |
| `Cost` | double | Estimated cost in USD for this activity |

**Special row** (PartitionKey = `"meta"`, RowKey = `"feed"`):
| Column | Type | Description |
|--------|------|-------------|
| `FeedClearedAfterUtc` | string | ISO-8601 timestamp; entries at or before this time are filtered from the live feed |

**Query pattern**: Queries filter by `PartitionKey eq 'activity'`, sorted by RowKey (which is an inverted tick count, so rows are naturally in reverse-chronological order).

**Infrastructure**: Provisioned via Terraform in `infrastructure/storage.tf` as `azurerm_storage_table.activity_log`.

---

### 2. CopilotDelegations Table

**Purpose**: Tracks active GitHub Copilot coding delegations (pipeline paused waiting for Copilot).
**Managed by**: `src/AIAgents.Functions/Services/CopilotDelegationService.cs`

| Column | Type | Description |
|--------|------|-------------|
| `PartitionKey` | string | `"delegation"` |
| `RowKey` | string | GitHub Issue number (as string) |
| `WorkItemId` | int | ADO work item ID |
| `BranchName` | string | Feature branch (e.g., `"feature/US-110"`) |
| `DelegatedAt` | string | ISO-8601 UTC timestamp of delegation |
| `Status` | string | `"active"`, `"completed"`, or `"timed-out"` |

---

## Git-Based State Storage

Story execution state is NOT in a database — it lives in the Git repository as JSON files. This provides version history, branch isolation, and zero additional infrastructure:

**Path**: `.ado/stories/US-{workItemId}/state.json`
**Branch**: `feature/US-{workItemId}` (AI-owned feature branch)

### state.json Schema

```json
{
  "workItemId": 110,
  "currentState": "AI Code",
  "createdAt": "2026-02-26T05:51:20Z",
  "updatedAt": "2026-02-26T06:00:00Z",
  "agents": {
    "Planning": {
      "status": "completed",
      "startedAt": "2026-02-26T05:51:30Z",
      "completedAt": "2026-02-26T05:54:00Z",
      "additionalData": {
        "triageResult": "approved",
        "readinessScore": 95
      }
    },
    "Coding": {
      "status": "in_progress",
      "startedAt": "2026-02-26T05:54:10Z"
    }
  },
  "artifacts": {
    "codePaths": ["src/MyFeature/MyService.cs"],
    "testPaths": ["src/MyFeature.Tests/MyServiceTests.cs"],
    "docPaths": [".ado/stories/US-110/DOCUMENTATION.md"]
  },
  "tokenUsage": {
    "agents": {
      "Planning": {
        "inputTokens": 8456,
        "outputTokens": 1234,
        "estimatedCost": 0.043
      }
    }
  },
  "decisions": [
    {
      "agent": "Planning",
      "decisionText": "Estimated complexity: 5 story points",
      "rationale": "Small focused change with clear requirements"
    }
  ]
}
```

### State Schema Reference

`src/AIAgents.Core/Models/StoryState.cs` — the C# model for state.json
`src/AIAgents.Core/Templates/state.schema.json` — JSON schema for validation

## Connection Management

All Azure Table Storage connections use the `AzureWebJobsStorage` connection string (same connection used by Azure Functions for queue storage). No separate database connection string is needed:

```csharp
var connectionString = configuration["AzureWebJobsStorage"];
_tableClient = new TableClient(connectionString, TableName);
_tableClient.CreateIfNotExists(); // Idempotent — safe to call on every startup
```

## ORM Patterns

No ORM is used. Azure Table Storage entities are accessed directly via `Azure.Data.Tables.TableClient`:

```csharp
// Write
var entity = new TableEntity(partitionKey, rowKey)
{
    ["Agent"] = "Planning",
    ["WorkItemId"] = 110,
    ["Message"] = "Planning agent completed"
};
await _tableClient.AddEntityAsync(entity, ct);

// Read
var response = await _tableClient.GetEntityIfExistsAsync<TableEntity>(partitionKey, rowKey, ct);
if (response.HasValue)
{
    var agent = response.Value!.GetString("Agent");
}

// Query
var query = _tableClient.QueryAsync<TableEntity>(
    filter: $"PartitionKey eq 'activity'",
    maxPerPage: 50,
    cancellationToken: ct);
await foreach (var entity in query) { ... }
```

## Infrastructure

Azure Storage resources are provisioned via Terraform (`infrastructure/storage.tf`):
- **Storage account**: `azurerm_storage_account.functions`
- **Queue**: `agent-tasks` (primary work queue)
- **Queue**: `agent-tasks-poison` (dead-letter queue)
- **Table**: `activitylog` (activity log entries)
- **Blob container**: `temp-repos` (temporary git clone storage)
