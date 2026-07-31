using System;
using System.Collections.Generic;
using Il2CppScheduleOne.Vehicles;   // LandVehicle

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
        }

        private static readonly Dictionary<int, Saved> _held = new Dictionary<int, Saved>();

        /// <summary>Releases the handbrake, remembering what to restore. Idempotent per vehicle.</summary>
        internal static void TakeShared(LandVehicle v)
        {
            if (v == null) return;

            try
            {
                int id = v.GetInstanceID();
                if (_held.ContainsKey(id)) return;

                _held[id] = new Saved
                {
                    Vehicle = v,
                    OverrideControls = v.overrideControls,
                    ThrottleOverride = v.throttleOverride,
                    SteerOverride = v.steerOverride,
                };

                v.throttleOverride = 0f;
                v.steerOverride = 0f;
                v.handbrakeOverride = false;
                v.overrideControls = true;
            }
            catch (Exception e) { Core.Log.Warning("[Grip] could not release the handbrake: " + e.Message); }
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
                LandVehicle v = s.Vehicle;
                if (v == null) return;
                v.throttleOverride = s.ThrottleOverride;
                v.steerOverride = s.SteerOverride;
                v.handbrakeOverride = false;
                v.overrideControls = s.OverrideControls;
            }
            catch (Exception e) { Core.Log.Warning("[Grip] restore failed: " + e.Message); }
        }

        /// <summary>True when somebody is sitting in <paramref name="v"/> - their controls beat ours.</summary>
        internal static bool IsOccupied(LandVehicle v)
        {
            try { return v != null && v.IsOccupied; }
            catch { return false; }
        }
    }
}
