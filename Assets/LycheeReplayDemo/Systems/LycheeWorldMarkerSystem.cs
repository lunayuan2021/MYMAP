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
    const float PillarFadeSeconds = 0.5f;
    const float RingFlashSeconds = 0.5f;
    const float ResourceHoverHeight = 2.2f;
    const int RingSegments = 32;
    const float RingRadius = 0.8f;

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
        public int lastCount = int.MinValue;
    }

    sealed class ResourceMarker
    {
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
        public Color baseColor;
        public float fadeStart = -1f;
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
        public int lastElapsed = -1;
        public float finishStart = -1f;
    }

    LycheeReplayPlayer player;
    Transform markerRoot;
    int lastSeenRound = int.MinValue;

    readonly Dictionary<string, Material> materialCache = new Dictionary<string, Material>();
    readonly Dictionary<string, GuardMarker> guards = new Dictionary<string, GuardMarker>();
    readonly Dictionary<string, ResourceMarker> resources = new Dictionary<string, ResourceMarker>();
    readonly Dictionary<string, PillarMarker> taskPillars = new Dictionary<string, PillarMarker>();
    readonly Dictionary<string, PillarMarker> bountyPillars = new Dictionary<string, PillarMarker>();
    readonly Dictionary<int, ProgressRing> rings = new Dictionary<int, ProgressRing>();

    readonly HashSet<string> activeKeyBuffer = new HashSet<string>();
    readonly HashSet<string> iconSeenBuffer = new HashSet<string>();
    readonly List<string> removalBuffer = new List<string>();
    readonly List<int> ringRemovalBuffer = new List<int>();
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
        RefreshTaskPillars(current);
        RefreshBountyPillars(current);
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
            case "PROCESS_COMPLETE":
            case "VERIFY_GATE_COMPLETE":
                FinishRingForPlayer(payload, true);
                break;
            case "PROCESS_INTERRUPT":
                FinishRingForPlayer(payload, false);
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
            if (!activeKeyBuffer.Contains(pair.Key)) removalBuffer.Add(pair.Key);
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
            if (!iconSeenBuffer.Contains(pair.Key)) removalBuffer.Add(pair.Key);
        foreach (string key in removalBuffer)
        {
            ResourceIcon icon = marker.icons[key];
            if (icon != null && icon.root != null) Destroy(icon.root);
            marker.icons.Remove(key);
        }

        int index = 0;
        int total = marker.icons.Count;
        foreach (string type in ResourceOrder)
        {
            if (!marker.icons.TryGetValue(type, out ResourceIcon icon) || icon == null || icon.root == null) continue;
            icon.root.transform.localPosition = new Vector3((index - (total - 1) * 0.5f) * 0.62f, 0f, 0f);
            index++;
        }
    }

    ResourceIcon CreateResourceIcon(ResourceMarker marker, string type)
    {
        if (marker == null || marker.row == null) return null;
        GameObject iconRoot = new GameObject("Icon_" + type);
        iconRoot.transform.SetParent(marker.row, false);
        iconRoot.AddComponent<LycheeReplayBillboard>();

        Sprite sprite = LycheeLoadThemeSprite(ResourceSpriteFile(type));
        if (sprite != null)
        {
            GameObject spriteObject = new GameObject("Sprite");
            spriteObject.transform.SetParent(iconRoot.transform, false);
            spriteObject.transform.localScale = Vector3.one * 0.55f;
            SpriteRenderer spriteRenderer = spriteObject.AddComponent<SpriteRenderer>();
            spriteRenderer.sprite = sprite;
        }
        else
        {
            Color color = ResourceColor(type);
            Material material = GetLitMaterial("resource_" + type, color, color * 0.55f);
            CreateBarCube(iconRoot.transform, "Cube", Vector3.zero, Vector3.zero, Vector3.one * 0.30f, material);
            CreateWorldLabel(iconRoot.transform, new Vector3(0f, 0f, -0.18f),
                ResourceFallbackChar(type), Color.white, 26, 0.04f, false);
        }

        TextMesh count = CreateWorldLabel(iconRoot.transform, new Vector3(0.24f, -0.30f, -0.02f),
            string.Empty, GoldColor, 24, 0.04f, false);
        return new ResourceIcon { root = iconRoot, count = count };
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
        return new PillarMarker
        {
            root = root,
            pillar = pillar.transform,
            pillarMaterial = material,
            label = label,
            baseColor = color
        };
    }

    void BeginPillarFade(PillarMarker marker, bool completed)
    {
        if (marker == null || marker.fadeStart >= 0f) return;
        marker.fadeStart = Time.time;
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
    }

    void DestroyPillar(PillarMarker marker)
    {
        if (marker == null) return;
        if (marker.pillarMaterial != null) Destroy(marker.pillarMaterial);
        if (marker.root != null) Destroy(marker.root);
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
            : string.IsNullOrEmpty(bounty.bountyType) ? "破关行动" : bounty.bountyType.Replace("_", " ");
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

        LineRenderer background = CreateRingLine(root.transform, "RingBackground", backgroundMaterial, 0.085f, 0.03f);
        SetArcPositions(background, 1f);
        LineRenderer arc = CreateRingLine(root.transform, "RingArc", arcMaterial, 0.105f, 0f);

        TextMesh label = CreateWorldLabel(root.transform, new Vector3(0f, 0f, -0.02f),
            string.Empty, new Color(0.96f, 0.94f, 0.86f, 1f), 28, 0.045f, false);

        rings[playerId] = new ProgressRing
        {
            root = root,
            arc = arc,
            background = background,
            label = label,
            playerId = playerId,
            startRound = round,
            processRound = Mathf.Max(1, payload.processRound),
            actionName = RingActionName(payload, isVerify)
        };
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
        SetArcPositions(ring.arc, 1f);
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
            SetArcPositions(ring.arc, progress);
            int elapsed = Mathf.Clamp(Mathf.FloorToInt(replayRound - ring.startRound), 0, ring.processRound);
            if (elapsed != ring.lastElapsed && ring.label != null)
            {
                ring.lastElapsed = elapsed;
                ring.label.text = ring.actionName + "\n" + elapsed + "/" + ring.processRound;
            }
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

    static void SetArcPositions(LineRenderer line, float progress)
    {
        if (line == null) return;
        float clamped = Mathf.Clamp01(progress);
        if (clamped <= 0.002f)
        {
            line.positionCount = 0;
            return;
        }
        int count = Mathf.Max(2, Mathf.CeilToInt(RingSegments * clamped) + 1);
        line.positionCount = count;
        for (int i = 0; i < count; i++)
        {
            float angle = (90f - 360f * clamped * i / (count - 1)) * Mathf.Deg2Rad;
            line.SetPosition(i, new Vector3(Mathf.Cos(angle) * RingRadius, Mathf.Sin(angle) * RingRadius, 0f));
        }
    }

    static string RingActionName(LycheeReplayPayload payload, bool isVerify)
    {
        string action = payload == null ? null : payload.action;
        if (string.IsNullOrEmpty(action) && payload != null) action = payload.rushTactic;
        string mapped = ActionChineseName(action);
        if (!string.IsNullOrEmpty(mapped)) return mapped;
        return isVerify ? "宫门验核" : "处理中";
    }

    static string ActionChineseName(string action) => action switch
    {
        "BOARD" => "登船",
        "WATER_TRANSFER" => "水路换运",
        "BREAK_GUARD" => "破关",
        "CLAIM_RESOURCE" => "领取资源",
        "VERIFY_GATE" => "宫门验核",
        "BREAK_ORDER" => "破令验核",
        "SET_GUARD" => "设卡",
        "GUARD_SET" => "设卡",
        _ => null
    };

    // ------------------------------------------------------------------ shared helpers

    void ClearAllMarkers()
    {
        foreach (GuardMarker marker in guards.Values)
            if (marker != null && marker.root != null) Destroy(marker.root);
        guards.Clear();

        foreach (ResourceMarker marker in resources.Values)
            if (marker != null && marker.root != null) Destroy(marker.root);
        resources.Clear();

        foreach (PillarMarker marker in taskPillars.Values) DestroyPillar(marker);
        taskPillars.Clear();
        foreach (PillarMarker marker in bountyPillars.Values) DestroyPillar(marker);
        bountyPillars.Clear();

        foreach (ProgressRing ring in rings.Values)
            if (ring != null && ring.root != null) Destroy(ring.root);
        rings.Clear();
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
        main.duration = 0.6f;
        main.startLifetime = 0.7f;
        main.startSpeed = 2.4f;
        main.startSize = 0.2f;
        main.startColor = color;
        main.maxParticles = 32;
        ParticleSystem.EmissionModule emission = particles.emission;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 24) });
        ParticleSystem.ShapeModule shape = particles.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.5f;
        particles.Play();
        Destroy(root, 1.6f);
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
            string path = Path.Combine(Application.streamingAssetsPath, "UITheme", fileName + ".png");
            if (!File.Exists(path))
            {
                ThemeSpriteMisses.Add(fileName);
                return null;
            }
            byte[] bytes = File.ReadAllBytes(path);
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
