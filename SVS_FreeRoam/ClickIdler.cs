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
    /// <summary>What a tap of the idle button or key plays.</summary>
    public enum IdleTap
    {
        RandomAnimation,
        RandomFavorite,
        ChosenAnimation,
    }

    /// <summary>
    /// Idle animations on demand, and the wheel for choosing them. The spots are the map's own
    /// "urouro" (wander) points, the ones the game sends the player to on arriving in a map;
    /// seats and other special spots are used by walking to the real point, so the game seats
    /// the character itself. Animations are played directly, by their id in
    /// AnimationCtrlManager.Animation (FINDINGS.md §28).
    /// </summary>
    internal static class ClickIdler
    {
        private const float ClickSlop = 8f;            // pixels a click may drift and still be a click
        private const float BodyHeight = 1.5f;         // feet to about the head, for clicks on yourself
        private const float HoldTime = 0.35f;          // seconds held before the wheel opens
        private const float SpotReach = 1.5f;          // third person: how near a seat has to be
        private const float SpotHeight = 0.4f;         // where on a seat a click is aimed, above its base
        private const string Star = "★ ";

        // What the game's standing spots offer, for maps whose spots cannot be read.
        private static readonly int[] DefaultStanding = { 0, 13, 15, 24, 26, 29, 31, 34 };

        private enum Press { None, Self, Character, ThirdPerson }

        private static Press _press;
        private static Vector3 _pressAt;
        private static float _pressTime;
        private static SV.Chara.AI _pressCharacter;
        private static int _thirdPersonFrame;
        // The wheel is staying up after the button was let go; a click chooses.
        private static bool _sticky;
        private static bool _stickyThirdPerson;
        // A click that chose from the wheel is nobody else's, until every button is let go.
        private static bool _swallow;
        private static int _swallowEndFrame = -1;
        private static List<Action> _actions;
        private static List<int> _wheelIds;            // the animations in the open wheel; null for other wheels
        private static int _playing = -1;
        private static int _listedMap = -1;
        private static List<int> _all;

        /// <summary>The frame a wheel was closed in: that press or release is not also a click.</summary>
        internal static int WheelClosedFrame { get; private set; } = -1;

        /// <summary>The wheel has the mouse: no walking forward, interacting or click-walking.</summary>
        internal static bool BlocksInput =>
            IdleWheel.IsOpen || _swallow || _swallowEndFrame == Time.frameCount;

        // ------------------------------------------------------------------ names and lists

        internal static string Name(int id) =>
            CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                ((AnimationCtrlManager.Animation)id).ToString().Replace('_', ' '));

        /// <summary>
        /// Every animation one character can do alone, whatever the map: the game's whole
        /// list without moving about, the paired ones (hug, kiss...) and H.
        /// </summary>
        internal static List<int> AllAnimations()
        {
            if (_all != null) return _all;
            _all = new List<int>();
            foreach (AnimationCtrlManager.Animation animation in Enum.GetValues(typeof(AnimationCtrlManager.Animation)))
            {
                int id = (int)animation;
                string name = animation.ToString();
                if (id >= 1000 || name.Contains("_f_") || name.Contains("_m_") || name == "run" ||
                    name == "escape" || name.StartsWith("walk")) continue;
                _all.Add(id);
            }
            return _all;
        }

        private static List<int> Favorites()
        {
            var ids = new List<int>();
            foreach (string part in Plugin.FavoriteAnimations.Value.Split(','))
            {
                string name = part.Trim();
                foreach (int id in AllAnimations())
                    if (string.Equals(Name(id), name, StringComparison.OrdinalIgnoreCase) && !ids.Contains(id))
                        ids.Add(id);
            }
            return ids;
        }

        private static void ToggleFavorite(int id)
        {
            var ids = Favorites();
            if (!ids.Remove(id)) ids.Add(id);
            Plugin.FavoriteAnimations.Value = string.Join(", ", ids.ConvertAll(Name));
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

        /// <summary>The seat or other special spot the player is using, if any.</summary>
        private static MovePointInfo CurrentSeat(SV.Chara.AI playerAI)
        {
            var bctrl = playerAI.BehaviourCtrl;
            var point = bctrl?.target?.pInfo;
            return point != null && !Walker.IsOurTarget(bctrl) && !IsStanding(point) &&
                   Vector3.Distance(SeatPosition(point), playerAI.transform.position) < 1f
                ? point : null;
        }

        /// <summary>What suits where the player is: the seat's animations, or the standing ones.</summary>
        private static List<int> Fitting(SV.Chara.AI playerAI)
        {
            var seat = CurrentSeat(playerAI);
            if (seat != null)
            {
                var ids = new List<int>();
                AddAnimations(seat, ids);
                if (ids.Count > 0) return ids;
            }
            return StandingAnimations(playerAI);
        }

        /// <summary>The pose to go back to when an animation is stopped.</summary>
        private static int Resting(SV.Chara.AI playerAI)
        {
            var seat = CurrentSeat(playerAI);
            if (seat == null) return 0;
            var ids = new List<int>();
            AddAnimations(seat, ids);
            return ids.Count > 0 ? ids[0] : 0;      // a spot lists its plain waiting pose first
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
            if (playerAI == null) return;
            Follower.Stop("idle animation", playerAI);
            // A click-walk still under way would carry the animation along with it.
            if (Walker.IsWalking(playerAI.BehaviourCtrl)) playerAI.BehaviourCtrl.Stop(true);
            Notice.Log($"Idle: playing {id} ({Name(id)}).");

            bool played = false;
            if (Plugin.AnimationProps.Value)
            {
                // The game's own way of putting a character into an animation, which also
                // shows what the animation holds (a phone, a book).
                try
                {
                    var manager = SingletonInitializerAsync<AnimationCtrlManager>.Instance;
                    if (manager != null && playerAI.animator != null)
                    {
                        manager.SetAnim(playerAI.BehaviourCtrl, playerAI.animator, playerAI.charaData,
                                        id, 0.25f, true, false, true);
                        manager.SetItemVisible(playerAI.BehaviourCtrl);
                        played = true;
                    }
                }
                catch (Exception e)
                {
                    Notice.Log("Idle: the game's own animation call failed (" + e.Message + "); playing it plainly.");
                }
            }
            if (!played) playerAI.SetLowpolyAnimation(id, false, true, 0.25f, true);
            _playing = id == Resting(playerAI) ? -1 : id;
        }

        /// <summary>An animation made for a chair or a desk.</summary>
        private static bool NeedsSeat(int id)
        {
            var manager = SingletonInitializerAsync<AnimationCtrlManager>.Instance;
            if (manager?.posePtnChairIDs != null && manager.posePtnChairIDs.Contains(id)) return true;
            if (manager?.posePtnDeskIDs != null && manager.posePtnDeskIDs.Contains(id)) return true;
            string name = ((AnimationCtrlManager.Animation)id).ToString();
            return name.Contains("chair") || name.Contains("desk");
        }

        private static bool _voiceFailed;

        /// <summary>
        /// The voice lines map characters speak during some animations (exercise...). The game
        /// runs this for characters acting on their own; for the player nothing does, so we
        /// do while an animation we started is playing.
        /// </summary>
        private static void Voice(SV.Chara.AI playerAI)
        {
            if (_voiceFailed || _playing < 0 || !Plugin.AnimationProps.Value) return;
            try
            {
                if (!PlayingOurs(playerAI)) return;
                SingletonInitializerAsync<LowpolyActionVoiceManager>.Instance?.LowpolyVoiceProc(playerAI);
            }
            catch (Exception e)
            {
                _voiceFailed = true;
                Plugin.Logger.LogWarning("Animation voices are off: the game's voice call failed (" + e.Message + ").");
            }
        }

        private static bool PlayingOurs(SV.Chara.AI playerAI)
        {
            var manager = SingletonInitializerAsync<AnimationCtrlManager>.Instance;
            return _playing >= 0 && manager != null && manager.IsPlayMotion(playerAI.BehaviourCtrl, _playing);
        }

        /// <summary>
        /// A tap: stops the animation we started if it is still playing; otherwise whatever
        /// Tap Plays says.
        /// </summary>
        private static void Tap(SV.Chara.AI playerAI, bool thirdPerson)
        {
            if (PlayingOurs(playerAI))
            {
                Play(playerAI, Resting(playerAI));
                return;
            }

            var ids = Fitting(playerAI);
            switch (Plugin.TapPlays.Value)
            {
                case IdleTap.RandomFavorite:
                    var favorites = Favorites();
                    if (favorites.Count == 0)
                    {
                        Notice.Tell("No favorite animations yet: hold the button for the wheel and " +
                                    $"press {Plugin.FavoriteKey.Value} on an animation.", 6f);
                        break;
                    }
                    // Seated: the favorites this seat offers. Standing: those that need no
                    // seat, or the character would sit on air.
                    bool seated = CurrentSeat(playerAI) != null;
                    var usable = favorites.FindAll(id => seated ? ids.Contains(id) : !NeedsSeat(id));
                    if (usable.Count > 0) ids = usable;
                    break;

                case IdleTap.ChosenAnimation:
                    foreach (int id in AllAnimations())
                        if (Name(id) == Plugin.TapAnimation.Value) { Play(playerAI, id); return; }
                    break;
            }

            // Not the plain waiting poses: those are what the character does anyway.
            int resting = Resting(playerAI);
            var special = ids.FindAll(id => id != resting);
            if (special.Count > 0) ids = special;
            Play(playerAI, ids[UnityEngine.Random.Range(0, ids.Count)]);
        }

        // ------------------------------------------------------------------ wheels

        /// <summary>What the held wheel lists: what fits here, the favorites, and with
        /// Extended Animations everything else.</summary>
        private static List<int> WheelAnimations(SV.Chara.AI playerAI)
        {
            var ids = new List<int>();
            var fitting = Fitting(playerAI);
            // Seated, the seat's own come first; standing, the favorites do.
            if (CurrentSeat(playerAI) != null) ids.AddRange(fitting);
            foreach (int id in Favorites()) if (!ids.Contains(id)) ids.Add(id);
            foreach (int id in fitting) if (!ids.Contains(id)) ids.Add(id);
            if (Plugin.ExtendedAnimations.Value)
                foreach (int id in AllAnimations()) if (!ids.Contains(id)) ids.Add(id);
            return ids;
        }

        private static string WheelLabel(int id, List<int> favorites) =>
            (favorites.Contains(id) ? Star : "") + Name(id);

        private static void OpenAnimationWheel(List<int> ids, Vector2 centre, bool virtualPointer)
        {
            var favorites = Favorites();
            var labels = new List<string>();
            _actions = new List<Action>();
            _wheelIds = ids;
            foreach (int id in ids)
            {
                int chosen = id;
                labels.Add(WheelLabel(id, favorites));
                _actions.Add(() => Play(GameChara.PlayerAI, chosen));
            }
            IdleWheel.Open(labels, centre, virtualPointer);
        }

        private static void OpenCharacterWheel(SimulationScene scene, SV.Chara.AI npc, Vector2 centre)
        {
            var labels = new List<string>();
            _actions = new List<Action>();
            _wheelIds = null;

            labels.Add("Talk");
            _actions.Add(() => ThirdPersonController.WalkToCharacter(scene, GameChara.PlayerAI, npc));

            labels.Add(Follower.IsFollowing(npc) ? "Stop following" : "Follow");
            _actions.Add(() => Follower.Toggle(npc, GameChara.PlayerAI));

            if (SwitchButtonCompat.Available)
            {
                labels.Add("Switch to");
                _actions.Add(() => SwitchButtonCompat.SwitchTo(npc));
            }
            IdleWheel.Open(labels, centre, false);
        }

        /// <summary>
        /// While a wheel is open: the favorite key stars the animation under the pointer, and
        /// a left click chooses, whichever button opened the wheel. True when the wheel was
        /// closed by that click.
        /// </summary>
        private static bool WheelKeys()
        {
            if (!IdleWheel.IsOpen) return false;

            var highlighted = IdleWheel.Highlighted;
            if (_wheelIds != null && highlighted != null && highlighted.Value < _wheelIds.Count &&
                Input.GetKeyDown(Plugin.FavoriteKey.Value))
            {
                int id = _wheelIds[highlighted.Value];
                ToggleFavorite(id);
                IdleWheel.SetLabel(highlighted.Value, WheelLabel(id, Favorites()));
            }

            if (!Input.GetMouseButtonDown(0)) return false;
            CloseWheel();
            _swallow = true;
            _sticky = false;
            _press = Press.None;
            _pressCharacter = null;
            return true;
        }

        private static void CloseWheel()
        {
            var chosen = IdleWheel.Close();
            WheelClosedFrame = Time.frameCount;
            if (chosen == null || _actions == null || chosen.Value >= _actions.Count) return;
            try { _actions[chosen.Value](); }
            catch (Exception e) { Plugin.Logger.LogWarning("Wheel choice failed: " + e.Message); }
        }

        /// <summary>
        /// The button was let go with the wheel open: choose. In third person a wheel with
        /// more than one page stays up instead, until a choice is clicked.
        /// </summary>
        private static void ReleaseWheel(bool thirdPerson)
        {
            if (!thirdPerson || IdleWheel.Pages <= 1) { CloseWheel(); return; }
            _sticky = true;
            _stickyThirdPerson = true;
            IdleWheel.Sticky = true;
        }

        private static void Cancel()
        {
            _sticky = false;
            _press = Press.None;
            _pressCharacter = null;
            IdleWheel.Close();
        }

        // ------------------------------------------------------------------ cursor free

        /// <summary>
        /// With the cursor showing: a click of the idle button on your own character plays an
        /// animation (or stops it), holding it there opens the animation wheel, and holding it
        /// on another character opens their wheel.
        /// </summary>
        internal static void Update(SimulationScene scene, SV.Chara.AI playerAI)
        {
            if (Notice.On) ListMapAnimations(playerAI);
            Voice(playerAI);

            if (_swallow && !Input.GetMouseButton(0) && !Input.GetMouseButton(1) && !Input.GetMouseButton(2))
            {
                _swallow = false;
                _swallowEndFrame = Time.frameCount;
            }

            if (!Plugin.ClickIdle.Value) { Cancel(); return; }
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                // Third person's own press or wheel, unless third person stopped looking after it.
                bool thirdPersons = _press == Press.ThirdPerson || (_sticky && _stickyThirdPerson);
                if (!thirdPersons || Time.frameCount - _thirdPersonFrame > 2) Cancel();
                return;
            }
            if (_press == Press.ThirdPerson || (_sticky && _stickyThirdPerson)) Cancel();

            if (WheelKeys()) return;

            if (_sticky)
            {
                if (!IdleWheel.IsOpen) _sticky = false;
                else
                {
                    if (Keys.Down(Plugin.IdleButton, Plugin.IdleButton2)) { CloseWheel(); _sticky = false; }
                    else IdleWheel.Tick();
                    return;
                }
            }

            if (Keys.Down(Plugin.IdleButton, Plugin.IdleButton2))
            {
                Cancel();
                var cam = Camera.main;
                if (cam != null && Picker.WhyNotClickable() == null)
                {
                    Picker.UnderMouse(cam, playerAI, out var pick);
                    if (pick.Character != null)
                    {
                        if (Plugin.CharacterWheel.Value)
                        {
                            _press = Press.Character;
                            _pressCharacter = pick.Character;
                        }
                    }
                    else if (OnPlayer(cam, playerAI)) _press = Press.Self;
                    _pressAt = Input.mousePosition;
                    _pressTime = Time.unscaledTime;
                }
            }
            if (_press == Press.None) return;

            bool dragged = (Input.mousePosition - _pressAt).magnitude > ClickSlop;
            if (Keys.Held(Plugin.IdleButton, Plugin.IdleButton2))
            {
                if (!IdleWheel.IsOpen && !dragged && Time.unscaledTime - _pressTime >= HoldTime)
                {
                    if (_press == Press.Self) OpenAnimationWheel(WheelAnimations(playerAI), _pressAt, false);
                    else if (_pressCharacter != null) OpenCharacterWheel(scene, _pressCharacter, _pressAt);
                }
                IdleWheel.Tick();
                return;
            }

            // Released.
            var released = _press;
            _press = Press.None;
            _pressCharacter = null;
            if (IdleWheel.IsOpen) ReleaseWheel(false);
            else if (!dragged && released == Press.Self) Tap(playerAI, false);
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
            if (!Plugin.ThirdPersonSpots.Value) return null;
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

        /// <summary>
        /// Third person: sit on, or otherwise use, this spot within reach. The player is put on
        /// the spot first, so the game's walk there is over at once and it goes straight to
        /// seating the character: walking the last step by its own steering sometimes left
        /// the walk animation running (as at doorways, where the same is done).
        /// </summary>
        private static void UseSpot(SV.Chara.AI playerAI, MovePointInfo spot, int job)
        {
            Follower.Stop("using a spot", playerAI);
            Notice.Log($"Idle: using {Describe(spot)}, job {job}.");
            playerAI.BehaviourCtrl.Stop(true);
            playerAI.position = spot.transform.position;
            Walker.WalkToPoint(playerAI, spot, playerAI.BehaviourCtrl.NowMapID, job);
        }

        /// <summary>
        /// Third person, cursor locked: a tap of the idle key uses the seat within reach or
        /// plays an animation (or stops it), holding it opens the wheel.
        /// <paramref name="free"/>: the key has nothing else to do (no walk under way, nobody
        /// marked to walk to).
        /// </summary>
        internal static void ThirdPerson(SV.Chara.AI playerAI, bool free)
        {
            if (!Plugin.ClickIdle.Value) return;
            if ((_press != Press.None && _press != Press.ThirdPerson) || (_sticky && !_stickyThirdPerson))
                Cancel();
            _thirdPersonFrame = Time.frameCount;

            if (WheelKeys()) return;

            if (_sticky)
            {
                if (!IdleWheel.IsOpen) _sticky = false;
                else
                {
                    if (Keys.Down(Plugin.IdleKeyThirdPerson, Plugin.IdleKeyThirdPerson2)) { CloseWheel(); _sticky = false; }
                    else IdleWheel.Tick();
                    return;
                }
            }

            if (Keys.Down(Plugin.IdleKeyThirdPerson, Plugin.IdleKeyThirdPerson2) && free)
            {
                _press = Press.ThirdPerson;
                _pressTime = Time.unscaledTime;
            }
            if (_press != Press.ThirdPerson) return;

            if (Keys.Held(Plugin.IdleKeyThirdPerson, Plugin.IdleKeyThirdPerson2))
            {
                if (!IdleWheel.IsOpen && Time.unscaledTime - _pressTime >= HoldTime)
                    OpenAnimationWheel(WheelAnimations(playerAI), new Vector2(Screen.width, Screen.height) * 0.5f, true);
                IdleWheel.Tick();
                return;
            }

            // Released.
            _press = Press.None;
            if (IdleWheel.IsOpen) ReleaseWheel(true);
            else TapThirdPerson(playerAI);
        }

        /// <summary>
        /// The idle key tapped in third person: stops the animation we started; else gets up
        /// from the seat; else sits on the seat within reach; else whatever Tap Plays says.
        /// </summary>
        private static void TapThirdPerson(SV.Chara.AI playerAI)
        {
            if (PlayingOurs(playerAI)) { Tap(playerAI, true); return; }

            var seat = CurrentSeat(playerAI);
            if (seat != null)
            {
                // Leave the way a click-walk does: a walk to the floor in front of the seat.
                if (Walker.Snap(seat.transform.position, 3f, out var floor, out _))
                {
                    Notice.Log("Idle: getting up.");
                    Walker.WalkTo(playerAI, floor, playerAI.BehaviourCtrl.NowMapID);
                }
                return;
            }

            var spot = NearbySpot(playerAI, out int job);
            if (spot != null) { UseSpot(playerAI, spot, job); return; }
            Tap(playerAI, true);
        }

        // ------------------------------------------------------------------ for reference

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
