using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ProceduralTerrain
{
    // Code-built "dust motes" decoration: a drifting glowing-speck ParticleSystem + a faint point light, for subtle
    // atmosphere/light inside the strata galleries. No asset bundle — the mesh/material/texture are generated at
    // runtime. Registered into ZNetScene as "PT_DustMotes" so a normal [Placement] rule can scatter it.
    // Visual-only (no ZNetView) → client/host-side, re-spawned per zone load (ambient, not persisted). Cheap: one
    // low-rate emitter per placement; keep placements sparse (the point light is the only real cost).
    internal static class CaveMotes
    {
        internal const string Name = "PT_DustMotes";
        internal static readonly int Hash = Name.GetStableHashCode();
        static GameObject? _prefab;
        static Texture2D? _dot;
        static Material? _mat;

        internal static GameObject GetOrBuild()
        {
            if (_prefab != null) return _prefab;
            var go = new GameObject(Name);
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.SetActive(false);   // template stays inactive; SpawnCave activates the clone

            // DIAGNOSTIC: no point light (at 80% coverage hundreds of dynamic lights would tank fps; the big red
            // particles are enough to confirm placement). Re-add a faint light for the real "subtle source" version.

            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.loop = true; main.prewarm = true;
            main.startLifetime = 12f;
            main.startSpeed = 0.04f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.4f, 0.8f);   // DIAGNOSTIC: large
            main.startColor = new Color(1f, 0.05f, 0.05f, 0.9f);           // DIAGNOSTIC: red
            main.maxParticles = 40;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var em = ps.emission; em.rateOverTime = 4f;
            var sh = ps.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(8f, 4f, 8f);
            var vel = ps.velocityOverLifetime; vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.y = new ParticleSystem.MinMaxCurve(0.03f);
            var noise = ps.noise; noise.enabled = true; noise.strength = 0.08f; noise.frequency = 0.2f;

            var rnd = go.GetComponent<ParticleSystemRenderer>();
            rnd.renderMode = ParticleSystemRenderMode.Billboard;
            rnd.material = MoteMaterial();
            rnd.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rnd.receiveShadows = false;

            _prefab = go;
            return go;
        }

        static Material MoteMaterial()
        {
            if (_mat != null) return _mat;
            var shader = Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Sprites/Default");
            var m = new Material(shader) { mainTexture = DotTexture() };
            if (m.HasProperty("_Mode")) m.SetFloat("_Mode", 4f);           // additive (Standard Unlit)
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);   // neutral tint → particle startColor shows true
            _mat = m; return m;
        }

        static Texture2D DotTexture()
        {
            if (_dot != null) return _dot;
            const int S = 16;
            var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float dx = (x - S / 2f + 0.5f) / (S / 2f), dy = (y - S / 2f + 0.5f) / (S / 2f);
                float r = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                t.SetPixel(x, y, new Color(1f, 1f, 1f, r * r));            // soft radial dot
            }
            t.Apply(); _dot = t; return t;
        }
    }

    // Register the runtime mote prefab into ZNetScene so GetPrefab("PT_DustMotes") resolves for placement.
    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    static class MotePrefabRegister
    {
        static void Postfix(ZNetScene __instance)
        {
            var p = CaveMotes.GetOrBuild();
            if (__instance.m_prefabs != null && !__instance.m_prefabs.Contains(p)) __instance.m_prefabs.Add(p);
            var f = typeof(ZNetScene).GetField("m_namedPrefabs", BindingFlags.NonPublic | BindingFlags.Instance);
            if (f?.GetValue(__instance) is System.Collections.IDictionary dict && !dict.Contains(CaveMotes.Hash))
                dict[CaveMotes.Hash] = p;
        }
    }
}
