using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SVS_FreeRoam
{
    /// <summary>
    /// Reads an Xbox-style controller straight from Windows (XInput).
    ///
    /// Needed only for the right stick and the triggers. The game uses Unity's legacy input, whose Input
    /// Manager defines axes for the left stick (Horizontal/Vertical, joystick axes 0 and 1)
    /// and the D-pad, and nothing for the right stick or triggers -- so Unity cannot report
    /// them at all.
    /// The left stick and the face buttons are still read through Unity, where they already
    /// work (FINDINGS.md §15).
    ///
    /// XInput covers Xbox controllers and anything presented as one, which includes most pads
    /// under Steam Input.
    /// </summary>
    internal static class Gamepad
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct XInputGamepad
        {
            public ushort Buttons;
            public byte LeftTrigger;
            public byte RightTrigger;
            public short ThumbLX;
            public short ThumbLY;
            public short ThumbRX;
            public short ThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputState
        {
            public uint PacketNumber;
            public XInputGamepad Gamepad;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint GetState14(uint userIndex, out XInputState state);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        private static extern uint GetState910(uint userIndex, out XInputState state);

        private const uint Success = 0;

        /// <summary>Microsoft's recommended right-stick dead zone, as a fraction of full tilt.</summary>
        private const float RightDeadZone = 8689f / 32767f;

        private static int _library;            // 0 untried, 1 = 1_4, 2 = 9_1_0, -1 none
        private static int _connected = -1;     // controller slot in use, or -1
        private static float _nextScan;
        private static int _readFrame = -1;
        private static Vector2 _rightStick;
        private static float _leftTrigger, _rightTrigger;
        private static ushort _buttons, _previousButtons;

        // XInput button bits.
        internal const ushort DPadUp = 0x0001, DPadDown = 0x0002, DPadLeft = 0x0004,
                              DPadRight = 0x0008, Start = 0x0010, Back = 0x0020,
                              LeftShoulder = 0x0100, RightShoulder = 0x0200,
                              A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

        /// <summary>True on the frame a button goes down.</summary>
        internal static bool Pressed(ushort button)
        {
            Poll();
            return (_buttons & button) != 0 && (_previousButtons & button) == 0;
        }

        internal static bool Held(ushort button)
        {
            Poll();
            return (_buttons & button) != 0;
        }

        /// <summary>Microsoft's recommended trigger threshold, out of 255.</summary>
        private const float TriggerThreshold = 30f;

        /// <summary>
        /// Right stick with the dead zone removed and rescaled, so it starts at 0 just past
        /// the dead zone and reaches 1 at full tilt. Zero with no controller.
        /// </summary>
        internal static Vector2 RightStick
        {
            get
            {
                Poll();
                return _rightStick;
            }
        }

        /// <summary>Triggers, 0 released to 1 fully pulled, past a small threshold.</summary>
        internal static float LeftTrigger
        {
            get
            {
                Poll();
                return _leftTrigger;
            }
        }

        internal static float RightTrigger
        {
            get
            {
                Poll();
                return _rightTrigger;
            }
        }

        /// <summary>Once per frame at most, however many callers ask.</summary>
        private static void Poll()
        {
            if (_readFrame == Time.frameCount) return;
            _readFrame = Time.frameCount;
            _rightStick = Vector2.zero;
            _leftTrigger = _rightTrigger = 0f;
            _previousButtons = _buttons;
            _buttons = 0;

            // Gamepad Support off: nothing is asked of Windows at all.
            if (!Plugin.GamepadSupport.Value) return;

            if (_library < 0) return;

            try
            {
                XInputState state = default;
                bool have = _connected >= 0 && TryRead((uint)_connected, out state);

                // Asking an empty slot is slow (it waits on the driver), so while nothing is
                // connected the four slots are scanned only every couple of seconds.
                if (!have)
                {
                    _connected = -1;
                    if (Time.unscaledTime < _nextScan) return;
                    _nextScan = Time.unscaledTime + 2f;

                    for (uint i = 0; i < 4 && !have; i++)
                    {
                        have = TryRead(i, out state);
                        if (have) _connected = (int)i;
                    }
                    if (!have) return;
                }

                _rightStick = Shape(state.Gamepad.ThumbRX, state.Gamepad.ThumbRY);
                _leftTrigger = Trigger(state.Gamepad.LeftTrigger);
                _rightTrigger = Trigger(state.Gamepad.RightTrigger);
                _buttons = state.Gamepad.Buttons;
            }
            catch (Exception e)
            {
                // No XInput on this system at all: stop trying rather than throw every frame.
                _library = -1;
                Plugin.Logger.LogWarning("Gamepad right stick unavailable (XInput): " + e.Message);
            }
        }

        private static bool TryRead(uint slot, out XInputState state)
        {
            if (_library == 0)
            {
                try
                {
                    GetState14(0, out state);
                    _library = 1;
                }
                catch (DllNotFoundException)
                {
                    _library = 2;
                }
            }

            uint result = _library == 1 ? GetState14(slot, out state) : GetState910(slot, out state);
            return result == Success;
        }

        private static float Trigger(byte raw) =>
            raw <= TriggerThreshold ? 0f : (raw - TriggerThreshold) / (255f - TriggerThreshold);

        /// <summary>Radial dead zone, rescaled so movement starts smoothly from zero.</summary>
        private static Vector2 Shape(short rawX, short rawY)
        {
            var v = new Vector2(rawX / 32767f, rawY / 32767f);
            float magnitude = v.magnitude;
            if (magnitude <= RightDeadZone) return Vector2.zero;

            float scaled = Mathf.Clamp01((magnitude - RightDeadZone) / (1f - RightDeadZone));
            return v / magnitude * scaled;
        }
    }
}
