using System;

namespace Yoink.Item
{
    /// <summary>
    /// Draws the item icon from the winch model itself, once, at registration time.
    ///
    /// The alternative was a PNG exported from Blender and shipped beside the mesh, and it is the worse one: two
    /// files that describe the same object drift apart the moment the model is re-exported, and the icon is exactly
    /// the thing nobody remembers to redo. Rendering the real GLB means the icon cannot be out of date - it IS the
    /// model, framed and photographed at startup.
    ///
    /// Everything happens far below the world on a layer nothing else uses, with a camera and a light that only see
    /// that layer, so no part of this is visible to the player for the frame it exists. Any failure falls back to a
    /// borrowed vanilla sprite rather than leaving a blank square in the hotbar.
    /// </summary>
    internal static class IconRenderer
    {
        private const int Size = 256;
        private const int Layer = 31;                       // the last user layer; the camera sees only this one
        private static readonly Vector3 Nowhere = new Vector3(0f, -10000f, 0f);

        private static Sprite _cached;

        /// <summary>The winch's icon, rendered once per session. Null when it could not be produced.</summary>
        internal static Sprite Render(GameObject model)
        {
            if (_cached != null) return _cached;
            if (model == null) return null;

            GameObject subject = null;
            GameObject rig = null;
            RenderTexture rt = null;
            RenderTexture previous = RenderTexture.active;

            try
            {
                subject = UnityEngine.Object.Instantiate(model);
                subject.name = "YoinkIconSubject";
                subject.transform.position = Nowhere;
                subject.transform.rotation = Quaternion.identity;
                subject.transform.localScale = Vector3.one;
                subject.SetActive(true);
                SetLayerRecursively(subject.transform, Layer);

                Bounds b;
                if (!TryBounds(subject.transform, out b)) return null;

                rig = new GameObject("YoinkIconRig");
                rig.transform.position = Nowhere;

                // A three-quarter view from slightly above: the same angle the model was authored to read at, and the
                // one where the drum, the body and the hook are all visible at once. Straight-on hides the hook.
                Quaternion look = Quaternion.Euler(18f, 35f, 0f);
                Vector3 dir = look * Vector3.forward;

                var camObj = new GameObject("YoinkIconCamera");
                camObj.transform.SetParent(rig.transform, false);
                camObj.transform.position = b.center - dir * (b.extents.magnitude * 4f + 1f);
                camObj.transform.rotation = look;

                Camera cam = camObj.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);   // transparent, so the icon has no letterbox
                cam.cullingMask = 1 << Layer;
                cam.orthographic = true;
                // Slightly larger than the object so nothing touches the edge of the sprite.
                cam.orthographicSize = ProjectedRadius(b, look) * 1.12f;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = b.extents.magnitude * 8f + 10f;
                cam.enabled = false;                              // rendered by hand, never by the frame loop
                cam.allowHDR = false;
                cam.allowMSAA = false;

                // The model uses the game's lit shader, so with no light it photographs black. Two lights: a key from
                // the camera's shoulder for form, and a dim fill from the other side so the shadowed faces still read.
                AddLight(rig.transform, look * Quaternion.Euler(25f, -30f, 0f), 1.35f);
                AddLight(rig.transform, look * Quaternion.Euler(-15f, 140f, 0f), 0.55f);

                rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
                rt.Create();
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                var tex = new Texture2D(Size, Size, TextureFormat.ARGB32, false)
                {
                    name = "YoinkIcon",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                tex.ReadPixels(new Rect(0f, 0f, Size, Size), 0, 0, false);
                tex.Apply();

                _cached = Sprite.Create(tex, new Rect(0f, 0f, Size, Size), new Vector2(0.5f, 0.5f), 100f);
                if (_cached != null) _cached.hideFlags = HideFlags.HideAndDontSave;

                Core.Log.Msg("[Icon] item icon rendered from the winch model (" + Size + "px).");
                return _cached;
            }
            catch (Exception e)
            {
                Core.Log.Warning("[Icon] could not render the item icon, falling back to a vanilla sprite: " + e.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                try { if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); } } catch { }
                try { if (subject != null) UnityEngine.Object.Destroy(subject); } catch { }
                try { if (rig != null) UnityEngine.Object.Destroy(rig); } catch { }
            }
        }

        private static void AddLight(Transform parent, Quaternion rotation, float intensity)
        {
            var go = new GameObject("YoinkIconLight");
            go.transform.SetParent(parent, false);
            go.transform.rotation = rotation;
            Light l = go.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = intensity;
            l.cullingMask = 1 << Layer;      // never touches anything the player can see
            l.shadows = LightShadows.None;
        }

        /// <summary>
        /// How much of the frame the object needs, measured along the camera's own axes.
        ///
        /// The bounding sphere would be simpler and is what a first attempt reaches for, but it sizes for the longest
        /// diagonal and leaves a long thin tool floating in the middle of a mostly empty icon. Projecting the box onto
        /// the camera's right and up axes fits what is actually seen.
        /// </summary>
        private static float ProjectedRadius(Bounds b, Quaternion look)
        {
            Vector3 e = b.extents;
            Vector3 right = look * Vector3.right;
            Vector3 up = look * Vector3.up;

            float halfW = Mathf.Abs(right.x) * e.x + Mathf.Abs(right.y) * e.y + Mathf.Abs(right.z) * e.z;
            float halfH = Mathf.Abs(up.x) * e.x + Mathf.Abs(up.y) * e.y + Mathf.Abs(up.z) * e.z;
            return Mathf.Max(0.01f, Mathf.Max(halfW, halfH));
        }

        private static bool TryBounds(Transform root, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }

        private static void SetLayerRecursively(Transform t, int layer)
        {
            if (t == null) return;
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayerRecursively(t.GetChild(i), layer);
        }
    }
}
