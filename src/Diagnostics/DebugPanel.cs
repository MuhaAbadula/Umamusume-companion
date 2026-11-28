using System;
using System.Collections.Generic;
using HarmonyLib;
using Pathfinding;
using UnityEngine;

namespace UmamusumeCompanion
{
#if DEBUG
    public sealed partial class CompanionMod
    {
        DebugPanel panel;
        internal KeyCode PanelKey() { return GetPrimaryKeybind("DebugPanelKey"); }

        partial void RegisterDebugSettings()
        {
            AddToggle("ShowUncle", "Show the uncle too (debug)", false);
            AddToggle("PhysicsDebug", "Write physics stats to output_log.txt (debug)", false);
            AddToggle("DebugPanel", "Debug panel for testing: motel, bed sleep, night drive, triggers (Ctrl + the key below)", false);
            AddKeybind("JumpTown", "Debug: Ctrl + this key = sleep and wake up in the next town's motel", KeyCode.F9);
            AddKeybind("Gesture", "Debug: Ctrl + this key = play the test gesture", KeyCode.F11);
            AddKeybind("DozeKey", "Debug: Ctrl + this key = she falls asleep now / wakes up", KeyCode.F8);
            AddKeybind("Lean", "Debug: Ctrl + this key = lean on the door now / stop (window must be open)", KeyCode.F12);
            AddKeybind("Follow", "Debug: Ctrl + this key = she walks after you / stops (to watch her walk)", KeyCode.F7);
            AddKeybind("FortuneKey", "Fukukitaru debug: Ctrl + this key = she tells the fortune now (new draw)", KeyCode.F5);
            AddKeybind("FortuneCycle", "Fukukitaru debug: Ctrl + this key = next luck level L1..L5, said at once (L5: L5b after 60 s)", KeyCode.F6);
            AddKeybind("ShortTrip", "Debug: Ctrl + this key = short trip on/off (routes you pick are one segment) + the fastest engine", KeyCode.F10);
            AddKeybind("DebugPanelKey", "Debug: Ctrl + this key = open/close the debug panel (needs the Debug panel toggle)", KeyCode.F4);
        }

        partial void StartPanel()
        {
            try { JapanRoad.Apply(); }
            catch (Exception e) { Debug.LogWarning("[Umamusume companion] JapanRoad: fail patch (" + e.Message + ")"); }
            JapanRoad.Dir = System.IO.Path.Combine(AssetsPath, "road");    // task 56: japan.unity3d
            panel = gameObject.GetComponent<DebugPanel>();
            if (panel == null) panel = gameObject.AddComponent<DebugPanel>();
            panel.Owner = this;
            DebugPanel.Allowed = GetToggleValue("DebugPanel");
        }

        partial void RefreshPanelSetting()
        {
            DebugPanel.Allowed = GetToggleValue("DebugPanel");
        }

        partial void UpdateDebugShortcuts(bool ctrl)
        {
            if (ctrl && Input.GetKeyDown(GetPrimaryKeybind("DozeKey")) && driver != null) JaLoader.Console.Log(ModID, driver.DozeKey());
            if (ctrl && Input.GetKeyDown(GetPrimaryKeybind("JumpTown"))) JumpToNextTown();
            if (ctrl && Input.GetKeyDown(GetPrimaryKeybind("ShortTrip"))) ShortTrip();
            if (ctrl && Input.GetKeyDown(GetPrimaryKeybind("FortuneKey")) && driver != null) JaLoader.Console.Log(ModID, driver.FortuneDebugRedraw());
            if (ctrl && Input.GetKeyDown(GetPrimaryKeybind("FortuneCycle")) && driver != null) JaLoader.Console.Log(ModID, driver.FortuneDebugCycle());
            if (ctrl && Input.GetKeyDown(GetPrimaryKeybind("Lean")) && driver != null) driver.ToggleLean();
            if (ctrl && Input.GetKeyDown(GetPrimaryKeybind("Follow")) && driver != null) JaLoader.Console.Log(ModID, driver.ToggleFollow());
            if (ctrl && Input.GetKeyDown(GetPrimaryKeybind("Gesture")) && driver != null)
            {
                string g = driver.PlayGesture(null);
                JaLoader.Console.Log(ModID, g != null ? "Gesture: " + g : "No gesture clips in the character's anims folder.");
            }
            if (ctrl && Input.GetKeyDown(PanelKey()))
            {
                if (!DebugPanel.Allowed) JaLoader.Console.Log(ModID, "Debug panel is off: switch on \"Debug panel\" in the mod settings.");
                else if (panel != null) panel.Toggle();
            }
        }
    }

    /// <summary>
    /// Task 17: in-game debug panel (toggle "DebugPanel" in the settings, Ctrl + key "DebugPanelKey", default F4).
    /// Buttons jump straight to test situations: the motel lot, a real bed sleep (BedLogicC.Trigger, so the real morning
    /// runs), a night drive, triggers, the old debug keys, time. Every press logs "[Umamusume companion] Debug: <button> -> result".
    /// While open the cursor is free, mouse look and world clicks are off.
    /// </summary>
    public class DebugPanel : MonoBehaviour
    {
        public static bool Allowed;
        public static bool IsOpen { get { return inst != null && inst.open; } }
        static DebugPanel inst;

        public CompanionMod Owner;

        bool open;
        string result = "";
        string status = "", status2 = "";
        float statusAt;
        static string lastLog = "";

        // what the panel switched off while open
        MouseLook mlPlayer, mlCam;
        DragRigidbodyC drag;
        bool mlPlayerWas, mlCamWas, dragWas, lockWas;

        void Awake()
        {
            inst = this;
            Application.logMessageReceived += OnLog;

        }

        void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
            if (inst == this) inst = null;
        }

        static void OnLog(string msg, string stack, LogType type)
        {
            if (msg == null || !msg.StartsWith("[Umamusume companion]") || msg.StartsWith("[Umamusume companion] Debug:")) return;
            lastLog = msg.Substring(11).Trim();
        }

        public void Toggle()
        {
            if (open) Close();
            else Open();
        }

        void Open()
        {
            if (Camera.main == null) return;
            open = true;
            var cam = Camera.main.gameObject;
            var player = cam.transform.parent != null ? cam.transform.parent.gameObject : null;
            mlCam = cam.GetComponent<MouseLook>();
            mlPlayer = player != null ? player.GetComponent<MouseLook>() : null;
            drag = cam.GetComponent<DragRigidbodyC>();
            mlCamWas = mlCam != null && mlCam.enabled;
            mlPlayerWas = mlPlayer != null && mlPlayer.enabled;
            dragWas = drag != null && drag.enabled;
            if (mlCamWas) mlCam.enabled = false;
            if (mlPlayerWas) mlPlayer.enabled = false;
            if (dragWas) drag.enabled = false;
            var mm = MainMenuC.Global;
            lockWas = mm != null && mm.lockCursor;
            if (mm != null) mm.lockCursor = false;   // MainMenuC locks the cursor again on any key or click
            FreeCursor();
            RefreshStatus();
            Debug.Log("[Umamusume companion] Debug: panel open");
        }

        void Close()
        {
            if (!open) return;
            open = false;
            if (mlCamWas && mlCam != null) mlCam.enabled = true;
            if (mlPlayerWas && mlPlayer != null) mlPlayer.enabled = true;
            if (dragWas && drag != null) drag.enabled = true;
            mlCamWas = mlPlayerWas = dragWas = false;
            var mm = MainMenuC.Global;
            if (mm != null && lockWas) mm.lockCursor = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        static void FreeCursor()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        void Update()
        {
            if (!open) return;
            if (!Allowed || Camera.main == null) { Close(); return; }
            if (Time.unscaledTime >= statusAt) RefreshStatus();
        }

        void LateUpdate()
        {
            if (open) FreeCursor();
        }

        // ------------------------------------------------------------ GUI
        Vector2 scroll;

        void OnGUI()
        {
            if (!open) return;
            float s = Mathf.Max(1f, Screen.height / 900f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));
            float w = 600f, h = Mathf.Min(Screen.height / s - 40f, 660f);
            GUILayout.BeginArea(new Rect(20f, 20f, w, h), GUI.skin.box);
            GUILayout.Label("Umamusume companion debug  (Ctrl+" + KeyName() + " closes)");
            GUILayout.Label(status);
            GUILayout.Label(status2);
            if (!string.IsNullOrEmpty(result)) GUILayout.Label("> " + result);
            scroll = GUILayout.BeginScrollView(scroll);
            if (Trust.Here)
            {
                GUILayout.Label(Trust.Status);
                Row(B("Trust -1", () => Trust.DebugStep(-1), false), B("Trust +1", () => Trust.DebugStep(1), false));
            }

            if (CharFeatures.Has("boot_snack"))
            {
                GUILayout.Label("Oguri Cap");
                Row(B("Preview snack (no food needed)", () => TestSnack(true), false),
                    B("Eat 1 packet from boot", () => TestSnack(false), false));
            }
            GUILayout.Label("Motel");
            Row(B("To the motel now", ToMotel, true), B("Sleep in the motel bed now", SleepNow, true));
            GUILayout.Label("Morning shopping (Nice Nature)");
            Row(B("Inspect supplies / prices", () => D(d => d.ShopPreview(FindMotel(d.GetComponent<UncleLogicC>()))), false),
                B("Buy morning supplies", () => D(d => d.BuyMorningSupplies(FindMotel(d.GetComponent<UncleLogicC>()))), true));
            Row(B("Purchase receipt", () => D(d => d.ShopStatus), false),
                B("Balance checks", ShoppingRules.Checks, false));
            GUILayout.Label("Night and doze");
            Row(B("Night drive now (23:00, doze ready)", NightDrive, false), B("Doze now / wake", () => D(d => d.DozeKey()), false));
            GUILayout.Label("Triggers");
            Row(B("Thunder", Thunder, false), B("Pothole", Pothole, false), B("Light crash", Crash, false), B("Route talk", RouteTalk, false), B("Return talk now", ReturnTrip.DebugNow, false));
            Row(B("Spawn roadside crate", SpawnRoadsideCrate, false));
            GUILayout.Label("Her");
            Row(B("G outing", Outing, false), B("Follow on/off", () => D(d => d.ToggleFollow()), false),
                B("Test gesture", () => D(d => { string g = d.PlayGesture(null); return g != null ? "gesture " + g : "no gesture clips"; }), false),
                B("Lean on door", () => D(d => { d.ToggleLean(); return "lean toggled (window must be open)"; }), false));
            Row(B("Put her in her seat", () => D(d => d.DebugSeat("debug panel")), false));
            GUILayout.Label("Pranks (Sweep)");
            int built = Life.Current != null ? Life.Current.PranksBuilt : 0;
            Row(B(built == 0 ? "Prank now (no pranks yet: tasks 38+)" : "Prank now (" + built + " built, in turn)", () => Life.Current != null ? Life.Current.DebugPrank() : "she is not here", false),
                B("Radio trick now", () => Life.Current != null ? Life.Current.DebugRadioTrick() : "she is not here", false));
            Row(B("Reach test (next cab target)", () => Life.Current != null ? Life.Current.DebugReach() : "she is not here", false));
            GUILayout.Label("Experiments (Tachyon)");
            Row(B("Potion effect now (in turn)", () => D(d => d.DebugPotion()), false), B("Stop effect", CompanionDriver.DebugStopFx, false),
                B("Offer a vial now", () => D(d => d.DebugOffer()), false), B("Journal summary", () => D(d => d.ExpSummaryNow()), false));
            Row(B("Additive: arm (A1..A3 in turn)", () => D(d => d.DebugAddonArm()), false), B("Additive: pour now", () => D(d => d.DebugAddonNow()), false),
                B("Additive: off", () => D(d => d.DebugAddonStop()), false));
            GUILayout.Label("Souvenirs (" + Souvenirs.Count + " on the dash)");
            Row(B("Souvenir now", Souvenirs.DebugNow, false), B("Remove souvenirs", Souvenirs.DebugRemove, false));
            if (Souvenirs.Here)
                Row(B("Queue all 6 souvenirs", Souvenirs.DebugQueueAll, false));
            if (Luck.Mine)
            {
                GUILayout.Label("Fortune");
                Row(B("Fortune now (new draw)", () => D(d => d.FortuneDebugRedraw()), false), B("Next luck level", () => D(d => d.FortuneDebugCycle()), false));
                Row(B("Border: luck roll now", LuckBorder.DebugRoll, false));
            }
            else
            {
                GUILayout.Label("Fortune (not this character)");
                Func<string> no = () => Luck.NotMine;
                Row(B("Fortune now (not this character)", no, false), B("Next luck level (not this character)", no, false));
                Row(B("Border: luck roll (not this character)", no, false));
            }
            GUILayout.Label("Time and radio");
            Row(B("+1 h", () => AddHour(), false), B("6:00", () => SetTime(6), false), B("23:00", () => SetTime(23), false),
                B("Radio on + head bob", RadioBob, false), B("Uma FM: next track", UmaFm.DebugNext, false));
            Row(B("News after this song", News.DebugArm, false), B("Reset heard news", News.DebugResetHeard, false));
            GUILayout.Label("Trip");
            Row(B("Short trip + engine (Ctrl+F10)", () => { Owner.ShortTrip(); return "see the console"; }, false),
                B("Next town by reload (Ctrl+F9)", () => { Owner.JumpToNextTown(); return "see the console"; }, true));
            Row(B("Road spot probe", RoadSpots.DebugProbe, false));
            GUILayout.Label("Japan road (test, task 58: Japanese look)" + (JapanRoad.Armed ? ": ARMED for the next leg" : ""));
            Row(B(JapanRoad.Armed ? "Japanese segment next leg (armed: click to disarm)" : "Japanese segment next leg", JapanRoad.DebugArm, false),
                B("Show segment nodes", JapanRoad.DebugShowNodes, false));

            GUILayout.EndScrollView();
            if (GUILayout.Button("Close")) Close();
            GUILayout.EndArea();
        }

        string KeyName()
        {
            try { return Owner != null ? Owner.PanelKey().ToString() : "F4"; }
            catch (Exception) { return "F4"; }
        }

        struct Btn { public string Label; public Func<string> Act; public bool CloseFirst; }

        static Btn B(string label, Func<string> act, bool closeFirst) { return new Btn { Label = label, Act = act, CloseFirst = closeFirst }; }

        void Row(params Btn[] bs)
        {
            GUILayout.BeginHorizontal();
            foreach (var b in bs)
                if (GUILayout.Button(b.Label)) Press(b);
            GUILayout.EndHorizontal();
        }

        void Press(Btn b)
        {
            if (b.CloseFirst) Close();   // the action moves the player or starts a game sequence: give the controls back first
            string r;
            try { r = b.Act() ?? "done"; }
            catch (Exception e) { r = "failed: " + e.Message; Debug.LogError("[Umamusume companion] Debug: " + b.Label + " failed: " + e); }
            result = b.Label + ": " + r;
            Debug.Log("[Umamusume companion] Debug: " + b.Label + " -> " + r);
            try { JaLoader.Console.Log(CompanionStorage.ModId, "Debug: " + result); } catch (Exception) { }
            RefreshStatus();
        }

        // ------------------------------------------------------------ world access
        static CompanionDriver driverCache;
        static CompanionDriver Drv()
        {
            if (driverCache == null) driverCache = FindObjectOfType<CompanionDriver>();
            return driverCache;
        }

        static string D(Func<CompanionDriver, string> f)
        {
            var d = Drv();
            return d != null ? f(d) : "she is not here (no CompanionDriver)";
        }

        string TestSnack(bool preview)
        {
            var d = Drv();
            if (d == null) return "Companion is still loading.";
            string reason = d.DebugBootSnack(preview);
            if (reason != null) return reason;
            Close();
            return preview ? "Playing snack preview." : "Eating one packet from the boot.";
        }

        static UncleLogicC Uncle()
        {
            var go = GameObject.FindWithTag("Uncle");
            return go != null ? go.GetComponent<UncleLogicC>() : null;
        }

        static RouteGeneratorC Route()
        {
            var go = GameObject.FindWithTag("Director");
            return go != null ? go.GetComponent<RouteGeneratorC>() : null;
        }

        static CarLogicC Car()
        {
            var go = GameObject.FindWithTag("CarLogic");
            return go != null ? go.GetComponent<CarLogicC>() : null;
        }

        static Transform CarRoot()
        {
            var go = GameObject.FindWithTag("CarLogic");
            return go != null && go.transform.parent != null && go.transform.parent.parent != null ? go.transform.parent.parent : null;
        }

        static GameObject Player()
        {
            var cam = Camera.main;
            return cam != null && cam.transform.parent != null ? cam.transform.parent.gameObject : GameObject.FindWithTag("Player");
        }

        static bool PlayerSat()
        {
            var p = Player();
            var rc = p != null ? p.GetComponent<RigidbodyControllerC>() : null;
            return rc != null && rc.isSat;
        }

        // ------------------------------------------------------------ motel
        /// <summary>The game's motel for tonight: UncleLogicC.motel (the last motel that started), else the nearest one.</summary>
        static MotelLogicC FindMotel(UncleLogicC u)
        {
            MotelLogicC m = u != null && u.motel != null ? u.motel.GetComponent<MotelLogicC>() : null;
            if (m != null) return m;
            var cam = Camera.main;
            float best = float.MaxValue;
            foreach (var x in FindObjectsOfType<MotelLogicC>())
            {
                float dd = cam != null ? (x.transform.position - cam.transform.position).sqrMagnitude : 0f;
                if (dd < best) { best = dd; m = x; }
            }
            return m;
        }

        static BedLogicC RoomBed(MotelLogicC m)
        {
            if (m == null || m.roomBeds == null || m.roomBeds.Length == 0) return null;
            int i = Mathf.Clamp(m.roomNumber, 0, m.roomBeds.Length - 1);
            return m.roomBeds[i] != null ? m.roomBeds[i].GetComponent<BedLogicC>() : null;
        }

        static UncleBedTriggerC RoomUncleBed(MotelLogicC m, BedLogicC bed)
        {
            UncleBedTriggerC best = null;
            float bd = float.MaxValue;
            Transform root = m.transform.root;
            foreach (var t in FindObjectsOfType<UncleBedTriggerC>())
            {
                if (t.transform.root != root) continue;
                float dd = (t.transform.position - bed.transform.position).sqrMagnitude;
                if (dd < bd) { bd = dd; best = t; }
            }
            return best;
        }

        static readonly System.Reflection.FieldInfo bedBlock = AccessTools.Field(typeof(BedLogicC), "block");

        /// <summary>The bed can be slept in again: its click box back (the game destroys it at a sleep) and the run flag off.</summary>
        static void ReadyBed(BedLogicC bed)
        {
            if (bedBlock != null) bedBlock.SetValue(bed, false);
            if (bed.GetComponent<BoxCollider>() == null) bed.gameObject.AddComponent<BoxCollider>();
        }

        /// <summary>
        /// A motel you already woke up in (a loaded save, a debug sleep) is put back to its evening: not slept, not paid,
        /// parking not in the morning mode, and the route level one town back, so the sleep advances it to this town
        /// again (BedLogicC.Trigger does ++ on the way out, -- on the way home). Returns what changed, or null.
        /// </summary>
        static string EveningState(MotelLogicC m, BedLogicC bed, RouteGeneratorC rg, UncleLogicC u)
        {
            var pt = m.parkingTrigger != null ? m.parkingTrigger.GetComponent<MotelParkingTriggerC>() : null;
            if (!m.hasSlept && (pt == null || !pt.isMorning)) return null;
            int lv0 = rg.routeLevel;
            bool home0 = rg.drivingTowardsHome;
            if (rg.routeLevel >= 6) { rg.routeLevel = 5; rg.drivingTowardsHome = false; }
            else if (ReturnTrip.Active || rg.drivingTowardsHome) { rg.routeLevel++; rg.drivingTowardsHome = true; }
            else { rg.routeLevel = Mathf.Max(0, rg.routeLevel - 1); rg.drivingTowardsHome = false; }
            m.hasSlept = false;
            m.hasPaid = false;
            m.hasGreeted = false;
            m.isWelcomed = false;
            m.hasSaidBye = false;
            if (pt != null) pt.isMorning = false;
            var desk = m.GetComponent<BoxCollider>();
            if (desk != null) desk.enabled = true;   // Payed() switched the desk click off
            if (u != null) u.hasSaidLetsCheckIn = false;
            if (bed != null) ReadyBed(bed);
            return "evening again, route level " + lv0 + (home0 ? " home" : "") + " -> " + rg.routeLevel + (rg.drivingTowardsHome ? " home" : "");
        }

        static void StopBody(GameObject go)
        {
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
        }

        /// <summary>The player out of the driver's seat at once (DoorExitLogicC.Trigger + RestoreControl without the tween).</summary>
        static void PlayerOutOfSeat(CarLogicC cl)
        {
            var player = Player();
            var cam = Camera.main;
            if (player == null || cam == null || cl == null || cl.playerSeat == null) return;
            var seat = cl.playerSeat.GetComponent<SeatLogicC>();
            var rc = player.GetComponent<RigidbodyControllerC>();
            if (rc == null || !rc.isSat) return;
            if (seat != null) seat.isSat = false;
            rc.isSat = false;
            var mlp = player.GetComponent<MouseLook>();
            var mlc = cam.GetComponent<MouseLook>();
            if (mlp != null) { mlp.isSat = false; mlp.enabled = true; }
            if (mlc != null) { mlc.isSat = false; mlc.enabled = true; }
            player.transform.parent = null;
            if (seat != null)
            {
                seat.StopInteriorRainAudio();
                seat.gameObject.tag = "Interactor";
                if (seat.doorExit != null) seat.doorExit.SetActive(false);
            }
            var cc = player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = true;
            var rb = player.GetComponent<Rigidbody>();
            if (rb != null)
            {
                var cap = player.GetComponent<CapsuleCollider>();
                if (cap != null) cap.enabled = true;
                rb.detectCollisions = true;
                rb.isKinematic = false;
            }
            var drg = cam.GetComponent<DragRigidbodyC>();
            if (drg != null) drg.enabled = true;
            var fs = player.GetComponent<FootstepsC>();
            if (fs != null) fs.enabled = true;
        }

        static void PlacePlayer(Vector3 pos, float yaw)
        {
            var player = Player();
            if (player == null) return;
            player.transform.position = pos;
            player.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            var rb = player.GetComponent<Rigidbody>();
            if (rb != null) { rb.position = pos; if (!rb.isKinematic) rb.velocity = Vector3.zero; }
        }

        static string ToMotel()
        {
            var u = Uncle();
            var rg = Route();
            var cl = Car();
            var car = CarRoot();
            if (u == null || rg == null || cl == null || car == null) return "not in the game world";
            if (MainMenuC.Global != null && MainMenuC.Global.restrictPause) return "busy, a game sequence runs (sleep, border...)";
            var m = FindMotel(u);
            if (m == null) return "no motel loaded (drive on until the next town, or load a save)";
            var bed = RoomBed(m);
            string eve = EveningState(m, bed, rg, u);

            // her into the seat first: the game parents her to the car, so she rides along
            string her = D(d => d.DebugSeat("to the motel"));

            Transform spot = bed != null && bed.carLoadTransform != null ? bed.carLoadTransform : m.parkingTrigger != null ? m.parkingTrigger.transform : null;
            if (spot == null) return "the motel has no parking spot";
            var rb = car.GetComponent<Rigidbody>();
            if (rb != null) rb.isKinematic = true;
            car.position = spot.position;
            car.eulerAngles = spot.eulerAngles;
            if (rb != null) { rb.isKinematic = false; rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            m.carParked = true;

            string you;
            if (PlayerSat()) you = "you ride along in your seat (pull the handbrake: she starts the check-in)";
            else
            {
                var seat = cl.playerSeat != null ? cl.playerSeat.GetComponent<SeatLogicC>() : null;
                Vector3 p = seat != null && seat.doorExit != null ? seat.doorExit.transform.position : car.position - car.right * 1.6f + Vector3.up * 0.5f;
                PlacePlayer(p + Vector3.up * 0.1f, car.eulerAngles.y);
                you = "you stand at your door";
            }
            Debug.Log("[Umamusume companion] Debug: motel " + m.transform.root.name + " room " + m.roomNumber + ", car at " + spot.position.ToString("F1") + ", route level " + rg.routeLevel);
            return "car parked at the motel (room " + (m.roomNumber + 1) + "), " + you + "; her: " + her + (eve != null ? "; " + eve : "");
        }

        static string SleepNow()
        {
            var u = Uncle();
            var rg = Route();
            var cl = Car();
            if (u == null || rg == null || cl == null || MainMenuC.Global == null) return "not in the game world";
            if (MainMenuC.Global.restrictPause) return "busy, a game sequence runs (sleep, border...)";
            var m = FindMotel(u);
            if (m == null) return "no motel loaded (drive on until the next town, or load a save)";
            var bed = RoomBed(m);
            if (bed == null || bed.path == null || bed.path.Length < 3 || bed.path[2] == null) return "no bed with a standing spot in room " + (m.roomNumber + 1);
            string eve = EveningState(m, bed, rg, u);

            // checked in, as after paying and walking in with the key
            m.hasPaid = true;
            m.hasGreeted = true;
            if (m.roomDoors != null && m.roomNumber < m.roomDoors.Length && m.roomDoors[m.roomNumber] != null)
            {
                var door = m.roomDoors[m.roomNumber].GetComponent<EnvironmentDoorsC>();
                if (door != null) door.isLocked = false;
            }

            // the sleep deletes the route (spawnedRouteSegments) and the old starting town: keep the town of this motel
            var root = m.transform.root.gameObject;
            bool kept = false;
            if (rg.startingEnvironment == root) { rg.startingEnvironment = null; kept = true; }
            if (rg.spawnedRouteSegments != null && rg.spawnedRouteSegments.RemoveAll(g => g == root) > 0) kept = true;

            PlayerOutOfSeat(cl);
            PlacePlayer(bed.path[2].position, bed.path[2].eulerAngles.y);
            ReadyBed(bed);
            bed.StartCoroutine("Trigger");   // the game's own sleep: camera to the pillow, black, new day, wake

            // her onto the room's bed (after the sleep started: restrictPause makes the game skip the 4 evening lines)
            string her;
            var trig = RoomUncleBed(m, bed);
            if (u.uncleGoneForever || u.uncleAtHome) her = "not on the trip";
            else if (trig == null) her = "no bed trigger for her in this room, she stays where she is";
            else her = D(d => d.DebugToBed(trig));
            return "sleeping in room " + (m.roomNumber + 1) + ", morning in ~14 s; her: " + her + (eve != null ? "; " + eve : "") + (kept ? "; this town is kept" : "");
        }

        // ------------------------------------------------------------ night, triggers
        static string NightDrive()
        {
            string t = SetTime(23);
            var l = Life.Current;
            if (l == null || !l.Enabled) return t + "; her life is off (Lively), no doze";
            if (!Doze.Enabled) return t + "; the Doze toggle is off in the settings";
            l.DozeDebugPrime();
            return t + "; drive above 20 km/h: she yawns within ~3 s";
        }

        static string Thunder()
        {
            RainLightningC live = null;
            foreach (var r in FindObjectsOfType<RainLightningC>())
                if (r.isActiveAndEnabled && r.thunder != null && r.thunder.Length > 0) { live = r; break; }
            if (live != null)
            {
                live.CancelInvoke("Strike");
                live.Strike();   // the game's strike: sound + the next one rolled; the mod's postfix makes her react
                return "the game's thunder (it is raining)";
            }
            AudioClip clip = null;
            foreach (var r in Resources.FindObjectsOfTypeAll<RainLightningC>())
                if (r != null && r.thunder != null && r.thunder.Length > 0 && r.thunder[0] != null) { clip = r.thunder[UnityEngine.Random.Range(0, r.thunder.Length)] ?? r.thunder[0]; break; }
            if (clip != null && Camera.main != null) AudioSource.PlayClipAtPoint(clip, Camera.main.transform.position + Vector3.up * 20f, 1f);
            if (Life.Current != null && Life.Current.Enabled) Life.Current.OnThunder();
            return "no rain now: a thunder sound" + (clip != null ? "" : " (none found)") + " and her reaction";
        }

        static string Pothole()
        {
            var cl = Car();
            var cp = cl != null ? cl.GetComponent<CarPerformanceC>() : null;
            if (cp == null) return "no car";
            cp.PotHoleHitFLWheel();   // the game's hit: tyre wear above 25 km/h; the mod's postfix wakes her if she dozes
            var cs = CarRoot() != null ? CarRoot().GetComponent<CarControleScriptC>() : null;
            float v = cs != null ? Mathf.Abs(cs.currentSpeed) : 0f;
            return "front left wheel hit" + (v > 25f ? "" : " (only counts above 25 km/h, now " + v.ToString("0") + ")");
        }

        static string Crash()
        {
            var u = Uncle();
            if (u == null) return "no uncle";
            u.StartCoroutine("CarCollisionLight");   // the game's light-crash line; the mod's postfix: reaction, CrashSinceMorning
            Morning.CrashSinceMorning = true;
            return "light crash event (wake_crash next morning)";
        }

        static string RouteTalk()
        {
            var u = Uncle();
            var rg = Route();
            if (u == null || rg == null) return "not in the game world";
            if (rg.drivingTowardsHome) return "the game has route talks only heading east";
            u.RouteDialogueChecks();
            return "route talk of level " + rg.routeLevel + " (step " + u.routeDialogueLevel + ")";
        }

        static string SpawnRoadsideCrate()
        {
            var route = RouteGeneratorC.Global;
            var car = Car();
            if (car == null || route == null || route.cratePrefabs == null || route.cratePrefabs.Length == 0)
                return "Roadside crates are unavailable here.";
            var prefabs = Array.FindAll(route.cratePrefabs, p => p != null &&
                p.GetComponent<BoxContentsC>() != null && p.GetComponent<ObjectPickupC>() != null &&
                p.GetComponent<Rigidbody>() != null);
            if (prefabs.Length == 0) return "No pickup crate prefab available.";

            // CarLogic is a logic object. Use the actual seat for position and the boot for orientation.
            if (car.playerSeat == null || car.bootInventory == null) return "Car seat or boot unavailable.";
            Vector3 seat = car.playerSeat.transform.position;
            Vector3 forward = Vector3.ProjectOnPlane(seat - car.bootInventory.transform.position, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.5f) forward = Vector3.forward;
            Vector3 side = Vector3.Cross(Vector3.up, forward);
            for (int i = 0; i < 24; i++)
            {
                float angle = (i % 8) * 45f;
                float radius = 4f + 3f * (i / 8);
                Vector3 point = seat + Quaternion.AngleAxis(angle, Vector3.up) * side * radius;
                RaycastHit hit;
                if (!UnityEngine.Physics.Raycast(point + Vector3.up * 5f, Vector3.down, out hit, 24f, ~0,
                    QueryTriggerInteraction.Ignore) || hit.normal.y < 0.55f ||
                    hit.collider.GetComponentInParent<ObjectPickupC>() != null ||
                    hit.collider.attachedRigidbody != null) continue;
                Vector3 spawn = hit.point + Vector3.up * 2f;
                if (UnityEngine.Physics.CheckSphere(spawn, 0.75f, ~0, QueryTriggerInteraction.Ignore)) continue;
                var box = UnityEngine.Object.Instantiate(prefabs[UnityEngine.Random.Range(0, prefabs.Length)],
                    spawn, car.transform.rotation);
                box.GetComponent<ObjectPickupC>().isInGarbageCollection = route.cratesToBeCleanedUp.Count;
                route.cratesToBeCleanedUp.Add(box);
                return "Roadside crate spawned beside the car.";
            }
            return "No clear ground beside the car.";
        }

        static string Outing()
        {
            var d = Drv();
            if (d == null) return "she is not here";
            Doze.OnG();
            return d.OutingKey();
        }

        static string RadioBob()
        {
            var cl = Car();
            string r;
            if (cl != null && !cl.radioOn)
            {
                var radio = FindObjectOfType<RadioFreqLogicC>();
                if (radio == null) return "no radio in the car";
                radio.StartCoroutine(radio.Trigger());   // the player's click: battery check, knob, sound
                r = "radio switched on";
            }
            else r = "radio already on";
            var l = Life.Current;
            if (l == null || !l.Enabled) return r + "; her life is off (Lively)";
            l.BobDebugNow();
            return r + "; head bob burst as soon as the music plays" + (Life.BobShare <= 0.005f ? " (slider HeadBob is 0: never)" : "");
        }

        // ------------------------------------------------------------ time (JaLoader ModHelper.TimeSet, DNC_DayNight)
        static UnityEngine.Object dnc;
        static System.Reflection.FieldInfo dncHours;

        static float Hours()
        {
            try
            {
                if (dnc == null)
                {
                    Type type = null;
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                        if ((type = a.GetType("DNC_DayNight", false)) != null) break;
                    if (type == null) return -1f;
                    dnc = FindObjectOfType(type);
                    dncHours = type.GetField("timeInHours");
                }
                return dnc != null && dncHours != null ? Convert.ToSingle(dncHours.GetValue(dnc)) : -1f;
            }
            catch (Exception) { return -1f; }
        }

        static string SetTime(int hour, int minute = 0)
        {
            var mh = JaLoader.ModHelper.Instance;
            if (mh == null) return "no JaLoader ModHelper";
            mh.TimeSet(hour, minute);
            return "time " + hour + ":" + minute.ToString("00");
        }

        static string AddHour()
        {
            float h = Hours();
            if (h < 0f) return "time unknown";
            int hh = Mathf.FloorToInt(h), mm = Mathf.Clamp(Mathf.FloorToInt((h - hh) * 60f), 0, 59);
            return SetTime((hh + 1) % 24, mm);
        }

        // ------------------------------------------------------------ status
        void RefreshStatus()
        {
            statusAt = Time.unscaledTime + 0.5f;
            try
            {
                float h = Hours();
                string time = h >= 0f ? Mathf.FloorToInt(h).ToString("00") + ":" + Mathf.FloorToInt((h - Mathf.Floor(h)) * 60f).ToString("00") : "?";
                var rg = Route();
                string lv = rg != null ? rg.routeLevel + (rg.drivingTowardsHome ? " home" : " east") : "?";
                var d = Drv();
                string her = d != null ? d.DebugState() : "-";
                string luck = !Luck.Mine ? "n/a (not this character)"
                    : (Luck.Pending ? "pending" : Luck.Level.ToString("+0;-0;0")) + " (day " + Luck.Day + ")";
                status = "v" + (Owner != null ? Owner.ModVersion : "?") + "  time " + time + "  route " + lv + "  her: " + her + "  luck " + luck + "  radio: " + UmaFm.Status() + ", " + News.Status();
                string ll = lastLog.Length > 80 ? lastLog.Substring(0, 80) + "..." : lastLog;
                string jr = JapanRoad.Status();
                status2 = (jr.Length > 0 ? jr + "  " : "") + "last: " + ll;
            }
            catch (Exception e) { status = "status failed: " + e.Message; }
        }
    }

#endif

    public partial class CompanionDriver
    {
        /// <summary>Debug panel status: seat / bed / morning / outing state, plus the doze phase.</summary>
        public string DebugState()
        {
            var u = OutU();
            if (u == null) return "-";
            string s;
            if (u.uncleGoneForever) s = "gone";
            else if (u.uncleAtHome) s = "home";
            else if (animator != null && animator.GetBool("bedTime")) s = "bed";
            else if (mornPhase != MPhase.Off) s = "morning";
            else if (outState != Out.InCar) s = outState.ToString().ToLower();
            else if (InCar()) s = "seat";
            else s = "standing";
            if (following) s += " +follow";
            if (Doze.Phase != DozePhase.Awake) s += ", doze " + Doze.Phase.ToString().ToLower();
            return s;
        }

        /// <summary>Debug: she is put in her seat at once (outing, morning in the room, bed, follow), as every motel morning.</summary>
        public string DebugSeat(string why)
        {
            var u = OutU();
            if (u == null) return "no uncle";
            if (u.uncleGoneForever || u.uncleAtHome || u.debugStopUncleWorking) return "not on the trip";
            if (following) ToggleFollow();
            if (InCar() && outState == Out.InCar && mornPhase == MPhase.Off) return "in her seat";
            MornDone("debug");
            if (animator != null) animator.SetBool("bedTime", false);
            direct = false;
            outState = Out.InCar;
            var ai = GetComponent<AIPath>();
            if (ai != null) ai.target = null;
            u.StartCoroutine(u.UncleEnterCar());
            Debug.Log("[Umamusume companion] Debug: she is put in her seat (" + why + ")");
            return "put in her seat";
        }

        /// <summary>
        /// Debug: out of the seat and onto the room's bed the way the game's bed trigger does it (UncleBedTriggerC:
        /// bedTime, MotelSatOnBed, the Z particles). The suitcase goes to her hand as at UncleToMotelLobby.
        /// </summary>
        public string DebugToBed(UncleBedTriggerC trig)
        {
            var u = OutU();
            if (u == null) return "no uncle";
            if (following) ToggleFollow();
            MornDone("debug");
            transform.parent = null;
            if (animator != null)
            {
                animator.SetBool("enterCar", false);
                animator.SetBool("exitCar", false);
            }
            u.isSat = false;
            u.readyToBoard = false;
            u.walkingTowardsDoor = false;
            u.hasSaidLetsCheckIn = true;   // the evening check-in: the morning code lets the bed trigger work
            direct = false;
            outState = Out.InCar;
            if (u.suitcase != null && u.uncleRightHand != null)
            {
                u.suitcase.transform.parent = u.uncleRightHand;
                u.suitcase.transform.localPosition = new Vector3(-0.334f, 0.006f, 0.08f);
                u.suitcase.transform.localEulerAngles = new Vector3(-14.05f, -137.48f, 86.08f);
            }
            var col = GetComponent<Collider>();
            if (col == null) return "no collider on the uncle";
            trig.SendMessage("OnTriggerEnter", col, SendMessageOptions.DontRequireReceiver);
            Debug.Log("[Umamusume companion] Debug: she is put on the room's bed (" + trig.name + ")");
            return "on the room's bed";
        }
    }
}
