using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SVS_FreeRoam
{
    /// <summary>
    /// Draws related settings side by side on one ConfigurationManager row.
    ///
    /// A key row is two buttons, one per binding. Clicking one opens a searchable list of
    /// every key, the way ConfigurationManager's own editor does; picking from it assigns.
    ///
    /// It deliberately does **not** capture the next key pressed. That was tried and is worse
    /// than it sounds: any key touched while a capture is armed gets silently bound, so simply
    /// testing whether movement still worked rebound the PoV toggle to a movement key. A list
    /// cannot misfire.
    ///
    /// Three things that break ConfigurationManager's window, all learned the hard way:
    ///
    /// 1. **No FlexibleSpace.** Inside a row it expands past the window edge, pushing controls
    ///    off screen and stretching other settings.
    /// 2. **BeginVertical makes the setting name align to the top** instead of beside the
    ///    controls, so the row is only vertical while the list is actually open.
    /// 3. **No GUILayout.Toolbar.** It takes a managed string[], which the IL2CPP interop layer
    ///    rejects, so the whole row throws and renders blank.
    /// </summary>
    internal static class PairDrawer
    {
        private const float KeyWidth = 90f;
        private const float ResetWidth = 56f;

        /// <summary>
        /// True only while this drawer is writing a value. ConfigurationManager's own Reset
        /// button is the only other writer, so the plugin uses this to tell the two apart and
        /// clear the second binding alongside the first. See Plugin.LinkResetToPair.
        /// </summary>
        internal static bool AssigningFromList { get; private set; }

        private static ConfigEntry<KeyCode> _open;
        private static Vector2 _scroll;
        private static string _filter = "";
        private static KeyCode[] _keys;

        /// <summary>Two key bindings on one row, each opening a key list when clicked.</summary>
        internal static Action<ConfigEntryBase> Keys(ConfigEntry<KeyCode> first,
                                                     ConfigEntry<KeyCode> second)
        {
            return _ => Guarded(() =>
            {
                bool listOpen = _open == first || _open == second;

                if (listOpen) GUILayout.BeginVertical();

                GUILayout.BeginHorizontal();
                KeySlot(first);
                KeySlot(second);

                // Ours is the only Reset on this row: ConfigurationManager's own is hidden
                // with HideDefaultButton, because it can only ever reach the first of the pair.
                if (GUILayout.Button("Reset", GUILayout.Width(ResetWidth)))
                {
                    Assign(first, DefaultOf(first));
                    Assign(second, DefaultOf(second));
                    _open = null;
                }

                GUILayout.EndHorizontal();

                if (listOpen)
                {
                    DrawKeyList(_open);
                    GUILayout.EndVertical();
                }
            });
        }

        /// <summary>A checkbox and a three-way look choice on one row.</summary>
        internal static Action<ConfigEntryBase> WalkAndLook(ConfigEntry<bool> walk,
                                                            ConfigEntry<ButtonLookMode> look)
        {
            return _ => Guarded(() =>
            {
                GUILayout.BeginHorizontal();

                walk.Value = GUILayout.Toggle(walk.Value, " Walk", GUILayout.Width(70f));

                LookOption(look, ButtonLookMode.Off, "No look", 70f);
                LookOption(look, ButtonLookMode.FreeLook, "Free", 60f);
                LookOption(look, ButtonLookMode.ScreenEdge, "Edge", 60f);

                GUILayout.EndHorizontal();
            });
        }

        // ------------------------------------------------------------- internals

        private static void LookOption(ConfigEntry<ButtonLookMode> entry,
                                       ButtonLookMode value, string label, float width)
        {
            bool active = entry.Value == value;
            if (GUILayout.Button(active ? "* " + label : label, GUILayout.Width(width)))
                entry.Value = value;
        }

        private static void KeySlot(ConfigEntry<KeyCode> entry)
        {
            bool isOpen = _open == entry;
            string label = isOpen ? "[ " + entry.Value + " ]" : entry.Value.ToString();

            if (!GUILayout.Button(label, GUILayout.Width(KeyWidth))) return;

            _open = isOpen ? null : entry;
            _filter = "";
            _scroll = Vector2.zero;
        }

        private static void DrawKeyList(ConfigEntry<KeyCode> entry)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Find", GUILayout.Width(34f));
            _filter = GUILayout.TextField(_filter ?? "", GUILayout.Width(110f));

            if (GUILayout.Button("None", GUILayout.Width(56f)))
            {
                Assign(entry, KeyCode.None);
                _open = null;
            }

            if (GUILayout.Button("Close", GUILayout.Width(56f)))
                _open = null;

            GUILayout.EndHorizontal();

            if (_open == null) return;

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(140f));

            foreach (var key in AllKeys())
            {
                var name = key.ToString();
                if (_filter.Length > 0 &&
                    name.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                if (!GUILayout.Button(name)) continue;

                Assign(entry, key);
                _open = null;
                break;
            }

            GUILayout.EndScrollView();
        }

        /// <summary>
        /// Every KeyCode once. The enum has several names sharing a value (RightCommand and
        /// RightApple, for instance), which would otherwise appear as duplicate rows.
        /// </summary>
        private static KeyCode[] AllKeys()
        {
            if (_keys != null) return _keys;

            var seen = new HashSet<int>();
            var list = new List<KeyCode>();

            foreach (KeyCode key in (KeyCode[])Enum.GetValues(typeof(KeyCode)))
                if (seen.Add((int)key))
                    list.Add(key);

            _keys = list.ToArray();
            return _keys;
        }

        private static KeyCode DefaultOf(ConfigEntry<KeyCode> entry)
        {
            return entry.DefaultValue is KeyCode key ? key : KeyCode.None;
        }

        /// <summary>Column headings, drawn once at the top of the Hotkeys section.</summary>
        internal static Action<ConfigEntryBase> ColumnHeadings()
        {
            return _ => Guarded(() =>
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Hotkey 1", GUILayout.Width(KeyWidth));
                GUILayout.Label("Hotkey 2", GUILayout.Width(KeyWidth));
                GUILayout.EndHorizontal();
            });
        }

        private static void Assign(ConfigEntry<KeyCode> entry, KeyCode key)
        {
            AssigningFromList = true;
            try { entry.Value = key; }
            finally { AssigningFromList = false; }
        }

        private static float _resetArmedUntil;

        /// <summary>A button that puts every setting back to its default, on the second click.</summary>
        internal static Action<ConfigEntryBase> ResetAll(ConfigFile config)
        {
            return _ => Guarded(() =>
            {
                bool armed = Time.unscaledTime < _resetArmedUntil;
                bool clicked = GUILayout.Button(armed ? "Click again to confirm" : "Reset All Settings",
                                                GUILayout.ExpandWidth(true));
                // As wide as the other rows' fields: they end where their Reset button begins.
                GUILayout.Space(ResetWidth + 4f);
                if (!clicked) return;
                if (!armed)
                {
                    _resetArmedUntil = Time.unscaledTime + 4f;
                    return;
                }
                _resetArmedUntil = 0f;

                int count = 0;
                foreach (var definition in new List<ConfigDefinition>(config.Keys))
                {
                    // Not the note of which button layout the gamepad settings were moved to.
                    if (definition.Key == "Gamepad Layout" || definition.Key == "Reset All Settings") continue;
                    var entry = config[definition];
                    if (Equals(entry.BoxedValue, entry.DefaultValue)) continue;
                    entry.BoxedValue = entry.DefaultValue;
                    count++;
                }
                Plugin.Logger.LogInfo($"Reset All Settings: {count} settings put back to their defaults.");
            });
        }

        private static void Guarded(Action body)
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("Config row drawer failed: " + e.Message);
            }
        }
    }
}
