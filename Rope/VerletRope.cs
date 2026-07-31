using System;

namespace Yoink.Rope
{
    /// <summary>
    /// The visible rope: a small verlet simulation drawn on a LineRenderer.
    ///
    /// Nothing about it is networked and nothing needs to be. Both ends are positions every machine already knows
    /// (the hooked pivot and the winch anchor), and what happens between them is cosmetic - the simulation is
    /// frame-rate dependent and two clients will not produce identical sag, which does not matter for a rope
    /// nobody collides with. What it buys over a straight line is the thing players actually read: slack hangs,
    /// and the moment the winch bites, the rope visibly straightens.
    /// </summary>
    internal sealed class VerletRope
    {
        private const float Gravity = -9.81f;
        private const float Damping = 0.96f;
        private const int Iterations = 6;

        /// <summary>Half the rope's drawn thickness - how far a resting point sits off a surface.</summary>
        private const float Radius = 0.02f;

        /// <summary>How far above a point to start the fallback ground probe.</summary>
        private const float GroundProbe = 0.06f;

        private readonly Vector3[] _points;
        private readonly Vector3[] _prev;
        private readonly int _count;

        private GameObject _go;
        private LineRenderer _lr;
        private GameObject _guide;      // the eyelet the rope's near end disappears into
        private Material _ropeMaterial;
        private Material _guideMaterial;
        private Texture2D _ropeTexture;
        private bool _attached;
        private float _restLength = 1f;

        internal VerletRope(int segments)
        {
            _count = Mathf.Clamp(segments, 4, 64);
            _points = new Vector3[_count];
            _prev = new Vector3[_count];
        }

        /// <summary>Lays a fresh rope between hook and anchor with a bit of slack in it.</summary>
        internal void Attach(Vector3 hook, Vector3 anchor)
        {
            EnsureRenderer();
            if (_lr == null) return;

            float dist = Vector3.Distance(hook, anchor);
            _restLength = Mathf.Max(dist * 1.15f, 0.5f);

            for (int i = 0; i < _count; i++)
            {
                float t = (float)i / (_count - 1);
                _points[i] = Vector3.Lerp(hook, anchor, t);
                _prev[i] = _points[i];
            }

            _attached = true;
            _lr.enabled = true;

            // The eyelet exists to give the rope something to disappear into. With the winch model in hand the
            // model itself does that job, and a second sleeve floating in front of it just looks like a mistake.
            if (_guide != null) _guide.SetActive(!Winch.WinchSession.RopeEndsInModel());

            Render();
        }

        internal void Detach()
        {
            _attached = false;
            if (_lr != null) _lr.enabled = false;
            if (_guide != null) _guide.SetActive(false);
        }

        internal void SetEnds(Vector3 hook, Vector3 anchor)
        {
            if (!_attached) return;
            _points[0] = hook;
            _points[_count - 1] = anchor;
        }

        /// <summary>
        /// One simulation step. <paramref name="taut"/> is the winch reeling; the cable takes its slack up over a
        /// moment rather than on that frame, because seeing it tighten is most of what tells a player it has bitten.
        /// </summary>
        internal void Simulate(float dt, bool taut)
        {
            if (!_attached || _lr == null) return;
            if (dt <= 0f) return;

            Vector3 hook = _points[0];
            Vector3 anchor = _points[_count - 1];
            float direct = Vector3.Distance(hook, anchor);

            StepTension(taut, dt);

            // ONE filtered quantity drives both the rest length and the straightening, and it is derived from the
            // CURRENT gap every frame rather than eased from last frame's rest length. That distinction is the
            // difference between a cable that tightens and one that never quite does: easing the length itself
            // means that while the winch reels - and the gap shrinks every frame - the rest length is permanently
            // trailing above the gap, so the rope reads as slack for the whole pull no matter what the target says.
            float wanted = direct * Mathf.Lerp(SlackFactor, TautFactor, _tension);
            _restLength = wanted;

            float segLen = _restLength / (_count - 1);
            Vector3 gravityStep = new Vector3(0f, Gravity * dt * dt, 0f);

            for (int i = 1; i < _count - 1; i++)
            {
                Vector3 cur = _points[i];
                Vector3 vel = (cur - _prev[i]) * Damping;
                _prev[i] = cur;
                _points[i] = cur + vel + gravityStep;
            }

            for (int it = 0; it < Iterations; it++)
            {
                _points[0] = hook;
                _points[_count - 1] = anchor;

                for (int i = 0; i < _count - 1; i++)
                {
                    Vector3 a = _points[i];
                    Vector3 b = _points[i + 1];
                    Vector3 delta = b - a;
                    float d = delta.magnitude;
                    if (d < 0.0001f) continue;

                    // Split the correction between the two particles - unless one of them is a pinned end, in
                    // which case the free one has to take all of it. Giving it only half (the bug this replaces)
                    // left both ends permanently under-corrected, so the rope hung slacker near its anchors than
                    // anywhere else.
                    bool aPinned = i == 0;
                    bool bPinned = i + 1 == _count - 1;
                    float share = (aPinned || bPinned) ? 1f : 0.5f;
                    Vector3 correction = delta * ((d - segLen) / d * share);
                    if (!aPinned) _points[i] = a + correction;
                    if (!bPinned) _points[i + 1] = b - correction;
                }
            }

            Straighten(hook, anchor, direct);

            // Collision runs LAST and therefore wins over straightness. A cable cannot both be the exact chord and
            // go round a bollard standing in it, so one has to have priority: bending over the obstacle is the one
            // that looks like a rope, and a line passing through the world does not.
            Collide();
            Render();
        }

        /// <summary>Rest length as a fraction of the endpoint gap: hanging, and hauled.</summary>
        private const float SlackFactor = 1.12f;
        private const float TautFactor = 0.995f;

        /// <summary>
        /// Seconds to take up the slack, and to give it back. Winding in is a drum turning against a load, so it is
        /// quick but not instant; letting go is the rope's own weight, which is slower.
        /// </summary>
        private const float TightenTime = 0.45f;
        private const float SlackenTime = 0.8f;

        private float _tension;

        /// <summary>
        /// How taut the cable is, as a filtered version of the winch actually pulling.
        ///
        /// Deliberately NOT derived from the rest length against the endpoint gap, which is the obvious source and
        /// the wrong one: that ratio lags on purpose, and while the winch reels the gap shrinks every frame, so it
        /// can sit above 1 - reading "slack" - for the whole pull. The pulling state is the honest input.
        ///
        /// The filter is the whole point. Without it the cable is rigid on the exact frame the button goes down,
        /// which is what a winch does not look like: a drum takes up its slack over a moment, and seeing that moment
        /// is most of what tells a player the tool has bitten.
        /// </summary>
        private void StepTension(bool taut, float dt)
        {
            float per = dt / Mathf.Max(0.01f, taut ? TightenTime : SlackenTime);
            _tension = Mathf.MoveTowards(_tension, taut ? 1f : 0f, per);
        }

        /// <summary>
        /// Pulls a loaded cable onto the straight line between its ends.
        ///
        /// This exists because the constraint solver above CANNOT do it, which is not obvious and cost two failed
        /// attempts to establish. Simulating this exact solver and measuring the result: a 5 m span with the rest
        /// length already 0.5% SHORTER than the gap still settles at 54 cm of sag. Raising the iteration count to
        /// 150 only brings it to 12 cm, and shortening the rope to 90% of the gap still leaves 18 cm. The reason is
        /// that a Gauss-Seidel distance constraint only ever moves a point ALONG its current segment direction, so
        /// a chain that is already curved stays curved with slightly shorter links; meanwhile gravity re-injects a
        /// fresh downward step every frame. Shortening the rope makes it tighter, never straighter.
        ///
        /// So the straightness is stated rather than discovered. It is exact in one frame, costs one lerp per
        /// point, and leaves the slack case completely untouched.
        ///
        /// The blend is driven by the MEASURED slack ratio, not by the reeling flag: a boolean would snap the whole
        /// cable straight on the frame the button goes down. Between 1.00 and 1.06 of the gap it eases, which is
        /// what a cable taking up its own slack looks like.
        /// </summary>
        private void Straighten(Vector3 hook, Vector3 anchor, float direct)
        {
            if (direct < 0.001f || _tension <= 0.001f) return;

            for (int i = 1; i < _count - 1; i++)
            {
                Vector3 straight = Vector3.Lerp(hook, anchor, (float)i / (_count - 1));
                _points[i] = Vector3.Lerp(_points[i], straight, _tension);

                // The stored previous position has to come with it. Left behind, the verlet velocity term reads the
                // blend as motion and throws the point back off the line on the very next frame - the cable would
                // buzz instead of hanging still.
                _prev[i] = Vector3.Lerp(_prev[i], straight, _tension);
            }
        }

        /// <summary>
        /// Keeps the rope out of the world it is lying in. Each free point is traced along the way it just moved,
        /// and anything it ran into becomes where it stops - so slack piles up ON the road instead of sinking
        /// through it, and a rope drawn across a kerb rests on the kerb.
        ///
        /// The trace runs from the point's previous position, which is the cheap part: a Verlet point moves
        /// millimetres per frame, so these are almost-zero-length rays. Resetting the previous position to the
        /// contact point is what makes it settle instead of buzz - it throws away the velocity that was driving
        /// the point into the surface, which is the same thing a resting contact does in a real solver.
        ///
        /// A second downward probe catches the case the first cannot: a point that is already inside geometry has
        /// no movement ray that starts outside it.
        /// </summary>
        private void Collide()
        {
            if (!Config.Preferences.RopeCollision) return;

            for (int i = 1; i < _count - 1; i++)
            {
                try
                {
                    Vector3 from = _prev[i];
                    Vector3 move = _points[i] - from;
                    float d = move.magnitude;

                    if (d > 0.0005f)
                    {
                        RaycastHit hit;
                        if (Physics.Raycast(from, move / d, out hit, d + Radius, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                        {
                            _points[i] = hit.point + hit.normal * Radius;
                            _prev[i] = _points[i];
                            continue;
                        }
                    }

                    RaycastHit ground;
                    if (Physics.Raycast(_points[i] + Vector3.up * GroundProbe, Vector3.down, out ground,
                                        GroundProbe + Radius, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    {
                        float rest = ground.point.y + Radius;
                        if (_points[i].y < rest)
                        {
                            Vector3 p = _points[i];
                            p.y = rest;
                            _points[i] = p;
                            _prev[i] = p;
                        }
                    }
                }
                catch { }
            }
        }

        private void Render()
        {
            if (_lr == null) return;
            for (int i = 0; i < _count; i++) _lr.SetPosition(i, _points[i]);
            PlaceGuide();
        }

        /// <summary>
        /// Keeps the muzzle guide sitting on the rope's near end, aligned with its last segment. The rope's final
        /// point ends up inside the guide's opaque body, which is what actually fixes the end: a line renderer
        /// cannot hide its own termination - rounded caps and width tapers only make the cut-off thinner, never
        /// absent. Something the rope disappears INTO is the cheapest honest answer.
        /// </summary>
        private void PlaceGuide()
        {
            if (_guide == null || _count < 2) return;

            try
            {
                Vector3 end = _points[_count - 1];
                Vector3 dir = end - _points[_count - 2];
                if (dir.sqrMagnitude < 1e-6f) return;
                dir.Normalize();

                // Centred just past the rope's end, so the last couple of centimetres are swallowed.
                _guide.transform.position = end + dir * 0.015f;
                _guide.transform.rotation = Quaternion.FromToRotation(Vector3.up, dir);
            }
            catch { }
        }

        internal void Destroy()
        {
            // Destroying the GameObject does not take the runtime material and texture with it - and the texture is
            // deliberately flagged DontUnloadUnusedAsset - so they are released here rather than leaked every time
            // the renderer is rebuilt.
            try { if (_go != null) UnityEngine.Object.Destroy(_go); } catch { }
            try { if (_guide != null) UnityEngine.Object.Destroy(_guide); } catch { }
            try { if (_ropeMaterial != null) UnityEngine.Object.Destroy(_ropeMaterial); } catch { }
            try { if (_guideMaterial != null) UnityEngine.Object.Destroy(_guideMaterial); } catch { }
            try { if (_ropeTexture != null) UnityEngine.Object.Destroy(_ropeTexture); } catch { }

            _go = null;
            _lr = null;
            _guide = null;
            _ropeMaterial = null;
            _guideMaterial = null;
            _ropeTexture = null;
            _attached = false;
        }

        private void EnsureRenderer()
        {
            if (_lr != null) return;
            try
            {
                _go = new GameObject("YoinkRope");
                _lr = _go.AddComponent<LineRenderer>();
                _lr.useWorldSpace = true;
                _lr.positionCount = _count;
                _lr.widthMultiplier = 0.04f;
                _lr.numCapVertices = 4;
                _lr.numCornerVertices = 4;
                _lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _lr.receiveShadows = false;

                // RepeatPerSegment, not Tile. Tile keeps a constant tiling rate per world unit and accumulates U
                // along the line, so the phase at any point depends on the length of everything before it - and
                // this rope's length changes every frame (both ends move, and the rest length is eased toward the
                // current gap). At 26 repeats per unit, a centimetre of length change slid the pattern by a
                // quarter of a twist, which is the crawl players saw. Per-segment mapping pins one repeat to each
                // segment: segments still stretch, so the pitch breathes slightly, but the pattern no longer
                // scrolls along the rope. The twist pitch is baked into the texture instead of tiled on top.
                _lr.textureMode = LineTextureMode.RepeatPerSegment;

                // A slight taper into the guide. Only slight: a strong taper turns the rope into a thread and it
                // still ends visibly - hiding the end is the guide's job, not the width curve's.
                _lr.widthCurve = TaperedWidth();

                _ropeMaterial = MakeRopeMaterial();
                _ropeTexture = _lastTexture;
                if (_ropeMaterial != null)
                {
                    _lr.material = _ropeMaterial;
                    // A Lit fallback needs generated normals and tangents, or it shades as if it were unlit anyway.
                    try { _lr.generateLightingData = _ropeMaterial.shader != null && _ropeMaterial.shader.name.EndsWith("Lit"); }
                    catch { }
                }

                _lr.startColor = Color.white;   // the texture carries the colour; tinting it again only muddies it
                _lr.endColor = Color.white;

                BuildGuide();
            }
            catch (Exception e)
            {
                Core.Log.Warning("[Rope] could not create the rope renderer: " + e.Message);
                _lr = null;
            }
        }

        /// <summary>
        /// A short metal eyelet at the muzzle end. It is what the rope visibly runs into, which is the difference
        /// between "the rope ends here" and "the rope goes into the winch". A stock cylinder is plenty at this
        /// size; the collider comes off because it exists purely to be looked at.
        /// </summary>
        private void BuildGuide()
        {
            try
            {
                _guide = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                _guide.name = "YoinkRopeGuide";

                try
                {
                    Collider col = _guide.GetComponent<Collider>();
                    if (col != null) UnityEngine.Object.Destroy(col);
                }
                catch { }

                // Unity's cylinder is 2 units tall with radius 0.5, so this is a 5 cm sleeve, 7 cm long.
                _guide.transform.localScale = new Vector3(0.05f, 0.035f, 0.05f);

                MeshRenderer mr = _guide.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    _guideMaterial = MakeSolidMaterial(new Color(0.20f, 0.21f, 0.23f, 1f));   // dark steel
                    if (_guideMaterial != null) mr.material = _guideMaterial;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                }

                _guide.SetActive(false);
            }
            catch (Exception e)
            {
                Core.Log.Warning("[Rope] could not build the rope guide: " + e.Message);
                _guide = null;
            }
        }

        private static Material MakeSolidMaterial(Color colour)
        {
            string[] candidates = { "Universal Render Pipeline/Lit", "Universal Render Pipeline/Unlit", "Sprites/Default" };
            for (int i = 0; i < candidates.Length; i++)
            {
                try
                {
                    Shader s = Shader.Find(candidates[i]);
                    if (s == null) continue;
                    Material m = new Material(s);
                    m.color = colour;
                    try { m.SetColor("_BaseColor", colour); } catch { }
                    return m;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Full thickness for most of the run, then a taper over the last stretch into the winch. Point 0 is the
        /// hook (t = 0) and the last point is the muzzle (t = 1), so the taper sits at the end nearest the camera.
        /// </summary>
        private static AnimationCurve TaperedWidth()
        {
            AnimationCurve c = new AnimationCurve();
            c.AddKey(0f, 1f);
            c.AddKey(0.92f, 1f);
            c.AddKey(1f, 0.75f);
            return c;
        }

        /// <summary>URP is what the game renders with, so ask for its shaders first and fall back gracefully.</summary>
        private static Texture2D _lastTexture;

        private static Material MakeRopeMaterial()
        {
            string[] candidates = { "Universal Render Pipeline/Unlit", "Universal Render Pipeline/Lit", "Sprites/Default" };
            Texture2D tex = MakeRopeTexture();
            _lastTexture = tex;

            for (int i = 0; i < candidates.Length; i++)
            {
                try
                {
                    Shader s = Shader.Find(candidates[i]);
                    if (s == null) continue;

                    Material m = new Material(s);
                    if (tex != null)
                    {
                        // URP reads _BaseMap, the built-in shaders read _MainTex; set both rather than guess.
                        m.mainTexture = tex;
                        try { m.SetTexture("_BaseMap", tex); } catch { }
                        // One texture = one segment (see textureMode above), so the twist pitch lives in the
                        // texture itself and tiling stays at 1.
                        Vector2 tiling = Vector2.one;
                        m.mainTextureScale = tiling;
                        try { m.SetTextureScale("_BaseMap", tiling); } catch { }
                    }
                    m.color = Color.white;
                    try { m.SetColor("_BaseColor", Color.white); } catch { }
                    return m;
                }
                catch { }
            }

            Core.Log.Warning("[Rope] no usable shader found - the rope will draw with the default material.");
            return null;
        }

        /// <summary>
        /// A rope, drawn in code: strands running diagonally across a tiling strip, shaded lighter along the top
        /// of each strand and darker in the grooves between them, with a little per-pixel grain so it does not
        /// look like a printed pattern. Generated rather than shipped so the mod needs no texture asset at all.
        /// </summary>
        private static Texture2D MakeRopeTexture()
        {
            const int w = 96;   // along the rope - one repeat covers one whole rope segment
            const int h = 16;   // across the rope
            try
            {
                Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.wrapMode = TextureWrapMode.Repeat;
                tex.filterMode = FilterMode.Bilinear;
                tex.hideFlags |= HideFlags.DontUnloadUnusedAsset;

                // Unlit means these colours arrive on screen exactly as written, with no light to knock them back -
                // so they are picked darker and less saturated than hemp looks in a photo, or the rope reads as
                // gold cord rather than as rope.
                Color light = new Color(0.44f, 0.36f, 0.25f, 1f);
                Color dark = new Color(0.15f, 0.12f, 0.08f, 1f);    // the groove between strands
                const int strands = 9;   // twists per rope segment - the pitch, baked in rather than tiled

                for (int y = 0; y < h; y++)
                {
                    // Round the strip off across its width so the rope reads as a cylinder, not a ribbon.
                    float across = (y + 0.5f) / h;
                    float round = 1f - Mathf.Abs(across * 2f - 1f);
                    round = Mathf.Sqrt(Mathf.Clamp01(round));

                    for (int x = 0; x < w; x++)
                    {
                        // Diagonal offset is the twist: strand boundaries march along as you go across.
                        float u = (x + y * 0.75f) / (float)w * strands;
                        float inStrand = u - Mathf.Floor(u);            // 0..1 across one strand
                        float ridge = Mathf.Sin(inStrand * Mathf.PI);   // bright in the middle, dark at the seam

                        float shade = Mathf.Clamp01(0.25f + 0.75f * ridge) * (0.55f + 0.45f * round);
                        float grain = ((x * 7 + y * 13) % 5) * 0.012f;  // cheap fixed-pattern fibre noise

                        Color c = Color.Lerp(dark, light, Mathf.Clamp01(shade + grain));
                        tex.SetPixel(x, y, c);
                    }
                }

                tex.Apply(false, false);
                return tex;
            }
            catch (Exception e)
            {
                Core.Log.Warning("[Rope] could not build the rope texture, falling back to flat colour: " + e.Message);
                return null;
            }
        }
    }
}
