# Yoink - A Winch For Stuck Vehicles

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de/yoink](https://support.doodesch.de/yoink).

> A hand winch for Schedule I. Aim at anything with a rigidbody, fire the hook, and reel it in. Built for the
> case the game has no answer to: a van wedged between two walls, dropped through the world geometry, or
> parked somewhere it can never drive out of.

![Version](https://img.shields.io/badge/version-0.3.0-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-purple)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3+-green)
![Status](https://img.shields.io/badge/status-beta-yellow)

![Yoink in action: a car hauled in on the winch cable](https://raw.githubusercontent.com/DooDesch-Mods/ScheduleOne-Yoink/main/media/yoink.gif)

*Hook the car, hold right click, walk backwards. The cable tightens as the winch takes the load.*

## What it does

The hook bites the spot under your crosshair, not the object's centre. Pull a van by its rear corner and it
swings round; pull it by the nose and it comes at you straight. On something wedged, that choice is usually
the difference between freeing it and grinding it further in.

It reels anything with a rigidbody: vehicles, barrels, crates, litter. A parked car gets its handbrake
released first, because the game leaves it permanently applied on anything nobody is driving, and without
that no amount of force moves it a centimetre.

The cable is simulated rather than drawn. It sags under its own weight, rests on the ground instead of
hanging through it, takes about half a second to tighten when the winch bites, and snaps if you walk too far.

Co-op works. The host owns the physics and a client's pull travels as an intent, and if someone is sitting in
the vehicle you hooked, their machine applies the force, because that is who owns it.

## How to use it

1. Buy the Winch in the hardware store ($80 by default).
2. Equip it. **Left click** fires the hook at whatever you are looking at; click again to let go.
3. **Hold right click** to reel. Walk backwards while holding it and the load follows you out.

The rope breaks at 25 m, and reeling stops once the load is about 2.5 m away so it does not end up in your
face. Both are configurable.

## Settings

Everything lives in `UserData/MelonPreferences.cfg` under `[Yoink]`:

| Setting | Default | What it does |
|---|---|---|
| `PullNewtons` | 40000 | Pull force, about a four-tonne recovery winch. Real mass applies - a loaded van moves slower than a bin. |
| `MaxSpeed` | 1.5 | How fast the hook point is reeled in, m/s. |
| `HookRange` | 15 | How far the hook can be fired, metres. |
| `BreakDistance` | 25 | The rope snaps past this, metres. |
| `StopDistance` | 2.5 | Reeling stops once the load is this close. |
| `RopeSegments` | 20 | Points in the rope simulation. More is smoother, slightly more expensive. |
| `RopeCollision` | true | Rope slack rests on the ground instead of hanging through it. |
| `ShopPrice` | 80 | What it costs on the shelf. Read once when the item is registered. |

## Requirements

- [MelonLoader](https://melonloader.co/) 0.7.3 or newer
- [S1API](https://thunderstore.io/c/schedule-i/p/KaBooMa/S1API/)

## Credits

Winch model and texture authored in Blender with [blender-agent-studio](https://github.com/ifBars/blender-agent-studio)
by ifBars.

## Licence

MIT. See [LICENSE.md](LICENSE.md).
