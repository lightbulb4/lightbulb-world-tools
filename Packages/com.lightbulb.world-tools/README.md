# Lightbulb World Tools

## Mochie Baked Specular

**Tools > Lightbulb > Mochie Baked Specular** adds approximate baked highlights for Dominant Direction to the verified Mochie Standard / Standard Lite v2.13 source. Keep the material's **Bakery Mode = None** for Dominant Direction. The tool does not change bake mode or rebake the scene.

1. **Check installed Mochie**, then **Install patch**. This modifies only a recognized `Assets/.../StandardLighting.cginc`, retaining a byte-for-byte backup under `Library/LightbulbWorldTools/`. It uses the existing lightmap samples and Bakery Specular Highlights toggle/strength; no new lightmap textures, draw passes or shader/material copies are needed. Existing enabled toggles gain the new behavior immediately.
2. Optionally enable **Reapply after compatible updates**. This per-project setting is off by default. Source hashes (line-ending independent) guard the lighting and BRDF integration; changes to those files require review, even if a new release looks similar. Unchanged compatible files are automatically patched again after import. Unknown/native implementations are not guessed at or overwritten. An opted-in project's build is blocked if the patch is missing/incompatible; batch builds never rewrite the source.
3. **Scan active scene**. Only renderer-assigned Standard / Standard Lite materials with compatible baked data are candidates, including inactive renderers. Existing SH/RNM/MonoSH materials can use their native baked-specular support. Package-owned, embedded, read-only, already-enabled and zero-strength materials are skipped. Terrain and runtime/script-only material swaps are outside this tool's scope.
4. Review the material checkboxes and **Enable baked highlights**. Recommended selections have scalar roughness from 0.1 up to (but not including) 0.9. Textured/packed/detail/rain roughness, near-mirror or very rough surfaces, transparent materials and deliberately disabled regular highlights need manual review. These are recommendations, not a claim that roughness alone determines reflectivity. Zero roughness is a smooth reflective surface; nonmetals reflect light too. Texture pixels and animations are not analyzed. **All eligible** includes review cases.

Only the Bakery Specular Highlights property and keyword are changed. Strength, regular specular, environment reflections, metallic, roughness and Bakery Mode are preserved. Shared material changes affect all their other uses; Undo restores the changes. The tool does not automatically save material assets. Changed material or renderer/lightmap assignments invalidate the preview.

**Remove patch** disables auto-reapplication and removes only the exact Lightbulb block; unrelated source edits remain. Remove it **before uninstalling World Tools**. Shader-source changes are not Unity Undo operations. Backups in Library are local recovery copies and can be lost when Library is cleared; keep normal source control/backups too. Unknown or edited patch blocks require manual review, not restoring an old whole-file backup over a newer shader.

The new highlights use a single dominant direction, not separate lights. They can look harsh on smooth surfaces; test representative materials in VR before enabling broadly. There is extra per-pixel shader math when enabled, but no runtime editor-tool cost. The patch is limited to static directional lightmaps, leaves the existing diffuse decode unchanged, and does not extend Mochie Mobile, Uber, or dynamic GI.

## Original backups and legacy migration

Each file repair retains one original backup per asset. Crunch, resizing and the Mochie linear-texture fix share the same texture metadata original. Repeated operations and shader patch install/remove cycles reuse it. UV Viewer uses temporary editor materials and no longer writes Resources assets.

A temporary, automatic upgrade migration runs once per project/user when the editor is idle and open scenes are saved. It moves the earliest retained legacy originals into `Library/LightbulbWorldTools/Originals/<asset identity>/`, verifies their bytes, and removes surplus old run copies and empty directories. It also removes unchanged, recognized UV Viewer helpers when project and loaded-scene reference checks find no uses. Ambiguous, modified or referenced files are retained. Packed textures, source textures, swapper materials and pre-swap scene backups are functional/recovery assets and are not deleted by filename guesses.

There is no cleanup window or menu command. The migration records completion in editor preferences, adds no project marker file, and is isolated for removal in a later release. It does not run during batch builds/tests. If it encounters an error, it logs the reason and retries after the next editor reload.

Each original backup folder contains `source.txt` identifying its restore destination. To restore, close Unity and copy the retained file to the source asset's current path (match the GUID for renamed textures). This restores the starting state, not the previous operation. Library backups are not Unity Undo and disappear if Library is cleared.

Small Unity editor diagnostics and repairs for world projects. Diagnostics report to Unity's Console; texture batching has a preview window. Report rows include an object or material context where possible, so clicking a Console entry selects the relevant asset or GameObject.

Open the commands directly under **Tools > Lightbulb**.

## Rendering

### Scene Texture Crunch Compression

Open **Tools > Lightbulb > Scene Texture Crunch Compression**, choose **Enable** or **Disable**, then **Scan active scene**. The main list shows only textures with pending changes. Textures already matching your settings are summarized by count; skipped textures and reasons are available in the collapsed **Skipped textures** section. Exclude individual textures and apply the selected changes.

- Scans current in-memory scene references, including inactive objects, materials, sprites/UI, terrain layers and vegetation prototypes, referenced prefab assets, serialized script/ScriptableObject fields, animation texture/material swaps, skybox/reflection textures and lightmaps. Deduplicates texture assets. Uninspectable dependencies are reported. Runtime string-based loading, shader globals assigned by code, and arbitrary custom serialization are not guaranteed discoverable.
- **Crunch quality** ranges from **0 to 100** and defaults to **50**. Higher quality produces larger files and longer imports. Enabling sets this quality on compatible settings, including textures already using Crunch. Disabling preserves the stored quality for later use. Crunch reduces disk/download size, not runtime VRAM.
- When enabling, **Crunch currently off only** excludes an entire texture if Default or any enabled platform override already has Crunch enabled or an explicit Crunched format. Existing qualities such as 50 and 100 are left untouched; the quality slider applies only to textures newly enabled by this batch. Disabled platform overrides do not affect the filter. Turning this option off restores normal quality-update matching. Changing the option requires a new scan, so hidden textures cannot remain selected for application.
- **Proposed changes — not applied yet** distinguishes the current state from the action. **Default platform** means the texture's base import settings; named platform overrides are listed separately. Enabling shows **Crunch OFF → ON · Quality: 100**, while changing active compression shows **Crunch stays ON · Quality: 50 → 100**. Unity's stored quality while Crunch is off is not displayed as active compression.
- **Texture dimensions and maximum-resolution settings are preserved.** Updates Default and every existing enabled platform override; explicit DXT/ETC formats map to/from corresponding Crunch formats. Incompatible formats such as BC7/ASTC, HDR or uncompressed Automatic settings are skipped rather than coerced. Generated textures, lightmap-type imports, cubes, arrays, read-only metadata and non-embedded package assets appear only in the collapsed skipped section.
- Import settings belong to shared assets: changes also affect other scenes using those textures. Original image files remain untouched. Scene membership and import settings are rechecked before applying. Cancellation stops between textures, leaving completed changes applied.
- Original `.meta` files are backed up under `Library/LightbulbWorldTools/`. To restore, close Unity and copy the backed-up files to their matching project paths. This restores all import settings and is **not Unity Undo**; deleting `Library` removes the backups.

### Find Unreferenced Disabled Objects

Open **Tools > Lightbulb > Find Unreferenced Disabled Objects**, then **Scan active scene**. The compact list shows only eligible disabled objects with no known external references, checked by default. Uncheck anything you want to keep, then choose **Mark selected EditorOnly**. Referenced objects are hidden. Genuine scan issues appear once above the list and block changes until resolved. This tags branch roots; it does not delete objects or automatically save the scene.

- Candidates must be explicitly disabled (`activeSelf == false`); an active child beneath an inactive parent is not independently considered disabled. Already-EditorOnly branches are omitted.
- Checks references to GameObjects, components and descendants. An external reference to any child protects the parent branch. Structural hierarchy links and references wholly inside the same branch do not prevent removing that whole branch from a build. References from other loaded scenes also protect active-scene targets; those other scenes are not modified.
- Detects serialized fields/arrays, UnityEvents, animation object curves and hierarchy paths (including Animator overrides and legacy Animation), Timeline bindings/exposed references, and live Udon public variables/arrays. Video-player VRCUrl values and URL arrays are supported. Timeline-bound hierarchies and humanoid Animator hierarchies are conservatively retained. The optional Udon adapter is tested with VRChat SDK **3.10.5** and does not require or modify the SDK.
- Missing scripts, unsupported Udon reference containers or failed serialized inspection make the scan uncertain and block cleanup. Objects marked NotEditable and the RenderSettings sun are retained. The tool rescans before changing tags and refuses selected objects that are no longer eligible.
- **No detected references is not proof an object is never used.** Runtime name/tag lookups, hierarchy enumeration, animation-event strings and arbitrary custom code can still activate or use it. Exclude such objects manually. EditorOnly excludes **all children** from builds too. Assets referenced elsewhere can remain in the build.
- **Edit > Undo** restores the original tags. **Restore original tags from this window** also restores changed objects in the active scene, provided they still have the EditorOnly tag. Restore records survive script reloads while the window stays open; retain your normal scene/source-control backup for later sessions. Subsequent user tag changes are left alone. Nested selected branches are consolidated to their highest selected root.

### Resize Referenced Textures

Multi-select materials in the **Project** window, then right-click **Materials > Resize Referenced Textures...**. Also available at **Tools > Lightbulb > Resize Referenced Textures...**.

- The preview lists unique textures assigned to the selected materials' current shader texture properties, including normal maps and hidden slots. It does not search old saved properties from previous shaders, shader globals, or script-assigned textures. Hover the slot count to see the selected material/property names.
- **Maximum resolution** defaults to **1024**. Only lowers larger import caps when the source exceeds the limit; already smaller/equal textures and lower caps are left alone. Preserves aspect ratio and the existing resize algorithm. Original image files are never resized or overwritten.
- Processes **Default and every existing enabled platform override**. Does not create overrides or raise any platform's cap.
- **Crunch compression:** Leave unchanged (default), Enable, or Disable. This applies independently of size, including to textures already below the cap. Explicit DXT1/DXT5/ETC RGB/ETC2 RGBA formats switch only to/from their matching Crunch variant. Incompatible explicit formats (BC7, ASTC, HDR, etc.) are flagged, not converted; their size can still be reduced. Automatic remains Automatic and follows Unity's supported target-format selection. Uncompressed/HDR Automatic settings are not switched to Crunch. Other settings, including compression quality, sRGB, normal type, and mipmaps, are preserved.
- Exclude individual textures before applying. The material set stays fixed until you click **Use selected materials**; selecting a texture in the preview does not replace the material set. **Refresh preview** reads current settings again.
- **Shared textures change everywhere they are used**, including unselected materials and other scenes. Skips generated textures, RenderTextures, cubes, arrays, lightmap-type imports, read-only metadata, and non-embedded package assets.
- Confirms the operation, refuses stale import-setting previews, reimports each texture once, verifies the requested settings, and reports failures. Cancellation stops between textures; completed changes remain applied.
- Backs up original `.meta` files under `Library/LightbulbWorldTools/` before changing anything. The Console prints the location. To restore, close Unity and copy the backed-up `.meta` files to the matching project paths. This restores all import settings, not just size/Crunch; **it is not Unity Undo**. Deleting `Library` removes the backups.

Crunch targets disk/download size rather than GPU memory. Reducing resolution also reduces GPU memory usage. No textures are changed merely by installing the package or opening the preview.

### Find Empty Material Maps

Open **Tools > Lightbulb > Find Empty Material Maps**, then **Scan active scene** outside Play Mode and Prefab Mode.

- Finds assigned metallic, roughness/smoothness/gloss, ambient occlusion, normal/bump, height/parallax/displacement, and common packed data maps. Identification uses shader property names and Inspector descriptions, including saved properties from previous shaders. Custom slots with unrelated names/descriptions are not recognized.
- Scans materials currently assigned to renderers, terrains, and skyboxes in the **active scene**, including inactive objects and all renderer material slots. Other additive scenes, uninstantiated prefab assets, and materials referenced only by scripts or animation swaps are not scanned. Unreferenced texture files are not scanned. A preview cannot be applied after switching scenes.
- **Exact matching** requires every pixel to have the same RGBA value. **Fuzzy matching** requires **99.99% identical pixels**, with a fixed threshold and no percentage slider. The percentage measures pixels with exactly the same value, not a color-distance tolerance. Compression artifacts may reduce the match percentage.
- Reads every pixel of the current imported mip-zero image on the GPU in bounded strips, without enabling Read/Write or changing import settings. This checks the current platform's imported resolution/compression, not the original source at a higher resolution. All four channels are checked together: useful alpha or packed-channel detail prevents an exact match. Normal maps are compared in their GPU channel packing, not displayed as decoded normal vectors. A supported graphics device is required; unavailable full-resolution streaming mips and unsupported texture types are reported as skipped.
- Each result shows the texture, dimensions, constant sampled RGBA value, matching pixel count/percentage, and every material/property reference. **Filter results**, **Select all removable**, and **Select none** help review candidates. The filter does not change bulk selection.
- **Remove** clears one texture from all matching slots on the scanned scene materials. **Remove all selected** does the same for the selected textures. This includes non-data slots (such as albedo) sharing that texture and unused saved texture properties on those materials. Other materials are not edited. **A shared material asset also changes wherever else that same material is used**; scene-local copies are not created. Shader source files and texture files are never deleted or modified.
- All matching references within the scene material set must belong to editable standalone `.mat` assets under `Assets`. Read-only, package, embedded, or transient references block removal of that texture; extract/copy the material into `Assets` and update its references first. The tool rechecks texture changes, scene references, and editability before changing any material.
- Changes form one **Edit > Undo** operation and are not automatically saved. Review the scene, then save the project. Texture scale/offset and scalar values remain unchanged. Unity's normal material validation may update shader keywords (for example, Standard disables its metallic-map keyword).

**Constant does not mean visually irrelevant.** A solid black metallic map, white roughness map, or even a uniform non-neutral normal can affect appearance. Removing it uses that shader's unassigned-map behavior; the tool does not translate constants into shader-specific sliders or implement custom shader keyword rules. Fuzzy removal deliberately discards the nonmatching pixels. Review before saving and use Undo if needed.

Clearing material references can reduce build texture usage, but remaining references from scripts, other assets, Resources, or other build inclusion rules can keep a texture in a build. This tool does not promise a byte-size reduction or remove those other references.

### Find GPU Instancing Candidates

Finds repeated single-material `MeshRenderer` combinations whose material does not have GPU instancing enabled. It groups renderers by the state that must match for an instanced draw and reports active, enabled objects that are not marked for static batching.

Multi-material renderers are intentionally skipped. The report is advisory: the shader must support GPU instancing, and a material property block may still affect batching behavior.

## Lighting

### Pack Mochie Materials in Scene

Open **Tools > Lightbulb > Pack Mochie Materials in Scene**, then **Scan active scene**. Review each material's packing or cleanup actions, exclude any you want to leave unchanged, and choose **Pack / clean selected materials**. Rerun this tool to clear leftover separate-map references from earlier packing runs.

- Requires one installed copy of **Mochie Standard v2.13**, including its editor tools and `Hidden/Mochie/TexturePacker` shader. Other versions are refused until verified. World Tools remains usable without Mochie installed and does not modify or distribute Mochie source.
- Uses the installed `Mochie.TexturePacker.PackTextures` method through an optional reflection adapter, plus Mochie's own keyword and blend-mode updates. Primary maps are supported for **Mochie/Standard** and **Mochie/Standard Lite**. **Include Standard detail maps** additionally packs Standard's detail data maps. Uber and Mobile are not supported.
- Uses the same active-scene material scope as the empty-map tool: renderers, terrains, and skyboxes, including inactive objects. Materials used only outside that scene are excluded. Shared material assets still affect their other uses.
- Separate workflows require **at least two distinct source textures per packed output**. Primary and detail are checked independently: one primary texture plus one detail texture is not eligible, and reusing one texture in multiple slots still counts as one. Single-source workflows are skipped with a preview note and retain their settings/references. Already-packed workflows with leftover separate-map references remain eligible for cleanup when a packed texture asset is assigned and the packed shader keyword is enabled. Missing/invalid packed setups retain their references and report a note. Only editable standalone `.mat` assets under `Assets` are changed. Unsupported packing input types and unavailable full-resolution streaming mips are reported.
- Primary channel layout is **R = AO, G = roughness/smoothness, B = metallic, A = height** when present. Detail layout is RGB = AO / roughness / metallic. The native packer reads each source's red channel and handles source tiling/offset and linear PNG import. Each unique set of packing inputs creates one uniquely named `*_Packed.png` beside its first selected material (in asset-path order); matching materials in that batch share the output. Existing textures are not overwritten.
- Sharing compares source asset identities and import/content dependency hashes, effective source tiling/offset, missing maps, and primary/detail mode. Slider strengths, material names and AreaLit occlusion offsets do not affect the packed image. Materials retain independent strengths and AreaLit settings even when they share an output. Different input assets are conservatively packed separately even if they contain identical pixels. Sharing lasts for one batch; packed maps created by separate runs are not consolidated.
- All channels are packed with unit strength. Primary AO/roughness/metallic strengths are copied into their matching packed-mode sliders, preserving their values and adjustment range instead of baking them into the output image. Height and detail strengths remain on the material; detail channels without a source map have their blend strength set to 0, matching the separate workflow's absent-map behavior. Mochie source/shaders are not modified. This behavior applies to new packs: existing packed textures keep their current strengths during cleanup, and packs made by older versions require the original maps to recover any detail lost through baked strengths.
- Once all requested packs for a material succeed, its separate primary metallic/roughness/AO/height references and/or separate detail metallic/roughness/AO references are cleared. Reused outputs receive the same cleanup. Albedo, normal maps, masks, AreaLit maps, and every texture file are retained. Height presence and detail blending strengths are established before references are cleared.
- Cleanup of already-packed materials only clears the leftover references. It does not repack, reset packed-map tiling/channels/strengths, or change AreaLit settings. **Include Standard detail maps** controls both detail packing and detail cleanup. Switching back to a separate workflow later requires Undo or reassigning the original source textures.
- The tool validates scene identity, material state, source/packed texture dependency hashes, output assignment/import, and packed keywords. A failed material is restored with its source references; any PNG created before failure remains on disk. Cancellation stops between materials and keeps completed results.
- **Edit > Undo** restores material settings and cleared references for the batch. It does not delete generated PNGs. Materials are not automatically saved; review the scene and then save the project. Packing and import compression can affect appearance, especially non-grayscale source maps or detail maps using alpha in blending. This is not a guarantee of pixel-identical rendering or reduced build size.

### Bakery LV3 Swapper

Open **Tools > Lightbulb > Bakery LV3 Swapper** in one saved scene, outside Play Mode. This is an editor authoring workflow: **rebake after changing the setup**. It does not provide an in-game lighting switch or guarantee identical lighting between engines.

Requires installed **VRC Light Volumes 3.0.0-dev.18** and Bakery. The adapter checks the installed types and method contracts; neither dependency is modified or distributed. Older/newer LV versions are refused because registration and baking behavior varies.

1. Preview from Bakery or from Point Light Volumes. Choose natural point/spot/rectangular-area conversion, or force point-light approximations. Review unsupported sources and shaders, select conversions, and create the experiment. Before conversion, the tool saves a copy of the current scene (including unsaved scene edits) as `BeforeExperiment.unity` in its output folder. This preserves scene configuration; it does not duplicate texture/material dependencies or protect baked files from later overwrites.
2. Both sets remain in the scene with persistent counterpart links. **Bakery**, **Point LVs only**, and **Hybrid** presets change participation; they never repeat conversion. Edit each active setup independently. Explicit **Copy settings to counterpart** overwrites supported point/spot properties only after confirmation.
3. Choose individual lights and custom combinations, then **Apply**. Excluded lights have their intensity and relevant shadow/probe bake flags gated, with authoring values retained in the experiment state. The window exposes saved intensity and enabled state while a light is gated. Color, projection assets, transforms and other parameters remain on the actual components.
4. Regular volumes are excluded by deactivating their dedicated GameObjects, preserving their configured Bake flag and Bakery helper. Their objects must not contain renderers, terrain, Unity lights or a manager. Native regular-volume baking checks hierarchy activity; the point shadow baker does not reliably check component enabled state, so the tool separately gates its shadow flags. Native sync copies the result to Udon and refreshes the manager.
5. Save the scene. The experiment's data-only component lives on an **EditorOnly** object and is stripped from world/player builds. Both configured lighting systems remain in the scene until explicit cleanup. Keep that tag and do not manually delete the state or tracked lights mid-experiment.

**Lighting and material scope:** mesh-renderer lightmap scales, receive-GI/probe settings and existing lightmap indices/ST are saved and restored. Terrain lightmap scale and assignments are also recorded. Turning lightmaps off sets scale to zero and clears assignments; mesh renderers stop sampling old Unity light probes. Unity lights are disabled in the LV-only setup. Reflection probes are disabled there by default, with an explicit option to retain them. Baking can overwrite old lighting assets, so use a separate Bakery output folder for each setup. A restored assignment is not a restored bake.

Shader routing is verified for **Mochie Standard / Standard Lite v2.13** renderer materials. The tool creates material assets under `Assets/LightbulbLightingExperiments/<scene>/` and assigns those copies only to this scene's renderers; other scenes retain their originals. Shader-specific routing fields are owned by the experiment controls; other material edits persist. Unsupported shaders and terrain material routing are not automatically converted. The preview lists unsupported shaders; accepting them means those surfaces may retain other lighting or not show LVs. Materials reached only through scripts/animation swaps are outside scope.

Hybrid can keep Bakery lightmaps while enabling LV diffuse and/or LV specular highlights. **LV specular-only needs lightmaps** with this Mochie shader. Reflected scenery, lightmap specular highlights and light-source specular highlights are distinct contributions. Full baked lightmaps do not expose a general “Bakery shadows only” switch; no such unsupported control is offered. The tool does not suppress unrelated lighting packages such as AreaLit/LTCGI, emissive materials, or custom lighting scripts.

**Conversion:** point/spot color, pose and supported textures/cones are transferred. Brightness/falloff/source-size relationships are estimates with an initial brightness multiplier, not photometric equivalence. Only readable four-vertex XY rectangular emitters are mapped to area LVs; arbitrary mesh emission, IES, sky and sun need explicit force-point approximations or remain unconverted. Force-point sky uses world center, sun uses world center displaced opposite its direction by the world's bounds radius, and mesh emitters use their bounds center. Converted lights use world scale one except rectangular area dimensions. PLV parametric range is derived by its manager; a copied range value does not override that model. Unsupported LUT/material projections are refused rather than guessed.

**Fitted volumes:** deterministic geometry-bound splitting uses fixed candidate splits, at most 10 boxes, padding, and a configurable voxel density. It only splits when doing so reduces estimated occupied box volume by more than 10%; one cube-shaped world normally remains one volume. Geometry layers and a Scene View bounds preview help exclude distant decoration. This is a spatial heuristic, not room/walkability detection. The displayed raw SH estimate excludes atlas padding/other texture overhead. Regular volumes require a bake to contain light. The installed manager uploads at most 128 point/spot/area lights; previews warn above that count, and overlapping lights can still be expensive below it.

**Finishing:** cleanup previews the components to remove, then uses Undo-aware component removal, including Udon backings. Removing Bakery includes scene Bakery runtime components/storage and switches remaining LV baking to Progressive. Removing LVs retains unrelated geometry and Bakery emitters. Texture assets, source packages and experiment material copies remain on disk. Finalization keeps material edits and restores original shader-routing values when returning to Bakery. The retained setup needs another bake. Undo finalization before clearing Unity's undo history if you need the removed setup back.

**Known native bake issue:** the installed LV dev.18 point shadow baker reports `RenderTexture.Create failed: colorFormat & depthStencilFormat cannot both be none` when rendering a Mochie Standard test surface in Unity 2022.3.22f1 / D3D11. This was reproduced through the native LV bake without an experiment. The inclusion test passes with a basic Unlit surface: the enabled point gets a shadow asset and the excluded point gets none. The tool does not repair this upstream rendering issue; check the Console and verify shadow appearance in your world after baking.

### Fix Mochie Linear Textures in Scene

Run **Tools > Lightbulb > Fix Mochie Linear Textures in Scene** outside Play Mode. It applies the same texture import change as Mochie's **"This texture is marked as sRGB, but should be linear" > Fix Now**, across the active scene.

- Supports exactly **Mochie/Standard** and **Mochie/Standard Lite**, using the warning rules verified against StandardEditor **v2.13**. Mochie source is neither modified nor bundled; the tool has no Mochie assembly dependency.
- Includes inactive objects, every renderer material slot, and terrain material templates. Other loaded scenes, Prefab Mode, and materials only referenced by scripts or animations are not scanned.
- Separate primary workflow: metallic, roughness/smoothness, occlusion, and height (except triplanar). Packed primary workflow: packed map only. Standard also checks the separate or packed detail maps. Lite's hidden detail data maps are excluded.
- Only textures currently marked sRGB are changed. Each unique texture is reimported once. Color maps, normal-map import warnings, Uber, Mobile, and other shader families are outside the scan.
- Logs the candidate textures and material/property names, then asks for confirmation. **The texture import setting changes everywhere that texture is used**, including other materials and scenes, even if another use is a color slot.
- Skips textures without a texture importer, read-only metadata, and non-embedded packages. Reports individual reimport failures and verifies that each fixed texture remains linear.
- Backs up original `.meta` files under `Library/LightbulbWorldTools/`, preserving project-relative paths. The Console prints the location. To roll back, close Unity and copy those `.meta` files to their matching project paths. This restores all import settings to their pre-fix values; it is not Unity Undo. Deleting `Library` removes the backups.

A second run makes no changes when the scene's supported textures are already linear.

### Rank by Lightmap Texel Usage

Ranks baked renderers by their estimated allocation in the current lightmaps. This is total lightmap pixel usage, not texel density per world-space unit. Bake the scene before running the command.

## Geometry

### Rank Renderers by Vertex Count

Lists `MeshRenderer` and `SkinnedMeshRenderer` objects from highest to lowest mesh vertex count.

### Rank GameObjects by Vertex Count

Groups vertex counts by the GameObject directly containing each renderer and lists the totals from highest to lowest.

### UV Viewer

Opens Bunny83's UV Viewer for the selected mesh. It can display UV channels, submeshes, textures, triangles, and the corresponding geometry in Scene view. The bundled source is the upstream MIT-licensed `UVViewer.cs`, with only its Unity menu path changed for consistency.

See [Third-Party Notices](THIRD_PARTY_NOTICES.md) for attribution and license details.

## Video playback

### Fix VideoPlayerShim URL Resolver

Run **Tools > Lightbulb > Fix VideoPlayerShim URL Resolver** outside Play Mode. This fixes the known VideoPlayerShim **1.5.0** bug that sends direct streams such as `rtspt://` through yt-dlp and can pass its error output to AVPro as a media path.

- Checks the installed package's identity, version, and complete resolver-source fingerprint before editing. No downloads or extra package dependencies.
- Only patches the verified original 1.5.0 resolver in `Packages/dev.architech.videoplayershim`. Recognized previous repairs are left alone. Other versions, custom source, linked folders, and cached/external packages are refused.
- Passes non-HTTPS URLs directly to the player before starting yt-dlp. HTTPS resolution keeps its existing format options; failed resolver processes stop, and only an HTTP(S) URL from stdout can reach the player.
- Changes only `Editor/PlayModeUrlResolverShim.cs`, preserving UTF-8 BOM/line-ending style and its `.meta` file. Scenes, player settings, and yt-dlp itself are untouched.
- Atomically replaces the source with an exact original-file backup under `Library/LightbulbWorldTools/`. The Console prints the backup path. To undo, close Unity and copy that backup over the resolver script. This is not Unity Undo; deleting `Library` also deletes these backups.

Unity recompiles after a successful repair. Package reinstalls or updates may replace the patched source; rerun the command only if this specific issue returns. This does not repair unrelated yt-dlp or playback errors.

## Requirements

- Unity 2022.3
- No VRChat SDK or third-party package dependency

The package is editor-only and does not add scripts or assets to a world build.
