# Lightbulb World Tools

Small Unity editor diagnostics and repairs for world projects. Diagnostics report to Unity's Console; texture batching has a preview window. Report rows include an object or material context where possible, so clicking a Console entry selects the relevant asset or GameObject.

Open the commands directly under **Tools > Lightbulb**.

## Rendering

### Resize Referenced Textures

Multi-select materials in the **Project** window, then right-click **Materials > Resize Referenced Textures...**. Also available at **Tools > Lightbulb > Resize Referenced Textures...**.

- The preview lists unique textures assigned to the selected materials' current shader texture properties, including normal maps and hidden slots. It does not search old saved properties from previous shaders, shader globals, or script-assigned textures. Hover the slot count to see the selected material/property names.
- **Maximum resolution** defaults to **1024**. Only lowers larger import caps when the source exceeds the limit; already smaller/equal textures and lower caps are left alone. Preserves aspect ratio and the existing resize algorithm. Original image files are never resized or overwritten.
- Processes **Default and every existing enabled platform override**. Does not create overrides or raise any platform's cap.
- **Crunch compression:** Leave unchanged (default), Enable, or Disable. This applies independently of size, including to textures already below the cap. Explicit DXT1/DXT5/ETC RGB/ETC2 RGBA formats switch only to/from their matching Crunch variant. Incompatible explicit formats (BC7, ASTC, HDR, etc.) are flagged, not converted; their size can still be reduced. Automatic remains Automatic and follows Unity's supported target-format selection. Uncompressed/HDR Automatic settings are not switched to Crunch. Other settings, including compression quality, sRGB, normal type, and mipmaps, are preserved.
- Exclude individual textures before applying. The material set stays fixed until you click **Use selected materials**; selecting a texture in the preview does not replace the material set. **Refresh preview** reads current settings again.
- **Shared textures change everywhere they are used**, including unselected materials and other scenes. Skips generated textures, RenderTextures, cubes, arrays, lightmap-type imports, read-only metadata, and non-embedded package assets.
- Confirms the operation, refuses stale import-setting previews, reimports each texture once, verifies the requested settings, and reports failures. Cancellation stops between textures; completed changes remain applied.
- Backs up original `.meta` files under `Library/LightbulbWorldTools/Backups/MaterialTextures/<run>/` before changing anything. The Console prints the location. To restore, close Unity and copy the backed-up `.meta` files to the matching project paths. This restores all import settings, not just size/Crunch; **it is not Unity Undo**. Deleting `Library` removes the backups.

Crunch targets disk/download size rather than GPU memory. Reducing resolution also reduces GPU memory usage. No textures are changed merely by installing the package or opening the preview.

### Find Empty Material Maps

Open **Tools > Lightbulb > Find Empty Material Maps**, then **Scan project materials** outside Play Mode.

- Finds assigned metallic, roughness/smoothness/gloss, ambient occlusion, normal/bump, height/parallax/displacement, and common packed data maps. Identification uses shader property names and Inspector descriptions, including saved properties from previous shaders. Custom slots with unrelated names/descriptions are not recognized.
- Scans material assets across the project, including materials used only by other scenes, prefabs, or animations. Package, embedded, and loaded transient material references are included in the report. Unreferenced texture files are not scanned.
- **Exact matching** requires every pixel to have the same RGBA value. **Fuzzy matching** defaults to **99% identical pixels**, adjustable from 90–100%. The percentage measures pixels with exactly the same value, not a color-distance tolerance. Compression artifacts may reduce the match percentage.
- Reads every pixel of the current imported mip-zero image on the GPU in bounded strips, without enabling Read/Write or changing import settings. This checks the current platform's imported resolution/compression, not the original source at a higher resolution. All four channels are checked together: useful alpha or packed-channel detail prevents an exact match. Normal maps are compared in their GPU channel packing, not displayed as decoded normal vectors. A supported graphics device is required; unavailable full-resolution streaming mips and unsupported texture types are reported as skipped.
- Each result shows the texture, dimensions, constant sampled RGBA value, matching pixel count/percentage, and every material/property reference. **Filter results**, **Select all removable**, and **Select none** help review candidates. The filter does not change bulk selection.
- **Remove** clears one texture from all its referenced material slots. **Remove all selected** does the same for the selected textures. This includes non-data slots (such as albedo) sharing that texture and unused saved texture properties. Shader source files and texture files are never deleted or modified.
- All references must belong to editable standalone `.mat` assets under `Assets`. Read-only, package, embedded, or transient references block removal of that texture; extract/copy the material into `Assets` and update its references first. The tool rechecks texture changes, references, and editability before changing any material.
- Changes form one **Edit > Undo** operation and are not automatically saved. Review the scene, then save the project. Texture scale/offset and scalar values remain unchanged. Unity's normal material validation may update shader keywords (for example, Standard disables its metallic-map keyword).

**Constant does not mean visually irrelevant.** A solid black metallic map, white roughness map, or even a uniform non-neutral normal can affect appearance. Removing it uses that shader's unassigned-map behavior; the tool does not translate constants into shader-specific sliders or implement custom shader keyword rules. Fuzzy removal deliberately discards the nonmatching pixels. Review before saving and use Undo if needed.

Clearing material references can reduce build texture usage, but remaining references from scripts, other assets, Resources, or other build inclusion rules can keep a texture in a build. This tool does not promise a byte-size reduction or remove those other references.

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
