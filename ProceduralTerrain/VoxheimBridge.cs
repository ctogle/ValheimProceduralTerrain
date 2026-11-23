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
            double carveMs = 0, gwMs = 0;

            // Carve/read a PURELY PROCEDURAL baseline — identical for baked and live. Carve internally
            // serves a bake HIT (PopulateSparseFromBake) or carves live; either way the result has NO
            // location groundwork. Groundwork is a distinct live overlay applied below.
            if (!haveCache)
            {
                long _tc0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try { Plugin.Carve(context.Heights, ox, oz, width, context.InitialScanLo, context.InitialScanHi, context.Heights.Scale, context.Heights, context.Floor.Y); }
                catch (Exception e) { Plugin.Log.LogError($"[PT] Carve ({ox},{oz}): {e.Message}"); return; }
                carveMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _tc0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (carveMs > 3.0) Plugin.Log.LogDebug($"[PT-load] Carve ({ox},{oz}) = {carveMs:F2}ms (live/unbaked)");
                if (!Plugin._sdfCache.TryGetValue(zoneKey, out c)) return;
            }

            // Groundwork overlay (distinct LIVE step): the groundworked surface = context.Heights (vanilla's complete
            // mod-applied heightmap); vanGrid = the pre-modifier base. Patched columns (where they differ = the
            // location pads) emit the groundworked surface instead of PT's procedural terrain.
            Voxheim.Core.HeightGrid gwGrid = null, vanGrid = null;
            if (Plugin._applyGroundwork)
            {
                long _tgw0 = System.Diagnostics.Stopwatch.GetTimestamp();
                gwGrid = BuildGroundworkedHeights(context, out int nmods, out vanGrid);
                if (gwGrid == null || nmods == 0) { gwGrid = null; }
                gwMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _tgw0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
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
                    int wy = c.extLo + start + k;
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
            // HITCH quantifier: one LogInfo line only when a zone's main-thread serve cost blows the frame budget,
            // with the breakdown so the cause is unambiguous (carve = bake-decode/live-carve, gw = groundwork,
            // set = Field.Set blit, live = was it unbaked, gen2 = big GC pause). Flat = hitch is outside PT.
            // Feed the frame watchdog, SPLIT so we can tell what prefetch could actually move off-thread:
            // carve (prefetch-addressable) vs set=Field.Set blit (unconditional, prefetch can't help) vs gw.
            PtPerf.CarveMs += carveMs;
            PtPerf.GwMs    += gwMs;
            PtPerf.SetMs   += setMs;

            // ── m_heights write (surface-of-truth): push PT's top surface into the vanilla Heightmap so 2D
            // consumers (FarLands' capture, the minimap, anything reading Heightmap.GetWorldHeight) inherit the
            // REAL surface instead of vanilla — removing the need for bespoke bridges. worldY = m_heights[i] +
            // transform.position.y, so we store (surfaceY - baseY). Uses the live surfH (TrySampleSurfaceY) = the
            // exact surface real content conforms to. The voxel field regenerates from the ABSOLUTE bake, so this
            // write never feeds back into PT's own generation (base-fill decouple pending for the unbaked path).
            WritePtSurfaceToHeightmap(context);

            // [0.0.3 diag] per-ZONE serve cost (not frame-aggregated → immune to the PtPerf/HitchMonitor frame-order
            // skew). This runs inside Voxheim's main-thread Activate phase, so a fat line here IS the `activate` hitch.
            // Logs only zones that blow the budget, with the split so we can see bake-decode vs groundwork vs blit,
            // and whether the zone was live-carved (bake miss) vs served from the bake.
            double serveMs = carveMs + gwMs + setMs;
            if (serveMs > 6.0)
                Plugin.Log.LogInfo($"[PT-serve] zone ({ox},{oz}) = {serveMs:F1}ms  carve={carveMs:F1} gw={gwMs:F1} set={setMs:F1}  live={_genThisZone} gc2+{d2}");
        }

        // Write PT's top surface into the zone's vanilla Heightmap.m_heights so 2D consumers inherit it.
        // worldY(vertex) = m_heights[idx] + hmap.transform.position.y  →  m_heights[idx] = surfaceY - baseY.
        static void WritePtSurfaceToHeightmap(Voxheim.Core.VoxelBaselineContext context)
        {
            try
            {
                var hm = Heightmap.FindHeightmap(new Vector3(context.CenterX, context.CenterY, context.CenterZ));
                if (hm == null) return;
                int w = hm.m_width, num = w + 1;         // m_heights is private; write via public SetHeight(x,y,h)
                var tp = hm.transform.position;
                float half = w * hm.m_scale * 0.5f;
                int wrote = 0;
                for (int yy = 0; yy < num; yy++)
                for (int xx = 0; xx < num; xx++)
                {
                    float wx = tp.x - half + xx * hm.m_scale;
                    float wz = tp.z - half + yy * hm.m_scale;
                    if (Plugin.TrySampleSurfaceY(wx, wz, out float sh))
                    { hm.SetHeight(xx, yy, sh - tp.y); wrote++; }   // raw m_heights value; worldY = this + tp.y
                }
                // Record this zone as "PT's lift is now in FarLands' captured base" — the RELIABLE gate the
                // FarLands delta needs (vs FarLands' own PlayerEdited, which lied for captured wilderness).
                int zx = Mathf.FloorToInt((tp.x + 32f) / 64f), zz = Mathf.FloorToInt((tp.z + 32f) / 64f);
                Plugin._ptHeightmapWritten.TryAdd((zx, zz), 1);
                if ((++_mhWriteN) % 25 == 1)
                    Plugin.Log.LogInfo($"[PT-mheights] wrote {wrote}/{num * num} verts into Heightmap at ({tp.x:F0},{tp.z:F0}) (base={tp.y:F1})");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[PT-mheights] write failed: {e.Message}"); }
        }
        static int _mhWriteN;

        // Groundworked surface = context.Heights = hmap.m_heights = vanilla's COMPLETE ApplyModifiers result (every
        // location mod, incl. dungeon-generated, guaranteed present by Voxheim's POI departure barrier). A base
        // reference (the heightmap's pre-modifier heights) tells us which columns a location changed (the pads).
        // No gather, no templates, no rotation, no re-carve, no race — we read vanilla's own answer.
        internal static Voxheim.Core.HeightGrid BuildGroundworkedHeights(Voxheim.Core.VoxelBaselineContext context, out int nmods, out Voxheim.Core.HeightGrid vanillaGrid)
            => BuildGroundworkedFromContext(context, out nmods, out vanillaGrid);

        static System.Reflection.FieldInfo _hmBuildDataField;   // Heightmap.m_buildData (private) → base heights, free
        internal static Voxheim.Core.HeightGrid BuildGroundworkedFromContext(
            Voxheim.Core.VoxelBaselineContext context, out int nmods, out Voxheim.Core.HeightGrid vanillaGrid)
        {
            nmods = 0; vanillaGrid = null;
            int width = context.Width; float scale = context.Scale;
            var center = new Vector3(context.CenterX, context.CenterY, context.CenterZ);
            // Base reference = the SAME Heightmap's pre-modifier heights (m_buildData.m_baseHeights). Voxheim already
            // built it; PT's m_heights surface-write never touches m_buildData, so it stays pure vanilla base. Free —
            // no RequestTerrainSync. Fall back to a sync build only if the heightmap/build-data isn't ready yet.
            List<float> baseHeights = null;
            var hmap = Heightmap.FindHeightmap(center);
            if (hmap != null && hmap.m_width == width)
            {
                _hmBuildDataField ??= typeof(Heightmap).GetField("m_buildData", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (_hmBuildDataField?.GetValue(hmap) is HeightmapBuilder.HMBuildData bdLocal && bdLocal.m_baseHeights != null)
                    baseHeights = bdLocal.m_baseHeights;
            }
            if (baseHeights == null)
            {
                var hmb = HeightmapBuilder.instance; var wg = WorldGenerator.instance;
                if (hmb == null || wg == null) return null;
                HeightmapBuilder.HMBuildData bd;
                try { bd = hmb.RequestTerrainSync(center, width, scale, false, wg); }
                catch (Exception e) { Plugin.Log.LogError($"[PT] gwFromCtx base: {e.Message}"); return null; }
                if (bd == null || bd.m_baseHeights == null) return null;
                baseHeights = bd.m_baseHeights;
            }
            var van = new Voxheim.Core.HeightGrid(width, scale, baseHeights.ToArray());   // pure vanilla base
            var gw  = context.Heights;                                                         // mod-applied surface
            int diffs = 0;
            for (int ci = 0; ci < width; ci++)
            for (int ri = 0; ri < width; ri++)
                if (Math.Abs(gw[ci, ri] - van[ci, ri]) > 0.05f) diffs++;
            nmods = diffs;
            vanillaGrid = van;
            if (diffs == 0) return null;   // no location changed this zone
            if ((++Plugin._gwCalls) % 50 == 1)
                Plugin.Log.LogInfo($"[PT-gwctx] zone@({context.CenterX:F0},{context.CenterZ:F0}) padCols={diffs} (groundwork straight from the heightmap)");
            return gw;
        }

    }

} // namespace ProceduralTerrain
