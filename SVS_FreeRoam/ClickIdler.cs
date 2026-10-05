using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ILLGames.Unity.Component;
using Manager;
using SV;
using UnityEngine;

namespace SVS_FreeRoam
{
    /// <summary>Which animations the wheel lists, and a tap picks from.</summary>
    public enum AnimationSet
    {
        Fitting,
        MapAnimations,
        Sitting,
        Favorites1,
        Favorites2,
        Favorites3,
        All,
    }

    /// <summary>
    /// Idle animations on demand, and the wheel for choosing them. The spots are the map's own
    /// "urouro" (wander) points, the ones the game sends the player to on arriving in a map;
    /// seats and other special spots are used by walking to the real point, so the game seats
    /// the character itself. Animations are played directly, by their id in
    /// AnimationCtrlManager.Animation (FINDINGS.md §28).
    /// </summary>
    internal static class ClickIdler
    {
        private const float ClickSlop = 8f;            // pixels a click may drift and still be a click
        private const float BodyHeight = 1.5f;         // feet to about the head, for clicks on yourself
        private const float HoldTime = 0.35f;          // seconds held before the wheel opens
        private const float SpotReach = 1f;            // third person: how near a seat has to be
        private const float SpotHeight = 0.4f;         // where on a seat a click is aimed, above its base
        private const string Star = "★ ";

        // What the game's standing spots offer, for maps whose spots cannot be read.
        private static readonly int[] DefaultStanding = { 0, 13, 15, 24, 26, 29, 31, 34 };

        private enum Press { None, Self, Character, ThirdPerson }

        private static Press _press;
        private static Vector3 _pressAt;
        private static float _pressTime;
        private static SV.Chara.AI _pressCharacter;
        private static int _thirdPersonFrame;
        // The wheel is staying up after the button was let go; a click chooses.
        private static bool _sticky;
        private static bool _stickyThirdPerson;
        // A click that chose from the wheel is nobody else's, until every button is let go.
        private static bool _swallow;
        private static int _swallowEndFrame = -1;
        private static List<Action> _actions;
        private static List<int> _wheelIds;            // the animations in the open wheel; null for other wheels
        private static int _playing = -1;
        private static int _listedMap = -1;
        private static List<int> _all;

        /// <summary>The frame a wheel was closed in: that press or release is not also a click.</summary>
        internal static int WheelClosedFrame { get; private set; } = -1;

        /// <summary>The wheel has the mouse: no walking forward, interacting or click-walking.</summary>
        internal static bool BlocksInput =>
            IdleWheel.IsOpen || _swallow || _swallowEndFrame == Time.frameCount;

        // ------------------------------------------------------------------ names and lists

        internal static string Name(int id) =>
            CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                ((AnimationCtrlManager.Animation)id).ToString().Replace('_', ' '));

        /// <summary>
        /// Every animation one character can do alone, whatever the map: the game's whole
        /// list without moving about, the paired ones (hug, kiss...) and H.
        /// </summary>
        internal static List<int> AllAnimations()
        {
            if (_all != null) return _all;
            _all = new List<int>();
            foreach (AnimationCtrlManager.Animation animation in Enum.GetValues(typeof(AnimationCtrlManager.Animation)))
            {
                int id = (int)animation;
                string name = animation.ToString();
                if (id >= 1000 || name.Contains("_f_") || name.Contains("_m_") || name == "run" ||
                    name == "escape" || name.StartsWith("walk")) continue;
                _all.Add(id);
            }
            return _all;
        }

        private static BepInEx.Configuration.ConfigEntry<string> Collection(int number) =>
            number == 2 ? Plugin.Favorites2 : number == 3 ? Plugin.Favorites3 : Plugin.Favorites1;

        /// <summary>One of the three favorite collections.</summary>
        private static List<int> Favorites(int number)
        {
            var ids = new List<int>();
            foreach (string part in Collection(number).Value.Split(','))
            {
                string name = part.Trim();
                foreach (int id in AllAnimations())
                    if (string.Equals(Name(id), name, StringComparison.OrdinalIgnoreCase) && !ids.Contains(id))
                        ids.Add(id);
            }
            return ids;
        }

        /// <summary>Every favorite, of all three collections.</summary>
        private static List<int> Favorites()
        {
            var ids = Favorites(1);
            for (int number = 2; number <= 3; number++)
                foreach (int id in Favorites(number)) if (!ids.Contains(id)) ids.Add(id);
            return ids;
        }

        /// <summary>The collection the Favorite Key changes: the one being shown, else the first.</summary>
        private static int ActiveCollection =>
            Plugin.WheelSet.Value == AnimationSet.Favorites2 ? 2
            : Plugin.WheelSet.Value == AnimationSet.Favorites3 ? 3 : 1;

        private static void ToggleFavorite(int id, int number)
        {
            var ids = Favorites(number);
            if (!ids.Remove(id)) ids.Add(id);
            Collection(number).Value = string.Join(", ", ids.ConvertAll(Name));
        }

        private static List<int>[] Collections() => new[] { Favorites(1), Favorites(2), Favorites(3) };

        /// <summary>Every animation any spot of this map offers, for any activity.</summary>
        private static List<int> MapAnimations(SV.Chara.AI playerAI)
        {
            var ids = new List<int>();
            var table = Spots(playerAI, out _);
            if (table != null)
            {
                foreach (var pair in table)
                {
                    var points = pair.Value?.points;
                    if (points == null) continue;
                    for (int i = 0; i < points.Count; i++)
                    {
                        var details = points[i]?.urouroDetails;
                        if (details == null) continue;
                        for (int d = 0; d < details.Count; d++)
                        {
                            var animations = details[d]?.animations;
                            if (animations == null) continue;
                            for (int k = 0; k < animations.Count; k++)
                                if (!ids.Contains(animations[k].animMotion)) ids.Add(animations[k].animMotion);
                        }
                    }
                }
            }
            if (ids.Count == 0) ids.AddRange(DefaultStanding);
            return ids;
        }

        /// <summary>The animations of the set chosen in the settings.</summary>
        private static List<int> SetAnimations(SV.Chara.AI playerAI)
        {
            switch (Plugin.WheelSet.Value)
            {
                case AnimationSet.MapAnimations: return MapAnimations(playerAI);
                case AnimationSet.Sitting: return AllAnimations().FindAll(NeedsSeat);
                case AnimationSet.All: return new List<int>(AllAnimations());
                case AnimationSet.Favorites1:
                case AnimationSet.Favorites2:
                case AnimationSet.Favorites3:
                    var favorites = Favorites(ActiveCollection);
                    if (favorites.Count > 0) return favorites;
                    Notice.Tell("That favorite collection is empty: with another animation set chosen, " +
                                $"hold for the wheel and press {Plugin.FavoriteKey.Value} on an animation.", 6f);
                    break;
            }
            return Fitting(playerAI);
        }

        /// <summary>A spot's offer for one activity includes sitting.</summary>
        private static bool SeatDetail(MovePointInfo.JobDetail detail)
        {
            var animations = detail?.animations;
            if (animations == null) return false;
            for (int k = 0; k < animations.Count; k++)
                if (NeedsSeat(animations[k].animMotion)) return true;
            return false;
        }

        /// <summary>
        /// Whether a wander point, as listed under one activity, is somewhere to sit: by its
        /// pose, or because what it offers for that activity is a sitting animation. The
        /// classroom's desks are "standing" points that offer Study's desk animations.
        /// </summary>
        private static bool IsSeat(MovePointInfo point, int job)
        {
            if (!IsStanding(point)) return true;
            var details = point.urouroDetails;
            if (details == null) return false;
            for (int i = 0; i < details.Count; i++)
                if (details[i] != null && (int)details[i].job == job && SeatDetail(details[i])) return true;
            return false;
        }

        private static bool IsSeat(MovePointInfo point)
        {
            if (!IsStanding(point)) return true;
            var details = point.urouroDetails;
            if (details == null) return false;
            for (int i = 0; i < details.Count; i++)
                if (SeatDetail(details[i])) return true;
            return false;
        }

        // The seat last sat on through us, and for which activity: what it offers for that
        // activity is what can be played there.
        private static MovePointInfo _usedSpot;
        private static int _usedJob;

        internal static void NoteSpot(MovePointInfo spot, int job)
        {
            _usedSpot = spot;
            _usedJob = job;
        }

        private static Il2CppSystem.Collections.Generic.Dictionary<int, PointList.ListInfo> Spots(
            SV.Chara.AI playerAI, out int mapId)
        {
            mapId = -1;
            var mapManager = SingletonInitializerAsync<MapManager>.Instance;
            var bctrl = playerAI.BehaviourCtrl;
            if (mapManager == null || bctrl == null) return null;
            mapId = bctrl.NowMapID;

            MapCollisionCtrl.Info info = null;
            mapManager.pointInfoTable?.TryGetValue(mapId, out info);
            _pointList = info?.pointList;
            return _pointList?.urouroTable;
        }

        // The map's whole point list, as of the last Spots call.
        private static PointList _pointList;

        // A seat borrowed from an activity (study, a meal): the game only seats characters
        // there during that activity, so we do it by hand. Which point, where the body is,
        // and what can be played there.
        private static MovePointInfo _borrowed;
        private static Vector3 _borrowedAt;
        private static List<int> _borrowedIds;
        // Which table NearbySpot's last answer came from: 0 the wander points, else an activity's.
        private static int _nearbyTable;

        private static readonly string[] TableNames = { "urouro", "solo", "with", "everyone", "pc" };

        /// <summary>The activity tables: 1 solo, 2 with, 3 everyone, 4 pc.</summary>
        private static Il2CppSystem.Collections.Generic.Dictionary<int, PointList.ListInfo> Table(int table) =>
            _pointList == null ? null
            : table == 1 ? _pointList.soloTable
            : table == 2 ? _pointList.withTable
            : table == 3 ? _pointList.everyoneTable
            : _pointList.pcTable;

        private static Il2CppSystem.Collections.Generic.List<MovePointInfo.JobDetail> Details(MovePointInfo point, int table) =>
            table == 1 ? point.soloDetails
            : table == 2 ? point.withDetails
            : table == 3 ? point.everyoneDetails
            : point.pcDetails;

        /// <summary>What a point offers for one activity, if it has animations for it.</summary>
        private static MovePointInfo.JobDetail Detail(MovePointInfo point, int table, int job)
        {
            var details = Details(point, table);
            if (details == null) return null;
            for (int i = 0; i < details.Count; i++)
            {
                var detail = details[i];
                if (detail != null && (int)detail.job == job && detail.animations != null &&
                    detail.animations.Count > 0) return detail;
            }
            return null;
        }

        /// <summary>A spot to stand at, as opposed to a chair, a desk or a place on the ground.</summary>
        private static bool IsStanding(MovePointInfo point)
        {
            var poses = point.poses;
            if (poses == null || poses.Count == 0) return true;
            for (int i = 0; i < poses.Count; i++)
                if (poses[i] == MovePointInfo.PoseKind.Stand) return true;
            return false;
        }

        private static void AddAnimations(MovePointInfo point, List<int> ids)
        {
            if (_borrowed != null && point.Pointer == _borrowed.Pointer)
            {
                foreach (int id in _borrowedIds) if (!ids.Contains(id)) ids.Add(id);
                return;
            }
            var details = point.urouroDetails;
            if (details == null) return;

            // The seat we sat on: only what it offers for the activity it was used for.
            bool used = _usedSpot != null && point.Pointer == _usedSpot.Pointer;
            if (used)
            {
                used = false;
                for (int i = 0; i < details.Count; i++)
                    if (details[i] != null && (int)details[i].job == _usedJob) used = true;
            }

            for (int i = 0; i < details.Count; i++)
            {
                if (used && details[i] != null && (int)details[i].job != _usedJob) continue;
                var animations = details[i]?.animations;
                if (animations == null) continue;
                for (int k = 0; k < animations.Count; k++)
                {
                    int id = animations[k].animMotion;
                    if (!ids.Contains(id)) ids.Add(id);
                }
            }
        }

        /// <summary>Every animation the map's standing spots offer.</summary>
        internal static List<int> StandingAnimations(SV.Chara.AI playerAI)
        {
            var ids = new List<int>();
            var table = Spots(playerAI, out _);
            if (table != null)
            {
                foreach (var pair in table)
                {
                    var points = pair.Value?.points;
                    if (points == null) continue;
                    for (int i = 0; i < points.Count; i++)
                        if (points[i] != null && IsStanding(points[i])) AddAnimations(points[i], ids);
                }
            }
            if (ids.Count == 0) ids.AddRange(DefaultStanding);
            return ids;
        }

        /// <summary>The seat or other special spot the player is using, if any.</summary>
        private static MovePointInfo CurrentSeat(SV.Chara.AI playerAI)
        {
            if (_borrowed != null)
            {
                if (Vector3.Distance(playerAI.transform.position, _borrowedAt) < 0.5f) return _borrowed;
                Notice.Log($"Idle: no longer on the borrowed seat (now at {playerAI.transform.position}, " +
                           $"seat at {_borrowedAt}).");
                _borrowed = null;
            }

            var bctrl = playerAI.BehaviourCtrl;
            var point = bctrl?.target?.pInfo;
            return point != null && !Walker.IsOurTarget(bctrl) && IsSeat(point) &&
                   Vector3.Distance(SeatPosition(point), playerAI.transform.position) < 1f
                ? point : null;
        }

        /// <summary>What suits where the player is: the seat's animations, or the standing ones.</summary>
        private static List<int> Fitting(SV.Chara.AI playerAI)
        {
            var seat = CurrentSeat(playerAI);
            if (seat != null)
            {
                var ids = new List<int>();
                AddAnimations(seat, ids);
                if (ids.Count > 0) return ids;
            }
            return StandingAnimations(playerAI);
        }

        /// <summary>The pose to go back to when an animation is stopped.</summary>
        private static int Resting(SV.Chara.AI playerAI)
        {
            var seat = CurrentSeat(playerAI);
            if (seat == null) return 0;
            var ids = new List<int>();
            AddAnimations(seat, ids);
            return ids.Count > 0 ? ids[0] : 0;      // a spot lists its plain waiting pose first
        }

        private static Vector3 SeatPosition(MovePointInfo point)
        {
            if (_borrowed != null && point.Pointer == _borrowed.Pointer) return _borrowedAt;
            var details = point.urouroDetails;
            if (details != null)
                for (int i = 0; i < details.Count; i++)
                {
                    var offset = details[i]?.charactorOffset;
                    if (offset != null) return offset.position;
                }
            return point.transform.position;
        }

        // ------------------------------------------------------------------ playing

        private static void Play(SV.Chara.AI playerAI, int id)
        {
            if (playerAI == null) return;
            Follower.Stop("idle animation", playerAI);
            // A click-walk still under way would carry the animation along with it.
            if (Walker.IsWalking(playerAI.BehaviourCtrl)) playerAI.BehaviourCtrl.Stop(true);
            Notice.Log($"Idle: playing {id} ({Name(id)}).");

            bool played = false;
            if (Plugin.AnimationProps.Value)
            {
                // The game's own way of putting a character into an animation, which also
                // shows what the animation holds (a phone, a book).
                try
                {
                    var manager = SingletonInitializerAsync<AnimationCtrlManager>.Instance;
                    if (manager != null && playerAI.animator != null)
                    {
                        manager.SetAnim(playerAI.BehaviourCtrl, playerAI.animator, playerAI.charaData,
                                        id, 0.25f, true, false, true);
                        manager.SetItemVisible(playerAI.BehaviourCtrl);
                        played = true;
                    }
                }
                catch (Exception e)
                {
                    Notice.Log("Idle: the game's own animation call failed (" + e.Message + "); playing it plainly.");
                }
            }
            if (!played) playerAI.SetLowpolyAnimation(id, false, true, 0.25f, true);
            if (Plugin.AnimationProps.Value) StartVoice(playerAI, id);

            _held = id != Resting(playerAI) && CurrentSeat(playerAI) != null;
            _heldLogged = false;
            _heldSince = Time.unscaledTime;
            _heldAt = playerAI.transform.position;
            _heldRotation = playerAI.transform.rotation;
            _playing = id == Resting(playerAI) ? -1 : id;
        }

        /// <summary>An animation made for a chair or a desk.</summary>
        private static bool NeedsSeat(int id)
        {
            var manager = SingletonInitializerAsync<AnimationCtrlManager>.Instance;
            if (manager?.posePtnChairIDs != null && manager.posePtnChairIDs.Contains(id)) return true;
            if (manager?.posePtnDeskIDs != null && manager.posePtnDeskIDs.Contains(id)) return true;
            if (id == 14 || id == 16) return true;                 // waiting actions 1 and 3: the chair ones
            string name = ((AnimationCtrlManager.Animation)id).ToString();
            return name.Contains("chair") || name.Contains("desk");
        }

        private static bool _voiceFailed;
        private static bool _voicesListed;
        private static float _audioListAt = -1f;
        private static readonly HashSet<IntPtr> _voicesBefore = new HashSet<IntPtr>();
        private static float _voiceSeekUntil;
        private static AudioSource _voiceSource;

        /// <summary>A playing voice line: the game routes them to its "PCM" mixer group.</summary>
        private static bool IsVoice(AudioSource source) =>
            source != null && source.isPlaying && source.outputAudioMixerGroup != null &&
            source.outputAudioMixerGroup.name == "PCM";

        /// <summary>Finds the line just started for the player and keeps it audible.</summary>
        private static void KeepVoiceAudible()
        {
            if (_voiceSource == null && Time.unscaledTime < _voiceSeekUntil)
            {
                foreach (var source in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                {
                    if (!IsVoice(source) || _voicesBefore.Contains(source.Pointer)) continue;
                    _voiceSource = source;
                    Notice.Log($"Idle voice: found the line '{(source.clip != null ? source.clip.name : "none")}' at volume {source.volume:0.00}; turning it up.");
                    break;
                }
            }
            if (_voiceSource == null) return;
            if (!_voiceSource.isPlaying) { _voiceSource = null; return; }
            if (_voiceSource.volume < 0.99f) _voiceSource.volume = 1f;
        }

        /// <summary>
        /// Asks the game's voice table for this animation's line and plays it. Logs what it
        /// finds with Debug Info on, since how the table is keyed is not known for sure.
        /// </summary>
        private static void StartVoice(SV.Chara.AI playerAI, int id)
        {
            if (_voiceFailed) return;
            try
            {
                var voices = SingletonInitializerAsync<LowpolyActionVoiceManager>.Instance;
                var animations = SingletonInitializerAsync<AnimationCtrlManager>.Instance;
                var table = voices?.infoTable;
                if (table == null || animations == null)
                {
                    Notice.Log("Idle voice: the game's voice table is not there.");
                    return;
                }

                int hash = animations.GetHash(id);
                int state = playerAI.animator != null
                    ? playerAI.animator.GetCurrentAnimatorStateInfo(0).shortNameHash : 0;
                bool found = false;
                int key = 0;
                string what = "";
                foreach (var pair in table)
                {
                    var info = pair.Value;
                    if (info.hash != hash) continue;
                    found = true;
                    key = pair.Key;
                    what = $"'{info.name}' loop {info.IsLoop} noMale {info.IsNoMale} bundles {info.bundleInfos?.Count ?? -1}";
                    break;
                }

                // The line it starts is silent (volume 0 on its audio source): note which
                // voices were already playing, so the new one can be found and turned up.
                _voicesBefore.Clear();
                foreach (var source in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                    if (IsVoice(source)) _voicesBefore.Add(source.Pointer);

                bool played = found && voices.LowpolyVoicePlay(key, playerAI);
                if (played)
                {
                    _audioListAt = Time.unscaledTime + 1f;
                    _voiceSeekUntil = Time.unscaledTime + 1f;
                    _voiceSource = null;
                }
                Notice.Log($"Idle voice: animation {id} ({Name(id)}) hash {hash}, animator state now {state}; " +
                           $"table has {table.Count} entries; match {found} {what} key {key}; played {played}.");

                if (!_voicesListed && Notice.On)
                {
                    _voicesListed = true;
                    var sb = new StringBuilder("Idle voice: the game's voice table:");
                    foreach (var pair in table)
                        sb.Append("\n  key ").Append(pair.Key).Append(" hash ").Append(pair.Value.hash)
                          .Append(" '").Append(pair.Value.name).Append("' loop ").Append(pair.Value.IsLoop)
                          .Append(" noMale ").Append(pair.Value.IsNoMale);
                    sb.Append("\n  animation hashes:");
                    foreach (int other in AllAnimations())
                        sb.Append(' ').Append(other).Append('=').Append(animations.GetHash(other));
                    Notice.Log(sb.ToString());
                }
            }
            catch (Exception e)
            {
                _voiceFailed = true;
                Plugin.Logger.LogWarning("Animation voices are off: reading the game's voice table failed (" + e.Message + ").");
            }
        }

        /// <summary>
        /// The voice lines map characters speak during some animations (exercise...). The game
        /// runs this for characters acting on their own; for the player nothing does, so we
        /// do while an animation we started is playing.
        /// </summary>
        private static void Voice(SV.Chara.AI playerAI)
        {
            try { KeepVoiceAudible(); }
            catch (Exception e)
            {
                _voiceSource = null;
                _voiceSeekUntil = 0f;
                Notice.Log("Idle voice: could not turn the line up (" + e.Message + ").");
            }

            // The game says it played the line and nothing is heard: a second after each
            // one, with Debug Info on, list what the game is really playing and how loud.
            if (_audioListAt < 0f || Time.unscaledTime < _audioListAt) return;
            _audioListAt = -1f;
            if (!Notice.On) return;
            try
            {
                var cam = Camera.main;
                var sb = new StringBuilder("Idle voice: what is playing a second later:");
                int shown = 0;
                foreach (var source in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                {
                    if (source == null || !source.isPlaying) continue;
                    if (++shown > 25) break;
                    var t = source.transform;
                    sb.Append("\n  ").Append(t.parent != null ? t.parent.name + "/" : "").Append(t.name)
                      .Append(" clip '").Append(source.clip != null ? source.clip.name : "none")
                      .Append("' volume ").Append(source.volume.ToString("0.00"))
                      .Append(" mute ").Append(source.mute)
                      .Append(" 3D ").Append(source.spatialBlend.ToString("0.0"))
                      .Append(" max ").Append(source.maxDistance.ToString("0"))
                      .Append(" from camera ").Append(cam != null ? Vector3.Distance(t.position, cam.transform.position).ToString("0.0") : "?")
                      .Append(" group ").Append(source.outputAudioMixerGroup != null ? source.outputAudioMixerGroup.name : "none");
                }
                var player = playerAI.transform;
                sb.Append("\n  player has ").Append(player.GetComponentsInChildren<AudioSource>(true).Length)
                  .Append(" audio sources under it; listener on '");
                var listener = UnityEngine.Object.FindObjectOfType<AudioListener>();
                sb.Append(listener != null ? listener.name : "none").Append("'.");
                Notice.Log(sb.ToString());
            }
            catch (Exception e)
            {
                Notice.Log("Idle voice: could not list the audio (" + e.Message + ").");
            }
        }

        private static bool PlayingOurs(SV.Chara.AI playerAI)
        {
            var manager = SingletonInitializerAsync<AnimationCtrlManager>.Instance;
            return _playing >= 0 && manager != null && manager.IsPlayMotion(playerAI.BehaviourCtrl, _playing);
        }

        /// <summary>
        /// A tap: stops the animation we started if it is still playing; otherwise whatever
        /// Tap Plays says.
        /// </summary>
        private static void Tap(SV.Chara.AI playerAI, bool thirdPerson)
        {
            if (PlayingOurs(playerAI))
            {
                Play(playerAI, Resting(playerAI));
                return;
            }

            // A random one of the chosen set. Seated: of those this seat offers. Standing in
            // the open: of those that need no seat, or the character would sit on air (the
            // wheel still lists them all). If that leaves nothing, the set as it is.
            var ids = SetAnimations(playerAI);
            var fitting = Fitting(playerAI);
            bool seated = CurrentSeat(playerAI) != null;
            var usable = ids.FindAll(id => seated ? fitting.Contains(id) : !NeedsSeat(id));
            if (usable.Count > 0) ids = usable;

            // Not the plain waiting poses: those are what the character does anyway.
            int resting = Resting(playerAI);
            var special = ids.FindAll(id => id != resting);
            if (special.Count > 0) ids = special;
            Play(playerAI, ids[UnityEngine.Random.Range(0, ids.Count)]);
        }

        // ------------------------------------------------------------------ wheels

        /// <summary>What the held wheel lists: the chosen set, its favorites first.</summary>
        private static List<int> WheelAnimations(SV.Chara.AI playerAI)
        {
            var set = SetAnimations(playerAI);
            var ids = new List<int>();
            foreach (int id in Favorites()) if (set.Contains(id)) ids.Add(id);
            foreach (int id in set) if (!ids.Contains(id)) ids.Add(id);
            return ids;
        }

        /// <summary>The animation's name, starred with the numbers of the collections it is in.</summary>
        private static string WheelLabel(int id, List<int>[] collections)
        {
            string marks = "";
            for (int n = 0; n < collections.Length; n++)
                if (collections[n].Contains(id)) marks += (n + 1).ToString();
            return marks.Length == 0 ? Name(id) : "\u2605" + marks + " " + Name(id);
        }

        /// <summary>
        /// A choice from the wheel. An animation made for a seat, chosen while standing next
        /// to one, first sits the player there; the animation follows once seated.
        /// </summary>
        private static void PlayChoice(SV.Chara.AI playerAI, int id)
        {
            if (playerAI == null) return;
            if (NeedsSeat(id) && CurrentSeat(playerAI) == null)
            {
                var spot = NearbySpot(playerAI, out int job);
                if (spot != null)
                {
                    UseSpot(playerAI, spot, job);
                    _pendingId = id;
                    _pendingAt = Time.unscaledTime + 0.7f;
                    return;
                }
            }
            Play(playerAI, id);
        }

        private static int _pendingId = -1;
        private static float _pendingAt;

        // Seated with an animation of ours: where the player was put. Some animations (the
        // meals at the cafe's tables) make the game slide the player off the seat a moment
        // later; while this is set they are kept there.
        private static bool _held;
        private static bool _heldLogged;
        private static float _heldSince;
        private static Vector3 _heldAt;
        private static Quaternion _heldRotation;

        private static void SeatUpkeep(SV.Chara.AI playerAI)
        {
            if (_pendingId >= 0 && Time.unscaledTime >= _pendingAt)
            {
                int id = _pendingId;
                _pendingId = -1;
                if (CurrentSeat(playerAI) != null) Play(playerAI, id);
                else Notice.Log($"Idle: not seated after all, so {Name(id)} was not played.");
            }

            if (!_held) return;
            // (The animation takes a moment to start: not judged by it for the first second.)
            bool leaving = (Time.unscaledTime - _heldSince > 1f && !PlayingOurs(playerAI)) || Walker.IsOurTarget(playerAI.BehaviourCtrl) ||
                           Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.S) ||
                           Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.UpArrow) ||
                           Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.LeftArrow) ||
                           Input.GetKey(KeyCode.RightArrow) ||
                           (Cursor.lockState == CursorLockMode.Locked && !BlocksInput &&
                            (Input.GetMouseButton(0) || Input.GetMouseButton(1)));
            if (leaving) { _held = false; return; }

            var t = playerAI.transform;
            float moved = Vector3.Distance(t.position, _heldAt);
            if (moved < 0.02f) return;
            if (!_heldLogged)
            {
                _heldLogged = true;
                Notice.Log($"Idle: the game moved the seated player {moved:0.00} m (to {t.position}); keeping them on the seat.");
            }
            t.SetPositionAndRotation(_heldAt, _heldRotation);
        }

        private static void OpenAnimationWheel(List<int> ids, Vector2 centre, bool virtualPointer)
        {
            var collections = Collections();
            var labels = new List<string>();
            _actions = new List<Action>();
            _wheelIds = ids;
            foreach (int id in ids)
            {
                int chosen = id;
                labels.Add(WheelLabel(id, collections));
                _actions.Add(() => PlayChoice(GameChara.PlayerAI, chosen));
            }
            IdleWheel.Open(labels, centre, virtualPointer);
        }

        private static void OpenCharacterWheel(SimulationScene scene, SV.Chara.AI npc, Vector2 centre)
        {
            var labels = new List<string>();
            _actions = new List<Action>();
            _wheelIds = null;

            labels.Add("Talk");
            _actions.Add(() => ThirdPersonController.WalkToCharacter(scene, GameChara.PlayerAI, npc));

            labels.Add(Follower.IsFollowing(npc) ? "Stop following" : "Follow");
            _actions.Add(() => Follower.Toggle(npc, GameChara.PlayerAI));

            if (SwitchButtonCompat.Available)
            {
                labels.Add("Switch to");
                _actions.Add(() => SwitchButtonCompat.SwitchTo(npc));
            }
            IdleWheel.Open(labels, centre, false);
        }

        /// <summary>
        /// While a wheel is open: the favorite key stars the animation under the pointer, and
        /// a left click chooses, whichever button opened the wheel. True when the wheel was
        /// closed by that click.
        /// </summary>
        private static bool WheelKeys()
        {
            if (!IdleWheel.IsOpen) return false;

            var highlighted = IdleWheel.Highlighted;
            // The favorite key stars it in the collection being shown (else the first); the
            // number keys 1 to 3 say which collection outright.
            int collection = Input.GetKeyDown(Plugin.FavoriteKey.Value) ? ActiveCollection
                           : Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1) ? 1
                           : Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2) ? 2
                           : Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3) ? 3 : 0;
            if (_wheelIds != null && highlighted != null && highlighted.Value < _wheelIds.Count &&
                collection != 0)
            {
                int id = _wheelIds[highlighted.Value];
                ToggleFavorite(id, collection);
                IdleWheel.SetLabel(highlighted.Value, WheelLabel(id, Collections()));
            }

            if (!Input.GetMouseButtonDown(0)) return false;
            CloseWheel();
            _swallow = true;
            _sticky = false;
            _press = Press.None;
            _pressCharacter = null;
            return true;
        }

        private static void CloseWheel()
        {
            var chosen = IdleWheel.Close();
            WheelClosedFrame = Time.frameCount;
            if (chosen == null || _actions == null || chosen.Value >= _actions.Count) return;
            try { _actions[chosen.Value](); }
            catch (Exception e) { Plugin.Logger.LogWarning("Wheel choice failed: " + e.Message); }
        }

        /// <summary>
        /// The button was let go with the wheel open: choose. In third person a wheel with
        /// more than one page stays up instead, until a choice is clicked.
        /// </summary>
        private static void ReleaseWheel(bool thirdPerson)
        {
            if (!thirdPerson || IdleWheel.Pages <= 1) { CloseWheel(); return; }
            _sticky = true;
            _stickyThirdPerson = true;
            IdleWheel.Sticky = true;
        }

        private static void Cancel()
        {
            _sticky = false;
            _press = Press.None;
            _pressCharacter = null;
            IdleWheel.Close();
        }

        // ------------------------------------------------------------------ cursor free

        /// <summary>
        /// With the cursor showing: a click of the idle button on your own character plays an
        /// animation (or stops it), holding it there opens the animation wheel, and holding it
        /// on another character opens their wheel.
        /// </summary>
        internal static void Update(SimulationScene scene, SV.Chara.AI playerAI)
        {
            if (Notice.On) ListMapAnimations(playerAI);
            Voice(playerAI);
            SeatUpkeep(playerAI);

            if (_swallow && !Input.GetMouseButton(0) && !Input.GetMouseButton(1) && !Input.GetMouseButton(2))
            {
                _swallow = false;
                _swallowEndFrame = Time.frameCount;
            }

            if (!Plugin.ClickIdle.Value) { Cancel(); return; }
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                // Third person's own press or wheel, unless third person stopped looking after it.
                bool thirdPersons = _press == Press.ThirdPerson || (_sticky && _stickyThirdPerson);
                if (!thirdPersons || Time.frameCount - _thirdPersonFrame > 2) Cancel();
                return;
            }
            if (_press == Press.ThirdPerson || (_sticky && _stickyThirdPerson)) Cancel();

            if (WheelKeys()) return;

            if (_sticky)
            {
                if (!IdleWheel.IsOpen) _sticky = false;
                else
                {
                    if (Keys.Down(Plugin.IdleButton, Plugin.IdleButton2)) { CloseWheel(); _sticky = false; }
                    else IdleWheel.Tick();
                    return;
                }
            }

            if (Keys.Down(Plugin.IdleButton, Plugin.IdleButton2))
            {
                Cancel();
                var cam = Camera.main;
                if (cam != null && Picker.WhyNotClickable() == null)
                {
                    Picker.UnderMouse(cam, playerAI, out var pick);
                    if (pick.Character != null)
                    {
                        if (Plugin.CharacterWheel.Value)
                        {
                            _press = Press.Character;
                            _pressCharacter = pick.Character;
                        }
                    }
                    else if (OnPlayer(cam, playerAI)) _press = Press.Self;
                    _pressAt = Input.mousePosition;
                    _pressTime = Time.unscaledTime;
                }
            }
            if (_press == Press.None) return;

            bool dragged = (Input.mousePosition - _pressAt).magnitude > ClickSlop;
            if (Keys.Held(Plugin.IdleButton, Plugin.IdleButton2))
            {
                if (!IdleWheel.IsOpen && !dragged && Time.unscaledTime - _pressTime >= HoldTime)
                {
                    if (_press == Press.Self) OpenAnimationWheel(WheelAnimations(playerAI), _pressAt, false);
                    else if (_pressCharacter != null) OpenCharacterWheel(scene, _pressCharacter, _pressAt);
                }
                IdleWheel.Tick();
                return;
            }

            // Released.
            var released = _press;
            _press = Press.None;
            _pressCharacter = null;
            if (IdleWheel.IsOpen) ReleaseWheel(false);
            else if (!dragged && released == Press.Self) Tap(playerAI, false);
        }

        /// <summary>Whether the cursor is on the player's body, judged on screen: feet to head.</summary>
        private static bool OnPlayer(Camera cam, SV.Chara.AI playerAI)
        {
            var feetWorld = playerAI.transform.position;
            var feet = cam.WorldToScreenPoint(feetWorld);
            var head = cam.WorldToScreenPoint(feetWorld + Vector3.up * BodyHeight);
            if (feet.z <= 0f || head.z <= 0f) return false;

            Vector2 a = feet, b = head, m = Input.mousePosition;
            var ab = b - a;
            float length = ab.magnitude;
            float t = length < 0.001f ? 0f : Mathf.Clamp01(Vector2.Dot(m - a, ab) / (length * length));
            float distance = Vector2.Distance(m, a + ab * t);
            return distance <= Mathf.Max(20f, length * 0.2f);
        }

        // ------------------------------------------------------------------ click to walk

        /// <summary>
        /// The seat or other special spot the mouse points at. Judged by how close the line
        /// of sight passes to it, not by what the click hits: most props have nothing to hit.
        /// </summary>
        internal static bool SpotUnderMouse(Camera cam, SV.Chara.AI playerAI,
                                            out MovePointInfo spot, out int job)
        {
            spot = null;
            job = -1;
            var table = Spots(playerAI, out _);
            if (table == null) return false;

            var ray = cam.ScreenPointToRay(Input.mousePosition);
            float best = Plugin.SpotClickSize.Value;
            foreach (var pair in table)
            {
                var points = pair.Value?.points;
                if (points == null) continue;
                for (int i = 0; i < points.Count; i++)
                {
                    var point = points[i];
                    if (point == null || !IsSeat(point, pair.Key)) continue;

                    var to = SeatPosition(point) + Vector3.up * SpotHeight - ray.origin;
                    if (Vector3.Dot(to, ray.direction) <= 0f) continue;
                    float distance = Vector3.Cross(ray.direction, to).magnitude;
                    if (distance < best || (distance == best && pair.Key == -1))
                    {
                        best = distance;
                        spot = point;
                        job = pair.Key;
                    }
                }
            }
            return spot != null;
        }

        // ------------------------------------------------------------------ third person

        /// <summary>The seat or other special spot within reach of the player, if any.</summary>
        internal static MovePointInfo NearbySpot(SV.Chara.AI playerAI, out int job)
        {
            job = -1;
            _nearbyTable = 0;
            MovePointInfo spot = null;
            if (!Plugin.ThirdPersonSpots.Value) return null;
            var table = Spots(playerAI, out _);
            if (table == null) return null;

            var feet = playerAI.transform.position;
            float best = SpotReach;
            foreach (var pair in table)
            {
                var points = pair.Value?.points;
                if (points == null) continue;
                for (int i = 0; i < points.Count; i++)
                {
                    var point = points[i];
                    if (point == null || !IsSeat(point, pair.Key)) continue;
                    float distance = Mathf.Min(Vector3.Distance(point.transform.position, feet),
                                               Vector3.Distance(SeatPosition(point), feet));
                    if (distance < best || (distance == best && pair.Key == -1))
                    {
                        best = distance;
                        spot = point;
                        job = pair.Key;
                    }
                }
            }
            if (spot != null) return spot;

            // No wander seat here: the seats of the map's activities (the classroom's desks for
            // Study, the cafe's tables for a meal), which the game only uses during them.
            for (int t = 1; t <= 4; t++)
            {
                var activity = Table(t);
                if (activity == null) continue;
                foreach (var pair in activity)
                {
                    var points = pair.Value?.points;
                    if (points == null) continue;
                    for (int i = 0; i < points.Count; i++)
                    {
                        var point = points[i];
                        if (point == null) continue;
                        var detail = Detail(point, t, pair.Key);
                        if (detail == null || !SeatDetail(detail)) continue;
                        var seatAt = detail.charactorOffset != null ? detail.charactorOffset.position
                                                                    : point.transform.position;
                        float distance = Mathf.Min(Vector3.Distance(point.transform.position, feet),
                                                   Vector3.Distance(seatAt, feet));
                        if (distance >= best) continue;
                        best = distance;
                        spot = point;
                        job = pair.Key;
                        _nearbyTable = t;
                    }
                }
            }
            return spot;
        }

        /// <summary>
        /// Sits the player on an activity's seat by hand: onto the seat's own spot, facing its
        /// way, in the first animation the activity lists there.
        /// </summary>
        private static void UseBorrowed(SV.Chara.AI playerAI, MovePointInfo spot, int table, int job)
        {
            var detail = Detail(spot, table, job);
            if (detail == null) return;
            var ids = new List<int>();
            for (int i = 0; i < detail.animations.Count; i++)
                if (!ids.Contains(detail.animations[i].animMotion)) ids.Add(detail.animations[i].animMotion);

            Follower.Stop("using a spot", playerAI);
            playerAI.BehaviourCtrl.Stop(true);
            var where = detail.charactorOffset != null ? detail.charactorOffset : spot.transform;
            Notice.Log($"Idle: borrowing '{spot.name}' from the {TableNames[table]} table, activity " +
                       $"{(MovePointInfo.JobKind)job}: point at {spot.transform.position}, seat at {where.position}" +
                       $"{(detail.charactorOffset != null ? "" : " (no offset)")}, prop '{detail.moveObjectName}'.");

            playerAI.position = spot.transform.position;
            playerAI.transform.SetPositionAndRotation(where.position, where.rotation);
            _borrowed = spot;
            _borrowedAt = where.position;
            _borrowedIds = ids;
            Play(playerAI, ids[0]);
        }

        /// <summary>
        /// Third person: sit on, or otherwise use, this spot within reach. The player is put on
        /// the spot first, so the game's walk there is over at once and it goes straight to
        /// seating the character: walking the last step by its own steering sometimes left
        /// the walk animation running (as at doorways, where the same is done).
        /// </summary>
        private static void UseSpot(SV.Chara.AI playerAI, MovePointInfo spot, int job)
        {
            if (_nearbyTable != 0)
            {
                UseBorrowed(playerAI, spot, _nearbyTable, job);
                return;
            }
            Follower.Stop("using a spot", playerAI);
            NoteSpot(spot, job);
            Notice.Log($"Idle: using {Describe(spot)}, job {job}.");
            playerAI.BehaviourCtrl.Stop(true);
            playerAI.position = spot.transform.position;
            Walker.WalkToPoint(playerAI, spot, playerAI.BehaviourCtrl.NowMapID, job);
        }

        /// <summary>
        /// Third person, cursor locked: a tap of the idle key uses the seat within reach or
        /// plays an animation (or stops it), holding it opens the wheel.
        /// <paramref name="free"/>: the key has nothing else to do (no walk under way, nobody
        /// marked to walk to).
        /// </summary>
        internal static void ThirdPerson(SV.Chara.AI playerAI, bool free)
        {
            if (!Plugin.ClickIdle.Value) return;
            if ((_press != Press.None && _press != Press.ThirdPerson) || (_sticky && !_stickyThirdPerson))
                Cancel();
            _thirdPersonFrame = Time.frameCount;

            if (WheelKeys()) return;

            if (_sticky)
            {
                if (!IdleWheel.IsOpen) _sticky = false;
                else
                {
                    if (Keys.Down(Plugin.IdleKeyThirdPerson, Plugin.IdleKeyThirdPerson2)) { CloseWheel(); _sticky = false; }
                    else IdleWheel.Tick();
                    return;
                }
            }

            if (Keys.Down(Plugin.IdleKeyThirdPerson, Plugin.IdleKeyThirdPerson2) && free)
            {
                _press = Press.ThirdPerson;
                _pressTime = Time.unscaledTime;
            }
            if (_press != Press.ThirdPerson) return;

            if (Keys.Held(Plugin.IdleKeyThirdPerson, Plugin.IdleKeyThirdPerson2))
            {
                if (!IdleWheel.IsOpen && Time.unscaledTime - _pressTime >= HoldTime)
                    OpenAnimationWheel(WheelAnimations(playerAI), new Vector2(Screen.width, Screen.height) * 0.5f, true);
                IdleWheel.Tick();
                return;
            }

            // Released.
            _press = Press.None;
            if (IdleWheel.IsOpen) ReleaseWheel(true);
            else TapThirdPerson(playerAI);
        }

        /// <summary>
        /// The idle key tapped in third person: stops the animation we started; else gets up
        /// from the seat; else sits on the seat within reach; else whatever Tap Plays says.
        /// </summary>
        private static void TapThirdPerson(SV.Chara.AI playerAI)
        {
            if (PlayingOurs(playerAI)) { Tap(playerAI, true); return; }

            var seat = CurrentSeat(playerAI);
            if (seat != null)
            {
                _borrowed = null;
                // Leave the way a click-walk does: a walk to the floor in front of the seat.
                if (Walker.Snap(seat.transform.position, 3f, out var floor, out _))
                {
                    Notice.Log("Idle: getting up.");
                    Walker.WalkTo(playerAI, floor, playerAI.BehaviourCtrl.NowMapID);
                }
                return;
            }

            var spot = NearbySpot(playerAI, out int job);
            if (spot != null) { UseSpot(playerAI, spot, job); return; }
            Tap(playerAI, true);
        }

        // ------------------------------------------------------------------ for reference

        /// <summary>Once per map, with Debug Info on: every kind of spot and its animations.</summary>
        private static void ListMapAnimations(SV.Chara.AI playerAI)
        {
            var table = Spots(playerAI, out int mapId);
            if (table == null || mapId == _listedMap) return;
            _listedMap = mapId;

            // "Chair, activity None" -> spots counted, animation ids.
            var kinds = new SortedDictionary<string, (int Count, List<int> Ids)>();
            foreach (var pair in table)
            {
                var points = pair.Value?.points;
                if (points == null) continue;
                for (int i = 0; i < points.Count; i++)
                {
                    var point = points[i];
                    var details = point?.urouroDetails;
                    if (details == null) continue;

                    var poseText = new StringBuilder();
                    var poses = point.poses;
                    if (poses != null)
                        for (int k = 0; k < poses.Count; k++) poseText.Append(k > 0 ? "+" : "").Append(poses[k]);
                    if (poseText.Length == 0) poseText.Append("(no pose)");

                    for (int d = 0; d < details.Count; d++)
                    {
                        var detail = details[d];
                        var animations = detail?.animations;
                        if (animations == null) continue;
                        string key = $"{poseText}, activity {detail.job}, listed under {pair.Key}";
                        if (!kinds.TryGetValue(key, out var kind)) kind = (0, new List<int>());
                        for (int k = 0; k < animations.Count; k++)
                            if (!kind.Ids.Contains(animations[k].animMotion)) kind.Ids.Add(animations[k].animMotion);
                        kinds[key] = (kind.Count + 1, kind.Ids);
                    }
                }
            }

            var sb = new StringBuilder($"Idle: animations on map {mapId}:");
            foreach (var kind in kinds)
            {
                sb.Append("\n  ").Append(kind.Key).Append(" (").Append(kind.Value.Count).Append(" spots):");
                foreach (int id in kind.Value.Ids) sb.Append(' ').Append(id).Append('=').Append(Name(id)).Append(',');
            }
            for (int t = 1; t <= 4; t++)
            {
                var activity = Table(t);
                if (activity == null) continue;
                foreach (var pair in activity)
                {
                    var points = pair.Value?.points;
                    if (points == null) continue;
                    int seats = 0;
                    var ids = new List<int>();
                    for (int i = 0; i < points.Count; i++)
                    {
                        var point = points[i];
                        if (point == null) continue;
                        var detail = Detail(point, t, pair.Key);
                        if (detail == null || !SeatDetail(detail)) continue;
                        seats++;
                        for (int k = 0; k < detail.animations.Count; k++)
                            if (!ids.Contains(detail.animations[k].animMotion)) ids.Add(detail.animations[k].animMotion);
                    }
                    sb.Append("\n  ").Append(TableNames[t]).Append(" table, activity ")
                      .Append((MovePointInfo.JobKind)pair.Key).Append(": ").Append(points.Count)
                      .Append(" points, ").Append(seats).Append(" seats:");
                    foreach (int id in ids) sb.Append(' ').Append(id).Append('=').Append(Name(id)).Append(',');
                }
            }
            Notice.Log(sb.ToString());
        }

        internal static string Describe(MovePointInfo point)
        {
            var sb = new StringBuilder();
            sb.Append('\'').Append(point.name).Append("' at ").Append(point.transform.position).Append(" poses[");
            var poses = point.poses;
            if (poses != null)
                for (int i = 0; i < poses.Count; i++) sb.Append(i > 0 ? "," : "").Append(poses[i]);
            sb.Append(']');
            var details = point.urouroDetails;
            if (details != null)
                for (int i = 0; i < details.Count; i++)
                {
                    var detail = details[i];
                    if (detail == null) continue;
                    sb.Append(" {").Append(detail.job).Append(':');
                    var animations = detail.animations;
                    if (animations != null)
                        for (int k = 0; k < animations.Count; k++)
                            sb.Append(' ').Append(Name(animations[k].animMotion)).Append('x').Append(animations[k].weight);
                    if (detail.charactorOffset != null) sb.Append(", offset");
                    if (!string.IsNullOrEmpty(detail.moveObjectName)) sb.Append(", prop ").Append(detail.moveObjectName);
                    sb.Append('}');
                }
            return sb.ToString();
        }
    }
}
