using System;
using System.Collections.Generic;
using Il2CppFishNet;
using Il2CppScheduleOne.NPCs;   // NPC, NPCMovement
using Yoink.Config;

namespace Yoink.Winch
{
    /// <summary>
    /// Takes hooked people off their feet, and keeps them off them until the hook comes out.
    ///
    /// Everything here is the game's own knockdown, not a copy of it. <c>NPCMovement.ActivateRagdoll_Server</c> is
    /// what a car uses when it clips a pedestrian: it cancels a ladder climb, switches the avatar to ragdoll
    /// physics, interrupts whatever walk the NPC was on, disables the NavMeshAgent and its standing capsule, and
    /// applies the impact impulse to the limb nearest the point of contact. Reproducing any of that by hand would
    /// only be a worse version that drifts when the game changes.
    ///
    /// Standing back up is the game's too, and deliberately not ours. <c>NPCMovement.UpdateRagdoll</c> runs on the
    /// server every physics step and does exactly one thing: if the NPC is conscious and its spine has been still
    /// for a second, it deactivates the ragdoll. So a released person gets up by themselves, on the same timing as
    /// every other non-lethal knockdown in the game, and the winch never has to know how that works.
    ///
    /// What this class adds is the "until": while the hook is in, that same rule would stand them up the moment
    /// they stopped sliding, mid-tow, with a rope attached to a limb that has just gone kinematic. <see cref="Tick"/>
    /// holds the timer that rule runs on at zero, so the hook wins for as long as it is attached.
    ///
    /// Held people are tracked by instance id, not by reference: every interop cast hands out a fresh wrapper, so
    /// reference equality would fail to recognise somebody we are already holding.
    /// </summary>
    internal static class NpcGrip
    {
        private static readonly Dictionary<int, NPC> _held = new Dictionary<int, NPC>();

        /// <summary>How many people are being kept down by a hook right now. For readouts.</summary>
        internal static int HeldCount => _held.Count;

        /// <summary>
        /// Knocks <paramref name="npc"/> down and hands back the limb the hook bit into, or false with a
        /// player-readable reason.
        ///
        /// The limb matters: hooking a leg and hooking a shoulder drag a body very differently, and picking the
        /// nearest ragdoll rigidbody to the impact point is what makes the rope look attached to the place you
        /// actually shot. It has to be read AFTER the ragdoll is active, because until then every one of those
        /// rigidbodies is kinematic and its collider is a trigger.
        /// </summary>
        internal static bool TryHook(NPC npc, Vector3 hitPoint, Vector3 impactDir,
                                     out Rigidbody limb, out int limbIndex, out string reason)
        {
            limb = null;
            limbIndex = -1;
            reason = null;

            if (npc == null) { reason = "nobody there"; return false; }

            if (!Preferences.HookPeople)
            {
                reason = "the hook is set not to bite people (Yoink/HookPeople in MelonPreferences.cfg)";
                return false;
            }

            try
            {
                if (npc.IsInVehicle)
                {
                    reason = SafeName(npc) + " is in a vehicle - hook the vehicle instead";
                    return false;
                }
            }
            catch { }

            NPCMovement move = null;
            try { move = npc.Movement; } catch { }
            if (move == null) { reason = SafeName(npc) + " has no movement component"; return false; }

            try
            {
                bool alreadyDown = npc.Avatar != null && npc.Avatar.Ragdolled;
                if (!alreadyDown) Knock(move, hitPoint, impactDir);
            }
            catch (Exception e)
            {
                reason = "could not knock " + SafeName(npc) + " down: " + e.Message;
                return false;
            }

            if (!TryNearestLimb(npc, hitPoint, out limb, out limbIndex))
            {
                reason = SafeName(npc) + " has no ragdoll to hook onto";
                return false;
            }

            Hold(npc);
            return true;
        }

        /// <summary>
        /// Fires the game's knockdown by the route that suits this machine's role.
        ///
        /// On the host (and in single player) that is <c>ActivateRagdoll_Server</c>, which broadcasts to everyone.
        /// A connected client cannot broadcast, and calling the same method there would run the local knockdown and
        /// then log a FishNet warning for the observers RPC it is not allowed to send. So a client knocks down
        /// locally through the RPC's own logic method - no wire traffic, no warning - and the pull intent it sends
        /// to the host makes the host do the authoritative version a moment later.
        /// </summary>
        private static void Knock(NPCMovement move, Vector3 hitPoint, Vector3 impactDir)
        {
            Vector3 dir = impactDir.sqrMagnitude > 1e-6f ? impactDir.normalized : Vector3.forward;
            float force = Preferences.Knockdown;

            bool server = false;
            try { server = InstanceFinder.IsServer; } catch { }

            if (server) move.ActivateRagdoll_Server(hitPoint, dir, force);
            else move.RpcLogic___ActivateRagdoll_2690242654(hitPoint, dir, force);
        }

        /// <summary>
        /// The host's half of a client's hook: knock them down authoritatively and keep them down.
        ///
        /// The client that fired has already put them on the floor on its own screen, because waiting for a round
        /// trip before anything happens is what makes a tool feel broken. This is the version everybody else sees,
        /// and it is idempotent - somebody already lying down is only added to the hold list.
        /// </summary>
        internal static void TryHold(NPC npc, Vector3 hitPoint, Vector3 anchor)
        {
            if (npc == null) return;

            try
            {
                NPCMovement move = npc.Movement;
                if (move == null) return;

                bool down = npc.Avatar != null && npc.Avatar.Ragdolled;
                if (!down) Knock(move, hitPoint, hitPoint - anchor);
            }
            catch (Exception e) { Core.Log.Warning("[Grip] could not knock " + SafeName(npc) + " down: " + e.Message); }

            Hold(npc);
        }

        /// <summary>Remembers that a hook is in this person, so the recovery gate keeps them down.</summary>
        internal static void Hold(NPC npc)
        {
            if (npc == null) return;
            try { _held[npc.GetInstanceID()] = npc; }
            catch { }
        }

        /// <summary>
        /// Lets go. Nothing is undone on purpose: the game stands them up a second after they stop moving, which is
        /// what a person who has just been dragged down the street does anyway.
        /// </summary>
        internal static void Release(NPC npc)
        {
            if (npc == null) return;
            try { _held.Remove(npc.GetInstanceID()); }
            catch { }
        }

        internal static void ReleaseAll() => _held.Clear();

        /// <summary>
        /// Keeps every held person down, by holding the game's stand-up timer at zero. Called once per frame.
        ///
        /// Two earlier attempts at this are worth naming, because both looked reasonable and neither worked.
        ///
        /// Re-ragdolling somebody after they had got up is what made a towed body jump from place to place: standing
        /// up is not just a physics switch, it snaps the NPC root onto the avatar and warps it to the nearest
        /// NavMesh point (NPCMovement.cs:1562-1583), so putting them back down once a second left the body wherever
        /// that warp had landed.
        ///
        /// Patching <c>CanRecoverFromRagdoll</c> - the game's own "not yet" gate, which vanilla uses during a
        /// seizure - looked like the clean answer and had no effect at all. Measured in game with the patch
        /// confirmed applied and the NPC confirmed in the hold list, they still stood up: it is a two-line method
        /// and IL2CPP has inlined it into the caller, so the standalone method Harmony patched is never the one
        /// FixedUpdate runs.
        ///
        /// Writing the field the timer lives in has neither problem. <c>ragdollStaticTime</c> only ever reaches the
        /// one-second threshold by accumulating a physics step at a time, so a write of zero per frame means it can
        /// never get there, and there is no call site to be inlined away.
        /// </summary>
        internal static void Tick()
        {
            if (_held.Count == 0) return;

            List<int> dead = null;
            foreach (KeyValuePair<int, NPC> kv in _held)
            {
                NPCMovement move = null;
                try { move = kv.Value != null && kv.Value.Avatar != null ? kv.Value.Movement : null; }
                catch { }

                if (move == null) { (dead ??= new List<int>()).Add(kv.Key); continue; }

                try { move.ragdollStaticTime = 0f; }
                catch { }
            }

            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++) _held.Remove(dead[i]);
        }

        /// <summary>True while this person is on the floor because of a hook.</summary>
        internal static bool IsDown(NPC npc)
        {
            try { return npc != null && npc.Avatar != null && npc.Avatar.Ragdolled; }
            catch { return false; }
        }

        /// <summary>The ragdoll rigidbody nearest <paramref name="point"/>, with the index that identifies it on the wire.</summary>
        internal static bool TryNearestLimb(NPC npc, Vector3 point, out Rigidbody limb, out int index)
        {
            limb = null;
            index = -1;

            try
            {
                var rbs = npc.Avatar != null ? npc.Avatar.RagdollRBs : null;
                if (rbs == null) return false;

                float best = float.MaxValue;
                for (int i = 0; i < rbs.Length; i++)
                {
                    Rigidbody rb = rbs[i];
                    if (rb == null) continue;

                    float d = Vector3.SqrMagnitude(rb.worldCenterOfMass - point);
                    if (d >= best) continue;

                    best = d;
                    limb = rb;
                    index = i;
                }
            }
            catch { }

            return limb != null;
        }

        /// <summary>
        /// Every rigidbody in this person's ragdoll - the load a winch is actually pulling when the hook is in a
        /// limb. See <c>PullPhysics.ApplyToRagdoll</c> for why the limb on its own is not it.
        /// </summary>
        internal static Il2CppReferenceArray<Rigidbody> PartsOf(NPC npc)
        {
            try { return npc != null && npc.Avatar != null ? npc.Avatar.RagdollRBs : null; }
            catch { return null; }
        }

        /// <summary>The limb an index refers to, for a pull that arrived over the wire.</summary>
        internal static Rigidbody LimbAt(NPC npc, int index)
        {
            try
            {
                var rbs = npc != null && npc.Avatar != null ? npc.Avatar.RagdollRBs : null;
                if (rbs == null || index < 0 || index >= rbs.Length) return null;
                return rbs[index];
            }
            catch { return null; }
        }

        internal static string SafeName(NPC npc)
        {
            try
            {
                string n = npc.FullName;
                return string.IsNullOrEmpty(n) ? npc.gameObject.name : n;
            }
            catch { return "someone"; }
        }
    }
}
