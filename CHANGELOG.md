# Changelog

All notable changes to Yoink are documented here. Format based on [Keep a Changelog](https://keepachangelog.com).

## [1.2.0] - 2026-08-01

### Fixed
- Works on Schedule I 0.4.6f11. Update S1API to 3.1.1 with it.

## [1.1.0] - 2026-08-01

### Added
- The winch hooks people now. A hit takes them off their feet and drags the whole body, and they get
  back up by themselves once you let go.
  - `HookPeople` turns it off, `Knockdown` sets how hard it knocks them over. Both live in
    `UserData/MelonPreferences.cfg`.

### Fixed
- Cars with an NPC at the wheel can be winched. They used to ignore the cable completely, in single
  player and in co-op.
- Yoink reported itself to MelonLoader as version 0.2.0.

## [1.0.0] - 2026-08-01

### Added
- A hand winch, sold in the hardware store for $80. Left click fires the hook, hold right click to reel.
- The hook grabs the exact point you aimed at, not the object's centre. Pull a van by its rear corner
  and it swings round; pull it by the nose and it comes at you straight.
- Anything with a rigidbody comes along: vehicles, barrels, crates, litter. Parked cars get their
  handbrake released first, or no amount of force would move them.
- The rope sags under its own weight, rests on the ground instead of hanging through it, tightens over
  about half a second, and snaps past 25 m.
- Co-op works. Whoever owns an object applies the force to it, so a pull on a vehicle another player
  is sitting in is handed to their machine.

Pull force, reel speed, hook range and break distance are all in `UserData/MelonPreferences.cfg`.
