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
        // The PT-specific config folder (holds the biomes cfg, mode lua, and bake .bin files).
        internal static string PTConfigDir =>
            Path.Combine(BepInEx.Paths.ConfigPath, "ProceduralTerrain");
        internal static string ConfigPath =>
            Path.Combine(PTConfigDir, "biomes.cfg");

        // Read an embedded default config (configs/*.cfg, embedded at build). Empty string if absent.
        internal static string ReadEmbeddedConfig(string logicalName)
        {
            var asm = typeof(Plugin).Assembly;
            using var s = asm.GetManifestResourceStream(logicalName);
            if (s == null) { Log.LogWarning($"[ProceduralTerrain] embedded config '{logicalName}' missing."); return ""; }
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }

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
                    if (k == "surfaceanchored") { luaDef.SurfaceAnchoredFlag = v.Trim() == "1" || v.Trim().ToLowerInvariant() == "true"; continue; }
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

            // Native (C#) modes: same [Mode:*] contract as Lua, selected by type name. This is what lets
            // a mode be swapped between its Lua prototype and its C# port by editing `type=` and saving
            // (config hot-reload rebuilds the pipeline). Params are set generically by name via SetParam.
            if (_nativeModes.TryGetValue(typeStr, out var factory))
            {
                var nd = factory(); nd.Name = name;
                foreach (var (k, v) in kvs)
                {
                    if (k == "type") continue;
                    if (k == "blend")   { nd.Blend = v.Trim().ToLowerInvariant(); continue; }
                    if (k == "blend_k") { if (float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out float bk)) nd.BlendK = bk; continue; }
                    if (float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out float fv))
                        nd.SetParam(k, fv);
                }
                return nd;
            }

            Log.LogWarning($"[ProceduralTerrain] Mode '{name}': unknown type '{typeStr}' (expected 'lua' or a native mode name).");
            return null;
        }

        // Native C# mode registry: type name (as written in [Mode:*] type=, case-insensitive) -> factory.
        // Add a line here to expose a new C# mode to the config.
        static readonly Dictionary<string, Func<NativeModeDef>> _nativeModes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                { "isotropic_formation", () => new IsotropicFormation() },
                { "isotropic_tunnel",    () => new IsotropicTunnel()    },
                { "surface_blobs",       () => new SurfaceBlobs()       },
            };

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
                    case "profiles":   // nest/inherit from one or more parent profiles. Parents' modes are
                    {                  // prepended in listed order; their params applied (later parent wins),
                                       // then overridable by this profile's own keys below.
                        var parentModes = new List<string>();
                        foreach (var parentName in v.Split(','))
                        {
                            var pn = parentName.Trim();
                            if (pn.Length == 0) continue;
                            if (namedProfiles.TryGetValue(pn, out var incl))
                            {
                                parentModes.AddRange(incl.ModeNames);
                                tp.FloorDepth   = incl.FloorDepth;
                                tp.FloorFade    = incl.FloorFade;
                                tp.FloorLevel   = incl.FloorLevel;
                                tp.VegDensity   = incl.VegDensity;
                                tp.VegForestWiden = incl.VegForestWiden;
                                tp.VegInForest  = incl.VegInForest;
                                tp.VegForestMax = incl.VegForestMax;
                                tp.VegGroupSize = incl.VegGroupSize;
                                tp.VegGroupRadius = incl.VegGroupRadius;
                                tp.VegTiltInherit = incl.VegTiltInherit;
                                tp.VegRandTilt  = incl.VegRandTilt;
                                tp.VegMaxTilt   = incl.VegMaxTilt;
                                tp.VegConformMaxTilt = incl.VegConformMaxTilt;
                                tp.VegSupportRadius  = incl.VegSupportRadius;
                                tp.BlurPasses   = incl.BlurPasses;
                                tp.PeronaK      = incl.PeronaK;
                                tp.PeronaLambda = incl.PeronaLambda;
                                tp.ExtendUp     = incl.ExtendUp;
                            }
                            else Log.LogWarning($"[ProceduralTerrain] Profile '{name}': parent profile '{pn}' not found.");
                        }
                        tp.ModeNames.InsertRange(0, parentModes);
                        break;
                    }
                    case "modes":
                        foreach (var modeName in v.Split(','))
                        { var mn = modeName.Trim(); if (!string.IsNullOrEmpty(mn)) tp.ModeNames.Add(mn); }
                        break;
                    default:
                        if (!float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out float fv)) break;
                        switch (k) {
                            case "floordepth":    tp.FloorDepth   = fv; break;
                            case "floorfade":     tp.FloorFade    = fv; break;
                            case "floorlevel":    tp.FloorLevel   = fv; break;
                            case "vegdensity":    tp.VegDensity   = fv; break;
                            case "vegforestwiden": tp.VegForestWiden = fv; break;
                            case "veginforest":   tp.VegInForest   = fv > 0.5f; break;
                            case "vegforestmax":  tp.VegForestMax  = fv; break;
                            case "veggroupsize":  tp.VegGroupSize  = Mathf.RoundToInt(fv); break;
                            case "veggroupradius": tp.VegGroupRadius = fv; break;
                            case "vegtiltinherit": tp.VegTiltInherit = fv; break;
                            case "vegrandtilt": tp.VegRandTilt = fv; break;
                            case "vegmaxtilt": tp.VegMaxTilt = fv; break;
                            case "vegconformmaxtilt": tp.VegConformMaxTilt = fv; break;
                            case "vegsupportradius": tp.VegSupportRadius = fv; break;
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
            // Order-independent nesting: a profile's `profiles=` parents may appear anywhere in the file. We
            // resolve in dependency order — repeatedly parse any profile whose parents are all resolved, deferring
            // the rest, until no progress. Leftovers (missing parent or a cycle) are parsed anyway with a warning.
            var namedProfiles = new Dictionary<string, TerrainProfile>(StringComparer.OrdinalIgnoreCase);
            // profileParamOverrides[profileName][modeName][paramName] = value
            var allProfileParamOverrides = new Dictionary<string, Dictionary<string, Dictionary<string, float>>>(StringComparer.OrdinalIgnoreCase);
            var pendingProfiles = new List<(string profName, List<(string key, string val)> kvs)>();
            foreach (var (secName, kvs) in sections)
            {
                if (!secName.StartsWith("Profile:", StringComparison.OrdinalIgnoreCase)) continue;
                pendingProfiles.Add((secName.Substring(8).Trim(), kvs));
            }
            void ResolveProfile(string profName, List<(string key, string val)> kvs)
            {
                var profOvr = new Dictionary<string, Dictionary<string, float>>(StringComparer.OrdinalIgnoreCase);
                namedProfiles[profName] = ParseTerrainProfile(profName, kvs, namedProfiles, profOvr);
                if (profOvr.Count > 0) allProfileParamOverrides[profName] = profOvr;
            }
            bool ParentsReady(List<(string key, string val)> kvs)
            {
                foreach (var (k, v) in kvs)
                    if (k.Equals("profiles", StringComparison.OrdinalIgnoreCase))
                        foreach (var p in v.Split(','))
                        { var pn = p.Trim(); if (pn.Length > 0 && !namedProfiles.ContainsKey(pn)) return false; }
                return true;
            }
            bool progress = true;
            while (pendingProfiles.Count > 0 && progress)
            {
                progress = false;
                for (int i = pendingProfiles.Count - 1; i >= 0; i--)
                {
                    if (!ParentsReady(pendingProfiles[i].kvs)) continue;
                    ResolveProfile(pendingProfiles[i].profName, pendingProfiles[i].kvs);
                    pendingProfiles.RemoveAt(i);
                    progress = true;
                }
            }
            foreach (var (profName, kvs) in pendingProfiles)   // unresolved parent (missing or cyclic) — parse anyway
            {
                Log.LogWarning($"[ProceduralTerrain] Profile '{profName}': unresolved parent in profiles= (missing or cyclic); parsing without it.");
                ResolveProfile(profName, kvs);
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
                        // NOTE: vegetation + cave-content/placement keys live in the separate placement cfg
                        // (procedural_terrain_placements.cfg), not here. This cfg is terrain shaping only.
                        if (k == "seed")
                            _forcedSeed = string.IsNullOrWhiteSpace(v) ? null : v.Trim();
                        if (k == "prefetch")
                            ZonePrefetcher.Enabled = (v == "1" || v == "true");
                        if (k == "prefetchradius" && int.TryParse(v, out int prr))
                            ZonePrefetcher.RadiusZones = Mathf.Clamp(prr, 1, 16);
                        if (k == "prefetchthreads" && int.TryParse(v, out int prt))
                            ZonePrefetcher.ThreadCount = Mathf.Clamp(prt, 0, 32);
                        if (k == "protectlocations")
                            _protectLocations = !(v == "0" || v == "false");
                        if (k == "locdomeheight" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ldh))
                            _locDomeHeight = Mathf.Max(0f, ldh);
                        if (k == "locbandfade" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float lbf))
                            _locBandFade = Mathf.Max(0.001f, lbf);
                        if (k == "applygroundwork")
                            _applyGroundwork = !(v == "0" || v == "false");
                        if (k == "flatplanelocations")
                            _flatPlaneLocations = !(v == "0" || v == "false");
                        if (k == "islandremoval")
                            _islandRemoval = !(v == "0" || v == "false");
                        if (k == "pocketremoval")
                            _pocketRemoval = !(v == "0" || v == "false");
                        if (k == "hidelodmeshes")
                            _hideLodMeshes = (v == "1" || v == "true");
                        if (k == "lodunderground")
                            _lodUnderground = !(v == "0" || v == "false");
                        if (k == "debugviz")
                            DebugViz.Enabled = _debugViz = (v == "1" || v == "true");
                        if (k == "vizmode")
                            _vizMode = v.Trim();
                        if (k == "vegviz")
                        {
                            _vegViz = (v == "1" || v == "true");
                            if (_vegViz) DebugViz.Enabled = true;   // vegViz needs the renderer live even without debugViz
                        }
                        if (k == "vegvizradius" && float.TryParse(v, out float vvr))
                            DebugViz.Radius = vvr;
                        if (k == "debugvizocclude")
                            DebugViz.Occlude = (v == "1" || v == "true");
                        if (k == "overlap" && int.TryParse(v, out int ov))
                            _overlap = Mathf.Clamp(ov, 2, 16);
                        if (k == "fielddepthlo" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fdl))
                            _fieldDepthLo = Mathf.Max(0f, fdl);
                        // NOTE: bake interp (tricubic/trilinear) is a load-side choice, deliberately NOT a cfg key —
                        // it must not affect the bake's cfg-hash. Toggle it at runtime with the pt_interp command.
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
                        if (k == "coaststrength" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float cst))
                            _coastStrength = Mathf.Max(0f, cst);
                        if (k == "riverprotect" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rp))
                            _riverProtect = Mathf.Max(0f, rp);
                        if (k == "rivercore" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rc))
                            _riverCore = Mathf.Clamp(rc, 0.01f, 1f);
                        if (k == "riveredge" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float re))
                            _riverEdge = Mathf.Clamp(re, 0f, 0.99f);
                        if (k == "riverdomeheight" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rdh))
                            _riverDomeHeight = Mathf.Max(0f, rdh);
                        if (k == "riverbandfade" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rbf))
                            _riverBandFade = Mathf.Max(0.001f, rbf);
                        if (k == "blendmode")
                            _blendMode = (v == "sdf" || v == "1") ? 1 : 0;
                        if (k == "composemode")
                            _composeMode = (v == "sequential" || v == "1") ? 1 : 0;
                        if (k == "eikonal")
                            _eikonal = v == "1" || v == "true";
                        if (k == "coarsexz" && int.TryParse(v, out int cxz))
                            _coarseXZ = Mathf.Clamp(cxz, 1, 64);
                        if (k == "coarsey" && int.TryParse(v, out int cyv))
                            _coarseY = Mathf.Clamp(cyv, 1, 64);
                        if (k == "bakecoarsexz" && int.TryParse(v, out int bcxz))
                            _bakeCoarseXZ = Mathf.Clamp(bcxz, 1, 64);
                        if (k == "bakecoarsey" && int.TryParse(v, out int bcy))
                            _bakeCoarseY = Mathf.Clamp(bcy, 1, 64);
                        if (k == "surfacefullres")
                            _surfaceFullRes = !(v == "0" || v == "false");
                        if (k == "vanillaheight")
                            _vanillaHeight = !(v == "0" || v == "false");
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
                        if (k == "bakevanilla")
                            _bakeVanilla = !(v == "0" || v == "false");
                        if (k == "bakeformat")
                            _bakeCrossings = v.Trim().Equals("crossings", StringComparison.OrdinalIgnoreCase);
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
            Plugin.VanillaHeight.Clear();
            Plugin.ClearComposedSurfCache();   // formation params may have changed → cached surface nodes are stale

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

            // Biomes whose assigned profile has at least one mode with a nonzero amplitude gate ([0] param).
            // A zone touching none of these (center+corners, blend-radius expanded) is pure vanilla and can
            // skip BakeBlendedProfiles + all mode machinery → straight to VanillaFill.
            var modified = new HashSet<Heightmap.Biome>();
            foreach (var (b2, mp) in newBiomeParams)
                for (int mi = 0; mi < mp.Length; mi++)
                    if (mp[mi] != null && mp[mi].Length > 0 && mp[mi][0] > 0f) { modified.Add(b2); break; }
            Plugin._modifiedBiomes = modified;

            WarnNyquistFrequencies();

            // Persist per-biome veg density for the PlaceVegetation patch (non-terrain, live interpretation).
            var vegMap = new Dictionary<Heightmap.Biome, Plugin.VegOverride>();
            foreach (var (b3, tp3) in biomeProfileMap)
            {
                bool any = tp3.VegDensity != 1f || tp3.VegForestWiden != 0f || tp3.VegInForest
                         || tp3.VegForestMax >= 0f || tp3.VegGroupSize > 0 || tp3.VegGroupRadius >= 0f || tp3.VegTiltInherit >= 0f
                         || tp3.VegRandTilt >= 0f || tp3.VegMaxTilt >= 0f || tp3.VegConformMaxTilt >= 0f || tp3.VegSupportRadius >= 0f;
                if (any) vegMap[b3] = new Plugin.VegOverride {
                    density = tp3.VegDensity, widen = tp3.VegForestWiden, inForest = tp3.VegInForest,
                    forestMax = tp3.VegForestMax, groupSize = tp3.VegGroupSize, groupRadius = tp3.VegGroupRadius, tiltInherit = tp3.VegTiltInherit,
                    randTilt = tp3.VegRandTilt, maxTilt = tp3.VegMaxTilt, conformMaxTilt = tp3.VegConformMaxTilt, supportRadius = tp3.VegSupportRadius };
            }
            Plugin._vegByBiome = vegMap;

            _modeDefinitions = modeDefs;
            _namedProfiles   = namedProfiles;
            _biomeProfiles   = profiles;
            Log.LogInfo($"[ProceduralTerrain] Config reloaded: {modeDefs.Count} modes, {namedProfiles.Count} profiles, {profiles.Count} biomes, pipeline={_pipeline.Length}.");
        }

        // Advisory at config load: a coarse bake (XZ cell = CellVox*s) can only faithfully represent
        // feature wavelengths a few cells wide. Warn when a frequency param implies a wavelength below
        // that — it will facet/alias under coarse bilinear unless the mode evaluates full-res. Heuristic:
        // it can't know which freqs are XZ-spatial vs vertical/warp, so it's informational, not a hard error.
        static void WarnNyquistFrequencies()
        {
            if (_pipeline == null) return;
            float s       = _scale ?? 1f;
            float cell    = Plugin.CellVox * s;   // coarse XZ sample interval, world units
            float minWave = 3f * cell;            // ~3 samples/wavelength; below this bilinear visibly facets
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (var mode in _pipeline)
            {
                int mi = mode.PipelineIndex;
                var names = mode.ParamNames;
                for (int pi = 0; pi < mode.ParamCount; pi++)
                {
                    string nm = pi < names.Length ? names[pi].ToLowerInvariant() : "";
                    if (!nm.Contains("freq")) continue;
                    // Worst (highest) configured value across biomes, falling back to the mode default.
                    float maxv = mode.GetDefaultParam(pi);
                    foreach (var kv in _biomeParams)
                    {
                        var mp = (mi >= 0 && mi < kv.Value.Length) ? kv.Value[mi] : null;
                        if (mp != null && pi < mp.Length && mp[pi] > maxv) maxv = mp[pi];
                    }
                    if (maxv <= 0f) continue;
                    float wave = 1f / maxv;   // FastNoiseLite feature size ≈ 1/frequency (world units)
                    if (wave < minWave && seen.Add(mode.Name + "." + nm))
                        Log.LogWarning($"[ProceduralTerrain] Nyquist: {mode.Name}.{names[pi]}={maxv:G3} → ~{wave:F1}wu feature vs {cell:F1}wu coarse cell (want ≥{minWave:F1}wu). If XZ-spatial it will facet under coarse bake — lower the frequency, lower coarseXZ/coarseY, or make the mode full-res.");
                }
            }
        }

        // Write the shipped, tuned biome config (embedded at build from configs/biomes.cfg) to disk on first
        // launch. Only called when the file is absent, so player edits are never clobbered on update.
        static void WriteDefaultBiomeConfig(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, ReadEmbeddedConfig("ProceduralTerrain.biomes.cfg"));
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
            int seed = Plugin.WorldSeed();
            Log.LogInfo($"[ProceduralTerrain] noise seed={seed} (world seed string, portable)");

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
        internal static sbyte ToSDFByte(float sdf, float s)
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
            acc.floorLevel    += w * prof.floorLevel;
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
            p.floorLevel    *= rcp;
            p.extendUp      *= rcp;
        }

        // ── voxcaves_probe: biome blend breakdown logged to console ──────────────────
        static Dictionary<int, int> SetupPointBlend(float worldX, float worldZ, WorldGenerator wg, float s,
            out float coarseStep, out int pcw, out int pgSize, out int maxHops, out int center)
        {
            coarseStep = CellVox * s;
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
                float h = GroundHeight(worldX, worldZ, wg);
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

            float th = GroundHeight(worldX, worldZ, wg);
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
            float dhx = GroundHeight(worldX + hStep, worldZ, wg) - GroundHeight(worldX - hStep, worldZ, wg);
            float dhz = GroundHeight(worldX, worldZ + hStep, wg) - GroundHeight(worldX, worldZ - hStep, wg);
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
        // WorldGenerator.GetRiverWeight is private; bind a typed open delegate once via reflection (bake-time
        // only, so the reflection cost is trivial). Rebuilt if the WorldGenerator instance changes (new world).
        delegate void RiverWeightDel(float wx, float wy, out float weight, out float width);
        static RiverWeightDel? _riverWeightFn;
        static WorldGenerator? _riverWeightWg;
        static RiverWeightDel? RiverWeightFn(WorldGenerator wg)
        {
            if (wg == null) return null;
            if (_riverWeightFn != null && ReferenceEquals(_riverWeightWg, wg)) return _riverWeightFn;
            var mi = typeof(WorldGenerator).GetMethod("GetRiverWeight",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (mi == null) { Log.LogWarning("[ProceduralTerrain] riverProtect: WorldGenerator.GetRiverWeight not found — river protection disabled."); return null; }
            _riverWeightFn = (RiverWeightDel)System.Delegate.CreateDelegate(typeof(RiverWeightDel), wg, mi);
            _riverWeightWg = wg;
            return _riverWeightFn;
        }

        static void BakeBlendedProfiles(int cw4, int ox, int oz, int overlap, float s, WorldGenerator wg)
        {
            var profs = _biomeProfiles;
            CaveProfile GetP(Heightmap.Biome b) =>
                profs.TryGetValue(b, out var p) ? p : CaveProfile.Default;

            int   cellVox    = CellVox;
            // B: decouple biome sampling from carve resolution. Biomes vary over 100m+, so supersampling
            // them at a 1m carve cell was pure waste (the ~60ms/zone). Use a biome grid cell >= 4 voxels
            // and map carve cells to it by nearest-neighbor. At cellVox >= 4 this is IDENTITY: bCell ==
            // cellVox → biomeCw == cw4 → pIdx == (cx4+pad)*pcw+(cz4+pad), i.e. exactly the old behavior.
            int   bCell      = Math.Max(cellVox, 4);
            float coarseStep = bCell * s;
            int   ownedVox   = (cw4 - 1) * cellVox;                 // zone+overlap extent in voxels
            int   biomeCw    = (ownedVox + bCell - 1) / bCell + 1;  // biome grid width (== cw4 when bCell==cellVox)
            int pad    = (int)(_biomeBlendRadius / coarseStep) + 2;
            int pcw    = biomeCw + 2 * pad;
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
                int gx0 = ox - overlap + (px - pad) * bCell;
                int gz0 = oz - overlap + (pz - pad) * bCell;
                int cellBase = (px * pcw + pz) * MaxBiomeSlots;
                for (int si = 0; si < SuperN; si++)
                for (int sk = 0; sk < SuperN; sk++)
                {
                    float wx = (gx0 + (si + 0.5f) * bCell / SuperN) * s;
                    float wz = (gz0 + (sk + 0.5f) * bCell / SuperN) * s;
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
            if (_oceanDistCells == null || _oceanDistCells.Length < cellCount) _oceanDistCells = new float[cellCount];
            for (int i = 0; i < cellCount; i++) _oceanDistCells[i] = 1e9f;   // default: no Ocean nearby (coastFade won't fire inland)

            // SDF-blend: retain per-cell biome contributions (biome + weight) so BakeColumnSdfBlend
            // can bake each present biome's own params and crossfade the output columns (no param chirp).
            bool sdfBlend = _blendMode == 1;
            if (sdfBlend)
            {
                int slotStride = cellCount * MaxBiomeSlots;
                if (_sdfBiomeInt == null || _sdfBiomeInt.Length < slotStride) _sdfBiomeInt = new int[slotStride];
                if (_sdfBiomeW   == null || _sdfBiomeW.Length   < slotStride) _sdfBiomeW   = new float[slotStride];
                if (_sdfBiomeN   == null || _sdfBiomeN.Length   < cellCount)  _sdfBiomeN   = new int[cellCount];
                Array.Clear(_sdfBiomeN, 0, cellCount);
            }

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
            if (sdfBlend && (_sdfPresentW == null || _sdfPresentW.Length < pLen * cellCount))
                _sdfPresentW = new float[pLen * cellCount];

            foreach (var (biomeInt, slot) in biomeSlot)
            {
                var prof = GetP((Heightmap.Biome)biomeInt);

                // Multi-source BFS: seed from every cell that has any fraction of this biome.
                RunBiomeBFS(slot, pcw, pgSize, maxHops);

                // Capture Ocean's distance (world units) per carve cell → coastFade keys on this (coastline proximity).
                if ((Heightmap.Biome)biomeInt == Heightmap.Biome.Ocean && _coastFade > 0f)
                    for (int cx4 = 0; cx4 < cw4; cx4++)
                    for (int cz4 = 0; cz4 < cw4; cz4++)
                    {
                        int obx = (int)((cx4 * cellVox) / (float)bCell + 0.5f) + pad; if (obx >= pcw) obx = pcw - 1;
                        int obz = (int)((cz4 * cellVox) / (float)bCell + 0.5f) + pad; if (obz >= pcw) obz = pcw - 1;
                        int dc = _biomeDist![obx * pcw + obz];
                        _oceanDistCells![cx4 * cw4 + cz4] = dc >= int.MaxValue ? 1e9f : dc * coarseStep;
                    }

                float[][]? bParams = biomeParams.TryGetValue((Heightmap.Biome)biomeInt, out var bp) ? bp : null;

                // Accumulate weighted profile into each inner coarse cell.
                // At distance 0, scale by fractional membership so a cell that is 50% BF
                // contributes half as much as a fully-BF cell at the same distance.
                for (int cx4 = 0; cx4 < cw4; cx4++)
                for (int cz4 = 0; cz4 < cw4; cz4++)
                {
                    // Nearest-neighbor map this carve cell to the (coarser) biome grid. Identity when
                    // bCell == cellVox: (cx4*cellVox)/bCell == cx4 → bpx == cx4+pad, as before.
                    int bpx = (int)((cx4 * cellVox) / (float)bCell + 0.5f) + pad;
                    int bpz = (int)((cz4 * cellVox) / (float)bCell + 0.5f) + pad;
                    if (bpx >= pcw) bpx = pcw - 1;
                    if (bpz >= pcw) bpz = pcw - 1;
                    int pIdx = bpx * pcw + bpz;
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
                    if (sdfBlend)
                    {
                        int k = _sdfBiomeN![idx];
                        if (k < MaxBiomeSlots)
                        {
                            _sdfBiomeInt![idx * MaxBiomeSlots + k] = biomeInt;
                            _sdfBiomeW  ![idx * MaxBiomeSlots + k] = w;
                            _sdfBiomeN[idx] = k + 1;
                        }
                    }
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

            // ── Step 2b: Coastline Ocean inflation (distance-to-Ocean, NOT height) ─────
            // Injects extra Ocean-profile weight within coastFade world units of the actual OCEAN BIOME
            // (from the Step-2 BFS distance). Suppresses carving near the real coastline — altitude-independent,
            // so a low inland biome (the swamp) is left alone. Replaces the old height-from-sealevel cutoff.
            if (_coastFade > 0f)
            {
                var oceanProf = GetP(Heightmap.Biome.Ocean);
                float[][]? oceanBP = biomeParams.TryGetValue(Heightmap.Biome.Ocean, out var obp) ? obp : null;
                for (int cx4 = 0; cx4 < cw4; cx4++)
                for (int cz4 = 0; cz4 < cw4; cz4++)
                {
                    int idx = cx4 * cw4 + cz4;
                    float coastW = _coastStrength * (1f - Mathf.Clamp01(_oceanDistCells![idx] / _coastFade));
                    if (coastW <= 0f) continue;
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

            // (River protection is NOT done here — injecting biome weight interacts with per-zone biome
            // sampling + normalization and seams at zone boundaries. It's applied directly in FillPhase1 as a
            // per-column crossfade of our deviation back toward vanilla, keyed on GetRiverWeight. See RiverFac.)

            // ── Step 3: Normalize ─────────────────────────────────────────────────────
            // NOTE: these scratch spans are allocated ONCE here, not inside the loops below.
            // stackalloc inside a loop is not freed per-iteration — it accumulates until the
            // method returns, so a per-cell/per-mode stackalloc overflows the stack at fine
            // coarseXZ (cellCount grows as (bw/CellVox)²). maxParamCount bounds every mode's nP.
            Span<float> paramAcc    = stackalloc float[maxParamCount > 0 ? maxParamCount : 1];
            Span<float> paramResult = stackalloc float[maxParamCount > 0 ? maxParamCount : 1];
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
                    if (sdfBlend) _sdfPresentW![mi * cellCount + idx] = presentW;
                    // Gather per-param accumulated sums into the reused scratch spans (hoisted above).
                    for (int pi = 0; pi < nP; pi++) paramAcc[pi] = modeParamAcc[accBase + pi * cellCount];
                    ModeDefinition.BlendParams(paramResult, paramAcc, tw, presentW, nP);
                    for (int pi = 0; pi < nP; pi++) _cb.bkModeParams![mi][pi][idx] = paramResult[pi];
                }
            }

        }
    }

} // namespace ProceduralTerrain
