using System;
using System.Collections.Generic;
using SV;
using UnityEngine;

namespace SVS_FreeRoam
{
    /// <summary>
    /// Places the third-person camera, and remembers each location's own overview camera so it
    /// can be handed back.
    ///
    /// The original orbited the player's FEET while rotating an offset that already contained
    /// the camera height, so the real elevation angle was not the pitch value and the view
    /// curved over and flipped when zoomed in. This orbits the look target instead, which makes
    /// pitch mean exactly what it says at any distance, and sets the rotation directly rather
    /// than using LookAt so there is no undefined up-vector at straight up or down.
    /// </summary>
    internal static class CameraRig
    {
        private const float BaseFov = 50f;

        private static float _pitch, _yaw;
        private static float _distance = 3f, _targetDistance = 3f;
        // Dragging the camera with the right mouse button, as AC_MainCameraExtension does:
        // held, the mouse moves the camera instead of turning it. Up and down raise it, left
        // and right bring it in and out; with the left button held as well, up and down are in
        // and out and left and right slide it sideways. A short click is still the button's
        // own function (interact). Reset View puts it all back.
        private const float TapTime = 0.25f;
        private const float DragHeightSpeed = 0.03f, DragZoomSpeed = 0.15f, DragLimit = 1.5f;
        private static float _rightDownAt = -1f;
        private static bool _dragging, _dragInUse;
        private static int _tapFrame = -1;
        private static float _raise, _raiseWanted, _side, _sideWanted;

        /// <summary>The right button is held and dragging the camera.</summary>
        internal static bool Dragging => _dragging;

        /// <summary>The right button is the drag button just now, so its own function waits for a short click.</summary>
        internal static bool DragButtonInUse => _dragInUse;

        /// <summary>The right button was clicked, not dragged, and let go this frame.</summary>
        internal static bool RightTapped => _tapFrame == Time.frameCount;

        /// <param name="active">Third person is driving and the cursor is locked.</param>
        internal static void TrackDrag(bool active)
        {
            // Not where the right button is the one that walks forward.
            _dragInUse = active && Plugin.Mode.Value != ForwardMode.RightClickForward && !ClickIdler.BlocksInput;
            if (!_dragInUse)
            {
                _rightDownAt = -1f;
                _dragging = false;
                return;
            }

            if (Input.GetMouseButtonDown(1))
            {
                _rightDownAt = Time.unscaledTime;
                _dragging = false;
            }
            if (_rightDownAt < 0f) return;

            if (!Input.GetMouseButton(1))
            {
                if (!_dragging) _tapFrame = Time.frameCount;
                _rightDownAt = -1f;
                _dragging = false;
                return;
            }

            if (!_dragging && Time.unscaledTime - _rightDownAt > TapTime) _dragging = true;
            if (!_dragging) return;

            float dx = Input.GetAxis("Mouse X"), dy = Input.GetAxis("Mouse Y");
            float zoom;
            if (Input.GetMouseButton(0))
            {
                zoom = dy;
                _sideWanted = Mathf.Clamp(_sideWanted + dx * DragHeightSpeed, -DragLimit, DragLimit);
            }
            else
            {
                zoom = dx;
                _raiseWanted = Mathf.Clamp(_raiseWanted + dy * DragHeightSpeed, -DragLimit, DragLimit);
            }
            _targetDistance = Mathf.Clamp(_targetDistance - zoom * DragZoomSpeed,
                                          Plugin.MinZoom.Value, Plugin.MaxZoom.Value);
        }

        private static float _distanceBefore;          // where the camera was when the first person key took it in
        private static float _fov = BaseFov, _targetFov = BaseFov;
        private static bool _started;

        private static Vector3 _smoothedPivot, _smoothedCamera;
        private static bool _havePivot, _haveCameraPos;

        private static float _effectiveDistance = 3f;
        private static bool _manualHide;
        private static bool _hidingCharacter;
        private static bool _crouchLatched;
        private static bool _ignoreHeldCrouch;
        private static float _lastCrouchTap = -10f;

        // Collision probe, fixed: the thinnest probe that still catches thin walls, and no
        // gap in front of a surface. Both used to be settings nobody needed to change.
        private const float CollisionProbeRadius = 0.01f;
        private const float CollisionBuffer = 0f;

        // ---- overview camera, learned per location ---------------------------
        private struct Pose
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public float Fov;
        }

        private static readonly Dictionary<int, Pose> _mapPoses = new Dictionary<int, Pose>();
        private static bool _seeded;
        private static bool _povUsedSinceMapChange;
        private static Vector3 _poseAtMapChange, _settleReference;
        private static bool _sawPoseMove;
        private static int _settleFrames;
        private const int SettleFrames = 6;

        // ---- learning while third-person is on ------------------------------
        // Where Place last put the camera. If it is somewhere else at the start of the next
        // pass, something other than the rig moved it; shortly after arriving somewhere,
        // that is the game setting up the location's overview camera.
        private static Vector3 _placedPosition;
        private static Quaternion _placedRotation;
        private static bool _havePlaced;
        private static bool _placedThisSession;
        private static int _framesSinceMapChange;
        private static int _learningInPovFor = int.MinValue;

        /// <summary>How long after arriving the game's camera placement is watched for.</summary>
        private const int PovLearnWindowFrames = 600;

        /// <summary>
        /// Overview camera poses for the seven story locations, measured in game. Custom maps
        /// are absent by necessity -- their ids depend on what is installed -- and are learned
        /// instead. Any of these is replaced the first time the real pose is observed.
        /// </summary>
        private static readonly (int Map, Vector3 Pos, Vector3 Rot, float Fov)[] BuiltIn =
        {
            (0, new Vector3(  16.60f, 3.40f,  69.52f), new Vector3( 6.02f, 241.44f,   0.00f), 50f),
            (1, new Vector3(  50.28f, 2.42f,  -2.84f), new Vector3(16.35f, 139.98f,   0.00f), 55f),
            (2, new Vector3( 110.09f, 2.83f,  67.59f), new Vector3(11.28f, 177.10f, 359.97f), 35f),
            (3, new Vector3( -12.06f, 2.59f, 161.14f), new Vector3( 4.28f, 151.60f,   0.00f), 34f),
            (4, new Vector3(  -2.57f, 1.68f, 187.09f), new Vector3( 8.11f, 336.01f, 359.76f), 55f),
            (5, new Vector3( -88.30f, 1.43f,  90.72f), new Vector3( 5.75f, 179.95f,   0.04f), 35f),
            (6, new Vector3(-147.11f, 3.49f,  71.41f), new Vector3( 8.18f, 218.25f,   0.29f), 35f),
        };

        /// <summary>Where the camera sits when third-person first starts, and what Reset View returns to.</summary>
        private static float StartDistance =>
            Mathf.Clamp(3f, Plugin.MinZoom.Value, Plugin.MaxZoom.Value);

        internal static void Reset()
        {
            _havePlaced = false;
            _havePivot = false;
            _haveCameraPos = false;
        }

        // ------------------------------------------------------------ overview

        /// <summary>Called on arrival at a new location, before anything moves the camera.</summary>
        internal static void OnMapChanged(Camera cam)
        {
            _framesSinceMapChange = 0;
            _learningInPovFor = int.MinValue;
            _povUsedSinceMapChange = false;
            _sawPoseMove = false;
            _settleFrames = 0;
            _poseAtMapChange = cam != null ? cam.transform.position : Vector3.zero;
            _settleReference = _poseAtMapChange;
        }

        private static void Seed()
        {
            if (_seeded) return;
            _seeded = true;


            foreach (var e in BuiltIn)
                if (!_mapPoses.ContainsKey(e.Map))
                    _mapPoses[e.Map] = new Pose
                    {
                        Position = e.Pos,
                        Rotation = Quaternion.Euler(e.Rot),
                        Fov = e.Fov,
                    };
        }

        /// <summary>
        /// Learns a location's overview pose by watching the game place it.
        ///
        /// Two traps. The map id changes 17 to 72 frames before the camera moves, so reading it
        /// on arrival gives the PREVIOUS location's pose. And simply waiting for the pose to
        /// hold still is not enough either, because the previous pose is perfectly still for
        /// that whole gap -- the camera has to be seen MOVING first.
        /// </summary>
        internal static void LearnOverview(Camera cam, int mapId, bool povRunning)
        {
            Seed();
            if (_framesSinceMapChange < int.MaxValue) _framesSinceMapChange++;

            if (povRunning) { _povUsedSinceMapChange = true; return; }
            if (Plugin.OverviewRestore.Value != OverviewRestoreMode.On) return;
            if (_povUsedSinceMapChange || mapId == int.MinValue) return;
            if (Time.timeScale == 0f) return;
            if (_mapPoses.ContainsKey(mapId)) return;

            var position = cam.transform.position;

            if (!_sawPoseMove)
            {
                if ((position - _poseAtMapChange).sqrMagnitude < 1e-4f) return;
                _sawPoseMove = true;
                _settleReference = position;
                _settleFrames = 0;
                return;
            }

            if ((position - _settleReference).sqrMagnitude > 1e-6f)
            {
                _settleReference = position;
                _settleFrames = 0;
                return;
            }

            if (++_settleFrames != SettleFrames) return;

            _mapPoses[mapId] = new Pose
            {
                Position = position,
                Rotation = cam.transform.rotation,
                Fov = cam.fieldOfView,
            };
            Notice.Log($"Learned overview camera for map {mapId}: {position}");
        }

        /// <summary>
        /// Called at the start of each third-person pass, before Place moves the camera.
        ///
        /// Arriving somewhere new in third-person meant its overview pose was never learned:
        /// LearnOverview only watches while the overview camera is up, so switching
        /// third-person off there had nothing to restore. The game still places its overview
        /// camera on arrival -- the rig just moves it away again the same frame. Catching the
        /// camera away from where the rig left it, soon after a map change, catches that.
        /// Every such move inside the window updates the pose, so if the game eases the camera
        /// in over several frames the last, settled position is the one kept.
        /// </summary>
        internal static void WatchForOverviewInPov(Camera cam, int mapId)
        {
            Seed();
            if (cam == null || mapId == int.MinValue) return;

            // Before the rig has placed the camera even once this session -- loading straight
            // into a location with third-person already on -- there is nothing to compare
            // against, but nothing else has had the camera either: wherever it is, the game put
            // it there. Otherwise (a conversation just ended, or third-person was just switched
            // on) the camera's position says nothing about the overview, so wait for Place.
            bool firstEver = !_placedThisSession;
            if (!_havePlaced && !firstEver) return;
            if (Plugin.OverviewRestore.Value != OverviewRestoreMode.On) return;
            if (_framesSinceMapChange > PovLearnWindowFrames) return;
            if (_mapPoses.ContainsKey(mapId) && _learningInPovFor != mapId) return;

            var t = cam.transform;
            bool moved = firstEver ||
                         (t.position - _placedPosition).sqrMagnitude > 0.25f ||
                         Quaternion.Angle(t.rotation, _placedRotation) > 5f;
            if (!moved) return;

            _mapPoses[mapId] = new Pose
            {
                Position = t.position,
                Rotation = t.rotation,
                Fov = cam.fieldOfView,
            };
            if (_learningInPovFor != mapId)
                Notice.Log($"Learned overview camera for map {mapId} while in third-person: {t.position}");
            _learningInPovFor = mapId;
        }

        /// <summary>Something else (a conversation, an H scene) is placing the camera.</summary>
        internal static void CameraMovedElsewhere() => _havePlaced = false;

        /// <summary>Hands the location's own camera back when leaving third-person.</summary>
        internal static void RestoreOverview(Camera cam, int mapId)
        {
            if (Plugin.OverviewRestore.Value != OverviewRestoreMode.On) return;
            if (!_mapPoses.TryGetValue(mapId, out var pose)) return;

            cam.transform.position = pose.Position;
            cam.transform.rotation = pose.Rotation;
            cam.fieldOfView = pose.Fov;
        }

        // -------------------------------------------------------------- camera

        /// <summary>
        /// <paramref name="applyInput"/> is false while the cursor is free for clicking a
        /// location button, so moving the mouse to reach one does not spin the camera.
        /// </summary>
        private static Vector3 _checkPosition;
        private static int _checkFrame = -10;
        private static int _movedFrames;
        private static bool _warnedMoved;

        internal static void Place(Camera cam, SV.Chara.AI playerAI, bool applyInput,
                                   bool edgeLook = false, bool allowZoom = true)
        {
            if (cam == null || playerAI == null) return;

            // Something else moving the camera after us, frame after frame, is another camera
            // plugin: say so once, since all the player sees is third person "not working".
            if (!_warnedMoved && _checkFrame == Time.frameCount - 1 &&
                (cam.transform.position - _checkPosition).sqrMagnitude > 0.01f)
            {
                if (++_movedFrames >= 120)
                {
                    _warnedMoved = true;
                    Plugin.Logger.LogWarning("Third person is on, but something else keeps moving the " +
                        "camera after SVS_FreeRoam places it. Another camera plugin is probably " +
                        "installed (an older third person or PoV plugin?); remove it.");
                }
            }
            else if (_checkFrame == Time.frameCount - 1) _movedFrames = 0;

            if (!_started)
            {
                _started = true;
                _targetDistance = _distance = StartDistance;
                _targetFov = _fov = BaseFov;
            }

            // Sets the targets only, so with smoothing on it eases back like a scroll would.
            if (Keys.Down(Plugin.ViewResetKey, Plugin.ViewResetKey2) ||
                (Plugin.GamepadSupport.Value &&
                 Keys.Down(Plugin.GamepadResetViewKey, Plugin.GamepadResetViewKey2)))
            {
                _targetDistance = StartDistance;
                _targetFov = BaseFov;
                _raiseWanted = _sideWanted = 0f;
            }

            // One key between first person and wherever the camera was before, as in Aicomi.
            // First person is the camera zoomed all the way in.
            if (Keys.Down(Plugin.FirstPersonKey, Plugin.FirstPersonKey2) && !ClickIdler.BlocksInput)
            {
                float closest = Plugin.MinZoom.Value;
                if (_targetDistance <= closest + 0.0001f)
                    _targetDistance = _distanceBefore > closest + 0.05f ? _distanceBefore : StartDistance;
                else
                {
                    _distanceBefore = _targetDistance;
                    _targetDistance = closest;
                }
                _targetFov = BaseFov;
            }

            float sensitivity = Plugin.LookSensitivity.Value;

            // The animation wheel is steered with the mouse; the view holds still meanwhile.
            if (applyInput && !IdleWheel.IsOpen)
            {
                // (While the right button drags the camera the mouse is moving it, not turning it.)
                if (!_dragging)
                {
                    _yaw += Input.GetAxis("Mouse X") * sensitivity;
                    _pitch -= Input.GetAxis("Mouse Y") * sensitivity;
                }
                if (allowZoom) ApplyZoom(Input.GetAxis("Mouse ScrollWheel"));

                // Degrees per second, unlike the mouse, which reports movement per frame.
                // (Not a stick that has just chosen from the animation wheel and is still tilted.)
                var stick = ClickIdler.PadSticksBusy ? Vector2.zero : Gamepad.RightStick;
                float stickRate = 150f * Plugin.GamepadLookSpeed.Value * Time.deltaTime;
                _yaw += stick.x * stickRate;
                _pitch += (Plugin.GamepadInvertY.Value ? stick.y : -stick.y) * stickRate;
            }

            // Triggers stand in for the wheel wherever the wheel zooms: left in, right out.
            // One wheel notch is 0.1 of scroll, so a rate of 1 is ten notches a second.
            if (allowZoom && (applyInput || edgeLook))
            {
                // Squared, keeping the sign: the triggers were already analog, but a straight
                // line felt nearly all-or-nothing. Squaring makes a light pull a slow creep
                // (half a pull is a quarter speed) while a full pull is unchanged.
                float pull = Gamepad.LeftTrigger - Gamepad.RightTrigger;
                pull *= Mathf.Abs(pull);
                ApplyZoom(pull * Plugin.TriggerZoomSpeed.Value * Time.deltaTime);
            }
            else if (edgeLook)
            {
                ApplyEdgeLook(sensitivity);
                if (allowZoom) ApplyZoom(Input.GetAxis("Mouse ScrollWheel"));
            }

            _pitch = Mathf.Clamp(_pitch, Plugin.MinPitch.Value, Plugin.MaxPitch.Value);

            float smoothing = Mathf.Clamp01(Plugin.CameraSmoothing.Value);
            if (_hidingCharacter && Plugin.NoSmoothingWhenHidden.Value) smoothing = 0f;

            // Both curves ease the same way (a fixed share of what is left, each moment); they
            // differ in how the slider sets the pace. Ours runs from 30 a second down to 1.5;
            // AC_MainCameraExtension's takes the slider as the time, in seconds, to cover
            // about two thirds of the way, and keeps pace when the game is paused or stutters.
            float step;
            if (smoothing <= 0f) step = 1f;
            else if (Plugin.SmoothingCurve.Value == SmoothingFeel.AicomiGlide)
                step = 1f - Mathf.Exp(-Mathf.Min(Time.unscaledDeltaTime, 0.1f) / smoothing);
            else step = 1f - Mathf.Exp(-Mathf.Lerp(30f, 1.5f, smoothing) * Time.deltaTime);

            _raise = Mathf.Lerp(_raise, _raiseWanted, step);
            _side = Mathf.Lerp(_side, _sideWanted, step);

            _distance = smoothing > 0f ? Mathf.Lerp(_distance, _targetDistance, step) : _targetDistance;
            _fov = smoothing > 0f ? Mathf.Lerp(_fov, _targetFov, step) : _targetFov;

            float height = IsCrouching() ? Plugin.CrouchHeight.Value : Plugin.CameraHeight.Value;
            var lookTarget = playerAI.transform.position + Vector3.up * (height + _raise);

            bool smoothPivot = smoothing > 0f && Plugin.SmoothingMode.Value == SmoothingType.FollowPivot;
            bool smoothCamera = smoothing > 0f && Plugin.SmoothingMode.Value == SmoothingType.CameraPosition;

            if (smoothPivot && _havePivot)
                _smoothedPivot = Vector3.Lerp(_smoothedPivot, lookTarget, step);
            else
            {
                _smoothedPivot = lookTarget;
                _havePivot = true;
            }

            var pivot = smoothPivot ? _smoothedPivot : lookTarget;
            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            pivot += rotation * Vector3.right * _side;
            var desired = pivot + rotation * new Vector3(0f, 0f, -_distance);

            if (Plugin.CameraCollision.Value)
                desired = ResolveCollision(pivot, desired, playerAI.transform);

            if (smoothCamera && _haveCameraPos)
            {
                _smoothedCamera = Vector3.Lerp(_smoothedCamera, desired, step);
                desired = _smoothedCamera;
            }
            else
            {
                _smoothedCamera = desired;
                _haveCameraPos = true;
            }

            cam.transform.position = desired;
            cam.transform.rotation = rotation;
            cam.fieldOfView = _fov;
            _checkPosition = desired;
            _checkFrame = Time.frameCount;
            _placedPosition = desired;
            _placedRotation = rotation;
            _havePlaced = true;
            _placedThisSession = true;

            _effectiveDistance = Vector3.Distance(desired, lookTarget);
        }

        /// <summary>
        /// Crouching just lowers the eye line. The game has no crouch animation, so it is
        /// offered only while the character is hidden -- a visible one would glide along at
        /// full height with the camera sunk into their chest.
        ///
        /// Held, it crouches while held; with Double-Tap To Crouch a double tap latches it,
        /// the same way the walk/run toggle works. The second tap is still held when it lands,
        /// so it is ignored until released, or it would undo the latch it just made.
        /// Leaving first person releases it either way, since there is nothing left to crouch
        /// behind.
        /// </summary>
        private static bool IsCrouching()
        {
            // The controller's crouch shares B with Back: not while a button on screen is
            // selected (B backs out of that), nor while the animation wheel is up.
            bool pad = Plugin.GamepadSupport.Value && !GamepadUI.Active && !GamepadUI.UsedBThisFrame &&
                       !ClickIdler.BlocksInput;
            bool held = Keys.Held(Plugin.CrouchKey, Plugin.CrouchKey2) ||
                        (pad && Keys.Held(Plugin.GamepadCrouchKey, Plugin.GamepadCrouchKey2));

            if (!_hidingCharacter)
            {
                _crouchLatched = false;
                return false;
            }

            var function = Plugin.CrouchFunction.Value;
            if (function == CrouchMode.WhileHeld)
            {
                _crouchLatched = false;
                _ignoreHeldCrouch = false;
                return held;
            }

            bool down = Keys.Down(Plugin.CrouchKey, Plugin.CrouchKey2) ||
                        (pad && Keys.Down(Plugin.GamepadCrouchKey, Plugin.GamepadCrouchKey2));
            if (function == CrouchMode.Toggle)
            {
                if (down) _crouchLatched = !_crouchLatched;
                _ignoreHeldCrouch = false;
                return _crouchLatched;
            }

            // Double tap: a double-tap latches, and holding crouches meanwhile.
            if (down)
            {
                if (Time.unscaledTime - _lastCrouchTap < 0.3f)
                {
                    _crouchLatched = !_crouchLatched;
                    _ignoreHeldCrouch = true;
                    _lastCrouchTap = -10f;
                }
                else
                {
                    _lastCrouchTap = Time.unscaledTime;
                }
            }

            if (!held) _ignoreHeldCrouch = false;
            return _crouchLatched || (held && !_ignoreHeldCrouch);
        }

        /// <summary>
        /// Turns the camera while the cursor sits near an edge of the screen, so the buttons
        /// stay clickable and the view can still be swung round to find one. The further into
        /// the margin the cursor goes, the faster it turns.
        /// </summary>
        private static void ApplyEdgeLook(float sensitivity)
        {
            float marginX = Screen.width * Plugin.EdgeLookMargin.Value / 100f;
            float marginY = Screen.height * Plugin.EdgeLookMargin.Value / 100f;
            if (marginX <= 1f || marginY <= 1f) return;

            var mouse = Input.mousePosition;

            // Ignore a cursor outside the window entirely, or it sticks at full speed.
            if (mouse.x < 0f || mouse.y < 0f ||
                mouse.x > Screen.width || mouse.y > Screen.height) return;

            float dx = 0f, dy = 0f;
            if (mouse.x < marginX) dx = -(marginX - mouse.x) / marginX;
            else if (mouse.x > Screen.width - marginX) dx = (mouse.x - (Screen.width - marginX)) / marginX;

            if (mouse.y < marginY) dy = -(marginY - mouse.y) / marginY;
            else if (mouse.y > Screen.height - marginY) dy = (mouse.y - (Screen.height - marginY)) / marginY;

            const float degreesPerSecond = 120f;
            _yaw += dx * sensitivity * degreesPerSecond * Time.deltaTime;
            _pitch -= dy * sensitivity * degreesPerSecond * Time.deltaTime;
        }

        /// <summary>
        /// Scrolling pulls the camera in until Min Zoom Distance. Past that there is nowhere
        /// left to move, so further scrolling narrows the field of view instead -- an optical
        /// zoom rather than pushing the camera through the character's head.
        /// </summary>
        private static void ApplyZoom(float scroll)
        {
            if (Mathf.Approximately(scroll, 0f)) return;

            bool atMinimum = _targetDistance <= Plugin.MinZoom.Value + 0.0001f;
            bool zoomingIn = scroll > 0f;

            if (Plugin.OpticalZoom.Value && (atMinimum || _targetFov < BaseFov))
            {
                if (!zoomingIn && _targetFov >= BaseFov - 0.0001f)
                {
                    _targetDistance = Mathf.Clamp(_targetDistance - scroll * 3f,
                                                  Plugin.MinZoom.Value, Plugin.MaxZoom.Value);
                    return;
                }

                _targetFov = Mathf.Clamp(_targetFov - scroll * 20f, Plugin.MinimumFov.Value, BaseFov);
                return;
            }

            _targetFov = BaseFov;
            _targetDistance = Mathf.Clamp(_targetDistance - scroll * 3f,
                                          Plugin.MinZoom.Value, Plugin.MaxZoom.Value);
        }

        // ----------------------------------------------------- character hiding

        /// <summary>
        /// Zooming all the way in only works as a first-person view if the character stops
        /// being drawn. Uses the real measured camera-to-head gap, not the internal zoom
        /// target, which lags while easing and ignores any pull-in from collision.
        /// </summary>
        internal static bool ShouldHideCharacter(bool controlling)
        {
            if (Keys.Down(Plugin.HideCharacterKey, Plugin.HideCharacterKey2)) _manualHide = !_manualHide;

            _hidingCharacter = controlling &&
                               (_manualHide || _effectiveDistance <= Plugin.HideCharacterBelow.Value);
            return _hidingCharacter;
        }

        // ---------------------------------------------------------- collision

        /// <summary>
        /// Trigger volumes are always ignored: maps are full of invisible triggers the camera
        /// should pass through, and treating them as walls made it flicker violently. A cast
        /// that starts already overlapping reports distance 0 and a meaningless normal, so
        /// those are discarded too.
        /// </summary>
        private static Vector3 ResolveCollision(Vector3 pivot, Vector3 desired, Transform playerRoot)
        {
            try
            {
                var direction = desired - pivot;
                float length = direction.magnitude;
                if (length < 0.001f) return desired;
                direction /= length;

                float radius = CollisionProbeRadius;
                var hits = Physics.SphereCastAll(pivot, radius, direction, length,
                    Plugin.CollisionLayerMask, QueryTriggerInteraction.Ignore);

                float nearest = length;
                if (hits != null)
                {
                    foreach (var hit in hits)
                    {
                        if (hit.collider == null || hit.distance <= 0.001f) continue;

                        var t = hit.transform;
                        if (t == null) continue;
                        if (playerRoot != null && t.IsChildOf(playerRoot)) continue;

                        if (Plugin.CollisionIgnoreCharacters.Value &&
                            t.GetComponentInParent<SV.Chara.Base>() != null) continue;

                        if (hit.distance < nearest) nearest = hit.distance;
                    }
                }

                // Nothing in the way: hand back the requested position untouched. Subtracting
                // the buffer unconditionally pulled the camera in by that much on every frame,
                // even in open space, which quietly shifted the measured camera-to-head gap and
                // so moved the distance at which the character hides.
                if (nearest >= length) return desired;

                float allowed = Mathf.Clamp(nearest - CollisionBuffer,
                                            Plugin.MinZoom.Value, _distance);
                return pivot + direction * allowed;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("Camera collision failed, disabling it: " + e.Message);
                Plugin.CameraCollision.Value = false;
                return desired;
            }
        }
    }
}
