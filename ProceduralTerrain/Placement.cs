using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralTerrain
{
    // A single cfg-driven cave-content placement rule. One [Placement:name] block → one of these.
    //   kind=object  → a static prefab written as a ZDO (ore veins, rocks, giant-corpse scenery).
    //   kind=spawner → a CreatureSpawner chassis, optionally overridden to emit a different creature.
    // Rules with no explicit `prob` join their kind's shared weighted POOL (one pool fire-chance from
    // [CaveContent], members compete by `weight`, scaled by depth ramp). A rule that sets `prob` runs as
    // its OWN independent channel (bats, giants) with that per-crossing probability, ignoring the pool.
    internal enum PlacementKind { Object, Spawner }

    internal sealed class Placement
    {
        public string Name = "";
        public PlacementKind Kind = PlacementKind.Object;
        public HashSet<Heightmap.Biome> Biomes = new();
        public string[] PrefabCands = Array.Empty<string>();   // object prefab OR spawner chassis (first that resolves wins)
        public string? CreatureOverride;                        // spawner only: creature to force (blank = chassis native)

        public float Weight   = 1f;     // share within the kind's shared pool
        public float Prob     = -1f;    // <0 = join shared pool; >=0 = independent channel at this per-crossing prob
        public int   Cap      = -1;     // max of THIS rule per zone (<0 = use pool cap)
        public float Spacing  = -1f;    // min metres between placements of this rule (<0 = use pool spacing)
        public float DepthMin = 0f;     // nothing spawns above this depth below the surface
        public float DepthFull = -1f;   // ramp reaches full here (<0 = hard step at DepthMin)
        public float ZoneChance = 1f;   // fraction of zones even eligible for this rule (independent channels)

        // object-only placement shaping
        public bool  FloorOnly = false; // only on floor crossings (solid below, air above)
        public float Embed     = 0f;    // sink this far (m) into the floor
        public float Cover     = 0f;    // require the terrain's outer surface this far (m) above the deposit across
                                        // its lateral footprint (keeps it buried in rock, exposed only where a
                                        // tunnel cuts it — not poking out the mountainside). 0 = no check.
        public float Lift      = 0f;    // raise this far (m) off the crossing into the air side (flying spawners)
        public bool  Upright   = false; // force world-up orientation instead of tilting to the floor normal
        public bool  Surface   = false; // opt in to the TOP (surface) crossing — ground scatter, not cave-only
        public float MinY       = float.NegativeInfinity; // world-Y floor: skip crossings below this (e.g. keep scatter above sea level → raised hammocks)
        public float MinNormalY = 0.6f;  // floor-prop flatness gate; lower = allow steeper slopes (hammock flanks). only used for FloorOnly objects
        public float RandTilt   = 0f;    // extra random tilt (deg) about a random horizontal axis → varied X/Z orientation (roots don't all lie the same way). Y-yaw is always random.
        public float BiomePure  = 0f;    // >0: place only in the biome INTERIOR — WorldGenerator.GetBiomeArea != Edge (i.e. the point isn't near a boundary with an adjacent biome). 0 = off

        // resolved at runtime (needs ZNetScene). ALL candidates that resolve → one is picked uniformly per
        // placement (comma list = a random pool, not a first-wins fallback). Empty = none resolved.
        public GameObject[] Resolved = System.Array.Empty<GameObject>();
        public int CreatureHash;        // 0 = no override

        // ramp(depth): 0 below DepthMin, linear to 1 across [DepthMin, DepthFull], hard step if DepthFull<=DepthMin.
        public float Ramp(float depth)
        {
            if (depth < DepthMin) return 0f;
            if (DepthFull <= DepthMin) return 1f;
            return Mathf.Clamp01((depth - DepthMin) / (DepthFull - DepthMin));
        }
    }

    public partial class Plugin
    {
        internal static List<Placement> _placements = new();

        // Pool-level defaults (overridable per-rule). One shared OBJECT pool + one shared SPAWNER pool.
        internal static float _ccObjectProb     = 0.009f;
        internal static int   _ccObjectCap      = 9;
        internal static float _ccObjectSpacing  = 6f;
        internal static float _ccSpawnerProb    = 0.012f;
        internal static int   _ccSpawnerCap     = 8;
        internal static float _ccSpawnerSpacing = 10f;

        static bool _placementsResolved;

        // Built-in default set — installed when cavecontent=true but the cfg defines no [Placement:*] blocks,
        // so the mod still does something sensible out of the box. Mirrors the prior hardcoded Mountain POC.
        internal static List<Placement> BuildBuiltinPlacements()
        {
            var mtn = new HashSet<Heightmap.Biome> { Heightmap.Biome.Mountain };
            Placement Obj(string name, string[] cands, float weight, float embed, bool floor, float dMin, float dFull, float cover) =>
                new Placement { Name = name, Kind = PlacementKind.Object, Biomes = new(mtn), PrefabCands = cands,
                                Weight = weight, Embed = embed, FloorOnly = floor, DepthMin = dMin, DepthFull = dFull, Cover = cover };
            Placement Spawner(string name, string[] cands, string? creature, float weight, int cap) =>
                new Placement { Name = name, Kind = PlacementKind.Spawner, Biomes = new(mtn), PrefabCands = cands,
                                CreatureOverride = creature, Weight = weight, Cap = cap, FloorOnly = true, DepthFull = 60f };

            return new List<Placement>
            {
                // shared OBJECT pool (ores + rocks) — depthFull=60 gives the surface→deep ramp; embed+floor bury deposits
                Obj("tin",        new[]{ "MineRock_Tin" },            10f, 0f,   false, 0f,  60f, 4f),
                Obj("flint",      new[]{ "Pickable_Flint" },           7f, 0f,   false, 0f,  60f, 4f),
                Obj("obsidian",   new[]{ "MineRock_Obsidian" },        4f, 0f,   false, 0f,  60f, 4f),
                Obj("copper",     new[]{ "rock4_copper" },            10f, 1f,   true,  0f,  60f, 6f),
                Obj("muddyscrap", new[]{ "mudpile2", "mudpile" },     10f, 4f,   true,  0f,  60f, 4f),
                Obj("silver",     new[]{ "silvervein" },               2f, 4f,   true,  0f,  60f, 4f),
                // giant-corpse scenery — independent channel: deep, rare, upright, zone-gated
                new Placement { Name = "giant_sword",  Kind = PlacementKind.Object, Biomes = new(mtn),
                    PrefabCands = new[]{ "giant_sword1", "giant_sword2" }, Weight = 1f, Prob = 0.02f, Cap = 1,
                    Spacing = 40f, FloorOnly = true, Upright = true, DepthMin = 40f, ZoneChance = 0.2f },
                new Placement { Name = "giant_helmet", Kind = PlacementKind.Object, Biomes = new(mtn),
                    PrefabCands = new[]{ "giant_helmet1", "giant_helmet2" }, Weight = 1f, Prob = 0.02f, Cap = 1,
                    Spacing = 40f, FloorOnly = true, Upright = true, DepthMin = 40f, ZoneChance = 0.2f },
                // shared SPAWNER pool
                Spawner("wolf",    new[]{ "Spawner_Ulv" },        "Wolf", 8f, 7),
                Spawner("fenring", new[]{ "Spawner_Fenring" },    null,   1f, 2),
                Spawner("golem",   new[]{ "Spawner_StoneGolem" }, null,   1f, 1),
                // bat swarm — independent channel: own high prob/cap, tight spacing. NOTE: flying creatures don't
                // settle in a voxel cave, so Voxheim log-spams "unburied 'Bat(Clone)'" — a known, harmless annoyance.
                new Placement { Name = "bat", Kind = PlacementKind.Spawner, Biomes = new(mtn),
                    PrefabCands = new[]{ "Spawner_Bat" }, Weight = 1f, Prob = 0.02f, Cap = 20, Spacing = 4f, DepthFull = 60f },
            };
        }

        internal static void InvalidatePlacementResolution() => _placementsResolved = false;

        // Parse a [Placement:name] block. Returns null on a fatal problem (no kind / no biome).
        internal static Placement? ParsePlacement(string name, List<(string key, string val)> kvs)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var p = new Placement { Name = name };
            bool haveKind = false;
            float F(string v, float d) => float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out var f) ? f : d;
            foreach (var (k, v) in kvs)
            {
                switch (k)
                {
                    case "kind":
                        if (v.Equals("spawner", StringComparison.OrdinalIgnoreCase)) p.Kind = PlacementKind.Spawner;
                        else if (v.Equals("object", StringComparison.OrdinalIgnoreCase)) p.Kind = PlacementKind.Object;
                        else { Log.LogWarning($"[PT-cave] placement '{name}': unknown kind '{v}' (object|spawner)."); return null; }
                        haveKind = true; break;
                    case "biome":
                        foreach (var b in v.Split(','))
                        {
                            var bn = b.Trim(); if (bn.Length == 0) continue;
                            if (bn == "*" || bn.Equals("all", StringComparison.OrdinalIgnoreCase))
                                foreach (Heightmap.Biome bv in Enum.GetValues(typeof(Heightmap.Biome))) p.Biomes.Add(bv);
                            else if (Enum.TryParse<Heightmap.Biome>(bn, true, out var bio)) p.Biomes.Add(bio);
                            else Log.LogWarning($"[PT-cave] placement '{name}': unknown biome '{bn}'.");
                        }
                        break;
                    case "prefab":
                    case "chassis":
                        p.PrefabCands = v.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(); break;
                    case "creature":  p.CreatureOverride = string.IsNullOrWhiteSpace(v) ? null : v.Trim(); break;
                    case "weight":    p.Weight    = F(v, p.Weight); break;
                    case "prob":      p.Prob      = F(v, p.Prob); break;
                    case "cap":       p.Cap       = (int)F(v, p.Cap); break;
                    case "spacing":   p.Spacing   = F(v, p.Spacing); break;
                    case "depthmin":  p.DepthMin  = F(v, p.DepthMin); break;
                    case "depthfull": p.DepthFull = F(v, p.DepthFull); break;
                    case "zonechance":p.ZoneChance= F(v, p.ZoneChance); break;
                    case "floor":     p.FloorOnly = !(v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)); break;
                    case "embed":     p.Embed     = F(v, p.Embed); break;
                    case "cover":     p.Cover     = F(v, p.Cover); break;
                    case "lift":      p.Lift      = F(v, p.Lift); break;
                    case "upright":   p.Upright   = (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)); break;
                    case "surface":   p.Surface   = !(v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)); break;
                    case "miny":      p.MinY      = F(v, p.MinY); break;
                    case "minnormaly":p.MinNormalY= F(v, p.MinNormalY); break;
                    case "randtilt":  p.RandTilt  = F(v, p.RandTilt); break;
                    case "biomepure": p.BiomePure = F(v, p.BiomePure); break;
                }
            }
            if (!haveKind) { Log.LogWarning($"[PT-cave] placement '{name}': missing kind= (object|spawner)."); return null; }
            if (p.Biomes.Count == 0) { Log.LogWarning($"[PT-cave] placement '{name}': no biome= — rule is inert."); return null; }
            if (p.PrefabCands.Length == 0) { Log.LogWarning($"[PT-cave] placement '{name}': no prefab=/chassis=."); return null; }
            return p;
        }

        static void ResolvePlacements()
        {
            if (_placementsResolved) return;
            var zs = ZNetScene.instance; if (zs == null) return;
            foreach (var p in _placements)
            {
                var hits = new List<GameObject>(); var misses = new List<string>();
                foreach (var n in p.PrefabCands) { var go = zs.GetPrefab(n); if (go != null) hits.Add(go); else misses.Add(n); }
                p.Resolved = hits.ToArray();
                p.CreatureHash = string.IsNullOrEmpty(p.CreatureOverride) ? 0 : StringExtensionMethods.GetStableHashCode(p.CreatureOverride);
                string ov = p.CreatureHash != 0 ? $" → {p.CreatureOverride}" : "";
                string missTxt = misses.Count > 0 ? $"  MISSING (won't resolve): [{string.Join(",", misses)}]" : "";
                Log.LogInfo($"[PT-cave] resolve {p.Name} ({p.Kind}): pool={hits.Count}/{p.PrefabCands.Length} [{string.Join(",", hits.ConvertAll(g => g.name))}]{ov}{missTxt}");
            }
            _placementsResolved = true;
        }
    }
}
