using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Lives on the uncle. Hides his meshes, builds the character from a char.bin pack and every frame
    /// copies the uncle's bone deformation onto her skeleton (after all LateUpdates, before rendering),
    /// then runs helper bones, spring physics and the face. Nothing here knows a specific rig:
    /// bone names come from the pack (see blender/profiles/*.json).
    /// </summary>
    public partial class CompanionDriver : MonoBehaviour
    {
        class Link
        {
            public Transform Girl, Uncle;
            public Matrix4x4 UncleBind;    // uncle bindpose (bind space -> uncle bone)
            public Quaternion GirlRestRot; // girl bone rest rotation in bind space
            public Vector3 GirlRestPos;
            public int Depth;
            public int ArmSide;            // -1 left arm chain, +1 right arm chain, 0 other
        }

        class Partial
        {
            public Transform Bone, Base, Full;
            public Quaternion RelBase, RelFull;
            public float Weight;
        }

        class HandPull
        {
            public Transform UncleHand, UncleKnuckle, Wrist, Knuckle;
            public Vector3 SavedLocal;
            public bool Pulled;
        }

        readonly List<Link> links = new List<Link>();
        readonly List<Partial> partials = new List<Partial>();
        readonly List<HandPull> hands = new List<HandPull>();

        /// <summary>Spine/neck bone that gets part of the uncle's forward hunch removed (relative to its parent).</summary>
        class Posture
        {
            public Transform Bone, Parent;
            public Quaternion RestRel;      // bone rest rotation relative to its parent's rest
            public Vector3 Up, Axis;        // character up / bend axis in the parent's local frame
            public float Weight;
        }

        readonly List<Posture> posture = new List<Posture>();
        public float PostureStrength = 0.6f; // share of the forward hunch removed (0..1)

        // uncle bones whose forward bend is the hunch, with how much of the correction each one gets
        static readonly Dictionary<string, float> PostureBones = new Dictionary<string, float>
        {
            { "UpperBack", 1f }, { "Neck", 1f }, { "Head", 0.4f },
        };
        readonly List<SkinnedMeshRenderer> hidden = new List<SkinnedMeshRenderer>();
        Link hipLink;
        Transform girlRoot;
        SkinnedMeshRenderer girlSmr;
        float bodyHeight;   // y range of her body vertices in pack units (props left out), task 26 scale check

        /// <summary>Task 31: info.txt body_scale. Her whole rig is this much smaller (or larger) than the export,
        /// which puts every Uma's hips at the uncle's height. Set before Build. 1 = nothing changes.</summary>
        public float BodyScale = 1f;
        float restHipBind;  // her rest hip above her soles, pack units (unscaled)
        float bindWorld;    // game units per pack unit before BodyScale
        float standW;       // 0 seated/lying .. 1 the uncle stands: her hips go down by what her shorter legs lack

        /// <summary>Her rest height without props in pack units, after BodyScale.</summary>
        public float HeightBind { get { return bodyHeight * BodyScale; } }

        /// <summary>Her rest-pose height (soles to the top of the ears/hair) in game units; 0 = not built.</summary>
        public float HeightUnits { get { return girlRoot != null ? bodyHeight * girlRoot.lossyScale.y : 0f; } }
        Animator animator;
        Springs springs;
        int lastFrame = -1;
        bool keepHidden = true;

        // tunables (set from mod settings)
        public float SeatLift = 0.18f;      // bind units, while sitting in the car
        public float ArmSpreadStand = 12f;  // degrees the arms are lifted away from the body outside the car
        public float ArmSpreadCar = 3f;
        public bool Physics { set { if (springs != null) springs.Enabled = value; } }
        public bool PhysicsDebug { set { if (springs != null) springs.DebugLog = value; } }

        /// <summary>Game materials are tinted ~0.5 (Trabbi 0.47, uncle 0.54) because the scene light is strong.</summary>
        public void SetBrightness(float b)
        {
            if (girlSmr == null) return;
            foreach (var m in faceSwap != null ? faceSwap.Originals : girlSmr.sharedMaterials)   // hidden face variants too
                if (m != null && m.HasProperty("_Color")) m.SetColor("_Color", new Color(b, b, b, 1f));
        }
        float seatBlend;

        // face
        int[] blink = new int[0], talk = new int[0];
        float nextBlink, blinkT = -1f, talkTimer;
        int talkShape = -1;
        // concept 14: mesh-swap face (Konata's GMod bodygroups), null for shape-key packs
        FaceSwap faceSwap;
        float swapTalkTimer;
        int swapTalkIdx = -1;
        readonly Dictionary<string, string> swapWant = new Dictionary<string, string>();
        float[] talkWeights = new float[0];
        int[] driven = new int[0];   // every shape the face writes
        float talkBlend;
        Life life;
        public bool Lively { set { if (life != null) { life.Enabled = value; if (!value) life.Face.Clear(); } } }
        public float Activity { set { if (life != null) life.Activity = value; } }
        public float WindScale { set { if (life != null) life.WindScale = value; } }
        Backpack backpack;
        Gestures gestures;

        // ------------------------------------------------------------ her own walk, phase-locked to the uncle's steps
        Transform uAnkleL, uAnkleR, uHip;
        float walkPhase, walkFreq = 1f, walkW, lastUpAt = -1f, lastDownAt = -1f, walkCorr;
        int stepSide;   // +1 left ankle clearly ahead, -1 right ankle clearly ahead (hysteresis)
        int rightHandBaseline;
        public bool OwnWalk = true;
        public bool HideSuitcase = true;

        // ------------------------------------------------------------ her own standing loops (uncle in Uncle_Standing_Idle)
        float idleW, idleT, idleCheckAt, prevIdleW;
        int idleCur = -1, idlePrev = -1;
        float prevIdleT;
        bool uncleStanding;

        void UpdateIdle(float dt, bool inCar)
        {
            if (gestures == null || gestures.IdleCount == 0) { idleW = 0f; return; }
            if (Time.time >= idleCheckAt && animator != null)
            {
                idleCheckAt = Time.time + 0.2f;
                uncleStanding = false;
                var ci = animator.GetCurrentAnimatorClipInfo(0);
                if (ci != null && ci.Length > 0 && ci[0].clip != null) uncleStanding = ci[0].clip.name.Contains("Standing_Idle");
            }
            // v0.26: her own Uma stance whenever she stands outside, talking included (the game still turns the head).
            // v0.19 waited for the uncle's Standing_Idle clip, so every line of dialogue switched her to his stooped
            // talking pose (user: "stands like a hunched old man")
            bool scripted = animator != null && (animator.GetBool("enterCar") || animator.GetBool("exitCar")
                || animator.GetBool("giveItem") || animator.GetBool("bedTime"));
            bool still = !NewWalk || rootSpeed < 0.1f * walkLegLen;
            bool on = OwnWalk && !inCar && seatBlend <= 0f && walkW < 0.5f && !scripted && still && !InBed()
                && (life == null || life.Enabled);
            // task 14: in the motel morning the uncle sits in his car clip all along; her whole body is her own stance
            if (!on && walkW < 0.5f && MornOwnStance(inCar)) on = true;
            if (on && idleCur < 0) { idleCur = Random.Range(0, gestures.IdleCount); idleT = 0f; }
            idleW = Mathf.MoveTowards(idleW, on ? 1f : 0f, dt / 0.4f);
            if (idleCur < 0) return;
            idleT += dt;
            prevIdleT += dt;
            prevIdleW = Mathf.MoveTowards(prevIdleW, 0f, dt / 0.6f);
            float len = gestures.IdleLength(idleCur);
            if (idleT >= len)
            {
                // next loop: another standing clip now and then, cross-faded
                int next = gestures.IdleCount > 1 && Random.value < 0.5f ? (idleCur + Random.Range(1, gestures.IdleCount)) % gestures.IdleCount : idleCur;
                if (next != idleCur) { idlePrev = idleCur; prevIdleT = idleT; prevIdleW = 1f; idleCur = next; idleT = 0f; }
                else idleT -= len;
            }
            if (idleW <= 0f && !on) idleCur = -1;
        }

        Quaternion GroundFrame()
        {
            Vector3 u = transform.up, f = Vector3.ProjectOnPlane(transform.forward, u);
            return f.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(f.normalized, u) : transform.rotation;
        }

        // ------------------------------------------------------------ asleep in the motel bed: a cute lying pose
        public float BedLift = 0.08f;   // bind units: hips higher than the uncle's (lying on her side)
        float bedW, bedT;
        Quaternion bedFrame;
        bool bedFrameSet;
        Vector3 bedShift;
        Dictionary<string, Quaternion> restRot;
        Dictionary<string, Transform> boneByName;
        float bedFloorY;
        bool bedSurveyed;
        float mirrorW;

        /// <summary>
        /// When she sits down on the motel bed (the game moves the uncle from the street into the room): find the
        /// room's floor with a ray in front of her feet; the suitcase goes there. Her leg length sets the distances.
        /// </summary>
        bool SurveyBed(out Vector3 spot)
        {
            spot = transform.position;
            if (uHip == null || uAnkleL == null) return false;
            float leg = Mathf.Max(0.05f, (uHip.position - uAnkleL.position).magnitude);
            Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            // in front of her feet first, then the other sides; the floor is well below her hips (the mattress is not)
            bool found = false;
            foreach (var dir in new[] { fwd, right, -right, -fwd })
            {
                // just past the bed's edge: the suitcase goes right by the bed
                Vector3 from = uHip.position + dir * (0.35f * leg) + Vector3.up * (0.3f * leg);
                RaycastHit hit;
                if (!UnityEngine.Physics.Raycast(from, Vector3.down, out hit, 4f * leg, ~0, QueryTriggerInteraction.Ignore)) continue;
                if (uHip.position.y - hit.point.y < 0.3f * leg) continue;
                bedFloorY = hit.point.y;
                spot = hit.point;
                found = true;
                break;
            }
            Debug.Log("[Umamusume companion] bed: floor " + (found ? "found " + (uHip.position.y - bedFloorY).ToString("F2") + " below her hips" : "not found"));
            return found;
        }

        /// <summary>Sitting on the bed with the suitcase on the floor: her right arm mirrors the left one.</summary>
        void MirrorRightArm(float w)
        {
            if (w <= 0.001f || restRot == null || hipLink == null) return;
            Quaternion hd = hipLink.Girl.rotation * Quaternion.Inverse(restRot[hipLink.Girl.name]);
            Quaternion hdi = Quaternion.Inverse(hd);
            foreach (var n in new[] { "Shoulder", "Arm", "Elbow", "Wrist" })
            {
                Transform l, r;
                Quaternion rl, rr;
                if (!boneByName.TryGetValue(n + "_L", out l) || !boneByName.TryGetValue(n + "_R", out r)) continue;
                if (!restRot.TryGetValue(n + "_L", out rl) || !restRot.TryGetValue(n + "_R", out rr)) continue;
                Quaternion q = hdi * (l.rotation * Quaternion.Inverse(rl));   // left bone's change, in the body frame
                q = new Quaternion(q.x, -q.y, -q.z, q.w);                    // mirrored across the body (bind X = right)
                r.rotation = Quaternion.Slerp(r.rotation, hd * q * rr, w);
            }
        }

        /// <summary>
        /// The uncle lies near the edge. Probe across her body with downward rays: where the ray still lands on the
        /// mattress (well above the floor) is bed, where it falls to the floor is not. Move her hips to the middle.
        /// Distances in her hip height above the floor (the game's units are not metres).
        /// </summary>
        Vector3 BedCentre(Vector3 hip, Vector3 axis, float floorY)
        {
            float h = hip.y - floorY;
            if (h < 1e-3f || axis.sqrMagnitude < 1e-6f) return Vector3.zero;
            Vector3 perp = Vector3.Cross(Vector3.up, axis.normalized).normalized;
            float[] ext = new float[2];
            for (int side = 0; side < 2; side++)
            {
                Vector3 dir = side == 0 ? perp : -perp;
                float reach = 0f;
                for (float d = 0.05f; d <= 1.6f; d += 0.05f)
                {
                    Vector3 from = hip + dir * (d * h) + Vector3.up * (0.6f * h);
                    RaycastHit hit;
                    bool mattress = UnityEngine.Physics.Raycast(from, Vector3.down, out hit, 2.5f * h, ~0, QueryTriggerInteraction.Ignore)
                        && hit.point.y - floorY > 0.45f * h;
                    if (!mattress) break;
                    reach = d;
                }
                ext[side] = reach;
            }
            float shift = (ext[0] - ext[1]) * 0.5f * h;   // towards the wider side, up to the middle
            Debug.Log("[Umamusume companion] bed: mattress across her " + ext[0].ToString("F2") + " / " + ext[1].ToString("F2")
                + " hip heights, moving her " + (shift / h).ToString("F2") + " to the middle");
            return perp * shift;
        }
        int bedClip = -1;
        Transform uHead;
        Vector3 lastFloorPos;
        bool haveFloorPos;

        bool InBed()
        {
            return animator != null && animator.GetBool("bedTime") && !InCar() && sleepZ != null && sleepZ.isPlaying;
        }

        void UpdateBed(float dt)
        {
            if (gestures == null || gestures.BedCount == 0 || uHead == null || uHip == null) { bedW = 0f; return; }
            bool on = InBed() && (life == null || life.Enabled);
            if (on && bedClip < 0)
            {
                bedClip = Random.Range(0, gestures.BedCount);
                bedT = 0f;
                Debug.Log("[Umamusume companion] bed: pose " + bedClip + ", uncle hips->head " + (uHead.position - uHip.position).ToString("F2"));
            }
            bedW = Mathf.MoveTowards(bedW, on ? 1f : 0f, dt / 1.5f);
            bedT += dt;
            if (!on && bedW <= 0f) { bedClip = -1; bedFrameSet = false; BedClampReset(); }
        }

        void PoseBed()
        {
            if (bedClip < 0 || bedW <= 0f || hipLink == null) return;
            float w = Mathf.SmoothStep(0f, 1f, bedW);
            Vector3 up = Vector3.up;
            // align her lying axis with the uncle's (hips -> head), keep the clip's own roll (on her side)
            // the frame is fixed once she lies down: following the uncle's moving head every frame turned her whole
            // body by small amounts and the tail twitched
            if (!bedFrameSet)
            {
                // wait until the uncle really lies down: v0.21 log had hips->head (-0.07, 1.59, 0.22), still sitting,
                // and the yaw from that tiny horizontal part was random (user: odd half-sitting pose).
                // Task 12: v0.33.1 took it at 0.0 s with (-1.20, 1.07, 0.01), half reclined. Now: nearly flat
                // (head less than 0.35 of hips->head above the hips, ~20 deg) for 0.5 s in a row, 6 s at most
                Vector3 full = uHead.position - uHip.position;
                bool flat = full.y < 0.35f * full.magnitude;
                bedSettleT = flat ? bedSettleT + Time.deltaTime : 0f;
                if (bedSettleT < 0.5f && bedT < 6f) return;
                Debug.Log("[Umamusume companion] bed: lying axis set at " + bedT.ToString("F1") + " s" + (bedSettleT < 0.5f ? " (timeout, not flat)" : "")
                    + ", uncle hips->head " + full.ToString("F2"));
                Vector3 ua = Vector3.ProjectOnPlane(uHead.position - uHip.position, up);
                bool bedFound = BedFind(uHip.position);
                if (bedFound) ua = BedSnapAxis(ua);
                Quaternion baseF = Quaternion.LookRotation(Vector3.ProjectOnPlane(transform.forward, up).sqrMagnitude > 1e-6f
                    ? Vector3.ProjectOnPlane(transform.forward, up).normalized : Vector3.forward, up);
                Vector3 ca = Vector3.ProjectOnPlane(baseF * gestures.BedAxis(bedClip), up);
                Quaternion yaw = ua.sqrMagnitude > 1e-6f && ca.sqrMagnitude > 1e-6f
                    ? Quaternion.AngleAxis(Vector3.SignedAngle(ca, ua, up), up) : Quaternion.identity;
                bedFrame = yaw * baseF;
                bedFrameSet = true;
                // the clamp below replaces the ray survey (it found 0.00 / 0.00: no solid mattress collider)
                bedShift = !bedFound && bedSurveyed ? BedCentre(hipLink.Girl.position, ua, bedFloorY) : Vector3.zero;
                Debug.Log("[Umamusume companion] bed: pose " + gestures.BedName(bedClip) + " root " + hipLink.Girl.position.ToString("F2")
                    + " bed " + (bedHave ? bedSrc + " at " + bedO.ToString("F2") : "none"));
            }
            hipLink.Girl.position += up * (BedLift * girlRoot.lossyScale.x * w) + bedShift * w;
            gestures.ApplyBed(bedClip, bedT, w, bedFrame);
            // measured after the pose, applied as one shift of her root (the bones follow their parent)
            hipLink.Girl.position += BedClampShift(w);
            BedClampLog();
        }

        // the uncle's suitcase: hidden while she carries it (it is his, not hers; with or without her backpack);
        // shown again where he puts it.
        // In the motel bed it stands on the floor where he stood before lying down, and goes back to his hand after.
        Renderer[] suitcaseRenderers;
        bool suitcaseHidden, suitcaseOnFloor;
        Vector3 suitcaseLocalPos, floorPos;
        Quaternion floorRot;
        Transform suitcaseAnchor;
        bool bedSurveyTried;
        Quaternion suitcaseLocalRot;

        static Vector3 Abs(Vector3 v) { return new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z)); }

        void SuitcaseToFloor(UncleLogicC u)
        {
            var s = u.suitcase.transform;
            suitcaseLocalPos = s.localPosition;
            suitcaseLocalRot = s.localRotation;
            Vector3 spot;
            bedSurveyed = SurveyBed(out spot);
            if (!bedSurveyed) return;
            // lying flat like in the car (its local +Y up there), the long side along the bed's edge
            Vector3 outDir = Vector3.ProjectOnPlane(spot - uHip.position, Vector3.up);
            if (outDir.sqrMagnitude < 1e-6f) outDir = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            outDir.Normalize();
            var mf = u.suitcase.GetComponentInChildren<MeshFilter>();
            Vector3 size = mf != null && mf.sharedMesh != null ? mf.sharedMesh.bounds.size : Vector3.one;
            if (mf != null && mf.transform != s)
                size = Abs((Quaternion.Inverse(s.rotation) * mf.transform.rotation) * size);
            // which of its local axes is "up": the one pointing up right now, while it hangs from his hand by the
            // handle. It stands on its bottom, handle up (v0.20.7 took the car slot's up: it stood on an edge, user)
            Vector3 upL;
            {
                Vector3 v = Quaternion.Inverse(s.rotation) * Vector3.up;
                int k = Mathf.Abs(v.x) > Mathf.Abs(v.y) ? (Mathf.Abs(v.x) > Mathf.Abs(v.z) ? 0 : 2) : (Mathf.Abs(v.y) > Mathf.Abs(v.z) ? 1 : 2);
                upL = k == 0 ? new Vector3(Mathf.Sign(v.x), 0, 0) : k == 1 ? new Vector3(0, Mathf.Sign(v.y), 0) : new Vector3(0, 0, Mathf.Sign(v.z));
                Debug.Log("[Umamusume companion] bed: suitcase up axis (local, from the carry) " + upL);
            }
            // lying flat on its broad side, the handle towards her bed (user). upL is the handle side (up while carried);
            // of the two other local axes the thinner one goes up
            Vector3 h1 = upL.x != 0 ? Vector3.up : Vector3.right, h2 = Vector3.Cross(upL, h1);
            float s1 = Mathf.Abs(Vector3.Dot(size, h1)), s2 = Mathf.Abs(Vector3.Dot(size, h2));
            Vector3 thinL = s1 <= s2 ? h1 : h2;
            s.rotation = Quaternion.LookRotation(-outDir, Vector3.up) * Quaternion.Inverse(Quaternion.LookRotation(upL, thinL));
            s.position = spot;
            // inner edge right at the spot (by the bed), bottom on the floor.
            // From the meshes, not Renderer.bounds: the renderers are off while she carries it (HideSuitcase) and a
            // disabled renderer's bounds are junk - v0.20.7 put the suitcase ~10 units above the room (user: no suitcase)
            float minY = float.MaxValue, minOut = float.MaxValue;
            foreach (var f in u.suitcase.GetComponentsInChildren<MeshFilter>(true))
            {
                if (f == null || f.sharedMesh == null) continue;
                Bounds b = f.sharedMesh.bounds;
                Matrix4x4 m = f.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = m.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z));
                    minY = Mathf.Min(minY, c.y);
                    minOut = Mathf.Min(minOut, Vector3.Dot(c - spot, outDir));
                }
            }
            if (minOut < float.MaxValue) s.position -= outDir * minOut;
            if (minY < float.MaxValue) s.position += Vector3.up * (spot.y - minY);
            floorPos = s.position;
            floorRot = s.rotation;
            // really take it out of his hand: held there per frame it followed the hand between frames ("flying")
            if (suitcaseAnchor == null) suitcaseAnchor = new GameObject("FukuSuitcaseOnFloor").transform;
            suitcaseAnchor.position = floorPos;
            suitcaseAnchor.rotation = floorRot;
            s.SetParent(suitcaseAnchor, true);
            suitcaseOnFloor = true;
            Debug.Log("[Umamusume companion] bed: suitcase on the floor at " + floorPos.ToString("F2") + ", uncle at " + transform.position.ToString("F2"));
        }

        void UpdateSuitcase()
        {
            var u = GetComponent<UncleLogicC>();
            if (u == null || u.suitcase == null) return;
            bool carried = u.uncleRightHand != null && u.suitcase.transform.parent == u.uncleRightHand;
            // where the uncle last walked on the floor (next to the bed when he gets into it)
            if (animator != null && !animator.GetBool("bedTime") && !InCar()) { lastFloorPos = transform.position; haveFloorPos = true; }
            bool inBed = animator != null && animator.GetBool("bedTime") && !InCar();
            if (HideSuitcase && carried && inBed && !suitcaseOnFloor && !bedSurveyTried) { bedSurveyTried = true; SuitcaseToFloor(u); }
            if (!inBed) { bedSurveyTried = false; bedSurveyed = false; }
            if (suitcaseOnFloor && !inBed)
            {
                // awake: back into his hand exactly as it was (unless the game moved it somewhere itself)
                if (suitcaseAnchor != null && u.suitcase.transform.parent == suitcaseAnchor && u.uncleRightHand != null)
                {
                    u.suitcase.transform.SetParent(u.uncleRightHand, false);
                    u.suitcase.transform.localPosition = suitcaseLocalPos;
                    u.suitcase.transform.localRotation = suitcaseLocalRot;
                }
                suitcaseOnFloor = false;
                carried = u.uncleRightHand != null && u.suitcase.transform.parent == u.uncleRightHand;
            }
            bool hide = HideSuitcase && carried && !suitcaseOnFloor;
            if (hide == suitcaseHidden) return;
            if (suitcaseRenderers == null) suitcaseRenderers = u.suitcase.GetComponentsInChildren<Renderer>(true);
            foreach (var r in suitcaseRenderers) if (r != null) r.enabled = !hide;
            suitcaseHidden = hide;
        }

        /// <summary>Something visible in the right hand (not the hidden suitcase): keep that arm still.</summary>
        bool HoldingRightVisible()
        {
            if (!HoldingRight()) return false;
            var u = GetComponent<UncleLogicC>();
            if (suitcaseHidden && u != null && u.uncleRightHand.childCount - rightHandBaseline <= 1) return false;
            return true;
        }

        /// <summary>
        /// The uncle's legs tell where he is in his stride: x = how far his left ankle is ahead of the right one.
        /// Each time it crosses zero, her walk cycle is pulled towards the matching phase and the step rate is
        /// re-measured, so her feet land with his (no sliding) at any speed.
        /// </summary>
        void UpdateWalk(float dt, bool inCar)
        {
            if (gestures == null || !gestures.HasWalk || uAnkleL == null || uAnkleR == null || uHip == null) { walkW = 0f; return; }
            if (NewWalk) { UpdateWalk2(dt, inCar); return; }   // v5 clip: distance-driven phase + foot IK (CompanionWalk.cs)
            float ws = animator != null ? animator.GetFloat("walkSpeed") : 0f;
            bool walking = OwnWalk && !inCar && seatBlend <= 0f && ws > 0.2f && (life == null || life.Enabled);
            Vector3 up = transform.up;
            Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, up).normalized;
            float leg = Mathf.Max(1e-4f, (uHip.position - uAnkleL.position).magnitude);
            float x = Vector3.Dot(uAnkleL.position - uAnkleR.position, fwd) / leg;
            float now = Time.time;
            if (walking)
            {
                if (walkW <= 0.01f) { walkFreq = 1f / gestures.WalkLength; lastUpAt = lastDownAt = -1f; walkCorr = 0f; }
                // phase errors are worked off over ~0.3 s instead of jumping (jumps made her arms twitch)
                float k = 1f - Mathf.Exp(-dt / 0.3f);
                float c = walkCorr * k;
                walkCorr -= c;
                walkPhase = Mathf.Repeat(walkPhase + walkFreq * dt + c, 1f);
                // a step counts only when the ankles are clearly apart (they jitter around zero when starting/stopping)
                const float H = 0.06f;
                if (x > H && stepSide != 1) { if (stepSide == -1) StepEvent(gestures.WalkUps, now, ref lastUpAt); stepSide = 1; }
                else if (x < -H && stepSide != -1) { if (stepSide == 1) StepEvent(gestures.WalkDowns, now, ref lastDownAt); stepSide = -1; }
            }
            else stepSide = 0;
            walkW = Mathf.MoveTowards(walkW, walking ? 1f : 0f, dt / 0.25f);
        }

        void StepEvent(float[] phases, float now, ref float lastAt)
        {
            if (lastAt > 0f)
            {
                float T = now - lastAt;   // one full stride (same foot to same foot)
                if (T > 0.3f && T < 3f) walkFreq = Mathf.Lerp(walkFreq, 1f / T, 0.3f);
            }
            lastAt = now;
            if (phases.Length == 0) return;
            float best = phases[0], bestErr = 9f;
            foreach (var p in phases)
            {
                float e = Mathf.Repeat(p - walkPhase + 0.5f, 1f) - 0.5f;
                if (Mathf.Abs(e) < Mathf.Abs(bestErr)) { bestErr = e; best = p; }
            }
            // snap while fading in, otherwise spread the correction over the next frames
            if (walkW < 0.5f) { walkPhase = Mathf.Repeat(walkPhase + bestErr, 1f); walkCorr = 0f; }
            else walkCorr = bestErr;
        }

        bool HoldingRight()
        {
            var u = GetComponent<UncleLogicC>();
            return u != null && u.uncleRightHand != null && u.uncleRightHand.childCount > rightHandBaseline;
        }
        Remarks remarks;

        public void ToggleLean() { if (life != null) life.ToggleLean(); }
        public string DozeKey() { return Doze.DebugToggle(); }

        /// <summary>Play a gesture clip by name (null = the first one). Returns the name played or null.</summary>
        public string PlayGesture(string name)
        {
            if (gestures == null || gestures.Count == 0) return null;
            if (name == null) name = gestures.Next();
            if (!gestures.Play(name)) return null;
            if (life != null) life.GestureForced = true;
            return name;
        }
        /// <summary>Backpack on the back seat in the car; offsets in metres from her hips (behind, up, to her right).</summary>
        public void SetBackpack(bool onSeat, float back, float height, float side)
        {
            if (backpack == null) return;
            backpack.Enabled = onSeat;
            backpack.Back = back;
            backpack.Height = height;
            backpack.Side = side;
            backpack.Replace();
        }

        public string Summary { get; private set; }

        public void Build(CharPack d, string assetsDir, bool hideUncle)
        {
            animator = GetComponent<Animator>();

            SkinnedMeshRenderer body = null;
            foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!smr.name.StartsWith("Dantes_")) continue;
                if (smr.name == "Dantes_Body_001") body = smr;
                hidden.Add(smr);
            }
            if (body == null) throw new System.Exception("Dantes_Body_001 not found under the uncle");
            var ubones = body.bones;
            var ubinds = body.sharedMesh.bindposes;
            var uncleBone = new Dictionary<string, int>();
            for (int i = 0; i < ubones.Length; i++)
                if (ubones[i] != null && !uncleBone.ContainsKey(ubones[i].name)) uncleBone[ubones[i].name] = i;

            int lb = uncleBone["LowerBack"];
            Matrix4x4 D0 = ubones[lb].localToWorldMatrix * ubinds[lb];
            float bindScale = D0.GetColumn(0).magnitude;
            girlRoot = new GameObject("UmamusumeCompanionCharacter").transform;
            girlRoot.SetParent(transform, false);
            if (BodyScale <= 0f) BodyScale = 1f;
            bindWorld = bindScale;
            girlRoot.localScale = Vector3.one * (bindScale * BodyScale / transform.lossyScale.x);

            // skeleton
            int nb = d.Bones.Count;
            var girlBones = new Transform[nb];
            var bindposes = new Matrix4x4[nb];
            var byName = new Dictionary<string, int>();
            var tByName = new Dictionary<string, Transform>();
            for (int i = 0; i < nb; i++)
            {
                var b = d.Bones[i];
                var t = new GameObject(b.Name).transform;
                t.SetParent(b.Parent >= 0 ? girlBones[b.Parent] : girlRoot, false);
                Matrix4x4 parentRest = b.Parent >= 0 ? d.Bones[b.Parent].Rest : Matrix4x4.identity;
                Matrix4x4 local = parentRest.inverse * b.Rest;
                t.localPosition = local.GetColumn(3);
                t.localRotation = Rot(local);
                girlBones[i] = t;
                bindposes[i] = b.Rest.inverse;
                byName[b.Name] = i;
                tByName[b.Name] = t;
            }

            // mesh
            var mesh = new Mesh { name = "UmamusumeCompanionCharacter" };
            mesh.vertices = d.Positions;
            mesh.normals = d.Normals;
            mesh.uv = d.UVs;
            mesh.boneWeights = d.Weights;
            mesh.bindposes = bindposes;
            mesh.subMeshCount = d.SubMeshes.Count;
            // props ("<prop>|<material>": backpack, hat...) get their own renderers, see Props
            for (int s = 0; s < d.SubMeshes.Count; s++)
                mesh.SetTriangles(Props.IsProp(d.SubMeshes[s]) ? new int[0] : d.SubMeshes[s].Indices, s);
            var shapeIdx = new Dictionary<string, List<int>>();
            foreach (var sh in d.Shapes)
            {
                mesh.AddBlendShapeFrame(sh.Name, 100f, sh.Delta, null, null);
                if (!shapeIdx.ContainsKey(sh.Role)) shapeIdx[sh.Role] = new List<int>();
                shapeIdx[sh.Role].Add(mesh.blendShapeCount - 1);
            }
            mesh.RecalculateBounds();
            float ylo = float.MaxValue, yhi = float.MinValue;
            foreach (var sm in d.SubMeshes)
                if (!Props.IsProp(sm))   // her body only: a hat or a backpack must not count
                    foreach (int vi in sm.Indices) { float y = d.Positions[vi].y; if (y < ylo) ylo = y; if (y > yhi) yhi = y; }
            bodyHeight = yhi > ylo ? yhi - ylo : 0f;
            restHipBind = 0f;
            foreach (var kv in d.Map)
                if (kv.Value == "LowerBack" && byName.ContainsKey(kv.Key) && yhi > ylo)
                    restHipBind = d.Bones[byName[kv.Key]].Rest.GetColumn(3).y - ylo;

            var mats = new Material[d.SubMeshes.Count];
            var texCache = new Dictionary<string, Texture2D>();
            for (int s = 0; s < d.SubMeshes.Count; s++)
            {
                var sm = d.SubMeshes[s];
                Shader sh = sm.Shader == "opaque" ? Shader.Find("Legacy Shaders/Diffuse") : Shader.Find("Legacy Shaders/Transparent/Cutout/Diffuse");
                if (sh == null) sh = Shader.Find("Legacy Shaders/Diffuse");
                var m = new Material(sh) { name = sm.Material };
                if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.5f);
                if (m.HasProperty("_Color")) m.SetColor("_Color", new Color(0.55f, 0.55f, 0.55f, 1f));
                if (!string.IsNullOrEmpty(sm.Texture))
                {
                    Texture2D tex;
                    if (!texCache.TryGetValue(sm.Texture, out tex))
                    {
                        tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = sm.Texture };
                        tex.LoadImage(File.ReadAllBytes(Path.Combine(assetsDir, sm.Texture)));
                        texCache[sm.Texture] = tex;
                    }
                    m.mainTexture = tex;
                }
                mats[s] = m;
            }

            irisMaterial = EyeSurface.Apply(d, mesh, ref mats);
            girlSmr = girlRoot.gameObject.AddComponent<SkinnedMeshRenderer>();
            if (CharFeatures.Has("boot_snack")) fullBellyShape = mesh.GetBlendShapeIndex("Oguri_Full");
            girlSmr.sharedMesh = mesh;
            girlSmr.bones = girlBones;
            girlSmr.rootBone = girlBones[0];
            girlSmr.sharedMaterials = mats;
            faceSwap = FaceSwap.Build(d, girlSmr, mats);
            girlSmr.updateWhenOffscreen = true;
            girlSmr.quality = SkinQuality.Bone4;
            Props.Build(d, mats, girlRoot, girlBones, bindposes);
            backpack = Backpack.Build(girlRoot);

            // links: character bone follows uncle bone
            foreach (var kv in d.Map)
            {
                int gi, ui;
                if (!byName.TryGetValue(kv.Key, out gi) || !uncleBone.TryGetValue(kv.Value, out ui)) continue;
                int side = 0;
                foreach (var p in d.ArmPrefixes)
                    if (kv.Key.StartsWith(p)) { side = IsLeft(kv.Key) ? -1 : 1; break; }
                var rest = d.Bones[gi].Rest;
                var link = new Link
                {
                    Girl = girlBones[gi], Uncle = ubones[ui], UncleBind = ubinds[ui],
                    GirlRestRot = Rot(rest), GirlRestPos = rest.GetColumn(3), Depth = Depth(d, gi), ArmSide = side,
                };
                links.Add(link);
                if (kv.Value == "LowerBack") hipLink = link;
            }
            links.Sort((a, b) => a.Depth.CompareTo(b.Depth));

            // posture: keyed by the uncle's bone names, so it works for any character
            var postureList = new List<KeyValuePair<int, Posture>>();
            foreach (var kv in d.Map)
            {
                float w;
                int gi;
                if (!PostureBones.TryGetValue(kv.Value, out w) || !byName.TryGetValue(kv.Key, out gi)) continue;
                int pi = d.Bones[gi].Parent;
                if (pi < 0) continue;
                Quaternion prest = Rot(d.Bones[pi].Rest), rest = Rot(d.Bones[gi].Rest);
                Quaternion inv = Quaternion.Inverse(prest);
                Vector3 up = inv * Vector3.up, fwd = inv * Vector3.forward; // bind space: Y up, +Z forward
                postureList.Add(new KeyValuePair<int, Posture>(Depth(d, gi), new Posture
                {
                    Bone = girlBones[gi], Parent = girlBones[pi], RestRel = inv * rest,
                    Up = up, Axis = Vector3.Cross(up, fwd).normalized, Weight = w,
                }));
            }
            postureList.Sort((a, b) => a.Key.CompareTo(b.Key));
            foreach (var kv in postureList) posture.Add(kv.Value);

            foreach (var p in d.Partials)
            {
                int b, bb, fb;
                if (!byName.TryGetValue(p.Bone, out b) || !byName.TryGetValue(p.Base, out bb) || !byName.TryGetValue(p.Full, out fb)) continue;
                Quaternion rb = Rot(d.Bones[b].Rest);
                partials.Add(new Partial
                {
                    Bone = girlBones[b], Base = girlBones[bb], Full = girlBones[fb], Weight = p.Weight,
                    RelBase = Quaternion.Inverse(Rot(d.Bones[bb].Rest)) * rb, RelFull = Quaternion.Inverse(Rot(d.Bones[fb].Rest)) * rb,
                });
            }

            foreach (var hp in d.Hands)
            {
                int u, uk;
                Transform w, k;
                if (!uncleBone.TryGetValue(hp.UncleHand, out u) || !tByName.TryGetValue(hp.Wrist, out w)) continue;
                tByName.TryGetValue(hp.Knuckle, out k);
                hands.Add(new HandPull
                {
                    UncleHand = ubones[u], UncleKnuckle = uncleBone.TryGetValue(hp.UncleKnuckle, out uk) ? ubones[uk] : null,
                    Wrist = w, Knuckle = k,
                });
            }

            springs = new Springs(d, girlRoot, tByName) { TuckSeatedTail = CharFeatures.Has("seated_tail") };

            var rests = new Dictionary<string, Quaternion>();
            for (int i = 0; i < nb; i++) rests[d.Bones[i].Name] = Rot(d.Bones[i].Rest);
            restRot = rests;
            boneByName = tByName;
            life = new Life(transform, mesh, Path.Combine(assetsDir, "face.txt"), tByName, rests, d.Shapes);
            if (hipLink != null)
                gestures = new Gestures(Path.Combine(assetsDir, "anims"), hipLink.Girl, rests[hipLink.Girl.name], tByName, rests) { Up = transform };
            remarks = new Remarks(assetsDir);
            if (remarks.Count > 0)
                life.Say = key =>
                {
                    string text = remarks.Pick(key);
                    if (text == null) Debug.Log("[Umamusume companion] remark skipped: no line for " + key + " in this language");
                    else StartCoroutine(remarks.Say(GetComponent<UncleLogicC>(), text));
                };
            life.CanSay = () => remarks.CanSay(GetComponent<UncleLogicC>());
            life.HasLine = k => remarks.All(k) != null;   // task 52: news_<id> lines
            life.RemarkBusy = () => remarks.Busy;
            {
                var trip = gameObject.GetComponent<ReturnTrip>() ?? gameObject.AddComponent<ReturnTrip>();
                trip.Story = new Remarks(assetsDir, "story_");
                trip.RemarkBusy = () => remarks.Busy;
                var story = trip.Story;
                life.StorySay = key =>
                {
                    string text = story.Pick(key);
                    if (text == null) return false;
                    StartCoroutine(remarks.Say(GetComponent<UncleLogicC>(), text));
                    return true;
                };
            }
            {
                Link la = null, lr = null;
                foreach (var lk in links)
                {
                    if (lk.Uncle.name == "L_shin_001") la = lk;
                    if (lk.Uncle.name == "R_Shin_001") lr = lk;
                }
                if (la != null) uAnkleL = la.Uncle;
                if (lr != null) uAnkleR = lr.Uncle;
                if (hipLink != null) uHip = hipLink.Uncle;
                foreach (var lk in links) if (lk.Uncle.name == "Head") uHead = lk.Uncle;
                var ul = GetComponent<UncleLogicC>();
                rightHandBaseline = ul != null && ul.uncleRightHand != null ? ul.uncleRightHand.childCount : 0;
            }
            if (gestures != null && gestures.Count > 0)
            {
                life.Gest = gestures;
                Transform wrist;
                if (tByName.TryGetValue("Wrist_R", out wrist)) life.SetupConsole(wrist, tByName, girlRoot, girlSmr, Path.Combine(assetsDir, "props"), d.Scale);
                life.GestureNames = new List<string>(gestures.Names).ToArray();
                life.PlayGesture = n => gestures.Play(n) ? gestures.LengthOf(n) : 0f;
                life.StopGesture = gestures.Stop;
            }

            List<int> l;
            if (shapeIdx.TryGetValue("blink", out l)) blink = l.ToArray();
            if (shapeIdx.TryGetValue("talk", out l)) talk = l.ToArray();
            talkWeights = new float[talk.Length];
            nextBlink = Time.time + Random.Range(1.5f, 4f);

            var faceShapes = new HashSet<int>(life.Face.Managed);
            foreach (int i in blink) faceShapes.Add(i);
            foreach (int i in talk) faceShapes.Add(i);
            driven = new List<int>(faceShapes).ToArray();
            Summary = $"{nb} bones, {d.Positions.Length} verts, {links.Count} links, {springs.ChainCount} spring chains, {life.Summary}" + (backpack != null ? ", backpack " + backpack.Vertices + " verts" : "")
                + (Props.All.Count > (backpack != null ? 1 : 0) ? ", props " + Props.Names : "")
                + (gestures != null && gestures.Count > 0 ? ", gestures: " + string.Join(" ", new List<string>(gestures.Names).ToArray()) : "")
                + (gestures != null && gestures.HasWalk ? ", walk: " + gestures.WalkName : "")
                + (gestures != null && gestures.IdleCount > 0 ? ", standing: " + gestures.IdleNames : "")
                + (gestures != null && gestures.BedCount > 0 ? ", " + gestures.BedCount + " bed poses" : "")
                + (remarks != null && remarks.Count > 0 ? ", " + remarks.Count + " remarks" : "")
                + (faceSwap != null ? ", face swaps " + faceSwap.Variants + " in " + faceSwap.PartCount + " parts"
                    + (life.Face.SwapBlink != null ? ", blink " + life.Face.SwapBlink : ", NO blink line") + ", talk " + life.Face.SwapTalk.Length : "");
            SetUncleVisible(!hideUncle);
            Camera.onPreCull += OnAnyPreCull;
            if (irisMaterial != null)
            {
                Camera.onPreRender += BeforeEyeRender;
                Camera.onPostRender += AfterEyeRender;
            }
            StartCoroutine(RestoreHandsLoop());
        }

        static bool IsLeft(string n) { return n.EndsWith("_L") || n.EndsWith(".L") || n.StartsWith("Left") || n.StartsWith("L_"); }

        static int Depth(CharPack d, int i)
        {
            int n = 0;
            while (d.Bones[i].Parent >= 0) { i = d.Bones[i].Parent; n++; }
            return n;
        }

        public static Quaternion Rot(Matrix4x4 m)
        {
            return Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
        }

        public void SetUncleVisible(bool on)
        {
            keepHidden = !on;
            foreach (var r in hidden) if (r != null) r.enabled = on;
        }

        void OnDestroy()
        {
            EndFetch();
            Camera.onPreCull -= OnAnyPreCull;
            Camera.onPreRender -= BeforeEyeRender;
            Camera.onPostRender -= AfterEyeRender;
            if (Life.Current == life) Life.Current = null;
            if (backpack != null) backpack.Destroy();
            Props.Clear();
            if (girlRoot != null) Destroy(girlRoot.gameObject);
            SetUncleVisible(true);
            if (uncleLogic != null && originalSpeech != null) uncleLogic.speech = originalSpeech;
            VoicePatch.SetClips(null);
        }

        // ---------------------------------------------------------------- voice
        UncleLogicC uncleLogic;
        AudioClip[] originalSpeech;
        public string VoiceSummary { get; private set; }

        /// <summary>
        /// The uncle "speaks" by playing a random clip from UncleLogicC.speech per line. Replace those clips with
        /// voice\*.wav|*.ogg from the character folder, or else pitch the uncle's own clips up.
        /// </summary>
        public void SetupVoice(string dir, float pitch)
        {
            uncleLogic = GetComponent<UncleLogicC>();
            if (uncleLogic == null) return;
            originalSpeech = uncleLogic.speech;
            var files = new List<string>();
            string vdir = Path.Combine(dir, "voice");
            if (Directory.Exists(vdir))
                foreach (var f in Directory.GetFiles(vdir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".wav" || ext == ".ogg") files.Add(f);
                }
            files.Sort(System.StringComparer.OrdinalIgnoreCase);
            if (files.Count > 0) StartCoroutine(LoadVoice(files));
            else if (Mathf.Abs(pitch - 1f) > 0.01f) PitchUncle(pitch);
            else VoiceSummary = "uncle's voice";
        }

        System.Collections.IEnumerator LoadVoice(List<string> files)
        {
            var clips = new List<AudioClip>();
            foreach (var f in files)
            {
                var www = new WWW(new System.Uri(f).AbsoluteUri);
                yield return www;
                if (!string.IsNullOrEmpty(www.error)) { Debug.LogWarning("[Umamusume companion] voice " + f + ": " + www.error); continue; }
                var type = Path.GetExtension(f).ToLowerInvariant() == ".ogg" ? AudioType.OGGVORBIS : AudioType.WAV;
                var clip = www.GetAudioClip(false, false, type);
                while (clip != null && clip.loadState == AudioDataLoadState.Loading) yield return null;
                if (clip != null && clip.length > 0.05f) { clip.name = Path.GetFileNameWithoutExtension(f); clips.Add(clip); }
            }
            if (clips.Count == 0) { VoiceSummary = "voice folder empty or unreadable"; yield break; }
            uncleLogic.speech = clips.ToArray();
            // played from a shuffle bag (no repeats in a row), see VoicePatch
            VoicePatch.SetClips(uncleLogic.speech);
            try { VoicePatch.Apply(); }
            catch (System.Exception e) { Debug.LogWarning("[Umamusume companion] voice patch failed, game picks clips itself: " + e.Message); }
            VoiceSummary = clips.Count + " voice clips, shuffled";
            JaLoader.Console.Log(CompanionStorage.ModId, "Voice: " + VoiceSummary);
        }

        void PitchUncle(float pitch)
        {
            var outClips = new List<AudioClip>();
            foreach (var c in originalSpeech)
            {
                if (c == null) continue;
                try
                {
                    var data = new float[c.samples * c.channels];
                    if (!c.GetData(data, 0)) continue;
                    int frames = c.samples, ch = c.channels;
                    int outFrames = Mathf.Max(1, (int)(frames / pitch));
                    var res = new float[outFrames * ch];
                    for (int i = 0; i < outFrames; i++)
                    {
                        float src = i * pitch;
                        int i0 = Mathf.Min((int)src, frames - 1), i1 = Mathf.Min(i0 + 1, frames - 1);
                        float t = src - i0;
                        for (int k = 0; k < ch; k++)
                            res[i * ch + k] = Mathf.Lerp(data[i0 * ch + k], data[i1 * ch + k], t);
                    }
                    var nc = AudioClip.Create(c.name + "_hi", outFrames, ch, c.frequency, false);
                    nc.SetData(res, 0);
                    outClips.Add(nc);
                }
                catch (System.Exception e) { Debug.LogWarning("[Umamusume companion] pitch " + c.name + ": " + e.Message); }
            }
            if (outClips.Count > 0)
            {
                uncleLogic.speech = outClips.ToArray();
                VoicePatch.SetClips(uncleLogic.speech);
                try { VoicePatch.Apply(); } catch (System.Exception e) { Debug.LogWarning("[Umamusume companion] voice patch: " + e.Message); }
                VoiceSummary = "uncle's voice x" + pitch;
            }
            else VoiceSummary = "uncle's voice (could not pitch)";
        }

        Material irisMaterial;
        readonly Stack<bool> eyeCameraStencil = new Stack<bool>();

        void BeforeEyeRender(Camera cam)
        {
            bool stencil = cam.targetTexture == null || cam.targetTexture.depth >= 24;
            if (stencil != (eyeCameraStencil.Count == 0 || eyeCameraStencil.Peek()))
                EyeSurface.SetStencil(irisMaterial, stencil);
            eyeCameraStencil.Push(stencil);
        }

        void AfterEyeRender(Camera cam)
        {
            if (eyeCameraStencil.Count == 0) return;
            bool before = eyeCameraStencil.Pop();
            bool after = eyeCameraStencil.Count == 0 || eyeCameraStencil.Peek();
            if (before != after) EyeSurface.SetStencil(irisMaterial, after);
        }

        void OnAnyPreCull(Camera cam)
        {
            if (Time.frameCount == lastFrame || girlRoot == null) return;
            lastFrame = Time.frameCount;
            Pose();
        }

        bool InCar()
        {
            return transform.parent != null && animator != null && animator.GetBool("enterCar");
        }

        /// <summary>Task 31: her hips sit where the uncle's are (seated, lying: scaled about the pelvis). When he stands,
        /// her shorter legs would leave her feet in the air: drop the hips by the difference of the rest hip heights.
        /// How much he stands = his hip height above the floor against his rest hip height.</summary>
        float StandDrop(Vector3 hip)
        {
            float restHip = restHipBind * bindWorld;
            if (restHip <= 1e-4f) return 0f;
            float target = 0f;
            if (seatBlend < 1f && bedW < 1f && !InCar())
            {
                float r = (Vector3.Dot(hip, transform.up) - RootGround()) / restHip;
                target = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.6f, 0.85f, r)) * (1f - seatBlend) * (1f - bedW);
            }
            standW = Mathf.MoveTowards(standW, target, Time.deltaTime / 0.25f);
            return (1f - BodyScale) * restHip * standW;
        }

        void Pose()
        {
            if (life != null) life.ResetBones();
            if (gestures != null) gestures.ResetBones();
            bool inCar = InCar();
            seatBlend = Mathf.MoveTowards(seatBlend, inCar ? 1f : 0f, Time.deltaTime * 2f);
            float spread = Mathf.Lerp(ArmSpreadStand, ArmSpreadCar, seatBlend);
            // bind space: arms point along -X (left) / +X (right), forward is +Z
            Quaternion liftL = Quaternion.AngleAxis(-spread, Vector3.forward);
            Quaternion liftR = Quaternion.AngleAxis(spread, Vector3.forward);

            if (hipLink != null)
            {
                Matrix4x4 D = hipLink.Uncle.localToWorldMatrix * hipLink.UncleBind;
                girlRoot.rotation = Rot(D);
                girlRoot.position = D.GetColumn(3);
            }
            foreach (var l in links)
            {
                Matrix4x4 D = l.Uncle.localToWorldMatrix * l.UncleBind;
                Quaternion extra = l.ArmSide < 0 ? liftL : l.ArmSide > 0 ? liftR : Quaternion.identity;
                l.Girl.rotation = Rot(D) * extra * l.GirlRestRot;
                if (l == hipLink)
                {
                    Vector3 p = D.MultiplyPoint3x4(l.GirlRestPos);
                    if (seatBlend > 0f && transform.parent != null)
                        p += transform.parent.up * (seatBlend * SeatLift * girlRoot.lossyScale.x);
                    if (BodyScale != 1f) p -= transform.up * StandDrop(p);
                    l.Girl.position = p;
                }
            }
            // straighten the uncle's hunch: remove part of the forward pitch of spine/neck/head relative to the parent
            if (PostureStrength > 0f)
                foreach (var pc in posture)
                {
                    Quaternion pr = pc.Parent.rotation;
                    Quaternion delta = Quaternion.Inverse(pr) * pc.Bone.rotation * Quaternion.Inverse(pc.RestRel);
                    Vector3 v = Vector3.ProjectOnPlane(delta * pc.Up, pc.Axis);
                    if (v.sqrMagnitude < 1e-8f) continue;
                    float pitch = Vector3.SignedAngle(pc.Up, v, pc.Axis);
                    if (pitch <= 0f) continue; // only forward bend
                    delta = Quaternion.AngleAxis(-pitch * PostureStrength * pc.Weight, pc.Axis) * delta;
                    pc.Bone.rotation = pr * delta * pc.RestRel;
                }

            UpdateSuitcase();
            UpdateWalk(Time.deltaTime, inCar);
            UpdateIdle(Time.deltaTime, inCar);
            UpdateBed(Time.deltaTime);
            mirrorW = Mathf.MoveTowards(mirrorW, suitcaseOnFloor && bedW < 1f ? 1f : 0f, Time.deltaTime / 0.5f);
            MirrorRightArm(mirrorW * (1f - bedW));
            PoseBed();
            if (gestures != null && (walkW > 0f || idleW > 0f))
            {
                Quaternion gf = GroundFrame();
                bool holdR = HoldingRightVisible();
                if (idleCur >= 0 && idleW > 0f)
                {
                    float iw = Mathf.SmoothStep(0f, 1f, idleW);
                    if (idlePrev >= 0 && prevIdleW > 0f) gestures.ApplyIdle(idlePrev, prevIdleT, iw, gf, holdR);
                    gestures.ApplyIdle(idleCur, idleT, iw * (1f - prevIdleW), gf, holdR);
                }
                if (walkW > 0f) gestures.ApplyWalk(walkPhase, Mathf.SmoothStep(0f, 1f, walkW), NewWalk ? WalkFrame() : gf, false, holdR);
            }
            MornFeet(inCar);   // task 14: morning hips/feet from the floor, not from the seated uncle
            MorningWalk.CenterWalkingPose(this, inCar);
            WalkIK(Time.deltaTime);
            LearnHipHeight(inCar);
            if (gestures != null)
            {
                gestures.Apply(Time.deltaTime);
                if (life != null)
                {
                    life.GestureWeight = gestures.Weight;
                    life.GestureFace = gestures.Face;
                    if (!gestures.Playing) life.GestureForced = false;
                }
            }
            if (life != null) life.PoseBones(Time.deltaTime);
            if (life != null && life.OfferingFruit) life.PoseFruit(Time.time);
            if (snackStarted >= 0f) life.PoseSnack(Time.time - snackStarted, snackProp);
            PoseCrateHands();
            PoseExpHands();   // task 87

            foreach (var p in partials)
                p.Bone.rotation = Quaternion.Slerp(p.Base.rotation * p.RelBase, p.Full.rotation * p.RelFull, p.Weight);

            if (life != null)
            {
                springs.TailAmp = life.TailAmp; springs.TailHz = life.TailHz; springs.TailLift = life.TailLift;
                springs.Wind = life.Wind;
            }
            WalkSprings();
            try
            {
                springs.Seated = seatBlend;
                springs.Step(Time.deltaTime, inCar ? transform.parent : null);
            }
            catch (System.Exception e)
            {
                springs.Enabled = false; // one bad chain must not freeze the whole character
                Debug.LogError("[Umamusume companion] physics disabled: " + e);
            }

            if (backpack != null && life != null && hipLink != null)
            {
                Vector3 fwd, up;
                life.Facing(out fwd, out up);
                backpack.Update(inCar && seatBlend >= 1f, transform.parent, hipLink.Girl.position, fwd, up);
            }

            // held items hang on the uncle's (invisible) hands: put his knuckles on hers, but only for rendering.
            // The items have colliders/rigidbodies (suitcase, key): moved for real they end up inside the uncle's
            // CharacterController and his walk stutters. RestoreHands() puts them back at the end of the frame.
            foreach (var hp in hands)
            {
                hp.SavedLocal = hp.UncleHand.localPosition;
                hp.UncleHand.position = hp.Wrist.position;
                if (hp.UncleKnuckle != null && hp.Knuckle != null)
                    hp.UncleHand.position += hp.Knuckle.position - hp.UncleKnuckle.position;
                hp.Pulled = true;
            }
            if (life != null) life.ConsoleLate();
            ProbeTwitch();
        }

        System.Collections.IEnumerator RestoreHandsLoop()
        {
            var eof = new WaitForEndOfFrame();
            while (true)
            {
                yield return eof;
                foreach (var hp in hands)
                    if (hp.Pulled && hp.UncleHand != null)
                    {
                        hp.UncleHand.localPosition = hp.SavedLocal;
                        hp.Pulled = false;
                    }
            }
        }

        void LateUpdate()
        {
            if (life != null) life.PatCursor();
        }

        void Update()
        {
            UpdateFollow();
            UpdateOuting();
            ShopReceipt();
            UpdateBootSnack();
            UpdateExp();   // task 87
            MornUpdate();
            FortUpdate();
            if (girlSmr == null) return;
            foreach (var r in hidden) if (keepHidden && r != null && r.enabled) r.enabled = false;
            Face();
        }

        ParticleSystem sleepZ;
        float eyesShut; // 0 open .. 1 closed (asleep)

        /// <summary>The game shows "Z" particles over the uncle while he sleeps (bed, car).</summary>
        bool Asleep()
        {
            if (sleepZ == null)
            {
                if (uncleLogic == null) uncleLogic = GetComponent<UncleLogicC>();
                if (uncleLogic != null && uncleLogic.sleepParticle != null) sleepZ = uncleLogic.sleepParticle.GetComponent<ParticleSystem>();
            }
            return sleepZ != null && sleepZ.isPlaying;
        }

        void Face()
        {
            bool asleep = Asleep();
            bool talking = animator != null && animator.GetBool("talking");
            float dt = Time.deltaTime;
            eyesShut = Mathf.MoveTowards(eyesShut, asleep ? 1f : (life != null ? life.LidsTarget : 0f), dt / (life != null ? life.LidsTime : 0.6f));
            if (springs != null) springs.Calm = asleep;
            talkBlend = Mathf.MoveTowards(talkBlend, talking ? 1f : 0f, dt / 0.2f);

            float[] acc = null;
            if (life != null)
            {
                life.Update(dt, InCar(), asleep, talking);
                if (snackStarted >= 0f) SnackFace();
                life.Face.Step(dt, 1f - 0.75f * talkBlend);
                acc = life.Face.Acc;
            }
            else return;

            // blink, unless an expression already has the eyes closed (smiling eyes, yawn)
            float blinkW = 0f;
            if (eyesShut > 0f)
            {
                blinkT = -1f;
                nextBlink = Time.time + Random.Range(1f, 3f);
            }
            else
            {
                if (blinkT < 0f && Time.time >= nextBlink) blinkT = 0f;
                if (blinkT >= 0f)
                {
                    blinkT += dt;
                    const float close = 0.07f, hold = 0.04f, open = 0.09f;
                    if (blinkT < close) blinkW = blinkT / close;
                    else if (blinkT < close + hold) blinkW = 1f;
                    else if (blinkT < close + hold + open) blinkW = 1f - (blinkT - close - hold) / open;
                    else { blinkT = -1f; nextBlink = Time.time + Random.Range(2f, 5.5f) * life.BlinkScale; }
                }
            }
            float shut = Mathf.Max(eyesShut, blinkW * (1f - life.Face.EyeClosed)) * 100f;
            life.LidsShut = Mathf.Max(eyesShut, blinkW);   // pupils stay put under shut lids (user: pupil showed through)
            foreach (int i in blink) acc[i] = Mathf.Max(acc[i], shut);

            if (talk.Length > 0)
            {
                talkTimer -= dt;
                if (talkTimer <= 0f)
                {
                    talkTimer = Random.Range(0.07f, 0.16f);
                    talkShape = talking && Random.value > 0.15f ? Random.Range(0, talk.Length) : -1;
                }
                for (int i = 0; i < talk.Length; i++)
                {
                    float target = i == talkShape ? Random.Range(55f, 90f) : 0f;
                    talkWeights[i] = Mathf.MoveTowards(talkWeights[i], target, dt * 900f);
                    acc[talk[i]] += talkWeights[i];
                }
            }
            if (faceSwap != null) SwapFace(talking, dt, shut);
            foreach (int i in driven) girlSmr.SetBlendShapeWeight(i, Mathf.Min(100f, acc[i]));
            Props.SyncShapes(girlSmr);   // task 40 bug 2: head_nohat's ears follow the face
        }

        /// <summary>Concept 14: the expression's variants (FaceRig.SwapPick), then the blink variant while the lids are
        /// shut (sleep, doze, blink) unless the eyes already show a closed variant, then a talk mouth while talking.</summary>
        void SwapFace(bool talking, float dt, float shut)
        {
            var f = life.Face;
            swapWant.Clear();
            foreach (var kv in f.SwapPick) swapWant[kv.Key] = kv.Value;
            string eyes;
            swapWant.TryGetValue("eyes", out eyes);
            if (shut >= 50f && f.SwapBlink != null && !f.IsClosedEye(eyes)) swapWant[FaceSwap.PartOf(f.SwapBlink)] = f.SwapBlink;
            if (f.SwapTalk.Length > 0)
            {
                swapTalkTimer -= dt;
                if (swapTalkTimer <= 0f)
                {
                    swapTalkTimer = Random.Range(0.08f, 0.17f);
                    swapTalkIdx = talking && Random.value > 0.2f ? Random.Range(0, f.SwapTalk.Length) : -1;
                }
                if (talking && swapTalkIdx >= 0) swapWant[FaceSwap.PartOf(f.SwapTalk[swapTalkIdx])] = f.SwapTalk[swapTalkIdx];
            }
            faceSwap.Apply(swapWant);
        }
    }
}
