using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class LycheeReplayPlayer : MonoBehaviour
{
    [Header("Replay")]
    public TextAsset replayFile;
    // 0.22 秒/回合：0.10 时 600 回合一分钟跑完，事件演出根本来不及看；场景序列化值由编辑器脚本同步
    [Range(0.03f, 1f)] public float secondsPerRound = 0.40f;
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
    [Range(1, 12)] public int defaultDispatchMemberCount = 4;
    public Sprite redCaptainAvatar;
    public Sprite blueCaptainAvatar;
    public Sprite[] combatIllustrations;

    LycheeReplayFrame startFrame;
    readonly List<LycheeReplayFrame> frames = new List<LycheeReplayFrame>();
    readonly Dictionary<int, ConvoyVisual> convoys = new Dictionary<int, ConvoyVisual>();
    readonly Dictionary<int, int> teamRosterSizes = new Dictionary<int, int>();
    readonly Dictionary<string, MapStationAnchor> stations = new Dictionary<string, MapStationAnchor>();
    readonly Dictionary<string, MapRouteEdge> edges = new Dictionary<string, MapRouteEdge>();
    readonly Dictionary<string, GameObject> obstacles = new Dictionary<string, GameObject>();
    readonly List<SquadVisual> squads = new List<SquadVisual>();
    readonly List<GameObject> eventEffects = new List<GameObject>();

    MapElevationData elevation;
    Transform dynamicRoot;
    Transform eventRoot;
    int frameIndex;
    int lastEventFrame = -1;
    float frameTime;
    bool playing;
    LycheeReplayFrame lastAppliedFrame;
    bool matchOverRaised;
    string latestEvent = "READY";
    Vector3 eventFocusPosition;
    float eventFocusUntil;
    /// <summary>当前事件聚焦的建议相机距离（米）；&lt;0 表示未指定、由相机自行决定。对抗类拉近（15）、普通事件保持远景（30）。</summary>
    float eventFocusDistance = -1f;
    /// <summary>WINDOW_CONTEST_START 缓存的对抗节点位置：WINDOW_CARD_REVEAL 的 payload 没有 targetNodeId，靠它续期聚焦让整场对抗镜头不回退。</summary>
    Vector3 contestFocusPosition;
    bool hasContestFocus;
    GUIStyle hudStyle;
    GUIStyle titleStyle;
    GUIStyle buttonStyle;
    LycheeReplayRuntimeHud runtimeHud;

    sealed class ConvoyVisual
    {
        public GameObject root;
        public GameObject carriage;
        public GameObject boat;
        public GameObject captain;
        public readonly List<GameObject> members = new List<GameObject>();
        public TextMesh label;
        public Vector3 smoothedPosition;
        public Vector3 positionVelocity;
        public Vector3 lastTangent = Vector3.forward;
        public bool initialized;
    }

    sealed class SquadVisual
    {
        public GameObject root;
        public Vector3 start;
        public Vector3 end;
        public int startRound;
        public int endRound;
        public int memberCount;
        /// <summary>攻坚目标站 nodeId（抵达时请求建筑透视，让攻击动作不被楼挡）。</summary>
        public string targetNodeId;
        /// <summary>抵达后的真实时间戳；&lt;0 表示仍在行军。抵达后停留做攻击演出再销毁。</summary>
        public float arrivedRealTime = -1f;
        /// <summary>跨河时垫在脚下的小船（懒创建，仅水面段激活）。</summary>
        public GameObject boat;
        /// <summary>与 LycheeCharacterMotion 共享的水面状态（船上站立 vs 陆上行军）。</summary>
        public LycheeSquadWaterState waterState;
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
    public LycheeReplayFrame CurrentFrame => frames.Count == 0 ? null : frames[frameIndex];
    public LycheeReplayFrame NextFrame => frames.Count == 0 ? null : frames[Mathf.Min(frameIndex + 1, frames.Count - 1)];
    public LycheeReplayFrame StartFrameData => startFrame;
    public LycheeReplayOverFrame OverFrame { get; private set; }
    public string ReplaySource { get; private set; }
    public int TotalRounds => frames.Count == 0 ? 0 : frames[frames.Count - 1].round;
    public float FrameInterpolation => secondsPerRound <= 0f ? 1f : Mathf.Clamp01(frameTime / secondsPerRound);

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
        if (convoys.Count == 0) return false;

        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        foreach (ConvoyVisual convoy in convoys.Values)
        {
            Vector3 position = convoy.root.transform.position;
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }
        center = (min + max) * 0.5f;
        span = Mathf.Max(max.x - min.x, max.z - min.z);
        return true;
    }

    /// <summary>旧签名保留：相机控制器/卡牌特效/天气特效等既有调用方不关心距离时继续可用。</summary>
    public bool TryGetEventFocus(out Vector3 position)
    {
        return TryGetEventFocus(out position, out _);
    }

    /// <summary>事件聚焦扩展查询：除位置外返回建议聚焦距离（米）。focusDistance &lt; 0 表示事件未指定，由相机沿用自身默认值。</summary>
    public bool TryGetEventFocus(out Vector3 position, out float focusDistance)
    {
        position = eventFocusPosition;
        focusDistance = eventFocusDistance;
        return Time.unscaledTime < eventFocusUntil;
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

            Vector3 center = edge.Evaluate(state.moveProgress);
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
        playing = playOnStart;
        if (frames.Count > 0)
        {
            ApplyFrame(0, true);
            InterpolateConvoys(0f);
            UpdateRuntimeHud();
            ShowOpeningRoster();
        }
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    /// <summary>WebGL 画质裁剪：编辑器/桌面取向的 MSAA4x+SMAA High 在浏览器里帧率撑不住。</summary>
    void ApplyWebGLQualityTrim()
    {
        QualitySettings.shadowDistance = Mathf.Min(QualitySettings.shadowDistance, 60f);
        if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline is
            UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset urpAsset)
        {
            urpAsset.msaaSampleCount = 2;
            urpAsset.renderScale = 1f;
        }
        Camera sceneCamera = Camera.main ?? FindFirstObjectByType<Camera>();
        if (sceneCamera != null)
        {
            var cameraData = sceneCamera.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            if (cameraData != null)
                cameraData.antialiasing = UnityEngine.Rendering.Universal.AntialiasingMode.FastApproximateAntialiasing;
        }
    }

    System.Collections.IEnumerator LoadReplayThenInitialize()
    {
        // 先预载 UITheme 主题贴图（WebGL 不能同步读 StreamingAssets），
        // 保证 HUD/演出构建时缓存已就绪，不会静默回落到程序化兜底。
        yield return LycheeThemeCache.Preload();
        // WebGL 上 StreamingAssets 是 URL，System.IO 不可用，必须走网络请求；
        // 加时间戳参数避免浏览器缓存旧的 reply.txt。
        string url = Application.streamingAssetsPath + "/reply.txt?t=" + DateTime.UtcNow.Ticks;
        string text = null;
        using (var request = UnityEngine.Networking.UnityWebRequest.Get(url))
        {
            yield return request.SendWebRequest();
            if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                text = request.downloadHandler.text;
                ReplaySource = "StreamingAssets/reply.txt";
            }
            else
            {
                Debug.LogWarning("Failed to fetch StreamingAssets/reply.txt: " + request.error, this);
            }
        }
        ParseReplay(text);
        InitializeAfterLoad();
    }
#endif

    void Update()
    {
        if (frames.Count == 0) return;
        if (playing)
        {
            frameTime += Time.deltaTime;
            while (frameTime >= secondsPerRound && playing)
            {
                frameTime -= secondsPerRound;
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
                ApplyFrame(frameIndex, false);
            }
        }

        float interpolation = secondsPerRound <= 0f ? 1f : Mathf.Clamp01(frameTime / secondsPerRound);
        InterpolateConvoys(interpolation);
        UpdateSquads(interpolation);
        UpdateRuntimeHud();
    }

    void CacheSceneData()
    {
        foreach (MapStationAnchor station in FindObjectsByType<MapStationAnchor>(FindObjectsSortMode.None))
            stations[station.stationId] = station;
        foreach (MapRouteEdge edge in FindObjectsByType<MapRouteEdge>(FindObjectsSortMode.None))
            edges[edge.edgeId] = edge;
        elevation = FindFirstObjectByType<MapElevationData>();
    }

    void LoadReplay()
    {
        string text = null;
        try
        {
            string streamingPath = System.IO.Path.Combine(Application.streamingAssetsPath, "reply.txt");
            if (System.IO.File.Exists(streamingPath))
            {
                text = System.IO.File.ReadAllText(streamingPath);
                ReplaySource = "StreamingAssets/reply.txt";
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Failed to read StreamingAssets/reply.txt: " + exception.Message, this);
            text = null;
        }

        ParseReplay(text);
    }

    void ParseReplay(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            if (replayFile == null)
            {
                Debug.LogError("Replay file is not assigned.", this);
                return;
            }
            text = replayFile.text;
            ReplaySource = "内置样例";
        }

        string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string line in lines)
        {
            if (line.Contains("\"type\":\"start\""))
            {
                startFrame = JsonUtility.FromJson<LycheeReplayFrame>(line);
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
        Debug.Log("Replay loaded from " + ReplaySource + ": " + frames.Count + " round frames" +
            (OverFrame != null ? ", over frame parsed." : "."), this);
        ValidateScoreConsistency();
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
                          detail.time + detail.tasks - Mathf.Abs(detail.penalty);
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
                    ResolveGeneralModel(player.camp), root.transform, "Captain", 1.35f)
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
            visual.label = CreateLabel(root.transform, player.teamId, player.camp == 0
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

        if (startPlayer != null && startPlayer.objs != null && startPlayer.objs.Length > 1)
            return Mathf.Max(1, startPlayer.objs.Length);
        if (player.objs != null && player.objs.Length > 1)
            return Mathf.Max(1, player.objs.Length);
        if (startPlayer != null && startPlayer.squadAvailable > 0)
            return Mathf.Max(defaultTeamSize, startPlayer.squadAvailable * 2);
        if (player.squadAvailable > 0)
            return Mathf.Max(defaultTeamSize, player.squadAvailable * 2);
        return Mathf.Max(2, defaultTeamSize);
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
            GameObject obstacle = CreateNormalizedModel(
                roadObstacleModel,
                eventRoot,
                "Obstacle_" + station.stationId,
                1.65f);
            if (obstacle == null) continue;
            obstacle.transform.position = station.transform.position + Vector3.up * 0.1f;
            obstacle.SetActive(false);
            obstacles[station.stationId] = obstacle;
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
            frame.scores);
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
        if (frame.messages != null && (force || index > lastEventFrame))
        {
            foreach (LycheeReplayMessage message in frame.messages)
                HandleEvent(message, frame.round);
            lastEventFrame = index;
        }
        ApplyObstacles(frame);
        RoundApplied?.Invoke(previousApplied, frame);
        lastAppliedFrame = frame;
    }

    void InterpolateConvoys(float t)
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
            EvaluatePlayerPose(player, nextPlayer, t, out Vector3 routePosition, out Vector3 tangent);
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
                visual.smoothedPosition = Vector3.SmoothDamp(
                    visual.smoothedPosition,
                    targetPosition,
                    ref visual.positionVelocity,
                    0.055f,
                    80f,
                    Time.deltaTime);
                Quaternion targetRotation = Quaternion.LookRotation(visual.lastTangent, Vector3.up);
                float rotationBlend = 1f - Mathf.Exp(-10f * Time.deltaTime);
                visual.root.transform.rotation = Quaternion.Slerp(
                    visual.root.transform.rotation,
                    targetRotation,
                    rotationBlend);
            }
            visual.root.transform.position = visual.smoothedPosition +
                Vector3.up * (0.10f + Mathf.Sin(Time.time * 6f + player.camp) * 0.025f);

            bool water = IsWaterEdge(player.routeEdgeId);
            if (visual.carriage != null) visual.carriage.SetActive(!water);
            if (visual.boat != null) visual.boat.SetActive(water);
            if (visual.captain != null) visual.captain.SetActive(true);
            if (visual.label != null)
                visual.label.text = player.teamId + "  " + player.state +
                    "\nFresh " + player.freshness.ToString("0") +
                    "  Fruit " + player.goodFruit +
                    "  Score " + player.score;
        }
    }

    void EvaluatePlayerPose(
        LycheeReplayPlayerState player,
        LycheeReplayPlayerState nextPlayer,
        float interpolation,
        out Vector3 position,
        out Vector3 tangent)
    {
        tangent = Vector3.zero;
        if (!string.IsNullOrEmpty(player.routeEdgeId) &&
            edges.TryGetValue(player.routeEdgeId, out MapRouteEdge edge))
        {
            float progress = player.moveProgress;
            if (nextPlayer != null && nextPlayer.routeEdgeId == player.routeEdgeId)
                progress = Mathf.Lerp(player.moveProgress, nextPlayer.moveProgress, interpolation);
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

        Vector2 grid = new Vector2(player.position.x, player.position.y);
        if (nextPlayer != null)
            grid = Vector2.Lerp(grid, new Vector2(nextPlayer.position.x, nextPlayer.position.y), interpolation);
        position = GridToWorld(grid.x, grid.y);
        if (nextPlayer != null)
        {
            Vector3 nextPosition = GridToWorld(nextPlayer.position.x, nextPlayer.position.y);
            tangent = nextPosition - position;
        }
    }

    void ApplyObstacles(LycheeReplayFrame frame)
    {
        if (frame.nodes == null) return;
        foreach (LycheeReplayNodeState node in frame.nodes)
        {
            if (obstacles.TryGetValue(node.nodeId, out GameObject obstacle))
                obstacle.SetActive(node.hasObstacle);
        }
    }

    void HandleEvent(LycheeReplayMessage message, int round)
    {
        ReplayEventRaised?.Invoke(message, round);
        if (message == null) return;
        latestEvent = "R" + round + "  " + message.type;
        LycheeReplayPayload payload = message.payload ?? new LycheeReplayPayload();

        if (message.type == "WINDOW_CONTEST_START")
        {
            if (runtimeHud != null)
                runtimeHud.ShowCombatStart(round, payload.targetNodeId, payload.totalRounds);
            if (!string.IsNullOrEmpty(payload.targetNodeId) && stations.TryGetValue(payload.targetNodeId, out MapStationAnchor contestStation))
            {
                // 缓存对抗节点位置：后续 WINDOW_CARD_REVEAL 没有 targetNodeId，靠它续期聚焦
                contestFocusPosition = contestStation.transform.position;
                hasContestFocus = true;
                SetEventFocus(contestFocusPosition, 4.5f, 15f);
                SpawnEventPulse(contestFocusPosition, "对抗开始", new Color(1f, 0.78f, 0.22f));
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

        if (message.type == "SQUAD_DISPATCH")
        {
            if (!string.IsNullOrEmpty(payload.targetNodeId) &&
                stations.TryGetValue(payload.targetNodeId, out MapStationAnchor squadTarget))
                SetEventFocus(squadTarget.transform.position, 2.8f, 30f);
            if (runtimeHud != null)
            {
                string teamName = TeamName(payload.playerId);
                runtimeHud.ShowDispatch(teamName + "派出小分队 x" + SquadMemberCount(payload) + "  " + CleanNode(payload.fromNodeId) + " → " + CleanNode(payload.targetNodeId));
            }
            ShowKeyEventIllustration(message, payload, round);
            SpawnSquad(payload, round);
            return;
        }

        if (message.type.StartsWith("SQUAD_", StringComparison.Ordinal))
        {
            if (runtimeHud != null)
            {
                string detail = ShortEventName(message.type);
                if (message.type.Contains("WEAKEN") || message.type.Contains("REINFORCE"))
                    detail += "  防御 " + payload.defenseBefore + " → " + payload.defenseAfter;
                runtimeHud.ShowDispatch(detail + "  " + CleanNode(payload.targetNodeId));
            }
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
                message.type.Contains("FORCED");
            SetEventFocus(station.transform.position, combatEvent ? 4.5f : 2.8f, combatEvent ? 15f : 30f);
            SpawnEventPulse(station.transform.position, ShortEventName(message.type), EventColor(message.type));
        }
    }

    void ShowKeyEventIllustration(LycheeReplayMessage message, LycheeReplayPayload payload, int round)
    {
        if (runtimeHud == null || message == null) return;
        string type = message.type;
        if (string.IsNullOrEmpty(type) ||
            type == "MATCH_START" ||
            type == "NODE_ENTER" ||
            type.StartsWith("WINDOW_", StringComparison.Ordinal))
            return;

        // 低价值事件（天气预报/守卫衰减/成本类）不再进中央弹窗：信息密度低还连环刷屏遮挡战场，
        // 左侧事件流（LycheeHudExtensions 自己订阅 ReplayEventRaised）仍会展示，不丢信息。
        if (type == "WEATHER_FORECAST" ||
            type == "GUARD_DECAY" ||
            type.StartsWith("COST_", StringComparison.Ordinal))
            return;

        payload = payload ?? new LycheeReplayPayload();
        string title = null;
        string subtitle = "";
        string left = "";
        string right = "";
        string center = "R" + round;
        int imageIndex = 1;
        // 默认 2.6 秒：3.8 秒在 0.22 秒/回合的节奏下弹窗几乎常驻，压短后中央留白更多
        float seconds = 2.6f;
        string target = DisplayNode(!string.IsNullOrEmpty(payload.targetNodeId) ? payload.targetNodeId : payload.nodeId);
        string team = TeamName(payload.playerId);

        if (type == "TASK_SPAWN")
        {
            title = "皇榜任务刷新";
            subtitle = target + "  " + ScoreText(payload.score);
            left = ShortId(payload.taskId, "任务");
            right = "限时争夺";
        }
        else if (type == "TASK_COMPLETE")
        {
            title = "皇榜任务完成";
            subtitle = team + " 获得任务分";
            left = ShortId(payload.taskId, "任务");
            right = ScoreText(payload.score);
            center = "+" + Mathf.Max(0, payload.score);
            imageIndex = 2;
        }
        else if (type == "WEATHER_START")
        {
            title = "天气生效";
            subtitle = WeatherName(payload.weatherType) + " 覆盖 " + RegionName(payload.region);
            left = "鲜度损耗变化";
            right = DurationText(payload.durationRound);
        }
        else if (type == "WEATHER_END")
        {
            title = "天气结束";
            subtitle = WeatherName(payload.weatherType) + " 已退场";
            left = RegionName(payload.region);
            right = "恢复常规";
        }
        else if (type == "RESOURCE_CLAIM")
        {
            title = "资源获取";
            subtitle = team + " 在 " + target + " 取得补给";
            left = ResourceName(payload.resourceType);
            right = "库存更新";
        }
        else if (type == "PROCESS_START")
        {
            title = "站点处理开始";
            subtitle = team + " 在 " + target + " 执行 " + ActionName(payload.action);
            left = "处理中";
            right = DurationText(payload.processRound);
        }
        else if (type == "PROCESS_COMPLETE")
        {
            title = "站点处理完成";
            subtitle = team + " 完成 " + ActionName(payload.action);
            left = target;
            right = "继续前进";
            imageIndex = 2;
        }
        else if (type == "SQUAD_DISPATCH")
        {
            title = "小分队出发";
            subtitle = team + " 派出 " + SquadMemberCount(payload) + " 人前往 " + target;
            left = TacticName(payload.tactic);
            right = "预计 R" + (payload.arriveRound > round ? payload.arriveRound : round + 12);
            imageIndex = 1;
        }
        else if (type == "SQUAD_ARRIVE")
        {
            title = "小分队抵达";
            subtitle = team + " 抵达 " + target;
            left = TacticName(payload.tactic);
            right = "准备生效";
            imageIndex = 1;
        }
        else if (type.StartsWith("SQUAD_", StringComparison.Ordinal))
        {
            title = "小分队战术生效";
            subtitle = ShortEventName(type) + "  " + target;
            left = payload.defenseBefore != 0 || payload.defenseAfter != 0 ? "防御 " + payload.defenseBefore : team;
            right = payload.defenseBefore != 0 || payload.defenseAfter != 0 ? "→ " + payload.defenseAfter : "行动完成";
            imageIndex = 2;
        }
        else if (type == "GUARD_SET")
        {
            title = "设卡布防";
            subtitle = team + " 在 " + target + " 设置阻拦";
            left = "防御";
            right = payload.defense > 0 ? payload.defense.ToString() : "已部署";
            imageIndex = 0;
        }
        else if (type == "GUARD_BREAK")
        {
            title = "攻破设卡";
            subtitle = team + " 攻破 " + target;
            left = "攻坚 " + payload.attackValue;
            right = payload.defenseBefore + " → " + payload.defenseAfter;
            imageIndex = 2;
        }
        else if (type == "BOUNTY_SPAWN")
        {
            title = "破关悬赏出现";
            subtitle = target + " 进入悬赏窗口";
            left = BountyName(payload.bountyType);
            right = ScoreText(payload.score);
            imageIndex = 0;
        }
        else if (type == "BOUNTY_COMPLETE")
        {
            title = "破关悬赏完成";
            subtitle = team + " 获得悬赏奖励";
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
        else if (type == "ACTION_REJECTED")
        {
            title = "行动被拒绝";
            subtitle = ActionName(payload.action);
            left = string.IsNullOrEmpty(payload.errorCode) ? "规则拒绝" : payload.errorCode;
            right = string.IsNullOrEmpty(payload.message) ? "无效试探" : ShortDetail(payload.message, 16);
            imageIndex = 0;
        }
        else if (type == "RUSH_PHASE_START")
        {
            title = "宫宴冲刺开启";
            subtitle = "终局阶段开始，车队向长安冲刺";
            left = string.IsNullOrEmpty(payload.trigger) ? "RUSH" : payload.trigger;
            right = "争分夺秒";
            imageIndex = 2;
            seconds = 3.2f;
        }
        else if (type == "VERIFY_GATE_START")
        {
            title = "朱雀门验核开始";
            subtitle = team + " 在 " + target + " 提交验核";
            left = string.IsNullOrEmpty(payload.rushTactic) ? "验核" : ActionName(payload.rushTactic);
            right = DurationText(payload.processRound);
            imageIndex = 1;
        }
        else if (type == "VERIFY_GATE_COMPLETE")
        {
            title = "朱雀门验核完成";
            subtitle = team + " 获得交付资格";
            left = target;
            right = payload.verifiedEffectiveRound > 0 ? "R" + payload.verifiedEffectiveRound + " 生效" : "已生效";
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
            left = "Replay 完成";
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

        if (!string.IsNullOrEmpty(title))
            runtimeHud.ShowEventIllustration(imageIndex, title, subtitle, left, right, center, seconds);
    }

    void SetEventFocus(Vector3 position, float seconds = 2.8f, float focusDistance = -1f)
    {
        eventFocusPosition = position;
        eventFocusUntil = Time.unscaledTime + seconds;
        // 按事件类型区分聚焦距离：对抗类传 15 拉近看战斗，普通事件传 30 只轻微居中、保住远景；-1 由相机自行决定
        eventFocusDistance = focusDistance;
    }

    void SpawnSquad(LycheeReplayPayload payload, int round)
    {
        // 目标站必须有（否则无处攻坚）；注意 Dictionary.TryGetValue 传 null key 会抛 ArgumentNullException，
        // 务必先判空——新版回放小队派发只给 targetNodeId，fromNodeId 缺省会导致初始化崩溃卡死。
        if (string.IsNullOrEmpty(payload.targetNodeId) ||
            !stations.TryGetValue(payload.targetNodeId, out MapStationAnchor target)) return;
        // 出发点：有 fromNodeId 就用该站，否则回退到派发方车队当前位置（小队从本队出发去攻坚）
        Vector3 startPos;
        if (!string.IsNullOrEmpty(payload.fromNodeId) &&
            stations.TryGetValue(payload.fromNodeId, out MapStationAnchor from))
            startPos = from.transform.position + Vector3.up * 0.15f;
        else
        {
            Transform convoy = GetConvoyRoot(payload.playerId);
            startPos = (convoy != null ? convoy.position : target.transform.position) + Vector3.up * 0.15f;
        }
        GameObject root = new GameObject("Squad_" + payload.tactic + "_" + round);
        root.transform.SetParent(eventRoot, false);
        // 水面状态要在 NotifySquadSpawned 之前挂上，骨骼系统建组时会缓存它的引用
        LycheeSquadWaterState waterState = root.AddComponent<LycheeSquadWaterState>();
        int count = SquadMemberCount(payload);
        Color tint = TeamColor(payload.playerId);
        int columns = Mathf.Clamp(Mathf.CeilToInt(Mathf.Sqrt(count)), 2, 5);
        for (int i = 0; i < count; i++)
        {
            GameObject member = CreateNormalizedModel(squadMemberModel, root.transform, "Member_" + i, 1.0f);
            if (member != null)
            {
                float x = (i % columns - (columns - 1) * 0.5f) * 0.68f;
                float z = (i / columns - 0.5f) * 0.68f;
                member.transform.localPosition = new Vector3(x, 0f, z);
                TintModel(member, tint, 0.18f);
            }
        }
        // 成员全部创建完后通知骨骼动画系统接管（Member_* 是事件期临时对象，LycheeCharacterMotion
        // 的车队扫描覆盖不到，需要显式推送；对方实现为安全空操作兜底，没有骨骼也不会报错）
        LycheeCharacterMotion.NotifySquadSpawned(root);
        CreateLabel(root.transform, "小分队 x" + count + "\n" + TacticName(payload.tactic), new Color(1f, 0.82f, 0.22f));
        squads.Add(new SquadVisual
        {
            root = root,
            start = startPos,
            end = target.transform.position + Vector3.up * 0.15f,
            startRound = round,
            endRound = payload.arriveRound > round ? payload.arriveRound : round + 12,
            memberCount = count,
            targetNodeId = payload.targetNodeId,
            waterState = waterState
        });
    }

    void UpdateSquads(float interpolation)
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
            if (squad.arrivedRealTime < 0f)
            {
                float duration = Mathf.Max(1f, squad.endRound - squad.startRound);
                float t = Mathf.Clamp01((replayRound - squad.startRound) / duration);
                Vector3 position = Vector3.Lerp(squad.start, squad.end, t);
                // 跨河段：人物坐船贴水面滑行，不走陆路的抛物线跃迁，否则像泡在河里行军
                bool onWater = TrySampleWaterSurface(position.x, position.z, out float waterY);
                if (onWater)
                {
                    position.y = waterY + 0.05f + Mathf.Sin(Time.time * 1.8f) * 0.03f; // 贴水 + 随波轻浮沉
                    EnsureSquadBoat(squad);
                    if (squad.boat != null && !squad.boat.activeSelf) squad.boat.SetActive(true);
                }
                else
                {
                    position.y += Mathf.Sin(t * Mathf.PI) * 1.35f;
                    if (squad.boat != null && squad.boat.activeSelf) squad.boat.SetActive(false);
                }
                if (squad.waterState != null) squad.waterState.onWater = onWater;
                squad.root.transform.position = position;
                Vector3 direction = squad.end - squad.start;
                if (direction.sqrMagnitude > 0.001f)
                    squad.root.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
                AnimateSquadMembers(squad, false, onWater);
                if (t >= 1f)
                {
                    // 抵达后停留做攻击突刺演出，配合防御浮字/血条下降一起读，而不是 0.2 秒就消失
                    squad.arrivedRealTime = Time.time;
                    SpawnEventPulse(squad.end, "小队抵达 x" + squad.memberCount, new Color(1f, 0.72f, 0.16f));
                    // 攻坚聚焦拉近（22m，比普通 30m 近）+ 透视目标站建筑，让攻击动作不被楼挡
                    SetEventFocus(squad.end, 3.2f, 22f);
                    if (!string.IsNullOrEmpty(squad.targetNodeId))
                        LycheeContestBuildingXray.RequestXray(squad.targetNodeId, 3.0f);
                }
            }
            else
            {
                AnimateSquadMembers(squad, true, false);
                if (Time.time - squad.arrivedRealTime > 2.4f)
                {
                    Destroy(squad.root);
                    squads.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>程序化小队动作：行军时交替起伏+前倾步伐，攻击时朝目标方向突刺，船上时站立随波。仅改 Y 坐标/旋转/缩放，不破坏阵型。</summary>
    static void AnimateSquadMembers(SquadVisual squad, bool attacking, bool onWater)
    {
        Transform root = squad.root.transform;
        for (int m = 0; m < root.childCount; m++)
        {
            Transform member = root.GetChild(m);
            if (!member.name.StartsWith("Member_", StringComparison.Ordinal)) continue;
            float phase = Time.time * (attacking ? 8f : 10f) + m * 1.7f;
            if (attacking)
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
            }
            else
            {
                Vector3 memberPosition = member.localPosition;
                memberPosition.y = Mathf.Abs(Mathf.Sin(phase)) * 0.12f;
                member.localPosition = memberPosition;
                member.localRotation = Quaternion.Euler(6f, Mathf.Sin(m * 7.3f) * 8f, Mathf.Sin(phase) * 4f);
            }
        }
    }

    /// <summary>给跨河小队垫船（懒创建）。船体微沉入水线，成员在甲板高度由 AnimateSquadMembers 抬升。</summary>
    void EnsureSquadBoat(SquadVisual squad)
    {
        if (squad.boat != null || boatModel == null || squad.root == null) return;
        squad.boat = CreateNormalizedModel(boatModel, squad.root.transform, "SquadBoat", 2.8f);
        if (squad.boat != null)
            squad.boat.transform.localPosition = new Vector3(0f, -0.08f, 0f);
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

    void SpawnEventPulse(Vector3 position, string text, Color color)
    {
        GameObject root = new GameObject("Event_" + text);
        root.transform.SetParent(eventRoot, false);
        root.transform.position = position + Vector3.up * 0.25f;
        ParticleSystem particles = root.AddComponent<ParticleSystem>();
        particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ParticleSystem.MainModule main = particles.main;
        main.duration = 0.65f;
        main.startLifetime = 0.65f;
        main.startSpeed = 2.2f;
        main.startSize = 0.18f;
        main.startColor = color;
        main.maxParticles = 28;
        ParticleSystem.EmissionModule emission = particles.emission;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 22) });
        ParticleSystem.ShapeModule shape = particles.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.45f;
        particles.Play();
        CreateLabel(root.transform, text, color).transform.localPosition = new Vector3(0f, 1.4f, 0f);
        eventEffects.Add(root);
        Destroy(root, 1.8f);
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
        foreach (ConvoyVisual convoy in convoys.Values)
        {
            convoy.initialized = false;
            convoy.positionVelocity = Vector3.zero;
        }
        // 跳转后 HUD 排队中的事件弹窗都指向旧进度，先作废再重放目标帧事件，避免补播错位的回合号
        if (runtimeHud != null) runtimeHud.CancelCombatPopups();
        // 对抗聚焦缓存同理：跳过 CONTEST_END 的跳转会让 CARD_REVEAL 续期到旧对抗节点
        hasContestFocus = false;
        eventFocusUntil = 0f;
        ApplyFrame(frameIndex, true);
        InterpolateConvoys(0f);
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

    int SquadMemberCount(LycheeReplayPayload payload)
    {
        if (payload == null) return Mathf.Max(1, defaultDispatchMemberCount);
        int count = payload.memberCount > 0 ? payload.memberCount : payload.count;
        if (count <= 0) count = payload.squadCount;
        if (count <= 0) count = defaultDispatchMemberCount;
        return Mathf.Clamp(count, 1, 24);
    }

    string TeamName(int playerId)
    {
        if (frames.Count > 0 && frames[frameIndex].players != null)
        {
            LycheeReplayPlayerState player = frames[frameIndex].players.FirstOrDefault(candidate => candidate.playerId == playerId);
            if (player != null) return player.camp == 0 ? "红队" : "蓝队";
        }
        if (startFrame != null && startFrame.players != null)
        {
            LycheeReplayPlayerState player = startFrame.players.FirstOrDefault(candidate => candidate.playerId == playerId);
            if (player != null) return player.camp == 0 ? "红队" : "蓝队";
        }
        return "队伍";
    }

    string DisplayNode(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return "目标点";
        if (stations.TryGetValue(nodeId, out MapStationAnchor station) && !string.IsNullOrEmpty(station.displayName))
            return station.displayName;
        return nodeId;
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
        if (tactic == "CLEAR") return "清障";
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
        if (value == "HOT") return "酷热";
        if (value == "HEAVY_RAIN") return "暴雨";
        if (value == "MOUNTAIN_FOG") return "山雾";
        if (value == "NIGHT_WIND") return "夜风";
        return string.IsNullOrEmpty(value) ? "天气" : value.Replace("_", " ");
    }

    static string RegionName(string value)
    {
        if (value == "ALL") return "全图";
        if (value == "WATER") return "水路";
        if (value == "MOUNTAIN") return "山路";
        if (value == "PLAIN") return "平原";
        return string.IsNullOrEmpty(value) ? "区域" : value.Replace("_", " ");
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
        return string.IsNullOrEmpty(value) ? "资源" : value.Replace("_", " ");
    }

    static string ActionName(string value)
    {
        if (value == "BOARD") return "登船";
        if (value == "WATER_TRANSFER") return "水路换运";
        if (value == "BREAK_GUARD") return "破关";
        if (value == "CLAIM_RESOURCE") return "领取资源";
        if (value == "VERIFY_GATE") return "宫门验核";
        if (value == "BREAK_ORDER") return "破令验核";
        return string.IsNullOrEmpty(value) ? "行动" : value.Replace("_", " ");
    }

    static string BountyName(string value)
    {
        if (value == "KEY_BOUNTY") return "关隘悬赏";
        return string.IsNullOrEmpty(value) ? "悬赏" : value.Replace("_", " ");
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
        model.transform.position -= new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
        foreach (Collider collider in model.GetComponentsInChildren<Collider>(true))
            collider.enabled = false;
        return wrapper;
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
        label.characterSize = 0.055f;
        label.color = color;
        return label;
    }

    void CreateTeamRing(Transform parent, int camp)
    {
        GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ring.name = "TeamRing";
        ring.transform.SetParent(parent, false);
        ring.transform.localPosition = new Vector3(0f, 0.04f, 0f);
        ring.transform.localScale = new Vector3(1.15f, 0.025f, 1.15f);
        Material material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        Color color = camp == 0 ? new Color(0.95f, 0.16f, 0.08f) : new Color(0.06f, 0.42f, 1f);
        material.SetColor("_BaseColor", color);
        material.EnableKeyword("_EMISSION");
        material.SetColor("_EmissionColor", color * 0.65f);
        ring.GetComponent<MeshRenderer>().material = material;
        Destroy(ring.GetComponent<Collider>());
    }

    static string ShortEventName(string eventType)
    {
        return eventType.Replace("_", " ");
    }

    static Color EventColor(string eventType)
    {
        if (eventType.Contains("WEATHER")) return new Color(0.28f, 0.74f, 1f);
        if (eventType.Contains("GUARD") || eventType.Contains("FORCED")) return new Color(1f, 0.28f, 0.14f);
        if (eventType.Contains("TASK") || eventType.Contains("BOUNTY")) return new Color(1f, 0.78f, 0.16f);
        if (eventType.Contains("DELIVER") || eventType.Contains("VERIFY")) return new Color(0.34f, 1f, 0.42f);
        return new Color(0.86f, 0.48f, 1f);
    }

    void OnGUI()
    {
        if (!showLegacyTopHud) return;
        if (frames.Count == 0) return;
        EnsureGuiStyles();
        float width = Mathf.Min(610f, Screen.width - 32f);
        Rect panel = new Rect(16f, 16f, width, 126f);
        GUI.Box(panel, GUIContent.none);
        GUI.Label(new Rect(30f, 24f, width - 28f, 28f), "LYCHEE ROUTE REPLAY", titleStyle);
        GUI.Label(
            new Rect(30f, 52f, width - 28f, 24f),
            "Round " + CurrentRound + " / " + frames[frames.Count - 1].round +
            "   Phase " + frames[frameIndex].phase +
            "   " + latestEvent,
            hudStyle);

        if (GUI.Button(new Rect(30f, 82f, 74f, 30f), playing ? "Pause" : "Play", buttonStyle))
            playing = !playing;
        if (GUI.Button(new Rect(112f, 82f, 58f, 30f), "Reset", buttonStyle))
            SeekFrame(0);

        float speed = 0.10f / secondsPerRound;
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
    }
}
