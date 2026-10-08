# Changelog

## 1.1.0

**Fixed**
- The camera not appearing on older versions of the game (the toggle hid the UI and
  nothing else happened). If a game version is still missing something the plugin needs,
  it now says so on screen and lists it in `LogOutput.log`. Updating the game is still
  the safest fix.
- Characters turning invisible when you switched to the overview camera during an
  activity scene started in third person.
- Characters close to the camera being blurred in third person when Depth Of Field is
  on in the game's graphics settings.
- A repeating error is written to the log once, not every frame.
- Being walked somewhere with someone (an activity you were invited to, following
  them) could be broken by a click or a step, leaving the activity to start a minute
  late or not at all. The plugin now leaves you alone until that walk is over.

**Seats and idle animations**
- Sit on benches, chairs, the classroom's desks and the café's tables: click them, or in
  third person tap the idle key (middle mouse, LB on a controller) next to one. Seats
  someone else is using are left alone.
- Click yourself, or tap the idle key, to play an idle animation; again to stop.
- Hold for the animation wheel. Choose which animations it lists with `Animation Set`:
  what fits where you are, the map's own, every sitting one, your favorites, or all of
  them. Press 1, 2 or 3 on the wheel to keep an animation in one of three favorite
  collections.
- Animations come with what they hold, and the sounds some of them make (female
  characters only: the game has none for males).
- Hold right click on another character for a wheel: talk, follow, switch to.
- Optionally, an idle animation when you arrive from a click-walk.

**Camera**
- Drag the camera with the right mouse button held: up/down, in/out; with both buttons,
  forward/back and sideways. In first person it zooms.
- `Space` (right stick click) goes to first person and back out to the starting view.
- Camera smoothing is a single glide now; looking around is never delayed.
- Optional depth of field in third person that follows your character (`Third Person
  Blur Strength`, off at 0).
- `PoV Mode` can also allow third person on 2D maps (not recommended) or switch it off.

**Controls**
- Controller: LB is the idle button, the left stick click toggles third person, B
  crouches in first person. Bindings still on the old defaults are moved once.
- Crouch Key Function: toggle, double tap, or while held. Double-tap sprint and
  walk/run are always on.
- Changing Forward Mode moves Interact to the other mouse button.

**Settings**
- One `Clicking Actions` category in place of the three Click To ones; your values carry
  over. A `Reset All Settings` button under Enable.

## 1.0.0 — first release

The first public release of SVS_FreeRoam, a rework of SVS_3rdPov v0.0.6 by Junh2x.

**Third person**
- A camera that orbits your character, with a full look range, zoom from far out to
  first person, and a lens zoom past that.
- Hold a mouse button to walk forward; sprint, walk/run toggle and crouch, with optional
  double taps.
- Location buttons float over the doorways and activity spots they lead to, and only
  appear where the game itself offers them.
- Talk, take doorways and do activities (Work, Eat, Study...) by walking up and
  interacting, or walk over to someone you aim at from afar.
- Camera collision that ignores people.
- Optionally the only view: the overview camera never takes over.
- Doorways fade to white (or black, or not at all), like the game's own travel.

**Everywhere on the map**
- Walk with WASD, the arrow keys or the left stick in the overview camera and on 2D maps.
- Click the ground to walk there; click a character to follow them, through doorways.

**Gamepad**
- Camera on the right stick, zoom on the triggers, every action on a button, all
  rebindable.
- The D-pad chooses buttons on screen: the map's own buttons, Map Select, Meet Up, the
  Jizo screen, menus and conversation choices. LB / RB switch between the pause screens.

**Force High Poly Characters**
- Characters on the map use their full-detail models. Scenes that used to get stuck with
  them (Work, Study, Exercise, Eat) work.

**Other plugins**
- SVS_3rdPov is switched off if it is still installed.
- SVS_CustomGameBalance's character switch button works in third person.
