using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SVS_FreeRoam
{
    /// <summary>
    /// A few lines at the top of the screen for a few seconds, and the same in the log.
    /// Mostly Debug Info: things being tested that would otherwise be invisible, such as
    /// when Force High Poly had to step in for a scene; those do nothing with the setting off.
    /// Tell is for the few things the player must be told regardless.
    /// </summary>
    internal static class Notice
    {
        private const float Seconds = 5f;
        private const int MaxLines = 5;

        private static GameObject _root;
        private static Text _text;
        private static float _hideAt;
        private static bool _failed;
        private static readonly List<string> _lines = new List<string>();

        internal static bool On => Plugin.DebugInfo != null && Plugin.DebugInfo.Value;

        /// <summary>For the player rather than for testing: shown whatever Debug Info says,
        /// and for longer.</summary>
        internal static void Tell(string message, float seconds = 15f) => Show(message, true, seconds);

        internal static void Show(string message, bool always = false, float seconds = Seconds)
        {
            if (!On && !always) return;
            Plugin.Logger.LogInfo(message);
            if (_failed) return;

            try
            {
                Ensure();
                _lines.Add(message);
                while (_lines.Count > MaxLines) _lines.RemoveAt(0);
                _text.text = string.Join("\n", _lines);
                _root.SetActive(true);
                _hideAt = Mathf.Max(_hideAt, Time.unscaledTime + seconds);
            }
            catch (Exception e)
            {
                _failed = true;
                Plugin.Logger.LogWarning("Could not show a notice on screen: " + e.Message);
            }
        }

        /// <summary>Log only, when Debug Info is on.</summary>
        internal static void Log(string message)
        {
            if (On) Plugin.Logger.LogInfo(message);
        }

        internal static void Tick()
        {
            if (_root == null || !_root.activeSelf || Time.unscaledTime < _hideAt) return;
            _lines.Clear();
            _root.SetActive(false);
        }

        private static void Ensure()
        {
            if (_root != null) return;

            // Not kept across scenes, like the gamepad frame: nothing would hide it on the title.
            _root = new GameObject("SVS_FreeRoam Notice");
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32001;

            var go = new GameObject("Text");
            go.transform.SetParent(_root.transform, false);
            _text = go.AddComponent<Text>();
            _text.font = Font.CreateDynamicFontFromOSFont("Arial", 22);
            _text.fontSize = 22;
            _text.color = Color.white;
            _text.alignment = TextAnchor.UpperCenter;
            _text.horizontalOverflow = HorizontalWrapMode.Overflow;
            _text.verticalOverflow = VerticalWrapMode.Overflow;
            _text.raycastTarget = false;

            var outline = go.AddComponent<Outline>();
            outline.effectColor = Color.black;
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -60f);
            rt.sizeDelta = new Vector2(0f, 200f);
        }
    }
}
