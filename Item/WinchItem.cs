using System;
using System.Globalization;
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
    /// the console commands rather than dying: a winch you can still fire from the console beats a mod that took the
    /// session down with it.
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
#if DEBUG
            _holdDumped = false;
#endif
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
                    .WithIcon(Icon())
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
        internal static bool PreferHand = false;

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

                Vector3 onModel;
                if (MuzzleOverride != Vector3.zero) onModel = model.TransformPoint(MuzzleOverride);
                else if (!TryMeasureMuzzle(out onModel)) onModel = MeasuredMuzzle(model);

                world = ToRopeAnchor(onModel, model.gameObject.layer);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// How far in front of the eye the rope is anchored.
        ///
        /// Not where the muzzle is - where the rope should START. The first-person rig is a quarter-scale avatar and
        /// its muzzle really is about 10 cm from the camera, so a rope pinned exactly there is a 3 cm cable seen from
        /// 10 cm away: it fills a third of the screen like a tree trunk. Anchoring at arm's length along the SAME
        /// line of sight keeps the cable visually attached to the muzzle and gives it a believable thickness.
        /// </summary>
        internal static float RopeStartDepth = 0.7f;

        /// <summary>
        /// Where the rope leaves the tool, nudged across the screen. In fractions of screen HEIGHT, not pixels, so a
        /// value dialled in at one resolution still lands in the same place at another. X is right, Y is up.
        /// </summary>
        /// <remarks>
        /// The default is not zero and is not meant to be. The measured muzzle is the geometric cable exit on the
        /// mesh; the hook the eye reads the rope as leaving from sits up and to the right of it once the tool is in
        /// the aim pose. These numbers were dialled in against the shipped model at 0.70 scale - re-tune them if the
        /// model changes, do not "clean them up" to zero.
        /// </remarks>
        internal static Vector2 RopeAnchorNudge = new Vector2(0.120f, 0.110f);

        /// <summary>
        /// Turns a point ON THE HELD MODEL into the point the rope should actually be pinned to.
        ///
        /// It keeps the muzzle's position ON SCREEN and changes only its distance. Screen space is the right currency
        /// here for two reasons at once: it is the only thing that has to match for the cable to look attached, and it
        /// is the only space that still agrees if the viewmodel is ever drawn by its own stacked camera with its own
        /// FOV - a normal way to render a first-person model, and one this build could adopt at any time.
        ///
        /// What it buys today is thickness. The muzzle sits about 10 cm from the eye, and a rope pinned there is a
        /// cable seen from 10 cm away. Pushed out to arm's length along the same line of sight it stays glued to the
        /// muzzle and looks like rope.
        /// </summary>
        private static Vector3 ToRopeAnchor(Vector3 onModel, int layer)
        {
            Camera world = WorldCamera();
            if (world == null) return onModel;

            Camera drawn = ViewmodelCamera(layer, world) ?? world;

            Vector3 screen = drawn.WorldToScreenPoint(onModel);
            if (screen.z <= 0.001f) return onModel;   // behind the camera - there is no screen point to work from

            // Nudge the anchor across the picture. The measured muzzle is the geometric cable exit, which is not
            // always where the eye expects the rope to leave - the hook swings, the aim pose turns the tool, and a
            // few pixels of correction read better than a "correct" anchor that looks a hair off the hook. Nudging
            // here rather than in model space keeps it doing the same thing whatever pose the tool is in.
            screen.x += RopeAnchorNudge.x * Screen.height;
            screen.y += RopeAnchorNudge.y * Screen.height;

            Ray ray = world.ScreenPointToRay(screen);

            // Depth is measured along the camera's forward axis, not along the ray, because that is what the near
            // plane cuts against - at the edge of a wide FOV the two differ enough to matter.
            float forward = Vector3.Dot(ray.direction, world.transform.forward);
            float depth = Mathf.Max(RopeStartDepth, (world.nearClipPlane + 0.05f) / Mathf.Max(forward, 0.2f));
            return ray.origin + ray.direction * depth;
        }

        /// <summary>
        /// Everything the rope anchor depends on, in one console dump: which camera draws the world, which cameras
        /// exist and what layers they draw, where the model actually is, and what the muzzle projects to. Placing a
        /// point that has to line up across two cameras cannot be done by reasoning about it from the outside.
        /// </summary>
        internal static System.Collections.Generic.List<string> DescribeCameras()
        {
            var inv = CultureInfo.InvariantCulture;
            var lines = new System.Collections.Generic.List<string>();

            try
            {
                Camera world = WorldCamera();
                lines.Add("world camera: " + (world == null ? "none" :
                    world.name + " at " + world.transform.position.ToString("F2")
                    + " fov " + world.fieldOfView.ToString("F1", inv)
                    + " near " + world.nearClipPlane.ToString("F2", inv)
                    + " mask 0x" + world.cullingMask.ToString("X")));

                int viewmodelLayer = LayerMask.NameToLayer("Viewmodel");
                lines.Add("'Viewmodel' layer = " + viewmodelLayer);

                var cameras = Camera.allCameras;
                lines.Add(cameras.Length + " enabled camera(s):");
                for (int i = 0; i < cameras.Length; i++)
                {
                    Camera c = cameras[i];
                    if (c == null) continue;
                    bool drawsViewmodel = viewmodelLayer >= 0 && (c.cullingMask & (1 << viewmodelLayer)) != 0;
                    lines.Add("  " + c.name + " pos " + c.transform.position.ToString("F2")
                        + " fov " + c.fieldOfView.ToString("F1", inv)
                        + " depth " + c.depth.ToString("F0", inv)
                        + " mask 0x" + c.cullingMask.ToString("X")
                        + (drawsViewmodel ? "  <- draws Viewmodel" : string.Empty));
                }

                Transform model = HeldModel();
                if (model == null) { lines.Add("no winch in hand - equip it and run this again."); return lines; }

                lines.Add("model: layer " + model.gameObject.layer + " world pos " + model.position.ToString("F2"));

                Vector3 onModel;
                if (!TryMeasureMuzzle(out onModel)) onModel = MeasuredMuzzle(model);
                lines.Add("muzzle on model: " + onModel.ToString("F3")
                    + " local " + model.InverseTransformPoint(onModel).ToString("F3"));

                if (world != null)
                {
                    Camera drawn = ViewmodelCamera(model.gameObject.layer, world) ?? world;
                    Vector3 screen = drawn.WorldToScreenPoint(onModel);
                    lines.Add("projected by '" + drawn.name + "' -> screen " + screen.ToString("F0")
                        + " (screen is " + Screen.width + "x" + Screen.height + ")");
                    lines.Add("rope anchor: " + ToRopeAnchor(onModel, model.gameObject.layer).ToString("F2"));
                }
            }
            catch (Exception e) { lines.Add("camera dump failed: " + e.Message); }

            return lines;
        }

        private static Camera WorldCamera()
        {
            try
            {
                var pc = PlayerSingleton<PlayerCamera>.Instance;
                if (pc != null && pc.Camera != null) return pc.Camera;
            }
            catch { }
            return null;
        }

        private static Camera _viewmodelCamera;
        private static int _viewmodelCameraLayer = -1;

        /// <summary>
        /// The camera that draws the first-person layer, or null when the world camera draws it itself.
        ///
        /// Found rather than named, because the name is not part of any contract we control. The test is: it renders
        /// the layer the model sits on, it is not the world camera, and it sits ON the world camera - that last part
        /// is what keeps a security camera prop pointed at the same layer from being mistaken for the viewmodel one.
        /// </summary>
        private static Camera ViewmodelCamera(int layer, Camera world)
        {
            try
            {
                if (_viewmodelCamera != null && _viewmodelCameraLayer == layer && _viewmodelCamera.enabled)
                    return _viewmodelCamera;

                _viewmodelCamera = null;
                _viewmodelCameraLayer = layer;

                int worldId = world.GetInstanceID();
                Vector3 eye = world.transform.position;
                int mask = 1 << layer;

                var cameras = Camera.allCameras;
                for (int i = 0; i < cameras.Length; i++)
                {
                    Camera c = cameras[i];
                    if (c == null || !c.enabled) continue;
                    if (c.GetInstanceID() == worldId) continue;
                    if ((c.cullingMask & mask) == 0) continue;
                    if ((c.transform.position - eye).sqrMagnitude > 4f) continue;   // not on the player's head

                    _viewmodelCamera = c;
                    Core.Log.Msg("[Item] rope anchored through the first-person camera '" + c.name
                        + "' (fov " + c.fieldOfView.ToString("F0", CultureInfo.InvariantCulture)
                        + " vs world " + world.fieldOfView.ToString("F0", CultureInfo.InvariantCulture) + ").");
                    break;
                }

                return _viewmodelCamera;
            }
            catch { return null; }
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
        /// Where the cable actually leaves the tool, in the model's own space.
        ///
        /// Derived from the Hook mesh's bounds rather than from a hand-tuned constant, because a constant is only
        /// true for the geometry it was tuned against - re-export the model and the rope starts in mid-air, which
        /// has already happened twice here. But NOT the hook's centre: the hook hangs down and back from the cable
        /// exit, and its centroid measures 7.3 cm away from it. The exit is the top of the front face, which is
        /// where the mesh's own ring of attachment vertices sits.
        ///
        /// Measured against the current model, that lands within ~5 mm of the exact ring centre at
        /// (0, 0.120, -0.350) - close enough that nobody will see the difference, and it survives a re-export.
        /// </summary>
        /// <summary>
        /// Writes the current hold offsets onto the model that is actually in hand, every frame.
        ///
        /// Without this, tuning the numbers did nothing visible: on the equippable path the model lives inside a
        /// clone the GAME instantiated from our template, so changing the template's values only takes effect the
        /// next time the item is equipped. Pushing them onto the live object makes 'yoink vm' immediate, which is
        /// the difference between dialling a position in and rebuilding for every centimetre.
        /// </summary>
        private static void ApplyHeldTransform()
        {
            try
            {
                Transform model = HeldModel();
                if (model == null) return;

                model.localPosition = HeldPosition;
                model.localRotation = Quaternion.Euler(HeldRotation);
                model.localScale = Vector3.one * HeldScale;
            }
            catch { }

            ApplyRigOffset();
        }

        /// <summary>
        /// Where the whole first-person rig sits, on top of the offset borrowed from the donor weapon.
        ///
        /// The donor's offset frames a PISTOL: a short tool whose barrel is level with the hand. A winch is long and
        /// carries its business end well forward of the grip, and with the pistol framing the muzzle measured out at
        /// screen y = -8 on a 1200 px screen - just off the bottom edge. That is why the cable looked detached: the
        /// rope was anchored correctly and the anchor was outside the picture. Lifting the rig brings the muzzle,
        /// the hook and the first metre of cable into view.
        /// </summary>
        internal static Vector3 RigOffset = new Vector3(0f, 0.005f, 0f);

        private static void ApplyRigOffset()
        {
            if (RigOffset == Vector3.zero) return;

            try
            {
                var avatar = Singleton<ViewmodelAvatar>.Instance;
                if (avatar == null || !avatar.IsVisible) return;

                if (_borrowedAnimator == null) BorrowedAnimator();   // fills _borrowedOffset as a side effect

                // Every frame, not once: SetVisibility resets the offset to zero as part of its own bookkeeping, so
                // a value written at equip time quietly disappears the next time anything toggles the avatar.
                avatar.SetOffset(_borrowedOffset + RigOffset);
            }
            catch { }
        }

        /// <summary>
        /// Where the muzzle lands on screen, in pixels from the bottom-left, or false when nothing is held.
        ///
        /// The one number that matters for framing the tool: the cable leaves from here, so if this is outside
        /// 0..Screen.width / 0..Screen.height the rope starts off-camera and looks detached however correct the
        /// maths is. Read from LateUpdate - in Update the avatar rig is still in the previous frame's pose.
        /// </summary>
        internal static bool TryMuzzleScreen(out Vector2 screen)
        {
            screen = Vector2.zero;
            try
            {
                Transform model = HeldModel();
                Camera world = WorldCamera();
                if (model == null || world == null) return false;

                Vector3 onModel;
                if (!TryMeasureMuzzle(out onModel)) onModel = MeasuredMuzzle(model);

                Vector3 p = world.WorldToScreenPoint(onModel);
                screen = new Vector2(p.x, p.y);
                return true;
            }
            catch { return false; }
        }

        /// <summary>How the winch is framed right now, as a couple of readable lines.</summary>
        internal static string DescribeFraming()
        {
            var inv = CultureInfo.InvariantCulture;

            string muzzle;
            Vector2 screen;
            if (!TryMuzzleScreen(out screen)) muzzle = "not held";
            else
            {
                // Both points, because they are not the same once the nudge is non-zero and only one of them is the
                // end the rope is actually pinned to. Showing just the muzzle made the nudge buttons look dead.
                Vector2 anchor = screen + new Vector2(RopeAnchorNudge.x, RopeAnchorNudge.y) * Screen.height;
                bool onScreen = anchor.x >= 0f && anchor.x <= Screen.width && anchor.y >= 0f && anchor.y <= Screen.height;

                muzzle = screen.x.ToString("F0", inv) + "," + screen.y.ToString("F0", inv)
                       + "   rope starts at " + anchor.x.ToString("F0", inv) + "," + anchor.y.ToString("F0", inv)
                       + " of " + Screen.width + "x" + Screen.height
                       + (onScreen ? "" : "  << OFF SCREEN");
            }

            return "scale " + HeldScale.ToString("F2", inv)
                 + "   rig " + RigOffset.ToString("F3")
                 + "\ntool " + HeldOffset.ToString("F3")
                 + "   turn " + HeldRotation.y.ToString("F0", inv) + "/" + HeldRotation.x.ToString("F0", inv) + "deg"
                 + "\nrope anchor " + RopeStartDepth.ToString("F2", inv) + "m"
                 + "   nudge " + RopeAnchorNudge.ToString("F3")
                 + "\nmuzzle on screen: " + muzzle;
        }

        /// <summary>Puts the framing back to the shipped values, so a tuning session can always start over.</summary>
        internal static void ResetFraming()
        {
            HeldScale = 0.7f;
            RigOffset = new Vector3(0f, 0.005f, 0f);
            HeldOffset = new Vector3(0f, 0f, -0.08f);
            HeldRotation = new Vector3(0f, 180f, 0f);
            RopeStartDepth = 0.7f;
            RopeAnchorNudge = new Vector2(0.120f, 0.110f);
            _heldPositionOverridden = false;
            MuzzleOverride = Vector3.zero;
        }

        /// <summary>One line describing where the held model currently sits, for the console.</summary>
        internal static string DescribeHold()
        {
            var inv = CultureInfo.InvariantCulture;
            return "hold pos=" + HeldPosition.ToString("F3")
                 + " rot=" + HeldRotation.ToString("F0")
                 + " scale=" + HeldScale.ToString("F2", inv)
                 + " grip=" + GripLocal.ToString("F3")
                 + (HeldModel() != null ? " [live]" : " [not held]");
        }

        private static bool TryMeasureMuzzle(out Vector3 world)
        {
            world = Vector3.zero;
            Transform model = HeldModel();
            if (model == null) return false;

            try
            {
                MeshFilter hookMesh = null;

                for (int i = 0; i < model.childCount && hookMesh == null; i++)
                {
                    Transform child = model.GetChild(i);
                    if (child.name.IndexOf("hook", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    hookMesh = child.GetComponentInChildren<MeshFilter>(true);
                }

                if (hookMesh == null || hookMesh.sharedMesh == null) return false;

                // Mesh bounds live in the MESH's own space, and the reader puts each mesh under a node object -
                // so the point has to be transformed by the mesh's transform, not by the model root. Using the
                // root is what threw the rope sideways: any offset between node and mesh became an error in the
                // rope's start, and it looked like the cable was leaving somewhere off to the left.
                Bounds b = hookMesh.sharedMesh.bounds;
                Vector3 local = new Vector3(b.center.x, b.max.y, b.min.z);
                world = hookMesh.transform.TransformPoint(local);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// The model actually being rendered in hand. On the equippable path that is a child of the clone the game
        /// instantiated, not anything we hold a reference to, so it is looked up through PlayerInventory - which
        /// finally reports an equippable now that the definition uses the legacy equip mode.
        /// </summary>
        private static Transform HeldModel()
        {
            try
            {
                if (_heldModel != null) return _heldModel;

                // Found by name in the two places it can live, rather than through PlayerInventory.equippable. That
                // property is only filled on the legacy equip path, and the very first equip of a session can still
                // run through the new framework - so it reports null while the winch is plainly in your hands, and
                // everything measured off the model silently stops working.
                var avatar = Singleton<ViewmodelAvatar>.Instance;
                if (avatar != null && avatar.RightHandContainer != null)
                {
                    _heldModel = FindByName(avatar.RightHandContainer, "YoinkWinchModel", 0);
                    if (_heldModel != null) return _heldModel;
                }

                var inv = PlayerSingleton<PlayerInventory>.Instance;
                Transform container = inv != null ? inv.EquipContainer : null;
                if (container != null) _heldModel = FindByName(container, "YoinkWinchModel", 0);

                return _heldModel;
            }
            catch { return null; }
        }

        /// <summary>Drops the cached lookup when the held item changes.</summary>
        internal static void ForgetHeldModel()
        {
            _heldModel = null;
        }

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

        /// <summary>
        /// Re-places the model with the current offsets - used by the console while tuning them.
        ///
        /// Only ever touches the FALLBACK model. Calling it while the game holds the winch itself used to hang a
        /// second copy in the equip container, and since that copy is what HeldModel() finds first, every later
        /// measurement - the rope's start above all - came off a model nobody could see.
        /// </summary>
        internal static void RefreshViewmodel()
        {
            RemoveViewmodel();
            if (!WinchEquippable.Ready) EnsureViewmodel();
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

        /// <summary>
        /// The centre of the pistol grip in the model's own space, measured from the mesh (the volume centroid of
        /// the grip solid, after the reader's Z mirror). The model has to be shifted so THIS point lands on the
        /// hand, not its origin - with the origin on the hand the tool hung in front of the fingers instead of in
        /// them, which is exactly what it looked like.
        /// </summary>
        internal static Vector3 GripLocal = new Vector3(0f, -0.010f, -0.066f);

        /// <summary>Placement of the model inside the equippable template, relative to the hand.</summary>
        internal static Vector3 HeldRotation = new Vector3(0f, 180f, 0f);

        /// <summary>
        /// How big the winch is in hand, on top of the quarter-scale the first-person avatar already renders at.
        /// Full size read as a slab of tool filling the right of the screen; 0.7 leaves it readable without
        /// shrinking it to a prop.
        /// </summary>
        internal static float HeldScale = 0.7f;

        private static bool _heldPositionOverridden;
        private static Vector3 _heldPosition;

        /// <summary>
        /// Where the model sits relative to the hand: far enough back that the grip is in the palm. Computed from
        /// the grip point and the model's own rotation rather than typed in, so changing the rotation cannot
        /// silently push the tool out of the hand again.
        /// </summary>
        internal static Vector3 HeldPosition
        {
            // Scaled by HeldScale, because the grip point is a distance INSIDE the model: shrink the model and the
            // grip moves towards its origin, so a correction sized for full scale would push a smaller winch back
            // out of the hand. The hand-dialled part is a distance in the HAND's space and deliberately does not
            // scale with it.
            get => _heldPositionOverridden
                ? _heldPosition
                : -(Quaternion.Euler(HeldRotation) * GripLocal) * HeldScale + HeldOffset;
            set { _heldPosition = value; _heldPositionOverridden = true; }
        }

        /// <summary>
        /// Hand-dialled correction on top of the computed grip alignment, settled in game with 'yoink vm move'.
        ///
        /// The computed position puts the grip's centroid on the hand bone, which is geometrically right and still
        /// sat 8 cm too far forward - a real hand wraps behind the grip's centre, not through it, and the bone's
        /// pivot is at the wrist. Keeping the two apart matters: the computed part follows the model and its
        /// rotation, this part is taste, and mixing them would make a re-export silently undo the tuning.
        /// </summary>
        internal static Vector3 HeldOffset = new Vector3(0f, 0f, -0.08f);

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
            Audio.WinchSound.Tick(equipped && WinchSession.Pulling, WinchSession.ReelRateSmoothed,
                                  Config.Preferences.MaxSpeed, WinchSession.Stalled,
                                  equipped && WinchSession.Hooked ? WinchSession.PayOutRate : 0f);

            Vector3 muzzle;
            if (TryGetMuzzle(out muzzle)) Audio.WinchSound.FollowMuzzle(muzzle);

            // Installed BEFORE the equipped check, not after. HotbarSlot.Equip picks its path from EquipMode at the
            // moment you select the slot: if the template is not in place yet the item goes through the new equipping
            // framework, which brings no geometry, and the winch is invisible until something equips it a second
            // time. Installing as soon as the avatar exists means the first equip is already the right one.
            // The loose model in the equip container is only the fallback for when this could not be installed.
            // Gated on registration: the install has to write into the registered definition, and a miss there is
            // treated as permanent - running it one frame too early would disable the good path for the session.
            if (_registered) WinchEquippable.EnsureInstalled(EnsureModelSource());

            if (!equipped) return;

            // The fallback can win the race on the first frames - the equippable needs the avatar to be up, and
            // until it is, a loose model goes into the equip container. Leaving it there once the game holds the
            // real one means two winches exist and HeldModel() finds the invisible one first, which is where every
            // measurement taken off it - the rope's start above all - quietly goes wrong.
            if (WinchEquippable.Ready && _modelInstance != null) RemoveViewmodel();

            ApplyHeldTransform();
            if (!WinchEquippable.Ready)
            {
                EnsureViewmodel();
                KeepHandsUp();
            }

#if DEBUG
            DumpHoldOnce();
#endif

            try
            {
                if (Time.unscaledTime < _nextClickAt) return;
                if (GameInput.IsTyping) return;
                if (!Winch.Input.HookPressed()) return;

                PlayerCamera cam = PlayerSingleton<PlayerCamera>.Instance;
                if (cam != null && cam.activeUIElements.Count > 0) return;

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

#if DEBUG
        private static float _nextFrameReport;

        /// <summary>
        /// Once a second while the winch is held: where the muzzle lands on screen. Tuning the framing needs a number
        /// that updates as the value changes, and the console cannot supply one - the game unequips while it is open,
        /// so anything asked from there truthfully answers "nothing in hand".
        ///
        /// Called from LateUpdate, like the rope itself. Read in Update it reports a different point entirely - the
        /// avatar rig is still in last frame's pose - and a diagnostic that measures a moment nobody sees is worse
        /// than none: it sent this hunt after a phantom offset that was only ever in the reading.
        /// </summary>
        internal static void ReportFraming()
        {
            if (Time.unscaledTime < _nextFrameReport) return;
            _nextFrameReport = Time.unscaledTime + 1f;

            try
            {
                if (HeldModel() == null) return;
                Core.Log.Msg("[Frame] " + DescribeFraming().Replace('\n', ' '));
            }
            catch { }
        }

        private static bool _holdDumped;

        /// <summary>
        /// Logs the hold and muzzle geometry the first time the winch is genuinely in hand.
        ///
        /// Not a console command, on purpose: a console command runs with the console open, and the game unequips
        /// while a UI element is up - so every attempt to inspect the held model from the console truthfully reports
        /// that nothing is held. Firing from the update loop is the only place the answer exists.
        /// </summary>
        private static void DumpHoldOnce()
        {
            if (_holdDumped) return;
            if (HeldModel() == null) return;

            _holdDumped = true;
            foreach (string line in DescribeCameras()) Core.Log.Msg("[Cam] " + line);
        }

        private static bool _lateDumped;

        /// <summary>
        /// The same muzzle reading taken in LateUpdate, to see whether the first-person arms have already been moved
        /// into their drawn position by then. ViewmodelAvatar.LateUpdate shoves the whole rig forward every frame,
        /// so a reading taken before it runs describes a pose nobody ever sees.
        /// </summary>
        private static float _lateDumpAt;

        /// <summary>Re-arms both dumps, so 'yoink cam' can be used more than once per session.</summary>
        internal static void ArmDumps()
        {
            _holdDumped = false;
            _lateDumped = false;
            _lateDumpAt = 0f;
        }

        internal static void DumpMuzzleLateOnce()
        {
            if (_lateDumped) return;

            try
            {
                Transform model = HeldModel();
                if (model == null) return;

                // Not on the first frame the model exists. That frame is the START of the equip animation, with the
                // arms still down at the player's sides - a real reading of a pose nobody sees, which looks exactly
                // like the muzzle being computed wrongly. Wait for the animation to settle first.
                if (_lateDumpAt == 0f) { _lateDumpAt = Time.unscaledTime + 2f; return; }
                if (Time.unscaledTime < _lateDumpAt) return;

                Camera world = WorldCamera();
                if (world == null) return;

                Vector3 onModel;
                if (!TryMeasureMuzzle(out onModel)) onModel = MeasuredMuzzle(model);

                _lateDumped = true;
                Vector3 screen = world.WorldToScreenPoint(onModel);
                Core.Log.Msg("[Cam] LATE muzzle " + onModel.ToString("F3")
                    + " screen " + screen.ToString("F1")
                    + " (eye " + world.transform.position.ToString("F3") + ")");

                // The renderer's bounds are where the object is DRAWN. If they sit off-screen while a winch is
                // plainly visible, the object we measured is not the object being rendered - which is a different
                // bug from the maths being wrong, and the two are indistinguishable without this line.
                string path = model.name;
                for (Transform p = model.parent; p != null; p = p.parent) path = p.name + "/" + path;
                Core.Log.Msg("[Cam] LATE model '" + path + "' active=" + model.gameObject.activeInHierarchy);

                foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    Core.Log.Msg("[Cam] LATE renderer '" + r.name + "' on=" + r.enabled
                        + " centre " + r.bounds.center.ToString("F3")
                        + " screen " + world.WorldToScreenPoint(r.bounds.center).ToString("F1"));
                }

                var avatar = Singleton<ViewmodelAvatar>.Instance;
                if (avatar != null && avatar.RightHandContainer != null)
                {
                    Core.Log.Msg("[Cam] LATE hand " + avatar.RightHandContainer.position.ToString("F3")
                        + " screen " + world.WorldToScreenPoint(avatar.RightHandContainer.position).ToString("F1")
                        + " visible=" + avatar.IsVisible
                        + " children=" + avatar.RightHandContainer.childCount);
                }

                // Control reading: a point straight ahead must land on the crosshair. If it does not, the projection
                // itself is lying and every other screen figure above is worthless.
                Vector3 ahead = world.transform.position + world.transform.forward * 2f;
                Core.Log.Msg("[Cam] LATE control: 2m ahead -> screen "
                    + world.WorldToScreenPoint(ahead).ToString("F1") + " (centre is 960,600)");
            }
            catch { }
        }
#endif

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
        /// The item's icon: the winch model itself, photographed at startup. Falls back to a borrowed vanilla sprite
        /// only if that fails, so the worst case is a wrong-looking icon rather than a blank square in the hotbar.
        /// </summary>
        private static Sprite Icon()
        {
            Sprite rendered = IconRenderer.Render(EnsureModelSource());
            if (rendered != null) return rendered;
            return BorrowedIcon();
        }

        /// <summary>Last resort only - a vanilla tool sprite, so a failed render is not an empty square.</summary>
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
