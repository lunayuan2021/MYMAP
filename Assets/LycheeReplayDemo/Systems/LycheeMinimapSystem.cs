using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

/// <summary>
/// 右上角 260x260 小地图系统（运行时自举，挂到 LycheeReplayPlayer 所在物体）。
/// 俯视正交相机每 0.6 秒低频渲染一次到 RenderTexture；
/// 动态图标层每帧把车队 / 有效设卡 / ACTIVE 任务 / 障碍节点映射到小地图 UV。
/// RUSH 阶段边框红金色呼吸。
/// </summary>
public sealed class LycheeMinimapSystem : MonoBehaviour
{
    /// <summary>全局开关：true 时不再自举；运行中置 true 会隐藏小地图面板。</summary>
    public static bool Disabled;

    // 131 = 压过对抗暗角(129)与中央弹窗画布(130)：对抗聚焦时小地图保持清晰可读（LOL 式习惯）
    const int CanvasSortingOrder = 131;
    const float PanelSize = 260f;
    // 内容比面板小一圈：minimap_frame.png 的罗盘/铜镜装饰环画在面板外缘，
    // 内容若与面板同大会被纹样压住（"边框嵌进地图"）。
    const float MapSize = 208f;
    const int MinimapTextureSize = 512;
    const float RenderInterval = 0.6f;
    const float CameraHeight = 90f;
    const float CameraOrthoSize = 32f;
    const float PulsePeriod = 0.8f;

    static readonly Color PanelBackground = new Color(0.02f, 0.025f, 0.02f, 0.85f);
    static readonly Color GoldBorder = new Color(1f, 0.77f, 0.27f, 0.92f);
    static readonly Color RushBorderGold = new Color(1f, 0.72f, 0.20f, 0.96f);
    static readonly Color RushBorderRed = new Color(0.95f, 0.18f, 0.11f, 1f);
    static readonly Color RedTeamColor = new Color(0.95f, 0.18f, 0.11f, 1f);
    static readonly Color BlueTeamColor = new Color(0.20f, 0.48f, 1f, 1f);
    static readonly Color TaskGold = new Color(1f, 0.80f, 0.20f, 0.96f);
    static readonly Color ObstacleGray = new Color(0.62f, 0.62f, 0.58f, 0.92f);
    static readonly Color MinimapGroundColor = new Color(0.05f, 0.12f, 0.06f, 1f);

    static Sprite circleSprite;
    static Sprite ringSprite;
    static Sprite triangleSprite;

    sealed class ConvoyMarker
    {
        public RectTransform root;
        public Image pulse;
        public Color teamColor;
        public float phase;
        public bool seen;
    }

    sealed class IconPool
    {
        readonly List<Image> items = new List<Image>(32);
        readonly RectTransform parent;
        int used;

        public IconPool(RectTransform parentLayer)
        {
            parent = parentLayer;
        }

        public void Begin()
        {
            used = 0;
        }

        public Image Take()
        {
            while (used < items.Count)
            {
                Image existing = items[used];
                if (existing == null)
                {
                    items.RemoveAt(used);
                    continue;
                }
                used++;
                if (!existing.gameObject.activeSelf) existing.gameObject.SetActive(true);
                return existing;
            }
            if (parent == null) return null;
            RectTransform rect = CreateRect(
                "MinimapIcon_" + items.Count,
                parent,
                new Vector2(0.5f, 0.5f),
                Vector2.zero,
                new Vector2(10f, 10f),
                new Vector2(0.5f, 0.5f));
            Image image = rect.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            items.Add(image);
            used++;
            return image;
        }

        public void Finish()
        {
            for (int i = used; i < items.Count; i++)
            {
                Image item = items[i];
                if (item != null && item.gameObject.activeSelf)
                    item.gameObject.SetActive(false);
            }
        }
    }

    LycheeReplayPlayer replayPlayer;
    Canvas canvas;
    RectTransform panel;
    RectTransform nodeIconLayer;
    RectTransform convoyLayer;
    RawImage mapImage;
    readonly List<Image> borderImages = new List<Image>(4);
    readonly Dictionary<int, ConvoyMarker> convoyMarkers = new Dictionary<int, ConvoyMarker>();
    IconPool nodePool;

    Camera minimapCamera;
    RenderTexture minimapTexture;
    Coroutine renderLoop;
    Vector3 mapCenter;

    LycheeReplayFrame currentFrame;
    bool rushActive;
    bool borderNeedsReset;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Disabled) return;
        LycheeReplayPlayer player = FindFirstObjectByType<LycheeReplayPlayer>();
        if (player == null) return;
        if (player.GetComponent<LycheeMinimapSystem>() != null) return;
        player.gameObject.AddComponent<LycheeMinimapSystem>();
    }

    void Awake()
    {
        replayPlayer = GetComponent<LycheeReplayPlayer>();
        if (replayPlayer == null) replayPlayer = FindFirstObjectByType<LycheeReplayPlayer>();
        EnsureSprites();
        mapCenter = ComputeMapCenter();
        BuildMinimapCamera();
        BuildUi();
        nodePool = new IconPool(nodeIconLayer);
    }

    void OnEnable()
    {
        LycheeReplayPlayer.RoundApplied -= HandleRoundApplied;
        LycheeReplayPlayer.RoundApplied += HandleRoundApplied;
        if (renderLoop == null) renderLoop = StartCoroutine(RenderLoop());
    }

    void OnDisable()
    {
        LycheeReplayPlayer.RoundApplied -= HandleRoundApplied;
        if (renderLoop != null)
        {
            StopCoroutine(renderLoop);
            renderLoop = null;
        }
    }

    void Start()
    {
        if (replayPlayer == null) return;
        currentFrame = replayPlayer.CurrentFrame;
        rushActive = currentFrame != null && currentFrame.phase == "RUSH";
        RefreshNodeIcons(currentFrame);
    }

    void LateUpdate()
    {
        if (canvas != null && canvas.gameObject.activeSelf == Disabled)
            canvas.gameObject.SetActive(!Disabled);
        if (Disabled) return;
        UpdateBorderBreath();
        UpdateConvoyMarkers();
    }

    void OnDestroy()
    {
        LycheeReplayPlayer.RoundApplied -= HandleRoundApplied;
        if (minimapCamera != null)
        {
            minimapCamera.targetTexture = null;
            Destroy(minimapCamera.gameObject);
        }
        if (minimapTexture != null)
        {
            minimapTexture.Release();
            Destroy(minimapTexture);
        }
        if (canvas != null) Destroy(canvas.gameObject);
    }

    void HandleRoundApplied(LycheeReplayFrame previous, LycheeReplayFrame frame)
    {
        if (this == null) return;
        currentFrame = frame;
        rushActive = frame != null && frame.phase == "RUSH";
        RefreshNodeIcons(frame);
    }

    // ------------------------------------------------------------------
    // 俯视相机 + RenderTexture
    // ------------------------------------------------------------------

    void BuildMinimapCamera()
    {
        try
        {
            GameObject cameraObject = new GameObject("MinimapCamera");
            cameraObject.transform.position = mapCenter + Vector3.up * CameraHeight;
            cameraObject.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            minimapCamera = cameraObject.AddComponent<Camera>();
            minimapCamera.orthographic = true;
            minimapCamera.orthographicSize = CameraOrthoSize;
            minimapCamera.cullingMask = ~0;
            minimapCamera.clearFlags = CameraClearFlags.SolidColor;
            minimapCamera.backgroundColor = MinimapGroundColor;
            minimapCamera.nearClipPlane = 0.3f;
            minimapCamera.farClipPlane = CameraHeight + 180f;
            minimapCamera.allowHDR = false;
            minimapCamera.allowMSAA = false;
            minimapCamera.depth = -20f;

            minimapTexture = new RenderTexture(MinimapTextureSize, MinimapTextureSize, 16)
            {
                name = "LycheeMinimapRT",
                filterMode = FilterMode.Bilinear,
                antiAliasing = 1
            };
            minimapTexture.Create();
            minimapCamera.targetTexture = minimapTexture;
            minimapCamera.enabled = false;

            UniversalAdditionalCameraData extraData = minimapCamera.GetUniversalAdditionalCameraData();
            if (extraData != null)
            {
                extraData.renderPostProcessing = false;
                extraData.renderShadows = false;
                extraData.antialiasing = AntialiasingMode.None;
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("小地图相机创建失败，使用纯色兜底：" + exception.Message, this);
            if (minimapCamera != null)
            {
                minimapCamera.targetTexture = null;
                Destroy(minimapCamera.gameObject);
            }
            minimapCamera = null;
        }
    }

    IEnumerator RenderLoop()
    {
        WaitForSeconds wait = new WaitForSeconds(RenderInterval);
        yield return null;
        while (true)
        {
            if (!Disabled) RenderMinimapOnce();
            yield return wait;
        }
    }

    void RenderMinimapOnce()
    {
        if (minimapCamera == null || minimapTexture == null) return;
        if (!minimapTexture.IsCreated()) minimapTexture.Create();
        if (mapImage != null && mapImage.texture == null)
        {
            mapImage.texture = minimapTexture;
            mapImage.color = Color.white;
        }
        try
        {
            UniversalRenderPipeline.SingleCameraRequest request = new UniversalRenderPipeline.SingleCameraRequest
            {
                destination = minimapTexture
            };
            if (RenderPipeline.SupportsRenderRequest(minimapCamera, request))
            {
                RenderPipeline.SubmitRenderRequest(minimapCamera, request);
                return;
            }
        }
        catch (Exception)
        {
            // 走 Camera.Render 兜底
        }
        try
        {
            minimapCamera.Render();
        }
        catch (Exception)
        {
            // 渲染失败时保留上一帧画面即可，不抛异常
        }
    }

    static Vector3 ComputeMapCenter()
    {
        MapStationAnchor[] anchors = FindObjectsByType<MapStationAnchor>(FindObjectsSortMode.None);
        if (anchors == null || anchors.Length == 0) return Vector3.zero;
        Vector3 min = anchors[0].transform.position;
        Vector3 max = min;
        for (int i = 1; i < anchors.Length; i++)
        {
            if (anchors[i] == null) continue;
            Vector3 position = anchors[i].transform.position;
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }
        return (min + max) * 0.5f;
    }

    // ------------------------------------------------------------------
    // UI 搭建
    // ------------------------------------------------------------------

    void BuildUi()
    {
        GameObject canvasObject = new GameObject("LycheeMinimapCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        canvasObject.transform.SetParent(transform, false);
        canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = CanvasSortingOrder;
        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        panel = CreateRect(
            "MinimapPanel",
            canvasObject.transform,
            new Vector2(1f, 1f),
            new Vector2(-16f, -16f),
            new Vector2(PanelSize, PanelSize),
            new Vector2(1f, 1f));
        Image background = panel.gameObject.AddComponent<Image>();
        background.color = PanelBackground;
        background.raycastTarget = false;

        RectTransform mapRect = CreateRect(
            "MapImage",
            panel,
            new Vector2(0.5f, 0.5f),
            Vector2.zero,
            new Vector2(MapSize, MapSize),
            new Vector2(0.5f, 0.5f));
        mapImage = mapRect.gameObject.AddComponent<RawImage>();
        mapImage.raycastTarget = false;
        if (minimapTexture != null)
        {
            mapImage.texture = minimapTexture;
            mapImage.color = Color.white;
        }
        else
        {
            mapImage.color = MinimapGroundColor;
        }

        nodeIconLayer = CreateRect(
            "NodeIconLayer",
            panel,
            new Vector2(0.5f, 0.5f),
            Vector2.zero,
            new Vector2(MapSize, MapSize),
            new Vector2(0.5f, 0.5f));
        convoyLayer = CreateRect(
            "ConvoyLayer",
            panel,
            new Vector2(0.5f, 0.5f),
            Vector2.zero,
            new Vector2(MapSize, MapSize),
            new Vector2(0.5f, 0.5f));

        AddBorder("BorderTop", new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(PanelSize, 1f));
        AddBorder("BorderBottom", new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(PanelSize, 1f));
        AddBorder("BorderLeft", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(1f, PanelSize));
        AddBorder("BorderRight", new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, PanelSize));

        Sprite frameSprite = LycheeLoadThemeSprite("minimap_frame.png");
        if (frameSprite != null)
        {
            RectTransform frameRect = CreateRect(
                "ThemeFrame",
                panel,
                new Vector2(0.5f, 0.5f),
                Vector2.zero,
                new Vector2(PanelSize, PanelSize),
                new Vector2(0.5f, 0.5f));
            Image frameImage = frameRect.gameObject.AddComponent<Image>();
            frameImage.sprite = frameSprite;
            frameImage.color = Color.white;
            frameImage.raycastTarget = false;
        }
    }

    void AddBorder(string borderName, Vector2 anchor, Vector2 pivot, Vector2 size)
    {
        RectTransform rect = CreateRect(borderName, panel, anchor, Vector2.zero, size, pivot);
        Image image = rect.gameObject.AddComponent<Image>();
        image.color = GoldBorder;
        image.raycastTarget = false;
        borderImages.Add(image);
    }

    static RectTransform CreateRect(
        string objectName,
        Transform parent,
        Vector2 anchor,
        Vector2 anchoredPosition,
        Vector2 size,
        Vector2 pivot)
    {
        GameObject go = new GameObject(objectName, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = pivot;
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;
        rect.localScale = Vector3.one;
        return rect;
    }

    static Image CreateIconImage(string objectName, Transform parent, Sprite sprite, Color color, Vector2 size)
    {
        RectTransform rect = CreateRect(objectName, parent, new Vector2(0.5f, 0.5f), Vector2.zero, size, new Vector2(0.5f, 0.5f));
        Image image = rect.gameObject.AddComponent<Image>();
        image.sprite = sprite;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    // ------------------------------------------------------------------
    // 动态图标
    // ------------------------------------------------------------------

    void RefreshNodeIcons(LycheeReplayFrame frame)
    {
        if (nodePool == null || replayPlayer == null) return;
        nodePool.Begin();
        try
        {
            if (frame != null && frame.nodes != null)
            {
                foreach (LycheeReplayNodeState node in frame.nodes)
                {
                    if (node == null || string.IsNullOrEmpty(node.nodeId)) continue;
                    if (!replayPlayer.TryGetStationPosition(node.nodeId, out Vector3 world, out string _)) continue;
                    if (!TryWorldToLayer(world, out Vector2 local)) continue;
                    bool hasGuard = node.guard != null &&
                        node.guard.defense > 0 &&
                        !string.IsNullOrEmpty(node.guard.ownerTeamId);
                    if (hasGuard)
                        PlaceIcon(nodePool.Take(), null, GuardColor(node.guard.ownerTeamId, frame), new Vector2(10f, 10f), 0f, local);
                    if (node.hasObstacle)
                        PlaceIcon(
                            nodePool.Take(),
                            triangleSprite,
                            ObstacleGray,
                            new Vector2(11f, 10f),
                            0f,
                            hasGuard ? local + new Vector2(0f, 8f) : local);
                }
            }
            if (frame != null && frame.tasks != null)
            {
                foreach (LycheeReplayTaskState task in frame.tasks)
                {
                    if (task == null || task.status != "ACTIVE") continue;
                    if (!replayPlayer.TryGetStationPosition(task.targetNodeId, out Vector3 world, out string _)) continue;
                    if (!TryWorldToLayer(world, out Vector2 local)) continue;
                    PlaceIcon(nodePool.Take(), null, TaskGold, new Vector2(11f, 11f), 45f, local);
                }
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("小地图节点图标刷新失败：" + exception.Message, this);
        }
        nodePool.Finish();
    }

    static void PlaceIcon(Image icon, Sprite sprite, Color color, Vector2 size, float rotationZ, Vector2 local)
    {
        if (icon == null) return;
        icon.sprite = sprite;
        icon.color = color;
        RectTransform rect = icon.rectTransform;
        rect.sizeDelta = size;
        rect.localEulerAngles = new Vector3(0f, 0f, rotationZ);
        rect.anchoredPosition = local;
    }

    void UpdateConvoyMarkers()
    {
        foreach (ConvoyMarker marker in convoyMarkers.Values)
            marker.seen = false;

        LycheeReplayFrame frame = currentFrame;
        if (frame == null && replayPlayer != null) frame = replayPlayer.CurrentFrame;
        if (frame != null && frame.players != null && replayPlayer != null && convoyLayer != null)
        {
            foreach (LycheeReplayPlayerState state in frame.players)
            {
                if (state == null) continue;
                ConvoyMarker marker = GetOrCreateMarker(state);
                if (marker == null || marker.root == null) continue;

                Vector3 world;
                Transform convoyRoot = replayPlayer.GetConvoyRoot(state.playerId);
                if (convoyRoot != null) world = convoyRoot.position;
                else if (!TryResolvePlayerWorld(state, out world)) continue;
                if (!TryWorldToLayer(world, out Vector2 local)) continue;

                marker.seen = true;
                if (!marker.root.gameObject.activeSelf) marker.root.gameObject.SetActive(true);
                marker.root.anchoredPosition = local;

                if (marker.pulse != null)
                {
                    float t = Mathf.Repeat(Time.unscaledTime + marker.phase, PulsePeriod) / PulsePeriod;
                    marker.pulse.rectTransform.localScale = Vector3.one * (1f + 0.6f * t);
                    Color pulseColor = marker.teamColor;
                    pulseColor.a = (1f - t) * 0.85f;
                    marker.pulse.color = pulseColor;
                }
            }
        }

        foreach (ConvoyMarker marker in convoyMarkers.Values)
        {
            if (!marker.seen && marker.root != null && marker.root.gameObject.activeSelf)
                marker.root.gameObject.SetActive(false);
        }
    }

    ConvoyMarker GetOrCreateMarker(LycheeReplayPlayerState state)
    {
        if (convoyMarkers.TryGetValue(state.playerId, out ConvoyMarker existing) &&
            existing != null &&
            existing.root != null)
            return existing;
        if (convoyLayer == null) return null;

        Color teamColor = state.camp == 0 ? RedTeamColor : BlueTeamColor;
        ConvoyMarker marker = new ConvoyMarker
        {
            teamColor = teamColor,
            phase = Mathf.Abs(state.playerId) * 0.27f
        };
        marker.root = CreateRect(
            "ConvoyMarker_" + state.playerId,
            convoyLayer,
            new Vector2(0.5f, 0.5f),
            Vector2.zero,
            new Vector2(18f, 18f),
            new Vector2(0.5f, 0.5f));
        Sprite pulseSprite = ringSprite != null ? ringSprite : circleSprite;
        marker.pulse = CreateIconImage("Pulse", marker.root, pulseSprite, teamColor, new Vector2(18f, 18f));
        CreateIconImage("Halo", marker.root, circleSprite, Color.white, new Vector2(18f, 18f));
        CreateIconImage("Dot", marker.root, circleSprite, teamColor, new Vector2(14f, 14f));
        convoyMarkers[state.playerId] = marker;
        return marker;
    }

    bool TryResolvePlayerWorld(LycheeReplayPlayerState state, out Vector3 world)
    {
        world = Vector3.zero;
        if (state == null || replayPlayer == null) return false;
        string stationId = !string.IsNullOrEmpty(state.currentNodeId) ? state.currentNodeId : state.nextNodeId;
        if (replayPlayer.TryGetStationPosition(stationId, out Vector3 stationPosition, out string _))
        {
            world = stationPosition;
            return true;
        }
        if (state.position != null)
        {
            world = new Vector3(state.position.x - 40f, 0f, 30f - state.position.y);
            return true;
        }
        return false;
    }

    bool TryWorldToLayer(Vector3 world, out Vector2 local)
    {
        local = Vector2.zero;
        if (minimapCamera != null)
        {
            Vector3 viewport = minimapCamera.WorldToViewportPoint(world);
            if (viewport.z < 0f) return false;
            local = new Vector2(
                (Mathf.Clamp01(viewport.x) - 0.5f) * MapSize,
                (Mathf.Clamp01(viewport.y) - 0.5f) * MapSize);
            return true;
        }
        // 相机缺失时按正交参数手动映射，保证图标层仍可用。
        float extent = CameraOrthoSize * 2f;
        if (extent <= 0.0001f) return false;
        local = new Vector2(
            (Mathf.Clamp01((world.x - mapCenter.x) / extent + 0.5f) - 0.5f) * MapSize,
            (Mathf.Clamp01((world.z - mapCenter.z) / extent + 0.5f) - 0.5f) * MapSize);
        return true;
    }

    Color GuardColor(string ownerTeamId, LycheeReplayFrame frame)
    {
        if (!string.IsNullOrEmpty(ownerTeamId))
        {
            string upper = ownerTeamId.ToUpperInvariant();
            if (upper.Contains("RED")) return RedTeamColor;
            if (upper.Contains("BLUE")) return BlueTeamColor;
            if (frame != null && frame.players != null)
            {
                foreach (LycheeReplayPlayerState state in frame.players)
                {
                    if (state == null || state.teamId != ownerTeamId) continue;
                    return state.camp == 0 ? RedTeamColor : BlueTeamColor;
                }
            }
        }
        return GoldBorder;
    }

    void UpdateBorderBreath()
    {
        if (borderImages.Count == 0) return;
        if (rushActive)
        {
            float wave = (Mathf.Sin(Time.unscaledTime * 4.2f) + 1f) * 0.5f;
            Color color = Color.Lerp(RushBorderGold, RushBorderRed, wave);
            for (int i = 0; i < borderImages.Count; i++)
                if (borderImages[i] != null) borderImages[i].color = color;
            borderNeedsReset = true;
            return;
        }
        if (!borderNeedsReset) return;
        borderNeedsReset = false;
        for (int i = 0; i < borderImages.Count; i++)
            if (borderImages[i] != null) borderImages[i].color = GoldBorder;
    }

    // ------------------------------------------------------------------
    // 资源加载 / 程序化精灵
    // ------------------------------------------------------------------

    static Sprite LycheeLoadThemeSprite(string fileName)
    {
        try
        {
            byte[] bytes = LycheeThemeCache.LoadBytes(fileName);
            if (bytes == null || bytes.Length == 0) return null;
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(bytes)) return null;
            texture.name = "LycheeMinimapTheme_" + fileName;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.hideFlags = HideFlags.HideAndDontSave;
            return Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                100f);
        }
        catch (Exception)
        {
            return null;
        }
    }

    static void EnsureSprites()
    {
        if (circleSprite == null) circleSprite = LycheeMakeCircleSprite(32);
        if (ringSprite == null) ringSprite = LycheeMakeRingSprite(32);
        if (triangleSprite == null) triangleSprite = LycheeMakeTriangleSprite(32);
    }

    static Sprite LycheeMakeCircleSprite(int size)
    {
        try
        {
            Texture2D texture = NewIconTexture(size, "LycheeMinimapCircle");
            float radius = size * 0.5f - 1f;
            Vector2 center = new Vector2(size * 0.5f - 0.5f, size * 0.5f - 0.5f);
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x, y), center);
                    float alpha = Mathf.Clamp01(radius - distance + 0.5f);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }
            return FinishIconSprite(texture, pixels, size);
        }
        catch (Exception)
        {
            return null;
        }
    }

    static Sprite LycheeMakeRingSprite(int size)
    {
        try
        {
            Texture2D texture = NewIconTexture(size, "LycheeMinimapRing");
            float bandCenter = size * 0.5f - 3f;
            const float halfBand = 2f;
            Vector2 center = new Vector2(size * 0.5f - 0.5f, size * 0.5f - 0.5f);
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x, y), center);
                    float alpha = Mathf.Clamp01(halfBand - Mathf.Abs(distance - bandCenter) + 0.5f);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }
            return FinishIconSprite(texture, pixels, size);
        }
        catch (Exception)
        {
            return null;
        }
    }

    static Sprite LycheeMakeTriangleSprite(int size)
    {
        try
        {
            Texture2D texture = NewIconTexture(size, "LycheeMinimapTriangle");
            float centerX = size * 0.5f - 0.5f;
            float halfBase = size * 0.5f - 1.5f;
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                float rowFraction = size <= 1 ? 0f : (float)y / (size - 1);
                float rowHalfWidth = Mathf.Lerp(halfBase, 0f, rowFraction);
                for (int x = 0; x < size; x++)
                {
                    float alpha = Mathf.Clamp01(rowHalfWidth - Mathf.Abs(x - centerX) + 0.5f);
                    if (y < 1) alpha = 0f;
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }
            return FinishIconSprite(texture, pixels, size);
        }
        catch (Exception)
        {
            return null;
        }
    }

    static Texture2D NewIconTexture(int size, string textureName)
    {
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = textureName,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave
        };
        return texture;
    }

    static Sprite FinishIconSprite(Texture2D texture, Color32[] pixels, int size)
    {
        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        Sprite sprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, size, size),
            new Vector2(0.5f, 0.5f),
            100f);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }
}
