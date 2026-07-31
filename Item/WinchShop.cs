using System;
using System.Reflection;
using Il2CppScheduleOne.ItemFramework;   // ItemDefinition, StorableItemDefinition

namespace Yoink.Item
{
    /// <summary>
    /// Puts the winch on a shop shelf.
    ///
    /// Two things make this harder than it should be. S1API carries two parallel wrapper hierarchies for the same
    /// item, and <c>ShopIntegration.AddItemToShop</c> casts to the older one while <c>ItemManager.GetDefinition</c>
    /// hands out the newer - so the normal path refuses every mod item with "is not storable". BreedToSeed already
    /// solved that (Catalog/ShopCompat.cs) by building the legacy wrapper through reflection; the same trick is
    /// repeated here rather than depending on that mod.
    ///
    /// And shops are scene objects, so the listing has to be retried until they exist.
    /// </summary>
    internal static class WinchShop
    {
        private static bool _listed;

        internal static void ResetSession() => _listed = false;

        /// <summary>Idempotent; call from the Main scene until it reports true.</summary>
        internal static bool EnsureListed()
        {
            if (_listed) return true;
            if (!WinchItem.Available) return false;

            try
            {
                ItemDefinition native = Il2CppScheduleOne.Registry.GetItem(WinchItem.Id);
                if (native == null) return false;

                S1API.Items.ItemDefinition wrapped = WrapForShops(native);
                if (wrapped == null) return false;

                float price = Config.Preferences.ShopPrice;
                int shops = 0;

                // By name first: the hardware store is where a winch belongs, and naming it beats guessing which
                // vanilla item shares its shelves.
                shops += AddToNamed(wrapped, price, "Hardware Store", "Hardware", "hardware");

                // Otherwise fall in next to a tool that is already sold somewhere.
                if (shops == 0) shops = AddNextTo(wrapped, new[] { "trashgrabber", "trimmers", "wateringcan", "trashbag" }, price);

                if (shops == 0)
                {
                    LogShopNamesOnce();
                    return false;
                }

                _listed = true;
                Core.Log.Msg("[Shop] winch listed in " + shops + " shop(s) at $" + price.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + ".");
                return true;
            }
            catch (Exception e)
            {
                Core.Log.Warning("[Shop] listing failed: " + e.Message);
                _listed = true;   // do not retry forever on a broken build
                return false;
            }
        }

        private static int AddToNamed(S1API.Items.ItemDefinition item, float price, params string[] names)
        {
            int taken = 0;
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    var shop = S1API.Shops.ShopManager.GetShopByName(names[i]);
                    if (shop == null) continue;
                    if (shop.AddItem(item, price)) taken++;
                    break;
                }
                catch { }
            }
            return taken;
        }

        private static int AddNextTo(S1API.Items.ItemDefinition item, string[] anchors, float price)
        {
            for (int i = 0; i < anchors.Length; i++)
            {
                try
                {
                    var shops = S1API.Shops.ShopManager.FindShopsByItem(anchors[i]);
                    if (shops == null) continue;

                    int taken = 0;
                    foreach (var shop in shops)
                        if (shop != null && shop.AddItem(item, price)) taken++;

                    if (taken > 0) return taken;
                }
                catch { }
            }
            return 0;
        }

        /// <summary>
        /// Builds the legacy S1API wrapper that the shop code will actually accept, falling back to the ordinary
        /// lookup - which is the right answer again the moment S1API unifies its two hierarchies.
        /// </summary>
        private static S1API.Items.ItemDefinition WrapForShops(ItemDefinition native)
        {
            try
            {
                if (native.TryCast<StorableItemDefinition>() != null)
                {
                    Assembly s1api = typeof(S1API.Items.ItemDefinition).Assembly;
                    Type wrapper = s1api.GetType("S1API.Items.StorableItemDefinition", false);
                    if (wrapper != null)
                    {
                        ConstructorInfo ctor = wrapper.GetConstructor(
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                            null, new[] { typeof(StorableItemDefinition) }, null);
                        if (ctor != null)
                        {
                            var built = ctor.Invoke(new object[] { native.TryCast<StorableItemDefinition>() }) as S1API.Items.ItemDefinition;
                            if (built != null) return built;
                        }
                    }
                }
            }
            catch (Exception e) { Core.Log.Warning("[Shop] legacy wrapper failed: " + e.Message); }

            return S1API.Items.ItemManager.GetDefinition(WinchItem.Id);
        }

        private static bool _namesLogged;

        /// <summary>If neither route worked, say which shops actually exist instead of failing quietly.</summary>
        private static void LogShopNamesOnce()
        {
            if (_namesLogged) return;
            _namesLogged = true;
            try
            {
                var all = S1API.Shops.ShopManager.GetAllShops();
                if (all == null || all.Length == 0) { Core.Log.Msg("[Shop] no shops in the scene yet - will retry."); return; }

                string names = string.Empty;
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null) continue;
                    names += (names.Length > 0 ? ", " : string.Empty) + all[i].Name;
                }
                Core.Log.Warning("[Shop] could not place the winch. Shops present: " + names);
            }
            catch (Exception e) { Core.Log.Warning("[Shop] could not list shops: " + e.Message); }
        }
    }
}
