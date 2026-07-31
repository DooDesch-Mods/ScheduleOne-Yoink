using System;
using Il2CppScheduleOne;                        // GameInput
using Il2CppScheduleOne.ItemFramework;          // ItemInstance
using Yoink.Winch;

namespace Yoink.Item
{
    /// <summary>
    /// The winch as a real inventory item: buyable, holdable, and the thing the hook actually comes out of.
    ///
    /// Registration happens once per session after the Main scene loads - the game's Registry is cleared on scene
    /// transitions, so anything registered earlier is gone. If registration fails the mod stays usable through
    /// the console commands rather than dying, which is what a POC-turned-mod should do.
    ///
    /// Controls while it is equipped: primary click fires or releases the hook, secondary click held reels
    /// (<see cref="WinchSession"/> owns that half). Polling for the click rather than using S1API's use-callback
    /// is deliberate - the injected EquippableUseCallback does not survive being cloned by HotbarSlot.Equip, the
    /// same reason Backrooms polls for its drink.
    /// </summary>
    internal static class WinchItem
    {
        internal const string Id = "yoink_winch";

        private static bool _registered;
        private static float _nextClickAt;
        private static bool _wasEquipped;

        internal static bool Available => _registered;

        /// <summary>Scene left Main: the game's Registry is cleared, so our registration is stale.</summary>
        internal static void ResetSession()
        {
            _registered = false;
            WinchEquippable.ResetSession();
        }

        /// <summary>Idempotent per session; call from the Main scene. Never throws.</summary>
        internal static void EnsureRegistered()
        {
            if (_registered) return;

            try
            {
                try
                {
                    if (S1API.Items.ItemManager.GetDefinition(Id) != null) { _registered = true; return; }
                }
                catch { /* not found is the normal case */ }

                var equippable = S1API.Items.Storable.ItemCreator.CreateEquippableBuilder()
                    .CreateViewmodelEquippable("YoinkWinchEquippable")
                    .WithViewmodelTransform(
                        position: new Vector3(0.18f, -0.16f, 0.34f),
                        rotation: new Vector3(0f, 200f, 0f),
                        scale: Vector3.one)
                    .WithInteraction(canInteract: true, canPickup: true)
                    .WithAvatarEquippable(
                        assetPath: S1API.Items.AvatarEquippablePaths.M1911,   // closest base-game "held, pointed" pose
                        hand: S1API.Items.AvatarHand.Right,
                        animationTrigger: "RightArm_Hold_ClosedHand")
                    .Build();

                S1API.Items.Storable.ItemCreator.CreateBuilder()
                    .WithBasicInfo(Id, "Winch",
                        "Hooks whatever you aim at and drags it out. Right click and hold to reel.",
                        S1API.Items.ItemCategory.Tools)
                    .WithStackLimit(1)
                    .WithPricing(Config.Preferences.ShopPrice, 0.4f)
                    .WithLegalStatus(S1API.Items.LegalStatus.Legal)
                    .WithIcon(BorrowedIcon())
                    .WithEquippable(equippable)
                    .Build();

                _registered = true;
                Core.Log.Msg("[Item] winch registered ('" + Id + "').");
            }
            catch (Exception e)
            {
                Core.Log.Warning("[Item] winch registration failed - console commands still work: " + e.Message);
            }
        }

        // Where the model sits in the equip container. The scale is not a guess: holding a vanilla pistol and
        // dumping the container shows the game renders its first-person avatar at 0.25 scale close to the camera
        // (ViewmodelAvatar, scale 0.250), which is why a correctly sized 35 cm model looked enormous at 1.0. The
        // position was then set by eye against that reference and can be re-tuned live with 'yoink vm'.
        // Z is forward. 0.26 held the winch a full arm's length out in front of the hands, so the model floated with
        // nothing carrying it. 0.15 brings it back into the grip; fine-tune live with 'yoink vm pos 0.10 -0.17 0.15'
        // rather than by rebuilding.
        internal static Vector3 ViewmodelPosition = new Vector3(0.10f, -0.17f, 0.15f);
        internal static Vector3 ViewmodelRotation = new Vector3(0f, 180f, 0f);
        internal static float ViewmodelScale = 0.25f;

        /// <summary>
        /// Whether to hang the model on the avatar's right hand bone instead of the equip container.
        ///
        /// Off by default, and deliberately so - this is a half-finished path kept switchable with 'yoink vm hand on'.
        ///
        /// What works: borrowing a vanilla weapon's RuntimeAnimatorController and offsets does bring the arms into
        /// view (without a controller the avatar stays in its idle pose at the player's sides, which is why the
        /// first attempt made the model vanish), and the model parents onto ViewmodelAvatar.RightHandContainer.
        /// What does not: the avatar ends up in the wrong POSE, rendering right at the camera instead of holding
        /// the tool out front. The missing piece is the animator STATE a vanilla weapon drives through its own
        /// Equippable_AvatarViewmodel, which our item never gets because it runs on the new equipping framework
        /// (Player.Local.Equip -> EquippedItemHandler), not the legacy path that would instantiate ours.
        /// </summary>
        internal static bool PreferHand;

        /// <summary>Placement relative to the right hand bone, used only when <see cref="PreferHand"/> is on.</summary>
        internal static Vector3 HandPosition = new Vector3(0f, 0f, 0f);
        internal static Vector3 HandRotation = new Vector3(0f, 0f, 0f);
        internal static float HandScale = 1f;

        private static bool _onHand;

        /// <summary>
        /// Raises the first-person arms and returns the hand container to hang the model in, or null if that is
        /// not possible.
        ///
        /// This is what a vanilla weapon does, done by hand. <c>Equippable_AvatarViewmodel.Equip</c> parents itself
        /// to <c>ViewmodelAvatar.Instance.RightHandContainer</c>, assigns a RuntimeAnimatorController, and calls
        /// SetVisibility(true) - and it is the ANIMATOR CONTROLLER that brings the arms up. Without one the avatar
        /// exists but stays in its idle pose at the player's sides, which is exactly what a hand-parented model
        /// looked like: gone. The controller is borrowed from a vanilla weapon rather than authored, because a
        /// hand holding a pistol is the pose a winch wants anyway.
        /// </summary>
        private static Transform PrepareHands()
        {
            try
            {
                var avatar = Singleton<ViewmodelAvatar>.Instance;
                if (avatar == null || avatar.RightHandContainer == null) return null;

                RuntimeAnimatorController controller = BorrowedAnimator();
                if (controller == null) return null;   // no controller means no raised arms, so no point

                avatar.SetAnimatorController(controller);
                avatar.SetVisibility(true);
                // The offsets come from the same donor as the animator: a weapon's ViewmodelAvatarOffset is what
                // pushes the avatar down and away from the camera, and passing zero instead parks the arms in the
                // player's face.
                avatar.SetOffset(_borrowedOffset);
                avatar.SetRotationOffset(_borrowedRotationOffset);

                try { if (avatar.Animator != null) avatar.Animator.SetTrigger("Equip"); } catch { }

                return avatar.RightHandContainer;
            }
            catch (Exception e)
            {
                Core.Log.Warning("[Item] could not raise the first-person arms: " + e.Message);
                return null;
            }
        }

        private static RuntimeAnimatorController _borrowedAnimator;
        private static Vector3 _borrowedOffset;
        private static Vector3 _borrowedRotationOffset;

        /// <summary>The animator controller off a vanilla held weapon, which is what poses the arms.</summary>
        private static RuntimeAnimatorController BorrowedAnimator()
        {
            if (_borrowedAnimator != null) return _borrowedAnimator;

            string[] donors = { "m1911", "revolver", "pumpshotgun", "baton" };
            for (int i = 0; i < donors.Length; i++)
            {
                try
                {
                    var def = Il2CppScheduleOne.Registry.GetItem(donors[i]);
                    if (def == null || def.Equippable == null) continue;

                    var avatarViewmodel = def.Equippable.GetComponent<Il2CppScheduleOne.Equipping.Equippable_AvatarViewmodel>();
                    if (avatarViewmodel == null || avatarViewmodel.AnimatorController == null) continue;

                    _borrowedAnimator = avatarViewmodel.AnimatorController;
                    _borrowedOffset = avatarViewmodel.ViewmodelAvatarOffset;
                    _borrowedRotationOffset = avatarViewmodel.ViewmodelAvatarRotationOffset;
                    Core.Log.Msg("[Item] borrowed the first-person arm pose from '" + donors[i]
                        + "' (offset " + _borrowedOffset.ToString("F3") + ", rot " + _borrowedRotationOffset.ToString("F0") + ").");
                    return _borrowedAnimator;
                }
                catch { }
            }

            Core.Log.Warning("[Item] found no vanilla animator controller to borrow - the winch will be held without arms.");
            return null;
        }

        /// <summary>
        /// Re-asserts that the arms are visible, every frame the winch is held.
        ///
        /// Setting it once is not enough: the equipping framework hides the avatar again as part of its own
        /// unequip/equip bookkeeping, and the arms silently disappeared a frame after we raised them. Re-asserting
        /// is a property write on an already-correct value in the common case, so it costs nothing.
        /// </summary>
        /// <summary>Whether the first-person arms are shown while the winch is held on the container path.</summary>
        internal static bool ShowArms = true;

        private static bool _armsRaised;

        /// <summary>
        /// Brings the arms up without hanging anything on them.
        ///
        /// Same mechanism the hand path uses - a borrowed animator controller is what raises the avatar at all - but
        /// the model stays in the equip container. That separates the two things the hand path had welded together:
        /// showing arms, and parenting the tool to a bone whose pose is wrong for it.
        /// </summary>
        private static void RaiseArms()
        {
            try
            {
                var avatar = Singleton<ViewmodelAvatar>.Instance;
                if (avatar == null) return;

                RuntimeAnimatorController controller = BorrowedAnimator();
                if (controller == null) return;

                avatar.SetAnimatorController(controller);
                avatar.SetOffset(_borrowedOffset);
                avatar.SetRotationOffset(_borrowedRotationOffset);
                avatar.SetVisibility(true);
                _armsRaised = true;
            }
            catch { }
        }

        private static void KeepHandsUp()
        {
            if (!_onHand && !_armsRaised) return;
            try
            {
                var avatar = Singleton<ViewmodelAvatar>.Instance;
                if (avatar != null && !avatar.IsVisible) avatar.SetVisibility(true);
            }
            catch { }
        }

        /// <summary>Puts the arms away again.</summary>
        private static void LowerHands()
        {
            try
            {
                var avatar = Singleton<ViewmodelAvatar>.Instance;
                if (avatar != null) avatar.SetVisibility(false);
            }
            catch { }
        }

        private static Transform FindByName(Transform t, string name, int depth)
        {
            if (t == null || depth > 12) return null;
            if (t.name == name) return t;

            for (int i = 0; i < t.childCount; i++)
            {
                Transform hit = FindByName(t.GetChild(i), name, depth + 1);
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>
        /// Where the rope leaves the model, in the model's own space. Tuned live with 'yoink vm muzzle'; the
        /// default is the front of the barrel (the reader mirrors glTF's Z, so the model faces -Z).
        /// </summary>
        internal static Vector3 MuzzleLocal = new Vector3(0f, 0.09f, -0.30f);

        /// <summary>
        /// The world point the rope should start at, or false when no model is in hand. Callers fall back to a
        /// camera-relative point so the console path still draws a rope with nothing equipped.
        /// </summary>
        /// <summary>
        /// Overrides the measured muzzle when set from the console. Zero means "work it out from the model".
        /// </summary>
        internal static Vector3 MuzzleOverride = Vector3.zero;

        internal static bool TryGetMuzzle(out Vector3 world)
        {
            world = Vector3.zero;
            try
            {
                Transform model = HeldModel();
                if (model == null) return false;

                world = MuzzleOverride != Vector3.zero
                    ? model.TransformPoint(MuzzleOverride)
                    : MeasuredMuzzle(model);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Where the rope leaves the winch, measured off the model that is actually being rendered.
        ///
        /// This used to be a hand-tuned constant in model space, and a constant is only correct for the geometry it
        /// was tuned against - re-export the model a little longer or with a different origin and the rope starts
        /// leaving from thin air, which is exactly what happened. Reading the renderer's own bounds means the rope
        /// keeps coming off the front of the drum whatever the model becomes.
        ///
        /// The front face of the bounds, not its centre: a rope that starts inside the drum is hidden by it.
        /// </summary>
        private static Vector3 MeasuredMuzzle(Transform model)
        {
            Bounds bounds = default;
            bool any = false;

            foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }

            // No renderer at all is the one case where guessing is worse than the old constant, so it falls back.
            if (!any) return model.TransformPoint(MuzzleLocal);

            // Forward for the held model, not for the camera: the model carries its own 180-degree turn.
            Vector3 forward = -model.forward;
            Vector3 extent = bounds.extents;
            float reach = Mathf.Abs(forward.x) * extent.x + Mathf.Abs(forward.y) * extent.y + Mathf.Abs(forward.z) * extent.z;

            return bounds.center + forward * reach;
        }

        private static Transform _heldModel;

        /// <summary>
        /// The model actually being rendered in hand. On the equippable path that is a child of the clone the game
        /// instantiated, not anything we hold a reference to, so it is looked up through PlayerInventory - which
        /// finally reports an equippable now that the definition uses the legacy equip mode.
        /// </summary>
        private static Transform HeldModel()
        {
            if (_modelInstance != null) return _modelInstance.transform;   // fallback path

            try
            {
                if (_heldModel != null) return _heldModel;

                var inv = PlayerSingleton<PlayerInventory>.Instance;
                var equippable = inv != null ? inv.equippable : null;
                if (equippable == null) return null;

                _heldModel = equippable.transform.Find("YoinkWinchModel");
                return _heldModel;
            }
            catch { return null; }
        }

        /// <summary>Drops the cached lookup when the held item changes.</summary>
        internal static void ForgetHeldModel() => _heldModel = null;

        private static GameObject _modelSource;      // parsed once, kept inactive as the thing we copy from
        private static GameObject _modelInstance;    // the copy currently hanging under the live viewmodel
        private static int _viewmodelId;             // instance id of the equippable the copy belongs to
        private static bool _modelFailed;

        /// <summary>
        /// Hangs the winch model in the player's equip container while the winch is held.
        ///
        /// S1API's viewmodel equippable is an empty GameObject - it wires up the hand slot and the third-person
        /// animation, but brings no geometry - so something has to supply the mesh. Parenting it to the equippable
        /// instance is the obvious idea and does not work: <c>HotbarSlot.Equip</c> only instantiates that prefab
        /// when the definition asks for <c>EEquipMode.Legacy</c>, and otherwise hands the item to the newer
        /// equipping framework, leaving <c>PlayerInventory.equippable</c> null. The equip container is where held
        /// geometry lives either way, so the model goes there and is removed again on unequip.
        /// </summary>
        private static void EnsureViewmodel()
        {
            if (_modelFailed) return;

            try
            {
                var inv = PlayerSingleton<PlayerInventory>.Instance;
                Transform container = inv != null ? inv.EquipContainer : null;
                if (container == null) return;

                // That comment was wrong, and it cost a tuning session: the arms are NOT already there on this path.
                // The avatar only comes up when something assigns it an animator controller, which vanilla does per
                // weapon and nothing did here unless the hand-bone path was switched on. So the winch hung in space
                // with no arms, and there was nothing to judge its position against.
                //
                // Raising them without parenting to the bone is the useful middle: the arms come up in the borrowed
                // pose, the model stays in the equip container where ViewmodelPosition places it, and the two can be
                // aligned by eye. Switch off with 'yoink vm arms off' if the borrowed pose fights the tool.
                Transform hand = PreferHand ? PrepareHands() : null;
                if (hand == null && ShowArms) RaiseArms();

                Transform parent = hand != null ? hand : container;

                int id = parent.GetInstanceID();
                if (_modelInstance != null && id == _viewmodelId) return;   // already attached to this one

                if (EnsureModelSource() == null) return;

                if (_modelInstance != null) UnityEngine.Object.Destroy(_modelInstance);

                _onHand = hand != null;

                _modelInstance = UnityEngine.Object.Instantiate(_modelSource, parent);
                _modelInstance.name = "YoinkWinchModel";
                _modelInstance.transform.localPosition = _onHand ? HandPosition : ViewmodelPosition;
                _modelInstance.transform.localRotation = Quaternion.Euler(_onHand ? HandRotation : ViewmodelRotation);
                _modelInstance.transform.localScale = Vector3.one * (_onHand ? HandScale : ViewmodelScale);
                _modelInstance.SetActive(true);
                _viewmodelId = id;

                Core.Log.Msg("[Item] winch model attached to " + (_onHand ? "the right hand." : "the equip container (no hand bone found)."));
            }
            catch (Exception e)
            {
                _modelFailed = true;
                Core.Log.Warning("[Item] could not attach the winch model (the item still works, just invisible in hand): " + e.Message);
            }
        }

        /// <summary>Takes the model back out of the equip container when the winch is put away.</summary>
        internal static void RemoveViewmodel()
        {
            try { if (_modelInstance != null) UnityEngine.Object.Destroy(_modelInstance); }
            catch { }
            _modelInstance = null;
            _viewmodelId = 0;
            if (_onHand || _armsRaised) { LowerHands(); _onHand = false; _armsRaised = false; }
        }

        /// <summary>Re-places the model with the current offsets - used by the console while tuning them.</summary>
        internal static void RefreshViewmodel()
        {
            RemoveViewmodel();
            EnsureViewmodel();
        }

        /// <summary>
        /// Parses the embedded GLB once and keeps it as an inactive, scene-persistent original to copy from.
        /// Returns null when the model cannot be read, which leaves the winch usable but invisible in hand.
        /// </summary>
        internal static GameObject EnsureModelSource()
        {
            if (_modelSource != null) return _modelSource;
            if (_modelFailed) return null;

            try
            {
                byte[] glb = ReadEmbedded("Yoink.Assets.yoink_winch.glb");
                if (glb == null) { _modelFailed = true; return null; }

                _modelSource = Assets.GlbReader.Load(glb, "YoinkWinchModel");
                if (_modelSource == null) { _modelFailed = true; return null; }

                _modelSource.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_modelSource);
                return _modelSource;
            }
            catch (Exception e)
            {
                _modelFailed = true;
                Core.Log.Warning("[Item] could not load the winch model: " + e.Message);
                return null;
            }
        }

        /// <summary>Placement of the model inside the equippable template, relative to the hand.</summary>
        internal static Vector3 HeldPosition = new Vector3(0f, 0f, 0f);
        internal static Vector3 HeldRotation = new Vector3(0f, 180f, 0f);
        internal static float HeldScale = 1f;

        /// <summary>Same reader, for the audio side.</summary>
        internal static byte[] ReadEmbeddedPublic(string resource) => ReadEmbedded(resource);

        private static byte[] ReadEmbedded(string resource)
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var stream = asm.GetManifestResourceStream(resource))
                {
                    if (stream == null) { Core.Log.Warning("[Item] embedded resource missing: " + resource); return null; }
                    var data = new byte[stream.Length];
                    int off = 0;
                    while (off < data.Length)
                    {
                        int read = stream.Read(data, off, data.Length - off);
                        if (read <= 0) break;
                        off += read;
                    }
                    return data;
                }
            }
            catch (Exception e) { Core.Log.Warning("[Item] reading " + resource + " failed: " + e.Message); return null; }
        }

        /// <summary>True while the local player holds the winch.</summary>
        internal static bool IsEquipped()
        {
            try
            {
                var inv = PlayerSingleton<PlayerInventory>.Instance;
                if (inv == null || !inv.isAnythingEquipped) return false;
                ItemInstance item = inv.equippedSlot != null ? inv.equippedSlot.ItemInstance : null;
                return item != null && item.Definition != null && item.Definition.ID == Id;
            }
            catch { return false; }
        }

        /// <summary>
        /// Primary click fires the hook, or lets go if one is already attached. Polled from Core.OnUpdate with the
        /// same gates the vanilla equippables use: not typing, no UI element open.
        /// </summary>
        internal static void TickControls()
        {
            bool equipped = IsEquipped();

            // Putting the winch away lets go of whatever it was holding - a hook with no winch behind it would
            // keep a vehicle in neutral with nothing on screen to explain why.
            if (_wasEquipped && !equipped)
            {
                if (WinchSession.Hooked)
                {
                    WinchSession.Drop();
                    Core.Log.Msg("[Item] winch put away - hook released.");
                }
                RemoveViewmodel();
                ForgetHeldModel();
            }
            _wasEquipped = equipped;

            WinchAim.Tick(equipped, WinchSession.Hooked);
            Audio.WinchSound.Tick(equipped && WinchSession.Pulling, WinchSession.ReelRate, Config.Preferences.MaxSpeed);

            if (!equipped) return;

            // Preferred path: the game holds the winch itself, using a template we handed it (WinchEquippable).
            // The loose model in the equip container is only the fallback for when that could not be installed.
            WinchEquippable.EnsureInstalled(EnsureModelSource());
            if (!WinchEquippable.Ready)
            {
                EnsureViewmodel();
                KeepHandsUp();
            }

            try
            {
                if (Time.unscaledTime < _nextClickAt) return;
                if (GameInput.IsTyping) return;
                if (!Winch.Input.HookPressed()) return;

                PlayerCamera cam = PlayerSingleton<PlayerCamera>.Instance;
                if (cam != null && cam.activeUIElementCount > 0) return;

                _nextClickAt = Time.unscaledTime + 0.25f;

                if (WinchSession.Hooked)
                {
                    WinchSession.Drop();
                    return;
                }

                string message;
                WinchSession.HookFromCamera(out message);
                Core.Log.Msg("[Item] " + message);
            }
            catch (Exception e) { Core.Log.Warning("[Item] control tick failed: " + e.Message); }
        }

        internal static bool Give(int quantity = 1)
        {
            if (!_registered) return false;
            try
            {
                S1API.Console.ConsoleHelper.AddItemToInventory(Id, quantity);
                return true;
            }
            catch (Exception e)
            {
                Core.Log.Warning("[Item] giving the winch failed: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Placeholder art: a vanilla tool icon, so the item is not a blank square in the hotbar. The mod's own
        /// icon is being drawn separately and replaces this before anything ships - a borrowed icon is fine for a
        /// build under test and is not fine on a release.
        /// </summary>
        private static Sprite BorrowedIcon()
        {
            string[] donors = { "trashgrabber", "trimmers", "wateringcan", "trashbag" };
            for (int i = 0; i < donors.Length; i++)
            {
                try
                {
                    var def = Il2CppScheduleOne.Registry.GetItem(donors[i]);
                    if (def != null && def.Icon != null) return def.Icon;
                }
                catch { }
            }
            return null;
        }
    }
}
