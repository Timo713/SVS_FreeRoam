using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SVS_FreeRoam
{
    /// <summary>
    /// The radial menu: choices spread evenly round a dark disc, the one the pointer leans
    /// towards is the choice. More choices than the wheel's size make pages, turned with the
    /// mouse wheel. With the cursor free the pointer is the mouse; in third person, where the
    /// cursor is locked, it is the mouse's movement since the wheel opened (the camera holds
    /// still meanwhile, CameraRig). The disc and the highlight are drawn into textures here,
    /// once, so the plugin ships no image files. Who opens it decides when it closes
    /// (ClickIdler).
    /// </summary>
    internal static class IdleWheel
    {
        private const int MaxSlots = 16;
        private const float DiscRadius = 300f;         // all sizes at 1080p
        private const float LabelRadius = 195f;
        private const float Hole = 0.28f;              // the centre, as a part of the disc's radius
        private const int TextureSize = 512;
        private const float RimWidth = 0.016f;         // as a part of the disc's radius

        // Drawn once and kept for the whole session: drawing them again on every map was a hitch.
        private static Sprite _discSprite;
        private static Sprite _ringSprite;

        private static GameObject _root;
        private static RectTransform _wheel;
        private static Image _highlight;
        private static Text _centre;
        private static Font _font;
        private static readonly Text[] _labels = new Text[MaxSlots];

        private static List<string> _items;
        private static bool _pad;
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

        /// <summary>How many choices the page being shown has.</summary>
        private static int OnPage => _items == null ? 0 : Mathf.Clamp(_items.Count - _page * _slots, 0, _slots);

        /// <summary>The index of the choice the pointer is on, if any.</summary>
        internal static int? Highlighted =>
            IsOpen && _slot >= 0 && _page * _slots + _slot < _items.Count ? _page * _slots + _slot : (int?)null;

        /// <summary>The wheel stays up after the button is let go, until a choice is clicked.</summary>
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
            _pad = false;
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

            Refresh();
            _root.SetActive(true);
            IsOpen = true;
        }

        /// <summary>Changes the text of one choice while the wheel is open.</summary>
        internal static void SetLabel(int index, string text)
        {
            if (_items == null || index < 0 || index >= _items.Count) return;
            _items[index] = text;
            if (IsOpen && _root != null) Refresh();
        }

        /// <summary>Steered with a controller: the texts in the middle say so.</summary>
        internal static bool PadMode
        {
            get => _pad;
            set { _pad = value; if (IsOpen && _root != null) Refresh(); }
        }


        /// <summary>Steered with the mouse: its position, or its movement in third person.</summary>
        internal static void Tick()
        {
            if (!IsOpen) return;
            if (_root == null) { IsOpen = false; return; }

            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (scroll != 0f) TurnPage(scroll < 0f ? 1 : -1);

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
            PointAt(offset);
        }

        /// <summary>Steered with a controller: a stick's tilt points at a choice.</summary>
        /// <param name="pageStep">1 for the next page, -1 for the previous, 0 to stay.</param>
        internal static void TickPad(Vector2 stick, int pageStep)
        {
            if (!IsOpen) return;
            if (_root == null) { IsOpen = false; return; }

            if (pageStep != 0) TurnPage(pageStep);
            PointAt(stick.magnitude > 0.5f ? stick.normalized * DiscRadius * Scale * 0.7f : Vector2.zero);
        }

        private static void TurnPage(int step)
        {
            if (Pages <= 1) return;
            _page = (_page + (step > 0 ? 1 : Pages - 1)) % Pages;
            _slot = -1;
            Refresh();
        }

        /// <param name="offset">From the wheel's centre, in screen pixels.</param>
        private static void PointAt(Vector2 offset)
        {
            int slot = -1, count = OnPage;
            if (count > 0 && offset.magnitude > DiscRadius * Hole * Scale)
            {
                // Clockwise from straight up, as the choices are laid out.
                float angle = Mathf.Atan2(offset.x, offset.y);
                if (angle < 0f) angle += Mathf.PI * 2f;
                slot = Mathf.RoundToInt(angle / (Mathf.PI * 2f / count)) % count;
            }
            if (slot == _slot) return;
            _slot = slot;
            Refresh();
        }

        /// <summary>Closes the wheel. The index of the chosen entry, or null when nothing was chosen.</summary>
        internal static int? Close()
        {
            if (!IsOpen) return null;
            var chosen = Highlighted;
            IsOpen = false;
            _sticky = false;
            if (_root != null) _root.SetActive(false);
            return chosen;
        }

        private static void Refresh()
        {
            // The page's choices share the whole circle, however few they are.
            int count = OnPage;
            float width = count <= 2 ? 160f
                        : Mathf.Clamp(2f * LabelRadius * Mathf.Sin(Mathf.PI / count) - 8f, 60f, 160f);
            int size = count <= 8 ? 20 : count <= 12 ? 17 : 15;

            for (int i = 0; i < MaxSlots; i++)
            {
                bool used = i < count;
                _labels[i].gameObject.SetActive(used);
                if (!used) continue;

                float angle = i * Mathf.PI * 2f / count;
                var rt = _labels[i].rectTransform;
                rt.anchoredPosition = new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * LabelRadius;
                rt.sizeDelta = new Vector2(width, 60f);
                _labels[i].fontSize = size;
                _labels[i].text = _items[_page * _slots + i];
                // (A label that carries its own colour, as favorites do, keeps it either way.)
                _labels[i].color = i == _slot ? new Color(1f, 0.92f, 0.45f) : Color.white;
            }

            bool lit = _slot >= 0 && count > 0;
            _highlight.gameObject.SetActive(lit);
            if (lit)
            {
                // A slice of the ring, filled clockwise from the top: turned so that the
                // slice is centred on the choice.
                float slice = 360f / count;
                _highlight.fillAmount = 1f / count;
                _highlight.rectTransform.localEulerAngles = new Vector3(0f, 0f, slice * 0.5f - slice * _slot);
            }

            string text = lit ? _items[_page * _slots + _slot]
                        : _pad ? "Tilt a stick" : _sticky ? "Click here to cancel" : "Release to cancel";
            if (_pad && lit) text += "\n<size=15>A, or let go, plays it</size>";
            else if (_sticky && lit) text += "\n<size=15>click to choose</size>";
            if (Pages > 1)
                text += $"\n<size=15>page {_page + 1} of {Pages}  -  {(_pad ? "RB or D-pad" : "scroll")}</size>";
            _centre.text = text;
        }

        // ------------------------------------------------------------------ building

        private static void Ensure()
        {
            // Not kept across scenes, like the notice: rebuilt when the old one is gone. The
            // images it shows are kept.
            if (_root != null) return;

            if (_discSprite == null) _discSprite = Draw(DiscPixel);
            if (_ringSprite == null) _ringSprite = Draw(RingPixel);

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

            NewImage("Disc").sprite = _discSprite;
            _highlight = NewImage("Highlight");
            _highlight.sprite = _ringSprite;
            _highlight.type = Image.Type.Filled;
            _highlight.fillMethod = Image.FillMethod.Radial360;
            _highlight.fillOrigin = (int)Image.Origin360.Top;
            _highlight.fillClockwise = true;

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
        private delegate Color32 Pixel(float pixels, float rim);

        /// <summary>Draws a square texture pixel by pixel. Edges are softened over a pixel or
        /// so, and the texture has mip maps, so it stays smooth at any screen size.</summary>
        private static Sprite Draw(Pixel pixel)
        {
            const int size = TextureSize;
            var pixels = new Color32[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - half, dy = y + 0.5f - half;
                    pixels[y * size + x] = pixel(Mathf.Sqrt(dx * dx + dy * dy), half - 1f);
                }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Trilinear;
            texture.SetPixels32(pixels);
            texture.Apply(true);
            // Or the game's clean-up between maps would throw it away.
            texture.hideFlags = HideFlags.HideAndDontSave;

            var sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        private static Color32 DiscPixel(float pixels, float rim)
        {
            float inside = Mathf.Clamp01(rim - pixels);                // 1 inside the disc, fading over a pixel
            if (inside <= 0f) return new Color32(0, 0, 0, 0);

            // The light lines: the rim, and a thin ring around the centre.
            float line = Mathf.Max(Mathf.Clamp01(pixels - rim * (1f - RimWidth)),
                                   Mathf.Clamp01(1.5f - Mathf.Abs(pixels - rim * Hole)) * 0.75f);
            float dark = pixels < rim * Hole ? 0.8f : 0.62f;
            var colour = Color.Lerp(new Color(0.05f, 0.055f, 0.08f, dark), new Color(0.92f, 0.92f, 0.92f, 0.6f), line);
            colour.a *= inside;
            return colour;
        }

        /// <summary>The band between the centre and the rim: the highlight shows a slice of it.</summary>
        private static Color32 RingPixel(float pixels, float rim)
        {
            float inside = Mathf.Min(rim * (1f - RimWidth) - pixels, pixels - rim * Hole - 1.5f);
            if (inside <= 0f) return new Color32(0, 0, 0, 0);
            return new Color(1f, 0.84f, 0.35f, 0.32f * Mathf.Clamp01(inside));
        }
    }
}
