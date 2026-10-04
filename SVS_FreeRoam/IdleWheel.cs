using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SVS_FreeRoam
{
    /// <summary>
    /// The radial menu: slots on a dark disc, the slot the pointer leans towards is the choice.
    /// More entries than slots make pages, turned with the mouse wheel. With the cursor free
    /// the pointer is the mouse; in third person, where the cursor is locked, it is the
    /// mouse's movement since the wheel opened (the camera holds still meanwhile, CameraRig).
    /// The disc and the highlight are drawn into textures here, so the plugin ships no image
    /// files. Who opens it decides when it closes (ClickIdler).
    /// </summary>
    internal static class IdleWheel
    {
        private const int MaxSlots = 16;
        private const float DiscRadius = 300f;         // all sizes at 1080p
        private const float LabelRadius = 195f;
        private const float Hole = 0.28f;              // the centre, as a part of the disc's radius
        private const int DiscTexture = 1024;
        private const int WedgeTexture = 512;
        private const float RimWidth = 0.016f;         // as a part of the disc's radius

        private static GameObject _root;
        private static RectTransform _wheel;
        private static RectTransform _wedge;
        private static Image _wedgeImage;
        private static int _wedgeSlots;
        private static Text _centre;
        private static Font _font;
        private static readonly Text[] _labels = new Text[MaxSlots];

        private static List<string> _items;
        private static Vector2 _centrePos;
        private static Vector2 _pointer;
        private static bool _virtual;
        private static bool _sticky;
        private static int _slots = 8;
        private static int _page;
        private static int _slot;

        internal static bool IsOpen { get; private set; }

        /// <summary>How many pages the open wheel has.</summary>
        internal static int Pages => _items == null ? 1 : Mathf.Max(1, (_items.Count + _slots - 1) / _slots);

        /// <summary>The wheel stays up after the button is let go, until it is pressed again.</summary>
        internal static bool Sticky
        {
            get => _sticky;
            set { _sticky = value; if (IsOpen && _root != null) Refresh(); }
        }

        private static float Scale => Screen.height / 1080f;

        internal static void Open(List<string> items, Vector2 centre, bool virtualPointer)
        {
            if (items == null || items.Count == 0) return;
            _slots = Mathf.Clamp(Plugin.WheelSlots.Value, 2, MaxSlots);
            Ensure();

            _items = items;
            _virtual = virtualPointer;
            _sticky = false;
            _pointer = Vector2.zero;
            _page = 0;
            _slot = -1;

            float scale = Scale, reach = (DiscRadius + 10f) * scale;
            centre.x = Mathf.Clamp(centre.x, reach, Mathf.Max(reach, Screen.width - reach));
            centre.y = Mathf.Clamp(centre.y, reach, Mathf.Max(reach, Screen.height - reach));
            _centrePos = centre;
            _wheel.anchoredPosition = centre;
            _wheel.localScale = new Vector3(scale, scale, 1f);

            // Room for a label: the gap between neighbouring slots at the labels' radius.
            float width = Mathf.Clamp(2f * LabelRadius * Mathf.Sin(Mathf.PI / _slots) - 8f, 60f, 160f);
            int size = _slots <= 8 ? 20 : _slots <= 12 ? 17 : 15;
            for (int i = 0; i < MaxSlots; i++)
            {
                float angle = i * Mathf.PI * 2f / _slots;
                _labels[i].rectTransform.anchoredPosition =
                    new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * LabelRadius;
                _labels[i].rectTransform.sizeDelta = new Vector2(width, 60f);
                _labels[i].fontSize = size;
            }

            if (_wedgeSlots != _slots)
            {
                _wedgeSlots = _slots;
                _wedgeImage.sprite = Draw(WedgeTexture, WedgePixel);
            }

            Refresh();
            _root.SetActive(true);
            IsOpen = true;
        }

        internal static void Tick()
        {
            if (!IsOpen) return;
            if (_root == null) { IsOpen = false; return; }

            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (scroll != 0f && Pages > 1)
            {
                _page = (_page + (scroll < 0f ? 1 : Pages - 1)) % Pages;
                _slot = -1;
                Refresh();
            }

            float scale = Scale;
            Vector2 offset;
            if (_virtual)
            {
                _pointer += new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 14f * scale;
                _pointer = Vector2.ClampMagnitude(_pointer, DiscRadius * scale);
                offset = _pointer;
            }
            else
            {
                offset = (Vector2)Input.mousePosition - _centrePos;
            }

            int slot = -1;
            if (offset.magnitude > DiscRadius * Hole * scale)
            {
                // Clockwise from straight up, as the slots are laid out.
                float angle = Mathf.Atan2(offset.x, offset.y);
                if (angle < 0f) angle += Mathf.PI * 2f;
                slot = Mathf.RoundToInt(angle / (Mathf.PI * 2f / _slots)) % _slots;
                if (_page * _slots + slot >= _items.Count) slot = -1;
            }
            if (slot == _slot) return;
            _slot = slot;
            Refresh();
        }

        /// <summary>Closes the wheel. The index of the chosen entry, or null when nothing was chosen.</summary>
        internal static int? Close()
        {
            if (!IsOpen) return null;
            IsOpen = false;
            _sticky = false;
            if (_root != null) _root.SetActive(false);
            int index = _page * _slots + _slot;
            return _slot >= 0 && _items != null && index < _items.Count ? index : (int?)null;
        }

        private static void Refresh()
        {
            for (int i = 0; i < MaxSlots; i++)
            {
                int index = _page * _slots + i;
                bool used = i < _slots && index < _items.Count;
                _labels[i].gameObject.SetActive(used);
                if (!used) continue;
                _labels[i].text = _items[index];
                _labels[i].color = i == _slot ? new Color(1f, 0.92f, 0.45f) : Color.white;
            }

            _wedge.gameObject.SetActive(_slot >= 0);
            if (_slot >= 0) _wedge.localEulerAngles = new Vector3(0f, 0f, -360f / _slots * _slot);

            string text = _slot >= 0 ? _items[_page * _slots + _slot]
                        : _sticky ? "Press again here to cancel" : "Release to cancel";
            if (_sticky && _slot >= 0) text += "\n<size=15>press again to choose</size>";
            if (Pages > 1) text += $"\n<size=15>page {_page + 1} of {Pages}  -  scroll</size>";
            _centre.text = text;
        }

        // ------------------------------------------------------------------ building

        private static void Ensure()
        {
            // Not kept across scenes, like the notice: rebuilt when the old one is gone.
            if (_root != null) return;

            _root = new GameObject("SVS_FreeRoam Wheel");
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32002;
            _font = Font.CreateDynamicFontFromOSFont("Arial", 22);

            // Anchored to the bottom left corner, so its position is in screen pixels; scaled
            // as a whole, so everything inside is laid out for 1080p.
            var wheel = new GameObject("Wheel");
            wheel.transform.SetParent(_root.transform, false);
            _wheel = wheel.AddComponent<RectTransform>();
            _wheel.anchorMin = _wheel.anchorMax = Vector2.zero;
            _wheel.pivot = new Vector2(0.5f, 0.5f);
            _wheel.sizeDelta = Vector2.zero;

            NewImage("Disc").sprite = Draw(DiscTexture, DiscPixel);
            _wedgeImage = NewImage("Highlight");
            _wedge = _wedgeImage.rectTransform;
            _wedgeSlots = 0;

            for (int i = 0; i < MaxSlots; i++) _labels[i] = NewText("Label", 20, new Vector2(150f, 60f));
            _centre = NewText("Centre", 19, new Vector2(DiscRadius * Hole * 2f - 14f, 80f));
            _centre.supportRichText = true;
        }

        private static Image NewImage(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_wheel, false);
            var image = go.AddComponent<Image>();
            image.raycastTarget = false;
            image.rectTransform.sizeDelta = new Vector2(DiscRadius * 2f, DiscRadius * 2f);
            return image;
        }

        private static Text NewText(string name, int size, Vector2 box)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_wheel, false);
            var text = go.AddComponent<Text>();
            text.font = _font;
            text.fontSize = size;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            text.rectTransform.sizeDelta = box;

            var outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
            outline.effectDistance = new Vector2(1.2f, -1.2f);
            return text;
        }

        /// <param name="pixels">Distance from the centre in pixels.</param>
        /// <param name="rim">The disc's radius in pixels.</param>
        /// <param name="angle">Degrees from straight up, negative to the left.</param>
        private delegate Color32 Pixel(float pixels, float rim, float angle);

        /// <summary>Draws a square texture pixel by pixel. Edges are softened over a pixel or
        /// so, and the texture has mip maps, so it stays smooth at any screen size.</summary>
        private static Sprite Draw(int size, Pixel pixel)
        {
            var pixels = new Color32[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - half, dy = y + 0.5f - half;
                    pixels[y * size + x] = pixel(Mathf.Sqrt(dx * dx + dy * dy), half - 1f,
                                                 Mathf.Atan2(dx, dy) * Mathf.Rad2Deg);
                }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Trilinear;
            texture.SetPixels32(pixels);
            texture.Apply(true);
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
        }

        private static Color32 DiscPixel(float pixels, float rim, float angle)
        {
            float inside = Mathf.Clamp01(rim - pixels);                // 1 inside the disc, fading over a pixel
            if (inside <= 0f) return new Color32(0, 0, 0, 0);

            // The light lines: the rim, and a thin ring around the centre.
            float line = Mathf.Max(Mathf.Clamp01(pixels - rim * (1f - RimWidth)),
                                   Mathf.Clamp01(2f - Mathf.Abs(pixels - rim * Hole)) * 0.75f);
            float dark = pixels < rim * Hole ? 0.8f : 0.62f;
            var colour = Color.Lerp(new Color(0.05f, 0.055f, 0.08f, dark), new Color(0.92f, 0.92f, 0.92f, 0.6f), line);
            colour.a *= inside;
            return colour;
        }

        private static Color32 WedgePixel(float pixels, float rim, float angle)
        {
            // How far inside the slot's edges, in pixels: the two sides, the rim, the centre ring.
            float side = (180f / _slots - Mathf.Abs(angle)) * Mathf.Deg2Rad * pixels;
            float inside = Mathf.Min(side, Mathf.Min(rim * (1f - RimWidth) - pixels, pixels - rim * Hole - 2f));
            if (inside <= 0f) return new Color32(0, 0, 0, 0);
            return new Color(1f, 0.84f, 0.35f, 0.32f * Mathf.Clamp01(inside / 1.5f));
        }
    }
}
