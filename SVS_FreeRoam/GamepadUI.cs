using System;
using System.Collections.Generic;
using ILLGames.Unity.Component;
using SV;
using SV.CharaSelectScene;
using SV.Config;
using SV.CorrelationDiagramScene;
using SV.MapSelectScene;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SVS_FreeRoam
{
    /// <summary>
    /// Gamepad control of the buttons on screen while walking around: the overworld buttons,
    /// the screens they open (Map Select, Meet Up, Everyone, Jizo, options, help) and their
    /// windows, and conversations (GamepadADV). H is left alone for now.
    ///
    ///   D-pad          enters button mode and moves between buttons
    ///   Select (A)     presses the selection; with nothing selected, enters button mode as
    ///                  the D-pad does, or in a conversation moves the text on
    ///   Back (B)       folds away a list the last press opened (the Everyone list), else the
    ///                  open screen's back button, else leaves button mode; closes the F2 list
    ///   Menu (Start)   the options window from the map; closes an open screen
    ///   Shortcut (Back/View)  the map button chosen in Gamepad > Shortcut Button Opens
    ///
    /// Nothing is ever selected by itself -- not even a conversation's choices, which a mouse
    /// player would otherwise see framed. The first press picks the overworld's Select Map,
    /// a screen's top button, or of the choices the one that way (Select: the middle one).
    ///
    ///   LB / RB        on Map Select, Meet Up, options, shortcuts (F2) or help (F3): the
    ///                  previous / next of those screens
    ///
    /// Buttons are found generically: every visible, clickable Selectable on the top-most layer,
    /// plus the character portraits of Meet Up and the Jizo screen (SexualTargetUI), which
    /// are not Selectables but take clicks through pointer events. FINDINGS.md §22.
    /// </summary>
    internal static class GamepadUI
    {
        internal static bool Active { get; private set; }

        private static Component _selected;
        private static GameObject _frameRoot;
        private static RectTransform[] _frameEdges;

        // After pressing something, new buttons appearing (the Everyone list sliding out) are
        // jumped to automatically.
        private static HashSet<IntPtr> _beforePress;
        private static float _watchUntil;

        private static bool _wasChoosing;

        // The button whose press opened a list (btnEveryOne), and that list's buttons: Back
        // folds the list away and returns there, instead of leaving button mode.
        private static Component _pressed;
        private static Component _subParent;
        private static HashSet<IntPtr> _subChildren;

        internal static void Tick(bool allowed, bool inConversation)
        {
            try
            {
                if (!allowed)
                {
                    Exit();
                    return;
                }
                // The animation wheel has the controller: A plays from it, the D-pad turns its pages.
                if (IdleWheel.IsOpen) return;

                bool a = Keys.Down(Plugin.GamepadSelectKey, Plugin.GamepadSelectKey2);
                bool b = Keys.Down(Plugin.GamepadBackKey, Plugin.GamepadBackKey2);

                // The F2 shortcut list: nothing to choose on it, but Back closes it.
                if (b && CloseShortcutList())
                {
                    _usedBFrame = Time.frameCount;
                    return;
                }

                var scenario = inConversation ? GamepadADV.Scenario() : null;
                if (!inConversation) GamepadADV.Forget();
                bool choosing = scenario != null && scenario.IsChoice;

                // A choice was just made: back to Select-moves-the-text-on.
                if (_wasChoosing && !choosing) Exit();
                _wasChoosing = choosing;

                var candidates = choosing ? GamepadADV.Choices(scenario) : Candidates();
                bool overworld = !inConversation && IsOverworld(candidates);

                // LB / RB on Map Select, Meet Up, options, shortcuts or help: the next of them.
                if (!inConversation && CycleScreens(candidates)) return;

                // Menu (Start): the options window from the map; on any other screen, out of it.
                // Shortcut (Back/View): the map button the player chose for it.
                if (!inConversation && Keys.Down(Plugin.GamepadMenuKey, Plugin.GamepadMenuKey2))
                {
                    if (overworld) PressNamed("btnOption");
                    else Back(candidates, overworld: false);
                    return;
                }
                if (!inConversation && Keys.Down(Plugin.GamepadShortcutKey, Plugin.GamepadShortcutKey2))
                {
                    // From the map it opens its screen; on a screen it closes it again.
                    if (overworld)
                    {
                        string target = ShortcutButton(Plugin.GamepadShortcutOpens.Value);
                        if (target != null) PressNamed(target);
                    }
                    else Back(candidates, overworld: false);
                    return;
                }

                // The left stick joins the D-pad everywhere but the bare overworld, where it
                // walks the character.
                var direction = NavDirection(useStick: !overworld);

                if (!Active)
                {
                    // In a conversation, Select with nothing selected moves the text on.
                    if (inConversation && !choosing && a)
                    {
                        GamepadADV.Advance(scenario);
                        return;
                    }

                    // Back with nothing selected: a screen that is open (Map Select, Meet Up)
                    // is backed out of. On the overworld Back is for stopping a walk, and that
                    // is ThirdPersonController's.
                    if (!inConversation && !overworld && b)
                    {
                        _usedBFrame = Time.frameCount;
                        Back(candidates, overworld: false);
                        return;
                    }

                    // The D-pad, the stick where it navigates, or Select: start choosing.
                    if (direction == Vector2.zero && !a) return;
                    Select(choosing ? ChoiceStart(candidates, direction)
                         : overworld ? OverworldStart(candidates)
                         : TopMost(candidates));
                    Active = _selected != null;
                    DrawFrame();
                    return;
                }

                WatchForNewButtons(candidates);

                // The selection went away (its screen closed): start again at the top of
                // whatever is showing now.
                if (!Contains(candidates, _selected))
                {
                    // Back on the bare overworld: a screen closed (a map or a character was
                    // chosen, or it was backed out of). Nothing to carry on choosing there.
                    if (overworld)
                    {
                        Exit();
                        return;
                    }
                    Select(choosing ? Middle(candidates) : TopMost(candidates));
                    if (_selected == null)
                    {
                        Exit();
                        return;
                    }
                }

                if (direction != Vector2.zero) Move(candidates, direction);

                if (a) PressSelected(candidates);
                // A choice can't be backed out of; Back leaves button mode everywhere else --
                // after folding away a list the last press opened.
                else if (b && !choosing)
                {
                    _usedBFrame = Time.frameCount;
                    if (!FoldSubList(candidates)) Back(candidates, overworld);
                }

                DrawFrame();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("Gamepad UI failed, leaving button mode: " + e.Message);
                Exit();
            }
        }

        private static int _usedBFrame = -1;

        /// <summary>True on a frame B was used here, so it doesn't also stop a walk.</summary>
        internal static bool UsedBThisFrame => _usedBFrame == Time.frameCount;

        // ---------------------------------------------------------- input

        private static Vector2 _heldDirection;
        private static float _nextRepeat;

        /// <summary>Held this long before a direction starts repeating, then this often.</summary>
        private const float RepeatDelay = 0.4f, RepeatInterval = 0.12f;

        /// <summary>
        /// The direction to move the selection this frame, or zero. From the D-pad, and the
        /// left stick when allowed. A direction held down steps once, then after a pause
        /// repeats, so a long list can be skimmed without pressing for every entry.
        /// </summary>
        private static Vector2 NavDirection(bool useStick)
        {
            var held = Vector2.zero;
            if (Gamepad.Held(Gamepad.DPadUp)) held = Vector2.up;
            else if (Gamepad.Held(Gamepad.DPadDown)) held = Vector2.down;
            else if (Gamepad.Held(Gamepad.DPadLeft)) held = Vector2.left;
            else if (Gamepad.Held(Gamepad.DPadRight)) held = Vector2.right;
            else if (useStick)
            {
                float x = Input.GetAxisRaw("Horizontal"), y = Input.GetAxisRaw("Vertical");
                if (Mathf.Max(Mathf.Abs(x), Mathf.Abs(y)) >= 0.6f)
                    held = Mathf.Abs(y) >= Mathf.Abs(x)
                        ? (y > 0f ? Vector2.up : Vector2.down)
                        : (x > 0f ? Vector2.right : Vector2.left);
            }

            float now = Time.unscaledTime;
            if (held == Vector2.zero)
            {
                _heldDirection = Vector2.zero;
                return Vector2.zero;
            }
            if (held != _heldDirection)
            {
                _heldDirection = held;
                _nextRepeat = now + RepeatDelay;
                return held;
            }
            if (now < _nextRepeat) return Vector2.zero;
            _nextRepeat = now + RepeatInterval;
            return held;
        }

        internal static void Exit()
        {
            if (!Active && _frameRoot == null) return;
            Hover(_selected, false);
            _selected = null;
            _beforePress = null;
            _pressed = _subParent = null;
            _subChildren = null;
            Active = false;
            if (_frameRoot != null) _frameRoot.SetActive(false);
        }

        // ------------------------------------------------------- overworld

        /// <summary>
        /// The bare overworld is what is choosable when btnOpenClose is among the choices:
        /// the screens it opens hide it, and the pause-type windows (options, help, go home)
        /// sit on a higher layer, so it is not among theirs.
        /// </summary>
        private static bool IsOverworld(List<Component> candidates)
        {
            foreach (var c in candidates)
                if (c.gameObject.name == "btnOpenClose") return true;
            return false;
        }

        /// <summary>
        /// Where the overworld selection starts: always Select Map, so the way down the column
        /// is the same every time. Never the modded btn_Switch at the far left, which is only
        /// reached by pressing left.
        /// </summary>
        private static Component OverworldStart(List<Component> candidates)
        {
            foreach (var c in candidates)
                if (c.gameObject.name == "btnMap") return c;

            var column = new List<Component>();
            foreach (var c in candidates)
                if (c.gameObject.name != "btn_Switch") column.Add(c);
            return TopMost(column);
        }

        // ------------------------------------------------------- selection

        /// <summary>
        /// The first choice to highlight: the one furthest the way the D-pad or stick was
        /// pressed (right: the right-hand choice), or with Select the middle one. A second
        /// Select then picks it.
        /// </summary>
        private static Component ChoiceStart(List<Component> candidates, Vector2 direction)
        {
            if (direction == Vector2.zero) return Middle(candidates);
            Component best = null;
            float bestScore = float.MinValue;
            foreach (var c in candidates)
            {
                float score = Vector2.Dot(Center(c), direction);
                if (score <= bestScore) continue;
                bestScore = score;
                best = c;
            }
            return best;
        }

        /// <summary>The one nearest the middle of them all: of three choices side by side, the
        /// middle one.</summary>
        private static Component Middle(List<Component> candidates)
        {
            if (candidates.Count == 0) return null;
            var mean = Vector2.zero;
            foreach (var c in candidates) mean += Center(c);
            mean /= candidates.Count;

            Component best = null;
            float bestDistance = float.MaxValue;
            foreach (var c in candidates)
            {
                float d = (Center(c) - mean).sqrMagnitude;
                if (d >= bestDistance - 0.01f) continue;     // ties keep the earlier one
                bestDistance = d;
                best = c;
            }
            return best;
        }

        private static Component TopMost(List<Component> candidates)
        {
            Component best = null;
            float bestScore = float.MinValue;
            foreach (var c in candidates)
            {
                var p = Center(c);
                float score = p.y - p.x * 0.001f;       // highest; ties go to the leftmost
                if (score <= bestScore) continue;
                bestScore = score;
                best = c;
            }
            return best;
        }

        /// <summary>
        /// The nearest button in the pressed direction. Buttons within 45 degrees of it come
        /// first, so right from Beach on Map Select is School Gate, level with it, not
        /// Station, which is nearer but mostly upward. Only when nothing lies in that cone is
        /// anything further round considered, and past the last one it wraps to the far end.
        /// </summary>
        private static void Move(List<Component> candidates, Vector2 direction)
        {
            if (_selected == null) return;
            var from = Center(_selected);

            // Only within 45 degrees of the direction pressed, so up and down on the overworld
            // stay in the column and never jump sideways to btn_Switch.
            Component inCone = null;
            float coneScore = float.MaxValue;
            foreach (var c in candidates)
            {
                if (c.Pointer == _selected.Pointer) continue;
                var delta = Center(c) - from;
                float along = Vector2.Dot(delta, direction);
                if (along <= 1f) continue;
                float across = Mathf.Abs(delta.x * direction.y - delta.y * direction.x);
                if (across > along) continue;
                float score = along + across * 2f;
                if (score < coneScore) { coneScore = score; inCone = c; }
            }

            var best = inCone ?? Wrap(candidates, from, direction);
            if (best != null) Select(best);
        }

        /// <summary>
        /// Past the end: the furthest button back within 45 degrees, so down from Next reaches
        /// btnOpenClose at the very top, slightly offset from the column, rather than stopping
        /// at Map.
        /// </summary>
        private static Component Wrap(List<Component> candidates, Vector2 from, Vector2 direction)
        {
            Component best = null;
            float bestScore = float.MinValue;
            foreach (var c in candidates)
            {
                if (c.Pointer == _selected.Pointer) continue;
                var delta = Center(c) - from;
                float along = Vector2.Dot(delta, direction);
                if (along >= 0f) continue;
                float across = Mathf.Abs(delta.x * direction.y - delta.y * direction.x);
                if (across > -along) continue;
                float score = -along - across;
                if (score <= bestScore) continue;
                bestScore = score;
                best = c;
            }
            return best;
        }

        private static void Select(Component c)
        {
            if (_selected != null && c != null && _selected.Pointer == c.Pointer) return;
            Hover(_selected, false);
            _selected = c;
            Hover(_selected, true);
        }

        private static int _freshCount;
        private static float _freshSettleAt;

        /// <summary>
        /// For two seconds after a press, buttons that were not there before -- the Everyone
        /// list -- take the selection, at their top. The list slides out behind a mask, so its
        /// buttons become visible one after another, bottom first; choosing on the first one
        /// seen landed on the bottom. So it waits until no more have appeared for a moment.
        /// </summary>
        private static void WatchForNewButtons(List<Component> candidates)
        {
            if (_beforePress == null) return;
            float now = Time.unscaledTime;

            if (!Contains(candidates, _selected))
            {
                _beforePress = null;
                return;
            }

            var fresh = new List<Component>();
            foreach (var c in candidates)
                if (!_beforePress.Contains(c.Pointer)) fresh.Add(c);

            if (fresh.Count != _freshCount)
            {
                _freshCount = fresh.Count;
                _freshSettleAt = now + 0.25f;
            }

            if (fresh.Count > 0 && now >= _freshSettleAt)
            {
                _beforePress = null;
                _subParent = _pressed;
                _subChildren = new HashSet<IntPtr>();
                foreach (var c in fresh) _subChildren.Add(c.Pointer);
                Select(TopMost(fresh));
                return;
            }
            if (now > _watchUntil) _beforePress = null;
        }

        // --------------------------------------------------------- actions

        private static void PressSelected(List<Component> candidates)
        {
            _beforePress = new HashSet<IntPtr>();
            foreach (var c in candidates) _beforePress.Add(c.Pointer);
            _watchUntil = Time.unscaledTime + 2f;
            _freshCount = 0;
            _pressed = _selected;

            Press(_selected);
        }

        /// <summary>
        /// Back inside a list the last press opened (the Everyone list from btnEveryOne): press
        /// the button that opened it again, which folds it away, and go back to that button.
        /// </summary>
        private static bool FoldSubList(List<Component> candidates)
        {
            var parent = _subParent;
            var children = _subChildren;
            if (parent == null || children == null) return false;
            if (_selected == null || !children.Contains(_selected.Pointer)) return false;
            _subParent = null;
            _subChildren = null;
            if (!Contains(candidates, parent)) return false;

            Press(parent);
            Select(parent);
            return true;
        }

        /// <summary>A button on screen by name, pressed if the game has it switched on --
        /// whether or not the overworld's column of buttons is folded away.</summary>
        private static bool PressNamed(string name)
        {
            var array = Selectable.allSelectablesArray;
            if (array == null) return false;
            foreach (var s in array)
            {
                if (s == null || !s.isActiveAndEnabled || !s.interactable) continue;
                if (s.gameObject.name != name) continue;
                Press(s);
                return true;
            }
            return false;
        }

        private static string ShortcutButton(GamepadShortcut shortcut)
        {
            switch (shortcut)
            {
                case GamepadShortcut.MapSelect: return "btnMap";
                case GamepadShortcut.MeetUp: return "btnChara";
                case GamepadShortcut.Everyone: return "btnEveryone";
                case GamepadShortcut.Options: return "btnOption";
                case GamepadShortcut.Help: return "btnHelp";
                case GamepadShortcut.GoHome: return "btnGotoRoom";
                case GamepadShortcut.SwitchCharacter: return "btn_Switch";
                default: return null;
            }
        }

        // ---------------------------------------------------- cycling screens

        /// <summary>The screens LB and RB step between, in order. All of them pause the game.</summary>
        private enum CycledScreen { None = -1, MapSelect, MeetUp, Options, Shortcuts, Help }
        private const int ScreenCount = 5;

        private static CycledScreen _cycleTo = CycledScreen.None;
        private static float _cycleGiveUpAt, _cycleNextStep;
        private static bool _cycleOpened;

        /// <summary>One of those screens is up, or being changed to: LB is theirs, not the
        /// PoV toggle's.</summary>
        internal static bool OnCycledScreen => _cycleTo != CycledScreen.None || CurrentScreen() != CycledScreen.None;

        private static CycledScreen CurrentScreen()
        {
            try
            {
                if (ShortcutViewDialog.IsActive) return CycledScreen.Shortcuts;
                if (ConfigWindow.IsActive) return CycledScreen.Options;
                if (HelpWindow.IsActive) return CycledScreen.Help;
                var mapSelect = SingletonInitializer<MapSelect>._instance;
                if (mapSelect != null && mapSelect.IsOpen()) return CycledScreen.MapSelect;
                var charaSelect = SingletonInitializer<CharaSelect>._instance;
                if (charaSelect != null && charaSelect.IsOpen()) return CycledScreen.MeetUp;
            }
            catch { }
            return CycledScreen.None;
        }

        /// <summary>
        /// LB / RB on one of the screens: close it, wait for the map, open the next. Several
        /// frames' work, so it carries on from one Tick to the next; true while it does (and
        /// on the frame it starts), so nothing else reads the controller meanwhile.
        /// </summary>
        private static bool CycleScreens(List<Component> candidates)
        {
            float now = Time.unscaledTime;
            var current = CurrentScreen();

            if (_cycleTo == CycledScreen.None)
            {
                if (current == CycledScreen.None) return false;
                int step = Keys.Down(Plugin.GamepadNextScreenKey, Plugin.GamepadNextScreenKey2) ? 1
                         : Keys.Down(Plugin.GamepadPrevScreenKey, Plugin.GamepadPrevScreenKey2) ? -1 : 0;
                if (step == 0) return false;

                _cycleTo = (CycledScreen)(((int)current + step + ScreenCount) % ScreenCount);
                _cycleGiveUpAt = now + 4f;
                _cycleNextStep = 0f;
                _cycleOpened = false;
                Exit();     // the selection belonged to the screen being left
                return true;
            }

            if (current == _cycleTo || now > _cycleGiveUpAt)
            {
                _cycleTo = CycledScreen.None;
                return true;
            }
            if (now < _cycleNextStep) return true;
            _cycleNextStep = now + 0.3f;

            if (current != CycledScreen.None) CloseScreen(current, candidates);
            else if (!_cycleOpened) _cycleOpened = OpenScreen(_cycleTo);
            return true;
        }

        private static void CloseScreen(CycledScreen screen, List<Component> candidates)
        {
            switch (screen)
            {
                case CycledScreen.Shortcuts:
                    CloseShortcutList();
                    return;
                case CycledScreen.MeetUp:
                    var charaSelect = SingletonInitializer<CharaSelect>._instance;
                    if (charaSelect != null && charaSelect._btnBack != null)
                    {
                        charaSelect._btnBack.onClick.Invoke();
                        return;
                    }
                    break;
            }
            if (PressBackByName(candidates)) return;
            if (screen == CycledScreen.Options) ConfigWindow.Unload();
        }

        /// <summary>The way the game opens each: the map's own buttons where it has one, else
        /// what its F key does.</summary>
        private static bool OpenScreen(CycledScreen screen)
        {
            switch (screen)
            {
                case CycledScreen.MapSelect: return PressNamed("btnMap");
                case CycledScreen.MeetUp: return PressNamed("btnChara");
                case CycledScreen.Options:
                    if (PressNamed("btnOption")) return true;
                    ConfigWindow.Load();
                    return true;
                case CycledScreen.Shortcuts:
                    ShortcutViewDialog.Load();
                    return true;
                case CycledScreen.Help:
                    if (PressNamed("btnHelp")) return true;
                    HelpWindow.Load();
                    return true;
            }
            return false;
        }

        /// <summary>The list of keyboard shortcuts (F2) has no buttons; Back closes it.</summary>
        private static bool CloseShortcutList()
        {
            var dialog = SingletonInitializer<ShortcutViewDialog>._instance;
            if (dialog == null || !ShortcutViewDialog.IsActive) return false;
            dialog.OnBack();
            return true;
        }

        private static void Press(Component c)
        {
            if (c == null) return;

            var button = c.TryCast<Button>();
            if (button != null)
            {
                button.onClick.Invoke();
                return;
            }

            var toggle = c.TryCast<Toggle>();
            if (toggle != null)
            {
                toggle.isOn = !toggle.isOn;
                return;
            }

            // Portraits and anything else: a click the way the mouse sends one, delivered to
            // whichever object up from the picture handles it.
            var es = EventSystem.current;
            if (es == null) return;
            ExecuteEvents.ExecuteHierarchy(PointerTarget(c), new PointerEventData(es),
                                           ExecuteEvents.pointerClickHandler);
        }

        /// <summary>
        /// B: the open screen's back button -- by name (Map Select's btnBackBG/btnBack), or
        /// Meet Up's own. On the bare overworld, or with none found, leave button mode.
        /// </summary>
        private static bool PressBackByName(List<Component> candidates)
        {
            foreach (var c in candidates)
            {
                string n = c.gameObject.name.ToLowerInvariant();
                if (n.Contains("openclose")) continue;
                if (n.Contains("back") || n.Contains("close") || n.Contains("cancel") ||
                    n.Contains("return") || n.Contains("exit") || n.EndsWith("no"))
                {
                    Press(c);
                    return true;
                }
            }
            return false;
        }

        private static void Back(List<Component> candidates, bool overworld)
        {
            if (!overworld)
            {
                if (PressBackByName(candidates)) return;

                var charaSelect = SingletonInitializer<CharaSelect>._instance;
                if (charaSelect != null && charaSelect.IsOpen() && charaSelect._btnBack != null &&
                    charaSelect._btnBack.isActiveAndEnabled)
                {
                    charaSelect._btnBack.onClick.Invoke();
                    return;
                }

                var diagram = SingletonInitializer<CorrelationDiagram>._instance;
                if (diagram != null && diagram._isOpen && diagram._btnBack != null &&
                    diagram._btnBack.isActiveAndEnabled)
                {
                    diagram._btnBack.onClick.Invoke();
                    return;
                }
            }
            Exit();
        }

        /// <summary>Imitates the mouse resting on it, so hover effects show (map names on Map
        /// Select, the portrait highlight on Meet Up).</summary>
        private static void Hover(Component c, bool on)
        {
            if (c == null) return;
            try
            {
                var es = EventSystem.current;
                if (es == null) return;
                var data = new PointerEventData(es);
                var target = PointerTarget(c);
                if (on) ExecuteEvents.ExecuteHierarchy(target, data, ExecuteEvents.pointerEnterHandler);
                else ExecuteEvents.ExecuteHierarchy(target, data, ExecuteEvents.pointerExitHandler);
            }
            catch { }
        }

        /// <summary>A portrait takes pointer events on its picture; everything else on itself.</summary>
        private static GameObject PointerTarget(Component c)
        {
            var portrait = c.TryCast<SexualTargetUI>();
            if (portrait != null)
            {
                var picture = portrait._imgChara;
                if (picture != null) return picture.gameObject;
            }
            return c.gameObject;
        }

        // ------------------------------------------------------ candidates

        /// <summary>
        /// Everything choosable on the top-most layer: root canvases sharing the highest
        /// sorting order. Sorting order alone, since the overworld is two root canvases at the
        /// same order (MoveCanvas and GameCanvas); see FINDINGS.md §22.
        /// </summary>
        private static List<Component> Candidates()
        {
            var all = new List<Component>();
            int bestSort = int.MinValue;

            void Consider(Component c)
            {
                var root = c.GetComponentInParent<Canvas>()?.rootCanvas;
                if (root == null) return;
                int sort = root.sortingOrder;
                if (sort > bestSort)
                {
                    bestSort = sort;
                    all.Clear();
                }
                if (sort == bestSort) all.Add(c);
            }

            var array = Selectable.allSelectablesArray;
            if (array != null)
                foreach (var s in array)
                    if (IsCandidate(s)) Consider(s);

            // Character portraits (Meet Up, and the Jizo screen's character list, whose
            // CorrelationUI is the same kind of portrait), only while those screens are up.
            var charaSelect = SingletonInitializer<CharaSelect>._instance;
            if (charaSelect != null && charaSelect.IsOpen())
            {
                foreach (var p in charaSelect.GetComponentsInChildren<SexualTargetUI>())
                    if (IsCandidate(p) && p.HasChara()) Consider(p);
            }
            var diagram = SingletonInitializer<CorrelationDiagram>._instance;
            if (diagram != null && diagram._isOpen)
            {
                foreach (var p in diagram.GetComponentsInChildren<SexualTargetUI>())
                    if (IsCandidate(p) && p.HasChara()) Consider(p);
            }
            return all;
        }

        private static bool Contains(List<Component> list, Component c)
        {
            if (c == null) return false;
            foreach (var x in list) if (x.Pointer == c.Pointer) return true;
            return false;
        }

        private static bool IsCandidate(Component c)
        {
            if (c == null) return false;
            try
            {
                var behaviour = c.TryCast<Behaviour>();
                if (behaviour != null && !behaviour.isActiveAndEnabled) return false;

                var selectable = c.TryCast<Selectable>();
                if (selectable != null && !selectable.interactable) return false;

                var rect = c.transform.TryCast<RectTransform>();
                if (rect == null || rect.rect.width < 2f || rect.rect.height < 2f) return false;

                var p = Center(c);
                if (p.x < 0f || p.y < 0f || p.x > Screen.width || p.y > Screen.height) return false;

                // Hidden by a fade, or parked off screen by the location-button tracker.
                foreach (var group in c.GetComponentsInParent<CanvasGroup>())
                    if (group.alpha <= 0.01f || !group.interactable) return false;

                // Slid out of sight behind a mask: the Everyone list keeps its buttons switched
                // on while closed, just moved outside the mask's area.
                foreach (var mask in c.GetComponentsInParent<RectMask2D>())
                    if (!ContainsPoint(mask.transform, p)) return false;
                foreach (var mask in c.GetComponentsInParent<Mask>())
                    if (mask.enabled && !ContainsPoint(mask.transform, p)) return false;

                // SVS_CustomGameBalance's btn_Switch sits on the character panel, which third
                // person keeps switched on; it only shows while someone is aimed at.
                if (c.gameObject.name == "btn_Switch" && ThirdPersonController.IsPovRunning &&
                    ThirdPersonController.AimedCharacter == null) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ContainsPoint(Transform maskTransform, Vector2 point)
        {
            var rect = maskTransform.TryCast<RectTransform>();
            if (rect == null) return true;
            return RectTransformUtility.RectangleContainsScreenPoint(rect, point, CanvasCamera(maskTransform));
        }

        private static Camera CanvasCamera(Component c)
        {
            var canvas = c.GetComponentInParent<Canvas>()?.rootCanvas;
            return canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null : canvas.worldCamera;
        }

        private static Vector2 Center(Component c)
        {
            GetScreenRect(c, out var min, out var max);
            return (min + max) * 0.5f;
        }

        private static void GetScreenRect(Component c, out Vector2 min, out Vector2 max)
        {
            var rect = c.transform.TryCast<RectTransform>();
            var cam = CanvasCamera(c);
            var r = rect.rect;
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, rect.TransformPoint(new Vector3(r.xMin, r.yMin, 0f)));
            Vector2 b = RectTransformUtility.WorldToScreenPoint(cam, rect.TransformPoint(new Vector3(r.xMax, r.yMax, 0f)));
            min = Vector2.Min(a, b);
            max = Vector2.Max(a, b);
        }

        // ----------------------------------------------------------- frame

        /// <summary>Four thin bars around the selection, on an overlay canvas above
        /// everything. Plain Unity UI parts, so nothing needs registering with IL2CPP.</summary>
        private static void DrawFrame()
        {
            if (_selected == null) return;
            EnsureFrame();
            _frameRoot.SetActive(true);

            GetScreenRect(_selected, out var min, out var max);
            const float pad = 4f, width = 3f;
            min -= new Vector2(pad, pad);
            max += new Vector2(pad, pad);

            Place(_frameEdges[0], new Vector2(min.x, max.y - width), new Vector2(max.x, max.y));
            Place(_frameEdges[1], new Vector2(min.x, min.y), new Vector2(max.x, min.y + width));
            Place(_frameEdges[2], new Vector2(min.x, min.y), new Vector2(min.x + width, max.y));
            Place(_frameEdges[3], new Vector2(max.x - width, min.y), new Vector2(max.x, max.y));
        }

        private static void Place(RectTransform edge, Vector2 min, Vector2 max)
        {
            edge.position = new Vector3((min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f, 0f);
            edge.sizeDelta = max - min;
        }

        private static void EnsureFrame()
        {
            if (_frameRoot != null) return;

            // Not kept across scenes: going back to the title would leave it on screen with
            // nothing to hide it. It is made again when next needed.
            _frameRoot = new GameObject("SVS_FreeRoam GamepadFrame");
            var canvas = _frameRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;

            _frameEdges = new RectTransform[4];
            for (int i = 0; i < 4; i++)
            {
                var edge = new GameObject("Edge" + i);
                edge.transform.SetParent(_frameRoot.transform, false);
                var image = edge.AddComponent<Image>();
                image.color = new Color(1f, 0.85f, 0.2f, 0.95f);
                image.raycastTarget = false;
                var rt = edge.GetComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = Vector2.zero;
                rt.pivot = new Vector2(0.5f, 0.5f);
                _frameEdges[i] = rt;
            }
        }
    }
}
