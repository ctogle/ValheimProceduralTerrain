using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ProceduralTerrain
{
    // Store a creature-prefab-hash in a placed CreatureSpawner's ZDO and make the spawner honor it, overriding
    // the prefab's own (non-serialized) m_creaturePrefab. Lets ONE spawner chassis produce ANY creature
    // (e.g. Wolf, for which vanilla has no Spawner_ prefab) and survive the ghost→reload cycle.
    // Defensive fix for a vanilla bug: TerrainModifier.GetAllInstances() sorts a static list that can hold a
    // DESTROYED modifier; SortByModifiers then dereferences its null transform and the whole sort throws, killing
    // Heightmap.ApplyModifiers. Heavy object churn (our cave spawns, swarms, prefetch) trips it. Strip destroyed
    // entries before the sort runs. (Unity '== null' is true for destroyed objects.)
    // TODO: this surfaced right when we added wolf-spawner placement/override — before relying on this long-term,
    // verify it's a destroy-timing RACE (transient nulls) vs a LEAK from our ghost-spawned modifier-bearing prefabs
    // (counter: how many we strip per call — flat=race/ok, climbing=leak → strip TerrainModifier at placement instead).
    [HarmonyPatch(typeof(TerrainModifier), "GetAllInstances")]
    static class TerrainModifierNullGuardPatch
    {
        static readonly FieldInfo? _inst = typeof(TerrainModifier).GetField("s_instances", BindingFlags.NonPublic | BindingFlags.Static);
        static void Prefix()
        {
            if (_inst?.GetValue(null) is List<TerrainModifier> list)
                for (int i = list.Count - 1; i >= 0; i--)
                    if (list[i] == null) list.RemoveAt(i);
        }
    }

    // Hook Awake (runs ONCE per spawner at load, NOT the hot per-spawn path) and swap m_creaturePrefab only for
    // spawners carrying our ZDO key AND only if the target is a real Character — so a stray/colliding key can never
    // make a vanilla spawner emit a non-creature (that was the earlier TerrainModifier-churn crash).
    [HarmonyPatch(typeof(CreatureSpawner), "Awake")]
    static class CreatureSpawnerOverridePatch
    {
        internal static readonly int Key = StringExtensionMethods.GetStableHashCode("PT_creature");
        static readonly FieldInfo? _nview = typeof(CreatureSpawner).GetField("m_nview", BindingFlags.NonPublic | BindingFlags.Instance);
        static void Postfix(CreatureSpawner __instance)
        {
            var nv = _nview?.GetValue(__instance) as ZNetView;
            var zdo = nv?.GetZDO(); if (zdo == null) return;
            int h = zdo.GetInt(Key, 0); if (h == 0) return;
            var pf = ZNetScene.instance?.GetPrefab(h);
            if (pf != null && pf.GetComponent<Character>() != null) __instance.m_creaturePrefab = pf;   // creatures only
        }
    }

    // Rung 2 POC (hardcoded, not cfg-driven yet): spawn ADDITIONAL ore/rocks in Mountain caverns, on top of
    // vanilla (we never touch vanilla's own spawns). Runs as a postfix on ZoneSystem.PlaceVegetation, so the
    // SpawnZone gate has already made surfH/_sdfCache ready. One-shot per zone (generate block), server-side,
    // ghost-init-bracketed so ZDOs persist like vegetation. Ore goes on ANY surface crossing; creatures on
    // FLOOR crossings. Probability ramps linearly with depth below the column's surface. Diagnostics heavy —
    // this POC's job is to uncover exact prefab names + verify placement before we design the cfg schema.
    public partial class Plugin
    {
        internal static bool _caveContent = false;   // cfg [Debug] cavecontent

        static bool _ccDumped;
        static int  _ccLogged;

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
            unchecked
            {
                h = (h ^ (uint)d) * 16777619u; h = (h ^ (uint)WorldSeed()) * 16777619u;
                h = (h ^ (uint)a) * 16777619u; h = (h ^ (uint)b) * 16777619u; h = (h ^ (uint)c) * 16777619u;
                // avalanche finalizer (murmur3 fmix32). FNV's LOW bits are poorly mixed, so adjacent integer
                // inputs (ix±1) left the low 24 bits correlated → neighboring props drew near-identical angles.
                h ^= h >> 16; h *= 0x85ebca6bu; h ^= h >> 13; h *= 0xc2b2ae35u; h ^= h >> 16;
            }
            return (h & 0xFFFFFF) / (float)0x1000000;
        }

        // Placed-count + spacing-point bookkeeping, keyed per Placement rule.
        sealed class PlaceState { public int count; public List<Vector3> pts = new(); }

        internal static void PlaceCaveContent(Vector3 zoneCenterPos, object mode, List<GameObject> spawnedObjects)
        {
            if (!_caveContent || _placements.Count == 0) return;
            var wg = WorldGenerator.instance; if (wg == null) return;
            CaveContentDump();
            ResolvePlacements();
            var biome = wg.GetBiome(zoneCenterPos.x, zoneCenterPos.z);
            if (_ccLogged < 25) { _ccLogged++; Log.LogDebug($"[PT-cave] zone center ({zoneCenterPos.x:F0},{zoneCenterPos.z:F0}) biome={biome}"); }

            // Rules active in this biome, split by how they compete: shared object pool, shared spawner pool,
            // and independent channels (Prob>=0). Channels with ZoneChance<1 roll per-zone eligibility once.
            var objPool = new List<Placement>(); var spwPool = new List<Placement>(); var channels = new List<Placement>();
            foreach (var p in _placements)
            {
                if (p.Resolved == null || !p.Biomes.Contains(biome)) continue;
                if (p.Prob >= 0f) channels.Add(p);
                else if (p.Kind == PlacementKind.Spawner) spwPool.Add(p);
                else objPool.Add(p);
            }
            if (objPool.Count == 0 && spwPool.Count == 0 && channels.Count == 0) return;

            float s = ActiveScale();
            var (ox, oz) = ZoneOriginFromWorld(zoneCenterPos.x, zoneCenterPos.z, s);
            if (!_sdfCache.TryGetValue($"{ox},{oz}", out var e)) { if (_ccLogged < 5) { _ccLogged++; Log.LogWarning($"[PT-cave] zone ({ox},{oz}) SDF not cached — skipped."); } return; }

            bool ghost = mode.ToString() == "Ghost";
            var st = new Dictionary<Placement, PlaceState>();
            PlaceState S(Placement p) { if (!st.TryGetValue(p, out var v)) { v = new PlaceState(); st[p] = v; } return v; }
            // per-zone channel eligibility (ZoneChance gate), salted by channel index
            var chanOn = new bool[channels.Count];
            for (int j = 0; j < channels.Count; j++) chanOn[j] = channels[j].ZoneChance >= 1f || CCRand(ox, oz, j, 6) < channels[j].ZoneChance;
            int objPlaced = 0, spwPlaced = 0, width = e.width;

            // place one rule at a crossing, honoring its cap + spacing + flatness; returns true if placed.
            bool TryPlace(Placement p, int ix, int iz, int ci, bool isFloor, bool isSurface, float cyY, float worldX, float worldZ, float yaw)
            {
                if (isSurface && !p.Surface) return false;   // only surface-opted rules place at the top crossing
                if (cyY < p.MinY) return false;              // world-Y floor (e.g. above sea level → raised hammocks only)
                int cap = p.Cap < 0 ? int.MaxValue : p.Cap;
                var ps = S(p); if (ps.count >= cap) return false;
                if (p.FloorOnly && !isFloor) return false;
                float spacing = p.Spacing < 0f ? (p.Kind == PlacementKind.Spawner ? _ccSpawnerSpacing : _ccObjectSpacing) : p.Spacing;
                Vector3 anchor = new Vector3(worldX, cyY, worldZ);
                if (TooClose(ps.pts, anchor, spacing)) return false;
                // floor PROPS (static objects) need a reasonably flat floor so nothing juts out of a near-vertical
                // "floor". Creatures handle slopes fine, so this gate is objects-only (gating spawners here blocked
                // wolves on sloped cave floors).
                if (p.Kind == PlacementKind.Object && p.FloorOnly && CaveFloorNormal(e, ix, iz, width, cyY, s).y < p.MinNormalY) return false;
                // Rock-cover check: the terrain surface (surfH — the SAME field vegetation-conform uses, not a
                // reinvented one) must sit >= Cover metres above the deposit across its lateral footprint, so it's
                // buried and only exposed where a tunnel cuts it. Samples the own column + 4 laterals at radius Cover.
                if (p.Cover > 0f)
                {
                    float c = p.Cover, minClear = float.PositiveInfinity;
                    float[] dxs = { 0f, c, -c, 0f, 0f };
                    float[] dzs = { 0f, 0f, 0f, c, -c };
                    int hits = 0;
                    for (int d = 0; d < 5; d++)
                        if (Plugin.TrySampleSurfaceY(worldX + dxs[d], worldZ + dzs[d], out float sh))
                        { hits++; float clr = sh - cyY; if (clr < minClear) minClear = clr; }
                    if (hits == 0 || minClear < c) return false;   // exposed (or no surface data) → don't place
                    if (_ccLogged < 60) { _ccLogged++; Log.LogDebug($"[PT-cover] {p.Name} placed depth-clearance={minClear:F1}m (cover={c:F0}, hits={hits}) at ({worldX:F0},{cyY:F0},{worldZ:F0})"); }
                }

                Vector3 pos = anchor; Quaternion rot;
                // orientation: spawners/upright = world-up + yaw; floor objects tilt to the floor normal (any
                // orientation). Embed is a SEPARATE position sink (below), so tilted props can still bury their base.
                if (p.Kind == PlacementKind.Spawner || p.Upright) rot = Quaternion.Euler(0f, yaw, 0f);
                else if (isFloor) rot = Quaternion.FromToRotation(Vector3.up, CaveFloorNormal(e, ix, iz, width, cyY, s)) * Quaternion.Euler(0f, yaw, 0f);
                else rot = Quaternion.Euler(0f, yaw, 0f);
                if (p.Embed > 0f) pos = new Vector3(worldX, cyY - p.Embed, worldZ);   // sink to guarantee ground contact
                if (p.RandTilt > 0f)   // extra random X/Z tilt about a random horizontal axis → varied orientation (roots don't all lie identically)
                {
                    float ta = CCRand(ox + ix, oz + iz, ci, 31) * p.RandTilt;
                    float aa = CCRand(ox + ix, oz + iz, ci, 37) * 6.2831853f;
                    rot = Quaternion.AngleAxis(ta, new Vector3(Mathf.Cos(aa), 0f, Mathf.Sin(aa))) * rot;
                }
                // lift off the crossing into the air side (flying spawners born on the surface clip into rock →
                // Voxheim "unburies" them every tick). Air is up on a floor crossing, down on a ceiling crossing.
                if (p.Lift != 0f) pos += (isFloor ? Vector3.up : Vector3.down) * p.Lift;

                if (!SpawnCave(p.Resolved!, pos, rot, ghost, spawnedObjects, p.CreatureHash)) return false;
                ps.count++; ps.pts.Add(anchor);
                return true;
            }

            // weighted pick among pool rules eligible at this crossing (eff weight = Weight * depth ramp).
            Placement? PickPool(List<Placement> pool, bool isFloor, float depth, int ix, int iz, int ci, int salt)
            {
                float total = 0f;
                for (int i = 0; i < pool.Count; i++)
                {
                    var p = pool[i]; if (p.FloorOnly && !isFloor) continue;
                    float r = p.Ramp(depth); if (r <= 0f) continue;
                    total += p.Weight * r;
                }
                if (total <= 0f) return null;
                float roll = CCRand(ox + ix, oz + iz, ci, salt) * total, acc = 0f;
                for (int i = 0; i < pool.Count; i++)
                {
                    var p = pool[i]; if (p.FloorOnly && !isFloor) continue;
                    float r = p.Ramp(depth); if (r <= 0f) continue;
                    acc += p.Weight * r; if (roll < acc) return p;
                }
                return null;
            }

            for (int ix = 0; ix < width; ix++)
            for (int iz = 0; iz < width; iz++)
            {
                int oc = ix * width + iz;
                int start = e.colStart[oc]; if (start < 0) continue;
                int len = e.colLen[oc], off = e.colOff[oc];
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
                    float depth = surfaceY - cyY;
                    bool isSurface = depth <= 1f;         // the top crossing — only surface-opted rules take it
                    if (isSurface) depth = 0f;            // normalize so DepthMin=0 ramps fire
                    bool isFloor = aSolid && !bSolid;   // solid below, air above = standing floor
                    float yaw = CCRand(ox + ix, oz + iz, ci, 7) * 360f;

                    // shared OBJECT pool: pool-wide fire chance, then weighted pick scaled by depth ramp
                    if (objPlaced < _ccObjectCap && objPool.Count > 0 && CCRand(ox + ix, oz + iz, ci, 1) < _ccObjectProb)
                    {
                        var p = PickPool(objPool, isFloor, depth, ix, iz, ci, 3);
                        if (p != null && TryPlace(p, ix, iz, ci, isFloor, isSurface, cyY, worldX, worldZ, yaw)) objPlaced++;
                    }
                    // shared SPAWNER pool
                    if (spwPlaced < _ccSpawnerCap && spwPool.Count > 0 && CCRand(ox + ix, oz + iz, ci, 2) < _ccSpawnerProb)
                    {
                        var p = PickPool(spwPool, isFloor, depth, ix, iz, ci, 10);
                        if (p != null && TryPlace(p, ix, iz, ci, isFloor, isSurface, cyY, worldX, worldZ, yaw)) spwPlaced++;
                    }
                    // independent channels (bats, giants, …): own per-crossing prob × depth ramp
                    for (int j = 0; j < channels.Count; j++)
                    {
                        if (!chanOn[j]) continue;
                        var p = channels[j];
                        float r = p.Ramp(depth); if (r <= 0f) continue;
                        if (CCRand(ox + ix, oz + iz, ci, 20 + j) >= p.Prob * r) continue;
                        TryPlace(p, ix, iz, ci, isFloor, isSurface, cyY, worldX, worldZ, yaw);
                    }
                }
            }
            if (st.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                int tot = 0;
                foreach (var kv in st) if (kv.Value.count > 0) { sb.Append($" {kv.Key.Name}={kv.Value.count}"); tot += kv.Value.count; }
                if (tot > 0) Log.LogDebug($"[PT-cave] zone ({ox},{oz}):{sb}");
            }
        }

        static bool TooClose(List<Vector3> pts, Vector3 p, float min)
        {
            float m2 = min * min;
            for (int i = 0; i < pts.Count; i++) if ((pts[i] - p).sqrMagnitude < m2) return true;
            return false;
        }

        static bool SpawnCave(GameObject prefab, Vector3 pos, Quaternion rot, bool ghost, List<GameObject> spawnedObjects, int overrideCreature = 0)
        {
            try
            {
                if (ghost) ZNetView.StartGhostInit();
                var go = UnityEngine.Object.Instantiate(prefab, pos, rot);
                if (ghost) { spawnedObjects.Add(go); ZNetView.FinishGhostInit(); }
                if (overrideCreature != 0)
                {
                    // Two steps, because CreatureSpawner.Awake already ran during Instantiate (before the ZDO
                    // existed) so its read of our key found nothing:
                    //  1) stamp the ZDO — picked up by the Awake patch on every FUTURE load of this spawner.
                    //  2) set m_creaturePrefab directly on THIS instance — applies now, for the current session.
                    go.GetComponent<ZNetView>()?.GetZDO()?.Set(CreatureSpawnerOverridePatch.Key, overrideCreature);
                    var cs = go.GetComponent<CreatureSpawner>();
                    var pf = ZNetScene.instance?.GetPrefab(overrideCreature);
                    if (cs != null && pf != null && pf.GetComponent<Character>() != null) cs.m_creaturePrefab = pf;
                }
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
