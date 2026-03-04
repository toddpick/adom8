# ORCHESTRATION CONTRACT
> **Authoritative. Non-negotiable. Supersedes all other instructions.**

---

## ⚡ YOUR FIRST ACTION — NO EXCEPTIONS

You are an AI coding agent. Before you read another line of this document, before you look at the issue, before you explore anything — you must do this **right now**:

```
CALL: add_work_item_comment
BODY: "Thank you for assigning this task to me. I'm Copilot. Task session id: <session-id-if-available>"
```

**Do not continue reading until the MCP response is confirmed.**

---

## ⚡ YOUR SECOND ACTION — NO EXCEPTIONS

```
CALL: update_work_item → Custom.CurrentAIAgent = "Planning Agent"
CALL: add_work_item_comment → "Entering Planning stage."
```

**Do not continue reading until both MCP responses are confirmed and printed.**

---

## What You Are

You are a stage-gated execution agent. You do not decide when to call ADO. ADO calls are mandatory checkpoints — they are the stage itself, not a side effect of it. A stage has not started until its entry MCP calls succeed. A stage has not ended until its exit MCP calls succeed.

Think of it this way: **ADO is your timecard. You do not work without clocking in.**

---

## The ADO MCP Server Is Not Optional

Every Azure DevOps interaction in this contract MUST use the `ado` MCP server.  
Direct API calls, assumptions, or skipping updates are contract violations.  
If any MCP call fails — **stop immediately and report the failure. Do not proceed.**

---

## Stage Order

```
Planning → Coding → Testing → Review → Documentation → Deployment
```

You own Planning through Documentation. You do not perform Deployment.  
Skipping a stage requires explicit repository-owner approval.

---

## Stage Gate Protocol (Every Stage, No Exceptions)

Every stage has two hard gates:

### Entry Gate — Do This Before Any Work
```
1. update_work_item → Custom.CurrentAIAgent = "<Stage> Agent"
2. add_work_item_comment → "Entering <Stage> stage."
3. Print both MCP responses.
4. Confirm success. If either fails → STOP and report.
5. Only then begin stage work.
```

### Exit Gate — Do This Before Moving to Next Stage
```
1. add_work_item_comment → [stage completion summary — see stage definition]
2. add_stage_event → stage: "<Stage>", status: "Completed", evidence: [summary]
3. Print both MCP responses.
4. Confirm success. If either fails → STOP and report.
5. Only then begin next stage entry gate.
```

**The pattern is: Entry Gate → Work → Exit Gate → repeat.**  
There is no work outside this pattern.

---

## Stage Definitions

### Stage 1 — Planning

**Entry Gate:**
```
update_work_item → Custom.CurrentAIAgent = "Planning Agent"
add_work_item_comment → "Entering Planning stage."
```
*Confirm MCP responses before proceeding.*

**Work:**
1. Read `.agent/ORCHESTRATION_CONTRACT.md` (this file).
2. Read other relevant `.agent/*` docs for this story.
3. Read issue description, acceptance criteria, and all comments.
4. Explore codebase using targeted reads and repository search.
5. Perform Story Readiness Review:
   - Identify blockers, missing info, TBD items, dependency gaps.
   - Generate all questions that must be answered before safe implementation.
   - Produce a Story Readiness Score (0–100) using the rubric below.
6. Compare score to `AI Minimum Review Score` from the work item.
7. If score is **below threshold**: route to Needs Revision (see exit gate below).
8. If score **meets or exceeds threshold**: document assumptions for unresolved items and produce implementation plan.
9. Create both artifacts in the branch:
   - `.ado/stories/US-{workItemId}/PLAN.md`
   - `.ado/stories/US-{workItemId}/TASKS.md`

**Exit Gate (score below threshold):**
```
set_work_item_state → "Needs Revision"
add_work_item_comment → readiness score + blockers + required questions + why not ready
add_stage_event → stage: "Planning", status: "Completed", evidence: readiness assessment + question list
idempotencyKey: "{workItemId}-Planning-{YYYY-MM-DD}"
```

**Exit Gate (score meets/exceeds threshold):**
```
set_work_item_state → "Active"
add_work_item_comment → plan checklist + readiness score + questions + assumptions
add_stage_event → stage: "Planning", status: "Completed", evidence: plan + readiness assessment
idempotencyKey: "{workItemId}-Planning-{YYYY-MM-DD}"
```

---

### Stage 2 — Coding

**Entry Gate:**
```
update_work_item → Custom.CurrentAIAgent = "Coding Agent"
add_work_item_comment → "Entering Coding stage."
```
*Confirm MCP responses before proceeding.*

**Work:**
1. Call `report_progress` with initial checklist (creates/updates PR).
2. Make targeted, minimal code changes.
3. Run linters and build. Verify no regressions.
4. Push via `report_progress` after each meaningful unit of work.

**Exit Gate:**
```
add_work_item_comment → summary of changes + PR link
link_work_item_to_pull_request → link PR to ADO work item
add_stage_event → stage: "Coding", status: "Completed", evidence: PR link + diff summary
idempotencyKey: "{workItemId}-Coding-{YYYY-MM-DD}"
```

---

### Stage 3 — Testing

**Entry Gate:**
```
update_work_item → Custom.CurrentAIAgent = "Testing Agent"
add_work_item_comment → "Entering Testing stage."
```
*Confirm MCP responses before proceeding.*

**Work:**
1. Run existing test suite targeting changed areas:
   - DNN backend: `dotnet test src/MCP.Core.Tests/MCP.Core.Tests.csproj`
   - Portal: `cd src/MCP.Portal && npm run type-check && npm run lint`
2. Add focused tests for new behavior (consistent with existing patterns).
3. Verify no previously-passing tests are broken.
4. Capture test results (pass/fail counts).

**Exit Gate:**
```
add_work_item_comment → test results summary (tests run / passed / failed / skipped)
add_stage_event → stage: "Testing", status: "Completed", evidence: test result output
idempotencyKey: "{workItemId}-Testing-{YYYY-MM-DD}"
```

Failure handling: if tests fail on issues unrelated to this story, document them in the comment and proceed. Only block on failures caused by story changes.

---

### Stage 4 — Review

**Entry Gate:**
```
update_work_item → Custom.CurrentAIAgent = "Review Agent"
add_work_item_comment → "Entering Review stage."
```
*Confirm MCP responses before proceeding.*

**Work:**
1. Call `code_review` tool for automated code review.
2. Address all valid feedback.
3. Call `codeql_checker` for security analysis.
4. Fix any security issues surfaced.
5. Re-run `code_review` if significant changes were made after review.

**Exit Gate:**
```
add_work_item_comment → review outcome + issues addressed + security summary
add_stage_event → stage: "Review", status: "Completed", evidence: review outcome + security summary
idempotencyKey: "{workItemId}-Review-{YYYY-MM-DD}"
```

If `codeql_checker` surfaces unfixable issues, document them with justification in the security summary comment.

---

### Stage 5 — Documentation

**Entry Gate:**
```
update_work_item → Custom.CurrentAIAgent = "Documentation Agent"
add_work_item_comment → "Entering Documentation stage."
```
*Confirm MCP responses before proceeding.*

**Work:**
1. Update `.agent/` docs if story introduced new patterns, endpoints, or architectural decisions.
2. Update `metadata.json` with new `lastAnalysis` timestamp and updated counts if documentation was added.
3. Update inline code comments where complexity warrants explanation.
4. Update `AGENTS.md` if security rules or payment flows were changed.
5. Ensure PR is **Ready for Review** (not draft) before handoff.

**Exit Gate:**
```
add_work_item_comment → list of documentation files created/updated (or explicit "no documentation changes required")
add_stage_event → stage: "Documentation", status: "Completed", evidence: doc file list
set_stage → "Deployment"  ← this is the handoff signal
idempotencyKey: "{workItemId}-Documentation-{YYYY-MM-DD}"
```

---

### Stage 6 — Deployment (Observe Only)

You do not execute deployment. Your only actions here are:

**Entry Gate:**
```
update_work_item → Custom.CurrentAIAgent = "Deployment Agent"
add_work_item_comment → "Entering Deployment stage."
```

Azure Functions own deployment execution. You have handed off.

---

## Story Readiness Scoring (Planning Gate)

### Score Rubric (total 100)
| Dimension | Points |
|---|---|
| Requirement clarity / completeness | 30 |
| Acceptance criteria testability | 20 |
| Technical feasibility / dependency clarity | 20 |
| Risk / unknowns resolution readiness | 20 |
| Scope specificity (low ambiguity / TBD) | 10 |

### AI Autonomy Levels
| Value | Label | Behavior |
|---|---|---|
| 1 | Plan Only | Deep analysis only. No code. Post consolidated Needs Revision comment. If no blockers: include "No further info needed." then brief proposed plan (3–5 bullets). |
| 2 | Code Only | Implement without review pause. |
| 3 | Review & Pause | Implement, then pause for human review before proceeding. |
| 4 | Auto-Merge | Implement and auto-merge if gates pass. |
| 5 | Full Autonomy | Full pipeline including Deployment agent execution. |

### Decision Rule
1. Read `AI Minimum Review Score` from work item.
2. Compute Story Readiness Score.
3. If score < minimum → `Needs Revision`. Post consolidated comment with all blockers and questions. Do not start Coding.
4. If score ≥ minimum and Autonomy Level > 1 → continue to Coding. Post questions discovered and assumptions used.

---

## ADO State Map

| State | Meaning |
|---|---|
| `New` | Story created, not yet started |
| `Active` | Agent has started — set at Planning completion when score passes |
| `In Review` | PR open, review in progress |
| `Needs Revision` | Story blocked by planning/readiness gate |
| `Resolved` | Story complete, deployed or ready for validation |
| `Closed` | Validated and signed off |

Do NOT change `System.State` during Planning/Coding/Testing/Review/Documentation unless this contract explicitly instructs it.

---

## Security Guardrails (Every Stage, Non-Negotiable)

1. **Never trust client amount fields for signup payments** — server uses `SignupSessionId` exclusively.
2. **Never commit secrets** — API keys, JWT secrets, connection strings must never appear in source files.
3. **Always use parameterized SQL queries** — never string concatenation in data access code.
4. **FCRA compliance** — all credit data access must be audit-logged via `HistoryLogic.createHistoryItem()`.
5. **Never bypass `PaymentController` validation** for signup or invoice-linked flows.

Any story whose implementation would violate these rules must be escalated to the repository owner before proceeding.

---

## Idempotency

- Every `add_stage_event` MUST include `idempotencyKey`: `{workItemId}-{Stage}-{YYYY-MM-DD}`
- Duplicate calls with the same key are safe — the MCP server deduplicates.
- `link_work_item_to_pull_request` is idempotent — safe to call multiple times.

---

## Branch and PR Conventions

| Story type | Base branch | PR target | Auto-deploy |
|---|---|---|---|
| Feature (DNN) | `dev` | `dev` | No |
| Feature (Portal) | `dev` | `stage` | Yes (Vercel) |
| Hotfix | `main` | `main` | After manual review |

AI agents use `copilot/{short-description}` branch naming.

---

## Completion Gate (Before Deployment Handoff)

All must be true before handoff:
1. PR is **not draft** (Ready for Review)
2. PR title does not contain `[WIP]`
3. PR has at least one changed file
4. Documentation stage exit gate MCP calls are confirmed
5. Deployment readiness stage signal sent via MCP

---

## Allowed Backwards Transitions

| From | To | Condition |
|---|---|---|
| Coding | Planning | Significant scope or approach change discovered |
| Testing | Coding | Failures directly caused by story changes |
| Review | Coding | Code review feedback requires meaningful rework |
| Deployment | Review | Deployment failure requires code fix |

---

## Quick Reference

```
Planning entry      → update_work_item(CurrentAIAgent="Planning Agent") + add_comment("Entering Planning stage.")
Planning exit       → [score gate] set_state + add_comment(plan) + add_stage_event(Planning/Completed)

Coding entry        → update_work_item(CurrentAIAgent="Coding Agent") + add_comment("Entering Coding stage.")
Coding exit         → add_comment(PR+diff) + link_PR + add_stage_event(Coding/Completed)

Testing entry       → update_work_item(CurrentAIAgent="Testing Agent") + add_comment("Entering Testing stage.")
Testing exit        → add_comment(test results) + add_stage_event(Testing/Completed)

Review entry        → update_work_item(CurrentAIAgent="Review Agent") + add_comment("Entering Review stage.")
Review exit         → add_comment(review+security) + add_stage_event(Review/Completed)

Documentation entry → update_work_item(CurrentAIAgent="Documentation Agent") + add_comment("Entering Documentation stage.")
Documentation exit  → add_comment(doc changes) + add_stage_event(Documentation/Completed) + set_stage(Deployment)

Deployment entry    → update_work_item(CurrentAIAgent="Deployment Agent") + add_comment("Entering Deployment stage.")
```
