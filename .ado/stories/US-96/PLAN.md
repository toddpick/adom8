# Planning Analysis for US-96

## Story Overview

**ID:** US-96  
**Title:** Fix Dashboard Progress for Skipped Testing Agent  
**State:** Story Planning  
**Created:** 2026-02-18

### Description
<div><span>Dashboard should show 100% and COMPLETED when Testing agent is skipped for Copilot path<br></span> </div><div><div><br> </div><div>Description:<br> </div><div>As a user viewing the dashboard, when the Testing agent is skipped (because GitHub Copilot Coding Agent handles testing), I want the dashboard to show 100% progress and COMPLETED status, and the Testing step should display &quot;Skipped – Testing done by GitHub Coding Agent&quot; instead of remaining in PENDING.<br> </div><div><br> </div><div><br> </div><div>Technical Notes:<br> </div><div><br> </div><div>All changes are in dashboard/index.html (single-file SPA)<br> </div><div>calculateStoryProgress must count skipped agents as completed in the denominator/numerator<br> </div><div>getStoryStatus must treat skipped same as completed when determining overall story status<br> </div><div>formatStatusText should map 'skipped' → 'Skipped – Testing done by GitHub Coding Agent'<br> </div><span>Previous fix attempt may not have fully resolved this — verify both the card-level and detail-level progress indicators</span><br> </div>

### Acceptance Criteria
<div><div style="box-sizing:border-box;">&nbsp;When Testing agent status is skipped, the progress bar shows 100% (not 83%) when all other agents are completed<br style="box-sizing:border-box;"> </div><div style="box-sizing:border-box;">&nbsp;Story status badge shows COMPLETED (not PENDING) when all non-skipped agents have completed<br style="box-sizing:border-box;"> </div><div style="box-sizing:border-box;">&nbsp;The Testing agent step displays status text: &quot;Skipped – Testing done by GitHub Coding Agent&quot; instead of generic &quot;Skipped&quot;<br style="box-sizing:border-box;"> </div><div style="box-sizing:border-box;">&nbsp;Both the story card progress AND the detail view progress reflect the corrected calculation<br style="box-sizing:border-box;"> </div><div style="box-sizing:border-box;">&nbsp;Skipped step visual treatment (icon, color, badge) clearly distinguishes it from pending or failed states<br style="box-sizing:border-box;"> </div><div style="box-sizing:border-box;">&nbsp;Works correctly in both light and dark mode<br style="box-sizing:border-box;"> </div><div style="box-sizing:border-box;">&nbsp;Fix applies in calculateStoryProgress, getStoryStatus, and formatStatusText functions in dashboard/index.html </div><br> </div>

---

## Technical Analysis

### Problem Analysis
The dashboard incorrectly calculates progress when the Testing agent is skipped in Copilot workflows. Currently shows 83% (5/6 agents) instead of 100% when all non-skipped agents complete. The issue spans three functions: calculateStoryProgress (denominator calculation), getStoryStatus (overall status determination), and formatStatusText (display text for skipped state). Both card-level and detail-view progress indicators need correction.

### Recommended Approach
Modify the single-file SPA dashboard/index.html to properly handle skipped agents in progress calculations. Update calculateStoryProgress to count skipped agents as completed in both numerator and denominator. Modify getStoryStatus to treat skipped same as completed for overall story status. Enhance formatStatusText to show specific message for Testing agent skip. Ensure visual treatment distinguishes skipped from pending/failed states in both light and dark modes.

### Affected Files

- `dashboard/index.html`


### Complexity Estimate
**Story Points:** 5

### Architecture Considerations
Single-file SPA modification affecting three JavaScript functions within the existing dashboard. No backend changes required. Changes are isolated to progress calculation logic and status display formatting.

---

## Implementation Plan

### Sub-Tasks

1. Update calculateStoryProgress function to count skipped agents as completed

2. Modify getStoryStatus function to treat skipped status same as completed

3. Enhance formatStatusText function to show specific message for skipped Testing agent

4. Verify visual treatment for skipped state works in both light and dark modes

5. Test both card-level and detail-view progress indicators

6. Validate fix works for all agent types when skipped


### Dependencies


- Existing dashboard/index.html structure and functions

- Current agent status data model (completed, pending, failed, skipped states)

- Light/dark mode CSS classes and styling



---

## Risk Assessment

### Identified Risks

- Breaking existing progress calculation for non-skipped scenarios

- Inconsistent visual treatment between card and detail views

- CSS styling issues in light/dark mode transitions

- Regression in other agent status displays


---

## Assumptions Made

- The 'skipped' status is already being set correctly by the backend for Testing agent in Copilot workflows

- The dashboard data model includes agent status information

- Visual styling classes for skipped state already exist or can be derived from existing states

- Only Testing agent can be skipped (based on the specific message requirement)


---

## Testing Strategy
Test with mock data containing skipped Testing agent: verify 100% progress display, COMPLETED status badge, correct status text 'Skipped – Testing done by GitHub Coding Agent', consistent behavior between card and detail views, proper visual treatment in both light and dark modes. Test edge cases: all agents completed, mix of completed/pending/skipped, only Testing agent skipped vs other agents skipped. Verify no regression in existing completed/pending/failed scenarios.

---

*Generated by Planning Agent*  
*Timestamp: 2026-02-18T05:11:00.6729551Z*
