using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralTerrain
{
    // Voronoi-corridor canal routing, ported from the validated offline prototype (/mnt/c/dev/canalpath).
    //   Delaunay (Bowyer-Watson) over location centers -> Voronoi edges (medial axis between protection
    //   domes = max-clearance corridors) -> clearance-weighted graph -> Dijkstra from each coast seed to
    //   the deepest-inland reachable CLEAR node -> Chaikin smooth.
    // Pure geometry: no Unity generation deps, no game state. Deterministic given the site list + seeds.
    internal static class CanalRouter
    {
        public struct Site { public double x, z, r; public Site(double a, double b, double c) { x = a; z = b; r = c; } }
        struct V { public double x, z; public V(double a, double b) { x = a; z = b; } }

        public class Params
        {
            public double ClearR0   = 40.0; // SOFT: prefer >= this much clearance
            public double Beta      = 6.0;  // how hard to prefer high-clearance edges
            public double BoatClear = 10.0; // HARD floor: drop edges whose narrowest clearance < this
            public double InlandGoal = 600; // how far inland each seed aims (world units)
            public double MinProgress = 80; // require at least this much inland progress to accept a terminal
            public int    Smooth    = 3;    // Chaikin iterations
        }

        // Route every seed. Returns one polyline per seed (null if it couldn't reach a clear inland node).
        public static List<List<Vector2>> Route(List<Site> sites, List<(Vector2 start, Vector2 dir)> seeds, Params pr,
                                                out int reached, out int terminated)
        {
            reached = 0; terminated = 0;
            var outPolys = new List<List<Vector2>>();
            if (sites.Count < 3) { foreach (var _ in seeds) outPolys.Add(null); terminated = seeds.Count; return outPolys; }

            var pts = sites.Select(s => new V(s.x, s.z)).ToList();
            var tris = Delaunay(pts);
            var (vverts, vedges) = Voronoi(tris, pts);

            double Clearance(V p)
            {
                double best = double.MaxValue;
                for (int i = 0; i < sites.Count; i++)
                { var s = sites[i]; double d = Math.Sqrt((p.x - s.x) * (p.x - s.x) + (p.z - s.z) * (p.z - s.z)) - s.r; if (d < best) best = d; }
                return best;
            }
            double EdgeCost(V a, V b)
            {
                double len = Dist(a, b); if (len < 1e-6) return 0;
                int ns = Math.Max(2, (int)(len / 15)); double pen = 0;
                for (int i = 0; i <= ns; i++)
                {
                    double t = (double)i / ns; var m = new V(a.x + (b.x - a.x) * t, a.z + (b.z - a.z) * t);
                    double clr = Clearance(m);
                    if (clr < pr.BoatClear) return len * 1e4;                  // boat can't fit — blocked
                    if (clr < pr.ClearR0) pen += (pr.ClearR0 - clr) / pr.ClearR0;
                }
                pen /= (ns + 1);
                return len * (1 + pr.Beta * pen);
            }

            foreach (var (start, dir) in seeds)
            {
                var poly = RouteOne(vverts, vedges, new V(start.x, start.y), new V(dir.x, dir.y), pr, Clearance, EdgeCost);
                if (poly == null) { terminated++; outPolys.Add(null); continue; }
                reached++;
                outPolys.Add(Chaikin(poly, pr.Smooth).Select(v => new Vector2((float)v.x, (float)v.z)).ToList());
            }
            return outPolys;
        }

        static List<V> RouteOne(List<V> vverts, List<(int, int)> vedges, V start, V dir, Params pr,
                                Func<V, double> Clearance, Func<V, V, double> EdgeCost)
        {
            var nodes = new List<V>(vverts);
            int si = nodes.Count; nodes.Add(start);
            var adj = new Dictionary<int, List<(int, double)>>();
            void AddEdge(int a, int b) { double len = Dist(nodes[a], nodes[b]); double c = EdgeCost(nodes[a], nodes[b]); if (c > len * 100) return; Link(adj, a, b, c); Link(adj, b, a, c); }
            foreach (var (a, b) in vedges) AddEdge(a, b);
            foreach (int t in NearestK(nodes, si, 6)) AddEdge(si, t);

            var (g, came) = Dijkstra(adj, si);
            int best = -1; double bestScore = double.NegativeInfinity;
            for (int n = 0; n < nodes.Count; n++)
            {
                if (n == si || !g.ContainsKey(n)) continue;
                double ox = nodes[n].x - start.x, oz = nodes[n].z - start.z;
                double inland = ox * dir.x + oz * dir.z;
                double lateral = Math.Abs(ox * -dir.z + oz * dir.x);
                if (inland < pr.MinProgress) continue;
                if (Clearance(nodes[n]) < pr.ClearR0 * 0.5) continue;
                double over = Math.Max(0, inland - pr.InlandGoal);
                double score = Math.Min(inland, pr.InlandGoal) - 0.3 * lateral - 0.6 * over - 0.003 * g[n];
                if (score > bestScore) { bestScore = score; best = n; }
            }
            if (best < 0) return null;
            var path = new List<int> { best }; int c2 = best; while (came.ContainsKey(c2)) { c2 = came[c2]; path.Add(c2); }
            path.Reverse();
            return path.Select(i => nodes[i]).ToList();
        }

        // ---- Bowyer-Watson Delaunay ----
        static List<int[]> Delaunay(List<V> pts)
        {
            var p = new List<V>(pts);
            double minX = p.Min(v => v.x), minZ = p.Min(v => v.z), maxX = p.Max(v => v.x), maxZ = p.Max(v => v.z);
            double dmax = Math.Max(maxX - minX, maxZ - minZ) * 10 + 100, cx = (minX + maxX) / 2, cz = (minZ + maxZ) / 2;
            int n = p.Count;
            p.Add(new V(cx - dmax, cz - dmax)); p.Add(new V(cx + dmax, cz - dmax)); p.Add(new V(cx, cz + dmax));
            var tris = new List<int[]> { new[] { n, n + 1, n + 2 } };
            for (int i = 0; i < n; i++)
            {
                var bad = new List<int[]>();
                foreach (var t in tris) if (InCircum(p[i], p[t[0]], p[t[1]], p[t[2]])) bad.Add(t);
                var poly = new List<(int a, int b)>();
                foreach (var t in bad)
                    for (int e = 0; e < 3; e++)
                    {
                        int a = t[e], b = t[(e + 1) % 3]; bool shared = false;
                        foreach (var t2 in bad) { if (t2 == t) continue; if (HasEdge(t2, a, b)) { shared = true; break; } }
                        if (!shared) poly.Add((a, b));
                    }
                foreach (var t in bad) tris.Remove(t);
                foreach (var (a, b) in poly) tris.Add(new[] { a, b, i });
            }
            tris.RemoveAll(t => t[0] >= n || t[1] >= n || t[2] >= n);
            return tris;
        }
        static bool HasEdge(int[] t, int a, int b)
        { for (int e = 0; e < 3; e++) { int x = t[e], y = t[(e + 1) % 3]; if ((x == a && y == b) || (x == b && y == a)) return true; } return false; }
        static bool InCircum(V p, V a, V b, V c)
        {
            double ax = a.x - p.x, ay = a.z - p.z, bx = b.x - p.x, by = b.z - p.z, cx = c.x - p.x, cy = c.z - p.z;
            double d = (ax * ax + ay * ay) * (bx * cy - cx * by) - (bx * bx + by * by) * (ax * cy - cx * ay) + (cx * cx + cy * cy) * (ax * by - bx * ay);
            double orient = (b.x - a.x) * (c.z - a.z) - (b.z - a.z) * (c.x - a.x);
            return orient > 0 ? d > 0 : d < 0;
        }

        // ---- Voronoi edges = circumcenters of Delaunay triangles sharing an edge ----
        static (List<V>, List<(int, int)>) Voronoi(List<int[]> tris, List<V> pts)
        {
            var cc = new List<V>();
            foreach (var t in tris) cc.Add(Circumcenter(pts[t[0]], pts[t[1]], pts[t[2]]));
            var edgeToTri = new Dictionary<(int, int), int>(); var vedges = new List<(int, int)>();
            for (int ti = 0; ti < tris.Count; ti++)
            {
                var t = tris[ti];
                for (int e = 0; e < 3; e++)
                {
                    int a = t[e], b = t[(e + 1) % 3]; var key = a < b ? (a, b) : (b, a);
                    if (edgeToTri.TryGetValue(key, out int tj)) vedges.Add((ti, tj)); else edgeToTri[key] = ti;
                }
            }
            return (cc, vedges);
        }
        static V Circumcenter(V a, V b, V c)
        {
            double d = 2 * (a.x * (b.z - c.z) + b.x * (c.z - a.z) + c.x * (a.z - b.z));
            if (Math.Abs(d) < 1e-9) return new V((a.x + b.x + c.x) / 3, (a.z + b.z + c.z) / 3);
            double a2 = a.x * a.x + a.z * a.z, b2 = b.x * b.x + b.z * b.z, c2 = c.x * c.x + c.z * c.z;
            return new V((a2 * (b.z - c.z) + b2 * (c.z - a.z) + c2 * (a.z - b.z)) / d,
                         (a2 * (c.x - b.x) + b2 * (a.x - c.x) + c2 * (b.x - a.x)) / d);
        }

        // ---- graph + Dijkstra + smoothing ----
        static void Link(Dictionary<int, List<(int, double)>> adj, int a, int b, double c)
        { if (!adj.TryGetValue(a, out var l)) { l = new(); adj[a] = l; } l.Add((b, c)); }
        static IEnumerable<int> NearestK(List<V> nodes, int from, int k)
            => Enumerable.Range(0, nodes.Count).Where(i => i != from).OrderBy(i => Dist(nodes[from], nodes[i])).Take(k);
        static (Dictionary<int, double>, Dictionary<int, int>) Dijkstra(Dictionary<int, List<(int to, double cost)>> adj, int s)
        {
            var g = new Dictionary<int, double> { [s] = 0 }; var came = new Dictionary<int, int>();
            var pq = new SortedSet<(double d, int n)> { (0, s) };
            while (pq.Count > 0)
            {
                var cur = pq.Min; pq.Remove(cur);
                if (!adj.TryGetValue(cur.n, out var nbrs)) continue;
                foreach (var (to, cost) in nbrs)
                {
                    double nd = cur.d + cost;
                    if (!g.TryGetValue(to, out double old) || nd < old) { if (g.ContainsKey(to)) pq.Remove((old, to)); g[to] = nd; came[to] = cur.n; pq.Add((nd, to)); }
                }
            }
            return (g, came);
        }
        static List<V> Chaikin(List<V> pts, int iters)
        {
            var p = pts;
            for (int it = 0; it < iters; it++)
            {
                if (p.Count < 3) break;
                var q = new List<V> { p[0] };
                for (int i = 0; i < p.Count - 1; i++)
                {
                    var a = p[i]; var b = p[i + 1];
                    q.Add(new V(0.75 * a.x + 0.25 * b.x, 0.75 * a.z + 0.25 * b.z));
                    q.Add(new V(0.25 * a.x + 0.75 * b.x, 0.25 * a.z + 0.75 * b.z));
                }
                q.Add(p[p.Count - 1]); p = q;
            }
            return p;
        }
        static double Dist(V a, V b) => Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.z - b.z) * (a.z - b.z));
    }
}
