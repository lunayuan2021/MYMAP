using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 调测地图叠层：右上角小地图下方放一个「调测地图」按钮，点开后
///   ① 右侧列出每个站点的世界坐标 + 格坐标；
///   ② 实时列出各队主角(车队)的移动世界坐标；
///   ③ 在 3D 地图上每个站点 / 车队处直接贴一个坐标小标签。
/// 玩家可据此自行计算任意两点间距离。
/// 独立组件、纯 OnGUI(IMGUI)绘制，不改动现有 HUD / 小地图。
/// 自举：AfterSceneLoad 挂到 LycheeReplayPlayer 同物体上(与 LycheeHudExtensions 一致)。
/// </summary>
public sealed class LycheeDebugMapOverlay : MonoBehaviour
{
    /// <summary>设为 true 可整体关闭本调测叠层(自举前阻止挂载)。</summary>
    public static bool Disabled;

    static bool show;
    LycheeReplayPlayer player;
    Vector2 scroll;
    float lastScan;
    readonly List<MapStationAnchor> stations = new List<MapStationAnchor>();

    GUIStyle panelStyle, headStyle, rowStyle, tagStyle, tagConvoyStyle;
    Texture2D panelBg;

    const int MaxPlayers = 16;          // 车队 playerId 扫描上限(实际几支自动跳过 null)

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Disabled) return;
        LycheeReplayPlayer host = FindAnyObjectByType<LycheeReplayPlayer>();
        if (host == null) return;
        if (host.GetComponent<LycheeDebugMapOverlay>() != null) return;
        host.gameObject.AddComponent<LycheeDebugMapOverlay>();
    }

    void Awake() { player = GetComponent<LycheeReplayPlayer>(); }

    void EnsureStyles()
    {
        if (panelStyle != null) return;
        panelBg = new Texture2D(1, 1);
        panelBg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.80f));
        panelBg.Apply();
        panelStyle = new GUIStyle { padding = new RectOffset(10, 10, 8, 8) };
        panelStyle.normal.background = panelBg;
        headStyle = new GUIStyle(GUIStyle.none) { fontSize = 14, fontStyle = FontStyle.Bold };
        headStyle.normal.textColor = new Color(1f, 0.85f, 0.40f);
        rowStyle = new GUIStyle(GUIStyle.none) { fontSize = 12 };
        rowStyle.normal.textColor = Color.white;
        tagStyle = new GUIStyle(GUIStyle.none) { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        tagStyle.normal.textColor = new Color(1f, 0.95f, 0.6f);
        tagConvoyStyle = new GUIStyle(tagStyle);
        tagConvoyStyle.normal.textColor = new Color(0.6f, 1f, 0.7f);
    }

    void RescanStations()
    {
        stations.Clear();
        MapStationAnchor[] arr = FindObjectsByType<MapStationAnchor>(FindObjectsSortMode.None);
        if (arr != null) stations.AddRange(arr);
        stations.Sort((a, b) => string.CompareOrdinal(a != null ? a.stationId : "", b != null ? b.stationId : ""));
        lastScan = Time.unscaledTime;
    }

    void OnGUI()
    {
        EnsureStyles();

        // 按钮：右上角小地图(约 16..276)下方
        const float bw = 96f, bh = 30f, top = 292f, margin = 16f;
        Rect btn = new Rect(Screen.width - bw - margin, top, bw, bh);
        if (GUI.Button(btn, show ? "调测:开" : "调测地图"))
        {
            show = !show;
            if (show) RescanStations();
        }
        if (!show) return;
        if (Time.unscaledTime - lastScan > 2f) RescanStations();

        DrawWorldTags();
        DrawPanel(top + bh + 6f, bw, margin);
    }

    // 在 3D 地图上每个站点 / 车队处贴坐标小标签
    void DrawWorldTags()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        foreach (MapStationAnchor s in stations)
        {
            if (s == null) continue;
            Vector3 wp = s.transform.position;
            Vector3 sp = cam.WorldToScreenPoint(wp + Vector3.up * 1.5f);
            if (sp.z <= 0f) continue;                 // 在相机背后
            string nm = string.IsNullOrEmpty(s.displayName) ? s.stationId : s.displayName;
            var c = new GUIContent($"{nm}\n({wp.x:F0},{wp.z:F0})");
            Vector2 sz = tagStyle.CalcSize(c);
            GUI.Label(new Rect(sp.x - sz.x * 0.5f, Screen.height - sp.y - sz.y, sz.x + 6f, sz.y + 4f), c, tagStyle);
        }

        if (player == null) return;
        for (int pid = 0; pid < MaxPlayers; pid++)
        {
            Transform root = player.GetConvoyRoot(pid);
            if (root == null) continue;
            Vector3 wp = root.position;
            Vector3 sp = cam.WorldToScreenPoint(wp + Vector3.up * 2.0f);
            if (sp.z <= 0f) continue;
            var c = new GUIContent($"{player.TeamDisplayName(pid)}\n({wp.x:F1},{wp.z:F1})");
            Vector2 sz = tagConvoyStyle.CalcSize(c);
            GUI.Label(new Rect(sp.x - sz.x * 0.5f, Screen.height - sp.y - sz.y, sz.x + 6f, sz.y + 4f), c, tagConvoyStyle);
        }
    }

    // 右侧文本面板：回合 + 各队车队实时坐标 + 站点坐标列表
    void DrawPanel(float top, float bw, float margin)
    {
        const float pw = 312f;
        float ph = Mathf.Max(180f, Screen.height - top - margin);
        Rect panel = new Rect(Screen.width - pw - margin, top, pw, ph);
        GUILayout.BeginArea(panel, panelStyle);

        GUILayout.Label(player != null ? $"回合 {player.CurrentRound}/{player.TotalRounds}（坐标=世界 X/Z）" : "回合 -", headStyle);

        GUILayout.Space(4f);
        GUILayout.Label("主角 / 车队（实时）", headStyle);
        bool any = false;
        if (player != null)
        {
            for (int pid = 0; pid < MaxPlayers; pid++)
            {
                Transform root = player.GetConvoyRoot(pid);
                if (root == null) continue;
                any = true;
                Vector3 p = root.position;
                GUILayout.Label($"  {player.TeamDisplayName(pid)}:  X {p.x:F1}   Z {p.z:F1}", rowStyle);
            }
        }
        if (!any) GUILayout.Label("  （当前无移动车队）", rowStyle);

        GUILayout.Space(6f);
        GUILayout.Label($"站点坐标  ×{stations.Count}", headStyle);
        scroll = GUILayout.BeginScrollView(scroll);
        foreach (MapStationAnchor s in stations)
        {
            if (s == null) continue;
            Vector3 p = s.transform.position;
            string nm = string.IsNullOrEmpty(s.displayName) ? s.stationId : s.displayName;
            GUILayout.Label($"  {nm}   世界({p.x:F0},{p.z:F0})  格({s.gridCoordinate.x},{s.gridCoordinate.y})", rowStyle);
        }
        GUILayout.EndScrollView();

        GUILayout.EndArea();
    }
}
