# Blur + Grounding Problem Notes

## Hard facts about the system

- Zone buffer = 72×200×72 (bw×bh×bw), overlap=4 on each side, territory=4..67 in X and Z
- SDF convention: buf < 0 = solid, buf >= 0 = air
- VoxelField write covers territory only (cx=overlap..overlap+width-1)
- Blur reads the full buffer including overlap (neighbor's unblurred SDF)
- Island removal BFS seeds from cy=0 within territory, floods through solid within territory only
- Collision is defined by the voxel mesh. There is no safe "temporary wrong mesh" — if the mesh is wrong when a player loads in, collision is wrong. A two-phase load (placeholder then correction) is not viable.

## What Gaussian blur does wrong

- It is mean curvature flow: surfaces shrink, thin connections pinch off
- Pinched connections create floating islands that island removal then correctly (but undesirably) culls
- Fill value 10*s prevents blur from restoring removed voxels, but doesn't fix the pinch
- Running island removal post-blur catches blur-induced disconnections but also culls legitimate thin features

## Zone boundary blur discontinuity

- Blur reads overlap, which contains neighbor's SDF computed without blur
- This mixes blurred territory values with unblurred overlap values at the boundary
- Both blur quality and grounding correctness at zone boundaries depend on the same missing data: what is the neighbor's face state after its own full processing?

## The grounding / boundary cache problem

- A solid blob touching a zone face may be grounded in the neighbor zone but floating in this zone
- To correctly ground it: need neighbor's face cache (which voxels are grounded at the shared face)
- But neighbor's face cache requires running neighbor's full BFS first
- Circularity: A needs B's cache; B needs A's cache
- "Don't cull ambiguous boundary blobs" does NOT solve this — it just avoids the answer. The whole point is knowing the correct state. Without it, we're back to neighbor hopping at runtime.

## The precomputation approach

- Key insight: the face cache data is fully determined by the noise parameters, which are static for a given config
- Proposal: on hot-reload or world load (one-time, acceptable cost ~seconds), walk all zones in the loaded region in dependency order, compute full SDF + BFS + face cache for each, store results to disk
- At runtime zone load: read face cache from disk, no neighbor hopping needed
- This breaks the circularity by moving it to a precompute phase where we can afford to iterate
- Dependency order: zones with no neighbor dependencies first (trivially grounded), then propagate. May require multiple passes but that's fine offline.
- Cost is paid once per parameter change / world region, not per zone load

## Blur: same face data, same solution

- Boundary voxel blur should be constrained by face cache: what is across this face?
- If neighbor face voxel is air: don't blur solid toward it (suppress that tap)
- If neighbor face voxel is grounded solid: blur freely in that direction
- If neighbor face voxel is ungrounded solid: suppress or treat as air (it will be removed)
- This is NOT post-processing smoothing — it's just selecting which blur taps are valid based on cached face state
- Same precomputed face cache serves both grounding import and blur tap selection

## Underground cave networks

- Mode A tunnels are the existing mechanism
- Pocket removal (BFS from boundary air) is needed to reopen sealed underground cavities
- Risk: pocket removal may fill legitimate deep caves that don't touch a boundary face
- Face cache helps here too: a cave touching a zone face with confirmed-air on the other side stays open
- Cave connectivity across zones has the same face-cache dependency as grounding
