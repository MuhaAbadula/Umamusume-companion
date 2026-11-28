using System;
using System.IO;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 79 (concept 13, BALANCE.md "Доверие Нариты"): Narita's trust, 5 stages from hidden points.
    /// Thresholds 25 / 70 / 130 / 200; soft rollback: a stage drops only when the points are 15 under its threshold, at most
    /// one stage per game day. State: ModSaves\FukuUncle\trust.txt (survives a load); a new game = stage 1.
    /// Setting "TrustGrows" off = stage 5 at once. Log: [Umamusume companion] trust: stage N (reason).
    /// Here = the picked pack has the "trust" feature (info.txt, or a props\console.obj in the pack).
    /// </summary>
    public static class Trust
    {
        public static bool Here { get { return CharFeatures.Has("trust"); } }
        /// <summary>Setting "TrustGrows".</summary>
        public static bool Grows = true;

        // ---- numbers (BALANCE.md, approved 2026-11-15)
        static readonly int[] Th = { 0, 25, 70, 130, 200 };
        public const int Leg = 10, LegClean = 5, Motel = 5, QuietNight = 8, Pat = 2, PatTooSoon = -3,
            CrashLight = -4, CrashMed = -10, Town = -2, Abandoned = -6;
        const int RollbackMargin = 15, MaxPts = 260, PatsPerDay = 2;
        public const float PatGap = 600f, QuietEngineOff = 300f, AbandonGameMinutes = 10f;

        static int pts, stage = 1, dropDay = -1, patDay = -1, patsToday, motelDay = -1, quietDay = -1;
        static bool loaded;
        static float lastPat = -1f;

        /// <summary>1..5 for her (5 when the toggle is off), 0 for another character.</summary>
        public static int Stage { get { if (!Here) return 0; Load(); return Grows ? stage : 5; } }
        public static int Points { get { Load(); return pts; } }

        static string StatePath
        {
            get { return Path.Combine(CompanionStorage.SaveDirectory, "trust.txt"); }
        }

        static void Log(string reason) { Debug.Log("[Umamusume companion] trust: stage " + Stage + " (" + reason + ")"); }

        public static string Status
        {
            get { return Here ? "Trust: stage " + Stage + (Grows ? "" : " (toggle off)") + ", points " + Points : "Trust: not this character"; }
        }

        /// <summary>Points change by a rule. Logged every time (with the points), the stage follows.</summary>
        public static void Add(int d, string reason)
        {
            if (!Here) return;
            Load();
            int was = stage;
            pts = Mathf.Clamp(pts + d, 0, MaxPts);
            // up: as far as the points reach
            while (stage < 5 && pts >= Th[stage]) stage++;
            // down: soft, one stage per game day
            int day = Luck.Day;
            if (stage == was && stage > 1 && pts < Th[stage - 1] - RollbackMargin)
            {
                if (day < 0 || day != dropDay) { stage--; dropDay = day; }
                else reason += ", drop held: one stage a day";
            }
            Save();
            Log(reason + " " + (d >= 0 ? "+" : "") + d + ", points " + pts + (stage != was ? ", was " + was : ""));
        }

        /// <summary>A pat (awake): +2 at most twice a game day and 10 min apart; sooner = -3.</summary>
        public static void OnPat()
        {
            if (!Here) return;
            Load();
            float now = Time.time;
            bool soon = lastPat >= 0f && now - lastPat < PatGap;
            lastPat = now;
            if (soon) { Add(PatTooSoon, "pat again within 10 min"); return; }
            int day = Luck.Day;
            if (patDay != day) { patDay = day; patsToday = 0; }
            if (patsToday >= PatsPerDay) { Log("pat, no change: " + PatsPerDay + " today already"); return; }
            patsToday++;
            Add(Pat, "pat");
        }

        public static void OnMotelNight()
        {
            if (!Here) return;
            Load();
            int day = Luck.Day;
            if (day >= 0 && day == motelDay) { Log("motel night, already counted"); return; }
            motelDay = day;
            Add(Motel, "night in the motel");
        }

        /// <summary>A quiet night stop out of town: once per game day.</summary>
        public static bool QuietDone { get { Load(); int d = Luck.Day; return d >= 0 && d == quietDay; } }
        public static void OnQuietNight()
        {
            if (!Here) return;
            Load();
            quietDay = Luck.Day;
            Add(QuietNight, "quiet night stop out of town");
            if (Life.Current != null && Life.Current.Say != null) Life.Current.Say("narita_night_quiet");
        }

        /// <summary>Debug +1 / -1: the stage moves at once, points to its threshold.</summary>
        public static string DebugStep(int dir)
        {
            if (!Here) return "Trust: not this character";
            Load();
            stage = Mathf.Clamp(stage + dir, 1, 5);
            pts = Th[stage - 1];
            Save();
            Log("debug " + (dir > 0 ? "+1" : "-1") + ", points " + pts + (Grows ? "" : ", but the toggle is off: she acts as stage 5"));
            return Status;
        }

        public static void Reset(string why)
        {
            loaded = true;
            pts = 0; stage = 1; dropDay = patDay = motelDay = quietDay = -1; patsToday = 0; lastPat = -1f;
            Save();
            if (Here) Log("reset: " + why);
        }

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                string path = StatePath;
                if (!File.Exists(path))
                    path = Path.Combine(Path.Combine(Path.Combine(Application.persistentDataPath, "ModSaves"), "FukuUncle"), "trust.txt");
                if (!File.Exists(path)) return;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var kv = raw.Split('=');
                    int v;
                    if (kv.Length != 2 || !int.TryParse(kv[1].Trim(), out v)) continue;
                    switch (kv[0].Trim())
                    {
                        case "points": pts = Mathf.Clamp(v, 0, MaxPts); break;
                        case "stage": stage = Mathf.Clamp(v, 1, 5); break;
                        case "dropDay": dropDay = v; break;
                        case "patDay": patDay = v; break;
                        case "patsToday": patsToday = v; break;
                        case "motelDay": motelDay = v; break;
                        case "quietDay": quietDay = v; break;
                    }
                }
                Debug.Log("[Umamusume companion] trust: stage " + stage + " (loaded, points " + pts + ")");
            }
            catch (Exception e) { Debug.LogWarning("[Umamusume companion] trust: can't read state: " + e.Message); }
        }

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                string pending = StatePath + ".tmp";
                File.WriteAllLines(pending, new[] { "# FukuUncle: Narita's trust (task 79)", "points=" + pts, "stage=" + stage,
                    "dropDay=" + dropDay, "patDay=" + patDay, "patsToday=" + patsToday, "motelDay=" + motelDay, "quietDay=" + quietDay });
                if (File.Exists(StatePath)) File.Replace(pending, StatePath, null);
                else File.Move(pending, StatePath);
            }
            catch (Exception e) { Debug.LogWarning("[Umamusume companion] trust: can't save state: " + e.Message); }
        }
    }
}
