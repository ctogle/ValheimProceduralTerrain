using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

// ─────────────────────────────────────────────────────────────────────────────
//  Bake-time statistics — a cheap, no-risk pile of info written alongside each bake as a sidecar JSON
//  (procedural_terrain_bake_<seed>_<hash>.json, same path as the .bin with the extension swapped).
//
//  Nothing here touches the .bin format or the serve path. It records per-zone carve times (for distribution /
//  tail / multimodality analysis offline), machine load (GC/CPU/RAM), and subsystem counters (pocket fill,
//  fixpoint recompute). Written on bake completion AND on a cancelled partial bake (the finalize path still runs).
// ─────────────────────────────────────────────────────────────────────────────
namespace ProceduralTerrain
{
    internal static class BakeStats
    {
        // Per-zone samples, preallocated to the carve count; filled by the bake workers (each at a unique index).
        static int[] _ox, _oz; static float[] _ms;
        static int _cap;

        // Start-of-bake snapshots for deltas.
        static int _gc0, _gc1, _gc2;
        static TimeSpan _cpuStart;
        static long _memStart;
        static long _peakWorkingSet;

        // Call once the carve list is known, before the parallel carve begins.
        internal static void Begin(int zoneCapacity)
        {
            _cap = Math.Max(0, zoneCapacity);
            _ox = new int[_cap]; _oz = new int[_cap]; _ms = new float[_cap];
            try
            {
                _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
                var p = Process.GetCurrentProcess();
                _cpuStart = p.TotalProcessorTime;
                _memStart = GC.GetTotalMemory(false);
                _peakWorkingSet = p.WorkingSet64;
            }
            catch { /* best-effort — never let stats kill a bake */ }
            // reset subsystem counters so the numbers are per-bake
            Plugin._pocketZonesTotal = 0; Plugin._pocketFillZones = 0; Plugin._pocketVoxels = 0;
            Plugin._fixpointHitCapCalls = 0; Plugin._fixpointNeighborsComputed = 0;
        }

        // Record one carved zone at its completion slot (slot = the atomic done-count − 1, unique per worker).
        internal static void Record(int slot, int ox, int oz, float ms)
        {
            if (_ms == null || slot < 0 || slot >= _cap) return;
            _ox[slot] = ox; _oz[slot] = oz; _ms[slot] = ms;
            long ws = 0; try { ws = Process.GetCurrentProcess().WorkingSet64; } catch { }
            if (ws > _peakWorkingSet) _peakWorkingSet = ws;
        }

        // Write the sidecar next to the .bin. binPath is the final promoted bake path.
        internal static void Write(string binPath, int seed, uint cfgHash, int zonesCarved, int zonesPreserved,
                                   double elapsedSec, int threads, int coarseXZ, int coarseY, float radius,
                                   float centerX, float centerZ, bool crossings, bool cancelled)
        {
            try
            {
                if (_ms == null) return;
                int n = Math.Min(zonesCarved, _cap);

                // Distribution over per-zone carve times.
                var times = new float[n];
                Array.Copy(_ms, times, n);
                Array.Sort(times);
                float min = n > 0 ? times[0] : 0f, max = n > 0 ? times[n - 1] : 0f;
                float sum = 0f; for (int i = 0; i < n; i++) sum += times[i];
                float mean = n > 0 ? sum / n : 0f;
                float P(float q) => n > 0 ? times[Math.Min(n - 1, Math.Max(0, (int)Math.Round(q * (n - 1))))] : 0f;

                // Fixed log-ish histogram (ms bins) — enough to see tails / multimodality at a glance.
                float[] edges = { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000 };
                int[] bins = new int[edges.Length + 1];
                for (int i = 0; i < n; i++)
                {
                    float t = times[i]; int b = edges.Length;
                    for (int e = 0; e < edges.Length; e++) { if (t < edges[e]) { b = e; break; } }
                    bins[b]++;
                }

                int gc0 = 0, gc1 = 0, gc2 = 0; double cpuSec = 0;
                try
                {
                    gc0 = GC.CollectionCount(0) - _gc0; gc1 = GC.CollectionCount(1) - _gc1; gc2 = GC.CollectionCount(2) - _gc2;
                    cpuSec = (Process.GetCurrentProcess().TotalProcessorTime - _cpuStart).TotalSeconds;
                }
                catch { }
                double cpuUtil = elapsedSec > 0 ? cpuSec / (elapsedSec * Math.Max(1, Environment.ProcessorCount)) : 0;

                var sb = new StringBuilder(1 << 16);
                sb.Append("{\n");
                Kv(sb, "ptVersion", Plugin.Version); Comma(sb);
                Kv(sb, "utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)); Comma(sb);
                KvHex(sb, "seed", (uint)seed); Comma(sb);
                KvHex(sb, "cfgHash", cfgHash); Comma(sb);
                Kv(sb, "cancelled", cancelled); Comma(sb);
                Kv(sb, "zonesCarved", n); Comma(sb);
                Kv(sb, "zonesPreserved", zonesPreserved); Comma(sb);
                Kv(sb, "elapsedSec", Math.Round(elapsedSec, 2)); Comma(sb);
                Kv(sb, "zonesPerSec", Math.Round(elapsedSec > 0 ? n / elapsedSec : 0, 3)); Comma(sb);
                Kv(sb, "threads", threads); Comma(sb);
                Kv(sb, "coarseXZ", coarseXZ); Comma(sb);
                Kv(sb, "coarseY", coarseY); Comma(sb);
                Kv(sb, "radius", Math.Round(radius, 1)); Comma(sb);
                Kv(sb, "centerX", Math.Round(centerX, 1)); Comma(sb);
                Kv(sb, "centerZ", Math.Round(centerZ, 1)); Comma(sb);
                Kv(sb, "crossings", crossings); Comma(sb);

                sb.Append("  \"zoneMs\": {\n");
                Kv(sb, "min", Math.Round(min, 2), 4); Comma(sb);
                Kv(sb, "mean", Math.Round(mean, 2), 4); Comma(sb);
                Kv(sb, "p50", Math.Round(P(0.50f), 2), 4); Comma(sb);
                Kv(sb, "p90", Math.Round(P(0.90f), 2), 4); Comma(sb);
                Kv(sb, "p99", Math.Round(P(0.99f), 2), 4); Comma(sb);
                Kv(sb, "max", Math.Round(max, 2), 4); Comma(sb);
                sb.Append("    \"histEdgesMs\": "); Arr(sb, edges); Comma(sb);
                sb.Append("    \"histCounts\": "); Arr(sb, bins); sb.Append("\n  },\n");

                sb.Append("  \"subsystems\": {\n");
                Kv(sb, "pocketZonesTotal", Plugin._pocketZonesTotal, 4); Comma(sb);
                Kv(sb, "pocketFillZones", Plugin._pocketFillZones, 4); Comma(sb);
                Kv(sb, "pocketVoxels", Plugin._pocketVoxels, 4); Comma(sb);
                Kv(sb, "fixpointHitCapCalls", Plugin._fixpointHitCapCalls, 4); Comma(sb);
                Kv(sb, "fixpointNeighborsComputed", Plugin._fixpointNeighborsComputed, 4); sb.Append("\n  },\n");

                sb.Append("  \"machine\": {\n");
                Kv(sb, "cores", Environment.ProcessorCount, 4); Comma(sb);
                Kv(sb, "cpuSec", Math.Round(cpuSec, 1), 4); Comma(sb);
                Kv(sb, "cpuUtil", Math.Round(cpuUtil, 3), 4); Comma(sb);
                Kv(sb, "gc0", gc0, 4); Comma(sb);
                Kv(sb, "gc1", gc1, 4); Comma(sb);
                Kv(sb, "gc2", gc2, 4); Comma(sb);
                Kv(sb, "peakWorkingSetMB", Math.Round(_peakWorkingSet / (1024.0 * 1024.0), 1), 4); sb.Append("\n  },\n");

                // Raw per-zone triples last (the big pile): [ox, oz, ms] × n. Cheap (~230KB at 19k zones).
                sb.Append("  \"zones\": [");
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('[').Append(_ox[i]).Append(',').Append(_oz[i]).Append(',')
                      .Append(_ms[i].ToString("0.##", CultureInfo.InvariantCulture)).Append(']');
                }
                sb.Append("]\n}\n");

                string jsonPath = Path.ChangeExtension(binPath, ".json");
                File.WriteAllText(jsonPath, sb.ToString());
                Plugin.Log.LogInfo($"[ProceduralTerrain] bake stats → {jsonPath} ({n} zones, {Math.Round(elapsedSec,1)}s, p99={Math.Round(P(0.99f),1)}ms, cpuUtil={Math.Round(cpuUtil,2)})");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[ProceduralTerrain] bake stats write failed (ignored): {e.Message}"); }
            finally { _ox = null; _oz = null; _ms = null; _cap = 0; }
        }

        // ── tiny JSON helpers (hand-rolled → no System.Text.Json dependency) ──
        static void Comma(StringBuilder sb) => sb.Append(",\n");
        static void Kv(StringBuilder sb, string k, string v, int indent = 2) => sb.Append(new string(' ', indent)).Append('"').Append(k).Append("\": \"").Append(v).Append('"');
        static void Kv(StringBuilder sb, string k, bool v, int indent = 2) => sb.Append(new string(' ', indent)).Append('"').Append(k).Append("\": ").Append(v ? "true" : "false");
        static void Kv(StringBuilder sb, string k, long v, int indent = 2) => sb.Append(new string(' ', indent)).Append('"').Append(k).Append("\": ").Append(v.ToString(CultureInfo.InvariantCulture));
        static void Kv(StringBuilder sb, string k, double v, int indent = 2) => sb.Append(new string(' ', indent)).Append('"').Append(k).Append("\": ").Append(v.ToString(CultureInfo.InvariantCulture));
        static void KvHex(StringBuilder sb, string k, uint v, int indent = 2) => sb.Append(new string(' ', indent)).Append('"').Append(k).Append("\": \"").Append(v.ToString("X8")).Append('"');
        static void Arr(StringBuilder sb, float[] a) { sb.Append('['); for (int i = 0; i < a.Length; i++) { if (i > 0) sb.Append(','); sb.Append(a[i].ToString("0.##", CultureInfo.InvariantCulture)); } sb.Append(']'); }
        static void Arr(StringBuilder sb, int[] a) { sb.Append('['); for (int i = 0; i < a.Length; i++) { if (i > 0) sb.Append(','); sb.Append(a[i]); } sb.Append(']'); }
    }
}
