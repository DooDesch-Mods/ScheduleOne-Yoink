using System;
using Yoink.Config;

namespace Yoink.Winch
{
    /// <summary>
    /// The force model in one place, because three different callers have to produce exactly the same pull:
    /// the local player winching in single-player, the host applying a remote client's intent, and a driver's
    /// client applying a pull on the vehicle they are sitting in. Any drift between those would show up as a
    /// load that behaves differently depending on who is looking at it.
    /// </summary>
    internal static class PullPhysics
    {
        /// <summary>
        /// One FixedUpdate of winch pull on <paramref name="rb"/>, hauling <paramref name="pivotWorld"/> toward
        /// <paramref name="anchor"/>. Returns false when nothing was applied (kinematic body, or already reeling
        /// at the cap), so callers can report a pull that is doing nothing instead of failing silently.
        /// </summary>
        internal static bool Apply(Rigidbody rb, Vector3 pivotWorld, Vector3 anchor, out float distance, out float alongRope)
        {
            distance = 0f;
            alongRope = 0f;
            if (rb == null) return false;

            try
            {
                if (rb.isKinematic) return false;

                Vector3 toAnchor = anchor - pivotWorld;
                distance = toAnchor.magnitude;
                if (distance <= Preferences.StopDistance) return false;

                Vector3 dir = toAnchor / distance;

                // The cap applies to the reel-in rate ALONG the rope, and it is enforced by not pulling rather
                // than by writing to the velocity. Rescaling the whole velocity vector looked equivalent and was
                // not: a car half inside a wall gets a violent depenetration impulse from Unity, and normalising
                // that to the cap kept its direction while hiding its size - measured in game, the load travelled
                // AWAY from the winch at a steady 1.50 m/s and jumped to 6.69 m/s the moment the pull stopped.
                alongRope = Vector3.Dot(rb.velocity, dir);
                if (alongRope >= Preferences.MaxSpeed) return false;

                float force = Preferences.PullNewtons;
                float mass = Mathf.Max(rb.mass, 0.01f);
                float minForce = Preferences.MinAccel * mass;   // floor: nothing may be too heavy to move at all
                if (force < minForce) force = minForce;

                rb.AddForceAtPosition(dir * force, pivotWorld, ForceMode.Force);
                return true;
            }
            catch (Exception e)
            {
                Core.Log.Warning("[Winch] pull failed: " + e.Message);
                return false;
            }
        }
    }
}
