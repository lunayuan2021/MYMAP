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
/// 工程内没有任何 AnimationClip，因此基于「绑定姿势 + 偏移四元数」在 LateUpdate 驱动 Captain 的 22 根 Mixamo 骨骼。
/// 所有姿态 = Start 时记录的基准 localRotation × 当帧计算的偏移，绝不累积漂移。
/// 状态机（每帧由回放数据推导）：
///   Walk —— 陆路行进：腿臂反相摆动、膝/肘随相位屈伸、Spine 前倾、Hips 步频颠簸；
///   Row  —— 水路行进：双臂同相划桨、Spine 节奏前后倾、双腿半蹲；
///   Idle —— 静止：呼吸起伏 + Perlin 头部微动 + 手臂自然微摆；
///   Action 覆盖层 —— Attack / Build / Cheer / Hit，权重压过 locomotion，对外提供 PlayAction API，
///   并订阅 ReplayEventRaised 自驱动（设卡→Build、破关→Attack、对抗窗口→双方 Attack、交付/验核→Cheer）。
/// 无骨骼对象的廉价动画：TeamMember 行进弹跳、Carriage 速度颠簸（Perlin）、Boat 水上横摇。
/// 升级钩子：LycheeAnimationLibrary 提供非 legacy 的 walk/idle/row clip 时，该状态改用 Playables 循环播放，
/// 程序化层只保留 Action 覆盖；缺哪个状态哪个状态继续程序化。
/// </summary>
public class LycheeCharacterMotion : MonoBehaviour
{
    /// <summary>全局禁用开关：true 时不自举；运行中切换会恢复绑定姿势并停止驱动。</summary>
    public static bool Disabled;

    const float WeightFadeSeconds = 0.18f;   // 各层权重渐变时长
    const float MoveSpeedThreshold = 0.2f;   // 行进判定速度阈值（m/s，忽略 Y）
    const float TeleportSpeedLimit = 40f;    // SeekFrame 瞬移保护
    const float RowHz = 0.9f;                // 划桨节奏
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

    sealed class WrapperPose
    {
        public Transform transform;
        public Vector3 basePosition;
        public Quaternion baseRotation;
        public float seed;
    }

    sealed class CharacterRig
    {
        public int playerId;
        public Transform root;
        public Transform captainWrapper;
        public readonly Dictionary<string, BonePose> bones = new Dictionary<string, BonePose>();
        public Transform hips;
        public Vector3 hipsBasePosition;
        public float hipsLift;

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
    }

    static LycheeCharacterMotion instance;

    LycheeReplayPlayer player;
    readonly List<CharacterRig> rigs = new List<CharacterRig>();
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
            ReleasePlayable(rig);
        rigs.Clear();
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
                idleSeed = state.playerId * 2.13f + 0.7f,
                lastPosition = root.position,
                hasLastPosition = true,
                water = IsWaterEdge(state.routeEdgeId)
            };
            rig.waterBlend = rig.water ? 1f : 0f;

            rig.captainWrapper = root.Find("Captain");
            CollectBones(rig);
            CollectWrappers(rig);
            rigs.Add(rig);
        }
        built = rigs.Count > 0;
    }

    void CollectBones(CharacterRig rig)
    {
        if (rig == null || rig.captainWrapper == null) return;
        SkinnedMeshRenderer skin = rig.captainWrapper.GetComponentInChildren<SkinnedMeshRenderer>(true);
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
            if (bone == null) bone = FindDeep(rig.captainWrapper, boneName);
            if (bone == null) continue;
            rig.bones[boneName] = new BonePose
            {
                transform = bone,
                baseRotation = bone.localRotation
            };
        }
        if (rig.bones.TryGetValue("Hips", out BonePose hipsPose))
        {
            rig.hips = hipsPose.transform;
            rig.hipsBasePosition = hipsPose.transform.localPosition;
        }
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
                rig.members.Add(new WrapperPose
                {
                    transform = child,
                    basePosition = child.localPosition,
                    baseRotation = child.localRotation,
                    seed = (rig.members.Count + 1) * 1.7f
                });
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
                if (rig != null)
                    StartAction(rig.playerId, LycheeCharacterActionKind.Attack, ContestHoldSeconds, true);
            }
            return;
        }
        if (type == "WINDOW_CONTEST_END")
        {
            foreach (CharacterRig rig in rigs)
            {
                if (rig != null && rig.actionFromContest)
                {
                    rig.actionEnd = Time.time;
                    rig.actionFromContest = false;
                }
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
        if (!built) return;

        if (Disabled)
        {
            if (!disabledApplied)
            {
                disabledApplied = true;
                foreach (CharacterRig rig in rigs)
                {
                    RestoreBasePose(rig);
                    ReleasePlayable(rig);
                }
            }
            return;
        }
        disabledApplied = false;

        float deltaTime = Time.deltaTime;
        if (deltaTime <= 0f) return;
        foreach (CharacterRig rig in rigs)
            UpdateRig(rig, deltaTime);
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
        if (clip != null && rig.captainWrapper != null && rig.bones.Count > 0)
        {
            EnsurePlayable(rig, dominantState, clip);
            playableActive = rig.graphValid && rig.graph.IsValid();
        }
        else
        {
            ReleasePlayable(rig);
        }

        UpdateBones(rig, playableActive);
        UpdateWrappers(rig, deltaTime);
    }

    void UpdateBones(CharacterRig rig, bool playableActive)
    {
        if (rig.bones.Count == 0) return;

        foreach (BonePose bone in rig.bones.Values)
        {
            bone.offset = Quaternion.identity;
            bone.touched = false;
        }
        rig.hipsLift = 0f;

        float locomotionScale = 1f - rig.actionWeight; // Action 压过 locomotion
        if (!playableActive)
        {
            if (rig.walkWeight > 0.001f) ApplyWalkLayer(rig, rig.walkWeight * locomotionScale);
            if (rig.rowWeight > 0.001f) ApplyRowLayer(rig, rig.rowWeight * locomotionScale);
            if (rig.idleWeight > 0.001f) ApplyIdleLayer(rig, rig.idleWeight * locomotionScale);
        }
        if (rig.actionWeight > 0.001f)
            ApplyActionLayer(rig, playableActive ? 1f : rig.actionWeight);

        if (!playableActive)
        {
            // 程序化：基准 × 偏移，永不漂移
            foreach (BonePose bone in rig.bones.Values)
            {
                if (bone.transform == null) continue;
                bone.transform.localRotation = bone.baseRotation * bone.offset;
            }
            if (rig.hips != null)
                rig.hips.localPosition = rig.hipsBasePosition + LocalUp(rig.hips) * rig.hipsLift;
        }
        else if (rig.actionWeight > 0.001f)
        {
            // Playables 驱动 locomotion 时，Action 以权重混向「基准 × 动作偏移」，同样无漂移
            foreach (BonePose bone in rig.bones.Values)
            {
                if (bone.transform == null || !bone.touched) continue;
                bone.transform.localRotation = Quaternion.Slerp(
                    bone.transform.localRotation,
                    bone.baseRotation * bone.offset,
                    rig.actionWeight);
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

    void AddBoneOffset(CharacterRig rig, string boneName, Quaternion rotation, float weight)
    {
        if (weight <= 0.001f) return;
        if (!rig.bones.TryGetValue(boneName, out BonePose bone) || bone == null) return;
        bone.offset = bone.offset * Quaternion.Slerp(Quaternion.identity, rotation, Mathf.Clamp01(weight));
        bone.touched = true;
    }

    // ---------- Locomotion 各层 ----------

    void ApplyWalkLayer(CharacterRig rig, float w)
    {
        float phase = rig.walkPhase;
        float swing = Mathf.Sin(phase);
        // 下垂骨骼（腿/臂）向前 = -X；左右反相
        AddBoneOffset(rig, "LeftUpLeg", Quaternion.Euler(-swing * 26f, 0f, 0f), w);
        AddBoneOffset(rig, "RightUpLeg", Quaternion.Euler(swing * 26f, 0f, 0f), w);
        float kneeLeft = Mathf.Max(0f, Mathf.Sin(phase - 1.15f)) * 24f;
        float kneeRight = Mathf.Max(0f, Mathf.Sin(phase - 1.15f + Mathf.PI)) * 24f;
        AddBoneOffset(rig, "LeftLeg", Quaternion.Euler(kneeLeft, 0f, 0f), w);
        AddBoneOffset(rig, "RightLeg", Quaternion.Euler(kneeRight, 0f, 0f), w);
        AddBoneOffset(rig, "LeftFoot", Quaternion.Euler(swing * 6f, 0f, 0f), w);
        AddBoneOffset(rig, "RightFoot", Quaternion.Euler(-swing * 6f, 0f, 0f), w);
        // 手臂与同侧腿反相
        AddBoneOffset(rig, "LeftArm", Quaternion.Euler(swing * 18f, 0f, 0f), w);
        AddBoneOffset(rig, "RightArm", Quaternion.Euler(-swing * 18f, 0f, 0f), w);
        float foreLeft = 8f + Mathf.Max(0f, -swing) * 16f;
        float foreRight = 8f + Mathf.Max(0f, swing) * 16f;
        AddBoneOffset(rig, "LeftForeArm", Quaternion.Euler(-foreLeft, 0f, 0f), w);
        AddBoneOffset(rig, "RightForeArm", Quaternion.Euler(-foreRight, 0f, 0f), w);
        // 躯干前倾 + 头部稳定微摆
        AddBoneOffset(rig, "Spine", Quaternion.Euler(6f, 0f, 0f), w);
        AddBoneOffset(rig, "Head", Quaternion.Euler(Mathf.Sin(phase * 2f) * 1.2f, Mathf.Sin(phase) * 2.5f, 0f), w);
        // 步频上下颠 Hips
        rig.hipsLift += Mathf.Sin(phase * 2f) * 0.02f * w;
    }

    void ApplyRowLayer(CharacterRig rig, float w)
    {
        float p = Mathf.Sin(rig.rowPhase);
        float armForward = Mathf.Lerp(-15f, 35f, (p + 1f) * 0.5f); // 后 15° ↔ 前 35°
        float armX = -armForward;                                  // 下垂骨骼向前为 -X
        // 双臂同相划桨（Shoulder + Arm 分摊）
        AddBoneOffset(rig, "LeftShoulder", Quaternion.Euler(armX * 0.35f, 0f, 0f), w);
        AddBoneOffset(rig, "RightShoulder", Quaternion.Euler(armX * 0.35f, 0f, 0f), w);
        AddBoneOffset(rig, "LeftArm", Quaternion.Euler(armX * 0.75f, 0f, 0f), w);
        AddBoneOffset(rig, "RightArm", Quaternion.Euler(armX * 0.75f, 0f, 0f), w);
        float fore = 12f + Mathf.Max(0f, -p) * 22f; // 回拉时屈肘
        AddBoneOffset(rig, "LeftForeArm", Quaternion.Euler(-fore, 0f, 0f), w);
        AddBoneOffset(rig, "RightForeArm", Quaternion.Euler(-fore, 0f, 0f), w);
        // Spine 节奏前后倾 ±8°
        AddBoneOffset(rig, "Spine", Quaternion.Euler(4f + p * 8f, 0f, 0f), w);
        AddBoneOffset(rig, "Spine1", Quaternion.Euler(p * 4f, 0f, 0f), w);
        AddBoneOffset(rig, "Head", Quaternion.Euler(p * 2f, 0f, 0f), w);
        // 半蹲静态
        AddBoneOffset(rig, "LeftUpLeg", Quaternion.Euler(-20f, 0f, 0f), w);
        AddBoneOffset(rig, "RightUpLeg", Quaternion.Euler(-20f, 0f, 0f), w);
        AddBoneOffset(rig, "LeftLeg", Quaternion.Euler(26f, 0f, 0f), w);
        AddBoneOffset(rig, "RightLeg", Quaternion.Euler(26f, 0f, 0f), w);
    }

    void ApplyIdleLayer(CharacterRig rig, float w)
    {
        float t = Time.time + rig.idleSeed;
        // 呼吸：Spine1/Spine2 各 ±1.5°，0.25Hz
        float breath = Mathf.Sin(t * 0.25f * TwoPi) * 1.5f;
        AddBoneOffset(rig, "Spine1", Quaternion.Euler(breath, 0f, 0f), w);
        AddBoneOffset(rig, "Spine2", Quaternion.Euler(breath, 0f, 0f), w);
        // 头部 Perlin 微转 ±4°（慢）
        float headYaw = (Mathf.PerlinNoise(t * 0.13f, rig.idleSeed) - 0.5f) * 8f;
        float headPitch = (Mathf.PerlinNoise(rig.idleSeed, t * 0.11f) - 0.5f) * 6f;
        AddBoneOffset(rig, "Head", Quaternion.Euler(headPitch, headYaw, 0f), w);
        // 手臂自然下垂微摆 ±2°
        float armSway = Mathf.Sin(t * 0.4f * TwoPi) * 2f;
        AddBoneOffset(rig, "LeftArm", Quaternion.Euler(armSway, 0f, 0f), w);
        AddBoneOffset(rig, "RightArm", Quaternion.Euler(-armSway, 0f, 0f), w);
    }

    // ---------- Action 覆盖层 ----------

    void ApplyActionLayer(CharacterRig rig, float w)
    {
        float a = Time.time - rig.actionStart;
        if (a < 0f) a = 0f;
        switch (rig.actionKind)
        {
            case LycheeCharacterActionKind.Attack: ApplyAttack(rig, a, w); break;
            case LycheeCharacterActionKind.Build: ApplyBuild(rig, a, w); break;
            case LycheeCharacterActionKind.Cheer: ApplyCheer(rig, a, w); break;
            case LycheeCharacterActionKind.Hit: ApplyHit(rig, a, w); break;
        }
    }

    void ApplyAttack(CharacterRig rig, float a, float w)
    {
        // 0.55s 一循环：右臂高举过头 → 斜下劈，Spine 配合转体 12°，左臂格挡位
        float c = Mathf.Repeat(a, 0.55f) / 0.55f;
        float lift;
        if (c < 0.45f) lift = Mathf.SmoothStep(0f, 1f, c / 0.45f);
        else if (c < 0.62f) lift = Mathf.SmoothStep(1f, 0.12f, (c - 0.45f) / 0.17f);
        else lift = Mathf.SmoothStep(0.12f, 0f, (c - 0.62f) / 0.38f);

        AddBoneOffset(rig, "RightShoulder", Quaternion.Euler(-28f * lift, 0f, 10f * lift), w);
        AddBoneOffset(rig, "RightArm", Quaternion.Euler(-125f * lift, 0f, 18f * lift), w);
        AddBoneOffset(rig, "RightForeArm", Quaternion.Euler(-38f * lift, 0f, 0f), w);
        AddBoneOffset(rig, "Spine", Quaternion.Euler(4f * lift, -12f * lift, 0f), w);
        AddBoneOffset(rig, "Head", Quaternion.Euler(-4f * lift, 6f * lift, 0f), w);
        // 左臂格挡位（静态）
        AddBoneOffset(rig, "LeftArm", Quaternion.Euler(-55f, 0f, -12f), w);
        AddBoneOffset(rig, "LeftForeArm", Quaternion.Euler(-78f, 0f, 0f), w);
    }

    void ApplyBuild(CharacterRig rig, float a, float w)
    {
        // 0.7s 循环：双臂同步抬起再快速下压锤打，Spine 前倾 18°
        float c = Mathf.Repeat(a, 0.7f) / 0.7f;
        float h;
        if (c < 0.5f) h = Mathf.SmoothStep(0f, 1f, c / 0.5f);
        else if (c < 0.68f) h = Mathf.SmoothStep(1f, 0f, (c - 0.5f) / 0.18f);
        else h = 0f;
        float armX = Mathf.Lerp(-18f, -92f, h);

        AddBoneOffset(rig, "LeftShoulder", Quaternion.Euler(armX * 0.2f, 0f, 0f), w);
        AddBoneOffset(rig, "RightShoulder", Quaternion.Euler(armX * 0.2f, 0f, 0f), w);
        AddBoneOffset(rig, "LeftArm", Quaternion.Euler(armX, 0f, -6f), w);
        AddBoneOffset(rig, "RightArm", Quaternion.Euler(armX, 0f, 6f), w);
        AddBoneOffset(rig, "LeftForeArm", Quaternion.Euler(-(20f + 30f * h), 0f, 0f), w);
        AddBoneOffset(rig, "RightForeArm", Quaternion.Euler(-(20f + 30f * h), 0f, 0f), w);
        AddBoneOffset(rig, "Spine", Quaternion.Euler(18f, 0f, 0f), w);
        AddBoneOffset(rig, "Spine1", Quaternion.Euler(5f * h, 0f, 0f), w);
        AddBoneOffset(rig, "Head", Quaternion.Euler(8f, 0f, 0f), w);
    }

    void ApplyCheer(CharacterRig rig, float a, float w)
    {
        // 双臂高举过头摆动 + 小跳
        float wave = Mathf.Sin(a * TwoPi * 1.6f);
        AddBoneOffset(rig, "LeftArm", Quaternion.Euler(-155f, 0f, -14f + wave * 16f), w);
        AddBoneOffset(rig, "RightArm", Quaternion.Euler(-155f, 0f, 14f + wave * 16f), w);
        AddBoneOffset(rig, "LeftForeArm", Quaternion.Euler(-18f, 0f, 0f), w);
        AddBoneOffset(rig, "RightForeArm", Quaternion.Euler(-18f, 0f, 0f), w);
        AddBoneOffset(rig, "Spine", Quaternion.Euler(-6f, 0f, 0f), w);
        AddBoneOffset(rig, "Head", Quaternion.Euler(-8f, wave * 4f, 0f), w);
        // Hips Y +0.05 弹跳
        rig.hipsLift += Mathf.Abs(Mathf.Sin(a * Mathf.PI * 2.8f)) * 0.05f * w;
    }

    void ApplyHit(CharacterRig rig, float a, float w)
    {
        if (a < 0.4f)
        {
            // 单次 0.4s：Spine 急后仰 14° 再回正
            float pulse = Mathf.Sin(Mathf.Clamp01(a / 0.4f) * Mathf.PI);
            AddBoneOffset(rig, "Spine", Quaternion.Euler(-14f * pulse, 0f, 0f), w);
            AddBoneOffset(rig, "Spine1", Quaternion.Euler(-6f * pulse, 0f, 0f), w);
            AddBoneOffset(rig, "Head", Quaternion.Euler(-10f * pulse, 0f, 0f), w);
            AddBoneOffset(rig, "LeftArm", Quaternion.Euler(-24f * pulse, 0f, -10f * pulse), w);
            AddBoneOffset(rig, "RightArm", Quaternion.Euler(-24f * pulse, 0f, 10f * pulse), w);
        }
        else
        {
            // 之后保持微晃（权重在外层降到 0.6）
            float t = a - 0.4f;
            AddBoneOffset(rig, "Spine", Quaternion.Euler(Mathf.Sin(t * 8f) * 4f, Mathf.Sin(t * 6.3f) * 3f, 0f), w);
            AddBoneOffset(rig, "Head", Quaternion.Euler(Mathf.Sin(t * 7.1f) * 3f, 0f, 0f), w);
        }
    }

    // ---------- 无骨骼对象的廉价动画 ----------

    void UpdateWrappers(CharacterRig rig, float deltaTime)
    {
        float t = Time.time;

        // TeamMember：行进 Y 弹跳（相位按序号错开）+ 行进方向小幅俯仰
        foreach (WrapperPose member in rig.members)
        {
            if (member == null || member.transform == null) continue;
            float phase = rig.walkPhase + member.seed;
            float bounce = Mathf.Abs(Mathf.Sin(phase)) * 0.03f * rig.moveBlend;
            member.transform.localPosition = member.basePosition + new Vector3(0f, bounce, 0f);
            float pitch = Mathf.Sin(phase) * 3.5f * rig.moveBlend;
            member.transform.localRotation = member.baseRotation * Quaternion.Euler(pitch, 0f, 0f);
        }

        // Carriage：速度相关 Perlin 颠簸（roll ±1.8°、pitch ±1.2°）
        if (rig.carriage != null && rig.carriage.transform != null)
        {
            float k = Mathf.Clamp01(rig.speed / 2.5f) * (1f - rig.waterBlend);
            float roll = (Mathf.PerlinNoise(t * 3.1f, rig.carriage.seed) - 0.5f) * 2f * 1.8f * k;
            float pitch = (Mathf.PerlinNoise(rig.carriage.seed + 7.7f, t * 2.6f) - 0.5f) * 2f * 1.2f * k;
            rig.carriage.transform.localRotation = rig.carriage.baseRotation * Quaternion.Euler(pitch, 0f, roll);
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
        foreach (BonePose bone in rig.bones.Values)
        {
            if (bone != null && bone.transform != null)
                bone.transform.localRotation = bone.baseRotation;
        }
        if (rig.hips != null) rig.hips.localPosition = rig.hipsBasePosition;
        foreach (WrapperPose member in rig.members)
        {
            if (member == null || member.transform == null) continue;
            member.transform.localPosition = member.basePosition;
            member.transform.localRotation = member.baseRotation;
        }
        if (rig.carriage != null && rig.carriage.transform != null)
            rig.carriage.transform.localRotation = rig.carriage.baseRotation;
        if (rig.boat != null && rig.boat.transform != null)
            rig.boat.transform.localRotation = rig.boat.baseRotation;
    }
}
