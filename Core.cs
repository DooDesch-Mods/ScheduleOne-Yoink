using System;
using MelonLoader;
using Yoink.Config;
using Yoink.Item;
using Yoink.Net;
using Yoink.Winch;

[assembly: MelonInfo(typeof(Yoink.Core), "Yoink", "1.2.1", "DooDesch", null)]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace Yoink
{
    /// <summary>
    /// MelonLoader entry point for Yoink - a winch that hooks any rigidbody at the exact point you aimed at and
    /// drags it out. It exists for the situation vanilla has no answer to: a vehicle wedged where you cannot get
    /// in, which the game's self-righting (driver only) and its y &lt; -20 reset both leave stuck forever.
    ///
    /// Buy the winch, hold it, click to hook, hold the secondary button to reel. In co-op the host owns the
    /// physics of everything nobody is sitting in, and a vehicle with a driver is pulled on that driver's machine
    /// - see Net/RemotePulls.cs for why those are two different paths.
    /// </summary>
    public sealed class Core : MelonMod
    {
        public static Core Instance { get; private set; }
        public static MelonLogger.Instance Log { get; private set; }

        private bool _inWorld;
        private float _shopRetryAt;

        public override void OnInitializeMelon()
        {
            Instance = this;
            Log = LoggerInstance;

            Preferences.Initialize();

            try { HarmonyInstance.PatchAll(); }
            catch (Exception e) { Log.Warning("[Core] Harmony patch failed: " + e.Message); }

            try
            {
                QuestRpcPatches.Apply(HarmonyInstance);
                RemotePulls.Install();
            }
            catch (Exception e) { Log.Warning("[Core] co-op bus setup failed - single-player is unaffected: " + e.Message); }

#if DEBUG
            Log.Msg("Yoink v1.2.0 (DEBUG) - winch. Buy it in the hardware store, or type 'yoink help' in the console.");
#else
            Log.Msg("Yoink v1.2.0 - winch.");
#endif
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            _inWorld = sceneName == "Main";
            if (!_inWorld)
            {
                WinchSession.Reset();
                WinchItem.ResetSession();
                WinchShop.ResetSession();
                WinchAim.Reset();
                Audio.WinchSound.ResetSession();
            }
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            _inWorld = false;
            WinchSession.Reset();
            WinchItem.ResetSession();
            WinchShop.ResetSession();
            WinchAim.Reset();
            Audio.WinchSound.ResetSession();
        }

        public override void OnUpdate()
        {
            if (!_inWorld) return;

#if DEBUG
            Debugging.ConsoleRegistration.EnsureRegistered();
            Debugging.DebugConsolePatch.FlushToConsole();
            Debugging.TestRig.Tick();
#endif

            WinchItem.EnsureRegistered();

            // Shops are scene objects and can be later than we are, so keep asking - cheaply.
            if (Time.time >= _shopRetryAt)
            {
                _shopRetryAt = Time.time + 2f;
                WinchShop.EnsureListed();
            }

            WinchItem.TickControls();
            WinchSession.Tick(Time.deltaTime);

            // A hooked person is not allowed to stand up. The game's own recovery would put them back on their feet
            // a second after they stop sliding, which mid-tow means a rope attached to a limb that just went
            // kinematic - see Winch/NpcGrip.cs.
            NpcGrip.Tick();
        }

        /// <summary>The rope is drawn here, after the game has finished moving the camera and the viewmodel.</summary>
        public override void OnLateUpdate()
        {
            if (!_inWorld) return;
#if DEBUG
            WinchItem.DumpMuzzleLateOnce();
            WinchItem.ReportFraming();
#endif
            WinchSession.LateTick(Time.deltaTime);

            // Deliberately here and not in OnUpdate. VehicleAgent writes the throttle and steering it wants in its
            // own LateUpdate, so this is the first point in the frame where our neutral can be the last word before
            // the next physics step - see Winch/VehicleGrip.cs.
            VehicleGrip.TickHeld();
        }

        public override void OnFixedUpdate()
        {
            if (!_inWorld) return;
            WinchSession.FixedTick();
            RemotePulls.FixedTick();
        }
    }
}
