using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class LycheeReplayPlayer : MonoBehaviour
{
    [Header("Replay")]
    public TextAsset replayFile;
    [Range(0.03f, 1f)] public float secondsPerRound = 0.10f;
    public bool playOnStart = true;
    public bool loop;

    [Header("Models")]
    public GameObject carriageModel;
    public GameObject boatModel;
    public GameObject captainModel;
    public GameObject squadMemberModel;
    public GameObject roadObstacleModel;

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
    }

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

    public bool TryGetEventFocus(out Vector3 position)
    {
        position = eventFocusPosition;
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
    System.Collections.IEnumerator LoadReplayThenInitialize()
    {
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
                carriage = CreateNormalizedModel(carriageModel, root.transform, "Carriage", 4.2f),
                boat = CreateNormalizedModel(boatModel, root.transform, "Boat", 5.2f),
                captain = CreateNormalizedModel(captainModel, root.transform, "Captain", 1.8f)
            };
            int teamSize = ResolveTeamSize(player);
            teamRosterSizes[player.playerId] = teamSize;
            if (visual.captain != null)
            {
                visual.captain.transform.localPosition = new Vector3(player.camp == 0 ? -1.6f : 1.6f, 0.2f, -0.4f);
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
            GameObject member = CreateNormalizedModel(squadMemberModel, visual.root.transform, "TeamMember_" + (i + 1), 0.58f);
            if (member == null) continue;
            int col = i % columns;
            int row = i / columns;
            float x = (col - (columns - 1) * 0.5f) * 0.42f;
            float z = 0.45f + row * 0.42f;
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
                SetEventFocus(contestStation.transform.position);
                SpawnEventPulse(contestStation.transform.position, "对抗开始", new Color(1f, 0.78f, 0.22f));
            }
            return;
        }

        if (message.type == "WINDOW_CARD_REVEAL")
        {
            if (runtimeHud != null)
                runtimeHud.ShowCombatReveal(round, payload.roundIndex, payload.redCard, payload.blueCard, payload.winner, payload.redPoint, payload.bluePoint);
            return;
        }

        if (message.type == "WINDOW_CONTEST_END")
        {
            if (runtimeHud != null)
                runtimeHud.ShowCombatEnd(round, payload.winner, payload.redPoint, payload.bluePoint);
            return;
        }

        if (message.type == "SQUAD_DISPATCH")
        {
            if (stations.TryGetValue(payload.targetNodeId, out MapStationAnchor squadTarget))
                SetEventFocus(squadTarget.transform.position);
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
            // 对抗类事件（设卡/攻坚/窗口/强行）延长聚焦时间，让血条与对抗演出留在镜头里。
            bool combatEvent = message.type.Contains("GUARD") ||
                message.type.Contains("WINDOW") ||
                message.type.Contains("FORCED");
            SetEventFocus(station.transform.position, combatEvent ? 4.5f : 2.8f);
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

        payload = payload ?? new LycheeReplayPayload();
        string title = null;
        string subtitle = "";
        string left = "";
        string right = "";
        string center = "R" + round;
        int imageIndex = 1;
        float seconds = 3.8f;
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
        else if (type == "WEATHER_FORECAST")
        {
            title = "天气预告";
            subtitle = WeatherName(payload.weatherType) + " 即将在 " + RegionName(payload.region) + " 生效";
            left = payload.startRound > 0 ? "开始 R" + payload.startRound : "即将到来";
            right = DurationText(payload.durationRound);
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
        else if (type == "GUARD_DECAY")
        {
            title = "设卡风化";
            subtitle = target + " 防御衰减";
            left = payload.defenseBefore.ToString();
            right = "→ " + payload.defenseAfter;
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
        else if (type == "COST_COMMIT")
        {
            title = "代价投入";
            subtitle = team + " 提交 " + ActionName(payload.action);
            left = "好果 -" + payload.goodFruit;
            right = "坏果 +" + payload.badFruit;
            imageIndex = 0;
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
            seconds = 4.8f;
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
            seconds = 5.2f;
        }
        else if (type == "MATCH_END")
        {
            title = "比赛结束";
            subtitle = TeamName(payload.winnerPlayerId) + " 胜出";
            left = "Replay 完成";
            right = "最终结算";
            imageIndex = 2;
            seconds = 5.5f;
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

    void SetEventFocus(Vector3 position, float seconds = 2.8f)
    {
        eventFocusPosition = position;
        eventFocusUntil = Time.unscaledTime + seconds;
    }

    void SpawnSquad(LycheeReplayPayload payload, int round)
    {
        if (!stations.TryGetValue(payload.fromNodeId, out MapStationAnchor from)) return;
        if (!stations.TryGetValue(payload.targetNodeId, out MapStationAnchor target)) return;
        GameObject root = new GameObject("Squad_" + payload.tactic + "_" + round);
        root.transform.SetParent(eventRoot, false);
        int count = SquadMemberCount(payload);
        Color tint = TeamColor(payload.playerId);
        int columns = Mathf.Clamp(Mathf.CeilToInt(Mathf.Sqrt(count)), 2, 5);
        for (int i = 0; i < count; i++)
        {
            GameObject member = CreateNormalizedModel(squadMemberModel, root.transform, "Member_" + i, 0.72f);
            if (member != null)
            {
                float x = (i % columns - (columns - 1) * 0.5f) * 0.48f;
                float z = (i / columns - 0.5f) * 0.48f;
                member.transform.localPosition = new Vector3(x, 0f, z);
                TintModel(member, tint, 0.18f);
            }
        }
        CreateLabel(root.transform, "小分队 x" + count + "\n" + TacticName(payload.tactic), new Color(1f, 0.82f, 0.22f));
        squads.Add(new SquadVisual
        {
            root = root,
            start = from.transform.position + Vector3.up * 0.15f,
            end = target.transform.position + Vector3.up * 0.15f,
            startRound = round,
            endRound = payload.arriveRound > round ? payload.arriveRound : round + 12,
            memberCount = count
        });
    }

    void UpdateSquads(float interpolation)
    {
        float replayRound = CurrentRound + interpolation;
        for (int i = squads.Count - 1; i >= 0; i--)
        {
            SquadVisual squad = squads[i];
            float duration = Mathf.Max(1f, squad.endRound - squad.startRound);
            float t = Mathf.Clamp01((replayRound - squad.startRound) / duration);
            Vector3 position = Vector3.Lerp(squad.start, squad.end, t);
            position.y += Mathf.Sin(t * Mathf.PI) * 1.35f;
            squad.root.transform.position = position;
            Vector3 direction = squad.end - squad.start;
            if (direction.sqrMagnitude > 0.001f)
                squad.root.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
            if (t >= 1f)
            {
                SpawnEventPulse(squad.end, "小队抵达 x" + squad.memberCount, new Color(1f, 0.72f, 0.16f));
                Destroy(squad.root, 0.2f);
                squads.RemoveAt(i);
            }
        }
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
        return type.Contains("TASK") ||
            type.Contains("BOUNTY") ||
            type.Contains("GUARD") ||
            type.Contains("WEATHER") ||
            type.Contains("VERIFY") ||
            type.Contains("DELIVER") ||
            type.Contains("RUSH") ||
            type.Contains("SQUAD");
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
