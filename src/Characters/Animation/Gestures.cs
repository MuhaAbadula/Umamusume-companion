using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Upper-body gesture clips (models/&lt;name&gt;/anims/*.clip from blender/export_clip.py). Each frame stores, per
    /// bone, the world rotation change relative to the hips in bind space; played on top of the copied pose:
    /// target = hipDeform * delta * rest. Hips and legs stay the game's (car seat), the rest blends in and out.
    /// </summary>
    public class Gestures
    {
        class Clip
        {
            public string Name, Face;
            public int Space;              // 0 = relative to the hips, 1 = in her levelled facing frame
            public int Kind;               // 0 gesture, 1 walk cycle (looping, played by step phase)
            public float[] Ups = new float[0], Downs = new float[0]; // step phases: left ankle passes the right one fwd / back
            public string[] BoneNames;
            public float Fps;
            public int Frames;
            public int[] Bones;            // index into rig
            public Quaternion[] Rot;       // [frame * bones + i]
            public float[] W;              // per frame and bone: arm blend (how far the clip's arm left its start)
            public int ExtraN;             // v5: floats per frame in Extra (walk: contact L, contact R, hip height offset)
            public float[] Extra = new float[0];
            public float Cycle;            // v5 walk: ground covered per loop, in leg lengths
            public float HipRatio, LegRatio; // v6 walk: mean walking hip / standing hip, leg unit / standing hip (0 = unknown)
            public float Start;            // v5+ walk: phase where the left foot lands (common phase 0)
            public float Length { get { return (Frames - 1) / Fps; } }
        }

        class RigBone { public Transform T; public Quaternion Rest, RestLocal; }

        readonly Dictionary<string, Clip> clips = new Dictionary<string, Clip>();
        readonly List<RigBone> rig = new List<RigBone>();
        readonly Dictionary<string, int> rigIndex = new Dictionary<string, int>();
        readonly Transform hip;
        readonly Quaternion hipRest;
        Clip cur;
        float t;
        public float Weight { get; private set; }
        /// <summary>The character's up comes from here (the uncle's root: upright, in the car the seat).</summary>
        public Transform Up;
        public string Face { get { return cur != null ? cur.Face : null; } }
        public bool Playing { get { return cur != null; } }
        public int Count { get { return clips.Count; } }
        Clip walk, walkFast;
        /// <summary>v0.24.4: 0 = the calm walk, 1 = the brisk one ("walk_fast"); set by the driver from her speed.</summary>
        public float WalkBlend;
        float B { get { return walkFast != null && walk != null && walk.Cycle > 0f && walkFast.Cycle > 0f ? Mathf.Clamp01(WalkBlend) : 0f; } }
        public bool HasFastWalk { get { return walkFast != null && walkFast.Cycle > 0f; } }
        /// <summary>Natural speed of the calm (fast = false) or brisk walk, in its leg units per second.</summary>
        public float WalkNaturalSpeed(bool fast)
        {
            var c = fast ? walkFast : walk;
            return c != null && c.Cycle > 0f ? c.Cycle / Mathf.Max(0.05f, c.Length) : 0f;
        }
        /// <summary>Common walk phase (0 = left foot lands) -> this clip's own phase (old clips: as is).</summary>
        static float PhaseOf(Clip c, float phi) { return c.Cycle > 0f ? Mathf.Repeat(phi + c.Start, 1f) : phi; }
        readonly List<Clip> idles = new List<Clip>();
        readonly List<Clip> beds = new List<Clip>();
        public int BedCount { get { return beds.Count; } }

        /// <summary>Hip->head axis of a bed clip in bind space (its frame 0).</summary>
        public Vector3 BedAxis(int i)
        {
            var u = beds[i].Ups;
            return u.Length >= 3 ? new Vector3(u[0], u[1], u[2]) : Vector3.forward;
        }

        public string BedName(int i) { return i >= 0 && i < beds.Count ? beds[i].Name : "?"; }

        public void ApplyBed(int i, float t, float w, Quaternion frame)
        {
            if (i < 0 || i >= beds.Count || w <= 0.001f) return;
            var c = beds[i];
            // neck and head from the pose too: with the uncle's head her hand landed in her face
            ApplyLoop(c, c.Length > 0.1f ? Mathf.Repeat(t, c.Length) / c.Length : 0f, w, frame, false, false, 1f);
        }
        public int IdleCount { get { return idles.Count; } }
        public string IdleNames { get { var n = new List<string>(); foreach (var c in idles) n.Add(c.Name); return string.Join(" ", n.ToArray()); } }
        public float IdleLength(int i) { return idles[i].Length; }

        /// <summary>A standing loop (index, time in s); whole body except neck/head, like the walk.</summary>
        public void ApplyIdle(int i, float t, float w, Quaternion frame, bool skipR)
        {
            if (i < 0 || i >= idles.Count || w <= 0.001f) return;
            var c = idles[i];
            ApplyLoop(c, Mathf.Repeat(t, c.Length) / Mathf.Max(1e-4f, c.Length), w, frame, false, skipR);
        }
        public bool HasWalk { get { return walk != null; } }
        public string WalkName { get { return walk != null ? walk.Name : null; } }
        public float WalkLength { get { return walk != null ? walk.Length : 1f; } }
        public float[] WalkUps { get { return walk != null ? walk.Ups : new float[0]; } }
        public float[] WalkDowns { get { return walk != null ? walk.Downs : new float[0]; } }
        /// <summary>v5 walk: ground covered per loop in leg lengths (0 = unknown, old clip).</summary>
        public float WalkCycle { get { return walk == null ? 0f : B > 0f ? Mathf.Lerp(walk.Cycle, walkFast.Cycle, B) : walk.Cycle; } }
        public float WalkHipRatio { get { return walk == null || walk.HipRatio <= 0f ? 0f : B > 0f && walkFast.HipRatio > 0f ? Mathf.Lerp(walk.HipRatio, walkFast.HipRatio, B) : walk.HipRatio; } }
        public float WalkLegRatio { get { return walk == null || walk.LegRatio <= 0f ? 0f : B > 0f && walkFast.LegRatio > 0f ? Mathf.Lerp(walk.LegRatio, walkFast.LegRatio, B) : walk.LegRatio; } }

        /// <summary>v5 walk: the phase where the left foot has just landed (a natural first step from standing).</summary>
        public float WalkStartPhase
        {
            get
            {
                if (walk != null && walk.Cycle > 0f) return 0f;   // common phase: 0 is the left foot landing
                if (walk == null || walk.ExtraN < 1) return 0f;
                int n = walk.Frames - 1, e = walk.ExtraN;
                for (int k = 0; k < n; k++)
                    if (walk.Extra[k * e] < 0.5f && walk.Extra[(k + 1) * e] >= 0.5f) return (k + 1f) / n;
                return 0f;
            }
        }

        /// <summary>v5 walk at a phase: foot contacts (0..1) and the hip height offset (leg lengths).</summary>
        public bool WalkContacts(float phase, out float left, out float right, out float hipDy)
        {
            left = right = hipDy = 0f;
            if (walk == null || walk.ExtraN < 3) return false;
            Contacts(walk, PhaseOf(walk, phase), out left, out right, out hipDy);
            float b = B;
            if (b > 0f && walkFast.ExtraN >= 3)
            {
                float l2, r2, h2;
                Contacts(walkFast, PhaseOf(walkFast, phase), out l2, out r2, out h2);
                left = Mathf.Lerp(left, l2, b);
                right = Mathf.Lerp(right, r2, b);
                hipDy = Mathf.Lerp(hipDy, h2, b);
            }
            return true;
        }

        /// <summary>v6 walk: the hip's sideways (right) and forward sway at a phase, leg units; 0 for older clips.</summary>
        public void WalkHipSway(float phase, out float right, out float fwd)
        {
            right = fwd = 0f;
            if (walk == null) return;
            Sway(walk, PhaseOf(walk, phase), out right, out fwd);
            float b = B;
            if (b > 0f)
            {
                float r2, f2;
                Sway(walkFast, PhaseOf(walkFast, phase), out r2, out f2);
                right = Mathf.Lerp(right, r2, b);
                fwd = Mathf.Lerp(fwd, f2, b);
            }
        }

        static void Sway(Clip c, float phase, out float right, out float fwd)
        {
            right = fwd = 0f;
            if (c == null || c.ExtraN < 5) return;
            int n = c.Frames - 1;
            float ft = Mathf.Repeat(phase, 1f) * n;
            int f0 = Mathf.Min((int)ft, n), f1 = f0 + 1 > n ? 1 : f0 + 1;
            float a = ft - f0;
            int e = c.ExtraN;
            right = Mathf.Lerp(c.Extra[f0 * e + 3], c.Extra[f1 * e + 3], a);
            fwd = Mathf.Lerp(c.Extra[f0 * e + 4], c.Extra[f1 * e + 4], a);
        }

        static void Contacts(Clip c, float phase, out float left, out float right, out float hipDy)
        {
            int n = c.Frames - 1;
            float ft = Mathf.Repeat(phase, 1f) * n;
            int f0 = Mathf.Min((int)ft, n), f1 = f0 + 1 > n ? 1 : f0 + 1;
            float a = ft - f0;
            int e = c.ExtraN;
            left = Mathf.Lerp(c.Extra[f0 * e], c.Extra[f1 * e], a);
            right = Mathf.Lerp(c.Extra[f0 * e + 1], c.Extra[f1 * e + 1], a);
            hipDy = Mathf.Lerp(c.Extra[f0 * e + 2], c.Extra[f1 * e + 2], a);
        }

        static bool IsArm(string n, char side)
        {
            if (n.Length < 2 || n[n.Length - 1] != side || n[n.Length - 2] != '_') return false;
            foreach (var p in new[] { "Shoulder_", "Arm_", "Elbow_", "Wrist_", "Thumb_", "Index_", "Middle_", "Ring_", "Pinky_" })
                if (n.StartsWith(p)) return true;
            return false;
        }

        /// <summary>
        /// The walk cycle at a step phase (0..1, looping), whole body except the neck and head (the game turns those),
        /// in the walking frame (where the uncle's root faces). skipL/skipR: that arm holds something, leave it be.
        /// </summary>
        public void ApplyWalk(float phase, float w, Quaternion frame, bool skipL, bool skipR)
        {
            if (walk == null || w <= 0.001f) return;
            // half of the clip's neck/head (the bob and tilt of her steps), half left to the game's head look
            const float HeadW = 0.8f;   // was 0.5: half the head came from the uncle's walk
            ApplyLoop(walk, PhaseOf(walk, phase), w, frame, skipL, skipR, HeadW);
            float b = B;
            if (b > 0.001f) ApplyLoop(walkFast, PhaseOf(walkFast, phase), w * b, frame, skipL, skipR, HeadW);
        }

        /// <summary>headW: how much of the clip's neck/head to use (0 = leave them to the game).</summary>
        void ApplyLoop(Clip c, float phase, float w, Quaternion frame, bool skipL, bool skipR, float headW = 0f)
        {
            int n = c.Frames - 1;                 // looping: the last frame repeats the first
            float ft = Mathf.Repeat(phase, 1f) * n;
            int f0 = Mathf.Min((int)ft, n), f1 = f0 + 1 > n ? 1 : f0 + 1;
            float a = ft - f0;
            int nb = c.Bones.Length;
            for (int i = 0; i < nb; i++)
            {
                int ri = c.Bones[i];
                if (ri < 0) continue;
                string bn = c.BoneNames[i];
                float bw = w;
                if (bn == "Neck" || bn == "Head") { bw = w * headW; if (bw <= 0.001f) continue; }
                if ((skipL && IsArm(bn, 'L')) || (skipR && IsArm(bn, 'R'))) continue;
                var b = rig[ri];
                Quaternion d = Quaternion.Slerp(c.Rot[f0 * nb + i], c.Rot[f1 * nb + i], a);
                b.T.rotation = Quaternion.Slerp(b.T.rotation, frame * d * b.Rest, bw);
            }
        }
        public IEnumerable<string> Names { get { return clips.Keys; } }

        const float FadeIn = 0.35f, FadeOut = 0.5f;

        public Gestures(string dir, Transform hip, Quaternion hipRest, Dictionary<string, Transform> bones, Dictionary<string, Quaternion> rests)
        {
            this.hip = hip;
            this.hipRest = hipRest;
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.clip"))
            {
                try
                {
                    var c = Load(f, bones, rests);
                    if (c == null) continue;
                    if (c.Kind == 1 && c.Name == "walk_fast") walkFast = c;
                    else if (c.Kind == 1) { if (walk == null || c.Name == "walk") walk = c; }
                    else if (c.Kind == 2) idles.Add(c);
                    else if (c.Kind == 3) beds.Add(c);
                    else clips[c.Name] = c;
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("[Umamusume companion] clip " + f + ": " + e);
                    // loud: a walk clip that failed silently cost three rounds of tuning a walk nobody saw
                    JaLoader.Console.LogError(CompanionStorage.ModId, "Animation " + Path.GetFileName(f) + " failed to load: " + e.Message);
                }
            }
        }

        Clip Load(string path, Dictionary<string, Transform> bones, Dictionary<string, Quaternion> rests)
        {
            using (var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8))
            {
                if (new string(r.ReadChars(4)) != "FCLP") throw new IOException("not a clip");
                int ver = r.ReadInt32();
                if (ver < 1 || ver > 6) throw new IOException("clip version " + ver);
                var c = new Clip { Name = r.ReadString(), Face = r.ReadString() };
                c.Space = ver >= 2 ? r.ReadInt32() : 0;
                if (ver >= 4)
                {
                    c.Kind = r.ReadInt32();
                    c.Ups = new float[r.ReadInt32()];
                    for (int k = 0; k < c.Ups.Length; k++) c.Ups[k] = r.ReadSingle();
                    c.Downs = new float[r.ReadInt32()];
                    for (int k = 0; k < c.Downs.Length; k++) c.Downs[k] = r.ReadSingle();
                }
                c.Fps = r.ReadSingle();
                c.Frames = r.ReadInt32();
                int nb = r.ReadInt32();
                var names = new string[nb];
                for (int i = 0; i < nb; i++) names[i] = r.ReadString();
                c.BoneNames = names;
                c.Bones = new int[nb];
                for (int i = 0; i < nb; i++)
                {
                    Transform tr;
                    if (!bones.TryGetValue(names[i], out tr)) { c.Bones[i] = -1; continue; }
                    int ri;
                    if (!rigIndex.TryGetValue(names[i], out ri))
                    {
                        ri = rig.Count;
                        rig.Add(new RigBone { T = tr, Rest = rests[names[i]], RestLocal = tr.localRotation });
                        rigIndex[names[i]] = ri;
                    }
                    c.Bones[i] = ri;
                }
                c.Rot = new Quaternion[c.Frames * nb];
                c.W = new float[c.Frames * nb];
                for (int k = 0; k < c.Rot.Length; k++)
                {
                    var fw = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    var up = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    c.Rot[k] = Quaternion.LookRotation(fw, up);
                    c.W[k] = ver >= 3 ? r.ReadSingle() : 1f;
                }
                if (ver >= 5)
                {
                    c.ExtraN = r.ReadInt32();
                    c.Cycle = r.ReadSingle();
                    if (ver >= 6) { c.HipRatio = r.ReadSingle(); c.LegRatio = r.ReadSingle(); }
                    c.Extra = new float[c.ExtraN * c.Frames];
                    for (int k = 0; k < c.Extra.Length; k++) c.Extra[k] = r.ReadSingle();
                    // after the extras are read (v0.24.4-0.24.6 looked before reading: IndexOutOfRange, so no walk
                    // clip loaded at all and she walked the uncle's walk)
                    if (c.ExtraN >= 1)
                    {
                        int n = c.Frames - 1, e = c.ExtraN;
                        for (int k = 0; k < n; k++)
                            if (c.Extra[k * e] < 0.5f && c.Extra[(k + 1) * e] >= 0.5f) { c.Start = (k + 1f) / n; break; }
                    }
                }
                if (string.IsNullOrEmpty(c.Face)) c.Face = null;
                return c.Frames > 1 ? c : null;
            }
        }

        public bool Play(string name)
        {
            Clip c;
            if (name == null || !clips.TryGetValue(name, out c)) return false;
            StartXFade();
            cur = c;
            t = 0f;
            LoopClip = ThenFrom = null;
            Speed = 1f;
            return true;
        }

        // ---- task 79 (Narita): a held clip. LoopClip loops between frames LoopA..LoopB until Stop; ThenFrom, once it
        // reaches frame ThenAt, jumps to ThenClip at ThenFrame (look_up back to play_sit). Speed scales the clip time.
        public string LoopClip, ThenFrom, ThenClip;
        public float LoopA, LoopB, ThenAt, ThenFrame, Speed = 1f;
        public string CurName { get { return cur != null ? cur.Name : null; } }
        public float CurFrame { get { return cur != null ? t * cur.Fps : -1f; } }

        /// <summary>Jump to another clip at a frame, keeping the weight (no fade in): same arms in both clips.</summary>
        public bool Switch(string name, float frame)
        {
            Clip c;
            if (name == null || !clips.TryGetValue(name, out c)) return false;
            cur = c;
            t = Mathf.Max(FadeIn, frame / c.Fps);
            return true;
        }

        // task 78 bug 4: Switch / ThenClip jumps crossfade from the old clip's pose (frozen) over XFade seconds
        const float XFade = 0.35f;
        Clip xPrev;
        float xT, xLeft;

        void StartXFade()
        {
            if (cur == null) return;
            xPrev = cur; xT = t; xLeft = XFade;
        }

        static Quaternion Sample(Clip c, float tt, int i)
        {
            float ft = tt * c.Fps;
            int f0 = Mathf.Min((int)ft, c.Frames - 1), f1 = Mathf.Min(f0 + 1, c.Frames - 1);
            int nb = c.Bones.Length;
            return Quaternion.Slerp(c.Rot[f0 * nb + i], c.Rot[f1 * nb + i], ft - f0);
        }

        readonly List<string> order = new List<string>();
        int next;

        /// <summary>Clips in name order, one after another (debug key).</summary>
        public string Next()
        {
            if (order.Count != clips.Count) { order.Clear(); order.AddRange(clips.Keys); order.Sort(); }
            if (order.Count == 0) return null;
            return order[next++ % order.Count];
        }

        public float LengthOf(string name)
        {
            Clip c;
            return clips.TryGetValue(name, out c) ? c.Length : 0f;
        }

        /// <summary>Fade the current clip out now (she starts talking, gets a fright, leaves the car).</summary>
        public void Stop()
        {
            LoopClip = ThenFrom = null;
            Speed = 1f;
            if (cur != null) t = Mathf.Max(t, cur.Length - FadeOut);
        }

        /// <summary>Start of the frame: bones the clips touch go back to rest (not every one is copied from the uncle).</summary>
        public void ResetBones()
        {
            foreach (var b in rig) b.T.localRotation = b.RestLocal;
        }

        /// <summary>After the pose copy and posture.</summary>
        public void Apply(float dt)
        {
            if (cur == null) { Weight = 0f; return; }
            t += dt * Speed;
            if (ThenFrom != null && cur.Name == ThenFrom && t * cur.Fps >= ThenAt)
            {
                ThenFrom = null;
                Speed = 1f;
                Clip c2;
                if (ThenClip != null && clips.TryGetValue(ThenClip, out c2)) { StartXFade(); cur = c2; t = Mathf.Max(FadeIn, ThenFrame / c2.Fps); }
            }
            if (LoopClip != null && cur.Name == LoopClip && LoopB > LoopA)
            {
                float a0 = LoopA / cur.Fps, b0 = LoopB / cur.Fps;
                if (t >= b0) t = a0 + Mathf.Repeat(t - b0, b0 - a0);
            }
            float len = cur.Length;
            if (t >= len) { cur = null; Weight = 0f; return; }
            Weight = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(Mathf.Min(t / FadeIn, (len - t) / FadeOut)));

            float ft = t * cur.Fps;
            int f0 = Mathf.Min((int)ft, cur.Frames - 1), f1 = Mathf.Min(f0 + 1, cur.Frames - 1);
            float a = ft - f0;
            int nb = cur.Bones.Length;
            if (xLeft > 0f) xLeft -= dt;
            float xw = xLeft > 0f && xPrev != null ? Mathf.SmoothStep(0f, 1f, 1f - xLeft / XFade) : 1f;
            Quaternion hipDeform = hip.rotation * Quaternion.Inverse(hipRest);
            Quaternion frame = hipDeform;
            if (cur.Space == 1)
            {
                // bind space is Y up, Z forward: her facing (hips' forward, levelled) and the character's up
                Vector3 up = Up != null ? Up.up : Vector3.up;
                Vector3 fwd = Vector3.ProjectOnPlane(hipDeform * Vector3.forward, up);
                if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.ProjectOnPlane(hipDeform * Vector3.up, up);
                frame = Quaternion.LookRotation(fwd.normalized, up);
            }
            for (int i = 0; i < nb; i++)
            {
                int ri = cur.Bones[i];
                if (ri < 0) continue;
                var b = rig[ri];
                Quaternion d = Quaternion.Slerp(cur.Rot[f0 * nb + i], cur.Rot[f1 * nb + i], a);
                if (xw < 1f)
                {
                    int pi = System.Array.IndexOf(xPrev.Bones, ri);
                    if (pi >= 0) d = Quaternion.Slerp(Sample(xPrev, xT, pi), d, xw);
                }
                Quaternion target = frame * d * b.Rest;
                float bw = Mathf.Lerp(cur.W[f0 * nb + i], cur.W[f1 * nb + i], a);
                b.T.rotation = Quaternion.Slerp(b.T.rotation, target, Weight * bw);
            }
        }
    }
}
