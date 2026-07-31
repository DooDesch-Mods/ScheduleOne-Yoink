using System;
using Il2CppScheduleOne;   // GameInput

namespace Yoink.Winch
{
    /// <summary>
    /// Mouse state for the winch's two controls, asked twice.
    ///
    /// The game's own <c>GameInput</c> is the right source: it respects rebinding and the game's own notion of
    /// which button is which. But it is a rebindable map on top of an input system, and a mod that only asks
    /// there has no answer at all when the map does not report what the player is pressing - which reads to the
    /// player as "the winch is broken". So the raw mouse is checked as a fallback, and which source answered is
    /// logged once, so a report of "right click does nothing" can be settled from the log instead of guessed at.
    /// </summary>
    internal static class Input
    {
        private static bool _loggedHook;
        private static bool _loggedReel;

        /// <summary>True on the frame the hook button goes down (primary click).</summary>
        internal static bool HookPressed()
        {
            bool game = false, raw = false;
            try { game = GameInput.GetButtonDown(GameInput.ButtonCode.PrimaryClick); } catch { }
            try { raw = UnityEngine.Input.GetMouseButtonDown(0); } catch { }

            if ((game || raw) && !_loggedHook)
            {
                _loggedHook = true;
                Core.Log.Msg("[Input] hook button seen (" + Source(game, raw) + ").");
            }
            return game || raw;
        }

        /// <summary>True while the reel button is held (secondary click).</summary>
        internal static bool ReelHeld()
        {
            bool game = false, raw = false;
            try { game = GameInput.GetButton(GameInput.ButtonCode.SecondaryClick); } catch { }
            try { raw = UnityEngine.Input.GetMouseButton(1); } catch { }

            if ((game || raw) && !_loggedReel)
            {
                _loggedReel = true;
                Core.Log.Msg("[Input] reel button seen (" + Source(game, raw) + ").");
            }
            return game || raw;
        }

        private static string Source(bool game, bool raw)
        {
            if (game && raw) return "GameInput and raw mouse";
            return game ? "GameInput" : "raw mouse only - the game's input map did not report it";
        }
    }
}
