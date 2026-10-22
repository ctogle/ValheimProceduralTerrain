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

    // Inert decor: strip the components that make a prop remove or transform itself (building-ivy WearNTear
    // collapse, Plant grow/wither, Pickable harvest) so it persists as static decoration. Flagged per-instance via
    // a ZDO int and re-applied on EVERY load (the prefab respawns fresh with those components each session).
    [HarmonyPatch(typeof(ZNetView), "Awake")]
    static class InertDecorPatch
    {
        internal static readonly int Key = StringExtensionMethods.GetStableHashCode("PT_inert");
        static int _logged;
        static void Postfix(ZNetView __instance)
        {
            var zdo = __instance.GetZDO(); if (zdo == null) return;
            if (zdo.GetInt(Key, 0) == 0) return;                 // cheap: one int read for non-inert objects
            StripInertComponents(__instance.gameObject, ref _logged);
        }
        internal static void StripInertComponents(GameObject go, ref int logged)
        {
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                string n = mb.GetType().Name;
                if (n == "WearNTear" || n == "Plant" || n == "TreeBase" || n == "Pickable" || n == "Vine" || n == "TimedDestruction")
                {
                    if (logged < 30) { logged++; Plugin.Log.LogInfo($"[PT-cave] inert: stripped {n} from {go.name}"); }
                    UnityEngine.Object.Destroy(mb);
                }
            }
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
                if (p.Resolved.Length == 0 || !p.Biomes.Contains(biome)) continue;
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

            // Atrium gate: if any active rule confines to atriums, gather the carved-atrium centres near THIS zone
            // once (the carve records every emitted dome; this zone's own dome, if any, was just carved). Small list
            // → cheap per-column distance test below.
            float maxAtriumGate = 0f;
            foreach (var p in objPool)  if (p.AtriumOnly > maxAtriumGate) maxAtriumGate = p.AtriumOnly;
            foreach (var p in spwPool)  if (p.AtriumOnly > maxAtriumGate) maxAtriumGate = p.AtriumOnly;
            foreach (var p in channels) if (p.AtriumOnly > maxAtriumGate) maxAtriumGate = p.AtriumOnly;
            List<Vector3>? nearAtriums = null;
            if (maxAtriumGate > 0f)
            {
                nearAtriums = new List<Vector3>();
                float filt = maxAtriumGate + 96f, f2 = filt * filt;
                foreach (var a in Plugin.SnapshotCarvedAtriums())
                { float dx = a.x - zoneCenterPos.x, dz = a.z - zoneCenterPos.z; if (dx * dx + dz * dz <= f2) nearAtriums.Add(a); }
            }

            // Light field (0..1 per column): fade decoration out of the dark toward sky-openings. Built once per zone.
            float maxLightRange = 0f;
            foreach (var p in objPool)  if (p.LightRange > maxLightRange) maxLightRange = p.LightRange;
            foreach (var p in spwPool)  if (p.LightRange > maxLightRange) maxLightRange = p.LightRange;
            foreach (var p in channels) if (p.LightRange > maxLightRange) maxLightRange = p.LightRange;
            float[]? light = maxLightRange > 0f ? ComputeLightField(e, width, s, maxLightRange, nearAtriums ?? Plugin.SnapshotCarvedAtriums(), ox, oz) : null;

            // place one rule at a crossing, honoring its cap + spacing + flatness; returns true if placed.
            bool TryPlace(Placement p, int ix, int iz, int ci, bool isFloor, bool isSurface, float cyY, float worldX, float worldZ, float yaw)
            {
                if (isSurface && !p.Surface) return false;   // only surface-opted rules place at the top crossing
                if (p.Hang && (isFloor || isSurface)) return false;   // hanging props need a ceiling crossing (air below, solid above)
                if (cyY < p.MinY || cyY > p.MaxY) return false;   // world-Y band (e.g. sewer level only, below the strata galleries)
                if (p.ClusterFreq > 0f && Plugin._noisePool != null)  // patchy spatial structure: only place where the noise field clears the threshold
                {
                    float cn = Plugin._noisePool[1].GetNoise(worldX * p.ClusterFreq, 0f, worldZ * p.ClusterFreq);
                    if (cn < p.ClusterThresh) return false;
                }
                if (p.LightRange > 0f && light != null)   // fade out of the dark: keep ∝ proximity to a sky-opening
                {
                    float lf = light[ix * width + iz];
                    if (lf <= 0f || CCRand(ox + ix, oz + iz, ci, 53) >= lf) return false;
                }
                if (p.BiomePure > 0f && wg.GetBiomeArea(new Vector3(worldX, 0f, worldZ)) == Heightmap.BiomeArea.Edge) return false;  // interior only, off the boundary
                if (p.AtriumOnly > 0f)   // confine to atrium rooms: within AtriumOnly metres (XZ) of a carved dome centre
                {
                    if (nearAtriums == null || nearAtriums.Count == 0) return false;
                    float r2 = p.AtriumOnly * p.AtriumOnly; bool near = false;
                    foreach (var a in nearAtriums) { float dx = a.x - worldX, dz = a.z - worldZ; if (dx * dx + dz * dz <= r2) { near = true; break; } }
                    if (!near) return false;
                }
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
                // orientation). Embed sinks ALONG that same normal (below), so a tilted prop buries its base into
                // the slope instead of floating — straight-down embed only works on flat ground.
                // Surface normal for THIS crossing: floor → up-ish, ceiling → down-ish, surface/top → world up.
                // Used for both orientation and embed, so a rule works on floor AND ceiling crossings (no hang needed).
                Vector3 surfN = isSurface ? Vector3.up
                              : isFloor   ? CaveFloorNormal(e, ix, iz, width, cyY, s)
                              :             CaveCeilingNormal(e, ix, iz, width, cyY, s);
                // Orientation modes (all with a live rotx/y/z offset applied last to correct a model's axis):
                //   NoAlign / spawner / upright → identity + yaw (+offset)
                //   FaceNormal                  → align +Z (forward) to the surface normal (flat decals/ivy); yaw rolls about it.
                //     Uses FromToRotation (minimal rotation) NOT LookRotation — LookRotation is degenerate when the
                //     normal points straight down (ceiling) and produced the garbage roll.
                //   default (hang or floor)     → align +Y (up) to the surface normal (standing/hanging props)
                Quaternion offset = Quaternion.Euler(p.RotX, p.RotY, p.RotZ);
                Vector3 embedN = Vector3.up;
                if (p.NoAlign || p.Kind == PlacementKind.Spawner || p.Upright)
                    rot = Quaternion.Euler(0f, yaw, 0f) * offset;
                else if (p.FaceNormal)
                {
                    // TRUE normal from the density gradient (accurate on walls/overhangs, not just floors/ceilings).
                    // Build a FULL deterministic frame like the blob stamps (NOT a single-axis align + random roll,
                    // which spun flat ivy randomly): face (+Z) along -N so the ivy's -Z face points OUT along the
                    // normal, and the ivy's up (+Y) along world-up projected onto the surface plane → ivy stands
                    // "upright" on walls and lies flat on floors/ceilings with a consistent orientation.
                    int cyAbs = Mathf.RoundToInt(cyY / s) - e.extLo;
                    Vector3 gn = GradientNormalSnap(e, ix, iz, cyAbs, width, out float distVox, out float gmag);
                    // guard: degenerate gradient (flat coarse facet) or a long snap = unreliable → skip, don't float it
                    if (gmag < 1.0f || Mathf.Abs(distVox) > 3f) { if (_ivyDbg < 20) { _ivyDbg++; Log.LogInfo($"[PT-ivy] #{_ivyDbg} SKIP gm={gmag:F2} snapVox={distVox:F2}"); } return false; }
                    anchor -= gn * (distVox * s);   // SNAP onto the real surface (column x/z is only on-surface for flat spots)
                    pos = anchor;
                    Vector3 upRef = Mathf.Abs(gn.y) > 0.9f ? Vector3.forward : Vector3.up;   // avoid degenerate up∥N on floors/ceilings
                    Vector3 inUp = (upRef - Vector3.Dot(upRef, gn) * gn).normalized;
                    rot = Quaternion.LookRotation(-gn, inUp) * offset;   // +Z→-N (face -Z points out), +Y→in-plane up
                    embedN = gn;
                    // position-keyed jitter → break the uniform grid look, mask gaps (deterministic via CCRand)
                    if (p.JitterYaw > 0f) rot = Quaternion.AngleAxis((CCRand(ox + ix, oz + iz, ci, 61) - 0.5f) * 2f * p.JitterYaw, gn) * rot;
                    if (p.JitterPos > 0f)
                    {
                        Vector3 tA = inUp, tB = Vector3.Cross(gn, inUp);
                        anchor += tA * ((CCRand(ox + ix, oz + iz, ci, 67) - 0.5f) * 2f * p.JitterPos)
                                + tB * ((CCRand(ox + ix, oz + iz, ci, 71) - 0.5f) * 2f * p.JitterPos);
                        pos = anchor;
                    }
                    if (_ivyDbg < 20) { _ivyDbg++; Log.LogInfo($"[PT-ivy] #{_ivyDbg} {(isFloor ? "FLOOR" : isSurface ? "SURF" : "CEIL")} gn=({gn.x:F2},{gn.y:F2},{gn.z:F2}) gm={gmag:F1} snapVox={distVox:F2} pos=({anchor.x:F1},{anchor.y:F1},{anchor.z:F1})"); }
                }
                else if (p.Hang)
                {
                    rot = Quaternion.FromToRotation(Vector3.up, surfN) * Quaternion.Euler(0f, yaw, 0f) * offset;
                    embedN = surfN;
                }
                else if (isFloor && p.Kind == PlacementKind.Object)
                {
                    rot = Quaternion.FromToRotation(Vector3.up, surfN) * Quaternion.Euler(0f, yaw, 0f) * offset;
                    embedN = surfN;
                }
                else
                    rot = Quaternion.Euler(0f, yaw, 0f) * offset;
                if (p.Embed > 0f) pos -= embedN * p.Embed;   // sink along the aligned normal (keeps any surface snap)
                if (p.RandTilt > 0f)   // extra random X/Z tilt about a random horizontal axis → varied orientation (roots don't all lie identically)
                {
                    float ta = CCRand(ox + ix, oz + iz, ci, 31) * p.RandTilt;
                    float aa = CCRand(ox + ix, oz + iz, ci, 37) * 6.2831853f;
                    rot = Quaternion.AngleAxis(ta, new Vector3(Mathf.Cos(aa), 0f, Mathf.Sin(aa))) * rot;
                }
                // lift off the crossing into the air side (flying spawners born on the surface clip into rock →
                // Voxheim "unburies" them every tick). Air is up on a floor crossing, down on a ceiling crossing.
                if (p.Lift != 0f) pos += (isFloor ? Vector3.up : Vector3.down) * p.Lift;

                // uniform pick from the resolved pool (deterministic per crossing)
                var prefab = p.Resolved.Length == 1 ? p.Resolved[0]
                           : p.Resolved[Math.Min(p.Resolved.Length - 1, (int)(CCRand(ox + ix, oz + iz, ci, 43) * p.Resolved.Length))];
                float scaleMul = p.JitterScale > 0f ? 1f + (CCRand(ox + ix, oz + iz, ci, 73) - 0.5f) * 2f * p.JitterScale : 1f;
                if (!SpawnCave(prefab, pos, rot, ghost, spawnedObjects, p.CreatureHash, p.Inert, scaleMul)) return false;
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

        static int _inertLiveLogged;
        static int _ivyDbg;
        static bool SpawnCave(GameObject prefab, Vector3 pos, Quaternion rot, bool ghost, List<GameObject> spawnedObjects, int overrideCreature = 0, bool inert = false, float scaleMul = 1f)
        {
            try
            {
                if (ghost) ZNetView.StartGhostInit();
                var go = UnityEngine.Object.Instantiate(prefab, pos, rot);
                if (scaleMul != 1f) go.transform.localScale *= scaleMul;
                if (ghost) { spawnedObjects.Add(go); ZNetView.FinishGhostInit(); }
                if (inert)   // persist the flag (re-stripped on reload) + neutralize this live instance now
                {
                    go.GetComponent<ZNetView>()?.GetZDO()?.Set(InertDecorPatch.Key, 1);
                    InertDecorPatch.StripInertComponents(go, ref _inertLiveLogged);
                }
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

        // local cave-CEILING normal, pointing DOWN into the cavity (for hanging props). Same finite diff as the
        // floor normal but y negated → a prop aligned +Y to this normal hangs down and tilts with the ceiling slope.
        static Vector3 CaveCeilingNormal(Plugin.ZoneField e, int ix, int iz, int width, float cyY, float s)
        {
            float hpx = NearestCrossingY(e, ix + 1, iz, width, cyY, s, cyY), hmx = NearestCrossingY(e, ix - 1, iz, width, cyY, s, cyY);
            float hpz = NearestCrossingY(e, ix, iz + 1, width, cyY, s, cyY), hmz = NearestCrossingY(e, ix, iz - 1, width, cyY, s, cyY);
            float dydx = (hpx - hmx) / (2f * s), dydz = (hpz - hmz) / (2f * s);
            return new Vector3(dydx, -1f, dydz).normalized;
        }

        // Per-zone LIGHT field in 0..1: 1 at a sky-opening (skylight shaft / atrium breach / atrium centre), fading
        // linearly to 0 at `range` metres away (chamfer distance transform). Used to fade decoration out of the dark.
        static float[] ComputeLightField(Plugin.ZoneField e, int width, float s, float range, List<Vector3>? atriums, int ox, int oz)
        {
            int n = width * width;
            const float BIG = 1e9f;
            var dist = new float[n];
            for (int i = 0; i < n; i++) dist[i] = BIG;
            // seeds: columns whose surface has AIR a few voxels below it (a hole/atrium opening, not solid roof)
            for (int ix = 0; ix < width; ix++)
            for (int iz = 0; iz < width; iz++)
            {
                int oc = ix * width + iz, start = e.colStart[oc]; if (start < 0) continue;
                int len = e.colLen[oc], off = e.colOff[oc];
                int surfK = -1; for (int k = 1; k < len; k++) if ((e.band[off + k - 1] < 0) != (e.band[off + k] < 0)) surfK = k;
                if (surfK < 0) continue;
                int probe = surfK - 4;                                   // ~4 voxels below the surface crossing
                if (probe >= 0 && e.band[off + probe] >= 0) dist[oc] = 0f;   // air below surface → open to sky
            }
            // seed atrium centres too (their breach may be small/offset)
            if (atriums != null)
                foreach (var a in atriums)
                {
                    int aix = Mathf.RoundToInt(a.x / s) - ox, aiz = Mathf.RoundToInt(a.z / s) - oz;
                    if (aix >= 0 && aix < width && aiz >= 0 && aiz < width) dist[aix * width + aiz] = 0f;
                }
            // 2-pass chamfer distance transform (voxels)
            float d1 = 1f, d2 = 1.41421356f;
            for (int ix = 0; ix < width; ix++)
            for (int iz = 0; iz < width; iz++)
            {
                int c = ix * width + iz; float m = dist[c];
                if (ix > 0)            m = Mathf.Min(m, dist[c - width] + d1);
                if (iz > 0)            m = Mathf.Min(m, dist[c - 1] + d1);
                if (ix > 0 && iz > 0)  m = Mathf.Min(m, dist[c - width - 1] + d2);
                if (ix > 0 && iz < width - 1) m = Mathf.Min(m, dist[c - width + 1] + d2);
                dist[c] = m;
            }
            for (int ix = width - 1; ix >= 0; ix--)
            for (int iz = width - 1; iz >= 0; iz--)
            {
                int c = ix * width + iz; float m = dist[c];
                if (ix < width - 1)    m = Mathf.Min(m, dist[c + width] + d1);
                if (iz < width - 1)    m = Mathf.Min(m, dist[c + 1] + d1);
                if (ix < width - 1 && iz < width - 1) m = Mathf.Min(m, dist[c + width + 1] + d2);
                if (ix < width - 1 && iz > 0)         m = Mathf.Min(m, dist[c + width - 1] + d2);
                dist[c] = m;
            }
            var light = new float[n];
            float rv = Mathf.Max(0.001f, range / s);   // range in voxels
            for (int i = 0; i < n; i++) light[i] = Mathf.Clamp01(1f - dist[i] / rv);
            return light;
        }

        // Density at a single voxel from the packed band (sbyte; <0 = solid, >0 = air). Clamps to the stored run
        // at the column's ends and returns NaN for empty/out-of-zone columns (caller substitutes the centre value).
        static float SdfByte(Plugin.ZoneField e, int ix, int iz, int cyAbs, int width)
        {
            if (ix < 0 || ix >= width || iz < 0 || iz >= width) return float.NaN;
            int oc = ix * width + iz, start = e.colStart[oc];
            if (start < 0) return float.NaN;
            int len = e.colLen[oc], off = e.colOff[oc];
            int k = cyAbs - start; if (k < 0) k = 0; else if (k >= len) k = len - 1;
            return e.band[off + k];
        }

        // TRUE surface normal from the 3D gradient of the density field — accurate at ANY surface angle (wall,
        // overhang, ceiling), unlike the crossing-height finite diff which gets ambiguous in the multi-crossing
        // strata. Points toward air (increasing density) = outward. This is the reliable normal for decals/ivy.
        static Vector3 GradientNormal(Plugin.ZoneField e, int ix, int iz, int cyAbs, int width)
        {
            float c  = SdfByte(e, ix, iz, cyAbs, width); if (float.IsNaN(c)) c = 0f;
            float xp = SdfByte(e, ix + 1, iz, cyAbs, width), xm = SdfByte(e, ix - 1, iz, cyAbs, width);
            float zp = SdfByte(e, ix, iz + 1, cyAbs, width), zm = SdfByte(e, ix, iz - 1, cyAbs, width);
            float yp = SdfByte(e, ix, iz, cyAbs + 1, width), ym = SdfByte(e, ix, iz, cyAbs - 1, width);
            if (float.IsNaN(xp)) xp = c; if (float.IsNaN(xm)) xm = c;
            if (float.IsNaN(zp)) zp = c; if (float.IsNaN(zm)) zm = c;
            if (float.IsNaN(yp)) yp = c; if (float.IsNaN(ym)) ym = c;
            var g = new Vector3(xp - xm, yp - ym, zp - zm);
            return g.sqrMagnitude < 1e-6f ? Vector3.up : g.normalized;   // degenerate → fallback up
        }

        // Outward normal + signed distance (in VOXELS) from the sampled voxel to the actual zero-crossing surface,
        // scale-free (uses the band's own gradient magnitude so sbyte quantization cancels — Newton step to the
        // isosurface). distVox > 0 when the sample sits in air; move -normal*distVox to land ON the surface.
        static Vector3 GradientNormalSnap(Plugin.ZoneField e, int ix, int iz, int cyAbs, int width, out float distVox, out float gm)
        {
            float c  = SdfByte(e, ix, iz, cyAbs, width); if (float.IsNaN(c)) c = 0f;
            float xp = SdfByte(e, ix + 1, iz, cyAbs, width), xm = SdfByte(e, ix - 1, iz, cyAbs, width);
            float zp = SdfByte(e, ix, iz + 1, cyAbs, width), zm = SdfByte(e, ix, iz - 1, cyAbs, width);
            float yp = SdfByte(e, ix, iz, cyAbs + 1, width), ym = SdfByte(e, ix, iz, cyAbs - 1, width);
            if (float.IsNaN(xp)) xp = c; if (float.IsNaN(xm)) xm = c;
            if (float.IsNaN(zp)) zp = c; if (float.IsNaN(zm)) zm = c;
            if (float.IsNaN(yp)) yp = c; if (float.IsNaN(ym)) ym = c;
            var g = new Vector3(xp - xm, yp - ym, zp - zm);
            gm = g.magnitude;
            if (gm < 1e-4f) { distVox = 0f; return Vector3.up; }
            distVox = c / (gm * 0.5f);   // |∇band|/2 = band change per voxel → Newton step to the crossing
            return g / gm;
        }
    }
}
