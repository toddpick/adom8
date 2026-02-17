# Planning Analysis for US-92

## Story Overview

**ID:** US-92  
**Title:** US-92: Move health status indicators from header to left sidebar  
**State:** Story Planning  
**Created:** 2026-02-17

### Description
<p>Move the ADO, Queue, AI, Config, and Git status indicators currently in the dashboard header down into the left sidebar. The sidebar has plenty of room under the queue section. This declutters the header and makes better use of sidebar space. </p>

<h3>Technical Details </h3>
<p>The dashboard is a single-file SPA at <code>dashboard/index.html</code> (~4100 lines). All CSS, JS, and HTML are in this one file. </p>

<h4>Current Location (to remove from): </h4>
<ul>
<li>HTML: <code>div.nav-health#nav-health</code> inside <code>div.top-nav</code> (around lines 2314-2358) </li>
<li>Contains 5 <code>div.nav-health-item</code> elements with IDs: nh-ado, nh-queue, nh-ai, nh-config, nh-git </li>
<li>Each has: <code>span.nav-health-dot</code> (colored circle), <code>span.nav-health-label</code> (text), <code>div.nav-health-tooltip</code> (hover popup) </li>
<li>Plus poison message counter: <code>span#nav-health-poison</code> </li>
</ul>

<h4>Target Location (to move to): </h4>
<ul>
<li>HTML: Inside <code>aside.sidebar-left</code> (line ~2402), below the existing Totals stats section (div.sidebar-stats, lines ~2412-2443) </li>
<li>Add a new section with header &quot;System Health&quot; matching the sidebar-stats styling pattern </li>
</ul>

<h4>CSS Classes Involved: </h4>
<ul>
<li>Current header styles to repurpose: .nav-health, .nav-health-item, .nav-health-dot, .nav-health-label, .nav-health-tooltip, .nav-health-poison (lines ~335-500) </li>
<li>Dot status classes: .healthy (#4caf50), .degraded (#ff9800), .unhealthy (#f44336), .unknown (#666) </li>
<li>Sidebar styles to match: .sidebar-stats, .sidebar-header, .sidebar-stat-item, .sidebar-stat-label (lines ~504-545) </li>
<li>Layout will need to change from horizontal (flex-row in header) to vertical (flex-column in sidebar) </li>
</ul>

<h4>JavaScript (no logic changes needed): </h4>
<ul>
<li>fetchHealth() - fetches /api/health every 60s (line ~3814) </li>
<li>updateHealthPanel(data) - updates DOM via document.querySelectorAll('.nav-health-item') (line ~3825) </li>
<li>These query by CSS class, so moving the HTML elements preserves JS functionality as long as class names stay the same </li>
</ul>

<h4>Responsive Behavior: </h4>
<ul>
<li>Existing mobile breakpoint at 900px hides .sidebar-left entirely (line ~901) </li>
<li>Health indicators follow the same collapse behavior - no special mobile handling needed </li>
</ul>

<h3>Acceptance Criteria </h3>
<ul>
<li>All 5 status indicators (ADO, Queue, AI, Config, Git) removed from header </li>
<li>Status indicators added to left sidebar below the Totals/queue section </li>
<li>Status indicators retain same styling (green/red dots with labels) </li>
<li>Header is cleaner with more space </li>
<li>Hover tooltips still work </li>
<li>Health polling unchanged (60s interval, /api/health endpoint) </li>
</ul>

### Acceptance Criteria
<ol>
<li>All 5 health status indicators (ADO, Queue, AI, Config, Git) are removed from the top navigation bar (the div.nav-health#nav-health container inside div.top-nav). </li>
<li>A new &quot;System Health&quot; section is added to the left sidebar (aside.sidebar-left) below the existing &quot;Totals&quot; stats section (div.sidebar-stats). </li>
<li>Each indicator retains the same styling: 8px colored dot (.nav-health-dot) with status classes: healthy (green #4caf50), degraded (orange #ff9800), unhealthy (red #f44336), unknown (gray #666). </li>
<li>Each indicator retains its label (ADO, Queue, AI, Config, Git) and hover tooltip showing status + detail text. </li>
<li>The poison message counter (#nav-health-poison) moves with the health indicators to the sidebar. </li>
<li>The top navigation bar is visually cleaner with only the logo, project name, dark mode toggle, and emergency stop button remaining. </li>
<li>The health data source and polling interval remain unchanged: fetches from /api/health every 60 seconds via the existing fetchHealth() and updateHealthPanel() JavaScript functions. </li>
<li>Responsive: on mobile/tablet (existing breakpoint at 900px), the sidebar collapses as it does today - health indicators follow the same responsive behavior as the Queue and Totals sections. </li>
</ol>

---

## Technical Analysis

### Problem Analysis
This is a UI refactoring task to move health status indicators from the dashboard header to the left sidebar. The story involves relocating 5 health indicators (ADO, Queue, AI, Config, Git) and a poison message counter from the top navigation to a new 'System Health' section in the sidebar. The change improves header cleanliness and better utilizes sidebar space. All existing functionality (polling, tooltips, styling) must be preserved.

### Recommended Approach
1. Remove the existing div.nav-health container from div.top-nav (lines 2314-2358). 2. Create a new 'System Health' section in aside.sidebar-left below the Totals section, following the sidebar-stats styling pattern. 3. Move all 5 health indicator elements (nh-ado, nh-queue, nh-ai, nh-config, nh-git) and the poison counter to the new sidebar location. 4. Adapt CSS from horizontal (flex-row) to vertical (flex-column) layout while preserving dot colors, labels, and tooltips. 5. Ensure JavaScript functions (fetchHealth, updateHealthPanel) continue working by maintaining CSS class names. 6. Verify responsive behavior follows existing sidebar collapse at 900px breakpoint.

### Affected Files

- `dashboard/index.html`


### Complexity Estimate
**Story Points:** 5

### Architecture Considerations
Single-file SPA modification. The dashboard is entirely contained in dashboard/index.html (~4100 lines) with inline CSS, HTML, and JavaScript. No backend changes required - only DOM restructuring and CSS layout adjustments. The existing health polling mechanism (60s interval to /api/health) and JavaScript update logic remain unchanged.

---

## Implementation Plan

### Sub-Tasks

1. Remove div.nav-health#nav-health container from div.top-nav header section

2. Create new 'System Health' section in aside.sidebar-left below existing Totals section

3. Move all 5 health indicator elements (nh-ado, nh-queue, nh-ai, nh-config, nh-git) to new sidebar location

4. Move poison message counter span#nav-health-poison to sidebar

5. Update CSS layout from horizontal (flex-row) to vertical (flex-column) for sidebar placement

6. Preserve all existing styling: dot colors (.healthy, .degraded, .unhealthy, .unknown), labels, and tooltips

7. Test that fetchHealth() and updateHealthPanel() JavaScript functions continue working

8. Verify responsive behavior at 900px breakpoint (sidebar collapse)

9. Validate visual appearance matches sidebar styling patterns (sidebar-stats, sidebar-header)


### Dependencies


- No external dependencies - self-contained dashboard modification



---

## Risk Assessment

### Identified Risks

- Breaking existing JavaScript functionality if CSS selectors change

- Visual inconsistency if sidebar styling patterns aren't followed correctly

- Mobile responsive behavior could be affected if breakpoint handling changes


---

## Assumptions Made

- The dashboard/index.html file structure matches the described line numbers (~2314-2358 for header, ~2402+ for sidebar)

- Existing JavaScript uses CSS class selectors (.nav-health-item) that will be preserved

- The sidebar has sufficient vertical space for 5 health indicators plus poison counter

- Current responsive breakpoint at 900px for sidebar collapse should be maintained


---

## Testing Strategy
1. Visual verification: Confirm health indicators appear in sidebar with proper styling and layout. 2. Functional testing: Verify all 5 indicators show correct status colors and hover tooltips work. 3. JavaScript testing: Confirm fetchHealth() polling continues and updateHealthPanel() updates the moved elements. 4. Responsive testing: Test sidebar collapse behavior at 900px breakpoint on mobile/tablet. 5. Cross-browser testing: Verify layout works in major browsers. 6. Header verification: Confirm header is cleaner with only logo, project name, dark mode toggle, and emergency stop button remaining.

---

*Generated by Planning Agent*  
*Timestamp: 2026-02-17T19:09:30.9827942Z*
