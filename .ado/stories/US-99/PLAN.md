# Planning Analysis for US-99

## Story Overview

**ID:** US-99  
**Title:** Move Lock and Codebase Initialized controls next to Provision ADO  
**State:** Story Planning  
**Created:** 2026-02-19

### Description
<div>As an operator, I want the Lock button and Codebase Initialized control next to Provision ADO on the Dashboard so controls are visible, grouped, and not blocking actions. </div><div><br> </div><div>Additional requirement: fix story card header to show US-99: Move Lock and Codebase Initialized controls next to Provision ADO (not US-99: US-99). </div><div><br> </div><div>Supporting visual references for implementation: </div><div>- Screenshot 2026-02-19 021100.jpg (AttachedFile) </div><div>- image.png (pasted image in description) </div>

### Acceptance Criteria
<div><b>Acceptance Criteria</b> </div><ul><li>In the top nav header controls area, place controls in this exact order: Provision ADO, then Lock, then Codebase Initialized. </li><li>Do not change control behavior: Provision ADO still calls provisioning, Lock still opens function key prompt, Codebase Initialized still opens initialize/re-analyze flow. </li><li>Keep existing visual styles for all three controls; this story changes position/grouping only. </li><li>Desktop breakpoint: at 1024px width and above, all three controls render on one row with no overlap; each control remains fully clickable. </li><li>Mobile/tablet breakpoint: below 1024px, controls may wrap, but order remains Provision ADO -&gt; Lock -&gt; Codebase Initialized and no click-target overlap occurs (minimum 8px horizontal gap when on same row). </li><li>Story header rendering rule: show US-99: Move Lock and Codebase Initialized controls next to Provision ADO when title is available; never show US-99: US-99. </li><li>Implementation and validation must reference supporting files: Screenshot 2026-02-19 021100.jpg and image.png. </li> </ul>

---

## Technical Analysis

### Problem Analysis
This is a UI/UX repositioning task for the Dashboard. The story requests moving three existing controls (Lock button, Codebase Initialized control) to be positioned next to the Provision ADO control in a specific order. Additionally, there's a bug fix needed for story card header rendering to prevent duplicate ID display (US-99: US-99 → US-99: Move Lock...). The story includes visual references and specific responsive design requirements for desktop (1024px+) and mobile breakpoints.

### Recommended Approach
WARNING: This implementation plan assumes the Dashboard is implemented in the single-file SPA at `dashboard/index.html`. The approach would involve: 1) Locate the current header controls section in the HTML/JavaScript, 2) Identify the three controls (Provision ADO, Lock, Codebase Initialized) and their current positioning, 3) Restructure the DOM to place them in the specified order with proper CSS for responsive behavior, 4) Fix the story header rendering logic to prevent ID duplication, 5) Test responsive behavior at the 1024px breakpoint, 6) Validate against the provided screenshot references. Implementation requires CSS Grid or Flexbox for responsive layout with proper gap spacing and click target accessibility.

### Affected Files

- `dashboard/index.html`


### Complexity Estimate
**Story Points:** 5

### Architecture Considerations
Frontend-only change to the single-file SPA Dashboard. No backend API changes required. The implementation involves DOM restructuring, CSS responsive design updates, and JavaScript logic fixes for header rendering. Changes are isolated to the presentation layer with no impact on Azure Functions or core services.

---

## Implementation Plan

### Sub-Tasks

1. Analyze current Dashboard HTML structure to locate header controls area

2. Identify existing Provision ADO, Lock, and Codebase Initialized control implementations

3. Restructure DOM to place controls in specified order (Provision ADO → Lock → Codebase Initialized)

4. Implement responsive CSS for desktop (1024px+) single-row layout with no overlap

5. Implement mobile/tablet CSS for proper wrapping with 8px minimum gaps

6. Fix story header rendering logic to show 'US-99: Move Lock...' instead of 'US-99: US-99'

7. Validate implementation against Screenshot 2026-02-19 021100.jpg and image.png

8. Test responsive behavior across breakpoints

9. Verify all controls maintain existing functionality (no behavior changes)

10. Ensure click targets remain fully accessible with no overlap


### Dependencies


- Access to Dashboard source code in dashboard/index.html

- Supporting visual reference files (.ado/stories/US-99/documents/)

- Understanding of current control implementation and styling



---

## Risk Assessment

### Identified Risks

- Single-file SPA may have complex interdependencies that make control repositioning difficult

- Responsive design changes could break existing mobile/tablet layouts

- Control repositioning might affect existing CSS selectors or JavaScript event handlers

- Visual references may not provide sufficient detail for exact implementation requirements


---

## Assumptions Made

- Dashboard is implemented as a single-file SPA in dashboard/index.html

- The three controls (Provision ADO, Lock, Codebase Initialized) already exist in the current implementation

- Controls are currently positioned in a different order or location than specified

- Supporting screenshot files contain clear visual guidance for the desired layout

- Current control functionality and styling should remain unchanged (position-only change)


---

## Testing Strategy
Manual testing approach: 1) Visual validation against provided screenshots at multiple screen sizes, 2) Responsive testing at 1024px breakpoint and below to verify wrapping behavior, 3) Click target testing to ensure no overlap and proper 8px gaps, 4) Functional testing to verify all three controls maintain existing behavior (Provision ADO calls provisioning, Lock opens function key prompt, Codebase Initialized opens initialize/re-analyze flow), 5) Story header rendering validation to confirm US-99 prefix displays correctly, 6) Cross-browser compatibility testing for layout consistency.

---

*Generated by Planning Agent*  
*Timestamp: 2026-02-19T09:26:56.5233881Z*
