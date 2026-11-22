#!/usr/bin/env python3
"""
rename_bake.py — DEPRECATED (2026-11-22).

ComputeCfgHash no longer hashes the cfg TEXT — it hashes the RESOLVED in-memory
generation structure (pipeline + per-biome resolved params + globals), which can
only be computed by the C# runtime, NOT replicated offline here. So this script's
compute_cfg_hash is NO LONGER ACCURATE and will print a WRONG hash.

It's also largely unnecessary now: the new hash is default-normalised and
format/order-robust, so terrain-NEUTRAL edits (reformatting, reordering, writing a
default value, renaming a profile) don't change it — there's nothing to re-key.
To read a world's current hash, use the in-game `pt_bakeinfo` command.

The header-patch + rename plumbing below still works IF you already know the
target hash, but DO NOT trust compute_cfg_hash.

Usage:
  rename_bake.py <biomes.cfg>                 # dry-run: print new hash + planned renames
  rename_bake.py <biomes.cfg> --apply         # perform the renames
  rename_bake.py <biomes.cfg> --bakedir DIR   # bakes live elsewhere (default: cfg's dir)
"""
import sys, os, glob, argparse

# Mirror of BakeManager._hashExcludedKeys (BakeManager.cs:31-37). KEEP IN SYNC.
# Keep in lockstep with BakeManager._hashExcludedKeys. Client/render/debug keys now live in client.cfg (not
# biomes.cfg) so they're not here. Residual = seed (filename-keyed) + live-overlay + placement-file (dead-safe).
EXCLUDED = {
    "seed",
    "applygroundwork", "protectlocations", "flatplanelocations", "locdomeheight", "locbandfade",
    "disablevegetation", "vegdensity", "vegforestwiden", "veginforest", "vegforestmax", "veggroupsize",
    "veggroupradius", "vegtiltinherit", "vegrandtilt", "vegmaxtilt", "vegconformmaxtilt", "vegsupportradius",
    "vegetationconform", "cavecontent",
}

MASK = 0xFFFFFFFF
FNV_OFFSET = 2166136261
FNV_PRIME = 16777619

def compute_cfg_hash(path):
    # Mirror BakeManager.ComputeCfgHash: hash the PARSED config — one (section, key, value) triple per
    # generation-affecting assignment, value whitespace stripped, sorted (ordinal), then FNV-1a.
    # Order-independent + format-robust + section-scoped. KEEP IN LOCKSTEP with the C#.
    raw = open(path, "rb").read().decode("utf-8", "replace")
    lines = raw.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    triples = []
    section = ""
    for ln in lines:
        hpos = ln.find("#")
        line = (ln[:hpos] if hpos >= 0 else ln).strip()
        if not line:
            continue
        if line[0] == "[" and line[-1] == "]":
            section = line[1:-1].strip().lower()
            continue
        eq = line.find("=")
        if eq <= 0:
            continue
        key = line[:eq].strip().lower()
        if key in EXCLUDED:
            continue
        val = "".join(c for c in line[eq + 1:] if not c.isspace())
        triples.append(section + "\x1f" + key + "\x1f" + val)
    triples.sort()
    h = FNV_OFFSET
    for t in triples:
        for ch in t:
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
