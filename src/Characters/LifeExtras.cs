using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// v0.21: head pats (Fire1 while aiming at her head), flinching at thunder (RainLightningC.Strike),
    /// pointing at road signs ahead, waving to oncoming cars. One arm act at a time: point, wave or cower.
    /// </summary>
    public partial class Life
    {
        // ------------------------------------------------------------ fingers
        class Finger { public Transform T; public Quaternion RestLocal; public bool Index, Thumb; }
        readonly List<Finger> fingersL = new List<Finger>(), fingersR = new List<Finger>();

        void InitExtras(Dictionary<string, Transform> bones)
        {
            foreach (var kv in bones)
            {
                var m = Regex.Match(kv.Key, @"^(Index|Middle|Ring|Pinky|Little|Thumb)_0\d_([LR])$");
                if (!m.Success) continue;
                var f = new Finger { T = kv.Value, RestLocal = kv.Value.localRotation, Index = m.Groups[1].Value == "Index", Thumb = m.Groups[1].Value == "Thumb" };
                (m.Groups[2].Value == "L" ? fingersL : fingersR).Add(f);
            }
            nextSignScan = Time.time + 2f;
        }

        /// <summary>open: all fingers straight (rest). point: index straight, the others curled a bit more.</summary>
        void PoseFingers(Arm a, bool point, float w)
        {
            if (a == null || w <= 0.001f) return;
            var list = a == armL ? fingersL : fingersR;
            foreach (var f in list)
            {
                Quaternion cur = f.T.localRotation;
                Quaternion want;
                if (!point || f.Index) want = f.RestLocal;
                else if (f.Thumb) continue;
                else want = f.RestLocal * Quaternion.SlerpUnclamped(Quaternion.identity, Quaternion.Inverse(f.RestLocal) * cur, 1.7f);
                f.T.localRotation = Quaternion.Slerp(cur, want, w);
            }
        }

        // ------------------------------------------------------------ head size (for pats and hands on the head)
        /// <summary>Middle of her skull and its radius, from her arm length (Uma: head sphere ~0.3 x upper+fore arm).
        /// v0.21.0 used the neck length: far too small, no click ever got through.</summary>
        bool HeadSphere(out Vector3 c, out float r)
        {
            c = Vector3.zero; r = 0f;
            if (head == null || armL == null) return false;
            float arm = ArmLen(armL);
            if (arm < 1e-5f) return false;
            r = 0.3f * arm;
            c = head.T.position + (head.Deform() * Vector3.up) * (0.5f * r);
            return true;
        }

        // ------------------------------------------------------------ arm act: point / wave / cower
        enum Act { None, Point, Wave, Cower }
        Act act;
        float actT, actDur, actW;
        Arm actArm;
        Vector3 spotPos;
        Transform spotT;

        bool ArmFree { get { return act == Act.None && reachW <= 0.01f && leanW <= 0.01f && GestureWeight <= 0.01f; } }

        void StartAct(Act a, float dur, Arm arm)
        {
            act = a; actT = 0f; actDur = dur; actArm = arm;
        }

        float ActEnv()
        {
            float inT = act == Act.Cower ? 0.18f : 0.35f;
            return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(Mathf.Min(actT / inT, (actDur - actT) / 0.45f)));
        }

        void ArmSides(out Arm outer, out Arm inner, out Vector3 fwd, out Vector3 up, out Vector3 winDir)
        {
            Facing(out fwd, out up);
            Vector3 right = Vector3.Cross(up, fwd);
            winDir = right * WindowSide(head != null ? head.T.position : uncle.position, right);
            bool leftOuter = Vector3.Dot(armL.Upper.position - armR.Upper.position, winDir) > 0f;
            outer = leftOuter ? armL : armR;
            inner = leftOuter ? armR : armL;
        }

        static float ArmLen(Arm a)
        {
            return (a.Fore.position - a.Upper.position).magnitude + (a.Hand.position - a.Fore.position).magnitude;
        }

        // ------------------------------------------------------------ per frame (from Update)
        void Extras(float dt, float now, bool inCar, bool asleep, bool talking, bool reacting)
        {
            CheckPat(now, inCar, asleep, talking);
            PatFace(now, asleep);
            ThunderFace(now);

            if (act != Act.None)
            {
                actT += dt;
                bool cut = !inCar || asleep || (act == Act.Wave && (talking || reacting));
                if (act == Act.Point && spotT != null && actT > actDur - 0.6f && actT < 5f && SpotAhead(spotT) && !cut)
                    actDur = actT + 0.6f;   // keep pointing while the sign is still ahead (max ~5 s)
                if (cut && actT < actDur - 0.45f) actDur = actT + 0.45f;
                if (actT >= actDur) { act = Act.None; actW = 0f; spotT = null; }
            }
            actW = act == Act.None ? 0f : ActEnv();

            if (inCar && !asleep)
            {
                LookForSigns(now, talking, reacting);
                LookForWave(now, talking, reacting);
            }

            switch (act)
            {
                case Act.Point:
                    if (spotT != null) spotPos = SpotCentre(spotT);
                    if (!talking) { curLook = Look.Spot; curLookW = 0.9f; }
                    Face.Want(actT < 0.5f ? "surprise" : "smile", 0.7f * actW, 0.15f);
                    break;
                case Act.Wave:
                    Face.Want("happy", 0.85f * actW, 0.2f);
                    break;
            }
        }

        void PoseExtras(float dt)
        {
            PosePat(dt);
            PoseFlinch();
            if (act == Act.None || actW <= 0.001f || armL == null || armR == null) return;
            Vector3 fwd, up, winDir;
            Arm outer, inner;
            ArmSides(out outer, out inner, out fwd, out up, out winDir);
            switch (act)
            {
                case Act.Point:
                {
                    var a = actArm ?? outer;
                    Vector3 sh = a.Upper.position;
                    Vector3 d = (spotPos - sh).normalized;
                    // up a little (over the dashboard) and not further than 55 deg off her facing
                    Vector3 flat = Vector3.ProjectOnPlane(d, up).normalized;
                    if (flat.sqrMagnitude < 1e-6f) flat = fwd;
                    float yaw = Vector3.SignedAngle(fwd, flat, up);
                    flat = Quaternion.AngleAxis(Mathf.Clamp(yaw, -55f, 55f), up) * fwd;
                    float pitch = Mathf.Clamp(Vector3.Angle(flat, d) * Mathf.Sign(Vector3.Dot(d, up)), 10f, 35f);
                    // Unity: a positive turn about "right" tips forward down, so up is negative
                    Vector3 dir = Quaternion.AngleAxis(-pitch, Vector3.Cross(up, flat).normalized) * flat;
                    SolveArm(a, sh + dir * (ArmLen(a) * 0.85f), -up + winDir * (a == outer ? 0.3f : -0.3f), actW);
                    PoseFingers(a, true, actW);
                    break;
                }
                case Act.Wave:
                {
                    var a = actArm ?? inner;
                    float s = ArmLen(a) / 0.39f;
                    Vector3 inward = a == inner ? -winDir : winDir;
                    Vector3 sh = a.Upper.position;
                    Vector3 elbowT = sh + fwd * (0.10f * s) - up * (0.03f * s) + inward * (0.04f * s);
                    float osc = Mathf.Sin(actT * Mathf.PI * 2f * 2.4f) * 0.06f * s;
                    Vector3 handT = elbowT + up * (0.21f * s) + fwd * (0.05f * s) + inward * osc;
                    AimArm(a, elbowT, handT, actW);
                    PoseFingers(a, false, actW);
                    break;
                }
                case Act.Cower:
                {
                    Vector3 c;
                    float r;
                    if (!HeadSphere(out c, out r)) break;
                    Vector3 hr = head.Deform() * Vector3.right, hu = head.Deform() * Vector3.up;
                    foreach (var a in new[] { armL, armR })
                    {
                        float side = Mathf.Sign(Vector3.Dot(a.Upper.position - c, hr));
                        Vector3 t = c + hr * (side * r * 1.05f) + hu * (r * 0.35f) + fwd * (r * 0.1f);
                        SolveArm(a, t, -up + hr * side * 0.6f - fwd * 0.4f, actW);
                        PoseFingers(a, false, actW * 0.7f);
                    }
                    break;
                }
            }
        }

        // ------------------------------------------------------------ head pat
        float patT = -1f, patDur = 2.4f, patW;
        readonly List<float> patTimes = new List<float>();
        bool patAsleep;

        // The game's click is its own key binding (assignedInputStrings[16]/[17], mouse); "Fire1" is the gamepad only.
        // v0.21 listened to Fire1 alone, so a mouse click never reached the pat (no "pat:" line in the log).
        static bool GameClick()
        {
            if (CompanionMod.PanelIsOpen) return false;   // a click on the debug panel is not a pat
            try
            {
                var mm = MainMenuC.Global;
                if (mm != null && mm.assignedInputStrings != null && mm.assignedInputStrings.Length > 17
                    && (Input.GetKeyDown(mm.assignedInputStrings[16]) || Input.GetKeyDown(mm.assignedInputStrings[17])))
                    return true;
            }
            catch (System.Exception) { /* a key name Unity does not know */ }
            return Input.GetButtonDown("Fire1");
        }

        /// <summary>The crosshair is on her head and she is within the game's own reach (DragRigidbodyC.maxRayDistance,
        /// 4.25 units: the driver's seat reaches her), the hands are free and the game is not holding input.</summary>
        bool AimAtHead(out float miss, out float dist, out float r)
        {
            miss = dist = r = 0f;
            var cam = Camera.main;
            Vector3 c;
            if (cam == null || Time.timeScale <= 0f || !HeadSphere(out c, out r)) return false;
            var drag = cam.GetComponent<DragRigidbodyC>();
            if (drag != null && (drag.isHolding1 != null || (drag.inputRestrict && !drag.inputRestrictDropOnly))) return false;
            float reach = drag != null && drag.maxRayDistance > 0f ? drag.maxRayDistance : 4.25f;
            Vector3 v = c - cam.transform.position;
            float t = Vector3.Dot(v, cam.transform.forward);
            dist = v.magnitude;
            miss = t > 0f ? (v - cam.transform.forward * t).magnitude : dist;
            return t > 0f && dist < reach + r && miss < r * 1.6f;
        }

        bool cursorOn;

        /// <summary>LateUpdate (after the game set its crosshair): the "can click" hand while aiming at her head.</summary>
        public void PatCursor()
        {
            float miss, dist, r;
            bool aim = Enabled && (AimAtHead(out miss, out dist, out r) || FruitAimed() || SouvenirCursor() || (ExtraCursor != null && ExtraCursor()));   // task 87: the vial   // task 47: over a souvenir too
            var cam = Camera.main;
            var drag = cam != null ? cam.GetComponent<DragRigidbodyC>() : null;
            var cur = drag != null ? drag.cursors : null;
            if (cur == null || cur.Length < 6) return;
            // DragRigidbodyC: [0] = interact (over handles, pickups with empty hands), [2] = the plain dot,
            // [1] click animation, [4] use-with-held-item, [5] other. v0.22.1 turned on [2], so nothing changed (user).
            if (aim)
            {
                Show(cur, 0, true);
                Show(cur, 1, false);
                Show(cur, 2, false);
                Show(cur, 4, false);
                Show(cur, 5, false);
                cursorOn = true;
            }
            else if (cursorOn)
            {
                // back to the plain dot; the game sets its own cursor again next frame if it looks at something
                Show(cur, 0, false);
                Show(cur, 2, true);
                cursorOn = false;
            }
        }

        static void Show(GameObject[] cur, int i, bool on)
        {
            if (cur[i] != null && cur[i].activeSelf != on) cur[i].SetActive(on);
        }

        void CheckPat(float now, bool inCar, bool asleep, bool talking)
        {
            if (!GameClick()) return;
            if (OfferingFruit) { if (FruitAimed()) TakeFruit(now); return; }
            float miss, dist, r;
            bool hit = AimAtHead(out miss, out dist, out r);
            if (r > 0f && dist < 8f)
                Debug.Log("[Umamusume companion] pat: " + (hit ? "hit" : "miss") + ", off by " + (miss / r).ToString("F2") + " head radii, distance "
                    + dist.ToString("F2") + " units");
            if (!hit) return;
            // a click while she is still being patted extends it (full weight, no restart from zero):
            // restarting every click made the head snap back and tilt again (user: twitch on each click)
            if (patT >= 0f && !asleep) patT = Mathf.Min(patT, 0.2f);
            else patT = 0f;
            patAsleep = asleep;
            if (asleep) { DozePat(now); return; }
            if (PatBusy != null && PatBusy()) { Attend(Look.Player, 1.5f, 0.6f); SayMaybe("addon_busy", 1f, 15f); return; }   // task 87: pouring
            if (NaritaPat(now)) { patT = -1f; return; }
            // turn to the player like when he gets back in, and keep looking a while after the last click
            Attend(Look.Player, patDur + 1.5f, 1f);
            tailWagEnd = now + 2.2f;  // standing: a happy wag
            patTimes.Add(now);
            patTimes.RemoveAll(x => now - x > 15f);
            if (PrankCatch(now)) return;   // task 34: a pat right after a prank = she owns up
            if (talking) return;
            // words rarely (user: too often): the face and ears react every time, a line now and then,
            // and each line said makes the next one less likely (x0.6, down to 5 %)
            if (patTimes.Count >= 4 && NaritaHere) { patTimes.Clear(); SayMaybe("pat_many", 1f, 120f); }
            else if (patTimes.Count >= 4)
            {
                patTimes.Clear();
                Emote("smug", 2.5f, 0.9f);
                if (SayMaybe("pat_many", Mathf.Max(0.1f, 0.5f * Mathf.Pow(0.6f, patManySaid)), 300f)) patManySaid++;
            }
            else if (NaritaHere) NaritaPatLine(now);
            else if (SayMaybe("pat", Mathf.Max(0.05f, 0.2f * Mathf.Pow(0.6f, patSaid)), 120f)) patSaid++;
        }
        int patSaid, patManySaid;

        void PatFace(float now, bool asleep)
        {
            if (patT < 0f) { patW = 0f; return; }
            patT += Time.deltaTime;
            if (patT >= patDur) { patT = -1f; patW = 0f; return; }
            patW = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(Mathf.Min(patT / 0.2f, (patDur - patT) / 0.6f)));
            if (patAsleep) { Face.Want("smile", 0.6f * patW, 0.3f); return; }
            Face.Want(PatFaceName, patW, 0.12f);
            Face.Solo(PatFaceName, patW);            // nothing else on the face while patted (no mixed eye shapes)
            Face.Pulse("back_l", 2.2f * patW);   // ears flat under the hand
            Face.Pulse("back_r", 2.2f * patW);
        }

        void PosePat(float dt)
        {
            if (patW <= 0.001f || head == null) return;
            float k = patAsleep ? 0.3f : 1f;
            float pitch = 10f * patW * k;                                         // down into the hand
            float roll = Mathf.Sin(Time.time * Mathf.PI * 2f * 1.2f) * 5f * patW * k;  // and a little nuzzle (clock phase: no jump on a new click)
            Quaternion H = head.Deform();
            head.T.rotation = Quaternion.AngleAxis(roll, H * Vector3.forward) * Quaternion.AngleAxis(pitch, H * Vector3.right) * head.T.rotation;
            if (neck != null)
            {
                Quaternion N = neck.Deform();
                neck.T.rotation = Quaternion.AngleAxis(pitch * 0.4f, N * Vector3.right) * neck.T.rotation;
            }
        }

        // ------------------------------------------------------------ thunder
        float flinchT = -1f;

        /// <summary>A thunderclap (RainLightningC.Strike).</summary>
        public void OnThunder()
        {
            if (!Enabled) return;
            float now = Time.time;
            Debug.Log("[Umamusume companion] event: thunder");
            if (Doze.Gate) { DozeWake("thunder"); return; }   // wakes with her own line, no remark
            flinchT = 0f;
            if (asleepNow)
            {
                Flick("twitch_l", now, 0.8f);
                Flick("twitch_r", now + 0.03f, 0.8f);
                return;
            }
            React("shock", Luck.Bad ? 0.9f : 0.6f, "worried", Luck.Bad ? 4f : 2.2f);
            if (inCarNow && !talkingNow && armL != null && armR != null && act == Act.None && leanW <= 0.01f && Random.value < (Luck.Bad ? 0.9f : 0.55f))
                StartAct(Act.Cower, 1.6f, null);
            if (!talkingNow) SayMaybe("thunder", 0.4f, 120f);
        }

        void ThunderFace(float now)
        {
            if (flinchT < 0f) return;
            flinchT += Time.deltaTime;
            if (flinchT > 1.2f) { flinchT = -1f; return; }
            if (!asleepNow && Doze.Phase != DozePhase.Wake)   // waking from a doze: ears up (DozeWake), not flat
            {
                float e = flinchT < 0.08f ? flinchT / 0.08f : 1f - (flinchT - 0.08f) / 1.12f;
                Face.Pulse("back_l", 2.5f * e);
                Face.Pulse("back_r", 2.5f * e);
            }
        }

        void PoseFlinch()
        {
            if (flinchT < 0f) return;
            // fast in (0.08 s), slow out: head ducks, shoulders hunch
            float e = flinchT < 0.08f ? flinchT / 0.08f : Mathf.SmoothStep(1f, 0f, (flinchT - 0.08f) / 1.12f);
            float k = asleepNow ? 0.3f : Luck.Bad ? 1.6f : 1f;
            if (chest != null)
            {
                Quaternion C = chest.Deform();
                chest.T.rotation = Quaternion.AngleAxis(6f * e * k, C * Vector3.right) * chest.T.rotation;
            }
            if (head != null)
            {
                Quaternion H = head.Deform();
                head.T.rotation = Quaternion.AngleAxis(7f * e * k, H * Vector3.right) * head.T.rotation;
            }
        }

        // ------------------------------------------------------------ road signs
        class SignKind { public Regex Name; public string Key; public float Chance; public bool Remark; }
        static readonly SignKind[] SignKinds =
        {
            // the game has its own lines for the petrol station, scrapyard and motel: she only points there
            new SignKind { Name = new Regex(@"^PetrolStationSignage", RegexOptions.IgnoreCase), Key = "sign_petrol", Chance = 0.85f },
            new SignKind { Name = new Regex(@"^scrapSigns", RegexOptions.IgnoreCase), Key = "sign_scrap", Chance = 0.85f },
            new SignKind { Name = new Regex(@"^MotelSign", RegexOptions.IgnoreCase), Key = "sign_motel", Chance = 0.85f },
            new SignKind { Name = new Regex(@"^LaikaSign", RegexOptions.IgnoreCase), Key = "sign_shop", Chance = 0.8f, Remark = true },
            new SignKind { Name = new Regex(@"^(border_BannedSignage|Hungary_BorderSign|BannedSignLoc)", RegexOptions.IgnoreCase), Key = "sign_border", Chance = 0.8f, Remark = true },
            new SignKind { Name = new Regex(@"^(DresdenSign|SturovoSign|Turk_sign|signage_motorway_lettering|signage_motorway_Dresden)", RegexOptions.IgnoreCase), Key = "sign_town", Chance = 0.6f, Remark = true },
            new SignKind { Name = new Regex(@"^signage_motorway_0\d", RegexOptions.IgnoreCase), Key = "sign_road", Chance = 0.12f },
        };

        class Sign { public Transform T; public SignKind Kind; }
        readonly List<Sign> signs = new List<Sign>();
        readonly HashSet<int> signSeen = new HashSet<int>();
        float nextSignScan, nextSignCheck, nextPointOk;

        static SignKind KindOf(string name)
        {
            foreach (var k in SignKinds) if (k.Name.IsMatch(name)) return k;
            return null;
        }

        // The scene is walked a slice per frame (v0.21.0 did FindObjectsOfType at once: a 65-72 ms hitch every 6 s).
        readonly Stack<Transform> scanStack = new Stack<Transform>();
        readonly List<Sign> scanFound = new List<Sign>();
        bool scanning;
        int scanVisited, scanFrames, lastSignCount = -1;
        float scanMs;
        const int ScanPerFrame = 1200;

        void ScanSigns(float now)
        {
            if (!scanning)
            {
                scanning = true;
                scanStack.Clear();
                scanFound.Clear();
                scanVisited = scanFrames = 0;
                scanMs = 0f;
                foreach (var go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                    if (go != null && go.activeInHierarchy) scanStack.Push(go.transform);
            }
            float t0 = Time.realtimeSinceStartup;
            int n = 0;
            while (scanStack.Count > 0 && n < ScanPerFrame)
            {
                var t = scanStack.Pop();
                n++;
                if (t == null || !t.gameObject.activeSelf) continue;
                string name = t.name;
                if (name.IndexOf("sign", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var k = KindOf(name);
                    if (k != null && t.GetComponentInChildren<Renderer>() != null)
                    {
                        scanFound.Add(new Sign { T = t, Kind = k });
                        continue;   // its children are parts of the same sign
                    }
                }
                for (int i = 0; i < t.childCount; i++) scanStack.Push(t.GetChild(i));
            }
            scanVisited += n;
            scanFrames++;
            scanMs += (Time.realtimeSinceStartup - t0) * 1000f;
            if (scanStack.Count > 0) return;
            scanning = false;
            nextSignScan = now + 4f;
            signs.Clear();
            signs.AddRange(scanFound);
            if (signs.Count != lastSignCount)
            {
                lastSignCount = signs.Count;
                Debug.Log("[Umamusume companion] signs: " + signs.Count + " in the world (" + scanVisited + " objects in " + scanFrames
                    + " frames, " + (scanMs / Mathf.Max(1, scanFrames)).ToString("F2") + " ms per frame)");
            }
        }

        Vector3 SpotCentre(Transform t)
        {
            var rs = t.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return t.position;
            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return b.center;
        }

        /// <summary>The sign is in front of her: 10-70 units away, within 40 deg of where she faces.</summary>
        bool SpotAhead(Transform t)
        {
            if (head == null || t == null || !t.gameObject.activeInHierarchy) return false;
            if ((t.position - head.T.position).sqrMagnitude > 100f * 100f) return false; // cheap cut before the bounds
            Vector3 fwd, up;
            Facing(out fwd, out up);
            Vector3 d = SpotCentre(t) - head.T.position;
            float dist = d.magnitude;
            return dist > 10f && dist < 70f && Vector3.Angle(fwd, Vector3.ProjectOnPlane(d, up)) < 40f;
        }

        void LookForSigns(float now, bool talking, bool reacting)
        {
            if (scanning || now >= nextSignScan) ScanSigns(now);
            if (now < nextSignCheck) return;
            nextSignCheck = now + 0.3f;
            if (now < nextPointOk || reacting || !ArmFree || armL == null || armR == null) return;
            Sign best = null;
            float bestD = float.MaxValue;
            foreach (var s in signs)
            {
                if (s.T == null || signSeen.Contains(s.T.GetInstanceID()) || !SpotAhead(s.T)) continue;
                float d = (s.T.position - head.T.position).sqrMagnitude;
                if (d < bestD) { bestD = d; best = s; }
            }
            if (best == null) return;
            signSeen.Add(best.T.GetInstanceID());   // each sign gets one roll
            {
                Vector3 f0, u0, w0;
                Arm o0, i0;
                ArmSides(out o0, out i0, out f0, out u0, out w0);
                // her window side = our side of the road; signs across the road are for the oncoming lane (user)
                if (Vector3.Dot(SpotCentre(best.T) - head.T.position, w0) <= 0f)
                {
                    Debug.Log("[Umamusume companion] sign skipped (oncoming side): " + best.T.name);
                    return;
                }
            }
            if (Random.value > best.Kind.Chance) return;

            Vector3 fwd, up, winDir;
            Arm outer, inner;
            ArmSides(out outer, out inner, out fwd, out up, out winDir);
            spotT = best.T;
            spotPos = SpotCentre(best.T);
            bool windowSide = Vector3.Dot(spotPos - head.T.position, winDir) > 0f;
            StartAct(Act.Point, 2.6f, windowSide ? outer : inner);
            beat = null;
            nextBeat = now + Random.Range(4f, 8f);
            nextPointOk = now + 12f;
            Debug.Log("[Umamusume companion] event: points at " + best.T.name + " (" + best.Kind.Key + ")");
            if (best.Kind.Remark && !talking) SayMaybe(best.Kind.Key, 0.6f, 150f);
        }

        // ------------------------------------------------------------ waving to oncoming cars
        Transform waveSeen;
        Vector3 watchPrev;
        Transform watchPrevT;
        float nextWaveOk;

        void LookForWave(float now, bool talking, bool reacting)
        {
            if (watch == null) { watchPrevT = null; return; }
            Vector3 pos = watch.position;
            if (watchPrevT != watch) { watchPrevT = watch; watchPrev = pos; return; }
            Vector3 v = (pos - watchPrev) / Mathf.Max(Time.deltaTime, 1e-4f);
            watchPrev = pos;
            if (watch == waveSeen || now < nextWaveOk || talking || reacting || !ArmFree || head == null) return;
            Vector3 fwd, up;
            Facing(out fwd, out up);
            Vector3 d = pos - head.T.position;
            float dist = d.magnitude;
            if (dist < 6f || dist > 28f) return;
            Vector3 vf = Vector3.ProjectOnPlane(v, up);
            if (vf.magnitude < 1f || Vector3.Dot(vf.normalized, fwd) > -0.6f) return; // it comes towards us
            waveSeen = watch;    // one roll per car
            if (Random.value > 0.35f) return;
            Vector3 winDir;
            Arm outer, inner;
            ArmSides(out outer, out inner, out fwd, out up, out winDir);
            bool windowSide = Vector3.Dot(d, winDir) > 0f;
            StartAct(Act.Wave, 2.0f, windowSide ? outer : inner);
            nextWaveOk = now + 40f;
            Debug.Log("[Umamusume companion] event: waves at " + watch.name);
        }
    }
}
