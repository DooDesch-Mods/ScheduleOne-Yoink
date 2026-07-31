using System;
using Il2CppScheduleOne.Vehicles;   // LandVehicle
using Il2CppScheduleOne.Dragging;   // Draggable

namespace Yoink.Winch
{
    /// <summary>
    /// One hooked object: the rigidbody the force goes into, plus the exact point the hook bit into it, kept in
    /// the object's LOCAL space. Local space is the whole point - the pivot has to travel with the object as it
    /// rotates, otherwise the rope would visibly slide across the bodywork while the car turns.
    /// </summary>
    internal sealed class WinchTarget
    {
        internal Rigidbody Rb;
        internal Transform Root;
        internal Vector3 PivotLocal;

        /// <summary>Set when the hooked object is a vehicle - it needs unparking before physics will touch it.</summary>
        internal LandVehicle Vehicle;

        /// <summary>Set when the hooked object is one of the game's draggables (bodies, bags, containers).</summary>
        internal Draggable Draggable;

        /// <summary>Human-readable name for console output.</summary>
        internal string Label = "?";

        /// <summary>
        /// Wire id for co-op, or null when this object only exists on this machine. Only GUID-registered things
        /// travel - vehicles and the game's draggables; scenery physics is per-machine anyway and is pulled
        /// locally without ever being sent.
        /// </summary>
        internal string NetId;

        /// <summary>True when the hooked object is something every machine in the session can name.</summary>
        internal bool IsNetworked => !string.IsNullOrEmpty(NetId);

        internal Vector3 PivotWorld => Root != null ? Root.TransformPoint(PivotLocal) : Vector3.zero;

        internal bool Alive
        {
            get
            {
                try { return Rb != null && Root != null; }
                catch { return false; }
            }
        }

        internal float Mass
        {
            get
            {
                try { return Rb != null ? Rb.mass : 0f; }
                catch { return 0f; }
            }
        }

        /// <summary>
        /// Fires the hook along <paramref name="ray"/> and resolves whatever it hits into a target.
        /// Returns false with a player-readable <paramref name="reason"/> when there is nothing to hook.
        /// </summary>
        internal static bool TryAcquire(Ray ray, float maxRange, out WinchTarget target, out string reason)
        {
            target = null;
            reason = null;

            RaycastHit hit;
            if (!Physics.Raycast(ray, out hit, maxRange, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                reason = "nothing within " + Mathf.RoundToInt(maxRange) + "m";
                return false;
            }

            Collider col = hit.collider;
            if (col == null) { reason = "no collider on the hit"; return false; }

            // Never hook yourself - the player's own colliders are in the way of most useful shots.
            try { if (col.GetComponentInParent<Player>() != null) { reason = "that is you"; return false; } }
            catch { }

            Rigidbody rb = hit.rigidbody;
            if (rb == null)
            {
                try { rb = col.GetComponentInParent<Rigidbody>(); } catch { }
            }
            if (rb == null)
            {
                reason = "'" + SafeName(col.transform) + "' has no rigidbody - the hook needs something that can move";
                return false;
            }

            LandVehicle hitVehicle = null;
            try { hitVehicle = col.GetComponentInParent<LandVehicle>(); } catch { }

            // A pinned fixture would take the hook and then refuse to budge, which reads as a broken winch. Say so
            // instead. Vehicles are exempt: theirs is a temporary state the game flips back on when we unpark them
            // or simply stand close enough.
            bool pinned = false;
            try { pinned = hitVehicle == null && rb.isKinematic; } catch { }
            if (pinned)
            {
                reason = "'" + SafeName(rb.transform) + "' is fixed in place - no winch is going to move that";
                return false;
            }

            WinchTarget t = new WinchTarget();
            t.Rb = rb;
            t.Root = rb.transform;
            t.PivotLocal = t.Root.InverseTransformPoint(hit.point);

            t.Vehicle = hitVehicle;
            try { t.Draggable = col.GetComponentInParent<Draggable>(); } catch { }

            t.Label = t.Vehicle != null ? SafeVehicleName(t.Vehicle) : SafeName(t.Root);
            t.NetId = MakeNetId(t);

            target = t;
            return true;
        }

        /// <summary>
        /// Resolves the nearest hookable rigidbody around <paramref name="origin"/>, no aiming involved.
        /// The pivot is the point on that body closest to the origin - the spot a thrown hook would realistically
        /// bite. Used by the console test path (see WinchSession.HookNearest).
        /// </summary>
        internal static bool TryAcquireNearest(Vector3 origin, float radius, out WinchTarget target, out string reason)
        {
            target = null;
            reason = null;

            Collider[] hits;
            try { hits = Physics.OverlapSphere(origin, radius, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore); }
            catch (Exception e) { reason = "overlap query failed: " + e.Message; return false; }
            if (hits == null || hits.Length == 0) { reason = "nothing within " + Mathf.RoundToInt(radius) + "m"; return false; }

            Rigidbody best = null;
            Collider bestCol = null;
            float bestDist = float.MaxValue;
            bool bestIsVehicle = false;

            for (int i = 0; i < hits.Length; i++)
            {
                Collider col = hits[i];
                if (col == null) continue;

                try { if (col.GetComponentInParent<Player>() != null) continue; }
                catch { }

                Rigidbody rb = col.attachedRigidbody;
                if (rb == null) continue;

                bool isVehicle = false;
                try { isVehicle = col.GetComponentInParent<LandVehicle>() != null; } catch { }

                // Fixtures that are pinned in place (kinematic, and not a vehicle that just has not woken up yet)
                // would take a hook and then refuse to move, which reads as a broken winch.
                if (!isVehicle)
                {
                    try { if (rb.isKinematic) continue; } catch { }
                }

                float d;
                try { d = Vector3.Distance(origin, col.ClosestPoint(origin)); }
                catch { continue; }

                // A vehicle always beats a bin, even a closer one - this is the winch's whole reason to exist,
                // and on the test path it is the difference between measuring a car and measuring a stepladder.
                if (bestIsVehicle && !isVehicle) continue;
                if (!(isVehicle && !bestIsVehicle) && d >= bestDist) continue;

                bestDist = d;
                best = rb;
                bestCol = col;
                bestIsVehicle = isVehicle;
            }

            if (best == null) { reason = "nothing within " + Mathf.RoundToInt(radius) + "m that can move"; return false; }

            WinchTarget t = new WinchTarget();
            t.Rb = best;
            t.Root = best.transform;

            Vector3 bite;
            try { bite = bestCol.ClosestPoint(origin); }
            catch { bite = best.worldCenterOfMass; }
            t.PivotLocal = t.Root.InverseTransformPoint(bite);

            try { t.Vehicle = bestCol.GetComponentInParent<LandVehicle>(); } catch { }
            try { t.Draggable = bestCol.GetComponentInParent<Draggable>(); } catch { }
            t.Label = t.Vehicle != null ? SafeVehicleName(t.Vehicle) : SafeName(t.Root);
            t.NetId = MakeNetId(t);

            target = t;
            return true;
        }

        /// <summary>Wire id, or null for something only this machine knows about.</summary>
        private static string MakeNetId(WinchTarget t)
        {
            try
            {
                if (t.Vehicle != null) return "V:" + t.Vehicle.GUID.ToString();
                if (t.Draggable != null) return "D:" + t.Draggable.GUID.ToString();
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Makes the target physically reachable. A parked vehicle NEVER simulates - LandVehicle's
        /// ShouldBePhysicallySimulated returns false outright while CurrentParkingLot is set - so no amount of
        /// force would do anything until it leaves the lot. ExitPark(false) drops the parking state without the
        /// teleport-to-exit-point that ExitPark() normally does.
        /// </summary>
        internal void PrepareForPull()
        {
            try
            {
                if (Vehicle != null && Vehicle.isParked)
                {
                    Vehicle.ExitPark(false);
                    Core.Log.Msg("[Winch] " + Label + " was parked - left the lot so physics can act on it.");
                }
            }
            catch (Exception e) { Core.Log.Warning("[Winch] unpark failed: " + e.Message); }

            try
            {
                if (Rb != null && !Rb.isKinematic) Rb.WakeUp();
            }
            catch { }
        }

        internal string Describe()
        {
            string kind = Vehicle != null ? "vehicle" : (Draggable != null ? "draggable" : "rigidbody");
            string kin = "?";
            try { kin = Rb.isKinematic ? "kinematic" : "dynamic"; } catch { }
            return Label + " [" + kind + ", " + Mass.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "kg, " + kin + "]";
        }

        private static string SafeName(Transform t)
        {
            try { return t != null ? t.gameObject.name : "?"; }
            catch { return "?"; }
        }

        private static string SafeVehicleName(LandVehicle v)
        {
            try { return string.IsNullOrEmpty(v.VehicleName) ? v.gameObject.name : v.VehicleName; }
            catch { return "vehicle"; }
        }
    }
}
