using System;
using System.Collections.Generic;
using Il2CppScheduleOne.Vehicles;      // LandVehicle
using Il2CppScheduleOne.Vehicles.AI;   // VehicleAgent

namespace Yoink.Winch
{
    /// <summary>
    /// Puts hooked vehicles in neutral for as long as a hook is on them, and puts them back afterwards.
    ///
    /// This is the difference between a winch that works and one that does nothing. An unoccupied vehicle drives
    /// with its handbrake permanently on: <c>ApplyThrottle</c> takes its no-driver branch, sets <c>flag = true</c>
    /// because <c>!IsOccupied</c>, and writes <c>brakeTorque = handBrakeForce</c> to the handbrake wheels on every
    /// FixedUpdate (LandVehicle.cs:851-878). Locked wheels plus tyre friction hold 1200 kg against any pull worth
    /// calling a winch - measured in game, 12 kN moved the car 10 cm in four seconds.
    ///
    /// Rather than answering that with absurd force, we use the hook the game already has: <c>overrideControls</c>
    /// makes ApplyThrottle take its driver branch instead, where zero throttle and no handbrake mean
    /// <c>motorTorque = 0.0001f</c> and <c>brakeTorque = 0f</c> on every wheel. The car rolls, exactly like a real
    /// one being towed out of a ditch - and free-rolling wheels are also what lets it slide out of a wedge at all.
    ///
    /// A vehicle an NPC is driving needs the same neutral, and getting it takes more than one write. Its
    /// <see cref="VehicleAgent"/> already owns <c>overrideControls</c> and rewrites <c>throttleOverride</c> and
    /// <c>steerOverride</c> from a PID in its own LateUpdate, every frame - so a single write at hook time is gone
    /// before the next physics step, and the car goes on driving and braking against the cable. That is why a
    /// police cruiser behaved as if the winch were not attached at all: whenever the PID asked for less speed than
    /// the car had, ApplyThrottle put <c>brakeTorque</c> on all four wheels, which is the locked-wheel case again.
    /// <see cref="TickHeld"/> therefore re-zeroes both overrides after the agent has written them, so the last word
    /// before the next FixedUpdate is ours. The AI keeps navigating and steers again the moment the hook comes off.
    ///
    /// Held vehicles are tracked by instance id rather than by reference: every interop cast hands out a fresh
    /// wrapper, so reference equality would miss a vehicle we are already holding and record our own override as
    /// the state to restore - after which the handbrake would never come back.
    /// </summary>
    internal static class VehicleGrip
    {
        private struct Saved
        {
            internal LandVehicle Vehicle;
            internal bool OverrideControls;
            internal float ThrottleOverride;
            internal float SteerOverride;

            /// <summary>Set when this vehicle has AI at the wheel, which has to be held down every frame.</summary>
            internal VehicleAgent Agent;
            internal bool StuckDetection;
        }

        private static readonly Dictionary<int, Saved> _held = new Dictionary<int, Saved>();

        /// <summary>How many vehicles are currently in neutral because a hook is on them. For readouts.</summary>
        internal static int HeldCount => _held.Count;

        /// <summary>Releases the handbrake, remembering what to restore. Idempotent per vehicle.</summary>
        internal static void TakeShared(LandVehicle v)
        {
            if (v == null) return;

            try
            {
                int id = v.GetInstanceID();
                if (_held.ContainsKey(id)) return;

                VehicleAgent agent = null;
                try { agent = v.Agent; } catch { }

                Saved s = new Saved
                {
                    Vehicle = v,
                    OverrideControls = v.overrideControls,
                    ThrottleOverride = v.throttleOverride,
                    SteerOverride = v.steerOverride,
                    Agent = agent,
                    StuckDetection = true,
                };

                // The agent teleports a vehicle back onto the road network after ten seconds of not moving
                // (VehicleAgent.UpdateStuckDetection). A car being winched out of a wedge is, by definition, a car
                // that is not moving much - so leaving that on would vanish the load mid-pull.
                try
                {
                    if (agent != null && agent.Flags != null)
                    {
                        s.StuckDetection = agent.Flags.StuckDetection;
                        agent.Flags.StuckDetection = false;
                    }
                }
                catch { }

                _held[id] = s;

                Neutralise(v);
            }
            catch (Exception e) { Core.Log.Warning("[Grip] could not release the handbrake: " + e.Message); }
        }

        /// <summary>
        /// Re-asserts neutral on every held vehicle. Called from OnLateUpdate, which is the point in the frame
        /// AFTER every MonoBehaviour LateUpdate has run - including <c>VehicleAgent.LateUpdate</c>, which is where
        /// the AI writes the throttle and steering it wants. Writing here means our value is the one
        /// <c>LandVehicle.Update</c> reads next frame, and therefore the one ApplyThrottle acts on.
        /// </summary>
        internal static void TickHeld()
        {
            if (_held.Count == 0) return;

            List<int> gone = null;      // destroyed: forget them
            List<int> claimed = null;   // somebody got in: hand the vehicle back

            foreach (KeyValuePair<int, Saved> kv in _held)
            {
                LandVehicle v = kv.Value.Vehicle;
                bool alive;
                try { alive = v != null; } catch { alive = false; }
                if (!alive) { (gone ??= new List<int>()).Add(kv.Key); continue; }

                // Neutral works by taking overrideControls, and LandVehicle.Update checks that BEFORE it reads the
                // driver's input - so holding it on a vehicle a player has just climbed into would take their
                // throttle and steering away. Give it back instead; a towed car somebody gets into is theirs now.
                if (HasPlayerDriver(v)) { (claimed ??= new List<int>()).Add(kv.Key); continue; }

                Neutralise(v);
            }

            if (gone != null)
                for (int i = 0; i < gone.Count; i++) _held.Remove(gone[i]);

            if (claimed != null)
                for (int i = 0; i < claimed.Count; i++) Restore(claimed[i]);
        }

        /// <summary>Wheels rolling, nothing braking, nothing steering.</summary>
        private static void Neutralise(LandVehicle v)
        {
            try
            {
                v.throttleOverride = 0f;
                v.steerOverride = 0f;
                v.handbrakeOverride = false;
                v.overrideControls = true;

                // ApplyThrottle reads currentThrottle, which Update copies from throttleOverride. Writing it here
                // as well means the very next physics step is already free-rolling instead of the one after it.
                v.currentThrottle = 0f;
            }
            catch { }
        }

        /// <summary>Hands one vehicle back to the game exactly as it was found.</summary>
        internal static void ReleaseShared(LandVehicle v)
        {
            if (v == null) return;
            try { Restore(v.GetInstanceID()); }
            catch (Exception e) { Core.Log.Warning("[Grip] could not restore vehicle controls: " + e.Message); }
        }

        /// <summary>Hands every held vehicle back. Used on scene change, where holding on would leak the override.</summary>
        internal static void ReleaseAll()
        {
            List<int> ids = new List<int>(_held.Keys);
            for (int i = 0; i < ids.Count; i++) Restore(ids[i]);
            _held.Clear();
        }

        private static void Restore(int id)
        {
            if (!_held.TryGetValue(id, out Saved s)) return;
            _held.Remove(id);

            try
            {
                if (s.Agent != null && s.Agent.Flags != null) s.Agent.Flags.StuckDetection = s.StuckDetection;
            }
            catch { }

            try
            {
                LandVehicle v = s.Vehicle;
                if (v == null) return;
                v.throttleOverride = s.ThrottleOverride;
                v.steerOverride = s.SteerOverride;
                v.handbrakeOverride = false;
                v.overrideControls = s.OverrideControls;
            }
            catch (Exception e) { Core.Log.Warning("[Grip] restore failed: " + e.Message); }
        }

        /// <summary>
        /// True when a PLAYER is sitting in <paramref name="v"/> - their machine simulates it, so their controls
        /// beat ours and the pull has to be handed to them.
        ///
        /// Deliberately not <c>LandVehicle.IsOccupied</c>, which is a trap: <c>AddNPCOccupant</c> sets that flag too
        /// (LandVehicle.cs:1305), so every police cruiser and every NPC-driven car in the world reads as occupied.
        /// Asking the wrong question there is what made those vehicles ignore the winch completely - the pull was
        /// handed to "the driver", and in single player there is no other machine to hand it to, so nobody applied
        /// it at all. Counting player seats is the question we actually meant: <c>VehicleSeat.Occupant</c> is a
        /// Player and is never set for an NPC.
        /// </summary>
        internal static bool HasPlayerDriver(LandVehicle v)
        {
            try { return v != null && v.CurrentPlayerOccupancy > 0; }
            catch { return false; }
        }

        /// <summary>
        /// True when an NPC is at the wheel. They ride in <c>OccupantNPCs</c>, a separate array from the player
        /// seats, which is why <see cref="HasPlayerDriver"/> cannot see them and why this one exists. An NPC-driven
        /// vehicle stays the host's to simulate and is simply put in neutral.
        /// </summary>
        internal static bool HasNpcDriver(LandVehicle v)
        {
            try
            {
                if (v == null) return false;
                var npcs = v.OccupantNPCs;
                if (npcs == null) return false;
                for (int i = 0; i < npcs.Length; i++)
                {
                    if (npcs[i] != null) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Short description of who or what is driving <paramref name="v"/>, for console output.</summary>
        internal static string DescribeDriver(LandVehicle v)
        {
            try
            {
                if (v == null) return "none";
                if (HasPlayerDriver(v)) return v.LocalPlayerIsDriver ? "you" : "another player";
                if (HasNpcDriver(v))
                {
                    bool driving = false;
                    try { driving = v.Agent != null && v.Agent.AutoDriving; } catch { }

                    string who = "an NPC";
                    try
                    {
                        var npcs = v.OccupantNPCs;
                        for (int i = 0; i < npcs.Length; i++)
                        {
                            if (npcs[i] == null) continue;
                            who = NpcGrip.SafeName(npcs[i]);
                            break;
                        }
                    }
                    catch { }

                    return who + (driving ? " (driving)" : " (idle)");
                }
            }
            catch { }
            return "nobody";
        }
    }
}
