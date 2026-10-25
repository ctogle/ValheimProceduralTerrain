using HarmonyLib;
using UnityEngine;

namespace ProceduralTerrain
{
    // World version guard. A world generated with PT installed gets a version tag (a custom global key, which
    // persists in the world .db). On loading an EXISTING world whose tag is absent or mismatched, we warn and
    // make the user confirm — declining logs out WITHOUT saving, so the on-disk world is never overwritten with
    // scrambled terrain. This protects a vanilla (or older-PT) world from being wrecked by loading it with PT on.
    //
    // The tag is a key=value global key: "pt_ver <version>" → readable via GetGlobalKey("pt_ver", out ver).
    // ZNet.LoadWorld returns true for an existing world (ZoneSystem.Load ran → global keys populated) and false
    // when the save .db is missing (brand-new world). That __result is the authoritative new-vs-existing signal,
    // so we never mistake an existing untagged world for a fresh one (which would silently tag + wreck it).
    internal static class WorldVersionGuard
    {
        const string Key = "pt_ver";

        enum Action { None, Stamp, Prompt }
        static volatile Action _pending = Action.None;
        static string _worldVer = "";          // the mismatched world's tag (empty = untagged)
        static bool _promptShown;              // guard against pushing the popup twice while it's open

        // ZNet.LoadWorld postfix (main thread). existing = __result: true → save loaded, false → brand-new world.
        internal static void OnWorldLoaded(bool existing)
        {
            _promptShown = false;
            if (!existing) { _pending = Action.Stamp; return; }   // fresh world → tag it

            var zs = ZoneSystem.instance;
            string ver = "";
            if (zs != null) zs.GetGlobalKey(Key, out ver);
            if (ver == Plugin.Version) { _pending = Action.None; return; }   // tag matches installed → fine

            _worldVer = ver ?? "";
            _pending = Action.Prompt;   // absent or mismatched → confirm before the user can wreck it
        }

        static void Stamp()
        {
            var zs = ZoneSystem.instance;
            if (zs == null) return;
            zs.SetGlobalKey($"{Key} {Plugin.Version}");
            Plugin.Log.LogInfo($"[PT-guard] tagged world with PT v{Plugin.Version}");
            _pending = Action.None;
        }

        // Driven on the main thread (WorldGuardDriver.Update) so the popup + Logout run on Unity's thread.
        internal static void Tick()
        {
            switch (_pending)
            {
                case Action.None: return;
                case Action.Stamp: Stamp(); return;
                case Action.Prompt:
                    if (_promptShown) return;
                    if (ZoneSystem.instance == null) return;
                    if (!UnifiedPopup.IsAvailable())   // GUI not up yet (or headless/dedicated — no way to prompt)
                    {
                        if (ZNet.instance != null && ZNet.instance.IsDedicated())
                        {
                            // no interactive user to confirm — don't silently convert; log loudly and stamp so a
                            // deliberate server setup proceeds, but leave a trail.
                            Plugin.Log.LogWarning($"[PT-guard] DEDICATED: world tag '{_worldVer}' != installed v{Plugin.Version} and no popup available — proceeding and re-stamping. Verify this is intended.");
                            Stamp();
                        }
                        return;   // client GUI not ready yet → try again next frame
                    }
                    _promptShown = true;
                    string installed = Plugin.Version;
                    string text = string.IsNullOrEmpty(_worldVer)
                        ? $"This world has no Procedural Terrain data (installed v{installed}).\nContinuing will permanently alter its terrain — do NOT do this to an existing vanilla world.\n\nAre you sure?"
                        : $"Procedural Terrain version mismatch with world version (world v{_worldVer}, installed v{installed}).\nStructures are at risk; are you sure?";
                    _pending = Action.None;
                    UnifiedPopup.Push(new YesNoPopup("Procedural Terrain", text,
                        yesCallback: () => { UnifiedPopup.Pop(); Stamp(); },                      // consent → re-tag + play
                        noCallback:  () => { UnifiedPopup.Pop(); Game.instance?.Logout(save: false, changeToStartScene: true); },  // back to menu, DO NOT save → disk world untouched
                        localizeText: false));
                    return;
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), "LoadWorld")]
    static class ZNetLoadWorldGuard
    {
        static void Postfix(bool __result) => WorldVersionGuard.OnWorldLoaded(__result);
    }

    // Main-thread pump for the guard (popup/Logout must run on Unity's thread).
    internal class WorldGuardDriver : MonoBehaviour
    {
        void Update() => WorldVersionGuard.Tick();
    }
}
