# ProceduralTerrain

> **Work in progress — 0.0.1 preview.** Ships novel default terrain for the first four biomes only; the rest stay near-vanilla for now. Expect breaking config and bake-format changes between versions (retune and rebake). Feedback welcome.

Procedural terrain system for Valheim built on top of [Voxheim](https://thunderstore.io/c/valheim/p/Voxheim/Voxheim/). Carves caves, overhangs, arches, and raised stone formations into the voxel world using configurable noise-based profiles per biome.

## Features

- **Hot-reload config** — edit and save `procedural_terrain_biomes.cfg` while in-game; terrain remeshes live without restarting
- **Per-biome noise profiles** — combine named terrain modes into profiles and map each biome to one; biome boundaries blend smoothly
- **SDF bake system** — pre-compute terrain for a radius and save to disk, eliminating zone-load hitching for baked areas
- **Multiplayer bake distribution** — clients connecting to a server with a bake file download it automatically on join; the game is fully playable while the bake generates on the server and while clients download it

## Requirements

- [BepInExPack Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/)
- [Voxheim](https://thunderstore.io/c/valheim/p/Voxheim/Voxheim/)

## Configuration

Biome profiles live in `BepInEx/config/procedural_terrain_biomes.cfg` and hot-reload on save. A fully commented example config with all parameters documented is included in the download.

The config has three sections:
- `[Bake]` — bake resolution and compression settings
- `[Debug]` — global terrain toggles and blending parameters
- `[Profile:name]` / `[BiomeName]` — per-biome noise profiles and biome-to-profile mappings

## Baking

Once your config is tuned for a playthrough, bake the terrain to disk for much faster generation during gameplay. The bake is world- and config-specific — changing either invalidates it.

```
pt_bake [radius_in_meters]   — bake terrain on the current machine (admin only on servers)
pt_clearbake                 — delete the local bake file
pt_clearbake server          — delete the bake file on the server (admin only)
```

**Multiplayer:** run `pt_bake` once on the server (or as an admin while connected). The server bakes in the background — players can continue playing. When done, new clients connecting will automatically download the bake file with a progress indicator. Live calculation is used as fallback during download.

## Thunderstore

https://thunderstore.io/c/valheim/p/ctogle/ProceduralTerrain/
