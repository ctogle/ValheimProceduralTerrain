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

        // Apply a flat key→value list on top of a base CaveProfile (struct copy).
        // Parses "profA * w + profB * w + ..." into a normalized blended profile.
        // Profiles are resolved from `named`; unknown names are silently skipped.
        // Coefficient may appear on either side of *. Order of terms in cfg matters:
        // a profile can only reference profiles defined above it.
        static CaveProfile ParseBlendExpression(string expr, Dictionary<string, CaveProfile> named)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var result = new CaveProfile();
            float totW = 0f;
            foreach (var term in expr.Split('+'))
            {
                var t = term.Trim();
                float weight = 1f;
                string profName = t;
                int star = t.LastIndexOf('*');
                if (star >= 0)
                {
                    var left  = t.Substring(0, star).Trim();
                    var right = t.Substring(star + 1).Trim();
                    if (float.TryParse(right, System.Globalization.NumberStyles.Float, inv, out float wr))
                    { weight = wr; profName = left; }
                    else if (float.TryParse(left, System.Globalization.NumberStyles.Float, inv, out float wl))
                    { weight = wl; profName = right; }
                    else continue;
                }
                if (!named.TryGetValue(profName, out var prof))
                {
                    Log.LogWarning($"[ProceduralTerrain] base blend: profile '{profName}' not found.");
                    continue;
                }
                AccumulateProfile(ref result, ref totW, in prof, weight);
            }
            // Structural params must not be summed from components — reset to defaults
            // so they are strictly controlled by the real profile's own key overrides.
            var d = CaveProfile.Default;
            result.floorDepth   = d.floorDepth;
            result.floorFade    = d.floorFade;
            result.blur_passes  = d.blur_passes;
            result.perona_k     = d.perona_k;
            result.perona_lambda = d.perona_lambda;
            return result;
        }

        static CaveProfile ApplyProfile(
            IEnumerable<(string key, string val)> kvs, CaveProfile p)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var (key, val) in kvs)
            {
                if (!float.TryParse(val, System.Globalization.NumberStyles.Float, inv, out float v))
                    continue;
                switch (key)
                {
                    case "modeb_strength":        break; // removed — use modeB_radius=0 to disable
                    case "modeb_radius":          p.modeB_radius          = v; break;
                    case "modeb_warpamp":         p.modeB_warpAmp         = v; break;
                    case "modeb_warpfrequency":   p.modeB_warpFrequency   = v; break;
                    case "modeb_vscale":          p.modeB_vScale          = v; break;
                    case "modeb_noiseamp":        p.modeB_noiseAmp        = v; break;
                    case "modeb_yoffset":         p.modeB_yOffset         = v; break;
                    case "modeb_twist":           p.modeB_twist           = v; break;
                    case "modea_strength":        break; // removed — use modeA_width=0 to disable
                    case "modea_width":           p.modeA_width           = v; break;
                    case "modea_frequency":       p.modeA_frequency       = v; break;
                    case "modea_warpamp":         p.modeA_warpAmp         = v; break;
                    case "modea_warpampy":        p.modeA_warpAmpY        = v; break;
                    case "modea_warpfrequency":   p.modeA_warpFrequency   = v; break;
                    case "modea_vscale":          p.modeA_vScale          = v; break;
                    case "modea_widthvary":       p.modeA_widthVary       = v; break;
                    case "modea_widthfrequency":  p.modeA_widthFrequency  = v; break;
                    case "modea_yoffset":         p.modeA_yOffset         = v; break;
                    case "modec_strength":        break; // removed — use modeC_noiseAmp=0 to disable
                    case "modec_threshold":       p.modeC_threshold       = v; break;
                    case "modec_noiseamp":        p.modeC_noiseAmp        = v; break;
                    case "modec_frequency":       p.modeC_frequency       = v; break;
                    case "modec_warpamp":         p.modeC_warpAmp         = v; break;
                    case "modec_warpfrequency":   p.modeC_warpFrequency   = v; break;
                    case "modec_vscale":          p.modeC_vScale          = v; break;
                    case "modec_twist":           p.modeC_twist           = v; break;
                    case "modec_twistvary":       p.modeC_twistVary       = v; break;
                    case "modec_twistfrequency":  p.modeC_twistFrequency  = v; break;
                    case "modec_ampvary":         p.modeC_ampVary         = v; break;
                    case "modec_ampfrequency":    p.modeC_ampFrequency    = v; break;
                    case "modec_flare":           p.modeC_flare           = v; break;
                    case "modec_yoffset":         p.modeC_yOffset         = v; break;
                    case "moded_noiseamp":        p.modeD_noiseAmp        = v; break;
                    case "moded_frequency":       p.modeD_frequency       = v; break;
                    case "blur_passes":            p.blur_passes            = v; break;
                    case "perona_k":              p.perona_k              = v; break;
                    case "perona_lambda":         p.perona_lambda         = v; break;
                    case "floordepth":            p.floorDepth            = v; break;
                    case "floorfade":             p.floorFade             = v; break;
                }
            }
            return p;
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

            // Collect all sections into raw key-value lists for two-pass resolution.
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

            // Pass 1: named profiles  [Profile:name]
            // A profile may include: base = profA * w + profB * w + ...
            // which sets a blended starting point before per-key overrides.
            // Profiles may only reference profiles defined earlier in the file.
            var named = new Dictionary<string, CaveProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, kvs) in sections)
            {
                if (!name.StartsWith("Profile:", StringComparison.OrdinalIgnoreCase)) continue;
                string profName = name.Substring(8).Trim();
                CaveProfile baseProf = CaveProfile.Default;
                foreach (var (k, v) in kvs)
                    if (k == "base") { baseProf = ParseBlendExpression(v, named); break; }
                named[profName] = ApplyProfile(kvs, baseProf);
            }

            // Pass 2: debug + biome assignments
            var profiles = BuildDefaultProfiles();
            foreach (var (name, kvs) in sections)
            {
                if (name.Equals("Debug", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (k, v) in kvs)
                    {
                        if (k == "disablevegetation")
                        {
                            _disableVegetation = v == "1" || v == "true";
                        }
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
                if (name.Equals("Bake", StringComparison.OrdinalIgnoreCase))
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
                if (!Enum.TryParse<Heightmap.Biome>(name, out var biome)) continue;

                // Resolve base: named profile if specified, else existing default.
                CaveProfile baseProf = profiles.TryGetValue(biome, out var def) ? def : CaveProfile.Default;
                foreach (var (k, v) in kvs)
                    if (k == "profile" && named.TryGetValue(v, out var np)) { baseProf = np; break; }

                profiles[biome] = ApplyProfile(kvs, baseProf);
            }

            _namedProfiles = named;
            _biomeProfiles = profiles;
            Log.LogInfo($"[ProceduralTerrain] Profiles reloaded: {named.Count} named, {profiles.Count} biomes.");
            foreach (var kv in profiles)
            {
                var p = kv.Value;
                if (p.modeA_width > 0f || p.modeB_radius > 0f || p.modeC_noiseAmp > 0f)
                    Log.LogInfo($"[ProceduralTerrain]   {kv.Key}: mB_r={p.modeB_radius:F1} mA_w={p.modeA_width:F2} mC_amp={p.modeC_noiseAmp:F1}");
            }
        }

        static void WriteDefaultBiomeConfig(string path)
        {
            var sb = new System.Text.StringBuilder();

            sb.AppendLine("# ProceduralTerrain — edit and save to hot-reload in-game");
            sb.AppendLine("#");
            sb.AppendLine("# Define profiles with [Profile:name], assign to biomes with:");
            sb.AppendLine("#   profile = name");
            sb.AppendLine("# Params listed directly in a [BiomeName] section override the profile.");
            sb.AppendLine("# Profiles may blend others as a base layer (must be defined above):");
            sb.AppendLine("#   base = profileA * 0.5 + profileB * 2.0");
            sb.AppendLine("# Per-param overrides are applied on top of the blended base.");
            sb.AppendLine("#");
            sb.AppendLine("# ── Full parameter reference (copy to start a new profile) ─────────────");
            sb.AppendLine("# [Profile:my_profile]");
            sb.AppendLine("# blur_passes           = 0      # XZ Gaussian passes for seam smoothing (0=off, 1-3=light/heavy; Y never blurred)");
            sb.AppendLine("# floorDepth            = 8      # how far below heightmap modes A+B stop carving");
            sb.AppendLine("# floorFade             = 0      # world units above floorY over which carving tapers to zero (0=hard stop)");
            sb.AppendLine("# # Mode B — carves a band of air around the heightmap surface");
            sb.AppendLine("# modeB_strength        = 0      # 0=off 1=full");
            sb.AppendLine("# modeB_radius          = 6      # half-thickness of carved band (world units)");
            sb.AppendLine("# modeB_warpAmp         = 80     # XZ warp displacement (world units)");
            sb.AppendLine("# modeB_warpFrequency   = 0.005  # spatial frequency of XZ warp");
            sb.AppendLine("# modeB_vScale          = 0.5    # Y scale in noise space (low=vertical walls, high=horizontal banding)");
            sb.AppendLine("# modeB_noiseAmp        = 0      # 3D SDF perturbation on band walls (world units; 0=off)");
            sb.AppendLine("# modeB_yOffset         = 0      # push band centre N units below surface (0=trench; >mbR=enclosed cave)");
            sb.AppendLine("# # Mode A — 3D noise tunnel network (punches through Mode C solid)");
            sb.AppendLine("# modeA_strength        = 0      # 0=off 1=full");
            sb.AppendLine("# modeA_width           = 0.5    # tunnel size (noise threshold 0-1)");
            sb.AppendLine("# modeA_frequency       = 0.02   # spatial frequency of tunnel noise");
            sb.AppendLine("# modeA_warpAmp         = 5      # 3D domain-warp displacement XZ (world units)");
            sb.AppendLine("# modeA_warpAmpY        = 0      # 3D domain-warp displacement Y/vertical (world units; 0=no Y bend)");
            sb.AppendLine("# modeA_warpFrequency   = 0.02   # spatial frequency of domain warp");
            sb.AppendLine("# modeA_vScale          = 0.1    # Y scale in noise space (low=vertical shafts, high=horizontal caverns)");
            sb.AppendLine("# modeA_widthVary       = 1      # tunnel-width spatial variation (0=uniform)");
            sb.AppendLine("# modeA_widthFrequency  = 0.02   # spatial frequency of width variation");
            sb.AppendLine("# # Mode C — 3D density field raising solid terrain above the heightmap");
            sb.AppendLine("# modeC_strength        = 0      # 0=off 1=full");
            sb.AppendLine("# modeC_threshold       = 0      # iso-surface offset (-1..1; 0=half solid)");
            sb.AppendLine("# modeC_noiseAmp        = 10     # height of raised terrain (world units)");
            sb.AppendLine("# modeC_frequency       = 0.015  # spatial frequency of 3D density noise");
            sb.AppendLine("# modeC_warpAmp         = 8      # XZ anchor-point warp displacement");
            sb.AppendLine("# modeC_warpFrequency   = 0.005  # spatial frequency of anchor warp");
            sb.AppendLine("# modeC_vScale          = 0.4    # Y stretch (higher = more overhangs/cliffs)");
            sb.AppendLine("# modeC_twist           = 0      # XZ rotation per world-unit Y (radians/m)");
            sb.AppendLine("# modeC_twistVary       = 0      # per-XZ twist-scale variation (0=uniform)");
            sb.AppendLine("# modeC_twistFrequency  = 0.003  # spatial frequency of twist variation");
            sb.AppendLine("# modeC_ampVary         = 0      # height variation amplitude (0=uniform)");
            sb.AppendLine("# modeC_ampFrequency    = 0.003  # spatial frequency of height variation");
            sb.AppendLine("# ─────────────────────────────────────────────────────────────────────────");
            sb.AppendLine();
            sb.AppendLine("[Debug]");
            sb.AppendLine("disableVegetation = false");
            sb.AppendLine("protectLocations  = true   # false = carve through location areas (use with SpawnLocation Y-snap patch)");
            sb.AppendLine("islandRemoval     = true   # false = disable floating-island BFS removal");
            sb.AppendLine("pocketRemoval     = true   # false = disable sealed underground pocket BFS fill");
            sb.AppendLine("biomeBlendRadius  = 192    # world units over which biome profiles blend at boundaries");
            sb.AppendLine("coastFade         = 0      # world units above sea level (30) over which Ocean inflates into blend; 0=off");
            sb.AppendLine();

            sb.AppendLine("[Profile:vanilla_heightmap]");
            sb.AppendLine("blur_passes    = 0");
            sb.AppendLine("modeB_strength = 0");
            sb.AppendLine("modeA_strength = 0");
            sb.AppendLine("modeC_strength = 0");
            sb.AppendLine();

            // ── Biome assignments — all default to vanilla_heightmap ──────────────
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
            acc.modeD_frequency      += w * prof.modeD_frequency;
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
            p.modeD_noiseAmp       *= rcp; p.modeD_frequency     *= rcp;
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
                Log.LogInfo($"  [{name}]  dist={worldDist:F0}wu ({distCells} hops)  seedFrac={frac:F2}  weight={w:F3}  normW={normW:P0}  mcAmp={prof.modeC_noiseAmp:F1}  mbR={prof.modeB_radius:F1}  blur={prof.blur_passes:F0}");
            }

            // Blended result
            float rcp = 1f / totW;
            float blendMcAmp = contributions.Sum(c => c.w * c.prof.modeC_noiseAmp) * rcp;
            float blendMbR   = contributions.Sum(c => c.w * c.prof.modeB_radius)   * rcp;
            float blendBlur  = contributions.Sum(c => c.w * c.prof.blur_passes)    * rcp;
            float blendFloor = contributions.Sum(c => c.w * c.prof.floorDepth)     * rcp;
            Log.LogInfo($"  → blended: mcAmp={blendMcAmp:F2}  mbRadius={blendMbR:F2}  blur={blendBlur:F1}  floorDepth={blendFloor:F1}");
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
