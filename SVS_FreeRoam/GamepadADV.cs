using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.UI;

namespace SVS_FreeRoam
{
    /// <summary>
    /// The conversation (ADV) side of gamepad control: advancing the text, and where the
    /// choice buttons are. Everything about advancing was worked out for SVS_ADVFreeCamera;
    /// see D:\Code\CameraPlus\FINDINGS.md §2 "Blocking click-advance" for the traps, which
    /// are all respected here.
    /// </summary>
    internal static class GamepadADV
    {
        private static ADV.ADVScene _scene;
        private static float _nextLookup;

        /// <summary>The running conversation's scenario, or null.</summary>
        internal static ADV.TextScenario Scenario()
        {
            // FindObjectOfType is not cheap, so the scene is looked up at most once a second.
            if (_scene == null && Time.unscaledTime >= _nextLookup)
            {
                _nextLookup = Time.unscaledTime + 1f;
                _scene = Object.FindObjectOfType<ADV.ADVScene>();
            }

            try
            {
                var scenario = _scene != null ? _scene.Scenario : null;
                if (scenario != null && scenario.Regulate == null) scenario = null;
                if (scenario == null) _scene = null;
                return scenario;
            }
            catch
            {
                _scene = null;
                return null;
            }
        }

        internal static void Forget() => _scene = null;

        /// <summary>The choice buttons on screen, while the scenario is asking.</summary>
        internal static List<Component> Choices(ADV.TextScenario scenario)
        {
            var list = new List<Component>();
            var parent = scenario.Choices;
            if (parent == null) return list;
            foreach (var s in parent.GetComponentsInChildren<Selectable>())
                if (s != null && s.isActiveAndEnabled && s.interactable) list.Add(s);
            return list;
        }

        /// <summary>
        /// Advance the way a click would. A line still typing out is finished first, as the
        /// first click does; asking for the next line before that queues the request and
        /// makes everything feel seconds behind. Then the game's own click handler,
        /// MessageWindowProc, which does the bookkeeping RequestNextLine skips (without it the
        /// last voice line restarts). ClickNext is lifted around the call if something (such
        /// as SVS_ADVFreeCamera) is blocking clicks, and put straight back.
        /// </summary>
        internal static bool Advance(ADV.TextScenario scenario)
        {
            if (scenario == null || scenario.IsChoice) return false;

            // No frame on screen: there is no line, and asking for one ends the conversation.
            if (!MessageWindowOnScreen(scenario)) return false;

            // Paused: the frame is up, but the press is meant for the pause menu.
            if (Time.timeScale <= 0f) return false;

            var text = scenario.TextController;
            if (text != null && !text.IsCompleteDisplayText)
            {
                text.ForceCompleteDisplayText();
                return true;
            }

            var regulate = scenario.Regulate;
            bool blocked = (regulate.Control & ADV.Regulate.Controls.ClickNext) != 0;
            if (blocked) regulate.SubRegulate(ADV.Regulate.Controls.ClickNext);

            var info = new ADV.TextScenario.NextInfo();
            info.Set(true, true, false);
            scenario.MessageWindowProc(info);

            if (blocked) regulate.AddRegulate(ADV.Regulate.Controls.ClickNext);
            return true;
        }

        /// <summary>
        /// The message frame is really showing. It is not deactivated when a conversation
        /// ends -- it is moved off screen -- so being active is not enough; its rect must
        /// still overlap the screen, and its canvas and fade must be on.
        /// </summary>
        private static bool MessageWindowOnScreen(ADV.TextScenario scenario)
        {
            var window = scenario._messageWindow;
            if (window == null || !window.gameObject.activeInHierarchy) return false;

            var canvas = scenario._msgWindowCanvas;
            if (canvas != null && !canvas.enabled) return false;

            var group = window.GetComponentInParent<CanvasGroup>();
            if (group != null && group.alpha < 0.05f) return false;

            var corners = new Il2CppStructArray<Vector3>(4);
            window.GetWorldCorners(corners);

            Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera : null;
            Vector3 a = corners[0], b = corners[2];
            if (cam != null)
            {
                a = cam.WorldToScreenPoint(a);
                b = cam.WorldToScreenPoint(b);
            }
            var area = Rect.MinMaxRect(a.x, a.y, b.x, b.y);
            return area.Overlaps(new Rect(0f, 0f, Screen.width, Screen.height));
        }
    }
}
