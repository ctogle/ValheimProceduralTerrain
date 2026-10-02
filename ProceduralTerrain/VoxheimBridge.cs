using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ProceduralTerrain
{
    public partial class Plugin
    {
        // ── Type lookup ───────────────────────────────────────────────────────────
        internal static Type? FindType(string typeName, string assemblyName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetName().Name == assemblyName)
                {
                    var t = asm.GetType(typeName);
                    if (t != null) return t;
                }
            return null;
        }

        // ── Delegate compilation ──────────────────────────────────────────────────
        static bool BuildDelegates()
        {
            var zoneT = FindType("Voxheim.Unity.VoxelZone", "Voxheim");

            _scaleField = zoneT?.GetField("s_scale",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (_scaleField == null) Log.LogWarning("[ProceduralTerrain] s_scale not found, will assume 1.");

            // Voxheim 0.1.7: skinning is async (worker pump + CompleteWorkerSkin); FinishSkinNow is gone
            // and Unskin() DestroyImmediate's the VoxelZone. Hot-reload = Unskin then the static Skin(hmap),
            // which re-adds a VoxelZone, Activates, and enqueues it (s_pendingZones) so the worker finishes it.
            _skinMethod          = zoneT?.GetMethod("Skin",
                BindingFlags.NonPublic | BindingFlags.Static);
            _unskinMethod        = zoneT?.GetMethod("Unskin",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _hmapField           = zoneT?.GetField("_hmap",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (_skinMethod          == null) Log.LogWarning("[ProceduralTerrain] Skin(Heightmap) not found — hot-reload will not work.");
            if (_unskinMethod        == null) Log.LogWarning("[ProceduralTerrain] Unskin not found — hot-reload will not work.");
            if (_hmapField           == null) Log.LogWarning("[ProceduralTerrain] _hmap field not found — hot-reload will not work.");

            _zoneOriginX = zoneT?.GetField("_originX", BindingFlags.NonPublic | BindingFlags.Instance);
            _zoneOriginZ = zoneT?.GetField("_originZ", BindingFlags.NonPublic | BindingFlags.Instance);
            if (_zoneOriginX == null || _zoneOriginZ == null)
                Log.LogWarning("[ProceduralTerrain] Zone origin fields missing.");

            _locationInstancesField =
                typeof(ZoneSystem).GetField("m_locationInstances",
                    BindingFlags.NonPublic | BindingFlags.Instance) ??
                typeof(ZoneSystem).GetField("m_locationInstances",
                    BindingFlags.Public    | BindingFlags.Instance);
            if (_locationInstancesField == null)
                Log.LogWarning("[ProceduralTerrain] m_locationInstances not found — location protection off.");

            return true;
        }

        // ── Groundwork overlay ────────────────────────────────────────────────────
        // Patches the pad columns of a procedural (baked or live) zone with the groundworked surface. The
        // footprint is where the groundworked height differs from PURE vanilla (location protection guarantees
        // those columns are vanilla-clean in the bake, so overwriting them loses no procedural detail). The
        // patch is EIKONAL-REDISTANCED (reusing Carve's RedistanceEikonal over the pad bbox + margin) so a
        // voxel's nearest surface point can lie in a neighbour column — true distance/normals on the sloped
        // rim, matching Carve's post-mod eikonal, instead of an aliased per-column vertical field.
        internal sealed class GroundworkOverlay
        {
            readonly int _w, _x0, _z0, _nz, _extLo, _bh;
            readonly float _scale;
            readonly bool[] _ovr;
            readonly float[] _f;     // redistanced signed SDF over the pad sub-region [(x*_nz+z)*_bh + y]
            internal GroundworkOverlay(int w, int x0, int z0, int nz, int extLo, int bh, float scale, bool[] ovr, float[] f)
            { _w = w; _x0 = x0; _z0 = z0; _nz = nz; _extLo = extLo; _bh = bh; _scale = scale; _ovr = ovr; _f = f; }
            public bool IsOverride(int ci, int ri) => _ovr[ci * _w + ri];
            public sbyte Sample(int ci, int ri, int wy)
            {
                int i = (((ci - _x0) * _nz) + (ri - _z0)) * _bh + (wy - _extLo);
                return ToSDFByte(_f[i], _scale);
            }
        }

        internal static GroundworkOverlay BuildGroundworkOverlay(
            Voxheim.Core.HeightGrid gw, Voxheim.Core.HeightGrid van, int width, float scale, int extLo, int bh)
        {
            // Footprint mask + bounding box.
            var ovr = new bool[width * width];
            int minX = width, maxX = -1, minZ = width, maxZ = -1;
            for (int ci = 0; ci < width; ci++)
            for (int ri = 0; ri < width; ri++)
                if (Math.Abs(gw[ci, ri] - van[ci, ri]) > 0.05f)
                {
                    ovr[ci * width + ri] = true;
                    if (ci < minX) minX = ci; if (ci > maxX) maxX = ci;
                    if (ri < minZ) minZ = ri; if (ri > maxZ) maxZ = ri;
                }
            if (maxX < 0) return new GroundworkOverlay(width, 0, 0, 1, extLo, bh, scale, ovr, Array.Empty<float>());

            // Sub-region = footprint bbox + margin, so the eikonal has boundary neighbours past the pad rim.
            const int M = 4;
            int x0 = Math.Max(0, minX - M), x1 = Math.Min(width - 1, maxX + M);
            int z0 = Math.Max(0, minZ - M), z1 = Math.Min(width - 1, maxZ + M);
            int nx = x1 - x0 + 1, nz = z1 - z0 + 1;
            int n  = nx * nz * bh;
            var f = _gwOverlayScratch;
            if (f == null || f.Length < n) { f = new float[n]; _gwOverlayScratch = f; }

            // Heightfield init over the sub-region (gw == vanilla outside the footprint → correct protected
            // surface at the rim). sdf = worldY_meters − surface_meters = (wy − gw[..])·scale.
            for (int x = 0; x < nx; x++)
            for (int z = 0; z < nz; z++)
            {
                float hgw = gw[x0 + x, z0 + z];
                int baseI = (x * nz + z) * bh;
                for (int y = 0; y < bh; y++)
                    f[baseI + y] = ((extLo + y) - hgw) * scale;
            }
            RedistanceEikonal(f, nx, nz, bh, scale, scale, scale, 2);
            return new GroundworkOverlay(width, x0, z0, nz, extLo, bh, scale, ovr, f);
        }
    }

    // ── Harmony patches ───────────────────────────────────────────────────────────

    [HarmonyPatch]
    static class VoxelZoneDestroyPatch
    {
        static MethodBase? TargetMethod()
        {
            var t = Plugin.FindType("Voxheim.Unity.VoxelZone", "Voxheim");
            return t?.GetMethod("OnDestroy", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        static void Postfix(object __instance)
        {
            if (Plugin._zoneOriginX == null || Plugin._zoneOriginZ == null) return;
            int ox = (int)Plugin._zoneOriginX.GetValue(__instance)!;
            int oz = (int)Plugin._zoneOriginZ.GetValue(__instance)!;
            string key = $"{ox},{oz}";
            Plugin._sdfCache.TryRemove(key, out _);
            lock (Plugin._surfHCache) Plugin._surfHCache.Remove((ox, oz));
        }
    }

    // ── Voxheim IVoxelBaselineGenerator ──────────────────────────────────────────
    sealed class PTGenerator : Voxheim.Core.IVoxelBaselineGenerator
    {
        public string Id       => "com.ctogle.procedural_terrain";
        public string Revision => "1";

        public void Generate(Voxheim.Core.VoxelBaselineContext context, Voxheim.Core.VoxelBaselineBuilder builder)
        {
            int ox    = context.OriginX;
            int oz    = context.OriginZ;
            int width = context.Width;

            string zoneKey = $"{ox},{oz}";
            Plugin.EnsureBakeLoaded();
            bool haveCache = Plugin._sdfCache.TryGetValue(zoneKey, out var c);
            // GC correlation: did serving this zone trigger a collection? (gen0 = churn, gen2 = big pause)
            int _gc0 = GC.CollectionCount(0), _gc1 = GC.CollectionCount(1), _gc2 = GC.CollectionCount(2);
            bool _genThisZone = !haveCache;

            // Carve/read a PURELY PROCEDURAL baseline — identical for baked and live. Carve internally
            // serves a bake HIT (PopulateSparseFromBake) or carves live; either way the result has NO
            // location groundwork. Groundwork is a distinct live overlay applied below.
            if (!haveCache)
            {
                long _tc0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try { Plugin.Carve(context.Heights, ox, oz, width, context.InitialScanLo, context.InitialScanHi, context.Heights.Scale, context.Heights, context.Floor.Y); }
                catch (Exception e) { Plugin.Log.LogError($"[PT] Carve ({ox},{oz}): {e.Message}"); return; }
                double carveMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _tc0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (carveMs > 3.0) Plugin.Log.LogInfo($"[PT-load] Carve ({ox},{oz}) = {carveMs:F2}ms (live/unbaked)");
                if (!Plugin._sdfCache.TryGetValue(zoneKey, out c)) return;
            }

            // Groundwork overlay (distinct LIVE step): groundworked surface heights for this zone (cache-backed
            // for revisit determinism). Footprint = where groundwork changed PURE vanilla (context.Heights
            // already has groundwork, so it can't be the reference). The patched columns get an eikonal
            // redistance pass so the pad rim has true distance/normals — matching Carve's post-mod eikonal.
            Voxheim.Core.HeightGrid gwGrid = null, vanGrid = null;
            if (Plugin._applyGroundwork)
            {
                gwGrid = BuildGroundworkedHeights(context, out int nmods, out vanGrid);  // cache-backed; van = PURE vanilla
                if (gwGrid == null || nmods == 0) { gwGrid = null; }
            }

            builder.SetFloor(context.Floor);
            builder.IncludeY(c.extLo, c.extLo + c.bh - 1);

            // Sparse consume: Carve stored only the deviating band per column (the rest is served by the
            // SolidFloorRule default). Replay the runs — EXCEPT columns under location groundwork, which are
            // re-emitted from the groundworked surface, eikonal-redistanced for the pad rim (see ApplyGroundworkOverlay).
            int floorY = context.Floor.Y;
            float scale = context.Heights.Scale;
            Plugin.GroundworkOverlay gw = gwGrid != null
                ? Plugin.BuildGroundworkOverlay(gwGrid, vanGrid, width, scale, c.extLo, c.bh)
                : null;
            int ovCols = 0;
            long _tSet0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int setCount = 0;
            for (int ci = 0; ci < width; ci++)
            for (int ri = 0; ri < width; ri++)
            {
                int wx = ox + ci, wz = oz + ri;
                if (gw != null && gw.IsOverride(ci, ri))
                {
                    for (int wy = c.extLo; wy < c.extLo + c.bh; wy++)
                    {
                        sbyte d = gw.Sample(ci, ri, wy);
                        if (d == 127 && wy >= floorY) continue;
                        if (d == -127 && wy < floorY) continue;
                        builder.Field.Set(new Voxheim.Core.Int3(wx, wy, wz), d);
                        setCount++;
                    }
                    ovCols++;
                    continue;
                }
                int col = ci * c.width + ri;
                int len = c.colLen[col];
                if (len == 0) continue;
                int start = c.colStart[col], off = c.colOff[col];
                for (int k = 0; k < len; k++)
                {
                    int   wy = c.extLo + start + k;
                    builder.Field.Set(new Voxheim.Core.Int3(wx, wy, wz), c.band[off + k]);
                    setCount++;
                }
            }
            double setMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _tSet0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (gwGrid != null) Plugin.Log.LogInfo($"[PT-conform] zone ({ox},{oz}) groundwork overlay: override columns={ovCols}");
            if (setMs > 2.0) Plugin.Log.LogInfo($"[PT-load] Field.Set zone ({ox},{oz}) = {setMs:F2}ms set={setCount} ({width}x{width}x{c.bh})");
            int d0 = GC.CollectionCount(0) - _gc0, d1 = GC.CollectionCount(1) - _gc1, d2 = GC.CollectionCount(2) - _gc2;
            if (d0 > 0 || d1 > 0 || d2 > 0)
                Plugin.Log.LogInfo($"[PT-gc] zone ({ox},{oz}) load triggered GC: gen0+{d0} gen1+{d1} gen2+{d2}  (gen={_genThisZone})");
        }

        // Build a per-zone height grid with overlapping LOCATION terrain ops baked in, using vanilla
        // HMBuildData (base heights + corner biomes) + Voxheim's ApplyGroundworkInPlace. Returns null
        // (nmods=0) when there are no location modifiers or worldgen isn't ready. Callable by zone
        // coords so both the live generator and the bake pre-gather share this exact path.
        internal static Voxheim.Core.HeightGrid BuildGroundworkedHeights(Voxheim.Core.VoxelBaselineContext context, out int nmods, out Voxheim.Core.HeightGrid vanillaGrid)
            => BuildGroundworkedHeights(context.CenterX, context.CenterY, context.CenterZ, context.Width, context.Scale, out nmods, out vanillaGrid);

        internal static Voxheim.Core.HeightGrid BuildGroundworkedHeights(
            float centerX, float centerY, float centerZ, int width, float scale, out int nmods, out Voxheim.Core.HeightGrid vanillaGrid)
        {
            nmods = 0;
            vanillaGrid = null;
            var wg = WorldGenerator.instance;
            if (wg == null) return null;

            // Revisit determinism: the gather below reads live TerrainModifier GameObjects, which are present
            // on first load but NOT on revisit (location already m_placed; objects respawn async after our
            // Generate). So we cache the groundworked heights from the first successful gather and reuse them
            // on later regenerations — keeping the baseline identical so Voxheim's authority doesn't rebase
            // to vanilla. Keyed by Valheim zone index; survives zone unload (only location zones cached).
            var zoneIdx = ZoneSystem.GetZone(new Vector3(centerX, 0f, centerZ));
            string gwKey = $"{zoneIdx.x},{zoneIdx.y}";
            if (Plugin._groundworkCache.TryGetValue(gwKey, out var cached))
            {
                nmods = cached.nmods;
                vanillaGrid = cached.vanilla;
                return cached.grid;
            }

            var center  = new Vector3(centerX, centerY, centerZ);
            float half  = width * scale * 0.5f;

            // Fallback gather of LOCATION terrain modifiers overlapping this zone. Player edits (m_playerModifiction)
            // are deliberately excluded: Voxheim replays them as an overlay on top of our baseline, so
            // baking them here would double-apply them and make the baseline non-deterministic.
            var mods = new List<Voxheim.Core.ModifierDescriptor>();
            var all  = TerrainModifier.GetAllInstances();
            int gwLvl = 0, gwSm = 0; float gwTgtMin = 9999f, gwTgtMax = -9999f, gwMaxR = 0f;
            for (int i = 0; i < all.Count; i++)
            {
                var tm = all[i];
                if (tm == null || tm.m_playerModifiction) continue;
                var p = tm.transform.position;
                float r = tm.GetRadius();
                if (Math.Abs(p.x - center.x) > half + r + 4f) continue;
                if (Math.Abs(p.z - center.z) > half + r + 4f) continue;
                mods.Add(new Voxheim.Core.ModifierDescriptor(
                    p.x, p.y, p.z, tm.m_playerModifiction, tm.m_sortOrder, 0L,
                    tm.m_levelOffset, tm.m_level, tm.m_levelRadius,
                    tm.m_smooth, tm.m_smoothRadius, tm.m_smoothPower,
                    tm.m_square, tm.m_paintCleared, tm.m_paintRadius));
                if (tm.m_level)  { gwLvl++; float ty = p.y + tm.m_levelOffset; if (ty < gwTgtMin) gwTgtMin = ty; if (ty > gwTgtMax) gwTgtMax = ty; }
                if (tm.m_smooth) gwSm++;
                float rr = Mathf.Max(tm.m_levelRadius, tm.m_smoothRadius); if (rr > gwMaxR) gwMaxR = rr;
            }
            nmods = mods.Count;
            if (mods.Count == 0) return null;
            mods.Sort(Voxheim.Core.ModifierDescriptor.CompareVanilla);

            // Vanilla base heights + corner biomes for this zone (same inputs Voxheim's authority uses).
            var hmb = HeightmapBuilder.instance;
            if (hmb == null) return null;
            HeightmapBuilder.HMBuildData bd;
            try { bd = hmb.RequestTerrainSync(center, width, scale, false, wg); }
            catch (Exception e) { Plugin.Log.LogError($"[PT] HMBuildData: {e.Message}"); return null; }
            if (bd == null || bd.m_baseHeights == null || bd.m_cornerBiomes == null || bd.m_cornerBiomes.Length != 4) return null;

            var hArr = bd.m_baseHeights.ToArray();
            var vanillaHArr = (float[])hArr.Clone();   // pure vanilla (before groundwork) — the footprint reference
            int seed = (wg.m_world != null) ? wg.m_world.m_seed : 0;
            try
            {
                var input = new Voxheim.Core.HeadlessZoneInput(
                    center.x, center.y, center.z, width, scale,
                    (Voxheim.Core.HeadlessBiome)(int)bd.m_cornerBiomes[0].Biome,
                    (Voxheim.Core.HeadlessBiome)(int)bd.m_cornerBiomes[1].Biome,
                    (Voxheim.Core.HeadlessBiome)(int)bd.m_cornerBiomes[2].Biome,
                    (Voxheim.Core.HeadlessBiome)(int)bd.m_cornerBiomes[3].Biome,
                    hArr, mods, null, null, 8f, seed);
                Voxheim.Core.HeadlessBaselineBuilder.ApplyGroundworkInPlace(hArr, mods, input);
            }
            catch (Exception e) { Plugin.Log.LogError($"[PT] groundwork build: {e.Message}"); return null; }

            var resultGrid = new Voxheim.Core.HeightGrid(width, scale, hArr);
            var vanGrid    = new Voxheim.Core.HeightGrid(width, scale, vanillaHArr);
            vanillaGrid = vanGrid;
            Plugin._groundworkCache[gwKey] = (resultGrid, vanGrid, nmods);   // remember for deterministic revisit
            return resultGrid;
        }

        static Type? _extractorType;
        static MethodInfo? _tryBuildMethod;
        static bool _extractorResolved;

        // Calls Voxheim.Unity.AuthorityHeadlessExtractor.TryBuild (internal static) via reflection to obtain
        // the deterministic groundworked baseline: vanilla heights + location modifiers (registry/prefab +
        // ZDOs, correct rotation), built the same way every load. Null carrier ⇒ groundwork only (no player
        // edits). Out: EffectiveHeights, and the count of columns that differ from BaseHeights (footprint).
        static bool TryVoxheimGroundwork(float centerX, float centerZ, int width, float scale,
            out float[]? effective, out int changed)
        {
            effective = null; changed = 0;
            if (!_extractorResolved)
            {
                _extractorResolved = true;
                _extractorType = Plugin.FindType("Voxheim.Unity.AuthorityHeadlessExtractor", "Voxheim");
                _tryBuildMethod = _extractorType?.GetMethod("TryBuild", BindingFlags.NonPublic | BindingFlags.Static);
                if (_tryBuildMethod == null)
                    Plugin.Log.LogWarning("[ProceduralTerrain] AuthorityHeadlessExtractor.TryBuild not found — using fallback groundwork gather.");
            }
            if (_tryBuildMethod == null) return false;

            if (ZoneSystem.instance == null) return false;
            var zoneIdx = ZoneSystem.GetZone(new Vector3(centerX, 0f, centerZ));
            var zoneKey = new Voxheim.Core.ZoneKey(zoneIdx.x, zoneIdx.y);

            var args = new object?[] { zoneKey, null, width, scale, null, null };  // out baseline=[4], failure=[5]
            bool ok;
            try { ok = (bool)_tryBuildMethod.Invoke(null, args)!; }
            catch (Exception e) { Plugin.Log.LogWarning($"[ProceduralTerrain] TryBuild threw: {e.InnerException?.Message ?? e.Message}"); return false; }
            if (!ok || args[4] is not Voxheim.Core.HeadlessBaselineResult baseline)
            {
                Plugin.Log.LogWarning($"[PT-gw] TryBuild({zoneKey}) failed: {(args[5] as string) ?? "no baseline"}");
                return false;
            }

            var eff = baseline.EffectiveHeights;
            var baseH = baseline.BaseHeights;
            int n = eff.Count;
            effective = new float[n];
            for (int i = 0; i < n; i++)
            {
                effective[i] = eff[i];
                if (i < baseH.Count && Math.Abs(eff[i] - baseH[i]) > 0.01f) changed++;
            }
            Plugin.Log.LogInfo($"[PT-gw] zone {zoneKey} extractor ok: changed={changed}/{n} (0 ⇒ extractor found no location modifiers here)");
            return true;
        }
    }

} // namespace ProceduralTerrain
