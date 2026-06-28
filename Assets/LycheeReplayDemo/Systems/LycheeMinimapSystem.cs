using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

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
    static readonly Color WeatherBadgeBg = new Color(0.78f, 0.58f, 0.22f, 0.72f);
    static readonly Color WeatherBadgeEdge = new Color(1f, 0.78f, 0.32f, 0.95f);

    static Sprite circleSprite;
    static Sprite ringSprite;
    static Sprite triangleSprite;
    static readonly Dictionary<string, Sprite> weatherSpriteCache = new Dictionary<string, Sprite>();

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
    RectTransform weatherLayer;
    RawImage mapImage;
    Image themeFrameImage;
    RectTransform weatherMarker;
    Image weatherMarkerBg;
    Image weatherMarkerIcon;
    Text weatherMarkerText;
    readonly List<Image> borderImages = new List<Image>(4);
    readonly Dictionary<int, ConvoyMarker> convoyMarkers = new Dictionary<int, ConvoyMarker>();
    IconPool nodePool;

    Camera minimapCamera;
    RenderTexture minimapTexture;
    Coroutine renderLoop;
    Vector3 mapCenter;
    // 俯视相机实际正交半径：按站点包围盒动态求得，保证首末站（尤其右上角终点 S15）不被裁切。
    // 默认值仅作兜底；Awake 里用 ComputeMapOrthoSize() 覆盖。
    float mapOrthoSize = CameraOrthoSize;

    LycheeReplayFrame currentFrame;
    bool rushActive;
    bool borderNeedsReset;
    bool themeFrameResolved;
    float nextThemeFrameRetry;

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
        mapOrthoSize = ComputeMapOrthoSize();
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
        UpgradeThemeFrame();
        UpdateBorderBreath();
        UpdateConvoyMarkers();
        UpdateWeatherMarker();
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
            minimapCamera.orthographicSize = mapOrthoSize;
            minimapCamera.cullingMask = MinimapCullingMask();
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

    static int MinimapCullingMask()
    {
        int mask = ~0;
        ExcludeLayer(ref mask, "TransparentFX");
        return mask;
    }

    static void ExcludeLayer(ref int mask, string layerName)
    {
        int layer = LayerMask.NameToLayer(layerName);
        if (layer >= 0) mask &= ~(1 << layer);
    }

    /// <summary>按站点包围盒求俯视相机正交半径：取 X/Z 跨度较大者的一半再留 12% 余量，
    /// 保证整条路线（含右上角终点站）都落在小地图内——固定的 32 只能容下 64 宽，而站点横跨约 73，
    /// 终点 S15 会被裁出右边界，正是“右上角小地图显示不全”的根因。</summary>
    static float ComputeMapOrthoSize()
    {
        MapStationAnchor[] anchors = FindObjectsByType<MapStationAnchor>(FindObjectsSortMode.None);
        if (anchors == null || anchors.Length == 0) return CameraOrthoSize;
        Vector3 min = anchors[0].transform.position;
        Vector3 max = min;
        for (int i = 1; i < anchors.Length; i++)
        {
            if (anchors[i] == null) continue;
            Vector3 position = anchors[i].transform.position;
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }
        float halfSpan = Mathf.Max(max.x - min.x, max.z - min.z) * 0.5f;
        return Mathf.Max(CameraOrthoSize, halfSpan * 1.12f);
    }

    // ------------------------------------------------------------------
    // UI 搭建
    // ------------------------------------------------------------------

    void BuildUi()
    {
        // 必须带 GraphicRaycaster，否则小地图的"点击展开大图"按钮收不到点击(用户：点了没反应)；
        // 同时确保场景里有 EventSystem，否则任何 UI 点击都不触发。
        EnsureEventSystem();
        GameObject canvasObject = new GameObject("LycheeMinimapCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
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
        background.raycastTarget = true;   // 2026-06-25 用户：小地图可点击展开看全图做策略分析
        var expandBtn = panel.gameObject.AddComponent<UnityEngine.UI.Button>();
        expandBtn.transition = UnityEngine.UI.Selectable.Transition.None;
        expandBtn.targetGraphic = background;
        expandBtn.onClick.AddListener(ToggleMinimapExpanded);

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
        weatherLayer = CreateRect(
            "WeatherLayer",
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

        AddBorder("BorderTop", new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(PanelSize, 3f));
        AddBorder("BorderBottom", new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(PanelSize, 3f));
        AddBorder("BorderLeft", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(3f, PanelSize));
        AddBorder("BorderRight", new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(3f, PanelSize));

        RectTransform frameRect = CreateRect(
            "ThemeFrame",
            panel,
            new Vector2(0.5f, 0.5f),
            Vector2.zero,
            new Vector2(PanelSize, PanelSize),
            new Vector2(0.5f, 0.5f));
        themeFrameImage = frameRect.gameObject.AddComponent<Image>();
        themeFrameImage.color = Color.white;
        themeFrameImage.preserveAspect = true;
        themeFrameImage.raycastTarget = false;
        themeFrameImage.enabled = false;
        UpgradeThemeFrame();
    }

    bool minimapExpanded = false;
    /// <summary>点击小地图：右上角小图 ↔ 居中放大全图 切换（用户：方便选手看全景做策略分析）。整体 localScale 缩放，
    /// 地图与图标随之等比放大、无需重映射；再点收回角落。</summary>
    void ToggleMinimapExpanded()
    {
        if (panel == null) return;
        minimapExpanded = !minimapExpanded;
        if (minimapExpanded)
        {
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
            panel.anchoredPosition = Vector2.zero;
            panel.localScale = Vector3.one * 3.2f;   // 放大居中、看全图
        }
        else
        {
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(1f, 1f);
            panel.anchoredPosition = new Vector2(-16f, -16f);
            panel.localScale = Vector3.one;          // 收回右上角小图
        }
    }

    // 场景缺 EventSystem 时补一个，否则小地图(及一切 UI)点击都不触发。与 LycheeVideoInterlude 同款。
    static void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null) return;
        GameObject es = new GameObject("LycheeMinimapEventSystem");
        es.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
        es.AddComponent<InputSystemUIInputModule>();
#else
        es.AddComponent<StandaloneInputModule>();
#endif
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

    void UpdateWeatherMarker()
    {
        if (weatherLayer == null) return;
        LycheeReplayFrame frame = currentFrame;
        if (frame == null && replayPlayer != null) frame = replayPlayer.CurrentFrame;
        LycheeReplayWeatherState weather = FirstWeather(frame == null || frame.weather == null ? null : frame.weather.active);
        bool forecast = false;
        if (weather == null)
        {
            weather = FirstWeather(frame == null || frame.weather == null ? null : frame.weather.forecast);
            forecast = weather != null;
        }
        if (weather == null || string.IsNullOrEmpty(weather.type))
        {
            if (weatherMarker != null && weatherMarker.gameObject.activeSelf)
                weatherMarker.gameObject.SetActive(false);
            return;
        }

        EnsureWeatherMarker();
        if (weatherMarker == null) return;
        if (!weatherMarker.gameObject.activeSelf) weatherMarker.gameObject.SetActive(true);

        Vector3 anchor = WeatherAnchor(weather);
        if (TryWorldToLayer(anchor, out Vector2 local))
            weatherMarker.anchoredPosition = ClampToMap(local, 16f);
        else
            weatherMarker.anchoredPosition = new Vector2(-MapSize * 0.32f, MapSize * 0.32f);

        Sprite sprite = WeatherSprite(weather.type);
        if (weatherMarkerIcon != null)
        {
            weatherMarkerIcon.enabled = sprite != null;
            if (sprite != null) weatherMarkerIcon.sprite = sprite;
        }
        if (weatherMarkerText != null)
        {
            weatherMarkerText.enabled = sprite == null;
            weatherMarkerText.text = WeatherSymbol(weather.type);
        }
        if (weatherMarkerBg != null)
        {
            Color color = WeatherColor(weather.type);
            color.a = forecast ? 0.68f : 0.86f;
            weatherMarkerBg.color = color;
        }
        float pulse = forecast ? 0.92f + 0.08f * Mathf.Sin(Time.unscaledTime * 3.2f) : 1f;
        weatherMarker.localScale = Vector3.one * pulse;
    }

    void EnsureWeatherMarker()
    {
        if (weatherMarker != null) return;
        weatherMarker = CreateRect("WeatherMarker", weatherLayer, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(34f, 34f), new Vector2(0.5f, 0.5f));
        weatherMarkerBg = weatherMarker.gameObject.AddComponent<Image>();
        weatherMarkerBg.sprite = circleSprite;
        weatherMarkerBg.color = WeatherBadgeBg;
        weatherMarkerBg.raycastTarget = false;
        Outline outline = weatherMarker.gameObject.AddComponent<Outline>();
        outline.effectColor = WeatherBadgeEdge;
        outline.effectDistance = new Vector2(1.2f, -1.2f);

        RectTransform iconRt = CreateRect("WeatherIcon", weatherMarker, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(24f, 24f), new Vector2(0.5f, 0.5f));
        weatherMarkerIcon = iconRt.gameObject.AddComponent<Image>();
        weatherMarkerIcon.preserveAspect = true;
        weatherMarkerIcon.raycastTarget = false;

        weatherMarkerText = iconRt.gameObject.AddComponent<Text>();
        weatherMarkerText.font = Resources.Load<Font>("Fonts/LXGWWenKai-Regular") ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        weatherMarkerText.fontSize = 22;
        weatherMarkerText.fontStyle = FontStyle.Bold;
        weatherMarkerText.alignment = TextAnchor.MiddleCenter;
        weatherMarkerText.color = Color.white;
        weatherMarkerText.raycastTarget = false;
        weatherMarkerText.enabled = false;
    }

    Vector3 WeatherAnchor(LycheeReplayWeatherState weather)
    {
        string region = NormalizeWeatherRegion(weather == null ? null : weather.region, weather == null ? null : weather.type);
        if (TryWeatherRegionCenter(region, out Vector3 center)) return center;
        return mapCenter;
    }

    bool TryWeatherRegionCenter(string region, out Vector3 center)
    {
        center = mapCenter;
        MapStationAnchor[] anchors = FindObjectsByType<MapStationAnchor>(FindObjectsSortMode.None);
        if (anchors == null || anchors.Length == 0) return false;
        Vector3 sum = Vector3.zero;
        int count = 0;
        for (int i = 0; i < anchors.Length; i++)
        {
            MapStationAnchor anchor = anchors[i];
            if (anchor == null || !StationMatchesWeatherRegion(anchor, region)) continue;
            sum += anchor.transform.position;
            count++;
        }
        if (count <= 0) return false;
        center = sum / count;
        return true;
    }

    static bool StationMatchesWeatherRegion(MapStationAnchor anchor, string region)
    {
        if (anchor == null) return false;
        string id = anchor.stationId ?? string.Empty;
        MapStationType type = anchor.stationType;
        string combined = (id + " " + type + " " + anchor.displayName).ToUpperInvariant();
        if (region == "WATER")
            return type == MapStationType.WaterStation || type == MapStationType.Dock ||
                combined.Contains("WATER") || combined.Contains("DOCK") || id == "S04" || id == "S05" || id == "S10";
        if (region == "MOUNTAIN")
            return combined.Contains("MOUNTAIN") || combined.Contains("PASS") || id == "S06" || id == "S08";
        if (region == "PLAIN")
            return id == "S07" || id == "S09" || id == "S11" || id == "S13";
        return true;
    }

    static string NormalizeWeatherRegion(string region, string type)
    {
        string r = string.IsNullOrEmpty(region) ? string.Empty : region.Trim().ToUpperInvariant();
        string t = string.IsNullOrEmpty(type) ? string.Empty : type.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(r) && r != "ALL") return r;
        if (t == "HEAVY_RAIN") return "WATER";
        if (t == "MOUNTAIN_FOG") return "MOUNTAIN";
        if (t == "HOT") return "PLAIN";
        return "ALL";
    }

    static LycheeReplayWeatherState FirstWeather(LycheeReplayWeatherState[] list)
    {
        if (list == null) return null;
        for (int i = 0; i < list.Length; i++)
            if (list[i] != null && !string.IsNullOrEmpty(list[i].type)) return list[i];
        return null;
    }

    static Vector2 ClampToMap(Vector2 local, float inset)
    {
        float half = MapSize * 0.5f - inset;
        return new Vector2(Mathf.Clamp(local.x, -half, half), Mathf.Clamp(local.y, -half, half));
    }

    static Sprite WeatherSprite(string type)
    {
        string fileName = WeatherIconFile(type);
        if (string.IsNullOrEmpty(fileName)) return null;
        if (weatherSpriteCache.TryGetValue(fileName, out Sprite cached)) return cached;
        Sprite sprite = LoadThemeSprite(fileName);
        if (sprite != null || LycheeThemeCache.Preloaded) weatherSpriteCache[fileName] = sprite;
        return sprite;
    }

    static string WeatherIconFile(string type)
    {
        if (type == "HOT") return "icon_weather_hot.png";
        if (type == "MOUNTAIN_FOG") return "icon_weather_fog.png";
        if (type == "HEAVY_RAIN") return "icon_weather_rain.png";
        return "EventIcons/icon_event_weather.png";
    }

    static Color WeatherColor(string type)
    {
        if (type == "HOT") return new Color(0.90f, 0.44f, 0.10f, 0.86f);
        if (type == "HEAVY_RAIN") return new Color(0.14f, 0.36f, 0.78f, 0.86f);
        if (type == "MOUNTAIN_FOG") return new Color(0.58f, 0.64f, 0.66f, 0.86f);
        return WeatherBadgeBg;
    }

    static string WeatherSymbol(string type)
    {
        if (type == "HOT") return "☀";
        if (type == "HEAVY_RAIN") return "雨";
        if (type == "MOUNTAIN_FOG") return "雾";
        return "云";
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
        float extent = mapOrthoSize * 2f;
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

    void UpgradeThemeFrame()
    {
        if (themeFrameResolved || themeFrameImage == null) return;
        if (Time.unscaledTime < nextThemeFrameRetry) return;
        nextThemeFrameRetry = Time.unscaledTime + 0.35f;
        Sprite frameSprite = LycheeLoadThemeSprite("minimap_frame.png");
        if (frameSprite != null)
        {
            themeFrameImage.sprite = frameSprite;
            themeFrameImage.enabled = true;
            themeFrameImage.transform.SetAsLastSibling();
            themeFrameResolved = true;
            return;
        }
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

    static Sprite LoadThemeSprite(string fileName)
    {
        try
        {
            byte[] bytes = LycheeThemeCache.LoadBytes(fileName);
            if (bytes == null || bytes.Length == 0) return null;
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(bytes)) return null;
            texture.wrapMode = TextureWrapMode.Clamp;
            return Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
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
