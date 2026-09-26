#!/usr/bin/env python3
"""
cfg_delta_audit — static analysis of procedural_terrain_biomes.cfg.

Answers: across every possible biome boundary, how hard does the blend have to
work to reconcile parameters? Two mechanisms are reported separately:

  (B) within-mode param gradient — a mode present in BOTH biomes with different
      param values. The blend interpolates the values and evaluates the generator
      once, so a swept *frequency* becomes a chirp (muddy). This is the thing SDF
      output-blending would fix. Ranked by a sensitivity-weighted relative delta.

  (A) cross-mode superposition — a mode present in only ONE biome of the pair.
      Fades in over the band and stacks with the other biome's modes via the SDF
      min/max op. Widening blendRadius makes this WORSE. Just counted/listed.

Also reports profile-level param jumps (floorDepth/floorFade/blur_passes/extendUp),
which seam terrain but don't cause chirp.

Usage:
  python3 cfg_delta_audit.py [path/to/procedural_terrain_biomes.cfg] [--top N]
"""

import sys, re, argparse
from collections import defaultdict

DEFAULT_CFG = ("/mnt/c/Users/curti/AppData/Roaming/com.kesomannen.gale/valheim/"
               "profiles/onepoint0/BepInEx/config/ProceduralTerrain/"
               "procedural_terrain_biomes.cfg")

# Param sensitivity to spatial sweep. Frequency/phase params turn into chirp when
# interpolated (neither-biome garbage). Amplitude is a benign fade. Shape params
# morph morphology — medium. Anything unlisted defaults to SHAPE.
FREQ_TOKENS  = ("freq", "frequency")   # substring match, lowercased
BENIGN       = {"amplitude", "featureamp", "noiseamp", "yoffset", "threshold"}
SHAPE_WEIGHT, FREQ_WEIGHT, BENIGN_WEIGHT = 1.0, 3.0, 0.15

# The nine biomes and which are "renders nothing" (vanilla passthrough). Used to
# flag param contamination coming from biomes that carve nothing but still vote.
BIOME_ORDER = ["Meadows","BlackForest","Swamp","Mountain","Plains","Flats",
               "Mistlands","AshLands","DeepNorth","Ocean"]


def sensitivity(param):
    p = param.lower()
    if p in BENIGN: return BENIGN_WEIGHT, "benign"
    if any(t in p for t in FREQ_TOKENS): return FREQ_WEIGHT, "freq"
    return SHAPE_WEIGHT, "shape"


def parse_cfg(path):
    modes, profiles, biomes = {}, {}, {}
    cur_kind = cur_name = None
    sec_re = re.compile(r'^\[(\w+):?(.*)\]\s*$')
    for raw in open(path):
        line = raw.split('#', 1)[0].strip()
        if not line: continue
        m = sec_re.match(line)
        if m:
            tag, name = m.group(1), m.group(2).strip()
            if tag == "Mode":
                cur_kind, cur_name = "mode", name; modes[name] = {}
            elif tag == "Profile":
                cur_kind, cur_name = "profile", name
                profiles[name] = {"modes": [], "over": defaultdict(dict), "plevel": {}}
            elif tag in ("Bake","Debug","Pipeline"):
                cur_kind = tag.lower(); cur_name = None
            else:  # a [BiomeName] assignment section
                cur_kind, cur_name = "biome", tag; biomes[tag] = {}
            continue
        if '=' not in line: continue
        k, v = (x.strip() for x in line.split('=', 1))
        if cur_kind == "mode":
            modes[cur_name][k.lower()] = v
        elif cur_kind == "profile":
            if k == "modes":
                profiles[cur_name]["modes"] = [x.strip() for x in v.split(',') if x.strip()]
            elif '.' in k:
                mn, pn = k.split('.', 1)
                profiles[cur_name]["over"][mn][pn.lower()] = v
            else:
                profiles[cur_name]["plevel"][k.lower()] = v
        elif cur_kind == "biome":
            if k == "profile": biomes[cur_name]["profile"] = v
        elif cur_kind == "debug":
            biomes.setdefault("__debug__", {})[k.lower()] = v
    return modes, profiles, biomes


def fnum(s):
    try: return float(s)
    except (TypeError, ValueError): return None


def resolve_biome(bname, biomes, profiles, modes):
    """-> (profile_name, {mode: {param: value_str}}, plevel{param:val})"""
    prof = biomes.get(bname, {}).get("profile")
    if not prof or prof not in profiles: return prof, {}, {}
    P = profiles[prof]
    eff = {}
    for mn in P["modes"]:
        base = dict(modes.get(mn, {}))
        base.pop("type", None); base.pop("file", None); base.pop("blend", None)
        base.update(P["over"].get(mn, {}))
        eff[mn] = base
    return prof, eff, dict(P["plevel"])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cfg", nargs="?", default=DEFAULT_CFG)
    ap.add_argument("--top", type=int, default=25)
    args = ap.parse_args()

    modes, profiles, biomes = parse_cfg(args.cfg)
    blend_radius = fnum(biomes.get("__debug__", {}).get("biomeblendradius")) or 32.0

    present = [b for b in BIOME_ORDER if b in biomes]
    resolved = {b: resolve_biome(b, biomes, profiles, modes) for b in present}
    vanilla = {b for b in present if resolved[b][0] == "vanilla_heightmap"}

    print(f"# cfg Δ-audit   blendRadius={blend_radius:g}wu")
    print(f"# biomes: {', '.join(present)}")
    print(f"# vanilla/passthrough (render ~nothing but still vote): "
          f"{', '.join(sorted(vanilla)) or 'none'}\n")

    B_rows, A_rows, plevel_rows = [], [], []
    for i in range(len(present)):
        for j in range(i + 1, len(present)):
            a, b = present[i], present[j]
            _, eff_a, pl_a = resolved[a]
            _, eff_b, pl_b = resolved[b]
            ma, mb = set(eff_a), set(eff_b)

            for mn in ma & mb:                      # (B) shared mode
                for pn in set(eff_a[mn]) | set(eff_b[mn]):
                    va, vb = fnum(eff_a[mn].get(pn)), fnum(eff_b[mn].get(pn))
                    if va is None or vb is None: continue
                    d = abs(va - vb)
                    if d == 0: continue
                    denom = max(abs(va), abs(vb)) or 1.0
                    rel = d / denom
                    w, cls = sensitivity(pn)
                    contaminated = (a in vanilla) or (b in vanilla)
                    B_rows.append((w * rel, cls, a, b, mn, pn, va, vb, d,
                                   d / blend_radius, contaminated))

            for mn in ma ^ mb:                      # (A) one-sided mode
                owner = a if mn in ma else b
                other = b if mn in ma else a
                amp = fnum(eff_a.get(mn, eff_b.get(mn, {})).get("amplitude"))
                A_rows.append((owner, other, mn, amp))

            for pn in set(pl_a) | set(pl_b):        # profile-level jumps
                va, vb = fnum(pl_a.get(pn)), fnum(pl_b.get(pn))
                if va is None or vb is None: continue
                d = abs(va - vb)
                if d: plevel_rows.append((d, a, b, pn, va, vb))

    B_rows.sort(reverse=True)
    plevel_rows.sort(reverse=True)

    print("=" * 78)
    print("(B) WITHIN-MODE PARAM GRADIENT — chirp risk. score = sensitivity × relΔ")
    print("=" * 78)
    print(f"{'score':>6} {'cls':<6} {'biome pair':<22} {'mode':<20} "
          f"{'param':<14} {'A':>9} {'B':>9} {'Δ/wu':>8}  flag")
    for r in B_rows[:args.top]:
        score, cls, a, b, mn, pn, va, vb, d, dpwu, cont = r
        flag = "⚠vanilla-drag" if cont else ""
        print(f"{score:6.2f} {cls:<6} {a+'↔'+b:<22} {mn:<20} {pn:<14} "
              f"{va:9.4g} {vb:9.4g} {dpwu:8.4g}  {flag}")
    print(f"\n  ({len(B_rows)} nonzero shared-param deltas total; showing top {args.top})")
    # sensitivity-class rollup
    by_cls = defaultdict(lambda: [0, 0.0])
    for r in B_rows:
        by_cls[r[1]][0] += 1; by_cls[r[1]][1] = max(by_cls[r[1]][1], r[0])
    print("  by class:  " + "   ".join(
        f"{c}: {n} deltas, max score {s:.2f}" for c, (n, s) in sorted(by_cls.items())))

    print("\n" + "=" * 78)
    print("(A) CROSS-MODE SUPERPOSITION — one-sided modes (widening radius worsens)")
    print("=" * 78)
    counts = defaultdict(int)
    for owner, other, mn, amp in A_rows:
        counts[(mn, owner)] += 1
    for (mn, owner), c in sorted(counts.items(), key=lambda x: -x[1]):
        print(f"  {mn:<20} only in {owner:<12} → stacks across {c} boundaries")

    print("\n" + "=" * 78)
    print("PROFILE-LEVEL JUMPS — seams (floor/blur), not chirp")
    print("=" * 78)
    print(f"{'Δ':>7} {'biome pair':<24} {'param':<14} {'A':>7} {'B':>7}")
    for d, a, b, pn, va, vb in plevel_rows[:args.top]:
        print(f"{d:7.3g} {a+'↔'+b:<24} {pn:<14} {va:7.4g} {vb:7.4g}")

    print("\n# verdict heuristic:")
    top = B_rows[0][0] if B_rows else 0
    freq_max = by_cls.get("freq", [0,0])[1]
    print(f"#   top (B) score = {top:.2f}; top freq-class score = {freq_max:.2f}")
    if freq_max >= 1.5:
        print("#   → high frequency-sweep present: SDF output-blend likely worth it.")
    elif top < 0.5:
        print("#   → all deltas mild: muddle is probably (A) superposition, not (B).")
    else:
        print("#   → mixed: shape morphs dominate; SDF-blend helps some, not decisive.")


if __name__ == "__main__":
    main()
