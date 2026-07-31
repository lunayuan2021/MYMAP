using System;
using System.Collections;
using System.Collections.Generic;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif
using UnityEngine;

/// <summary>
/// 音频导演：统一管理回放的 BGM（双源交叉淡入淡出）、事件音效与环境循环音。
/// 素材改为从 Resources 加载 Unity 导入的 AudioClip（Assets/LycheeReplayDemo/Resources/LycheeAudio/<BGM|SFX>/），
/// 不再从 StreamingAssets 流式拉 .ogg：流式 OGG 在 Safari 解不了码=全静音，且服务器对 .ogg 二次 gzip 会整坏=各浏览器都静音。
/// 导入的 AudioClip 由 Unity 自带 WASM 音频引擎解码、烘进 .data，所有浏览器一致可放、不经服务器单独请求。
/// 另兜底 EnsureAudioListener：没有 Listener 整个工程一点声音都没有。文件缺失一律静默跳过，绝不影响回放本身。
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
    bool webAudioUnlocked;     // AudioContext 第一次进入 running 后置 true：此时才真被 resume
    float webAudioKickUntil;   // 解锁后的"补播窗口"截止时刻：ctx.resume() 是异步的，窗口内每个探测周期都重启 BGM，直到落在已恢复的 context 上
    float nextWebAudioUnlockProbe;
    bool audioUpdateErrorLogged;  // Update 里 try/catch 吞掉的异常只记一次（否则每帧刷屏），用于排查 BGM 静默失败

    [DllImport("__Internal")]
    static extern void LycheeResumeAudioContext();
    [DllImport("__Internal")]
    static extern int LycheeAudioContextRunning();
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
        EnsureAudioListener();   // 没有 Listener 整个工程就一点声音都没有，先保一道
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
        catch (Exception e)
        {
            // 任何运行时异常都不允许打断回放；但**只记一次**——之前是完全静默吞掉，BGM 若因异常每帧失败也看不见。
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!audioUpdateErrorLogged) { audioUpdateErrorLogged = true; Debug.LogWarning("[LycheeAudioDirector] 音频 Update 异常(已吞、仅记一次，可能导致 BGM 静默): " + e); }
#endif
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
        nextWebAudioUnlockProbe = Time.unscaledTime + 0.4f;

        // ⚠ 关键修复：绝不能用旧 UnityEngine.Input（项目已切到 Input System 包，旧 Input.GetMouseButtonDown/
        // touchCount/anyKeyDown 会每帧抛 InvalidOperationException → 把整个音频 Update 打断 → UpdateBgm 永不执行 →
        // BGM 永远静音；音效走另一条 PlayOneShot 路径所以照响）。改为：每个探测周期都幂等地请求恢复 AudioContext
        // （index.html 亦在 pointerdown/touchstart/keydown 上调它），并通过 jslib 直接查 context 是否 running 来判定
        // “用户解锁了没”，完全不碰 Input。
        try { LycheeResumeAudioContext(); } catch (Exception) { }

        bool running = false;
        try { running = LycheeAudioContextRunning() != 0; } catch (Exception) { }

        // context 第一次进入 running（用户已手势 + resume 完成）：开 2.5s 补播窗口。ctx.resume() 是异步的，
        // 早期对 looping BGM 调过的 Play() 在挂起 context 上落空、且 Unity 把 isPlaying 假标 true 不会自动续起，
        // 所以窗口内每个周期无条件 Stop+Play，直到稳稳落在已 running 的 context 上。
        if (running && !webAudioUnlocked)
        {
            webAudioUnlocked = true;
            webAudioKickUntil = Time.unscaledTime + 2.5f;
        }

        if (webAudioUnlocked && Time.unscaledTime < webAudioKickUntil)
        {
            ForceRestartBgm(bgmSourceA);
            ForceRestartBgm(bgmSourceB);
        }
        else if (!webAudioUnlocked)
        {
            // 还没解锁：context 仍 suspended，发不出声，只保持 clip 就绪的轻量兜底。
            ResumeSourceIfNeeded(bgmSourceA, bgmWeightA);
            ResumeSourceIfNeeded(bgmSourceB, bgmWeightB);
        }
#endif
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    static void ResumeSourceIfNeeded(AudioSource source, float weight)
    {
        if (source == null || source.clip == null || source.isPlaying || weight <= 0f) return;
        try { source.Play(); } catch (Exception) { }
    }

    // 无条件重启（不看 isPlaying，因为挂起 context 上它会假阳性）：只要有 clip 就 Stop+Play，把 BGM 踢进已恢复的 context。
    static void ForceRestartBgm(AudioSource source)
    {
        if (source == null || source.clip == null) return;
        try { source.Stop(); source.Play(); } catch (Exception) { }
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

    // 改为从 Resources 加载 Unity 导入的 AudioClip（不再从 StreamingAssets 流式拉 .ogg）。
    // 原因：流式 OGG 在 Safari 解不了码（全静音），且服务器若对 .ogg 二次 gzip 会整坏文件（各浏览器都静音）。
    // 导入的 AudioClip 由 Unity 自带的 WASM 音频引擎解码、烘进 .data 文件，所有浏览器一致可放、且不经服务器单独请求。
    // 文件在 Assets/LycheeReplayDemo/Resources/LycheeAudio/<BGM|SFX>/<名字>.ogg。
    IEnumerator LoadAllClipsRoutine()
    {
        LoadGroupFromResources("BGM", BgmClipNames);
        LoadGroupFromResources("SFX", SfxClipNames);
        Debug.Log(
            "LycheeAudioDirector 音频加载完成(Resources): " + clips.Count + "/" +
            (BgmClipNames.Length + SfxClipNames.Length) + " 个（缺失文件已静默跳过）。",
            this);
        yield break;
    }

    void LoadGroupFromResources(string subFolder, string[] baseNames)
    {
        if (baseNames == null) return;
        for (int i = 0; i < baseNames.Length; i++)
        {
            string baseName = baseNames[i];
            if (string.IsNullOrEmpty(baseName) || clips.ContainsKey(baseName)) continue;
            try
            {
                AudioClip clip = Resources.Load<AudioClip>("LycheeAudio/" + subFolder + "/" + baseName);
                if (clip != null) clips[baseName] = clip;
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>兜底确保场景里有一个 AudioListener——没有它整个工程一点声音都没有（BGM+音效全哑）。
    /// 场景里没序列化 Listener，平时靠 LycheeCinematicDirector 给相机加；这里再保一道，且避免重复。</summary>
    void EnsureAudioListener()
    {
        try
        {
            if (FindFirstObjectByType<AudioListener>() != null) return;
            Camera cam = Camera.main;
            if (cam == null) cam = FindFirstObjectByType<Camera>();
            GameObject host = cam != null ? cam.gameObject : gameObject;
            if (host.GetComponent<AudioListener>() == null) host.AddComponent<AudioListener>();
        }
        catch (Exception)
        {
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
