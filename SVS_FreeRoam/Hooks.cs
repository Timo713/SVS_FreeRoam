using System;
using HarmonyLib;
using Pathfinding;
using SV;
using UnityEngine;

namespace SVS_FreeRoam
{
    /// <summary>
    /// Every patch the plugin needs, now that it is one assembly (Force High Poly adds one
    /// of its own in HighPoly.Apply).
    ///
    /// The companion version also had to patch UnityEngine.Input.GetAxis, GetKey and
    /// GetKeyDown, purely to feed different answers into another plugin's code. None of that
    /// survives the merge: forward movement is part of the movement vector, second bindings
    /// are an `or`, and the toggle is read directly.
    /// </summary>
    internal static class Hooks
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SimulationScene), nameof(SimulationScene.Update))]
        private static void SimulationSceneUpdate(SimulationScene __instance)
        {
            try
            {
                // Before the Enable check: characters already upgraded stay upgraded, and
                // must stay on the layer the map camera draws either way.
                HighPoly.Tick();
                Notice.Tick();
                SwitchButtonCompat.TryApply();

                // Junh2x's SVS_3rdPov drives the same camera and player. If it is installed it
                // has been switched off (Plugin.SwitchOffOriginal), and the player is told once.
                // Only if that failed does this plugin stand down instead.
                Plugin.TellAboutOriginal();
                if (Plugin.OriginalInstalled)
                {
                    GamepadUI.Exit();
                    Follower.Stop("the original SVS_3rdPov is installed");
                    ThirdPersonController.Shutdown();
                    return;
                }

                // The Enable switch is live. The patches stay in place either way; turning it
                // off hands everything back once and then skips the pass. The prefixes below
                // need no check of their own: they only act while IsPovRunning, which
                // Shutdown clears.
                if (!Plugin.Enabled.Value)
                {
                    GamepadUI.Exit();
                    Follower.Stop("plugin disabled");
                    ThirdPersonController.Shutdown();
                    return;
                }

                // First, so that while a button is selected, A presses it and nothing else.
                GamepadUI.Tick(Plugin.GamepadSupport.Value && !ThirdPersonController.InH(),
                               ThirdPersonController.InConversation());

                ThirdPersonController.Update(__instance);
                // Not under a menu or in a conversation: those set the focus up themselves.
                PovFocus.Tick(ThirdPersonController.IsPovRunning && !Manager.Scene.IsOverlap &&
                              !ThirdPersonController.IsAnyMenuOpen(), Camera.main, GameChara.PlayerAI);

                // Clicks on the world, after the game's own click handling for the frame: a
                // click it used on a character has already set its target by now.
                var playerAI = GameChara.PlayerAI;
                if (playerAI != null)
                {
                    ClickWalker.Update(playerAI);
                    ClickIdler.Update(__instance, playerAI);
                    Follower.Update(playerAI);
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("Free-roam update failed: " + e);
            }
        }

        /// <summary>
        /// Where a walker's speed can be overridden (SVS_CheatTools uses the same spot). Only
        /// acts on the player's walker, and only while following someone at a matched speed.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(AIBase), nameof(AIBase.FixedUpdate))]
        private static void AIBaseFixedUpdate(AIBase __instance)
        {
            try
            {
                if (__instance != null) Follower.FixedUpdate(__instance);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("Follow speed update failed: " + e);
            }
        }

        /// <summary>
        /// Wheel and middle click select targets in the ordinary overhead view, which makes no
        /// sense while driving a third-person camera. Both were suppressed in the original.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SimulationScene), nameof(SimulationScene.WheelTargetSelect))]
        private static bool WheelTargetSelect()
        {
            if (!ThirdPersonController.IsPovRunning) return true;

            // The original suppressed this outright in third person, which is why cycling was
            // only ever available in the overview camera. It is let through while the location
            // buttons are up, where the cursor is free and the wheel is not zooming.
            return ThirdPersonController.WheelCyclingActive;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SimulationScene), nameof(SimulationScene.MouseMiddleClick))]
        private static bool MouseMiddleClick() => !ThirdPersonController.IsPovRunning;

        /// <summary>
        /// The third click handler the original left alone. It selects whatever is under the
        /// cursor, which fires constantly once left click is held to walk forward.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SimulationScene), nameof(SimulationScene.CursorTargetSelect))]
        private static bool CursorTargetSelect()
        {
            if (!ThirdPersonController.IsPovRunning) return true;

            // Follows Forward Mode rather than a switch of its own, because the two can never
            // sensibly disagree: with a mouse button bound to walking, that button's clicks are
            // not meant for the world, and with Forward Mode off nothing has been taken over so
            // the vanilla click should behave exactly as it always did.
            if (Plugin.Mode.Value == ForwardMode.Off) return true;

            // Only while the cursor is captured for looking. Whenever it is free -- button
            // mode, or the Mouse Mode key held -- clicking a character to talk to them should
            // work, the way it did in the original.
            return Cursor.lockState != CursorLockMode.Locked;
        }
    }
}
