using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 透视运镜 + 天空盒光照氛围导演：
/// 1. 运行时自举到 LycheeReplayPlayer 所在物体，配置程序化天空盒、暖色平行光、三色环境光与线性雾；
/// 2. 确保主相机挂载 LycheeReplayCameraController 并开启透视运镜，打开 URP 后处理；
/// 3. 订阅回放事件驱动镜头震动，RUSH 阶段把平行光推向更暖更亮（2 秒插值），氛围参数在 OnDestroy 恢复原值。
/// </summary>
public class LycheeCinematicDirector : MonoBehaviour
{
    public static bool Disabled;

    const float RushBlendSeconds = 2f;
    const float CinematicLightIntensity = 1.55f;
    const float RushIntensityMultiplier = 1.22f;
    static readonly Color CinematicLightColor = new Color(1f, 0.95f, 0.86f);
    static readonly Color RushLightColor = new Color(1f, 0.86f, 0.7f);

    Camera mainCamera;
    LycheeReplayCameraController cameraController;
    UniversalAdditionalCameraData cameraData;
    Light directionalLight;

    bool atmosphereConfigured;
    bool eventsSubscribed;
    bool cameraConfigured;

    Material createdSkybox;
    Material originalSkybox;
    bool skyboxReplaced;

    Quaternion originalLightRotation;
    Color originalLightColor;
    float originalLightIntensity;
    LightShadows originalLightShadows;
    float originalLightShadowStrength;
    bool lightModified;

    AmbientMode originalAmbientMode;
    Color originalAmbientSky;
    Color originalAmbientEquator;
    Color originalAmbientGround;
    Color originalAmbientLight;
    bool ambientRecorded;

    bool originalFog;
    FogMode originalFogMode;
    float originalFogStart;
    float originalFogEnd;
    float originalFogDensity;
    Color originalFogColor;
    bool fogRecorded;

    bool originalPostProcessing;
    bool postProcessingRecorded;
    CameraClearFlags originalClearFlags;
    bool clearFlagsRecorded;

    bool rushActive;
    float rushBlend;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Disabled) return;
        LycheeReplayPlayer player = FindFirstObjectByType<LycheeReplayPlayer>();
        if (player == null) return;
        if (player.GetComponent<LycheeCinematicDirector>() != null) return;
        player.gameObject.AddComponent<LycheeCinematicDirector>();
    }

    void Awake()
    {
        if (Disabled)
        {
            enabled = false;
            return;
        }
        ConfigureAtmosphere();
        EnsureCameraSetup();
        SubscribeEvents();
    }

    void Update()
    {
        if (Disabled) return;
        if (!cameraConfigured || cameraController == null) EnsureCameraSetup();
        UpdateRushLighting();
    }

    void OnDestroy()
    {
        UnsubscribeEvents();
        RestoreAtmosphere();
    }

    // ---------- 氛围配置 ----------

    void ConfigureAtmosphere()
    {
        if (atmosphereConfigured) return;
        atmosphereConfigured = true;
        try
        {
            ConfigureSkybox();
            ConfigureDirectionalLight();
            ConfigureAmbient();
            ConfigureFog();
        }
        catch (Exception exception)
        {
            Debug.LogWarning("LycheeCinematicDirector 氛围配置失败：" + exception.Message, this);
        }
    }

    void ConfigureSkybox()
    {
        Shader skyboxShader = Shader.Find("Skybox/Procedural");
        if (skyboxShader == null) return;
        originalSkybox = RenderSettings.skybox;
        createdSkybox = new Material(skyboxShader);
        SetFloatSafe(createdSkybox, "_SunSize", 0.05f);
        SetFloatSafe(createdSkybox, "_AtmosphereThickness", 0.9f);
        SetColorSafe(createdSkybox, "_SkyTint", new Color(0.62f, 0.52f, 0.45f));
        SetColorSafe(createdSkybox, "_GroundColor", new Color(0.35f, 0.30f, 0.26f));
        SetFloatSafe(createdSkybox, "_Exposure", 1.15f);
        RenderSettings.skybox = createdSkybox;
        skyboxReplaced = true;
    }

    void ConfigureDirectionalLight()
    {
        directionalLight = FindDirectionalLight();
        if (directionalLight == null) return;
        originalLightRotation = directionalLight.transform.rotation;
        originalLightColor = directionalLight.color;
        originalLightIntensity = directionalLight.intensity;
        originalLightShadows = directionalLight.shadows;
        originalLightShadowStrength = directionalLight.shadowStrength;
        lightModified = true;
        directionalLight.transform.rotation = Quaternion.Euler(52f, -32f, 0f);
        directionalLight.color = CinematicLightColor;
        directionalLight.intensity = CinematicLightIntensity;
        directionalLight.shadows = LightShadows.Soft;
        directionalLight.shadowStrength = 0.38f;
    }

    void ConfigureAmbient()
    {
        originalAmbientMode = RenderSettings.ambientMode;
        originalAmbientSky = RenderSettings.ambientSkyColor;
        originalAmbientEquator = RenderSettings.ambientEquatorColor;
        originalAmbientGround = RenderSettings.ambientGroundColor;
        originalAmbientLight = RenderSettings.ambientLight;
        ambientRecorded = true;
        RenderSettings.ambientMode = AmbientMode.Trilight;
        // 环境光略提，给单位暗部/将军补点散射光，避免侧面发黑（配合对比度下调）
        RenderSettings.ambientSkyColor = new Color(0.75f, 0.75f, 0.79f);
        RenderSettings.ambientEquatorColor = new Color(0.63f, 0.60f, 0.55f);
        RenderSettings.ambientGroundColor = new Color(0.50f, 0.47f, 0.42f);
    }

    void ConfigureFog()
    {
        originalFog = RenderSettings.fog;
        originalFogMode = RenderSettings.fogMode;
        originalFogStart = RenderSettings.fogStartDistance;
        originalFogEnd = RenderSettings.fogEndDistance;
        originalFogDensity = RenderSettings.fogDensity;
        originalFogColor = RenderSettings.fogColor;
        fogRecorded = true;
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogStartDistance = 48f;
        RenderSettings.fogEndDistance = 170f;
        RenderSettings.fogColor = new Color(0.78f, 0.75f, 0.69f);
    }

    static Light FindDirectionalLight()
    {
        Light[] lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
        Light fallback = null;
        foreach (Light light in lights)
        {
            if (light == null || light.type != LightType.Directional) continue;
            if (light.enabled && light.gameObject.activeInHierarchy) return light;
            if (fallback == null) fallback = light;
        }
        return fallback;
    }

    // ---------- 相机配置 ----------

    void EnsureCameraSetup()
    {
        if (mainCamera == null) mainCamera = Camera.main;
        if (mainCamera == null) mainCamera = FindFirstObjectByType<Camera>();
        if (mainCamera == null) return;

        try
        {
            cameraController = mainCamera.GetComponent<LycheeReplayCameraController>();
            if (cameraController == null)
                cameraController = mainCamera.gameObject.AddComponent<LycheeReplayCameraController>();
            // 场景里序列化的控制器组件是禁用状态（m_Enabled:0，历史遗留），必须显式启用，
            // 否则透视/跟拍/拉近全部不生效。
            cameraController.enabled = true;
            ApplyCameraDefaults(cameraController);
            if (mainCamera.GetComponent<AudioListener>() == null)
                mainCamera.gameObject.AddComponent<AudioListener>();
            if (!cameraConfigured)
            {
                cameraController.perspectiveMode = true;
                mainCamera.nearClipPlane = 0.3f;
                mainCamera.farClipPlane = 400f;
                if (!clearFlagsRecorded)
                {
                    originalClearFlags = mainCamera.clearFlags;
                    clearFlagsRecorded = true;
                }
                mainCamera.clearFlags = CameraClearFlags.Skybox;

                cameraData = mainCamera.GetComponent<UniversalAdditionalCameraData>();
                if (cameraData == null)
                    cameraData = mainCamera.gameObject.AddComponent<UniversalAdditionalCameraData>();
                if (cameraData != null)
                {
                    if (!postProcessingRecorded)
                    {
                        originalPostProcessing = cameraData.renderPostProcessing;
                        postProcessingRecorded = true;
                    }
                    cameraData.renderPostProcessing = true;
                }
                cameraConfigured = true;
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("LycheeCinematicDirector 相机配置失败：" + exception.Message, this);
        }
    }

    static void ApplyCameraDefaults(LycheeReplayCameraController controller)
    {
        controller.perspectiveMode = true;
        controller.perspectiveFieldOfView = 42f;
        controller.perspectivePitch = 45f;
        controller.perspectiveDistanceSpanScale = 1.08f;
        controller.perspectiveDistancePadding = 9f;
        controller.perspectiveMinDistance = 17f;
        // 最大相机距离封顶到 60（旧 108 太远 → 多事件时退到准全图、能看见地图边界）。
        // 配合 LycheeReplayPlayer.TryGetStrategicFraming 的聚类取景(span≤30)，镜头永不全图。
        controller.perspectiveMaxDistance = 60f;
        controller.perspectiveEventDistance = 15f;
        controller.perspectiveZoomMultiplier = 1f;
        controller.focusFollowEnabled = false;
        controller.constrainFocusToPlayableBounds = false;
        controller.playableMin = new Vector2(-34f, -21f);
        controller.playableMax = new Vector2(34f, 18f);
    }

    // ---------- 回放事件 ----------

    void SubscribeEvents()
    {
        if (eventsSubscribed) return;
        LycheeReplayPlayer.ReplayEventRaised += OnReplayEvent;
        LycheeReplayPlayer.RoundApplied += OnRoundApplied;
        eventsSubscribed = true;
    }

    void UnsubscribeEvents()
    {
        if (!eventsSubscribed) return;
        LycheeReplayPlayer.ReplayEventRaised -= OnReplayEvent;
        LycheeReplayPlayer.RoundApplied -= OnRoundApplied;
        eventsSubscribed = false;
    }

    void OnReplayEvent(LycheeReplayMessage message, int round)
    {
        if (Disabled || message == null || string.IsNullOrEmpty(message.type)) return;
        if (cameraController == null) EnsureCameraSetup();
        if (cameraController == null) return;

        switch (message.type)
        {
            case "GUARD_BREAK":
            case "FORCED_PASS_START":
                cameraController.Shake(0.25f, 0.5f);
                break;
            case "RUSH_PHASE_START":
            case "RUSH_START": // 真实回放发的是 RUSH_START；RUSH_PHASE_START 为旧名兼容
                cameraController.Shake(0.45f, 1.2f);
                break;
            case "DELIVER_SUCCESS":
                cameraController.Shake(0.3f, 0.8f);
                break;
        }
    }

    void OnRoundApplied(LycheeReplayFrame previousFrame, LycheeReplayFrame currentFrame)
    {
        if (currentFrame == null) return;
        string phase = currentFrame.phase;
        rushActive = !string.IsNullOrEmpty(phase) &&
            phase.IndexOf("RUSH", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    void UpdateRushLighting()
    {
        if (directionalLight == null || !lightModified) return;
        if (LycheeWeatherFxSystem.EnvironmentActive) return; // 天气系统正在接管全局光照，避免双写

        float targetBlend = rushActive ? 1f : 0f;
        if (Mathf.Approximately(rushBlend, targetBlend)) return;
        rushBlend = Mathf.MoveTowards(rushBlend, targetBlend, Time.deltaTime / RushBlendSeconds);
        directionalLight.color = Color.Lerp(CinematicLightColor, RushLightColor, rushBlend);
        directionalLight.intensity = Mathf.Lerp(
            CinematicLightIntensity,
            CinematicLightIntensity * RushIntensityMultiplier,
            rushBlend);
    }

    // ---------- 恢复 ----------

    void RestoreAtmosphere()
    {
        if (!atmosphereConfigured) return;
        atmosphereConfigured = false;
        try
        {
            if (skyboxReplaced)
            {
                RenderSettings.skybox = originalSkybox;
                skyboxReplaced = false;
            }
            if (createdSkybox != null)
            {
                Destroy(createdSkybox);
                createdSkybox = null;
            }
            if (lightModified && directionalLight != null)
            {
                directionalLight.transform.rotation = originalLightRotation;
                directionalLight.color = originalLightColor;
                directionalLight.intensity = originalLightIntensity;
                directionalLight.shadows = originalLightShadows;
                directionalLight.shadowStrength = originalLightShadowStrength;
            }
            if (ambientRecorded)
            {
                RenderSettings.ambientMode = originalAmbientMode;
                RenderSettings.ambientSkyColor = originalAmbientSky;
                RenderSettings.ambientEquatorColor = originalAmbientEquator;
                RenderSettings.ambientGroundColor = originalAmbientGround;
                RenderSettings.ambientLight = originalAmbientLight;
            }
            if (fogRecorded)
            {
                RenderSettings.fog = originalFog;
                RenderSettings.fogMode = originalFogMode;
                RenderSettings.fogStartDistance = originalFogStart;
                RenderSettings.fogEndDistance = originalFogEnd;
                RenderSettings.fogDensity = originalFogDensity;
                RenderSettings.fogColor = originalFogColor;
            }
            if (postProcessingRecorded && cameraData != null)
                cameraData.renderPostProcessing = originalPostProcessing;
            if (clearFlagsRecorded && mainCamera != null)
                mainCamera.clearFlags = originalClearFlags;
        }
        catch (Exception exception)
        {
            Debug.LogWarning("LycheeCinematicDirector 氛围恢复失败：" + exception.Message);
        }
    }

    // ---------- 工具 ----------

    static void SetFloatSafe(Material material, string property, float value)
    {
        if (material != null && material.HasProperty(property)) material.SetFloat(property, value);
    }

    static void SetColorSafe(Material material, string property, Color value)
    {
        if (material != null && material.HasProperty(property)) material.SetColor(property, value);
    }
}
