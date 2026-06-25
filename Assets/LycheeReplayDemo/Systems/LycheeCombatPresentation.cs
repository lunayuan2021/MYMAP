using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 对抗演出系统（运行时自举，MOBA 血条 + 飘字）：
/// 1. 设卡血条 —— guard 有效（defense>0 且 ownerTeamId 非空）的节点上方 1.3m 显示程序化 MOBA 血条
///    （暗色背板 + 左锚填充 + 每点防御一道分段刻线），填充色随守方队色；defense 下降时白色「损伤残影」
///    保持旧值再 0.4s 收缩到新值（双层 fill），整条 0.3s 抖动；guard 消失/归零时碎裂淡出；血条实例缓存复用。
/// 2. 对抗决斗条 —— WINDOW_CONTEST_START 在目标节点上空 3.2m 生成左红右蓝相向决斗条（各 3 段=拍胜点上限）
///    + 中央金色 VS；WINDOW_CARD_REVEAL 胜方段队色发光闪 0.5s + 中央金白火花迸发 + 败方条横向抖动，
///    并驱动双方 Captain 演出（双方 Attack、败方追加 Hit）；WINDOW_CONTEST_END 胜方金色描边脉冲 1s、
///    败方条变灰，1.8s 后整体淡出销毁；缺 START 的孤儿 END / 回放跳转都安全兜底清理。
/// 3. 飘字伤害 —— 世界空间 TextMesh 对象池（12+），上升 1.2m/s、1.1s 淡出、Perlin 水平偏移 ±0.3：
///    GUARD_BREAK 红色大字 -攻坚值（防御差额另出灰字）、削弱紫字、增援绿字、
///    鲜度跌破 10 倍数阈值时车队头顶琥珀字「-1 好果」、献贡出牌琥珀字「献贡 -1 好果」。
/// 4. 受击反馈 —— GUARD_BREAK 拒马位置木屑色粒子爆（0.5s）+ 该节点血条强抖动。
/// 与 LycheeWorldMarkerSystem 的「⛨ 防 N/M」文字（约 1.95m）错开高度，不修改任何现有系统。
/// </summary>
public class LycheeCombatPresentation : MonoBehaviour
{
    /// <summary>全局禁用开关：true 时不自举；运行中切换会隐藏/恢复全部演出物。</summary>
    public static bool Disabled;

    // ---------- 血条参数 ----------
    const float BarHover = 1.3f;             // 节点上方 1.3m，低于拒马文字
    const float BarWidth = 1.6f;
    const float BarHeight = 0.18f;
    const float FillWidth = 1.54f;
    const float FillHeight = 0.125f;
    const float GhostHoldSeconds = 0.12f;    // 损伤残影先停留
    const float GhostShrinkSeconds = 0.4f;   // 再收缩到新值
    const float BarShakeSeconds = 0.3f;
    const float BreakFadeSeconds = 0.6f;     // 碎裂淡出时长
    const int MaxTickSegments = 20;          // 刻线段数上限（防异常数据）

    // ---------- 决斗条参数 ----------
    const float DuelHover = 3.2f;
    const int DuelSegmentCount = 3;          // 拍胜点上限
    const float SegFlashSeconds = 0.5f;
    const float SideShakeSeconds = 0.45f;
    const float OutlinePulseSeconds = 1f;
    const float DuelLingerSeconds = 1.8f;
    const float DuelFadeSeconds = 0.5f;

    // ---------- 飘字参数 ----------
    const int FloatPoolSize = 12;
    const int FloatPoolMax = 18;
    const float RiseSpeed = 1.2f;            // 上升 1.2m/s
    const float FloatLifeSeconds = 1.1f;     // 1.1s 淡出
    const float NodeFloatHeight = 1.8f;
    const float ConvoyFloatHeight = 2.9f;    // 高于车队 Label(2.25m)

    static readonly Color BarBackColor = new Color(0.06f, 0.06f, 0.09f, 0.88f);
    static readonly Color GhostWhite = new Color(1f, 1f, 1f, 0.95f);
    static readonly Color TickDark = new Color(0.02f, 0.02f, 0.03f, 0.6f);
    static readonly Color LitRed = new Color(1f, 0.27f, 0.18f, 1f);
    static readonly Color LitBlue = new Color(0.32f, 0.6f, 1f, 1f);
    static readonly Color DimRed = new Color(0.42f, 0.12f, 0.09f, 0.9f);
    static readonly Color DimBlue = new Color(0.1f, 0.2f, 0.46f, 0.9f);
    static readonly Color LitGray = new Color(0.5f, 0.5f, 0.52f, 0.95f);
    static readonly Color DimGray = new Color(0.2f, 0.2f, 0.22f, 0.85f);
    static readonly Color GoldColor = new Color(1f, 0.78f, 0.22f, 1f);
    static readonly Color SparkGold = new Color(1f, 0.84f, 0.32f, 1f);
    static readonly Color DuelBackColor = new Color(0.05f, 0.05f, 0.08f, 0.88f);
    static readonly Color DamageRed = new Color(1f, 0.22f, 0.16f, 1f);
    static readonly Color GrayText = new Color(0.8f, 0.8f, 0.8f, 1f);
    static readonly Color PurpleText = new Color(0.78f, 0.42f, 1f, 1f);
    static readonly Color GreenText = new Color(0.36f, 0.95f, 0.46f, 1f);
    static readonly Color AmberText = new Color(1f, 0.72f, 0.2f, 1f);
    static readonly Color WoodChipA = new Color(0.66f, 0.45f, 0.22f, 1f);
    static readonly Color WoodChipB = new Color(0.44f, 0.29f, 0.14f, 1f);

    // ---------- 设卡血条 ----------
    sealed class GuardBar
    {
        public string nodeId;
        public GameObject root;
        public Transform shakeNode;
        public Transform fillAnchor;
        public Transform ghostAnchor;
        public Material backMat;
        public Material fillMat;
        public Material ghostMat;
        public Material tickMat;
        public readonly List<GameObject> ticks = new List<GameObject>();
        public int tickCount;
        public string ownerTeamId;
        public Color fillColor = Color.white;
        public int defense = -1;
        public int maxDefense;
        public float currentGhost;
        public float ghostFrom;
        public float ghostTarget;
        public float ghostHoldStart = -1f;
        public float shakeUntil;
        public float shakeStrength;
        public float dieStart = -1f;
        public float seed;
        public int stamp;
    }

    // ---------- 对抗决斗条 ----------
    struct SegFlash
    {
        public Material mat;
        public Color target;
        public float start;
    }

    sealed class DuelDisplay
    {
        public string nodeId;
        public GameObject root;
        public Transform panel;
        public Transform redSide;
        public Transform blueSide;
        public readonly Material[] redMats = new Material[DuelSegmentCount];
        public readonly Material[] blueMats = new Material[DuelSegmentCount];
        public Material backMat;
        public Material outlineMat;
        public Renderer outlineRenderer;
        public Transform outline;
        public TextMesh vs;
        public readonly List<Material> ownedMaterials = new List<Material>();
        public readonly List<SegFlash> flashes = new List<SegFlash>();
        public int redLit;
        public int blueLit;
        public float redShakeUntil;
        public float blueShakeUntil;
        public float endStart = -1f;
        public int endWinnerCamp = -1;
        public bool fadeCaptured;
        public readonly List<Color> fadeBase = new List<Color>();
        public Color vsBase = Color.white;
    }

    // ---------- 飘字 ----------
    sealed class FloatText
    {
        public GameObject go;
        public TextMesh text;
        public bool active;
        public float start;
        public float duration = FloatLifeSeconds;
        public Vector3 origin;
        public float ox;
        public float oz;
        public Color baseColor = Color.white;
    }

    LycheeReplayPlayer player;
    Transform fxRoot;
    readonly Dictionary<string, GuardBar> bars = new Dictionary<string, GuardBar>();
    readonly List<GuardBar> barPool = new List<GuardBar>();
    readonly List<string> barRemovalBuffer = new List<string>();
    readonly List<FloatText> floats = new List<FloatText>();
    readonly List<Object> generatedAssets = new List<Object>();
    DuelDisplay duel;
    Mesh quadMesh;
    Material particleMaterial;
    bool sharedAssetsReady;
    bool disabledApplied;
    int frameStamp;
    int floatSeq;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Disabled) return;
        LycheeReplayPlayer host = FindFirstObjectByType<LycheeReplayPlayer>();
        if (host == null) return;
        if (host.GetComponent<LycheeCombatPresentation>() != null) return;
        host.gameObject.AddComponent<LycheeCombatPresentation>();
    }

    void Awake()
    {
        player = GetComponent<LycheeReplayPlayer>();
        fxRoot = new GameObject("CombatPresentationRoot").transform;
        fxRoot.SetParent(transform, false);
        EnsureFloatPool();
    }

    void OnEnable()
    {
        LycheeReplayPlayer.RoundApplied += OnRoundApplied;
        LycheeReplayPlayer.ReplayEventRaised += OnReplayEvent;
    }

    void OnDisable()
    {
        LycheeReplayPlayer.RoundApplied -= OnRoundApplied;
        LycheeReplayPlayer.ReplayEventRaised -= OnReplayEvent;
    }

    void OnDestroy()
    {
        LycheeReplayPlayer.RoundApplied -= OnRoundApplied;
        LycheeReplayPlayer.ReplayEventRaised -= OnReplayEvent;
        DestroyDuel();
        bars.Clear();
        barPool.Clear();
        floats.Clear();
        if (fxRoot != null) Destroy(fxRoot.gameObject);
        foreach (Object asset in generatedAssets)
        {
            if (asset != null) Destroy(asset);
        }
        generatedAssets.Clear();
    }

    void Update()
    {
        if (fxRoot == null) return;
        if (Disabled != disabledApplied)
        {
            disabledApplied = Disabled;
            fxRoot.gameObject.SetActive(!disabledApplied);
        }
        if (disabledApplied) return;
        float now = Time.time;
        UpdateBars(now);
        UpdateDuel(now);
        UpdateFloats(now);
    }

    // ====================================================================
    // 回放事件入口
    // ====================================================================

    void OnRoundApplied(LycheeReplayFrame previous, LycheeReplayFrame current)
    {
        if (current == null || player == null || fxRoot == null) return;
        bool seek = previous == null || Mathf.Abs(current.round - previous.round) != 1;
        if (seek)
        {
            DestroyDuel();          // 跳转后窗口状态不可靠，直接清理
            RecycleAllFloats();
        }
        RefreshGuardBars(current, seek);
        if (!seek && !Disabled) CheckFreshnessDrops(previous, current);
    }

    void OnReplayEvent(LycheeReplayMessage message, int round)
    {
        if (Disabled || message == null || string.IsNullOrEmpty(message.type)) return;
        if (player == null || fxRoot == null) return;
        LycheeReplayPayload payload = message.payload ?? new LycheeReplayPayload();
        switch (message.type)
        {
            case "WINDOW_CONTEST_START":
                HandleContestStart(payload);
                break;
            case "WINDOW_CARD_REVEAL":
                HandleCardReveal(payload);
                break;
            case "WINDOW_CONTEST_END":
                HandleContestEnd(payload);
                break;
            case "GUARD_BREAK":
                HandleGuardBreak(payload);
                break;
            case "SQUAD_WEAKEN":            // V4.8.12 裸名
            case "SQUAD_WEAKEN_SUCCESS":
                SpawnNodeFloat(payload, DefenseDeltaText(payload, -2), PurpleText, 34, 0.06f);
                break;
            case "SQUAD_REINFORCE":        // V4.8.12 裸名
            case "SQUAD_REINFORCE_SUCCESS":
                SpawnNodeFloat(payload, DefenseDeltaText(payload, 2), GreenText, 34, 0.06f);
                break;
        }
    }

    // ====================================================================
    // 模块 1：设卡血条
    // ====================================================================

    void RefreshGuardBars(LycheeReplayFrame frame, bool snap)
    {
        frameStamp++;
        if (frame.nodes != null)
        {
            foreach (LycheeReplayNodeState node in frame.nodes)
            {
                if (node == null || string.IsNullOrEmpty(node.nodeId)) continue;
                LycheeReplayGuardState guard = node.guard;
                bool valid = guard != null && guard.defense > 0 && !string.IsNullOrEmpty(guard.ownerTeamId);
                if (!valid) continue;
                if (!player.TryGetStationPosition(node.nodeId, out Vector3 position, out _)) continue;

                bars.TryGetValue(node.nodeId, out GuardBar bar);
                if (bar != null && bar.dieStart >= 0f)
                {
                    // 碎裂途中 guard 重新出现（多见于回放跳转）：丢弃旧条重建
                    RecycleBar(bar);
                    bars.Remove(node.nodeId);
                    bar = null;
                }
                if (bar == null)
                {
                    bar = ObtainBar(node.nodeId);
                    if (bar == null) continue;
                    bars[node.nodeId] = bar;
                }
                UpdateBarState(bar, guard, position, snap);
                bar.stamp = frameStamp;
            }
        }

        // 本帧未出现的 guard → 碎裂淡出（跳转时直接回收）
        barRemovalBuffer.Clear();
        foreach (KeyValuePair<string, GuardBar> pair in bars)
        {
            GuardBar bar = pair.Value;
            if (bar == null || bar.root == null)
            {
                barRemovalBuffer.Add(pair.Key);
                continue;
            }
            if (bar.stamp == frameStamp || bar.dieStart >= 0f) continue;
            if (snap) barRemovalBuffer.Add(pair.Key);
            else StartBarBreak(bar);
        }
        foreach (string key in barRemovalBuffer)
        {
            if (bars.TryGetValue(key, out GuardBar bar)) RecycleBar(bar);
            bars.Remove(key);
        }
    }

    void UpdateBarState(GuardBar bar, LycheeReplayGuardState guard, Vector3 position, bool snap)
    {
        if (bar == null || bar.root == null || guard == null) return;
        bar.root.transform.position = position + Vector3.up * BarHover;
        int max = Mathf.Max(1, Mathf.Max(guard.maxDefense, guard.defense));
        if (bar.tickCount != max) RebuildTicks(bar, max);
        if (bar.ownerTeamId != guard.ownerTeamId) ApplyOwnerColor(bar, guard.ownerTeamId);

        float fraction = Mathf.Clamp01(guard.defense / (float)max);
        bool decreased = bar.defense >= 0 && guard.defense < bar.defense && bar.maxDefense == max;
        if (snap || !decreased)
        {
            SetFill(bar, fraction);
            SetGhost(bar, fraction);
            bar.ghostHoldStart = -1f;
        }
        else
        {
            // 双层 fill：实条立即到新值，白色残影从旧值停留后 0.4s 收缩
            float oldFraction = Mathf.Clamp01(bar.defense / (float)Mathf.Max(1, bar.maxDefense));
            SetFill(bar, fraction);
            bar.ghostFrom = Mathf.Max(oldFraction, bar.currentGhost);
            bar.ghostTarget = fraction;
            bar.ghostHoldStart = Time.time;
            SetGhost(bar, bar.ghostFrom);
            bar.shakeUntil = Mathf.Max(bar.shakeUntil, Time.time + BarShakeSeconds);
            bar.shakeStrength = Mathf.Max(bar.shakeStrength, 0.05f);
        }
        bar.defense = guard.defense;
        bar.maxDefense = max;
    }

    void UpdateBars(float now)
    {
        if (bars.Count == 0) return;
        barRemovalBuffer.Clear();
        foreach (KeyValuePair<string, GuardBar> pair in bars)
        {
            GuardBar bar = pair.Value;
            if (bar == null || bar.root == null)
            {
                barRemovalBuffer.Add(pair.Key);
                continue;
            }

            if (bar.dieStart >= 0f)
            {
                // 碎裂淡出：透明度衰减 + 横向崩开纵向压扁
                float t = (now - bar.dieStart) / BreakFadeSeconds;
                if (t >= 1f)
                {
                    barRemovalBuffer.Add(pair.Key);
                    continue;
                }
                SetBarAlpha(bar, 1f - t);
                bar.shakeNode.localScale = new Vector3(1f + t * 0.35f, Mathf.Max(0.04f, 1f - t * t), 1f);
                continue;
            }

            // 损伤残影收缩
            if (bar.ghostHoldStart >= 0f)
            {
                float t = (now - bar.ghostHoldStart - GhostHoldSeconds) / GhostShrinkSeconds;
                if (t >= 1f)
                {
                    SetGhost(bar, bar.ghostTarget);
                    bar.ghostHoldStart = -1f;
                }
                else if (t > 0f)
                {
                    SetGhost(bar, Mathf.Lerp(bar.ghostFrom, bar.ghostTarget, Mathf.SmoothStep(0f, 1f, t)));
                }
            }

            // 抖动（Perlin，无 Random）
            if (now < bar.shakeUntil)
            {
                float remain = Mathf.Clamp01((bar.shakeUntil - now) / BarShakeSeconds);
                float amp = bar.shakeStrength * remain;
                float nx = (Mathf.PerlinNoise(now * 26f, bar.seed) - 0.5f) * 2f;
                float ny = (Mathf.PerlinNoise(bar.seed, now * 26f) - 0.5f) * 2f;
                bar.shakeNode.localPosition = new Vector3(nx * amp, ny * amp * 0.6f, 0f);
            }
            else if (bar.shakeStrength > 0f)
            {
                bar.shakeNode.localPosition = Vector3.zero;
                bar.shakeStrength = 0f;
            }
        }
        foreach (string key in barRemovalBuffer)
        {
            if (bars.TryGetValue(key, out GuardBar bar)) RecycleBar(bar);
            bars.Remove(key);
        }
    }

    GuardBar ObtainBar(string nodeId)
    {
        EnsureSharedAssets();
        GuardBar bar = null;
        while (barPool.Count > 0 && (bar == null || bar.root == null))
        {
            bar = barPool[barPool.Count - 1];
            barPool.RemoveAt(barPool.Count - 1);
        }
        if (bar == null || bar.root == null) bar = BuildBar();
        if (bar == null) return null;
        bar.nodeId = nodeId;
        bar.root.name = "GuardHpBar_" + nodeId;
        bar.seed = (Mathf.Abs(nodeId.GetHashCode()) % 977) * 0.013f;
        bar.defense = -1;
        bar.maxDefense = 0;
        bar.tickCount = -1;
        bar.ownerTeamId = null;
        bar.dieStart = -1f;
        bar.ghostHoldStart = -1f;
        bar.shakeUntil = 0f;
        bar.shakeStrength = 0f;
        bar.currentGhost = 0f;
        bar.shakeNode.localPosition = Vector3.zero;
        bar.shakeNode.localScale = Vector3.one;
        bar.root.SetActive(true);
        SetBarAlpha(bar, 1f);
        return bar;
    }

    GuardBar BuildBar()
    {
        if (quadMesh == null || fxRoot == null) return null;
        GuardBar bar = new GuardBar();
        bar.root = new GameObject("GuardHpBar");
        bar.root.transform.SetParent(fxRoot, false);
        bar.root.AddComponent<LycheeReplayBillboard>();
        bar.shakeNode = new GameObject("Shake").transform;
        bar.shakeNode.SetParent(bar.root.transform, false);

        bar.backMat = CreateRuntimeMaterial("lychee_hpbar_back", BarBackColor, 3000, true);
        bar.ghostMat = CreateRuntimeMaterial("lychee_hpbar_ghost", GhostWhite, 3001, true);
        bar.fillMat = CreateRuntimeMaterial("lychee_hpbar_fill", LitRed, 3002, true);
        bar.tickMat = CreateRuntimeMaterial("lychee_hpbar_tick", TickDark, 3003, true);

        CreateQuad(bar.shakeNode, "Back", bar.backMat, Vector3.zero, new Vector3(BarWidth, BarHeight, 1f));
        bar.ghostAnchor = new GameObject("GhostAnchor").transform;
        bar.ghostAnchor.SetParent(bar.shakeNode, false);
        bar.ghostAnchor.localPosition = new Vector3(-FillWidth * 0.5f, 0f, -0.002f);
        CreateQuad(bar.ghostAnchor, "GhostFill", bar.ghostMat, new Vector3(0.5f, 0f, 0f), Vector3.one);
        bar.fillAnchor = new GameObject("FillAnchor").transform;
        bar.fillAnchor.SetParent(bar.shakeNode, false);
        bar.fillAnchor.localPosition = new Vector3(-FillWidth * 0.5f, 0f, -0.004f);
        CreateQuad(bar.fillAnchor, "Fill", bar.fillMat, new Vector3(0.5f, 0f, 0f), Vector3.one);
        SetFill(bar, 0f);
        SetGhost(bar, 0f);
        return bar;
    }

    void RebuildTicks(GuardBar bar, int maxDefense)
    {
        if (bar == null || bar.shakeNode == null) return;
        foreach (GameObject tick in bar.ticks)
        {
            if (tick != null) Destroy(tick);
        }
        bar.ticks.Clear();
        bar.tickCount = maxDefense;
        int segments = Mathf.Clamp(maxDefense, 1, MaxTickSegments);
        for (int i = 1; i < segments; i++)
        {
            float x = -FillWidth * 0.5f + FillWidth * (i / (float)segments);
            Renderer tick = CreateQuad(
                bar.shakeNode,
                "Tick_" + i,
                bar.tickMat,
                new Vector3(x, 0f, -0.006f),
                new Vector3(0.02f, FillHeight + 0.02f, 1f));
            if (tick != null) bar.ticks.Add(tick.gameObject);
        }
    }

    void ApplyOwnerColor(GuardBar bar, string ownerTeamId)
    {
        bar.ownerTeamId = ownerTeamId;
        string key = string.IsNullOrEmpty(ownerTeamId) ? "" : ownerTeamId.ToUpperInvariant();
        bar.fillColor = key.Contains("RED") ? LitRed : key.Contains("BLUE") ? LitBlue : GoldColor;
        SetMatColor(bar.fillMat, bar.fillColor);
    }

    static void SetFill(GuardBar bar, float fraction)
    {
        if (bar.fillAnchor != null)
            bar.fillAnchor.localScale = new Vector3(FillWidth * Mathf.Clamp01(fraction), FillHeight, 1f);
    }

    static void SetGhost(GuardBar bar, float fraction)
    {
        bar.currentGhost = Mathf.Clamp01(fraction);
        if (bar.ghostAnchor != null)
            bar.ghostAnchor.localScale = new Vector3(FillWidth * bar.currentGhost, FillHeight, 1f);
    }

    static void SetBarAlpha(GuardBar bar, float alpha)
    {
        SetMatColor(bar.backMat, FadeColor(BarBackColor, alpha));
        SetMatColor(bar.ghostMat, FadeColor(GhostWhite, alpha));
        SetMatColor(bar.fillMat, FadeColor(bar.fillColor, alpha));
        SetMatColor(bar.tickMat, FadeColor(TickDark, alpha));
    }

    void StartBarBreak(GuardBar bar)
    {
        if (bar == null || bar.dieStart >= 0f) return;
        bar.dieStart = Time.time;
        bar.ghostHoldStart = -1f;
        if (bar.root != null && !Disabled)
            SpawnBurst(bar.root.transform.position, bar.fillColor, Color.white, 14, 1.4f, 2.8f, 0.04f, 0.1f, 0.45f, 0.8f);
    }

    void RecycleBar(GuardBar bar)
    {
        if (bar == null || bar.root == null) return;
        bar.dieStart = -1f;
        bar.ghostHoldStart = -1f;
        bar.shakeUntil = 0f;
        bar.shakeStrength = 0f;
        if (bar.shakeNode != null)
        {
            bar.shakeNode.localPosition = Vector3.zero;
            bar.shakeNode.localScale = Vector3.one;
        }
        SetBarAlpha(bar, 1f);
        bar.root.SetActive(false);
        barPool.Add(bar);
    }

    // ====================================================================
    // 模块 2：对抗决斗条
    // ====================================================================

    // ---------- 对抗节奏：争夺期间自动放慢回放 ----------
    // 0.1 秒/回合时三拍亮牌 0.4 秒闪过根本看不见；START 时降速到 1 秒/回合，
    // END 后 2.5 秒恢复；若 END 缺失（跳转/坏数据），12 秒安全恢复。
    const float ContestSecondsPerRound = 1.0f;
    const float PacingRestoreDelay = 2.5f;
    const float PacingSafetyTimeout = 12f;
    float pacingOriginal = -1f;
    Coroutine pacingRestoreRoutine;

    void BeginContestPacing()
    {
        if (player == null) return;
        if (pacingOriginal < 0f) pacingOriginal = player.secondsPerRound;
        player.secondsPerRound = Mathf.Max(player.secondsPerRound, ContestSecondsPerRound);
        ScheduleContestPacingRestore(PacingSafetyTimeout);
    }

    void ScheduleContestPacingRestore(float delay)
    {
        if (pacingRestoreRoutine != null) StopCoroutine(pacingRestoreRoutine);
        pacingRestoreRoutine = StartCoroutine(RestorePacingAfter(delay));
    }

    IEnumerator RestorePacingAfter(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        if (player != null && pacingOriginal > 0f) player.secondsPerRound = pacingOriginal;
        pacingOriginal = -1f;
        pacingRestoreRoutine = null;
    }

    void HandleContestStart(LycheeReplayPayload payload)
    {
        BeginContestPacing();
        DestroyDuel();
        if (string.IsNullOrEmpty(payload.targetNodeId)) return;
        if (!player.TryGetStationPosition(payload.targetNodeId, out Vector3 position, out _)) return;
        EnsureSharedAssets();
        if (quadMesh == null) return;

        DuelDisplay display = new DuelDisplay { nodeId = payload.targetNodeId };
        display.root = new GameObject("ContestDuel_" + payload.targetNodeId);
        display.root.transform.SetParent(fxRoot, false);
        display.root.transform.position = position + Vector3.up * DuelHover;
        display.root.AddComponent<LycheeReplayBillboard>();
        display.panel = new GameObject("Panel").transform;
        display.panel.SetParent(display.root.transform, false);

        display.backMat = CreateDuelMaterial(display, "lychee_duel_back", DuelBackColor, 3000);
        CreateQuad(display.panel, "Back", display.backMat, Vector3.zero, new Vector3(2.7f, 0.3f, 1f));

        display.outlineMat = CreateDuelMaterial(
            display, "lychee_duel_outline", new Color(GoldColor.r, GoldColor.g, GoldColor.b, 0f), 2999);
        Renderer outline = CreateQuad(
            display.panel, "WinnerOutline", display.outlineMat,
            new Vector3(0f, 0f, 0.005f), new Vector3(1.28f, 0.38f, 1f));
        if (outline != null)
        {
            outline.enabled = false;
            display.outlineRenderer = outline;
            display.outline = outline.transform;
        }

        display.redSide = new GameObject("RedSide").transform;
        display.redSide.SetParent(display.panel, false);
        display.blueSide = new GameObject("BlueSide").transform;
        display.blueSide.SetParent(display.panel, false);
        for (int i = 0; i < DuelSegmentCount; i++)
        {
            // 左红右蓝相向：红从左外侧向中心、蓝从右外侧向中心依次点亮
            display.redMats[i] = CreateDuelMaterial(display, "lychee_duel_red_" + i, DimRed, 3002);
            CreateQuad(
                display.redSide, "RedSeg_" + i, display.redMats[i],
                new Vector3(-1.19f + 0.37f * i, 0f, -0.004f), new Vector3(0.34f, 0.2f, 1f));
            display.blueMats[i] = CreateDuelMaterial(display, "lychee_duel_blue_" + i, DimBlue, 3002);
            CreateQuad(
                display.blueSide, "BlueSeg_" + i, display.blueMats[i],
                new Vector3(1.19f - 0.37f * i, 0f, -0.004f), new Vector3(0.34f, 0.2f, 1f));
        }
        display.vs = CreateText(display.panel, "VsLabel", "VS", GoldColor, 42, 0.08f, new Vector3(0f, 0f, -0.02f));
        duel = display;
    }

    void HandleCardReveal(LycheeReplayPayload payload)
    {
        int winnerCamp = ParseWinnerCamp(payload.winner);
        if (duel != null && duel.root != null && duel.endStart < 0f)
        {
            LightSegments(duel, true, Mathf.Clamp(payload.redPoint, 0, DuelSegmentCount), winnerCamp == 0);
            LightSegments(duel, false, Mathf.Clamp(payload.bluePoint, 0, DuelSegmentCount), winnerCamp == 1);
            // 中央迸发金白火花（18 粒短爆发）
            SpawnBurst(duel.root.transform.position, Color.white, SparkGold, 18, 2.2f, 3.6f, 0.05f, 0.12f, 0.5f, 0.35f);
            if (winnerCamp == 0) duel.blueShakeUntil = Time.time + SideShakeSeconds;
            else if (winnerCamp == 1) duel.redShakeUntil = Time.time + SideShakeSeconds;
        }

        // 角色演出：双方 Attack，败方追加 Hit
        bool hasRed = TryGetCampPlayerId(0, out int redId);
        bool hasBlue = TryGetCampPlayerId(1, out int blueId);
        if (hasRed) LycheeCharacterMotion.PlayAction(redId, LycheeCharacterActionKind.Attack, 1.2f);
        if (hasBlue) LycheeCharacterMotion.PlayAction(blueId, LycheeCharacterActionKind.Attack, 1.2f);
        if (winnerCamp == 0 && hasBlue) LycheeCharacterMotion.PlayAction(blueId, LycheeCharacterActionKind.Hit, 0.6f);
        else if (winnerCamp == 1 && hasRed) LycheeCharacterMotion.PlayAction(redId, LycheeCharacterActionKind.Hit, 0.6f);

        // 献贡出牌：对应车队头顶飘字
        if (payload.redCard == "XIAN_GONG" && hasRed)
            SpawnConvoyFloat(redId, "献贡 -1 好果", AmberText, 32, 0.058f);
        if (payload.blueCard == "XIAN_GONG" && hasBlue)
            SpawnConvoyFloat(blueId, "献贡 -1 好果", AmberText, 32, 0.058f);
    }

    void HandleContestEnd(LycheeReplayPayload payload)
    {
        if (pacingOriginal > 0f) ScheduleContestPacingRestore(PacingRestoreDelay);
        // 兜底：直接 END 没 START 时 duel 为空，无残留即清理完成
        if (duel == null || duel.root == null)
        {
            DestroyDuel();
            return;
        }
        if (duel.endStart >= 0f) return;
        int winnerCamp = ParseWinnerCamp(payload.winner);
        duel.flashes.Clear();
        duel.endStart = Time.time;
        duel.endWinnerCamp = winnerCamp;
        if (winnerCamp != 0) GrayOutSide(duel, true);
        if (winnerCamp != 1) GrayOutSide(duel, false);
        if (winnerCamp >= 0 && duel.outline != null)
            duel.outline.localPosition = new Vector3(winnerCamp == 0 ? -0.82f : 0.82f, 0f, 0.005f);
    }

    void LightSegments(DuelDisplay display, bool redSide, int target, bool flashNew)
    {
        Material[] mats = redSide ? display.redMats : display.blueMats;
        int lit = redSide ? display.redLit : display.blueLit;
        Color litColor = redSide ? LitRed : LitBlue;
        for (int i = lit; i < target && i < DuelSegmentCount; i++)
        {
            if (mats[i] == null) continue;
            if (flashNew)
            {
                SetMatColor(mats[i], Color.white);
                display.flashes.Add(new SegFlash { mat = mats[i], target = litColor, start = Time.time });
            }
            else
            {
                SetMatColor(mats[i], litColor);
            }
        }
        if (redSide) display.redLit = Mathf.Max(display.redLit, target);
        else display.blueLit = Mathf.Max(display.blueLit, target);
    }

    static void GrayOutSide(DuelDisplay display, bool redSide)
    {
        Material[] mats = redSide ? display.redMats : display.blueMats;
        int lit = redSide ? display.redLit : display.blueLit;
        for (int i = 0; i < DuelSegmentCount; i++)
            SetMatColor(mats[i], i < lit ? LitGray : DimGray);
    }

    void UpdateDuel(float now)
    {
        if (duel == null) return;
        if (duel.root == null)
        {
            duel = null;
            return;
        }

        // 段位发光闪：白 → 队色 0.5s
        for (int i = duel.flashes.Count - 1; i >= 0; i--)
        {
            SegFlash flash = duel.flashes[i];
            float t = (now - flash.start) / SegFlashSeconds;
            if (t >= 1f)
            {
                SetMatColor(flash.mat, flash.target);
                duel.flashes.RemoveAt(i);
            }
            else
            {
                SetMatColor(flash.mat, Color.Lerp(Color.white, flash.target, Mathf.Clamp01(t)));
            }
        }

        ApplySideShake(duel.redSide, duel.redShakeUntil, now, 11.3f);
        ApplySideShake(duel.blueSide, duel.blueShakeUntil, now, 27.9f);

        if (duel.endStart < 0f) return;
        float elapsed = now - duel.endStart;

        // 胜方金色描边脉冲 1s
        if (duel.outlineRenderer != null)
        {
            if (duel.endWinnerCamp >= 0 && elapsed <= OutlinePulseSeconds)
            {
                duel.outlineRenderer.enabled = true;
                float pulse = 0.5f + 0.5f * Mathf.Sin(elapsed * Mathf.PI * 4f);
                float strength = Mathf.Max(1f - elapsed / OutlinePulseSeconds, 0.35f);
                SetMatColor(duel.outlineMat,
                    new Color(GoldColor.r, GoldColor.g, GoldColor.b, (0.2f + 0.6f * pulse) * strength));
            }
            else if (duel.outlineRenderer.enabled && elapsed > OutlinePulseSeconds)
            {
                duel.outlineRenderer.enabled = false;
            }
        }

        // 1.8s 后整体淡出销毁
        if (elapsed < DuelLingerSeconds) return;
        if (!duel.fadeCaptured)
        {
            duel.fadeCaptured = true;
            duel.fadeBase.Clear();
            foreach (Material material in duel.ownedMaterials)
                duel.fadeBase.Add(GetMatColor(material));
            duel.vsBase = duel.vs != null ? duel.vs.color : Color.white;
        }
        float fade = 1f - Mathf.Clamp01((elapsed - DuelLingerSeconds) / DuelFadeSeconds);
        for (int i = 0; i < duel.ownedMaterials.Count && i < duel.fadeBase.Count; i++)
        {
            Material material = duel.ownedMaterials[i];
            if (material == null) continue;
            Color baseColor = duel.fadeBase[i];
            SetMatColor(material, new Color(baseColor.r, baseColor.g, baseColor.b, baseColor.a * fade));
        }
        if (duel.vs != null)
            duel.vs.color = new Color(duel.vsBase.r, duel.vsBase.g, duel.vsBase.b, duel.vsBase.a * fade);
        if (fade <= 0f) DestroyDuel();
    }

    static void ApplySideShake(Transform side, float until, float now, float seed)
    {
        if (side == null) return;
        if (now >= until)
        {
            if (side.localPosition != Vector3.zero) side.localPosition = Vector3.zero;
            return;
        }
        float remain = Mathf.Clamp01((until - now) / SideShakeSeconds);
        float offset = (Mathf.PerlinNoise(now * 22f, seed) - 0.5f) * 2f * 0.09f * remain;
        side.localPosition = new Vector3(offset, 0f, 0f);
    }

    void DestroyDuel()
    {
        if (duel == null) return;
        if (duel.root != null) Destroy(duel.root);
        foreach (Material material in duel.ownedMaterials)
        {
            if (material != null) Destroy(material);
        }
        duel = null;
    }

    Material CreateDuelMaterial(DuelDisplay display, string materialName, Color color, int renderQueue)
    {
        Material material = CreateRuntimeMaterial(materialName, color, renderQueue, false);
        if (material != null && display != null) display.ownedMaterials.Add(material);
        return material;
    }

    static int ParseWinnerCamp(string winner)
    {
        if (string.IsNullOrEmpty(winner)) return -1;
        string key = winner.ToUpperInvariant();
        if (key.Contains("RED")) return 0;
        if (key.Contains("BLUE")) return 1;
        return -1;
    }

    // ====================================================================
    // 模块 3：飘字伤害
    // ====================================================================

    void HandleGuardBreak(LycheeReplayPayload payload)
    {
        string nodeId = !string.IsNullOrEmpty(payload.targetNodeId) ? payload.targetNodeId : payload.nodeId;
        if (string.IsNullOrEmpty(nodeId)) return;
        if (!player.TryGetStationPosition(nodeId, out Vector3 position, out _)) return;

        int damage = payload.attackValue > 0
            ? payload.attackValue
            : Mathf.Max(1, payload.defenseBefore - payload.defenseAfter);
        SpawnFloat("-" + damage, position + Vector3.up * (NodeFloatHeight + 0.15f), DamageRed, 48, 0.085f);
        if (payload.defenseBefore != payload.defenseAfter)
            SpawnFloat(
                "防御 " + payload.defenseBefore + " → " + payload.defenseAfter,
                position + Vector3.up * (NodeFloatHeight - 0.35f),
                GrayText, 26, 0.05f);

        // 模块 4：受击反馈 —— 木屑粒子爆 + 血条强抖动
        SpawnBurst(position + Vector3.up * 0.55f, WoodChipA, WoodChipB, 24, 2f, 4f, 0.06f, 0.14f, 0.5f, 1.1f);
        if (bars.TryGetValue(nodeId, out GuardBar bar) && bar != null && bar.dieStart < 0f)
        {
            bar.shakeUntil = Time.time + 0.5f;
            bar.shakeStrength = 0.12f;
        }
    }

    void CheckFreshnessDrops(LycheeReplayFrame previous, LycheeReplayFrame current)
    {
        if (previous.players == null || current.players == null) return;
        foreach (LycheeReplayPlayerState state in current.players)
        {
            if (state == null) continue;
            LycheeReplayPlayerState before = null;
            foreach (LycheeReplayPlayerState candidate in previous.players)
            {
                if (candidate != null && candidate.playerId == state.playerId)
                {
                    before = candidate;
                    break;
                }
            }
            if (before == null) continue;
            int prevTier = Mathf.FloorToInt(before.freshness / 10f);
            int curTier = Mathf.FloorToInt(state.freshness / 10f);
            int drop = prevTier - curTier;
            if (drop < 1) continue;
            string text = drop > 1 ? "-" + drop + " 好果" : "-1 好果";
            SpawnConvoyFloat(state.playerId, text, AmberText, 32, 0.058f);
        }
    }

    void SpawnNodeFloat(LycheeReplayPayload payload, string content, Color color, int fontSize, float charSize)
    {
        string nodeId = !string.IsNullOrEmpty(payload.targetNodeId) ? payload.targetNodeId : payload.nodeId;
        if (string.IsNullOrEmpty(nodeId)) return;
        if (!player.TryGetStationPosition(nodeId, out Vector3 position, out _)) return;
        SpawnFloat(content, position + Vector3.up * NodeFloatHeight, color, fontSize, charSize);
    }

    void SpawnConvoyFloat(int playerId, string content, Color color, int fontSize, float charSize)
    {
        Transform convoy = player == null ? null : player.GetConvoyRoot(playerId);
        if (convoy == null) return;
        SpawnFloat(content, convoy.position + Vector3.up * ConvoyFloatHeight, color, fontSize, charSize);
    }

    void SpawnFloat(string content, Vector3 origin, Color color, int fontSize, float charSize)
    {
        if (Disabled || fxRoot == null || string.IsNullOrEmpty(content)) return;
        EnsureFloatPool();
        FloatText item = null;
        foreach (FloatText candidate in floats)
        {
            if (candidate != null && !candidate.active && candidate.go != null)
            {
                item = candidate;
                break;
            }
        }
        if (item == null && floats.Count < FloatPoolMax) item = CreateFloat();
        if (item == null)
        {
            // 池满：抢占最旧的一条
            float oldest = float.MaxValue;
            foreach (FloatText candidate in floats)
            {
                if (candidate == null || candidate.go == null) continue;
                if (candidate.start < oldest)
                {
                    oldest = candidate.start;
                    item = candidate;
                }
            }
        }
        if (item == null || item.go == null || item.text == null) return;

        floatSeq++;
        // 用 PerlinNoise + 实例序号生成 ±0.3 水平偏移（不使用 Random）
        item.ox = (Mathf.PerlinNoise(floatSeq * 0.731f, 0.137f) - 0.5f) * 0.6f;
        item.oz = (Mathf.PerlinNoise(0.589f, floatSeq * 0.547f) - 0.5f) * 0.6f;
        item.origin = origin;
        item.start = Time.time;
        item.duration = FloatLifeSeconds;
        item.baseColor = color;
        item.active = true;
        item.text.text = content;
        item.text.fontSize = fontSize;
        item.text.characterSize = charSize;
        item.text.color = color;
        item.go.transform.position = origin + new Vector3(item.ox, 0f, item.oz);
        item.go.SetActive(true);
    }

    void UpdateFloats(float now)
    {
        foreach (FloatText item in floats)
        {
            if (item == null || !item.active) continue;
            if (item.go == null || item.text == null)
            {
                item.active = false;
                continue;
            }
            float t = now - item.start;
            if (t >= item.duration)
            {
                item.active = false;
                item.go.SetActive(false);
                continue;
            }
            item.go.transform.position = item.origin + new Vector3(item.ox, RiseSpeed * t, item.oz);
            float alpha = Mathf.Clamp01(1f - t / item.duration);
            Color color = item.baseColor;
            item.text.color = new Color(color.r, color.g, color.b, color.a * alpha);
        }
    }

    void EnsureFloatPool()
    {
        if (fxRoot == null) return;
        while (floats.Count < FloatPoolSize)
        {
            if (CreateFloat() == null) break;
        }
    }

    FloatText CreateFloat()
    {
        if (fxRoot == null) return null;
        GameObject go = new GameObject("DamageFloat_" + floats.Count);
        go.transform.SetParent(fxRoot, false);
        go.AddComponent<LycheeReplayBillboard>();
        TextMesh text = go.AddComponent<TextMesh>();
        text.anchor = TextAnchor.MiddleCenter;
        text.alignment = TextAlignment.Center;
        go.AddComponent<LycheeChineseLabel>();
        go.SetActive(false);
        FloatText item = new FloatText { go = go, text = text };
        floats.Add(item);
        return item;
    }

    void RecycleAllFloats()
    {
        foreach (FloatText item in floats)
        {
            if (item == null) continue;
            item.active = false;
            if (item.go != null) item.go.SetActive(false);
        }
    }

    static string DefenseDeltaText(LycheeReplayPayload payload, int fallback)
    {
        int diff = payload.defenseAfter - payload.defenseBefore;
        if (diff == 0) diff = fallback;
        return diff > 0 ? "防御 +" + diff : "防御 " + diff;
    }

    bool TryGetCampPlayerId(int camp, out int playerId)
    {
        playerId = 0;
        LycheeReplayFrame frame = player == null ? null : player.CurrentFrame;
        if (frame == null || frame.players == null) return false;
        foreach (LycheeReplayPlayerState state in frame.players)
        {
            if (state != null && state.camp == camp)
            {
                playerId = state.playerId;
                return true;
            }
        }
        return false;
    }

    // ====================================================================
    // 共享资产与程序化构件
    // ====================================================================

    void EnsureSharedAssets()
    {
        if (sharedAssetsReady) return;
        sharedAssetsReady = true;
        GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Quad);
        MeshFilter tempFilter = temp.GetComponent<MeshFilter>();
        if (tempFilter != null) quadMesh = tempFilter.sharedMesh; // 引擎内置共享网格，不需要销毁
        Destroy(temp);

        Texture2D texture = CreateSoftCircleTexture(64);
        generatedAssets.Add(texture);
        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        if (shader != null)
        {
            particleMaterial = new Material(shader) { name = "lychee_combat_particle" };
            ConfigureTransparentMaterial(particleMaterial);
            if (particleMaterial.HasProperty("_BaseMap")) particleMaterial.SetTexture("_BaseMap", texture);
            else if (particleMaterial.HasProperty("_MainTex")) particleMaterial.SetTexture("_MainTex", texture);
            generatedAssets.Add(particleMaterial);
        }
    }

    Material CreateRuntimeMaterial(string materialName, Color color, int renderQueue, bool trackGlobal)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        if (shader == null) return null;
        Material material = new Material(shader) { name = materialName };
        ConfigureTransparentMaterial(material);
        material.renderQueue = renderQueue;
        SetMatColor(material, color);
        if (trackGlobal) generatedAssets.Add(material);
        return material;
    }

    Renderer CreateQuad(Transform parent, string objectName, Material material, Vector3 localPosition, Vector3 localScale)
    {
        if (parent == null || quadMesh == null || material == null) return null;
        GameObject go = new GameObject(objectName);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        go.transform.localScale = localScale;
        MeshFilter filter = go.AddComponent<MeshFilter>();
        filter.sharedMesh = quadMesh;
        MeshRenderer renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return renderer;
    }

    static TextMesh CreateText(
        Transform parent, string objectName, string content, Color color, int fontSize, float charSize, Vector3 localPosition)
    {
        if (parent == null) return null;
        GameObject go = new GameObject(objectName);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        TextMesh text = go.AddComponent<TextMesh>();
        text.text = content;
        text.anchor = TextAnchor.MiddleCenter;
        text.alignment = TextAlignment.Center;
        text.fontSize = fontSize;
        text.characterSize = charSize;
        text.color = color;
        go.AddComponent<LycheeChineseLabel>();
        return text;
    }

    void SpawnBurst(
        Vector3 position, Color colorA, Color colorB, int count,
        float speedMin, float speedMax, float sizeMin, float sizeMax, float life, float gravity)
    {
        if (fxRoot == null || Disabled) return;
        EnsureSharedAssets();
        GameObject go = new GameObject("BurstFx");
        go.transform.SetParent(fxRoot, false);
        go.transform.position = position;
        ParticleSystem particles = go.AddComponent<ParticleSystem>();
        particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        ParticleSystem.MainModule main = particles.main;
        main.duration = life;
        main.loop = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.5f, life);
        main.startSpeed = new ParticleSystem.MinMaxCurve(speedMin, speedMax);
        main.startSize = new ParticleSystem.MinMaxCurve(sizeMin, sizeMax);
        main.startColor = new ParticleSystem.MinMaxGradient(colorA, colorB);
        main.gravityModifier = gravity;
        main.maxParticles = Mathf.Max(8, count + 4);

        ParticleSystem.EmissionModule emission = particles.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, count) });

        ParticleSystem.ShapeModule shape = particles.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.18f;

        ParticleSystem.ColorOverLifetimeModule colorOverLifetime = particles.colorOverLifetime;
        colorOverLifetime.enabled = true;
        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(Color.white, 0f),
                new GradientColorKey(Color.white, 1f)
            },
            new[]
            {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(1f, 0.55f),
                new GradientAlphaKey(0f, 1f)
            });
        colorOverLifetime.color = gradient;

        ParticleSystemRenderer renderer = particles.GetComponent<ParticleSystemRenderer>();
        if (renderer != null)
        {
            if (particleMaterial != null) renderer.sharedMaterial = particleMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
        particles.Play();
        Destroy(go, life + 0.8f);
    }

    static void SetMatColor(Material material, Color color)
    {
        if (material == null) return;
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        else if (material.HasProperty("_Color")) material.SetColor("_Color", color);
    }

    static Color GetMatColor(Material material)
    {
        if (material == null) return Color.clear;
        if (material.HasProperty("_BaseColor")) return material.GetColor("_BaseColor");
        if (material.HasProperty("_Color")) return material.GetColor("_Color");
        return Color.white;
    }

    static Color FadeColor(Color color, float alpha)
    {
        return new Color(color.r, color.g, color.b, color.a * alpha);
    }

    static void ConfigureTransparentMaterial(Material material)
    {
        if (material == null) return;
        if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
        if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f);
        if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
        material.SetOverrideTag("RenderType", "Transparent");
        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.renderQueue = (int)RenderQueue.Transparent;
    }

    /// <summary>程序化柔边圆点贴图（粒子兜底贴图）。</summary>
    static Texture2D CreateSoftCircleTexture(int size)
    {
        size = Mathf.Max(8, size);
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "LycheeCombatSoftCircle",
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
                float alpha = Mathf.Clamp01(1f - distance);
                alpha = alpha * alpha * (3f - 2f * alpha);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
            }
        }
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        return texture;
    }
}
