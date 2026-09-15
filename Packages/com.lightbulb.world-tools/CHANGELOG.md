# Changelog

## Unreleased

- Renamed Lighting Experiment to **Bakery LV3 Swapper**, preserving existing scene component identities and generated folder paths.
- File repairs share one original backup per asset; repeated operations reuse the original. A one-time automatic migration consolidates verified legacy backups and removes surplus copies. No cleanup window or menu command.
- UV Viewer uses temporary editor materials instead of generating Resources assets. The migration removes unchanged obsolete helpers only when no known references remain; ambiguous files and functional world assets are retained.

- Added **Mochie Baked Specular**: a reversible, source-checked Dominant Direction specular patch for Mochie Standard / Standard Lite, with opt-in reapplication after compatible updates and a build check for overwritten patches.
- Added a scene-material preview to enable Bakery Specular Highlights with Undo. Requires existing compatible baked data; preserves material strengths, bake modes, roughness and metallic. Recommends moderate scalar roughness and leaves textured/very smooth/rough cases for review instead of treating zero roughness or zero metallic as non-reflective.

## 0.1.16 - 2026-09-14

- Added **Crunch currently off only** to Scene Texture Crunch Compression. When enabling Crunch, excludes textures already configured for Crunch in Default or any enabled platform override, regardless of quality, and preserves their existing import settings.
- Preview explicitly labels proposed changes as not applied yet. Each platform has one state/quality line: enabling Crunch shows **OFF → ON** and the chosen quality, without presenting inactive stored quality as previously active compression. Quality changes to enabled Crunch show **stays ON** and the old/new qualities.
- Passed 19 texture-batch EditMode tests, including mixed 50/100-quality preservation, enabled versus disabled platform overrides, and preview state wording.

## 0.1.15 - 2026-09-14

- **Scene Texture Crunch Compression** now lists only textures with pending changes. Textures already matching the requested settings are summarized by count; skipped textures and their reasons are in a collapsed section. Shows a clear empty result when nothing needs changing.

## 0.1.14 - 2026-09-14

- Simplified **Find Unreferenced Disabled Objects** to show only eligible disabled objects, selected by default, in compact rows. Referenced objects and verbose per-object explanations are hidden. Genuine scan issues appear once above the results; incomplete scans remain blocked.
- Fixed video-player `VRCUrl` values and URL arrays incorrectly making reference scans incomplete. Inspects the native SDK URL wrapper's fields without treating ordinary URLs as unknown object-reference containers.
- Passed 15 scene-cleanup EditMode tests, including native SDK scalar/array URL cases, candidate-only results, referenced-object exclusion, and incomplete-scan protection.

## 0.1.13 - 2026-09-14

- Added **Scene Texture Crunch Compression**: scan active-scene texture dependencies, preview/exclude textures, enable Crunch with a 0–100 quality slider or disable it. Preserves resolution, updates supported Default/platform settings, backs up import metadata, and checks for stale references/settings.
- Added **Find Unreferenced Disabled Objects**: inspect disabled branches for external references to their objects or descendants, including serialized fields, UnityEvents, animation paths, Timeline bindings and live Udon variables. Mark selected candidates EditorOnly with Undo and original-tag restoration while the window remains open; no objects are deleted or scenes automatically saved.
- Reference discovery includes inactive objects, referenced prefab assets, terrain layers/prototypes, sprites and animation material swaps. Cleanup checks incoming references from all loaded ordinary scenes while changing only the active scene. Missing scripts or unsupported reference inspection block cleanup; runtime name/tag lookups remain a manual exclusion.
- Validated with 26 focused Unity 2022.3 EditMode tests, including native Timeline and VRChat SDK 3.10.5 Udon references, texture import round-trips, prefab/terrain discovery, exclusions, stale-preview refusal and Undo/tag restoration.

## 0.1.12 - 2026-09-14

- New Mochie primary packs keep metallic, roughness and occlusion channels unscaled and copy their separate-mode strengths into the packed-mode sliders. Zero strengths no longer erase source detail from the new packed image; slider adjustments remain available afterward.
- Materials with identical packing inputs can share an output despite different strengths, with independent sliders. Height/detail behavior and the two-distinct-source minimum are preserved; no Mochie shader or source files are changed.
- Existing packed textures and their slider values are retained during reference cleanup. This does not reconstruct channels from older strength-baked packs; use the original source maps to repack those materials.

## 0.1.11 - 2026-09-14

- Mochie scene packing now requires at least two distinct source textures per packed output. Primary and detail are checked independently; a single texture reused in multiple slots is skipped, with a preview note.
- Already-packed material cleanup still clears safe leftover source references without generating another texture. Skipped separate workflows retain all references and settings.

## 0.1.10 - 2026-09-13

- Removed the one-off **Consolidate Mochie Packed Maps in Scene** tool and its shortcut in the packer. Automatic sharing of matching outputs within each packing batch and cleanup of obsolete source references remain available.
- **Lighting Experiment** remains included and unchanged.

## 0.1.9 - 2026-09-12

- **Pack Mochie Materials in Scene** now clears separate primary/detail data-map references after successful packing, including reused packed outputs. Source texture files remain on disk; Undo restores the original references and settings.
- The same scan now offers cleanup of leftover references on already-packed scene materials without repacking. Preview distinguishes packing from cleanup. Missing packed textures or inactive packed keywords retain source references and report a note.
- Preserves packed height, detail strengths, normal/mask/albedo maps, and independent AreaLit settings. Cleanup validates the existing packed texture against preview changes and restores the material if any operation fails.

## 0.1.8 - 2026-09-12

- Added **Lighting Experiment**: keep paired Bakery and Light Volumes configurations in one scene, switch Bakery / Point LVs only / Hybrid, and control individual light participation without reconverting or discarding independent edits.
- Added point/spot and rectangular-area conversion, explicit force-point approximations, a native manager, and deterministic fitted regular volumes (up to 10). Saves a pre-experiment scene copy before conversion.
- Excludes inactive lights through intensity, shadow/probe bake flags, native activity and Udon state; preserves regular-volume Bakery helpers while excluding their objects from baking. Restores saved lightmap scales and assignments when returning to Bakery.
- Routes diffuse/specular and reflection participation for scene-local Mochie Standard / Standard Lite material copies. Includes explicit cleanup of either lighting system with Undo; source packages and baked texture files are retained.
- Requires the verified Light Volumes **3.0.0-dev.18** contract and installed Bakery. Documents the native LV/Mochie shadow-bake RenderTexture error reproduced independently of this tool. Full-world visual bake validation remains required.

## 0.1.7 - 2026-09-12

- Scene packing now creates one PNG per unique set of native packing inputs per batch and shares it across matching materials. Material identities and independent AreaLit occlusion settings are preserved. Source identities/import hashes, effective tiling/offset, baked strengths, map presence, and primary/detail mode determine sharing.
- Added **Consolidate Mochie Packed Maps in Scene** to replace duplicate packed-map references with a shared texture. Requires byte-identical PNG files and identical texture importer settings, including all platform overrides; uses a stable keeper path, selectable preview groups, stale-preview checks, and Undo. Material settings and all texture files are retained.

## 0.1.6 - 2026-09-12

- **Find Empty Material Maps** now scans only materials assigned to renderers, terrains, and skyboxes in the active scene, including inactive objects. Removal is limited to those materials, and switching scenes requires a new scan. Shared material assets still affect their other uses.
- Added **Pack Mochie Materials in Scene**, using the installed Mochie Standard v2.13 texture packer without modifying or bundling Mochie. Supports Standard / Standard Lite primary maps and optional Standard detail maps, preview/exclusion, unique output PNGs, cancellation, stale-preview checks, and material Undo.
- Keeps height/detail strengths from being applied twice during packing and disables blending for absent detail channels. Original source maps are retained; generated PNGs remain after Undo.

## 0.1.5 - 2026-09-12

- Added **Find Empty Material Maps** under **Tools > Lightbulb** to find uniform metallic, roughness/smoothness, AO, normal, height, and packed data maps across project materials.
- Full-resolution, all-channel scanning supports exact matching or a configurable fuzzy threshold (99% by default), without changing texture files or import settings.
- Preview every shared material reference, filter/select results, and remove one texture or all selected textures from material slots without deleting files. Removal supports Undo, checks for stale previews, and refuses partial removal when a referenced material cannot be edited.

## 0.1.4 - 2026-09-11

- Added **Resize Referenced Textures** to the material right-click menu and **Tools > Lightbulb**. Multi-select materials, preview and exclude textures, cap resolution, and optionally enable or disable Crunch.
- Handles existing platform overrides, backs up import settings, and reimports each shared texture once. Original images are untouched.

## 0.1.3 - 2026-09-05

- Added **Fix Mochie Linear Textures in Scene** for Mochie Standard and Standard Lite, matching their separate/packed workflow and height/detail warning rules.
- Scans the active scene including inactive renderers, all material slots, and terrain materials; fixes each shared texture once, with a confirmation, Console report, and original metadata backups.

## 0.1.2 - 2026-09-03

- Added **Fix VideoPlayerShim URL Resolver** under **Tools > Lightbulb**, with exact 1.5.0 source checks, recognized-repair no-ops, and recoverable backups.

## 0.1.1 - 2026-08-31

- Flattened every command directly under **Tools > Lightbulb**.

## 0.1.0 - 2026-08-31

- Added GPU instancing candidate reporting.
- Added baked lightmap texel-usage ranking.
- Added renderer and GameObject vertex-count rankings.
- Added Bunny83's MIT-licensed UV Viewer under the unified Lightbulb menu.
