#if SNITCH
using Snitch.Api;
using UnityEngine;
using Yoink.Config;
using Yoink.Item;
using Yoink.Winch;

namespace Yoink.Profiling
{
    /// <summary>
    /// DEBUG-only Snitch panel for Yoink. The Snitch host auto-discovers this type (leaf name SnitchProbe with a
    /// static Register) on bind and calls it; whatever it registers is also forwarded into the Hotline overlay by the
    /// host, so there is no direct Hotline dependency. Compiled only under the SNITCH symbol - the Release DLL
    /// contains zero Snitch types.
    ///
    /// Why this exists: every value worth tuning here - how big the winch is in hand, how high the rig sits, how hard
    /// it pulls - can only be judged by looking at it while it moves. The console can set them, but typing a number,
    /// reading a log line and typing the next number is a slow loop. This is the same knobs as a control surface.
    ///
    /// Note on ids: Snitch derives a control's id by slugifying its label, and punctuation is dropped. Two labels that
    /// differ only in punctuation collide and the second registration silently replaces the first, so every label here
    /// is distinct in its WORDS. Ranges are deliberately wider than the useful band - a value you cannot reach is
    /// worse than one you have to be careful with, and the host clamps to the range anyway.
    /// </summary>
    internal static class SnitchProbe
    {
        public static void Register()
        {
            Panel p = Profiler.RegisterPanel("Yoink", "Yoink (Winch)");

            // ---- readouts ---------------------------------------------------------------------------------

            p.Text(() => WinchItem.DescribeFraming());

            p.Text(() =>
            {
                if (!WinchSession.Hooked) return "hook: empty";
                return "hook: " + WinchSession.TargetLabel()
                     + "  " + WinchSession.TargetMass().ToString("F0") + "kg"
                     + "  " + WinchSession.Distance().ToString("F1") + "m"
                     + "  " + WinchSession.TargetSpeed().ToString("F2") + "m/s"
                     + (WinchSession.Pulling ? "  PULLING" : "  idle")
                     + (WinchSession.Stalled ? "  STALLED" : string.Empty);
            });

            p.Text(() => Preferences.Describe());

            p.Counter("Distance", () => WinchSession.Distance(), "m");
            p.Counter("TargetSpeed", () => WinchSession.TargetSpeed(), "m/s");
            p.Counter("TargetMass", () => WinchSession.TargetMass(), "kg");
            p.Counter("ReelRate", () => WinchSession.ReelRateSmoothed, "m/s");
            p.Counter("HeldScale", () => WinchItem.HeldScale, "x");
            p.Counter("RigHeight", () => WinchItem.RigOffset.y, "m");

            // MuzzleY is the number to watch while framing: below 0 or above the screen height means the cable
            // starts outside the picture, which reads as "the rope is not attached to the winch".
            p.Counter("MuzzleY", () =>
            {
                Vector2 s;
                return WinchItem.TryMuzzleScreen(out s) ? s.y : 0d;
            }, "px");

            // ---- how it is held ---------------------------------------------------------------------------

            p.Slider("size", 0.2, 2.0, () => WinchItem.HeldScale, v => WinchItem.HeldScale = (float)v, 0.05, "x");

            // The rig is the whole first-person arrangement - arms included. All three axes, because a tool that
            // frames well vertically can still sit too far into the screen edge.
            p.Slider("rig height", -0.15, 0.15, () => WinchItem.RigOffset.y,
                v => WinchItem.RigOffset = new Vector3(WinchItem.RigOffset.x, (float)v, WinchItem.RigOffset.z), 0.005, "m");
            p.Slider("rig side", -0.15, 0.15, () => WinchItem.RigOffset.x,
                v => WinchItem.RigOffset = new Vector3((float)v, WinchItem.RigOffset.y, WinchItem.RigOffset.z), 0.005, "m");
            p.Slider("rig depth", -0.15, 0.15, () => WinchItem.RigOffset.z,
                v => WinchItem.RigOffset = new Vector3(WinchItem.RigOffset.x, WinchItem.RigOffset.y, (float)v), 0.005, "m");

            // Moves the tool inside the hand, not the hand. Negative z pulls it back into the grip.
            p.Slider("tool depth", -0.3, 0.3, () => WinchItem.HeldOffset.z,
                v => WinchItem.HeldOffset = new Vector3(WinchItem.HeldOffset.x, WinchItem.HeldOffset.y, (float)v), 0.01, "m");
            p.Slider("tool height", -0.3, 0.3, () => WinchItem.HeldOffset.y,
                v => WinchItem.HeldOffset = new Vector3(WinchItem.HeldOffset.x, (float)v, WinchItem.HeldOffset.z), 0.01, "m");
            p.Slider("tool side", -0.3, 0.3, () => WinchItem.HeldOffset.x,
                v => WinchItem.HeldOffset = new Vector3((float)v, WinchItem.HeldOffset.y, WinchItem.HeldOffset.z), 0.01, "m");
            p.Slider("tool turn", 0, 360, () => WinchItem.HeldRotation.y,
                v => WinchItem.HeldRotation = new Vector3(WinchItem.HeldRotation.x, (float)v, WinchItem.HeldRotation.z), 5, "deg");
            p.Slider("tool pitch", -90, 90, () => WinchItem.HeldRotation.x,
                v => WinchItem.HeldRotation = new Vector3((float)v, WinchItem.HeldRotation.y, WinchItem.HeldRotation.z), 5, "deg");

            // How far in front of the eye the cable is pinned. Too close and a 3 cm rope seen from 10 cm fills the
            // screen; too far and it visibly detaches from the muzzle when you turn.
            p.Slider("rope anchor depth", 0.2, 3.0, () => WinchItem.RopeStartDepth,
                v => WinchItem.RopeStartDepth = (float)v, 0.05, "m");

            // ...and across the picture, to land it on the hook by eye. In fractions of screen height, so +-0.5 is
            // half a screen either way - far more than anyone needs, and the range a value has to be findable in.
            p.Slider("anchor side", -0.5, 0.5, () => WinchItem.RopeAnchorNudge.x,
                v => WinchItem.RopeAnchorNudge = new Vector2((float)v, WinchItem.RopeAnchorNudge.y), 0.005, "scr");
            p.Slider("anchor height", -0.5, 0.5, () => WinchItem.RopeAnchorNudge.y,
                v => WinchItem.RopeAnchorNudge = new Vector2(WinchItem.RopeAnchorNudge.x, (float)v), 0.005, "scr");

            p.Action("reset framing", WinchItem.ResetFraming);

            // ---- how it pulls -----------------------------------------------------------------------------

            p.Slider("pull force", 0, 120000, () => Preferences.PullNewtons, v => Preferences.PullNewtons = (float)v, 500, "N");
            p.Slider("max speed", 0.1, 12, () => Preferences.MaxSpeed, v => Preferences.MaxSpeed = (float)v, 0.25, "m/s");
            p.Slider("hook range", 2, 60, () => Preferences.HookRange, v => Preferences.HookRange = (float)v, 0.5, "m");
            p.Slider("break dist", 3, 80, () => Preferences.BreakDistance, v => Preferences.BreakDistance = (float)v, 0.5, "m");
            p.Slider("stop dist", 0.5, 15, () => Preferences.StopDistance, v => Preferences.StopDistance = (float)v, 0.25, "m");
            p.Slider("shop price", 0, 2000, () => Preferences.ShopPrice, v => Preferences.ShopPrice = (float)v, 5, "$");

            // ---- doing things -----------------------------------------------------------------------------

            p.Action("give winch", () => WinchItem.Give());
            p.Action("hook nearest", () => { string _; WinchSession.HookNearest(12f, out _); });
            p.Action("pull 3s", () => { string _; WinchSession.Pull(3f, out _); });
            p.Action("stop pulling", WinchSession.Stop);
            p.Action("drop hook", WinchSession.Drop);

            p.Toggle("arms visible", () => WinchItem.ShowArms, v => WinchItem.ShowArms = v);

            p.Log();
        }
    }
}
#endif
