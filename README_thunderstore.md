# ProceduralTerrain

> **Work in progress.** Expect rough edges, breaking config changes, and incomplete documentation.

Procedural terrain system for Valheim built on top of Voxheim. Carves caves, overhangs, arches, and raised stone formations into the voxel world using configurable noise-based profiles per biome.

- Per-biome terrain profiles with hot-reload on save
- Three independent terrain modes: tunnels (A), surface bands (B), raised density terrain (C)
- Biome blending at boundaries
- SDF bake system: pre-compute terrain for a radius and save to disk, eliminating zone-load hitching for baked areas

---

## Requirements

- [BepInExPack Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/)
- [Voxheim](https://thunderstore.io/c/valheim/p/Voxheim/Voxheim/)

---

## Configuration

Biome profiles live in `BepInEx/config/procedural_terrain_biomes.cfg` and hot-reload on save. A fully commented example config is included.

---

## Baking

For smooth zone load times across a large area, pre-compute and save SDF data to disk:

```
voxcaves_bake [radius_in_meters]
```

The bake file is world- and config-specific. For multiplayer, generate it once and distribute it to players — they place it in `BepInEx/config/` and it loads automatically on join.

---

## Source

https://github.com/ctogle/ValheimProceduralTerrain
