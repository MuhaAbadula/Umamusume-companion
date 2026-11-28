using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using JaLoader;
using UnityEngine;
using UnityEngine.UI;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Replaces the uncle with any character exported by blender/export_char.py.
    /// Packs live in Mods\Assets\UmamusumeCompanion\models\&lt;name&gt;\char.bin (+ textures).
    /// </summary>
    public sealed partial class CompanionMod : Mod
    {
        public override string ModID => CompanionStorage.ModId;
        public override string ModName => "Umamusume companion";
        public override string ModAuthor => "Osobl";
        public override string ModDescription => "Travel with an Umamusume companion. Use the command wheel to ask her to follow, wait, board the car or fetch a crate.";
        public override string ModVersion => "0.55.6";
        public override JaLoader.Common.WhenToInit WhenToInitMod => JaLoader.Common.WhenToInit.InGame;
        public override bool UseAssets => true;

        CompanionDriver driver;
        partial void RegisterDebugSettings();
        partial void StartPanel();
        partial void RefreshPanelSetting();
        partial void UpdateDebugShortcuts(bool ctrl);

        bool ShowOriginalPassenger
        {
            get
            {
#if DEBUG
                return GetToggleValue("ShowUncle");
#else
                return false;
#endif
            }
        }

        internal static bool PanelIsOpen
        {
            get
            {
#if DEBUG
                return DebugPanel.IsOpen;
#else
                return false;
#endif
            }
        }
        string[] models = new string[0];
        int confirmedModel = -1;
        bool confirmingModel;

        string ModelsDir => Path.Combine(AssetsPath, "models");
        internal int ModelCount => models.Length;
        internal bool NeedsCompanionChoice => confirmedModel < 0 && ModelCount > 0;
        internal int SelectedModel => Mathf.Clamp(GetDropdownValue("Model"), 0, ModelCount - 1);

        static string WheelLabel => GameText.Choose("Command wheel (hold)", "Круговое меню команд (удерживать)");
        static string ModelLabel => GameText.Choose("Companion (applies when entering a trip)", "Спутница (сменится при входе в поездку)");
        static string NewsGameLabel => GameText.Choose("News bulletins on the in-game radio", "Новости на радио игры");
        static string NewsUmaLabel => GameText.Choose("Racing news on Uma FM", "Новости скачек на Uma FM");
        static string NewsGapLabel => GameText.Choose("Time between news bulletins (min)", "Перерыв между выпусками, мин");

        public override void SettingsDeclaration()
        {
            CompanionStorage.ImportPreviousSave();
            base.SettingsDeclaration();
            InstantiateSettings();
            AddKeybind("Wheel", WheelLabel, LegacyWheelBinding());
            models = FindModels();
            AddDropdown("Model", ModelLabel, ModelLabels(), 0);
            AddToggle("Physics", "Hair, skirt and tail physics", true);
            AddToggle("Lively", "Facial expressions, gaze and idle animations", true);
            AddToggle("OwnWalk", "Use the companion's own walk and stance", true);
            AddSlider("StepVolume", "Companion footstep volume (%)", 0, 150, 70, true);
            // new id again (WalkPace 80, WalkPace2 60 were saved): 0 = her own Uma pace
            AddSlider("WalkPace3", "Walking speed (% of the uncle's, 0 for her own pace)", 0, 100, 0, true);
            AddSlider("BedLift", "Hip height in the motel bed (cm)", 0, 40, 8, true);
            AddToggle("Doze", "Doze off on long night drives", true);
            AddSlider("DozeMinutes", "Night driving before dozing off (min)", 1, 20, 6, true);
            AddSlider("DozeFrom", "Start of the night (hour)", 0, 23, 22, true);
            AddSlider("DozeTo", "End of the night (hour)", 0, 23, 5, true);
            AddToggle("Morning", "Wake up with your companion at motels", true);
            AddToggle("Fortune", "Fukukitaru reads your fortune each morning", true);
            AddSlider("Luck1", "Chance of great luck (%)", 0, 100, 10, true);
            AddSlider("Luck2", "Chance of good luck (%)", 0, 100, 25, true);
            AddSlider("Luck3", "Chance of average luck (%)", 0, 100, 30, true);
            AddSlider("Luck4", "Chance of a little luck (%)", 0, 100, 25, true);
            AddSlider("Luck5", "Chance of bad luck (%)", 0, 100, 10, true);
            AddToggle("LuckFx", "Fukukitaru's fortune affects the trip", true);
            AddToggle("LuckShop", "Luck affects shop prices", true);
            AddToggle("LuckCrates", "Luck affects crate resale prices", true);
            AddToggle("LuckWear", "Luck affects part wear", true);
            AddToggle("LuckBorder", "Luck affects the chance of a border search", true);
            AddSlider("LuckStrength", "Luck effect strength (%, 0 for base game behaviour)", 0, 200, 100, true);
            AddToggle("Pranks", "Sweep: pranks and radio tricks", true);
            AddSlider("PrankMinutes", "Driving between Sweep's pranks (min, 0 for off)", 0, 30, 12, true);
            AddToggle("Experiments", "Tachyon: experiments (potions for you at stops, additives for the Laika)", true);   // task 87
            AddToggle("TrustGrows", "Narita: trust grows (off = warm at once, stage 5)", true);
            AddSlider("NaritaDistract", "Narita: how often she looks up from the console (%)", 0, 200, 100, true);
            AddSlider("NaritaPhone", "Narita: phone height over her hands (cm, 5 = resting on her fingers, less = deeper in the hands)", 0, 15, 5, true);
            AddSlider("NaritaChat", "Narita: how often she talks at stage 5 (%)", 0, 200, 100, true);
            AddSlider("PotionMinutes", "Time between Tachyon's potions (min, 0 for off)", 0, 60, 30, true);
            AddSlider("AddonMinutes", "Driving between Tachyon's additives (min, 0 for off)", 0, 60, 35, true);
            AddToggle("ExpStrong", "Intense camera effects for Tachyon's experiments", true);
            AddDropdown("MirrorQuality", "Rear-view mirror quality", new[] { "High", "Game default" }, 0);
            AddToggle("Souvenirs", "Dashboard souvenirs (click one to hear about it)", true);
            AddToggle("StayIstanbul", "Bring your companion home from Istanbul", true);
            AddSlider("ReturnTalkGap", "Driving between conversations on the way home (min)", 2, 10, 4, true);   // task 60
            AddToggle("HideSuitcase", "Hide the uncle's suitcase when carried", true);
            AddToggle("UmaFm", "Uma FM station (click the radio again to tune in)", true);
            AddToggle("NewsGame", NewsGameLabel, true);
            AddToggle("NewsUma", NewsUmaLabel, true);
            AddSlider("NewsGap", NewsGapLabel, 1, 30, 8, true);
            AddSlider("HeadBob", "Time spent nodding along to the radio (%)", 0, 100, 30, true);
            AddSlider("SelfActDelay", "Delay before using the radio or window after getting in (s)", 0, 300, 90, true);
            AddSlider("Activity", "Companion activity frequency (%)", 30, 200, 80, true);
            AddSlider("Wind", "Wind strength with the passenger window open (%)", 0, 200, 100, true);
            AddToggle("BagSeat", "Put the backpack on the back seat", true);
            AddSlider("BagBack", "Backpack distance behind the companion (cm)", 30, 250, 150, true);
            AddSlider("BagHeight", "Backpack height (cm, 50 at hip level)", 0, 200, 80, true);
            AddSlider("BagSide", "Backpack sideways position (cm, 50 centred)", 0, 100, 50, true);
            AddSlider("Brightness", "Brightness (game objects use about 50)", 20, 100, 55, true);
            AddSlider("SeatLift", "Sitting height in the car (cm)", 0, 40, 18, true);
            AddSlider("ArmSpread", "Arm angle when standing (degrees)", 0, 30, 12, true);
            AddSlider("Posture", "How much to straighten her posture (%)", 0, 100, 60, true);
            RegisterDebugSettings();
            AddKeybind("FetchCrate", "Fetch the nearest roadside crate (while stopped)", KeyCode.J);
            AddKeybind("Outing", "Get out of the parked car or switch between waiting and following", KeyCode.G);
            // task 47: the new-game reset must hook the main menu's New Game, before any game scene
            try { Souvenirs.Apply(); }
            catch (Exception e) { JaLoader.Console.LogError(ModID, "Souvenir patch failed: " + e.Message); }
            try { News.Apply(); }   // task 52: heard news reset on a new game / return home
            catch (Exception e) { JaLoader.Console.LogError(ModID, "News patch failed: " + e.Message); }
        }

        public override void Update()
        {
            base.Update();
            UpdateWheel();
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (!ctrl && driver != null && Input.GetKeyDown(GetPrimaryKeybind("FetchCrate")))
                JaLoader.Console.Log(ModID, driver.FetchKey());
            if (!ctrl && Input.GetKeyDown(GetPrimaryKeybind("Outing")) && driver != null)
            {
                Doze.OnG();   // asleep: G wakes her first, her line goes before the outing remark
                JaLoader.Console.Log(ModID, driver.OutingKey());
            }
            UpdateDebugShortcuts(ctrl);
        }

#if DEBUG
        /// <summary>
        /// Debug: every route on the map becomes 1 road segment long (no stop-offs), so after picking a route you
        /// drive one segment and arrive at the next town in the normal way (motel, check-in, bed).
        /// Works for routes not picked yet; the game re-rolls distances after the next sleep.
        /// </summary>
        internal void ShortTrip()
        {
            try
            {
                ShortTripPatch.Apply();
                ShortTripPatch.Armed = !ShortTripPatch.Armed;
                var director = GameObject.FindWithTag("Director");
                var rg = director != null ? director.GetComponent<RouteGeneratorC>() : null;
                if (rg != null && ShortTripPatch.Armed)
                {
                    // the route builder also reads these (stop-offs, segment props); the trip length itself is
                    // swapped in when a route is picked (ShortTripPatch)
                    rg.route1Distance = rg.route2Distance = rg.route3Distance = rg.route4Distance = 1;
                    rg.route1StopOffPoint = rg.route2StopOffPoint = rg.route3StopOffPoint = 0;
                }
                JaLoader.Console.Log(ModID, ShortTripPatch.Armed
                    ? "Short trip ON: the next route you pick on the map is one segment to the next town."
                    : "Short trip OFF: routes have their normal length again.");
                if (ShortTripPatch.Armed) GiveFastestEngine();   // same key (user): a quick trip with the best engine
            }
            catch (Exception e)
            {
                JaLoader.Console.LogError(ModID, "Short trip failed: " + e);
            }
        }

        /// <summary>
        /// Debug: the engine block with the highest top speed in the game (then acceleration), new, in front of you.
        /// Engine blocks = EngineComponentC whose ObjectPickupC.engineString is "EngineBlock"; prefabs are preferred
        /// (scene copies may be installed or worn). Spawned the way the Laika catalogue spawns an order.
        /// </summary>
        void GiveFastestEngine()
        {
            try
            {
                EngineComponentC best = null;
                bool bestPrefab = false;
                foreach (var e in Resources.FindObjectsOfTypeAll<EngineComponentC>())
                {
                    var p = e != null ? e.GetComponent<ObjectPickupC>() : null;
                    if (p == null || p.engineString != "EngineBlock") continue;
                    bool prefab = !e.gameObject.scene.IsValid();
                    if (!prefab && (p.isInEngine || !e.gameObject.activeInHierarchy)) continue;
                    if (best == null || (prefab && !bestPrefab)
                        || (prefab == bestPrefab && (e.topSpeed > best.topSpeed || (e.topSpeed == best.topSpeed && e.acceleration > best.acceleration))))
                    { best = e; bestPrefab = prefab; }
                }
                var cam = Camera.main;
                if (best == null || cam == null) { JaLoader.Console.LogError(ModID, "Engine: no engine block found."); return; }
                var pos = cam.transform.position + cam.transform.forward * 1.5f + Vector3.up * 0.3f;
                var go = (GameObject)Instantiate(best.gameObject, pos, Quaternion.identity);
                go.name = best.gameObject.name;
                go.SetActive(true);
                var pk = go.GetComponent<ObjectPickupC>();
                if (pk != null)
                {
                    pk.isPurchased = true;
                    pk.forceCollision = true;
                    pk.isInEngine = false;
                    go.transform.eulerAngles = pk.throwRotAdjustment;
                }
                var col = go.GetComponent<Collider>();
                if (col != null) col.isTrigger = false;
                var rb = go.GetComponent<Rigidbody>();
                if (rb != null) rb.isKinematic = false;
                var ec = go.GetComponent<EngineComponentC>();
                ec.Condition = ec.durability;
                JaLoader.Console.Log(ModID, "Engine: " + go.name + " (top speed " + ec.topSpeed + ", acceleration " + ec.acceleration
                    + (bestPrefab ? "" : ", copied from the world") + ") is in front of you.");
            }
            catch (Exception e)
            {
                JaLoader.Console.LogError(ModID, "Engine failed: " + e);
            }
        }

        /// <summary>
        /// Debug shortcut = what sleeping in a bed does: mark the tutorial done, advance routeLevel by one
        /// (0 home -> 1 Dresden ... 6 Istanbul), then MainMenuC.SaveData(1), which saves and reloads the level.
        /// On load the game wakes you in that town's motel with the car parked and the uncle next to you.
        /// The game backs up the old save to Saves\Backups before writing.
        /// </summary>
        internal void JumpToNextTown()
        {
            try
            {
                var mm = MainMenuC.Global;
                var director = GameObject.FindWithTag("Director");
                var rg = director != null ? director.GetComponent<RouteGeneratorC>() : null;
                if (mm == null || rg == null)
                {
                    JaLoader.Console.LogError(ModID, "Jump: not in the game world.");
                    return;
                }
                if (rg.routeLevel >= 6)
                {
                    JaLoader.Console.Log(ModID, "Jump: already in Istanbul, the last town.");
                    return;
                }
                var uncle = FindObjectOfType<UncleLogicC>();
                if (uncle != null)
                {
                    var s1 = uncle.GetComponent<Scene1_LogicC>();
                    if (s1 != null) s1.hasGivenTutorial = true;
                }
                rg.drivingTowardsHome = false;
                rg.routeLevel++;
                JaLoader.Console.Log(ModID, "Jump: sleeping until the motel of town #" + rg.routeLevel + "...");
                mm.SaveData(1);
            }
            catch (Exception e)
            {
                JaLoader.Console.LogError(ModID, "Jump failed: " + e);
            }
        }

#endif

        string[] FindModels()
        {
            var list = new List<string>();
            try
            {
                if (Directory.Exists(ModelsDir))
                    foreach (var dir in Directory.GetDirectories(ModelsDir))
                        if (File.Exists(Path.Combine(dir, "char.bin"))) list.Add(Path.GetFileName(dir));
            }
            catch (Exception e)
            {
                JaLoader.Console.LogError(ModID, "Can't list models: " + e.Message);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            // JaLoader stores indices. Append later additions in their original release order.
            foreach (string id in new[] { "oguricap", "specialweek", "silencesuzuka" })
            {
                int index = list.FindIndex(name => string.Equals(name, id, StringComparison.OrdinalIgnoreCase));
                if (index < 0) continue;
                string pack = list[index];
                list.RemoveAt(index);
                list.Add(pack);
            }
            return list.ToArray();
        }

        string[] ModelLabels()
        {
            if (models.Length == 0)
                return new[] { GameText.Choose("No companion packs found", "Спутницы не установлены") };
            var labels = new string[models.Length];
            for (int i = 0; i < models.Length; i++)
            {
                var info = ReadModelInfo(i);
                string englishName = info.Get("name_en", models[i]);
                labels[i] = GameText.Russian ? info.Get("name", englishName) : englishName;
            }
            return labels;
        }

        void ChangedLanguage(LanguageCode language)
        {
            if (UIManager.Instance == null) return;
            var model = GetDropdown("Model");
            if (model == null) return;
            var names = ModelLabels();
            if (model.options.Count == names.Length)
            {
                for (int i = 0; i < names.Length; i++)
                    model.options[i].text = ModelLabel + ": " + names[i];
                model.RefreshShownValue();
            }

            var modelRoot = model.transform;
            while (modelRoot != null && modelRoot.name != "Model_Dropdown") modelRoot = modelRoot.parent;
            if (modelRoot == null || modelRoot.parent == null) return;
            var holder = modelRoot.parent;
            var wheel = holder.Find("Wheel_Keybind/HeaderText")?.GetComponent<Text>();
            if (wheel != null) wheel.text = WheelLabel;
            var gap = holder.Find("NewsGap_Slider")?.GetComponentInChildren<Text>();
            if (gap != null) gap.text = NewsGapLabel;
            RefreshToggleLabel(GetToggle("NewsGame"), NewsGameLabel);
            RefreshToggleLabel(GetToggle("NewsUma"), NewsUmaLabel);
        }

        static void RefreshToggleLabel(Dropdown toggle, string label)
        {
            if (toggle == null || toggle.options.Count != 2) return;
            toggle.options[0].text = label + ": On";
            toggle.options[1].text = label + ": Off";
            toggle.RefreshShownValue();
        }

        internal CharInfo ReadModelInfo(int index) => CharInfo.Load(Path.Combine(ModelsDir, models[index]));
        internal string ModelId(int index) => models[index];

        public override void OnSettingsLoaded()
        {
            base.OnSettingsLoaded();
            key = GetPrimaryKeybind("Wheel");
            string pack = CompanionStorage.ReadSelectedPack();
            confirmedModel = Array.FindIndex(models, name => string.Equals(name, pack, StringComparison.OrdinalIgnoreCase));
            if (confirmedModel >= 0) GetDropdown("Model").value = confirmedModel;

            try { CompanionPicker.Register(this); }
            catch (Exception e) { JaLoader.Console.LogError(ModID, "Companion menu: " + e); }
        }

        internal bool ConfirmCompanion(int index)
        {
            if (index < 0 || index >= ModelCount) return false;
            var dropdown = GetDropdown("Model");
            int previous = dropdown.value;
            confirmingModel = true;
            try
            {
                dropdown.value = index;
                SaveModSettings();
                if (confirmedModel == index) return true;
            }
            catch (Exception e) { JaLoader.Console.LogError(ModID, "Saving companion: " + e); }
            finally { confirmingModel = false; }

            dropdown.value = previous;
            return false;
        }

        public override void Start()
        {
            base.Start();
            StartWheel();
            try { LuckBorder.Apply(); }
            catch (Exception e) { Debug.LogWarning("[Umamusume companion] LuckFx: Border transpiler not found (" + e.Message + ")"); }
            StartPanel();
            LoadCharInfo();
            Luck.Enabled = GetToggleValue("Fortune") && Luck.Mine;   // no luck effects for another character, even before Setup
            ApplyRoad();
            UmaFm.Dir = Path.Combine(Path.Combine(AssetsPath, "radio"), "umafm");   // task 44
            UmaFm.Enabled = GetToggleValue("UmaFm");
            News.RadioDir = Path.Combine(AssetsPath, "radio");   // task 52
            ApplyNews();
            try { UmaFm.Apply(); }
            catch (Exception e) { JaLoader.Console.LogError(ModID, "Uma FM patch failed: " + e.Message); }
            StartCoroutine(Setup());
        }

        public override void OnReload()
        {
            base.OnReload();
            if (driver == null) StartCoroutine(Setup());
        }

        public override void OnSettingsSaved()
        {
            base.OnSettingsSaved();
            if (ModelCount > 0 && (confirmingModel || confirmedModel >= 0))
            {
                int index = SelectedModel;
                if (index != confirmedModel)
                {
                    CompanionStorage.WriteSelectedPack(models[index]);
                    confirmedModel = index;
                }
            }
            key = GetPrimaryKeybind("Wheel");
            ApplySettings();
        }

        string PickedDir()
        {
            return models.Length > 0 ? Path.Combine(ModelsDir, models[Mathf.Clamp(GetDropdownValue("Model"), 0, models.Length - 1)]) : null;
        }

        CharInfo charInfo;

        /// <summary>Task 30: info.txt of the picked pack -> features (CharFeatures) and height (RoadSpots.HerMetres).</summary>
        void LoadCharInfo()
        {
            string dir = PickedDir();
            charInfo = dir != null ? CharInfo.Load(dir) : new CharInfo();
            CharFeatures.Load(charInfo);
            float h = charInfo.GetFloat("height_m", 1.56f);
            if (h < 0.5f || h > 3f) { Debug.LogWarning("[Umamusume companion] Char: height_m=" + h + " out of 0.5..3, 1.56 used"); h = 1.56f; }
            RoadSpots.HerMetres = h;
        }

        void ApplyNews()
        {
            News.GameOn = GetToggleValue("NewsGame");
            News.UmaOn = GetToggleValue("NewsUma");
            News.GapMinutes = GetSliderValue("NewsGap");
        }

        void ApplyRoad()
        {
            Souvenirs.Enabled = GetToggleValue("Souvenirs");
            if (models.Length > 0)   // props\ of the picked pack (PropMesh: souvenirs)
            {
                string pack = models[SelectedModel];
                Souvenirs.SelectPack(pack);
                PropMesh.Dir = Path.Combine(Path.Combine(ModelsDir, pack), "props");
            }
        }

        void ApplySettings()
        {
            RefreshPanelSetting();
            MirrorQuality.Level = GetDropdownValue("MirrorQuality") == 0 ? 1 : 0;   // task 40 bug 3
            UmaFm.Enabled = GetToggleValue("UmaFm");
            ApplyNews();
            ApplyRoad();
            if (driver == null) return;
            driver.SeatLift = GetSliderValue("SeatLift") / 100f;
            driver.ArmSpreadStand = GetSliderValue("ArmSpread");
            driver.PostureStrength = GetSliderValue("Posture") / 100f;
            driver.Physics = GetToggleValue("Physics");
            driver.Lively = GetToggleValue("Lively");
            driver.Activity = GetSliderValue("Activity") / 100f;
            Life.BobShare = GetSliderValue("HeadBob") / 100f;
            Life.BoardDelay = GetSliderValue("SelfActDelay");
            Life.PranksOn = GetToggleValue("Pranks");
            Life.PrankMinutes = GetSliderValue("PrankMinutes");
            CompanionDriver.ExpOn = GetToggleValue("Experiments");
            Trust.Grows = GetToggleValue("TrustGrows");
            Life.NaritaDistract = GetSliderValue("NaritaDistract") / 100f;
            Life.NaritaChat = GetSliderValue("NaritaChat") / 100f;
            Life.PhoneLiftCm = GetSliderValue("NaritaPhone") - 5f;
            CompanionDriver.PotionMinutes = GetSliderValue("PotionMinutes");
            CompanionDriver.AddonMinutes = GetSliderValue("AddonMinutes");
            ExpFx.StrongOn = GetToggleValue("ExpStrong");
            driver.OwnWalk = GetToggleValue("OwnWalk");
            driver.WalkPace = GetSliderValue("WalkPace3") / 100f;
            driver.StepVolume = GetSliderValue("StepVolume") / 100f;
            driver.HideSuitcase = GetToggleValue("HideSuitcase");
            IstanbulPatch.Stay = GetToggleValue("StayIstanbul");
            Morning.Enabled = GetToggleValue("Morning");
            Luck.Enabled = GetToggleValue("Fortune") && Luck.Mine;
            for (int i = 0; i < 5; i++) Luck.Chances[i] = GetSliderValue("Luck" + (i + 1));
            LuckFx.Enabled = GetToggleValue("LuckFx");
            LuckFx.Shop = GetToggleValue("LuckShop");
            LuckFx.Crates = GetToggleValue("LuckCrates");
            LuckFx.Wear = GetToggleValue("LuckWear");
            LuckFx.Border = GetToggleValue("LuckBorder");
            LuckFx.Strength = GetSliderValue("LuckStrength") / 100f;
            ReturnTrip.GapMinutes = GetSliderValue("ReturnTalkGap");
            Doze.Enabled = GetToggleValue("Doze");
            Doze.Minutes = GetSliderValue("DozeMinutes");
            Doze.NightFrom = Mathf.RoundToInt(GetSliderValue("DozeFrom"));
            Doze.NightTo = Mathf.RoundToInt(GetSliderValue("DozeTo"));
            driver.BedLift = GetSliderValue("BedLift") / 100f;
            driver.WindScale = GetSliderValue("Wind") / 100f;
            driver.SetBackpack(GetToggleValue("BagSeat"), GetSliderValue("BagBack") / 100f,
                (GetSliderValue("BagHeight") - 50f) / 100f, (GetSliderValue("BagSide") - 50f) / 100f);
#if DEBUG
            driver.PhysicsDebug = GetToggleValue("PhysicsDebug");
            driver.WalkDebug = GetToggleValue("PhysicsDebug");
#endif
            driver.SetBrightness(GetSliderValue("Brightness") / 100f);
            driver.SetUncleVisible(ShowOriginalPassenger);
        }

        IEnumerator Setup()
        {
            UncleLogicC uncle = null;
            for (int i = 0; i < 600 && uncle == null; i++)
            {
                uncle = FindObjectOfType<UncleLogicC>();
                if (uncle == null) yield return null;
            }
            if (uncle == null)
            {
                JaLoader.Console.LogError(ModID, "Uncle not found, nothing to replace.");
                yield break;
            }
            if (uncle.GetComponent<CompanionDriver>() != null) yield break;
            if (models.Length == 0)
            {
                JaLoader.Console.LogError(ModID, "No character packs in " + ModelsDir);
                yield break;
            }

            try
            {
                int pick = Mathf.Clamp(GetDropdownValue("Model"), 0, models.Length - 1);
                string dir = Path.Combine(ModelsDir, models[pick]);
                if (charInfo == null) LoadCharInfo();
                var info = charInfo;
                var pack = CharPack.Load(Path.Combine(dir, "char.bin"));
                driver = uncle.gameObject.AddComponent<CompanionDriver>();
                float bs = info.GetFloat("body_scale", 1f);
                if (bs < 0.5f || bs > 1.5f) { Debug.LogWarning("[Umamusume companion] Char: body_scale=" + bs + " out of 0.5..1.5, 1 used"); bs = 1f; }
                driver.BodyScale = bs;
                driver.Build(pack, dir, !ShowOriginalPassenger);
                RoadSpots.HerHeight = driver.HeightUnits;
                ApplySettings();

                NamePatch.Name = info.Get("name", models[pick]);
                NamePatch.NameEn = info.Get("name_en", NamePatch.Name);
                Debug.Log("[Umamusume companion] Char: " + NamePatch.NameEn + " features=" + CharFeatures.List
                    + " height_m=" + RoadSpots.HerMetres.ToString(System.Globalization.CultureInfo.InvariantCulture) + " props=" + Props.Names);
                Debug.Log("[Umamusume companion] Char: " + NamePatch.NameEn + " body_scale " + bs.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
                    + " height " + driver.HeightBind.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " u (hatless), "
                    + driver.HeightUnits.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " in game units");
                try { NamePatch.Apply(); }
                catch (Exception e) { JaLoader.Console.LogError(ModID, "Name patch failed: " + e.Message); }
                try { TypeTextPatch.Apply(); }
                catch (Exception e) { JaLoader.Console.LogError(ModID, "Typewriter patch failed: " + e.Message); }
                try { MirrorQuality.Apply(); }
                catch (Exception e) { JaLoader.Console.LogError(ModID, "Mirror patch failed: " + e.Message); }
                try { ReactPatch.Apply(); }
                catch (Exception e) { JaLoader.Console.LogError(ModID, "Reaction patch failed: " + e.Message); }
                try { Doze.Apply(); }
                catch (Exception e) { JaLoader.Console.LogError(ModID, "Doze patch failed: " + e.Message); }
                try { Morning.Apply(); }
                catch (Exception e) { JaLoader.Console.LogError(ModID, "Morning patch failed: " + e.Message); }
                try { BootPatch.Apply(); }
                catch (Exception e) { JaLoader.Console.LogError(ModID, "Boot patch failed: " + e.Message); }
                try { IstanbulPatch.Apply(); }
                catch (Exception e) { JaLoader.Console.LogError(ModID, "Istanbul patch failed: " + e.Message); }
                try { WalkPatch.Apply(); }
                catch (Exception e) { JaLoader.Console.LogError(ModID, "Walk patch failed: " + e.Message); }
                try
                {
                    int lines = SpeechPatch.Load(dir);
                    if (lines > 0)
                    {
                        SpeechPatch.Apply();
                        JaLoader.Console.Log(ModID, "Dialogue: " + lines + " own lines.");
                    }
                }
                catch (Exception e) { JaLoader.Console.LogError(ModID, "Dialogue patch failed: " + e.Message); }
                driver.SetupVoice(dir, info.GetFloat("voice_pitch", 1f));
                driver.SetupSteps(dir);
                JaLoader.Console.Log(ModID, $"{models[pick]} is here: {driver.Summary}.");
            }
            catch (Exception e)
            {
                JaLoader.Console.LogError(ModID, "Setup failed: " + e);
                Debug.LogException(e);
            }
        }
    }
}
