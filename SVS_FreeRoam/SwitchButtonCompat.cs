using System;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Manager;
using SV;

namespace SVS_FreeRoam
{
    /// <summary>
    /// SVS_CustomGameBalance puts a btn_Switch on the game's character panel
    /// (GameCanvas/CharaInfo) that makes you play as another character. It switches to
    /// whoever is *marked* -- the character with the ring and its particles on
    /// (CustomGameFunctions.SwitchPCCharacter) -- while in third person the panel shows whoever
    /// is *aimed at*. So pressing it in third person switched to nobody, or to someone else,
    /// unless the two happened to agree.
    ///
    /// A prefix on that method marks the aimed character first, in third person only. It is
    /// applied once the plugins have all loaded, and only if that one is installed.
    /// </summary>
    internal static class SwitchButtonCompat
    {
        private static bool _tried;
        private static System.Reflection.MethodInfo _switch;
        private static SV.Chara.AI _wanted;

        /// <summary>Whether SVS_CustomGameBalance's character switch is installed.</summary>
        internal static bool Available => _switch != null;

        /// <summary>Switches the player to this character, with SVS_CustomGameBalance's own switch.</summary>
        internal static void SwitchTo(SV.Chara.AI npc)
        {
            if (_switch == null || npc == null) return;

            // The switch goes to whoever carries the game's marking rings, and BeforeSwitch
            // lights them on the wanted character (and puts out everyone else's). The plugin
            // itself only moves the plain ring from the old player to the new, so afterwards
            // every particle ring goes back to how it was: they were lit to say who, not to stay.
            var rings = new System.Collections.Generic.List<UnityEngine.GameObject>();
            var lit = new System.Collections.Generic.List<bool>();
            foreach (var ai in Game.AICharas)
            {
                var circles = ai?.particleCircles;
                if (circles == null) continue;
                for (int i = 0; i < circles.Count; i++)
                {
                    if (circles[i] == null) continue;
                    rings.Add(circles[i].gameObject);
                    lit.Add(circles[i].gameObject.activeSelf);
                }
            }

            _wanted = npc;
            try { _switch.Invoke(null, null); }
            finally
            {
                _wanted = null;
                for (int i = 0; i < rings.Count; i++)
                    if (rings[i] != null && rings[i].activeSelf != lit[i]) rings[i].SetActive(lit[i]);
            }
        }

        internal static void TryApply()
        {
            if (_tried) return;
            _tried = true;
            try
            {
                Type functions = null;
                foreach (var info in IL2CPPChainloader.Instance.Plugins.Values)
                {
                    var assembly = info.Instance?.GetType().Assembly;
                    if (assembly == null || assembly.GetName().Name != "SVS_CustomGameBalance") continue;
                    functions = assembly.GetType("SVS_CustomGameBalance.CustomGameFunctions");
                    break;
                }
                if (functions == null) return;

                var method = AccessTools.Method(functions, "SwitchPCCharacter");
                if (method == null) return;
                _switch = method;
                new Harmony(Plugin.Guid + ".switch").Patch(method,
                    prefix: new HarmonyMethod(typeof(SwitchButtonCompat), nameof(BeforeSwitch)));
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("Could not adapt SVS_CustomGameBalance's switch button: " + e.Message);
            }
        }

        private static void BeforeSwitch()
        {
            try
            {
                // The one chosen from the character wheel, or in third person the one aimed at.
                var aimed = _wanted ?? (ThirdPersonController.IsPovRunning
                    ? ThirdPersonController.AimedCharacter : null);
                var player = GameChara.PlayerAI;
                if (aimed == null || player == null) return;

                foreach (var ai in Game.AICharas)
                {
                    if (ai == null || ai.Pointer == player.Pointer) continue;
                    bool mark = ai.Pointer == aimed.Pointer;
                    if (ai.objCircle != null) ai.objCircle.active = mark;
                    var circles = ai.particleCircles;
                    if (circles == null) continue;
                    for (int i = 0; i < circles.Count; i++)
                        if (circles[i] != null) circles[i].gameObject.active = mark;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("Could not mark the aimed character for the switch: " + e.Message);
            }
        }
    }
}
