using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

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
    const int RainParticleBudget = 2400; // 雨幕总粒子预算（>2000）
    const float RainEmitterHeight = 13f; // 雨幕发射盒高度
    const float RainFallSpeed = 14f;     // 雨滴下落速度 m/s
    const int MaxEmitterSpots = 6;       // 单种天气最多发射器数量，超出则合并为包围盒

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
        if (volumeObject != null) Destroy(volumeObject);
        if (volumeProfileInstance != null) Destroy(volumeProfileInstance);
        if (rainMaterial != null) Destroy(rainMaterial);
        if (fogMaterial != null) Destroy(fogMaterial);
        if (heatMaterial != null) Destroy(heatMaterial);
        if (rainTexture != null && rainTextureOwned) Destroy(rainTexture);
        if (puffTexture != null && puffTextureOwned) Destroy(puffTexture);
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
        List<RegionSpot> spots = CollectRegionSpots(type, region);
        heatDefaultPosition = spots.Count > 0 ? spots[0].position : Vector3.zero;

        if (type == "HEAVY_RAIN")
        {
            currentProfile = new EnvProfile
            {
                fogEnabled = true,
                fogColor = new Color(0.58f, 0.64f, 0.73f, 1f),
                fogStart = 30f,
                fogEnd = 230f,
                sunMultiplier = 0.75f,
                useVolume = false
            };
            SpawnRain(spots);
        }
        else if (type == "MOUNTAIN_FOG")
        {
            currentProfile = new EnvProfile
            {
                fogEnabled = true,
                fogColor = new Color(0.80f, 0.82f, 0.85f, 1f),
                fogStart = 8f,
                fogEnd = 95f,
                sunMultiplier = 0.85f,
                useVolume = false
            };
            SpawnGroundFog(spots);
        }
        else // HOT
        {
            currentProfile = new EnvProfile
            {
                fogEnabled = false,
                sunMultiplier = 1f,
                useVolume = true
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

    void AddStationFallback(List<RegionSpot> spots, string region)
    {
        if (player == null) return;
        if (region == "WATER")
        {
            AddStationSpot(spots, "S04", 9f);
            AddStationSpot(spots, "S05", 9f);
        }
        else if (region == "MOUNTAIN")
        {
            AddStationSpot(spots, "S06", 8f);
            AddStationSpot(spots, "S08", 8f);
        }
        else
        {
            for (int i = 1; i <= 15; i++)
                AddStationSpot(spots, "S" + i.ToString("00"), 7f);
        }
    }

    void AddStationSpot(List<RegionSpot> spots, string nodeId, float radius)
    {
        if (player == null) return;
        if (player.TryGetStationPosition(nodeId, out Vector3 position, out string _))
            spots.Add(new RegionSpot { position = position, radius = radius });
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
        int budgetPerEmitter = Mathf.Max(350, RainParticleBudget / spots.Count);
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
            main.startColor = new Color(0.74f, 0.81f, 0.93f, 0.5f);
            main.maxParticles = budgetPerEmitter + 64;
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
            main.startLifetime = new ParticleSystem.MinMaxCurve(7f, 11f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.1f, 0.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(3.5f, 8f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new Color(0.84f, 0.86f, 0.88f, 0.3f);
            main.maxParticles = 64;
            main.gravityModifier = 0f;

            ParticleSystem.EmissionModule emission = system.emission;
            emission.rateOverTime = 5.5f;

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
        main.startLifetime = new ParticleSystem.MinMaxCurve(1.8f, 2.6f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.45f, 0.9f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.9f, 1.7f);
        main.startColor = new Color(1f, 0.9f, 0.78f, 0.16f);
        main.maxParticles = 60;
        main.gravityModifier = 0f;

        ParticleSystem.EmissionModule emission = system.emission;
        emission.rateOverTime = 18f;

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

    void EnsureHotVolume()
    {
        if (hotVolume != null) return;
        try
        {
            volumeObject = new GameObject("WeatherFx_HotVolume");
            volumeObject.transform.SetParent(transform, false);
            Volume volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 50f;
            volume.weight = 0f;
            volumeProfileInstance = ScriptableObject.CreateInstance<VolumeProfile>();
            ColorAdjustments colorAdjustments = volumeProfileInstance.Add<ColorAdjustments>(false);
            colorAdjustments.postExposure.Override(0.25f);
            colorAdjustments.colorFilter.Override(new Color(1f, 0.93f, 0.8f, 1f));
            volume.sharedProfile = volumeProfileInstance;
            hotVolume = volume;
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[天气特效] 创建酷热后处理 Volume 失败：" + exception.Message, this);
            if (volumeObject != null) Destroy(volumeObject);
            volumeObject = null;
            hotVolume = null;
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
        if (hotVolume != null)
            hotVolume.weight = profile.useVolume ? envBlend : 0f;
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
            string path = System.IO.Path.Combine(Application.streamingAssetsPath, "UITheme", fileName);
            if (!System.IO.File.Exists(path)) return null;
            byte[] bytes = System.IO.File.ReadAllBytes(path);
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
