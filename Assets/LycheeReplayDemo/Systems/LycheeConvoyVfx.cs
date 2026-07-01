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
    const float EventIconDedupWindow = 0.45f;

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
        // 马信标延后到"骑行时"才冒（仅快马/短驿马；冰鉴不走这条）：用完马若还停在站里就先挂起，等车队真正上路再冒。
        public bool onEdge;                // 当前是否在路段上（=行进中）；OnRoundApplied 每帧按 routeEdgeId 落定
        public bool pendingHorse;          // 用了短驿马/快马但当时车队停在站点，等它开始移动(上路)再冒 3D 信标
        public bool pendingHorseFast;      // 挂起的是快马(true)还是短驿马(false)
        public int pendingHorseArmedRound; // 挂起时的回合，用于"超过 N 回合还没上路就作废"
        public readonly Dictionary<string, GameObject> activeEventIconRoots = new Dictionary<string, GameObject>();
        public readonly Dictionary<string, float> lastEventIconAt = new Dictionary<string, float>();
    }

    LycheeReplayPlayer player;
    readonly List<ConvoyVfxRig> rigs = new List<ConvoyVfxRig>();
    readonly List<Object> generatedAssets = new List<Object>();
    Material particleMaterial;
    Mesh ringMesh;
    Mesh floatingRingMesh;   // 脚下悬空发光圆环用的有厚度 torus（事件特效共用，懒建一次）
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
        // 相邻帧顺序播放才处理挂起的马信标；跳转/倒带(非相邻)不补冒，避免 seek 后错位。
        bool sequential = previous != null && current.round - previous.round == 1;
        foreach (LycheeReplayPlayerState state in current.players)
        {
            if (state == null) continue;
            ConvoyVfxRig rig = FindRig(state.playerId);
            if (rig == null) continue;
            rig.targetFreshness = state.freshness;
            rig.waterMode = IsWaterEdge(state.routeEdgeId);
            rig.onEdge = !string.IsNullOrEmpty(state.routeEdgeId);   // 在路段=行进中

            // ★决策(2026-07-01，用户复议确认)：短驿马/快马的 3D 信标【延到"使用后真正上路行进"那刻才冒】。
            //   马是停站时用的，延到上路冒能把信标和"停站/获取"错开，读作"骑马赶路"更贴语义(用户："能错开，思路更好")。
            //   冰鉴不走这条(停站保鲜、无骑行语义)，仍 RESOURCE_USE 当场冒；见 PlayResourceVfx。
            //   超过 40 回合仍没上路(极少)就作废，不硬冒在站里。勿改回"用即冒"。
            if (rig.pendingHorse)
            {
                if (!sequential || current.round - rig.pendingHorseArmedRound > 40)
                    rig.pendingHorse = false;
                else if (rig.onEdge)
                {
                    rig.pendingHorse = false;
                    PlayHorseVfx(rig.playerId, rig.pendingHorseFast);
                }
            }
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

        // seek/拖动时间轴的 force 重放只是"补演历史帧状态"，不应凭空生成活 6 秒的头顶信标（冰鉴/马/荔枝转坏）；
        // 用户反馈"获取短驿马仍冒 3D 模型"根因即在此——seek 到车队在路上的帧再触发历史 RESOURCE_USE 时错冒。
        if (player != null && player.IsApplyingForcedFrame) return;

        // 用户要求：冰鉴 / 快马 / 短驿马 三个 3D 信标只在【真正使用】资源那一刻冒出（RESOURCE_USE），
        // 不在【获得/领取】时提前冒（RESOURCE_CLAIM）——冰鉴常在获得后很多回合才用（本回放：第90回合领、
        // 第202回合才用），早早冒出会误导观众以为已经用了。荔枝转坏走 GOOD_TO_BAD（那本身就是触发时刻）。
        if (message.type == "RESOURCE_USE")
        {
            PlayResourceVfx(payload.playerId, payload.resourceType);
            return;
        }

        if (message.type == "GOOD_TO_BAD")
            PlaySpoilVfx(payload.playerId);
    }

    void PlayResourceVfx(int playerId, string resourceType)
    {
        // ★决策(2026-07-01，两轮确认)：
        //   • 冰鉴(ICE_BOX)：RESOURCE_USE 当场冒(停站保鲜、无骑行语义)。
        //   • 快马/短驿马：用时先挂起，等车队【真正上路行进】那刻才冒 3D 信标(QueueOrPlayHorse→OnRoundApplied)。
        //     信标与"停站/获取"错开，读作"骑马赶路"，比"用即冒"更好(用户："能错开，思路更好")。
        //   资源【获取(RESOURCE_CLAIM)】已不再弹中央卡(见 ShowKeyEventIllustration 白名单)，故不会有"获取冒一次"。
        //   seek/force 补演历史帧已在 OnReplayEvent 开头用 IsApplyingForcedFrame 拦截，不会凭空冒活信标。
        string resource = resourceType ?? string.Empty;
        if (resource == "ICE_BOX") PlayIceBoxVfx(playerId);
        else if (resource == "FAST_HORSE") QueueOrPlayHorse(playerId, true);
        else if (resource == "SHORT_HORSE") QueueOrPlayHorse(playerId, false);
    }

    // 马信标延后到"骑行时"冒：车队已在路上就立刻冒；停在站里用的就挂起，等它上路再冒(见 OnRoundApplied)。
    void QueueOrPlayHorse(int playerId, bool fastHorse)
    {
        ConvoyVfxRig rig = FindRig(playerId);
        if (rig == null) { PlayHorseVfx(playerId, fastHorse); return; }
        // 按【当前帧】实时判断是否在路段(不吃缓存的 rig.onEdge——事件在 HandleEvent 阶段派发，onEdge 要等本帧
        // OnRoundApplied 才落定，读缓存会拿到上一帧/seek 来源帧的脏值 → 停站用马被误判成在路上当场冒)。
        if (IsPlayerOnEdgeNow(playerId))
        {
            PlayHorseVfx(playerId, fastHorse);
            return;
        }
        rig.pendingHorse = true;
        rig.pendingHorseFast = fastHorse;
        rig.pendingHorseArmedRound = player != null ? player.CurrentRound : 0;
    }

    // 从当前帧实时判断该队是否在路段（routeEdgeId 非空=行进中）；不依赖 OnRoundApplied 之后才落定的 rig.onEdge。
    bool IsPlayerOnEdgeNow(int playerId)
    {
        LycheeReplayFrame frame = player == null ? null : player.CurrentFrame;
        if (frame == null || frame.players == null) return false;
        foreach (LycheeReplayPlayerState state in frame.players)
            if (state != null && state.playerId == playerId)
                return !string.IsNullOrEmpty(state.routeEdgeId);
        return false;
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

#if UNITY_EDITOR
    /// <summary>编辑器内可视验证用：直接触发某车队的事件特效（绕开回放事件管线）。仅 Editor 编译，不进构建、零运行时开销。
    /// 像 OnReplayEvent 一样先确保 rig 已建（否则 FindRig 取不到、什么都不出）。返回诊断串供截图脚本读。</summary>
    public string EditorDebugTriggerEvent(int playerId, string kind)
    {
        if (player == null) player = GetComponent<LycheeReplayPlayer>();
        if (!built) BuildRigs();
        string diag = "built=" + built + " rigs=" + rigs.Count + " rigFound=" + (FindRig(playerId) != null);
        switch (kind)
        {
            case "ICE_BOX": PlayIceBoxVfx(playerId); break;
            case "FAST_HORSE": PlayHorseVfx(playerId, true); break;
            case "SHORT_HORSE": PlayHorseVfx(playerId, false); break;
            case "GOOD_TO_BAD": PlaySpoilVfx(playerId); break;
        }
        return diag;
    }
#endif

    void PlayIceBoxVfx(int playerId)
    {
        ConvoyVfxRig rig = FindRig(playerId);
        if (rig == null || rig.fxRoot == null) return;
        EnsureSharedAssets();
        if (!BeginEventVfx(rig, "ICE_BOX")) return;
        Color iceTint = new Color(0.55f, 0.90f, 1f);
        // 冰蓝光柱：收细（决策4——冰/荔枝转坏的柱只作竖直定位信号，不当速度光束）。
        StartCoroutine(PlayEventPillar(rig.fxRoot, iceTint, 10.0f, 1.8f, 2.2f));
        // 脚下悬空发光圆环（取代旧贴地霜环 decal）：浮在队色圈上方、比它大一圈、随距离放大的厚环。
        StartCoroutine(FloatingRingRoutine(rig.fxRoot, iceTint, 2.8f));
        // 主视觉：车队头顶 3D 冰棱簇信标（缺模型时回退冰鉴图标牌）+ 一圈柔和配色光环。
        StartEventIconRoutine(rig, "ICE_BOX", "icon_resource_icebox.png", "冰鉴", new Color(0.82f, 0.96f, 1f, 1f), 6.2f, iceTint);
    }

    void PlayHorseVfx(int playerId, bool fastHorse)
    {
        ConvoyVfxRig rig = FindRig(playerId);
        if (rig == null || rig.fxRoot == null) return;
        EnsureSharedAssets();

        string eventKey = fastHorse ? "FAST_HORSE" : "SHORT_HORSE";
        if (!BeginEventVfx(rig, eventKey)) return;
        Color horseTint = fastHorse ? new Color(1f, 0.86f, 0.46f) : new Color(0.95f, 0.70f, 0.30f);
        // 金色光柱：保留作"速度光束"（决策4），短驿马柱矮一点、暗一点。
        StartCoroutine(PlayEventPillar(rig.fxRoot, horseTint, fastHorse ? 10.0f : 9.0f, fastHorse ? 2.6f : 2.3f, fastHorse ? 2.1f : 2.0f));
        // 脚下悬空发光圆环（取代旧贴地风纹 decal + 杂乱风环）。
        StartCoroutine(FloatingRingRoutine(rig.fxRoot, horseTint, fastHorse ? 2.8f : 2.6f));
        // 主视觉：车队头顶 3D 奔马信标（快马/短驿马共用，缺模型时回退图标牌）+ 一圈柔和配色光环。
        StartEventIconRoutine(
            rig,
            eventKey,
            fastHorse ? "icon_resource_fasthorse.png" : "icon_resource_shorthorse.png",
            fastHorse ? "快马" : "短驿马",
            new Color(1f, 0.90f, 0.55f, 1f),
            fastHorse ? 6.2f : 6.0f,
            horseTint);
    }

    void PlaySpoilVfx(int playerId)
    {
        ConvoyVfxRig rig = FindRig(playerId);
        if (rig == null || rig.fxRoot == null) return;
        EnsureSharedAssets();
        if (!BeginEventVfx(rig, "GOOD_TO_BAD")) return;
        Color spoilTint = new Color(1f, 0.42f, 0.18f);   // 警示琥珀红：荔枝转坏
        // 琥珀红光柱：收细（决策4——变质警示柱只作竖直定位信号，不当速度光束）。
        StartCoroutine(PlayEventPillar(rig.fxRoot, spoilTint, 10.0f, 1.9f, 2.2f));
        // 脚下悬空发光圆环。
        StartCoroutine(FloatingRingRoutine(rig.fxRoot, spoilTint, 2.8f));
        // 主视觉：车队头顶 3D 蔫荔枝串信标（缺模型时回退「荔枝转坏」图标牌）+ 一圈柔和配色光环。
        StartEventIconRoutine(rig, "GOOD_TO_BAD", "icon_event_lychee_spoiled.png", "荔枝转坏", new Color(1f, 0.80f, 0.46f, 1f), 6.4f, spoilTint);
    }

    bool BeginEventVfx(ConvoyVfxRig rig, string eventKey)
    {
        if (rig == null) return false;
        string key = EventVfxKey(eventKey);
        float now = Time.time;
        if (rig.lastEventIconAt.TryGetValue(key, out float last) && now - last < EventIconDedupWindow)
            return false;
        rig.lastEventIconAt[key] = now;

        if (rig.activeEventIconRoots.TryGetValue(key, out GameObject oldRoot) && oldRoot != null)
            Destroy(oldRoot);
        rig.activeEventIconRoots.Remove(key);

        // 全局同类去重：同一时刻整张地图只保留一个「冰鉴/快马/荔枝转坏」头顶信标（用户要求"一次一个"）。
        // 冰鉴常被两队在相邻回合各用一次（本回放 202/204），而每个信标活 ~6s（约 18 回合），两个会在屏上叠住；
        // 快马各次使用相隔很远（81/207/294）不会撞，所以只有冰鉴看着"叠了好几个"。新信标出现前，先销毁其它车队上
        // 同类型的旧信标，只留最新这一个。
        for (int i = 0; i < rigs.Count; i++)
        {
            ConvoyVfxRig other = rigs[i];
            if (other == null || other == rig) continue;
            if (other.activeEventIconRoots.TryGetValue(key, out GameObject otherRoot) && otherRoot != null)
                Destroy(otherRoot);
            other.activeEventIconRoots.Remove(key);
        }
        return true;
    }

    void StartEventIconRoutine(ConvoyVfxRig rig, string eventKey, string textureFile, string caption, Color captionColor, float duration, Color haloColor)
    {
        if (rig == null || rig.fxRoot == null) return;
        StartCoroutine(EventIconRoutine(rig, eventKey, textureFile, caption, captionColor, duration, haloColor));
    }

    static string EventVfxKey(string eventKey)
    {
        return string.IsNullOrEmpty(eventKey) ? "__event" : eventKey.ToUpperInvariant();
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
    /// 车队头顶事件主视觉——层级拆分（决策2/4/5）：
    ///   • visualRoot 挂 billboard（朝相机 + 恒定屏幕大小），承载「一圈柔和配色光环」(+ 缺模型时的回退 2D 图标/文字牌)；
    ///   • 有 3D 信标模型(player.GetEventModel)时，模型放单独的「非-billboard」子根，保持自身朝向、原地慢自转 + 上下浮、
    ///     反距离放大、always-on-top（专用 overlay shader 盖过场景几何）——绝不被压成平片、绝不被车厢/地形挡住。
    /// 决策4：3D 模型够清楚，去掉文字牌 + 暗底盘；缺模型才回退旧的 图标贴图 + 底盘 + 文字（URP/Unlit 透明、构建不变洋红）。
    /// </summary>
    IEnumerator EventIconRoutine(ConvoyVfxRig rig, string eventKey, string textureFile, string caption, Color captionColor, float duration, Color haloColor)
    {
        Transform parent = rig != null ? rig.fxRoot : null;
        if (parent == null) yield break;
        string activeKey = EventVfxKey(eventKey);
        GameObject modelPrefab = player != null ? player.GetEventModel(eventKey) : null;
        bool useModel = modelPrefab != null;
        Texture2D tex = useModel || string.IsNullOrEmpty(textureFile) ? null : LycheeLoadThemeTexture(textureFile);

        GameObject root = new GameObject("LycheeEventIcon");
        root.transform.SetParent(parent, false);
        if (rig != null) rig.activeEventIconRoots[activeKey] = root;
        // WebGL 远机位下站牌/资源图标会遮住车队头顶，事件信标必须更高更大。
        Vector3 start = new Vector3(0f, 5.2f, 0f);   // 车队头顶上方的空处
        root.transform.localPosition = start;

        // —— 光环层：billboard 子根（朝相机 + 距离恒定屏幕大小）。模型/回退共用。——
        // 与旧版差异：billboard 只管这层，不再接管整组 root.localScale，所以 3D 模型那层不会被压成平片（最大的坑）。
        GameObject visualRoot = new GameObject("EventIconBillboard");
        visualRoot.transform.SetParent(root.transform, false);
        LycheeReplayBillboard billboard = visualRoot.AddComponent<LycheeReplayBillboard>();
        billboard.referenceDistance = 18f;
        billboard.minScale = 1.0f;
        billboard.maxScale = 5.2f;
        billboard.nearFadeStart = 0f;   // 事件牌短暂出现，不需要贴脸淡出
        billboard.nearFadeEnd = 0f;
        billboard.distanceScale = true;

        // pop 弹入容器：visualRoot.scale 被 billboard 按距离接管，弹入缩放放这层、二者相乘。
        GameObject content = new GameObject("EventIconContent");
        content.transform.SetParent(visualRoot.transform, false);

        // 一圈柔和配色光环（颜色提示 + 把信标从乱战场里托起）。全走 CreateEffectMaterial→ConfigureTransparentMaterial，构建不变洋红。
        Texture2D softTex = CreateSoftCircleTexture(128);
        Material haloMat = CreateEffectMaterial("LycheeEventHaloMat", new Color(haloColor.r, haloColor.g, haloColor.b, 0.70f));
        if (haloMat != null && haloMat.HasProperty("_BaseMap")) haloMat.SetTexture("_BaseMap", softTex);
        else if (haloMat != null && haloMat.HasProperty("_MainTex")) haloMat.SetTexture("_MainTex", softTex);
        GameObject haloQuad = CreateSoftQuad(content.transform, "EventIconHalo", haloMat, 3.2f, 0.06f);

        // —— 回退专用：缺 3D 模型才建 暗底盘 + 图标贴图 + 文字牌（保留旧行为兜底）。——
        Material discMat = null;
        Material iconMaterial = null;
        GameObject iconQuad = null;
        TextMesh shadow = null;
        TextMesh cap = null;
        TextMesh modelShadow = null;
        TextMesh modelCap = null;
        if (!useModel)
        {
            discMat = CreateEffectMaterial("LycheeEventDiscMat", new Color(0.05f, 0.04f, 0.03f, 0.74f));
            if (discMat != null && discMat.HasProperty("_BaseMap")) discMat.SetTexture("_BaseMap", softTex);
            else if (discMat != null && discMat.HasProperty("_MainTex")) discMat.SetTexture("_MainTex", softTex);
            CreateSoftQuad(content.transform, "EventIconDisc", discMat, 1.78f, 0.03f);

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
                    iconQuad.transform.SetParent(content.transform, false);
                    iconQuad.transform.localScale = new Vector3(1.5f, 1.5f, 1f);
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
            shadow = CreatePlaqueText(content.transform, "EventIconCapShadow", new Vector3(0.02f, capY - 0.02f, -0.018f), new Color(0.03f, 0.02f, 0.015f, 0.82f), caption);
            cap = CreatePlaqueText(content.transform, "EventIconCap", new Vector3(0f, capY, -0.022f), captionColor, caption);
        }
        else
        {
            modelShadow = CreatePlaqueText(content.transform, "EventModelCapShadow", new Vector3(0.03f, -1.28f, -0.020f), new Color(0.03f, 0.02f, 0.015f, 0.9f), caption);
            modelCap = CreatePlaqueText(content.transform, "EventModelCap", new Vector3(0f, -1.25f, -0.024f), captionColor, caption);
        }

        // —— 模型层：非-billboard 子根，保持自身朝向；强制 overlay shader + 事件 tint（永不被挡）。——
        GameObject modelWrapper = null;
        List<Material> modelMaterials = new List<Material>();
        if (useModel)
        {
            // 归一化到 ~4.2m、几何中心坐到 wrapper 原点；WebGL 远机位下必须明显大于资源小图标。
            modelWrapper = player.CreateNormalizedOverheadModel(modelPrefab, root.transform, "EventModel", 4.2f);
            ApplyOverheadModelMaterial(modelWrapper, haloColor, modelMaterials);
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            if (root == null) break;
            float t = Mathf.Clamp01(elapsed / duration);
            // 淡入用【绝对时间】而非按 duration 比例：信标要在 ~0.12s 内快速冒到全亮，和左侧「使用XX」播报同帧出现
            //（用户反馈：左侧事件条比 3D 特效先出，原来按 duration≈6s 比例淡入要 ~0.84s 才亮，明显慢半拍）。
            // 淡出仍按 duration 比例(最后 28%)，不动。
            float inT = Ease01(Mathf.Clamp01(elapsed / 0.12f));
            float outT = Ease01((t - 0.72f) / 0.28f);
            float alpha = inT * (1f - outT);

            // 上浮放 root（位置）；pop 放 content（缩放）——content 再与 billboard 距离缩放相乘。
            root.transform.localPosition = start + Vector3.up * (0.65f * EaseOutCubic(t));
            float pop = Mathf.Lerp(1.08f, 2.05f, EaseOutCubic(Mathf.Clamp01(elapsed / 0.14f)));   // 弹入缩放也用绝对时间(~0.14s)，快速到全尺寸（与淡入同步）
            content.transform.localScale = Vector3.one * pop;

            // 光环呼吸脉冲（模型/回退共用）。
            if (haloMat != null)
            {
                float pulse = 0.5f + 0.5f * Mathf.Sin(t * Mathf.PI * 2f * 2.2f);   // ~2.2Hz 呼吸
                SetMaterialColor(haloMat, new Color(haloColor.r, haloColor.g, haloColor.b, alpha * (0.40f + 0.30f * pulse)));
                if (haloQuad != null) haloQuad.transform.localScale = Vector3.one * (3.2f * (1f + 0.10f * pulse));
            }

            if (useModel)
            {
                if (modelShadow != null) modelShadow.color = new Color(0.03f, 0.02f, 0.015f, 0.90f * alpha);
                if (modelCap != null) modelCap.color = new Color(captionColor.r, captionColor.g, captionColor.b, alpha);
                if (modelWrapper != null)
                {
                    // 反距离放大（同光柱/光环）+ 弹入弹出缩放包络（模型不透明，靠缩放进退场）+ 原地慢自转 + 上下浮。
                    Camera cam = Camera.main;
                    float dist = cam != null ? Vector3.Distance(cam.transform.position, modelWrapper.transform.position) : 22f;
                    float distScale = Mathf.Clamp(dist / 18f, 1.8f, 4.8f);
                    float envIn = EaseOutCubic(Mathf.Clamp01(elapsed / 0.14f));   // 模型缩放入场也用绝对时间(~0.14s)，与淡入/弹入同步快速冒出
                    float envOut = 1f - Ease01(Mathf.Clamp01((t - 0.82f) / 0.18f));
                    float env = Mathf.Max(0.0001f, envIn * envOut);
                    float bob = Mathf.Sin(elapsed * 2.2f) * 0.1f;
                    modelWrapper.transform.localPosition = new Vector3(0f, bob, 0f);
                    modelWrapper.transform.localRotation = Quaternion.Euler(0f, elapsed * 40f, 0f);
                    modelWrapper.transform.localScale = Vector3.one * (distScale * env);
                }
            }
            else
            {
                if (iconMaterial != null)
                {
                    if (iconMaterial.HasProperty("_BaseColor")) iconMaterial.SetColor("_BaseColor", new Color(1f, 1f, 1f, alpha));
                    else if (iconMaterial.HasProperty("_Color")) iconMaterial.SetColor("_Color", new Color(1f, 1f, 1f, alpha));
                }
                if (shadow != null) shadow.color = new Color(0.03f, 0.02f, 0.015f, 0.82f * alpha);
                if (cap != null) cap.color = new Color(captionColor.r, captionColor.g, captionColor.b, alpha);
            }

            elapsed += Time.deltaTime;
            yield return null;
        }

        if (rig != null &&
            rig.activeEventIconRoots.TryGetValue(activeKey, out GameObject currentRoot) &&
            object.ReferenceEquals(currentRoot, root))
            rig.activeEventIconRoots.Remove(activeKey);

        if (modelWrapper != null) Destroy(modelWrapper);
        foreach (Material material in modelMaterials)
            if (material != null) Destroy(material);
        if (iconQuad != null) Destroy(iconQuad);
        if (iconMaterial != null) Destroy(iconMaterial);
        if (haloMat != null) Destroy(haloMat);
        if (discMat != null) Destroy(discMat);
        if (softTex != null) Destroy(softTex);
        if (root != null) Destroy(root);
    }

    /// <summary>
    /// 给头顶信标模型的每个 Renderer 强制套 overlay shader：导入 GLB 的材质在 WebGL 构建会变洋红，必须整体替换；
    /// 但**保留用户贴图**——先从原材质抠出 baseColor 贴图，转绑到自定义 shader 的 _BaseMap，再叠淡淡的配色 tint。
    /// shader 自带 ZTest Always、renderQueue 抬到 3100 → always-on-top（决策5，盖过场景几何）。
    /// 找不到专用 shader 时回退 URP/Unlit（带贴图、可见但不保证盖过几何）。生成的材质记入 tracker 供收尾销毁（贴图是共享资产不销毁）。
    /// </summary>
    void ApplyOverheadModelMaterial(GameObject wrapper, Color tint, List<Material> tracker)
    {
        if (wrapper == null) return;
        Renderer[] renderers = wrapper.GetComponentsInChildren<Renderer>(true);

        // 先抠出导入 GLB 的 baseColor 贴图（glTFast 在 URP 下用 _BaseMap，旧版用 baseColorTexture/_MainTex；mainTexture 兜底）。
        Texture baseTex = null;
        foreach (Renderer r in renderers)
        {
            Material src = r.sharedMaterial;
            if (src == null) continue;
            if (src.HasProperty("_BaseMap") && src.GetTexture("_BaseMap") != null) baseTex = src.GetTexture("_BaseMap");
            else if (src.HasProperty("baseColorTexture") && src.GetTexture("baseColorTexture") != null) baseTex = src.GetTexture("baseColorTexture");
            else if (src.HasProperty("_MainTex") && src.GetTexture("_MainTex") != null) baseTex = src.GetTexture("_MainTex");
            else if (src.mainTexture != null) baseTex = src.mainTexture;
            if (baseTex != null) break;
        }

        Shader shader = Shader.Find("Lychee/Overhead Event Model");
        Material material;
        if (shader != null)
        {
            material = new Material(shader) { name = "LycheeOverheadModelMat" };
            // 贴图本色为主，配色只做淡提示（往白 lerp 0.55）+ 一点自发光，保证贴图看得清又带事件色。
            Color baseTint = Color.Lerp(tint, Color.white, 0.55f);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", baseTint);
            if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", new Color(tint.r, tint.g, tint.b) * 0.18f);
            if (baseTex != null && material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", baseTex);
            material.renderQueue = 3100;   // 画在场景透明特效(3000)之后 → 盖在光柱/光环上；ZTest Always 再盖过所有几何
        }
        else
        {
            Shader fallback = Shader.Find("Universal Render Pipeline/Unlit");
            if (fallback == null) fallback = Shader.Find("Sprites/Default");
            if (fallback == null) return;
            material = new Material(fallback) { name = "LycheeOverheadModelMatFallback" };
            Color baseTint = Color.Lerp(tint, Color.white, 0.55f);
            if (baseTex != null)
            {
                if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", baseTex);
                else if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", baseTex);
            }
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", baseTint);
            else if (material.HasProperty("_Color")) material.SetColor("_Color", baseTint);
            material.renderQueue = 3100;
        }
        if (tracker != null) tracker.Add(material);

        foreach (Renderer renderer in renderers)
        {
            int slots = Mathf.Max(1, renderer.sharedMaterials.Length);
            Material[] materials = new Material[slots];
            for (int i = 0; i < slots; i++) materials[i] = material;
            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
    }

    /// <summary>
    /// 脚下悬空发光圆环（决策2，取代旧贴地花纹 GroundDecalRoutine）：浮在队色圈(y~0.05)上方 ~y0.5、
    /// 比它大一圈、有厚度的细环(torus，零厚度平片斜视角会消失)；随距离放大、URP 自发光配色(暖色往白提亮~35%)、
    /// 慢转、淡入淡出。双面渲染保证任意斜视角都看得见；不贴地（避免穿河道/山脊 z-fight）、不挡队色。
    /// </summary>
    IEnumerator FloatingRingRoutine(Transform parent, Color tint, float duration)
    {
        if (parent == null) yield break;
        EnsureSharedAssets();
        if (floatingRingMesh == null) yield break;
        Color warm = Color.Lerp(tint, Color.white, 0.35f);   // 暖色往白提亮 ~35%，任意背景都跳得出来
        Material material = CreateEffectMaterial("LycheeFloatingRingMat", new Color(warm.r, warm.g, warm.b, 0.9f));
        if (material == null) yield break;
        if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);   // 双面：斜视角任意朝向都可见

        GameObject ring = new GameObject("LycheeFloatingRing");
        ring.transform.SetParent(parent, false);
        MeshFilter filter = ring.AddComponent<MeshFilter>();
        filter.sharedMesh = floatingRingMesh;
        MeshRenderer renderer = ring.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        float elapsed = 0f;
        while (elapsed < duration)
        {
            float t = Mathf.Clamp01(elapsed / duration);
            float inA = Mathf.Clamp01(t / 0.12f);
            float outA = Mathf.Clamp01((t - 0.70f) / 0.30f);
            float alpha = inA * (1f - outA);
            float y = 0.5f + Mathf.Sin(elapsed * 2f) * 0.06f;   // 悬空 ~y0.5、轻微上下呼吸
            Camera cam = Camera.main;
            float distScale = 1f;
            if (cam != null) distScale = Mathf.Clamp(Vector3.Distance(cam.transform.position, ring.transform.position) / 22f, 1f, 2.4f);
            ring.transform.localPosition = new Vector3(0f, y, 0f);
            ring.transform.localScale = Vector3.one * distScale;
            ring.transform.localRotation = Quaternion.Euler(0f, elapsed * 30f, 0f);
            SetMaterialColor(material, new Color(warm.r, warm.g, warm.b, alpha * 0.9f));
            elapsed += Time.deltaTime;
            yield return null;
        }

        Destroy(ring);
        Destroy(material);
    }

    /// <summary>程序化圆环管(torus，XZ 平面、法线朝外)：majorRadius=环中心半径、minorRadius=管半径，有厚度，斜视角也看得见。</summary>
    static Mesh CreateTorusMesh(float majorRadius, float minorRadius, int majorSegments, int minorSegments)
    {
        majorSegments = Mathf.Max(8, majorSegments);
        minorSegments = Mathf.Max(4, minorSegments);
        Mesh mesh = new Mesh { name = "LycheeFloatingRingMesh" };
        int vertexCount = (majorSegments + 1) * (minorSegments + 1);
        Vector3[] vertices = new Vector3[vertexCount];
        Vector3[] normals = new Vector3[vertexCount];
        Vector2[] uvs = new Vector2[vertexCount];
        int[] triangles = new int[majorSegments * minorSegments * 6];
        int ti = 0;
        for (int i = 0; i <= majorSegments; i++)
        {
            float u = i / (float)majorSegments * Mathf.PI * 2f;
            Vector3 radial = new Vector3(Mathf.Cos(u), 0f, Mathf.Sin(u));
            for (int j = 0; j <= minorSegments; j++)
            {
                float v = j / (float)minorSegments * Mathf.PI * 2f;
                Vector3 normal = radial * Mathf.Cos(v) + Vector3.up * Mathf.Sin(v);
                int index = i * (minorSegments + 1) + j;
                vertices[index] = radial * majorRadius + normal * minorRadius;
                normals[index] = normal;
                uvs[index] = new Vector2(i / (float)majorSegments, j / (float)minorSegments);
                if (i < majorSegments && j < minorSegments)
                {
                    int a = i * (minorSegments + 1) + j;
                    int b = (i + 1) * (minorSegments + 1) + j;
                    int c = (i + 1) * (minorSegments + 1) + (j + 1);
                    int d = i * (minorSegments + 1) + (j + 1);
                    triangles[ti++] = a; triangles[ti++] = d; triangles[ti++] = b;
                    triangles[ti++] = b; triangles[ti++] = d; triangles[ti++] = c;
                }
            }
        }
        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>建一张软圆贴图 quad（去掉碰撞体、按 localZ 排层），用于事件徽记的暗底盘/光环。</summary>
    GameObject CreateSoftQuad(Transform parent, string objectName, Material material, float scale, float localZ)
    {
        GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = objectName;
        quad.transform.SetParent(parent, false);
        quad.transform.localPosition = new Vector3(0f, 0f, localZ);
        quad.transform.localScale = new Vector3(scale, scale, 1f);
        Collider col = quad.GetComponent<Collider>();
        if (col != null) Destroy(col);
        MeshRenderer mr = quad.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            mr.sharedMaterial = material;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }
        return quad;
    }

    /// <summary>
    /// 事件「光柱」：从车队脚下立起的一根配色竖直光柱（3 片交叉 quad），立起→呼吸→淡出。
    /// 这是远机位下的"抓眼"主信号——高、竖、有颜色，即使车队很小或贴边也一眼能看到。
    /// 关键：绝不挂 billboard（必须保持世界竖直），且材质走 CreateEffectMaterial→ConfigureTransparentMaterial（WebGL 不变洋红）。
    /// </summary>
    IEnumerator PlayEventPillar(Transform parent, Color color, float height, float width, float duration)
    {
        if (parent == null) yield break;
        GameObject root = new GameObject("LycheeEventPillar");
        root.transform.SetParent(parent, false);
        root.transform.localPosition = Vector3.zero;
        root.transform.localRotation = Quaternion.identity;

        // 把柱体颜色往白里提亮 40%：暖色(金/琥珀红)在暖色地形上对比低、看不清，提亮后任意背景都跳得出来。
        // 徽记/光环仍用满饱和原色保留辨识度，只有光柱提亮。
        Color beam = Color.Lerp(color, Color.white, 0.4f);
        Texture2D ramp = CreateVerticalRampTexture(16, 128);
        Material mat = CreateEffectMaterial("LycheeEventPillarMat", new Color(beam.r, beam.g, beam.b, 0f));
        if (mat == null) { Destroy(root); if (ramp != null) Destroy(ramp); yield break; }
        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", ramp);
        else if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", ramp);

        for (int i = 0; i < 3; i++)
        {
            GameObject blade = GameObject.CreatePrimitive(PrimitiveType.Quad);
            blade.name = "PillarBlade" + i;
            blade.transform.SetParent(root.transform, false);
            blade.transform.localRotation = Quaternion.Euler(0f, i * 60f, 0f);  // 交叉成星形，任意角度都能看到
            blade.transform.localPosition = new Vector3(0f, height * 0.5f, 0f);  // 底坐地面、向上立
            blade.transform.localScale = new Vector3(width, height, 1f);
            Collider col = blade.GetComponent<Collider>();
            if (col != null) Destroy(col);
            MeshRenderer mr = blade.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            float t = Mathf.Clamp01(elapsed / Mathf.Max(0.05f, duration));
            float inA = Mathf.Clamp01(t / 0.10f);
            float outA = Mathf.Clamp01((t - 0.55f) / 0.45f);
            float alpha = inA * (1f - outA);
            float grow = EaseOutCubic(Mathf.Clamp01(t / 0.14f));   // 从地面快速立起
            // 反距离放大：相机越远，光柱整体(含宽度)按比例放大，保证远机位也是醒目的一道竖光，永不缩小。
            Camera cam = Camera.main;
            float distFactor = 1f;
            if (cam != null) distFactor = Mathf.Clamp(Vector3.Distance(cam.transform.position, root.transform.position) / 22f, 1f, 2.4f);
            root.transform.localScale = new Vector3(distFactor, grow * distFactor, distFactor);
            root.transform.localRotation = Quaternion.Euler(0f, elapsed * 28f, 0f);  // 缓慢自转的微光感
            SetMaterialColor(mat, new Color(beam.r, beam.g, beam.b, alpha * 0.85f));
            elapsed += Time.deltaTime;
            yield return null;
        }

        Destroy(mat);
        if (ramp != null) Destroy(ramp);
        Destroy(root);
    }

    /// <summary>竖直光柱用的渐变贴图：横向亮芯软边、纵向自下而上渐隐（顶部淡出）。</summary>
    static Texture2D CreateVerticalRampTexture(int w, int h)
    {
        w = Mathf.Max(4, w); h = Mathf.Max(8, h);
        Texture2D texture = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            name = "LycheeEventPillarRamp",
            wrapMode = TextureWrapMode.Clamp
        };
        Color32[] pixels = new Color32[w * h];
        for (int y = 0; y < h; y++)
        {
            float vy = (float)y / (h - 1);                                   // 0 底 .. 1 顶
            float vAlpha = 1f - Mathf.Clamp01((vy - 0.45f) / 0.55f);         // 顶部 45%→100% 渐隐
            for (int x = 0; x < w; x++)
            {
                float dx = ((x + 0.5f) / w - 0.5f) * 2f;                     // -1..1
                float hAlpha = Mathf.Clamp01(1f - Mathf.Abs(dx));
                hAlpha = hAlpha * hAlpha;                                    // 软边、亮芯
                float a = hAlpha * vAlpha;
                pixels[y * w + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        }
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        return texture;
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
        text.characterSize = 0.05f;
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
        // 悬空发光圆环：外径 ~2.1（比队色圈大一圈）、管半径 0.10 的细环；事件特效共用。
        floatingRingMesh = CreateTorusMesh(2.0f, 0.10f, 56, 10);
        Track(floatingRingMesh);
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
