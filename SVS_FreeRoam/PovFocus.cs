using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using BeautifyFx = Beautify.Universal.Beautify;

namespace SVS_FreeRoam
{
    /// <summary>
    /// The game's depth of field (Beautify, switched on in its graphics settings) is set up for
    /// the overview camera, which sits far from everyone: each map focuses 8 to 10 m away. In
    /// third person the camera is close, so whoever is near it lands in the blurred
    /// foreground. While third person runs this keeps the focus on the player (in first
    /// person: on whoever is aimed at, else no blur), and puts the game's values back
    /// afterwards (FINDINGS.md §29).
    /// </summary>
    internal static class PovFocus
    {
        private struct Saved
        {
            internal BeautifyFx Effect;
            internal bool On;
            internal BeautifyFx.DoFFocusMode Mode;
            internal float Distance, Aperture;
            internal bool ModeOverride, DistanceOverride, ApertureOverride;
        }

        private static readonly List<Saved> _saved = new List<Saved>();
        private static float _nextScan;
        private static int _map = int.MinValue;
        private static bool _failed;
        private static bool _active;
        private const float FocusSpeed = 4f;           // how fast the focus glides; higher is quicker
        private static float _distance = 3f;           // where the focus is now
        private static float _blur;                    // how much of the blur is showing, 0 to 1

        internal static void Tick(bool pov, Camera cam, SV.Chara.AI playerAI)
        {
            if (_failed) return;
            try
            {
                if (!pov || cam == null || playerAI == null)
                {
                    Restore();
                    return;
                }
                _active = true;

                // A new map brings its own volume: look at once, not at the next half second.
                int map = playerAI.BehaviourCtrl != null ? playerAI.BehaviourCtrl.NowMapID : -1;
                if (map != _map || Time.unscaledTime >= _nextScan)
                {
                    _map = map;
                    _nextScan = Time.unscaledTime + 0.5f;
                    _saved.RemoveAll(s => s.Effect == null);
                    Scan();
                }

                // What to keep sharp. Third person: the player. First person (the camera is
                // inside the player): whoever is aimed at, and nothing blurred otherwise.
                var eye = cam.transform.position;
                var player = playerAI.transform.position;
                bool firstPerson = new Vector2(eye.x - player.x, eye.z - player.z).magnitude < 0.45f;
                float focus = -1f;
                if (!firstPerson) focus = Vector3.Distance(eye, player + Vector3.up * 1.3f);
                else if (ThirdPersonController.AimedCharacter != null)
                    focus = Vector3.Distance(eye, ThirdPersonController.AimedCharacter.transform.position + Vector3.up * 1.3f);

                // Eased, so a change of what is in focus (the player, someone aimed at, nothing)
                // glides instead of snapping: the distance moves, and the blur fades in and out.
                bool wanted = Plugin.PovBlurStrength.Value > 0f && focus >= 0f;
                float ease = 1f - Mathf.Exp(-FocusSpeed * Time.unscaledDeltaTime);
                if (wanted)
                    _distance = _blur < 0.01f ? Mathf.Max(0.5f, focus)      // nothing to glide from
                                              : Mathf.Lerp(_distance, Mathf.Max(0.5f, focus), ease);
                _blur = Mathf.Lerp(_blur, wanted ? 1f : 0f, ease);
                if (!wanted && _blur < 0.01f) _blur = 0f;

                bool blur = _blur > 0f;
                float distance = _distance;

                foreach (var saved in _saved)
                {
                    var effect = saved.Effect;
                    if (effect == null) continue;
                    effect.depthOfField.value = saved.On && blur;
                    if (!blur) continue;

                    effect.depthOfFieldFocusMode.overrideState = true;
                    effect.depthOfFieldFocusMode.value = BeautifyFx.DoFFocusMode.FixedDistance;
                    effect.depthOfFieldDistance.overrideState = true;
                    effect.depthOfFieldDistance.value = distance;

                    // Focusing nearer makes the same lens blur the background much more. The
                    // aperture is narrowed by as much, so the far background is blurred as the
                    // map's own setting blurs it; Strength then scales that.
                    float focal = effect.depthOfFieldFocalLength.value;
                    float match = Mathf.Clamp((distance - focal) / Mathf.Max(0.1f, saved.Distance - focal), 0.02f, 4f);
                    effect.depthOfFieldAperture.overrideState = true;
                    effect.depthOfFieldAperture.value = saved.Aperture * match * Plugin.PovBlurStrength.Value * _blur;
                }
            }
            catch (Exception e)
            {
                _failed = true;
                Plugin.Logger.LogWarning("Could not adjust the depth of field for third person: " + e.Message);
            }
        }

        /// <summary>Finds the Beautify settings of every volume in the scene, remembering the game's values.</summary>
        private static void Scan()
        {
            foreach (var volume in UnityEngine.Object.FindObjectsOfType<Volume>())
            {
                if (volume == null) continue;
                // The profile the volume really uses, without making a private copy of it.
                var profile = volume.HasInstantiatedProfile() ? volume.profile : volume.sharedProfile;
                var components = profile?.components;
                if (components == null) continue;

                for (int i = 0; i < components.Count; i++)
                {
                    var effect = components[i]?.TryCast<BeautifyFx>();
                    if (effect == null || _saved.Exists(s => s.Effect != null && s.Effect.Pointer == effect.Pointer))
                        continue;

                    _saved.Add(new Saved
                    {
                        Effect = effect,
                        On = effect.depthOfField.value,
                        Mode = effect.depthOfFieldFocusMode.value,
                        Distance = effect.depthOfFieldDistance.value,
                        Aperture = effect.depthOfFieldAperture.value,
                        ModeOverride = effect.depthOfFieldFocusMode.overrideState,
                        DistanceOverride = effect.depthOfFieldDistance.overrideState,
                        ApertureOverride = effect.depthOfFieldAperture.overrideState,
                    });
                    Notice.Log($"Depth of field: volume '{volume.name}' (global {volume.isGlobal}, priority " +
                               $"{volume.priority}): on {effect.depthOfField.value}, focus " +
                               $"{effect.depthOfFieldFocusMode.value}, distance {effect.depthOfFieldDistance.value}, " +
                               $"focal length {effect.depthOfFieldFocalLength.value}, aperture " +
                               $"{effect.depthOfFieldAperture.value}.");
                }
            }
        }

        private static void Restore()
        {
            if (!_active) return;
            _active = false;
            foreach (var saved in _saved)
            {
                var effect = saved.Effect;
                if (effect == null) continue;
                effect.depthOfField.value = saved.On;
                effect.depthOfFieldFocusMode.value = saved.Mode;
                effect.depthOfFieldFocusMode.overrideState = saved.ModeOverride;
                effect.depthOfFieldDistance.value = saved.Distance;
                effect.depthOfFieldDistance.overrideState = saved.DistanceOverride;
                effect.depthOfFieldAperture.value = saved.Aperture;
                effect.depthOfFieldAperture.overrideState = saved.ApertureOverride;
            }
            _saved.Clear();
            _nextScan = 0f;
            _map = int.MinValue;
        }
    }
}
