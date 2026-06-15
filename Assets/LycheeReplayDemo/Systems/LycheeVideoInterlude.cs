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
///   WINDOW_CONTEST_START → contest.mp4（对抗大战）
///   RUSH_PHASE_START     → rush.mp4（冲刺阶段）
///   DELIVER_SUCCESS      → deliver.mp4（交付成功）
/// 用户按约定丢视频进去即自动接入；没丢的事件正常播放不中断。
///
/// 关键设计：先静默 Prepare 视频（回放继续跑），确认能播了才暂停+淡黑+播放——缺失/失败的视频
/// 经 errorReceived/超时被标记 knownMissing，永不再试、也绝不中断回放。点击可跳过；两次插播间有冷却。
/// 自举挂到 LycheeReplayPlayer 上，订阅 ReplayEventRaised。WebGL 安全（VideoPlayer 走 URL，无 File IO）。
/// </summary>
public sealed class LycheeVideoInterlude : MonoBehaviour
{
    const float PrepareTimeout = 12f;     // 视频准备超时（WebGL 首次解码慢，对齐开场演出的 12s）
    const float CooldownSeconds = 20f;    // 两次插播之间的最小真实间隔，避免连环打断
    const float FadeSeconds = 0.45f;
    const int CanvasSortingOrder = 235;   // 压在常规 HUD 之上、开场演出(230)之上、结算(220)之下不冲突

    // 事件→插播视频。事件名按实际回放数据对齐：冲刺是 RUSH_START（非 RUSH_PHASE_START）。
    static readonly Dictionary<string, string> EventVideo = new Dictionary<string, string>
    {
        // 对抗大战。注意：当前 reply.txt 不含任何对抗事件（contests 恒为空，无 WINDOW_CONTEST_START），
        // 因此 contest.mp4 在这份回放里不会触发；需换用含对抗的回放，或把键改成本回放实际存在的事件名。
        { "WINDOW_CONTEST_START", "contest.mp4" },
        { "RUSH_START", "rush.mp4" },              // 冲刺阶段开始（本回放第 ~450 回合触发）
        { "DELIVER_SUCCESS", "deliver.mp4" },      // 荔枝交付成功（本回放第 ~490 / 503 回合触发）
    };

    LycheeReplayPlayer player;
    readonly HashSet<string> knownMissing = new HashSet<string>(); // 确认缺失/坏的文件名，不再尝试
    bool busy;            // 正在准备或播放插播
    float lastEndTime = -999f;
    bool skipRequested;

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
        if (videoTexture != null) videoTexture.Release();
    }

    void OnReplayEvent(LycheeReplayMessage message, int round)
    {
        if (player == null || busy) return;
        if (message == null || string.IsNullOrEmpty(message.type)) return;
        if (Time.time - lastEndTime < CooldownSeconds) return;
        if (!EventVideo.TryGetValue(message.type, out string fileName)) return;
        if (knownMissing.Contains(fileName)) return;
        StartCoroutine(RunInterlude(fileName));
    }

    IEnumerator RunInterlude(string fileName)
    {
        busy = true;
        EnsureCanvas();

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

        // 2. 确认能播：暂停回放、淡黑、播视频
        bool wasPlaying = player.IsPlaying;
        player.SetPlaying(false);
        skipRequested = false;
        canvasRoot.SetActive(true);
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
        if (wasPlaying) player.SetPlaying(true);
        lastEndTime = Time.time;
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
