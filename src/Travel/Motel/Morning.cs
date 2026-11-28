using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Pathfinding;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// A real motel morning (sleep in a bed, not Ctrl+F9): the game seats her in the car while the screen is black
    /// (BedLogicC.UncleReadyForNextDay -> UncleEnterCar). At MainMenuC.WakeUpGeneric, still black, she is taken out of
    /// the seat and stands on the floor about 1.1 m in front of the player, never on a bed (task 13).
    /// Task 07: as the lids open she bends over towards him, waves (Life, MorningPose.cs) and says an alarm line
    /// (story key wake_alarm, or wake_crash after a crash since the last morning); the fortune (task 08) follows at
    /// <see cref="CompanionDriver.MornAfterAlarm"/>. Task 13: she does not follow him. She stays in the room facing him;
    /// once he turns away and she is out of his sight, she is put in her seat (WarpIn).
    /// </summary>
    public static class Morning
    {
        static bool applied;
        public static bool Enabled = true;
        /// <summary>A crash (light, medium or heavy) happened since the last morning; read and cleared at wake.</summary>
        public static bool CrashSinceMorning;

        static BedLogicC bed;      // the bed that started the sleep (real sleep only)
        static float bedAt = -100f;

        public static void Apply()
        {
            if (applied) return;
            var h = new Harmony("osobl.jalopy.umamusumecompanion.morning");
            var ready = AccessTools.Method(typeof(BedLogicC), "UncleReadyForNextDay");
            var wake = AccessTools.Method(typeof(MainMenuC), "WakeUpGeneric");
            if (ready == null || wake == null)
            {
                Debug.LogWarning("[Umamusume companion] Morning: hooks not found, the morning stays as in the game");
                return;
            }
            h.Patch(ready, postfix: new HarmonyMethod(typeof(Morning), nameof(Ready)));
            h.Patch(wake, prefix: new HarmonyMethod(typeof(Morning), nameof(WakePrefix)));
            var bedTrig = AccessTools.Method(typeof(UncleBedTriggerC), "OnTriggerEnter");
            if (bedTrig != null) h.Patch(bedTrig, prefix: new HarmonyMethod(typeof(Morning), nameof(BedTriggerPrefix)));
            else Debug.LogWarning("[Umamusume companion] Morning: UncleBedTriggerC.OnTriggerEnter not found, she may sit on the bed in the morning");
            applied = true;
        }

        // runs when the game starts the coroutine (the real sleep only: the Ctrl+F9 reload never calls it)
        static void Ready(BedLogicC __instance)
        {
            bed = __instance;
            bedAt = Time.time;
            DayStarted = false;
            if (Enabled && __instance != null && !__instance.homeBed)
            {
                var go = GameObject.FindWithTag("Uncle");
                var u = go != null ? go.GetComponent<UncleLogicC>() : null;
                if (u != null && !u.uncleAtHome && !u.uncleGoneForever && !u.debugStopUncleWorking)
                    DoorMute(u, 40f, "the game seats her in the black screen");
            }
        }

        /// <summary>Set at a real motel wake; the game's next evening check-in (hasSaidLetsCheckIn) ends it.</summary>
        public static bool DayStarted;
        static float bedTrigLogAt = -100f;

        // Task 11: the bed in the room has a trigger (UncleBedTriggerC) that seats the uncle on it, sets bedTime and runs
        // MotelSatOnBed (un_motel_06/07/08/15: "...Р»РѕР¶РёСЃСЊ РЅР° РљСЂРѕРІР°С‚СЊ, Рё Р±СѓРґРµРј СЃРїР°С‚СЊ"). In the game he never walks in the
        // room in the morning, she does (following you), so she sat on the bed and said the evening lines again.
        // The trigger now only works during the evening check-in.
        static bool BedTriggerPrefix(UncleBedTriggerC __instance, Collider uncle)
        {
            if (uncle == null || uncle.tag != "Uncle") return true;
            var u = uncle.GetComponent<UncleLogicC>();
            if (u == null || u.hasSaidLetsCheckIn) { DayStarted = false; return true; }   // the evening check-in: as in the game
            MotelLogicC ml = __instance != null ? __instance.GetComponentInParent<MotelLogicC>() : null;
            if (ml == null && u.motel != null) ml = u.motel.GetComponent<MotelLogicC>();
            bool morning = ml != null && ml.parkingTrigger != null && ml.parkingTrigger.GetComponent<MotelParkingTriggerC>() != null
                && ml.parkingTrigger.GetComponent<MotelParkingTriggerC>().isMorning;
            if (!DayStarted && !morning) return true;
            if (Time.time > bedTrigLogAt)
            {
                bedTrigLogAt = Time.time + 5f;
                Debug.Log("[Umamusume companion] Morning: bed trigger ignored (day started " + DayStarted + ", motel isMorning " + morning + ", no check-in yet)");
            }
            return false;
        }

        // task 14 (5): the game seats the uncle during the sleep (UncleReadyForNextDay -> UncleEnterCar): his car door
        // opens with a sound in the black screen while she is in the room. Her door is muted from then until 3 s
        // after the wake, and for 4 s around the morning warp to her seat.
        static readonly List<AudioSource> doorMuted = new List<AudioSource>();
        static float doorUnmuteAt = -1f;

        public static void DoorMute(UncleLogicC u, float seconds, string why)
        {
            if (u == null || u.uncleDoorTrigger == null) return;
            var d = u.uncleDoorTrigger.transform;
            int n = 0;
            foreach (var a in new[] { d.GetComponent<AudioSource>(), d.parent != null ? d.parent.GetComponent<AudioSource>() : null })
            {
                if (a == null) continue;
                if (!doorMuted.Contains(a)) { if (a.mute) continue; a.mute = true; doorMuted.Add(a); }
                n++;
            }
            doorUnmuteAt = Mathf.Max(doorUnmuteAt, Time.time + seconds);
            Debug.Log("[Umamusume companion] Morning: her car door muted for " + seconds.ToString("0") + " s (" + why + ", " + n + " audio sources)");
        }

        /// <summary>Every frame (MornUpdate): the door gets its sound back when the window is over.</summary>
        public static void DoorTick()
        {
            if (doorMuted.Count == 0 || Time.time < doorUnmuteAt) return;
            foreach (var a in doorMuted) if (a != null) a.mute = false;
            doorMuted.Clear();
            doorUnmuteAt = -1f;
            Debug.Log("[Umamusume companion] Morning: her car door sound back");
        }

        /// <summary>Shorten the mute window (from now).</summary>
        public static void DoorUnmuteIn(float seconds)
        {
            if (doorMuted.Count > 0) doorUnmuteAt = Time.time + seconds;
        }

        static void WakePrefix()
        {
            var b = bed;
            bool real = b != null && Time.time - bedAt < 40f;
            bed = null;
            DoorUnmuteIn(3f);
            if (!real) return;   // Ctrl+F9 / load: she is placed by the game (WakeUpFromLoad)
            DayStarted = true;
            Luck.NewDay();
            if (!b.homeBed) Trust.OnMotelNight();
            Souvenirs.OnMorning();   // task 47
            try
            {
                var d = UnityEngine.Object.FindObjectOfType<CompanionDriver>();
                if (d != null) d.MorningWake(b);
            }
            catch (Exception e) { Debug.LogError("[Umamusume companion] Morning: wake failed: " + e); }
        }
    }

    public partial class CompanionDriver
    {
        internal static string MorningLine(bool istanbul, bool crash)
        {
            if (istanbul) return null;
            if (crash) return "wake_crash";
            int stage = Trust.Stage;
            return stage > 0 ? "wake_alarm_s" + stage : "wake_alarm";
        }
        // task 13: Hold for the whole morning (no follow, no walk to the car)
        enum MPhase { Off, Hold }
        MPhase mornPhase = MPhase.Off;
        BedLogicC mornBed;
        MotelLogicC mornMotel;
        bool mornSilent, mornWarped, mornLeft, mornWarpNow;
        bool mornSeenAtWake;
        Vector3 mornWakeForward;
        float mornAt, mornFreeAt = -1f, mornSeenSince = -1f, mornLogAt;

        // task 07: the wake-up pose and the alarm line. Timeline from WakeUpGeneric (screen black): lids peek at
        // +0-1 s, shut +1-2 s, open ~+2.8 s; the lean starts at +1.5 s, the line at +2.8 s.
        enum MPose { None, Wait, On, Ending }
        MPose mornPose = MPose.None;
        float mornPoseAt, mornTalkEndAt;
        string mornLineKey;
        bool mornLineSent, mornTalkSeen, mornAfterDone;
        const float MornPoseDelay = 1.5f, MornLineDelay = 1.3f;

        // ------------------------------------------------------------ wake
        public void MorningWake(BedLogicC b)
        {
            ResetShoppingMorning();
            var u = OutU();
            bool crash = Morning.CrashSinceMorning;
            Morning.CrashSinceMorning = false;
            var route = u != null && u.director != null ? u.director.GetComponent<RouteGeneratorC>() : null;
            bool istanbul = ReturnTrip.MorningPending;
            Debug.Log("[Umamusume companion] Morning: wake (crash since last morning: " + crash + ", route level " +
                      (route != null ? route.routeLevel.ToString() : "?") + (istanbul ? ", Istanbul morning: stays silent" : "") + ")");
            if (u != null)
            {
                var pt = b != null && b.motelLogic != null && b.motelLogic.GetComponent<MotelLogicC>() != null && b.motelLogic.GetComponent<MotelLogicC>().parkingTrigger != null
                    ? b.motelLogic.GetComponent<MotelLogicC>().parkingTrigger.GetComponent<MotelParkingTriggerC>() : null;
                Debug.Log("[Umamusume companion] Morning: flags at wake: withinRangeOfMotel " + u.withinRangeOfMotel + ", hasSpokenWithinRangeOfMotel " + u.hasSpokenWithinRangeOfMotel +
                          ", hasSaidLetsCheckIn " + u.hasSaidLetsCheckIn + ", bedTime " + (animator != null && animator.GetBool("bedTime")) + ", isSat " + u.isSat +
                          ", parking isMorning " + (pt != null ? pt.isMorning.ToString() : "-") + ", borderTalk " + u.isReadyToTalkAboutBorder + ", dayStarted " + Morning.DayStarted);
            }

            string skip = null;
            if (!Morning.Enabled) skip = "toggle off";
            else if (u == null) skip = "no uncle";
            else if (u.uncleAtHome || u.uncleGoneForever || u.debugStopUncleWorking) skip = "not on a trip with you";
            else if (b == null || b.homeBed) skip = "home bed";
            else if (b.path == null || b.path.Length < 3 || b.path[2] == null) skip = "bed has no standing spot";
            else if (!InCar()) skip = "she is not in the seat (" + outState + ")";
            if (skip != null) { Debug.Log("[Umamusume companion] Morning: skip (" + skip + ")"); return; }

            mornBed = b;
            mornMotel = b.motelLogic != null ? b.motelLogic.GetComponent<MotelLogicC>() : null;
            mornSilent = istanbul;
            mornWarped = mornLeft = mornWarpNow = false;
            mornFreeAt = mornSeenSince = -1f;
            mornLogAt = Time.time + 5f;
            MornProbe(u, b, "at wake");
            // task 11: the game's own morning talk (un_newday_01/02, "LetsGoBorderCross") fires as soon as she is near
            // you, so in the room it took the bubble and the alarm line never came. It waits until she is in the car.
            mornBorderHeld = u.isReadyToTalkAboutBorder;
            u.isReadyToTalkAboutBorder = false;
            mornReplaced = false;
            mornSeenAtWake = false;
            if (!mornSilent && CharFeatures.Has("shopping")) BuyMorningSupplies(mornMotel);
            MornBedsFind(b.path[2].position);
            MornPlace(u, b);

            mornPose = MPose.Wait;
            mornPoseAt = Time.time + MornPoseDelay;
            mornLineKey = MorningLine(istanbul, crash);
            mornLineSent = mornTalkSeen = mornAfterDone = false;
            mornTalkEndAt = -1f;
        }

        // ------------------------------------------------------------ task 07: pose + alarm line (every frame from MornUpdate)
        void MornPoseStep()
        {
            if (mornPose == MPose.None) return;
            if (life == null) { mornPose = MPose.None; return; }
            float now = Time.time, t = now - mornPoseAt;
            if (mornPose == MPose.Wait)
            {
                if (t < 0f) return;
                if (mornPhase != MPhase.Hold) { mornPose = MPose.None; Debug.Log("[Umamusume companion] Morning: pose skipped (" + mornPhase + "/" + outState + ")"); return; }
                if (!life.MornPoseBegin()) { mornPose = MPose.None; Debug.Log("[Umamusume companion] Morning: pose skipped (Lively off or no arm bones)"); MornAfterAlarm(); return; }
                mornPose = MPose.On;
                return;
            }
            if (mornPose == MPose.Ending)
            {
                if (!life.MornPoseOn) { mornPose = MPose.None; MornAfterAlarm(); }
                return;
            }

            // On: the line once the eyes are open; it waits up to ~7 s for a free bubble
            if (mornLineKey != null && !mornLineSent && t >= MornLineDelay)
            {
                if (life.CanSay == null || life.CanSay())
                {
                    mornLineSent = true;
                    bool said = life.StorySay != null && life.StorySay(mornLineKey);
                    Debug.Log("[Umamusume companion] Morning: " + (said ? "line " + mornLineKey : "no line " + mornLineKey + " in this language"));
                    if (!said) mornLineKey = null;
                }
                else if (t > 8f) { Debug.Log("[Umamusume companion] Morning: line " + mornLineKey + " skipped (bubble busy)"); mornLineKey = null; }
            }
            if (mornLineSent && mornLineKey != null && animator != null)
            {
                bool talking = animator.GetBool("talking");
                if (talking) mornTalkSeen = true;
                else if (mornTalkSeen && mornTalkEndAt < 0f) mornTalkEndAt = now + 0.6f;   // typed out: a beat more, then straighten up
            }

            string why = null;
            if (mornPhase != MPhase.Hold) why = "morning over";
            else if (mornTalkEndAt >= 0f && now >= mornTalkEndAt && t >= 3.5f) why = "line said";
            else if (mornLineKey == null && t >= 4.5f) why = "no line";
            else if (t >= 11f) why = "max time";
            if (why == null) return;
            life.MornPoseEnd(why);
            mornPose = MPose.Ending;
        }

        /// <summary>
        /// Once per real motel morning, after the wake-up pose blended out (the alarm line said or skipped). She stands
        /// where she was put (task 13: she stays); the fortune (task 08) goes here.
        /// </summary>
        void MornAfterAlarm()
        {
            if (mornAfterDone) return;
            mornAfterDone = true;
            Debug.Log("[Umamusume companion] Morning: after alarm (" + mornPhase + "/" + outState + ", line " + (mornLineSent && mornLineKey != null ? mornLineKey : "none") + ")");
            FortMorning();
        }

        // ------------------------------------------------------------ where she stands (task 11, task 13)
        // v0.33.1 put her on the floor inside the player's bed: the mattress has no solid collider, so the floor ray
        // went through it to the floor and the capsule test found nothing. Now every bed near the room (BedLogicC mesh
        // and render targets, colliders including triggers, the uncle's bed trigger, meshes named "bed") is a no-go zone.
        bool mornBorderHeld, mornReplaced, mornViewLogged;
        float mornViewAt = -1f;
        const float MornLegM = 0.78f;                            // her thigh + shin in metres (Fukukitaru 156 cm)
        const float MornBedMarginM = 0.3f;                       // her body radius + a little: the spot keeps off the bed edge

        // task 31: her leg shrinks with body_scale, a metre does not
        float MornMetre { get { return Mathf.Max(1e-3f, walkLegLen) / (MornLegM * BodyScale); } }

        /// <summary>A bed footprint: a mesh's local bounds (exact for a turned bed) or a world box.</summary>
        class MornBedZone
        {
            public string Name;
            public Transform T;      // null = world box
            public Bounds B;
            public int Up;           // local up axis (mesh zones)
        }
        readonly List<MornBedZone> mornBeds = new List<MornBedZone>();

        void MornBedAddMesh(Transform t, Mesh m, string tag)
        {
            if (t == null || m == null) return;
            int up = 0;
            float bu = -1f;
            for (int k = 0; k < 3; k++)
            {
                Vector3 ax = Vector3.zero; ax[k] = 1f;
                float dp = Mathf.Abs(Vector3.Dot(t.TransformDirection(ax), Vector3.up));
                if (dp > bu) { bu = dp; up = k; }
            }
            Vector3 s = Vector3.Scale(m.bounds.size, t.lossyScale);
            float mt = MornMetre;
            float h1 = Mathf.Abs(s[(up + 1) % 3]) / mt, h2 = Mathf.Abs(s[(up + 2) % 3]) / mt;
            if (h1 > 3.5f || h2 > 3.5f) { Debug.Log("[Umamusume companion] Morning: bed zone " + tag + " " + t.name + " skipped (too big, " + h1.ToString("F1") + " x " + h2.ToString("F1") + " m)"); return; }
            mornBeds.Add(new MornBedZone { Name = tag + " " + t.name, T = t, B = m.bounds, Up = up });
        }

        void MornBedAddBox(Bounds w, string name)
        {
            float m = MornMetre;
            if (w.size.x > 3.5f * m || w.size.z > 3.5f * m) { Debug.Log("[Umamusume companion] Morning: bed zone " + name + " skipped (too big, " + (w.size.x / m).ToString("F1") + " x " + (w.size.z / m).ToString("F1") + " m)"); return; }
            mornBeds.Add(new MornBedZone { Name = name, B = w });
        }

        /// <summary>Every bed within 8 m of the eye, once per morning.</summary>
        void MornBedsFind(Vector3 eye)
        {
            mornBeds.Clear();
            float m = MornMetre, r = 8f * m;
            try
            {
                foreach (var b in UnityEngine.Object.FindObjectsOfType<BedLogicC>())
                {
                    if (b == null || Flat(b.transform.position - eye).magnitude > r) continue;
                    var mf = b.GetComponent<MeshFilter>();
                    if (mf != null) MornBedAddMesh(b.transform, mf.sharedMesh, "bed");
                    if (b.renderTargets != null)
                        foreach (var g in b.renderTargets)
                        {
                            if (g == null || g.transform == b.transform) continue;
                            var gf = g.GetComponent<MeshFilter>();
                            if (gf != null && gf.sharedMesh != null) MornBedAddMesh(g.transform, gf.sharedMesh, "bed part");
                            else { var gr = g.GetComponent<Renderer>(); if (gr != null) MornBedAddBox(gr.bounds, "bed part " + g.name); }
                        }
                    foreach (var c in b.GetComponentsInChildren<Collider>()) if (c != null) MornBedAddBox(c.bounds, "bed collider " + c.name);
                }
                foreach (var t in UnityEngine.Object.FindObjectsOfType<UncleBedTriggerC>())
                {
                    if (t == null || Flat(t.transform.position - eye).magnitude > r) continue;
                    var c = t.GetComponent<Collider>();
                    // the trigger is where the uncle lies (he is snapped to it): a bed around it even if the box is small
                    Bounds w = c != null ? c.bounds : new Bounds(t.transform.position, Vector3.zero);
                    w.Encapsulate(new Bounds(t.transform.position, new Vector3(1.4f * m, 0f, 1.4f * m)));
                    MornBedAddBox(w, "uncle bed " + t.name);
                }
                // other furniture named as a bed (the room's second bed may have no game logic)
                foreach (var rr in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
                {
                    if (rr == null || rr.name.IndexOf("bed", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (rr.GetComponent<BedLogicC>() != null || rr.transform.IsChildOf(transform)) continue;
                    if (Flat(rr.bounds.center - eye).magnitude > r) continue;
                    var f = rr.GetComponent<MeshFilter>();
                    if (f != null && f.sharedMesh != null) MornBedAddMesh(rr.transform, f.sharedMesh, "named");
                    else MornBedAddBox(rr.bounds, "named " + rr.name);
                }
            }
            catch (Exception e) { Debug.Log("[Umamusume companion] Morning: bed search failed: " + e.Message); }
            var sb = new StringBuilder("[Umamusume companion] Morning: beds near you: " + mornBeds.Count);
            foreach (var z in mornBeds)
            {
                Vector3 c = z.T != null ? z.T.TransformPoint(z.B.center) : z.B.center;
                Vector3 sz = z.T != null ? Vector3.Scale(z.B.size, z.T.lossyScale) : z.B.size;
                sb.Append("; " + z.Name + " at " + c.ToString("F1") + " size " + (Mathf.Abs(sz.x) / m).ToString("F1") + "/" + (Mathf.Abs(sz.y) / m).ToString("F1") + "/" + (Mathf.Abs(sz.z) / m).ToString("F1") + " m");
            }
            Debug.Log(sb.ToString());
        }

        /// <summary>The bed whose footprint (plus margin) holds this floor point, or null.</summary>
        string MornOnBed(Vector3 p, float marginM)
        {
            float mg = marginM * MornMetre;
            foreach (var z in mornBeds)
            {
                if (z.T == null)
                {
                    if (p.x > z.B.min.x - mg && p.x < z.B.max.x + mg && p.z > z.B.min.z - mg && p.z < z.B.max.z + mg) return z.Name;
                    continue;
                }
                Vector3 lp = z.T.InverseTransformPoint(p);
                lp[z.Up] = z.B.center[z.Up];                     // footprint only: the height does not matter
                Vector3 cp = z.B.ClosestPoint(lp);
                if (Flat(z.T.TransformVector(cp - lp)).magnitude <= mg) return z.Name;
            }
            return null;
        }

        bool MornIgnore(Collider c, Transform player)
        {
            if (c == null || c.isTrigger) return true;
            Transform t = c.transform;
            return t.IsChildOf(transform) || (player != null && t.IsChildOf(player));
        }

        /// <summary>First solid surface straight down (the player and she do not count).</summary>
        bool MornGround(Vector3 from, float range, Transform player, out float y)
        {
            string n;
            return MornGround(from, range, player, out y, out n);
        }

        bool MornGround(Vector3 from, float range, Transform player, out float y, out string name)
        {
            y = 0f;
            name = "-";
            float best = float.MaxValue;
            foreach (var h in UnityEngine.Physics.RaycastAll(from, Vector3.down, range, ~0, QueryTriggerInteraction.Ignore))
            {
                if (MornIgnore(h.collider, player) || h.distance >= best) continue;
                best = h.distance;
                y = h.point.y;
                name = h.collider.name;
            }
            return best < float.MaxValue;
        }

        Collider MornOverlap(Vector3 foot, Transform player)
        {
            float leg = walkLegLen, r = 0.32f * leg;
            Vector3 a = foot + Vector3.up * (0.15f * leg + r), b = foot + Vector3.up * (2.0f * leg - r);
            foreach (var c in UnityEngine.Physics.OverlapCapsule(a, b, r, ~0, QueryTriggerInteraction.Ignore))
                if (!MornIgnore(c, player)) return c;
            return null;
        }

        bool MornSightBlocked(Vector3 a, Vector3 b, Transform player)
        {
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 1e-4f) return false;
            foreach (var h in UnityEngine.Physics.RaycastAll(a, d / len, len, ~0, QueryTriggerInteraction.Ignore))
                if (!MornIgnore(h.collider, player)) return true;
            return false;
        }

        /// <summary>Spots in metres: (forward, sideways) from the eye.</summary>
        static readonly Vector2[] MornTries = BuildMornTries();

        static Vector2[] BuildMornTries()
        {
            var l = new List<Vector2>();
            float[] side = { 0.6f, -0.6f, 1.0f, -1.0f };
            foreach (float f in new[] { 1.1f, 1.0f, 1.2f }) l.Add(new Vector2(f, 0f));                            // straight ahead
            foreach (float f in new[] { 1.1f, 1.0f, 1.2f }) foreach (float s in side) l.Add(new Vector2(f, s));   // sideways
            foreach (float f in new[] { 0.8f, 0.6f }) { l.Add(new Vector2(f, 0f)); foreach (float s in side) l.Add(new Vector2(f, s)); }   // closer to the eye
            foreach (float s in new[] { 1.4f, -1.4f }) { l.Add(new Vector2(1.0f, s)); l.Add(new Vector2(0.5f, s)); }
            for (int a = 90; a <= 270; a += 30)                                                                 // last: around the eye, behind too
            {
                float rad = a * Mathf.Deg2Rad;
                l.Add(new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)));
            }
            return l.ToArray();
        }

        /// <summary>
        /// A floor spot about 1.1 m in front of the eye, facing it (task 13): straight ahead first, then sideways
        /// (В±0.6, В±1.0 m), then closer to the eye, then around it. A spot is good when the surface under it is the floor
        /// (no mattress, no table), it is not on any bed footprint, her body fits (no solid collider) and the eye sees it.
        /// Every try is logged once.
        /// </summary>
        bool MornPick(Vector3 eye, Vector3 fwd, Transform player, string when, out Vector3 spot, out float floorY, out string how)
        {
            float leg = walkLegLen, m = MornMetre;
            fwd = Flat(fwd);
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            if (!MornGround(eye, 6f * leg, player, out floorY)) floorY = eye.y - 2.2f * leg;   // from the eye: above it may be the ceiling
            spot = eye + fwd * (1.1f * m);
            spot.y = floorY;
            how = "no clear spot";
            Vector3 fallback = Vector3.zero;
            string fallbackHow = null;
            int n = 0;
            foreach (var t in MornTries)
            {
                n++;
                Vector3 c = eye + fwd * (t.x * m) + right * (t.y * m);
                float fy;
                string hit, res, why = "";
                if (!MornGround(new Vector3(c.x, floorY + 2.2f * leg, c.z), 3f * leg, player, out fy, out hit)) { res = "blocked"; why = "no ground"; fy = floorY; }
                else
                {
                    Vector3 foot = new Vector3(c.x, fy, c.z);
                    string bedName = MornOnBed(foot, MornBedMarginM);
                    if (fy - floorY > 0.15f * m) { res = "bed"; why = "surface " + ((fy - floorY) / m).ToString("F2") + " m above the floor"; }
                    else if (floorY - fy > 0.3f * m) { res = "blocked"; why = "drop " + ((floorY - fy) / m).ToString("F2") + " m"; }
                    else if (bedName != null) { res = "bed"; why = "in " + bedName; }
                    else
                    {
                        var blocker = MornOverlap(foot, player);
                        if (blocker != null) { res = "blocked"; why = "inside " + blocker.name; }
                        else if (MornSightBlocked(new Vector3(eye.x, floorY + 1.9f * leg, eye.z), foot + Vector3.up * (1.4f * leg), player))
                        {
                            res = "blocked"; why = "not in sight";
                            if (fallbackHow == null) { fallback = foot; fallbackHow = "try " + n + " not in sight"; }
                        }
                        else res = "ok";
                    }
                }
                Debug.Log("[Umamusume companion] Morning: place try " + n + " (" + when + ", fwd " + t.x.ToString("0.0") + " side " + t.y.ToString("0.0") + " m) " +
                          new Vector3(c.x, fy, c.z).ToString("F2") + " hit " + hit + " y " + fy.ToString("F2") + " " + res + (why.Length > 0 ? " (" + why + ")" : ""));
                if (res != "ok") continue;
                spot = new Vector3(c.x, fy, c.z);
                how = "try " + n + ": fwd " + t.x.ToString("0.0") + " side " + t.y.ToString("0.0") + " m";
                return true;
            }
            if (fallbackHow != null) { spot = fallback; how = "fallback " + fallbackHow; return true; }
            how = "no clear spot, forced 1.1 m ahead";
            return false;
        }

        /// <summary>Task 14: the uncle stays in his car clip for the whole morning; he is not stood up any more.
        /// v0.33.4 ran his animator through exitCar in one frame (Update x16, events off): the user log showed
        /// Uncle_car_Stationary_idle before and after, 4 times. The game never stands him up at the motel either
        /// (WakeUpFromLoad only moves him to uncleLoadTransform, the real-sleep wake leaves him in the seat), so the
        /// run-through is dropped. Her legs and hips come from her own stance and the floor instead (MornFeet).
        /// One line records his animator so the cause can be looked at later.</summary>
        void MornStand()
        {
            if (animator == null) return;
            animator.SetBool("enterCar", false);
            animator.SetBool("exitCar", false);
            animator.SetBool("bedTime", false);
            animator.SetFloat("walkSpeed", 0f);
            var st = animator.GetCurrentAnimatorStateInfo(0);
            var ps = new StringBuilder();
            foreach (var p in animator.parameters)
            {
                if (ps.Length > 0) ps.Append(", ");
                ps.Append(p.name).Append('=');
                if (p.type == AnimatorControllerParameterType.Bool) ps.Append(animator.GetBool(p.name) ? "1" : "0");
                else if (p.type == AnimatorControllerParameterType.Float) ps.Append(animator.GetFloat(p.name).ToString("0.##"));
                else if (p.type == AnimatorControllerParameterType.Int) ps.Append(animator.GetInteger(p.name));
                else ps.Append("trigger");
            }
            Debug.Log("[Umamusume companion] Morning: stand: uncle stays in " + MornUncleClip() + " (not run through the car exit any more); her own stance + floor. " +
                      "Animator enabled " + animator.enabled + ", culling " + animator.cullingMode + ", update " + animator.updateMode +
                      ", state " + st.fullPathHash + " t " + st.normalizedTime.ToString("0.00") + ", in transition " + animator.IsInTransition(0) +
                      ", layers " + animator.layerCount + ", params: " + ps);
            MornClearOffsets("stand");
        }

        // ------------------------------------------------------------ task 14: her own stance on the floor
        // The user log (v0.33.4): ankles -0.12 legs under the floor, root +0.04. Her hip position is copied from the
        // uncle's hips every frame (Pose), and he sits: her straight standing legs hang from seated hips. In the
        // morning her body is her own standing loop (UpdateIdle forced on) and her hips are put over her root at the
        // height where the lower ankle is at floor + ankle height.
        const float MornAnkleRest = 0.16f;   // her standing ankle above the floor, legs (G outing learned 0.30 / leg 1.84)
        float mornFeetLogAt;

        /// <summary>The morning hold: she stands outside, not in the seat, not in a bed.</summary>
        bool MornOwnStance(bool inCar)
        {
            return mornPhase == MPhase.Hold && !inCar && transform.parent == null && bedW <= 0f && !InBed();
        }

        void MornFeet(bool inCar)
        {
            if (!MornOwnStance(inCar) || walkW > 0f || hipLink == null || legL == null || legR == null) return;
            Vector3 up = transform.up;
            float g = RootGround();
            float leg = Mathf.Max(1e-3f, walkLegLen);
            Vector3 hp = hipLink.Girl.position;
            hp -= Vector3.ProjectOnPlane(hp - transform.position, up);   // over her root (his seated hips sit back)
            hipLink.Girl.position = hp;
            float low = Mathf.Min(Vector3.Dot(legL.Ankle.position, up), Vector3.Dot(legR.Ankle.position, up)) - g;
            hipLink.Girl.position += up * (MornAnkleRest * leg - low);
        }

        /// <summary>The feet line 1 s after the placing, then every 5 s in the first minute of the morning.</summary>
        void MornFeetStep()
        {
            if (mornFeetLogAt < 0f || Time.time < mornFeetLogAt || !MornOwnStance(InCar())) return;
            mornFeetLogAt = Time.time - mornAt > 60f ? -1f : Time.time + 5f;
            MornFeetLine((Time.time - mornAt).ToString("0") + " s");
        }

        /// <summary>"Morning: feet ankles l/r legs above floor": ankle height above floor + her ankle height (target ~0).</summary>
        void MornFeetLine(string when)
        {
            if (legL == null || legR == null || hipLink == null) return;
            Vector3 up = transform.up;
            float g = RootGround(), leg = Mathf.Max(1e-3f, walkLegLen);
            float l = (Vector3.Dot(legL.Ankle.position, up) - g) / leg - MornAnkleRest, r = (Vector3.Dot(legR.Ankle.position, up) - g) / leg - MornAnkleRest;
            Debug.Log("[Umamusume companion] Morning: feet ankles " + l.ToString("F2") + "/" + r.ToString("F2") + " legs above floor (" + when +
                      ", target ~0; hip " + ((Vector3.Dot(hipLink.Girl.position, up) - g) / leg).ToString("F2") + " legs, root " +
                      ((Vector3.Dot(transform.position, up) - g) / leg).ToString("F2") + ", her stance " + idleW.ToString("F2") + ", uncle clip " + MornUncleClip() + ")");
        }

        string MornUncleClip()
        {
            var ci = animator != null ? animator.GetCurrentAnimatorClipInfo(0) : null;
            return ci != null && ci.Length > 0 && ci[0].clip != null ? ci[0].clip.name : "-";
        }

        bool MornUncleInCarClip() { return MornUncleClip().IndexOf("_car_", StringComparison.OrdinalIgnoreCase) >= 0; }

        /// <summary>Learned standing heights (hip, ankles) and the walk's root memory start again from her real stance.</summary>
        void MornClearOffsets(string when)
        {
            hipAbove = -1f;
            ankleRest = -1f;
            haveRoot = false;
            rootSpeed = 0f;
            walkW = 0f;
            Debug.Log("[Umamusume companion] Morning: offsets cleared (" + when + ", uncle clip " + MornUncleClip() + ")");
        }

        /// <summary>Put her at the spot facing the eye; her walk forgets the jump (no step from the teleport).</summary>
        void MornSet(Vector3 spot, Vector3 eye)
        {
            var cc = GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            transform.parent = null;
            transform.position = spot + Vector3.up * (0.02f * walkLegLen);
            Vector3 face = Flat(eye - spot);
            if (face.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(face.normalized, Vector3.up);
            if (cc != null) cc.enabled = true;
            haveRoot = false;
            rootSpeed = 0f;
            movingState = false;
            walkW = 0f;
            if (outT != null) outT.position = transform.position;   // Hold: she stays right here
            MornWaveSideSet();
        }

        /// <summary>The waving hand is the one away from the bed.</summary>
        void MornWaveSideSet()
        {
            if (life == null || mornBed == null) return;
            Vector3 bc = mornBed.path[1] != null ? mornBed.path[1].position : mornBed.transform.position;
            var col = mornBed.GetComponent<Collider>();
            if (col != null) bc = col.bounds.center;
            else { var r = mornBed.GetComponent<Renderer>(); if (r != null) bc = r.bounds.center; }
            Vector3 right = Vector3.Cross(Vector3.up, Flat(transform.forward).normalized);
            life.MornWaveSide = Vector3.Dot(bc - transform.position, right) > 0f ? -1 : 1;
        }

        string MornDistLog(Vector3 eye)
        {
            Vector3 d = Flat(transform.position - eye);
            return "distToCam " + (d.magnitude / MornMetre).ToString("F2") + " m (" + d.magnitude.ToString("F2") + " u)";
        }

        void MornPlace(UncleLogicC u, BedLogicC b)
        {
            Transform p2 = b.path[2];
            Transform player = u.player != null ? u.player.transform : null;
            // the game moves the player to path[2] (rotation too) 0.5 s after this and lets him walk at +2.5 s
            Vector3 eye = p2.position;
            Vector3 spot;
            float floorY;
            string how;
            MornPick(eye, p2.forward, player, "at wake", out spot, out floorY, out how);
            MornSet(spot, eye);
            animator.SetBool("enterCar", false);
            animator.SetBool("exitCar", false);
            u.isSat = false;
            u.readyToBoard = false;
            MornStand();

            direct = false;
            outPlayerSat = false;
            movingFor = 0f;
            outState = Out.Follow;
            mornPhase = MPhase.Hold;
            mornAt = Time.time;
            mornViewLogged = false;
            mornViewAt = -1f;
            mornTurning = false;
            mornFeetLogAt = Time.time + 1f;
            OutFollowTarget();   // Hold: the target is her own spot
            var ai = GetComponent<AIPath>();
            if (ai != null) { ai.target = outT; ai.canMove = false; }   // task 13: she stands; the path code does not move or turn her
            if (life != null) life.MornStay = true;
            Debug.Log("[Umamusume companion] Morning: placed pos " + transform.position.ToString("F2") + " floorY " + floorY.ToString("F2") + " " +
                      MornDistLog(eye) + " (" + how + "), eye path[2] " + eye.ToString("F2") + ", wave hand " +
                      (life != null ? (life.MornWaveSide > 0 ? "right" : "left") : "-") + ", leg " + walkLegLen.ToString("F2"));
        }

        /// <summary>
        /// When the game lets you walk (lids still shut, ~0.3 s before they open) the camera is where it stays: if she is
        /// not about 1.1 m in front of it, or she is on a bed, she is put there again.
        /// </summary>
        void MornRecheck(UncleLogicC u)
        {
            if (mornReplaced) return;
            mornReplaced = true;
            var cam = Camera.main;
            if (cam == null) return;
            Transform player = u.player != null ? u.player.transform : null;
            Vector3 eye = cam.transform.position, fwd = Flat(cam.transform.forward);
            Vector3 d = Flat(transform.position - eye);
            float dm = d.magnitude / MornMetre;
            float ang = fwd.sqrMagnitude > 1e-4f && d.sqrMagnitude > 1e-4f ? Vector3.Angle(fwd, d) : 180f;
            mornViewAt = Time.time + 1.5f;
            string onBed = MornOnBed(transform.position, MornBedMarginM);
            if (dm >= 0.5f && dm <= 1.5f && ang <= 60f && onBed == null && MornOverlap(transform.position, player) == null)
            {
                Debug.Log("[Umamusume companion] Morning: placed check ok at you can walk: " + MornDistLog(eye) + ", " + ang.ToString("0") + " deg off the view, camera " + eye.ToString("F2"));
                return;
            }
            Vector3 spot;
            float floorY;
            string how;
            MornPick(eye, fwd, player, "you can walk", out spot, out floorY, out how);
            MornSet(spot, eye);
            Debug.Log("[Umamusume companion] Morning: placed pos " + transform.position.ToString("F2") + " floorY " + floorY.ToString("F2") + " " +
                      MornDistLog(eye) + " (again at you can walk, was " + dm.ToString("F2") + " m / " + ang.ToString("0") + " deg" +
                      (onBed != null ? " on " + onBed : "") + "; " + how + "), camera " + eye.ToString("F2") + ", wave hand " +
                      (life != null ? (life.MornWaveSide > 0 ? "right" : "left") : "-"));
        }

        /// <summary>Once, 1.5 s after you can walk (you landed): how far she really is from the camera.</summary>
        void MornViewLog()
        {
            if (mornViewLogged || mornViewAt < 0f || Time.time < mornViewAt) return;
            mornViewLogged = true;
            var cam = Camera.main;
            if (cam == null) return;
            Vector3 eye = cam.transform.position;
            var ci = animator != null ? animator.GetCurrentAnimatorClipInfo(0) : null;
            string onBed = MornOnBed(transform.position, 0f);
            Debug.Log("[Umamusume companion] Morning: view " + MornDistLog(eye) + ", " + Vector3.Angle(Flat(cam.transform.forward), Flat(transform.position - eye)).ToString("0") +
                      " deg off the view, camera " + (eye.y - transform.position.y).ToString("F2") + " u above her feet, uncle clip " +
                      (ci != null && ci.Length > 0 && ci[0].clip != null ? ci[0].clip.name : "-") + ", walkW " + walkW.ToString("F2") +
                      ", on a bed: " + (onBed ?? "no") + MornFeetLog());
            MornFeetLine("view");
        }

        /// <summary>Her ankles above the floor under her (in legs; a standing rest is about 0.05-0.1): feet in the floor show as negative.</summary>
        string MornFeetLog()
        {
            if (legL == null || legR == null) return ", ankles -";
            float fy;
            if (!MornGround(transform.position + Vector3.up * walkLegLen, 3f * walkLegLen, null, out fy)) return ", ankles: no floor under her";
            float leg = Mathf.Max(1e-3f, walkLegLen);
            return ", ankles above the floor " + ((legL.Ankle.position.y - fy) / leg).ToString("F2") + "/" + ((legR.Ankle.position.y - fy) / leg).ToString("F2") +
                   " legs, root " + ((transform.position.y - fy) / leg).ToString("F2") + " legs";
        }

        // ------------------------------------------------------------ every frame
        void MornUpdate()
        {
            Morning.DoorTick();
            MornHintStep();
            if (mornPhase == MPhase.Off) return;
            var u = OutU();
            if (u == null) return;

            if (outState == Out.InCar) { MornDone(mornWarped ? "warp" : "in the seat"); return; }
            if (u.uncleGoneForever || u.uncleAtHome) { MornDone("gone"); return; }
            MornPoseStep();

            var pctl = u.player != null ? u.player.GetComponent<RigidbodyControllerC>() : null;
            Vector3 pp = u.player != null ? u.player.transform.position : Vector3.zero;
            float now = Time.time;
            if (mornFreeAt < 0f && pctl != null && pctl.enabled)
            {
                mornFreeAt = now;
                Debug.Log("[Umamusume companion] Morning: you can walk");
                MornRecheck(u);
                mornWakeForward = Camera.main != null ? Camera.main.transform.forward : Vector3.zero;
            }
            MornViewLog();
            MornFeetStep();
            if (MorningWalk.UpdateDeparture(this)) return;
            MornTurnToYou(pp);

            // Wait for player control before using the wake camera to decide whether to seat her.
            if (mornFreeAt < 0f) { MornLog(u, pp); return; }
            float far = Flat(pp - transform.position).magnitude / MornMetre;
            if (life != null) life.MornStay = far < 6f;   // head and eyes on you while you are near
            if (!mornLeft && (MornLeftRoom(pp) || far > 6f))
            {
                mornLeft = true;
                Debug.Log("[Umamusume companion] Morning: you left the room (" + far.ToString("F1") + " m from her)");
            }
            bool sat = PlayerSat(), moving = CarSpeed() > 3f;
            movingFor = moving ? movingFor + Time.deltaTime : 0f;
            string why = mornLeft ? "you left the room" : sat ? "you sat in the car" : moving ? "the car moves" : null;
            var cam = Camera.main;
            bool unseen = cam != null && MornUnseen();
            if (cam != null && !unseen) mornSeenAtWake = true;
            // Some rooms place her just outside the initial view. Wait for a real look-away there.
            if (why == null && unseen && (mornSeenAtWake || Vector3.Angle(mornWakeForward, cam.transform.forward) > 15f))
                why = "you looked away";
            if (why == null) { mornSeenSince = -1f; MornLog(u, pp); return; }
            if (!unseen && mornSeenSince < 0f)
            {
                mornSeenSince = now;
                Debug.Log("[Umamusume companion] Morning: " + why + ", waiting until she is out of your sight");
            }
            string force = null;
            if (!unseen)
            {
                if (movingFor > 3f) force = "the car drove off";
                else if (sat && now - mornSeenSince > 20f) force = "seen 20 s while you sit in the car";
                else if (now - mornSeenSince > 60f) force = "seen 60 s";
            }
            if (!unseen && force == null) { MornLog(u, pp); return; }
            Debug.Log("[Umamusume companion] Morning: warp seat (" + why + (unseen ? ", out of your sight" : ", still seen: " + force) + ", " +
                      (now - mornAt).ToString("0") + " s after the wake)");
            mornWarpNow = true;
            Morning.DoorMute(u, 4f, "morning warp to her seat");
            WarpIn(u, "morning: " + why);
            mornWarpNow = false;
        }

        /// <summary>Standing in place she turns to face you when you walk round her (slowly, no steps).</summary>
        void MornTurnToYou(Vector3 pp)
        {
            if (mornPose != MPose.None || walkW > 0.1f) return;   // the wake pose faces him by itself
            var cam = Camera.main;
            Vector3 you = cam != null ? cam.transform.position : pp;
            Vector3 d = Flat(you - transform.position);
            if (d.sqrMagnitude < 1e-4f || d.magnitude > 6f * MornMetre) { mornTurning = false; return; }
            float ang = Vector3.Angle(Flat(transform.forward), d);
            if (ang > 45f) mornTurning = true;
            else if (ang < 8f) mornTurning = false;
            if (!mornTurning) return;
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(d.normalized, Vector3.up), 90f * Time.deltaTime);
        }
        bool mornTurning;

        void MornLog(UncleLogicC u, Vector3 pp)
        {
            if (Time.time < mornLogAt || Time.time - mornAt > 120f) return;
            mornLogAt = Time.time + 5f;
            Debug.Log("[Umamusume companion] Morning: " + mornPhase + "/" + outState + " t " + (Time.time - mornAt).ToString("0") + " s, she " + transform.position.ToString("F1") +
                      ", you " + pp.ToString("F1") + " (" + (Flat(pp - transform.position).magnitude / MornMetre).ToString("F1") + " m), left room " + mornLeft +
                      ", you sat " + PlayerSat());
        }

        void MornDone(string how)
        {
            MorningWalk.EndMorning(this);
            if (mornPhase == MPhase.Off) return;
            mornPhase = MPhase.Off;
            direct = false;
            mornTurning = false;
            if (life != null) life.MornStay = false;
            MornClearOffsets("morning over");
            if (mornBorderHeld)
            {
                mornBorderHeld = false;
                var u = OutU();
                bool greeted = mornLineSent && mornLineKey != null;   // she said wake_alarm / wake_crash in the room
                if (u != null && !u.uncleGoneForever && !u.uncleAtHome)
                {
                    if (greeted && !mornSilent)
                    {
                        // task 13: un_newday_01 "Р”РѕР±СЂРѕРµ СѓС‚СЂРѕ Рё РґРѕР±СЂРѕР№ СѓРґР°С‡Рё! РҐРѕСЂРѕС€Рѕ СЃРїР°Р»РѕСЃСЊ?" would be a second good morning;
                        // only the route hint (un_newday_02) is said, when you are near her, as the game would
                        mornHintPending = true;
                        mornHintAt = Time.time + 1f;
                        Debug.Log("[Umamusume companion] Morning: car greeting skipped (un_newday_01, she greeted you in the room); route hint un_newday_02 waits for you (" + how + ")");
                    }
                    else
                    {
                        // the game's morning talk (un_newday_01 + 02) as it would be without the mod
                        u.isReadyToTalkAboutBorder = true;
                        Debug.Log("[Umamusume companion] Morning: game's morning talk released (" + how + (greeted ? "" : ", no greeting in the room") + ")");
                    }
                }
            }
            if (mornPose != MPose.None && life != null) life.MornPoseEnd("morning over: " + how);
            mornPose = MPose.None;
            Debug.Log("[Umamusume companion] Morning: " + how + " (" + (Time.time - mornAt).ToString("0") + " s after the wake)");
        }

        // ------------------------------------------------------------ task 13: the route hint without the second good morning
        bool mornHintPending;
        float mornHintAt;

        /// <summary>Same trigger as the game's LetsGoBorderCross (you within 3x its talk distance), then only un_newday_02.</summary>
        void MornHintStep()
        {
            if (!mornHintPending || Time.time < mornHintAt) return;
            mornHintAt = Time.time + 0.25f;
            var u = OutU();
            if (u == null || u.uncleGoneForever || u.uncleAtHome || u.player == null) { mornHintPending = false; return; }
            if (Vector3.Distance(transform.position, u.player.transform.position) / 3f >= u.distanceCheck) return;
            // On purchase mornings, let her finish the receipt before starting the route hint.
            if (shopLine != null || (remarks != null && remarks.Busy) || u.isTalking || BubbleShown(u)) return;
            mornHintPending = false;
            StartCoroutine(MornRouteHint(u));
        }

        IEnumerator MornRouteHint(UncleLogicC u)
        {
            string text = MornGameLine("un_newday_02");
            if (string.IsNullOrEmpty(text)) { Debug.Log("[Umamusume companion] Morning: route hint: no text for un_newday_02"); yield break; }
            u.dialogueChecker = "DemoOutro";          // as LetsGoBorderCross
            u.StartCoroutine("DialogueCheck");
            yield return new WaitForSeconds(0.1f);
            if (!u.canSpeak) { Debug.Log("[Umamusume companion] Morning: route hint not said (the game says she can't speak now)"); yield break; }
            u.speechStack.Clear();
            u.speechStack.Add(text);
            u.StartCoroutine("DialogueSpeech");
            Debug.Log("[Umamusume companion] Morning: route hint said (un_newday_02)");
        }

        /// <summary>The game's own line: Language.Get(key, "Speech") lives in Assembly-CSharp-firstpass (not referenced here).</summary>
        static string MornGameLine(string key)
        {
            try
            {
                var t = Type.GetType("Language, Assembly-CSharp-firstpass");
                var m = t != null ? t.GetMethod("Get", new[] { typeof(string), typeof(string) }) : null;
                return m != null ? m.Invoke(null, new object[] { key, "Speech" }) as string : null;
            }
            catch (Exception e) { Debug.Log("[Umamusume companion] Morning: Language.Get failed: " + e.Message); return null; }
        }

        // ------------------------------------------------------------ helpers (also used by Outing.cs)
        internal bool MornHolding { get { return mornPhase == MPhase.Hold; } }

        bool IsMotelNode(UncleLogicC u, Transform t)
        {
            if (t == null) return false;
            var ml = mornMotel;
            if (ml == null && u != null && u.motel != null) ml = u.motel.GetComponent<MotelLogicC>();
            if (ml == null) return false;
            if (t == ml.lobbyNode) return true;
            if (ml.doorNodes != null) foreach (var n in ml.doorNodes) if (n == t) return true;
            if (ml.roomNodes != null) foreach (var n in ml.roomNodes) if (n == t) return true;
            return false;
        }

        bool MornLeftRoom(Vector3 pp)
        {
            float leg = walkLegLen;
            Vector3 center = mornBed != null && mornBed.path[2] != null ? mornBed.path[2].position : transform.position;
            float radius = 6f * leg;
            if (mornMotel != null && mornMotel.roomNodes != null && mornMotel.doorNodes != null)
            {
                int r = mornMotel.roomNumber;
                if (r >= 0 && r < mornMotel.roomNodes.Length && r < mornMotel.doorNodes.Length && mornMotel.roomNodes[r] != null && mornMotel.doorNodes[r] != null)
                {
                    center = mornMotel.roomNodes[r].position;
                    radius = Mathf.Max(5f * leg, 1.6f * Flat(center - mornMotel.doorNodes[r].position).magnitude);
                }
            }
            return Flat(pp - center).magnitude > radius;
        }

        /// <summary>Warp-in during the morning: from the morning code itself (out of sight or forced), or when the car drives off.</summary>
        bool MornWarpOk(string why)
        {
            if (mornPhase == MPhase.Off || mornWarpNow || why == "the car drove off") return true;
            return MornUnseen();
        }

        bool MornUnseen()
        {
            var cam = Camera.main;
            if (cam == null) return true;
            Vector3 head = uHead != null ? uHead.position : transform.position + Vector3.up * 1.6f * walkLegLen;
            Vector3 vp = cam.WorldToViewportPoint(head);
            if (vp.z <= 0f || vp.x < -0.05f || vp.x > 1.05f || vp.y < -0.05f || vp.y > 1.05f) return true;
            var u = OutU();
            Transform player = u != null && u.player != null ? u.player.transform : null;
            return MornSightBlocked(cam.transform.position, head, player);   // a wall or a door between you and her head
        }

        // ------------------------------------------------------------ probe: where things are at the real morning
        void MornProbe(UncleLogicC u, BedLogicC b, string when)
        {
            var sb = new StringBuilder("[Umamusume companion] Morning: probe " + when + ": bed " + b.name + " path1 " + b.path[1].position.ToString("F1") +
                " path2 " + b.path[2].position.ToString("F1") + " (fwd " + Flat(b.path[2].forward).normalized.ToString("F2") + ")" +
                "; she " + transform.position.ToString("F1") + " seat " + (u.uncleCarTransform != null ? u.uncleCarTransform.position.ToString("F1") : "-") +
                "; you " + (u.player != null ? u.player.transform.position.ToString("F1") : "-") + "; leg " + walkLegLen.ToString("F2"));
            try
            {
                var ml = mornMotel;
                if (ml != null)
                {
                    int r = ml.roomNumber;
                    Transform door = ml.doorNodes != null && r >= 0 && r < ml.doorNodes.Length ? ml.doorNodes[r] : null;
                    Transform room = ml.roomNodes != null && r >= 0 && r < ml.roomNodes.Length ? ml.roomNodes[r] : null;
                    sb.Append("; motel room " + r + " door " + P(door) + " roomNode " + P(room));
                }
                else sb.Append("; no MotelLogicC");
            }
            catch (Exception e) { sb.Append("; probe failed: " + e.Message); }
            Debug.Log(sb.ToString());
        }

        static string P(Transform t) { return t != null ? t.position.ToString("F1") : "-"; }
    }
}
