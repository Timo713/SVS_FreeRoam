namespace SVS_FreeRoam
{
    /// <summary>
    /// Which view we are in, and what the toggle key means.
    ///
    ///   Overview Camera Restore ON  -- the toggle switches between third-person and the
    ///                                  location's own overview camera.
    ///
    ///   Overview Camera Restore OFF -- third-person is the only view. The toggle shows and
    ///                                  hides the location buttons instead, and neither it nor
    ///                                  travelling somewhere new can reach the overview camera.
    ///
    /// Before the merge this had to watch SVS_3rdPov's povActive flag and correct it after the
    /// fact, which was always one frame too late because that plugin branched on the flag in
    /// the same pass it flipped it. Owning the flag outright removes the whole problem.
    /// </summary>
    internal static class ViewMode
    {
        /// <summary>True while third-person is running.</summary>
        /// <summary>True while third-person is running. Always false with Third-Person Mode
        /// switched off, which is all it takes to hand everything back (the Disable path).</summary>
        internal static bool Active
        {
            get => _active && Plugin.ThirdPersonMode.Value;
            private set => _active = value;
        }

        private static bool _active;

        private static bool _buttonsLatched;
        private static bool _started;

        /// <summary>Third-person is the only view, so the toggle is free to mean something else.</summary>
        internal static bool ThirdPersonOnly =>
            Plugin.OverviewRestore.Value == OverviewRestoreMode.Off;

        internal static void SetActive(bool active)
        {
            Active = active || ThirdPersonOnly;
            if (!Active) _buttonsLatched = false;
        }

        /// <summary>Applied on the first frame, once the scene actually exists.</summary>
        internal static void EnsureStarted()
        {
            if (_started) return;
            _started = true;
            SetActive(Plugin.StartPovActive.Value);
        }

        /// <summary>
        /// The plugin was switched off. Coming back on starts over from Start With PoV
        /// Active, like a fresh launch, rather than dropping straight back into whatever
        /// view was left behind.
        /// </summary>
        internal static void Restart()
        {
            _started = false;
            Active = false;
            _buttonsLatched = false;
        }

        /// <summary>Reads the toggle key and applies whatever it means in this mode.</summary>
        internal static void HandleToggle()
        {
            if (!Plugin.ThirdPersonMode.Value) return;
            if (!Keys.Down(Plugin.ToggleKey, Plugin.ToggleKey2) &&
                !(Plugin.GamepadSupport.Value && !GamepadUI.OnCycledScreen &&
                  Keys.Down(Plugin.GamepadToggleKey, Plugin.GamepadToggleKey2))) return;

            if (ThirdPersonOnly)
            {
                Active = true;
                _buttonsLatched = !_buttonsLatched;
                return;
            }

            Active = !Active;
            _buttonsLatched = false;
        }

        /// <summary>True when the location buttons should be placed on their markers.</summary>
        internal static bool ButtonsWanted
        {
            get
            {
                if (!Active) return false;
                if (ThirdPersonOnly && _buttonsLatched) return true;

                return Keys.Held(Plugin.MouseKey, Plugin.MouseKey2);
            }
        }

        /// <summary>
        /// Whether the character may still walk while the buttons are up.
        ///
        /// This applies in both camera modes now. With the overview camera on, button mode is
        /// only ever reached by holding the Mouse Mode key -- the toggle goes to the overview
        /// camera instead -- so there is no way for it to interfere with that arrangement.
        /// </summary>
        internal static bool MovementAllowedInButtonMode => Plugin.MoveInButtonMode.Value;

        /// <summary>How the mouse may turn the camera while the buttons are up.</summary>
        internal static ButtonLookMode LookInButtonMode => Plugin.LookInButtonMode.Value;

        /// <summary>
        /// Arriving somewhere new drops back to the plain third-person view. Leaving the latch
        /// on meant the new location's buttons flashed up for a frame before the tracker had
        /// found them, which read as a blink.
        /// </summary>
        internal static void OnMapChanged() => _buttonsLatched = false;
    }
}
