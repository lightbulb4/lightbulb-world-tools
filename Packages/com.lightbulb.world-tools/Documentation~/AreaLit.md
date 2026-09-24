# AreaLit Occlusion Baker

Safely bake, inspect, adjust, and apply AreaLit occlusion maps with Bakery. Open the tool from **Tools > Lightbulb > AreaLit Occlusion Baker**.

General-purpose helpers have a separate entry: **Tools > Lightbulb > AreaLit Configuration Audit**. Both tools are included in Lightbulb World Tools. The existing occlusion workflow, asset GUIDs, assembly identity, and recovery paths are preserved.

## Configuration audit

Choose **Run audit** for a read-only scan of loaded original scenes. The **Include inactive objects and disabled components** option controls the audit and all its bulk/UV operations. AreaLit-disabled materials are not enabled or edited by the tool. The scan uses renderer shared materials and projector materials, not a project-wide search through unused assets.

The native Unity window shows all findings with short summaries and actions; expand a finding for technical detail. Titles use the full available width, with actions below. An out-of-date snapshot shows a warning icon and refresh glyph beside **Run audit**. Successful changes refresh the findings without a completion banner; errors and skipped-operation warnings remain visible.

### Shared material assignments

Use **Material assignments** to select Mesh, Texture 0–3+ or the occlusion map, choose a texture, then **Apply to all** or **Clear all**. Material lists start collapsed. A discrepancy's **Edit assignments** button selects its slot in this shared tool.

- **Mesh** and **Texture 0** must be assigned consistently on AreaLit-enabled receivers under this tool's scene convention. Missing or differing assignments are errors.
- **Texture 1**, **Texture 2**, **Texture 3+** and **AreaLit occlusion map** are optional. Entirely unused slots produce no finding, even when an emitter references them. Mixed assigned/empty slots are discrepancies; different non-empty textures are review choices, not errors.
- A single existing texture prefills the picker. Multiple existing textures require an explicit selection; the most common assignment is never assumed to be correct. An entirely empty slot also requires a texture selection.
- Bulk color-texture assignments exclude recursive emitter materials: their copied bounce input is deliberately different. Light Mesh consistency still includes those emitters. Occlusion assignment uses the `_AreaLitOcclusion` property on supported integrated shaders; it does not replace a native shader's general-purpose `_OcclusionMap` AO texture.
- The assignment tool also handles consistent or unused slots. Clearing a required slot is allowed only after a warning that this will produce audit errors.

Bulk actions change only the selected texture reference, preserving tiling, offset, UV selection and shader controls. They show the material count, confirm the shared-asset impact, and revalidate both the material list and assignments before writing. Every changed material must be an editable standalone native asset under `Assets`; a blocked material prevents a partial "apply to all." Undo is recorded and no assets or scenes are saved.

### Emitter and projector layers

The audit checks this project's two-projector convention:

| Object | GameObject layer | Affected receiver layers |
| --- | --- | --- |
| `AreaLit/LightMesh` emitter | TransparentFX | Not applicable |
| Player projector | Player | Player and PlayerLocal only |
| Mirror projector | MirrorReflection | MirrorReflection only |

Misplaced emitters form one finding with a grouped fix and a collapsed object list. Each projector fix sets its own layer and its **Ignore Layers** mask together, ignoring all other 32-bit layer entries, including unnamed ones. The player projector handles remote players in mirrors as well; the mirror projector handles the local mirror avatar. This follows VRChat's [documented avatar layers](https://creators.vrchat.com/worlds/layers/).

Role-bearing object names (`mirror`, `player` or `avatar`) take precedence; otherwise the object's layer and avatar-mask bits suggest its role. Conflicting or insufficient evidence offers **Use player setup** and **Use mirror setup** choices instead of a guessed fix. Custom world-lighting projectors should not use these avatar-only presets.

Layer fixes respect the audit's disabled-object scope. They recheck the affected objects, original layers, masks and inferred role after confirmation, record Undo and prefab overrides, and never save scenes. They change only the listed GameObjects, not their children. Moving an emitter also changes layer-based rendering, collisions and raycasts for other components on that same object. Camera masks and unrelated projector settings remain untouched. Missing VRChat layers, read-only scenes or an emitter and projector sharing one GameObject block the fix rather than partially applying it.

### Capture order and texture checks

The intended dependency is **emitter-data capture → indirect-bounce capture → player view**. Camera Depth sorts lower values first. If the two captures both use depth -10, **Set depth to -11** moves the data capture earlier, leaving the bounce and player cameras unchanged. This is not an instruction to render the player camera first.

The fix is offered only for a uniquely identified data camera. Bounce captures are inferred from orthographic, non-VertexLit render-texture cameras that can see an emitter with its standard indirect-light pass enabled. This also covers single-bounce setups without a recursive copy. Multiple data-camera owners require inspection. Runtime camera changes, custom render scheduling and copy timing still need validation in the client.

Other checks cover light-data dimensions, precision, stencil, multisampling and unnecessary mipmaps; lighting-color dimensions, mipmaps and filtering; emitter indices/topology and keyword mismatches; conservative emitter-budget pressure; and projector layer overlap and brightness/specular policy. Budget pressure is not measured GPU overflow, and overlapping layers do not prove double application.

**Projector appearance (optional)** is not a recommendation to enable specular. Select the listed projector material, then check or uncheck **Inspector > Forward Rendering Options > Specular Highlights** (not **Glossy Reflections**). On adds AreaLit highlights and GPU work, but may make matte avatar surfaces look glossy because the projector uses its own Smoothness/Metallic settings. Off keeps diffuse AreaLit lighting without those added highlights and specular work; the avatar's own specular settings are unchanged.

For baseline brightness, use **Inspector > AreaLit Projector > Color > A**. Lower alpha preserves more existing avatar brightness with less light/dark contrast; higher alpha darkens unlit areas and can make dim avatars too dark. The audit shows the current alpha in expanded instructions and approximate retained brightness at zero projector contribution. Color's RGB controls AreaLit intensity/tint. These remain manual appearance choices.

Nonzero **mip bias is informational only**: the compact row shows the value and an optional **Reset to 0** button for editable native textures. Negative bias may intentionally sharpen reflections. Reset records Undo; other fixes preserve the bias.

### Other fixes and safety

Editable native render textures can enable mipmaps plus automatic generation, disable unnecessary data mipmaps, or switch to trilinear filtering without resetting anisotropy. Mip allocation changes recreate a live buffer; its producer must populate it again. Imported images, subassets, package assets and temporary textures remain inspection-only for these settings.

There is no blanket "Fix All." Bulk assignment is an explicit choice for one property; projector layer presets also require confirmation. Brightness, specular settings and emitter capacity are not changed automatically. Scene/project changes, Inspector edits and Undo mark the snapshot stale. Scanning and fixes are blocked in Play Mode, during compilation/import, or during an occlusion transaction/staging scene. No startup or per-frame scene scan runs.

The audit needs neither Bakery nor AreaLit source code to compile. It does not modify third-party shaders or measure final avatar appearance, GPU timings, runtime texture contents or actual light-table occupancy.

## Requirements

- Unity 2022.3
- A VRChat Worlds 3.x project
- AreaLit for emitter and receiver shaders
- Bakery for scene preparation and baking
- Mochie shaders only when using the Mochie receiver workflow

The package still compiles when Bakery is absent. Baking and scene preparation are disabled with an explanatory warning, while output adjustment remains available in the baker and material setup/occlusion UV tools remain available in the Configuration Audit.

Generated maps, material variants, and transaction scenes are project-owned assets under `Assets/Lightbulb/AreaLitOcclusion`. Package upgrades never replace that folder.

Open **Tools > Lightbulb > AreaLit Occlusion Baker**.

The editor tool discovers loaded `AreaLit/LightMesh` emitters and materials with an `_AreaLitOcclusion` property. AreaLit emitters are the only user-facing occlusion sources. Normal Unity and Bakery lights are disabled in isolated staging scenes and are never repurposed.

Every automatic checkbox displays its reason:

- Active AreaLit emitters start checked when a safe Bakery proxy can be made. Inactive emitters start unchecked.
- A `MeshRenderer`/`MeshFilter` emitter always receives a transaction-owned proxy built from its exact submesh and transform. Unity's editor-only mesh access supports this even when the imported mesh has Read/Write disabled. Unsupported geometry stays unchecked rather than being approximated.
- Receiver materials appear when AreaLit is enabled, or when their compatible shader has no AreaLit toggle. Materials with AreaLit disabled stay hidden unless **Show materials with AreaLit disabled** is enabled.
- Manual changes are labeled as manually included or excluded.

## Safety model

An occlusion bake never changes lights in the user's scene. All loaded scenes must already be saved and clean. The tool then:

1. Writes a persistent transaction journal under `Library/AreaLitOcclusion`.
2. Saves copies of every loaded scene under this feature's `Transactions` folder.
3. Opens those copies, disables their normal lights, and clones every AreaLit emitter material before recoloring it.
4. Creates transaction-owned Bakery Light Mesh proxies for selected emitters using only the AreaLit emitter geometry, material intensity, and the tool's controlled defaults.
5. Forces Bakery to render into that transaction's isolated `BakeOutput` folder.
6. Restores the original scene setup before applying anything to shared materials.
7. Backs up every existing output texture and material file before changing it.
8. Publishes stable textures under `Generated/<scene GUID>` so scene renames and moves do not change the destination.

Existing texture publication and recovery release Unity's cached asset-file handles before atomically replacing only the image bytes. This keeps the `.meta` GUID stable while avoiding Windows file-sharing failures when the Project window or a loaded material still references the HDR.

If Unity or Bakery stops during the process, the next editor load offers recovery. Staging data is retained until recovery finishes. Completed, canceled, and successfully recovered transactions automatically remove their staging scenes, proxy assets, isolated bake output, and recovery backups. Cleanup only accepts a terminal journal whose transaction ID exactly matches its tool-owned folder; active or loaded staging scenes are never removed. Locked folders remain available for a later automatic retry. The ten newest lightweight diagnostic receipts stay under `Library/AreaLitOcclusion/History`; older receipts are pruned after their asset folders are gone. Stable maps under `Generated` are never part of transaction cleanup.
Transaction contents are Git-ignored because copied scenes and temporary lightmaps can be very large.

## Proxy behavior

The proxy always uses the AreaLit emitter's exact submesh and transform. Read/Write does not need to be enabled and a hand-authored Bakery mesh is not required. Existing Bakery Light Meshes are ignored. Intensity is derived from the AreaLit material, while conservative Bakery sample, cutoff, self-shadow, and indirect defaults are applied consistently. **Bake intensity** shows that automatic value on every emitter row; editing it creates a manual override, and **Auto** restores the detected value. **Global intensity multiplier** scales every selected emitter after those individual values and defaults to 1. Use **Prepare Scene for Inspection** to review the result before rendering. Skinned meshes, missing geometry, and transforms that cannot be reproduced without shear are blocked instead of guessed.

## Current output mode

The first implementation uses Bakery's color lightmap output and therefore supports red, green, and blue packing. Alpha requires a future shadowmask-based output mode and is blocked rather than silently producing an incorrect map.

Selected receivers with unambiguous mappings receive UV1 and the tiling/offset captured from the **new occlusion bake**, before its temporary scenes are closed. Texture assignment is enabled by default; turn **Assign generated occlusion textures after baking** off to retain manually assigned textures while still updating UVs. Deselected receivers are unchanged.

The baker reuses the audit's UV-setting code. Shared-material conflicts are reported, never automatically repaired: differing atlas positions leave UV settings unchanged, and differing atlas textures leave the texture unchanged. Unambiguous settings can still be applied independently. If a loaded user has no baked mapping, the shared material is left unchanged. No material variants are created or assigned; conflict repair remains an explicit manual audit action. Unmapped, missing, read-only, or concurrently edited receivers are reported. Bake recovery restores published material/texture changes.

## Output adjustments

**Brightness** and **Contrast** default to 1. The preview and every saved output clamp adjusted RGB to 0�1, including when both sliders are at their defaults. This prevents HDR bake values from amplifying AreaLit emitter opacity and suppressing neighboring lights. Changing either slider updates the thumbnail immediately and applies the same adjustment during publication. Display compensation is limited to the thumbnail so it follows Unity's normal asset preview instead of appearing artificially dark. Saved Radiance HDR files are adjusted directly in linear RGBE data while preserving their original dimensions and scanline orientation; they do not pass through Unity's imported texture, GPU readback, compression, or display gamma. Empty color channels remain black so contrast changes do not leak between red, green, and blue-packed emitters. The preview automatically uses an occlusion map assigned to a checked receiver, then falls back to a previous or generated map; **Preview override** can show any texture without changing the bake source.

**Save Adjustments to Current Map** applies the sliders and clamp immediately to the previewed HDR, EXR, or PNG asset. Saving with both sliders at 1 clamps an existing map without an additional brightness or contrast adjustment. Existing project maps are not changed automatically when the package is upgraded. It creates a persistent recovery copy under `Library/AreaLitOcclusion/ManualAdjustmentBackups`, atomically replaces only the image bytes, preserves the `.meta` GUID and material links, and resets the sliders to neutral to prevent applying the same adjustment twice. **Revert Last Save** restores the original even after a script reload or Unity restart and retains a copy of the adjusted version. Normal bake publication uses the same image path and keeps its transaction backup available for recovery.

## Occlusion UV tools

These tools now live in **AreaLit Configuration Audit**, independently of baking. They consider `MeshRenderer` materials with AreaLit enabled and an `_AreaLitOcclusion` map assigned. **Match**, **Reset**, conflict detection and repair all use the audit's loaded-scene scope and **Include inactive objects and disabled components** option.

- **Match Lightmap Tiling / Offset** copies each renderer's `lightmapScaleOffset` to the AreaLit occlusion texture and selects UV1, matching the previous standalone scanner's intended AreaLit behavior.
- **Reset to Tiling 1,1 / Offset 0,0** restores only the occlusion texture's default scale and offset.

Both actions support Unity Undo and mark changed materials dirty without saving unrelated assets. Shared materials that need conflicting lightmap transforms are identified automatically in the window and remain skipped rather than being overwritten in an arbitrary scan order. The audit excludes inactive objects and disabled components by default; enable its scope option to include them. The window reports how many otherwise eligible disabled renderers are hidden. One grouped UV finding summarizes all conflicts. Expand its material and mapping lists to inspect individual renderers.

**Fix this material** and **Fix all UV conflicts** create one generated material variant per distinct required transform, then reassign only the affected renderer material slots. The original material is never modified. Variants live under `Generated/UV Material Variants`, and identical objects that need the same transform continue sharing one variant. Every repair is preflighted against a fresh scene scan, supports Unity Undo for scene assignments, and writes a recovery journal under `Library/AreaLitOcclusion` before creating assets or changing renderers. **Revert UV material repairs** restores tracked assignments after a restart or interrupted operation and removes generated variants only when no loaded object or saved project asset still references them. Scenes are marked dirty but are never saved automatically. The former `AreaLitMaterialScanner.cs` menu and context-menu tool have been removed.

## Debug and inspection

Manual occlusion-map assignment has moved to **AreaLit Configuration Audit**. The baker's **Debug & inspection** section can prepare the occlusion bake without starting Bakery. The tool creates and opens the same isolated staging scenes used by the normal bake, disables normal lights, applies channel-colored cloned AreaLit materials, creates the Bakery proxies, and pauses for inspection.

While inspection mode is active, use **Bake Prepared Scene** to continue or **Revert to Original Scenes** to leave without baking. A render started directly in Bakery is also detected and tracked. The transaction survives script recompilation as long as its staging scenes remain loaded; otherwise the normal recovery prompt is shown.

The first published texture is converted from Bakery's Lightmap importer type to a regular material texture with sRGB sampling and mipmaps, matching the existing replacement-file workflow. Later bakes preserve the stable texture's `.meta` file and any importer changes made by the artist.
