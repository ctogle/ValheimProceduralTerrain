# ProceduralTerrain

> **Work in progress.** Expect rough edges, breaking config changes, and incomplete documentation.

Procedural terrain system for Valheim built on top of Voxheim. Carves caves, overhangs, arches, and raised stone formations into the voxel world using configurable noise-based profiles per biome.

## Features

- **Hot-reload config** — edit and save `procedural_terrain_biomes.cfg` while in-game; terrain remeshes live without restarting
- **Per-biome noise profiles** — define named profiles of terrain parameters and map each biome to one; biome boundaries blend smoothly
- **Three independent terrain modes** — tunnels (A), surface bands (B), raised density terrain (C) — combinable per profile
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
voxcaves_bake [radius_in_meters]   — bake terrain on the current machine (admin only on servers)
voxcaves_clearbake                 — delete the local bake file
voxcaves_clearbake server          — delete the bake file on the server (admin only)
```

**Multiplayer:** run `voxcaves_bake` once on the server (or as an admin while connected). The server bakes in the background — players can continue playing. When done, new clients connecting will automatically download the bake file with a progress indicator. Live calculation is used as fallback during download.

## Thunderstore

https://thunderstore.io/c/valheim/p/ctogle/ProceduralTerrain/
