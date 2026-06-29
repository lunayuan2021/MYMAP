using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class LycheeReplayPlayer : MonoBehaviour
{
    [Header("Replay")]
    public TextAsset replayFile;
    /// <summary>本场景要加载的 StreamingAssets 相对路径（默认 replay.txt）。
    /// 锦标赛分场景方案下，地图生成器给每张图的场景写各自的回放（如 "Tournament/map2.txt"），
    /// 这样切场景即换回放、零运行时耦合。向后兼容：旧场景在此字段加入前已序列化，反序列化时为 null/空，
    /// ReplayRelative 会自动回退到 replay.txt。</summary>
    public string streamingReplayPath = "replay.txt";
    // 1 秒/回合：600 回合约 10 分钟。不要再按“静止回合/处理回合”压缩，否则观赛节奏会失真。
    [Range(0.03f, 2f)] public float secondsPerRound = 1.00f;
    public bool playOnStart = true;
    public bool loop;

    [Header("Models")]
    public GameObject carriageModel;
    public GameObject boatModel;
    /// <summary>马模型（Tripo3D Horse.glb，由 LycheeHorseHookup_20260613 编辑器脚本赋值）：
    /// 为空时"马拉车"特性整体静默不存在；消费方在 LycheeCharacterMotion，本类不读不实例化。</summary>
    public GameObject horseModel;
    /// <summary>车体模型整体偏航修正（度）：AI 导出的组合马车正面朝向不一定符合 +Z 约定，由装配脚本按实测设置。</summary>
    [Range(-180f, 180f)] public float carriageYawOffset;
    /// <summary>马行走 AnimationClip（Horse.glb 子资产，可空）：空则 LycheeCharacterMotion 程序化 bob 兜底。</summary>
    public AnimationClip horseWalkClip;
    public GameObject captainModel;
    public GameObject squadMemberModel;
    public GameObject roadObstacleModel;
    /// <summary>红/蓝队将军模型（取代队长 figure，由 LycheeGeneralHookup_20260613 赋值，带 walking 子动画）：
    /// 红队将军走在车队最前领队、蓝队将军居中坐镇；某队为空时该队回退用 captainModel 占位。</summary>
    public GameObject redGeneralModel;
    public GameObject blueGeneralModel;
    /// <summary>将军行走 AnimationClip（各自 GLB 子资产，可空）：有则 Playables 播放，空则程序化兜底。</summary>
    public AnimationClip redGeneralWalkClip;
    public AnimationClip blueGeneralWalkClip;
    /// <summary>将军攻击 AnimationClip（RedGeneralAttack/BlueGeneralAttack.glb 子资产，可空）：对抗期间从走路切到挥剑。</summary>
    public AnimationClip redGeneralAttackClip;
    public AnimationClip blueGeneralAttackClip;

    [Header("Presentation")]
    public bool useRuntimeHud = true;
    public bool showLegacyTopHud;
    [Min(2)] public int defaultTeamSize = 16;
    // 回放数据不带每次派遣人数,默认按设计"清障消耗 2 名"出 2 人(原 4 人显得像一队、太多)。
    [Range(1, 12)] public int defaultDispatchMemberCount = 2;
    public Sprite redCaptainAvatar;
    public Sprite blueCaptainAvatar;
    public Sprite[] combatIllustrations;

    const float MinRoundSeconds = 0.01f;
    // WebGL 偶发掉帧时不要一次补很多视觉时间，否则车队会像 PPT 一格一格跳；观感优先，掉帧时宁可轻微慢放。
    const float MaxPlaybackDeltaSeconds = 1f / 30f;
    // 窗口争夺逐拍：对抗期间每回合至少 hold 这么久，让出牌(每拍 1 回合、原 0.2s 一闪而过)各自看清。
    const float ContestRoundSeconds = 2.6f;
    const float EventPulseClusterRadius = 2.4f;
    const float EventPulseSlotSpacing = 2.15f;
    float contestSlowUntil = -999f;   // 最近一次对抗事件起算的减速截止时刻(unscaledTime)；每个对抗事件续期。

    LycheeReplayFrame startFrame;
    readonly List<LycheeReplayFrame> frames = new List<LycheeReplayFrame>();
    int declaredTotalRounds;
    readonly Dictionary<int, ConvoyVisual> convoys = new Dictionary<int, ConvoyVisual>();
    readonly Dictionary<int, int> teamRosterSizes = new Dictionary<int, int>();
    readonly Dictionary<string, MapStationAnchor> stations = new Dictionary<string, MapStationAnchor>();
    readonly Dictionary<string, MapRouteEdge> edges = new Dictionary<string, MapRouteEdge>();
    readonly Dictionary<string, GameObject> obstacles = new Dictionary<string, GameObject>();
    readonly Dictionary<string, GameObject> obstacleGenericModels = new Dictionary<string, GameObject>();
    readonly Dictionary<string, TextMesh> obstacleLabels = new Dictionary<string, TextMesh>();
    readonly Dictionary<string, GameObject> obstacleRockPiles = new Dictionary<string, GameObject>();
    readonly Dictionary<string, GameObject> obstacleBrokenBridges = new Dictionary<string, GameObject>();
    readonly Dictionary<string, GameObject> obstacleDockBlocks = new Dictionary<string, GameObject>();
    readonly Dictionary<string, string> activeObstacleTypes = new Dictionary<string, string>();
    readonly Dictionary<string, bool> obstacleWasActive = new Dictionary<string, bool>();
    readonly Dictionary<string, float> obstacleCueCooldown = new Dictionary<string, float>();
    readonly List<SquadVisual> squads = new List<SquadVisual>();
    readonly List<GameObject> eventEffects = new List<GameObject>();
    readonly Dictionary<GameObject, Vector3> eventEffectBasePositions = new Dictionary<GameObject, Vector3>();
    readonly List<GameObject> removalEventBuffer = new List<GameObject>();
    readonly Dictionary<string, int> processCardRoundByKey = new Dictionary<string, int>();

    MapElevationData elevation;
    Transform dynamicRoot;
    Transform eventRoot;
    int frameIndex;
    int lastEventFrame = -1;
    float frameTime;
    bool playing;
    // 开局首帧的 SQUAD_DISPATCH 延迟到出发动画结束再生成：否则在 Awake 的 ApplyFrame(0,true) 里就凭空冒出小队，
    // 早于 LycheeOpeningShowcase 暂停播放（其暂停在 Start 协程 yield 之后才执行），表现为"出发动画前小队凭空派发、走着走着消失"。
    bool applyingInitialFrame;
    bool applyingForcedFrame;
    bool openingDispatchesReleased;
    int pendingOpeningRound;
    readonly List<LycheeReplayMessage> pendingOpeningDispatches = new List<LycheeReplayMessage>();
    LycheeReplayFrame lastAppliedFrame;
    bool matchOverRaised;
    string latestEvent = "就绪";
    Vector3 eventFocusPosition;
    Transform eventFocusTarget;
    float eventFocusUntil;
    /// <summary>当前事件聚焦的建议相机距离（米）；&lt;0 表示未指定、由相机自行决定。对抗类拉近（15）、普通事件保持远景（30）。</summary>
    float eventFocusDistance = -1f;
    readonly List<EventFocusAnchor> recentEventFocuses = new List<EventFocusAnchor>(8);
    /// <summary>WINDOW_CONTEST_START 缓存的对抗节点位置：WINDOW_CARD_REVEAL 的 payload 没有 targetNodeId，靠它续期聚焦让整场对抗镜头不回退。</summary>
    Vector3 contestFocusPosition;
    bool hasContestFocus;
    GUIStyle hudStyle;
    GUIStyle titleStyle;
    GUIStyle buttonStyle;
    GUIStyle startupErrorStyle;
    string startupError;
    string replayLoadDiagnostics;
    LycheeReplayRuntimeHud runtimeHud;
    Material rockMaterial;
    Material woodMaterial;
    Material warningMaterial;
    Material cargoMaterial;
    Material ropeMaterial;

    sealed class ConvoyVisual
    {
        public GameObject root;
        public GameObject carriage;
        public GameObject boat;
        public GameObject boatFlag;   // 水路时船上的红蓝队旗（懒创建）
        public GameObject captain;
        public readonly List<GameObject> members = new List<GameObject>();
        public TextMesh label;
        public Vector3 smoothedPosition;
        public Vector3 positionVelocity;
        public Vector3 lastTangent = Vector3.forward;
        public float bobTime;
        public bool initialized;
    }

    sealed class SquadVisual
    {
        public GameObject root;
        public Vector3 start;
        public Vector3 end;
        public int startRound;
        public int endRound;
        public int playerId;
        public int memberCount;
        public string orderId;
        /// <summary>攻坚目标站 nodeId（抵达时请求建筑透视，让攻击动作不被楼挡）。</summary>
        public string targetNodeId;
        public string tactic;
        /// <summary>抵达后的真实时间戳；&lt;0 表示仍在行军。抵达后停留做攻击演出再销毁。</summary>
        public float arrivedRealTime = -1f;
        public float holdSeconds = 3.2f;
        public bool resultResolved;
        public bool resultSuccess;
        // 结算后向节点平滑冲刺（替代旧的瞬移闪现）：记录冲刺起点/起时/时长
        public bool dashing;
        public Vector3 dashFrom;
        public float dashStartTime;
        public float dashElapsed;
        public float dashSeconds;
        public float arrivedElapsed;
        public float animTime;
        public TextMesh label;
        /// <summary>跨河时垫在脚下的小船（懒创建，仅水面段激活）。</summary>
        public GameObject boat;
        /// <summary>与 LycheeCharacterMotion 共享的水面状态（船上站立 vs 陆上行军）。</summary>
        public LycheeSquadWaterState waterState;
    }

    sealed class EventFocusAnchor
    {
        public Transform target;
        public Vector3 position;
        public float until;
        public float focusDistance;
    }

    // 河面三角形缓存（世界空间）：小队直线跨图时判断脚下是否为水面。
    // 来源是场景里的河面容器对象，启动后首个小队出发时懒构建一次。
    readonly List<Vector3> waterTriangleA = new List<Vector3>();
    readonly List<Vector3> waterTriangleB = new List<Vector3>();
    readonly List<Vector3> waterTriangleC = new List<Vector3>();
    readonly List<Vector4> waterTriangleBox = new List<Vector4>(); // xMin,zMin,xMax,zMax
    bool waterTrianglesBuilt;
    static readonly string[] WaterContainerNames = { "WaterRoute_And_River", "Water_Junction_Patches_20260611O" };

    /// <summary>每当 HandleEvent 处理一条回放事件时触发（message, round）。订阅方自行管理生命周期。</summary>
    public static event Action<LycheeReplayMessage, int> ReplayEventRaised;
    /// <summary>每次 ApplyFrame 应用一帧后触发（上一次应用的帧或 null, 当前帧）。</summary>
    public static event Action<LycheeReplayFrame, LycheeReplayFrame> RoundApplied;
    /// <summary>播放自然到达最后一帧并停止时触发一次（OverFrame 可能为 null）。SeekFrame 跳回更早帧后可再次触发。</summary>
    public static event Action<LycheeReplayOverFrame> MatchOverRaised;

    public int CurrentRound => frames.Count == 0 ? 0 : frames[frameIndex].round;
    public bool IsPlaying => playing;
    public bool IsApplyingForcedFrame => applyingForcedFrame;
    public LycheeReplayFrame CurrentFrame => frames.Count == 0 ? null : frames[frameIndex];
    public LycheeReplayFrame NextFrame => frames.Count == 0 ? null : frames[Mathf.Min(frameIndex + 1, frames.Count - 1)];
    public LycheeReplayFrame StartFrameData => startFrame;
    public LycheeReplayOverFrame OverFrame { get; private set; }
    /// <summary>实际加载到的回放的 start 行原文（含 ?path= 外部回放）；供开场字幕等取 matchId/rulesVersion，
    /// 避免 WebGL 上用 System.IO 读包内 replay.txt 必失败、且无视 ?path= 的问题。</summary>
    public string StartLineRaw { get; private set; }
    public string ReplaySource { get; private set; }
    // 时长 = 真实回合数（1 回合 = 1 秒）。不再 Max 上 declaredTotalRounds(=durationRound 上限，如 600)，
    // 否则胜负已定、真实帧只到第 503 回合时，进度条/HUD 仍按 600 计，造成“为凑 10 分钟拉长”的错觉。
    public int TotalRounds => frames.Count == 0 ? 0 : frames[frames.Count - 1].round;
    public float FrameInterpolation => Mathf.Clamp01(frameTime / EffectiveSecondsForFrame(CurrentFrame));

    public bool TryGetStationPosition(string nodeId, out Vector3 position, out string displayName)
    {
        if (!string.IsNullOrEmpty(nodeId) &&
            stations.TryGetValue(nodeId, out MapStationAnchor anchor) &&
            anchor != null)
        {
            position = anchor.transform.position;
            displayName = string.IsNullOrEmpty(anchor.displayName) ? nodeId : anchor.displayName;
            return true;
        }
        position = Vector3.zero;
        displayName = nodeId;
        return false;
    }

    public bool TryGetStationAnchor(string nodeId, out MapStationAnchor anchor)
    {
        if (!string.IsNullOrEmpty(nodeId) &&
            stations.TryGetValue(nodeId, out anchor) &&
            anchor != null)
            return true;
        anchor = null;
        return false;
    }

    public bool TryGetEdge(string edgeId, out MapRouteEdge edge)
    {
        if (!string.IsNullOrEmpty(edgeId)) return edges.TryGetValue(edgeId, out edge);
        edge = null;
        return false;
    }

    public Transform GetConvoyRoot(int playerId)
    {
        return convoys.TryGetValue(playerId, out ConvoyVisual visual) && visual != null && visual.root != null
            ? visual.root.transform
            : null;
    }

    /// <summary>收集当前有可视化车队的真实 playerId（回放里是 1002/2002 这类四位数，并非 0..15）。供调测叠层用。</summary>
    public void CollectConvoyPlayerIds(List<int> buffer)
    {
        if (buffer == null) return;
        buffer.Clear();
        foreach (KeyValuePair<int, ConvoyVisual> kv in convoys)
            if (kv.Value != null && kv.Value.root != null) buffer.Add(kv.Key);
    }

    /// <summary>收集当前在途小分队的（世界坐标, 队名·战术标签）。供调测叠层在脑袋顶贴坐标。</summary>
    public void CollectSquadTags(List<KeyValuePair<Vector3, string>> buffer)
    {
        if (buffer == null) return;
        buffer.Clear();
        foreach (SquadVisual squad in squads)
            if (squad != null && squad.root != null)
                buffer.Add(new KeyValuePair<Vector3, string>(
                    squad.root.transform.position, TeamName(squad.playerId) + "·小队"));
    }

    public string TeamDisplayName(int playerId)
    {
        return TeamName(playerId);
    }

    public Color TeamColorOf(int playerId)
    {
        return TeamColor(playerId);
    }

    public void SeekToRound(int round)
    {
        if (frames.Count == 0) return;
        int index = frames.FindIndex(frame => frame.round >= round);
        SeekFrame(index < 0 ? frames.Count - 1 : index);
    }

    public void SetPlaying(bool value)
    {
        playing = value;
    }

    public bool TryGetConvoyFraming(out Vector3 center, out float span)
    {
        center = Vector3.zero;
        span = 0f;

        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        bool any = false;
        IncludeConvoysAndSquads(ref min, ref max, ref any);
        if (!any) return false;
        center = (min + max) * 0.5f;
        span = Mathf.Max(max.x - min.x, max.z - min.z);
        return true;
    }

    // 取景缓冲（避免每帧分配）
    readonly List<Vector3> framingConvoyPts = new List<Vector3>(8);
    readonly List<Vector3> framingActionPts = new List<Vector3>(16);

    // “刚好框住当前有状态的地方”：以当前动作（最近事件点 → 小队 → 车队中心）为主锚点，
    // 只把锚点附近 ClusterRadius 内的有状态对象算进 bounds，丢弃散落全图的远处事件/小队，
    // 再对跨度硬封顶。这样多事件/兵分两路时也绝不退到全地图，更不会把镜头拉到能看见地图边界。
    const float FramingClusterRadius = 22f; // 锚点附近多大范围内的对象才纳入取景
    const float FramingMaxSpan = 30f;       // 取景跨度上限（远低于地图 ~73 宽 → 永不全图/不露边界）
    const float FramingMinSpan = 13f;       // 下限：单点也不至于贴脸

    public bool TryGetStrategicFraming(out Vector3 center, out float span, out bool actionFocusActive)
    {
        center = Vector3.zero;
        span = 0f;
        actionFocusActive = false;

        framingConvoyPts.Clear();
        framingActionPts.Clear();
        foreach (ConvoyVisual convoy in convoys.Values)
            if (convoy != null && convoy.root != null)
                framingConvoyPts.Add(convoy.root.transform.position);

        bool hasSquad = false;
        foreach (SquadVisual squad in squads)
        {
            if (squad == null || squad.root == null) continue;
            hasSquad = true;
            framingActionPts.Add(squad.root.transform.position);
        }

        bool hasEvent = false;
        Vector3 latestEvent = Vector3.zero;
        float now = Time.unscaledTime;
        for (int i = recentEventFocuses.Count - 1; i >= 0; i--)
        {
            EventFocusAnchor focus = recentEventFocuses[i];
            if (focus == null || now >= focus.until) { recentEventFocuses.RemoveAt(i); continue; }
            if (focus.target != null) focus.position = focus.target.position;
            framingActionPts.Add(focus.position);
            if (!hasEvent) { latestEvent = focus.position; hasEvent = true; } // 列表尾部=最新
        }

        if (framingConvoyPts.Count == 0 && framingActionPts.Count == 0) return false;

        // 主锚点：优先“当前事件点”，其次小队均值，再次车队中心。
        Vector3 anchor;
        if (hasEvent) anchor = latestEvent;
        else if (framingActionPts.Count > 0) anchor = Centroid(framingActionPts);
        else anchor = Centroid(framingConvoyPts);

        // 只纳入锚点附近的有状态对象（远处散点丢弃）。
        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        bool any = false;
        IncludeFramingPoint(anchor, ref min, ref max, ref any);
        IncludePointsNear(anchor, FramingClusterRadius, framingConvoyPts, ref min, ref max, ref any);
        IncludePointsNear(anchor, FramingClusterRadius, framingActionPts, ref min, ref max, ref any);

        actionFocusActive = hasEvent || hasSquad;
        center = (min + max) * 0.5f;
        span = Mathf.Max(max.x - min.x, max.z - min.z);

        // 跨度硬封顶：超出就以锚点为中心收紧，绝不为框住散点而退到全图。
        if (span > FramingMaxSpan) { center = anchor; span = FramingMaxSpan; }
        span = Mathf.Max(span, FramingMinSpan);
        return true;
    }

    static Vector3 Centroid(List<Vector3> pts)
    {
        if (pts == null || pts.Count == 0) return Vector3.zero;
        Vector3 sum = Vector3.zero;
        for (int i = 0; i < pts.Count; i++) sum += pts[i];
        return sum / pts.Count;
    }

    static void IncludePointsNear(Vector3 anchor, float radius, List<Vector3> pts,
        ref Vector3 min, ref Vector3 max, ref bool any)
    {
        float r2 = radius * radius;
        for (int i = 0; i < pts.Count; i++)
        {
            float dx = pts[i].x - anchor.x, dz = pts[i].z - anchor.z;
            if (dx * dx + dz * dz > r2) continue; // 远处散点不纳入取景
            IncludeFramingPoint(pts[i], ref min, ref max, ref any);
        }
    }

    bool IncludeConvoysAndSquads(ref Vector3 min, ref Vector3 max, ref bool any)
    {
        bool hasSquad = false;
        foreach (ConvoyVisual convoy in convoys.Values)
        {
            if (convoy == null || convoy.root == null) continue;
            IncludeFramingPoint(convoy.root.transform.position, ref min, ref max, ref any);
        }

        foreach (SquadVisual squad in squads)
        {
            if (squad == null || squad.root == null) continue;
            hasSquad = true;
            IncludeFramingPoint(squad.root.transform.position, ref min, ref max, ref any);
        }
        return hasSquad;
    }

    static void IncludeFramingPoint(Vector3 position, ref Vector3 min, ref Vector3 max, ref bool any)
    {
        min = Vector3.Min(min, position);
        max = Vector3.Max(max, position);
        any = true;
    }

    bool IncludeEventFocuses(ref Vector3 min, ref Vector3 max, ref bool any)
    {
        bool included = false;
        float now = Time.unscaledTime;
        for (int i = recentEventFocuses.Count - 1; i >= 0; i--)
        {
            EventFocusAnchor focus = recentEventFocuses[i];
            if (focus == null || now >= focus.until)
            {
                recentEventFocuses.RemoveAt(i);
                continue;
            }
            if (focus.target != null)
                focus.position = focus.target.position;
            IncludeFramingPoint(focus.position, ref min, ref max, ref any);
            included = true;
        }
        return included;
    }

    /// <summary>旧签名保留：相机控制器/卡牌特效/天气特效等既有调用方不关心距离时继续可用。</summary>
    public bool TryGetEventFocus(out Vector3 position)
    {
        return TryGetEventFocus(out position, out _);
    }

    /// <summary>事件聚焦扩展查询：除位置外返回建议聚焦距离（米）。focusDistance &lt; 0 表示事件未指定，由相机沿用自身默认值。</summary>
    public bool TryGetEventFocus(out Vector3 position, out float focusDistance)
    {
        if (Time.unscaledTime >= eventFocusUntil)
        {
            eventFocusTarget = null;
            position = eventFocusPosition;
            focusDistance = eventFocusDistance;
            return false;
        }
        if (eventFocusTarget != null)
            eventFocusPosition = eventFocusTarget.position;
        position = eventFocusPosition;
        focusDistance = eventFocusDistance;
        return true;
    }

    public string GetRouteAlignmentReport()
    {
        if (frames.Count == 0) return "Replay has no frames.";
        LycheeReplayFrame frame = frames[frameIndex];
        if (frame.players == null) return "Current frame has no players.";

        var report = new System.Text.StringBuilder();
        foreach (LycheeReplayPlayerState state in frame.players)
        {
            if (!convoys.TryGetValue(state.playerId, out ConvoyVisual convoy)) continue;
            if (!edges.TryGetValue(state.routeEdgeId ?? string.Empty, out MapRouteEdge edge))
            {
                report.AppendLine(state.teamId + ": stopped at node " + state.currentNodeId);
                continue;
            }

            Vector3 center = edge.Evaluate(PlayerEdgeProgress(state));
            float offset = Vector2.Distance(
                new Vector2(convoy.root.transform.position.x, convoy.root.transform.position.z),
                new Vector2(center.x, center.z));
            report.AppendLine(
                state.teamId + " edge=" + state.routeEdgeId +
                " type=" + edge.routeType +
                " routeOffset=" + offset.ToString("0.00") +
                " carriage=" + convoy.carriage.activeSelf +
                " boat=" + convoy.boat.activeSelf);
        }
        return report.ToString();
    }

    void Awake()
    {
        CacheSceneData();
#if UNITY_WEBGL && !UNITY_EDITOR
        ApplyWebGLQualityTrim();
        StartCoroutine(LoadReplayThenInitialize());
#else
        LoadReplay();
        InitializeAfterLoad();
#endif
    }

    void InitializeAfterLoad()
    {
        CreateDynamicRoots();
        CreateConvoys();
        CreateObstacleVisuals();
        CreateRuntimeHud();
        if (frames.Count > 0)
        {
            playing = playOnStart;
            applyingInitialFrame = true;
            ApplyFrame(0, true);
            applyingInitialFrame = false;
            InterpolateConvoys(0f, 0f);
            UpdateRuntimeHud();
            ShowOpeningRoster();
            SnapReplayCameras();
        }
        else
        {
            playing = false;
            // 若上游（如 ?path= 失败）已写入更具体的报错，则保留它，不被这条泛化信息覆盖。
            if (string.IsNullOrEmpty(startupError))
            {
                startupError =
                    "回放没有启动：未读取到任何 round 帧。\n" +
                    "来源：" + (string.IsNullOrEmpty(ReplaySource) ? ReplayRelative : ReplaySource) + "\n" +
                    "请确认构建包是通过 HTTP 服务打开，并且完整保留 StreamingAssets/Tournament/map1.txt。\n" +
                    "直接双击 index.html、只拷贝 Build 目录、或服务器拦截 txt/json 文件都会导致这里停住。";
                if (!string.IsNullOrEmpty(replayLoadDiagnostics))
                    startupError += "\n\n加载记录：\n" + replayLoadDiagnostics.Trim();
            }
            Debug.LogError("Replay initialization stopped: no round frames were loaded from " + ReplaySource + ".", this);
        }
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    /// <summary>WebGL 画质裁剪：浏览器单线程、draw call 是主瓶颈。WebGL 专属降配——
    /// 不改任何场景资产/桌面包，只在 WebGL 运行时收紧渲染并按位置哈希抽稀植被。</summary>
    void ApplyWebGLQualityTrim()
    {
        QualitySettings.shadowDistance = Mathf.Min(QualitySettings.shadowDistance, 45f);
        QualitySettings.lodBias = 0.8f; // 原 2 会强制远景树用高模；降到 0.8 让远树落到便宜 LOD(省三角、不增 draw call)
        QualitySettings.globalTextureMipmapLimit = 1; // 降一级贴图 mip(显存/带宽≈1/4)；俯视远景几乎看不出,即用户要的"降贴面"
        if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline is
            UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset urpAsset)
        {
            urpAsset.msaaSampleCount = 1;    // 关 MSAA，改用相机 FXAA 兜 AA
            urpAsset.renderScale = 0.8f;     // 片元成本≈0.64x
            urpAsset.shadowDistance = Mathf.Min(urpAsset.shadowDistance, 45f);
            // 水面 shader（IgniteCoders Simple Water）只采样 SceneDepth → 深度图必须保留；
            // 不透明图(SceneColor)无人采样，关掉省一遍全屏拷贝，且不会破坏水面。
            urpAsset.supportsCameraOpaqueTexture = false;
        }
        Camera sceneCamera = Camera.main ?? FindFirstObjectByType<Camera>();
        if (sceneCamera != null)
        {
            var cameraData = sceneCamera.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            if (cameraData != null)
                cameraData.antialiasing = UnityEngine.Rendering.Universal.AntialiasingMode.FastApproximateAntialiasing;
        }
        // 不再运行时抽稀植被(用户要求保留密度)。draw call 改由植被材质的 GPU 实例化收口——
        // 草 2 种 mesh / 树 3 种 mesh 共用 URP/Lit、已开 enableInstancing，几千个 draw 在 GPU 侧塌成几个实例批，
        // 零内存代价、植被一棵不少；比抽稀更省。
    }

    System.Collections.IEnumerator LoadReplayThenInitialize()
    {
        // 先预载 UITheme 主题贴图（WebGL 不能同步读 StreamingAssets），
        // 保证 HUD/演出构建时缓存已就绪，不会静默回落到程序化兜底。
        yield return LycheeThemeCache.Preload();
        // WebGL 上 StreamingAssets 是 URL，System.IO 不可用，必须走网络请求；
        // 加时间戳参数避免浏览器缓存旧的回放文件。文件名由 streamingReplayPath 决定（默认 replay.txt）。
        string relative = ReplayRelative;
        string text = null;
        string externalReplayUrl = ReplayUrlFromQuery();
        bool externalRequested = !string.IsNullOrEmpty(externalReplayUrl);
        if (externalRequested)
            yield return FetchReplayUrl(externalReplayUrl, "path", value => text = value);
        // 显式指定了 ?path= 却没取到 → 明确报错，绝不静默退回包内 StreamingAssets / 内置样例。
        // （否则会拿一份固定的旧回放冒充成功，结算/队名永远不变——这正是 ?path= "看似没生效" 的元凶。）
        if (externalRequested && string.IsNullOrEmpty(text))
        {
            startupError =
                "回放加载失败：?path= 指定的回放取不到。\n" +
                "URL：" + externalReplayUrl + "\n" +
                "请在浏览器直接打开该地址确认可访问（HTTP 200、内容非空；跨源时服务器需带 Access-Control-Allow-Origin 头）。" +
                (string.IsNullOrEmpty(replayLoadDiagnostics) ? "" : "\n\n加载记录：\n" + replayLoadDiagnostics.Trim());
            Debug.LogError("Replay load failed for explicit ?path=: " + externalReplayUrl, this);
            InitializeAfterLoad();
            yield break;
        }
        if (string.IsNullOrEmpty(text))
            yield return FetchStreamingText(relative, value => text = value);
        ParseReplay(text);
        InitializeAfterLoad();
    }

    System.Collections.IEnumerator FetchStreamingText(string relative, Action<string> onLoaded)
    {
        string url = Application.streamingAssetsPath + "/" + relative;
        yield return FetchReplayUrl(url, "StreamingAssets/" + relative, onLoaded);
    }

    System.Collections.IEnumerator FetchReplayUrl(string url, string sourceLabel, Action<string> onLoaded)
    {
        url = AddCacheBust(url);
        using (var request = UnityEngine.Networking.UnityWebRequest.Get(url))
        {
            request.timeout = 12;
            yield return request.SendWebRequest();
            if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                string text = request.downloadHandler.text;
                if (!string.IsNullOrEmpty(text))
                {
                    ReplaySource = sourceLabel + ": " + url;
                    onLoaded?.Invoke(text);
                    yield break;
                }
            }
            string detail = sourceLabel + " response=" + request.responseCode +
                " error=" + (string.IsNullOrEmpty(request.error) ? "<none>" : request.error);
            replayLoadDiagnostics += detail + "\n";
            Debug.LogWarning("Failed to fetch " + detail, this);
        }
        onLoaded?.Invoke(null);
    }

    static string ReplayUrlFromQuery()
    {
        string path = QueryValue(Application.absoluteURL, "path");
        if (string.IsNullOrEmpty(path)) return null;
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return path;
        Uri baseUri;
        if (Uri.TryCreate(Application.absoluteURL, UriKind.Absolute, out baseUri))
            return new Uri(baseUri, path).ToString();
        return path;
    }

    static string AddCacheBust(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        string separator = url.Contains("?") ? "&" : "?";
        return url + separator + "t=" + DateTime.UtcNow.Ticks;
    }

    static string QueryValue(string url, string key)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key)) return null;
        int queryIndex = url.IndexOf('?');
        if (queryIndex < 0 || queryIndex == url.Length - 1) return null;
        string query = url.Substring(queryIndex + 1);
        int hashIndex = query.IndexOf('#');
        if (hashIndex >= 0) query = query.Substring(0, hashIndex);

        string[] pairs = query.Split('&');
        foreach (string pair in pairs)
        {
            int equalIndex = pair.IndexOf('=');
            if (equalIndex <= 0) continue;
            string pairKey = Uri.UnescapeDataString(pair.Substring(0, equalIndex));
            if (!string.Equals(pairKey, key, StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(pair.Substring(equalIndex + 1));
        }
        return null;
    }
#endif

    void Update()
    {
        if (frames.Count == 0) return;
        float playbackDelta = Mathf.Min(Time.deltaTime, MaxPlaybackDeltaSeconds);
        if (playing)
        {
            frameTime += playbackDelta;
            float frameSeconds = EffectiveSecondsForFrame(CurrentFrame);
            while (frameTime >= frameSeconds && playing)
            {
                frameTime -= frameSeconds;
                if (frameIndex >= frames.Count - 1)
                {
                    if (loop) SeekFrame(0);
                    else
                    {
                        playing = false;
                        if (!matchOverRaised)
                        {
                            matchOverRaised = true;
                            MatchOverRaised?.Invoke(OverFrame);
                        }
                    }
                    break;
                }
                frameIndex++;
                // 播放真正越过开局首帧（出发动画已结束、或无演出）→ 兜底释放被延迟的开局小队派发。
                if (!openingDispatchesReleased) ReleaseOpeningDispatches();
                ApplyFrame(frameIndex, false);
                frameSeconds = EffectiveSecondsForFrame(CurrentFrame);
            }
        }

        float interpolation = FrameInterpolation;
        float visualDelta = playing ? playbackDelta : 0f;
        InterpolateConvoys(interpolation, visualDelta);
        // 出发动画展示期间冻结小队动作/到达消失计时，避免漏网小队在演出中走动或被销毁。
        if (!LycheeOpeningShowcase.AnyShowing) UpdateSquads(interpolation, visualDelta);
        UpdateRuntimeHud();
    }

    float EffectiveSecondsForFrame(LycheeReplayFrame frame)
    {
        float baseSeconds = Mathf.Max(MinRoundSeconds, secondsPerRound);
        // 对抗逐拍减速：对抗事件刚发生(含三拍)→每回合放慢到 ContestRoundSeconds，出牌按拍依次看清、镜头与世界同步不脱节。
        if (Time.unscaledTime < contestSlowUntil)
            return Mathf.Max(baseSeconds, ContestRoundSeconds);
        return baseSeconds;
    }

    static bool FrameHasMovingPlayer(LycheeReplayFrame frame)
    {
        if (frame.players == null) return false;
        for (int i = 0; i < frame.players.Length; i++)
        {
            LycheeReplayPlayerState player = frame.players[i];
            if (player == null) continue;
            if (!string.IsNullOrEmpty(player.routeEdgeId)) return true;
            if (string.Equals(player.state, "MOVING", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    static bool FrameHasStationProcess(LycheeReplayFrame frame)
    {
        if (frame.players != null)
        {
            for (int i = 0; i < frame.players.Length; i++)
            {
                string state = frame.players[i] == null ? "" : frame.players[i].state;
                if (string.Equals(state, "PROCESSING", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(state, "IDLE", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        if (frame.messages == null) return false;
        for (int i = 0; i < frame.messages.Length; i++)
        {
            string type = frame.messages[i] == null ? "" : frame.messages[i].type;
            if (type == "PROCESS_PROGRESS" || type == "MOVE_PROGRESS" || type == "FRESHNESS_DROP")
                return true;
        }
        return false;
    }

    static bool FrameHasMajorEvent(LycheeReplayFrame frame)
    {
        if (frame.messages == null) return false;
        for (int i = 0; i < frame.messages.Length; i++)
        {
            string type = frame.messages[i] == null ? "" : frame.messages[i].type;
            if (string.IsNullOrEmpty(type)) continue;
            if (type == "MOVE_PROGRESS" || type == "PROCESS_PROGRESS" || type == "FRESHNESS_DROP")
                continue;
            if (type == "SQUAD_DISPATCH")
                continue;
            return true;
        }
        return false;
    }

    void CacheSceneData()
    {
        foreach (MapStationAnchor station in FindObjectsByType<MapStationAnchor>(FindObjectsSortMode.None))
            stations[station.stationId] = station;
        foreach (MapRouteEdge edge in FindObjectsByType<MapRouteEdge>(FindObjectsSortMode.None))
            edges[edge.edgeId] = edge;
        elevation = FindFirstObjectByType<MapElevationData>();
    }

    /// <summary>本场景回放文件的 StreamingAssets 相对路径，空值回退 replay.txt。</summary>
    string ReplayRelative => string.IsNullOrEmpty(streamingReplayPath) ? "replay.txt" : streamingReplayPath;

    void LoadReplay()
    {
        string text = null;
        string relative = ReplayRelative;
        try
        {
            string streamingPath = System.IO.Path.Combine(Application.streamingAssetsPath, relative);
            if (System.IO.File.Exists(streamingPath))
            {
                text = System.IO.File.ReadAllText(streamingPath);
                ReplaySource = "StreamingAssets/" + relative;
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Failed to read StreamingAssets/" + relative + ": " + exception.Message, this);
            text = null;
        }

        ParseReplay(text);
    }

    void ParseReplay(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            // 用户：replay.txt 路径找不到就明确报错，不要回落去读内置样例/烘焙样例回放。
            string full = System.IO.Path.Combine(Application.streamingAssetsPath, ReplayRelative);
            startupError =
                "回放加载失败：找不到回放文件 " + ReplayRelative + "。\n" +
                "查找路径：" + full + "\n" +
                "请确认该文件存在且可读。已按要求不再回落到内置样例回放。";
            Debug.LogError("Replay not found at " + full + "; not falling back to bundled sample (per design).", this);
            return;
        }

        string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string line in lines)
        {
            if (line.Contains("\"type\":\"start\""))
            {
                startFrame = JsonUtility.FromJson<LycheeReplayFrame>(line);
                StartLineRaw = line;
                declaredTotalRounds = startFrame == null ? 0 : Mathf.Max(0, startFrame.tick > 0 ? startFrame.tick : startFrame.round);
                if (declaredTotalRounds <= 0 && line.Contains("\"durationRound\""))
                    declaredTotalRounds = ExtractIntField(line, "durationRound");
                continue;
            }
            if (line.Contains("\"type\":\"over\"") && !line.Contains("\"type\":\"round\""))
            {
                LycheeReplayOverFrame over = JsonUtility.FromJson<LycheeReplayOverFrame>(line);
                if (over != null && over.type == "over") OverFrame = over;
                continue;
            }
            if (!line.Contains("\"type\":\"round\"")) continue;
            LycheeReplayFrame frame = JsonUtility.FromJson<LycheeReplayFrame>(line);
            if (frame != null && frame.type == "round") frames.Add(frame);
        }
        PadReplayToDeclaredDuration();
        BackfillPlayerScoresFromFrameScores();
        Debug.Log("Replay loaded from " + ReplaySource + ": " + frames.Count + " round frames" +
            (OverFrame != null ? ", over frame parsed." : "."), this);
        ValidateScoreConsistency();
    }

    void PadReplayToDeclaredDuration()
    {
        if (frames.Count == 0 || declaredTotalRounds <= 0) return;
        // 比赛已分胜负（解析到 over 帧）→ 按真实回合数收尾，不再用静态帧补满到 durationRound。
        // 需求：replay 有多少回合就播多少秒，提前结束就提前结束，绝不为凑 600 回合/10 分钟而空播。
        // 仅当回放日志被截断且没有 over 帧时，才保留补帧兜底，避免末尾跳变。
        if (OverFrame != null)
        {
            Debug.Log("Replay ends at real round " + frames[frames.Count - 1].round +
                " (over frame present); skip padding to declared durationRound " + declaredTotalRounds + ".", this);
            return;
        }
        LycheeReplayFrame last = frames[frames.Count - 1];
        if (last == null || last.round >= declaredTotalRounds) return;
        string snapshot = JsonUtility.ToJson(last);
        for (int round = last.round + 1; round <= declaredTotalRounds; round++)
        {
            LycheeReplayFrame padded = JsonUtility.FromJson<LycheeReplayFrame>(snapshot);
            if (padded == null) continue;
            padded.round = round;
            padded.tick = round;
            padded.messages = new LycheeReplayMessage[0];
            frames.Add(padded);
        }
        if (OverFrame != null && OverFrame.overRound < declaredTotalRounds)
            OverFrame.overRound = declaredTotalRounds;
        Debug.Log("Replay padded to declared durationRound: " + declaredTotalRounds + " rounds.", this);
    }

    static int ExtractIntField(string jsonLine, string field)
    {
        if (string.IsNullOrEmpty(jsonLine) || string.IsNullOrEmpty(field)) return 0;
        string marker = "\"" + field + "\":";
        int start = jsonLine.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return 0;
        start += marker.Length;
        while (start < jsonLine.Length && char.IsWhiteSpace(jsonLine[start])) start++;
        int end = start;
        while (end < jsonLine.Length && char.IsDigit(jsonLine[end])) end++;
        if (end <= start) return 0;
        int value;
        return int.TryParse(jsonLine.Substring(start, end - start), out value) ? value : 0;
    }

    void SnapReplayCameras()
    {
        foreach (LycheeReplayCameraController controller in FindObjectsByType<LycheeReplayCameraController>(FindObjectsSortMode.None))
        {
            if (controller == null || !controller.isActiveAndEnabled) continue;
            if (controller.replay == null || controller.replay == this)
                controller.replay = this;
            if (controller.replay == this)
                controller.SnapToCurrentFocus();
        }
    }

    void BackfillPlayerScoresFromFrameScores()
    {
        foreach (LycheeReplayFrame frame in frames)
        {
            if (frame == null || frame.players == null || frame.scores == null) continue;
            foreach (LycheeReplayPlayerState state in frame.players)
            {
                if (state == null) continue;
                if (state.camp == 0) state.score = frame.scores.RED;
                else if (state.camp == 1) state.score = frame.scores.BLUE;
            }
        }
    }

    /// <summary>
    /// 回放数据一致性体检：测试中出现过"回合帧分数最高 682、结算页 800+"的反馈——
    /// 引擎只忠实显示数据，HUD 用 round 帧的 score、结算页用 over 帧的 totalScore、
    /// 明细行用 scoreDetail；三者由生成器分别写出，互相矛盾时这里点名报警，方便服务端定位。
    /// </summary>
    void ValidateScoreConsistency()
    {
        if (OverFrame == null || OverFrame.players == null || frames.Count == 0) return;
        LycheeReplayFrame lastFrame = frames[frames.Count - 1];
        foreach (LycheeReplayOverPlayer overPlayer in OverFrame.players)
        {
            if (overPlayer == null) continue;
            // 1. over.totalScore vs 最后一个 round 帧的实时分
            if (lastFrame.players != null)
            {
                foreach (LycheeReplayPlayerState state in lastFrame.players)
                {
                    if (state == null || state.playerId != overPlayer.playerId) continue;
                    if (state.score != overPlayer.totalScore)
                    {
                        Debug.LogWarning(string.Format(
                            "[回放数据不一致] {0}: over帧 totalScore={1}，但最后一个 round 帧 score={2}。" +
                            "HUD 显示前者轨迹、结算页显示后者，观感即\"分数对不上\"。请检查回放生成器。",
                            overPlayer.playerName, overPlayer.totalScore, state.score));
                    }
                }
            }
            // 2. scoreDetail 明细和 vs totalScore（结算页明细行加总应等于总分）
            LycheeReplayScoreDetail detail = overPlayer.scoreDetail;
            if (detail != null)
            {
                int sum = detail.delivery + detail.goodFruit + detail.freshness +
                          detail.time + detail.tasks + detail.bounty - Mathf.Abs(detail.penalty);
                if (sum != overPlayer.totalScore)
                {
                    Debug.LogWarning(string.Format(
                        "[回放数据不一致] {0}: scoreDetail 明细和={1} ≠ totalScore={2}（差 {3}）。" +
                        "结算页明细行将与总分对不上。请检查回放生成器的分项拆解。",
                        overPlayer.playerName, sum, overPlayer.totalScore, overPlayer.totalScore - sum));
                }
            }
            // 3. HUD vs 结算的真正错位源：游戏内顶层比分条读 round 帧 scores.RED/BLUE（见 RuntimeHud），
            //    结算页读 over.totalScore。两者由生成器分别累计，不一致就是"HUD 最高 682、结算 800+"。
            if (lastFrame.scores != null)
            {
                int running = overPlayer.camp == 0 ? lastFrame.scores.RED : lastFrame.scores.BLUE;
                if (running != overPlayer.totalScore)
                {
                    Debug.LogWarning(string.Format(
                        "[回放数据不一致] {0}(camp{1}): 末回合顶层 scores={2}（游戏内比分条显示），" +
                        "但 over.totalScore={3}（结算页显示），差 {4}。这就是\"HUD 与结算分数对不上\"的根源——" +
                        "顶层运行分与结算总分须同源，请检查回放生成器。",
                        overPlayer.playerName, overPlayer.camp, running, overPlayer.totalScore, overPlayer.totalScore - running));
                }
            }
        }
    }

    void CreateDynamicRoots()
    {
        dynamicRoot = new GameObject("Replay_Convoys").transform;
        dynamicRoot.SetParent(transform, false);
        eventRoot = new GameObject("Replay_Events").transform;
        eventRoot.SetParent(transform, false);
    }

    void CreateConvoys()
    {
        if (frames.Count == 0 || frames[0].players == null) return;
        foreach (LycheeReplayPlayerState player in frames[0].players)
        {
            GameObject root = new GameObject("Convoy_" + player.teamId);
            root.transform.SetParent(dynamicRoot, false);
            ConvoyVisual visual = new ConvoyVisual
            {
                root = root,
                // 比例锚定站点建筑（4.2~6.4）：建筑 > 马车/船 > 人物，近景才不会出现"马车比楼大"
                // 3.2：组合马车（马+车一体）比纯货车长，归一到 2.4 会让车厢显小；纯货车模型用此值也不违和
                carriage = CreateNormalizedModel(carriageModel, root.transform, "Carriage", 3.2f),
                boat = CreateNormalizedModel(boatModel, root.transform, "Boat", 3.0f),
                // 将军取代队长（"Captain" wrapper 名保留，内部换将军模型）：红队用赤龙将军、蓝队用蓝将军，
                // 某队将军模型缺失则回退 captainModel 占位（蓝将军待用户提供，先用绑骨队长占位居中）
                captain = CreateNormalizedModel(
                    ResolveGeneralModel(player.camp), root.transform, "Captain", 2.0f)
            };
            // AI 导出的组合马车正面不一定符合 +Z 约定，按装配脚本实测的偏航修正整体转正
            if (visual.carriage != null && Mathf.Abs(carriageYawOffset) > 0.01f)
                visual.carriage.transform.localRotation = Quaternion.Euler(0f, carriageYawOffset, 0f);
            int teamSize = ResolveTeamSize(player);
            teamRosterSizes[player.playerId] = teamSize;
            if (visual.captain != null)
            {
                // 红蓝两队将军都走在各自车队最前领队（+Z 前方居中）
                visual.captain.transform.localPosition = new Vector3(0f, 0f, 2.8f);
                // 将军模型自带贴图，只在用 captainModel 占位时才上队色 tint（避免把贴图染脏）
                bool generalIsPlaceholder = (player.camp == 0 ? redGeneralModel : blueGeneralModel) == null;
                if (generalIsPlaceholder)
                    TintModel(visual.captain, player.camp == 0 ? new Color(1f, 0.36f, 0.28f, 1f) : new Color(0.36f, 0.62f, 1f, 1f), 0.14f);
            }
            CreateTeamMembers(visual, Mathf.Max(0, teamSize - 1), player.camp);
            visual.label = CreateLabel(root.transform, TeamDisplayName(player), player.camp == 0
                ? new Color(1f, 0.31f, 0.18f)
                : new Color(0.24f, 0.62f, 1f));
            CreateTeamRing(root.transform, player.camp);
            convoys[player.playerId] = visual;
        }
    }

    /// <summary>按阵营选将军模型：红队(0)=赤龙将军、蓝队(1)=蓝将军；该队将军未提供则回退 captainModel 占位。</summary>
    GameObject ResolveGeneralModel(int camp)
    {
        GameObject general = camp == 0 ? redGeneralModel : blueGeneralModel;
        return general != null ? general : captainModel;
    }

    /// <summary>按阵营取将军行走 clip（供 LycheeCharacterMotion 播放；占位的 captainModel 无 clip 返回 null 走程序化）。</summary>
    public AnimationClip GetGeneralWalkClip(int camp)
    {
        if (camp == 0) return redGeneralModel != null ? redGeneralWalkClip : null;
        return blueGeneralModel != null ? blueGeneralWalkClip : null;
    }

    /// <summary>按阵营取将军攻击 clip（对抗期间播；空则对抗时保持走路）。</summary>
    public AnimationClip GetGeneralAttackClip(int camp)
    {
        if (camp == 0) return redGeneralModel != null ? redGeneralAttackClip : null;
        return blueGeneralModel != null ? blueGeneralAttackClip : null;
    }

    int ResolveTeamSize(LycheeReplayPlayerState player)
    {
        LycheeReplayPlayerState startPlayer = null;
        if (startFrame != null && startFrame.players != null)
            startPlayer = startFrame.players.FirstOrDefault(candidate => candidate.playerId == player.playerId);

        // 回放真有单位数(objs)就用它，但封顶 8 —— 16 人跟车太挤(用户反馈)。
        if (startPlayer != null && startPlayer.objs != null && startPlayer.objs.Length > 1)
            return Mathf.Clamp(startPlayer.objs.Length, 2, 8);
        if (player.objs != null && player.objs.Length > 1)
            return Mathf.Clamp(player.objs.Length, 2, 8);
        // 回放没给单位数：固定 6(队长1+队员5)，不再 squadAvailable*2 膨胀到 16。
        // 用 Min 兜底场景里序列化的旧值(16)，避免必须改场景。
        return Mathf.Min(defaultTeamSize, 6);
    }

    void CreateTeamMembers(ConvoyVisual visual, int memberCount, int camp)
    {
        if (visual == null || visual.root == null) return;
        Color tint = camp == 0 ? new Color(1f, 0.34f, 0.26f, 1f) : new Color(0.32f, 0.58f, 1f, 1f);
        const int columns = 5;
        for (int i = 0; i < memberCount; i++)
        {
            GameObject member = CreateNormalizedModel(squadMemberModel, visual.root.transform, "TeamMember_" + (i + 1), 0.95f);
            if (member == null) continue;
            int col = i % columns;
            int row = i / columns;
            float x = (col - (columns - 1) * 0.5f) * 0.62f;
            float z = 0.7f + row * 0.62f;
            member.transform.localPosition = new Vector3(x, 0.02f, z);
            member.transform.localRotation = Quaternion.Euler(0f, Mathf.Sin(i * 7.3f) * 8f, 0f);
            TintModel(member, tint, 0.10f);
            visual.members.Add(member);
        }
    }

    void TintModel(GameObject root, Color tint, float strength)
    {
        if (root == null) return;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        foreach (Renderer renderer in renderers)
        {
            Material[] materials = renderer.materials;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null) continue;
                if (material.HasProperty("_BaseColor"))
                    material.SetColor("_BaseColor", Color.Lerp(material.GetColor("_BaseColor"), tint, strength));
                else if (material.HasProperty("_Color"))
                    material.SetColor("_Color", Color.Lerp(material.GetColor("_Color"), tint, strength));
            }
            renderer.materials = materials;
        }
    }

    void CreateObstacleVisuals()
    {
        foreach (MapStationAnchor station in stations.Values)
        {
            GameObject obstacle = new GameObject("Obstacle_" + station.stationId);
            obstacle.transform.SetParent(eventRoot, false);
            obstacle.transform.position = ObstacleWorldPosition(station);
            obstacle.transform.rotation = ObstacleWorldRotation(station, null);

            GameObject model = CreateNormalizedModel(
                roadObstacleModel,
                obstacle.transform,
                "ObstacleModel",
                1.65f);
            if (model != null)
            {
                model.transform.localPosition = Vector3.zero;
                obstacleGenericModels[station.stationId] = model;
            }

            TextMesh label = CreateLabel(obstacle.transform, "", new Color(1f, 0.78f, 0.32f, 1f));
            label.transform.localPosition = new Vector3(0f, 2.1f, 0f);

            obstacle.SetActive(false);
            obstacles[station.stationId] = obstacle;
            obstacleLabels[station.stationId] = label;
        }
    }

    void CreateRuntimeHud()
    {
        if (!useRuntimeHud) return;
        runtimeHud = GetComponent<LycheeReplayRuntimeHud>();
        if (runtimeHud == null) runtimeHud = gameObject.AddComponent<LycheeReplayRuntimeHud>();
        runtimeHud.Configure(combatIllustrations, redCaptainAvatar, blueCaptainAvatar);
    }

    void ShowOpeningRoster()
    {
        if (runtimeHud == null || frames.Count == 0 || frames[0].players == null) return;
        LycheeReplayPlayerState red = frames[0].players.FirstOrDefault(player => player.camp == 0);
        LycheeReplayPlayerState blue = frames[0].players.FirstOrDefault(player => player.camp == 1);
        runtimeHud.ShowOpening(
            red == null ? "红队" : red.name,
            red == null ? defaultTeamSize : TeamSize(red.playerId),
            blue == null ? "蓝队" : blue.name,
            blue == null ? defaultTeamSize : TeamSize(blue.playerId));
    }

    int routeNodeMax = -1;   // 路网终点站序号(S15→15)，懒算缓存

    static int StationNumber(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId) || nodeId.Length < 2 || (nodeId[0] != 'S' && nodeId[0] != 's')) return -1;
        return int.TryParse(nodeId.Substring(1), out int n) ? n : -1;
    }

    int RouteNodeMax()
    {
        if (routeNodeMax > 0) return routeNodeMax;
        int m = 2;
        foreach (MapStationAnchor s in stations.Values)
            if (s != null) { int n = StationNumber(s.stationId); if (n > m) m = n; }
        routeNodeMax = m;
        return routeNodeMax;
    }

    /// <summary>整段送达进度(0..1)：用节点序号(S01→S15)推总进度，<b>不</b>依赖各回放文件对 progress 字段的不同口径。
    /// replay.txt 的 progress 是「当前路段」比例、每到站清零，直接用会 0↔100 抖动；这里改用站号+本段比例得到单调总进度。</summary>
    float RouteProgressOf(LycheeReplayPlayerState s)
    {
        if (s == null) return 0f;
        if (s.delivered) return 1f;
        int cur = StationNumber(s.currentNodeId);
        if (cur < 1) return Mathf.Clamp01(s.progress);                 // 拿不到站号时退回原字段
        float edgeFrac = !string.IsNullOrEmpty(s.routeEdgeId) ? Mathf.Clamp01(s.edgeProgressPermille / 1000f) : 0f;
        return Mathf.Clamp01((cur - 1 + edgeFrac) / (RouteNodeMax() - 1));
    }

    void UpdateRuntimeHud()
    {
        if (runtimeHud == null || frames.Count == 0) return;
        LycheeReplayFrame frame = frames[frameIndex];
        LycheeReplayPlayerState red = frame.players == null ? null : frame.players.FirstOrDefault(player => player.camp == 0);
        LycheeReplayPlayerState blue = frame.players == null ? null : frame.players.FirstOrDefault(player => player.camp == 1);
        runtimeHud.Refresh(
            frame.round,
            frames[frames.Count - 1].round,
            frame.phase,
            latestEvent,
            red,
            blue,
            red == null ? defaultTeamSize : TeamSize(red.playerId),
            blue == null ? defaultTeamSize : TeamSize(blue.playerId),
            frame.scores,
            RouteProgressOf(red),
            RouteProgressOf(blue),
            CompletedTaskCount(frame, red),
            CompletedTaskCount(frame, blue));
    }

    // 某队已完成的任务数：frame.tasks 里 completed 且 ownerPlayerId == 该队 playerId。
    static int CompletedTaskCount(LycheeReplayFrame frame, LycheeReplayPlayerState player)
    {
        if (frame == null || frame.tasks == null || player == null) return 0;
        int n = 0;
        foreach (LycheeReplayTaskState t in frame.tasks)
            if (t != null && t.completed && t.ownerPlayerId == player.playerId) n++;
        return n;
    }

    int TeamSize(int playerId)
    {
        return teamRosterSizes.TryGetValue(playerId, out int size) ? size : Mathf.Max(2, defaultTeamSize);
    }

    void ApplyFrame(int index, bool force)
    {
        if (index < 0 || index >= frames.Count) return;
        LycheeReplayFrame frame = frames[index];
        LycheeReplayFrame previousApplied = lastAppliedFrame;
        bool previousForcedState = applyingForcedFrame;
        applyingForcedFrame = force;
        try
        {
            if (frame.messages != null && (force || index > lastEventFrame))
            {
                foreach (LycheeReplayMessage message in frame.messages)
                {
                    if (force) RestoreContestStartBeforeReveal(index, message);
                    HandleEvent(message, frame.round);
                }
                lastEventFrame = index;
            }
        }
        finally
        {
            applyingForcedFrame = previousForcedState;
        }
        ApplyObstacles(frame);
        RoundApplied?.Invoke(previousApplied, frame);
        lastAppliedFrame = frame;
    }

    void RestoreContestStartBeforeReveal(int targetIndex, LycheeReplayMessage message)
    {
        if (message == null || message.type != "WINDOW_CARD_REVEAL" || message.payload == null) return;
        string contestId = message.payload.contestId;
        if (string.IsNullOrEmpty(contestId)) return;

        for (int i = targetIndex - 1; i >= 0; i--)
        {
            LycheeReplayFrame frame = frames[i];
            if (frame == null || frame.messages == null) continue;
            for (int j = frame.messages.Length - 1; j >= 0; j--)
            {
                LycheeReplayMessage candidate = frame.messages[j];
                if (candidate == null || candidate.type != "WINDOW_CONTEST_START" || candidate.payload == null) continue;
                if (candidate.payload.contestId != contestId) continue;
                HandleEvent(candidate, frame.round);
                return;
            }
        }
    }

    /// <summary>
    /// 释放开局首帧被延迟入队的 SQUAD_DISPATCH：由 LycheeOpeningShowcase 在出发动画结束（CloseShowcase）
    /// 或判定非开局/无演出时调用；播放推进越过首帧时也兜底释放。幂等，仅生效一次。
    /// </summary>
    public void ReleaseOpeningDispatches()
    {
        if (openingDispatchesReleased) return;
        openingDispatchesReleased = true;
        if (pendingOpeningDispatches.Count == 0) return;
        LycheeReplayMessage[] queued = pendingOpeningDispatches.ToArray();
        pendingOpeningDispatches.Clear();
        foreach (LycheeReplayMessage queuedMessage in queued)
            HandleEvent(queuedMessage, pendingOpeningRound);
    }

    void InterpolateConvoys(float t, float visualDelta)
    {
        LycheeReplayFrame current = frames[frameIndex];
        LycheeReplayFrame next = frames[Mathf.Min(frameIndex + 1, frames.Count - 1)];
        if (current.players == null) return;

        foreach (LycheeReplayPlayerState player in current.players)
        {
            if (!convoys.TryGetValue(player.playerId, out ConvoyVisual visual)) continue;
            LycheeReplayPlayerState nextPlayer = next.players == null
                ? null
                : next.players.FirstOrDefault(candidate => candidate.playerId == player.playerId);
            EvaluatePlayerPose(player, nextPlayer, t, out Vector3 routePosition, out Vector3 tangent, out bool resolved);
            // 未解析（无路径边、无可解析站点、且回放未提供 grid 坐标）时保持上一帧位置，
            // 绝不把车队更新到 GridToWorld(0,0) 的地图角落——杜绝“开局一队马车闪出去又弹回起点”。
            if (resolved)
            {
                if (tangent.sqrMagnitude > 0.0001f)
                    visual.lastTangent = tangent.normalized;

                Vector3 routeSide = Vector3.Cross(Vector3.up, visual.lastTangent);
                Vector3 targetPosition = routePosition + routeSide * (player.camp == 0 ? -0.72f : 0.72f);
                if (!visual.initialized)
                {
                    visual.smoothedPosition = targetPosition;
                    visual.root.transform.position = targetPosition;
                    visual.root.transform.rotation = Quaternion.LookRotation(visual.lastTangent, Vector3.up);
                    visual.initialized = true;
                }
                else
                {
                    if (visualDelta > 0f)
                    {
                        visual.smoothedPosition = Vector3.SmoothDamp(
                            visual.smoothedPosition,
                            targetPosition,
                            ref visual.positionVelocity,
                            0.055f,
                            80f,
                            visualDelta);
                        Quaternion targetRotation = Quaternion.LookRotation(visual.lastTangent, Vector3.up);
                        float rotationBlend = 1f - Mathf.Exp(-10f * visualDelta);
                        visual.root.transform.rotation = Quaternion.Slerp(
                            visual.root.transform.rotation,
                            targetRotation,
                            rotationBlend);
                        visual.bobTime += visualDelta;
                    }
                }
                visual.root.transform.position = visual.smoothedPosition +
                    Vector3.up * (0.10f + Mathf.Sin(visual.bobTime * 6f + player.camp) * 0.025f);
            }

            bool water = IsWaterEdge(player.routeEdgeId);
            if (visual.carriage != null) visual.carriage.SetActive(!water);
            if (visual.boat != null)
            {
                visual.boat.SetActive(water);
                if (water) EnsureConvoyBoatFlag(visual, player.camp);
            }
            // 水路只保留船（带红蓝队旗），将军与队员藏起来——否则像在水面上走路
            if (visual.captain != null) visual.captain.SetActive(!water);
            for (int mi = 0; mi < visual.members.Count; mi++)
                if (visual.members[mi] != null) visual.members[mi].SetActive(!water);
            if (visual.label != null)
                visual.label.text = TeamDisplayName(player) +
                    "\n鲜度 " + player.freshness.ToString("0") +
                    "  好果 " + player.goodFruit +
                    "  得分 " + player.score;
        }
    }

    void EvaluatePlayerPose(
        LycheeReplayPlayerState player,
        LycheeReplayPlayerState nextPlayer,
        float interpolation,
        out Vector3 position,
        out Vector3 tangent,
        out bool resolved)
    {
        tangent = Vector3.zero;
        resolved = true;
        if (!string.IsNullOrEmpty(player.routeEdgeId) &&
            edges.TryGetValue(player.routeEdgeId, out MapRouteEdge edge))
        {
            float progress = PlayerEdgeProgress(player);
            if (nextPlayer != null && nextPlayer.routeEdgeId == player.routeEdgeId)
            {
                // 回合间 progress 用 Catmull-Rom 样条（穿过 前/现/次/次次 回合的 progress）而非直线 Lerp：
                // 直线在每个回合边界处速度会"折"一下，1× 时摊到整整 1 秒非常显眼（"抽帧/一顿一顿"的感觉）；
                // 样条让速度在回合边界连续(C1)、过弯/变速顺滑。两端缺邻居时回退到该端点 → 近似直线、不外冲。
                float p1 = progress;
                float p2 = PlayerEdgeProgress(nextPlayer);
                float p0 = EdgeProgressAt(frameIndex - 1, player.playerId, player.routeEdgeId, p1);
                float p3 = EdgeProgressAt(frameIndex + 2, player.playerId, player.routeEdgeId, p2);
                progress = Mathf.Clamp01(CatmullRom(p0, p1, p2, p3, interpolation));
            }
            bool reverse = player.currentNodeId == edge.toStationId;
            float evaluatedProgress = reverse ? 1f - progress : progress;
            float travelSign = reverse ? -1f : 1f;
            const float tangentSample = 0.0125f;
            Vector3 before = edge.Evaluate(Mathf.Clamp01(evaluatedProgress - tangentSample * travelSign));
            Vector3 after = edge.Evaluate(Mathf.Clamp01(evaluatedProgress + tangentSample * travelSign));
            tangent = after - before;
            position = edge.Evaluate(evaluatedProgress) + Vector3.up * 0.12f;
            return;
        }

        string stationId = !string.IsNullOrEmpty(player.currentNodeId)
            ? player.currentNodeId
            : player.nextNodeId;
        if (!string.IsNullOrEmpty(stationId) &&
            stations.TryGetValue(stationId, out MapStationAnchor station))
        {
            Transform stop = player.camp == 0 ? station.vehicleStopRed : station.vehicleStopBlue;
            position = (stop == null ? station.transform.position : stop.position) + Vector3.up * 0.12f;
            return;
        }

        // 回放数据未提供 grid 坐标时 player.position 恒为 (0,0)（JSON 用 progress/节点字段，无 position 键）。
        // 落到这里说明既无路径边、也无可解析站点：GridToWorld(0,0) 会把车队丢到地图角落(-40,30)，
        // 下一帧拿到有效节点又被 SmoothDamp 拉回起点——正是“开局一队马车一下子闪出去又弹回起点”的根因。
        // 仅当确有非零 grid 坐标时才算解析成功；否则标记未解析，由调用方保持上一帧位置、绝不瞬移到角落。
        Vector2 playerGrid = PlayerGridPosition(player);
        Vector2 nextGrid = PlayerGridPosition(nextPlayer);
        resolved =
            playerGrid.sqrMagnitude > 0.0001f ||
            (nextPlayer != null && nextGrid.sqrMagnitude > 0.0001f);
        Vector2 grid = playerGrid;
        if (nextPlayer != null)
            grid = Vector2.Lerp(grid, nextGrid, interpolation);
        position = GridToWorld(grid.x, grid.y);
        if (nextPlayer != null)
        {
            Vector3 nextPosition = GridToWorld(nextGrid.x, nextGrid.y);
            tangent = nextPosition - position;
        }
    }

    /// <summary>取第 frameIdx 帧里 playerId 在 edgeId 上的进度；若该帧不存在/不是同一条边/玩家缺失，返回 fallback。
    /// 给 Catmull-Rom 取相邻回合控制点用——换边或越界处回退到端点，自动退化为近似直线、避免样条外冲跑偏。</summary>
    float EdgeProgressAt(int frameIdx, int playerId, string edgeId, float fallback)
    {
        if (frameIdx < 0 || frameIdx >= frames.Count) return fallback;
        LycheeReplayFrame f = frames[frameIdx];
        if (f.players == null) return fallback;
        LycheeReplayPlayerState p = f.players.FirstOrDefault(c => c.playerId == playerId);
        if (p == null || p.routeEdgeId != edgeId) return fallback;
        return PlayerEdgeProgress(p);
    }

    /// <summary>均匀 Catmull-Rom：t∈[0,1] 在 p1→p2 之间，切线由 p0/p3 决定（穿过 p1、p2，回合边界速度连续 C1）。</summary>
    static float CatmullRom(float p0, float p1, float p2, float p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * ((2f * p1) +
                       (-p0 + p2) * t +
                       (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                       (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    static float PlayerEdgeProgress(LycheeReplayPlayerState state)
    {
        if (state == null) return 0f;
        if (state.edgeTotalMs > 0 && state.edgeProgressMs >= 0)
            return Mathf.Clamp01(state.edgeProgressMs / (float)state.edgeTotalMs);
        if (state.edgeProgressPermille > 0) return Mathf.Clamp01(state.edgeProgressPermille / 1000f);
        if (state.moveProgress > 0f || state.position != null) return Mathf.Clamp01(state.moveProgress);
        return Mathf.Clamp01(state.progress);
    }

    static Vector2 PlayerGridPosition(LycheeReplayPlayerState state)
    {
        return state == null || state.position == null
            ? Vector2.zero
            : new Vector2(state.position.x, state.position.y);
    }

    void ApplyObstacles(LycheeReplayFrame frame)
    {
        if (frame.nodes == null) return;
        foreach (LycheeReplayNodeState node in frame.nodes)
        {
            if (obstacles.TryGetValue(node.nodeId, out GameObject obstacle))
            {
                bool wasActive = obstacleWasActive.TryGetValue(node.nodeId, out bool previous) && previous;
                obstacle.SetActive(node.hasObstacle);
                if (node.hasObstacle)
                {
                    string obstacleType = string.IsNullOrEmpty(node.obstacleType) ? "OBSTACLE" : node.obstacleType;
                    ConfigureObstacleVisual(node, obstacle, obstacleType);
                    if (!wasActive && frameIndex > 0)
                        SpawnObstacleAppearFx(node.nodeId, obstacleType);
                    activeObstacleTypes[node.nodeId] = obstacleType;
                }
                else
                {
                    activeObstacleTypes.Remove(node.nodeId);
                    HideObstacleExtras(node.nodeId);
                }
                obstacleWasActive[node.nodeId] = node.hasObstacle;
            }
        }
    }

    void ConfigureObstacleVisual(LycheeReplayNodeState node, GameObject obstacle, string obstacleType)
    {
        if (node == null || obstacle == null) return;
        if (stations.TryGetValue(node.nodeId, out MapStationAnchor station) && station != null)
        {
            obstacle.transform.position = ObstacleWorldPosition(station);
            obstacle.transform.rotation = ObstacleWorldRotation(station, obstacleType);
        }
        if (obstacleLabels.TryGetValue(node.nodeId, out TextMesh label) && label != null)
            label.text = ObstacleName(obstacleType) + "\n" + DisplayNode(node.nodeId);

        bool rockfall = obstacleType == "ROCKFALL";
        bool brokenBridge = obstacleType == "BROKEN_BRIDGE";
        bool dockBlock = obstacleType == "DOCK_BLOCK";
        if (obstacleGenericModels.TryGetValue(node.nodeId, out GameObject genericModel) && genericModel != null)
            genericModel.SetActive(!rockfall && !brokenBridge && !dockBlock);
        GameObject rockPile = EnsureRockfallPile(node.nodeId, obstacle.transform);
        if (rockPile != null)
            rockPile.SetActive(rockfall);
        GameObject bridge = EnsureBrokenBridge(node.nodeId, obstacle.transform);
        if (bridge != null)
            bridge.SetActive(brokenBridge);
        GameObject dock = EnsureDockBlock(node.nodeId, obstacle.transform);
        if (dock != null)
            dock.SetActive(dockBlock);
    }

    void HideObstacleExtras(string nodeId)
    {
        if (obstacleRockPiles.TryGetValue(nodeId, out GameObject rockPile) && rockPile != null)
            rockPile.SetActive(false);
        if (obstacleBrokenBridges.TryGetValue(nodeId, out GameObject bridge) && bridge != null)
            bridge.SetActive(false);
        if (obstacleDockBlocks.TryGetValue(nodeId, out GameObject dock) && dock != null)
            dock.SetActive(false);
        if (obstacleGenericModels.TryGetValue(nodeId, out GameObject genericModel) && genericModel != null)
            genericModel.SetActive(false);
    }

    Vector3 ObstacleWorldPosition(MapStationAnchor station)
    {
        if (station == null) return transform.position;
        Transform anchor = station.obstacleAnchor != null ? station.obstacleAnchor : station.eventAnchor;
        if (anchor != null)
            return anchor.position + Vector3.up * 0.05f;

        Vector3 position = station.transform.position;
        if (elevation != null)
            position.y = elevation.SampleWorldHeight(position.x, position.z) + 0.12f;
        else
            position += Vector3.up * 0.1f;
        return position;
    }

    Quaternion ObstacleWorldRotation(MapStationAnchor station, string obstacleType)
    {
        if (station == null) return Quaternion.identity;
        if (station.obstacleAnchor != null)
            return Quaternion.Euler(0f, station.obstacleAnchor.eulerAngles.y, 0f);

        bool preferWater = obstacleType == "DOCK_BLOCK";
        if (TryGetConnectedRouteDirection(station.stationId, station.transform.position, preferWater, out Vector3 direction))
            return Quaternion.LookRotation(direction, Vector3.up);
        return Quaternion.Euler(0f, StableHash(station.stationId) % 360, 0f);
    }

    bool TryGetConnectedRouteDirection(string stationId, Vector3 stationPosition, bool preferWater, out Vector3 direction)
    {
        direction = Vector3.zero;
        int bestScore = int.MinValue;
        foreach (MapRouteEdge edge in edges.Values)
        {
            if (edge == null) continue;
            bool from = edge.fromStationId == stationId;
            bool to = edge.toStationId == stationId;
            if (!from && !to) continue;

            Vector3 candidate = Vector3.zero;
            if (edge.pathPoints != null && edge.pathPoints.Length > 1)
            {
                if (from)
                    candidate = edge.pathPoints[1].position - edge.pathPoints[0].position;
                else
                    candidate = edge.pathPoints[edge.pathPoints.Length - 2].position - edge.pathPoints[edge.pathPoints.Length - 1].position;
            }
            else
            {
                string otherId = from ? edge.toStationId : edge.fromStationId;
                if (stations.TryGetValue(otherId, out MapStationAnchor other) && other != null)
                    candidate = other.transform.position - stationPosition;
            }

            candidate.y = 0f;
            if (candidate.sqrMagnitude <= 0.0001f) continue;
            bool water = edge.routeType == MapRouteType.Water;
            int score = water == preferWater ? 10 : 0;
            if (!water && !preferWater) score += 4;
            if (score <= bestScore) continue;
            bestScore = score;
            direction = candidate.normalized;
        }
        return direction.sqrMagnitude > 0.0001f;
    }

    // 「幽灵清障失败」过滤用：本回合已 SQUAD_CLEAR 成功的节点集合(按回合重置)。
    int squadGuardRound = -1;
    readonly HashSet<string> squadClearedNodesThisRound = new HashSet<string>();

    // 「宫门验核开始」合成用：已为之合成(或已收到真)START 的队伍集合；倒带时清空重新武装。
    int verifyGateGuardRound = -1;
    readonly HashSet<int> verifyGateStartedPlayers = new HashSet<int>();

    void HandleEvent(LycheeReplayMessage message, int round)
    {
        // 用户：被争抢的障碍会在同回合同节点同时发「清障成功 SQUAD_CLEAR(有队号)」+「清障失败 SQUAD_FAILED」。
        // 那条失败要么没队号(player=None)、要么是"赶到时障碍已被清掉"的迟到者——它会把成功提示覆盖成
        // "失败"、音效却响清障成功音，自相矛盾。在广播给任何子系统(音效/演出/HUD/事件流)之前做通用过滤，
        // 挡掉这类幽灵清障失败：① 无队伍归属(playerId<=0)；或 ② 该节点本回合已有清障成功(障碍已没、失败无意义)。
        if (message != null)
        {
            if (round != squadGuardRound) { squadClearedNodesThisRound.Clear(); squadGuardRound = round; }
            // 倒带：重新武装「验核开始」合成，让回看时声音/播报能再次触发。
            if (round < verifyGateGuardRound) verifyGateStartedPlayers.Clear();
            verifyGateGuardRound = round;
            LycheeReplayPayload sp = message.payload;
            string mtype = message.type;
            if ((mtype == "SQUAD_CLEAR" || mtype == "SQUAD_CLEAR_SUCCESS") && sp != null && !string.IsNullOrEmpty(sp.targetNodeId))
                squadClearedNodesThisRound.Add(sp.targetNodeId);
            if (mtype == "SQUAD_FAILED")
            {
                bool noTeam = sp == null || sp.playerId <= 0;
                bool alreadyCleared = sp != null && !string.IsNullOrEmpty(sp.targetNodeId) && squadClearedNodesThisRound.Contains(sp.targetNodeId);
                if (noTeam || alreadyCleared) return;
            }
            // 「宫门验核开始」合成：V4.8.12 起外部回放不再发 VERIFY_GATE_START，但验核进度流
            // PROCESS_PROGRESS(VERIFY_GATE) 仍在——每队第一条进度就是真正的验核开始(比 COMPLETE 早约6回合)。
            // 据此合成一条 START 广播出去，sfx_verify_gate_start + 左侧播报 + 世界进度环随之复活。
            // 去重：若收到过真 START，登记该队、不再合成，避免双响(对未来含真 START 的回放也安全)。
            if (sp != null && sp.playerId > 0)
            {
                if (mtype == "VERIFY_GATE_START")
                    verifyGateStartedPlayers.Add(sp.playerId);
                else if (mtype == "PROCESS_PROGRESS" && sp.processType == "VERIFY_GATE"
                         && !verifyGateStartedPlayers.Contains(sp.playerId))
                {
                    verifyGateStartedPlayers.Add(sp.playerId);
                    var synthStart = new LycheeReplayMessage { type = "VERIFY_GATE_START", payload = sp };
                    ReplayEventRaised?.Invoke(synthStart, round);
                }
            }
        }
        ReplayEventRaised?.Invoke(message, round);
        if (message == null) return;
        latestEvent = "回合" + round + "  " + LycheeHudExtensions.EventNameCn(message.type);
        LycheeReplayPayload payload = message.payload ?? new LycheeReplayPayload();

        // 冰鉴 / 马 / 荔枝转坏 三类资源特效事件【不聚焦镜头】：用户明确要求不要改镜头约束、不要拉近，
        // 改用"把特效本身做明显"（恒定屏幕尺寸的头顶徽记 + 高对比光环/光柱 + 大地面爆裂）在原机位也看得清，
        // 而不是把镜头拉到车队。所以这里不再调用 SetEventFocus。对抗/小队/站点的聚焦保持原样（在下方）。

        // 任一对抗事件(开始/逐拍亮牌/结束/平局)都给回放减速续期：让每一拍出牌各占 ~2.6s、看得清；
        // 末拍后再多撑一拍把结算卡也留住，之后自然恢复原速。
        if (message.type == "WINDOW_CONTEST_START" || message.type == "WINDOW_CARD_REVEAL" ||
            message.type == "WINDOW_CONTEST_END" || message.type == "WINDOW_CONTEST_DRAW")
            contestSlowUntil = Time.unscaledTime + ContestRoundSeconds + 0.6f;

        if (message.type == "WINDOW_CONTEST_START")
        {
            if (runtimeHud != null)
                runtimeHud.ShowCombatStart(round, payload.targetNodeId, payload.totalRounds, payload.contestType);
            if (!string.IsNullOrEmpty(payload.targetNodeId) && stations.TryGetValue(payload.targetNodeId, out MapStationAnchor contestStation))
            {
                // 缓存对抗节点位置：后续 WINDOW_CARD_REVEAL 没有 targetNodeId，靠它续期聚焦
                contestFocusPosition = contestStation.transform.position;
                hasContestFocus = true;
                SetEventFocus(contestFocusPosition, 4.5f, 15f);
                // 世界脉冲标签也按窗口类型分流（宫门/通行/资源/任务/交接/清障）
                SpawnEventPulse(contestFocusPosition, LycheeReplayRuntimeHud.ContestTypeLabel(payload.contestType) + "开始", new Color(1f, 0.78f, 0.22f), ResolveEventIcon("WINDOW_CONTEST_START", payload));
            }
            return;
        }

        if (message.type == "WINDOW_CARD_REVEAL")
        {
            if (runtimeHud != null)
                runtimeHud.ShowCombatReveal(round, payload.roundIndex, payload.redCard, payload.blueCard, payload.winner, payload.redPoint, payload.bluePoint);
            // 每次亮牌都续期拉近聚焦：多回合对抗中途镜头不回退远景，CONTEST_END 后自然过期
            if (hasContestFocus)
                SetEventFocus(contestFocusPosition, 4.5f, 15f);
            return;
        }

        if (message.type == "WINDOW_CONTEST_END")
        {
            if (runtimeHud != null)
                runtimeHud.ShowCombatEnd(round, payload.winner, payload.redPoint, payload.bluePoint);
            // 对抗结束：清掉缓存节点，避免跳转回放后过期位置被误用；聚焦计时到期镜头自然拉远
            hasContestFocus = false;
            return;
        }

        if (message.type == "WINDOW_CONTEST_DRAW")
        {
            if (runtimeHud != null)
                runtimeHud.ShowCombatDraw(round, payload.redPoint, payload.bluePoint, payload.restRound);
            hasContestFocus = false;
            return;
        }

        if (message.type == "SQUAD_DISPATCH")
        {
            // 开局首帧（Awake 内 ApplyFrame(0,true)）派发的小队延迟到出发动画结束再生成，避免凭空出现/中途消失。
            if (applyingInitialFrame && !openingDispatchesReleased)
            {
                pendingOpeningDispatches.Add(message);
                pendingOpeningRound = round;
                return;
            }
            int dispatchedCount = SquadMemberCount(payload);
            if (runtimeHud != null)
            {
                // 用干净队名（红队/蓝队），不带 #2002·blue-l4 内部串——状态条窄，verbose 名会撑爆溢出。
                runtimeHud.ShowDispatch(TeamShortName(payload.playerId) + " 派出小分队 x" + dispatchedCount + " · 正在 " + DisplayNode(payload.targetNodeId) + " 执行 " + SquadActionCn(payload.action));
            }
            ShowKeyEventIllustration(message, payload, round);
            ShowObstacleCauseCue(payload.targetNodeId);
            SquadVisual dispatched = SpawnSquad(payload, round);
            if (dispatched != null && dispatched.root != null)
            {
                // 相机带一下出征的小队：跟拍小队根节点 ~3.4s、拉近到 17m，让 3D 队员"出发"看得见，
                // 而不是只剩头顶一行飞字（旧代码这里是 ClearEventFocus()，镜头当场把小队甩回主车队）。
                SetEventFocus(dispatched.root.transform, 3.4f, 17f);
                SpawnEventPulse(dispatched.start, TeamName(payload.playerId) + "小队出发 x" + dispatched.memberCount, TeamColor(payload.playerId), ResolveEventIcon("SQUAD_DISPATCH", payload));
            }
            else if (!string.IsNullOrEmpty(payload.targetNodeId) &&
                stations.TryGetValue(payload.targetNodeId, out MapStationAnchor squadTarget))
                SpawnEventPulse(squadTarget.transform.position, "小队出发 x" + dispatchedCount, TeamColor(payload.playerId), ResolveEventIcon("SQUAD_DISPATCH", payload));
            return;
        }

        if (message.type.StartsWith("SQUAD_", StringComparison.Ordinal))
        {
            BackfillSquadPayloadPlayer(payload);
            if (runtimeHud != null)
            {
                string actCn = SquadActionCn(string.IsNullOrEmpty(payload.action) ? message.type : payload.action);
                string verb = message.type == "SQUAD_FAILED" ? "未能完成" : "完成";
                string extra = (message.type.Contains("WEAKEN") || message.type.Contains("REINFORCE"))
                    ? "  防御 " + payload.defenseBefore + " → " + payload.defenseAfter : "";
                // 「XX队小分队 在 站点名 完成/未能完成 动作」——带站点中文名。
                runtimeHud.ShowDispatch(TeamShortName(payload.playerId) + " 小分队 在 " + DisplayNode(payload.targetNodeId) + " " + verb + " " + actCn + extra);
            }
            if (message.type == "SQUAD_CLEAR" || message.type == "SQUAD_CLEAR_SUCCESS")
                ResolveSquadResult(payload, round, true);
            else if (message.type == "SQUAD_FAILED")
                ResolveSquadResult(payload, round, false);
        }

        if (message.type == "OBSTACLE_CLEAR")
        {
            if (!ResolveObstacleClearSquads(payload, round))
                ShowObstacleClearResult(payload);
        }

        ShowKeyEventIllustration(message, payload, round);

        string nodeId = !string.IsNullOrEmpty(payload.targetNodeId)
            ? payload.targetNodeId
            : payload.nodeId;
        if (string.IsNullOrEmpty(nodeId) && payload.playerId != 0)
        {
            LycheeReplayPlayerState player = frames[frameIndex].players?
                .FirstOrDefault(candidate => candidate.playerId == payload.playerId);
            nodeId = player?.currentNodeId;
        }
        if (!string.IsNullOrEmpty(nodeId) && stations.TryGetValue(nodeId, out MapStationAnchor station))
        {
            // 对抗类事件（设卡/攻坚/窗口/强行）延长聚焦时间并拉近到 15 米，让血条与对抗演出留在镜头里；
            // 普通事件给 30 米只轻微居中——平时保持远景看全局，是演出节奏的硬要求。
            bool combatEvent = message.type.Contains("GUARD") ||
                message.type.Contains("WINDOW") ||
                message.type.Contains("FORCED") ||
                message.type == "SQUAD_CLEAR" ||
                message.type == "SQUAD_CLEAR_SUCCESS" ||
                message.type == "SQUAD_FAILED" ||
                message.type == "OBSTACLE_CLEAR" ||
                message.type == "DELIVER_SUCCESS" ||
                message.type == "RUSH_START" ||
                message.type == "RUSH_PHASE_START";
            // 只有突发/结果类事件才拉近抢镜头；资源领取、站点处理只给图卡和地图特效，不打断常态远景。
            if (combatEvent)
                SetEventFocus(station.transform.position, 3.8f, 15f);
            if (ShouldShowWorldPulse(message.type))
                SpawnEventPulse(station.transform.position, WorldEventLabel(message.type, payload), EventColor(message.type), ResolveEventIcon(message.type, payload), EventPulseStatus(message.type));
        }
    }

    static bool ShouldShowWorldPulse(string eventType)
    {
        if (string.IsNullOrEmpty(eventType)) return false;
        if (eventType.Contains("GUARD") || eventType.Contains("WINDOW") || eventType.Contains("FORCED")) return true;
        if (eventType.Contains("TASK") || eventType.Contains("BOUNTY")) return true;
        if (eventType.Contains("DELIVER") || eventType.Contains("VERIFY")) return true;
        if (eventType == "PROCESS_START" || eventType == "PROCESS_COMPLETE") return true;
        if (eventType == "RESOURCE_CLAIM" || eventType == "RESOURCE_USE") return true;
        if (eventType == "SQUAD_CLEAR" ||
            eventType == "SQUAD_CLEAR_SUCCESS" ||
            eventType == "SQUAD_FAILED" ||
            eventType == "OBSTACLE_CLEAR" ||
            eventType == "RUSH_START" ||
            eventType == "RUSH_PHASE_START" ||
            eventType == "RUSH_TACTIC_USE" ||
            eventType == "ACTION_REJECTED" ||
            eventType == "INVALID_ACTION")
            return true;
        return false;
    }

    static string EventPulseStatus(string eventType)
    {
        if (string.IsNullOrEmpty(eventType)) return null;
        if (eventType == "ACTION_REJECTED" ||
            eventType == "INVALID_ACTION" ||
            eventType == "SQUAD_FAILED" ||
            eventType == "PROCESS_INTERRUPT" ||
            eventType == "TASK_EXPIRE" ||
            eventType == "TASK_TARGET_LOST" ||
            eventType.Contains("TARGET_LOST") ||
            eventType.EndsWith("_EXPIRE", StringComparison.Ordinal) ||
            eventType.EndsWith("_EXPIRED", StringComparison.Ordinal) ||
            eventType.EndsWith("_FAIL", StringComparison.Ordinal) ||
            eventType.EndsWith("_FAILED", StringComparison.Ordinal))
            return "fail";
        if (eventType == "TASK_COMPLETE" ||
            eventType == "BOUNTY_COMPLETE" ||
            eventType == "BOUNTY_CLAIM" ||
            eventType == "RESOURCE_CLAIM" ||
            eventType == "VERIFY_GATE_COMPLETE" ||
            eventType == "PROCESS_COMPLETE" ||
            eventType == "OBSTACLE_CLEAR" ||
            eventType == "SQUAD_CLEAR" ||
            eventType == "SQUAD_CLEAR_SUCCESS" ||
            eventType == "DELIVER_SUCCESS" ||
            eventType.EndsWith("_SUCCESS", StringComparison.Ordinal) ||
            eventType.EndsWith("_COMPLETE", StringComparison.Ordinal))
            return "success";
        return null;
    }

    void ShowKeyEventIllustration(LycheeReplayMessage message, LycheeReplayPayload payload, int round)
    {
        if (runtimeHud == null || message == null) return;
        string type = message.type;
        if (string.IsNullOrEmpty(type) ||
            type == "MATCH_START" ||
            type.StartsWith("WINDOW_", StringComparison.Ordinal))
            return;

        // 低价值事件（天气预报/守卫衰减/成本类）不再进中央弹窗：信息密度低还连环刷屏遮挡战场，
        // 左侧事件流（LycheeHudExtensions 自己订阅 ReplayEventRaised）仍会展示，不丢信息。
        if (type == "WEATHER_FORECAST" ||
            type == "GUARD_DECAY" ||
            type == "GUARD_WEATHERING" ||   // V4.8.12 新名（旧 GUARD_DECAY），同样不进中央弹窗
            type.StartsWith("COST_", StringComparison.Ordinal))
            return;
        if (type == "PROCESS_PROGRESS" && !ShouldShowProcessProgressCard(payload, round))
            return;

        // 用户(点9)：右侧事件卡片只保留这几类，其余(到达/天气/处理/小分队/强制通行/交付/冲刺/
        // 资源使用/任务过期/悬赏出现 等)不再弹中央卡；左侧事件流仍有文字、不丢信息。
        // 窗口争夺(WINDOW_*)走对抗演出系统、已在上面 return，单独设计展示。
        if (type != "TASK_SPAWN" && type != "TASK_REFRESH" &&
            type != "TASK_COMPLETE" &&
            type != "BOUNTY_COMPLETE" && type != "BOUNTY_CLAIM" &&
            type != "RESOURCE_CLAIM" &&
            type != "GUARD_SET" && type != "GUARD_BREAK" &&
            type != "VERIFY_GATE_COMPLETE")
            return;

        payload = payload ?? new LycheeReplayPayload();
        if (type.StartsWith("SQUAD_", StringComparison.Ordinal))
            BackfillSquadPayloadPlayer(payload);
        string title = null;
        string subtitle = "";
        string left = "";
        string right = "";
        string center = "R" + round;
        int imageIndex = 1;
        // 用户：卡片"停留太短"，要"保留时长久一些"。事件已收窄到 8 类、不会连环刷屏，
        // 默认提到 7.5 秒更从容(HUD 端 CombatMaxSeconds 同步放到 8.0f 不再压回)。
        float seconds = 7.5f;
        string target = DisplayNode(!string.IsNullOrEmpty(payload.targetNodeId) ? payload.targetNodeId : payload.nodeId);
        string team = TeamName(payload.playerId);
        bool priorityCard = IsPriorityEventCard(type);

        if (type == "NODE_ENTER")
        {
            title = "到达节点";
            subtitle = team + " 进入 " + target;
            left = "节点";
            right = "路线推进";
            center = "到达";
            imageIndex = 1;
            seconds = 2.2f;
        }
        else if (type == "TASK_SPAWN" || type == "TASK_REFRESH")
        {
            string taskName = TaskNameWithSuffix(TaskDisplayName(payload));
            int taskScore = TaskScore(payload);
            title = "皇榜任务刷新";
            subtitle = target + "位置已公开" + taskName;
            left = taskName;
            right = ScoreText(taskScore);
            center = "公开";
        }
        else if (type == "TASK_COMPLETE")
        {
            title = "皇榜任务完成";
            subtitle = team + " 完成 " + ShortId(payload.taskId, "任务");
            left = ShortId(payload.taskId, "任务");
            right = ScoreText(payload.score);
            center = "+" + Mathf.Max(0, payload.score);
            imageIndex = 2;
        }
        else if (type == "WEATHER_START")
        {
            title = "天气生效";
            subtitle = WeatherName(payload.weatherType) + WeatherCoverageText(payload.region);
            left = "鲜度损耗变化";
            right = DurationText(payload.durationRound);
        }
        else if (type == "WEATHER_END")
        {
            title = "天气结束";
            subtitle = WeatherName(payload.weatherType) + " 已退场";
            left = string.IsNullOrEmpty(RegionName(payload.region)) ? "天气影响" : RegionName(payload.region);
            right = "恢复常规";
        }
        else if (type == "RESOURCE_CLAIM")
        {
            title = "资源获取";
            subtitle = team + " 获取 " + target + " " + ResourceName(payload.resourceType);
            left = ResourceName(payload.resourceType);
            right = "库存更新";
            // 底部条用「队色·队名 · 资源名」替代回合号 R x（用户点2）。用干净队名（红队/蓝队），不带内部串。
            center = TeamColoredFooter(payload.playerId, TeamShortName(payload.playerId) + " · " + ResourceName(payload.resourceType));
        }
        else if (type == "RESOURCE_USE")
        {
            title = "使用补给";
            subtitle = team + " 在 " + target + " 施用补给";
            left = ResourceName(payload.resourceType);
            right = "资源消耗";
        }
        else if (type == "PROCESS_START")
        {
            title = "站点处理开始";
            subtitle = team + " 在 " + target + " 执行 " + ActionName(payload.action);
            left = "处理中";
            right = DurationText(payload.processRound);
        }
        else if (type == "PROCESS_PROGRESS")
        {
            title = "站点行动";
            subtitle = team + " 在 " + target + " 执行 " + ActionName(payload.processType);
            left = "剩余";
            right = payload.remainingRound >= 0 ? payload.remainingRound + "回合" : "即将完成";
            center = ActionName(payload.processType);
            imageIndex = 1;
            seconds = 1.8f;
        }
        else if (type == "PROCESS_COMPLETE")
        {
            title = "站点处理完成";
            subtitle = team + " 完成 " + ActionName(payload.action);
            left = target;
            right = "继续前进";
            imageIndex = 2;
        }
        else if (type == "PROCESS_INTERRUPT")
        {
            title = "站点处理中断";
            subtitle = team + " 在 " + target + " 的处理被打断";
            left = ActionName(payload.processType);
            right = "流程受阻";
        }
        else if (type == "SQUAD_DISPATCH")
        {
            title = team + "小分队出发";
            subtitle = team + " 派出 " + SquadMemberCount(payload) + " 人前往 " + target;
            left = TacticName(PayloadTactic(payload));
            right = "预计 R" + PayloadArrivalRound(payload, round);
            center = "出发";
            imageIndex = 1;
            seconds = 3.0f;
        }
        else if (type == "SQUAD_ARRIVE")
        {
            title = team + "小分队抵达";
            subtitle = team + " 抵达 " + target;
            left = TacticName(payload.tactic);
            right = "准备生效";
            center = "抵达";
            imageIndex = 1;
            seconds = 3.0f; // 抵达卡片够时长看清"谁到了哪、要干啥"
        }
        else if (type == "OBSTACLE_CLEAR" || type == "SQUAD_CLEAR" || type == "SQUAD_CLEAR_SUCCESS")
        {
            title = team + "清障成功";
            subtitle = team + " 清除 " + target + " 障碍";
            left = target;
            right = "路线恢复";
            center = "成功";
            imageIndex = 2;
            seconds = 3.2f;
        }
        else if (type == "SQUAD_FAILED")
        {
            title = team + "小分队失败";
            subtitle = team + " 未能完成 " + target + " 的" + TacticName(PayloadTactic(payload));
            left = TacticName(PayloadTactic(payload));
            right = "未达成";
            center = "失败";
            imageIndex = 0;
            seconds = 3.2f;
        }
        else if (type.StartsWith("SQUAD_", StringComparison.Ordinal))
        {
            title = team + "小分队战术生效";
            subtitle = ShortEventName(type) + "  " + target;
            left = payload.defenseBefore != 0 || payload.defenseAfter != 0 ? "防御 " + payload.defenseBefore : team;
            right = payload.defenseBefore != 0 || payload.defenseAfter != 0 ? "→ " + payload.defenseAfter : "行动完成";
            center = "完成";
            imageIndex = 2;
        }
        else if (type == "GUARD_SET")
        {
            title = "设卡布防";
            subtitle = team + " 在 " + target + " 设卡";
            left = "防御";
            right = payload.defense > 0 ? payload.defense.ToString() : "已部署";
            imageIndex = 0;
        }
        else if (type == "GUARD_BREAK")
        {
            title = "攻坚破卡";
            subtitle = team + " 在攻击 " + target + " 防守";
            left = "攻坚 " + payload.attackValue;
            right = payload.defenseBefore + " → " + payload.defenseAfter;
            imageIndex = 2;
        }
        else if (type == "BOUNTY_SPAWN" || type == "BOUNTY_CREATE")   // BOUNTY_CREATE 为 V4.8.12 新名
        {
            title = "破关悬赏出现";
            subtitle = target + " 进入悬赏窗口";
            left = BountyName(payload.bountyType);
            right = ScoreText(payload.score);
            imageIndex = 0;
        }
        else if (type == "BOUNTY_COMPLETE" || type == "BOUNTY_CLAIM") // BOUNTY_CLAIM 为 V4.8.12 新名
        {
            title = "破关悬赏完成";
            subtitle = team + " 完成破关悬赏";
            left = ScoreText(payload.score);
            right = ResourceName(payload.reward);
            imageIndex = 2;
        }
        else if (type == "FORCED_PASS_START")
        {
            title = "强制通行";
            subtitle = team + " 强行通过 " + target;
            left = "时间税";
            right = payload.timeTax > 0 ? payload.timeTax + "回合" : "额外等待";
            imageIndex = 0;
        }
        else if (type == "FORCED_PASS_END")
        {
            title = "强制通行结束";
            subtitle = team + " 恢复推进";
            left = target;
            right = "道路重新开放";
            imageIndex = 2;
        }
        else if (type == "ACTION_REJECTED" || type == "INVALID_ACTION")
        {
            title = "行动被拒绝";
            subtitle = ActionName(payload.action);
            left = ErrorCodeName(payload.errorCode);
            right = string.IsNullOrEmpty(payload.message) ? "无效试探" : ShortDetail(payload.message, 16);
            imageIndex = 0;
        }
        else if (type == "RUSH_PHASE_START" || type == "RUSH_START")
        {
            title = "宫宴冲刺开启";
            subtitle = "终局阶段开始，车队向长安冲刺";
            left = "冲刺";
            right = "争分夺秒";
            imageIndex = 2;
            seconds = 3.2f;
        }
        else if (type == "VERIFY_GATE_START")
        {
            title = "宫门验核开始";
            subtitle = team + " 在 " + target + " 提交验核";
            left = string.IsNullOrEmpty(payload.rushTactic) ? "验核" : ActionName(payload.rushTactic);
            right = DurationText(payload.processRound);
            imageIndex = 1;
        }
        else if (type == "VERIFY_GATE_COMPLETE")
        {
            title = "宫门核验完成";
            subtitle = team + " 完成宫门核验，获得交付资格";
            left = target;
            right = payload.verifiedEffectiveRound > 0 ? "R" + payload.verifiedEffectiveRound + " 生效" : "已生效";
            // 底部条用「队色·队名 · 核验完成」替代回合号 R x（用户点3）。用干净队名（红队/蓝队）。
            center = TeamColoredFooter(payload.playerId, TeamShortName(payload.playerId) + " · 核验完成");
            imageIndex = 2;
        }
        else if (type == "DELIVER_SUCCESS")
        {
            title = "兴庆宫交付";
            subtitle = team + " 完成荔枝交付";
            left = "好果 " + payload.goodFruit;
            right = "鲜度 " + payload.freshness.ToString("0.#") + "%";
            center = payload.score > 0 ? payload.score.ToString() : center;
            imageIndex = 2;
            seconds = 3.2f;
        }
        else if (type == "MATCH_END")
        {
            title = "比赛结束";
            subtitle = TeamName(payload.winnerPlayerId) + " 胜出";
            left = "回放完成";
            right = "最终结算";
            imageIndex = 2;
            seconds = 3.5f;
        }
        else if (IsMajorEvent(type))
        {
            title = ShortEventName(type);
            subtitle = target;
            left = team;
            right = "状态更新";
        }

        // 非对抗事件用各自的专属插画（Illustrations/card_*.png）；解析不到就把 imageIndex 置 -1 让 HUD 隐藏图片，
        // 避免硬套对抗插画。对抗/清障/破卡/失败仍走 combatSprites。
        Sprite eventIllustration = null;
        if (!EventUsesCombatIllustration(type))
        {
            imageIndex = -1;
            eventIllustration = ResolveEventIllustration(type, payload);
        }

        if (!string.IsNullOrEmpty(title))
        {
            if (priorityCard)
                runtimeHud.ShowPriorityEventIllustration(imageIndex, title, subtitle, left, right, center, seconds, EventBorderCategory(type), eventIllustration);
            else
                runtimeHud.ShowEventIllustration(imageIndex, title, subtitle, left, right, center, seconds, EventBorderCategory(type), eventIllustration);
        }
    }

    static bool IsPriorityEventCard(string type)
    {
        return type == "SQUAD_DISPATCH" ||
            type == "SQUAD_ARRIVE" ||
            type == "SQUAD_CLEAR" ||
            type == "SQUAD_CLEAR_SUCCESS" ||
            type == "SQUAD_FAILED" ||
            type == "OBSTACLE_CLEAR";
    }

    /// <summary>该事件是否用"对抗插画"做卡片图：仅真·对抗/清障/破卡类。其余事件不显示对抗图，
    /// 等各自专属插画就位后再单独接入。</summary>
    static bool EventUsesCombatIllustration(string type)
    {
        if (string.IsNullOrEmpty(type)) return false;
        // FORCED_PASS 与 SQUAD_FAILED 现各有专属插画（combat_forced_pass / combat_squad_failed），
        // 改走 StreamingAssets 插画路径（EventIllustrationPath），不再共用 combatSprites 对抗图集。
        return type == "SQUAD_CLEAR" ||
            type == "SQUAD_CLEAR_SUCCESS" ||
            type == "OBSTACLE_CLEAR" ||
            type == "GUARD_BREAK";
    }

    bool ShouldShowProcessProgressCard(LycheeReplayPayload payload, int round)
    {
        payload = payload ?? new LycheeReplayPayload();
        string nodeId = !string.IsNullOrEmpty(payload.targetNodeId) ? payload.targetNodeId : payload.nodeId;
        string key = payload.playerId + "|" + nodeId + "|" + payload.processType;
        if (processCardRoundByKey.TryGetValue(key, out int lastRound) && round - lastRound < 6)
        {
            if (payload.remainingRound <= 1 && round > lastRound)
            {
                processCardRoundByKey[key] = round;
                return true;
            }
            return false;
        }
        processCardRoundByKey[key] = round;
        return true;
    }

    void SetEventFocus(Vector3 position, float seconds = 2.8f, float focusDistance = -1f)
    {
        eventFocusTarget = null;
        eventFocusPosition = position;
        eventFocusUntil = Time.unscaledTime + seconds;
        // 按事件类型区分聚焦距离：对抗类传 15 拉近看战斗，普通事件传 30 只轻微居中、保住远景；-1 由相机自行决定
        eventFocusDistance = focusDistance;
        AddRecentEventFocus(null, position, seconds, focusDistance);
    }

    void SetEventFocus(Transform target, float seconds = 2.8f, float focusDistance = -1f)
    {
        if (target == null) return;
        eventFocusTarget = target;
        eventFocusPosition = target.position;
        eventFocusUntil = Time.unscaledTime + seconds;
        eventFocusDistance = focusDistance;
        AddRecentEventFocus(target, target.position, seconds, focusDistance);
    }

    void ClearEventFocus()
    {
        eventFocusTarget = null;
        eventFocusUntil = 0f;
        eventFocusDistance = -1f;
        recentEventFocuses.Clear();
    }

    void AddRecentEventFocus(Transform target, Vector3 position, float seconds, float focusDistance)
    {
        float until = Time.unscaledTime + Mathf.Max(1.2f, seconds);
        if (target != null)
        {
            for (int i = 0; i < recentEventFocuses.Count; i++)
            {
                EventFocusAnchor existing = recentEventFocuses[i];
                if (existing == null || existing.target != target) continue;
                existing.position = position;
                existing.until = Mathf.Max(existing.until, until);
                existing.focusDistance = focusDistance;
                return;
            }
        }
        recentEventFocuses.Add(new EventFocusAnchor
        {
            target = target,
            position = position,
            until = until,
            focusDistance = focusDistance
        });
        while (recentEventFocuses.Count > 8)
            recentEventFocuses.RemoveAt(0);
    }

    SquadVisual SpawnSquad(LycheeReplayPayload payload, int round)
    {
        // 目标站必须有（否则无处攻坚）；注意 Dictionary.TryGetValue 传 null key 会抛 ArgumentNullException，
        // 务必先判空——新版回放小队派发只给 targetNodeId，fromNodeId 缺省会导致初始化崩溃卡死。
        if (string.IsNullOrEmpty(payload.targetNodeId) ||
            !stations.TryGetValue(payload.targetNodeId, out MapStationAnchor target)) return null;
        // 出发点：有 fromNodeId 用站点；缺省时优先用该玩家当前所在节点。
        // 开局回放常见形态是 currentNodeId=S01、routeEdgeId 已经有少量进度；若直接取车队插值位置，
        // 观感会变成“小分队从路中间冒出来”，而不是从起点/驿站派出。
        Vector3 startPos;
        Vector3 endPos = target.transform.position + Vector3.up * 0.15f;
        if (TryResolveSquadStartStation(payload, out MapStationAnchor fromStation))
            startPos = fromStation.transform.position + Vector3.up * 0.15f;
        else
        {
            Transform convoy = GetConvoyRoot(payload.playerId);
            if (convoy != null)
                startPos = convoy.position + Vector3.up * 0.15f;
            else
                startPos = LocalSquadStagingPosition(target.transform.position, payload, round);
        }
        ApplySquadLaneOffset(ref startPos, ref endPos, payload);
        GameObject root = new GameObject("Squad_" + PayloadTactic(payload) + "_" + round);
        root.transform.SetParent(eventRoot, false);
        root.transform.position = startPos;
        Vector3 initialDirection = endPos - startPos;
        initialDirection.y = 0f;
        if (initialDirection.sqrMagnitude > 0.001f)
            root.transform.rotation = Quaternion.LookRotation(initialDirection.normalized, Vector3.up);
        // 水面状态要在 NotifySquadSpawned 之前挂上，骨骼系统建组时会缓存它的引用
        LycheeSquadWaterState waterState = root.AddComponent<LycheeSquadWaterState>();
        int count = SquadMemberCount(payload);
        Color tint = TeamColor(payload.playerId);
        int columns = Mathf.Clamp(Mathf.CeilToInt(Mathf.Sqrt(count)), 2, 5);
        for (int i = 0; i < count; i++)
        {
            // 放大队员（1.15→1.6）：远处小队不再缩成看不清的一小撮，远看也认得出是人。
            GameObject member = CreateNormalizedModel(squadMemberModel, root.transform, "Member_" + i, 1.6f);
            if (member != null)
            {
                float x = (i % columns - (columns - 1) * 0.5f) * 0.72f;
                float z = (i / columns - 0.5f) * 0.72f;
                member.transform.localPosition = new Vector3(x, 0f, z);
                TintModel(member, tint, 0.18f);
            }
        }
        // 脚下队色光圈：远处也能一眼看出"这儿有一支红/蓝小队"，而不是只剩头顶一行字（复用车队同款光圈）。
        CreateTeamRing(root.transform, tint.r > tint.b ? 0 : 1);
        // 成员全部创建完后通知骨骼动画系统接管（Member_* 是事件期临时对象，LycheeCharacterMotion
        // 的车队扫描覆盖不到，需要显式推送；对方实现为安全空操作兜底，没有骨骼也不会报错）
        LycheeCharacterMotion.NotifySquadSpawned(root);
        TextMesh label = CreateLabel(root.transform, TeamName(payload.playerId) + "小分队 x" + count + "\n" + TacticName(PayloadTactic(payload)) + " → " + DisplayNode(payload.targetNodeId), new Color(1f, 0.82f, 0.22f));
        SquadVisual visual = new SquadVisual
        {
            root = root,
            start = startPos,
            end = endPos,
            startRound = round,
            endRound = PayloadArrivalRound(payload, round),
            playerId = payload.playerId,
            memberCount = count,
            orderId = payload.orderId,
            targetNodeId = payload.targetNodeId,
            tactic = PayloadTactic(payload),
            waterState = waterState,
            label = label
        };
        squads.Add(visual);
        return visual;
    }

    bool TryResolveSquadStartStation(LycheeReplayPayload payload, out MapStationAnchor station)
    {
        station = null;
        if (payload == null) return false;

        if (!string.IsNullOrEmpty(payload.fromNodeId) &&
            stations.TryGetValue(payload.fromNodeId, out station) &&
            station != null)
            return true;

        if (TryGetPlayerCurrentStation(frameIndex, payload.playerId, out station)) return true;
        if (TryGetPlayerCurrentStation(frameIndex - 1, payload.playerId, out station)) return true;
        if (TryGetPlayerCurrentStation(0, payload.playerId, out station)) return true;

        return stations.TryGetValue("S01", out station) && station != null;
    }

    bool TryGetPlayerCurrentStation(int index, int playerId, out MapStationAnchor station)
    {
        station = null;
        if (index < 0 || index >= frames.Count) return false;
        LycheeReplayFrame frame = frames[index];
        if (frame == null || frame.players == null) return false;
        LycheeReplayPlayerState state = frame.players.FirstOrDefault(player => player != null && player.playerId == playerId);
        if (state == null || string.IsNullOrEmpty(state.currentNodeId)) return false;
        return stations.TryGetValue(state.currentNodeId, out station) && station != null;
    }

    static void ApplySquadLaneOffset(ref Vector3 startPos, ref Vector3 endPos, LycheeReplayPayload payload)
    {
        Vector3 direction = endPos - startPos;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.001f) return;
        Vector3 side = Vector3.Cross(Vector3.up, direction.normalized);
        float sign = payload != null && payload.playerId >= 2000 ? 1f : -1f;
        startPos += side * sign * 0.55f;
        endPos += side * sign * 0.35f;
    }

    float SquadFocusSeconds(SquadVisual squad, int round)
    {
        if (squad == null) return 3.0f;
        int rounds = Mathf.Max(1, squad.endRound - round);
        return Mathf.Clamp(rounds * Mathf.Max(0.12f, secondsPerRound), 2.8f, 7.0f);
    }

    static Vector3 LocalSquadStagingPosition(Vector3 targetPosition, LycheeReplayPayload payload, int round)
    {
        int playerSeed = payload == null ? 0 : payload.playerId;
        float angle = Mathf.Repeat(playerSeed * 0.017f + round * 1.37f, Mathf.PI * 2f);
        Vector3 radial = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
        return targetPosition + radial * 3.2f + Vector3.up * 0.15f;
    }

    void UpdateSquads(float interpolation, float visualDelta)
    {
        float replayRound = CurrentRound + interpolation;
        for (int i = squads.Count - 1; i >= 0; i--)
        {
            SquadVisual squad = squads[i];
            if (squad.root == null)
            {
                squads.RemoveAt(i);
                continue;
            }
            if (squad.dashing)
            {
                // 结算冲刺：从当前位置平滑滑到结算节点，替代旧的瞬移闪现；到位后转入抵达/攻击演出。
                if (visualDelta > 0f) squad.dashElapsed += visualDelta;
                float dprog = Mathf.Clamp01(squad.dashElapsed / Mathf.Max(0.01f, squad.dashSeconds));
                Vector3 dpos = Vector3.Lerp(squad.dashFrom, squad.end, Mathf.SmoothStep(0f, 1f, dprog));
                if (elevation != null) dpos.y = elevation.SampleWorldHeight(dpos.x, dpos.z) + 0.15f;
                squad.root.transform.position = dpos;
                Vector3 ddir = squad.end - squad.dashFrom; ddir.y = 0f;
                if (ddir.sqrMagnitude > 0.001f)
                    squad.root.transform.rotation = Quaternion.LookRotation(ddir.normalized, Vector3.up);
                AnimateSquadMembers(squad, false, false, false, visualDelta);
                if (dprog >= 1f)
                {
                    squad.dashing = false;
                    squad.arrivedRealTime = Time.time;
                    squad.arrivedElapsed = 0f;
                }
                continue;
            }
            if (squad.arrivedRealTime < 0f)
            {
                float duration = Mathf.Max(1f, squad.endRound - squad.startRound);
                float t = Mathf.Clamp01((replayRound - squad.startRound) / duration);
                float eased = Mathf.SmoothStep(0f, 1f, t);
                Vector3 position = Vector3.Lerp(squad.start, squad.end, eased);
                // 跨河段：人物坐船贴水面滑行，不走陆路的抛物线跃迁，否则像泡在河里行军
                bool onWater = TrySampleWaterSurface(position.x, position.z, out float waterY);
                if (onWater)
                {
                    position.y = waterY + 0.05f + Mathf.Sin(squad.animTime * 1.8f) * 0.03f; // 贴水 + 随波轻浮沉
                    EnsureSquadBoat(squad);
                    if (squad.boat != null && !squad.boat.activeSelf) squad.boat.SetActive(true);
                }
                else
                {
                    if (elevation != null)
                        position.y = elevation.SampleWorldHeight(position.x, position.z) + 0.15f;
                    else
                        position.y = Mathf.Lerp(squad.start.y, squad.end.y, eased);
                    if (squad.boat != null && squad.boat.activeSelf) squad.boat.SetActive(false);
                }
                if (squad.waterState != null) squad.waterState.onWater = onWater;
                squad.root.transform.position = position;
                Vector3 direction = squad.end - squad.start;
                if (direction.sqrMagnitude > 0.001f)
                    squad.root.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
                AnimateSquadMembers(squad, false, onWater, false, visualDelta);
                if (t >= 1f)
                {
                    // 抵达后停留做攻击突刺演出，配合防御浮字/血条下降一起读，而不是 0.2 秒就消失
                    squad.arrivedRealTime = Time.time;
                    squad.arrivedElapsed = 0f;
                    SpawnEventPulse(squad.end, "小队抵达 x" + squad.memberCount, new Color(1f, 0.72f, 0.16f), ResolveEventIcon("SQUAD_ARRIVE"));
                    SpawnClearActionFx(squad.end, TeamColor(squad.playerId), "清障中", true);
                    SetEventFocus(squad.root.transform, 2.8f, 13.5f);
                    if (!string.IsNullOrEmpty(squad.targetNodeId))
                        LycheeContestBuildingXray.RequestXray(squad.targetNodeId, 3.0f);
                }
            }
            else
            {
                AnimateSquadMembers(squad, true, false, squad.resultResolved && !squad.resultSuccess, visualDelta);
                if (visualDelta > 0f) squad.arrivedElapsed += visualDelta;
                if (squad.arrivedElapsed > squad.holdSeconds)
                {
                    Destroy(squad.root);
                    squads.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>程序化小队动作：行军时交替起伏+前倾步伐，攻击时朝目标方向突刺，船上时站立随波。仅改 Y 坐标/旋转/缩放，不破坏阵型。</summary>
    static void AnimateSquadMembers(SquadVisual squad, bool attacking, bool onWater, bool failed = false, float visualDelta = 0f)
    {
        if (visualDelta > 0f) squad.animTime += visualDelta;
        Transform root = squad.root.transform;
        for (int m = 0; m < root.childCount; m++)
        {
            Transform member = root.GetChild(m);
            if (!member.name.StartsWith("Member_", StringComparison.Ordinal)) continue;
            float phase = squad.animTime * (attacking ? 8f : 10f) + m * 1.7f;
            if (failed)
            {
                Vector3 memberPosition = member.localPosition;
                memberPosition.y = Mathf.Sin(phase * 0.35f) * 0.018f;
                member.localPosition = memberPosition;
                member.localRotation = Quaternion.Euler(18f + Mathf.Sin(phase * 0.4f) * 3f, Mathf.Sin(m * 7.3f) * 8f, Mathf.Sin(phase * 0.3f) * 5f);
                member.localScale = new Vector3(1f, 0.9f, 1f);
            }
            else if (attacking)
            {
                float lunge = Mathf.Max(0f, Mathf.Sin(phase));
                member.localRotation = Quaternion.Euler(lunge * 24f, Mathf.Sin(m * 7.3f) * 8f, 0f);
                member.localScale = Vector3.one * (1f + lunge * 0.08f);
                Vector3 memberPosition = member.localPosition;
                memberPosition.y = lunge * 0.06f;
                member.localPosition = memberPosition;
            }
            else if (onWater)
            {
                // 站在甲板上：抬到船舷高度，只随波轻晃，不迈步
                Vector3 memberPosition = member.localPosition;
                memberPosition.y = 0.24f + Mathf.Sin(phase * 0.32f) * 0.015f;
                member.localPosition = memberPosition;
                member.localRotation = Quaternion.Euler(0f, Mathf.Sin(m * 7.3f) * 8f, Mathf.Sin(phase * 0.28f) * 2.5f);
                member.localScale = Vector3.one;
            }
            else
            {
                Vector3 memberPosition = member.localPosition;
                memberPosition.y = Mathf.Abs(Mathf.Sin(phase)) * 0.12f;
                member.localPosition = memberPosition;
                member.localRotation = Quaternion.Euler(6f, Mathf.Sin(m * 7.3f) * 8f, Mathf.Sin(phase) * 4f);
                member.localScale = Vector3.one;
            }
        }
    }

    void ResolveSquadResult(LycheeReplayPayload payload, int round, bool success)
    {
        payload = payload ?? new LycheeReplayPayload();
        SquadVisual squad = FindMatchingSquad(payload);
        if (payload.playerId == 0 && squad != null)
            payload.playerId = squad.playerId;
        Vector3 focusPosition = squad != null ? squad.end : transform.position;
        if (squad == null &&
            !string.IsNullOrEmpty(payload.targetNodeId) &&
            stations.TryGetValue(payload.targetNodeId, out MapStationAnchor target))
            focusPosition = target.transform.position + Vector3.up * 0.15f;

        Color color = success ? TeamColor(payload.playerId) : new Color(1f, 0.24f, 0.16f, 1f);
        string label = success ? TeamName(payload.playerId) + "清障成功" : TeamName(payload.playerId) + "小队失败";
        SpawnClearActionFx(focusPosition, color, label, success);

        if (squad == null || squad.root == null)
        {
            SetEventFocus(focusPosition, 3.0f, 14f);
            return;
        }

        squad.resultResolved = true;
        squad.resultSuccess = success;
        squad.holdSeconds = success ? 4.0f : 3.6f;
        squad.endRound = Mathf.Min(squad.endRound, round);
        if (squad.arrivedRealTime < 0f && !squad.dashing)
        {
            // 旧实现这里直接 squad.root.transform.position = focusPosition 瞬移到节点（"走一两步就闪现到节点"）。
            // 改为从当前位置平滑冲刺到节点：距离越远略长、封顶 ~0.6s，到位后再进入攻击/结算演出。
            Vector3 here = squad.root.transform.position;
            float dist = Vector3.Distance(here, focusPosition);
            if (dist > 0.4f)
            {
                squad.dashing = true;
                squad.dashFrom = here;
                squad.dashStartTime = Time.time;
                squad.dashElapsed = 0f;
                squad.dashSeconds = Mathf.Clamp(dist / 14f, 0.18f, 0.6f);
            }
            else
            {
                squad.root.transform.position = focusPosition;
                squad.arrivedRealTime = Time.time;
                squad.arrivedElapsed = 0f;
            }
        }
        else
        {
            squad.root.transform.position = focusPosition;
            if (squad.arrivedRealTime < 0f) squad.arrivedRealTime = Time.time;
            if (squad.arrivedRealTime >= 0f) squad.arrivedElapsed = 0f;
        }
        Vector3 direction = squad.end - squad.start;
        direction.y = 0f;
        if (direction.sqrMagnitude > 0.001f)
            squad.root.transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
        if (squad.label != null)
            squad.label.text = TeamName(squad.playerId) + "小分队\n" + (success ? "清障成功" : "行动失败");
        if (!string.IsNullOrEmpty(squad.targetNodeId))
            LycheeContestBuildingXray.RequestXray(squad.targetNodeId, 3.2f);
        SetEventFocus(squad.root.transform, success ? 3.4f : 3.2f, 13.5f);
    }

    void ShowObstacleClearResult(LycheeReplayPayload payload)
    {
        payload = payload ?? new LycheeReplayPayload();
        Vector3 focusPosition = transform.position;
        if (!string.IsNullOrEmpty(payload.targetNodeId) &&
            stations.TryGetValue(payload.targetNodeId, out MapStationAnchor target))
            focusPosition = target.transform.position + Vector3.up * 0.15f;
        SpawnClearActionFx(focusPosition, TeamColor(payload.playerId), TeamName(payload.playerId) + "清障成功", true);
        SetEventFocus(focusPosition, 3.0f, 14f);
    }

    bool ResolveObstacleClearSquads(LycheeReplayPayload payload, int round)
    {
        payload = payload ?? new LycheeReplayPayload();
        if (string.IsNullOrEmpty(payload.targetNodeId)) return false;

        SquadVisual primary = null;
        for (int i = squads.Count - 1; i >= 0; i--)
        {
            SquadVisual candidate = squads[i];
            if (candidate == null || candidate.root == null || candidate.resultResolved) continue;
            if (candidate.targetNodeId != payload.targetNodeId) continue;
            if (payload.playerId != 0 && candidate.playerId != payload.playerId) continue;
            primary = candidate;
            break;
        }

        if (primary == null)
        {
            for (int i = squads.Count - 1; i >= 0; i--)
            {
                SquadVisual candidate = squads[i];
                if (candidate == null || candidate.root == null || candidate.resultResolved) continue;
                if (candidate.targetNodeId == payload.targetNodeId)
                {
                    primary = candidate;
                    break;
                }
            }
        }

        bool handled = false;
        if (primary != null)
        {
            if (payload.playerId == 0)
                payload.playerId = primary.playerId;
            ResolveSquadResult(payload, round, true);
            handled = true;
        }

        for (int i = squads.Count - 1; i >= 0; i--)
        {
            SquadVisual squad = squads[i];
            if (squad == null || squad.root == null || squad == primary) continue;
            if (squad.resultResolved || squad.targetNodeId != payload.targetNodeId) continue;
            SpawnEventPulse(squad.root.transform.position, TeamName(squad.playerId) + "小队撤回", TeamColor(squad.playerId));
            Destroy(squad.root, 0.3f);
            squads.RemoveAt(i);
            handled = true;
        }

        return handled;
    }

    SquadVisual FindMatchingSquad(LycheeReplayPayload payload)
    {
        if (payload == null) return null;
        if (!string.IsNullOrEmpty(payload.orderId))
        {
            for (int i = squads.Count - 1; i >= 0; i--)
            {
                SquadVisual candidate = squads[i];
                if (candidate != null && candidate.root != null && candidate.orderId == payload.orderId)
                    return candidate;
            }
        }
        for (int i = squads.Count - 1; i >= 0; i--)
        {
            SquadVisual candidate = squads[i];
            if (candidate == null || candidate.root == null) continue;
            if (payload.playerId != 0 && candidate.playerId != payload.playerId) continue;
            if (!string.IsNullOrEmpty(payload.targetNodeId) && candidate.targetNodeId != payload.targetNodeId) continue;
            return candidate;
        }
        if (!string.IsNullOrEmpty(payload.targetNodeId))
        {
            for (int i = squads.Count - 1; i >= 0; i--)
            {
                SquadVisual candidate = squads[i];
                if (candidate != null && candidate.root != null && candidate.targetNodeId == payload.targetNodeId)
                    return candidate;
            }
        }
        return null;
    }

    void BackfillSquadPayloadPlayer(LycheeReplayPayload payload)
    {
        if (payload == null || payload.playerId != 0) return;
        SquadVisual squad = FindMatchingSquad(payload);
        if (squad != null)
            payload.playerId = squad.playerId;
    }

    GameObject EnsureRockfallPile(string nodeId, Transform parent)
    {
        if (string.IsNullOrEmpty(nodeId) || parent == null) return null;
        if (obstacleRockPiles.TryGetValue(nodeId, out GameObject existing) && existing != null)
            return existing;

        GameObject pile = new GameObject("RockfallPile");
        pile.transform.SetParent(parent, false);
        pile.transform.localPosition = new Vector3(0f, 0.05f, 0f);
        int seed = StableHash(nodeId);
        int count = 9;
        for (int i = 0; i < count; i++)
        {
            GameObject rock = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            rock.name = "FallenRock_" + i;
            rock.transform.SetParent(pile.transform, false);
            float angle = (seed * 0.017f + i * 2.399f) % (Mathf.PI * 2f);
            float radius = 0.18f + 0.12f * (i % 4);
            rock.transform.localPosition = new Vector3(Mathf.Cos(angle) * radius, 0.08f + i * 0.018f, Mathf.Sin(angle) * radius);
            float scale = 0.28f + 0.07f * ((seed + i * 3) % 5);
            rock.transform.localScale = new Vector3(scale * 1.25f, scale * 0.72f, scale);
            rock.transform.localRotation = Quaternion.Euler((seed + i * 31) % 47, (seed + i * 73) % 360, (seed + i * 19) % 42);
            Renderer renderer = rock.GetComponent<Renderer>();
            if (renderer != null) renderer.sharedMaterial = RockMaterial();
            Collider collider = rock.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
        }
        pile.SetActive(false);
        obstacleRockPiles[nodeId] = pile;
        return pile;
    }

    GameObject EnsureBrokenBridge(string nodeId, Transform parent)
    {
        if (string.IsNullOrEmpty(nodeId) || parent == null) return null;
        if (obstacleBrokenBridges.TryGetValue(nodeId, out GameObject existing) && existing != null)
            return existing;

        GameObject bridge = new GameObject("BrokenBridgeBlock");
        bridge.transform.SetParent(parent, false);
        bridge.transform.localPosition = new Vector3(0f, 0.04f, 0f);
        int seed = StableHash(nodeId);

        for (int i = 0; i < 5; i++)
        {
            float x = -0.52f + i * 0.26f;
            float tilt = ((seed + i * 17) % 17) - 8f;
            CreateObstaclePiece(
                bridge.transform,
                "BridgePlank_Near_" + i,
                PrimitiveType.Cube,
                new Vector3(x, 0.10f + i * 0.004f, -0.46f),
                new Vector3(0.17f, 0.08f, 0.72f),
                Quaternion.Euler(9f + tilt * 0.25f, 0f, -16f + tilt),
                WoodMaterial());
            CreateObstaclePiece(
                bridge.transform,
                "BridgePlank_Far_" + i,
                PrimitiveType.Cube,
                new Vector3(x + Mathf.Sin(seed + i) * 0.035f, 0.11f, 0.48f),
                new Vector3(0.17f, 0.08f, 0.68f),
                Quaternion.Euler(-11f + tilt * 0.2f, 0f, 14f - tilt),
                WoodMaterial());
        }

        CreateObstaclePiece(bridge.transform, "BridgeBeam_BrokenA", PrimitiveType.Cube, new Vector3(0f, 0.16f, -1.00f), new Vector3(1.75f, 0.12f, 0.14f), Quaternion.Euler(0f, 0f, -7f), WoodMaterial());
        CreateObstaclePiece(bridge.transform, "BridgeBeam_BrokenB", PrimitiveType.Cube, new Vector3(0.08f, 0.18f, 1.02f), new Vector3(1.55f, 0.12f, 0.14f), Quaternion.Euler(0f, 0f, 9f), WoodMaterial());
        CreateObstaclePiece(bridge.transform, "WarningBar_A", PrimitiveType.Cube, new Vector3(0f, 0.54f, 0f), new Vector3(1.55f, 0.10f, 0.11f), Quaternion.Euler(0f, 0f, 24f), WarningMaterial());
        CreateObstaclePiece(bridge.transform, "WarningBar_B", PrimitiveType.Cube, new Vector3(0f, 0.54f, 0f), new Vector3(1.55f, 0.10f, 0.11f), Quaternion.Euler(0f, 0f, -24f), WarningMaterial());
        CreateObstaclePiece(bridge.transform, "WarningPost_L", PrimitiveType.Cylinder, new Vector3(-0.82f, 0.31f, 0f), new Vector3(0.05f, 0.32f, 0.05f), Quaternion.identity, WoodMaterial());
        CreateObstaclePiece(bridge.transform, "WarningPost_R", PrimitiveType.Cylinder, new Vector3(0.82f, 0.31f, 0f), new Vector3(0.05f, 0.32f, 0.05f), Quaternion.identity, WoodMaterial());

        bridge.SetActive(false);
        obstacleBrokenBridges[nodeId] = bridge;
        return bridge;
    }

    GameObject EnsureDockBlock(string nodeId, Transform parent)
    {
        if (string.IsNullOrEmpty(nodeId) || parent == null) return null;
        if (obstacleDockBlocks.TryGetValue(nodeId, out GameObject existing) && existing != null)
            return existing;

        GameObject block = new GameObject("DockCargoBlock");
        block.transform.SetParent(parent, false);
        block.transform.localPosition = new Vector3(0f, 0.04f, 0f);
        int seed = StableHash(nodeId);

        Vector3[] cratePositions =
        {
            new Vector3(-0.52f, 0.23f, -0.18f),
            new Vector3(0.02f, 0.23f, -0.22f),
            new Vector3(0.56f, 0.23f, -0.10f),
            new Vector3(-0.25f, 0.60f, 0.12f),
            new Vector3(0.34f, 0.58f, 0.18f)
        };
        for (int i = 0; i < cratePositions.Length; i++)
        {
            CreateObstaclePiece(
                block.transform,
                "DockCrate_" + i,
                PrimitiveType.Cube,
                cratePositions[i],
                new Vector3(0.46f, 0.42f, 0.42f),
                Quaternion.Euler(0f, (seed + i * 31) % 24 - 12f, 0f),
                CargoMaterial());
        }

        CreateObstaclePiece(block.transform, "DockBarrel_A", PrimitiveType.Cylinder, new Vector3(-0.78f, 0.26f, 0.42f), new Vector3(0.20f, 0.34f, 0.20f), Quaternion.Euler(90f, 0f, 10f), CargoMaterial());
        CreateObstaclePiece(block.transform, "DockBarrel_B", PrimitiveType.Cylinder, new Vector3(0.76f, 0.26f, 0.38f), new Vector3(0.20f, 0.34f, 0.20f), Quaternion.Euler(90f, 0f, -12f), CargoMaterial());
        CreateObstaclePiece(block.transform, "CargoSack_A", PrimitiveType.Sphere, new Vector3(-0.10f, 0.20f, 0.58f), new Vector3(0.44f, 0.22f, 0.34f), Quaternion.Euler(0f, 18f, 0f), RopeMaterial());
        CreateObstaclePiece(block.transform, "CargoSack_B", PrimitiveType.Sphere, new Vector3(0.38f, 0.20f, 0.62f), new Vector3(0.38f, 0.20f, 0.30f), Quaternion.Euler(0f, -10f, 0f), RopeMaterial());

        CreateObstaclePiece(block.transform, "RopeBarrier_Front", PrimitiveType.Cylinder, new Vector3(0f, 0.58f, -0.62f), new Vector3(0.035f, 0.92f, 0.035f), Quaternion.Euler(0f, 0f, 90f), RopeMaterial());
        CreateObstaclePiece(block.transform, "RopeBarrier_Back", PrimitiveType.Cylinder, new Vector3(0f, 0.58f, 0.72f), new Vector3(0.035f, 0.86f, 0.035f), Quaternion.Euler(0f, 0f, 90f), RopeMaterial());
        CreateObstaclePiece(block.transform, "DockPost_L", PrimitiveType.Cylinder, new Vector3(-0.95f, 0.37f, 0.02f), new Vector3(0.06f, 0.42f, 0.06f), Quaternion.identity, WoodMaterial());
        CreateObstaclePiece(block.transform, "DockPost_R", PrimitiveType.Cylinder, new Vector3(0.95f, 0.37f, 0.02f), new Vector3(0.06f, 0.42f, 0.06f), Quaternion.identity, WoodMaterial());

        block.SetActive(false);
        obstacleDockBlocks[nodeId] = block;
        return block;
    }

    GameObject CreateObstaclePiece(Transform parent, string name, PrimitiveType type, Vector3 localPosition, Vector3 localScale, Quaternion localRotation, Material material)
    {
        GameObject piece = GameObject.CreatePrimitive(type);
        piece.name = name;
        piece.transform.SetParent(parent, false);
        piece.transform.localPosition = localPosition;
        piece.transform.localScale = localScale;
        piece.transform.localRotation = localRotation;
        Renderer renderer = piece.GetComponent<Renderer>();
        if (renderer != null && material != null)
            renderer.sharedMaterial = material;
        Collider collider = piece.GetComponent<Collider>();
        if (collider != null) Destroy(collider);
        return piece;
    }

    void ShowObstacleCauseCue(string nodeId)
    {
        string obstacleType = CurrentObstacleType(nodeId);
        if (string.IsNullOrEmpty(obstacleType)) return;
        if (obstacleCueCooldown.TryGetValue(nodeId, out float nextAllowed) && Time.unscaledTime < nextAllowed)
            return;
        obstacleCueCooldown[nodeId] = Time.unscaledTime + 2.8f;
        SpawnObstacleAppearFx(nodeId, obstacleType);
    }

    string CurrentObstacleType(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return null;
        if (activeObstacleTypes.TryGetValue(nodeId, out string activeType) && !string.IsNullOrEmpty(activeType))
            return activeType;
        LycheeReplayFrame frame = frames.Count == 0 ? lastAppliedFrame : frames[Mathf.Clamp(frameIndex, 0, frames.Count - 1)];
        if (frame != null && frame.nodes != null)
        {
            for (int i = 0; i < frame.nodes.Length; i++)
            {
                LycheeReplayNodeState node = frame.nodes[i];
                if (node != null && node.nodeId == nodeId && node.hasObstacle)
                    return string.IsNullOrEmpty(node.obstacleType) ? "OBSTACLE" : node.obstacleType;
            }
        }
        return null;
    }

    void SpawnObstacleAppearFx(string nodeId, string obstacleType)
    {
        if (string.IsNullOrEmpty(nodeId) || !stations.TryGetValue(nodeId, out MapStationAnchor station))
            return;
        Vector3 position = ObstacleWorldPosition(station) + Vector3.up * 0.12f;
        if (obstacles.TryGetValue(nodeId, out GameObject obstacle) && obstacle != null && obstacle.activeInHierarchy)
            StartCoroutine(JoltObstacleVisual(obstacle.transform));
        if (obstacleType == "BROKEN_BRIDGE")
        {
            SpawnBrokenBridgeFx(position, nodeId);
            return;
        }
        if (obstacleType == "DOCK_BLOCK")
        {
            SpawnDockBlockFx(position, nodeId);
            return;
        }
        if (obstacleType != "ROCKFALL")
        {
            SpawnEventPulse(position, ObstacleName(obstacleType), new Color(1f, 0.68f, 0.24f, 1f));
            return;
        }

        GameObject root = new GameObject("RockfallFx_" + nodeId);
        root.transform.SetParent(eventRoot, false);
        root.transform.position = position;

        ParticleSystem falling = root.AddComponent<ParticleSystem>();
        falling.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ParticleSystemRenderer renderer = falling.GetComponent<ParticleSystemRenderer>();
        Material material = EventPulseMaterial();
        if (renderer != null && material != null) renderer.material = material;

        ParticleSystem.MainModule main = falling.main;
        main.duration = 1.1f;
        main.startLifetime = 1.15f;
        main.startSpeed = 2.4f;
        main.startSize = 0.22f;
        main.startColor = new Color(0.56f, 0.50f, 0.42f, 0.95f);
        main.gravityModifier = 1.65f;
        main.maxParticles = 24;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        ParticleSystem.EmissionModule emission = falling.emission;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 16) });
        ParticleSystem.ShapeModule shape = falling.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.75f;
        shape.position = new Vector3(0f, 2.7f, 0f);
        falling.Play();

        SpawnEventPulse(position, "落石阻路", new Color(1f, 0.63f, 0.20f, 1f));
        eventEffects.Add(root);
        Destroy(root, 2.6f);
    }

    void SpawnBrokenBridgeFx(Vector3 position, string nodeId)
    {
        GameObject root = new GameObject("BrokenBridgeFx_" + nodeId);
        root.transform.SetParent(eventRoot, false);
        root.transform.position = position + Vector3.up * 0.25f;

        SpawnObstacleParticles(root.transform, new Color(0.55f, 0.34f, 0.16f, 0.92f), 18, 1.7f, 0.11f, 1.0f);
        int seed = StableHash(nodeId);
        for (int i = 0; i < 7; i++)
        {
            float angle = seed * 0.011f + i * 0.9f;
            Vector3 local = new Vector3(Mathf.Cos(angle) * 0.18f, 0.42f + i * 0.025f, Mathf.Sin(angle) * 0.18f);
            Vector3 velocity = new Vector3(Mathf.Cos(angle) * 1.0f, 1.25f + i * 0.06f, Mathf.Sin(angle) * 0.9f);
            SpawnFlyingObstaclePiece(
                root.transform,
                "FlyingSplinter_" + i,
                PrimitiveType.Cube,
                local,
                new Vector3(0.08f, 0.035f, 0.42f),
                Quaternion.Euler((seed + i * 17) % 60, (seed + i * 43) % 360, (seed + i * 29) % 50),
                WoodMaterial(),
                velocity,
                new Vector3(2.8f + i, 3.6f - i * 0.25f, 4.2f));
        }

        SpawnEventPulse(position, "断桥阻路", new Color(1f, 0.62f, 0.18f, 1f));
        eventEffects.Add(root);
        Destroy(root, 2.6f);
    }

    void SpawnDockBlockFx(Vector3 position, string nodeId)
    {
        GameObject root = new GameObject("DockBlockFx_" + nodeId);
        root.transform.SetParent(eventRoot, false);
        root.transform.position = position + Vector3.up * 0.2f;

        SpawnObstacleParticles(root.transform, new Color(0.76f, 0.56f, 0.30f, 0.86f), 14, 1.1f, 0.13f, 0.45f);
        int seed = StableHash(nodeId);
        for (int i = 0; i < 5; i++)
        {
            float angle = seed * 0.013f + i * 1.22f;
            PrimitiveType type = i % 2 == 0 ? PrimitiveType.Cube : PrimitiveType.Sphere;
            Vector3 scale = type == PrimitiveType.Cube
                ? new Vector3(0.18f, 0.16f, 0.18f)
                : new Vector3(0.20f, 0.11f, 0.16f);
            Vector3 local = new Vector3(Mathf.Cos(angle) * 0.22f, 0.34f + i * 0.03f, Mathf.Sin(angle) * 0.18f);
            Vector3 velocity = new Vector3(Mathf.Cos(angle) * 0.7f, 0.78f + i * 0.04f, Mathf.Sin(angle) * 0.7f);
            SpawnFlyingObstaclePiece(
                root.transform,
                "CargoBump_" + i,
                type,
                local,
                scale,
                Quaternion.Euler(0f, (seed + i * 37) % 360, 0f),
                type == PrimitiveType.Cube ? CargoMaterial() : RopeMaterial(),
                velocity,
                new Vector3(1.8f + i * 0.25f, 2.2f, 2.6f - i * 0.2f));
        }

        SpawnEventPulse(position, "码头堵塞", new Color(1f, 0.70f, 0.24f, 1f));
        eventEffects.Add(root);
        Destroy(root, 2.6f);
    }

    void SpawnObstacleParticles(Transform parent, Color color, short count, float speed, float size, float gravity)
    {
        ParticleSystem particles = parent.gameObject.AddComponent<ParticleSystem>();
        particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ParticleSystemRenderer renderer = particles.GetComponent<ParticleSystemRenderer>();
        Material material = EventPulseMaterial();
        if (renderer != null && material != null) renderer.material = material;

        ParticleSystem.MainModule main = particles.main;
        main.duration = 0.9f;
        main.startLifetime = 0.9f;
        main.startSpeed = speed;
        main.startSize = size;
        main.startColor = color;
        main.gravityModifier = gravity;
        main.maxParticles = Mathf.Max(16, count + 4);
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        ParticleSystem.EmissionModule emission = particles.emission;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, count) });
        ParticleSystem.ShapeModule shape = particles.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.55f;
        particles.Play();
    }

    GameObject SpawnFlyingObstaclePiece(Transform parent, string name, PrimitiveType type, Vector3 localPosition, Vector3 localScale, Quaternion localRotation, Material material, Vector3 velocity, Vector3 angularVelocity)
    {
        GameObject piece = CreateObstaclePiece(parent, name, type, localPosition, localScale, localRotation, material);
        Rigidbody body = piece.AddComponent<Rigidbody>();
        body.mass = 0.15f;
        body.useGravity = true;
        body.linearVelocity = velocity;
        body.angularVelocity = angularVelocity;
        return piece;
    }

    IEnumerator JoltObstacleVisual(Transform target)
    {
        if (target == null) yield break;
        Vector3 baseScale = target.localScale;
        Quaternion baseRotation = target.localRotation;
        float seconds = 0.55f;
        float elapsed = 0f;
        while (elapsed < seconds && target != null)
        {
            float k = 1f - elapsed / seconds;
            float shake = Mathf.Sin(elapsed * 38f) * k;
            target.localScale = baseScale * (1f + 0.055f * Mathf.Abs(shake));
            target.localRotation = baseRotation * Quaternion.Euler(0f, shake * 2.4f, 0f);
            elapsed += Time.deltaTime;
            yield return null;
        }
        if (target != null)
        {
            target.localScale = baseScale;
            target.localRotation = baseRotation;
        }
    }

    void SpawnClearActionFx(Vector3 position, Color color, string text, bool success)
    {
        GameObject root = new GameObject("ClearFx_" + text);
        root.transform.SetParent(eventRoot, false);
        root.transform.position = position + Vector3.up * 0.35f;

        ParticleSystem particles = root.AddComponent<ParticleSystem>();
        particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ParticleSystemRenderer renderer = particles.GetComponent<ParticleSystemRenderer>();
        Material material = EventPulseMaterial();
        if (renderer != null && material != null) renderer.material = material;

        ParticleSystem.MainModule main = particles.main;
        main.duration = success ? 1.05f : 0.85f;
        main.startLifetime = success ? 0.8f : 0.65f;
        main.startSpeed = success ? 2.8f : 1.7f;
        main.startSize = success ? 0.22f : 0.18f;
        main.startColor = success ? new Color(color.r, color.g, color.b, 0.92f) : new Color(1f, 0.28f, 0.18f, 0.9f);
        main.gravityModifier = success ? 0.28f : 0.08f;
        main.maxParticles = 28;

        ParticleSystem.EmissionModule emission = particles.emission;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)(success ? 18 : 12)) });
        ParticleSystem.ShapeModule shape = particles.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = success ? 38f : 24f;
        shape.radius = success ? 0.8f : 0.5f;
        particles.Play();

        TextMesh label = CreateLabel(root.transform, text, success ? new Color(1f, 0.86f, 0.28f, 1f) : new Color(1f, 0.32f, 0.22f, 1f));
        label.transform.localPosition = new Vector3(0f, 1.6f, 0f);
        eventEffects.Add(root);
        Destroy(root, success ? 2.8f : 2.4f);
    }

    /// <summary>给跨河小队垫船（懒创建）。船体微沉入水线，成员在甲板高度由 AnimateSquadMembers 抬升。</summary>
    void EnsureSquadBoat(SquadVisual squad)
    {
        if (squad.boat != null || boatModel == null || squad.root == null) return;
        squad.boat = CreateNormalizedModel(boatModel, squad.root.transform, "SquadBoat", 2.8f);
        if (squad.boat != null)
            squad.boat.transform.localPosition = new Vector3(0f, -0.08f, 0f);
    }

    /// <summary>水路时给主车队的船插一面红/蓝队旗（懒创建一次）：旗杆 + 队色自发光旗面，远景俯视也分得清红蓝。</summary>
    void EnsureConvoyBoatFlag(ConvoyVisual visual, int camp)
    {
        if (visual.boatFlag != null || visual.boat == null) return;
        Color teamColor = camp == 0 ? new Color(1f, 0.22f, 0.14f) : new Color(0.16f, 0.46f, 1f);
        GameObject flag = new GameObject("BoatFlag");
        flag.transform.SetParent(visual.boat.transform, false);
        flag.transform.localPosition = new Vector3(0f, 1.5f, 0f);   // 船桅高度
        GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pole.name = "Pole";
        pole.transform.SetParent(flag.transform, false);
        pole.transform.localScale = new Vector3(0.04f, 0.8f, 0.04f);
        Destroy(pole.GetComponent<Collider>());
        ApplyEmissive(pole, new Color(0.22f, 0.17f, 0.12f));
        GameObject cloth = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cloth.name = "Cloth";
        cloth.transform.SetParent(flag.transform, false);
        cloth.transform.localPosition = new Vector3(0.36f, 0.5f, 0f);
        cloth.transform.localScale = new Vector3(0.66f, 0.4f, 0.03f);
        Destroy(cloth.GetComponent<Collider>());
        ApplyEmissive(cloth, teamColor);
        visual.boatFlag = flag;
    }

    /// <summary>给基本几何体套 URP/Lit 自发光材质（队旗/旗杆用，俯视远景也不被压暗）。</summary>
    static void ApplyEmissive(GameObject obj, Color color)
    {
        MeshRenderer renderer = obj.GetComponent<MeshRenderer>();
        if (renderer == null) return;
        Material material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        material.SetColor("_BaseColor", color);
        material.EnableKeyword("_EMISSION");
        material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        material.SetColor("_EmissionColor", color * 1.4f);
        renderer.material = material;
    }

    /// <summary>采样 (x,z) 处河面高度。XZ 重心插值，多层取最上层；不在水面返回 false。</summary>
    bool TrySampleWaterSurface(float x, float z, out float waterY)
    {
        if (!waterTrianglesBuilt) BuildWaterTriangleCache();
        waterY = float.MinValue;
        bool hit = false;
        for (int i = 0; i < waterTriangleA.Count; i++)
        {
            Vector4 box = waterTriangleBox[i];
            if (x < box.x || z < box.y || x > box.z || z > box.w) continue;
            Vector3 pa = waterTriangleA[i], pb = waterTriangleB[i], pc = waterTriangleC[i];
            float d = (pb.z - pc.z) * (pa.x - pc.x) + (pc.x - pb.x) * (pa.z - pc.z);
            if (Mathf.Abs(d) < 1e-6f) continue;
            float w1 = ((pb.z - pc.z) * (x - pc.x) + (pc.x - pb.x) * (z - pc.z)) / d;
            float w2 = ((pc.z - pa.z) * (x - pc.x) + (pa.x - pc.x) * (z - pc.z)) / d;
            float w3 = 1f - w1 - w2;
            if (w1 < -0.001f || w2 < -0.001f || w3 < -0.001f) continue;
            float sampled = w1 * pa.y + w2 * pb.y + w3 * pc.y;
            if (sampled > waterY) { waterY = sampled; hit = true; }
        }
        return hit;
    }

    /// <summary>从场景河面容器收集世界空间三角形，只建一次。容器缺失时退化为"全图无水"，小队保持旧行为。</summary>
    void BuildWaterTriangleCache()
    {
        waterTrianglesBuilt = true;
        foreach (string containerName in WaterContainerNames)
        {
            GameObject container = GameObject.Find(containerName);
            if (container == null) continue;
            foreach (MeshFilter filter in container.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh == null) continue;
                Vector3[] vertices = filter.sharedMesh.vertices;
                int[] triangles = filter.sharedMesh.triangles;
                Transform t = filter.transform;
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    Vector3 wa = t.TransformPoint(vertices[triangles[i]]);
                    Vector3 wb = t.TransformPoint(vertices[triangles[i + 1]]);
                    Vector3 wc = t.TransformPoint(vertices[triangles[i + 2]]);
                    waterTriangleA.Add(wa);
                    waterTriangleB.Add(wb);
                    waterTriangleC.Add(wc);
                    waterTriangleBox.Add(new Vector4(
                        Mathf.Min(wa.x, wb.x, wc.x), Mathf.Min(wa.z, wb.z, wc.z),
                        Mathf.Max(wa.x, wb.x, wc.x), Mathf.Max(wa.z, wb.z, wc.z)));
                }
            }
        }
        Debug.Log("[LycheeReplayPlayer] 河面三角形缓存: " + waterTriangleA.Count + " 个");
    }

    Material eventPulseMaterial;

    /// <summary>事件光点粒子材质（URP 兼容）：默认粒子材质在 URP 下渲染成洋红错误色，必须自建一份共享。</summary>
    Material EventPulseMaterial()
    {
        if (eventPulseMaterial != null) return eventPulseMaterial;
        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        if (shader == null) return null;
        eventPulseMaterial = new Material(shader) { name = "LycheeEventPulseMat" };
        // 用 alpha 混合（与红尘拖尾同款，已验证 WebGL 构建里该变体不被裁剪、不会品红）；
        // 加色混合变体在 WebGL 构建会被 shader 变体裁剪掉导致品红，故不用 Additive
        if (eventPulseMaterial.HasProperty("_Surface")) eventPulseMaterial.SetFloat("_Surface", 1f);
        if (eventPulseMaterial.HasProperty("_Blend")) eventPulseMaterial.SetFloat("_Blend", 0f); // Alpha
        if (eventPulseMaterial.HasProperty("_SrcBlend")) eventPulseMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (eventPulseMaterial.HasProperty("_DstBlend")) eventPulseMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (eventPulseMaterial.HasProperty("_ZWrite")) eventPulseMaterial.SetFloat("_ZWrite", 0f);
        eventPulseMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        eventPulseMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        return eventPulseMaterial;
    }

    Material RockMaterial()
    {
        if (rockMaterial != null) return rockMaterial;
        return rockMaterial = CreateLitMaterial("LycheeRockfallStoneMat", new Color(0.34f, 0.30f, 0.25f, 1f), 0.18f);
    }

    Material WoodMaterial()
    {
        if (woodMaterial != null) return woodMaterial;
        return woodMaterial = CreateLitMaterial("LycheeObstacleWoodMat", new Color(0.44f, 0.24f, 0.10f, 1f), 0.22f);
    }

    Material WarningMaterial()
    {
        if (warningMaterial != null) return warningMaterial;
        return warningMaterial = CreateLitMaterial("LycheeObstacleWarningMat", new Color(0.95f, 0.55f, 0.08f, 1f), 0.28f);
    }

    Material CargoMaterial()
    {
        if (cargoMaterial != null) return cargoMaterial;
        return cargoMaterial = CreateLitMaterial("LycheeObstacleCargoMat", new Color(0.62f, 0.42f, 0.20f, 1f), 0.24f);
    }

    Material RopeMaterial()
    {
        if (ropeMaterial != null) return ropeMaterial;
        return ropeMaterial = CreateLitMaterial("LycheeObstacleRopeMat", new Color(0.50f, 0.39f, 0.22f, 1f), 0.35f);
    }

    Material CreateLitMaterial(string name, Color color, float smoothness)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) return null;
        Material material = new Material(shader) { name = name };
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        else if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
        return material;
    }

    void SpawnEventPulse(Vector3 position, string text, Color color, Sprite icon = null, string status = null)
    {
        PruneEventEffects();
        GameObject root = new GameObject("Event_" + text);
        root.transform.SetParent(eventRoot, false);
        root.transform.position = position + Vector3.up * 0.25f;
        // 节点事件只保留图标 billboard；无图标时不再额外打白字，避免覆盖地图与事件卡信息重复。
        if (icon == null)
        {
            Destroy(root);
            return;
        }
        CreateEventIconBillboard(root.transform, icon, status);
        eventEffects.Add(root);
        eventEffectBasePositions[root] = position;
        ReflowEventPulseCluster(position);
        Destroy(root, 1.8f);
    }

    void ReflowEventPulseCluster(Vector3 basePosition)
    {
        PruneEventEffects();

        List<GameObject> cluster = new List<GameObject>();
        float radiusSqr = EventPulseClusterRadius * EventPulseClusterRadius;
        foreach (GameObject effect in eventEffects)
        {
            if (effect == null) continue;
            if (!eventEffectBasePositions.TryGetValue(effect, out Vector3 effectBase))
                effectBase = effect.transform.position;
            if ((effectBase - basePosition).sqrMagnitude <= radiusSqr)
                cluster.Add(effect);
        }

        int total = cluster.Count;
        if (total <= 1)
        {
            if (total == 1 && eventEffectBasePositions.TryGetValue(cluster[0], out Vector3 onlyBase))
                cluster[0].transform.position = onlyBase + Vector3.up * 0.25f;
            return;
        }

        Vector3 right = Vector3.right;
        Camera camera = Camera.main;
        if (camera != null)
        {
            right = camera.transform.right;
            right.y = 0f;
            if (right.sqrMagnitude < 0.001f) right = camera.transform.right;
        }
        right.Normalize();

        for (int i = 0; i < total; i++)
        {
            GameObject effect = cluster[i];
            if (effect == null) continue;
            if (!eventEffectBasePositions.TryGetValue(effect, out Vector3 effectBase))
                effectBase = effect.transform.position - Vector3.up * 0.25f;
            float offset = (i - (total - 1) * 0.5f) * EventPulseSlotSpacing;
            effect.transform.position = effectBase + Vector3.up * 0.25f + right * offset;
        }
    }

    void PruneEventEffects()
    {
        for (int i = eventEffects.Count - 1; i >= 0; i--)
        {
            if (eventEffects[i] == null)
            {
                eventEffects.RemoveAt(i);
                continue;
            }
            if (!eventEffectBasePositions.ContainsKey(eventEffects[i]))
                eventEffectBasePositions[eventEffects[i]] = eventEffects[i].transform.position;
        }

        removalEventBuffer.Clear();
        foreach (GameObject effect in eventEffectBasePositions.Keys)
            if (effect == null || !eventEffects.Contains(effect)) removalEventBuffer.Add(effect);
        foreach (GameObject effect in removalEventBuffer)
            eventEffectBasePositions.Remove(effect);
    }

    void ClearEventEffects()
    {
        for (int i = eventEffects.Count - 1; i >= 0; i--)
            if (eventEffects[i] != null) Destroy(eventEffects[i]);
        eventEffects.Clear();
        eventEffectBasePositions.Clear();
    }

    void SeekFrame(int index)
    {
        frameIndex = Mathf.Clamp(index, 0, frames.Count - 1);
        frameTime = 0f;
        lastEventFrame = frameIndex - 1;
        if (frameIndex < frames.Count - 1) matchOverRaised = false;
        foreach (SquadVisual squad in squads)
            if (squad.root != null) Destroy(squad.root);
        squads.Clear();
        ClearEventEffects();
        foreach (ConvoyVisual convoy in convoys.Values)
        {
            convoy.initialized = false;
            convoy.positionVelocity = Vector3.zero;
        }
        // 跳转后 HUD 排队中的事件弹窗都指向旧进度，先作废再重放目标帧事件，避免补播错位的回合号
        if (runtimeHud != null) runtimeHud.CancelCombatPopups();
        // 对抗聚焦缓存同理：跳过 CONTEST_END 的跳转会让 CARD_REVEAL 续期到旧对抗节点
        hasContestFocus = false;
        eventFocusTarget = null;
        eventFocusUntil = 0f;
        recentEventFocuses.Clear();
        processCardRoundByKey.Clear();
        ApplyFrame(frameIndex, true);
        InterpolateConvoys(0f, 0f);
        UpdateRuntimeHud();
        if (frameIndex == 0) ShowOpeningRoster();
    }

    Vector3 GridToWorld(float x, float y)
    {
        float worldX = x - 40f;
        float worldZ = 30f - y;
        float worldY = elevation == null ? 0f : elevation.SampleWorldHeight(worldX, worldZ);
        return new Vector3(worldX, worldY, worldZ);
    }

    bool IsWaterEdge(string edgeId)
    {
        return !string.IsNullOrEmpty(edgeId) &&
            edges.TryGetValue(edgeId, out MapRouteEdge edge) &&
            edge.routeType == MapRouteType.Water;
    }

    /// <summary>小分队战术动作 → 中文(清障/增援/削弱/探路/破袭)，供「XX队小分队正在/完成 XX节点 XX动作」状态条用。</summary>
    static string SquadActionCn(string action)
    {
        if (string.IsNullOrEmpty(action)) return "行动";
        string normalized = action.ToUpperInvariant();
        if (normalized.Contains("CLEAR")) return "清障";
        if (normalized.Contains("REINFORCE")) return "增援";
        if (normalized.Contains("WEAKEN")) return "削弱";
        if (normalized.Contains("SCOUT")) return "探路";
        if (normalized.Contains("RAID")) return "破袭";
        return "行动";
    }

    int SquadMemberCount(LycheeReplayPayload payload)
    {
        string action = PayloadTactic(payload);
        if (!string.IsNullOrEmpty(action))
        {
            string normalized = action.ToUpperInvariant();
            if (normalized.Contains("SCOUT")) return 1;
            if (normalized.Contains("CLEAR") ||
                normalized.Contains("REINFORCE") ||
                normalized.Contains("WEAKEN"))
                return 2;
        }
        return Mathf.Clamp(defaultDispatchMemberCount, 1, 12);
    }

    static int PayloadArrivalRound(LycheeReplayPayload payload, int currentRound)
    {
        if (payload == null) return currentRound + 12;
        if (payload.arriveRound > currentRound) return payload.arriveRound;
        if (payload.completeRound > currentRound) return payload.completeRound;
        return currentRound + 12;
    }

    static string PayloadTactic(LycheeReplayPayload payload)
    {
        if (payload == null) return null;
        if (!string.IsNullOrEmpty(payload.tactic)) return payload.tactic;
        if (!string.IsNullOrEmpty(payload.action)) return payload.action;
        return payload.processType;
    }

    string TeamName(int playerId)
    {
        if (frames.Count > 0 && frames[frameIndex].players != null)
        {
            LycheeReplayPlayerState player = frames[frameIndex].players.FirstOrDefault(candidate => candidate.playerId == playerId);
            if (player != null) return TeamDisplayName(player);
        }
        if (startFrame != null && startFrame.players != null)
        {
            LycheeReplayPlayerState player = startFrame.players.FirstOrDefault(candidate => candidate.playerId == playerId);
            if (player != null) return TeamDisplayName(player);
        }
        return "队伍";
    }

    static string TeamDisplayName(LycheeReplayPlayerState player)
    {
        if (player == null) return "队伍";
        string campName = player.camp == 0 ? "红队" : "蓝队";
        // 地图车队 label 用数值 playerId（红 1002 / 蓝 2002，唯一）做队伍 ID，与底部 HUD 一致，重名也能区分。
        string id = player.playerId > 0 ? "#" + player.playerId : (!string.IsNullOrEmpty(player.teamId) ? "#" + player.teamId : string.Empty);
        string name = string.IsNullOrEmpty(player.name) ? string.Empty : player.name;
        if (string.IsNullOrEmpty(id)) return string.IsNullOrEmpty(name) ? campName : campName + " " + name;
        if (string.IsNullOrEmpty(name)) return campName + " " + id;
        return campName + " " + id + " · " + name;
    }

    string DisplayNode(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return "目标点";
        if (stations.TryGetValue(nodeId, out MapStationAnchor station) && !string.IsNullOrEmpty(station.displayName))
            return station.displayName;
        return nodeId;
    }

    // 事件卡底部条用的「队色文字」：把队色转成 #RRGGBB 富文本包裹，RuntimeHud 底部条遇到 <color 直接原样显示。
    string TeamColoredFooter(int playerId, string text)
    {
        Color c = TeamColor(playerId);
        return "<color=#" + ColorUtility.ToHtmlStringRGB(c) + ">" + text + "</color>";
    }

    // 干净的队名「红队 / 蓝队」（camp 0=红/1=蓝），底部条用——TeamName 带 #2002·blue-l4 内部串太杂。
    string TeamShortName(int playerId)
    {
        if (frames.Count > 0 && frames[frameIndex].players != null)
        {
            LycheeReplayPlayerState ps = frames[frameIndex].players.FirstOrDefault(c => c != null && c.playerId == playerId);
            if (ps != null) return ps.camp == 0 ? "红队" : "蓝队";
        }
        return "队伍";
    }

    Color TeamColor(int playerId)
    {
        if (frames.Count > 0 && frames[frameIndex].players != null)
        {
            LycheeReplayPlayerState player = frames[frameIndex].players.FirstOrDefault(candidate => candidate.playerId == playerId);
            if (player != null)
                return player.camp == 0 ? new Color(1f, 0.32f, 0.24f, 1f) : new Color(0.34f, 0.60f, 1f, 1f);
        }
        return new Color(1f, 0.82f, 0.22f, 1f);
    }

    static string CleanNode(string nodeId)
    {
        return string.IsNullOrEmpty(nodeId) ? "目标点" : nodeId;
    }

    static string TacticName(string tactic)
    {
        if (tactic == "WEAKEN") return "削弱";
        if (tactic == "REINFORCE") return "增援";
        if (tactic == "CLEAR" || tactic == "SQUAD_CLEAR" || tactic == "SQUAD_CLEAR_SUCCESS" || tactic == "CLEAR_OBSTACLE") return "清障";
        if (tactic == "SCOUT") return "侦察";
        return string.IsNullOrEmpty(tactic) ? "行动" : tactic;
    }

    static string ScoreText(int score)
    {
        return score > 0 ? "+" + score + "分" : "待结算";
    }

    static string ShortId(string id, string fallback)
    {
        if (string.IsNullOrEmpty(id)) return fallback;
        return id.Length > 10 ? id.Substring(0, 10) : id;
    }

    LycheeReplayTaskState FindTaskState(LycheeReplayPayload payload)
    {
        if (payload == null) return null;
        LycheeReplayFrame frame = CurrentFrame;
        LycheeReplayTaskState[] tasks = frame == null ? null : frame.tasks;
        if (tasks == null || tasks.Length == 0) return null;

        string nodeId = !string.IsNullOrEmpty(payload.targetNodeId) ? payload.targetNodeId : payload.nodeId;
        string templateId = PayloadTaskTemplateId(payload);
        for (int i = 0; i < tasks.Length; i++)
        {
            LycheeReplayTaskState task = tasks[i];
            if (task == null) continue;
            if (!string.IsNullOrEmpty(payload.taskId) && task.taskId == payload.taskId) return task;
            if (!string.IsNullOrEmpty(templateId) && (task.taskTemplateId == templateId || task.templateId == templateId)) return task;
            string taskNode = !string.IsNullOrEmpty(task.targetNodeId) ? task.targetNodeId : task.nodeId;
            if (!string.IsNullOrEmpty(nodeId) && taskNode == nodeId) return task;
        }
        return null;
    }

    string TaskDisplayName(LycheeReplayPayload payload)
    {
        LycheeReplayTaskState task = FindTaskState(payload);
        if (task != null && !string.IsNullOrEmpty(task.name)) return task.name;
        return TaskTemplateName(PayloadTaskTemplateId(payload));
    }

    int TaskScore(LycheeReplayPayload payload)
    {
        if (payload != null && payload.score > 0) return payload.score;
        LycheeReplayTaskState task = FindTaskState(payload);
        return task != null ? task.score : 0;
    }

    static string PayloadTaskTemplateId(LycheeReplayPayload payload)
    {
        if (payload == null) return null;
        if (!string.IsNullOrEmpty(payload.taskTemplateId)) return payload.taskTemplateId;
        if (!string.IsNullOrEmpty(payload.templateId)) return payload.templateId;
        if (!string.IsNullOrEmpty(payload.taskId))
        {
            int split = payload.taskId.IndexOf('_');
            if (split > 1 && payload.taskId[0] == 'T')
                return payload.taskId.Substring(0, split);
        }
        return null;
    }

    static string TaskNameWithSuffix(string taskName)
    {
        if (string.IsNullOrEmpty(taskName)) return "皇榜任务";
        return taskName.EndsWith("任务", StringComparison.Ordinal) ? taskName : taskName + "任务";
    }

    static string TaskTemplateName(string templateId)
    {
        switch (templateId)
        {
            case "T01": return "限时过关";
            case "T02": return "抵驿催运";
            case "T04": return "清障任务";
            case "T05": return "争柜取冰";
            case "T07": return "官凭验牒";
            case "T08": return "码头争船";
            case "T09": return "宫门催运";
            case "T10": return "御前验鲜";
            case "T11": return "栈道复核";
            default: return "皇榜";
        }
    }

    static string ShortDetail(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
        return value.Substring(0, Mathf.Max(1, maxLength - 1)) + "...";
    }

    static string DurationText(int rounds)
    {
        return rounds > 0 ? rounds + "回合" : "短时";
    }

    static string WeatherName(string value)
    {
        if (value == "HOT") return "酷暑";
        if (value == "HEAVY_RAIN") return "暴雨";
        if (value == "MOUNTAIN_FOG") return "山雾";
        if (value == "CLEAR") return "晴朗";
        if (value == "NIGHT_WIND") return "夜风";
        return "天气";   // 兜底改中文
    }

    static string RegionName(string value)
    {
        if (value == "ALL") return "";
        if (value == "WATER") return "水路";
        if (value == "MOUNTAIN") return "山路";
        if (value == "PLAIN") return "平原";
        return "区域";   // 兜底改中文
    }

    static string WeatherCoverageText(string region)
    {
        string name = RegionName(region);
        return string.IsNullOrEmpty(name) ? "" : " 覆盖 " + name;
    }

    static string ResourceName(string value)
    {
        if (value == "ICE_BOX") return "冰鉴";
        if (value == "FAST_HORSE") return "快马";
        if (value == "SHORT_HORSE") return "短驿马";
        if (value == "PASS_TOKEN") return "通关令";
        if (value == "OFFICIAL_PERMIT") return "官牒";
        if (value == "INTEL") return "情报";
        if (value == "BOAT_RIGHT") return "船班权益";
        return "资源";   // 兜底改中文
    }

    static string ActionName(string value)
    {
        if (value == "MOVE") return "移动";
        if (value == "DELIVER") return "交付";
        if (value == "WAIT") return "等待";
        if (value == "BOARD") return "登船处理";
        if (value == "TRANSFER") return "前段交接";
        if (value == "WATER_TRANSFER") return "水路换运";
        if (value == "PASS_TRANSFER") return "入关交接";
        if (value == "PALACE_TRANSFER") return "宫前交接";
        if (value == "PROCESS") return "站点处理";
        if (value == "SET_GUARD") return "设卡";
        if (value == "BREAK_GUARD") return "攻坚破卡";
        if (value == "FORCED_PASS") return "强制通行";
        if (value == "CLEAR_OBSTACLE" || value == "SQUAD_CLEAR" || value == "SQUAD_CLEAR_SUCCESS" || value == "CLEAR") return "清障";
        if (value == "CLAIM_RESOURCE") return "领取资源";
        if (value == "USE_RESOURCE") return "使用资源";
        if (value == "CLAIM_TASK") return "处理皇榜任务";
        if (value == "USE_RUSH_TACTIC") return "使用急策";
        if (value == "RUSH_SPEED") return "疾行令";
        if (value == "RUSH_PROTECT") return "护果令";
        if (value == "VERIFY" || value == "VERIFY_GATE" || value == "RUSH_VERIFY_GATE") return "宫门验核";
        if (value == "BREAK_ORDER") return "破关令";
        if (value == "PASS_NODE") return "限时过关";
        if (value == "STATION_PROCESS") return "驿站催运";
        return "行动";   // 兜底改中文
    }

    static string BountyName(string value)
    {
        if (value == "KEY_BOUNTY") return "关隘悬赏";
        if (value == "NORMAL_BOUNTY") return "普通悬赏";
        return "悬赏";   // 兜底改中文
    }

    static string ObstacleName(string value)
    {
        if (value == "ROCKFALL") return "落石阻路";
        if (value == "BROKEN_BRIDGE") return "断桥阻路";
        if (value == "DOCK_BLOCK") return "码头堵塞";
        if (value == "FLOOD") return "洪水阻路";
        if (value == "MUD") return "泥泞阻路";
        if (value == "PASS_CROWD") return "关口拥堵";
        if (value == "LANDSLIDE") return "山体滑坡";
        return "道路障碍";   // 兜底改中文
    }

    /// <summary>动作被拒/非法动作的 errorCode（及个别英文 message）→ 中文，全部兜底中文，绝不上屏英文。</summary>
    public static string ErrorCodeName(string code)
    {
        if (string.IsNullOrEmpty(code)) return "规则拒绝";
        switch (code)
        {
            case "ACTION_REJECTED": return "业务拒绝";
            case "OBJECT_BUSY": return "对象忙";
            case "TASK_PROTECTED": return "任务保护中";
            case "TASK_NOT_FOUND": return "任务不存在";
            case "ALREADY_VERIFIED": return "已验核";
            case "ALREADY_DELIVERED": return "已交付";
            case "DELIVER_NOT_VERIFIED": return "未验核不可交付";
            case "DELIVER_REQUIREMENT_NOT_MET": return "交付条件不满足";
            case "RESOURCE_NOT_ENOUGH":
            case "RESOURCE_INSUFFICIENT": return "资源不足";
            case "RESOURCE_NOT_USABLE": return "资源不可使用";
            case "NOT_AT_TARGET_NODE": return "不在目标节点";
            case "TARGET_NOT_REACHABLE": return "目标不可达";
            case "MOVE_BLOCKED_BY_GUARD": return "设卡阻挡";
            case "PARAM_OUT_OF_RANGE": return "参数越界";
            case "WINDOW_DRAW_RETRY_LIMIT": return "平局冷却中";
            case "WINDOW_CONTEST_REPEAT_SUPPRESSED": return "重复窗口已抑制";
            case "RESTING_ACTION_FORBIDDEN": return "休整中禁此动作";
            case "INVALID_ACTION_TYPE": return "非法动作类型";
            case "INVALID_ACTION_CONFLICT": return "同类动作冲突";
            case "hidden resource probe rejected": return "试探隐藏信息被拒";
            default: return "规则拒绝";   // 未知一律中文兜底，不上屏英文
        }
    }

    static int StableHash(string value)
    {
        unchecked
        {
            int hash = 23;
            if (!string.IsNullOrEmpty(value))
            {
                for (int i = 0; i < value.Length; i++)
                    hash = hash * 31 + value[i];
            }
            return Mathf.Abs(hash);
        }
    }

    static bool IsMajorEvent(string type)
    {
        // 中央弹窗兜底只留高价值类别（任务/悬赏/验核/交付/冲刺）：
        // GUARD/WEATHER/SQUAD 的高价值变体都有上面的专属分支，这里再兜会把衰减、预报
        // 之类的低价值变体也推上屏；唯一调用方是 ShowKeyEventIllustration，收紧无副作用，
        // 左侧事件流（LycheeHudExtensions）自己订阅事件、不经过这里。
        return type.Contains("TASK") ||
            type.Contains("BOUNTY") ||
            type.Contains("VERIFY") ||
            type.Contains("DELIVER") ||
            type.Contains("RUSH");
    }

    GameObject CreateNormalizedModel(
        GameObject prefab,
        Transform parent,
        string objectName,
        float targetSize)
    {
        if (prefab == null) return null;
        GameObject wrapper = new GameObject(objectName);
        wrapper.transform.SetParent(parent, false);
        GameObject model = Instantiate(prefab, wrapper.transform);
        model.name = prefab.name;
        Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return wrapper;
        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
        float largest = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
        if (largest > 0.0001f) model.transform.localScale *= targetSize / largest;
        bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
        // 地面对齐用的是世界包围盒，必须相对 wrapper(根)的世界坐标做偏移，否则当根已被预先定位
        // (小队 SpawnSquad 先把 root 放到出发点、再建成员)时，会把模型直接平移到世界原点(0,0,0)——
        // 表现就是"光圈+文字在小队处、3D 兵却被甩到地图原点不见了"，且离原点越远越明显。
        // 车队是"先在原点建成员、后定位"，wrapper 在原点，wp≈0，与原行为一致，不受影响。
        Vector3 wp = wrapper.transform.position;
        model.transform.position -= new Vector3(bounds.center.x - wp.x, bounds.min.y - wp.y, bounds.center.z - wp.z);
        foreach (Collider collider in model.GetComponentsInChildren<Collider>(true))
            collider.enabled = false;
        ApplyCharacterBrightness(wrapper, objectName);
        return wrapper;
    }

    static bool IsCharacterVisual(string objectName)
    {
        return objectName == "Captain" ||
            objectName.StartsWith("TeamMember_", StringComparison.Ordinal) ||
            objectName.StartsWith("Member_", StringComparison.Ordinal);
    }

    static void ApplyCharacterBrightness(GameObject root, string objectName)
    {
        if (root == null || string.IsNullOrEmpty(objectName) || !IsCharacterVisual(objectName)) return;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        foreach (Renderer renderer in renderers)
        {
            Material[] materials = renderer.materials;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null) continue;

                // glTF PBR 模型（将军/赤龙将军）常被导出成 metallicFactor=1 全金属：全金属面不显示
                // albedo 贴图、只反射环境，在偏暗环境里渲染成"黑黢黢一团"。把金属度压到接近 0，让角色
                // 本身的贴图正常受光显示；再给一点点 emissive 兜底，避免阴影侧仍偏黑。这条对 _BaseColor
                // 找不到的 glTF 材质同样生效（下面的提亮分支只认 _BaseColor/_Color，会漏掉将军）。
                if (material.HasProperty("metallicFactor") && material.GetFloat("metallicFactor") > 0.3f)
                    material.SetFloat("metallicFactor", 0.05f);
                if (material.HasProperty("_Metallic") && material.GetFloat("_Metallic") > 0.3f)
                    material.SetFloat("_Metallic", 0.05f);
                if (material.HasProperty("emissiveFactor") && material.GetColor("emissiveFactor").maxColorComponent < 0.02f)
                    material.SetColor("emissiveFactor", new Color(0.06f, 0.06f, 0.06f, 1f));

                Color baseColor = Color.white;
                bool hasBase = false;
                if (material.HasProperty("_BaseColor"))
                {
                    baseColor = material.GetColor("_BaseColor");
                    hasBase = true;
                }
                else if (material.HasProperty("_Color"))
                {
                    baseColor = material.GetColor("_Color");
                    hasBase = true;
                }
                if (!hasBase) continue;

                Color boosted = new Color(
                    Mathf.Clamp01(baseColor.r * 1.18f + 0.045f),
                    Mathf.Clamp01(baseColor.g * 1.18f + 0.045f),
                    Mathf.Clamp01(baseColor.b * 1.18f + 0.045f),
                    baseColor.a);
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", boosted);
                if (material.HasProperty("_Color")) material.SetColor("_Color", boosted);
                if (material.HasProperty("_EmissionColor"))
                {
                    Color emission = new Color(boosted.r, boosted.g, boosted.b, 1f) * 0.075f;
                    material.EnableKeyword("_EMISSION");
                    material.SetColor("_EmissionColor", emission);
                    material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                }
            }
            renderer.materials = materials;
        }
    }

    TextMesh CreateLabel(Transform parent, string text, Color color)
    {
        GameObject labelObject = new GameObject("Label");
        labelObject.transform.SetParent(parent, false);
        labelObject.transform.localPosition = new Vector3(0f, 2.25f, 0f);
        labelObject.AddComponent<LycheeReplayBillboard>();
        TextMesh label = labelObject.AddComponent<TextMesh>();
        label.text = text;
        label.anchor = TextAnchor.MiddleCenter;
        label.alignment = TextAlignment.Center;
        label.fontSize = 32;
        label.characterSize = 0.072f;   // 放大（原 0.055），主队/小分队头顶名字看得清（用户点4）
        label.fontStyle = FontStyle.Bold; // 加粗，原来太细
        label.color = color;
        return label;
    }

    void CreateTeamRing(Transform parent, int camp)
    {
        GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ring.name = "TeamRing";
        ring.transform.SetParent(parent, false);
        ring.transform.localPosition = new Vector3(0f, 0.05f, 0f);
        // 放大并提亮成"脚下队色光圈"：红蓝一眼可分；emission 自发光，不被光照/后期对比度压暗
        ring.transform.localScale = new Vector3(1.6f, 0.022f, 1.6f);
        Material material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        Color color = camp == 0 ? new Color(1f, 0.20f, 0.12f) : new Color(0.10f, 0.45f, 1f);
        material.SetColor("_BaseColor", color);
        material.EnableKeyword("_EMISSION");
        material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        material.SetColor("_EmissionColor", color * 1.6f);
        ring.GetComponent<MeshRenderer>().material = material;
        Destroy(ring.GetComponent<Collider>());
    }

    static string ShortEventName(string eventType)
    {
        // 统一走 EventNameCn 中文映射（含全部 V4.8.12 事件 + 中文兜底"事件"），不再显英文裸名
        return LycheeHudExtensions.EventNameCn(eventType);
    }

    string WorldEventLabel(string eventType, LycheeReplayPayload payload)
    {
        payload = payload ?? new LycheeReplayPayload();
        if (eventType == "RESOURCE_CLAIM") return "获得" + ResourceName(payload.resourceType);
        if (eventType == "RESOURCE_USE") return "使用" + ResourceName(payload.resourceType);
        if (eventType == "PROCESS_PROGRESS") return ActionName(payload.processType);
        if (eventType == "PROCESS_COMPLETE") return "完成" + ActionName(payload.processType);
        if (eventType == "SQUAD_CLEAR" || eventType == "SQUAD_CLEAR_SUCCESS" || eventType == "OBSTACLE_CLEAR") return TeamName(payload.playerId) + "清障成功";
        if (eventType == "SQUAD_FAILED") return TeamName(payload.playerId) + "小队失败";
        if (eventType == "TASK_COMPLETE") return "任务完成";
        if (eventType == "TASK_REFRESH" || eventType == "TASK_SPAWN") return "任务刷新";
        if (eventType == "DELIVER_SUCCESS") return "交付成功";
        if (eventType == "VERIFY_GATE_COMPLETE") return "验核通过";
        return ShortEventName(eventType);
    }

    static Color EventColor(string eventType)
    {
        if (eventType.Contains("WEATHER")) return new Color(0.28f, 0.74f, 1f);
        if (eventType.Contains("GUARD") || eventType.Contains("FORCED")) return new Color(1f, 0.28f, 0.14f);
        if (eventType.Contains("TASK") || eventType.Contains("BOUNTY")) return new Color(1f, 0.78f, 0.16f);
        if (eventType.Contains("DELIVER") || eventType.Contains("VERIFY")) return new Color(0.34f, 1f, 0.42f);
        if (eventType.Contains("RESOURCE")) return new Color(0.95f, 0.70f, 0.24f);
        if (eventType.Contains("PROCESS")) return new Color(0.42f, 0.86f, 1f);
        if (eventType.Contains("SQUAD")) return new Color(1f, 0.72f, 0.18f);
        return new Color(1f, 0.72f, 0.18f);
    }

    // ===== 事件世界图标：把 EventIcons/*.png 作为事件脉冲上方朝向相机的贴片，
    //       让“地图远端事件激活”在拉远镜头下也能按图标一眼识别类别 =====
    static string EventIconPath(string t, LycheeReplayPayload payload = null)
    {
        if (string.IsNullOrEmpty(t)) return null;
        if (t.Contains("WEATHER")) return WeatherIconPath(payload == null ? null : payload.weatherType);
        if (t == "SQUAD_FAILED" || t == "SQUAD_FAIL")
        {
            string action = payload == null
                ? string.Empty
                : (payload.action ?? payload.tactic ?? payload.rushTactic ?? string.Empty);
            action = action.Trim().ToUpperInvariant();
            if (action.Contains("CLEAR"))
                return "EventIcons/icon_event_clear_obstacle.png";
        }
        string mapped = LycheeEventIconMap.ResolveFile(t, false);
        if (!string.IsNullOrEmpty(mapped)) return mapped;
        if (t == "MATCH_START" || t == "NODE_ENTER") return "icon_task.png";
        if (t.StartsWith("WINDOW", StringComparison.Ordinal)) return "EventIcons/icon_event_contest_swords.png";
        if (t == "OBSTACLE_CLEAR" || t == "SQUAD_CLEAR" || t == "SQUAD_CLEAR_SUCCESS") return "EventIcons/icon_event_clear_obstacle.png";
        if (t == "GUARD_SET") return "EventIcons/icon_event_guard_set.png";
        if (t == "GUARD_BREAK") return "EventIcons/icon_event_guard_break.png";
        if (t.Contains("GUARD")) return "icon_guard.png";
        if (t == "SQUAD_DISPATCH") return "EventIcons/icon_event_squad_dispatch.png";
        if (t == "SQUAD_ARRIVE") return "EventIcons/icon_event_squad_arrive.png";
        if (t == "SQUAD_FAILED") return "EventIcons/icon_event_squad_fail.png";
        if (t.StartsWith("SQUAD_", StringComparison.Ordinal)) return "EventIcons/icon_event_squad_dispatch.png";
        if (t == "RESOURCE_CLAIM" || t == "RESOURCE_USE") return "EventIcons/icon_event_resource_claim.png";
        if (t.StartsWith("PROCESS_", StringComparison.Ordinal)) return "icon_task.png";
        if (t.StartsWith("RUSH_", StringComparison.Ordinal)) return "EventIcons/icon_event_contest_swords.png";
        if (t == "ACTION_REJECTED" || t == "INVALID_ACTION") return "EventIcons/icon_event_action_invalid.png"; // 与 event_icon_mapping.json 对齐（JSON 缺载时的正确兜底）
        if (t == "FRESHNESS_DROP" || t == "GOOD_TO_BAD") return "icon_resource_icebox.png";
        if (t.StartsWith("COST_", StringComparison.Ordinal)) return "icon_resource_passtoken.png";
        if (t == "MATCH_END") return "EventIcons/icon_event_match_end.png"; // 与 JSON 对齐（原 delivery_success 与 verify 撞图且偏离 JSON）
        if (t.StartsWith("VERIFY", StringComparison.Ordinal)) return "EventIcons/icon_event_verify_gate.png";
        if (t.StartsWith("FORCED", StringComparison.Ordinal)) return "EventIcons/icon_event_forced_pass.png";
        if (t == "DELIVER_SUCCESS") return "EventIcons/icon_event_delivery_success.png";
        if (t.Contains("TASK")) return "icon_task.png";
        if (t.Contains("BOUNTY")) return "icon_bounty.png";
        return null;
    }

    static string WeatherIconPath(string weatherType)
    {
        string type = string.IsNullOrEmpty(weatherType) ? string.Empty : weatherType.Trim().ToUpperInvariant();
        if (type == "HOT") return "icon_weather_hot.png";
        if (type == "MOUNTAIN_FOG") return "icon_weather_fog.png";
        if (type == "HEAVY_RAIN") return "icon_weather_rain.png";
        return "icon_weather_rain.png";
    }

    /// <summary>事件类型 → 事件卡分类边框类别（battle/resource/weather/squad/verification）；无专属类别返回 null 走通用边框。</summary>
    static string EventBorderCategory(string t)
    {
        if (string.IsNullOrEmpty(t)) return null;
        if (t == "MATCH_START" || t == "NODE_ENTER") return "resource";
        if (t.Contains("WEATHER") || t == "FRESHNESS_DROP" || t == "GOOD_TO_BAD") return "weather";
        if (t.StartsWith("SQUAD_", StringComparison.Ordinal)) return "squad";
        if (t.Contains("RESOURCE") || t.Contains("TASK") || t.Contains("BOUNTY") ||
            t.StartsWith("PROCESS_", StringComparison.Ordinal) || t.StartsWith("COST_", StringComparison.Ordinal)) return "resource";
        if (t.Contains("VERIFY") || t.Contains("DELIVER") || t == "MATCH_END") return "verification";
        if (t.Contains("WINDOW") || t.Contains("CONTEST") || t.Contains("CARD") ||
            t.Contains("GUARD") || t.Contains("FORCED") || t.Contains("OBSTACLE") ||
            t.StartsWith("RUSH_", StringComparison.Ordinal) ||
            t == "ACTION_REJECTED" || t == "INVALID_ACTION") return "battle";
        return null;
    }

    readonly Dictionary<string, Sprite> eventIconSprites = new Dictionary<string, Sprite>();

    Sprite ResolveEventIcon(string eventType, LycheeReplayPayload payload = null)
    {
        string path = EventIconPath(eventType, payload);
        if (string.IsNullOrEmpty(path)) return null;
        if (eventIconSprites.TryGetValue(path, out Sprite cached)) return cached;
        Sprite sprite = null;
        byte[] bytes = LycheeThemeCache.LoadBytes(path);
        if (bytes != null)
        {
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (texture.LoadImage(bytes, markNonReadable: true)) // 上传 GPU 后立即释放 CPU 端 RGBA 副本：省 WASM 堆/内存(这些卡牌/图标贴图运行中逐张缓存、从不回收)
            {
                texture.wrapMode = TextureWrapMode.Clamp;
                sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f), Mathf.Max(texture.width, texture.height));
            }
            else
            {
                Destroy(texture);
            }
        }
        // WebGL 下 UITheme 是异步预加载；预加载完成前不要把一次空读写死进缓存。
        if (sprite != null || LycheeThemeCache.Preloaded) eventIconSprites[path] = sprite;
        return sprite;
    }

    readonly Dictionary<string, Sprite> eventIllustrationSprites = new Dictionary<string, Sprite>();

    /// <summary>非对抗事件 → 专属卡片插画路径（StreamingAssets/UITheme/Illustrations/card_*.png）；无专属图返回 null（HUD 隐藏图片区）。</summary>
    static string EventIllustrationPath(string t, LycheeReplayPayload payload)
    {
        if (string.IsNullOrEmpty(t)) return null;
        if (t == "NODE_ENTER") return "Illustrations/card_arrive.png";
        if (t == "TASK_SPAWN" || t == "TASK_REFRESH") return "Illustrations/card_task_post.png";
        if (t == "TASK_COMPLETE") return "Illustrations/card_task_done.png";
        if (t == "RESOURCE_USE") return "Illustrations/card_resource_use.png";          // 使用补给：与"获得补给"分流
        if (t == "RESOURCE_CLAIM") return "Illustrations/card_resource.png";
        if (t == "PROCESS_INTERRUPT") return "Illustrations/card_process_interrupt.png"; // 流程中断
        if (t == "PROCESS_COMPLETE") return "Illustrations/card_process_complete.png";    // 流程完成
        if (t.StartsWith("PROCESS_", StringComparison.Ordinal)) return "Illustrations/card_process.png"; // PROCESS_START/PROGRESS 兜底
        if (t == "FORCED_PASS_START" || t == "FORCED_PASS_END") return "Illustrations/combat_forced_pass.png"; // 强行通关（专属图，不再共用对抗图集）
        if (t == "SQUAD_DISPATCH") return "Illustrations/card_dispatch.png";
        if (t == "SQUAD_ARRIVE") return "Illustrations/card_squad_arrive.png";
        if (t == "SQUAD_FAILED") return "Illustrations/combat_squad_failed.png";         // 小队攻坚失败（专属图）
        if (t.StartsWith("SQUAD_", StringComparison.Ordinal)) return "Illustrations/card_squad_arrive.png"; // 其余非对抗小队事件（战术生效等）兜底，避免空白
        if (t == "GUARD_SET") return "Illustrations/card_guard.png";
        if (t == "MATCH_END") return "Illustrations/card_finish.png";
        if (t == "ACTION_REJECTED" || t == "INVALID_ACTION") return "Illustrations/card_action_invalid.png";
        if (t == "BOUNTY_SPAWN" || t == "BOUNTY_CREATE") return "Illustrations/card_bounty.png";
        if (t == "BOUNTY_COMPLETE" || t == "BOUNTY_CLAIM") return "Illustrations/card_bounty_done.png";
        if (t == "VERIFY_GATE_START") return "Illustrations/card_verify.png";
        if (t == "VERIFY_GATE_COMPLETE") return "Illustrations/card_verify_done.png";
        if (t == "DELIVER_SUCCESS") return "Illustrations/card_deliver.png";
        if (t == "RUSH_PHASE_START" || t == "RUSH_START") return "Illustrations/card_rush.png";
        if (t.Contains("WEATHER"))
        {
            string w = string.IsNullOrEmpty(payload == null ? null : payload.weatherType)
                ? string.Empty : payload.weatherType.Trim().ToUpperInvariant();
            if (w == "MOUNTAIN_FOG") return "Illustrations/card_weather_fog.png";
            if (w == "HOT") return "Illustrations/card_weather_hot.png";
            if (w == "HEAVY_RAIN" || w == "RAIN") return "Illustrations/card_weather_rain.png";
            return "Illustrations/card_weather.png";
        }
        return null;
    }

    /// <summary>加载非对抗事件的专属卡片插画（机制同 ResolveEventIcon：主题缓存读字节→Sprite，按路径缓存）。</summary>
    Sprite ResolveEventIllustration(string eventType, LycheeReplayPayload payload)
    {
        string path = EventIllustrationPath(eventType, payload);
        if (string.IsNullOrEmpty(path)) return null;
        if (eventIllustrationSprites.TryGetValue(path, out Sprite cached)) return cached;
        Sprite sprite = null;
        byte[] bytes = LycheeThemeCache.LoadBytes(path);
        if (bytes != null)
        {
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (texture.LoadImage(bytes, markNonReadable: true)) // 上传 GPU 后立即释放 CPU 端 RGBA 副本：省 WASM 堆/内存(这些卡牌/图标贴图运行中逐张缓存、从不回收)
            {
                texture.wrapMode = TextureWrapMode.Clamp;
                sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f), 100f);
            }
            else
            {
                Destroy(texture);
            }
        }
        if (sprite != null || LycheeThemeCache.Preloaded) eventIllustrationSprites[path] = sprite;
        return sprite;
    }

    Sprite eventIconBackingSprite;

    void CreateEventIconBillboard(Transform parent, Sprite icon, string status)
    {
        GameObject iconObject = new GameObject("EventIcon");
        iconObject.transform.SetParent(parent, false);
        iconObject.transform.localPosition = new Vector3(0f, 4.0f, 0f);  // 抬到角色头顶之上，不挡主角
        iconObject.transform.localScale = Vector3.one * 1.9f;            // 与世界常驻徽章同级尺寸，避免事件完成后突然变巨
        // 深色软圆底衬：图标在杂乱草地/碎石路上更跳出
        GameObject backing = new GameObject("EventIconBacking");
        backing.transform.SetParent(iconObject.transform, false);
        backing.transform.localScale = Vector3.one * 1.22f;
        SpriteRenderer backRenderer = backing.AddComponent<SpriteRenderer>();
        backRenderer.sprite = EventIconBackingSprite();
        backRenderer.color = new Color(0.05f, 0.05f, 0.08f, 0.6f);
        backRenderer.sortingOrder = 29999;
        SpriteRenderer iconRenderer = iconObject.AddComponent<SpriteRenderer>();
        iconRenderer.sprite = icon;
        iconRenderer.sortingOrder = 30000;
        CreateEventStatusMark(iconObject.transform, status);
        LycheeReplayBillboard billboard = iconObject.AddComponent<LycheeReplayBillboard>();
        billboard.maxScale = 2.6f; // 远景拉远时图标也保持可辨识
    }

    void CreateEventStatusMark(Transform parent, string status)
    {
        if (string.IsNullOrEmpty(status)) return;
        bool success = status == "success";
        GameObject root = new GameObject(success ? "EventStatusSuccess" : "EventStatusFail");
        root.transform.SetParent(parent, false);
        root.transform.localPosition = new Vector3(0.42f, 0.42f, -0.04f);
        root.transform.localScale = Vector3.one * 0.42f;

        SpriteRenderer backRenderer = root.AddComponent<SpriteRenderer>();
        backRenderer.sprite = EventIconBackingSprite();
        backRenderer.color = success
            ? new Color(0.02f, 0.34f, 0.10f, 0.94f)
            : new Color(0.42f, 0.04f, 0.03f, 0.94f);
        backRenderer.sortingOrder = 30001;

        GameObject textObject = new GameObject("StatusGlyph");
        textObject.transform.SetParent(root.transform, false);
        textObject.transform.localPosition = new Vector3(0f, 0.01f, -0.04f);
        TextMesh mark = textObject.AddComponent<TextMesh>();
        mark.text = success ? "✓" : "×";
        mark.anchor = TextAnchor.MiddleCenter;
        mark.alignment = TextAlignment.Center;
        mark.fontSize = 64;
        mark.characterSize = 0.065f;
        mark.color = Color.white;
        MeshRenderer renderer = mark.GetComponent<MeshRenderer>();
        if (renderer != null) renderer.sortingOrder = 30002;
    }

    /// <summary>事件图标的深色软圆底衬贴图（程序化生成一次并缓存）。</summary>
    Sprite EventIconBackingSprite()
    {
        if (eventIconBackingSprite != null) return eventIconBackingSprite;
        const int size = 64;
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        Vector2 c = new Vector2(size * 0.5f, size * 0.5f);
        float radius = size * 0.5f;
        Color[] px = new Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), c) / radius;
                float a = 1f - Mathf.SmoothStep(0.72f, 1f, d); // 软边圆盘
                px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(a));
            }
        tex.SetPixels(px);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.Apply();
        eventIconBackingSprite = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        return eventIconBackingSprite;
    }

    void OnGUI()
    {
        if (!string.IsNullOrEmpty(startupError))
        {
            EnsureGuiStyles();
            float errorPanelWidth = Mathf.Min(760f, Screen.width - 48f);
            float errorPanelHeight = Mathf.Min(260f, Screen.height - 48f);
            Rect errorPanel = new Rect((Screen.width - errorPanelWidth) * 0.5f, (Screen.height - errorPanelHeight) * 0.5f, errorPanelWidth, errorPanelHeight);
            GUI.Box(errorPanel, GUIContent.none);
            GUI.Label(new Rect(errorPanel.x + 24f, errorPanel.y + 22f, errorPanel.width - 48f, errorPanel.height - 44f), startupError, startupErrorStyle);
            return;
        }
        if (!showLegacyTopHud) return;
        if (frames.Count == 0) return;
        EnsureGuiStyles();
        float width = Mathf.Min(610f, Screen.width - 32f);
        Rect panel = new Rect(16f, 16f, width, 126f);
        GUI.Box(panel, GUIContent.none);
        GUI.Label(new Rect(30f, 24f, width - 28f, 28f), "荔枝运送回放", titleStyle);
        GUI.Label(
            new Rect(30f, 52f, width - 28f, 24f),
            "回合 " + CurrentRound + " / " + frames[frames.Count - 1].round +
            "   阶段 " + (frames[frameIndex].phase == "RUSH" ? "冲刺" : "常规") +
            "   " + latestEvent,
            hudStyle);

        if (GUI.Button(new Rect(30f, 82f, 74f, 30f), playing ? "暂停" : "播放", buttonStyle))
            playing = !playing;
        if (GUI.Button(new Rect(112f, 82f, 58f, 30f), "重播", buttonStyle))
            SeekFrame(0);

        float speed = 1.00f / secondsPerRound;
        GUI.Label(new Rect(181f, 84f, 78f, 24f), speed.ToString("0.0") + "x", hudStyle);
        float sliderValue = GUI.HorizontalSlider(
            new Rect(250f, 91f, width - 275f, 18f),
            frameIndex,
            0,
            frames.Count - 1);
        int requested = Mathf.RoundToInt(sliderValue);
        if (requested != frameIndex) SeekFrame(requested);
    }

    void EnsureGuiStyles()
    {
        if (hudStyle != null) return;
        hudStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 15,
            normal = { textColor = new Color(0.92f, 0.92f, 0.86f) }
        };
        titleStyle = new GUIStyle(hudStyle)
        {
            fontSize = 21,
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(1f, 0.76f, 0.24f) }
        };
        buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 14 };
        startupErrorStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 20,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft,
            wordWrap = true,
            normal = { textColor = new Color(1f, 0.88f, 0.62f) }
        };
        // 绑中文字体：IMGUI 默认内置 Arial 无中文字形，WebGL 无系统字体 → OnGUI 中文(暂停/播放/重播/报错)空白。
        Font cjk = Resources.Load<Font>("Fonts/LXGWWenKai-Regular");
        if (cjk != null) { hudStyle.font = cjk; titleStyle.font = cjk; buttonStyle.font = cjk; startupErrorStyle.font = cjk; }
    }
}
