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
    /// <summary>Which animations the wheel lists, and a tap picks from.</summary>
    public enum AnimationSet
    {
        Fitting,
        MapAnimations,
        Sitting,
        Favorites1,
        Favorites2,
        Favorites3,
        All,
    }

    /// <summary>
    /// Idle animations on demand, and the wheel for choosing them. The spots are the map's own
    /// "urouro" (wander) points, the ones the game sends the player to on arriving in a map;
    /// seats and other special spots are used by walking to the real point, so the game seats
    /// the character itself. Animations are played directly, by their id in
    /// AnimationCtrlManager.Animation (FINDINGS.md §28).
    /// </summary>
    internal static partial class ClickIdler
    {
        private const float ClickSlop = 8f;            // pixels a click may drift and still be a click

        private const float BodyHeight = 1.5f;         // feet to about the head, for clicks on yourself

        private const float HoldTime = 0.35f;          // seconds held before the wheel opens

        private enum Press { None, Self, Character, ThirdPerson, Gamepad, GamepadSpent }

        private static Press _press;

        private static Vector3 _pressAt;

        private static float _pressTime;

        private static SV.Chara.AI _pressCharacter;

        private static int _thirdPersonFrame;

        private static IntPtr _player;                 // whose state all this is

        // The wheel is staying up after the button was let go; a click chooses.
        private static bool _sticky;

        private static bool _stickyThirdPerson;

        // A click that chose from the wheel is nobody else's, until every button is let go.
        private static bool _swallow;

        private static int _swallowEndFrame = -1;

        private static List<Action> _actions;

        private static List<int> _wheelIds;            // the animations in the open wheel; null for other wheels

        private static int _playing = -1;

        /// <summary>The frame a wheel was closed in: that press or release is not also a click.</summary>
        internal static int WheelClosedFrame { get; private set; } = -1;

        /// <summary>The wheel has the mouse: no walking forward, interacting or click-walking.</summary>
        internal static bool BlocksInput =>
            IdleWheel.IsOpen || _swallow || _swallowEndFrame == Time.frameCount;

        private static void Play(SV.Chara.AI playerAI, int id)
        {
            if (playerAI == null) return;
            Follower.Stop("idle animation", playerAI);
            // A click-walk still under way would carry the animation along with it.
            if (Walker.IsWalking(playerAI.BehaviourCtrl)) playerAI.BehaviourCtrl.Stop(true);
            Notice.Log($"Idle: playing {id} ({Name(id)}).");

            bool played = false;
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
            Playing(playerAI, id);
        }

        /// <summary>
        /// An animation of ours is under way, started here or by the game on our say: its
        /// sound, the hold on the seat, and what a tap will stop.
        /// </summary>
        private static void Playing(SV.Chara.AI playerAI, int id)
        {
            StartVoice(playerAI, id);

            // Anything but the seat's plain pose: from here on the player is kept on the seat.
            int resting = Resting(playerAI);
            if (!_held && id != resting)
            {
                var seat = CurrentSeat(playerAI);
                if (seat != null) Hold(playerAI, seat);
            }
            _playing = id == resting ? -1 : id;
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

            // A random one of the chosen set. Seated: of those this seat offers. Standing in
            // the open: of those that need no seat, or the character would sit on air (the
            // wheel still lists them all). If that leaves nothing, the set as it is.
            var ids = SetAnimations(playerAI);
            var fitting = Fitting(playerAI);
            bool seated = CurrentSeat(playerAI) != null;
            var usable = ids.FindAll(id => seated ? fitting.Contains(id) : !NeedsSeat(id));
            if (usable.Count > 0) ids = usable;

            // Not the plain waiting poses: those are what the character does anyway.
            int resting = Resting(playerAI);
            var special = ids.FindAll(id => id != resting);
            if (special.Count > 0) ids = special;
            Play(playerAI, ids[UnityEngine.Random.Range(0, ids.Count)]);
        }

        private static void OpenAnimationWheel(List<int> ids, Vector2 centre, bool virtualPointer)
        {
            var collections = Collections();
            var labels = new List<string>();
            _actions = new List<Action>();
            _wheelIds = ids;
            foreach (int id in ids)
            {
                int chosen = id;
                labels.Add(WheelLabel(id, collections));
                _actions.Add(() => PlayChoice(GameChara.PlayerAI, chosen));
            }
            IdleWheel.Open(labels, centre, virtualPointer);
            IdleWheel.Footer = "1, 2, 3 to favorite";
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
            // The number keys 1 to 3 put the animation in that favorite collection, or take it out.
            int collection = Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1) ? 1
                           : Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2) ? 2
                           : Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3) ? 3 : 0;
            if (_wheelIds != null && highlighted != null && highlighted.Value < _wheelIds.Count &&
                collection != 0)
            {
                int id = _wheelIds[highlighted.Value];
                ToggleFavorite(id, collection);
                IdleWheel.SetLabel(highlighted.Value, WheelLabel(id, Collections()));
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

        /// <summary>
        /// With the cursor showing: a click of the idle button on your own character plays an
        /// animation (or stops it), holding it there opens the animation wheel, and holding it
        /// on another character opens their wheel.
        /// </summary>
        internal static void Update(SimulationScene scene, SV.Chara.AI playerAI)
        {
            if (playerAI.Pointer != _player)
            {
                // Another character is the player now (switched to): the seat, the animation
                // and the follow all belonged to the old one.
                _player = playerAI.Pointer;
                Cancel();
                Release();
                Unforce();
                StopVoice();
                _pendingId = -1;
                _walkingToBorrow = null;
                _playing = -1;
                _usedSpot = null;
                Follower.Stop("the player changed");
            }

            if (Notice.On) ListMapAnimations(playerAI);
            Voice(playerAI);
            SeatUpkeep(playerAI);

            if (_swallow && !Input.GetMouseButton(0) && !Input.GetMouseButton(1) && !Input.GetMouseButton(2))
            {
                _swallow = false;
                _swallowEndFrame = Time.frameCount;
            }

            if (!Plugin.ClickIdle.Value || ThirdPersonController.PlayerLed) { Cancel(); return; }
            if (GamepadIdle(playerAI)) return;
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

        // A stick that pointed at a choice is still tilted when the wheel closes. Until it has
        // been let go it must not walk the player or swing the camera.
        private static bool _sticksLatched;

        /// <summary>The wheel is up and steered with the controller: the sticks are choosing.</summary>
        internal static bool PadWheelOpen => _press == Press.Gamepad && IdleWheel.IsOpen;

        /// <summary>The sticks are the wheel's, or were a moment ago and have not been let go.</summary>
        internal static bool PadSticksBusy => PadWheelOpen || _sticksLatched;

        private static bool SticksAtRest() =>
            new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")).magnitude < 0.25f &&
            Gamepad.RightStick.magnitude < 0.25f;

        /// <summary>
        /// The controller's idle button, in either view. A tap does what the idle key does in
        /// third person (sit, get up, play, stop). Held, the wheel is up: a stick points at an
        /// animation, and Select, or letting the button go, plays it. True while the button
        /// has the say.
        /// </summary>
        private static bool GamepadIdle(SV.Chara.AI playerAI)
        {
            if (!Plugin.GamepadSupport.Value)
            {
                _sticksLatched = false;
                if (_press == Press.Gamepad || _press == Press.GamepadSpent) Cancel();
                return false;
            }
            if (_sticksLatched && SticksAtRest()) _sticksLatched = false;

            // Chosen with Select while the button is still down: nothing more until it is let go.
            if (_press == Press.GamepadSpent)
            {
                if (!Keys.Held(Plugin.GamepadIdleKey, Plugin.GamepadIdleKey2)) _press = Press.None;
                return true;
            }

            if (_press == Press.Gamepad)
            {
                if (Keys.Held(Plugin.GamepadIdleKey, Plugin.GamepadIdleKey2))
                {
                    if (IdleWheel.IsOpen) PadWheel();
                    else if (Time.unscaledTime - _pressTime >= HoldTime)
                    {
                        OpenAnimationWheel(WheelAnimations(playerAI),
                                           new Vector2(Screen.width, Screen.height) * 0.5f, false);
                        IdleWheel.PadMode = true;
                    }
                    return true;
                }

                // Let go: with the wheel up, whatever a stick points at; without it, a tap.
                _press = Press.None;
                if (IdleWheel.IsOpen) ClosePadWheel();
                else TapThirdPerson(playerAI);
                return true;
            }

            if (_press != Press.None || _sticky || IdleWheel.IsOpen ||
                !Keys.Down(Plugin.GamepadIdleKey, Plugin.GamepadIdleKey2)) return false;
            // On the pause screens the same button steps between them.
            if (Scene.IsOverlap || ThirdPersonController.IsAnyMenuOpen() || GamepadUI.OnCycledScreen) return false;

            _press = Press.Gamepad;
            _pressTime = Time.unscaledTime;
            return true;
        }

        private static void PadWheel()
        {
            // Whichever stick is tilted further points at a choice. (The left one walks at
            // other times; ThirdPersonController leaves it alone while the wheel has it.)
            var left = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            var right = Gamepad.RightStick;
            int page = Keys.Down(Plugin.GamepadNextScreenKey, Plugin.GamepadNextScreenKey2) ||
                       Gamepad.Pressed(Gamepad.DPadRight) ? 1
                     : Keys.Down(Plugin.GamepadPrevScreenKey, Plugin.GamepadPrevScreenKey2) ||
                       Gamepad.Pressed(Gamepad.DPadLeft) ? -1 : 0;
            IdleWheel.TickPad(right.magnitude > left.magnitude ? right : left, page);

            if (!Keys.Down(Plugin.GamepadSelectKey, Plugin.GamepadSelectKey2)) return;
            if (IdleWheel.Highlighted == null) return;
            ClosePadWheel();
            _press = Press.GamepadSpent;
        }

        /// <summary>Closes the controller's wheel, playing whatever a stick points at.</summary>
        private static void ClosePadWheel()
        {
            _sticksLatched = true;
            CloseWheel();
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
            // The controller's idle button has the say (ClickIdler.Update looks after it).
            if (_press == Press.Gamepad || _press == Press.GamepadSpent)
            {
                _thirdPersonFrame = Time.frameCount;
                return;
            }
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
                Release();
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
    }
}
