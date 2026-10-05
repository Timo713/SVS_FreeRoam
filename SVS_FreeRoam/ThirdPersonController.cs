using System;
using ILLGames.Unity.Component;
using Il2CppSystem.Collections.Generic;
using Manager;
using SV;
using SV.Chara;
using SV.CharaSelectScene;
using SV.CoordeSelectScene;
using SV.CorrelationDiagramScene;
using SV.H;
using SV.MapSelectScene;
using SV.MyRoomScene;
using UnityEngine;

namespace SVS_FreeRoam
{
    /// <summary>
    /// The heart of the plugin: one pass per frame, run as a postfix on SimulationScene.Update.
    ///
    /// This is Junh2x's original structure, kept deliberately close so its behaviour carries
    /// over, with the camera work handed to CameraRig and the location buttons to
    /// MapButtonTracker.
    /// </summary>
    internal static class ThirdPersonController
    {
        internal static bool IsPovRunning { get; private set; }

        /// <summary>
        /// True while the mouse wheel steps through characters instead of zooming. Read by the
        /// WheelTargetSelect patch, which otherwise suppresses the game's own cycling.
        /// </summary>
        internal static bool WheelCyclingActive { get; private set; }

        private static bool _handling;

        /// <summary>The player is being moved by hand this frame, not by a walk of the game's.</summary>
        internal static bool Handling => _handling;
        private static bool _running;
        private static int _lastMapId = int.MinValue;

        /// <summary>
        /// Who the player was last sent to walk to. Kept so a second press of interact cancels
        /// the trip, the way a second middle click does in the overview camera.
        /// </summary>
        private static SV.Chara.AI _walkingTo;

        private static bool _weHidCursor;
        private static bool _weHidCharacter;

        private static MapCollisionCtrl.Info _collisionCtrl;
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<SVNodeLink2> _links;

        internal static void Update(SimulationScene scene)
        {
            var cam = Camera.main;
            var mapManager = SingletonInitializerAsync<MapManager>.Instance;
            var playerAI = GameChara.PlayerAI;
            if (cam == null || mapManager == null) return;

            LoadCollisionWhenReady(mapManager);
            UntintFadeWhenDone();
            ViewMode.EnsureStarted();
            ViewMode.HandleToggle();

            int mapId = playerAI != null ? playerAI.BehaviourCtrl.NowMapID : mapManager.MapID;

            if (mapId != _lastMapId)
            {
                _lastMapId = mapId;
                _walkingTo = null;
                _arrivalPending = true;
                if (IsPovRunning) _ringMapChanged = true;
                CameraRig.OnMapChanged(cam);
                MapButtonTracker.Invalidate();
                ViewMode.OnMapChanged();
            }

            MapInfoParam mapInfo = null;
            mapManager.MapListTable?.TryGetValue(mapId, out mapInfo);

            bool flat = mapManager.Is2DMap(mapManager.MapID) ||
                        (mapInfo != null && mapInfo.IsUseTimezone2D);

            TrackMenuTrip(playerAI);
            ArrivalWalk(scene, playerAI, flat);

            // Gamepad B with nothing selected stops a walk the game is making -- to a
            // character picked on Meet Up, say -- the way right or middle click does. Not when
            // button mode just used the press, and not over an open screen.
            if (playerAI != null && Plugin.GamepadSupport.Value &&
                Keys.Down(Plugin.GamepadBackKey, Plugin.GamepadBackKey2) && !GamepadUI.Active &&
                !GamepadUI.UsedBThisFrame && !Scene.IsOverlap && !IsAnyMenuOpen())
            {
                scene.MoveStop();
                playerAI.BehaviourCtrl.Stop(true);
                _walkingTo = null;
                _handling = false;
                _menuTrip = false;
            }

            if (!ViewMode.Active || playerAI == null || flat)
            {
                AimedCharacter = null;
                Disable(cam, mapManager, mapId);

                if (playerAI != null) HandleMarkedOutsideThirdPerson(scene, playerAI);

                // Gamepad A at a doorway or job spot, where third-person's interact can't reach.
                if (playerAI != null && !Scene.IsOverlap && !IsAnyMenuOpen() && !ClickIdler.BlocksInput)
                    GamepadTravel.Run(scene, mapManager, playerAI, mapId);

                // WASD In All Views is the one setting that reaches past third-person.
                if (playerAI != null && Plugin.MoveInAllViews.Value)
                    MoveOutsideThirdPerson(cam, playerAI);
                else
                    StopHandling(playerAI);
                return;
            }

            if (!IsPovRunning) CapturePlayerRing(playerAI);
            IsPovRunning = true;
            CameraRig.LearnOverview(cam, mapId, true);

            bool buttonMode = ViewMode.ButtonsWanted;

            // The travel UI is hidden while third-person drives, unless the buttons have been
            // called up, in which case MapButtonTracker takes it over and places them.
            if (mapManager.objMapMoveUI != null && !buttonMode)
                mapManager.objMapMoveUI.active = false;

            // Only while the location buttons are up: cycling needs the cursor free and a
            // wheel that is not busy zooming.
            WheelCyclingActive = buttonMode && Plugin.WheelCycling.Value;

            bool uiOpen = IsAnyMenuOpen();

            // Before anything moves the camera this pass. Not during conversations or H,
            // whose own cameras would otherwise be mistaken for the overview.
            if (!uiOpen) CameraRig.WatchForOverviewInPov(cam, mapId);

            // Only the overlays that stage their own view of the character should hide it.
            // Select Map, Meet Up, the outfit picker and the relationship chart all draw
            // over the world, so hiding the player there just makes them vanish.
            ApplyCharacterVisibility(playerAI, ShouldHidePlayer(), WheelCyclingActive);

            bool blocked = Scene.IsOverlap || uiOpen;

            if (blocked)
            {
                // A conversation starting is how most of these walks end.
                _walkingTo = null;
                RunMenuBranch(cam, playerAI, uiOpen);
                MapButtonTracker.Run(cam, mapId, false);

                // Hidden here too. Outside button mode it already is (above); with the buttons
                // toggled on it was not, so handing them back left the game's travel UI up at
                // its fixed overview positions for the whole conversation.
                if (mapManager.objMapMoveUI != null) mapManager.objMapMoveUI.active = false;
                return;
            }

            MapButtonTracker.Run(cam, mapId, buttonMode);

            // Button mode frees the cursor so a button can be clicked. Movement continues only
            // where third-person is the only view and the player asked for it -- the overview
            // arrangement keeps standing still, exactly as before.
            bool moving = !buttonMode || ViewMode.MovementAllowedInButtonMode;
            var lookMode = buttonMode ? ViewMode.LookInButtonMode : ButtonLookMode.FreeLook;
            bool looking = lookMode == ButtonLookMode.FreeLook;
            bool edgeLook = lookMode == ButtonLookMode.ScreenEdge;
            bool cursorFree = buttonMode;

            Cursor.lockState = cursorFree ? CursorLockMode.None : CursorLockMode.Locked;
            UpdateCursor(!cursorFree);

            // The controller splits interact in two: talk (X) reaches only characters, and
            // interact (Y) only doorways and activity spots. The keyboard's does either.
            bool pad = Plugin.GamepadSupport.Value && !GamepadUI.Active;
            bool talkPad = pad && Keys.Down(Plugin.GamepadTalkKey, Plugin.GamepadTalkKey2);
            bool placePad = pad && Keys.Down(Plugin.GamepadInteractKey, Plugin.GamepadInteractKey2);
            // With the cursor free, a click of the follow button belongs to following
            // (right click on someone); interact walking to whoever is aimed at as well was
            // the two fighting over one click.
            bool followClick = cursorFree && Plugin.ClickFollow.Value &&
                               Keys.Down(Plugin.FollowButton, Plugin.FollowButton2);
            bool interactKey = Keys.Down(Plugin.InteractKey, Plugin.InteractKey2) && !followClick;
            // While the animation wheel is up the mouse is choosing from it.
            bool interactPressed = (interactKey || talkPad || placePad) && !ClickIdler.BlocksInput;
            var filter = interactKey || (talkPad && placePad) ? TargetFilter.Any
                       : talkPad ? TargetFilter.People
                       : placePad ? TargetFilter.Places
                       : TargetFilter.Any;

            // The marked character has its own key, middle click by default, as in the overview
            // camera. The game's own middle-click handler stays suppressed in third person
            // (Hooks), so this is the only thing that answers it.
            bool markedPressed = Keys.Down(Plugin.GoToMarkedKey, Plugin.GoToMarkedKey2) &&
                                 !ClickIdler.BlocksInput;
            ForgetArrivedWalk(playerAI);

            // Order matters, and it is: cancel, then marked, then anything nearby, then
            // whoever is being aimed at. The last two are handled inside FindNearestTarget,
            // which only reaches for a distant target when nothing is within arm's length.

            // Go To Marked Key behaves as middle click does in the overview camera: a different
            // marked character re-aims the walk at them; the same one, or nobody marked, stops it.
            if (markedPressed)
            {
                var marked = FindMarkedCharacter(playerAI);
                if (_walkingTo != null && !IsDifferent(marked, _walkingTo))
                    CancelWalk(scene, playerAI);
                else if (marked != null)
                    SendPlayerTo(scene, playerAI, marked);
                markedPressed = false;
            }

            // Already on the way somewhere: interact calls it off rather than starting again.
            if (interactPressed && _walkingTo != null)
            {
                CancelWalk(scene, playerAI);
                interactPressed = false;
            }

            if (moving)
            {
                var target = FindNearestTarget(scene, mapManager, playerAI, cam, filter);
                HandleInteract(scene, mapManager, playerAI, target, buttonMode, interactPressed);
                if (!cursorFree)
                    ClickIdler.ThirdPerson(playerAI, _walkingTo == null &&
                                                     FindMarkedCharacter(playerAI) == null);

                // While the cursor is free, a mouse button is for clicking things, not for
                // walking. Leaving hold-to-walk live meant that clicking a travel button was
                // read as one frame of "forward", which drove the player by hand and then
                // stopped -- cancelling the walk the button had just started.
                MovePlayer(cam, playerAI, allowMouseForward: !cursorFree && !ClickIdler.BlocksInput);
                SteadyMarker(playerAI);
            }
            else if (_handling)
            {
                playerAI.BehaviourCtrl.Stop(true);
                _handling = false;
            }

            CameraRig.Place(cam, playerAI, applyInput: looking, edgeLook: edgeLook,
                            allowZoom: !WheelCyclingActive);
            SetCharacterHidden(playerAI, CameraRig.ShouldHideCharacter(true));
        }

        // ------------------------------------------------------- arrival walk

        // On arriving somewhere by the game's own travel (a location button), it walks the
        // character on to a wander point in the new place. Third-person between 3D maps
        // never saw this, because TakeDoorway moves the character itself -- but arriving from
        // a 2D map (Asagaya Station to Station) is the game's travel, so the walk happened
        // there too. UroUroPointMove can't simply be skipped: it is the whole trip order
        // (walk to the doorway, cross, wander), and skipping it cancels the trip. Instead the
        // walk is stopped once the new map is up.
        private static bool _arrivalPending;
        private static float _arrivalStopLeft;
        private static Vector3 _arrivalLastPosition;

        /// <summary>Seconds after arriving (loading screens excluded) to keep stopping it.</summary>
        private const float ArrivalStopSeconds = 3f;

        // A trip picked on Map Select or Meet Up, or one a conversation sends you on (going
        // somewhere with someone, following them): remembered from when the screen or the
        // conversation closes until the character has stood still for a while (arrived, or
        // there was no trip after all), another conversation starts, or the player takes over.
        private static bool _menuScreenOpen;
        private static bool _sceneWasOpen;
        private static bool _menuTrip;
        private static float _menuTripSince, _menuTripStill;
        private static Vector3 _menuTripLastPosition;

        private static void TrackMenuTrip(SV.Chara.AI playerAI)
        {
            if (playerAI == null) return;
            float now = Time.unscaledTime;

            bool open = IsOpen(SingletonInitializer<MapSelect>._instance) ||
                        IsOpen(SingletonInitializer<CharaSelect>._instance);
            if (_menuScreenOpen && !open)
            {
                _menuTrip = true;
                _menuTripSince = _menuTripStill = now;
                _menuTripLastPosition = playerAI.position;
            }
            _menuScreenOpen = open;

            var adv = SingletonInitializer<ADV.ADVManager>._instance;
            bool inScene = adv != null && (adv.IsADV || adv.IsInsertADV);
            if (_sceneWasOpen && !inScene)
            {
                _menuTrip = true;
                _menuTripSince = _menuTripStill = now;
                _menuTripLastPosition = playerAI.position;
            }
            _sceneWasOpen = inScene;
            if (!_menuTrip) return;

            if (_handling || inScene)
            {
                _menuTrip = false;
                return;
            }

            // Loading screens between maps are not standing still.
            var position = playerAI.position;
            if (Scene.IsOverlap || (position - _menuTripLastPosition).sqrMagnitude > 0.0004f)
                _menuTripStill = now;
            _menuTripLastPosition = position;

            // A few seconds' grace for the walk to start, then still for two means it's over.
            if (now - _menuTripSince > 5f && now - _menuTripStill > 2f) _menuTrip = false;
        }

        private static void ArrivalWalk(SimulationScene scene, SV.Chara.AI playerAI, bool flat)
        {
            if (playerAI == null) return;

            if (_arrivalPending)
            {
                _arrivalPending = false;
                bool gamepadTrip = GamepadTravel.TakeTrip();

                // Third-person arriving on a 3D map: full control, as between 3D maps. Arriving
                // on a 2D map keeps the walk, since third-person does not run there. After a
                // gamepad trip in the other views, stopping it is an option.
                // Never on a trip picked from Map Select or Meet Up: that one crosses maps
                // on purpose and has to keep going through each of them.
                // Nor on a walk the click features made: following someone goes through
                // doorways on purpose.
                bool stop = !_menuTrip && !Follower.Following &&
                            !Walker.IsOurTarget(playerAI.BehaviourCtrl) &&
                            ((ViewMode.Active && !flat) ||
                             (gamepadTrip && Plugin.NoAutoWalkAfterGamepadTravel.Value));
                _arrivalStopLeft = stop ? ArrivalStopSeconds : 0f;
                _arrivalLastPosition = playerAI.position;
            }

            if (_arrivalStopLeft <= 0f) return;

            // Loading screens don't count, and nothing is touched during them: the game is
            // still placing the character.
            if (Scene.IsOverlap)
            {
                _arrivalLastPosition = playerAI.position;
                return;
            }

            _arrivalStopLeft -= Time.unscaledDeltaTime;

            var position = playerAI.position;
            bool moved = (position - _arrivalLastPosition).sqrMagnitude > 0.0001f;
            _arrivalLastPosition = position;

            // Only movement that isn't ours: not the player's own stick or keys, and not a walk
            // they asked for with interact or Go To Marked.
            if (moved && !_handling && _walkingTo == null)
            {
                scene.MoveStop();
                playerAI.BehaviourCtrl.Stop(true);
            }
        }

        // ------------------------------------------------------- other views

        private static float _walkStartedAt;
        private static float _stillSince;
        private static Vector3 _lastWalkPosition;

        /// <summary>
        /// A walk that is over, however it ended, must be forgotten, or the next press cancels
        /// a walk that is not happening -- which is why middle click did nothing once after
        /// a right click had stopped the walk. Over means arrived (within 1.5 m), or standing
        /// still for half a second, after a one-second grace for the walk to get going.
        /// </summary>
        private static void ForgetArrivedWalk(SV.Chara.AI playerAI)
        {
            if (_walkingTo == null) return;

            var position = playerAI.position;
            if (Vector3.Distance(position, _walkingTo.position) < 1.5f)
            {
                _walkingTo = null;
                return;
            }

            float now = Time.unscaledTime;
            if (now - _walkStartedAt < 1f || (position - _lastWalkPosition).sqrMagnitude > 0.0004f)
            {
                _lastWalkPosition = position;
                _stillSince = now;
                return;
            }
            if (now - _stillSince > 0.5f) _walkingTo = null;
        }

        private static void BeginWalk(SV.Chara.AI playerAI, SV.Chara.AI target)
        {
            _walkingTo = target;
            _walkStartedAt = _stillSince = Time.unscaledTime;
            _lastWalkPosition = playerAI.position;
        }

        /// <summary>
        /// Go To Marked Key in the overview camera and other views, so a second press stops
        /// the walk the way it does in third-person.
        ///
        /// Middle click is the game's own binding there, and its handler has already run by
        /// the time this postfix does: on the first press it started the walk, so all that is
        /// left is remembering who to. On the second it has just re-aimed the same walk, and
        /// stopping here cancels it. Bound to anything else, nothing in the game answers, so
        /// the walk is started here instead.
        /// </summary>
        private static void HandleMarkedOutsideThirdPerson(SimulationScene scene, SV.Chara.AI playerAI)
        {
            if (Scene.IsOverlap || IsAnyMenuOpen())
            {
                _walkingTo = null;
                return;
            }

            // Right click is the game's own way to call a walk off out here. Seen or not, a
            // stopped player is caught by ForgetArrivedWalk, but this makes it immediate.
            if (Input.GetMouseButtonDown(1)) _walkingTo = null;

            ForgetArrivedWalk(playerAI);
            if (!Keys.Down(Plugin.GoToMarkedKey, Plugin.GoToMarkedKey2)) return;

            var marked = FindMarkedCharacter(playerAI);

            // Same person, or nobody marked: stop. The game's handler has just re-aimed the
            // walk at them, so stopping here cancels it.
            if (_walkingTo != null && !IsDifferent(marked, _walkingTo))
            {
                CancelWalk(scene, playerAI);
                return;
            }
            if (marked == null) return;

            // Someone else: the game's handler has already re-aimed the walk at them on middle
            // click, as it does without this plugin; only the record needs updating.
            if (Input.GetMouseButtonDown(2)) BeginWalk(playerAI, marked);
            else SendPlayerTo(scene, playerAI, marked);
        }

        private static bool IsDifferent(SV.Chara.AI a, SV.Chara.AI b) =>
            a == null || b == null ? a != b : a.Pointer != b.Pointer;

        private static void CancelWalk(SimulationScene scene, SV.Chara.AI playerAI)
        {
            scene.MoveStop();
            playerAI.BehaviourCtrl.Stop(true);
            _walkingTo = null;
            _handling = false;
        }

        /// <summary>
        /// WASD and the left stick in the overview camera and on 2D or custom maps. Nothing
        /// else of third-person runs here: the camera, cursor, buttons and clicks all stay
        /// the game's. Stands aside for menus and conversations like third-person does.
        /// </summary>
        private static void MoveOutsideThirdPerson(Camera cam, SV.Chara.AI playerAI)
        {
            if (Scene.IsOverlap || IsAnyMenuOpen())
            {
                StopHandling(playerAI);
                return;
            }

            // Left click is the game's own out here, so it never walks.
            MovePlayer(cam, playerAI, allowMouseForward: false, screenRelative: true);
        }

        /// <summary>Ends a walk we were driving by hand, and only one of ours.</summary>
        private static void StopHandling(SV.Chara.AI playerAI)
        {
            if (!_handling) return;
            _handling = false;
            if (playerAI != null) playerAI.BehaviourCtrl.Stop(true);
        }

        // ------------------------------------------------------------ disabled

        private static void Disable(Camera cam, MapManager mapManager, int mapId)
        {
            bool was = IsPovRunning;
            IsPovRunning = false;
            WheelCyclingActive = false;

            // Only on the way out of third-person. This runs every frame the overview camera
            // is up, and forcing the travel UI on and the cursor free on every one of those
            // frames would override the game whenever it wants either of them otherwise.
            if (was)
            {
                Cursor.lockState = CursorLockMode.None;
                if (mapManager.objMapMoveUI != null) mapManager.objMapMoveUI.active = true;
            }
            UpdateCursor(false);

            MapButtonTracker.Run(cam, mapId, false);
            CameraRig.Reset();

            var playerAI = GameChara.PlayerAI;
            if (playerAI != null) SetCharacterHidden(playerAI, false);
            if (was && playerAI != null) ShowEveryone(playerAI, mapManager);

            if (was) CameraRig.RestoreOverview(cam, mapId);
            CameraRig.LearnOverview(cam, mapId, false);

            if (was && Plugin.OverviewRestore.Value == OverviewRestoreMode.On)
                RestorePlayerRing(playerAI);
            // Either way the ring goes back under the player: it was last a marker somewhere.
            else if (was && _ringCaptured && playerAI != null && playerAI.objCircle != null)
                playerAI.objCircle.transform.localPosition = _ringLocalPosition;
        }

        // ------------------------------------------------------- player ring

        // The player's ring (objCircle) is the game's "this is you" marker in the overview.
        // Third-person switches it off every frame and borrows it to mark job spots and
        // doorways, moving it off the player to do so -- and nothing gave it back, so it
        // stayed gone after returning to the overview camera.
        private static bool _ringCaptured;
        private static bool _ringWasActive;
        private static Vector3 _ringLocalPosition;
        private static bool _ringMapChanged;

        private static void CapturePlayerRing(SV.Chara.AI playerAI)
        {
            var ring = playerAI?.objCircle;
            _ringCaptured = ring != null;
            _ringMapChanged = false;
            if (ring == null) return;

            _ringWasActive = ring.active;
            _ringLocalPosition = ring.transform.localPosition;
        }

        /// <summary>
        /// Back under the player, and shown if it was shown when third-person started -- or if
        /// the map changed meanwhile, since arriving somewhere is exactly when the game shows
        /// it, and it would have been showing had third-person not been on.
        /// </summary>
        private static void RestorePlayerRing(SV.Chara.AI playerAI)
        {
            if (!_ringCaptured) return;
            _ringCaptured = false;

            var ring = playerAI?.objCircle;
            if (ring == null) return;

            ring.transform.localPosition = _ringLocalPosition;
            bool show = _ringWasActive || _ringMapChanged;
            if (ring.active != show) ring.active = show;
        }

        /// <summary>
        /// The plugin's Enable switch was turned off while playing. Hands everything back
        /// once, the same way leaving third-person does, so the game carries on as if the
        /// plugin were absent; after this the per-frame pass is skipped until re-enabled.
        /// Does nothing if the pass never ran, so starting the game disabled touches nothing.
        /// </summary>
        internal static void Shutdown()
        {
            if (_lastMapId == int.MinValue) return;

            var cam = Camera.main;
            var mapManager = SingletonInitializerAsync<MapManager>.Instance;
            if (cam != null && mapManager != null)
            {
                Disable(cam, mapManager, _lastMapId);
            }
            else
            {
                MapButtonTracker.Invalidate();
                IsPovRunning = false;
                WheelCyclingActive = false;
                UpdateCursor(false);
            }


            StopHandling(GameChara.PlayerAI);
            _walkingTo = null;
            _lastMapId = int.MinValue;
            ViewMode.Restart();
        }

        // -------------------------------------------------------------- menus

        /// <summary>Take a doorway directly, as third-person's interact does. For the gamepad.</summary>
        internal static void CrossDoorway(MapManager mapManager, SV.Chara.AI playerAI, SVNodeLink2 link)
        {
            MapInfoParam dest = null;
            MapManager.mapListTable.TryGetValue(link.endMapID, out dest);
            TakeDoorway(mapManager, playerAI, link, dest);
        }

        /// <summary>Walk to a character and talk, as clicking them does. For the gamepad.</summary>
        internal static void WalkToCharacter(SimulationScene scene, SV.Chara.AI playerAI,
                                             SV.Chara.AI target) =>
            SendPlayerTo(scene, playerAI, target);

        /// <summary>An H scene is on. Gamepad button mode stays out of it for now.</summary>
        internal static bool InH()
        {
            var hScene = SingletonInitializer<HScene>._instance;
            var adv = SingletonInitializer<ADV.ADVManager>._instance;
            return (adv != null && adv.IsHScene) || (hScene != null && hScene.isActiveAndEnabled);
        }

        /// <summary>A conversation (ADV) is on, and not an H scene.</summary>
        internal static bool InConversation()
        {
            var adv = SingletonInitializer<ADV.ADVManager>._instance;
            return adv != null && adv.IsADV && !InH();
        }

        internal static bool IsAnyMenuOpen()
        {
            var hScene = SingletonInitializer<HScene>._instance;
            var adv = SingletonInitializer<ADV.ADVManager>._instance;

            bool inH = (adv != null && adv.IsHScene) || (hScene != null && hScene.isActiveAndEnabled);
            bool inAdv = adv != null && adv.IsADV;

            return inH || inAdv ||
                   IsOpen(SingletonInitializer<MyRoom>._instance) ||
                   IsOpen(SingletonInitializer<MapSelect>._instance) ||
                   IsOpen(SingletonInitializer<CharaSelect>._instance) ||
                   IsOpen(SingletonInitializer<CoordeSelect>._instance) ||
                   IsOpen(SingletonInitializer<CorrelationDiagram>._instance);
        }

        /// <summary>
        /// Whether the player character should stop being drawn. Narrower than IsAnyMenuOpen:
        /// conversations, H scenes and My Room stage their own view and the world copy would
        /// be in the way, but the map, meet-up, outfit and relationship overlays simply draw
        /// on top and the character should stay where it is.
        /// </summary>
        private static bool ShouldHidePlayer()
        {
            var hScene = SingletonInitializer<HScene>._instance;
            var adv = SingletonInitializer<ADV.ADVManager>._instance;

            return (adv != null && (adv.IsADV || adv.IsHScene)) ||
                   (hScene != null && hScene.isActiveAndEnabled) ||
                   IsOpen(SingletonInitializer<MyRoom>._instance);
        }

        private static bool IsOpen(MyRoom o) => o != null && o.IsOpen();
        private static bool IsOpen(MapSelect o) => o != null && o.IsOpen();
        private static bool IsOpen(CharaSelect o) => o != null && o.IsOpen();
        private static bool IsOpen(CoordeSelect o) => o != null && o.IsOpen();
        private static bool IsOpen(CorrelationDiagram o) => o != null && o.IsOpen();

        /// <summary>
        /// While a menu or conversation owns the screen the camera is handed over: snapped to
        /// the player's head during dialogue, or following the H scene's own camera.
        /// </summary>
        private static void RunMenuBranch(Camera cam, SV.Chara.AI playerAI, bool uiOpen)
        {
            var simManager = SingletonInitializerAsync<SimulationManager>.Instance;
            var charaInfo = simManager?.uiSimCtrl?._objCharaInfo;
            if (charaInfo != null) charaInfo.SetActive(false);

            var adv = SingletonInitializer<ADV.ADVManager>._instance;
            bool inAdv = adv != null && adv.IsADV;

            if (_handling || inAdv)
            {
                playerAI.BehaviourCtrl.Stop(true);
                _handling = false;
            }

            Cursor.lockState = CursorLockMode.None;
            UpdateCursor(false);
            SetCharacterHidden(playerAI, false);

            if (inAdv)
            {
                playerAI.chaCtrl.transform.position = playerAI.position;
                cam.transform.position = playerAI.HeadPos;
                cam.transform.rotation = playerAI.rotation;
                CameraRig.CameraMovedElsewhere();
                return;
            }

            var hScene = SingletonInitializer<HScene>._instance;
            bool inH = (adv != null && adv.IsHScene) || (hScene != null && hScene.isActiveAndEnabled);
            if (!inH || hScene == null) return;

            var flip = playerAI.chaCtrl.transform.rotation * Quaternion.Euler(0f, 180f, 0f);
            cam.transform.position = playerAI.chaCtrl.transform.position +
                                     flip * hScene.MainCamera.transform.position;
            cam.transform.rotation = flip * hScene.MainCamera.transform.rotation;
            cam.fieldOfView = hScene.MainCamera.fieldOfView;
            CameraRig.CameraMovedElsewhere();
        }

        // ------------------------------------------------------- visibility

        /// <summary>
        /// Leaving third person: undoes the hiding below. Under a menu or an activity scene
        /// third person hides the player and whoever they are with; switching to the overview
        /// camera in the middle of one left them hidden, since nothing here runs any more to
        /// show them again.
        /// </summary>
        private static void ShowEveryone(SV.Chara.AI playerAI, MapManager mapManager)
        {
            foreach (var ai in Game.AICharas)
            {
                var human = ai?.BehaviourCtrl?.ChaCtrl;
                if (human == null) continue;
                human._visibleAll_k__BackingField = ai.Pointer == playerAI.Pointer ||
                                                    ai.BehaviourCtrl.NowMapID == mapManager.MapID;
            }
        }

        private static void ApplyCharacterVisibility(SV.Chara.AI playerAI, bool uiOpen,
                                                     bool cycling)
        {
            foreach (var ai in Game.AICharas)
            {
                if (ai == null) continue;
                ai.BehaviourCtrl.ChaCtrl._visibleAll_k__BackingField =
                    ai.BehaviourCtrl.NowMapID == SingletonInitializerAsync<MapManager>.Instance.MapID;
                // While cycling, the game owns the rings on OTHER characters -- clearing
                // them every frame would wipe the one it just lit. Not clearing them is also
                // what makes markers appear only once scrolling starts.
                //
                // The player's own ring is a different matter: it is ours, borrowed to mark job
                // spots and doorways, and it is parented to the player. Leaving it lit is what
                // made a location marker appear stuck to the character and follow them around.
                bool isPlayer = ai.Pointer == playerAI.Pointer;
                if ((!cycling || isPlayer) && ai.objCircle != null) ai.objCircle.active = false;
            }

            playerAI.BehaviourCtrl.ChaCtrl._visibleAll_k__BackingField = !uiOpen;

            // Three plain calls rather than a loop over a new array, which allocated every frame.
            SetVisible(playerAI.BehaviourCtrl.TargetBehaviorCtrl, !uiOpen);
            SetVisible(playerAI.BehaviourCtrl.TargetBehaviorCtrl1, !uiOpen);
            SetVisible(playerAI.BehaviourCtrl.ChaseBehaviourCtrl, !uiOpen);
        }

        private static void SetVisible(BehaviourController ctrl, bool visible)
        {
            if (ctrl != null) ctrl.ChaCtrl._visibleAll_k__BackingField = visible;
        }

        /// <summary>
        /// Runs after the visibility pass above, so hiding for first person wins over it.
        /// </summary>
        private static void SetCharacterHidden(SV.Chara.AI playerAI, bool hidden)
        {
            if (!hidden && !_weHidCharacter) return;
            _weHidCharacter = hidden;

            try
            {
                var human = playerAI.chaCtrl;
                if (human == null) return;
                human._visibleAll_k__BackingField = !hidden;
                human.visibleAll = !hidden;
            }
            catch { }
        }

        /// <summary>
        /// Unity hides the cursor for free while the lock state is Locked and shows it on
        /// unlock, so we only ever hide, remember that we did, and restore it when we stop.
        /// Hiding without that bookkeeping left the cursor gone everywhere, 2D maps included.
        /// </summary>
        private static void UpdateCursor(bool driving)
        {
            if (driving)
            {
                if (Cursor.visible)
                {
                    Cursor.visible = false;
                    _weHidCursor = true;
                }
                return;
            }

            if (_weHidCursor)
            {
                Cursor.visible = true;
                _weHidCursor = false;
            }
        }

        // -------------------------------------------------------- interaction

        private sealed class Target
        {
            public SV.Chara.AI Ai;
            public SVNodeLink2 Link;
            public Transform JobPoint;
            public MovePointInfo.JobKind Job = (MovePointInfo.JobKind)(-1);
        }

        private enum TargetFilter { Any, People, Places }

        /// <summary>
        /// Picks whatever is nearest and in front: a character, a job spot, or a doorway --
        /// or only characters, or only places, for the controller's talk and interact.
        /// </summary>
        private static Target FindNearestTarget(SimulationScene scene, MapManager mapManager,
                                                SV.Chara.AI playerAI, Camera cam,
                                                TargetFilter filter = TargetFilter.Any)
        {
            bool people = filter != TargetFilter.Places;
            bool places = filter != TargetFilter.People;
            var result = new Target();
            float nearest = 1f;

            MapCollisionCtrl.Info info = null;
            mapManager.pointInfoTable?.TryGetValue(mapManager.MapID, out info);

            // Compared by native pointer. The dictionary hands back a fresh managed wrapper on
            // every lookup, so comparing the wrappers themselves never matched, and this
            // re-searched the whole map hierarchy every frame instead of once per map.
            IntPtr infoPtr = info != null ? info.Pointer : IntPtr.Zero;
            IntPtr cachedPtr = _collisionCtrl != null ? _collisionCtrl.Pointer : IntPtr.Zero;
            if (_links == null || cachedPtr != infoPtr)
            {
                _collisionCtrl = info;
                _links = info?.transformParent != null
                    ? info.transformParent.GetComponentsInChildren<SVNodeLink2>()
                    : null;
            }

            if (_links != null && places)
            {
                foreach (var link in _links)
                {
                    if (link == null) continue;
                    if (link.startMapID != mapManager.MapID || link.endMapID == 15) continue;

                    MapInfoParam dest = null;
                    MapManager.mapListTable.TryGetValue(link.endMapID, out dest);
                    if (dest == null) continue;
                    if (dest.Kind != 0 && playerAI.BehaviourCtrl.ChaseBehaviourCtrl == null) continue;

                    float d = Vector3.Distance(link.transform.position, playerAI.position);
                    if (d >= nearest) continue;

                    nearest = d;
                    result.Link = link;
                }
            }

            if (places)
            foreach (var pair in MapButtonTracker.JobPoints(mapManager, mapManager.MapID))
            {
                if (pair.Value == null) continue;
                float d = Vector3.Distance(pair.Value.position, playerAI.position);
                if (d >= nearest) continue;

                nearest = d;
                result.Job = (MovePointInfo.JobKind)pair.Key;
                result.JobPoint = pair.Value;
                result.Link = null;
            }

            var forward = cam.transform.forward;
            if (!people) return result;
            foreach (var ai in Game.AICharas)
            {
                if (ai == null || ai.Pointer == playerAI.Pointer) continue;

                float d = Vector3.Distance(ai.position, playerAI.position);
                float angle = Vector3.Angle(ai.position - playerAI.position, forward);
                if (Math.Abs(angle) >= 60f || d >= nearest) continue;

                nearest = d;
                result.Ai = ai;
                result.Link = null;
                result.JobPoint = null;
            }

            // Someone at arm's length wins over someone across the map, whichever way you
            // happen to be facing. The loop above inherits a 60 degree cone from the original,
            // so a character standing beside or behind you was skipped entirely and the aimed
            // search below then picked the distant one you were looking at.
            if (result.Ai == null)
            {
                foreach (var ai in Game.AICharas)
                {
                    if (ai == null || ai.Pointer == playerAI.Pointer) continue;
                    if (ai.BehaviourCtrl.NowMapID != mapManager.MapID) continue;

                    float d = Vector3.Distance(ai.position, playerAI.position);
                    if (d >= nearest) continue;

                    nearest = d;
                    result.Ai = ai;
                    result.Link = null;
                    result.JobPoint = null;
                }
            }

            if (result.Ai == null && result.Link == null && result.JobPoint == null &&
                Plugin.InteractWithDistant.Value)
                result.Ai = AimedAtFromAfar(mapManager, playerAI, forward);

            return result;
        }

        /// <summary>Widest angle off centre that still counts as aiming at someone.</summary>
        private const float DistantAimAngle = 14f;

        /// <summary>How far away someone can be and still be walked to.</summary>
        private const float DistantAimRange = 45f;

        /// <summary>
        /// Whoever you are pointing at, however far away. Pressing interact then walks over to
        /// them, which is the game's own click-on-a-character behaviour.
        ///
        /// That behaviour arrives through SimulationScene.CursorTargetSelect, which this plugin
        /// suppresses whenever a mouse button has been taken over for walking -- so the feature
        /// quietly disappeared in both Forward Modes. Rebuilding it on the interact key instead
        /// puts it back regardless of which button walks.
        ///
        /// Only used when nothing is close enough to interact with, so it never steals a
        /// doorway or job spot from under your feet.
        /// </summary>
        private static SV.Chara.AI AimedAtFromAfar(MapManager mapManager, SV.Chara.AI playerAI,
                                                   Vector3 forward)
        {
            SV.Chara.AI best = null;
            float bestAngle = DistantAimAngle;

            foreach (var ai in Game.AICharas)
            {
                if (ai == null || ai.Pointer == playerAI.Pointer) continue;
                if (ai.BehaviourCtrl.NowMapID != mapManager.MapID) continue;

                var toThem = ai.position - playerAI.position;
                if (toThem.magnitude > DistantAimRange) continue;

                float angle = Vector3.Angle(toThem, forward);
                if (angle >= bestAngle) continue;

                bestAngle = angle;
                best = ai;
            }

            return best;
        }

        /// <summary>
        /// The character third person last aimed at, while third person runs. The game's
        /// character panel (GameCanvas/CharaInfo) shows them, and so does a button other
        /// plugins put on it (SVS_CustomGameBalance's btn_Switch).
        /// </summary>
        internal static SV.Chara.AI AimedCharacter { get; private set; }

        private static void HandleInteract(SimulationScene scene, MapManager mapManager,
                                           SV.Chara.AI playerAI, Target target,
                                           bool inButtonMode, bool pressed)
        {
            var simManager = SingletonInitializerAsync<SimulationManager>.Instance;
            var ui = simManager?.uiSimCtrl;
            if (ui?._objCharaInfo != null) ui._objCharaInfo.active = true;


            AimedCharacter = target.Ai;
            if (target.Ai != null)
            {
                ui?.SetTargetCharaName(target.Ai.charaData.Name);
                // Not in button mode: there the wheel owns the markers.
                if (Plugin.ShowTargetMarker.Value && !inButtonMode &&
                    target.Ai.objCircle != null) target.Ai.objCircle.active = true;
                if (pressed) SendPlayerTo(scene, playerAI, target.Ai);
                return;
            }

            if (target.JobPoint != null)
            {
                ui?.SetTargetCharaName(JobNames.Of(target.Job));
                ShowMarker(playerAI, target.JobPoint.position);
                if (pressed)
                {
                    playerAI.position = target.JobPoint.position;
                    mapManager.PCActionButtonAction(target.Job, mapManager.MapID);
                }
                return;
            }

            if (target.Link != null)
            {
                MapInfoParam dest = null;
                MapManager.mapListTable.TryGetValue(target.Link.endMapID, out dest);
                ui?.SetTargetCharaName(dest?.Name);
                ShowMarker(playerAI, target.Link.transform.position);

                if (pressed) TakeDoorway(mapManager, playerAI, target.Link, dest);
                return;
            }

            // Nothing else in reach: a seat or other special spot, used by ClickIdler.
            ui?.SetTargetCharaName(null);

            // The seat or other spot the idle key would use, for those who want it marked.
            if (Plugin.ShowSpotMarker.Value && !inButtonMode)
            {
                var spot = ClickIdler.NearbySpot(playerAI, out _);
                if (spot != null) ShowMarker(playerAI, spot.transform.position);
            }
        }

        /// <summary>
        /// Whoever the game currently has a ring under. Marks come from cycling with the wheel,
        /// or from Show Target Marker while walking around.
        /// </summary>
        private static void SendPlayerTo(SimulationScene scene, SV.Chara.AI playerAI,
                                         SV.Chara.AI target)
        {
            scene.SetTarget(target);
            BeginWalk(playerAI, target);
        }

        private static SV.Chara.AI FindMarkedCharacter(SV.Chara.AI playerAI)
        {
            foreach (var ai in Game.AICharas)
            {
                if (ai == null || ai.Pointer == playerAI.Pointer) continue;
                if (ai.objCircle != null && ai.objCircle.active) return ai;
            }
            return null;
        }

        private static void ShowMarker(SV.Chara.AI playerAI, Vector3 position)
        {
            if (playerAI.objCircle == null) return;
            playerAI.objCircle.active = true;
            playerAI.objCircle.gameObject.transform.position = position;
            _markerAt = position;
            _markerFrame = Time.frameCount;
        }

        private static Vector3 _markerAt;
        private static int _markerFrame = -1;

        /// <summary>
        /// The marker is the player's own ring, so it moves and turns with the player. Put
        /// back where it belongs after the player has moved this frame, or it jumps aside for
        /// a frame whenever the player turns next to it.
        /// </summary>
        private static void SteadyMarker(SV.Chara.AI playerAI)
        {
            if (_markerFrame != Time.frameCount || playerAI.objCircle == null) return;
            playerAI.objCircle.gameObject.transform.position = _markerAt;
        }

        // The game's scene fade is one shared overlay, white unless something (SVS_FadeController)
        // recolours it. A crossing tints it for itself and puts the colour back afterwards, so
        // the location buttons' travel keeps whatever colour it had. Its image is tinted
        // directly: SVS_FadeController rewrites any colour passed through SetColor.
        private static bool _tinted;
        private static Color _fadeColourBefore;
        private static float _untintAfter;

        private static void TintFade(Color colour)
        {
            try
            {
                var image = Scene.SceneFadeCanvas?.fadeImage;
                if (image == null) return;
                if (!_tinted) _fadeColourBefore = image.color;
                colour.a = _fadeColourBefore.a;
                image.color = colour;
                _tinted = true;
                _untintAfter = Time.unscaledTime + 0.5f;
            }
            catch { }
        }

        private static void UntintFadeWhenDone()
        {
            if (!_tinted || Time.unscaledTime < _untintAfter) return;
            try
            {
                if (Scene.IsFadeNow) return;
                var image = Scene.SceneFadeCanvas?.fadeImage;
                if (image != null) image.color = _fadeColourBefore;
            }
            catch { }
            _tinted = false;
        }

        private static int _collisionFor = -1;
        private static float _collisionGiveUpAt;

        /// <summary>After a doorway crossing, loads the new map's collision and points once
        /// the map has changed. Called every frame while one is pending.</summary>
        private static void LoadCollisionWhenReady(MapManager mapManager)
        {
            if (_collisionFor < 0 || mapManager == null) return;
            if (mapManager.MapID != _collisionFor && Time.unscaledTime < _collisionGiveUpAt) return;
            _collisionFor = -1;
            mapManager.LoadCollisionAndPoint(0);
        }

        private static void TakeDoorway(MapManager mapManager, SV.Chara.AI playerAI,
                                        SVNodeLink2 link, MapInfoParam dest)
        {
            MapManager.UroUroPointMove(playerAI.BehaviourCtrl, link.endMapID, -1, null);

            if (dest != null && dest.Kind == 0)
            {
                playerAI.position = link.EndTransform.position;
                playerAI.BehaviourCtrl.NowMapID = link.endMapID;
                playerAI.BehaviourCtrl.Stop(true);

                var simManager = SingletonInitializerAsync<SimulationManager>.Instance;
                // The game's own travel fades the screen out and in around a map change
                // (white by default; SVS_FadeController recolours it), which hides the new
                // map's sky and lights settling. Crossing directly used to skip that.
                var mode = Plugin.MapChangeFade.Value;
                bool fade = mode != MapFade.NoFade;
                if (fade) TintFade(mode == MapFade.FadeToBlack ? Color.black : Color.white);
                mapManager.ChangeMap(link.endMapID, null,
                    simManager != null ? simManager.GetNowTimeZone() : 0, false, true,
                    fade ? FadeCanvas.Fade.InOut : FadeCanvas.Fade.None, false);

                // With the fade the change itself may wait for the screen to cover over, so the
                // new map's collision and points are loaded once the map id says it is there
                // (at once if it already does, which is how it always ran without the fade).
                _collisionFor = link.endMapID;
                _collisionGiveUpAt = Time.unscaledTime + 5f;
                LoadCollisionWhenReady(mapManager);
            }
            else
            {
                playerAI.position = link.StartTransform.position;
            }
        }

        // ----------------------------------------------------------- movement

        /// <summary>
        /// Which ground directions look like "right" and "up" on screen, measured where the
        /// character stands: nudge its screen position each way and see where those points
        /// land on the horizontal plane through its feet. False when the view is too edge-on
        /// to tell, and the caller falls back to the camera's heading.
        /// </summary>
        private static bool TryScreenAxes(Camera cam, Vector3 feet, Vector3 input, out Vector3 world)
        {
            world = Vector3.zero;
            var ground = new Plane(Vector3.up, feet);
            var screen = cam.WorldToScreenPoint(feet);
            if (screen.z <= 0f) return false;

            const float nudge = 20f;    // pixels
            if (!OnGround(cam, ground, new Vector2(screen.x, screen.y), out var origin) ||
                !OnGround(cam, ground, new Vector2(screen.x + nudge, screen.y), out var right) ||
                !OnGround(cam, ground, new Vector2(screen.x, screen.y + nudge), out var up))
                return false;

            var rightDir = right - origin;
            var upDir = up - origin;
            rightDir.y = 0f;
            upDir.y = 0f;
            if (rightDir.sqrMagnitude < 1e-8f || upDir.sqrMagnitude < 1e-8f) return false;

            world = rightDir.normalized * input.x + upDir.normalized * input.z;
            return world.sqrMagnitude > 1e-6f;
        }

        private static bool OnGround(Camera cam, Plane ground, Vector2 screenPoint, out Vector3 point)
        {
            var ray = cam.ScreenPointToRay(new Vector3(screenPoint.x, screenPoint.y, 0f));
            if (ground.Raycast(ray, out float distance))
            {
                point = ray.GetPoint(distance);
                return true;
            }
            point = Vector3.zero;
            return false;
        }

        private const float DoubleTapWindow = 0.3f;    // seconds between the two presses
        private static float _lastForwardTap = -10f;
        private static bool _tapSprinting;

        private static readonly KeyCode[] DirectionKeys =
        {
            KeyCode.W, KeyCode.A, KeyCode.S, KeyCode.D,
            KeyCode.UpArrow, KeyCode.LeftArrow, KeyCode.DownArrow, KeyCode.RightArrow,
        };

        /// <summary>Stands for the walk-forward mouse button in the tap tracking.</summary>
        private const KeyCode MouseForwardTap = KeyCode.Mouse6;

        private static KeyCode _lastTapKey = KeyCode.None;

        /// <summary>
        /// Double-tap a direction and hold the second tap to sprint: WASD, the arrows, or the
        /// mouse button Forward Mode walks on (only where it walks, i.e. third-person with the
        /// cursor captured). Both taps must be the same key, so turning quickly from one key to
        /// another is not mistaken for a double tap. Once sprinting it carries on across keys
        /// -- switch from A to S, or hold two -- until no direction is held at all.
        /// </summary>
        private static bool DoubleTapSprint(bool allowMouseForward)
        {
            int mouse = allowMouseForward && Plugin.Mode.Value != ForwardMode.Off
                ? (Plugin.Mode.Value == ForwardMode.LeftClickForward ? 0 : 1)
                : -1;

            bool held = mouse >= 0 && Input.GetMouseButton(mouse);
            if (mouse >= 0 && Input.GetMouseButtonDown(mouse)) Tapped(MouseForwardTap);

            foreach (var key in DirectionKeys)
            {
                if (Input.GetKeyDown(key)) Tapped(key);
                if (Input.GetKey(key)) held = true;
            }

            if (!held) _tapSprinting = false;
            return _tapSprinting;
        }

        private static void Tapped(KeyCode key)
        {
            if (key == _lastTapKey && Time.unscaledTime - _lastForwardTap < DoubleTapWindow)
                _tapSprinting = true;
            _lastTapKey = key;
            _lastForwardTap = Time.unscaledTime;
        }

        private static bool _walkRunLatched;
        private static bool _ignoreHeldWalkKey;
        private static float _lastWalkKeyTap = -10f;

        /// <summary>
        /// Whether walking replaces the default of running: while the
        /// Walk/Run key is held, and -- with Double-Tap To Toggle Walk/Run -- latched on or off
        /// by a double tap. The second tap of a double tap is still being held when it lands,
        /// so it is ignored until released; otherwise it would undo the toggle it just made.
        /// </summary>
        private static bool WalkRunSwapped()
        {
            bool held = Keys.Held(Plugin.RunningKey, Plugin.RunningKey2);

            if (Keys.Down(Plugin.RunningKey, Plugin.RunningKey2))
            {
                if (Time.unscaledTime - _lastWalkKeyTap < DoubleTapWindow)
                {
                    _walkRunLatched = !_walkRunLatched;
                    _ignoreHeldWalkKey = true;
                    _lastWalkKeyTap = -10f;
                }
                else
                {
                    _lastWalkKeyTap = Time.unscaledTime;
                }
            }

            if (!held) _ignoreHeldWalkKey = false;
            return _walkRunLatched ^ (held && !_ignoreHeldWalkKey);
        }

        private static bool _stickWalking;

        /// <summary>
        /// A left stick only partly pushed walks. Raw axes, because the smoothed ones ramp the
        /// keyboard up from zero, which would read as a slight tilt for the first few frames.
        /// Raw keyboard input is always 0 or 1, so keys never trip this. A little hysteresis
        /// stops the walk and run animations flickering with a stick held near the line.
        /// </summary>
        private static bool StickTiltedSlightly()
        {
            float tilt = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")).magnitude;
            float line = Plugin.StickWalkThreshold.Value;

            if (tilt < line - 0.05f) _stickWalking = true;
            else if (tilt > line + 0.05f) _stickWalking = false;
            return _stickWalking && tilt > 0.01f;
        }

        private static void MovePlayer(Camera cam, SV.Chara.AI playerAI, bool allowMouseForward,
                                       bool screenRelative = false)
        {
            float horizontal = Input.GetAxis("Horizontal");
            float vertical = Input.GetAxis("Vertical");
            // The animation wheel, steered with the controller, has the sticks.
            if (ClickIdler.PadWheelOpen) horizontal = vertical = 0f;

            // Hold a mouse button to walk forward, the way Koikatsu and Aicomi do. Before the
            // merge this had to be done by patching Input.GetAxis to lie about the Vertical
            // axis; here it is simply part of the movement vector.
            if (allowMouseForward && Plugin.Mode.Value != ForwardMode.Off)
            {
                int button = Plugin.Mode.Value == ForwardMode.LeftClickForward ? 0 : 1;
                if (Input.GetMouseButton(button)) vertical = Mathf.Max(vertical, 1f);
            }

            // Before the no-input early return below: the gap between the two taps is a frame
            // with no input, and the second tap has to be seen coming after it.
            bool tapSprint = DoubleTapSprint(allowMouseForward);
            bool walkRunSwapped = WalkRunSwapped();

            var move = new Vector3(horizontal, 0f, vertical);

            if (move.sqrMagnitude < 0.01f)
            {
                if (_handling)
                {
                    playerAI.BehaviourCtrl.Stop(true);
                    _handling = false;
                }
                return;
            }

            // Walking under your own power calls off any trip the interact key started.
            _walkingTo = null;

            // Running is the default; the Walk/Run key (held or toggled) walks. The old Always
            // Running option, which flipped that default, was retired in favour of the toggle.
            bool running = !walkRunSwapped;
            if (StickTiltedSlightly()) running = false;

            // Sprint is a run with more speed on top, so it wins over a light stick.
            bool sprinting = tapSprint ||
                             Keys.Held(Plugin.SprintKey, Plugin.SprintKey2) ||
                             (Plugin.GamepadSupport.Value &&
                              Keys.Held(Plugin.GamepadSprintKey, Plugin.GamepadSprintKey2));
            if (sprinting) running = true;
            float speed = Plugin.WalkingSpeed.Value;
            if (Plugin.StaminaAffectsSpeed.Value)
                speed += playerAI.charaData.charasGameParam._baseParameter_k__BackingField.NowStamina *
                         Plugin.PhysicalGain.Value / 1000f;

            if (!_handling || _running != running)
            {
                _handling = true;
                _running = running;
                playerAI.BehaviourCtrl.accel = 10f;
                playerAI.BehaviourCtrl.SetSpeed(speed, true);
                playerAI.BehaviourCtrl.SetSpeedRate();
                playerAI.BehaviourCtrl.AnimRunAndWalk(_running);
            }

            // By the camera's heading alone. Transforming by its full rotation and flattening
            // shrank "forward" towards nothing as the camera tilted down, and the overview
            // camera looks down steeply, so W barely moved you there.
            //
            // In a fixed view that is still not enough. The camera does not follow the player,
            // so a character off to one side is seen at an angle: through a perspective lens
            // "straight away from the camera" converges on the middle of the screen, and
            // holding up drifted them sideways. There, directions come from the screen itself.
            if (screenRelative && TryScreenAxes(cam, playerAI.transform.position, move, out var onScreen))
                move = onScreen;
            else
                move = Quaternion.Euler(0f, cam.transform.eulerAngles.y, 0f) * move;

            float factor = running ? Plugin.RunningFactor.Value : 1f;
            if (sprinting) factor *= Plugin.SprintFactor.Value;

            playerAI.chaCtrl.gameObject.transform.localPosition = Vector3.zero;
            playerAI.transform.position += move.normalized * speed * factor * Time.deltaTime;
            playerAI.transform.forward = move.normalized;
        }
    }

    /// <summary>Labels for the job spots, as the original shipped them.</summary>
    internal static class JobNames
    {
        internal static string Of(MovePointInfo.JobKind job)
        {
            switch ((int)job)
            {
                case 0: return "食事";
                case 1: return "勉強";
                case 2: return "運動";
                case 3: return "バイト";
                case 6: return "着替え";
                case 8: return "祈願";
                default: return null;
            }
        }
    }
}
