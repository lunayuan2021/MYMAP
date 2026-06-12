using System;
using System.Collections;
using System.Collections.Generic;
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

    // ---- 唐风统一调色板：墨底承画面、鎏金点题、绢黄走正文、朱红示警、暗金收边 ----
    static readonly Color PanelBg = new Color(0.102f, 0.090f, 0.078f, 0.86f);     // 墨 #1A1714
    static readonly Color GoldTitle = new Color(0.831f, 0.686f, 0.361f, 1f);      // 鎏金 #D4AF5C
    static readonly Color SilkText = new Color(0.929f, 0.878f, 0.765f, 1f);       // 绢黄 #EDE0C3
    static readonly Color DarkGoldEdge = new Color(0.549f, 0.455f, 0.220f, 0.9f); // 暗金描边 #8C7438
    static readonly Color Vermilion = new Color(0.620f, 0.161f, 0.125f, 1f);      // 朱红 #9E2920
    static readonly Color CloudGray = new Color(0.45f, 0.53f, 0.56f, 1f);         // 青灰（天气类条目色条）
    static readonly Color RedTeamColor = new Color(0.95f, 0.18f, 0.11f, 1f);
    static readonly Color BlueTeamColor = new Color(0.20f, 0.48f, 1f, 1f);
    static readonly Color SoftText = new Color(0.929f, 0.878f, 0.765f, 0.95f);    // 绢黄正文（事件流条目）

    static readonly float[] SpeedSeconds = { 0.20f, 0.10f, 0.05f, 0.025f };
    static readonly string[] SpeedLabels = { "0.5×", "1×", "2×", "4×" };

    static Font sharedFont;
    static bool sharedFontResolved;
    static Sprite circleSprite;
    static Sprite bandSprite;
    static Sprite vignetteSprite;
    static Sprite panelFrameSprite;
    static bool panelFrameResolved;

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

    /// <summary>数值变化飘字（"+N"/"-N"）的池化条目：复用 GameObject，禁每帧 new。</summary>
    sealed class DeltaFloater
    {
        public RectTransform rect;
        public Text text;
        public CanvasGroup group;
        public float start;
        public bool active;
    }

    /// <summary>panel_frame 兜底登记项：WebGL 预载完成后补换肤时，需要一并关掉的兜底元素。</summary>
    sealed class PendingSkin
    {
        public Image bg;          // 等待升级的面板底图
        public Outline edge;      // 兜底暗金细线描边（升级后关闭）
        public GameObject extra;  // 兜底形态的附加装饰（控制条顶部鎏金线，升级后隐藏）
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

    // ---- 回合数滚动 + Delta 飘字 ----
    const int MaxFloaters = 6;
    float displayedRound = -1f;          // 滚动显示值；-1 表示未初始化（首帧直接对齐不滚动）
    int lastRoundValue = int.MinValue;   // 上一帧的真实回合，用于检测数值跳变与方向
    float roundFlashUntil = -1f;         // 鎏金闪烁截止时刻（数值增加瞬间点亮 0.3s）
    int lastShownRound = int.MinValue;   // 标签上一次实际写入的回合显示值（变化才拼串，免每帧分配）
    int lastShownTotal = int.MinValue;   // 标签上一次实际写入的总回合数
    readonly List<DeltaFloater> floaterPool = new List<DeltaFloater>();

    // ---- 面板换肤兜底登记（等待 WebGL 主题预载完成后升级为 panel_frame 边框）----
    readonly List<PendingSkin> pendingSkins = new List<PendingSkin>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void LycheeBootstrapHudExtensions()
    {
        if (Disabled) return;
        LycheeReplayPlayer host = FindAnyObjectByType<LycheeReplayPlayer>();
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
        UpdateFloaters(now);
        UpgradePendingSkins();
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
        // 非相邻帧应用 = 回放跳转（拖滑杆/重播/循环回开头）：回合显示直接贴齐，
        // 与 LycheeReplayRuntimeHud 比分滚动的口径一致——硬滚追只会长时间显示错误中间值。
        // 快速播放下单帧补播多回合时 RoundApplied 逐帧触发、相邻帧差恒为 1，不会误判。
        if (previous == null || current.round - previous.round != 1)
            SnapRoundDisplay(current.round);
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
        if (FindAnyObjectByType<EventSystem>() != null) return;
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
        // 天气横幅底色需按天气类型动态变化（语义色），不能整体九宫格换肤，
        // 改为常驻一圈暗金细边统一唐风气质；预告呼吸用的鎏金描边仍单独开关。
        Outline weatherEdge = root.gameObject.AddComponent<Outline>();
        weatherEdge.effectColor = DarkGoldEdge;
        weatherEdge.effectDistance = new Vector2(1f, -1f);
        weatherOutline = root.gameObject.AddComponent<Outline>();
        weatherOutline.effectColor = new Color(GoldTitle.r, GoldTitle.g, GoldTitle.b, 0.9f);
        weatherOutline.effectDistance = new Vector2(1.6f, -1.6f);
        weatherOutline.enabled = false;

        // 图标左缘留 16px 边距；文字区自图标右缘（44）再留 6px 起步，右缘留 16px。
        RectTransform iconRt = MakeRect("WeatherIcon", root);
        SetRect(iconRt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(16f, 0f), new Vector2(28f, 28f));
        weatherIcon = iconRt.gameObject.AddComponent<Image>();
        weatherIcon.preserveAspect = true;
        weatherIcon.raycastTarget = false;
        weatherIcon.enabled = false;

        weatherText = MakeText("WeatherText", root, 18, FontStyle.Bold, SilkText, TextAnchor.MiddleCenter);
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
        bg.raycastTarget = true;
        PendingSkin skin = ApplyPanelSkin(bg);

        // 顶部鎏金提示线只在无边框贴图的兜底形态下画，避免与九宫格描金边叠出双线；
        // 记入 PendingSkin.extra：WebGL 预载完成后若换上边框贴图，这条线要一并隐藏。
        if (skin != null)
        {
            RectTransform line = MakeRect("TopLine", panel);
            SetRect(line, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 2f));
            Image lineImg = line.gameObject.AddComponent<Image>();
            lineImg.color = new Color(GoldTitle.r, GoldTitle.g, GoldTitle.b, 0.86f);
            lineImg.raycastTarget = false;
            skin.extra = line.gameObject;
        }

        Font font = GetSharedFont();
        string replayLabel = HasGlyph(font, '⟲') ? "⟲ 重播" : "重播";
        playGlyph = HasGlyph(font, '▶') ? "▶" : "播放";
        pauseGlyph = HasGlyph(font, '⏸') ? "⏸" : "暂停";

        // 按钮行（自面板左缘起，按钮间隔 12px）：重播 16..112 / 播放 124..196 / 速度 208..284。
        MakeButton(panel, "ReplayButton", replayLabel, new Vector2(16f, -16f), new Vector2(96f, 36f), OnReplayClicked, out _);
        MakeButton(panel, "PlayPauseButton", playGlyph, new Vector2(124f, -16f), new Vector2(72f, 36f), OnPlayPauseClicked, out playButtonLabel);
        MakeButton(panel, "SpeedButton", SpeedLabels[speedIndex], new Vector2(208f, -16f), new Vector2(76f, 36f), OnSpeedClicked, out speedButtonLabel);

        roundLabel = MakeText("RoundLabel", panel, 18, FontStyle.Bold, SilkText, TextAnchor.MiddleRight);
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
        fillImg.color = new Color(GoldTitle.r, GoldTitle.g, GoldTitle.b, 0.85f);
        fillImg.raycastTarget = false;

        RectTransform handleArea = MakeRect("HandleSlideArea", rt);
        SetRect(handleArea, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-18f, 0f));
        RectTransform handleRt = MakeRect("Handle", handleArea);
        SetRect(handleRt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(18f, 18f));
        Image handleImg = handleRt.gameObject.AddComponent<Image>();
        handleImg.sprite = GetCircleSprite();
        handleImg.color = SilkText;

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
        toastBg.raycastTarget = false;
        ApplyPanelSkin(toastBg);

        RectTransform accentRt = MakeRect("Accent", toastRt);
        SetRect(accentRt, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(5f, 0f));
        toastAccent = accentRt.gameObject.AddComponent<Image>();
        toastAccent.color = GoldTitle;
        toastAccent.raycastTarget = false;

        // 文字区距左侧强调条（0..5）19px、距右缘 18px，保证内容不贴背景边。
        toastText = MakeText("ToastText", toastRt, 18, FontStyle.Bold, SilkText, TextAnchor.MiddleCenter);
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

    void CreateFeedEntry(string label, Color barColor)
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

        // 左侧 3px 类别色条贴满条目全高（对抗=朱红/任务=鎏金/天气=青灰/资源=绢黄），
        // 比圆点占位更省、一眼可按颜色扫读事件类别。
        RectTransform barRt = MakeRect("CategoryBar", rt);
        SetRect(barRt, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(3f, 0f));
        Image bar = barRt.gameObject.AddComponent<Image>();
        bar.color = barColor;
        bar.raycastTarget = false;

        Text text = MakeText("Label", rt, 15, FontStyle.Bold, SoftText, TextAnchor.MiddleLeft);
        SetRect(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), new Vector2(1f, 0f), new Vector2(-22f, 0f));
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        // 超长条目按可用宽度（条目宽 332 - 色条加左留白 12 - 右留白 10）截断并追加省略号，
        // 保证单行展示、不会伸出条目背景/面板。
        text.text = TruncateWithEllipsis(text, label, FeedWidth - 22f);

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
            // EaseOutCubic：滑入起步迅猛、落位轻收，比 SmoothStep 更有"甩入"劲道。
            float slide = EaseOutCubic((now - entry.slideStart) / 0.25f);
            float x = Mathf.Lerp(-FeedWidth - 40f, 0f, slide);
            float blend = 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime);
            float y = Mathf.Lerp(entry.rect.anchoredPosition.y, entry.targetY, blend);
            entry.rect.anchoredPosition = new Vector2(x, y);

            if (!entry.fading && now - entry.born > FeedLifetime) BeginFeedFade(entry);
            if (entry.fading)
            {
                float keep = 1f - EaseOutCubic((now - entry.fadeStart) / 0.3f);
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
        UpdateRoundLabel(player.CurrentRound, total);
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

    // ================= 数字滚动 + Delta 飘字 =================

    /// <summary>
    /// 回合数滚动跳字：displayedRound 以 MoveTowards 追真实值；速度下限跟随播放节奏
    /// （1.5×回合推进速率，至少 4/秒），任何倍速下显示值至多落后不足一回合——若仍按固定
    /// 4/秒追，4× 速度推进 40 回合/秒时显示值会稳定落后十余回合。数值增加瞬间标签闪鎏金
    /// 0.3s 回绢黄。回放跳转的贴齐与飘字在 SnapRoundDisplay（由 HandleRoundApplied 触发）。
    /// </summary>
    void UpdateRoundLabel(int current, int total)
    {
        if (roundLabel == null) return;
        if (lastRoundValue == int.MinValue) lastRoundValue = current;
        if (current != lastRoundValue)
        {
            if (current > lastRoundValue) roundFlashUntil = Time.unscaledTime + 0.3f;
            lastRoundValue = current;
        }
        if (displayedRound < 0f) displayedRound = current;
        float floor = player != null && player.secondsPerRound > 0.001f
            ? Mathf.Max(4f, 1.5f / player.secondsPerRound)
            : 4f;
        float speed = Mathf.Max(Mathf.Abs(current - displayedRound) * 3f, floor);
        displayedRound = Mathf.MoveTowards(displayedRound, current, speed * Time.unscaledDeltaTime);
        int shown = Mathf.RoundToInt(displayedRound);
        if (shown != lastShownRound || total != lastShownTotal)
        {
            lastShownRound = shown;
            lastShownTotal = total;
            roundLabel.text = "R " + shown + " / " + total;
        }
        roundLabel.color = Time.unscaledTime < roundFlashUntil ? GoldTitle : SilkText;
    }

    /// <summary>
    /// 回放跳转贴齐：非相邻帧应用（拖滑杆/重播/循环回开头）时滚动显示值立即对齐真实回合，
    /// 不让回合号花一两秒"追"错误的中间值（与主 HUD 比分滚动 RollScore 的贴齐口径一致）；
    /// 跨度 ≥2 时出一条 "+N"/"-N" 飘字示意跳变。逐回合推进（含低帧率下单帧补播多回合，
    /// 每次 RoundApplied 仍为相邻帧）不会走到这里，故飘字不会在常规播放中刷屏。
    /// </summary>
    void SnapRoundDisplay(int round)
    {
        if (lastRoundValue != int.MinValue)
        {
            int delta = round - lastRoundValue;
            if (Mathf.Abs(delta) >= 2 && roundLabel != null)
                SpawnDeltaFloater(roundLabel.rectTransform, delta);
        }
        lastRoundValue = round;
        displayedRound = round;
        roundFlashUntil = -1f;
    }

    /// <summary>在目标数字上方生成 "+N"/"-N" 飘字（上飘 18px、0.8s 淡出，增=鎏金/减=朱红）；池化复用。</summary>
    void SpawnDeltaFloater(RectTransform anchor, int delta)
    {
        if (anchor == null || delta == 0) return;
        DeltaFloater item = null;
        for (int i = 0; i < floaterPool.Count; i++)
        {
            if (!floaterPool[i].active)
            {
                item = floaterPool[i];
                break;
            }
        }
        if (item == null)
        {
            if (floaterPool.Count >= MaxFloaters)
            {
                // 池满征用最早的一条：飘字寿命仅 0.8s，被顶掉的早已接近全透明。
                item = floaterPool[0];
                for (int i = 1; i < floaterPool.Count; i++)
                    if (floaterPool[i].start < item.start) item = floaterPool[i];
            }
            else
            {
                Text text = MakeText("DeltaFloater", canvasRect, 16, FontStyle.Bold, GoldTitle, TextAnchor.MiddleCenter);
                item = new DeltaFloater
                {
                    rect = text.rectTransform,
                    text = text,
                    group = text.gameObject.AddComponent<CanvasGroup>()
                };
                item.group.interactable = false;
                item.group.blocksRaycasts = false;
                floaterPool.Add(item);
            }
        }
        // 挂到目标数字下面（SetParent(false) 保持 UI 缩放），自其顶缘上方起飘。
        item.rect.SetParent(anchor, false);
        SetRect(item.rect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 0f), new Vector2(0f, 2f), new Vector2(120f, 22f));
        item.text.text = (delta > 0 ? "+" : "") + delta;
        item.text.color = delta > 0 ? GoldTitle : Vermilion;
        item.group.alpha = 1f;
        item.start = Time.unscaledTime;
        item.active = true;
        item.rect.gameObject.SetActive(true);
    }

    void UpdateFloaters(float now)
    {
        for (int i = 0; i < floaterPool.Count; i++)
        {
            DeltaFloater item = floaterPool[i];
            if (!item.active || item.rect == null) continue;
            float t = (now - item.start) / 0.8f;
            if (t >= 1f)
            {
                item.active = false;
                item.rect.gameObject.SetActive(false);
                continue;
            }
            // 上飘缓出（起飘快、到顶慢）+ 平滑淡出。
            item.rect.anchoredPosition = new Vector2(0f, 2f + 18f * EaseOutCubic(t));
            item.group.alpha = 1f - Ease01(t);
        }
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
        // 类别色条与唐风调色板对齐：对抗=朱红、任务=鎏金、天气=青灰、资源=绢黄，
        // 杂项落暗金不抢戏；对抗组的匹配口径与 CategoryIcon 的 ⚔ 一致。
        if (string.IsNullOrEmpty(type)) return DarkGoldEdge;
        if (type.Contains("WEATHER")) return CloudGray;
        if (type.Contains("GUARD") || type.Contains("FORCED") || type.Contains("CONTEST") || type.Contains("CARD")) return Vermilion;
        if (type.Contains("TASK") || type.Contains("BOUNTY")) return GoldTitle;
        if (type.Contains("RESOURCE") || type.Contains("DELIVER") || type.Contains("VERIFY") || type.Contains("PROCESS")) return SilkText;
        return DarkGoldEdge;
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

    /// <summary>三次缓出（1-(1-t)³）：起步快、收尾稳，事件流滑入与飘字上浮用。</summary>
    static float EaseOutCubic(float t)
    {
        t = Mathf.Clamp01(t);
        float inv = 1f - t;
        return 1f - inv * inv * inv;
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

    /// <summary>
    /// panel_frame.png 九宫格 Sprite：border 取贴图短边 1/4，拉伸到任意面板尺寸时
    /// 四角与描金边花纹不变形；缺失或解码失败返回 null（调用方走细线描边兜底）。
    /// </summary>
    static Sprite GetPanelFrameSprite()
    {
        if (panelFrameResolved) return panelFrameSprite;
        try
        {
            byte[] bytes = LycheeThemeCache.LoadBytes("panel_frame.png");
            if (bytes == null || bytes.Length == 0)
            {
                // WebGL 预载未完成时取不到字节属于"尚未就绪"而非"素材缺失"：
                // 此时不锁定判定结果，预载完成后 UpgradePendingSkins 会再来取一次。
                panelFrameResolved = LycheeThemeCache.Preloaded;
                return null;
            }
            panelFrameResolved = true;
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(bytes)) return null;
            texture.wrapMode = TextureWrapMode.Clamp;
            float edge = Mathf.Max(8f, Mathf.Round(Mathf.Min(texture.width, texture.height) * 0.25f));
            panelFrameSprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(edge, edge, edge, edge));
            return panelFrameSprite;
        }
        catch (Exception)
        {
            panelFrameResolved = true;
            return null;
        }
    }

    /// <summary>
    /// 主要面板换肤：panel_frame 可用 → 九宫格描金边框；缺失 → 墨底 #1A1714 + 暗金细线描边兜底，
    /// 并登记到 pendingSkins（WebGL 上 HUD 构建早于主题预载完成，之后由 UpgradePendingSkins 补换肤）。
    /// 返回兜底登记项（null 表示已直接用上边框贴图）。
    /// </summary>
    PendingSkin ApplyPanelSkin(Image bg)
    {
        if (bg == null) return null;
        if (TrySkinWithFrame(bg)) return null;
        bg.color = PanelBg;
        Outline edge = bg.gameObject.AddComponent<Outline>();
        edge.effectColor = DarkGoldEdge;
        edge.effectDistance = new Vector2(1f, -1f);
        PendingSkin pending = new PendingSkin { bg = bg, edge = edge };
        pendingSkins.Add(pending);
        return pending;
    }

    /// <summary>给单个面板套上 panel_frame 九宫格；贴图尚不可用时返回 false（调用方走兜底）。</summary>
    static bool TrySkinWithFrame(Image bg)
    {
        Sprite frame = GetPanelFrameSprite();
        if (frame == null) return false;
        bg.sprite = frame;
        bg.type = Image.Type.Sliced;
        // 512 贴图的 128px 边框直接铺 42 高的 Toast 会被切片的边框收缩压成 6:1 的扁条，
        // 角花严重变形；按面板短边把 pixelsPerUnitMultiplier 等比放大，让单侧边框
        // 至多占短边约 42%，四角花纹保持原始比例。两处面板均为点锚定（sizeDelta 即实际
        // 尺寸）；拿不到正尺寸时跳过缩放，仅走默认切片。
        float shortSide = Mathf.Min(bg.rectTransform.sizeDelta.x, bg.rectTransform.sizeDelta.y);
        if (shortSide > 0f)
            bg.pixelsPerUnitMultiplier = Mathf.Max(1f, frame.border.x / (shortSide * 0.42f));
        // 贴图自带墨底花纹，白色微透叠出即可，不再二次着色。
        bg.color = new Color(1f, 1f, 1f, 0.95f);
        return true;
    }

    /// <summary>
    /// WebGL 上 HUD 自举（AfterSceneLoad）先于 LycheeThemeCache 预载协程完成，构建面板时
    /// panel_frame 取不到字节、只能先落兜底；预载完成后在此补一次换肤升级，并关掉兜底
    /// 描边/顶线。无论成败列表都会清空，整个生命周期至多执行一轮。
    /// </summary>
    void UpgradePendingSkins()
    {
        if (pendingSkins.Count == 0 || !LycheeThemeCache.Preloaded) return;
        for (int i = pendingSkins.Count - 1; i >= 0; i--)
        {
            PendingSkin pending = pendingSkins[i];
            if (pending.bg != null && TrySkinWithFrame(pending.bg))
            {
                if (pending.edge != null) pending.edge.enabled = false;
                if (pending.extra != null) pending.extra.SetActive(false);
            }
            pendingSkins.RemoveAt(i);
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
        // 按钮底色取墨色稍提亮（在面板墨底上需有一级层次差），描边走暗金。
        image.color = new Color(0.16f, 0.14f, 0.11f, 0.95f);
        Outline outline = go.AddComponent<Outline>();
        outline.effectColor = new Color(DarkGoldEdge.r, DarkGoldEdge.g, DarkGoldEdge.b, 0.7f);
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
