using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 世界标记系统：地图常驻状态标记（设计文档 §9.1 动态对象）。
/// 1) 设卡拒马标记（程序化 X 形栅栏 + 防御读数）
/// 2) 站点资源悬浮图标（贴图优先，程序化兜底）
/// 3) 任务 / 悬赏金色光柱（剩余回合实时刷新）
/// 4) 车队读条进度环（PROCESS / VERIFY_GATE）
/// 自举挂载到 LycheeReplayPlayer 所在 GameObject，订阅 RoundApplied + ReplayEventRaised。
/// </summary>
public sealed class LycheeWorldMarkerSystem : MonoBehaviour
{
    /// <summary>全局开关：true 时隐藏全部标记并停止处理。</summary>
    public static bool Disabled;

    const string RootName = "Lychee_WorldMarkers";
    const float GuardCollapseSeconds = 0.4f;
    const float PillarFadeSeconds = 1.1f;
    const float RingFlashSeconds = 0.5f;
    const float ResourceHoverHeight = 4.55f;
    const float StationBadgeSlotSpacing = 1.92f;
    const float BadgeSpriteScale = 1.48f;
    const float ObstaclePlaqueWidth = 1.18f;
    const float ObstaclePlaqueHeight = 0.38f;
    const float ObstacleResultFlashSeconds = 0.55f;
    const int BadgeSpriteOrder = 24000;
    const int BadgeTextOrder = 24004;
    const int RingSegments = 32;
    const float RingRadius = 0.8f;
    // 处理/核验进度：原为环形，改成「文字 +横向进度条」（用户要求）。
    const float ProgressBarWidth = 1.95f;      // 进度条满宽（世界单位；billboard 恒定屏幕大小）
    const float ProgressBarThickness = 0.18f;  // 进度条粗细（LineRenderer 宽）
    const float ProgressBarLabelY = 0.36f;     // 「核验中/转运中…」文字在条上方的偏移

    static readonly Color RedTeamColor = new Color(0.95f, 0.18f, 0.11f, 1f);
    static readonly Color BlueTeamColor = new Color(0.20f, 0.48f, 1.0f, 1f);
    static readonly Color GoldColor = new Color(1f, 0.82f, 0.28f, 1f);
    static readonly Color BountyColor = new Color(1f, 0.46f, 0.16f, 1f);
    static readonly Color WoodColor = new Color(0.45f, 0.31f, 0.18f, 1f);
    static readonly Color CompletePulseColor = new Color(0.35f, 1f, 0.45f, 1f);

    static readonly string[] ResourceOrder =
    {
        "ICE_BOX", "FAST_HORSE", "SHORT_HORSE", "PASS_TOKEN", "OFFICIAL_PERMIT", "INTEL", "BOAT_RIGHT"
    };

    static readonly Dictionary<string, Sprite> ThemeSpriteCache = new Dictionary<string, Sprite>();
    static readonly HashSet<string> ThemeSpriteMisses = new HashSet<string>();

    sealed class GuardMarker
    {
        public GameObject root;
        public TextMesh label;
        public string owner;
        public float collapseStart = -1f;
    }

    sealed class ResourceIcon
    {
        public GameObject root;
        public TextMesh count;
        public TextMesh statusMark;
        public int lastCount = int.MinValue;
        public bool resultMarked;
    }

    sealed class ResourceMarker
    {
        public string nodeId;
        public GameObject root;
        public Transform row;
        public float bobSeed;
        public readonly Dictionary<string, ResourceIcon> icons = new Dictionary<string, ResourceIcon>();
    }

    sealed class PillarMarker
    {
        public GameObject root;
        public Transform pillar;
        public Material pillarMaterial;
        public TextMesh label;
        public TextMesh statusMark;
        public Color baseColor;
        public float fadeStart = -1f;
    }

    sealed class ObstacleBadge
    {
        public string nodeId;
        public GameObject root;
        public Transform iconRoot;
        public SpriteRenderer iconRenderer;
        public TextMesh statusMark;
        public bool cleared;
        public bool resultMarked;
        public bool resultSuccess;
        public float resultFlashStart = -1f;
        public float bobSeed;
        // 盖章底色 = 真正完成清障那一队的队色（默认中性金，收到带队号的事件后改写）。
        public Color teamColor = GoldColor;
    }

    sealed class ProgressRing
    {
        public GameObject root;
        public LineRenderer arc;
        public LineRenderer background;
        public TextMesh label;
        public int playerId;
        public float startRound;
        public int processRound;
        public string actionName;
        public string processTypeKey;   // 该条对应的 processType；换了类型=新一段处理，需重建
        public int lastElapsed = -1;
        public float finishStart = -1f;
    }

    sealed class ScoutMarker
    {
        public GameObject root;
        public int expireRound;   // 到该回合（或收到 EXPIRE）移除；前向 seek 越过时也据此清理
    }

    LycheeReplayPlayer player;
    Transform markerRoot;
    int lastSeenRound = int.MinValue;

    readonly Dictionary<string, Material> materialCache = new Dictionary<string, Material>();
    readonly Dictionary<string, GuardMarker> guards = new Dictionary<string, GuardMarker>();
    readonly Dictionary<string, ResourceMarker> resources = new Dictionary<string, ResourceMarker>();
    readonly Dictionary<string, PillarMarker> taskPillars = new Dictionary<string, PillarMarker>();
    readonly Dictionary<string, PillarMarker> bountyPillars = new Dictionary<string, PillarMarker>();
    readonly Dictionary<string, ObstacleBadge> obstacleBadges = new Dictionary<string, ObstacleBadge>();
    readonly Dictionary<int, ProgressRing> rings = new Dictionary<int, ProgressRing>();
    // 探路标记（SCOUT_MARKER_ADD/EXPIRE）：绑定节点、到期移除。key=nodeId（两队在同节点探路视为同一标记）。
    readonly Dictionary<string, ScoutMarker> scoutMarkers = new Dictionary<string, ScoutMarker>();

    readonly HashSet<string> activeKeyBuffer = new HashSet<string>();
    readonly HashSet<string> iconSeenBuffer = new HashSet<string>();
    readonly HashSet<string> knownObstacleNodes = new HashSet<string>();
    readonly HashSet<string> activeObstacleNodes = new HashSet<string>();
    readonly List<string> removalBuffer = new List<string>();
    readonly List<int> ringRemovalBuffer = new List<int>();
    readonly List<string> scoutRemovalBuffer = new List<string>();
    readonly List<(string type, int count)> stockBuffer = new List<(string, int)>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Disabled) return;
        LycheeReplayPlayer host = FindFirstObjectByType<LycheeReplayPlayer>();
        if (host == null) return;
        if (host.GetComponent<LycheeWorldMarkerSystem>() != null) return;
        host.gameObject.AddComponent<LycheeWorldMarkerSystem>();
    }

    void Awake()
    {
        player = GetComponent<LycheeReplayPlayer>();
        if (player == null) player = FindFirstObjectByType<LycheeReplayPlayer>();
        GameObject rootObject = new GameObject(RootName);
        markerRoot = rootObject.transform;
    }

    void OnEnable()
    {
        LycheeReplayPlayer.RoundApplied += HandleRoundApplied;
        LycheeReplayPlayer.ReplayEventRaised += HandleReplayEvent;
    }

    void OnDisable()
    {
        LycheeReplayPlayer.RoundApplied -= HandleRoundApplied;
        LycheeReplayPlayer.ReplayEventRaised -= HandleReplayEvent;
    }

    void Start()
    {
        // 组件在场景加载后自举，可能错过 player.Awake 内应用的第 0 帧 → 补一次全量刷新。
        if (player != null && player.CurrentFrame != null)
            HandleRoundApplied(null, player.CurrentFrame);
    }

    void OnDestroy()
    {
        ClearAllMarkers();
        foreach (Material material in materialCache.Values)
            if (material != null) Destroy(material);
        materialCache.Clear();
        if (markerRoot != null) Destroy(markerRoot.gameObject);
    }

    void Update()
    {
        if (markerRoot == null) return;
        bool active = !Disabled;
        if (markerRoot.gameObject.activeSelf != active) markerRoot.gameObject.SetActive(active);
        if (!active) return;

        float time = Time.time;
        UpdateGuardCollapse(time);
        UpdateResourceBob(time);
        UpdateObstacleBadges(time);
        UpdatePillarGroup(taskPillars, time);
        UpdatePillarGroup(bountyPillars, time);
        UpdateRings(time);
    }

    // ------------------------------------------------------------------ events

    void HandleRoundApplied(LycheeReplayFrame previous, LycheeReplayFrame current)
    {
        if (Disabled || current == null || markerRoot == null) return;
        if (lastSeenRound != int.MinValue && current.round < lastSeenRound)
            ClearAllMarkers(); // SeekToRound 回跳 → 全量重建
        lastSeenRound = current.round;

        RefreshGuardMarkers(current);
        RefreshResourceMarkers(current);
        RefreshObstacleBadges(current);
        RefreshTaskPillars(current);
        RefreshBountyPillars(current);
        PruneScoutMarkers(current.round);  // 前向 seek 越过到期回合时也清理探路标记
    }

    void HandleReplayEvent(LycheeReplayMessage message, int round)
    {
        if (Disabled || message == null || string.IsNullOrEmpty(message.type) || markerRoot == null) return;
        LycheeReplayPayload payload = message.payload;
        switch (message.type)
        {
            case "PROCESS_START":
                CreateProgressRing(payload, round, false);
                break;
            case "VERIFY_GATE_START":
                CreateProgressRing(payload, round, true);
                break;
            case "PROCESS_PROGRESS":
                // 数据不发 PROCESS_START，据进度流让 8 类普通处理 + 核验都出「XX中 + 进度条」。
                EnsureProgressRing(payload, round);
                break;
            case "SCOUT_MARKER_ADD":
                CreateScoutMarker(payload, round);
                break;
            case "SCOUT_MARKER_EXPIRE":
                RemoveScoutMarker(payload);
                break;
            case "RESOURCE_CLAIM":
                MarkResourceBadgeResult(payload, true);
                break;
            case "PROCESS_COMPLETE":
            case "VERIFY_GATE_COMPLETE":
                FinishRingForPlayer(payload, true);
                break;
            case "PROCESS_INTERRUPT":
                FinishRingForPlayer(payload, false);
                break;
            case "SQUAD_CLEAR":
            case "SQUAD_CLEAR_SUCCESS":
            case "OBSTACLE_CLEAR":
                MarkObstacleBadgeResult(payload, true);
                break;
            case "SQUAD_FAILED":
                MarkObstacleBadgeResult(payload, false);
                break;
        }
    }

    // ------------------------------------------------------------------ 1. 设卡标识

    void RefreshGuardMarkers(LycheeReplayFrame frame)
    {
        activeKeyBuffer.Clear();
        if (frame.nodes != null)
        {
            foreach (LycheeReplayNodeState node in frame.nodes)
            {
                if (node == null || string.IsNullOrEmpty(node.nodeId)) continue;
                LycheeReplayGuardState guard = node.guard;
                bool valid = guard != null && guard.defense > 0 && !string.IsNullOrEmpty(guard.ownerTeamId);
                if (!valid) continue;
                activeKeyBuffer.Add(node.nodeId);

                guards.TryGetValue(node.nodeId, out GuardMarker marker);
                bool rebuild = marker == null || marker.root == null ||
                    marker.collapseStart >= 0f || marker.owner != guard.ownerTeamId;
                if (rebuild)
                {
                    if (marker != null)
                    {
                        if (marker.root != null) Destroy(marker.root);
                        guards.Remove(node.nodeId);
                    }
                    CreateGuardMarker(node.nodeId, guard);
                }
                else if (marker.label != null)
                {
                    marker.label.text = GuardLabelText(guard);
                }
            }
        }

        foreach (KeyValuePair<string, GuardMarker> pair in guards)
        {
            GuardMarker marker = pair.Value;
            if (marker == null || marker.collapseStart >= 0f) continue;
            if (!activeKeyBuffer.Contains(pair.Key))
                marker.collapseStart = Time.time; // defense 归零 / guard 消失 → 倒塌
        }
    }

    void CreateGuardMarker(string nodeId, LycheeReplayGuardState guard)
    {
        if (player == null || guard == null) return;
        if (!player.TryGetStationPosition(nodeId, out Vector3 position, out _)) return;

        GameObject root = new GameObject("GuardMarker_" + nodeId);
        root.transform.SetParent(markerRoot, false);
        root.transform.position = position;

        Color teamColor = TeamColorFromTeamId(guard.ownerTeamId);
        Material wood = GetLitMaterial("guard_wood", WoodColor, WoodColor * 0.12f);
        Material glow = GetLitMaterial("guard_glow_" + TeamKey(guard.ownerTeamId), teamColor, teamColor * 1.8f);

        for (int row = 0; row < 2; row++)
        {
            float z = row == 0 ? -0.38f : 0.38f;
            CreateBarCube(root.transform, "CrossA_" + row,
                new Vector3(0f, 0.55f, z), new Vector3(0f, 0f, 52f), new Vector3(0.09f, 1.55f, 0.09f), wood);
            CreateBarCube(root.transform, "CrossB_" + row,
                new Vector3(0f, 0.55f, z), new Vector3(0f, 0f, -52f), new Vector3(0.09f, 1.55f, 0.09f), wood);
            CreateBarCube(root.transform, "Rail_" + row,
                new Vector3(0f, 0.72f, z), Vector3.zero, new Vector3(1.75f, 0.07f, 0.07f), glow);
        }

        TextMesh label = CreateWorldLabel(root.transform, new Vector3(0f, 1.95f, 0f),
            GuardLabelText(guard), teamColor, 30, 0.05f, true);
        guards[nodeId] = new GuardMarker { root = root, label = label, owner = guard.ownerTeamId };
    }

    void UpdateGuardCollapse(float time)
    {
        removalBuffer.Clear();
        foreach (KeyValuePair<string, GuardMarker> pair in guards)
        {
            GuardMarker marker = pair.Value;
            if (marker == null || marker.root == null)
            {
                removalBuffer.Add(pair.Key);
                continue;
            }
            if (marker.collapseStart < 0f) continue;
            float t = (time - marker.collapseStart) / GuardCollapseSeconds;
            if (t >= 1f)
            {
                Destroy(marker.root);
                removalBuffer.Add(pair.Key);
                continue;
            }
            float scale = Mathf.Max(0.001f, 1f - t);
            marker.root.transform.localScale = Vector3.one * scale;
            marker.root.transform.localRotation = Quaternion.Euler(t * 75f, t * 150f, t * 35f);
        }
        foreach (string key in removalBuffer) guards.Remove(key);
    }

    static string GuardLabelText(LycheeReplayGuardState guard)
    {
        if (guard == null) return "⛨ 防 0/0";
        int max = Mathf.Max(guard.maxDefense, guard.defense);
        return "⛨ 防 " + guard.defense + "/" + max;
    }

    // ------------------------------------------------------------------ 2. 资源标记

    void RefreshResourceMarkers(LycheeReplayFrame frame)
    {
        activeKeyBuffer.Clear();
        if (frame.nodes != null)
        {
            foreach (LycheeReplayNodeState node in frame.nodes)
            {
                if (node == null || string.IsNullOrEmpty(node.nodeId) || !node.resourceVisible) continue;
                FillStockBuffer(node.resourceStock);
                if (stockBuffer.Count == 0) continue;

                resources.TryGetValue(node.nodeId, out ResourceMarker marker);
                if (marker == null || marker.root == null)
                {
                    if (marker != null) resources.Remove(node.nodeId);
                    marker = CreateResourceMarker(node.nodeId);
                    if (marker == null) continue;
                    resources[node.nodeId] = marker;
                }
                activeKeyBuffer.Add(node.nodeId);
                SyncResourceIcons(marker);
            }
        }

        removalBuffer.Clear();
        foreach (KeyValuePair<string, ResourceMarker> pair in resources)
            if (!activeKeyBuffer.Contains(pair.Key) && !HasMarkedResourceIcons(pair.Value)) removalBuffer.Add(pair.Key);
        foreach (string key in removalBuffer)
        {
            ResourceMarker marker = resources[key];
            if (marker != null && marker.root != null) Destroy(marker.root);
            resources.Remove(key);
        }
    }

    void FillStockBuffer(LycheeReplayResourceStock stock)
    {
        stockBuffer.Clear();
        if (stock == null) return;
        AddStock("ICE_BOX", stock.ICE_BOX);
        AddStock("FAST_HORSE", stock.FAST_HORSE);
        AddStock("SHORT_HORSE", stock.SHORT_HORSE);
        AddStock("PASS_TOKEN", stock.PASS_TOKEN);
        AddStock("OFFICIAL_PERMIT", stock.OFFICIAL_PERMIT);
        AddStock("INTEL", stock.INTEL);
        AddStock("BOAT_RIGHT", stock.BOAT_RIGHT);
    }

    void AddStock(string type, int count)
    {
        if (count > 0) stockBuffer.Add((type, count));
    }

    ResourceMarker CreateResourceMarker(string nodeId)
    {
        if (player == null) return null;
        if (!player.TryGetStationPosition(nodeId, out Vector3 position, out _)) return null;

        GameObject root = new GameObject("ResourceMarker_" + nodeId);
        root.transform.SetParent(markerRoot, false);
        root.transform.position = position;
        GameObject row = new GameObject("IconRow");
        row.transform.SetParent(root.transform, false);
        row.transform.localPosition = new Vector3(0f, ResourceHoverHeight, 0f);
        return new ResourceMarker
        {
            nodeId = nodeId,
            root = root,
            row = row.transform,
            bobSeed = Mathf.Abs(nodeId.GetHashCode() % 628) * 0.01f
        };
    }

    void SyncResourceIcons(ResourceMarker marker)
    {
        if (marker == null || marker.row == null) return;

        iconSeenBuffer.Clear();
        for (int i = 0; i < stockBuffer.Count; i++)
        {
            (string type, int count) entry = stockBuffer[i];
            iconSeenBuffer.Add(entry.type);
            if (!marker.icons.TryGetValue(entry.type, out ResourceIcon icon) || icon == null || icon.root == null)
            {
                icon = CreateResourceIcon(marker, entry.type);
                if (icon == null) continue;
                marker.icons[entry.type] = icon;
            }
            if (icon.lastCount != entry.count)
            {
                icon.lastCount = entry.count;
                if (icon.count != null)
                    icon.count.text = entry.count > 1 ? "×" + entry.count : string.Empty;
            }
        }

        removalBuffer.Clear();
        foreach (KeyValuePair<string, ResourceIcon> pair in marker.icons)
            if (!iconSeenBuffer.Contains(pair.Key) && (pair.Value == null || !pair.Value.resultMarked)) removalBuffer.Add(pair.Key);
        foreach (string key in removalBuffer)
        {
            ResourceIcon icon = marker.icons[key];
            if (icon != null && icon.root != null) Destroy(icon.root);
            marker.icons.Remove(key);
        }

        LayoutStationBadgeRow(marker);
    }

    void LayoutStationBadgeRow(ResourceMarker marker)
    {
        if (marker == null || marker.row == null) return;

        ObstacleBadge obstacle = null;
        bool includeObstacle = !string.IsNullOrEmpty(marker.nodeId)
            && obstacleBadges.TryGetValue(marker.nodeId, out obstacle)
            && obstacle != null
            && obstacle.root != null;

        int total = marker.icons.Count + (includeObstacle ? 1 : 0);
        if (total <= 0) return;

        int index = 0;
        foreach (string type in ResourceOrder)
        {
            if (!marker.icons.TryGetValue(type, out ResourceIcon icon) || icon == null || icon.root == null) continue;
            icon.root.transform.localPosition = BadgeSlotPosition(index, total);
            index++;
        }

        if (includeObstacle)
        {
            Transform obstacleTransform = obstacle.root.transform;
            if (obstacleTransform.parent != marker.row)
                obstacleTransform.SetParent(marker.row, false);
            obstacleTransform.localPosition = BadgeSlotPosition(index, total);
            obstacleTransform.localRotation = Quaternion.identity;
            obstacleTransform.localScale = Vector3.one;
        }
    }

    static Vector3 BadgeSlotPosition(int index, int total)
    {
        return new Vector3((index - (total - 1) * 0.5f) * StationBadgeSlotSpacing, 0f, 0f);
    }

    ResourceIcon CreateResourceIcon(ResourceMarker marker, string type)
    {
        if (marker == null || marker.row == null) return null;
        GameObject iconRoot = new GameObject("Icon_" + type);
        iconRoot.transform.SetParent(marker.row, false);
        iconRoot.AddComponent<LycheeReplayBillboard>();
        iconRoot.AddComponent<LycheeLabelUpgradeOptOut>();

        Sprite sprite = LycheeLoadThemeSprite(ResourceSpriteFile(type));
        if (sprite != null)
        {
            GameObject spriteObject = new GameObject("Sprite");
            spriteObject.transform.SetParent(iconRoot.transform, false);
            spriteObject.transform.localScale = Vector3.one * BadgeSpriteScale;
            SpriteRenderer spriteRenderer = spriteObject.AddComponent<SpriteRenderer>();
            spriteRenderer.sprite = sprite;
            spriteRenderer.sortingOrder = BadgeSpriteOrder;
        }
        else
        {
            Color color = ResourceColor(type);
            Material material = GetLitMaterial("resource_" + type, color, color * 0.55f);
            CreateBarCube(iconRoot.transform, "Cube", Vector3.zero, Vector3.zero, Vector3.one * 0.9f, material);
            CreateWorldLabel(iconRoot.transform, new Vector3(0f, 0f, -0.36f),
                ResourceFallbackChar(type), Color.white, 30, 0.08f, false);
        }

        TextMesh count = CreateWorldLabel(iconRoot.transform, new Vector3(0.88f, 0.54f, -0.02f),
            string.Empty, GoldColor, 28, 0.07f, false);
        MeshRenderer countRenderer = count == null ? null : count.GetComponent<MeshRenderer>();
        if (countRenderer != null) countRenderer.sortingOrder = BadgeTextOrder;

        TextMesh status = CreateStatusMark(iconRoot.transform, new Vector3(0.42f, 0.42f, -0.04f), true);
        if (status != null) status.gameObject.SetActive(false);
        return new ResourceIcon { root = iconRoot, count = count, statusMark = status };
    }

    bool HasMarkedResourceIcons(ResourceMarker marker)
    {
        if (marker == null || marker.icons == null) return false;
        foreach (ResourceIcon icon in marker.icons.Values)
            if (icon != null && icon.root != null && icon.resultMarked)
                return true;
        return false;
    }

    void MarkResourceBadgeResult(LycheeReplayPayload payload, bool success)
    {
        if (payload == null || string.IsNullOrEmpty(payload.resourceType)) return;
        string nodeId = ResultNodeId(payload);
        if (string.IsNullOrEmpty(nodeId)) return;

        if (!resources.TryGetValue(nodeId, out ResourceMarker marker) || marker == null || marker.root == null)
        {
            if (marker != null) resources.Remove(nodeId);
            marker = CreateResourceMarker(nodeId);
            if (marker == null) return;
            resources[nodeId] = marker;
        }

        if (!marker.icons.TryGetValue(payload.resourceType, out ResourceIcon icon) || icon == null || icon.root == null)
        {
            icon = CreateResourceIcon(marker, payload.resourceType);
            if (icon == null) return;
            marker.icons[payload.resourceType] = icon;
        }

        icon.resultMarked = true;
        ApplyStatusMark(icon.statusMark, success, ResolveTeamColor(payload));
        LayoutStationBadgeRow(marker);
        if (marker.root != null)
        {
            Color pulseColor = success ? CompletePulseColor : new Color(1f, 0.30f, 0.22f, 1f);
            SpawnPulse(marker.root.transform.position + Vector3.up * ResourceHoverHeight, pulseColor);
        }
    }

    void UpdateResourceBob(float time)
    {
        foreach (KeyValuePair<string, ResourceMarker> pair in resources)
        {
            ResourceMarker marker = pair.Value;
            if (marker == null || marker.root == null || marker.row == null) continue;
            float y = ResourceHoverHeight + Mathf.Sin(time * 1.1f + marker.bobSeed) * 0.16f;
            marker.row.localPosition = new Vector3(0f, y, 0f);
        }
    }

    static string ResourceSpriteFile(string type) => type switch
    {
        "ICE_BOX" => "icon_resource_icebox",
        "FAST_HORSE" => "icon_resource_fasthorse",
        "SHORT_HORSE" => "icon_resource_shorthorse",
        "PASS_TOKEN" => "icon_resource_passtoken",
        "OFFICIAL_PERMIT" => "icon_resource_permit",
        "INTEL" => "icon_resource_intel",
        "BOAT_RIGHT" => "icon_resource_boatright",
        _ => null
    };

    static string ResourceFallbackChar(string type) => type switch
    {
        "ICE_BOX" => "冰",
        "FAST_HORSE" => "马",
        "SHORT_HORSE" => "驹",
        "PASS_TOKEN" => "令",
        "OFFICIAL_PERMIT" => "牒",
        "INTEL" => "报",
        "BOAT_RIGHT" => "船",
        _ => "资"
    };

    static Color ResourceColor(string type) => type switch
    {
        "ICE_BOX" => new Color(0.45f, 0.85f, 1f, 1f),
        "FAST_HORSE" => new Color(0.95f, 0.55f, 0.20f, 1f),
        "SHORT_HORSE" => new Color(0.85f, 0.72f, 0.40f, 1f),
        "PASS_TOKEN" => new Color(1f, 0.82f, 0.28f, 1f),
        "OFFICIAL_PERMIT" => new Color(0.92f, 0.90f, 0.80f, 1f),
        "INTEL" => new Color(0.75f, 0.45f, 1f, 1f),
        "BOAT_RIGHT" => new Color(0.30f, 0.80f, 0.75f, 1f),
        _ => GoldColor
    };

    // ------------------------------------------------------------------ 2b. 障碍常驻徽章

    void RefreshObstacleBadges(LycheeReplayFrame frame)
    {
        activeObstacleNodes.Clear();
        if (frame.nodes != null)
        {
            foreach (LycheeReplayNodeState node in frame.nodes)
            {
                if (node == null || string.IsNullOrEmpty(node.nodeId)) continue;
                if (node.hasObstacle)
                    knownObstacleNodes.Add(node.nodeId);
                bool known = knownObstacleNodes.Contains(node.nodeId);
                if (!known) continue;
                if (node.hasObstacle)
                    activeObstacleNodes.Add(node.nodeId);
            }
        }

        foreach (string nodeId in knownObstacleNodes)
        {
            obstacleBadges.TryGetValue(nodeId, out ObstacleBadge badge);
            if (badge == null || badge.root == null)
            {
                if (badge != null) obstacleBadges.Remove(nodeId);
                badge = CreateObstacleBadge(nodeId);
                if (badge != null) obstacleBadges[nodeId] = badge;
            }
            ApplyObstacleBadgeState(badge, activeObstacleNodes.Contains(nodeId));
            if (resources.TryGetValue(nodeId, out ResourceMarker resourceMarker))
                LayoutStationBadgeRow(resourceMarker);
        }
    }

    ObstacleBadge CreateObstacleBadge(string nodeId)
    {
        if (player == null || string.IsNullOrEmpty(nodeId)) return null;

        Transform anchor = null;
        bool useResourceRow = resources.TryGetValue(nodeId, out ResourceMarker resourceMarker)
            && resourceMarker != null
            && resourceMarker.row != null;
        if (!useResourceRow && player.TryGetStationAnchor(nodeId, out MapStationAnchor station) && station != null)
            anchor = station.obstacleAnchor != null ? station.obstacleAnchor :
                (station.eventAnchor != null ? station.eventAnchor :
                (station.modelSlot != null ? station.modelSlot : station.transform));

        GameObject root = new GameObject("ObstacleBadge_" + nodeId);
        if (useResourceRow)
        {
            root.transform.SetParent(resourceMarker.row, false);
            root.transform.localPosition = Vector3.zero;
        }
        else if (anchor != null)
        {
            root.transform.SetParent(anchor, false);
            root.transform.localPosition = new Vector3(0f, ResourceHoverHeight, 0f);
        }
        else
        {
            if (!player.TryGetStationPosition(nodeId, out Vector3 position, out _))
            {
                Destroy(root);
                return null;
            }
            root.transform.SetParent(markerRoot, false);
            root.transform.position = position + Vector3.up * ResourceHoverHeight;
        }
        root.transform.localScale = Vector3.one;
        LycheeReplayBillboard billboard = root.AddComponent<LycheeReplayBillboard>();
        billboard.maxScale = 1.25f;
        billboard.nearFadeStart = 7.5f;
        billboard.nearFadeEnd = 3.8f;

        GameObject iconRoot = new GameObject("ObstacleIcon");
        iconRoot.transform.SetParent(root.transform, false);
        iconRoot.transform.localPosition = Vector3.zero;
        iconRoot.AddComponent<LycheeLabelUpgradeOptOut>();
        SpriteRenderer spriteRenderer = null;

        Sprite sprite = LycheeLoadThemeSprite("EventIcons/icon_event_clear_obstacle");
        if (sprite != null)
        {
            GameObject spriteObject = new GameObject("Sprite");
            spriteObject.transform.SetParent(iconRoot.transform, false);
            spriteObject.transform.localScale = Vector3.one * BadgeSpriteScale;
            spriteRenderer = spriteObject.AddComponent<SpriteRenderer>();
            spriteRenderer.sprite = sprite;
            spriteRenderer.sortingOrder = BadgeSpriteOrder;
            spriteRenderer.color = Color.white;
        }
        else
        {
            Material material = GetUnlitMaterial("clear_badge_fallback", new Color(1f, 0.74f, 0.24f, 1f), false);
            CreateBarCube(iconRoot.transform, "PickaxeA", new Vector3(-0.15f, 0f, 0f), new Vector3(0f, 0f, 45f), new Vector3(0.08f, 1.0f, 0.08f), material);
            CreateBarCube(iconRoot.transform, "PickaxeB", new Vector3(0.15f, 0f, 0f), new Vector3(0f, 0f, -45f), new Vector3(0.08f, 1.0f, 0.08f), material);
            CreatePlaqueLabel(iconRoot.transform, new Vector3(0f, -0.86f, -0.03f),
                "清障", ObstaclePlaqueWidth, ObstaclePlaqueHeight, 40, 0.17f);
        }

        TextMesh status = CreateStatusMark(iconRoot.transform, new Vector3(0.42f, 0.42f, -0.04f), true);
        if (status != null) status.gameObject.SetActive(false);

        return new ObstacleBadge
        {
            nodeId = nodeId,
            root = root,
            iconRoot = iconRoot.transform,
            iconRenderer = spriteRenderer,
            statusMark = status,
            bobSeed = Mathf.Abs(nodeId.GetHashCode() % 628) * 0.01f
        };
    }

    static void ApplyObstacleBadgeState(ObstacleBadge badge, bool active)
    {
        if (badge == null) return;
        badge.cleared = !active;
        if (badge.root != null && !badge.root.activeSelf) badge.root.SetActive(true);
        if (badge.iconRenderer != null)
            badge.iconRenderer.color = Color.white;
        if (badge.statusMark != null && !badge.resultMarked)
        {
            if (active)
            {
                // 障碍还在 → 收起盖章整块（含底色块根节点），避免遗留色块。
                Transform smRoot = badge.statusMark.transform.parent;
                if (smRoot != null) smRoot.gameObject.SetActive(false);
            }
            else
            {
                ApplyStatusMark(badge.statusMark, true, badge.teamColor);
            }
        }
    }

    void MarkObstacleBadgeResult(LycheeReplayPayload payload, bool success)
    {
        string nodeId = ResultNodeId(payload);
        if (string.IsNullOrEmpty(nodeId)) return;

        knownObstacleNodes.Add(nodeId);
        if (!obstacleBadges.TryGetValue(nodeId, out ObstacleBadge badge) || badge == null || badge.root == null)
        {
            badge = CreateObstacleBadge(nodeId);
            if (badge == null) return;
            obstacleBadges[nodeId] = badge;
        }

        badge.resultMarked = true;
        badge.resultSuccess = success;
        badge.resultFlashStart = Time.time;
        badge.teamColor = ResolveTeamColor(payload);
        if (badge.iconRenderer != null) badge.iconRenderer.color = Color.white;
        ApplyStatusMark(badge.statusMark, success, badge.teamColor);
        if (badge.root != null)
            SpawnPulse(badge.root.transform.position, success ? CompletePulseColor : new Color(1f, 0.30f, 0.22f, 1f));
    }

    // 事件归属的队色：有队号就取该队队色（与回放其余队色一致），否则中性金。
    Color ResolveTeamColor(LycheeReplayPayload payload)
    {
        if (player != null && payload != null && payload.playerId > 0)
            return player.TeamColorOf(payload.playerId);
        return GoldColor;
    }

    static string ResultNodeId(LycheeReplayPayload payload)
    {
        if (payload == null) return null;
        if (!string.IsNullOrEmpty(payload.targetNodeId)) return payload.targetNodeId;
        if (!string.IsNullOrEmpty(payload.nodeId)) return payload.nodeId;
        if (!string.IsNullOrEmpty(payload.toNodeId)) return payload.toNodeId;
        return null;
    }

    void UpdateObstacleBadges(float time)
    {
        foreach (KeyValuePair<string, ObstacleBadge> pair in obstacleBadges)
        {
            ObstacleBadge badge = pair.Value;
            if (badge == null || badge.root == null || badge.iconRoot == null) continue;
            float y = badge.cleared ? 0f : Mathf.Sin(time * 1.65f + badge.bobSeed) * 0.12f;
            badge.iconRoot.localPosition = new Vector3(0f, y, 0f);
            float pulse = badge.cleared ? 0.96f : 1f + Mathf.Sin(time * 2.2f + badge.bobSeed) * 0.04f;
            if (badge.resultFlashStart >= 0f)
            {
                float t = (time - badge.resultFlashStart) / ObstacleResultFlashSeconds;
                if (t >= 1f)
                {
                    badge.resultFlashStart = -1f;
                }
                else
                {
                    pulse *= 1f + Mathf.Sin(t * Mathf.PI) * 0.22f;
                }
            }
            badge.iconRoot.localScale = Vector3.one * pulse;
        }
    }

    // ------------------------------------------------------------------ 3. 任务 / 悬赏光柱

    void RefreshTaskPillars(LycheeReplayFrame frame)
    {
        activeKeyBuffer.Clear();
        if (frame.tasks != null)
        {
            foreach (LycheeReplayTaskState task in frame.tasks)
            {
                if (task == null) continue;
                string key = !string.IsNullOrEmpty(task.taskId)
                    ? task.taskId
                    : task.templateId + "@" + task.targetNodeId;
                if (task.status == "ACTIVE")
                {
                    activeKeyBuffer.Add(key);
                    taskPillars.TryGetValue(key, out PillarMarker marker);
                    if (marker == null || marker.root == null || marker.fadeStart >= 0f)
                    {
                        if (marker != null)
                        {
                            DestroyPillar(marker);
                            taskPillars.Remove(key);
                        }
                        marker = CreatePillarMarker(task.targetNodeId, GoldColor, false);
                        if (marker == null) continue;
                        taskPillars[key] = marker;
                    }
                    if (marker.label != null) marker.label.text = TaskLabelText(task, frame.round);
                }
                else if (taskPillars.TryGetValue(key, out PillarMarker existing))
                {
                    BeginPillarFade(existing, task.status == "COMPLETED");
                }
            }
        }

        foreach (KeyValuePair<string, PillarMarker> pair in taskPillars)
            if (!activeKeyBuffer.Contains(pair.Key)) BeginPillarFade(pair.Value, false);
    }

    void RefreshBountyPillars(LycheeReplayFrame frame)
    {
        activeKeyBuffer.Clear();
        if (frame.bounties != null)
        {
            foreach (LycheeReplayBountyState bounty in frame.bounties)
            {
                if (bounty == null) continue;
                string key = !string.IsNullOrEmpty(bounty.bountyId)
                    ? bounty.bountyId
                    : bounty.bountyType + "@" + bounty.targetNodeId;
                if (bounty.status == "ACTIVE")
                {
                    activeKeyBuffer.Add(key);
                    bountyPillars.TryGetValue(key, out PillarMarker marker);
                    if (marker == null || marker.root == null || marker.fadeStart >= 0f)
                    {
                        if (marker != null)
                        {
                            DestroyPillar(marker);
                            bountyPillars.Remove(key);
                        }
                        marker = CreatePillarMarker(bounty.targetNodeId, BountyColor, true);
                        if (marker == null) continue;
                        bountyPillars[key] = marker;
                    }
                    if (marker.label != null) marker.label.text = BountyLabelText(bounty);
                }
                else if (bountyPillars.TryGetValue(key, out PillarMarker existing))
                {
                    BeginPillarFade(existing, bounty.status == "COMPLETED");
                }
            }
        }

        foreach (KeyValuePair<string, PillarMarker> pair in bountyPillars)
            if (!activeKeyBuffer.Contains(pair.Key)) BeginPillarFade(pair.Value, false);
    }

    PillarMarker CreatePillarMarker(string nodeId, Color color, bool isBounty)
    {
        if (player == null) return null;
        if (!player.TryGetStationPosition(nodeId, out Vector3 position, out _)) return null;

        GameObject root = new GameObject((isBounty ? "BountyPillar_" : "TaskPillar_") + nodeId);
        root.transform.SetParent(markerRoot, false);
        root.transform.position = position + (isBounty ? new Vector3(0.9f, 0f, 0f) : Vector3.zero);

        GameObject pillar = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pillar.name = "Pillar";
        pillar.transform.SetParent(root.transform, false);
        pillar.transform.localPosition = new Vector3(0f, 3f, 0f);
        pillar.transform.localScale = new Vector3(1f, 3f, 1f); // 高 6m，半径 0.5
        Collider pillarCollider = pillar.GetComponent<Collider>();
        if (pillarCollider != null) Destroy(pillarCollider);

        Material material = CreatePillarMaterial(color);
        Renderer renderer = pillar.GetComponent<Renderer>();
        if (renderer != null && material != null)
        {
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        TextMesh label = CreateWorldLabel(root.transform, new Vector3(0f, 6.6f, 0f),
            string.Empty, color, 32, 0.06f, true);
        TextMesh status = CreateStatusMark(root.transform, new Vector3(0.72f, 6.2f, -0.04f), true);
        if (status != null) status.gameObject.SetActive(false);
        return new PillarMarker
        {
            root = root,
            pillar = pillar.transform,
            pillarMaterial = material,
            label = label,
            statusMark = status,
            baseColor = color
        };
    }

    void BeginPillarFade(PillarMarker marker, bool completed)
    {
        if (marker == null || marker.fadeStart >= 0f) return;
        marker.fadeStart = Time.time;
        ApplyStatusMark(marker.statusMark, completed);
        if (completed && marker.root != null)
            SpawnPulse(marker.root.transform.position + Vector3.up * 1.2f, CompletePulseColor);
    }

    void UpdatePillarGroup(Dictionary<string, PillarMarker> group, float time)
    {
        removalBuffer.Clear();
        foreach (KeyValuePair<string, PillarMarker> pair in group)
        {
            PillarMarker marker = pair.Value;
            if (marker == null || marker.root == null)
            {
                removalBuffer.Add(pair.Key);
                continue;
            }
            if (marker.pillar != null)
                marker.pillar.Rotate(0f, 24f * Time.deltaTime, 0f, Space.Self); // 缓慢旋转
            if (marker.fadeStart < 0f) continue;

            float t = (time - marker.fadeStart) / PillarFadeSeconds;
            if (t >= 1f)
            {
                DestroyPillar(marker);
                removalBuffer.Add(pair.Key);
                continue;
            }
            ApplyPillarFade(marker, 1f - t);
        }
        foreach (string key in removalBuffer) group.Remove(key);
    }

    static void ApplyPillarFade(PillarMarker marker, float factor)
    {
        if (marker == null) return;
        if (marker.pillarMaterial != null)
        {
            Color baseColor = new Color(marker.baseColor.r, marker.baseColor.g, marker.baseColor.b, 0.35f * factor);
            if (marker.pillarMaterial.HasProperty("_BaseColor")) marker.pillarMaterial.SetColor("_BaseColor", baseColor);
            else if (marker.pillarMaterial.HasProperty("_Color")) marker.pillarMaterial.SetColor("_Color", baseColor);
            if (marker.pillarMaterial.HasProperty("_EmissionColor"))
                marker.pillarMaterial.SetColor("_EmissionColor", marker.baseColor * (1.3f * factor));
        }
        if (marker.label != null)
        {
            Color labelColor = marker.label.color;
            labelColor.a = factor;
            marker.label.color = labelColor;
        }
        if (marker.statusMark != null)
        {
            Color statusColor = marker.statusMark.color;
            statusColor.a = factor;
            marker.statusMark.color = statusColor;
        }
    }

    void DestroyPillar(PillarMarker marker, bool immediate = false)
    {
        if (marker == null) return;
        if (marker.pillarMaterial != null) Destroy(marker.pillarMaterial);
        if (marker.root != null)
        {
            if (immediate) DestroyMarkerRoot(marker.root);
            else Destroy(marker.root);
        }
    }

    static string TaskLabelText(LycheeReplayTaskState task, int round)
    {
        if (task == null) return "皇榜·任务";
        string text = "皇榜·" + (string.IsNullOrEmpty(task.name) ? "限时任务" : task.name);
        if (task.score > 0) text += " +" + task.score + "分";
        if (task.expireRound > 0) text += "\n余 " + Mathf.Max(0, task.expireRound - round) + " 回合";
        return text;
    }

    static string BountyLabelText(LycheeReplayBountyState bounty)
    {
        if (bounty == null) return "悬赏·破关";
        string name = bounty.bountyType == "KEY_BOUNTY"
            ? "关隘破袭"
            : "破关行动";   // 兜底改中文，不显英文
        string text = "悬赏·" + name;
        if (bounty.score > 0) text += " +" + bounty.score + "分";
        return text;
    }

    // ------------------------------------------------------------------ 4. 读条进度环

    void CreateProgressRing(LycheeReplayPayload payload, int round, bool isVerify)
    {
        if (payload == null || markerRoot == null) return;
        int playerId = payload.playerId;
        if (rings.TryGetValue(playerId, out ProgressRing existing))
        {
            if (existing != null && existing.root != null) Destroy(existing.root);
            rings.Remove(playerId);
        }

        Vector3 position;
        Transform convoy = player == null ? null : player.GetConvoyRoot(playerId);
        if (convoy != null)
        {
            position = convoy.position + Vector3.up * 3f;
        }
        else
        {
            string nodeId = !string.IsNullOrEmpty(payload.targetNodeId) ? payload.targetNodeId : payload.nodeId;
            if (player == null || !player.TryGetStationPosition(nodeId, out Vector3 stationPosition, out _))
                return; // 无车队也无站点可定位 → 放弃本次读条
            position = stationPosition + Vector3.up * 3f;
        }

        GameObject root = new GameObject("ProgressRing_P" + playerId);
        root.transform.SetParent(markerRoot, false);
        root.transform.position = position;
        root.AddComponent<LycheeReplayBillboard>();

        Color teamColor = player == null ? GoldColor : player.TeamColorOf(playerId);
        Material backgroundMaterial = GetUnlitMaterial("ring_background", new Color(0.05f, 0.05f, 0.08f, 0.55f), true);
        Material arcMaterial = GetUnlitMaterial("ring_player_" + playerId, teamColor, false);

        // 横向进度条：底条铺满、填充条按进度从左向右长（原环形 arc 改横条 bar）。
        LineRenderer background = CreateRingLine(root.transform, "BarBackground", backgroundMaterial, ProgressBarThickness, 0.03f);
        SetBarPositions(background, 1f);
        LineRenderer arc = CreateRingLine(root.transform, "BarFill", arcMaterial, ProgressBarThickness, 0f);
        SetBarPositions(arc, 0f);

        // 「核验中 / 转运中 / 领资源中 …」文字在进度条上方（取代原环心 N/M 文字）。
        TextMesh label = CreateWorldLabel(root.transform, new Vector3(0f, ProgressBarLabelY, -0.02f),
            RingActionName(payload, isVerify), new Color(0.98f, 0.95f, 0.86f, 1f), 30, 0.05f, false);

        // PROCESS_PROGRESS/合成 START 的 payload 只带 remainingRound（首拍≈总拍数），不带 processRound。
        int totalRounds = payload.remainingRound > 0 ? payload.remainingRound
            : (payload.processRound > 0 ? payload.processRound : 1);
        rings[playerId] = new ProgressRing
        {
            root = root,
            arc = arc,
            background = background,
            label = label,
            playerId = playerId,
            startRound = round,
            processRound = Mathf.Max(1, totalRounds),
            actionName = RingActionName(payload, isVerify),
            processTypeKey = payload.processType
        };
    }

    // 数据只发 PROCESS_PROGRESS、不发 PROCESS_START：直接据进度流建/换进度条（不合成事件，避免触发音效/播报/动画）。
    // 已有同一段(同 processType)的活动条就跳过；换了 processType = 新一段处理，重建。VERIFY_GATE 若已被
    // VERIFY_GATE_START 建好条也会因同 processType 被跳过，不重复建。
    void EnsureProgressRing(LycheeReplayPayload payload, int round)
    {
        if (payload == null || payload.playerId <= 0 || string.IsNullOrEmpty(payload.processType)) return;
        if (rings.TryGetValue(payload.playerId, out ProgressRing existing) && existing != null
            && existing.finishStart < 0f && existing.processTypeKey == payload.processType)
            return;
        CreateProgressRing(payload, round, payload.processType == "VERIFY_GATE");
    }

    // ------------------------------------------------------------------ 探路标记（SCOUT_MARKER）

    void CreateScoutMarker(LycheeReplayPayload payload, int round)
    {
        if (payload == null || markerRoot == null) return;
        string nodeId = !string.IsNullOrEmpty(payload.targetNodeId) ? payload.targetNodeId : payload.nodeId;
        if (string.IsNullOrEmpty(nodeId) || player == null) return;
        if (!player.TryGetStationPosition(nodeId, out Vector3 position, out _)) return;
        // 已在该节点有探路标记（另一队同回合也探同点）→ 只续期到期回合，不重复建。
        if (scoutMarkers.TryGetValue(nodeId, out ScoutMarker existing) && existing != null && existing.root != null)
        {
            if (payload.expireRound > existing.expireRound) existing.expireRound = payload.expireRound;
            return;
        }

        GameObject root = new GameObject("ScoutMarker_" + nodeId);
        root.transform.SetParent(markerRoot, false);
        root.transform.position = position + Vector3.up * 5.4f;
        root.AddComponent<LycheeReplayBillboard>();

        // 仅文字「探路」（青蓝）飘在节点上方——billboard 让 root 的 +Z 朝相机反向，Quad 底盘会被背面剔除，
        // 故不用底盘；TextMesh 标签与站名一样可读（同 CreateWorldLabel 路径）。带阴影描边保证草地上看得清。
        Color scoutColor = new Color(0.52f, 0.90f, 0.98f, 1f);
        CreateWorldLabel(root.transform, new Vector3(0.04f, -0.04f, 0.01f), "探路", new Color(0.02f, 0.06f, 0.08f, 0.9f), 30, 0.052f, false);
        CreateWorldLabel(root.transform, new Vector3(0f, 0f, -0.02f), "探路", scoutColor, 30, 0.052f, false);

        scoutMarkers[nodeId] = new ScoutMarker { root = root, expireRound = payload.expireRound };
    }

    void RemoveScoutMarker(LycheeReplayPayload payload)
    {
        if (payload == null) return;
        string nodeId = !string.IsNullOrEmpty(payload.targetNodeId) ? payload.targetNodeId : payload.nodeId;
        if (string.IsNullOrEmpty(nodeId)) return;
        if (scoutMarkers.TryGetValue(nodeId, out ScoutMarker marker))
        {
            if (marker != null && marker.root != null) Destroy(marker.root);
            scoutMarkers.Remove(nodeId);
        }
    }

    void PruneScoutMarkers(int round)
    {
        if (scoutMarkers.Count == 0) return;
        scoutRemovalBuffer.Clear();
        foreach (KeyValuePair<string, ScoutMarker> pair in scoutMarkers)
        {
            ScoutMarker m = pair.Value;
            if (m == null || m.root == null || (m.expireRound > 0 && round >= m.expireRound))
                scoutRemovalBuffer.Add(pair.Key);
        }
        foreach (string key in scoutRemovalBuffer)
        {
            if (scoutMarkers.TryGetValue(key, out ScoutMarker m) && m != null && m.root != null) Destroy(m.root);
            scoutMarkers.Remove(key);
        }
    }

    void FinishRingForPlayer(LycheeReplayPayload payload, bool success)
    {
        if (payload == null) return;
        if (rings.TryGetValue(payload.playerId, out ProgressRing ring))
            BeginRingFinish(ring, success);
    }

    void BeginRingFinish(ProgressRing ring, bool success)
    {
        if (ring == null || ring.finishStart >= 0f) return;
        ring.finishStart = Time.time;
        SetBarPositions(ring.arc, 1f);
        Material flash = GetUnlitMaterial(success ? "ring_flash_ok" : "ring_flash_fail",
            success ? new Color(0.45f, 1f, 0.50f, 1f) : new Color(1f, 0.40f, 0.30f, 1f), false);
        if (ring.arc != null && flash != null) ring.arc.material = flash;
        if (ring.label != null)
        {
            ring.label.text = ring.actionName + "\n" + (success ? "完成" : "中断");
            ring.label.color = success ? new Color(0.60f, 1f, 0.60f, 1f) : new Color(1f, 0.55f, 0.45f, 1f);
        }
    }

    void UpdateRings(float time)
    {
        ringRemovalBuffer.Clear();
        float replayRound = player == null ? 0f : player.CurrentRound + player.FrameInterpolation;
        foreach (KeyValuePair<int, ProgressRing> pair in rings)
        {
            ProgressRing ring = pair.Value;
            if (ring == null || ring.root == null)
            {
                ringRemovalBuffer.Add(pair.Key);
                continue;
            }

            Transform convoy = player == null ? null : player.GetConvoyRoot(ring.playerId);
            if (convoy != null)
                ring.root.transform.position = convoy.position + Vector3.up * 3f; // 跟随车队

            if (ring.finishStart >= 0f)
            {
                float elapsedFlash = time - ring.finishStart;
                bool visible = Mathf.Sin(elapsedFlash * 36f) > -0.35f;
                if (ring.arc != null) ring.arc.enabled = visible;
                if (elapsedFlash >= RingFlashSeconds)
                {
                    Destroy(ring.root);
                    ringRemovalBuffer.Add(pair.Key);
                }
                continue;
            }

            float progress = Mathf.Clamp01((replayRound - ring.startRound) / ring.processRound);
            SetBarPositions(ring.arc, progress);
            // 文字恒为「核验中/转运中…」（进度靠条表达，不再逐拍改写 N/M）。
            if (replayRound > ring.startRound + ring.processRound + 2f)
                BeginRingFinish(ring, true); // 超时兜底：当作完成收尾
        }
        foreach (int key in ringRemovalBuffer) rings.Remove(key);
    }

    LineRenderer CreateRingLine(Transform parent, string childName, Material material, float width, float zOffset)
    {
        GameObject child = new GameObject(childName);
        child.transform.SetParent(parent, false);
        child.transform.localPosition = new Vector3(0f, 0f, zOffset);
        LineRenderer line = child.AddComponent<LineRenderer>();
        line.useWorldSpace = false;
        line.loop = false;
        line.widthMultiplier = width;
        line.numCapVertices = 2;
        line.numCornerVertices = 2;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.startColor = Color.white;
        line.endColor = Color.white;
        if (material != null) line.material = material;
        line.positionCount = 0;
        return line;
    }

    // 横向进度条：从左端 (-W/2) 向右画到 (-W/2 + W*progress)。progress<1 为填充条，=1 用作底条铺满。
    static void SetBarPositions(LineRenderer line, float progress)
    {
        if (line == null) return;
        float clamped = Mathf.Clamp01(progress);
        if (clamped <= 0.002f)
        {
            line.positionCount = 0;
            return;
        }
        float half = ProgressBarWidth * 0.5f;
        line.positionCount = 2;
        line.SetPosition(0, new Vector3(-half, 0f, 0f));
        line.SetPosition(1, new Vector3(-half + ProgressBarWidth * clamped, 0f, 0f));
    }

    static string RingActionName(LycheeReplayPayload payload, bool isVerify)
    {
        // 优先按 processType 出「XX中」进行时文案（PROCESS_PROGRESS/合成 START 的 payload 带 processType）；
        // 兜底再看 action/rushTactic；verify 恒「核验中」。
        string byType = ProcessTypeChineseName(payload == null ? null : payload.processType);
        if (!string.IsNullOrEmpty(byType)) return byType;
        string action = payload == null ? null : payload.action;
        if (string.IsNullOrEmpty(action) && payload != null) action = payload.rushTactic;
        string mapped = ActionChineseName(action);
        if (!string.IsNullOrEmpty(mapped)) return mapped;
        return isVerify ? "核验中" : "处理中";
    }

    // processType → 进行时中文（本回放全 9 种：转运/领任务/领资源/登船/休整/清障/过关转运/皇榜转运/宫门核验）。
    static string ProcessTypeChineseName(string processType) => processType switch
    {
        "VERIFY_GATE" => "核验中",
        "TRANSFER" => "转运中",
        "CLAIM_TASK" => "领任务中",
        "CLAIM_RESOURCE" => "领资源中",
        "BOARD" => "登船中",
        "RESTING" => "休整中",
        "CLEAR_OBSTACLE" => "清障中",
        "PASS_TRANSFER" => "过关转运中",
        "PALACE_TRANSFER" => "皇榜转运中",
        "WATER_TRANSFER" => "水路换运中",
        _ => null
    };

    static string ActionChineseName(string action) => action switch
    {
        "BOARD" => "登船中",
        "WATER_TRANSFER" => "水路换运中",
        "BREAK_GUARD" => "破关中",
        "CLAIM_RESOURCE" => "领资源中",
        "VERIFY_GATE" => "核验中",
        "BREAK_ORDER" => "破令验核中",
        "SET_GUARD" => "设卡中",
        "GUARD_SET" => "设卡中",
        _ => null
    };

    // ------------------------------------------------------------------ shared helpers

    void ClearAllMarkers()
    {
        foreach (GuardMarker marker in guards.Values)
            if (marker != null && marker.root != null) DestroyMarkerRoot(marker.root);
        guards.Clear();

        foreach (ResourceMarker marker in resources.Values)
            if (marker != null && marker.root != null) DestroyMarkerRoot(marker.root);
        resources.Clear();

        foreach (PillarMarker marker in taskPillars.Values) DestroyPillar(marker, true);
        taskPillars.Clear();
        foreach (PillarMarker marker in bountyPillars.Values) DestroyPillar(marker, true);
        bountyPillars.Clear();

        foreach (ObstacleBadge badge in obstacleBadges.Values)
            if (badge != null && badge.root != null) DestroyMarkerRoot(badge.root);
        obstacleBadges.Clear();
        knownObstacleNodes.Clear();
        activeObstacleNodes.Clear();

        foreach (ProgressRing ring in rings.Values)
            if (ring != null && ring.root != null) DestroyMarkerRoot(ring.root);
        rings.Clear();

        foreach (ScoutMarker scout in scoutMarkers.Values)
            if (scout != null && scout.root != null) DestroyMarkerRoot(scout.root);
        scoutMarkers.Clear();
    }

    static void DestroyMarkerRoot(GameObject root)
    {
        if (root == null) return;
        DestroyImmediate(root);
    }

    void SpawnPulse(Vector3 position, Color color)
    {
        if (markerRoot == null) return;
        GameObject root = new GameObject("MarkerPulse");
        root.transform.SetParent(markerRoot, false);
        root.transform.position = position;
        ParticleSystem particles = root.AddComponent<ParticleSystem>();
        particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ParticleSystem.MainModule main = particles.main;
        main.duration = 0.38f;
        main.startLifetime = 0.42f;
        main.startSpeed = 1.45f;
        main.startSize = 0.13f;
        main.startColor = color;
        main.maxParticles = 12;
        ParticleSystem.EmissionModule emission = particles.emission;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 8) });
        ParticleSystem.ShapeModule shape = particles.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.5f;
        // 关键：粒子渲染器必须显式给材质。默认粒子材质在 URP 下 shader 解析为 NULL → 整团粒子渲成洋红方块
        // （用户截图里资源徽章旁那几个品红色块就是它）。统一用 URP 透明材质，命中即按 startColor 着色。
        ParticleSystemRenderer pulseRenderer = particles.GetComponent<ParticleSystemRenderer>();
        if (pulseRenderer != null)
        {
            Material pulseMat = GetPulseMaterial();
            if (pulseMat != null) pulseRenderer.sharedMaterial = pulseMat;
            pulseRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            pulseRenderer.receiveShadows = false;
        }
        particles.Play();
        Destroy(root, 0.9f);
    }

    // 脉冲粒子材质（透明、白底，按粒子 startColor 着色）。URP/Particles/Unlit 优先，逐级回退，
    // 缓存复用——避免每次脉冲都新建材质，也避免默认粒子材质在 URP 下变洋红。
    Material GetPulseMaterial()
    {
        if (materialCache.TryGetValue("__markerPulse", out Material cached) && cached != null) return cached;
        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        if (shader == null) return null;
        Material material = new Material(shader) { name = "LycheeMarkerPulseMat" };
        LycheeMakeTransparent(material);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
        if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
        materialCache["__markerPulse"] = material;
        return material;
    }

    static void CreateBarCube(
        Transform parent,
        string childName,
        Vector3 localPosition,
        Vector3 localEuler,
        Vector3 localScale,
        Material material)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = childName;
        cube.transform.SetParent(parent, false);
        cube.transform.localPosition = localPosition;
        cube.transform.localRotation = Quaternion.Euler(localEuler);
        cube.transform.localScale = localScale;
        Collider barCollider = cube.GetComponent<Collider>();
        if (barCollider != null) Destroy(barCollider);
        if (material != null)
        {
            Renderer renderer = cube.GetComponent<Renderer>();
            if (renderer != null) renderer.sharedMaterial = material;
        }
    }

    static TextMesh CreateWorldLabel(
        Transform parent,
        Vector3 localPosition,
        string text,
        Color color,
        int fontSize,
        float characterSize,
        bool billboard)
    {
        GameObject labelObject = new GameObject("Label");
        labelObject.transform.SetParent(parent, false);
        labelObject.transform.localPosition = localPosition;
        if (billboard) labelObject.AddComponent<LycheeReplayBillboard>();
        TextMesh label = labelObject.AddComponent<TextMesh>();
        label.text = text ?? string.Empty;
        label.anchor = TextAnchor.MiddleCenter;
        label.alignment = TextAlignment.Center;
        label.fontSize = fontSize;
        label.characterSize = characterSize;
        label.color = color;
        labelObject.AddComponent<LycheeChineseLabel>();
        return label;
    }

    TextMesh CreatePlaqueLabel(
        Transform parent,
        Vector3 localPosition,
        string text,
        float width,
        float height,
        int fontSize,
        float characterSizeOverride = -1f)
    {
        GameObject root = new GameObject("PlaqueLabel");
        root.transform.SetParent(parent, false);
        root.transform.localPosition = localPosition;
        root.AddComponent<LycheeLabelUpgradeOptOut>();

        GameObject plateObject = new GameObject("Plate");
        plateObject.transform.SetParent(root.transform, false);
        plateObject.transform.localScale = new Vector3(width, height, 1f);
        SpriteRenderer plate = plateObject.AddComponent<SpriteRenderer>();
        plate.sprite = EventPlaqueSprite();
        plate.color = new Color(0.30f, 0.20f, 0.07f, 0.94f);
        plate.sortingOrder = 24001;

        int textLength = string.IsNullOrEmpty(text) ? 1 : text.Length;
        float characterSize = characterSizeOverride > 0f
            ? characterSizeOverride
            : (textLength >= 4 ? 0.07f : (textLength == 3 ? 0.082f : 0.095f));
        TextMesh label = CreateWorldLabel(root.transform, new Vector3(0f, 0f, -0.03f),
            text, new Color(1f, 0.91f, 0.66f, 1f), fontSize, characterSize, false);
        MeshRenderer labelRenderer = label.GetComponent<MeshRenderer>();
        if (labelRenderer != null) labelRenderer.sortingOrder = 24003;

        return label;
    }

    static TextMesh CreateStatusMark(Transform parent, Vector3 localPosition, bool success)
    {
        GameObject root = new GameObject("StatusMark");
        root.transform.SetParent(parent, false);
        root.transform.localPosition = localPosition;
        root.transform.localScale = Vector3.one * 0.62f;

        SpriteRenderer back = root.AddComponent<SpriteRenderer>();
        back.sprite = EventPlaqueSprite();
        back.color = success
            ? new Color(0.04f, 0.30f, 0.11f, 0.96f)
            : new Color(0.42f, 0.06f, 0.04f, 0.96f);
        back.sortingOrder = 24010;

        TextMesh label = CreateWorldLabel(root.transform, new Vector3(0f, 0.01f, -0.03f),
            success ? "✓" : "×", Color.white, 58, 0.07f, false);
        MeshRenderer labelRenderer = label.GetComponent<MeshRenderer>();
        if (labelRenderer != null) labelRenderer.sortingOrder = 24011;
        // 整块盖章（含底色块 SpriteRenderer）默认隐藏：过去只 SetActive(false) 返回的 label 子物体、
        // 根节点的底色块仍亮着，于是每个徽章右下角常驻一块"统一的绿色"色块（用户要去掉的就是它）。
        // 关掉根节点 → 盖章在「完成并 ApplyStatusMark」之前完全不显示。
        root.SetActive(false);
        return label;
    }

    // 显示盖章：先把根节点（底色块所在）激活，再激活 ✓/× 文字——只 SetActive 文字救不活被隐藏的根。
    static void ShowStatusMarkRoot(TextMesh mark)
    {
        if (mark == null) return;
        Transform root = mark.transform.parent;
        if (root != null && !root.gameObject.activeSelf) root.gameObject.SetActive(true);
        mark.gameObject.SetActive(true);
    }

    static void ApplyStatusMark(TextMesh mark, bool success)
    {
        if (mark == null) return;
        ShowStatusMarkRoot(mark);
        mark.text = success ? "✓" : "×";
        mark.color = Color.white;
        SpriteRenderer back = mark.transform.parent == null ? null : mark.transform.parent.GetComponent<SpriteRenderer>();
        if (back != null)
        {
            back.color = success
                ? new Color(0.04f, 0.30f, 0.11f, 0.96f)
                : new Color(0.42f, 0.06f, 0.04f, 0.96f);
        }
    }

    // 队色版盖章：底色块改成「哪队完成的就用哪队的颜色」，不再是统一的绿/红。
    // ✓/× 符号本身继续区分成败，队色只表明是哪一队盖的章（用户诉求）。
    static void ApplyStatusMark(TextMesh mark, bool success, Color teamColor)
    {
        if (mark == null) return;
        ShowStatusMarkRoot(mark);
        mark.text = success ? "✓" : "×";
        mark.color = Color.white;
        SpriteRenderer back = mark.transform.parent == null ? null : mark.transform.parent.GetComponent<SpriteRenderer>();
        if (back != null) back.color = StampPlaqueColor(teamColor, success);
    }

    // 盖章底色块：以队色为基调，成功偏亮一档、失败压暗一档，保证白色 ✓/× 在两种情况下都清楚。
    static Color StampPlaqueColor(Color teamColor, bool success)
    {
        float mul = success ? 0.66f : 0.42f;
        return new Color(teamColor.r * mul, teamColor.g * mul, teamColor.b * mul, 0.96f);
    }

    static Sprite eventPlaqueSprite;

    static Sprite EventPlaqueSprite()
    {
        if (eventPlaqueSprite != null) return eventPlaqueSprite;
        const int width = 128;
        const int height = 48;
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        Color[] pixels = new Color[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float nx = Mathf.Abs((x + 0.5f) / width - 0.5f) * 2f;
                float ny = Mathf.Abs((y + 0.5f) / height - 0.5f) * 2f;
                float edge = Mathf.Max(nx, ny);
                float alpha = edge > 0.98f ? 0f : 1f;
                pixels[y * width + x] = new Color(1f, 1f, 1f, alpha);
            }
        }
        texture.SetPixels(pixels);
        texture.Apply(false, true);
        texture.name = "LycheeEventPlaqueSprite";
        eventPlaqueSprite = Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), width);
        eventPlaqueSprite.name = "LycheeEventPlaqueSprite";
        return eventPlaqueSprite;
    }

    Material GetLitMaterial(string key, Color baseColor, Color emissionColor)
    {
        if (materialCache.TryGetValue(key, out Material cached) && cached != null) return cached;
        Material material = LycheeCreateMaterial(false);
        if (material == null) return null;
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", baseColor);
        else if (material.HasProperty("_Color")) material.SetColor("_Color", baseColor);
        if (emissionColor.maxColorComponent > 0.001f && material.HasProperty("_EmissionColor"))
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", emissionColor);
        }
        materialCache[key] = material;
        return material;
    }

    Material GetUnlitMaterial(string key, Color color, bool transparent)
    {
        if (materialCache.TryGetValue(key, out Material cached) && cached != null) return cached;
        Material material = LycheeCreateMaterial(true);
        if (material == null) return null;
        if (transparent) LycheeMakeTransparent(material);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        else if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        materialCache[key] = material;
        return material;
    }

    Material CreatePillarMaterial(Color color)
    {
        // 光柱需要逐实例做 alpha 淡出，因此不能共享缓存；随标记一起显式 Destroy，避免泄漏。
        Material material = LycheeCreateMaterial(false);
        if (material == null) return null;
        LycheeMakeTransparent(material);
        Color baseColor = new Color(color.r, color.g, color.b, 0.35f);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", baseColor);
        else if (material.HasProperty("_Color")) material.SetColor("_Color", baseColor);
        if (material.HasProperty("_EmissionColor"))
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", color * 1.3f);
        }
        return material;
    }

    static Material LycheeCreateMaterial(bool unlit)
    {
        Shader shader = Shader.Find(unlit ? "Universal Render Pipeline/Unlit" : "Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        return shader == null ? null : new Material(shader);
    }

    static void LycheeMakeTransparent(Material material)
    {
        if (material == null) return;
        if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
        material.SetOverrideTag("RenderType", "Transparent");
        if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.DisableKeyword("_ALPHATEST_ON");
        material.renderQueue = (int)RenderQueue.Transparent;
    }

    static Color TeamColorFromTeamId(string teamId)
    {
        if (string.IsNullOrEmpty(teamId)) return GoldColor;
        string upper = teamId.ToUpperInvariant();
        if (upper.Contains("RED")) return RedTeamColor;
        if (upper.Contains("BLUE")) return BlueTeamColor;
        return GoldColor;
    }

    static string TeamKey(string teamId)
    {
        if (string.IsNullOrEmpty(teamId)) return "none";
        string upper = teamId.ToUpperInvariant();
        if (upper.Contains("RED")) return "red";
        if (upper.Contains("BLUE")) return "blue";
        return "other";
    }

    /// <summary>从 StreamingAssets/UITheme/&lt;fileName&gt;.png 加载主题贴图为 Sprite；失败返回 null 走程序化兜底。</summary>
    static Sprite LycheeLoadThemeSprite(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        if (ThemeSpriteCache.TryGetValue(fileName, out Sprite cached) && cached != null) return cached;
        if (ThemeSpriteMisses.Contains(fileName)) return null;
        try
        {
            byte[] bytes = LycheeThemeCache.LoadBytes(fileName + ".png");
            if (bytes == null || bytes.Length == 0)
            {
                ThemeSpriteMisses.Add(fileName);
                return null;
            }
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(bytes))
            {
                ThemeSpriteMisses.Add(fileName);
                return null;
            }
            texture.name = fileName;
            texture.wrapMode = TextureWrapMode.Clamp;
            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                Mathf.Max(texture.width, texture.height));
            ThemeSpriteCache[fileName] = sprite;
            return sprite;
        }
        catch (Exception)
        {
            ThemeSpriteMisses.Add(fileName);
            return null;
        }
    }
}
