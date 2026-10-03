using Manager;
using SV;
using UnityEngine;

namespace SVS_FreeRoam
{
    /// <summary>
    /// Sends the player to a point with the game's own map-point walk: a MovePointInfo placed
    /// at the spot, handed to MapManager.SetCharaMapMove. That runs the same Move tree the game
    /// uses for every walk, so pathing and animation are the game's (FINDINGS.md §25).
    /// Shared by click-to-walk and follow.
    /// </summary>
    internal static class Walker
    {
        // One marker, reused. It survives map changes because a map unload would otherwise
        // destroy it while it is still the player's target.
        private static GameObject _marker;
        private static MovePointInfo _markerPoint;
        // What the marker holds when it is only a place to walk to.
        private static Il2CppSystem.Collections.Generic.List<MovePointInfo.PoseKind> _blankPoses;
        private static Il2CppSystem.Collections.Generic.List<MovePointInfo.JobDetail> _blankDetails;

        internal static Vector3 MarkerPosition => _marker != null ? _marker.transform.position : Vector3.zero;

        /// <summary>
        /// The nearest walkable point to <paramref name="aimed"/>. False when there is no nav
        /// mesh, or the nearest walkable point is further away than <paramref name="maxDistance"/>.
        /// </summary>
        internal static bool Snap(Vector3 aimed, float maxDistance, out Vector3 point, out float distance)
        {
            point = default;
            distance = float.PositiveInfinity;

            var astar = AstarPath.active;
            if (astar == null) return false;

            var nearest = astar.GetNearest(aimed);
            if (nearest.node == null) return false;

            point = nearest.position;
            distance = Vector3.Distance(aimed, point);
            return distance <= maxDistance;
        }

        /// <param name="mapId">The map the point is in. The player's own map for a click; the
        /// followed character's map when following, which is how the game walks through
        /// doorways into another map (travel buttons do the same).</param>
        /// <param name="arrival">Animations to choose from on arrival (the game picks one, as
        /// at its own spots); null to just stand.</param>
        internal static void WalkTo(SV.Chara.AI playerAI, Vector3 point, int mapId,
                                    System.Collections.Generic.List<int> arrival = null)
        {
            EnsureMarker();
            Dress(arrival);
            _marker.transform.position = point;

            // Same shape as the game's own player walks via UroUroPointMove: type 0 (Urouro,
            // "wander"), job -1 (none).
            var target = new MapManager.MapTargetInfo
            {
                pInfo = _markerPoint,
                map = mapId,
                type = 0,
                job = -1,
            };
            MapManager.SetCharaMapMove(BehaviourController.BaseActionKind.Personal,
                                       playerAI.BehaviourCtrl, target, false);
        }

        /// <summary>
        /// Sends the player to one of the map's own points, so the game plays that point's
        /// animation on arrival, as it does for the spot it picks when a map is entered.
        /// </summary>
        internal static void WalkToPoint(SV.Chara.AI playerAI, MovePointInfo point, int mapId, int job)
        {
            var target = new MapManager.MapTargetInfo
            {
                pInfo = point,
                map = mapId,
                type = 0,
                job = job,
            };
            MapManager.SetCharaMapMove(BehaviourController.BaseActionKind.Personal,
                                       playerAI.BehaviourCtrl, target, false);
        }

        /// <summary>
        /// Makes the marker a standing spot offering these animations, as the map's own spots
        /// are, or (null) a bare place to walk to.
        /// </summary>
        private static void Dress(System.Collections.Generic.List<int> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                _markerPoint.poses = _blankPoses;
                _markerPoint.urouroDetails = _blankDetails;
                return;
            }

            var animations = new Il2CppSystem.Collections.Generic.List<MovePointInfo.AnimationInfo>();
            foreach (int id in ids)
                animations.Add(new MovePointInfo.AnimationInfo { weight = 1, animMotion = id, isAddH = false });

            var details = new Il2CppSystem.Collections.Generic.List<MovePointInfo.JobDetail>();
            details.Add(new MovePointInfo.JobDetail { job = MovePointInfo.JobKind.None, animations = animations });

            var poses = new Il2CppSystem.Collections.Generic.List<MovePointInfo.PoseKind>();
            poses.Add(MovePointInfo.PoseKind.Stand);

            _markerPoint.poses = poses;
            _markerPoint.urouroDetails = details;
        }

        /// <summary>Moves the destination of a walk already under way, without restarting it.</summary>
        internal static void MoveMarker(Vector3 point)
        {
            EnsureMarker();
            _marker.transform.position = point;
        }

        /// <summary>Whether the player's current target is our marker.</summary>
        internal static bool IsOurTarget(BehaviourController bctrl)
        {
            var t = bctrl.target?.transform;
            return t != null && _marker != null && t.Pointer == _marker.transform.Pointer;
        }

        /// <summary>
        /// Whether the player is walking to our marker right now. From the logged trace:
        /// walking reads "mode=Move task=MoveAction"; arrived reads Idle with the marker
        /// still the target.
        /// </summary>
        internal static bool IsWalking(BehaviourController bctrl)
        {
            if (!IsOurTarget(bctrl)) return false;
            var tree = bctrl.BehaviorTreeCtrl;
            if (tree == null || tree.ActionMode != Manager.Game.ActionKind.Move) return false;
            var task = tree.GetNowTask();
            return task == null || task.friendlyName == "MoveAction";
        }

        private static void EnsureMarker()
        {
            if (_marker != null && !_marker.WasCollected) return;
            _marker = new GameObject("SVS_FreeRoam WalkTarget");
            Object.DontDestroyOnLoad(_marker);
            _markerPoint = _marker.AddComponent<MovePointInfo>();
            _blankPoses = _markerPoint.poses;
            _blankDetails = _markerPoint.urouroDetails;
        }
    }
}
