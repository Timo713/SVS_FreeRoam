using System.Collections.Generic;
using System.Text;
using ILLGames.Unity.Component;
using Manager;
using SV;
using UnityEngine;

namespace SVS_FreeRoam
{
    /// <summary>
    /// Click an idle spot (a bench, a chair, a place characters hang around at) to go there and
    /// use it, or click your own character to idle where you stand. The spots are the map's own
    /// "urouro" (wander) points, the ones the game sends the player to on arriving in a map, and
    /// the animation is whatever the game picks for the character there (FINDINGS.md §28).
    /// </summary>
    internal static class ClickIdler
    {
        private const float ClickSlop = 8f;            // pixels a click may drift and still be a click
        private const float BodyHeight = 1.5f;         // feet to about the head, for clicks on yourself

        private static Vector3? _pressAt;

        internal static void Update(SV.Chara.AI playerAI)
        {
            if (!Plugin.ClickIdle.Value) { _pressAt = null; return; }

            // A click is press and release in about the same place, made with the cursor free
            // (as for follow, which shares the right button by default).
            if (Keys.Down(Plugin.IdleButton, Plugin.IdleButton2))
                _pressAt = Cursor.lockState == CursorLockMode.Locked ? (Vector3?)null : Input.mousePosition;
            if (!Keys.Up(Plugin.IdleButton, Plugin.IdleButton2) || _pressAt == null) return;

            bool dragged = (Input.mousePosition - _pressAt.Value).magnitude > ClickSlop;
            _pressAt = null;
            if (dragged) return;

            if (Picker.WhyNotClickable() != null) return;

            var cam = Camera.main;
            if (cam == null) return;

            bool hit = Picker.UnderMouse(cam, playerAI, out var pick);
            // A click on another character is follow's.
            if (hit && pick.Character != null) return;

            var mapManager = SingletonInitializerAsync<MapManager>.Instance;
            var bctrl = playerAI.BehaviourCtrl;
            if (mapManager == null || bctrl == null) return;
            int mapId = bctrl.NowMapID;

            MapCollisionCtrl.Info info = null;
            mapManager.pointInfoTable?.TryGetValue(mapId, out info);
            var table = info?.pointList?.urouroTable;
            if (table == null)
            {
                Notice.Log("Idle click ignored: this map has no idle spots.");
                return;
            }

            if (OnPlayer(cam, playerAI))
            {
                IdleHere(playerAI, table, mapId);
                return;
            }
            if (!hit) return;

            float limit = Plugin.IdleSnap.Value;
            MovePointInfo best = null;
            int bestJob = -1;
            float bestDistance = float.PositiveInfinity;
            foreach (var pair in table)
            {
                var points = pair.Value?.points;
                if (points == null) continue;
                for (int i = 0; i < points.Count; i++)
                {
                    var point = points[i];
                    if (point == null) continue;
                    float d = Vector3.Distance(point.transform.position, pick.Aimed);
                    if (d < bestDistance || (d == bestDistance && pair.Key == -1))
                    {
                        best = point;
                        bestJob = pair.Key;
                        bestDistance = d;
                    }
                }
            }

            if (best == null || bestDistance > limit)
            {
                Notice.Log($"Idle click ignored: hit {pick.What} at {pick.Aimed}; nearest idle spot is " +
                           (best == null ? "none" : $"{bestDistance:0.0} m away (limit {limit:0.0} m)") + ".");
                return;
            }

            Follower.Stop("idle click");
            Notice.Log($"Idle: going to {Describe(best)}, job {bestJob}, {bestDistance:0.0} m from the click.");
            Walker.WalkToPoint(playerAI, best, mapId, bestJob);
        }

        /// <summary>
        /// Idle on the spot: our own marker at the player's feet, carrying the animations of one
        /// of the map's standing idle spots, so the game picks from that set as it would there.
        /// </summary>
        private static void IdleHere(SV.Chara.AI playerAI, Il2CppSystem.Collections.Generic.Dictionary<int, PointList.ListInfo> table, int mapId)
        {
            var candidates = new List<MovePointInfo>();
            PointList.ListInfo list = null;
            if (table.TryGetValue(-1, out list) && list?.points != null)
            {
                for (int i = 0; i < list.points.Count; i++)
                {
                    var point = list.points[i];
                    if (point != null && IsPlainStandingSpot(point)) candidates.Add(point);
                }
            }

            if (candidates.Count == 0)
            {
                Notice.Log("Idle click on yourself ignored: this map has no standing idle spot to take animations from.");
                return;
            }

            var source = candidates[Random.Range(0, candidates.Count)];
            Follower.Stop("idle click");
            Notice.Log($"Idle: on the spot, with the animations of {Describe(source)} ({candidates.Count} standing spots here).");
            Walker.IdleAt(playerAI, playerAI.transform, source, mapId);
        }

        /// <summary>A spot to stand at, with animations, that does not move the character onto a prop.</summary>
        private static bool IsPlainStandingSpot(MovePointInfo point)
        {
            var poses = point.poses;
            if (poses != null && poses.Count > 0)
            {
                bool stand = false;
                for (int i = 0; i < poses.Count; i++)
                    if (poses[i] == MovePointInfo.PoseKind.Stand) stand = true;
                if (!stand) return false;
            }

            var details = point.urouroDetails;
            if (details == null || details.Count == 0) return false;
            bool animated = false;
            for (int i = 0; i < details.Count; i++)
            {
                var detail = details[i];
                if (detail == null) continue;
                if (detail.charactorOffset != null || !string.IsNullOrEmpty(detail.moveObjectName)) return false;
                if (detail.animations != null && detail.animations.Count > 0) animated = true;
            }
            return animated;
        }

        /// <summary>Whether the cursor is on the player's body, judged on screen: feet to head.</summary>
        private static bool OnPlayer(Camera cam, SV.Chara.AI playerAI)
        {
            var feetWorld = playerAI.transform.position;
            var feet = cam.WorldToScreenPoint(feetWorld);
            var head = cam.WorldToScreenPoint(feetWorld + Vector3.up * BodyHeight);
            if (feet.z <= 0f || head.z <= 0f) return false;

            Vector2 a = feet, b = head, m = Input.mousePosition;
            var ab = b - a;
            float length = ab.magnitude;
            float t = length < 0.001f ? 0f : Mathf.Clamp01(Vector2.Dot(m - a, ab) / (length * length));
            float distance = Vector2.Distance(m, a + ab * t);
            return distance <= Mathf.Max(20f, length * 0.2f);
        }

        internal static string Describe(MovePointInfo point)
        {
            var sb = new StringBuilder();
            sb.Append('\'').Append(point.name).Append("' at ").Append(point.transform.position);
            sb.Append(" poses[");
            var poses = point.poses;
            if (poses != null)
                for (int i = 0; i < poses.Count; i++) sb.Append(i > 0 ? "," : "").Append(poses[i]);
            sb.Append(']');

            var details = point.urouroDetails;
            if (details != null)
            {
                for (int i = 0; i < details.Count; i++)
                {
                    var detail = details[i];
                    if (detail == null) continue;
                    sb.Append(" {job ").Append((int)detail.job).Append(" anims");
                    var animations = detail.animations;
                    if (animations != null)
                        for (int k = 0; k < animations.Count; k++)
                            sb.Append(' ').Append(animations[k].animMotion).Append('x').Append(animations[k].weight);
                    if (detail.charactorOffset != null) sb.Append(" offset");
                    if (!string.IsNullOrEmpty(detail.moveObjectName)) sb.Append(" prop '").Append(detail.moveObjectName).Append('\'');
                    sb.Append('}');
                }
            }
            return sb.ToString();
        }
    }
}
