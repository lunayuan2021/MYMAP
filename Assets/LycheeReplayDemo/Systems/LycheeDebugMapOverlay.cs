using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 调测地图叠层：右上角小地图「下方」放一个「调测地图」按钮，点开后
///   ① 右侧列出每个站点的世界坐标 + 格坐标；
///   ② 实时列出红蓝车队 + 在途小分队的移动世界坐标；
///   ③ 在 3D 地图上每个站点 / 车队 / 小分队处直接贴一个坐标小标签。
/// 玩家可据此自行计算任意两点间距离。
/// 独立组件、纯 OnGUI(IMGUI)绘制，不改动现有 HUD / 小地图。
/// 自举：AfterSceneLoad 挂到 LycheeReplayPlayer 同物体上(与 LycheeHudExtensions 一致)。
/// 坐标=世界 X/Z。按钮位置与字号随分辨率缩放(小地图用 CanvasScaler 会放大，固定像素按钮会被它盖住)。
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
    readonly List<int> convoyIds = new List<int>();
    readonly List<KeyValuePair<Vector3, string>> squadTags = new List<KeyValuePair<Vector3, string>>();

    GUIStyle panelStyle, headStyle, rowStyle, tagStyle, tagConvoyStyle, tagSquadStyle, buttonStyle;
    Texture2D panelBg;

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

    // 小地图为 CanvasScaler(ScaleWithScreenSize, 参考 1920x1080, match=0.5)，缩放因子按几何平均。
    static float UiScale()
    {
        return Mathf.Max(1f, Mathf.Sqrt((Screen.width / 1920f) * (Screen.height / 1080f)));
    }

    void EnsureStyles()
    {
        if (panelStyle != null) return;
        panelBg = new Texture2D(1, 1);
        panelBg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.80f));
        panelBg.Apply();
        panelStyle = new GUIStyle { padding = new RectOffset(10, 10, 8, 8) };
        panelStyle.normal.background = panelBg;
        headStyle = new GUIStyle(GUIStyle.none) { fontStyle = FontStyle.Bold };
        headStyle.normal.textColor = new Color(1f, 0.85f, 0.40f);
        rowStyle = new GUIStyle(GUIStyle.none);
        rowStyle.normal.textColor = Color.white;
        tagStyle = new GUIStyle(GUIStyle.none) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        tagStyle.normal.textColor = new Color(1f, 0.95f, 0.6f);          // 站点：暖黄
        tagConvoyStyle = new GUIStyle(tagStyle);
        tagConvoyStyle.normal.textColor = new Color(0.55f, 1f, 0.7f);    // 车队：青绿
        tagSquadStyle = new GUIStyle(tagStyle);
        tagSquadStyle.normal.textColor = new Color(1f, 0.78f, 0.5f);     // 小分队：橙
        buttonStyle = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold };
    }

    // 每帧按当前分辨率刷新字号（窗口缩放时也能跟随）。
    void ApplyScaledFonts(float ui)
    {
        headStyle.fontSize = Mathf.RoundToInt(15f * ui);
        rowStyle.fontSize = Mathf.RoundToInt(13f * ui);
        int tagFs = Mathf.RoundToInt(15f * ui);                          // 原 11，按用户要求调大
        tagStyle.fontSize = tagFs;
        tagConvoyStyle.fontSize = Mathf.RoundToInt(16f * ui);
        tagSquadStyle.fontSize = tagFs;
        buttonStyle.fontSize = Mathf.RoundToInt(15f * ui);
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
        float ui = UiScale();
        ApplyScaledFonts(ui);

        // 按钮放在小地图(右上角，参考边距16 + PanelSize260 = 276)「下方」，整体随分辨率缩放，避免被放大的小地图盖住
        float margin = 16f * ui;
        float bw = 104f * ui, bh = 34f * ui;
        float minimapBottom = 276f * ui;
        // 小地图与按钮之间留出小分队状态条(HUD 顶右锚, -288 起、高 44)的位置，避免互相遮挡(用户要求)。
        float top = minimapBottom + 70f * ui;
        Rect btn = new Rect(Screen.width - bw - margin, top, bw, bh);
        if (GUI.Button(btn, show ? "调测:开" : "调测地图", buttonStyle))
        {
            show = !show;
            if (show) RescanStations();
        }
        if (!show) return;
        if (Time.unscaledTime - lastScan > 2f) RescanStations();

        DrawWorldTags();
        DrawPanel(top + bh + 6f * ui, margin, ui);
    }

    // 在 3D 地图上每个站点 / 车队 / 小分队处贴坐标小标签
    void DrawWorldTags()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        foreach (MapStationAnchor s in stations)
        {
            if (s == null) continue;
            Vector3 wp = s.transform.position;
            string nm = string.IsNullOrEmpty(s.displayName) ? s.stationId : s.displayName;
            DrawTag(cam, wp + Vector3.up * 1.5f, $"{nm}\n({wp.x:F0},{wp.z:F0})", tagStyle);
        }

        if (player == null) return;

        // 红蓝车队（真实 playerId 是 1002/2002 这类，不能用 0..15 扫）
        player.CollectConvoyPlayerIds(convoyIds);
        foreach (int pid in convoyIds)
        {
            Transform root = player.GetConvoyRoot(pid);
            if (root == null) continue;
            Vector3 wp = root.position;
            DrawTag(cam, wp + Vector3.up * 2.0f, $"{player.TeamDisplayName(pid)}\n({wp.x:F1},{wp.z:F1})", tagConvoyStyle);
        }

        // 在途小分队
        player.CollectSquadTags(squadTags);
        foreach (KeyValuePair<Vector3, string> kv in squadTags)
            DrawTag(cam, kv.Key + Vector3.up * 1.8f, $"{kv.Value}\n({kv.Key.x:F1},{kv.Key.z:F1})", tagSquadStyle);
    }

    void DrawTag(Camera cam, Vector3 world, string text, GUIStyle style)
    {
        Vector3 sp = cam.WorldToScreenPoint(world);
        if (sp.z <= 0f) return;                       // 在相机背后
        var c = new GUIContent(text);
        Vector2 sz = style.CalcSize(c);
        GUI.Label(new Rect(sp.x - sz.x * 0.5f, Screen.height - sp.y - sz.y, sz.x + 6f, sz.y + 4f), c, style);
    }

    // 右侧文本面板：回合 + 红蓝车队 + 在途小分队 实时坐标 + 站点坐标列表
    void DrawPanel(float top, float margin, float ui)
    {
        float pw = 312f * ui;
        float ph = Mathf.Max(180f * ui, Screen.height - top - margin);
        Rect panel = new Rect(Screen.width - pw - margin, top, pw, ph);
        GUILayout.BeginArea(panel, panelStyle);

        GUILayout.Label(player != null ? $"回合 {player.CurrentRound}/{player.TotalRounds}（坐标=世界 X/Z）" : "回合 -", headStyle);

        GUILayout.Space(4f);
        GUILayout.Label("主角 / 车队 / 小分队（实时）", headStyle);
        bool any = false;
        if (player != null)
        {
            player.CollectConvoyPlayerIds(convoyIds);
            foreach (int pid in convoyIds)
            {
                Transform root = player.GetConvoyRoot(pid);
                if (root == null) continue;
                any = true;
                Vector3 p = root.position;
                GUILayout.Label($"  {player.TeamDisplayName(pid)}:  X {p.x:F1}   Z {p.z:F1}", rowStyle);
            }
            player.CollectSquadTags(squadTags);
            foreach (KeyValuePair<Vector3, string> kv in squadTags)
            {
                any = true;
                GUILayout.Label($"  {kv.Value}:  X {kv.Key.x:F1}   Z {kv.Key.z:F1}", rowStyle);
            }
        }
        if (!any) GUILayout.Label("  （当前无车队 / 小分队）", rowStyle);

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
