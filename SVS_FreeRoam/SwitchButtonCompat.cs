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
            _wanted = npc;
            try { _switch.Invoke(null, null); }
            finally { _wanted = null; }
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
