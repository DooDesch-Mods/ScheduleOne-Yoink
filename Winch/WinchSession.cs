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

        /// <summary>
        /// Hooks the nearest person within <paramref name="radius"/>, ignoring where the camera points.
        ///
        /// Same reason <see cref="HookNearest"/> exists: knocking somebody down and dragging them is a path that
        /// has to be runnable from a script, or it only ever gets checked by hand and reaches players unverified.
        /// </summary>
        internal static bool HookNearestPerson(float radius, out string message)
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
            if (!WinchTarget.TryAcquireNearestPerson(origin, radius, out t, out reason))
            {
                message = "no hold: " + reason;
                return false;
            }

            Attach(t);
            message = "hooked " + t.Describe() + " at " + Distance().ToString("F1", CultureInfo.InvariantCulture) + "m (nearest person)";
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

            // A PLAYER is sitting in it: their machine simulates that vehicle, so ours must not fight it. An NPC at
            // the wheel is not that case - the host still owns those, and handing them over meant nobody applied
            // the pull at all.
            if (_target.Vehicle != null && VehicleGrip.HasPlayerDriver(_target.Vehicle))
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

            // Ours to apply. Free the wheels first, or the handbrake eats the whole pull - and if an NPC is at the
            // wheel, keep freeing them, because that one drives its throttle and steering back every single frame.
            if (_target.Vehicle != null)
            {
                VehicleGrip.TakeShared(_target.Vehicle);
                if (VehicleGrip.HasNpcDriver(_target.Vehicle))
                    Core.Log.Msg("[Winch] " + _target.Label + " has " + VehicleGrip.DescribeDriver(_target.Vehicle)
                               + " at the wheel - holding it in neutral while the hook is in.");
            }
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
            if (_target != null) _target.Release();
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

            // A hooked person who is back on their feet has nothing left to pull on: standing up turns every ragdoll
            // rigidbody kinematic. Something outside the winch did that - being taken to the medical centre, or
            // waking up from unconscious - and a rope tied to a body that cannot move reads as a broken winch, so
            // let go and say why.
            if (_target.Npc != null && !NpcGrip.IsDown(_target.Npc))
            {
                Core.Log.Msg("[Winch] " + _target.Label + " got back on their feet - hook released.");
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
        /// <summary>
        /// The anchor the PHYSICS uses, which is deliberately not the one the rope is drawn from.
        ///
        /// The drawn anchor rides on the camera, and the camera bobs, snaps and turns faster than any winch could be
        /// carried. Feeding that straight into the force model makes the anchor an infinite-mass energy source: every
        /// head movement is a free tug on the load, which is one of the things that made light targets thrash. This
        /// one chases the drawn anchor with a short time constant and a speed limit, and reports how fast it is
        /// moving so closing speed can be measured in its frame rather than the world's.
        /// </summary>
        private static Vector3 _physicsAnchor;
        private static Vector3 _physicsAnchorVelocity;
        private static bool _physicsAnchorValid;

        private const float AnchorFollowTime = 0.06f;   // seconds to close most of the gap to the drawn anchor
        private const float AnchorMaxSpeed = 10f;       // m/s; a teleport must not become a whip-crack

        private static void StepPhysicsAnchor(Vector3 drawn, float dt)
        {
            if (!_physicsAnchorValid || dt <= 0f)
            {
                _physicsAnchor = drawn;
                _physicsAnchorVelocity = Vector3.zero;
                _physicsAnchorValid = true;
                return;
            }

            Vector3 step = (drawn - _physicsAnchor) * (1f - Mathf.Exp(-dt / AnchorFollowTime));
            step = Vector3.ClampMagnitude(step, AnchorMaxSpeed * dt);

            _physicsAnchor += step;
            _physicsAnchorVelocity = step / dt;
        }

        internal static void FixedTick()
        {
            if (!_pulling || _target == null || !_target.Alive)
            {
                if (ReelRateSmoothed > 0f || Stalled) TrackRate(0f, false);
                _physicsAnchorValid = false;   // a fresh hook starts from where the anchor actually is
                return;
            }

            StepPhysicsAnchor(_pullAnchor, Time.fixedDeltaTime);

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
            bool applied = _target.Npc != null
                ? PullPhysics.ApplyToRagdoll(NpcGrip.PartsOf(_target.Npc), rb, _target.PivotWorld, _physicsAnchor, _physicsAnchorVelocity, out dist, out along)
                : PullPhysics.Apply(rb, _target.PivotWorld, _physicsAnchor, _physicsAnchorVelocity, out dist, out along);
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
                + (applied ? "" : " (at cap, coasting)")
#if DEBUG
                + "  " + PullPhysics.DescribeLastStep()
#endif
                );
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
            NpcGrip.ReleaseAll();
            RemotePulls.Clear();
            if (_rope != null) { _rope.Destroy(); _rope = null; }
        }

        internal static float Distance()
        {
            if (_target == null || !_target.Alive) return 0f;
            return Vector3.Distance(_target.PivotWorld, _pullAnchor);
        }

        /// <summary>Mass of the hooked target in kg, or 0 with nothing on the hook. For readouts.</summary>
        internal static float TargetMass()
        {
            try { return _target != null && _target.Alive ? _target.Mass : 0f; }
            catch { return 0f; }
        }

        /// <summary>How fast the hooked target is moving, in m/s. For readouts.</summary>
        internal static float TargetSpeed()
        {
            try { return _target != null && _target.Alive ? _target.Rb.velocity.magnitude : 0f; }
            catch { return 0f; }
        }

        /// <summary>Short label of what is on the hook, for a panel that has no room for the full status line.</summary>
        internal static string TargetLabel()
        {
            try { return _target != null && _target.Alive ? _target.Label : "-"; }
            catch { return "-"; }
        }

        internal static string StatusLine()
        {
            if (!Hooked) return "no hook. " + Preferences.Describe();

            CultureInfo inv = CultureInfo.InvariantCulture;
            float speed = 0f;
            try { speed = _target.Rb.velocity.magnitude; } catch { }

            string owner = _delegatedToHost ? " [host pulls]" : (_delegatedToDriver ? " [driver pulls]" : "");
            if (_target.Npc != null) owner += NpcGrip.IsDown(_target.Npc) ? " [down]" : " [back on their feet]";

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

#if DEBUG
        /// <summary>
        /// Lists every vehicle the game knows about, nearest first, with the state that decides whether a winch can
        /// move it. Console only.
        ///
        /// The crosshair probe answers "why is THIS one not moving"; this answers "what kinds of vehicle are even
        /// out there", which is the question when a report says a whole category ignores the winch. Reading it out
        /// of VehicleManager rather than a physics query means parked, invisible and far-away ones show up too -
        /// exactly the ones a query near the player would miss.
        /// </summary>
        internal static string ListVehicles(int max)
        {
            var mgr = Il2CppScheduleOne.Vehicles.VehicleManager.Instance;
            if (mgr == null || mgr.AllVehicles == null) return "no vehicle manager";

            Vector3 me;
            try { me = Player.Local.transform.position; } catch { me = Vector3.zero; }

            CultureInfo inv = CultureInfo.InvariantCulture;
            var rows = new List<KeyValuePair<float, string>>();

            for (int i = 0; i < mgr.AllVehicles.Count; i++)
            {
                var v = mgr.AllVehicles[i];
                if (v == null) continue;

                try
                {
                    float d = Vector3.Distance(me, v.transform.position);
                    string name = string.IsNullOrEmpty(v.VehicleName) ? v.gameObject.name : v.VehicleName;

                    string row = d.ToString("F0", inv) + "m " + name
                               + (v.Rb != null && v.Rb.isKinematic ? " KINEMATIC" : " dynamic")
                               + (v.isParked ? " parked" : "")
                               + (v.IsPlayerOwned ? " mine" : "")
                               + (v.IsVisible ? "" : " hidden")
                               + " sim=" + v.IsPhysicallySimulated
                               + " brake=" + v.HandbrakeApplied
                               + " driver=" + VehicleGrip.DescribeDriver(v);

                    rows.Add(new KeyValuePair<float, string>(d, row));
                }
                catch { }
            }

            if (rows.Count == 0) return "no vehicles in the world";
            rows.Sort((a, b) => a.Key.CompareTo(b.Key));

            var sb = new System.Text.StringBuilder();
            sb.Append(rows.Count).Append(" vehicle(s):");
            for (int i = 0; i < rows.Count && i < max; i++) sb.Append("\n  ").Append(rows[i].Value);
            return sb.ToString();
        }

        /// <summary>
        /// Reports what is under the crosshair and what would stop the winch moving it, WITHOUT hooking anything.
        ///
        /// This exists because "the pull does nothing" and "the pull is being cancelled" look identical from the
        /// outside, and every wrong guess in this mod's history was fixed by measuring instead. It reads the state
        /// that actually decides the outcome: whether the body is kinematic (the game switches vehicles in and out
        /// of physics every FixedUpdate), whether the wheels are braked, and who is driving.
        /// </summary>
        internal static string ProbeAhead()
        {
            Transform cam = Camera();
            if (cam == null) return "no player camera";

            RaycastHit hit;
            if (!Physics.Raycast(new Ray(cam.position, cam.forward), out hit, Preferences.HookRange,
                                 Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return "nothing within " + Mathf.RoundToInt(Preferences.HookRange) + "m";

            CultureInfo inv = CultureInfo.InvariantCulture;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            try
            {
                sb.Append(hit.collider.gameObject.name)
                  .Append(" layer=").Append(LayerMask.LayerToName(hit.collider.gameObject.layer))
                  .Append(" at ").Append(hit.distance.ToString("F1", inv)).Append('m');
            }
            catch { sb.Append("<collider unreadable>"); }

            Il2CppScheduleOne.NPCs.NPC npc = null;
            try { npc = hit.collider.GetComponentInParent<Il2CppScheduleOne.NPCs.NPC>(); } catch { }
            if (npc != null)
            {
                try
                {
                    sb.Append(" | person ").Append(NpcGrip.SafeName(npc))
                      .Append(" ragdolled=").Append(npc.Avatar != null && npc.Avatar.Ragdolled)
                      .Append(" conscious=").Append(npc.IsConscious)
                      .Append(" inVehicle=").Append(npc.IsInVehicle);
                }
                catch { sb.Append(" | person <unreadable>"); }
                return sb.ToString();
            }

            Rigidbody rb = hit.rigidbody;
            if (rb == null) { try { rb = hit.collider.GetComponentInParent<Rigidbody>(); } catch { } }
            if (rb == null) return sb.Append(" | no rigidbody").ToString();

            try
            {
                sb.Append(" | rb ").Append(rb.gameObject.name)
                  .Append(' ').Append(rb.mass.ToString("F0", inv)).Append("kg")
                  .Append(rb.isKinematic ? " KINEMATIC" : " dynamic");
            }
            catch { }

            Il2CppScheduleOne.Vehicles.LandVehicle v = null;
            try { v = hit.collider.GetComponentInParent<Il2CppScheduleOne.Vehicles.LandVehicle>(); } catch { }
            if (v == null) return sb.ToString();

            try
            {
                sb.Append(" | vehicle parked=").Append(v.isParked)
                  .Append(" simulated=").Append(v.IsPhysicallySimulated)
                  .Append(" handbrake=").Append(v.HandbrakeApplied)
                  .Append(" driver=").Append(VehicleGrip.DescribeDriver(v))
                  .Append(" override=").Append(v.overrideControls)
                  .Append(" throttle=").Append(v.currentThrottle.ToString("F2", inv))
                  .Append(" steer=").Append(v.steerOverride.ToString("F2", inv));
            }
            catch { }

            try
            {
                var agent = v.Agent;
                if (agent != null) sb.Append(" | agent driving=").Append(agent.AutoDriving).Append(" kinematicMode=").Append(agent.KinematicMode);
            }
            catch { }

            return sb.ToString();
        }
#endif

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
