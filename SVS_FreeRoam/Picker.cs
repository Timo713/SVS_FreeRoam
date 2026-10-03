using Manager;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SVS_FreeRoam
{
    /// <summary>What a click landed on: another character, or a spot in the world.</summary>
    internal struct Pick
    {
        internal SV.Chara.AI Character;
        internal Vector3 Aimed;
        internal string What;
    }

    internal static class Picker
    {
        private const float MaxRayDistance = 1000f;

        /// <summary>
        /// Whether a mouse click may be ours at all. Null when it may; otherwise the reason not.
        /// </summary>
        internal static string WhyNotClickable()
        {
            // A locked cursor means third person is driving, and its mouse buttons walk and
            // interact. Clicks are ours in the overview camera and while the location buttons
            // are up, where the cursor is free.
            if (Cursor.lockState == CursorLockMode.Locked) return "cursor is locked";

            var eventSystem = EventSystem.current;
            if (eventSystem != null && eventSystem.IsPointerOverGameObject()) return "over UI";

            if ((Scene.IsOverlap || ThirdPersonController.IsAnyMenuOpen())) return "a menu, conversation or H scene is open";
            return null;
        }

        /// <summary>
        /// What is under the mouse. False only when the ray finds nothing and never reaches
        /// the floor either (pointing at the sky).
        /// </summary>
        internal static bool UnderMouse(Camera cam, SV.Chara.AI playerAI, out Pick pick)
        {
            pick = default;
            var ray = cam.ScreenPointToRay(Input.mousePosition);

            // Nearest solid hit that is not the player. Triggers (sensors, zones) are ignored.
            var hits = Physics.RaycastAll(ray, MaxRayDistance, ~0, QueryTriggerInteraction.Ignore);
            bool found = false;
            RaycastHit best = default;
            foreach (var hit in hits)
            {
                var ai = hit.collider.GetComponentInParent<SV.Chara.AI>();
                if (ai != null && ai.Pointer == playerAI.Pointer) continue;
                if (!found || hit.distance < best.distance)
                {
                    best = hit;
                    found = true;
                }
            }

            if (found)
            {
                pick.Character = best.collider.GetComponentInParent<SV.Chara.AI>();
                pick.Aimed = best.point;
                pick.What = pick.Character != null
                    ? $"character {pick.Character.name} ({best.collider.name})"
                    : $"'{best.collider.name}' layer {best.collider.gameObject.layer} " +
                      $"({LayerMask.LayerToName(best.collider.gameObject.layer)})";
                return true;
            }

            // Nothing solid under the cursor, as on 2D maps where the scenery is only a
            // picture: use the flat floor the player stands on.
            var floor = new Plane(Vector3.up, playerAI.transform.position);
            if (!floor.Raycast(ray, out float enter)) return false;

            pick.Aimed = ray.GetPoint(enter);
            pick.What = "nothing (used the player's floor)";
            return true;
        }
    }
}
