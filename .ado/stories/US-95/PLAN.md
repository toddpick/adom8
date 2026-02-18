# Planning Analysis for US-95

## Story Overview

**ID:** US-95  
**Title:** Improve Planning Agent Feasibility Analysys  
**State:** Story Planning  
**Created:** 2026-02-18

### Description
<div><span>Planning Agent should flag unverified external API assumptions during triage</span> </div><span>Technical Notes:<br></span><div><br> </div><div>Modify system prompt in PlanningAgentService.cs — the 6 triage checks already include &quot;Feasibility&quot; but it's surface-level<br> </div><div>Infrastructure already exists via CodebaseContextLoader.LoadRelevantContextAsync() — no architectural changes needed<br> </div><div>Estimated cost increase: ~$0.05 → ~$0.07-0.10 per planning call (+3-8 seconds)<br> </div><span>Example scenario: a story asking to &quot;link to GitHub Copilot Agent Tab&quot; should flag that the task GUID isn't available via GitHub Issues API</span><div><div><br> </div><div>Description:<br> </div><span>As a developer, I want the Planning Agent to identify and flag unverified assumptions about external API capabilities so that stories requiring external integrations don't fail during coding due to undiscoverable APIs.</span><br> </div>

### Acceptance Criteria
<div><span>&nbsp;Planning Agent system prompt includes an &quot;Unverified Assumptions&quot; triage category<br></span><div>&nbsp;When a story references external APIs (GitHub, Azure DevOps, third-party), the planner flags any assumptions about data availability that can't be confirmed from codebase docs alone<br> </div><div>&nbsp;Triage response distinguishes questions (need human clarification) from researchNeeded (AI could investigate but hasn't verified)<br> </div><div>&nbsp;If unverified external dependencies are detected, the planner adds a warning note to the implementation plan rather than silently assuming feasibility<br> </div><div>&nbsp;No additional API calls required — improvement is prompt engineering within existing single Claude call<br> </div><span>&nbsp;Existing tests continue to pass</span><br> </div>

---

## Technical Analysis

### Problem Analysis
The Planning Agent currently performs surface-level feasibility checks that don't identify unverified assumptions about external API capabilities. This leads to stories proceeding to coding that later fail due to unavailable data or endpoints. The story requests enhancing the existing feasibility triage to flag external API assumptions that cannot be verified from codebase documentation alone.

### Recommended Approach
Enhance the PlanningAgentService system prompt to include explicit detection of external API assumptions. Modify the triage response structure to distinguish between 'questions' (need human clarification) and 'researchNeeded' (unverified external dependencies). Leverage existing CodebaseContextLoader to check for API documentation. No architectural changes required - this is prompt engineering within the existing single Claude call.

### Affected Files

- `src/AIAgents.Functions/Agents/PlanningAgentService.cs`

- `src/AIAgents.Core/Models/PlanningResult.cs`

- `src/AIAgents.Functions.Tests/Agents/PlanningAgentServiceTests.cs`


### Complexity Estimate
**Story Points:** 5

### Architecture Considerations
Prompt engineering enhancement within existing PlanningAgentService. Uses current CodebaseContextLoader infrastructure to provide context about available APIs. Maintains single AI call pattern with enhanced response structure to include assumption flagging.

---

## Implementation Plan

### Sub-Tasks

1. Update PlanningAgentService system prompt to include 'Unverified Assumptions' triage category

2. Modify PlanningResult model to include researchNeeded field alongside existing questions field

3. Enhance triage logic to detect external API references (GitHub, Azure DevOps, third-party services)

4. Add warning notes to implementation plans when unverified external dependencies are detected

5. Update unit tests to verify assumption detection functionality

6. Test with example scenarios (GitHub Copilot Agent Tab, Azure DevOps custom fields, etc.)


### Dependencies


- Existing CodebaseContextLoader.LoadRelevantContextAsync() method

- Current PlanningAgentService system prompt structure

- PlanningResult JSON response format



---

## Risk Assessment

### Identified Risks

- False positives: flagging valid API assumptions that are actually documented

- False negatives: missing subtle external API dependencies

- Increased token usage ($0.05 → $0.07-0.10 per call) may impact cost at scale

- Additional processing time (3-8 seconds) may affect user experience


---

## Assumptions Made

- CodebaseContextLoader can provide sufficient context about available APIs

- Single Claude call can handle enhanced triage complexity without timeout

- Cost increase is acceptable for improved story quality

- Existing test suite covers PlanningAgentService adequately for extension


---

## Testing Strategy
Unit tests for PlanningAgentService with mock scenarios containing external API references. Test cases should include: GitHub API assumptions (task GUIDs, repository data), Azure DevOps API assumptions (custom fields, work item types), third-party API assumptions (webhooks, integrations). Verify that researchNeeded field is populated correctly and implementation plans include appropriate warnings. Test existing functionality remains unchanged.

---

*Generated by Planning Agent*  
*Timestamp: 2026-02-18T05:10:34.4444426Z*
