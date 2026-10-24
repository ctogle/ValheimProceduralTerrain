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

            bool anyLight = false;
            foreach (var p in objPool)  if (p.LightRange > 0f) anyLight = true;
            foreach (var p in channels) if (p.LightRange > 0f) anyLight = true;
            foreach (var p in spwPool)  if (p.LightRange > 0f) anyLight = true;
            float[]? seedY = anyLight ? ComputeLightSeeds(e, width, s) : null;

            // place one rule at a crossing, honoring its cap + spacing + flatness; returns true if placed.
            bool TryPlace(Placement p, int ix, int iz, int ci, bool isFloor, bool isSurface, float cyY, float worldX, float worldZ, float yaw, Vector3? wallNormal = null)
            {
                if (isSurface && !p.Surface) return false;   // only surface-opted rules place at the top crossing
                if (p.Hang && (isFloor || isSurface)) return false;   // hanging props need a ceiling crossing (air below, solid above)
                if (cyY < p.MinY || cyY > p.MaxY) return false;   // world-Y band (e.g. sewer level only, below the strata galleries)
                if (p.ClusterFreq > 0f && Plugin._noisePool != null)  // patchy spatial structure: only place where the noise field clears the threshold
                {
                    float cn = Plugin._noisePool[1].GetNoise(worldX * p.ClusterFreq, 0f, worldZ * p.ClusterFreq);
                    if (cn < p.ClusterThresh) return false;
                }
                if (p.LightRange > 0f && seedY != null)   // lit iff within LightRange (3D) of a sky-ray-reached seed
                {
                    float X = p.LightRange, X2 = X * X;
                    bool lit = false;
                    float self = seedY[ix * width + iz];                       // own column's surface (vertical check, cheap)
                    if (!float.IsNaN(self) && Mathf.Abs(self - cyY) <= X) lit = true;
                    if (!lit)                                                  // else scan laterally for a near seed (shaft-bottom halo)
                    {
                        int R = Mathf.CeilToInt(X / s);
                        for (int dxi = -R; dxi <= R && !lit; dxi++)
                        for (int dzi = -R; dzi <= R && !lit; dzi++)
                        {
                            int nx = ix + dxi, nz = iz + dzi; if (nx < 0 || nx >= width || nz < 0 || nz >= width) continue;
                            float ny = seedY[nx * width + nz]; if (float.IsNaN(ny)) continue;
                            float lx = dxi * s, lz = dzi * s, ldy = ny - cyY;
                            if (lx * lx + lz * lz + ldy * ldy <= X2) lit = true;
                        }
                    }
                    if (!lit) return false;
                }
                if (p.BiomePure > 0f && wg.GetBiomeArea(new Vector3(worldX, 0f, worldZ)) == Heightmap.BiomeArea.Edge) return false;  // interior only, off the boundary
                if (p.AtriumOnly > 0f)   // confine to atrium ROOM: within AtriumOnly metres in 3D of a carved dome centre
                {                        // (3D — XZ-only let strata floors ABOVE an atrium pass → elites in the strata)
                    if (nearAtriums == null || nearAtriums.Count == 0) return false;
                    float r2 = p.AtriumOnly * p.AtriumOnly; bool near = false;
                    foreach (var a in nearAtriums) { float dx = a.x - worldX, dy = a.y - cyY, dz = a.z - worldZ; if (dx * dx + dy * dy + dz * dz <= r2) { near = true; break; } }
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
                    Vector3 gn;
                    if (wallNormal.HasValue)
                    {
                        // WALL pass: the normal comes from the solid→air column pair (the band is saturated laterally,
                        // so its gradient is useless here). The anchor is already the face midpoint → no snap, no guard.
                        gn = wallNormal.Value;
                    }
                    else
                    {
                        int cyAbs = Mathf.RoundToInt(cyY / s) - e.extLo;
                        gn = GradientNormalSnap(e, ix, iz, cyAbs, width, out float distVox, out float gmag);
                        // guard: degenerate gradient (flat coarse facet) or a long snap = unreliable → skip, don't float it
                        if (gmag < 1.0f || Mathf.Abs(distVox) > 1.2f) { if (_ivyDbg < 20) { _ivyDbg++; Log.LogInfo($"[PT-ivy] #{_ivyDbg} SKIP gm={gmag:F2} snapVox={distVox:F2}"); } return false; }
                        anchor -= gn * (distVox * s);   // SNAP onto the real surface (column x/z is only on-surface for flat spots)
                        pos = anchor;
                    }
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
                    if (_ivyDbg < 20) { _ivyDbg++; Log.LogInfo($"[PT-ivy] #{_ivyDbg} {(wallNormal.HasValue ? "WALL" : isFloor ? "FLOOR" : isSurface ? "SURF" : "CEIL")} gn=({gn.x:F2},{gn.y:F2},{gn.z:F2}) pos=({anchor.x:F1},{anchor.y:F1},{anchor.z:F1})"); }
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
                if (!SpawnCave(prefab, pos, rot, ghost, spawnedObjects, p.CreatureHash, p.Inert, scaleMul, p)) return false;
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
                        if (p.WallOnly) continue;   // wall-only rules skip the vertical floor/ceiling pass
                        float r = p.Ramp(depth); if (r <= 0f) continue;
                        if (CCRand(ox + ix, oz + iz, ci, 20 + j) >= p.Prob * r) continue;
                        TryPlace(p, ix, iz, ci, isFloor, isSurface, cyY, worldX, worldZ, yaw);
                    }
                }
            }

            // LATERAL WALL pass: the vertical-column crossing scan can't see near-vertical faces (a column runs
            // parallel to a wall). For Wall-flagged rules, seed where adjacent columns flip solidity at the same Y =
            // a vertical face. Cheap: band-run array lookups, stride 2 in Y, +x/+z neighbours only. TryPlace does the
            // rest (gradient normal here is ~horizontal → correct wall orientation; snap lands it on the face).
            var wallRules = new List<Placement>();
            foreach (var p in objPool)  if (p.Wall || p.WallOnly) wallRules.Add(p);
            foreach (var p in channels) if (p.Wall || p.WallOnly) wallRules.Add(p);
            if (wallRules.Count > 0)
            {
                // Collect threshold = most permissive steepness across the wall rules (per-rule gate still applies at
                // placement). minClump = strictest component-size floor among them.
                float collectSlope = 90f; int minClump = 0;
                foreach (var wr in wallRules) { if (wr.WallSlopeMin < collectSlope) collectSlope = wr.WallSlopeMin; if (wr.WallMinClump > minClump) minClump = wr.WallMinClump; }
                float collectCos = Mathf.Cos(collectSlope * Mathf.Deg2Rad);

                // PASS 1 — collect steep wall-face voxels + their normals (keyed by column+Y, neighbour-agnostic).
                var faceNorm = new Dictionary<long, Vector3>();
                long Key(int cx, int cz, int cy) => ((long)(cx * width + cz) << 20) | (uint)cy;
                for (int ix = 0; ix < width; ix++)
                for (int iz = 0; iz < width; iz++)
                {
                    int oc = ix * width + iz, start = e.colStart[oc]; if (start < 0) continue;
                    int len = e.colLen[oc], off = e.colOff[oc];
                    for (int k = 0; k < len; k++)
                    {
                        int cyAbs = start + k;
                        bool colSolid = e.band[off + k] < 0;
                        // is this a lateral face? any of the 4 XZ neighbours flips solidity at this Y
                        bool face = false;
                        if (WallSign(e, ix + 1, iz, cyAbs, width) != 0f && (WallSign(e, ix + 1, iz, cyAbs, width) < 0f) != colSolid) face = true;
                        else if (WallSign(e, ix - 1, iz, cyAbs, width) != 0f && (WallSign(e, ix - 1, iz, cyAbs, width) < 0f) != colSolid) face = true;
                        else if (WallSign(e, ix, iz + 1, cyAbs, width) != 0f && (WallSign(e, ix, iz + 1, cyAbs, width) < 0f) != colSolid) face = true;
                        else if (WallSign(e, ix, iz - 1, cyAbs, width) != 0f && (WallSign(e, ix, iz - 1, cyAbs, width) < 0f) != colSolid) face = true;
                        if (!face) continue;
                        // smooth 3D normal from the 3x3x3 solidity gradient (incl. Y slope) + surface-mix check
                        Vector3 g = Vector3.zero; int airN = 0, solN = 0;
                        for (int ddx = -1; ddx <= 1; ddx++)
                        for (int ddy = -1; ddy <= 1; ddy++)
                        for (int ddz = -1; ddz <= 1; ddz++)
                        {
                            if (ddx == 0 && ddy == 0 && ddz == 0) continue;
                            float sgn = WallSign(e, ix + ddx, iz + ddz, cyAbs + ddy, width);
                            if (sgn == 0f) continue;
                            if (sgn > 0f) airN++; else solN++;
                            g += new Vector3(ddx, ddy, ddz).normalized * sgn;
                        }
                        if (airN < 3 || solN < 3 || g.sqrMagnitude < 1e-4f) continue;   // speck/corner
                        Vector3 wallN = g.normalized;
                        if (Mathf.Abs(wallN.y) > collectCos) continue;                   // not steep enough for any rule
                        faceNorm[Key(ix, iz, cyAbs)] = wallN;
                    }
                }

                // PASS 2 — flood-fill into connected components (6-conn in column+Y); keep only big ones.
                var kept = new HashSet<long>();
                var visited = new HashSet<long>();
                var comp = new List<long>();
                var stack = new Stack<long>();
                foreach (var seed in faceNorm.Keys)
                {
                    if (!visited.Add(seed)) continue;
                    comp.Clear(); stack.Clear(); stack.Push(seed);
                    while (stack.Count > 0)
                    {
                        long cur = stack.Pop(); comp.Add(cur);
                        long xz = cur >> 20; int cy = (int)(cur & 0xFFFFF);
                        int cx = (int)(xz / width), cz = (int)(xz % width);
                        long[] nb = { Key(cx + 1, cz, cy), Key(cx - 1, cz, cy), Key(cx, cz + 1, cy), Key(cx, cz - 1, cy), Key(cx, cz, cy + 1), Key(cx, cz, cy - 1) };
                        foreach (var nkey in nb) if (faceNorm.ContainsKey(nkey) && visited.Add(nkey)) stack.Push(nkey);
                    }
                    if (comp.Count >= minClump) foreach (var c in comp) kept.Add(c);
                }

                // PASS 3 — place on surviving (big-component) faces; light/cluster/spacing/cap applied in TryPlace.
                foreach (var key in kept)
                {
                    long xz = key >> 20; int cyAbs = (int)(key & 0xFFFFF);
                    int ix = (int)(xz / width), iz = (int)(xz % width);
                    Vector3 wallN = faceNorm[key];
                    float cyY = (e.extLo + cyAbs) * s;
                    // POSITION at the actual face midpoint — offset toward the opposite-solidity neighbour(s), NOT
                    // along wallN. The 26-neighbour normal is corner-biased (averages both walls at a concave corner),
                    // so positioning by it gave a distance-from-corner linear drift. The face is between this column
                    // and its solid/air-flipped neighbour → independent of the normal, so no corner drift. wallN now
                    // only ORIENTS. Corner (2 flips) → average toward the concave edge.
                    bool colSolid = WallSign(e, ix, iz, cyAbs, width) < 0f;
                    float spx = WallSign(e, ix + 1, iz, cyAbs, width), smx = WallSign(e, ix - 1, iz, cyAbs, width);
                    float spz = WallSign(e, ix, iz + 1, cyAbs, width), smz = WallSign(e, ix, iz - 1, cyAbs, width);
                    float offX = 0f, offZ = 0f; int bn = 0;
                    if (spx != 0f && (spx < 0f) != colSolid) { offX += 1f; bn++; }
                    if (smx != 0f && (smx < 0f) != colSolid) { offX -= 1f; bn++; }
                    if (spz != 0f && (spz < 0f) != colSolid) { offZ += 1f; bn++; }
                    if (smz != 0f && (smz < 0f) != colSolid) { offZ -= 1f; bn++; }
                    if (bn > 1) { offX /= bn; offZ /= bn; }
                    float wX = (ox + ix) * s + offX * 0.5f * s, wZ = (oz + iz) * s + offZ * 0.5f * s;
                    float yaw = CCRand(ox + ix, oz + iz, cyAbs, 7) * 360f;
                    foreach (var wr in wallRules)
                    {
                        if (Mathf.Abs(wallN.y) > Mathf.Cos(wr.WallSlopeMin * Mathf.Deg2Rad)) continue;
                        if (wr.Prob >= 0f && CCRand(ox + ix, oz + iz, cyAbs, 23) >= wr.Prob) continue;
                        TryPlace(wr, ix, iz, cyAbs, false, false, cyY, wX, wZ, yaw, wallN);
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
        static int _pivotDbg;
        static int _pivotDbgShip;
        static bool SpawnCave(GameObject prefab, Vector3 pos, Quaternion rot, bool ghost, List<GameObject> spawnedObjects, int overrideCreature = 0, bool inert = false, float scaleMul = 1f, Placement? cfg = null)
        {
            try
            {
                if (ghost) ZNetView.StartGhostInit();
                var go = UnityEngine.Object.Instantiate(prefab, pos, rot);
                if (!go.activeSelf) go.SetActive(true);   // some runtime prefabs (dust motes) ship inactive as a template
                if (scaleMul != 1f) go.transform.localScale *= scaleMul;
                bool isShip = go.name.StartsWith("shipwreck", StringComparison.OrdinalIgnoreCase);
                bool isVine = go.name.StartsWith("vines", StringComparison.OrdinalIgnoreCase) || go.name.StartsWith("VineGreen", StringComparison.OrdinalIgnoreCase);
                // separate budgets so a flood of vines can't starve the shipwreck-pivot capture (and vice-versa)
                if ((isShip ? _pivotDbgShip : _pivotDbg) < 16 && (isShip || isVine))
                {
                    var rnd = go.GetComponentInChildren<Renderer>();
                    if (rnd != null)
                    {
                        Vector3 localCenter = go.transform.InverseTransformPoint(rnd.bounds.center);
                        Vector3 ext = go.transform.InverseTransformVector(rnd.bounds.size);
                        if (isShip) _pivotDbgShip++; else _pivotDbg++;
                        Plugin.Log.LogInfo($"[PT-pivot] {go.name} pivot→centroid(local)=({localCenter.x:F2},{localCenter.y:F2},{localCenter.z:F2}) size(local)=({Mathf.Abs(ext.x):F2},{Mathf.Abs(ext.y):F2},{Mathf.Abs(ext.z):F2})");
                    }
                }
                if (cfg != null) ConfigBird(go, cfg);
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

        // Tune a RandomFlyingBird after spawn (nesting vibe: more/longer perching, tamer noise, tighter roam) and
        // optionally swap its model from another bird prefab. Behaviour fields read live each Update → safe post-Awake;
        // the MODEL swap is best-effort (Awake may have already built the visible model — may need a prefab-level swap).
        static void ConfigBird(GameObject go, Placement p)
        {
            var bird = go.GetComponent<RandomFlyingBird>();
            if (bird == null) return;
            if (p.BirdLandChance   >= 0f) bird.m_landChance             = p.BirdLandChance;
            if (p.BirdLandDuration >= 0f) bird.m_landDuration           = p.BirdLandDuration;
            if (p.BirdNoiseMin     >= 0f) bird.m_randomNoiseIntervalMin = p.BirdNoiseMin;
            if (p.BirdNoiseMax     >= 0f) bird.m_randomNoiseIntervalMax = p.BirdNoiseMax;
            if (p.BirdFlyRange     >= 0f) bird.m_flyRange               = p.BirdFlyRange;
            if (p.BirdMinAlt       >= 0f) bird.m_minAlt                 = p.BirdMinAlt;
            if (p.BirdMaxAlt       >= 0f) bird.m_maxAlt                 = p.BirdMaxAlt;
            if (!string.IsNullOrEmpty(p.BirdModel))
            {
                var mp = ZNetScene.instance?.GetPrefab(p.BirdModel);
                var mb = mp != null ? mp.GetComponent<RandomFlyingBird>() : null;
                if (mb != null) { bird.m_flyingModel = mb.m_flyingModel; bird.m_landedModel = mb.m_landedModel; }
            }
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

        // LIGHT SEEDS: a column is a seed only if its topmost crossing (where a downward sky-ray stops) is a LOCAL
        // DEPRESSION — ≥OPEN_DEPTH below the highest surface in a small neighbourhood (a shaft/breach into the cave),
        // NOT the normal roof/ground. Returns per-column seed Y (NaN = not a seed). This is where light actually enters.
        static float[] ComputeLightSeeds(Plugin.ZoneField e, int width, float s)
        {
            int n = width * width;
            var topY = new float[n];
            for (int ix = 0; ix < width; ix++)
            for (int iz = 0; iz < width; iz++)
            {
                int oc = ix * width + iz; topY[oc] = float.NaN;
                int start = e.colStart[oc]; if (start < 0) continue;
                int len = e.colLen[oc], off = e.colOff[oc]; int topK = -1;
                for (int k = 1; k < len; k++) if ((e.band[off + k - 1] < 0) != (e.band[off + k] < 0)) topK = k;
                if (topK >= 0) topY[oc] = (e.extLo + start + topK) * s;
            }
            const float OPEN_DEPTH = 5f; const int RN = 4;
            var seed = new float[n];
            for (int ix = 0; ix < width; ix++)
            for (int iz = 0; iz < width; iz++)
            {
                int oc = ix * width + iz; seed[oc] = float.NaN;
                float t = topY[oc]; if (float.IsNaN(t)) continue;
                float nmax = t;
                for (int dxi = -RN; dxi <= RN; dxi++)
                for (int dzi = -RN; dzi <= RN; dzi++)
                {
                    int nx = ix + dxi, nz = iz + dzi; if (nx < 0 || nx >= width || nz < 0 || nz >= width) continue;
                    float nt = topY[nx * width + nz]; if (!float.IsNaN(nt) && nt > nmax) nmax = nt;
                }
                if (nmax - t >= OPEN_DEPTH) seed[oc] = t;   // a hole below the surrounding terrain → light enters here
            }
            return seed;
        }

        // Debug: draw a cyan marker at every light seed within `radius` of center (pt_lightfield).
        internal static void DrawLightSeeds(Vector3 center, float radius)
        {
            float s = ActiveScale(); int drawn = 0; float r2 = radius * radius;
            foreach (var kv in _sdfCache)
            {
                var parts = kv.Key.Split(',');
                if (parts.Length != 2 || !int.TryParse(parts[0], out int ox) || !int.TryParse(parts[1], out int oz)) continue;
                var e = kv.Value; int width = e.width;
                float zcx = (ox + width * 0.5f) * s, zcz = (oz + width * 0.5f) * s, zr = radius + width * s;
                if ((zcx - center.x) * (zcx - center.x) + (zcz - center.z) * (zcz - center.z) > zr * zr) continue;
                var seed = ComputeLightSeeds(e, width, s);
                for (int ix = 0; ix < width; ix++)
                for (int iz = 0; iz < width; iz++)
                {
                    float sy = seed[ix * width + iz]; if (float.IsNaN(sy)) continue;
                    float wx = (ox + ix) * s, wz = (oz + iz) * s;
                    if ((wx - center.x) * (wx - center.x) + (wz - center.z) * (wz - center.z) > r2) continue;
                    // AddToZone (explicit zone + version bump), NOT Line — Line needs the thread-static _cur that only
                    // the Carve capture sets; from the console thread it's unset and every marker is silently dropped.
                    DebugViz.AddToZone(ox, oz, new[] { new Vector3(wx, sy, wz), new Vector3(wx, sy + 4f, wz) }, new Color(0.2f, 0.9f, 1f), 0.3f);
                    drawn++;
                }
            }
            Log.LogInfo($"[PT] lightfield: drew {drawn} seed markers within {radius:F0}m");
            Console.instance?.Print($"[PT] lightfield: {drawn} seed markers (cyan verticals) — these are the ONLY spots light enters. pt_viz_clear to wipe.");
        }

        // Solidity sign at a voxel for the wall-normal gradient: -1 solid, +1 air, 0 = no data (out of band).
        // Does NOT clamp (unlike SdfByte) — out-of-band contributes nothing so the horizontal gradient stays local.
        static float WallSign(Plugin.ZoneField e, int cx, int cz, int cyAbs, int width)
        {
            if (cx < 0 || cx >= width || cz < 0 || cz >= width) return 0f;
            int o = cx * width + cz, st = e.colStart[o]; if (st < 0) return 0f;
            int kk = cyAbs - st; if (kk < 0 || kk >= e.colLen[o]) return 0f;
            return e.band[e.colOff[o] + kk] < 0 ? -1f : 1f;
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
