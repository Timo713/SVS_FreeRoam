using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ILLGames.Unity.Component;
using Manager;
using SV;
using UnityEngine;

namespace SVS_FreeRoam
{
    /// <summary>
    /// Spots and seats: finding them, sitting the player on them (through the game for its
    /// own wander seats, by hand for an activity's), keeping the player there, and taking
    /// them off again (FINDINGS.md §28).
    /// </summary>
    internal static partial class ClickIdler
    {
        private const float SpotReach = 1f;            // third person: how near a seat has to be

        private const float ChoiceReach = 2f;          // ...for a sitting animation chosen from the wheel

        private const float SpotHeight = 0.4f;         // where on a seat a click is aimed, above its base

        private static int _listedMap = -1;

        /// <summary>What a wander point offers for one activity.</summary>
        private static MovePointInfo.JobDetail UrouroDetail(MovePointInfo point, int job)
        {
            var details = point.urouroDetails;
            if (details == null) return null;
            for (int i = 0; i < details.Count; i++)
                if (details[i]?.animations != null && (int)details[i].job == job) return details[i];
            return null;
        }

        // A spot told to offer one animation only, so that the game, putting the player there,
        // starts the one chosen from the wheel and not one of its own choosing first.
        private static MovePointInfo.JobDetail _forcedDetail;

        private static Il2CppSystem.Collections.Generic.List<MovePointInfo.AnimationInfo> _forcedOffer;

        private static void Force(MovePointInfo spot, int job, int id)
        {
            Unforce();
            var detail = UrouroDetail(spot, job);
            if (detail == null) return;

            // The spot's own entry for it, if it has one; else one like the others.
            MovePointInfo.AnimationInfo entry = null;
            for (int k = 0; k < detail.animations.Count; k++)
                if (detail.animations[k].animMotion == id) entry = detail.animations[k];
            entry ??= new MovePointInfo.AnimationInfo { weight = 1, animMotion = id, isAddH = false };

            var only = new Il2CppSystem.Collections.Generic.List<MovePointInfo.AnimationInfo>();
            only.Add(entry);
            _forcedDetail = detail;
            _forcedOffer = detail.animations;
            detail.animations = only;
        }

        /// <summary>The spot offers what it always did again.</summary>
        private static void Unforce()
        {
            if (_forcedDetail != null) _forcedDetail.animations = _forcedOffer;
            _forcedDetail = null;
            _forcedOffer = null;
        }

        private static readonly HashSet<IntPtr> _taken = new HashSet<IntPtr>();

        /// <summary>
        /// The spots other characters are on, or on their way to: not for the player to sit on
        /// as well. A character's target stays what it was after they wander off to talk to
        /// someone, so one that is neither walking there nor within reach of it does not count.
        /// </summary>
        private static HashSet<IntPtr> Taken(SV.Chara.AI playerAI)
        {
            _taken.Clear();
            foreach (var ai in Game.AICharas)
            {
                if (ai == null || ai.Pointer == playerAI.Pointer) continue;
                var bctrl = ai.BehaviourCtrl;
                var point = bctrl?.target?.pInfo;
                if (point == null) continue;

                var mode = bctrl.BehaviorTreeCtrl != null ? bctrl.BehaviorTreeCtrl.ActionMode : Manager.Game.ActionKind.Idle;
                bool onTheWay = mode == Manager.Game.ActionKind.Move || mode == Manager.Game.ActionKind.UroUroMove;
                var at = ai.transform.position;
                if (onTheWay || Vector3.Distance(at, point.transform.position) < 1.5f ||
                    Vector3.Distance(at, SeatPosition(point)) < 1.5f) _taken.Add(point.Pointer);
            }
            return _taken;
        }

        /// <summary>A spot's offer for one activity includes sitting.</summary>
        private static bool SeatDetail(MovePointInfo.JobDetail detail)
        {
            var animations = detail?.animations;
            if (animations == null) return false;
            for (int k = 0; k < animations.Count; k++)
                if (NeedsSeat(animations[k].animMotion)) return true;
            return false;
        }

        /// <summary>
        /// Whether a wander point, as listed under one activity, is somewhere to sit: by its
        /// pose, or because what it offers for that activity is a sitting animation. The
        /// classroom's desks are "standing" points that offer Study's desk animations.
        /// </summary>
        private static bool IsSeat(MovePointInfo point, int job)
        {
            if (!IsStanding(point)) return true;
            var details = point.urouroDetails;
            if (details == null) return false;
            for (int i = 0; i < details.Count; i++)
                if (details[i] != null && (int)details[i].job == job && SeatDetail(details[i])) return true;
            return false;
        }

        private static bool IsSeat(MovePointInfo point)
        {
            if (!IsStanding(point)) return true;
            var details = point.urouroDetails;
            if (details == null) return false;
            for (int i = 0; i < details.Count; i++)
                if (SeatDetail(details[i])) return true;
            return false;
        }

        // The seat last sat on through us, and for which activity: what it offers for that
        // activity is what can be played there.
        private static MovePointInfo _usedSpot;

        private static int _usedJob;

        internal static void NoteSpot(MovePointInfo spot, int job)
        {
            Release();
            _usedSpot = spot;
            _usedJob = job;
        }

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
            _pointList = info?.pointList;
            return _pointList?.urouroTable;
        }

        // The map's whole point list, as of the last Spots call.
        private static PointList _pointList;

        // A seat borrowed from an activity (study, a meal): the game only seats characters
        // there during that activity, so we do it by hand. Which point, where the body is,
        // and what can be played there.
        private static MovePointInfo _borrowed;

        private static Vector3 _borrowedAt;

        private static List<int> _borrowedIds;

        // Which table NearbySpot's last answer came from: 0 the wander points, else an activity's.
        private static int _nearbyTable;

        private static readonly string[] TableNames = { "urouro", "solo", "with", "everyone", "pc" };

        /// <summary>The activity tables: 1 solo, 2 with, 3 everyone, 4 pc.</summary>
        private static Il2CppSystem.Collections.Generic.Dictionary<int, PointList.ListInfo> Table(int table) =>
            _pointList == null ? null
            : table == 1 ? _pointList.soloTable
            : table == 2 ? _pointList.withTable
            : table == 3 ? _pointList.everyoneTable
            : _pointList.pcTable;

        private static Il2CppSystem.Collections.Generic.List<MovePointInfo.JobDetail> Details(MovePointInfo point, int table) =>
            table == 1 ? point.soloDetails
            : table == 2 ? point.withDetails
            : table == 3 ? point.everyoneDetails
            : point.pcDetails;

        /// <summary>What a point offers for one activity, if it has animations for it.</summary>
        private static MovePointInfo.JobDetail Detail(MovePointInfo point, int table, int job)
        {
            var details = Details(point, table);
            if (details == null) return null;
            for (int i = 0; i < details.Count; i++)
            {
                var detail = details[i];
                if (detail != null && (int)detail.job == job && detail.animations != null &&
                    detail.animations.Count > 0) return detail;
            }
            return null;
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
            if (_borrowed != null && point.Pointer == _borrowed.Pointer)
            {
                foreach (int id in _borrowedIds) if (!ids.Contains(id)) ids.Add(id);
                return;
            }
            var details = point.urouroDetails;
            if (details == null) return;

            // The seat we sat on: only what it offers for the activity it was used for.
            bool used = _usedSpot != null && point.Pointer == _usedSpot.Pointer;
            if (used)
            {
                used = false;
                for (int i = 0; i < details.Count; i++)
                    if (details[i] != null && (int)details[i].job == _usedJob) used = true;
            }

            for (int i = 0; i < details.Count; i++)
            {
                if (used && details[i] != null && (int)details[i].job != _usedJob) continue;
                var animations = details[i]?.animations;
                if (animations == null) continue;
                for (int k = 0; k < animations.Count; k++)
                {
                    int id = animations[k].animMotion;
                    if (!ids.Contains(id)) ids.Add(id);
                }
            }
        }

        /// <summary>The seat or other special spot the player is using, if any.</summary>
        private static MovePointInfo CurrentSeat(SV.Chara.AI playerAI)
        {
            if (_held && _heldSeat != null) return _heldSeat;
            if (_borrowed != null)
            {
                if (Vector3.Distance(playerAI.transform.position, _borrowedAt) < 0.5f) return _borrowed;
                Notice.Log($"Idle: no longer on the borrowed seat (now at {playerAI.transform.position}, " +
                           $"seat at {_borrowedAt}).");
                _borrowed = null;
            }

            var bctrl = playerAI.BehaviourCtrl;
            var point = bctrl?.target?.pInfo;
            return point != null && !Walker.IsOurTarget(bctrl) && IsSeat(point) &&
                   // (The character itself stands at the point; only its body is on the seat.)
                   Mathf.Min(Vector3.Distance(point.transform.position, playerAI.transform.position),
                             Vector3.Distance(SeatPosition(point), playerAI.transform.position)) < 1f
                ? point : null;
        }

        private static Vector3 SeatPosition(MovePointInfo point)
        {
            if (_borrowed != null && point.Pointer == _borrowed.Pointer) return _borrowedAt;
            var details = point.urouroDetails;
            if (details != null)
                for (int i = 0; i < details.Count; i++)
                {
                    var offset = details[i]?.charactorOffset;
                    if (offset != null) return offset.position;
                }
            return point.transform.position;
        }

        /// <summary>
        /// A spot within reach that offers exactly this animation at a place of its own (the
        /// cafe's tables for the waitress's wiping). Standing spots that merely list it do not
        /// count: there is nothing to go there for.
        /// </summary>
        private static MovePointInfo NearbyOffering(SV.Chara.AI playerAI, int id, float reach, out int job)
        {
            job = -1;
            _nearbyTable = 0;
            MovePointInfo spot = null;
            var table = Spots(playerAI, out _);
            if (table == null) return null;

            var taken = Taken(playerAI);
            var feet = playerAI.transform.position;
            float best = reach;
            foreach (var pair in table)
            {
                var points = pair.Value?.points;
                if (points == null) continue;
                for (int i = 0; i < points.Count; i++)
                {
                    var details = points[i]?.urouroDetails;
                    if (details == null || taken.Contains(points[i].Pointer)) continue;
                    for (int d = 0; d < details.Count; d++)
                    {
                        var detail = details[d];
                        if (detail?.animations == null || (int)detail.job != pair.Key ||
                            detail.charactorOffset == null) continue;
                        bool offers = false;
                        for (int k = 0; k < detail.animations.Count; k++)
                            if (detail.animations[k].animMotion == id) offers = true;
                        if (!offers) continue;

                        float distance = Mathf.Min(Vector3.Distance(points[i].transform.position, feet),
                                                   Vector3.Distance(detail.charactorOffset.position, feet));
                        if (distance >= best) continue;
                        best = distance;
                        spot = points[i];
                        job = pair.Key;
                    }
                }
            }
            return spot;
        }

        /// <summary>
        /// A choice from the wheel. An animation that belongs on a spot (a seat, or a place
        /// only some spots have), chosen next to one, first puts the player there; the
        /// animation follows once the game has settled them. Anywhere else, or already seated,
        /// it plays where the player is.
        /// </summary>
        private static void PlayChoice(SV.Chara.AI playerAI, int id)
        {
            if (playerAI == null) return;
            _pendingId = -1;
            Unforce();
            if (CurrentSeat(playerAI) == null)
            {
                // Asked for outright, so the reach is wider than a tap's.
                var spot = NearbyOffering(playerAI, id, ChoiceReach, out int job);
                if (spot == null && NeedsSeat(id)) spot = NearbySpot(playerAI, out job, ChoiceReach);
                if (spot != null && _nearbyTable != 0)
                {
                    // An activity's seat, where we sit the player by hand: in this animation.
                    UseBorrowed(playerAI, spot, _nearbyTable, job, id);
                    return;
                }
                if (spot != null)
                {
                    // The game seats the player and starts one of the animations the spot
                    // offers: for the moment, it offers this one alone.
                    Force(spot, job, id);
                    UseSpot(playerAI, spot, job);
                    _pendingId = id;
                    _pendingReadySince = -1f;
                    _pendingDeadline = Time.unscaledTime + 5f;
                    return;
                }
            }
            Play(playerAI, id);
        }

        // A wheel choice waiting for the game to finish putting the player on the spot.
        private static int _pendingId = -1;

        private static float _pendingReadySince = -1f;

        private static float _pendingDeadline;

        // A click on an activity's seat: walking to the floor next to it, to be seated on arrival.
        private static MovePointInfo _walkingToBorrow;

        private static int _walkingToTable, _walkingToJob;

        private static Vector3 _walkingToFloor;

        private static float _walkingToDeadline;

        // Kept on the seat. A seat is off the walkable floor, and the character's walker (the
        // path-following component) puts whoever it moves back onto the floor. The game stops
        // that for the seats it uses itself, as long as the animation is one of the seat's
        // own; it does not for an activity's seat we sat the player on by hand, nor once
        // something else is played (a meal at any of the cafe's tables). From then until the
        // player leaves, the walker is told not to place the player, and the player is put
        // back where they sat should anything else move them.
        private static bool _held;

        private static bool _heldLogged;

        private static MovePointInfo _heldSeat;

        private static SV.Chara.AI _heldPlayer;

        private static Vector3 _heldAt;

        private static Quaternion _heldRotation;

        private static IntPtr _heldTarget;

        private static int _heldMap;

        private static SVRichAI _heldWalker;

        private static bool _walkerPlaced, _walkerTurned;


        private static IntPtr TargetOf(SV.Chara.AI playerAI)
        {
            var point = playerAI.BehaviourCtrl?.target?.pInfo;
            return point != null ? point.Pointer : IntPtr.Zero;
        }

        private static void Hold(SV.Chara.AI playerAI, MovePointInfo seat)
        {
            var bctrl = playerAI.BehaviourCtrl;
            _held = true;
            _heldLogged = false;
            _heldSeat = seat;
            _heldPlayer = playerAI;
            _heldAt = playerAI.transform.position;
            _heldRotation = playerAI.transform.rotation;
            _heldTarget = TargetOf(playerAI);
            _heldMap = bctrl != null ? bctrl.NowMapID : -1;
            _heldWalker = bctrl?.SVRichAI;
            if (_heldWalker != null)
            {
                _walkerPlaced = _heldWalker.updatePosition;
                _walkerTurned = _heldWalker.updateRotation;
            }
            StillWalker();
        }

        private static void StillWalker()
        {
            if (_heldWalker == null) return;
            if (_heldWalker.updatePosition) _heldWalker.updatePosition = false;
            if (_heldWalker.updateRotation) _heldWalker.updateRotation = false;
        }

        /// <summary>The player is leaving the seat, or is being seated anew.</summary>
        private static void Release()
        {
            if (_held && _heldWalker != null)
            {
                _heldWalker.updatePosition = _walkerPlaced;
                _heldWalker.updateRotation = _walkerTurned;
            }
            _held = false;
            _heldSeat = null;
            _heldPlayer = null;
            _heldWalker = null;
            _borrowed = null;
        }

        private static void Pin()
        {
            var player = _heldPlayer;
            if (player == null) { Release(); return; }
            StillWalker();

            var t = player.transform;
            float moved = Vector3.Distance(t.position, _heldAt);
            if (moved < 0.01f && Quaternion.Angle(t.rotation, _heldRotation) < 1f) return;
            if (!_heldLogged && moved >= 0.01f)
            {
                _heldLogged = true;
                Notice.Log($"Idle: the game moved the seated player {moved:0.00} m (to {t.position}); keeping them on the seat.");
            }
            t.SetPositionAndRotation(_heldAt, _heldRotation);
        }

        /// <summary>After the game's movement step for a character: the held player goes back.</summary>
        internal static void FixedUpdate(Pathfinding.AIBase ai)
        {
            if (_held && _heldWalker != null && ai.Pointer == _heldWalker.Pointer) Pin();
        }

        /// <summary>Whether the player is sitting on this very seat.</summary>
        internal static bool SeatedOn(SV.Chara.AI playerAI, MovePointInfo spot)
        {
            var seat = CurrentSeat(playerAI);
            return seat != null && spot != null && seat.Pointer == spot.Pointer;
        }

        /// <summary>
        /// A choice from the wheel that belongs on a spot next to the player. The game is
        /// putting the player there and will start an animation of its own choosing; ours has
        /// to come after that, or the game's replaces it, and the spot's position is not yet
        /// the player's until then.
        /// </summary>
        private static void PendingChoice(SV.Chara.AI playerAI)
        {
            if (_pendingId < 0) return;

            var manager = SingletonInitializerAsync<AnimationCtrlManager>.Instance;
            bool ready = false;
            if (manager != null)
            {
                if (_borrowed != null) ready = true;        // seated by hand: nothing to wait for
                else if (_usedSpot != null && TargetOf(playerAI) == _usedSpot.Pointer)
                {
                    var own = new List<int>();
                    AddAnimations(_usedSpot, own);
                    // Should the game not go by the narrowed offer after all, it will be in
                    // one of the spot's usual ones: settled all the same.
                    if (_forcedOffer != null)
                        for (int k = 0; k < _forcedOffer.Count; k++)
                            if (!own.Contains(_forcedOffer[k].animMotion)) own.Add(_forcedOffer[k].animMotion);
                    foreach (int id in own)
                        if (manager.IsPlayMotion(playerAI.BehaviourCtrl, id)) { ready = true; break; }
                }
            }

            if (!ready)
            {
                _pendingReadySince = -1f;
                if (Time.unscaledTime <= _pendingDeadline) return;
                Notice.Log($"Idle: the game did not settle the player on the spot in time, so {Name(_pendingId)} was not played.");
                _pendingId = -1;
                Unforce();
                return;
            }
            if (_pendingReadySince < 0f) _pendingReadySince = Time.unscaledTime;
            if (Time.unscaledTime - _pendingReadySince < 0.35f) return;

            int chosen = _pendingId;
            _pendingId = -1;
            Unforce();
            // The game started it itself, the spot offering nothing else; if it did not after
            // all, it is played now.
            if (manager.IsPlayMotion(playerAI.BehaviourCtrl, chosen))
            {
                Notice.Log($"Idle: settled in {chosen} ({Name(chosen)}).");
                Playing(playerAI, chosen);
            }
            else Play(playerAI, chosen);
        }

        /// <summary>A click on an activity's seat: walk to the floor beside it, then sit by hand.</summary>
        internal static void WalkToBorrowed(SV.Chara.AI playerAI, MovePointInfo spot, int table, int job)
        {
            if (!Walker.Snap(spot.transform.position, 3f, out var floor, out _)) return;
            Release();
            Follower.Stop("walk click", playerAI);
            _walkingToBorrow = spot;
            _walkingToTable = table;
            _walkingToJob = job;
            _walkingToFloor = floor;
            _walkingToDeadline = Time.unscaledTime + 30f;
            Notice.Log($"Walk: to the {TableNames[table]} table's seat '{spot.name}', activity {(MovePointInfo.JobKind)job}.");
            Walker.WalkTo(playerAI, floor, playerAI.BehaviourCtrl.NowMapID);
        }

        private static void PendingBorrow(SV.Chara.AI playerAI)
        {
            if (_walkingToBorrow == null) return;
            var bctrl = playerAI.BehaviourCtrl;
            // Called off: another click moved the walk's target, or it took too long.
            if (Time.unscaledTime > _walkingToDeadline || !Walker.IsOurTarget(bctrl) ||
                Vector3.Distance(Walker.MarkerPosition, _walkingToFloor) > 0.05f)
            {
                _walkingToBorrow = null;
                return;
            }
            if (Walker.IsWalking(bctrl) ||
                Vector3.Distance(playerAI.transform.position, _walkingToFloor) > 0.5f) return;

            var spot = _walkingToBorrow;
            _walkingToBorrow = null;
            UseBorrowed(playerAI, spot, _walkingToTable, _walkingToJob);
        }

        private static void SeatUpkeep(SV.Chara.AI playerAI)
        {
            PendingChoice(playerAI);
            PendingBorrow(playerAI);

            // Being moved by hand: the keys, the stick, or the mouse button that walks forward.
            bool byHand = ThirdPersonController.Handling;

            if (_held)
            {
                // Leaving: by hand, by a walk of ours or the game's, another map, a conversation.
                var bctrl = playerAI.BehaviourCtrl;
                bool leaving = byHand || bctrl == null || bctrl.NowMapID != _heldMap ||
                               TargetOf(playerAI) != _heldTarget || Walker.IsWalking(bctrl) ||
                               ThirdPersonController.InConversation() || ThirdPersonController.InH();
                if (leaving) Release();
                else Pin();
            }

            // Walked off a seat by hand. The game still has the seat as the player's target and
            // sits them back on it, body and ring, the next time they stand still. The body is
            // reset on every frame of hand movement (MovePlayer) and the ring is not, so the
            // ring trailed beside the player from then on. So: off the seat the way the game
            // does it at the start of a walk of its own, and a target of ours in its place.
            var seat = byHand ? CurrentSeat(playerAI) : null;
            if (seat != null)
            {
                _pendingId = -1;
                Notice.Log("Idle: walked off the seat by hand.");
                try
                {
                    if (!GameChara.RestoreOffset(playerAI.BehaviourCtrl, true)) playerAI.RestoreObjectsPosition();
                    Walker.Retarget(playerAI);
                }
                catch (Exception e) { Notice.Log("Idle: could not take the player off the seat properly (" + e.Message + ")."); }
            }

            if (_pendingId < 0 && _forcedDetail != null) Unforce();
        }

        /// <summary>
        /// The seat or other special spot the mouse points at. Judged by how close the line
        /// of sight passes to it, not by what the click hits: most props have nothing to hit.
        /// </summary>
        /// <param name="seatTable">0 for a wander seat, which the game seats the player on; else the
        /// activity table the seat belongs to (see WalkToBorrowed).</param>
        internal static bool SpotUnderMouse(Camera cam, SV.Chara.AI playerAI,
                                            out MovePointInfo spot, out int job, out int seatTable)
        {
            spot = null;
            job = -1;
            seatTable = 0;
            var table = Spots(playerAI, out _);
            if (table == null) return false;

            var taken = Taken(playerAI);
            var ray = cam.ScreenPointToRay(Input.mousePosition);
            float best = Plugin.SpotClickSize.Value;
            foreach (var pair in table)
            {
                var points = pair.Value?.points;
                if (points == null) continue;
                for (int i = 0; i < points.Count; i++)
                {
                    var point = points[i];
                    if (point == null || taken.Contains(point.Pointer) || !IsSeat(point, pair.Key)) continue;

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
            if (spot != null) return true;

            // No wander seat under the mouse: the activities' seats, as NearbySpot does.
            for (int t = 1; t <= 4; t++)
            {
                var activity = Table(t);
                if (activity == null) continue;
                foreach (var pair in activity)
                {
                    var points = pair.Value?.points;
                    if (points == null) continue;
                    for (int i = 0; i < points.Count; i++)
                    {
                        var point = points[i];
                        if (point == null) continue;
                        var detail = Detail(point, t, pair.Key);
                        if (detail == null || taken.Contains(point.Pointer) || !SeatDetail(detail)) continue;
                        var seatAt = detail.charactorOffset != null ? detail.charactorOffset.position
                                                                    : point.transform.position;
                        var to = seatAt + Vector3.up * SpotHeight - ray.origin;
                        if (Vector3.Dot(to, ray.direction) <= 0f) continue;
                        float distance = Vector3.Cross(ray.direction, to).magnitude;
                        if (distance >= best) continue;
                        best = distance;
                        spot = point;
                        job = pair.Key;
                        seatTable = t;
                    }
                }
            }
            return spot != null;
        }

        /// <summary>The seat or other special spot within reach of the player, if any.</summary>
        internal static MovePointInfo NearbySpot(SV.Chara.AI playerAI, out int job, float reach = SpotReach)
        {
            job = -1;
            _nearbyTable = 0;
            MovePointInfo spot = null;
            if (!Plugin.ThirdPersonSpots.Value) return null;
            var table = Spots(playerAI, out _);
            if (table == null) return null;

            var taken = Taken(playerAI);
            var feet = playerAI.transform.position;
            float best = reach;
            foreach (var pair in table)
            {
                var points = pair.Value?.points;
                if (points == null) continue;
                for (int i = 0; i < points.Count; i++)
                {
                    var point = points[i];
                    if (point == null || taken.Contains(point.Pointer) || !IsSeat(point, pair.Key)) continue;
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
            if (spot != null) return spot;

            // No wander seat here: the seats of the map's activities (the classroom's desks for
            // Study, the cafe's tables for a meal), which the game only uses during them.
            for (int t = 1; t <= 4; t++)
            {
                var activity = Table(t);
                if (activity == null) continue;
                foreach (var pair in activity)
                {
                    var points = pair.Value?.points;
                    if (points == null) continue;
                    for (int i = 0; i < points.Count; i++)
                    {
                        var point = points[i];
                        if (point == null) continue;
                        var detail = Detail(point, t, pair.Key);
                        if (detail == null || taken.Contains(point.Pointer) || !SeatDetail(detail)) continue;
                        var seatAt = detail.charactorOffset != null ? detail.charactorOffset.position
                                                                    : point.transform.position;
                        float distance = Mathf.Min(Vector3.Distance(point.transform.position, feet),
                                                   Vector3.Distance(seatAt, feet));
                        if (distance >= best) continue;
                        best = distance;
                        spot = point;
                        job = pair.Key;
                        _nearbyTable = t;
                    }
                }
            }
            return spot;
        }

        /// <summary>
        /// Sits the player on an activity's seat by hand: onto the seat's own spot, facing its
        /// way, in the first animation the activity lists there.
        /// </summary>
        /// <param name="play">The animation to sit in; the activity's first when not given.</param>
        private static void UseBorrowed(SV.Chara.AI playerAI, MovePointInfo spot, int table, int job,
                                        int play = -1)
        {
            var detail = Detail(spot, table, job);
            if (detail == null) return;
            Release();
            var ids = new List<int>();
            for (int i = 0; i < detail.animations.Count; i++)
                if (!ids.Contains(detail.animations[i].animMotion)) ids.Add(detail.animations[i].animMotion);

            Follower.Stop("using a spot", playerAI);
            playerAI.BehaviourCtrl.Stop(true);
            var where = detail.charactorOffset != null ? detail.charactorOffset : spot.transform;
            Notice.Log($"Idle: borrowing '{spot.name}' from the {TableNames[table]} table, activity " +
                       $"{(MovePointInfo.JobKind)job}: point at {spot.transform.position}, seat at {where.position}" +
                       $"{(detail.charactorOffset != null ? "" : " (no offset)")}, prop '{detail.moveObjectName}'.");

            playerAI.position = spot.transform.position;
            playerAI.transform.SetPositionAndRotation(where.position, where.rotation);
            _borrowed = spot;
            _borrowedAt = where.position;
            _borrowedIds = ids;
            Play(playerAI, play >= 0 ? play : ids[0]);
            // Nothing in the game keeps a character here outside the activity: we do.
            if (!_held) Hold(playerAI, spot);
        }

        /// <summary>
        /// Third person: sit on, or otherwise use, this spot within reach. The player is put on
        /// the spot first, so the game's walk there is over at once and it goes straight to
        /// seating the character: walking the last step by its own steering sometimes left
        /// the walk animation running (as at doorways, where the same is done).
        /// </summary>
        private static void UseSpot(SV.Chara.AI playerAI, MovePointInfo spot, int job)
        {
            if (_nearbyTable != 0)
            {
                UseBorrowed(playerAI, spot, _nearbyTable, job);
                return;
            }
            Release();
            Follower.Stop("using a spot", playerAI);
            NoteSpot(spot, job);
            Notice.Log($"Idle: using {Describe(spot)}, job {job}.");
            playerAI.BehaviourCtrl.Stop(true);
            playerAI.position = spot.transform.position;
            Walker.WalkToPoint(playerAI, spot, playerAI.BehaviourCtrl.NowMapID, job);
        }

        /// <summary>Once per map, with Debug Info on: every kind of spot and its animations.</summary>
        private static void ListMapAnimations(SV.Chara.AI playerAI)
        {
            var table = Spots(playerAI, out int mapId);
            if (table == null || mapId == _listedMap) return;
            _listedMap = mapId;

            // "Chair, activity None" -> spots counted, animation ids.
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
            for (int t = 1; t <= 4; t++)
            {
                var activity = Table(t);
                if (activity == null) continue;
                foreach (var pair in activity)
                {
                    var points = pair.Value?.points;
                    if (points == null) continue;
                    int seats = 0;
                    var ids = new List<int>();
                    for (int i = 0; i < points.Count; i++)
                    {
                        var point = points[i];
                        if (point == null) continue;
                        var detail = Detail(point, t, pair.Key);
                        if (detail == null || !SeatDetail(detail)) continue;
                        seats++;
                        for (int k = 0; k < detail.animations.Count; k++)
                            if (!ids.Contains(detail.animations[k].animMotion)) ids.Add(detail.animations[k].animMotion);
                    }
                    sb.Append("\n  ").Append(TableNames[t]).Append(" table, activity ")
                      .Append((MovePointInfo.JobKind)pair.Key).Append(": ").Append(points.Count)
                      .Append(" points, ").Append(seats).Append(" seats:");
                    foreach (int id in ids) sb.Append(' ').Append(id).Append('=').Append(Name(id)).Append(',');
                }
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
            var details = point.urouroDetails;
            if (details != null)
                for (int i = 0; i < details.Count; i++)
                {
                    var detail = details[i];
                    if (detail == null) continue;
                    sb.Append(" {").Append(detail.job).Append(':');
                    var animations = detail.animations;
                    if (animations != null)
                        for (int k = 0; k < animations.Count; k++)
                            sb.Append(' ').Append(Name(animations[k].animMotion)).Append('x').Append(animations[k].weight);
                    if (detail.charactorOffset != null) sb.Append(", offset");
                    if (!string.IsNullOrEmpty(detail.moveObjectName)) sb.Append(", prop ").Append(detail.moveObjectName);
                    sb.Append('}');
                }
            return sb.ToString();
        }
    }
}
