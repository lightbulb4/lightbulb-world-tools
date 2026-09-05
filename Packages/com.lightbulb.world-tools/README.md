# Lightbulb World Tools

Small Unity editor diagnostics and repairs for world projects. The tools intentionally report to Unity's Console instead of maintaining a separate results window. Report rows include an object or material context where possible, so clicking a Console entry selects the relevant asset or GameObject.

Open the commands directly under **Tools > Lightbulb**.

## Rendering

### Find GPU Instancing Candidates

Finds repeated single-material `MeshRenderer` combinations whose material does not have GPU instancing enabled. It groups renderers by the state that must match for an instanced draw and reports active, enabled objects that are not marked for static batching.

Multi-material renderers are intentionally skipped. The report is advisory: the shader must support GPU instancing, and a material property block may still affect batching behavior.

## Lighting

### Fix Mochie Linear Textures in Scene

Run **Tools > Lightbulb > Fix Mochie Linear Textures in Scene** outside Play Mode. It applies the same texture import change as Mochie's **"This texture is marked as sRGB, but should be linear" > Fix Now**, across the active scene.

- Supports exactly **Mochie/Standard** and **Mochie/Standard Lite**, using the warning rules verified against StandardEditor **v2.13**. Mochie source is neither modified nor bundled; the tool has no Mochie assembly dependency.
- Includes inactive objects, every renderer material slot, and terrain material templates. Other loaded scenes, Prefab Mode, and materials only referenced by scripts or animations are not scanned.
- Separate primary workflow: metallic, roughness/smoothness, occlusion, and height (except triplanar). Packed primary workflow: packed map only. Standard also checks the separate or packed detail maps. Lite's hidden detail data maps are excluded.
- Only textures currently marked sRGB are changed. Each unique texture is reimported once. Color maps, normal-map import warnings, Uber, Mobile, and other shader families are outside the scan.
- Logs the candidate textures and material/property names, then asks for confirmation. **The texture import setting changes everywhere that texture is used**, including other materials and scenes, even if another use is a color slot.
- Skips textures without a texture importer, read-only metadata, and non-embedded packages. Reports individual reimport failures and verifies that each fixed texture remains linear.
- Backs up original `.meta` files under `Library/LightbulbWorldTools/Backups/MochieLinearTextures/<run>/`, preserving project-relative paths. The Console prints the location. To roll back, close Unity and copy those `.meta` files to their matching project paths. This restores all import settings to their pre-fix values; it is not Unity Undo. Deleting `Library` removes the backups.

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
- Atomically replaces the source with an exact original-file backup under `Library/LightbulbWorldTools/Backups/VideoPlayerShim/`. The Console prints the backup path. To undo, close Unity and copy that backup over the resolver script. This is not Unity Undo; deleting `Library` also deletes these backups.

Unity recompiles after a successful repair. Package reinstalls or updates may replace the patched source; rerun the command only if this specific issue returns. This does not repair unrelated yt-dlp or playback errors.

## Requirements

- Unity 2022.3
- No VRChat SDK or third-party package dependency

The package is editor-only and does not add scripts or assets to a world build.
