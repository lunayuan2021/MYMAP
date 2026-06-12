using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 车队特效系统（运行时自举）：
/// 1. 一骑红尘拖尾——陆路红棕尘土，速度越快越浓；水路自动切换为白色水花尾迹。
/// 2. 鲜度光环——车队脚下半透明圆环，颜色随当前鲜度在翠绿/琥珀/警红之间平滑过渡并呼吸闪烁。
/// 3. RUSH 金色流光——冲刺阶段车队四周升起一圈金色细粒子，离开 RUSH 自动停止。
/// 特效全部无状态跟随车队根节点，SeekFrame 回跳无需额外处理。
/// </summary>
public class LycheeConvoyVfx : MonoBehaviour
{
    /// <summary>全局禁用开关：true 时不自举；运行中切换会隐藏/恢复已生成的特效。</summary>
    public static bool Disabled;

    static readonly Color DustColor = new Color(0.78f, 0.36f, 0.22f, 0.9f);
    static readonly Color WaterSprayColor = new Color(0.93f, 0.97f, 1f, 0.92f);
    static readonly Color RushGoldColor = new Color(1f, 0.85f, 0.3f, 0.95f);
    static readonly Color FreshGreen = new Color(0.2f, 0.95f, 0.4f);
    static readonly Color FreshAmber = new Color(1f, 0.72f, 0.1f);
    static readonly Color FreshRed = new Color(1f, 0.2f, 0.1f);

    const float DustSpeedThreshold = 0.2f;   // 低于此速度不冒尘
    const float DustMaxRate = 36f;           // 速度映射的最大发射速率
    const float DustRatePerSpeed = 6f;       // rate = speed * 6，最高 36
    const float RingHeight = 0.07f;          // 与 TeamRing(y=0.04) 错开
    const float BreathHz = 1.2f;

    sealed class ConvoyVfxRig
    {
        public int playerId;
        public Transform root;
        public Transform fxRoot;
        public ParticleSystem dustTrail;
        public ParticleSystem rushGlow;
        public Material ringMaterial;
        public Vector3 lastPosition;
        public bool hasLastPosition;
        public float displayFreshness = 100f;
        public float targetFreshness = 100f;
        public bool waterMode;
        public bool waterModeApplied;
        public bool rushApplied;
        public float breathSeed;
    }

    LycheeReplayPlayer player;
    readonly List<ConvoyVfxRig> rigs = new List<ConvoyVfxRig>();
    readonly List<Object> generatedAssets = new List<Object>();
    Material particleMaterial;
    Mesh ringMesh;
    bool sharedAssetsReady;
    bool built;
    bool rushPhase;
    bool disabledApplied;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Disabled) return;
        LycheeReplayPlayer host = FindFirstObjectByType<LycheeReplayPlayer>();
        if (host == null) return;
        if (host.GetComponent<LycheeConvoyVfx>() != null) return;
        host.gameObject.AddComponent<LycheeConvoyVfx>();
    }

    void Awake()
    {
        player = GetComponent<LycheeReplayPlayer>();
    }

    void OnEnable()
    {
        LycheeReplayPlayer.RoundApplied += OnRoundApplied;
    }

    void OnDisable()
    {
        LycheeReplayPlayer.RoundApplied -= OnRoundApplied;
    }

    IEnumerator Start()
    {
        // 延迟一帧，确保 LycheeReplayPlayer.Awake 已建好车队
        yield return null;
        if (player == null) player = GetComponent<LycheeReplayPlayer>();
        if (player == null) yield break;
        // 回放数据可能稍晚就绪，最多再等约 2 秒
        for (int i = 0; i < 120 && player != null && player.CurrentFrame == null; i++)
            yield return null;
        BuildRigs();
    }

    void OnDestroy()
    {
        LycheeReplayPlayer.RoundApplied -= OnRoundApplied;
        foreach (ConvoyVfxRig rig in rigs)
        {
            if (rig != null && rig.fxRoot != null)
                Destroy(rig.fxRoot.gameObject);
        }
        rigs.Clear();
        foreach (Object asset in generatedAssets)
        {
            if (asset != null) Destroy(asset);
        }
        generatedAssets.Clear();
    }

    void Update()
    {
        if (!built) return;
        if (Disabled != disabledApplied)
        {
            disabledApplied = Disabled;
            foreach (ConvoyVfxRig rig in rigs)
            {
                if (rig != null && rig.fxRoot != null)
                    rig.fxRoot.gameObject.SetActive(!disabledApplied);
            }
        }
        if (disabledApplied) return;

        float deltaTime = Time.deltaTime;
        foreach (ConvoyVfxRig rig in rigs)
            UpdateRig(rig, deltaTime);
    }

    void OnRoundApplied(LycheeReplayFrame previous, LycheeReplayFrame current)
    {
        if (current == null) return;
        rushPhase = current.phase == "RUSH";
        if (current.players == null) return;
        foreach (LycheeReplayPlayerState state in current.players)
        {
            if (state == null) continue;
            ConvoyVfxRig rig = FindRig(state.playerId);
            if (rig == null) continue;
            rig.targetFreshness = state.freshness;
            rig.waterMode = IsWaterEdge(state.routeEdgeId);
        }
    }

    ConvoyVfxRig FindRig(int playerId)
    {
        for (int i = 0; i < rigs.Count; i++)
        {
            if (rigs[i] != null && rigs[i].playerId == playerId) return rigs[i];
        }
        return null;
    }

    bool IsWaterEdge(string edgeId)
    {
        return player != null &&
            player.TryGetEdge(edgeId, out MapRouteEdge edge) &&
            edge != null &&
            edge.routeType == MapRouteType.Water;
    }

    void BuildRigs()
    {
        if (built || player == null) return;
        LycheeReplayFrame frame = player.CurrentFrame;
        if (frame == null || frame.players == null || frame.players.Length == 0) return;

        EnsureSharedAssets();
        rushPhase = frame.phase == "RUSH";

        foreach (LycheeReplayPlayerState state in frame.players)
        {
            if (state == null) continue;
            if (FindRig(state.playerId) != null) continue;
            Transform root = player.GetConvoyRoot(state.playerId);
            if (root == null) continue;

            GameObject fxRoot = new GameObject("ConvoyVfx_" + state.playerId);
            fxRoot.transform.SetParent(root, false);

            ConvoyVfxRig rig = new ConvoyVfxRig
            {
                playerId = state.playerId,
                root = root,
                fxRoot = fxRoot.transform,
                targetFreshness = state.freshness,
                displayFreshness = state.freshness,
                waterMode = IsWaterEdge(state.routeEdgeId),
                breathSeed = state.playerId * 1.73f,
                lastPosition = root.position,
                hasLastPosition = true
            };
            rig.dustTrail = CreateDustTrail(fxRoot.transform);
            rig.rushGlow = CreateRushGlow(fxRoot.transform);
            CreateFreshnessRing(fxRoot.transform, rig);
            rigs.Add(rig);
        }
        built = rigs.Count > 0;
        if (built && disabledApplied)
        {
            foreach (ConvoyVfxRig rig in rigs)
            {
                if (rig != null && rig.fxRoot != null)
                    rig.fxRoot.gameObject.SetActive(false);
            }
        }
    }

    void UpdateRig(ConvoyVfxRig rig, float deltaTime)
    {
        if (rig == null || rig.root == null || rig.fxRoot == null) return;

        Vector3 position = rig.root.position;
        float speed = 0f;
        if (rig.hasLastPosition && deltaTime > 0.0001f)
        {
            Vector3 delta = position - rig.lastPosition;
            delta.y = 0f; // 忽略上下浮动，只统计水平位移
            speed = delta.magnitude / deltaTime;
        }
        rig.lastPosition = position;
        rig.hasLastPosition = true;

        // 1. 红尘/水花拖尾：发射速率与速度成正比
        if (rig.dustTrail != null)
        {
            if (rig.waterMode != rig.waterModeApplied)
            {
                rig.waterModeApplied = rig.waterMode;
                ParticleSystem.MainModule main = rig.dustTrail.main;
                main.startColor = rig.waterMode ? WaterSprayColor : DustColor;
                main.gravityModifier = rig.waterMode ? 0.22f : -0.02f;
                main.startLifetime = rig.waterMode ? 1.0f : 1.4f;
            }
            ParticleSystem.EmissionModule emission = rig.dustTrail.emission;
            emission.rateOverTime = speed < DustSpeedThreshold
                ? 0f
                : Mathf.Clamp(speed * DustRatePerSpeed, 0f, DustMaxRate);
        }

        // 3. RUSH 金色流光开关
        if (rig.rushGlow != null && rig.rushApplied != rushPhase)
        {
            rig.rushApplied = rushPhase;
            ParticleSystem.EmissionModule rushEmission = rig.rushGlow.emission;
            rushEmission.rateOverTime = rushPhase ? 12f : 0f;
        }

        // 2. 鲜度光环：颜色平滑过渡 + 1.2Hz 呼吸
        if (rig.ringMaterial != null)
        {
            rig.displayFreshness = Mathf.MoveTowards(rig.displayFreshness, rig.targetFreshness, deltaTime * 45f);
            Color color = FreshnessColor(rig.displayFreshness);
            float breath = Mathf.Sin((Time.time * BreathHz + rig.breathSeed) * Mathf.PI * 2f);
            color.a = 0.375f + 0.125f * breath; // 0.25 ~ 0.5
            if (rig.ringMaterial.HasProperty("_BaseColor")) rig.ringMaterial.SetColor("_BaseColor", color);
            else if (rig.ringMaterial.HasProperty("_Color")) rig.ringMaterial.SetColor("_Color", color);
            if (rig.ringMaterial.HasProperty("_EmissionColor"))
                rig.ringMaterial.SetColor("_EmissionColor", new Color(color.r, color.g, color.b) * 1.5f);
        }
    }

    static Color FreshnessColor(float freshness)
    {
        // <40 警红，40~70 琥珀，>70 翠绿，边界附近平滑插值
        Color low = Color.Lerp(FreshRed, FreshAmber, Mathf.InverseLerp(34f, 46f, freshness));
        return Color.Lerp(low, FreshGreen, Mathf.InverseLerp(64f, 76f, freshness));
    }

    // ---------- 生成物 ----------

    void EnsureSharedAssets()
    {
        if (sharedAssetsReady) return;
        Texture2D particleTexture = LycheeLoadThemeTexture("convoy_dust.png");
        if (particleTexture == null) particleTexture = CreateSoftCircleTexture(64);
        Track(particleTexture);
        particleMaterial = CreateParticleMaterial(particleTexture);
        Track(particleMaterial);
        ringMesh = CreateRingMesh(0.62f, 0.88f, 48);
        Track(ringMesh);
        sharedAssetsReady = true;
    }

    void Track(Object asset)
    {
        if (asset != null) generatedAssets.Add(asset);
    }

    ParticleSystem CreateDustTrail(Transform parent)
    {
        GameObject dustObject = new GameObject("DustTrail");
        dustObject.transform.SetParent(parent, false);
        dustObject.transform.localPosition = new Vector3(0f, 0.18f, -0.85f);
        // 锥口朝向后上方：粒子向车队斜后上方扩散
        dustObject.transform.localRotation = Quaternion.LookRotation(new Vector3(0f, 0.55f, -1f).normalized, Vector3.up);

        ParticleSystem particles = dustObject.AddComponent<ParticleSystem>();
        particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        ParticleSystem.MainModule main = particles.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = 1.4f;
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.9f, 2.2f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.25f, 0.5f);
        main.startColor = DustColor;
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.gravityModifier = -0.02f;
        main.maxParticles = 160;

        ParticleSystem.EmissionModule emission = particles.emission;
        emission.rateOverTime = 0f;

        ParticleSystem.ShapeModule shape = particles.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 24f;
        shape.radius = 0.28f;

        ParticleSystem.ColorOverLifetimeModule colorOverLifetime = particles.colorOverLifetime;
        colorOverLifetime.enabled = true;
        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(Color.white, 0f),
                new GradientColorKey(new Color(1f, 0.95f, 0.88f), 1f)
            },
            new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(0.85f, 0.08f),
                new GradientAlphaKey(0f, 1f)
            });
        colorOverLifetime.color = gradient;

        ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = particles.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(
            1f,
            new AnimationCurve(new Keyframe(0f, 0.55f), new Keyframe(1f, 1.55f)));

        ApplyParticleMaterial(particles);
        particles.Play();
        return particles;
    }

    ParticleSystem CreateRushGlow(Transform parent)
    {
        GameObject glowObject = new GameObject("RushGlow");
        glowObject.transform.SetParent(parent, false);
        glowObject.transform.localPosition = new Vector3(0f, 0.1f, 0f);
        // Circle 形状默认位于本地 XY 平面，绕 X 轴转 90 度放平成水平一圈
        glowObject.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

        ParticleSystem particles = glowObject.AddComponent<ParticleSystem>();
        particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        ParticleSystem.MainModule main = particles.main;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.startLifetime = 1.1f;
        main.startSpeed = 0f;
        main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.12f);
        main.startColor = RushGoldColor;
        main.gravityModifier = 0f;
        main.maxParticles = 48;

        ParticleSystem.EmissionModule emission = particles.emission;
        emission.rateOverTime = 0f; // 进入 RUSH 后设为 12

        ParticleSystem.ShapeModule shape = particles.shape;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = 0.85f;
        shape.radiusThickness = 0.15f;

        ParticleSystem.VelocityOverLifetimeModule velocity = particles.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.World;
        velocity.x = 0f;
        velocity.y = 1.2f; // 向上 1.2m/s
        velocity.z = 0f;

        ParticleSystem.ColorOverLifetimeModule colorOverLifetime = particles.colorOverLifetime;
        colorOverLifetime.enabled = true;
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
                new GradientAlphaKey(1f, 0.15f),
                new GradientAlphaKey(0f, 1f)
            });
        colorOverLifetime.color = gradient;

        ApplyParticleMaterial(particles);
        particles.Play();
        return particles;
    }

    void ApplyParticleMaterial(ParticleSystem particles)
    {
        if (particles == null || particleMaterial == null) return; // 材质创建失败时保持默认，不抛异常
        ParticleSystemRenderer renderer = particles.GetComponent<ParticleSystemRenderer>();
        if (renderer == null) return;
        renderer.sharedMaterial = particleMaterial;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    void CreateFreshnessRing(Transform parent, ConvoyVfxRig rig)
    {
        if (parent == null || rig == null || ringMesh == null) return;
        Material material = CreateRingMaterial();
        if (material == null) return; // 没有可用 Shader 就放弃光环，其他特效不受影响
        Track(material);

        GameObject ringObject = new GameObject("FreshnessRing");
        ringObject.transform.SetParent(parent, false);
        ringObject.transform.localPosition = new Vector3(0f, RingHeight, 0f);
        MeshFilter filter = ringObject.AddComponent<MeshFilter>();
        filter.sharedMesh = ringMesh;
        MeshRenderer renderer = ringObject.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        rig.ringMaterial = material;
    }

    static Material CreateParticleMaterial(Texture2D texture)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        if (shader == null) return null;
        Material material = new Material(shader) { name = "LycheeConvoyParticleMat" };
        ConfigureTransparentMaterial(material);
        if (texture != null)
        {
            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
            else if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
        }
        return material;
    }

    static Material CreateRingMaterial()
    {
        // Particles/Unlit 自带 _EmissionColor，优先；逐级回退保证总有可用方案
        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        if (shader == null) return null;
        Material material = new Material(shader) { name = "LycheeFreshnessRingMat" };
        ConfigureTransparentMaterial(material);
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", new Color(FreshGreen.r, FreshGreen.g, FreshGreen.b, 0.4f));
        if (material.HasProperty("_EmissionColor"))
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", FreshGreen * 1.5f);
        }
        return material;
    }

    static void ConfigureTransparentMaterial(Material material)
    {
        if (material == null) return;
        if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
        if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f);
        if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
        material.SetOverrideTag("RenderType", "Transparent");
        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.renderQueue = (int)RenderQueue.Transparent;
    }

    /// <summary>程序化圆环（XZ 平面，法线朝上），带白色顶点色与径向 UV。</summary>
    static Mesh CreateRingMesh(float innerRadius, float outerRadius, int segments)
    {
        segments = Mathf.Max(8, segments);
        Mesh mesh = new Mesh { name = "LycheeFreshnessRingMesh" };
        int vertexCount = (segments + 1) * 2;
        Vector3[] vertices = new Vector3[vertexCount];
        Vector2[] uvs = new Vector2[vertexCount];
        Color32[] colors = new Color32[vertexCount];
        int[] triangles = new int[segments * 6];
        for (int i = 0; i <= segments; i++)
        {
            float angle = i / (float)segments * Mathf.PI * 2f;
            float cos = Mathf.Cos(angle);
            float sin = Mathf.Sin(angle);
            vertices[i * 2] = new Vector3(cos * innerRadius, 0f, sin * innerRadius);
            vertices[i * 2 + 1] = new Vector3(cos * outerRadius, 0f, sin * outerRadius);
            uvs[i * 2] = new Vector2(i / (float)segments, 0f);
            uvs[i * 2 + 1] = new Vector2(i / (float)segments, 1f);
            colors[i * 2] = new Color32(255, 255, 255, 255);
            colors[i * 2 + 1] = new Color32(255, 255, 255, 255);
            if (i < segments)
            {
                int baseIndex = i * 2;
                int triangleIndex = i * 6;
                triangles[triangleIndex] = baseIndex;
                triangles[triangleIndex + 1] = baseIndex + 3;
                triangles[triangleIndex + 2] = baseIndex + 1;
                triangles[triangleIndex + 3] = baseIndex;
                triangles[triangleIndex + 4] = baseIndex + 2;
                triangles[triangleIndex + 5] = baseIndex + 3;
            }
        }
        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.colors32 = colors;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>程序化柔边圆点贴图（粒子兜底贴图）。</summary>
    static Texture2D CreateSoftCircleTexture(int size)
    {
        size = Mathf.Max(8, size);
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "LycheeConvoySoftCircle",
            wrapMode = TextureWrapMode.Clamp
        };
        float half = (size - 1) * 0.5f;
        Color32[] pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x - half) / half;
                float dy = (y - half) / half;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = Mathf.Clamp01(1f - distance);
                alpha = alpha * alpha * (3f - 2f * alpha); // smoothstep 柔边
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
            }
        }
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        return texture;
    }

    /// <summary>从 StreamingAssets/UITheme/ 按需加载主题贴图，缺失或异常时返回 null 走程序化兜底。</summary>
    static Texture2D LycheeLoadThemeTexture(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        try
        {
            byte[] bytes = LycheeThemeCache.LoadBytes(fileName);
            if (bytes == null || bytes.Length == 0) return null;
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = "LycheeTheme_" + fileName,
                wrapMode = TextureWrapMode.Clamp
            };
            if (!texture.LoadImage(bytes))
            {
                Destroy(texture);
                return null;
            }
            return texture;
        }
        catch (System.Exception)
        {
            return null;
        }
    }
}
