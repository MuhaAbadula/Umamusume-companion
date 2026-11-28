using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Random = UnityEngine.Random;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 79 (concept 13): Narita Taishin. A character with the "trust" feature plays a handheld console most of the drive
    /// (clip play_sit held in its loop, prop props\console.obj on Wrist_R, screen glows at night), raises her head (look_up)
    /// on her own every 60-120 s (slider "NaritaDistract") and at once on border, town, crash, a long fast straight.
    /// A pat interrupts her game. At higher trust she turns to the driver sooner.
    /// Stage >= 3: now and then the console goes aside and she looks out of the window. Trust events -> Trust.cs.
    /// Log: [Umamusume companion] Narita: ..., [Umamusume companion] trust: ...
    /// </summary>
    public partial class Life
    {
        public Gestures Gest;
        public static float NaritaDistract = 1f;   // setting, 0..2
        public static float NaritaChat = 1f;       // setting, 0..2 (stage 5 talks)

        const string PlayClip = "play_sit", UpClip = "look_up";
        const float PlayA = 12f, PlayB = 162f, UpHoldA = 12f, UpHoldB = 102f;   // 102 = last hold frame (103 is already fading out)

        bool NaritaHere { get { return Trust.Here; } }
        /// <summary>Task 78, BALANCE.md "Кивает под радио": head-bob frequency x0.4 while she plays, x0.7 with the phone put away.</summary>
        const float NaritaBobPlay = 0.4f, NaritaBobFree = 0.7f;
        static void NLog(string s) { Debug.Log("[Umamusume companion] Narita: " + s); }

        float nPlayOkAt = -1f, nNextSelfUp = -1f, nLastEventUp = -100f, nNextAside = -1f, nAsideUntil = -1f;
        float nHuhAt = -1f, nPatLineAt = -1f, nFastFor;
        bool nFastDone, nBorderWas, nWasInCar, nTalkWas;
        int nHuhStage;
        GameObject nHub;
        bool nHubInit, nLegCrash;
        float nQuietFor, nLeftHour = -1f;
        bool nPlayerOut, nAbandonDone;

        bool NPlaying
        {
            // bug 6 (v0.51.7): no weight test. Her first frame's weight is SmoothStep(dt / 0.35) ~ 0.007 at 60 fps (< 0.01),
            // so the clip was restarted every frame ("plays the console" x3037 in the v0.51.6 log) and the arms never rose.
            get { return Gest != null && Gest.Playing && (Gest.CurName == PlayClip || Gest.CurName == UpClip); }
        }

        /// <summary>Trust: remark chance factor by stage (stage 1 says little).</summary>
        static readonly float[] TalkChance = { 0.35f, 0.55f, 0.75f, 0.9f, 1f };
        float TrustTalkK
        {
            get
            {
                int s = Trust.Stage;
                return s <= 0 ? 1f : TalkChance[s - 1];
            }
        }

        /// <summary>Doze only at stage 5 (or another character).</summary>
        bool TrustDozeOk { get { int s = Trust.Stage; return s == 0 || s >= 5; } }

        public static void OnCrashTrust(bool med)
        {
            if (!Trust.Here) return;
            Trust.Add(med ? Trust.CrashMed : Trust.CrashLight, med ? "medium crash" : "light crash");
            if (Current != null) { Current.nLegCrash = true; Current.NaritaUp("crash"); }
        }

        // ------------------------------------------------------------ per frame (from Update)
        void NaritaStep(float dt, float now, bool inCar, bool slp, bool talking, bool reacting)
        {
            if (!NaritaHere || logic == null || logic.uncleAtHome || logic.uncleGoneForever) { ConsoleShow(false); CancelFruit(); return; }
            TrustEvents(dt, now, inCar);
            ConsoleShow(!OfferingFruit && NPlaying && GestureWeight > 0.6f);

            if (inCar && !nWasInCar) { nPlayOkAt = now + Random.Range(5f, 15f); NextSelfUp(now); nNextAside = now + Random.Range(480f, 720f); }
            nWasInCar = inCar;

            // task 78 bug 3: a line no longer stops the clip (Life keeps it); she looks up from the screen for it, phone in hand
            if (talking && !nTalkWas && NPlaying) { NLog("talk while playing (stage " + Trust.Stage + "): looks up, phone stays"); StartUp(Random.Range(4f, 7f), "talk"); }
            nTalkWas = talking;

            if (OfferingFruit)
            {
                if (!inCar || slp || reacting || logic.restrictTalk || playerCtl == null || !playerCtl.isSat) CancelFruit();
                else TickFruit(now);
                return;
            }
            if (!inCar) nHuhAt = nPatLineAt = -1f;
            if (!inCar || slp || talking || reacting || (CanSay != null && !CanSay())) return;
            // A delayed pat response waits for a free bubble.
            if (nHuhAt >= 0f && now >= nHuhAt)
            {
                nHuhAt = -1f;
                if (StopGesture != null) StopGesture();
                nPlayOkAt = now + 8f;
                Attend(Look.Player, 3f, 1f);
                if (nHuhStage <= 3 || (nHuhStage == 4 && Random.value < 0.4f))
                {
                    if (Say != null) Say(nHuhStage == 4 ? "narita_huh_s4" : "narita_huh");
                    nPatLineAt = now + 3f;
                }
                else
                {
                    nPatLineAt = -1f;
                    NaritaPatLine(now);
                }
            }
            if (nPatLineAt >= 0f && now >= nPatLineAt) { nPatLineAt = -1f; NaritaPatLine(now); }

            NaritaTalks(now);
            if (OfferingFruit) return;

            // stage >= 3: console aside, a look out of the window
            if (Trust.Stage >= 3 && nNextAside >= 0f && now >= nNextAside && nAsideUntil < 0f)
            {
                float len = Random.Range(20f, 40f);
                nAsideUntil = now + len;
                nNextAside = now + Random.Range(480f, 720f);
                if (StopGesture != null && NPlaying) StopGesture();
                Attend(Look.Window, len, 0.8f);
                NLog("console aside, window " + len.ToString("F0") + " s (stage " + Trust.Stage + ")");
            }
            if (nAsideUntil >= 0f)
            {
                if (now < nAsideUntil) return;
                nAsideUntil = -1f;
                nPlayOkAt = now + 2f;
            }

            // stage 5 at night: stop playing when the doze timer is nearly full
            bool dozeSoon = Doze.Enabled && TrustDozeOk && night && drive >= Doze.Minutes * 60f - 10f;

            if (!NPlaying)
            {
                if (dozeSoon || beat != null || GestureWeight > 0.01f || now < nPlayOkAt || Gest == null) return;
                if (Gest.Play(PlayClip))
                {
                    Gest.LoopClip = PlayClip; Gest.LoopA = PlayA; Gest.LoopB = PlayB;
                    NLog("plays the console");
                }
                else { nPlayOkAt = now + 60f; NLog("no clip " + PlayClip); }
                return;
            }
            if (dozeSoon && Gest.CurName == PlayClip) { StopGesture(); nPlayOkAt = now + 120f; NLog("puts the console down: sleepy"); return; }

            // her own look up
            if (nNextSelfUp >= 0f && now >= nNextSelfUp) { NextSelfUp(now); StartUp(Random.Range(3f, 6f), "self"); }

            // long fast straight
            float sp = SpeedKmh();
            if (sp > 60f) nFastFor += dt; else if (sp < 50f) { nFastFor = 0f; nFastDone = false; }
            if (!nFastDone && nFastFor > 30f) { nFastDone = true; NaritaUp("long straight"); }
        }

        // ---- task 79 addition: her own lines by stage (texts: report 80)
        bool nThanksPending;
        int nGreetDay = -2, nChatDay = -2, nChats, nFruitDay = -2;
        float nNextChat = -1f, nNextFruit = -1f;

        void NaritaTalks(float now)
        {
            if (playerCtl == null || !playerCtl.isSat || Say == null || (CanSay != null && !CanSay())) return;
            int s = Trust.Stage, day = Luck.Day;
            if (day < 0) return;
            if (nThanksPending)
            {
                nThanksPending = false;
                if (s >= 3) { Say("narita_thanks_s" + s); return; }
            }
            // first line of the day, by stage
            if (nGreetDay != day) { nGreetDay = day; NLog("greet stage " + s); Say("narita_greet_s" + s); return; }
            // stages 4-5: a small talk of her own (BALANCE.md): st.4 2 a day, 30-50 min apart; st.5 3 a day, 20-40 min / NaritaChat
            if (nChatDay != day) { nChatDay = day; nChats = 0; nNextChat = now + Random.Range(600f, 1200f); }
            bool chatOn = s == 4 || (s >= 5 && NaritaChat > 0.01f);
            if (s >= 4 && chatOn && nChats < (s >= 5 ? 3 : 2) && nNextChat >= 0f && now >= nNextChat)
            {
                nChats++;
                nNextChat = now + (s >= 5 ? Random.Range(1200f, 2400f) / NaritaChat : Random.Range(1800f, 3000f));
                NLog("chat stage " + s + " (" + nChats + " today)");
                Say("narita_chat_s" + s);
                return;
            }
            // stages 4-5: dried fruit once a day, 15-30 min in, chance 70 %
            if (nFruitDay != day) { nFruitDay = day; nNextFruit = Random.value < 0.7f ? now + Random.Range(900f, 1800f) : -1f; }
            if (s >= 4 && nNextFruit >= 0f && now >= nNextFruit)
            {
                nNextFruit = -1f;
                BeginFruit(now);
                return;
            }
        }

        /// <summary>Base remark key -> narita_&lt;key&gt;_s&lt;stage&gt; when her file has it (pat_many, player_back).</summary>
        string NaritaKey(string key)
        {
            int s = Trust.Stage;
            if (s <= 0 || HasLine == null) return key;
            string k = "narita_" + key + "_s" + s;
            return HasLine(k) ? k : key;
        }

        void NextSelfUp(float now)
        {
            nNextSelfUp = NaritaDistract <= 0.01f ? -1f : now + Random.Range(60f, 120f) / NaritaDistract;
        }

        /// <summary>An event: head up at once for 4-8 s, 20 s apart.</summary>
        public void NaritaUp(string why)
        {
            if (!NaritaHere) return;
            float now = Time.time;
            if (now - nLastEventUp < 20f) { NLog("look up skipped (" + why + "): 20 s gap"); return; }
            if (!StartUp(Random.Range(4f, 8f), why)) return;
            nLastEventUp = now;
        }

        bool StartUp(float dur, string why)
        {
            if (Gest == null || Gest.CurName != PlayClip || GestureWeight < 0.5f) return false;
            float hold = (UpHoldB - UpHoldA) / 30f;
            if (!Gest.Switch(UpClip, UpHoldA)) return false;
            Gest.Speed = Mathf.Clamp(hold / Mathf.Max(0.5f, dur), 0.2f, 2f);
            Gest.ThenFrom = UpClip; Gest.ThenAt = UpHoldB; Gest.ThenClip = PlayClip; Gest.ThenFrame = PlayA;
            NLog("looks up " + dur.ToString("F1") + " s (" + why + ")");
            return true;
        }

        /// <summary>From CheckPat (awake, a hit). True = handled here (she was playing: answer later).</summary>
        bool NaritaPat(float now)
        {
            if (!NaritaHere) return false;
            Trust.OnPat();
            if (!NPlaying) return false;
            nHuhStage = Trust.Stage;
            float delay = nHuhStage <= 3 ? Random.Range(1.2f, 2f) : 0.6f;
            nHuhAt = now + delay;
            NLog("pat while playing, stage " + nHuhStage + ", answer in " + delay.ToString("F1") + " s");
            return true;
        }

        /// <summary>Her pat line by stage (stage 1: pushes the hand away - TODO clip).</summary>
        void NaritaPatLine(float now)
        {
            int s = Trust.Stage;
            if (s == 1) NLog("pushes the hand away (TODO clip)");
            if (Say != null) Say("narita_pat_s" + s);
        }

        /// <summary>Pat face: stages 1-2 annoyed instead of happy.</summary>
        string PatFaceName { get { int s = Trust.Stage; return s > 0 && s <= 2 ? "annoyed" : "happy"; } }

        // ------------------------------------------------------------ trust events from the world
        void TrustEvents(float dt, float now, bool inCar)
        {
            // border
            bool border = logic != null && logic.restrictTalk;
            if (border && !nBorderWas) NaritaUp("border");
            nBorderWas = border;

            // a new town hub = a leg done, a noisy town
            GameObject hub = route != null ? route.spawnedHub : null;
            if (!nHubInit) { if (route != null && Time.timeSinceLevelLoad > 5f) { nHub = hub; nHubInit = true; } }
            else if (hub != null && hub != nHub)
            {
                nHub = hub;
                if (!inCar) { nLegCrash = false; return; }
                Trust.Add(Trust.Leg, "leg together");
                if (!nLegCrash) Trust.Add(Trust.LegClean, "leg without crashes");
                Trust.Add(Trust.Town, "noisy town");
                if (Say != null) Say("narita_crowd");
                nLegCrash = false;
                NaritaUp("town");
            }
            else if (hub == null) nHub = null;

            // quiet night stop out of town: engine off >= 5 min, her in the car
            bool engine = car != null && car.engineOn;
            bool outOfTown = hub == null || car == null || Vector3.Distance(hub.transform.position, car.transform.position) > 300f;
            if (inCar && playerCtl != null && playerCtl.isSat && night && !engine && outOfTown && !Trust.QuietDone)
            {
                nQuietFor += dt;
                if (nQuietFor >= Trust.QuietEngineOff) { nQuietFor = 0f; Trust.OnQuietNight(); nThanksPending = Trust.Stage >= 3; }
            }
            else nQuietFor = 0f;

            // left at a stop: the player out of the car > 10 game minutes while she waits in it
            if (playerCtl != null && hourNow >= 0f)
            {
                bool outNow = !playerCtl.isSat;
                if (outNow && !nPlayerOut) { nLeftHour = hourNow; nAbandonDone = false; }
                nPlayerOut = outNow;
                if (outNow && inCar && !nAbandonDone && nLeftHour >= 0f)
                {
                    float mins = Mathf.Repeat(hourNow - nLeftHour, 24f) * 60f;
                    if (mins > Trust.AbandonGameMinutes) { nAbandonDone = true; Trust.Add(Trust.Abandoned, "left at a stop " + mins.ToString("F0") + " game min"); }
                }
            }
        }

        // ------------------------------------------------------------ the console prop
        GameObject console;
        Material screenMat;
        Shader screenDay, screenNight;
        bool consoleNight;

        /// <summary>Report 78: Wrist_R-relative, Blender pose bone frame, metres of the K-scaled rig (K 0.989).
        /// Unity bone frame = P^T B P (P: jalopy_common), so v -> (-x, z, -y), q (w,x,y,z) -> (x, -z, y, w); bind units = m x S / K.</summary>
        public void SetupConsole(Transform wristR, Dictionary<string, Transform> bones, Transform girlRoot, SkinnedMeshRenderer smr, string propsDir, float packScale)
        {
            if (!NaritaHere || wristR == null) return;
            cRoot = girlRoot;
            string obj = Path.Combine(propsDir, "console.obj");
            if (!File.Exists(obj)) { NLog("no console.obj"); return; }
            try
            {
                float f = (packScale > 0f ? packScale : 1.505f) / 0.989f;
                var mtl = ReadMtl(Path.Combine(propsDir, "console.mtl"));
                List<string> names;
                var mesh = ReadObj(obj, out names);
                console = new GameObject("FukuConsole");
                console.transform.SetParent(wristR, false);
                console.transform.localPosition = new Vector3(0.0016f, -0.0092f, -0.0584f) * f;   // report 78 Addition: the Uma phone
                console.transform.localRotation = new Quaternion(0.77799f, 0.0761f, 0.58676f, 0.2113f);
                console.transform.localScale = Vector3.one * f;
                console.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = console.AddComponent<MeshRenderer>();
                screenDay = Shader.Find("Legacy Shaders/Diffuse");
                screenNight = Shader.Find("Legacy Shaders/Self-Illumin/Diffuse") ?? Shader.Find("Unlit/Color");
                var mats = new Material[names.Count];
                for (int i = 0; i < names.Count; i++)
                {
                    Color c;
                    if (!mtl.TryGetValue(names[i], out c)) c = Color.gray;
                    mats[i] = new Material(screenDay) { name = "Fuku_" + names[i], color = c };
                    if (names[i] == "console_screen") screenMat = mats[i];
                }
                string png = Path.Combine(propsDir, "console.png");
                if (File.Exists(png))
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGB24, true) { name = "Fuku_console" };
                    if (tex.LoadImage(File.ReadAllBytes(png))) foreach (var m in mats) m.mainTexture = tex;
                }
                r.sharedMaterials = mats;
                console.SetActive(false);
                cWristR = wristR;
                // bug 6: her own bones from the pack's bone map. FindBone(wristR.root, ...) walked the uncle first, so "Head"
                // was the uncle's hidden head (the uncle rig has a bone of that name), and the root is the car when seated.
                bones.TryGetValue("Wrist_L", out cWristL);
                bones.TryGetValue("Middle_01_L", out cMidL);
                bones.TryGetValue("Middle_01_R", out cMidR);
                bones.TryGetValue("Head", out cHead);
                cScale = f;
                if (cRoot != null) console.transform.SetParent(cRoot, true);
                SetupHands(smr);
                NLog("console fit: " + (cWristL != null && cMidL != null && cMidR != null && cHead != null ? "both palms" : "fixed offset (bones missing)"));
                NLog("console on " + wristR.name + ", scale " + f.ToString("F3") + ", " + names.Count + " materials");
            }
            catch (Exception e) { NLog("console failed: " + e.Message); console = null; }
        }

        Transform cWristR, cWristL, cMidL, cMidR, cHead, cRoot;
        float cScale;

        static string V(Vector3 v) { return "(" + v.x.ToString("F3") + ", " + v.y.ToString("F3") + ", " + v.z.ToString("F3") + ")"; }

        /// <summary>Task 78 bug 1: the phone lies across both palms (landscape), screen to her face, from her hand bones
        /// (as narita_seat_preview.py --landscape 1).
        /// Bug 6 (v0.51.7): placed in world space right after the pose (FukuDriver.Pose -> ConsoleLate), on her root, not
        /// in Update from the last frame's bones through Wrist_R's local frame.
        /// Bug 8 (v0.51.9, "в руках, но слишком глубоко"): the height along the screen normal comes from her skinned hand
        /// mesh, palms AND fingers: the phone's bottom face goes over the highest hand vertex under the phone's footprint,
        /// + PhoneLiftCm (slider). Re-measured every 0.4 s while it shows, eased.</summary>
        void ConsoleFit()
        {
            if (cWristL == null || cMidL == null || cMidR == null || cHead == null) return;
            Vector3 grip = (cWristR.position + cWristL.position + cMidL.position + cMidR.position) * 0.25f;
            Vector3 toHead = cHead.position - grip;
            Vector3 a = cWristL.position - cWristR.position;
            if (a.sqrMagnitude < 1e-8f || toHead.sqrMagnitude < 1e-8f) return;
            a.Normalize();
            // bug 10 (v0.51.11): the phone lies FLAT on the plane of her hands (wrists -> middle knuckles), not tilted
            // toward her head: that normal was ~60 deg off the hands' plane, so one long edge sank into her fingers and
            // every lift along it went "по диагонали" (user, v0.51.10 red marker test).
            Vector3 f = 0.5f * (cMidL.position + cMidR.position - cWristL.position - cWristR.position);
            f -= a * Vector3.Dot(f, a);
            Vector3 n = Vector3.Cross(a, f);
            if (n.sqrMagnitude < 1e-8f) return;
            n.Normalize();
            if (Vector3.Dot(n, toHead) < 0f) n = -n;   // screen side up, toward her face
            a -= n * Vector3.Dot(a, n);
            a.Normalize();
            // mesh frame (ReadObj): screen = local +Y, top = local -Z -> top toward her left hand, screen up off her palms
            Quaternion w = Quaternion.LookRotation(-a, n);
            // the lift goes straight up her body (her root's up), not along a tilted normal
            Vector3 d = cRoot != null ? cRoot.up : Vector3.up;
            if (Vector3.Dot(d, n) < 0.3f) d = n;
            float s = Mathf.Abs(cWristR.lossyScale.x);   // world per bind unit
            float u = cScale * s;                        // world per phone-mesh unit (= metre of her)
            var ct = console.transform;
            if (Time.time >= cMeasureAt) { cMeasureAt = Time.time + 0.4f; MeasureLift(grip, w, n, d, toHead.normalized, u); }
            float target = cLiftT + PhoneLiftCm * 0.01f;
            cLiftCur = cLiftCur < -1f ? target : Mathf.MoveTowards(cLiftCur, target, 0.15f * Time.deltaTime);   // 15 cm/s of her
            ct.position = grip + d * (cLiftCur * u);
            ct.rotation = w;
            float ps = ct.parent != null ? Mathf.Abs(ct.parent.lossyScale.x) : 1f;
            ct.localScale = Vector3.one * (u / Mathf.Max(ps, 1e-6f));

        }

        public static float PhoneLiftCm = 0f;

        // ---- bug 7/8: her hand vertices (dominant weight on Wrist_L/R or any bone under them: fingers), skinned on the CPU
        SkinnedMeshRenderer cSmr;
        Vector3[] cHandV;
        BoneWeight[] cHandW;
        bool[] cHandFinger;
        Matrix4x4[] cBind, cSkin;
        bool[] cBoneUsed;
        float cLiftT, cLiftCur = -2f, cMeasureAt;
        Bounds cPhoneB;
        int cLiftLogs;

        void SetupHands(SkinnedMeshRenderer smr)
        {
            cSmr = smr;
            cPhoneB = console.GetComponent<MeshFilter>().sharedMesh.bounds;   // phone mesh units = metres of her
            cLiftT = -cPhoneB.min.y;   // until measured: bottom face on the bone grip
            if (smr == null || smr.sharedMesh == null || cWristL == null) { NLog("console lift: no mesh"); return; }
            var bs = smr.bones;
            int il = Array.IndexOf(bs, cWristL), ir = Array.IndexOf(bs, cWristR);
            if (il < 0 || ir < 0) { NLog("console lift: wrists not in the mesh bones"); return; }
            var hand = new bool[bs.Length];
            var finger = new bool[bs.Length];
            int nb = 0;
            for (int b = 0; b < bs.Length; b++)
            {
                if (bs[b] == null) continue;
                hand[b] = bs[b].IsChildOf(cWristL) || bs[b].IsChildOf(cWristR);   // IsChildOf is true for the wrist itself
                finger[b] = hand[b] && b != il && b != ir;
                if (hand[b]) nb++;
            }
            var mesh = smr.sharedMesh;
            var v = mesh.vertices;
            var bw = mesh.boneWeights;
            cBind = mesh.bindposes;
            cSkin = new Matrix4x4[bs.Length];
            cBoneUsed = new bool[bs.Length];
            var lv = new List<Vector3>(); var lw = new List<BoneWeight>(); var lf = new List<bool>();
            for (int i = 0; i < v.Length && i < bw.Length; i++)
            {
                var w = bw[i];
                if (w.weight0 < 0.5f || w.boneIndex0 >= hand.Length || !hand[w.boneIndex0]) continue;
                lv.Add(v[i]); lw.Add(w); lf.Add(finger[w.boneIndex0]);
                cBoneUsed[w.boneIndex0] = true; cBoneUsed[w.boneIndex1] = true; cBoneUsed[w.boneIndex2] = true; cBoneUsed[w.boneIndex3] = true;
            }
            cHandV = lv.ToArray(); cHandW = lw.ToArray(); cHandFinger = lf.ToArray();
            int nf = 0; foreach (bool f in cHandFinger) if (f) nf++;
            NLog("console lift: " + cHandV.Length + " hand vertices (" + nf + " on fingers, " + nb + " hand bones), phone mesh " + V(cPhoneB.size) + " m");
        }

        Vector3 SkinV(int i)
        {
            var w = cHandW[i];
            Vector3 p = cHandV[i], r = Vector3.zero;
            if (w.weight0 > 0f) r += w.weight0 * cSkin[w.boneIndex0].MultiplyPoint3x4(p);
            if (w.weight1 > 0f) r += w.weight1 * cSkin[w.boneIndex1].MultiplyPoint3x4(p);
            if (w.weight2 > 0f) r += w.weight2 * cSkin[w.boneIndex2].MultiplyPoint3x4(p);
            if (w.weight3 > 0f) r += w.weight3 * cSkin[w.boneIndex3].MultiplyPoint3x4(p);
            float sum = w.weight0 + w.weight1 + w.weight2 + w.weight3;
            return sum > 1e-4f ? r / sum : r;
        }

        /// <summary>Phone origin height over the bone grip along n (phone-mesh units) so that its bottom face clears every
        /// hand vertex (palm or finger) that lies under the phone (its footprint + 1 cm). None under it: clear all of them.</summary>
        void MeasureLift(Vector3 grip, Quaternion w, Vector3 n, Vector3 d, Vector3 toHead, float u)
        {
            if (cHandV == null || cHandV.Length == 0 || cSmr == null) return;
            var bs = cSmr.bones;
            for (int b = 0; b < bs.Length; b++) if (cBoneUsed[b] && bs[b] != null) cSkin[b] = bs[b].localToWorldMatrix * cBind[b];
            Quaternion inv = Quaternion.Inverse(w);
            Vector3 c = cPhoneB.center, e = cPhoneB.extents;
            const float m = 0.01f;
            var qs = new Vector3[cHandV.Length];
            for (int i = 0; i < cHandV.Length; i++) qs[i] = inv * (SkinV(i) - grip) / u;   // phone frame at lift 0, metres of her
            Vector3 dl = inv * d;   // the lift direction in the phone frame; dl.y = how much 1 m of lift raises the bottom face
            float dy = Mathf.Max(dl.y, 0.3f);
            float topIn = float.MinValue, topAll = float.MinValue, topPalm = float.MinValue, low = float.MaxValue, t = 0f;
            int nIn = 0, nInF = 0;
            // the phone slides a little sideways when lifted along d (d is not exactly its normal): footprint at the new lift, 3 passes
            for (int pass = 0; pass < 3; pass++)
            {
                topIn = topAll = topPalm = float.MinValue; low = float.MaxValue; nIn = nInF = 0;
                Vector3 off = dl * t;
                for (int i = 0; i < qs.Length; i++)
                {
                    Vector3 q = qs[i] - new Vector3(off.x, 0f, off.z);   // height stays measured from the bone grip
                    topAll = Mathf.Max(topAll, q.y);
                    low = Mathf.Min(low, q.y);
                    if (!cHandFinger[i]) topPalm = Mathf.Max(topPalm, q.y);
                    if (Mathf.Abs(q.x - c.x) <= e.x + m && Mathf.Abs(q.z - c.z) <= e.z + m)
                    {
                        nIn++; if (cHandFinger[i]) nInF++;
                        topIn = Mathf.Max(topIn, q.y);
                    }
                }
                float topN = nIn > 0 ? topIn : topAll;
                t = Mathf.Clamp((topN - cPhoneB.min.y + 0.003f) / dy, 0f, 0.15f);   // bottom face 3 mm over the hand, at most 15 cm
            }
            cLiftT = t;
            float top = nIn > 0 ? topIn : topAll;
            if (cLiftLogs++ < 3)
                NLog("console lift: phone flat on her hands' plane; plane normal vs body up " + Vector3.Angle(n, d).ToString("F0") + " deg, vs to-head (old screen normal) "
                    + Vector3.Angle(n, toHead).ToString("F0") + " deg; lift straight up her body. Cm of her over the bone grip: hands under the phone " + nIn + " (" + nInF
                    + " finger) top " + (nIn > 0 ? (topIn * 100f).ToString("F1") : "-") + ", all hand top " + (topAll * 100f).ToString("F1")
                    + " (palms only " + (topPalm * 100f).ToString("F1") + "), lowest " + (low * 100f).ToString("F1")
                    + " -> lift up " + (cLiftT * 100f).ToString("F1") + " + slider " + PhoneLiftCm.ToString("F0")
                    + ", phone bottom face " + (-cPhoneB.min.y * 100f).ToString("F1") + " under its centre");
        }

        public void ConsoleLate()
        {
            if (console != null && console.activeSelf) ConsoleFit();
        }

        void ConsoleShow(bool on)
        {
            if (console == null) return;
            if (console.activeSelf != on)
            {
                console.SetActive(on);
                if (on) { cLiftCur = -2f; cMeasureAt = 0f; ConsoleFit(); }
            }
            if (on && screenMat != null && night != consoleNight && screenNight != null)
            {
                consoleNight = night;
                screenMat.shader = night ? screenNight : screenDay;
                NLog("screen " + (night ? "glows (night)" : "day"));
            }
        }

        static Dictionary<string, Color> ReadMtl(string path)
        {
            var d = new Dictionary<string, Color>();
            if (!File.Exists(path)) return d;
            string cur = null;
            foreach (var raw in File.ReadAllLines(path))
            {
                var p = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2) continue;
                if (p[0] == "newmtl") cur = p[1];
                else if (p[0] == "Kd" && cur != null && p.Length >= 4)
                    d[cur] = new Color(F(p[1]), F(p[2]), F(p[3]), 1f);
            }
            return d;
        }

        static float F(string s) { return float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture); }

        /// <summary>OBJ in the console's Blender local frame: v -> (-x, z, -y), winding reversed (det P = -1); a submesh per usemtl.</summary>
        static Mesh ReadObj(string path, out List<string> names)
        {
            var v = new List<Vector3>();
            var pos = new List<Vector3>();
            var vt = new List<Vector2>();
            var uv = new List<Vector2>();
            var subs = new List<List<int>>();
            names = new List<string>();
            List<int> curTris = null;
            foreach (var raw in File.ReadAllLines(path))
            {
                var p = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2) continue;
                if (p[0] == "vt") vt.Add(new Vector2(F(p[1]), F(p[2])));
                else if (p[0] == "v") v.Add(new Vector3(-F(p[1]), F(p[3]), -F(p[2])));
                else if (p[0] == "usemtl")
                {
                    int i = names.IndexOf(p[1]);
                    if (i < 0) { names.Add(p[1]); subs.Add(new List<int>()); i = names.Count - 1; }
                    curTris = subs[i];
                }
                else if (p[0] == "f")
                {
                    if (curTris == null) { names.Add("console_body"); subs.Add(curTris = new List<int>()); }
                    var idx = new int[p.Length - 1];
                    for (int i = 1; i < p.Length; i++)
                    {
                        var c3 = p[i].Split('/');
                        int a = int.Parse(c3[0], CultureInfo.InvariantCulture);
                        int b = c3.Length > 1 && c3[1].Length > 0 ? int.Parse(c3[1], CultureInfo.InvariantCulture) : 0;
                        uv.Add(b != 0 ? vt[b < 0 ? vt.Count + b : b - 1] : Vector2.zero);
                        idx[i - 1] = pos.Count;
                        pos.Add(v[a < 0 ? v.Count + a : a - 1]);
                    }
                    for (int i = 1; i + 1 < idx.Length; i++) { curTris.Add(idx[0]); curTris.Add(idx[i + 1]); curTris.Add(idx[i]); }
                }
            }
            var m = new Mesh { name = "Fuku_console" };
            m.vertices = pos.ToArray();
            m.uv = uv.ToArray();
            m.subMeshCount = subs.Count;
            for (int i = 0; i < subs.Count; i++) m.SetTriangles(subs[i], i);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
