using System.Globalization;
using MelonLoader;

namespace Yoink.Config
{
    /// <summary>
    /// MelonPreferences for the winch. Everything here is a tuning knob that live testing exists to find real values
    /// for - the defaults are starting points, not answers, and the Snitch panel can move them at runtime without a
    /// rebuild.
    /// </summary>
    internal static class Preferences
    {
        private static MelonPreferences_Category _cat;

        private static MelonPreferences_Entry<float> _pullNewtons;
        private static MelonPreferences_Entry<float> _maxSpeed;
        private static MelonPreferences_Entry<float> _hookRange;
        private static MelonPreferences_Entry<float> _breakDistance;
        private static MelonPreferences_Entry<float> _stopDistance;
        private static MelonPreferences_Entry<int> _ropeSegments;
        private static MelonPreferences_Entry<float> _shopPrice;
        private static MelonPreferences_Entry<bool> _ropeCollision;

        /// <summary>Nominal pull of the winch in newtons - the force that hauls a car out of a ditch.</summary>
        internal static float PullNewtons
        {
            get => _pullNewtons?.Value ?? 12000f;
            set { if (_pullNewtons != null) _pullNewtons.Value = value; }
        }

        /// <summary>
        /// Reel-in rate cap along the rope, in m/s. A winch reels, it does not launch.
        ///
        /// Enforced by pulling less, never by overwriting the rigidbody's velocity - rescaling the whole velocity
        /// vector looked equivalent and was not: it kept the direction of Unity's depenetration impulse while hiding
        /// its size, and a load measured travelling AWAY at a steady 1.50 m/s jumped to 6.69 the moment pulling stopped.
        /// </summary>
        internal static float MaxSpeed
        {
            get => _maxSpeed?.Value ?? 1.5f;
            set { if (_maxSpeed != null) _maxSpeed.Value = value; }
        }

        /// <summary>Maximum line-of-sight distance at which the hook can be fired, in metres.</summary>
        internal static float HookRange
        {
            get => _hookRange?.Value ?? 15f;
            set { if (_hookRange != null) _hookRange.Value = value; }
        }

        /// <summary>Distance at which the rope snaps, in metres.</summary>
        internal static float BreakDistance
        {
            get => _breakDistance?.Value ?? 25f;
            set { if (_breakDistance != null) _breakDistance.Value = value; }
        }

        /// <summary>Pulling stops once the pivot is this close to the anchor - so the load stops short of you.</summary>
        internal static float StopDistance
        {
            get => _stopDistance?.Value ?? 2.5f;
            set { if (_stopDistance != null) _stopDistance.Value = value; }
        }

        /// <summary>Sets a tuning value by name. Returns false for an unknown key. Used by the console.</summary>
        internal static bool TrySet(string key, float value)
        {
            switch (key)
            {
                case "pull": PullNewtons = value; return true;
                case "vmax": MaxSpeed = value; return true;
                case "range": HookRange = value; return true;
                case "break": BreakDistance = value; return true;
                case "stop": StopDistance = value; return true;
                default: return false;
            }
        }

        /// <summary>Number of rope points in the verlet simulation.</summary>
        internal static int RopeSegments => _ropeSegments?.Value ?? 20;

        /// <summary>
        /// What the winch costs on the shelf. Settable at runtime so a price can be tried out without a rebuild,
        /// but the shop listing reads it once when the item is registered - changing it mid-session moves the
        /// preference, not the label on the shelf, and takes effect on the next load.
        /// </summary>
        internal static float ShopPrice
        {
            get => _shopPrice?.Value ?? 80f;
            set { if (_shopPrice != null) _shopPrice.Value = value < 0f ? 0f : value; }
        }

        /// <summary>Whether the rope collides with the world instead of hanging through it.</summary>
        internal static bool RopeCollision => _ropeCollision?.Value ?? true;

        internal static void Initialize()
        {
            _cat = MelonPreferences.CreateCategory("Yoink", "Yoink");

            _pullNewtons = _cat.CreateEntry("PullNewtons", 12000f, "Pull force (N)",
                "Nominal winch pull. Real mass applies, so a heavy vehicle moves slower than a bin.");
            _maxSpeed = _cat.CreateEntry("MaxSpeed", 1.5f, "Maximum winch speed (m/s)",
                "How fast the winch reels the load in along the rope. Other motion (falling, being pushed out of a wall) is not capped.");
            _hookRange = _cat.CreateEntry("HookRange", 15f, "Hook range (m)",
                "How far the hook can be fired in line of sight.");
            _breakDistance = _cat.CreateEntry("BreakDistance", 25f, "Rope break distance (m)",
                "The rope snaps beyond this distance between hook and winch.");
            _stopDistance = _cat.CreateEntry("StopDistance", 2.5f, "Stop distance (m)",
                "Reeling stops once the hooked point gets this close to the anchor.");
            _ropeSegments = _cat.CreateEntry("RopeSegments", 20, "Rope segments",
                "Points in the rope simulation. More is smoother and slightly more expensive.");
            _shopPrice = _cat.CreateEntry("ShopPrice", 80f, "Shop price ($)",
                "What the winch costs in the shop. Read once when the item is registered, so a change applies on the next load.");
            _ropeCollision = _cat.CreateEntry("RopeCollision", true, "Rope collides with the world",
                "Rope slack rests on the ground instead of hanging through it. Costs a couple of short raycasts per frame while a hook is attached.");
        }

        /// <summary>One-line dump of the live tuning values, for <c>yoinkinfo</c>.</summary>
        internal static string Describe()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            return "pull=" + PullNewtons.ToString("F0", inv) + "N"
                 + " vMax=" + MaxSpeed.ToString("F2", inv)
                 + " range=" + HookRange.ToString("F1", inv)
                 + " break=" + BreakDistance.ToString("F1", inv)
                 + " stop=" + StopDistance.ToString("F1", inv);
        }
    }
}
