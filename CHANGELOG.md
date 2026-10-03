# Changelog

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
