using System;
using Manager;
using Pathfinding;
using SV;
using UnityEngine;

namespace SVS_FreeRoam
{
    public enum FollowSpeed
    {
        /// <summary>Run when beyond Run Distance, walk once back at Ideal Distance, at the
        /// player's own walk and run speeds.</summary>
        WalkOrRun,
        /// <summary>Move at their speed; the animation follows that speed.</summary>
        MatchSpeed,
        /// <summary>Faster the further behind, slower when near Ideal Distance; the animation
        /// follows the speed.</summary>
        Dynamic,
    }

    /// <summary>
    /// Click a character to keep following them. There is no "player follows an NPC" routine in
    /// the game, so this keeps one of the game's own walks (Walker) going toward the character,
    /// steers its destination as they move, sets its speed, and stops it when the player should
    /// wait. One continuous walk, never restarted while moving: restarting every few frames is
    /// what made v0.2.0 stutter and snap back to the running animation.
    ///
    /// Distances: Minimum -- never closer. Ideal -- where the player settles, and where running
    /// turns back into walking. Run -- beyond this, catch up (run).
    /// </summary>
    internal static class Follower
    {
        private const float TickInterval = 0.1f;
        private const float MovingSpeed = 0.2f;        // m/s: slower than this counts as standing
        private const float AnimHysteresis = 0.15f;    // m/s either side of Run Animation Above
        private const float SpeedChangeRate = 4f;      // m/s per second, how fast our speed may change
        private const float DynamicGain = 0.8f;        // m/s of extra speed per metre behind Ideal
        private const float ApproachSpeed = 1.2f;      // Match: closing in on someone standing still
        private const float ClickSlop = 8f;            // pixels a click may drift and still be a click

        private static SV.Chara.AI _npc;

        /// <summary>Someone is being followed: the walks this makes are wanted, across maps too.</summary>
        internal static bool Following => _npc != null;
        private static float _npcSpeed;
        private static Vector3 _npcLastPos;
        private static float _nextTick;

        private static bool _moving;       // we have a walk under way
        private static bool _catching;     // fell beyond Run Distance; running until back at Ideal
        private static int _issuedMap;
        private static float _lastIssue;
        private static float _notWalkingSince;
        private static Vector3 _lastSearchGoal;
        private static float _lastSearch;
        private static bool? _animRun;

        private static Vector3? _pressAt;

        // Speed override for MatchSpeed and Dynamic, applied from the AIBase.FixedUpdate postfix.
        private static bool _speedActive;
        private static IntPtr _speedAI;
        private static float _speedWanted;   // where speed is heading
        private static float _speed;         // current, eased toward _speedWanted
        private static SavedSpeed? _saved;

        private struct SavedSpeed
        {
            internal float AccelType, MaxSpeed, Acceleration, SlowSpeed;
        }

        internal static void Update(SV.Chara.AI playerAI)
        {
            HandleClick(playerAI);
            if (_npc == null) return;

            if (!Plugin.ClickFollow.Value) { Stop("follow disabled"); return; }
            if ((Scene.IsOverlap || ThirdPersonController.IsAnyMenuOpen())) { Stop("a menu, conversation or H scene opened"); return; }
            if (_npc.WasCollected || _npc.gameObject == null) { Stop("the character is gone"); return; }

            var bctrl = playerAI.BehaviourCtrl;

            // Someone coming over to talk: give way, or they wait for a player who never stops
            // and stand stuck. Halting lets the game's own approach finish into a conversation.
            var caller = Approaching(playerAI, bctrl);
            if (caller != null)
            {
                Stop(Name(caller) + " came over to talk", playerAI);
                return;
            }

            // Anything else giving the player a target -- a travel button, clicking someone to
            // talk -- ends the follow. Our own Stop() leaves the target at None, which is fine.
            if (_issuedMap != int.MinValue && bctrl.target != null &&
                bctrl.target.kind != BehaviourController.TargetInfo.TargetKind.None &&
                !Walker.IsOurTarget(bctrl))
            {
                Stop("the game gave the player another target (" + ClickWalker.Describe(bctrl) + ")");
                return;
            }

            MeasureNpcSpeed();
            _speed = Mathf.MoveTowards(_speed, _speedWanted, SpeedChangeRate * Time.deltaTime);

            if (Time.time < _nextTick) return;
            _nextTick = Time.time + TickInterval;
            Tick(playerAI, bctrl);
        }

        // ------------------------------------------------------------------ clicks

        private static void HandleClick(SV.Chara.AI playerAI)
        {
            if (!Plugin.ClickFollow.Value) { _pressAt = null; return; }

            // A click is press and release in about the same place, so a drag with this button
            // (camera turning, in some views) is never read as "follow".
            // Only a press made with the cursor free: in third person the same button is
            // interact, and the cursor is free only with the location buttons up.
            if (Keys.Down(Plugin.FollowButton, null))
                _pressAt = Cursor.lockState == CursorLockMode.Locked ||
                           ClickIdler.WheelClosedFrame == Time.frameCount   // that press chose from the wheel
                    ? (Vector3?)null : Input.mousePosition;
            if (!Keys.Up(Plugin.FollowButton, null) || _pressAt == null) return;
            // A release that just chose from the character wheel is not also a click.
            if (ClickIdler.WheelClosedFrame == Time.frameCount) { _pressAt = null; return; }

            bool dragged = (Input.mousePosition - _pressAt.Value).magnitude > ClickSlop;
            _pressAt = null;
            if (dragged) return;

            string why = Picker.WhyNotClickable();
            if (why != null)
            {
                Notice.Log("Follow click ignored: " + why + ".");
                return;
            }

            var cam = Camera.main;
            if (cam == null || !Picker.UnderMouse(cam, playerAI, out var pick) || pick.Character == null)
            {
                // Clicking anything but a character while following calls it off.
                if (_npc != null) Stop("follow button clicked away from a character", playerAI);
                return;
            }

            if (_npc != null && pick.Character.Pointer == _npc.Pointer)
            {
                Stop("clicked the same character again", playerAI);
                return;
            }

            Start(pick.Character);
        }

        internal static bool IsFollowing(SV.Chara.AI npc) =>
            _npc != null && npc != null && _npc.Pointer == npc.Pointer;

        /// <summary>Follow this character, or stop if already following them. For the character wheel.</summary>
        internal static void Toggle(SV.Chara.AI npc, SV.Chara.AI playerAI)
        {
            if (npc == null) return;
            if (IsFollowing(npc)) Stop("chosen from the wheel", playerAI);
            else Start(npc);
        }

        private static void Start(SV.Chara.AI npc)
        {
            Stop(null);
            _npc = npc;
            _npcSpeed = 0f;
            _npcLastPos = npc.transform.position;
            _nextTick = 0f;
            _moving = false;
            _catching = false;
            _issuedMap = int.MinValue;
            _animRun = null;
            _speed = _speedWanted = 0f;
            Notice.Log($"Follow: started following {Name(npc)} (mode {Plugin.FollowSpeedMode.Value}).");
        }

        /// <param name="playerAI">Given when the player should also be brought to a halt.</param>
        internal static void Stop(string reason, SV.Chara.AI playerAI = null)
        {
            if (_npc == null) return;
            if (reason != null) Notice.Log($"Follow: stopped following {Name(_npc)} -- {reason}.");
            if (playerAI != null && _moving && Walker.IsOurTarget(playerAI.BehaviourCtrl))
                playerAI.BehaviourCtrl.Stop(true);
            _npc = null;
            _moving = false;
            EndSpeedOverride();
        }

        // ------------------------------------------------------------------ per tick

        private static void MeasureNpcSpeed()
        {
            // From how far they actually moved, smoothed. Works whoever is moving them.
            var pos = _npc.transform.position;
            var delta = pos - _npcLastPos;
            _npcLastPos = pos;
            delta.y = 0f;
            if (Time.deltaTime <= 0f) return;
            float now = delta.magnitude / Time.deltaTime;
            if (now > 20f) now = _npcSpeed;   // a teleport (map change), not movement
            _npcSpeed = Mathf.Lerp(_npcSpeed, now, 1f - Mathf.Exp(-Time.deltaTime * 4f));
        }

        private static void Tick(SV.Chara.AI playerAI, BehaviourController bctrl)
        {
            var mode = Plugin.FollowSpeedMode.Value;
            float min = Plugin.FollowMinDistance.Value;
            float ideal = Mathf.Max(Plugin.FollowIdealDistance.Value, min);
            float run = Mathf.Max(Plugin.FollowRunDistance.Value, ideal + 0.5f);

            var npcPos = _npc.transform.position;
            var flat = playerAI.transform.position - npcPos;
            flat.y = 0f;
            float d = flat.magnitude;

            int npcMap = _npc.BehaviourCtrl.NowMapID;
            bool otherMap = npcMap != bctrl.NowMapID;
            bool npcMoving = _npcSpeed > MovingSpeed;

            // Catch-up: starts beyond Run Distance, lasts until back at Ideal.
            if (otherMap || d > run) _catching = true;
            else if (d <= ideal) _catching = false;

            bool want = WantToMove(mode, d, min, ideal, npcMoving, otherMap);

            if (want)
            {
                if (!Walker.Snap(npcPos, 50f, out var goal, out _)) goal = npcPos;
                KeepWalking(playerAI, bctrl, goal, npcMap);
                SetPace(playerAI, bctrl, mode, d, ideal, npcMoving);
            }
            else if (_moving)
            {
                // Standing still is the game's own stop, blending to idle.
                if (Walker.IsOurTarget(bctrl)) bctrl.Stop(true);
                _moving = false;
                _animRun = null;
                EndSpeedOverride();
                Notice.Log($"Follow: waiting at {d:0.0} m.");
            }

        }

        private static bool WantToMove(FollowSpeed mode, float d, float min, float ideal,
                                       bool npcMoving, bool otherMap)
        {
            if (otherMap || _catching) return true;
            if (d < min) return false;

            if (_moving)
            {
                // Keep closing in while they move; once they stop, settle at Ideal.
                if (!npcMoving) return d > ideal + 0.2f;
                // Walk or Run has no speed control, so if our walk is faster than theirs it can
                // only drop back: wait once well inside Ideal.
                if (mode == FollowSpeed.WalkOrRun) return d > (min + ideal) * 0.5f;
                return true;
            }

            // Standing: set off once they have moved beyond Ideal. Someone standing still
            // between Ideal and Run Distance is left alone.
            return npcMoving && d > ideal + 0.3f;
        }

        /// <summary>
        /// Starts a walk if none is under way; otherwise steers the one we have. Restarting is
        /// kept to a minimum because every restart re-picks the game's own speed and animation.
        /// </summary>
        private static void KeepWalking(SV.Chara.AI playerAI, BehaviourController bctrl, Vector3 goal, int map)
        {
            // The task name flickers between nodes, so the walk only counts as over once it has
            // looked over for half a second.
            if (Walker.IsWalking(bctrl)) _notWalkingSince = Time.time;
            bool ended = Time.time - _notWalkingSince > 0.5f && Time.time - _lastIssue > 0.5f;

            if (!_moving || map != _issuedMap || ended)
            {
                string why = !_moving ? "setting off" : map != _issuedMap ? "they are in another map" :
                             "the walk had ended";
                Notice.Log($"Follow: walking to {goal} in map {map} ({why}).");
                Walker.WalkTo(playerAI, goal, map);
                _moving = true;
                _issuedMap = map;
                _lastIssue = Time.time;
                _notWalkingSince = Time.time;
                _lastSearchGoal = goal;
                _lastSearch = Time.time;
                _animRun = null;   // the walk just started and chose its own animation
                return;
            }

            // Already walking: move the destination with them. The walker only takes a new
            // destination while it is moving, which it is here (it ignored one while idle).
            Walker.MoveMarker(goal);
            var rich = bctrl.SVRichAI;
            rich.destination = goal;
            if ((goal - _lastSearchGoal).sqrMagnitude > 0.25f && Time.time - _lastSearch > 0.25f)
            {
                rich.SearchPath();
                _lastSearchGoal = goal;
                _lastSearch = Time.time;
            }
        }

        private static void SetPace(SV.Chara.AI playerAI, BehaviourController bctrl, FollowSpeed mode,
                                    float d, float ideal, bool npcMoving)
        {
            float catchUp = Plugin.FollowCatchUpSpeed.Value;
            bool run;

            if (mode == FollowSpeed.WalkOrRun)
            {
                // The game's own walk and run speeds; we only choose which.
                EndSpeedOverride();
                run = _catching;
            }
            else
            {
                float wanted;
                if (_catching)
                    wanted = catchUp;
                else if (mode == FollowSpeed.MatchSpeed)
                    wanted = npcMoving ? _npcSpeed : ApproachSpeed;
                else
                    wanted = Mathf.Max(_npcSpeed + DynamicGain * (d - ideal), npcMoving ? 0f : 0.4f);
                _speedWanted = Mathf.Clamp(wanted, 0.3f, catchUp);
                if (!_speedActive) _speed = bctrl.SVRichAI.velocity.magnitude;
                _speedActive = true;
                _speedAI = bctrl.SVRichAI.Pointer;

                float threshold = Plugin.FollowRunAnimationAbove.Value;
                run = _animRun == true ? _speed > threshold - AnimHysteresis
                                       : _speed > threshold + AnimHysteresis;
            }

            if (_animRun != run)
            {
                _animRun = run;
                bctrl.AnimRunAndWalk(run);
            }
        }

        // ------------------------------------------------------------------ speed override

        /// <summary>
        /// Called from the AIBase.FixedUpdate postfix, where SVS_CheatTools also sets speed.
        /// accelType 1 makes the walker use maxSpeed as given (CheatTools' Fast mode).
        /// </summary>
        internal static void FixedUpdate(AIBase ai)
        {
            if (!_speedActive || ai.Pointer != _speedAI) return;
            var rich = ai.TryCast<SVRichAI>();
            if (rich == null) return;

            _saved ??= new SavedSpeed
            {
                AccelType = rich.accelType,
                MaxSpeed = rich.maxSpeed,
                Acceleration = rich.acceleration,
                SlowSpeed = rich.slowSpeed,
            };
            rich.accelType = 1f;
            rich.maxSpeed = _speed;
            rich.slowSpeed = _speed;
        }

        private static void EndSpeedOverride()
        {
            _speedActive = false;
            _speedWanted = 0f;
            if (_saved == null) return;
            var playerAI = GameChara.PlayerAI;
            var rich = playerAI != null ? playerAI.BehaviourCtrl.SVRichAI : null;
            if (rich != null)
            {
                var s = _saved.Value;
                rich.accelType = s.AccelType;
                rich.maxSpeed = s.MaxSpeed;
                rich.acceleration = s.Acceleration;
                rich.slowSpeed = s.SlowSpeed;
            }
            _saved = null;
        }

        private static float _nextApproachCheck;

        /// <summary>A character on this map who has the player as their target, close by.
        /// Checked a few times a second.</summary>
        private static SV.Chara.AI Approaching(SV.Chara.AI playerAI, BehaviourController bctrl)
        {
            if (Time.unscaledTime < _nextApproachCheck) return null;
            _nextApproachCheck = Time.unscaledTime + 0.2f;

            var list = Game.AICharas;
            if (list == null) return null;
            foreach (var ai in list)
            {
                if (ai == null || ai.Pointer == playerAI.Pointer) continue;
                var their = ai.BehaviourCtrl;
                if (their == null || their.NowMapID != bctrl.NowMapID) continue;
                var target = their.targetBehaviourCtrl;
                if (target == null || target.Pointer != bctrl.Pointer) continue;
                if (Vector3.Distance(ai.position, playerAI.position) > 6f) continue;
                return ai;
            }
            return null;
        }

        private static string Name(SV.Chara.AI ai)
        {
            try { return ai.charaData?.Name ?? ai.name; }
            catch { return ai.name; }
        }
    }
}
