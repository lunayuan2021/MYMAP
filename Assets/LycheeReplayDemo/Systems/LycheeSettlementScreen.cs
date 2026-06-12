using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.Video;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

/// <summary>
/// 终局结算界面：订阅 LycheeReplayPlayer.MatchOverRaised，比赛自然播完后暂停播放，
/// 0.5 秒黑场渐入，再全屏展示胜负横幅、双方六项分数明细（滚动跳字）、交付信息与
/// 「再看一遍 / 关闭」按钮。自建 Canvas(sortingOrder=220)，运行时自举，无需场景手工挂接。
/// </summary>
public sealed class LycheeSettlementScreen : MonoBehaviour
{
    /// <summary>全局开关：置 true 后不再弹出结算界面。</summary>
    public static bool Disabled;

    const int RowCount = 6;
    const float RowSpacing = 52f;
    const float NumberRollSeconds = 1.2f;
    const float ColumnStaggerSeconds = 0.15f;
    const float RowStaggerSeconds = 0.12f;

    static readonly string[] RowLabels =
    {
        "送达基础分", "好果数量分", "鲜度品质分", "用时分", "皇榜任务分", "惩罚"
    };

    static readonly Color GoldColor = new Color(1f, 0.84f, 0.40f, 1f);
    static readonly Color GoldDimColor = new Color(0.93f, 0.80f, 0.50f, 0.94f);
    static readonly Color GoldLineColor = new Color(0.86f, 0.70f, 0.32f, 0.85f);
    static readonly Color RedTextColor = new Color(1f, 0.44f, 0.36f, 1f);
    static readonly Color BlueTextColor = new Color(0.46f, 0.66f, 1f, 1f);
    static readonly Color PenaltyColor = new Color(1f, 0.28f, 0.20f, 1f);
    static readonly Color GrayColor = new Color(0.62f, 0.62f, 0.58f, 0.92f);
    static readonly Color BodyTextColor = new Color(0.88f, 0.88f, 0.82f, 0.95f);

    LycheeReplayPlayer player;
    GameObject canvasRoot;
    // 结算背景视频（victory.mp4 循环；失败回退静态 settlement_bg.png）
    VideoPlayer victoryVideo;
    RenderTexture victoryVideoTexture;
    RawImage victoryVideoImage;
    bool victoryVideoFailed;
    CanvasGroup fadeGroup;
    CanvasGroup panelGroup;
    CanvasGroup bannerGroup;
    RectTransform bannerRect;
    Text bannerTitle;
    Text bannerScore;
    Text redHeader;
    Text blueHeader;
    readonly CanvasGroup[] rowGroups = new CanvasGroup[RowCount];
    readonly Text[] redCells = new Text[RowCount];
    readonly Text[] blueCells = new Text[RowCount];
    CanvasGroup redTotalGroup;
    CanvasGroup blueTotalGroup;
    RectTransform redTotalRect;
    RectTransform blueTotalRect;
    Text redTotalText;
    Text blueTotalText;
    CanvasGroup auxGroup;
    Text redAuxText;
    Text blueAuxText;
    CanvasGroup buttonsGroup;
    CanvasGroup mvpGroup;
    RectTransform mvpRect;
    RectTransform mvpAvatarRect;
    RectTransform mvpSparkleLayer;
    Image mvpAvatarImage;
    Text mvpAvatarGlyph;
    Text mvpTitleText;
    Text mvpStatsText;
    Sprite starSprite;
    Font font;

    readonly int?[] redTargets = new int?[RowCount];
    readonly int?[] blueTargets = new int?[RowCount];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        LycheeReplayPlayer host = FindFirstObjectByType<LycheeReplayPlayer>();
        if (host == null) return;
        if (host.GetComponent<LycheeSettlementScreen>() != null) return;
        host.gameObject.AddComponent<LycheeSettlementScreen>();
    }

    void Awake()
    {
        player = GetComponent<LycheeReplayPlayer>();
        if (player == null) player = FindFirstObjectByType<LycheeReplayPlayer>();
    }

    void OnEnable()
    {
        LycheeReplayPlayer.MatchOverRaised += HandleMatchOver;
    }

    void OnDisable()
    {
        LycheeReplayPlayer.MatchOverRaised -= HandleMatchOver;
    }

    void HandleMatchOver(LycheeReplayOverFrame over)
    {
        if (Disabled || !isActiveAndEnabled) return;
        if (player != null) player.SetPlaying(false);
        try
        {
            EnsureEventSystem();
            EnsureBuilt();
            Populate(over);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("结算界面构建失败：" + exception.Message, this);
            return;
        }
        if (canvasRoot == null) return;
        StopAllCoroutines();
        StartCoroutine(PlayIntroSequence());
    }

    // ---------------------------------------------------------------- data

    void Populate(LycheeReplayOverFrame over)
    {
        LycheeReplayOverPlayer redOver = FindOverPlayer(over, 0);
        LycheeReplayOverPlayer blueOver = FindOverPlayer(over, 1);
        LycheeReplayPlayerState redFrame = FindFramePlayer(0);
        LycheeReplayPlayerState blueFrame = FindFramePlayer(1);
        LycheeReplayScores scores = player == null || player.CurrentFrame == null ? null : player.CurrentFrame.scores;

        int redTotal = ResolveTotal(redOver, scores, redFrame, 0);
        int blueTotal = ResolveTotal(blueOver, scores, blueFrame, 1);

        SetText(bannerTitle, ResolveWinnerTitle(over, redTotal, blueTotal));
        SetText(bannerScore, "最终比分 " + redTotal + " : " + blueTotal);
        SetText(redHeader, HeaderText(0, redOver, redFrame));
        SetText(blueHeader, HeaderText(1, blueOver, blueFrame));

        FillTargets(redTargets, redOver);
        FillTargets(blueTargets, blueOver);
        for (int i = 0; i < RowCount; i++)
        {
            bool penaltyRow = i == RowCount - 1;
            if (redCells[i] != null)
            {
                redCells[i].color = penaltyRow ? PenaltyColor : RedTextColor;
                redCells[i].text = redTargets[i].HasValue ? "0" : "--";
            }
            if (blueCells[i] != null)
            {
                blueCells[i].color = penaltyRow ? PenaltyColor : BlueTextColor;
                blueCells[i].text = blueTargets[i].HasValue ? "0" : "--";
            }
        }

        SetText(redTotalText, redTotal.ToString());
        SetText(blueTotalText, blueTotal.ToString());
        ApplyAux(redAuxText, redOver, redFrame);
        ApplyAux(blueAuxText, blueOver, blueFrame);

        int winnerCamp = ResolveWinnerCamp(over, redTotal, blueTotal);
        PopulateMvp(winnerCamp,
            winnerCamp == 0 ? redOver : blueOver,
            winnerCamp == 0 ? redFrame : blueFrame);
    }

    int ResolveWinnerCamp(LycheeReplayOverFrame over, int redTotal, int blueTotal)
    {
        if (over != null && over.players != null)
        {
            LycheeReplayOverPlayer winner = over.players
                .FirstOrDefault(candidate => candidate != null && candidate.playerId == over.winnerPlayerId);
            if (winner != null) return winner.camp == 1 ? 1 : 0;
        }
        return blueTotal > redTotal ? 1 : 0;
    }

    void PopulateMvp(int camp, LycheeReplayOverPlayer overPlayer, LycheeReplayPlayerState framePlayer)
    {
        Color campColor = camp == 0 ? RedTextColor : BlueTextColor;
        string team = camp == 0 ? "红队" : "蓝队";
        string name = overPlayer != null && !string.IsNullOrEmpty(overPlayer.playerName)
            ? overPlayer.playerName
            : framePlayer != null && !string.IsNullOrEmpty(framePlayer.name) ? framePlayer.name : team;
        SetText(mvpTitleText, "贡运状元 · " + name);

        string stats;
        if (overPlayer != null)
        {
            string deliver = overPlayer.delivered
                ? "交付回合 R" + Mathf.Max(0, overPlayer.deliverRound)
                : "未完成交付";
            stats = deliver +
                " / 交付鲜度 " + overPlayer.freshness.ToString("0.#") +
                " / 皇榜任务分 " + overPlayer.taskScore;
        }
        else if (framePlayer != null)
        {
            stats = (framePlayer.delivered ? "已完成交付" : "未完成交付") +
                " / 鲜度 " + framePlayer.freshness.ToString("0.#") +
                " / 总分 " + framePlayer.score;
        }
        else
        {
            stats = "战绩数据缺失";
        }
        SetText(mvpStatsText, stats);

        Sprite avatar = null;
        if (player != null) avatar = camp == 0 ? player.redCaptainAvatar : player.blueCaptainAvatar;
        if (mvpAvatarImage != null)
        {
            if (avatar != null)
            {
                mvpAvatarImage.sprite = avatar;
                mvpAvatarImage.color = Color.white;
            }
            else
            {
                mvpAvatarImage.sprite = null;
                mvpAvatarImage.color = new Color(campColor.r * 0.55f, campColor.g * 0.55f, campColor.b * 0.55f, 1f);
            }
        }
        if (mvpAvatarGlyph != null) mvpAvatarGlyph.gameObject.SetActive(avatar == null);
    }

    string ResolveWinnerTitle(LycheeReplayOverFrame over, int redTotal, int blueTotal)
    {
        if (over != null)
        {
            string winnerName = null;
            if (over.players != null)
            {
                LycheeReplayOverPlayer winner = over.players
                    .FirstOrDefault(candidate => candidate != null && candidate.playerId == over.winnerPlayerId);
                if (winner != null && !string.IsNullOrEmpty(winner.playerName)) winnerName = winner.playerName;
            }
            if (string.IsNullOrEmpty(winnerName) && player != null)
                winnerName = player.TeamDisplayName(over.winnerPlayerId);
            if (string.IsNullOrEmpty(winnerName)) winnerName = "胜方";
            return winnerName + " 夺魁";
        }

        if (redTotal == blueTotal) return "双方战平";
        int winnerCamp = redTotal > blueTotal ? 0 : 1;
        LycheeReplayPlayerState framePlayer = FindFramePlayer(winnerCamp);
        string display = null;
        if (framePlayer != null && player != null) display = player.TeamDisplayName(framePlayer.playerId);
        if (string.IsNullOrEmpty(display)) display = winnerCamp == 0 ? "红队" : "蓝队";
        return display + " 夺魁";
    }

    static string HeaderText(int camp, LycheeReplayOverPlayer overPlayer, LycheeReplayPlayerState framePlayer)
    {
        string team = camp == 0 ? "红队" : "蓝队";
        string name = overPlayer != null && !string.IsNullOrEmpty(overPlayer.playerName)
            ? overPlayer.playerName
            : framePlayer != null && !string.IsNullOrEmpty(framePlayer.name) ? framePlayer.name : null;
        return string.IsNullOrEmpty(name) ? team : team + " · " + name;
    }

    static void FillTargets(int?[] targets, LycheeReplayOverPlayer overPlayer)
    {
        LycheeReplayScoreDetail detail = overPlayer == null ? null : overPlayer.scoreDetail;
        if (detail == null)
        {
            for (int i = 0; i < targets.Length; i++) targets[i] = null;
            return;
        }
        targets[0] = detail.delivery;
        targets[1] = detail.goodFruit;
        targets[2] = detail.freshness;
        targets[3] = detail.time;
        targets[4] = detail.tasks;
        targets[5] = -Mathf.Abs(detail.penalty);
    }

    int ResolveTotal(LycheeReplayOverPlayer overPlayer, LycheeReplayScores scores, LycheeReplayPlayerState framePlayer, int camp)
    {
        if (overPlayer != null) return overPlayer.totalScore;
        if (scores != null) return camp == 0 ? scores.RED : scores.BLUE;
        return framePlayer == null ? 0 : framePlayer.score;
    }

    static void ApplyAux(Text text, LycheeReplayOverPlayer overPlayer, LycheeReplayPlayerState framePlayer)
    {
        if (text == null) return;
        bool delivered = overPlayer != null ? overPlayer.delivered : framePlayer != null && framePlayer.delivered;
        if (!delivered)
        {
            text.text = "未完成交付";
            text.color = GrayColor;
            return;
        }
        text.color = BodyTextColor;
        if (overPlayer != null)
        {
            text.text = "交付回合 R" + Mathf.Max(0, overPlayer.deliverRound) +
                " / 交付鲜度 " + overPlayer.freshness.ToString("0.#") +
                " / 好果 " + overPlayer.goodFruit + " 坏果 " + overPlayer.badFruit;
            return;
        }
        text.text = "交付鲜度 " + framePlayer.freshness.ToString("0.#") +
            " / 好果 " + framePlayer.goodFruit + " 坏果 " + framePlayer.badFruit;
    }

    static LycheeReplayOverPlayer FindOverPlayer(LycheeReplayOverFrame over, int camp)
    {
        if (over == null || over.players == null) return null;
        return over.players.FirstOrDefault(candidate => candidate != null && candidate.camp == camp);
    }

    LycheeReplayPlayerState FindFramePlayer(int camp)
    {
        LycheeReplayFrame frame = player == null ? null : player.CurrentFrame;
        if (frame == null || frame.players == null) return null;
        return frame.players.FirstOrDefault(candidate => candidate != null && candidate.camp == camp);
    }

    // ---------------------------------------------------------------- intro sequence

    IEnumerator PlayIntroSequence()
    {
        if (canvasRoot == null) yield break;
        canvasRoot.SetActive(true);
        StartVictoryVideo();

        SetGroupAlpha(fadeGroup, 0f);
        SetGroupAlpha(panelGroup, 0f);
        SetGroupAlpha(bannerGroup, 0f);
        for (int i = 0; i < RowCount; i++) SetGroupAlpha(rowGroups[i], 0f);
        SetGroupAlpha(redTotalGroup, 0f);
        SetGroupAlpha(blueTotalGroup, 0f);
        if (redTotalRect != null) redTotalRect.localScale = new Vector3(0.6f, 0.6f, 1f);
        if (blueTotalRect != null) blueTotalRect.localScale = new Vector3(0.6f, 0.6f, 1f);
        SetGroupAlpha(mvpGroup, 0f);
        if (mvpAvatarRect != null) mvpAvatarRect.localScale = Vector3.zero;
        ClearSparkles();
        SetGroupAlpha(auxGroup, 0f);
        if (buttonsGroup != null)
        {
            buttonsGroup.alpha = 0f;
            buttonsGroup.interactable = false;
            buttonsGroup.blocksRaycasts = false;
        }

        // 1) 0.5 秒黑色渐入
        yield return FadeGroup(fadeGroup, 0f, 1f, 0.5f);
        // 2) 背景面板
        yield return FadeGroup(panelGroup, 0f, 1f, 0.25f);
        // 3) 胜负横幅 0.3 秒
        yield return AnimateBanner(0.3f);
        // 4) 分数表逐行（每行 +0.12s；列内红蓝错峰 0.15s）
        for (int i = 0; i < RowCount; i++)
            StartCoroutine(PlayRow(i, i * RowStaggerSeconds));
        yield return new WaitForSecondsRealtime(RowCount * RowStaggerSeconds + NumberRollSeconds + ColumnStaggerSeconds + 0.35f);
        // 5) 总分弹出
        StartCoroutine(PopTotal(redTotalRect, redTotalGroup, 0f));
        StartCoroutine(PopTotal(blueTotalRect, blueTotalGroup, ColumnStaggerSeconds));
        yield return new WaitForSecondsRealtime(0.62f);
        // 5.5) 本局最佳：头像 0→1 回弹 + 金色星光闪散
        yield return PlayMvpReveal();
        // 6) 辅助信息 + 按钮
        StartCoroutine(FadeGroup(auxGroup, 0f, 1f, 0.3f));
        yield return FadeGroup(buttonsGroup, 0f, 1f, 0.35f);
        if (buttonsGroup != null)
        {
            buttonsGroup.interactable = true;
            buttonsGroup.blocksRaycasts = true;
        }
    }

    IEnumerator AnimateBanner(float duration)
    {
        if (bannerGroup == null || bannerRect == null) yield break;
        Vector2 target = new Vector2(0f, -180f);
        Vector2 from = target + new Vector2(0f, 36f);
        float time = 0f;
        while (time < duration)
        {
            time += Time.unscaledDeltaTime;
            float k = EaseOutCubic(time / duration);
            bannerGroup.alpha = k;
            bannerRect.anchoredPosition = Vector2.LerpUnclamped(from, target, k);
            yield return null;
        }
        bannerGroup.alpha = 1f;
        bannerRect.anchoredPosition = target;
    }

    IEnumerator PlayRow(int index, float delay)
    {
        if (index < 0 || index >= RowCount) yield break;
        if (delay > 0f) yield return new WaitForSecondsRealtime(delay);
        if (rowGroups[index] != null) StartCoroutine(FadeGroup(rowGroups[index], 0f, 1f, 0.25f));
        if (redTargets[index].HasValue) StartCoroutine(RollNumber(redCells[index], redTargets[index].Value, 0f));
        else if (redCells[index] != null) redCells[index].text = "--";
        if (blueTargets[index].HasValue) StartCoroutine(RollNumber(blueCells[index], blueTargets[index].Value, ColumnStaggerSeconds));
        else if (blueCells[index] != null) blueCells[index].text = "--";
    }

    IEnumerator RollNumber(Text text, int target, float delay)
    {
        if (text == null) yield break;
        if (delay > 0f) yield return new WaitForSecondsRealtime(delay);
        float time = 0f;
        while (time < NumberRollSeconds)
        {
            time += Time.unscaledDeltaTime;
            int value = Mathf.RoundToInt(Mathf.Lerp(0f, target, EaseOutCubic(time / NumberRollSeconds)));
            text.text = value.ToString();
            yield return null;
        }
        text.text = target.ToString();
    }

    IEnumerator PopTotal(RectTransform rect, CanvasGroup group, float delay)
    {
        if (rect == null || group == null) yield break;
        if (delay > 0f) yield return new WaitForSecondsRealtime(delay);
        const float duration = 0.45f;
        float time = 0f;
        while (time < duration)
        {
            time += Time.unscaledDeltaTime;
            float k = Mathf.Clamp01(time / duration);
            group.alpha = Mathf.Clamp01(k * 2f);
            float scale = Mathf.LerpUnclamped(0.6f, 1f, EaseOutBack(k));
            rect.localScale = new Vector3(scale, scale, 1f);
            yield return null;
        }
        group.alpha = 1f;
        rect.localScale = Vector3.one;
    }

    IEnumerator PlayMvpReveal()
    {
        if (mvpGroup == null) yield break;
        StartCoroutine(FadeGroup(mvpGroup, 0f, 1f, 0.28f));
        if (mvpAvatarRect != null)
        {
            const float duration = 0.5f;
            float time = 0f;
            while (time < duration)
            {
                time += Time.unscaledDeltaTime;
                float scale = Mathf.Max(0f, Mathf.LerpUnclamped(0f, 1f, EaseOutBack(time / duration)));
                mvpAvatarRect.localScale = new Vector3(scale, scale, 1f);
                yield return null;
            }
            mvpAvatarRect.localScale = Vector3.one;
        }
        StartCoroutine(PlayMvpSparkles());
        yield return new WaitForSecondsRealtime(0.15f);
    }

    IEnumerator PlayMvpSparkles()
    {
        if (mvpSparkleLayer == null || mvpAvatarRect == null) yield break;
        ClearSparkles();
        const int count = 10;
        RectTransform[] sparkles = new RectTransform[count];
        Image[] images = new Image[count];
        float[] angles = new float[count];
        float[] spins = new float[count];
        float[] radii = new float[count];
        Vector2 center = mvpAvatarRect.anchoredPosition;
        for (int i = 0; i < count; i++)
        {
            angles[i] = 360f / count * i + UnityEngine.Random.Range(-14f, 14f);
            spins[i] = UnityEngine.Random.Range(140f, 340f) * (i % 2 == 0 ? 1f : -1f);
            radii[i] = UnityEngine.Random.Range(56f, 96f);
            float size = UnityEngine.Random.Range(10f, 22f);
            RectTransform rect = MakeRect(
                "MvpSparkle_" + i, mvpSparkleLayer,
                new Vector2(0.5f, 0.5f), center, new Vector2(size, size), new Vector2(0.5f, 0.5f));
            rect.localRotation = Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 90f));
            Image image = rect.gameObject.AddComponent<Image>();
            if (starSprite != null) image.sprite = starSprite;
            image.color = GoldColor;
            image.raycastTarget = false;
            sparkles[i] = rect;
            images[i] = image;
        }

        const float duration = 0.85f;
        float time = 0f;
        while (time < duration)
        {
            float delta = Time.unscaledDeltaTime;
            time += delta;
            float k = Mathf.Clamp01(time / duration);
            float eased = EaseOutCubic(k);
            float alpha = Mathf.Clamp01(k * 6f) * (1f - k);
            float scale = Mathf.Lerp(1f, 0.35f, k);
            for (int i = 0; i < count; i++)
            {
                if (sparkles[i] == null) continue;
                float radians = angles[i] * Mathf.Deg2Rad;
                Vector2 direction = new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
                sparkles[i].anchoredPosition = center + direction * Mathf.Lerp(14f, radii[i], eased);
                sparkles[i].Rotate(0f, 0f, spins[i] * delta);
                sparkles[i].localScale = new Vector3(scale, scale, 1f);
                if (images[i] != null)
                    images[i].color = new Color(GoldColor.r, GoldColor.g, GoldColor.b, alpha);
            }
            yield return null;
        }
        for (int i = 0; i < count; i++)
            if (sparkles[i] != null) Destroy(sparkles[i].gameObject);
    }

    void ClearSparkles()
    {
        if (mvpSparkleLayer == null) return;
        for (int i = mvpSparkleLayer.childCount - 1; i >= 0; i--)
        {
            Transform child = mvpSparkleLayer.GetChild(i);
            if (child != null) Destroy(child.gameObject);
        }
    }

    IEnumerator FadeGroup(CanvasGroup group, float from, float to, float duration)
    {
        if (group == null) yield break;
        if (duration <= 0f)
        {
            group.alpha = to;
            yield break;
        }
        float time = 0f;
        while (time < duration)
        {
            time += Time.unscaledDeltaTime;
            group.alpha = Mathf.Lerp(from, to, EaseOutCubic(time / duration));
            yield return null;
        }
        group.alpha = to;
    }

    static float EaseOutCubic(float t)
    {
        t = Mathf.Clamp01(t);
        return 1f - Mathf.Pow(1f - t, 3f);
    }

    static float EaseOutBack(float t)
    {
        t = Mathf.Clamp01(t);
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        return 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f);
    }

    // ---------------------------------------------------------------- buttons

    void OnReplayClicked()
    {
        HideScreen();
        if (player == null) return;
        player.SeekToRound(1);
        player.SetPlaying(true);
    }

    void OnCloseClicked()
    {
        HideScreen();
    }

    void HideScreen()
    {
        StopAllCoroutines();
        StopVictoryVideo();
        if (canvasRoot != null) canvasRoot.SetActive(false);
    }

    static void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null) return;
        GameObject eventSystem = new GameObject("LycheeSettlementEventSystem");
        eventSystem.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
        eventSystem.AddComponent<InputSystemUIInputModule>();
#else
        eventSystem.AddComponent<StandaloneInputModule>();
#endif
    }

    // ---------------------------------------------------------------- build UI

    void EnsureBuilt()
    {
        if (canvasRoot != null) return;
        font = LoadChineseFont();

        canvasRoot = new GameObject(
            "LycheeSettlementCanvas",
            typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasRoot.transform.SetParent(transform, false);
        Canvas canvas = canvasRoot.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 220;
        CanvasScaler scaler = canvasRoot.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        RectTransform fadeRect = MakeStretch("FadeBlack", canvasRoot.transform, Vector2.zero, Vector2.zero);
        Image fadeImage = fadeRect.gameObject.AddComponent<Image>();
        fadeImage.color = Color.black;
        fadeImage.raycastTarget = true;
        fadeGroup = fadeRect.gameObject.AddComponent<CanvasGroup>();
        fadeGroup.alpha = 0f;

        RectTransform panelRect = MakeStretch("SettlementPanel", canvasRoot.transform, Vector2.zero, Vector2.zero);
        panelGroup = panelRect.gameObject.AddComponent<CanvasGroup>();
        panelGroup.alpha = 0f;

        BuildBackground(panelRect);
        BuildBanner(panelRect);
        BuildScoreTable(panelRect);
        BuildAux(panelRect);
        BuildMvp(panelRect);
        BuildButtons(panelRect);

        canvasRoot.SetActive(false);
    }

    void BuildBackground(RectTransform parent)
    {
        RectTransform container = MakeStretch("Background", parent, Vector2.zero, Vector2.zero);
        container.gameObject.AddComponent<RectMask2D>();

        Sprite sprite = LoadThemeSprite("settlement_bg.png");
        if (sprite != null)
        {
            RectTransform imageRect = MakeRect(
                "BackdropImage", container,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1920f, 1080f), new Vector2(0.5f, 0.5f));
            Image image = imageRect.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.color = Color.white;
            image.raycastTarget = false;
            AspectRatioFitter fitter = imageRect.gameObject.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
            fitter.aspectRatio = sprite.rect.height <= 0f ? 16f / 9f : sprite.rect.width / sprite.rect.height;
            // 静态图保留作视频加载期间/失败时的兜底，视频层叠在其上
            BuildVictoryVideo(container);
            return;
        }

        RectTransform gradientRect = MakeStretch("BackdropGradient", container, Vector2.zero, Vector2.zero);
        Image gradient = gradientRect.gameObject.AddComponent<Image>();
        Sprite gradientSprite = CreateVerticalGradientSprite(
            new Color(0.02f, 0.02f, 0.04f, 1f),
            new Color(0.06f, 0.05f, 0.10f, 1f));
        if (gradientSprite != null)
        {
            gradient.sprite = gradientSprite;
            gradient.color = Color.white;
        }
        else
        {
            gradient.color = new Color(0.04f, 0.035f, 0.07f, 1f);
        }
        gradient.raycastTarget = false;
        BuildVictoryVideo(container);
        BuildGoldBorder(container);
    }

    /// <summary>
    /// 结算背景视频层：素材清单约定 StreamingAssets/Video/victory.mp4 为"胜利/结算背景循环"。
    /// 叠在静态背景之上、金边框之下；准备成功才显示（RawImage 初始隐藏），
    /// 解码失败/文件缺失走 errorReceived 永久回退静态图——和开场视频同款兜底口径。
    /// </summary>
    void BuildVictoryVideo(RectTransform parent)
    {
        string videoUrl;
#if UNITY_WEBGL && !UNITY_EDITOR
        // WebGL：StreamingAssets 是 URL，不能 File.Exists 探测，直接设 URL 靠错误回调兜底
        videoUrl = Application.streamingAssetsPath + "/Video/victory.mp4";
#else
        try
        {
            string path = System.IO.Path.Combine(Application.streamingAssetsPath, "Video", "victory.mp4");
            if (!System.IO.File.Exists(path)) return;
            videoUrl = "file://" + path;
        }
        catch (Exception)
        {
            return;
        }
#endif

        victoryVideoTexture = new RenderTexture(1280, 720, 0) { name = "LycheeVictoryVideoRT" };

        RectTransform rect = MakeRect(
            "VictoryVideo", parent,
            new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1920f, 1080f), new Vector2(0.5f, 0.5f));
        victoryVideoImage = rect.gameObject.AddComponent<RawImage>();
        victoryVideoImage.texture = victoryVideoTexture;
        victoryVideoImage.color = Color.white;
        victoryVideoImage.raycastTarget = false;
        AspectRatioFitter videoFitter = rect.gameObject.AddComponent<AspectRatioFitter>();
        videoFitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
        videoFitter.aspectRatio = 16f / 9f;

        victoryVideo = rect.gameObject.AddComponent<VideoPlayer>();
        victoryVideo.playOnAwake = false;
        victoryVideo.isLooping = true;
        victoryVideo.renderMode = VideoRenderMode.RenderTexture;
        victoryVideo.targetTexture = victoryVideoTexture;
        victoryVideo.audioOutputMode = VideoAudioOutputMode.None; // 配乐由 AudioDirector 负责，视频只出画面
        victoryVideo.url = videoUrl;
        victoryVideo.prepareCompleted += OnVictoryVideoPrepared;
        victoryVideo.errorReceived += OnVictoryVideoError;
        rect.gameObject.SetActive(false);
    }

    void StartVictoryVideo()
    {
        if (victoryVideo == null || victoryVideoFailed) return;
        // RawImage 物体先激活（VideoPlayer 在非激活物体上不会推进 Prepare），
        // 但画面用 alpha 藏到 prepareCompleted 再亮，避免一帧黑闪
        victoryVideo.gameObject.SetActive(true);
        if (victoryVideoImage != null) victoryVideoImage.color = new Color(1f, 1f, 1f, 0f);
        if (victoryVideo.isPrepared) OnVictoryVideoPrepared(victoryVideo);
        else victoryVideo.Prepare();
    }

    void StopVictoryVideo()
    {
        if (victoryVideo == null) return;
        if (victoryVideo.isPlaying) victoryVideo.Stop();
        victoryVideo.gameObject.SetActive(false);
    }

    void OnVictoryVideoPrepared(VideoPlayer source)
    {
        if (victoryVideoFailed || source == null) return;
        if (victoryVideoImage != null) victoryVideoImage.color = Color.white;
        source.Play();
    }

    void OnVictoryVideoError(VideoPlayer source, string message)
    {
        // 永久回退静态背景：标记失败防止下次 Show 再尝试（WebGL 下错误通常不可恢复）
        victoryVideoFailed = true;
        if (victoryVideo != null) victoryVideo.gameObject.SetActive(false);
        Debug.LogWarning("[LycheeSettlementScreen] victory.mp4 播放失败，回退静态背景: " + message);
    }

    void BuildGoldBorder(RectTransform parent)
    {
        MakeEdgeLine(parent, "BorderTop", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -14f), new Vector2(-28f, 2f), GoldLineColor);
        MakeEdgeLine(parent, "BorderBottom", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
            new Vector2(0f, 14f), new Vector2(-28f, 2f), GoldLineColor);
        MakeEdgeLine(parent, "BorderLeft", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f),
            new Vector2(14f, 0f), new Vector2(2f, -28f), GoldLineColor);
        MakeEdgeLine(parent, "BorderRight", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f),
            new Vector2(-14f, 0f), new Vector2(2f, -28f), GoldLineColor);
    }

    void BuildBanner(RectTransform parent)
    {
        bannerRect = MakeRect(
            "VictoryBanner", parent,
            new Vector2(0.5f, 1f), new Vector2(0f, -180f), new Vector2(920f, 250f), new Vector2(0.5f, 0.5f));
        bannerGroup = bannerRect.gameObject.AddComponent<CanvasGroup>();
        bannerGroup.alpha = 0f;

        Sprite sprite = LoadThemeSprite("victory_banner.png");
        if (sprite != null)
        {
            RectTransform imageRect = MakeStretch("BannerImage", bannerRect, Vector2.zero, Vector2.zero);
            Image image = imageRect.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.color = Color.white;
            image.preserveAspect = true;
            image.raycastTarget = false;
        }
        else
        {
            RectTransform band = MakeRect(
                "BannerBand", bannerRect,
                new Vector2(0.5f, 0.5f), new Vector2(0f, 18f), new Vector2(760f, 118f), new Vector2(0.5f, 0.5f));
            Image bandImage = band.gameObject.AddComponent<Image>();
            bandImage.color = new Color(0.07f, 0.05f, 0.10f, 0.80f);
            bandImage.raycastTarget = false;
            Outline outline = band.gameObject.AddComponent<Outline>();
            outline.effectColor = GoldLineColor;
            outline.effectDistance = new Vector2(2f, -2f);
        }

        bannerTitle = MakeText("BannerTitle", bannerRect, 56, FontStyle.Bold, GoldColor, TextAnchor.MiddleCenter);
        SetRect(bannerTitle.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 22f), new Vector2(880f, 76f), new Vector2(0.5f, 0.5f));
        AddTextOutline(bannerTitle, 2f);
        AddTextShadow(bannerTitle, 2f, -3f, 0.7f);
        bannerScore = MakeText("BannerScore", bannerRect, 26, FontStyle.Bold, BodyTextColor, TextAnchor.MiddleCenter);
        SetRect(bannerScore.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, -58f), new Vector2(640f, 36f), new Vector2(0.5f, 0.5f));
        AddTextOutline(bannerScore, 1.2f);
        AddTextShadow(bannerScore, 1f, -1f, 0.65f);
    }

    void BuildScoreTable(RectTransform parent)
    {
        // 半透明深色衬底：覆盖表头至交付信息整块（含 24px 内边距），让数字从灯火背景中跳出
        RectTransform backing = MakeRect(
            "ScoreTableBacking", parent,
            new Vector2(0.5f, 1f), new Vector2(0f, -588f), new Vector2(1088f, 562f), new Vector2(0.5f, 0.5f));
        Image backingImage = backing.gameObject.AddComponent<Image>();
        backingImage.color = new Color(0.02f, 0.02f, 0.05f, 0.55f);
        backingImage.raycastTarget = false;
        Outline backingOutline = backing.gameObject.AddComponent<Outline>();
        backingOutline.effectColor = new Color(GoldLineColor.r, GoldLineColor.g, GoldLineColor.b, 0.35f);
        backingOutline.effectDistance = new Vector2(1.5f, -1.5f);

        redHeader = MakeText("RedColumnHeader", parent, 24, FontStyle.Bold, RedTextColor, TextAnchor.MiddleCenter);
        SetRect(redHeader.rectTransform, new Vector2(0.5f, 1f), new Vector2(-270f, -348f), new Vector2(460f, 32f), new Vector2(0.5f, 0.5f));
        AddTextShadow(redHeader, 1f, -1f, 0.65f);
        blueHeader = MakeText("BlueColumnHeader", parent, 24, FontStyle.Bold, BlueTextColor, TextAnchor.MiddleCenter);
        SetRect(blueHeader.rectTransform, new Vector2(0.5f, 1f), new Vector2(270f, -348f), new Vector2(460f, 32f), new Vector2(0.5f, 0.5f));
        AddTextShadow(blueHeader, 1f, -1f, 0.65f);
        AttachHeaderEmblem(redHeader, "team_red_emblem.png");
        AttachHeaderEmblem(blueHeader, "team_blue_emblem.png");

        for (int i = 0; i < RowCount; i++)
        {
            float y = -404f - RowSpacing * i;
            RectTransform row = MakeRect(
                "ScoreRow_" + i, parent,
                new Vector2(0.5f, 1f), new Vector2(0f, y), new Vector2(1040f, 46f), new Vector2(0.5f, 0.5f));
            rowGroups[i] = row.gameObject.AddComponent<CanvasGroup>();
            rowGroups[i].alpha = 0f;

            Text label = MakeText("Label", row, 22, FontStyle.Bold, GoldDimColor, TextAnchor.MiddleCenter);
            SetRect(label.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(220f, 40f), new Vector2(0.5f, 0.5f));
            label.text = i < RowLabels.Length ? RowLabels[i] : "";
            AddTextShadow(label, 1f, -1f, 0.6f);

            redCells[i] = MakeText("RedValue", row, 27, FontStyle.Bold, RedTextColor, TextAnchor.MiddleRight);
            SetRect(redCells[i].rectTransform, new Vector2(0.5f, 0.5f), new Vector2(-268f, 0f), new Vector2(270f, 40f), new Vector2(0.5f, 0.5f));
            AddTextShadow(redCells[i], 1f, -1f, 0.65f);
            blueCells[i] = MakeText("BlueValue", row, 27, FontStyle.Bold, BlueTextColor, TextAnchor.MiddleLeft);
            SetRect(blueCells[i].rectTransform, new Vector2(0.5f, 0.5f), new Vector2(268f, 0f), new Vector2(270f, 40f), new Vector2(0.5f, 0.5f));
            AddTextShadow(blueCells[i], 1f, -1f, 0.65f);
        }

        RectTransform divider = MakeRect(
            "TotalsDivider", parent,
            new Vector2(0.5f, 1f), new Vector2(0f, -700f), new Vector2(900f, 2f), new Vector2(0.5f, 0.5f));
        Image dividerImage = divider.gameObject.AddComponent<Image>();
        dividerImage.color = new Color(GoldLineColor.r, GoldLineColor.g, GoldLineColor.b, 0.4f);
        dividerImage.raycastTarget = false;

        redTotalRect = MakeRect(
            "RedTotal", parent,
            new Vector2(0.5f, 1f), new Vector2(-270f, -756f), new Vector2(360f, 92f), new Vector2(0.5f, 0.5f));
        redTotalGroup = redTotalRect.gameObject.AddComponent<CanvasGroup>();
        redTotalGroup.alpha = 0f;
        Text redTotalLabel = MakeText("Label", redTotalRect, 20, FontStyle.Bold, RedTextColor, TextAnchor.MiddleCenter);
        SetRect(redTotalLabel.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 32f), new Vector2(140f, 26f), new Vector2(0.5f, 0.5f));
        redTotalLabel.text = "总分";
        AddTextShadow(redTotalLabel, 1f, -1f, 0.6f);
        redTotalText = MakeText("Value", redTotalRect, 50, FontStyle.Bold, GoldColor, TextAnchor.MiddleCenter);
        SetRect(redTotalText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, -14f), new Vector2(340f, 60f), new Vector2(0.5f, 0.5f));
        AddTextShadow(redTotalText, 1.5f, -1.5f, 0.7f);

        blueTotalRect = MakeRect(
            "BlueTotal", parent,
            new Vector2(0.5f, 1f), new Vector2(270f, -756f), new Vector2(360f, 92f), new Vector2(0.5f, 0.5f));
        blueTotalGroup = blueTotalRect.gameObject.AddComponent<CanvasGroup>();
        blueTotalGroup.alpha = 0f;
        Text blueTotalLabel = MakeText("Label", blueTotalRect, 20, FontStyle.Bold, BlueTextColor, TextAnchor.MiddleCenter);
        SetRect(blueTotalLabel.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 32f), new Vector2(140f, 26f), new Vector2(0.5f, 0.5f));
        blueTotalLabel.text = "总分";
        AddTextShadow(blueTotalLabel, 1f, -1f, 0.6f);
        blueTotalText = MakeText("Value", blueTotalRect, 50, FontStyle.Bold, GoldColor, TextAnchor.MiddleCenter);
        SetRect(blueTotalText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, -14f), new Vector2(340f, 60f), new Vector2(0.5f, 0.5f));
        AddTextShadow(blueTotalText, 1.5f, -1.5f, 0.7f);
    }

    void AttachHeaderEmblem(Text header, string fileName)
    {
        if (header == null) return;
        Sprite sprite = LoadThemeSprite(fileName);
        if (sprite == null) return;
        RectTransform rect = MakeRect(
            "TeamEmblem", header.rectTransform,
            new Vector2(0f, 0.5f), new Vector2(42f, 0f), new Vector2(38f, 38f), new Vector2(0.5f, 0.5f));
        Image image = rect.gameObject.AddComponent<Image>();
        image.sprite = sprite;
        image.color = Color.white;
        image.preserveAspect = true;
        image.raycastTarget = false;
    }

    void BuildAux(RectTransform parent)
    {
        RectTransform container = MakeRect(
            "AuxInfoRow", parent,
            new Vector2(0.5f, 1f), new Vector2(0f, -828f), new Vector2(1200f, 34f), new Vector2(0.5f, 0.5f));
        auxGroup = container.gameObject.AddComponent<CanvasGroup>();
        auxGroup.alpha = 0f;
        redAuxText = MakeText("RedAux", container, 18, FontStyle.Normal, BodyTextColor, TextAnchor.MiddleCenter);
        SetRect(redAuxText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(-270f, 0f), new Vector2(540f, 30f), new Vector2(0.5f, 0.5f));
        AddTextShadow(redAuxText, 1f, -1f, 0.6f);
        blueAuxText = MakeText("BlueAux", container, 18, FontStyle.Normal, BodyTextColor, TextAnchor.MiddleCenter);
        SetRect(blueAuxText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(270f, 0f), new Vector2(540f, 30f), new Vector2(0.5f, 0.5f));
        AddTextShadow(blueAuxText, 1f, -1f, 0.6f);
    }

    void BuildMvp(RectTransform parent)
    {
        mvpRect = MakeRect(
            "MvpShowcase", parent,
            new Vector2(0.5f, 1f), new Vector2(0f, -928f), new Vector2(1000f, 116f), new Vector2(0.5f, 0.5f));
        mvpGroup = mvpRect.gameObject.AddComponent<CanvasGroup>();
        mvpGroup.alpha = 0f;

        RectTransform backing = MakeStretch("MvpBacking", mvpRect, Vector2.zero, Vector2.zero);
        Image backingImage = backing.gameObject.AddComponent<Image>();
        backingImage.color = new Color(0.03f, 0.025f, 0.06f, 0.5f);
        backingImage.raycastTarget = false;
        Outline backingOutline = backing.gameObject.AddComponent<Outline>();
        backingOutline.effectColor = new Color(GoldLineColor.r, GoldLineColor.g, GoldLineColor.b, 0.5f);
        backingOutline.effectDistance = new Vector2(1.5f, -1.5f);

        RectTransform tag = MakeRect(
            "MvpTag", mvpRect,
            new Vector2(0.5f, 1f), new Vector2(0f, 2f), new Vector2(168f, 34f), new Vector2(0.5f, 0.5f));
        Image tagImage = tag.gameObject.AddComponent<Image>();
        tagImage.color = new Color(0.24f, 0.16f, 0.05f, 0.95f);
        tagImage.raycastTarget = false;
        Outline tagOutline = tag.gameObject.AddComponent<Outline>();
        tagOutline.effectColor = GoldLineColor;
        tagOutline.effectDistance = new Vector2(1.5f, -1.5f);
        Text tagText = MakeText("Label", tag, 19, FontStyle.Bold, GoldColor, TextAnchor.MiddleCenter);
        SetRect(tagText.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(160f, 30f), new Vector2(0.5f, 0.5f));
        tagText.text = "本局最佳";
        AddTextShadow(tagText, 1f, -1f, 0.65f);

        // 圆形头像 + 金色描边环
        mvpAvatarRect = MakeRect(
            "MvpAvatar", mvpRect,
            new Vector2(0.5f, 0.5f), new Vector2(-340f, -4f), new Vector2(100f, 100f), new Vector2(0.5f, 0.5f));
        mvpAvatarRect.localScale = Vector3.zero;
        Sprite ringSprite = CreateCircleSprite(96, 0.84f);
        Sprite circleSprite = CreateCircleSprite(96, 0f);
        RectTransform ringRect = MakeStretch("GoldRing", mvpAvatarRect, Vector2.zero, Vector2.zero);
        Image ringImage = ringRect.gameObject.AddComponent<Image>();
        if (ringSprite != null) ringImage.sprite = ringSprite;
        ringImage.color = GoldColor;
        ringImage.raycastTarget = false;
        RectTransform maskRect = MakeRect(
            "AvatarMask", mvpAvatarRect,
            new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(86f, 86f), new Vector2(0.5f, 0.5f));
        Image maskImage = maskRect.gameObject.AddComponent<Image>();
        if (circleSprite != null) maskImage.sprite = circleSprite;
        maskImage.color = Color.white;
        maskImage.raycastTarget = false;
        Mask mask = maskRect.gameObject.AddComponent<Mask>();
        mask.showMaskGraphic = false;
        RectTransform avatarRect = MakeStretch("AvatarImage", maskRect, Vector2.zero, Vector2.zero);
        mvpAvatarImage = avatarRect.gameObject.AddComponent<Image>();
        mvpAvatarImage.color = Color.white;
        mvpAvatarImage.raycastTarget = false;
        mvpAvatarGlyph = MakeText("AvatarGlyph", maskRect, 40, FontStyle.Bold, GoldColor, TextAnchor.MiddleCenter);
        SetRect(mvpAvatarGlyph.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(80f, 60f), new Vector2(0.5f, 0.5f));
        mvpAvatarGlyph.text = "将";
        mvpAvatarGlyph.gameObject.SetActive(false);

        mvpTitleText = MakeText("MvpTitle", mvpRect, 28, FontStyle.Bold, GoldColor, TextAnchor.MiddleCenter);
        SetRect(mvpTitleText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(60f, 22f), new Vector2(760f, 40f), new Vector2(0.5f, 0.5f));
        AddTextOutline(mvpTitleText, 1.5f);
        AddTextShadow(mvpTitleText, 1.5f, -2f, 0.7f);
        mvpStatsText = MakeText("MvpStats", mvpRect, 20, FontStyle.Normal, BodyTextColor, TextAnchor.MiddleCenter);
        SetRect(mvpStatsText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(60f, -24f), new Vector2(800f, 32f), new Vector2(0.5f, 0.5f));
        AddTextShadow(mvpStatsText, 1f, -1f, 0.65f);

        mvpSparkleLayer = MakeStretch("MvpSparkleLayer", mvpRect, Vector2.zero, Vector2.zero);
        starSprite = CreateStarSprite(32);
    }

    void BuildButtons(RectTransform parent)
    {
        RectTransform container = MakeRect(
            "Buttons", parent,
            new Vector2(0.5f, 0f), new Vector2(0f, 48f), new Vector2(700f, 70f), new Vector2(0.5f, 0.5f));
        buttonsGroup = container.gameObject.AddComponent<CanvasGroup>();
        buttonsGroup.alpha = 0f;
        buttonsGroup.interactable = false;
        buttonsGroup.blocksRaycasts = false;
        MakeButton(container, "ReplayButton", "再看一遍", new Vector2(-150f, 0f),
            new Color(0.30f, 0.20f, 0.06f, 0.92f), GoldColor, OnReplayClicked);
        MakeButton(container, "CloseButton", "关闭", new Vector2(150f, 0f),
            new Color(0.10f, 0.10f, 0.12f, 0.92f), new Color(0.88f, 0.88f, 0.84f, 1f), OnCloseClicked);
    }

    void MakeButton(Transform parent, string objectName, string label, Vector2 position,
        Color background, Color textColor, UnityEngine.Events.UnityAction onClick)
    {
        RectTransform rect = MakeRect(objectName, parent, new Vector2(0.5f, 0.5f), position, new Vector2(240f, 62f), new Vector2(0.5f, 0.5f));
        Image image = rect.gameObject.AddComponent<Image>();
        image.color = background;
        image.raycastTarget = true;
        Outline outline = rect.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(GoldLineColor.r, GoldLineColor.g, GoldLineColor.b, 0.6f);
        outline.effectDistance = new Vector2(2f, -2f);
        Button button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(1.12f, 1.12f, 1.06f, 1f);
        colors.pressedColor = new Color(0.80f, 0.80f, 0.78f, 1f);
        colors.selectedColor = Color.white;
        button.colors = colors;
        if (onClick != null) button.onClick.AddListener(onClick);
        Text text = MakeText("Label", rect, 26, FontStyle.Bold, textColor, TextAnchor.MiddleCenter);
        SetRect(text.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(220f, 44f), new Vector2(0.5f, 0.5f));
        text.text = label;
    }

    // ---------------------------------------------------------------- low-level helpers

    Text MakeText(string objectName, Transform parent, int size, FontStyle style, Color color, TextAnchor anchor)
    {
        GameObject go = new GameObject(objectName, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        Text text = go.AddComponent<Text>();
        if (font != null) text.font = font;
        text.fontSize = size;
        text.fontStyle = style;
        text.color = color;
        text.alignment = anchor;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        return text;
    }

    static RectTransform MakeRect(string objectName, Transform parent, Vector2 anchor, Vector2 anchoredPosition, Vector2 size, Vector2 pivot)
    {
        GameObject go = new GameObject(objectName, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        SetRect(rt, anchor, anchoredPosition, size, pivot);
        return rt;
    }

    static void SetRect(RectTransform rt, Vector2 anchor, Vector2 anchoredPosition, Vector2 size, Vector2 pivot)
    {
        if (rt == null) return;
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = anchoredPosition;
        rt.sizeDelta = size;
        rt.localScale = Vector3.one;
    }

    static RectTransform MakeStretch(string objectName, Transform parent, Vector2 offsetMin, Vector2 offsetMax)
    {
        GameObject go = new GameObject(objectName, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;
        rt.localScale = Vector3.one;
        return rt;
    }

    static void MakeEdgeLine(Transform parent, string objectName, Vector2 anchorMin, Vector2 anchorMax,
        Vector2 pivot, Vector2 anchoredPosition, Vector2 sizeDelta, Color color)
    {
        GameObject go = new GameObject(objectName, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = pivot;
        rt.anchoredPosition = anchoredPosition;
        rt.sizeDelta = sizeDelta;
        rt.localScale = Vector3.one;
        Image image = go.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
    }

    static void AddTextOutline(Text text, float thickness)
    {
        if (text == null) return;
        Outline outline = text.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.8f);
        outline.effectDistance = new Vector2(thickness, -thickness);
    }

    static void AddTextShadow(Text text, float offsetX, float offsetY, float alpha)
    {
        if (text == null) return;
        Shadow shadow = text.gameObject.AddComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, alpha);
        shadow.effectDistance = new Vector2(offsetX, offsetY);
    }

    static void SetText(Text text, string value)
    {
        if (text != null) text.text = value;
    }

    static void SetGroupAlpha(CanvasGroup group, float alpha)
    {
        if (group != null) group.alpha = alpha;
    }

    static Font LoadChineseFont()
    {
        // 优先用随包字体：WebGL 取不到系统字体
        Font bundled = Resources.Load<Font>("Fonts/LXGWWenKai-Regular");
        if (bundled != null) return bundled;
        Font loaded = null;
        try
        {
            loaded = Font.CreateDynamicFontFromOSFont(
                new[] { "PingFang SC", "Heiti SC", "Arial Unicode MS" }, 32);
        }
        catch (Exception)
        {
            loaded = null;
        }
        if (loaded != null) return loaded;
        try
        {
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }
        catch (Exception)
        {
            return null;
        }
    }

    static Sprite LoadThemeSprite(string fileName)
    {
        try
        {
            byte[] bytes = LycheeThemeCache.LoadBytes(fileName);
            if (bytes == null || bytes.Length == 0) return null;
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(bytes)) return null;
            texture.wrapMode = TextureWrapMode.Clamp;
            return Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                100f);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>程序化圆形精灵：innerRatio 为 0 生成实心圆，大于 0 生成描边环。</summary>
    static Sprite CreateCircleSprite(int size, float innerRatio)
    {
        try
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            float center = (size - 1) * 0.5f;
            float outer = size * 0.5f - 1.2f;
            float inner = outer * Mathf.Clamp01(innerRatio);
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = Mathf.Clamp01(outer - distance + 0.8f);
                    if (innerRatio > 0f) alpha *= Mathf.Clamp01(distance - inner + 0.8f);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>程序化四角星光精灵，用于 MVP 头像入场的金色闪光。</summary>
    static Sprite CreateStarSprite(int size)
    {
        try
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            float center = (size - 1) * 0.5f;
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float nx = (x - center) / center;
                    float ny = (y - center) / center;
                    float radial = Mathf.Clamp01(1f - Mathf.Sqrt(nx * nx + ny * ny));
                    float arm = Mathf.Clamp01(1f - Mathf.Sqrt(Mathf.Abs(nx * ny)) * 2.4f);
                    float alpha = Mathf.Clamp01(arm * arm * (0.25f + 0.75f * radial));
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        }
        catch (Exception)
        {
            return null;
        }
    }

    static Sprite CreateVerticalGradientSprite(Color bottom, Color top)
    {
        try
        {
            const int height = 64;
            Texture2D texture = new Texture2D(1, height, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            for (int y = 0; y < height; y++)
                texture.SetPixel(0, y, Color.Lerp(bottom, top, y / (float)(height - 1)));
            texture.Apply(false, false);
            return Sprite.Create(texture, new Rect(0f, 0f, 1f, height), new Vector2(0.5f, 0.5f), 100f);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
