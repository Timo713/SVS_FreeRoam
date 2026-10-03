using System;
using System.Collections.Generic;
using System.Text;
using ILLGames.Unity.Component;
using Manager;
using SV;
using UnityEngine;
using UnityEngine.UI;

namespace SVS_FreeRoam
{
    /// <summary>
    /// Makes the location buttons follow the things they actually lead to, instead of sitting
    /// at fixed screen positions that mean nothing in a third-person view.
    ///
    /// Two kinds of button, two sources of marker:
    ///
    ///   TRAVEL   SV.MapMoveUIButtonCtrl.GotoMapID   pairs with SV.SVNodeLink2.endMapID
    ///            (the btnGoto&lt;NNN&gt; objects; NNN is the destination map id)
    ///
    ///   ACTION   SV.MapPCActionUIButtonCtrl.Job     pairs with the job marker table
    ///            (Work, Eat, Change Outfit, Pray)   MapManager.Instance.pointInfoTable
    ///                                                 [map].pointList.pcTable[job].points
    ///
    /// Three rules keep this well behaved, all learned from bugs:
    ///
    /// 1. **Never touch a button's own active flag.** The game switches buttons on and off to
    ///    reflect what is available right now. A button with nowhere to point is parked far
    ///    off screen instead, which hides it without overriding availability.
    /// 2. **Only the live UI.** Reached through MapManager, never a scene-wide search: those
    ///    turn up stale UI trees from previous locations, which is what produced duplicates.
    /// 3. **Only MapMoveUI entries the game left active.** The summary holds several
    ///    alternates and only one is in use; touching the others duplicates every button.
    ///
    /// The alternates are the game's Action points graphics option: MoveUIAlways (Constant)
    /// and MoveUINormal (Pop-ups). A Pop-ups button is a large invisible hit area over the
    /// doorway in the overview picture, with the visible label as a hidden child, imgFrame,
    /// that an Animator switches on while hovered. Those are presented like Constant ones
    /// while tracked; see PopUp.
    /// </summary>
    internal static class MapButtonTracker
    {
        private sealed class Tracked
        {
            public RectTransform Rect;
            public Canvas Canvas;
            public Vector3 OriginalPosition;
            public bool Moved;

            public int GotoMapId = int.MinValue;      // travel button
            public int JobKind = int.MinValue;        // action button

            /// <summary>The MapMoveUI this came from, to notice the game swapping sets.</summary>
            public GameObject Owner;

            // Pop-ups style only (see PopUp below). Null for the Constant style.
            public RectTransform Frame;
            public Animator Animator;
            public bool AnimatorWasEnabled;
            public bool FrameWasActive;
            public Image HitImage;
            public bool HitWasRaycastTarget;
            public Image FrameImage;
            public bool FrameWasRaycastTarget;
        }

        /// <summary>Comfortably outside any screen, for buttons with nothing to point at.</summary>
        private static readonly Vector3 Parked = new Vector3(-20000f, -20000f, 0f);

        private static readonly List<Tracked> _tracked = new List<Tracked>();
        private static readonly List<KeyValuePair<GameObject, bool>> _uiRoots =
            new List<KeyValuePair<GameObject, bool>>();
        private static readonly Dictionary<int, Vector3> _travelMarkers = new Dictionary<int, Vector3>();
        private static readonly Dictionary<int, Vector3> _jobMarkers = new Dictionary<int, Vector3>();

        private static int _builtForMap = int.MinValue;
        private static bool _active;
        private static bool _failed;

        internal static void Invalidate()
        {
            RestoreAll();
            _builtForMap = int.MinValue;
            _failed = false;
        }

        internal static void Run(Camera cam, int mapId, bool show)
        {
            if (!show || cam == null || _failed)
            {
                if (_active) RestoreAll();
                return;
            }

            try
            {
                Build(mapId);
                KeepUiVisible();
                RefreshJobMarkers(mapId);
                Position(cam);
                _active = true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("Map button tracking failed, standing down: " + e);
                RestoreAll();
                _failed = true;
            }
        }

        // ---------------------------------------------------------------- discovery

        /// <summary>
        /// Built once per location and not rebuilt while standing on it. Rebuilding
        /// periodically meant restoring every button to its original place and moving it
        /// again, which is what made them blink every couple of seconds.
        /// </summary>
        private static void Build(int mapId)
        {
            if (mapId == _builtForMap && _tracked.Count > 0 && !AnyStale()) return;

            RestoreAll();
            _builtForMap = mapId;

            var mapManager = SingletonInitializerAsync<MapManager>.Instance;
            if (mapManager == null) return;

            // SVS_3rdPov disables the travel UI wholesale while third-person is on. Only these
            // shared roots are forced back on; individual buttons and the alternate MapMoveUI
            // entries are left exactly as the game set them.
            ForceActive(mapManager.objMapMoveUICanvas);
            ForceActive(mapManager.objMapMoveUI);

            CollectTravelMarkers(mapId);
            CollectJobMarkers(mapManager, mapId);

            var summary = mapManager._mapMoveUISummary;
            var uis = summary != null ? summary.MapMoveUIs : null;
            if (uis == null) return;

            var seenTravel = new HashSet<int>();
            var seenJob = new HashSet<int>();
            var registered = new HashSet<IntPtr>();

            for (int i = 0; i < uis.Count; i++)
            {
                var ui = uis[i];
                if (ui == null) continue;

                // The summary keeps several alternates; only the one in use is active. Using
                // them all is what duplicated every button.
                if (!ui.gameObject.active) continue;

                var travel = ui.MapMoveUIButtonCtrls;
                if (travel != null)
                {
                    for (int j = 0; j < travel.Count; j++)
                    {
                        var b = travel[j];
                        if (b == null) continue;
                        registered.Add(b.Pointer);
                        if (!seenTravel.Add(b.GotoMapID)) continue;

                        Add(b.transform, ui.gameObject, gotoMapId: b.GotoMapID, jobKind: int.MinValue);
                    }
                }

                var actions = ui.pcSoloActionUIButtonCtrls;
                if (actions != null)
                {
                    for (int j = 0; j < actions.Count; j++)
                    {
                        var b = actions[j];
                        if (b == null) continue;
                        registered.Add(b.Pointer);

                        int job = (int)b.Job;
                        if (!seenJob.Add(job)) continue;

                        Add(b.transform, ui.gameObject, gotoMapId: int.MinValue, jobKind: job);
                    }
                }
            }

            // Buttons placed in a location's UI but never registered with its MapMoveUI. The
            // game does not manage them, so they sit at their fixed spot forever -- such as the
            // "Coming Soon" label in the MapExpansion mod, whose button even points back at its
            // own map. They lead nowhere in the world, so they are hidden (parked) while the
            // buttons are on their markers, and put back afterwards like the rest.
            for (int i = 0; i < uis.Count; i++)
            {
                var ui = uis[i];
                if (ui == null || !ui.gameObject.active) continue;

                foreach (var b in ui.GetComponentsInChildren<MapMoveUIButtonCtrl>())
                    if (b != null && registered.Add(b.Pointer))
                        Add(b.transform, ui.gameObject, gotoMapId: int.MinValue, jobKind: int.MinValue);

                // An action button the game left out of its list still names its job; if
                // that job has a spot here it is tracked like the rest.
                foreach (var b in ui.GetComponentsInChildren<MapPCActionUIButtonCtrl>())
                    if (b != null && registered.Add(b.Pointer))
                        Add(b.transform, ui.gameObject, gotoMapId: int.MinValue,
                            jobKind: seenJob.Add((int)b.Job) ? (int)b.Job : int.MinValue);
            }

            if (_tracked.Count == 0)
            {
                // The UI for this location has not been built yet. Do not cache an empty
                // result, or nothing will ever track here again.
                _builtForMap = int.MinValue;
                return;
            }

            if (Notice.On) Describe(mapManager, mapId);

        }

        /// <summary>
        /// The game rebuilds the travel UI on its own, and switches between the Constant and
        /// Pop-ups sets when that option is changed; detect either and start over.
        /// </summary>
        private static bool AnyStale()
        {
            foreach (var t in _tracked)
            {
                if (t.Rect == null) return true;
                if (t.Owner == null || !t.Owner.active) return true;
            }
            return false;
        }

        private static void Add(Transform transform, GameObject owner, int gotoMapId, int jobKind)
        {
            var rect = transform.TryCast<RectTransform>();
            if (rect == null) return;

            var t = new Tracked
            {
                Rect = rect,
                Canvas = rect.GetComponentInParent<Canvas>(),
                OriginalPosition = rect.position,
                GotoMapId = gotoMapId,
                JobKind = jobKind,
                Owner = owner,
            };
            PopUp.Take(t);
            _tracked.Add(t);
        }

        /// <summary>
        /// Presents a Pop-ups style button the way a Constant one is drawn, for as long as it
        /// is tracked. Left as the game built it, each one misbehaved three ways at once:
        ///
        ///   - Its pivot is the hit area's top-left corner, and the label sits somewhere
        ///     inside that area, so pinning the button to a doorway put the label hundreds of
        ///     pixels away from it.
        ///   - The label only exists while hovered, so a label showed only when one of the
        ///     invisible areas, now sliding around the screen with the camera, passed under
        ///     the cursor: one at a time, and apparently gliding about.
        ///   - Those invisible areas caught clicks meant for the world.
        ///
        /// So while tracked: the label rather than the corner is what lands on the marker; the
        /// Animator is paused and we do its job instead, showing the label while the cursor
        /// is over the label's own spot rather than over the big area; and the label takes
        /// the clicks in place of the hit area. A click on a child still reaches the Button,
        /// since Unity hands a click to the nearest handler up the hierarchy.
        /// Everything is put back as it was by Give.
        /// </summary>
        private static class PopUp
        {
            internal static void Take(Tracked t)
            {
                var animator = t.Rect.GetComponent<Animator>();
                var frameTransform = t.Rect.Find("imgFrame");
                if (animator == null || frameTransform == null) return;

                var frame = frameTransform.TryCast<RectTransform>();
                if (frame == null) return;

                t.Frame = frame;
                t.Animator = animator;
                t.AnimatorWasEnabled = animator.enabled;
                t.FrameWasActive = frame.gameObject.activeSelf;

                t.HitImage = t.Rect.GetComponent<Image>();
                if (t.HitImage != null)
                {
                    t.HitWasRaycastTarget = t.HitImage.raycastTarget;
                    t.HitImage.raycastTarget = false;
                }

                t.FrameImage = frame.GetComponent<Image>();
                if (t.FrameImage != null)
                {
                    t.FrameWasRaycastTarget = t.FrameImage.raycastTarget;
                    t.FrameImage.raycastTarget = true;
                }

                Show(t, false);
            }

            /// <summary>
            /// Re-asserted each frame, cheaply, in case anything turns it back. The label is
            /// shown only while hovered, which is what the Pop-ups option means; the Animator
            /// that did this in the overview stays off because its hit area is the big one.
            /// </summary>
            internal static void Show(Tracked t, bool visible)
            {
                if (t.Frame == null) return;
                if (t.Animator != null && t.Animator.enabled) t.Animator.enabled = false;
                if (t.Frame.gameObject.activeSelf != visible) t.Frame.gameObject.SetActive(visible);
            }

            /// <summary>
            /// Whether the cursor is over the label's spot, visible or not. The area is the
            /// label widened by half its height all round: the overview hovers over a whole
            /// doorway, and an invisible 168x40 box would be needlessly hard to find. A hidden
            /// label still has a valid rect, since the parent was just placed.
            /// </summary>
            internal static bool Hovered(Tracked t, Camera cam)
            {
                if (t.Frame == null) return false;
                if (Cursor.lockState == CursorLockMode.Locked) return false;

                var canvas = t.Canvas;
                Camera canvasCam = null;
                if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    canvasCam = canvas.worldCamera != null ? canvas.worldCamera : cam;

                var r = t.Frame.rect;
                Vector2 a = RectTransformUtility.WorldToScreenPoint(
                    canvasCam, t.Frame.TransformPoint(new Vector3(r.xMin, r.yMin, 0f)));
                Vector2 b = RectTransformUtility.WorldToScreenPoint(
                    canvasCam, t.Frame.TransformPoint(new Vector3(r.xMax, r.yMax, 0f)));

                float minX = Mathf.Min(a.x, b.x), maxX = Mathf.Max(a.x, b.x);
                float minY = Mathf.Min(a.y, b.y), maxY = Mathf.Max(a.y, b.y);
                float margin = (maxY - minY) * 0.5f;

                var mouse = Input.mousePosition;
                return mouse.x >= minX - margin && mouse.x <= maxX + margin &&
                       mouse.y >= minY - margin && mouse.y <= maxY + margin;
            }

            internal static void Give(Tracked t)
            {
                if (t.Frame == null) return;

                if (t.HitImage != null) t.HitImage.raycastTarget = t.HitWasRaycastTarget;
                if (t.FrameImage != null) t.FrameImage.raycastTarget = t.FrameWasRaycastTarget;
                if (t.Frame.gameObject.activeSelf != t.FrameWasActive)
                    t.Frame.gameObject.SetActive(t.FrameWasActive);
                if (t.Animator != null && t.Animator.enabled != t.AnimatorWasEnabled)
                    t.Animator.enabled = t.AnimatorWasEnabled;
            }
        }

        private static void CollectTravelMarkers(int mapId)
        {
            _travelMarkers.Clear();

            var all = UnityEngine.Object.FindObjectsOfType<SVNodeLink2>();
            if (all == null) return;

            foreach (var link in all)
            {
                if (link == null || link.startMapID != mapId) continue;
                if (_travelMarkers.ContainsKey(link.endMapID)) continue;
                _travelMarkers[link.endMapID] = link.transform.position;
            }
        }

        /// <summary>
        /// Job markers for Work, Eat, Change Outfit and Pray. Same table SVS_3rdPov walks to
        /// decide what the interact key acts on, keyed by MovePointInfo.JobKind.
        /// </summary>
        private static void CollectJobMarkers(MapManager mapManager, int mapId)
        {
            _jobMarkers.Clear();
            _jobPointsMap = int.MinValue;      // read afresh
            foreach (var pair in JobPoints(mapManager, mapId))
                if (!_jobMarkers.ContainsKey(pair.Key))
                    _jobMarkers[pair.Key] = pair.Value.position;
        }

        /// <summary>The game switches activities on and off while you stand on a map; the
        /// markers follow (JobPoints is read at most once a second).</summary>
        private static void RefreshJobMarkers(int mapId)
        {
            var mapManager = SingletonInitializerAsync<MapManager>.Instance;
            if (mapManager == null) return;
            _jobMarkers.Clear();
            foreach (var pair in JobPoints(mapManager, mapId))
                if (pair.Value != null && !_jobMarkers.ContainsKey(pair.Key))
                    _jobMarkers[pair.Key] = pair.Value.position;
        }

        private static readonly List<KeyValuePair<int, Transform>> _jobPoints =
            new List<KeyValuePair<int, Transform>>();
        private static int _jobPointsMap = int.MinValue;
        private static float _jobPointsAt;

        /// <summary>
        /// Every spot on this map where the player can do an activity, as (job, point). Read
        /// again once a second at most.
        ///
        /// Only the activities whose button the game has switched on, so that third person
        /// offers exactly what the overview does: the map's point table lists every spot
        /// whether or not this character may use it, and going by the table alone let anyone
        /// Work at the cafe, the shrine or the beach by walking onto the spot.
        ///
        /// The spots are PointList.pcTable's. The classroom's Study is not in it: seats there
        /// belong to particular characters (JobDetail.ClassroomNo), so the player's is asked
        /// for by character index, PointList.GetClassRoomIDPoint.
        /// </summary>
        internal static List<KeyValuePair<int, Transform>> JobPoints(MapManager mapManager, int mapId)
        {
            float now = Time.unscaledTime;
            if (mapId == _jobPointsMap && now - _jobPointsAt < 1f) return _jobPoints;
            _jobPointsMap = mapId;
            _jobPointsAt = now;
            _jobPoints.Clear();

            try
            {
                var table = mapManager.pointInfoTable;
                MapCollisionCtrl.Info info = null;
                if (table == null || !table.TryGetValue(mapId, out info) || info == null) return _jobPoints;

                var pointList = info.pointList;
                if (pointList == null) return _jobPoints;

                // Only activities the game is offering right now: those whose button it has
                // switched on. Each has its own conditions (the cafe's Work needs that job),
                // and a spot without its button must not be usable by walking onto it.
                foreach (int job in ButtonJobs(mapManager))
                {
                    bool any = false;
                    PointList.ListInfo list = null;
                    if (pointList.pcTable != null && pointList.pcTable.TryGetValue(job, out list) &&
                        list?.points != null)
                    {
                        foreach (var point in list.points)
                        {
                            if (point == null) continue;
                            _jobPoints.Add(new KeyValuePair<int, Transform>(job, point.transform));
                            any = true;
                        }
                    }
                    if (any) continue;

                    var seat = SeatFor(pointList, job);
                    if (seat != null) _jobPoints.Add(new KeyValuePair<int, Transform>(job, seat));
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("Could not read the job marker table: " + e.Message);
            }
            return _jobPoints;
        }

        /// <summary>Debug Info: what was found for this location's buttons, in the log.</summary>
        private static void Describe(MapManager mapManager, int mapId)
        {
            try
            {
                var sb = new StringBuilder($"Location buttons on map {mapId}:");
                foreach (var t in _tracked)
                {
                    if (t.Rect == null) continue;
                    string kind = t.GotoMapId != int.MinValue ? "travel to " + t.GotoMapId
                                : t.JobKind != int.MinValue ? "job " + t.JobKind
                                : "unregistered";
                    bool has = TryGetMarker(t, out Vector3 world);
                    sb.Append($"\n  {t.Rect.name} ({kind}): active {t.Rect.gameObject.activeSelf}/" +
                              $"{t.Rect.gameObject.activeInHierarchy}, " +
                              (has ? "marker at " + world : "NO MARKER"));
                }

                MapCollisionCtrl.Info info = null;
                mapManager.pointInfoTable?.TryGetValue(mapId, out info);
                var pointList = info?.pointList;
                if (pointList == null) sb.Append("\n  no point list for this map");
                else
                {
                    var names = new[] { "urouro", "solo", "with", "everyone", "pc" };
                    var tables = new[] { pointList.urouroTable, pointList.soloTable, pointList.withTable,
                                         pointList.everyoneTable, pointList.pcTable };
                    for (int i = 0; i < tables.Length; i++)
                    {
                        sb.Append("\n  ").Append(names[i]).Append(" table:");
                        if (tables[i] == null) { sb.Append(" none"); continue; }
                        foreach (var pair in tables[i])
                            sb.Append($" job {pair.Key} x{pair.Value?.points?.Count ?? -1}");
                    }

                    var param = GameChara.PlayerAI?.charaData?.charasGameParam;
                    if (param != null)
                    {
                        sb.Append($"\n  player Index {param.Index}, ArrayIndex {param.ArrayIndex}");
                        foreach (int job in ButtonJobs(mapManager))
                        {
                            foreach (int index in new[] { param.Index, param.ArrayIndex })
                            {
                                string result;
                                try
                                {
                                    var seat = pointList.GetClassRoomIDPoint(job, index);
                                    result = seat != null ? seat.name + " at " + seat.transform.position : "null";
                                }
                                catch (Exception e) { result = "threw " + e.Message; }
                                sb.Append($"\n  GetClassRoomIDPoint(job {job}, {index}) = {result}");
                            }
                        }
                    }
                }
                Plugin.Logger.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("Could not describe the location buttons: " + e.Message);
            }
        }

        /// <summary>The jobs whose action button the game has switched on in the live travel UI.</summary>
        private static HashSet<int> ButtonJobs(MapManager mapManager)
        {
            var jobs = new HashSet<int>();
            var uis = mapManager._mapMoveUISummary?.MapMoveUIs;
            if (uis == null) return jobs;

            for (int i = 0; i < uis.Count; i++)
            {
                // activeSelf, not GameObject.active: that one answers for the whole
                // hierarchy, and in third person the travel UI's root is switched off except in
                // button mode -- which is how the classroom's Study went without a marker.
                var ui = uis[i];
                if (ui == null || !ui.gameObject.activeSelf) continue;
                foreach (var b in ui.GetComponentsInChildren<MapPCActionUIButtonCtrl>(true))
                    if (b != null && (int)b.Job >= 0 && b.gameObject.activeSelf) jobs.Add((int)b.Job);
            }
            return jobs;
        }

        /// <summary>The player's own seat for a job (the classroom), else the first spot any of
        /// the other tables has for it.</summary>
        private static Transform SeatFor(PointList pointList, int job)
        {
            try
            {
                var param = GameChara.PlayerAI?.charaData?.charasGameParam;
                if (param != null)
                {
                    var seat = pointList.GetClassRoomIDPoint(job, param.Index);
                    if (seat != null) return seat.transform;
                }
            }
            catch { }

            foreach (var table in new[] { pointList.soloTable, pointList.everyoneTable, pointList.withTable })
            {
                try
                {
                    PointList.ListInfo list = null;
                    if (table == null || !table.TryGetValue(job, out list) || list?.points == null) continue;
                    foreach (var point in list.points)
                        if (point != null) return point.transform;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// SVS_3rdPov hides the travel UI on every pass of its own Update postfix, not once.
        /// Ours runs after it, so re-asserting here each frame is what actually keeps the
        /// buttons on screen rather than flashing for a single frame.
        /// </summary>
        private static void KeepUiVisible()
        {
            for (int i = 0; i < _uiRoots.Count; i++)
            {
                var go = _uiRoots[i].Key;
                if (go != null && !go.active) go.active = true;
            }
        }

        private static void ForceActive(GameObject go)
        {
            if (go == null) return;

            foreach (var existing in _uiRoots)
                if (existing.Key == go) return;

            _uiRoots.Add(new KeyValuePair<GameObject, bool>(go, go.active));
            if (!go.active) go.active = true;
        }

        // ---------------------------------------------------------------- placement

        private static void Position(Camera cam)
        {
            bool clamp = Plugin.MapButtonTracking.Value == MapButtonMode.ClampToScreenEdge;

            foreach (var t in _tracked)
            {
                if (t.Rect == null) continue;

                if (!TryGetMarker(t, out Vector3 world))
                {
                    Park(t);
                    continue;
                }

                world.y += Plugin.MapButtonHeight.Value;
                var screen = cam.WorldToScreenPoint(world);
                bool behind = screen.z <= 0f;
                bool offScreen = behind ||
                                 screen.x < 0f || screen.x > Screen.width ||
                                 screen.y < 0f || screen.y > Screen.height;

                if (offScreen && !clamp)
                {
                    Park(t);
                    continue;
                }

                if (offScreen)
                {
                    // Behind the camera the projection mirrors through the centre, so flip it
                    // back before clamping or the button points the wrong way.
                    if (behind)
                    {
                        screen.x = Screen.width - screen.x;
                        screen.y = Screen.height - screen.y;
                    }
                    screen.x = Mathf.Clamp(screen.x, 0f, Screen.width);
                    screen.y = Mathf.Clamp(screen.y, 0f, Screen.height);
                }

                Place(t, screen, cam);
                PopUp.Show(t, PopUp.Hovered(t, cam));
            }
        }

        private static bool TryGetMarker(Tracked t, out Vector3 world)
        {
            if (t.GotoMapId != int.MinValue)
                return _travelMarkers.TryGetValue(t.GotoMapId, out world);

            if (t.JobKind != int.MinValue)
                return _jobMarkers.TryGetValue(t.JobKind, out world);

            world = default;
            return false;
        }

        private static void Park(Tracked t)
        {
            PopUp.Show(t, false);
            t.Rect.position = Parked;
            t.Moved = true;
        }

        /// <summary>
        /// On a Screen Space Overlay canvas a RectTransform's world position is measured in
        /// screen pixels, so the projected point is assigned straight across. Any other render
        /// mode has to be converted through the canvas camera first.
        /// </summary>
        private static void Place(Tracked t, Vector3 screen, Camera cam)
        {
            var canvas = t.Canvas;
            if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            {
                screen.z = 0f;
                MoveVisiblePartTo(t, screen);
                return;
            }

            var parent = t.Rect.parent != null ? t.Rect.parent.TryCast<RectTransform>() : null;
            var canvasCam = canvas.worldCamera != null ? canvas.worldCamera : cam;

            if (parent != null &&
                RectTransformUtility.ScreenPointToWorldPointInRectangle(
                    parent, new Vector2(screen.x, screen.y), canvasCam, out Vector3 world))
            {
                MoveVisiblePartTo(t, world);
            }
        }

        /// <summary>
        /// Puts what the player actually sees on the target. For a Constant button that is
        /// the button itself. For a Pop-ups one it is the label, offset inside a much larger
        /// hit area whose pivot is a corner, so the button is moved by whatever it takes to
        /// bring the label there. Children follow a moved parent immediately, so the label's
        /// position can be read straight back.
        /// </summary>
        private static void MoveVisiblePartTo(Tracked t, Vector3 target)
        {
            t.Rect.position = target;
            if (t.Frame != null)
                t.Rect.position += target - t.Frame.position;
            t.Moved = true;
        }

        // ---------------------------------------------------------------- teardown

        private static void RestoreAll()
        {
            foreach (var t in _tracked)
            {
                try
                {
                    if (t.Rect != null && t.Moved) t.Rect.position = t.OriginalPosition;
                }
                catch { }

                try
                {
                    PopUp.Give(t);
                }
                catch { }
            }
            _tracked.Clear();

            // Put each root back to exactly what it was. Forcing them inactive instead is
            // what stopped the buttons appearing with the overview camera, where the game
            // wants them visible and had them active all along.
            for (int i = _uiRoots.Count - 1; i >= 0; i--)
            {
                try
                {
                    var entry = _uiRoots[i];
                    if (entry.Key != null && entry.Key.active != entry.Value)
                        entry.Key.active = entry.Value;
                }
                catch { }
            }
            _uiRoots.Clear();

            _travelMarkers.Clear();
            _jobMarkers.Clear();
            _active = false;
            _builtForMap = int.MinValue;
        }

    }
}
