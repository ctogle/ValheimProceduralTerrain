#!/usr/bin/env python3
"""
rename_bake.py — recompute a PT bake's cfg-hash from biomes.cfg and rename the
bake file(s) to the new hash, WITHOUT rebaking.

Use this ONLY when a biomes.cfg edit changes the hash but provably does NOT change
the baked terrain (e.g. renaming a profile, repointing an assignment to an
IDENTICAL-body profile, or editing a comment). The hash is a conservative cache
key over the raw config text; it busts on any textual change. When you KNOW the
terrain is unchanged, the baked bytes are still valid — only the filename key
needs to catch up.

Replicates BakeManager.ComputeCfgHash exactly (FNV-1a over every line of
biomes.cfg, skipping _hashExcludedKeys by exact lowercased key before '=').

Usage:
  rename_bake.py <biomes.cfg>                 # dry-run: print new hash + planned renames
  rename_bake.py <biomes.cfg> --apply         # perform the renames
  rename_bake.py <biomes.cfg> --bakedir DIR   # bakes live elsewhere (default: cfg's dir)
"""
import sys, os, glob, argparse

# Mirror of BakeManager._hashExcludedKeys (BakeManager.cs:31-37). KEEP IN SYNC.
EXCLUDED = {
    "coarsexz", "coarsey", "bakeradius", "debugviz", "debugvizocclude", "vegviz",
    "vegvizradius", "hidelodmeshes", "disablevegetation", "applygroundwork",
    "protectlocations", "flatplanelocations", "locdomeheight", "locbandfade",
    "vegdensity", "vegforestwiden", "veginforest", "vegforestmax", "veggroupsize",
    "veggroupradius", "vegtiltinherit", "vegrandtilt", "vegmaxtilt",
    "vegconformmaxtilt", "vegsupportradius", "vegetationconform", "seed",
    "prefetch", "prefetchradius", "prefetchthreads", "cavecontent", "lodunderground",
}

MASK = 0xFFFFFFFF
FNV_OFFSET = 2166136261
FNV_PRIME = 16777619

def compute_cfg_hash(path):
    # .NET File.ReadAllLines: splits on \r\n / \r / \n, strips the terminator.
    raw = open(path, "rb").read().decode("utf-8", "replace")
    lines = raw.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    # split() leaves a trailing "" if the file ends with \n; ReadAllLines does NOT
    # yield that final empty line, so drop one trailing empty to match.
    if lines and lines[-1] == "":
        lines.pop()
    h = FNV_OFFSET
    for raw in lines:
        # Mirror BakeManager.ComputeCfgHash: strip comments (full-line + inline) + trailing whitespace,
        # skip now-empty lines, then hash only key=value content. KEEP IN LOCKSTEP with the C#.
        hpos = raw.find("#")
        line = (raw[:hpos] if hpos >= 0 else raw).rstrip()
        if not line:
            continue
        eq = line.find("=")
        if eq > 0:
            key = line[:eq].strip().lower()
            if key in EXCLUDED:
                continue
        for ch in line:
            h = ((h ^ (ord(ch) & 0xFF)) * FNV_PRIME) & MASK
        h = ((h ^ 0x0A) * FNV_PRIME) & MASK
    return h

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cfg")
    ap.add_argument("--bakedir", default=None)
    ap.add_argument("--apply", action="store_true")
    a = ap.parse_args()

    new_hash = compute_cfg_hash(a.cfg)
    new_hex = f"{new_hash:08X}"
    print(f"cfgHash({os.path.basename(a.cfg)}) = {new_hex}")

    bakedir = a.bakedir or os.path.dirname(os.path.abspath(a.cfg))
    bakes = sorted(glob.glob(os.path.join(bakedir, "procedural_terrain_bake_*.bin")))
    if not bakes:
        print(f"(no bake .bin files in {bakedir})")
        return

    import struct
    MAGIC = 0x564F5846   # 'FXOV'
    HDR_CFG_OFF = 14     # v5/v6: magic u32@0, ver u16@4, seed u64@6, cfgHash u32@14
    for b in bakes:
        name = os.path.basename(b)
        stem = name[len("procedural_terrain_bake_"):-len(".bin")]
        parts = stem.split("_")
        if len(parts) != 2:
            print(f"  SKIP {name} (unexpected name)")
            continue
        seed, old_hex = parts
        # read header: the cfgHash is embedded in the file too; renaming alone is NOT enough —
        # the loader validates the HEADER cfgHash and rejects the bake on mismatch.
        with open(b, "rb") as fh:
            hdr = fh.read(18)
        magic, ver = struct.unpack_from("<IH", hdr, 0)
        hdr_cfg = struct.unpack_from("<I", hdr, HDR_CFG_OFF)[0] if len(hdr) >= 18 else None
        if magic != MAGIC or ver not in (5, 6):
            print(f"  SKIP {name} (magic=0x{magic:08X} ver={ver} — not a v5/v6 bake; header patch unsupported)")
            continue
        fname_ok = old_hex.upper() == new_hex
        hdr_ok = hdr_cfg == new_hash
        if fname_ok and hdr_ok:
            print(f"  OK   {name} (filename + header current)")
            continue
        new_name = f"procedural_terrain_bake_{seed}_{new_hex}.bin"
        dst = os.path.join(bakedir, new_name)
        note = []
        if not hdr_ok: note.append(f"patch header 0x{hdr_cfg:08X}->{new_hex}")
        if not fname_ok: note.append(f"rename ->{new_name}")
        if a.apply:
            if not hdr_ok:
                with open(b, "r+b") as fh:
                    fh.seek(HDR_CFG_OFF); fh.write(struct.pack("<I", new_hash))
            if not fname_ok:
                os.rename(b, dst)
            print(f"  FIXED {name}: {'; '.join(note)}")
        else:
            print(f"  WOULD FIX {name}: {'; '.join(note)}")
    if not a.apply:
        print("\n(dry run — pass --apply to patch header + rename)")

if __name__ == "__main__":
    main()
