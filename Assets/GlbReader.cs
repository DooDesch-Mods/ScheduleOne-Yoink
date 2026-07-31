using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Yoink.Assets
{
    /// <summary>
    /// A minimal GLB reader: turns a binary glTF file into a Unity GameObject with meshes and flat materials.
    ///
    /// This exists to avoid a dependency. The obvious route was S1MAPI's <c>GltfLoader</c>, which does this and
    /// much more - but S1MAPI is GPL-3.0 with no linking exception, and every other DooDesch mod ships MIT.
    /// Depending on it would have pulled the whole mod into copyleft over one model load.
    ///
    /// Deliberately NOT a general glTF implementation. It supports exactly what this mod's own asset uses and
    /// rejects everything else loudly rather than half-working:
    ///
    /// * a single binary buffer (the GLB's BIN chunk) - no external .bin, no data: URIs
    /// * POSITION and NORMAL attributes, and indices, as the usual float32 / uint16 / uint32
    /// * flat <c>baseColorFactor</c> and <c>emissiveFactor</c> materials - no textures, no samplers
    /// * per-vertex COLOR_0, split into one submesh per distinct colour (see <see cref="SplitByColour"/>)
    /// * static meshes only - no skins, no animation, no morph targets
    ///
    /// Format reference: a GLB is a 12-byte header (magic "glTF", version, total length) followed by chunks,
    /// each a 4-byte length + 4-byte type + payload. Chunk 0 is the JSON scene description, chunk 1 the binary
    /// buffer it indexes into.
    /// </summary>
    internal static class GlbReader
    {
        private const uint MagicGltf = 0x46546C67;   // "glTF" little-endian
        private const uint ChunkJson = 0x4E4F534A;   // "JSON"
        private const uint ChunkBin = 0x004E4942;    // "BIN\0"

        /// <summary>Parse a GLB and build a GameObject. Returns null and logs on any unsupported input.</summary>
        internal static GameObject Load(byte[] glb, string name = "GlbModel")
        {
            try
            {
                if (glb == null || glb.Length < 20) { Core.Log.Warning("[Glb] file is too small to be a GLB."); return null; }

                if (!TrySplitChunks(glb, out JObject json, out byte[] bin)) return null;

                var buffers = new List<byte[]> { bin };
                var accessors = json["accessors"] as JArray;
                var views = json["bufferViews"] as JArray;
                var meshes = json["meshes"] as JArray;

                if (accessors == null || views == null || meshes == null)
                {
                    Core.Log.Warning("[Glb] no meshes/accessors in the file.");
                    return null;
                }

                _textures = ReadTextures(json, bin);
                var materials = ReadMaterials(json["materials"] as JArray);

                var root = new GameObject(name);
                int built = 0;

                // Nodes carry the transforms. Walking them keeps the model's authored layout instead of piling
                // every mesh at the origin.
                var nodes = json["nodes"] as JArray;
                if (nodes != null)
                {
                    var scenes = json["scenes"] as JArray;
                    JArray roots = scenes != null && scenes.Count > 0 ? scenes[0]["nodes"] as JArray : null;

                    if (roots != null)
                        foreach (var idx in roots)
                            built += BuildNode(root.transform, nodes, (int)idx, meshes, accessors, views, buffers, materials);
                }

                // Some exporters emit meshes without a scene graph - fall back to flattening them.
                if (built == 0)
                    for (int m = 0; m < meshes.Count; m++)
                        built += BuildMesh(root.transform, meshes[m] as JObject, accessors, views, buffers, materials, "mesh" + m);

                if (built == 0)
                {
                    Core.Log.Warning("[Glb] nothing could be built from the file.");
                    UnityEngine.Object.Destroy(root);
                    return null;
                }

                Core.Log.Msg("[Glb] built " + built + " mesh part(s).");
                return root;
            }
            catch (Exception e)
            {
                Core.Log.Warning("[Glb] load failed: " + e.Message);
                return null;
            }
        }

        // ---- container ------------------------------------------------------------------------------------

        private static bool TrySplitChunks(byte[] glb, out JObject json, out byte[] bin)
        {
            json = null;
            bin = null;

            uint magic = BitConverter.ToUInt32(glb, 0);
            if (magic != MagicGltf) { Core.Log.Warning("[Glb] not a GLB file (bad magic)."); return false; }

            int offset = 12;   // magic + version + length
            while (offset + 8 <= glb.Length)
            {
                int length = (int)BitConverter.ToUInt32(glb, offset);
                uint type = BitConverter.ToUInt32(glb, offset + 4);
                int start = offset + 8;

                if (start + length > glb.Length) break;

                if (type == ChunkJson)
                {
                    json = JObject.Parse(Encoding.UTF8.GetString(glb, start, length));
                }
                else if (type == ChunkBin)
                {
                    bin = new byte[length];
                    Buffer.BlockCopy(glb, start, bin, 0, length);
                }

                // Chunks are padded to 4-byte boundaries.
                offset = start + length;
                if ((offset & 3) != 0) offset += 4 - (offset & 3);
            }

            if (json == null) { Core.Log.Warning("[Glb] no JSON chunk."); return false; }
            if (bin == null) { Core.Log.Warning("[Glb] no BIN chunk - external buffers are not supported."); return false; }
            return true;
        }

        // ---- scene graph ----------------------------------------------------------------------------------

        private static int BuildNode(Transform parent, JArray nodes, int index, JArray meshes,
                                     JArray accessors, JArray views, List<byte[]> buffers, List<Material> materials)
        {
            if (index < 0 || index >= nodes.Count) return 0;

            var node = nodes[index] as JObject;
            if (node == null) return 0;

            var go = new GameObject((string)node["name"] ?? ("node" + index));
            go.transform.SetParent(parent, false);
            ApplyTransform(go.transform, node);

            int built = 0;

            if (node["mesh"] != null)
                built += BuildMesh(go.transform, meshes[(int)node["mesh"]] as JObject, accessors, views, buffers, materials, go.name);

            var children = node["children"] as JArray;
            if (children != null)
                foreach (var c in children)
                    built += BuildNode(go.transform, nodes, (int)c, meshes, accessors, views, buffers, materials);

            return built;
        }

        private static void ApplyTransform(Transform t, JObject node)
        {
            // glTF is right-handed +Y up, Unity is left-handed +Y up: negating Z on positions and flipping the
            // triangle winding converts between them. The same flip is applied to translations here.
            var translation = node["translation"] as JArray;
            if (translation != null && translation.Count == 3)
                t.localPosition = new Vector3((float)translation[0], (float)translation[1], -(float)translation[2]);

            var rotation = node["rotation"] as JArray;
            if (rotation != null && rotation.Count == 4)
                t.localRotation = new Quaternion(-(float)rotation[0], -(float)rotation[1], (float)rotation[2], (float)rotation[3]);

            var scale = node["scale"] as JArray;
            if (scale != null && scale.Count == 3)
                t.localScale = new Vector3((float)scale[0], (float)scale[1], (float)scale[2]);
        }

        // ---- geometry -------------------------------------------------------------------------------------

        private static int BuildMesh(Transform parent, JObject mesh, JArray accessors, JArray views,
                                     List<byte[]> buffers, List<Material> materials, string name)
        {
            if (mesh == null) return 0;

            var primitives = mesh["primitives"] as JArray;
            if (primitives == null) return 0;

            int built = 0;

            for (int p = 0; p < primitives.Count; p++)
            {
                var prim = primitives[p] as JObject;
                var attributes = prim?["attributes"] as JObject;
                if (attributes?["POSITION"] == null) continue;

                Vector3[] positions = ReadVector3(accessors, views, buffers, (int)attributes["POSITION"], flipZ: true);
                if (positions == null || positions.Length == 0) continue;

                Vector3[] normals = attributes["NORMAL"] != null
                    ? ReadVector3(accessors, views, buffers, (int)attributes["NORMAL"], flipZ: true)
                    : null;

                int[] indices = prim["indices"] != null
                    ? ReadIndices(accessors, views, buffers, (int)prim["indices"])
                    : Sequential(positions.Length);

                if (indices == null || indices.Length < 3) continue;

                Color[] colours = attributes["COLOR_0"] != null
                    ? ReadColours(accessors, views, buffers, (int)attributes["COLOR_0"])
                    : null;

                // A textured model paints itself through its UVs, so the colour split is not just unnecessary
                // there - it would shatter one textured mesh into a dozen submeshes for nothing.
                int materialIndex = prim["material"] != null ? (int)prim["material"] : -1;
                bool textured = materialIndex >= 0 && materialIndex < materials.Count
                                && materials[materialIndex] != null && materials[materialIndex].mainTexture != null;
                if (textured) colours = null;

                Vector2[] uvs = textured && attributes["TEXCOORD_0"] != null
                    ? ReadVector2(accessors, views, buffers, (int)attributes["TEXCOORD_0"])
                    : null;

                // Negating Z mirrors the mesh, which reverses face winding - swap two corners of every triangle
                // back so the surfaces face outwards again.
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    int tmp = indices[i + 1];
                    indices[i + 1] = indices[i + 2];
                    indices[i + 2] = tmp;
                }

                var unityMesh = new Mesh { name = name + "_" + p };
                unityMesh.SetVertices(ToIl2Cpp(positions));
                if (normals != null && normals.Length == positions.Length) unityMesh.SetNormals(ToIl2Cpp(normals));
                if (normals == null || normals.Length != positions.Length) unityMesh.RecalculateNormals();
                if (uvs != null && uvs.Length == positions.Length) unityMesh.SetUVs(0, ToIl2Cpp(uvs));

                var go = new GameObject(name + "_" + p);
                go.transform.SetParent(parent, false);
                go.AddComponent<MeshFilter>().mesh = unityMesh;
                var renderer = go.AddComponent<MeshRenderer>();

                // A model that carries its colour per vertex needs either a vertex-colour shader - which URP/Lit is
                // not - or one material per colour. Splitting is the better trade here: the model is flat-shaded
                // with a handful of solid colours, so this reproduces the intended look with stock shaders and no
                // shader authoring. Models without COLOR_0 keep the plain single-material path.
                Material[] split = colours != null && colours.Length == positions.Length
                    ? SplitByColour(unityMesh, indices, colours)
                    : null;

                if (split != null && split.Length > 0)
                {
                    renderer.materials = ToIl2CppMaterials(split);
                }
                else
                {
                    unityMesh.SetTriangles(ToIl2CppInt(indices), 0);
                    renderer.material = materialIndex >= 0 && materialIndex < materials.Count
                        ? materials[materialIndex]
                        : FallbackMaterial();
                }

                unityMesh.RecalculateBounds();
                built++;
            }

            return built;
        }

        /// <summary>
        /// Groups triangles by the colour of their first corner and writes one submesh per colour, returning the
        /// matching materials. Flat-shaded geometry shares a colour across all three corners of a face, so the
        /// first corner is the face colour; colours are quantised before comparing so exporter round-off does not
        /// shatter one colour into a dozen near-identical materials.
        /// </summary>
        private static Material[] SplitByColour(Mesh mesh, int[] indices, Color[] colours)
        {
            try
            {
                var order = new List<int>();                       // packed colour keys, in first-seen order
                var groups = new Dictionary<int, List<int>>();     // key -> triangle corner indices

                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    int key = Pack(colours[indices[i]]);

                    List<int> tris;
                    if (!groups.TryGetValue(key, out tris))
                    {
                        tris = new List<int>();
                        groups[key] = tris;
                        order.Add(key);
                    }

                    tris.Add(indices[i]);
                    tris.Add(indices[i + 1]);
                    tris.Add(indices[i + 2]);
                }

                if (order.Count == 0) return null;

                mesh.subMeshCount = order.Count;
                var mats = new Material[order.Count];

                for (int s = 0; s < order.Count; s++)
                {
                    mesh.SetTriangles(ToIl2CppInt(groups[order[s]].ToArray()), s);
                    Material mat = NewMaterial("GlbColour" + s);
                    SetColor(mat, Unpack(order[s]));
                    mats[s] = mat;
                }

                return mats;
            }
            catch (Exception e)
            {
                Core.Log.Warning("[Glb] colour split failed, falling back to one material: " + e.Message);
                return null;
            }
        }

        /// <summary>Quantises a colour to 5 bits per channel - fine enough to keep every authored shade apart.</summary>
        private static int Pack(Color c)
        {
            int r = Mathf.Clamp(Mathf.RoundToInt(c.r * 31f), 0, 31);
            int g = Mathf.Clamp(Mathf.RoundToInt(c.g * 31f), 0, 31);
            int b = Mathf.Clamp(Mathf.RoundToInt(c.b * 31f), 0, 31);
            return (r << 10) | (g << 5) | b;
        }

        private static Color Unpack(int key)
        {
            return new Color(((key >> 10) & 31) / 31f, ((key >> 5) & 31) / 31f, (key & 31) / 31f, 1f);
        }

        /// <summary>Reads COLOR_0. glTF allows float or normalised integer, and three or four components.</summary>
        private static Color[] ReadColours(JArray accessors, JArray views, List<byte[]> buffers, int index)
        {
            var accessor = accessors[index] as JObject;
            if (accessor == null) return null;

            string type = (string)accessor["type"];
            int components = type == "VEC4" ? 4 : type == "VEC3" ? 3 : 0;
            if (components == 0) return null;

            int componentType = (int)accessor["componentType"];
            int size = componentType == 5126 ? 4 : componentType == 5123 ? 2 : componentType == 5121 ? 1 : 0;
            if (size == 0) { Core.Log.Warning("[Glb] unsupported COLOR_0 component type " + componentType + "."); return null; }

            int count = (int)accessor["count"];
            byte[] data;
            int start, stride;
            if (!TryResolve(accessor, views, buffers, out data, out start, out stride, components * size)) return null;

            var result = new Color[count];
            var v = new float[4];
            for (int i = 0; i < count; i++)
            {
                int o = start + i * stride;
                v[0] = 0f; v[1] = 0f; v[2] = 0f; v[3] = 1f;
                for (int c = 0; c < components; c++)
                {
                    int co = o + c * size;
                    v[c] = size == 4 ? BitConverter.ToSingle(data, co)
                         : size == 2 ? BitConverter.ToUInt16(data, co) / 65535f
                         : data[co] / 255f;
                }
                result[i] = new Color(v[0], v[1], v[2], v[3]);
            }
            return result;
        }

        private static int[] Sequential(int count)
        {
            var a = new int[count];
            for (int i = 0; i < count; i++) a[i] = i;
            return a;
        }

        // ---- accessors ------------------------------------------------------------------------------------

        private static Vector3[] ReadVector3(JArray accessors, JArray views, List<byte[]> buffers, int index, bool flipZ)
        {
            var accessor = accessors[index] as JObject;
            if (accessor == null) return null;
            if ((string)accessor["type"] != "VEC3") return null;
            if ((int)accessor["componentType"] != 5126) return null;   // FLOAT

            int count = (int)accessor["count"];
            if (!TryResolve(accessor, views, buffers, out byte[] data, out int start, out int stride, 12)) return null;

            var result = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                int o = start + i * stride;
                float x = BitConverter.ToSingle(data, o);
                float y = BitConverter.ToSingle(data, o + 4);
                float z = BitConverter.ToSingle(data, o + 8);
                result[i] = new Vector3(x, y, flipZ ? -z : z);
            }
            return result;
        }

        private static Vector2[] ReadVector2(JArray accessors, JArray views, List<byte[]> buffers, int index)
        {
            var accessor = accessors[index] as JObject;
            if (accessor == null) return null;
            if ((string)accessor["type"] != "VEC2") return null;
            if ((int)accessor["componentType"] != 5126) return null;   // FLOAT

            int count = (int)accessor["count"];
            byte[] data;
            int start, stride;
            if (!TryResolve(accessor, views, buffers, out data, out start, out stride, 8)) return null;

            var result = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                int o = start + i * stride;
                // glTF puts UV origin top-left, Unity bottom-left.
                result[i] = new Vector2(BitConverter.ToSingle(data, o), 1f - BitConverter.ToSingle(data, o + 4));
            }
            return result;
        }

        private static int[] ReadIndices(JArray accessors, JArray views, List<byte[]> buffers, int index)
        {
            var accessor = accessors[index] as JObject;
            if (accessor == null) return null;

            int componentType = (int)accessor["componentType"];
            int size = componentType == 5125 ? 4 : componentType == 5123 ? 2 : componentType == 5121 ? 1 : 0;
            if (size == 0) { Core.Log.Warning("[Glb] unsupported index type " + componentType + "."); return null; }

            int count = (int)accessor["count"];
            if (!TryResolve(accessor, views, buffers, out byte[] data, out int start, out int stride, size)) return null;

            var result = new int[count];
            for (int i = 0; i < count; i++)
            {
                int o = start + i * stride;
                result[i] = size == 4 ? (int)BitConverter.ToUInt32(data, o)
                          : size == 2 ? BitConverter.ToUInt16(data, o)
                          : data[o];
            }
            return result;
        }

        private static bool TryResolve(JObject accessor, JArray views, List<byte[]> buffers,
                                       out byte[] data, out int start, out int stride, int elementSize)
        {
            data = null; start = 0; stride = elementSize;

            if (accessor["bufferView"] == null) return false;   // sparse/zero-filled accessors are not supported
            var view = views[(int)accessor["bufferView"]] as JObject;
            if (view == null) return false;

            int buffer = view["buffer"] != null ? (int)view["buffer"] : 0;
            if (buffer < 0 || buffer >= buffers.Count) return false;

            data = buffers[buffer];
            int viewOffset = view["byteOffset"] != null ? (int)view["byteOffset"] : 0;
            int accessorOffset = accessor["byteOffset"] != null ? (int)accessor["byteOffset"] : 0;
            start = viewOffset + accessorOffset;

            // A byteStride means the data is interleaved with other attributes.
            if (view["byteStride"] != null) stride = (int)view["byteStride"];
            return true;
        }

        // ---- materials ------------------------------------------------------------------------------------

        private static List<Texture2D> _textures = new List<Texture2D>();

        /// <summary>
        /// Decodes the images the file carries. Only embedded ones: a GLB that references an external PNG has
        /// already lost the argument for shipping as a single file, which is the whole reason the model is
        /// embedded in the DLL.
        /// </summary>
        private static List<Texture2D> ReadTextures(JObject json, byte[] bin)
        {
            var result = new List<Texture2D>();

            try
            {
                var images = json["images"] as JArray;
                var views = json["bufferViews"] as JArray;
                if (images == null || views == null) return result;

                for (int i = 0; i < images.Count; i++)
                {
                    var image = images[i] as JObject;
                    if (image == null || image["bufferView"] == null) { result.Add(null); continue; }

                    var view = views[(int)image["bufferView"]] as JObject;
                    if (view == null) { result.Add(null); continue; }

                    int offset = view["byteOffset"] != null ? (int)view["byteOffset"] : 0;
                    int length = (int)view["byteLength"];

                    var bytes = new byte[length];
                    Buffer.BlockCopy(bin, offset, bytes, 0, length);

                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                    if (!ImageConversion.LoadImage(tex, (Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>)bytes, false))
                    {
                        result.Add(null);
                        continue;
                    }

                    // Point filtering: this is a hand-drawn low-poly atlas held 30 cm from the camera, and
                    // bilinear smearing of a hard-edged texture is what makes stylised art look muddy.
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    tex.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                    result.Add(tex);
                }
            }
            catch (Exception e) { Core.Log.Warning("[Glb] texture decode failed: " + e.Message); }

            return result;
        }

        /// <summary>The texture a material paints with, or null when it is a flat-colour material.</summary>
        private static Texture2D TextureFor(JObject material)
        {
            try
            {
                var pbr = material?["pbrMetallicRoughness"] as JObject;
                var baseColor = pbr?["baseColorTexture"] as JObject;
                if (baseColor == null || baseColor["index"] == null) return null;

                int index = (int)baseColor["index"];
                return index >= 0 && index < _textures.Count ? _textures[index] : null;
            }
            catch { return null; }
        }

        private static List<Material> ReadMaterials(JArray materials)
        {
            var result = new List<Material>();
            if (materials == null) return result;

            for (int i = 0; i < materials.Count; i++)
            {
                var m = materials[i] as JObject;
                var mat = NewMaterial((string)m?["name"] ?? ("material" + i));

                Color baseColor = Color.white;
                var pbr = m?["pbrMetallicRoughness"] as JObject;
                var factor = pbr?["baseColorFactor"] as JArray;
                if (factor != null && factor.Count >= 3)
                    baseColor = new Color((float)factor[0], (float)factor[1], (float)factor[2],
                                          factor.Count > 3 ? (float)factor[3] : 1f);

                SetColor(mat, baseColor);

                // A textured material paints itself. The base colour factor stays as a tint, so it must be white
                // here or the texture comes out darkened by it.
                Texture2D tex = TextureFor(m);
                if (tex != null)
                {
                    mat.mainTexture = tex;
                    try { mat.SetTexture("_BaseMap", tex); } catch { }
                    SetColor(mat, Color.white);
                }

                // Emissive parts (the light strip) get their glow carried over, otherwise they read as flat
                // grey panels rather than as lights.
                var emissive = m?["emissiveFactor"] as JArray;
                if (emissive != null && emissive.Count >= 3)
                {
                    var e = new Color((float)emissive[0], (float)emissive[1], (float)emissive[2], 1f);
                    if (e.r > 0.001f || e.g > 0.001f || e.b > 0.001f)
                    {
                        try
                        {
                            mat.EnableKeyword("_EMISSION");
                            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", e);
                        }
                        catch { }
                    }
                }

                result.Add(mat);
            }

            return result;
        }

        private static Material _fallback;

        private static Material FallbackMaterial()
        {
            if (_fallback == null) { _fallback = NewMaterial("GlbFallback"); SetColor(_fallback, Color.magenta); }
            return _fallback;
        }

        /// <summary>
        /// A material on the game's own render pipeline. Shader.Find on "Universal Render Pipeline/Lit" is the
        /// URP name the game uses; falling back to Unity's default keeps a model visible (untinted) rather than
        /// invisible if that ever changes.
        /// </summary>
        private static Material NewMaterial(string name)
        {
            Shader shader = null;
            try { shader = Shader.Find("Universal Render Pipeline/Lit"); } catch { }
            if (shader == null) { try { shader = Shader.Find("Standard"); } catch { } }

            var mat = shader != null ? new Material(shader) : new Material(Shader.Find("Sprites/Default"));
            mat.name = name;
            mat.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            return mat;
        }

        private static void SetColor(Material mat, Color c)
        {
            try
            {
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
                mat.color = c;
            }
            catch { }
        }

        // ---- il2cpp interop -------------------------------------------------------------------------------

        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Material> ToIl2CppMaterials(Material[] values)
        {
            var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Material>(values.Length);
            for (int i = 0; i < values.Length; i++) arr[i] = values[i];
            return arr;
        }

        private static Il2CppSystem.Collections.Generic.List<Vector2> ToIl2Cpp(Vector2[] values)
        {
            var list = new Il2CppSystem.Collections.Generic.List<Vector2>(values.Length);
            for (int i = 0; i < values.Length; i++) list.Add(values[i]);
            return list;
        }

        private static Il2CppSystem.Collections.Generic.List<Vector3> ToIl2Cpp(Vector3[] values)
        {
            var list = new Il2CppSystem.Collections.Generic.List<Vector3>(values.Length);
            for (int i = 0; i < values.Length; i++) list.Add(values[i]);
            return list;
        }

        private static Il2CppSystem.Collections.Generic.List<int> ToIl2CppInt(int[] values)
        {
            var list = new Il2CppSystem.Collections.Generic.List<int>(values.Length);
            for (int i = 0; i < values.Length; i++) list.Add(values[i]);
            return list;
        }
    }
}
