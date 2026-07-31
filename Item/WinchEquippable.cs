using System;
using Il2CppScheduleOne.Equipping;      // Equippable_AvatarViewmodel
using Il2CppScheduleOne.ItemFramework;  // ItemDefinition, StorableItemDefinition
using Il2CppScheduleOne.PlayerScripts;  // ViewmodelAvatar

namespace Yoink.Item
{
    /// <summary>
    /// Builds the winch's held viewmodel the way the game itself does it, and hands it to the game rather than
    /// animating anything ourselves.
    ///
    /// The route: a template GameObject carrying a real <c>Equippable_AvatarViewmodel</c>, its fields copied from a
    /// vanilla weapon, our model parented underneath, and the registered item definition pointed at it with
    /// <c>EquipMode = Legacy</c>. From there <c>HotbarSlot.Equip</c> clones the template and calls Equip on the
    /// clone, and the vanilla code parents it into <c>ViewmodelAvatar.RightHandContainer</c>, sets the animator
    /// controller, raises the arms and applies the viewmodel transform - the whole first-person pose, for free.
    /// This is what MoreGuns gets by shipping a configured prefab in an AssetBundle; we build the same thing at
    /// runtime because a GLB parsed in-process cannot be serialized into a prefab ahead of time.
    ///
    /// Three details are the difference between this working and silently doing nothing:
    ///
    /// * The template must be ACTIVE under an INACTIVE parent. S1API's own EquippableBuilder.Build() deactivates
    ///   the object it returns, and HotbarSlot never reactivates the clone - which is why the S1API equippable
    ///   produced no geometry at all. Parking an active object under an inactive holder keeps it out of the scene
    ///   while leaving activeSelf true, so the clone comes out active.
    /// * The inherited viewmodel transform fields (localPosition/localEulerAngles/localScale on
    ///   Equippable_Viewmodel) must be set, because Equip() writes them onto the transform AFTER parenting.
    /// * EquipTrigger must never be null: PlayEquipAnimation compares it against string.Empty, so null reaches
    ///   Animator.SetTrigger(null).
    ///
    /// AvatarEquippable is deliberately left null. It is the third-person representation, and copying the donor's
    /// would show other players an M1911 instead of a winch.
    /// </summary>
    internal static class WinchEquippable
    {
        private static GameObject _bank;        // inactive holder, keeps the template out of the scene
        private static GameObject _template;    // active child - what HotbarSlot clones
        private static bool _failed;

        internal static bool Ready => _template != null;

        internal static void ResetSession()
        {
            try { if (_bank != null) UnityEngine.Object.Destroy(_bank); } catch { }
            _bank = null;
            _template = null;
            _failed = false;
        }

        /// <summary>
        /// Builds the template and points the registered definition at it. Safe to call repeatedly; does nothing
        /// once it has succeeded or once it has failed for a reason that will not change this session.
        /// </summary>
        internal static void EnsureInstalled(GameObject model)
        {
            if (_failed || _template != null || model == null) return;

            try
            {
                var avatar = Singleton<ViewmodelAvatar>.Instance;
                if (avatar == null || avatar.Animator == null || avatar.RightHandContainer == null) return;   // not up yet

                Equippable_AvatarViewmodel donor = FindDonor();
                if (donor == null) { _failed = true; return; }

                _bank = new GameObject("YoinkEquippableTemplates");
                _bank.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_bank);

                _template = new GameObject("YoinkWinchEquippable");
                _template.transform.SetParent(_bank.transform, false);
                _template.SetActive(true);

                Equippable_AvatarViewmodel eq = _template.AddComponent<Equippable_AvatarViewmodel>();
                eq.AnimatorController = donor.AnimatorController;
                eq.ViewmodelAvatarOffset = donor.ViewmodelAvatarOffset;
                eq.ViewmodelAvatarRotationOffset = donor.ViewmodelAvatarRotationOffset;
                eq.EquipTrigger = donor.EquipTrigger ?? string.Empty;
                eq.EquipTime = donor.EquipTime;
                eq.localPosition = donor.localPosition;
                eq.localEulerAngles = donor.localEulerAngles;
                eq.localScale = donor.localScale;
                eq.AvatarEquippable = null;
                eq.CanInteractWhenEquipped = true;
                eq.CanPickUpWhenEquipped = true;

                GameObject visual = UnityEngine.Object.Instantiate(model, _template.transform);
                visual.name = "YoinkWinchModel";
                visual.transform.localPosition = WinchItem.HeldPosition;
                visual.transform.localRotation = Quaternion.Euler(WinchItem.HeldRotation);
                visual.transform.localScale = Vector3.one * WinchItem.HeldScale;
                visual.SetActive(true);

                if (!PointDefinitionAtTemplate(eq))
                {
                    _failed = true;
                    return;
                }

                Core.Log.Msg("[Item] winch equippable installed - the game now holds it like a weapon.");
            }
            catch (Exception e)
            {
                _failed = true;
                Core.Log.Warning("[Item] could not install the winch equippable, falling back to the loose model: " + e.Message);
            }
        }

        /// <summary>
        /// Writes our template into the registered native definition. S1API cannot do this for us: its
        /// WithEquippable takes an S1API wrapper whose native constructor is internal, and it never touches
        /// EquipMode at all.
        /// </summary>
        private static bool PointDefinitionAtTemplate(Equippable_AvatarViewmodel eq)
        {
            try
            {
                var native = Il2CppScheduleOne.Registry.GetItem(WinchItem.Id);
                if (native == null) { Core.Log.Warning("[Item] the winch is not in the registry yet."); return false; }

                var storable = native.TryCast<StorableItemDefinition>();
                if (storable == null) { Core.Log.Warning("[Item] the winch definition is not storable."); return false; }

                storable.Equippable = eq;
                storable.EquipMode = ItemDefinition.EEquipMode.Legacy;
                return true;
            }
            catch (Exception e)
            {
                Core.Log.Warning("[Item] could not point the definition at the equippable: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// A vanilla held item to copy the first-person setup from. Deliberately not Equippable_RangedWeapon
        /// itself - that subclass casts the item to IntegerItemInstance and installs firearm state - only the
        /// Equippable_AvatarViewmodel fields it inherits.
        /// </summary>
        private static Equippable_AvatarViewmodel FindDonor()
        {
            string[] donors = { "m1911", "revolver", "baton", "pumpshotgun" };
            for (int i = 0; i < donors.Length; i++)
            {
                try
                {
                    var def = Il2CppScheduleOne.Registry.GetItem(donors[i]);
                    if (def == null || def.Equippable == null) continue;

                    var view = def.Equippable.GetComponent<Equippable_AvatarViewmodel>();
                    if (view == null || view.AnimatorController == null) continue;

                    Core.Log.Msg("[Item] copying the held-item setup from '" + donors[i] + "'.");
                    return view;
                }
                catch { }
            }

            Core.Log.Warning("[Item] no vanilla held item to copy from - the winch stays a loose model in front of the camera.");
            return null;
        }
    }
}
