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
    const float FeedWidth = 500f;
    const float FeedEntryHeight = 36f;
    const float FeedLifetime = 12f;
    const float TickerWidth = 1160f;
    const float TickerHeight = 44f;
    const float TickerSpeed = 92f;
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

    // 1× = 1 回合 / 秒：replay 有多少回合就播多少秒（不固定假设 600 回合，胜负已定即收尾）。
    // 其余档位为快进倍率；0.125s/回合(8×) 仍高于 LycheeAudioDirector 的一次性音效静音阈值(0.026)。
    static readonly float[] SpeedSeconds = { 1.00f, 0.50f, 0.25f, 0.125f };
    static readonly string[] SpeedLabels = { "1×", "2×", "4×", "8×" };

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
        public string eventType;
        public string iconPath;
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
    readonly Dictionary<int, int> lastFeedFreshRoundByPlayer = new Dictionary<int, int>();

    // ---- 底部机制说明滚动条 ----
    RectTransform tickerRoot;
    RectTransform tickerClip;
    RectTransform tickerTextRect;
    CanvasGroup tickerGroup;
    Image tickerAccent;
    Text tickerText;
    readonly Queue<PendingFeed> tickerQueue = new Queue<PendingFeed>();
    readonly Dictionary<int, int> lastTickerFreshRoundByPlayer = new Dictionary<int, int>();
    readonly Dictionary<int, int> lastStateExplainRoundByPlayer = new Dictionary<int, int>();
    readonly Dictionary<string, int> recentFeedRoundByKey = new Dictionary<string, int>();
    readonly Dictionary<string, int> recentTickerRoundByKey = new Dictionary<string, int>();
    string tickerCurrentLabel;
    string lastFeedDedupKey;
    string lastTickerDedupKey;
    const int NotificationDedupWindowRounds = 10;
    float tickerStart;
    float tickerDuration = 6f;
    float tickerTextWidth = 800f;

    // ---- 天气横幅 ----
    CanvasGroup weatherGroup;
    Image weatherBg;
    Outline weatherOutline;
    RawImage weatherIcon;
    Text weatherText;
    float weatherShown;
    bool weatherVisibleTarget;
    bool weatherBreathing;
    Sprite weatherHotSprite;
    Sprite weatherRainSprite;
    Sprite weatherFogSprite;
    bool weatherSpritesResolved;

    // ---- 回放控制条 ----
    CanvasGroup controlGroup;
    Slider seekSlider;
    Text roundLabel;
    // ---- 任务计数框（已公开 / 已完成 / 已失效）----
    CanvasGroup taskStatsGroup;
    Text taskStatsText;
    int lastTaskStatsKey = int.MinValue;
    Text playButtonLabel;
    Dropdown speedDropdown;
    string playGlyph = "播放";
    string pauseGlyph = "暂停";
    bool suppressSlider;
    bool sliderHeld;
    bool playStateCaptured;
    bool wasPlayingBeforeDrag;
    float lastUserSliderChange;
    int speedIndex = 0; // 默认 1×（SpeedSeconds[0]=1.00s/回合=1 回合/秒）；Awake 再按场景值吸附校正

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
            player.secondsPerRound = SpeedSeconds[speedIndex];
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
        if (taskStatsGroup != null) StartCoroutine(PanelEnterRoutine(taskStatsGroup, 0.08f));
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
        UpdateTicker(now);
        UpdateWeatherBanner(now);
        UpdateControls();
        UpdateFloaters(now);
        UpgradeWeatherSprites();
        UpgradePendingSkins();
    }

    // ================= 事件订阅 =================

    void HandleReplayEvent(LycheeReplayMessage message, int round)
    {
        if (Disabled || message == null || string.IsNullOrEmpty(message.type)) return;
        string type = message.type;
        if (type == "RUSH_PHASE_START") TriggerRushTransition(round);
        if (type == "RUSH_START") TriggerRushTransition(round);
        if (type == "GOOD_TO_BAD" || type == "FRESHNESS_DROP") HandleFreshnessEvent(message, round);
        PushFeedEvent(message, round);
        PushMechanicTicker(message, round);
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
        ExplainStationaryStates(previous, current);
        UpdateTaskStats(current);
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
        BuildTaskStats(canvasRect);
        BuildMechanicTicker(canvasRect);
        BuildRushOverlay(canvasRect);
    }

    // 任务计数框：紧贴控制条下方（控制条 pos(16,-16)、高 104 → 底边 y=-120，再留 8px）。
    void BuildTaskStats(Transform parent)
    {
        RectTransform panel = MakeRect("TaskStatsPanel", parent);
        // 加宽到 560 容下「任务 ｜」前缀（明确这条是任务完成度，而不是裸的三个计数）。
        SetRect(panel, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -128f), new Vector2(560f, 34f));
        taskStatsGroup = panel.gameObject.AddComponent<CanvasGroup>();
        taskStatsGroup.alpha = 0f;
        taskStatsGroup.interactable = false;
        taskStatsGroup.blocksRaycasts = false;
        Image bg = panel.gameObject.AddComponent<Image>();
        bg.raycastTarget = false;
        ApplyPanelSkin(bg);

        taskStatsText = MakeText("TaskStatsText", panel, 17, FontStyle.Bold, SilkText, TextAnchor.MiddleCenter);
        SetRect(taskStatsText.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-24f, 0f));
        taskStatsText.text = "<color=#F6E8C8><b>任务</b></color> <color=#9A8A66>｜</color>  已公开 0   已完成 0   已失效 0";
    }

    // 从当前帧任务表统计：已公开=列表内全部、已完成=completed、已失效=failed。逐帧只在数字变化时刷新文本。
    void UpdateTaskStats(LycheeReplayFrame frame)
    {
        if (taskStatsText == null) return;
        int published = 0, completed = 0, expired = 0;
        if (frame != null && frame.tasks != null)
        {
            published = frame.tasks.Length;
            foreach (LycheeReplayTaskState t in frame.tasks)
            {
                if (t == null) continue;
                if (t.completed) completed++;
                else if (t.failed) expired++;
            }
        }
        int key = (published * 1000 + completed) * 1000 + expired;
        if (key == lastTaskStatsKey) return;
        lastTaskStatsKey = key;
        taskStatsText.text =
            "<color=#F6E8C8><b>任务</b></color> <color=#9A8A66>｜</color>  " +
            "已公开 <color=#FFD24A>" + published + "</color>   " +
            "已完成 <color=#5FE08A>" + completed + "</color>   " +
            "已失效 <color=#E0604C>" + expired + "</color>";
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

    void BuildMechanicTicker(Transform parent)
    {
        tickerRoot = MakeRect("MechanicTicker", parent);
        // 提示条下边框贴着底部 HUD 上边框(用户要求)：底部 HUD 底边留 6px 安全边(WebSafeBottomInset=6)、背板顶≈屏252(=6+背板高246)，
        // 故提示条底边 y=252(锚底/pivot 底)。改 HUD 底边距或背板高记得同步这个 252。
        SetRect(tickerRoot, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 252f), new Vector2(TickerWidth, TickerHeight));
        tickerGroup = tickerRoot.gameObject.AddComponent<CanvasGroup>();
        tickerGroup.alpha = 0f;
        tickerGroup.interactable = false;
        tickerGroup.blocksRaycasts = false;
        Image bg = tickerRoot.gameObject.AddComponent<Image>();
        bg.raycastTarget = false;
        ApplyPanelSkin(bg);

        RectTransform accentRt = MakeRect("TickerAccent", tickerRoot);
        SetRect(accentRt, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(5f, 0f));
        tickerAccent = accentRt.gameObject.AddComponent<Image>();
        tickerAccent.color = GoldTitle;
        tickerAccent.raycastTarget = false;

        Text caption = MakeText("TickerCaption", tickerRoot, 18, FontStyle.Bold, GoldTitle, TextAnchor.MiddleLeft);
        SetRect(caption.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(18f, 0f), new Vector2(84f, 0f));
        caption.text = "提示";

        tickerClip = MakeRect("TickerClip", tickerRoot);
        // 裁剪区左缘留到「提示」标签之后(≈110)、右缘内缩到描金边内(≈1140)。旧值 offset(72)/size(-132) 会把
        // 右缘算到 1166——比 1160 宽的面板还超出 6px，于是滚动的解释文字会戳出右侧描金边（用户说的「下方播报内容比边框宽」）。
        // 新 offset(45)/size(-130) → 裁剪区 [110,1140]，两侧都收进描金边内，RectMask2D 再把超出的滚动文字裁掉。
        SetRect(tickerClip, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), new Vector2(45f, 0f), new Vector2(-130f, -10f));
        tickerClip.gameObject.AddComponent<RectMask2D>();

        tickerText = MakeText("TickerText", tickerClip, 20, FontStyle.Bold, SilkText, TextAnchor.MiddleLeft);
        tickerText.horizontalOverflow = HorizontalWrapMode.Overflow;
        tickerText.verticalOverflow = VerticalWrapMode.Truncate;
        tickerTextRect = tickerText.rectTransform;
        SetRect(tickerTextRect, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(2200f, 28f));
        PushTicker("回放提示会在这里解释停留、处理回合、任务收益、清障和悬赏判定。", GoldTitle);
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
        weatherIcon = iconRt.gameObject.AddComponent<RawImage>();
        weatherIcon.raycastTarget = false;
        weatherIcon.enabled = false;

        weatherText = MakeText("WeatherText", root, 18, FontStyle.Bold, SilkText, TextAnchor.MiddleCenter);
        SetRect(weatherText.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), new Vector2(17f, 0f), new Vector2(-66f, 0f));
    }

    void BuildControls(Transform parent)
    {
        // 控制条加宽到 640：按钮、速度下拉和回合标签不再贴边，WebGL 小窗口缩放后也不会溢出边框。
        // 顶部按钮行与底部滑杆行垂直间隔 12px，内容距背景边缘 16px。
        RectTransform panel = MakeRect("ControlPanel", parent);
        // 面板 580 宽：panel_frame 九宫格描金边按短边(108)算，单侧描金边区≈45px；内容内缩到 ≥45px 不压四角金花。
        // 回合标签改成紧跟速度下拉（左对齐、间隔 ~14px），不再右贴边——旧版 720 宽 + 右锚定让 R xxx/xxx 离下拉
        // 隔了一大段空、整条也太长（用户反馈）。580 − 两侧各 50 内缩 = 480 可用，按钮行 + 紧凑回合标签正好铺满、不留大空。
        // 别改回 720 拉长、别把回合标签改回右贴边。
        SetRect(panel, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -16f), new Vector2(580f, 108f));
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
        // WebGL 字体会把部分播放/暂停符号判成可用但实际渲染为空白，直接用中文确保按钮可见。
        playGlyph = "播放";
        pauseGlyph = "暂停";

        // 按钮行自左缘内缩 50px 起（清四角金花），按钮间隔 12px；速度下拉止于 386。
        MakeButton(panel, "ReplayButton", replayLabel, new Vector2(50f, -16f), new Vector2(112f, 36f), OnReplayClicked, out _);
        MakeButton(panel, "PlayPauseButton", playGlyph, new Vector2(174f, -16f), new Vector2(84f, 36f), OnPlayPauseClicked, out playButtonLabel);
        speedDropdown = MakeSpeedDropdown(panel, new Vector2(270f, -16f), new Vector2(116f, 36f));

        // 回合标签紧跟速度下拉（下拉止于 386，标签自 400 起、间隔 14px），左对齐、不再右贴边。
        roundLabel = MakeText("RoundLabel", panel, 18, FontStyle.Bold, SilkText, TextAnchor.MiddleLeft);
        SetRect(roundLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(400f, -16f), new Vector2(128f, 36f));
        roundLabel.text = "R 0 / 0";
        roundLabel.resizeTextForBestFit = true;
        roundLabel.resizeTextMinSize = 13;
        roundLabel.resizeTextMaxSize = 18;

        seekSlider = BuildSeekSlider(panel);
    }

    Slider BuildSeekSlider(RectTransform parent)
    {
        GameObject go = new GameObject("SeekSlider", typeof(RectTransform), typeof(Slider));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        // 滑杆行 y 16..40，与上方按钮行下缘（52）保持 12px 净空；左右各内缩 50px 清四角金花（面板 580 → 宽 480）。
        SetRect(rt, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(50f, 16f), new Vector2(480f, 24f));

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
        if (type == "FRESHNESS_DROP") return;   // 用户(点7)：鲜度骤减不再进左侧消息列表(底部维度面板已能看出)
        LycheeReplayPayload payload = message.payload;
        string node = ResolveNodeName(payload);
        // 用户(点6/点8)：跟队伍绑定的事件要写明是哪队(谁的好果变坏/谁进入XX节点…)，队名按队色(红/蓝)上色，白字不明显。
        string actor = FeedTeamPrefix(payload != null ? payload.playerId : 0);
        // 类别符号不再拼进文本：交给 CreateFeedEntry 用专属 PNG 图标呈现（缺图才回退文字符号）。
        // 小分队事件单独成句、写明站点名+动作（用户点5/点6）：
        //   出发→「X队 派出小分队 出发 站点名 执行 动作」；完成/失败→「X队 小分队 在 站点名 完成/未能完成 动作」。
        string label = SquadFeedLabel(round, type, payload, actor, node);
        if (label == null)
            label = "回合 " + round + "  " + actor +
                (string.IsNullOrEmpty(node) ? "" : node + " · ") + FeedEventDetail(type, payload, node);
        Color color = FeedColor(type);
        string dedupKey = BuildNotificationDedupKey(type, payload);
        if (ShouldSkipNotification(recentFeedRoundByKey, ref lastFeedDedupKey, dedupKey, round, NotificationDedupWindowRounds)) return;
        if (feedPaused)
        {
            pendingFeed.Add(new PendingFeed { label = label, color = color, eventType = type, iconPath = FeedIconPath(type, payload) });
            while (pendingFeed.Count > 8) pendingFeed.RemoveAt(0);
            return;
        }
        CreateFeedEntry(label, color, type, FeedIconPath(type, payload));
    }

    void CreateFeedEntry(string label, Color barColor, string eventType, string iconPath)
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

        // 事件类别图标：有专属 PNG（EventIcons/ 或顶层 icon_*）就贴图，缺图回退文字符号，
        // 文本相应右移腾出图标位。这样事件流与地图脉冲、对抗演出统一了类别识别语言。
        Sprite feedIcon = FeedIconSprite(iconPath);
        float textLeft;
        if (feedIcon != null)
        {
            RectTransform iconRt = MakeRect("FeedIcon", rt);
            SetRect(iconRt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(9f, 0f), new Vector2(24f, 24f));
            RawImage iconImage = iconRt.gameObject.AddComponent<RawImage>();
            SetRawImageSprite(iconImage, feedIcon);
            iconImage.raycastTarget = false;
            textLeft = 38f;
        }
        else
        {
            label = CategoryIcon(eventType) + " " + label;
            textLeft = 8f;
        }

        Text text = MakeText("Label", rt, 15, FontStyle.Bold, SoftText, TextAnchor.MiddleLeft);
        text.supportRichText = true;   // 队伍前缀用 <color> 富文本上色(点6/点8)，需开启
        SetRect(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
            new Vector2((textLeft - 12f) * 0.5f, 0f), new Vector2(-(textLeft + 12f), 0f));
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        text.text = label;

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
        if (IsReplayPausedForHud() && !IsPresentationActiveForHud()) return;
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
            CreateFeedEntry(pendingFeed[i].label, pendingFeed[i].color, pendingFeed[i].eventType, pendingFeed[i].iconPath);
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

    // 小分队事件专属文案（出发/完成/失败各成一句、明确站点名与动作）；非小分队事件返回 null 走通用格式。
    static string SquadFeedLabel(int round, string type, LycheeReplayPayload payload, string actor, string node)
    {
        if (string.IsNullOrEmpty(type)) return null;
        string station = string.IsNullOrEmpty(node) ? "目标点" : node;
        string actionRaw = payload == null ? null : (!string.IsNullOrEmpty(payload.action) ? payload.action : payload.tactic);
        string actionCn = string.IsNullOrEmpty(actionRaw) ? "清障" : ActionName(actionRaw);
        // 用户反馈：小分队播报太长(如「红队 小分队 在 潼关驿 完成 清障」)。压缩成紧凑句式：去掉「在/完成/派出…出发…执行」
        // 等填充词，用 → 和 · 收紧长度。actor 已含队名(带队色)+尾空格。勿再加回「在/出发/执行」等虚词把它撑长。
        switch (type)
        {
            case "SQUAD_DISPATCH":
                return "回合 " + round + " " + actor + "小分队→" + station + " " + actionCn;
            case "SQUAD_CLEAR":
            case "SQUAD_CLEAR_SUCCESS":
            case "OBSTACLE_CLEAR":
                return "回合 " + round + " " + actor + "小分队·" + station + " " + actionCn + "完成";
            case "SQUAD_FAILED":
                return "回合 " + round + " " + actor + "小分队·" + station + " " + actionCn + "失败";
            default:
                return null;
        }
    }

    static string FeedEventDetail(string type, LycheeReplayPayload payload, string nodeName)
    {
        // 资源类写明是哪种资源（冰鉴/快马/短驿马…），否则泛化的「资源使用」看不出用了什么。
        if (type == "RESOURCE_USE")
            return "使用" + ResourceName(payload == null ? null : payload.resourceType);
        if (type == "RESOURCE_CLAIM")
            return "获取" + ResourceName(payload == null ? null : payload.resourceType);
        if (type == "WINDOW_CONTEST_DRAW")
            return ContestDrawText(payload);
        // C5: 终局急策按 rushTactic 分疾行令/护果令/破关令(§5.6)，不再泛化"冲刺策略"。HudExt 的 ActionName/ProcessName 不识别急策，需专用映射。
        if (type == "RUSH_TACTIC_USE")
            return RushTacticNameCn(payload == null ? null : payload.rushTactic);
        if (type == "PROCESS_PROGRESS" && IsRestingProcess(payload))
            return RestingProgressText(payload);
        if (IsProcessEvent(type))
        {
            string action = StationProcessAction(payload, nodeName);
            if (!string.IsNullOrEmpty(action))
                return action + " · " + EventNameCn(type);
        }
        return EventNameCn(type);
    }

    static bool IsProcessEvent(string type)
    {
        return type == "PROCESS_START" || type == "PROCESS_PROGRESS" ||
            type == "PROCESS_COMPLETE" || type == "PROCESS_INTERRUPT";
    }

    static string StationProcessAction(LycheeReplayPayload payload, string nodeName)
    {
        string nodeId = payload == null ? null : (!string.IsNullOrEmpty(payload.targetNodeId) ? payload.targetNodeId : payload.nodeId);
        if (string.IsNullOrEmpty(nodeId) && payload != null) nodeId = payload.fromNodeId;
        if (nodeId == "S02" || nodeName == "南岭驿") return "前段交接";
        if (nodeId == "S04" || nodeName == "江南码头") return "登船处理";
        if (nodeId == "S05" || nodeName == "洞庭水驿") return "水路换运";
        if (nodeId == "S11" || nodeName == "潼关驿") return "入关交接";
        if (nodeId == "S13" || nodeName == "灞桥驿") return "宫前交接";
        if (nodeId == "S14" || nodeName == "朱雀门") return "宫门验核";

        string process = payload == null ? null : (!string.IsNullOrEmpty(payload.processType) ? payload.processType : payload.action);
        return ProcessName(process);
    }

    // ================= 机制说明滚动条 =================

    void PushMechanicTicker(LycheeReplayMessage message, int round)
    {
        if (message == null || string.IsNullOrEmpty(message.type)) return;
        if (TryBuildMechanicText(message, round, out string text, out Color color))
        {
            string dedupKey = BuildNotificationDedupKey(message.type, message.payload);
            if (ShouldSkipNotification(recentTickerRoundByKey, ref lastTickerDedupKey, dedupKey, round, NotificationDedupWindowRounds)) return;
            PushTicker(text, color);
        }
    }

    void PushTicker(string label, Color color)
    {
        if (tickerText == null || string.IsNullOrEmpty(label)) return;
        tickerQueue.Enqueue(new PendingFeed { label = label, color = color });
        while (tickerQueue.Count > 8) tickerQueue.Dequeue();
    }

    void UpdateTicker(float now)
    {
        if (tickerGroup == null || tickerText == null || tickerTextRect == null) return;
        if (IsReplayPausedForHud() && !IsPresentationActiveForHud()) return;
        if (tickerCurrentLabel == null && tickerQueue.Count > 0)
            StartNextTicker(now);

        float targetAlpha = tickerCurrentLabel == null ? 0f : 1f;
        tickerGroup.alpha = Mathf.MoveTowards(tickerGroup.alpha, targetAlpha, Time.unscaledDeltaTime / 0.18f);
        if (tickerCurrentLabel == null) return;

        float clipWidth = tickerClip != null && tickerClip.rect.width > 1f ? tickerClip.rect.width : TickerWidth - 132f;
        float t = tickerDuration <= 0.001f ? 1f : Mathf.Clamp01((now - tickerStart) / tickerDuration);
        float x = Mathf.Lerp(clipWidth, -tickerTextWidth, t);
        tickerTextRect.anchoredPosition = new Vector2(x, 0f);
        if (t >= 1f)
        {
            tickerCurrentLabel = null;
            if (tickerQueue.Count > 0) StartNextTicker(now);
        }
    }

    void StartNextTicker(float now)
    {
        PendingFeed next = tickerQueue.Dequeue();
        tickerCurrentLabel = next.label;
        tickerText.text = next.label;
        tickerText.color = SilkText;
        if (tickerAccent != null) tickerAccent.color = next.color;
        float clipWidth = tickerClip != null && tickerClip.rect.width > 1f ? tickerClip.rect.width : TickerWidth - 132f;
        tickerTextWidth = Mathf.Max(tickerText.preferredWidth + 40f, 420f);
        tickerTextRect.sizeDelta = new Vector2(Mathf.Max(2200f, tickerTextWidth + 40f), 28f);
        tickerStart = now;
        tickerDuration = Mathf.Clamp((clipWidth + tickerTextWidth) / TickerSpeed, 5.2f, 14f);
        tickerTextRect.anchoredPosition = new Vector2(clipWidth, 0f);
    }

    bool TryBuildMechanicText(LycheeReplayMessage message, int round, out string text, out Color color)
    {
        text = null;
        color = FeedColor(message.type);
        string type = message.type;
        LycheeReplayPayload payload = message.payload ?? new LycheeReplayPayload();
        string team = TeamName(payload.playerId);
        string node = ResolveNodeName(payload);
        string reward = RewardHint(payload);

        if (type == "FRESHNESS_DROP") return false;   // 用户(点7)：鲜度骤减不再上提示条，底部维度面板已能看出
        if (type == "GOOD_TO_BAD")
        {
            string fruit = payload.badFruit > 0 ? "，坏果 " + payload.badFruit : "";
            text = "回合 " + round + " " + team + " 出现好果转坏" + fruit + "：好果数和鲜度都会影响最终分，低鲜度会拖低结算。";
            return true;
        }
        if (type == "WINDOW_CONTEST_DRAW")
        {
            text = "回合 " + round + " " + ContestDrawText(payload) + "。";
            return true;
        }
        if (type == "PROCESS_PROGRESS")
        {
            if (IsRestingProcess(payload))
            {
                text = "回合 " + round + " " + RestingProgressText(payload) + "。";
                return true;
            }
            string left = payload.remainingRound >= 0 ? "，剩余 " + payload.remainingRound + " 回合" : "";
            text = "回合 " + round + " " + team + " 停留在 " + SafeNode(node) + "：" + ProcessName(payload.processType) + left + reward + "；该行动完成后会恢复推进。";
            return true;
        }
        if (type == "PROCESS_START")
        {
            string cost = payload.processRound > 0 ? "，预计 " + payload.processRound + " 回合" : "";
            text = "回合 " + round + " " + team + " 开始" + ProcessName(payload.processType) + " " + SafeNode(node) + cost + reward + "。";
            return true;
        }
        if (type == "PROCESS_COMPLETE")
        {
            text = "回合 " + round + " " + team + " 完成" + ProcessName(payload.processType) + " " + SafeNode(node) + "，路线/资源/通行条件已更新。";
            return true;
        }
        if (type == "SQUAD_DISPATCH")
        {
            string eta = payload.completeRound > round ? "，预计 R" + payload.completeRound + " 完成" : "";
            text = "回合 " + round + " " + team + " 派出小分队前往 " + SafeNode(node) + " " + ActionName(payload.action) + eta + "；清掉障碍是打开路线，后续站点仍可能另有处理耗时。";   // C11: 去掉杜撰的"N 人"(§3.7 提交成本非人数)
            return true;
        }
        if (type == "SQUAD_CLEAR" || type == "SQUAD_CLEAR_SUCCESS" || type == "OBSTACLE_CLEAR")
        {
            text = "回合 " + round + " " + team + " 清掉 " + SafeNode(node) + " 的障碍：路线恢复；若对方随后仍 +6 个结算帧，是清障残留通行税(非站点处理耗时)。";   // C10: §3.7 单位为结算帧、归因为清障残留税
            return true;
        }
        // E1(确认口径,审查报告第五部分): 补全服务端实际发出的小分队事件全集(SCOUT/REINFORCE/WEAKEN/FAILED；
        // DISPATCH/CLEAR 上面已有)。字段以真实回放为准：SCOUT=playerId/targetNodeId；REINFORCE/WEAKEN=
        // playerId/targetNodeId/before/after(防守值,defenseBefore/After 为旧命名兜底)；FAILED=action/targetNodeId(常无 playerId)。
        if (type == "SQUAD_SCOUT" || type == "SQUAD_SCOUT_SUCCESS")
        {
            text = "回合 " + round + " " + team + " 小分队探路生效：" + SafeNode(node) + " 获得探路标记，之后在该点处理更省时。";
            return true;
        }
        if (type == "SQUAD_REINFORCE" || type == "SQUAD_REINFORCE_SUCCESS" || type == "SQUAD_WEAKEN" || type == "SQUAD_WEAKEN_SUCCESS")
        {
            bool weaken = type.Contains("WEAKEN");
            int defBefore = payload.defenseBefore != 0 || payload.defenseAfter != 0 ? payload.defenseBefore : Mathf.RoundToInt(payload.before);
            int defAfter = payload.defenseBefore != 0 || payload.defenseAfter != 0 ? payload.defenseAfter : Mathf.RoundToInt(payload.after);
            string change = defBefore != 0 || defAfter != 0 ? "：防守 " + defBefore + " → " + defAfter : "";
            text = "回合 " + round + " " + team + " 小分队" + (weaken ? "削弱" : "增援") + "生效 " + SafeNode(node) + change
                + (weaken ? "，之后攻破该点更容易。" : "，之后该点更难被攻破。");
            return true;
        }
        if (type == "SQUAD_FAILED" || type == "SQUAD_FAIL")
        {
            // 确认口径：UI 只表明失败即可，可附带 action/targetNodeId；**不展示失败原因、不读 payload.reason**。
            string failTeam = payload.playerId > 0 ? team + " " : "";
            text = "回合 " + round + " " + failTeam + "小分队行动失败：" + SafeNode(node) + " 的" + ActionName(payload.action) + "未完成。";
            return true;
        }
        // E5(确认口径): 探路标记四事件都上机制播报；字段用 playerId/targetNodeId/expireRound/remainingTriggers/
        // processType/beforeRound/afterRound，**不依赖 markerId**(服务端无此字段)。文案统一"探路标记"。
        if (type == "SCOUT_MARKER_ADD")
        {
            string expire = payload.expireRound > round ? "，R" + payload.expireRound + " 前有效" : "";
            string triggers = payload.remainingTriggers > 0 ? "，可触发 " + payload.remainingTriggers + " 次" : "";
            text = "回合 " + round + " " + team + " 在 " + SafeNode(node) + " 布下探路标记" + expire + triggers + "。";
            return true;
        }
        if (type == "SCOUT_MARKER_APPLY")
        {
            string delta = payload.beforeRound > 0 && payload.afterRound > 0 && payload.beforeRound != payload.afterRound
                ? "：" + ProcessName(payload.processType) + "耗时 " + payload.beforeRound + " → " + payload.afterRound + " 回合" : "";
            text = "回合 " + round + " " + SafeNode(node) + " 探路标记生效" + delta + "。";
            return true;
        }
        if (type == "SCOUT_MARKER_CONSUME")
        {
            string left = payload.remainingTriggers > 0 ? "，剩余 " + payload.remainingTriggers + " 次" : "，已用尽";
            text = "回合 " + round + " " + SafeNode(node) + " 探路标记消耗" + left + "。";
            return true;
        }
        if (type == "SCOUT_MARKER_EXPIRE")
        {
            text = "回合 " + round + " " + SafeNode(node) + " 的探路标记过期，不再提供处理减时。";
            return true;
        }
        if (type == "TASK_REFRESH" || type == "TASK_SPAWN")
        {
            int score = FindTaskScore(payload);
            string scoreText = score > 0 ? " +" + score + " 分" : " 任务分";
            string expire = payload.expireRound > round ? "，R" + payload.expireRound + " 前有效" : "";
            // E4(确认口径): routeBucket/protectionPlayerId 从 tasks[] 状态读(FindTaskState)，别假设事件 payload 带全量字段。
            LycheeReplayTaskState refreshTask = FindTaskState(payload);
            string bucket = refreshTask != null ? RouteBucketCn(refreshTask.routeBucket) : null;
            string bucketTag = string.IsNullOrEmpty(bucket) ? "" : "（" + bucket + "）";
            string protect = refreshTask != null && refreshTask.protectionPlayerId > 0
                ? "；保护期内仅 " + TeamName(refreshTask.protectionPlayerId) + " 可完成" : "";
            text = "回合 " + round + " 任务刷新 " + SafeNode(node) + bucketTag + "：完成可获得" + scoreText + expire + protect + "；被抢先或过期就不会到账。";
            return true;
        }
        if (type == "TASK_COMPLETE")
        {
            int score = payload.score > 0 ? payload.score : FindTaskScore(payload);
            text = "回合 " + round + " " + team + " 完成 " + SafeNode(node) + " 任务" + (score > 0 ? "，得分 +" + score : "") + "。";
            return true;
        }
        if (type == "TASK_EXPIRE")
        {
            // E4(确认口径): failureReason 从 tasks[] 状态读；为 null/未知就保持原通用文案，不杜撰原因。
            LycheeReplayTaskState expiredTask = FindTaskState(payload);
            string failReason = expiredTask != null ? FailureReasonCn(expiredTask.failureReason) : null;
            text = "回合 " + round + " " + SafeNode(node) + " 任务过期：" + (failReason ?? "没有队伍在窗口内完成") + "，这部分收益不会计入得分。";
            return true;
        }
        if (type == "BOUNTY_SPAWN" || type == "BOUNTY_CREATE")   // BOUNTY_CREATE 为 V4.8.12 新名
        {
            string score = payload.score > 0 ? " +" + payload.score + " 分" : " 额外奖励";
            text = "回合 " + round + " " + SafeNode(node) + " 出现" + BountyName(payload.bountyType) + "：先满足破关/清障条件的队伍才能拿到" + score + "。";
            return true;
        }
        if (type == "BOUNTY_COMPLETE" || type == "BOUNTY_CLAIM") // BOUNTY_CLAIM 为 V4.8.12 新名
        {
            string score = payload.score > 0 ? " +" + payload.score : "";
            // B3: 破关悬赏只发悬赏分，不发放随机资源(设计§5.4)。去掉"奖励 XX 已生效"误导文案。
            text = "回合 " + round + " " + team + " 拿到" + BountyName(payload.bountyType) + score + "，计入总分。";
            return true;
        }
        if (type == "INVALID_ACTION" || type == "ACTION_REJECTED")
        {
            // 一律走 errorCode/message→中文，避免英文错误码/英文 message 上屏
            string reason = LycheeReplayPlayer.ErrorCodeName(!string.IsNullOrEmpty(payload.errorCode) ? payload.errorCode : payload.message);
            text = "回合 " + round + " 行动未生效：" + reason + "。未满足节点、资源或时机条件时，不会获得任务/悬赏收益。";
            return true;
        }
        if (type == "RESOURCE_CLAIM")
        {
            text = "回合 " + round + " " + team + " 在 " + SafeNode(node) + " 获取 " + ResourceName(payload.resourceType) + "：资源会影响后续通行、验核或抢时间能力。";
            return true;
        }
        if (type == "RESOURCE_USE")
        {
            text = "回合 " + round + " " + team + " 使用 " + ResourceName(payload.resourceType) + "：这是为了降低后续路线成本或满足通行条件。";
            return true;
        }
        if (type == "FORCED_PASS_START")
        {
            string tax = payload.timeTax > 0 ? "，时间税 +" + payload.timeTax + " 帧" : "";   // C2: §4.5 1点时间税=1个结算帧
            text = "回合 " + round + " " + team + " 强制通行 " + SafeNode(node) + tax + "：能绕过阻拦，但会用时间换进度。";
            return true;
        }
        if (type == "OBSTACLE_RESIDUAL_TAX")
        {
            // 契约表: 非清障方经过被清障节点被额外加帧(extraRound)；清障方免税(§2.12/§3.7)。
            string extra = payload.extraRound > 0 ? "额外 +" + payload.extraRound + " 个结算帧" : "额外通行税";
            string by = payload.clearedByPlayerId > 0 ? "（" + TeamName(payload.clearedByPlayerId) + "清障残留）" : "";
            text = "回合 " + round + " " + team + " 通过 " + SafeNode(node) + "：非清障方" + extra + "通行税" + by + "。";
            return true;
        }
        if (type == "RUSH_START" || type == "RUSH_PHASE_START")
        {
            text = "回合 " + round + " 宫宴冲刺开启：最后阶段更看重验核、交付时机、好果数和鲜度保留。";
            return true;
        }
        if (type == "VERIFY_GATE_START")
        {
            text = "回合 " + round + " " + team + " 开始宫门验核：验核完成后才具备进宫交付资格。";
            return true;
        }
        if (type == "VERIFY_GATE_COMPLETE")
        {
            text = "回合 " + round + " " + team + " 宫门验核通过：已获得交付资格，接下来拼交付速度与保鲜结果。";
            return true;
        }
        if (type == "DELIVER_SUCCESS")
        {
            text = "回合 " + round + " " + team + " 完成交付：好果 " + payload.goodFruit + "，鲜度 " + payload.freshness.ToString("0.#") + "%，本次交付分 " + payload.score + "。";
            return true;
        }
        return false;
    }

    void ExplainStationaryStates(LycheeReplayFrame previous, LycheeReplayFrame current)
    {
        if (current == null || current.players == null) return;
        for (int i = 0; i < current.players.Length; i++)
        {
            LycheeReplayPlayerState state = current.players[i];
            if (state == null || state.delivered) continue;
            string playerState = state.state ?? "";
            bool stationary = playerState == "PROCESSING" || playerState == "IDLE";
            if (!stationary || !string.IsNullOrEmpty(state.routeEdgeId)) continue;
            if (lastStateExplainRoundByPlayer.TryGetValue(state.playerId, out int lastRound) &&
                current.round - lastRound < 6)
                continue;
            lastStateExplainRoundByPlayer[state.playerId] = current.round;

            string node = ResolveNodeName(new LycheeReplayPayload { nodeId = state.currentNodeId, playerId = state.playerId });
            string process = NodeProcessName(current, state.currentNodeId);
            string reason = playerState == "PROCESSING"
                ? process + "进行中，完成后继续推进"
                : "等待路线、资源、小队结果或下一步条件";
            PushTicker("R" + current.round + " " + TeamName(state.playerId) + " 停留在 " + SafeNode(node) + "：" + reason + "。", playerState == "PROCESSING" ? SilkText : DarkGoldEdge);
        }
    }

    static bool ShouldSurfaceFreshnessDrop(LycheeReplayPayload payload, int round, Dictionary<int, int> cache, int minInterval, float minLoss)
    {
        if (payload == null) return true;
        int key = payload.playerId;
        if (payload.loss >= minLoss) return true;
        if (key == 0) key = -1;
        if (cache.TryGetValue(key, out int lastRound) && round - lastRound < minInterval) return false;
        cache[key] = round;
        return true;
    }

    string TeamName(int playerId)
    {
        if (playerId == 0) return "车队";
        LycheeReplayFrame frame = player != null ? player.CurrentFrame : null;
        LycheeReplayPlayerState[] states = frame == null ? null : frame.players;
        if (states != null)
        {
            for (int i = 0; i < states.Length; i++)
            {
                LycheeReplayPlayerState state = states[i];
                if (state != null && state.playerId == playerId)
                    return FormatTeamDisplayName(state);
            }
        }
        return "队伍 " + playerId;
    }

    // 左侧消息列表的队伍前缀(富文本按队色上色)：红队朱红 / 蓝队队蓝，让「谁」一眼可辨且不再是白字。
    // playerId==0(无归属的节点/全局事件)返回空串、不加前缀。
    string FeedTeamPrefix(int playerId)
    {
        if (playerId == 0) return string.Empty;
        int camp = FindCamp(playerId);
        string name = camp == 0 ? "红队" : camp == 1 ? "蓝队" : "队伍" + playerId;
        string hex = camp == 0 ? "E0604C" : camp == 1 ? "5AA0FF" : "D4AF5C";
        return "<color=#" + hex + ">" + name + "</color> ";
    }

    static string FormatTeamDisplayName(LycheeReplayPlayerState state)
    {
        if (state == null) return "队伍";
        string campName = state.camp == 0 ? "红队" : "蓝队";
        // 用数值 playerId（红 1002 / 蓝 2002，唯一）作为队伍 ID，与底部 HUD 一致，重名也能区分。
        string id = state.playerId > 0 ? "#" + state.playerId : (!string.IsNullOrEmpty(state.teamId) ? "#" + state.teamId : string.Empty);
        string name = string.IsNullOrEmpty(state.name) ? string.Empty : state.name;
        if (string.IsNullOrEmpty(id)) return string.IsNullOrEmpty(name) ? campName : campName + " " + name;
        if (string.IsNullOrEmpty(name)) return campName + " " + id;
        return campName + " " + id + " · " + name;
    }

    string RewardHint(LycheeReplayPayload payload)
    {
        int taskScore = FindTaskScore(payload);
        int bountyScore = FindBountyScore(payload);
        if (taskScore > 0 && bountyScore > 0) return "，潜在收益 +" + (taskScore + bountyScore) + " 分";
        if (taskScore > 0) return "，任务收益 +" + taskScore + " 分";
        if (bountyScore > 0) return "，悬赏收益 +" + bountyScore + " 分";
        return "";
    }

    int FindTaskScore(LycheeReplayPayload payload)
    {
        if (payload == null) return 0;
        if (payload.score > 0) return payload.score;
        LycheeReplayFrame frame = player != null ? player.CurrentFrame : null;
        LycheeReplayTaskState[] tasks = frame == null ? null : frame.tasks;
        if (tasks == null) return 0;
        string nodeId = !string.IsNullOrEmpty(payload.targetNodeId) ? payload.targetNodeId : payload.nodeId;
        for (int i = 0; i < tasks.Length; i++)
        {
            LycheeReplayTaskState task = tasks[i];
            if (task == null || task.score <= 0) continue;
            if (!string.IsNullOrEmpty(payload.taskId) && task.taskId == payload.taskId) return task.score;
            if (!string.IsNullOrEmpty(payload.taskTemplateId) &&
                (task.taskTemplateId == payload.taskTemplateId || task.templateId == payload.taskTemplateId)) return task.score;
            string taskNode = !string.IsNullOrEmpty(task.targetNodeId) ? task.targetNodeId : task.nodeId;
            if (!string.IsNullOrEmpty(nodeId) && taskNode == nodeId) return task.score;
        }
        return 0;
    }

    /// <summary>E4: 按 taskId → taskTemplateId → 节点 依次匹配当前帧 tasks[] 状态(与 FindTaskScore 同口径)。
    /// routeBucket/failureReason/protectionPlayerId 是任务状态字段、不保证出现在事件 payload 里，必须从这里读。</summary>
    LycheeReplayTaskState FindTaskState(LycheeReplayPayload payload)
    {
        if (payload == null) return null;
        LycheeReplayFrame frame = player != null ? player.CurrentFrame : null;
        LycheeReplayTaskState[] tasks = frame == null ? null : frame.tasks;
        if (tasks == null) return null;
        string nodeId = !string.IsNullOrEmpty(payload.targetNodeId) ? payload.targetNodeId : payload.nodeId;
        LycheeReplayTaskState nodeMatch = null;
        for (int i = 0; i < tasks.Length; i++)
        {
            LycheeReplayTaskState task = tasks[i];
            if (task == null) continue;
            if (!string.IsNullOrEmpty(payload.taskId) && task.taskId == payload.taskId) return task;
            if (!string.IsNullOrEmpty(payload.taskTemplateId) &&
                (task.taskTemplateId == payload.taskTemplateId || task.templateId == payload.taskTemplateId)) return task;
            string taskNode = !string.IsNullOrEmpty(task.targetNodeId) ? task.targetNodeId : task.nodeId;
            if (nodeMatch == null && !string.IsNullOrEmpty(nodeId) && taskNode == nodeId) nodeMatch = task;
        }
        return nodeMatch;
    }

    /// <summary>E4(确认口径): 路线桶→中文。ROAD=官道/WATER=水路/MOUNTAIN=山路/BRANCH=支线；未知值返回 null 不显示。</summary>
    internal static string RouteBucketCn(string bucket)
    {
        if (string.IsNullOrEmpty(bucket)) return null;
        switch (bucket.ToUpperInvariant())
        {
            case "ROAD": return "官道";
            case "WATER": return "水路";
            case "MOUNTAIN": return "山路";
            case "BRANCH": return "支线";
            default: return null;
        }
    }

    /// <summary>E4(确认口径): 任务失败原因→中文；null/未知返回 null(调用方保持通用文案，不杜撰)。</summary>
    static string FailureReasonCn(string reason)
    {
        if (string.IsNullOrEmpty(reason)) return null;
        switch (reason.ToUpperInvariant())
        {
            case "FAILED_TARGET_LOST": return "任务目标已失效(如障碍已被提前清除)";
            case "FAILED_EXPIRED":
            case "EXPIRED": return "窗口期内没有队伍完成";
            default: return null;
        }
    }

    int FindBountyScore(LycheeReplayPayload payload)
    {
        if (payload == null) return 0;
        if (payload.score > 0 && !string.IsNullOrEmpty(payload.bountyId)) return payload.score;
        LycheeReplayFrame frame = player != null ? player.CurrentFrame : null;
        LycheeReplayBountyState[] bounties = frame == null ? null : frame.bounties;
        if (bounties == null) return 0;
        string nodeId = !string.IsNullOrEmpty(payload.targetNodeId) ? payload.targetNodeId : payload.nodeId;
        for (int i = 0; i < bounties.Length; i++)
        {
            LycheeReplayBountyState bounty = bounties[i];
            if (bounty == null || bounty.score <= 0) continue;
            if (!string.IsNullOrEmpty(payload.bountyId) && bounty.bountyId == payload.bountyId) return bounty.score;
            if (!string.IsNullOrEmpty(nodeId) && bounty.targetNodeId == nodeId) return bounty.score;
        }
        return 0;
    }

    string NodeProcessName(LycheeReplayFrame frame, string nodeId)
    {
        if (frame != null && frame.nodes != null && !string.IsNullOrEmpty(nodeId))
        {
            for (int i = 0; i < frame.nodes.Length; i++)
            {
                LycheeReplayNodeState node = frame.nodes[i];
                if (node != null && node.nodeId == nodeId && !string.IsNullOrEmpty(node.processType))
                    return ProcessName(node.processType);
            }
        }
        return "站点处理";
    }

    static string SafeNode(string node)
    {
        return string.IsNullOrEmpty(node) ? "当前位置" : node;
    }

    // C5: 终局急策名(§5.6) → 中文。与 LycheeReplayPlayer.ActionName 的急策映射保持一致；空/未知兜底"冲刺策略"。
    static string RushTacticNameCn(string value)
    {
        if (value == "RUSH_SPEED") return "疾行令";
        if (value == "RUSH_PROTECT") return "护果令";
        if (value == "BREAK_ORDER") return "破关令";
        return "冲刺策略";
    }

    static string ProcessName(string value)
    {
        if (value == "CLEAR_OBSTACLE") return "清障";
        if (value == "CLAIM_RESOURCE") return "领取资源";
        if (value == "BOARD") return "登船处理";
        if (value == "WATER_TRANSFER") return "水路换运";
        if (value == "TRANSFER") return "前段交接";
        if (value == "PASS_TRANSFER") return "入关交接";
        if (value == "PALACE_TRANSFER") return "宫前交接";
        if (value == "VERIFY" || value == "VERIFY_GATE" || value == "RUSH_VERIFY_GATE") return "宫门验核";
        if (value == "PASS_NODE") return "限时过关";
        if (value == "STATION_PROCESS") return "驿站催运";
        if (value == "WIN_HORSE_WINDOW") return "争马换乘";
        if (value == "WIN_DOCK_WINDOW") return "码头争船";
        if (value == "BREAK_GUARD") return "破关";
        if (value == "RESTING") return "平局休整";
        return "站点处理";   // 兜底改中文
    }

    static bool IsRestingProcess(LycheeReplayPayload payload)
    {
        return payload != null && payload.processType == "RESTING";
    }

    static string RestingProgressText(LycheeReplayPayload payload)
    {
        int remaining = payload == null ? 0 : payload.remainingRound;
        if (remaining <= 0) return "平局休整结束";
        return "平局休整中，剩 " + remaining + " 帧";
    }

    static string ContestDrawText(LycheeReplayPayload payload)
    {
        int rest = payload == null ? 0 : payload.restRound;
        if (rest <= 0 && payload != null) rest = payload.remainingRound > 0 ? payload.remainingRound : payload.durationRound;
        if (rest <= 0 && payload != null) rest = payload.totalRounds;
        if (rest <= 0) rest = 3;
        return "窗口平局，双方休整 " + rest + " 帧，休整中";
    }

    static string BuildNotificationDedupKey(string type, LycheeReplayPayload payload)
    {
        payload = payload ?? new LycheeReplayPayload();
        string nodeId = !string.IsNullOrEmpty(payload.targetNodeId) ? payload.targetNodeId : payload.nodeId;
        if (string.IsNullOrEmpty(nodeId)) nodeId = payload.fromNodeId;
        // 故意不纳入 remainingRound/restRound/score 这类「每回合都在变的倒计时/分数」字段：
        // 否则「平局休整中（还剩 3 帧→2→1）」每回合 key 都不同，连续去重失效、同一条提示刷屏。
        // 只用事件身份（类型/队伍/节点/处理/资源/动作/错误码）做 key，让同一状态的连发塌缩成一条，
        // 直到出现一条「不一样的事件」才再次放行（用户诉求：平局休整只显示一遍）。
        return type + "|" + payload.playerId + "|" + nodeId + "|" + payload.processType + "|" +
            payload.resourceType + "|" + payload.action + "|" + payload.tactic + "|" +
            payload.errorCode + "|" + payload.taskId + "|" + payload.taskTemplateId + "|" +
            payload.templateId + "|" + payload.contestId + "|" + payload.bountyId + "|" + payload.reward;
    }

    static bool ShouldSkipConsecutiveNotification(ref string lastKey, string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (lastKey == key) return true;
        lastKey = key;
        return false;
    }

    static bool ShouldSkipNotification(Dictionary<string, int> cache, ref string lastKey, string key, int round, int windowRounds)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (ShouldSkipConsecutiveNotification(ref lastKey, key)) return true;
        if (cache != null && cache.TryGetValue(key, out int lastRound) && round - lastRound >= 0 && round - lastRound < windowRounds)
            return true;
        if (cache != null)
        {
            cache[key] = round;
            if (cache.Count > 96)
                PruneNotificationCache(cache, round, windowRounds * 2);
        }
        return false;
    }

    static void PruneNotificationCache(Dictionary<string, int> cache, int round, int keepWindowRounds)
    {
        if (cache == null || cache.Count == 0) return;
        List<string> remove = new List<string>();
        foreach (KeyValuePair<string, int> entry in cache)
        {
            if (round - entry.Value > keepWindowRounds)
                remove.Add(entry.Key);
        }
        for (int i = 0; i < remove.Count; i++)
            cache.Remove(remove[i]);
    }

    static string ActionName(string value)
    {
        if (value == "SQUAD_SCOUT") return "探路";
        if (value == "SQUAD_CLEAR") return "清障";
        if (value == "SQUAD_REINFORCE") return "增援";
        if (value == "SQUAD_WEAKEN") return "削弱";
        if (value == "BREAK_GUARD") return "破关";
        if (value == "BOARD") return "登船处理";
        if (value == "CLAIM_RESOURCE") return "领取资源";
        if (value == "VERIFY_GATE") return "宫门验核";
        return ProcessName(value);
    }

    static int SquadMemberCount(LycheeReplayPayload payload)
    {
        string action = payload == null ? null : (!string.IsNullOrEmpty(payload.action) ? payload.action : payload.tactic);
        if (!string.IsNullOrEmpty(action))
        {
            string normalized = action.ToUpperInvariant();
            if (normalized.Contains("SCOUT")) return 1;
            if (normalized.Contains("CLEAR") ||
                normalized.Contains("REINFORCE") ||
                normalized.Contains("WEAKEN"))
                return 2;
        }
        return 2;
    }

    static string ResourceName(string value)
    {
        if (value == "ICE_BOX") return "冰鉴";
        if (value == "FAST_HORSE") return "快马";
        if (value == "SHORT_HORSE") return "短驿马";      // 注：设计文档写"短程马"，此处保留用户习惯用词"短驿马"
        if (value == "PASS_TOKEN") return "过所";          // B4: 对齐设计文档(原"通关令")
        if (value == "OFFICIAL_PERMIT") return "官凭";     // B4: 对齐设计文档(原"官牒")
        if (value == "INTEL") return "情报";
        if (value == "BOAT_RIGHT") return "船权";          // B4: 对齐设计文档(原"船班权益")
        return "资源";   // 兜底改中文
    }

    static string BountyName(string value)
    {
        if (value == "KEY_BOUNTY") return "关隘悬赏";
        return "悬赏";   // 兜底改中文
    }

    // ================= 天气横幅 =================

    void LoadWeatherSprites()
    {
        weatherHotSprite = LycheeLoadThemeSprite("icon_weather_hot.png");
        weatherRainSprite = LycheeLoadThemeSprite("icon_weather_rain.png");
        weatherFogSprite = LycheeLoadThemeSprite("icon_weather_fog.png");
        weatherSpritesResolved =
            (weatherHotSprite != null && weatherRainSprite != null && weatherFogSprite != null) ||
            LycheeThemeCache.Preloaded;
    }

    void UpgradeWeatherSprites()
    {
        if (weatherSpritesResolved) return;
        LoadWeatherSprites();
        if (weatherSpritesResolved && player != null)
            RefreshWeatherBanner(player.CurrentFrame);
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
            string region = RegionNameCn(active.region);
            weatherText.text = symbol + WeatherNameCn(active.type) + "进行中" + WeatherRegionSuffix(region) + remain;
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
            // 倒计时(用户要求)：按"将在 N 回合内出现"逐回合递减(30→29→…)，N = 起效回合 − 当前回合。
            // RefreshWeatherBanner 每回合(HandleRoundApplied)被调一次，故 N 自动倒数。
            int remaining = frame != null && forecast.startRound > 0 ? forecast.startRound - frame.round : 0;
            string startText = remaining > 0 ? "将在 " + remaining + " 回合内出现" : "即将来临";
            string region = RegionNameCn(forecast.region);
            weatherText.text = symbol + "预告：" + WeatherNameCn(forecast.type) + " " + startText + WeatherRegionSuffix(region);
            return;
        }

        weatherVisibleTarget = false;
        weatherBreathing = false;
    }

    void ApplyWeatherIcon(Sprite icon)
    {
        if (weatherIcon == null) return;
        weatherIcon.enabled = icon != null;
        if (icon != null) SetRawImageSprite(weatherIcon, icon);
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
        return "天气";   // 兜底改中文
    }

    static string RegionNameCn(string region)
    {
        if (region == "ALL") return "";
        if (region == "WATER") return "水路";
        if (region == "MOUNTAIN") return "山路";
        if (region == "PLAIN") return "平原";
        return "区域";   // 兜底改中文
    }

    static string WeatherRegionSuffix(string region)
    {
        return string.IsNullOrEmpty(region) ? "" : " · " + region;
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
        if (speedDropdown != null && speedDropdown.value != speedIndex)
            speedDropdown.SetValueWithoutNotify(speedIndex);
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
        UpdateControls();
    }

    void OnSpeedChanged(int index)
    {
        speedIndex = Mathf.Clamp(index, 0, SpeedSeconds.Length - 1);
        if (player != null) player.secondsPerRound = SpeedSeconds[speedIndex];
    }

    bool IsReplayPausedForHud()
    {
        return player != null && !player.IsPlaying && !sliderHeld && !playStateCaptured;
    }

    static bool IsPresentationActiveForHud()
    {
        return LycheeVideoInterlude.IsPresentationActive ||
            LycheeVideoInterlude.IsPresentationVisible ||
            LycheeOpeningShowcase.AnyShowing;
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
        // 用户(点7)：最上面的「鲜度骤减」顶部提示(toast + 描边闪)取消——底部维度面板的鲜度已能看出下降。
        // 仅保留「好果转坏」这种不可逆损失的顶部提示(它在底部看不出来)。
        if (message.type != "GOOD_TO_BAD") return;
        LycheeReplayPayload payload = message.payload;
        int playerId = payload == null ? 0 : payload.playerId;
        int camp = FindCamp(playerId);
        string team = player != null ? player.TeamDisplayName(playerId) : "队伍";
        RaiseFreshnessAlert(camp, round, team + " 好果转坏 · 坏果 +1");
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
        if (type.Contains("GUARD") || type.Contains("FORCED") || type.Contains("CONTEST") || type.Contains("CARD") ||
            type.Contains("OBSTACLE") || type.Contains("INVALID")) return Vermilion;
        if (type.Contains("TASK") || type.Contains("BOUNTY")) return GoldTitle;
        if (type.Contains("RESOURCE") || type.Contains("DELIVER") || type.Contains("VERIFY") || type.Contains("PROCESS") ||
            type.Contains("RUSH")) return SilkText;
        return DarkGoldEdge;
    }

    static string CategoryIcon(string type)
    {
        if (string.IsNullOrEmpty(type)) return "◆";
        if (type.Contains("WEATHER")) return "☁";
        if (type.Contains("GUARD") || type.Contains("FORCED") || type.Contains("CONTEST") || type.Contains("CARD") ||
            type.Contains("OBSTACLE")) return "⚔";
        if (type.Contains("TASK") || type.Contains("BOUNTY")) return "★";
        if (type.Contains("DELIVER") || type.Contains("VERIFY")) return "✓";
        return "◆";
    }

    static readonly Dictionary<string, Sprite> feedIconCache = new Dictionary<string, Sprite>();

    /// <summary>事件类型 → 事件流图标文件（相对 UITheme）；无专属图标返回 null（走文字符号兜底）。</summary>
    static string FeedIconPath(string t, LycheeReplayPayload payload = null)
    {
        if (string.IsNullOrEmpty(t)) return null;
        if (t.Contains("WEATHER")) return WeatherIconFile(payload == null ? null : payload.weatherType);
        string mapped = LycheeEventIconMap.ResolveFile(t, false);
        if (!string.IsNullOrEmpty(mapped)) return mapped;
        if (t == "MATCH_START" || t == "NODE_ENTER") return "icon_task.png";
        if (t.Contains("WINDOW") || t.Contains("CONTEST") || t.Contains("CARD")) return "EventIcons/icon_event_contest_swords.png";
        if (t == "OBSTACLE_CLEAR" || t == "SQUAD_CLEAR" || t == "SQUAD_CLEAR_SUCCESS") return "EventIcons/icon_event_clear_obstacle.png";
        if (t == "OBSTACLE_RESIDUAL_TAX") return "EventIcons/icon_event_residual_tax.png";   // 残留税专属图标(JSON 未载时兜底)
        if (t == "GUARD_SET") return "EventIcons/icon_event_guard_set.png";
        if (t == "GUARD_BREAK") return "EventIcons/icon_event_guard_break.png";
        if (t == "SQUAD_DISPATCH") return "EventIcons/icon_event_squad_dispatch.png";
        if (t == "SQUAD_ARRIVE") return "EventIcons/icon_event_squad_arrive.png";
        if (t == "SQUAD_FAILED") return "EventIcons/icon_event_squad_fail.png";
        if (t.StartsWith("SQUAD_")) return "EventIcons/icon_event_squad_dispatch.png";
        if (t == "RESOURCE_CLAIM" || t == "RESOURCE_USE") return "EventIcons/icon_event_resource_claim.png";
        if (t.StartsWith("PROCESS_")) return "icon_task.png";
        if (t.StartsWith("RUSH_")) return "EventIcons/icon_event_contest_swords.png";
        if (t == "ACTION_REJECTED") return "EventIcons/icon_event_action_reject.png"; // 行动被拒专属图(用户新做，区别于无效)
        if (t == "INVALID_ACTION") return "EventIcons/icon_event_action_invalid.png"; // 与 event_icon_mapping.json 对齐
        if (t == "FRESHNESS_DROP" || t == "GOOD_TO_BAD") return "icon_resource_icebox.png";
        if (t.StartsWith("COST_")) return "icon_resource_passtoken.png";
        if (t == "MATCH_END") return "EventIcons/icon_event_match_end.png"; // 与 JSON 对齐
        if (t.Contains("VERIFY")) return "EventIcons/icon_event_verify_gate.png";
        if (t.Contains("FORCED")) return "EventIcons/icon_event_forced_pass.png";
        if (t.Contains("DELIVER")) return "EventIcons/icon_event_delivery_success.png";
        if (t.Contains("BOUNTY")) return "icon_bounty.png";
        if (t.Contains("TASK")) return "icon_task.png";
        if (t.Contains("GUARD")) return "icon_guard.png";
        return null;
    }

    static string WeatherIconFile(string weatherType)
    {
        string type = string.IsNullOrEmpty(weatherType) ? string.Empty : weatherType.Trim().ToUpperInvariant();
        if (type == "HOT") return "icon_weather_hot.png";
        if (type == "MOUNTAIN_FOG") return "icon_weather_fog.png";
        if (type == "HEAVY_RAIN") return "icon_weather_rain.png";
        return "icon_weather_rain.png";
    }

    static Sprite FeedIconSprite(string iconPath)
    {
        if (string.IsNullOrEmpty(iconPath)) return null;
        if (feedIconCache.TryGetValue(iconPath, out Sprite cached)) return cached;
        Sprite sprite = LycheeLoadThemeSprite(iconPath);
        // WebGL 下 UITheme 是异步预加载；预加载完成前不要把一次空读写死进缓存。
        if (sprite != null || LycheeThemeCache.Preloaded) feedIconCache[iconPath] = sprite;
        return sprite;
    }

    static void SetRawImageSprite(RawImage image, Sprite sprite)
    {
        if (image == null) return;
        if (sprite == null || sprite.texture == null)
        {
            image.texture = null;
            image.enabled = false;
            return;
        }

        image.texture = sprite.texture;
        image.uvRect = SpriteUv(sprite);
        image.color = Color.white;
        image.enabled = true;
    }

    static Rect SpriteUv(Sprite sprite)
    {
        if (sprite == null || sprite.texture == null) return new Rect(0f, 0f, 1f, 1f);
        Rect r = sprite.rect;
        Texture2D texture = sprite.texture;
        if (texture.width <= 0 || texture.height <= 0) return new Rect(0f, 0f, 1f, 1f);
        return new Rect(r.x / texture.width, r.y / texture.height, r.width / texture.width, r.height / texture.height);
    }

    public static string EventNameCn(string type)
    {
        switch (type)
        {
            case "MATCH_START": return "比赛开始";
            case "MATCH_END": return "比赛结束";
            case "NODE_ENTER": return "进入节点";
            case "TASK_SPAWN": return "皇榜任务刷新";
            case "TASK_REFRESH": return "皇榜任务刷新";
            case "TASK_COMPLETE": return "任务完成";
            case "TASK_EXPIRE": return "任务过期";
            case "WEATHER_FORECAST": return "天气预告";
            case "WEATHER_START": return "天气生效";
            case "WEATHER_END": return "天气结束";
            case "RESOURCE_CLAIM": return "资源获取";
            case "RESOURCE_USE": return "资源使用";
            case "PROCESS_START": return "处理开始";
            case "PROCESS_PROGRESS": return "处理中";
            case "PROCESS_COMPLETE": return "处理完成";
            case "SQUAD_DISPATCH": return "小队出发";
            case "SQUAD_ARRIVE": return "小队抵达";
            case "SQUAD_CLEAR": return "清障完成";
            case "SQUAD_WEAKEN":                 // V4.8.12 裸名
            case "SQUAD_WEAKEN_SUCCESS": return "削弱奏效";
            case "SQUAD_REINFORCE":             // V4.8.12 裸名
            case "SQUAD_REINFORCE_SUCCESS": return "增援奏效";
            case "SQUAD_CLEAR_SUCCESS": return "清障完成";
            case "SQUAD_SCOUT": return "派出探路";              // C8: 裸名=刚派出，不能报"完成"(原误显"侦察完成"语义反)
            case "SQUAD_SCOUT_SUCCESS": return "探路完成";   // E5(确认口径): 统一"探路"，不用"侦察/侦路"
            case "SQUAD_FAILED": return "小队失败";
            case "GUARD_SET": return "设卡布防";
            case "GUARD_WEATHERING":             // V4.8.12 新名（旧 GUARD_DECAY）
            case "GUARD_DECAY": return "设卡风化";
            case "GUARD_BREAK": return "攻破设卡";
            case "OBSTACLE_CLEAR": return "障碍清除";
            case "BOUNTY_CREATE":                // V4.8.12 新名（旧 BOUNTY_SPAWN）
            case "BOUNTY_SPAWN": return "悬赏出现";
            case "BOUNTY_CLAIM":                 // V4.8.12 新名（旧 BOUNTY_COMPLETE）
            case "BOUNTY_COMPLETE": return "悬赏完成";
            case "COST_COMMIT": return "代价投入";
            case "FORCED_PASS_START": return "强制通行";
            case "FORCED_PASS_END": return "强制通行结束";   // C9: 与配对的"强制通行"成对，不漏"强制"
            case "ACTION_REJECTED": return "行动被拒";
            case "INVALID_ACTION": return "行动无效";
            case "RUSH_PHASE_START": return "宫宴冲刺开启";
            case "RUSH_START": return "宫宴冲刺开启";
            case "RUSH_TACTIC_USE": return "冲刺策略";
            case "VERIFY_GATE_START": return "验核开始";
            case "VERIFY_GATE_COMPLETE": return "验核完成";
            case "DELIVER_SUCCESS": return "交付成功";
            case "GOOD_TO_BAD": return "好果转坏";
            case "FRESHNESS_DROP": return "鲜度骤降";
            case "WINDOW_CONTEST_START": return "对抗开始";
            case "WINDOW_CARD_REVEAL": return "对抗出牌";
            case "WINDOW_CONTEST_END": return "对抗结束";
            case "WINDOW_CONTEST_DRAW": return "窗口平局";   // V4.8.12 三拍胜点相同、无胜方
            // ↓ V4.8.12 补全（原先落英文兜底）
            case "WAIT": return "等待";
            case "MOVE_PROGRESS": return "移动推进";
            case "COST_BANKRUPT": return "冻结好果清算";
            case "GOOD_FRUIT_SCRAP": return "好果报废";
            case "BUFF_OVERWRITE": return "增益覆盖";
            case "DISCONNECT_WARNING": return "断线警告";
            case "PLAYER_RETIRED": return "玩家退赛";
            case "RETIREMENT_CLEANUP": return "退赛清理";
            case "VERIFY_GATE_ALREADY_DONE": return "宫门已验核";
            case "OBSTACLE_RESIDUAL_TAX": return "清障残留税";
            case "WINDOW_EFFECTIVE_COMBAT": return "有效对抗";
            case "WINDOW_CONTEST_REPEAT_SUPPRESSED": return "重复窗口抑制";
            case "GATE_CONTEST_WIN": return "宫门争夺胜出";
            case "TASK_CONTEST_WIN": return "任务争夺胜出";
            case "OBSTACLE_CONTEST_WIN": return "障碍争夺胜出";
            case "DOCK_CONTEST_WIN": return "处理点争夺胜出";
            case "PASS_CONTEST_WIN": return "强制通行胜出";
            case "PASS_CONTEST_DEFENDED": return "守卡成功";
            case "SCOUT_MARKER_ADD": return "探路标记添加";
            case "SCOUT_MARKER_APPLY": return "探路标记生效";   // E5(确认口径)统一措辞
            case "SCOUT_MARKER_CONSUME": return "探路标记消耗";
            case "SCOUT_MARKER_EXPIRE": return "探路标记过期";
            case "TASK_TARGET_LOST": return "任务目标丢失";
            case "TASK_PROTECTION_START": return "任务保护开始";
            case "BOUNTY_EXPIRE": return "悬赏失效";
            case "BREAK_ORDER_BIND": return "破关令绑定";
            case "POST_DELIVER_PENALTY": return "交付后扣分";
        }
        if (!string.IsNullOrEmpty(type) && type.StartsWith("SQUAD_", StringComparison.Ordinal)) return "小队行动";
        if (!string.IsNullOrEmpty(type) && type.StartsWith("SCOUT_", StringComparison.Ordinal)) return "探路标记";
        return "事件";   // 兜底改中文：未知事件不再显英文裸名
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

        // 富文本标签 <…>(如 <color=#E0604C>红队</color>)不计入可见宽度，否则会把宽度算爆、提前截断可见文字。
        float total = 0f;
        for (int i = 0; i < label.Length; i++)
        {
            if (label[i] == '<') { int e = label.IndexOf('>', i); if (e >= 0) { i = e; continue; } }
            total += CharAdvance(textFont, label[i], size, style);
        }
        if (total <= maxWidth) return label;

        float budget = Mathf.Max(0f, maxWidth - CharAdvance(textFont, ellipsis, size, style));
        float used = 0f;
        int keep = 0;
        while (keep < label.Length)
        {
            if (label[keep] == '<')
            {
                int e = label.IndexOf('>', keep);
                if (e >= 0) { keep = e + 1; continue; }   // 整段标签计 0 宽、整体保留，截断不会把标签切碎
            }
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
            if (!texture.LoadImage(bytes, markNonReadable: true)) return null;
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
            if (!texture.LoadImage(bytes, markNonReadable: true)) return null;
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

    Dropdown MakeSpeedDropdown(RectTransform parent, Vector2 anchoredPosition, Vector2 size)
    {
        GameObject go = new GameObject("SpeedDropdown", typeof(RectTransform), typeof(Image), typeof(Dropdown));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        SetRect(rt, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), anchoredPosition, size);

        Image image = go.GetComponent<Image>();
        image.color = new Color(0.16f, 0.14f, 0.11f, 0.97f);
        Outline outline = go.AddComponent<Outline>();
        outline.effectColor = new Color(DarkGoldEdge.r, DarkGoldEdge.g, DarkGoldEdge.b, 0.72f);
        outline.effectDistance = new Vector2(1f, -1f);

        Text caption = MakeText("Label", rt, 20, FontStyle.Bold, GoldTitle, TextAnchor.MiddleCenter);
        SetRect(caption.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), new Vector2(-8f, 0f), new Vector2(-28f, 0f));
        caption.text = SpeedLabels[speedIndex];

        Text arrow = MakeText("Arrow", rt, 18, FontStyle.Bold, GoldTitle, TextAnchor.MiddleCenter);
        SetRect(arrow.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(-8f, 0f), new Vector2(22f, 0f));
        arrow.text = "v";

        RectTransform template = MakeRect("Template", rt);
        SetRect(template, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 1f), new Vector2(0f, -4f), new Vector2(0f, 126f));
        Image templateBg = template.gameObject.AddComponent<Image>();
        templateBg.color = new Color(0.09f, 0.08f, 0.06f, 0.98f);
        Outline templateOutline = template.gameObject.AddComponent<Outline>();
        templateOutline.effectColor = new Color(DarkGoldEdge.r, DarkGoldEdge.g, DarkGoldEdge.b, 0.72f);
        templateOutline.effectDistance = new Vector2(1f, -1f);
        ScrollRect scroll = template.gameObject.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;

        RectTransform viewport = MakeRect("Viewport", template);
        SetRect(viewport, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        Image viewportImage = viewport.gameObject.AddComponent<Image>();
        viewportImage.color = new Color(0f, 0f, 0f, 0.05f);
        Mask mask = viewport.gameObject.AddComponent<Mask>();
        mask.showMaskGraphic = false;

        RectTransform content = MakeRect("Content", viewport);
        float rowHeight = 30f;
        SetRect(content, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, SpeedLabels.Length * rowHeight));

        RectTransform item = MakeRect("Item", content);
        SetRect(item, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -rowHeight * 0.5f), new Vector2(0f, rowHeight));
        Image itemBg = item.gameObject.AddComponent<Image>();
        itemBg.color = new Color(0.16f, 0.12f, 0.09f, 0.92f);
        Toggle itemToggle = item.gameObject.AddComponent<Toggle>();
        itemToggle.targetGraphic = itemBg;

        RectTransform checkmark = MakeRect("Item Checkmark", item);
        SetRect(checkmark, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(8f, 0f), new Vector2(10f, 10f));
        Image checkImage = checkmark.gameObject.AddComponent<Image>();
        checkImage.color = GoldTitle;
        itemToggle.graphic = checkImage;

        Text itemLabel = MakeText("Item Label", item, 18, FontStyle.Bold, GoldTitle, TextAnchor.MiddleCenter);
        SetRect(itemLabel.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), new Vector2(6f, 0f), new Vector2(-24f, 0f));

        scroll.viewport = viewport;
        scroll.content = content;
        template.gameObject.SetActive(false);

        Dropdown dropdown = go.GetComponent<Dropdown>();
        dropdown.targetGraphic = image;
        dropdown.template = template;
        dropdown.captionText = caption;
        dropdown.itemText = itemLabel;
        dropdown.options.Clear();
        for (int i = 0; i < SpeedLabels.Length; i++)
            dropdown.options.Add(new Dropdown.OptionData(SpeedLabels[i]));
        dropdown.SetValueWithoutNotify(speedIndex);
        dropdown.RefreshShownValue();
        dropdown.onValueChanged.AddListener(OnSpeedChanged);
        return dropdown;
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
        labelText.resizeTextForBestFit = true;
        labelText.resizeTextMinSize = 13;
        labelText.resizeTextMaxSize = 20;
        return button;
    }
}
