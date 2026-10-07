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
    /// <summary>
    /// Which animations there are: their names, the sets the wheel and a tap choose from,
    /// the favorite collections, and what suits where the player is.
    /// </summary>
    internal static partial class ClickIdler
    {
        // What the game's standing spots offer, for maps whose spots cannot be read.
        private static readonly int[] DefaultStanding = { 0, 13, 15, 24, 26, 29, 31, 34 };

        private static List<int> _all;

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
                                $"hold for the wheel and press {ActiveCollection} on an animation.", 6f);
                    break;
            }
            // Fitting. Standing next to a seat, what that seat offers comes first: choosing one
            // sits the player there. (A tap never plays those standing: see Tap.)
            var fitting = Fitting(playerAI);
            if (CurrentSeat(playerAI) != null) return fitting;
            var spot = NearbySpot(playerAI, out int job, ChoiceReach);
            if (spot == null) return fitting;

            var ids = new List<int>();
            var offer = _nearbyTable != 0 ? Detail(spot, _nearbyTable, job) : UrouroDetail(spot, job);
            if (offer?.animations != null)
                for (int k = 0; k < offer.animations.Count; k++)
                    if (!ids.Contains(offer.animations[k].animMotion)) ids.Add(offer.animations[k].animMotion);
            foreach (int id in fitting) if (!ids.Contains(id)) ids.Add(id);
            return ids;
        }

        /// <summary>What a standing spot offers with no activity, that needs no seat.</summary>
        private static void AddStanding(MovePointInfo point, List<int> ids)
        {
            var details = point.urouroDetails;
            if (details == null) return;
            for (int i = 0; i < details.Count; i++)
            {
                var detail = details[i];
                if (detail?.animations == null || detail.job != MovePointInfo.JobKind.None) continue;
                for (int k = 0; k < detail.animations.Count; k++)
                {
                    int id = detail.animations[k].animMotion;
                    if (!NeedsSeat(id) && !ids.Contains(id)) ids.Add(id);
                }
            }
        }

        /// <summary>
        /// The standing idles of this map: what its standing spots offer when no activity is
        /// going on. (Their activities' animations, and the desk ones some "standing" spots
        /// carry, are the map's own: see MapAnimations.)
        /// </summary>
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
                        if (points[i] != null && IsStanding(points[i])) AddStanding(points[i], ids);
                }
            }
            if (ids.Count == 0) ids.AddRange(DefaultStanding);
            return ids;
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

        /// <summary>An animation made for a chair or a desk.</summary>
        private static bool NeedsSeat(int id)
        {
            // The jobs' animations are done standing, some of them at a table (the waitress
            // wiping one): not sitting, whatever list the game keeps them in.
            if (((AnimationCtrlManager.Animation)id).ToString().StartsWith("job_")) return false;
            var manager = SingletonInitializerAsync<AnimationCtrlManager>.Instance;
            if (manager?.posePtnChairIDs != null && manager.posePtnChairIDs.Contains(id)) return true;
            if (manager?.posePtnDeskIDs != null && manager.posePtnDeskIDs.Contains(id)) return true;
            if (id == 14 || id == 16) return true;                 // waiting actions 1 and 3: the chair ones
            string name = ((AnimationCtrlManager.Animation)id).ToString();
            return name.Contains("chair") || name.Contains("desk");
        }

        /// <summary>What the held wheel lists: the chosen set, its favorites first.</summary>
        private static List<int> WheelAnimations(SV.Chara.AI playerAI)
        {
            var set = SetAnimations(playerAI);
            var ids = new List<int>();
            foreach (int id in Favorites()) if (set.Contains(id)) ids.Add(id);
            foreach (int id in set) if (!ids.Contains(id)) ids.Add(id);
            return ids;
        }

        // A favorite's star: the collection's own colour, or one for "in two" and "in all three".
        private static readonly string[] CollectionColours = { "#FFD75A", "#6FD6FF", "#FF8FC8" };

        private const string InTwoColour = "#9CFF7A";

        private const string InThreeColour = "#C9A0FF";

        /// <summary>The numbers of the collections an animation is in, and the colour that goes with them.</summary>
        private static List<string> FavoriteMarks(int id, List<int>[] collections, out string colour)
        {
            var marks = new List<string>();
            int last = 0;
            for (int n = 0; n < collections.Length; n++)
            {
                if (!collections[n].Contains(id)) continue;
                marks.Add((n + 1).ToString());
                last = n;
            }
            colour = marks.Count == 0 ? null
                   : marks.Count == 1 ? CollectionColours[last]
                   : marks.Count == 2 ? InTwoColour : InThreeColour;
            return marks;
        }

        /// <summary>
        /// The animation's name. A favorite is starred with the numbers of the collections it
        /// is in and written in their colour throughout, pointed at or not.
        /// </summary>
        private static string WheelLabel(int id, List<int>[] collections)
        {
            var marks = FavoriteMarks(id, collections, out string colour);
            return marks.Count == 0 ? Name(id)
                 : $"<color={colour}>\u2605 {string.Join(", ", marks)}  {Name(id)}</color>";
        }
    }
}
