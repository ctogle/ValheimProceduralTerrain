using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  [0.0.3 diag] SpawnSystem reject-reason probe.
//
//  Question: with PT raising/reshaping terrain, do ambient spawns get rejected (and why), making biomes
//  like the meadows feel empty? SpawnSystem.Spawn places creatures wherever ZoneSystem.GetGroundData's
//  DOWNWARD RAYCAST hits the collider — NOT WorldGenerator.GetHeight — so they're never buried at vanilla
//  height. The emptiness must come from IsSpawnPointGood's filters (biome / altitude band / tilt / blocked).
//
//  IsSpawnPointGood returns a single bool, so to learn WHICH test failed we re-derive it in a postfix
//  (re-run GetGroundData + replay the checks in order). Counts are aggregated and logged every N rejects
//  so the frequent calls don't spam. Temporary — strip once the meadows cause is identified.
// ─────────────────────────────────────────────────────────────────────────────
namespace ProceduralTerrain
{
    [HarmonyPatch(typeof(SpawnSystem), "IsSpawnPointGood")]
    internal static class SpawnRejectProbe
    {
        static readonly Dictionary<string, int> _reasons = new();
        static int _passed, _rejected;
        const int LogEvery = 300;   // log the cumulative tally every this many rejects

        static void Postfix(SpawnSystem.SpawnData spawn, Vector3 spawnPoint, bool __result)
        {
            if (__result) { _passed++; return; }
            _rejected++;

            string reason = "other";
            float alt = spawnPoint.y - 30f, ny = 1f;
            Heightmap.Biome biome = Heightmap.Biome.None;
            try
            {
                var zs = ZoneSystem.instance;
                if (zs == null) { Bump("noZoneSystem"); return; }
                var p = spawnPoint;   // already ground-resolved by the original call
                zs.GetGroundData(ref p, out var normal, out biome, out var area, out _);
                ny  = normal.y;
                alt = p.y - 30f;
                if ((spawn.m_biome & biome) == 0)                                   reason = "biome";
                else if ((spawn.m_biomeArea & area) == 0)                           reason = "biomeArea";
                else if (zs.IsBlocked(p))                                           reason = "blocked";
                else if (alt < spawn.m_minAltitude)                                 reason = "altLow";
                else if (alt > spawn.m_maxAltitude)                                 reason = "altHigh";
                else
                {
                    float cosMax = Mathf.Cos(Mathf.Deg2Rad * spawn.m_maxTilt);
                    float cosMin = Mathf.Cos(Mathf.Deg2Rad * spawn.m_minTilt);
                    if (ny < cosMax || ny > cosMin)                                 reason = "tilt";
                    // else "other" (distance-from-center, ocean depth, group checks, etc.)
                }
            }
            catch { reason = "probeErr"; }

            Bump(reason);
            if (_rejected % LogEvery == 0)
            {
                var parts = new List<string>();
                foreach (var kv in _reasons) parts.Add($"{kv.Key}={kv.Value}");
                parts.Sort();
                Plugin.Log.LogInfo($"[PT-spawnprobe] passed={_passed} rejected={_rejected} | {string.Join(" ", parts)} " +
                                   $"| last: {(spawn.m_prefab != null ? spawn.m_prefab.name : "?")} reason={reason} " +
                                   $"alt={alt:F0} band=[{spawn.m_minAltitude:F0},{spawn.m_maxAltitude:F0}] normal.y={ny:F2} maxTilt={spawn.m_maxTilt:F0} biome={biome}");
            }
        }

        static void Bump(string r) { _reasons.TryGetValue(r, out int c); _reasons[r] = c + 1; }
    }
}
