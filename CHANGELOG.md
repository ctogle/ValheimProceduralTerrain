# Changelog

## 0.0.3

- Introduced the rolling bake — the world bakes around you as you play, so pre-baking a world is rarely needed. On by default, tunable live, F8 for a status panel.
- Overhauled config handling: terrain shape (biomes.cfg), world content (placements.cfg), and local settings (client.cfg) are now separate files, and a server imposes terrain and content on its clients. Cosmetic or unused-config edits no longer invalidate a baked world.
- A server now imposes live edits to its terrain config on connected clients without a relog.
- FarLands integration: the distant far-land follows PT's terrain.
- Content and balance tweaks: disabled Fenring cave spawns; clustered swamp roots so monsters can path between them.
- Location groundwork (village & dungeon pads, entrances) now levels correctly on first visit, for all location types, and is cheaper to serve.
- Added basebuffer to pinch closed the gap at the base of the isotropic_formation mode.
- Lower serve-path hitching.

## 0.0.2

- ⚠️ **Breaking: upgrading from 0.0.1 destroys existing worlds — please start a new world. Sorry!**
- Plains cave content: ivy, shipwreck debris, seagull colonies, fuling patrols.
- Ivy lit only where daylight reaches through open air.
- Warns before loading a world with a mismatched/missing terrain version (protects vanilla saves).
- Fixed Swiss-cheese parameter blending from the strata mode.
- Draft Mistlands profile.
- New parameters for the blob modes.
- Tuned Black Forest and Mountains for traversability.
- Config comment and formatting edits no longer invalidate a baked world.

## 0.0.1

Initial release of the first four biomes.
