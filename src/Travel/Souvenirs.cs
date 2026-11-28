using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 47 (concept 09, recon 45): souvenirs on the dashboard. In each of the 6 countries (1 Germany ... 6 Turkey) she puts one
    /// keepsake of her own on her half of the dash, one row left to right in country order. Feature gate `souvenirs` (info.txt).
    /// When: a shop in country K (ShopC.PlayerTriggerEnter, K = StoreC.countryCode + 1) or the motel morning in town K (routeLevel K,
    /// not Turkey: the Istanbul morning is her story) marks K pending; she places it at the next boarding (Life: LifeSouvenirs.cs).
    /// Only heading east. After a load, missing countries 1..routeLevel (max 5) are caught up at the first boarding.
    /// State: ModSaves\UmamusumeCompanion\souvenirs.txt, a line per pack folder `fukukitaru=1:3,2:5` (country:daysPassed at placement); an older
    /// save shows only entries with day &lt;= daysPassed. New game (MainMenuClickersC.EraseSaveData) and "return home"
    /// (MainMenuC.SaveAndReturnHome) clear every pack. Meshes: props\souv_&lt;code&gt;.obj + souvenirs.png (PropMesh), grey boxes until then.
    /// Log: Souvenir: &lt;pack&gt; &lt;item&gt; &lt;country&gt; placed|click|reset|skip|pending ...
    /// </summary>
    public static class Souvenirs
    {
        public static bool Enabled = true;
        /// <summary>Pack folder name (set by UmamusumeCompanion from the Model dropdown).</summary>
        public static string Pack = "?";

        public static readonly string[] Codes = { "ger", "csfr", "hun", "yugo", "bul", "turk" };
        static readonly string[] Countries = { "Germany", "CSFR", "Hungary", "Yugoslavia", "Bulgaria", "Turkey" };

        // recon 45: car space (cab.json: x right, y up, z forward, game units), FrameHolder scale 2.2
        static readonly float[] SpotX = { 0.52f, 0.71f, 0.90f, 1.09f, 1.28f, 1.46f };
        static readonly float[] SpotY = { -1.011f, -1.012f, -1.029f, -1.055f, -1.081f, -1.106f };
        const float SpotZ = 2.13f;
        static readonly Vector3 FlatN = new Vector3(0.005f, 1f, 0f), RollN = new Vector3(0.135f, 0.991f, 0f);
        static readonly Vector3 EyeCar = new Vector3(-0.918f, -0.389f, 1.160f);
        public const float FrameScale = 2.2f;
        /// <summary>Game units per metre at the dash (2.61 u = 1 m of her, recon 45).</summary>
        public const float UPerM = 2.61f;

        public static bool Here { get { return Enabled && CharFeatures.Has("souvenirs"); } }

        public static string Item(int k) { return "souv_" + Codes[k - 1]; }
        public static string Country(int k) { return Countries[k - 1]; }
        public static void Log(string s) { Debug.Log("[Umamusume companion] Souvenir: " + s); }
        public static void Log(int k, string what) { Log(Pack + " " + (k >= 1 && k <= 6 ? Item(k) + " " + Country(k) : "- -") + " " + what); }

        // ------------------------------------------------------------ state of this pack
        static readonly int[] day = { -1, -1, -1, -1, -1, -1 };   // daysPassed at placement, -1 = none
        static bool loaded;
        static int pendingMask;                // countries waiting for her (bit k-1)
        /// <summary>A pending one waits for him to get out and back in (shop, morning, catch-up). Debug: no.</summary>
        public static bool NeedBoard;
        public static bool SawOut;
        public static bool Forced;              // debug: no speed / boarding rule

        public static void SelectPack(string pack)
        {
            if (Pack == pack) return;
            Pack = pack;
            pendingMask = 0;
            Forced = NeedBoard = SawOut = false;
            loaded = false;
            Row.DestroyItems();
            if (Row.Live != null) Row.Live.CaughtUp = false;
        }

        static string StatePath
        {
            get { return Path.Combine(CompanionStorage.SaveDirectory, "souvenirs.txt"); }
        }

        static Dictionary<string, string> ReadAll()
        {
            var all = new Dictionary<string, string>();
            try
            {
                if (File.Exists(StatePath))
                    foreach (var raw in File.ReadAllLines(StatePath))
                    {
                        string line = raw.Trim();
                        int eq = line.IndexOf('=');
                        if (line.Length == 0 || line[0] == '#' || eq <= 0) continue;
                        all[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
            }
            catch (Exception e) { Debug.LogWarning("[Umamusume companion] Souvenir: state not read: " + e.Message); }
            return all;
        }

        static void WriteAll(Dictionary<string, string> all)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                var lines = new List<string> { "# UmamusumeCompanion souvenirs: pack=country:daysPassed,... (1 Germany ... 6 Turkey)" };
                foreach (var kv in all) if (!string.IsNullOrEmpty(kv.Value)) lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(StatePath, lines.ToArray());
            }
            catch (Exception e) { Debug.LogWarning("[Umamusume companion] Souvenir: state not saved: " + e.Message); }
        }

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            for (int i = 0; i < 6; i++) day[i] = -1;
            string v;
            if (!ReadAll().TryGetValue(Pack, out v)) return;
            foreach (var part in v.Split(','))
            {
                var kd = part.Split(':');
                int k, d;
                if (kd.Length == 2 && int.TryParse(kd[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out k)
                    && int.TryParse(kd[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out d) && k >= 1 && k <= 6)
                    day[k - 1] = Mathf.Max(0, d);
            }
        }

        static void Save()
        {
            var all = ReadAll();
            var parts = new List<string>();
            for (int k = 1; k <= 6; k++) if (day[k - 1] >= 0) parts.Add(k + ":" + day[k - 1]);
            all[Pack] = string.Join(",", parts.ToArray());
            WriteAll(all);
        }

        /// <summary>The game's daysPassed, -1 until the save is in (MainMenuC.LateStart loads it ~2 s after the scene).</summary>
        public static int GameDay()
        {
            if (Time.timeSinceLevelLoad < 2f) return -1;
            if (director == null)
            {
                if (Time.realtimeSinceStartup < findAt) return -1;
                findAt = Time.realtimeSinceStartup + 1f;
                director = UnityEngine.Object.FindObjectOfType<DirectorC>();
                if (director == null) return -1;
            }
            return director.daysPassed;
        }
        static DirectorC director;
        static float findAt;

        static RouteGeneratorC Route()
        {
            var go = GameObject.FindWithTag("Director");
            return go != null ? go.GetComponent<RouteGeneratorC>() : null;
        }

        /// <summary>Country k is on the dash in this save (placed on a day not after today).</summary>
        public static bool Has(int k)
        {
            Load();
            int d = day[k - 1], now = GameDay();
            return d >= 0 && (now < 0 || d <= now);
        }

        public static int Count { get { int n = 0; for (int k = 1; k <= 6; k++) if (Has(k)) n++; return n; } }

        /// <summary>Lowest country waiting for her, 0 = none.</summary>
        public static int NextPending()
        {
            for (int k = 1; k <= 6; k++) if ((pendingMask & (1 << (k - 1))) != 0) { if (Has(k)) pendingMask &= ~(1 << (k - 1)); else return k; }
            return 0;
        }

        /// <summary>Country k is due: she places it at the next boarding.</summary>
        static void Mark(int k, string why, bool board)
        {
            if (!Here || k < 1 || k > 6 || Has(k) || (pendingMask & (1 << (k - 1))) != 0) return;
            pendingMask |= 1 << (k - 1);
            if (board) { NeedBoard = true; SawOut = false; }
            Log(k, "pending (" + why + ")");
        }

        /// <summary>From the beat: the item is on the dash now.</summary>
        public static void Placed(int k)
        {
            Load();
            int d = GameDay();
            day[k - 1] = Mathf.Max(0, d);
            pendingMask &= ~(1 << (k - 1));
            if (pendingMask == 0) { Forced = false; NeedBoard = false; }
            Save();
            Log(k, "placed (day " + day[k - 1] + ", " + Count + " on the dash)");
        }

        /// <summary>Nothing on the dash, nothing pending (this pack, in memory; the caller saves).</summary>
        public static void ClearAll(string why)
        {
            for (int i = 0; i < 6; i++) day[i] = -1;
            pendingMask = 0;
            Forced = NeedBoard = false;
            loaded = true;
            Row.DestroyItems();
        }

        // ------------------------------------------------------------ game hooks
        static bool applied;

        /// <summary>From SettingsDeclaration (the main menu): the new-game hook must be on before the menu's New Game.</summary>
        public static void Apply()
        {
            if (applied) return;
            applied = true;
            var h = new Harmony("osobl.jalopy.umamusumecompanion.souvenirs");
            var erase = AccessTools.Method(typeof(MainMenuClickersC), "EraseSaveData");
            if (erase != null) h.Patch(erase, postfix: new HarmonyMethod(typeof(Souvenirs), nameof(NewGame)));
            else Debug.LogWarning("[Umamusume companion] Souvenir: MainMenuClickersC.EraseSaveData not found, a new game keeps them");
            var home = AccessTools.Method(typeof(MainMenuC), "SaveAndReturnHome");
            if (home != null) h.Patch(home, prefix: new HarmonyMethod(typeof(Souvenirs), nameof(ReturnHome)));
            var shop = AccessTools.Method(typeof(ShopC), "PlayerTriggerEnter");
            if (shop != null) h.Patch(shop, postfix: new HarmonyMethod(typeof(Souvenirs), nameof(ShopEnter)));
            Debug.Log("[Umamusume companion] Souvenir: patches on (new game " + (erase != null) + ", return home " + (home != null) + ", shop " + (shop != null) + ")");
        }

        static void Reset(string why)
        {
            var all = ReadAll();
            if (all.Count > 0) WriteAll(new Dictionary<string, string>());
            ClearAll(why);
            Log(Pack + " - - reset (" + why + ", every pack)");
        }

        static void NewGame() { try { Trust.Reset("new game"); Reset("new game"); } catch (Exception e) { Debug.LogWarning("[Umamusume companion] Souvenir: reset failed: " + e.Message); } }
        static void ReturnHome() { try { Reset("return home"); } catch (Exception e) { Debug.LogWarning("[Umamusume companion] Souvenir: reset failed: " + e.Message); } }

        static void ShopEnter(ShopC __instance)
        {
            try
            {
                if (!Here || __instance == null) return;
                var st = __instance.GetComponentInParent<StoreC>();
                var rg = Route();
                if (st == null || rg == null) return;
                int k = st.countryCode + 1;
                if (rg.drivingTowardsHome) return;   // only on the way east
                Mark(k, "shop", true);
            }
            catch (Exception e) { Debug.LogWarning("[Umamusume companion] Souvenir: shop hook: " + e.Message); }
        }

        /// <summary>From Morning.WakePrefix (a real motel wake): the morning in town routeLevel, east, never Turkey.</summary>
        public static void OnMorning()
        {
            try
            {
                var rg = Route();
                if (!Here || rg == null || rg.drivingTowardsHome) return;
                if (rg.routeLevel >= 1 && rg.routeLevel <= 5) Mark(rg.routeLevel, "morning", true);
            }
            catch (Exception e) { Debug.LogWarning("[Umamusume companion] Souvenir: morning hook: " + e.Message); }
        }

        /// <summary>Once per scene when the save's day is in: countries 1..routeLevel (max 5, east) not on the dash are due.</summary>
        public static void CatchUp()
        {
            var row = Row.Live;
            if (row == null || row.CaughtUp || GameDay() < 0) return;
            row.CaughtUp = true;
            var rg = Route();
            if (!Here || rg == null) return;
            Log(Pack + " - - load: day " + GameDay() + ", routeLevel " + rg.routeLevel + (rg.drivingTowardsHome ? " home" : " east") + ", " + Count + " on the dash");
            if (rg.drivingTowardsHome) return;
            for (int k = 1; k <= Mathf.Min(rg.routeLevel, 5); k++) Mark(k, "catch-up after load", true);
        }

        /// <summary>Debug "Souvenir now": the next one in order, now (no boarding, no speed rule).</summary>
        public static string DebugNow()
        {
            if (!Enabled) return "the Souvenirs toggle is off";
            if (!CharFeatures.Has("souvenirs")) return "not this character (features=" + CharFeatures.List + ")";
            int k = 0, waiting = 0;
            for (int i = 1; i <= 6; i++)
            {
                if (Has(i)) continue;
                if ((pendingMask & (1 << (i - 1))) != 0) waiting++;
                else if (k == 0) k = i;
            }
            if (k == 0) return waiting > 0 ? "the rest (" + waiting + ") already wait for her" : "all 6 are on the dash";
            Mark(k, "debug", false);
            Forced = true;
            return Item(k) + " " + Country(k) + ": she places it when free";
        }

        public static string DebugQueueAll()
        {
            if (!Here) return "Souvenirs are unavailable for this companion.";
            if (Count == 6) return "All 6 are already on the dash.";
            for (int k = 1; k <= 6; k++) Mark(k, "debug", false);
            Forced = true;
            return "Close the panel. She will place the remaining souvenirs in turn when seated and free.";
        }

        /// <summary>Debug "Remove souvenirs": this pack only, this save.</summary>
        public static string DebugRemove()
        {
            int n = Count;
            ClearAll("debug");
            Save();
            Log(Pack + " - - reset (debug, this pack)");
            return n + " removed (" + Pack + ")";
        }

        // ------------------------------------------------------------ the row on the dash
        /// <summary>
        /// The six spots under FrameHolder/TweenHolder/Frame/Dashboard of the player's car, and the items on them. Own damped
        /// sway (&lt;= 3 deg) from the car's acceleration; the hop from her fingertips to the spot (0.25 s).
        /// </summary>
        public class Row : MonoBehaviour
        {
            public static Row Live;
            public bool CaughtUp;   // the after-load check ran for this scene
            public Transform Frame, Dash;
            public readonly Transform[] Spots = new Transform[6];
            readonly Transform[] items = new Transform[6];
            readonly Bounds[] bounds = new Bounds[6];
            Rigidbody body;
            Vector3 lastVel, acc;
            Vector2 ang, angV;
            bool velInit;
            // hop
            int hopK;
            float hopT = -1f;
            Vector3 hopFrom;
            const float HopLen = 0.25f;

            public static Row Build(Transform carRoot)
            {
                if (Live != null) return Live;
                if (carRoot == null) return null;
                Transform frame = FindDeep(carRoot, "FrameHolder");
                if (frame == null) return null;
                Transform dash = frame.Find("TweenHolder/Frame/Dashboard");
                if (dash == null) dash = FindDeep(frame, "Dashboard");
                if (dash == null) return null;
                var go = new GameObject("FukuSouvenirs");
                go.transform.SetParent(dash, false);
                var r = go.AddComponent<Row>();
                r.Frame = frame;
                r.Dash = dash;
                r.body = dash.GetComponentInParent<Rigidbody>();
                Vector3 eye = r.CarToWorld(EyeCar);
                for (int i = 0; i < 6; i++)
                {
                    var s = new GameObject("Spot" + (i + 1)).transform;
                    s.SetParent(go.transform, false);
                    Vector3 p = r.CarToWorld(new Vector3(SpotX[i], SpotY[i], SpotZ));
                    Vector3 n = frame.TransformDirection(i < 2 ? FlatN : RollN).normalized;
                    Vector3 face = Vector3.ProjectOnPlane(eye - p, n);
                    if (face.sqrMagnitude < 1e-8f) face = Vector3.ProjectOnPlane(-frame.forward, n);
                    s.position = p;
                    s.rotation = Quaternion.LookRotation(face.normalized, n);   // +z of the item (OBJ front) to the driver
                    Vector3 ls = dash.lossyScale;
                    s.localScale = new Vector3(UPerM / Mathf.Max(1e-4f, Mathf.Abs(ls.x)), UPerM / Mathf.Max(1e-4f, Mathf.Abs(ls.y)),
                        UPerM / Mathf.Max(1e-4f, Mathf.Abs(ls.z)));   // spot space = metres
                    r.Spots[i] = s;
                }
                Live = r;
                Log(Pack + " - - row on " + Path(dash) + " (scale " + dash.lossyScale.x.ToString("F3", CultureInfo.InvariantCulture)
                    + "), spot 1 world " + V(r.Spots[0].position) + ", spot 6 " + V(r.Spots[5].position));
                return r;
            }

            public Vector3 CarToWorld(Vector3 car) { return Frame.TransformPoint(car / FrameScale); }
            public Vector3 WorldToCar(Vector3 w) { return Frame.InverseTransformPoint(w) * FrameScale; }

            static Transform FindDeep(Transform t, string name)
            {
                if (t.name == name) return t;
                for (int i = 0; i < t.childCount; i++)
                {
                    var f = FindDeep(t.GetChild(i), name);
                    if (f != null) return f;
                }
                return null;
            }

            static string Path(Transform t)
            {
                string p = t.name;
                for (var c = t.parent; c != null; c = c.parent) p = c.name + "/" + p;
                return p;
            }

            public static string V(Vector3 v)
            {
                return "(" + v.x.ToString("F3", CultureInfo.InvariantCulture) + ", " + v.y.ToString("F3", CultureInfo.InvariantCulture) + ", "
                    + v.z.ToString("F3", CultureInfo.InvariantCulture) + ")";
            }

            public Transform ItemT(int k) { return items[k - 1]; }

            /// <summary>Visible items = Has(k); built on demand, removed when gone (older save, reset).</summary>
            public void Sync()
            {
                if (GameDay() < 0) return;
                for (int k = 1; k <= 6; k++)
                {
                    bool want = Has(k) || (hopT >= 0f && hopK == k);
                    if (want && items[k - 1] == null) items[k - 1] = Make(k);
                    else if (!want && items[k - 1] != null && hopK != k) { Destroy(items[k - 1].gameObject); items[k - 1] = null; }
                }
            }

            public static void DestroyItems()
            {
                if (Live == null) return;
                for (int i = 0; i < 6; i++)
                    if (Live.items[i] != null) { Destroy(Live.items[i].gameObject); Live.items[i] = null; }
                Live.hopT = -1f;
                Live.hopK = 0;
            }

            /// <summary>The item at its spot (base on the surface), OBJ or a grey box.</summary>
            Transform Make(int k)
            {
                var root = new GameObject(Item(k)).transform;
                root.SetParent(Spots[k - 1], false);
                var vis = new GameObject("mesh");
                vis.transform.SetParent(root, false);
                vis.layer = root.gameObject.layer = Dash.gameObject.layer;
                Mesh m = PropMesh.Get(Item(k));
                Material mat = m != null ? PropMesh.Material("souvenirs.png") : null;
                if (m == null || mat == null)
                {
                    // grey box 5 x 6 x 5 cm, base on the spot
                    m = BoxMesh();
                    mat = GreyMat();
                }
                vis.AddComponent<MeshFilter>().sharedMesh = m;
                var mr = vis.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                Bounds b = m.bounds;
                b.Expand(0.02f);   // +1 cm each side for the click
                bounds[k - 1] = b;
                return root;
            }

            public Bounds BoundsOf(int k) { return bounds[k - 1]; }

            static Mesh box;
            static Material grey;

            static Mesh BoxMesh()
            {
                if (box != null) return box;
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var src = go.GetComponent<MeshFilter>().sharedMesh;
                var v = src.vertices;
                for (int i = 0; i < v.Length; i++) v[i] = new Vector3(v[i].x * 0.05f, (v[i].y + 0.5f) * 0.06f, v[i].z * 0.05f);
                box = new Mesh { name = "Fuku_souv_box", vertices = v, normals = src.normals, uv = src.uv, triangles = src.triangles };
                box.RecalculateBounds();
                Destroy(go);
                return box;
            }

            static Material GreyMat()
            {
                if (grey != null) return grey;
                var sh = Shader.Find("Legacy Shaders/Diffuse") ?? Shader.Find("Diffuse");
                grey = new Material(sh) { name = "Fuku_souv_grey" };
                if (grey.HasProperty("_Color")) grey.SetColor("_Color", new Color(0.55f, 0.55f, 0.55f, 1f));
                return grey;
            }

            /// <summary>Show item k at a world point (her fingertips) while it is still in her hand.</summary>
            public void Hold(int k, Vector3 world)
            {
                if (items[k - 1] == null) items[k - 1] = Make(k);
                hopK = k;
                hopT = -1f;
                items[k - 1].position = world;
            }

            /// <summary>The hop from the fingertips to the spot.</summary>
            public void Hop(int k, Vector3 from)
            {
                if (items[k - 1] == null) items[k - 1] = Make(k);
                hopK = k;
                hopT = 0f;
                hopFrom = Spots[k - 1].InverseTransformPoint(from);
            }

            /// <summary>Item k left in her hand without a hop (the beat was cut): gone again.</summary>
            public void Drop(int k)
            {
                if (hopK != k || hopT >= 0f) return;
                hopK = 0;
                if (!Has(k) && items[k - 1] != null) { Destroy(items[k - 1].gameObject); items[k - 1] = null; }
            }

            void Update()
            {
                bool on = Here;
                for (int i = 0; i < 6; i++) if (items[i] != null && items[i].gameObject.activeSelf != on) items[i].gameObject.SetActive(on);
                if (!on) return;
                float dt = Time.deltaTime;
                if (dt <= 0f) return;
                // sway: the car's acceleration in the dash frame, low-passed; a damped pendulum, at most 3 deg
                if (body != null)
                {
                    Vector3 v = body.velocity;
                    if (!velInit) { lastVel = v; velInit = true; }
                    Vector3 a = (v - lastVel) / dt;
                    lastVel = v;
                    acc = Vector3.Lerp(acc, a, Mathf.Clamp01(dt / 0.08f));
                }
                Vector3 al = Spots[0] != null ? Spots[0].InverseTransformDirection(acc) / UPerM : Vector3.zero;   // m/s^2 in the spot frame
                // braking (acc backwards) tips them forward: about 3 deg at 1 g
                Vector2 target = new Vector2(Mathf.Clamp(-al.z * 0.3f, -3f, 3f), Mathf.Clamp(al.x * 0.3f, -3f, 3f));
                const float w = 2f * Mathf.PI * 3f, z = 0.3f;
                angV += (w * w * (target - ang) - 2f * z * w * angV) * dt;
                ang += angV * dt;
                ang.x = Mathf.Clamp(ang.x, -3f, 3f);
                ang.y = Mathf.Clamp(ang.y, -3f, 3f);
                for (int k = 1; k <= 6; k++)
                {
                    var t = items[k - 1];
                    if (t == null) continue;
                    if (hopK == k && hopT < 0f) continue;   // in her hand: Hold sets it
                    if (hopK == k && hopT >= 0f)
                    {
                        hopT += dt;
                        float u = Mathf.Clamp01(hopT / HopLen);
                        float e = Mathf.SmoothStep(0f, 1f, u);
                        Vector3 p = Vector3.Lerp(hopFrom, Vector3.zero, e) + Vector3.up * (0.03f * Mathf.Sin(u * Mathf.PI));
                        t.localPosition = p;
                        t.localRotation = Quaternion.Slerp(Quaternion.Euler(0f, 0f, 20f), Quaternion.identity, e);
                        if (u >= 1f) { hopT = -1f; hopK = 0; }
                        continue;
                    }
                    t.localPosition = Vector3.zero;
                    // a little difference per item, so they don't move as one block
                    float f = 0.75f + 0.05f * k;
                    t.localRotation = Quaternion.Euler(ang.x * f, 0f, -ang.y * f);
                }
            }

            void OnDestroy() { if (Live == this) Live = null; }
        }
    }
}
