# Yoink - A Winch For Stuck Vehicles

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de/yoink](https://support.doodesch.de/yoink).

> A hand winch for Schedule I. Aim at anything with a rigidbody, fire the hook, and reel it in. Built for the
> case the game has no answer to: a van wedged between two walls, dropped through the world geometry, or
> parked somewhere it can never drive out of.

![Version](https://img.shields.io/badge/version-0.3.0-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-purple)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3+-green)
![Status](https://img.shields.io/badge/status-beta-yellow)

## What it does

- **Hooks the exact point you aimed at.** Not the object's centre - the spot under your crosshair. Pull a
  van by its rear corner and it swings round; pull it by the nose and it comes at you straight.
- **Reels anything with a rigidbody.** Vehicles, barrels, crates, litter. A parked car has its handbrake
  released first, because the game leaves it permanently applied on anything nobody is driving - without
  that, no amount of force moves it a centimetre.
- **A real rope.** A verlet-simulated cable that sags under its own weight, rests on the ground instead of
  hanging through it, and snaps if you walk too far.
- **Co-op.** The host owns the physics; a client's pull travels as an intent. If someone is sitting in the
  vehicle you hooked, their client applies the force, because that is who owns it.

## How to use it

1. Buy the **Winch** in the hardware store ($80 by default).
2. Equip it. **Left click** fires the hook at whatever you are looking at, and clicking again lets go.
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
