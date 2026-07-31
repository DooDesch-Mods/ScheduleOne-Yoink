using System;
using System.Collections.Generic;
using Il2CppScheduleOne.Vehicles;   // LandVehicle
using Il2CppScheduleOne.Dragging;   // Draggable
using Yoink.Winch;

namespace Yoink.Net
{
    /// <summary>
    /// Pulls this machine has to apply on someone else's behalf.
    ///
    /// On the host that is every connected client's winch, which is where the design puts authority: the host
    /// already simulates every unoccupied vehicle, so letting it own the force means no transform fight, no
    /// ownership dance, and several players pulling the same wreck simply add up.
    ///
    /// The one exception is a vehicle with somebody in it. That one is simulated on the DRIVER's machine
    /// (LandVehicle.ShouldBePhysicallySimulated returns true for LocalPlayerIsDriver), so host-side force would
    /// only produce rubber-banding. For those the host hands the pull to whoever is driving, and their client
    /// applies it here.
    /// </summary>
    internal static class RemotePulls
    {
        private sealed class Entry
        {
            internal string Id;
            internal Vector3 PivotLocal;
            internal Vector3 Anchor;
            internal Rigidbody Rb;
            internal Transform Root;
            internal LandVehicle Vehicle;
        }

        private static readonly Dictionary<string, Entry> _active = new Dictionary<string, Entry>(StringComparer.Ordinal);

        internal static int Count => _active.Count;

        internal static void Clear() => _active.Clear();

        /// <summary>Wires the bus to this registry. Safe to call once at startup.</summary>
        internal static void Install()
        {
            YoinkNet.OnServerIntent = OnServerIntent;
            YoinkNet.OnClientState = OnClientState;
        }

        /// <summary>Host: a client wants to reel something in.</summary>
        private static void OnServerIntent(YoinkMsg m)
        {
            if (m == null) return;

            switch (m.Op)
            {
                case YoinkOp.PullStart:
                {
                    Entry e = Resolve(m.TargetId);
                    if (e == null) { Core.Log.Warning("[Net] host cannot resolve pull target '" + m.TargetId + "'."); return; }

                    e.PivotLocal = m.PivotLocal;
                    e.Anchor = m.Anchor;

                    // Occupied vehicle: the driver's machine owns its physics, so hand the pull over instead of
                    // applying force here where it would be overwritten every frame.
                    bool occupied = false;
                    try { occupied = e.Vehicle != null && e.Vehicle.IsOccupied; } catch { }
                    if (occupied)
                    {
                        _active.Remove(m.TargetId);
                        YoinkMsg fwd = new YoinkMsg { Op = YoinkOp.DriverPull, TargetId = m.TargetId, PivotLocal = m.PivotLocal, Anchor = m.Anchor };
                        YoinkNet.BroadcastToAll(fwd);
                        ApplyDriverPullLocally(fwd);   // the host may be the driver
                        Core.Log.Msg("[Net] '" + m.TargetId + "' is occupied - pull handed to the driver's client.");
                        return;
                    }

                    if (e.Vehicle != null) VehicleGrip.TakeShared(e.Vehicle);
                    _active[m.TargetId] = e;
                    break;
                }

                case YoinkOp.PullStop:
                {
                    if (_active.TryGetValue(m.TargetId, out Entry e) && e.Vehicle != null) VehicleGrip.ReleaseShared(e.Vehicle);
                    _active.Remove(m.TargetId);
                    YoinkNet.BroadcastToAll(new YoinkMsg { Op = YoinkOp.DriverStop, TargetId = m.TargetId });
                    StopDriverPullLocally(m.TargetId);
                    break;
                }
            }
        }

        /// <summary>Client: the host is telling us to pull the vehicle we are driving (or to stop).</summary>
        private static void OnClientState(YoinkMsg m)
        {
            if (m == null) return;
            if (m.Op == YoinkOp.DriverPull) ApplyDriverPullLocally(m);
            else if (m.Op == YoinkOp.DriverStop) StopDriverPullLocally(m.TargetId);
        }

        private static void ApplyDriverPullLocally(YoinkMsg m)
        {
            Entry e = Resolve(m.TargetId);
            if (e == null || e.Vehicle == null) return;

            bool weDrive = false;
            try { weDrive = e.Vehicle.LocalPlayerIsDriver; } catch { }
            if (!weDrive) return;

            e.PivotLocal = m.PivotLocal;
            e.Anchor = m.Anchor;
            _active[m.TargetId] = e;
        }

        private static void StopDriverPullLocally(string id)
        {
            _active.Remove(id);
        }

        /// <summary>Applies every pull this machine owns. Called once per FixedUpdate.</summary>
        internal static void FixedTick()
        {
            if (_active.Count == 0) return;

            List<string> dead = null;
            foreach (KeyValuePair<string, Entry> kv in _active)
            {
                Entry e = kv.Value;
                bool alive;
                try { alive = e.Rb != null && e.Root != null; } catch { alive = false; }
                if (!alive)
                {
                    (dead ??= new List<string>()).Add(kv.Key);
                    continue;
                }

                float dist, along;
                PullPhysics.Apply(e.Rb, e.Root.TransformPoint(e.PivotLocal), e.Anchor, out dist, out along);
            }

            if (dead != null)
                for (int i = 0; i < dead.Count; i++) _active.Remove(dead[i]);
        }

        /// <summary>
        /// Turns a wire id back into something with a rigidbody. Only GUID-registered objects travel: vehicles
        /// and the game's draggables. Everything else is scenery physics that exists per-machine anyway, which is
        /// why the design pulls those locally and never sends them.
        /// </summary>
        private static Entry Resolve(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length < 3) return null;

            try
            {
                string guid = id.Substring(2);
                if (id[0] == 'V')
                {
                    LandVehicle v = Il2Cpp.GUIDManager.GetObject<LandVehicle>(new Il2CppSystem.Guid(guid));
                    if (v == null || v.Rb == null) return null;
                    return new Entry { Id = id, Rb = v.Rb, Root = v.transform, Vehicle = v };
                }
                if (id[0] == 'D')
                {
                    Draggable d = Il2Cpp.GUIDManager.GetObject<Draggable>(new Il2CppSystem.Guid(guid));
                    if (d == null || d.Rigidbody == null) return null;
                    return new Entry { Id = id, Rb = d.Rigidbody, Root = d.transform };
                }
            }
            catch (Exception e) { Core.Log.Warning("[Net] resolving '" + id + "' failed: " + e.Message); }

            return null;
        }
    }
}
