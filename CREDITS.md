# Credits

## Original plugin — SVS_3rdPov, by **Junh2x**

SVS_FreeRoam (called SVS_3rdPovPlus while it was being built) grew out of
**SVS_3rdPov v0.0.6** by **Junh2x**, whose repository is no longer available. That plugin is the origin of everything this one does: the
third-person camera for Summer Vacation Scramble, the orbit rig, the walk and run
handling driven from the character's stamina, the interaction system for
characters, job spots and doorways, the detection of conversations, H scenes and
menus, and the hotkey layout.

This project began as a companion plugin that changed SVS_3rdPov's behaviour from
outside without touching its DLL. That was the right shape while the changes were
still moving, but it meant two plugins, a large amount of reflection, and several
workarounds that existed only because the two could not be edited together. With
the plugin abandoned and its repository gone, the work was folded into a single
assembly so the underlying bugs could be fixed properly rather than patched
around.

**Junh2x's design decisions are still visible throughout**, deliberately so. The
structure of the per-frame update, the branch between menu and control states, the
stamina-based speed, the interaction search and the suppression of the wheel and
middle-click handlers are all theirs. Where behaviour changed, it is because
something was measurably wrong, and each of those is documented in FINDINGS.md
rather than quietly rewritten.

SVS_3rdPov shipped without a licence file. This continuation is to keep it available for the community of the game. If Junh2x or anyone acting for them wants it taken down, changed,
or credited differently, that request should be honoured without argument.

## What this version adds

- Hold a mouse button to walk forward, as Koikatsu and Aicomi do
- A camera rig that orbits the look target, so pitch means what it says and the
  view no longer flips when zoomed in
- A full up and down look range instead of −15..65 degrees
- Zoom out to 12 (was 5), zoom in to 0, and optical zoom past that
- Optional first-person view by hiding your own character
- Optional camera collision that ignores characters and trigger volumes
- The location's own overview camera remembered per map and restored on exit
- Location buttons that follow the doorways and job spots they lead to
- A second binding for every hotkey
- Cursor handling that gives the cursor back, including after a map change
- Sprint (key or double tap) and a walk/run toggle; stamina-based speed optional
- Walking to the marked character with middle click, as in the overview camera
- Gamepad support: sticks, triggers, interact, sprint and toggle
- Optional WASD movement in the overview camera and on 2D and custom maps
- Support for the game's Pop-ups action point setting, and custom maps' buttons
  and overview cameras
- Enable/disable while playing
- Gamepad: choosing on-screen buttons with the D-pad, conversations, travel
- Click the ground to walk there, click a character to follow them (merged from our
  own SVS_WalkAnywhere)
- Force High Poly Characters on the map

## Tools and references

- **BepInEx 6** (IL2CPP) and **HarmonyX** — the plugin framework and patching
- **dnSpy** — decompiling SVS_3rdPov and reading the game's interop assemblies
- **RuntimeUnityEditor** — inspecting the live scene, which is where the button
  naming and the overview camera timings were actually established
- **SVS_3DRooms** by TonWonton — structural model for the plugin template
- **KK_Plugins' ForceHighPoly** (Koikatsu) — the idea behind Force High Poly Characters
- **SVS_CheatTools** — where a walker's speed can be overridden, used by follow
- **UnityPy** — reading the game's asset bundles
- The Illusion modding guide shared by Marco
  (https://hackmd.io/Av9XnTkrSMOOrRguEMMnSw?view=)
