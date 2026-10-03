using System;
using System.Collections.Generic;
using Character;
using HarmonyLib;
using Manager;
using SV;
using UnityEngine;

namespace SVS_FreeRoam
{
    /// <summary>
    /// Force High Poly: characters walking the map use their full-detail models instead of
    /// the cheap ones.
    ///
    /// SVS keeps two copies of a character: a low-poly one on the map and a separate
    /// high-poly one built for conversations and H, drawn by its own camera. The two are told
    /// apart by render layer as well as by mesh: low-poly parts sit on Human.lowLayer (7),
    /// which the map camera draws, and high-poly parts on Human.highLayer (10), which it does
    /// not (FINDINGS.md §20).
    ///
    /// Map characters cannot be built high-poly (the creation path could not be patched),
    /// so they are upgraded after the fact, exactly as confirmed by hand in RUE:
    ///
    ///   1. Human.hiPoly = true, then Reload(): the parts load again at full detail -- and
    ///      land on the high layer, invisible to the map camera.
    ///   2. Every part on the high layer is moved to the low one, so the map camera draws it
    ///      and the conversation camera does not. To the rest of the game it is an ordinary
    ///      map character.
    ///
    /// One character per frame, so a full map does not hitch all at once. Turning the option
    /// off does the reverse. Parts loaded later (a clothes change) arrive on the high layer
    /// again, so upgraded characters keep being checked.
    /// </summary>
    internal static class HighPoly
    {
        private sealed class Tracked
        {
            public Human Human;
            public float FirstSeen;
            public bool Upgraded;
            public float FreshUntil;
        }

        private static readonly Dictionary<IntPtr, Tracked> _tracked = new Dictionary<IntPtr, Tracked>();
        private static readonly List<IntPtr> _order = new List<IntPtr>();
        private static int _sweepIndex;
        private static float _nextDiscover;

        /// <summary>A character must exist this long before it is reloaded, so its own first
        /// load has finished.</summary>
        private const float SettleBeforeUpgrade = 1f;

        /// <summary>After a reload, parts keep arriving for a while; check every frame then.</summary>
        private const float FreshSeconds = 15f;

        /// <summary>The option is on. Only turning it off downgrades anyone.</summary>
        private static bool Wanted => Plugin.Enabled.Value && Plugin.ForceHighPoly.Value;

        // ---------------------------------------------------------- scenes

        /// <summary>No upgrades until then: a scene is running, or ended only a moment ago.</summary>
        private static float _suspendedUntil;

        private static bool Suspended => Time.unscaledTime < _suspendedUntil;

        /// <summary>
        /// Some scenes get stuck on a high-poly player: Work, Study, Exercise, Eat and others
        /// stay on their question line for good. The step that fails is always the same,
        /// ADV.Commands.Game.Apartment.Judgement.Do(), on an Enumerable.First() of something
        /// that comes back empty ("Sequence contains no elements", FINDINGS.md §24); the
        /// scenario never gets its next line.
        ///
        /// Only the player matters, and only the flag: with Human.hiPoly false on the player
        /// while that one method runs, the model left as it is, nothing freezes. So a prefix
        /// on it sets the flag false and the next frame sets it back. Nothing is reloaded and
        /// no other scene is touched.
        ///
        /// Tried on the way and dropped: reloading everyone, the scene's characters or the
        /// player as low poly for the scene; skipping the game's animation backup; finalizers
        /// on every ADV.Commands.Game.* Do() (patching ~120 methods crashed the game).
        /// </summary>
        internal static void Apply(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(ADV.Commands.Game.Apartment.Judgement),
                                                nameof(ADV.Commands.Game.Apartment.Judgement.Do));
                if (target == null) throw new MissingMethodException("not found");
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(HighPoly), nameof(BeforeJudgement)));
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("Force High Poly: could not hook the scene judgement step, " +
                                         "some scenes may get stuck (" + e.Message + ").");
            }
        }

        /// <summary>The player, told to call itself low poly until the next frame.</summary>
        private static Tracked _pretending;

        private static void BeforeJudgement()
        {
            try
            {
                if (!Wanted) return;
                var player = GameChara.PlayerAI?.chaCtrl;
                if (player == null || !_tracked.TryGetValue(player.Pointer, out var t) || !t.Upgraded) return;

                player.hiPoly = false;
                _pretending = t;
                Notice.Show("High poly: a scene reached the step that needs the normal model - pretending for it");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("Force High Poly: judgement check failed: " + e.Message);
            }
        }

        private static void StopPretending()
        {
            var t = _pretending;
            _pretending = null;
            try { t.Human.hiPoly = true; } catch { }
        }

        /// <summary>Keeps upgrades held off for as long as a scene runs, and a moment after.</summary>
        private static void HoldForScenes()
        {
            var adv = ILLGames.Unity.Component.SingletonInitializer<ADV.ADVManager>._instance;
            if (adv != null && (adv.IsADV || adv.IsInsertADV))
                _suspendedUntil = Mathf.Max(_suspendedUntil, Time.unscaledTime + 1.5f);
        }

        /// <summary>Once per frame, from the SimulationScene.Update postfix.</summary>
        internal static void Tick()
        {
            HoldForScenes();
            float now = Time.unscaledTime;
            if (_pretending != null) StopPretending();
            bool wanted = Wanted;

            // During a scene (and a moment after) nobody new is upgraded -- but nobody already
            // upgraded is touched either. Treating the pause as "option off" was what switched
            // every character to low poly and back on every scene, the lag with a full map.
            bool mayUpgrade = wanted && !Suspended;

            // Looking for new characters twice a second is plenty; they are only upgraded a
            // second after appearing anyway.
            if (mayUpgrade && now >= _nextDiscover)
            {
                _nextDiscover = now + 0.5f;
                Discover(now);
            }
            if (_order.Count == 0) return;

            bool changedOne = false;
            for (int i = _order.Count - 1; i >= 0; i--)
            {
                var t = _tracked[_order[i]];
                GameObject root = null;
                try { root = t.Human?.gameObject; } catch { }

                if (root == null)
                {
                    _tracked.Remove(_order[i]);
                    _order.RemoveAt(i);
                    continue;
                }

                bool reloading = false;
                try { reloading = t.Human.isReloading; } catch { }
                if (reloading) t.FreshUntil = now + FreshSeconds;

                // At most one reload per frame, in either direction.
                if (!changedOne && !reloading)
                {
                    if (mayUpgrade && !t.Upgraded && now - t.FirstSeen >= SettleBeforeUpgrade)
                    {
                        changedOne = SetHighPoly(t, true, now);
                    }
                    else if (!wanted && t.Upgraded)
                    {
                        changedOne = SetHighPoly(t, false, now);
                    }
                }

                if (t.Upgraded && now < t.FreshUntil) ToLowLayer(root);
            }

            // Settled upgraded characters: one per frame, in case a part was re-layered later.
            if (_order.Count == 0) return;
            if (_sweepIndex >= _order.Count) _sweepIndex = 0;
            var s = _tracked[_order[_sweepIndex++]];
            if (s.Upgraded)
            {
                GameObject root = null;
                try { root = s.Human?.gameObject; } catch { }
                if (root != null) ToLowLayer(root);
            }

            // Everything downgraded and the option off: stop tracking.
            if (!wanted)
            {
                bool any = false;
                foreach (var t in _tracked.Values) any |= t.Upgraded;
                if (!any)
                {
                    _tracked.Clear();
                    _order.Clear();
                }
            }
        }

        /// <summary>Picks up map characters not seen before. Upgrades only ones still low
        /// poly, so the game's own high-poly copies are never touched.</summary>
        private static void Discover(float now)
        {
            var list = Game.AICharas;
            if (list == null) return;

            foreach (var ai in list)
            {
                if (ai == null) continue;
                Human human = null;
                try { human = ai.chaCtrl; } catch { }
                if (human == null || _tracked.ContainsKey(human.Pointer)) continue;
                if (human.hiPoly) continue;

                _tracked[human.Pointer] = new Tracked { Human = human, FirstSeen = now };
                _order.Add(human.Pointer);
            }
        }

        private static bool SetHighPoly(Tracked t, bool high, float now)
        {
            try
            {
                t.Human.hiPoly = high;
                t.Human.Reload();
                t.Upgraded = high;
                t.FreshUntil = high ? now + FreshSeconds : 0f;
                if (high) ToLowLayer(t.Human.gameObject);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("Force High Poly: could not reload a character: " + e.Message);
                t.Upgraded = high;    // do not retry every frame
                return false;
            }
        }

        /// <summary>
        /// Moves the character's parts off the high-poly layer, which the map camera does not
        /// draw, onto the map's character layer. To the rest of the game -- the map's lights,
        /// clicks, raycasts -- it is then an ordinary map character.
        /// </summary>
        private static void ToLowLayer(GameObject root)
        {
            int high = Human.highLayer;
            int low = Human.lowLayer;

            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.gameObject.layer == high) t.gameObject.layer = low;
        }
    }
}
