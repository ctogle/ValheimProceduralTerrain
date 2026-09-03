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
        static string ConfigPath =>
            Path.Combine(BepInEx.Paths.ConfigPath, "procedural_terrain_biomes.cfg");

        // Parse a [Mode:name] section into a ModeDefinition.
        static ModeDefinition? ParseModeDefinition(string name, List<(string key, string val)> kvs)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            ModeType? type = null;
            foreach (var (k, v) in kvs)
            {
                if (k != "type") continue;
                type = v.Trim().ToUpperInvariant() switch { "A" => ModeType.A, "B" => ModeType.B, "C" => ModeType.C, "D" => ModeType.D, _ => (ModeType?)null };
                break;
            }
            if (type == null) { Log.LogWarning($"[ProceduralTerrain] Mode '{name}': missing or invalid type."); return null; }

            var md = new ModeDefinition { Name = name, Type = type.Value };
            foreach (var (k, v) in kvs)
            {
                if (k == "type") continue;
                if (!float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out float fv)) continue;
                if (k == "amplitude") { md.Amplitude = fv; continue; }
                switch (type.Value)
                {
                    case ModeType.A:
                        switch (k) {
                            case "frequency":      md.A_frequency      = fv; break;
                            case "warpamp":        md.A_warpAmp        = fv; break;
                            case "warpampy":       md.A_warpAmpY       = fv; break;
                            case "warpfrequency":  md.A_warpFrequency  = fv; break;
                            case "vscale":         md.A_vScale         = fv; break;
                            case "widthvary":      md.A_widthVary      = fv; break;
                            case "widthfrequency": md.A_widthFrequency = fv; break;
                            case "yoffset":        md.A_yOffset        = fv; break;
                        }
                        break;
                    case ModeType.B:
                        switch (k) {
                            case "warpamp":       md.B_warpAmp       = fv; break;
                            case "warpfrequency": md.B_warpFrequency = fv; break;
                            case "vscale":        md.B_vScale        = fv; break;
                            case "noiseamp":      md.B_noiseAmp      = fv; break;
                            case "yoffset":       md.B_yOffset       = fv; break;
                            case "twist":         md.B_twist         = fv; break;
                        }
                        break;
                    case ModeType.C:
                        switch (k) {
                            case "threshold":      md.C_threshold      = fv; break;
                            case "frequency":      md.C_frequency      = fv; break;
                            case "warpamp":        md.C_warpAmp        = fv; break;
                            case "warpfrequency":  md.C_warpFrequency  = fv; break;
                            case "vscale":         md.C_vScale         = fv; break;
                            case "twist":          md.C_twist          = fv; break;
                            case "twistvary":      md.C_twistVary      = fv; break;
                            case "twistfrequency": md.C_twistFrequency = fv; break;
                            case "ampvary":        md.C_ampVary        = fv; break;
                            case "ampfrequency":   md.C_ampFrequency   = fv; break;
                            case "flare":          md.C_flare          = fv; break;
                            case "yoffset":        md.C_yOffset        = fv; break;
                        }
                        break;
                    case ModeType.D:
                        switch (k) {
                            case "freqt":      md.D_freqT      = fv; break;
                            case "freqn":      md.D_freqN      = fv; break;
                            case "blurradius": md.D_blurRadius = fv; break;
                            case "alignment":  md.D_alignment  = fv; break;
                            case "vscale":     md.D_vScale     = fv; break;
                            case "gradfade":   md.D_gradFade   = fv; break;
                            case "seafade":    md.D_seaFade    = fv; break;
                        }
                        break;
                }
            }
            return md;
        }

        // Parse a [Profile:name] section into a TerrainProfile.
        // include = otherProfile prepends the named profile's modes and inherits its params.
        static TerrainProfile ParseTerrainProfile(string name, List<(string key, string val)> kvs,
            Dictionary<string, TerrainProfile> namedProfiles)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var tp = new TerrainProfile { Name = name };
            foreach (var (k, v) in kvs)
            {
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
            foreach (var (secName, kvs) in sections)
            {
                if (!secName.StartsWith("Profile:", StringComparison.OrdinalIgnoreCase)) continue;
                string profName = secName.Substring(8).Trim();
                namedProfiles[profName] = ParseTerrainProfile(profName, kvs, namedProfiles);
            }

            // Pass 3: Debug, Bake, and biome assignments
            var profiles = BuildDefaultProfiles();
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
                        if (k == "islandremoval")
                            _islandRemoval = !(v == "0" || v == "false");
                        if (k == "pocketremoval")
                            _pocketRemoval = !(v == "0" || v == "false");
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
                    }
                    continue;
                }
                if (secName.StartsWith("Mode:", StringComparison.OrdinalIgnoreCase)) continue;
                if (secName.StartsWith("Profile:", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Enum.TryParse<Heightmap.Biome>(secName, out var biome)) continue;

                TerrainProfile tp = TerrainProfile.Default;
                foreach (var (k, v) in kvs)
                    if (k == "profile" && namedProfiles.TryGetValue(v.Trim(), out var np)) { tp = np; break; }
                profiles[biome] = tp.ToCaveProfile(modeDefs);
            }

            _modeDefinitions = modeDefs;
            _namedProfiles   = namedProfiles;
            _biomeProfiles   = profiles;
            Log.LogInfo($"[ProceduralTerrain] Config reloaded: {modeDefs.Count} modes, {namedProfiles.Count} profiles, {profiles.Count} biomes.");
            foreach (var kv in profiles)
            {
                var p = kv.Value;
                if (p.modeA_width > 0f || p.modeB_radius > 0f || p.modeC_noiseAmp > 0f || p.modeD_noiseAmp > 0f)
                    Log.LogInfo($"[ProceduralTerrain]   {kv.Key}: mB_r={p.modeB_radius:F1} mA_w={p.modeA_width:F2} mC_amp={p.modeC_noiseAmp:F1} mD_amp={p.modeD_noiseAmp:F1}");
            }
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
        }

        // ── Noise initialisation ──────────────────────────────────────────────────
        // All objects at frequency=1.0; call sites scale coordinates explicitly so
        // blended per-biome frequencies interpolate correctly across biome boundaries.
        static void InitNoise()
        {
            if (_caveNoise != null) return;
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

            _caveNoise   = MakeNoise(0);  // Mode A tunnel field 1
            _caveNoise2  = MakeNoise(1);  // Mode A tunnel field 2
            _warpX       = MakeNoise(2);  // Mode A warp X (field 1)
            _warpZ       = MakeNoise(3);  // Mode A warp Z (field 1)
            _warpX2      = MakeNoise(13); // Mode A warp X (field 2)
            _warpZ2      = MakeNoise(14); // Mode A warp Z (field 2)
            _warpY       = MakeNoise(15); // Mode A warp Y/sy (field 1)
            _warpY2      = MakeNoise(16); // Mode A warp Y/sy (field 2)
            _hmWarpX     = MakeNoise(4); // Mode B warp X
            _hmWarpZ     = MakeNoise(5); // Mode B warp Z
            _hmAddWarpX  = MakeNoise(6); // Mode C anchor warp X
            _hmAddWarpZ  = MakeNoise(7); // Mode C anchor warp Z
            _widthNoise  = MakeNoise(8);  // Mode A width modulation
            _addNoise       = MakeNoise(9);  // Mode C 3D density field
            _addVaryNoise   = MakeNoise(10); // Mode C amplitude modulation
            _twistVaryNoise = MakeNoise(11); // Mode C twist scale modulation
            _mbNoise        = MakeNoise(12); // Mode B 3D wall perturbation
            _mdNoise        = MakeNoise(17); // Mode D anisotropic noise field
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
        static float SmoothMax(float a, float b, float k)
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
            acc.modeB_radius         += w * prof.modeB_radius;
            acc.modeB_warpAmp        += w * prof.modeB_warpAmp;
            acc.modeB_warpFrequency  += w * prof.modeB_warpFrequency;
            acc.modeB_vScale         += w * prof.modeB_vScale;
            acc.modeB_noiseAmp       += w * prof.modeB_noiseAmp;
            acc.modeB_yOffset        += w * prof.modeB_yOffset;
            acc.modeB_twist          += w * prof.modeB_twist;
            acc.modeA_width          += w * prof.modeA_width;
            acc.modeA_frequency      += w * prof.modeA_frequency;
            acc.modeA_warpAmp        += w * prof.modeA_warpAmp;
            acc.modeA_warpAmpY       += w * prof.modeA_warpAmpY;
            acc.modeA_warpFrequency  += w * prof.modeA_warpFrequency;
            acc.modeA_vScale         += w * prof.modeA_vScale;
            acc.modeA_widthVary      += w * prof.modeA_widthVary;
            acc.modeA_widthFrequency += w * prof.modeA_widthFrequency;
            acc.modeA_yOffset        += w * prof.modeA_yOffset;
            acc.modeC_threshold      += w * prof.modeC_threshold;
            acc.modeC_noiseAmp       += w * prof.modeC_noiseAmp;
            acc.modeC_frequency      += w * prof.modeC_frequency;
            acc.modeC_warpAmp        += w * prof.modeC_warpAmp;
            acc.modeC_warpFrequency  += w * prof.modeC_warpFrequency;
            acc.modeC_vScale         += w * prof.modeC_vScale;
            acc.modeC_twist          += w * prof.modeC_twist;
            acc.modeC_twistVary      += w * prof.modeC_twistVary;
            acc.modeC_twistFrequency += w * prof.modeC_twistFrequency;
            acc.modeC_ampVary        += w * prof.modeC_ampVary;
            acc.modeC_ampFrequency   += w * prof.modeC_ampFrequency;
            acc.modeC_flare          += w * prof.modeC_flare;
            acc.modeC_yOffset        += w * prof.modeC_yOffset;
            acc.modeD_noiseAmp       += w * prof.modeD_noiseAmp;
            float wdAmp = w * prof.modeD_noiseAmp;
            acc.modeD_freqT          += wdAmp * prof.modeD_freqT;
            acc.modeD_freqN          += wdAmp * prof.modeD_freqN;
            acc.modeD_blurRadius     += wdAmp * prof.modeD_blurRadius;
            acc.modeD_alignment      += wdAmp * prof.modeD_alignment;
            acc.modeD_vScale         += wdAmp * prof.modeD_vScale;
            acc.modeD_gradFade       += wdAmp * prof.modeD_gradFade;
            acc.modeD_seaFade        += wdAmp * prof.modeD_seaFade;
            acc.blur_passes          += w * prof.blur_passes;
            acc.perona_k             += w * prof.perona_k;
            acc.perona_lambda        += w * prof.perona_lambda;
            acc.floorDepth           += w * prof.floorDepth;
            acc.floorFade            += w * prof.floorFade;
        }

        static void NormalizeProfile(ref CaveProfile p, float totW)
        {
            if (totW <= 0f) return;
            float rcp = 1f / totW;
            p.modeB_radius         *= rcp; p.modeB_warpAmp       *= rcp;
            p.modeB_warpFrequency  *= rcp; p.modeB_vScale        *= rcp;
            p.modeB_noiseAmp       *= rcp; p.modeB_yOffset       *= rcp; p.modeB_twist *= rcp;
            p.modeA_width          *= rcp; p.modeA_frequency     *= rcp;
            p.modeA_warpAmp        *= rcp; p.modeA_warpAmpY      *= rcp; p.modeA_warpFrequency *= rcp;
            p.modeA_vScale         *= rcp; p.modeA_widthVary     *= rcp; p.modeA_widthFrequency *= rcp; p.modeA_yOffset *= rcp;
            p.modeC_threshold      *= rcp; p.modeC_noiseAmp      *= rcp;
            p.modeC_frequency      *= rcp; p.modeC_warpAmp       *= rcp;
            p.modeC_warpFrequency  *= rcp; p.modeC_vScale        *= rcp;
            p.modeC_twist          *= rcp; p.modeC_twistVary     *= rcp; p.modeC_twistFrequency *= rcp;
            p.modeC_ampVary        *= rcp; p.modeC_ampFrequency  *= rcp; p.modeC_flare *= rcp; p.modeC_yOffset *= rcp;
            float mdAmpSum = p.modeD_noiseAmp;
            p.modeD_noiseAmp *= rcp;
            if (mdAmpSum > 0f) { p.modeD_freqT /= mdAmpSum; p.modeD_freqN /= mdAmpSum; p.modeD_blurRadius /= mdAmpSum; p.modeD_alignment /= mdAmpSum; p.modeD_vScale /= mdAmpSum; p.modeD_gradFade /= mdAmpSum; p.modeD_seaFade /= mdAmpSum; }
            else { p.modeD_freqT = CaveProfile.Default.modeD_freqT; p.modeD_freqN = CaveProfile.Default.modeD_freqN; p.modeD_blurRadius = CaveProfile.Default.modeD_blurRadius; p.modeD_alignment = CaveProfile.Default.modeD_alignment; p.modeD_vScale = CaveProfile.Default.modeD_vScale; p.modeD_gradFade = CaveProfile.Default.modeD_gradFade; p.modeD_seaFade = CaveProfile.Default.modeD_seaFade; }
            p.blur_passes          *= rcp; p.perona_k            *= rcp;
            p.perona_lambda        *= rcp; p.floorDepth          *= rcp; p.floorFade *= rcp;
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

        static void ProbeBlend(float worldX, float worldZ, WorldGenerator wg, float s)
        {
            var profs     = _biomeProfiles;
            var biomeSlot = SetupPointBlend(worldX, worldZ, wg, s,
                out float coarseStep, out int pcw, out int pgSize, out int maxHops, out int center);

            // Center cell fractions (what fraction of sub-samples at this exact cell are each biome)
            var sb = new System.Text.StringBuilder();
            sb.Append($"  center cell fractions: ");
            foreach (var (biomeInt, slot) in biomeSlot)
            {
                float f = _biomeFrac[center * MaxBiomeSlots + slot];
                if (f > 0f) sb.Append($"{(Heightmap.Biome)biomeInt}={f:F2} ");
            }
            Log.LogInfo(sb.ToString());

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

            if (totW <= 0f) { Log.LogInfo("  no biome contribution (totW=0)"); return; }

            Log.LogInfo($"  blendRadius={_biomeBlendRadius} coarseStep={coarseStep:F1} maxHops={maxHops}  totW={totW:F3}");
            foreach (var (name, distCells, frac, w, prof) in contributions)
            {
                float normW = w / totW;
                float worldDist = distCells * coarseStep;
                Log.LogInfo($"  [{name}]  dist={worldDist:F0}wu ({distCells} hops)  seedFrac={frac:F2}  weight={w:F3}  normW={normW:P0}  mcAmp={prof.modeC_noiseAmp:F1}  mbR={prof.modeB_radius:F1}  mdAmp={prof.modeD_noiseAmp:F1}  blur={prof.blur_passes:F0}");
                if (prof.modeD_noiseAmp > 0f)
                    Log.LogInfo($"    D: freqT={prof.modeD_freqT} freqN={prof.modeD_freqN} blurR={prof.modeD_blurRadius} align={prof.modeD_alignment} vScale={prof.modeD_vScale} gradFade={prof.modeD_gradFade} seaFade={prof.modeD_seaFade}");
            }

            // Blended result
            float rcp = 1f / totW;
            float blendMcAmp = contributions.Sum(c => c.w * c.prof.modeC_noiseAmp) * rcp;
            float blendMbR   = contributions.Sum(c => c.w * c.prof.modeB_radius)   * rcp;
            float blendMdAmp = contributions.Sum(c => c.w * c.prof.modeD_noiseAmp) * rcp;
            float blendBlur  = contributions.Sum(c => c.w * c.prof.blur_passes)    * rcp;
            float blendFloor = contributions.Sum(c => c.w * c.prof.floorDepth)     * rcp;
            Log.LogInfo($"  → blended: mcAmp={blendMcAmp:F2}  mbRadius={blendMbR:F2}  mdAmp={blendMdAmp:F2}  blur={blendBlur:F1}  floorDepth={blendFloor:F1}");
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

            foreach (var (biomeInt, slot) in biomeSlot)
            {
                var prof = GetP((Heightmap.Biome)biomeInt);

                // Multi-source BFS: seed from every cell that has any fraction of this biome.
                RunBiomeBFS(slot, pcw, pgSize, maxHops);

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
                }
            }

            // ── Step 3: Normalize ─────────────────────────────────────────────────────
            for (int idx = 0; idx < cellCount; idx++)
            {
                ref var p = ref _blendedProfs![idx];
                NormalizeProfile(ref p, _biomeTotW![idx]);
            }
        }
    }

} // namespace ProceduralTerrain
