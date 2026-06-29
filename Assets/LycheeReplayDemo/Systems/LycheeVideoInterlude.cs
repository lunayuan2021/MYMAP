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
/// 中段视频插播：回放进行到关键事件时，插播一段短视频，播放期间暂停回放、播完恢复，让整体更有观赏性。
/// 触发事件→视频文件名约定（放 StreamingAssets/Video/ 下，缺失则该事件不插播、零打扰）：
///   WINDOW_CONTEST_START → contest.mp4/resource_contest.mp4   RUSH_START → rush.mp4   DELIVER_SUCCESS → deliver.mp4
///   OBSTACLE_CLEAR → clear_obstacle.mp4  GUARD_BREAK → break_guard.mp4
///   VERIFY_GATE_START → gate_verification.mp4  WEATHER_START → weather_start.mp4
/// 用户按约定丢视频进去即自动接入；没丢的事件正常播放不中断。两次插播间有 20s 冷却，不会连环打断。
///
/// 关键设计：先静默 Prepare 视频（回放继续跑），确认能播了才暂停+淡黑+播放——缺失/失败的视频
/// 经 errorReceived/超时被标记 knownMissing，永不再试、也绝不中断回放。点击可跳过；两次插播间有冷却。
/// 自举挂到 LycheeReplayPlayer 上，订阅 ReplayEventRaised。WebGL 安全（VideoPlayer 走 URL，无 File IO）。
/// </summary>
public sealed class LycheeVideoInterlude : MonoBehaviour
{
    const float PrepareTimeout = 12f;     // 视频准备超时（WebGL 首次解码慢，对齐开场演出的 12s）
    const float RepeatWindowSeconds = 18f;   // 同一段短片在此窗口内不重复播（去掉重复的那一个）；不同短片不受限、照播。
    const int SquadDispatchMaxPlays = 2;     // 小分队出发短片每场只播前 N 次（当机制介绍，之后不再打扰）。
    const float FadeSeconds = 0.45f;
    const int ClearObstacleCardConflictGraceRounds = 3;
    // 插播短片要在天气氛围层(125)之上，但在运行时 HUD(130/140)之下；
    // 否则左侧事件流、右侧事件卡和中央窗口争夺会被视频盖住。
    const int CanvasSortingOrder = 128;

    public static bool IsPresentationVisible { get; private set; }
    public static bool IsPresentationActive { get; private set; }

    // 事件→插播视频。事件名按实际回放数据对齐：冲刺是 RUSH_START（非 RUSH_PHASE_START）。
    static readonly Dictionary<string, string> EventVideo = new Dictionary<string, string>
    {
        { "RUSH_START", "rush.mp4" },              // 冲刺阶段开始（本回放第 ~450 回合触发）
        { "DELIVER_SUCCESS", "deliver.mp4" },      // 荔枝交付成功（本回放第 ~490 / 503 回合触发）
        // 关键事件短片（StreamingAssets/Video/，20s 冷却 + knownMissing 兜底；缺文件则该事件零打扰）
        { "OBSTACLE_CLEAR", "clear_obstacle.mp4" },       // 清障
        { "GUARD_BREAK", "break_guard.mp4" },             // 破卡
        { "VERIFY_GATE_START", "gate_verification.mp4" }, // 宫门验核
        { "WEATHER_START", "weather_start.mp4" },         // 天气开始
        { "SQUAD_DISPATCH", "squad_dispatch.mp4" },       // 小分队出发（每场只播前 SquadDispatchMaxPlays 次）
    };

    LycheeReplayPlayer player;
    readonly HashSet<string> knownMissing = new HashSet<string>(); // 确认缺失/坏的文件名，不再尝试
    readonly Dictionary<string, float> lastPlayByClip = new Dictionary<string, float>(); // 每段短片上次播放的真实时刻 → 同片去重
    int squadDispatchPlays;             // 本场已播过的小分队出发短片次数（封顶 SquadDispatchMaxPlays）
    int lastSeenRound = int.MinValue;   // 上次事件回合，检测大幅倒带（重播/循环）以复位本场计数
    bool busy;            // 正在准备或播放插播
    bool skipRequested;
    bool cardWindowActive;
    int suppressClearObstacleUntilRound = -1;

    GameObject canvasRoot;
    CanvasGroup group;
    RawImage videoImage;
    RenderTexture videoTexture;
    VideoPlayer videoPlayer;
    bool videoFinished;
    bool videoErrored;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var p = FindAnyObjectByType<LycheeReplayPlayer>();
        if (p == null) return;
        if (p.GetComponent<LycheeVideoInterlude>() == null)
            p.gameObject.AddComponent<LycheeVideoInterlude>();
    }

    void OnEnable()
    {
        player = GetComponent<LycheeReplayPlayer>();
        LycheeReplayPlayer.ReplayEventRaised += OnReplayEvent;
    }

    void OnDisable()
    {
        LycheeReplayPlayer.ReplayEventRaised -= OnReplayEvent;
        IsPresentationVisible = false;
        IsPresentationActive = false;
        if (videoTexture != null) videoTexture.Release();
    }

    void OnReplayEvent(LycheeReplayMessage message, int round)
    {
        if (message == null || string.IsNullOrEmpty(message.type)) return;
        if (player == null) return;
        if (player.IsApplyingForcedFrame) return;

        // 大幅倒带（重播 / 循环回开头）：复位本场计数，新一轮重新计「前N次」和同片去重。
        if (round < lastSeenRound - 5) { squadDispatchPlays = 0; lastPlayByClip.Clear(); }
        lastSeenRound = round;

        TrackCardWindowState(message, round);

        if (busy) return;                                          // 一次只播一个；播放期间回放暂停，不会有新事件挤进来
        if (ShouldSuppressForContest(message, round)) return;      // 对抗出牌窗口期间一律不插播，绝不打断逐拍出牌
        string fileName = ResolveInterludeVideo(message);
        if (string.IsNullOrEmpty(fileName)) return;
        if (knownMissing.Contains(fileName)) return;
        // 同一段短片在窗口内不重复播（去掉重复的那一个）；不同短片不受影响、照播。
        if (lastPlayByClip.TryGetValue(fileName, out float last) && Time.time - last < RepeatWindowSeconds) return;
        // 小分队出发短片每场只播前 N 次。
        if (message.type == "SQUAD_DISPATCH" && squadDispatchPlays >= SquadDispatchMaxPlays) return;
        StartCoroutine(RunInterlude(fileName, message.type));
    }

    void TrackCardWindowState(LycheeReplayMessage message, int round)
    {
        if (message.type == "WINDOW_CONTEST_START")
        {
            cardWindowActive = true;
            ExtendClearObstacleSuppression(round + ClearObstacleCardConflictGraceRounds);
            return;
        }

        if (message.type == "WINDOW_CARD_REVEAL")
        {
            ExtendClearObstacleSuppression(round + ClearObstacleCardConflictGraceRounds);
            return;
        }

        if (message.type == "WINDOW_CONTEST_END" ||
            message.type == "WINDOW_CONTEST_DRAW" ||
            message.type == "WINDOW_CARD_WIN" ||
            message.type == "WINDOW_CARD_DRAW")
        {
            cardWindowActive = false;
            ExtendClearObstacleSuppression(round + ClearObstacleCardConflictGraceRounds);
        }
    }

    void ExtendClearObstacleSuppression(int untilRound)
    {
        if (untilRound > suppressClearObstacleUntilRound)
            suppressClearObstacleUntilRound = untilRound;
    }

    // 对抗保护：对抗出牌窗口期（START~END）+ 数回合缓冲内，除"对抗自己的开场短片"外一律不插播，
    // 保证逐拍出牌/窗口争夺完整不被任何短片打断（用户要求：不能和对抗冲突）。
    bool ShouldSuppressForContest(LycheeReplayMessage message, int round)
    {
        if (message.type == "WINDOW_CONTEST_START") return false; // 对抗开场短片本身放行
        return cardWindowActive || round <= suppressClearObstacleUntilRound;
    }

    static string ResolveInterludeVideo(LycheeReplayMessage message)
    {
        if (message == null || string.IsNullOrEmpty(message.type)) return null;
        if (message.type == "WINDOW_CONTEST_START")
        {
            LycheeReplayPayload pl = message.payload;
            string contestType = pl == null ? null : pl.contestType;
            if (string.Equals(contestType, "RESOURCE", System.StringComparison.OrdinalIgnoreCase))
                return "resource_contest.mp4";

            // 普通三拍对抗直接进逐拍出牌；只有五拍终局/宫门争夺播通用对抗短片。
            bool keyContest = pl != null &&
                (pl.totalRounds >= 5 || string.Equals(contestType, "GATE", System.StringComparison.OrdinalIgnoreCase));
            return keyContest ? "contest.mp4" : null;
        }
        return EventVideo.TryGetValue(message.type, out string fileName) ? fileName : null;
    }

    IEnumerator RunInterlude(string fileName, string eventType)
    {
        busy = true;
        EnsureCanvas();
        IsPresentationActive = true;

        // 关键修复：VideoPlayer 挂在 canvasRoot 上，Prepare() 只有在 GameObject 处于激活态时才会推进。
        // 旧实现先 Prepare() 后才 SetActive(true)，于是 isPrepared 永远为 false → 等满 PrepareTimeout 后被
        // 判为缺失写入 knownMissing → 该视频此后永不再播。这正是“中段插播(contest/rush/deliver)从没出现”的根因。
        // 准备阶段让 canvasRoot 处于「激活但全透明、不拦截点击」，回放无感继续，确认能播再淡黑展示。
        canvasRoot.SetActive(true);
        if (group != null) { group.alpha = 0f; group.blocksRaycasts = false; }

        // 1. 静默准备视频（回放继续跑，不暂停）——缺失/坏文件在这一步被识破，绝不打断回放
        string url = Application.streamingAssetsPath + "/Video/" + fileName;
        videoFinished = false;
        videoErrored = false;
        videoPlayer.url = url;
        videoPlayer.Prepare();
        float wait = 0f;
        while (!videoPlayer.isPrepared && !videoErrored && wait < PrepareTimeout)
        {
            wait += Time.unscaledDeltaTime;
            yield return null;
        }
        if (!videoPlayer.isPrepared || videoErrored)
        {
            // 未提供该视频或解码失败：记为缺失永不再试，回放从未被打断
            knownMissing.Add(fileName);
            CleanupVideo();
            busy = false;
            yield break;
        }

        // 2. 确认能播：先登记"已播"（同片去重 + 小分队封顶都按真正播出计），再暂停回放、淡黑、播视频
        lastPlayByClip[fileName] = Time.time;
        if (eventType == "SQUAD_DISPATCH") squadDispatchPlays++;
        bool wasPlaying = player.IsPlaying;
        player.SetPlaying(false);
        skipRequested = false;
        canvasRoot.SetActive(true);
        IsPresentationVisible = true;
        if (group != null) group.blocksRaycasts = true; // 开始展示：恢复点击跳过
        videoImage.gameObject.SetActive(true);
        yield return Fade(0f, 1f, FadeSeconds);

        videoPlayer.Play();
        float guard = videoPlayer.length > 0.5 ? (float)videoPlayer.length + 2f : 30f;
        float watched = 0f;
        while (!videoFinished && !videoErrored && !skipRequested && watched < guard)
        {
            watched += Time.unscaledDeltaTime;
            EnforcePause(wasPlaying);
            yield return null;
        }
        videoPlayer.Stop();

        // 3. 淡出、恢复回放
        yield return Fade(1f, 0f, FadeSeconds);
        videoImage.gameObject.SetActive(false);
        canvasRoot.SetActive(false);
        IsPresentationVisible = false;
        IsPresentationActive = false;
        if (wasPlaying) player.SetPlaying(true);
        busy = false;
    }

    /// <summary>插播期间若外部把回放又打开了，压回暂停（与开场演出同款守护）。跳过靠 UI Button（本工程用新输入系统，旧 Input 类会抛异常）。</summary>
    void EnforcePause(bool wasPlaying)
    {
        if (player != null && player.IsPlaying) player.SetPlaying(false);
    }

    IEnumerator Fade(float from, float to, float seconds)
    {
        float t = 0f;
        while (t < seconds)
        {
            t += Time.unscaledDeltaTime;
            if (group != null) group.alpha = Mathf.Lerp(from, to, Mathf.Clamp01(t / seconds));
            yield return null;
        }
        if (group != null) group.alpha = to;
    }

    static void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null) return;
        GameObject es = new GameObject("LycheeInterludeEventSystem");
        es.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
        es.AddComponent<InputSystemUIInputModule>();
#else
        es.AddComponent<StandaloneInputModule>();
#endif
    }

    void EnsureCanvas()
    {
        if (canvasRoot != null) return;
        EnsureEventSystem();
        canvasRoot = new GameObject("LycheeVideoInterludeCanvas");
        canvasRoot.transform.SetParent(transform, false);
        Canvas canvas = canvasRoot.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = CanvasSortingOrder;
        var scaler = canvasRoot.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        canvasRoot.AddComponent<GraphicRaycaster>();
        group = canvasRoot.AddComponent<CanvasGroup>();
        group.alpha = 0f;

        // 全屏黑底（视频两侧留黑边时不露出场景）+ 点击跳过（用 UI Button，本工程用新输入系统）
        var bg = NewStretch("Black", canvasRoot.transform);
        Image bgImage = bg.gameObject.AddComponent<Image>();
        bgImage.color = Color.black;
        bgImage.raycastTarget = true;
        Button skipButton = bg.gameObject.AddComponent<Button>();
        skipButton.transition = Selectable.Transition.None;
        skipButton.onClick.AddListener(() => skipRequested = true);

        // 视频画面层
        videoTexture = new RenderTexture(1280, 720, 0) { name = "LycheeInterludeVideoRT" };
        var rect = NewStretch("Video", canvasRoot.transform);
        videoImage = rect.gameObject.AddComponent<RawImage>();
        videoImage.texture = videoTexture;
        videoImage.color = Color.white;
        videoImage.raycastTarget = false;

        videoPlayer = canvasRoot.AddComponent<VideoPlayer>();
        videoPlayer.playOnAwake = false;
        videoPlayer.isLooping = false;
        videoPlayer.renderMode = VideoRenderMode.RenderTexture;
        videoPlayer.targetTexture = videoTexture;
        videoPlayer.audioOutputMode = VideoAudioOutputMode.None;
        videoPlayer.loopPointReached += _ => videoFinished = true;
        videoPlayer.errorReceived += (_, msg) => { videoErrored = true; Debug.LogWarning("[LycheeVideoInterlude] 视频加载失败: " + msg); };

        canvasRoot.SetActive(false);
    }

    void CleanupVideo()
    {
        if (videoPlayer != null && videoPlayer.isPlaying) videoPlayer.Stop();
        if (canvasRoot != null) canvasRoot.SetActive(false);
        if (group != null) group.alpha = 0f;
        IsPresentationVisible = false;
        IsPresentationActive = false;
    }

    static RectTransform NewStretch(string name, Transform parent)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        return rt;
    }
}
