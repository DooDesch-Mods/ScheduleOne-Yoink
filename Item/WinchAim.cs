using System;
using Il2CppScheduleOne.PlayerScripts;   // ViewmodelAvatar

namespace Yoink.Item
{
    /// <summary>
    /// Raises the winch into the aiming pose while a hook is attached.
    ///
    /// The game already has exactly this: aiming a firearm is a float, not a state swap - the ranged weapon feeds
    /// <c>Animator.SetFloat("Aim", Aim)</c> with a value it smooth-damps between 0 and 1
    /// (Equippable_RangedWeapon.cs:308, :213). Writing the same parameter gives the winch the same raised
    /// two-handed pose the pistol gets, blended by the same animator, with no animation work of our own.
    ///
    /// Hooked means raised: the hook being out is precisely the moment the tool is doing something and the moment
    /// the player wants to see where the rope goes.
    /// </summary>
    internal static class WinchAim
    {
        private const float BlendTime = 0.18f;   // matches the weapon's AimDuration / 2

        private static float _aim;
        private static float _velocity;

        internal static void Reset()
        {
            _aim = 0f;
            _velocity = 0f;
        }

        /// <summary>Drives the pose. Call every frame while the winch is held; pass false once it is not.</summary>
        internal static void Tick(bool held, bool hooked)
        {
            try
            {
                float target = held && hooked ? 1f : 0f;

                // Nothing to do once it has settled at rest - and importantly, do not keep writing the parameter
                // when the winch is put away, or the next item to be held inherits a raised pose from us.
                if (!held && _aim <= 0.001f) return;

                _aim = Mathf.SmoothDamp(_aim, target, ref _velocity, BlendTime);
                if (_aim < 0.001f) _aim = 0f;

                var avatar = Singleton<ViewmodelAvatar>.Instance;
                if (avatar == null || avatar.Animator == null) return;

                avatar.Animator.SetFloat("Aim", _aim);
            }
            catch { }
        }
    }
}
