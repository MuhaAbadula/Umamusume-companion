using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace UmamusumeCompanion
{
    internal sealed class CompanionPicker : MonoBehaviour
    {
        const int RosterColumns = 2;
        const int VisibleRows = 3;
        const int RowStride = 104;
        const int RosterWidth = 928;

        static readonly Color Backdrop = new Color32(25, 31, 29, 255);
        static readonly Color Card = new Color32(45, 55, 50, 255);
        static readonly Color SelectedCard = new Color32(75, 86, 67, 255);
        static readonly Color Ink = new Color32(248, 238, 215, 255);
        static readonly Color MutedInk = new Color32(199, 204, 187, 255);
        static readonly Color Accent = new Color32(238, 191, 102, 255);

        static CompanionMod mod;
        static CompanionPicker current;
        static bool registered;

        MainMenuBookC book;
        MenuMouseInteractionsC menuInput;
        GameObject loaderSplash;
        Button menuButton;
        Text menuLabel;
        RectTransform overlay;
        Button[] choices;
        Image[] selectionMarks;
        Text status;
        Font font;
        int candidate;
        int closedFrame = -1;
        bool required;

        internal static void Register(CompanionMod owner)
        {
            mod = owner;
            if (!registered)
            {
                // Jalopy's NGUI and 3D menu both read Input independently of Unity UI raycasts.
                var harmony = new Harmony("osobl.jalopy.umamusumecompanion.picker");
                var pointer = new HarmonyMethod(typeof(CompanionPicker), nameof(AllowMenuPointer));
                harmony.Patch(AccessTools.Method(typeof(UICamera), "Update"), prefix: pointer);
                harmony.Patch(AccessTools.Method(typeof(MenuMouseInteractionsC), "Update"), prefix: pointer);
                harmony.Patch(AccessTools.Method(typeof(MainMenuBookC), "Update"),
                    prefix: new HarmonyMethod(typeof(CompanionPicker), nameof(AllowBookInput)));
                SceneManager.sceneLoaded += OnSceneLoaded;
                registered = true;
            }
            OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.buildIndex != 1 || current != null || mod == null) return;
            var root = new GameObject("Umamusume Companion Menu", typeof(RectTransform),
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            SceneManager.MoveGameObjectToScene(root, scene);
            current = root.AddComponent<CompanionPicker>();
        }

        static bool AllowBookInput()
        {
            return current == null || (current.overlay == null && current.closedFrame != Time.frameCount);
        }

        static bool AllowMenuPointer()
        {
            if (!AllowBookInput()) return false;
            var button = current != null ? current.menuButton : null;
            return button == null || !button.gameObject.activeInHierarchy ||
                !RectTransformUtility.RectangleContainsScreenPoint((RectTransform)button.transform, Input.mousePosition);
        }

        void Start()
        {
            book = FindObjectOfType<MainMenuBookC>();
            menuInput = FindObjectOfType<MenuMouseInteractionsC>();
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            var canvas = GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1120, 620);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            loaderSplash = GameObject.Find("JaLoader Loading Screen Canvas");

            menuButton = MakeButton(transform, "Choose Companion", new Vector2(1, 1),
                new Vector2(-248, -72), new Vector2(224, 46), GameText.Choose("Companion", "Спутница"), Open);
            menuLabel = menuButton.GetComponentInChildren<Text>();
            menuButton.gameObject.SetActive(false);
        }

        void Update()
        {
            if (menuButton == null) return;
            bool enabled = mod != null && JaLoader.ModManager.IsModEnabled(mod.ModAuthor, mod.ModID, mod.ModName);
            if (overlay != null)
            {
                if (!enabled || (!required && Input.GetKeyDown(KeyCode.Escape))) Close();
                return;
            }

            bool available = enabled && MenuIsVisible();
            menuButton.gameObject.SetActive(available);
            if (!available) return;
            menuLabel.text = GameText.Choose("Companion", "Спутница");
            if (mod.NeedsCompanionChoice) Open();
        }

        bool MenuIsVisible()
        {
            if (loaderSplash != null || EventSystem.current == null || book == null ||
                (menuInput != null && menuInput.restrictRay) ||
                (book.splashImage != null && book.splashImage.color.a > 0.05f)) return false;
            return !AnyVisible(book.optionsBackdrops) &&
                !AnyVisible(book.newGameTexts) && !AnyVisible(book.exitGameTexts);
        }

        static bool AnyVisible(GameObject[] items)
        {
            foreach (var item in items)
                if (item != null && item.activeInHierarchy) return true;
            return false;
        }

        void Open()
        {
            if (overlay != null || mod == null) return;
            required = mod.NeedsCompanionChoice;
            menuButton.gameObject.SetActive(false);
            overlay = Rect("Companion Picker", transform, Vector2.zero, Vector2.one);
            overlay.gameObject.AddComponent<Image>().color = Backdrop;
            var page = Place("Page", overlay, new Vector2(0.5f, 0.5f), new Vector2(-464, -258), new Vector2(928, 516));

            Label(Place("Title", page, Vector2.zero, new Vector2(0, 445), new Vector2(928, 70)),
                GameText.Choose("Choose your companion", "Выберите спутницу"), 48, Ink, FontStyle.Bold);
            string help = mod.ModelCount > 0
                ? GameText.Choose("Your choice applies when you start or continue a trip.",
                    "Спутница присоединится, когда вы начнёте или продолжите поездку.")
                : GameText.Choose("No companion packs installed.", "Спутницы не установлены.");
            bool scrollable = mod.ModelCount > RosterColumns * VisibleRows;
            Label(Place("Help", page, Vector2.zero, new Vector2(0, 402), new Vector2(RosterWidth, 37)),
                help, 21, MutedInk);

            candidate = mod.ModelCount > 0 ? mod.SelectedModel : -1;
            choices = new Button[mod.ModelCount];
            selectionMarks = new Image[choices.Length];
            if (choices.Length > 0) BuildRoster(page, scrollable);

            status = Label(Place("Status", page, Vector2.zero, new Vector2(0, 0), new Vector2(460, 58)), "", 18, Accent);
            Button close = null;
            if (!required)
            {
                close = MakeButton(page, "Back", Vector2.zero, new Vector2(500, 0), new Vector2(190, 58),
                    GameText.Choose("Back", "Назад"), Close);
            }
            if (choices.Length > 0)
            {
                var confirm = MakeButton(page, "Confirm", Vector2.zero, new Vector2(706, 0), new Vector2(222, 58),
                    GameText.Choose("Confirm", "Подтвердить"), Confirm);
                confirm.GetComponent<Image>().color = Accent;
                confirm.GetComponentInChildren<Text>().color = Backdrop;
                Select(candidate);
                choices[candidate].Select();
            }
            else if (close != null) close.Select();
        }

        void BuildRoster(RectTransform page, bool scrollable)
        {
            const int gap = 16;
            const int scrollbarWidth = 24;
            const int rosterHeight = VisibleRows * RowStride;
            float viewportWidth = scrollable ? RosterWidth - gap - scrollbarWidth : RosterWidth;
            float cardWidth = (viewportWidth - gap) / RosterColumns;
            var roster = Place("Roster", page, Vector2.zero, new Vector2(0, 76), new Vector2(RosterWidth, rosterHeight));
            var viewport = Place("Viewport", roster, Vector2.zero, Vector2.zero, new Vector2(viewportWidth, rosterHeight));
            viewport.gameObject.AddComponent<Image>().color = Color.clear;
            viewport.gameObject.AddComponent<RectMask2D>();
            var scroll = roster.gameObject.AddComponent<ScrollRect>();
            int rows = (choices.Length + RosterColumns - 1) / RosterColumns;
            float contentHeight = rows * RowStride;
            var content = Place("Packs", viewport, new Vector2(0, 1),
                new Vector2(0, -contentHeight), new Vector2(viewportWidth, contentHeight));
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = scrollable;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 38;
            if (scrollable)
                scroll.verticalScrollbar = MakeScrollbar(roster, new Vector2(viewportWidth + gap, 0),
                    new Vector2(scrollbarWidth, rosterHeight));

            for (int i = 0; i < choices.Length; i++)
            {
                int index = i;
                var info = mod.ReadModelInfo(i);
                string englishName = info.Get("name_en", mod.ModelId(i));
                string name = GameText.Russian ? info.Get("name", englishName) : englishName;
                var card = MakeButton(content, "Pack " + mod.ModelId(i), new Vector2(0, 1),
                    new Vector2((i % RosterColumns) * (cardWidth + gap), -(i / RosterColumns) * RowStride - 91),
                    new Vector2(cardWidth, 91),
                    name, () => Select(index));
                var title = card.GetComponentInChildren<Text>();
                title.alignment = TextAnchor.MiddleLeft;
                title.fontSize = 25;
                title.rectTransform.offsetMin = new Vector2(23, 39);
                title.rectTransform.offsetMax = new Vector2(-22, -10);
                Label(Place("Feature", card.transform, Vector2.zero, new Vector2(23, 9), new Vector2(cardWidth - 46, 29)),
                    PackFeature(info), 17, MutedInk);
                var mark = Place("Selection", card.transform, Vector2.zero, Vector2.zero, new Vector2(5, 91)).gameObject.AddComponent<Image>();
                mark.color = Accent;
                mark.raycastTarget = false;
                selectionMarks[i] = mark;
                choices[i] = card;
            }

            if (scrollable)
            {
                int firstRow = Mathf.Max(0, candidate / RosterColumns - VisibleRows + 1);
                Canvas.ForceUpdateCanvases();
                scroll.verticalNormalizedPosition = 1f - firstRow / (float)(rows - VisibleRows);
            }
        }

        static Scrollbar MakeScrollbar(Transform parent, Vector2 position, Vector2 size)
        {
            var track = Place("Companion Scrollbar", parent, Vector2.zero, position, size);
            track.gameObject.AddComponent<Image>().color = Card;
            var slidingArea = Rect("Sliding Area", track, Vector2.zero, Vector2.one);
            slidingArea.offsetMin = new Vector2(4, 4);
            slidingArea.offsetMax = new Vector2(-4, -4);
            var handle = Rect("Handle", slidingArea, Vector2.zero, Vector2.one);
            var thumb = handle.gameObject.AddComponent<Image>();
            thumb.color = Accent;

            var scrollbar = track.gameObject.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scrollbar.handleRect = handle;
            scrollbar.targetGraphic = thumb;
            scrollbar.navigation = new Navigation { mode = Navigation.Mode.None };
            return scrollbar;
        }

        static string PackFeature(CharInfo info)
        {
            bool souvenirs = false;
            foreach (string feature in info.Get("features", "fortune").Split(','))
            {
                switch (feature.Trim())
                {
                    case "experiments": return GameText.Choose("Experiments with potions and fuel additives", "Эксперименты с зельями и присадками");
                    case "fortune": return GameText.Choose("Reads your fortune each morning", "Гадание по утрам");
                    case "shopping": return GameText.Choose("Picks up supplies in town", "Покупки к выезду из города");
                    case "pranks": return GameText.Choose("Pranks and radio tricks", "Розыгрыши и проделки с радио");
                    case "boot_snack": return GameText.Choose("Looks for snacks in the boot", "Перекусы из багажника");
                    case "souvenirs": souvenirs = true; break;
                }
            }
            return souvenirs ? GameText.Choose("Collects souvenirs for the dashboard", "Сувениры на приборной панели") : "";
        }

        void Select(int index)
        {
            candidate = index;
            for (int i = 0; i < choices.Length; i++)
            {
                choices[i].GetComponent<Image>().color = i == candidate ? SelectedCard : Card;
                selectionMarks[i].enabled = i == candidate;
            }
            status.text = "";
        }

        void Confirm()
        {
            if (mod.ConfirmCompanion(candidate)) Close();
            else status.text = GameText.Choose("Could not save. Try again.", "Не удалось сохранить выбор. Попробуйте ещё раз.");
        }

        void Close()
        {
            closedFrame = Time.frameCount;
            overlay.gameObject.SetActive(false);
            Destroy(overlay.gameObject);
            overlay = null;
        }

        Button MakeButton(Transform parent, string id, Vector2 anchor, Vector2 position, Vector2 size,
            string label, UnityAction action)
        {
            var rect = Place(id, parent, anchor, position, size);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = Card;
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.normalColor = new Color(0.9f, 0.9f, 0.9f, 1);
            colors.highlightedColor = Color.white;
            button.colors = colors;
            button.onClick.AddListener(action);
            Label(Rect("Label", rect, Vector2.zero, Vector2.one), label, 22, Ink, FontStyle.Bold).alignment = TextAnchor.MiddleCenter;
            return button;
        }

        Text Label(RectTransform rect, string value, int size, Color color, FontStyle style = FontStyle.Normal)
        {
            var label = rect.gameObject.AddComponent<Text>();
            label.font = font;
            label.fontSize = size;
            label.fontStyle = style;
            label.color = color;
            label.alignment = TextAnchor.MiddleLeft;
            label.raycastTarget = false;
            label.supportRichText = false;
            label.text = value;
            return label;
        }

        static RectTransform Place(string name, Transform parent, Vector2 anchor, Vector2 position, Vector2 size)
        {
            var rect = Rect(name, parent, anchor, anchor);
            rect.pivot = Vector2.zero;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return rect;
        }

        static RectTransform Rect(string name, Transform parent, Vector2 min, Vector2 max)
        {
            var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rect.SetParent(parent, false);
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            return rect;
        }
    }
}
