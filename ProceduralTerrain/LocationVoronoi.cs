using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralTerrain
{
    // Global, deterministic corridor graph built once from the world's location set (+ optional Steiner fill),
    // annotated per edge, and shared by every corridor consumer (swamp canals, plains sewers, MST backbone, viz).
    //
    // The GRAPH is the transferable asset; SELECTION (seeded path / MST / loops) is policy on top. Because the graph
    // is a pure function of (location set, Steiner spacing, clearance) and locations are a pure function of the seed,
    // every consumer sees the same corridors in every zone — seam-free by construction.
    internal static class LocationVoronoi
    {
        internal struct CEdge
        {
            public int A, B;          // vertex indices into Verts
            public float Length;      // world meters
            public float Clearance;   // min over the segment of (dist to nearest LOCATION − that location's radius)
            public bool Ocean;        // BOTH endpoints in the Ocean biome (fully-submerged segment)
            public bool Steiner;      // edge touches a Steiner point (the densified fill)
        }

        internal sealed class CorridorGraph
        {
            public readonly Vector2[] Verts;
            public readonly float[] VertClearance;   // per-vertex min (dist − radius) to any LOCATION (for atrium siting)
            public readonly CEdge[] Edges;
            public readonly int[][] Adj;          // vertex -> incident edge indices
            public readonly float SteinerSpacing, Clearance;

            readonly Dictionary<(int, int), List<int>> _grid = new Dictionary<(int, int), List<int>>();
            readonly float _cell;

            public CorridorGraph(Vector2[] verts, float[] vertClearance, CEdge[] edges, int[][] adj, float cell, float steinerSpacing, float clearance)
            {
                Verts = verts; VertClearance = vertClearance; Edges = edges; Adj = adj; _cell = cell;
                SteinerSpacing = steinerSpacing; Clearance = clearance;
                for (int i = 0; i < verts.Length; i++)
                {
                    var key = (Mathf.FloorToInt(verts[i].x / _cell), Mathf.FloorToInt(verts[i].y / _cell));
                    if (!_grid.TryGetValue(key, out var lst)) { lst = new List<int>(); _grid[key] = lst; }
                    lst.Add(i);
                }
            }

            public int NearestNode(Vector2 p)
            {
                int best = -1; float bestD = float.MaxValue;
                int cx = Mathf.FloorToInt(p.x / _cell), cz = Mathf.FloorToInt(p.y / _cell);
                for (int ring = 0; ring < 64; ring++)
                {
                    for (int dz = -ring; dz <= ring; dz++)
                    for (int dx = -ring; dx <= ring; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;   // only the new ring shell
                        if (!_grid.TryGetValue((cx + dx, cz + dz), out var lst)) continue;
                        foreach (int i in lst) { float d = (Verts[i] - p).sqrMagnitude; if (d < bestD) { bestD = d; best = i; } }
                    }
                    // once we have a hit, one extra ring guarantees correctness (nearest cell may be diagonal)
                    if (best >= 0 && ring >= 1) break;
                }
                return best;
            }
        }

        static readonly object _lock = new object();       // guards _cache/_key reads+writes (cheap, held briefly)
        static readonly object _buildLock = new object();   // single-flight: only ONE expensive Build runs at a time
        static CorridorGraph? _cache;
        static (float sp, float cl, int nLoc, int posHash) _key;

        public static void Invalidate() { lock (_lock) { _cache = null; _key = default; } }

        // Build or return the cached global graph for a Steiner spacing + clearance. Returns null if locations aren't
        // ready yet (< 3) or worldgen is unavailable.
        //
        // The global Delaunay over the whole location set is expensive (seconds) and memory-heavy, and GetCanalRoute
        // is called from many bake worker threads + the main thread. Builds are SINGLE-FLIGHTED under _buildLock so a
        // first-contact cache miss can't fan out into N concurrent full builds (that OOM-crashed the game). Late
        // callers block on _buildLock, then fall straight through to the freshly-cached graph.
        public static CorridorGraph? Get(float steinerSpacing, float clearance)
        {
            var locs = Plugin._locationPositions;
            var wg = WorldGenerator.instance;
            if (wg == null || locs == null || locs.Count < 3) return null;

            int posHash = locs.Count;
            for (int i = 0; i < locs.Count; i += Math.Max(1, locs.Count / 32))
                posHash = posHash * 31 + (int)locs[i].pos.x * 73856093 + (int)locs[i].pos.y * 19349663;
            var k = (steinerSpacing, clearance, locs.Count, posHash);
            lock (_lock) { if (_cache != null && _key.Equals(k)) return _cache; }

            lock (_buildLock)
            {
                lock (_lock) { if (_cache != null && _key.Equals(k)) return _cache; }   // re-check: someone built it while we waited
                var g = Build(locs, wg, steinerSpacing, clearance);
                lock (_lock) { _cache = g; _key = k; }
                return g;
            }
        }

        static CorridorGraph Build(List<(Vector2 pos, float extR)> locs, WorldGenerator wg, float steinerSpacing, float clearance)
        {
            // ---- sites: locations first (indices < nLoc), then Steiner fill ----
            var sites = new List<CanalRouter.Site>(locs.Count);
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (var l in locs)
            {
                sites.Add(new CanalRouter.Site(l.pos.x, l.pos.y, Math.Max(l.extR, Plugin.LocProtectR)));
                if (l.pos.x < minX) minX = l.pos.x; if (l.pos.x > maxX) maxX = l.pos.x;
                if (l.pos.y < minZ) minZ = l.pos.y; if (l.pos.y > maxZ) maxZ = l.pos.y;
            }
            int nLoc = sites.Count;

            // location spatial grid (for clearance annotation + Steiner prune)
            float maxLocR = 0f; foreach (var s in sites) if ((float)s.r > maxLocR) maxLocR = (float)s.r;
            float lcell = Math.Max(steinerSpacing > 0f ? steinerSpacing : 400f, maxLocR + clearance + 1f);
            var lgrid = new Dictionary<(int, int), List<int>>();
            int LC(float v) => Mathf.FloorToInt(v / lcell);
            for (int i = 0; i < nLoc; i++)
            {
                var key = (LC((float)sites[i].x), LC((float)sites[i].z));
                if (!lgrid.TryGetValue(key, out var lst)) { lst = new List<int>(); lgrid[key] = lst; }
                lst.Add(i);
            }
            // min (dist − r) to any location, via the grid (3x3 neighbourhood is enough since cell ≥ maxLocR+clearance)
            float ClearanceAt(float px, float pz)
            {
                float best = float.MaxValue; int cx = LC(px), cz = LC(pz);
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if (lgrid.TryGetValue((cx + dx, cz + dz), out var lst))
                        foreach (int li in lst)
                        {
                            float ddx = (float)sites[li].x - px, ddz = (float)sites[li].z - pz;
                            float d = Mathf.Sqrt(ddx * ddx + ddz * ddz) - (float)sites[li].r;
                            if (d < best) best = d;
                        }
                return best == float.MaxValue ? 1e6f : best;
            }
            bool TooClose(float px, float pz)
            {
                int cx = LC(px), cz = LC(pz);
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if (lgrid.TryGetValue((cx + dx, cz + dz), out var lst))
                        foreach (int li in lst)
                        {
                            float ddx = (float)sites[li].x - px, ddz = (float)sites[li].z - pz;
                            float rr = (float)sites[li].r + clearance;
                            if (ddx * ddx + ddz * ddz < rr * rr) return true;
                        }
                return false;
            }

            // ---- Steiner lattice, aligned to WORLD ORIGIN (globally stable), jittered per cell, clearance-pruned ----
            if (steinerSpacing > 0f)
            {
                float jit = steinerSpacing * 0.35f, margin = steinerSpacing;
                int ix0 = Mathf.FloorToInt((minX - margin) / steinerSpacing), ix1 = Mathf.CeilToInt((maxX + margin) / steinerSpacing);
                int iz0 = Mathf.FloorToInt((minZ - margin) / steinerSpacing), iz1 = Mathf.CeilToInt((maxZ + margin) / steinerSpacing);
                for (int iz = iz0; iz <= iz1; iz++)
                for (int ix = ix0; ix <= ix1; ix++)
                {
                    uint h = (uint)(ix * 73856093) ^ (uint)(iz * 19349663); h ^= h >> 13; h *= 2246822519u; h ^= h >> 16;
                    float jx = ((h & 0xffff) / 65535f - 0.5f) * 2f * jit;
                    float jz = (((h >> 16) & 0xffff) / 65535f - 0.5f) * 2f * jit;
                    float sx = ix * steinerSpacing + jx, sz = iz * steinerSpacing + jz;
                    if (clearance > 0f && TooClose(sx, sz)) continue;
                    sites.Add(new CanalRouter.Site(sx, sz, 0f));
                }
            }

            // ---- triangulate the union, annotate each edge ----
            var (verts2, edges, kinds) = CanalRouter.BuildGraph(sites, nLoc);
            var verts = verts2.ToArray();
            var vertClr = new float[verts.Length];
            for (int i = 0; i < verts.Length; i++) vertClr[i] = ClearanceAt(verts[i].x, verts[i].y);
            var cedges = new CEdge[edges.Count];
            var adjLists = new List<int>[verts.Length];
            for (int i = 0; i < edges.Count; i++)
            {
                var (a, b) = edges[i];
                Vector2 pa = verts[a], pb = verts[b];
                float len = (pa - pb).magnitude;
                // clearance = min over a few samples along the segment
                float clr = float.MaxValue; int ns = Math.Max(2, (int)(len / 15f));
                for (int k = 0; k <= ns; k++) { float t = (float)k / ns; float mx = pa.x + (pb.x - pa.x) * t, mz = pa.y + (pb.y - pa.y) * t; float c = ClearanceAt(mx, mz); if (c < clr) clr = c; }
                bool ocean = wg.GetBiome(pa.x, pa.y) == Heightmap.Biome.Ocean && wg.GetBiome(pb.x, pb.y) == Heightmap.Biome.Ocean;
                cedges[i] = new CEdge { A = a, B = b, Length = len, Clearance = clr, Ocean = ocean, Steiner = (i < kinds.Count && kinds[i] == 1) };
                (adjLists[a] ??= new List<int>()).Add(i);
                (adjLists[b] ??= new List<int>()).Add(i);
            }
            var adj = new int[verts.Length][];
            for (int i = 0; i < verts.Length; i++) adj[i] = adjLists[i]?.ToArray() ?? Array.Empty<int>();

            float ncell = Math.Max(steinerSpacing > 0f ? steinerSpacing : 400f, 100f);
            _graphId++;   // invalidate the atrium cache — it's keyed off a fresh graph identity
            return new CorridorGraph(verts, vertClr, cedges, adj, ncell, steinerSpacing, clearance);
        }

        // ---------------------------------------------------------------------------------------------------------
        // POLICY: seeded least-cost path — the swamp/plains canal. Routes from `start` toward `dir` to the deepest
        // reachable inland node, clearance-weighted (reads edge.Clearance), then Chaikin-smoothed. Returns world-space
        // points at y=waterY, or null if no acceptable inland terminal is reachable. Mirrors CanalRouter.RouteOne.
        // ---------------------------------------------------------------------------------------------------------
        public static List<Vector3>? RoutePath(CorridorGraph g, Vector2 start, Vector2 dir, CanalRouter.Params pr, float waterY)
        {
            var nodes = RouteNodes(g, start, dir, pr);
            if (nodes == null) return null;
            var raw = new List<Vector2>(nodes.Count); foreach (int i in nodes) raw.Add(g.Verts[i]);
            var sm = CanalRouter.Smooth(raw, pr.Smooth);
            var result = new List<Vector3>(sm.Count); foreach (var p in sm) result.Add(new Vector3(p.x, waterY, p.y));
            return result;
        }

        // The seeded least-cost path as a list of GRAPH VERTEX INDICES (start node → deepest-inland terminal). This is
        // the form the corridor network unions over (so junctions = shared vertices). RoutePath wraps this + smooths.
        public static List<int>? RouteNodes(CorridorGraph g, Vector2 start, Vector2 dir, CanalRouter.Params pr)
        {
            int si = g.NearestNode(start);
            if (si < 0) return null;

            // BOUNDED Dijkstra over only the local region (like the legacy windowed router). The graph is GLOBAL
            // (~24k nodes); an unbounded search per seed is what crashed plains when flying. We only ever accept a
            // terminal within ~InlandGoal of the start, so we stop expanding nodes beyond maxReach. Sparse
            // (dictionary/heap) structures keep each call proportional to the LOCAL node count, not the whole world.
            float maxReach = (float)pr.InlandGoal + 400f, maxReach2 = maxReach * maxReach;
            var dist = new Dictionary<int, float>(256) { [si] = 0f };
            var came = new Dictionary<int, int>(256);
            var done = new HashSet<int>();
            var touched = new List<int> { si };
            var heap = new MinHeap(256);
            heap.Push(si, 0f);
            while (heap.Pop(out int u, out float du))
            {
                if (!done.Add(u)) continue;
                // don't expand outward from nodes already past the local reach (they can't yield a nearer terminal)
                float oxu = g.Verts[u].x - start.x, ozu = g.Verts[u].y - start.y;
                if (oxu * oxu + ozu * ozu > maxReach2) continue;
                foreach (int ei in g.Adj[u])
                {
                    var e = g.Edges[ei];
                    if (e.Clearance < pr.BoatClear) continue;                 // boat can't fit — hard block
                    float pen = Mathf.Clamp01(((float)pr.ClearR0 - e.Clearance) / (float)pr.ClearR0);
                    float w = e.Length * (1f + (float)pr.Beta * pen);
                    int v = e.A == u ? e.B : e.A;
                    float nd = du + w;
                    if (!dist.TryGetValue(v, out float dv) || nd < dv) { if (!dist.ContainsKey(v)) touched.Add(v); dist[v] = nd; came[v] = u; heap.Push(v, nd); }
                }
            }

            // pick the deepest-inland acceptable terminal (same scoring as CanalRouter.RouteOne), over touched nodes
            int best = -1; float bestScore = float.NegativeInfinity;
            foreach (int v in touched)
            {
                if (v == si) continue;
                float ox = g.Verts[v].x - start.x, oz = g.Verts[v].y - start.y;
                float inland = ox * dir.x + oz * dir.y;
                float lateral = Math.Abs(ox * -dir.y + oz * dir.x);
                if (inland < pr.MinProgress) continue;
                float over = Math.Max(0f, inland - (float)pr.InlandGoal);
                float score = Math.Min(inland, (float)pr.InlandGoal) - 0.3f * lateral - 0.6f * over - 0.003f * dist[v];
                if (score > bestScore) { bestScore = score; best = v; }
            }
            if (best < 0) return null;

            var idx = new List<int> { best }; int c = best; while (came.TryGetValue(c, out int pc)) { c = pc; idx.Add(c); }
            idx.Reverse();
            return idx;
        }

        // POLICY: minimum spanning tree over the graph (length-weighted), optionally dropping fully-ocean edges.
        // Returns the selected edge indices. Disjoint land masses yield a forest.
        public static List<int> BuildMST(CorridorGraph g, bool excludeOcean)
        {
            var order = new List<int>(g.Edges.Length);
            for (int i = 0; i < g.Edges.Length; i++) if (!(excludeOcean && g.Edges[i].Ocean)) order.Add(i);
            order.Sort((x, y) => g.Edges[x].Length.CompareTo(g.Edges[y].Length));
            var uf = new int[g.Verts.Length]; for (int i = 0; i < uf.Length; i++) uf[i] = i;
            int Find(int x) { while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; } return x; }
            var sel = new List<int>();
            foreach (int ei in order)
            {
                var e = g.Edges[ei]; int ra = Find(e.A), rb = Find(e.B);
                if (ra == rb) continue; uf[ra] = rb; sel.Add(ei);
            }
            return sel;
        }

        // POLICY: atrium sites = merge junctions. Start from degree≥3 Voronoi vertices (where ≥3 corridors meet),
        // then greedily cluster any within `mergeRadius` into a single site at the cluster centroid. Returns
        // (position, convergence) where convergence = max corridor-degree in the cluster (a dome-size weight).
        // Deterministic: candidates processed by (degree desc, index) so the same clusters form every run.
        // An atrium: its centre, convergence (corridor degree → dome size weight), and Links = the neighbour-junction
        // positions of its incident graph edges. The carve carves connector tunnels to each Link so the atrium is
        // ALWAYS anchored to the tunnel network (on the same medial axis the corridors follow), never isolated.
        public struct Atrium { public Vector2 Pos; public int Conv; public Vector2[] Links; }

        // minClearance: drop junctions whose clearance to the nearest LOCATION is below this, so atrium domes never
        // land within a location's protected footprint (pass ≈ dome radius + margin). Ocean junctions are dropped too
        // (sewer atriums are inland). The atrium is sited at the cluster's SEED junction (highest-degree member), NOT
        // the centroid — so the reported clearance is exact and no drift margin is needed. Clustering only dedupes
        // nearby junctions into one site; the seed junction's incident edges become the atrium's connector links.
        public static List<Atrium> AtriumSites(CorridorGraph g, float mergeRadius, float minClearance)
        {
            var wg = WorldGenerator.instance;
            var deg = new int[g.Verts.Length];
            foreach (var e in g.Edges) { deg[e.A]++; deg[e.B]++; }
            var cand = new List<int>();
            for (int i = 0; i < g.Verts.Length; i++)
            {
                if (deg[i] < 3 || g.VertClearance[i] < minClearance) continue;
                if (wg != null && wg.GetBiome(g.Verts[i].x, g.Verts[i].y) == Heightmap.Biome.Ocean) continue;
                cand.Add(i);
            }
            cand.Sort((a, b) => deg[a] != deg[b] ? deg[b].CompareTo(deg[a]) : a.CompareTo(b));

            Atrium Make(int sv, int conv)
            {
                var links = new List<Vector2>(g.Adj[sv].Length);
                foreach (int ei in g.Adj[sv]) { var e = g.Edges[ei]; int nb = e.A == sv ? e.B : e.A; links.Add(g.Verts[nb]); }
                return new Atrium { Pos = g.Verts[sv], Conv = conv, Links = links.ToArray() };
            }

            var sites = new List<Atrium>();
            if (mergeRadius <= 0f) { foreach (int i in cand) sites.Add(Make(i, deg[i])); return sites; }

            // spatial grid over candidates so clustering is local, not O(cand^2)
            float cell = mergeRadius;
            var grid = new Dictionary<(int, int), List<int>>();
            int GC(float v) => Mathf.FloorToInt(v / cell);
            foreach (int i in cand) { var key = (GC(g.Verts[i].x), GC(g.Verts[i].y)); if (!grid.TryGetValue(key, out var l)) { l = new List<int>(); grid[key] = l; } l.Add(i); }
            var claimed = new HashSet<int>();
            float mr2 = mergeRadius * mergeRadius;
            foreach (int i in cand)   // seeds come in (degree desc, index) order → highest-convergence junction wins the cluster
            {
                if (claimed.Contains(i)) continue;
                claimed.Add(i);
                int maxDeg = deg[i];
                int cx = GC(g.Verts[i].x), cz = GC(g.Verts[i].y);
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if (grid.TryGetValue((cx + dx, cz + dz), out var l))
                        foreach (int j in l)
                        {
                            if (claimed.Contains(j)) continue;
                            if ((g.Verts[j] - g.Verts[i]).sqrMagnitude > mr2) continue;
                            claimed.Add(j); if (deg[j] > maxDeg) maxDeg = deg[j];
                        }
                sites.Add(Make(i, maxDeg));   // site AT the seed junction — exact clearance, links from its incident edges
            }
            return sites;
        }

        static int _graphId;   // bumped each Build so the network cache invalidates with the graph

        // ---------------------------------------------------------------------------------------------------------
        // CORRIDOR NETWORK — the single source of truth for canals AND atriums. Coast-mouth graph vertices are routed
        // inland (RouteNodes) and UNIONED; the carve stamps these canal polylines, and atriums are the junctions of
        // this same network (vertices where ≥3 distinct routed edges meet). Atriums are therefore ON the carved
        // tunnels BY CONSTRUCTION — no connector hack, guaranteed reachable. Global + deterministic = seam-free.
        // ---------------------------------------------------------------------------------------------------------
        public sealed class CorridorNetwork
        {
            public readonly List<Vector3[]> Canals;   // smoothed world polylines at waterY (carve stamps these; viz draws them)
            public readonly List<Atrium> Atriums;     // junction domes
            readonly Dictionary<(int, int), List<int>> _canalGrid = new Dictionary<(int, int), List<int>>();
            readonly Dictionary<(int, int), List<int>> _atriumGrid = new Dictionary<(int, int), List<int>>();
            readonly float _cell;

            public CorridorNetwork(List<Vector3[]> canals, List<Atrium> atriums, float cell)
            {
                Canals = canals; Atriums = atriums; _cell = Math.Max(cell, 32f);
                for (int i = 0; i < canals.Count; i++)
                    foreach (var p in canals[i]) { var k = (Mathf.FloorToInt(p.x / _cell), Mathf.FloorToInt(p.z / _cell)); if (!_canalGrid.TryGetValue(k, out var l)) { l = new List<int>(); _canalGrid[k] = l; } if (l.Count == 0 || l[l.Count - 1] != i) l.Add(i); }
                for (int i = 0; i < atriums.Count; i++) { var k = (Mathf.FloorToInt(atriums[i].Pos.x / _cell), Mathf.FloorToInt(atriums[i].Pos.y / _cell)); if (!_atriumGrid.TryGetValue(k, out var l)) { l = new List<int>(); _atriumGrid[k] = l; } l.Add(i); }
            }

            public void CanalsNear(Vector2 c, float r, List<Vector3[]> outp)
            {
                int reach = Mathf.CeilToInt(r / _cell); int cx = Mathf.FloorToInt(c.x / _cell), cz = Mathf.FloorToInt(c.y / _cell);
                var seen = new HashSet<int>();
                for (int dz = -reach; dz <= reach; dz++)
                for (int dx = -reach; dx <= reach; dx++)
                    if (_canalGrid.TryGetValue((cx + dx, cz + dz), out var l))
                        foreach (int i in l) if (seen.Add(i)) outp.Add(Canals[i]);
            }
            public void AtriumsNear(Vector2 c, float r, List<Atrium> outp)
            {
                int reach = Mathf.CeilToInt(r / _cell); int cx = Mathf.FloorToInt(c.x / _cell), cz = Mathf.FloorToInt(c.y / _cell);
                float r2 = r * r;
                for (int dz = -reach; dz <= reach; dz++)
                for (int dx = -reach; dx <= reach; dx++)
                    if (_atriumGrid.TryGetValue((cx + dx, cz + dz), out var l))
                        foreach (int i in l) if ((Atriums[i].Pos - c).sqrMagnitude <= r2) outp.Add(Atriums[i]);
            }
        }

        static readonly object _netLock = new object();
        static CorridorNetwork? _netCache;
        static (int graphId, float cov, float merge, float clr, float over, float water, float goal, int biomeMask) _netKey;

        // Build (or reuse) the corridor network. coverage subsamples coast mouths (density). minClearance drops atria
        // in location footprints. waterY = canal floor. inlandGoal = how far each canal aims inland.
        // biomeMask = OR of Heightmap.Biome values the mode carves in (0 = no biome filter). Junctions/atriums outside
        // the mask are dropped so the network == what the carve actually places (biomemajority parity).
        public static CorridorNetwork? GetNetwork(float steiner, float clearance, float coverage, float mergeRadius, float minClearance, float minOverburden, float waterY, float inlandGoal, int biomeMask)
        {
            var g = Get(steiner, clearance);
            var wg = WorldGenerator.instance;
            if (g == null || wg == null) return null;
            var k = (_graphId, coverage, mergeRadius, minClearance, minOverburden, waterY, inlandGoal, biomeMask);
            lock (_netLock) { if (_netCache != null && _netKey.Equals(k)) return _netCache; }
            var net = BuildNetwork(g, wg, coverage, mergeRadius, minClearance, minOverburden, waterY, inlandGoal, biomeMask);
            lock (_netLock) { _netCache = net; _netKey = k; }
            return net;
        }

        static CorridorNetwork BuildNetwork(CorridorGraph g, WorldGenerator wg, float coverage, float mergeRadius, float minClearance, float minOverburden, float waterY, float inlandGoal, int biomeMask)
        {
            int nv = g.Verts.Length;
            var isOcean = new bool[nv];
            for (int i = 0; i < nv; i++) isOcean[i] = wg.GetBiome(g.Verts[i].x, g.Verts[i].y) == Heightmap.Biome.Ocean;

            int cCoast = 0, cMouthUsed = 0, cRouted = 0, cRouteFail = 0;   // funnel counters
            var pr = new CanalRouter.Params { InlandGoal = inlandGoal };
            var canals = new List<Vector3[]>();
            var usedDeg = new int[nv];                        // # distinct routed edges incident to each vertex
            var usedEdges = new HashSet<long>();              // dedupe routed edges
            long EK(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

            for (int v = 0; v < nv; v++)
            {
                if (isOcean[v]) continue;
                // coast mouth = land vertex with an ocean neighbour; inland dir = away from that neighbour
                Vector2 inland = Vector2.zero; bool coast = false;
                foreach (int ei in g.Adj[v]) { var e = g.Edges[ei]; int nb = e.A == v ? e.B : e.A; if (isOcean[nb]) { inland += (g.Verts[v] - g.Verts[nb]); coast = true; } }
                if (!coast) continue;
                cCoast++;
                // subsample mouths for density (deterministic per-vertex hash)
                uint hv = (uint)(v * 2654435761u); hv ^= hv >> 15; hv *= 2246822519u; hv ^= hv >> 13;
                if (coverage < 1f && hv / 4294967295f > coverage) continue;
                if (inland.sqrMagnitude < 1e-6f) continue; inland.Normalize();
                cMouthUsed++;

                var nodes = RouteNodes(g, g.Verts[v], inland, pr);
                if (nodes == null || nodes.Count < 2) { cRouteFail++; continue; }
                cRouted++;
                var raw = new List<Vector2>(nodes.Count); foreach (int n in nodes) raw.Add(g.Verts[n]);
                var sm = CanalRouter.Smooth(raw, pr.Smooth);
                var poly = new Vector3[sm.Count]; for (int i = 0; i < sm.Count; i++) poly[i] = new Vector3(sm[i].x, waterY, sm[i].y);
                canals.Add(poly);
                for (int i = 1; i < nodes.Count; i++) { long ek = EK(nodes[i - 1], nodes[i]); if (usedEdges.Add(ek)) { usedDeg[nodes[i - 1]]++; usedDeg[nodes[i]]++; } }
            }

            // atriums = network junctions (≥3 distinct routed edges meet), clearance-ok, non-ocean, biome-INTERIOR
            // (not on a biome edge → avoids half-in-the-wrong-biome rooms), clustered.
            int cJunc = 0, cRejOcean = 0, cRejClear = 0, cRejEdge = 0, cRejShallow = 0, cRejBiome = 0;
            var cand = new List<int>();
            for (int v = 0; v < nv; v++)
            {
                if (usedDeg[v] < 3) continue;
                cJunc++;
                if (isOcean[v]) { cRejOcean++; continue; }
                if (biomeMask != 0 && (biomeMask & (int)wg.GetBiome(g.Verts[v].x, g.Verts[v].y)) == 0) { cRejBiome++; continue; }   // wrong biome for this mode
                // overburden: surface must sit well above the water floor, else the dome breaches/floods. Use the
                // COMPOSED PT surface (GroundHeight) — the exact function the carve's atriummindepth gate uses — so
                // this atrium list == what's actually carved (vanilla GetHeight diverges on raised plains → the tool
                // would send you to junctions the carve skipped).
                if (Plugin.ComposedSurfaceSample(g.Verts[v].x, g.Verts[v].y, wg) < waterY + minOverburden) { cRejShallow++; continue; }
                if (g.VertClearance[v] < minClearance) { cRejClear++; continue; }
                if (wg.GetBiomeArea(new Vector3(g.Verts[v].x, 0f, g.Verts[v].y)) == Heightmap.BiomeArea.Edge) { cRejEdge++; continue; }
                cand.Add(v);
            }
            cand.Sort((a, b) => usedDeg[a] != usedDeg[b] ? usedDeg[b].CompareTo(usedDeg[a]) : a.CompareTo(b));
            var atriums = new List<Atrium>();
            float mr2 = mergeRadius > 0f ? mergeRadius * mergeRadius : 0f;
            var acell = Math.Max(mergeRadius, 64f);
            var agrid = new Dictionary<(int, int), List<int>>();
            int AG(float x) => Mathf.FloorToInt(x / acell);
            foreach (int v in cand) { var key = (AG(g.Verts[v].x), AG(g.Verts[v].y)); if (!agrid.TryGetValue(key, out var l)) { l = new List<int>(); agrid[key] = l; } l.Add(v); }
            var claimed = new HashSet<int>();
            foreach (int v in cand)
            {
                if (claimed.Contains(v)) continue; claimed.Add(v);
                int maxDeg = usedDeg[v];
                if (mr2 > 0f) { int cx = AG(g.Verts[v].x), cz = AG(g.Verts[v].y);
                    for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) if (agrid.TryGetValue((cx + dx, cz + dz), out var l))
                        foreach (int j in l) { if (claimed.Contains(j)) continue; if ((g.Verts[j] - g.Verts[v]).sqrMagnitude > mr2) continue; claimed.Add(j); if (usedDeg[j] > maxDeg) maxDeg = usedDeg[j]; } }
                // Links = neighbour-junction positions along this vertex's USED canal edges → the carve stamps short
                // connector tubes to these so the dome is never a sealed bubble in any single zone buffer (cork fix).
                var links = new List<Vector2>(4);
                foreach (int ei in g.Adj[v]) { var e = g.Edges[ei]; int nb = e.A == v ? e.B : e.A; if (usedEdges.Contains(EK(v, nb))) links.Add(g.Verts[nb]); }
                atriums.Add(new Atrium { Pos = g.Verts[v], Conv = maxDeg, Links = links.ToArray() });
            }
            _netBuildStats = $"verts={nv} | coastMouths={cCoast} →cov{coverage:F2}→ used={cMouthUsed} | routed={cRouted} (fail={cRouteFail}) | junctions(deg≥3)={cJunc} →rejOcean={cRejOcean} rejWrongBiome={cRejBiome} rejShallow={cRejShallow}(need surfH≥{waterY + minOverburden:F0}) rejClear={cRejClear}(minClr={minClearance:F0}) rejBiomeEdge={cRejEdge} → candidates={cand.Count} →mergeClustered→ atriums={atriums.Count}. canals={canals.Count}";
            Debug.Log("[PT-net] " + _netBuildStats);
            return new CorridorNetwork(canals, atriums, 128f);
        }
        internal static string _netBuildStats = "(none)";

        // tiny binary min-heap keyed by float priority (lazy-deletion: stale entries skipped via done[] in caller)
        sealed class MinHeap
        {
            int[] _node; float[] _pri; int _n;
            public MinHeap(int cap) { cap = Math.Max(16, cap); _node = new int[cap]; _pri = new float[cap]; }
            public void Push(int node, float pri)
            {
                if (_n == _node.Length) { Array.Resize(ref _node, _n * 2); Array.Resize(ref _pri, _n * 2); }
                int i = _n++; _node[i] = node; _pri[i] = pri;
                while (i > 0) { int p = (i - 1) / 2; if (_pri[p] <= _pri[i]) break; Swap(i, p); i = p; }
            }
            public bool Pop(out int node, out float pri)
            {
                if (_n == 0) { node = -1; pri = 0f; return false; }
                node = _node[0]; pri = _pri[0]; _n--;
                if (_n > 0) { _node[0] = _node[_n]; _pri[0] = _pri[_n]; int i = 0; while (true) { int l = 2 * i + 1, r = l + 1, m = i; if (l < _n && _pri[l] < _pri[m]) m = l; if (r < _n && _pri[r] < _pri[m]) m = r; if (m == i) break; Swap(i, m); i = m; } }
                return true;
            }
            void Swap(int a, int b) { (_node[a], _node[b]) = (_node[b], _node[a]); (_pri[a], _pri[b]) = (_pri[b], _pri[a]); }
        }
    }
}
