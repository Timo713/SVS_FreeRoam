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
            Walker.WalkTo(playerAI, point, playerAI.BehaviourCtrl.NowMapID);
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
