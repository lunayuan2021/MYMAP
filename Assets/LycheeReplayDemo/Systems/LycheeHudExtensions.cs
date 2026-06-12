using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// HUD 扩展系统：事件流 / 天气横幅 / 回放控制条 / RUSH 转场 / 鲜度警示。
/// 自建独立 Canvas（ScreenSpaceOverlay，sortingOrder=140），不修改现有 LycheeReplayRuntimeHud。
/// 通过 RuntimeInitializeOnLoadMethod 自举挂到 LycheeReplayPlayer 所在 GameObject。
/// </summary>
public sealed class LycheeHudExtensions : MonoBehaviour
{
    /// <summary>设为 true 可整体关闭本扩展（自举前阻止挂载，运行中隐藏 Canvas）。</summary>
    public static bool Disabled;

    const string CanvasName = "LycheeHudExtCanvas";
    const int MaxFeedEntries = 6;
    const float FeedWidth = 332f;
    const float FeedEntryHeight = 36f;
    const float FeedLifetime = 12f;
    const float RushDuration = 2.8f;

    static readonly Color PanelBg = new Color(0.01f, 0.012f, 0.018f, 0.8f);
    static readonly Color GoldTitle = new Color(1f, 0.78f, 0.16f, 1f);
    static readonly Color RedTeamColor = new Color(0.95f, 0.18f, 0.11f, 1f);
    static readonly Color BlueTeamColor = new Color(0.20f, 0.48f, 1f, 1f);
    static readonly Color SoftText = new Color(0.92f, 0.93f, 0.88f, 0.95f);
    static readonly Color AmberBg = new Color(0.46f, 0.30f, 0.05f, 0.88f);

    static readonly float[] SpeedSeconds = { 0.20f, 0.10f, 0.05f, 0.025f };
    static readonly string[] SpeedLabels = { "0.5×", "1×", "2×", "4×" };

    static Font sharedFont;
    static bool sharedFontResolved;
    static Sprite circleSprite;
    static Sprite bandSprite;
    static Sprite vignetteSprite;

    sealed class FeedEntry
    {
        public RectTransform rect;
        public CanvasGroup group;
        public float born;
        public float slideStart;
        public float targetY;
        public bool fading;
        public float fadeStart;
    }

    sealed class PendingFeed
    {
        public string label;
        public Color color;
    }

    LycheeReplayPlayer player;
    Canvas canvas;
    RectTransform canvasRect;

    // ---- 事件流 ----
    RectTransform feedRoot;
    CanvasGroup feedGroup;
    readonly List<FeedEntry> feedEntries = new List<FeedEntry>();
    readonly List<PendingFeed> pendingFeed = new List<PendingFeed>();
    bool feedPaused;

    // ---- 天气横幅 ----
    CanvasGroup weatherGroup;
    Image weatherBg;
    Outline weatherOutline;
    Image weatherIcon;
    Text weatherText;
    float weatherShown;
    bool weatherVisibleTarget;
    bool weatherBreathing;
    Sprite weatherHotSprite;
    Sprite weatherRainSprite;
    Sprite weatherFogSprite;

    // ---- 回放控制条 ----
    CanvasGroup controlGroup;
    Slider seekSlider;
    Text roundLabel;
    Text playButtonLabel;
    Text speedButtonLabel;
    string playGlyph = "播放";
    string pauseGlyph = "暂停";
    bool suppressSlider;
    bool sliderHeld;
    bool playStateCaptured;
    bool wasPlayingBeforeDrag;
    float lastUserSliderChange;
    int speedIndex = 1;

    // ---- RUSH 转场 ----
    RectTransform rushRoot;
    CanvasGroup rushGroup;
    RectTransform rushTopBand;
    RectTransform rushBottomBand;
    RectTransform rushTextRect;
    Coroutine rushRoutine;
    int lastRushRound = int.MinValue;

    // ---- 鲜度警示 ----
    Image leftEdgeBar;
    Image rightEdgeBar;
    Coroutine leftFlashRoutine;
    Coroutine rightFlashRoutine;
    CanvasGroup toastGroup;
    Image toastAccent;
    Text toastText;
    Coroutine toastRoutine;
    readonly Dictionary<int, int> alertRoundByCamp = new Dictionary<int, int>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void LycheeBootstrapHudExtensions()
    {
        if (Disabled) return;
        LycheeReplayPlayer host = FindFirstObjectByType<LycheeReplayPlayer>();
        if (host == null) return;
        if (host.GetComponent<LycheeHudExtensions>() != null) return;
        host.gameObject.AddComponent<LycheeHudExtensions>();
    }

    void Awake()
    {
        player = GetComponent<LycheeReplayPlayer>();
        if (player != null)
        {
            float best = float.MaxValue;
            for (int i = 0; i < SpeedSeconds.Length; i++)
            {
                float diff = Mathf.Abs(player.secondsPerRound - SpeedSeconds[i]);
                if (diff < best)
                {
                    best = diff;
                    speedIndex = i;
                }
            }
        }
        LoadWeatherSprites();
        BuildCanvas();
        EnsureEventSystem();
        if (player != null) RefreshWeatherBanner(player.CurrentFrame);
    }

    void OnEnable()
    {
        LycheeReplayPlayer.ReplayEventRaised += HandleReplayEvent;
        LycheeReplayPlayer.RoundApplied += HandleRoundApplied;
    }

    void OnDisable()
    {
        LycheeReplayPlayer.ReplayEventRaised -= HandleReplayEvent;
        LycheeReplayPlayer.RoundApplied -= HandleRoundApplied;
        // 协程随组件禁用被终止，复位演出状态避免悬挂。
        rushRoutine = null;
        toastRoutine = null;
        leftFlashRoutine = null;
        rightFlashRoutine = null;
        feedPaused = false;
        if (rushGroup != null) rushGroup.alpha = 0f;
        if (rushRoot != null) rushRoot.gameObject.SetActive(false);
    }

    void Start()
    {
        if (controlGroup != null) StartCoroutine(PanelEnterRoutine(controlGroup, 0.05f));
        if (feedGroup != null) StartCoroutine(PanelEnterRoutine(feedGroup, 0.12f));
    }

    void Update()
    {
        if (canvas == null) return;
        bool active = !Disabled;
        if (canvas.enabled != active) canvas.enabled = active;
        if (!active) return;
        float now = Time.unscaledTime;
        UpdateFeed(now);
        UpdateWeatherBanner(now);
        UpdateControls();
    }

    // ================= 事件订阅 =================

    void HandleReplayEvent(LycheeReplayMessage message, int round)
    {
        if (Disabled || message == null || string.IsNullOrEmpty(message.type)) return;
        string type = message.type;
        if (type == "RUSH_PHASE_START") TriggerRushTransition(round);
        if (type == "GOOD_TO_BAD" || type == "FRESHNESS_DROP") HandleFreshnessEvent(message, round);
        PushFeedEvent(message, round);
    }

    void HandleRoundApplied(LycheeReplayFrame previous, LycheeReplayFrame current)
    {
        if (Disabled || current == null) return;
        RefreshWeatherBanner(current);
        if (current.phase == "NORMAL" && current.round < lastRushRound)
            lastRushRound = int.MinValue;
        if (previous != null && current.phase == "RUSH" && previous.phase != "RUSH" && current.round >= previous.round)
            TriggerRushTransition(current.round);
        CheckFreshnessThresholds(previous, current);
    }

    // ================= 构建 =================

    void BuildCanvas()
    {
        GameObject canvasGo = new GameObject(CanvasName, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(transform, false);
        canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 140;
        CanvasScaler scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        canvasRect = canvasGo.GetComponent<RectTransform>();

        BuildFreshnessAlerts(canvasRect);
        BuildFeed(canvasRect);
        BuildWeatherBanner(canvasRect);
        BuildControls(canvasRect);
        BuildRushOverlay(canvasRect);
    }

    static void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null) return;
        GameObject go = new GameObject("LycheeHudExtEventSystem");
        go.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
        go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
        go.AddComponent<StandaloneInputModule>();
#endif
    }

    void BuildFeed(Transform parent)
    {
        feedRoot = MakeRect("EventFeedRoot", parent);
        SetRect(feedRoot, new Vector2(0f, 0.46f), new Vector2(0f, 0.46f), new Vector2(0f, 1f), new Vector2(14f, 0f), new Vector2(FeedWidth, FeedEntryHeight * MaxFeedEntries));
        feedGroup = feedRoot.gameObject.AddComponent<CanvasGroup>();
        feedGroup.alpha = 0f;
        feedGroup.interactable = false;
        feedGroup.blocksRaycasts = false;
    }

    void BuildWeatherBanner(Transform parent)
    {
        RectTransform root = MakeRect("WeatherBanner", parent);
        SetRect(root, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -10f), new Vector2(540f, 42f));
        weatherGroup = root.gameObject.AddComponent<CanvasGroup>();
        weatherGroup.alpha = 0f;
        weatherGroup.interactable = false;
        weatherGroup.blocksRaycasts = false;
        weatherBg = root.gameObject.AddComponent<Image>();
        weatherBg.color = PanelBg;
        weatherBg.raycastTarget = false;
        weatherOutline = root.gameObject.AddComponent<Outline>();
        weatherOutline.effectColor = new Color(1f, 0.78f, 0.16f, 0.9f);
        weatherOutline.effectDistance = new Vector2(1.6f, -1.6f);
        weatherOutline.enabled = false;

        // 图标左缘留 16px 边距；文字区自图标右缘（44）再留 6px 起步，右缘留 16px。
        RectTransform iconRt = MakeRect("WeatherIcon", root);
        SetRect(iconRt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(16f, 0f), new Vector2(28f, 28f));
        weatherIcon = iconRt.gameObject.AddComponent<Image>();
        weatherIcon.preserveAspect = true;
        weatherIcon.raycastTarget = false;
        weatherIcon.enabled = false;

        weatherText = MakeText("WeatherText", root, 18, FontStyle.Bold, new Color(0.96f, 0.97f, 0.92f, 1f), TextAnchor.MiddleCenter);
        SetRect(weatherText.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), new Vector2(17f, 0f), new Vector2(-66f, 0f));
    }

    void BuildControls(Transform parent)
    {
        // 控制条加宽 440->500、加高 84->104：按钮字号提升到 20 后行高 36，
        // 顶部按钮行与底部滑杆行垂直间隔 12px，内容距背景边缘 16px。
        RectTransform panel = MakeRect("ControlPanel", parent);
        SetRect(panel, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-16f, 16f), new Vector2(500f, 104f));
        controlGroup = panel.gameObject.AddComponent<CanvasGroup>();
        controlGroup.alpha = 0f;
        Image bg = panel.gameObject.AddComponent<Image>();
        bg.color = PanelBg;
        bg.raycastTarget = true;

        RectTransform line = MakeRect("TopLine", panel);
        SetRect(line, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 2f));
        Image lineImg = line.gameObject.AddComponent<Image>();
        lineImg.color = new Color(1f, 0.77f, 0.27f, 0.86f);
        lineImg.raycastTarget = false;

        Font font = GetSharedFont();
        string replayLabel = HasGlyph(font, '⟲') ? "⟲ 重播" : "重播";
        playGlyph = HasGlyph(font, '▶') ? "▶" : "播放";
        pauseGlyph = HasGlyph(font, '⏸') ? "⏸" : "暂停";

        // 按钮行（自面板左缘起，按钮间隔 12px）：重播 16..112 / 播放 124..196 / 速度 208..284。
        MakeButton(panel, "ReplayButton", replayLabel, new Vector2(16f, -16f), new Vector2(96f, 36f), OnReplayClicked, out _);
        MakeButton(panel, "PlayPauseButton", playGlyph, new Vector2(124f, -16f), new Vector2(72f, 36f), OnPlayPauseClicked, out playButtonLabel);
        MakeButton(panel, "SpeedButton", SpeedLabels[speedIndex], new Vector2(208f, -16f), new Vector2(76f, 36f), OnSpeedClicked, out speedButtonLabel);

        roundLabel = MakeText("RoundLabel", panel, 18, FontStyle.Bold, new Color(1f, 0.88f, 0.58f, 1f), TextAnchor.MiddleRight);
        // 回合标签右对齐 308..484，与速度按钮右缘（284）保持 24px 净空。
        SetRect(roundLabel.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-16f, -16f), new Vector2(176f, 36f));
        roundLabel.text = "R 0 / 0";

        seekSlider = BuildSeekSlider(panel);
    }

    Slider BuildSeekSlider(RectTransform parent)
    {
        GameObject go = new GameObject("SeekSlider", typeof(RectTransform), typeof(Slider));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        // 滑杆行 y 16..40，与上方按钮行下缘（52）保持 12px 净空；左右各留 16px 边距。
        SetRect(rt, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(16f, 16f), new Vector2(468f, 24f));

        RectTransform bgRt = MakeRect("Background", rt);
        SetRect(bgRt, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(0f, 8f));
        Image bgImg = bgRt.gameObject.AddComponent<Image>();
        bgImg.color = new Color(1f, 1f, 1f, 0.14f);
        bgImg.raycastTarget = true;

        RectTransform fillArea = MakeRect("FillArea", rt);
        SetRect(fillArea, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-18f, 8f));
        RectTransform fillRt = MakeRect("Fill", fillArea);
        SetRect(fillRt, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(10f, 0f));
        Image fillImg = fillRt.gameObject.AddComponent<Image>();
        fillImg.color = new Color(1f, 0.78f, 0.16f, 0.85f);
        fillImg.raycastTarget = false;

        RectTransform handleArea = MakeRect("HandleSlideArea", rt);
        SetRect(handleArea, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-18f, 0f));
        RectTransform handleRt = MakeRect("Handle", handleArea);
        SetRect(handleRt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(18f, 18f));
        Image handleImg = handleRt.gameObject.AddComponent<Image>();
        handleImg.sprite = GetCircleSprite();
        handleImg.color = new Color(1f, 0.92f, 0.62f, 1f);

        Slider slider = go.GetComponent<Slider>();
        slider.fillRect = fillRt;
        slider.handleRect = handleRt;
        slider.targetGraphic = handleImg;
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = 1f;
        slider.maxValue = Mathf.Max(1f, player == null ? 1f : player.TotalRounds);
        slider.wholeNumbers = true;
        slider.SetValueWithoutNotify(player == null ? 1f : Mathf.Max(1, player.CurrentRound));
        slider.onValueChanged.AddListener(OnSliderChanged);

        EventTrigger trigger = go.AddComponent<EventTrigger>();
        AddTrigger(trigger, EventTriggerType.PointerDown, OnSliderPointerDown);
        AddTrigger(trigger, EventTriggerType.PointerUp, OnSliderPointerUp);
        return slider;
    }

    void BuildRushOverlay(Transform parent)
    {
        rushRoot = MakeRect("RushOverlay", parent);
        SetRect(rushRoot, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        rushGroup = rushRoot.gameObject.AddComponent<CanvasGroup>();
        rushGroup.alpha = 0f;
        rushGroup.interactable = false;
        rushGroup.blocksRaycasts = false;

        RectTransform vignetteRt = MakeRect("Vignette", rushRoot);
        SetRect(vignetteRt, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        Image vignette = vignetteRt.gameObject.AddComponent<Image>();
        vignette.sprite = GetVignetteSprite();
        vignette.color = new Color(1f, 1f, 1f, 0.5f);
        vignette.raycastTarget = false;

        rushTopBand = MakeRect("TopBand", rushRoot);
        SetRect(rushTopBand, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, 175f), new Vector2(0f, 170f));
        Image topImg = rushTopBand.gameObject.AddComponent<Image>();
        topImg.sprite = GetBandSprite();
        topImg.color = Color.white;
        topImg.raycastTarget = false;

        rushBottomBand = MakeRect("BottomBand", rushRoot);
        SetRect(rushBottomBand, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, -175f), new Vector2(0f, 170f));
        Image bottomImg = rushBottomBand.gameObject.AddComponent<Image>();
        bottomImg.sprite = GetBandSprite();
        bottomImg.color = Color.white;
        bottomImg.raycastTarget = false;
        rushBottomBand.localScale = new Vector3(1f, -1f, 1f);

        Sprite banner = LycheeLoadThemeSprite("rush_banner.png");
        if (banner != null)
        {
            RectTransform bannerRt = MakeRect("RushBannerImage", rushRoot);
            SetRect(bannerRt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(780f, 250f));
            Image bannerImg = bannerRt.gameObject.AddComponent<Image>();
            bannerImg.sprite = banner;
            bannerImg.preserveAspect = true;
            bannerImg.raycastTarget = false;
        }

        Text title = MakeText("RushTitle", rushRoot, 96, FontStyle.Bold, GoldTitle, TextAnchor.MiddleCenter);
        SetRect(title.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(960f, 160f));
        title.text = "宫 宴 冲 刺";
        Shadow shadow = title.gameObject.AddComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
        shadow.effectDistance = new Vector2(4f, -4f);
        Outline outline = title.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0.55f, 0.06f, 0.03f, 0.95f);
        outline.effectDistance = new Vector2(2.5f, -2.5f);
        rushTextRect = title.rectTransform;

        rushRoot.gameObject.SetActive(false);
    }

    void BuildFreshnessAlerts(Transform parent)
    {
        RectTransform leftRt = MakeRect("FreshEdgeLeft", parent);
        SetRect(leftRt, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(16f, 0f));
        leftEdgeBar = leftRt.gameObject.AddComponent<Image>();
        leftEdgeBar.color = new Color(1f, 0f, 0f, 0f);
        leftEdgeBar.raycastTarget = false;

        RectTransform rightRt = MakeRect("FreshEdgeRight", parent);
        SetRect(rightRt, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), Vector2.zero, new Vector2(16f, 0f));
        rightEdgeBar = rightRt.gameObject.AddComponent<Image>();
        rightEdgeBar.color = new Color(0f, 0f, 1f, 0f);
        rightEdgeBar.raycastTarget = false;

        RectTransform toastRt = MakeRect("FreshToast", parent);
        SetRect(toastRt, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -62f), new Vector2(440f, 42f));
        toastGroup = toastRt.gameObject.AddComponent<CanvasGroup>();
        toastGroup.alpha = 0f;
        toastGroup.interactable = false;
        toastGroup.blocksRaycasts = false;
        Image toastBg = toastRt.gameObject.AddComponent<Image>();
        toastBg.color = AmberBg;
        toastBg.raycastTarget = false;

        RectTransform accentRt = MakeRect("Accent", toastRt);
        SetRect(accentRt, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(5f, 0f));
        toastAccent = accentRt.gameObject.AddComponent<Image>();
        toastAccent.color = GoldTitle;
        toastAccent.raycastTarget = false;

        // 文字区距左侧强调条（0..5）19px、距右缘 18px，保证内容不贴背景边。
        toastText = MakeText("ToastText", toastRt, 18, FontStyle.Bold, new Color(1f, 0.95f, 0.82f, 1f), TextAnchor.MiddleCenter);
        SetRect(toastText.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), new Vector2(3f, 0f), new Vector2(-42f, 0f));
    }

    // ================= 事件流 =================

    void PushFeedEvent(LycheeReplayMessage message, int round)
    {
        string type = message.type;
        if (type == "MOVE_PROGRESS" || type == "ROUND_STATE") return;
        string node = ResolveNodeName(message.payload);
        string label = "R" + round + " " + CategoryIcon(type) + " " +
            (string.IsNullOrEmpty(node) ? "" : node + " ") + EventNameCn(type);
        Color color = FeedColor(type);
        if (feedPaused)
        {
            pendingFeed.Add(new PendingFeed { label = label, color = color });
            while (pendingFeed.Count > 8) pendingFeed.RemoveAt(0);
            return;
        }
        CreateFeedEntry(label, color);
    }

    void CreateFeedEntry(string label, Color dotColor)
    {
        if (feedRoot == null) return;
        GameObject go = new GameObject("FeedEntry", typeof(RectTransform));
        go.transform.SetParent(feedRoot, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        SetRect(rt, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(-FeedWidth - 40f, 0f), new Vector2(FeedWidth, FeedEntryHeight - 4f));
        CanvasGroup group = go.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.interactable = false;
        group.blocksRaycasts = false;
        Image bg = go.AddComponent<Image>();
        bg.color = PanelBg;
        bg.raycastTarget = false;

        RectTransform dotRt = MakeRect("Dot", rt);
        SetRect(dotRt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(15f, 0f), new Vector2(10f, 10f));
        Image dot = dotRt.gameObject.AddComponent<Image>();
        dot.sprite = GetCircleSprite();
        dot.color = dotColor;
        dot.raycastTarget = false;

        Text text = MakeText("Label", rt, 15, FontStyle.Bold, SoftText, TextAnchor.MiddleLeft);
        SetRect(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), new Vector2(13f, 0f), new Vector2(-40f, 0f));
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        // 超长条目按可用宽度（条目宽 332 - 圆点区 33 - 右留白 13）截断并追加省略号，
        // 保证单行展示、不会伸出条目背景/面板。
        text.text = TruncateWithEllipsis(text, label, FeedWidth - 46f);

        FeedEntry entry = new FeedEntry
        {
            rect = rt,
            group = group,
            born = Time.unscaledTime,
            slideStart = Time.unscaledTime
        };
        feedEntries.Insert(0, entry);
        ReassignFeedTargets();
        rt.anchoredPosition = new Vector2(-FeedWidth - 40f, entry.targetY);

        int alive = 0;
        for (int i = 0; i < feedEntries.Count; i++)
            if (!feedEntries[i].fading) alive++;
        for (int i = feedEntries.Count - 1; i >= 0 && alive > MaxFeedEntries; i--)
        {
            if (feedEntries[i].fading) continue;
            BeginFeedFade(feedEntries[i]);
            alive--;
        }
    }

    void ReassignFeedTargets()
    {
        for (int i = 0; i < feedEntries.Count; i++)
            feedEntries[i].targetY = -i * FeedEntryHeight;
    }

    void BeginFeedFade(FeedEntry entry)
    {
        if (entry == null || entry.fading) return;
        entry.fading = true;
        entry.fadeStart = Time.unscaledTime;
    }

    void UpdateFeed(float now)
    {
        if (feedPaused && rushRoutine == null)
        {
            feedPaused = false;
            FlushPendingFeed();
        }
        bool removed = false;
        for (int i = feedEntries.Count - 1; i >= 0; i--)
        {
            FeedEntry entry = feedEntries[i];
            if (entry == null || entry.rect == null)
            {
                feedEntries.RemoveAt(i);
                removed = true;
                continue;
            }
            float slide = Ease01((now - entry.slideStart) / 0.25f);
            float x = Mathf.Lerp(-FeedWidth - 40f, 0f, slide);
            float blend = 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime);
            float y = Mathf.Lerp(entry.rect.anchoredPosition.y, entry.targetY, blend);
            entry.rect.anchoredPosition = new Vector2(x, y);

            if (!entry.fading && now - entry.born > FeedLifetime) BeginFeedFade(entry);
            if (entry.fading)
            {
                float keep = 1f - Ease01((now - entry.fadeStart) / 0.3f);
                entry.group.alpha = Mathf.Min(slide, keep);
                if (keep <= 0f)
                {
                    Destroy(entry.rect.gameObject);
                    feedEntries.RemoveAt(i);
                    removed = true;
                }
            }
            else
            {
                entry.group.alpha = slide;
            }
        }
        if (removed) ReassignFeedTargets();
    }

    void FlushPendingFeed()
    {
        if (pendingFeed.Count == 0) return;
        for (int i = 0; i < pendingFeed.Count; i++)
            CreateFeedEntry(pendingFeed[i].label, pendingFeed[i].color);
        pendingFeed.Clear();
    }

    string ResolveNodeName(LycheeReplayPayload payload)
    {
        if (payload == null) return null;
        string nodeId = !string.IsNullOrEmpty(payload.targetNodeId) ? payload.targetNodeId : payload.nodeId;
        if (string.IsNullOrEmpty(nodeId)) nodeId = payload.fromNodeId;
        if (string.IsNullOrEmpty(nodeId)) return null;
        if (player != null && player.TryGetStationPosition(nodeId, out _, out string displayName) && !string.IsNullOrEmpty(displayName))
            return displayName;
        return nodeId;
    }

    // ================= 天气横幅 =================

    void LoadWeatherSprites()
    {
        weatherHotSprite = LycheeLoadThemeSprite("icon_weather_hot.png");
        weatherRainSprite = LycheeLoadThemeSprite("icon_weather_rain.png");
        weatherFogSprite = LycheeLoadThemeSprite("icon_weather_fog.png");
    }

    void RefreshWeatherBanner(LycheeReplayFrame frame)
    {
        if (weatherText == null || weatherBg == null) return;
        LycheeReplayWeatherState active = FirstWeather(frame == null || frame.weather == null ? null : frame.weather.active);
        LycheeReplayWeatherState forecast = FirstWeather(frame == null || frame.weather == null ? null : frame.weather.forecast);

        if (active != null)
        {
            weatherVisibleTarget = true;
            weatherBreathing = false;
            weatherBg.color = WeatherBgColor(active.type);
            if (weatherOutline != null) weatherOutline.enabled = false;
            Sprite icon = WeatherSprite(active.type);
            ApplyWeatherIcon(icon);
            string symbol = icon == null ? WeatherSymbol(active.type) + " " : "";
            int remainRounds = active.remainRound > 0
                ? active.remainRound
                : (frame != null && active.endRound > frame.round ? active.endRound - frame.round : 0);
            string remain = remainRounds > 0 ? " · 余 " + remainRounds + " 回合" : "";
            weatherText.text = symbol + WeatherNameCn(active.type) + "进行中 · " + RegionNameCn(active.region) + remain;
            return;
        }

        if (forecast != null)
        {
            weatherVisibleTarget = true;
            weatherBreathing = true;
            weatherBg.color = PanelBg;
            if (weatherOutline != null) weatherOutline.enabled = true;
            Sprite icon = WeatherSprite(forecast.type);
            ApplyWeatherIcon(icon);
            string symbol = icon == null ? "☁ " : "";
            string startText = forecast.startRound > 0 ? "将于 R" + forecast.startRound + " 起" : "即将来临";
            weatherText.text = symbol + "预告：" + WeatherNameCn(forecast.type) + " " + startText + " · " + RegionNameCn(forecast.region);
            return;
        }

        weatherVisibleTarget = false;
        weatherBreathing = false;
    }

    void ApplyWeatherIcon(Sprite icon)
    {
        if (weatherIcon == null) return;
        weatherIcon.enabled = icon != null;
        if (icon != null) weatherIcon.sprite = icon;
    }

    void UpdateWeatherBanner(float now)
    {
        if (weatherGroup == null) return;
        float target = weatherVisibleTarget ? 1f : 0f;
        weatherShown = Mathf.MoveTowards(weatherShown, target, Time.unscaledDeltaTime / 0.25f);
        float breathe = weatherBreathing ? 0.78f + 0.22f * Mathf.Sin(now * 2.6f) : 1f;
        weatherGroup.alpha = Ease01(weatherShown) * breathe;
    }

    static LycheeReplayWeatherState FirstWeather(LycheeReplayWeatherState[] list)
    {
        if (list == null) return null;
        for (int i = 0; i < list.Length; i++)
            if (list[i] != null && !string.IsNullOrEmpty(list[i].type)) return list[i];
        return null;
    }

    Sprite WeatherSprite(string type)
    {
        if (string.IsNullOrEmpty(type)) return null;
        if (type == "HOT") return weatherHotSprite;
        if (type == "HEAVY_RAIN") return weatherRainSprite;
        if (type == "MOUNTAIN_FOG") return weatherFogSprite;
        return null;
    }

    static Color WeatherBgColor(string type)
    {
        if (type == "HOT") return new Color(0.58f, 0.30f, 0.04f, 0.85f);
        if (type == "HEAVY_RAIN") return new Color(0.05f, 0.22f, 0.46f, 0.85f);
        if (type == "MOUNTAIN_FOG") return new Color(0.30f, 0.32f, 0.34f, 0.85f);
        if (type == "NIGHT_WIND") return new Color(0.10f, 0.12f, 0.30f, 0.85f);
        return PanelBg;
    }

    static string WeatherSymbol(string type)
    {
        if (type == "HOT") return "☀";
        if (type == "HEAVY_RAIN") return "⛈";
        if (type == "MOUNTAIN_FOG") return "☁";
        if (type == "NIGHT_WIND") return "☾";
        return "☁";
    }

    static string WeatherNameCn(string type)
    {
        if (type == "HOT") return "酷热";
        if (type == "HEAVY_RAIN") return "暴雨";
        if (type == "MOUNTAIN_FOG") return "山雾";
        if (type == "NIGHT_WIND") return "夜风";
        return string.IsNullOrEmpty(type) ? "天气" : type.Replace("_", " ");
    }

    static string RegionNameCn(string region)
    {
        if (region == "ALL") return "全图";
        if (region == "WATER") return "水路";
        if (region == "MOUNTAIN") return "山路";
        if (region == "PLAIN") return "平原";
        return string.IsNullOrEmpty(region) ? "区域" : region.Replace("_", " ");
    }

    // ================= 回放控制条 =================

    void UpdateControls()
    {
        if (player == null || seekSlider == null) return;
        int total = Mathf.Max(1, player.TotalRounds);
        if ((int)seekSlider.maxValue != total)
        {
            suppressSlider = true;
            seekSlider.maxValue = total;
            suppressSlider = false;
        }
        // 失误兜底：非指针途径（如键盘）改动滑杆后没有 PointerUp，0.5 秒后自动恢复播放状态。
        if (playStateCaptured && !sliderHeld && Time.unscaledTime - lastUserSliderChange > 0.5f)
        {
            playStateCaptured = false;
            player.SetPlaying(wasPlayingBeforeDrag);
        }
        if (!sliderHeld && !playStateCaptured)
        {
            suppressSlider = true;
            seekSlider.SetValueWithoutNotify(Mathf.Clamp(player.CurrentRound, (int)seekSlider.minValue, total));
            suppressSlider = false;
        }
        if (roundLabel != null) roundLabel.text = "R " + player.CurrentRound + " / " + total;
        if (playButtonLabel != null)
        {
            string wanted = player.IsPlaying ? pauseGlyph : playGlyph;
            if (playButtonLabel.text != wanted) playButtonLabel.text = wanted;
        }
    }

    void OnReplayClicked()
    {
        if (player == null) return;
        player.SeekToRound(0);
        player.SetPlaying(true);
    }

    void OnPlayPauseClicked()
    {
        if (player == null) return;
        player.SetPlaying(!player.IsPlaying);
    }

    void OnSpeedClicked()
    {
        speedIndex = (speedIndex + 1) % SpeedSeconds.Length;
        if (player != null) player.secondsPerRound = SpeedSeconds[speedIndex];
        if (speedButtonLabel != null) speedButtonLabel.text = SpeedLabels[speedIndex];
    }

    void OnSliderChanged(float value)
    {
        if (suppressSlider || player == null) return;
        lastUserSliderChange = Time.unscaledTime;
        if (!playStateCaptured)
        {
            wasPlayingBeforeDrag = player.IsPlaying;
            playStateCaptured = true;
        }
        player.SetPlaying(false);
        player.SeekToRound(Mathf.RoundToInt(value));
    }

    void OnSliderPointerDown()
    {
        sliderHeld = true;
        lastUserSliderChange = Time.unscaledTime;
        if (!playStateCaptured)
        {
            wasPlayingBeforeDrag = player != null && player.IsPlaying;
            playStateCaptured = true;
        }
        if (player != null) player.SetPlaying(false);
    }

    void OnSliderPointerUp()
    {
        sliderHeld = false;
        if (playStateCaptured && player != null) player.SetPlaying(wasPlayingBeforeDrag);
        playStateCaptured = false;
    }

    static void AddTrigger(EventTrigger trigger, EventTriggerType eventType, Action handler)
    {
        if (trigger == null || handler == null) return;
        EventTrigger.Entry entry = new EventTrigger.Entry { eventID = eventType };
        entry.callback.AddListener(_ => handler());
        trigger.triggers.Add(entry);
    }

    // ================= RUSH 转场 =================

    void TriggerRushTransition(int round)
    {
        if (rushRoot == null || rushRoutine != null) return;
        if (round == lastRushRound) return;
        lastRushRound = round;
        rushRoutine = StartCoroutine(RushRoutine());
    }

    IEnumerator RushRoutine()
    {
        feedPaused = true;
        rushRoot.gameObject.SetActive(true);
        rushGroup.alpha = 0f;
        float start = Time.unscaledTime;
        const float bandTravel = 175f;
        while (true)
        {
            float elapsed = Time.unscaledTime - start;
            if (elapsed >= RushDuration) break;
            float bandT = Ease01(elapsed / 0.45f);
            if (rushTopBand != null)
                rushTopBand.anchoredPosition = new Vector2(0f, Mathf.Lerp(bandTravel, 0f, bandT));
            if (rushBottomBand != null)
                rushBottomBand.anchoredPosition = new Vector2(0f, Mathf.Lerp(-bandTravel, 0f, bandT));

            float inAlpha = Ease01(elapsed / 0.3f);
            float outAlpha = 1f - Ease01((elapsed - (RushDuration - 0.55f)) / 0.55f);
            rushGroup.alpha = Mathf.Min(inAlpha, outAlpha);

            if (rushTextRect != null)
            {
                float scaleT = Ease01((elapsed - 0.2f) / 0.6f);
                float scale = Mathf.Lerp(1.4f, 1f, scaleT);
                rushTextRect.localScale = new Vector3(scale, scale, 1f);
                float shakeAmp = Mathf.Clamp01(1f - elapsed / 1.1f) * 5f;
                rushTextRect.anchoredPosition = shakeAmp > 0.05f
                    ? new Vector2(UnityEngine.Random.Range(-shakeAmp, shakeAmp), UnityEngine.Random.Range(-shakeAmp, shakeAmp))
                    : Vector2.zero;
            }
            yield return null;
        }
        rushGroup.alpha = 0f;
        if (rushTextRect != null) rushTextRect.anchoredPosition = Vector2.zero;
        rushRoot.gameObject.SetActive(false);
        feedPaused = false;
        FlushPendingFeed();
        rushRoutine = null;
    }

    // ================= 鲜度警示 =================

    void CheckFreshnessThresholds(LycheeReplayFrame previous, LycheeReplayFrame current)
    {
        if (previous == null || current == null || previous.players == null || current.players == null) return;
        if (current.round - previous.round != 1) return;
        for (int i = 0; i < current.players.Length; i++)
        {
            LycheeReplayPlayerState now = current.players[i];
            if (now == null) continue;
            LycheeReplayPlayerState before = null;
            for (int j = 0; j < previous.players.Length; j++)
            {
                if (previous.players[j] != null && previous.players[j].playerId == now.playerId)
                {
                    before = previous.players[j];
                    break;
                }
            }
            if (before == null) continue;
            int crossed = 0;
            for (int threshold = 90; threshold >= 10; threshold -= 10)
            {
                if (before.freshness > threshold && now.freshness <= threshold)
                {
                    crossed = threshold;
                    break;
                }
            }
            if (crossed <= 0) continue;
            int fruitDelta = now.goodFruit - before.goodFruit;
            string team = now.camp == 0 ? "红队" : "蓝队";
            string text = team + " 鲜度跌破 " + crossed + (fruitDelta < 0 ? " · 好果 " + fruitDelta : "");
            RaiseFreshnessAlert(now.camp, current.round, text);
        }
    }

    void HandleFreshnessEvent(LycheeReplayMessage message, int round)
    {
        LycheeReplayPayload payload = message.payload;
        int playerId = payload == null ? 0 : payload.playerId;
        int camp = FindCamp(playerId);
        string team = player != null ? player.TeamDisplayName(playerId) : "队伍";
        string text;
        if (message.type == "GOOD_TO_BAD")
        {
            text = team + " 好果转坏 · 坏果 +1";
        }
        else
        {
            string detail = payload != null && payload.freshness > 0f
                ? " · 鲜度 " + payload.freshness.ToString("0")
                : "";
            text = team + " 鲜度骤降" + detail;
        }
        RaiseFreshnessAlert(camp, round, text);
    }

    void RaiseFreshnessAlert(int camp, int round, string text)
    {
        if (camp == 0 || camp == 1)
        {
            if (alertRoundByCamp.TryGetValue(camp, out int lastRound) && lastRound == round) return;
            alertRoundByCamp[camp] = round;
            Color color = camp == 0 ? RedTeamColor : BlueTeamColor;
            FlashEdge(camp, color);
            ShowFreshToast(text, color);
            return;
        }
        ShowFreshToast(text, GoldTitle);
    }

    int FindCamp(int playerId)
    {
        if (player == null) return -1;
        int camp = SearchCamp(player.CurrentFrame, playerId);
        if (camp < 0) camp = SearchCamp(player.StartFrameData, playerId);
        return camp;
    }

    static int SearchCamp(LycheeReplayFrame frame, int playerId)
    {
        if (frame == null || frame.players == null) return -1;
        for (int i = 0; i < frame.players.Length; i++)
            if (frame.players[i] != null && frame.players[i].playerId == playerId)
                return frame.players[i].camp;
        return -1;
    }

    void FlashEdge(int camp, Color color)
    {
        Image bar = camp == 0 ? leftEdgeBar : rightEdgeBar;
        if (bar == null) return;
        if (camp == 0)
        {
            if (leftFlashRoutine != null) StopCoroutine(leftFlashRoutine);
            leftFlashRoutine = StartCoroutine(EdgeFlashRoutine(bar, color));
        }
        else
        {
            if (rightFlashRoutine != null) StopCoroutine(rightFlashRoutine);
            rightFlashRoutine = StartCoroutine(EdgeFlashRoutine(bar, color));
        }
    }

    IEnumerator EdgeFlashRoutine(Image bar, Color color)
    {
        float start = Time.unscaledTime;
        while (bar != null)
        {
            float progress = (Time.unscaledTime - start) / 1.2f;
            if (progress >= 1f) break;
            float pulse = Mathf.Abs(Mathf.Sin(progress * Mathf.PI * 3f));
            bar.color = new Color(color.r, color.g, color.b, 0.6f * pulse * (1f - 0.35f * progress));
            yield return null;
        }
        if (bar != null) bar.color = new Color(color.r, color.g, color.b, 0f);
    }

    void ShowFreshToast(string text, Color accent)
    {
        if (toastText == null || toastGroup == null) return;
        toastText.text = text;
        if (toastAccent != null) toastAccent.color = accent;
        if (toastRoutine != null) StopCoroutine(toastRoutine);
        toastRoutine = StartCoroutine(ToastRoutine());
    }

    IEnumerator ToastRoutine()
    {
        float start = Time.unscaledTime;
        while (Time.unscaledTime - start < 0.22f)
        {
            toastGroup.alpha = Ease01((Time.unscaledTime - start) / 0.22f);
            yield return null;
        }
        toastGroup.alpha = 1f;
        float hold = Time.unscaledTime;
        while (Time.unscaledTime - hold < 1.7f) yield return null;
        float fade = Time.unscaledTime;
        while (Time.unscaledTime - fade < 0.3f)
        {
            toastGroup.alpha = 1f - Ease01((Time.unscaledTime - fade) / 0.3f);
            yield return null;
        }
        toastGroup.alpha = 0f;
        toastRoutine = null;
    }

    // ================= 文案/配色映射 =================

    static Color FeedColor(string type)
    {
        if (string.IsNullOrEmpty(type)) return new Color(0.86f, 0.48f, 1f);
        if (type.Contains("WEATHER")) return new Color(0.28f, 0.74f, 1f);
        if (type.Contains("GUARD") || type.Contains("FORCED")) return new Color(1f, 0.28f, 0.14f);
        if (type.Contains("TASK") || type.Contains("BOUNTY")) return new Color(1f, 0.78f, 0.16f);
        if (type.Contains("DELIVER") || type.Contains("VERIFY")) return new Color(0.34f, 1f, 0.42f);
        return new Color(0.86f, 0.48f, 1f);
    }

    static string CategoryIcon(string type)
    {
        if (string.IsNullOrEmpty(type)) return "◆";
        if (type.Contains("WEATHER")) return "☁";
        if (type.Contains("GUARD") || type.Contains("FORCED") || type.Contains("CONTEST") || type.Contains("CARD")) return "⚔";
        if (type.Contains("TASK") || type.Contains("BOUNTY")) return "★";
        if (type.Contains("DELIVER") || type.Contains("VERIFY")) return "✓";
        return "◆";
    }

    static string EventNameCn(string type)
    {
        switch (type)
        {
            case "MATCH_START": return "比赛开始";
            case "MATCH_END": return "比赛结束";
            case "NODE_ENTER": return "进入节点";
            case "TASK_SPAWN": return "皇榜任务刷新";
            case "TASK_COMPLETE": return "任务完成";
            case "TASK_EXPIRE": return "任务过期";
            case "WEATHER_FORECAST": return "天气预告";
            case "WEATHER_START": return "天气生效";
            case "WEATHER_END": return "天气结束";
            case "RESOURCE_CLAIM": return "资源获取";
            case "PROCESS_START": return "处理开始";
            case "PROCESS_COMPLETE": return "处理完成";
            case "SQUAD_DISPATCH": return "小队出发";
            case "SQUAD_ARRIVE": return "小队抵达";
            case "SQUAD_WEAKEN_SUCCESS": return "削弱奏效";
            case "SQUAD_REINFORCE_SUCCESS": return "增援奏效";
            case "SQUAD_CLEAR_SUCCESS": return "清障完成";
            case "SQUAD_SCOUT_SUCCESS": return "侦察完成";
            case "GUARD_SET": return "设卡布防";
            case "GUARD_DECAY": return "设卡风化";
            case "GUARD_BREAK": return "攻破设卡";
            case "BOUNTY_SPAWN": return "悬赏出现";
            case "BOUNTY_COMPLETE": return "悬赏完成";
            case "COST_COMMIT": return "代价投入";
            case "FORCED_PASS_START": return "强制通行";
            case "FORCED_PASS_END": return "通行结束";
            case "ACTION_REJECTED": return "行动被拒";
            case "RUSH_PHASE_START": return "宫宴冲刺开启";
            case "VERIFY_GATE_START": return "验核开始";
            case "VERIFY_GATE_COMPLETE": return "验核完成";
            case "DELIVER_SUCCESS": return "交付成功";
            case "GOOD_TO_BAD": return "好果转坏";
            case "FRESHNESS_DROP": return "鲜度骤降";
            case "WINDOW_CONTEST_START": return "对抗开始";
            case "WINDOW_CARD_REVEAL": return "对抗出牌";
            case "WINDOW_CONTEST_END": return "对抗结束";
        }
        if (!string.IsNullOrEmpty(type) && type.StartsWith("SQUAD_", StringComparison.Ordinal)) return "小队行动";
        return string.IsNullOrEmpty(type) ? "事件" : type.Replace('_', ' ');
    }

    // ================= 通用工具 =================

    static float Ease01(float t)
    {
        return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
    }

    /// <summary>按像素宽度截断文本：超出 maxWidth 时截到可容纳处并追加省略号，用于事件流条目防溢出。</summary>
    static string TruncateWithEllipsis(Text text, string label, float maxWidth)
    {
        if (text == null || string.IsNullOrEmpty(label) || maxWidth <= 0f) return label;
        Font textFont = text.font;
        if (textFont == null) return label;
        const char ellipsis = '…';
        int size = text.fontSize;
        FontStyle style = text.fontStyle;
        // 动态字体需先请求字形，否则 GetCharacterInfo 取不到 advance。
        textFont.RequestCharactersInTexture(label + ellipsis, size, style);

        float total = 0f;
        for (int i = 0; i < label.Length; i++)
            total += CharAdvance(textFont, label[i], size, style);
        if (total <= maxWidth) return label;

        float budget = Mathf.Max(0f, maxWidth - CharAdvance(textFont, ellipsis, size, style));
        float used = 0f;
        int keep = 0;
        while (keep < label.Length)
        {
            float advance = CharAdvance(textFont, label[keep], size, style);
            if (used + advance > budget) break;
            used += advance;
            keep++;
        }
        return label.Substring(0, Mathf.Max(1, keep)) + ellipsis;
    }

    static float CharAdvance(Font font, char c, int size, FontStyle style)
    {
        if (font != null && font.GetCharacterInfo(c, out CharacterInfo info, size, style))
            return info.advance;
        // 取不到字形信息时按字号估算宽度兜底（CJK 约等宽于字号，西文略窄）。
        return c > 0x2E7F ? size : size * 0.6f;
    }

    IEnumerator PanelEnterRoutine(CanvasGroup group, float delay)
    {
        if (group == null) yield break;
        group.alpha = 0f;
        float wait = Time.unscaledTime + Mathf.Max(0f, delay);
        while (Time.unscaledTime < wait) yield return null;
        float start = Time.unscaledTime;
        while (true)
        {
            float t = (Time.unscaledTime - start) / 0.28f;
            group.alpha = Ease01(t);
            if (t >= 1f) break;
            yield return null;
        }
        group.alpha = 1f;
    }

    static Font GetSharedFont()
    {
        if (sharedFontResolved) return sharedFont;
        sharedFontResolved = true;
        // 优先用随包字体：WebGL 取不到系统字体
        sharedFont = Resources.Load<Font>("Fonts/LXGWWenKai-Regular");
        if (sharedFont != null) return sharedFont;
        try
        {
            sharedFont = Font.CreateDynamicFontFromOSFont(new[] { "PingFang SC", "Heiti SC", "Arial Unicode MS" }, 32);
        }
        catch (Exception)
        {
            sharedFont = null;
        }
        if (sharedFont == null)
        {
            try
            {
                sharedFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }
            catch (Exception)
            {
                sharedFont = null;
            }
        }
        return sharedFont;
    }

    static bool HasGlyph(Font font, char glyph)
    {
        try
        {
            return font != null && font.HasCharacter(glyph);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>从 StreamingAssets/UITheme/ 运行时加载主题贴图；缺失或失败返回 null（走程序化兜底）。</summary>
    static Sprite LycheeLoadThemeSprite(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        try
        {
            byte[] bytes = LycheeThemeCache.LoadBytes(fileName);
            if (bytes == null || bytes.Length == 0) return null;
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(bytes)) return null;
            texture.wrapMode = TextureWrapMode.Clamp;
            return Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
        }
        catch (Exception)
        {
            return null;
        }
    }

    static Sprite GetCircleSprite()
    {
        if (circleSprite != null) return circleSprite;
        const int size = 24;
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
        float radius = (size - 2) * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float distance = Vector2.Distance(new Vector2(x, y), center);
                float alpha = Mathf.Clamp01(radius - distance + 0.5f);
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }
        texture.Apply();
        texture.wrapMode = TextureWrapMode.Clamp;
        circleSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        return circleSprite;
    }

    static Sprite GetBandSprite()
    {
        if (bandSprite != null) return bandSprite;
        const int height = 64;
        Texture2D texture = new Texture2D(1, height, TextureFormat.RGBA32, false);
        Color band = new Color(0.78f, 0.10f, 0.06f, 1f);
        for (int y = 0; y < height; y++)
        {
            float alpha = Mathf.Pow(y / (float)(height - 1), 0.8f);
            texture.SetPixel(0, y, new Color(band.r, band.g, band.b, alpha));
        }
        texture.Apply();
        texture.wrapMode = TextureWrapMode.Clamp;
        bandSprite = Sprite.Create(texture, new Rect(0f, 0f, 1f, height), new Vector2(0.5f, 0.5f), 100f);
        return bandSprite;
    }

    static Sprite GetVignetteSprite()
    {
        if (vignetteSprite != null) return vignetteSprite;
        const int size = 128;
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x / (float)(size - 1) * 2f - 1f;
                float dy = y / (float)(size - 1) * 2f - 1f;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.55f, 1.35f, distance));
                texture.SetPixel(x, y, new Color(0f, 0f, 0f, alpha));
            }
        }
        texture.Apply();
        texture.wrapMode = TextureWrapMode.Clamp;
        vignetteSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        return vignetteSprite;
    }

    static RectTransform MakeRect(string goName, Transform parent)
    {
        GameObject go = new GameObject(goName, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go.GetComponent<RectTransform>();
    }

    static void SetRect(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 anchoredPosition, Vector2 size)
    {
        if (rt == null) return;
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = pivot;
        rt.anchoredPosition = anchoredPosition;
        rt.sizeDelta = size;
        rt.localScale = Vector3.one;
    }

    Text MakeText(string goName, Transform parent, int size, FontStyle style, Color color, TextAnchor anchor)
    {
        GameObject go = new GameObject(goName, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        Text text = go.AddComponent<Text>();
        text.font = GetSharedFont();
        text.fontSize = size;
        text.fontStyle = style;
        text.color = color;
        text.alignment = anchor;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        return text;
    }

    Button MakeButton(RectTransform parent, string goName, string label, Vector2 anchoredPosition, Vector2 size, UnityEngine.Events.UnityAction onClick, out Text labelText)
    {
        GameObject go = new GameObject(goName, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        SetRect(rt, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), anchoredPosition, size);
        Image image = go.GetComponent<Image>();
        image.color = new Color(0.10f, 0.09f, 0.055f, 0.95f);
        Outline outline = go.AddComponent<Outline>();
        outline.effectColor = new Color(1f, 0.78f, 0.16f, 0.38f);
        outline.effectDistance = new Vector2(1f, -1f);
        Button button = go.GetComponent<Button>();
        button.targetGraphic = image;
        if (onClick != null) button.onClick.AddListener(onClick);
        // 按钮文字字号 20（原 16 在 1080p 下偏小难读）。
        labelText = MakeText("Label", rt, 20, FontStyle.Bold, GoldTitle, TextAnchor.MiddleCenter);
        SetRect(labelText.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        labelText.text = label;
        return button;
    }
}
