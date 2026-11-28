using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Random = UnityEngine.Random;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Pranks of a character with features=pranks (Sweep Tosho). Task 34 core, task 37 refactor: every prank is its own class
    /// (PrankKind, one file each: PrankWash.cs ...), found by reflection at start. A prank runs as a beat (her hand to the
    /// target through WantReachTo / ReachTo, the action fires when the hand is there or as far as it goes), then a 10 s window:
    /// a pat = caught (prank_caught, face shy, giggle), the player undoes it (the kind's UndoneBy) = undone (prank_undone, sulk).
    /// Scheduler: one every PrankMinutes x 0.7..1.25 of driving, none in the first 180 s after boarding, never the same twice
    /// in a row, only when nothing else is going on (PrankBlock). Log: [Umamusume companion] Prank: id start|acted|done|caught|undone|skip why.
    /// Also here: her radio trick (own will, not a prank: next song / louder, pleased face, no giggle, no caught/undone) and the hat hold while
    /// her window is open (any pack with a "hat" prop).
    /// </summary>
    public partial class Life
    {
        /// <summary>Setting "Pranks" (on/off) and "PrankMinutes" (average minutes of driving between pranks, 0 = never).</summary>
        public static bool PranksOn = true;
        public static float PrankMinutes = 12f;
        /// <summary>Hand on the hat while her window is open (debug panel toggles it).</summary>
        public static bool HatHold = false;

        // ------------------------------------------------------------ one prank
        /// <summary>
        /// A prank. Subclass it as a class nested in Life, in its own file; it registers by itself (reflection, parameterless
        /// constructor). The core calls, in order: Can -> Start (her beat) -> Arm every beat frame -> Act at the beat's ActAt
        /// (hand there or 1 s late) -> Checked after CheckDelay -> her Line -> UndoneBy every frame of the 10 s window -> End.
        /// </summary>
        abstract class PrankKind
        {
            /// <summary>Her (set by the core before any call).</summary>
            public Life L;
            /// <summary>Short id for the log and the debug panel ("N1").</summary>
            public abstract string Id { get; }
            /// <summary>Null = it can start now, else why not (logged).</summary>
            public abstract string Can();
            /// <summary>Her beat for it (Dur, Look, Face, ActAt; the core names it prank_id and sets WaitReach). note goes to the log.</summary>
            public abstract Beat Start(out string note);
            /// <summary>Every frame of the beat: where her hand goes (L.WantReachTo).</summary>
            public virtual void Arm(Beat b, float env) { }
            /// <summary>At ActAt: do it, set How. Null = done, else why it was skipped.</summary>
            public abstract string Act(float now);
            /// <summary>What she did, for the log ("wipers on").</summary>
            public string How;
            /// <summary>Seconds after Act until Checked.</summary>
            public virtual float CheckDelay { get { return 0.5f; } }
            /// <summary>Did it take? Null = yes, else why not (skip, no line).</summary>
            public virtual string Checked(float now) { return null; }
            /// <summary>Her line when it worked (speech key).</summary>
            public virtual string Line { get { return "prank_" + Id.ToLowerInvariant(); } }
            /// <summary>Each frame of the 10 s window: how the player undid it, or null. L.PrankEnd(why) ends it quietly.</summary>
            public virtual string UndoneBy(float now) { return null; }
            /// <summary>The prank is over (any way): clean up own state.</summary>
            public virtual void End() { }
            /// <summary>Task 38: every frame (Sweep), whatever the core is doing: own watchers and after-effects (N1 wipers off by
            /// herself, N4 waits for the throttle). idle = in her seat, awake, not talking or reacting.</summary>
            public virtual void Tick(float now, bool inCar, bool idle) { }
            /// <summary>Task 38: debug "Prank now" picked this kind. Null = start it the normal way, else the kind took over
            /// (e.g. N4 arms and waits for the throttle) and this is the panel's answer.</summary>
            public virtual string DebugStart() { return null; }
        }

        static Type[] prankTypes;
        List<PrankKind> prankKinds;

        /// <summary>All PrankKind classes in the mod, one instance each, ordered by id.</summary>
        List<PrankKind> PrankKinds
        {
            get
            {
                if (prankKinds != null) return prankKinds;
                prankKinds = new List<PrankKind>();
                if (prankTypes == null)
                {
                    var l = new List<Type>();
                    try
                    {
                        foreach (var t in typeof(Life).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                            if (t.IsSubclassOf(typeof(PrankKind)) && !t.IsAbstract) l.Add(t);
                    }
                    catch (Exception e) { Debug.LogWarning("[Umamusume companion] Prank: kinds lookup failed: " + e.Message); }
                    prankTypes = l.ToArray();
                }
                foreach (var t in prankTypes)
                {
                    try
                    {
                        var k = (PrankKind)Activator.CreateInstance(t, true);
                        k.L = this;
                        prankKinds.Add(k);
                    }
                    catch (Exception e) { Debug.LogWarning("[Umamusume companion] Prank: " + t.Name + " not made: " + e.Message); }
                }
                prankKinds.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                var ids = new List<string>();
                foreach (var k in prankKinds) ids.Add(k.Id);
                PLog(prankKinds.Count == 0 ? "none built" : "kinds " + string.Join(", ", ids.ToArray()));
                return prankKinds;
            }
        }

        const float PrankWindow = 10f;
        const float FirstPrankDelay = 180f;
        const float GiggleLen = 1.8f;

        enum PState { None, Doing, Check, Open }
        PState pState;
        PrankKind pk;
        string lastPrank, dueBlock;
        float pCheckAt, pOpenUntil, pActAt, pClock = -1f, pFirstLeft;
        bool pWasInCar;
        int debugPrank;
        RadioVolLogicC volCache;

        // her line after a prank: waits for a free bubble
        string pLine;
        float pLineAt, pLineUntil;

        // giggle, sulk, owning up
        float giggleAt = -1f, giggleWant, giggleW;
        float sulkUntil = -1f, sulkLen, caughtUntil = -1f;
        bool sulkClip;

        bool PranksHere { get { return CharFeatures.Has("pranks"); } }
        static void PLog(string s) { Debug.Log("[Umamusume companion] Prank: " + s); }

        /// <summary>A prank is under way or its 10 s window is open: no radio/window moves of her own.</summary>
        bool PrankBusy { get { return pState != PState.None; } }

        /// <summary>The sulk owns the head even over a gesture clip (prank_sulk: arms crossed, head free).</summary>
        bool SulkHead { get { return Time.time < sulkUntil; } }

        string[] randomGestures, randomGesturesSrc;

        /// <summary>Gesture clips she may pick as bored beats: all but prank_* (those only play from a prank).</summary>
        string[] RandomGestures
        {
            get
            {
                if (GestureNames == null) return null;
                if (randomGesturesSrc != GestureNames)
                {
                    var l = new List<string>();
                    foreach (var g in GestureNames) if (!g.StartsWith("prank_", StringComparison.OrdinalIgnoreCase) && g != "play_sit" && g != "look_up") l.Add(g);
                    randomGestures = l.ToArray();
                    randomGesturesSrc = GestureNames;
                }
                return randomGestures;
            }
        }

        bool HasGesture(string n)
        {
            if (GestureNames == null) return false;
            foreach (var g in GestureNames) if (string.Equals(g, n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        RadioVolLogicC Vol()
        {
            if (volCache == null && radio != null && radio.radioVolume != null) volCache = radio.radioVolume.GetComponent<RadioVolLogicC>();
            return volCache;
        }

        float NextPrankGap() { return Random.Range(0.7f, 1.25f) * Mathf.Max(0.5f, PrankMinutes) * 60f; }

        // ------------------------------------------------------------ per frame (from Update, after Extras)
        void PrankStep(float dt, float now, bool inCar, bool slp, bool talking, bool reacting)
        {
            HatStep(inCar, slp);
            if (!PranksHere) return;
            ProbeStep(dt, now, inCar, slp);
            var kinds = PrankKinds;

            if (inCar && !pWasInCar)
            {
                pFirstLeft = FirstPrankDelay;
                if (pClock < 0f) pClock = NextPrankGap();
            }
            pWasInCar = inCar;
            if (pFirstLeft > 0f && inCar && PlayerSeated()) pFirstLeft -= dt;

            switch (pState)
            {
                case PState.Doing:
                    // the beat was cut (she started talking, a fright...) before the action
                    if (beat == null || !beat.Name.StartsWith("prank_")) PrankEnd("skip interrupted");
                    break;
                case PState.Check:
                    if (now >= pCheckAt) PrankChecked(now);
                    break;
                case PState.Open:
                    if (!inCar || slp) { PrankEnd("end, " + (!inCar ? "she left her seat" : "asleep")); break; }
                    // the 10 s start when her own line is over
                    if (pLine != null || (RemarkBusy != null && RemarkBusy()))
                        pOpenUntil = Mathf.Max(pOpenUntil, Mathf.Min(now + PrankWindow, pActAt + 25f));
                    string how = null;
                    try { how = pk.UndoneBy(now); }
                    catch (Exception e) { Debug.LogWarning("[Umamusume companion] Prank: " + pk.Id + " UndoneBy: " + e.Message); }
                    if (pState != PState.Open) break;   // the kind ended it itself
                    if (how != null) { PrankUndone(now, how); break; }
                    if (now > pOpenUntil) PrankEnd("end, no reaction");
                    break;
            }

            // scheduler (nothing to schedule while no prank is built)
            if (PranksOn && PrankMinutes > 0.01f && pState == PState.None && kinds.Count > 0)
            {
                if (pClock < 0f) pClock = NextPrankGap();
                bool driving = inCar && !slp && PlayerSeated() && SpeedKmh() > 15f;
                if (driving && pFirstLeft <= 0f && pClock > 0f) pClock -= dt;
                if (pClock <= 0f && pFirstLeft <= 0f)
                {
                    string block = PrankBlock(now, inCar, slp, talking, reacting);
                    if (block == null && !driving) block = "not driving";
                    if (block != null)
                    {
                        if (block != dueBlock) { dueBlock = block; PLog("due, waiting: " + block); }
                    }
                    else
                    {
                        dueBlock = null;
                        string why;
                        var pick = PickPrank(out why);
                        if (pick == null) { PLog("skip " + why); pClock = Random.Range(60f, 120f); }
                        else pClock = StartPrank(pick, false) == null ? NextPrankGap() : Random.Range(60f, 120f);
                    }
                }
            }

            bool kIdle = inCar && !slp && !talking && !reacting;
            foreach (var k in kinds)
            {
                try { k.Tick(now, inCar, kIdle); }
                catch (Exception e) { Debug.LogWarning("[Umamusume companion] Prank: " + k.Id + " Tick: " + e.Message); }
            }

            PrankLines(now);
            PrankLooks(now, inCar, slp, talking, reacting);
        }

        /// <summary>Why a prank can't start now (null = free). Recon 32 §7.</summary>
        string PrankBlock(float now, bool inCar, bool slp, bool talking, bool reacting)
        {
            if (!inCar) return "outside";
            if (slp || Doze.Gate) return "asleep";
            if (!PlayerSeated()) return "player out";
            if (talking || talkingNow) return "talking";
            if (reacting) return "reacting";
            var u = logic;
            if (u != null && u.restrictTalk) return "border";
            if (u != null && (u.withinRangeOfMotel || u.hasSaidLetsCheckIn)) return "motel";
            if (DozeDialog(u)) return "conversation";
            if (ReturnTrip.Playing) return "return trip talk";
            if (CanSay != null && !CanSay()) return "bubble busy";
            if (GestureWeight > 0.01f || act != Act.None || patT >= 0f) return "busy";
            if (beat != null && beat.ActAt >= 0f && !beat.Acted) return "busy";   // her own radio/window move
            if (now < sulkUntil || giggleAt >= 0f || now < caughtUntil) return "busy";
            return null;
        }

        string CanSafe(PrankKind k)
        {
            try { return k.Can(); }
            catch (Exception e) { return "error " + e.Message; }
        }

        PrankKind PickPrank(out string why)
        {
            var ok = new List<PrankKind>();
            var no = new List<string>();
            foreach (var k in PrankKinds)
            {
                string r = k.Id == lastPrank && PrankKinds.Count > 1 ? "same as last" : CanSafe(k);
                if (r != null) no.Add(k.Id + " " + r);
                else ok.Add(k);
            }
            why = no.Count > 0 ? string.Join(", ", no.ToArray()) : "none built";
            return ok.Count > 0 ? ok[Random.Range(0, ok.Count)] : null;
        }

        /// <summary>Starts the prank's beat. Null = started, else why not.</summary>
        string StartPrank(PrankKind k, bool debug)
        {
            string r = CanSafe(k);
            if (r != null) { PLog(k.Id + " skip " + r + (debug ? " (debug)" : "")); return r; }
            string note = null;
            Beat b = null;
            try { b = k.Start(out note); }
            catch (Exception e) { Debug.LogWarning("[Umamusume companion] Prank: " + k.Id + " Start: " + e.Message); }
            if (b == null) { PLog(k.Id + " skip no beat"); return "no beat"; }
            if (GestureWeight > 0f && StopGesture != null) StopGesture();
            pk = k;
            lastPrank = k.Id;
            pState = PState.Doing;
            k.How = null;
            b.Name = "prank_" + k.Id.ToLowerInvariant();
            b.WaitReach = true;
            b.T = 0f;
            beat = b;
            PLog(k.Id + " start" + (debug ? " (debug)" : "") + (note != null ? ", " + note : ""));
            return null;
        }

        /// <summary>From ArmIntent for a prank_* beat.</summary>
        void PrankArm(Beat b, float env)
        {
            if (pk == null || pState != PState.Doing) return;
            try { pk.Arm(b, env); }
            catch (Exception e) { Debug.LogWarning("[Umamusume companion] Prank: " + pk.Id + " Arm: " + e.Message); }
        }

        /// <summary>From DoAction at the beat's ActAt: the prank itself.</summary>
        void PrankAct(float now)
        {
            if (pk == null || pState != PState.Doing) return;
            string r;
            try { r = pk.Act(now); }
            catch (Exception e) { r = "error " + e.Message; }
            if (r != null) { PrankEnd("skip " + r); return; }
            pState = PState.Check;
            pCheckAt = now + pk.CheckDelay;
            pActAt = now;
            PLog(pk.Id + " acted: " + (pk.How ?? "?") + (ReachDone ? ", hand short " + ReachShort.ToString("F3") + " u" : ", hand still on its way"));
        }

        void PrankChecked(float now)
        {
            string r;
            try { r = pk.Checked(now); }
            catch (Exception e) { r = "error " + e.Message; }
            if (r != null) { PrankEnd("skip " + r); return; }
            pState = PState.Open;
            pOpenUntil = now + PrankWindow;
            QueueLine(pk.Line, 0.5f, 6f);
            giggleAt = now + 0.1f;
            Attend(Look.Player, 2.2f, 0.9f);
            PLog(pk.Id + " done: " + (pk.How ?? "?") + ", 10 s for caught/undone");
        }

        /// <summary>The prank is over without a sulk or a catch: log and let the kind clean up.</summary>
        void PrankEnd(string why)
        {
            var k = pk;
            pState = PState.None;
            pk = null;
            if (k == null) return;
            PLog(k.Id + " " + why);
            try { k.End(); }
            catch (Exception e) { Debug.LogWarning("[Umamusume companion] Prank: " + k.Id + " End: " + e.Message); }
        }

        void PrankUndone(float now, string how)
        {
            PrankEnd("undone (" + how + ")");
            giggleAt = -1f;
            QueueLine("prank_undone", 0.3f, 5f);
            sulkLen = Random.Range(5f, 10f);
            sulkUntil = now + sulkLen;
            sulkClip = false;
            if (HasGesture("prank_sulk") && PlayGesture != null && PlayGesture("prank_sulk") > 0f)
            {
                sulkClip = true;
                GestureForced = true;   // her line must not cut the clip (talking stops unforced gestures)
            }
            PLog("sulk " + sulkLen.ToString("F0") + " s, " + (sulkClip ? "clip prank_sulk" : "no prank_sulk clip, head to the window only"));
        }

        /// <summary>From CheckPat (a hit, she is awake), before the normal pat line. True = it was a catch.</summary>
        bool PrankCatch(float now)
        {
            if (pState != PState.Open || !PranksHere) return false;
            PrankEnd("caught (pat)");
            QueueLine("prank_caught", 0.4f, 6f);
            caughtUntil = now + 4.5f;
            giggleAt = now + 2.6f;   // after the pat (2.4 s)
            return true;
        }

        // ------------------------------------------------------------ her radio trick (own will, task 37; was prank P1)
        float nextRadioTrickOk = -1f;
        bool trickVolUp;

        /// <summary>Sweep, radio playing, every 5-10 min: she skips the song or turns it up. Not a prank: no caught/undone, no sulk,
        /// no mockery (bug 1: no smug face, no giggle, no look at him; "hum" face as if she likes the new song).</summary>
        bool RadioTrickOk(float now)
        {
            if (!PranksHere || !PranksOn || radio == null || car == null || !car.radioOn) return false;
            if (News.OnAir) return false;   // task 52: not during a news bulletin
            if (nextRadioTrickOk < 0f) nextRadioTrickOk = now + Random.Range(300f, 600f);
            return now >= nextRadioTrickOk;
        }

        Beat RadioTrickBeat()
        {
            var v = Vol();
            // louder only if it plays quietly; else the next song
            trickVolUp = v != null && v.volumeSetting == 1 && Random.value < 0.5f;
            Debug.Log("[Umamusume companion] radio trick: start, " + (trickVolUp ? "volume up" : "next song"));
            // bug 1 (user): no mockery: no smug face, no giggle, no look at him; she just likes the new song
            return new Beat { Name = "radio_trick", Dur = 2.4f, Look = Look.Radio, LookW = 0.9f, Face = "focused", FaceW = 0.4f, ActAt = 0.9f, WaitReach = true };
        }

        void RadioTrickArm(Beat b)
        {
            var v = trickVolUp ? Vol() : null;
            var knob = v != null && v.handle != null ? v.handle : radio != null ? radio.handle : null;
            if (knob != null) WantReachTo(trickVolUp ? "volume knob" : "radio", knob.transform, false, false, ReachCurve(b));
        }

        void RadioTrickAct(float now)
        {
            nextRadioTrickOk = now + Random.Range(300f, 600f);
            if (radio == null || car == null || !car.radioOn) { Debug.Log("[Umamusume companion] radio trick: skip, radio off"); return; }
            var v = Vol();
            string how;
            if (trickVolUp && v != null && v.volumeSetting == 1)
            {
                // the field, not Trigger: Trigger makes the game complain about the noise (recon 32 §1)
                v.volumeSetting = 0;
                TurnKnob(v);
                how = "volume up";
            }
            else { radio.NextSong(); how = "next song"; }
            sheTurnedRadio = false;   // no radio_off_by_player line if he switches it off now
            Debug.Log("[Umamusume companion] radio trick: " + how + (ReachDone ? ", hand short " + ReachShort.ToString("F3") + " u" : ", hand still on its way"));
            QueueLine("prank_radio", 0.5f, 6f);
            Emote("hum", 3f, 0.7f);
        }

        /// <summary>The knob turns like the player's loud click (RadioVolLogicC.Trigger), without its oncomplete complaint.</summary>
        static void TurnKnob(RadioVolLogicC v)
        {
            try
            {
                if (v.handle != null)
                    iTween.RotateBy(v.handle, iTween.Hash("amount", v.volumeRotAmount2, "time", v.tweenTime, "islocal", true, "easetype", v.dialEaseType));
            }
            catch (Exception e) { Debug.LogWarning("[Umamusume companion] radio trick: volume knob turn failed: " + e.Message); }
        }

        void QueueLine(string key, float delay, float window)
        {
            pLine = key;
            pLineAt = Time.time + delay;
            pLineUntil = pLineAt + window;
        }

        void PrankLines(float now)
        {
            if (pLine == null || now < pLineAt) return;
            if (Say == null) { pLine = null; return; }
            if (CanSay == null || CanSay())
            {
                Debug.Log("[Umamusume companion] remark: " + pLine);
                Say(pLine);
                pLine = null;
            }
            else if (now > pLineUntil) { PLog("line " + pLine + " skipped, bubble busy"); pLine = null; }
        }

        /// <summary>Face and head for the giggle, the sulk and owning up (after Extras: these win).</summary>
        void PrankLooks(float now, bool inCar, bool slp, bool talking, bool reacting)
        {
            giggleWant = 0f;
            if (!inCar || slp)
            {
                giggleAt = -1f;
                if (sulkUntil > now) sulkUntil = now;
                caughtUntil = -1f;
                return;
            }
            bool idle = !talking && !reacting;
            if (now < sulkUntil)
            {
                // the sulk runs after her line: while she talks the clock waits
                if (talking || pLine == "prank_undone" || (RemarkBusy != null && RemarkBusy())) sulkUntil = Mathf.Max(sulkUntil, now + Mathf.Min(sulkLen, 4f));
                Face.Want("annoyed", 0.85f, 0.25f);
                if (idle) { curLook = Look.Window; curLookW = 0.95f; }
            }
            else if (sulkClip)
            {
                sulkClip = false;
                if (GestureWeight > 0f && StopGesture != null) StopGesture();
            }
            if (now < caughtUntil)
            {
                if (Face.Has("shy")) Face.Want("shy", 0.9f, 0.2f);
                else { Face.Want("smile", 0.7f, 0.2f); Face.Want("worried", 0.35f, 0.2f); }
                if (idle && patT < 0f) { curLook = Look.Player; curLookW = 0.8f; }
            }
            if (giggleAt >= 0f && now >= giggleAt)
            {
                float t = now - giggleAt;
                if (t >= GiggleLen) giggleAt = -1f;
                else
                {
                    giggleWant = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(Mathf.Min(t / 0.3f, (GiggleLen - t) / 0.4f)));
                    Face.Want("happy", 0.9f * giggleWant, 0.12f);
                }
            }
        }

        // ------------------------------------------------------------ debug panel
        /// <summary>Debug "Prank now": the next built prank in turn, right away.</summary>
        public string DebugPrank()
        {
            if (!PranksHere) return "pranks: not this character (features=" + CharFeatures.List + ")";
            var kinds = PrankKinds;
            if (kinds.Count == 0) { PLog("none built"); return "no pranks yet (they come in tasks 38+); the radio one is \"Radio trick now\""; }
            var k = kinds[debugPrank++ % kinds.Count];
            if (!inCarNow) { PLog(k.Id + " skip outside (debug)"); return k.Id + " skip: she is not in her seat"; }
            if (asleepNow || Doze.Gate) { PLog(k.Id + " skip asleep (debug)"); return k.Id + " skip: she is asleep"; }
            if (pState == PState.Doing || pState == PState.Check) return "a prank is under way";
            if (pState == PState.Open) PrankEnd("end, debug restart");
            string own = null;
            try { own = k.DebugStart(); }
            catch (Exception e) { Debug.LogWarning("[Umamusume companion] Prank: " + k.Id + " DebugStart: " + e.Message); }
            if (own != null) return own;
            beat = null;
            pClock = Mathf.Max(pClock, 60f);
            string r = StartPrank(k, true);
            return r == null ? k.Id + " start" : k.Id + " skip: " + r;
        }

        /// <summary>How many pranks this character has (0 without features=pranks). Debug panel label.</summary>
        public int PranksBuilt { get { return PranksHere ? PrankKinds.Count : 0; } }

        /// <summary>Debug "Radio trick now" (Sweep): next song or louder, right away (radio must play).</summary>
        public string DebugRadioTrick()
        {
            if (!PranksHere) return "not this character (features=" + CharFeatures.List + ")";
            if (!inCarNow || asleepNow || Doze.Gate) return "she is not awake in her seat";
            if (radio == null || car == null || !car.radioOn) return "radio is off";
            if (PrankBusy) return "a prank is under way";
            nextBeat = Time.time + 30f;
            beat = RadioTrickBeat();
            beat.T = 0f;
            return "radio trick: " + (trickVolUp ? "volume up" : "next song");
        }

        public string DebugHatHold()
        {
            HatHold = !HatHold;
            return "hat hold " + (HatHold ? "on" : "off") + (Props.Get("hat") == null ? " (this pack has no hat)" : "");
        }

        // ------------------------------------------------------------ pose (from PoseBones, after PoseExtras)
        void PosePranks(float dt)
        {
            giggleW = Mathf.MoveTowards(giggleW, Enabled && inCarNow ? giggleWant : 0f, dt / 0.25f);
            HatPose(dt);
            PoseBrim(dt);   // task 40: hand on the brim (N5, N6) and the hat copy in her hand
            if (giggleW <= 0.001f || armL == null || armR == null || head == null) return;
            Vector3 fwd, up, winDir;
            Arm outer, inner;
            ArmSides(out outer, out inner, out fwd, out up, out winDir);
            float w = giggleW, now = Time.time;
            // shoulders bounce (~6 Hz) and the head nods a little with the laugh
            float bounce = (0.5f + 0.5f * Mathf.Sin(now * Mathf.PI * 2f * 6f)) * 3f * w;
            LiftShoulder(shoulderL, armL, up, bounce);
            LiftShoulder(shoulderR, armR, up, bounce);
            Quaternion H0 = head.Deform();
            head.T.rotation = Quaternion.AngleAxis(Mathf.Abs(Mathf.Sin(now * Mathf.PI * 6f)) * 3f * w, H0 * Vector3.right) * head.T.rotation;
            GiggleHand(outer, winDir, up, w);
        }

        bool giggleLogged;

        /// <summary>
        /// "Fufu" giggle: the window-side hand in front of the lips, palm to the mouth, back of the hand out, fingers slanted
        /// up across to her other cheek and a little bent. Bug 1 (v0.39.1): v0.39.0 only turned the knuckles up, the palm roll
        /// came from the uncle's lap pose and the palm faced the player. Numbers checked in the Laika: blender/giggle_preview.py.
        /// </summary>
        void GiggleHand(Arm a, Vector3 winDir, Vector3 up, float w)
        {
            Vector3 c;
            float r;
            if (a == null || !HeadSphere(out c, out r)) return;
            Quaternion H = head.Deform();
            Vector3 hf = H * Vector3.forward, hu = H * Vector3.up, hr = H * Vector3.right;
            Vector3 across = Vector3.Dot(hr, winDir) > 0f ? -hr : hr;          // towards her other cheek, in the head's frame
            Vector3 mouth = c + hf * (r * 0.85f) - hu * (r * 0.6f);
            Vector3 palm = mouth + hf * (r * 0.4f) - hu * (r * 0.3f);
            Vector3 axis = (hu * 0.65f + across * 0.75f).normalized;
            Vector3 knuck = Knuckles(a);
            float k = (knuck - a.Hand.position).magnitude;
            if (k < 1e-5f) k = ArmLen(a) * 0.12f;
            Vector3 wrist = palm - axis * (k * 0.6f);
            float sw = Mathf.SmoothStep(0f, 1f, w);
            SolveArm(a, wrist, -up + winDir * 0.6f - hf * 0.2f, sw);
            bool full = OrientHand(a, axis, -hf, sw);
            PoseFingers(a, false, sw);
            CurlFingers(a, sw);
            if (!giggleLogged && w >= 0.99f)
            {
                giggleLogged = true;
                Vector3 n = PalmNormal(a);
                Vector3 pc = (Knuckles(a) + a.Hand.position) * 0.5f;
                var cam = Camera.main;
                PLog("giggle hand " + a.Hand.name + ": wrist off target by " + ((a.Hand.position - wrist).magnitude / r).ToString("F2")
                    + " head radii, palm . to mouth " + Vector3.Dot(n.normalized, (mouth - pc).normalized).ToString("F2")
                    + (cam != null ? ", palm . to the player " + Vector3.Dot(n.normalized, (cam.transform.position - pc).normalized).ToString("F2") : "")
                    + (full ? "" : " (no index/pinky bones: knuckles only)"));
            }
        }

        /// <summary>Palm normal (out of the palm) from the finger roots: Unity cross(finger dir, index root - pinky root),
        /// sign by hand (right +1, left -1; checked on the Sweep rig). Zero if the bones are missing.</summary>
        Vector3 PalmNormal(Arm a)
        {
            var list = a == armL ? fingersL : fingersR;
            Vector3 idx = Vector3.zero, pinky = Vector3.zero;
            bool hi = false, hp = false;
            foreach (var f in list)
            {
                if (f.T.parent != a.Hand) continue;
                if (f.Index) { idx = f.T.position; hi = true; }
                else if (f.T.name.StartsWith("Pinky") || f.T.name.StartsWith("Little")) { pinky = f.T.position; hp = true; }
            }
            if (!hi || !hp) return Vector3.zero;
            return Vector3.Cross(Knuckles(a) - a.Hand.position, idx - pinky) * (a == armR ? 1f : -1f);
        }

        /// <summary>Turn the hand so the knuckles point along axis and the palm faces palmTo (both, not just the first).
        /// False = no palm normal, knuckles only.</summary>
        bool OrientHand(Arm a, Vector3 axis, Vector3 palmTo, float w)
        {
            Vector3 cur = Knuckles(a) - a.Hand.position;
            if (cur.sqrMagnitude < 1e-10f) return false;
            Vector3 n = PalmNormal(a);
            bool full = n.sqrMagnitude > 1e-12f && Vector3.Cross(cur, n).sqrMagnitude > 1e-12f && Vector3.Cross(axis, palmTo).sqrMagnitude > 1e-8f;
            Quaternion d = full
                ? Quaternion.LookRotation(axis, palmTo) * Quaternion.Inverse(Quaternion.LookRotation(cur, n))
                : Quaternion.FromToRotation(cur, axis);
            a.Hand.rotation = Quaternion.Slerp(Quaternion.identity, d, w) * a.Hand.rotation;
            return full;
        }

        static readonly float[] GiggleCurl = { 20f, 30f, 25f };   // degrees per joint 01..03 (ring x1.1, pinky x1.25)

        /// <summary>Relaxed fingers: each joint of index..pinky bends towards the palm (thumb untouched).</summary>
        void CurlFingers(Arm a, float w)
        {
            Vector3 pn = PalmNormal(a);
            if (pn.sqrMagnitude < 1e-12f || w <= 0.001f) return;
            pn.Normalize();
            var list = new List<Finger>(a == armL ? fingersL : fingersR);
            list.Sort((x, y) => string.CompareOrdinal(x.T.name, y.T.name));   // Index_01, _02, _03, Middle_01 ...: parents first
            foreach (var f in list)
            {
                if (f.Thumb) continue;
                string nm = f.T.name;
                int j = nm.Length > 0 ? nm[nm.IndexOf('_') + 2] - '1' : -1;
                if (j < 0 || j > 2) continue;
                float extra = nm.StartsWith("Pinky") || nm.StartsWith("Little") ? 1.25f : nm.StartsWith("Ring") ? 1.1f : 1f;
                Vector3 fd = f.T.childCount > 0 ? f.T.GetChild(0).position - f.T.position : f.T.position - f.T.parent.position;
                Vector3 ax = Vector3.Cross(fd, pn);   // +angle about f x n moves the finger towards n (Unity: v -> a x v)
                if (ax.sqrMagnitude < 1e-12f) continue;
                f.T.rotation = Quaternion.AngleAxis(GiggleCurl[j] * extra * w, ax.normalized) * f.T.rotation;
            }
        }

        // ------------------------------------------------------------ hat hold while her window is open
        Prop hatProp;
        bool hatTried, hatWant, hatLogged;
        Vector3[] hatRing;       // the hat's widest points in 8 directions, in the head bone's local space
        float hatW;
        Arm hatArm;

        void HatInit()
        {
            if (hatTried) return;
            hatTried = true;
            hatProp = Props.Get("hat");
            if (hatProp == null || head == null || hatProp.Renderer == null || hatProp.Renderer.sharedMesh == null) { hatProp = null; return; }
            var bones = hatProp.Renderer.bones;
            var bp = hatProp.Renderer.sharedMesh.bindposes;
            int hi = Array.IndexOf(bones, head.T);
            if (hi < 0 || hi >= bp.Length) { Debug.Log("[Umamusume companion] hat: head bone not in the hat renderer, no hat hold"); hatProp = null; return; }
            // head-local axes of her bind space (Y up, Z forward): constant, from the head's rest rotation
            Quaternion inv = Quaternion.Inverse(head.Rest);
            Vector3 fL = inv * Vector3.forward, rL = inv * Vector3.right;
            hatRing = new Vector3[8];
            var best = new float[8];
            for (int i = 0; i < 8; i++) best[i] = float.MinValue;
            var verts = hatProp.RestPositions;
            for (int v = 0; v < verts.Length; v++)
            {
                Vector3 p = bp[hi].MultiplyPoint3x4(verts[v]);
                for (int i = 0; i < 8; i++)
                {
                    float ang = i * Mathf.PI / 4f;
                    float d = Vector3.Dot(p, fL * Mathf.Cos(ang) + rL * Mathf.Sin(ang));
                    if (d > best[i]) { best[i] = d; hatRing[i] = p; }
                }
            }
            Debug.Log("[Umamusume companion] hat: " + verts.Length + " verts, brim front " + best[0].ToString("F3") + " / side " + best[2].ToString("F3") + " (head-local units)");
        }

        void HatStep(bool inCar, bool slp)
        {
            HatInit();
            hatWant = HatHold && hatProp != null && Enabled && inCar && !slp && windowOpen && SpeedKmh() > 25f && !HatAway && brimW < 0.01f;   // task 40: not while her hat is off or her hand is on the brim
        }

        void HatPose(float dt)
        {
            if (hatProp == null || armL == null || armR == null || head == null) { hatW = 0f; return; }
            Vector3 fwd, up, winDir;
            Arm outer, inner;
            ArmSides(out outer, out inner, out fwd, out up, out winDir);
            // the window-side hand, unless it leans on the door or cranks; then the other one (unless it is at the radio)
            Arm want = null;
            bool armsBusy = GestureWeight > 0.01f || act != Act.None || giggleW > 0.01f || patW > 0.3f || Doze.Gate;
            if (hatWant && !armsBusy)
            {
                bool outerBusy = leanW > 0.01f || (reachW > 0.01f && reachOuter);
                bool innerBusy = reachW > 0.01f && !reachOuter;
                want = !outerBusy ? outer : !innerBusy ? inner : null;
            }
            if (hatArm == null) hatArm = want;
            if (want != hatArm) hatW = Mathf.MoveTowards(hatW, 0f, dt / 0.3f);   // let go before switching hands (or for good)
            else hatW = Mathf.MoveTowards(hatW, want != null ? 1f : 0f, dt / 0.4f);
            if (hatW <= 0f) hatArm = want;
            if (hatArm == null || hatW <= 0.001f) return;

            Vector3 c;
            float r;
            if (!HeadSphere(out c, out r)) return;
            Quaternion H = head.Deform();
            Vector3 hf = H * Vector3.forward, hu = H * Vector3.up;
            Vector3 side = hatArm == outer ? winDir : -winDir;
            Vector3 dirW = (hf + side * 0.8f).normalized;
            Vector3 grip = c;
            float bestD = float.MinValue;
            foreach (var p in hatRing)
            {
                Vector3 wp = head.T.TransformPoint(p);
                float d = Vector3.Dot(wp - c, dirW);
                if (d > bestD) { bestD = d; grip = wp; }
            }
            // palm on the brim just inside its edge, fingers out over it
            Vector3 outward = Vector3.ProjectOnPlane(grip - c, hu);
            outward = outward.sqrMagnitude > 1e-8f ? outward.normalized : dirW;
            Vector3 knuck = Knuckles(hatArm);
            float k = (knuck - hatArm.Hand.position).magnitude;
            if (k < 1e-5f) k = ArmLen(hatArm) * 0.12f;
            Vector3 wrist = grip - outward * (k * 1.2f) + hu * (r * 0.12f);
            float sw = Mathf.SmoothStep(0f, 1f, hatW);
            ReachLog("hat brim", hatArm, wrist);
            SolveArm(hatArm, wrist, -up + side * 0.6f, sw);
            Vector3 cur = Knuckles(hatArm) - hatArm.Hand.position;
            Vector3 axis = (outward - hu * 0.3f).normalized;
            if (cur.sqrMagnitude > 1e-10f)
                hatArm.Hand.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(cur, axis), sw) * hatArm.Hand.rotation;
            PoseFingers(hatArm, false, sw * 0.6f);
            if (!hatLogged && hatW >= 0.99f)
            {
                hatLogged = true;
                Debug.Log("[Umamusume companion] hat: hold with " + hatArm.Hand.name + ", wrist off target by " + ((hatArm.Hand.position - wrist).magnitude / r).ToString("F2")
                    + " head radii, grip " + ((grip - c).magnitude / r).ToString("F2") + " head radii from the head centre");
            }
        }
    }
}
