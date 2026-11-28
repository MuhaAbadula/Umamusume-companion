using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace UmamusumeCompanion
{
    public enum DozePhase { Awake, Yawn, Drowse, Sleep, Wake }

    /// <summary>
    /// She dozes off on a long night drive and wakes by cause (concept design/concepts/01_doze.md). Own flag, not the
    /// game's Z-particle flag (<see cref="Life"/>.asleepNow). Yawn, Drowse and Sleep gate everything she does on her own;
    /// <see cref="Muted"/> silences every remark (Remarks.Say) except the doze lines themselves.
    /// The state machine lives in Life (partial, below); this class holds settings, the shared phase and the Harmony hooks.
    /// </summary>
    public static class Doze
    {
        // settings (CompanionMod.ApplySettings)
        public static bool Enabled = true;
        public static float Minutes = 6f;
        public static int NightFrom = 22, NightTo = 5;

        public static DozePhase Phase = DozePhase.Awake;
        /// <summary>Task 39 prank N7: the doze is a pretence (straight to Sleep, no mumbles; every wake is silent, PrankBoo.cs).</summary>
        public static bool Fake;
        /// <summary>True while a doze line is being started: Remarks lets it through the mute.</summary>
        public static bool Speaking;

        /// <summary>Eyes shut or going shut: Drowse and Sleep.</summary>
        public static bool IsAsleep { get { return Phase == DozePhase.Drowse || Phase == DozePhase.Sleep; } }
        /// <summary>Falling asleep or asleep: her own remarks, gestures, waving etc. are off.</summary>
        public static bool Gate { get { return Phase == DozePhase.Yawn || Phase == DozePhase.Drowse || Phase == DozePhase.Sleep; } }
        public static bool Muted { get { return Gate && !Speaking; } }

        public static bool InNight(float h)
        {
            if (NightFrom == NightTo) return false;
            return NightFrom < NightTo ? h >= NightFrom && h < NightTo : h >= NightFrom || h < NightTo;
        }

        static bool applied;

        public static void Apply()
        {
            if (applied) return;
            var h = new Harmony("osobl.jalopy.umamusumecompanion.doze");
            foreach (var n in new[] { "PotHoleHitFLWheel", "PotHoleHitFRWheel", "PotHoleHitRLWheel", "PotHoleHitRRWheel" })
            {
                var m = AccessTools.Method(typeof(CarPerformanceC), n);
                if (m != null) h.Patch(m, postfix: new HarmonyMethod(typeof(Doze), nameof(PotHole)));
                else Debug.LogWarning("[Umamusume companion] Doze: " + n + " not found, that wheel will not wake her");
            }
            applied = true;
        }

        static Life L { get { return Life.Current != null && Life.Current.Enabled ? Life.Current : null; } }

        static void PotHole() { if (Gate && L != null) L.DozePothole(); }
        public static void OnCrash() { if (Gate && L != null) L.DozeWake("crash"); }
        public static void OnG() { if (Gate && L != null) L.DozeWake("g"); }
        public static string DebugToggle() { return L != null ? L.DozeDebugToggle() : "Doze: she is not here."; }
    }

    public partial class Life
    {
        // read by the driver
        public float LidsTarget;            // eyes shut 0..1 (the driver eases towards it)
        public float LidsTime = 0.6f;       // seconds the lids need for the whole way
        public float BlinkScale = 1f;       // < 1 = blinks come sooner (drowsy after waking)
        /// <summary>Say a line of the story file by key (set by the driver); false = no such line.</summary>
        public Func<string, bool> StorySay;
        /// <summary>Her own remark bubble is up (set by the driver): not a game dialogue.</summary>
        public Func<bool> RemarkBusy;

        bool dozeG;                          // Doze.Gate this frame
        float dozeT, dozeLen, drive, slowFor, nextMumble, drowsyUntil = -1f, potholeAt = -100f, patLineAt = -100f;
        bool dozedThisNight, dozeNight, dialogWas;
        float dialogWakeAt = -1f, hourNow = -1f;
        readonly List<Vector2> speedLog = new List<Vector2>();   // (time, km/h) of the last second, for hard braking
        string lineKey;
        float lineAt, lineUntil;
        bool lineQuiet;

        const float YawnLen = 4f, WakeLen = 2.5f;   // 4 s: the hand goes to the mouth and back (2.8 s was rushed)

        void DozeLog(string what) { Debug.Log("[Umamusume companion] Doze: " + what); }

        void DozeSet(DozePhase p, string why)
        {
            Doze.Phase = p;
            dozeT = 0f;
            DozeLog(p.ToString().ToLowerInvariant() + (why != null ? " " + why : ""));
        }

        // ------------------------------------------------------------ the state machine (every frame, from Update)
        void DozeStep(float now, float dt, bool inCar, bool asleep, bool talking)
        {
            if (playerCtl == null && logic != null && logic.player != null) playerCtl = logic.player.GetComponent<RigidbodyControllerC>();
            float speed = SpeedKmh();
            bool seated = inCar && playerCtl != null && playerCtl.isSat;
            if (Doze.Fake && !Doze.Gate) { Doze.Fake = false; DozeLog("fake flag cleared (the doze ended elsewhere)"); }
            dozeNight = hourNow >= 0f && Doze.InNight(hourNow);
            if (!dozeNight) dozedThisNight = false;

            // drive timer: above 20 km/h counts, under 20 for 10 s (a parking, not a red light) or the player out resets it
            if (!seated || !dozeNight) { drive = 0f; slowFor = 0f; }
            else if (speed > 20f) { drive += dt; slowFor = 0f; }
            else if ((slowFor += dt) >= 10f) drive = 0f;
            if (dozePrime && seated && dozeNight && speed > 20f)
            {
                dozePrime = false;
                dozedThisNight = false;
                drive = Mathf.Max(drive, Doze.Minutes * 60f - 3f);
                DozeLog("debug: drive timer filled, yawn in ~3 s");
            }

            // speed of the last second (hard braking)
            speedLog.Add(new Vector2(now, speed));
            while (speedLog.Count > 0 && now - speedLog[0].x > 1f) speedLog.RemoveAt(0);

            var u = logic;
            bool border = u != null && u.restrictTalk;
            bool motel = u != null && (u.withinRangeOfMotel || u.hasSaidLetsCheckIn);
            bool dialog = DozeDialog(u);
            bool dialogEdge = dialog && !dialogWas;
            dialogWas = dialog;

            switch (Doze.Phase)
            {
                case DozePhase.Awake:
                    if (Doze.Enabled && TrustDozeOk && dozeNight && !dozedThisNight && seated && !asleep && !talking && !dialog && !border && !motel
                        && drive >= Doze.Minutes * 60f && GestureWeight <= 0.01f && reactT < 0f
                        && (CanSay == null || CanSay()))
                        DozeBegin("timer");
                    break;

                case DozePhase.Wake:
                    dozeT += dt;
                    if (dozeT >= WakeLen) { Doze.Phase = DozePhase.Awake; LidsTime = 0.6f; DozeLog("awake"); }
                    break;

                default:
                    if (!inCar || asleep) { DozeWake(asleep ? "gamesleep" : "out"); break; }
                    if (motel) { DozeWake("motel"); break; }
                    if (border) { DozeWake("border"); break; }
                    if (dialogEdge) dialogWakeAt = now + 0.3f;   // a crash line arrives with its own crash wake: that one wins
                    if (dialogWakeAt >= 0f && now >= dialogWakeAt)
                    {
                        dialogWakeAt = -1f;
                        if (dialog) { DozeWake("dialog"); break; }
                    }
                    if (speedLog.Count > 1)
                    {
                        float peak = 0f;
                        foreach (var s in speedLog) peak = Mathf.Max(peak, s.y);
                        if (peak - speed > 25f) { DozeWake("brake"); break; }
                    }
                    if (Doze.Fake && BooStep(now, dt, seated)) break;   // task 39: his look, the 40 s timeout
                    DozeAdvance(now, dt);
                    break;
            }
            dozeG = Doze.Gate;
            if (Doze.Phase == DozePhase.Awake) { LidsTarget = 0f; BlinkScale = now < drowsyUntil ? 0.4f : 1f; }
            DozeLines(now);
        }

        void DozeAdvance(float now, float dt)
        {
            dozeT += dt;
            switch (Doze.Phase)
            {
                case DozePhase.Yawn:
                    LidsTarget = 0f;
                    if (dozeT >= YawnLen)
                    {
                        DozeSet(DozePhase.Drowse, null);
                        DozeLine("doze_fall", 0.2f, 8f, false);
                    }
                    break;
                case DozePhase.Drowse:
                    LidsTime = 0.1f;                 // the driver follows our curve: heavy lids, slow blinks, nods
                    LidsTarget = DrowseLids();
                    if (dozeT >= dozeLen)
                    {
                        DozeSet(DozePhase.Sleep, null);
                        LidsTime = 0.6f;
                        nextMumble = now + UnityEngine.Random.Range(90f, 180f);
                    }
                    break;
                case DozePhase.Sleep:
                    LidsTarget = 1f;
                    if (!Doze.Fake && now >= nextMumble)
                    {
                        nextMumble = now + UnityEngine.Random.Range(90f, 180f);
                        DozeLine("doze_mumble", 0f, 10f, true);
                    }
                    break;
            }
        }

        void DozeBegin(string reason)
        {
            dozedThisNight = true;
            drive = 0f;
            dozeLen = UnityEngine.Random.Range(17f, 27f);   // yawn + drowse = 20-30 s
            speedLog.Clear();
            if (beat != null) beat = null;
            if (GestureWeight > 0f && StopGesture != null) StopGesture();
            DozeSet(DozePhase.Yawn, reason);
        }

        /// <summary>
        /// Task 39 (prank N7): straight into the sleep pose (lids 0.6 s, head to the window over ~0.9 s), no yawn, no drowse,
        /// no mumbles, nothing of the night doze touched. Called from the prank's Act inside her beat: the beat is not cleared
        /// here (it ends itself next frame, when the gate stops her beats).
        /// </summary>
        void DozeFake()
        {
            speedLog.Clear();
            dialogWakeAt = -1f;
            lineKey = null;
            Doze.Fake = true;
            DozeSet(DozePhase.Sleep, "fake (prank)");
            LidsTime = 0.6f;
        }

        /// <summary>Ends the fake doze silently: eyes open in lidsTime, the head comes up with the fast sit-up (dozeW, 0.35 s).
        /// No line, no flinch, no ears, no drowsy face after.</summary>
        void DozeFakeEnd(string why, float lidsTime)
        {
            Doze.Fake = false;
            if (!Doze.Gate) return;
            DozeSet(DozePhase.Wake, "fake " + why);
            dialogWakeAt = -1f;
            lineKey = null;
            LidsTarget = 0f;
            LidsTime = lidsTime;
            speedLog.Clear();
            dozeG = false;
        }

        /// <summary>
        /// Wakes her (from Yawn, Drowse or Sleep). Silent for the game's own talk and the border, else a line by cause.
        /// </summary>
        public void DozeWake(string reason)
        {
            if (!Doze.Gate) return;
            if (Doze.Fake) { BooWoken(reason); return; }   // task 39: she was only pretending: silent
            float now = Time.time;
            string key = null;
            switch (reason)
            {
                case "pothole":
                case "brake":
                case "debug": key = "doze_wake_bump"; break;
                case "crash": key = "doze_wake_crash"; break;
                case "thunder": key = "doze_wake_thunder"; break;
                case "motel": key = "doze_wake_stop"; break;
                case "g": key = SpeedKmh() < 5f ? "doze_wake_stop" : "doze_wake_bump"; break;
            }
            DozeSet(DozePhase.Wake, reason);
            dialogWakeAt = -1f;
            LidsTarget = 0f;
            LidsTime = 0.12f;
            drowsyUntil = now + UnityEngine.Random.Range(120f, 180f);
            lineKey = null;
            speedLog.Clear();
            flinchT = 0f;                                  // the thunder flinch: head ducks, shoulders hunch
            React("surprise", 0.9f, "worried", 1.2f);
            earMoves.Add(new EarMove { Name = "fwd_l", Start = now, In = 0.08f, Hold = 1.2f, Out = 0.6f, W = 1f });
            earMoves.Add(new EarMove { Name = "fwd_r", Start = now, In = 0.08f, Hold = 1.2f, Out = 0.6f, W = 1f });
            // G: the outing starts right after this call and would put its own remark in the bubble first, so speak now
            bool now0 = reason == "g";
            if (key != null) DozeLine(key, now0 ? 0f : 0.9f, reason == "crash" ? 4f : 5f, false);
            dozeG = false;
            if (key != null && now0) DozeLines(now);
        }

        public void DozePothole()
        {
            float now = Time.time;
            if (now - potholeAt < 1f) return;
            potholeAt = now;
            if (SpeedKmh() > 25f) DozeWake("pothole");
        }

        bool dozePrime;

        /// <summary>Debug panel: the night drive timer fills up as soon as you drive above 20 km/h at night.</summary>
        public void DozeDebugPrime() { dozePrime = true; dozedThisNight = false; }

        /// <summary>Ctrl+F8: asleep at once / awake at once.</summary>
        public string DozeDebugToggle()
        {
            if (Doze.Gate) { DozeWake("debug"); return "Doze: wake up."; }
            if (Doze.Phase == DozePhase.Wake) return "Doze: she is just waking.";
            if (!inCarNow) return "Doze: she is not in the car.";
            DozeBegin("debug");
            dozedThisNight = false;   // a test must not use up the night
            return "Doze: she falls asleep.";
        }

        // ------------------------------------------------------------ face and head, after the idle branch of Update
        void DozeApply(float now)
        {
            dozeWant = yawnHandWant = nodPitch = 0f;
            BooFace(now);   // task 39: the "Бу!" face right after she sits up
            switch (Doze.Phase)
            {
                case DozePhase.Yawn:
                    float env = Mathf.Clamp01(Mathf.Min(dozeT / 0.6f, (YawnLen - dozeT) / 0.7f));
                    Face.Want("yawn", env, 0.3f);
                    curPitch = -10f * Mathf.Sin(Mathf.Clamp01(dozeT / YawnLen) * Mathf.PI);
                    curLook = Look.Road;
                    curLookW = 0.3f;
                    // the hand comes up as the mouth opens and stays until it closes
                    yawnHandWant = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(Mathf.Min((dozeT - 0.3f) / 0.7f, (YawnLen - 0.2f - dozeT) / 0.8f)));
                    break;
                case DozePhase.Drowse:
                {
                    // heavy-eyed face gives way to the sleeping one as the lids close (half-shut eye shapes under
                    // fully shut lids overshoot)
                    float p = Mathf.Clamp01(dozeT / dozeLen), lids = Mathf.Clamp01(LidsShut);
                    Face.Want("sleepy", (0.5f + 0.5f * p) * (1f - lids), 0.4f);
                    Face.Want("asleep", lids, 0.4f);
                    curLook = Look.Road;
                    curLookW = 0.15f;
                    dozeWant = 0.45f * Mathf.SmoothStep(0f, 1f, (p - 0.45f) / 0.55f);   // the head starts to sink halfway
                    float nodLid;
                    int nod;
                    nodPitch = Nod(out nodLid, out nod);
                    if (nod != nodLogged)
                    {
                        nodLogged = nod;
                        if (nod > 0) DozeLog("pose nod " + nod);
                    }
                    if (nod > 0 && dozeT - NodStart(nod) >= 1.8f && !nodFlicked)
                    {
                        nodFlicked = true;                  // jerks back up: the ears flick
                        Flick("twitch_l", now, 0.9f);
                        Flick("twitch_r", now + 0.04f, 0.9f);
                    }
                    if (nod == 0) nodFlicked = false;
                    break;
                }
                case DozePhase.Sleep:
                    Face.Want("asleep", 1f, 0.8f);
                    curLook = Look.Road;
                    curLookW = 0f;
                    dozeWant = 1f;
                    break;
                case DozePhase.Awake:
                    if (now < drowsyUntil)
                        Face.Want("sleepy", 0.7f * Mathf.Clamp01((drowsyUntil - now) / 30f), 1f);   // heavy eyes, fading over the last 30 s
                    break;
            }
            string pose = Doze.Phase == DozePhase.Awake ? (dozeW > 0.001f ? poseLogged : "off") : Doze.Phase.ToString().ToLowerInvariant();
            if (pose != poseLogged)
            {
                if (poseLogged != null || pose != "off") DozeLog("pose " + pose);
                poseLogged = pose;
            }
        }

        // ------------------------------------------------------------ pose (task 04): wanted in DozeApply, drawn in DozePose
        float dozeWant, dozeW, dozeWVel;     // slumped to her window, 0..1
        float yawnHandWant, yawnHandW;       // hand at the mouth while yawning
        float nodPitch, nodNow, nodVel;      // head nod, degrees (+ = chin down)
        float dozeSide;                      // her window along her right vector (+1/-1), latched while the pose is on
        float breathPh, breathS;             // breathing phase (turns) and its sine
        int nodLogged;
        bool nodFlicked, handLogged;
        string poseLogged;
        Transform shoulderL, shoulderR;
        Quaternion shoulderLRest, shoulderRRest;

        const float NodLen = 3f;

        /// <summary>Called from the constructor: shoulder bones for the breathing (optional).</summary>
        void DozeInit(Dictionary<string, Transform> bones)
        {
            if (bones.TryGetValue("Shoulder_L", out shoulderL)) shoulderLRest = shoulderL.localRotation;
            if (bones.TryGetValue("Shoulder_R", out shoulderR)) shoulderRRest = shoulderR.localRotation;
        }

        /// <summary>Start of the frame (ResetBones): the shoulders are not copied from the uncle.</summary>
        void DozeResetBones()
        {
            if (shoulderL != null) shoulderL.localRotation = shoulderLRest;
            if (shoulderR != null) shoulderR.localRotation = shoulderRRest;
        }

        /// <summary>Two nods while drowsing, at 28 % and 58 % of the drowse (dozeLen 17-27 s: they never overlap).</summary>
        float NodStart(int i) { return dozeLen * (i == 1 ? 0.28f : 0.58f); }

        /// <summary>The head sinks over 1.8 s with the lids, jerks up in 0.3 s with the eyes open, settles in 0.9 s.</summary>
        float Nod(out float lid, out int which)
        {
            lid = 0f;
            which = 0;
            for (int i = 1; i <= 2; i++)
            {
                float u = dozeT - NodStart(i);
                if (u < 0f || u >= NodLen) continue;
                which = i;
                if (u < 1.8f) { float s = Mathf.SmoothStep(0f, 1f, u / 1.8f); lid = s; return 20f * s; }
                if (u < 2.1f) { float k = Mathf.SmoothStep(0f, 1f, (u - 1.8f) / 0.3f); lid = 1f - k; return Mathf.Lerp(20f, -4f, k); }
                float k2 = Mathf.SmoothStep(0f, 1f, (u - 2.1f) / 0.9f);
                lid = -k2;                                  // negative: "back to the heavy lids by this much"
                return Mathf.Lerp(-4f, 0f, k2);
            }
            return 0f;
        }

        /// <summary>Drowse lids: they sink, a slow blink every ~4 s, the nods, and shut for good over the last 18 %.</summary>
        float DrowseLids()
        {
            float p = Mathf.Clamp01(dozeT / dozeLen);
            float heavy = 0.12f + 0.5f * Mathf.SmoothStep(0f, 1f, p);
            float nodLid;
            int nod;
            Nod(out nodLid, out nod);
            if (nod > 0)
            {
                float u = dozeT - NodStart(nod);
                if (u < 1.8f) return Mathf.Max(heavy, nodLid);
                return nodLid >= 0f ? nodLid : heavy * -nodLid;   // wide open with the jerk, then heavy again
            }
            float bt = Mathf.Repeat(dozeT, 4.2f) - 2.5f;               // slow blink: 0.35 s shut, 0.3 s held, 0.55 s open
            float blink = bt < 0f ? 0f : bt < 0.35f ? bt / 0.35f : bt < 0.65f ? 1f : bt < 1.2f ? 1f - (bt - 0.65f) / 0.55f : 0f;
            float close = Mathf.SmoothStep(0f, 1f, (p - 0.82f) / 0.18f);
            return Mathf.Max(heavy, Mathf.Max(blink, close));
        }

        /// <summary>Breathing rate and depth: slower and deeper as she sinks into sleep.</summary>
        float BreathStep(float dt)
        {
            float hz = asleepNow ? 0.2f : Mathf.Lerp(0.27f, 0.17f, dozeW), amp = asleepNow ? 2f : Mathf.Lerp(1.1f, 2.4f, dozeW);
            breathPh = Mathf.Repeat(breathPh + dt * hz, 1f);              // a phase, so a rate change never jumps
            breathS = Mathf.Sin(breathPh * Mathf.PI * 2f);
            return breathS * amp;
        }

        /// <summary>
        /// From PoseBones, after the head look: the sleep slump (chest, neck, head to her window, chin down), nods,
        /// shoulders with the breath, the hand to the mouth while yawning. Hands on the lap = her seated arms.
        /// </summary>
        void DozePose(float dt)
        {
            bool on = Enabled && inCarNow;
            float want = on ? dozeWant : 0f;
            dozeW = Mathf.Clamp01(Mathf.SmoothDamp(dozeW, want, ref dozeWVel, want > dozeW ? 0.9f : 0.35f, Mathf.Infinity, dt));
            nodNow = Mathf.SmoothDamp(nodNow, on ? nodPitch : 0f, ref nodVel, 0.06f, Mathf.Infinity, dt);
            yawnHandW = Mathf.MoveTowards(yawnHandW, on ? yawnHandWant : 0f, dt / 0.35f);
            if (yawnHandW <= 0.001f) handLogged = false;
            if (dozeW < 0.001f && Mathf.Abs(nodNow) < 0.01f && yawnHandW <= 0.001f) { dozeSide = 0f; return; }

            Vector3 fwd, up;
            Facing(out fwd, out up);
            Vector3 right = Vector3.Cross(up, fwd);
            if (dozeSide == 0f) dozeSide = HerWindowSide(right);
            Vector3 winDir = right * dozeSide;
            float w = dozeW;

            // the shoulders rise with each breath
            if (w > 0.001f)
            {
                float lift = (breathS * 0.5f + 0.5f) * 1.6f * w;
                LiftShoulder(shoulderL, armL, up, lift);
                LiftShoulder(shoulderR, armR, up, lift);
            }
            // the road rocks the sleeping head a little
            float rock = (Mathf.PerlinNoise(Time.time * 0.9f, 4.1f) - 0.5f) * 3f * w * Mathf.Clamp01(SpeedKmh() / 60f);
            if (chest != null && w > 0.001f)
            {
                Quaternion C = chest.Deform();
                chest.T.rotation = Quaternion.AngleAxis(4f * w, C * Vector3.right) * chest.T.rotation;
                Turn(chest, Vector3.forward, Vector3.up, winDir, 6f * w);
            }
            float tilt = 22f * w + rock, pitch = 12f * w + nodNow, yaw = 6f * w;
            if (neck != null)
            {
                Quaternion N = neck.Deform();
                neck.T.rotation = Quaternion.AngleAxis(pitch * 0.4f, N * Vector3.right) * neck.T.rotation;
                Turn(neck, Vector3.forward, Vector3.up, winDir, tilt * 0.4f);
                Turn(neck, Vector3.up, Vector3.forward, winDir, yaw * 0.4f);
            }
            float hk = neck != null ? 0.6f : 1f;
            Quaternion H = head.Deform();
            head.T.rotation = Quaternion.AngleAxis(pitch * hk, H * Vector3.right) * head.T.rotation;
            Turn(head, Vector3.forward, Vector3.up, winDir, tilt * hk);
            Turn(head, Vector3.up, Vector3.forward, winDir, yaw * hk);

            YawnHand(winDir, up);
        }

        /// <summary>Turn a bone about its own axis so that its other axis heads towards dir (Unity: +angle about a moves v to a x v).</summary>
        static void Turn(Bone b, Vector3 axis, Vector3 moving, Vector3 dir, float deg)
        {
            if (Mathf.Abs(deg) < 1e-3f) return;
            Quaternion D = b.Deform();
            Vector3 a = D * axis, v = D * moving;
            float s = Mathf.Sign(Vector3.Dot(Vector3.Cross(a, v), dir));
            b.T.rotation = Quaternion.AngleAxis(s * deg, a) * b.T.rotation;
        }

        static void LiftShoulder(Transform sh, Arm arm, Vector3 up, float deg)
        {
            if (sh == null || arm == null) return;
            Vector3 v = arm.Upper.position - sh.position;
            if (v.sqrMagnitude < 1e-10f) return;
            sh.rotation = Quaternion.FromToRotation(v, v + up * (v.magnitude * Mathf.Tan(deg * Mathf.Deg2Rad))) * sh.rotation;
        }

        /// <summary>Her window side from the window itself (the camera flips sides when the player walks round the car).</summary>
        float HerWindowSide(Vector3 right)
        {
            if (herWindow != null && head != null)
            {
                float d = Vector3.Dot(herWindow.transform.position - head.T.position, right);
                if (Mathf.Abs(d) > 0.05f) return Mathf.Sign(d);
            }
            return WindowSide(head != null ? head.T.position : uncle.position, right);
        }

        /// <summary>The window-side hand up to the mouth, fingers up and across it, palm a little in front of the lips.</summary>
        void YawnHand(Vector3 winDir, Vector3 up)
        {
            if (yawnHandW <= 0.001f || armL == null || armR == null) return;
            Vector3 c;
            float r;
            if (!HeadSphere(out c, out r)) return;
            bool leftOuter = Vector3.Dot(armL.Upper.position - armR.Upper.position, winDir) > 0f;
            Arm a = leftOuter ? armL : armR;
            Quaternion H = head.Deform();
            Vector3 hf = H * Vector3.forward, hu = H * Vector3.up;
            Vector3 mouth = c + hf * (r * 0.85f) - hu * (r * 0.6f);
            Vector3 palm = mouth + hf * (r * 0.3f);
            Vector3 axis = (hu * 0.8f - winDir * 0.5f).normalized;          // fingers up and across the mouth
            Vector3 knuck = Knuckles(a);
            float k = (knuck - a.Hand.position).magnitude;
            if (k < 1e-5f) k = ArmLen(a) * 0.12f;
            Vector3 wrist = palm - axis * (k * 0.6f);
            float w = Mathf.SmoothStep(0f, 1f, yawnHandW);
            SolveArm(a, wrist, -up + winDir * 0.6f - hf * 0.2f, w);
            Vector3 cur = Knuckles(a) - a.Hand.position;
            if (cur.sqrMagnitude > 1e-10f)
                a.Hand.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(cur, axis), w) * a.Hand.rotation;
            PoseFingers(a, false, w);
            if (!handLogged && yawnHandW >= 0.99f)
            {
                handLogged = true;
                DozeLog("pose yawn hand " + a.Hand.name + ": wrist off target by " + ((a.Hand.position - wrist).magnitude / r).ToString("F2")
                    + " head radii, palm to mouth " + ((a.Hand.position + axis * (k * 0.6f) - mouth).magnitude / r).ToString("F2"));
            }
        }

        /// <summary>Middle of the finger roots (children of the hand, no thumb); the hand itself if none.</summary>
        Vector3 Knuckles(Arm a)
        {
            var list = a == armL ? fingersL : fingersR;
            Vector3 sum = Vector3.zero;
            int n = 0;
            foreach (var f in list)
                if (!f.Thumb && f.T.parent == a.Hand) { sum += f.T.position; n++; }
            if (n > 0) return sum / n;
            return a.Hand.position + (a.Hand.position - a.Fore.position).normalized * (ArmLen(a) * 0.12f);
        }

        // ------------------------------------------------------------ lines
        /// <summary>Queue a line of the story file; it waits for a free bubble until the window is over.</summary>
        void DozeLine(string key, float delay, float window, bool quiet)
        {
            lineKey = key;
            lineAt = Time.time + delay;
            lineUntil = lineAt + window;
            lineQuiet = quiet;
        }

        void DozeLines(float now)
        {
            if (lineKey == null || now < lineAt) return;
            if (now > lineUntil) { DozeLog("line " + lineKey + " skipped (bubble busy)"); lineKey = null; return; }
            if (StorySay == null) { lineKey = null; return; }
            bool free, done = false;
            Doze.Speaking = true;
            VoicePatch.Quiet = lineQuiet;
            try
            {
                free = CanSay == null || CanSay();
                if (free) done = StorySay(lineKey);
            }
            finally { Doze.Speaking = false; VoicePatch.Quiet = false; }
            if (!free) return;                               // the bubble is busy: try again next frame
            DozeLog(done ? "line " + lineKey : "no line for " + lineKey);
            lineKey = null;
        }

        /// <summary>A pat while asleep: she smiles and does not wake (CheckPat); sometimes a quiet line.</summary>
        void DozePat(float now)
        {
            if (Doze.Fake) { BooCaught(now); return; }   // task 39: a pat while she pretends = caught
            if (!Doze.IsAsleep || now - patLineAt < 25f || UnityEngine.Random.value > 0.8f) return;
            patLineAt = now;
            DozeLine("doze_pat", 0.3f, 2f, true);
        }

        /// <summary>A game dialogue is on: a talk item is queued, or the bubble shows a game line (not our own, not the radio bug).</summary>
        bool DozeDialog(UncleLogicC u)
        {
            if (u == null) return false;
            if (!string.IsNullOrEmpty(u.dialogueConvoItem)) return true;
            if (!u.isTalking || u.interruptConvoItem == "RadioTurnedDown") return false;
            if (RemarkBusy != null && RemarkBusy()) return false;
            var label = u.dialogueHolder != null ? u.dialogueHolder.GetComponent<UILabel>() : null;
            return label != null && !string.IsNullOrEmpty(label.text);
        }
    }
}
