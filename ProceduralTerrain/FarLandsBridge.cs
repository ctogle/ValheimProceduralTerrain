using System;
using System.Reflection;
using UnityEngine;
using BepInEx.Bootstrap;

// ─────────────────────────────────────────────────────────────────────────────
//  PT ↔ FarLands integration.
//
//  FarLands (ru.ivest.farlands) synthesises the distant world (≤2500m, GPU) from the VANILLA
//  world generator + players' saved terrain edits — it has no idea PT reshaped the terrain via the
//  Voxheim voxel engine. So far off it draws vanilla heights while PT-conformed vegetation sits at
//  PT's surface → trees float. FarLands publishes an API for exactly this: a mod feeds a per-point
//  height `delta` (and optional paint/clearing) so the far land follows its changes.
//
//  PT supplies ONLY a height delta (PT surface − vanilla), read from the loaded bake (whole-world,
//  deterministic). PT does not paint ground or clear trees, so those delegates are null. `print`
//  returns the bake cfgHash so FarLands rebuilds its cached far land exactly when PT terrain changes.
//
//  Reflection-only (no hard ref to FarLands.dll); a no-op when FarLands isn't installed.
// ─────────────────────────────────────────────────────────────────────────────
namespace ProceduralTerrain
{
    internal static class FarLandsBridge
    {
        const string FarLandsGuid = "ru.ivest.farlands";
        static bool _registered;
        static object _handle;
        static MethodInfo _removeMethod;
        static Func<int, int, bool> _playerEdited;   // FarLandsLand.PlayerEdited — zones already carrying PT's lift

        internal static bool IsPresent => Chainloader.PluginInfos.ContainsKey(FarLandsGuid);

        // Call once the PT bake is loaded and data is ready (delta needs the bake to sample).
        internal static void TryRegister()
        {
            if (_registered) return;
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue(FarLandsGuid, out var info) || info?.Instance == null)
                    return;   // FarLands not installed — nothing to do
                var asm = info.Instance.GetType().Assembly;
                var landType = asm.GetType("FarLands.FarLandsLand");
                if (landType == null) { Plugin.Log.LogWarning("[PT-FarLands] FarLandsLand type not found; skipping."); return; }

                int apiVersion = (int)(landType.GetField("ApiVersion")?.GetValue(null) ?? 0);
                if (apiVersion != 1) { Plugin.Log.LogWarning($"[PT-FarLands] API version {apiVersion} != 1; skipping (won't risk a contract mismatch)."); return; }

                var add = landType.GetMethod("Add", BindingFlags.Public | BindingFlags.Static);
                _removeMethod = landType.GetMethod("Remove", BindingFlags.Public | BindingFlags.Static);
                if (add == null) { Plugin.Log.LogWarning("[PT-FarLands] Add method not found; skipping."); return; }

                // PlayerEdited(zoneX,zoneZ): true where FarLands already holds the zone's captured heights. Voxheim
                // writes PT's surface into the loaded Heightmap.m_heights, which FarLands captures — so in those
                // zones the PT lift is ALREADY in FarLands' base. Per the API ("return 0 from delta there, or it
                // counts twice"), we must return 0 there to avoid double-applying PT (the 2×-above-sea rock dupes).
                // Bind it as a fast delegate (delta is called ~20k×/tile — no per-call reflection).
                var peMethod = landType.GetMethod("PlayerEdited", BindingFlags.Public | BindingFlags.Static);
                if (peMethod != null)
                    _playerEdited = (Func<int, int, bool>)Delegate.CreateDelegate(typeof(Func<int, int, bool>), peMethod);
                else
                    Plugin.Log.LogWarning("[PT-FarLands] PlayerEdited not found — can't guard against double-count in captured zones.");

                Func<float, float, float, float> delta = Delta;
                Func<float, float, float, float, float, uint> print = Print;
                // paint and cleared are null: PT changes neither ground paint nor tree clearing.
                _handle = add.Invoke(null, new object[] { "ProceduralTerrain", delta, null, print, null });
                _registered = true;
                Plugin.Log.LogInfo("[PT-FarLands] Registered PT height delta with FarLands — distant terrain will follow PT's surface.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[PT-FarLands] registration failed: {e.Message}");
            }
        }

        internal static void Unregister()
        {
            try { if (_registered && _handle != null && _removeMethod != null) _removeMethod.Invoke(null, new[] { _handle }); }
            catch { /* ignore */ }
            _registered = false; _handle = null;
        }

        // Metres PT adds to the land at (x,z): PT's baked surface minus the vanilla world-gen height.
        // Runs on FarLands builder threads — must be thread-safe, no Unity object access beyond the pure
        // WorldGenerator.GetHeight (which PT already calls off-thread during baking). Returns 0 where PT has
        // no bake coverage for the point (far land stays vanilla there rather than guessing).
        static float Delta(float x, float z, float spacing)
        {
            try
            {
                // Already-captured zones carry PT's lift in FarLands' own base (Voxheim wrote it into m_heights) →
                // return 0 or it double-counts. Zone is 64m, centred at (zx*64, zz*64) per ZoneSystem.GetZone.
                if (_playerEdited != null)
                {
                    int zx = Mathf.FloorToInt((x + 32f) / 64f);
                    int zz = Mathf.FloorToInt((z + 32f) / 64f);
                    if (_playerEdited(zx, zz)) return 0f;
                }
                if (!Plugin.TryGetBakeSurfaceY(x, z, out float ptY)) return 0f;
                var wg = WorldGenerator.instance;
                if (wg == null) return 0f;
                float vanilla = wg.GetHeight(x, z);
                return ptY - vanilla;
            }
            catch { return 0f; }
        }

        // One number for everything PT does inside the rect. PT's terrain is fully determined by seed+cfgHash
        // (the bake is keyed on it), so the cfgHash IS the version: same terrain → same print, changed cfg →
        // new print → FarLands rebuilds. 0 when no bake is loaded (PT contributes nothing to the far view then).
        static uint Print(float minX, float minZ, float maxX, float maxZ, float spacing)
        {
            try
            {
                if (Plugin._bakeData == null && Plugin._bakeIndex == null) return 0u;
                return Plugin.ComputeCfgHash();
            }
            catch { return 0u; }
        }
    }
}
