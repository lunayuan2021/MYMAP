using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[ExecuteAlways]
public sealed class LycheeReplayRuntimeHud : MonoBehaviour
{
    // 排队中的事件弹窗内容快照：普通事件在面板占用期间不再直接覆盖（原“最后写入者赢”
    // 导致密集回合下弹窗每帧闪变、永驻屏幕），而是先存进队列等空档再弹。
    sealed class PendingCombat
    {
        public int imageIndex;
        public string title;
        public string subtitle;
        public string redCard;
        public string blueCard;
        public string score;
        public float seconds;
    }

    sealed class TeamWidgets
    {
        public Text name;
        public Text roster;
        public Text score;
        public Image avatar;
    }

    sealed class DualBar
    {
        public Image redFill;
        public Image blueFill;
        public Text redValue;
        public Text blueValue;
        public Text redDelta;
        public Text blueDelta;
        public float maxValue;
    }

    const string CanvasName = "LycheeReplayRuntimeHUD_20260612";
    const float BottomWidth = 1180f;
    // 面板加高（164 -> 210）：让四条进度条完整收进背景内，并保证内容距背景边缘 >= 16px 留白。
    const float BottomHeight = 210f;
    // LOL 取向：面板贴画面下边缘（原 0.25 悬浮在画面 1/4 处）
    const float BottomHudAnchorY = 0f;
    // 红蓝双条 track 宽度；条内端距中央标签、外端距数值文字均保持 >= 6px 净空。
    const float BarTrackWidth = 190f;
    // 事件弹窗单条时长硬上限：上游传入 3.8~5.5 秒，在 0.025~0.2 秒/回合的回放节奏下会长期霸屏。
    const float CombatMaxSeconds = 3f;
    // 弹窗淡入淡出时长：取代原 alpha 1↔0 硬切，观赏性显著提升。
    const float CombatFadeSeconds = 0.25f;
    // 两条普通弹窗之间的最小喘息间隔（上一条隐藏后再等这么久才弹队首）。
    const float CombatGapSeconds = 0.9f;
    // 排队上限：再多就丢最旧的，保证弹窗内容不会落后画面太久。
    const int CombatQueueLimit = 4;

    [Header("Editor Preview")]
    public bool previewInEditMode = true;
    public Sprite previewRedAvatar;
    public Sprite previewBlueAvatar;
    public Sprite[] previewCombatIllustrations;

    Font font;
    Canvas canvas;
    RectTransform root;
    TeamWidgets redTeam;
    TeamWidgets blueTeam;
    Text roundText;
    Text phaseText;
    // 原中央“最新事件”文本（EventText）已删除：它与四条进度条的中央标签文字重叠，
    // 且左侧事件流（LycheeHudExtensions）已承担最新事件展示职责。
    DualBar freshBar;
    DualBar fruitBar;
    DualBar squadBar;
    DualBar scoreBar;
    CanvasGroup combatGroup;
    Image combatImage;
    Text combatTitle;
    Text combatSubtitle;
    Text combatRedCard;
    Text combatBlueCard;
    Text combatScore;
    CanvasGroup openingGroup;
    Text openingTitle;
    Text openingRed;
    Text openingBlue;
    CanvasGroup toastGroup;
    Text toastText;
    Sprite[] combatSprites;

    float combatUntil;
    // 下一条普通弹窗最早允许出现的时刻（= 上一条隐藏时刻 + CombatGapSeconds）。
    float combatNextAllowed;
    // 普通事件弹窗等待队列；对抗三连击（开始/亮牌/结算）走 priority 路径绕过它。
    readonly List<PendingCombat> pendingCombats = new List<PendingCombat>();
    float openingUntil;
    float toastUntil;
    int lastRound = -1;
    float lastRedFresh;
    float lastBlueFresh;
    int lastRedFruit;
    int lastBlueFruit;
    int lastRedSquad;
    int lastBlueSquad;
    int lastRedScore;
    int lastBlueScore;
#if UNITY_EDITOR
    bool editorPreviewQueued;
#endif

    void OnEnable()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying && previewInEditMode)
            QueueEditorPreview();
#endif
    }

    void OnDisable()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying)
            ClearBuiltCanvas();
#endif
    }

    void OnValidate()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying && isActiveAndEnabled && previewInEditMode)
            QueueEditorPreview();
#endif
    }

#if UNITY_EDITOR
    void QueueEditorPreview()
    {
        if (editorPreviewQueued) return;
        editorPreviewQueued = true;
        UnityEditor.EditorApplication.delayCall += RunQueuedEditorPreview;
    }

    void RunQueuedEditorPreview()
    {
        editorPreviewQueued = false;
        if (this == null || Application.isPlaying || !isActiveAndEnabled || !previewInEditMode)
            return;

        ClearBuiltCanvas();
        ShowEditorPreview();
    }
#endif

    public void Configure(Sprite[] illustrations, Sprite redAvatar, Sprite blueAvatar)
    {
        EnsureBuilt();
        if (illustrations != null && illustrations.Length > 0) previewCombatIllustrations = illustrations;
        if (redAvatar != null) previewRedAvatar = redAvatar;
        if (blueAvatar != null) previewBlueAvatar = blueAvatar;
        combatSprites = previewCombatIllustrations;
        if (redTeam.avatar != null) redTeam.avatar.sprite = previewRedAvatar;
        if (blueTeam.avatar != null) blueTeam.avatar.sprite = previewBlueAvatar;
    }

    public void ShowEditorPreview()
    {
        EnsureBuilt();
        combatSprites = previewCombatIllustrations;
        if (redTeam.avatar != null) redTeam.avatar.sprite = previewRedAvatar;
        if (blueTeam.avatar != null) blueTeam.avatar.sprite = previewBlueAvatar;

        roundText.text = "第 1 / 600 回合";
        phaseText.text = "Replay 预览";
        redTeam.name.text = "红队";
        redTeam.roster.text = "16人  队长1 / 队员15";
        redTeam.score.text = "0";
        blueTeam.name.text = "蓝队";
        blueTeam.roster.text = "16人  队长1 / 队员15";
        blueTeam.score.text = "0";
        UpdateBar(freshBar, 92f, 88f, "92%", "88%", 0f, 0f, "%");
        UpdateBar(fruitBar, 36f, 32f, "36", "32", 0f, 0f, "");
        UpdateBar(squadBar, 8f, 8f, "8", "8", 0f, 0f, "");
        UpdateBar(scoreBar, 0f, 0f, "0", "0", 0f, 0f, "");
        SetGroup(combatGroup, false, 0f);
        SetGroup(openingGroup, false, 0f);
        SetGroup(toastGroup, false, 0f);
    }

    public void ShowOpening(string redName, int redCount, string blueName, int blueCount)
    {
        // 开幕阵容面板（OpeningRosterPanel）已被全屏对阵演出 LycheeOpeningShowcase 取代。
        // 保留方法签名以兼容既有调用方，这里直接空操作，避免两层开场 UI 叠加。
    }

    // 对抗三连击（开始/亮牌/结算）是同一场叙事的连续镜头，priority=true 绕过队列实时覆盖；
    // 时长从 4.6/4.9/5.2 压到 2.8/2.8/3.0，避免叙事链拖到下一场对抗还没放完。
    public void ShowCombatStart(int round, string nodeId, int totalRounds)
    {
        ShowCombat(0, "对抗开始  第" + round + "回合", "争夺节点 " + Clean(nodeId) + "  ·  " + Mathf.Max(1, totalRounds) + "局对抗", "红队准备", "蓝队准备", "0 : 0", 2.8f, true);
    }

    public void ShowCombatReveal(int round, int roundIndex, string redCard, string blueCard, string winner, int redPoint, int bluePoint)
    {
        string winnerText = WinnerText(winner);
        ShowCombat(1, "对抗第" + Mathf.Max(1, roundIndex) + "局  第" + round + "回合", winnerText + " 得势，状态条同步更新", CardText(redCard), CardText(blueCard), redPoint + " : " + bluePoint, 2.8f, true);
    }

    public void ShowCombatEnd(int round, string winner, int redPoint, int bluePoint)
    {
        ShowCombat(2, "对抗结束  第" + round + "回合", WinnerText(winner) + " 胜出", "红队积分 " + redPoint, "蓝队积分 " + bluePoint, redPoint + " : " + bluePoint, 3.0f, true);
    }

    public void ShowEventIllustration(int imageIndex, string title, string subtitle, string leftDetail, string rightDetail, string centerText, float seconds)
    {
        // 普通事件走排队路径：面板被占用或未到间隔时先入队，避免连弹覆盖
        ShowCombat(imageIndex, title, subtitle, leftDetail, rightDetail, centerText, seconds, false);
    }

    public void ShowDispatch(string text)
    {
        EnsureBuilt();
        toastText.text = text;
        // 4.2 秒压到 3.0 秒：调度提示信息量小，停留太久反而显得画面“糊”着一层字
        toastUntil = Time.unscaledTime + 3.0f;
        SetGroup(toastGroup, true, 1f);
    }

    // 回放跳转（拖进度条、重播按钮、循环回开头）后调用：排队中的弹窗与在显内容都指向跳转前
    // 的旧进度，继续补播只会报错位的回合号；全部作废并解除间隔锁，让跳转目标帧重放出的事件
    // 能即时弹出，而不是被旧的 combatNextAllowed 压回队列。
    public void CancelCombatPopups()
    {
        pendingCombats.Clear();
        combatUntil = 0f;
        combatNextAllowed = 0f;
    }

    public void Refresh(
        int round,
        int lastReplayRound,
        string phase,
        string latestEvent,
        LycheeReplayPlayerState red,
        LycheeReplayPlayerState blue,
        int redCount,
        int blueCount,
        LycheeReplayScores scores)
    {
        EnsureBuilt();

        int redScore = scores == null ? (red == null ? 0 : red.score) : scores.RED;
        int blueScore = scores == null ? (blue == null ? 0 : blue.score) : scores.BLUE;
        bool newRound = round != lastRound;

        roundText.text = "第 " + round + " / " + lastReplayRound + " 回合";
        phaseText.text = phase == "RUSH" ? "冲刺阶段" : "常规阶段";
        // latestEvent 不再在底部面板展示（参数保留以兼容调用方）：
        // 原 EventText 与进度条标签重叠，左侧事件流已承担该职责。

        if (red != null)
        {
            redTeam.name.text = "红队  " + red.name;
            redTeam.roster.text = redCount + "人  队长1 / 队员" + Mathf.Max(0, redCount - 1);
            redTeam.score.text = redScore.ToString();
        }
        if (blue != null)
        {
            blueTeam.name.text = "蓝队  " + blue.name;
            blueTeam.roster.text = blueCount + "人  队长1 / 队员" + Mathf.Max(0, blueCount - 1);
            blueTeam.score.text = blueScore.ToString();
        }

        float redFresh = red == null ? 0f : red.freshness;
        float blueFresh = blue == null ? 0f : blue.freshness;
        int redFruit = red == null ? 0 : red.goodFruit;
        int blueFruit = blue == null ? 0 : blue.goodFruit;
        int redSquad = red == null ? 0 : red.squadAvailable;
        int blueSquad = blue == null ? 0 : blue.squadAvailable;

        UpdateBar(freshBar, redFresh, blueFresh, redFresh.ToString("0") + "%", blueFresh.ToString("0") + "%", newRound ? redFresh - lastRedFresh : 0f, newRound ? blueFresh - lastBlueFresh : 0f, "%");
        UpdateBar(fruitBar, redFruit, blueFruit, redFruit.ToString(), blueFruit.ToString(), newRound ? redFruit - lastRedFruit : 0, newRound ? blueFruit - lastBlueFruit : 0, "");
        UpdateBar(squadBar, redSquad, blueSquad, redSquad.ToString(), blueSquad.ToString(), newRound ? redSquad - lastRedSquad : 0, newRound ? blueSquad - lastBlueSquad : 0, "");
        UpdateBar(scoreBar, redScore, blueScore, redScore.ToString(), blueScore.ToString(), newRound ? redScore - lastRedScore : 0, newRound ? blueScore - lastBlueScore : 0, "");

        if (newRound)
        {
            lastRound = round;
            lastRedFresh = redFresh;
            lastBlueFresh = blueFresh;
            lastRedFruit = redFruit;
            lastBlueFruit = blueFruit;
            lastRedSquad = redSquad;
            lastBlueSquad = blueSquad;
            lastRedScore = redScore;
            lastBlueScore = blueScore;
        }

        float now = Time.unscaledTime;

        // 排队消费点：Refresh 每帧必经。面板熄灭且过了喘息间隔后，再弹下一条排队事件。
        if (now >= combatUntil && now >= combatNextAllowed && pendingCombats.Count > 0)
        {
            PendingCombat next = pendingCombats[0];
            pendingCombats.RemoveAt(0);
            DisplayCombat(next.imageIndex, next.title, next.subtitle, next.redCard, next.blueCard, next.score, next.seconds);
        }

        // 对抗弹窗用 0.25 秒淡入淡出取代原 alpha 硬切；面板纯展示无交互，
        // 永远不拦截 raycast（否则右侧 400x252 区域会吃掉穿透点击）。
        combatGroup.alpha = Mathf.MoveTowards(combatGroup.alpha, now < combatUntil ? 1f : 0f, Time.unscaledDeltaTime / CombatFadeSeconds);
        combatGroup.interactable = false;
        combatGroup.blocksRaycasts = false;
        SetGroup(openingGroup, now < openingUntil && round <= 24, now < openingUntil && round <= 24 ? 1f : 0f);
        SetGroup(toastGroup, now < toastUntil, now < toastUntil ? 1f : 0f);
    }

    void ShowCombat(int imageIndex, string title, string subtitle, string redCard, string blueCard, string score, float seconds, bool priority)
    {
        EnsureBuilt();
        // 时长一刀切上限：上游各处传入 3.8~5.5 秒，密集事件下会让面板长期霸屏
        seconds = Mathf.Min(seconds, CombatMaxSeconds);
        float now = Time.unscaledTime;

        // 普通事件在面板被占用（仍可见）或未到喘息间隔时先入队；
        // 对抗三连击（priority=true）是连续叙事，必须实时覆盖，绕过队列直接显示。
        if (!priority && (now < combatUntil || now < combatNextAllowed))
        {
            EnqueueCombat(imageIndex, title, subtitle, redCard, blueCard, score, seconds);
            return;
        }
        DisplayCombat(imageIndex, title, subtitle, redCard, blueCard, score, seconds);
    }

    void EnqueueCombat(int imageIndex, string title, string subtitle, string redCard, string blueCard, string score, float seconds)
    {
        // 连续同标题去重：密集回合里同类事件被反复触发，重复排队没有信息量
        string lastTitle = pendingCombats.Count > 0 ? pendingCombats[pendingCombats.Count - 1].title : combatTitle.text;
        if (lastTitle == title)
            return;
        // 超过上限丢最旧的：宁可漏掉旧事件，也不让弹窗内容落后画面太久
        if (pendingCombats.Count >= CombatQueueLimit)
            pendingCombats.RemoveAt(0);
        pendingCombats.Add(new PendingCombat
        {
            imageIndex = imageIndex,
            title = title,
            subtitle = subtitle,
            redCard = redCard,
            blueCard = blueCard,
            score = score,
            seconds = seconds,
        });
    }

    void DisplayCombat(int imageIndex, string title, string subtitle, string redCard, string blueCard, string score, float seconds)
    {
        if (combatSprites != null && combatSprites.Length > 0)
            combatImage.sprite = combatSprites[Mathf.Clamp(imageIndex, 0, combatSprites.Length - 1)];
        combatTitle.text = title;
        combatSubtitle.text = subtitle;
        combatRedCard.text = redCard;
        combatBlueCard.text = blueCard;
        combatScore.text = score;
        combatUntil = Time.unscaledTime + seconds;
        // 下一条普通弹窗最早出现时刻 = 本条隐藏后再留一段喘息间隔
        combatNextAllowed = combatUntil + CombatGapSeconds;
        // 不再直接把 alpha 打到 1：交给 Refresh 里的渐变做淡入；纯展示面板永不拦截点击
        combatGroup.interactable = false;
        combatGroup.blocksRaycasts = false;
    }

    void Build()
    {
        ClearBuiltCanvas();
        // 优先用随包中文字体：内置 LegacyRuntime 缺中文字形，WebGL 上无法回退到系统字体
        font = Resources.Load<Font>("Fonts/LXGWWenKai-Regular");
        if (font == null)
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
            font = Font.CreateDynamicFontFromOSFont(new[] { "PingFang SC", "Heiti SC", "Arial" }, 16);

        GameObject canvasObject = new GameObject(CanvasName, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        if (!Application.isPlaying)
            canvasObject.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
        canvasObject.transform.SetParent(transform, false);
        root = canvasObject.GetComponent<RectTransform>();
        canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 130;

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        BuildBottomHud(root);
        BuildCombat(root);
        BuildOpening(root);
        BuildToast(root);
    }

    void EnsureBuilt()
    {
        if (canvas != null && redTeam != null && blueTeam != null && roundText != null && freshBar != null && combatGroup != null && openingGroup != null && toastGroup != null)
            return;
        Build();
    }

    void ClearBuiltCanvas()
    {
        GameObject target = root == null ? null : root.gameObject;
        if (target == null)
        {
            Transform child = transform.Find(CanvasName);
            if (child != null) target = child.gameObject;
        }

        canvas = null;
        root = null;
        redTeam = null;
        blueTeam = null;
        roundText = null;
        phaseText = null;
        freshBar = null;
        fruitBar = null;
        squadBar = null;
        scoreBar = null;
        combatGroup = null;
        combatImage = null;
        combatTitle = null;
        combatSubtitle = null;
        combatRedCard = null;
        combatBlueCard = null;
        combatScore = null;
        openingGroup = null;
        openingTitle = null;
        openingRed = null;
        openingBlue = null;
        toastGroup = null;
        toastText = null;
        // 画布重建时清掉排队中的旧弹窗，避免重建后把过期事件补播出来
        pendingCombats.Clear();
        combatNextAllowed = 0f;

        if (target == null) return;
        if (Application.isPlaying) Destroy(target);
        else DestroyImmediate(target);
    }

    void BuildBottomHud(Transform parent)
    {
        // 垂直布局（自面板底边起，面板高 210）：
        //   回合标题 166..194 / 阶段文字 140..160 / 四条进度条行中心 120, 90, 60, 30（行高 30，行间净空 8）。
        // 最底行文字下缘 19px、最顶行标题上缘 16px，均满足内容距背景边缘 >= 16px。
        // LOL 风格：面板自身全透明贴底，只给「进度条块」和「回合标题」各留一小块底色，
        // 其余（头像/队名/比分）直接浮在画面上。
        RectTransform panel = Rect("BottomStatus_Runtime", parent, new Vector2(0.5f, BottomHudAnchorY), Vector2.zero, new Vector2(BottomWidth, BottomHeight), new Vector2(0.5f, 0f));

        RectTransform barsBackdrop = Rect("BarsBackdrop", panel, new Vector2(0.5f, 0f), new Vector2(0f, 8f), new Vector2(730f, 134f), new Vector2(0.5f, 0f));
        Image barsBg = barsBackdrop.gameObject.AddComponent<Image>();
        barsBg.color = new Color(0.015f, 0.018f, 0.014f, 0.62f);
        barsBg.raycastTarget = false;
        Rect("BarsTopLine", barsBackdrop, new Vector2(0.5f, 1f), new Vector2(0f, -1f), new Vector2(730f, 2f), new Vector2(0.5f, 1f)).gameObject.AddComponent<Image>().color = new Color(1f, 0.77f, 0.27f, 0.7f);

        RectTransform titleChip = Rect("TitleChip", panel, new Vector2(0.5f, 1f), new Vector2(0f, -6f), new Vector2(340f, 70f), new Vector2(0.5f, 1f));
        Image titleBg = titleChip.gameObject.AddComponent<Image>();
        titleBg.color = new Color(0.015f, 0.018f, 0.014f, 0.55f);
        titleBg.raycastTarget = false;

        roundText = Text("RoundText", panel, 22, FontStyle.Bold, new Color(1f, 0.88f, 0.58f, 1f), TextAnchor.MiddleCenter);
        SetRect(roundText.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -16f), new Vector2(320f, 28f), new Vector2(0.5f, 1f));
        phaseText = Text("PhaseText", panel, 15, FontStyle.Bold, new Color(0.78f, 0.88f, 1f, 1f), TextAnchor.MiddleCenter);
        SetRect(phaseText.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -50f), new Vector2(180f, 20f), new Vector2(0.5f, 1f));
        // 此处原有的 EventText（最新事件一行）已删除：与进度条中央标签重叠，
        // 左侧事件流（LycheeHudExtensions）已完整承担最新事件展示。

        redTeam = BuildTeam(panel, true);
        blueTeam = BuildTeam(panel, false);

        freshBar = BuildDualBar(panel, "鲜度", 100f, 120f);
        fruitBar = BuildDualBar(panel, "荔枝", 100f, 90f);
        squadBar = BuildDualBar(panel, "分队", 12f, 60f);
        scoreBar = BuildDualBar(panel, "得分", 60f, 30f);
    }

    TeamWidgets BuildTeam(Transform parent, bool red)
    {
        // 队伍块横向分区（红侧取负、蓝侧取正，自面板中心起）：
        //   头像 476..568（92px，含 4px 描边环至 472）/ 文本列 354..466（宽 112，与头像环净空 6px）。
        // 文本列三行与头像（中心 y=105）整体垂直居中：队名 129..153 / 小字 101..121 / 积分 57..93，行间净空 8px。
        // 文本列内缘 354 与进度条最外侧 Delta（344）保持 10px 净空，积分大数字不再压头像描边环。
        Color teamColor = red ? new Color(1f, 0.28f, 0.20f, 1f) : new Color(0.25f, 0.55f, 1f, 1f);
        float side = red ? -1f : 1f;
        TeamWidgets widgets = new TeamWidgets();

        RectTransform avatarRt = Rect(red ? "RedCaptainAvatar_Runtime" : "BlueCaptainAvatar_Runtime", parent, new Vector2(0.5f, 0f), new Vector2(side * 522f, 105f), new Vector2(92f, 92f), new Vector2(0.5f, 0.5f));
        widgets.avatar = avatarRt.gameObject.AddComponent<Image>();
        widgets.avatar.color = Color.white;
        widgets.avatar.preserveAspect = true;
        Outline outline = avatarRt.gameObject.AddComponent<Outline>();
        outline.effectColor = teamColor;
        outline.effectDistance = new Vector2(4f, -4f);

        float textX = side * 410f;
        TextAnchor align = red ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight;
        widgets.name = Text(red ? "RedName" : "BlueName", parent, 16, FontStyle.Bold, teamColor, align);
        SetRect(widgets.name.rectTransform, new Vector2(0.5f, 0f), new Vector2(textX, 141f), new Vector2(112f, 24f), new Vector2(0.5f, 0.5f));
        widgets.roster = Text(red ? "RedRoster" : "BlueRoster", parent, 12, FontStyle.Normal, new Color(0.88f, 0.90f, 0.82f, 0.82f), align);
        SetRect(widgets.roster.rectTransform, new Vector2(0.5f, 0f), new Vector2(textX, 111f), new Vector2(112f, 20f), new Vector2(0.5f, 0.5f));
        widgets.score = Text(red ? "RedScore" : "BlueScore", parent, 34, FontStyle.Bold, teamColor, align);
        SetRect(widgets.score.rectTransform, new Vector2(0.5f, 0f), new Vector2(textX, 75f), new Vector2(112f, 36f), new Vector2(0.5f, 0.5f));
        return widgets;
    }

    DualBar BuildDualBar(Transform parent, string label, float max, float y)
    {
        // 单条横向分区（红侧取负、蓝侧取正，自面板中心起）：
        //   中央标签 -36..36 / track 44..234（宽 190，距标签 8px）/ 数值 242..294（距条外端 8px）/ Delta 300..344（距数值 6px）。
        // 红侧数值右对齐固定在条左外侧，蓝侧数值左对齐固定在条右外侧，百分比/Delta 均不会压在填充条上。
        DualBar bar = new DualBar { maxValue = max };
        Text labelText = Text(label + "Label", parent, 14, FontStyle.Bold, new Color(0.98f, 0.90f, 0.68f, 1f), TextAnchor.MiddleCenter);
        SetRect(labelText.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, y), new Vector2(72f, 22f), new Vector2(0.5f, 0.5f));
        bar.redFill = BarFill(parent, label + "RedTrack", new Vector2(-139f, y), new Color(0.95f, 0.18f, 0.11f, 0.94f));
        bar.blueFill = BarFill(parent, label + "BlueTrack", new Vector2(139f, y), new Color(0.20f, 0.48f, 1f, 0.94f));
        bar.redValue = Text(label + "RedValue", parent, 12, FontStyle.Bold, Color.white, TextAnchor.MiddleRight);
        SetRect(bar.redValue.rectTransform, new Vector2(0.5f, 0f), new Vector2(-242f, y), new Vector2(52f, 22f), new Vector2(1f, 0.5f));
        bar.blueValue = Text(label + "BlueValue", parent, 12, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft);
        SetRect(bar.blueValue.rectTransform, new Vector2(0.5f, 0f), new Vector2(242f, y), new Vector2(52f, 22f), new Vector2(0f, 0.5f));
        bar.redDelta = Text(label + "RedDelta", parent, 11, FontStyle.Bold, Color.white, TextAnchor.MiddleRight);
        SetRect(bar.redDelta.rectTransform, new Vector2(0.5f, 0f), new Vector2(-300f, y), new Vector2(44f, 22f), new Vector2(1f, 0.5f));
        bar.blueDelta = Text(label + "BlueDelta", parent, 11, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft);
        SetRect(bar.blueDelta.rectTransform, new Vector2(0.5f, 0f), new Vector2(300f, y), new Vector2(44f, 22f), new Vector2(0f, 0.5f));
        return bar;
    }

    Image BarFill(Transform parent, string name, Vector2 position, Color color)
    {
        RectTransform track = Rect(name, parent, new Vector2(0.5f, 0f), position, new Vector2(BarTrackWidth, 13f), new Vector2(0.5f, 0.5f));
        track.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.13f);
        RectTransform fill = Rect(name + "Fill", track, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(BarTrackWidth, 13f), new Vector2(0f, 0.5f));
        Image image = fill.gameObject.AddComponent<Image>();
        image.color = color;
        return image;
    }

    void BuildCombat(Transform parent)
    {
        // 右侧停靠（原 720x424 死死压在屏幕中央遮挡战场）：缩为 400x252 贴右缘；
        // y=+110 抬离右下角控制条（sortingOrder=140 的回放控制面板），左侧事件流（LycheeHudExtensions）也互不抢位。
        RectTransform panel = Rect("CombatOverlay", parent, new Vector2(1f, 0.5f), new Vector2(-16f, 110f), new Vector2(400f, 252f), new Vector2(1f, 0.5f));
        combatGroup = panel.gameObject.AddComponent<CanvasGroup>();
        Image bg = panel.gameObject.AddComponent<Image>();
        bg.color = new Color(0.01f, 0.012f, 0.018f, 0.82f);
        // 弹窗纯展示无任何可交互元素：背景/插画/卡片一律不接 raycast，避免拦截穿透点击
        bg.raycastTarget = false;

        // 垂直分区（面板高 252，自顶边起）：标题 10..36 / 插画 42..178 / 副标题（自底边 48 起，高 20）/
        // 卡片与比分行（自底边 12 起），各区净空 >= 4px，整体按原 720x424 版式等比缩小。
        combatImage = Rect("CombatIllustration", panel, new Vector2(0.5f, 0.5f), new Vector2(0f, 16f), new Vector2(368f, 136f), new Vector2(0.5f, 0.5f)).gameObject.AddComponent<Image>();
        combatImage.color = Color.white;
        combatImage.preserveAspect = true;
        combatImage.raycastTarget = false;

        combatTitle = Text("CombatTitle", panel, 20, FontStyle.Bold, new Color(1f, 0.88f, 0.55f, 1f), TextAnchor.MiddleCenter);
        SetRect(combatTitle.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -10f), new Vector2(368f, 26f), new Vector2(0.5f, 1f));
        combatSubtitle = Text("CombatSubtitle", panel, 13, FontStyle.Bold, new Color(0.90f, 0.95f, 1f, 0.95f), TextAnchor.MiddleCenter);
        SetRect(combatSubtitle.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 48f), new Vector2(368f, 20f), new Vector2(0.5f, 0f));
        combatRedCard = CombatCard(panel, true);
        combatBlueCard = CombatCard(panel, false);
        combatScore = Text("CombatScore", panel, 24, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
        SetRect(combatScore.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 12f), new Vector2(90f, 32f), new Vector2(0.5f, 0f));
        SetGroup(combatGroup, false, 0f);
    }

    Text CombatCard(Transform parent, bool red)
    {
        Color color = red ? new Color(0.95f, 0.19f, 0.12f, 0.92f) : new Color(0.16f, 0.42f, 1f, 0.92f);
        float x = red ? -95f : 95f;
        RectTransform box = Rect(red ? "CombatRedCard" : "CombatBlueCard", parent, new Vector2(0.5f, 0f), new Vector2(x, 12f), new Vector2(150f, 32f), new Vector2(0.5f, 0f));
        Image boxImage = box.gameObject.AddComponent<Image>();
        boxImage.color = color;
        // 卡片块同为纯展示，禁止拦截点击
        boxImage.raycastTarget = false;
        Text text = Text("Text", box, 12, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
        SetRect(text.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(138f, 24f), new Vector2(0.5f, 0.5f));
        return text;
    }

    void BuildOpening(Transform parent)
    {
        RectTransform panel = Rect("OpeningRosterPanel", parent, new Vector2(0.5f, 0.72f), Vector2.zero, new Vector2(660f, 150f), new Vector2(0.5f, 0.5f));
        openingGroup = panel.gameObject.AddComponent<CanvasGroup>();
        panel.gameObject.AddComponent<Image>().color = new Color(0.012f, 0.018f, 0.014f, 0.72f);
        openingTitle = Text("OpeningTitle", panel, 26, FontStyle.Bold, new Color(1f, 0.86f, 0.45f, 1f), TextAnchor.MiddleCenter);
        SetRect(openingTitle.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -28f), new Vector2(560f, 34f), new Vector2(0.5f, 1f));
        openingRed = Text("OpeningRed", panel, 18, FontStyle.Bold, new Color(1f, 0.34f, 0.28f, 1f), TextAnchor.MiddleCenter);
        SetRect(openingRed.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 10f), new Vector2(600f, 28f), new Vector2(0.5f, 0.5f));
        openingBlue = Text("OpeningBlue", panel, 18, FontStyle.Bold, new Color(0.36f, 0.62f, 1f, 1f), TextAnchor.MiddleCenter);
        SetRect(openingBlue.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, -26f), new Vector2(600f, 28f), new Vector2(0.5f, 0.5f));
        SetGroup(openingGroup, false, 0f);
    }

    void BuildToast(Transform parent)
    {
        RectTransform panel = Rect("DispatchToast", parent, new Vector2(0.5f, BottomHudAnchorY), new Vector2(0f, BottomHeight * 0.5f + 34f), new Vector2(700f, 50f), new Vector2(0.5f, 0.5f));
        toastGroup = panel.gameObject.AddComponent<CanvasGroup>();
        Image toastBg = panel.gameObject.AddComponent<Image>();
        toastBg.color = new Color(0.04f, 0.035f, 0.018f, 0.82f);
        // 提示条纯展示，背景不接 raycast，避免挡住底部 HUD 上方的点击
        toastBg.raycastTarget = false;
        toastText = Text("ToastText", panel, 17, FontStyle.Bold, new Color(1f, 0.87f, 0.44f, 1f), TextAnchor.MiddleCenter);
        SetRect(toastText.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(666f, 34f), new Vector2(0.5f, 0.5f));
        SetGroup(toastGroup, false, 0f);
    }

    void UpdateBar(DualBar bar, float red, float blue, string redValue, string blueValue, float redDelta, float blueDelta, string suffix)
    {
        bar.redFill.rectTransform.sizeDelta = new Vector2(BarTrackWidth * Mathf.Clamp01(red / Mathf.Max(1f, bar.maxValue)), 13f);
        bar.blueFill.rectTransform.sizeDelta = new Vector2(BarTrackWidth * Mathf.Clamp01(blue / Mathf.Max(1f, bar.maxValue)), 13f);
        bar.redValue.text = redValue;
        bar.blueValue.text = blueValue;
        SetDelta(bar.redDelta, redDelta, suffix);
        SetDelta(bar.blueDelta, blueDelta, suffix);
    }

    void SetDelta(Text text, float delta, string suffix)
    {
        if (Mathf.Abs(delta) < 0.01f)
        {
            text.text = "";
            return;
        }
        text.text = (delta > 0f ? "+" : "") + delta.ToString(Mathf.Abs(delta) < 9.9f ? "0.#" : "0") + suffix;
        text.color = delta > 0f ? new Color(0.42f, 1f, 0.45f, 1f) : new Color(1f, 0.38f, 0.28f, 1f);
    }

    static void SetGroup(CanvasGroup group, bool visible, float alpha)
    {
        group.alpha = alpha;
        group.interactable = visible;
        group.blocksRaycasts = visible;
    }

    RectTransform Rect(string name, Transform parent, Vector2 anchor, Vector2 anchoredPosition, Vector2 size, Vector2 pivot)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        SetRect(rt, anchor, anchoredPosition, size, pivot);
        return rt;
    }

    static void SetRect(RectTransform rt, Vector2 anchor, Vector2 anchoredPosition, Vector2 size, Vector2 pivot)
    {
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = anchoredPosition;
        rt.sizeDelta = size;
        rt.localScale = Vector3.one;
    }

    Text Text(string name, Transform parent, int size, FontStyle style, Color color, TextAnchor anchor)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        Text text = go.AddComponent<Text>();
        text.font = font;
        text.fontSize = size;
        text.fontStyle = style;
        text.color = color;
        text.alignment = anchor;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        text.raycastTarget = false;
        return text;
    }

    static string CardText(string card)
    {
        if (string.IsNullOrEmpty(card)) return "未出牌";
        if (card == "YAN_DIE") return "验牒";
        if (card == "BING_ZHENG") return "兵争";
        if (card == "XIAN_GONG") return "献贡";
        if (card == "QIANG_XING") return "抢行";
        return card.Replace("_", " ");
    }

    static string WinnerText(string winner)
    {
        if (winner == "RED") return "红队";
        if (winner == "BLUE") return "蓝队";
        return "双方";
    }

    static string Clean(string value)
    {
        return string.IsNullOrEmpty(value) ? "未知" : value;
    }
}
