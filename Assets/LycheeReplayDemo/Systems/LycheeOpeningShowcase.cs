using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.Video;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

/// <summary>
/// 开局 MOBA 对阵演出（运行时自举）：
/// 1. 自举后若 CurrentRound &lt;= 5 自动展示一次；SeekToRound 跳回开局（round 从大跳回 &lt;=2）时再次展示。
/// 2. 背景优先播放 StreamingAssets/Video/opening.mp4（播完自动进入对阵图）；
///    无视频用 StreamingAssets/UITheme/opening_bg.png；都没有则用朱红/青蓝对角斜分割渐变兜底。
/// 3. 对阵图：左红右蓝队长头像 + 队名 + 副标，中央金色大 VS（弹入 + 闪光），上方标题、下方对局信息。
/// 4. 展示约 4.5 秒自动淡出，点击任意处立即关闭；期间暂停回放并屏蔽下层点击，结束恢复原播放状态。
/// </summary>
public class LycheeOpeningShowcase : MonoBehaviour
{
    /// <summary>全局禁用开关：true 时不自举，也不再触发展示。</summary>
    public static bool Disabled;

    const float ShowcaseSeconds = 2.0f;   // 对阵图阶段总时长（含淡出）。回放在此期间被暂停，结束即恢复，缩短只是让游戏更早 2.5s 开始
    const float SlideSeconds = 0.5f;      // 左右两侧滑入
    const float VsPopStart = 0.5f;        // VS 弹入起始时刻
    const float VsPopSeconds = 0.35f;     // VS 缩放 1.8 -> 1.0
    const float FadeInSeconds = 0.2f;
    const float FadeOutSeconds = 0.4f;    // 配合 2s 总时长收窄淡出，避免淡出吃掉整段后 30%、VS 几乎没停留
    const float SkipFadeSeconds = 0.2f;   // 点击关闭时的快速淡出
    const float VideoPrepareTimeout = 12f; // 视频准备超时则跳过视频（WebGL 首次解码缓冲慢，4s 太短会跳过开场视频）
    const int OpeningRoundGrace = 24; // WebGL 入口场景切换后补挂脚本时，回放可能已推进数回合。

    static readonly Color GoldColor = new Color(1f, 0.84f, 0.30f, 1f);
    static readonly Color RedTeamColor = new Color(0.96f, 0.30f, 0.20f, 1f);
    static readonly Color BlueTeamColor = new Color(0.30f, 0.58f, 1f, 1f);
    static readonly Vector2 LeftHidden = new Vector2(-720f, 10f);
    static readonly Vector2 LeftShown = new Vector2(130f, 10f);
    static readonly Vector2 RightHidden = new Vector2(720f, 10f);
    static readonly Vector2 RightShown = new Vector2(-130f, 10f);

    LycheeReplayPlayer player;
    Font font;

    GameObject canvasRoot;
    CanvasGroup rootGroup;
    GameObject versusLayer;
    RectTransform leftPanel;
    RectTransform rightPanel;
    RectTransform vsRect;
    CanvasGroup vsGroup;
    Image vsFlash;
    RectTransform vsFlashRect;
    RectTransform progressFill;
    Text redNameText;
    Text redSubText;
    Text blueNameText;
    Text blueSubText;
    Text infoText;
    Text loadingText;   // 标题下方"正在加载游戏资源…"提示，随 rootGroup 一起淡入
    Image redAvatarImage;
    Image blueAvatarImage;
    Text redAvatarFallback;
    Text blueAvatarFallback;

    RawImage videoImage;
    VideoPlayer videoPlayer;
    RenderTexture videoTexture;
    bool videoFinished;
    bool videoFailed;

    bool showing;
    bool skipRequested;
    bool wasPlaying;
    Sprite circleSprite;
    Sprite softCircleSprite;
    readonly List<UnityEngine.Object> generatedAssets = new List<UnityEngine.Object>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Disabled) return;
        LycheeReplayPlayer host = FindFirstObjectByType<LycheeReplayPlayer>();
        if (host == null) return;
        if (host.GetComponent<LycheeOpeningShowcase>() != null) return;
        host.gameObject.AddComponent<LycheeOpeningShowcase>();
    }

    void Awake()
    {
        player = GetComponent<LycheeReplayPlayer>();
    }

    void OnEnable()
    {
        LycheeReplayPlayer.RoundApplied += OnRoundApplied;
    }

    void OnDisable()
    {
        LycheeReplayPlayer.RoundApplied -= OnRoundApplied;
    }

    IEnumerator Start()
    {
        if (player == null) player = GetComponent<LycheeReplayPlayer>();
        if (player == null) yield break;
        // 根因修复：必须在第一次 yield 之前锁定“是否处于开局”的判定。
        // 编辑器进 Play 的前几帧 deltaTime 会被钳到 Maximum Allowed Timestep(0.33s)，
        // playOnStart + secondsPerRound=0.1 的回放在本协程恢复前就可能被推进超过第 5 回合，
        // 旧实现把 CurrentRound <= 5 延后到 yield 之后判定，竞速失败时演出被静默跳过。
        bool openingRound = player.CurrentFrame != null && player.CurrentRound <= OpeningRoundGrace;
        // 延迟一帧，确保 LycheeReplayPlayer.Awake 完成回放加载（CurrentFrame 为空时的兜底路径）
        yield return null;
        for (int i = 0; i < 120 && player != null && player.CurrentFrame == null; i++)
            yield return null;
        if (Disabled || player == null || player.CurrentFrame == null) yield break;
        if (!openingRound) openingRound = player.CurrentRound <= OpeningRoundGrace;
        if (openingRound) BeginShowcase();
    }

    void OnDestroy()
    {
        LycheeReplayPlayer.RoundApplied -= OnRoundApplied;
        if (videoPlayer != null)
        {
            videoPlayer.loopPointReached -= OnVideoFinished;
            videoPlayer.errorReceived -= OnVideoError;
        }
        if (showing && player != null && wasPlaying) player.SetPlaying(true);
        if (videoTexture != null) videoTexture.Release();
        foreach (UnityEngine.Object asset in generatedAssets)
        {
            if (asset != null) Destroy(asset);
        }
        generatedAssets.Clear();
    }

    void OnRoundApplied(LycheeReplayFrame previous, LycheeReplayFrame current)
    {
        if (Disabled || showing || !isActiveAndEnabled) return;
        if (previous == null || current == null) return;
        // SeekToRound 回到开局重看：round 从大跳回 <=2 时再次展示
        if (current.round <= 2 && previous.round > current.round) BeginShowcase();
    }

    // ---------------------------------------------------------------- 演出主流程

    /// <summary>当前是否有开局演出在展示（供自动截图等外部流程查询）。</summary>
    public static bool AnyShowing { get; private set; }

    /// <summary>立即跳过当前正在展示的开局演出（等价于用户点击关闭）。</summary>
    public static void SkipActive()
    {
        LycheeOpeningShowcase active = FindFirstObjectByType<LycheeOpeningShowcase>();
        if (active != null && active.showing) active.skipRequested = true;
    }

    void BeginShowcase()
    {
        if (Disabled || showing || player == null || !isActiveAndEnabled) return;
        try
        {
            EnsureEventSystem();
            // 触发时现场重建（与可正常显示的结算界面同模式）：
            // 预建+SetActive(false)+复活的路径在本场景下内容不可见，原因未明，弃用。
            if (canvasRoot != null)
            {
                Destroy(canvasRoot);
                canvasRoot = null;
            }
            EnsureBuilt();
            PopulateContent();
        }
        catch (Exception exception)
        {
            Debug.LogWarning("开局对阵演出构建失败：" + exception.Message, this);
            return;
        }
        if (canvasRoot == null) return;
        showing = true;
        AnyShowing = true;
        skipRequested = false;
        wasPlaying = player.IsPlaying;
        player.SetPlaying(false);
        canvasRoot.SetActive(true);
        Canvas builtCanvas = canvasRoot.GetComponent<Canvas>();
        UnityEngine.UI.Graphic[] graphics = canvasRoot.GetComponentsInChildren<UnityEngine.UI.Graphic>(true);
        Debug.Log("[LycheeOpening] 演出开始 canvasActive=" + canvasRoot.activeInHierarchy +
            " video=" + (videoPlayer != null) + " versus=" + (versusLayer != null) +
            " graphics=" + graphics.Length +
            " pixelRect=" + (builtCanvas == null ? "null" : builtCanvas.pixelRect.ToString()) +
            " scale=" + (builtCanvas == null ? 0f : builtCanvas.scaleFactor) +
            " font=" + (font == null ? "null" : font.name), this);
        StopAllCoroutines();
        StartCoroutine(RunShowcase());
    }

    IEnumerator RunShowcase()
    {
        if (rootGroup != null) rootGroup.alpha = 0f;
        SetProgress(1f);
        if (versusLayer != null) versusLayer.SetActive(false);

        // 阶段一：开场视频（存在且可播时）。播完自动进入对阵图，点击可跳过。
        if (videoPlayer != null && videoImage != null)
        {
            // VideoPlayer 与 RawImage 同处一个 GameObject：必须先激活 GameObject，Prepare() 才会推进。
            videoImage.gameObject.SetActive(true);
            // 准备期间先隐藏画面（只关 RawImage 渲染，不关 GameObject，保证 VideoPlayer 仍在 Prepare）：
            // 让红蓝背景可见，而不是盖一层未初始化的黑色 RenderTexture。
            videoImage.enabled = false;
            videoFinished = false;
            videoFailed = false;
            // 关键修复：在 GameObject 激活后才 Prepare()。TryBuildVideo 已不再 Prepare（那时 GO 即将失活）。
            videoPlayer.Prepare();
            // 准备超时必须用真实时间累加：UiDelta() 每帧钳到 0.05s，会把 12s 超时压成 ~4s 真实时间，
            // 视频（首帧解码缓冲约 5s，WebGL 更久）来不及准备就被跳过——这正是"开场视频从没播出"的根因。
            float prepareWait = 0f;
            float prepareLimit = VideoPrepareLimit();
            while (videoPlayer != null && !videoPlayer.isPrepared && !videoFailed &&
                !skipRequested && !Disabled && prepareWait < prepareLimit)
            {
                prepareWait += Time.unscaledDeltaTime;
                EnforcePause();
                if (rootGroup != null) rootGroup.alpha = Mathf.Clamp01(prepareWait / FadeInSeconds);
                yield return null;
            }
            if (videoPlayer != null && videoPlayer.isPrepared && !videoFailed && !skipRequested && !Disabled)
            {
                videoImage.enabled = true; // 准备就绪、即将播放：显示视频画面
                videoFinished = false;
                videoPlayer.Play();
                float guard = VideoPlayGuard(videoPlayer.length);
                float watched = 0f;
                while (!videoFinished && !videoFailed && !skipRequested && !Disabled && watched < guard)
                {
                    watched += Time.unscaledDeltaTime;
                    EnforcePause();
                    if (rootGroup != null)
                        rootGroup.alpha = Mathf.Min(1f, rootGroup.alpha + UiDelta() * 4f);
                    yield return null;
                }
                if (videoPlayer != null) videoPlayer.Stop();
            }
            if (videoImage != null) videoImage.gameObject.SetActive(false);
        }

        // 阶段二：对阵图（滑入 -> VS 弹入闪光 -> 停留 -> 淡出）
        if (versusLayer != null) versusLayer.SetActive(true);
        ApplyTimeline(0f);
        float elapsed = 0f;
        while (elapsed < ShowcaseSeconds && !skipRequested && !Disabled)
        {
            elapsed += UiDelta();
            EnforcePause();
            ApplyTimeline(elapsed);
            SetProgress(1f - Mathf.Clamp01(elapsed / ShowcaseSeconds));
            yield return null;
        }

        // 点击跳过：快速淡出收尾
        if ((skipRequested || Disabled) && rootGroup != null)
        {
            float from = rootGroup.alpha;
            float fade = 0f;
            while (fade < SkipFadeSeconds && rootGroup != null)
            {
                fade += UiDelta();
                rootGroup.alpha = Mathf.Lerp(from, 0f, fade / SkipFadeSeconds);
                yield return null;
            }
        }
        CloseShowcase();
    }

    static float VideoPrepareLimit()
    {
        return VideoPrepareTimeout;
    }

    static float VideoPlayGuard(double length)
    {
        return length > 0.5 ? (float)length + 2f : 20f;
    }

    void CloseShowcase()
    {
        if (videoPlayer != null && videoPlayer.isPlaying) videoPlayer.Stop();
        if (canvasRoot != null) canvasRoot.SetActive(false);
        showing = false;
        AnyShowing = false;
        if (player != null && wasPlaying) player.SetPlaying(true);
    }

    /// <summary>演出期间持续压制播放；外部若中途 SetPlaying(true)，记入 wasPlaying 并在结束时恢复。</summary>

    /// 进 Play/场景加载后的首帧 unscaledDeltaTime 可达数秒，未钳制会把整条演出时间线一帧吃光（演出闪播即关、永远不可见）。
    static float UiDelta()
    {
        return Mathf.Min(Time.unscaledDeltaTime, 0.05f);
    }

    void EnforcePause()
    {
        if (player == null) return;
        if (player.IsPlaying)
        {
            wasPlaying = true;
            player.SetPlaying(false);
        }
    }

    void OnSkipClicked()
    {
        if (!showing) return;
        skipRequested = true;
    }

    void OnVideoFinished(VideoPlayer source)
    {
        videoFinished = true;
    }

    void OnVideoError(VideoPlayer source, string message)
    {
        videoFailed = true;
    }

    // ---------------------------------------------------------------- 动画编排

    void ApplyTimeline(float elapsed)
    {
        float fadeIn = Mathf.Clamp01(elapsed / FadeInSeconds);
        float fadeOut = 1f - Mathf.Clamp01((elapsed - (ShowcaseSeconds - FadeOutSeconds)) / FadeOutSeconds);
        if (rootGroup != null) rootGroup.alpha = Mathf.Min(fadeIn, fadeOut);

        // 左右两侧相向滑入（缓出）
        float slide = EaseOutCubic(Mathf.Clamp01(elapsed / SlideSeconds));
        if (leftPanel != null) leftPanel.anchoredPosition = Vector2.LerpUnclamped(LeftHidden, LeftShown, slide);
        if (rightPanel != null) rightPanel.anchoredPosition = Vector2.LerpUnclamped(RightHidden, RightShown, slide);

        // VS 缩放 1.8 -> 1.0 弹入
        float pop = Mathf.Clamp01((elapsed - VsPopStart) / VsPopSeconds);
        if (vsGroup != null) vsGroup.alpha = Mathf.Clamp01(pop * 3f);
        if (vsRect != null)
        {
            float scale = Mathf.LerpUnclamped(1.8f, 1f, EaseOutBack(pop));
            vsRect.localScale = new Vector3(scale, scale, 1f);
        }

        // 金色闪光：随 VS 弹入绽放后消散
        float flash = Mathf.Clamp01((elapsed - VsPopStart) / 0.55f);
        if (vsFlash != null)
        {
            Color color = GoldColor;
            color.a = Mathf.Sin(flash * Mathf.PI) * 0.8f;
            vsFlash.color = color;
        }
        if (vsFlashRect != null)
        {
            float flashScale = Mathf.Lerp(0.55f, 1.65f, flash);
            vsFlashRect.localScale = new Vector3(flashScale, flashScale, 1f);
        }

        // "正在加载游戏资源…" 呼吸闪烁，读起来像在加载（整体淡入由 rootGroup 负责）
        if (loadingText != null)
        {
            Color lc = loadingText.color;
            lc.a = 0.55f + 0.4f * (0.5f + 0.5f * Mathf.Sin(elapsed * 3.2f));
            loadingText.color = lc;
        }
    }

    void SetProgress(float fraction)
    {
        if (progressFill == null) return;
        progressFill.anchorMin = new Vector2(0f, 0f);
        progressFill.anchorMax = new Vector2(Mathf.Clamp01(fraction), 1f);
        progressFill.offsetMin = Vector2.zero;
        progressFill.offsetMax = Vector2.zero;
    }

    static float EaseOutCubic(float x)
    {
        float inv = 1f - Mathf.Clamp01(x);
        return 1f - inv * inv * inv;
    }

    static float EaseOutBack(float x)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        float p = Mathf.Clamp01(x) - 1f;
        return 1f + c3 * p * p * p + c1 * p * p;
    }

    // ---------------------------------------------------------------- 数据填充

    void PopulateContent()
    {
        LycheeReplayPlayerState red = FindCampPlayer(0);
        LycheeReplayPlayerState blue = FindCampPlayer(1);

        SetTextValue(redNameText, red != null && !string.IsNullOrEmpty(red.name) ? red.name : "红队");
        SetTextValue(blueNameText, blue != null && !string.IsNullOrEmpty(blue.name) ? blue.name : "蓝队");
        SetTextValue(redSubText, "岭南出发 · 贡运 " + ResolveFruitCount(red) + " 篓");
        SetTextValue(blueSubText, "岭南出发 · 贡运 " + ResolveFruitCount(blue) + " 篓");
        SetTextValue(infoText, ResolveMatchInfo());

        ApplyAvatar(redAvatarImage, redAvatarFallback, player != null ? player.redCaptainAvatar : null);
        ApplyAvatar(blueAvatarImage, blueAvatarFallback, player != null ? player.blueCaptainAvatar : null);
    }

    LycheeReplayPlayerState FindCampPlayer(int camp)
    {
        LycheeReplayPlayerState found = FindCampPlayer(player != null ? player.StartFrameData : null, camp);
        if (found == null) found = FindCampPlayer(player != null ? player.CurrentFrame : null, camp);
        return found;
    }

    static LycheeReplayPlayerState FindCampPlayer(LycheeReplayFrame frame, int camp)
    {
        if (frame == null || frame.players == null) return null;
        foreach (LycheeReplayPlayerState state in frame.players)
        {
            if (state != null && state.camp == camp) return state;
        }
        return null;
    }

    static int ResolveFruitCount(LycheeReplayPlayerState state)
    {
        return state != null && state.goodFruit > 0 ? state.goodFruit : 100;
    }

    static void ApplyAvatar(Image avatarImage, Text fallback, Sprite sprite)
    {
        if (avatarImage != null)
        {
            avatarImage.sprite = sprite;
            avatarImage.gameObject.SetActive(sprite != null);
        }
        if (fallback != null) fallback.gameObject.SetActive(sprite == null);
    }

    string ResolveMatchInfo()
    {
        string line = ReadReplayStartLine();
        string matchId = ExtractJsonValue(line, "matchId");
        string rules = ExtractJsonValue(line, "rulesVersion");
        if (string.IsNullOrEmpty(matchId) && player != null && player.OverFrame != null)
            matchId = player.OverFrame.matchId;
        string left = string.IsNullOrEmpty(matchId) ? "对局编号待定" : "对局 " + matchId;
        string right = string.IsNullOrEmpty(rules) ? "规则版本待定" : "规则 v" + rules;
        return left + " · " + right;
    }

    string ReadReplayStartLine()
    {
        try
        {
            string path = System.IO.Path.Combine(Application.streamingAssetsPath, "replay.txt");
            if (System.IO.File.Exists(path))
            {
                using (System.IO.StreamReader reader = new System.IO.StreamReader(path))
                {
                    for (int i = 0; i < 5; i++)
                    {
                        string line = reader.ReadLine();
                        if (line == null) break;
                        if (line.Contains("\"matchId\"")) return line;
                    }
                }
            }
        }
        catch (Exception)
        {
            // 读取失败时继续走内置样例兜底
        }
        try
        {
            if (player != null && player.replayFile != null && !string.IsNullOrEmpty(player.replayFile.text))
            {
                string text = player.replayFile.text;
                int index = text.IndexOf("\"type\":\"start\"", StringComparison.Ordinal);
                if (index >= 0)
                {
                    int lineStart = text.LastIndexOf('\n', index) + 1;
                    int lineEnd = text.IndexOf('\n', index);
                    return lineEnd < 0 ? text.Substring(lineStart) : text.Substring(lineStart, lineEnd - lineStart);
                }
            }
        }
        catch (Exception)
        {
            // 兜底失败则返回 null，由调用方降级显示
        }
        return null;
    }

    static string ExtractJsonValue(string json, string key)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
        string token = "\"" + key + "\":\"";
        int start = json.IndexOf(token, StringComparison.Ordinal);
        if (start < 0) return null;
        start += token.Length;
        int end = json.IndexOf('"', start);
        if (end <= start) return null;
        return json.Substring(start, end - start);
    }

    static void SetTextValue(Text text, string value)
    {
        if (text != null) text.text = value;
    }

    // ---------------------------------------------------------------- UI 构建

    static void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null) return;
        GameObject eventSystem = new GameObject("LycheeOpeningEventSystem");
        eventSystem.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
        eventSystem.AddComponent<InputSystemUIInputModule>();
#else
        eventSystem.AddComponent<StandaloneInputModule>();
#endif
    }

    void EnsureBuilt()
    {
        if (canvasRoot != null) return;
        font = LoadChineseFont();
        circleSprite = CreateCircleSprite(128, false);
        softCircleSprite = CreateCircleSprite(128, true);

        canvasRoot = new GameObject(
            "LycheeOpeningCanvas",
            typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasRoot.transform.SetParent(transform, false);
        Canvas canvas = canvasRoot.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 230;
        CanvasScaler scaler = canvasRoot.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        RectTransform root = MakeStretch("OpeningRoot", canvasRoot.transform);
        rootGroup = root.gameObject.AddComponent<CanvasGroup>();
        rootGroup.alpha = 0f;
        rootGroup.blocksRaycasts = true;

        BuildBackground(root);
        TryBuildVideo(root);
        BuildVersusLayer(root);
        BuildProgressBar(root);
        BuildClickCatcher(root);

        canvasRoot.SetActive(false);
    }

    void BuildBackground(Transform parent)
    {
        RectTransform bgRoot = MakeStretch("OpeningBg", parent);
        Sprite themed = LoadThemeSprite("opening_bg.png");
        if (themed != null)
        {
            Image themedImage = bgRoot.gameObject.AddComponent<Image>();
            themedImage.sprite = themed;
            themedImage.color = Color.white;
            themedImage.raycastTarget = false;
            return;
        }

        // 程序化兜底：深色底 + 朱红/青蓝对角斜分割（两个旋转的超尺寸 Image）
        Image baseImage = bgRoot.gameObject.AddComponent<Image>();
        baseImage.color = new Color(0.055f, 0.045f, 0.085f, 1f);
        baseImage.raycastTarget = false;

        Sprite blueGradient = CreateGradientSprite(
            new Color(0.05f, 0.16f, 0.34f, 0.97f),
            new Color(0.13f, 0.46f, 0.76f, 0.97f));
        Sprite redGradient = CreateGradientSprite(
            new Color(0.42f, 0.07f, 0.06f, 0.97f),
            new Color(0.86f, 0.28f, 0.16f, 0.97f));

        RectTransform blueRect = MakeRect("BgBlueSlash", bgRoot, new Vector2(1200f, 0f), new Vector2(2600f, 3200f));
        blueRect.localRotation = Quaternion.Euler(0f, 0f, 8f);
        if (blueGradient != null)
        {
            Image blueImage = blueRect.gameObject.AddComponent<Image>();
            blueImage.sprite = blueGradient;
            blueImage.raycastTarget = false;
        }

        RectTransform redRect = MakeRect("BgRedSlash", bgRoot, new Vector2(-1200f, 0f), new Vector2(2600f, 3200f));
        redRect.localRotation = Quaternion.Euler(0f, 0f, 8f);
        if (redGradient != null)
        {
            Image redImage = redRect.gameObject.AddComponent<Image>();
            redImage.sprite = redGradient;
            redImage.raycastTarget = false;
        }
    }

    void TryBuildVideo(Transform parent)
    {
        string videoUrl;
#if UNITY_WEBGL && !UNITY_EDITOR
        // WebGL：StreamingAssets 是 URL，无法 File.Exists 探测；直接设 URL，
        // 缺失/解码失败由 errorReceived 与 Prepare 超时兜底（跳过视频走对阵图）。
        videoUrl = Application.streamingAssetsPath + "/Video/opening.mp4";
#else
        try
        {
            string path = System.IO.Path.Combine(Application.streamingAssetsPath, "Video", "opening.mp4");
            if (!System.IO.File.Exists(path)) return;
            videoUrl = "file://" + path;
        }
        catch (Exception)
        {
            return;
        }
#endif

        videoTexture = new RenderTexture(1280, 720, 0) { name = "LycheeOpeningVideoRT" };
        generatedAssets.Add(videoTexture);

        RectTransform rect = MakeStretch("OpeningVideo", parent);
        videoImage = rect.gameObject.AddComponent<RawImage>();
        videoImage.texture = videoTexture;
        videoImage.color = Color.white;
        videoImage.raycastTarget = false;

        videoPlayer = rect.gameObject.AddComponent<VideoPlayer>();
        videoPlayer.playOnAwake = false;
        videoPlayer.isLooping = false;
        videoPlayer.renderMode = VideoRenderMode.RenderTexture;
        videoPlayer.targetTexture = videoTexture;
        videoPlayer.audioOutputMode = VideoAudioOutputMode.None;
        videoPlayer.url = videoUrl;
        videoPlayer.loopPointReached += OnVideoFinished;
        videoPlayer.errorReceived += OnVideoError;
        // 不在此处 Prepare()：VideoPlayer 所在 GameObject 紧接着被 SetActive(false)，
        // 非激活的 GameObject 上 Prepare() 永远完不成（isPrepared 一直 false）→ RunShowcase 等满
        // 12s 超时后跳过视频——这正是“开场视频从没播出 + 卡在对阵图很久(~16s)”的真正根因。
        // 改为在 RunShowcase 把视频层激活后再 Prepare()。
        rect.gameObject.SetActive(false);
    }

    void BuildVersusLayer(Transform parent)
    {
        RectTransform layer = MakeStretch("VersusLayer", parent);
        versusLayer = layer.gameObject;

        leftPanel = MakeRectAnchored(
            "RedSide", layer,
            new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
            LeftHidden, new Vector2(460f, 560f));
        BuildTeamPanel(leftPanel, true);

        rightPanel = MakeRectAnchored(
            "BlueSide", layer,
            new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
            RightHidden, new Vector2(460f, 560f));
        BuildTeamPanel(rightPanel, false);

        // 上方主标题
        Text title = MakeText(
            layer, "OpeningTitle", "一骑红尘 · 荔枝争运战",
            40, new Color(1f, 0.92f, 0.78f, 1f), FontStyle.Bold,
            new Vector2(0f, 238f), new Vector2(1000f, 72f));
        if (title != null)
        {
            Shadow titleShadow = title.gameObject.AddComponent<Shadow>();
            titleShadow.effectColor = new Color(0f, 0f, 0f, 0.75f);
            titleShadow.effectDistance = new Vector2(2f, -3f);
        }

        // 标题下方：明确告知"正在加载游戏资源"（VS 页即开场加载过渡屏）。ApplyTimeline 里做呼吸闪烁。
        loadingText = MakeText(
            layer, "OpeningLoadingHint", "正在加载游戏资源…",
            24, new Color(0.97f, 0.92f, 0.76f, 0.95f), FontStyle.Bold,
            new Vector2(0f, 188f), new Vector2(760f, 38f));
        if (loadingText != null)
        {
            Shadow loadShadow = loadingText.gameObject.AddComponent<Shadow>();
            loadShadow.effectColor = new Color(0f, 0f, 0f, 0.7f);
            loadShadow.effectDistance = new Vector2(1.5f, -2f);
        }

        // 金色闪光（VS 背后）
        vsFlashRect = MakeRect("VsFlash", layer, new Vector2(0f, 16f), new Vector2(540f, 540f));
        vsFlash = vsFlashRect.gameObject.AddComponent<Image>();
        vsFlash.sprite = softCircleSprite;
        vsFlash.color = new Color(GoldColor.r, GoldColor.g, GoldColor.b, 0f);
        vsFlash.raycastTarget = false;

        // 中央巨大 VS
        Text vs = MakeText(
            layer, "VsText", "VS",
            120, GoldColor, FontStyle.Bold,
            new Vector2(0f, 16f), new Vector2(420f, 220f));
        if (vs != null)
        {
            vsRect = vs.rectTransform;
            vsGroup = vs.gameObject.AddComponent<CanvasGroup>();
            vsGroup.alpha = 0f;
            Shadow vsShadow = vs.gameObject.AddComponent<Shadow>();
            vsShadow.effectColor = new Color(0f, 0f, 0f, 0.6f);
            vsShadow.effectDistance = new Vector2(5f, -6f);
            Outline vsOutline = vs.gameObject.AddComponent<Outline>();
            vsOutline.effectColor = new Color(0.45f, 0.26f, 0.04f, 0.95f);
            vsOutline.effectDistance = new Vector2(3f, -3f);
        }

        // 下方对局信息小字
        infoText = MakeText(
            layer, "MatchInfo", "",
            20, new Color(0.86f, 0.84f, 0.80f, 0.85f), FontStyle.Normal,
            new Vector2(0f, -188f), new Vector2(960f, 34f));
    }

    void BuildTeamPanel(RectTransform panel, bool red)
    {
        if (panel == null) return;
        Color teamColor = red ? RedTeamColor : BlueTeamColor;

        // 队长头像：圆环 + 圆形遮罩裁切
        RectTransform ring = MakeRect("AvatarRing", panel, new Vector2(0f, 92f), new Vector2(332f, 332f));
        Image ringImage = ring.gameObject.AddComponent<Image>();
        ringImage.sprite = circleSprite;
        ringImage.color = teamColor;
        ringImage.raycastTarget = false;

        RectTransform plate = MakeRect("AvatarPlate", ring, Vector2.zero, new Vector2(302f, 302f));
        Image plateImage = plate.gameObject.AddComponent<Image>();
        plateImage.sprite = circleSprite;
        plateImage.color = new Color(0.11f, 0.09f, 0.13f, 1f);
        plateImage.raycastTarget = false;
        Mask mask = plate.gameObject.AddComponent<Mask>();
        mask.showMaskGraphic = true;

        RectTransform avatarRect = MakeRect("Avatar", plate, Vector2.zero, new Vector2(300f, 300f));
        Image avatarImage = avatarRect.gameObject.AddComponent<Image>();
        avatarImage.color = Color.white;
        avatarImage.raycastTarget = false;

        Text fallback = MakeText(
            plate, "AvatarFallback", red ? "红" : "蓝",
            110, Color.Lerp(teamColor, Color.white, 0.35f), FontStyle.Bold,
            new Vector2(0f, 6f), new Vector2(300f, 300f));

        Text name = MakeText(
            panel, "TeamName", red ? "红队" : "蓝队",
            44, Color.white, FontStyle.Bold,
            new Vector2(0f, -122f), new Vector2(540f, 64f));
        if (name != null)
        {
            Shadow nameShadow = name.gameObject.AddComponent<Shadow>();
            nameShadow.effectColor = new Color(0f, 0f, 0f, 0.7f);
            nameShadow.effectDistance = new Vector2(2f, -2f);
        }

        Text sub = MakeText(
            panel, "TeamSub", "岭南出发 · 贡运 100 篓",
            24, new Color(1f, 0.88f, 0.70f, 0.92f), FontStyle.Normal,
            new Vector2(0f, -176f), new Vector2(540f, 40f));

        if (red)
        {
            redAvatarImage = avatarImage;
            redAvatarFallback = fallback;
            redNameText = name;
            redSubText = sub;
        }
        else
        {
            blueAvatarImage = avatarImage;
            blueAvatarFallback = fallback;
            blueNameText = name;
            blueSubText = sub;
        }
    }

    void BuildProgressBar(Transform parent)
    {
        RectTransform track = MakeRectAnchored(
            "ProgressTrack", parent,
            new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
            new Vector2(0f, 18f), new Vector2(-200f, 6f));
        Image trackImage = track.gameObject.AddComponent<Image>();
        trackImage.color = new Color(1f, 1f, 1f, 0.12f);
        trackImage.raycastTarget = false;

        progressFill = MakeRectAnchored(
            "ProgressFill", track,
            new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0f, 0.5f),
            Vector2.zero, Vector2.zero);
        Image fillImage = progressFill.gameObject.AddComponent<Image>();
        fillImage.color = new Color(GoldColor.r, GoldColor.g, GoldColor.b, 0.9f);
        fillImage.raycastTarget = false;
        SetProgress(1f);
    }

    void BuildClickCatcher(Transform parent)
    {
        RectTransform catcher = MakeStretch("ClickCatcher", parent);
        Image catcherImage = catcher.gameObject.AddComponent<Image>();
        catcherImage.color = new Color(0f, 0f, 0f, 0f); // 全屏透明，仅用于拦截点击
        catcherImage.raycastTarget = true;
        Button button = catcher.gameObject.AddComponent<Button>();
        button.transition = Selectable.Transition.None;
        button.onClick.AddListener(OnSkipClicked);
    }

    // ---------------------------------------------------------------- 通用助手

    static RectTransform MakeStretch(string objectName, Transform parent)
    {
        GameObject go = new GameObject(objectName, typeof(RectTransform));
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        return rect;
    }

    static RectTransform MakeRect(string objectName, Transform parent, Vector2 anchoredPosition, Vector2 size)
    {
        return MakeRectAnchored(
            objectName, parent,
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            anchoredPosition, size);
    }

    static RectTransform MakeRectAnchored(
        string objectName,
        Transform parent,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 pivot,
        Vector2 anchoredPosition,
        Vector2 size)
    {
        GameObject go = new GameObject(objectName, typeof(RectTransform));
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = pivot;
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;
        return rect;
    }

    Text MakeText(
        Transform parent,
        string objectName,
        string value,
        int fontSize,
        Color color,
        FontStyle style,
        Vector2 anchoredPosition,
        Vector2 size)
    {
        RectTransform rect = MakeRect(objectName, parent, anchoredPosition, size);
        Text text = rect.gameObject.AddComponent<Text>();
        if (font != null) text.font = font;
        text.text = value;
        text.fontSize = fontSize;
        text.color = color;
        text.fontStyle = style;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        return text;
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
                new[] { "PingFang SC", "Heiti SC", "Arial Unicode MS" }, 48);
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

    /// <summary>从 StreamingAssets/UITheme/ 按需加载主题贴图为 Sprite，缺失或异常返回 null 走程序化兜底。</summary>
    Sprite LoadThemeSprite(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        try
        {
            byte[] bytes = LycheeThemeCache.LoadBytes(fileName);
            if (bytes == null || bytes.Length == 0) return null;
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = "LycheeOpening_" + fileName,
                wrapMode = TextureWrapMode.Clamp
            };
            if (!texture.LoadImage(bytes))
            {
                Destroy(texture);
                return null;
            }
            generatedAssets.Add(texture);
            Sprite sprite = Sprite.Create(
                texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f),
                100f, 0, SpriteMeshType.FullRect);
            sprite.name = "LycheeOpeningSprite_" + fileName;
            generatedAssets.Add(sprite);
            return sprite;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>程序化圆形 Sprite：soft=true 为柔光圆（闪光用），false 为带抗锯齿硬边圆（头像环/遮罩用）。</summary>
    Sprite CreateCircleSprite(int size, bool soft)
    {
        size = Mathf.Max(16, size);
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = soft ? "LycheeOpeningSoftCircle" : "LycheeOpeningCircle",
            wrapMode = TextureWrapMode.Clamp
        };
        float half = (size - 1) * 0.5f;
        Color32[] pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x - half) / half;
                float dy = (y - half) / half;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha;
                if (soft)
                {
                    alpha = Mathf.Clamp01(1f - distance);
                    alpha = alpha * alpha * (3f - 2f * alpha);
                    alpha *= alpha; // 更柔的光晕
                }
                else
                {
                    alpha = Mathf.Clamp01((1f - distance) * half * 0.5f); // 边缘约 2px 抗锯齿
                }
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
            }
        }
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        generatedAssets.Add(texture);
        Sprite sprite = Sprite.Create(
            texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f),
            100f, 0, SpriteMeshType.FullRect);
        sprite.name = texture.name + "Sprite";
        generatedAssets.Add(sprite);
        return sprite;
    }

    /// <summary>程序化纵向渐变 Sprite（底色 -&gt; 顶色），用于斜分割底。</summary>
    Sprite CreateGradientSprite(Color bottom, Color top)
    {
        const int width = 4;
        const int height = 256;
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            name = "LycheeOpeningGradient",
            wrapMode = TextureWrapMode.Clamp
        };
        Color32[] pixels = new Color32[width * height];
        for (int y = 0; y < height; y++)
        {
            Color color = Color.Lerp(bottom, top, y / (float)(height - 1));
            Color32 packed = color;
            for (int x = 0; x < width; x++)
                pixels[y * width + x] = packed;
        }
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        generatedAssets.Add(texture);
        Sprite sprite = Sprite.Create(
            texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f),
            100f, 0, SpriteMeshType.FullRect);
        sprite.name = "LycheeOpeningGradientSprite";
        generatedAssets.Add(sprite);
        return sprite;
    }
}
