# Changelog

All notable changes to Yoink are documented here. Format based on [Keep a Changelog](https://keepachangelog.com).

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
