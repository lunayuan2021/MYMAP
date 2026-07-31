using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 打斗建筑透视（X-ray）：打斗（对抗 / 小队攻坚）发生在站点节点，而站点建筑有高度，
/// 从俯视相机会挡住将军/小队的打斗动作。打斗期间把该站建筑整体换成半透明"虚影"材质
/// （淡青、~20% alpha）让动作透出，结束后换回原材质。LOL 类游戏遮挡物淡出的标准做法。
///
/// 统一"请求"模型：任何系统调 RequestXray(nodeId, holdSeconds) 即可让某站透视一段时间，
/// 重复请求刷新保持时长；当前只透一座站。对抗(WINDOW_CONTEST)与小队攻坚(SpawnSquad 抵达)都接入。
/// 为何换材质而非改原材质 alpha：站点是 glTFast ShaderGraph，运行时切 opaque→transparent 易碎；
/// 换成自建 URP/Lit 透明虚影材质、结束再换回原 sharedMaterials，最稳。自举挂到 LycheeReplayPlayer。
/// </summary>
public sealed class LycheeContestBuildingXray : MonoBehaviour
{
    const float FadeSeconds = 0.35f;
    const float XrayAlpha = 0.2f;

    static LycheeContestBuildingXray instance;

    readonly Dictionary<string, MapStationAnchor> stationsById = new Dictionary<string, MapStationAnchor>();
    Material ghostMaterial;

    sealed class XrayTarget
    {
        public Renderer renderer;
        public Material[] original;
    }
    readonly List<XrayTarget> active = new List<XrayTarget>();
    string currentNode;
    float holdUntil = -999f;
    float fadeValue = 1f; // 1=完全实体，0=完全虚影
    bool hasActive;

    /// <summary>请求让某站建筑透视 holdSeconds 秒（对抗/小队攻坚调用）。重复请求刷新保持时长。</summary>
    public static void RequestXray(string nodeId, float holdSeconds)
    {
        if (instance == null || string.IsNullOrEmpty(nodeId)) return;
        instance.RequestInternal(nodeId, holdSeconds);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var player = FindAnyObjectByType<LycheeReplayPlayer>();
        if (player == null) return;
        if (player.GetComponent<LycheeContestBuildingXray>() == null)
            player.gameObject.AddComponent<LycheeContestBuildingXray>();
    }

    void OnEnable()
    {
        instance = this;
        LycheeReplayPlayer.ReplayEventRaised += OnReplayEvent;
    }

    void OnDisable()
    {
        LycheeReplayPlayer.ReplayEventRaised -= OnReplayEvent;
        RestoreImmediate();
        if (instance == this) instance = null;
    }

    void Start()
    {
        foreach (MapStationAnchor anchor in FindObjectsByType<MapStationAnchor>(FindObjectsSortMode.None))
            if (anchor != null && !string.IsNullOrEmpty(anchor.stationId))
                stationsById[anchor.stationId] = anchor;
        ghostMaterial = BuildGhostMaterial();
    }

    /// <summary>淡青半透明虚影材质：URP/Lit 透明设置一次配好，运行时只 lerp _BaseColor.a。</summary>
    static Material BuildGhostMaterial()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) return null;
        var m = new Material(shader) { name = "LycheeBuildingGhost" };
        m.SetFloat("_Surface", 1f);          // 0=Opaque 1=Transparent
        m.SetFloat("_Blend", 0f);            // 0=Alpha
        m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0f);
        m.SetFloat("_AlphaClip", 0f);
        m.DisableKeyword("_SURFACE_TYPE_OPAQUE");
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.5f);
        m.renderQueue = (int)RenderQueue.Transparent;
        var tint = new Color(0.62f, 0.78f, 0.95f, 1f);
        m.SetColor("_BaseColor", tint);
        if (m.HasProperty("_Color")) m.SetColor("_Color", tint);
        return m;
    }

    void OnReplayEvent(LycheeReplayMessage message, int round)
    {
        if (message == null || string.IsNullOrEmpty(message.type) || message.payload == null) return;
        // 对抗：整场（约 12s）透视目标站建筑
        if (message.type == "WINDOW_CONTEST_START")
            RequestInternal(message.payload.targetNodeId, 12f);
        else if (message.type == "WINDOW_CONTEST_END")
            RequestInternal(message.payload.targetNodeId, 0.4f); // 缩短保持→很快淡出恢复
    }

    void RequestInternal(string nodeId, float holdSeconds)
    {
        if (ghostMaterial == null || string.IsNullOrEmpty(nodeId)) return;
        if (!stationsById.TryGetValue(nodeId, out MapStationAnchor anchor) || anchor == null) return;

        if (currentNode == nodeId && hasActive)
        {
            holdUntil = Mathf.Max(holdUntil, Time.time + holdSeconds);
            return;
        }
        // 切到新站：先恢复旧站
        if (hasActive) RestoreImmediate();

        Transform building = anchor.transform.Find("Imported_Station_Model");
        Transform scope = building != null ? building : anchor.transform;
        foreach (Renderer renderer in scope.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || !renderer.enabled) continue;
            Material[] originals = renderer.sharedMaterials;
            var ghosts = new Material[originals.Length];
            for (int i = 0; i < ghosts.Length; i++) ghosts[i] = ghostMaterial;
            active.Add(new XrayTarget { renderer = renderer, original = originals });
            renderer.sharedMaterials = ghosts;
        }
        if (active.Count == 0) return;
        hasActive = true;
        currentNode = nodeId;
        holdUntil = Time.time + holdSeconds;
    }

    void Update()
    {
        if (!hasActive) return;
        bool hold = Time.time < holdUntil;
        float target = hold ? 0f : 1f; // 0=虚影 1=实体
        fadeValue = Mathf.MoveTowards(fadeValue, target, Time.deltaTime / FadeSeconds);

        float alpha = Mathf.Lerp(XrayAlpha, 1f, fadeValue);
        if (ghostMaterial != null)
        {
            Color c = ghostMaterial.GetColor("_BaseColor"); c.a = alpha;
            ghostMaterial.SetColor("_BaseColor", c);
            if (ghostMaterial.HasProperty("_Color")) ghostMaterial.SetColor("_Color", c);
        }
        if (!hold && fadeValue >= 0.999f) RestoreImmediate();
    }

    void RestoreImmediate()
    {
        foreach (XrayTarget t in active)
            if (t != null && t.renderer != null) t.renderer.sharedMaterials = t.original;
        active.Clear();
        hasActive = false;
        currentNode = null;
        fadeValue = 1f;
    }
}
