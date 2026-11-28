using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 09: with the radio on she bobs her head in bursts (15-40 s) with pauses (60-180 s), not all the time.
    /// The bob itself (head/neck pitch + roll, ears) is in Life.PoseBones / Life.Update; this file only decides when it is on.
    /// </summary>
    public partial class Life
    {
        /// <summary>Share of the music time with a bob, 0..1 (slider "HeadBob"). 0 = never, 1 = always (the old behaviour).</summary>
        public static float BobShare = 0.3f;

        enum BobPhase { Off, Wait, Burst, Pause }
        BobPhase bobPhase = BobPhase.Off;
        float bobTimer, bobBlockT, bobLinear;
        bool bobOn, bobAlwaysLogged, bobForce, bobUma;

        /// <summary>Debug panel: the next head bob burst starts now (once the radio plays and nothing blocks it).</summary>
        public void BobDebugNow() { bobForce = true; }

        /// <summary>0..1 eased strength of the bob (multiplied by radioBlend where it is used).</summary>
        float bobW { get { return Mathf.SmoothStep(0f, 1f, bobLinear); } }

        /// <summary>Why a burst may not run right now (null = free).</summary>
        string BobBlock(float now, bool talking, bool reacting)
        {
            if (News.OnAir) return "news";   // task 52: a bulletin is not music (concept 05)
            if (talking) return "talking";
            if (reacting) return "reaction";
            if (Doze.Phase != DozePhase.Awake) return "doze";
            if (GestureWeight > 0.01f || act != Act.None || reachW > 0.01f || leanW > 0.01f) return "gesture";
            if (watch != null || now < attnUntil) return "sign/car";
            if (patT >= 0f) return "pat";
            if (MornPoseOn || fortT >= 0f) return "pose";
            return null;
        }

        float BobPause()
        {
            float f = Mathf.Clamp(BobShare, 0.01f, 0.99f);
            // 30 % = the designed 60-180 s; the pause shrinks towards 100 % and grows towards 0 %
            float mul = (1f - f) / f * (0.3f / 0.7f);
            if (NaritaHere) mul /= NPlaying ? NaritaBobPlay : NaritaBobFree;
            int lv = Luck.Level;
            if (lv >= 1) mul *= 0.6f;
            else if (lv <= -2) mul *= 1.5f;
            if (UmaFm.Active) mul *= 0.5f;   // task 44: Uma FM, pauses twice as short
            return Random.Range(60f, 180f) * mul;
        }

        void BobLog(string what) { Debug.Log("[Umamusume companion] MusicBob: " + what); }

        void BobStep(float dt, float now, bool music, bool talking, bool reacting)
        {
            float share = BobShare;
            bool uma = UmaFm.Active;
            if (uma != bobUma)
            {
                bobUma = uma;
                if (bobPhase == BobPhase.Pause) { bobTimer *= uma ? 0.5f : 2f; BobLog("station " + (uma ? "Uma FM" : "game") + ", pause now " + bobTimer.ToString("F0") + " s"); }
            }
            if (!music)
            {
                if (bobOn) BobLog("stop radio off");
                bobOn = false;
                bobAlwaysLogged = false;
                bobPhase = BobPhase.Off;   // the next music starts at step 1 again
                bobLinear = Mathf.MoveTowards(bobLinear, 0f, dt / 0.5f);
                return;
            }

            if (share >= 0.995f)
            {
                // old behaviour: always
                if (!bobAlwaysLogged) { bobAlwaysLogged = true; BobLog("start always"); }
                bobPhase = BobPhase.Off;
                bobOn = false;
                bobLinear = Mathf.MoveTowards(bobLinear, 1f, dt / 0.5f);
                return;
            }
            bobAlwaysLogged = false;

            if (share <= 0.005f)
            {
                if (bobOn) BobLog("stop slider 0");
                bobOn = false;
                bobPhase = BobPhase.Off;
                bobLinear = Mathf.MoveTowards(bobLinear, 0f, dt / 0.5f);
                return;
            }

            if (bobPhase == BobPhase.Off) { bobPhase = BobPhase.Wait; bobTimer = Random.Range(5f, 15f); }
            if (bobForce)
            {
                bobForce = false;
                if (bobPhase != BobPhase.Burst) { bobPhase = BobPhase.Wait; bobTimer = 0f; BobLog("debug: burst as soon as nothing blocks"); }
            }

            string block = BobBlock(now, talking, reacting);
            switch (bobPhase)
            {
                case BobPhase.Wait:
                    bobTimer -= dt;
                    if (bobTimer <= 0f && block == null)
                    {
                        bobPhase = BobPhase.Burst;
                        bobTimer = Random.Range(15f, 40f);
                        bobBlockT = 0f;
                        bobOn = true;
                        BobLog("start (" + bobTimer.ToString("F0") + " s)");
                    }
                    break;
                case BobPhase.Burst:
                    if (block != null)
                    {
                        if (bobOn) { bobOn = false; BobLog("stop " + block); }
                        bobBlockT += dt;
                        if (bobBlockT > 4f) { bobPhase = BobPhase.Pause; bobTimer = BobPause(); }   // distracted for long: a full pause
                    }
                    else
                    {
                        bobBlockT = 0f;
                        if (!bobOn) { bobOn = true; BobLog("start (resume)"); }
                        bobTimer -= dt;
                        if (bobTimer <= 0f)
                        {
                            bobOn = false;
                            bobPhase = BobPhase.Pause;
                            bobTimer = BobPause();
                            BobLog("stop burst end, pause " + bobTimer.ToString("F0") + " s");
                        }
                    }
                    break;
                case BobPhase.Pause:
                    bobTimer -= dt;
                    if (bobTimer <= 0f) { bobPhase = BobPhase.Wait; bobTimer = 0f; }   // waits until nothing blocks
                    break;
            }
            bobLinear = Mathf.MoveTowards(bobLinear, bobOn ? 1f : 0f, dt / 0.5f);
        }
    }
}
