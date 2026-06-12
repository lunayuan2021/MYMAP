using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 窗口期亮牌专属技能特效（运行时自举）：
/// 订阅 WINDOW_CARD_REVEAL，按红/蓝双方各自的出牌种类生成独立视觉：
/// 1. YAN_DIE  验牒·金光印玺——金色光柱渐显 + 旋转方形印章框落下盖印（带回弹）+ 金粉粒子环。
/// 2. QIANG_XING 抢行·疾影——沿行进切向爆发的青白拉伸速度线 + 三道队色残影面片依次淡出。
/// 3. XIAN_GONG 献贡·果光——荔枝红上升粒子喷泉（重力回落）+ 地面红色光环扩散淡出。
/// 4. BING_ZHENG 兵争·刀阵——6 道银白刀光绕中心旋转抬升消散 + 金属火花粒子。
/// 红牌特效出现在争夺节点左侧 1.2m、蓝牌右侧 1.2m；车队在场时各自车队位置再叠放一份。
/// targetNodeId 为空时回退到 WINDOW_CONTEST_START 缓存的节点，再回退 TryGetEventFocus。
/// 每个特效生命周期不超过 1.6 秒并自动销毁；ABSTAIN/空牌不出特效。
/// </summary>
public class LycheeCardFxSystem : MonoBehaviour
{
    /// <summary>全局禁用开关：true 时不自举；运行中置 true 会立即清掉在场特效并停止生成。</summary>
    public static bool Disabled;

    enum CardKind
    {
        None,
        YanDie,
        QiangXing,
        XianGong,
        BingZheng
    }

    static readonly Color GoldColor = new Color(1f, 0.84f, 0.35f, 1f);
    static readonly Color CyanWhite = new Color(0.78f, 0.96f, 1f, 0.95f);
    static readonly Color LycheeRed = new Color(0.95f, 0.25f, 0.3f, 1f);
    static readonly Color SilverWhite = new Color(0.88f, 0.93f, 1f, 1f);
    static readonly Color SparkColor = new Color(1f, 0.93f, 0.78f, 1f);

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int LegacyColorId = Shader.PropertyToID("_Color");
    static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

    const float SideOffsetMeters = 1.2f;
    const int MaxActiveFx = 36;

    sealed class FxElement
    {
        public Transform transform;
        public MeshRenderer renderer;
        public Vector3 basePosition;
        public float delay;
    }

    sealed class FxInstance
    {
        public CardKind kind;
        public GameObject root;
        public float elapsed;
        public float duration = 1.6f;
        public Transform animRoot;          // YAN_DIE 印章 / BING_ZHENG 刀阵旋转根
        public MeshRenderer pillarRenderer; // YAN_DIE 光柱
        public Color teamColor;
        public readonly List<FxElement> elements = new List<FxElement>();
    }

    LycheeReplayPlayer player;
    Transform fxRoot;
    readonly List<FxInstance> activeFx = new List<FxInstance>();
    readonly List<Object> generatedAssets = new List<Object>();
    MaterialPropertyBlock propertyBlock;
    Material particleMaterial;
    Material meshMaterial;
    Mesh quadMesh;
    Mesh boxMesh;
    Mesh tubeMesh;
    Mesh ringMesh;
    bool sharedAssetsReady;
    string contestNodeId;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Disabled) return;
        LycheeReplayPlayer host = FindFirstObjectByType<LycheeReplayPlayer>();
        if (host == null) return;
        if (host.GetComponent<LycheeCardFxSystem>() != null) return;
        host.gameObject.AddComponent<LycheeCardFxSystem>();
    }

    void Awake()
    {
        player = GetComponent<LycheeReplayPlayer>();
        GameObject rootObject = new GameObject("CardFxRoot");
        rootObject.transform.SetParent(transform, false);
        fxRoot = rootObject.transform;
    }

    void OnEnable()
    {
        LycheeReplayPlayer.ReplayEventRaised += OnReplayEvent;
    }

    void OnDisable()
    {
        LycheeReplayPlayer.ReplayEventRaised -= OnReplayEvent;
    }

    void OnDestroy()
    {
        LycheeReplayPlayer.ReplayEventRaised -= OnReplayEvent;
        foreach (FxInstance fx in activeFx)
        {
            if (fx != null && fx.root != null) Destroy(fx.root);
        }
        activeFx.Clear();
        foreach (Object asset in generatedAssets)
        {
            if (asset != null) Destroy(asset);
        }
        generatedAssets.Clear();
    }

    void Update()
    {
        if (activeFx.Count == 0) return;
        if (Disabled)
        {
            foreach (FxInstance fx in activeFx)
            {
                if (fx != null && fx.root != null) Destroy(fx.root);
            }
            activeFx.Clear();
            return;
        }

        float deltaTime = Time.deltaTime;
        for (int i = activeFx.Count - 1; i >= 0; i--)
        {
            FxInstance fx = activeFx[i];
            if (fx == null || fx.root == null)
            {
                activeFx.RemoveAt(i);
                continue;
            }
            fx.elapsed += deltaTime;
            if (fx.elapsed >= fx.duration)
            {
                Destroy(fx.root);
                activeFx.RemoveAt(i);
                continue;
            }
            UpdateFx(fx);
        }
    }

    // ---------- 事件 ----------

    void OnReplayEvent(LycheeReplayMessage message, int round)
    {
        if (message == null || string.IsNullOrEmpty(message.type)) return;
        LycheeReplayPayload payload = message.payload;

        if (message.type == "WINDOW_CONTEST_START")
        {
            if (payload != null && !string.IsNullOrEmpty(payload.targetNodeId))
                contestNodeId = payload.targetNodeId;
            return;
        }
        if (message.type == "WINDOW_CONTEST_END")
        {
            contestNodeId = null;
            return;
        }
        if (message.type != "WINDOW_CARD_REVEAL") return;
        if (Disabled || player == null || payload == null) return;
        HandleCardReveal(payload);
    }

    void HandleCardReveal(LycheeReplayPayload payload)
    {
        string nodeId = !string.IsNullOrEmpty(payload.targetNodeId) ? payload.targetNodeId : contestNodeId;
        bool hasNodePosition = false;
        Vector3 nodePosition = Vector3.zero;
        if (!string.IsNullOrEmpty(nodeId) &&
            player.TryGetStationPosition(nodeId, out Vector3 stationPosition, out _))
        {
            nodePosition = stationPosition;
            hasNodePosition = true;
        }
        else if (player.TryGetEventFocus(out Vector3 focusPosition))
        {
            nodePosition = focusPosition;
            hasNodePosition = true;
        }

        SpawnSide(ParseCard(payload.redCard), payload.redPlayerId, 0, hasNodePosition, nodePosition, Vector3.left * SideOffsetMeters);
        SpawnSide(ParseCard(payload.blueCard), payload.bluePlayerId, 1, hasNodePosition, nodePosition, Vector3.right * SideOffsetMeters);
    }

    void SpawnSide(CardKind kind, int payloadPlayerId, int camp, bool hasNodePosition, Vector3 nodePosition, Vector3 sideOffset)
    {
        if (kind == CardKind.None || player == null) return;
        int playerId = ResolvePlayerId(payloadPlayerId, camp);
        Color teamColor = playerId != 0
            ? player.TeamColorOf(playerId)
            : (camp == 0 ? new Color(1f, 0.32f, 0.24f, 1f) : new Color(0.34f, 0.6f, 1f, 1f));
        Transform convoy = playerId != 0 ? player.GetConvoyRoot(playerId) : null;

        Vector3 tangent = convoy != null ? convoy.forward : Vector3.right;
        tangent.y = 0f;
        if (tangent.sqrMagnitude < 0.0001f) tangent = Vector3.right;
        tangent.Normalize();

        if (hasNodePosition)
        {
            Spawn(kind, null,
                nodePosition + sideOffset + Vector3.up * 0.05f,
                Quaternion.LookRotation(tangent, Vector3.up),
                true, teamColor);
        }
        if (convoy != null)
        {
            Spawn(kind, convoy, Vector3.up * 0.05f, Quaternion.identity, false, teamColor);
        }
    }

    int ResolvePlayerId(int payloadPlayerId, int camp)
    {
        if (payloadPlayerId != 0) return payloadPlayerId;
        if (player == null) return 0;
        LycheeReplayFrame frame = player.CurrentFrame ?? player.StartFrameData;
        if (frame == null || frame.players == null) return 0;
        foreach (LycheeReplayPlayerState state in frame.players)
        {
            if (state != null && state.camp == camp) return state.playerId;
        }
        return 0;
    }

    static CardKind ParseCard(string card)
    {
        if (string.IsNullOrEmpty(card)) return CardKind.None;
        string value = card.Trim().ToUpperInvariant();
        if (value.Length == 0 || value.Contains("ABSTAIN") || value == "NONE" || value == "PASS") return CardKind.None;
        if (value.Contains("YAN_DIE") || value.Contains("YANDIE")) return CardKind.YanDie;
        if (value.Contains("QIANG_XING") || value.Contains("QIANGXING")) return CardKind.QiangXing;
        if (value.Contains("XIAN_GONG") || value.Contains("XIANGONG")) return CardKind.XianGong;
        if (value.Contains("BING_ZHENG") || value.Contains("BINGZHENG")) return CardKind.BingZheng;
        return CardKind.None;
    }

    // ---------- 生成 ----------

    void Spawn(CardKind kind, Transform parent, Vector3 position, Quaternion rotation, bool worldSpace, Color teamColor)
    {
        EnsureSharedAssets();
        if (activeFx.Count >= MaxActiveFx)
        {
            FxInstance oldest = activeFx[0];
            if (oldest != null && oldest.root != null) Destroy(oldest.root);
            activeFx.RemoveAt(0);
        }

        GameObject root = new GameObject("CardFx_" + kind);
        root.transform.SetParent(parent != null ? parent : fxRoot, false);
        if (worldSpace) root.transform.SetPositionAndRotation(position, rotation);
        else
        {
            root.transform.localPosition = position;
            root.transform.localRotation = rotation;
        }

        FxInstance fx = new FxInstance { kind = kind, root = root, teamColor = teamColor };
        switch (kind)
        {
            case CardKind.YanDie: BuildYanDie(fx); break;
            case CardKind.QiangXing: BuildQiangXing(fx); break;
            case CardKind.XianGong: BuildXianGong(fx); break;
            case CardKind.BingZheng: BuildBingZheng(fx); break;
        }
        activeFx.Add(fx);
        UpdateFx(fx);
        Destroy(root, fx.duration + 0.5f); // 兜底：组件被禁用时也能回收
    }

    /// <summary>1. 验牒·金光印玺：光柱渐显 + 旋转印章框盖印回弹 + 金粉粒子环。</summary>
    void BuildYanDie(FxInstance fx)
    {
        fx.duration = 1.6f;
        Transform root = fx.root.transform;

        fx.pillarRenderer = CreateMeshElement(root, "Pillar", tubeMesh,
            Vector3.zero, Quaternion.identity, new Vector3(0.16f, 2.2f, 0.16f));
        ApplyColor(fx.pillarRenderer, ColorWithAlpha(GoldColor, 0f), 2f);

        GameObject seal = new GameObject("Seal");
        seal.transform.SetParent(root, false);
        seal.transform.localPosition = new Vector3(0f, 2.2f, 0f);
        fx.animRoot = seal.transform;
        const float half = 0.32f;
        AddElement(fx, CreateMeshElement(seal.transform, "SealBarN", boxMesh,
            new Vector3(0f, 0f, half), Quaternion.identity, new Vector3(0.7f, 0.06f, 0.06f)), 0f);
        AddElement(fx, CreateMeshElement(seal.transform, "SealBarS", boxMesh,
            new Vector3(0f, 0f, -half), Quaternion.identity, new Vector3(0.7f, 0.06f, 0.06f)), 0f);
        AddElement(fx, CreateMeshElement(seal.transform, "SealBarE", boxMesh,
            new Vector3(half, 0f, 0f), Quaternion.identity, new Vector3(0.06f, 0.06f, 0.7f)), 0f);
        AddElement(fx, CreateMeshElement(seal.transform, "SealBarW", boxMesh,
            new Vector3(-half, 0f, 0f), Quaternion.identity, new Vector3(0.06f, 0.06f, 0.7f)), 0f);

        ParticleSystem dust = CreateParticleBase(root, "GoldDustRing",
            new Vector3(0f, 0.62f, 0f), Quaternion.Euler(90f, 0f, 0f), 1.4f);
        if (dust != null)
        {
            ParticleSystem.MainModule main = dust.main;
            main.startLifetime = 0.7f;
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 0.9f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.04f, 0.09f);
            main.startColor = GoldColor;
            main.maxParticles = 40;
            ParticleSystem.EmissionModule emission = dust.emission;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0.45f, 26) }); // 盖印落地瞬间扬起金粉
            ParticleSystem.ShapeModule shape = dust.shape;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.5f;
            shape.radiusThickness = 0.25f;
            dust.Play();
        }
    }

    /// <summary>2. 抢行·疾影：切向爆发的拉伸速度线 + 三道队色残影依次淡出。</summary>
    void BuildQiangXing(FxInstance fx)
    {
        fx.duration = 1.0f;
        Transform root = fx.root.transform;

        ParticleSystem lines = CreateParticleBase(root, "SpeedLines",
            new Vector3(0f, 0.6f, 0f), Quaternion.identity, 1f);
        if (lines != null)
        {
            ParticleSystem.MainModule main = lines.main;
            main.startLifetime = 0.35f;
            main.startSpeed = new ParticleSystem.MinMaxCurve(6f, 10f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.08f);
            main.startColor = CyanWhite;
            main.maxParticles = 32;
            ParticleSystem.EmissionModule emission = lines.emission;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 24) });
            ParticleSystem.ShapeModule shape = lines.shape;
            shape.shapeType = ParticleSystemShapeType.Cone; // +Z 即本地切向（根节点已朝向切向）
            shape.angle = 7f;
            shape.radius = 0.12f;
            ParticleSystemRenderer renderer = lines.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.renderMode = ParticleSystemRenderMode.Stretch;
                renderer.lengthScale = 6f;
                renderer.velocityScale = 0.02f;
            }
            lines.Play();
        }

        for (int i = 0; i < 3; i++)
        {
            MeshRenderer ghost = CreateMeshElement(root, "Ghost_" + i, quadMesh,
                new Vector3(0f, 0.7f, -0.3f * i), Quaternion.identity, new Vector3(0.95f, 1.15f, 1f));
            AddElement(fx, ghost, 0.1f * i);
            ApplyColor(ghost, ColorWithAlpha(fx.teamColor, 0.55f), 1.2f);
        }
    }

    /// <summary>3. 献贡·果光：荔枝红上升喷泉（重力回落）+ 地面红色光环扩散淡出。</summary>
    void BuildXianGong(FxInstance fx)
    {
        fx.duration = 1.6f;
        Transform root = fx.root.transform;

        ParticleSystem fountain = CreateParticleBase(root, "FruitFountain",
            new Vector3(0f, 0.15f, 0f), Quaternion.Euler(-90f, 0f, 0f), 1.5f);
        if (fountain != null)
        {
            ParticleSystem.MainModule main = fountain.main;
            main.startLifetime = 1.25f;
            main.startSpeed = 2f; // 向上 2m/s
            main.startSize = new ParticleSystem.MinMaxCurve(0.09f, 0.15f);
            main.startColor = LycheeRed;
            main.gravityModifier = 1f; // 带重力回落
            main.maxParticles = 24;
            ParticleSystem.EmissionModule emission = fountain.emission;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 20) });
            ParticleSystem.ShapeModule shape = fountain.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 14f;
            shape.radius = 0.18f;
            fountain.Play();
        }

        MeshRenderer ring = CreateMeshElement(root, "GroundRing", ringMesh,
            new Vector3(0f, 0.06f, 0f), Quaternion.identity, Vector3.one * 0.3f);
        AddElement(fx, ring, 0f);
        ApplyColor(ring, ColorWithAlpha(LycheeRed, 0.7f), 1.6f);
    }

    /// <summary>4. 兵争·刀阵：6 道银白刀光绕中心两圈旋转抬升消散 + 金属火花。</summary>
    void BuildBingZheng(FxInstance fx)
    {
        fx.duration = 1.25f;
        Transform root = fx.root.transform;

        GameObject spin = new GameObject("BladeSpin");
        spin.transform.SetParent(root, false);
        spin.transform.localPosition = new Vector3(0f, 0.35f, 0f);
        fx.animRoot = spin.transform;
        for (int i = 0; i < 6; i++)
        {
            float angle = i * 60f;
            float radians = angle * Mathf.Deg2Rad;
            Vector3 position = new Vector3(Mathf.Cos(radians) * 0.55f, 0f, Mathf.Sin(radians) * 0.55f);
            MeshRenderer blade = CreateMeshElement(spin.transform, "Blade_" + i, quadMesh,
                position, Quaternion.Euler(0f, angle + 90f, 18f), new Vector3(1.3f, 0.1f, 1f));
            AddElement(fx, blade, 0f);
            ApplyColor(blade, ColorWithAlpha(SilverWhite, 0f), 2f);
        }

        ParticleSystem sparks = CreateParticleBase(root, "MetalSparks",
            new Vector3(0f, 0.7f, 0f), Quaternion.identity, 1f);
        if (sparks != null)
        {
            ParticleSystem.MainModule main = sparks.main;
            main.startLifetime = 0.5f;
            main.startSpeed = new ParticleSystem.MinMaxCurve(2.5f, 5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.06f);
            main.startColor = SparkColor;
            main.gravityModifier = 1.2f;
            main.maxParticles = 40;
            ParticleSystem.EmissionModule emission = sparks.emission;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 30) });
            ParticleSystem.ShapeModule shape = sparks.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.2f;
            ParticleSystemRenderer renderer = sparks.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.renderMode = ParticleSystemRenderMode.Stretch;
                renderer.lengthScale = 3f;
            }
            sparks.Play();
        }
    }

    // ---------- 逐帧动画 ----------

    void UpdateFx(FxInstance fx)
    {
        if (fx == null || fx.root == null) return;
        switch (fx.kind)
        {
            case CardKind.YanDie: UpdateYanDie(fx); break;
            case CardKind.QiangXing: UpdateQiangXing(fx); break;
            case CardKind.XianGong: UpdateXianGong(fx); break;
            case CardKind.BingZheng: UpdateBingZheng(fx); break;
        }
    }

    void UpdateYanDie(FxInstance fx)
    {
        float t = fx.elapsed;
        // 光柱 0.8s 渐显，尾段渐隐
        float rise = Mathf.Clamp01(t / 0.8f);
        float fall = 1f - Mathf.Clamp01((t - 1.15f) / 0.45f);
        ApplyColor(fx.pillarRenderer, ColorWithAlpha(GoldColor, rise * fall * 0.6f), 2f);

        if (fx.animRoot != null)
        {
            fx.animRoot.localPosition = new Vector3(0f, SealHeight(t), 0f);
            fx.animRoot.localRotation = Quaternion.Euler(0f, t * 240f, 0f);
        }
        float barAlpha = Mathf.Clamp01(t / 0.15f) * (1f - Mathf.Clamp01((t - 1.2f) / 0.4f));
        Color barColor = ColorWithAlpha(GoldColor, barAlpha * 0.95f);
        foreach (FxElement element in fx.elements)
        {
            if (element != null) ApplyColor(element.renderer, barColor, 2.4f);
        }
    }

    /// <summary>印章高度：0~0.45s 从 2.2m 加速落到 0.6m，随后阻尼回弹。</summary>
    static float SealHeight(float t)
    {
        const float dropDuration = 0.45f;
        if (t < dropDuration)
        {
            float k = t / dropDuration;
            return Mathf.Lerp(2.2f, 0.6f, k * k);
        }
        float u = t - dropDuration;
        return 0.6f + 0.32f * Mathf.Abs(Mathf.Sin(u * Mathf.PI / 0.45f)) * Mathf.Exp(-5f * u);
    }

    void UpdateQiangXing(FxInstance fx)
    {
        foreach (FxElement element in fx.elements)
        {
            if (element == null || element.renderer == null || element.transform == null) continue;
            float k = Mathf.Clamp01((fx.elapsed - element.delay) / 0.5f); // 依次错位淡出 0.5s
            element.transform.localPosition = element.basePosition + Vector3.back * (0.45f * k);
            ApplyColor(element.renderer, ColorWithAlpha(fx.teamColor, (1f - k) * 0.55f), 1.2f);
        }
    }

    void UpdateXianGong(FxInstance fx)
    {
        foreach (FxElement element in fx.elements)
        {
            if (element == null || element.renderer == null || element.transform == null) continue;
            float k = Mathf.Clamp01(fx.elapsed / 0.75f);
            float ease = 1f - (1f - k) * (1f - k);
            float scale = Mathf.Lerp(0.3f, 2.4f, ease);
            element.transform.localScale = new Vector3(scale, 1f, scale);
            ApplyColor(element.renderer, ColorWithAlpha(LycheeRed, (1f - k) * 0.7f), 1.6f);
        }
    }

    void UpdateBingZheng(FxInstance fx)
    {
        float k = Mathf.Clamp01(fx.elapsed / 1.2f);
        if (fx.animRoot != null)
        {
            fx.animRoot.localRotation = Quaternion.Euler(0f, k * 720f, 0f); // 1.2s 两圈
            fx.animRoot.localPosition = new Vector3(0f, Mathf.Lerp(0.35f, 1.4f, k), 0f);
        }
        float alpha = Mathf.Clamp01(fx.elapsed / 0.12f) * (1f - Mathf.Clamp01((fx.elapsed - 0.7f) / 0.5f)) * 0.85f;
        Color bladeColor = ColorWithAlpha(SilverWhite, alpha);
        foreach (FxElement element in fx.elements)
        {
            if (element != null) ApplyColor(element.renderer, bladeColor, 2f);
        }
    }

    // ---------- 共享资产与助手 ----------

    void EnsureSharedAssets()
    {
        if (sharedAssetsReady) return;
        Texture2D particleTexture = LycheeLoadThemeTexture("card_fx.png");
        if (particleTexture == null) particleTexture = CreateSoftCircleTexture(64);
        Track(particleTexture);
        particleMaterial = CreateFxMaterial("LycheeCardFxParticleMat", particleTexture);
        Track(particleMaterial);
        meshMaterial = CreateFxMaterial("LycheeCardFxMeshMat", null);
        Track(meshMaterial);
        quadMesh = CreateQuadMesh();
        Track(quadMesh);
        boxMesh = CreateBoxMesh();
        Track(boxMesh);
        tubeMesh = CreateTubeMesh(20);
        Track(tubeMesh);
        ringMesh = CreateRingMesh(0.78f, 1f, 48);
        Track(ringMesh);
        sharedAssetsReady = true;
    }

    void Track(Object asset)
    {
        if (asset != null) generatedAssets.Add(asset);
    }

    void AddElement(FxInstance fx, MeshRenderer renderer, float delay)
    {
        if (fx == null || renderer == null) return;
        fx.elements.Add(new FxElement
        {
            transform = renderer.transform,
            renderer = renderer,
            basePosition = renderer.transform.localPosition,
            delay = delay
        });
    }

    MeshRenderer CreateMeshElement(Transform parent, string name, Mesh mesh, Vector3 localPosition, Quaternion localRotation, Vector3 localScale)
    {
        if (parent == null || mesh == null || meshMaterial == null) return null; // Shader 缺失时整体跳过网格元素
        GameObject elementObject = new GameObject(name);
        elementObject.transform.SetParent(parent, false);
        elementObject.transform.localPosition = localPosition;
        elementObject.transform.localRotation = localRotation;
        elementObject.transform.localScale = localScale;
        MeshFilter filter = elementObject.AddComponent<MeshFilter>();
        filter.sharedMesh = mesh;
        MeshRenderer renderer = elementObject.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = meshMaterial;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return renderer;
    }

    /// <summary>用 MaterialPropertyBlock 写颜色，避免实例化材质造成泄漏。</summary>
    void ApplyColor(MeshRenderer renderer, Color color, float emissionScale)
    {
        if (renderer == null) return;
        if (propertyBlock == null) propertyBlock = new MaterialPropertyBlock();
        propertyBlock.Clear();
        propertyBlock.SetColor(BaseColorId, color);
        propertyBlock.SetColor(LegacyColorId, color);
        propertyBlock.SetColor(EmissionColorId, new Color(color.r, color.g, color.b) * emissionScale * color.a);
        renderer.SetPropertyBlock(propertyBlock);
    }

    static Color ColorWithAlpha(Color color, float alpha)
    {
        color.a = Mathf.Clamp01(alpha);
        return color;
    }

    ParticleSystem CreateParticleBase(Transform parent, string name, Vector3 localPosition, Quaternion localRotation, float duration)
    {
        if (parent == null) return null;
        GameObject particleObject = new GameObject(name);
        particleObject.transform.SetParent(parent, false);
        particleObject.transform.localPosition = localPosition;
        particleObject.transform.localRotation = localRotation;

        ParticleSystem particles = particleObject.AddComponent<ParticleSystem>();
        particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ParticleSystem.MainModule main = particles.main;
        main.loop = false;
        main.duration = duration;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        ParticleSystem.EmissionModule emission = particles.emission;
        emission.rateOverTime = 0f;

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
                new GradientAlphaKey(1f, 0.08f),
                new GradientAlphaKey(0f, 1f)
            });
        colorOverLifetime.color = gradient;

        ParticleSystemRenderer renderer = particles.GetComponent<ParticleSystemRenderer>();
        if (renderer != null)
        {
            if (particleMaterial != null) renderer.sharedMaterial = particleMaterial; // 材质创建失败时保留默认
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
        return particles;
    }

    static Material CreateFxMaterial(string name, Texture2D texture)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        if (shader == null) return null;
        Material material = new Material(shader) { name = name };
        if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
        if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f);
        if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
        if (material.HasProperty("_Cull")) material.SetFloat("_Cull", (float)CullMode.Off); // 双面：残影/刀光任意角度可见
        material.SetOverrideTag("RenderType", "Transparent");
        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.renderQueue = (int)RenderQueue.Transparent;
        if (material.HasProperty("_EmissionColor"))
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", Color.black);
        }
        if (texture != null)
        {
            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
            else if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
        }
        return material;
    }

    /// <summary>单位正方形面片（XY 平面，居中，白顶点色；配合 Cull Off 双面可见）。</summary>
    static Mesh CreateQuadMesh()
    {
        Mesh mesh = new Mesh { name = "LycheeCardFxQuad" };
        mesh.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3(0.5f, -0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f)
        };
        mesh.uv = new[]
        {
            new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f)
        };
        mesh.colors32 = new[]
        {
            new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255),
            new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255)
        };
        mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>单位立方体（8 顶点，白顶点色，Unlit 用不需要法线）。</summary>
    static Mesh CreateBoxMesh()
    {
        Mesh mesh = new Mesh { name = "LycheeCardFxBox" };
        Vector3[] vertices = new Vector3[8];
        Color32[] colors = new Color32[8];
        for (int i = 0; i < 8; i++)
        {
            vertices[i] = new Vector3(
                (i & 1) == 0 ? -0.5f : 0.5f,
                (i & 2) == 0 ? -0.5f : 0.5f,
                (i & 4) == 0 ? -0.5f : 0.5f);
            colors[i] = new Color32(255, 255, 255, 255);
        }
        mesh.vertices = vertices;
        mesh.colors32 = colors;
        mesh.triangles = new[]
        {
            0, 1, 3, 0, 3, 2, // -z
            4, 6, 7, 4, 7, 5, // +z
            0, 4, 5, 0, 5, 1, // -y
            2, 3, 7, 2, 7, 6, // +y
            0, 2, 6, 0, 6, 4, // -x
            1, 5, 7, 1, 7, 3  // +x
        };
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>开口圆柱侧面（半径 0.5，y 从 0 到 1），配合 Cull Off 双面渲染作光柱。</summary>
    static Mesh CreateTubeMesh(int segments)
    {
        segments = Mathf.Max(8, segments);
        Mesh mesh = new Mesh { name = "LycheeCardFxTube" };
        int vertexCount = (segments + 1) * 2;
        Vector3[] vertices = new Vector3[vertexCount];
        Vector2[] uvs = new Vector2[vertexCount];
        Color32[] colors = new Color32[vertexCount];
        int[] triangles = new int[segments * 6];
        for (int i = 0; i <= segments; i++)
        {
            float angle = i / (float)segments * Mathf.PI * 2f;
            float cos = Mathf.Cos(angle) * 0.5f;
            float sin = Mathf.Sin(angle) * 0.5f;
            vertices[i * 2] = new Vector3(cos, 0f, sin);
            vertices[i * 2 + 1] = new Vector3(cos, 1f, sin);
            uvs[i * 2] = new Vector2(i / (float)segments, 0f);
            uvs[i * 2 + 1] = new Vector2(i / (float)segments, 1f);
            colors[i * 2] = new Color32(255, 255, 255, 255);
            colors[i * 2 + 1] = new Color32(255, 255, 255, 255);
            if (i < segments)
            {
                int baseIndex = i * 2;
                int triangleIndex = i * 6;
                triangles[triangleIndex] = baseIndex;
                triangles[triangleIndex + 1] = baseIndex + 1;
                triangles[triangleIndex + 2] = baseIndex + 2;
                triangles[triangleIndex + 3] = baseIndex + 1;
                triangles[triangleIndex + 4] = baseIndex + 3;
                triangles[triangleIndex + 5] = baseIndex + 2;
            }
        }
        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.colors32 = colors;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>平放圆环（XZ 平面，白顶点色），用于献贡地面光环。</summary>
    static Mesh CreateRingMesh(float innerRadius, float outerRadius, int segments)
    {
        segments = Mathf.Max(8, segments);
        Mesh mesh = new Mesh { name = "LycheeCardFxRing" };
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
            name = "LycheeCardFxSoftCircle",
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
                alpha = alpha * alpha * (3f - 2f * alpha);
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
