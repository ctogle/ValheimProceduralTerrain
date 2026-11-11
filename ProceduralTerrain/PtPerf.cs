using System.Threading;
using System.Diagnostics;

namespace ProceduralTerrain
{
    // Frame-level hitch attribution. Plugin.Update() runs every frame = the DOMINATOR node for a main-thread
    // stall, so its span necessarily includes everything on the frame. When a frame exceeds budget it prints a
    // breakdown so we can tell whether the hitch is PT main-thread serve (gen/veg/cave), GC from prefetch churn,
    // or neither of ours (Voxheim mesh / Valheim). A low PTmain on a hitch frame EXONERATES PT.
    internal static class PtPerf
    {
        // Main-thread, frame-local accumulators (ms). Reset each frame by the watchdog.
        // Generate is split: carve = bake-decode/live-carve (prefetch CAN move this off-thread);
        // set = Field.Set blit (ALWAYS on main thread, even on a prefetched cache hit — prefetch can't help it);
        // gw = groundwork overlay.
        internal static double CarveMs, GwMs, SetMs, VegMs, CaveMs;
        // Zones carved by prefetch WORKERS this frame (any thread) — correlate hitches with prefetch bursts.
        internal static int PrefetchCarves;
        // prefix→postfix span handle for PlaceVegetation (main thread only).
        internal static long VegT0;

        internal static void PrefetchCarve() => Interlocked.Increment(ref PrefetchCarves);
        internal static double MsSince(long t0) => (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        internal static void ResetFrame() { CarveMs = 0; GwMs = 0; SetMs = 0; VegMs = 0; CaveMs = 0; PrefetchCarves = 0; }
    }
}
