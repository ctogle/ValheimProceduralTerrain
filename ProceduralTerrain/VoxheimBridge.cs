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

            _finishSkinNowMethod = zoneT?.GetMethod("FinishSkinNow",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _activateMethod      = zoneT?.GetMethod("Activate",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _unskinMethod        = zoneT?.GetMethod("Unskin",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _hmapField           = zoneT?.GetField("_hmap",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (_finishSkinNowMethod == null) Log.LogWarning("[ProceduralTerrain] FinishSkinNow not found.");
            if (_activateMethod      == null) Log.LogWarning("[ProceduralTerrain] Activate not found — hot-reload will not work.");
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
            bool haveCache = Plugin._sdfCache.TryGetValue(zoneKey, out var c);

            // World-edit conform: the authority path never hands PT the location modifiers
            // (context.HeadlessBaseline is null during play), so we gather the placed terrain
            // modifiers ourselves (TerrainModifier.GetAllInstances — locations + player edits) and
            // bake them into our baseline heights via Voxheim's own ApplyGroundworkInPlace. Only do
            // this expensive step when we'll actually carve: a fresh zone, or the first time we can
            // fold modifiers into a zone that was cached before they were placed.
            if (!haveCache || !Plugin._groundworkedZones.ContainsKey(zoneKey))
            {
                var heights = BuildGroundworkedHeights(context, out int nmods) ?? context.Heights;
                if (nmods > 0 && Plugin._groundworkedZones.TryAdd(zoneKey, 0))
                {
                    if (haveCache) { Plugin._sdfCache.TryRemove(zoneKey, out _); haveCache = false; }
                    Plugin.Log.LogInfo($"[PT-conform] zone ({ox},{oz}) groundwork: {nmods} modifier(s) baked into baseline");
                }

                if (!haveCache)
                {
                    try { Plugin.Carve(heights, ox, oz, width, context.InitialScanLo, context.InitialScanHi, heights.Scale, heights); }
                    catch (Exception e) { Plugin.Log.LogError($"[PT] Carve ({ox},{oz}): {e.Message}"); return; }
                    if (!Plugin._sdfCache.TryGetValue(zoneKey, out c)) return;
                }
            }

            builder.SetFloor(context.Floor);
            builder.IncludeY(c.extLo, c.extLo + c.bh - 1);

            for (int ci = 0; ci < width; ci++)
            for (int ri = 0; ri < width; ri++)
            {
                int wx = ox + ci, wz = oz + ri;
                for (int cy = 0; cy < c.bh; cy++)
                {
                    sbyte d = c.data[(ci * c.bh + cy) * c.width + ri];
                    int   wy = c.extLo + cy;
                    builder.Field.Set(new Voxheim.Core.Int3(wx, wy, wz), d);
                }
            }
        }

        // Build a per-zone height grid with all overlapping location/player terrain ops baked in,
        // using vanilla HMBuildData (base heights + corner biomes) + Voxheim's ApplyGroundworkInPlace.
        // Returns null (and nmods=0) when there are no modifiers or worldgen isn't ready.
        static Voxheim.Core.HeightGrid BuildGroundworkedHeights(Voxheim.Core.VoxelBaselineContext context, out int nmods)
        {
            nmods = 0;
            var wg = WorldGenerator.instance;
            if (wg == null) return null;

            int width   = context.Width;
            float scale = context.Scale;
            var center  = new Vector3(context.CenterX, context.CenterY, context.CenterZ);
            float half  = width * scale * 0.5f;

            // Gather placed terrain modifiers (locations + player edits) overlapping this zone.
            var mods = new List<Voxheim.Core.ModifierDescriptor>();
            var all  = TerrainModifier.GetAllInstances();
            for (int i = 0; i < all.Count; i++)
            {
                var tm = all[i];
                if (tm == null) continue;
                var p = tm.transform.position;
                float r = tm.GetRadius();
                if (Math.Abs(p.x - center.x) > half + r + 4f) continue;
                if (Math.Abs(p.z - center.z) > half + r + 4f) continue;
                mods.Add(new Voxheim.Core.ModifierDescriptor(
                    p.x, p.y, p.z, tm.m_playerModifiction, tm.m_sortOrder, 0L,
                    tm.m_levelOffset, tm.m_level, tm.m_levelRadius,
                    tm.m_smooth, tm.m_smoothRadius, tm.m_smoothPower,
                    tm.m_square, tm.m_paintCleared, tm.m_paintRadius));
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

            return new Voxheim.Core.HeightGrid(width, scale, hArr);
        }
    }

} // namespace ProceduralTerrain
