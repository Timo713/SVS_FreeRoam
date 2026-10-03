using SV;
using UnityEngine;

namespace SVS_FreeRoam
{
    /// <summary>
    /// Click the ground to walk there. The walk itself is Walker's; this decides when a click
    /// means "go there".
    /// </summary>
    internal static class ClickWalker
    {
        internal static void Update(SV.Chara.AI playerAI)
        {
            if (!Plugin.ClickWalk.Value) return;
            if (!Keys.Down(Plugin.ClickWalkButton, Plugin.ClickWalkButton2)) return;

            string why = Picker.WhyNotClickable();
            if (why != null)
            {
                Notice.Log("Walk click ignored: " + why + ".");
                return;
            }

            var cam = Camera.main;
            if (cam == null) return;

            if (!Picker.UnderMouse(cam, playerAI, out var pick))
            {
                Notice.Log("Walk click ignored: pointing above the horizon.");
                return;
            }

            // The game's own click handler walks over to talk to characters.
            if (pick.Character != null)
            {
                Notice.Log($"Walk click ignored: on {pick.What}.");
                return;
            }

            // A seat or other special spot: walk to the game's own point, which seats the
            // character on arrival.
            if (Plugin.ClickWalkSpots.Value &&
                ClickIdler.SpotUnderMouse(cam, playerAI, out var spot, out int job))
            {
                Follower.Stop("walk click");
                Notice.Log($"Walk: to spot {ClickIdler.Describe(spot)}, job {job}.");
                Walker.WalkToPoint(playerAI, spot, playerAI.BehaviourCtrl.NowMapID, job);
                return;
            }

            float maxSnap = Plugin.ClickWalkMaxSnap.Value;
            if (!Walker.Snap(pick.Aimed, maxSnap, out var point, out float off))
            {
                Notice.Log(float.IsInfinity(off)
                    ? $"Walk click ignored: hit {pick.What} but there is no walkable area."
                    : $"Walk click ignored: hit {pick.What} at {pick.Aimed}; nearest walkable point " +
                      $"is {off:0.0} m away (limit {maxSnap:0.0} m).");
                return;
            }

            Follower.Stop("walk click");
            Notice.Log($"Walk: hit {pick.What} at {pick.Aimed}; walkable point {point} " +
                         $"({off:0.00} m away); player at {playerAI.transform.position}.");
            Walker.WalkTo(playerAI, point, playerAI.BehaviourCtrl.NowMapID,
                          Plugin.ClickWalkIdle.Value ? ArrivalAnimations(playerAI) : null);
        }

        /// <summary>The map's standing animations without the plain standing pose.</summary>
        private static System.Collections.Generic.List<int> ArrivalAnimations(SV.Chara.AI playerAI)
        {
            var ids = ClickIdler.StandingAnimations(playerAI);
            ids.Remove(0);
            return ids.Count > 0 ? ids : null;
        }

        internal static string Describe(BehaviourController bctrl)
        {
            var tree = bctrl.BehaviorTreeCtrl;
            var task = tree != null ? tree.GetNowTask() : null;
            var targetTransform = bctrl.target?.transform;
            return $"mode={tree?.ActionMode} task={task?.friendlyName} " +
                   $"target={bctrl.target?.kind}:{(targetTransform != null ? targetTransform.name : "none")}";
        }
    }
}
