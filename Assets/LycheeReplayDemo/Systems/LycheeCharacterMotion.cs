using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

/// <summary>角色动作覆盖层种类（对抗演出 API 用）。</summary>
public enum LycheeCharacterActionKind
{
    Attack,
    Build,
    Cheer,
    Hit
}

/// <summary>
/// 角色动画系统（运行时自举，纯程序化骨骼动画）：
/// 工程内没有任何 AnimationClip，因此基于「绑定姿势 + 偏移四元数」在 LateUpdate 驱动 22 根 Mixamo 骨骼。
/// 驱动单位是「骨骼组」（wrapper + 骨骼字典 + Hips 基准 + 相位种子）：车队的 Captain、换上 Member.glb
/// 后能采到骨骼的每个 TeamMember_*、以及出击小队的 Member_*（经 NotifySquadSpawned 接入）各占一组，
/// 相位种子随机错开避免齐步走。所有姿态 = 采集瞬间记录的基准 localRotation × 当帧计算的偏移，绝不累积漂移。
/// 状态机（每帧由回放数据推导）：
///   Walk —— 陆路行进：腿臂反相摆动、膝/肘随相位屈伸、Spine 前倾、Hips 步频颠簸；
///   Row  —— 水路行进：双臂同相划桨、Spine 节奏前后倾、双腿半蹲；
///   Idle —— 静止：呼吸起伏 + Perlin 头部微动 + 手臂自然微摆；
///   Action 覆盖层 —— Attack / Build / Cheer / Hit（仅作用于 Captain 组），权重压过 locomotion，
///   对外提供 PlayAction API，并订阅 ReplayEventRaised 自驱动（设卡→Build、破关→Attack、
///   对抗窗口→双方 Attack、交付/验核→Cheer）。
/// 出击小队是临时对象（抵达约 2.4 秒后销毁），其骨骼组没有车队 rig.speed 可用，靠 wrapper 世界位移自估速度，
/// wrapper 变 Unity 假 null 时静默剔除；LycheeReplayPlayer.AnimateSquadMembers 的 wrapper 级行军起伏/攻击突刺
/// 与本系统的骨骼摆动叠加是预期效果。
/// 无骨骼对象的廉价动画：TeamMember 行进弹跳（采到骨骼组的减半防双重起伏）、Boat 水上横摇、
/// Carriage 速度颠簸（Perlin）+ 程序化假车轮滚动 + 轮速同步弹跳 + 加减速点头
/// （Carriage.glb 是单节点烘焙网格拆不出轮子，只能圆柱补假轮）。
/// 马拉车（player.horseModel 非空时启用，空则特性整体静默不存在）：每辆 Carriage 车头前方挂一匹
/// 归一 ~1.7m 的马（车包围盒 +Z 前缘 + 半身长，底部对齐车底），horseWalkClip 可用时走 Playables
/// 循环（播放速率随 rig.speed 调档），否则程序化 bob 兜底（相位与假车轮同步）；水路与假车轮同款整马隐藏；
/// 马是 carriage wrapper 子物体跟车走，SeekFrame 瞬移无需特殊处理。
/// 升级钩子：LycheeAnimationLibrary 提供非 legacy 的 walk/idle/row clip 时，Captain 组改用 Playables 循环播放，
/// 程序化层只保留 Action 覆盖；缺哪个状态哪个状态继续程序化，队员/小队组始终程序化。
/// </summary>
public class LycheeCharacterMotion : MonoBehaviour
{
    /// <summary>全局禁用开关：true 时不自举；运行中切换会恢复绑定姿势并停止驱动。</summary>
    public static bool Disabled;

    const float WeightFadeSeconds = 0.18f;   // 各层权重渐变时长
    const float MoveSpeedThreshold = 0.2f;   // 行进判定速度阈值（m/s，忽略 Y）
    const float SquadMoveSpeedThreshold = 0.1f; // 出击小队自估速度的行进判定阈值（小队整体移速较低）
    const float TeleportSpeedLimit = 40f;    // SeekFrame 瞬移保护
    const float RowHz = 0.9f;                // 划桨节奏
    const float HorseTargetSize = 1.7f;      // 马归一尺寸（米，最大边）：比 2.4 的车身矮一头、比 1.25 的队长高一头
    const float TwoPi = Mathf.PI * 2f;
    const float ContestHoldSeconds = 99999f; // 对抗窗口期间持续 Attack，CONTEST_END 主动结束

    static readonly string[] BoneNames =
    {
        "Hips", "Spine", "Spine1", "Spine2", "Neck", "Head",
        "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
        "RightShoulder", "RightArm", "RightForeArm", "RightHand",
        "LeftUpLeg", "LeftLeg", "LeftFoot", "LeftToeBase",
        "RightUpLeg", "RightLeg", "RightFoot", "RightToeBase"
    };

    sealed class BonePose
    {
        public Transform transform;
        public Quaternion baseRotation;
        public Quaternion offset = Quaternion.identity;
        public bool touched;
    }

    /// <summary>
    /// 一套可独立驱动的骨骼组：wrapper（Captain / TeamMember_* / 出击小队 Member_*）+ 骨骼字典 + Hips 基准。
    /// phaseSeed 随机错开各组 walk/row 相位避免齐步走；出击小队组没有车队 rig.speed 可用，
    /// 由 selfSpeed 系列字段按 wrapper 世界位移自行估速并维护自己的 Walk/Idle 权重。
    /// </summary>
    sealed class BoneGroup
    {
        public Transform wrapper;
        public readonly Dictionary<string, BonePose> bones = new Dictionary<string, BonePose>();
        public Transform hips;
        public Vector3 hipsBasePosition;
        public float hipsLift;
        public float phaseSeed;  // walk/row 相位随机偏移
        public float idleSeed;   // Idle 层呼吸/Perlin 种子

        // —— 以下字段仅出击小队组使用（独立速度估计与权重状态）——
        public Vector3 lastPosition;
        public bool hasLastPosition;
        public float selfSpeed;
        public float walkPhase;
        public float walkWeight;
        public float idleWeight = 1f;
        /// <summary>小队根上的水面状态（坐船时不迈步，改站立呼吸）。</summary>
        public LycheeSquadWaterState waterState;
    }

    sealed class WrapperPose
    {
        public Transform transform;
        public Vector3 basePosition;
        public Quaternion baseRotation;
        public float seed;
        public bool hasBones;     // 该 wrapper 已被采为骨骼组：弹跳减半，避免与骨骼步行的 Hips 起伏双重叠加

        // —— 以下字段仅 Carriage 使用：程序化假车轮与车身动态 ——
        public readonly List<Transform> wheels = new List<Transform>();
        public float wheelRadius;
        public float wheelSpin;   // 车轮累计转角（度，绕车轴）
        public bool wheelsHidden; // 水路换船期间轮子整体隐藏
        public float lastSpeed;   // 上帧速度缓存，用于加减速点头
        public float pitchOffset; // 点头偏移（度），由速度变化率注入后衰减回零

        // —— 以下字段同样仅 Carriage 使用：马拉车（player.horseModel 非空时 AttachHorse 挂载）——
        public Transform horse;              // 马实例根（carriage wrapper 子物体，跟车走）
        public Vector3 horseBasePosition;    // 马基准局部位姿：程序化 bob/还原都以此为锚，绝不累积漂移
        public Quaternion horseBaseRotation;
        public bool horseHidden;             // 水路换船期间整马隐藏（与 wheelsHidden 同款开关同款时机）
        public Animator horseAnimator;
        public PlayableGraph horseGraph;
        public bool horseGraphValid;
        public AnimationClipPlayable horsePlayable; // 留引用以便每帧按 rig.speed 调播放速率/手动回卷循环

        // —— 组合马车（horse_new.glb 马+车一体）专用：模型自带骨骼但无动画 clip，
        //    程序化驱动 4 条腿做行走摆动（车轮焊在蒙皮网格里无法转，故只动腿+车身轻晃）——
        public List<HorseLeg> horseLegs;     // 运行时按蹄子世界高度认出的 4 条腿；null=非组合模型或未识别到
        public float horseWalkPhase;         // 腿摆相位累加器，随 rig.speed 推进（同假车轮的 wheelSpin 思路）
    }

    /// <summary>一条腿的程序化摆动数据：上段（髋/肩）+ 中段（膝）的绑定姿态与摆动轴，相位区分四蹄步态。</summary>
    sealed class HorseLeg
    {
        public Transform upper;            // 上段骨（髋/肩，摆幅最大）
        public Transform mid;              // 中段骨（膝，抬腿时附加弯曲）
        public Quaternion upperBind;       // 绑定局部旋转（摆动以此为锚不漂移）
        public Quaternion midBind;
        public Vector3 swingAxisUpper;     // 上段局部空间内"前后摆"的旋转轴
        public Vector3 swingAxisMid;
        public float phase;                // 步态相位偏移（对角腿同相，0 或 0.5）
    }

    sealed class CharacterRig
    {
        public int playerId;
        public Transform root;
        public Transform captainWrapper;
        public BoneGroup captainGroup;                                      // Captain 骨骼组（Action/Playables 只作用于它）
        public readonly List<BoneGroup> boneGroups = new List<BoneGroup>(); // Captain + 采到骨骼的 TeamMember_*

        public readonly List<WrapperPose> members = new List<WrapperPose>();
        public WrapperPose carriage;
        public WrapperPose boat;

        public Vector3 lastPosition;
        public bool hasLastPosition;
        public float speed;
        public bool water;
        public float waterBlend;
        public float moveBlend;
        public float walkPhase;
        public float rowPhase;
        public float idleSeed;

        public float walkWeight;
        public float rowWeight;
        public float idleWeight = 1f;

        public LycheeCharacterActionKind actionKind;
        public float actionStart = -999f;
        public float actionEnd = -999f;
        public float actionWeight;
        public bool actionFromContest;

        // Playables 升级钩子
        public Animator animator;
        public PlayableGraph graph;
        public bool graphValid;
        public int playingState = -1; // 0=walk 1=row 2=idle

        // 将军（取代队长）：非 Mixamo 的将军模型自带 walking clip，走独立 Playables 播放（speed 同步）。
        // captainGroup 非空（Mixamo 占位队长）时此路不启用，仍走原程序化骨骼驱动。
        public int camp;
        public AnimationClip generalWalkClip;     // 该队将军行走 clip；空=占位队长或无 clip
        public AnimationClip generalAttackClip;   // 该队将军攻击 clip；对抗期间切到它，空则对抗时保持走路
        public bool generalAttacking;             // 对抗中（WINDOW_CONTEST 期间）：true 时播攻击 clip
        public AnimationClip generalActiveClip;   // 当前 Playables 正在播的 clip，用于检测走路↔攻击切换需重建图
        public Transform generalWrapper;          // = captainWrapper，便于语义清晰
        public Animator generalAnimator;
        public PlayableGraph generalGraph;
        public bool generalGraphValid;
        public AnimationClipPlayable generalPlayable;
    }

    static LycheeCharacterMotion instance;

    /// <summary>
    /// 车轮基准朝向：圆柱主轴(Y)绕 Z 转 -90° 后指向父空间 +X（车侧向），压扁后轮面正好朝外；
    /// 在此基础上绕自身 Y 累加 spin 即为绕车轴滚动，正角对应车队前进（+Z）方向的滚动。
    /// </summary>
    static readonly Quaternion WheelBaseRotation = Quaternion.Euler(0f, 0f, -90f);
    static Material wheelSharedMaterial; // 全部假车轮共用一份深灰材质，避免重复实例化

    LycheeReplayPlayer player;
    readonly List<CharacterRig> rigs = new List<CharacterRig>();
    readonly List<BoneGroup> squadGroups = new List<BoneGroup>(); // 出击小队的临时骨骼组（销毁后静默剔除）
    bool built;
    bool disabledApplied;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Disabled) return;
        LycheeReplayPlayer host = FindFirstObjectByType<LycheeReplayPlayer>();
        if (host == null) return;
        if (host.GetComponent<LycheeCharacterMotion>() != null) return;
        host.gameObject.AddComponent<LycheeCharacterMotion>();
    }

    /// <summary>
    /// 让指定车队的 Captain 播放一个覆盖动作，时长结束后自动回到 Walk/Row/Idle 状态机。
    /// 任何缺失（系统未就绪 / playerId 不存在 / Captain 无骨骼）都静默 no-op。
    /// </summary>
    public static void PlayAction(int playerId, LycheeCharacterActionKind kind, float durationSeconds)
    {
        if (Disabled) return;
        LycheeCharacterMotion system = instance;
        if (system == null) return;
        system.StartAction(playerId, kind, durationSeconds, false);
    }

    /// <summary>
    /// 出击小队生成通知（由 LycheeReplayPlayer.SpawnSquad 调用）：扫描 squadRoot 下名为 Member_* 的成员 wrapper
    /// （每个下面是 Member.glb 实例）采集骨骼组并加入驱动列表。
    /// 实例不存在 / 全局禁用 / 传 null 都安全空操作；无骨骼的成员静默跳过（继续走 wrapper 级演出兜底）。
    /// </summary>
    public static void NotifySquadSpawned(GameObject squadRoot)
    {
        if (Disabled || squadRoot == null) return;
        LycheeCharacterMotion system = instance;
        if (system == null) return;
        system.CollectSquadGroups(squadRoot.transform);
    }

    void Awake()
    {
        player = GetComponent<LycheeReplayPlayer>();
        if (instance == null) instance = this;
    }

    void OnEnable()
    {
        LycheeReplayPlayer.ReplayEventRaised += OnReplayEvent;
        LycheeReplayPlayer.RoundApplied += OnRoundApplied;
    }

    void OnDisable()
    {
        LycheeReplayPlayer.ReplayEventRaised -= OnReplayEvent;
        LycheeReplayPlayer.RoundApplied -= OnRoundApplied;
    }

    IEnumerator Start()
    {
        // 延迟一帧，确保 LycheeReplayPlayer.Awake 已建好车队（参考 LycheeConvoyVfx 的等待写法）
        yield return null;
        if (player == null) player = GetComponent<LycheeReplayPlayer>();
        if (player == null) yield break;
        for (int i = 0; i < 120 && player != null && player.CurrentFrame == null; i++)
            yield return null;
        // 再等车队根节点就绪（最多约 2 秒）
        for (int i = 0; i < 120 && !ConvoyRootsReady(); i++)
            yield return null;
        BuildRigs();
    }

    void OnDestroy()
    {
        LycheeReplayPlayer.ReplayEventRaised -= OnReplayEvent;
        LycheeReplayPlayer.RoundApplied -= OnRoundApplied;
        foreach (CharacterRig rig in rigs)
        {
            ReleasePlayable(rig);
            ReleaseGeneralClip(rig);
            if (rig != null) ReleaseHorsePlayable(rig.carriage);
        }
        rigs.Clear();
        squadGroups.Clear();
        if (instance == this) instance = null;
    }

    bool ConvoyRootsReady()
    {
        if (player == null) return false;
        LycheeReplayFrame frame = player.CurrentFrame;
        if (frame == null || frame.players == null || frame.players.Length == 0) return false;
        foreach (LycheeReplayPlayerState state in frame.players)
        {
            if (state == null) continue;
            if (player.GetConvoyRoot(state.playerId) != null) return true;
        }
        return false;
    }

    // ---------- 装配 ----------

    void BuildRigs()
    {
        if (built || player == null) return;
        LycheeReplayFrame frame = player.CurrentFrame;
        if (frame == null || frame.players == null || frame.players.Length == 0) return;

        foreach (LycheeReplayPlayerState state in frame.players)
        {
            if (state == null) continue;
            if (FindRig(state.playerId) != null) continue;
            Transform root = player.GetConvoyRoot(state.playerId);
            if (root == null) continue;

            CharacterRig rig = new CharacterRig
            {
                playerId = state.playerId,
                root = root,
                camp = state.camp,
                idleSeed = state.playerId * 2.13f + 0.7f,
                lastPosition = root.position,
                hasLastPosition = true,
                water = IsWaterEdge(state.routeEdgeId)
            };
            rig.waterBlend = rig.water ? 1f : 0f;

            rig.captainWrapper = root.Find("Captain"); // wrapper 名仍叫 Captain，但内部可能是将军模型
            rig.generalWrapper = rig.captainWrapper;
            // 将军模型自带 walking clip（GetGeneralWalkClip 仅在该队将军模型已赋值时返回非空）→ 强制走
            // Playables 播放，绝不采 Mixamo 骨骼组：赤龙将军骨骼名与 Mixamo 列表大量重合会被误采、
            // 导致程序化驱动抢占而 clip 不播。占位队长（captainModel）或将军无 clip 才走原程序化骨骼。
            AnimationClip generalClip = player != null ? player.GetGeneralWalkClip(state.camp) : null;
            if (generalClip != null)
            {
                rig.generalWalkClip = generalClip;
                rig.generalAttackClip = player.GetGeneralAttackClip(state.camp);
            }
            else
            {
                // Captain 组相位种子取 0：保持与历史版本完全一致的步伐基准，错相位交给队员组
                rig.captainGroup = CollectBoneGroup(rig.captainWrapper, 0f, rig.idleSeed);
                if (rig.captainGroup != null) rig.boneGroups.Add(rig.captainGroup);
            }
            CollectWrappers(rig);
            rigs.Add(rig);
        }
        built = rigs.Count > 0;
    }

    /// <summary>
    /// 从 wrapper 下的蒙皮网格采集一套骨骼组：优先按 SkinnedMeshRenderer.bones 精确匹配，缺失时 FindDeep 兜底。
    /// 基准 localRotation 在采集瞬间记录——出击小队成员此时可能已被 AnimateSquadMembers 摆过 wrapper 级姿态，
    /// 但骨骼本身仍是绑定姿势，记录值即正确基准。一根骨骼都没采到时返回 null（无骨骼模型走 wrapper 弹跳兜底）。
    /// </summary>
    static BoneGroup CollectBoneGroup(Transform wrapper, float phaseSeed, float idleSeed)
    {
        if (wrapper == null) return null;
        BoneGroup group = new BoneGroup
        {
            wrapper = wrapper,
            phaseSeed = phaseSeed,
            idleSeed = idleSeed,
            lastPosition = wrapper.position,
            hasLastPosition = true
        };
        SkinnedMeshRenderer skin = wrapper.GetComponentInChildren<SkinnedMeshRenderer>(true);
        foreach (string boneName in BoneNames)
        {
            Transform bone = null;
            if (skin != null && skin.bones != null)
            {
                foreach (Transform candidate in skin.bones)
                {
                    if (candidate != null && candidate.name == boneName)
                    {
                        bone = candidate;
                        break;
                    }
                }
            }
            if (bone == null) bone = FindDeep(wrapper, boneName);
            if (bone == null) continue;
            group.bones[boneName] = new BonePose
            {
                transform = bone,
                baseRotation = bone.localRotation
            };
        }
        if (group.bones.Count == 0) return null;
        if (group.bones.TryGetValue("Hips", out BonePose hipsPose))
        {
            group.hips = hipsPose.transform;
            group.hipsBasePosition = hipsPose.transform.localPosition;
        }
        return group;
    }

    void CollectWrappers(CharacterRig rig)
    {
        if (rig == null || rig.root == null) return;
        for (int i = 0; i < rig.root.childCount; i++)
        {
            Transform child = rig.root.GetChild(i);
            if (child == null) continue;
            if (child.name.StartsWith("TeamMember_", System.StringComparison.Ordinal))
            {
                WrapperPose pose = new WrapperPose
                {
                    transform = child,
                    basePosition = child.localPosition,
                    baseRotation = child.localRotation,
                    seed = (rig.members.Count + 1) * 1.7f
                };
                // 队员模型换成带 Mixamo 骨骼的 Member.glb（骨骼名与 Captain 完全一致）后这里能采到骨骼组；
                // 相位种子随机错开各队员步伐。仍是旧无骨骼模型时返回 null，保持 wrapper 弹跳兜底。
                BoneGroup group = CollectBoneGroup(child, Random.Range(0f, TwoPi), rig.idleSeed + pose.seed);
                if (group != null)
                {
                    pose.hasBones = true;
                    rig.boneGroups.Add(group);
                }
                rig.members.Add(pose);
            }
            else if (child.name == "Carriage")
            {
                rig.carriage = new WrapperPose
                {
                    transform = child,
                    basePosition = child.localPosition,
                    baseRotation = child.localRotation,
                    seed = rig.idleSeed
                };
                // 组合马车（horse_new.glb：马+车一体绑骨，自带 SkinnedMeshRenderer）自含马匹与车轮，
                // 程序化假车轮和独立挂马都要让位，否则双马+轮子穿模
                bool combinedCarriage = child.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
                if (!combinedCarriage)
                {
                    // Carriage.glb 车轮已烘进单一网格无法按节点旋转，程序化补 4 个假车轮（Boat 不需要轮子）
                    CreateCarriageWheels(rig.carriage);
                    // 马拉车：horseModel 已接线（Horse.glb 落地且编辑器脚本装配过）才挂，否则静默无马
                    AttachHorse(rig.carriage);
                }
                else if (child.childCount > 0)
                {
                    // 组合马车：把整个车体实例当"马"接入既有 Playables 管线——
                    // horseWalkClip（由 CarriageSwap 从 horse_new.glb 挑选）可用时播骨骼行走，
                    // 无 clip 时落到腿部程序化兜底（见 CaptureHorseLegs / DriveHorseLegs）
                    Transform combinedRoot = child.GetChild(0);
                    rig.carriage.horse = combinedRoot;
                    rig.carriage.horseBasePosition = combinedRoot.localPosition;
                    rig.carriage.horseBaseRotation = combinedRoot.localRotation;
                    // 先把车体朝向自动转正（对齐车队前进方向），再识别四腿做程序化行走
                    // （有动画 clip 则不识别腿，让 Playables 接管）
                    AlignCombinedCarriage(child, combinedRoot, rig.root);
                    if (player == null || player.horseWalkClip == null)
                        CaptureHorseLegs(rig.carriage, child);
                }
            }
            else if (child.name == "Boat")
            {
                rig.boat = new WrapperPose
                {
                    transform = child,
                    basePosition = child.localPosition,
                    baseRotation = child.localRotation,
                    seed = rig.idleSeed + 3.3f
                };
            }
        }
    }

    /// <summary>
    /// 把 subtree 下所有 Renderer 的世界包围盒 8 角点逐一变换进 space 局部空间统计合并包围盒：
    /// 直接用世界 AABB 在车队旋转后会虚胖，估出的轮位/马位会偏，必须走局部空间（假车轮与马共用）。
    /// </summary>
    static bool TryGetLocalBounds(Transform space, Transform subtree, out Bounds local)
    {
        local = new Bounds();
        if (space == null || subtree == null) return false;
        Renderer[] renderers = subtree.GetComponentsInChildren<Renderer>(true);
        if (renderers == null || renderers.Length == 0) return false;
        bool hasBounds = false;
        foreach (Renderer renderer in renderers)
        {
            if (renderer == null) continue;
            Bounds world = renderer.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 point = space.InverseTransformPoint(new Vector3(
                    (corner & 1) == 0 ? world.min.x : world.max.x,
                    (corner & 2) == 0 ? world.min.y : world.max.y,
                    (corner & 4) == 0 ? world.min.z : world.max.z));
                if (!hasBounds)
                {
                    local = new Bounds(point, Vector3.zero);
                    hasBounds = true;
                }
                else
                {
                    local.Encapsulate(point);
                }
            }
        }
        return hasBounds;
    }

    /// <summary>
    /// 给 Carriage wrapper 程序化补 4 个假车轮：压扁圆柱放在包围盒底部四角内收处，
    /// 滚动角由 UpdateWrappers 按 rig.speed / 轮半径 累加。
    /// </summary>
    void CreateCarriageWheels(WrapperPose carriage)
    {
        if (carriage == null || carriage.transform == null) return;
        Transform wrapper = carriage.transform;
        if (!TryGetLocalBounds(wrapper, wrapper, out Bounds local)) return;

        float radius = local.size.y * 0.25f; // 轮半径按车身高度估：约车高的四分之一
        if (radius < 0.02f) return;          // 包围盒异常小（模型还没就绪）就放弃，避免生成肉眼不可见的碎轮
        carriage.wheelRadius = radius;

        // 底部四角内收：X 收到 78%（轮面贴住车帮不悬空），Z 收到 60%（前后轴距留出车辕）
        float offsetX = Mathf.Max(0.01f, local.extents.x * 0.78f);
        float offsetZ = Mathf.Max(0.01f, local.extents.z * 0.60f);
        float wheelY = local.min.y + radius; // 轮心抬高一个半径，轮底正好齐车底/地面
        Material material = GetWheelMaterial();

        for (int i = 0; i < 4; i++)
        {
            GameObject wheel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            wheel.name = "FakeWheel_" + i;
            // CreatePrimitive 自带 CapsuleCollider；模型其余 Collider 全被 CreateNormalizedModel 禁用，
            // 这里必须销毁，否则车轮会成为场景里唯一可被射线/物理命中的部件
            Collider collider = wheel.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
            Transform wheelTransform = wheel.transform;
            wheelTransform.SetParent(wrapper, false);
            wheelTransform.localPosition = new Vector3(
                (i & 1) == 0 ? -offsetX : offsetX,
                wheelY,
                (i & 2) == 0 ? offsetZ : -offsetZ);
            wheelTransform.localRotation = WheelBaseRotation;
            // 圆柱默认主轴为 Y：直径=2r、厚 0.1（scaleY 是半高），绕 Z 转 -90° 后轮面朝向车侧
            wheelTransform.localScale = new Vector3(radius * 2f, 0.05f, radius * 2f);
            if (material != null)
            {
                Renderer wheelRenderer = wheel.GetComponent<Renderer>();
                if (wheelRenderer != null) wheelRenderer.sharedMaterial = material;
            }
            carriage.wheels.Add(wheelTransform);
        }
    }

    static Material GetWheelMaterial()
    {
        if (wheelSharedMaterial != null) return wheelSharedMaterial;
        // Shader.Find 在 WebGL 同样可用：URP/Lit 被场景大量材质引用，必然已打进包
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) return null; // 找不到就保留圆柱默认材质，宁可颜色不对也不能粉紫
        wheelSharedMaterial = new Material(shader);
        wheelSharedMaterial.color = new Color(0.16f, 0.14f, 0.12f, 1f); // 深灰木轮色（URP/Lit 的 MainColor 即 _BaseColor）
        if (wheelSharedMaterial.HasProperty("_Smoothness"))
            wheelSharedMaterial.SetFloat("_Smoothness", 0.25f);
        return wheelSharedMaterial;
    }

    /// <summary>
    /// 给 Carriage wrapper 在车头前方挂一匹马（马拉车）：player.horseModel 为空时整个特性静默不存在。
    /// 归一是小型版 CreateNormalizedModel（player 里的私有方法做不了"对齐到车头"，自做一份）：
    /// 实例化 → 合并包围盒最大边缩放到 ~1.7m → 重采包围盒再定位（两段式免去对模型 pivot/旋转的任何假设）
    /// → 销毁 Collider（演出物体不参与物理/射线，与假车轮同理）。
    /// 车头位置：wrapper 局部 +Z 是车队前进方向（朝向由车队 root 旋转决定），
    /// 马中心 Z = 车包围盒前缘 + 马身长一半，首尾相接像被车辕牵着；底部对齐车底贴地。
    /// 注意必须先采车包围盒再实例化马，否则马自己会污染"车头前缘"的估计。
    /// </summary>
    void AttachHorse(WrapperPose carriage)
    {
        if (carriage == null || carriage.transform == null) return;
        if (player == null || player.horseModel == null) return;
        Transform wrapper = carriage.transform;
        if (!TryGetLocalBounds(wrapper, wrapper, out Bounds carriageBounds)) return;

        GameObject horse = Instantiate(player.horseModel, wrapper);
        horse.name = "Horse";
        if (!TryGetLocalBounds(wrapper, horse.transform, out Bounds horseBounds))
        {
            Destroy(horse); // 无渲染器的空模型：挂着也看不见，干脆不挂
            return;
        }
        float largest = Mathf.Max(horseBounds.size.x, horseBounds.size.y, horseBounds.size.z);
        if (largest > 0.0001f) horse.transform.localScale *= HorseTargetSize / largest;
        if (!TryGetLocalBounds(wrapper, horse.transform, out horseBounds))
        {
            Destroy(horse);
            return;
        }
        // localPosition 增量即 wrapper 空间位移：横向居中、底部对齐车底、纵向推到车头前缘 + 半身长
        horse.transform.localPosition += new Vector3(
            -horseBounds.center.x,
            carriageBounds.min.y - horseBounds.min.y,
            carriageBounds.max.z + horseBounds.extents.z - horseBounds.center.z);
        foreach (Collider collider in horse.GetComponentsInChildren<Collider>(true))
            Destroy(collider);

        carriage.horse = horse.transform;
        carriage.horseBasePosition = horse.transform.localPosition;
        carriage.horseBaseRotation = horse.transform.localRotation;
        // Playables 图不在这里建：UpdateWrappers 每帧懒建/自愈（Disabled 切换销毁后能自动恢复）
    }

    /// <summary>
    /// 给马建/重建 Playables 行走图（参考 EnsurePlayable 的 walk clip 写法）。
    /// Animator 找根上现成的（glTFast 可能已带），没有再补——clip 曲线路径相对 GLB 根，
    /// Animator 挂深层节点会绑不上骨骼，所以不用 GetComponentInChildren。
    /// </summary>
    void EnsureHorsePlayable(WrapperPose carriage, AnimationClip clip)
    {
        if (carriage == null || carriage.horse == null || clip == null) return;
        if (carriage.horseAnimator == null)
        {
            carriage.horseAnimator = carriage.horse.GetComponent<Animator>();
            if (carriage.horseAnimator == null)
                carriage.horseAnimator = carriage.horse.gameObject.AddComponent<Animator>();
        }
        if (carriage.horseAnimator == null) return;
        carriage.horsePlayable = AnimationPlayableUtilities.PlayClip(
            carriage.horseAnimator, clip, out carriage.horseGraph);
        carriage.horseGraphValid = carriage.horseGraph.IsValid();
    }

    /// <summary>销毁马的 Playables 图（Disabled / OnDestroy 清理；重新启用后 UpdateWrappers 下一帧自动重建）。</summary>
    static void ReleaseHorsePlayable(WrapperPose carriage)
    {
        if (carriage == null) return;
        if (carriage.horseGraphValid && carriage.horseGraph.IsValid()) carriage.horseGraph.Destroy();
        carriage.horseGraphValid = false;
    }

    /// <summary>
    /// 把组合马车自动转正：AI 导出的模型正面朝向不定（实测 horse_new 头朝局部 +X），
    /// 与车队前进方向（convoy 根 +Z）不一致就会"横着走"。这里量出马头方向自动对齐，
    /// 根除手猜偏航角——马头方向 = 蹄群质心 → 离质心水平最远的骨（马颈/头总比尾巴伸得远）。
    /// 把转正量加到 Carriage 包装(child)的局部旋转上（child 是 convoy 根的直接子物体，会随车队转向）。
    /// </summary>
    static void AlignCombinedCarriage(Transform carriageWrapper, Transform combinedRoot, Transform convoyRoot)
    {
        if (carriageWrapper == null || combinedRoot == null || convoyRoot == null) return;
        var skinned = combinedRoot.GetComponentInChildren<SkinnedMeshRenderer>(true);
        if (skinned == null || skinned.bones == null || skinned.bones.Length < 6) return;
        Transform[] bones = skinned.bones;

        // 4 个最低骨=四蹄，求其质心（身体中心的水平位置）
        var ordered = new List<Transform>(bones);
        ordered.Sort((a, b) =>
        {
            if (a == null) return 1; if (b == null) return -1;
            return a.position.y.CompareTo(b.position.y);
        });
        Vector3 centroid = Vector3.zero; int taken = 0;
        foreach (Transform t in ordered)
        {
            if (t == null) continue;
            centroid += t.position; taken++;
            if (taken == 4) break;
        }
        if (taken < 4) return;
        centroid /= taken;

        // 马头 = 离蹄群质心水平距离最远的骨
        Transform head = null; float far = -1f;
        foreach (Transform b in bones)
        {
            if (b == null) continue;
            Vector3 d = b.position - centroid; d.y = 0f;
            if (d.sqrMagnitude > far) { far = d.sqrMagnitude; head = b; }
        }
        if (head == null) return;

        Vector3 headWorld = head.position - centroid; headWorld.y = 0f;
        if (headWorld.sqrMagnitude < 1e-6f) return;
        // 换到 convoy 根局部空间求与 +Z 的夹角，加到 child 局部旋转上（绕局部 Y）
        Vector3 headLocal = convoyRoot.InverseTransformDirection(headWorld.normalized);
        headLocal.y = 0f;
        if (headLocal.sqrMagnitude < 1e-6f) return;
        float yaw = Vector3.SignedAngle(headLocal.normalized, Vector3.forward, Vector3.up);
        carriageWrapper.localRotation = Quaternion.AngleAxis(yaw, Vector3.up) * carriageWrapper.localRotation;
    }

    /// <summary>
    /// 识别组合马车（horse_new.glb）的 4 条腿，建程序化行走数据。模型骨骼名是通用的
    /// （UniRig 自动绑定 Bone_xxx），不能按名字找腿，改按"绑定姿势下 4 个最低骨=四蹄"反推：
    /// 蹄→祖父级=上段腿骨。再用前后/左右象限分配对角步态相位（对角腿同相，像真马慢步）。
    /// 摆动轴 = 马身横向轴（左右）变换到各骨局部空间——绕它转即前后甩腿。
    /// </summary>
    static void CaptureHorseLegs(WrapperPose carriage, Transform combinedWrapper)
    {
        if (carriage == null || combinedWrapper == null) return;
        var skinned = combinedWrapper.GetComponentInChildren<SkinnedMeshRenderer>(true);
        if (skinned == null || skinned.bones == null || skinned.bones.Length < 6) return;
        Transform[] bones = skinned.bones;

        // 1. 找 4 个最低骨（世界 Y 最低）= 四蹄
        var ordered = new List<Transform>(bones);
        ordered.Sort((a, b) =>
        {
            if (a == null) return 1; if (b == null) return -1;
            return a.position.y.CompareTo(b.position.y);
        });
        var hooves = new List<Transform>();
        foreach (Transform t in ordered)
        {
            if (t == null || t.parent == null || t.parent.parent == null) continue;
            hooves.Add(t);
            if (hooves.Count == 4) break;
        }
        if (hooves.Count < 4) return;

        // 2. 马身前向 = 前蹄群中心 → 由整体蹄群质心指向最远蹄的水平方向（足够区分前后/左右即可）
        Vector3 centroid = Vector3.zero;
        foreach (Transform h in hooves) centroid += h.position;
        centroid /= hooves.Count;
        // 用蹄子在水平面的主轴当身体前向：取离质心最远两蹄连线方向
        Vector3 forward = Vector3.forward;
        float maxd = 0f;
        for (int i = 0; i < hooves.Count; i++)
            for (int j = i + 1; j < hooves.Count; j++)
            {
                Vector3 d = hooves[i].position - hooves[j].position;
                d.y = 0f;
                if (d.sqrMagnitude > maxd) { maxd = d.sqrMagnitude; forward = d.normalized; }
            }
        Vector3 lateral = Vector3.Cross(Vector3.up, forward).normalized; // 横向（左右）轴=摆动轴

        carriage.horseLegs = new List<HorseLeg>(4);
        foreach (Transform hoof in hooves)
        {
            Transform mid = hoof.parent;
            Transform upper = mid.parent;
            if (upper == null) continue;
            // 象限：沿 forward 投影分前后、沿 lateral 投影分左右；对角腿（前左+后右 / 前右+后左）同相
            Vector3 rel = hoof.position - centroid;
            bool front = Vector3.Dot(rel, forward) >= 0f;
            bool left = Vector3.Dot(rel, lateral) >= 0f;
            float phase = (front == left) ? 0f : 0.5f;
            carriage.horseLegs.Add(new HorseLeg
            {
                upper = upper,
                mid = mid,
                upperBind = upper.localRotation,
                midBind = mid.localRotation,
                swingAxisUpper = upper.InverseTransformDirection(lateral),
                swingAxisMid = mid.InverseTransformDirection(lateral),
                phase = phase
            });
        }
        if (carriage.horseLegs.Count < 4) carriage.horseLegs = null; // 识别不全就放弃，回退静态
    }

    /// <summary>程序化驱动四腿行走：上段前后摆 ±幅度、中段在抬腿半程附加弯曲，整体幅度随车速。</summary>
    static void DriveHorseLegs(WrapperPose carriage, float speed)
    {
        if (carriage == null || carriage.horseLegs == null) return;
        // 速度→幅度：静止收敛到 0（站立），行进抬到满幅；步频也随速度（像真马越快步越急）
        float gait = Mathf.Clamp01(speed / 2.2f);
        float swingAmp = 17f * gait;           // 上段最大摆角（度）
        float kneeAmp = 14f * gait;            // 中段抬腿弯曲（度）
        foreach (HorseLeg leg in carriage.horseLegs)
        {
            if (leg.upper == null) continue;
            float ph = carriage.horseWalkPhase + leg.phase * TwoPi;
            float swing = Mathf.Sin(ph) * swingAmp;
            leg.upper.localRotation = leg.upperBind * Quaternion.AngleAxis(swing, leg.swingAxisUpper);
            if (leg.mid != null)
            {
                // 腿向前摆（sin>0）时膝盖抬起弯曲，向后蹬地时伸直
                float knee = Mathf.Max(0f, Mathf.Sin(ph)) * kneeAmp;
                leg.mid.localRotation = leg.midBind * Quaternion.AngleAxis(knee, leg.swingAxisMid);
            }
        }
    }

    /// <summary>还原四腿到绑定姿态（水路隐藏/Disabled 时调，防止停在半摆姿势）。</summary>
    static void RestoreHorseLegs(WrapperPose carriage)
    {
        if (carriage == null || carriage.horseLegs == null) return;
        foreach (HorseLeg leg in carriage.horseLegs)
        {
            if (leg.upper != null) leg.upper.localRotation = leg.upperBind;
            if (leg.mid != null) leg.mid.localRotation = leg.midBind;
        }
    }

    /// <summary>扫描出击小队根下的 Member_* wrapper（Member.glb 实例）采集骨骼组，加入临时驱动列表。</summary>
    void CollectSquadGroups(Transform squadRoot)
    {
        if (squadRoot == null) return;
        for (int i = 0; i < squadRoot.childCount; i++)
        {
            Transform child = squadRoot.GetChild(i);
            if (child == null) continue;
            if (!child.name.StartsWith("Member_", System.StringComparison.Ordinal)) continue;
            // 基准姿态在采集瞬间记录；AnimateSquadMembers 只动 wrapper 级 transform，
            // 骨骼摆动与其行军起伏/攻击突刺叠加是预期效果，互不覆盖
            BoneGroup group = CollectBoneGroup(child, Random.Range(0f, TwoPi), Random.Range(0f, 10f));
            if (group != null)
            {
                group.waterState = squadRoot.GetComponent<LycheeSquadWaterState>();
                squadGroups.Add(group);
            }
        }
    }

    static Transform FindDeep(Transform root, string name)
    {
        if (root == null) return null;
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindDeep(root.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }

    CharacterRig FindRig(int playerId)
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

    // ---------- 事件联动 ----------

    void OnRoundApplied(LycheeReplayFrame previous, LycheeReplayFrame current)
    {
        if (current == null || current.players == null) return;
        foreach (LycheeReplayPlayerState state in current.players)
        {
            if (state == null) continue;
            CharacterRig rig = FindRig(state.playerId);
            if (rig == null) continue;
            rig.water = IsWaterEdge(state.routeEdgeId);
        }
        // 兜底：SeekFrame 跳过 CONTEST_END 时，依据帧数据没有进行中的对抗就结束持续 Attack
        if (current.contests == null || current.contests.Length == 0)
        {
            foreach (CharacterRig rig in rigs)
            {
                if (rig != null && rig.actionFromContest && Time.time < rig.actionEnd)
                {
                    rig.actionEnd = Time.time;
                    rig.actionFromContest = false;
                }
            }
        }
    }

    void OnReplayEvent(LycheeReplayMessage message, int round)
    {
        if (Disabled || message == null || string.IsNullOrEmpty(message.type)) return;
        string type = message.type;
        LycheeReplayPayload payload = message.payload;
        int payloadPlayer = payload != null ? payload.playerId : 0;

        if (type == "GUARD_SET")
        {
            StartAction(payloadPlayer, LycheeCharacterActionKind.Build, 3f, false);
            return;
        }
        if (type.Contains("GUARD_BREAK") || type.Contains("BREAK_GUARD") ||
            (payload != null && payload.action == "BREAK_GUARD"))
        {
            StartAction(payloadPlayer, LycheeCharacterActionKind.Attack, 2.5f, false);
            return;
        }
        if (type == "WINDOW_CONTEST_START")
        {
            foreach (CharacterRig rig in rigs)
            {
                if (rig == null) continue;
                StartAction(rig.playerId, LycheeCharacterActionKind.Attack, ContestHoldSeconds, true); // Mixamo 占位队长
                rig.generalAttacking = true;                                                            // 将军切挥剑 clip
            }
            return;
        }
        if (type == "WINDOW_CONTEST_END")
        {
            foreach (CharacterRig rig in rigs)
            {
                if (rig == null) continue;
                if (rig.actionFromContest)
                {
                    rig.actionEnd = Time.time;
                    rig.actionFromContest = false;
                }
                rig.generalAttacking = false; // 将军切回走路
            }
            return;
        }
        if (type == "DELIVER_SUCCESS")
        {
            StartAction(payloadPlayer, LycheeCharacterActionKind.Cheer, 4f, false);
            return;
        }
        if (type == "VERIFY_GATE_COMPLETE")
        {
            StartAction(payloadPlayer, LycheeCharacterActionKind.Cheer, 2f, false);
        }
    }

    void StartAction(int playerId, LycheeCharacterActionKind kind, float durationSeconds, bool fromContest)
    {
        CharacterRig rig = FindRig(playerId);
        if (rig == null) return;
        rig.actionKind = kind;
        rig.actionStart = Time.time;
        rig.actionEnd = Time.time + Mathf.Max(0.05f, durationSeconds);
        rig.actionFromContest = fromContest;
    }

    // ---------- 主驱动 ----------

    void LateUpdate()
    {
        // 小队骨骼组可能先于车队 rig 就绪（理论上不会，但守住）
        if (!built && squadGroups.Count == 0) return;

        if (Disabled)
        {
            if (!disabledApplied)
            {
                disabledApplied = true;
                foreach (CharacterRig rig in rigs)
                {
                    RestoreBasePose(rig);
                    ReleasePlayable(rig);
                    ReleaseGeneralClip(rig);
                    if (rig != null) ReleaseHorsePlayable(rig.carriage); // 重新启用后 UpdateWrappers 懒重建
                }
                foreach (BoneGroup group in squadGroups)
                    RestoreGroupPose(group);
            }
            return;
        }
        disabledApplied = false;

        float deltaTime = Time.deltaTime;
        if (deltaTime <= 0f) return;
        foreach (CharacterRig rig in rigs)
            UpdateRig(rig, deltaTime);
        UpdateSquadGroups(deltaTime);
    }

    /// <summary>
    /// 驱动出击小队骨骼组：每组用 wrapper 世界位移自估速度（小队没有车队 rig.speed 可用），
    /// 速度超阈值播 Walk 摆动、否则 Idle 轻微呼吸。小队抵达约 2.4 秒后被整体 Destroy，
    /// wrapper 变 Unity 假 null 的组在此静默剔除（倒序遍历安全 RemoveAt）。
    /// </summary>
    void UpdateSquadGroups(float deltaTime)
    {
        for (int i = squadGroups.Count - 1; i >= 0; i--)
        {
            BoneGroup group = squadGroups[i];
            if (group == null || !group.wrapper) // !wrapper 同时覆盖真 null 与 Destroy 后的 Unity 假 null
            {
                squadGroups.RemoveAt(i);
                continue;
            }

            // 自身速度估计：水平帧间位移 / deltaTime 再指数平滑，与 UpdateRig 的车队估速同款写法
            Vector3 position = group.wrapper.position;
            if (group.hasLastPosition)
            {
                Vector3 delta = position - group.lastPosition;
                delta.y = 0f;
                float raw = delta.magnitude / deltaTime;
                if (raw > TeleportSpeedLimit) raw = 0f; // SeekFrame 瞬移保护
                group.selfSpeed = Mathf.Lerp(group.selfSpeed, raw, 1f - Mathf.Exp(-12f * deltaTime));
            }
            group.lastPosition = position;
            group.hasLastPosition = true;

            // 坐船跨河时不迈步：哪怕船在滑行（selfSpeed 高），人物也应站立呼吸而不是水上漂步
            bool onBoat = group.waterState != null && group.waterState.onWater;
            bool moving = !onBoat && group.selfSpeed > SquadMoveSpeedThreshold;
            float fade = deltaTime / WeightFadeSeconds;
            group.walkWeight = Mathf.MoveTowards(group.walkWeight, moving ? 1f : 0f, fade);
            group.idleWeight = Mathf.MoveTowards(group.idleWeight, moving ? 0f : 1f, fade);
            float stepHz = Mathf.Clamp(group.selfSpeed * 2.2f, 1.2f, 3.4f);
            if (moving && group.selfSpeed > 0.05f) group.walkPhase += stepHz * TwoPi * deltaTime;

            foreach (BonePose bone in group.bones.Values)
            {
                bone.offset = Quaternion.identity;
                bone.touched = false;
            }
            group.hipsLift = 0f;
            if (group.walkWeight > 0.001f) ApplyWalkLayer(group, group.walkPhase + group.phaseSeed, group.walkWeight);
            if (group.idleWeight > 0.001f) ApplyIdleLayer(group, group.idleWeight);
            ApplyGroupPose(group, false, 0f);
        }
    }

    void UpdateRig(CharacterRig rig, float deltaTime)
    {
        if (rig == null || rig.root == null) return;

        // 速度：车队根水平位移 / deltaTime
        Vector3 position = rig.root.position;
        if (rig.hasLastPosition)
        {
            Vector3 delta = position - rig.lastPosition;
            delta.y = 0f;
            float raw = delta.magnitude / deltaTime;
            if (raw > TeleportSpeedLimit) raw = 0f; // SeekFrame 瞬移
            rig.speed = Mathf.Lerp(rig.speed, raw, 1f - Mathf.Exp(-12f * deltaTime));
        }
        rig.lastPosition = position;
        rig.hasLastPosition = true;

        bool moving = rig.speed > MoveSpeedThreshold;
        float fade = deltaTime / WeightFadeSeconds;
        rig.walkWeight = Mathf.MoveTowards(rig.walkWeight, moving && !rig.water ? 1f : 0f, fade);
        rig.rowWeight = Mathf.MoveTowards(rig.rowWeight, moving && rig.water ? 1f : 0f, fade);
        rig.idleWeight = Mathf.MoveTowards(rig.idleWeight, moving ? 0f : 1f, fade);
        rig.moveBlend = Mathf.MoveTowards(rig.moveBlend, moving ? 1f : 0f, fade);
        rig.waterBlend = Mathf.MoveTowards(rig.waterBlend, rig.water ? 1f : 0f, fade);

        // 相位推进
        float stepHz = Mathf.Clamp(rig.speed * 2.2f, 1.2f, 3.4f);
        if (rig.speed > 0.05f) rig.walkPhase += stepHz * TwoPi * deltaTime;
        rig.rowPhase += RowHz * TwoPi * deltaTime;

        // Action 权重：Hit 的 0.4s 急回后保持 0.6 微晃
        bool actionActive = Time.time < rig.actionEnd;
        float actionTarget = 0f;
        if (actionActive)
        {
            actionTarget = 1f;
            if (rig.actionKind == LycheeCharacterActionKind.Hit && Time.time - rig.actionStart > 0.4f)
                actionTarget = 0.6f;
        }
        rig.actionWeight = Mathf.MoveTowards(rig.actionWeight, actionTarget, fade);

        // Playables 升级钩子：当前主导状态有可用 clip 就交给 Animator
        int dominantState = moving ? (rig.water ? 1 : 0) : 2;
        AnimationClip clip = ClipForState(dominantState);
        bool playableActive = false;
        if (clip != null && rig.captainWrapper != null && rig.captainGroup != null)
        {
            EnsurePlayable(rig, dominantState, clip);
            playableActive = rig.graphValid && rig.graph.IsValid();
        }
        else
        {
            ReleasePlayable(rig);
        }

        // 将军（非 Mixamo 模型自带 walking clip）：独立 Playables 播放，速率随车速
        DriveGeneralClip(rig);

        UpdateBones(rig, playableActive);
        UpdateWrappers(rig, deltaTime);
    }

    /// <summary>
    /// 播放将军自带的 walking clip（赤龙将军等非 Mixamo 模型）：clip 是原地走，靠车队带着前进，
    /// 腿臂循环即"行军"观感。仅 captainGroup 为空（非 Mixamo 占位队长）且该队有 clip 时启用；
    /// Animator 必须挂 GLB 实例根（captainWrapper 下首个子物体），clip 曲线路径相对它才能绑上骨骼。
    /// </summary>
    void DriveGeneralClip(CharacterRig rig)
    {
        if (rig.generalWalkClip == null || rig.captainGroup != null) return;
        if (rig.generalWrapper == null || rig.generalWrapper.childCount == 0) return;

        // 对抗中且有攻击 clip → 挥剑，否则走路。切换时需重建 Playables 图（PlayClip 是单 clip 图）。
        AnimationClip desired =
            (rig.generalAttacking && rig.generalAttackClip != null && !rig.generalAttackClip.legacy)
            ? rig.generalAttackClip : rig.generalWalkClip;
        if (desired == null || desired.legacy) return;

        bool needRebuild = !rig.generalGraphValid || !rig.generalGraph.IsValid() || rig.generalActiveClip != desired;
        if (needRebuild)
        {
            Transform modelRoot = rig.generalWrapper.GetChild(0); // CreateNormalizedModel 内的 GLB 实例
            if (rig.generalAnimator == null)
            {
                rig.generalAnimator = modelRoot.GetComponent<Animator>();
                if (rig.generalAnimator == null)
                    rig.generalAnimator = modelRoot.gameObject.AddComponent<Animator>();
            }
            if (rig.generalAnimator == null) return;
            if (rig.generalGraphValid && rig.generalGraph.IsValid()) rig.generalGraph.Destroy();
            rig.generalPlayable = AnimationPlayableUtilities.PlayClip(
                rig.generalAnimator, desired, out rig.generalGraph);
            rig.generalGraphValid = rig.generalGraph.IsValid();
            rig.generalActiveClip = desired;
        }
        if (rig.generalGraphValid && rig.generalGraph.IsValid())
        {
            // 攻击 clip 固定常速挥砍；走路 clip 随车速调档（静止放慢近站立、保持待命踏步感）
            float playSpeed = rig.generalAttacking && rig.generalAttackClip != null
                ? 1f
                : (rig.speed < 0.1f ? 0.35f : Mathf.Min(1.5f, 0.7f + rig.speed * 0.22f));
            rig.generalPlayable.SetSpeed(playSpeed);
            if (!desired.isLooping && desired.length > 0.01f &&
                rig.generalPlayable.GetTime() >= desired.length)
                rig.generalPlayable.SetTime(rig.generalPlayable.GetTime() % desired.length); // 非循环 clip 手动回卷
        }
    }

    static void ReleaseGeneralClip(CharacterRig rig)
    {
        if (rig == null) return;
        if (rig.generalGraphValid && rig.generalGraph.IsValid()) rig.generalGraph.Destroy();
        rig.generalGraphValid = false;
    }

    void UpdateBones(CharacterRig rig, bool playableActive)
    {
        foreach (BoneGroup group in rig.boneGroups)
        {
            if (group == null || group.wrapper == null) continue;
            bool isCaptain = group == rig.captainGroup;
            bool groupPlayable = playableActive && isCaptain; // Playables 目前只接管 Captain，队员组始终程序化

            foreach (BonePose bone in group.bones.Values)
            {
                bone.offset = Quaternion.identity;
                bone.touched = false;
            }
            group.hipsLift = 0f;

            // Action 压过 locomotion，但只作用于 Captain（对抗演出语义），队员组不被压制
            float locomotionScale = isCaptain ? 1f - rig.actionWeight : 1f;
            if (!groupPlayable)
            {
                // 每组叠加自身相位种子错开齐步走（Captain 种子为 0，保持历史步伐基准）
                if (rig.walkWeight > 0.001f) ApplyWalkLayer(group, rig.walkPhase + group.phaseSeed, rig.walkWeight * locomotionScale);
                if (rig.rowWeight > 0.001f) ApplyRowLayer(group, rig.rowPhase + group.phaseSeed * 0.7f, rig.rowWeight * locomotionScale);
                if (rig.idleWeight > 0.001f) ApplyIdleLayer(group, rig.idleWeight * locomotionScale);
            }
            if (isCaptain && rig.actionWeight > 0.001f)
                ApplyActionLayer(rig, group, groupPlayable ? 1f : rig.actionWeight);

            ApplyGroupPose(group, groupPlayable, rig.actionWeight);
        }
    }

    /// <summary>把当帧累积的偏移写回骨骼：程序化路径直接「基准 × 偏移」，Playables 路径只按 Action 权重混入。</summary>
    static void ApplyGroupPose(BoneGroup group, bool playableActive, float actionWeight)
    {
        if (!playableActive)
        {
            // 程序化：基准 × 偏移，永不漂移
            foreach (BonePose bone in group.bones.Values)
            {
                if (bone.transform == null) continue;
                bone.transform.localRotation = bone.baseRotation * bone.offset;
            }
            if (group.hips != null)
                group.hips.localPosition = group.hipsBasePosition + LocalUp(group.hips) * group.hipsLift;
        }
        else if (actionWeight > 0.001f)
        {
            // Playables 驱动 locomotion 时，Action 以权重混向「基准 × 动作偏移」，同样无漂移
            foreach (BonePose bone in group.bones.Values)
            {
                if (bone.transform == null || !bone.touched) continue;
                bone.transform.localRotation = Quaternion.Slerp(
                    bone.transform.localRotation,
                    bone.baseRotation * bone.offset,
                    actionWeight);
            }
        }
    }

    static Vector3 LocalUp(Transform bone)
    {
        // 把"世界向上的米数"换算到骨骼父空间（兼容缩放过的 Mixamo 骨架）
        return bone != null && bone.parent != null
            ? bone.parent.InverseTransformVector(Vector3.up)
            : Vector3.up;
    }

    static void AddBoneOffset(BoneGroup group, string boneName, Quaternion rotation, float weight)
    {
        if (weight <= 0.001f) return;
        if (!group.bones.TryGetValue(boneName, out BonePose bone) || bone == null) return;
        bone.offset = bone.offset * Quaternion.Slerp(Quaternion.identity, rotation, Mathf.Clamp01(weight));
        bone.touched = true;
    }

    // ---------- Locomotion 各层 ----------

    void ApplyWalkLayer(BoneGroup group, float phase, float w)
    {
        float swing = Mathf.Sin(phase);
        // 下垂骨骼（腿/臂）向前 = -X；左右反相
        AddBoneOffset(group, "LeftUpLeg", Quaternion.Euler(-swing * 26f, 0f, 0f), w);
        AddBoneOffset(group, "RightUpLeg", Quaternion.Euler(swing * 26f, 0f, 0f), w);
        float kneeLeft = Mathf.Max(0f, Mathf.Sin(phase - 1.15f)) * 24f;
        float kneeRight = Mathf.Max(0f, Mathf.Sin(phase - 1.15f + Mathf.PI)) * 24f;
        AddBoneOffset(group, "LeftLeg", Quaternion.Euler(kneeLeft, 0f, 0f), w);
        AddBoneOffset(group, "RightLeg", Quaternion.Euler(kneeRight, 0f, 0f), w);
        AddBoneOffset(group, "LeftFoot", Quaternion.Euler(swing * 6f, 0f, 0f), w);
        AddBoneOffset(group, "RightFoot", Quaternion.Euler(-swing * 6f, 0f, 0f), w);
        // 手臂与同侧腿反相
        AddBoneOffset(group, "LeftArm", Quaternion.Euler(swing * 18f, 0f, 0f), w);
        AddBoneOffset(group, "RightArm", Quaternion.Euler(-swing * 18f, 0f, 0f), w);
        float foreLeft = 8f + Mathf.Max(0f, -swing) * 16f;
        float foreRight = 8f + Mathf.Max(0f, swing) * 16f;
        AddBoneOffset(group, "LeftForeArm", Quaternion.Euler(-foreLeft, 0f, 0f), w);
        AddBoneOffset(group, "RightForeArm", Quaternion.Euler(-foreRight, 0f, 0f), w);
        // 躯干前倾 + 头部稳定微摆
        AddBoneOffset(group, "Spine", Quaternion.Euler(6f, 0f, 0f), w);
        AddBoneOffset(group, "Head", Quaternion.Euler(Mathf.Sin(phase * 2f) * 1.2f, Mathf.Sin(phase) * 2.5f, 0f), w);
        // 步频上下颠 Hips
        group.hipsLift += Mathf.Sin(phase * 2f) * 0.02f * w;
    }

    void ApplyRowLayer(BoneGroup group, float rowPhase, float w)
    {
        float p = Mathf.Sin(rowPhase);
        float armForward = Mathf.Lerp(-15f, 35f, (p + 1f) * 0.5f); // 后 15° ↔ 前 35°
        float armX = -armForward;                                  // 下垂骨骼向前为 -X
        // 双臂同相划桨（Shoulder + Arm 分摊）
        AddBoneOffset(group, "LeftShoulder", Quaternion.Euler(armX * 0.35f, 0f, 0f), w);
        AddBoneOffset(group, "RightShoulder", Quaternion.Euler(armX * 0.35f, 0f, 0f), w);
        AddBoneOffset(group, "LeftArm", Quaternion.Euler(armX * 0.75f, 0f, 0f), w);
        AddBoneOffset(group, "RightArm", Quaternion.Euler(armX * 0.75f, 0f, 0f), w);
        float fore = 12f + Mathf.Max(0f, -p) * 22f; // 回拉时屈肘
        AddBoneOffset(group, "LeftForeArm", Quaternion.Euler(-fore, 0f, 0f), w);
        AddBoneOffset(group, "RightForeArm", Quaternion.Euler(-fore, 0f, 0f), w);
        // Spine 节奏前后倾 ±8°
        AddBoneOffset(group, "Spine", Quaternion.Euler(4f + p * 8f, 0f, 0f), w);
        AddBoneOffset(group, "Spine1", Quaternion.Euler(p * 4f, 0f, 0f), w);
        AddBoneOffset(group, "Head", Quaternion.Euler(p * 2f, 0f, 0f), w);
        // 半蹲静态
        AddBoneOffset(group, "LeftUpLeg", Quaternion.Euler(-20f, 0f, 0f), w);
        AddBoneOffset(group, "RightUpLeg", Quaternion.Euler(-20f, 0f, 0f), w);
        AddBoneOffset(group, "LeftLeg", Quaternion.Euler(26f, 0f, 0f), w);
        AddBoneOffset(group, "RightLeg", Quaternion.Euler(26f, 0f, 0f), w);
    }

    void ApplyIdleLayer(BoneGroup group, float w)
    {
        float t = Time.time + group.idleSeed;
        // 呼吸：Spine1/Spine2 各 ±1.5°，0.25Hz
        float breath = Mathf.Sin(t * 0.25f * TwoPi) * 1.5f;
        AddBoneOffset(group, "Spine1", Quaternion.Euler(breath, 0f, 0f), w);
        AddBoneOffset(group, "Spine2", Quaternion.Euler(breath, 0f, 0f), w);
        // 头部 Perlin 微转 ±4°（慢）
        float headYaw = (Mathf.PerlinNoise(t * 0.13f, group.idleSeed) - 0.5f) * 8f;
        float headPitch = (Mathf.PerlinNoise(group.idleSeed, t * 0.11f) - 0.5f) * 6f;
        AddBoneOffset(group, "Head", Quaternion.Euler(headPitch, headYaw, 0f), w);
        // 手臂自然下垂微摆 ±2°
        float armSway = Mathf.Sin(t * 0.4f * TwoPi) * 2f;
        AddBoneOffset(group, "LeftArm", Quaternion.Euler(armSway, 0f, 0f), w);
        AddBoneOffset(group, "RightArm", Quaternion.Euler(-armSway, 0f, 0f), w);
    }

    // ---------- Action 覆盖层 ----------

    void ApplyActionLayer(CharacterRig rig, BoneGroup group, float w)
    {
        float a = Time.time - rig.actionStart;
        if (a < 0f) a = 0f;
        switch (rig.actionKind)
        {
            case LycheeCharacterActionKind.Attack: ApplyAttack(group, a, w); break;
            case LycheeCharacterActionKind.Build: ApplyBuild(group, a, w); break;
            case LycheeCharacterActionKind.Cheer: ApplyCheer(group, a, w); break;
            case LycheeCharacterActionKind.Hit: ApplyHit(group, a, w); break;
        }
    }

    void ApplyAttack(BoneGroup group, float a, float w)
    {
        // 0.55s 一循环：右臂高举过头 → 斜下劈，Spine 配合转体 12°，左臂格挡位
        float c = Mathf.Repeat(a, 0.55f) / 0.55f;
        float lift;
        if (c < 0.45f) lift = Mathf.SmoothStep(0f, 1f, c / 0.45f);
        else if (c < 0.62f) lift = Mathf.SmoothStep(1f, 0.12f, (c - 0.45f) / 0.17f);
        else lift = Mathf.SmoothStep(0.12f, 0f, (c - 0.62f) / 0.38f);

        AddBoneOffset(group, "RightShoulder", Quaternion.Euler(-28f * lift, 0f, 10f * lift), w);
        AddBoneOffset(group, "RightArm", Quaternion.Euler(-125f * lift, 0f, 18f * lift), w);
        AddBoneOffset(group, "RightForeArm", Quaternion.Euler(-38f * lift, 0f, 0f), w);
        AddBoneOffset(group, "Spine", Quaternion.Euler(4f * lift, -12f * lift, 0f), w);
        AddBoneOffset(group, "Head", Quaternion.Euler(-4f * lift, 6f * lift, 0f), w);
        // 左臂格挡位（静态）
        AddBoneOffset(group, "LeftArm", Quaternion.Euler(-55f, 0f, -12f), w);
        AddBoneOffset(group, "LeftForeArm", Quaternion.Euler(-78f, 0f, 0f), w);
    }

    void ApplyBuild(BoneGroup group, float a, float w)
    {
        // 0.7s 循环：双臂同步抬起再快速下压锤打，Spine 前倾 18°
        float c = Mathf.Repeat(a, 0.7f) / 0.7f;
        float h;
        if (c < 0.5f) h = Mathf.SmoothStep(0f, 1f, c / 0.5f);
        else if (c < 0.68f) h = Mathf.SmoothStep(1f, 0f, (c - 0.5f) / 0.18f);
        else h = 0f;
        float armX = Mathf.Lerp(-18f, -92f, h);

        AddBoneOffset(group, "LeftShoulder", Quaternion.Euler(armX * 0.2f, 0f, 0f), w);
        AddBoneOffset(group, "RightShoulder", Quaternion.Euler(armX * 0.2f, 0f, 0f), w);
        AddBoneOffset(group, "LeftArm", Quaternion.Euler(armX, 0f, -6f), w);
        AddBoneOffset(group, "RightArm", Quaternion.Euler(armX, 0f, 6f), w);
        AddBoneOffset(group, "LeftForeArm", Quaternion.Euler(-(20f + 30f * h), 0f, 0f), w);
        AddBoneOffset(group, "RightForeArm", Quaternion.Euler(-(20f + 30f * h), 0f, 0f), w);
        AddBoneOffset(group, "Spine", Quaternion.Euler(18f, 0f, 0f), w);
        AddBoneOffset(group, "Spine1", Quaternion.Euler(5f * h, 0f, 0f), w);
        AddBoneOffset(group, "Head", Quaternion.Euler(8f, 0f, 0f), w);
    }

    void ApplyCheer(BoneGroup group, float a, float w)
    {
        // 双臂高举过头摆动 + 小跳
        float wave = Mathf.Sin(a * TwoPi * 1.6f);
        AddBoneOffset(group, "LeftArm", Quaternion.Euler(-155f, 0f, -14f + wave * 16f), w);
        AddBoneOffset(group, "RightArm", Quaternion.Euler(-155f, 0f, 14f + wave * 16f), w);
        AddBoneOffset(group, "LeftForeArm", Quaternion.Euler(-18f, 0f, 0f), w);
        AddBoneOffset(group, "RightForeArm", Quaternion.Euler(-18f, 0f, 0f), w);
        AddBoneOffset(group, "Spine", Quaternion.Euler(-6f, 0f, 0f), w);
        AddBoneOffset(group, "Head", Quaternion.Euler(-8f, wave * 4f, 0f), w);
        // Hips Y +0.05 弹跳
        group.hipsLift += Mathf.Abs(Mathf.Sin(a * Mathf.PI * 2.8f)) * 0.05f * w;
    }

    void ApplyHit(BoneGroup group, float a, float w)
    {
        if (a < 0.4f)
        {
            // 单次 0.4s：Spine 急后仰 14° 再回正
            float pulse = Mathf.Sin(Mathf.Clamp01(a / 0.4f) * Mathf.PI);
            AddBoneOffset(group, "Spine", Quaternion.Euler(-14f * pulse, 0f, 0f), w);
            AddBoneOffset(group, "Spine1", Quaternion.Euler(-6f * pulse, 0f, 0f), w);
            AddBoneOffset(group, "Head", Quaternion.Euler(-10f * pulse, 0f, 0f), w);
            AddBoneOffset(group, "LeftArm", Quaternion.Euler(-24f * pulse, 0f, -10f * pulse), w);
            AddBoneOffset(group, "RightArm", Quaternion.Euler(-24f * pulse, 0f, 10f * pulse), w);
        }
        else
        {
            // 之后保持微晃（权重在外层降到 0.6）
            float t = a - 0.4f;
            AddBoneOffset(group, "Spine", Quaternion.Euler(Mathf.Sin(t * 8f) * 4f, Mathf.Sin(t * 6.3f) * 3f, 0f), w);
            AddBoneOffset(group, "Head", Quaternion.Euler(Mathf.Sin(t * 7.1f) * 3f, 0f, 0f), w);
        }
    }

    // ---------- 无骨骼对象的廉价动画 ----------

    void UpdateWrappers(CharacterRig rig, float deltaTime)
    {
        float t = Time.time;

        // TeamMember：行进 Y 弹跳（相位按序号错开）+ 行进方向小幅俯仰；
        // 采到骨骼组的队员弹跳/俯仰减半——骨骼步行已自带 Hips 起伏与躯干前倾，全幅会双重弹跳
        foreach (WrapperPose member in rig.members)
        {
            if (member == null || member.transform == null) continue;
            float amplitude = member.hasBones ? 0.5f : 1f;
            float phase = rig.walkPhase + member.seed;
            float bounce = Mathf.Abs(Mathf.Sin(phase)) * 0.03f * rig.moveBlend * amplitude;
            member.transform.localPosition = member.basePosition + new Vector3(0f, bounce, 0f);
            float pitch = Mathf.Sin(phase) * 3.5f * rig.moveBlend * amplitude;
            member.transform.localRotation = member.baseRotation * Quaternion.Euler(pitch, 0f, 0f);
        }

        // Carriage：速度相关 Perlin 颠簸（roll ±1.8°、pitch ±1.2°）+ 假车轮滚动 + 轮速弹跳 + 加减速点头
        if (rig.carriage != null && rig.carriage.transform != null)
        {
            WrapperPose carriage = rig.carriage;
            float k = Mathf.Clamp01(rig.speed / 2.5f) * (1f - rig.waterBlend);
            float roll = (Mathf.PerlinNoise(t * 3.1f, carriage.seed) - 0.5f) * 2f * 1.8f * k;
            float pitch = (Mathf.PerlinNoise(carriage.seed + 7.7f, t * 2.6f) - 0.5f) * 2f * 1.2f * k;

            // 加减速点头：速度变化率注入 pitchOffset（刹车前倾、起步后仰），随后匀速衰减回零
            float deltaSpeed = rig.speed - carriage.lastSpeed;
            carriage.lastSpeed = rig.speed;
            carriage.pitchOffset = Mathf.Clamp(carriage.pitchOffset - deltaSpeed * 2.4f, -7f, 7f);
            carriage.pitchOffset = Mathf.MoveTowards(carriage.pitchOffset, 0f, deltaTime * 9f);

            float bounce = 0f;
            if (carriage.wheels.Count > 0 && carriage.wheelRadius > 0.001f)
            {
                // 车轮滚动角速度 = 线速度 / 半径（弧度），换算成度累加；水路时轮子不转
                float spinDelta = rig.speed / carriage.wheelRadius * Mathf.Rad2Deg * deltaTime * (1f - rig.waterBlend);
                carriage.wheelSpin = Mathf.Repeat(carriage.wheelSpin + spinDelta, 360f);

                // 水路换船：轮子整体隐藏（比随 waterBlend 缩进更干净，不会出现半截轮悬在船边）
                bool hideWheels = rig.waterBlend > 0.5f;
                if (hideWheels != carriage.wheelsHidden)
                {
                    carriage.wheelsHidden = hideWheels;
                    foreach (Transform wheel in carriage.wheels)
                    {
                        if (wheel != null) wheel.gameObject.SetActive(!hideWheels);
                    }
                }
                if (!hideWheels)
                {
                    // 基准朝向 × 绕自身 Y 的累计转角 = 绕车轴滚动（四轮同步即可，肉眼分不出差速）
                    Quaternion spin = WheelBaseRotation * Quaternion.Euler(0f, carriage.wheelSpin, 0f);
                    foreach (Transform wheel in carriage.wheels)
                    {
                        if (wheel != null) wheel.localRotation = spin;
                    }
                }
                // 与轮速同步的轻微 Y 弹跳：每转两次起伏（模拟轮辋不圆），幅度随速度因子收放
                bounce = Mathf.Abs(Mathf.Sin(carriage.wheelSpin * Mathf.Deg2Rad * 2f)) * 0.012f * k;
            }

            carriage.transform.localRotation = carriage.baseRotation * Quaternion.Euler(pitch + carriage.pitchOffset, 0f, roll);
            carriage.transform.localPosition = carriage.basePosition + new Vector3(0f, bounce, 0f);

            // 马拉车：水路整马隐藏（与假车轮同款开关）；陆路动画双路——有 walk clip 走 Playables，否则程序化 bob
            if (carriage.horse != null)
            {
                bool hideHorse = rig.waterBlend > 0.5f;
                if (hideHorse != carriage.horseHidden)
                {
                    carriage.horseHidden = hideHorse;
                    carriage.horse.gameObject.SetActive(!hideHorse);
                    if (hideHorse) RestoreHorseLegs(carriage); // 隐藏前归位四腿，防下次显形停在半摆姿势
                }
                if (!hideHorse && carriage.horseLegs != null)
                {
                    // 组合马车无动画 clip：程序化驱动四腿行走。步频随车速，静止收敛站立。
                    // 车体本身（含车轮，焊在蒙皮网格里转不了）保持基准姿态不做整体晃动，只腿在动。
                    carriage.horseWalkPhase += (1.2f + rig.speed * 1.6f) * deltaTime;
                    if (carriage.horseWalkPhase > TwoPi) carriage.horseWalkPhase -= TwoPi;
                    DriveHorseLegs(carriage, rig.speed);
                    carriage.horse.localPosition = carriage.horseBasePosition;
                    carriage.horse.localRotation = carriage.horseBaseRotation;
                }
                else if (!hideHorse)
                {
                    AnimationClip horseClip = player != null ? player.horseWalkClip : null;
                    if (horseClip != null && horseClip.legacy) horseClip = null; // legacy 进 Playables 会报错，当无 clip 处理
                    if (horseClip != null && (!carriage.horseGraphValid || !carriage.horseGraph.IsValid()))
                        EnsureHorsePlayable(carriage, horseClip);

                    if (horseClip != null && carriage.horseGraphValid && carriage.horseGraph.IsValid())
                    {
                        // Playables 路：播放速率随车队速度调档——静止 0.3 近似原地踏步/站立，
                        // 行进 0.6 起步随速度抬升、封顶 1.6 防快进鬼畜
                        float playSpeed = rig.speed < 0.1f ? 0.3f : Mathf.Min(1.6f, 0.6f + rig.speed * 0.25f);
                        carriage.horsePlayable.SetSpeed(playSpeed);
                        // PlayClip 不保证循环（取决于 clip 自带 loopTime），非循环 clip 到尾手动回卷
                        if (!horseClip.isLooping && horseClip.length > 0.01f &&
                            carriage.horsePlayable.GetTime() >= horseClip.length)
                        {
                            carriage.horsePlayable.SetTime(carriage.horsePlayable.GetTime() % horseClip.length);
                        }
                    }
                    else
                    {
                        // 程序化兜底：bob 相位由 wheelSpin 推导、与假车轮严格同步（像马蹄带着车轮走）；
                        // 速度趋零时 k→0，bob/俯仰收敛，剩呼吸式起伏（与 Idle 层同款 0.25Hz）
                        float horsePhase = carriage.wheelSpin * Mathf.Deg2Rad * 2f;
                        float horseBob = Mathf.Abs(Mathf.Sin(horsePhase)) * 0.05f * k;
                        float breathe = Mathf.Sin(t * 0.25f * TwoPi + carriage.seed) * 0.012f * (1f - k);
                        float horsePitch = Mathf.Sin(horsePhase) * 3f * k;
                        carriage.horse.localPosition = carriage.horseBasePosition + new Vector3(0f, horseBob + breathe, 0f);
                        carriage.horse.localRotation = carriage.horseBaseRotation * Quaternion.Euler(horsePitch, 0f, 0f);
                    }
                }
            }
        }

        // Boat：水上缓慢横摇 ±2.5°
        if (rig.boat != null && rig.boat.transform != null)
        {
            float roll = Mathf.Sin(t * TwoPi * 0.3f + rig.boat.seed) * 2.5f * rig.waterBlend;
            rig.boat.transform.localRotation = rig.boat.baseRotation * Quaternion.Euler(0f, 0f, roll);
        }
    }

    // ---------- Playables 升级钩子 ----------

    static AnimationClip ClipForState(int state)
    {
        LycheeAnimationLibrary library = LycheeAnimationLibrary.Instance;
        if (library == null) return null;
        AnimationClip clip = state == 0 ? library.walk : state == 1 ? library.row : library.idle;
        if (clip == null || clip.legacy) return null;
        return clip;
    }

    void EnsurePlayable(CharacterRig rig, int state, AnimationClip clip)
    {
        if (rig.playingState == state && rig.graphValid && rig.graph.IsValid()) return;
        ReleasePlayable(rig);
        if (rig.captainWrapper == null || clip == null) return;
        if (rig.animator == null)
        {
            rig.animator = rig.captainWrapper.GetComponentInChildren<Animator>(true);
            if (rig.animator == null)
                rig.animator = rig.captainWrapper.gameObject.AddComponent<Animator>();
        }
        if (rig.animator == null) return;
        AnimationPlayableUtilities.PlayClip(rig.animator, clip, out rig.graph);
        rig.graphValid = rig.graph.IsValid();
        rig.playingState = rig.graphValid ? state : -1;
    }

    void ReleasePlayable(CharacterRig rig)
    {
        if (rig == null) return;
        if (rig.graphValid && rig.graph.IsValid()) rig.graph.Destroy();
        rig.graphValid = false;
        rig.playingState = -1;
    }

    // ---------- 还原 ----------

    void RestoreBasePose(CharacterRig rig)
    {
        if (rig == null) return;
        foreach (BoneGroup group in rig.boneGroups)
            RestoreGroupPose(group);
        foreach (WrapperPose member in rig.members)
        {
            if (member == null || member.transform == null) continue;
            member.transform.localPosition = member.basePosition;
            member.transform.localRotation = member.baseRotation;
        }
        if (rig.carriage != null && rig.carriage.transform != null)
        {
            rig.carriage.transform.localPosition = rig.carriage.basePosition; // 轮速弹跳会动 localPosition，一并还原
            rig.carriage.transform.localRotation = rig.carriage.baseRotation;
            if (rig.carriage.horse != null)
            {
                rig.carriage.horse.localPosition = rig.carriage.horseBasePosition; // 程序化 bob 动的是马局部位姿
                rig.carriage.horse.localRotation = rig.carriage.horseBaseRotation;
            }
        }
        if (rig.boat != null && rig.boat.transform != null)
            rig.boat.transform.localRotation = rig.boat.baseRotation;
    }

    static void RestoreGroupPose(BoneGroup group)
    {
        if (group == null || !group.wrapper) return; // Unity 假 null：小队组可能已随小队销毁
        foreach (BonePose bone in group.bones.Values)
        {
            if (bone != null && bone.transform != null)
                bone.transform.localRotation = bone.baseRotation;
        }
        if (group.hips != null) group.hips.localPosition = group.hipsBasePosition;
    }
}
