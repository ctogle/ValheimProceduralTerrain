using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ProceduralTerrain
{
    // Loader for the PLACEMENT config — a file separate from the terrain-shaping cfg. This one owns
    // [Vegetation], [CaveContent], and the [Placement:*] rules (ore/scenery/creature spawns in caves).
    // Terrain shaping stays in procedural_terrain_biomes.cfg; placement of things lives here.
    public partial class Plugin
    {
        internal static string PlacementConfigPath =>
            Path.Combine(PTConfigDir, "placements.cfg");

        static DateTime _placementCfgLastWrite = DateTime.MinValue;

        internal static void LoadPlacementConfig()
        {
            string path = PlacementConfigPath;
            if (!File.Exists(path))
            {
                WriteDefaultPlacementConfig(path);
            }

            // When connected to a server, the server's placements override the local file (applied in-memory,
            // local file untouched) — same pattern as biomes.cfg. Placements are server-authoritative.
            string[] lines = _serverPlacementLines ?? File.ReadAllLines(path);

            // Read sections (same shape as the terrain cfg: [Section] then key = value, # comments).
            var sections = new List<(string name, List<(string key, string val)> kvs)>();
            List<(string, string)>? cur = null;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] == '[')
                {
                    cur = new List<(string, string)>();
                    sections.Add((line.Trim('[', ']').Trim(), cur));
                    continue;
                }
                if (cur == null) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                var rawVal = line.Substring(eq + 1);
                int hashIdx = rawVal.IndexOf('#');
                if (hashIdx >= 0) rawVal = rawVal.Substring(0, hashIdx);
                cur.Add((line.Substring(0, eq).Trim().ToLowerInvariant(), rawVal.Trim()));
            }

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var parsed = new List<Placement>();
            foreach (var (secName, kvs) in sections)
            {
                if (secName.StartsWith("Placement:", StringComparison.OrdinalIgnoreCase))
                {
                    var pl = ParsePlacement(secName.Substring(10).Trim(), kvs);
                    if (pl != null) parsed.Add(pl);
                }
                else if (secName.Equals("CaveContent", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (k, v) in kvs)
                    {
                        if (k == "enabled") _caveContent = (v == "1" || v == "true");
                        else if (k == "objectprob"     && float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out var op)) _ccObjectProb = Mathf.Max(0f, op);
                        else if (k == "objectcap"      && int.TryParse(v, out var ocp)) _ccObjectCap = Mathf.Max(0, ocp);
                        else if (k == "objectspacing"  && float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out var osp)) _ccObjectSpacing = Mathf.Max(0f, osp);
                        else if (k == "spawnerprob"    && float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out var sp)) _ccSpawnerProb = Mathf.Max(0f, sp);
                        else if (k == "spawnercap"     && int.TryParse(v, out var scp)) _ccSpawnerCap = Mathf.Max(0, scp);
                        else if (k == "spawnerspacing" && float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out var ssp)) _ccSpawnerSpacing = Mathf.Max(0f, ssp);
                    }
                }
                else if (secName.Equals("Vegetation", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (k, v) in kvs)
                    {
                        if (k == "disable") _disableVegetation = v == "1" || v == "true";
                        else if (k == "conform") _vegetationConform = !(v == "0" || v == "false");
                        else if (k == "maxtilt" && float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out float vmt))
                            _vegMaxTilt = Mathf.Clamp(vmt, 0f, 90f);
                        else if (k == "supportradius" && float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out float vsr))
                            _vegSupportRadius = Mathf.Clamp(vsr, 0.5f, 8f);
                    }
                }
                else if (secName.Equals("Location", StringComparison.OrdinalIgnoreCase))
                {
                    // Location protection: keeps world locations (dungeons/bosses/villages) reachable. Server-authoritative
                    // (so clients inherit it) but NOT hashed — applied live as an overlay, doesn't change the baked .bin.
                    foreach (var (k, v) in kvs)
                    {
                        if (k == "protectlocations") _protectLocations = !(v == "0" || v == "false");
                        else if (k == "applygroundwork") _applyGroundwork = !(v == "0" || v == "false");
                        else if (k == "flatplanelocations") _flatPlaneLocations = !(v == "0" || v == "false");
                        else if (k == "locdomeheight" && float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out float ldh))
                            _locDomeHeight = Mathf.Max(0f, ldh);
                        else if (k == "locbandfade" && float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out float lbf))
                            _locBandFade = Mathf.Max(0.001f, lbf);
                        else if (k == "locfadedist" && float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out float lfd))
                            LocFadeR = Mathf.Max(0.001f, lfd);
                        else if (k == "locminradius" && float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out float lmr))
                            LocProtectR = Mathf.Max(0f, lmr);
                    }
                }
            }

            // cfg-defined set, or built-in Mountain defaults if cavecontent is on but none were authored.
            _placements = parsed.Count > 0 ? parsed
                        : (_caveContent ? BuildBuiltinPlacements() : new List<Placement>());
            InvalidatePlacementResolution();
            _placementCfgLastWrite = File.GetLastWriteTimeUtc(path);
            Log.LogInfo($"[ProceduralTerrain] Placement config loaded: {_placements.Count} placements, cavecontent={_caveContent}, veg(conform={_vegetationConform},maxTilt={_vegMaxTilt:F0},disable={_disableVegetation}), loc(protect={_protectLocations},fadeDist={LocFadeR:F0},minRadius={LocProtectR:F0},domeH={_locDomeHeight:F0}).");
        }

        // Hot-reload: reload the placement cfg when its file changes. Placements only affect spawns at
        // zone-generation time, so no recarve is needed — newly generated zones pick up the new rules.
        // Returns true if the file changed and was reloaded (so the caller can recarve — the [Location]
        // keys are terrain-affecting; veg/cavecontent keys only matter for newly-loaded zones, but a recarve
        // covers both and keeps live tuning honest).
        internal static bool WatchPlacementConfigReload()
        {
            var t = File.GetLastWriteTimeUtc(PlacementConfigPath);
            if (t == _placementCfgLastWrite) return false;
            _placementCfgLastWrite = t;
            LoadPlacementConfig();
            return true;
        }

        // Write the shipped placement config (embedded at build from configs/placements.cfg) to disk on first
        // launch. Only called when the file is absent, so player edits are never clobbered on update.
        static void WriteDefaultPlacementConfig(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, ReadEmbeddedConfig("ProceduralTerrain.placements.cfg"));
            Log.LogInfo($"[ProceduralTerrain] Wrote default placement config to {path}");
        }
    }
}
