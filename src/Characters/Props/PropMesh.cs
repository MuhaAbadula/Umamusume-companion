using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Static props from the pack's props\ folder (task 26 reader; today the souvenir_*.obj + souvenirs.png of task 46/47,
    /// and RoadSpots.PlaceModel for any future roadside prop).
    /// Small OBJ reader: v / vt / vn / f, one submesh, polygons fanned into triangles. OBJ is right-handed, so x is
    /// negated and each triangle's order reversed (what Unity's own OBJ import does). Meshes, texture and material are
    /// loaded once and cached; a missing or broken file returns null (the caller falls back or skips).
    /// </summary>
    public static class PropMesh
    {
        /// <summary>models\&lt;current model&gt;\props folder of the deployed pack (set by UmamusumeCompanion from the Model dropdown).</summary>
        public static string Dir;

        static readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();
        static readonly HashSet<string> failed = new HashSet<string>();
        static readonly Dictionary<string, Material> mats = new Dictionary<string, Material>();
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public static Mesh Get(string name)
        {
            Mesh m;
            string path = Path.Combine(Dir ?? "", name + ".obj");
            if (meshes.TryGetValue(path, out m)) return m;
            if (failed.Contains(path)) return null;
            try
            {
                if (string.IsNullOrEmpty(Dir) || !File.Exists(path)) throw new FileNotFoundException("no " + path);
                m = Read(path, name);
                meshes[path] = m;
                return m;
            }
            catch (Exception e)
            {
                failed.Add(path);
                Debug.LogWarning("[Umamusume companion] PropMesh: " + name + " not loaded: " + e.Message);
                return null;
            }
        }

        /// <summary>Opaque Legacy Diffuse (no emission) with any atlas png of the props folder (souvenirs.png). Cached per file; null if missing.</summary>
        public static Material Material(string png)
        {
            Material m;
            string path = Path.Combine(Dir ?? "", png);
            if (mats.TryGetValue(path, out m)) return m;
            try
            {
                if (string.IsNullOrEmpty(Dir) || !File.Exists(path)) throw new FileNotFoundException("no " + path);
                var tex = new Texture2D(2, 2, TextureFormat.RGB24, true) { name = "Fuku_" + Path.GetFileNameWithoutExtension(png) };
                if (!tex.LoadImage(File.ReadAllBytes(path))) throw new Exception("bad png " + path);
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.anisoLevel = 2;
                var sh = Shader.Find("Legacy Shaders/Diffuse");
                if (sh == null) throw new Exception("Legacy Shaders/Diffuse not found");
                m = new Material(sh) { name = tex.name, mainTexture = tex };
                // a bit under white: the limewash in full sun should read as paint, not as a lamp
                if (m.HasProperty("_Color")) m.SetColor("_Color", new Color(0.8f, 0.8f, 0.8f, 1f));
            }
            catch (Exception e)
            {
                m = null;
                Debug.LogWarning("[Umamusume companion] PropMesh: material " + png + " not loaded: " + e.Message);
            }
            mats[path] = m;
            return m;
        }

        static float Fl(string s) { return float.Parse(s, NumberStyles.Float, IC); }

        static Mesh Read(string path, string name)
        {
            var v = new List<Vector3>();
            var vt = new List<Vector2>();
            var vn = new List<Vector3>();
            var pos = new List<Vector3>();
            var uv = new List<Vector2>();
            var nor = new List<Vector3>();
            var tris = new List<int>();
            var map = new Dictionary<string, int>();
            var sep = new[] { ' ', '\t' };
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length < 2 || line[0] == '#') continue;
                var p = line.Split(sep, StringSplitOptions.RemoveEmptyEntries);
                switch (p[0])
                {
                    case "v": v.Add(new Vector3(-Fl(p[1]), Fl(p[2]), Fl(p[3]))); break;
                    case "vt": vt.Add(new Vector2(Fl(p[1]), Fl(p[2]))); break;
                    case "vn": vn.Add(new Vector3(-Fl(p[1]), Fl(p[2]), Fl(p[3]))); break;
                    case "f":
                        var idx = new int[p.Length - 1];
                        for (int i = 1; i < p.Length; i++)
                        {
                            int k;
                            if (!map.TryGetValue(p[i], out k))
                            {
                                var c = p[i].Split('/');
                                int a = Ix(c[0], v.Count);
                                int b = c.Length > 1 && c[1].Length > 0 ? Ix(c[1], vt.Count) : -1;
                                int n = c.Length > 2 && c[2].Length > 0 ? Ix(c[2], vn.Count) : -1;
                                k = pos.Count;
                                pos.Add(v[a]);
                                uv.Add(b >= 0 ? vt[b] : Vector2.zero);
                                nor.Add(n >= 0 ? vn[n] : Vector3.up);
                                map[p[i]] = k;
                            }
                            idx[i - 1] = k;
                        }
                        // fan; reversed winding for the x flip
                        for (int i = 1; i + 1 < idx.Length; i++) { tris.Add(idx[0]); tris.Add(idx[i + 1]); tris.Add(idx[i]); }
                        break;
                }
            }
            if (tris.Count == 0) throw new Exception("no faces in " + path);
            if (pos.Count > 65000) throw new Exception("too many vertices (" + pos.Count + ")");
            var m = new Mesh { name = "Fuku_" + name };
            m.vertices = pos.ToArray();
            m.uv = uv.ToArray();
            m.normals = nor.ToArray();
            m.triangles = tris.ToArray();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>OBJ index (1-based, negative = from the end) to 0-based.</summary>
        static int Ix(string s, int count)
        {
            int i = int.Parse(s, IC);
            return i < 0 ? count + i : i - 1;
        }
    }
}
