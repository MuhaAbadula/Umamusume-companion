using HarmonyLib;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// The game starts UncleLogicC.CarCollisionLight / CarCollisionMed by name on a crash (CarControleScriptC).
    /// A postfix on them gives her a face for it: fright first, then a grumble.
    /// The crash line ("ouch") would switch the Animator to "talking", and the uncle's talking pose turns her to the
    /// driver and breaks the fright. While that line plays, "talking" is switched back off right after the game sets
    /// it (inside the same coroutine step, before the Animator runs): sound and text stay, the pose and lip flaps don't.
    /// </summary>
    public static class ReactPatch
    {
        static bool applied;
        static float quietUntil = -1f;  // the next crash line plays without the talking pose

        public static void Apply()
        {
            if (applied) return;
            var h = new Harmony("osobl.jalopy.umamusumecompanion.react");
            h.Patch(AccessTools.Method(typeof(UncleLogicC), "CarCollisionLight"), postfix: new HarmonyMethod(typeof(ReactPatch), nameof(Light)));
            h.Patch(AccessTools.Method(typeof(UncleLogicC), "CarCollisionMed"), postfix: new HarmonyMethod(typeof(ReactPatch), nameof(Med)));
            var heavy = AccessTools.Method(typeof(UncleLogicC), "CarCollisionHeavy");
            if (heavy != null) h.Patch(heavy, postfix: new HarmonyMethod(typeof(ReactPatch), nameof(Heavy)));
            var iter = AccessTools.FirstInner(typeof(UncleLogicC), t => t.Name.Contains("InterruptSpeech"));
            if (iter != null)
                h.Patch(AccessTools.Method(iter, "MoveNext"), postfix: new HarmonyMethod(typeof(ReactPatch), nameof(SpeechStep)));
            else Debug.LogWarning("[Umamusume companion] InterruptSpeech iterator not found: crash lines keep the talking pose");
            h.Patch(AccessTools.Method(typeof(UncleLogicC), "CarSpeech"), prefix: new HarmonyMethod(typeof(ReactPatch), nameof(CarSpeechPrefix)));
            var horn = AccessTools.Method(typeof(CarHornC), "Trigger");
            if (horn != null) h.Patch(horn, postfix: new HarmonyMethod(typeof(ReactPatch), nameof(Horn)));
            var fail = AccessTools.Method(typeof(IgnitionLogicC), "IgnitionGoAndFail");
            if (fail != null) h.Patch(fail, postfix: new HarmonyMethod(typeof(ReactPatch), nameof(EngineFail)));
            var strike = AccessTools.Method(typeof(RainLightningC), "Strike");
            if (strike != null) h.Patch(strike, postfix: new HarmonyMethod(typeof(ReactPatch), nameof(Thunder)));
            applied = true;
        }

        static bool Active { get { return Life.Current != null && Life.Current.Enabled; } }

        static void Light()
        {
            Morning.CrashSinceMorning = true;
            if (!Active) return;
            Doze.OnCrash();
            Life.OnCrashTrust(false);
            Life.Current.React("surprise", 1.0f, "annoyed", 1.8f);
            quietUntil = Time.time + 6f;
        }

        static void Med()
        {
            Morning.CrashSinceMorning = true;
            if (!Active) return;
            Doze.OnCrash();
            Life.OnCrashTrust(true);
            Life.Current.React("shock", 1.4f, "worried", 3f);
            quietUntil = Time.time + 6f;
        }

        // speed >= 100: the game has its own heavy-crash line; only the morning flag is kept here
        static void Heavy()
        {
            Morning.CrashSinceMorning = true;
        }

        static void SpeechStep(object __instance, bool __result)
        {
            if (Time.time >= quietUntil) return;
            var uncle = Traverse.Create(__instance).Field("$this").GetValue<UncleLogicC>();
            var an = uncle != null ? uncle.GetComponent<Animator>() : null;
            if (an != null) an.SetBool("talking", false);
            if (!__result) quietUntil = -1f; // the crash line is over
        }

        static void Horn(CarHornC __instance)
        {
            if (Active && __instance != null && !__instance.isDead) Life.Current.OnHorn();
        }

        static void EngineFail()
        {
            if (Active) Life.Current.OnEngineFail();
        }

        /// <summary>A thunderclap plays (the game rolls the next one in 10-240 s).</summary>
        static void Thunder(RainLightningC __instance)
        {
            if (!Active || __instance == null || !__instance.isActiveAndEnabled) return;
            Life.Current.OnThunder();
        }

        static bool CarSpeechPrefix()
        {
            return Time.time >= quietUntil;
        }
    }
}
