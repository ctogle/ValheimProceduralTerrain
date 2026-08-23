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
        const float LocFadeR = 10f; // fade band outside the exterior radius

        // ── Location cache ────────────────────────────────────────────────────────
        static List<(Vector3 pos, float extR)> ReadLocationInstances()
        {
            var result = new List<(Vector3, float)>();
            var zs = ZoneSystem.instance;
            if (zs == null || _locationInstancesField == null) return result;
            var dict = _locationInstancesField.GetValue(zs);
            if (dict == null) return result;
            if (dict.GetType().GetProperty("Values")?.GetValue(dict) is not System.Collections.IEnumerable vals) return result;
            FieldInfo? mPos = null, mLoc = null;
            foreach (var inst in vals)
            {
                if (inst == null) continue;
                mPos ??= inst.GetType().GetField("m_position");
                mLoc ??= inst.GetType().GetField("m_location");
                if (mPos?.GetValue(inst) is not Vector3 pos) continue;
                float extR = mLoc?.GetValue(inst) is ZoneSystem.ZoneLocation zloc ? zloc.m_exteriorRadius : 0f;
                result.Add((pos, extR));
            }
            return result;
        }

        static void BuildLocationCache()
        {
            if (_locationsCached) return;
            var entries = ReadLocationInstances();
            if (entries.Count == 0) return;
            _locationPositions.Clear();
            foreach (var (pos, extR) in entries)
                _locationPositions.Add((new Vector2(pos.x, pos.z), extR));
            _locationsCached  = true;
            _hotReloadPending = true;
            Log.LogInfo($"[ProceduralTerrain] Location cache built: {_locationPositions.Count} location(s) loaded.");
        }

        static float LocFactor((Vector2 pos, float extR) loc, float worldX, float worldZ)
        {
            float dx = worldX - loc.pos.x, dz = worldZ - loc.pos.y;
            float d  = Mathf.Sqrt(dx * dx + dz * dz);
            float r  = Mathf.Max(loc.extR, LocProtectR);
            return Mathf.Clamp01((d - r) / LocFadeR);
        }

        static float LocDist((Vector2 pos, float extR) loc, float worldX, float worldZ)
        {
            float dx = worldX - loc.pos.x, dz = worldZ - loc.pos.y;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        static float LocationFactor(float worldX, float worldZ, List<(Vector2 pos, float extR)> nearbyLocs)
        {
            if (!_protectLocations || nearbyLocs.Count == 0) return 1f;
            float minFactor = 1f;
            for (int i = 0; i < nearbyLocs.Count; i++)
            {
                float f = LocFactor(nearbyLocs[i], worldX, worldZ);
                if (f < minFactor) minFactor = f;
            }
            return minFactor;
        }
    }

} // namespace ProceduralTerrain
