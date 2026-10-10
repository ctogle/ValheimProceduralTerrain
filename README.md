# ProceduralTerrain

> **Work in progress — 0.0.1 preview.** Ships novel default terrain for the first four biomes only; the rest stay near-vanilla for now. Expect breaking config and bake-format changes between versions (retune and rebake). Feedback welcome.

Procedural terrain system for Valheim built on top of [Voxheim](https://thunderstore.io/c/valheim/p/Voxheim/Voxheim/). Carves caves, overhangs, arches, and raised stone formations into the voxel world using configurable noise-based profiles per biome.

## Features

- **Hot-reload config** — edit and save `procedural_terrain_biomes.cfg` while in-game; terrain remeshes live without restarting
- **Per-biome noise profiles** — combine named terrain modes into profiles and map each biome to one; biome boundaries blend smoothly
- **SDF bake system** — pre-compute terrain for a radius and save to disk, eliminating zone-load hitching for baked areas
- **Multiplayer bake distribution** — clients auto-download a server's bake on join; playable (with stutter) while the bake generates and downloads, hitch-free once it's in place

## How it works

Two steps:

1. **Configure the terrain** *(optional)*. The world's shape comes from `procedural_terrain_biomes.cfg` — noise modes combined into per-biome profiles. Tuning it well is the deep end and takes effort. **If you just want to play, skip this** — the mod ships with novel defaults for the first four biomes.
2. **Bake it for a playthrough.** Once you've settled on a config, bake the terrain to disk. Generating it at full quality live is expensive, so the bake pre-computes it once and serves it back with no hitching.

### Why bake, and how it's keyed

A bake is tied to **(world seed + config)** — the same seed string and the same cfg always produce the same terrain, so a bake made for them is valid on any world that matches. Change either and it no longer matches, and you'll rebake.

### Dedicated servers

Bake on the server once (`pt_bake`, admin-only). It runs in the background. When a client connects, if it doesn't already have the matching bake it **downloads it from the server** on join (with a progress bar). To skip that download, ship the `.bin` alongside the modpack — same seed+cfg means the client finds it locally.

**During baking and downloading the world is fully playable** — terrain carves live as a fallback — but it **will stutter** until the bake is in place. Once it is, generation is hitch-free.

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

Bake the terrain to disk once your config is set. Commands:

```
pt_bake [radius_in_meters]   — bake terrain on the current machine (admin only on servers)
pt_clearbake                 — delete the local bake file
pt_clearbake server          — delete the bake file on the server (admin only)
```

## Thunderstore

https://thunderstore.io/c/valheim/p/ctogle/ProceduralTerrain/
