using System.Collections.Generic;
using System.Text;
using ILLGames.Unity.Component;
using Manager;
using SV;
using UnityEngine;

namespace SVS_FreeRoam
{
    /// <summary>
    /// Idle animations on demand. The spots are the map's own "urouro" (wander) points, the
    /// ones the game sends the player to on arriving in a map; seats and other special spots
    /// are used by walking to the real point, so the game seats the character itself. Standing
    /// animations are played directly, by their id in AnimationCtrlManager.Animation
    /// (FINDINGS.md §28).
    /// </summary>
    internal static class ClickIdler
    {
        private const float ClickSlop = 8f;            // pixels a click may drift and still be a click
        private const float BodyHeight = 1.5f;         // feet to about the head, for clicks on yourself
        private const float HoldTime = 0.35f;          // seconds held before the wheel opens
        private const float SpotReach = 1.5f;          // third person: how near a seat has to be
        private const float SpotHeight = 0.4f;         // where on a seat a click is aimed, above its base

        // What the game's standing spots offer, for maps whose spots cannot be read.
        private static readonly int[] DefaultStanding = { 0, 13, 15, 24, 26, 29, 31, 34 };

        private static Vector3? _pressAt;
        private static float _pressTime;
        private static float _heldSince = -1f;
        private static int _listedMap = -1;

        // ------------------------------------------------------------------ names and lists

        internal static string Name(int id) =>
            ((AnimationCtrlManager.Animation)id).ToString().Replace('_', ' ');

        private static Il2CppSystem.Collections.Generic.Dictionary<int, PointList.ListInfo> Spots(
            SV.Chara.AI playerAI, out int mapId)
        {
            mapId = -1;
            var mapManager = SingletonInitializerAsync<MapManager>.Instance;
            var bctrl = playerAI.BehaviourCtrl;
            if (mapManager == null || bctrl == null) return null;
            mapId = bctrl.NowMapID;

            MapCollisionCtrl.Info info = null;
            mapManager.pointInfoTable?.TryGetValue(mapId, out info);
            return info?.pointList?.urouroTable;
        }

        /// <summary>A spot to stand at, as opposed to a chair, a desk or a place on the ground.</summary>
        private static bool IsStanding(MovePointInfo point)
        {
            var poses = point.poses;
            if (poses == null || poses.Count == 0) return true;
            for (int i = 0; i < poses.Count; i++)
                if (poses[i] == MovePointInfo.PoseKind.Stand) return true;
            return false;
        }

        private static void AddAnimations(MovePointInfo point, List<int> ids)
        {
            var details = point.urouroDetails;
            if (details == null) return;
            for (int i = 0; i < details.Count; i++)
            {
                var animations = details[i]?.animations;
                if (animations == null) continue;
                for (int k = 0; k < animations.Count; k++)
                {
                    int id = animations[k].animMotion;
                    if (!ids.Contains(id)) ids.Add(id);
                }
            }
        }

        /// <summary>Every animation the map's standing spots offer.</summary>
        internal static List<int> StandingAnimations(SV.Chara.AI playerAI)
        {
            var ids = new List<int>();
            var table = Spots(playerAI, out _);
            if (table != null)
            {
                foreach (var pair in table)
                {
                    var points = pair.Value?.points;
                    if (points == null) continue;
                    for (int i = 0; i < points.Count; i++)
                        if (points[i] != null && IsStanding(points[i])) AddAnimations(points[i], ids);
                }
            }
            if (ids.Count == 0) ids.AddRange(DefaultStanding);
            return ids;
        }

        /// <summary>
        /// What can be played right now: the animations of the seat the player is using, or
        /// the standing ones anywhere else.
        /// </summary>
        private static List<int> Choices(SV.Chara.AI playerAI)
        {
            var bctrl = playerAI.BehaviourCtrl;
            var point = bctrl?.target?.pInfo;
            if (point != null && !Walker.IsOurTarget(bctrl) && !IsStanding(point) &&
                Vector3.Distance(SeatPosition(point), playerAI.transform.position) < 1f)
            {
                var ids = new List<int>();
                AddAnimations(point, ids);
                if (ids.Count > 0) return ids;
            }
            return StandingAnimations(playerAI);
        }

        private static Vector3 SeatPosition(MovePointInfo point)
        {
            var details = point.urouroDetails;
            if (details != null)
                for (int i = 0; i < details.Count; i++)
                {
                    var offset = details[i]?.charactorOffset;
                    if (offset != null) return offset.position;
                }
            return point.transform.position;
        }

        // ------------------------------------------------------------------ playing

        private static void Play(SV.Chara.AI playerAI, int id)
        {
            Follower.Stop("idle animation", playerAI);
            // A click-walk still under way would carry the animation along with it.
            if (Walker.IsWalking(playerAI.BehaviourCtrl)) playerAI.BehaviourCtrl.Stop(true);
            Notice.Log($"Idle: playing {id} ({Name(id)}).");
            playerAI.SetLowpolyAnimation(id, false, true, 0.25f, true);
        }

        private static void PlayRandom(SV.Chara.AI playerAI)
        {
            var ids = Choices(playerAI);
            // Not the plain waiting poses: those are what the character does anyway.
            var special = ids.FindAll(id => id != 0 && id != 8 && id != 9 && id != 10);
            if (special.Count > 0) ids = special;
            Play(playerAI, ids[Random.Range(0, ids.Count)]);
        }

        // ------------------------------------------------------------------ cursor free

        /// <summary>
        /// With the cursor showing: a click of the idle button on your own character plays a
        /// random animation, holding it there opens the wheel.
        /// </summary>
        internal static void Update(SV.Chara.AI playerAI)
        {
            if (Notice.On) ListMapAnimations(playerAI);

            if (!Plugin.ClickIdle.Value) { Cancel(); return; }
            if (Cursor.lockState == CursorLockMode.Locked) { _pressAt = null; return; }
            // Third person's hold does not carry over into button mode.
            if (_heldSince >= 0f) Cancel();

            if (Keys.Down(Plugin.IdleButton, Plugin.IdleButton2))
            {
                _pressAt = null;
                var cam = Camera.main;
                if (cam != null && Picker.WhyNotClickable() == null && OnPlayer(cam, playerAI) &&
                    !(Picker.UnderMouse(cam, playerAI, out var pick) && pick.Character != null))
                {
                    _pressAt = Input.mousePosition;
                    _pressTime = Time.unscaledTime;
                }
            }
            if (_pressAt == null) return;

            bool dragged = (Input.mousePosition - _pressAt.Value).magnitude > ClickSlop;
            if (Keys.Held(Plugin.IdleButton, Plugin.IdleButton2))
            {
                if (!IdleWheel.IsOpen && !dragged && Time.unscaledTime - _pressTime >= HoldTime)
                    IdleWheel.Open(Choices(playerAI), _pressAt.Value, false);
                IdleWheel.Tick();
                return;
            }

            // Released.
            if (IdleWheel.IsOpen)
            {
                var chosen = IdleWheel.Close();
                if (chosen != null) Play(playerAI, chosen.Value);
            }
            else if (!dragged)
            {
                PlayRandom(playerAI);
            }
            _pressAt = null;
        }

        private static void Cancel()
        {
            _pressAt = null;
            _heldSince = -1f;
            IdleWheel.Close();
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

        // ------------------------------------------------------------------ click to walk

        /// <summary>
        /// The seat or other special spot the mouse points at. Judged by how close the line
        /// of sight passes to it, not by what the click hits: most props have nothing to hit.
        /// </summary>
        internal static bool SpotUnderMouse(Camera cam, SV.Chara.AI playerAI,
                                            out MovePointInfo spot, out int job)
        {
            spot = null;
            job = -1;
            var table = Spots(playerAI, out _);
            if (table == null) return false;

            var ray = cam.ScreenPointToRay(Input.mousePosition);
            float best = Plugin.SpotClickSize.Value;
            foreach (var pair in table)
            {
                var points = pair.Value?.points;
                if (points == null) continue;
                for (int i = 0; i < points.Count; i++)
                {
                    var point = points[i];
                    if (point == null || IsStanding(point)) continue;

                    var to = SeatPosition(point) + Vector3.up * SpotHeight - ray.origin;
                    if (Vector3.Dot(to, ray.direction) <= 0f) continue;
                    float distance = Vector3.Cross(ray.direction, to).magnitude;
                    if (distance < best || (distance == best && pair.Key == -1))
                    {
                        best = distance;
                        spot = point;
                        job = pair.Key;
                    }
                }
            }
            return spot != null;
        }

        // ------------------------------------------------------------------ third person

        /// <summary>The seat or other special spot within reach of the player, if any.</summary>
        internal static MovePointInfo NearbySpot(SV.Chara.AI playerAI, out int job)
        {
            job = -1;
            MovePointInfo spot = null;
            if (!Plugin.ClickIdle.Value || !Plugin.ThirdPersonSpots.Value) return null;
            var table = Spots(playerAI, out _);
            if (table == null) return null;

            var feet = playerAI.transform.position;
            float best = SpotReach;
            foreach (var pair in table)
            {
                var points = pair.Value?.points;
                if (points == null) continue;
                for (int i = 0; i < points.Count; i++)
                {
                    var point = points[i];
                    if (point == null || IsStanding(point)) continue;
                    float distance = Mathf.Min(Vector3.Distance(point.transform.position, feet),
                                               Vector3.Distance(SeatPosition(point), feet));
                    if (distance < best || (distance == best && pair.Key == -1))
                    {
                        best = distance;
                        spot = point;
                        job = pair.Key;
                    }
                }
            }
            return spot;
        }

        internal static string Label(MovePointInfo spot)
        {
            var poses = spot.poses;
            var pose = poses != null && poses.Count > 0 ? poses[0] : MovePointInfo.PoseKind.Stand;
            return pose == MovePointInfo.PoseKind.Chair ? "Sit"
                 : pose == MovePointInfo.PoseKind.Desk ? "Sit at the desk"
                 : "Rest here";
        }

        /// <summary>
        /// Third person, cursor locked: a press of interact next to a seat uses it; holding
        /// interact opens the wheel. <paramref name="free"/>: interact has nothing else to act
        /// on (nobody aimed at, no doorway or activity in reach, no walk under way).
        /// </summary>
        internal static void ThirdPerson(SV.Chara.AI playerAI, bool free)
        {
            if (!Plugin.ClickIdle.Value || !Plugin.ThirdPersonSpots.Value) { Cancel(); return; }

            bool pad = Plugin.GamepadSupport.Value && !GamepadUI.Active;
            bool down = Keys.Down(Plugin.InteractKey, Plugin.InteractKey2) ||
                        (pad && Keys.Down(Plugin.GamepadInteractKey, Plugin.GamepadInteractKey2));
            bool held = Keys.Held(Plugin.InteractKey, Plugin.InteractKey2) ||
                        (pad && Keys.Held(Plugin.GamepadInteractKey, Plugin.GamepadInteractKey2));

            if (down && free) _heldSince = Time.unscaledTime;
            if (_heldSince < 0f) return;

            if (held)
            {
                if (!IdleWheel.IsOpen && Time.unscaledTime - _heldSince >= HoldTime)
                    IdleWheel.Open(Choices(playerAI), new Vector2(Screen.width, Screen.height) * 0.5f, true);
                IdleWheel.Tick();
                return;
            }

            // Released.
            _heldSince = -1f;
            if (IdleWheel.IsOpen)
            {
                var chosen = IdleWheel.Close();
                if (chosen != null) Play(playerAI, chosen.Value);
                return;
            }

            var spot = NearbySpot(playerAI, out int job);
            if (spot == null) return;
            Follower.Stop("using a spot", playerAI);
            Notice.Log($"Idle: using {Describe(spot)}, job {job}.");
            Walker.WalkToPoint(playerAI, spot, playerAI.BehaviourCtrl.NowMapID, job);
        }

        // ------------------------------------------------------------------ for reference

        /// <summary>Once per map, with Debug Info on: every kind of spot and its animations.</summary>
        private static void ListMapAnimations(SV.Chara.AI playerAI)
        {
            var table = Spots(playerAI, out int mapId);
            if (table == null || mapId == _listedMap) return;
            _listedMap = mapId;

            // "Chair, job -1" -> spots counted, animation ids.
            var kinds = new SortedDictionary<string, (int Count, List<int> Ids)>();
            foreach (var pair in table)
            {
                var points = pair.Value?.points;
                if (points == null) continue;
                for (int i = 0; i < points.Count; i++)
                {
                    var point = points[i];
                    var details = point?.urouroDetails;
                    if (details == null) continue;

                    var poseText = new StringBuilder();
                    var poses = point.poses;
                    if (poses != null)
                        for (int k = 0; k < poses.Count; k++) poseText.Append(k > 0 ? "+" : "").Append(poses[k]);
                    if (poseText.Length == 0) poseText.Append("(no pose)");

                    for (int d = 0; d < details.Count; d++)
                    {
                        var detail = details[d];
                        var animations = detail?.animations;
                        if (animations == null) continue;
                        string key = $"{poseText}, activity {detail.job}, listed under {pair.Key}";
                        if (!kinds.TryGetValue(key, out var kind)) kind = (0, new List<int>());
                        for (int k = 0; k < animations.Count; k++)
                            if (!kind.Ids.Contains(animations[k].animMotion)) kind.Ids.Add(animations[k].animMotion);
                        kinds[key] = (kind.Count + 1, kind.Ids);
                    }
                }
            }

            var sb = new StringBuilder($"Idle: animations on map {mapId}:");
            foreach (var kind in kinds)
            {
                sb.Append("\n  ").Append(kind.Key).Append(" (").Append(kind.Value.Count).Append(" spots):");
                foreach (int id in kind.Value.Ids) sb.Append(' ').Append(id).Append('=').Append(Name(id)).Append(',');
            }
            Notice.Log(sb.ToString());
        }

        internal static string Describe(MovePointInfo point)
        {
            var sb = new StringBuilder();
            sb.Append('\'').Append(point.name).Append("' at ").Append(point.transform.position).Append(" poses[");
            var poses = point.poses;
            if (poses != null)
                for (int i = 0; i < poses.Count; i++) sb.Append(i > 0 ? "," : "").Append(poses[i]);
            sb.Append(']');
            var ids = new List<int>();
            AddAnimations(point, ids);
            foreach (int id in ids) sb.Append(' ').Append(id).Append('=').Append(Name(id));
            return sb.ToString();
        }
    }
}
