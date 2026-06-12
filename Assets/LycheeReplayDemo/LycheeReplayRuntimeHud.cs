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

    // 对抗卡片块的演出状态（翻面 + 胜方金色脉冲）：全部由 Refresh 每帧驱动的
    // unscaledTime 状态机推进，不用协程——与本面板既有的淡入淡出驱动方式保持一致。
    sealed class CardFx
    {
        public RectTransform block;   // 翻面的整块（底色 + 牌面 + 文字一起绕中轴翻转）
        public Image face;            // 牌面图：前半程显示 card_back，过中点换正面贴图
        public Image backPlate;       // 背面态墨色底：盖住队色块模拟牌背
        public Text backMark;         // card_back 缺失时的兜底：墨底 + 金“?”
        public Text label;            // 牌名文字（翻到正面后才显示）
        public Image glow;            // 胜方金色脉冲垫片（同层靠前 = 渲染在卡片块下面）
        public float flipStart = float.NegativeInfinity; // 翻面开始时刻，NegInf 表示空闲
        public string flipCard;       // 原始牌名（映射正面贴图用，与显示文字分离）
        public bool flipFrontShown;   // 是否已过中点换到正面
        public float glowStart = float.NegativeInfinity; // 脉冲开始时刻（可设未来值=延迟启动）
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
    // 出牌翻面总时长：前半程 x 1→0 收拢露牌背，过中点换正面，后半程 0→1 张开。
    const float CardFlipSeconds = 0.35f;
    // 胜方金色脉冲：单次 0.7→0 的衰减时长，连放两次。
    const float WinnerGlowPulseSeconds = 0.45f;
    // 面板进场滑入：自 +24px 偏移 EaseOutCubic 滑到位，与 0.25s 淡入节拍接近。
    const float PanelSlideSeconds = 0.3f;
    const float PanelSlideOffset = 24f;
    // 比分滚动速度（分/秒）与涨分瞬间字号回弹时长。
    const float ScoreRollPerSecond = 6f;
    const float ScorePopSeconds = 0.2f;
    const int TeamScoreFontSize = 34;
    // 对抗暗角：开始 0.4s 淡入聚焦、结束 0.6s 淡出释放。
    const float VignetteFadeInSeconds = 0.4f;
    const float VignetteFadeOutSeconds = 0.6f;

    // 唐风统一调色板（与全工程其他 HUD 一致）：把“工程 Demo 感”的深灰面板整体换成
    // 墨底 + 鎏金标题 + 绢黄正文 + 暗金描边，红队队色统一用朱红。
    static readonly Color InkColor = new Color(0.102f, 0.090f, 0.078f, 0.86f);    // 墨 #1A1714，半透明面板底
    static readonly Color GoldColor = new Color(0.831f, 0.686f, 0.361f, 1f);      // 鎏金 #D4AF5C，标题/强调
    static readonly Color SilkColor = new Color(0.929f, 0.878f, 0.765f, 1f);      // 绢黄 #EDE0C3，正文
    static readonly Color DarkGoldColor = new Color(0.549f, 0.455f, 0.220f, 1f);  // 暗金 #8C7438，描边/分隔线
    static readonly Color VermilionColor = new Color(0.620f, 0.161f, 0.125f, 1f); // 朱红 #9E2920，红队队色

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
    RectTransform combatRect;
    Vector2 combatBasePos;
    Image combatImage;
    Text combatTitle;
    Text combatSubtitle;
    Text combatRedCard;
    Text combatBlueCard;
    Text combatScore;
    CardFx redCardFx;
    CardFx blueCardFx;
    CanvasGroup openingGroup;
    Text openingTitle;
    Text openingRed;
    Text openingBlue;
    CanvasGroup toastGroup;
    RectTransform toastRect;
    Vector2 toastBasePos;
    Text toastText;
    Image vignetteImage;
    Sprite[] combatSprites;

    float combatUntil;
    // 下一条普通弹窗最早允许出现的时刻（= 上一条隐藏时刻 + CombatGapSeconds）。
    float combatNextAllowed;
    // 普通事件弹窗等待队列；对抗三连击（开始/亮牌/结算）走 priority 路径绕过它。
    readonly List<PendingCombat> pendingCombats = new List<PendingCombat>();
    float openingUntil;
    float toastUntil;
    // 面板进场滑入的起始时刻：NegInf 表示无进行中的滑入（偏移恒为 0）。
    float combatShowStart = float.NegativeInfinity;
    float toastShowStart = float.NegativeInfinity;
    // 对抗暗角的目标透明度与当前淡变时长（淡入 0.4s / 淡出 0.6s 不同速）。
    float vignetteTarget;
    float vignetteFade = VignetteFadeOutSeconds;
    // 比分滚动跳字的内部状态：显示值朝真实值滚动，目标变化瞬间触发字号回弹。
    float displayedRedScore;
    float displayedBlueScore;
    int rollTargetRedScore;
    int rollTargetBlueScore;
    float redScorePopStart = float.NegativeInfinity;
    float blueScorePopStart = float.NegativeInfinity;
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

    // 主题贴图静态缓存（同 LycheeHudExtensions 的惯例）：跨画布重建复用，
    // 退出播放后 Unity 对象被销毁时重载（== null 走 Unity 重载判空）。
    static Sprite frameSprite;
    static Sprite vignetteSprite;
    static readonly Dictionary<string, Sprite> themeSpriteCache = new Dictionary<string, Sprite>();

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
        redTeam.score.fontSize = TeamScoreFontSize;
        blueTeam.name.text = "蓝队";
        blueTeam.roster.text = "16人  队长1 / 队员15";
        blueTeam.score.text = "0";
        blueTeam.score.fontSize = TeamScoreFontSize;
        // 比分滚动状态归零：预览不滚动、不回弹
        displayedRedScore = 0f;
        displayedBlueScore = 0f;
        rollTargetRedScore = 0;
        rollTargetBlueScore = 0;
        redScorePopStart = float.NegativeInfinity;
        blueScorePopStart = float.NegativeInfinity;
        UpdateBar(freshBar, 92f, 88f, "92%", "88%", 0f, 0f, "%");
        UpdateBar(fruitBar, 36f, 32f, "36", "32", 0f, 0f, "");
        UpdateBar(squadBar, 8f, 8f, "8", "8", 0f, 0f, "");
        UpdateBar(scoreBar, 0f, 0f, "0", "0", 0f, 0f, "");
        SetGroup(combatGroup, false, 0f);
        SetGroup(openingGroup, false, 0f);
        SetGroup(toastGroup, false, 0f);
        // 对抗演出状态全部归位：翻面/脉冲/暗角/滑入在预览态不应残留
        ResetCardFx(redCardFx);
        ResetCardFx(blueCardFx);
        vignetteTarget = 0f;
        if (vignetteImage != null) vignetteImage.color = new Color(1f, 1f, 1f, 0f);
        combatShowStart = float.NegativeInfinity;
        toastShowStart = float.NegativeInfinity;
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
        // 对抗暗角聚焦：压暗四周把视线收到战场与弹窗上，CONTEST_END 时淡出
        SetVignette(true);
    }

    public void ShowCombatReveal(int round, int roundIndex, string redCard, string blueCard, string winner, int redPoint, int bluePoint)
    {
        string winnerText = WinnerText(winner);
        ShowCombat(1, "对抗第" + Mathf.Max(1, roundIndex) + "局  第" + round + "回合", winnerText + " 得势，状态条同步更新", CardText(redCard), CardText(blueCard), redPoint + " : " + bluePoint, 2.8f, true);
        // 亮牌瞬间双方卡片同时翻面：先露牌背、过中点换正面贴图（传原始牌名做映射）
        StartCardFlip(redCardFx, redCard);
        StartCardFlip(blueCardFx, blueCard);
        // 单局得势方的金色脉冲延迟到翻面落定后再亮，节拍上不与翻面抢戏
        if (winner == "RED") StartWinnerGlow(redCardFx, CardFlipSeconds);
        else if (winner == "BLUE") StartWinnerGlow(blueCardFx, CardFlipSeconds);
    }

    public void ShowCombatEnd(int round, string winner, int redPoint, int bluePoint)
    {
        ShowCombat(2, "对抗结束  第" + round + "回合", WinnerText(winner) + " 胜出", "红队积分 " + redPoint, "蓝队积分 " + bluePoint, redPoint + " : " + bluePoint, 3.0f, true);
        // 胜者演出：判出胜负的这一拍给胜方卡片两次金色脉冲，并配一记短促震屏强化打击感
        if (winner == "RED") StartWinnerGlow(redCardFx, 0f);
        else if (winner == "BLUE") StartWinnerGlow(blueCardFx, 0f);
        if (winner == "RED" || winner == "BLUE") ShakeCamera(0.18f, 0.35f);
        // 对抗收束：暗角 0.6s 缓缓放开
        SetVignette(false);
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
        // 进场滑入只在从隐藏态弹出时触发：连续调度提示只换文字、不反复跳位
        if (toastGroup.alpha <= 0.05f)
            toastShowStart = Time.unscaledTime;
    }

    // 回放跳转（拖进度条、重播按钮、循环回开头）后调用：排队中的弹窗与在显内容都指向跳转前
    // 的旧进度，继续补播只会报错位的回合号；全部作废并解除间隔锁，让跳转目标帧重放出的事件
    // 能即时弹出，而不是被旧的 combatNextAllowed 压回队列。
    public void CancelCombatPopups()
    {
        pendingCombats.Clear();
        combatUntil = 0f;
        combatNextAllowed = 0f;
        // 对抗演出同步作废：翻面/脉冲指向跳转前的旧对抗，暗角立即熄灭避免残留压暗画面
        ResetCardFx(redCardFx);
        ResetCardFx(blueCardFx);
        vignetteTarget = 0f;
        if (vignetteImage != null) vignetteImage.color = new Color(1f, 1f, 1f, 0f);
        // 比分滚动同步贴齐：跳转后不该花几秒"追"一个旧进度的中间值
        displayedRedScore = rollTargetRedScore;
        displayedBlueScore = rollTargetBlueScore;
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
        }
        if (blue != null)
        {
            blueTeam.name.text = "蓝队  " + blue.name;
            blueTeam.roster.text = blueCount + "人  队长1 / 队员" + Mathf.Max(0, blueCount - 1);
        }

        float now = Time.unscaledTime;

        // 比分滚动跳字：大数字不再瞬间改值，~6/s 滚动 + 涨分瞬间字号回弹（取代原 text 直写）
        RollScore(ref displayedRedScore, ref rollTargetRedScore, ref redScorePopStart, redScore, redTeam.score, now);
        RollScore(ref displayedBlueScore, ref rollTargetBlueScore, ref blueScorePopStart, blueScore, blueTeam.score, now);

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
        // 进场滑入：右侧停靠面板自 +24px（更靠屏幕外）EaseOutCubic 滑回原位；
        // combatShowStart 为 NegInf 时进度恒为 1、偏移恒为 0，无需分支。
        float combatSlide = (1f - EaseOutCubic((now - combatShowStart) / PanelSlideSeconds)) * PanelSlideOffset;
        combatRect.anchoredPosition = combatBasePos + new Vector2(combatSlide, 0f);

        SetGroup(openingGroup, now < openingUntil && round <= 24, now < openingUntil && round <= 24 ? 1f : 0f);

        // 调度提示同样改为淡入淡出（原 alpha 硬切）+ 自上方 24px 滑入；
        // 纯展示面板与对抗弹窗一致：永不拦截 raycast。
        toastGroup.alpha = Mathf.MoveTowards(toastGroup.alpha, now < toastUntil ? 1f : 0f, Time.unscaledDeltaTime / CombatFadeSeconds);
        toastGroup.interactable = false;
        toastGroup.blocksRaycasts = false;
        float toastSlide = (1f - EaseOutCubic((now - toastShowStart) / PanelSlideSeconds)) * PanelSlideOffset;
        toastRect.anchoredPosition = toastBasePos + new Vector2(0f, toastSlide);

        // 对抗演出每帧驱动：出牌翻面状态机 + 胜方金色脉冲
        UpdateCardFlip(redCardFx, now);
        UpdateCardFlip(blueCardFx, now);
        UpdateWinnerGlow(redCardFx, now);
        UpdateWinnerGlow(blueCardFx, now);

        // 对抗暗角：朝目标透明度淡变（淡入 0.4s / 淡出 0.6s 不同速）
        if (vignetteImage != null)
            vignetteImage.color = new Color(1f, 1f, 1f, Mathf.MoveTowards(vignetteImage.color.a, vignetteTarget, Time.unscaledDeltaTime / Mathf.Max(0.01f, vignetteFade)));
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
        // 新内容上屏先清掉上一拍的翻面/脉冲残留：亮牌路径会在 ShowCombatReveal 里随后重启翻面
        ResetCardFx(redCardFx);
        ResetCardFx(blueCardFx);
        // 进场滑入只在从隐藏态弹出时触发：对抗三连击连换内容不反复跳位
        if (combatGroup.alpha <= 0.05f)
            combatShowStart = Time.unscaledTime;
        combatUntil = Time.unscaledTime + seconds;
        // 下一条普通弹窗最早出现时刻 = 本条隐藏后再留一段喘息间隔
        combatNextAllowed = combatUntil + CombatGapSeconds;
        // 不再直接把 alpha 打到 1：交给 Refresh 里的渐变做淡入；纯展示面板永不拦截点击
        combatGroup.interactable = false;
        combatGroup.blocksRaycasts = false;
    }

    // ---------- 对抗演出（翻面 / 胜方脉冲 / 震屏 / 暗角） ----------

    void StartCardFlip(CardFx fx, string rawCard)
    {
        if (fx == null || fx.block == null) return;
        fx.flipStart = Time.unscaledTime;
        fx.flipCard = rawCard;
        fx.flipFrontShown = false;
        fx.block.localScale = Vector3.one;
        // 立刻切到背面态：DisplayCombat 已把正面文字写好，等下一帧 Refresh 再隐藏会穿帮一帧
        ApplyFlipBack(fx);
    }

    void StartWinnerGlow(CardFx fx, float delay)
    {
        if (fx == null || fx.glow == null) return;
        fx.glowStart = Time.unscaledTime + delay;
    }

    void UpdateCardFlip(CardFx fx, float now)
    {
        if (fx == null || fx.block == null || float.IsNegativeInfinity(fx.flipStart)) return;
        float t = (now - fx.flipStart) / CardFlipSeconds;
        if (t >= 1f)
        {
            // 翻面收尾：保证正面已换上、缩放归一，状态机回到空闲
            if (!fx.flipFrontShown) ApplyFlipFront(fx);
            fx.block.localScale = Vector3.one;
            fx.flipStart = float.NegativeInfinity;
            return;
        }
        if (t < 0.5f)
        {
            // 前半程：x 1→0 收拢，露出牌背
            fx.block.localScale = new Vector3(1f - t * 2f, 1f, 1f);
            return;
        }
        // 后半程：过中点换正面，x 0→1 张开
        if (!fx.flipFrontShown) ApplyFlipFront(fx);
        fx.block.localScale = new Vector3((t - 0.5f) * 2f, 1f, 1f);
    }

    void ApplyFlipBack(CardFx fx)
    {
        fx.label.enabled = false;
        fx.backPlate.enabled = true;
        Sprite back = GetThemeSprite("card_back.png");
        if (back != null)
        {
            fx.face.enabled = true;
            fx.face.sprite = back;
            fx.face.rectTransform.anchoredPosition = Vector2.zero; // 背面牌图居中
            fx.backMark.enabled = false;
        }
        else
        {
            // 兜底：card_back 缺失时用墨色底 + 金“?”模拟扣着的牌
            fx.face.enabled = false;
            fx.backMark.enabled = true;
        }
    }

    void ApplyFlipFront(CardFx fx)
    {
        fx.flipFrontShown = true;
        fx.backPlate.enabled = false;
        fx.backMark.enabled = false;
        fx.label.enabled = true;
        Sprite front = GetCardFrontSprite(fx.flipCard);
        if (front != null)
        {
            fx.face.enabled = true;
            fx.face.sprite = front;
            fx.face.rectTransform.anchoredPosition = new Vector2(-55f, 0f); // 牌面靠左给牌名让位
        }
        else
        {
            fx.face.enabled = false; // 贴图缺失：维持原本的纯文字呈现
        }
    }

    void UpdateWinnerGlow(CardFx fx, float now)
    {
        if (fx == null || fx.glow == null) return;
        float t = (now - fx.glowStart) / WinnerGlowPulseSeconds;
        float alpha = 0f;
        // 两次脉冲：每个整数段内 alpha 0.7→0 线性衰减；glowStart 在未来（延迟启动）时 t<0 保持熄灭
        if (t >= 0f && t < 2f)
            alpha = 0.7f * (1f - (t - Mathf.Floor(t)));
        fx.glow.color = new Color(GoldColor.r, GoldColor.g, GoldColor.b, alpha);
    }

    void ResetCardFx(CardFx fx)
    {
        if (fx == null || fx.block == null) return;
        fx.flipStart = float.NegativeInfinity;
        fx.glowStart = float.NegativeInfinity;
        fx.flipFrontShown = false;
        fx.block.localScale = Vector3.one;
        fx.face.enabled = false;
        fx.backPlate.enabled = false;
        fx.backMark.enabled = false;
        fx.label.enabled = true;
        fx.glow.color = new Color(GoldColor.r, GoldColor.g, GoldColor.b, 0f);
    }

    // 对抗暗角开关：开 0.4s 淡入、关 0.6s 淡出，实际淡变在 Refresh 里逐帧推进
    void SetVignette(bool visible)
    {
        vignetteTarget = visible ? 1f : 0f;
        vignetteFade = visible ? VignetteFadeInSeconds : VignetteFadeOutSeconds;
    }

    // 相机震屏（null 安全）：编辑器预览或主相机未挂控制器时静默跳过
    static void ShakeCamera(float strength, float duration)
    {
        if (!Application.isPlaying) return;
        Camera main = Camera.main;
        if (main == null) return;
        LycheeReplayCameraController controller = main.GetComponent<LycheeReplayCameraController>();
        if (controller != null) controller.Shake(strength, duration);
    }

    // 比分滚动跳字：显示值以 ~6/s 朝真实值滚动、四舍五入显示；涨分瞬间字号 1.25x 回弹 0.2s。
    // 回放跳转（比分回退或大跨度前进）直接贴齐且不回弹——6/s 的滚动追不上跳变，硬滚只会
    // 让比分长时间显示错误值。
    void RollScore(ref float displayed, ref int lastTarget, ref float popStart, int target, Text text, float now)
    {
        if (target != lastTarget)
        {
            if (target < lastTarget || Mathf.Abs(target - displayed) > 20f)
                displayed = target;
            else
                popStart = now;
            lastTarget = target;
        }
        displayed = Mathf.MoveTowards(displayed, target, ScoreRollPerSecond * Time.unscaledDeltaTime);
        text.text = Mathf.RoundToInt(displayed).ToString();
        float pop = 1f + 0.25f * Mathf.Clamp01(1f - (now - popStart) / ScorePopSeconds);
        text.fontSize = Mathf.RoundToInt(TeamScoreFontSize * pop);
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

        BuildVignette(root);
        BuildBottomHud(root);
        BuildCombat(root);
        BuildOpening(root);
        BuildToast(root);
    }

    void EnsureBuilt()
    {
        if (canvas != null && redTeam != null && blueTeam != null && roundText != null && freshBar != null && combatGroup != null && openingGroup != null && toastGroup != null && vignetteImage != null && redCardFx != null)
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
        combatRect = null;
        combatImage = null;
        combatTitle = null;
        combatSubtitle = null;
        combatRedCard = null;
        combatBlueCard = null;
        combatScore = null;
        redCardFx = null;
        blueCardFx = null;
        openingGroup = null;
        openingTitle = null;
        openingRed = null;
        openingBlue = null;
        toastGroup = null;
        toastRect = null;
        toastText = null;
        vignetteImage = null;
        // 画布重建时清掉排队中的旧弹窗，避免重建后把过期事件补播出来
        pendingCombats.Clear();
        combatNextAllowed = 0f;
        // 演出状态同步归零（displayed 比分保留：重建画布不应让比分重新从 0 滚起）
        combatShowStart = float.NegativeInfinity;
        toastShowStart = float.NegativeInfinity;
        vignetteTarget = 0f;

        if (target == null) return;
        if (Application.isPlaying) Destroy(target);
        else DestroyImmediate(target);
    }

    // 对抗暗角聚焦：全屏程序化径向渐变（中心透明、四角压黑），用子 Canvas 的 overrideSorting
    // 压在 CombatOverlay 所在主画布（130）之下一层，弹窗与底部 HUD 始终浮在暗角之上。
    void BuildVignette(Transform parent)
    {
        RectTransform rt = StretchRect("ContestVignette", parent);
        Canvas sub = rt.gameObject.AddComponent<Canvas>();
        sub.overrideSorting = true;
        sub.sortingOrder = canvas.sortingOrder - 1;
        vignetteImage = rt.gameObject.AddComponent<Image>();
        vignetteImage.sprite = GetVignetteSprite();
        vignetteImage.color = new Color(1f, 1f, 1f, 0f);
        vignetteImage.raycastTarget = false;
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
        barsBg.color = InkColor;
        barsBg.raycastTarget = false;
        // 顶部分隔线换暗金：原亮金黄过于刺眼，与唐风描边统一
        Rect("BarsTopLine", barsBackdrop, new Vector2(0.5f, 1f), new Vector2(0f, -1f), new Vector2(730f, 2f), new Vector2(0.5f, 1f)).gameObject.AddComponent<Image>().color = DarkGoldColor;

        RectTransform titleChip = Rect("TitleChip", panel, new Vector2(0.5f, 1f), new Vector2(0f, -6f), new Vector2(340f, 70f), new Vector2(0.5f, 1f));
        Image titleBg = titleChip.gameObject.AddComponent<Image>();
        titleBg.color = InkColor;
        titleBg.raycastTarget = false;

        roundText = Text("RoundText", panel, 22, FontStyle.Bold, GoldColor, TextAnchor.MiddleCenter);
        SetRect(roundText.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -16f), new Vector2(320f, 28f), new Vector2(0.5f, 1f));
        phaseText = Text("PhaseText", panel, 15, FontStyle.Bold, SilkColor, TextAnchor.MiddleCenter);
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
        // 红队队色统一为唐风朱红（蓝队保留原队蓝）。
        Color teamColor = red ? VermilionColor : new Color(0.25f, 0.55f, 1f, 1f);
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
        widgets.roster = Text(red ? "RedRoster" : "BlueRoster", parent, 12, FontStyle.Normal, new Color(SilkColor.r, SilkColor.g, SilkColor.b, 0.82f), align);
        SetRect(widgets.roster.rectTransform, new Vector2(0.5f, 0f), new Vector2(textX, 111f), new Vector2(112f, 20f), new Vector2(0.5f, 0.5f));
        widgets.score = Text(red ? "RedScore" : "BlueScore", parent, TeamScoreFontSize, FontStyle.Bold, teamColor, align);
        SetRect(widgets.score.rectTransform, new Vector2(0.5f, 0f), new Vector2(textX, 75f), new Vector2(112f, 36f), new Vector2(0.5f, 0.5f));
        return widgets;
    }

    DualBar BuildDualBar(Transform parent, string label, float max, float y)
    {
        // 单条横向分区（红侧取负、蓝侧取正，自面板中心起）：
        //   中央标签 -36..36 / track 44..234（宽 190，距标签 8px）/ 数值 242..294（距条外端 8px）/ Delta 300..344（距数值 6px）。
        // 红侧数值右对齐固定在条左外侧，蓝侧数值左对齐固定在条右外侧，百分比/Delta 均不会压在填充条上。
        DualBar bar = new DualBar { maxValue = max };
        Text labelText = Text(label + "Label", parent, 14, FontStyle.Bold, GoldColor, TextAnchor.MiddleCenter);
        SetRect(labelText.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, y), new Vector2(72f, 22f), new Vector2(0.5f, 0.5f));
        // 红条填充用朱红（提一点透明度补足暗色底上的对比），蓝条保留队蓝
        bar.redFill = BarFill(parent, label + "RedTrack", new Vector2(-139f, y), new Color(VermilionColor.r, VermilionColor.g, VermilionColor.b, 0.94f));
        bar.blueFill = BarFill(parent, label + "BlueTrack", new Vector2(139f, y), new Color(0.20f, 0.48f, 1f, 0.94f));
        bar.redValue = Text(label + "RedValue", parent, 12, FontStyle.Bold, SilkColor, TextAnchor.MiddleRight);
        SetRect(bar.redValue.rectTransform, new Vector2(0.5f, 0f), new Vector2(-242f, y), new Vector2(52f, 22f), new Vector2(1f, 0.5f));
        bar.blueValue = Text(label + "BlueValue", parent, 12, FontStyle.Bold, SilkColor, TextAnchor.MiddleLeft);
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
        // track 底色从纯白透明改为暗金：与分隔线/描边同一色系
        track.gameObject.AddComponent<Image>().color = new Color(DarkGoldColor.r, DarkGoldColor.g, DarkGoldColor.b, 0.28f);
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
        combatRect = panel;
        combatBasePos = panel.anchoredPosition;
        combatGroup = panel.gameObject.AddComponent<CanvasGroup>();
        Image bg = panel.gameObject.AddComponent<Image>();
        bg.color = InkColor;
        // 弹窗纯展示无任何可交互元素：背景/插画/卡片一律不接 raycast，避免拦截穿透点击
        bg.raycastTarget = false;
        // 唐风描边：panel_frame 九宫格（缺失回退四边暗金细线）；紧跟背景创建保证画在内容下层
        ApplyPanelFrame(panel, 2f);

        // 垂直分区（面板高 252，自顶边起）：标题 10..36 / 插画 42..178 / 副标题（自底边 48 起，高 20）/
        // 卡片与比分行（自底边 12 起），各区净空 >= 4px，整体按原 720x424 版式等比缩小。
        combatImage = Rect("CombatIllustration", panel, new Vector2(0.5f, 0.5f), new Vector2(0f, 16f), new Vector2(368f, 136f), new Vector2(0.5f, 0.5f)).gameObject.AddComponent<Image>();
        combatImage.color = Color.white;
        combatImage.preserveAspect = true;
        combatImage.raycastTarget = false;

        combatTitle = Text("CombatTitle", panel, 20, FontStyle.Bold, GoldColor, TextAnchor.MiddleCenter);
        SetRect(combatTitle.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -10f), new Vector2(368f, 26f), new Vector2(0.5f, 1f));
        combatSubtitle = Text("CombatSubtitle", panel, 13, FontStyle.Bold, new Color(SilkColor.r, SilkColor.g, SilkColor.b, 0.95f), TextAnchor.MiddleCenter);
        SetRect(combatSubtitle.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 48f), new Vector2(368f, 20f), new Vector2(0.5f, 0f));
        redCardFx = BuildCombatCard(panel, true);
        blueCardFx = BuildCombatCard(panel, false);
        combatRedCard = redCardFx.label;
        combatBlueCard = blueCardFx.label;
        combatScore = Text("CombatScore", panel, 24, FontStyle.Bold, GoldColor, TextAnchor.MiddleCenter);
        SetRect(combatScore.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 12f), new Vector2(90f, 32f), new Vector2(0.5f, 0f));
        SetGroup(combatGroup, false, 0f);
    }

    // 对抗卡片块：唐风底色（红=朱红 / 蓝=队蓝）+ 牌面图层 + 背面兜底层，下层垫金色脉冲。
    // 块的 pivot.x=0.5，localScale.x 直接绕中轴翻面。
    CardFx BuildCombatCard(Transform parent, bool red)
    {
        Color color = red ? new Color(VermilionColor.r, VermilionColor.g, VermilionColor.b, 0.92f) : new Color(0.16f, 0.42f, 1f, 0.92f);
        float x = red ? -95f : 95f;
        CardFx fx = new CardFx();

        // 胜方金色脉冲垫片先创建（同层靠前 = 渲染在卡片块下面），尺寸放大 12%（150x32 → 168x36）；
        // pivot 同为底边，y 下移 2px 让垫片上下各凸出 2px。
        RectTransform glowRt = Rect(red ? "CombatRedCardGlow" : "CombatBlueCardGlow", parent, new Vector2(0.5f, 0f), new Vector2(x, 10f), new Vector2(168f, 36f), new Vector2(0.5f, 0f));
        fx.glow = glowRt.gameObject.AddComponent<Image>();
        fx.glow.color = new Color(GoldColor.r, GoldColor.g, GoldColor.b, 0f);
        fx.glow.raycastTarget = false;

        RectTransform box = Rect(red ? "CombatRedCard" : "CombatBlueCard", parent, new Vector2(0.5f, 0f), new Vector2(x, 12f), new Vector2(150f, 32f), new Vector2(0.5f, 0f));
        fx.block = box;
        Image boxImage = box.gameObject.AddComponent<Image>();
        boxImage.color = color;
        // 卡片块同为纯展示，禁止拦截点击
        boxImage.raycastTarget = false;

        // 背面态墨色底：盖住队色块模拟牌背（card_back 在与不在都先压一层墨）
        RectTransform plateRt = Rect("CardBackPlate", box, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(150f, 32f), new Vector2(0.5f, 0.5f));
        fx.backPlate = plateRt.gameObject.AddComponent<Image>();
        fx.backPlate.color = new Color(InkColor.r, InkColor.g, InkColor.b, 0.96f);
        fx.backPlate.raycastTarget = false;
        fx.backPlate.enabled = false;

        // 牌面图：背面态显示 card_back（居中），正面态换牌面贴图并靠左给牌名让位
        RectTransform faceRt = Rect("CardFace", box, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(28f, 28f), new Vector2(0.5f, 0.5f));
        fx.face = faceRt.gameObject.AddComponent<Image>();
        fx.face.color = Color.white;
        fx.face.preserveAspect = true;
        fx.face.raycastTarget = false;
        fx.face.enabled = false;

        // card_back 缺失时的背面兜底：墨底上的金“?”
        fx.backMark = Text("CardBackMark", box, 18, FontStyle.Bold, GoldColor, TextAnchor.MiddleCenter);
        SetRect(fx.backMark.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(150f, 32f), new Vector2(0.5f, 0.5f));
        fx.backMark.text = "?";
        fx.backMark.enabled = false;

        fx.label = Text("Text", box, 12, FontStyle.Bold, SilkColor, TextAnchor.MiddleCenter);
        SetRect(fx.label.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(138f, 24f), new Vector2(0.5f, 0.5f));
        return fx;
    }

    void BuildOpening(Transform parent)
    {
        RectTransform panel = Rect("OpeningRosterPanel", parent, new Vector2(0.5f, 0.72f), Vector2.zero, new Vector2(660f, 150f), new Vector2(0.5f, 0.5f));
        openingGroup = panel.gameObject.AddComponent<CanvasGroup>();
        panel.gameObject.AddComponent<Image>().color = InkColor;
        openingTitle = Text("OpeningTitle", panel, 26, FontStyle.Bold, GoldColor, TextAnchor.MiddleCenter);
        SetRect(openingTitle.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -28f), new Vector2(560f, 34f), new Vector2(0.5f, 1f));
        openingRed = Text("OpeningRed", panel, 18, FontStyle.Bold, VermilionColor, TextAnchor.MiddleCenter);
        SetRect(openingRed.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 10f), new Vector2(600f, 28f), new Vector2(0.5f, 0.5f));
        openingBlue = Text("OpeningBlue", panel, 18, FontStyle.Bold, new Color(0.36f, 0.62f, 1f, 1f), TextAnchor.MiddleCenter);
        SetRect(openingBlue.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, -26f), new Vector2(600f, 28f), new Vector2(0.5f, 0.5f));
        SetGroup(openingGroup, false, 0f);
    }

    void BuildToast(Transform parent)
    {
        RectTransform panel = Rect("DispatchToast", parent, new Vector2(0.5f, BottomHudAnchorY), new Vector2(0f, BottomHeight * 0.5f + 34f), new Vector2(700f, 50f), new Vector2(0.5f, 0.5f));
        toastRect = panel;
        toastBasePos = panel.anchoredPosition;
        toastGroup = panel.gameObject.AddComponent<CanvasGroup>();
        Image toastBg = panel.gameObject.AddComponent<Image>();
        toastBg.color = InkColor;
        // 提示条纯展示，背景不接 raycast，避免挡住底部 HUD 上方的点击
        toastBg.raycastTarget = false;
        // 矮条用更大的 border 缩放：512px 贴图的 18% 边在 50 高的条上必须压薄才不挤爆四角
        ApplyPanelFrame(panel, 6f);
        toastText = Text("ToastText", panel, 17, FontStyle.Bold, GoldColor, TextAnchor.MiddleCenter);
        SetRect(toastText.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(666f, 34f), new Vector2(0.5f, 0.5f));
        SetGroup(toastGroup, false, 0f);
    }

    // 面板唐风描边：panel_frame.png 可用时叠一层九宫格边框（保留墨色底保证正文可读，
    // 不依赖贴图中心区是否透明）；取不到时回退四边 2px 暗金细线，任何素材缺失下轮廓完整。
    void ApplyPanelFrame(RectTransform panel, float borderScale)
    {
        Sprite frame = GetFrameSprite();
        if (frame != null)
        {
            RectTransform rt = StretchRect("PanelFrame", panel);
            Image image = rt.gameObject.AddComponent<Image>();
            image.sprite = frame;
            image.type = Image.Type.Sliced;
            image.color = Color.white;
            // 九宫格边框按面板尺寸压薄：multiplier 越大屏幕上边越细
            image.pixelsPerUnitMultiplier = borderScale;
            image.raycastTarget = false;
            return;
        }
        AddEdgeLine(panel, "EdgeTop", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, 2f));
        AddEdgeLine(panel, "EdgeBottom", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 2f));
        AddEdgeLine(panel, "EdgeLeft", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(2f, 0f));
        AddEdgeLine(panel, "EdgeRight", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(2f, 0f));
    }

    void AddEdgeLine(RectTransform panel, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 sizeDelta)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(panel, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = pivot;
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = sizeDelta;
        rt.localScale = Vector3.one;
        Image line = go.AddComponent<Image>();
        line.color = DarkGoldColor;
        line.raycastTarget = false;
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

    // 四角锚满父级的全屏/全面板矩形（Rect 帮手只支持点锚，描边/暗角需要拉伸锚）
    RectTransform StretchRect(string name, Transform parent)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        rt.localScale = Vector3.one;
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

    // ---------- 主题贴图与程序化兜底 ----------

    /// <summary>从 ThemeCache 解码主题贴图；缺失或解码失败返回 null（调用方走程序化兜底）。</summary>
    static Texture2D LoadThemeTexture(string fileName)
    {
        byte[] bytes = LycheeThemeCache.LoadBytes(fileName);
        if (bytes == null || bytes.Length == 0) return null;
        Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!texture.LoadImage(bytes)) return null;
        texture.wrapMode = TextureWrapMode.Clamp;
        return texture;
    }

    /// <summary>panel_frame 九宫格 Sprite（border 取贴图尺寸 18%）；缺失返回 null 走细线兜底。</summary>
    static Sprite GetFrameSprite()
    {
        if (frameSprite != null) return frameSprite;
        Texture2D texture = LoadThemeTexture("panel_frame.png");
        if (texture == null) return null;
        float borderX = texture.width * 0.18f;
        float borderY = texture.height * 0.18f;
        frameSprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(borderX, borderY, borderX, borderY));
        return frameSprite;
    }

    /// <summary>普通主题 Sprite（牌面/牌背）。只缓存成功结果：WebGL 预载完成前的失败不会被锁死。</summary>
    static Sprite GetThemeSprite(string fileName)
    {
        if (themeSpriteCache.TryGetValue(fileName, out Sprite cached) && cached != null) return cached;
        Texture2D texture = LoadThemeTexture(fileName);
        if (texture == null) return null;
        Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
        themeSpriteCache[fileName] = sprite;
        return sprite;
    }

    /// <summary>牌名 → 正面贴图映射（与 LycheeCardFxSystem.ParseCard 的判定口径一致）；
    /// 弃权/未出牌用弃权牌面，未知牌名返回 null（保持纯文字呈现）。</summary>
    static Sprite GetCardFrontSprite(string card)
    {
        string value = string.IsNullOrEmpty(card) ? "" : card.Trim().ToUpperInvariant();
        string fileName;
        if (value.Length == 0 || value.Contains("ABSTAIN") || value == "NONE" || value == "PASS") fileName = "card_abstain.png";
        else if (value.Contains("BING_ZHENG") || value.Contains("BINGZHENG")) fileName = "card_bing_zheng.png";
        else if (value.Contains("QIANG_XING") || value.Contains("QIANGXING")) fileName = "card_qiang_xing.png";
        else if (value.Contains("XIAN_GONG") || value.Contains("XIANGONG")) fileName = "card_xian_gong.png";
        else if (value.Contains("YAN_DIE") || value.Contains("YANDIE")) fileName = "card_yan_die.png";
        else return null;
        return GetThemeSprite(fileName);
    }

    /// <summary>对抗暗角：64x64 程序化径向渐变（中心透明、四角黑 a~0.5），双线性放大后即是柔和暗角。</summary>
    static Sprite GetVignetteSprite()
    {
        if (vignetteSprite != null) return vignetteSprite;
        const int size = 64;
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x / (float)(size - 1) * 2f - 1f;
                float dy = y / (float)(size - 1) * 2f - 1f;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                // 中心 0.45 半径内全透明，向四角（distance≈1.414）平滑压黑到 a0.5
                float alpha = Mathf.SmoothStep(0f, 0.5f, Mathf.InverseLerp(0.45f, 1.414f, distance));
                texture.SetPixel(x, y, new Color(0f, 0f, 0f, alpha));
            }
        }
        texture.Apply();
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;
        vignetteSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        return vignetteSprite;
    }

    // 自写缓动（不引 DOTween 等依赖）：EaseOutCubic = 1-(1-t)^3，入场快出、缓停到位
    static float EaseOutCubic(float t)
    {
        t = Mathf.Clamp01(t);
        float inverse = 1f - t;
        return 1f - inverse * inverse * inverse;
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
