using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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

            // HeightGrid Scale delegate
            var gridT      = FindType("Voxheim.Core.HeightGrid", "Voxheim.Core");
            var gridScaleP = gridT?.GetProperty("Scale", BindingFlags.Public | BindingFlags.Instance);
            if (gridT != null && gridScaleP?.GetMethod != null)
            {
                var sdm = new DynamicMethod("HG_Scale", typeof(float),
                    new[] { typeof(object) }, typeof(Plugin).Module, skipVisibility: true);
                var sgi = sdm.GetILGenerator();
                sgi.Emit(OpCodes.Ldarg_0); sgi.Emit(OpCodes.Castclass, gridT);
                sgi.Emit(OpCodes.Callvirt, gridScaleP.GetMethod);
                sgi.Emit(OpCodes.Ret);
                _gridScale = (Func<object,float>)sdm.CreateDelegate(typeof(Func<object,float>));
                Log.LogInfo("[ProceduralTerrain] HeightGrid.Scale delegate ready — scale read from grid, no s_scale dependency.");
            }
            else { Log.LogWarning("[ProceduralTerrain] HeightGrid.Scale not found — falling back to s_scale."); }

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
            Plugin._sdfCache.Remove(key);
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

            builder.SetFloor(context.Floor);

            string zoneKey = $"{ox},{oz}";
            if (!Plugin._sdfCache.TryGetValue(zoneKey, out var c))
            {
                float gridS = Plugin._gridScale != null ? Plugin._gridScale(context.Heights) : 0f;
                try { Plugin.Carve(context.Heights, ox, oz, width, context.InitialScanLo, context.InitialScanHi, gridS); }
                catch (Exception e) { Plugin.Log.LogError($"[PT] Carve ({ox},{oz}): {e.Message}"); return; }
                if (!Plugin._sdfCache.TryGetValue(zoneKey, out c)) return;
            }

            for (int ci = 0; ci < width; ci++)
            for (int ri = 0; ri < width; ri++)
            {
                int wx = ox + ci, wz = oz + ri;
                for (int cy = 0; cy < c.bh; cy++)
                {
                    sbyte d = c.data[(ci * c.bh + cy) * c.width + ri];
                    int   wy = c.extLo + cy;
                    builder.Set(new Voxheim.Core.Int3(wx, wy, wz), d);
                }
            }
        }
    }

} // namespace ProceduralTerrain
