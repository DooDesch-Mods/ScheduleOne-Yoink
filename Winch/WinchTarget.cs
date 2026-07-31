using System;
using System.Globalization;
using Il2CppScheduleOne.Vehicles;   // LandVehicle
using Il2CppScheduleOne.Dragging;   // Draggable
using Il2CppScheduleOne.NPCs;       // NPC

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

        /// <summary>Set when the hook bit into a person - they stay down for as long as this target lives.</summary>
        internal NPC Npc;

        /// <summary>Which of the NPC's ragdoll rigidbodies the hook is in. Travels on the wire with the id.</summary>
        internal int LimbIndex = -1;

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

            // People come first, and before the rigidbody lookup on purpose. A standing NPC has no body to pull:
            // its ragdoll rigidbodies are kinematic and its ragdoll colliders are triggers, so the only thing the
            // ray can hit is the standing capsule and the only rigidbody in the hierarchy is one that refuses to
            // move. Knocking them down is what CREATES something to hook, which is why it happens here rather than
            // as a special case further down.
            NPC npc = null;
            try { npc = col.GetComponentInParent<NPC>(); } catch { }
            if (npc != null) return TryAcquireNpc(npc, hit.point, ray.direction, out target, out reason);

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
        /// Turns a person into a target: knock them down, then hook the limb the shot landed on.
        /// </summary>
        private static bool TryAcquireNpc(NPC npc, Vector3 hitPoint, Vector3 shotDir,
                                          out WinchTarget target, out string reason)
        {
            target = null;

            Rigidbody limb;
            int limbIndex;
            if (!NpcGrip.TryHook(npc, hitPoint, shotDir, out limb, out limbIndex, out reason)) return false;

            WinchTarget t = new WinchTarget();
            t.Rb = limb;
            t.Root = limb.transform;
            t.PivotLocal = t.Root.InverseTransformPoint(hitPoint);
            t.Npc = npc;
            t.LimbIndex = limbIndex;
            t.Label = NpcGrip.SafeName(npc);
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

                // People are deliberately out of this path. It is the one the console and the automated runs use,
                // and a test that knocks over whoever happens to be walking past is not a repeatable test. Hooking
                // a person without aiming has its own command.
                try { if (col.GetComponentInParent<NPC>() != null) continue; }
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

        /// <summary>
        /// Hooks the nearest person within <paramref name="radius"/>, ignoring where the camera points.
        ///
        /// The counterpart to <see cref="TryAcquireNearest"/> for the one target type that one refuses to touch, and
        /// it exists for the same reason: the console can be scripted and a mouse cannot, so without it the whole
        /// knock-down-and-drag path could only ever be checked by hand.
        ///
        /// Triggers are included in the search because a standing NPC's ragdoll colliders ARE triggers until the
        /// moment they go down - excluding them would find nothing but the standing capsule and would miss anybody
        /// already lying on the floor.
        /// </summary>
        internal static bool TryAcquireNearestPerson(Vector3 origin, float radius, out WinchTarget target, out string reason)
        {
            target = null;
            reason = null;

            Collider[] hits;
            try { hits = Physics.OverlapSphere(origin, radius, Physics.AllLayers, QueryTriggerInteraction.Collide); }
            catch (Exception e) { reason = "overlap query failed: " + e.Message; return false; }
            if (hits == null || hits.Length == 0) { reason = "nobody within " + Mathf.RoundToInt(radius) + "m"; return false; }

            NPC best = null;
            float bestDist = float.MaxValue;

            for (int i = 0; i < hits.Length; i++)
            {
                Collider col = hits[i];
                if (col == null) continue;

                NPC npc = null;
                try { npc = col.GetComponentInParent<NPC>(); } catch { }
                if (npc == null) continue;

                float d;
                try { d = Vector3.Distance(origin, npc.transform.position); }
                catch { continue; }
                if (d >= bestDist) continue;

                bestDist = d;
                best = npc;
            }

            if (best == null) { reason = "nobody within " + Mathf.RoundToInt(radius) + "m"; return false; }

            Vector3 bite;
            try { bite = best.transform.position + Vector3.up * 1.0f; }
            catch { bite = origin; }

            Vector3 dir;
            try { dir = (bite - origin).normalized; }
            catch { dir = Vector3.forward; }

            return TryAcquireNpc(best, bite, dir, out target, out reason);
        }

        /// <summary>
        /// Wire id, or null for something only this machine knows about.
        ///
        /// A person carries one extra field the others do not need: which ragdoll rigidbody the hook is in. Without
        /// it the receiving machine would know who was hooked but not where, and would drag them by whichever limb
        /// happened to come first. It rides on the end of the id rather than as a new message field, so the wire
        /// format is unchanged and an older build simply fails to resolve the id instead of misreading it.
        /// </summary>
        private static string MakeNetId(WinchTarget t)
        {
            try
            {
                if (t.Vehicle != null) return "V:" + t.Vehicle.GUID.ToString();
                if (t.Draggable != null) return "D:" + t.Draggable.GUID.ToString();
                if (t.Npc != null) return "N:" + t.Npc.GUID.ToString() + ":" + t.LimbIndex.ToString(CultureInfo.InvariantCulture);
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

        /// <summary>
        /// Hands the target back. Only people need it: a vehicle's neutral is undone by the session, and a hooked
        /// person has to be taken off the keep-them-down list or they would never be allowed to stand up again.
        /// </summary>
        internal void Release()
        {
            if (Npc != null) NpcGrip.Release(Npc);
        }

        internal string Describe()
        {
            string kind = Npc != null ? "person"
                        : Vehicle != null ? "vehicle"
                        : Draggable != null ? "draggable" : "rigidbody";
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
