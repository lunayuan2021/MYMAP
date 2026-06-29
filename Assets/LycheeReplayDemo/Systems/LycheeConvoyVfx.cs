using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 车队特效系统（运行时自举）：
/// 1. 一骑红尘拖尾——按阵营着色的尘土（红队朱红 #B85C3E、蓝队灰褐 #7A6A55），World 空间按行进距离扬尘，
///    静止不冒烟、跑起来才在身后拉出一条"红尘"；水路扬尘渐隐到 0（船不扬尘）。
/// 2. 鲜度光环——车队脚下半透明圆环，颜色随当前鲜度在翠绿/琥珀/警红之间平滑过渡并呼吸闪烁。
/// 3. RUSH 金色流光——冲刺阶段车队四周升起一圈金色细粒子，离开 RUSH 自动停止。
/// 特效跟随车队根节点；SeekFrame 瞬移由速度阈值检测并重置拖尾，避免沿跳转路径留下跨图尘线。
/// </summary>
public class LycheeConvoyVfx : MonoBehaviour
{
    /// <summary>全局禁用开关：true 时不自举；运行中切换会隐藏/恢复已生成的特效。</summary>
    public static bool Disabled;

    // 红队尘土取"一骑红尘"的朱红基调，蓝队压成灰褐偏冷——红尘是红队主题，但双队跑路都该带土
    static readonly Color DustRedCamp = new Color(0.722f, 0.361f, 0.243f, 0.20f);  // #B85C3E，轻量尘土，不遮挡单位
    static readonly Color DustBlueCamp = new Color(0.478f, 0.416f, 0.333f, 0.20f); // #7A6A55
    static readonly Color RushGoldColor = new Color(1f, 0.85f, 0.3f, 0.95f);
    static readonly Color FreshGreen = new Color(0.2f, 0.95f, 0.4f);
    static readonly Color FreshAmber = new Color(1f, 0.72f, 0.1f);
    static readonly Color FreshRed = new Color(1f, 0.2f, 0.1f);

    const float DustRatePerMeter = 3.2f;     // 每米扬尘粒子数：只作运动提示，不压住车队/小人
    const float WaterFadeSeconds = 0.45f;    // 水陆切换时扬尘渐隐时长（船不扬尘）
    const float TeleportSpeedLimit = 40f;    // SeekFrame 瞬移保护，与 LycheeCharacterMotion 同阈值
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
        public float waterBlend;       // 0=陆路 1=水路，平滑过渡驱动扬尘渐隐
        public float dustRateApplied;  // 上次写入的 rateOverDistance，避免每帧无谓改模块
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
        LycheeReplayPlayer host = FindAnyObjectByType<LycheeReplayPlayer>();
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
        LycheeReplayPlayer.ReplayEventRaised += OnReplayEvent;
    }

    void OnDisable()
    {
        LycheeReplayPlayer.RoundApplied -= OnRoundApplied;
        LycheeReplayPlayer.ReplayEventRaised -= OnReplayEvent;
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
        LycheeReplayPlayer.ReplayEventRaised -= OnReplayEvent;
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

    void OnReplayEvent(LycheeReplayMessage message, int round)
    {
        if (Disabled || message == null || message.payload == null) return;
        if (player == null) player = GetComponent<LycheeReplayPlayer>();
        if (!built) BuildRigs();
        if (!built || disabledApplied) return;

        LycheeReplayPayload payload = message.payload;
        if (payload.playerId <= 0) return;

        if (message.type == "RESOURCE_USE")
        {
            string resource = payload.resourceType ?? string.Empty;
            if (resource == "ICE_BOX") PlayIceBoxVfx(payload.playerId);
            else if (resource == "FAST_HORSE") PlayHorseVfx(payload.playerId, true);
            else if (resource == "SHORT_HORSE") PlayHorseVfx(payload.playerId, false);
            return;
        }

        if (message.type == "GOOD_TO_BAD")
            PlaySpoilVfx(payload.playerId);
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

            bool onWater = IsWaterEdge(state.routeEdgeId);
            ConvoyVfxRig rig = new ConvoyVfxRig
            {
                playerId = state.playerId,
                root = root,
                fxRoot = fxRoot.transform,
                targetFreshness = state.freshness,
                displayFreshness = state.freshness,
                waterMode = onWater,
                // 开局就在水上时 waterBlend 直接置 1，避免起步先冒一团土再渐隐
                waterBlend = onWater ? 1f : 0f,
                dustRateApplied = DustRatePerMeter, // 与 CreateDustTrail 的初始发射率保持一致
                breathSeed = state.playerId * 1.73f,
                lastPosition = root.position,
                hasLastPosition = true
            };
            rig.dustTrail = CreateDustTrail(fxRoot.transform, state.camp);
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

        // 1. 一骑红尘拖尾：rateOverDistance 按行进距离扬尘，水路渐隐（船不扬尘）
        if (rig.dustTrail != null)
        {
            // SeekFrame 瞬移：World 空间 + rateOverDistance 会沿跳转路径拉出一条横贯地图的尘线，
            // 检测到超物理速度时整体重启粒子系统（Simulate(0,restart) 连内部距离计数一起清零，
            // 顺带清掉旧位置残留的拖尾，等效于回放跳转的"清场"）
            if (speed > TeleportSpeedLimit)
            {
                rig.dustTrail.Simulate(0f, true, true);
                rig.dustTrail.Play();
            }
            // waterBlend 平滑过渡而非硬切：上下船瞬间尘土淡出/淡入更自然
            rig.waterBlend = Mathf.MoveTowards(rig.waterBlend, rig.waterMode ? 1f : 0f, deltaTime / WaterFadeSeconds);
            float dustRate = DustRatePerMeter * (1f - rig.waterBlend);
            if (!Mathf.Approximately(dustRate, rig.dustRateApplied))
            {
                rig.dustRateApplied = dustRate;
                ParticleSystem.EmissionModule emission = rig.dustTrail.emission;
                emission.rateOverDistance = dustRate;
            }
        }

        // 3. RUSH 金色流光开关
        if (rig.rushGlow != null && rig.rushApplied != rushPhase)
        {
            rig.rushApplied = rushPhase;
            ParticleSystem.EmissionModule rushEmission = rig.rushGlow.emission;
            rushEmission.rateOverTime = rushPhase ? 5f : 0f;
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

    void PlayIceBoxVfx(int playerId)
    {
        ConvoyVfxRig rig = FindRig(playerId);
        if (rig == null || rig.fxRoot == null) return;
        EnsureSharedAssets();
        // 冰蓝光环（干净的环、不是杂乱粒子）：表达"车队周围冰蓝特效"。
        StartCoroutine(AnimateRing(rig.fxRoot, "IceBoxRing", new Color(0.55f, 0.90f, 1f, 0.60f), 1.7f, 0.6f, 3.0f, 0.12f));
        // 脚下"霜环"地贴：平铺一张冰霜贴图在车队脚下（用户做的 decal_ice_frost.png）；贴图缺失则自动跳过、光环兜底。
        StartCoroutine(GroundDecalRoutine(rig.fxRoot, "decal_ice_frost.png", Color.white, 3.8f, 1.1f));
        // 主视觉：车队上方弹出"冰鉴"资源图标贴图 + 文字。
        StartCoroutine(EventIconRoutine(rig.fxRoot, "icon_resource_icebox.png", "冰鉴", new Color(0.82f, 0.96f, 1f, 1f), 2.0f));
    }

    void PlayHorseVfx(int playerId, bool fastHorse)
    {
        ConvoyVfxRig rig = FindRig(playerId);
        if (rig == null || rig.fxRoot == null) return;
        EnsureSharedAssets();

        // 脚下风环（干净的环，表达"疾驰加速"），不再撒杂乱的尘土/风线粒子。
        StartCoroutine(AnimateRing(
            rig.fxRoot,
            fastHorse ? "FastHorseGustRing" : "ShortHorseGustRing",
            new Color(1f, 0.86f, 0.46f, fastHorse ? 0.46f : 0.36f),
            fastHorse ? 1.0f : 0.78f,
            0.5f,
            fastHorse ? 2.5f : 1.95f,
            0.09f));
        // 脚下"疾风"地贴：平铺一张风纹贴图在车队脚下（用户做的 decal_wind_gust.png）；贴图缺失则自动跳过、光环兜底。
        StartCoroutine(GroundDecalRoutine(rig.fxRoot, "decal_wind_gust.png", Color.white, fastHorse ? 4.2f : 3.4f, fastHorse ? 1.1f : 0.95f));
        // 主视觉：车队上方弹出快马/短驿马图标贴图 + 文字。
        StartCoroutine(EventIconRoutine(
            rig.fxRoot,
            fastHorse ? "icon_resource_fasthorse.png" : "icon_resource_shorthorse.png",
            fastHorse ? "快马" : "短驿马",
            new Color(1f, 0.90f, 0.55f, 1f),
            fastHorse ? 2.0f : 1.8f));
    }

    void PlaySpoilVfx(int playerId)
    {
        ConvoyVfxRig rig = FindRig(playerId);
        if (rig == null || rig.fxRoot == null) return;
        EnsureSharedAssets();
        // 荔枝转坏图标已就位：与冰鉴/快马一致，车队上方弹「荔枝转坏」图标贴图 + 文字（贴图缺失时回退纯文字牌）。
        StartCoroutine(EventIconRoutine(rig.fxRoot, "icon_event_lychee_spoiled.png", "荔枝转坏", new Color(1f, 0.80f, 0.46f, 1f), 2.0f));
    }

    IEnumerator AnimateRing(Transform parent, string objectName, Color baseColor, float duration, float startScale, float endScale, float y)
    {
        if (parent == null || ringMesh == null) yield break;
        Material material = CreateEffectMaterial(objectName + "Mat", baseColor);
        if (material == null) yield break;

        GameObject ringObject = new GameObject(objectName);
        ringObject.transform.SetParent(parent, false);
        ringObject.transform.localPosition = new Vector3(0f, y, 0f);
        MeshFilter filter = ringObject.AddComponent<MeshFilter>();
        filter.sharedMesh = ringMesh;
        MeshRenderer renderer = ringObject.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        float elapsed = 0f;
        while (elapsed < duration)
        {
            float t = Mathf.Clamp01(elapsed / Mathf.Max(0.01f, duration));
            float eased = EaseOutCubic(t);
            ringObject.transform.localScale = Vector3.one * Mathf.Lerp(startScale, endScale, eased);
            Color color = baseColor;
            color.a = baseColor.a * (1f - Ease01(t));
            SetMaterialColor(material, color);
            elapsed += Time.deltaTime;
            yield return null;
        }

        Destroy(ringObject);
        Destroy(material);
    }

    void SpawnBurstParticles(
        Transform parent,
        string objectName,
        Vector3 localPosition,
        Quaternion localRotation,
        Color color,
        float duration,
        float lifetimeMin,
        float lifetimeMax,
        float speedMin,
        float speedMax,
        float sizeMin,
        float sizeMax,
        int count,
        ParticleSystemShapeType shapeType,
        float radius,
        Vector3 velocity,
        ParticleSystemRenderMode renderMode,
        float lengthScale)
    {
        if (parent == null) return;
        GameObject particleObject = new GameObject(objectName);
        particleObject.transform.SetParent(parent, false);
        particleObject.transform.localPosition = localPosition;
        particleObject.transform.localRotation = localRotation;

        ParticleSystem particles = particleObject.AddComponent<ParticleSystem>();
        particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        ParticleSystem.MainModule main = particles.main;
        main.duration = Mathf.Max(0.05f, duration);
        main.loop = false;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.startLifetime = new ParticleSystem.MinMaxCurve(lifetimeMin, lifetimeMax);
        main.startSpeed = new ParticleSystem.MinMaxCurve(speedMin, speedMax);
        main.startSize = new ParticleSystem.MinMaxCurve(sizeMin, sizeMax);
        main.startColor = color;
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.maxParticles = Mathf.Max(12, count * 2);

        ParticleSystem.EmissionModule emission = particles.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)Mathf.Clamp(count, 1, 200)) });

        ParticleSystem.ShapeModule shape = particles.shape;
        shape.shapeType = shapeType;
        shape.radius = Mathf.Max(0.01f, radius);
        if (shapeType == ParticleSystemShapeType.Cone)
        {
            shape.angle = 10f;
            shape.radiusThickness = 0.65f;
        }

        ParticleSystem.VelocityOverLifetimeModule velocityModule = particles.velocityOverLifetime;
        velocityModule.enabled = velocity.sqrMagnitude > 0.0001f;
        velocityModule.space = ParticleSystemSimulationSpace.Local;
        velocityModule.x = velocity.x;
        velocityModule.y = velocity.y;
        velocityModule.z = velocity.z;

        ParticleSystem.ColorOverLifetimeModule colorOverLifetime = particles.colorOverLifetime;
        colorOverLifetime.enabled = true;
        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(1f, 0.16f),
                new GradientAlphaKey(0f, 1f)
            });
        colorOverLifetime.color = gradient;

        ApplyParticleMaterial(particles);
        ParticleSystemRenderer particleRenderer = particles.GetComponent<ParticleSystemRenderer>();
        if (particleRenderer != null)
        {
            particleRenderer.renderMode = renderMode;
            particleRenderer.lengthScale = Mathf.Max(1f, lengthScale);
            particleRenderer.velocityScale = renderMode == ParticleSystemRenderMode.Stretch ? 0.35f : 0f;
            particleRenderer.shadowCastingMode = ShadowCastingMode.Off;
            particleRenderer.receiveShadows = false;
        }

        particles.Play();
        Destroy(particleObject, duration + lifetimeMax + 0.35f);
    }

    /// <summary>
    /// 脚下平铺地贴：把一张贴图水平铺在车队脚下（如疾风风纹），放大淡出。贴图缺失则什么都不做（光环兜底）。
    /// 走 URP/Unlit 透明材质（与场景同管线，构建里不会变洋红）。
    /// </summary>
    IEnumerator GroundDecalRoutine(Transform parent, string textureFile, Color tint, float maxScale, float duration)
    {
        if (parent == null || string.IsNullOrEmpty(textureFile)) yield break;
        Texture2D tex = LycheeLoadThemeTexture(textureFile);
        if (tex == null) yield break;   // 没这张贴图就不显示，PlayHorseVfx 仍有光环

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        if (shader == null) yield break;
        Material mat = new Material(shader) { name = "LycheeGroundDecalMat" };
        ConfigureTransparentMaterial(mat);
        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
        else if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);

        GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = "GroundDecal";
        Collider col = quad.GetComponent<Collider>();
        if (col != null) Destroy(col);
        quad.transform.SetParent(parent, false);
        // 抬到红蓝队色圈(TeamRing y≈0.05)之上，避免被它压住/z-fight（用户反馈贴地花纹看不到，被圈挡了）。
        quad.transform.localPosition = new Vector3(0f, 0.25f, 0f);
        quad.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);  // 平铺到水平面、法线朝上
        MeshRenderer mr = quad.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            float t = Mathf.Clamp01(elapsed / Mathf.Max(0.05f, duration));
            // 「从车队中心闪大」：前 22% 快速从一个小尺寸放大到 maxScale，alpha 先猛地冲亮再淡出。
            float scale = Mathf.Lerp(maxScale * 0.25f, maxScale, EaseOutCubic(Mathf.Clamp01(t / 0.22f)));
            float inA = Mathf.Clamp01(t / 0.08f);
            float outA = Mathf.Clamp01((t - 0.28f) / 0.72f);
            float alpha = inA * (1f - outA) * tint.a;
            quad.transform.localScale = new Vector3(scale, scale, 1f);
            Color c = new Color(tint.r, tint.g, tint.b, alpha);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            else if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
            elapsed += Time.deltaTime;
            yield return null;
        }

        Destroy(quad);
        Destroy(mat);
    }

    /// <summary>
    /// 通用「事件贴图牌」：在车队正上方弹出一张图标贴图（冰鉴/快马的资源图标 PNG）+ 一行说明文字，
    /// 弹入→上浮→淡出。用户明确要"看得懂的贴图"而非粒子，所以这是三个事件的主视觉。
    /// 贴图走 URP/Unlit 透明材质（与 3D 场景同管线、构建里绝不会被裁成洋红）；贴图缺失则只显示文字牌兜底。
    /// </summary>
    IEnumerator EventIconRoutine(Transform parent, string textureFile, string caption, Color captionColor, float duration)
    {
        if (parent == null) yield break;
        Texture2D tex = string.IsNullOrEmpty(textureFile) ? null : LycheeLoadThemeTexture(textureFile);

        GameObject root = new GameObject("LycheeEventIcon");
        root.transform.SetParent(parent, false);
        // 抬到 2.85：实测 1.65 时图标被马车模型挡住、从 2.5D 斜视角根本看不到（用户反复反馈看不到的真因）。
        Vector3 start = new Vector3(0f, 2.85f, 0f);   // 车队头顶、模型之上，斜视角清楚可见
        root.transform.localPosition = start;
        LycheeReplayBillboard billboard = root.AddComponent<LycheeReplayBillboard>();
        billboard.referenceDistance = 26f;
        billboard.minScale = 0.9f;
        billboard.maxScale = 2.8f;
        billboard.distanceScale = false;

        Material iconMaterial = null;
        GameObject iconQuad = null;
        if (tex != null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader != null)
            {
                iconMaterial = new Material(shader) { name = "LycheeEventIconMat" };
                ConfigureTransparentMaterial(iconMaterial);
                if (iconMaterial.HasProperty("_BaseMap")) iconMaterial.SetTexture("_BaseMap", tex);
                else if (iconMaterial.HasProperty("_MainTex")) iconMaterial.SetTexture("_MainTex", tex);
                if (iconMaterial.HasProperty("_BaseColor")) iconMaterial.SetColor("_BaseColor", Color.white);
                else if (iconMaterial.HasProperty("_Color")) iconMaterial.SetColor("_Color", Color.white);

                iconQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                iconQuad.name = "EventIconQuad";
                iconQuad.transform.SetParent(root.transform, false);
                iconQuad.transform.localScale = new Vector3(1.15f, 1.15f, 1f);
                Collider iconCollider = iconQuad.GetComponent<Collider>();
                if (iconCollider != null) Destroy(iconCollider);
                MeshRenderer iconRenderer = iconQuad.GetComponent<MeshRenderer>();
                if (iconRenderer != null)
                {
                    iconRenderer.sharedMaterial = iconMaterial;
                    iconRenderer.shadowCastingMode = ShadowCastingMode.Off;
                    iconRenderer.receiveShadows = false;
                }
            }
        }

        float capY = iconQuad != null ? -0.80f : 0f;   // 有图就把文字放图下方，没图就居中
        TextMesh shadow = CreatePlaqueText(root.transform, "EventIconCapShadow", new Vector3(0.02f, capY - 0.02f, -0.018f), new Color(0.03f, 0.02f, 0.015f, 0.82f), caption);
        TextMesh cap = CreatePlaqueText(root.transform, "EventIconCap", new Vector3(0f, capY, -0.022f), captionColor, caption);

        float elapsed = 0f;
        while (elapsed < duration)
        {
            float t = Mathf.Clamp01(elapsed / duration);
            float inT = Ease01(t / 0.14f);
            float outT = Ease01((t - 0.72f) / 0.28f);
            float alpha = inT * (1f - outT);
            float pop = Mathf.Lerp(0.8f, 1.55f, EaseOutCubic(Mathf.Clamp01(t / 0.20f)));   // 放大：实测原来 max1.0 太小
            root.transform.localPosition = start + Vector3.up * (0.45f * EaseOutCubic(t));
            root.transform.localScale = Vector3.one * pop;
            if (iconMaterial != null)
            {
                if (iconMaterial.HasProperty("_BaseColor")) iconMaterial.SetColor("_BaseColor", new Color(1f, 1f, 1f, alpha));
                else if (iconMaterial.HasProperty("_Color")) iconMaterial.SetColor("_Color", new Color(1f, 1f, 1f, alpha));
            }
            if (shadow != null) shadow.color = new Color(0.03f, 0.02f, 0.015f, 0.82f * alpha);
            if (cap != null) cap.color = new Color(captionColor.r, captionColor.g, captionColor.b, alpha);
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (iconQuad != null) Destroy(iconQuad);
        if (iconMaterial != null) Destroy(iconMaterial);
        Destroy(root);
    }

    /// <summary>
    /// 通用「事件文字牌」：在车队上方弹出一块带文字的牌子并上浮淡出。荔枝转坏（无对应图标）用它。
    /// </summary>
    IEnumerator LabelPlaqueRoutine(Transform parent, string label, Color plaqueColor, Color textColor,
        Vector3 startLocal, float rise, float baseScale, float duration)
    {
        if (parent == null || string.IsNullOrEmpty(label)) yield break;
        Material plaqueMaterial = CreateEffectMaterial("LycheeEventPlaqueMat", plaqueColor);
        if (plaqueMaterial == null) yield break;

        GameObject root = new GameObject("LycheeEventPlaque");
        root.transform.SetParent(parent, false);
        root.transform.localPosition = startLocal;
        root.transform.localScale = Vector3.one * baseScale;
        LycheeReplayBillboard billboard = root.AddComponent<LycheeReplayBillboard>();
        billboard.referenceDistance = 26f;
        billboard.minScale = 0.72f;
        billboard.maxScale = 1.6f;
        billboard.distanceScale = false;

        GameObject bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
        bg.name = "PlaqueBg";
        bg.transform.SetParent(root.transform, false);
        // 牌面随字数加宽：2 字（冰鉴/快马）窄一点，4 字（荔枝转坏）才需要满宽。
        float bgWidth = 0.62f + 0.30f * label.Length;
        bg.transform.localScale = new Vector3(bgWidth, 0.52f, 1f);
        Collider collider = bg.GetComponent<Collider>();
        if (collider != null) Destroy(collider);
        MeshRenderer bgRenderer = bg.GetComponent<MeshRenderer>();
        if (bgRenderer != null)
        {
            bgRenderer.sharedMaterial = plaqueMaterial;
            bgRenderer.shadowCastingMode = ShadowCastingMode.Off;
            bgRenderer.receiveShadows = false;
        }

        TextMesh shadow = CreatePlaqueText(root.transform, "PlaqueTextShadow", new Vector3(0.02f, -0.02f, -0.018f), new Color(0.03f, 0.02f, 0.015f, 0.82f), label);
        TextMesh text = CreatePlaqueText(root.transform, "PlaqueText", new Vector3(0f, 0f, -0.022f), textColor, label);

        float elapsed = 0f;
        while (elapsed < duration)
        {
            float t = Mathf.Clamp01(elapsed / duration);
            float inT = Ease01(t / 0.14f);
            float outT = Ease01((t - 0.70f) / 0.30f);
            float alpha = inT * (1f - outT);
            root.transform.localPosition = startLocal + Vector3.up * (rise * EaseOutCubic(t));
            root.transform.localScale = Vector3.one * Mathf.Lerp(baseScale * 0.82f, baseScale, EaseOutCubic(Mathf.Clamp01(t / 0.22f)));

            SetMaterialColor(plaqueMaterial, new Color(plaqueColor.r, plaqueColor.g, plaqueColor.b, plaqueColor.a * alpha));
            if (shadow != null) shadow.color = new Color(0.03f, 0.02f, 0.015f, 0.82f * alpha);
            if (text != null) text.color = new Color(textColor.r, textColor.g, textColor.b, alpha);

            elapsed += Time.deltaTime;
            yield return null;
        }

        Destroy(root);
        Destroy(plaqueMaterial);
    }

    TextMesh CreatePlaqueText(Transform parent, string objectName, Vector3 localPosition, Color color, string label)
    {
        GameObject textObject = new GameObject(objectName);
        textObject.transform.SetParent(parent, false);
        textObject.transform.localPosition = localPosition;
        TextMesh text = textObject.AddComponent<TextMesh>();
        text.text = label;
        text.font = Resources.Load<Font>("Fonts/LXGWWenKai-Regular") ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = 58;
        text.characterSize = 0.042f;
        text.anchor = TextAnchor.MiddleCenter;
        text.alignment = TextAlignment.Center;
        text.color = color;
        return text;
    }

    static float Ease01(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    static float EaseOutCubic(float t)
    {
        t = 1f - Mathf.Clamp01(t);
        return 1f - t * t * t;
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

    ParticleSystem CreateDustTrail(Transform parent, int camp)
    {
        GameObject dustObject = new GameObject("DustTrail");
        dustObject.transform.SetParent(parent, false);
        // 挂在车队后方约 1.2m：尘土从车尾扬起，锥口朝向斜后上方扩散
        dustObject.transform.localPosition = new Vector3(0f, 0.18f, -1.2f);
        dustObject.transform.localRotation = Quaternion.LookRotation(new Vector3(0f, 0.55f, -1f).normalized, Vector3.up);

        ParticleSystem particles = dustObject.AddComponent<ParticleSystem>();
        particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        ParticleSystem.MainModule main = particles.main;
        // World 空间是拖尾成立的关键：粒子留在出生地，车队跑过去自然拉出一条"红尘"
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.85f, 1.35f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.22f, 0.55f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.22f, 0.55f);
        main.startColor = camp == 0 ? DustRedCamp : DustBlueCamp;
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.maxParticles = 44;

        ParticleSystem.EmissionModule emission = particles.emission;
        emission.rateOverTime = 0f;
        emission.rateOverDistance = DustRatePerMeter; // 按距离发射：静止天然不冒烟

        ParticleSystem.ShapeModule shape = particles.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 24f;
        shape.radius = 0.28f;

        // 尘土轻微上飘：用 velocityOverLifetime 而非负重力，飘升量不受粒子初速方向影响
        ParticleSystem.VelocityOverLifetimeModule velocity = particles.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.World;
        velocity.x = 0f;
        velocity.y = 0.35f;
        velocity.z = 0f;

        ParticleSystem.ColorOverLifetimeModule colorOverLifetime = particles.colorOverLifetime;
        colorOverLifetime.enabled = true;
        Gradient gradient = new Gradient();
        // alpha 曲线与 startColor 的 0.20 相乘：只保留轻尘提示，避免激活效果盖住单位
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(Color.white, 0f),
                new GradientColorKey(Color.white, 1f)
            },
            new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(1f, 0.06f),
                new GradientAlphaKey(0f, 1f)
            });
        colorOverLifetime.color = gradient;

        // 尘团轻微扩散边变淡：1 → 1.6，控制屏幕占比
        ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = particles.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(
            1.6f,
            new AnimationCurve(new Keyframe(0f, 1f / 1.6f), new Keyframe(1f, 1f)));

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
        main.startSize = new ParticleSystem.MinMaxCurve(0.035f, 0.08f);
        main.startColor = RushGoldColor;
        main.gravityModifier = 0f;
        main.maxParticles = 20;

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
        // 默认粒子材质在 URP 下会显示为洋红错误材质，必须自建；
        // Shader.Find 失败时逐级回退，最后落到内置管线的 Sprites/Default 兜底
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

    static Material CreateEffectMaterial(string materialName, Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        if (shader == null) return null;
        Material material = new Material(shader) { name = materialName };
        ConfigureTransparentMaterial(material);
        SetMaterialColor(material, color);
        return material;
    }

    static void SetMaterialColor(Material material, Color color)
    {
        if (material == null) return;
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        if (material.HasProperty("_EmissionColor"))
        {
            material.EnableKeyword("_EMISSION");
            Color emission = new Color(color.r, color.g, color.b, 1f) * Mathf.Clamp01(color.a) * 1.4f;
            material.SetColor("_EmissionColor", emission);
        }
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
