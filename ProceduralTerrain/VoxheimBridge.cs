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

        // ── Brick-blit writer ─────────────────────────────────────────────────────
        // Full-res volumetric terrain fills ~85% of a zone's voxels, so Generate was doing ~240k per-voxel
        // VoxelField.Set calls (each a brick dictionary lookup) ≈ 13ms/zone → stutter. Voxheim's field is 16³
        // bricks; a column's Y-run touches the same brick for 16 consecutive voxels. This caches the current
        // brick and writes straight to it via the public Brick.Set, doing the dictionary lookup only on the
        // first voxel of each brick (routed through Field.Set so the brick is created with the correct
        // background + min/maxY bookkeeping). Brick tracking is off during baseline build, so the direct
        // writes are equivalent to Field.Set. Falls back to Field.Set if the _bricks field can't be reflected.
        internal sealed class BrickWriter
        {
            static FieldInfo _bricksFI;
            static bool _resolved;
            readonly Voxheim.Core.VoxelField _field;
            readonly Dictionary<Voxheim.Core.Int3, Voxheim.Core.Brick> _bricks;
            Voxheim.Core.Brick _cur;
            int _kx, _ky, _kz;
            bool _have;
            internal BrickWriter(Voxheim.Core.VoxelField field)
            {
                _field = field;
                if (!_resolved)
                {
                    _bricksFI = typeof(Voxheim.Core.VoxelField).GetField("_bricks", BindingFlags.NonPublic | BindingFlags.Instance);
                    _resolved = true;
                    if (_bricksFI == null) Log.LogWarning("[ProceduralTerrain] VoxelField._bricks not found — brick-blit disabled, using per-voxel Set.");
                }
                _bricks = _bricksFI?.GetValue(field) as Dictionary<Voxheim.Core.Int3, Voxheim.Core.Brick>;
            }
            public void Set(int wx, int wy, int wz, sbyte v)
            {
                if (_bricks == null) { _field.Set(new Voxheim.Core.Int3(wx, wy, wz), v); return; }
                int kx = wx >> 4, ky = wy >> 4, kz = wz >> 4;
                if (!_have || kx != _kx || ky != _ky || kz != _kz)
                {
                    _have = _bricks.TryGetValue(new Voxheim.Core.Int3(kx, ky, kz), out _cur);
                    _kx = kx; _ky = ky; _kz = kz;
                }
                if (_have && _cur != null) { _cur.Set(wx & 0xF, wy & 0xF, wz & 0xF, v); return; }
                // Brick doesn't exist yet: let Field.Set create it (background + bookkeeping), then cache it.
                _field.Set(new Voxheim.Core.Int3(wx, wy, wz), v);
                _have = _bricks.TryGetValue(new Voxheim.Core.Int3(kx, ky, kz), out _cur);
                _kx = kx; _ky = ky; _kz = kz;
            }
        }

        // ── Groundwork overlay ────────────────────────────────────────────────────
        // Patches the pad columns of a procedural (baked or live) zone with the groundworked surface. The
        // footprint is where the groundworked height differs from PURE vanilla (location protection guarantees
        // those columns are vanilla-clean in the bake, so overwriting them loses no procedural detail). Per-column
        // heightfield SDF — cheap (no dense eikonal, no stutter). Cached per zone (built once from the cached gw).
        internal sealed class GroundworkOverlay
        {
            readonly int _w;
            readonly float _scale;
            readonly bool[] _ovr;
            readonly float[] _h;     // groundworked height per owned column
            internal GroundworkOverlay(int w, float scale, bool[] ovr, float[] h)
            { _w = w; _scale = scale; _ovr = ovr; _h = h; }
            public bool IsOverride(int ci, int ri) => _ovr[ci * _w + ri];
            public sbyte Sample(int ci, int ri, int wy)
                => ToSDFByte((wy - _h[ci * _w + ri]) * _scale, _scale);
        }

        internal static GroundworkOverlay BuildGroundworkOverlay(
            Voxheim.Core.HeightGrid gw, Voxheim.Core.HeightGrid van, int width, float scale, int extLo, int bh)
        {
            var ovr = new bool[width * width];
            var h   = new float[width * width];
            for (int ci = 0; ci < width; ci++)
            for (int ri = 0; ri < width; ri++)
            {
                int idx = ci * width + ri;
                h[idx] = gw[ci, ri];
                ovr[idx] = Math.Abs(gw[ci, ri] - van[ci, ri]) > 0.05f;
            }
            return new GroundworkOverlay(width, scale, ovr, h);
        }
    }

    // ── Harmony patches ───────────────────────────────────────────────────────────

    // DEBUG: hide Valheim's distant TerrainLod heightmap meshes. They render a low-detail terrain surface
    // at the vanilla height out to ~2400m, which cuts through the view when inspecting voxel caves/features
    // from under/inside the terrain. When _hideLodMeshes is set, skip Update (no rebuild) and SetActive(false)
    // the existing lod heightmaps. Toggle off + relaunch to restore.
    [HarmonyPatch(typeof(TerrainLod), "Update")]
    static class TerrainLodHidePatch
    {
        static bool _hidden;   // transition tracking so we re-show when surfacing

        static void SetAll(TerrainLod inst, bool active)
        {
            if (Traverse.Create(inst).Field("m_heightmaps").GetValue() is System.Collections.IList hms)
                foreach (var o in hms)
                {
                    var hm = Traverse.Create(o).Field("m_heightmap").GetValue<Heightmap>();
                    if (hm != null && hm.gameObject.activeSelf != active) hm.gameObject.SetActive(active);
                }
        }

        static bool Prefix(TerrainLod __instance)
        {
            // Hide when manually forced OR when the player is underground (auto). Dynamic: re-show on surfacing.
            bool hide = Plugin._hideLodMeshes || (Plugin._lodUnderground && Plugin._playerUnderground);
            if (hide)
            {
                if (!_hidden) { SetAll(__instance, false); _hidden = true; }
                return false;   // skip the normal update so meshes aren't rebuilt/re-shown
            }
            if (_hidden) { SetAll(__instance, true); _hidden = false; }   // surfaced → restore
            return true;        // normal LOD update
        }
    }

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
                if (carveMs > 3.0) Plugin.Log.LogDebug($"[PT-load] Carve ({ox},{oz}) = {carveMs:F2}ms (live/unbaked)");
                if (!Plugin._sdfCache.TryGetValue(zoneKey, out c)) return;
            }

            // Groundwork overlay (distinct LIVE step): groundworked surface heights for this zone (cache-backed
            // for revisit determinism). Footprint = where groundwork changed PURE vanilla (context.Heights
            // already has groundwork, so it can't be the reference). Patched columns emit the groundworked surface.
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
            // Brick-blit writer: caches the current 16³ brick so a column's contiguous Y-run writes go straight
            // to the brick array, skipping the per-voxel dictionary lookup that dominated Field.Set at full res.
            var bw = new Plugin.BrickWriter(builder.Field);
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
                        bw.Set(wx, wy, wz, d);
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
                    bw.Set(wx, wy, wz, c.band[off + k]);
                    setCount++;
                }
            }
            double setMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _tSet0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (gwGrid != null) Plugin.Log.LogDebug($"[PT-conform] zone ({ox},{oz}) groundwork overlay: override columns={ovCols}");
            if (setMs > 2.0) Plugin.Log.LogDebug($"[PT-load] Field.Set zone ({ox},{oz}) = {setMs:F2}ms set={setCount} ({width}x{width}x{c.bh})");
            int d0 = GC.CollectionCount(0) - _gc0, d1 = GC.CollectionCount(1) - _gc1, d2 = GC.CollectionCount(2) - _gc2;
            if (d0 > 0 || d1 > 0 || d2 > 0)
                Plugin.Log.LogDebug($"[PT-gc] zone ({ox},{oz}) load triggered GC: gen0+{d0} gen1+{d1} gen2+{d2}  (gen={_genThisZone})");
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
            Plugin.Log.LogDebug($"[PT-gw] zone {zoneKey} extractor ok: changed={changed}/{n} (0 ⇒ extractor found no location modifiers here)");
            return true;
        }
    }

} // namespace ProceduralTerrain
