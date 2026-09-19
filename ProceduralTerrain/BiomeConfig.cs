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
        // ── Biome profile defaults ────────────────────────────────────────────────
        static Dictionary<Heightmap.Biome, CaveProfile> BuildDefaultProfiles() =>
            new Dictionary<Heightmap.Biome, CaveProfile>
            {
                [Heightmap.Biome.Meadows]     = CaveProfile.Default,
                [Heightmap.Biome.BlackForest] = CaveProfile.Default,
                [Heightmap.Biome.Swamp]       = CaveProfile.Default,
                [Heightmap.Biome.Mountain]    = CaveProfile.Default,
                [Heightmap.Biome.Plains]      = CaveProfile.Default,
                [Heightmap.Biome.Mistlands]   = CaveProfile.Default,
                [Heightmap.Biome.AshLands]    = CaveProfile.Default,
                [Heightmap.Biome.DeepNorth]   = CaveProfile.Default,
                [Heightmap.Biome.Ocean]       = CaveProfile.Default,
                [Heightmap.Biome.None]        = CaveProfile.Default,
            };

        // ── Config file ───────────────────────────────────────────────────────────
        internal static string ConfigPath =>
            Path.Combine(BepInEx.Paths.ConfigPath, "ProceduralTerrain", "procedural_terrain_biomes.cfg");

        // Parse a [Mode:name] section into a ModeDefinition subclass.
        static ModeDefinition? ParseModeDefinition(string name, List<(string key, string val)> kvs)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string? typeStr = null;
            foreach (var (k, v) in kvs)
            {
                if (k != "type") continue;
                typeStr = v.Trim().ToUpperInvariant();
                break;
            }
            if (typeStr == null) { Log.LogWarning($"[ProceduralTerrain] Mode '{name}': missing or invalid type."); return null; }

            // Handle Lua mode separately
            if (typeStr == "LUA")
            {
                var luaDef = new LuaModeDef { Name = name };
                var dynParams = new List<(string name, float def)>();
                string? fileVal = null, blendVal = null, blendKStr = null;
                foreach (var (k, v) in kvs)
                {
                    if (k == "type") continue;
                    if (k == "file")    { fileVal  = v.Trim(); continue; }
                    if (k == "blend")   { blendVal  = v.Trim().ToLowerInvariant(); continue; }
                    if (k == "blend_k") { blendKStr = v.Trim(); continue; }
                    if (float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out float fv))
                    {
                        if (k == "amplitude") { luaDef.Amplitude = fv; }
                        else dynParams.Add((k, fv));
                    }
                }
                if (blendVal  != null) luaDef.Blend  = blendVal;
                if (blendKStr != null && float.TryParse(blendKStr, System.Globalization.NumberStyles.Float, inv, out float bk)) luaDef.BlendK = bk;
                // Ensure amplitude is index 0
                var orderedParams = new List<(string name, float def)>();
                orderedParams.Add(("amplitude", luaDef.Amplitude));
                foreach (var p in dynParams) if (p.name != "amplitude") orderedParams.Add(p);
                luaDef.SetDynParams(orderedParams);
                if (fileVal != null)
                    luaDef.FilePath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ConfigPath)!, "modes", fileVal);
                luaDef.LoadScript();
                return luaDef;
            }

            ModeDefinition md = typeStr switch {
                "A" => new ModeADef { Name = name },
                "B" => new ModeBDef { Name = name },
                "C" => new ModeCDef { Name = name },
                "D" => new ModeDDef { Name = name },
                _ => (ModeDefinition?)null!
            };
            if (md == null) { Log.LogWarning($"[ProceduralTerrain] Mode '{name}': unknown type '{typeStr}'."); return null; }

            foreach (var (k, v) in kvs)
            {
                if (k == "type") continue;
                if (!float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out float fv)) continue;
                if (k == "amplitude") { md.Amplitude = fv; continue; }
                switch (md)
                {
                    case ModeADef a:
                        switch (k) {
                            case "frequency":      a.Frequency      = fv; break;
                            case "warpamp":        a.WarpAmp        = fv; break;
                            case "warpampy":       a.WarpAmpY       = fv; break;
                            case "warpfrequency":  a.WarpFrequency  = fv; break;
                            case "vscale":         a.VScale         = fv; break;
                            case "widthvary":      a.WidthVary      = fv; break;
                            case "widthfrequency": a.WidthFrequency = fv; break;
                            case "yoffset":        a.YOffset        = fv; break;
                        }
                        break;
                    case ModeBDef b:
                        switch (k) {
                            case "warpamp":       b.WarpAmp       = fv; break;
                            case "warpfrequency": b.WarpFrequency = fv; break;
                            case "vscale":        b.VScale        = fv; break;
                            case "noiseamp":      b.NoiseAmp      = fv; break;
                            case "yoffset":       b.YOffset       = fv; break;
                            case "twist":         b.Twist         = fv; break;
                        }
                        break;
                    case ModeCDef c:
                        switch (k) {
                            case "threshold":      c.Threshold      = fv; break;
                            case "frequency":      c.Frequency      = fv; break;
                            case "warpamp":        c.WarpAmp        = fv; break;
                            case "warpfrequency":  c.WarpFrequency  = fv; break;
                            case "vscale":         c.VScale         = fv; break;
                            case "twist":          c.Twist          = fv; break;
                            case "twistvary":      c.TwistVary      = fv; break;
                            case "twistfrequency": c.TwistFrequency = fv; break;
                            case "ampvary":        c.AmpVary        = fv; break;
                            case "ampfrequency":   c.AmpFrequency   = fv; break;
                            case "flare":          c.Flare          = fv; break;
                            case "yoffset":        c.YOffset        = fv; break;
                        }
                        break;
                    case ModeDDef d:
                        switch (k) {
                            case "freqt":      d.FreqT      = fv; break;
                            case "freqn":      d.FreqN      = fv; break;
                            case "blurradius": d.BlurRadius = fv; break;
                            case "alignment":  d.Alignment  = fv; break;
                            case "vscale":     d.VScale     = fv; break;
                            case "gradfade":   d.GradFade   = fv; break;
                            case "seafade":    d.SeaFade    = fv; break;
                        }
                        break;
                }
            }
            return md;
        }

        // Parse a [Profile:name] section into a TerrainProfile.
        // include = otherProfile prepends the named profile's modes and inherits its params.
        // profileParamOverrides receives modeName -> paramName -> value for "modename.paramname = value" entries.
        static TerrainProfile ParseTerrainProfile(string name, List<(string key, string val)> kvs,
            Dictionary<string, TerrainProfile> namedProfiles,
            Dictionary<string, Dictionary<string, float>> profileParamOverrides)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var tp = new TerrainProfile { Name = name };
            foreach (var (k, v) in kvs)
            {
                // Detect modename.paramname overrides (key contains a dot)
                int dotIdx = k.IndexOf('.');
                if (dotIdx > 0)
                {
                    string modePart  = k.Substring(0, dotIdx);
                    string paramPart = k.Substring(dotIdx + 1);
                    if (float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out float pv))
                    {
                        if (!profileParamOverrides.TryGetValue(modePart, out var modeOvr))
                        { modeOvr = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase); profileParamOverrides[modePart] = modeOvr; }
                        modeOvr[paramPart] = pv;
                    }
                    continue;
                }
                switch (k)
                {
                    case "include":
                        if (namedProfiles.TryGetValue(v.Trim(), out var incl))
                        {
                            tp.ModeNames.InsertRange(0, incl.ModeNames);
                            tp.FloorDepth    = incl.FloorDepth;
                            tp.FloorFade     = incl.FloorFade;
                            tp.BlurPasses    = incl.BlurPasses;
                            tp.PeronaK       = incl.PeronaK;
                            tp.PeronaLambda  = incl.PeronaLambda;
                            tp.ExtendUp      = incl.ExtendUp;
                        }
                        else Log.LogWarning($"[ProceduralTerrain] Profile '{name}': include '{v.Trim()}' not found.");
                        break;
                    case "modes":
                        foreach (var modeName in v.Split(','))
                        { var mn = modeName.Trim(); if (!string.IsNullOrEmpty(mn)) tp.ModeNames.Add(mn); }
                        break;
                    default:
                        if (!float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out float fv)) break;
                        switch (k) {
                            case "floordepth":    tp.FloorDepth   = fv; break;
                            case "floorfade":     tp.FloorFade    = fv; break;
                            case "blur_passes":   tp.BlurPasses   = fv; break;
                            case "perona_k":      tp.PeronaK      = fv; break;
                            case "perona_lambda": tp.PeronaLambda = fv; break;
                            case "extendup":      tp.ExtendUp     = fv; break;
                        }
                        break;
                }
            }
            return tp;
        }

        internal static void LoadBiomeConfig()
        {
            string[] lines;
            if (_serverCfgLines != null)
            {
                lines = _serverCfgLines;
            }
            else
            {
                string path = ConfigPath;
                if (!File.Exists(path))
                {
                    WriteDefaultBiomeConfig(path);
                    _biomeProfiles = BuildDefaultProfiles();
                    return;
                }
                lines = File.ReadAllLines(path);
            }

            // Collect all sections into raw key-value lists.
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
                var hashIdx = rawVal.IndexOf('#');
                if (hashIdx >= 0) rawVal = rawVal.Substring(0, hashIdx);
                cur.Add((line.Substring(0, eq).Trim().ToLowerInvariant(), rawVal.Trim()));
            }

            // Pass 1: Mode definitions [Mode:name]
            var modeDefs = new Dictionary<string, ModeDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var (secName, kvs) in sections)
            {
                if (!secName.StartsWith("Mode:", StringComparison.OrdinalIgnoreCase)) continue;
                string modeName = secName.Substring(5).Trim();
                var md = ParseModeDefinition(modeName, kvs);
                if (md != null) modeDefs[modeName] = md;
            }

            // Pass 2: Profile definitions [Profile:name]
            // Profiles may only include profiles defined above them.
            var namedProfiles = new Dictionary<string, TerrainProfile>(StringComparer.OrdinalIgnoreCase);
            // profileParamOverrides[profileName][modeName][paramName] = value
            var allProfileParamOverrides = new Dictionary<string, Dictionary<string, Dictionary<string, float>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (secName, kvs) in sections)
            {
                if (!secName.StartsWith("Profile:", StringComparison.OrdinalIgnoreCase)) continue;
                string profName = secName.Substring(8).Trim();
                var profOvr = new Dictionary<string, Dictionary<string, float>>(StringComparer.OrdinalIgnoreCase);
                namedProfiles[profName] = ParseTerrainProfile(profName, kvs, namedProfiles, profOvr);
                if (profOvr.Count > 0) allProfileParamOverrides[profName] = profOvr;
            }

            // Pass 3: Debug, Bake, and biome assignments
            var profiles = BuildDefaultProfiles();
            var biomeProfileMap = new Dictionary<Heightmap.Biome, TerrainProfile>();
            foreach (var (secName, kvs) in sections)
            {
                if (secName.Equals("Debug", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (k, v) in kvs)
                    {
                        if (k == "disablevegetation")
                            _disableVegetation = v == "1" || v == "true";
                        if (k == "protectlocations")
                            _protectLocations = !(v == "0" || v == "false");
                        if (k == "flatplanelocations")
                            _flatPlaneLocations = !(v == "0" || v == "false");
                        if (k == "islandremoval")
                            _islandRemoval = !(v == "0" || v == "false");
                        if (k == "pocketremoval")
                            _pocketRemoval = !(v == "0" || v == "false");
                        if (k == "probeluaskip")
                            _probeLuaSkip = v == "1" || v == "true";
                        if (k == "probeluanoisezero")
                            _probeLuaNoiseZero = v == "1" || v == "true";
                        if (k == "biomeblendradius" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float bbr))
                            _biomeBlendRadius = Mathf.Max(1f, bbr);
                        if (k == "biomeblendsmooth")
                            _biomeBlendSmooth = !(v == "0" || v == "false");
                        if (k == "transitionblurpasses" && int.TryParse(v, out int tbp))
                            _transitionBlurPasses = Mathf.Max(0, tbp);
                        if (k == "fixpointmaxzones" && int.TryParse(v, out int fmz))
                            _fixpointMaxZones = Mathf.Max(1, fmz);
                        if (k == "coastfade" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float cf))
                            _coastFade = Mathf.Max(0f, cf);
                        if (k == "sealevelfade" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float slf))
                            _seaLevelFade = Mathf.Max(0f, slf);
                        if (k == "sealevelatten" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sla))
                            _seaLevelAtten = Mathf.Clamp01(sla);
                    }
                    continue;
                }
                if (secName.Equals("Bake", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (k, v) in kvs)
                    {
                        if (k == "bakeradius" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float br))
                            _bakeRadius = Mathf.Max(0f, br);
                        if (k == "bakestep" && int.TryParse(v, out int bs))
                            _bakeStep = Mathf.Clamp(bs, 1, 64);
                        if (k == "compressbake")
                            _compressBake = !(v == "0" || v == "false");
                        if (k == "noise_count" && int.TryParse(v, out int nc))
                        {
                            nc = Mathf.Clamp(nc, 10, 256);
                            if (nc != _noiseCount) RebuildNoisePool(nc);
                        }
                    }
                    continue;
                }
                if (secName.StartsWith("Mode:", StringComparison.OrdinalIgnoreCase)) continue;
                if (secName.StartsWith("Profile:", StringComparison.OrdinalIgnoreCase)) continue;
                if (secName.Equals("Pipeline", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Enum.TryParse<Heightmap.Biome>(secName, out var biome)) continue;

                TerrainProfile tp = TerrainProfile.Default;
                foreach (var (k, v) in kvs)
                    if (k == "profile" && namedProfiles.TryGetValue(v.Trim(), out var np)) { tp = np; break; }
                profiles[biome] = tp.ToCaveProfile(modeDefs);
                biomeProfileMap[biome] = tp;
            }

            // Build pipeline from [Pipeline] section or all named modes
            var pipelineNames = new List<string>();
            foreach (var (secName, kvs) in sections)
            {
                if (!secName.Equals("Pipeline", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var (k, v) in kvs)
                    if (k == "modes")
                        foreach (var mn in v.Split(',')) { var t = mn.Trim(); if (t.Length > 0) pipelineNames.Add(t); }
            }
            if (pipelineNames.Count == 0)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var tp in namedProfiles.Values)
                    foreach (var mn in tp.ModeNames)
                        if (seen.Add(mn)) pipelineNames.Add(mn);
            }
            var pipelineList = new List<ModeDefinition>();
            foreach (var mn in pipelineNames)
                if (modeDefs.TryGetValue(mn, out var pmd)) { pmd.PipelineIndex = pipelineList.Count; pipelineList.Add(pmd); }
            _pipeline = pipelineList.ToArray();
            Plugin.ZoneGradCache.Clear();

            // Build _biomeParams [biome][pipelineIdx][paramIdx]
            var newBiomeParams = new Dictionary<Heightmap.Biome, float[][]>();
            foreach (var (biome2, tp) in biomeProfileMap)
            {
                var modeParams = new float[_pipeline.Length][];
                // For each pipeline mode, look up overrides for this profile
                allProfileParamOverrides.TryGetValue(tp.Name, out var profOvr);
                foreach (var mn in tp.ModeNames)
                {
                    if (!modeDefs.TryGetValue(mn, out var pmd) || pmd.PipelineIndex < 0) continue;
                    int mi = pmd.PipelineIndex;
                    int nP = pmd.ParamCount;
                    var pArr = new float[nP];
                    for (int pi = 0; pi < nP; pi++) pArr[pi] = pmd.GetDefaultParam(pi);
                    // Apply profile-level overrides for this mode
                    if (profOvr != null && profOvr.TryGetValue(mn, out var modeOvr))
                    {
                        string[] pNames = pmd.ParamNames;
                        for (int pi = 0; pi < nP; pi++)
                            if (modeOvr.TryGetValue(pNames[pi], out float ov)) pArr[pi] = ov;
                    }
                    modeParams[mi] = pArr;
                }
                // Leave absent pipeline modes as null — null = truly absent (vs explicit amplitude=0).
                // The blend accumulator skips non-amplitude params for null entries so absent
                // biomes only dilute amplitude, not frequency/vscale/etc.
                newBiomeParams[biome2] = modeParams;
            }
            _biomeParams = newBiomeParams;

            _modeDefinitions = modeDefs;
            _namedProfiles   = namedProfiles;
            _biomeProfiles   = profiles;
            Log.LogInfo($"[ProceduralTerrain] Config reloaded: {modeDefs.Count} modes, {namedProfiles.Count} profiles, {profiles.Count} biomes, pipeline={_pipeline.Length}.");
        }

        static void WriteDefaultBiomeConfig(string path)
        {
            var sb = new System.Text.StringBuilder();

            sb.AppendLine("# ProceduralTerrain — edit and save to hot-reload in-game");
            sb.AppendLine("#");
            sb.AppendLine("# Three-tier config: Modes → Profiles → Biomes");
            sb.AppendLine("#   [Mode:name]     — named terrain primitive (type A/B/C/D + params)");
            sb.AppendLine("#   [Profile:name]  — ordered list of mode names + profile-level params");
            sb.AppendLine("#   [BiomeName]     — assigns a profile to a biome");
            sb.AppendLine("#");
            sb.AppendLine("# ── Mode parameter reference ──────────────────────────────────────────");
            sb.AppendLine("# [Mode:my_mode]");
            sb.AppendLine("# type        = D          # A / B / C / D");
            sb.AppendLine("# amplitude   = 0          # on/off + strength; distance-blended across biomes");
            sb.AppendLine("# # Type A (tunnel network): frequency warpAmp warpAmpY warpFrequency vScale widthVary widthFrequency yOffset");
            sb.AppendLine("# # Type B (surface band):   warpAmp warpFrequency vScale noiseAmp yOffset twist");
            sb.AppendLine("# # Type C (density field):  threshold frequency warpAmp warpFrequency vScale twist twistVary twistFrequency ampVary ampFrequency flare yOffset");
            sb.AppendLine("# # Type D (coast ridges):   freqT freqN blurRadius alignment vScale gradFade seaFade");
            sb.AppendLine("#");
            sb.AppendLine("# ── Profile reference ────────────────────────────────────────────────");
            sb.AppendLine("# [Profile:my_profile]");
            sb.AppendLine("# include     = other_profile   # prepend another profile's modes (must be defined above)");
            sb.AppendLine("# modes       = mode_a, mode_b  # ordered list of mode names");
            sb.AppendLine("# floorDepth  = 8               # wu below surface where carving stops");
            sb.AppendLine("# floorFade   = 0               # wu above floorY over which carving tapers");
            sb.AppendLine("# blur_passes = 0               # XZ smoothing passes (0=off)");
            sb.AppendLine("# ─────────────────────────────────────────────────────────────────────");
            sb.AppendLine();
            sb.AppendLine("[Debug]");
            sb.AppendLine("disableVegetation = false");
            sb.AppendLine("protectLocations  = true");
            sb.AppendLine("islandRemoval     = true");
            sb.AppendLine("pocketRemoval     = true");
            sb.AppendLine("biomeBlendRadius  = 192");
            sb.AppendLine("coastFade         = 0");
            sb.AppendLine();

            sb.AppendLine("[Profile:vanilla_heightmap]");
            sb.AppendLine("# No modes — vanilla heightmap only");
            sb.AppendLine();

            var biomes = new[]
            {
                Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp,
                Heightmap.Biome.Mountain, Heightmap.Biome.Plains, Heightmap.Biome.Mistlands,
                Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth, Heightmap.Biome.Ocean,
            };
            foreach (var biome in biomes)
            {
                sb.AppendLine($"[{biome}]");
                sb.AppendLine("profile = vanilla_heightmap");
                sb.AppendLine();
            }

            File.WriteAllText(path, sb.ToString());
            Log.LogInfo($"[ProceduralTerrain] Wrote default biome config to {path}");
        }

        static void WatchBiomeConfig()
        {
            _configLastWrite = File.GetLastWriteTimeUtc(ConfigPath);
            _luaLastWrite    = LatestLuaWrite();
        }

        static DateTime LatestLuaWrite()
        {
            var dir = Path.Combine(Path.GetDirectoryName(ConfigPath)!, "modes");
            if (!Directory.Exists(dir)) return DateTime.MinValue;
            var t = DateTime.MinValue;
            foreach (var f in Directory.GetFiles(dir, "*.lua"))
            {
                var ft = File.GetLastWriteTimeUtc(f);
                if (ft > t) t = ft;
            }
            return t;
        }

        // ── Noise initialisation ──────────────────────────────────────────────────
        // All objects at frequency=1.0; call sites scale coordinates explicitly so
        // blended per-biome frequencies interpolate correctly across biome boundaries.
        static void InitNoise()
        {
            if (_noisePool != null) return;
            long uid = ZNet.instance?.GetWorldUID() ?? 0L;
            int seed = uid != 0L ? (int)(uid * 2654435761L) : 12345;
            Log.LogInfo($"[ProceduralTerrain] noise seed={seed} (uid={uid})");

            FastNoiseLite MakeNoise(int offset)
            {
                var n = new FastNoiseLite(seed + offset);
                n.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
                n.SetFrequency(1f);
                return n;
            }

            // Warp noises (named fields, kept for backward compat in tight loops)
            _warpX       = MakeNoise(2);  // Mode A warp X (field 1)
            _warpZ       = MakeNoise(3);  // Mode A warp Z (field 1)
            _warpX2      = MakeNoise(13); // Mode A warp X (field 2)
            _warpZ2      = MakeNoise(14); // Mode A warp Z (field 2)
            _warpY       = MakeNoise(15); // Mode A warp Y/sy (field 1)
            _warpY2      = MakeNoise(16); // Mode A warp Y/sy (field 2)
            _hmWarpX     = MakeNoise(4);  // Mode B warp X
            _hmWarpZ     = MakeNoise(5);  // Mode B warp Z

            // Pool noises — use deterministic prime-spaced seeds independent of warp offsets
            RebuildNoisePool(_noiseCount);
        }

        // ── Per-column analytic floor ─────────────────────────────────────────────
        static sbyte ToSDFByte(float sdf, float s)
        {
            // Match Density.FromSdf exactly: clamp to ±1 voxel then round to nearest sbyte.
            // No snap — snapping collapsed sub-voxel fractions to ±12 on flat tops,
            // making adjacent columns round to the same interpolated height = terracing.
            return (sbyte)(int)Math.Round(Mathf.Clamp(sdf / s, -1f, 1f) * 127f);
        }

        // C1-continuous max — rounds the crease where two SDF fields meet, over a blend width of k.
        // Identical to max(a,b) when |a-b| > k; adds at most k/4 at the join centre.
        internal static float SmoothMax(float a, float b, float k)
        {
            float h = Mathf.Max(k - Mathf.Abs(a - b), 0f) / k;
            return Mathf.Max(a, b) + h * h * k * 0.25f;
        }

        // ── Shared biome-blend BFS primitives ────────────────────────────────────────

        // Multi-source BFS from all cells with non-zero fraction of biome slot.
        // Fills _biomeDist with hop-counts; call after _biomeFrac is populated.
        static void RunBiomeBFS(int slot, int pcw, int pgSize, int maxHops)
        {
            int qHead = 0, qTail = 0;
            for (int i = 0; i < pgSize; i++)
            {
                if (_biomeFrac![i * MaxBiomeSlots + slot] > 0f)
                    { _biomeDist![i] = 0; _biomeBfsQ![qTail++] = i; }
                else _biomeDist![i] = int.MaxValue;
            }
            while (qHead < qTail)
            {
                int cell = _biomeBfsQ![qHead++];
                int nd   = _biomeDist[cell] + 1;
                if (nd > maxHops) continue;
                int px = cell / pcw, pz = cell % pcw;
                if (px > 0)     { int n=cell-pcw; if (_biomeDist[n]>nd){_biomeDist[n]=nd;_biomeBfsQ[qTail++]=n;} }
                if (px < pcw-1) { int n=cell+pcw; if (_biomeDist[n]>nd){_biomeDist[n]=nd;_biomeBfsQ[qTail++]=n;} }
                if (pz > 0)     { int n=cell-1;   if (_biomeDist[n]>nd){_biomeDist[n]=nd;_biomeBfsQ[qTail++]=n;} }
                if (pz < pcw-1) { int n=cell+1;   if (_biomeDist[n]>nd){_biomeDist[n]=nd;_biomeBfsQ[qTail++]=n;} }
            }
        }

        // Accumulate one weighted profile contribution into acc, adding w to totW.
        static void AccumulateProfile(ref CaveProfile acc, ref float totW, in CaveProfile prof, float w)
        {
            totW += w;
            acc.blur_passes   += w * prof.blur_passes;
            acc.perona_k      += w * prof.perona_k;
            acc.perona_lambda += w * prof.perona_lambda;
            acc.floorDepth    += w * prof.floorDepth;
            acc.floorFade     += w * prof.floorFade;
            acc.extendUp      += w * prof.extendUp;
        }

        static void NormalizeProfile(ref CaveProfile p, float totW)
        {
            if (totW <= 0f) return;
            float rcp = 1f / totW;
            p.blur_passes   *= rcp;
            p.perona_k      *= rcp;
            p.perona_lambda *= rcp;
            p.floorDepth    *= rcp;
            p.floorFade      = Mathf.Min(p.floorFade * rcp, p.floorDepth);
            p.extendUp      *= rcp;
        }

        // ── voxcaves_probe: biome blend breakdown logged to console ──────────────────
        static Dictionary<int, int> SetupPointBlend(float worldX, float worldZ, WorldGenerator wg, float s,
            out float coarseStep, out int pcw, out int pgSize, out int maxHops, out int center)
        {
            coarseStep = 4f * s;
            int pad  = (int)(_biomeBlendRadius / coarseStep) + 2;
            pcw      = 1 + 2 * pad;
            pgSize   = pcw * pcw;
            maxHops  = (int)(_biomeBlendRadius / coarseStep) + 1;
            center   = pad * pcw + pad;

            int fracStride = pgSize * MaxBiomeSlots;
            if (_biomeFrac == null || _biomeFrac.Length < fracStride) _biomeFrac = new float[fracStride];
            Array.Clear(_biomeFrac, 0, fracStride);
            if (_biomeDist == null || _biomeDist.Length < pgSize) _biomeDist = new int[pgSize];
            if (_biomeBfsQ == null || _biomeBfsQ.Length < pgSize) _biomeBfsQ = new int[pgSize];

            var biomeSlot = new Dictionary<int, int>(MaxBiomeSlots);
            float subWeight = 1f / (SuperN * SuperN);
            for (int px = 0; px < pcw; px++)
            for (int pz = 0; pz < pcw; pz++)
            {
                float cx = worldX + (px - pad) * coarseStep;
                float cz = worldZ + (pz - pad) * coarseStep;
                int cellBase = (px * pcw + pz) * MaxBiomeSlots;
                for (int si = 0; si < SuperN; si++)
                for (int sk = 0; sk < SuperN; sk++)
                {
                    int b = (int)wg.GetBiome(cx + (si + 0.5f) * coarseStep / SuperN,
                                             cz + (sk + 0.5f) * coarseStep / SuperN);
                    if (!biomeSlot.TryGetValue(b, out int slot))
                    {
                        if (biomeSlot.Count >= MaxBiomeSlots) continue;
                        slot = biomeSlot.Count;
                        biomeSlot[b] = slot;
                    }
                    _biomeFrac[cellBase + slot] += subWeight;
                }
            }
            return biomeSlot;
        }

        static void ProbeLog(string msg)
        {
            Log.LogInfo(msg);
            Console.instance?.Print(msg);
        }

        static void ProbeBlend(float worldX, float worldZ, float worldY, WorldGenerator wg, float s)
        {
            var profs     = _biomeProfiles;
            var biomeSlot = SetupPointBlend(worldX, worldZ, wg, s,
                out float coarseStep, out int pcw, out int pgSize, out int maxHops, out int center);

            // ── Biome blend ──────────────────────────────────────────────────────────
            var sb = new System.Text.StringBuilder();
            sb.Append($"  center cell fractions: ");
            foreach (var (biomeInt, slot) in biomeSlot)
            {
                float f = _biomeFrac[center * MaxBiomeSlots + slot];
                if (f > 0f) sb.Append($"{(Heightmap.Biome)biomeInt}={f:F2} ");
            }
            ProbeLog(sb.ToString());

            float totW = 0f;
            var contributions = new List<(string name, int distCells, float frac, float w, CaveProfile prof)>();

            foreach (var (biomeInt, slot) in biomeSlot)
            {
                RunBiomeBFS(slot, pcw, pgSize, maxHops);
                int distC = _biomeDist![center];
                if (distC > maxHops) continue;
                float t = distC * coarseStep / _biomeBlendRadius;
                float w = _biomeBlendSmooth ? 1f - t * t * (3f - 2f * t) : 1f - t;
                if (w <= 0f) continue;
                float frac = distC == 0 ? _biomeFrac![center * MaxBiomeSlots + slot] : 1f;
                w *= frac;
                if (w <= 0f) continue;
                totW += w;
                var prof = profs.TryGetValue((Heightmap.Biome)biomeInt, out var p) ? p : CaveProfile.Default;
                contributions.Add(($"{(Heightmap.Biome)biomeInt}", distC, frac, w, prof));
            }

            // ── CoastFade injection (mirrors BakeBlendedProfiles step 2b) ────────────
            float coastWProbe = 0f;
            if (_coastFade > 0f)
            {
                const float SeaLevel = 30f;
                float h = wg.GetHeight(worldX, worldZ);
                coastWProbe = 1f - Mathf.Clamp01((h - SeaLevel) / _coastFade);
                if (coastWProbe > 0f)
                {
                    var oceanProf = profs.TryGetValue(Heightmap.Biome.Ocean, out var op) ? op : CaveProfile.Default;
                    totW += coastWProbe;
                    contributions.Add(($"Ocean(coast)", 0, coastWProbe, coastWProbe, oceanProf));
                }
            }

            if (totW <= 0f) { ProbeLog("  no biome contribution (totW=0)"); return; }

            ProbeLog($"  blendRadius={_biomeBlendRadius} coastFade={_coastFade:F0} coarseStep={coarseStep:F1} maxHops={maxHops}  totW={totW:F3}");
            foreach (var (name, distCells, frac, w, prof) in contributions)
            {
                float normW = w / totW;
                float worldDist = distCells * coarseStep;
                ProbeLog($"  [{name}]  dist={worldDist:F0}wu ({distCells} hops)  seedFrac={frac:F2}  weight={w:F3}  normW={normW:P0}  blur={prof.blur_passes:F0}  floorDepth={prof.floorDepth:F1}");
            }

            // ── Blended profile + full per-mode params ───────────────────────────────
            float rcp = 1f / totW;
            float blendBlur  = contributions.Sum(c => c.w * c.prof.blur_passes) * rcp;
            float blendFloor = contributions.Sum(c => c.w * c.prof.floorDepth)  * rcp;
            ProbeLog($"  → blended: blur={blendBlur:F1}  floorDepth={blendFloor:F1}  pipeline={_pipeline.Length} modes");

            var bpMap    = _biomeParams;
            var pipeline = _pipeline;
            for (int mi = 0; mi < pipeline.Length; mi++)
            {
                var mode = pipeline[mi];
                float[][] bmpActive = Array.Empty<float[]>();
                foreach (var (name, _, _, w, _) in contributions)
                {
                    if (!System.Enum.TryParse<Heightmap.Biome>(name, out var biome)) continue;
                    if (bpMap.TryGetValue(biome, out var a) && mi < a.Length && a[mi] != null)
                        bmpActive = a; // use first contributing biome that has params (blend below)
                }
                // build blended param array for this mode
                int pc = mode.ParamCount;
                if (pc == 0) continue;
                float[] acc = new float[pc];
                float presentW = 0f;
                foreach (var (name, _, _, w, _) in contributions)
                {
                    if (!System.Enum.TryParse<Heightmap.Biome>(name, out var biome)) biome = Heightmap.Biome.Ocean;
                    float[][] bmp = bpMap.TryGetValue(biome, out var a) ? a : Array.Empty<float[]>();
                    float[]? mArr = (mi < bmp.Length) ? bmp[mi] : null;
                    bool present = mArr != null;
                    if (present) presentW += w;
                    for (int pi = 0; pi < pc; pi++)
                    {
                        if (pi > 0 && !present) continue;
                        acc[pi] += w * (mArr != null && pi < mArr.Length ? mArr[pi] : 0f);
                    }
                }
                float[] blendedP = new float[pc];
                ModeDefinition.BlendParams(blendedP, acc, totW, presentW, pc);
                if (blendedP[0] <= 0f) continue;

                var psb = new System.Text.StringBuilder();
                psb.Append($"    [{mi}] {mode.Name}  ");
                string[] pnames = mode.ParamNames;
                for (int pi = 0; pi < pc; pi++)
                    psb.Append($"{(pi < pnames.Length ? pnames[pi] : pi.ToString())}={blendedP[pi]:G4}  ");
                ProbeLog(psb.ToString());
            }

            // ── Input features at probe point ────────────────────────────────────────
            ProbeLog($"  input features at ({worldX:F1}, {worldY:F1}, {worldZ:F1}):");

            float th = wg.GetHeight(worldX, worldZ);
            ProbeLog($"    terrain_h={th:F2}");

            // Zone extent for the zone containing this world point
            {
                const int zoneW = 64;
                int gridOffset = ((-(int)(32f / s)) % zoneW + zoneW) % zoneW;
                int ox = (int)Math.Floor((worldX / s - gridOffset) / zoneW) * zoneW + gridOffset;
                int oz = (int)Math.Floor((worldZ / s - gridOffset) / zoneW) * zoneW + gridOffset;
                var (yLo, yHi) = Plugin.ComputeYBoundsPublic(ox, oz, zoneW, s, wg);
                int loMarg = Mathf.Max(24, (int)Mathf.Ceil(48f / s) + 4); // approx loMargin
                float extLo = (yLo - loMarg) * s;
                float extHi = (yHi + 24) * s;
                ProbeLog($"    zone ox={ox} oz={oz}  yLo={yLo} yHi={yHi}  extLo≈{extLo:F0}wu extHi≈{extHi:F0}wu");
            }

            // Gradient via finite difference (same 4wu step as coarse grid)
            float hStep = 4f;
            float dhx = wg.GetHeight(worldX + hStep, worldZ) - wg.GetHeight(worldX - hStep, worldZ);
            float dhz = wg.GetHeight(worldX, worldZ + hStep) - wg.GetHeight(worldX, worldZ - hStep);
            float gradMag = Mathf.Sqrt(dhx * dhx + dhz * dhz) / (2f * hStep);
            float gradLen = Mathf.Max(gradMag * 2f * hStep, 1e-6f);
            float gdx = dhx / gradLen, gdz = dhz / gradLen; // uphill direction
            float tdx = -gdz, tdz = gdx;                    // tangent (90° CCW)
            ProbeLog($"    grad_dirx={gdx:F3}  grad_dirz={gdz:F3}  grad_mag={gradMag:F4}");
            ProbeLog($"    tangent_x={tdx:F3}  tangent_z={tdz:F3}");

            if (Plugin._noisePool != null)
            {
                var np = Plugin._noisePool;
                int nc = Mathf.Min(Plugin._noiseCount, np.Length);
                var nsb = new System.Text.StringBuilder();
                nsb.Append("    noise: ");
                for (int ni = 0; ni < nc; ni++)
                    nsb.Append($"n{ni + 1}={np[ni].GetNoise(worldX, worldY, worldZ):F3}  ");
                ProbeLog(nsb.ToString());
            }
        }

        // ── Point-query biome blend — same supersampled fractional approach as BakeBlendedProfiles ──
        // Used by callers that need a blend for a single world point (e.g. hot-reload ceiling estimate).
        static CaveProfile PointBlendProfile(float worldX, float worldZ, WorldGenerator wg, float s)
        {
            var profs     = _biomeProfiles;
            var biomeSlot = SetupPointBlend(worldX, worldZ, wg, s,
                out float coarseStep, out int pcw, out int pgSize, out int maxHops, out int center);

            var   result = new CaveProfile();
            float totW   = 0f;

            foreach (var (biomeInt, slot) in biomeSlot)
            {
                RunBiomeBFS(slot, pcw, pgSize, maxHops);
                int distC = _biomeDist![center];
                if (distC > maxHops) continue;
                float tC = distC * coarseStep / _biomeBlendRadius;
                float w  = _biomeBlendSmooth ? 1f - tC * tC * (3f - 2f * tC) : 1f - tC;
                if (w <= 0f) continue;
                if (distC == 0) w *= _biomeFrac![center * MaxBiomeSlots + slot];
                if (w <= 0f) continue;
                var prof = profs.TryGetValue((Heightmap.Biome)biomeInt, out var p) ? p : CaveProfile.Default;
                AccumulateProfile(ref result, ref totW, in prof, w);
            }
            NormalizeProfile(ref result, totW);
            return result;
        }

        // ── Distance-based biome weight pipeline ─────────────────────────────────────
        // For each coarse grid cell, computes the world-space distance to each distinct
        // biome via BFS on the padded sample grid, then derives blend weights from that
        // distance.  Weight = max(0, 1 - dist/blendRadius), normalized so all biome
        // weights sum to 1.  This gives exact 50/50 at any biome boundary, pure 1.0
        // far inside a biome, and a predictable linear falloff of width blendRadius.
        static void BakeBlendedProfiles(int cw4, int ox, int oz, int overlap, float s, WorldGenerator wg)
        {
            var profs = _biomeProfiles;
            CaveProfile GetP(Heightmap.Biome b) =>
                profs.TryGetValue(b, out var p) ? p : CaveProfile.Default;

            float coarseStep = 4f * s;
            int pad    = (int)(_biomeBlendRadius / coarseStep) + 2;
            int pcw    = cw4 + 2 * pad;
            int pgSize = pcw * pcw;
            int maxHops = (int)(_biomeBlendRadius / coarseStep) + 1;

            // ── Step 1: supersample GetBiome — SuperN×SuperN calls per coarse cell ──
            // Each cell gets fractional membership per biome rather than a single hard label.
            // This eliminates the aliasing artifact where one zone shows more BF influence
            // than its neighbor simply because its single sample fell inside a thin biome finger.
            int fracStride = pgSize * MaxBiomeSlots;
            if (_biomeFrac == null || _biomeFrac.Length < fracStride) _biomeFrac = new float[fracStride];
            Array.Clear(_biomeFrac, 0, fracStride);

            var biomeSlot = new Dictionary<int, int>(MaxBiomeSlots);
            float subWeight = 1f / (SuperN * SuperN);
            for (int px = 0; px < pcw; px++)
            for (int pz = 0; pz < pcw; pz++)
            {
                int gx0 = ox - overlap + (px - pad) * 4;
                int gz0 = oz - overlap + (pz - pad) * 4;
                int cellBase = (px * pcw + pz) * MaxBiomeSlots;
                for (int si = 0; si < SuperN; si++)
                for (int sk = 0; sk < SuperN; sk++)
                {
                    float wx = (gx0 + (si + 0.5f) * 4f / SuperN) * s;
                    float wz = (gz0 + (sk + 0.5f) * 4f / SuperN) * s;
                    int b = (int)wg.GetBiome(wx, wz);
                    if (!biomeSlot.TryGetValue(b, out int slot))
                    {
                        if (biomeSlot.Count >= MaxBiomeSlots) continue;
                        slot = biomeSlot.Count;
                        biomeSlot[b] = slot;
                    }
                    _biomeFrac[cellBase + slot] += subWeight;
                }
            }

            // ── Step 2: per-biome BFS distance transform + accumulate ────────────────
            int cellCount = cw4 * cw4;
            if (_blendedProfs == null || _blendedProfs.Length < cellCount)
                _blendedProfs = new CaveProfile[cellCount];
            if (_biomeTotW == null || _biomeTotW.Length < cellCount)
                _biomeTotW = new float[cellCount];
            for (int i = 0; i < cellCount; i++) { _blendedProfs[i] = new CaveProfile(); _biomeTotW[i] = 0f; }

            if (_biomeDist == null || _biomeDist.Length < pgSize) _biomeDist = new int[pgSize];
            if (_biomeBfsQ == null || _biomeBfsQ.Length < pgSize) _biomeBfsQ = new int[pgSize];

            var pipeline = _pipeline;
            int pLen     = pipeline.Length;
            var biomeParams = _biomeParams;

            // Compute maxParamCount for flat accumulator layout
            int maxParamCount = 0;
            for (int mi = 0; mi < pLen; mi++)
                if (pipeline[mi].ParamCount > maxParamCount) maxParamCount = pipeline[mi].ParamCount;

            // Ensure bkModeParams sized [pipelineIdx][paramIdx][cellIdx]
            if (_cb.bkModeParams == null || _cb.bkModeParams.Length < pLen)
                _cb.bkModeParams = new float[pLen][][];
            for (int mi = 0; mi < pLen; mi++)
            {
                int nP = pipeline[mi].ParamCount;
                if (_cb.bkModeParams[mi] == null || _cb.bkModeParams[mi].Length < nP)
                    _cb.bkModeParams[mi] = new float[nP][];
                for (int pi = 0; pi < nP; pi++)
                    if (_cb.bkModeParams[mi][pi] == null || _cb.bkModeParams[mi][pi].Length < cellCount)
                        _cb.bkModeParams[mi][pi] = new float[cellCount];
            }

            // Local flat accumulator: [mi * maxParamCount + pi] * cellCount + idx
            var modeParamAcc      = new float[pLen * maxParamCount * cellCount];
            // Per-mode present weight: sum of biome weights where mode is non-null.
            // Used to normalize non-amplitude params so absent biomes don't pull them toward zero.
            var modePresentWAcc   = new float[pLen * cellCount];

            foreach (var (biomeInt, slot) in biomeSlot)
            {
                var prof = GetP((Heightmap.Biome)biomeInt);

                // Multi-source BFS: seed from every cell that has any fraction of this biome.
                RunBiomeBFS(slot, pcw, pgSize, maxHops);

                float[][]? bParams = biomeParams.TryGetValue((Heightmap.Biome)biomeInt, out var bp) ? bp : null;

                // Accumulate weighted profile into each inner coarse cell.
                // At distance 0, scale by fractional membership so a cell that is 50% BF
                // contributes half as much as a fully-BF cell at the same distance.
                for (int cx4 = 0; cx4 < cw4; cx4++)
                for (int cz4 = 0; cz4 < cw4; cz4++)
                {
                    int pIdx = (cx4 + pad) * pcw + (cz4 + pad);
                    int distCells = _biomeDist![pIdx];
                    if (distCells > maxHops) continue;
                    float t = distCells * coarseStep / _biomeBlendRadius;
                    float w = _biomeBlendSmooth ? 1f - t * t * (3f - 2f * t) : 1f - t;
                    if (w <= 0f) continue;
                    // Scale by fractional membership at the seed cell (distance 0 only).
                    // For propagated cells (dist > 0) the full falloff weight applies as-is.
                    if (distCells == 0)
                        w *= _biomeFrac![pIdx * MaxBiomeSlots + slot];
                    if (w <= 0f) continue;
                    int idx = cx4 * cw4 + cz4;
                    ref var acc = ref _blendedProfs![idx];
                    AccumulateProfile(ref acc, ref _biomeTotW![idx], in prof, w);
                    // Accumulate mode params
                    for (int mi = 0; mi < pLen; mi++)
                    {
                        float[]? mBiomeP = (bParams != null && mi < bParams.Length) ? bParams[mi] : null;
                        bool present = mBiomeP != null;
                        if (present) modePresentWAcc[mi * cellCount + idx] += w;
                        int nP = pipeline[mi].ParamCount;
                        int accBase = (mi * maxParamCount) * cellCount + idx;
                        for (int pi = 0; pi < nP; pi++)
                        {
                            if (pi > 0 && !present) continue; // absent biomes only dilute amplitude
                            float pv = (mBiomeP != null && pi < mBiomeP.Length) ? mBiomeP[pi] : 0f;
                            modeParamAcc[accBase + pi * cellCount] += w * pv;
                        }
                    }
                }
            }

            // ── Step 2b: Height-based Ocean inflation ─────────────────────────────────
            // Injects extra Ocean-profile weight wherever the heightmap is within coastFade
            // world units of sea level (30f).  This suppresses carving in shallow coastal
            // areas regardless of what biome label the map assigns there.
            if (_coastFade > 0f)
            {
                const float SeaLevel = 30f;
                var oceanProf = GetP(Heightmap.Biome.Ocean);
                float[][]? oceanBP = biomeParams.TryGetValue(Heightmap.Biome.Ocean, out var obp) ? obp : null;
                for (int cx4 = 0; cx4 < cw4; cx4++)
                for (int cz4 = 0; cz4 < cw4; cz4++)
                {
                    float wx = (ox - overlap + cx4 * 4 + 2) * s;
                    float wz = (oz - overlap + cz4 * 4 + 2) * s;
                    float h  = wg.GetHeight(wx, wz);
                    float coastW = 1f - Mathf.Clamp01((h - SeaLevel) / _coastFade);
                    if (coastW <= 0f) continue;
                    int idx = cx4 * cw4 + cz4;
                    ref var acc = ref _blendedProfs![idx];
                    AccumulateProfile(ref acc, ref _biomeTotW![idx], in oceanProf, coastW);
                    for (int mi = 0; mi < pLen; mi++)
                    {
                        float[]? mOceanP = (oceanBP != null && mi < oceanBP.Length) ? oceanBP[mi] : null;
                        bool present = mOceanP != null;
                        if (present) modePresentWAcc[mi * cellCount + idx] += coastW;
                        int nP = pipeline[mi].ParamCount;
                        int accBase = (mi * maxParamCount) * cellCount + idx;
                        for (int pi = 0; pi < nP; pi++)
                        {
                            if (pi > 0 && !present) continue;
                            float pv = (mOceanP != null && pi < mOceanP.Length) ? mOceanP[pi] : 0f;
                            modeParamAcc[accBase + pi * cellCount] += coastW * pv;
                        }
                    }
                }
            }

            // ── Step 3: Normalize ─────────────────────────────────────────────────────
            for (int idx = 0; idx < cellCount; idx++)
            {
                ref var p = ref _blendedProfs![idx];
                float tw = _biomeTotW![idx];
                NormalizeProfile(ref p, tw);
                float rcp = tw > 0f ? 1f / tw : 0f;
                for (int mi = 0; mi < pLen; mi++)
                {
                    int nP = pipeline[mi].ParamCount;
                    int accBase = (mi * maxParamCount) * cellCount + idx;
                    float presentW = modePresentWAcc[mi * cellCount + idx];
                    // Gather per-param accumulated sums into a temp span, then normalize.
                    Span<float> acc = stackalloc float[nP];
                    for (int pi = 0; pi < nP; pi++) acc[pi] = modeParamAcc[accBase + pi * cellCount];
                    Span<float> result = stackalloc float[nP];
                    ModeDefinition.BlendParams(result, acc, tw, presentW, nP);
                    for (int pi = 0; pi < nP; pi++) _cb.bkModeParams![mi][pi][idx] = result[pi];
                }
            }

        }
    }

} // namespace ProceduralTerrain
