using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace UmamusumeCompanion
{
    // On the Uma face atlas the whites and eyelids share a material. Draw the whites
    // after opaque geometry, then draw the iris only on the pixels they actually covered.
    static class EyeSurface
    {
        static Shader shader;
        static bool triedShader;

        static Shader LoadShader()
        {
            if (triedShader) return shader;
            triedShader = true;
            using (var stream = typeof(EyeSurface).Assembly.GetManifestResourceStream("eyes.unity3d"))
            {
                if (stream == null) return null;
                byte[] bytes;
                using (var reader = new BinaryReader(stream)) bytes = reader.ReadBytes((int)stream.Length);
                var bundle = AssetBundle.LoadFromMemory(bytes);
                if (bundle == null) return null;
                try { shader = bundle.LoadAsset<Shader>("Assets/shaders/EyeSurface.shader"); }
                finally { bundle.Unload(false); }
            }
            return shader;
        }

        static bool InWhiteAtlas(Vector2 uv)
        {
            return uv.x >= 0f && uv.x <= 0.18f && uv.y >= 0.84f && uv.y <= 1f;
        }

        public static Material Apply(CharPack pack, Mesh mesh, ref Material[] materials)
        {
            int face = pack.SubMeshes.FindIndex(s => s.Material.StartsWith("mtl_chr") && s.Material.EndsWith("_face.001"));
            int eye = pack.SubMeshes.FindIndex(s => s.Material.StartsWith("mtl_chr") && s.Material.EndsWith("_eye"));
            if (face < 0 || eye < 0) return null;
            var whites = new List<int>();
            var skin = new List<int>();
            var indices = pack.SubMeshes[face].Indices;
            for (int i = 0; i < indices.Length; i += 3)
            {
                int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                var dest = InWhiteAtlas(pack.UVs[a]) && InWhiteAtlas(pack.UVs[b]) && InWhiteAtlas(pack.UVs[c]) ? whites : skin;
                dest.Add(a); dest.Add(b); dest.Add(c);
            }
            if (whites.Count == 0 || skin.Count == 0 || LoadShader() == null || !shader.isSupported) return null;

            int slot = materials.Length;
            mesh.subMeshCount = slot + 1;
            mesh.SetTriangles(skin.ToArray(), face);
            mesh.SetTriangles(whites.ToArray(), slot);
            System.Array.Resize(ref materials, slot + 1);
            var white = new Material(materials[face]) { name = "eye_white", shader = shader, renderQueue = 2988 };
            materials[slot] = white;
            var iris = materials[eye];
            iris.shader = shader;
            iris.renderQueue = 2989;
            iris.SetInt("_StencilPass", (int)StencilOp.Keep);
            iris.SetInt("_DepthWrite", 0);
            SetStencil(iris, true);
            return iris;
        }

        // Some reflection cameras render to a 16-bit depth buffer without stencil.
        public static void SetStencil(Material iris, bool enabled)
        {
            iris.SetInt("_StencilTest", (int)(enabled ? CompareFunction.Equal : CompareFunction.Always));
            iris.SetInt("_DepthTest", (int)(enabled ? CompareFunction.Always : CompareFunction.LessEqual));
        }
    }
}
