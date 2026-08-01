#if DEBUG
using System;
using System.Globalization;
using Il2CppScheduleOne.Vehicles;   // VehicleManager

namespace Yoink.Debugging
{
    /// <summary>
    /// DEBUG-only test scaffolding. The vanilla <c>spawnvehicle</c> command always drops a car 4 m ahead, which
    /// is inside the winch's stop distance - so measuring a pull with it is impossible. This puts a test vehicle
    /// at a distance we choose, which is what makes a run repeatable.
    /// </summary>
    internal static class TestRig
    {
        /// <summary>
        /// Equips the winch from the hotbar. Exists because the harness can submit console commands but cannot
        /// press a number key, and an item whose controls can only be reached by hand is an item whose controls
        /// never get verified.
        /// </summary>
        internal static void EquipWinch()
        {
            // Deliberately not equipped here. A console command runs with the console OPEN, and the console is a UI
            // element: PlayerInventory.SetEquippingEnabled(false) has already blanked EquippedSlotIndex, and when the
            // console closes it re-equips PriorEquippedSlotIndex - a value captured BEFORE this command ran. Anything
            // written now is silently thrown away a moment later, which is exactly what it looked like: the log said
            // "equipped" and nothing was ever in hand. So the request is parked and applied once the game will keep it.
            _equipPending = Time.unscaledTime + 3f;
        }

        private static float _equipPending;
        private static float _camDumpPending;

        /// <summary>
        /// Asks for the camera dump once the console is out of the way.
        ///
        /// Same reason the equip is parked: while the console is open the game has unequipped the item, so anything
        /// that inspects the held model reports "nothing in hand" - which is a fact about the console, not about the
        /// winch, and reads like a bug that is not there.
        /// </summary>
        internal static void DumpCamerasSoon()
        {
            Yoink.Item.WinchItem.ArmDumps();
            _camDumpPending = Time.unscaledTime + 3f;
        }

        /// <summary>Applies parked console requests once the console has closed and the game is live again.</summary>
        internal static void Tick()
        {
            TickCameraDump();
            if (_equipPending <= 0f) return;

            if (Time.unscaledTime > _equipPending)
            {
                _equipPending = 0f;
                Core.Log.Warning("[Test] gave up equipping the winch - equipping never came back.");
                return;
            }

            try
            {
                var inv = PlayerSingleton<PlayerInventory>.Instance;
                if (inv == null || !inv.EquippingEnabled) return;

                var cam = PlayerSingleton<PlayerCamera>.Instance;
                if (cam != null && cam.activeUIElements.Count > 0) return;   // console still up

                for (int i = 0; i < inv.hotbarSlots.Count; i++)
                {
                    var slot = inv.hotbarSlots[i];
                    var item = slot != null ? slot.ItemInstance : null;
                    if (item == null || item.Definition == null) continue;
                    if (item.Definition.ID != Yoink.Item.WinchItem.Id) continue;

                    // The same two steps the game's own hotkey handler does. Setting the index alone only tells the
                    // network which slot is live; Select() is what actually puts the thing in your hands.
                    inv.EquippedSlotIndex = i;
                    inv.Equip(slot);

                    _equipPending = 0f;
                    Core.Log.Msg("[Test] equipped the winch from hotbar slot " + (i + 1) + ".");
                    return;
                }

                _equipPending = 0f;
                Core.Log.Warning("[Test] no winch in the hotbar - run 'yoink give' first.");
            }
            catch (Exception e)
            {
                _equipPending = 0f;
                Core.Log.Warning("[Test] equip failed: " + e.Message);
            }
        }

        private static void TickCameraDump()
        {
            if (_camDumpPending <= 0f) return;

            bool expired = Time.unscaledTime > _camDumpPending;
            if (!expired && !Yoink.Item.WinchItem.IsEquipped()) return;

            _camDumpPending = 0f;
            foreach (string line in Yoink.Item.WinchItem.DescribeCameras()) Core.Log.Msg("[Cam] " + line);
        }

        /// <summary>Equips an arbitrary hotbar slot, so a vanilla weapon can be put in hand for comparison.</summary>
        internal static void EquipSlot(int index)
        {
            try
            {
                var inv = PlayerSingleton<PlayerInventory>.Instance;
                if (inv == null) { Core.Log.Warning("[Test] no inventory."); return; }
                if (index < 0 || index >= inv.hotbarSlots.Count) { Core.Log.Warning("[Test] slot out of range."); return; }

                inv.EquippedSlotIndex = index;
                Core.Log.Msg("[Test] equipped hotbar slot " + (index + 1) + ".");
            }
            catch (Exception e) { Core.Log.Warning("[Test] equip slot failed: " + e.Message); }
        }

        /// <summary>
        /// Dumps what is currently in the equip container and where. This is how the winch model gets placed by
        /// measurement rather than by eye: hold a vanilla weapon, read its numbers, match them.
        /// </summary>
        internal static void DumpEquipContainer()
        {
            try
            {
                var inv = PlayerSingleton<PlayerInventory>.Instance;
                Transform parent = inv != null ? inv.EquipContainer : null;
                if (parent == null) { Core.Log.Warning("[Test] no equip container."); return; }

                Core.Log.Msg("[Test] equip container '" + parent.name + "' has " + parent.childCount + " child(ren):");
                for (int i = 0; i < parent.childCount; i++)
                {
                    Transform c = parent.GetChild(i);
                    Core.Log.Msg("  [" + i + "] " + c.name
                        + " pos=" + c.localPosition.ToString("F3")
                        + " rot=" + c.localEulerAngles.ToString("F1")
                        + " scale=" + c.localScale.ToString("F3")
                        + " active=" + c.gameObject.activeSelf);

                    for (int j = 0; j < c.childCount && j < 6; j++)
                    {
                        Transform g = c.GetChild(j);
                        Core.Log.Msg("      - " + g.name
                            + " pos=" + g.localPosition.ToString("F3")
                            + " rot=" + g.localEulerAngles.ToString("F1")
                            + " scale=" + g.localScale.ToString("F3"));
                    }
                }
            }
            catch (Exception e) { Core.Log.Warning("[Test] dump failed: " + e.Message); }
        }

        /// <summary>
        /// Searches the equip container's hierarchy for transforms whose name matches <paramref name="needle"/>
        /// and prints their full paths. Finding the hand bone by name beats guessing at an offset: a model parented
        /// to the hand inherits the avatar's animation for free, which is the difference between a tool that is
        /// held and one that floats beside the arms.
        /// </summary>
        internal static void FindInViewmodel(string needle)
        {
            try
            {
                var inv = PlayerSingleton<PlayerInventory>.Instance;
                Transform root = inv != null ? inv.EquipContainer : null;
                if (root == null) { Core.Log.Warning("[Test] no equip container."); return; }

                int found = 0;
                Walk(root, string.Empty, needle.ToLower(), ref found, 0);
                Core.Log.Msg("[Test] " + found + " match(es) for '" + needle + "'.");
            }
            catch (Exception e) { Core.Log.Warning("[Test] search failed: " + e.Message); }
        }

        private static void Walk(Transform t, string path, string needle, ref int found, int depth)
        {
            if (t == null || depth > 12) return;

            string here = path.Length == 0 ? t.name : path + "/" + t.name;
            if (t.name.ToLower().Contains(needle))
            {
                found++;
                Core.Log.Msg("  " + here + "  pos=" + t.localPosition.ToString("F3") + " rot=" + t.localEulerAngles.ToString("F0"));
            }

            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), here, needle, ref found, depth + 1);
        }

        internal static void SpawnVehicleAhead(float distance, string code = "shitbox")
        {
            try
            {
                Player local = Player.Local;
                if (local == null) { Core.Log.Warning("[Test] no local player."); return; }

                Transform t = local.transform;
                Vector3 pos = t.position + t.forward * distance + Vector3.up * 0.6f;
                Quaternion rot = Quaternion.LookRotation(t.right, Vector3.up);   // broadside, so the pull can rotate it

                VehicleManager vm = NetworkSingleton<VehicleManager>.Instance;
                if (vm == null) { Core.Log.Warning("[Test] no VehicleManager."); return; }

                vm.SpawnAndReturnVehicle(code, pos, rot, true);
                Core.Log.Msg("[Test] spawned '" + code + "' " + distance.ToString("F1", CultureInfo.InvariantCulture) + "m ahead.");
            }
            catch (Exception e) { Core.Log.Warning("[Test] spawn failed: " + e.Message); }
        }
    }
}
#endif
