using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralTerrain
{
    // Rung 2 POC (hardcoded, not cfg-driven yet): spawn ADDITIONAL ore/rocks in Mountain caverns, on top of
    // vanilla (we never touch vanilla's own spawns). Runs as a postfix on ZoneSystem.PlaceVegetation, so the
    // SpawnZone gate has already made surfH/_sdfCache ready. One-shot per zone (generate block), server-side,
    // ghost-init-bracketed so ZDOs persist like vegetation. Ore goes on ANY surface crossing; creatures on
    // FLOOR crossings. Probability ramps linearly with depth below the column's surface. Diagnostics heavy —
    // this POC's job is to uncover exact prefab names + verify placement before we design the cfg schema.
    public partial class Plugin
    {
        internal static bool _caveContent = false;   // cfg [Debug] cavecontent

        // Tunables (deliberately a bit hot; turn down later)
        const float CC_DepthFull  = 60f;     // metres below surface where the ramp reaches full probability
        const float CC_OreMaxProb = 0.006f;  // per-crossing chance at full depth (hunt a little)
        const float CC_WolfMaxProb = 0.003f; // per-floor-crossing chance at full depth
        const int   CC_OreCap     = 6;       // HARD cap per zone (~1/5 of the earlier test density)
        const int   CC_WolfCap    = 3;
        const float CC_OreSpacing   = 6f;    // min metres between placed ore (no clumped silver piles)
        const float CC_WolfSpacing  = 24f;
        const float CC_GiantSpacing = 40f;

        // Resource → candidate prefab names (first that resolves wins). Logged once so we learn the real names.
        // tag, prefab names, relative weight, embed depth (metres buried below a FLOOR crossing; 0 = sits on surface,
        // any crossing). Buried deposits (silver/scrap/copper) only spawn on floors, dug out like vanilla + Wishbone-findable.
        static readonly (string tag, string[] cands, int weight, float embed)[] _oreCands =
        {
            ("tin",      new[]{ "MineRock_Tin" },       10, 0f),  // fine on the surface
            ("flint",    new[]{ "Pickable_Flint" },      7, 0f),  // interactable pile, not the raw item drop
            ("obsidian", new[]{ "MineRock_Obsidian" },   4, 0f),
            ("copper",   new[]{ "rock4_copper" },        2, 3.0f), // the LARGE BF deposit; sink a few metres, huge top exposed
            ("muddyscrap", new[]{ "mudpile2", "mudpile" }, 3, 4.0f), // sunk ~fully, dug out + Wishbone-findable
            ("silver",   new[]{ "silvervein" },          2, 4.0f), // sunk ~fully, Wishbone-findable
        };
        static readonly string[] _creatureCands = { "Wolf" };

        // Mistlands giant-corpse SCENERY, deep only. Confirmed names. giant_skull/giant_brain EXCLUDED — those
        // carry late-game resources; sword/helmet/ribs/arm are just scenery (safe for early mountains).
        static readonly (string tag, string[] cands)[] _giantCands =
        {
            ("giant_sword",  new[]{ "giant_sword1", "giant_sword2" }),
            ("giant_helmet", new[]{ "giant_helmet1", "giant_helmet2" }),
        };
        static GameObject[]? _giantResolved;
        const float CC_GiantMinDepth = 40f;    // only this far below surface
        const float CC_GiantProb     = 0.02f;  // per deep flat-floor crossing
        const int   CC_GiantCap      = 1;       // per zone (they're enormous)
        const float CC_GiantZoneChance = 0.1f;  // only ~1 in 10 mountain zones is even eligible for a giant

        static GameObject[]? _oreResolved;   // parallel to _oreCands
        static GameObject?   _wolfResolved;
        static bool _ccDumped;
        static int  _ccLogged;

        static GameObject? Resolve(string[] cands)
        {
            var zs = ZNetScene.instance; if (zs == null) return null;
            foreach (var n in cands) { var p = zs.GetPrefab(n); if (p != null) return p; }
            return null;
        }

        static void CaveContentResolve()
        {
            if (_oreResolved != null) return;
            _oreResolved = new GameObject[_oreCands.Length];
            for (int i = 0; i < _oreCands.Length; i++)
            {
                _oreResolved[i] = Resolve(_oreCands[i].cands)!;
                Log.LogInfo($"[PT-cave] resolve {_oreCands[i].tag}: {(_oreResolved[i] ? _oreResolved[i].name : "NONE of [" + string.Join(",", _oreCands[i].cands) + "]")}");
            }
            _wolfResolved = Resolve(_creatureCands);
            Log.LogInfo($"[PT-cave] resolve wolf: {(_wolfResolved ? _wolfResolved!.name : "NONE")}");
            _giantResolved = new GameObject[_giantCands.Length];
            for (int i = 0; i < _giantCands.Length; i++)
            {
                _giantResolved[i] = Resolve(_giantCands[i].cands)!;
                Log.LogInfo($"[PT-cave] resolve {_giantCands[i].tag}: {(_giantResolved[i] ? _giantResolved[i].name : "NONE of [" + string.Join(",", _giantCands[i].cands) + "]")}");
            }
        }

        // One-time dump of all ZNetScene prefab names matching ore/rock keywords — so we discover exact names.
        static void CaveContentDump()
        {
            if (_ccDumped) return; _ccDumped = true;
            var zs = ZNetScene.instance; if (zs == null) { _ccDumped = false; return; }
            string[] kw = { "mine", "ore", "vein", "copper", "tin", "silver", "obsidian", "flint", "mud", "scrap", "rock",
                            "giant", "sword", "helm", "skull", "rib", "bone", "corpse", "remains" };
            var hits = new List<string>();
            try
            {
                var f = typeof(ZNetScene).GetField("m_namedPrefabs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (f?.GetValue(zs) is System.Collections.IDictionary dict)
                    foreach (var k in dict.Values)
                        if (k is GameObject go) { string nm = go.name.ToLowerInvariant(); foreach (var w in kw) if (nm.Contains(w)) { hits.Add(go.name); break; } }
            }
            catch (Exception e) { Log.LogWarning($"[PT-cave] prefab dump failed: {e.Message}"); }
            hits.Sort();
            Log.LogInfo($"[PT-cave] prefab names matching ore/rock keywords ({hits.Count}): {string.Join(", ", hits)}");
        }

        // Salt (d) + seed FIRST so different d values produce fully independent sequences — otherwise the place
        // gate (d=1) and the ore pick (d=3) share the a,b,c hash prefix and correlate (→ some ores never picked).
        static float CCRand(int a, int b, int c, int d)
        {
            uint h = 2166136261u;
            unchecked { h = (h ^ (uint)d) * 16777619u; h = (h ^ (uint)WorldSeed()) * 16777619u; h = (h ^ (uint)a) * 16777619u; h = (h ^ (uint)b) * 16777619u; h = (h ^ (uint)c) * 16777619u; }
            return (h & 0xFFFFFF) / (float)0x1000000;
        }

        internal static void PlaceCaveContent(Vector3 zoneCenterPos, object mode, List<GameObject> spawnedObjects)
        {
            if (!_caveContent) return;
            var wg = WorldGenerator.instance; if (wg == null) return;
            CaveContentDump();
            CaveContentResolve();
            var biome = wg.GetBiome(zoneCenterPos.x, zoneCenterPos.z);
            if (_ccLogged < 25) { _ccLogged++; Log.LogInfo($"[PT-cave] zone center ({zoneCenterPos.x:F0},{zoneCenterPos.z:F0}) biome={biome}"); }
            if (biome != Heightmap.Biome.Mountain) return;

            float s = ActiveScale();
            var (ox, oz) = ZoneOriginFromWorld(zoneCenterPos.x, zoneCenterPos.z, s);
            if (!_sdfCache.TryGetValue($"{ox},{oz}", out var e)) { if (_ccLogged < 5) { _ccLogged++; Log.LogWarning($"[PT-cave] zone ({ox},{oz}) SDF not cached — skipped."); } return; }

            bool ghost = mode.ToString() == "Ghost";
            bool giantEligible = CCRand(ox, oz, 0, 6) < CC_GiantZoneChance;   // most zones get NO giant
            int width = e.width, placedOre = 0, placedWolf = 0, placedGiant = 0;
            var perType = new int[_oreCands.Length];
            var oreP = new List<Vector3>(); var wolfP = new List<Vector3>(); var giantP = new List<Vector3>();
            for (int ix = 0; ix < width; ix++)
            for (int iz = 0; iz < width; iz++)
            {
                if (placedOre >= CC_OreCap && placedWolf >= CC_WolfCap && placedGiant >= CC_GiantCap) break;
                int oc = ix * width + iz;
                int start = e.colStart[oc]; if (start < 0) continue;
                int len = e.colLen[oc], off = e.colOff[oc];
                // crossings within the deviating band: sign change between consecutive voxels
                // first pass: surface = highest crossing (largest cy)
                int surfCy = -1;
                for (int k = 1; k < len; k++)
                    if ((e.band[off + k - 1] < 0) != (e.band[off + k] < 0)) surfCy = start + k;
                if (surfCy < 0) continue;
                float surfaceY = (e.extLo + surfCy) * s;
                float worldX = (ox + ix) * s, worldZ = (oz + iz) * s;

                int ci = 0;
                for (int k = 1; k < len; k++)
                {
                    bool aSolid = e.band[off + k - 1] < 0, bSolid = e.band[off + k] < 0;
                    if (aSolid == bSolid) continue;
                    int cy = start + k; ci++;
                    float cyY = (e.extLo + cy) * s;
                    float depth = surfaceY - cyY; if (depth <= 1f) continue;   // skip the surface crossing itself
                    float ramp = Mathf.Clamp01(depth / CC_DepthFull);
                    bool isFloor = aSolid && !bSolid;   // solid below, air above = standing floor
                    float yaw = CCRand(ox + ix, oz + iz, ci, 7) * 360f;
                    // ground props tilt to the local cave-floor normal; wall/ceiling crossings stay upright.
                    Quaternion rot = isFloor
                        ? Quaternion.FromToRotation(Vector3.up, CaveFloorNormal(e, ix, iz, width, cyY, s)) * Quaternion.Euler(0f, yaw, 0f)
                        : Quaternion.Euler(0f, yaw, 0f);

                    Vector3 pOre = new Vector3(worldX, cyY, worldZ);
                    // ore: any crossing
                    if (placedOre < CC_OreCap && CCRand(ox + ix, oz + iz, ci, 1) < CC_OreMaxProb * ramp && !TooClose(oreP, pOre, CC_OreSpacing))
                    {
                        int oi = PickOreIndex(ox + ix, oz + iz, ci);
                        float embed = oi >= 0 ? _oreCands[oi].embed : 0f;
                        // buried deposits (embed>0) only on floors; bury them into the rock so they're dug out + Wishbone-findable.
                        if (oi >= 0 && (embed == 0f || isFloor))
                        {
                            Vector3 p = embed > 0f ? new Vector3(worldX, cyY - embed, worldZ) : pOre;
                            Quaternion orot = embed > 0f ? Quaternion.Euler(0f, yaw, 0f) : rot;   // buried: keep upright, don't tilt
                            if (SpawnCave(_oreResolved![oi], p, orot, ghost, spawnedObjects))
                            {
                                placedOre++; perType[oi]++; oreP.Add(pOre);
                                if (_ccLogged < 15) { _ccLogged++; Log.LogInfo($"[PT-cave] ore {_oreResolved[oi].name} at ({worldX:F0},{p.y:F0},{worldZ:F0}) depth={depth:F0} embed={embed:F0}"); }
                            }
                        }
                    }
                    // creature spawner POC: floor crossings only
                    if (isFloor && placedWolf < CC_WolfCap && _wolfResolved != null && CCRand(ox + ix, oz + iz, ci, 2) < CC_WolfMaxProb * ramp)
                    {
                        Vector3 pW = new Vector3(worldX, cyY + 0.5f, worldZ);
                        if (!TooClose(wolfP, pW, CC_WolfSpacing) && SpawnCave(_wolfResolved, pW, Quaternion.Euler(0f, yaw, 0f), ghost, spawnedObjects)) { placedWolf++; wolfP.Add(pW); }
                    }

                    // giant corpse scenery: deep, flat floor only, very rare, capped. Stand mostly VERTICAL with a
                    // slight monument lean (not tilted flat to the floor like ore).
                    if (giantEligible && isFloor && placedGiant < CC_GiantCap && depth >= CC_GiantMinDepth && CCRand(ox + ix, oz + iz, ci, 4) < CC_GiantProb && !TooClose(giantP, pOre, CC_GiantSpacing)
                        && CaveFloorNormal(e, ix, iz, width, cyY, s).y > 0.85f)   // flat enough to stand
                    {
                        int gi = PickGiantIndex(ox + ix, oz + iz, ci);
                        float tx = (CCRand(ox + ix, oz + iz, ci, 8) - 0.5f) * 24f;   // ±12° lean
                        float tz = (CCRand(ox + ix, oz + iz, ci, 9) - 0.5f) * 24f;
                        Quaternion grot = Quaternion.Euler(tx, yaw, tz);
                        if (gi >= 0 && SpawnCave(_giantResolved![gi], pOre, grot, ghost, spawnedObjects))
                        {
                            placedGiant++; giantP.Add(pOre);
                            Log.LogInfo($"[PT-cave] giant {_giantResolved[gi].name} at ({worldX:F0},{cyY:F0},{worldZ:F0}) depth={depth:F0}");
                        }
                    }
                }
            }
            if (placedOre + placedWolf > 0)
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < perType.Length; i++) if (perType[i] > 0) sb.Append($" {_oreCands[i].tag}={perType[i]}");
                Log.LogInfo($"[PT-cave] zone ({ox},{oz}): +{placedOre} ore ({sb}), +{placedWolf} wolves, +{placedGiant} giant");
            }
        }

        static int PickGiantIndex(int a, int b, int c)
        {
            if (_giantResolved == null) return -1;
            var idx = new List<int>();
            for (int i = 0; i < _giantResolved.Length; i++) if (_giantResolved[i] != null) idx.Add(i);
            if (idx.Count == 0) return -1;
            return idx[(int)(CCRand(a, b, c, 5) * idx.Count) % idx.Count];
        }

        // weighted pick among RESOLVED ores (skips NONE slots), or -1 if none resolved.
        static int PickOreIndex(int a, int b, int c)
        {
            if (_oreResolved == null) return -1;
            int total = 0;
            for (int i = 0; i < _oreResolved.Length; i++) if (_oreResolved[i] != null) total += _oreCands[i].weight;
            if (total == 0) return -1;
            float r = CCRand(a, b, c, 3) * total; int acc = 0;
            for (int i = 0; i < _oreResolved.Length; i++)
                if (_oreResolved[i] != null) { acc += _oreCands[i].weight; if (r < acc) return i; }
            return -1;
        }

        static bool TooClose(List<Vector3> pts, Vector3 p, float min)
        {
            float m2 = min * min;
            for (int i = 0; i < pts.Count; i++) if ((pts[i] - p).sqrMagnitude < m2) return true;
            return false;
        }

        static bool SpawnCave(GameObject prefab, Vector3 pos, Quaternion rot, bool ghost, List<GameObject> spawnedObjects)
        {
            try
            {
                if (ghost) ZNetView.StartGhostInit();
                var go = UnityEngine.Object.Instantiate(prefab, pos, rot);
                if (ghost) { spawnedObjects.Add(go); ZNetView.FinishGhostInit(); }
                return true;
            }
            catch (Exception ex) { Log.LogWarning($"[PT-cave] spawn {prefab.name} failed: {ex.Message}"); if (ghost) ZNetView.FinishGhostInit(); return false; }
        }

        // nearest sign-crossing Y in a neighbour column (for a local floor-slope finite diff), fallback if none.
        static float NearestCrossingY(Plugin.ZoneField e, int nix, int niz, int width, float targetY, float s, float fallback)
        {
            if (nix < 0 || nix >= width || niz < 0 || niz >= width) return fallback;
            int oc = nix * width + niz; int start = e.colStart[oc]; if (start < 0) return fallback;
            int len = e.colLen[oc], off = e.colOff[oc]; float best = fallback, bestd = 1e9f;
            for (int k = 1; k < len; k++)
                if ((e.band[off + k - 1] < 0) != (e.band[off + k] < 0))
                { float y = (e.extLo + start + k) * s; float d = Mathf.Abs(y - targetY); if (d < bestd) { bestd = d; best = y; } }
            return best;
        }

        // local cave-floor normal from the 4 neighbour columns' nearest crossing to this one.
        static Vector3 CaveFloorNormal(Plugin.ZoneField e, int ix, int iz, int width, float cyY, float s)
        {
            float hpx = NearestCrossingY(e, ix + 1, iz, width, cyY, s, cyY), hmx = NearestCrossingY(e, ix - 1, iz, width, cyY, s, cyY);
            float hpz = NearestCrossingY(e, ix, iz + 1, width, cyY, s, cyY), hmz = NearestCrossingY(e, ix, iz - 1, width, cyY, s, cyY);
            float dydx = (hpx - hmx) / (2f * s), dydz = (hpz - hmz) / (2f * s);
            return new Vector3(-dydx, 1f, -dydz).normalized;
        }
    }
}
