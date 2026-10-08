using System;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace SVS_FreeRoam
{
    /// <summary>How the crouch key works.</summary>
    public enum CrouchMode
    {
        Toggle,
        DoubleTap,
        WhileHeld,
    }

    /// <summary>How a doorway crossing in third person changes the screen.</summary>
    public enum MapFade
    {
        NoFade,
        FadeToWhite,
        FadeToBlack,
    }

    /// <summary>The map screen Gamepad Shortcut Button opens: one of the map's own buttons.</summary>
    public enum GamepadShortcut
    {
        None,
        MapSelect,
        MeetUp,
        Everyone,
        Options,
        Help,
        GoHome,
        /// <summary>btn_Switch, added by SVS_CustomGameBalance: play as the marked character.</summary>
        SwitchCharacter,
    }

    /// <summary>How the mouse drives forward movement.</summary>
    public enum ForwardMode
    {
        /// <summary>Keyboard only.</summary>
        Off,

        /// <summary>Hold LEFT click to walk forward; interact stays on the interact key.</summary>
        LeftClickForward,

        /// <summary>Hold RIGHT click to walk forward, and move interact onto LEFT click.</summary>
        RightClickForward,
    }

    /// <summary>What happens to the camera when third-person is switched off.</summary>
    public enum OverviewRestoreMode
    {
        /// <summary>Leave the camera where third-person left it.</summary>
        Off,

        /// <summary>Restore the location's own overview camera.</summary>
        On,
    }

    /// <summary>Which easing Camera Smoothing uses.</summary>
    public enum SmoothingFeel
    {
        Original,
        AicomiGlide,
    }

    /// <summary>Which part of the camera rig gets eased when smoothing is on.</summary>
    public enum SmoothingType
    {
        /// <summary>Ease what the camera orbits; mouse look stays fully responsive.</summary>
        FollowPivot,

        /// <summary>Ease the camera position itself.</summary>
        CameraPosition,
    }

    /// <summary>How the mouse may turn the camera while the location buttons are up.</summary>
    public enum ButtonLookMode
    {
        /// <summary>The camera holds still, so the cursor is free to reach a button.</summary>
        Off,

        /// <summary>The mouse turns the camera as usual.</summary>
        FreeLook,

        /// <summary>The camera turns only while the cursor is near an edge of the screen.</summary>
        ScreenEdge,
    }

    /// <summary>Where the location buttons are drawn once shown.</summary>
    public enum MapButtonMode
    {
        /// <summary>On their marker; hidden while that is off screen.</summary>
        TrackMarkers,

        /// <summary>On their marker, pinned to the screen edge when off screen.</summary>
        ClampToScreenEdge,
    }

    /// <summary>
    /// Free roam for Summer Vacation Scramble: a third-person camera and controls, gamepad
    /// support, click-to-walk and follow, high-poly map characters.
    ///
    /// It grew out of SVS_3rdPov by Junh2x, whose repository is no longer available, first as
    /// a continuation of that plugin (SVS_3rdPovPlus) and now under a name that covers what it
    /// does. Its behaviour is preserved and extended rather than replaced. See CREDITS.md.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    // Soft: only so that, if the original is installed, it is loaded first and can be seen.
    [BepInDependency(OriginalGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "SVS_FreeRoam";
        public const string Name = "SVS_FreeRoam";
        public const string Version = "1.0.0";

        /// <summary>Junh2x's SVS_3rdPov, which drives the same camera and player.</summary>
        private const string OriginalGuid = "SVS_3rdPov";

        /// <summary>This plugin under its working name, never released. Its config is carried
        /// over once, so settings made while testing survive the rename.</summary>
        private const string PreviousGuid = "SVS_3rdPovPlus";

        /// <summary>
        /// Junh2x's original is installed as well; the two cannot share the camera. Its patches
        /// are taken out (by its own Unload), so it sits idle and this plugin carries on, and the
        /// player is told once, on screen and in the log, that this is the reworked version and
        /// the original should be removed. Only if that fails does this plugin stand down
        /// instead (OriginalInstalled; see Hooks).
        /// </summary>
        internal static bool OriginalInstalled { get; private set; }
        private static bool _originalSwitchedOff;

        private const string OriginalOffMessage =
            "SVS_3rdPov is deprecated and has been switched off: SVS_FreeRoam is its reworked " +
            "version. Remove SVS_3rdPov.dll from BepInEx" + "\\" + "plugins.";

        private const string OriginalMessage =
            "SVS_3rdPov is deprecated: SVS_FreeRoam is its reworked version. It could not be " +
            "switched off, so SVS_FreeRoam is idle. Remove SVS_3rdPov.dll from BepInEx" + "\\" +
            "plugins.";

        private static bool _toldAboutOriginal;

        internal static void TellAboutOriginal()
        {
            if (_toldAboutOriginal || (!OriginalInstalled && !_originalSwitchedOff)) return;
            _toldAboutOriginal = true;
            Notice.Tell(OriginalInstalled ? OriginalMessage : OriginalOffMessage, 20f);
        }

        /// <summary>Takes the original's patches out, leaving it loaded but idle.</summary>
        private void SwitchOffOriginal()
        {
            if (!IL2CPPChainloader.Instance.Plugins.TryGetValue(OriginalGuid, out var original)) return;
            try
            {
                if (!(original.Instance is BasePlugin plugin))
                    throw new InvalidOperationException("it has not loaded");
                plugin.Unload();
                _originalSwitchedOff = true;
                Logger.LogWarning(OriginalOffMessage);
            }
            catch (Exception e)
            {
                OriginalInstalled = true;
                Logger.LogWarning(OriginalMessage + " (" + e.Message + ")");
            }
        }

        internal static ManualLogSource Logger;

        // ---- original SVS_3rdPov options ------------------------------------
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> ForceHighPoly;
        internal static ConfigEntry<bool> DebugInfo;

        // ---- clicks on the world (from SVS_WalkAnywhere) ---------------------
        internal static ConfigEntry<bool> ClickWalk;
        internal static ConfigEntry<KeyCode> ClickWalkButton;
        internal static ConfigEntry<KeyCode> ClickWalkButton2;
        internal static ConfigEntry<float> ClickWalkMaxSnap;
        internal static ConfigEntry<bool> ClickFollow;
        internal static ConfigEntry<bool> ClickIdle;
        internal static ConfigEntry<KeyCode> IdleButton;
        internal static ConfigEntry<KeyCode> IdleButton2;
        internal static ConfigEntry<bool> ThirdPersonSpots;
        internal static ConfigEntry<bool> ShowSpotMarker;
        internal static ConfigEntry<int> WheelSlots;
        internal static ConfigEntry<AnimationSet> WheelSet;
        internal static ConfigEntry<string> Favorites1;
        internal static ConfigEntry<string> Favorites2;
        internal static ConfigEntry<string> Favorites3;
        internal static ConfigEntry<bool> AnimationProps;
        internal static ConfigEntry<float> PovBlurStrength;
        internal static ConfigEntry<bool> CharacterWheel;
        internal static ConfigEntry<KeyCode> IdleKeyThirdPerson;
        internal static ConfigEntry<KeyCode> IdleKeyThirdPerson2;
        internal static ConfigEntry<bool> ClickWalkSpots;
        internal static ConfigEntry<bool> ClickWalkIdle;
        internal static ConfigEntry<float> SpotClickSize;
        internal static ConfigEntry<KeyCode> FollowButton;
        internal static ConfigEntry<KeyCode> FollowButton2;
        internal static ConfigEntry<float> FollowMinDistance;
        internal static ConfigEntry<float> FollowIdealDistance;
        internal static ConfigEntry<float> FollowRunDistance;
        internal static ConfigEntry<FollowSpeed> FollowSpeedMode;
        internal static ConfigEntry<float> FollowRunAnimationAbove;
        internal static ConfigEntry<float> FollowCatchUpSpeed;
        internal static ConfigEntry<float> WalkingSpeed;
        internal static ConfigEntry<float> RunningFactor;
        internal static ConfigEntry<float> PhysicalGain;
        internal static ConfigEntry<bool> StaminaAffectsSpeed;

        internal static ConfigEntry<KeyCode> ToggleKey;
        internal static ConfigEntry<KeyCode> InteractKey;
        internal static ConfigEntry<KeyCode> MouseKey;
        internal static ConfigEntry<KeyCode> RunningKey;

        internal static ConfigEntry<KeyCode> ToggleKey2;
        internal static ConfigEntry<KeyCode> InteractKey2;
        internal static ConfigEntry<KeyCode> MouseKey2;
        internal static ConfigEntry<KeyCode> RunningKey2;
        internal static ConfigEntry<KeyCode> HideCharacterKey;
        internal static ConfigEntry<KeyCode> ViewResetKey;
        internal static ConfigEntry<KeyCode> ViewResetKey2;
        internal static ConfigEntry<KeyCode> FirstPersonKey;
        internal static ConfigEntry<KeyCode> FirstPersonKey2;
        internal static ConfigEntry<KeyCode> HideCharacterKey2;
        internal static ConfigEntry<KeyCode> CrouchKey;
        internal static ConfigEntry<KeyCode> CrouchKey2;

        // ---- movement --------------------------------------------------------
        internal static ConfigEntry<ForwardMode> Mode;
        internal static ConfigEntry<bool> ShowTargetMarker;
        internal static ConfigEntry<bool> InteractWithDistant;
        internal static ConfigEntry<KeyCode> GoToMarkedKey;
        internal static ConfigEntry<KeyCode> GoToMarkedKey2;
        internal static ConfigEntry<KeyCode> SprintKey;
        internal static ConfigEntry<KeyCode> SprintKey2;
        internal static ConfigEntry<bool> MoveInAllViews;
        internal static ConfigEntry<bool> ThirdPersonMode;
        internal static ConfigEntry<bool> GamepadSupport;

        // ---- gamepad ---------------------------------------------------------
        internal static ConfigEntry<KeyCode> GamepadInteractKey;
        internal static ConfigEntry<KeyCode> GamepadInteractKey2;
        internal static ConfigEntry<KeyCode> GamepadIdleKey;
        internal static ConfigEntry<KeyCode> GamepadIdleKey2;
        internal static ConfigEntry<KeyCode> GamepadToggleKey;
        internal static ConfigEntry<KeyCode> GamepadToggleKey2;
        internal static ConfigEntry<KeyCode> GamepadSprintKey;
        internal static ConfigEntry<KeyCode> GamepadSprintKey2;
        internal static ConfigEntry<KeyCode> GamepadSelectKey;
        internal static ConfigEntry<KeyCode> GamepadSelectKey2;
        internal static ConfigEntry<KeyCode> GamepadBackKey;
        internal static ConfigEntry<KeyCode> GamepadBackKey2;
        internal static ConfigEntry<KeyCode> GamepadTalkKey;
        internal static ConfigEntry<KeyCode> GamepadTalkKey2;
        internal static ConfigEntry<KeyCode> GamepadMenuKey;
        internal static ConfigEntry<KeyCode> GamepadMenuKey2;
        internal static ConfigEntry<KeyCode> GamepadShortcutKey;
        internal static ConfigEntry<KeyCode> GamepadShortcutKey2;
        internal static ConfigEntry<KeyCode> GamepadCrouchKey;
        internal static ConfigEntry<KeyCode> GamepadCrouchKey2;
        internal static ConfigEntry<KeyCode> GamepadResetViewKey;
        internal static ConfigEntry<KeyCode> GamepadResetViewKey2;
        internal static ConfigEntry<GamepadShortcut> GamepadShortcutOpens;
        internal static ConfigEntry<KeyCode> GamepadPrevScreenKey;
        internal static ConfigEntry<KeyCode> GamepadPrevScreenKey2;
        internal static ConfigEntry<KeyCode> GamepadNextScreenKey;
        internal static ConfigEntry<KeyCode> GamepadNextScreenKey2;
        internal static ConfigEntry<MapFade> MapChangeFade;
        internal static ConfigEntry<float> SprintFactor;
        internal static ConfigEntry<float> TriggerZoomSpeed;
        internal static ConfigEntry<float> GamepadReach;
        internal static ConfigEntry<bool> NoAutoWalkAfterGamepadTravel;
        internal static ConfigEntry<float> GamepadLookSpeed;
        internal static ConfigEntry<bool> GamepadInvertY;
        internal static ConfigEntry<float> StickWalkThreshold;

        // ---- startup ---------------------------------------------------------
        internal static ConfigEntry<bool> StartPovActive;

        // ---- camera ----------------------------------------------------------
        internal static ConfigEntry<float> CameraHeight;
        internal static ConfigEntry<float> MinPitch;
        internal static ConfigEntry<float> MaxPitch;
        internal static ConfigEntry<float> MinZoom;
        internal static ConfigEntry<float> MaxZoom;
        internal static ConfigEntry<float> LookSensitivity;
        internal static ConfigEntry<float> CameraSmoothing;
        internal static ConfigEntry<SmoothingType> SmoothingMode;
        internal static ConfigEntry<SmoothingFeel> SmoothingCurve;
        internal static ConfigEntry<bool> PovOn2DMaps;
        internal static ConfigEntry<OverviewRestoreMode> OverviewRestore;
        internal static ConfigEntry<float> HideCharacterBelow;
        internal static ConfigEntry<bool> NoSmoothingWhenHidden;
        internal static ConfigEntry<bool> OpticalZoom;
        internal static ConfigEntry<float> MinimumFov;
        internal static ConfigEntry<float> CrouchHeight;
        internal static ConfigEntry<CrouchMode> CrouchFunction;

        // ---- collision -------------------------------------------------------
        internal static ConfigEntry<bool> CameraCollision;
        internal static ConfigEntry<bool> CollisionIgnoreCharacters;
        internal static ConfigEntry<bool> CollisionIgnoreMapObjects;
        internal static ConfigEntry<bool> CollisionIgnoreGround;

        // ---- map buttons -----------------------------------------------------
        internal static ConfigEntry<MapButtonMode> MapButtonTracking;
        internal static ConfigEntry<float> MapButtonHeight;
        internal static ConfigEntry<bool> MoveInButtonMode;
        internal static ConfigEntry<ButtonLookMode> LookInButtonMode;
        internal static ConfigEntry<float> EdgeLookMargin;
        internal static ConfigEntry<bool> WheelCycling;


        internal static int CollisionLayerMask { get; private set; } = ~0;

        private Harmony _harmony;

        public override void Load()
        {
            Logger = base.Log;
            CarryOverPreviousConfig();
            SnapshotConfigFile();

            Enabled = Config.Bind(
                "General", "Enable", true,
                Ordered("Turns the whole plugin on or off. Takes effect immediately.", 101));

            Config.Bind(
                "General", "Reset All Settings", false,
                new ConfigDescription(
                    "Puts every setting of this plugin back to its default. Click twice to confirm.",
                    null,
                    new ConfigurationManagerAttributes
                    {
                        Order = 100,
                        HideDefaultButton = true,
                        CustomDrawer = PairDrawer.ResetAll(Config),
                    }));

            ThirdPersonMode = Config.Bind(
                "General", "PoV Mode", true,
                Ordered("The third-person camera and its controls. Off, you keep the game's own " +
                        "overview camera; the other features still work.", 99));
            CarryOver(ThirdPersonMode, "General", "Third-Person Mode");

            StartPovActive = Config.Bind(
                "General", "Start With PoV Mode", false,
                Ordered("Start the game in third person.", 98));
            CarryOver(StartPovActive, "Startup", "Start With PoV Active");
            CarryOver(StartPovActive, "General", "Start With PoV Active");

            MoveInAllViews = Config.Bind(
                "General", "Directional Movement In All Views", true,
                Ordered("Walk with WASD, the arrow keys or the left stick in the overview camera and on" +
                        " 2D maps too, not only in third person. Directions follow the screen.", 97));
            CarryOver(MoveInAllViews, "Movement", "WASD In All Views");

            GamepadSupport = Config.Bind(
                "General", "Extended Gamepad Support", true,
                Ordered("Full controller support: the right stick turns the camera, the triggers zoom, " +
                        "the gamepad buttons in Hotkeys work, and the D-pad chooses buttons on screen. " +
                        "Off, the controller only walks with the left stick, as in the base game.", 96));
            CarryOver(GamepadSupport, "General", "Gamepad Support");



            ForceHighPoly = Config.Bind(
                "General", "Force High Poly Characters", false,
                Ordered("Characters on the map use their full-detail models, the ones normally seen " +
                        "only in conversations. Uses a lot more memory, more so the more characters " +
                        "there are.", 45));

            // ---------------------------------------------- clicks on the world
            MapChangeFade = Config.Bind(
                "Camera", "Fade Between Maps", MapFade.FadeToWhite,
                Ordered("How the screen changes when you take a doorway in third person. A fade hides " +
                        "the new map loading in. Travelling with the location buttons keeps the game's " +
                        "own fade.", 5));

            PovBlurStrength = Config.Bind(
                "Camera", "Third Person Blur Strength", 0f,
                Ordered("Depth of field in third person: how strongly the background is blurred " +
                        "while you, or in first person whoever you aim at, stay sharp. 0 is no " +
                        "blur; 1 matches the map's own setting. Only with Depth Of Field switched " +
                        "on in the game's graphics settings.",
                        new AcceptableValueRange<float>(0f, 5f), 3));

            ClickWalk = Config.Bind(
                "Click To Walk", "Click To Walk", true,
                Ordered("Click the ground to walk there. Works whenever the mouse cursor is showing: in" +
                        " the overview camera, on 2D maps, and in third person while the location " +
                        "buttons are shown.", 100));

            ClickWalkButton = Config.Bind(
                "Click To Walk", "Walk Button", KeyCode.Mouse0,
                Ordered("Mouse0 is the left button.", 90));

            ClickWalkButton2 = Config.Bind(
                "Click To Walk", "Walk Button (second)", KeyCode.None,
                Ordered("An optional second button.", 89));

            ClickWalkMaxSnap = Config.Bind(
                "Click To Walk", "Max Snap Distance", 3f,
                Ordered("A click lands on the nearest spot you can walk to. If that is further than " +
                        "this many metres away, the click is ignored.", new AcceptableValueRange<float>(0.5f, 50f), 80));

            ClickWalkSpots = Config.Bind(
                "Click To Walk", "Use Seats And Special Spots", true,
                Ordered("Clicking a chair, a bench or another special spot walks you there and uses " +
                        "it, with the animation the game plays at that spot.", 70));

            SpotClickSize = Config.Bind(
                "Click To Walk", "Spot Click Size", 0.6f,
                Ordered("How close to a seat or special spot the cursor has to be for the click to " +
                        "count as a click on it, in metres.", new AcceptableValueRange<float>(0.2f, 3f), 65));

            ClickWalkIdle = Config.Bind(
                "Click To Walk", "Idle Animation On Arrival", false,
                Ordered("After walking to a clicked spot, your character plays one of the map's " +
                        "standing idle animations instead of just standing there.", 60));

            ClickFollow = Config.Bind(
                "Click To Follow", "Click To Follow", true,
                Ordered("Click a character to follow them, through doorways too. Click them again, or " +
                        "click somewhere else, to stop. You stop if someone comes over to talk to you.", 100));

            FollowButton = Config.Bind(
                "Click To Follow", "Follow Button", KeyCode.Mouse1,
                Ordered("Mouse1 is the right button. In third person it only follows while the mouse " +
                        "cursor is showing; otherwise it is the interact button.", 90));

            FollowButton2 = Config.Bind(
                "Click To Follow", "Follow Button (second)", KeyCode.None,
                Ordered("An optional second button.", 89));

            FollowMinDistance = Config.Bind(
                "Click To Follow", "Minimum Distance", 1f,
                Ordered("Never get closer to them than this (metres).",
                        new AcceptableValueRange<float>(0.3f, 10f), 80));

            FollowIdealDistance = Config.Bind(
                "Click To Follow", "Ideal Distance", 2f,
                Ordered("How far behind them you settle (metres).",
                        new AcceptableValueRange<float>(0.3f, 30f), 79));

            FollowRunDistance = Config.Bind(
                "Click To Follow", "Run Distance", 5f,
                Ordered("Fall further behind than this (metres) and you run to catch up.",
                        new AcceptableValueRange<float>(1f, 50f), 78));

            FollowSpeedMode = Config.Bind(
                "Click To Follow", "Speed Mode", FollowSpeed.Dynamic,
                Ordered("WalkOrRun: walk or run at your own speeds. MatchSpeed: keep their pace. " +
                        "Dynamic: the further behind you are, the faster you go.", 70));

            FollowRunAnimationAbove = Config.Bind(
                "Click To Follow", "Run Animation Above", 2.2f,
                Ordered("MatchSpeed and Dynamic: above this speed (metres per second) you are shown " +
                        "running rather than walking.", new AcceptableValueRange<float>(0.5f, 8f), 69));

            FollowCatchUpSpeed = Config.Bind(
                "Click To Follow", "Catch-Up Speed", 4f,
                Ordered("MatchSpeed and Dynamic: how fast you go when catching up (metres per second).",
                        new AcceptableValueRange<float>(1f, 10f), 68));
            ClickIdle = Config.Bind(
                "Click To Idle", "Click To Idle", true,
                Ordered("Click your own character to play a random idle animation, and click again " +
                        "to stop it. Hold the button on your character to choose one from a wheel. " +
                        "In third person, the key below does the same.", 100));

            IdleButton = Config.Bind(
                "Click To Idle", "Idle Button", KeyCode.Mouse1,
                Ordered("Mouse1 is the right button.", 90));

            IdleButton2 = Config.Bind(
                "Click To Idle", "Idle Button (second)", KeyCode.None,
                Ordered("An optional second button.", 89));

            IdleKeyThirdPerson = Config.Bind(
                "Click To Idle", "Idle Key In Third Person", KeyCode.Mouse2,
                Ordered("Mouse2 is the middle button. Tap next to a chair or other special spot to " +
                        "use it, and tap again to get up; anywhere else a tap plays a random " +
                        "animation, and another tap stops it. Hold for the wheel. While a character " +
                        "is marked, the middle button walks to them instead.", 88));

            IdleKeyThirdPerson2 = Config.Bind(
                "Click To Idle", "Idle Key In Third Person (second)", KeyCode.None,
                Ordered("An optional second key.", 87));

            WheelSet = Config.Bind(
                "Click To Idle", "Animation Set", AnimationSet.Fitting,
                Ordered("Which animations the wheel lists, and a tap picks one of at random.\n\n" +
                        "Fitting - what suits where you are: standing ones in the open, the seat's " +
                        "own when seated.\nMap Animations - everything this map's spots offer.\n" +
                        "Sitting - every chair and desk animation in the game.\nFavorites 1 to 3 - " +
                        "your own collections; put one animation in a collection to make a tap " +
                        "always play it.\nAll - every animation in the game. Many only look right " +
                        "at a chair, a desk or with the props of an activity.", 85));

            CharacterWheel = Config.Bind(
                "Click To Idle", "Character Wheel", true,
                Ordered("Hold the button on another character for a wheel of things to do with " +
                        "them: talk, follow, switch to.", 83));

            ThirdPersonSpots = Config.Bind(
                "Click To Idle", "Use Spots In Third Person", true,
                Ordered("In third person, a tap of the idle key next to a chair, bench or other " +
                        "special spot uses it.", 80));

            ShowSpotMarker = Config.Bind(
                "Click To Idle", "Show Spot Marker", false,
                Ordered("In third person, show a marker on the seat or spot the idle key would use.", 79));

            WheelSlots = Config.Bind(
                "Click To Idle", "Wheel Size", 12,
                Ordered("How many choices the wheel shows at once. The rest go on further pages, " +
                        "turned with the mouse wheel. In third person a wheel with pages stays open " +
                        "when you let go: click a choice, or the centre to cancel.",
                        new AcceptableValueRange<int>(4, 16), 78));

            Favorites1 = Config.Bind(
                "Click To Idle", "Favorites 1", "",
                Ordered("A collection of favorite animations. With the wheel open, press 1 on an " +
                        "animation to add it or take it out. Favorites are starred with their " +
                        "collection's number and listed first. (Kept here by name, separated by " +
                        "commas.)", 74));
            CarryOver(Favorites1, "Click To Idle", "Favorite Animations");
            CarryOver(Favorites1, "Click To Idle", "Favo" + "urite Animations");

            Favorites2 = Config.Bind(
                "Click To Idle", "Favorites 2", "",
                Ordered("A second collection: press 2 on an animation in the wheel.", 73));

            Favorites3 = Config.Bind(
                "Click To Idle", "Favorites 3", "",
                Ordered("A third collection: press 3 on an animation in the wheel.", 72));

            AnimationProps = Config.Bind(
                "Click To Idle", "Animation Props And Effects", true,
                Ordered("Animations are started the way the game starts them for other characters, " +
                        "with whatever they hold (a phone, a book) and the sounds some of them " +
                        "make (the exercise ones). Off, only the movement plays.", 71));

            CarryOverFromWalkAnywhere();


            WalkingSpeed = Config.Bind(
                "Movement", "Walking Speed", 1f,
                Ordered("How fast you walk.", new AcceptableValueRange<float>(0f, 3f), 96));
            CarryOver(WalkingSpeed, "General");

            RunningFactor = Config.Bind(
                "Movement", "Running Factor", 2.8f,
                Ordered("How much faster running is than walking.", new AcceptableValueRange<float>(1f, 5f), 95));
            CarryOver(RunningFactor, "General");

            SprintFactor = Config.Bind(
                "Movement", "Sprint Factor", 1.6f,
                Ordered("How much faster sprinting is than running.",
                        new AcceptableValueRange<float>(1f, 3f), 94));
            CarryOver(SprintFactor, "Gamepad");
            CarryOver(SprintFactor, "General");

            CrouchFunction = Config.Bind(
                "Movement", "Crouch Key Function", CrouchMode.Toggle,
                Ordered("How the crouch key works: Toggle crouches on one press and stands up on the " +
                        "next; Double Tap does that on a double-tap, and crouches while held; While " +
                        "Held crouches only while it is held. Crouching only works in first person.", 97));
            // The third choice was called While Pressed.
            if (ValueInFile("Movement", "Crouch Key Function") == "WhilePressed")
                CrouchFunction.Value = CrouchMode.WhileHeld;

            StaminaAffectsSpeed = Config.Bind(
                "Movement", "Stamina Affects Speed", true,
                Ordered("The more stamina you have left, the faster you move. Off, you always move at " +
                        "the same speed.", 93));
            CarryOver(StaminaAffectsSpeed, "General");

            PhysicalGain = Config.Bind(
                "Movement", "Stamina Gain", 1f,
                Ordered("How much your stamina adds to your speed.",
                        new AcceptableValueRange<float>(0f, 2f), 92));
            CarryOver(PhysicalGain, "General");

            ShowTargetMarker = Config.Bind(
                "Movement", "Show Target Marker", false,
                Ordered("Show a ring under the character you are aiming at in third person.", 80));

            InteractWithDistant = Config.Bind(
                "Movement", "Interact With Aimed Character", true,
                Ordered("With nothing close by, the interact key walks you over to whoever you are " +
                        "aiming at, even far away.", 85));
            CarryOver(InteractWithDistant, "Movement", "Interact With Distant Character");

            // ---------------------------------------------------------- hotkeys
            // A label-only row so the two key columns are headed once, rather than repeating
            // the headings on every row or pushing each setting's name above its buttons.
            var headings = Config.Bind("Hotkeys", "Columns", false,
                new ConfigDescription("", null,
                    new ConfigurationManagerAttributes
                    {
                        // NOT HideSettingName: that gives the drawer the whole row, so the
                        // headings start at the far left above the setting names instead of
                        // above the key buttons. An empty name leaves them in the value column,
                        // lined up with the keys below.
                        DispName = "",
                        HideDefaultButton = true,
                        Order = 1000,
                    }));
            SetDrawer(headings, PairDrawer.ColumnHeadings());

            // Each binding is a pair drawn on one ConfigurationManager row. The second of the
            // pair is bound FIRST, because the first one's drawer has to capture both.
            ToggleKey2 = BindSecond("PoV Toggle Key 2");
            ToggleKey = Config.Bind("Hotkeys", "PoV Toggle Key", KeyCode.F4,
                Paired("Switch third person on and off. With Overview Camera Restore off, it shows and" +
                       " hides the location buttons instead.",
                       null, 100));

            InteractKey2 = BindSecond("Interact Key 2");
            InteractKey = Config.Bind("Hotkeys", "Interact Key", KeyCode.Mouse1,
                Paired("Talk to whoever is in front of you, use an activity spot, or take a doorway.",
                       null, 90));

            GoToMarkedKey2 = BindSecond("Go To Marked Key 2");
            GoToMarkedKey = Config.Bind("Hotkeys", "Go To Marked Key", KeyCode.Mouse2,
                Paired("Walk to the marked character, wherever they are; press again to stop. Mark " +
                       "characters by scrolling the mouse wheel while the location buttons are shown.",
                       null, 88));

            SprintKey2 = BindSecond("Sprint Key 2");
            SprintKey = Config.Bind("Hotkeys", "Sprint Key", KeyCode.LeftShift,
                Paired("Hold to sprint. Double-tapping a direction (WASD, the arrow keys or the " +
                       "mouse button that walks you forward) and holding it sprints too.",
                       null, 69));

            // The controller's buttons sit together at the bottom of the list. Joystick Button
            // numbers are an Xbox controller's: 0 A, 1 B, 2 X, 3 Y, 4 LB, 5 RB, 6 Back/View,
            // 7 Start/Menu, 8 left stick click, 9 right stick click. The D-pad is not a button
            // to Unity and is not rebindable.
            GamepadSelectKey2 = BindSecond("Gamepad Select Button 2");
            GamepadSelectKey = Config.Bind("Hotkeys", "Gamepad Select Button",
                KeyCode.JoystickButton0,
                Paired("A. Press the highlighted button. With nothing highlighted it starts choosing, " +
                       "like the D-pad; in conversations it moves the text on.",
                       null, 30));

            GamepadBackKey2 = BindSecond("Gamepad Back Button 2");
            GamepadBackKey = Config.Bind("Hotkeys", "Gamepad Back Button",
                KeyCode.JoystickButton1,
                Paired("B. Go back, close a screen, or stop walking.",
                       null, 29));

            GamepadTalkKey2 = BindSecond("Gamepad Talk Button 2");
            GamepadTalkKey = Config.Bind("Hotkeys", "Gamepad Talk Button",
                KeyCode.JoystickButton2,
                Paired("X. Talk to the nearest character.", null, 28));

            GamepadInteractKey2 = BindSecond("Gamepad Interact Button 2");
            GamepadInteractKey = Config.Bind("Hotkeys", "Gamepad Interact Button",
                KeyCode.JoystickButton3,
                Paired("Y. Use the nearest doorway or activity spot.",
                       null, 27));

            GamepadIdleKey2 = BindSecond("Gamepad Idle Button 2");
            GamepadIdleKey = Config.Bind("Hotkeys", "Gamepad Idle Button",
                KeyCode.JoystickButton4,
                Paired("LB. Tap to sit on the seat next to you or play an idle animation; tap again " +
                       "to get up or stop. Hold for the animation wheel: tilt a stick to an " +
                       "animation and let go, or press Select (A), to play it; RB or the D-pad " +
                       "turns its pages.",
                       null, 26));

            GamepadToggleKey2 = BindSecond("Gamepad PoV Toggle Button 2");
            GamepadToggleKey = Config.Bind("Hotkeys", "Gamepad PoV Toggle Button",
                KeyCode.JoystickButton8,
                Paired("Left stick click. Switch third person on and off.",
                       null, 26));

            GamepadSprintKey2 = BindSecond("Gamepad Sprint Button 2");
            GamepadSprintKey = Config.Bind("Hotkeys", "Gamepad Sprint Button",
                KeyCode.JoystickButton5,
                Paired("RB. Hold to sprint.", null, 25));

            GamepadMenuKey2 = BindSecond("Gamepad Menu Button 2");
            GamepadMenuKey = Config.Bind("Hotkeys", "Gamepad Menu Button",
                KeyCode.JoystickButton7,
                Paired("Start. Open the options; press again to close them, or any other open screen.", null, 24));

            GamepadShortcutKey2 = BindSecond("Gamepad Shortcut Button 2");
            GamepadShortcutKey = Config.Bind("Hotkeys", "Gamepad Shortcut Button",
                KeyCode.JoystickButton6,
                Paired("Back (View). Open the screen chosen in Gamepad > Shortcut Button Opens; press " +
                       "again to close it.", null, 23));

            GamepadCrouchKey2 = BindSecond("Gamepad Crouch Button 2");
            GamepadCrouchKey = Config.Bind("Hotkeys", "Gamepad Crouch Button",
                KeyCode.JoystickButton1,
                Paired("B. Crouch, in first person. Only while no button on screen is selected, " +
                       "since B also backs out of those.", null, 22));

            GamepadResetViewKey2 = BindSecond("Gamepad Reset View Button 2");
            GamepadResetViewKey = Config.Bind("Hotkeys", "Gamepad Reset View Button",
                KeyCode.JoystickButton9,
                Paired("Right stick click. Reset the camera.", null, 21));

            GamepadPrevScreenKey2 = BindSecond("Gamepad Previous Screen Button 2");
            GamepadPrevScreenKey = Config.Bind("Hotkeys", "Gamepad Previous Screen Button",
                KeyCode.JoystickButton4,
                Paired("LB. On Map Select, Meet Up, Options, Shortcuts or Help: go to the previous of " +
                       "those screens.", null, 20));

            GamepadNextScreenKey2 = BindSecond("Gamepad Next Screen Button 2");
            GamepadNextScreenKey = Config.Bind("Hotkeys", "Gamepad Next Screen Button",
                KeyCode.JoystickButton5,
                Paired("RB. The same, the next screen.", null, 19));

            MigrateGamepadLayout();

            MouseKey2 = BindSecond("Mouse Mode Key 2");
            MouseKey = Config.Bind("Hotkeys", "Mouse Mode Key", KeyCode.LeftAlt,
                Paired("Hold to show the mouse cursor and the location buttons.",
                       null, 95));

            RunningKey2 = BindSecond("Walk/Run Key 2");
            RunningKey = Config.Bind("Hotkeys", "Walk/Run Key", KeyCode.LeftControl,
                Paired("Hold to walk instead of run. Double-tap it to switch between walking and " +
                       "running for good.",
                       null, 70));

            HideCharacterKey2 = BindSecond("Hide Character Key 2");
            HideCharacterKey = Config.Bind("Hotkeys", "Hide Character Key", KeyCode.None,
                Paired("Show or hide your own character.", null, 60));

            // The drawers need both halves of each pair, which only exist now.
            SetPairDrawer(ToggleKey, ToggleKey2);
            SetPairDrawer(InteractKey, InteractKey2);
            SetPairDrawer(GamepadInteractKey, GamepadInteractKey2);
            SetPairDrawer(GamepadToggleKey, GamepadToggleKey2);
            SetPairDrawer(GamepadIdleKey, GamepadIdleKey2);
            SetPairDrawer(GamepadSprintKey, GamepadSprintKey2);
            SetPairDrawer(GamepadSelectKey, GamepadSelectKey2);
            SetPairDrawer(GamepadBackKey, GamepadBackKey2);
            SetPairDrawer(GamepadTalkKey, GamepadTalkKey2);
            SetPairDrawer(GamepadMenuKey, GamepadMenuKey2);
            SetPairDrawer(GamepadShortcutKey, GamepadShortcutKey2);
            SetPairDrawer(GamepadCrouchKey, GamepadCrouchKey2);
            SetPairDrawer(GamepadResetViewKey, GamepadResetViewKey2);
            SetPairDrawer(GamepadPrevScreenKey, GamepadPrevScreenKey2);
            SetPairDrawer(GamepadNextScreenKey, GamepadNextScreenKey2);
            SetPairDrawer(GoToMarkedKey, GoToMarkedKey2);
            SetPairDrawer(SprintKey, SprintKey2);
            SetPairDrawer(MouseKey, MouseKey2);
            SetPairDrawer(RunningKey, RunningKey2);
            CrouchKey2 = BindSecond("Crouch Key 2");
            CrouchKey = Config.Bind("Hotkeys", "Crouch Key", KeyCode.C,
                Paired("Hold to crouch. Only works in first person (zoomed all the way in).",
                       null, 50));

            ViewResetKey2 = BindSecond("Reset View Key 2");
            CarryOver(ViewResetKey2, "Hotkeys", "View Reset Key 2");
            ViewResetKey = Config.Bind("Hotkeys", "Reset View Key", KeyCode.None,
                Paired("Put the camera back to its starting distance and zoom.",
                       null, 65));

            SetPairDrawer(HideCharacterKey, HideCharacterKey2);
            CarryOver(ViewResetKey, "Hotkeys", "View Reset Key");
            SetPairDrawer(ViewResetKey, ViewResetKey2);

            FirstPersonKey2 = BindSecond("First Person Key 2");
            FirstPersonKey = Config.Bind("Hotkeys", "First Person Key", KeyCode.Space,
                Paired("In third person, switch to first person, and back to where the camera was.",
                       null, 64));
            SetPairDrawer(FirstPersonKey, FirstPersonKey2);
            SetPairDrawer(CrouchKey, CrouchKey2);


            // --------------------------------------------------------- movement
            Mode = Config.Bind(
                "Movement", "Forward Mode", ForwardMode.LeftClickForward,
                new ConfigDescription(
                    "Which mouse button walks you forward in third person.\n\nOff - keyboard " +
                    "only.\nLeftClickForward - hold the left button.\nRightClickForward - hold the " +
                    "right button.\n\nDouble-tap the button, or a direction key, and hold it to " +
                    "sprint. Double-tap the Walk/Run key to switch between walking and running.", null,
                    new ConfigurationManagerAttributes { Order = 100 }));


            // ---------------------------------------------------------- gamepad
            GamepadLookSpeed = Config.Bind(
                "Gamepad", "Right Stick Look Speed", 1.5f,
                Ordered("How fast the right stick turns the camera.",
                        new AcceptableValueRange<float>(0.1f, 4f), 90));

            GamepadInvertY = Config.Bind(
                "Gamepad", "Invert Right Stick Vertical", false,
                Ordered("Push up to look down, like a flight stick.", 80));

            TriggerZoomSpeed = Config.Bind(
                "Gamepad", "Trigger Zoom Speed", 1.5f,
                Ordered("How fast the triggers zoom: left in, right out.",
                        new AcceptableValueRange<float>(0.1f, 4f), 85));

            GamepadShortcutOpens = Config.Bind(
                "Gamepad", "Shortcut Button Opens", GamepadShortcut.MeetUp,
                Ordered("Which screen the Shortcut button (Back/View) opens. SwitchCharacter is the " +
                        "switch button added by the SVS_CustomGameBalance plugin.", 40));

            GamepadReach = Config.Bind(
                "Gamepad", "Doorway Reach", 3f,
                Ordered("Outside third person: how close you must be to a doorway or activity spot to " +
                        "use it with the gamepad.",
                        new AcceptableValueRange<float>(0.5f, 10f), 65));

            NoAutoWalkAfterGamepadTravel = Config.Bind(
                "Gamepad", "No Auto-Walk After Gamepad Travel", true,
                Ordered("After travelling with the gamepad outside third person, stay where you arrive " +
                        "instead of walking on.", 64));

            StickWalkThreshold = Config.Bind(
                "Gamepad", "Left Stick Walk Threshold", 0.6f,
                Ordered("Tilt the left stick less than this to walk, further to run.",
                        new AcceptableValueRange<float>(0.1f, 0.95f), 70));

            // ----------------------------------------------------------- camera
            CameraHeight = Config.Bind(
                "Camera", "Camera Height", 1.2f,
                Ordered("How high above your character the camera looks.",
                    new AcceptableValueRange<float>(0.05f, 2.5f), 100));

            MinPitch = Config.Bind(
                "Camera", "Min Pitch", -90f,
                Ordered("How far you can look up from below.",
                    new AcceptableValueRange<float>(-90f, 0f), 70));

            MaxPitch = Config.Bind(
                "Camera", "Max Pitch", 90f,
                Ordered("How far you can look down from above.",
                    new AcceptableValueRange<float>(0f, 90f), 65));

            MinZoom = Config.Bind(
                "Camera", "Min Camera Distance", 0f,
                new ConfigDescription(
                    "The closest the camera can get. 0 gives a first-person view.",
                    new AcceptableValueRange<float>(0f, 5f),
                    new ConfigurationManagerAttributes { Order = 90 }));

            MaxZoom = Config.Bind(
                "Camera", "Max Camera Distance", 12f,
                Ordered("The furthest the camera can pull back.",
                    new AcceptableValueRange<float>(3f, 30f), 85));

            LookSensitivity = Config.Bind(
                "Camera", "Look Sensitivity", 1f,
                Ordered("Mouse look speed.",
                    new AcceptableValueRange<float>(0.1f, 5f), 95));

            CameraSmoothing = Config.Bind(
                "Camera", "Camera Smoothing", 0f,
                new ConfigDescription(
                    "How smoothly the camera follows. 0 is instant.",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { Order = 40 }));

            SmoothingMode = Config.Bind(
                "Camera", "Camera Smoothing Type", SmoothingType.FollowPivot,
                new ConfigDescription(
                    "FollowPivot - smooths the camera following you, while mouse look stays " +
                    "instant.\n\nCameraPosition - smooths everything; softer, but the camera lags " +
                    "behind.", null,
                    new ConfigurationManagerAttributes { Order = 35 }));

            SmoothingCurve = Config.Bind(
                "Camera", "Camera Smoothing Feel", SmoothingFeel.Original,
                new ConfigDescription(
                    "How Camera Smoothing eases. Original - this plugin's own.\n\nAicomi Glide - " +
                    "the glide of AC_MainCameraExtension: the slider is how long the camera " +
                    "takes to catch up, in seconds.", null,
                    new ConfigurationManagerAttributes { Order = 34 }));

            PovOn2DMaps = Config.Bind(
                "Camera", "Third Person On 2D Maps (Not Recommended)", false,
                new ConfigDescription(
                    "NOT RECOMMENDED. Allows third person on the maps that are a flat picture. " +
                    "They were not made for it: expect to walk on a backdrop.", null,
                    new ConfigurationManagerAttributes { Order = 3 }));

            OverviewRestore = Config.Bind(
                "Camera", "Overview Camera Restore", OverviewRestoreMode.On,
                new ConfigDescription(
                    "On - the toggle switches between third person and the map's overview " +
                    "camera.\n\nOff - third person is the only view, and the toggle shows and hides" +
                    " the location buttons instead.", null,
                    new ConfigurationManagerAttributes { Order = 110 }));

            HideCharacterBelow = Config.Bind(
                "Camera", "Hide Character Below Distance", 0.8f,
                new ConfigDescription(
                    "Hide your character when the camera is closer than this, for a clean first-" +
                    "person view.",
                    new AcceptableValueRange<float>(0f, 10f),
                    new ConfigurationManagerAttributes { Order = 60 }));

            NoSmoothingWhenHidden = Config.Bind(
                "Camera", "No Smoothing While Hidden", true,
                Ordered("No camera smoothing in first person, where it feels like lag.", 34));

            OpticalZoom = Config.Bind(
                "Camera", "Zoom Past Minimum Distance", true,
                Ordered("Once zoomed all the way in, keep scrolling to zoom like a camera lens.", 80));

            CrouchHeight = Config.Bind(
                "Camera", "Crouch Height", 0.6f,
                new ConfigDescription(
                    "Camera height while crouching.",
                    new AcceptableValueRange<float>(0.05f, 1.2f),
                    new ConfigurationManagerAttributes { Order = 55 }));

            MinimumFov = Config.Bind(
                "Camera", "Minimum Field Of View", 5f,
                Ordered("How far the lens zoom can go.",
                    new AcceptableValueRange<float>(1f, 50f), 75));

            // -------------------------------------------------------- collision
            CameraCollision = Config.Bind(
                "Camera Collision", "Camera Collision", true,
                Ordered("Keep the camera from going through walls.",
                        100));

            CollisionIgnoreCharacters = Config.Bind(
                "Camera Collision", "Ignore Characters", true,
                Ordered("The camera passes through people.", 90));

            CollisionIgnoreMapObjects = Config.Bind(
                "Camera Collision", "Ignore Map Objects", true,
                Ordered("The camera passes through small props.", 85));

            CollisionIgnoreGround = Config.Bind(
                "Camera Collision", "Ignore Ground", false,
                Ordered("The camera may go below the floor.", 80));

            // ------------------------------------------------------ map buttons
            MapButtonTracking = Config.Bind(
                "Map Buttons", "Map Button Tracking", MapButtonMode.TrackMarkers,
                new ConfigDescription(
                    "TrackMarkers - in third person, each location button floats over the doorway " +
                    "or spot it leads to.\n\nClampToScreenEdge - the same, but buttons for places " +
                    "off screen stay at the edge of the screen.", null,
                    new ConfigurationManagerAttributes { Order = 100 }));

            MapButtonHeight = Config.Bind(
                "Map Buttons", "Button Height Offset", 1.5f,
                new ConfigDescription(
                    "How high above their spot the location buttons float.",
                    new AcceptableValueRange<float>(0f, 3f),
                    new ConfigurationManagerAttributes { Order = 90 }));

            LookInButtonMode = Config.Bind(
                "Map Buttons", "Keep Looking In Button Mode", ButtonLookMode.Off,
                new ConfigDescription(
                    "Whether the mouse may turn the camera while the location buttons are up.",
                    null,
                    new ConfigurationManagerAttributes { Browsable = false }));

            MoveInButtonMode = Config.Bind(
                "Map Buttons", "Keep Walking In Button Mode", true,
                new ConfigDescription(
                    "Keep walking with the keyboard while the location buttons are shown.",
                    null,
                    new ConfigurationManagerAttributes { DispName = "Walk While Buttons Shown", Order = 80 }));

            WheelCycling = Config.Bind(
                "Map Buttons", "Cycle Characters With Wheel", true,
                Ordered("While the location buttons are shown, the mouse wheel steps through the " +
                        "characters on the map to mark them, instead of zooming.", 70));

            EdgeLookMargin = Config.Bind(
                "Map Buttons", "Screen Edge Look Margin", 12f,
                new ConfigDescription(
                    "How close to the edge of the screen the cursor has to get before the " +
                    "camera starts turning, as a percentage of the screen. Only used by the " +
                    "Screen edge look option above.",
                    new AcceptableValueRange<float>(2f, 40f),
                    new ConfigurationManagerAttributes { Order = 60 }));

            // Bound last: the settings window lists categories in the order they are bound.
            DebugInfo = Config.Bind(
                "Debug", "Debug Info", false,
                Ordered("Shows short notes on screen and writes extra details to the log, for " +
                        "reporting problems. Leave it off for normal play.", 0));
            CarryOver(DebugInfo, "General", "Testing Info");

            SetTogglePairDrawer();

            StartPovActive.SettingChanged += (_, __) => ViewMode.SetActive(StartPovActive.Value);
            CollisionIgnoreMapObjects.SettingChanged += (_, __) => RebuildLayerMask();
            CollisionIgnoreGround.SettingChanged += (_, __) => RebuildLayerMask();
            RebuildLayerMask();

            if (!Enabled.Value)
                Logger.LogInfo($"{Name} {Version} is disabled in its config; it stays idle " +
                               "until switched on.");

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(Hooks));
            HighPoly.Apply(_harmony);

            WarnAboutInteractKey();
            Mode.SettingChanged += (_, __) => WarnAboutInteractKey();
            InteractKey.SettingChanged += (_, __) => WarnAboutInteractKey();
            InteractKey2.SettingChanged += (_, __) => WarnAboutInteractKey();
            WarnAboutSprintKey();
            SprintKey.SettingChanged += (_, __) => WarnAboutSprintKey();
            SprintKey2.SettingChanged += (_, __) => WarnAboutSprintKey();
            RunningKey.SettingChanged += (_, __) => WarnAboutSprintKey();
            RunningKey2.SettingChanged += (_, __) => WarnAboutSprintKey();
            WarnAboutFirstPerson();
            SwitchOffOriginal();

            // Our own earlier project, now part of this one. Running both would act on every
            // click twice.
            if (IL2CPPChainloader.Instance.Plugins.ContainsKey("SVS_WalkAnywhere"))
                Logger.LogWarning("SVS_WalkAnywhere is installed as well. Its click-to-walk and " +
                                  "follow are part of SVS_FreeRoam now: remove SVS_WalkAnywhere.dll, " +
                                  "or every click is acted on twice.");

            Logger.LogInfo($"{Name} {Version} loaded. A rework of SVS_3rdPov by Junh2x.");
        }

        public override bool Unload()
        {
            MapButtonTracker.Invalidate();
            _harmony?.UnpatchSelf();
            return true;
        }


        /// <summary>
        /// The config file is named after the GUID, so the rename would otherwise start
        /// everyone from defaults and silently undo their bindings. On the first run only --
        /// when this plugin has no file of its own yet -- copy the previous one across. It
        /// must happen before the first Bind, which is what creates the new file. The old
        /// file is left where it is.
        /// </summary>
        private void CarryOverPreviousConfig()
        {
            try
            {
                string current = Config.ConfigFilePath;
                if (System.IO.File.Exists(current)) return;

                string previous = System.IO.Path.Combine(Paths.ConfigPath, PreviousGuid + ".cfg");
                if (!System.IO.File.Exists(previous)) return;

                System.IO.File.Copy(previous, current);
                Config.Reload();
                Logger.LogInfo($"Carried settings over from {PreviousGuid}.cfg.");
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not carry over the previous config, using defaults: " +
                                  e.Message);
            }
        }

        /// <summary>The config file as it stood before this run bound anything.</summary>
        private string[] _fileAtStart = Array.Empty<string>();

        /// <summary>
        /// Taken before the first Bind, because binding a new entry writes it into the file,
        /// and CarryOver needs to know which entries were there beforehand.
        /// </summary>
        private void SnapshotConfigFile()
        {
            try
            {
                if (System.IO.File.Exists(Config.ConfigFilePath))
                    _fileAtStart = System.IO.File.ReadAllLines(Config.ConfigFilePath);
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not read the config file for carrying settings over: " +
                                  e.Message);
            }
        }

        /// <summary>
        /// A setting moved to another section is a new entry to BepInEx, which would start it
        /// from its default and silently drop the user's choice. If the new entry did not exist
        /// before this run but the old one did, its value is copied across. The old line stays
        /// in the file, unused.
        /// </summary>
        private void CarryOver(ConfigEntryBase entry, string oldSection, string oldKey = null)
        {
            string key = entry.Definition.Key;
            if (ValueInFile(entry.Definition.Section, key) != null) return;

            oldKey = oldKey ?? key;
            string old = ValueInFile(oldSection, oldKey);
            if (old == null) return;

            try
            {
                entry.SetSerializedValue(old);
                Logger.LogInfo($"Carried \"{oldKey}\" [{oldSection}] over to \"{key}\" " +
                               $"[{entry.Definition.Section}].");
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Could not carry over \"{key}\": {e.Message}");
            }
        }

        private string ValueInFile(string section, string key) => ValueIn(_fileAtStart, section, key);

        /// <summary>
        /// The click features were a plugin of their own (SVS_WalkAnywhere) with its own config
        /// file. Their settings are taken from it the first time, key for key.
        /// </summary>
        private void CarryOverFromWalkAnywhere()
        {
            string[] lines;
            try
            {
                string path = System.IO.Path.Combine(Paths.ConfigPath, "SVS_WalkAnywhere.cfg");
                if (!System.IO.File.Exists(path)) return;
                lines = System.IO.File.ReadAllLines(path);
            }
            catch { return; }

            int carried = 0;
            var entries = new (ConfigEntryBase Entry, string OldKey)[]
            {
                (ClickWalk, "Enable Click To Walk"), (ClickWalkButton, null), (ClickWalkButton2, null),
                (ClickWalkMaxSnap, null), (ClickFollow, "Enable Click To Follow"), (FollowButton, null),
                (FollowButton2, null), (FollowMinDistance, null), (FollowIdealDistance, null),
                (FollowRunDistance, null), (FollowSpeedMode, null), (FollowRunAnimationAbove, null),
                (FollowCatchUpSpeed, null),
            };
            foreach (var (entry, oldKey) in entries)
            {
                // Only what this plugin's own file did not already hold before this run.
                if (ValueInFile(entry.Definition.Section, entry.Definition.Key) != null) continue;
                string old = ValueIn(lines, entry.Definition.Section, oldKey ?? entry.Definition.Key);
                if (old == null) continue;
                try
                {
                    entry.SetSerializedValue(old);
                    carried++;
                }
                catch { }
            }
            if (carried > 0) Logger.LogInfo($"Carried {carried} click settings over from SVS_WalkAnywhere.cfg.");
        }

        private static string ValueIn(string[] lines, string section, string key)
        {
            string current = null;
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    current = line.Substring(1, line.Length - 2);
                    continue;
                }
                if (current != section || line.StartsWith("#")) continue;

                int eq = line.IndexOf(" = ", StringComparison.Ordinal);
                if (eq > 0 && line.Substring(0, eq) == key) return line.Substring(eq + 3);
            }
            return null;
        }

        /// <summary>
        /// The gamepad layout changed (2026-10-02): A became Select, interact moved to Y and
        /// talk to X, and the PoV toggle moved to LB to free the left stick click for crouch.
        /// Configs still holding the old defaults are moved to the new ones, once, and the log
        /// says so; anything the player had chosen themselves is left alone.
        /// </summary>
        private void MigrateGamepadLayout()
        {
            var layout = Config.Bind("Hotkeys", "Gamepad Layout", 1,
                new ConfigDescription("Which gamepad layout this file was last updated to.", null,
                    new ConfigurationManagerAttributes { Browsable = false }));
            if (layout.Value >= 3) return;

            if (layout.Value < 2)
            {
                if (GamepadInteractKey.Value == KeyCode.JoystickButton0)
                {
                    GamepadInteractKey.Value = KeyCode.JoystickButton3;
                    Logger.LogInfo("Gamepad Interact Button moved from A to Y (the new layout: A selects).");
                }
                if (GamepadToggleKey.Value == KeyCode.JoystickButton8)
                    GamepadToggleKey.Value = KeyCode.JoystickButton4;
            }

            // Layout 3: LB is the idle button (sit, idle animations, the wheel). The toggle goes
            // back to the left stick click, and crouch from there to B.
            if (GamepadToggleKey.Value == KeyCode.JoystickButton4)
            {
                GamepadToggleKey.Value = KeyCode.JoystickButton8;
                Logger.LogInfo("Gamepad PoV Toggle Button moved from LB to the left stick click " +
                               "(the new layout: LB sits and plays idle animations).");
            }
            if (GamepadCrouchKey.Value == KeyCode.JoystickButton8)
            {
                GamepadCrouchKey.Value = KeyCode.JoystickButton1;
                Logger.LogInfo("Gamepad Crouch Button moved from the left stick click to B " +
                               "(the new layout: the left stick click switches third person).");
            }
            layout.Value = 3;
        }

        private ConfigEntry<KeyCode> BindSecond(string key)
        {
            return Config.Bind("Hotkeys", key, KeyCode.None,
                new ConfigDescription(
                    "Optional second binding for the key above.", null,
                    new ConfigurationManagerAttributes { Browsable = false }));
        }

        /// <summary>Wraps a description so ConfigurationManager sorts it where we want.</summary>
        private static ConfigDescription Ordered(string description, int order)
        {
            return new ConfigDescription(description, null,
                new ConfigurationManagerAttributes { Order = order });
        }

        private static ConfigDescription Ordered(string description, AcceptableValueBase range,
                                                 int order)
        {
            return new ConfigDescription(description, range,
                new ConfigurationManagerAttributes { Order = order });
        }

        private static ConfigDescription Paired(string description,
                                                Action<ConfigEntryBase> drawer, int order)
        {
            return new ConfigDescription(description, null,
                new ConfigurationManagerAttributes
                {
                    CustomDrawer = drawer,
                    Order = order,

                    // Our row draws its own Reset, which clears both halves of the pair.
                    // ConfigurationManager's can only reach the first, and cannot be detected
                    // at all when the first is already at its default -- no value changes, so
                    // no SettingChanged fires.
                    HideDefaultButton = true,
                });
        }

        /// <summary>
        /// Points a pair's drawer at both halves. The ConfigDescription is fixed once the
        /// setting is bound, so the attributes object it already carries is updated in place.
        /// </summary>
        private static void SetPairDrawer(ConfigEntry<KeyCode> first, ConfigEntry<KeyCode> second)
        {
            var attributes = FindAttributes(first);
            if (attributes != null) attributes.CustomDrawer = PairDrawer.Keys(first, second);
        }

        private static void SetTogglePairDrawer()
        {
            var attributes = FindAttributes(MoveInButtonMode);
            if (attributes != null)
                attributes.CustomDrawer = PairDrawer.WalkAndLook(MoveInButtonMode, LookInButtonMode);
        }

        private static void SetDrawer(ConfigEntryBase entry, Action<ConfigEntryBase> drawer)
        {
            var attributes = FindAttributes(entry);
            if (attributes != null) attributes.CustomDrawer = drawer;
        }

        private static ConfigurationManagerAttributes FindAttributes(ConfigEntryBase entry)
        {
            foreach (var tag in entry.Description.Tags)
                if (tag is ConfigurationManagerAttributes attributes)
                    return attributes;
            return null;
        }

        private static void RebuildLayerMask()
        {
            int mask = ~0;
            if (CollisionIgnoreMapObjects.Value) mask = Exclude(mask, "Map");
            if (CollisionIgnoreGround.Value) mask = Exclude(mask, "Ground");

            CollisionLayerMask = mask;
        }

        private static int Exclude(int mask, string layerName)
        {
            int layer = LayerMask.NameToLayer(layerName);
            if (layer < 0)
            {
                Logger.LogWarning($"No physics layer called '{layerName}' in this game.");
                return mask;
            }
            return mask & ~(1 << layer);
        }

        /// <summary>
        /// Forward Mode used to move the interact button out of the way by itself. That is gone
        /// -- the key is yours to set -- so the clash it prevented is warned about instead of
        /// being quietly corrected.
        /// </summary>
        private static void WarnAboutInteractKey()
        {
            if (Mode.Value == ForwardMode.Off) return;

            var walk = Mode.Value == ForwardMode.LeftClickForward ? KeyCode.Mouse0 : KeyCode.Mouse1;
            if (InteractKey.Value != walk && InteractKey2.Value != walk) return;

            Logger.LogWarning(
                $"Interact Key is bound to {walk}, which Forward Mode is also using to walk. " +
                "Both will fire on the same button. Bind interact to the other mouse button, or " +
                "set Forward Mode to Off.");
        }

        /// <summary>
        /// Sprint forces running, so sharing a key with Walk/Run means that key sprints and
        /// never walks. Said once in the log; the bindings are left as they are.
        /// </summary>
        private static void WarnAboutSprintKey()
        {
            foreach (var sprint in new[] { SprintKey.Value, SprintKey2.Value })
            {
                if (sprint == KeyCode.None) continue;
                if (sprint != RunningKey.Value && sprint != RunningKey2.Value) continue;

                Logger.LogWarning(
                    $"Sprint Key and Walk/Run Key are both on {sprint}, so it will sprint and " +
                    "never walk. The defaults are now Left Shift to sprint and Left Control to " +
                    "walk; change one of them in the Hotkeys section.");
                return;
            }
        }

        private static void WarnAboutFirstPerson()
        {
            if (HideCharacterBelow.Value <= 0f) return;
            if (HideCharacterBelow.Value >= MinZoom.Value) return;

            Logger.LogWarning(
                $"Hide Character Below Distance ({HideCharacterBelow.Value}) is closer than Min " +
                $"Zoom Distance ({MinZoom.Value}), so the camera can never reach it and your " +
                "character will never be hidden.");
        }

    }
}
