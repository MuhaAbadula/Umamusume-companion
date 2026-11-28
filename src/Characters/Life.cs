using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;
using Random = UnityEngine.Random;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Expressions built from the pack's face/ear shape keys by models/&lt;name&gt;/face.txt
    /// ("name = Group(Tag) weight, Group(Tag).L weight"). Channels fade in and out; Step() sums them into Acc.
    /// Concept 14 (mesh-swap faces, FaceSwap.cs): a term "Swap(part_k) weight" shows that variant of its part while
    /// the expression is stronger than any other asking for the part (SwapPick). Lines named blink / talk / closed /
    /// half are not expressions: the blink variant, the talk mouth variants, the variants that count as closed eyes
    /// (no blink on top, pupils still) and as half-closed lids.
    /// </summary>
    public class FaceRig
    {
        class Preset { public int[] Shapes; public float[] W; public bool[] Mouth; public string[] Group; public float EyeClose, EyeCover; public string[] Swaps = new string[0]; public float[] SwapW = new float[0]; }
        class Channel { public Preset P; public float Cur, Target, Fade = 0.35f, Extra; }

        static readonly Regex ShapeName = new Regex(@"^(EyeBrow|Eye|Mouth|Ear)_\d+_([LR0])\((\w+)\)\[");
        static readonly Regex Term = new Regex(@"^\s*(\w+)\((\w+)\)(?:\.([LR]))?\s+([\d.]+)\s*$");

        readonly Dictionary<string, Channel> channels = new Dictionary<string, Channel>();
        public readonly float[] Acc;
        public readonly List<int> Managed = new List<int>();
        readonly Dictionary<string, List<int>> capGroups = new Dictionary<string, List<int>>();
        public float EyeClosed { get; private set; }
        /// <summary>How much the lids cover the eyes (closed or half-closed expressions): the pupils turn less.</summary>
        public float EyeCovered { get; private set; }
        public int Count { get { return channels.Count; } }

        // mesh-swap faces (concept 14)
        public string SwapBlink;
        public string[] SwapTalk = new string[0];
        readonly HashSet<string> swapClosed = new HashSet<string>(), swapHalf = new HashSet<string>();
        /// <summary>After Step(): part -> variant of the strongest expression asking for it (weight > 0.5); absent = default.</summary>
        public readonly Dictionary<string, string> SwapPick = new Dictionary<string, string>();
        readonly Dictionary<string, float> swapBest = new Dictionary<string, float>();
        public bool HasSwaps { get; private set; }
        public bool IsClosedEye(string variant) { return variant != null && swapClosed.Contains(variant); }

        public FaceRig(Mesh mesh, string file)
        {
            Acc = new float[mesh.blendShapeCount];
            if (!File.Exists(file)) return;
            // (group, tag) -> shape indices with their side
            var index = new Dictionary<string, List<KeyValuePair<int, string>>>();
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                var m = ShapeName.Match(mesh.GetBlendShapeName(i));
                if (!m.Success) continue;
                string key = m.Groups[1].Value + "(" + m.Groups[3].Value + ")";
                List<KeyValuePair<int, string>> l;
                if (!index.TryGetValue(key, out l)) index[key] = l = new List<KeyValuePair<int, string>>();
                l.Add(new KeyValuePair<int, string>(i, m.Groups[2].Value));
            }
            var managed = new HashSet<int>();
            foreach (var raw in File.ReadAllLines(file))
            {
                string line = raw;
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash);
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string name = line.Substring(0, eq).Trim();
                var shapes = new List<int>();
                var ws = new List<float>();
                var mouth = new List<bool>();
                var groupL = new List<string>();
                var swaps = new List<string>();
                var swapW = new List<float>();
                float eyeClose = 0f, eyeCover = 0f;
                foreach (var t in line.Substring(eq + 1).Split(','))
                {
                    var m = Term.Match(t);
                    if (!m.Success) continue;
                    string group = m.Groups[1].Value, tag = m.Groups[2].Value, side = m.Groups[3].Value;
                    float w = float.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
                    if (group == "Swap") { swaps.Add(tag); swapW.Add(w / 100f); continue; }
                    List<KeyValuePair<int, string>> hits;
                    if (!index.TryGetValue(group + "(" + tag + ")", out hits))
                    {
                        Debug.LogWarning("[Umamusume companion] face.txt: no shape " + group + "(" + tag + ") for " + name);
                        continue;
                    }
                    foreach (var h in hits)
                    {
                        if (side.Length > 0 && h.Value != side) continue;
                        shapes.Add(h.Key);
                        ws.Add(w);
                        mouth.Add(group == "Mouth");
                        groupL.Add(group + "|" + h.Value);   // per side: L, R or 0
                        managed.Add(h.Key);
                    }
                    if (group == "Eye" && (tag.StartsWith("Warai") || tag.StartsWith("Close")))
                        eyeClose = Mathf.Max(eyeClose, w / 100f);
                    // half-closed lids (sleepy, dreamy, smug, annoyed) cover part of the eye: less room for the pupil
                    else if (group == "Eye" && (tag.StartsWith("Half") || tag.StartsWith("Jito")))
                        eyeCover = Mathf.Max(eyeCover, 0.6f * w / 100f);
                }
                if (swaps.Count > 0)
                {
                    HasSwaps = true;
                    if (name == "blink") { SwapBlink = swaps[0]; continue; }
                    if (name == "talk") { SwapTalk = swaps.ToArray(); continue; }
                    if (name == "closed") { foreach (var v in swaps) swapClosed.Add(v); continue; }
                    if (name == "half") { foreach (var v in swaps) swapHalf.Add(v); continue; }
                }
                if (shapes.Count > 0 || swaps.Count > 0)
                    channels[name] = new Channel
                    {
                        P = new Preset { Shapes = shapes.ToArray(), W = ws.ToArray(), Mouth = mouth.ToArray(), Group = groupL.ToArray(), EyeClose = eyeClose, EyeCover = Mathf.Max(eyeClose, eyeCover), Swaps = swaps.ToArray(), SwapW = swapW.ToArray() },
                    };
            }
            if (SwapBlink != null) swapClosed.Add(SwapBlink);
            foreach (var c in channels.Values)   // closed / half-closed eye variants act like the Eye(Close*) / Eye(Half*) shapes
                for (int i = 0; i < c.P.Swaps.Length; i++)
                {
                    if (swapClosed.Contains(c.P.Swaps[i])) c.P.EyeClose = Mathf.Max(c.P.EyeClose, c.P.SwapW[i]);
                    else if (swapHalf.Contains(c.P.Swaps[i])) c.P.EyeCover = Mathf.Max(c.P.EyeCover, 0.6f * c.P.SwapW[i]);
                    c.P.EyeCover = Mathf.Max(c.P.EyeCover, c.P.EyeClose);
                }
            Managed.AddRange(managed);
            foreach (var c in channels.Values)
                for (int i = 0; i < c.P.Shapes.Length; i++)
                {
                    string g = c.P.Group[i];
                    if (g.StartsWith("Ear|")) continue;   // ear flicks stack on purpose
                    List<int> l;
                    if (!capGroups.TryGetValue(g, out l)) capGroups[g] = l = new List<int>();
                    if (!l.Contains(c.P.Shapes[i])) l.Add(c.P.Shapes[i]);
                }
        }

        public bool Has(string name) { return channels.ContainsKey(name); }

        /// <summary>Every channel aims at 0 unless Want() is called for it this frame.</summary>
        public void BeginFrame()
        {
            foreach (var c in channels.Values) { c.Target = 0f; c.Fade = 0.6f; c.Extra = 0f; }
            soloName = null; soloW = 0f;
        }

        string soloName;
        float soloW;

        /// <summary>This frame the named expression owns the face: every other channel is scaled by (1 - w).</summary>
        public void Solo(string name, float w) { soloName = name; soloW = Mathf.Clamp01(w); }

        public void Want(string name, float weight, float fade = 0.25f)
        {
            Channel c;
            if (!channels.TryGetValue(name, out c) || weight < c.Target) return;
            c.Target = Mathf.Clamp01(weight);
            c.Fade = Mathf.Max(0.01f, fade);
        }

        /// <summary>Added on top of the faded channels this frame, no fading (ear flicks).</summary>
        public void Pulse(string name, float weight)
        {
            Channel c;
            if (channels.TryGetValue(name, out c)) c.Extra += weight;
        }

        /// <summary>Fades channels and fills Acc (0..100). Mouth terms are scaled while talking.</summary>
        public void Step(float dt, float mouthScale)
        {
            Array.Clear(Acc, 0, Acc.Length);
            float eye = 0f, cover = 0f;
            SwapPick.Clear();
            swapBest.Clear();
            foreach (var kv in channels)
            {
                var c = kv.Value;
                c.Cur = Mathf.MoveTowards(c.Cur, c.Target, dt / c.Fade);
                if (c.Cur <= 0f && c.Extra <= 0f) continue;
                float k = soloName != null && kv.Key != soloName ? 1f - soloW : 1f;
                var p = c.P;
                for (int i = 0; i < p.Shapes.Length; i++)
                    Acc[p.Shapes[i]] += (c.Cur * k + c.Extra) * p.W[i] * (p.Mouth[i] ? mouthScale : 1f);
                eye = Mathf.Max(eye, c.Cur * k * p.EyeClose);
                cover = Mathf.Max(cover, c.Cur * k * p.EyeCover);
                for (int i = 0; i < p.Swaps.Length; i++)
                {
                    float w = (c.Cur * k + c.Extra) * p.SwapW[i];
                    string part = FaceSwap.PartOf(p.Swaps[i]);
                    float best;
                    if (w <= 0.5f || (swapBest.TryGetValue(part, out best) && best >= w)) continue;
                    swapBest[part] = w;
                    SwapPick[part] = p.Swaps[i];
                }
            }
            // shapes of one group from several expressions (happy + smile + hum share WaraiA/WaraiB) summed past 100 %;
            // Unity extrapolates past 100, the brows sank into the closed lids (user screenshots).
            // Eye, EyeBrow and Mouth shapes together stay <= 100 per group and side
            foreach (var l in capGroups.Values)
            {
                float sum = 0f;
                foreach (int i in l) sum += Acc[i];
                if (sum > 100f) foreach (int i in l) Acc[i] *= 100f / sum;
            }
            EyeClosed = eye;
            EyeCovered = cover;
        }

        public void Clear()
        {
            foreach (var c in channels.Values) c.Cur = c.Target = 0f;
            Array.Clear(Acc, 0, Acc.Length);
            EyeClosed = EyeCovered = 0f;
        }
    }

    /// <summary>
    /// Makes the character feel alive on top of the uncle's animation: little things she does in the car on her own
    /// (looks out of the window, at the player, yawns, daydreams...), gaze with saccades, breathing, ear twitches,
    /// moods (radio, rain, night) and reactions (collisions). Rig-agnostic: it needs Chest/Neck/Head/Eye_L/Eye_R
    /// style bones and face.txt presets; anything missing is skipped.
    /// </summary>
    public partial class Life
    {
        public static Life Current;

        public FaceRig Face;
        public bool Enabled = true;
        public float Activity = 1f;   // 0.5 = calmer, 2 = busier
        public string Summary { get; private set; }

        /// <summary>Set by the driver: a gesture clip owns the head and arms at this weight; its face if any.</summary>
        /// <summary>Lids shut by the driver this frame (asleep, blinking), 0..1: eye bones don't turn under them.</summary>
        public float LidsShut;
        public float GestureWeight;
        public string GestureFace;
        /// <summary>Gesture clips she can pick in the car on her own; Play returns the clip length (0 = failed).</summary>
        public string[] GestureNames;
        public Func<string, float> PlayGesture;
        public Action StopGesture;
        public bool GestureForced;   // started with the debug key: not cut short
        string lastGesture;

        /// <summary>Idle tail sway for Springs (degrees per joint, Hz, degrees). Zero in the car (not visible there).</summary>
        public float TailAmp, TailHz = 0.45f, TailLift;
        float tailWagEnd = -1f, nextTailWag;

        class Bone
        {
            public Transform T;
            public Quaternion Rest;       // rest rotation in bind space
            public Quaternion RestLocal;  // rest local rotation (eyes are reset every frame)
            public Quaternion Deform() { return T.rotation * Quaternion.Inverse(Rest); }
        }

        Bone chest, neck, head;
        Eye eyeL, eyeR;

        class Eye
        {
            public Transform T;
            Quaternion restLocal;
            Vector3 restPosition, right, up;
            bool slides;

            public static Eye Bind(Bone bone, string side, List<CharPack.Shape> shapes)
            {
                if (bone == null) return null;
                var eye = new Eye { T = bone.T, restLocal = bone.RestLocal, restPosition = bone.T.localPosition };
                if (shapes == null) return eye;
                // Pack deltas are in bind space. The eye position is relative to its parent.
                Quaternion toParent = bone.RestLocal * Quaternion.Inverse(bone.Rest);
                foreach (var shape in shapes)
                {
                    if (!shape.Name.StartsWith("Eye_")) continue;
                    bool horizontal = shape.Name.Contains("_" + side + "(XRange)[");
                    if (!horizontal && !shape.Name.Contains("_" + side + "(YRange)[")) continue;
                    // These shapes translate the entire iris patch by a single vector.
                    foreach (var d in shape.Delta)
                    {
                        if (d.sqrMagnitude == 0f) continue;
                        if (horizontal) eye.right = toParent * (d * Mathf.Sign(d.x));
                        else eye.up = toParent * (d * Mathf.Sign(d.y));
                        break;
                    }
                }
                eye.slides = eye.right != Vector3.zero || eye.up != Vector3.zero;
                return eye;
            }

            public void Reset()
            {
                T.localRotation = restLocal;
                if (slides) T.localPosition = restPosition;
            }

            public void Aim(Vector2 slide, Quaternion rotation)
            {
                Reset();
                if (slides)
                    T.localPosition += right * slide.x + up * slide.y;
                else
                    T.rotation = rotation * T.rotation; // rigs with conventional rotating eyeballs
            }
        }

        /// <summary>Upper arm, forearm, hand: posed with two-bone IK (reaching for the radio/crank, elbow on the door).</summary>
        class Arm { public Transform Upper, Fore, Hand; }
        Arm armL, armR;

        static Arm FindArm(Dictionary<string, Transform> bones, string up, string fore, string hand)
        {
            Transform a, b, c;
            if (!bones.TryGetValue(up, out a) || !bones.TryGetValue(fore, out b) || !bones.TryGetValue(hand, out c)) return null;
            return new Arm { Upper = a, Fore = b, Hand = c };
        }
        readonly Transform uncle;
        readonly UncleLogicC logic;
        readonly Animator anim;

        public Life(Transform uncle, Mesh mesh, string faceFile, Dictionary<string, Transform> bones,
            Dictionary<string, Quaternion> rests) : this(uncle, mesh, faceFile, bones, rests, null) { }

        public Life(Transform uncle, Mesh mesh, string faceFile, Dictionary<string, Transform> bones,
            Dictionary<string, Quaternion> rests, List<CharPack.Shape> shapes)
        {
            this.uncle = uncle;
            logic = uncle.GetComponent<UncleLogicC>();
            anim = uncle.GetComponent<Animator>();
            Face = new FaceRig(mesh, faceFile);
            chest = Find(bones, rests, "Chest", "Spine2", "UpperChest");
            neck = Find(bones, rests, "Neck");
            head = Find(bones, rests, "Head");
            eyeL = Eye.Bind(Find(bones, rests, "Eye_L", "LeftEye", "Eye.L"), "L", shapes);
            eyeR = Eye.Bind(Find(bones, rests, "Eye_R", "RightEye", "Eye.R"), "R", shapes);
            armL = FindArm(bones, "Arm_L", "Elbow_L", "Wrist_L") ?? FindArm(bones, "LeftArm", "LeftForeArm", "LeftHand");
            armR = FindArm(bones, "Arm_R", "Elbow_R", "Wrist_R") ?? FindArm(bones, "RightArm", "RightForeArm", "RightHand");
            Summary = Face.Count + " expressions" + (eyeL != null ? ", eye bones" : "") + (head != null ? ", head look" : "");
            nextBeat = Time.time + Random.Range(4f, 8f);
            nextEar = Time.time + Random.Range(1f, 3f);
            nextTailWag = Time.time + Random.Range(5f, 12f);
            InitExtras(bones);
            if (CharFeatures.Has("boot_snack")) BindSnackGrip(bones);
            DozeInit(bones);
            MornInit(bones);
            Current = this;
            Doze.Phase = DozePhase.Awake;
        }

        static Bone Find(Dictionary<string, Transform> bones, Dictionary<string, Quaternion> rests, params string[] names)
        {
            foreach (var n in names)
            {
                Transform t;
                if (bones.TryGetValue(n, out t))
                    return new Bone { T = t, Rest = rests[n], RestLocal = t.localRotation };
            }
            return null;
        }

        // ------------------------------------------------------------ game state
        CarLogicC car;
        RouteGeneratorC route;
        object dayNight;
        FieldInfo hoursField;
        float nextWorldCheck;
        bool radioOn, raining, night, windowOpen;
        SmartAICar[] aiCars = new SmartAICar[0];
        float nextCarScan;
        WindowLogicC herWindow;
        RadioFreqLogicC radio;
        // her own will: she turns the radio on and opens/closes her window now and then (cooldowns in seconds of game time)
        float nextRadioOk, nextWindowOk, windowOpenedAt = -1f, radioOnAt = -1f;
        bool radioWasOn, sheTurnedRadio;
        /// <summary>Say a remark by key (set by the driver); after an action she checks how it went, then speaks.</summary>
        public Action<string> Say;
        string pendingAction;

        // what the driver and the car do: the player getting out and back, rough road, speed, stalling, horn
        RigidbodyControllerC playerCtl;
        bool playerSat = true, engineWas, rough, fast, fastFun;
        float playerLeftAt = -1f, fastFor;
        readonly Dictionary<string, float> sayCool = new Dictionary<string, float>();
        Look attnLook;
        float attnUntil = -1f, attnW;
        string emoteFace;
        float emoteUntil = -1f, emoteW;

        /// <summary>A remark with a chance and a cooldown per key, so she doesn't chatter.</summary>
        bool SayMaybe(string key, float chance, float cooldown)
        {
            if (Say == null) return false;
            float now = Time.time, until;
            if (sayCool.TryGetValue(key, out until) && now < until) return false;
            if (Random.value > chance * TrustTalkK) return false;
            sayCool[key] = now + cooldown;
            key = NaritaKey(key);
            Debug.Log("[Umamusume companion] remark: " + key);
            Say(key);
            return true;
        }

        void Attend(Look l, float dur, float w) { attnLook = l; attnUntil = Time.time + dur; attnW = w; }

        /// <summary>Task 52: does her remarks file have this key (set by the driver).</summary>
        public Func<string, bool> HasLine;

        /// <summary>Task 52: her line after a news piece (key news_&lt;id&gt;), normal remark rules. Returns what happened, for the News log.</summary>
        public string NewsLine(string key)
        {
            if (HasLine == null || !HasLine(key)) return "skip no line";
            if (!inCarNow) return "skip not in the car";
            if (asleepNow || Doze.Phase != DozePhase.Awake) return "skip asleep";
            if (Say == null) return "skip no remarks";
            if (CanSay != null && !CanSay()) return "skip busy";
            Debug.Log("[Umamusume companion] remark: " + key);
            Say(key);
            return "line";
        }
        void Emote(string face, float dur, float w) { emoteFace = face; emoteUntil = Time.time + dur; emoteW = w; }

        /// <summary>The driver honked (CarHornC.Trigger).</summary>
        public void OnHorn()
        {
            if (!Enabled || !inCarNow || asleepNow || Doze.Gate) return;
            if (Time.time - modHornAt < 0.25f) return;   // task 38 N2: her own press, not the driver's
            React("surprise", 0.6f, null, 0f);
            SayMaybe("horn", 0.35f, 45f);
        }

        /// <summary>The engine failed to start (IgnitionLogicC.IgnitionGoAndFail).</summary>
        public void OnEngineFail()
        {
            if (!Enabled || !inCarNow || asleepNow || Doze.Gate) return;
            Emote("worried", 2f, 0.8f);
            SayMaybe("engine_fail", 0.5f, 40f);
        }

        void DriverEvents(float now, bool inCar, bool asleep)
        {
            if (playerCtl == null && logic != null && logic.player != null) playerCtl = logic.player.GetComponent<RigidbodyControllerC>();
            IstanbulEvents(now, inCar, asleep);
            float speed = SpeedKmh();
            if (!inCar || asleep)
            {
                rough = fast = false;
                fastFor = 0f;
                if (playerCtl != null) playerSat = playerCtl.isSat;
                engineWas = car != null && car.engineOn;
                return;
            }

            // the player gets out / back in
            if (playerCtl != null)
            {
                bool sat = playerCtl.isSat;
                if (playerSat && !sat)
                {
                    playerLeftAt = now;
                    Attend(Look.Player, 4f, 0.8f);
                    Debug.Log("[Umamusume companion] event: player got out");
                    SayMaybe("player_leaves", 0.5f, 60f);
                }
                else if (!playerSat && sat && playerLeftAt >= 0f)
                {
                    float away = now - playerLeftAt;
                    Debug.Log("[Umamusume companion] event: player back after " + away.ToString("F0") + " s");
                    if (away > 15f)
                    {
                        Attend(Look.Player, 2.5f, 0.9f);
                        Emote("happy", 2.5f, 0.9f);
                        SayMaybe("player_back", away > 30f ? 1f : 0.6f, 20f); // a long wait: always a word
                    }
                    playerLeftAt = -1f;
                }
                playerSat = sat;
                // he's out of the car and close by: she keeps an eye on him
                var cam = Camera.main;
                if (!sat && cam != null && head != null && Vector3.Distance(cam.transform.position, head.T.position) < 10f)
                    Attend(Look.Player, 0.3f, 1f);
            }

            // engine dies while rolling
            bool engine = car != null && car.engineOn;
            if (engineWas && !engine && speed > 10f)
            {
                React("surprise", 0.8f, "worried", 2f);
                SayMaybe("stall", 0.7f, 60f);
            }
            engineWas = engine;

            // dirt or grass at speed
            bool r = car != null && (car.ridingThroughDirt || car.ridingThroughGrass) && speed > 15f;
            if (r && !rough) SayMaybe("rough_road", 0.5f, 150f);
            rough = r;
            if (rough) Face.Want("worried", 0.6f, 0.3f);

            // fast: about 80 km/h for 2 s, she either loves it or not (decided each time)
            if (speed > 80f) fastFor += Time.deltaTime; else if (speed < 65f) { fastFor = 0f; fast = false; }
            if (!fast && fastFor > 2f)
            {
                fast = true;
                fastFun = Random.value < 0.6f;
                SayMaybe(fastFun ? "fast_fun" : "fast_scared", 0.7f, 240f);
            }
            // speed: only a remark, no face change (user)
        }
        float pendingAt = -1f;
        bool pendingWasOpen, pendingRain;
        bool hadWindow;
        float nextWindLog;
        Rigidbody carBody;
        CarControleScriptC carCtl;

        /// <summary>The game's own speedometer value (km/h, from the wheel rpm); the car Rigidbody read far too low.</summary>
        float SpeedKmh()
        {
            if (carCtl == null) carCtl = UnityEngine.Object.FindObjectOfType<CarControleScriptC>();
            return carCtl != null ? Mathf.Abs(carCtl.currentSpeed) : 0f;
        }

        void ReadWorld()
        {
            if (Time.time < nextWorldCheck) return;
            nextWorldCheck = Time.time + 0.5f;
            try
            {
                if (car == null && logic != null && logic.carLogic != null) car = logic.carLogic.GetComponent<CarLogicC>();
                if (route == null && logic != null && logic.director != null) route = logic.director.GetComponent<RouteGeneratorC>();
                radioOn = car != null && car.radioOn;
                if (herWindow == null)
                    foreach (var w in UnityEngine.Object.FindObjectsOfType<WindowLogicC>())
                        if (w.isUncleWindow) { herWindow = w; break; }
                bool wasOpen = windowOpen;
                windowOpen = herWindow != null && herWindow.isOpen;
                if (windowOpen != wasOpen || (herWindow != null) != hadWindow)
                {
                    hadWindow = herWindow != null;
                    Debug.Log("[Umamusume companion] wind: her window " + (herWindow != null ? herWindow.name + (windowOpen ? " open" : " shut") : "not found"));
                }
                if (carBody == null && car != null)
                {
                    carBody = car.GetComponent<Rigidbody>();
                    if (carBody == null) carBody = car.GetComponentInParent<Rigidbody>();
                }
                if (radio == null) radio = UnityEngine.Object.FindObjectOfType<RadioFreqLogicC>();
                if (windowOpen && windowOpenedAt < 0f) windowOpenedAt = Time.time;
                if (!windowOpen) windowOpenedAt = -1f;
                // the player switched off the radio she had just put on: a sulky face
                if (radioWasOn && !radioOn && sheTurnedRadio && Time.time - radioOnAt < 90f)
                {
                    React("annoyed", 2f, null, 0f);
                    if (Say != null) Say("radio_off_by_player");
                }
                if (!radioOn) sheTurnedRadio = false;
                radioWasOn = radioOn;
                if (Time.time >= nextCarScan)
                {
                    nextCarScan = Time.time + 2f;
                    aiCars = ScanAICars();   // task 34: by tag (FindObjectsOfType hitches)
                }
                raining = route != null && route.currentWeatherCondition > 1;
                if (dayNight == null)
                {
                    var type = FindType("DNC_DayNight");
                    if (type != null)
                    {
                        dayNight = UnityEngine.Object.FindObjectOfType(type);
                        hoursField = type.GetField("timeInHours");
                    }
                }
                if (dayNight != null && hoursField != null)
                {
                    float h = Convert.ToSingle(hoursField.GetValue(dayNight));
                    hourNow = h;
                    night = h >= 22f || h < 5f;
                }
            }
            catch (Exception) { /* world state is optional */ }
        }

        static Type FindType(string name)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = a.GetType(name, false);
                if (t != null) return t;
            }
            return null;
        }

        // ------------------------------------------------------------ beats: small things she does on her own
        enum Look { Road, Window, Player, Dash, Up, Target, Radio, Crank, Spot, Reach }

        class Beat
        {
            public string Name, Face;
            public float Dur, FaceW, Roll, Pitch, LookW, T;
            public float ActAt = -1f;      // seconds into the beat when she flips the switch
            public bool Acted;
            public bool WaitReach;         // task 37: ActAt waits for her hand (ReachDone), at most 1 s
            public Look Look;
        }

        Beat beat;
        string lastBeat;
        float nextBeat;

        Beat PickBeat()
        {
            // name, weight
            var options = new List<KeyValuePair<string, float>>
            {
                new KeyValuePair<string, float>("window", 3f),
                new KeyValuePair<string, float>("player", 2f),
                new KeyValuePair<string, float>("road", 2f),
                new KeyValuePair<string, float>("dash", 1f),
                new KeyValuePair<string, float>("dreamy", 1.5f),
                new KeyValuePair<string, float>("smug", 1f),
                new KeyValuePair<string, float>("yawn", night ? 3f : 0.6f),
            };
            var bored = RandomGestures;   // task 34: prank_* clips play only from a prank
            if (bored != null && bored.Length > 0 && PlayGesture != null)
                options.Add(new KeyValuePair<string, float>("gesture", Luck.Happy ? 1.6f : 0.8f)); // body clips: rarer than glances (user)
            if (Luck.Happy) { options.Add(new KeyValuePair<string, float>("player", 1.5f)); options.Add(new KeyValuePair<string, float>("smug", 1f)); }
            if (Luck.Bad) options.Add(new KeyValuePair<string, float>("road", Luck.Nervous ? 6f : 2f));   // bad luck: eyes on the road
            if (Luck.Nervous) options.Add(new KeyValuePair<string, float>("window", 3f));
            if (raining) options.Add(new KeyValuePair<string, float>("window", 2f));
            if (windowOpen) options.Add(new KeyValuePair<string, float>("window", 3f));
            if (windowOpen && armL != null && armR != null) options.Add(new KeyValuePair<string, float>("lean_window", 2f));
            float now0 = Time.time;
            bool selfBlock = SelfActBlocked() || PrankBusy;   // task 16: just boarded; task 34: a prank is on
            if (!selfBlock && CanRadio() && now0 >= nextRadioOk) options.Add(new KeyValuePair<string, float>("radio_on", 1.2f));
            if (!selfBlock && RadioTrickOk(now0)) options.Add(new KeyValuePair<string, float>("radio_trick", 1.2f));   // task 37: Sweep only
            if (!selfBlock && herWindow != null && !windowOpen && !raining && now0 >= nextWindowOk) options.Add(new KeyValuePair<string, float>("window_open", 1.2f));
            if (!selfBlock && herWindow != null && windowOpen && now0 >= nextWindowOk
                && (night || (windowOpenedAt >= 0f && now0 - windowOpenedAt > 240f)))
                options.Add(new KeyValuePair<string, float>("window_close", 2f));
            float sum = 0f;
            foreach (var o in options) if (o.Key != lastBeat) sum += o.Value;
            float r = Random.value * sum;
            string pick = "road";
            foreach (var o in options)
            {
                if (o.Key == lastBeat) continue;
                r -= o.Value;
                if (r <= 0f) { pick = o.Key; break; }
            }
            lastBeat = pick;
            switch (pick)
            {
                case "lean_window": return new Beat { Name = pick, Dur = Random.Range(8f, 18f), Look = Look.Window, LookW = 0.5f, Face = Random.value < 0.5f ? "dreamy" : null, FaceW = 0.3f };
                case "window": return new Beat { Name = pick, Dur = Random.Range(4f, 9f), Look = Look.Window, LookW = 0.8f, Face = Random.value < 0.5f ? "dreamy" : null, FaceW = 0.35f };
                case "player": return new Beat { Name = pick, Dur = Random.Range(1.8f, 3.5f), Look = Look.Player, LookW = 1f, Face = Random.value < 0.3f ? "happy" : "smile", FaceW = 0.9f };
                case "dash": return new Beat { Name = pick, Dur = Random.Range(1.2f, 2.2f), Look = Look.Dash, LookW = 0.8f, Face = "focused", FaceW = 0.4f };
                case "dreamy": return new Beat { Name = pick, Dur = Random.Range(3f, 5f), Look = Look.Up, LookW = 0.7f, Face = "dreamy", FaceW = 1f, Roll = Random.value < 0.5f ? 7f : -7f };
                case "smug": return new Beat { Name = pick, Dur = Random.Range(2f, 3f), Look = Look.Player, LookW = 0.6f, Face = "smug", FaceW = 1f, Roll = -5f };
                case "gesture":
                {
                    // a body clip (stretching, rubbing an arm...): it owns head and arms, the face comes with it
                    string g = bored[Random.Range(0, bored.Length)];
                    if (g == lastGesture && bored.Length > 1) g = bored[(Array.IndexOf(bored, g) + 1) % bored.Length];
                    float len = PlayGesture(g);
                    if (len <= 0f) return new Beat { Name = "road", Dur = 3f, Look = Look.Road, LookW = 0.7f };
                    lastGesture = g;
                    return new Beat { Name = "gesture", Dur = len, Look = Look.Road, LookW = 0f };
                }
                case "radio_on": return new Beat { Name = pick, Dur = 2.4f, Look = Look.Radio, LookW = 0.9f, Face = "smile", FaceW = 0.7f, ActAt = 0.9f, WaitReach = true };
                case "radio_trick": return RadioTrickBeat();
                case "window_open": return new Beat { Name = pick, Dur = 2.6f, Look = Look.Crank, LookW = 0.9f, Face = null, ActAt = 0.8f, WaitReach = true };
                case "window_close": return new Beat { Name = pick, Dur = 2.6f, Look = Look.Crank, LookW = 0.9f, Face = raining ? "worried" : null, FaceW = 0.5f, ActAt = 0.7f, WaitReach = true };
                case "yawn": return new Beat { Name = pick, Dur = 2.8f, Look = Look.Road, LookW = 0.3f, Face = "yawn", FaceW = 1f, Pitch = -10f };
                default: return new Beat { Name = "road", Dur = Random.Range(3f, 6f), Look = Look.Road, LookW = 0.7f, Face = Random.value < 0.3f ? "focused" : null, FaceW = 0.3f };
            }
        }

        // ------------------------------------------------------------ reactions (from Harmony patches)
        string reactFace, reactAfter;
        float reactT = -1f, reactDur, reactAfterDur;

        public void React(string face, float dur, string after, float afterDur)
        {
            reactFace = face;
            reactDur = dur;
            reactAfter = after;
            reactAfterDur = afterDur;
            reactT = 0f;
            beat = null;
            nextBeat = Time.time + dur + afterDur + Random.Range(2f, 5f);
        }

        // ------------------------------------------------------------ per-frame state
        float radioBlend, humSwitch;
        bool humOn;
        float lookW, roll, pitchExtra, lookWVel, rollVel, pitchVel;
        Vector3 lookDir;              // current look direction from the head, in the uncle's local space
        float lookDist;
        bool lookInit;
        Look lookKind;
        Transform lookWatch;
        Vector3 lookFromDir;          // eased transition between targets (slow start, slow stop)
        float lookFromDist, lookT = 1f, lookDur = 1f;
        Vector3 saccade;
        float nextSaccade;
        float eyeYaw, eyePitch;
        bool lookAtPlayerOutside;
        float nextOutsideGlance;

        // decided in Update, used in PoseBones
        Look curLook = Look.Road;
        float curLookW, curRoll, curPitch;
        bool inCarNow, asleepNow, talkingNow;

        /// <summary>Called from the driver's Update: decides what she is doing and drives the face channels.</summary>
        public void Update(float dt, bool inCar, bool asleep, bool talking)
        {
            inCarNow = inCar;
            asleepNow = asleep;
            talkingNow = talking;
            Face.BeginFrame();
            if (!Enabled)
            {
                CancelFruit();
                if (Doze.Phase != DozePhase.Awake) { Doze.Phase = DozePhase.Awake; LidsTarget = 0f; dozeG = false; }
                curLookW = 0f;
                TailAmp = TailLift = 0f;
                Wind = windSmooth = Vector3.zero;
                return;
            }
            ReadWorld();
            float now = Time.time;
            DozeStep(now, dt, inCar, asleep, talking);
            bool slp = asleep || dozeG;   // the game's sleep or ours: everything she does on her own is off
            SelfActStep(dt, inCar);

            // reactions win over everything
            bool reacting = false;
            if (reactT >= 0f)
            {
                reactT += dt;
                if (reactT < reactDur) { Face.Want(reactFace, 1f, 0.08f); reacting = true; }
                else if (reactT < reactDur + reactAfterDur) { if (reactAfter != null) Face.Want(reactAfter, 0.9f, 0.3f); reacting = true; }
                else reactT = -1f;
            }

            curLook = Look.Road;
            curLookW = 0f;
            curRoll = 0f;
            curPitch = 0f;
            wantReach = wantLean = tiltTarget = chestTiltTarget = 0f;

            bool idleInCar = inCar && !slp && !talking && !reacting;
            radioBlend = Mathf.MoveTowards(radioBlend, inCar && radioOn && !slp ? 1f : 0f, dt / 0.8f);

            if (idleInCar)
            {
                if (beat == null && raining && windowOpen && herWindow != null && GestureWeight <= 0f && now >= nextWindowOk)
                {
                    beat = new Beat { Name = "window_close", Dur = 2.6f, Look = Look.Crank, LookW = 0.9f, Face = "worried", FaceW = 0.5f, ActAt = 0.7f, WaitReach = true };
                    nextWindowOk = now + Random.Range(5f, 12f); // if the crank sticks she tries again a bit later
                }
                if (beat == null && now >= nextBeat && GestureWeight <= 0f && !News.OnAir) { beat = PickBeat(); beat.T = 0f; }   // task 52: she listens to the news
                if (beat != null)
                {
                    beat.T += dt;
                    float env = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(Mathf.Min(beat.T / 0.6f, (beat.Dur - beat.T) / 1f)));
                    if (beat.Face != null)
                    {
                        float w = beat.FaceW;
                        if (beat.Name == "yawn") w *= Mathf.Clamp01(Mathf.Min(beat.T / 0.6f, (beat.Dur - beat.T) / 0.7f));
                        Face.Want(beat.Face, w, 0.3f);
                    }
                    curLook = beat.Look;
                    curLookW = beat.LookW;
                    // task 37: the switch flips when her hand is there (or as far as it goes), 1 s late at most
                    if (beat.ActAt >= 0f && !beat.Acted && beat.T >= beat.ActAt && (!beat.WaitReach || ReachDone || beat.T >= beat.ActAt + 1f))
                    {
                        beat.Acted = true;
                        DoAction(beat.Name, now);
                    }
                    ArmIntent(beat, env);
                    curRoll = beat.Roll * env;
                    curPitch = beat.Pitch * (beat.Name == "yawn" ? Mathf.Sin(Mathf.Clamp01(beat.T / beat.Dur) * Mathf.PI) : env);
                    if (beat.T >= beat.Dur)
                    {
                        beat = null;
                        nextBeat = now + Random.Range(6f, 16f) / Mathf.Max(0.2f, Activity * LuckActivity);
                    }
                }
                else
                {
                    curLook = Look.Road;
                    curLookW = 0.35f;
                }

                // a passing car (or one ahead) catches her eye: follow it until it is behind her
                WatchTraffic(now);
                if (watch != null && GestureWeight <= 0.01f)
                {
                    curLook = Look.Target;
                    curLookW = 0.85f;
                }
                // task 52: a news bulletin on the radio: she looks at it (a reach of a running beat keeps its own look)
                if (News.OnAir && GestureWeight <= 0.01f && (beat == null || beat.ActAt < 0f))
                {
                    curLook = Look.Radio;
                    curLookW = 0.8f;
                }
                if (now < attnUntil && GestureWeight <= 0.01f)
                {
                    curLook = attnLook;
                    curLookW = attnW;
                }

                // moods
                if (radioBlend > 0.01f && News.OnAir) Face.Want("smile", 0.25f * radioBlend, 0.6f);   // task 52: listening, no humming
                else if (radioBlend > 0.01f)
                {
                    if (now >= humSwitch) { humOn = !humOn; humSwitch = now + Random.Range(3f, 7f); }
                    Face.Want(humOn ? "hum" : "smile", 0.8f * radioBlend, 0.4f);
                }
                else if (Luck.Happy)
                {
                    // good luck: hums now and then without the radio
                    if (now >= humSwitch) { humOn = !humOn; humSwitch = now + (humOn ? Random.Range(2f, 4f) : Random.Range(8f, 20f)); }
                    Face.Want(humOn ? "hum" : "smile", humOn ? 0.7f : 0.35f, 0.5f);
                }
                else if (Luck.Nervous) Face.Want("worried", 0.45f, 0.8f);
                else if (night) Face.Want("sleepy", 0.6f, 1f);
                else if (raining) Face.Want("sad", 0.35f, 1f);
            }
            else
            {
                if (beat != null) { beat = null; nextBeat = now + Random.Range(3f, 8f) / Mathf.Max(0.2f, Activity); }
                watch = null;
                bool keepConsole = NaritaHere && NPlaying && inCar && talking && !asleep && !reacting;
                if (!keepConsole && GestureWeight > 0f && !GestureForced && StopGesture != null) StopGesture();
                if (inCar && asleep) { tiltTarget = 22f; chestTiltTarget = 6f; } // asleep in the car: slumped towards the window
                if (inCar && talking) { curLook = Look.Player; curLookW = 0f; }  // the game turns the head; eyes go to the player
                if (inCar && reacting) { curLook = Look.Road; curLookW = 0.8f; }
            }

            DozeApply(now);
            MornFace(dt);
            FortFace(dt);
            if (GestureFace != null && GestureWeight > 0f) Face.Want(GestureFace, GestureWeight, 0.2f);
            EarLayer(dt, slp);
            TailLayer(dt, inCar, asleep, talking);
            WindLayer(dt, inCar);
            CheckPending(now);
            DriverEvents(now, inCar, slp);
            Extras(dt, now, inCar, slp, talking, reacting);
            NaritaStep(dt, now, inCar, slp, talking, reacting);
            PrankStep(dt, now, inCar, slp, talking, reacting);
            SouvenirStep(now, inCar, slp, talking, reacting);   // task 47
            BootLayer(now);
            if (now < emoteUntil && emoteFace != null) Face.Want(emoteFace, emoteW, 0.2f);

            BobStep(dt, now, inCar && radioOn && !slp, talking, reacting);
            if (!slp)
            {
                // radio: ears bounce to the beat (only during a head-bob burst)
                float earK = radioBlend * bobW;
                if (earK > 0.01f && !talking)
                {
                    float s = Mathf.Sin(now * Mathf.PI * 2f * BeatHz);
                    Face.Want("twitch_l", Mathf.Max(0f, s) * 0.5f * earK, 0.05f);
                    Face.Want("twitch_r", Mathf.Max(0f, -s) * 0.5f * earK, 0.05f);
                }
            }
        }

        // ------------------------------------------------------------ tail: lazy sway standing and asleep, a happy wag now and then
        void TailLayer(float dt, bool inCar, bool asleep, bool talking)
        {
            float now = Time.time;
            bool walking = anim != null && anim.GetFloat("walkSpeed") > 0.2f;
            float amp, hz, lift;
            bool inBed = anim != null && anim.GetBool("bedTime");
            if (inCar) { amp = 0f; hz = 0.45f; lift = 0f; }
            else if (inBed) { amp = 1.5f; hz = 0.15f; lift = 0f; }   // on the motel bed, awake or not: no wagging
            else if (asleep) { amp = 2f; hz = 0.15f; lift = 0f; }   // lying in bed: barely
            else if (walking) { amp = 3f; hz = 0.6f; lift = 0f; }
            else
            {
                if (now >= nextTailWag && tailWagEnd < now)
                {
                    tailWagEnd = now + Random.Range(1.5f, 3f);
                    nextTailWag = tailWagEnd + Random.Range(8f, 20f) / Mathf.Max(0.2f, Activity);
                }
                bool wag = now < tailWagEnd;
                amp = wag ? 18f : talking ? 12f : 9f;
                hz = wag ? 0.95f : talking ? 0.6f : 0.42f;
                lift = wag ? 6f : 3f;
            }
            TailAmp = Mathf.MoveTowards(TailAmp, amp, dt * 20f);
            TailHz = Mathf.MoveTowards(TailHz, hz, dt * 0.8f);
            TailLift = Mathf.MoveTowards(TailLift, lift, dt * 6f);
        }

        // ------------------------------------------------------------ ears: flicks on top of everything (like in the Uma game)
        class EarMove { public string Name; public float Start, In, Hold, Out, W; }
        readonly List<EarMove> earMoves = new List<EarMove>();
        float nextEar;

        void EarLayer(float dt, bool asleep)
        {
            float now = Time.time;
            if (now >= nextEar)
            {
                string s = Random.value < 0.5f ? "_l" : "_r", o = s == "_l" ? "_r" : "_l";
                float w = Random.Range(0.7f, 1f) * (asleep ? 0.5f : 1f);
                float r = asleep ? 0f : Random.value;
                if (r < 0.5f) Flick("twitch" + s, now, w);
                else if (r < 0.7f) { Flick("twitch" + s, now, w); Flick("twitch" + s, now + 0.22f, w * 0.8f); }
                else if (r < 0.85f) { Flick("twitch" + s, now, w); Flick("twitch" + o, now + 0.03f, w); }
                else
                {
                    string[] turns = { "out", "fwd", "back", "roll" };
                    earMoves.Add(new EarMove
                    {
                        Name = turns[Random.Range(0, turns.Length)] + s, Start = now,
                        In = 0.35f, Hold = Random.Range(0.6f, 1.5f), Out = 0.4f, W = Random.Range(0.6f, 1f),
                    });
                }
                nextEar = now + (asleep ? Random.Range(4f, 10f) : Random.Range(1f, 3.5f));
            }
            for (int i = earMoves.Count - 1; i >= 0; i--)
            {
                var m = earMoves[i];
                float t = now - m.Start;
                if (t < 0f) continue;
                float env;
                if (t < m.In) env = Mathf.SmoothStep(0f, 1f, t / m.In);
                else if (t < m.In + m.Hold) env = 1f;
                else if (t < m.In + m.Hold + m.Out) env = Mathf.SmoothStep(1f, 0f, (t - m.In - m.Hold) / m.Out);
                else { earMoves.RemoveAt(i); continue; }
                Face.Pulse(m.Name, env * m.W);
            }
        }

        void Flick(string name, float start, float w)
        {
            earMoves.Add(new EarMove { Name = name, Start = start, In = 0.05f, Hold = 0.05f, Out = 0.14f, W = w });
        }

        const float BeatHz = 1.8f; // ~108 bpm head bob

        // ------------------------------------------------------------ body: arms and leaning towards the window
        float wantReach, wantLean, tiltTarget, chestTiltTarget;
        float reachW, leanW, tiltNow, chestTiltNow, tiltVel, chestTiltVel;
        // task 37: what her hand goes for this frame (set by ArmIntent, used by PoseArms -> ReachTo)
        string reachName;
        Transform reachT;
        Vector3 reachLocal;   // aim point in reachT's local space (the visible part, not the pivot)
        bool reachOuter, reachGrip, reachCrank;

        /// <summary>Her hand is there, or as far as her arm goes without leaning (the user: no lean, 2026-11-06).
        /// The action fires on it ("on the fingertips"). Updated in PoseArms.</summary>
        public bool ReachDone { get; private set; }

        /// <summary>Gap from her fingertips (palm for a grip) to the target at the last full reach, game units.</summary>
        public float ReachShort { get; private set; }

        /// <summary>Usual hand curve of a reach beat: out in 0.6 s from 0.1 s, back in 0.6 s ending 0.3 s before the end.</summary>
        static float ReachCurve(Beat b) { return Mathf.Clamp01(Mathf.Min((b.T - 0.1f) / 0.6f, (b.Dur - 0.3f - b.T) / 0.6f)); }

        /// <summary>Send a hand to t this frame. outer = the window-side arm (else the driver-side one); grip = the palm goes
        /// there (crank), else the fingertips (knobs, switches). Pranks call it from their Arm hook.</summary>
        void WantReachTo(string name, Transform t, bool outer, bool grip, float w) { WantReachTo(name, t, Vector3.zero, outer, grip, w); }

        /// <summary>Same, aimed at a point in t's local space (VisibleCentre: the middle of what is drawn, e.g. the mirror glass).</summary>
        void WantReachTo(string name, Transform t, Vector3 local, bool outer, bool grip, float w)
        {
            if (t == null || w <= 0f) return;
            reachName = name;
            reachT = t;
            reachLocal = local;
            reachOuter = outer;
            reachGrip = grip;
            reachCrank = false;
            wantReach = w;
        }

        void ArmIntent(Beat b, float env)
        {
            if (armL == null || armR == null) return;
            float T = b.T;
            if (b.Name.StartsWith("prank_")) { PrankArm(b, env); return; }
            switch (b.Name)
            {
                case "radio_on":
                    if (radio != null && radio.handle != null) WantReachTo("radio", radio.handle.transform, false, false, ReachCurve(b));
                    break;
                case "radio_trick":
                    RadioTrickArm(b);
                    break;
                case SouvBeat:
                    SouvenirArm(b);
                    break;
                case "reach_test":
                    if (reachTestT != null) WantReachTo(reachTestName, reachTestT, reachTestLocal, reachTestOuter, reachTestOuter, ReachCurve(b));
                    break;
                case "window_open":
                case "window_close":
                    // keep cranking while the glass moves
                    if (b.Acted && herWindow != null && herWindow.isAnimating) b.Dur = Mathf.Max(b.Dur, T + 0.8f);
                    if (herWindow != null)
                    {
                        WantReachTo("window crank", herWindow.transform, true, true, ReachCurve(b));
                        reachCrank = true;
                    }
                    break;
                case "lean_window":
                    if (!windowOpen) break;
                    wantLean = env;
                    tiltTarget = 10f * env;
                    chestTiltTarget = 3f * env;
                    break;
            }
        }

        bool leanLogged, leanLogged2;

        /// <summary>Debug key: start or stop leaning on the door now.</summary>
        public void ToggleLean()
        {
            if (beat != null && beat.Name == "lean_window") { beat.Dur = beat.T + 0.8f; return; }
            beat = new Beat { Name = "lean_window", Dur = 30f, Look = Look.Window, LookW = 0.5f };
        }

        /// <summary>Put the elbow towards one point and the hand towards another (no reach solving).</summary>
        static void AimArm(Arm a, Vector3 elbowT, Vector3 handT, float w)
        {
            if (a == null || w <= 0.001f) return;
            Quaternion upperOrig = a.Upper.rotation, foreOrig = a.Fore.rotation;
            Vector3 A = a.Upper.position, B = a.Fore.position;
            Quaternion upperSolved = Quaternion.FromToRotation(B - A, elbowT - A) * upperOrig;
            a.Upper.rotation = upperSolved;
            Vector3 B2 = a.Fore.position, C2 = a.Hand.position;
            Quaternion foreSolved = Quaternion.FromToRotation(C2 - B2, handT - B2) * a.Fore.rotation;
            a.Upper.rotation = Quaternion.Slerp(upperOrig, upperSolved, w);
            a.Fore.rotation = Quaternion.Slerp(foreOrig, foreSolved, w);
        }

        readonly HashSet<string> reachLogged = new HashSet<string>();

        /// <summary>Task 31: once per target, how far it is from her shoulder in arm lengths (over 1 = the arm stays
        /// straight and the hand falls short; SolveArm never stretches it).</summary>
        void ReachLog(string what, Arm a, Vector3 target)
        {
            if (a == null || reachLogged.Contains(what)) return;
            reachLogged.Add(what);
            float len = (a.Fore.position - a.Upper.position).magnitude + (a.Hand.position - a.Fore.position).magnitude;
            if (len < 1e-5f) return;
            float r = (target - a.Upper.position).magnitude / len;
            Debug.Log("[Umamusume companion] reach: " + what + " " + r.ToString("F2") + " arm lengths from the shoulder" + (r > 1f ? " (short, hand stops "
                + ((r - 1f) * len).ToString("F3") + " u away)" : " (ok)"));
        }

        bool reachShortLogged;

        /// <summary>
        /// Task 37: her hand to a point anywhere in the cab, with no lean of the body (user, 2026-11-06). The shoulder slides up to
        /// 0.1 arm length (~3 cm) towards the target, only as much as the arm lacks at 90 % extension; the arm (two-bone IK) goes
        /// straight at it; the knuckles point at it and the fingers open. Fingertips (grip: the wrist) are aimed at the point, so
        /// a near target is touched and a far one is pointed at from as close as the arm gets. ReachDone at 90 % of the way out;
        /// ReachShort = what is left between fingertips and target. Log once per reach: reach: name short u.
        /// </summary>
        public void CrateHands(Vector3 left, Vector3 right, Vector3 side, Vector3 forward, float weight)
        {
            SupportCrate(armL, left, -side, forward, weight);
            SupportCrate(armR, right, side, forward, weight);
        }

        void SupportCrate(Arm arm, Vector3 wrist, Vector3 outward, Vector3 forward, float weight)
        {
            if (arm == null) return;
            SolveArm(arm, wrist, outward * 0.5f - forward * 0.4f - Vector3.up, weight);
            PoseFingers(arm, false, weight);
            OrientHand(arm, (forward - outward * 0.45f).normalized, Vector3.up, weight);
        }

        float ReachTo(Arm a, string name, Vector3 point, bool grip, Vector3 pole, float w)
        {
            if (a == null || w <= 0.001f) return 0f;
            float L = ArmLen(a);
            if (L < 1e-5f) return 0f;
            float k = (Knuckles(a) - a.Hand.position).magnitude;
            if (k < 1e-5f) k = L * 0.12f;
            // wrist -> fingertips is about twice wrist -> knuckles on the Uma hand (palm 0.55 of the hand, recon 36);
            // a grip (crank) keeps v0.39's wrist on the handle: a palm offset pushed the fingers into the door panel (render)
            float tipOff = grip ? 0f : k * 1.9f;
            Transform sh = a == armL ? shoulderL : shoulderR;
            Vector3 S = a.Upper.position;
            Vector3 dir = point - S;
            float dist = dir.magnitude;
            if (dist < 1e-5f) return 0f;
            dir /= dist;
            // no slide on a grip: the crank's fingers went into the door panel with it (render 37); v0.39 crank kept as is
            float slide = grip ? 0f : Mathf.Clamp(dist - tipOff - 0.9f * L, 0f, 0.1f * L) * w;
            Vector3 S0 = S;
            if (sh != null && slide > 1e-5f)
            {
                // the arm root swings about the collar bone: move it the full slide across the bone, towards the target's side
                Vector3 v = S - sh.position;
                Vector3 across = Vector3.ProjectOnPlane(dir, v);
                if (v.sqrMagnitude > 1e-10f && across.sqrMagnitude > 1e-6f)
                    sh.rotation = Quaternion.FromToRotation(v, v + across.normalized * slide) * sh.rotation;
                S = a.Upper.position;
                dir = (point - S).normalized;
            }
            SolveArm(a, point - dir * tipOff, pole, w);
            // knuckles at the target, fingers open (not on a grip)
            Vector3 cur = Knuckles(a) - a.Hand.position, want = point - a.Hand.position;
            if (!grip && cur.sqrMagnitude > 1e-10f && want.sqrMagnitude > 1e-10f)
            {
                a.Hand.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(cur, want), w) * a.Hand.rotation;
                PoseFingers(a, false, w * 0.8f);
            }
            float gap = Mathf.Max(0f, (point - a.Hand.position).magnitude - tipOff);
            if (w >= 0.9f)
            {
                ReachDone = true;
                ReachShort = gap;
                if (!reachShortLogged && w >= 0.99f)
                {
                    reachShortLogged = true;
                    Debug.Log("[Umamusume companion] reach: " + name + " short " + gap.ToString("F3") + " u (" + (gap / L).ToString("F2") + " arm, "
                        + (grip ? "wrist" : "fingertips") + ", shoulder slid " + ((a.Upper.position - S0).magnitude / L).ToString("F2") + " arm, " + a.Upper.name + ")");
                }
            }
            return gap;
        }

        static void SolveArm(Arm a, Vector3 target, Vector3 pole, float w, Vector3 handLocalEnd = default(Vector3))
        {
            if (a == null || w <= 0.001f) return;
            Vector3 A = a.Upper.position, B = a.Fore.position, C = a.Hand.TransformPoint(handLocalEnd);
            float l1 = (B - A).magnitude, l2 = (C - B).magnitude;
            if (l1 < 1e-5f || l2 < 1e-5f) return;
            Vector3 d = target - A;
            float dist = Mathf.Clamp(d.magnitude, Mathf.Abs(l1 - l2) + 1e-3f, l1 + l2 - 1e-3f);
            Vector3 dn = d.sqrMagnitude > 1e-10f ? d.normalized : (C - A).normalized;
            float cosA = Mathf.Clamp((l1 * l1 + dist * dist - l2 * l2) / (2f * l1 * dist), -1f, 1f);
            Vector3 pd = Vector3.ProjectOnPlane(pole, dn);
            if (pd.sqrMagnitude < 1e-8f) pd = Vector3.ProjectOnPlane(Vector3.down, dn);
            pd.Normalize();
            Vector3 E = A + dn * (l1 * cosA) + pd * (l1 * Mathf.Sqrt(1f - cosA * cosA));
            Quaternion upperOrig = a.Upper.rotation, foreOrig = a.Fore.rotation;
            Quaternion upperSolved = Quaternion.FromToRotation(B - A, E - A) * upperOrig;
            a.Upper.rotation = upperSolved;
            Vector3 B2 = a.Fore.position, C2 = a.Hand.TransformPoint(handLocalEnd);
            Quaternion foreSolved = Quaternion.FromToRotation(C2 - B2, A + dn * dist - B2) * a.Fore.rotation;
            a.Upper.rotation = Quaternion.Slerp(upperOrig, upperSolved, w);
            a.Fore.rotation = Quaternion.Slerp(foreOrig, foreSolved, w);
        }

        /// <summary>Top edge of her lowered window glass, level with her shoulder (fallback: beside her).</summary>
        Vector3 SillPoint(Vector3 headPos, Vector3 shoulder, Vector3 fwd, Vector3 up, Vector3 right, Vector3 winDir)
        {
            Renderer r = herWindow != null && herWindow.window != null ? herWindow.window.GetComponentInChildren<Renderer>() : null;
            if (r == null) return shoulder + winDir * 0.3f - up * 0.25f;
            Bounds b = r.bounds;
            float top = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                Vector3 c = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                top = Mathf.Max(top, Vector3.Dot(c - headPos, up));
            }
            float lat = Vector3.Dot(b.center - headPos, right);
            float along = Vector3.Dot(shoulder - headPos, fwd) - 0.05f;
            return headPos + up * (top + 0.03f) + right * lat + fwd * along - winDir * 0.03f;
        }

        void PoseArms(float dt, Vector3 headPos)
        {
            reachW = Mathf.MoveTowards(reachW, wantReach, dt / 0.25f);
            if (reachW < 0.9f) ReachDone = false;
            if (reachW <= 0f) reachShortLogged = false;
            leanW = Mathf.MoveTowards(leanW, GestureWeight > 0.01f ? 0f : wantLean, dt / 0.8f);
            if (armL == null || armR == null || (reachW <= 0f && leanW <= 0f)) return;
            Vector3 fwd, up;
            Facing(out fwd, out up);
            Vector3 right = Vector3.Cross(up, fwd);
            Vector3 winDir = right * WindowSide(headPos, right);
            // the arm on the window side is the outer one
            bool leftOuter = Vector3.Dot(armL.Upper.position - armR.Upper.position, winDir) > 0f;
            Arm outer = leftOuter ? armL : armR, inner = leftOuter ? armR : armL;

            if (reachW > 0f && reachT != null)
            {
                Arm a = reachOuter ? outer : inner;
                Vector3 toSide = reachOuter ? winDir : -winDir;
                Vector3 t = reachT.TransformPoint(reachLocal);
                if (reachCrank)
                {
                    t -= winDir * 0.03f;
                    if (herWindow != null && herWindow.isAnimating)
                    {
                        float ph = Time.time * Mathf.PI * 2f * 1.2f;
                        t += (up * Mathf.Cos(ph) + fwd * Mathf.Sin(ph)) * 0.04f;
                    }
                }
                ReachLog(reachName, a, t);
                ReachTo(a, reachName, t, reachGrip, -up + toSide * 0.5f - fwd * 0.3f, reachW);
            }
            else ReachDone = false;
            if (leanW > 0f)
            {
                // elbow out on the door's edge. Offsets are in her own size (arm length),
                // not metres: the game's units are not metres (its speed read far too low).
                Vector3 sh = outer.Upper.position;
                float s = ((outer.Fore.position - sh).magnitude + (outer.Hand.position - outer.Fore.position).magnitude) / 0.39f;
                Vector3 elbowT = sh + winDir * (0.16f * s) - up * (0.02f * s) + fwd * (0.06f * s);
                Vector3 sill = SillPoint(headPos, sh, fwd, up, right, winDir);
                float latS = Vector3.Dot(sill - sh, winDir) / s, upS = Vector3.Dot(sill - sh, up) / s;
                bool sillOk = latS > 0.05f && latS < 0.35f && upS > -0.35f && upS < 0.1f;
                // elbow on top of the edge (user: the arm was below the door; the edge is about shoulder height)
                if (sillOk) elbowT = sh + winDir * (Mathf.Clamp(latS, 0.1f, 0.25f) * s) + up * (Mathf.Clamp(upS + 0.04f, -0.2f, 0.08f) * s) + fwd * (0.06f * s);
                if (!leanLogged)
                {
                    leanLogged = true;
                    Debug.Log("[Umamusume companion] lean: sill from shoulder lateral " + latS.ToString("F2") + " up " + upS.ToString("F2")
                        + " (arm units), " + (sillOk ? "used" : "ignored, default elbow point"));
                }
                // forearm lies forward along the door's edge, the hand a little outside (no face contact: with the
                // head turned to the window a body-relative "cheek" point landed in front of her chin)
                Vector3 handT = elbowT + fwd * (0.24f * s) + winDir * (0.06f * s) + up * (0.03f * s);
                AimArm(outer, elbowT, handT, leanW);
                if (!leanLogged2 && leanW >= 0.99f)
                {
                    leanLogged2 = true;
                    Vector3 e = outer.Fore.position - sh, h = outer.Hand.position - sh;
                    Debug.Log("[Umamusume companion] lean pose: " + outer.Upper.name + " elbow out " + (Vector3.Dot(e, winDir) / s).ToString("F2")
                        + " up " + (Vector3.Dot(e, up) / s).ToString("F2") + " fwd " + (Vector3.Dot(e, fwd) / s).ToString("F2")
                        + "; hand out " + (Vector3.Dot(h, winDir) / s).ToString("F2") + " up " + (Vector3.Dot(h, up) / s).ToString("F2")
                        + " fwd " + (Vector3.Dot(h, fwd) / s).ToString("F2"));
                }
            }
            else leanLogged = leanLogged2 = false;
        }

        /// <summary>Where she faces: her chest's forward, levelled (the uncle's root may not face where she sits).</summary>
        public void Facing(out Vector3 fwd, out Vector3 up)
        {
            up = uncle.up;
            fwd = uncle.forward;
            if (chest != null)
            {
                Vector3 f = Vector3.ProjectOnPlane(chest.Deform() * Vector3.forward, up);
                if (f.sqrMagnitude > 1e-4f) fwd = f.normalized;
            }
        }

        // ------------------------------------------------------------ her own will: radio and window
        bool CanRadio()
        {
            if (radio == null || radioOn || car == null) return false;
            // at full volume the game makes her complain about the noise right after: don't
            var vol = radio.radioVolume != null ? radio.radioVolume.GetComponent<RadioVolLogicC>() : null;
            return vol == null || vol.volumeSetting != 0;
        }

        void DoAction(string name, float now)
        {
            try
            {
                switch (name)
                {
                    case "radio_on":
                        if (!CanRadio()) break;
                        Pending(name, now);
                        radio.StartCoroutine(radio.Trigger()); // same as the player's click: battery check, knob, sound
                        sheTurnedRadio = true;
                        radioOnAt = now;
                        nextRadioOk = now + Random.Range(300f, 600f);
                        break;
                    case "window_open":
                        if (herWindow == null || herWindow.isOpen || raining) break;
                        Pending(name, now);
                        herWindow.StartCoroutine(herWindow.Trigger()); // may stick (the game's own chance), she'll try another time
                        nextWindowOk = now + Random.Range(240f, 480f);
                        break;
                    case "window_close":
                        if (herWindow == null || !herWindow.isOpen) break;
                        Pending(name, now);
                        herWindow.StartCoroutine(herWindow.Trigger());
                        if (!raining) nextWindowOk = now + Random.Range(180f, 360f);
                        break;
                    case "radio_trick":
                        RadioTrickAct(now);
                        break;
                    case SouvBeat:
                        SouvenirAct(now);
                        break;
                    default:
                        if (name.StartsWith("prank_")) PrankAct(now);
                        break;
                }
            }
            catch (Exception e) { Debug.LogWarning("[Umamusume companion] own action " + name + ": " + e.Message); }
        }

        void Pending(string action, float now)
        {
            pendingAction = action;
            pendingAt = now + 0.6f;
            pendingWasOpen = herWindow != null && herWindow.isOpen;
            pendingRain = raining;
        }

        /// <summary>A moment after her action: did it work? Then a remark (not every time).</summary>
        void CheckPending(float now)
        {
            if (pendingAt < 0f || now < pendingAt) return;
            string a = pendingAction;
            pendingAt = -1f;
            if (Say == null) return;
            string key = null;
            switch (a)
            {
                case "radio_on":
                    if (car != null && car.radioOn) key = "radio_on";
                    break;
                case "window_open":
                case "window_close":
                    bool moved = herWindow != null && herWindow.isOpen != pendingWasOpen;
                    if (!moved) key = "window_stuck";
                    else if (a == "window_open") key = "window_open";
                    else key = pendingRain ? "window_close_rain" : "window_close";
                    break;
            }
            if (key != null && (key == "window_close_rain" || Random.value < 0.8f)) Say(key);
        }

        // ------------------------------------------------------------ traffic she watches
        Transform watch;
        float nextWatch;

        void WatchTraffic(float now)
        {
            if (head == null) return;
            Vector3 fwd, up;
            Facing(out fwd, out up);
            Vector3 hp = head.T.position;
            if (watch != null)
            {
                Vector3 d = watch.position - hp;
                if (!watch.gameObject.activeInHierarchy || d.magnitude > 45f || Vector3.Angle(fwd, Vector3.ProjectOnPlane(d, up)) > 115f)
                {
                    watch = null;
                    nextWatch = now + Random.Range(3f, 8f);
                }
                return;
            }
            if (now < nextWatch) return;
            nextWatch = now + 0.3f;
            Transform best = null;
            float bestD = 35f;
            foreach (var c in aiCars)
            {
                if (c == null || !c.gameObject.activeInHierarchy) continue;
                Vector3 d = c.transform.position - hp;
                float dist = d.magnitude;
                if (dist < 4f || dist > bestD) continue;
                if (Vector3.Angle(fwd, Vector3.ProjectOnPlane(d, up)) > 80f) continue;
                best = c.transform;
                bestD = dist;
            }
            if (best == null) return;
            if (Random.value < 0.75f) watch = best;
            else nextWatch = now + Random.Range(3f, 6f);
        }

        // ------------------------------------------------------------ wind through her open window
        /// <summary>World direction * strength (0..~1.4) for the hair springs; zero with the window shut or parked.</summary>
        public Vector3 Wind { get; private set; }
        public float WindScale = 1f;
        Vector3 windSmooth;

        void WindLayer(float dt, bool inCar)
        {
            Vector3 want = Vector3.zero;
            if (inCar && windowOpen && head != null && WindScale > 0f)
            {
                float speed = SpeedKmh();
                Vector3 fwd, up;
                Facing(out fwd, out up);
                Vector3 right = Vector3.Cross(up, fwd);
                float side = WindowSide(head.T.position, right);
                // air comes in through her window and goes back: inward + backward, with gusts
                Vector3 dir = (-right * side * 0.8f - fwd * 0.6f).normalized;
                float gust = 0.6f + 0.8f * Mathf.PerlinNoise(Time.time * 1.7f, 0.3f);
                want = dir * Mathf.Clamp01(speed / 72f) * gust * WindScale;
                if (Time.time >= nextWindLog)
                {
                    nextWindLog = Time.time + 10f;
                    Debug.Log("[Umamusume companion] wind: speed " + speed.ToString("F0") + " km/h, strength " + want.magnitude.ToString("F2"));
                }
            }
            windSmooth = Vector3.Lerp(windSmooth, want, 1f - Mathf.Exp(-dt / 0.15f));
            Wind = windSmooth;
        }

        float WindowSide(Vector3 headPos, Vector3 right)
        {
            var cam = Camera.main;
            if (cam != null)
            {
                float d = Vector3.Dot(headPos - cam.transform.position, right);
                if (Mathf.Abs(d) > 0.1f) return Mathf.Sign(d);
            }
            return 1f;
        }

        Vector3 LookPoint(Look l, Vector3 headPos)
        {
            Vector3 up, fwd;
            Facing(out fwd, out up);
            Vector3 right = Vector3.Cross(up, fwd);
            var cam = Camera.main;
            // her window is on the side away from the driver (player)
            float side = 1f;
            if (cam != null)
            {
                float d = Vector3.Dot(headPos - cam.transform.position, right);
                if (Mathf.Abs(d) > 0.1f) side = Mathf.Sign(d);
            }
            switch (l)
            {
                case Look.Window: return headPos + right * side * 5f + fwd * 5f + up * 0.2f; // ~45°, the eyes do the rest
                case Look.Player: return cam != null ? cam.transform.position : headPos + fwd * 5f;
                case Look.Dash: return headPos + fwd * 0.7f - up * 0.45f - right * side * 0.3f;
                case Look.Up: return headPos + fwd * 6f + up * 2.5f;
                case Look.Target: return watch != null ? watch.position + up * 0.8f : headPos + fwd * 12f;
                case Look.Radio: return radio != null && radio.handle != null ? radio.handle.transform.position : headPos + fwd * 0.7f - up * 0.45f;
                case Look.Spot: return spotPos;
                case Look.Crank: return herWindow != null ? herWindow.transform.position : headPos + right * side * 0.5f - up * 0.4f;
                case Look.Reach: return reachT != null ? reachT.TransformPoint(reachLocal) : headPos + fwd * 0.7f - up * 0.45f;
                default: return headPos + fwd * 12f + up * 0.3f;
            }
        }

        /// <summary>Called from the driver's Pose() after the pose copy and posture, before springs.</summary>
        public void PoseBones(float dt)
        {
            if (head == null || dt <= 0f) return;
            float now = Time.time;

            // breathing
            if (chest != null)
            {
                float a = BreathStep(dt);   // slower and deeper while she dozes (Doze.cs)
                chest.T.rotation = Quaternion.AngleAxis(a, chest.Deform() * Vector3.right) * chest.T.rotation;
            }
            tiltNow = Mathf.SmoothDamp(tiltNow, Enabled ? tiltTarget : 0f, ref tiltVel, 0.8f, Mathf.Infinity, dt);
            chestTiltNow = Mathf.SmoothDamp(chestTiltNow, Enabled ? chestTiltTarget : 0f, ref chestTiltVel, 0.8f, Mathf.Infinity, dt);
            if (chest != null && Mathf.Abs(chestTiltNow) > 0.01f) TiltTowardsWindow(chest, chestTiltNow);
            if (!Enabled) { ResetEyes(); return; }

            Vector3 headPos = head.T.position;
            Vector3 targetW = LookPoint(curLook, headPos);
            if (now >= nextSaccade)
            {
                nextSaccade = now + Random.Range(0.6f, 2.2f);
                float dist = Vector3.Distance(headPos, targetW);
                saccade = Random.insideUnitSphere * dist * (curLook == Look.Player ? 0.03f : 0.12f);
            }

            // smooth the look target in the uncle's local space (it moves with the car)
            Vector3 wantLocal = uncle.InverseTransformPoint(targetW);
            Vector3 headLocal = uncle.InverseTransformPoint(headPos);
            Vector3 wantVec = wantLocal - headLocal;
            float wantDist = Mathf.Max(0.1f, wantVec.magnitude);
            Vector3 wantDir = wantVec / wantDist;
            if (!lookInit) { lookDir = wantDir; lookDist = wantDist; lookKind = curLook; lookInit = true; }
            if (curLook != lookKind || (curLook == Look.Target && watch != lookWatch))
            {
                lookWatch = watch;
                // new target: ease over 0.5-1.3 s depending on how far the head has to turn
                lookKind = curLook;
                lookFromDir = lookDir;
                lookFromDist = lookDist;
                lookT = 0f;
                lookDur = Mathf.Clamp(0.5f + Vector3.Angle(lookDir, wantDir) / 90f * 0.8f, 0.5f, 1.3f);
            }
            if (lookT < 1f)
            {
                lookT = Mathf.Min(1f, lookT + dt / lookDur);
                float e = Mathf.SmoothStep(0f, 1f, lookT);
                lookDir = Vector3.Slerp(lookFromDir, wantDir, e);
                lookDist = Mathf.Lerp(lookFromDist, wantDist, e);
            }
            else
            {
                // same target moving (saccade, car turning): follow gently
                float f = 1f - Mathf.Exp(-dt / 0.25f);
                lookDir = Vector3.Slerp(lookDir, wantDir, f);
                lookDist = Mathf.Lerp(lookDist, wantDist, f);
            }
            Vector3 look = uncle.TransformPoint(headLocal + lookDir * lookDist);

            float headW = inCarNow && !asleepNow ? curLookW : 0f;
            if (talkingNow) headW = 0f;
            if (!SulkHead) headW *= 1f - GestureWeight;   // task 34: the sulk turns the head to the window over its clip
            // getting out of / into the car: the body turns to the door, let go of the head at once
            bool moving = anim != null && (anim.GetBool("exitCar") || (anim.GetBool("enterCar") && uncle.parent == null));
            if (moving) { headW = 0f; lookW = Mathf.MoveTowards(lookW, 0f, dt / 0.2f); lookWVel = 0f; }
            else lookW = Mathf.SmoothDamp(lookW, headW, ref lookWVel, 0.5f, Mathf.Infinity, dt);
            roll = Mathf.SmoothDamp(roll, curRoll, ref rollVel, 0.35f, Mathf.Infinity, dt);
            pitchExtra = Mathf.SmoothDamp(pitchExtra, curPitch, ref pitchVel, 0.3f, Mathf.Infinity, dt);

            // radio head bob
            float bob = 0f, sway = 0f;
            if (rough && !talkingNow)
            {
                bob += (Mathf.PerlinNoise(now * 13f, 1.7f) - 0.5f) * 5f;
                sway += (Mathf.PerlinNoise(2.3f, now * 11f) - 0.5f) * 4f;
            }
            float bobK = radioBlend * bobW;   // MusicBob.cs: bursts and pauses
            if (bobK > 0.01f && !talkingNow)
            {
                bob += Mathf.Sin(now * Mathf.PI * 2f * BeatHz) * 4f * bobK;
                sway += Mathf.Sin(now * Mathf.PI * BeatHz) * 3f * bobK;
            }

            if (Mathf.Abs(tiltNow) > 0.01f) TiltTowardsWindow(head, tiltNow * 0.6f);
            if (neck != null && Mathf.Abs(tiltNow) > 0.01f) TiltTowardsWindow(neck, tiltNow * 0.4f);
            if (lookW > 0.001f || Mathf.Abs(roll) + Mathf.Abs(pitchExtra) + Mathf.Abs(bob) + Mathf.Abs(sway) > 0.01f)
            {
                float yaw, pitch;
                // at the player she turns properly: up to 80 deg, a quarter of it in the shoulders
                bool atPlayer = curLook == Look.Player;
                YawPitch(head, look, atPlayer ? 65f : 50f, atPlayer ? 30f : 25f, out yaw, out pitch);
                yaw *= lookW;
                pitch *= lookW;
                if (atPlayer && chest != null)
                {
                    Quaternion Dc = chest.Deform();
                    chest.T.rotation = Quaternion.AngleAxis(yaw * 0.33f, Dc * Vector3.up) * chest.T.rotation;
                    yaw *= 0.67f;
                }
                if (neck != null)
                {
                    Quaternion D = neck.Deform();
                    neck.T.rotation = Quaternion.AngleAxis(yaw * 0.4f, D * Vector3.up)
                        * Quaternion.AngleAxis(pitch * 0.4f + bob * 0.5f, D * Vector3.right) * neck.T.rotation;
                }
                else { yaw /= 0.6f; pitch /= 0.6f; }
                Quaternion H = head.Deform();
                head.T.rotation = Quaternion.AngleAxis(yaw * 0.6f, H * Vector3.up)
                    * Quaternion.AngleAxis(pitch * 0.6f + pitchExtra + bob * 0.5f, H * Vector3.right)
                    * Quaternion.AngleAxis(roll + sway, H * Vector3.forward) * head.T.rotation;
            }

            DozePose(dt);
            PoseArms(dt, headPos);
            PoseExtras(dt);
            PosePranks(dt);
            SouvenirPose();   // task 47: the item on her fingertips
            MornPose();
            FortPose();

            // eyes: at the look target (player while talking), with saccades
            if (eyeL == null && eyeR == null) return;
            Vector3 eyeTarget;
            var cam = Camera.main;
            if (inCarNow && talkingNow && cam != null) eyeTarget = cam.transform.position;
            else if (inCarNow) eyeTarget = look + saccade;
            else
            {
                // outside: now and then glance at the player when close
                if (now >= nextOutsideGlance)
                {
                    nextOutsideGlance = now + Random.Range(2f, 6f);
                    lookAtPlayerOutside = cam != null && Vector3.Distance(cam.transform.position, headPos) < 6f && (talkingNow || Random.value < 0.4f);
                }
                Quaternion Hd = head.Deform();
                eyeTarget = lookAtPlayerOutside && cam != null ? cam.transform.position : headPos + Hd * Vector3.forward * 4f + saccade * 0.5f;
            }
            float ey, ep;
            const float maxYaw = 15f, maxPitch = 8f;
            YawPitch(head, eyeTarget, maxYaw, maxPitch, out ey, out ep);
            float k = 1f - Mathf.Exp(-dt / 0.04f);
            eyeYaw = Mathf.Lerp(eyeYaw, ey, k);
            eyePitch = Mathf.Lerp(eyePitch, ep, k);
            // Apply lid coverage after smoothing so the iris is centred on the actual closed frame.
            float open = 1f - Mathf.Clamp01(Mathf.Max(Face.EyeCovered, LidsShut));
            Vector2 slide = Vector2.ClampMagnitude(new Vector2(eyeYaw / maxYaw, -eyePitch / maxPitch), 1f) * (0.45f * open);
            Quaternion He = head.Deform();
            Quaternion r = Quaternion.AngleAxis(eyeYaw * open, He * Vector3.up) * Quaternion.AngleAxis(eyePitch * open, He * Vector3.right);
            if (eyeL != null) eyeL.Aim(slide, r);
            if (eyeR != null) eyeR.Aim(slide, r);
        }

        /// <summary>
        /// Start of every frame, before the pose copy: bones this class rotates go back to rest. Bones that are not
        /// copied from the uncle (Chest) would otherwise keep last frame's offset and drift (breathing piled up).
        /// </summary>
        public void ResetBones()
        {
            foreach (var b in new[] { chest, neck, head })
                if (b != null) b.T.localRotation = b.RestLocal;
            ResetEyes();
            DozeResetBones();
        }

        /// <summary>Roll a bone so its top goes towards her window (degrees).</summary>
        void TiltTowardsWindow(Bone b, float deg)
        {
            Quaternion D = b.Deform();
            Vector3 f = D * Vector3.forward, u = D * Vector3.up;
            Vector3 fw, up0;
            Facing(out fw, out up0);
            Vector3 right = Vector3.Cross(up0, fw);
            Vector3 winDir = right * WindowSide(b.T.position, right);
            float sign = Mathf.Sign(Vector3.Dot(Vector3.Cross(f, u), winDir)); // +angle about f moves u towards f x u
            b.T.rotation = Quaternion.AngleAxis(sign * deg, f) * b.T.rotation;
        }

        void ResetEyes()
        {
            if (eyeL != null) eyeL.Reset();
            if (eyeR != null) eyeR.Reset();
        }

        /// <summary>Yaw/pitch (degrees, clamped) that turn the bone's forward towards the point.</summary>
        static void YawPitch(Bone b, Vector3 point, float maxYaw, float maxPitch, out float yaw, out float pitch)
        {
            Quaternion D = b.Deform();
            Vector3 fwd = D * Vector3.forward, up = D * Vector3.up, right = D * Vector3.right;
            Vector3 dir = point - b.T.position;
            if (dir.sqrMagnitude < 1e-6f) { yaw = pitch = 0f; return; }
            dir.Normalize();
            yaw = Mathf.Clamp(Vector3.SignedAngle(Vector3.ProjectOnPlane(fwd, up), Vector3.ProjectOnPlane(dir, up), up), -maxYaw, maxYaw);
            Quaternion y = Quaternion.AngleAxis(yaw, up);
            Vector3 fwdY = y * fwd, rightY = y * right;
            pitch = Mathf.Clamp(Vector3.SignedAngle(fwdY, Vector3.ProjectOnPlane(dir, rightY), rightY), -maxPitch, maxPitch);
        }
    }
}
