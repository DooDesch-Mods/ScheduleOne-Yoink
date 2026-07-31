using System;
using System.Globalization;
using Il2CppScheduleOne;   // GameInput
using Yoink.Config;
using Yoink.Net;
using Yoink.Rope;

namespace Yoink.Winch
{
    /// <summary>
    /// The local player's hook (one at a time) and the pull it produces.
    ///
    /// Two different anchors, on purpose:
    /// * the PULL anchor is frozen when reeling starts, so the direction of pull is a straight line you can plan
    ///   rather than something that swings around with the mouse. It is also the seam the tow-rope extension slots
    ///   into later - the anchor is a point, and a second vehicle is just a different point.
    /// * the ROPE anchor follows the camera, because the rope has to stay attached to the thing in your hands.
    ///
    /// Who actually applies the force depends on the session (see <see cref="FixedTick"/>): single-player and the
    /// host do it here, a connected client asks the host, and a vehicle with a driver in it is handed to that
    /// driver's machine because that is where the game simulates it.
    /// </summary>
    internal static class WinchSession
    {
        private static WinchTarget _target;
        private static Vector3 _pullAnchor;
        private static bool _pulling;
        private static bool _holdPulling;           // true while the player holds the reel button
        private static float _pullUntil = -1f;      // time-boxed pull from the console; -1 = untimed
        private static bool _delegatedToHost;       // a client's pull: the host owns the force
        private static bool _delegatedToDriver;     // occupied vehicle: the driver's client owns the force
        private static VerletRope _rope;
#if DEBUG
        private static float _nextTraceTime;
#endif

        internal static bool Hooked => _target != null && _target.Alive;
        internal static bool Pulling => _pulling;

        /// <summary>
        /// How fast the load is currently coming in along the rope, in m/s. Measured in the physics step and read
        /// by the sound, so the crank's pitch reports what the winch is actually achieving rather than what it was
        /// asked for - straining against something stuck sounds different from rope running in free.
        /// </summary>
        internal static float ReelRate { get; private set; }

        /// <summary>
        /// The reel rate the sound should listen to: clamped, median-filtered and asymmetrically smoothed at
        /// physics cadence.
        ///
        /// The raw dot product is not a measurement of a winch turning - it carries collision response, suspension
        /// bounce, depenetration impulses and the mismatch between physics and render cadence. Feeding it straight
        /// into a pitch every frame is what made the crank warble. The rise is quick (0.10 s) so breaking free is
        /// heard immediately; the fall is slow (0.25 s) so a single ratchet gap or a kerb does not drop the sound
        /// out mid-stroke.
        /// </summary>
        internal static float ReelRateSmoothed { get; private set; }

        /// <summary>True while the winch is pulling hard at something that is not moving.</summary>
        internal static bool Stalled { get; private set; }

        /// <summary>
        /// How fast rope is running OUT, in m/s - the hook is attached and the gap is growing, because the player
        /// is walking away or the load is. A real ratchet winch clatters while it pays out, and silence there was
        /// the giveaway that this was a sound effect bolted onto a pull rather than a mechanism.
        /// </summary>
        internal static float PayOutRate { get; private set; }

        private static float _lastDistance = -1f;

        private static readonly float[] _rateWindow = new float[3];
        private static int _rateIndex;
        private static float _lowRateTime;

        private static void TrackRate(float rawAlongRope, bool demandingTension)
        {
            float dt = Time.fixedDeltaTime;
            float clamped = Mathf.Max(rawAlongRope, 0f);

            _rateWindow[_rateIndex % 3] = clamped;
            _rateIndex++;
            float median = Median(_rateWindow[0], _rateWindow[1], _rateWindow[2]);

            float tau = median > ReelRateSmoothed ? 0.10f : 0.25f;
            ReelRateSmoothed += (median - ReelRateSmoothed) * (1f - Mathf.Exp(-dt / tau));
            ReelRate = clamped;

            // Stalled is "asking for tension and getting no travel", held for a quarter second so a momentary
            // catch on a kerb does not flip the sound into its strain state.
            if (demandingTension && ReelRateSmoothed < 0.04f) _lowRateTime += dt;
            else _lowRateTime = 0f;

            Stalled = demandingTension && _lowRateTime >= 0.25f;
        }

        /// <summary>
        /// Measures rope running out, smoothed the same way the reel rate is. Only the growing direction counts:
        /// the shrinking one is what the pull already reports, and feeding both into one number would make the
        /// winch clatter while it is hauling something in.
        /// </summary>
        private static void TrackPayOut(float distance, float dt)
        {
            if (_lastDistance < 0f) { _lastDistance = distance; return; }

            float delta = (distance - _lastDistance) / Mathf.Max(dt, 0.0001f);
            _lastDistance = distance;

            float outward = Mathf.Max(delta, 0f);
            float tau = outward > PayOutRate ? 0.08f : 0.20f;
            PayOutRate += (outward - PayOutRate) * (1f - Mathf.Exp(-dt / tau));
            if (PayOutRate < 0.01f) PayOutRate = 0f;
        }

        private static float Median(float a, float b, float c)
        {
            return Mathf.Max(Mathf.Min(a, b), Mathf.Min(Mathf.Max(a, b), c));
        }

        /// <summary>Fires the hook where the player camera is looking.</summary>
        internal static bool HookFromCamera(out string message)
        {
            message = null;

            Transform cam = Camera();
            if (cam == null) { message = "no player camera - are you in the world?"; return false; }

            WinchTarget t;
            string reason;
            if (!WinchTarget.TryAcquire(new Ray(cam.position, cam.forward), Preferences.HookRange, out t, out reason))
            {
                message = "no hold: " + reason;
                return false;
            }

            Attach(t);
            message = "hooked " + t.Describe() + " at " + Distance().ToString("F1", CultureInfo.InvariantCulture) + "m";
            return true;
        }

        /// <summary>
        /// Hooks the nearest rigidbody within <paramref name="radius"/>, ignoring where the camera points.
        ///
        /// This exists so the winch can be driven end to end from the console, including by the MCP test harness,
        /// which can submit commands but cannot aim a mouse. Without it, every automated run would depend on a
        /// target happening to sit under the crosshair - and an untestable path is one that reaches players
        /// unverified.
        /// </summary>
        internal static bool HookNearest(float radius, out string message)
        {
            message = null;

            Vector3 origin;
            try
            {
                Player local = Player.Local;
                if (local == null) { message = "no local player"; return false; }
                origin = local.transform.position + Vector3.up * 1.0f;
            }
            catch { message = "no local player"; return false; }

            WinchTarget t;
            string reason;
            if (!WinchTarget.TryAcquireNearest(origin, radius, out t, out reason))
            {
                message = "no hold: " + reason;
                return false;
            }

            Attach(t);
            message = "hooked " + t.Describe() + " at " + Distance().ToString("F1", CultureInfo.InvariantCulture) + "m (nearest)";
            return true;
        }

        private static void Attach(WinchTarget t)
        {
            Drop();   // one hook at a time

            _target = t;
            _pullAnchor = MuzzlePoint();
            _pulling = false;
            _pullUntil = -1f;
            t.PrepareForPull();

            EnsureRope();
            if (_rope != null) _rope.Attach(t.PivotWorld, MuzzlePoint());
        }

        /// <summary>Starts reeling. <paramref name="seconds"/> above 0 makes it a time-boxed, repeatable pull.</summary>
        internal static bool Pull(float seconds, out string message)
        {
            if (!Hooked) { message = "nothing hooked"; return false; }

            // The direction is decided here, not when the hook landed - walk around, aim from a better angle, pull.
            _pullAnchor = MuzzlePoint();
            _pulling = true;
            _pullUntil = seconds > 0f ? Time.time + seconds : -1f;
            _target.PrepareForPull();
            RoutePull();
#if DEBUG
            _nextTraceTime = 0f;
#endif

            message = seconds > 0f
                ? "pulling for " + seconds.ToString("F1", CultureInfo.InvariantCulture) + "s"
                : "pulling until yoinkstop";
            return true;
        }

        /// <summary>Decides who applies this pull, and tells them.</summary>
        private static void RoutePull()
        {
            _delegatedToHost = false;
            _delegatedToDriver = false;

            bool online = YoinkNet.Online;
            bool host = YoinkNet.IsServer;

            // A connected client: the host owns unoccupied-vehicle physics, so send the intent and let it pull.
            if (online && !host && _target.IsNetworked)
            {
                _delegatedToHost = true;
                YoinkNet.SendToHost(new YoinkMsg
                {
                    Op = YoinkOp.PullStart,
                    TargetId = _target.NetId,
                    PivotLocal = _target.PivotLocal,
                    Anchor = _pullAnchor,
                });
                return;
            }

            // Somebody is sitting in it: their machine simulates that vehicle, so ours must not fight it.
            if (_target.Vehicle != null && VehicleGrip.IsOccupied(_target.Vehicle))
            {
                bool weDrive = false;
                try { weDrive = _target.Vehicle.LocalPlayerIsDriver; } catch { }
                if (!weDrive)
                {
                    _delegatedToDriver = true;
                    if (online && host && _target.IsNetworked)
                    {
                        YoinkNet.BroadcastToAll(new YoinkMsg
                        {
                            Op = YoinkOp.DriverPull,
                            TargetId = _target.NetId,
                            PivotLocal = _target.PivotLocal,
                            Anchor = _pullAnchor,
                        });
                    }
                    Core.Log.Msg("[Winch] someone is driving - the pull is theirs to feel, not ours to apply.");
                    return;
                }
            }

            // Ours to apply. Free the wheels first, or the handbrake eats the whole pull.
            if (_target.Vehicle != null) VehicleGrip.TakeShared(_target.Vehicle);
        }

        internal static void Stop()
        {
            bool was = _pulling;
            _pulling = false;
            _pullUntil = -1f;
            ReelRate = 0f;
            Stalled = false;
            _lowRateTime = 0f;
            if (!was) return;

            try
            {
                if (_target != null && _target.IsNetworked && YoinkNet.Online)
                {
                    if (_delegatedToHost) YoinkNet.SendToHost(new YoinkMsg { Op = YoinkOp.PullStop, TargetId = _target.NetId });
                    else if (_delegatedToDriver && YoinkNet.IsServer) YoinkNet.BroadcastToAll(new YoinkMsg { Op = YoinkOp.DriverStop, TargetId = _target.NetId });
                }
            }
            catch (Exception e) { Core.Log.Warning("[Winch] stop message failed: " + e.Message); }

            if (_target != null && _target.Vehicle != null && !_delegatedToHost) VehicleGrip.ReleaseShared(_target.Vehicle);
            _delegatedToHost = false;
            _delegatedToDriver = false;
        }

        internal static void Drop()
        {
            Stop();
            PayOutRate = 0f;
            _lastDistance = -1f;
            if (_target != null && _target.Vehicle != null) VehicleGrip.ReleaseShared(_target.Vehicle);
            _target = null;
            _holdPulling = false;
            if (_rope != null) _rope.Detach();
        }

        /// <summary>
        /// Hold the secondary mouse button to reel while a hook is attached - the player-facing control from the
        /// design ("hold the button, the ratchet cranks"). The anchor is re-frozen on every press, which is what
        /// makes "walk around, pull again from a better angle" work.
        ///
        /// This is a player function, not dev tooling: the console commands stay the way every test and every
        /// automated run drives the winch.
        /// </summary>
        private static void HandleHoldToReel()
        {
            if (!Hooked) return;

            bool allowed;
            try
            {
                if (GameInput.IsTyping) allowed = false;
                else
                {
                    PlayerCamera pc = PlayerSingleton<PlayerCamera>.Instance;
                    allowed = pc == null || pc.activeUIElementCount == 0;   // not while the phone or a menu is open
                }
            }
            catch { allowed = false; }

            bool held = allowed && Input.ReelHeld();

            if (held && !_holdPulling)
            {
                _holdPulling = true;
                string msg;
                Pull(0f, out msg);   // untimed: runs until the button comes up
                Core.Log.Msg("[Winch] reeling. " + StatusLine());
            }
            else if (held && _holdPulling)
            {
                // Holding the button means "keep hauling it to me", so the anchor follows the player instead of
                // staying where it was pressed - walk backwards and the load comes with you. A pull started from
                // the console keeps its frozen anchor, which is what makes those runs repeatable.
                _pullAnchor = MuzzlePoint();

                // And a load that has arrived does not end the pull, it just stops being pulled. Ending it meant
                // letting go and pressing again every time something drifted in close, which is exactly what it
                // felt like.
                if (!_pulling) _pulling = true;
            }
            else if (!held && _holdPulling)
            {
                _holdPulling = false;
                Stop();
                Core.Log.Msg("[Winch] released. " + StatusLine());
            }
        }

        /// <summary>Frame work: input, rope simulation and the checks that can end a hook.</summary>
        internal static void Tick(float dt)
        {
            if (_target == null) return;

            HandleHoldToReel();

            if (!_target.Alive)
            {
                Core.Log.Msg("[Winch] target is gone - hook released.");
                Drop();
                return;
            }

            if (_pulling && _pullUntil > 0f && Time.time >= _pullUntil)
            {
                Stop();
                Core.Log.Msg("[Winch] pull finished. " + StatusLine());
            }

            float dist = Distance();
            TrackPayOut(dist, dt);

            if (dist > Preferences.BreakDistance)
            {
                Core.Log.Msg("[Winch] rope snapped at " + dist.ToString("F1", CultureInfo.InvariantCulture) + "m.");
                Drop();
                return;
            }
        }

        /// <summary>
        /// Rope work, deliberately in LateUpdate.
        ///
        /// The rope starts at the held model's muzzle, and that model rides the viewmodel, which the game moves
        /// AFTER Update - camera look and weapon sway both land later in the frame. Reading the muzzle in Update
        /// therefore sampled last frame's position, and while walking or turning quickly the rope's end visibly
        /// trailed the winch instead of sitting on it. Sampling in LateUpdate reads the final position of the
        /// frame that is about to be drawn.
        /// </summary>
        internal static void LateTick(float dt)
        {
            if (_target == null || !_target.Alive || _rope == null) return;

            _rope.SetEnds(_target.PivotWorld, MuzzlePoint());
            _rope.Simulate(dt, _pulling);
        }

        /// <summary>Physics work: the pull, applied at the pivot so the load rotates as it comes free.</summary>
        internal static void FixedTick()
        {
            if (!_pulling || _target == null || !_target.Alive)
            {
                if (ReelRateSmoothed > 0f || Stalled) TrackRate(0f, false);
                return;
            }

            Rigidbody rb = _target.Rb;

            // Measure even when the force belongs to another machine. The load still moves here, and audio that
            // went silent on a client's screen while a car visibly slid toward them would be a worse lie than a
            // slightly late one.
            if (_delegatedToHost || _delegatedToDriver)
            {
                MeasureOnly(rb);
                return;
            }

            float dist, along;
            bool applied = PullPhysics.Apply(rb, _target.PivotWorld, _pullAnchor, out dist, out along);
            TrackRate(along, dist > Preferences.StopDistance);

            if (!applied && dist > 0f && dist <= Preferences.StopDistance)
            {
                // While the button is held the winch stays engaged and simply idles at arm's length; a timed pull
                // from the console is a discrete action, so that one ends here.
                if (!_holdPulling)
                {
                    Stop();
                    Core.Log.Msg("[Winch] load is in reach - stopped reeling.");
                }
                return;
            }

            bool kinematic = false;
            try { kinematic = rb != null && rb.isKinematic; } catch { }
            if (kinematic)
            {
                // Not something we can fix from here, but silence would be worse than a line in the log.
                Trace("target is kinematic - the game is holding it still, no force applied");
                return;
            }

            Trace("dist=" + dist.ToString("F2", CultureInfo.InvariantCulture)
                + " along=" + along.ToString("F2", CultureInfo.InvariantCulture)
                + (applied ? "" : " (at cap, coasting)"));
        }

        /// <summary>Tracks the rate without applying force - used when another machine owns the pull.</summary>
        private static void MeasureOnly(Rigidbody rb)
        {
            try
            {
                Vector3 toAnchor = _pullAnchor - _target.PivotWorld;
                float dist = toAnchor.magnitude;
                float along = dist > 0.001f ? Vector3.Dot(rb.velocity, toAnchor / dist) : 0f;
                TrackRate(along, dist > Preferences.StopDistance);
            }
            catch { }
        }

        internal static void Reset()
        {
            Drop();
            VehicleGrip.ReleaseAll();
            RemotePulls.Clear();
            if (_rope != null) { _rope.Destroy(); _rope = null; }
        }

        internal static float Distance()
        {
            if (_target == null || !_target.Alive) return 0f;
            return Vector3.Distance(_target.PivotWorld, _pullAnchor);
        }

        internal static string StatusLine()
        {
            if (!Hooked) return "no hook. " + Preferences.Describe();

            CultureInfo inv = CultureInfo.InvariantCulture;
            float speed = 0f;
            try { speed = _target.Rb.velocity.magnitude; } catch { }

            string owner = _delegatedToHost ? " [host pulls]" : (_delegatedToDriver ? " [driver pulls]" : "");

            return _target.Describe()
                 + " dist=" + Distance().ToString("F1", inv) + "m"
                 + " speed=" + speed.ToString("F2", inv) + "m/s"
                 + (_pulling ? " PULLING" : " idle")
                 + owner
                 + " | " + Preferences.Describe();
        }

        /// <summary>
        /// Where the rope leaves the winch. The held model's own muzzle when there is one, so the rope reads as
        /// part of the tool rather than as a line starting in mid-air; the camera-relative fallback keeps the
        /// console path working when nothing is equipped.
        /// </summary>
        private static Vector3 MuzzlePoint()
        {
            Vector3 fromModel;
            if (Yoink.Item.WinchItem.TryGetMuzzle(out fromModel)) return fromModel;

            Transform cam = Camera();
            if (cam == null) return _pullAnchor;
            return cam.position + cam.forward * 0.7f + cam.right * 0.2f - cam.up * 0.3f;
        }

        /// <summary>True when the rope is coming out of the actual model, so the eyelet is not needed.</summary>
        internal static bool RopeEndsInModel()
        {
            Vector3 unused;
            return Yoink.Item.WinchItem.TryGetMuzzle(out unused);
        }

        private static Transform Camera()
        {
            try
            {
                PlayerCamera pc = PlayerSingleton<PlayerCamera>.Instance;
                return pc != null ? pc.transform : null;
            }
            catch { return null; }
        }

        private static void EnsureRope()
        {
            if (_rope == null) _rope = new VerletRope(Preferences.RopeSegments);
        }

        /// <summary>Once-per-second physics trace, so a pull that does nothing says why. Debug builds only.</summary>
        [System.Diagnostics.Conditional("DEBUG")]
        private static void Trace(string msg)
        {
#if DEBUG
            if (Time.time < _nextTraceTime) return;
            _nextTraceTime = Time.time + 1f;
            Core.Log.Msg("[Winch] " + msg);
#endif
        }
    }
}
