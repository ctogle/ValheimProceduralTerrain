using HarmonyLib;
using UnityEngine;

namespace ProceduralTerrain
{
    // DEBUG inspection aid: when _hideMist is set (cfg [Debug] hideMist=true), globally suppress the Mistlands
    // ground mist so the terrain/sewers are visible. The dense Mistlands mist is ParticleMist (a singleton with a
    // private ParticleSystem m_ps) — NOT the per-point MistEmitter. ParticleMist.Update emits into m_ps each frame
    // unless m_ps.emission is disabled. We skip Update and clear m_ps while hiding; fully reversible (flip the cfg
    // back and emission resumes next frame). Live-only, nothing baked.
    [HarmonyPatch(typeof(ParticleMist), "Update")]
    static class ParticleMistUpdatePatch
    {
        static bool Prefix(ParticleSystem ___m_ps)
        {
            if (!Plugin._hideMist) return true;          // normal behaviour
            if (___m_ps != null) ___m_ps.Clear(true);    // wipe any live particles
            return false;                                // skip emission while hiding
        }
    }
}
