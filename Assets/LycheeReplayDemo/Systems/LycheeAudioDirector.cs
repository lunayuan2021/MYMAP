using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// 音频导演：统一管理回放的 BGM（双源交叉淡入淡出）、事件音效与环境循环音。
/// 所有素材运行时从 StreamingAssets/Audio/ 加载（BGM/ 与 SFX/ 子目录），统一为 .ogg：
/// WebGL 运行时无法解码 MP3（UnityWebRequestMultimedia 下 MP3 会"加载成功"却 0 采样=静音），
/// 而 OGG/WAV 才能在浏览器里实时解码——素材全部转成 OGG 后只请求 .ogg，既能播放又不再产生
/// .wav/.mp3 探测的 404。文件缺失一律静默跳过，绝不影响回放本身。
/// 通过 RuntimeInitializeOnLoadMethod 自举挂到 LycheeReplayPlayer 所在 GameObject。
/// </summary>
[DisallowMultipleComponent]
public sealed class LycheeAudioDirector : MonoBehaviour
{
    /// <summary>总闸：true 时事件音效全部跳过，BGM 与环境循环音量压至 0。</summary>
    public static bool Disabled;

    /// <summary>BGM 目标音量（交叉淡入淡出的上限）。</summary>
    [Range(0f, 1f)] public float bgmVolume = 0.55f;

    const float BgmCrossfadeSeconds = 1.5f;
    const float AmbientFadeSeconds = 1.0f;
    const float AmbientMaxVolume = 0.5f;
    const float SfxVolume = 0.9f;
    const float SfxDedupeSeconds = 0.2f;
    const float SfxMuteSecondsPerRound = 0.026f; // secondsPerRound<=0.026 即 ≥4 倍速：一次性音效静音

    const string BgmNormalKey = "bgm_normal";
    const string BgmRushKey = "bgm_rush";
    const string BgmSettlementKey = "bgm_settlement";

    static readonly string[] BgmClipNames = { BgmNormalKey, BgmRushKey, BgmSettlementKey };

    static readonly string[] SfxClipNames =
    {
        "sfx_card_reveal", "sfx_contest_win", "sfx_guard_set", "sfx_guard_break",
        "sfx_squad_dispatch", "sfx_squad_arrive", "sfx_task_spawn", "sfx_task_complete",
        "sfx_verify_gate", "sfx_deliver", "sfx_rush_horn", "sfx_fresh_warning",
        "sfx_ui_click", "sfx_weather_rain", "sfx_weather_wind", "sfx_weather_heat",
        "sfx_hoofbeats", "sfx_water_row",
        // 推荐新增(本批音效包带):需在 SfxKeyFor 里映射事件才会响
        "sfx_contest_start", "sfx_squad_clear", "sfx_squad_failed", "sfx_verify_gate_start"
    };

    static LycheeAudioDirector instance;

    sealed class AmbientLoop
    {
        public AudioSource source;
        public string clipKey;
        public bool movement;   // 移动类循环：回放暂停时压静音
        public float target;    // 目标音量（0 或 AmbientMaxVolume）
        public float volume;    // 当前音量（1 秒线性渐变）
    }

    readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
    readonly Dictionary<string, float> lastSfxTimes = new Dictionary<string, float>();

    LycheeReplayPlayer player;
    AudioSource bgmSourceA;
    AudioSource bgmSourceB;
    AudioSource sfxSource;
    AmbientLoop rainLoop;
    AmbientLoop windLoop;
    AmbientLoop heatLoop;
    AmbientLoop hoofLoop;
    AmbientLoop rowLoop;
    AmbientLoop[] ambientLoops = Array.Empty<AmbientLoop>();

    float bgmWeightA;
    float bgmWeightB;
    bool bgmActiveIsA;
    AudioClip currentBgmClip;
    string desiredBgmKey = BgmNormalKey;
    bool initialized;
#if UNITY_WEBGL && !UNITY_EDITOR
    bool webAudioUnlockTried;
    float nextWebAudioUnlockProbe;

    [DllImport("__Internal")]
    static extern void LycheeResumeAudioContext();
#endif

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        try
        {
            if (Disabled) return;
            LycheeReplayPlayer host = FindFirstObjectByType<LycheeReplayPlayer>();
            if (host == null) return;
            if (host.GetComponent<LycheeAudioDirector>() != null) return;
            host.gameObject.AddComponent<LycheeAudioDirector>();
        }
        catch (Exception exception)
        {
            Debug.LogWarning("LycheeAudioDirector 自举失败: " + exception.Message);
        }
    }

    /// <summary>UI 系统统一点击音入口（素材缺失/未加载/总闸关闭时安全无操作，不受倍速静音影响）。</summary>
    public static void PlayUiClick()
    {
        if (instance != null) instance.TryPlaySfx("UI_CLICK", "sfx_ui_click", true);
    }

    void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(this);
            return;
        }
        instance = this;

        player = GetComponent<LycheeReplayPlayer>();
        if (player == null) player = FindFirstObjectByType<LycheeReplayPlayer>();

        bgmSourceA = CreateFlatSource(true, 0f);
        bgmSourceB = CreateFlatSource(true, 0f);
        sfxSource = CreateFlatSource(false, SfxVolume);
        rainLoop = CreateAmbientLoop("sfx_weather_rain", false);
        windLoop = CreateAmbientLoop("sfx_weather_wind", false);
        heatLoop = CreateAmbientLoop("sfx_weather_heat", false);
        hoofLoop = CreateAmbientLoop("sfx_hoofbeats", true);
        rowLoop = CreateAmbientLoop("sfx_water_row", true);
        ambientLoops = new[] { rainLoop, windLoop, heatLoop, hoofLoop, rowLoop };
        initialized = true;
    }

    void OnEnable()
    {
        LycheeReplayPlayer.ReplayEventRaised += HandleReplayEvent;
        LycheeReplayPlayer.RoundApplied += HandleRoundApplied;
        LycheeReplayPlayer.MatchOverRaised += HandleMatchOver;
    }

    void OnDisable()
    {
        LycheeReplayPlayer.ReplayEventRaised -= HandleReplayEvent;
        LycheeReplayPlayer.RoundApplied -= HandleRoundApplied;
        LycheeReplayPlayer.MatchOverRaised -= HandleMatchOver;
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    void Start()
    {
        if (!initialized) return;
        try
        {
            StartCoroutine(LoadAllClipsRoutine());
        }
        catch (Exception exception)
        {
            Debug.LogWarning("LycheeAudioDirector 启动音频加载失败: " + exception.Message, this);
        }
    }

    void Update()
    {
        if (!initialized) return;
        try
        {
            TryResumeWebAudio();
            UpdateBgm();
            UpdateAmbient();
        }
        catch (Exception)
        {
            // 任何运行时异常都不允许打断回放。
        }
    }

    // ---------------- 事件响应 ----------------

    void HandleReplayEvent(LycheeReplayMessage message, int round)
    {
        // ReplayEventRaised 在 HandleEvent 的 null 守卫之前触发，message 可能为 null。
        if (!initialized || message == null || string.IsNullOrEmpty(message.type)) return;

        // 真实回放发的是 RUSH_START；RUSH_PHASE_START 为旧名兼容。两者都要切冲刺 BGM。
        if (message.type == "RUSH_START" || message.type == "RUSH_PHASE_START") desiredBgmKey = BgmRushKey;

        if (Disabled) return;
        string clipKey = SfxKeyFor(message.type);
        if (!string.IsNullOrEmpty(clipKey)) TryPlaySfx(message.type, clipKey, false);
    }

    void HandleRoundApplied(LycheeReplayFrame previousFrame, LycheeReplayFrame currentFrame)
    {
        if (!initialized || currentFrame == null) return;
        try
        {
            // 回拖/循环重播：回到更早回合时恢复常规 BGM（RUSH 事件重放时会再次切换）。
            if (previousFrame != null && currentFrame.round < previousFrame.round && desiredBgmKey != BgmNormalKey)
                desiredBgmKey = BgmNormalKey;
            UpdateWeatherTargets(currentFrame);
            UpdateMovementTargets(currentFrame);
        }
        catch (Exception)
        {
        }
    }

    void HandleMatchOver(LycheeReplayOverFrame overFrame)
    {
        // overFrame 可能为 null，仅作为切歌信号使用。
        if (!initialized) return;
        desiredBgmKey = BgmSettlementKey;
    }

    static string SfxKeyFor(string eventType)
    {
        switch (eventType)
        {
            case "WINDOW_CONTEST_START": return "sfx_contest_start";   // 对抗开局(本批新增)
            case "WINDOW_CARD_REVEAL": return "sfx_card_reveal";
            case "WINDOW_CONTEST_END": return "sfx_contest_win";
            case "GUARD_SET": return "sfx_guard_set";
            case "GUARD_BREAK": return "sfx_guard_break";
            case "SQUAD_DISPATCH": return "sfx_squad_dispatch";
            case "SQUAD_ARRIVE": return "sfx_squad_arrive";
            case "SQUAD_CLEAR":
            case "SQUAD_CLEAR_SUCCESS": return "sfx_squad_clear";       // 小队清障成功(本批新增)
            case "SQUAD_FAILED": return "sfx_squad_failed";            // 小队失败(本批新增)
            case "VERIFY_GATE_START": return "sfx_verify_gate_start";  // 宫门验核开始(本批新增)
            case "TASK_SPAWN":
            case "BOUNTY_SPAWN":
            case "BOUNTY_CREATE": return "sfx_task_spawn";       // BOUNTY_CREATE 为 V4.8.12 新名
            case "TASK_COMPLETE":
            case "BOUNTY_COMPLETE":
            case "BOUNTY_CLAIM": return "sfx_task_complete";     // BOUNTY_CLAIM 为 V4.8.12 新名
            case "VERIFY_GATE_COMPLETE": return "sfx_verify_gate";
            case "DELIVER_SUCCESS": return "sfx_deliver";
            case "RUSH_START":                                    // 真实回放发的是 RUSH_START
            case "RUSH_PHASE_START": return "sfx_rush_horn";      // RUSH_PHASE_START 为旧名兼容
            // 只在「好果转坏」时响一声警示；FRESHNESS_DROP 每回合每队都发(本局 1012 次)，绑上去会从头响到尾，故不绑。
            case "GOOD_TO_BAD": return "sfx_fresh_warning";
            default: return null;
        }
    }

    void TryPlaySfx(string eventType, string clipKey, bool ignoreSpeedMute)
    {
        try
        {
            if (Disabled || sfxSource == null) return;
            if (!ignoreSpeedMute && player != null && player.secondsPerRound <= SfxMuteSecondsPerRound) return;

            float now = Time.unscaledTime;
            if (lastSfxTimes.TryGetValue(eventType, out float lastTime) && now - lastTime < SfxDedupeSeconds) return;

            AudioClip clip = GetClip(clipKey);
            if (clip == null) return;
            lastSfxTimes[eventType] = now;
            sfxSource.PlayOneShot(clip);
        }
        catch (Exception)
        {
        }
    }

    // ---------------- BGM ----------------

    void UpdateBgm()
    {
        AudioClip desired = GetClip(desiredBgmKey);
        if (desired != null && desired != currentBgmClip) BeginBgmCrossfade(desired);

        float master = Disabled ? 0f : Mathf.Clamp01(bgmVolume);
        float rate = BgmCrossfadeSeconds <= 0f ? 1f : Time.deltaTime / BgmCrossfadeSeconds;

        bgmWeightA = Mathf.MoveTowards(
            bgmWeightA,
            bgmActiveIsA && bgmSourceA != null && bgmSourceA.clip != null ? 1f : 0f,
            rate);
        bgmWeightB = Mathf.MoveTowards(
            bgmWeightB,
            !bgmActiveIsA && bgmSourceB != null && bgmSourceB.clip != null ? 1f : 0f,
            rate);

        if (bgmSourceA != null)
        {
            bgmSourceA.volume = bgmWeightA * master;
            if (!bgmActiveIsA && bgmWeightA <= 0f && bgmSourceA.isPlaying) bgmSourceA.Stop();
        }
        if (bgmSourceB != null)
        {
            bgmSourceB.volume = bgmWeightB * master;
            if (bgmActiveIsA && bgmWeightB <= 0f && bgmSourceB.isPlaying) bgmSourceB.Stop();
        }
    }

    void BeginBgmCrossfade(AudioClip clip)
    {
        if (clip == null) return;
        AudioSource incoming = bgmActiveIsA ? bgmSourceB : bgmSourceA;
        if (incoming == null) return;
        try
        {
            incoming.clip = clip;
            incoming.loop = true;
            incoming.volume = 0f;
            incoming.Play();
            bgmActiveIsA = !bgmActiveIsA;
            currentBgmClip = clip;
        }
        catch (Exception)
        {
        }
    }

    void TryResumeWebAudio()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        if (Time.unscaledTime < nextWebAudioUnlockProbe) return;
        bool firstProbe = !webAudioUnlockTried;
        bool userGesture = Input.GetMouseButtonDown(0) || Input.touchCount > 0 || Input.anyKeyDown;
        if (!firstProbe && !userGesture) return;

        webAudioUnlockTried = true;
        nextWebAudioUnlockProbe = Time.unscaledTime + 0.5f;
        try { LycheeResumeAudioContext(); } catch (Exception) { }

        // 某些浏览器在 AudioContext 恢复后不会自动续起先前被挂起的 AudioSource，补一次播放兜底。
        ResumeSourceIfNeeded(bgmSourceA, bgmWeightA);
        ResumeSourceIfNeeded(bgmSourceB, bgmWeightB);
#endif
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    static void ResumeSourceIfNeeded(AudioSource source, float weight)
    {
        if (source == null || source.clip == null || source.isPlaying || weight <= 0f) return;
        try { source.Play(); } catch (Exception) { }
    }
#endif

    // ---------------- 环境循环 ----------------

    void UpdateWeatherTargets(LycheeReplayFrame frame)
    {
        bool rain = false;
        bool fog = false;
        bool hot = false;
        LycheeReplayWeatherState[] active = frame.weather == null ? null : frame.weather.active;
        if (active != null)
        {
            for (int i = 0; i < active.Length; i++)
            {
                LycheeReplayWeatherState state = active[i];
                if (state == null) continue;
                if (state.type == "HEAVY_RAIN") rain = true;
                else if (state.type == "MOUNTAIN_FOG") fog = true;
                else if (state.type == "HOT") hot = true;
            }
        }
        if (rainLoop != null) rainLoop.target = rain ? AmbientMaxVolume : 0f;
        if (windLoop != null) windLoop.target = fog ? AmbientMaxVolume : 0f;
        if (heatLoop != null) heatLoop.target = hot ? AmbientMaxVolume : 0f;
    }

    void UpdateMovementTargets(LycheeReplayFrame frame)
    {
        bool landMoving = false;
        bool waterMoving = false;
        if (frame.players != null)
        {
            for (int i = 0; i < frame.players.Length; i++)
            {
                LycheeReplayPlayerState state = frame.players[i];
                if (state == null || state.state != "MOVING") continue;
                bool onWater = player != null &&
                    player.TryGetEdge(state.routeEdgeId, out MapRouteEdge edge) &&
                    edge != null &&
                    edge.routeType == MapRouteType.Water;
                if (onWater) waterMoving = true;
                else landMoving = true;
            }
        }
        // 两个移动循环互斥：陆路（非水路）优先，仅当只有水路车队移动时播放划水声。
        if (hoofLoop != null) hoofLoop.target = landMoving ? AmbientMaxVolume : 0f;
        if (rowLoop != null) rowLoop.target = !landMoving && waterMoving ? AmbientMaxVolume : 0f;
    }

    void UpdateAmbient()
    {
        float rate = AmbientFadeSeconds <= 0f
            ? AmbientMaxVolume
            : Time.deltaTime * (AmbientMaxVolume / AmbientFadeSeconds);
        bool movementAllowed = player == null || player.IsPlaying;

        for (int i = 0; i < ambientLoops.Length; i++)
        {
            AmbientLoop loop = ambientLoops[i];
            if (loop == null || loop.source == null) continue;

            if (loop.source.clip == null)
            {
                AudioClip clip = GetClip(loop.clipKey);
                if (clip != null) loop.source.clip = clip;
            }

            float target = loop.target;
            if (Disabled || loop.source.clip == null) target = 0f;
            else if (loop.movement && !movementAllowed) target = 0f;

            loop.volume = Mathf.MoveTowards(loop.volume, target, rate);
            loop.source.volume = loop.volume;

            if (loop.source.clip == null) continue;
            if (target > 0f && !loop.source.isPlaying) loop.source.Play();
            else if (loop.volume <= 0f && target <= 0f && loop.source.isPlaying) loop.source.Stop();
        }
    }

    // ---------------- 加载 ----------------

    IEnumerator LoadAllClipsRoutine()
    {
        yield return LoadGroupRoutine("BGM", BgmClipNames);
        yield return LoadGroupRoutine("SFX", SfxClipNames);
        Debug.Log(
            "LycheeAudioDirector 音频加载完成: " + clips.Count + "/" +
            (BgmClipNames.Length + SfxClipNames.Length) + " 个（缺失文件已静默跳过）。",
            this);
    }

    IEnumerator LoadGroupRoutine(string subFolder, string[] baseNames)
    {
        if (baseNames == null) yield break;
        for (int i = 0; i < baseNames.Length; i++)
        {
            string baseName = baseNames[i];
            if (string.IsNullOrEmpty(baseName) || clips.ContainsKey(baseName)) continue;

#if UNITY_WEBGL && !UNITY_EDITOR
            // WebGL 无法 File.Exists 探测扩展名，按 .wav/.ogg/.mp3 顺序逐个请求，命中即停
            string directoryUrl = Application.streamingAssetsPath + "/Audio/" + subFolder + "/";
            for (int c = 0; c < WebGLAudioCandidates.Length; c++)
            {
                string url = directoryUrl + baseName + WebGLAudioCandidates[c].extension;
                yield return TryLoadClipRoutine(url, WebGLAudioCandidates[c].audioType, baseName);
                if (clips.ContainsKey(baseName)) break;
            }
#else
            string path = LycheeResolveAudioPath(subFolder, baseName, out AudioType audioType);
            if (string.IsNullOrEmpty(path)) continue;
            yield return TryLoadClipRoutine(LycheeFileUrl(path), audioType, baseName);
#endif
        }
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    struct WebGLAudioCandidate
    {
        public string extension;
        public AudioType audioType;
    }

    // 素材已统一为 .ogg（WebGL 唯一既能实时解码又紧凑的格式）。只请求 .ogg：命中即播放、不再
    // 探测不存在的 .wav/.mp3 而刷 404，也避免 MP3 在 WebGL 下"成功但 0 采样"的静音陷阱。
    static readonly WebGLAudioCandidate[] WebGLAudioCandidates =
    {
        new WebGLAudioCandidate { extension = ".ogg", audioType = AudioType.OGGVORBIS }
    };
#endif

    IEnumerator TryLoadClipRoutine(string url, AudioType audioType, string baseName)
    {
        UnityWebRequest request = null;
        try
        {
            request = UnityWebRequestMultimedia.GetAudioClip(url, audioType);
        }
        catch (Exception)
        {
            request = null;
        }
        if (request == null) yield break;

        using (request)
        {
            yield return request.SendWebRequest();
            try
            {
                if (request.result == UnityWebRequest.Result.Success)
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip != null)
                    {
                        clip.name = "LycheeAudio_" + baseName;
                        clips[baseName] = clip;
                    }
                }
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>按 .wav/.ogg/.mp3 顺序解析 StreamingAssets/Audio/&lt;子目录&gt;/&lt;文件名&gt; 的实际路径，全部缺失返回 null。</summary>
    static string LycheeResolveAudioPath(string subFolder, string baseName, out AudioType audioType)
    {
        audioType = AudioType.WAV;
        try
        {
            string directory = Path.Combine(Application.streamingAssetsPath, "Audio", subFolder);
            string wavPath = Path.Combine(directory, baseName + ".wav");
            if (File.Exists(wavPath))
            {
                audioType = AudioType.WAV;
                return wavPath;
            }
            string oggPath = Path.Combine(directory, baseName + ".ogg");
            if (File.Exists(oggPath))
            {
                audioType = AudioType.OGGVORBIS;
                return oggPath;
            }
            string mp3Path = Path.Combine(directory, baseName + ".mp3");
            if (File.Exists(mp3Path))
            {
                audioType = AudioType.MPEG;
                return mp3Path;
            }
        }
        catch (Exception)
        {
        }
        return null;
    }

    /// <summary>本地绝对路径转 file:// URL（转义空格等字符，失败时退回简单拼接）。</summary>
    static string LycheeFileUrl(string path)
    {
        try
        {
            return new Uri(path).AbsoluteUri;
        }
        catch (Exception)
        {
            return "file://" + path;
        }
    }

    // ---------------- 工具 ----------------

    AudioClip GetClip(string key)
    {
        return !string.IsNullOrEmpty(key) && clips.TryGetValue(key, out AudioClip clip) ? clip : null;
    }

    AudioSource CreateFlatSource(bool loop, float volume)
    {
        AudioSource source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = loop;
        source.spatialBlend = 0f; // 全部 2D
        source.volume = volume;
        return source;
    }

    AmbientLoop CreateAmbientLoop(string clipKey, bool movement)
    {
        return new AmbientLoop
        {
            source = CreateFlatSource(true, 0f),
            clipKey = clipKey,
            movement = movement,
            target = 0f,
            volume = 0f
        };
    }
}
