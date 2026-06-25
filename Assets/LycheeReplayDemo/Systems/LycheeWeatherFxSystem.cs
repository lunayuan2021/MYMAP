using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

/// <summary>
/// 回放天气特效系统：订阅 LycheeReplayPlayer.RoundApplied，按 frame.weather.active
/// 驱动暴雨（HEAVY_RAIN）/山雾（MOUNTAIN_FOG）/酷热（HOT）的全程序化视觉表现。
/// 自举挂载到 LycheeReplayPlayer 所在 GameObject，不依赖场景手工配置；
/// 天气切换先平滑恢复再应用，组件销毁时恢复全部全局渲染状态。
/// </summary>
public sealed class LycheeWeatherFxSystem : MonoBehaviour
{
    /// <summary>全局开关：置 true 可禁用本系统（自举与运行时均生效）。</summary>
    public static bool Disabled;

    /// 天气环境效果正在驱动全局光照/雾时为 true，其他系统（如 LycheeCinematicDirector 的 RUSH 灯光）应暂停写入平行光，避免双写打架。
    public static bool EnvironmentActive { get; private set; }

    const float BlendInSeconds = 1.2f;   // 天气进场插值时长
    const float BlendOutSeconds = 2f;    // 天气退场/恢复插值时长（设计验收：2 秒）
    const int RainParticleBudget = 900;  // WebGL 预算：天气用于识别环境，不做遮屏级雨幕
    const float RainEmitterHeight = 13f; // 雨幕发射盒高度
    const float RainFallSpeed = 14f;     // 雨滴下落速度 m/s
    const int MaxEmitterSpots = 4;       // 单种天气最多发射器数量，超出则合并为包围盒

    sealed class RegionSpot
    {
        public Vector3 position;
        public float radius;
    }

    sealed class EnvProfile
    {
        public bool fogEnabled;
        public Color fogColor;
        public float fogStart;
        public float fogEnd;
        public float sunMultiplier = 1f;
        public bool useVolume;

        // 全屏氛围叠加层（三张 ScreenSpaceOverlay Image）的目标值，全部 ×envBlend 后生效：
        public Color overlayTint = Color.white;   // tint 染色图的 RGB
        public float overlayAlpha;                 // tint 满淡入时的目标 alpha
        public Color vignetteColor = Color.black;  // 暗角染色（HOT 用暖橙边缘）
        public float vignetteAlpha;                // 暗角满淡入时的目标 alpha
        public bool useRainOverlay;                // 是否启用滚动雨丝层（仅 HEAVY_RAIN）
        public float rainScroll;                   // 雨丝 UV.y 每秒推进格数（向下滚）

        // 运行时调色 Volume（ColorAdjustments）的目标参数，weight = useGrade ? envBlend : 0：
        public float saturation;                   // 饱和度 [-100,100]
        public float contrast;                     // 对比度 [-100,100]
        public Color gradeFilter = Color.white;    // colorFilter 染色
        public float gradeExposure;                // postExposure 曝光补偿
        public bool useGrade;                      // 是否启用调色 Volume
    }

    LycheeReplayPlayer player;
    Transform fxRoot;
    readonly List<ParticleSystem> activeParticles = new List<ParticleSystem>();

    string desiredType;
    string desiredRegion;
    string desiredKey;
    string currentType;
    string currentKey;
    EnvProfile currentProfile;
    float envBlend;

    bool envCaptured;
    bool envDirty;
    bool origFog;
    Color origFogColor;
    FogMode origFogMode;
    float origFogStart;
    float origFogEnd;
    float origFogDensity;

    Light sun;
    bool sunCaptured;
    float origSunIntensity;

    GameObject volumeObject;
    Volume hotVolume;
    VolumeProfile volumeProfileInstance;
    ColorAdjustments gradeAdjustments;

    // 全屏天气氛围叠加层（镜像 HUD 的 ScreenSpaceOverlay Canvas 写法，sortingOrder=125 压在 HUD 130 之下）
    Canvas weatherCanvas;
    Image overlayTintImg;
    Image overlayVignetteImg;
    RawImage overlayRainImg;
    Texture2D whiteTex;
    Texture2D vignetteTex;
    Texture2D rainScrollTex;       // 滚动雨丝层专用：Repeat 包裹（既有 rainTexture 为 Clamp，不可复用作平铺）
    bool rainScrollTexOwned;
    Rect rainUv = new Rect(0f, 0f, 6f, 5f); // UV 平铺次数（横 6 纵 5，更密=雨丝更细），y 偏移随时间推进模拟下落

    ParticleSystem heatShimmer;
    Vector3 heatDefaultPosition;

    Material rainMaterial;
    Material fogMaterial;
    Material heatMaterial;
    Texture2D rainTexture;
    Texture2D puffTexture;
    bool rainTextureOwned;
    bool puffTextureOwned;
    bool materialsBuilt;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void LycheeWeatherFxBootstrap()
    {
        if (Disabled) return;
        LycheeReplayPlayer host = UnityEngine.Object.FindFirstObjectByType<LycheeReplayPlayer>();
        if (host == null) return;
        if (host.GetComponent<LycheeWeatherFxSystem>() != null) return;
        host.gameObject.AddComponent<LycheeWeatherFxSystem>();
    }

    void Awake()
    {
        player = GetComponent<LycheeReplayPlayer>();
        if (player == null) player = FindFirstObjectByType<LycheeReplayPlayer>();
    }

    void OnEnable()
    {
        LycheeReplayPlayer.RoundApplied += HandleRoundApplied;
        SyncFromFrame(player != null ? player.CurrentFrame : null);
    }

    void OnDisable()
    {
        LycheeReplayPlayer.RoundApplied -= HandleRoundApplied;
        RetireActiveParticles();
        currentType = null;
        currentKey = null;
        currentProfile = null;
        desiredType = null;
        desiredRegion = null;
        desiredKey = null;
        envBlend = 0f;
        EnvironmentActive = false;
        if (envDirty)
        {
            RestoreOriginalEnvironment();
            envDirty = false;
        }
    }

    void Start()
    {
        SyncFromFrame(player != null ? player.CurrentFrame : null);
    }

    void OnDestroy()
    {
        if (envDirty) RestoreOriginalEnvironment();
        if (fxRoot != null) Destroy(fxRoot.gameObject);
        if (weatherCanvas != null) Destroy(weatherCanvas.gameObject);
        if (volumeObject != null) Destroy(volumeObject);
        if (volumeProfileInstance != null) Destroy(volumeProfileInstance);
        if (rainMaterial != null) Destroy(rainMaterial);
        if (fogMaterial != null) Destroy(fogMaterial);
        if (heatMaterial != null) Destroy(heatMaterial);
        if (rainTexture != null && rainTextureOwned) Destroy(rainTexture);
        if (puffTexture != null && puffTextureOwned) Destroy(puffTexture);
        if (whiteTex != null) Destroy(whiteTex);
        if (vignetteTex != null) Destroy(vignetteTex);
        if (rainScrollTex != null && rainScrollTexOwned) Destroy(rainScrollTex);
    }

    void Update()
    {
        if (Disabled)
        {
            desiredType = null;
            desiredRegion = null;
            desiredKey = null;
        }

        if (!string.Equals(currentKey, desiredKey, StringComparison.Ordinal))
        {
            // 切换/结束：先停旧粒子（残留自然消亡），全局状态插值回原状，归零后再应用新天气。
            RetireActiveParticles();
            envBlend = Mathf.MoveTowards(envBlend, 0f, Time.deltaTime / BlendOutSeconds);
            if (envBlend <= 0.0001f) ActivateWeather(desiredType, desiredRegion, desiredKey);
        }
        else if (currentKey != null)
        {
            envBlend = Mathf.MoveTowards(envBlend, 1f, Time.deltaTime / BlendInSeconds);
        }
        else
        {
            envBlend = 0f;
        }

        EnvironmentActive = envBlend > 0.0001f;
        ApplyEnvironmentBlend();
        ApplyOverlayBlend();
        UpdateHeatShimmer();
    }

    void HandleRoundApplied(LycheeReplayFrame previous, LycheeReplayFrame current)
    {
        SyncFromFrame(current);
    }

    void SyncFromFrame(LycheeReplayFrame frame)
    {
        string type = null;
        string region = null;
        if (!Disabled && frame != null && frame.weather != null && frame.weather.active != null)
        {
            foreach (LycheeReplayWeatherState state in frame.weather.active)
            {
                if (state == null) continue;
                string candidate = NormalizeToken(state.type);
                if (candidate != "HOT" && candidate != "HEAVY_RAIN" && candidate != "MOUNTAIN_FOG") continue;
                type = candidate;
                region = NormalizeRegion(state.region, candidate);
                break; // 同屏只会有一种天气，取第一条有效记录
            }
        }
        desiredType = type;
        desiredRegion = region;
        desiredKey = type == null ? null : type + "|" + region;
    }

    static string NormalizeToken(string value)
    {
        return string.IsNullOrEmpty(value) ? string.Empty : value.Trim().ToUpperInvariant();
    }

    static string NormalizeRegion(string region, string weatherType)
    {
        string normalized = NormalizeToken(region);
        if (!string.IsNullOrEmpty(normalized)) return normalized;
        if (weatherType == "HEAVY_RAIN") return "WATER";
        if (weatherType == "MOUNTAIN_FOG") return "MOUNTAIN";
        return "ALL";
    }

    // ---------------- 天气激活 ----------------

    void ActivateWeather(string type, string region, string key)
    {
        currentType = type;
        currentKey = key;
        if (string.IsNullOrEmpty(type))
        {
            currentProfile = null;
            return;
        }

        CaptureOriginalEnvironment();
        EnsureSun();
        EnsureMaterials();
        EnsureOverlay();
        List<RegionSpot> spots = CollectRegionSpots(type, region);
        heatDefaultPosition = spots.Count > 0 ? spots[0].position : Vector3.zero;

        if (type == "HEAVY_RAIN")
        {
            currentProfile = new EnvProfile
            {
                fogEnabled = true,
                fogColor = new Color(0.58f, 0.64f, 0.73f, 1f),
                fogStart = 18f,
                fogEnd = 120f,
                sunMultiplier = 0.75f,
                useVolume = true,
                // 全屏：冷蓝灰染色 + 较重暗角 + 向下滚动雨丝；去饱和 + 冷色调色
                overlayTint = new Color(0.40f, 0.46f, 0.58f, 1f),
                overlayAlpha = 0.00f,   // 已有真雨丝层，去掉蓝色蒙版（用户要求）；阴沉感交给暗角+冷调色

                vignetteColor = new Color(0.04f, 0.05f, 0.08f, 1f),
                vignetteAlpha = 0.35f,
                useRainOverlay = true,
                rainScroll = 3.5f,
                saturation = -25f,
                contrast = 6f,
                gradeFilter = new Color(0.80f, 0.86f, 1.00f, 1f),
                gradeExposure = -0.05f,
                useGrade = true
            };
            SpawnRain(spots);
        }
        else if (type == "MOUNTAIN_FOG")
        {
            currentProfile = new EnvProfile
            {
                fogEnabled = true,
                fogColor = new Color(0.80f, 0.82f, 0.85f, 1f),
                fogStart = 15f,
                fogEnd = 85f, // 减浓：雾推远、近景留清（原 6/48 太浓糊脸）
                sunMultiplier = 0.85f,
                useVolume = true,
                // 全屏：近白染色（远近发灰）+ 轻暗角；轻度去饱和，无雨丝
                overlayTint = new Color(0.86f, 0.88f, 0.90f, 1f),
                overlayAlpha = 0.15f,   // 减浓：白雾霭叠加从 0.30 降一半
                vignetteColor = new Color(0.70f, 0.72f, 0.75f, 1f),
                vignetteAlpha = 0.12f,
                useRainOverlay = false,
                rainScroll = 0f,
                saturation = -15f,
                contrast = -3f,
                gradeFilter = new Color(0.96f, 0.97f, 0.99f, 1f),
                gradeExposure = 0.03f,
                useGrade = true
            };
            SpawnGroundFog(spots);
        }
        else // HOT
        {
            currentProfile = new EnvProfile
            {
                fogEnabled = false,
                sunMultiplier = 1f,
                useVolume = true,
                // 全屏：暖橙边缘暗角（中心留干净，别糊红蓝队色）+ 轻暖染色；暖调 + 提曝光，无雨丝
                overlayTint = new Color(1.00f, 0.72f, 0.42f, 1f),
                overlayAlpha = 0.00f,                               // 2026-06-25 用户：酷暑别"蒙一层"→去掉全屏暖染色蒙版、保持清晰一目了然
                vignetteColor = new Color(1.00f, 0.66f, 0.30f, 1f),
                vignetteAlpha = 0.08f,                              // 暗角减半 0.18→0.08：只留极轻边缘暖意、不糊画面
                useRainOverlay = false,
                rainScroll = 0f,
                saturation = 3f,
                contrast = 4f,
                gradeFilter = new Color(1.00f, 0.93f, 0.82f, 1f),
                gradeExposure = 0.22f,
                useGrade = true
            };
            EnsureHotVolume();
            SpawnHeatShimmer(heatDefaultPosition);
        }
        envDirty = true;
    }

    void RetireActiveParticles()
    {
        for (int i = 0; i < activeParticles.Count; i++)
        {
            ParticleSystem system = activeParticles[i];
            if (system == null) continue;
            system.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            Destroy(system.gameObject, 12f);
        }
        activeParticles.Clear();
        heatShimmer = null;
    }

    // ---------------- 区域定位 ----------------

    List<RegionSpot> CollectRegionSpots(string type, string region)
    {
        // 全图暴雨：region=ALL 的 HEAVY_RAIN 必须铺满整张地图。直接按地图包围盒生成一个大发射器，
        // 不走「天气锚点/站点 → 合并包围盒」那条路径——一旦场景里存在局部天气锚点，那条路径会把雨幕
        // 收缩到一隅，正是“全图暴雨没覆盖全图”的根因。
        if (type == "HEAVY_RAIN" && region == "ALL")
            return new List<RegionSpot> { BuildFullMapSpot() };

        List<RegionSpot> spots = new List<RegionSpot>();

        MapEnvironmentAnchor[] anchors = FindObjectsByType<MapEnvironmentAnchor>(FindObjectsSortMode.None);
        if (anchors != null)
        {
            foreach (MapEnvironmentAnchor anchor in anchors)
            {
                if (anchor == null || anchor.anchorType != EnvironmentAnchorType.Weather) continue;
                if (!AnchorMatchesWeather(anchor.regionId, type, region)) continue;
                spots.Add(new RegionSpot
                {
                    position = anchor.transform.position,
                    radius = Mathf.Max(2f, anchor.radius)
                });
            }
        }

        if (spots.Count == 0) AddStationFallback(spots, region);

        if (spots.Count == 0)
        {
            // 站点也缺失：用全部站点锚点兜底，再不行就给地图原点一个大范围。
            MapStationAnchor[] stations = FindObjectsByType<MapStationAnchor>(FindObjectsSortMode.None);
            if (stations != null)
            {
                foreach (MapStationAnchor station in stations)
                {
                    if (station == null) continue;
                    spots.Add(new RegionSpot { position = station.transform.position, radius = 7f });
                }
            }
            if (spots.Count == 0)
                spots.Add(new RegionSpot { position = Vector3.zero, radius = 26f });
        }

        if ((region == "ALL" && spots.Count > 1) || spots.Count > MaxEmitterSpots)
            spots = MergeIntoBoundingSpot(spots);
        return spots;
    }

    /// <summary>整张地图的雨幕覆盖点：中心取世界原点（GridToWorld 让地图以原点为中心：X∈[-40,40], Z∈[-30,30]），
    /// 半径按高程数据的网格尺寸推导并留余量，保证四角也落在雨盒内。无高程数据时按默认 80x60 兜底。</summary>
    RegionSpot BuildFullMapSpot()
    {
        MapElevationData elevation = FindFirstObjectByType<MapElevationData>();
        float halfX = 40f, halfZ = 30f;
        if (elevation != null)
        {
            halfX = elevation.gridWidth * elevation.cellSize * 0.5f;
            halfZ = elevation.gridHeight * elevation.cellSize * 0.5f;
        }
        // SpawnRain 用 side = radius*2 作雨盒边长（钳到 110），radius 取最大半边长加余量即可铺满全图。
        float radius = Mathf.Max(halfX, halfZ) + 8f;
        return new RegionSpot { position = Vector3.zero, radius = radius };
    }

    void AddStationFallback(List<RegionSpot> spots, string region)
    {
        if (player == null) return;
        LycheeReplayFrame frame = player.CurrentFrame ?? player.StartFrameData;
        if (region == "WATER")
        {
            AddFrameNodeSpots(spots, frame, node => IsWaterNodeType(node.nodeType), 9f);
            if (spots.Count == 0)
            {
                AddStationSpot(spots, "S04", 9f);
                AddStationSpot(spots, "S05", 9f);
            }
        }
        else if (region == "MOUNTAIN")
        {
            AddFrameNodeSpots(spots, frame, node => IsMountainNodeType(node.nodeType), 8f);
            if (spots.Count == 0)
            {
                AddStationSpot(spots, "S06", 8f);
                AddStationSpot(spots, "S08", 8f);
            }
        }
        else
        {
            AddFrameNodeSpots(spots, frame, node => true, 7f);
            if (spots.Count == 0)
            {
                MapStationAnchor[] stations = FindObjectsByType<MapStationAnchor>(FindObjectsSortMode.None);
                if (stations != null)
                {
                    foreach (MapStationAnchor station in stations)
                    {
                        if (station == null || string.IsNullOrEmpty(station.stationId)) continue;
                        AddStationSpot(spots, station.stationId, 7f);
                    }
                }
            }
        }
    }

    void AddFrameNodeSpots(List<RegionSpot> spots, LycheeReplayFrame frame,
        Func<LycheeReplayNodeState, bool> predicate, float radius)
    {
        if (frame == null || frame.nodes == null || predicate == null) return;
        foreach (LycheeReplayNodeState node in frame.nodes)
        {
            if (node == null || string.IsNullOrEmpty(node.nodeId) || !predicate(node)) continue;
            AddStationSpot(spots, node.nodeId, radius);
        }
    }

    void AddStationSpot(List<RegionSpot> spots, string nodeId, float radius)
    {
        if (player == null) return;
        if (player.TryGetStationPosition(nodeId, out Vector3 position, out string _))
            spots.Add(new RegionSpot { position = position, radius = radius });
    }

    static bool IsWaterNodeType(string nodeType)
    {
        string value = NormalizeToken(nodeType);
        return value.Contains("WATER") || value.Contains("DOCK") || value.Contains("PORT") || value.Contains("RIVER");
    }

    static bool IsMountainNodeType(string nodeType)
    {
        string value = NormalizeToken(nodeType);
        return value.Contains("MOUNTAIN") || value.Contains("HILL") || value.Contains("RIDGE");
    }

    static bool AnchorMatchesWeather(string regionId, string type, string region)
    {
        if (string.IsNullOrEmpty(regionId)) return false;
        string id = regionId.Trim().ToUpperInvariant();
        string normalizedRegion = string.IsNullOrEmpty(region) ? "ALL" : region;

        if (normalizedRegion == "WATER")
            return id.Contains("WATER") || id.Contains("RAIN") || id.Contains("RIVER") || id.Contains("JIANGNAN");
        if (normalizedRegion == "MOUNTAIN")
            return id.Contains("MOUNTAIN") || id.Contains("FOG") || id.Contains("QINLING");

        // ALL / 未知区域：接受 ALL/GLOBAL 标记，或与天气类型同名/同义的锚点。
        if (id.Contains("ALL") || id.Contains("GLOBAL")) return true;
        if (type == "HOT") return id.Contains("HOT") || id.Contains("HEAT") || id.Contains("LINGNAN");
        return !string.IsNullOrEmpty(type) && id.Contains(type);
    }

    static List<RegionSpot> MergeIntoBoundingSpot(List<RegionSpot> spots)
    {
        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        foreach (RegionSpot spot in spots)
        {
            min = Vector3.Min(min, spot.position);
            max = Vector3.Max(max, spot.position);
        }
        Vector3 center = (min + max) * 0.5f;
        float radius = Mathf.Max(18f, Mathf.Max(max.x - min.x, max.z - min.z) * 0.5f + 8f);
        return new List<RegionSpot> { new RegionSpot { position = center, radius = radius } };
    }

    // ---------------- 粒子构建 ----------------

    void EnsureFxRoot()
    {
        if (fxRoot != null) return;
        GameObject rootObject = new GameObject("WeatherFx_Root");
        rootObject.transform.SetParent(transform, false);
        fxRoot = rootObject.transform;
    }

    void SpawnRain(List<RegionSpot> spots)
    {
        if (rainMaterial == null || spots == null || spots.Count == 0) return;
        EnsureFxRoot();
        int budgetPerEmitter = Mathf.Max(180, RainParticleBudget / spots.Count);
        float lifetime = RainEmitterHeight / RainFallSpeed + 0.25f;

        for (int i = 0; i < spots.Count; i++)
        {
            RegionSpot spot = spots[i];
            float side = Mathf.Clamp(spot.radius * 2f, 8f, 110f);

            GameObject emitterObject = new GameObject("WeatherFx_Rain_" + i);
            emitterObject.transform.SetParent(fxRoot, false);
            emitterObject.transform.position = spot.position + Vector3.up * RainEmitterHeight;
            emitterObject.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // 本地 +Z 朝下

            ParticleSystem system = emitterObject.AddComponent<ParticleSystem>();
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = system.main;
            main.loop = true;
            main.prewarm = true;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.startLifetime = lifetime;
            main.startSpeed = RainFallSpeed;
            main.startSize = new ParticleSystem.MinMaxCurve(0.035f, 0.075f);
            main.startColor = new Color(0.74f, 0.81f, 0.93f, 0.34f);
            main.maxParticles = budgetPerEmitter + 24;
            main.gravityModifier = 0f;

            ParticleSystem.EmissionModule emission = system.emission;
            emission.rateOverTime = budgetPerEmitter / lifetime;

            ParticleSystem.ShapeModule shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(side, side, 0.6f);

            ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.renderMode = ParticleSystemRenderMode.Stretch;
                renderer.lengthScale = 6f;
                renderer.velocityScale = 0f;
                renderer.cameraVelocityScale = 0f;
                renderer.sharedMaterial = rainMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            system.Play();
            activeParticles.Add(system);
        }
    }

    void SpawnGroundFog(List<RegionSpot> spots)
    {
        if (fogMaterial == null || spots == null || spots.Count == 0) return;
        EnsureFxRoot();
        for (int i = 0; i < spots.Count; i++)
        {
            RegionSpot spot = spots[i];
            float side = Mathf.Clamp(spot.radius * 2f, 6f, 80f);

            GameObject emitterObject = new GameObject("WeatherFx_Fog_" + i);
            emitterObject.transform.SetParent(fxRoot, false);
            emitterObject.transform.position = spot.position + Vector3.up * 0.8f;

            ParticleSystem system = emitterObject.AddComponent<ParticleSystem>();
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = system.main;
            main.loop = true;
            main.prewarm = true;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.startLifetime = new ParticleSystem.MinMaxCurve(5f, 8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.08f, 0.28f);
            main.startSize = new ParticleSystem.MinMaxCurve(2.4f, 5.4f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new Color(0.84f, 0.86f, 0.88f, 0.22f);
            main.maxParticles = 32;
            main.gravityModifier = 0f;

            ParticleSystem.EmissionModule emission = system.emission;
            emission.rateOverTime = 3.0f;

            ParticleSystem.ShapeModule shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(side, 1.6f, side);
            shape.randomDirectionAmount = 1f;

            ParticleSystem.ColorOverLifetimeModule colorOverLifetime = system.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(BuildFadeGradient(0.2f, 0.78f));

            ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = system.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.82f, 1f, 1.25f));

            ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
                renderer.sharedMaterial = fogMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            system.Play();
            activeParticles.Add(system);
        }
    }

    void SpawnHeatShimmer(Vector3 position)
    {
        if (heatMaterial == null) return;
        EnsureFxRoot();
        GameObject emitterObject = new GameObject("WeatherFx_HeatShimmer");
        emitterObject.transform.SetParent(fxRoot, false);
        emitterObject.transform.position = position + Vector3.up * 0.2f;
        emitterObject.transform.rotation = Quaternion.Euler(-90f, 0f, 0f); // 本地 +Z 朝上

        ParticleSystem system = emitterObject.AddComponent<ParticleSystem>();
        system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        ParticleSystem.MainModule main = system.main;
        main.loop = true;
        main.prewarm = true;
        main.simulationSpace = ParticleSystemSimulationSpace.World; // 发射器跟随事件焦点移动
        main.startLifetime = new ParticleSystem.MinMaxCurve(1.25f, 1.9f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.28f, 0.55f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.55f, 1.05f);
        main.startColor = new Color(1f, 0.9f, 0.78f, 0.10f);
        main.maxParticles = 24;
        main.gravityModifier = 0f;

        ParticleSystem.EmissionModule emission = system.emission;
        emission.rateOverTime = 7f;

        ParticleSystem.ShapeModule shape = system.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(6f, 6f, 0.4f);

        ParticleSystem.ColorOverLifetimeModule colorOverLifetime = system.colorOverLifetime;
        colorOverLifetime.enabled = true;
        colorOverLifetime.color = new ParticleSystem.MinMaxGradient(BuildFadeGradient(0.18f, 0.6f));

        ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = system.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 1.8f));

        ParticleSystem.NoiseModule noise = system.noise;
        noise.enabled = true;
        noise.strength = 0.35f;
        noise.frequency = 0.6f;

        ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
        if (renderer != null)
        {
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = heatMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        system.Play();
        activeParticles.Add(system);
        heatShimmer = system;
    }

    void UpdateHeatShimmer()
    {
        if (heatShimmer == null || currentType != "HOT") return;
        Vector3 target = heatDefaultPosition;
        bool found = player != null && player.TryGetEventFocus(out target);
        if (!found && player != null && player.TryGetConvoyFraming(out Vector3 center, out float _))
        {
            target = center;
            found = true;
        }
        if (!found) target = heatDefaultPosition;
        Vector3 desired = target + Vector3.up * 0.2f;
        heatShimmer.transform.position = Vector3.Lerp(
            heatShimmer.transform.position,
            desired,
            1f - Mathf.Exp(-2.5f * Time.deltaTime));
    }

    static Gradient BuildFadeGradient(float fadeInEnd, float fadeOutStart)
    {
        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(Color.white, 0f),
                new GradientColorKey(Color.white, 1f)
            },
            new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(1f, Mathf.Clamp01(fadeInEnd)),
                new GradientAlphaKey(1f, Mathf.Clamp01(fadeOutStart)),
                new GradientAlphaKey(0f, 1f)
            });
        return gradient;
    }

    // ---------------- 全局环境（雾 / 平行光 / Volume） ----------------

    void CaptureOriginalEnvironment()
    {
        if (envCaptured) return;
        origFog = RenderSettings.fog;
        origFogColor = RenderSettings.fogColor;
        origFogMode = RenderSettings.fogMode;
        origFogStart = RenderSettings.fogStartDistance;
        origFogEnd = RenderSettings.fogEndDistance;
        origFogDensity = RenderSettings.fogDensity;
        envCaptured = true;
    }

    void EnsureSun()
    {
        if (sun != null) return;
        Light[] lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
        if (lights == null) return;
        foreach (Light candidate in lights)
        {
            if (candidate == null || candidate.type != LightType.Directional) continue;
            sun = candidate;
            if (!sunCaptured)
            {
                origSunIntensity = candidate.intensity;
                sunCaptured = true;
            }
            break;
        }
    }

    // 运行时调色 Volume：原本只服务 HOT，现推广到三种天气（暴雨去饱和冷调 / 山雾去饱和 / 酷暑暖调提曝光）。
    // 每帧由 ApplyEnvironmentBlend 从 currentProfile 写入 override 值并把 weight 挂到 envBlend 上。
    void EnsureHotVolume()
    {
        if (hotVolume != null) return;
        try
        {
            volumeObject = new GameObject("WeatherFx_GradeVolume");
            volumeObject.transform.SetParent(transform, false);
            Volume volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 50f;
            volume.weight = 0f;
            volumeProfileInstance = ScriptableObject.CreateInstance<VolumeProfile>();
            ColorAdjustments colorAdjustments = volumeProfileInstance.Add<ColorAdjustments>(false);
            // 初值取酷暑（保留旧默认观感）；运行时各天气会按 currentProfile 覆盖这些参数。
            colorAdjustments.postExposure.Override(0.18f);
            colorAdjustments.contrast.Override(0f);
            colorAdjustments.colorFilter.Override(new Color(1f, 0.93f, 0.82f, 1f));
            colorAdjustments.saturation.Override(0f);
            volume.sharedProfile = volumeProfileInstance;
            hotVolume = volume;
            gradeAdjustments = colorAdjustments;
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[天气特效] 创建天气调色 Volume 失败：" + exception.Message, this);
            if (volumeObject != null) Destroy(volumeObject);
            volumeObject = null;
            hotVolume = null;
            gradeAdjustments = null;
        }
    }

    void ApplyEnvironmentBlend()
    {
        if (!envCaptured) return;
        if (currentProfile == null)
        {
            if (envDirty && envBlend <= 0.0001f)
            {
                RestoreOriginalEnvironment();
                envDirty = false;
            }
            return;
        }

        envDirty = true;
        EnsureSun();
        EnvProfile profile = currentProfile;

        if (profile.fogEnabled)
        {
            float fromStart;
            float fromEnd;
            if (origFog && origFogMode == FogMode.Linear)
            {
                fromStart = origFogStart;
                fromEnd = origFogEnd;
            }
            else
            {
                // 原本无雾（或非线性雾）：用极远距离近似“无雾”，避免开启瞬间跳变。
                fromStart = 140f;
                fromEnd = 480f;
            }
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = Color.Lerp(origFogColor, profile.fogColor, envBlend);
            RenderSettings.fogStartDistance = Mathf.Lerp(fromStart, profile.fogStart, envBlend);
            RenderSettings.fogEndDistance = Mathf.Lerp(fromEnd, profile.fogEnd, envBlend);
        }
        else
        {
            RenderSettings.fog = origFog;
            RenderSettings.fogMode = origFogMode;
            RenderSettings.fogColor = origFogColor;
            RenderSettings.fogStartDistance = origFogStart;
            RenderSettings.fogEndDistance = origFogEnd;
            RenderSettings.fogDensity = origFogDensity;
        }

        if (sun != null && sunCaptured)
            sun.intensity = origSunIntensity * Mathf.Lerp(1f, profile.sunMultiplier, envBlend);

        // 调色 Volume：三种天气都用。weight 挂在 envBlend 上随淡入淡出，override 参数从当前 profile 写入。
        if (profile.useGrade)
        {
            if (hotVolume == null) EnsureHotVolume();
            if (gradeAdjustments != null)
            {
                gradeAdjustments.postExposure.value = profile.gradeExposure;
                gradeAdjustments.contrast.value = profile.contrast;
                gradeAdjustments.colorFilter.value = profile.gradeFilter;
                gradeAdjustments.saturation.value = profile.saturation;
            }
            if (hotVolume != null) hotVolume.weight = envBlend;
        }
        else if (hotVolume != null)
        {
            hotVolume.weight = 0f;
        }
    }

    void RestoreOriginalEnvironment()
    {
        if (!envCaptured) return;
        RenderSettings.fog = origFog;
        RenderSettings.fogMode = origFogMode;
        RenderSettings.fogColor = origFogColor;
        RenderSettings.fogStartDistance = origFogStart;
        RenderSettings.fogEndDistance = origFogEnd;
        RenderSettings.fogDensity = origFogDensity;
        if (sun != null && sunCaptured) sun.intensity = origSunIntensity;
        if (hotVolume != null) hotVolume.weight = 0f;
        // 全屏叠加层三张 Image alpha 归零（雨丝层一并停滚）
        if (overlayTintImg != null) overlayTintImg.color = WithAlpha(overlayTintImg.color, 0f);
        if (overlayVignetteImg != null) overlayVignetteImg.color = WithAlpha(overlayVignetteImg.color, 0f);
        if (overlayRainImg != null) overlayRainImg.color = WithAlpha(overlayRainImg.color, 0f);
    }

    static Color WithAlpha(Color color, float alpha)
    {
        color.a = alpha;
        return color;
    }

    // ---------------- 全屏氛围叠加层（ScreenSpaceOverlay） ----------------

    // 镜像 HUD 的画布构建（ScreenSpaceOverlay + CanvasScaler + 全屏拉伸 Image），sortingOrder=125 压在 HUD(130) 之下。
    // 三张全屏 quad：tint 染色 / vignette 暗角 / rain 滚动雨丝。全部 raycastTarget=false、静止 alpha=0。
    void EnsureOverlay()
    {
        if (weatherCanvas != null) return;

        if (whiteTex == null) whiteTex = BuildWhiteTexture();
        if (vignetteTex == null) vignetteTex = BuildVignetteTexture(64);
        if (rainScrollTex == null)
        {
            rainScrollTex = BuildRainStreakTextureRepeat();
            rainScrollTexOwned = true;
        }

        GameObject canvasObject = new GameObject(
            "WeatherFxOverlay_Canvas",
            typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        canvasObject.transform.SetParent(transform, false);
        RectTransform overlayRoot = canvasObject.GetComponent<RectTransform>();

        weatherCanvas = canvasObject.GetComponent<Canvas>();
        weatherCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        weatherCanvas.sortingOrder = 125; // HUD 是 130，必须压在它下面

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        // tint 染色（1x1 白纹按天气重新着色），底层
        RectTransform tintRt = StretchRect("WeatherFxTint", overlayRoot);
        overlayTintImg = tintRt.gameObject.AddComponent<Image>();
        overlayTintImg.sprite = Sprite.Create(whiteTex, new Rect(0f, 0f, whiteTex.width, whiteTex.height), new Vector2(0.5f, 0.5f), 100f);
        overlayTintImg.color = new Color(1f, 1f, 1f, 0f);
        overlayTintImg.raycastTarget = false;

        // rain 滚动雨丝（RawImage 才能直接驱动 uvRect 平铺/滚动），中层
        RectTransform rainRt = StretchRect("WeatherFxRain", overlayRoot);
        overlayRainImg = rainRt.gameObject.AddComponent<RawImage>();
        overlayRainImg.texture = rainScrollTex;
        overlayRainImg.uvRect = rainUv;
        overlayRainImg.color = new Color(0.92f, 0.96f, 1.00f, 0f);
        overlayRainImg.raycastTarget = false;

        // vignette 暗角（径向四角加深、中心透明），顶层
        RectTransform vigRt = StretchRect("WeatherFxVignette", overlayRoot);
        overlayVignetteImg = vigRt.gameObject.AddComponent<Image>();
        overlayVignetteImg.sprite = Sprite.Create(vignetteTex, new Rect(0f, 0f, vignetteTex.width, vignetteTex.height), new Vector2(0.5f, 0.5f), 100f);
        overlayVignetteImg.color = new Color(0f, 0f, 0f, 0f);
        overlayVignetteImg.raycastTarget = false;
    }

    // 四角锚满父级的全屏拉伸矩形（镜像 HUD 的同名帮手；本系统私有，避免跨类引用 HUD 的私有方法）。
    static RectTransform StretchRect(string name, Transform parent)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        rt.localScale = Vector3.one;
        return rt;
    }

    // 每帧把三张 Image 的 alpha lerp 到 目标×envBlend；雨丝层按 rainScroll×deltaTime 推进 UV.y（向下滚）。
    void ApplyOverlayBlend()
    {
        if (weatherCanvas == null) return;
        EnvProfile profile = currentProfile;
        float blend = Mathf.Clamp01(envBlend);

        float tintTarget = profile != null ? profile.overlayAlpha * blend : 0f;
        float vigTarget = profile != null ? profile.vignetteAlpha * blend : 0f;
        bool rainOn = profile != null && profile.useRainOverlay;
        float rainTarget = rainOn ? 0.42f * blend : 0f;

        float k = 1f - Mathf.Exp(-8f * Time.deltaTime); // 平滑跟随，避免切换瞬跳

        if (overlayTintImg != null)
        {
            Color c = profile != null ? profile.overlayTint : Color.white;
            c.a = Mathf.Lerp(overlayTintImg.color.a, tintTarget, k);
            overlayTintImg.color = c;
        }

        if (overlayVignetteImg != null)
        {
            Color c = profile != null ? profile.vignetteColor : Color.black;
            c.a = Mathf.Lerp(overlayVignetteImg.color.a, vigTarget, k);
            overlayVignetteImg.color = c;
        }

        if (overlayRainImg != null)
        {
            if (rainOn)
            {
                // UV.y 减小 = 纹理向上 = 画面雨丝向下落
                rainUv.y -= profile.rainScroll * Time.deltaTime;
                if (rainUv.y < -1024f) rainUv.y += 1024f; // 防长跑累积溢出
                overlayRainImg.uvRect = rainUv;
            }
            Color c = overlayRainImg.color;
            c.a = Mathf.Lerp(c.a, rainTarget, k);
            overlayRainImg.color = c;
        }
    }

    // ---------------- 材质 / 贴图 ----------------

    void EnsureMaterials()
    {
        if (materialsBuilt) return;
        materialsBuilt = true;

        Texture2D themedRain = LycheeWeatherLoadThemeTexture("weather_rain.png");
        if (themedRain != null)
        {
            rainTexture = themedRain;
            rainTextureOwned = true;
        }
        else
        {
            rainTexture = BuildRainStreakTexture();
            rainTextureOwned = true;
        }

        Texture2D themedPuff = LycheeWeatherLoadThemeTexture("weather_fog.png");
        if (themedPuff != null)
        {
            puffTexture = themedPuff;
            puffTextureOwned = true;
        }
        else
        {
            puffTexture = BuildSoftCircleTexture(64);
            puffTextureOwned = true;
        }

        rainMaterial = CreateLycheeParticleMaterial(rainTexture, new Color(0.78f, 0.85f, 0.96f, 1f));
        fogMaterial = CreateLycheeParticleMaterial(puffTexture, Color.white);
        Texture2D themedHeat = LycheeWeatherLoadThemeTexture("weather_heat.png");
        heatMaterial = CreateLycheeParticleMaterial(themedHeat != null ? themedHeat : puffTexture, new Color(1f, 0.95f, 0.86f, 1f));
    }

    static Texture2D LycheeWeatherLoadThemeTexture(string fileName)
    {
        try
        {
            byte[] bytes = LycheeThemeCache.LoadBytes(fileName);
            if (bytes == null || bytes.Length == 0) return null;
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(bytes))
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            return texture;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static Material CreateLycheeParticleMaterial(Texture2D texture, Color baseColor)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) return null; // 找不到 URP 粒子 shader：放弃粒子表现
        Material material = new Material(shader);
        try
        {
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f); // Transparent
            if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f);     // Alpha
            material.SetOverrideTag("RenderType", "Transparent");
            if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
            material.DisableKeyword("_ALPHATEST_ON");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = 3000;
            if (texture != null && material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", baseColor);
            else if (material.HasProperty("_Color")) material.SetColor("_Color", baseColor);
        }
        catch (Exception)
        {
            // 属性缺失等异常不致命，保留默认材质设置。
        }
        return material;
    }

    static Texture2D BuildRainStreakTexture()
    {
        const int width = 8;
        const int height = 32;
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        for (int y = 0; y < height; y++)
        {
            float v = y / (float)(height - 1);
            float vertical = Mathf.Sin(v * Mathf.PI); // 两端淡出
            for (int x = 0; x < width; x++)
            {
                float u = x / (float)(width - 1);
                float horizontal = 1f - Mathf.Abs(u - 0.5f) * 2f;
                float alpha = Mathf.Clamp01(horizontal * horizontal * vertical);
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }
        texture.Apply(false, false);
        return texture;
    }

    // 滚动雨丝层专用：分段散布的【细】雨滴短线（纵向留间断 → 滚动有下落动感；纹理加大+1px 主线，避免平铺放大后过粗），白色，Repeat 平铺。
    static Texture2D BuildRainStreakTextureRepeat()
    {
        const int width = 128;
        const int height = 256;
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear
        };
        Color[] px = new Color[width * height];
        for (int i = 0; i < px.Length; i++) px[i] = new Color(1f, 1f, 1f, 0f);
        System.Random rng = new System.Random(20260623);
        const int streaks = 22;
        for (int s = 0; s < streaks; s++)
        {
            int cx = rng.Next(width);
            int startY = rng.Next(height);
            int len = 16 + rng.Next(28);                 // 16–43px 细短线
            float peak = 0.55f + (float)rng.NextDouble() * 0.45f;
            for (int d = 0; d < len; d++)
            {
                int y = (startY + d) % height;           // 纵向环绕，平铺无缝
                float aFade = Mathf.Sin((d / (float)Mathf.Max(1, len - 1)) * Mathf.PI); // 两端淡出（雨滴拖尾）
                float a = peak * aFade;
                BlendRainPixel(px, width, cx, y, a);             // 1px 主线（细）
                BlendRainPixel(px, width, cx + 1, y, a * 0.30f); // 右侧极淡抗锯齿，不加粗
            }
        }
        texture.SetPixels(px);
        texture.Apply(false, false);
        return texture;
    }

    static void BlendRainPixel(Color[] px, int w, int x, int y, float a)
    {
        x = ((x % w) + w) % w;
        int idx = y * w + x;
        if (a > px[idx].a) px[idx] = new Color(1f, 1f, 1f, Mathf.Clamp01(a));
    }

    // 1x1 纯白：作为 tint 染色图，运行时整张按天气颜色重新着色。
    static Texture2D BuildWhiteTexture()
    {
        Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        texture.SetPixel(0, 0, Color.white);
        texture.Apply(false, false);
        return texture;
    }

    // 径向暗角：中心 alpha=0、四角 alpha 渐增（RGB 留白，运行时按天气重新着色）。
    static Texture2D BuildVignetteTexture(int size)
    {
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x / (float)(size - 1) * 2f - 1f;
                float dy = y / (float)(size - 1) * 2f - 1f;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                // 中心 0.40 半径内全透明，向四角（distance≈1.414）平滑加深到 a=1
                float alpha = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.40f, 1.414f, distance));
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }
        texture.Apply(false, false);
        return texture;
    }

    static Texture2D BuildSoftCircleTexture(int size)
    {
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        float half = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x - half) / half;
                float dy = (y - half) / half;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = Mathf.Clamp01(1f - distance);
                alpha *= alpha;
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }
        texture.Apply(false, false);
        return texture;
    }
}
