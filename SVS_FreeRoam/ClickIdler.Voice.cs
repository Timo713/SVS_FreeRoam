using System;
using System.Collections.Generic;
using ILLGames.Unity.Component;
using SV;
using UnityEngine;

namespace SVS_FreeRoam
{
    /// <summary>
    /// The sounds map characters make during some animations (the grunts of the dumbbell and
    /// exercise ones, a hum while reading...). The game has a table of them by animation
    /// (LowpolyActionVoiceManager.infoTable) and plays them for characters acting on their
    /// own. Asked to play one for the player it does start the clip, but at volume 0, and it
    /// keeps it there. So the game is asked for the line, which makes it load and pick the
    /// clip, and the same clip is then played on an audio source of our own, through the
    /// same mixer group, so the game's voice volume setting still applies (FINDINGS.md §28).
    /// </summary>
    internal static partial class ClickIdler
    {
        private static bool _voiceFailed;
        private static GameObject _voiceObject;
        private static AudioSource _voice;              // ours: the audible copy
        private static AudioSource _gameVoice;          // the game's silent one
        private static readonly HashSet<IntPtr> _voicesBefore = new HashSet<IntPtr>();
        private static float _voiceSeekUntil;
        private static float _voiceStarted;
        private static int _voiceId = -1;               // the animation being voiced
        private static int _voiceKey;
        private static bool _voiceLoops;
        private static float _voiceCycle;               // which loop of the animation was last voiced

        /// <summary>A playing voice line: the game routes them to its "PCM" mixer group.</summary>
        private static bool IsVoice(AudioSource source) =>
            source != null && source.isPlaying && source.outputAudioMixerGroup != null &&
            source.outputAudioMixerGroup.name == "PCM";

        /// <summary>An animation of ours has started: its sound, if the game has one for it.</summary>
        private static void StartVoice(SV.Chara.AI playerAI, int id)
        {
            StopVoice();
            if (_voiceFailed) return;
            try
            {
                var voices = SingletonInitializerAsync<LowpolyActionVoiceManager>.Instance;
                var animations = SingletonInitializerAsync<AnimationCtrlManager>.Instance;
                var table = voices?.infoTable;
                if (table == null || animations == null) return;

                // The table is numbered; each entry names its animation by the state's hash.
                int hash = animations.GetHash(id);
                foreach (var pair in table)
                {
                    if (pair.Value.hash != hash) continue;
                    _voiceId = id;
                    _voiceKey = pair.Key;
                    _voiceLoops = pair.Value.IsLoop;
                    _voiceStarted = Time.unscaledTime;
                    _voiceCycle = -1f;
                    AskForLine(playerAI);
                    return;
                }
            }
            catch (Exception e)
            {
                VoiceFailed(e);
            }
        }

        /// <summary>
        /// Has the game start the line (silently, as it does for the player), noting which
        /// voices were already playing so the new one can be told apart.
        /// </summary>
        private static void AskForLine(SV.Chara.AI playerAI)
        {
            _voicesBefore.Clear();
            foreach (var source in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                if (IsVoice(source)) _voicesBefore.Add(source.Pointer);

            var voices = SingletonInitializerAsync<LowpolyActionVoiceManager>.Instance;
            bool started = voices != null && voices.LowpolyVoicePlay(_voiceKey, playerAI);
            _voiceSeekUntil = started ? Time.unscaledTime + 1f : 0f;
            if (!started) Notice.Log($"Idle voice: the game has a sound for {Name(_voiceId)} but did not start it.");
        }

        /// <summary>Every frame: picks up the line the game started, repeats it, stops it.</summary>
        private static void Voice(SV.Chara.AI playerAI)
        {
            if (_voiceId < 0 || _voiceFailed) return;
            try
            {
                // The animation is over (stopped, replaced, walked away from). It takes a
                // moment to start, so it is not judged by that for the first second.
                if (_playing != _voiceId ||
                    (Time.unscaledTime - _voiceStarted > 1f && !PlayingOurs(playerAI)))
                {
                    StopVoice();
                    return;
                }

                if (Time.unscaledTime < _voiceSeekUntil)
                {
                    foreach (var source in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                    {
                        if (!IsVoice(source) || _voicesBefore.Contains(source.Pointer) || source.clip == null)
                            continue;
                        Adopt(source, playerAI);
                        break;
                    }
                    return;
                }

                // Once more on each new round of the animation, when the last one has finished.
                if (_voiceLoops || _voice == null || _voice.isPlaying || playerAI.animator == null) return;
                float cycle = Mathf.Floor(playerAI.animator.GetCurrentAnimatorStateInfo(0).normalizedTime);
                if (cycle <= _voiceCycle) return;
                _voiceCycle = cycle;
                AskForLine(playerAI);
            }
            catch (Exception e)
            {
                VoiceFailed(e);
            }
        }

        /// <summary>Plays the game's silent line on our own audio source.</summary>
        private static void Adopt(AudioSource source, SV.Chara.AI playerAI)
        {
            if (_voice == null)
            {
                // Not kept across scenes; made again when needed.
                _voiceObject = new GameObject("SVS_FreeRoam Voice");
                _voice = _voiceObject.AddComponent<AudioSource>();
                _voice.playOnAwake = false;
                _voice.spatialBlend = 0f;
            }

            _gameVoice = source;
            _voice.clip = source.clip;
            _voice.outputAudioMixerGroup = source.outputAudioMixerGroup;
            _voice.loop = _voiceLoops;
            _voice.volume = 1f;
            _voice.Play();
            _voiceSeekUntil = 0f;
            if (playerAI.animator != null)
                _voiceCycle = Mathf.Floor(playerAI.animator.GetCurrentAnimatorStateInfo(0).normalizedTime);
            Notice.Log($"Idle voice: {Name(_voiceId)} plays '{source.clip.name}' (the game's own copy is at volume {source.volume:0.00}).");
        }

        private static void StopVoice()
        {
            _voiceId = -1;
            _voiceSeekUntil = 0f;
            try
            {
                if (_voice != null && _voice.isPlaying) _voice.Stop();
                // A looping line would go on, silently, for as long as the game let it.
                if (_gameVoice != null && _gameVoice.isPlaying && _gameVoice.loop) _gameVoice.Stop();
            }
            catch { }
            _gameVoice = null;
        }

        private static void VoiceFailed(Exception e)
        {
            _voiceFailed = true;
            _voiceId = -1;
            Plugin.Logger.LogWarning("Animation sounds are off: " + e.Message);
        }
    }
}
