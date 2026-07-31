# Changelog

All notable changes to Yoink are documented here. Format based on [Keep a Changelog](https://keepachangelog.com).

## [1.1.0] - 2026-08-01

The hook now bites two things it used to slide off: cars somebody is driving, and people.

### Added
- **Hook people.** A shot that lands on somebody takes them off their feet and drags them, and they get back up
  by themselves once you let go - the same knockdown and the same recovery the game already uses when a car
  clips a pedestrian. The whole body comes along, not just the limb the hook is in. Turn it off with
  `HookPeople` in `UserData/MelonPreferences.cfg`, or change how hard it knocks them over with `Knockdown`.

### Fixed
- **Vehicles with an NPC driving them ignored the winch completely.** Two separate reasons, both now gone. The
  game marks a car as occupied when an NPC gets in, and the winch read that as "a player is driving, so their
  machine owns the physics" and handed the pull to a machine that does not exist in single player - so nobody
  applied it. And an AI driver rewrites its own throttle and steering every frame, which meant the neutral the
  winch puts a car in was undone before the next physics step and the car went on braking against the cable.
- The mod reported itself to MelonLoader as version 0.2.0.

## [1.0.0] - 2026-08-01

First public build. A hand winch that hooks whatever you aimed at and drags it out.

### Added
- **Hook the exact point you aimed at**, not the object's centre. Pull a van by its rear corner and it swings
  round; pull it by the nose and it comes at you straight.
- **Works on anything with a rigidbody** - vehicles, barrels, crates, litter. A parked car has its handbrake
  released first, because the game leaves it permanently applied on anything nobody is driving, and without
  that no amount of force moves it a centimetre.
- **A rope that behaves like one.** It sags under its own weight, rests on the ground instead of hanging
  through it, tightens over about half a second when the winch bites, and snaps if you walk too far.
- **Co-op.** The host owns the physics and a client's pull travels as an intent. If someone is sitting in the
  vehicle you hooked, their machine applies the force, because that is who owns it.
- **Buyable in the hardware store** for $80, configurable along with the pull force, speed, range and break
  distance in `UserData/MelonPreferences.cfg`.
- Debug builds register a Snitch panel with every tuning value as a slider, so the winch can be dialled in
  while it is moving rather than through the console.

### Notes
- The item icon is rendered from the winch model at startup, so it cannot drift out of date when the model
  is re-exported.
- Nothing about the rope is networked and nothing needs to be: both its ends are positions every machine
  already knows, and what happens between them is cosmetic.
