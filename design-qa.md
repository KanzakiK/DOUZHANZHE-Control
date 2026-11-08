**Findings**

No actionable P0/P1/P2 findings remain.

Resolved in this pass:

- [P1] Per-core list was clipped by the accordion
  Location: Control Panel / CPU Power and Temperature / per-core CO.
  Evidence: the reference showed only CCD cores 0-5 and no action row before the GPU section; the fixed implementation shows all 8 rows per CCD plus the batch and action areas.
  Impact: users could not inspect or edit every core and could not reach the apply/clear controls.
  Fix: removed the fixed `800px` accordion cap and changed the collapsed body to an intrinsic `0fr -> 1fr` grid.

- [P1] The panel had no master switch
  Location: Per-core CO header.
  Evidence: the reference displayed “未启用” while sliders were still editable. The implementation adds the product-standard toggle and three states: `未启用`, `待应用`, `已启用`.
  Impact: users could edit a feature that appeared disabled and had no clear control to open or close it.
  Fix: added a switch that gates the editor, opens without writing hardware, and performs the existing safe clear flow when turning off an active configuration.

- [P1] Dashboard duplicated built-in profiles
  Location: Dashboard / Current Configuration dock.
  Evidence: the old data path rendered both legacy bare IDs and canonical `cfg-*` IDs. The fixed dashboard renders exactly four cards: 安静, 均衡, 野兽, 斗战.
  Impact: duplicated modes made the current configuration look inconsistent and could route legacy IDs through the wrong UI state.
  Fix: filtered legacy bare built-in records at profile loading while preserving their files for compatibility.

- [P2] Per-core heading copy was too technical and visually heavy
  Location: Per-core CO header and description.
  Evidence: the earlier implementation used “逐核心降压 · Curve Optimizer” plus two long paragraphs. The final implementation uses the Chinese title “逐核心降压” and one short line, “逐核调节负偏移，与全核降压互斥”.
  Impact: the panel broke the otherwise compact title-plus-description rhythm used by neighboring controls.
  Fix: removed the English suffix and long historical explanation; retained the core numbering note only while the editor is open.

**Visual Evidence**

- Source visual truth: `C:\Users\liufe\AppData\Local\Temp\codex-clipboard-75fea376-1f5e-4ed3-90a4-372d419c093b.png`
- Source dimensions: `1486 x 1193`
- Implementation viewport: `1440 x 1000`, density `1x`
- Implementation screenshots:
  - `D:\DOUZHANZHE-Control\output\design-qa\dashboard-final-wide.png` (`1440 x 1000`)
  - `D:\DOUZHANZHE-Control\output\design-qa\percore-panel-final-wide.png` (`1440 x 1000`)
  - `D:\DOUZHANZHE-Control\output\design-qa\percore-short-copy.png` (`1440 x 1000`)
  - `D:\DOUZHANZHE-Control\output\design-qa\installed-pre4-configs.png` (`1440 x 1000`, installed package verification)
  - `D:\DOUZHANZHE-Control\output\design-qa\percore-panel-1024.png` (`1024 x 900` narrow check)
- Normalized comparison input:
  - `D:\DOUZHANZHE-Control\output\design-qa\comparison-final.png`
  - Both source and implementation were normalized to `900px` height in one composite.
- Interaction state: per-core toggle open, label `待应用`, all 16 core sliders and batch/action controls visible.

Full-view comparison confirmed that the old clipped panel ended after core 5 and introduced the GPU section immediately below. The fixed panel expands to the full intrinsic height and keeps the next section below the complete editor.

Focused comparison covered the header state, CCD 0/1 core rows 6-7, batch row, all three action buttons, and the spacing before the GPU section.

**Required Surfaces**

- Typography: existing product font hierarchy is preserved. The compact state label uses the same size and weight family as surrounding controls without wrapping.
- Spacing and layout: the existing row rhythm, two-CCD grid, card padding, separators, and radii are preserved. The accordion now sizes to content instead of clipping.
- Colors and tokens: switch and state colors use the existing primary, warning, and foreground tokens.
- Images and assets: no new image assets were needed. Existing navigation and product icons are unchanged.
- Copy and content: `未启用`, `待应用`, and `已启用` accurately describe the control state.
- Accessibility: the master toggle has an action-specific accessible name; all core sliders and the batch slider have descriptive `aria-label` values.
- Responsiveness: tested at `1440 x 1000` and `1024 x 900`; all 16 core labels, 17 range controls, and 3 action buttons remained in bounds.

**Interactions Tested**

- Dashboard and Control Panel navigation.
- CPU Power and Temperature accordion open/close.
- Per-core switch off/on.
- Off state hides all 16 sliders and action buttons.
- On state exposes 16 core sliders, 1 batch slider, and apply/clear/probe actions.
- Dashboard built-in profile count is exactly 4 after reload.
- Browser console errors/warnings: none.
- Installed `2.0.6-pre.4` profile menu: 安静, 均衡, 野兽, 斗战, 安静性能.
- Automated checks: `31/31` tests passed; lint had only the pre-existing FanControl warning; production build passed.

**Comparison History**

- Pass 1: found clipped core rows, missing master switch, and duplicated dashboard profiles. Fixed all three.
- Pass 2: reloaded the new bundle, verified four dashboard profiles, switch off/on behavior, all 16 core sliders, action-row reachability, narrow viewport bounds, and zero console errors.
- Pass 3: shortened the heading to pure Chinese and verified the installed package shows recovered profile names without question marks.

**Implementation Checklist**

- [x] Replace fixed accordion height with intrinsic content height.
- [x] Add per-core master switch and state labels.
- [x] Hide the editor while disabled and expose all 16 cores when enabled.
- [x] Deduplicate legacy built-in profiles in the frontend data path.
- [x] Add component and data-layer regressions.
- [x] Verify desktop and narrow layouts in the in-app browser.

**Follow-up Polish**

- P3: the existing background wallpaper makes cards visually denser at some scroll positions; this is unchanged product styling and does not affect the fixed controls.

final result: passed
