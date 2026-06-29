using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 调测地图叠层：右侧放一个「调测地图」按钮，点开后
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
    Vector2 mapOrigin;  // 地图西南角世界坐标；显示坐标 = 世界 - 此原点 → 全为 0 基正数（用户不想看到负数）
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
        // 绑中文字体：IMGUI 默认用内置 Arial(无中文字形)，WebGL 无系统字体 → 中文按钮(如"调测地图")空白。
        Font cjk = Resources.Load<Font>("Fonts/LXGWWenKai-Regular");
        if (cjk != null)
        {
            headStyle.font = cjk; rowStyle.font = cjk;
            tagStyle.font = cjk; tagConvoyStyle.font = cjk; tagSquadStyle.font = cjk;
            buttonStyle.font = cjk;
        }
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
        var terr = GameObject.Find("Continuous_Terrain");
        var tmr = terr != null ? terr.GetComponent<MeshRenderer>() : null;
        if (tmr != null) mapOrigin = new Vector2(tmr.bounds.min.x, tmr.bounds.min.z);  // 西南角为显示原点(0,0)
        lastScan = Time.unscaledTime;
    }

    // 显示坐标 = 世界坐标 - 地图西南角 → 全为 0 基正数（地图中心在世界原点，西/南半区世界坐标为负，用户不想看负数）。
    Vector2 L(Vector3 p) => new Vector2(p.x - mapOrigin.x, p.z - mapOrigin.y);

    void OnGUI()
    {
        if (ShouldHideForPresentation()) return;

        EnsureStyles();
        float ui = UiScale();
        ApplyScaledFonts(ui);

        // 按钮和坐标面板统一放右侧；面板展开时仍避开右上小地图，短视频/开场期间整体隐藏。
        float margin = 16f * ui;
        float bw = 112f * ui, bh = 34f * ui;
        float left = Mathf.Max(margin, Screen.width - bw - margin);
        float top = Mathf.Clamp(302f * ui, margin, Mathf.Max(margin, Screen.height - bh - margin));
        Rect btn = new Rect(left, top, bw, bh);
        if (GUI.Button(btn, show ? "调测:开" : "调测地图", buttonStyle))
        {
            show = !show;
            if (show) RescanStations();
        }
        if (!show) return;
        if (Time.unscaledTime - lastScan > 2f) RescanStations();

        DrawWorldTags();
        DrawPanel(top + bh + 8f * ui, margin, ui);
    }

    static bool ShouldHideForPresentation()
    {
        return LycheeVideoInterlude.IsPresentationActive ||
            LycheeVideoInterlude.IsPresentationVisible ||
            LycheeOpeningShowcase.AnyShowing;
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
            DrawTag(cam, wp + Vector3.up * 1.5f, $"{nm}\n({L(wp).x:F0},{L(wp).y:F0})", tagStyle);
        }

        if (player == null) return;

        // 红蓝车队（真实 playerId 是 1002/2002 这类，不能用 0..15 扫）
        player.CollectConvoyPlayerIds(convoyIds);
        foreach (int pid in convoyIds)
        {
            Transform root = player.GetConvoyRoot(pid);
            if (root == null) continue;
            Vector3 wp = root.position;
            DrawTag(cam, wp + Vector3.up * 2.0f, $"{player.TeamDisplayName(pid)}\n({L(wp).x:F1},{L(wp).y:F1})", tagConvoyStyle);
        }

        // 在途小分队
        player.CollectSquadTags(squadTags);
        foreach (KeyValuePair<Vector3, string> kv in squadTags)
            DrawTag(cam, kv.Key + Vector3.up * 1.8f, $"{kv.Value}\n({L(kv.Key).x:F1},{L(kv.Key).y:F1})", tagSquadStyle);
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
        float pw = Mathf.Min(360f * ui, Screen.width - margin * 2f);
        float ph = Mathf.Max(180f * ui, Screen.height - top - margin);
        Rect panel = new Rect(Screen.width - pw - margin, top, pw, ph);
        GUILayout.BeginArea(panel, panelStyle);

        GUILayout.Label(player != null ? $"回合 {player.CurrentRound}/{player.TotalRounds}（坐标=地图 X/Z，西南角为0）" : "回合 -", headStyle);

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
                GUILayout.Label($"  {player.TeamDisplayName(pid)}:  X {L(p).x:F1}   Z {L(p).y:F1}", rowStyle);
            }
            player.CollectSquadTags(squadTags);
            foreach (KeyValuePair<Vector3, string> kv in squadTags)
            {
                any = true;
                GUILayout.Label($"  {kv.Value}:  X {L(kv.Key).x:F1}   Z {L(kv.Key).y:F1}", rowStyle);
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
            GUILayout.Label($"  {nm}   地图({L(p).x:F0},{L(p).y:F0})  格({s.gridCoordinate.x},{s.gridCoordinate.y})", rowStyle);
        }
        GUILayout.EndScrollView();

        GUILayout.EndArea();
    }
}
