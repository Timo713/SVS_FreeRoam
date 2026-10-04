using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using BeautifyFx = Beautify.Universal.Beautify;

namespace SVS_FreeRoam
{
    /// <summary>
    /// The game's depth of field (Beautify, switched on in its graphics settings) is set up for
    /// the overview camera, which sits far from everyone. In third person the camera is close,
    /// so whoever is near it lands in the blurred foreground. While third person runs this
    /// keeps the focus at the player's distance, or switches the effect off, and puts the
    /// game's values back afterwards (FINDINGS.md §29).
    /// </summary>
    internal static class PovFocus
    {
        private struct Saved
        {
            internal BeautifyFx Effect;
            internal bool On;
            internal BeautifyFx.DoFFocusMode Mode;
            internal float Distance;
            internal bool ModeOverride, DistanceOverride;
        }

        private static readonly List<Saved> _saved = new List<Saved>();
        private static float _nextScan;
        private static bool _failed;
        private static PovDepthOfField _applied = PovDepthOfField.Unchanged;

        internal static void Tick(bool pov, Camera cam, SV.Chara.AI playerAI)
        {
            if (_failed) return;
            try
            {
                var mode = Plugin.PovDepthOfFieldMode.Value;
                if (!pov || cam == null || playerAI == null || mode == PovDepthOfField.Unchanged)
                {
                    Restore();
                    return;
                }
                if (mode != _applied) Restore();
                _applied = mode;

                if (Time.unscaledTime >= _nextScan)
                {
                    _nextScan = Time.unscaledTime + 2f;
                    Scan();
                }

                // To about the player's chest; never closer than arm's length, so first person
                // (the camera inside the head) focuses on what is in front instead.
                float distance = Mathf.Max(1.2f,
                    Vector3.Distance(cam.transform.position, playerAI.transform.position + Vector3.up * 1.2f));

                foreach (var saved in _saved)
                {
                    var effect = saved.Effect;
                    if (effect == null) continue;
                    if (mode == PovDepthOfField.Off)
                    {
                        effect.depthOfField.value = false;
                        continue;
                    }
                    effect.depthOfFieldFocusMode.overrideState = true;
                    effect.depthOfFieldFocusMode.value = BeautifyFx.DoFFocusMode.FixedDistance;
                    effect.depthOfFieldDistance.overrideState = true;
                    effect.depthOfFieldDistance.value = distance;
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
                        ModeOverride = effect.depthOfFieldFocusMode.overrideState,
                        DistanceOverride = effect.depthOfFieldDistance.overrideState,
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
            if (_saved.Count == 0) { _applied = PovDepthOfField.Unchanged; return; }
            foreach (var saved in _saved)
            {
                var effect = saved.Effect;
                if (effect == null) continue;
                if (_applied == PovDepthOfField.Off) effect.depthOfField.value = saved.On;
                effect.depthOfFieldFocusMode.value = saved.Mode;
                effect.depthOfFieldFocusMode.overrideState = saved.ModeOverride;
                effect.depthOfFieldDistance.value = saved.Distance;
                effect.depthOfFieldDistance.overrideState = saved.DistanceOverride;
            }
            _saved.Clear();
            _nextScan = 0f;
            _applied = PovDepthOfField.Unchanged;
        }
    }
}
