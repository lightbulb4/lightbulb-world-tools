# Changelog

## [0.2.2] - 2026-09-24

- Automatically apply UV1 and the new occlusion bake's atlas tiling/offset to selected receivers with unambiguous mappings, including when automatic texture assignment is disabled.
- Report shared-material conflicts and leave conflicting UV or texture settings unchanged. Never automatically create or assign material variants; conflict repair remains a manual audit action.

All notable changes to this package are documented here.

## [0.2.1] - 2026-09-07

### Fixed

- Clamp adjusted occlusion RGB to 0–1 in previews and HDR, EXR, and PNG output so HDR masks cannot amplify AreaLit emitter opacity and suppress neighboring lights.
- Apply the same clamp when publishing a bake with default brightness and contrast.
- Allow saving at default slider values to clamp an existing HDR map using the normal backup and restore workflow.

## [0.2.0] - 2026-09-06

### Added

- Automatic removal of terminal transaction staging scenes, proxy assets, isolated bake output, and recovery backups, with lock-safe retry and bounded diagnostic history.
- Separate `Tools > Lightbulb > AreaLit Configuration Audit` window for loaded-scene configuration checks.
- Checks for texture setup, receiver inputs, capture dependencies, potential emitter-budget pressure and projector policy/overlap.
- Confirmed, undoable texture-setting fixes, explicit capture-camera ordering, and grouped bulk material assignment/removal with fresh-scope validation.
- Explicit separation of configuration problems from appearance choices and runtime-unverified risks.
- Collapsed material lists, required Mesh/Texture 0 consistency, and a shared slot picker with apply-to-all/clear-all controls.
- Grouped TransparentFX emitter-layer fixes and player/mirror projector layer + ignore-mask presets, with explicit role choices when ambiguous, fresh-snapshot checks and Undo.

### Changed

- Moved manual occlusion-map assignment, tiling/offset tools, UV conflict repair and recovery into Configuration Audit. Baking, preparation and output adjustment stay in Occlusion Baker.
- Match/Reset now respect the same disabled-object scope as the audit and UV conflict repair.
- Unused optional slots are not findings; mixed assigned/empty slots are discrepancies, while different assigned optional textures are review choices.
- Findings use compact summaries with expandable detail. Mip bias is informational with an optional undoable Reset to 0 action.
- Removed finding filters, search, report export and routine completion banners. Stale results use a compact toolbar indicator; full-width titles and content-sized rows prevent cramped, overlapping text.
- Projector appearance guidance names the exact Specular Highlights checkbox and Color alpha control, with concise on/off and brightness tradeoffs rather than implying a required setting.

## [0.1.0] - 2026-08-31

### Added

- Transaction-owned AreaLit occlusion baking through Bakery.
- Automatic AreaLit emitter discovery and exact-geometry Bakery proxies.
- RGB channel packing, per-emitter intensity, and a global bake multiplier.
- Stable output publication with brightness and contrast adjustment.
- Receiver material assignment and independent occlusion UV tools.
- Shared-material UV conflict discovery, repair, and persistent recovery.
- Scene preparation and revert controls for inspecting a bake before it runs.
