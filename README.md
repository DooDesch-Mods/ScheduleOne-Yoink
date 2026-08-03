# Yoink - A Winch For Stuck Vehicles

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de/yoink](https://support.doodesch.de/yoink).

> A hand winch for Schedule I. Aim at anything with a rigidbody, fire the hook, and reel it in. Built for the
> case the game has no answer to: a van wedged between two walls, dropped through the world geometry, or
> parked somewhere it can never drive out of.

![Version](https://img.shields.io/badge/version-1.2.1-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-purple)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3+-green)
![Status](https://img.shields.io/badge/status-working-brightgreen)

![Yoink in action: a car hauled in on the winch cable](https://raw.githubusercontent.com/DooDesch-Mods/ScheduleOne-Yoink/main/media/yoink.gif)

*Hook the car, hold right click, walk backwards. The cable tightens as the winch takes the load.*

## What it does

The hook bites the spot under your crosshair, not the object's centre. Pull a van by its rear corner and it
swings round; pull it by the nose and it comes at you straight. On something wedged, that choice is usually
the difference between freeing it and grinding it further in.

It reels anything with a rigidbody: vehicles, barrels, crates, litter. A parked car gets its handbrake
released first, because the game leaves it permanently applied on anything nobody is driving, and without
that no amount of force moves it a centimetre. A car with an NPC at the wheel is held in neutral for as long
as the hook is in it, because an AI driver otherwise brakes against the cable every frame.

It bites people too. A shot that lands on somebody takes them off their feet and drags the whole body, and
they get back up by themselves once you let go - the same knockdown and the same recovery the game uses when
a car clips a pedestrian. Set `HookPeople` to false if you would rather it did not.

The cable is simulated rather than drawn. It sags under its own weight, rests on the ground instead of
hanging through it, takes about half a second to tighten when the winch bites, and snaps if you walk too far.

Co-op works. The host owns the physics and a client's pull travels as an intent, and if another player is
sitting in the vehicle you hooked, their machine applies the force, because that is who owns it.

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
| `HookPeople` | true | Whether the hook bites people and drags them. |
| `Knockdown` | 60 | How hard a hooked person is knocked over, in newton-seconds. |

## When it will not move

The mod says why in the console every time it refuses a hook: `nothing within Xm` (nothing hookable in
range), `has no rigidbody` (scenery), `is fixed in place` (kinematic, and no winch moves a building).

If it hooked but nothing happens, it is usually one of three things. Another player is sitting in the
vehicle, in which case their machine owns it. Or it is wedged hard and wants a different hook point - aim at
the corner that has somewhere to go. Or it is simply heavy: raise `PullNewtons`.

`PullNewtons` and `MaxSpeed` do different jobs. The force decides whether a heavy thing moves at all; the
speed caps how fast anything is allowed to arrive. Raising the force will not make a barrel come faster,
because the speed ceiling binds first. Raising the speed will not free a stuck van.

## Multiplayer

Everyone needs the mod. The machine that owns an object applies the force to it, so a pull on a networked
object goes through the host, and a pull on a vehicle another player is sitting in is handed to their machine,
which is the only one whose forces that vehicle accepts. A vehicle an NPC is driving stays the host's. The rope
is not networked and does not need to be.

Co-op is implemented and reviewed but has not been run with two players since the physics rework.

## Requirements

- [MelonLoader](https://melonwiki.xyz/) 0.7.3 or newer
- [S1API](https://thunderstore.io/c/schedule-i/p/ifBars/S1API_Forked/) 3.1.1 or newer

## Credits

Built by DooDesch on [S1API](https://github.com/ifBars/S1API) by ifBars.
Winch sound effect by **fadestyle**.

## License

MIT. See [LICENSE.md](LICENSE.md).
