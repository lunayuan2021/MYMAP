using UnityEngine;

[RequireComponent(typeof(Camera))]
public class LycheeReplayCameraController : MonoBehaviour
{
    public LycheeReplayPlayer replay;
    public float minimumSize = 7.8f;
    public float maximumSize = 26f;
    public float eventSize = 6.8f;
    public float framingPadding = 6f;
    public float positionSmoothTime = 0.65f;
    public float zoomSmoothTime = 0.45f;
    public float cameraDistance = 58f;
    [Min(0.2f)] public float replayZoomMultiplier = 1.2f;
    public bool lockObliqueAngle = true;
    public Vector3 obliqueEuler = new Vector3(52f, -29f, 0f);

    [Header("Perspective Mode")]
    public bool perspectiveMode = true;
    // 默认取景框住当前行动对象；不能为了远端目标点把镜头推到整张地图。
    public float perspectiveFieldOfView = 42f;
    public float perspectivePitch = 41f;
    public float perspectiveDistanceSpanScale = 1.08f;
    public float perspectiveDistancePadding = 9f;
    public float perspectiveMinDistance = 17f;
    public float perspectiveMaxDistance = 108f;
    public float perspectiveEventDistance = 15f;
    public float rotationSmoothTime = 0.4f;

    [Header("Playable Bounds Guard")]
    public bool constrainFocusToPlayableBounds = false;
    public Vector2 playableMin = new Vector2(-34f, -21f);
    public Vector2 playableMax = new Vector2(34f, 18f);

    [Min(0.5f)] public float perspectiveZoomMultiplier = 1f; // 画面整体放大倍率：镜头拉近实现，HUD 不受影响；默认 1 即不额外拉近，保留字段供调参

    [Header("Focus Follow（两队拉开后轮流跟拍近景）")]
    // 关闭：用户要求镜头时刻能看见两个队伍，兵分两路时拉远景而非抛弃一队去单独跟拍
    public bool focusFollowEnabled = false;
    [Min(5f)] public float focusSpanThreshold = 26f;   // 两队间距超过该值时切换为单队跟拍
    [Min(2f)] public float focusSwapSeconds = 14f;     // 轮流跟拍的切换周期
    [Min(2f)] public float focusedSpan = 24f;          // 跟拍单队时使用的取景跨度（取宽些，保持远景感）

    Camera targetCamera;
    Vector3 positionVelocity;
    float zoomVelocity;
    float distanceVelocity;
    float pitchVelocity;
    float yawVelocity;
    float smoothedPitch;
    float smoothedYaw;
    float perspectiveYaw;
    bool perspectiveAnglesInitialized;
    float smoothedDistance;
    bool distanceInitialized;
    Vector3 appliedShakeOffset;
    float shakeStrength;
    float shakeDuration;
    float shakeTimeRemaining;

    void Awake()
    {
        EnsureReferences();
    }

    void OnEnable()
    {
        EnsureReferences();
    }

    void Start()
    {
        SnapToCurrentFocus();
    }

    void LateUpdate()
    {
        EnsureReferences();
        RemoveShakeOffset();
        if (targetCamera != null)
        {
            if (perspectiveMode) UpdatePerspectiveCamera(false);
            else UpdateOrthographicCamera(false);
        }
        ApplyShakeOffset(Time.unscaledDeltaTime);
    }

    public void SnapToCurrentFocus()
    {
        EnsureReferences();
        if (targetCamera == null) return;
        RemoveShakeOffset();
        if (perspectiveMode) UpdatePerspectiveCamera(true);
        else UpdateOrthographicCamera(true);
    }

    /// <summary>轻量镜头震动：strength 为最大偏移幅度（米），duration 为衰减时长（秒）。</summary>
    public void Shake(float strength, float duration)
    {
        if (strength <= 0f || duration <= 0f) return;
        if (strength < CurrentShakeAmplitude()) return;
        shakeStrength = strength;
        shakeDuration = Mathf.Max(0.05f, duration);
        shakeTimeRemaining = shakeDuration;
    }

    void UpdatePerspectiveCamera(bool snap)
    {
        targetCamera.orthographic = false;
        targetCamera.fieldOfView = perspectiveFieldOfView;
        if (!perspectiveAnglesInitialized)
        {
            perspectiveYaw = transform.eulerAngles.y;
            smoothedYaw = perspectiveYaw;
            smoothedPitch = Mathf.DeltaAngle(0f, transform.eulerAngles.x);
            perspectiveAnglesInitialized = true;
        }

        if (snap)
        {
            smoothedPitch = perspectivePitch;
            smoothedYaw = perspectiveYaw;
            pitchVelocity = 0f;
            yawVelocity = 0f;
        }
        else
        {
            smoothedPitch = Mathf.SmoothDampAngle(
                smoothedPitch,
                perspectivePitch,
                ref pitchVelocity,
                rotationSmoothTime,
                Mathf.Infinity,
                Time.unscaledDeltaTime);
            smoothedYaw = Mathf.SmoothDampAngle(
                smoothedYaw,
                perspectiveYaw,
                ref yawVelocity,
                rotationSmoothTime,
                Mathf.Infinity,
                Time.unscaledDeltaTime);
        }
        transform.rotation = Quaternion.Euler(smoothedPitch, smoothedYaw, 0f);

        if (!TryGetPerspectiveFraming(out Vector3 focus, out float desiredDistance))
            return;

        if (snap || !distanceInitialized)
        {
            smoothedDistance = desiredDistance;
            distanceVelocity = 0f;
            distanceInitialized = true;
        }
        else
        {
            smoothedDistance = Mathf.SmoothDamp(
                smoothedDistance,
                desiredDistance,
                ref distanceVelocity,
                zoomSmoothTime,
                Mathf.Infinity,
                Time.unscaledDeltaTime);
        }

        Vector3 desiredPosition = focus - transform.forward * smoothedDistance;
        if (snap)
        {
            transform.position = desiredPosition;
            positionVelocity = Vector3.zero;
            return;
        }
        transform.position = Vector3.SmoothDamp(
            transform.position,
            desiredPosition,
            ref positionVelocity,
            positionSmoothTime,
            Mathf.Infinity,
            Time.unscaledDeltaTime);
    }

    void UpdateOrthographicCamera(bool snap)
    {
        targetCamera.orthographic = true;
        ApplyObliqueAngle();
        if (!TryGetDesiredFraming(out Vector3 desiredPosition, out float desiredSize))
            return;

        if (snap)
        {
            transform.position = desiredPosition;
            targetCamera.orthographicSize = desiredSize;
            positionVelocity = Vector3.zero;
            zoomVelocity = 0f;
            return;
        }

        transform.position = Vector3.SmoothDamp(
            transform.position,
            desiredPosition,
            ref positionVelocity,
            positionSmoothTime,
            Mathf.Infinity,
            Time.unscaledDeltaTime);
        targetCamera.orthographicSize = Mathf.SmoothDamp(
            targetCamera.orthographicSize,
            desiredSize,
            ref zoomVelocity,
            zoomSmoothTime,
            Mathf.Infinity,
            Time.unscaledDeltaTime);
    }

    bool TryGetPerspectiveFraming(out Vector3 focus, out float distance)
    {
        focus = transform.position + transform.forward * Mathf.Max(1f, smoothedDistance);
        distance = Mathf.Max(1f, smoothedDistance);
        if (replay == null || !replay.TryGetStrategicFraming(out Vector3 center, out float span, out bool actionFocusActive))
            return false;

        focus = center;
        if (constrainFocusToPlayableBounds && !actionFocusActive)
            focus = ClampFocusToPlayableBounds(focus);

        if (!actionFocusActive && focusFollowEnabled && span > focusSpanThreshold)
        {
            // 两队相距过远时框住双方会拉到最远，角色动画/血条全看不清；
            // 改为按周期轮流跟拍其中一队的近景。
            LycheeReplayFrame frame = replay.CurrentFrame;
            if (frame != null && frame.players != null && frame.players.Length > 0)
            {
                int slot = Mathf.FloorToInt(Time.unscaledTime / Mathf.Max(2f, focusSwapSeconds))
                    % frame.players.Length;
                Transform convoy = replay.GetConvoyRoot(frame.players[slot].playerId);
                if (convoy != null)
                {
                    focus = convoy.position;
                    span = focusedSpan;
                }
            }
        }
        // 自适应包围框：距离由 span（相关对象真实跨度）驱动，“刚好框住”。
        // 事件/小队出现时只给少量额外余量（看清事件点+相关队伍即可），不再 ×1.32 放大、+24 余量、强制 65 远距——
        // 那套旧逻辑会让任意事件一触发就把镜头拉到准全图，队伍/小人/事件图标全变小看不清。
        span = Mathf.Min(span, 70f); // 二级护栏：player 端聚类封顶（默认30，录制宽取景可经 SetFramingSpanWeb 放宽），这里只夹极端值
        float actionScale = perspectiveDistanceSpanScale;
        float actionPadding = actionFocusActive ? perspectiveDistancePadding + 2f : perspectiveDistancePadding;
        float actionMinDistance = actionFocusActive ? Mathf.Max(perspectiveMinDistance, 22f) : perspectiveMinDistance;
        distance = Mathf.Clamp(
            span * actionScale + actionPadding,
            actionMinDistance,
            perspectiveMaxDistance);
        // 画面放大 = 镜头拉近，代替 Game 视图 Scale 裁切（后者会把 HUD 裁出画面）；倍率 1 时为恒等运算。
        distance = Mathf.Max(10f, distance / Mathf.Max(0.5f, perspectiveZoomMultiplier));
        return true;
    }

    Vector3 ClampFocusToPlayableBounds(Vector3 focus)
    {
        focus.x = Mathf.Clamp(focus.x, playableMin.x, playableMax.x);
        focus.z = Mathf.Clamp(focus.z, playableMin.y, playableMax.y);
        return focus;
    }

    bool TryGetDesiredFraming(out Vector3 desiredPosition, out float desiredSize)
    {
        desiredPosition = transform.position;
        desiredSize = targetCamera == null ? minimumSize : targetCamera.orthographicSize;
        if (targetCamera == null || replay == null || !replay.TryGetStrategicFraming(out Vector3 focus, out float span, out bool actionFocusActive))
            return false;

        float sizeScale = actionFocusActive ? 0.64f : 0.52f;
        float sizePadding = actionFocusActive ? framingPadding + 3f : framingPadding;
        desiredSize = Mathf.Clamp((span * sizeScale + sizePadding) / Mathf.Max(0.2f, replayZoomMultiplier), minimumSize, maximumSize);
        desiredPosition = focus - transform.forward * cameraDistance;
        return true;
    }

    float CurrentShakeAmplitude()
    {
        if (shakeTimeRemaining <= 0f || shakeDuration <= 0f) return 0f;
        float falloff = Mathf.Clamp01(shakeTimeRemaining / shakeDuration);
        return shakeStrength * falloff * falloff;
    }

    void RemoveShakeOffset()
    {
        if (appliedShakeOffset == Vector3.zero) return;
        transform.position -= appliedShakeOffset;
        appliedShakeOffset = Vector3.zero;
    }

    void ApplyShakeOffset(float deltaTime)
    {
        if (shakeTimeRemaining <= 0f) return;
        shakeTimeRemaining = Mathf.Max(0f, shakeTimeRemaining - deltaTime);
        float amplitude = CurrentShakeAmplitude();
        if (amplitude <= 0.0001f) return;

        float noiseTime = Time.time * 17f;
        float offsetX = Mathf.PerlinNoise(noiseTime, 0.731f) * 2f - 1f;
        float offsetY = Mathf.PerlinNoise(0.137f, noiseTime) * 2f - 1f;
        appliedShakeOffset = (transform.right * offsetX + transform.up * offsetY) * amplitude;
        transform.position += appliedShakeOffset;
    }

    void EnsureReferences()
    {
        if (targetCamera == null) targetCamera = GetComponent<Camera>();
        if (replay == null) replay = FindFirstObjectByType<LycheeReplayPlayer>();
    }

    void ApplyObliqueAngle()
    {
        if (!lockObliqueAngle) return;
        transform.rotation = Quaternion.Euler(obliqueEuler);
    }
}
