using System;
using System.Collections.Generic;
using ILLGames.Unity.Component;
using Manager;
using SV;
using UnityEngine;
using UnityEngine.UI;

namespace SVS_FreeRoam
{
    /// <summary>
    /// Gamepad interact outside third-person: in the overview camera and on 2D maps, pressing
    /// the gamepad interact button near a character, doorway or job spot uses the nearest --
    /// walking over to talk, or travelling.
    ///
    /// Third-person already has this (FindNearestTarget / TakeDoorway), but nothing drove it
    /// in the other views, where the only way to travel was clicking a location button with
    /// the mouse. Rather than travel by hand, this presses that same button: the game then
    /// walks the character to the (invisible) doorway and changes map exactly as a mouse
    /// click would, with its own rules about which destinations are open -- a destination
    /// that is closed right now has no active button, so it is simply not offered.
    ///
    ///   doorway  SVNodeLink2 (startMapID = here)  ->  MapMoveUIButtonCtrl.GotoMapID = endMapID
    ///   job spot pointInfoTable[here].pcTable      ->  MapPCActionUIButtonCtrl.Job = job kind
    /// </summary>
    internal static class GamepadTravel
    {
        private static float _tripStartedAt = -1000f;

        /// <summary>Closer than this to a doorway, it is crossed directly (see Run).</summary>
        private const float DirectCrossing = 1.5f;

        /// <summary>
        /// Whether the map change just seen came from a gamepad trip, and forgets it. A trip
        /// that never arrived (cancelled, or blocked) expires after half a minute.
        /// </summary>
        internal static bool TakeTrip()
        {
            bool recent = Time.unscaledTime - _tripStartedAt < 30f;
            _tripStartedAt = -1000f;
            return recent;
        }
        /// <summary>Returns true when it pressed a location's button.</summary>
        internal static bool Run(SimulationScene scene, MapManager mapManager, SV.Chara.AI playerAI,
                                 int mapId)
        {
            if (!Plugin.GamepadSupport.Value) return false;
            if (GamepadUI.Active) return false;    // the buttons on screen have the controller

            // Talk (X) reaches characters, interact (Y) doorways and activity spots.
            bool talk = Keys.Down(Plugin.GamepadTalkKey, Plugin.GamepadTalkKey2);
            bool place = Keys.Down(Plugin.GamepadInteractKey, Plugin.GamepadInteractKey2);
            if (!talk && !place) return false;

            try
            {
                float range = Plugin.GamepadReach.Value;
                var feet = playerAI.position;

                Button best = null;
                string what = null;
                bool isTrip = false;
                SVNodeLink2 bestLink = null;
                float nearest = range;

                // Doorways leading out of here.
                var links = place ? UnityEngine.Object.FindObjectsOfType<SVNodeLink2>() : null;
                if (links != null)
                {
                    foreach (var link in links)
                    {
                        if (link == null || link.startMapID != mapId) continue;
                        float d = Flat(link.transform.position, feet);
                        if (d >= nearest) continue;

                        var button = TravelButton(mapManager, link.endMapID);
                        if (button == null) continue;

                        nearest = d;
                        best = button;
                        what = "doorway to map " + link.endMapID;
                        isTrip = true;
                        bestLink = link;
                    }
                }

                // Job spots here: Work, Eat, Study, Change Outfit, Pray.
                if (place)
                foreach (var pair in MapButtonTracker.JobPoints(mapManager, mapId))
                {
                    if (pair.Value == null) continue;
                    float d = Flat(pair.Value.position, feet);
                    if (d >= nearest) continue;

                    var button = JobButton(mapManager, pair.Key);
                    if (button == null) continue;

                    nearest = d;
                    best = button;
                    what = "job spot " + pair.Key;
                    isTrip = false;
                    bestLink = null;
                }

                // Characters: walk over and talk, as clicking one does.
                SV.Chara.AI person = null;
                if (talk)
                foreach (var ai in Game.AICharas)
                {
                    if (ai == null || ai.Pointer == playerAI.Pointer) continue;
                    if (ai.BehaviourCtrl.NowMapID != mapId) continue;
                    float d = Flat(ai.position, feet);
                    if (d >= nearest) continue;
                    nearest = d;
                    person = ai;
                }
                if (person != null)
                {
                    Notice.Log($"Gamepad interact: {person.charaData.Name} ({nearest:0.0} m).");
                    ThirdPersonController.WalkToCharacter(scene, playerAI, person);
                    return true;
                }

                if (best == null) return false;

                Notice.Log($"Gamepad interact: {what} ({nearest:0.0} m).");
                if (isTrip) _tripStartedAt = Time.unscaledTime;

                // Right at the doorway, the game's own trip has to walk to a point the
                // character is already standing on or has passed: it turned round, stopped,
                // and needed pressing again. Third-person's direct crossing never fails, so
                // that is used this close; further off, the location's button, so the
                // character runs there as a click would have it.
                if (bestLink != null && nearest < DirectCrossing)
                {
                    Notice.Log("Gamepad interact: close enough to cross directly.");
                    ThirdPersonController.CrossDoorway(mapManager, playerAI, bestLink);
                    return true;
                }

                best.onClick.Invoke();
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("Gamepad interact failed: " + e.Message);
            }
            return false;
        }

        /// <summary>Distance across the ground. Doorway markers can sit above or below the
        /// character's feet, particularly on 2D maps.</summary>
        private static float Flat(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        /// <summary>
        /// The travel button for a destination, only if the game currently offers it. Only the
        /// MapMoveUI in use is searched (the summary also holds the unused Constant/Pop-ups
        /// alternate, FINDINGS §14).
        /// </summary>
        private static Button TravelButton(MapManager mapManager, int destination)
        {
            foreach (var ui in ActiveMoveUIs(mapManager))
            {
                var list = ui.MapMoveUIButtonCtrls;
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    var b = list[i];
                    if (b == null || b.GotoMapID != destination) continue;
                    if (Usable(b._btn)) return b._btn;
                }
            }
            return null;
        }

        private static Button JobButton(MapManager mapManager, int job)
        {
            foreach (var ui in ActiveMoveUIs(mapManager))
            {
                var list = ui.pcSoloActionUIButtonCtrls;
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    var b = list[i];
                    if (b == null || (int)b.Job != job) continue;
                    if (Usable(b.btn)) return b.btn;
                }
            }

            // Not every action button is in that list (the classroom's Study is not).
            foreach (var ui in ActiveMoveUIs(mapManager))
                foreach (var b in ui.GetComponentsInChildren<MapPCActionUIButtonCtrl>())
                    if (b != null && (int)b.Job == job && Usable(b.btn)) return b.btn;
            return null;
        }

        private static IEnumerable<MapMoveUI> ActiveMoveUIs(MapManager mapManager)
        {
            var uis = mapManager._mapMoveUISummary?.MapMoveUIs;
            if (uis == null) yield break;
            for (int i = 0; i < uis.Count; i++)
            {
                var ui = uis[i];
                if (ui != null && ui.gameObject.activeInHierarchy) yield return ui;
            }
        }

        /// <summary>Available right now: shown and clickable, as far as the game is concerned.</summary>
        private static bool Usable(Button button) =>
            button != null && button.gameObject.activeInHierarchy && button.interactable;
    }
}
