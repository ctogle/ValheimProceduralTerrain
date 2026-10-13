using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProceduralTerrain
{
    // Reusable in-game debug overlay for the brush tiers. Capture world-space polylines during Carve
    // (seed ticks, tunnel chains, later spine seeds), rendered as thick, SEE-THROUGH LineRenderers so
    // underground features are visible through the mountain. Storage is zone-keyed: re-carving a zone
    // replaces its primitives (no duplicate buildup as you move around). Capture is thread-safe; the
    // actual GameObject build happens on the main thread in DebugVizRenderer.Update.
    internal static class DebugViz
    {
        internal static bool Enabled = false;
        internal static bool Occlude = false;   // true = terrain mesh hides lines behind it (ZTest LessEqual); false = draw through everything (Always)
        internal static float Radius = 40f;     // only polys whose zone is within this many metres of the local player are shown (0 = no cull)

        internal struct Poly { public Vector3[] pts; public Color col; public float width; }

        static readonly object _lk = new object();
        static readonly Dictionary<(int, int), List<Poly>> _zones = new Dictionary<(int, int), List<Poly>>();
        static readonly Dictionary<(int, int), int> _zoneVer = new Dictionary<(int, int), int>();
        [System.ThreadStatic] static (int, int)? _cur;

        // Start (or restart) capture for a zone — clears whatever it held before.
        internal static void BeginZone(int ox, int oz)
        {
            var key = (ox, oz);
            lock (_lk)
            {
                _zones[key] = new List<Poly>(64);
                _zoneVer.TryGetValue(key, out int v);
                _zoneVer[key] = v + 1;
                _cur = key;
            }
        }

        // Set the current thread's zone WITHOUT clearing it — used by a second+ surface_blobs mode in the
        // same zone so it appends to (rather than wipes) the first mode's lines. Ensures the zone list exists.
        internal static void SetZone(int ox, int oz)
        {
            var key = (ox, oz);
            lock (_lk)
            {
                if (!_zones.ContainsKey(key)) { _zones[key] = new List<Poly>(64); _zoneVer[key] = (_zoneVer.TryGetValue(key, out int v) ? v : 0) + 1; }
                _cur = key;
            }
        }

        // Add a polyline to the current thread's zone. Colors chosen at the call site per tier/tag.
        internal static void Line(IReadOnlyList<Vector3> pts, Color col, float width)
        {
            if (pts == null || pts.Count < 2) return;
            var arr = new Vector3[pts.Count];
            for (int i = 0; i < pts.Count; i++) arr[i] = pts[i];
            lock (_lk)
            {
                if (_cur is (int, int) k && _zones.TryGetValue(k, out var lst))
                    lst.Add(new Poly { pts = arr, col = col, width = width });
            }
        }

        // Add a polyline to an EXPLICIT zone (no thread-static _cur) — used by vegetation-conform viz, which
        // runs during PlaceVegetation, outside the Carve capture scope. Ensures the zone list + bumps version.
        internal static void AddToZone(int ox, int oz, IReadOnlyList<Vector3> pts, Color col, float width)
        {
            if (pts == null || pts.Count < 2) return;
            var arr = new Vector3[pts.Count];
            for (int i = 0; i < pts.Count; i++) arr[i] = pts[i];
            var key = (ox, oz);
            lock (_lk)
            {
                if (!_zones.TryGetValue(key, out var lst)) { lst = new List<Poly>(64); _zones[key] = lst; }
                lst.Add(new Poly { pts = arr, col = col, width = width });
                _zoneVer[key] = (_zoneVer.TryGetValue(key, out int v) ? v : 0) + 1;
            }
        }

        // A point marker = a short vertical tick (uniform LineRenderer path, thick + see-through).
        internal static void Point(Vector3 p, Color col, float size, float width)
        {
            Line(new[] { p + Vector3.up * size, p - Vector3.up * size }, col, width);
        }

        internal static void ClearAll()
        {
            lock (_lk)
            {
                var keys = new List<(int, int)>(_zones.Keys);
                foreach (var k in keys) { _zones[k] = new List<Poly>(); _zoneVer[k] = (_zoneVer.TryGetValue(k, out int v) ? v : 0) + 1; }
            }
        }

        // Snapshot zones whose version changed since the renderer last built them.
        internal static List<((int, int) key, int ver, List<Poly> polys)> SnapshotChanged(Dictionary<(int, int), int> built)
        {
            var outp = new List<((int, int), int, List<Poly>)>();
            lock (_lk)
            {
                foreach (var kv in _zoneVer)
                {
                    built.TryGetValue(kv.Key, out int b);
                    if (b == kv.Value) continue;
                    var copy = _zones.TryGetValue(kv.Key, out var l) ? new List<Poly>(l) : new List<Poly>();
                    outp.Add((kv.Key, kv.Value, copy));
                }
            }
            return outp;
        }
    }

    internal class DebugVizRenderer : MonoBehaviour
    {
        static Material _mat;
        readonly Dictionary<(int, int), GameObject> _go = new Dictionary<(int, int), GameObject>();
        readonly Dictionary<(int, int), int> _built = new Dictionary<(int, int), int>();

        static Material Mat()
        {
            if (_mat != null) return _mat;
            var sh = Shader.Find("Hidden/Internal-Colored");
            _mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            _mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _mat.SetInt("_Cull", (int)CullMode.Off);
            _mat.SetInt("_ZWrite", 0);
            _mat.SetInt("_ZTest", (int)CompareFunction.Always);   // draw through terrain
            _mat.renderQueue = 5000;
            return _mat;
        }

        void Update()
        {
            if (!DebugViz.Enabled) return;
            // Terrain occlusion toggle: LessEqual = hidden behind terrain (filters the clutter), Always = see-through.
            Mat().SetInt("_ZTest", (int)(DebugViz.Occlude ? CompareFunction.LessEqual : CompareFunction.Always));
            var changed = DebugViz.SnapshotChanged(_built);
            foreach (var (key, ver, polys) in changed)
            {
                if (_go.TryGetValue(key, out var old) && old != null) Destroy(old);
                var root = new GameObject($"PTViz_{key.Item1}_{key.Item2}");
                root.transform.position = Vector3.zero;
                int idx = 0;
                foreach (var p in polys)
                {
                    var lrGo = new GameObject($"l{idx++}");
                    lrGo.transform.SetParent(root.transform, false);
                    var lr = lrGo.AddComponent<LineRenderer>();
                    lr.material = Mat();
                    lr.useWorldSpace = true;
                    lr.widthMultiplier = p.width;
                    lr.numCapVertices = 2;
                    lr.startColor = lr.endColor = p.col;
                    lr.positionCount = p.pts.Length;
                    lr.SetPositions(p.pts);
                    lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    lr.receiveShadows = false;
                }
                _go[key] = root;
                _built[key] = ver;
            }

            // Per-frame radius cull: show only lines within Radius of the CAMERA, so the overlay is a small
            // bubble around what you're looking at rather than a wall of lines. Per-line (each tree), not per-zone.
            if (DebugViz.Radius > 0f)
            {
                var cam = Utils.GetMainCamera();
                if (cam != null)
                {
                    Vector3 cp = cam.transform.position;
                    float r2 = DebugViz.Radius * DebugViz.Radius;
                    foreach (var kv in _go)
                    {
                        if (kv.Value == null) continue;
                        var t = kv.Value.transform;
                        int n = t.childCount;
                        for (int i = 0; i < n; i++)
                        {
                            var child = t.GetChild(i);
                            var lr = child.GetComponent<LineRenderer>();
                            if (lr == null || lr.positionCount == 0) continue;
                            bool near = (lr.GetPosition(0) - cp).sqrMagnitude <= r2;
                            if (child.gameObject.activeSelf != near) child.gameObject.SetActive(near);
                        }
                    }
                }
            }
        }
    }
}
