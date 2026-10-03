using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SVS_FreeRoam
{
    /// <summary>
    /// The animation wheel: names in a circle, the one the pointer leans towards is chosen on
    /// release. With the cursor free the pointer is the mouse; in third person, where the
    /// cursor is locked, it is the mouse's movement since the wheel opened (the camera holds
    /// still meanwhile, CameraRig).
    /// </summary>
    internal static class IdleWheel
    {
        private const float Radius = 210f;             // at 1080p
        private const float DeadZone = 45f;

        private static GameObject _root;
        private static Font _font;
        private static Text _centre;
        private static readonly List<Text> _labels = new List<Text>();

        private static List<int> _ids;
        private static Vector2 _centrePos;
        private static Vector2 _pointer;
        private static bool _virtual;
        private static int _selected;

        internal static bool IsOpen { get; private set; }

        private static float Scale => Screen.height / 1080f;

        internal static void Open(List<int> ids, Vector2 centre, bool virtualPointer)
        {
            if (ids == null || ids.Count == 0) return;
            Ensure(ids.Count);

            _ids = ids;
            _virtual = virtualPointer;
            _pointer = Vector2.zero;
            _selected = -1;

            float scale = Scale, radius = Radius * scale, margin = radius + 130f * scale;
            centre.x = Mathf.Clamp(centre.x, margin, Mathf.Max(margin, Screen.width - margin));
            centre.y = Mathf.Clamp(centre.y, radius + 40f * scale, Mathf.Max(radius + 40f * scale, Screen.height - radius - 40f * scale));
            _centrePos = centre;

            int size = Mathf.RoundToInt(22f * scale);
            for (int i = 0; i < _labels.Count; i++)
            {
                var label = _labels[i];
                bool used = i < ids.Count;
                label.gameObject.SetActive(used);
                if (!used) continue;

                float angle = i * Mathf.PI * 2f / ids.Count;
                label.rectTransform.anchoredPosition =
                    centre + new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * radius;
                label.text = ClickIdler.Name(ids[i]);
                label.fontSize = size;
                label.color = Color.white;
            }

            _centre.rectTransform.anchoredPosition = centre;
            _centre.fontSize = size;
            _centre.text = "Release to cancel";

            _root.SetActive(true);
            IsOpen = true;
        }

        internal static void Tick()
        {
            if (!IsOpen) return;
            if (_root == null) { IsOpen = false; return; }

            float scale = Scale;
            Vector2 offset;
            if (_virtual)
            {
                _pointer += new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 14f * scale;
                _pointer = Vector2.ClampMagnitude(_pointer, Radius * scale);
                offset = _pointer;
            }
            else
            {
                offset = (Vector2)Input.mousePosition - _centrePos;
            }

            int selected = -1;
            if (offset.magnitude > DeadZone * scale)
            {
                // Clockwise from straight up, as the labels are laid out.
                float angle = Mathf.Atan2(offset.x, offset.y);
                if (angle < 0f) angle += Mathf.PI * 2f;
                selected = Mathf.RoundToInt(angle / (Mathf.PI * 2f / _ids.Count)) % _ids.Count;
            }
            if (selected == _selected) return;

            if (_selected >= 0) _labels[_selected].color = Color.white;
            if (selected >= 0) _labels[selected].color = Color.yellow;
            _centre.text = selected >= 0 ? ClickIdler.Name(_ids[selected]) : "Release to cancel";
            _selected = selected;
        }

        /// <summary>Closes the wheel. The chosen animation, or null when nothing was chosen.</summary>
        internal static int? Close()
        {
            if (!IsOpen) return null;
            IsOpen = false;
            if (_root != null) _root.SetActive(false);
            return _selected >= 0 && _ids != null && _selected < _ids.Count ? _ids[_selected] : (int?)null;
        }

        private static void Ensure(int count)
        {
            // Not kept across scenes, like the notice: rebuilt when the old one is gone.
            if (_root == null)
            {
                _labels.Clear();
                _root = new GameObject("SVS_FreeRoam IdleWheel");
                var canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 32002;
                _font = Font.CreateDynamicFontFromOSFont("Arial", 22);
                _centre = NewText("Centre");
                _centre.color = new Color(1f, 1f, 1f, 0.8f);
            }
            while (_labels.Count < count) _labels.Add(NewText("Label"));
        }

        private static Text NewText(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            var text = go.AddComponent<Text>();
            text.font = _font;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;

            var outline = go.AddComponent<Outline>();
            outline.effectColor = Color.black;
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            // Anchored to the bottom left corner, so positions are screen pixels.
            var rt = text.rectTransform;
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(260f, 40f);
            return text;
        }
    }
}
