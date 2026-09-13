# Changelog

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
