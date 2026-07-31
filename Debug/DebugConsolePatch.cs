#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Yoink.Config;
using Yoink.Winch;

namespace Yoink.Debugging
{
    /// <summary>
    /// DEBUG-only dev console commands, all under one word: <c>yoink &lt;subcommand&gt;</c>. One command word keeps
    /// the mod's tooling together in the command list instead of scattering a dozen near-identical entries through
    /// it, and the autocomplete mod turns the comma-separated ExampleUsage into subcommand suggestions.
    ///
    /// Console only, no hotkeys: console commands can be scripted and therefore verified without a human at the
    /// keyboard, and anything that only works on a key press reaches testers unverified. (The winch's own controls
    /// are a different thing - those are player functions and belong on the mouse.)
    ///
    /// Both Console.SubmitCommand overloads are patched (string + List&lt;string&gt;): depending on the caller either
    /// one can be the overload whose managed prefix actually fires, so catching both is the reliable path. Dispatch
    /// dedupes per frame and signature so a command with side effects never runs twice for one submission.
    /// </summary>
    internal static class DebugConsolePatch
    {
        internal const string Word = "yoink";

        /// <summary>
        /// Fed to the game's command list, and read by the autocomplete mod as its source of argument suggestions -
        /// it splits on commas and offers the token at the position you are typing.
        /// </summary>
        internal const string Usage =
            "yoink help, yoink hook, yoink nearest 8, yoink person 8, yoink probe, yoink pull 3, yoink stop, " +
            "yoink drop, yoink info, yoink give, yoink equip, yoink force 12000, yoink set pull 12000, " +
            "yoink set vmax 1.5, yoink set knock 60, yoink test 8, " +
            "yoink vm show, yoink vm move 0 0 -0.02, yoink vm turn 0 15 0";

        private static readonly List<string> _pending = new List<string>();

        internal static bool TryHandle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return Dispatch(raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        internal static bool TryHandle(Il2CppSystem.Collections.Generic.List<string> args)
        {
            if (args == null || args.Count == 0) return false;
            string[] parts = new string[args.Count];
            for (int i = 0; i < args.Count; i++) parts[i] = args[i];
            return Dispatch(parts);
        }

        private static int _lastFrame = -1;
        private static string _lastSig = "";

        /// <summary>True when the command was ours (and should be swallowed), false to let the game handle it.</summary>
        private static bool Dispatch(string[] parts)
        {
            if (parts.Length == 0) return false;
            if (!string.Equals(parts[0], Word, StringComparison.OrdinalIgnoreCase)) return false;

            string sig = string.Join(" ", parts);
            int frame = Time.frameCount;
            if (frame == _lastFrame && sig == _lastSig) return true;   // both overloads fired for one submission
            _lastFrame = frame; _lastSig = sig;

            string sub = parts.Length > 1 ? parts[1].ToLower() : "help";
            string arg1 = parts.Length > 2 ? parts[2] : null;
            string arg2 = parts.Length > 3 ? parts[3] : null;

            try
            {
                string message;
                switch (sub)
                {
                    case "help":
                        Say("hook            - fire the hook where you are looking");
                        Say("nearest [r]     - hook the nearest rigidbody (default 8m, no aiming)");
                        Say("person [r]      - knock down and hook the nearest person (default 8m, no aiming)");
                        Say("probe           - what is under the crosshair, and whether it can be pulled");
                        Say("cars [n]        - every vehicle in the world, nearest first, with its physics state");
                        Say("pull [seconds]  - reel in (default 3s, 0 = until 'yoink stop')");
                        Say("stop            - stop reeling, keep the hook");
                        Say("drop            - release the hook");
                        Say("info            - target, mass, distance, speed, tuning");
                        Say("give / equip    - put a winch in your inventory / hold it");
                        Say("force <newtons> - change pull force at runtime");
                        Say("set <key> <val> - pull | vmax | range | break | stop");
                        Say("test [distance] - spawn a test vehicle N metres ahead");
                        Say("vm show           - where the held winch sits");
                        Say("vm move x y z     - nudge it (metres), vm turn x y z - rotate it");
                        Say("cam             - cameras, layers and where the rope is anchored");
                        Say("icon [path]     - write the in-game item icon to a PNG (listing art comes from here)");
                        break;

                    case "icon":
                    {
                        // Default target is the repo's Thunderstore folder, so the shipped listing art and the
                        // in-game item icon cannot be two different pictures of the same winch.
                        string target = arg1 ?? System.IO.Path.Combine(
                            System.IO.Directory.GetCurrentDirectory(), "yoink_icon.png");
                        Say(Yoink.Item.IconRenderer.SaveToDisk(Yoink.Item.WinchItem.EnsureModelSource(), target));
                        break;
                    }

                    case "cam":
                        TestRig.DumpCamerasSoon();
                        Say("camera dump goes to the log once the console closes (the game unequips while it is open).");
                        break;

                    case "hook":
                        WinchSession.HookFromCamera(out message);
                        Say(message);
                        break;

                    case "nearest":
                    {
                        float radius = 8f;
                        if (arg1 != null && !float.TryParse(arg1, NumberStyles.Float, CultureInfo.InvariantCulture, out radius))
                        {
                            Say("usage: yoink nearest [radius]");
                            break;
                        }
                        WinchSession.HookNearest(radius, out message);
                        Say(message);
                        break;
                    }

                    case "person":
                    {
                        float radius = 8f;
                        if (arg1 != null && !float.TryParse(arg1, NumberStyles.Float, CultureInfo.InvariantCulture, out radius))
                        {
                            Say("usage: yoink person [radius]");
                            break;
                        }
                        WinchSession.HookNearestPerson(radius, out message);
                        Say(message);
                        break;
                    }

                    case "probe":
                        Say(WinchSession.ProbeAhead());
                        break;

                    case "cars":
                    {
                        int max = 12;
                        if (arg1 != null && !int.TryParse(arg1, NumberStyles.Integer, CultureInfo.InvariantCulture, out max))
                        {
                            Say("usage: yoink cars [how many]");
                            break;
                        }
                        foreach (string line in WinchSession.ListVehicles(max).Split('\n')) Say(line);
                        break;
                    }

                    case "pull":
                    {
                        float secs = 3f;
                        if (arg1 != null && !float.TryParse(arg1, NumberStyles.Float, CultureInfo.InvariantCulture, out secs))
                        {
                            Say("usage: yoink pull [seconds]");
                            break;
                        }
                        WinchSession.Pull(secs, out message);
                        Say(message + " | " + WinchSession.StatusLine());
                        break;
                    }

                    case "stop":
                        WinchSession.Stop();
                        Say("stopped. " + WinchSession.StatusLine());
                        break;

                    case "drop":
                        WinchSession.Drop();
                        Say("hook released.");
                        break;

                    case "info":
                        Say(WinchSession.StatusLine());
                        break;

                    case "give":
                        Say(Yoink.Item.WinchItem.Give()
                            ? "winch added to your inventory."
                            : "the winch item is not registered yet - try again once you are in the world.");
                        break;

                    case "equip":
                        TestRig.EquipWinch();
                        break;

                    case "force":
                    {
                        float n;
                        if (arg1 == null || !float.TryParse(arg1, NumberStyles.Float, CultureInfo.InvariantCulture, out n))
                        {
                            Say("usage: yoink force <newtons>  (current " + Preferences.PullNewtons.ToString("F0", CultureInfo.InvariantCulture) + ")");
                            break;
                        }
                        Preferences.PullNewtons = n;
                        Say("pull force is now " + n.ToString("F0", CultureInfo.InvariantCulture) + "N.");
                        break;
                    }

                    case "set":
                    {
                        float v;
                        if (arg1 == null || arg2 == null || !float.TryParse(arg2, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                            || !Preferences.TrySet(arg1.ToLower(), v))
                        {
                            Say("usage: yoink set <pull|vmax|range|break|stop|knock> <value>  (now: " + Preferences.Describe() + ")");
                            break;
                        }
                        Say(Preferences.Describe());
                        break;
                    }

                    case "slot":
                    {
                        int n;
                        if (arg1 == null || !int.TryParse(arg1, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                        {
                            Say("usage: yoink slot <1-8>");
                            break;
                        }
                        TestRig.EquipSlot(n - 1);
                        break;
                    }

                    case "dump":
                        if (arg1 != null) TestRig.FindInViewmodel(arg1);
                        else TestRig.DumpEquipContainer();
                        break;

                    case "vm":
                    {
                        // Placing a viewmodel by eye means rebuilding for every centimetre unless the numbers are
                        // reachable from the console. They are dev-only; the shipped defaults live in code.
                        string what = arg1 != null ? arg1.ToLower() : string.Empty;
                        float s;

                        if (what == "show" || what.Length == 0)
                        {
                            Say(Yoink.Item.WinchItem.DescribeHold());
                            Say("nudge it: yoink vm move <x> <y> <z>   turn it: yoink vm turn <x> <y> <z>");
                            break;
                        }

                        // Relative steps, because dialling a hold position in absolute numbers means guessing the
                        // starting point every time. Type 'yoink vm move 0 0 -0.02' and watch it move 2 cm back.
                        float dx, dy, dz;
                        if ((what == "move" || what == "turn") && parts.Length >= 6
                            && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out dx)
                            && float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out dy)
                            && float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out dz))
                        {
                            if (what == "move") Yoink.Item.WinchItem.HeldPosition = Yoink.Item.WinchItem.HeldPosition + new Vector3(dx, dy, dz);
                            else Yoink.Item.WinchItem.HeldRotation = Yoink.Item.WinchItem.HeldRotation + new Vector3(dx, dy, dz);

                            Say(Yoink.Item.WinchItem.DescribeHold());
                            break;
                        }

                        // Lifts the whole first-person rig, hands included. Separate from 'move', which shifts only
                        // the tool inside the hand - raising the tool alone makes it float above the fingers.
                        if (what == "rig" && parts.Length >= 6
                            && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out dx)
                            && float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out dy)
                            && float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out dz))
                        {
                            Yoink.Item.WinchItem.RigOffset = Yoink.Item.WinchItem.RigOffset + new Vector3(dx, dy, dz);
                            Say("rig offset " + Yoink.Item.WinchItem.RigOffset.ToString("F3")
                                + " - watch the [Frame] lines in the log for where the muzzle lands.");
                            break;
                        }

                        if (what == "hand" && parts.Length >= 4)
                        {
                            Yoink.Item.WinchItem.PreferHand = parts[3].ToLower() == "on";
                            Yoink.Item.WinchItem.RefreshViewmodel();
                            Say("hand parenting " + (Yoink.Item.WinchItem.PreferHand ? "on" : "off") + ". " + ViewmodelState());
                            break;
                        }

                        if (what == "arms" && parts.Length >= 4)
                        {
                            Yoink.Item.WinchItem.ShowArms = parts[3].ToLower() == "on";
                            Yoink.Item.WinchItem.RefreshViewmodel();
                            Say("arms " + (Yoink.Item.WinchItem.ShowArms ? "on" : "off") + ". " + ViewmodelState());
                            break;
                        }

                        if (what == "scale" && parts.Length >= 4
                            && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out s))
                        {
                            Yoink.Item.WinchItem.ViewmodelScale = s;
                            Yoink.Item.WinchItem.HandScale = s;
                            Yoink.Item.WinchItem.HeldScale = s;
                            Yoink.Item.WinchItem.RefreshViewmodel();
                            Say(ViewmodelState());
                            break;
                        }

                        float x, y, z;
                        if ((what == "pos" || what == "rot" || what == "muzzle") && parts.Length >= 6
                            && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                            && float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                            && float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                        {
                            if (what == "muzzle") Yoink.Item.WinchItem.MuzzleOverride = new Vector3(x, y, z);
                            else if (what == "pos") { Yoink.Item.WinchItem.ViewmodelPosition = new Vector3(x, y, z); Yoink.Item.WinchItem.HandPosition = new Vector3(x, y, z); Yoink.Item.WinchItem.HeldPosition = new Vector3(x, y, z); }
                            else { Yoink.Item.WinchItem.ViewmodelRotation = new Vector3(x, y, z); Yoink.Item.WinchItem.HandRotation = new Vector3(x, y, z); Yoink.Item.WinchItem.HeldRotation = new Vector3(x, y, z); }

                            Yoink.Item.WinchItem.RefreshViewmodel();
                            Say(ViewmodelState());
                            break;
                        }

                        Say("usage: yoink vm <pos|rot|muzzle|rig> <x> <y> <z>  |  yoink vm scale <s>  |  yoink vm arms <on|off>");
                        Say(ViewmodelState());
                        break;
                    }

                    case "test":
                    {
                        float dist = 8f;
                        if (arg1 != null && !float.TryParse(arg1, NumberStyles.Float, CultureInfo.InvariantCulture, out dist))
                        {
                            Say("usage: yoink test [distance]");
                            break;
                        }
                        TestRig.SpawnVehicleAhead(dist);
                        break;
                    }

                    default:
                        Say("unknown subcommand '" + sub + "'. Try 'yoink help'.");
                        break;
                }
            }
            catch (Exception e) { Core.Log.Warning("[Yoink] console command failed: " + e.Message); }

            return true;   // ours - swallow it either way
        }

        private static string ViewmodelState()
        {
            return "viewmodel pos=" + Yoink.Item.WinchItem.ViewmodelPosition.ToString("F2")
                 + " rot=" + Yoink.Item.WinchItem.ViewmodelRotation.ToString("F0")
                 + " scale=" + Yoink.Item.WinchItem.ViewmodelScale.ToString("F2", CultureInfo.InvariantCulture)
                 + " muzzle=" + Yoink.Item.WinchItem.MuzzleLocal.ToString("F2")
                 + " hand=" + Yoink.Item.WinchItem.HandPosition.ToString("F2") + "/" + Yoink.Item.WinchItem.HandRotation.ToString("F0")
                 + " handScale=" + Yoink.Item.WinchItem.HandScale.ToString("F2", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Answers go to the Melon log immediately and to the in-game console on the next frame. Writing into the
        /// console from here would land in the middle of its own SubmitCommand, so the lines are queued instead.
        /// </summary>
        internal static void Say(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            Core.Log.Msg("[Yoink] " + line);
            _pending.Add("[Yoink] " + line);
        }

        /// <summary>Flushes queued answers into the in-game console. Called once per frame from Core.</summary>
        internal static void FlushToConsole()
        {
            if (_pending.Count == 0) return;
            for (int i = 0; i < _pending.Count; i++)
            {
                try { Il2CppScheduleOne.Console.Log(_pending[i]); } catch { }
            }
            _pending.Clear();
        }
    }

    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), nameof(Il2CppScheduleOne.Console.SubmitCommand), new Type[] { typeof(string) })]
    internal static class Yoink_Console_SubmitCommand_String_Patch
    {
        private static bool Prefix(string args)
        {
            try { return !DebugConsolePatch.TryHandle(args); } catch { return true; }
        }
    }

    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), nameof(Il2CppScheduleOne.Console.SubmitCommand), new Type[] { typeof(Il2CppSystem.Collections.Generic.List<string>) })]
    internal static class Yoink_Console_SubmitCommand_List_Patch
    {
        private static bool Prefix(Il2CppSystem.Collections.Generic.List<string> args)
        {
            try { return !DebugConsolePatch.TryHandle(args); } catch { return true; }
        }
    }
}
#endif
