using BepInEx.Configuration;
using UnityEngine;

namespace SVS_FreeRoam
{
    /// <summary>
    /// Every binding can have a second key. Before the merge this needed a Harmony patch on
    /// Input.GetKey to widen the answer given to another assembly; now it is just an or.
    /// </summary>
    internal static class Keys
    {
        internal static bool Down(ConfigEntry<KeyCode> primary, ConfigEntry<KeyCode> secondary)
        {
            if (primary != null && primary.Value != KeyCode.None && Input.GetKeyDown(primary.Value))
                return true;
            return secondary != null && secondary.Value != KeyCode.None &&
                   Input.GetKeyDown(secondary.Value);
        }

        internal static bool Up(ConfigEntry<KeyCode> primary, ConfigEntry<KeyCode> secondary)
        {
            if (primary != null && primary.Value != KeyCode.None && Input.GetKeyUp(primary.Value))
                return true;
            return secondary != null && secondary.Value != KeyCode.None &&
                   Input.GetKeyUp(secondary.Value);
        }

        internal static bool Held(ConfigEntry<KeyCode> primary, ConfigEntry<KeyCode> secondary)
        {
            if (primary != null && primary.Value != KeyCode.None && Input.GetKey(primary.Value))
                return true;
            return secondary != null && secondary.Value != KeyCode.None &&
                   Input.GetKey(secondary.Value);
        }
    }
}
