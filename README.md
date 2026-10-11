# ProceduralTerrain

> **Work in progress — 0.0.1 preview.** Ships novel default terrain for the first four biomes only; the rest stay near-vanilla for now. Expect breaking config and bake-format changes between versions (retune and rebake). Feedback welcome.

Procedural terrain system for Valheim built on top of [Voxheim](https://thunderstore.io/c/valheim/p/Voxheim/Voxheim/). Carves caves, overhangs, arches, and raised stone formations into the voxel world using configurable noise-based profiles per biome.

## Features

- **Hot-reload config** — edit and save the config files while in-game; terrain remeshes live without restarting
- **Per-biome noise profiles** — combine named terrain modes into profiles and map each biome to one; biome boundaries blend smoothly
- **Cave content** — ore, scenery, and creature spawners placed inside carved caverns (on top of vanilla), configurable per prefab and biome
- **Locations preserved** — world locations (dungeons, bosses, villages, etc.) sit at their exact vanilla positions, and the terrain around them is protected during generation so they stay accessible
- **SDF bake system** — pre-compute terrain for a radius and save to disk, so baked areas load cheaply instead of carving live
- **Multiplayer bake distribution** — clients auto-download a server's bake on join; playable (with stutter) while the bake generates and downloads, hitch-free once it's in place

## How it works

Two steps:

1. **Configure the terrain** *(optional)*. The world's shape comes from `biomes.cfg` — noise modes combined into per-biome profiles. Tuning it well is the deep end and takes effort. **If you just want to play, skip this** — the mod writes tuned defaults on first launch (novel terrain for the first four biomes).
2. **Bake it for a playthrough.** Once you've settled on a config, bake the terrain to disk. Generating it at full quality live is expensive, so the bake pre-computes it once and serves it back cheaply.

### Why bake, and how it's keyed

A bake is tied to **(world seed + config)** — the same seed string and the same cfg always produce the same terrain, so a bake made for them is valid on any world that matches. Change either and it no longer matches, and you'll rebake.

### Dedicated servers

Bake on the server once (`pt_bake`, admin-only). It runs in the background. When a client connects, if it doesn't already have the matching bake it **downloads it from the server** on join (with a progress bar); clients that already have an identical copy skip the download.

**During baking and downloading the world is fully playable** — terrain carves live as a fallback — but it **will stutter** until the bake is in place. Once it is, zone loading is cheap.

## Requirements

- [BepInExPack Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/)
- [Voxheim](https://thunderstore.io/c/valheim/p/Voxheim/Voxheim/)

## Configuration

Config lives in `BepInEx/config/ProceduralTerrain/` as two files, both fully commented and both hot-reloading on save. The mod writes them with tuned defaults on first launch; edits are never overwritten on update.

**`biomes.cfg`** — the terrain *shape*:
- `[Bake]` — bake resolution, format, and compression
- `[Debug]` — global terrain toggles, blend radius, live carve resolution, and the optional forced seed
- `[Profile:name]` / `[BiomeName]` — named noise profiles and the biome→profile assignments
- `[Mode:name]` / `[Pipeline]` — the terrain primitives and the order they run in

**`placements.cfg`** — *what* gets placed in the world:
- `[Vegetation]` — whether trees/bushes place at all, and how they conform to carved ground
- `[CaveContent]` — master toggle plus the shared ore/spawner pool densities and per-zone caps
- `[Placement:name]` — one rule per thing spawned in caves (ore, scenery, or creature spawner): its prefab/chassis, biome, weight, and depth/placement options

## Baking

Bake the terrain to disk once your config is set. Commands:

```
pt_bake [radius_in_meters]   — bake terrain on the current machine (admin only on servers)
pt_clearbake                 — delete the local bake file
pt_clearbake server          — delete the bake file on the server (admin only)
```

## Thunderstore

https://thunderstore.io/c/valheim/p/ctogle/ProceduralTerrain/
