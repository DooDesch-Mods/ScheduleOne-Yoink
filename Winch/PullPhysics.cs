using System;
using Yoink.Config;

namespace Yoink.Winch
{
    /// <summary>
    /// The force model in one place, because three different callers have to produce exactly the same pull:
    /// the local player winching in single-player, the host applying a remote client's intent, and a driver's
    /// client applying a pull on the vehicle they are sitting in. Any drift between those would show up as a
    /// load that behaves differently depending on who is looking at it.
    ///
    /// What it models: a cable under tension, applying a flat force at the point it is hooked to, with ONE ceiling -
    /// the closing speed of that point may never exceed the cap.
    ///
    /// Both halves matter and neither is decoration. The force is what hauls a 1200 kg car out of a ditch, and it is
    /// unchanged from the version that did that well. The ceiling is what stops the same 12 kN turning a half-kilo
    /// drinks can into a projectile: one physics step of it is about 480 m/s of delta-v on that can, and that single
    /// step was the whole of the "light litter flies through the air" bug.
    ///
    /// Replacing the force with an acceleration model was tried and measured, and it is why this comment exists: it
    /// fixed the can and made the car limp, because an acceleration bound low enough to be safe cannot beat ground
    /// friction. So does a separate spin limit - at any value tight enough to do anything it clamped every step of a
    /// 35 kg barrel. Bounding the outcome instead of the input costs the heavy case nothing.
    /// </summary>
    internal static class PullPhysics
    {
#if DEBUG
        /// <summary>
        /// The last step's impulse and every limiter that could have cut it, for the console trace.
        ///
        /// A pull that does nothing and a pull that is being clamped to nothing look identical from the outside -
        /// the load simply sits there. These are what tell them apart without another guess-and-rebuild cycle.
        /// </summary>
        internal static float LastWanted, LastSpeedLimit, LastImpulse, LastInvEffMass;

        internal static string DescribeLastStep()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return "imp=" + LastImpulse.ToString("F3", inv)
                 + " wanted=" + LastWanted.ToString("F3", inv)
                 + " speedCap=" + LastSpeedLimit.ToString("F1", inv)
                 + " 1/mEff=" + LastInvEffMass.ToString("F4", inv);
        }
#endif

        /// <summary>
        /// One FixedUpdate of winch pull on <paramref name="rb"/>, hauling <paramref name="pivotWorld"/> toward
        /// <paramref name="anchor"/>. <paramref name="anchorVelocity"/> is how fast the anchor itself is moving, so
        /// closing speed is measured in the anchor's frame - the anchor rides on the player's head, and treating a
        /// walking player as a stationary post makes the winch pump energy into the load every time you move.
        ///
        /// Returns false when nothing was applied (kinematic body, already reeling at the cap, unusable inertia),
        /// so callers can report a pull that is doing nothing instead of failing silently.
        /// </summary>
        internal static bool Apply(Rigidbody rb, Vector3 pivotWorld, Vector3 anchor, Vector3 anchorVelocity,
                                   out float distance, out float alongRope)
        {
            distance = 0f;
            alongRope = 0f;
            if (rb == null) return false;

            try
            {
                if (rb.isKinematic) return false;

                float dt = Time.fixedDeltaTime;
                if (dt <= 0f) return false;

                Vector3 toAnchor = anchor - pivotWorld;
                distance = toAnchor.magnitude;
                if (distance <= Preferences.StopDistance) return false;

                Vector3 dir = toAnchor / distance;

                // The velocity OF THE HOOK POINT, not of the centre of mass. They are the same thing only for a body
                // that is not rotating: GetPointVelocity adds the angular term, and a spinning can can have a hook
                // point racing towards the anchor while its centre still reads as slow. Gating on the centre let the
                // winch keep pulling a target that was already moving faster than the cap could ever allow.
                Vector3 hookVelocity = rb.GetPointVelocity(pivotWorld);
                alongRope = Vector3.Dot(hookVelocity - anchorVelocity, dir);

                // Tension only. The cable never pushes and never writes velocity - if the load is already closing
                // faster than the cap, a real cable simply goes slack, and so does this one. The measured overshoot
                // that motivated this (a load travelling AWAY at a steady 1.50 m/s and jumping to 6.69 the moment
                // the pull stopped) came from rescaling the velocity vector, which hid Unity's depenetration impulse
                // inside a number that looked capped.
                float headroom = Preferences.MaxSpeed - alongRope;
                if (headroom <= 0f) return false;

                float mass = rb.mass;
                if (mass <= 1e-6f || float.IsNaN(mass) || float.IsInfinity(mass)) return false;

                // How much of an impulse becomes translation and how much becomes spin - the mass the cable actually
                // feels at the hook. Needed because the ceiling below is expressed in speed, and turning a speed into
                // an impulse with the plain mass would over-deliver on any off-centre hook, and every hook is
                // off-centre.
                float inverseEffectiveMass;
                if (!TryHookResponse(rb, pivotWorld, dir, mass, out inverseEffectiveMass)) return false;

                // THE PULL ITSELF IS UNCHANGED: a flat force, the same one that hauls a car out of a ditch. What was
                // wrong was never the force, it was that nothing bounded the RESULT on a light body. So the force is
                // applied as before and only the outcome is capped.
                float impulse = Preferences.PullNewtons * dt;

                // The ceiling: never add more closing speed in one step than the headroom to the cap. This is the
                // whole light-item fix. The speed gate above is checked BEFORE the force, so on its own it only ever
                // guaranteed that a pull does not START above the cap - one step of 12 kN on a half-kilo can is a
                // delta-v of about 480 m/s, and that single step was the entire explosion. Converted through the
                // effective mass it costs a heavy target nothing: a 1200 kg car's cap works out around 900 Ns
                // against the 240 Ns the force actually delivers, so the car never notices this line exists.
                float maxImpulseFromSpeed = headroom / inverseEffectiveMass;
                if (impulse > maxImpulseFromSpeed) impulse = maxImpulseFromSpeed;

                // There is deliberately NO separate spin limit. One was tried and measured: at any value low enough to
                // matter it was the binding limiter on EVERY step of a 35 kg barrel, which is what made the pull go
                // limp - "could just about tip it over" instead of hauling it. It is also unnecessary, because the
                // ceiling above already bounds rotation: it is measured at the HOOK POINT, whose velocity includes the
                // angular term, so a body that starts spinning immediately shows up as closing speed and gets cut off.
                // A cable that rotates what it is hooked to is correct behaviour and is what swings a car's nose round.
                if (impulse <= 0f) return false;

#if DEBUG
                LastWanted = Preferences.PullNewtons * dt;
                LastSpeedLimit = maxImpulseFromSpeed;
                LastImpulse = impulse;
                LastInvEffMass = inverseEffectiveMass;
#endif

                rb.AddForceAtPosition(dir * impulse, pivotWorld, ForceMode.Impulse);
                return true;
            }
            catch (Exception e)
            {
                Core.Log.Warning("[Winch] pull failed: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// How the body responds, at the hook point and along the rope, to one newton-second of impulse.
        ///
        /// <paramref name="inverseEffectiveMass"/> is 1/m plus the rotational term - the reciprocal of the mass the
        /// cable actually feels at that point. <paramref name="angularPerImpulse"/> is the angular velocity the same
        /// impulse would add, which the spin guard needs. Returns false on an inertia tensor that cannot be inverted
        /// meaningfully, which is safer than dividing by it.
        /// </summary>
        private static bool TryHookResponse(Rigidbody rb, Vector3 pivotWorld, Vector3 dir, float mass,
                                            out float inverseEffectiveMass)
        {
            inverseEffectiveMass = 1f / mass;

            try
            {
                Vector3 inertia = rb.inertiaTensor;
                if (inertia.x <= 1e-8f || inertia.y <= 1e-8f || inertia.z <= 1e-8f) return true;   // treat as pure mass

                Vector3 r = pivotWorld - rb.worldCenterOfMass;
                Vector3 angularPerImpulse = InverseInertiaTimes(rb, Vector3.Cross(r, dir), inertia);

                float rotational = Vector3.Dot(dir, Vector3.Cross(angularPerImpulse, r));
                if (float.IsNaN(rotational) || float.IsInfinity(rotational) || rotational < 0f) rotational = 0f;

                inverseEffectiveMass = 1f / mass + rotational;
                return !float.IsNaN(inverseEffectiveMass) && !float.IsInfinity(inverseEffectiveMass)
                       && inverseEffectiveMass > 0f;
            }
            catch
            {
                inverseEffectiveMass = 1f / mass;
                return true;
            }
        }

        /// <summary>
        /// World-space inverse inertia applied to a vector. Rigidbody.inertiaTensor is diagonal in its OWN frame, so
        /// the vector has to be rotated into that frame, divided, and rotated back - dividing in world space would be
        /// wrong for any body whose principal axes are not aligned with the world, which is most of them.
        /// </summary>
        private static Vector3 InverseInertiaTimes(Rigidbody rb, Vector3 worldVector, Vector3 inertia)
        {
            Quaternion frame = rb.rotation * rb.inertiaTensorRotation;
            Vector3 local = Quaternion.Inverse(frame) * worldVector;
            local.x /= inertia.x;
            local.y /= inertia.y;
            local.z /= inertia.z;
            return frame * local;
        }
    }
}
