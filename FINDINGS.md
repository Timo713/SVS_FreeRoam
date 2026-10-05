# FINDINGS

Reference for **SVS_FreeRoam** — until 2026-10-02 called SVS_3rdPovPlus (project folder
`3rdPOV`, namespace `SVS_3rdPOV`); older sections use those names. The character
lighting work recorded in §24 has moved to its own plugin, D:\Code\SVS_CustomLights,
and its code is no longer here. §25 is SVS_WalkAnywhere's findings, merged in with its
code. Sections 1–3 describe Junh2x's original plugin, which is
now merged in — they remain the best explanation of *why* the current code is
shaped as it is, so read them as history rather than as a description of a separate
DLL. Sources and their limits:

- **dnSpy on SVS_3rdPov.dll** — real .NET, full method bodies. Everything in
  §1–§3 is read directly from decompiled source and is reliable.
- **dnSpy MCP on Assembly-CSharp** — the game is IL2CPP, so the interop
  assembly carries signatures, fields and enums but **empty method bodies**.
  §4 is built from names and shapes only. Marked UNVERIFIED until tested live.
- **The live .cfg file** — ground truth for what is actually bound right now.

---

## 1. What SVS_3rdPov is

`[BepInPlugin("SVS_3rdPov", "SVS_3rdPov", "0.0.6")]`, a BepInEx 6 IL2CPP
`BasePlugin`. Assembly version 0.0.6.0. Three types only:

- `SVS_3rdPov.MyPluginInfo`
- `SVS_3rdPov.Plugin` — config binding, `Load()`, `Unload()`
- `SVS_3rdPov.Plugin/Hooks` — `internal static`, holds all three patches and
  all camera state

`Load()` binds config, then
`patchedHooks = Harmony.CreateAndPatchAll(typeof(Plugin.Hooks))`, gated on the
`General/Enable` bool. `Unload()` calls `UnpatchSelf()`.

### Its three patches

| Target | Kind | Body |
|---|---|---|
| `SimulationScene.Update` | Postfix | The entire plugin, ~380 lines |
| `SimulationScene.WheelTargetSelect` | Prefix | `return !isPovRunning;` |
| `SimulationScene.MouseMiddleClick` | Prefix | `return !isPovRunning;` |

`SimulationScene.CursorTargetSelect()` also exists on the game type and is
**not** patched. It is the remaining un-suppressed click handler and is the
prime suspect for left-click target selection during POV. Confirm live.

---

## 2. Its config, and what is actually bound

Defaults are stored as raw ints in the decompiled source; translated to
`UnityEngine.KeyCode` names below. The right-hand column is what the live
`SVS_3rdPov.cfg` in the test install currently holds.

| Section / Key | Field | Default | Live value |
|---|---|---|---|
| Hotkeys / PoV Toggle Key | `ToggleKey` | 285 = `F4` | **`Mouse4`** |
| Hotkeys / Interact Key | `InteractKey` | 324 = `Mouse1` | `Mouse1` |
| Hotkeys / Mouse Mode Key | `MouseKey` | 308 = `LeftAlt` | `LeftAlt` |
| Hotkeys / Running Key | `RunningKey` | 304 = `LeftShift` | `LeftShift` |
| General / Enable | (local) | `true` | `true` |
| General / Always Running | `AlwaysRunning` | `true` | `true` |
| General / Hide Blur | `HideBlur` | `true` | **`false`** |
| General / Walking Speed | `WalkingSpeed` | `1` (0–3) | `1` |
| General / Running Factor | `RunningFactor` | `2` (1–3) | `2` |
| General / Stamina Gain | `PhysicalGain` | `1` (0–2) | `1` |

**Interact is on `Mouse1`, the RIGHT button — not the left.** Unity's
`KeyCode.Mouse0` is left, `Mouse1` is right, `Mouse2` is middle. So left click
is currently unused *by SVS_3rdPov*. The collision we have to solve is with the
**game's** left click, not with this plugin's interact binding.

Six `ConfigEntry` members are `internal static` **properties** on
`SVS_3rdPov.Plugin` (`HideBlur`, `AlwaysRunning`, `ToggleKey`, `RunningKey`,
`InteractKey`, `MouseKey`); `WalkingSpeed`, `RunningFactor` and `PhysicalGain`
are `internal static` **fields**. `internal` means reflection reaches all of
them from our assembly. This is what makes the companion-plugin approach work.

---

## 3. How the one big postfix is structured

`Hooks.MapManagerUpdate(SimulationScene __instance)`. Reading order matters —
the branch you are in decides which inputs are even polled.

```
povActive ^= Input.GetKeyDown(ToggleKey)          // toggle, every frame

if (!povActive || playerAI == null || 2D map)     // POV OFF
      isPovRunning = false; cursor unlocked; map UI shown
else                                              // POV ON
      isPovRunning = true; map UI hidden; fieldOfView = 50
      uiOpen = IsADV || IsHScene || MyRoom || MapSelect
             || CharaSelect || CoordeSelect || CorrelationDiagram
      if (Scene.IsOverlap || uiOpen || GetKey(MouseKey))   // ---- UI BRANCH ----
            stop the player, unlock cursor
            if (IsADV)  camera snaps to playerAI.HeadPos / .rotation
            else if HScene  camera follows HScene.MainCamera
      else                                                // ---- CONTROL BRANCH ----
            Cursor.lockState = Locked
            pick nearest of: SVNodeLink2 / MovePointInfo / AI  (60 deg cone, < 1m)
            if (Input.GetKeyDown(InteractKey))  interact with whatever was picked
            move   = Input.GetAxis("Horizontal"/"Vertical")
            run    = AlwaysRunning ^ Input.GetKey(RunningKey)
            speed  = WalkingSpeed + NowStamina * PhysicalGain / 1000
            camera = orbit at pitch/yaw, distance 1-5 (scroll), height 1.2
```

Consequences that shape our design:

- **Interact and run are only polled in the control branch.** During ADV the
  code never reads `InteractKey` or `RunningKey` at all. So SVS_3rdPov is not
  what advances dialogue — the game is.
- `run = AlwaysRunning ^ GetKey(RunningKey)`. With the live config
  (`AlwaysRunning = true`, `RunningKey = LeftShift`) the player runs by default
  and **holds shift to walk**. To get "hold left click to run" the pair must
  become `AlwaysRunning = false`, `RunningKey = Mouse0`.
- Movement, interaction and camera all live in this one method, so without
  transpilers (banned under IL2CPP) we cannot alter the logic between those
  steps. We can only change the values it reads, and patch the game around it.
- State we may want to read by reflection, all `private static` on
  `Plugin/Hooks`: `povActive`, `isPovRunning`, `handling`, `running`, `yaw`,
  `pitch`.
- `sensitivity` (1.0), `distance` (3.0), `height` (1.2) are `public static`
  **fields with no config entry** — hard-coded. Exposing them as real config
  options is nearly free, since we can just write the statics.
- Pitch is clamped to -15..65, distance to 1..5. Also un-configurable.

---

## 4. The ADV advance click — CONFIRMED

This is the open question in CameraPlus's FINDINGS §6 ("left click still
advances the dialogue even with `IsInputLock` held"). The game has a dedicated,
named mechanism for exactly this, and `IsInputLock` appears to be simply the
wrong flag.

`ADV.TextScenario` exposes `public ADV.Regulate get_Regulate()`.
`ADV.Regulate` has:

```
public void AddRegulate(Regulate.Controls regulate)
public void SubRegulate(Regulate.Controls regulate)
public void SetRegulate(Regulate.Controls regulate)
public Regulate.Controls Control { get; set; }
```

`ADV.Regulate/Controls` is an `Int32` bit-flags enum:

| Name | Value |
|---|---|
| `Next` | 1 |
| `ClickNext` | **2** |
| `Skip` | 4 |
| `Auto` | 8 |
| `AutoForce` | 16 |
| `Log` | 32 |

"Regulate" here reads as *restrict*: `Add` imposes a restriction, `Sub` lifts
it. `ClickNext` being its own flag, separate from `Next`, is exactly the
distinction needed — block advancing **by click** while leaving other advance
paths alone.

Confirmed usage, measured in game:

```csharp
scenario.Regulate.AddRegulate(ADV.Regulate.Controls.ClickNext);   // block
scenario.Regulate.SubRegulate(ADV.Regulate.Controls.ClickNext);   // restore
```

### Corroborating evidence

`ADV.Commands.Base.Regulate` is a **scenario command** — one of the instructions
an ADV script file can contain — and its argument enum is:

```
ADV.Commands.Base.Regulate/Type :  Set = 0,  Add = 1,  Sub = 2
```

which maps one-to-one onto `SetRegulate` / `AddRegulate` / `SubRegulate`. So this
is not an incidental API: it is the game's own scripted mechanism for locking and
unlocking player input mid-conversation, already used by the shipped scenarios.
That is exactly the behaviour we want to borrow, it is per-scenario, and it is
designed to be reversible.

The metadata string dump at `D:\Code\_reference\v1.1.6.dat` independently
confirms the member names `AddRegulate SubRegulate SetRegulate Control` and
`get_Regulate`, alongside `_isInputLock` — they are separate things.

### What was actually measured (2026-09-02)

Tested from the RuntimeUnityEditor console during a normal conversation, with
the free camera off. The console needs the scenario fetched first - `scenario`
is not a name it knows:

```csharp
var scenario = UnityEngine.Object.FindObjectOfType<ADV.ADVScene>().Scenario;
scenario.Regulate.Control
```

- `Control` reads **0** during normal dialogue. So the set lists what is
  **blocked**, not what is permitted: `Add` restricts, `Sub` restores, exactly
  as the naming suggested. No inversion.
- `AddRegulate(ClickNext)` stopped clicking from advancing **and** stopped the
  mouse wheel advancing. `SubRegulate(ClickNext)` restored both.
- **Choice buttons stay clickable** with the flag set, so choices need no
  special case.
- Setting `Control` directly to `Controls.ClickNext` does the same thing.
- `UpdateRegulate()` does **not** wipe it. `Control` still read `ClickNext`
  seconds after the call, across separate console submissions - so the
  per-frame-recompute worry was unfounded and a one-shot is enough. Writing it
  every frame anyway is free, and covers the scenario object being swapped.

SVS_ADVFreeCamera v1.0.1 ships this as `ApplyClickBlock` / `ReleaseClickBlock`,
which only lifts the flag from the exact `TextScenario` it set it on, and never
touches a scenario that was already blocking clicks by itself.

Related, also unread: `TextScenario.MessageWindowProc(NextInfo)` returns bool,
and `NextInfo` carries `IsNext`, `IsSkip`, `IsCompleteDisplayText`. If
`Regulate` turns out not to be the gate, a prefix on `MessageWindowProc`
forcing `IsNext = false` is the fallback. `ADV.ADVButton` was checked and is
**not** relevant — it is the skip/auto/backlog/voice/config/close button bar.

---

## 5. Harmony behaviour on this game

CameraPlus FINDINGS §2 records that "a Harmony prefix returning false does not
skip the original" — but that was measured on `MainScenario.Update`, a Unity
lifecycle message. SVS_3rdPov ships two prefixes that return `false` to suppress
`SimulationScene.WheelTargetSelect` and `SimulationScene.MouseMiddleClick`.

Working assumption: **prefix-skip works on ordinary game methods and fails on
Unity messages** (`Update`, `LateUpdate`, `Awake`, ...), which the IL2CPP
runtime invokes through a path Harmony's return value does not gate. Useful
either way — it means `CursorTargetSelect` should be suppressible the same way.
Still worth confirming that SVS_3rdPov's two prefixes genuinely take effect
rather than being dead code nobody noticed.

---

## 6. Design decision — companion plugin, not a fork

Chosen: a separate plugin that leaves SVS_3rdPov.dll untouched. Rebuilding from
decompiled source was rejected — there is no upstream repo and no license, so
redistributing it is not ours to do, and every future update of theirs would
have to be re-decompiled and re-merged by hand.

It is also technically sufficient, because the three things we need are all
reachable from outside:

1. **Run on left click** — set their own `AlwaysRunning = false` and
   `RunningKey = Mouse0`. Their unmodified code then does exactly what we want.
   No patching of their assembly involved at all.
2. **Interact stays rebindable** — their `InteractKey` is already a config
   entry. We add our own entry that *proposes* a value rather than overwriting
   silently, defaulting to leaving `Mouse1` exactly where it is.
3. **Conflict resolution** — the part config cannot do, and the real work:
   suppress `CursorTargetSelect` while POV is active, and stop a left click
   held for running from advancing dialogue the instant an ADV opens (§4).

Load ordering is handled with
`[BepInDependency("SVS_3rdPov", DependencyFlags.SoftDependency)]`, and their
internals are reached with Harmony `AccessTools` at runtime rather than a
compile-time reference, so a missing or updated SVS_3rdPov degrades instead of
breaking our load.

Known cost, to be disclosed in the README: writing to their `ConfigEntry.Value`
causes BepInEx to rewrite `SVS_3rdPov.cfg` on disk. It must sit behind an
explicit, default-off option.

---

## 7. How "move forward with the mouse" is actually implemented

The goal is not a run modifier — it is **borrowing W**. SVS_3rdPov reads movement
from Unity's legacy axes (§3):

```csharp
new Vector3(Input.GetAxis("Horizontal"), 0f, Input.GetAxis("Vertical"))
```

`"Vertical"` is the axis W and S are bound to. So instead of trying to rewrite
SVS_3rdPov's movement code — impossible without a transpiler — we answer that
question differently:

```csharp
[HarmonyPostfix]
[HarmonyPatch(typeof(Input), nameof(Input.GetAxis))]
static void GetAxisPostfix(string axisName, ref float __result)
```

While third-person mode is on and the chosen mouse button is held, `"Vertical"`
returns 1, and SVS_3rdPov's own untouched code walks the player forward exactly
as if W were held. Speed, animation, stamina and the run/walk state all keep
working, because we never touched them.

`UnityEngine.Input.GetAxis(System.String)` is `public static`, returns
`System.Single`, and lives in `UnityEngine.InputLegacyModule.dll` in the interop
folder — confirmed patchable. A **postfix** editing `ref __result` is used
deliberately, so this does not depend on prefix-skip working (§5).

The game's own left-click target select is suppressed with a prefix on
`SimulationScene.CursorTargetSelect()` returning `!isPovRunning`, the same shape
SVS_3rdPov already uses for `WheelTargetSelect` and `MouseMiddleClick`.

### Changing their interact binding without touching their file

Assigning to a `ConfigEntry.Value` normally makes BepInEx rewrite that plugin's
`.cfg`. To avoid editing someone else's config behind their back,
`ConfigEntry.ConfigFile.SaveOnConfigSet` is set to `false` around the
assignment. The change is in-memory only; `SVS_3rdPov.cfg` on disk is untouched
and a restart restores the user's own value. This removed the need for the
"allow config writes" opt-in that §6 anticipated.

---

## 8. The map travel buttons, and making them track world markers

Chain, all confirmed from metadata:

```
Manager.MapManager
  .objMapMoveUI          GameObject   <- the thing SVS_3rdPov just hides wholesale
  .objMapMoveUICanvas    GameObject
  ._mapMoveUISummary     SV.MapMoveUISummary
       .MapMoveUIs       List<SV.MapMoveUI>
            .MapMoveUIButtonCtrls      List<SV.MapMoveUIButtonCtrl>   <- travel buttons
            .pcSoloActionUIButtonCtrls List<SV.MapPCActionUIButtonCtrl> <- job buttons
```

`SV.MapMoveUIButtonCtrl` is a MonoBehaviour carrying:

| Member | Type | Use |
|---|---|---|
| `GotoMapID` / `_gotoMapID` | `int` | **the destination map** |
| `_btn` | `UnityEngine.UI.Button` | the clickable |
| `_text` | `TextMeshProUGUI` | its label |
| `ActMoveMap` | `Action<int>` | what it invokes |

And the world-side marker, `SV.SVNodeLink2`, carries `startMapID` and
`endMapID` plus its own `transform`.

**So the pairing is direct: `button.GotoMapID == link.endMapID`.** Position a
button each frame with
`Camera.main.WorldToScreenPoint(link.transform.position)` and it tracks its
real doorway instead of sitting at a fixed screen spot. SVS_3rdPov already
gathers the links itself via
`info.transformParent.GetComponentsInChildren<SVNodeLink2>()` (§3), filtered to
`startMapID == MapID`, so the same collection is available to us.

Job markers pair the same way through `MovePointInfo.JobKind` and
`MapPCActionUIButtonCtrl`.

**Confirmed in game (RUE, Station):** the button objects are named
`btnGoto<NNN>` where NNN is the **destination map id** —
`btnGoto001` = Cafe (map 1), `btnGoto002` = Beach (map 2), `btnGoto003` = School
Gate (map 3), `btnGoto005` = Roadside (map 5), `btnGoto139` = Asagaya Station
(map 139). So the object name and `GotoMapID` agree, and **no per-map table is
needed** — the pairing generalises to any map, custom ones included.

`btnGotoPCAction` is a different type: `SV.MapPCActionUIButtonCtrl`, carrying
`Job` (`MovePointInfo.JobKind`) and `NowMapID` rather than a destination map.
Its markers come from `MapCollisionCtrl.Info.pointList.pcTable`, which is keyed by
JobKind and only reachable through the MapManager singleton, so job buttons are
**not** handled yet.

Two traps that broke the first implementation:

1. `FindObjectsOfType` **cannot see inactive objects**, and SVS_3rdPov disables the
   whole travel UI while third-person is on — so it found nothing at all.
   `Resources.FindObjectsOfTypeAll` is used instead, filtered to objects with a
   valid `scene` so prefab assets are excluded. Note the generic overload is not
   exposed through IL2CPP interop: it must be called as
   `Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>())` with each result cast back.
2. Reactivating a button is not enough — **every inactive ancestor** up to the
   canvas has to be switched on too, and remembered so it can be put back.

### Four bugs from the first working version, and their causes

1. **Blinking every few seconds.** The list was rebuilt every 120 frames, and each
   rebuild restored every button to its original place before moving it again.
   Now built once per location and never rebuilt while standing on it.
2. **Duplicate buttons.** `Resources.FindObjectsOfTypeAll` returns *stale* travel-UI
   trees left over from previous locations, and force-activating their ancestors
   made those old copies visible. Fixed by reaching the live UI through
   `SingletonInitializerAsync<MapManager>.Instance._mapMoveUISummary` instead of
   searching the scene.
3. **Event-gated destinations appearing** (Love Hotel, Work). The tracker was
   setting buttons active. The game switches individual buttons on and off to
   reflect availability, so **never touch a button's active flag** — a button with
   nowhere to point is parked far off screen instead.
4. **The Work button never tracked.** It is `btnGotoPCAction`, a
   `MapPCActionUIButtonCtrl`, not a travel button at all — see above. Expected.

### Reaching MapManager

`Manager.MapManager` derives from `ILLGames.Unity.Component.SingletonInitializerAsync<T>`,
which exposes a static `Instance`. The type search only finds it as
`SingletonInitializerAsync\`1` — the backtick is why a plain name search misses it.
This gives `objMapMoveUI`, `objMapMoveUICanvas` and `_mapMoveUISummary` directly,
which is the authoritative live UI rather than whatever a scene search turns up.

### Button mode: borrow SVS_3rdPov's Mouse Mode branch, don't rebuild it

To click a travel button you need a cursor and a character that stands still.
SVS_3rdPov already has exactly that branch — the one entered while its Mouse Mode
key is held, which unlocks the cursor, stops the player and hides the chara info.

So the latched "buttons on" state does not reimplement any of that. It makes
`Input.GetKey(MouseKey)` **read as held** (Hooks.GetKeyPostfix), and SVS_3rdPov's
own code does the rest. `ViewMode.RawKeyHeld` sets `Hooks.SuppressAliasing` to read
the real key state, otherwise the latched state and a genuinely held key could not
be told apart.

The toggle key is **swallowed** in this mode (`SwallowToggleIfOurs`): its press is
turned into "show or hide the buttons" and `__result` set false, so SVS_3rdPov
never sees it and `povActive` never flips. An earlier version let it flip and
corrected it in the postfix — but SVS_3rdPov branches on `povActive` in the same
pass it flips it, so for one frame it took the POV-OFF branch and set
`objMapMoveUI.active = true`, showing the buttons at their fixed screen positions.
That one frame is the blink seen on the first toggle. **Correcting a flag after the
owner has already branched on it is always one frame too late.**

### In button mode, nobody drives the camera unless we do

Borrowing SVS_3rdPov's Mouse Mode branch gets the free cursor and the stopped
character for nothing, but that branch **does not touch the camera at all** — it
only repositions it for ADV and H scenes. Our own camera pass was gated on
`_controlling`, which button mode deliberately clears by unlocking the cursor.

So while latched, *nothing* positioned the camera. On the same location that went
unnoticed, because the last third-person pose simply stayed. After changing
location the game places its own camera, and with nobody overriding it the result
was the overview view — even with the overview camera switched off. Clicking a
travel button reproduced it; walking to the marker did not, because walking never
enters button mode.

The too-wide view was the same cause seen from the other side: SVS_3rdPov forces
`fieldOfView = 50` on every pass regardless of branch, while each location's own
overview camera uses anything from 26 to 55 (§10). Correct position, wrong FOV.

Fixed by driving the camera whenever `povRunning && (controlling || buttonMode)`,
with mouse look and zoom read only when actually controlling — in button mode the
cursor is free for clicking, so moving it must not spin the camera.

### Restore UI roots to what they were, not to "off"

`objMapMoveUI` must be re-asserted active every frame while tracking, because
SVS_3rdPov disables it on every pass of its own postfix, not once. But the restore
has to put back the **original** state, not force it inactive: with the overview
camera in play the game wants that object active, and blindly disabling it on
teardown is what stopped the buttons appearing in the Overview Camera Restore On
mode. Each root is now stored with the active state it had when first seen.

### The three-view cycle

`ViewMode` translates SVS_3rdPov's single `povActive` flag into three states:
`ThirdPerson`, `ThirdPersonWithButtons`, `Overview`. It does not intercept the
toggle key — it watches `povActive` for a change, decides what that press should
have meant, and writes the flag back when the answer differs. With Map Button
Tracking off there are two states and behaviour is unchanged.

Still open: whether the buttons sit
under a Screen Space Overlay or Camera canvas (decides whether
`WorldToScreenPoint` is the right transform or whether a canvas-space
conversion is needed), what to do when a marker is behind the camera or
off-screen (hide, or clamp to the edge), and whether `MapMoveUI.Reset()` or
`ActiveDraw()` re-lays them out and would fight per-frame positioning. All of
that is quicker to answer in RuntimeUnityEditor than from metadata.

---

## 9. Bugs reported in testing, and what actually caused them

All found by the developer playing v0.2.0. Causes below are diagnosed, fixes are
in v0.3.0 and **not yet re-tested**.

**"Start With PoV Active" only worked after a restart, and started in PoV anyway.**
It was applied once from `Load()`. SVS_3rdPov's `povActive` static initialises to
`true`, and at BepInEx plugin-load time its statics are not reliably in place, so
the write either landed too early or on a not-yet-initialised class. Now applied
on the first `Update` frame where the bridge resolves, plus immediately on
`SettingChanged`, so the option doubles as a live on/off switch.

**The cursor never came back, on any map, until a restart.**
The original code only ever *hid* it. It never restored it. This is the whole
bug, and it explains all three symptoms at once — hidden on 2D maps, hidden after
map changes, and not returning when PoV was switched off.

Worth understanding why SVS_3rdPov itself never had this problem: it never
touches `Cursor.visible` at all. Unity hides the cursor for free while
`lockState == Locked` and shows it again on unlock, so their plugin got the
restore automatically. Setting `visible = false` explicitly is what broke that.
The fix is symmetric: hide, remember that we hid it, restore the moment we stop
controlling. **No 2D-map special case is needed** — on 2D maps SVS_3rdPov sets
`isPovRunning = false` and unlocks the cursor, so the restore path already covers
them. SVS_MapLoader was not needed either.

**Turning PoV off after changing map put the ordinary camera nowhere.**
The saved pose was from the *previous* map, so restoring it aimed the camera at
coordinates that mean nothing on the new one. Now the map id is saved alongside
the pose (`playerAI.BehaviourCtrl.NowMapID`) and a pose from a different map is
discarded rather than used.

**Camera collision flickered violently.**
Three compounding causes:
1. The ray hit the **player's own colliders** — body, hair, clothes, accessories.
   The camera got yanked to almost zero, which put it inside the character, which
   changed what the next cast hit. That oscillation is the blinking, and it
   explains why it was worse on some maps than others and why it ignored Min Zoom.
2. It hit **trigger volumes**. Maps are full of invisible triggers the camera
   should pass through; treating them as walls causes flicker in open space.
3. It snapped to the raw result every frame with no easing.

Now: `SphereCastAll` skipping anything parented to the player, triggers ignored
via `QueryTriggerInteraction.Ignore`, zero-distance hits discarded (a cast
starting inside a collider reports distance 0 and a meaningless normal),
exponential smoothing, and the result clamped to Min Zoom so it can never end up
inside the character. The requested per-category switches are exposed under
`[Camera Collision]`, and the game's real physics layer names are written to
LogOutput.log at startup, since layer names are not in assembly metadata.

**The view curved downward and flipped when zoomed in.**
This one is SVS_3rdPov's own framing formula, not the pitch clamp:

```csharp
main.transform.position = playerAI.transform.position + rot * new Vector3(0, height, -distance);
main.transform.LookAt(playerAI.transform.position + Vector3.up * height);
```

It orbits the player's **feet** while rotating an offset that already contains
`height`. So the real elevation angle is not the pitch value, and as `distance`
shrinks the `height` term dominates until the effective angle passes 90 degrees
and `LookAt` inverts. Fixed by orbiting the **look target** instead —
`lookTarget + rot * (0, 0, -distance)` — which makes pitch mean exactly what it
says at any distance. A hard internal limit of 89.5 degrees stops the flip; the
config range is deliberately wider so values past 90 can still be tried, they
just do not bend the camera over.

---

## 10. Where the vanilla map camera pose lives (and how we get it)

Searched and **not** found in metadata:

- `SV.MapInfoParam` has 23 properties — name, IDs, bundle paths, light settings —
  and **no camera position or rotation**.
- `SV.MapCollisionCtrl/Info` has only `id`, `transformParent`, `pointList`.
- There is no `MapCamera` / `CameraPoint` / `CameraAnchor` type in the `SV`
  namespace at all.
- `MapManager.ChangeMap(int, Camera, int, bool, bool, Fade, bool)` and
  `LoadMapObject(...)` both *take* a `Camera`, but their bodies are stubs.
  SVS_3rdPov passes `null` to `ChangeMap`.

Conclusion: **the overview camera pose is baked into each map's asset bundle**
and applied while the map loads. It is not reachable statically, which matches
the developer's guess that RUE is the place to find it.

So we do not look it up — we **observe** it. `CameraTweaks.PreUpdate()` is a
`Priority.First` **prefix** on `SimulationScene.Update`, which runs before
SVS_3rdPov's postfix. On the first frame a new map id appears
(`playerAI.BehaviourCtrl.NowMapID`), the camera still holds whatever the map load
put there, so we capture it then and only then.

This is the fix for the reported bug. The previous version saved the pose
"whenever third-person is off", which meant that after changing map *with*
third-person on, the first thing it saved was SVS_3rdPov's own camera — and it
then faithfully restored that wrong pose every time. Capturing strictly on map
arrival cannot pick up a SVS_3rdPov pose, because SVS_3rdPov has not run yet on
that frame.

`Disable PoV When Changing Location` (default on) backs this up: with
third-person off during the transition, the game places its own camera normally
and the captured pose is unambiguous.

---

### Camera restore was removed in v0.5.0

Both attempts made things worse and the feature is gone. Leaving third-person now
moves the camera not at all, which is what plain SVS_3rdPov did.

- **v0.3.0** saved the pose "whenever third-person is off". After changing map
  *with* third-person on, the first thing it saved was SVS_3rdPov's own camera,
  and it restored that wrong pose forever after.
- **v0.4.0** captured on the first `Update` of a new map instead. Still wrong, and
  it *regressed* the one case that used to work (change map with third-person off,
  then toggle on and off). So the game does **not** have the overview camera in
  place by the first `SimulationScene.Update` of a new map — the pose captured
  there is stale or belongs to something else.

That last point is the useful finding: whatever sets the overview camera runs
**later than the first Update after arrival**, or does not touch `Camera.main` at
all.

`Disable PoV When Changing Location` is kept, and is now the whole answer: with
third-person off during the transition the game places its own camera normally.

### SOLVED in v0.6.0 — the map id changes long before the camera moves

The `[Debug] Log Camera On Map Change` output settled it. Two facts:

**1. `NowMapID` changes 17–72 frames before the game places the camera.** The pose
readable on arrival is always the *previous* map's. Measured delays: map 256 at
f+17, map 258 at f+18, map 4 at f+27, map 6 at f+32, map 0 at f+34/35, map 3 at
f+38/39, map 5 at f+44, map 1 at f+52, map 2 at f+72. This is the whole reason
v0.4.0 failed, and it varies per map, so no fixed delay would be safe.

**2. The pose is deterministic per map.** Every map revisited produced byte-identical
values — map 0 settled at `(16.60, 3.40, 69.52)` fov 50 on both visits, map 3 at
`(-12.06, 2.59, 161.14)` fov 34 on both.

Observed overview poses (vanilla + two custom maps):

| Map | Name | Position | Rotation | FOV |
|---|---|---|---|---|
| 0 | Station | (16.60, 3.40, 69.52) | (6.02, 241.44, 0) | 50 |
| 1 | Cafe | (50.28, 2.42, -2.84) | (16.35, 139.98, 0) | 55 |
| 2 | Beach | (110.09, 2.83, 67.59) | (11.28, 177.10, 359.97) | 35 |
| 3 | School Gate | (-12.06, 2.59, 161.14) | (4.28, 151.60, 0) | 34 |
| 4 | Classroom | (-2.57, 1.68, 187.09) | (8.11, 336.01, 359.76) | 55 |
| 5 | Roadside | (-88.30, 1.43, 90.72) | (5.75, 179.95, 0.04) | 35 |
| 6 | Shrine | (-147.11, 3.49, 71.41) | (8.18, 218.25, 0.29) | 35 |
| 256 | Forest (custom) | (-164.19, 5.20, 89.54) | (9.76, 21.59, 0) | 26.2 |
| 258 | Camping (custom) | (-80.70, 2.40, 103.78) | (4.00, 340.32, 0) | 31.3 |

These are **not hard-coded** — custom maps have their own, so v0.6.0 learns them at
runtime instead: while third-person is off, watch `Camera.main` and record the pose
once it has held still for 6 frames. Guarded by `_povUsedSinceMapChange`, so a pose
is never learned from a frame where SVS_3rdPov was driving — which is what made
v0.3.0 memorise a wrong position permanently.

Also ruled out: `SimulationScene.mainCamera` is the **same object** as `Camera.main`
('Main Camera' under 'SimulationScene') on every map, so the ADV-style split
between a background camera and a real one does not exist here. `BaseCamera` under
`Manager(Clone)` tracks the main camera's rotation and fov but sits at world origin,
so it is not a usable pose source either.

### The trap that made v0.6.0 erratic

Waiting for the pose to hold still is **not** sufficient on its own. Because the
map id changes 17-72 frames before the camera moves, the *previous* map's pose sits
perfectly still for that entire gap — so a settle detector fires early and records
the old map's pose against the new map's id. That is the "camera in the void", and
why pausing shifted which map happened to be correct: pausing changes the frame
timing, moving which pose the detector latched onto.

v0.7.0 requires the camera to be **seen moving** after the map id changes before
stillness counts for anything, and skips learning entirely while
`Time.timeScale == 0`.

### v0.8.0: one mode, plus two things that compose with it

`LiveWhilePovOff` was removed — it only ever recorded correctly when travelling with
third-person off, and recorded SVS_3rdPov's own camera otherwise, which made it
worse than doing nothing.

`LearnedPerMap` is keyed by map id and records whatever the game places, so **custom
and newly installed maps are learned automatically with no plugin update**. Two
options build on it, deliberately as checkboxes rather than dropdown entries,
because they combine:

- **Auto Learn On Arrival** — arriving somewhere unknown *with* third-person on
  briefly clears `povActive`, lets the game place its camera, learns it, and sets
  `povActive` back. Costs a moment of overview on first visit only, and has a
  600-frame deadline so a location that never settles cannot strand the player.
- **Use Built In Coordinates** — seeds the seven story-location poses from the table
  above at startup so a fresh playthrough already knows them. Custom map ids cannot
  be baked in (they depend on what is installed), and any seeded value is replaced
  the first time the real pose is observed.

### The LateUpdate driver was removed

Injecting a MonoBehaviour to place the camera in `LateUpdate` made no observable
difference to the judder, so it was deleted along with `CameraDriver.cs` rather than
left as a knob. Useful negative result: **`SimulationScene.Update` evidently runs
once per rendered frame**, since moving the camera write to a guaranteed per-frame
`LateUpdate` changed nothing. That rules out a camera update-rate mismatch and
points at the follow *target* instead — hence the `Log Movement Cadence` diagnostic.

`Overview Camera Restore`:

- **LiveWhilePovOff** (default, the v0.3.0 behaviour) — continuously remember the
  camera while third-person is off. Reliable when travelling with third-person
  off, which is the common case. Its weakness: arrive with third-person *on* and
  the first pose it sees is SVS_3rdPov's own, which it will then memorise.
- **LearnedPerMap** — the settle-detection above, per map id. Handles arriving with
  third-person on, but a location must be seen settling once before it can be
  restored.
- **Off** — leave the camera where third-person left it.

---

## 10a. The clamp write-back trap (caused two separate bugs)

We write our pitch and distance into SVS_3rdPov's statics each frame to keep them
in step. SVS_3rdPov then **re-clamps them on its next pass** — `distance` to 1..5
and `pitch` to -15..65.

So its statics are a *lossy* store, and reading them back destroys anything outside
those ranges. v0.5.0 did exactly that: `_initialised` was cleared whenever control
was interrupted (a menu, dialogue, the mouse-mode key, a 2D map), and the next
controlling frame re-adopted their clamped values. A zoomed-in distance of 0 came
back as 1, and any extended pitch was thrown away.

That is what broke first-person character hiding: the threshold is
`distance <= First Person Below Distance` (0.5), and distance could no longer stay
below 1. **Never read those statics back after the first frame** — v0.7.0 adopts
them exactly once, behind `_everInitialised`.

Related: character visibility is now written from the **Update postfix**, not from
the LateUpdate camera pass, so it lands in the same pass as SVS_3rdPov's own
`ChaCtrl._visibleAll_k__BackingField = ...` write. Both the property and the
backing field are set, since SVS_3rdPov writes the field directly and bypasses the
setter.

---

## 10b. If it ever needs re-investigating

A `[Debug] Log Camera On Map Change` option is in v0.5.0. It logs, for two seconds
after arriving on a map: every camera in the scene with name, enabled, depth, fov,
position, rotation and parent; `Camera.main`; `SimulationScene.mainCamera`; and
then every subsequent change to the main camera's position, frame-numbered.

Read the log for these, in order of likelihood:

1. **Is `SimulationScene.mainCamera` the same object as `Camera.main`?** CameraPlus
   FINDINGS §2 already established that in ADV scenes `Camera.main` is the
   *background* camera and the real one is separate. If the same split exists on
   the simulation map, SVS_3rdPov has been moving the wrong camera all along and
   the overview pose was never lost — it is on a camera nobody touched.
2. **On which frame does the pose settle?** If it changes at f+15 rather than f+1,
   the capture simply needs to happen later, or on a "pose stopped changing" test.
3. **Is there a second, disabled camera** in the list? Map prefabs often ship a
   camera used purely as a pose marker.

If the log shows nothing useful, the next step is RuntimeUnityEditor rather than
more static analysis:

- Object Browser, with third-person **off**, on a map: find the active camera and
  note its transform. Change map, note it again. Two known-good poses to compare
  the log against.
- Search the scene for the loaded map root and look for child objects named
  `Camera`, `CamPos`, `Anchor` or similar — the pose is baked into the map's asset
  bundle, so if there is a marker it is a child of the map.
- Watch `MapManager.ChangeMap` / `LoadMapObject`: both take a `Camera` argument and
  SVS_3rdPov passes `null`. A postfix logging that argument would show whether the
  game hands the camera in explicitly.

Ghidra is the fallback and should not be needed for this.

---

## 10c. Telling 2D locations apart — the game already does it

No external plugin is needed; SVS_MapLoader was not required. SVS_3rdPov itself
tests exactly this pair, and both halves are reachable:

```csharp
MapManager.mapListTable                 // STATIC property -- no singleton needed
    .TryGetValue(mapId, out MapInfoParam param);
param.IsUseTimezone2D                   // true for the flat 2D-backdrop locations
```

`Manager.MapManager.get_mapListTable()` is confirmed `Static=True`, which is why
SVS_3rdPov can write `MapManager.mapListTable.TryGetValue(...)` directly. Note the
interop signature takes **`out`**, not the `ref` that dnSpy renders in decompiled
SVS_3rdPov source — passing `ref` is a compile error.

`MapManager.Is2DMap(int)` also exists but is an *instance* method, so it needs the
singleton; the static table above avoids that entirely.

Used to skip overview-pose learning and auto-learn on 2D locations, where
third-person never runs and there is no overview camera to learn.

---

## 10d. Pose priority, and whether built-in beats learned

Order applied in v0.9.0:

1. **A pose we already hold wins** — built-in or previously learned, used as is.
   Learning will not overwrite an existing entry, and auto-learn does not trigger.
2. **Otherwise, auto-learn on arrival** — only when the location is unknown, we
   arrived with third-person on, and it is not a 2D map.
3. **Otherwise, ordinary learning** — arriving with third-person off records it.

To answer the question that prompted this: **there is no runtime speed difference
between a built-in pose and a learned one.** Both end up as entries in the same
`Dictionary<int, Pose>` and cost one lookup. The only advantage of the built-in
list is that it is populated before the first visit, so a fresh playthrough never
needs the auto-learn detour for the seven story locations.

The trade-off of "never overwrite" is that a wrong built-in value cannot
self-correct; turning off `Use Built In Coordinates` falls back to learning
everything.

---

## 11. First person by hiding the character

`Character.Human.visibleAll` (and its `_visibleAll_k__BackingField`) is a plain
bool that SVS_3rdPov already drives every frame:

```csharp
playerAI.BehaviourCtrl.ChaCtrl._visibleAll_k__BackingField = !flag5;
```

Since we run after it, setting `visibleAll = false` wins. And because SVS_3rdPov
rewrites it every frame, simply *not* setting it hands control straight back —
no restore bookkeeping needed beyond one flag.

**A gradual alpha fade was not implemented.** `visibleAll` is a hard on/off, so a
fade would mean walking every renderer on the character and animating material
alpha. SVS characters use custom multi-pass shaders whose transparency properties
are not documented in metadata, the correct property name differs per shader, and
switching a material to transparent mid-frame commonly breaks render order. That
needs RUE to inspect the real materials first, and is worth doing as its own
piece of work rather than guessed at blind.

---

## 12. Still open (companion era) — superseded

This list dated from the two-plugin design and is closed. The `Input.GetAxis` /
`GetKey` patches it worried about were removed in the merge (forward-on-mouse is
part of the movement vector now); `CursorTargetSelect` was confirmed as the left
click during third-person and is suppressed only while the cursor is captured
(§13); left click no longer leaks into ADV, H, My Room or map select, since
movement stops in the menu branch. What is genuinely open now is at the end of §13.
The ADV `ClickNext` question (§4) belongs to CameraPlus.

---

## 13. Merged-plugin findings

### The collision buffer was applied even with nothing in the way

`ResolveCollision` computed `nearest = length` when no collider was hit, then
returned `Clamp(nearest - buffer, ...)` regardless -- so the camera was pulled in
by the buffer amount on **every frame, in open space**. That silently shifted the
measured camera-to-head gap, which is what the character-hide threshold tests, so
the distance at which the character vanished moved as soon as collision was
switched on whether or not anything was actually being hit. Now an unobstructed
cast returns the requested position untouched.

### Clicking a travel button was read as "walk forward"

With Forward Mode on LeftClickForward, holding left click sets the Vertical axis to
1. In button mode the cursor is freed so a button can be clicked -- and that click
was still being read as one frame of "forward". Our movement code then drove the
player by hand for that frame and stopped, cancelling the walk the button had just
started. Hence "it stops as soon as it starts", and only with walking enabled:
without it our movement pass never ran.

The fix is one line: while the cursor is free, a mouse button is for clicking
things, not for walking (`MovePlayer(..., allowMouseForward: !cursorFree)`).

A first attempt yielded movement control for 180 frames after any click in button
mode. It did not work, because the problem was never the `Stop()` call — it was our
own forward movement. That scaffolding was removed rather than left in place.

### Hiding the player belongs to ADV, H and My Room only

The original hid the player for every overlay it detected. Select Map, Meet Up,
the outfit picker and the relationship chart all draw *over* the world rather than
staging their own view, so hiding the character there just makes them disappear.
`ShouldHidePlayer()` is now narrower than `IsAnyMenuOpen()`: the former decides
visibility, the latter decides whether we drive.

### Clicking characters had been suppressed too broadly

`CursorTargetSelect` is the game's click-on-a-character handler. Blocking it
whenever third-person runs also removed clicking people to talk to them, which the
original supported. It is now suppressed only while the cursor is *locked* -- the
state where left click means "walk forward". Whenever the cursor is free the click
goes through as it always did.

### Drawing our own ConfigurationManager rows — what breaks the window

A `CustomDrawer` replaces ConfigurationManager's whole value cell, so anything it
provided goes with it. Three mistakes, each with a distinct symptom:

| Mistake | What it looked like |
|---|---|
| `GUILayout.FlexibleSpace()` in the row | Row expands past the window; the last button sits off screen, and other settings stretch too |
| `GUILayout.BeginVertical()` for a header line | The setting name jumps *above* the controls instead of sitting beside them |
| `GUILayout.Toolbar(int, string[])` | Row renders **blank** — the managed `string[]` is rejected by the IL2CPP interop layer and the drawer throws |

The blank row is the nastiest, because the drawer is wrapped in a try/catch so the
window still works and nothing obviously fails. **If a row renders empty, read
LogOutput.log** — the warning names the exception. Plain `GUILayout.Button` calls
avoid the array problem entirely.

Replacing the drawer also removes CM's own Set button, so a key row has to supply
its own way of assigning a binding.

### Capturing a keybind inside IMGUI

Two traps:

- **The click that starts the capture is still down on the same frame**, so polling
  input immediately records left click and closes again — which reads as "clicking
  does nothing" while silently rebinding the key. Guard on `Time.frameCount`.
- **A Button swallows the mouse click you are trying to record.** While capturing,
  draw a `Box` instead.

Keyboard and the first three mouse buttons come from `Event.current`
(`EventType.KeyDown` / `MouseDown`), which is the reliable route inside an IMGUI
window. `Mouse3`..`Mouse6` are not reported there, so those alone are polled via
`Input`, once per frame.

### Hooking ConfigurationManager's own Reset instead of drawing another

Every ConfigurationManager row already carries a Reset button, so adding one to a
custom drawer duplicates it. But the second half of a pair is `Browsable = false`,
has no row of its own, and so has no Reset — resetting the first left a stray
second key behind.

Rather than compete with it, the effect is watched for: a write that puts the
setting back to its `DefaultValue` and did **not** come from our own key list is a
Reset, and clears the pair. `PairDrawer.AssigningFromList` is what tells the two
apart, and it works because `SettingChanged` fires synchronously on assignment.
Only this plugin's own settings are subscribed, so no other plugin's Reset is
affected.

### Walk-to-a-distant-character came from the game, and we were suppressing it

Pointing at someone across the map and pressing interact makes the player walk over
— that is `SimulationScene.CursorTargetSelect`, the game's own click-on-a-character
handler. This plugin suppresses it whenever a mouse button has been taken over for
walking, so the feature quietly vanished in **both** Forward Modes and survived only
with Forward Mode off.

Rebuilt on the interact key instead (`AimedAtFromAfar`): whoever is nearest the
centre of view within 14 degrees and 45 metres, passed to the same
`scene.SetTarget(ai)` the close-range path already used. Only consulted when nothing
is within reach, so it cannot steal a doorway or job spot from under your feet.

### Wheel character-cycling was never available in third person

Worth recording because it looks like a regression and is not. Junh2x's original
prefixed `SimulationScene.WheelTargetSelect` with `return !isPovRunning`, so the
game's wheel-cycling was suppressed for the whole time third-person ran — it only
ever worked in the overview camera. It also cleared every `ai.objCircle` on every
frame, so no character marker could survive anyway.

Added in this version, limited to button mode, where the cursor is already free and
the wheel is not being used to zoom. Two details make it behave like the overview:

- **Markers appear only after scrolling.** Not a timer or a flag — while cycling we
  simply stop clearing `objCircle` each frame. Until the game lights one they are
  already off, and they clear again the moment cycling ends.
- **Zoom is suspended while cycling** (`Place(..., allowZoom: false)`), since one
  wheel cannot do both. Only noticeable with Looking set to Free or Edge; the
  default No look leaves the wheel free anyway.

`WheelCyclingActive` is read by the patch one frame late — the game calls
`WheelTargetSelect` during its own `Update`, before our postfix recomputes the flag.
One frame at the very start of button mode; not worth chasing.

### Still open

- **Lock-on.** Right click in first person to follow the character nearest the
  centre of the screen, camera tracking them. Not started.
- **SVS_PovX** is installed in the test game, unexamined, and also drives the
  camera. Worth checking for conflicts before recommending the two together.
- **Gamepad for choices and menus** — next planned work: picking dialogue choices,
  moving through menus, and the rest of the game's UI from a controller. Not
  started. The game's Input Manager already maps `Submit`/`Cancel` to keys only and
  joystick buttons 0–3 to `Fire1`–`Jump` (§15); whether its UI listens to any of
  them is unknown.
- **Other gamepads without Steam Input** (DirectInput pads): left stick and buttons
  arrive through Unity with different button numbers; right stick and triggers
  need XInput and will not work. Untested.

### Settled

- **Two hotkeys per row in the config UI** — built and working. `ConfigurationManagerAttributes`
  is matched by ConfigurationManager **by field name through reflection**, not by
  type, so declaring our own copy in the global namespace is the normal pattern and
  costs nothing when that plugin is absent. The first of each pair carries a
  `CustomDrawer` that renders both; the second is `Browsable = false` so it does not
  also get its own row. Scope is limited to our own settings inside CM's window.
  **If CM's IL2CPP build ignores CustomDrawer, the second key vanishes from the
  window instead of appearing beside the first** — it stays editable in the .cfg,
  and the fix is to drop `Browsable`.
- Job buttons take `points[0]` for their job kind. Where a map has several markers
  for one job, nearest-to-player would be better.

## 14. Action points: Constant vs Pop-ups (the game's graphics option)

Reported: with the game's graphics option for action points on **Pop-ups** (a
community translation; Constant is the default), tracked buttons sat well away
from their doorways, appeared only under the cursor, one at a time, and seemed
to glide about as the character moved. Fine on Constant.

**The setting** is `SV.Config.GraphicSystem.MoveUIDrawChange` (bool). The game's
code that reads it is a stub, so it was not followed; the prefabs answered it.

**Read from the asset bundle, not the code.** `abdata\map\ui\000_00.unity3d`
(also 005_00, 100_00, and the custom maps' `ui\*.unity3d`) holds the travel UI.
Dumped with UnityPy (`pip install --target <scratch>\pylib UnityPy`, then walk
`RectTransform.m_Children` and each GameObject's components via
`read_typetree()`). Every `MoveUI_NNN` (a `MapMoveUISummary`) has two
`MapMoveUI` children, and the option picks which is active:

| | `MoveUIAlways` — Constant | `MoveUINormal` — Pop-ups |
|---|---|---|
| Button rect | 168×40, pivot centre | a hit area up to ~580×630, **pivot top-left** |
| Button `Image` | the visible button | invisible, raycast target |
| `Button.transition` | 2 (sprite swap) | 3 (animation) |
| Label | the button itself | child `imgFrame`, 168×40, **inactive**, offset inside the area |
| Animator | none | `ButtonUIDraw`; its clips only toggle `imgFrame` active (hover → on) |

So on Pop-ups, pinning the button put the hit area's *corner* on the marker and
the label hundreds of pixels off; the label existed only while one of the
now-moving invisible areas was under the cursor; and those areas caught clicks.
Same layout in every bundle checked, custom maps included.

**Fix (MapButtonTracker.PopUp):** a tracked button with an `Animator` and an
`imgFrame` child gets, until tracking stops: Animator disabled, and we do its
job instead — `imgFrame` is shown only while the cursor is over the label's own
spot, widened by half its height all round (`PopUp.Hovered`; the big area can't
be used, its offset from the label is what caused the drift). First version
forced every label on, which the user rejected: Pop-ups should still pop up.
Raycast moved from the hit area's Image
to the label's Image, and positioning that moves the button by whatever brings
the *label* onto the marker. A click on the label still reaches the Button
(Unity walks up the hierarchy for the handler). `Selectable` skips its animation
triggers when the Animator is disabled, so there is no log noise. All four
things are restored with the button's position. `AnyStale` now also rebuilds
when the owning `MapMoveUI` goes inactive, so switching the option mid-game
picks up the other set.

Confirmed live, including the later hover-to-show behaviour.

## 15. Gamepad input, and movement outside third-person

**The game's Input Manager** (read from `SamabakeScramble_Data\globalgamemanagers`
with UnityPy, `InputManager.m_Axes`) is close to Unity's default:

- `Horizontal` / `Vertical`: keyboard (arrows, with WASD as the alternates) **and**
  joystick axes 0/1, the left stick, dead zone 0.19, sensitivity 1.
- `Fire1`–`Fire3`, `Jump`: joystick buttons 0–3 (A, B, X, Y on Xbox).
- `Debug Horizontal` / `Debug Vertical`: joystick axes 5/6, the D-pad.
- **Nothing on axes 3/4, the right stick.** The legacy input system can only read
  axes the Input Manager names, so the right stick is unreadable through Unity.
- Only `UnityEngine.InputLegacyModule` ships; the new Input System isn't present.

So: the left stick already arrives through `Horizontal`/`Vertical`; `GetAxisRaw`
gives its true tilt (keyboard is always 0 or ±1), which is how "slight tilt walks"
is detected. Buttons are ordinary `KeyCode.JoystickButtonN`, so the gamepad
interact button is a normal key entry. The right stick is read directly from
Windows through XInput (`Gamepad.cs`: `xinput1_4.dll`, falling back to
`xinput9_1_0.dll`; radial dead zone 8689/32767; empty slots rescanned every 2 s
because querying a disconnected slot is slow).

**WASD In All Views:** the Disable branch of the per-frame pass (overview camera,
2D maps, anything not third-person) now calls `MovePlayer` when the option is on,
with mouse-forward off and the same menu/ADV/H stand-down. Movement direction is
taken from the camera's **yaw only**: the old `TransformDirection` then flatten
shrank forward towards zero as the camera pitched down, which the overview camera
does steeply. `StopHandling` ends a hand-driven walk when the option is off or a
menu opens, and on Shutdown.

Confirmed live: WASD In All Views, right stick, A to interact, slight-tilt walk.

**Fixed views need screen-relative directions.** In the overview camera, "camera
heading" was right only for a character at screen centre; off to the side they
drifted, because a perspective view makes lines running away from the camera
converge on the centre. Outside third-person, `TryScreenAxes` nudges the
character's screen position 20 px right and up, casts both back onto the
horizontal plane through its feet, and moves along those ground directions — so
"up" is up on screen wherever the character stands. Falls back to camera heading
if the view is too edge-on. Third-person keeps camera heading (the camera orbits
the player, so the two agree).

**Triggers** come from XInput as well (no Input Manager axes); threshold 30/255.
Left in, right out (swapped from the first version at the developer's request), squared, fed to `ApplyZoom` as scroll per second (one wheel notch =
0.1). **Buttons** (legacy KeyCodes, Windows Xbox mapping): 0 A, 1 B, 2 X, 3 Y,
4 LB, 5 RB, 6 Back, 7 Start, 8 left-stick click, 9 right-stick click. Sprint
defaults to RB, the toggle to left-stick click.

## 16. The player's ring after leaving third-person

The player's `objCircle` is the game's "this is you" marker in the overview, shown
on arriving somewhere. Third-person clears it every frame and borrows it (moved
off the player) to mark job spots and doorways; nothing restored it, so it stayed
gone back in the overview. Now its active state and local position are captured
on entering third-person and restored when leaving with Overview Camera Restore
On — shown if it was shown, or if the map changed meanwhile (arriving is when the
game would have shown it). UNVERIFIED that the game itself never hides it later.

## 17. Custom maps: overview poses, and unregistered buttons

**Poses were only learned in the overview camera, and only kept in memory.** A map
first entered in third-person therefore had no pose, so switching third-person off
restored nothing (camera left where the rig had it, vanilla fixed buttons out of
line with it), and every custom map was forgotten on quitting. Now:
- (Briefly, learned poses were also saved to `SVS_3rdPovPlus.overview.txt`. Dropped
  at the developer's request: with learning in both views every map is relearned
  on its first visit each session, which costs nothing noticeable. The one case the
  file covered — loading straight into a custom map with third-person already on —
  is handled by accepting whatever pose the game set before the rig's first ever
  `Place` of the session, since nothing else can have moved the camera by then.)
- `WatchForOverviewInPov`: the rig records where `Place` put the camera; at the
  start of the next third-person pass, within 600 frames of a map change, a camera
  that is elsewhere was moved by the game — its overview placement on arrival —
  and that pose is kept (later moves in the window update it). Skipped while a
  conversation/H is open; the ADV and H camera branches call
  `CameraMovedElsewhere` so their moves are never mistaken for it. Confirmed live,
  including starting a new game with third-person already on.

**Unregistered travel buttons.** SVS_MapExpansion's map 256 has two "Coming
Soon" buttons; `btnGoto260` (whose `_gotoMapID` is 256, its own map) is **not in
its MapMoveUI's `mapMoveUIButtonCtrls` list**, so the game never manages it and
the tracker, which read only that list, never saw it — it stayed at its fixed
spot. The tracker now also collects `MapMoveUIButtonCtrl` /
`MapPCActionUIButtonCtrl` children of the active MapMoveUI that are not in the
lists, and parks them while tracking (they lead nowhere in the world).

## 18. Walks, and cancelling them

`_walkingTo` records a walk started by interact or Go To Marked Key; a second
press of either stops it (`scene.MoveStop()` + `Stop(true)`). It was cleared
every frame in `Disable`, i.e. always outside third-person, so middle click could
never cancel in the overview camera. Now cleared only on map change, when a
menu/conversation opens, on manual movement, and on arrival (within 1.5 m).
Outside third-person the game's own middle-click handler starts the walk (it runs
inside `SimulationScene.Update`, before our postfix), so the postfix only records
the target on the first press and cancels on the second.

Two follow-ups from testing:
- **Stale record after the game's own cancel.** Right click cancels a walk in the
  overview, unseen by us, so the next middle click "cancelled" a walk that wasn't
  running and only the third press worked. `ForgetArrivedWalk` now also drops the
  record once the player has stood still for 0.5 s (after a 1 s start-up grace),
  whatever stopped them; right click outside third-person clears it at once.
- **Vanilla middle click re-targets.** With a different character marked it heads
  for them instead of stopping. Go To Marked now does the same everywhere: it
  only stops when the marked character is the one being walked to, or nobody is
  marked.

**Latched buttons during conversations** (Overview Camera Restore Off): the
blocked branch handed the tracker's roots back but, unlike the non-button-mode
path, never hid `objMapMoveUI`, so the vanilla travel UI sat at its fixed
overview positions through the conversation. The blocked branch now hides it.

## 19. Clean-up pass (2026-09-28)

- `Disable()` runs every frame outside third-person. It used to set
  `objMapMoveUI.active = true` and `Cursor.lockState = None` on every one of those
  frames, overriding the game whenever it wanted either otherwise. Both now happen
  only on the frame third-person ends (`was`).
- `ApplyCharacterVisibility` looped over a `new[]` of three controllers every frame;
  now three calls, no allocation.
- The doorway list cache compared managed wrappers (§ above, `FindNearestTarget`)
  and so rebuilt via `GetComponentsInChildren` every frame; it compares native
  pointers now. **General rule: never compare Il2Cpp wrapper objects with `==` /
  `!=` unless the type is a `UnityEngine.Object`; compare `.Pointer`.**
- Removed: unused `CameraRig.HasPoseFor`; the learned-poses file (§17).

## 20. Force High Poly Characters

SVS keeps **two copies** of a character. The map copy is built by
`Character.Human.CreateLowPoly(HumanData)`; conversations and H build a separate
high-poly copy. `Human` has a `hiPoly` flag (constructors
`.ctor(HumanData, bool hiPoly[, bool releaseCustomInputTexture])`,
`CreateCustom(data, hiPoly)`), and every part class (`HumanBody`, `HumanFace`,
`HumanHair`, `HumanCloth`, `HumanAccessory`) carries its own `hiPoly`. Parts load
through `Human.LoadCharaFbxData(..., bool hiPoly, ...)`.

**They are separated by render layer too** (measured live in RUE, 2026-09-29):
`Human.lowLayer` = 7 (mask 128), `Human.highLayer` = 10 (mask 1024). A map
character's meshes are on layer 7 (its root on 0). `Camera.main` ("Main Camera")
has cullingMask 9119 — includes 7, **excludes 10** — in the overview and during
conversations alike, so the conversation copy is drawn by another camera (the
HighPolyBackGroundFrame's, presumably).

So forcing high poly cannot just flip the flag: the meshes would land on layer 10
and vanish from the map. `HighPoly.cs` turns the constructor's `hiPoly` on only
while `CreateLowPoly` runs, moves each part `LoadCharaFbxData` returns from layer
10 to 7, and re-checks one upgraded character per frame as a safety net. Adding
layer 10 to the main camera instead would also draw the conversation copies in
the world. Compare: KK_Plugins' ForceHighPoly (Koikatsu) sets `ChaControl.hiPoly`
and swaps `_low` assets — one copy, no layer split.

**What could not be patched** (first attempt, 2026-09-29 — broke character loading):
- `Human..ctor(HumanData, bool[, bool])`: Il2CppInterop logged "Failed to init
  IL2CPP patch backend … using normal patch handlers" — constructors can't be
  detoured for native callers, so the `hiPoly` argument was never changed.
- `Human.LoadCharaFbxData` (three `ref` Il2Cpp parameters): even a postfix made
  the trampoline throw `InvalidProgramException` on every call, so **every part
  failed to load** (no heads, clothes, accessories) — with the option off, since
  the patch was installed regardless.
**Rule: don't Harmony-patch IL2CPP constructors, or methods with `ref`/`out`
Il2Cpp parameters.** Check LogOutput.log for Il2CppInterop warnings after adding
any new patch.

Second attempt: a prefix on `Human.CreateLowPoly` returning
`CreateCustom(data, true)`. It never fired — map characters are built by
`SV.Chara.AI.CreateAsync` (a UniTask state machine), which evidently doesn't go
through `CreateLowPoly` as a call (inlined, or builds directly).

**What works — upgrade after creation, confirmed by hand in RUE (2026-09-29):**
`human.hiPoly = true; human.Reload();` reloads every part at full detail (onto
layer 10, so the character mostly vanishes), then moving each transform on
`Human.highLayer` to `Human.lowLayer` (91 on the player) brings it back fully
clothed, high-poly, animating normally. `HighPoly.cs` does this for every
`Game.AICharas` character that is still low poly (so the game's own high-poly
copies are never touched): discovered twice a second, upgraded one per frame once
it has existed 1 s and isn't `isReloading`, layers fixed every frame for 15 s
after any reload and round-robin afterwards. Switching the option off reloads
them back to low poly the same way, so it is live in both directions.
- Removed options (2026-09-29): **Hide Blur** — the plugin forced
  `HighPolyBackGroundFrame.mainCamera` on every third-person frame (off only with
  Hide Blur), inherited from the original; the frame is now left to the game.
  **Crouch Is A Toggle** — replaced by Double-Tap To Crouch (hold still crouches).
  **Collision Buffer / Probe Radius** — now constants in `CameraRig` (0 and 0.01).

## 21. Gamepad travel outside third-person

Third-person interact reaches doorways and job spots itself (`FindNearestTarget`,
`TakeDoorway`). In the overview camera and on 2D maps nothing did — travel was
mouse-only, through the location buttons. `GamepadTravel.cs`: on the Gamepad
Interact Button, find the nearest doorway (`SVNodeLink2`, `startMapID` = here) or
job spot (`pointInfoTable[here].pointList.pcTable`) within **Doorway Reach**
(ground distance), and **invoke that location's own button**
(`MapMoveUIButtonCtrl._btn` by `GotoMapID`, `MapPCActionUIButtonCtrl.btn` by
`Job`) via `Button.onClick.Invoke()`. Both controls subscribe to their button in
`Start` (UniRx `OnClickAsObservable`), so this is a real click: the game walks the
character to the invisible doorway and changes map itself. A button that is
inactive or not interactable (destination closed now) is skipped, so the game's
own availability rules hold. Only the MapMoveUI in use is searched (§14).
UNVERIFIED live, in particular on DarkSoldier27's 2D maps.

Build: `GenerateAssemblyInfo` is now on in the csproj with Version 1.0.0 /
AssemblyVersion and FileVersion 1.0.0.0, and
`IncludeSourceRevisionInInformationalVersion` off (otherwise the product version
carries `+<git hash>`). Windows' file properties showed 0.0.0.0 before.

## 22. Gamepad button mode: the overworld UI's layout (from a live dump)

`Selectable.allSelectablesArray` held ~200 entries; most belong to hidden screens
(the options window sits on a root `Canvas` at sort 99 under a CanvasGroup with
alpha 0). The ones that matter, as root canvases:

| Root canvas | sortingOrder | renderOrder | Holds |
|---|---|---|---|
| `MoveCanvas` | 1 | 1 | Week/btnOpenClose, BaseButtons (btnMap, btnChara, btnEveryone, btnOption, btnHelp, btnGotoRoom), EveryoneButtonsAnimator/mask/Everyone/btnEveryOne00–06 |
| `GameCanvas` | 1 | 2 | btnNext |
| `CanvasMapMove` | 0 | 0 | the location buttons (MoveUI_NNN) |
| MapSelect(Clone)/`Canvas` | 1 | 0 | Thumbnails/btnMap_000–006, btnBackBG/btnBack |

- Picking "the top screen" by (sortingOrder, renderOrder) chose GameCanvas alone,
  so btnNext was the only choice. **Group by sortingOrder only.** While Map Select
  is open the game hides MoveCanvas's buttons, so its maps end up alone anyway.
- The btnEveryOne buttons stay active and interactable while the Everyone list is
  closed — slid outside `mask`. Exclude a selectable whose centre is outside any
  `RectMask2D`/`Mask` parent.
- Map Select's current map is `interactable = false` (btnMap_000 when at Station).

Open: gamepad A at a doorway sometimes starts the walk and it stops at once;
pressing again usually works. Suppressing hand-driven movement until the stick is
released did not fix it (reverted). Developer's guess: depends on which way the
character faces.

**Meet Up portraits** are `SV.SexualTargetUI` (via `SexualTargetActorUI` /
`CharaSelectScene.CharaUI`), a `UIBehaviour`, not a Selectable: clicks and hover
arrive through pointer events on the picture (`_imgChara`, exposed as
`OnClickObservable` / `OnEnterObservable`). Gamepad mode collects them from the
open `CharaSelect` (`HasChara()` only) and sends `ExecuteEvents.ExecuteHierarchy`
pointer click/enter/exit to the picture. Its back button is `CharaSelect._btnBack`.

## 23. Gamepad in conversations

`GamepadADV.cs`, built on CameraPlus FINDINGS §2 "Blocking click-advance":
A with nothing selected finishes a typing line (`TextController
.ForceCompleteDisplayText`) or advances via `MessageWindowProc(NextInfo.Set(true,
true, false))`, only while the message frame is on screen, lifting `ClickNext`
around the call if something blocks it. Choices: `TextScenario.IsChoice`, buttons
under `TextScenario.Choices` — button mode switches on at the top choice and off
again after one is picked. Left stick flicks move like the D-pad in conversations.
UNVERIFIED live.

## 24. Character lighting (RUE light dumps, 2026-09-29, morning)

Each map has a directional **character key light** with `cullingMask` 128 (layer 7
only), tuned per map for the low-poly models, plus `Directional Light` mask 129
(layers 0+7) at 0.1 and environment lights on 256 (layer 8), 16, 8192:

| Where | Character light | Intensity / colour | Rotation |
|---|---|---|---|
| Classroom (head darker) | `Directional Light_chara_map` | 1 / 0.80 grey | (58.6, 327.2, 285.3) |
| Shrine | `Directional Light_chara` | 1 / white | (65.9, 193.2, 174.6) |
| Beach | `Directional Light` (mask 128) | 1 / white | (46.4, 164.7, 174.6) |
| Character creator | `Directional Light` mask **1024** | 1 / white | (21.5, 188.9, 0) |
| Conversation | `Directional Light Key` mask **1024** (+ `Sub` at 0.01) | 1.5 / 0.84 grey | (29.2, 163.2, 354.8) |

High-poly models are lit on layer 10 by the conversation/creator key light. Moved to
layer 7 (§20), upgraded characters got each map's low-poly light instead. With
**Custom Light For High Poly** (`CharaLight.cs`) their *renderers* go on layer 31
(unused, added to `Camera.main.cullingMask`) and a light of our own lights only that
layer (optionally layer 7 too); colliders stay on 7 so clicks and raycasts are
unchanged. Defaults are the creator's, turning with the camera.

Open: Force High Poly breaks some ADV scenes (Work, conversations with some
choices; also after waiting on the first line or on the choices): the text stops
advancing. Log shows `[Error : Unity] InvalidOperationException: Sequence contains
no elements`. Deferring upgrades to free roaming did not help (reverted). Suspect
the map-character ADV commands (`ADV.Commands.Game.LowChara.LowPoly*`) expecting a
low-poly character. The error is raised inside game code, so the log has no stack
trace for it. Key observation: with high poly on, the question line stays after a
timed choice expires, where normally a result line follows.

Current approach (UNVERIFIED): `ADVManager` backs up each map character's low-poly
animation when a scene starts (`SetBackupAnim(Actor)` into `_backupLowAnim`).
Prefixes on `OpenADV`, `OpenADVAsync`, `InsertADV` and `SetBackupAnim` switch every
upgraded character back to low poly (hiPoly=false + Reload) before the scene takes
its backup; upgrades are held off while `IsADV`/`IsInsertADV` and 1.5 s after.

Also: **the game maps Fire1 (a click) to joystick button 0**, so gamepad A also
clicked wherever the mouse cursor was. `CursorTargetSelect` is now skipped while A
is held with Gamepad Support on (suspected cause of gamepad doorway trips being
re-aimed or cancelled, character flipping round).

**Lighting v2 (UNVERIFIED).** A second directional light of our own looked soft and
"plastic" and never reached low-poly characters. Likely cause: URP gives full
per-pixel lighting to one *main* directional light and treats others as additional
lights (and the low-poly material may ignore them). So `CharaLight.cs` now edits the
**map's own character light** in place (the directional light whose cullingMask is
exactly 128), from a preset (Shrine/Beach/Classroom world rotations from the dumps;
CharacterCreator/Conversation camera-relative) or Custom sliders, and restores the
original when set back to MapDefault or on map change. HighPolyOnly/LowPolyOnly keep
the layer-31 split with our own light and are marked experimental.

**High Poly In Scenes** (temporary choice): RestoreAll (default, what was
confirmed), RestoreSceneCharacters (only actors passed to `SetBackupAnim`),
SkipBackup (prefix returns false on `SetBackupAnim` for upgraded actors;
experimental — the restore at scene end may then find nothing).

Tested (2026-09-30), all FAILED:
- **SkipBackup** still froze (same error). Removed.
- **RestoreSceneCharacters** froze after choices once the mayUpgrade fix landed. It had
  only "worked" because the bug switched *everyone* back during every scene. So a
  scene needs more characters back than the ones passed to `SetBackupAnim`.
- **MapScenesOnly** (prefix on `ADV.LowCharaADV.Processing`) froze; its log line never
  appeared, so LowCharaADV is not what runs in the scenes that freeze. Removed.
- **SkipFailingSteps** (finalizers on `Do()` of ~120 `ADV.Commands.Game.*` types)
  crashed the game the moment it was chosen. Removed. Do not mass-patch command types.
- Receiving shadows switched off on map surfaces (`Renderer.receiveShadows`, and
  `_ReceiveShadows` = 0 + `_RECEIVE_SHADOWS_OFF` on 54-379 materials) changed nothing
  visible. Removed.

Developer's observations: the freeze is in some scenes with three choices. While
stuck, the "next" arrow (`ADVScene(Clone)/Canvas_Main/MsgWindowCanvas/MsgWindow02/
Right/Next`) never appears; it only signals and does not gate advancing. Disabling
`ADVScene(Clone)` or its `ADVScene`, `MainScenario` or `ObservableUpdateTrigger`
components stops advancing. There is a camera-parented light
`SimulationScene/Main Camera/Directional Light` that can be switched off without
visible change to the scenery while `sv_m000/light/light_gp_00/Directional_chara_map`
is active.

Now (UNVERIFIED): RestoreAll is the default again. RestoreSceneCharacters = the player +
`SetBackupAnim` actors + `Set3PFlag(actor, actor1, actor2)` actors; RestoreNearby = that
+ everyone within 12 m of the player. A `Application.logMessageReceived` listener
writes the Unity stack trace of "Sequence contains no elements" (BepInEx prints only
the message), plus who was still high poly and how far away. **Scenery Shadows** off
sets `shadowCastingMode = Off` on non-character renderers. **Hide Camera Fill Light**
(testing) disables the camera-parented light. Every light (path, mask, intensity,
shadows, rotation) and `RenderSettings.sun` is logged once per map.

Results (2026-09-30): RestoreAll, RestoreSceneCharacters (with player + 3P) and
RestoreNearby all **work**. So the missing piece was probably the player. Kept:
RestoreSceneCharacters (default; cheapest), RestorePlayerOnly (test: is the player
alone enough?), DontRestore (freezes on purpose so the stack-trace trap fires). Every
scene opened is logged as "Force High Poly: scene <asset> (character N, category N)"
(prefixes on OpenADV/OpenADVAsync/InsertADV read `asset`, `charaID`, `category`), so
the scenes that need it can later be singled out. Confirmed freezing activities:
Work, Study, Exercise, Eat.

**Ground shadows are a separate plane (CONFIRMED from bundles).** The map light dump
showed the only active shadow-casting light is the character light
(`Directional_chara_map` / `Directional_chara Light`, mask 128, Soft, strength 0.8), and
it is `RenderSettings.sun`. Map lights on mask 256 (layer 8) are bake lights, off. The
ground does not receive shadows itself: `map/scene/000_00/01.unity3d` has
**`o_plane_shadow`** (layer 7, MeshRenderer, material "m_map shadow", shader
**`lif_shadow_map`**, Transparent+1900, `_ReceiveShadows` 1, casts nothing), a
transparent shadow catcher over the ground. That is why props never show character
shadows. **Character Shadows On Ground** off disables every renderer whose material
uses `lif_shadow_map` (UNVERIFIED in game). "Scenery Shadows" (casting off on the map)
removed the map's own shadows instead, which was not wanted; removed.

**Conversation lights per time of day** (`adv/lit/000_00.unity3d`, prefabs
`sv_adv_lit_cha_00..03`, each Key + Sub at 0.01, Back inactive). All Keys share one
angle; only colour and intensity differ:
- 00 morning: 1.5, (0.84, 0.84, 0.84)
- 01 midday: 1.5, (0.976, 0.959, 0.875)
- 02 afternoon: 1.9, (1, 0.671, 0.4) (02_001: 1.75, (1, 0.725, 0.5))
- 03 night: 1.5, (0.55, 0.738, 1) (03_000: (0.7, 0.824, 1); 03_001: 0.86 grey)

The SV map has `TimeofDay/Morning_obj, Day_obj, Evening_obj, Night_obj`, each with
its own `Directional_chara Light`.

**The freeze, located (CONFIRMED by stack trace, 2026-10-01).** With nobody restored
the trap logged, for Work (`s_86`, category 4), Study (`s_84`, category 4) and asking
someone to eat (`a_22_1`, category 0), the same trace every time:

    System.Linq.Enumerable.First(source)
    ADV.Commands.Game.Apartment.Judgement.Do()
    ADV.CommandList.Add(ScenarioCommand, currentLine)
    ADV.TextScenario.<_RequestNextLine>d__147.MoveNext()
    ADV.TextScenario.MessageWindowProc(NextInfo)

`Judgement` (base `ADV.CommandBase`; members `_result`, `ConvertBeforeArgsProc()`,
`Do()`, no lambdas of its own) takes `First()` of something that is empty when the
**player** is high poly, and the scenario never requests its next line. Only the player
matters: RestorePlayerOnly never freezes, with other characters left upgraded. What the
sequence is remains unknown (`Character.Human.list` is a static list of every Human and
a likely source). Modes now: RestorePlayerOnly (default, confirmed), RestoreWhenNeeded
(reload in a prefix on `Judgement.Do`; UNVERIFIED), PretendOnly (no reload,
`Human.hiPoly = false` from scene open until the scene ends; UNVERIFIED). OpenADV calls
OpenADVAsync, so every scene reaches the prefix twice.

**Time of day**: `Manager.SimulationManager` (a `SingletonInitializerAsync`)
`.GetNowTimeZone()` returns an int; `TimeZoneMode` is Morning, Noon, Evening, Night.
`SimulationMode` runs RoomMorning=0 ... SimMorning=2, SimNoon=4, SimEvening=6,
SimNight=8 with transitions between. Used for the ConversationCurrentTime preset
(mapping 0..3 UNVERIFIED; the lighting dump prints the value).

**Scripted trips** (going somewhere with someone after a conversation, following them)
were being stopped on arrival in third person like a location-button trip. A trip is
now also exempt when it starts as a scene closes (same expiry as menu trips).
UNVERIFIED.

The lighting dump now also lists ambient settings, every post-processing `Volume` with
its components, and cameras, on each map and for the first two conversations, to find
why characters look different between maps and conversations under the same light.

**PretendOnly works (CONFIRMED 2026-10-01).** `Human.hiPoly = false` on the player
(model left high poly, no Reload) from scene open to scene end: Work, Study, Exercise,
Eat no longer freeze. So `Judgement.Do()` filters on the flag, not on the model.
PretendWhenNeeded (flag false only from a prefix on `Judgement.Do` until the next
frame) is UNVERIFIED; if it works the others go. Seen once and not reproduced: during a
group activity (Exercise/Study/Eat with others) in third person every character went
invisible until the next map change.

**Classroom Study has no pcTable entry.** `PointList` has five tables (urouro, solo,
with, everyone, pc), each `Dictionary<int, ListInfo>` keyed by `MovePointInfo.JobKind`
(Meal 0, Study 1, Motion 2, Job 3, Bath 4, HouseParty 5, ChangeClothes 6, Shopping 7,
Zizo 8, H 9). Classroom seats are per character (`JobDetail.ClassroomNo`), fetched with
`PointList.GetClassRoomIDPoint(job, charaIndex)`; index tried is
`charaData.charasGameParam.Index` (UNVERIFIED; `ArrayIndex` is the other candidate).
`MapButtonTracker.JobPoints` now falls back to that, then to the solo/everyone/with
tables, for jobs that have a `MapPCActionUIButtonCtrl` on the map; the tracker, the
interact search and gamepad A all read it.

**Map character lights are not always mask 128.** `sv_m000/light/light_gp_01/
Directional_chara_map` (evening) is mask 384 (layers 7+8) with soft shadows: one light
for characters and scenery. The finder now takes any active directional light reaching
layer 7 that is not under a Camera, preferring mask 128, then `RenderSettings.sun`, then
a shadow caster. Editing a shared light moves the scenery's lighting and shadows too,
and its shadows of scenery land on the `o_plane_shadow` catcher, so the ground-shadow
switch hides those as well. Unsolved: giving characters their own light there.

**Apply Light To** now has Scenes: the scene's key light is the brightest active
directional light reaching layer 10 (not mask -1); the scene camera is the
highest-depth camera drawing layer 10 (`Camera.main` stays the map's). UNVERIFIED.

Static ground shadows under a camera-following light are not possible: one shadow map,
rendered from the one main light, gives both the self-shadowing and the ground shadow.

**Settled (2026-10-01): PretendWhenNeeded is the only behaviour.** A prefix on
`ADV.Commands.Game.Apartment.Judgement.Do` sets the player's `Human.hiPoly = false`;
the next `HighPoly.Tick` sets it back. No reload, no other scene touched, no setting.
The scene-open prefixes, the other modes and the log trap are removed.

**Shared map lights: left alone again.** Editing the Station's evening light (mask
384) and Camping's (custom map, one light for everything) turned the scenery's shadows
with the camera, and on those maps the ground receives shadows itself, so hiding the
`o_plane_shadow` catcher leaves a second copy (Station evening) or changes nothing
(Camping, which has no catcher). The finder is back to mask == 128 only, once a second;
on those maps and times the map override does nothing. Open: a character-only light
there.

**Classroom Study button: still missing after the JobPoints fallback (cause unknown).**
From `map/ui/000_00.unity3d`: `MoveUI_004/MoveUIAlways/btnGotoPCAction` is a
`MapPCActionUIButtonCtrl` with `job: 1` (Study), `nowMapID: 0`. From
`map/collison/000_00.unity3d`: map 4's only Study points are 24 **"with"** points
(`m004/PointList_04/with/Target (n)`, type 2, `withDetails` job 1, ClassroomNo 0..23);
it has no pc points at all. Other maps' pc points: Job 3 (maps 0, 1, 2, 6),
ChangeClothes 6 (1, 2, 6), Meal 0 (5), Motion 2 and Zizo 8 (6). `MapMoveUI` has
`SetPCActionButton(act, nowMapID, playerJob)` and `SetTakingButton(isChase)`, which may
be what shows and hides action buttons. With Testing Info on, `MapButtonTracker.Describe`
logs every tracked button (active flags, marker or NO MARKER), the five point tables'
job counts, and `GetClassRoomIDPoint` for the player's Index and ArrayIndex.

**Light sliders in scenes.** Inside a scene the presets are read against the scene's
own key light (`SceneValues`): Map Default and Scene Default show its values, the Scene
time presets keep its angle. The sliders are re-seeded when a scene's light is found and
when the scene ends.

**Study button: solved in code (UNVERIFIED in game).** The Describe log showed the
`with` table with Study x24 and `GetClassRoomIDPoint(1, Index 1)` = `Target (3)` at
(-2.45, 0, 191.90), yet NO MARKER. Cause: **`GameObject.active` reads
activeInHierarchy, not activeSelf.** `ButtonJobs` skipped every MapMoveUI because, in
third person outside button mode, the travel UI root is off; and markers were
collected before `ForceActive`. Now `activeSelf`, and markers are collected after the
roots are forced on. `charasGameParam.Index` is the right seat index (player = 1 here).
The urouro table also holds Study x24 plus job -1 x25.

**Split lighting lit the whole map.** With High Poly Map (or + Scenes) and Scene Night,
the whole map brightened. Likely cause (UNVERIFIED): URP takes `RenderSettings.sun` as
the main light if it is visible, else the brightest directional light; ours at 1.5
out-shone the map's 1 and became the main light, against which the scenery's baked
lighting is mixed. While split, `RenderSettings.sun` is now set to the map's character
light and put back after. Splitting is also skipped on maps with no character-only
light, where upgraded characters used to be left with our light and stale values.

The scene camera is `ADVScene.ADVCamera` (what SVS_ADVFreeCamera drives), now used
directly for Light Turns With Camera in scenes.

**Study button CONFIRMED fixed (2026-10-02).** The same test showed the opposite fault:
`pcTable` lists every activity spot whether or not this character may use it, so in
third person anyone could Work at the cafe, the shrine or the beach by walking onto
the spot, though the overview shows no button there. `JobPoints` now starts from the
action buttons the game has switched on (`MapPCActionUIButtonCtrl` with
`gameObject.activeSelf`, in the MapMoveUI that is `activeSelf`) and only then looks up
their spots; markers are refreshed from it on every tracker pass. UNVERIFIED that
activeSelf is how the game hides an unavailable button; Describe (Testing Info) prints
both active flags if not.

**Lighting section rules (current).** Apply Light Settings To = None switches the whole
section off, ground shadows and fill light included. Choosing a preset sets the
switches (ground shadows on, fill light on, turning with the camera only for Character
Creator) and the sliders; changing any switch or slider while a preset is chosen makes
it Custom from that preset's values. Light Turns With Camera defaults off. On maps
with no character-only light the shared light is read (never written) for Map
Default's slider values, and a line on screen says once per light that the map
override and ground shadows cannot act there (`Notice.Tell`, shown whatever Testing
Info says).

---

## 25. Click to walk and follow (merged from SVS_WalkAnywhere, 2026-10-02)

SVS_WalkAnywhere (D:\Code\SVS_WalkAnywhere, v0.3.0, never released) was a separate
plugin: left click the ground to walk there, right click a character to follow them.
Its code now lives here as `ClickWalker.cs`, `Picker.cs`, `Walker.cs` and
`Follower.cs`, called from the `SimulationScene.Update` postfix after the
third-person pass, with a postfix on `Pathfinding.AIBase.FixedUpdate` for the follow
speed. What changed on the way in:

- Its debug switch is Testing Info (`Notice.Log`); its menu check is
  `ThirdPersonController.IsAnyMenuOpen` plus `Scene.IsOverlap`.
- Clicks still act only while the cursor is free (the overview camera, 2D maps,
  third person with the location buttons up), exactly as when the two were separate.
- The arrival stop (§18) leaves alone a walk whose target is `Walker`'s marker, and
  any walk while `Follower.Following`: following goes through doorways on purpose.
- Settings are in `Click To Walk` and `Click To Follow`; values are taken once from
  `SVS_WalkAnywhere.cfg` if that file exists.
- UNVERIFIED since the merge. Follow was unfinished when merged (v0.3.0).

Its findings, as written in that project (section numbers are its own):

### FINDINGS

Reference for SVS_WalkAnywhere: game class names, hook points, and why the code is
arranged as it is.

Confidence markers:
- **CONFIRMED** — used by working code in 3rdPovPlus, or tested live.
- **SIGNATURE** — seen in dnSpy; the name and shape exist, behaviour unknown.
- **UNVERIFIED** — a guess from names. Needs RuntimeUnityEditor before relying on it.

#### 1. Seeded from 3rdPovPlus (CONFIRMED there)

Full detail in D:\Code\3rdPov\FINDINGS.md.

**Where to run per-frame code:** a Harmony postfix on `SV.SimulationScene.Update`.
It is the free-roam simulation scene; ADV (dialogue), H and menus run on top of it.

**Getting things:**
- Player: `SV.GameChara.PlayerAI` (an `SV.Chara.AI`). Null outside the simulation.
- All characters: `SV.Game.AICharas`.
- Map manager: `SingletonInitializerAsync<Manager.MapManager>.Instance`.
- Current map id: `playerAI.BehaviourCtrl.NowMapID` (the manager's `MapID` lags).
- 2D maps, where there is no world to walk in: `mapManager.Is2DMap(mapManager.MapID)`
  or `mapInfo.IsUseTimezone2D`, with `mapInfo` from `mapManager.MapListTable`.

**The game's own click handlers on `SimulationScene`** (all ordinary methods, so a
prefix returning false skips them):
- `CursorTargetSelect()` — selects whatever is under the cursor on left click.
- `WheelTargetSelect()` — mouse wheel cycles through characters.
- `MouseMiddleClick()` — walks to the marked character.

**Walking the player somewhere, the game's way:**
- `scene.SetTarget(ai)` walks the player to a character, pathing and all.
- `scene.MoveStop()` cancels that walk.
- `playerAI.BehaviourCtrl.Stop(true)` stops movement and blends to idle.
- A marked character has `ai.objCircle.active == true` (the blue ring).

**Moving the player by hand** (what 3rdPovPlus does for WASD, in
`ThirdPersonController.MovePlayer`; fine to reuse, credit Junh2x):
`BehaviourCtrl.accel`, `SetSpeed(float, bool)`, `SetSpeedRate()`,
`AnimRunAndWalk(bool isRun)` for the animation, then translate `playerAI.transform`
and set its `forward`. `playerAI.chaCtrl.gameObject.transform.localPosition` must be
zeroed, or the body drifts from the AI root. Stamina is
`playerAI.charaData.charasGameParam._baseParameter_k__BackingField.NowStamina`.
This bypasses pathing: nothing stops it walking into walls except colliders.

**When not to act** — 3rdPovPlus's `IsAnyMenuOpen`: ADV (`ADV.ADVManager.IsADV`),
H (`adv.IsHScene`, or `SV.H.HScene` active), `Scene.IsOverlap`, and the `IsOpen()` of
MyRoom, MapSelect, CharaSelect, CoordeSelect and CorrelationDiagram singletons.

**Doorways and job spots:** `SV.SVNodeLink2` (startMapID → endMapID) and
`mapManager.pointInfoTable[map].pointList.pcTable[job].points`.

#### 2. Pathfinding (SIGNATURE, seen 2026-09-27)

The game uses the **A\* Pathfinding Project** (Aron Granberg), not Unity's NavMesh.
Assembly: `AstarPathfindingProject.dll` in the interop folder.

`SV.BehaviourController` (the player's is `playerAI.BehaviourCtrl`) has:
- `Seeker seeker` — A*'s path requester. The usual API is
  `seeker.StartPath(Vector3 start, Vector3 end, OnPathDelegate callback)`.
- `OnPathComplete(Pathfinding.Path p, bool _isDestory)` — the game's own callback.
- `List<Vector3> vPaths` — probably the waypoint list being followed. UNVERIFIED.
- `RVOController rvoCtrl` — local avoidance between characters.
- `TraverseOffMeshLink(RichSpecial)` — off-mesh link handling for the mover, which
  is indeed a RichAI (`SVRichAI`, see §2a).
- `TargetInfo target` — where it is heading: `transform`, `pInfo` (MovePointInfo),
  `kind` (TargetKind), `id`, `type`, `job`, `IsMap`; set with
  `Set(Transform, MovePointInfo, TargetKind, int id)` or
  `SetMap(MovePointInfo, int id, int type, int job)`.
- `Stop(bool)`, `StopMoveOnly()`, `IsStoped`, `speed`, `nowSpeed`, `SetSpeed(...)`,
  `RunOrWalk(bool isPC, int actionNo, bool isRun)`.

`SV.NavMeshInfo` and `SV.NavMeshInfoParam` exist; `MapManager.NavMeshInfoTable` and
`LoadNavMeshInfo(bundles)` suggest they describe which nav graph data each map loads.

#### 2a. How the game walks a character (seen 2026-09-27)

Sources: dnSpy signatures (SIGNATURE), and the **behaviour trees read straight out of
`abdata\action\prefabs\base\000_00.unity3d`** — those are the game's real data, not
guesses, so the tree structure below is reliable. What each task does *inside* is
still inferred from names and Japanese comments until tested.

##### The pieces, all on the character prefab's root object `AI`

`Seeker` + `FunnelModifier` + `SimpleSmoothModifier` + **`SVRichAI`** + `RVOController`
+ `BehaviourController` + `SV.Chara.AI`, with a child `BehaviorTree`
(`SV.BehaviorTreeCtrl`) holding one `SVBehaviorTree` per mode.

- **`SVRichAI : Pathfinding.RichAI : AIBase`** is the mover — it follows a path over
  the nav mesh, handles off-mesh links (doorways are `SVNodeLink2`, a NodeLink2) and
  wall avoidance. Reached as `BehaviourCtrl.SVRichAI`. Prefab settings: `maxSpeed 6`,
  `acceleration 6`, `endReachedDistance 0.3`, `radius 0.2`, `canMove` and
  `canSearch` on, `autoRepath` mode 2 (dynamic) every 0.5–2 s. Base API present:
  `destination`, `SearchPath()`, `isStopped`, `canMove`, `canSearch`,
  `reachedDestination`, `remainingDistance`, `pathPending`, `Teleport()`.
- **The game modified its copy of A\*** — `OnPathComplete(Path, bool _isDestory)` is
  not stock. Don't assume stock A* behaviour anywhere without testing.
- **Behavior Designer** (Opsive) drives characters. `BehaviorTreeCtrl.ChangeMode(
  Manager.Game.ActionKind)` (89 callers) switches tree; `ActionMode` reads it;
  `GetNowTask().friendlyName` names the running node.
  `ActionKind`: Idle 0, Thinking 1, Contact 2, **Move 3**, UroUroMove 4, LookAt 5,
  BathFirstHalf 6, BathSecondHalf 7, TitleMove 8, EveryOneWait 9.

##### The trees (read from the bundle's `mBehaviorSource` JSON)

```
Move                                     UroUroMove ("wander")
Selector                                 Selector
  Sequence: SVIsReaction -> Idle           Sequence: SVIsReaction -> Idle
  Sequence: SVIsStop -> SVRVOStop          Sequence: SVRVOTargetMove -> SVUroUroMoveToDo
  Sequence: SVRVOTargetMove  "move to            -> SVLookAtThePerson -> SVUroUroLookAtToDo
              target"                      SVRVOStop
            -> SVMoveToDo  "on arrival,
              decide what to do"         Thinking
  SVRVOStop                              Selector
                                           SVIsPC  "PC must not think, unless Auto"
Idle                                       SVThinking  "decide where to go / whom / what"
Selector                                   Idle
  Sequence: SVIdleIsThinking -> SVIdleSetMotion
            -> SVIdleWait -> SVIdleToDo ("move to thinking")
  SVIdleReaction
```

So **every walk is the same Move tree**: something fills in `BehaviourCtrl.target`,
switches to Move, `SVRVOTargetMove` steers `SVRichAI` there, and `SVMoveToDo` decides
what happens on arrival (`PersonalProc` for map points, `InterpersonalProc` for
characters — method names on SVMoveToDo). The `SVIsPC` gate means the **player
never picks a new destination by itself** after arriving, unless in Auto mode.

##### The entry points that fill in the target

| Call | What it is for | Callers |
|---|---|---|
| `SimulationScene.SetTarget(AI)` | walk to a character (then talk) | 2 |
| `MapManager.SetCharaMapMove(BaseActionKind, BehaviourController, MapTargetInfo, bool)` | **walk to a map point** — static, the central routine | 45 |
| `MapManager.UroUroPointMove(bctrl, mapID, job = -1, poses = null)` | pick a random wander point in `mapID` and walk there. 3rdPovPlus's `TakeDoorway` uses it for the player | 2 |
| `bctrl.target.SetMap(MovePointInfo, mapID, type, job)` | rewrite a target in place; MapLoader and CustomLogic use it on NPCs from an `SVThinking.OnUpdate` postfix | 6 |
| `bctrl.target.Set(Transform, MovePointInfo, TargetKind, id)` | lower level form | 33 |

- `MapManager.MapTargetInfo`: `pInfo` (MovePointInfo), `map`, `type`, `job`,
  `pointIndx`; `SetTypeJob(type, job)`, `SetUrouroTypeJob()`.
- `TargetInfo.TargetKind`: None -1, **Map 0**, Chara 1.
  `BaseActionKind`: **Personal 0**, Interpersonal 1.
- `MovePointInfo.TypeKind` (the `type` int): **Urouro 0**, Solo 1, With 2, Everyone 3,
  PC 4. `JobKind` (the `job` int): **None -1**, Meal 0, Study 1, Motion 2, Job 3,
  Bath 4, HouseParty 5, ChangeClothes 6, Shopping 7, Zizo 8, H 9.
- **`MovePointInfo` is a MonoBehaviour** — a marker object placed in the map. Its
  per-job data (`urouroDetails`, `pcDetails`, …) says what animation to play there.
  Because it is a component, one can be created at any position at runtime.

##### The plan this suggests (UNVERIFIED — tests in progress)

1. **Preferred:** a temporary GameObject with a `MovePointInfo` at the clicked point,
   passed to `SetCharaMapMove(Personal, playerBctrl, {pInfo, map = NowMapID,
   type 0 Urouro, job -1 None})`. That is the same shape as the walk the game
   already does for the player via `UroUroPointMove(..., job -1)`, so arrival
   should end in plain Idle. Risk: something may read the empty detail lists or
   `pointIndx` of our fake point.
2. Fallback: `target.Set(ourTransform, ourPointInfo, TargetKind.Map, NowMapID)` then
   `BehaviorTreeCtrl.ChangeMode(Move)`.
3. Last resort: `SVRichAI.destination = p; SearchPath();` directly — bypasses the
   tree, so animation and arrival handling are ours to do.

##### Test round 1 (RUE, overview camera, 3rdPovPlus off)

- **CONFIRMED:** the player standing still in free roam reads
  `Move | task=StopAction | kind=None id=-1`, not `Idle`. The PC sits in the Move tree
  with no target and the StopSequence running. `IsStoped` was false.
- **CONFIRMED:** `SetCharaMapMove(Personal, bctrl, {pInfo = new MovePointInfo at p,
  map = NowMapID, type 0, job -1})` makes the player walk **with proper walk/run
  animation**, apparently pathing normally (Station map).
- **But not to our point:** they walked far off to the side, out of the camera, not
  6 m ahead. Suspects: `pointIndx` (left at 0) picks one of the map's own points,
  or GetNearest snapped somewhere unexpected. Not yet known.
- Repeating it on another map failed with errors, and so did the `target.Set` +
  `ChangeMode(Move)` fallback. The error text is shown only in the RUE REPL window,
  not in LogOutput.log. Stale `go`/`mpi` from the previous map are a suspect.
##### Test round 2 (RUE, Station, standing where the game spawned the player)

- Freshly spawned the player reads `mode=Idle task=SV Idle Reaction kind=Map
  tgt="Target (4)"` with **`SVRichAI.isStopped = True`**. (Round 1's "Move/StopAction"
  was after having walked.) Station has `AstarPath.active.graphs.Length == 2`; a point
  3 m ahead snapped by 0 m.
- **CONFIRMED: setting `SVRichAI.destination` + `SearchPath()` directly does nothing**
  while idle: the walker is held stopped by the game. Going around the tree is out.
- **CONFIRMED: `SetCharaMapMove` with our own `MovePointInfo` works.** The target became
  our `WA_Target`, the RichAI destination our point, and the player walked the 3 m there.
  `pointIndx` left at its default 0 did not matter. **This is the method the plugin uses.**
- Round 1's walk "far to the right" is unexplained; most likely a bad point, not the
  method. Watch for it in the plugin's click log.

- Requirement from the developer: must work on **2D maps too**. The 2D picture is only
  a background; the characters still stand in a 3D space.

Snapping a point onto the nav mesh: `AstarPath.active.GetNearest(Vector3)` returns
`NNInfo` with `.position` (and `.node`). SIGNATURE.

##### Other plugins touching movement

Byte-searching the installed plugins for these API names: only SVS_3rdPovPlus
(ours) and **SVS_CheatTools**, which postfixes `AIBase.FixedUpdate` to change
`SVRichAI.maxSpeed/acceleration/accelType/slowSpeed` for its speed cheat. A
click-walk will simply inherit that speed; no conflict.

##### Plugin v0.1.0 in play (CONFIRMED 2026-09-27, several 2D and 3D maps, with 3rdPovPlus)

Click-to-walk works on every map tried, 2D included, and alongside 3rdPovPlus.
From 226 logged walks:
- Ground clicks hit **layer 9 `Ground`** (objects `nav_Plane_map000`,
  `nav_Plane_map000_noH` — the walkable surfaces themselves) or **layer 8 `Map`**
  (scenery, e.g. `Plane (2)`). 32 clicks hit nothing (2D maps) and the
  player's-floor fallback worked.
- Snapping moved some points **16–33 m** (clicks on scenery or background far from any
  walkable area). Candidate setting: a maximum snap distance.
- 53 clicks were correctly ignored as over UI, 1 as on a character, 5 because the
  ray pointed above the horizon.

##### Walk states, from the v0.1.0 trace (CONFIRMED)

Walking to our marker: `mode=Move task=MoveAction target=Map:WalkAnywhere_Target`.
Arrived: `mode=Idle` (task `Idle Wait` or none), **target still our marker**. A null
task between nodes is common (`mode=Move task=`). Travel buttons give the player
`Map:Target (N)` (the game's own points); clicking a character gives
`Chara:<name>_AI(Clone)`.

#### 2c. Following a character (v0.2.0, UNVERIFIED in play)

No "player follows an NPC" routine exists. The game's chase pieces
(`BehaviourController.chaseBehaviourCtrl`, `SetSpeed(bool _isChase)`,
`SVRVOTargetMove.ChaseProc`, `CharactersGameParameter.isChase`,
`AI.isChaseMaintain`) serve NPCs following the player and have stubbed bodies.
So `Follower` re-uses the click walk: every 0.2 s it aims at a point Min Distance
from the NPC on the player's side, with `MapTargetInfo.map` = **the NPC's map** (how
travel buttons cross into another map). While a walk is under way it only moves the
marker, and re-issues the walk if the walker's `destination` doesn't follow.

**Speed.** `abdata\action\list\movespeed\000_00.unity3d` holds TextAssets
`movespeedwalk` / `movespeedrun`: 7 rows of 9 little-endian floats (row id then
3×3). Walk row 0: 0.5 0.9 2.3 | 0.6 0.95 2.5 | 0.6 1.0 2.6; run row 0: 1.4 2.2 6.0 |
1.6 2.8 7.5 | 1.8 3.5 9.0. Meaning of the columns unknown; walk looks ≈0.5–1.1 m/s and
run ≈1.4–3.5 m/s, so "running" is guessed at > 1.3 m/s measured.
- WalkOrRun: `BehaviourCtrl.AnimRunAndWalk(bool)` (3rdPovPlus uses it).
- Exact: SVS_CheatTools' lever: in an `AIBase.FixedUpdate` postfix set
  `SVRichAI.accelType = 1` (use maxSpeed as given; 0 = the game's own speed logic),
  `maxSpeed` and `slowSpeed`. Defaults to restore: accelType 0, maxSpeed 6,
  acceleration 6, slowSpeed 1 (we restore what we saved instead).

##### v0.2.0 in play: what went wrong (CONFIRMED from the [follow] log)

- **NPCs walk at about 1.5–1.8 m/s** (measured from position change; 581 samples,
  almost nothing above 2.2). The 1.3 m/s run threshold guessed from the tables was
  wrong: every walking NPC counted as running.
- **Aiming at a point Min Distance away made the player arrive, stop and get re-sent
  every few ticks** (95 re-issues in one map; player speed 0 → 4 → 0 → 5). Each
  `SetCharaMapMove` restart re-picks the game's speed and the running animation, which
  is why the player always ran and hugged Min Distance whatever Max was.
- Moving only the marker during a walk often did not move the walker's destination
  (27 "did not follow" restarts).
- **Exact speed (accelType 1 + maxSpeed) did match the NPC's speed well** — the
  developer's observation — while the animation stayed on running.
- NPCs react to a player who gets very close (`mode=Contact task=Contact_Look`), which
  can open a conversation. Keeping a minimum distance matters.

##### v0.3.0 design

One walk, issued once, then steered: `SVRichAI.destination` set directly each tick
(accepted while the walker is moving) plus `SearchPath()` when the goal moved >0.5 m.
Re-issued only on a map change or after the walk has looked over for 0.5 s. Waiting
uses `BehaviourCtrl.Stop(true)`. Distances Minimum / Ideal / Run with catch-up
hysteresis (Run → Ideal). Modes WalkOrRun (game speeds, `AnimRunAndWalk` only),
MatchSpeed, Dynamic (speed = theirs + 0.8 × (d − Ideal), eased at 4 m/s²); the last two
choose the animation from the actual speed with a threshold (default 2.2 m/s).
UNVERIFIED: whether `AnimRunAndWalk(false)` alone makes WalkOrRun walk at walking speed.

#### 2b. Harmony trap: PatchAll(assembly) silently skipped our hook (CONFIRMED)

The skeleton called `_harmony.PatchAll(typeof(Plugin).Assembly)`. That form only
processes classes that carry a **class-level** `[HarmonyPatch]`; a static class whose
methods each have `[HarmonyPatch(typeof(X), nameof(X.Y))]` is skipped with no error.
The plugin loaded, logged "loaded", and never ran. `PatchAll(typeof(Hooks))` (as
3rdPovPlus does) applies method-level attributes. The load line now reports how
many game methods were patched, so a zero is visible in the log.

#### 3. Open questions

- Which physics layers are walkable ground? 3rdPovPlus writes layer names to the log
  at startup; characters share the `Default` layer with scenery (3rdPov §13).
- Does `GetNearest` restricted to the current map's graph matter (the town may be one
  graph spanning several maps, joined by `SVNodeLink2` links)?
- Whether a click-started walk plays the proper walk/run animation (expected yes, if
  it goes through the Move tree).
- What `SVMoveToDo.PersonalProc` does for the PC on arrival with job -1.
- What SVS_PovX does to movement.

---

## 26. The rename to SVS_FreeRoam (2026-10-02)

- GUID, name, assembly and DLL are `SVS_FreeRoam`; project folder `SVS_FreeRoam\`,
  solution `SVS_FreeRoam.sln`, namespace `SVS_FreeRoam`. The repository folder is
  still D:\Code\3rdPov.
- Config: `SVS_FreeRoam.cfg`, copied once from `SVS_3rdPovPlus.cfg` (the working name,
  never released) on first run. Orphaned lighting entries in it are harmless.
- **Junh2x's SVS_3rdPov is no longer declared incompatible** (that made BepInEx skip
  *this* plugin). It is a soft dependency so it loads first;
  `IL2CPPChainloader.Instance.Plugins.ContainsKey("SVS_3rdPov")` is checked in `Load`,
  and if present this plugin stands down every frame (as if disabled), logs a warning,
  and shows a 20-second line on screen saying it is the reworked version and the
  original should be removed. UNVERIFIED in game.
- SVS_WalkAnywhere installed alongside is only warned about in the log.

**Changed the same day: the original is switched off, not us.** Junh2x's plugin applies
its patches with `Harmony.CreateAndPatchAll(typeof(Hooks), null)` (a random Harmony ID,
kept in a static field `patchedHooks`), and its `Unload()` calls `UnpatchSelf()` on it.
So in `Load`, `IL2CPPChainloader.Instance.Plugins["SVS_3rdPov"].Instance` is cast to
`BasePlugin` and `Unload()` is called: it stays loaded but has no patches left, which is
all it ever did (one `SimulationScene.Update` postfix, two prefixes). The player is told
once, on screen for 20 s and in the log. Only if that throws does this plugin stand down
instead. UNVERIFIED in game.

## 27. Gamepad layout and button mode, round 2 (2026-10-02)

- **Extended Gamepad Support** (was Gamepad Support, now on by default). The game's own
  legacy input reads the left stick as Horizontal/Vertical, so walking with it works
  even with the option off. XInput is read once per frame when a controller is
  connected (microseconds) and the four slots are scanned every 2 s when none is; the
  option costs next to nothing.
- **Layout**: A Select, B Back, X Talk (characters only), Y Interact (doorways and
  activity spots only), LB PoV toggle, RB sprint, Start options, Back/View a chosen map
  screen (`btnMap`, `btnChara`, `btnEveryone`, `btnOption`, `btnHelp`, `btnGotoRoom`),
  left stick click crouch, right stick click reset view. All are KeyCode entries read
  through Unity's input (Joystick Button 0..9 on an Xbox pad); the D-pad is XInput and
  not rebindable. Configs holding the old defaults (interact on A, toggle on the left
  stick click) are moved once, tracked by a hidden `Hotkeys > Gamepad Layout` = 2.
- Third person's interact takes a `TargetFilter` (Any / People / Places), so X and Y
  each find the nearest of their kind; the keyboard's interact still finds either.
- **Nothing is selected by itself**, conversation choices included (a mouse player saw
  a frame on the first choice). The first D-pad, stick or Select press picks the middle
  choice (nearest the centroid), the overworld's btnMap, or a screen's top button.
- **Back folds a list away**: a press that makes new buttons appear records the pressed
  button as the list's parent; Back while on one of those buttons presses the parent
  again (btnEveryOne toggles its list) and selects it. Generic, so conversation
  sub-lists get the same treatment; UNVERIFIED there.
- **Jizo screen** (`SV.CorrelationDiagramScene.CorrelationDiagram`, `_isOpen`,
  `_btnBack`): its characters are `CorrelationUI`, a subclass of `SexualTargetUI`, so
  they join the candidates exactly like Meet Up's portraits.
- **F2 list** is `SV.ShortcutViewDialog` (static `IsActive`, instance `OnBack()`); Back
  closes it.
- **Hidden buttons excluded**: a Selectable under a disabled `Canvas`, under 2 px on
  screen, or with no visible `Graphic` (enabled, colour alpha > 0.01, CanvasRenderer not
  culled and alpha > 0.01) is not a candidate. This is for the modded btn_Switch, which
  stayed selectable while not drawn. UNVERIFIED which of these hides it.
- **Follow gives way to someone coming to talk**: an NPC on the same map within 6 m
  whose `BehaviourController.targetBehaviourCtrl` is the player's stops the follow and
  halts the player, so the game's approach can finish into a conversation instead of
  the NPC standing stuck. UNVERIFIED.
- Click-walk's per-state `[trace]` and follow's once-a-second `[follow]` log lines are
  gone; only event lines remain, behind Testing Info. Max Snap Distance defaults to 3.

**Round 3 (same day).**
- Choices: the first D-pad/stick press highlights the choice furthest that way (right:
  the right-hand one); Select highlights the middle one; a second Select picks it.
- **Screen cycling (UNVERIFIED)**: LB / RB (Gamepad Previous/Next Screen Button) on Map
  Select, Meet Up, options (F1), shortcuts (F2) or help (F3) closes it and opens the
  next. Open checks: `ShortcutViewDialog.IsActive`, `SV.Config.ConfigWindow.IsActive`,
  `SV.HelpWindow.IsActive` (all static), `MapSelect.IsOpen()`, `CharaSelect.IsOpen()`.
  Opening: btnMap, btnChara, btnOption (else static `ConfigWindow.Load()`),
  `ShortcutViewDialog.Load()`, btnHelp (else `HelpWindow.Load()`). The F keys go
  through `SV.ShortcutKey._OpenConfig/_OpenShortcutKey/_OpenTutorial`. The PoV toggle
  ignores LB while one of these screens is up.
- **btn_Switch** comes from **SVS_CustomGameBalance** (`CGBSwitchButton`), created
  under `Canvas/MainCanvas/GameCanvas/CharaInfo`. Its `CustomGameFunctions
  .SwitchPCCharacter` switches to the character whose `objCircle` **and**
  `particleCircles` are active — the marked one. Third person keeps CharaInfo switched
  on and fills it with the *aimed* character, so the button showed one character and
  switched to another (or none). `SwitchButtonCompat` prefixes that method (found
  through the loaded plugin's assembly, once all plugins are up) and marks the aimed
  character first, in third person only. UNVERIFIED. `ThirdPersonController
  .AimedCharacter` is the last character aimed at while third person runs.
- btn_Switch selected while hidden: it is a child of CharaInfo, which third person keeps
  active; it is now a candidate only while something is aimed at. The Canvas/size/
  visible-graphic checks from round 2 were removed (they did not catch it).
- **Fade Between Maps (UNVERIFIED)**: `MapManager.ChangeMap(..., FadeCanvas.Fade fade, ...)`
  takes a fade (IL.dll: `FadeCanvas.Fade` None 0, In 1, Out 2, InOut 3; the scene's is
  `Manager.Scene.SceneFadeCanvas`, a `FadeCanvas`, which SVS_FadeController recolours).
  `TakeDoorway` passed None; it now passes InOut, and `LoadCollisionAndPoint(0)` waits
  until `mapManager.MapID` is the new map (5 s at most), in case the change waits for
  the fade. If doorways or activity spots misbehave after a crossing, this is the first
  suspect: turn the setting off.
- **Fade Between Maps** is now a dropdown: No Fade, Fade To White (default), Fade To
  Black. The scene fade is one shared overlay (`Manager.Scene.SceneFadeCanvas`,
  `fadeImage`); a crossing tints the image directly (SVS_FadeController's prefix on
  `SetColor` would override a colour passed that way) and restores the previous colour
  once `Scene.IsFadeNow` is false, so location-button travel keeps its own colour.
- The gamepad frame and the notice overlay are no longer `DontDestroyOnLoad`: on return
  to the title nothing ticks to hide them, so they stayed on screen. They are recreated
  when next needed.
- Gamepad Shortcut Button closes the open screen when pressed again (like Start).
- Setting descriptions rewritten for players (short, no internals).
- Right click to follow: only a press made with the cursor free counts, and while the
  cursor is free the follow button no longer also fires interact (the walk to the aimed
  character).

## 28. Click To Idle (2026-10-03)

**Where idle spots and their animations live (CONFIRMED from interop signatures).**
- Each map's `PointList.urouroTable` (`Dictionary<int job, ListInfo>`, `ListInfo.points`)
  holds the "urouro" (wander) points: the spots the game sends a character to on
  entering a map (`MapManager.UroUroPointMove(bctrl, mapID, job -1, poses)`).
  Reached as `mapManager.pointInfoTable[mapId].pointList`.
- A point is a `MovePointInfo`: `poses` (`PoseKind` Stand 0, Ground 1, Chair 2, Desk 3),
  and `urouroDetails` (`JobDetail`: `job`, `animations` = list of `AnimationInfo`
  {`weight`, `animMotion`, `isAddH`}, `charactorOffset` (where the body is put, e.g. on
  the seat), `moveObjectName` / `moveObject` (a prop that is moved)).
- The game picks the animation itself: `MovePointInfo.GetAnimationID(type, job,
  StateParameter.StateKind state, isWithPair, OnesPropertyInfo[] onesProperty)`. So the
  choice depends on the character's **mood state** (UPLIFT ... NORMAL) and **traits**
  (`charasGameParam.onesPropertys`), weighted. Sex is `Human.sex` (byte), personality
  `HumanDataParameter.personality`; neither is an argument, so any male/female split
  is in what `animMotion` resolves to. UNVERIFIED which.

**Round 1 (UNVERIFIED in game).** `ClickIdler.cs`, section "Click To Idle".
- Click near a urouro point (any job key in the table, nearest within Max Distance To
  Spot): `SetCharaMapMove(Personal, bctrl, {pInfo = that real point, map, type 0,
  job = its table key})` -- the walk click-to-walk already does, but to the game's own
  point, so arrival should play its animation.
- Click on yourself (judged on screen, feet to head): our walk marker is put at the
  player's feet "wearing" the `poses` and `urouroDetails` of a random standing point of
  the map that has animations and no offset or prop (`Walker.IdleAt`, `Dress`). Open
  questions: whether a zero-length walk reaches the arrival step at all, and whether
  the animation is read from `pInfo` at arrival.
- With Debug Info on, each idle click logs the point: name, position, poses, and per
  detail the job and `animMotion x weight` list.

**Settings window order (CONFIRMED from ConfigurationManager's IL).** Categories are
ordered by first appearance, i.e. the order the entries are bound, then by name.
Debug Info is therefore bound last.

**Round 1 result (developer, 2026-10-03).** Walking to a real urouro point works: the
character sits on chairs and on the ground (beach). CONFIRMED. The marker "wearing" a
standing point's details also plays its animations. CONFIRMED. Wrong: right click near a
standing spot walked there like a left click; clicks on props often missed, because most
props have no collider and the ray lands on a plane under the floor (`'Plane' layer 8`,
y -3.04), metres past the prop.

**The animation ids are named (CONFIRMED from interop).** `animMotion` is a value of
`SV.AnimationCtrlManager.Animation`: stand 0, run 1, walk 3, floor_wait 8, chair_wait 9,
desk_wait 10, waiting_action_0..3 13-16, exercise 17-19, dumbbell 20, meal_* 21-23,
work_stand 24, work_chair 25, smart_phone_stand/chair/desk 26-28, bookread_stand/chair
29-30, erotic_book_stand/chair 31-32, game_stand/chair 34-35, study_desk 36-37,
job_* 45-52, then paired ones (stroke/hug/kiss/touch by sex). The classroom's spots:
Stand = 0 x8, 13, 15, 24, 26, 29, 31, 34 (+ activity 3: 51, 52); Chair = 9 x8, 14, 16,
25, 27, 30, 32, 35; Desk = 10 x8, 28. So the suffix (or the spot's `PoseKind`) is the
type, and the heavily weighted first entry is the plain waiting pose.
- `AnimationCtrlManager` (singleton): `animTable` (id -> list of `AnimStateInfo`
  {hash, name}; probably where sex or variants split, UNVERIFIED), `itemTable` (id ->
  held items), `idlePtnIDs`, `posePtnChairIDs`, `posePtnDeskIDs`, `IsChair(bctrl)`,
  `GetNowPtnID(animator)`, `SetAnim(bctrl, animator, actor, ptn, ...)`.
- `SV.Chara.Base.SetLowpolyAnimation(animID, isLockFlagChange, isBlend,
  fixedTransitionDuration, isAnimForceChange)` plays one on a character directly.

**Round 2 (UNVERIFIED).**
- Right click acts only on yourself: click = random animation (`SetLowpolyAnimation(id,
  false, true, 0.25, true)`, no walk at all), hold 0.35 s = `IdleWheel`.
- Wheel contents (`ClickIdler.Choices`): at a seat (target `pInfo` is a non-standing
  real point within 1 m) that point's animations; otherwise every animation of the
  map's standing spots, all activities.
- Left click (`ClickWalker`): "Use Seats And Special Spots" picks a non-standing point by
  the distance from the mouse ray to the seat (`charactorOffset`, +0.4 m), within
  "Spot Click Size"; no collider needed. "Idle Animation On Arrival" builds a
  `JobDetail` for our marker from the standing ids minus 0 (`Walker.Dress`).
- Third person (`ClickIdler.ThirdPerson`, "Use Spots In Third Person"): with nothing
  else in reach, a seat within 1.5 m is named in the target label; releasing interact
  uses it, holding opens the wheel, steered by mouse movement while `CameraRig` holds
  the view still. No stick steering for the wheel yet.
- With Debug Info on, each map logs once "Idle: animations on map N" with every kind of
  spot and its named animations.

**Round 3 (UNVERIFIED; round 2 was committed without a detailed report).**
- A tap while our animation is still playing (`AnimationCtrlManager.IsPlayMotion(bctrl,
  id)`) returns to the resting pose: the seat's first listed animation, else stand 0.
- Third person: the idle key is its own setting (Mouse2), free only while no walk is
  under way and nobody is marked, since Go To Marked Key is Mouse2 too. Seats use
  interact on press. The seat marker is optional, off by default.
- Extended Animations: the wheel adds every `AnimationCtrlManager.Animation` below 1000
  whose name has no `_f_`/`_m_` (paired) and is not run/escape/walk*.
- `IdleWheel` is a general radial menu now: 8 fixed slots, pages turned with the mouse
  wheel, disc and highlight wedge drawn into `Texture2D`s at run time
  (`SetPixels32` + `Sprite.Create`), no image files.
- Character wheel: holding the idle button on another character offers Talk
  (`WalkToCharacter`), Follow / Stop following, and Switch to when SVS_CustomGameBalance
  is there (`CustomGameFunctions.SwitchPCCharacter()` is static with no arguments; it is
  invoked by reflection after our prefix marks the wanted character). Follower ignores
  the release that closed a wheel (`ClickIdler.WheelClosedFrame`).
- Asked for, not built: Ignore, remove from the game, replace with another character
  (the first belongs to a plugin that does not exist yet).

**Round 3 result (developer, 2026-10-04).** The wheel, pages and direct play
(`SetLowpolyAnimation`) work. CONFIRMED. Interact-to-sit worked; a random animation
played next to a chair sat the character badly placed and facing the wrong way.

**Round 4 (UNVERIFIED).**
- Third person: interact no longer sits. The idle key's tap, in order: stop our
  animation; get up from the seat (`Walker.WalkTo` the nav point nearest the seat's
  base, the way a click-walk leaves a seat); use the seat within reach; random animation.
- A wheel with more than one page stays open when the button is let go; the next
  press chooses (`ClickIdler._sticky`). Click-walk is off while a wheel is open, and
  Follower ignores the press that chose.
- "Wheel Size" (4-16 slots). Disc 1024 px with mip maps and edges softened in the
  drawing itself; the 256 px one upscaled was the pixelated outline.

## 29. Depth of field in third person (2026-10-04)

- The game's depth of field is **Beautify** (`Beautify.Universal.Beautify`, a URP volume
  component in `Beautify.Universal.Runtime.dll`), switched by
  `SV.Config.GraphicSystem.DepthOfField`. Parameters: `depthOfField`,
  `depthOfFieldFocusMode` (FixedDistance 0, AutoFocus 1, FollowTarget 2),
  `depthOfFieldDistance`, `depthOfFieldFocalLength`, `depthOfFieldAperture`;
  `BeautifySettings.depthOfFieldTarget` for FollowTarget. `CameraControl` has
  `ChangeDepthOfFieldSetting` / `UpdateDepthOfFieldSetting` (stubs to us).
  `UnityStandardAssets.ImageEffects.DepthOfField` also exists (ADV backup code) but is a
  built-in-pipeline effect. LIKELY unused.
- Reported: with it on, characters near the third-person camera are blurred. The old
  "Hide Blur" option was something else (`HighPolyBackGroundFrame.mainCamera`).
- `PovFocus` (UNVERIFIED): while third person runs, every Beautify component found in
  the scene's volumes gets focus mode FixedDistance at the camera-to-player distance
  (at least 1.2 m), or `depthOfField` false with the Off choice; the game's values are
  put back when third person ends. With Debug Info on it logs each volume's values
  once ("Depth of field: volume ..."): if the fix does nothing, that line says whether
  the game uses a volume at all and with which focus mode.

**Report from a player (unresolved):** "F4 only hides the UI, the camera does not
change", after uninstalling an old plugin called ThirdPOV. Not reproduced. `CameraRig
.Place` now warns once in the log when the camera is somewhere else on the next frame
for 120 frames running, i.e. another plugin is moving it. Their LogOutput.log is needed.

**Round 4 result (developer, 2026-10-04), and round 5 (UNVERIFIED).**
- Sitting by a tap in third person worked but sometimes left the walk animation running
  on the way to the seat (third person steers the player by hand while the game's walk
  runs; the same as at doorways). Now the player is put on the point first
  (`playerAI.position = spot.transform.position`, then the same `SetCharaMapMove`), and
  the reach is 1 m.
- The wheel hitched the first time on each map: its textures were redrawn whenever the
  scene's canvas was rebuilt, at 1024 px. They are drawn once now, 512 px, kept with
  `HideFlags.HideAndDontSave`. The highlight is one ring sprite on a Filled / Radial360
  `Image` (`fillAmount = 1 / choices`), so any number of choices per page works and a
  page's choices always share the whole circle.
- The wheel stays open after release only in third person with more than one page (and
  for the favourites wheel opened by a tap). A left click always chooses;
  `ClickIdler.BlocksInput` keeps that click from walking, interacting or click-walking
  until every mouse button is up.
- Favourites: names in the "Favourite Animations" setting, toggled with the Favourite Key
  on the wheel. "Tap Plays": random / favourites wheel / one chosen animation.
- "Animation Props And Effects": plays through `AnimationCtrlManager.SetAnim(bctrl,
  animator, actor, id, 0.25, force true, lockFlag false, blend true)` and
  `SetItemVisible(bctrl)`, falling back to `SetLowpolyAnimation`. Whether this adds props
  or sounds over the plain call is UNVERIFIED; no separate sound call for map
  animations was found in the interop (sounds may be animation events on the clips).
- **Depth of field values per map (CONFIRMED from the log):** each map has a global
  volume with Beautify, all FixedDistance: distance 10.1 / focal length 0.011 /
  aperture 67.85 ('Global Volume_m00'); 9 / 0.15 / 4; 8.34 / 0.308 / 0.9. A base
  'Global Volume' (priority -1) has it off. So the look differs a lot by map.
  `PovFocus` now scales the aperture by (new distance - f) / (map's distance - f), which
  keeps the far background as blurred as the map's own setting does, times "Third
  Person Blur Strength"; rescans on a map change; in first person focuses on the aimed
  character or switches the blur off; and stands down under menus and conversations.

**Round 5 result (developer, 2026-10-04), and round 6 (UNVERIFIED).**
- Instant sitting, the wheel, favorites and the per-map depth of field work. CONFIRMED.
- **Voices.** NPCs acting on their own speak during some animations (dumbbell and the
  exercise ones at the shrine); the player never did, and characters invited to an
  activity do not either. Those NPCs carry an `ObservableDestroyTrigger` (UniRx).
  `SV.LowpolyActionVoiceManager` (singleton): `infoTable` (by animation state),
  `oldAnimationTable`, `LowpolyVoiceProc(AI)`, `LowpolyVoicePlay(id, AI)`. `ClickIdler
  .Voice` now calls `LowpolyVoiceProc(playerAI)` every frame while an animation we
  started is playing. UNVERIFIED that this is the call the game makes for NPCs.
- **The invisible-characters bug, explained.** Under a menu or an activity scene third
  person hides the player and their companions (`ApplyCharacterVisibility`,
  `_visibleAll` false). Switching to the overview camera in the middle of the scene
  stopped that code, so nobody was shown again until third person ran once more or the
  map changed. `Disable` now calls `ShowEveryone` on the way out of third person.
- A 1 m reach lost the seats behind desks (cafe, classroom): the point cannot be walked
  up to that closely. Back to 1.5 m; the player is still put on the point, so no walk.
- Tap Plays "Random Favorite" plays one (not a wheel); standing, animations made for a
  chair or desk are left out (`AnimationCtrlManager.posePtnChairIDs` / `posePtnDeskIDs`).
- Settings: "Crouch Key Function" (Toggle / Double Tap / While Pressed) replaces
  Double-Tap To Crouch; double-tap sprint and double-tap walk/run are always on;
  "Reset All Settings" is a custom drawer button under Enable (two clicks).
- Depth of field: the focus distance and the blur's weight are eased
  (`1 - exp(-speed * dt)`), "Third Person Focus Speed"; the offset setting is gone.

**Round 6 result (developer, 2026-10-04), and round 7 (UNVERIFIED).**
- **Voices still silent.** Calling `LowpolyVoiceProc(playerAI)` every frame did not
  throw and did not speak. TRIED, FAILED as the whole answer. Round 7 calls
  `LowpolyVoicePlay(key, playerAI)` once when an animation starts, with the `infoTable`
  entry whose key or `hash` equals `AnimationCtrlManager.GetHash(id)` (or the id), and
  with Debug Info logs "Idle voice: ..." (hash, match, result) plus, once, the whole
  table and every animation's hash. If there is no match the table is keyed some other
  way and that dump says how. `AnimStateInfo`: `hash`, `name`, `IsLoop`, `IsNoMale`,
  `bundleInfos`.
- **Seats missing in the classroom and at the cafe's tables** were not the reach: those
  seats are not wander (urouro) points. They belong to activities (Study, a meal with
  someone), in the solo / with / everyone / pc tables, each point's `soloDetails` /
  `withDetails` / `everyoneDetails` / `pcDetails`. Reach is 1 m again. When no wander
  seat is in reach, `NearbySpot` now looks through those tables and `UseBorrowed` seats
  the player by hand: `playerAI.transform` to the detail's `charactorOffset` (position
  and rotation), first listed animation. Getting up is the usual walk to the floor.
  Open questions: whether the game's idle tree leaves the player there (a log line
  says so if not), and props (`moveObjectName`, a chair pulled out) are not moved.
  Only the third-person idle key reaches these; click-to-walk still uses wander seats.
- The reference list ("Idle: animations on map") now ends with the activity tables'
  seats per activity.
- Debug Info was off in the round 6 test (Reset All Settings switches it off too).

**Round 7 result (developer, 2026-10-04), and round 8 (UNVERIFIED).**
- **A point's `poses` does not say whether it is a seat.** From the per-map lists: the
  classroom (map 4) has 24 points with pose Stand listed in the urouro table under key 1
  (Study) offering Study Desk0/1; the cafe (map 1) has pose-Stand points whose
  activity-None offer includes Desk Wait and Smart Phone Desk, and Job offers Job
  Waitress 0/1; the beach (map 3) has Stand+Ground points whose offer lists only standing
  animations. The activity tables' points are pose Stand too ("0 seats" everywhere), so
  round 7's borrowed seats never triggered. A seat is now a point that is non-standing
  **or** whose offer for the activity it is listed under contains a sitting animation
  (`ClickIdler.IsSeat`), and it is used with that activity as the job
  (`SetCharaMapMove`, type 0, job = the table key). UNVERIFIED that the game then seats
  the player as it seats NPCs; `Describe` now logs a point detail by detail (activity,
  animations x weight, offset, prop) to settle the cafe's mixed offers.
- **Voices: the table is keyed by a running number; `AnimStateInfo.hash` is the
  animation's state hash** (= `AnimationCtrlManager.GetHash(id)`), 45 entries, all
  `IsNoMale`. CONFIRMED from the dump: dumbbell 20 -> key 3 'f_training_00', exercise
  17-19 -> keys 0-2, study 36/37 -> 18/19, and so on. `LowpolyVoicePlay(key, playerAI)`
  **returns true and nothing is heard**. (Round 7 also matched Stand to key 0 by
  mistake, through an id comparison; removed.) Round 8 lists, a second after each line,
  every playing AudioSource (clip, volume, 3D, distance, mixer group) to see whether
  the clip plays at all. The per-frame `LowpolyVoiceProc` call is gone.
- Sitting animations (`NeedsSeat`: the game's `posePtnChairIDs` / `posePtnDeskIDs`, names
  with chair or desk, and 14 / 16): Chair Wait 9, Desk Wait 10, Waiting Action 1 and 3,
  Meal Chair 22, Meal Desk 23, Work Chair 25, Smart Phone Chair 27 / Desk 28, Bookread
  Chair 30, Erotic Book Chair 32, Game Chair 35, Study Desk0/1 36-37, Masturbation
  Chair 40 / Desk 41, Meal2 Chair 54 / Desk 55.
- Settings: "Animation Set" (Fitting, Map Animations, Sitting, Favorites 1-3, All)
  replaces Extended Animations, Tap Plays and Tap Animation; a tap plays a random one
  of the set. Three favorite collections. `HideSettingName` added to our
  ConfigurationManagerAttributes copy for the full-row Reset button.

**Round 8 result (developer, 2026-10-04), and round 9 (UNVERIFIED).**
- Seats by what a spot offers: the classroom's desks and the cafe's tables seat the
  player through the game's own walk (job = the table key). CONFIRMED. Station and beach
  still work.
- **Voices: the line does start, at volume 0.** One second after `LowpolyVoicePlay`:
  `c04/sv_004_ms_000_422 ... volume 0.00 mute False 3D 0.0 ... group PCM`, an audio
  source under the personality's voice object (`c04`), not under the player. CONFIRMED.
  So the game plays the player's line muted (or fades it by something we do not
  drive). Round 9 notes the PCM sources playing before the call, finds the new one
  after it and sets its volume to 1 every frame while it plays (`KeepVoiceAudible`).
- At the cafe, playing a meal animation on a seat made the game slide the player off
  the seat about a second later, animation still running; Desk Wait and Smart Phone
  Desk (the seat's own) stay. LIKELY the idle tree undoing the seat's offset when the
  animation is not one of the seat's. While an animation of ours plays on a seat the
  player is now kept at the seated position (`SeatUpkeep`), released by a movement key,
  a walk, or the animation ending; it logs once how far the game moved them.
- A sitting animation chosen from the wheel while standing next to a seat sits the
  player there first (`PlayChoice`: `UseSpot`, then the animation 0.7 s later).
- Markers are the player's own ring, a child of the player: placed before the player
  moves in the same pass, they jumped for a frame on every turn. `SteadyMarker` puts
  the ring back after `MovePlayer`. Leaving third person now always puts the ring's
  local position back (it was only done with Overview Restore on).
- Favorites: number keys 1-3 on the wheel toggle that collection; labels show the star
  with the collection numbers. Spot Click Size default 0.6.

**Round 9 result (developer, 2026-10-04), and round 10 (UNVERIFIED).**
- **Sound: setting the game's audio source to volume 1 did not make it audible**
  (TRIED, FAILED). The "volume 1.00 a second later" in the log was read straight after
  we wrote it, so it proved nothing; LIKELY the game writes 0 back every frame, after
  us. Round 10 does not touch the game's source: it takes its `clip` and
  `outputAudioMixerGroup` and plays them on an AudioSource of our own
  (`ClickIdler.Voice.cs`), once per loop of the animation (animator `normalizedTime`),
  looping for `IsLoop` entries, stopped when the animation ends. These are the
  exercise grunts, not spoken lines.
- **Sitting animations chosen from the wheel raced the game's own seating.** `UseSpot`
  puts the player on the point and starts the game's walk; the game then seats the
  player (offset) and starts an animation of its own choosing some moments later. Ours
  was played 0.7 s after `UseSpot` regardless, so it sometimes came first: played in
  mid-air before the offset, then replaced by the game's pick; and the seat hold,
  taken at that moment, pinned the player at the pre-offset spot ("moved away a few
  frames later", ring off-centre). Now the choice waits until the game is playing one
  of the seat's own animations for 0.35 s (`PendingChoice`, 5 s limit).
- **The hold** (`Hold` / `Pin` / `Release`): from the first animation that is not the
  seat's plain pose until the player leaves (movement key, a walk, another target, map
  change, conversation), the player is put back after `SimulationScene.Update` and
  after the player's `AIBase.FixedUpdate`. Activity ("borrowed") seats are held from
  the moment of sitting, since nothing in the game keeps a character there.
- The cafe's "with" table (activity Meal) seats are the ones the idle key reached and
  a click did not: they are activity seats. A click now walks to the floor beside one
  and sits by hand on arrival (`WalkToBorrowed`, `PendingBorrow`).
- `SV.Chara.Base` also has `SetMapPosition(map, MovePointInfo)`,
  `SetPositionAndRotation(Transform)`, `SetObjectsPosition(Transform)` /
  `RestoreObjectsPosition()` (LIKELY the ring and particles, kept apart from a seated
  body) and `SetRotationObjParticleCircle`. Not used yet; the first place to look if the
  ring is off-centre again after sitting.
- LIKELY: `JobDetail.SetOffset(_baseTrans)` moves the *point's* transform (or its
  `transPair`) to the seat while in use and `RestoreOffset` puts it back, which is why
  both take a base transform. UNVERIFIED.
- Fitting, standing, is now only what standing spots offer with no activity and needs
  no seat (it had become the same list as Map Animations once "standing" points turned
  out to carry desk and activity animations).
- Favorites: no Favorite Key any more, the number keys only; the star shows "1, 3" in
  the collection's colour (gold, blue, pink; green in two; violet in all three).
- The Reset All Settings row has its label again; the button ends where other rows'
  Reset buttons begin (a spacer of that width).

**Round 10 result (developer, 2026-10-04), and round 11 (UNVERIFIED).**
- **Sounds work**: the game's clip played on our own AudioSource through the same mixer
  group. CONFIRMED.
- The idle key seats the player correctly on every cafe and classroom seat. CONFIRMED.
- **Activity ("borrowed") seats did not hold.** Seated by hand the player was right at
  first and a few seconds later stood at another spot, the same spot a click put them
  at from the start; clicking again flipped between the two. LIKELY the walker
  (`SVRichAI`, A* `RichAI`): it keeps its character on the nav mesh and writes the
  transform in its own Update, after our pin, so the pin in `SimulationScene.Update` and
  after `AIBase.FixedUpdate` lost every frame. Idle and hand-driven it was dormant, which
  is why third person looked right for a while; after a click-walk it was awake at once.
  Round 11: while held, `walker.updatePosition` and `updateRotation` are false (saved and
  put back on release); the pin stays as a second line. `Pathfinding.AIBase` also has
  `canMove`, `isStopped`, `simulatedPosition`, `Teleport(pos, clearPath)`.
- **Leaving a seat by hand** (keys or stick; `ThirdPersonController.Handling`): the ring
  kept the offset the game gave it for the seat and trailed beside the player until the
  game next walked them. Now `Base.RestoreObjectsPosition()` is called at that moment.
  UNVERIFIED that this is the game's own undo. The seat is also remembered as left
  (`_leftSeat`): it stayed "the current seat" while the player stood within 1 m of it
  with it still the game's target, so sitting animations chosen there played in the air
  (LIKELY the Waiting Action 1 / 3 report) and the wheel kept offering the seat's list.
- Clicking the seat one is on does nothing now.
- The jobs' animations (`job_*`) never count as sitting. An animation chosen from the
  wheel goes to a spot within 2 m only if that spot offers exactly it **with an offset**
  (`NearbyOffering`: the cafe's tables for Job Waitress), or, for a sitting animation,
  to the nearest seat; otherwise it plays where the player is.
- A favorite's name takes its star's colour while pointed at. Wheel Size default 12.
- **Gamepad layout 3** (replaces the table in §27 where they differ): LB = idle button
  (`Gamepad Idle Button`: tap as the idle key in third person, in either view; hold =
  the wheel for as long as it is held, either stick points, Select (A) plays and the
  wheel stays up, RB / D-pad left and right turn pages, letting go closes it without
  choosing). PoV toggle LB -> left stick click; crouch left stick click -> B, not while
  a button on screen is selected (`GamepadUI.Active` / `UsedBThisFrame`). Migrated once
  ("Gamepad Layout" 3) for bindings still on the old defaults, with a log line each.
  While the pad wheel is up: `MovePlayer` ignores the sticks, `GamepadUI.Tick` and
  `GamepadTravel.Run` stand down, the camera holds.
