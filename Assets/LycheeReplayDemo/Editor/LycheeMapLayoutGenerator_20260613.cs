using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 多地图布局生成器（20260613，方案「克隆母版 + 差异化重布局」）。
///
/// 目标：锦标赛 5 张图各有一份 map_config（同 15 站拓扑，路网/坐标可不同）。本工具以现役成品场景
/// LycheeReplayDemo.unity 为母版（保留全部设计层：地形/氛围/天空/将军/小兵/HUD/结算/视频/材质/水面/标签），
/// 读入一份 map_config.json，**只改地图相关、其余设计层绝不触碰**，另存为 Map_&lt;key&gt;.unity。
///
/// 核心原则——「差异化」：把新 config 与母版基线 config（BaselineConfigPath）逐项比对，
/// 凡是**没变的站点/道路一律原样保留**（不重画，保住所有 RoadGroundFit/RibbonRefit 贴合成果），
/// 只对真正变化的部分动手：
///   ① 站点坐标变了 → 平移该 MapStationAnchor（建筑/停靠点/标签都是子物体，整体跟随）。
///   ② 道路**新增**（如 E18~E21）→ 克隆「同类型」的现役道路作模板（继承其精确材质/子物体结构），
///      重建几何（路面 ribbon、车辙/边、桥/关卡占位、彩带 overlay），赋新 edgeId/起止站/类型。
///   ③ 道路**改类型**（如 E14 ROAD→BRANCH）→ 删旧道路+其彩带，按目标类型克隆模板重建。
///   ④ 道路**仅折线变化、类型不变** → 复用原对象，仅重画 ribbon 网格 + pathPoints。
///   ⑤ config 里没有、母版里有的边 → 禁用（SetActive false）+ 报告，不删（保守）。
/// 新增节点（母版无对应锚点）只能报告跳过——建筑模型需另行导入，本工具不造楼。
///
/// 坐标一致性：GridToWorld 与运行时 LycheeReplayPlayer.GridToWorld 完全同式
///   x_world = (x - gridW/2)*tile，z_world = (gridH/2 - y)*tile（80x60 tile=1 即 (x-40, 30-y)）。
/// 高度策略：道路/彩带/锚点顶点高度采样现役地形 MapElevationData（地形是不动的设计层），自动贴合。
///
/// 回放绑定（分场景方案）：给每张图场景里的 LycheeReplayPlayer.streamingReplayPath 写各自的回放路径
///   （默认 "Tournament/&lt;key&gt;.txt"），切场景即换回放，零运行时耦合；母版保持 "replay.txt" 不变。
/// 生成的场景自动登记进 Build Settings（LycheeTournamentController 分场景切换需要）。
///
/// 安全约束：① 输出绝不等于母版主场景（防覆盖硬校验）；② 不删任何贴合脚本的 .done（避免唤醒
/// 那些硬编码母版路径的 InitializeOnLoad 轮询去重跑母版）；③ 母版若有未保存改动则中止（防克隆到脏盘态）。
/// 新网格统一另存 GeneratedMeshes/MapLayout_&lt;key&gt;/，原资产不动，可整目录删除回滚。
/// 本类是 Editor 工具，允许 System.IO / AssetDatabase。
/// </summary>
public static class LycheeMapLayoutGenerator_20260613
{
    const string MasterScenePath = "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity";
    const string OfficialMap1SceneName = "Map_medium";
    const string OutSceneFolder = "Assets/Scenes/GeneratedMaps";
    const string MeshRootFolder = "Assets/LycheeMap15/GeneratedMeshes";
    const string RuntimeConfigFolder = "Assets/StreamingAssets/Tournament";
    const string MapConfigArchiveFolder = "Assets/LycheeMap15/Configs/Maps";
    const string OverlayRootName = "Route_Line_Overlay_20260611V";
    // 母版当前所代表的基线 config（用于「变没变」的差异判定，最大化保留）。
    const string BaselineConfigPath = "Assets/LycheeMap15/Configs/Maps/ui_map_config_unity_full_coverage_v2.json";
    const string ReportFileName = "LycheeMapLayoutGenerator_20260613_report.txt";

    // 道路宽度/抬升：与 LycheeConfiguredMapGenerator.CreateRoutes 同一套数值，保证新道路与现役同款。
    // 路面宽度：支路原 0.94 太窄（用户反馈"比别的路窄"），加宽到 1.4（≈山路，介于山路与官道之间）。
    const float RoadWidth = 1.65f, WaterWidth = 3.2f, MountainWidth = 1.28f, BranchWidth = 1.05f; // S2:支路变细 1.4→1.05(用户锁定)
    // 清障：物体「包围盒边缘」到新路/新河中心线小于此值即清（用包围盒能清掉树冠/大石悬到路上的）。
    const float DecoReachRoad = 2.2f, DecoReachWater = 3.4f; // 清障路半径 1.4→2.2：路边草清宽点、路显出来(用户：地图尾部路被草盖)
    const float RoadLift = 0.026f, WaterLift = 0.038f, MountainLift = 0.032f, BranchLift = 0.028f;
    // 彩带宽度：实测现役 overlay 宽度（ROAD 0.34 / WATER 0.36 / MOUNTAIN 0.32 / BRANCH 0.30）——
    // 旧值 0.55 过宽导致"彩带和道路宽度不统一"，按实测统一。彩带贴在路面上方 OverlayLift。
    const float RoadOverlayWidth = 0.34f, WaterOverlayWidth = 0.36f, MountainOverlayWidth = 0.32f, BranchOverlayWidth = 0.30f;
    const float OverlayLift = 0.045f;    // 彩带高于路面，避免被压进路面/地表
    const bool EnableOverlayRibbons = false; // 去彩带总开关：false=不生成/不重画路彩带与水路蓝线，并禁用母版现有彩带（重跑也不回来）
    const float RoadTuckUnder = 0.35f;   // 路头在建筑足迹内降到驿站地面下这么多 → 建筑盖住路头（不留缺口/不盖楼）
    const float RoadUnderRiver = 0.22f;  // 路与河重叠处把路面降到河床（CarvedY）之下这么多 → 河床/水面盖住路（道路在最底层）
    const float AnchorLift = 0.018f;     // 锚点贴地抬升，与原生成器一致
    const float CoordEps = 0.001f;       // 网格坐标相等判定阈值
    // 水路质量约束：
    // - 新 WATER 必须先下挖地形，再铺窄水面；不能只把蓝色贴片盖在地表上。
    // - 克隆水路模板时不保留模板中心宽黄土 RiverBank，避免低质量“大黄贴片”。
    // - 旧 WATER 改成陆路时，旧河床要抬回并覆盖为陆地材质，不能让黄土河床残在路底。
    const float CarveHalfWidth = 1.4f;     // 河谷半宽：大幅收窄河道（2.4→1.4，碗形截面）
    const float CarveDepth = 1.50f;        // 河谷深度 1.5：容下 -1.26 的水面、谷壁高出水面 ~1.26
    // S3 山路抬脊（镜像挖河、只抬不降）：沿山路中心线把平面抬成倒U山脊，山路自然贴脊
    const float RidgeCrest = 1.2f;         // 山脊中心抬高（2026-06-25 用户：0.6 太不明显、不像真山路 → 提到 1.2）；山口 N3 再叠 RidgePassExtra
    const float RidgeHalfWidth = 3.2f;     // 山脊半宽：中心最高、到此宽渐降回平地（加宽 2.5→3.2 配合平顶）
    const float RidgeFlatFrac = 0.45f;     // 山脊平顶占比：内侧这比例宽度保持满高（平顶）→ 路坐平顶上不颠，外侧才渐降（修路面坑洼）
    const float RidgeEndTaperFrac = 0.22f; // 端点收口占比：山路两端这一段把脊高渐隐到 0，别顶飞驿站
    const float RidgeWaterKeepout = 2.0f;  // 到水中心线 <此距离 不抬脊（防把河填平）
    const float RidgePassExtra = 0.2f;     // N3 山口：脊顶再叠这么高（0.35→0.2 放缓，太陡会把连过去的路拉细，用户反馈）
    // S4 节点分级抬升（建筑+脚下地形同抬一块小台 → 节点高度成玩法语言：山口最高、忙站微抬、官道广场平）
    const float NodeBusyLift = 0.10f;      // N1 忙站(>2 路/河汇聚)：建筑+脚下抬这么高的夯土小广场（用户锁定）
    const float NodePlazaPad = 2.6f;       // 节点台半径 = 建筑包围盒半宽 + 这个余量（1.4→2.6 加宽：台子更缓、连过去的路不被拉细）
    // S5 背景山川散布（图外留白处成链摆山脉+山脚岩树，"古地图味"；确定性哈希、禁无种子 Random）
    // 背景山脉成「链」散布（2026-06-25 用户：均匀环太假、要自然错落有致）——几条链、每链 2-4 座成脉、链间留空、大小远近随机
    const int   BackgroundChainCount = 5;     // 山脉链数（链中心随机角、随机深度 → 不均匀）
    const float BackgroundMountainScale = 1.1f; // 山脉基准缩放（×0.55~1.55 哈希抖动 → 大小错落）
    const float BackgroundRingFracInner = 0.62f; // 链中心最近半径 = 大地图(背景地形)半幅 × 此（外推、配合 radius keep-out 山不挡路）
    const float BackgroundRingFracOuter = 0.96f; // 链中心最远半径 = 半幅 × 此（到边界红线）；链中心在 [inner,outer] 随机 → 远近错落
    const float BackgroundChainStep = 20f;    // 链内相邻山沿链方向间距（成脉互相搭叠）
    const float BackgroundChainJitter = 9f;   // 链内垂直链方向抖动（脉不成直线）
    const float BackgroundKeepout = 3.5f;     // 背景景物距路/河中心线最小距离（不挡玩法可读区）
    const float BackgroundMountainBaseWidth = 32f; // 背景山基准世界宽度(m)；主峰 ×1.7~2.6、小丘 ×0.55~1.2 → 大小错落、按此归一两种 prefab 尺寸
    const float RiverMeanderAmp = 0.85f;   // 河道蜿蜒幅度（中心线垂直偏移上限，端点0、中间最大）→ 河不再直直的
    const float RiverMeanderFreq = 0.16f;  // 蜿蜒频率（Perlin 采样频率；小=大弯、大=碎弯）
    const float StationSandFadeExtra = 3.0f; // 站点收沙作用半径在干核外再加这么多：站点周围沙三角收成规整小沙滩（越大收得越狠）
    const float RoadCausewayHalfWidth = 1.2f; // 陆路堤半宽：连到水驿的陆路在此半宽内保留实地不挖（窄一点、像路面宽的小堤）
    const float WaterStationRoadGap = -0.8f;  // 陆路堤戳进水塘多深：负=堤头探进塘内 0.8m（短短一截）；路面在堤上、过堤头才沉
    const float WaterStationRoadDip = 0.5f;   // 陆路在水驿端下沉到「水面 - 这个深度」→ 路伸进水塘被水盖住、消失在水边
    const float StationWaterPad = 0.5f;       // 水塘半径 = 建筑包围盒半宽 + 这个余量；只比水驿大一圈（用户：圆别太大、差不多就好）
    const float RoadWaterNarrowFrac = 0.35f;  // 陆路在贴着水中心线处收窄到原宽的这个比例（大幅收窄、过河/近河处变细）
    const float RoadWaterNarrowDist = 2.5f;   // 到水中心线 ≤此距离 → 收到最窄
    const float RoadWaterNarrowFull = 5.0f;   // 到水中心线 ≥此距离 → 恢复原宽（中间平滑过渡）
    const float BoatRiverAngleOffset = 0f;    // 船身顺河时的额外偏转角（船模型长轴若不是 +Z，用它校正 ±90）
    const float BoatModelRestHeading = 0f;    // 船模型静止时长轴的世界朝向(度)：0=沿+Z（同一母版恒定，别再按包围盒长边自动判、近正方形会判反）
    const float BoatAlongRiverOffset = 1.33f; // 船心相对站点锚点：沿河方向偏移(m)（用户手调验收：船坐进河道、离站约这么远）
    const float BoatAcrossRiverOffset = -0.30f; // 船心相对站点锚点：垂直河方向偏移(m)（基本压在中心线、略偏一侧）
    const float CarveEndTaperFrac = 0.18f; // 沿河两端更长收口到 0（端点是驿站/码头，不能挖坑淹建筑）
    const float WaterDepthFrac = 0.3f;     // 水位（0.55→0.3 水面更靠近岸顶、河道水更满，露出的沙岸更薄）
    const float WaterDrapeWidth = 4.2f;    // 水面宽：半宽2.1 几乎填满河槽(半宽2.4)，消掉裸沙浴缸、水贴岸→水与河谷一体（S1 一体化）
    const float WaterDrapeLift = 0f;       // 水位不再额外抬（碗形：水面=岸顶-WaterDepthBelowBank；行船点/交汇共用）
    const float WaterDepthBelowBank = 1.26f; // 网格顶点几何高度（注意：这个 WaterShader 渲染时不看顶点Y，只看物体 transform.Y → 见 WaterRenderHeight）
    const float WaterRenderHeight = -0.15f;  // ★★ 真正生效的水面高度 = 水路物体 transform.Y。WaterShader 按物体位置渲染水面、无视网格顶点Y。用户验收值 -0.15
    const float RiverBedWidth = 4.8f;      // 沙床宽：半宽2.4 配合收窄后河谷，盖满不露草
    const int RiverBedCrossSegs = 16;      // 沙床/水面横向细分段数（10→16 水面更顺滑、少块状）
    const float RiverEndpointClearFrac = 0.06f; // 只保护紧贴驿站/码头的起止点；其余交汇道路必须压到河床下
    const float RiverBedEndpointClearDistance = 0.08f; // 河床必须铺到节点附近，避免透明水下露草地
    const float RoadWaterEndpointClearDistance = 5.2f; // 水驿/码头附近道路接头不能被河道下压吃掉
    const float WaterSurfaceEndTaperFrac = 0.14f; // 水面端点做宽度渐变，不再硬裁首尾
    const float WaterSurfaceEndMinWidthFrac = 0.68f; // 端点仍保留足够水面，避免水驿/码头旁断水露草
    const float WaterSurfaceEndpointDrop = 0.2f; // 端点水位额外下压（0.44→0.2 别压太狠致端点水量看着空；建筑已被 LiftWaterStations 抬高）
    const float WaterOverlayLift = 0.05f;  // 水面高光带高于水面
    const float WaterJunctionWaterReach = 1.6f; // 水驿多水路交汇：只补端口水舌，不替代主河床（收窄）
    const float WaterJunctionBedReach = 1.7f;
    const float WaterJunctionWaterWidth = 1.0f;
    const float WaterJunctionBedWidth = 2.4f;
    const float WaterJunctionCoreWaterRadius = 3.2f; // 水驿交汇水面最小干地避让半径；实际还会按建筑包围盒扩大
    const float WaterJunctionCoreBedRadius = 2.25f;
    // 水驿建筑已被 LiftWaterStationsAboveWater 抬到水面之上，故不必再留很大干地——
    // 把避让收小让河水贴近建筑脚下，呈现"临水/码头"观感（建筑底仍高于水面，不会被淹）。
    const float WaterStationSurfaceClearExtra = 1.35f;
    const float WaterStationSurfaceClearMax = 4.4f;
    const float FormerRiverRestoreHalfWidth = 3.2f;    // 旧水路改陆路时，回填旧河沟的影响半宽
    const float FormerRiverRestoreSampleOffset = 3.6f; // 采两侧岸高的偏移，略大于挖河半宽
    const float FormerRiverRestoreSettle = 0.04f;      // 回填后略低于两侧岸面，保留一点自然起伏
    const float RoadbedUnderlayLift = 0.008f;           // 新生成陆路的砂石路基，低于路肩/路面但高于地表
    const int RoadbedUnderlayCrossSegs = 4;
    // 道路路堤/栈道：道路穿过天然低洼谷地会跟着沉到 y≈-0.82 被谷壁压成黑沟。
    // 解法——把道路走廊下的地形"填洼"抬到接近岸高的平缓 grade，路面坐在抬高的实地上（不悬空），
    // 路堤外侧用斜坡渐隐回原地形；河道下挖效果保留，近水路不抬以保河道不被填。
    const float RoadEmbankSag = 0.2f;        // 路面允许比两端驿站连线略沉这一点，保留自然起伏但不掉进暗沟（无1.5封顶：本质贴连线不是凭空抬）
    const float RoadEmbankSkirt = 2.6f;      // 路堤外侧斜坡宽（超出路基半宽后渐隐回地形）
    const float RoadEmbankSink = 0.12f;      // 地形顶面坐在路面之下这一点点，让路面 ribbon 盖住路堤顶
    const float RoadEmbankRiverKeepout = 1.4f; // 离水路中心线小于此(≈水面半宽)才不抬，只让位真正水槽、不再一刀切吃掉近河路堤

    static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    static string ReportPath => Path.Combine(ProjectRoot, ReportFileName);

    // ============================================================ 菜单入口

    [MenuItem("Tools/Lychee/MultiMap/Apply Map Layout (pick config)")]
    static void ApplyMapLayoutMenu()
    {
        string startDir = Directory.Exists("/Users/lunayuan/Downloads") ? "/Users/lunayuan/Downloads" : ProjectRoot;
        string jsonPath = EditorUtility.OpenFilePanel("选择 map_config.json", startDir, "json");
        if (string.IsNullOrEmpty(jsonPath)) return;

        string suggested = SuggestKey(jsonPath);
        string outAbs = EditorUtility.SaveFilePanelInProject(
            "另存地图场景（绝不能覆盖母版）", "Map_" + suggested, "unity",
            "选择输出场景路径", OutSceneFolder);
        if (string.IsNullOrEmpty(outAbs)) return;

        string mapKey = MapKeyFromSceneName(Path.GetFileNameWithoutExtension(outAbs));
        ApplyLayout(jsonPath, outAbs, mapKey, "Tournament/" + mapKey + ".txt", BaselineConfigPath);
    }

    [MenuItem("Tools/Lychee/MultiMap/Validate Against Current (baseline self-check)")]
    static void ValidateSelf()
    {
        // 一致性自检：用基线 config 本身跑一遍，应「零站点移动、零道路重建」（全部判定为未变）。
        ApplyLayout(BaselineConfigPath, OutSceneFolder + "/Map_SelfCheck.unity",
            "SelfCheck", "Tournament/selfcheck.txt", BaselineConfigPath);
    }

    [MenuItem("Tools/Lychee/MultiMap/Build Tournament (pick folder of 5 configs)")]
    static void BuildTournamentMenu()
    {
        string dir = EditorUtility.OpenFolderPanel("选择含 5 份 map_config.json 的文件夹", ProjectRoot, "");
        if (string.IsNullOrEmpty(dir)) return;
        string[] configs = Directory.GetFiles(dir, "*.json").OrderBy(p => p).ToArray();
        if (configs.Length == 0) { Debug.LogError("[MapLayout] 该文件夹没有 .json"); return; }
        BuildTournament(configs);
    }

    [MenuItem("Tools/Lychee/MultiMap/Rebuild Official Map 1")]
    public static void RebuildOfficialMap1()
    {
        string[] sceneNames = EditorBuildSettings.scenes
            .Where(s => s.enabled && !string.IsNullOrEmpty(s.path))
            .Select(s => Path.GetFileNameWithoutExtension(s.path))
            .Where(n => n == OfficialMap1SceneName || n.StartsWith("Map_map", StringComparison.Ordinal))
            .Distinct()
            .ToArray();
        if (sceneNames.Length == 0) sceneNames = new[] { OfficialMap1SceneName };

        ApplyLayout(RuntimeConfigFolder + "/map1_config.json",
            OutSceneFolder + "/" + OfficialMap1SceneName + ".unity",
            "map1", "Tournament/map1.txt", BaselineConfigPath, sceneNames);
    }

    /// <summary>
    /// 锦标赛批量生成：按字母序把 N 份 config 生成 Map_medium(map1), Map_map2..Map_mapN 场景，
    /// 每张图 streamingReplayPath="Tournament/mapN.txt"，并把全 5 张图场景名写进每张图的
    /// LycheeTournamentController.perMapScenes（切场景链就绪）。第一张正式图（map_1）即 Map_medium。
    /// </summary>
    public static void BuildTournament(string[] configPaths)
    {
        int n = configPaths.Length;
        var keys = Enumerable.Range(1, n).Select(i => "map" + i).ToArray();
        var sceneNames = Enumerable.Range(1, n).Select(OfficialSceneNameForMapIndex).ToArray(); // LoadScene 用的场景名
        for (int i = 0; i < n; i++)
        {
            ApplyLayout(configPaths[i],
                OutSceneFolder + "/" + sceneNames[i] + ".unity",
                keys[i], "Tournament/" + keys[i] + ".txt",
                BaselineConfigPath, sceneNames);
        }
        Debug.Log($"[MapLayout] 锦标赛 {n} 张图已生成：{string.Join(", ", sceneNames)}。" +
            $"回放放 StreamingAssets/Tournament/map1.txt..map{n}.txt；map_1 = {OfficialMap1SceneName}。");
    }

    // ============================================================ 可编程入口

    /// <summary>按 map_config.json 在母版副本上做差异化重布局并另存。</summary>
    /// <param name="mapConfigJsonPath">新 map_config 绝对路径或工程相对路径。</param>
    /// <param name="outScenePath">输出场景（Assets/...），绝不能等于母版主场景。</param>
    /// <param name="mapKey">网格资产目录/默认回放名用的唯一键（如 "map2"）。configs 共享 mapId，故用它区分。</param>
    /// <param name="replayRelative">写入场景内 player.streamingReplayPath 的回放相对路径（StreamingAssets 内）。</param>
    /// <param name="baselineConfigPath">母版当前所代表的基线 config；用于差异判定。空则退化为「全部重建」。</param>
    /// <param name="perMapSceneNames">非空时写进本场景 LycheeTournamentController.perMapScenes（锦标赛切场景链）。</param>
    public static bool ApplyLayout(
        string mapConfigJsonPath, string outScenePath, string mapKey,
        string replayRelative, string baselineConfigPath, string[] perMapSceneNames = null)
    {
        var report = new List<string>
        {
            "LycheeMapLayoutGenerator_20260613 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            "config=" + mapConfigJsonPath,
            "outScene=" + outScenePath + "  mapKey=" + mapKey + "  replay=" + replayRelative,
            "baseline=" + (baselineConfigPath ?? "<none -> rebuild all>"),
        };
        try
        {
            outScenePath = ToProjectRelative(outScenePath);

            // 0. 防覆盖母版。
            if (PathEquals(outScenePath, MasterScenePath))
            { Abort(report, "outScenePath 等于母版主场景，已拒绝（防覆盖）。"); return false; }

            // 0b. 母版若有未保存改动则中止（避免把脏盘态克隆出去 / 误存母版）。
            Scene activeNow = SceneManager.GetActiveScene();
            if (PathEquals(activeNow.path, MasterScenePath) && activeNow.isDirty)
            { Abort(report, "母版场景当前有未保存改动，请先保存或撤销后再生成（防误存母版）。"); return false; }

            // 1. 读新 config + 基线 config。
            string configJson = File.ReadAllText(ToAbsolute(mapConfigJsonPath));
            LycheeUiMapConfig config = ReadConfigText(configJson);
            if (config == null || config.grid == null || config.nodes == null ||
                config.edges == null || config.routePaths == null)
            { Abort(report, "新 config 结构无效（grid/nodes/edges/routePaths 缺失）。"); return false; }
            LycheeUiMapConfig baseline = null;
            if (!string.IsNullOrEmpty(baselineConfigPath))
            {
                try { baseline = ReadConfig(baselineConfigPath); }
                catch (Exception bex) { report.Add("[警告] 基线 config 读取异常，退化为全部重建: " + bex.Message); }
                if (baseline == null) report.Add("[警告] 基线 config 解析为 null（JSON 损坏？），退化为全部重建: " + baselineConfigPath);
            }

            int gridW = config.grid.width, gridH = config.grid.height;
            float tile = config.grid.tileSize <= 0f ? 1f : config.grid.tileSize;
            report.Add($"config: mapId={config.mapId} name={config.mapName} nodes={config.nodes.Count} " +
                       $"edges={config.edges.Count} routePaths={config.routePaths.Count} grid={gridW}x{gridH} tile={tile}");

            var newEdgeInfo = config.edges.ToDictionary(e => e.edgeId, e => e);
            var newRoutePath = config.routePaths.Where(p => !string.IsNullOrEmpty(p.edgeId))
                                                .GroupBy(p => p.edgeId).ToDictionary(g => g.Key, g => g.First());
            var newNode = config.nodes.ToDictionary(n => n.nodeId, n => n);

            // 基线索引（判定「没变」用）。
            var baseEdgeInfo = baseline?.edges?.ToDictionary(e => e.edgeId, e => e) ?? new Dictionary<string, LycheeEdgeInfo>();
            var baseRoutePath = baseline?.routePaths?.Where(p => !string.IsNullOrEmpty(p.edgeId))
                                   .GroupBy(p => p.edgeId).ToDictionary(g => g.Key, g => g.First())
                                   ?? new Dictionary<string, LycheeRoutePath>();
            var baseNode = baseline?.nodes?.ToDictionary(n => n.nodeId, n => n) ?? new Dictionary<string, LycheeNodeInfo>();

            ValidateFlexibleConfig(config, configJson, report);

            // 2. 克隆母版 → 打开副本（绝不在母版上改）。
            EnsureFolder(OutSceneFolder);
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(outScenePath) != null)
                AssetDatabase.DeleteAsset(outScenePath);
            if (!AssetDatabase.CopyAsset(MasterScenePath, outScenePath))
            { Abort(report, "复制母版场景失败: " + MasterScenePath + " -> " + outScenePath); return false; }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Scene scene = EditorSceneManager.OpenScene(outScenePath, OpenSceneMode.Single);
            report.Add("已克隆母版并打开副本: " + outScenePath);

            // 3. 取现役地形高度场（只读采样）。
            MapElevationData elevation = UnityEngine.Object.FindFirstObjectByType<MapElevationData>();
            if (elevation == null) { Abort(report, "副本里没有 MapElevationData，无法采样地形高度，中止。"); return false; }

            // 4. 索引场景：锚点、道路边、各类型模板、彩带根/模板、路层父级。
            var anchorById = new Dictionary<string, MapStationAnchor>();
            foreach (var a in UnityEngine.Object.FindObjectsByType<MapStationAnchor>(FindObjectsSortMode.None))
                if (!string.IsNullOrEmpty(a.stationId)) anchorById[a.stationId] = a;

            var edgeById = new Dictionary<string, MapRouteEdge>();
            foreach (var e in UnityEngine.Object.FindObjectsByType<MapRouteEdge>(FindObjectsSortMode.None))
                if (!string.IsNullOrEmpty(e.edgeId)) edgeById[e.edgeId] = e;

            BuildBusyJunctions(config, anchorById);  // 路口收窄：找出汇聚>2条路/河的"忙站"，路面末端在其附近收窄、不挤成团

            Transform overlayRoot = FindAnyByName(OverlayRootName);
            if (!EnableOverlayRibbons && overlayRoot != null)  // 去彩带：禁用母版克隆来的所有彩带子物体
                foreach (Transform c in overlayRoot) c.gameObject.SetActive(false);
            var sceneCtx = new SceneContext
            {
                elevation = elevation, gridW = gridW, gridH = gridH, tile = tile,
                overlayRoot = overlayRoot,
                meshDir = MeshRootFolder + "/MapLayout_" + Sanitize(mapKey),
                mapKey = mapKey,
            };
            EnsureFolderRecursive(sceneCtx.meshDir);
            BuildTerrainSurface(sceneCtx);
            if (RestoreFormerRiverbeds(edgeById, newEdgeInfo, sceneCtx, report) > 0)
                BuildTerrainSurface(sceneCtx);
            // 平面基底重构 第1步：把基底地形压成平面 Y=0（建路/挖河之前；之后 ConformY 取平面、CarveRivers 在平面挖河）。
            FlattenBaseTerrain(sceneCtx, report);
            report.Add($"地形采样器：三角形 {sceneCtx.terrainSurface.TriangleCount} 个 / 地形网格 {sceneCtx.terrainFilters.Count} 个（已压平为平面 Y=0，道路/彩带按平面贴地）");
            BuildTypeTemplates(edgeById, newEdgeInfo, overlayRoot, sceneCtx, report);

            // 校验：config 用到的每个路由类型都得有可克隆模板，否则新增该类型边会被静默跳过。
            foreach (var t in config.edges.Select(e => ParseRouteType(e.routeType)).Distinct())
                if (!sceneCtx.edgeTemplate.ContainsKey(t))
                    report.Add($"[警告] 场景缺少 {t} 类型道路模板：该类型的新增边将无法克隆生成（会跳过）。");

            // 预算所有水路中心线：建路时让路在与河重叠处下沉到河床以下（道路最底 < 河床 < 水面/建筑在上）。
            sceneCtx.waterTuples = new List<(MapRouteEdge edge, Vector3[] world)>();
            var waterRelayerTuples = new List<(MapRouteEdge edge, Vector3[] world)>();
            foreach (var we in config.edges)
            {
                if (ParseRouteType(we.routeType) != MapRouteType.Water) continue;
                if (!newRoutePath.TryGetValue(we.edgeId, out var wp) || wp.points == null || wp.points.Count < 2) continue;
                Vector3[] waterWorld = BuildTerrainFollowingRoute(wp.points, WaterLift, sceneCtx);
                sceneCtx.waterTuples.Add((null, waterWorld));
                bool baseSameWaterSamePath =
                    baseEdgeInfo.TryGetValue(we.edgeId, out var bwe) &&
                    ParseRouteType(bwe.routeType) == MapRouteType.Water &&
                    baseRoutePath.TryGetValue(we.edgeId, out var bwp) && SamePath(bwp, wp);
                bool sceneAlreadyHasWater =
                    edgeById.TryGetValue(we.edgeId, out var sceneWaterEdge) &&
                    sceneWaterEdge != null && sceneWaterEdge.routeType == MapRouteType.Water;
                if (!baseSameWaterSamePath || !sceneAlreadyHasWater)
                    waterRelayerTuples.Add((null, waterWorld));
            }

            // 5. 站点：仅坐标变化的才平移。
            int movedStations = 0;
            report.Add("--- 站点 ---");
            foreach (var node in config.nodes)
            {
                if (!anchorById.TryGetValue(node.nodeId, out var anchor) || anchor == null)
                { report.Add($"[跳过] {node.nodeId} {node.name}: 母版无此锚点（新增节点需另行导入建筑模型）"); continue; }

                bool moved = !(baseNode.TryGetValue(node.nodeId, out var bn) && bn.x == node.x && bn.y == node.y);
                if (!moved) continue; // 坐标未变：原样保留，绝不动
                Vector3 oldPos = anchor.transform.position;
                float wx = GridWorldX(node.x, gridW, tile), wz = GridWorldZ(node.y, gridH, tile);
                Vector3 newPos = new Vector3(wx, elevation.SampleWorldHeight(wx, wz) + AnchorLift, wz);
                anchor.transform.position = newPos;
                anchor.gridCoordinate = new Vector2Int(node.x, node.y);
                EditorUtility.SetDirty(anchor.transform); EditorUtility.SetDirty(anchor);
                movedStations++;
                report.Add($"[移动] {node.nodeId} {node.name}: {Fmt(oldPos)} -> {Fmt(newPos)}");
            }

            // 5b. S3 山路抬山脊 + S4 节点分级抬台：站点已移、建路之前；抬完重建采样器 → 路面经 ConformY 自动贴脊/台。
            //     山脊沿山路中心线抬倒U；节点按 ClassifyNodes 分级：N3 山口=脊上再凸 RidgeCrest+RidgePassExtra(全图制高点)、
            //     N1 忙站=+NodeBusyLift 小广场；台子无建筑核守卫(抬建筑脚下)、与脊 max 融合。水驿/码头(N4)走水上、不做陆地台。
            var mountainPaths = new List<Vector3[]>();
            foreach (var me in config.edges)
            {
                if (ParseRouteType(me.routeType) != MapRouteType.Mountain) continue;
                if (!newRoutePath.TryGetValue(me.edgeId, out var mp) || mp.points == null || mp.points.Count < 2) continue;
                mountainPaths.Add(BuildTerrainFollowingRoute(mp.points, 0f, sceneCtx));
            }
            var nodeClass = ClassifyNodes(config);
            var nodePlatforms = new List<(Vector2 c, float r, float h)>();
            var nodeLift = new List<MapStationAnchor>();   // N1/N3 陆地节点：抬台后把建筑吸附到抬起的地形上
            int n1c = 0, n3c = 0, n5c = 0;
            foreach (var node in config.nodes)
            {
                if (node == null || !nodeClass.TryGetValue(node.nodeId, out var nc)) continue;
                if (!anchorById.TryGetValue(node.nodeId, out var a) || a == null) continue;
                if (nc.n5) n5c++;
                if (IsWaterStationOrDock(a)) continue;     // 临水驿站/码头不抬陆地台（它们坐进水里）
                Vector2 pc = new Vector2(a.transform.position.x, a.transform.position.z);
                float r = BuildingRadius(a) + NodePlazaPad;
                if (nc.n3)      { nodePlatforms.Add((pc, r, RidgeCrest + RidgePassExtra)); nodeLift.Add(a); n3c++; } // 山口=全图制高点
                else if (nc.n1) { nodePlatforms.Add((pc, r, NodeBusyLift));               nodeLift.Add(a); n1c++; } // 忙站小广场
            }
            if (mountainPaths.Count > 0 || nodePlatforms.Count > 0)
            {
                RaiseMountains(mountainPaths, nodePlatforms, sceneCtx, anchorById, sceneCtx.waterTuples, null, report);
                BuildTerrainSurface(sceneCtx); // 重建采样器 → 后续建路 ConformY 取到抬起的脊/台
                // 把 N1/N3 建筑吸附到抬起的地形顶（否则建筑留在 0、陷进抬起的台里）
                foreach (var a in nodeLift)
                {
                    var p = a.transform.position;
                    if (sceneCtx.terrainSurface != null && sceneCtx.terrainSurface.TrySampleTop(p.x, p.z, out float ty))
                    { a.transform.position = new Vector3(p.x, ty + AnchorLift, p.z); EditorUtility.SetDirty(a.transform); }
                }
                report.Add($"[山脊/节点台] 山路 {mountainPaths.Count} 条 + 抬台 N1忙站×{n1c}/N3山口×{n3c}（N5官道广场×{n5c} 保持平整、靠铺装区分），采样器已重建");
            }

            // 6. 道路：差异化处理。
            int rebuilt = 0, cloned = 0, retyped = 0, unchanged = 0, disabled = 0;
            var waterEdges = new List<(MapRouteEdge edge, Vector3[] world)>(); // 新增/重建的水路，留待挖河凹陷
            var changedEdges = new List<(MapRouteEdge edge, Vector3[] world, MapRouteType type)>(); // 新增/重建边，留待清理压路植被
            report.Add("--- 道路 ---");
            foreach (var edge in config.edges)
            {
                MapRouteType type = ParseRouteType(edge.routeType);
                if (!newRoutePath.TryGetValue(edge.edgeId, out var path) || path.points == null || path.points.Count < 2)
                { report.Add($"[跳过] {edge.edgeId}: config 无有效 routePath（<2 点）"); continue; }

                // 分类
                bool existsInScene = edgeById.TryGetValue(edge.edgeId, out var sceneEdge) && sceneEdge != null;
                bool baseSameTypeSamePath =
                    baseEdgeInfo.TryGetValue(edge.edgeId, out var be) &&
                    ParseRouteType(be.routeType) == type &&
                    baseRoutePath.TryGetValue(edge.edgeId, out var bp) && SamePath(bp, path);

                if (baseSameTypeSamePath && existsInScene && sceneEdge.routeType == type)
                {
                    // 几何完全没变：原样保留（不重画，保住贴合成果）；仅同步端点/距离元数据，避免陈旧引用。
                    if (sceneEdge.fromStationId != edge.fromNodeId || sceneEdge.toStationId != edge.toNodeId ||
                        sceneEdge.bidirectional != edge.bidirectional ||
                        !Mathf.Approximately(sceneEdge.logicalDistance, edge.distance))
                    {
                        sceneEdge.fromStationId = edge.fromNodeId;
                        sceneEdge.toStationId = edge.toNodeId;
                        sceneEdge.bidirectional = edge.bidirectional;
                        sceneEdge.logicalDistance = edge.distance;
                        EditorUtility.SetDirty(sceneEdge);
                    }

                    Vector3[] relayerWorld = null;
                    bool forceWaterRelayer = false;
                    if (type != MapRouteType.Water && waterRelayerTuples.Count > 0)
                    {
                        relayerWorld = BuildTerrainFollowingRoute(path.points, LiftFor(type), sceneCtx);
                        forceWaterRelayer = RouteOverlapsWaters(relayerWorld, waterRelayerTuples,
                            CarveHalfWidth + RoadbedWidthFor(type) * 0.5f + 0.2f);
                    }
                    if (forceWaterRelayer)
                    {
                        sceneCtx.endRampOn = false;
                        if (anchorById.TryGetValue(edge.fromNodeId, out var fromAnchorRelayer) && fromAnchorRelayer != null &&
                            anchorById.TryGetValue(edge.toNodeId, out var toAnchorRelayer) && toAnchorRelayer != null)
                        {
                            var fp = fromAnchorRelayer.transform.position; var tp = toAnchorRelayer.transform.position;
                            sceneCtx.endA = new Vector2(fp.x, fp.z); sceneCtx.endB = new Vector2(tp.x, tp.z);
                            sceneCtx.endAY = fp.y - RoadTuckUnder; sceneCtx.endBY = tp.y - RoadTuckUnder;
                            sceneCtx.endRampA = BuildingRadius(fromAnchorRelayer) + 0.2f;
                            sceneCtx.endRampB = BuildingRadius(toAnchorRelayer) + 0.2f;
                            sceneCtx.endRampOn = true;
                        }
                        RebuildEdgeInPlace(sceneEdge, edge, type, relayerWorld, relayerWorld, sceneCtx, report);
                        changedEdges.Add((sceneEdge, relayerWorld, type));
                        rebuilt++;
                        report.Add($"[交汇重画] {edge.edgeId}: 与新增/变化水路交汇，保持道路连续并刷新贴地");
                        continue;
                    }
                    if (type == MapRouteType.Water)
                    {
                        Vector3[] waterWorld = BuildTerrainFollowingRoute(path.points, WaterLift, sceneCtx);
                        RebuildPathPoints(sceneEdge, waterWorld);
                        waterEdges.Add((sceneEdge, waterWorld));
                        changedEdges.Add((sceneEdge, waterWorld, type));
                        rebuilt++;
                        report.Add($"[水路质量刷新] {edge.edgeId}: 保留对象但重铺统一河床/水面，避免新旧河道观感不一致");
                        continue;
                    }

                    // 平面基底重构：原"陆路几何未变就原样保留"会把按旧起伏地形烘的路面+pathPoints 留在半空
                    // （压平后实测浮到 +6.4m，秦岭山路尤甚）。平面上 ConformY 直接贴平、重建成本低，故不再保留，
                    // 落到下方通用分支按平面重建（路面网格与 pathPoints 一起到 Y≈lift）。水路已在上面单独处理。
                    report.Add($"[平面重建] {edge.edgeId} ({type}): 路径未变但按平面基底重铺（旧几何会悬空）");
                }

                Vector3[] world = BuildTerrainFollowingRoute(path.points, LiftFor(type), sceneCtx);

                // 路面/彩带整条铺到驿站（不裁，无缺口），但两端"压到建筑底下"：在建筑足迹内把路面顶点降到
                // 驿站地面以下，让建筑（含码头/船）盖住路头——既不留缺口、也不盖在建筑/船上面。水路不处理。
                Vector3[] ribbonWorld = world;
                sceneCtx.endRampOn = false;
                if (type != MapRouteType.Water &&
                    anchorById.TryGetValue(edge.fromNodeId, out var fa) && fa != null &&
                    anchorById.TryGetValue(edge.toNodeId, out var ta) && ta != null)
                {
                    // 路面整条铺过去；两端压低：陆站端压到建筑底下（楼盖路头）；水驿端一路下沉到水面以下 →
                    // 陆路伸进水塘、被水盖住「消失在水边」（像下水的埠头）。压降范围水驿端取整个塘半径。
                    var fp = fa.transform.position; var tp = ta.transform.position;
                    sceneCtx.endA = new Vector2(fp.x, fp.z); sceneCtx.endB = new Vector2(tp.x, tp.z);
                    sceneCtx.endAY = IsWaterStation(fa) ? WaterRenderHeight - WaterStationRoadDip : fp.y - RoadTuckUnder;
                    sceneCtx.endBY = IsWaterStation(ta) ? WaterRenderHeight - WaterStationRoadDip : tp.y - RoadTuckUnder;
                    // 水驿端 endRamp 范围 = 堤头(塘半径+WaterStationRoadGap)：路在堤上保持地面高、过堤头才往下沉 → 自然下水、不被堤埋
                    sceneCtx.endRampA = IsWaterStation(fa) ? WaterStationPondRadius(fa) + WaterStationRoadGap : BuildingRadius(fa) + 0.2f;
                    sceneCtx.endRampB = IsWaterStation(ta) ? WaterStationPondRadius(ta) + WaterStationRoadGap : BuildingRadius(ta) + 0.2f;
                    // 砂石路基(土黄)在水驿端往岸上收一截：灰路面照样沉进水，但黄路基不探到水边、不"多出来没接住"
                    sceneCtx.endAUnderlayTrim = IsWaterStation(fa) ? WaterStationPondRadius(fa) + 2.5f : 0f;
                    sceneCtx.endBUnderlayTrim = IsWaterStation(ta) ? WaterStationPondRadius(ta) + 2.5f : 0f;
                    sceneCtx.endRampOn = true;
                }

                MapRouteEdge resultEdge = null;
                if (existsInScene && sceneEdge.routeType == type)
                {
                    // 同类型、仅折线变化：原地重画（保住对象身份与额外定制）。
                    RebuildEdgeInPlace(sceneEdge, edge, type, world, ribbonWorld, sceneCtx, report);
                    resultEdge = sceneEdge;
                    rebuilt++;
                }
                else
                {
                    // 新增 或 改类型：克隆同类型模板重建。
                    if (existsInScene)
                    {
                        // 改类型：删旧边对象 + 旧彩带。
                        DestroyOverlayLine(edge.edgeId, sceneCtx);
                        UnityEngine.Object.DestroyImmediate(sceneEdge.gameObject);
                        edgeById.Remove(edge.edgeId);
                        retyped++;
                    }
                    else cloned++;

                    resultEdge = CloneEdgeFromTemplate(edge, type, world, ribbonWorld, sceneCtx, report);
                    if (resultEdge != null) edgeById[edge.edgeId] = resultEdge;
                }

                if (resultEdge != null) changedEdges.Add((resultEdge, world, type));
                // 水路：登记留待挖河（凹陷河道 + 沉水面），与现役 E12/E13 同质量。
                if (type == MapRouteType.Water && resultEdge != null)
                    waterEdges.Add((resultEdge, world));
            }

            // 6b0. 道路路堤：收集**全部陆路边**(changed 用新 world，unchanged 用现有 pathPoints world)——
            //      否则只处理改动过的边、大部分老路永远留在沟里。BuildRoadEmbanks 与 LiftRoads 必须吃**同一份列表
            //      (同 world 引用)**，否则地形抬了、路面网格没抬 → 路面入地 + 小队悬空(复核重点警告)。
            // —— 路堤抬升暂时关闭：端点连线对"码头等低端点站"算不出抬升、又让路面颠簸；
            //    用户要"道路贴着地形、别坑坑洼洼"，故让道路回到纯贴地(ConformY)、不做抬升。
            _roadEmbanks = new List<(Vector3[] world, float[] grade, float[] delta, float halfW)>();

            // 6b. 新河道凹陷：下挖地形成河谷 + 沉水面（每图地形是独立副本，安全）。
            //     CarveRivers 内部在 carve 之后顺带把道路走廊下的地形/高度场抬到路面之下（近水路不抬以保河道）。
            CarveRivers(waterEdges, sceneCtx, anchorById, report);
            SinkExtendedGroundBelowMain(report);  // 第二张底板下沉一点，消与主地形共面的 z-fighting（草上沙/石疯狂闪动）
            // 6b2. 路堤已停用：道路保持纯贴地，不再抬升网格/pathPoints。
            // BuildWaterJunctionPools(...);  // 去掉交汇池：水面已铺满全河，不需要补丁（用户要求）
            // 水驿/码头各部件高度用用户手调并验收的值（按站型烘）；取代旧的整体抬升 LiftWaterStationsAboveWater。
            ApplyWaterStationPartHeights(anchorById, report);
            OrientStationBoatsAlongRiver(anchorById, report);  // 船身顺着河岸/水流停靠（对齐水路切向）
            HideWaterStationGroundPads(anchorById, report);  // 去掉水驿/码头脚下的夯土台：改为架在水上后不需旱台
            BuildWaterStationPonds(anchorById, sceneCtx, report);  // 给水驿脚下铺圆水塘：背水侧也进水、整个水驿坐在水中
            CleanupTransientRestoredTerrainAssets(sceneCtx, report);
            ScatterBackgroundScenery(sceneCtx, anchorById, report);  // S5 图外留白处沿边环抱背景山脉+山脚岩树（"古地图味"）

            // 6c. 清障：清掉压在新路/新河上的植被、岩石（原装饰按旧路布的，会挡新路）。
            ClearDecorationAlongPaths(changedEdges, sceneCtx, report);
            // 6c2. 专清河道走廊 + 码头/水驿足迹内的草丛：挖河把地表降了、草丛却贴老地表悬在河道里，
            //      且现有沿中心线清障半径够不到码头四周（江南码头交错混乱的一大成因）。
            ClearGrassInRiverAndDockFootprint(waterEdges, anchorById, report);

            // config 里没有、场景里还有的边 → 禁用 + 报告。
            var configEdgeIds = new HashSet<string>(newEdgeInfo.Keys);
            foreach (var kv in edgeById.ToList())
            {
                if (configEdgeIds.Contains(kv.Key) || kv.Value == null) continue;
                kv.Value.gameObject.SetActive(false);
                SetOverlayLineActive(kv.Key, false, sceneCtx);
                disabled++;
                report.Add($"[禁用] {kv.Key}: config 中不存在，已禁用其道路与彩带（保守不删）");
            }

            // 7. 绑定本场景的回放路径（分场景换回放）。
            int playersTagged = 0;
            foreach (var player in UnityEngine.Object.FindObjectsByType<LycheeReplayPlayer>(FindObjectsSortMode.None))
            {
                player.streamingReplayPath = string.IsNullOrEmpty(replayRelative) ? "replay.txt" : replayRelative;
                EditorUtility.SetDirty(player);
                playersTagged++;
            }
            report.Add($"player.streamingReplayPath = {replayRelative}（{playersTagged} 个 player）");
            ArchiveMapConfig(mapConfigJsonPath, mapKey, report);

            // 7b. 锦标赛切场景链：把全图场景名写进本场景的控制器。
            if (perMapSceneNames != null && perMapSceneNames.Length > 0)
            {
                int wired = 0;
                foreach (var ctrl in UnityEngine.Object.FindObjectsByType<LycheeTournamentController>(FindObjectsSortMode.None))
                {
                    ctrl.perMapScenes = (string[])perMapSceneNames.Clone();
                    ctrl.maxProbe = Mathf.Clamp(perMapSceneNames.Length, 1, 16);
                    EditorUtility.SetDirty(ctrl);
                    wired++;
                }
                report.Add($"perMapScenes 写入 {wired} 个控制器: [{string.Join(", ", perMapSceneNames)}]");
            }

            // 8. 保存副本 + 登记 Build Settings。
            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene, outScenePath);
            RegisterInBuildSettings(outScenePath, report);

            report.Add($"站点移动 {movedStations}；道路 未变 {unchanged}/原地重画 {rebuilt}/克隆新增 {cloned}/改类型重建 {retyped}/禁用 {disabled}；保存={saved}");
            report.Add("[说明] 未触碰任何贴合脚本 .done（不唤醒母版轮询）。站点移动时如需二次贴地，"
                + "用 LycheeStationLift/RibbonRefit 的 RunOnScene 入口对本副本单独跑。");
            WriteReport(report);
            Debug.Log("[MapLayout] 完成。报告: " + ReportPath + "  输出: " + outScenePath);
            return saved;
        }
        catch (Exception ex)
        {
            report.Add("EXCEPTION: " + ex);
            WriteReport(report);
            Debug.LogError("[MapLayout] 异常: " + ex.Message + "\n" + ex.StackTrace);
            return false;
        }
    }

    // ============================================================ 场景上下文 + 模板

    sealed class SceneContext
    {
        public MapElevationData elevation;
        public int gridW, gridH;
        public float tile;
        public Transform overlayRoot;
        public string meshDir;
        public string mapKey;
        public SurfaceSampler terrainSurface;   // 现役地形网格三角形池（原始，挖河前），道路/彩带贴地的真实采样源
        public List<MeshFilter> terrainFilters = new();  // 地形网格（挖河时下挖其顶点）
        public readonly List<string> transientRestoredTerrainAssets = new();
        // 路端"压到建筑底下"参数（每条路建前设；CreateRibbonMesh 在建筑足迹内把路面顶点降到驿站地面下）：
        public bool endRampOn;
        public Vector2 endA, endB;
        public float endAY, endBY, endRampA, endRampB;
        public float endAUnderlayTrim, endBUnderlayTrim;  // 砂石路基在该端裁掉此半径内的点（水驿端用，让黄路基不探到水边）
        // 所有水路中心线（建路前预算）：路在与河重叠处下沉到河床以下，实现"道路最底/河床/水面+建筑在上"的层级。
        public List<(MapRouteEdge edge, Vector3[] world)> waterTuples;
        public readonly Dictionary<MapRouteType, MapRouteEdge> edgeTemplate = new();
        public readonly Dictionary<MapRouteType, Transform> routeLayer = new();
        public readonly Dictionary<MapRouteType, Transform> overlayTemplate = new();
    }

    struct WaterEndpointDir
    {
        public string edgeId;
        public Vector2 dir;
    }

    /// <summary>为每个路由类型挑一个现役道路作克隆模板，并记录其路层父级与对应彩带模板。</summary>
    static void BuildTypeTemplates(Dictionary<string, MapRouteEdge> edgeById,
        Dictionary<string, LycheeEdgeInfo> newEdgeInfo, Transform overlayRoot, SceneContext ctx, List<string> report)
    {
        IEnumerable<MapRouteEdge> StableFirst()
        {
            foreach (MapRouteEdge edge in edgeById.Values)
            {
                if (edge == null || string.IsNullOrEmpty(edge.edgeId)) continue;
                if (newEdgeInfo != null &&
                    newEdgeInfo.TryGetValue(edge.edgeId, out LycheeEdgeInfo info) &&
                    ParseRouteType(info.routeType) == edge.routeType)
                    yield return edge;
            }
            foreach (MapRouteEdge edge in edgeById.Values)
            {
                if (edge == null || string.IsNullOrEmpty(edge.edgeId)) continue;
                if (newEdgeInfo != null &&
                    newEdgeInfo.TryGetValue(edge.edgeId, out LycheeEdgeInfo info) &&
                    ParseRouteType(info.routeType) == edge.routeType)
                    continue;
                yield return edge;
            }
        }

        foreach (var e in StableFirst())
        {
            if (e == null) continue;
            if (!ctx.edgeTemplate.ContainsKey(e.routeType))
            {
                ctx.edgeTemplate[e.routeType] = e;
                if (e.transform.parent != null) ctx.routeLayer[e.routeType] = e.transform.parent;
                Transform line = FindOverlayLine(e.edgeId, overlayRoot);
                if (line != null) ctx.overlayTemplate[e.routeType] = line;
            }
        }
        report.Add("路由模板: " + string.Join(", ",
            ctx.edgeTemplate.Select(kv => $"{kv.Key}<-{kv.Value.edgeId}(overlay={(ctx.overlayTemplate.ContainsKey(kv.Key) ? "有" : "无")})")));
    }

    // ============================================================ 原地重画（同类型，仅折线变）

    static void RebuildEdgeInPlace(MapRouteEdge edge, LycheeEdgeInfo info, MapRouteType type,
        Vector3[] world, Vector3[] ribbonWorld, SceneContext ctx, List<string> report)
    {
        edge.fromStationId = info.fromNodeId;
        edge.toStationId = info.toNodeId;
        edge.routeType = type;
        edge.logicalDistance = info.distance;
        edge.bidirectional = info.bidirectional;
        RebuildPathPoints(edge, world);                                                  // 整条路（车队走线）
        RebuildRibbonChildren(edge.gameObject, edge.edgeId, type, ribbonWorld, ctx);     // 路面裁到建筑边缘外
        RepositionSlots(edge.gameObject, world);
        BuildOverlayLine(edge.edgeId, type, ribbonWorld, edge.gameObject, ctx, report);  // 彩带随路面裁
        EditorUtility.SetDirty(edge);
        report.Add($"[原地重画] {edge.edgeId} ({type}) {world.Length} 点");
    }

    // ============================================================ 克隆模板（新增 / 改类型）

    static MapRouteEdge CloneEdgeFromTemplate(LycheeEdgeInfo info, MapRouteType type,
        Vector3[] world, Vector3[] ribbonWorld, SceneContext ctx, List<string> report)
    {
        if (!ctx.edgeTemplate.TryGetValue(type, out var template) || template == null)
        { report.Add($"[失败] {info.edgeId}: 场景里没有 {type} 类型的模板道路可克隆，跳过"); return null; }

        // 母版里可能残留同 edgeId 的旧彩带，但没有对应 MapRouteEdge。
        // 新增/改类型克隆前先清掉，避免 E13_Line_WATER 这类孤立 overlay 与新路并存。
        DestroyOverlayLine(info.edgeId, ctx);

        Transform parent = ctx.routeLayer.TryGetValue(type, out var layer) && layer != null
            ? layer : template.transform.parent;
        GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, parent);
        clone.name = info.edgeId + "_" + info.fromNodeId + "-" + info.toNodeId;
        clone.SetActive(true);

        var edge = clone.GetComponent<MapRouteEdge>();
        string templateEdgeId = template.edgeId;
        edge.edgeId = info.edgeId;
        edge.fromStationId = info.fromNodeId;
        edge.toStationId = info.toNodeId;
        edge.routeType = type;
        edge.logicalDistance = info.distance;
        edge.bidirectional = info.bidirectional;

        // 子物体改名前缀 templateEdgeId_ -> newEdgeId_（ribbon/slot）。
        foreach (Transform c in clone.transform)
            if (c.name.StartsWith(templateEdgeId + "_"))
                c.name = info.edgeId + c.name.Substring(templateEdgeId.Length);

        // 显式把 obstacleSlot 指向克隆体自己的 EdgeObstacleSlot（防 Instantiate 引用未重映射 → 误改模板槽位）。
        edge.obstacleSlot = FindChild(clone.transform, "EdgeObstacleSlot");

        RebuildPathPoints(edge, world);                                          // 整条路（车队走线）
        RebuildRibbonChildren(clone, info.edgeId, type, ribbonWorld, ctx);       // 路面裁到建筑边缘外
        RepositionSlots(clone, world);
        EditorUtility.SetDirty(edge);

        BuildOverlayLine(info.edgeId, type, ribbonWorld, clone, ctx, report);    // 彩带随路面裁
        report.Add($"[克隆新增] {info.edgeId} ({type}) <- 模板 {templateEdgeId}，{world.Length} 点");
        return edge;
    }

    // ============================================================ pathPoints / 子物体几何

    /// <summary>重建 PathPoint_NN 子物体到新折线（销毁旧的，逐点新建），并回写 edge.pathPoints。</summary>
    static void RebuildPathPoints(MapRouteEdge edge, Vector3[] world)
    {
        var go = edge.gameObject;
        var old = new List<Transform>();
        foreach (Transform c in go.transform) if (c.name.StartsWith("PathPoint_")) old.Add(c);
        foreach (var t in old) UnityEngine.Object.DestroyImmediate(t.gameObject);

        var pts = new Transform[world.Length];
        for (int i = 0; i < world.Length; i++)
        {
            var marker = new GameObject("PathPoint_" + i.ToString("00"));
            marker.transform.SetParent(go.transform, false);
            marker.transform.position = world[i];
            pts[i] = marker.transform;
        }
        edge.pathPoints = pts;
        if (edge.obstacleSlot == null)
        {
            Transform existing = FindChild(go.transform, "EdgeObstacleSlot");
            if (existing == null)
            {
                var slot = new GameObject("EdgeObstacleSlot");
                slot.transform.SetParent(go.transform, false);
                existing = slot.transform;
            }
            edge.obstacleSlot = existing;
        }
        edge.obstacleSlot.position = world[world.Length / 2];
    }

    /// <summary>按子物体名后缀重画各 ribbon 网格，所有顶点贴合现役地形面（RoadGroundFit 同款），消除沉入地表。
    /// 水路只重画 _Route（凹陷河道由 CarveRiverChannel 单独处理），边/车辙按层次微抬。</summary>
    static void RebuildRibbonChildren(GameObject edgeObject, string edgeId, MapRouteType type,
        Vector3[] world, SceneContext ctx)
    {
        float width = WidthFor(type);
        float lift = LiftFor(type);
        if (type != MapRouteType.Water)
            BuildRoadbedUnderlay(edgeObject, edgeId, type, world, ctx);
        // 主路面：贴地 lift。
        ReplaceRibbon(edgeObject, edgeId + "_Route", world, width, ctx, ctx.terrainSurface, lift);
        switch (type)
        {
            case MapRouteType.OfficialRoad:
                // 路肩略低于路面（-0.012）形成边；车辙略高于路面（+0.004）。
                ReplaceRibbon(edgeObject, edgeId + "_RoadBorder", world, width + 0.52f, ctx, ctx.terrainSurface, lift - 0.012f);
                ReplaceRut(edgeObject, edgeId + "_RutL", world, width, -1, ctx, lift + 0.004f);
                ReplaceRut(edgeObject, edgeId + "_RutR", world, width, 1, ctx, lift + 0.004f);
                break;
            case MapRouteType.Water:
                // 水路质量约束：模板里的中心宽 RiverBank 是旧版黄土贴片，克隆到新河道会显得没有下挖。
                // 新水路统一由 CarveRivers 生成：下挖地形 + 中心沙床 + 窄水面；不生成深色外侧 trim。
                DestroyChild(edgeObject.transform, edgeId + "_RiverBank");
                DestroyChild(edgeObject.transform, edgeId + "_ShallowWater");
                DestroyChild(edgeObject.transform, edgeId + "_L_BankTrim");
                DestroyChild(edgeObject.transform, edgeId + "_R_BankTrim");
                DestroyChild(edgeObject.transform, edgeId + "_L_BankSlope");
                DestroyChild(edgeObject.transform, edgeId + "_R_BankSlope");
                ReplaceRibbon(edgeObject, edgeId + "_WaterHighlight", world, width * 0.24f, ctx, ctx.terrainSurface, lift + 0.012f);
                break;
            case MapRouteType.Mountain:
                // 薄路肩（仅 +0.20）：让有贴图的路面主导，别被边压成一条棕带。
                ReplaceRibbon(edgeObject, edgeId + "_MountainBorder", world, width + 0.20f, ctx, ctx.terrainSurface, lift - 0.012f);
                break;
            case MapRouteType.Branch:
                ReplaceRibbon(edgeObject, edgeId + "_BranchBorder", world, width + 0.18f, ctx, ctx.terrainSurface, lift - 0.012f);
                break;
        }
    }

    /// <summary>砂石路基折线在水驿端裁掉落在 endXUnderlayTrim 半径内的点 → 黄路基止于岸上、不探到水边/塘里（灰路面照样沉进水）。</summary>
    static Vector3[] TrimUnderlayAtWaterEnds(Vector3[] world, SceneContext ctx)
    {
        if (world == null || world.Length < 3) return world;
        if (ctx.endAUnderlayTrim <= 0f && ctx.endBUnderlayTrim <= 0f) return world;
        var r = new List<Vector3>();
        foreach (var p in world)
        {
            var pp = new Vector2(p.x, p.z);
            if (ctx.endAUnderlayTrim > 0f && Vector2.Distance(pp, ctx.endA) < ctx.endAUnderlayTrim) continue;
            if (ctx.endBUnderlayTrim > 0f && Vector2.Distance(pp, ctx.endB) < ctx.endBUnderlayTrim) continue;
            r.Add(p);
        }
        return r.Count >= 2 ? r.ToArray() : world;
    }

    static void BuildRoadbedUnderlay(GameObject edgeObject, string edgeId, MapRouteType type,
        Vector3[] world, SceneContext ctx)
    {
        if (edgeObject == null || world == null || world.Length < 2) return;
        world = TrimUnderlayAtWaterEnds(world, ctx);  // 水驿端把砂石路基往岸上收，不让土黄路基探到水边
        if (world == null || world.Length < 2) return;
        string childName = edgeId + "_RoadbedUnderlay";
        Transform child = FindChild(edgeObject.transform, childName);
        if (child == null)
        {
            var go = new GameObject(childName);
            go.transform.SetParent(edgeObject.transform, false);
            go.transform.SetAsFirstSibling();
            go.AddComponent<MeshFilter>();
            go.AddComponent<MeshRenderer>();
            child = go.transform;
        }

        var mf = child.GetComponent<MeshFilter>();
        var mr = child.GetComponent<MeshRenderer>();
        if (mf == null) return;
        Material mat = FindRoadbedMaterial(type);
        if (mr != null && mat != null) mr.sharedMaterial = mat;
        Mesh mesh = CreateConformedGrid(world, RoadbedWidthFor(type), RoadbedUnderlayCrossSegs,
            childName, child, ctx, RoadbedUnderlayLift);
        mf.sharedMesh = SaveMesh(mesh, ctx.meshDir + "/" + Sanitize(ctx.mapKey) + "_" + childName + ".asset");
        EditorUtility.SetDirty(mf);
        if (mr != null) EditorUtility.SetDirty(mr);
    }

    static float RoadbedWidthFor(MapRouteType type)
    {
        switch (type)
        {
            case MapRouteType.OfficialRoad: return 3.0f;
            case MapRouteType.Mountain: return 2.35f;
            case MapRouteType.Branch: return 1.9f; // S2:支路路床 2.65→1.9(随路面变细)
            default: return WidthFor(type) + 0.8f;
        }
    }

    static Material FindRoadbedMaterial(MapRouteType type)
    {
        string[] preferred;
        switch (type)
        {
            case MapRouteType.OfficialRoad:
                preferred = new[] { "Assets/LycheeMap15/Materials/M_Config_OfficialRoad_Border.mat", "Assets/LycheeMap15/Materials/M_RiverBed_SandGravel.mat" };
                break;
            case MapRouteType.Mountain:
                preferred = new[] { "Assets/LycheeMap15/Materials/M_Config_MountainRoute_Border.mat", "Assets/LycheeMap15/Materials/M_RiverBed_SandGravel.mat" };
                break;
            case MapRouteType.Branch:
                preferred = new[] { "Assets/LycheeMap15/Materials/M_Config_BranchRoute_Border.mat", "Assets/LycheeMap15/Materials/M_RiverBed_SandGravel.mat" };
                break;
            default:
                preferred = new[] { "Assets/LycheeMap15/Materials/M_RiverBed_SandGravel.mat" };
                break;
        }

        foreach (string p in preferred)
        {
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (mat != null) return mat;
        }
        return null;
    }

    static void RepositionSlots(GameObject edgeObject, Vector3[] world)
    {
        Vector3 mid = world[world.Length / 2];
        foreach (Transform c in edgeObject.transform)
            if (c.name.Contains("Slot") && !c.name.StartsWith("PathPoint_") && c.name != "EdgeObstacleSlot")
                c.position = mid;
    }

    /// <summary>重画子物体网格：每顶点贴 primary 采样面（采不到回退地形/高度场）+ lift；顶点存到 child 局部空间。</summary>
    static void ReplaceRibbon(GameObject edgeObject, string childName, Vector3[] points, float width,
        SceneContext ctx, SurfaceSampler primary, float lift)
    {
        Transform child = FindChild(edgeObject.transform, childName);
        if (child == null) return; // 该类型本就没有此子物体 → 静默跳过
        var filter = child.GetComponent<MeshFilter>();
        if (filter == null) return;
        Mesh mesh = CreateRibbonMesh(points, width, childName, ctx, primary, lift, child);
        filter.sharedMesh = SaveMesh(mesh, ctx.meshDir + "/" + Sanitize(ctx.mapKey) + "_" + childName + ".asset");
        EditorUtility.SetDirty(filter);
    }

    static void ReplaceRut(GameObject edgeObject, string childName, Vector3[] points, float roadWidth, int lane,
        SceneContext ctx, float lift)
    {
        Transform child = FindChild(edgeObject.transform, childName);
        if (child == null) return;
        var filter = child.GetComponent<MeshFilter>();
        if (filter == null) return;
        var shifted = new Vector3[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 fwd = i == points.Length - 1 ? points[i] - points[i - 1] : points[i + 1] - points[i];
            fwd.y = 0f;
            Vector3 side = Vector3.Cross(Vector3.up, fwd.sqrMagnitude < 1e-8f ? Vector3.forward : fwd.normalized);
            shifted[i] = points[i] + side * lane * roadWidth * 0.22f;
        }
        Mesh mesh = CreateRibbonMesh(shifted, 0.065f, childName, ctx, ctx.terrainSurface, lift, child);
        filter.sharedMesh = SaveMesh(mesh, ctx.meshDir + "/" + Sanitize(ctx.mapKey) + "_" + childName + ".asset");
        EditorUtility.SetDirty(filter);
    }

    // ============================================================ 彩带 overlay

    static Transform FindOverlayLine(string edgeId, Transform overlayRoot)
    {
        if (overlayRoot == null) return null;
        foreach (Transform c in overlayRoot)
            if (IsOverlayForEdge(c, edgeId)) return c;
        return null;
    }

    static void DestroyOverlayLine(string edgeId, SceneContext ctx)
    {
        var matches = new List<GameObject>();
        if (ctx.overlayRoot != null)
            foreach (Transform c in ctx.overlayRoot)
                if (IsOverlayForEdge(c, edgeId))
                    matches.Add(c.gameObject);
        foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
            if (t != null && t.gameObject.scene.IsValid() && IsLegacyEdgeVisualName(t.name, edgeId))
                matches.Add(t.gameObject);

        foreach (GameObject match in matches)
            if (match != null)
                UnityEngine.Object.DestroyImmediate(match);
    }

    static void SetOverlayLineActive(string edgeId, bool active, SceneContext ctx)
    {
        if (ctx.overlayRoot != null)
            foreach (Transform c in ctx.overlayRoot)
                if (IsOverlayForEdge(c, edgeId))
                    c.gameObject.SetActive(active);
        if (!active)
            foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
                if (t != null && t.gameObject.scene.IsValid() && IsLegacyEdgeVisualName(t.name, edgeId))
                    t.gameObject.SetActive(false);
    }

    static bool IsOverlayForEdge(Transform line, string edgeId)
    {
        return line != null && line.name != "Route_Arrows" && line.name.Split('_')[0] == edgeId;
    }

    static bool IsLegacyEdgeVisualName(string name, string edgeId)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(edgeId)) return false;
        if (name.StartsWith(edgeId + "_Line_", StringComparison.Ordinal)) return true;
        return name == edgeId + "_L_BankTrim" || name == edgeId + "_R_BankTrim" ||
               name == edgeId + "_L_BankSlope" || name == edgeId + "_R_BankSlope" ||
               name == edgeId + "_RiverBed";
    }

    /// <summary>建/重画一条彩带 overlay：贴在新路面上方（RibbonRefit 同款，采路面顶+OverlayLift，
    /// 采不到回退地形），宽度按类型实测值，避免被压进路面/地表、并消除宽度不统一。
    /// 已有同 edgeId 彩带则原地重画其网格；没有则克隆同类型模板（继承彩带材质）。</summary>
    static void BuildOverlayLine(string edgeId, MapRouteType type, Vector3[] world, GameObject roadObject,
        SceneContext ctx, List<string> report)
    {
        if (!EnableOverlayRibbons || ctx.overlayRoot == null) return;

        // 用新路面三角形建采样器，让彩带稳稳贴在路面上方。
        var roadSurface = new SurfaceSampler();
        foreach (var mf in roadObject.GetComponentsInChildren<MeshFilter>(true))
            if (mf.sharedMesh != null && mf.name.EndsWith("_Route")) roadSurface.AddMesh(mf);

        Transform line = FindOverlayLine(edgeId, ctx.overlayRoot);
        if (line == null)
        {
            if (!ctx.overlayTemplate.TryGetValue(type, out var template) || template == null)
            { report.Add($"[彩带] {edgeId}: 无 {type} 彩带模板，跳过 overlay"); return; }
            GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, ctx.overlayRoot);
            string suffix = template.name.Contains('_') ? template.name.Substring(template.name.IndexOf('_')) : "_Line";
            clone.name = edgeId + suffix;
            line = clone.transform;
            line.gameObject.SetActive(true);
        }
        var filter = line.GetComponent<MeshFilter>();
        if (filter == null)
        { report.Add($"[彩带] {edgeId}: overlay '{line.name}' 无 MeshFilter，跳过"); return; }

        Mesh mesh = CreateRibbonMesh(world, OverlayWidthFor(type), line.name, ctx, roadSurface, OverlayLift, line);
        filter.sharedMesh = SaveMesh(mesh, ctx.meshDir + "/" + Sanitize(ctx.mapKey) + "_" + line.name + ".asset");
        EditorUtility.SetDirty(filter);
    }

    // ============================================================ 几何（采现役地形高度）

    /// <summary>折线控制点 -> 贴现役地形面的世界折线（0.5 网格步细分更密，贴合更顺）。</summary>
    static Vector3[] BuildTerrainFollowingRoute(List<LycheeGridPoint> ctrl, float lift, SceneContext ctx)
    {
        var result = new List<Vector3>();
        for (int i = 0; i < ctrl.Count - 1; i++)
        {
            LycheeGridPoint s = ctrl[i], e = ctrl[i + 1];
            float gd = Vector2.Distance(new Vector2(s.x, s.y), new Vector2(e.x, e.y));
            int steps = Mathf.Max(1, Mathf.CeilToInt(gd / 0.5f));
            for (int step = 0; step < steps; step++)
            {
                float t = step / (float)steps;
                result.Add(GridToWorld(Mathf.Lerp(s.x, e.x, t), Mathf.Lerp(s.y, e.y, t), lift, ctx));
            }
        }
        LycheeGridPoint last = ctrl[ctrl.Count - 1];
        result.Add(GridToWorld(last.x, last.y, lift, ctx));
        return result.ToArray();
    }

    /// <summary>带状网格：左右边各自贴 primary 采样面（采不到回退地形面/高度场）+ lift；顶点写到 target 局部空间，
    /// 不受 target/父级 transform 影响。这是"道路不沉入地表 + 与现役同款贴地"的核心。</summary>
    // 路口收窄：汇聚 >2 条路/河的"忙站"XZ。ribbon 末端靠近忙站时宽度收窄，避免多路在建筑脚下挤成一团。
    static List<Vector2> _busyJunctionXZ = new List<Vector2>();
    static void BuildBusyJunctions(LycheeUiMapConfig config, Dictionary<string, MapStationAnchor> anchorById)
    {
        _busyJunctionXZ = new List<Vector2>();
        if (config == null || config.edges == null || anchorById == null) return;
        var deg = new Dictionary<string, int>();
        foreach (var e in config.edges)
        {
            if (!string.IsNullOrEmpty(e.fromNodeId)) deg[e.fromNodeId] = deg.TryGetValue(e.fromNodeId, out var a) ? a + 1 : 1;
            if (!string.IsNullOrEmpty(e.toNodeId)) deg[e.toNodeId] = deg.TryGetValue(e.toNodeId, out var b) ? b + 1 : 1;
        }
        foreach (var kv in deg)
            if (kv.Value > 2 && anchorById.TryGetValue(kv.Key, out var an) && an != null)
                _busyJunctionXZ.Add(new Vector2(an.transform.position.x, an.transform.position.z));
    }
    static bool NearBusyJunction(Vector3 p, float r)
    {
        if (_busyJunctionXZ == null) return false;
        var xz = new Vector2(p.x, p.z);
        foreach (var b in _busyJunctionXZ) if ((xz - b).sqrMagnitude < r * r) return true;
        return false;
    }

    static Mesh CreateRibbonMesh(Vector3[] center, float width, string name,
        SceneContext ctx, SurfaceSampler primary, float lift, Transform target)
    {
        int n = center.Length;
        var vertices = new Vector3[n * 2];
        var uv = new Vector2[vertices.Length];
        var triangles = new int[(n - 1) * 6];
        float distance = 0f;
        // 路口收窄：端点靠近"忙站(>2条路/河)"则该端宽度收窄到一半，避免多路在建筑脚下挤成一团。
        bool startBusy = n > 1 && NearBusyJunction(center[0], 3.5f);
        bool endBusy = n > 1 && NearBusyJunction(center[n - 1], 3.5f);
        for (int i = 0; i < n; i++)
        {
            Vector3 fwd = i == n - 1 ? center[i] - center[i - 1] : center[i + 1] - center[i];
            fwd.y = 0f;
            Vector3 side = Vector3.Cross(Vector3.up, fwd.sqrMagnitude < 1e-8f ? Vector3.forward : fwd.normalized);
            if (i > 0) distance += Vector3.Distance(center[i - 1], center[i]);
            float w = width;
            if (startBusy || endBusy)
            {
                float along = i / (float)(n - 1);
                float s = 1f;
                if (startBusy) s = Mathf.Min(s, Mathf.Lerp(0.5f, 1f, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(along / 0.22f))));
                if (endBusy) s = Mathf.Min(s, Mathf.Lerp(0.5f, 1f, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((1f - along) / 0.22f))));
                w = width * s;
            }
            w *= WaterCrossingNarrow(ctx, center[i].x, center[i].z);  // 近河/跨河大幅收窄路面
            Vector3 left = center[i] - side * w * 0.5f;
            Vector3 right = center[i] + side * w * 0.5f;
            left.y = ConformY(ctx, primary, left.x, left.z, lift, center[i].y);
            right.y = ConformY(ctx, primary, right.x, right.z, lift, center[i].y);
            if (ctx.endRampOn) { left.y = RampEndY(ctx, left.x, left.z, left.y); right.y = RampEndY(ctx, right.x, right.z, right.y); }
            // 与河重叠处把路面降到河床之下（层级：道路最底 < 河床 < 水面/建筑）。
            left.y = Mathf.Min(left.y, RoadUnderRiverY(ctx, left.x, left.z));
            right.y = Mathf.Min(right.y, RoadUnderRiverY(ctx, right.x, right.z));
            vertices[i * 2] = target.InverseTransformPoint(left);
            vertices[i * 2 + 1] = target.InverseTransformPoint(right);
            uv[i * 2] = new Vector2(0f, distance * 0.42f);
            uv[i * 2 + 1] = new Vector2(1f, distance * 0.42f);
        }
        int ti = 0;
        for (int i = 0; i < n - 1; i++)
        {
            int v = i * 2;
            triangles[ti++] = v; triangles[ti++] = v + 2; triangles[ti++] = v + 1;
            triangles[ti++] = v + 1; triangles[ti++] = v + 2; triangles[ti++] = v + 3;
        }
        var mesh = new Mesh { name = name };
        mesh.vertices = vertices; mesh.uv = uv; mesh.triangles = triangles;
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>(x,z) 处贴地 y：取 primary 面 / 地形面 / 高度场三者**最高**值 + lift。
    /// 取最高很关键：现役河床/驿站洼地处地形网格比高度场低 1m+，若只贴网格，路会扎进洼地"消失"
    /// （实测 E20/E21 在荆襄大驿 S07 端扎进 E13 河床洼地）。取最高让路稳定贴在地表、不潜入洼地/河床。</summary>
    static float ConformY(SceneContext ctx, SurfaceSampler primary, float x, float z, float lift, float fallbackWorldY)
    {
        float best = float.MinValue; bool hit = false;
        if (primary != null && primary.TrySampleTop(x, z, out float py)) { best = Mathf.Max(best, py); hit = true; }
        if (ctx.terrainSurface != null && ctx.terrainSurface.TrySampleTop(x, z, out float ty)) { best = Mathf.Max(best, ty); hit = true; }
        if (ctx.elevation != null) { best = Mathf.Max(best, ctx.elevation.SampleWorldHeight(x, z)); hit = true; }
        return (hit ? best : fallbackWorldY) + lift;
    }

    /// <summary>路头压到建筑底下：在端点建筑足迹内，把贴地 y 平滑降到「驿站地面 - RoadTuckUnder」，
    /// 让建筑（含码头/船）从上方盖住路头——既不留缺口、也不盖在建筑/船上面。取较近一端。</summary>
    static float RampEndY(SceneContext ctx, float x, float z, float conformedY)
    {
        var p = new Vector2(x, z);
        float dA = Vector2.Distance(p, ctx.endA);
        float dB = Vector2.Distance(p, ctx.endB);
        float y = conformedY;
        bool inA = dA < ctx.endRampA, inB = dB < ctx.endRampB;
        if (inA && (!inB || dA <= dB))
            y = Mathf.Lerp(ctx.endAY, conformedY, Mathf.SmoothStep(0f, 1f, dA / ctx.endRampA));
        else if (inB)
            y = Mathf.Lerp(ctx.endBY, conformedY, Mathf.SmoothStep(0f, 1f, dB / ctx.endRampB));
        return y;
    }

    /// <summary>陆路在贴着水中心线处大幅收窄：到最近水路中心线距离 → [近=RoadWaterNarrowFrac, 远=1] 平滑。
    /// 路面/路肩/车辙/路基都用它乘宽度 → 路在跨河/近河处变细、河岸的宽路基也跟着缩（用户：路近河收窄）。</summary>
    static float WaterCrossingNarrow(SceneContext ctx, float x, float z)
    {
        if (ctx.waterTuples == null || ctx.waterTuples.Count == 0) return 1f;
        float best = float.MaxValue;
        foreach (var (edge, w) in ctx.waterTuples)
        {
            if (w == null || w.Length == 0) continue;
            float d = NearestOnPolylineXZ(x, z, w, out _, out _);
            if (d < best) best = d;
        }
        if (best >= RoadWaterNarrowFull) return 1f;
        if (best <= RoadWaterNarrowDist) return RoadWaterNarrowFrac;
        return Mathf.Lerp(RoadWaterNarrowFrac, 1f, Mathf.SmoothStep(RoadWaterNarrowDist, RoadWaterNarrowFull, best));
    }

    /// <summary>路与河重叠处不再下压道路。旧策略把道路压到河床下，会在水驿/河道交汇处形成视觉断路。</summary>
    static float RoadUnderRiverY(SceneContext ctx, float x, float z)
    {
        return float.MaxValue;
    }

    static bool RouteOverlapsWaters(Vector3[] route, List<(MapRouteEdge edge, Vector3[] world)> waters, float threshold)
    {
        if (route == null || route.Length < 2 || waters == null || waters.Count == 0) return false;
        foreach (var (_, water) in waters)
        {
            if (water == null || water.Length < 2) continue;
            foreach (Vector3 p in route)
            {
                float d = NearestOnPolylineXZ(p.x, p.z, water, out float along, out _);
                if (along < RiverEndpointClearFrac || along > 1f - RiverEndpointClearFrac) continue;
                if (NearPolylineEndpoint(p.x, p.z, water, RoadWaterEndpointClearDistance)) continue;
                if (d <= threshold)
                    return true;
            }
        }
        return false;
    }

    static bool NearPolylineEndpoint(float x, float z, Vector3[] pts, float clearDistance)
    {
        if (pts == null || pts.Length == 0 || clearDistance <= 0f) return false;
        var p = new Vector2(x, z);
        var a = new Vector2(pts[0].x, pts[0].z);
        var b = new Vector2(pts[pts.Length - 1].x, pts[pts.Length - 1].z);
        return Vector2.Distance(p, a) <= clearDistance || Vector2.Distance(p, b) <= clearDistance;
    }

    static Vector3 GridToWorld(float x, float y, float lift, SceneContext ctx)
    {
        float wx = GridWorldX(x, ctx.gridW, ctx.tile), wz = GridWorldZ(y, ctx.gridH, ctx.tile);
        return new Vector3(wx, ConformY(ctx, ctx.terrainSurface, wx, wz, lift, 0f), wz);
    }

    static float GridWorldX(float x, int gridW, float tile) => (x - gridW * 0.5f) * tile;
    static float GridWorldZ(float y, int gridH, float tile) => (gridH * 0.5f - y) * tile;

    // ============================================================ 地形面采样器（贴地核心）

    /// <summary>世界空间三角形池 + XZ 包围盒粗筛；TrySampleTop 取 (x,z) 处最高三角面 y（多层取最上层）。
    /// 与 LycheeRibbonRefit 的 RoadSurface 同一套算法，用来把道路/彩带精确贴到"渲染出来的"网格面上。</summary>
    sealed class SurfaceSampler
    {
        readonly List<Vector3> a = new(), b = new(), c = new();
        readonly List<Vector4> box = new(); // xMin,zMin,xMax,zMax
        public int TriangleCount => a.Count;

        public void AddMesh(MeshFilter filter)
        {
            Mesh mesh = filter.sharedMesh;
            if (mesh == null) return;
            Vector3[] v = mesh.vertices;
            int[] tris = mesh.triangles; // 含全部子网格三角形
            Transform t = filter.transform;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Vector3 wa = t.TransformPoint(v[tris[i]]);
                Vector3 wb = t.TransformPoint(v[tris[i + 1]]);
                Vector3 wc = t.TransformPoint(v[tris[i + 2]]);
                a.Add(wa); b.Add(wb); c.Add(wc);
                box.Add(new Vector4(Mathf.Min(wa.x, wb.x, wc.x), Mathf.Min(wa.z, wb.z, wc.z),
                                    Mathf.Max(wa.x, wb.x, wc.x), Mathf.Max(wa.z, wb.z, wc.z)));
            }
        }

        public bool TrySampleTop(float x, float z, out float y)
        {
            y = float.MinValue; bool hit = false;
            for (int i = 0; i < a.Count; i++)
            {
                Vector4 bb = box[i];
                if (x < bb.x || z < bb.y || x > bb.z || z > bb.w) continue;
                Vector3 pa = a[i], pb = b[i], pc = c[i];
                float d = (pb.z - pc.z) * (pa.x - pc.x) + (pc.x - pb.x) * (pa.z - pc.z);
                if (Mathf.Abs(d) < 1e-7f) continue;
                float w1 = ((pb.z - pc.z) * (x - pc.x) + (pc.x - pb.x) * (z - pc.z)) / d;
                float w2 = ((pc.z - pa.z) * (x - pc.x) + (pa.x - pc.x) * (z - pc.z)) / d;
                float w3 = 1f - w1 - w2;
                const float eps = -0.001f;
                if (w1 < eps || w2 < eps || w3 < eps) continue;
                float s = w1 * pa.y + w2 * pb.y + w3 * pc.y;
                if (s > y) { y = s; hit = true; }
            }
            return hit;
        }
    }

    /// <summary>收集场景中所有激活的"地形"网格（名字含 Terrain），建采样器 + 记录 MeshFilter（挖河用）。
    /// 与 StationLift 的地形识别一致。</summary>
    static void BuildTerrainSurface(SceneContext ctx)
    {
        var s = new SurfaceSampler();
        ctx.terrainFilters = new List<MeshFilter>();
        foreach (var mf in UnityEngine.Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
        {
            if (mf.sharedMesh == null || !mf.gameObject.activeInHierarchy) continue;
            // 含 "Terrain" 的主地形，外加 Replay_Extended_Ground（第二张地形/底板，名字不含 Terrain，
            // 之前被漏掉→没被挖没被染砂，平草板停在 -2.20 顶穿挖深的河床露草，是"绿谷底"真凶）。
            if (!mf.gameObject.name.Contains("Terrain") && !mf.gameObject.name.Contains("Extended_Ground")) continue;
            s.AddMesh(mf);
            ctx.terrainFilters.Add(mf);
        }
        ctx.terrainSurface = s;
    }

    // ============================================================ 平面基底重构 第1步：压平基底 Y=0

    /// <summary>把基底地形从"随机起伏"压成平面 Y=0：①克隆每张地形网格、所有顶点世界 Y→0（保留母版烘的
    /// splat 顶点色，外观不变只是变平）、法线重算（全朝上→地表/路面受光，黑路根因消失）；②高度场 heightSamples
    /// 全置 0（运行时小队/行船采样一致）；③站点锚点世界 Y 落到 AnchorLift（与"移动站点"落点公式一致，水驿/码头
    /// 随后由 LiftWaterStationsAboveWater 按挖出的水面再抬）；④重建采样器使 ctx.terrainSurface = 平面。
    /// 调用点在 RestoreFormerRiverbeds 之后、建路/CarveRivers 之前 → 之后 ConformY 取平面、CarveRivers 在平面挖河。
    /// 「四处同 delta 铁律」：地形 mesh / heightSamples 在此统一到 0，路面/pathPoints 随后按平面 ConformY 自然到 0。</summary>
    static void FlattenBaseTerrain(SceneContext ctx, List<string> report)
    {
        if (ctx.terrainFilters == null || ctx.terrainFilters.Count == 0)
        { report.Add("[压平] 场景无地形网格，跳过压平"); return; }
        report.Add("--- 压平基底 Y=0 ---");

        // 捕捉压平前的地形采样器：装饰物落回平面要按"压平前地形高度"下沉（采样器内部存的是建好时的世界三角，
        // 后面换 mesh / 重建采样器都不影响这个引用）。
        SurfaceSampler preFlatten = ctx.terrainSurface;

        // 1. 克隆并压平每张地形网格（per-map 副本，绝不动共享原 mesh；顶点色/uv 保留 → splat 外观不变）。
        foreach (var mf in ctx.terrainFilters)
        {
            if (mf == null || mf.sharedMesh == null) continue;
            Mesh src = mf.sharedMesh;
            Mesh clone = UnityEngine.Object.Instantiate(src);
            clone.name = GeneratedTerrainBaseName(src.name, ctx) + "_Flat_" + Sanitize(ctx.mapKey);
            Vector3[] verts = clone.vertices;
            Color[] cols = clone.colors;       // 母版烘的 splat 顶点色（R土 G岩砂 B变化草 主草=1-R-G-B）
            bool hasCols = cols != null && cols.Length == verts.Length;
            Transform t = mf.transform;
            int flattened = 0;
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 w = t.TransformPoint(verts[i]);   // 本地→世界，压世界 Y（与挖河同一套 transform 语义）
                if (Mathf.Abs(w.y) > 1e-4f) flattened++;
                w.y = 0f;
                verts[i] = t.InverseTransformPoint(w);
            }
            clone.vertices = verts;            // 只改位置；uv 不动
            if (hasCols) clone.colors = cols;  // 显式回写顶点色，杜绝任何丢失（与 CarveRivers 同防御）→ splat 外观不变只是变平
            clone.RecalculateNormals();        // 平面法线全朝上 (0,1,0) → 受光正常，路再也不沉进暗沟变黑
            clone.RecalculateBounds();
            Mesh saved = SaveMesh(clone, ctx.meshDir + "/" + Sanitize(ctx.mapKey) + "_" + clone.name + ".asset");
            mf.sharedMesh = saved;
            var mc = mf.GetComponent<MeshCollider>();
            if (mc != null) mc.sharedMesh = saved;
            EditorUtility.SetDirty(mf);
            report.Add($"[压平] 地形 '{mf.name}' 顶点 {flattened}/{verts.Length} → Y=0");
        }

        // 2. 高度场全置 0（MapElevationData.heightSamples 是绝对世界高度，运行时贴地采样跟着到 0）。
        var elev = ctx.elevation;
        if (elev != null && elev.heightSamples != null)
        {
            float[] hs = elev.heightSamples;
            for (int i = 0; i < hs.Length; i++) hs[i] = 0f;
            elev.heightSamples = hs;
            EditorUtility.SetDirty(elev);
            report.Add($"[压平] 高度场 {hs.Length} 个采样 → 0");
        }

        // 3. 站点落到平面：所有锚点世界 Y = AnchorLift（与"移动站点"落点 SampleWorldHeight(=0)+AnchorLift 一致；
        //    否则母版按随机地形烘的锚点会悬空/入地——本图实测 [-0.74, 6.30]）。XZ 不动；水驿/码头随后再按水面抬。
        //    两段落地：未变站点只在此落一次；坐标变了的站点稍后在站点循环按 SampleWorldHeight(=0)+AnchorLift 重算，
        //    两路殊途同归到同一 Y（无冲突）。
        int grounded = 0;
        foreach (var a in UnityEngine.Object.FindObjectsByType<MapStationAnchor>(FindObjectsSortMode.None))
        {
            if (a == null) continue;
            Vector3 p = a.transform.position;
            if (Mathf.Abs(p.y - AnchorLift) < 1e-4f) continue;
            a.transform.position = new Vector3(p.x, AnchorLift, p.z);
            EditorUtility.SetDirty(a.transform); EditorUtility.SetDirty(a);
            grounded++;
        }
        if (grounded > 0) report.Add($"[压平] 站点落到平面 {grounded} 个 (Y={AnchorLift})");

        // 3.5 装饰物随地形落回平面：母版的草/岩/树是按随机地形高度摆的（实测悬到 +6.2m）；压平后会悬空。
        //     只在明确的装饰容器内逐实例下沉「压平前该处地形高度」→ 继续坐在平面上。绝不碰地形/道路/站点/
        //     水面/相机/天空（它们各在 01/02/03/06/Water 等其它容器，天然不在这三个装饰容器里）。
        int droppedDeco = RegroundDecorations(preFlatten, CollectDecorationContainers());
        if (droppedDeco > 0) report.Add($"[压平] 装饰物随地形落回平面 {droppedDeco} 个实例");

        // 4. 重建采样器：ctx.terrainSurface 现在是平面、ctx.terrainFilters 重新指向已压平的 mesh。
        BuildTerrainSurface(ctx);
        report.Add($"[压平] 重建地形采样器：三角形 {ctx.terrainSurface.TriangleCount} 个 / 地形网格 {ctx.terrainFilters.Count} 个（现为平面 Y=0）");
    }

    /// <summary>收集装饰容器：配置根下的 04_Natural_Decoration（主植生/岩） + 场景根下的
    /// BoundaryTreeline*/BoundaryPalaceBuffer*（边界树/岩/覆盖）。只这些容器内的东西会被随地形落回平面。</summary>
    static List<Transform> CollectDecorationContainers()
    {
        var list = new List<Transform>();
        Transform natural = FindAnyByName("04_Natural_Decoration");
        if (natural != null) list.Add(natural);
        foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (t == null || t.parent != null) continue; // 仅场景根
            if (t.name.StartsWith("BoundaryTreeline", StringComparison.Ordinal) ||
                t.name.StartsWith("BoundaryPalaceBuffer", StringComparison.Ordinal))
                list.Add(t);
        }
        return list;
    }

    /// <summary>把装饰容器内每个网格实例按「压平前该 XZ 处地形高度 oh」下沉（pos.y -= oh）——地形从 oh 降到 0，
    /// 装饰同步降 oh 即继续坐在平面上。只移动直接挂 MeshRenderer 的 GO，且若其祖先也在集合里则跳过（由祖先带着动，
    /// 杜绝父子双重下沉）。同一实例的多个零件 XZ 几乎相同、oh 一致 → 整体一起降不散架。容器外（地形/路/站点/水面/
    /// 天空）一律不碰。</summary>
    static int RegroundDecorations(SurfaceSampler preFlatten, List<Transform> containers)
    {
        if (preFlatten == null || containers == null || containers.Count == 0) return 0;
        var rends = new List<MeshRenderer>();
        foreach (var c in containers)
            if (c != null) rends.AddRange(c.GetComponentsInChildren<MeshRenderer>(true));
        var set = new HashSet<Transform>();
        foreach (var r in rends) if (r != null) set.Add(r.transform);

        int dropped = 0;
        foreach (var r in rends)
        {
            if (r == null) continue;
            Transform t = r.transform;
            bool ancestorInSet = false;
            for (Transform p = t.parent; p != null; p = p.parent)
                if (set.Contains(p)) { ancestorInSet = true; break; }
            if (ancestorInSet) continue; // 由祖先实例带着一起下沉
            Vector3 pos = t.position;
            if (preFlatten.TrySampleTop(pos.x, pos.z, out float oh) && Mathf.Abs(oh) > 1e-3f)
            {
                t.position = new Vector3(pos.x, pos.y - oh, pos.z);
                EditorUtility.SetDirty(t);
                dropped++;
            }
        }
        return dropped;
    }

    // ============================================================ 旧河道回填（水路改陆路/删除水路）

    /// <summary>母版里是水路、目标 config 中不再是水路时，把旧下挖河床抬回两侧岸面附近。
    /// 这样 E13 WATER→BRANCH 这类变化不会留下空河沟；新 WATER 仍由 CarveRivers 重新下挖。</summary>
    static int RestoreFormerRiverbeds(Dictionary<string, MapRouteEdge> edgeById,
        Dictionary<string, LycheeEdgeInfo> newEdgeInfo, SceneContext ctx, List<string> report)
    {
        if (ctx.terrainSurface == null || ctx.terrainFilters == null || ctx.terrainFilters.Count == 0) return 0;

        var formerWaters = new List<(MapRouteEdge edge, Vector3[] world)>();
        foreach (MapRouteEdge edge in edgeById.Values)
        {
            if (edge == null || edge.routeType != MapRouteType.Water) continue;
            bool targetIsWater = newEdgeInfo.TryGetValue(edge.edgeId, out LycheeEdgeInfo info) &&
                                 ParseRouteType(info.routeType) == MapRouteType.Water;
            if (targetIsWater) continue;

            Vector3[] world = ExistingEdgeWorldPath(edge);
            if (world.Length >= 2) formerWaters.Add((edge, world));
        }

        if (formerWaters.Count == 0) return 0;
        report.Add("--- 旧河道回填 ---");

        foreach (var (edge, world) in formerWaters)
        {
            DestroyOverlayLine(edge.edgeId, ctx);
            RestoreFormerRiverTerrain(edge.edgeId, world, ctx, report);
            RestoreFormerRiverElevation(edge.edgeId, world, ctx, report);
        }

        return formerWaters.Count;
    }

    static Vector3[] ExistingEdgeWorldPath(MapRouteEdge edge)
    {
        if (edge?.pathPoints != null && edge.pathPoints.Length >= 2)
            return edge.pathPoints.Where(p => p != null).Select(p => p.position).ToArray();
        return Array.Empty<Vector3>();
    }

    static void RestoreFormerRiverTerrain(string edgeId, Vector3[] world, SceneContext ctx, List<string> report)
    {
        foreach (MeshFilter mf in ctx.terrainFilters)
        {
            if (mf == null || mf.sharedMesh == null) continue;
            Mesh src = mf.sharedMesh;
            Mesh clone = UnityEngine.Object.Instantiate(src);
            clone.name = GeneratedTerrainBaseName(src.name, ctx) + "_RiverRestored_" + edgeId + "_" + Sanitize(ctx.mapKey);
            Vector3[] verts = clone.vertices;
            Transform t = mf.transform;
            int lifted = 0;

            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 w = t.TransformPoint(verts[i]);
                if (TryRestoreFormerRiverY(ctx, world, w.x, w.z, w.y, out float ny) && ny > w.y + 0.0001f)
                {
                    w.y = ny;
                    verts[i] = t.InverseTransformPoint(w);
                    lifted++;
                }
            }

            if (lifted == 0)
            {
                UnityEngine.Object.DestroyImmediate(clone);
                continue;
            }

            clone.vertices = verts;
            clone.RecalculateNormals();
            clone.RecalculateBounds();
            Mesh saved = SaveMesh(clone, ctx.meshDir + "/" + Sanitize(ctx.mapKey) + "_" + clone.name + ".asset");
            ctx.transientRestoredTerrainAssets.Add(AssetDatabase.GetAssetPath(saved));
            mf.sharedMesh = saved;
            MeshCollider mc = mf.GetComponent<MeshCollider>();
            if (mc != null) mc.sharedMesh = saved;
            EditorUtility.SetDirty(mf);
            report.Add($"[回填] {edgeId}: 地形 '{mf.name}' 抬升顶点 {lifted}/{verts.Length}");
        }
    }

    static void RestoreFormerRiverElevation(string edgeId, Vector3[] world, SceneContext ctx, List<string> report)
    {
        MapElevationData elev = ctx.elevation;
        if (elev == null || elev.heightSamples == null) return;

        int lifted = 0;
        int W = elev.gridWidth, H = elev.gridHeight;
        float[] hs = elev.heightSamples;
        for (int z = 0; z <= H; z++)
            for (int x = 0; x <= W; x++)
            {
                int idx = z * (W + 1) + x;
                if (idx < 0 || idx >= hs.Length) continue;
                float wx = elev.worldOrigin.x + x * elev.cellSize;
                float wz = elev.worldOrigin.y + (H - z) * elev.cellSize;
                if (TryRestoreFormerRiverY(ctx, world, wx, wz, hs[idx], out float nh) && nh > hs[idx] + 0.0001f)
                {
                    hs[idx] = nh;
                    lifted++;
                }
            }

        if (lifted <= 0) return;
        elev.heightSamples = hs;
        EditorUtility.SetDirty(elev);
        report.Add($"[回填] {edgeId}: 高度场抬升采样 {lifted}/{hs.Length}");
    }

    static bool TryRestoreFormerRiverY(SceneContext ctx, Vector3[] world, float x, float z, float currentY, out float newY)
    {
        newY = currentY;
        float dist = NearestOnPolylineXZWithDirection(x, z, world, out _, out Vector2 nearest, out Vector2 dir);
        if (dist >= FormerRiverRestoreHalfWidth) return false;

        Vector2 side = new Vector2(-dir.y, dir.x);
        if (side.sqrMagnitude < 1e-8f) side = Vector2.right;
        else side.Normalize();

        bool hitA = ctx.terrainSurface.TrySampleTop(nearest.x + side.x * FormerRiverRestoreSampleOffset,
            nearest.y + side.y * FormerRiverRestoreSampleOffset, out float ay);
        bool hitB = ctx.terrainSurface.TrySampleTop(nearest.x - side.x * FormerRiverRestoreSampleOffset,
            nearest.y - side.y * FormerRiverRestoreSampleOffset, out float by);
        if (!hitA && !hitB) return false;

        float bankY = hitA && hitB ? Mathf.Max(ay, by) : (hitA ? ay : by);
        float targetY = bankY - FormerRiverRestoreSettle;
        if (targetY <= currentY + 0.0001f) return false;

        float flatHalf = WaterWidth * 0.5f + 0.2f;
        float fade = dist <= flatHalf ? 1f :
            1f - Mathf.SmoothStep(0f, 1f, (dist - flatHalf) / Mathf.Max(0.01f, FormerRiverRestoreHalfWidth - flatHalf));
        newY = Mathf.Lerp(currentY, targetY, fade);
        return newY > currentY + 0.0001f;
    }

    static void CleanupTransientRestoredTerrainAssets(SceneContext ctx, List<string> report)
    {
        if (ctx.transientRestoredTerrainAssets.Count == 0) return;

        var inUse = new HashSet<string>();
        foreach (MeshFilter mf in ctx.terrainFilters)
        {
            if (mf == null || mf.sharedMesh == null) continue;
            string p = AssetDatabase.GetAssetPath(mf.sharedMesh);
            if (!string.IsNullOrEmpty(p)) inUse.Add(p);
        }

        int deleted = 0;
        foreach (string path in ctx.transientRestoredTerrainAssets.Distinct())
        {
            if (string.IsNullOrEmpty(path) || inUse.Contains(path)) continue;
            if (AssetDatabase.LoadAssetAtPath<Mesh>(path) == null) continue;
            if (AssetDatabase.DeleteAsset(path)) deleted++;
        }
        if (deleted > 0) report.Add($"[清理] 删除未被场景引用的旧河道回填中间网格 {deleted} 个");
    }

    static string GeneratedTerrainBaseName(string meshName, SceneContext ctx)
    {
        string name = string.IsNullOrEmpty(meshName) ? "Continuous_Terrain" : meshName;
        string prefix = Sanitize(ctx.mapKey) + "_";
        while (name.StartsWith(prefix, StringComparison.Ordinal))
            name = name.Substring(prefix.Length);

        int restored = name.IndexOf("_RiverRestored_", StringComparison.Ordinal);
        if (restored >= 0) name = name.Substring(0, restored);

        int carved = name.IndexOf("_Carved_", StringComparison.Ordinal);
        if (carved >= 0) name = name.Substring(0, carved);

        return string.IsNullOrEmpty(name) ? "Continuous_Terrain" : name;
    }

    // ============================================================ 道路路堤/栈道（把路抬出洼地）

    /// <summary>每条陆路一条记录：世界折线、填洼后的平缓 grade、每点相对原 grade 的抬升量 delta、路堤半宽。
    /// 由 BuildRoadEmbanks 在 CarveRivers 之前填好，供地形抬升 pass(TryRoadEmbankTop) 与
    /// 道路网格抬升 pass(LiftRoadsOntoEmbankments) 共用。</summary>
    static List<(Vector3[] world, float[] grade, float[] delta, float halfW)> _roadEmbanks;

    /// <summary>把一条路抬成"端点连线 grade"：路面不低于两端驿站连线 - RoadEmbankSag，顺天然地形更高处保留。
    /// 这样路穿任何天然谷地都自动抬到连线上、不沉进暗沟——不依赖"恰好有更高邻居"(旧填V洼法对单调下坡失效的真根因)。
    /// 返回 grade 与每点抬升量 delta(≥0)。</summary>
    static (float[] grade, float[] delta) ComputeEmbankGrade(Vector3[] world)
    {
        int n = world.Length;
        var grade = new float[n]; var orig = new float[n]; var delta = new float[n];
        for (int i = 0; i < n; i++) orig[i] = world[i].y;
        var arc = new float[n];
        for (int i = 1; i < n; i++)
        { float dx = world[i].x - world[i - 1].x, dz = world[i].z - world[i - 1].z; arc[i] = arc[i - 1] + Mathf.Sqrt(dx * dx + dz * dz); }
        float total = Mathf.Max(1e-4f, arc[n - 1]);
        float yA = world[0].y, yB = world[n - 1].y;   // 两端=驿站连接点高程
        // 端点连线下限：每点 = max(原地形, 两端连线插值 - 容许下沉)。无封顶（贴连线，不凭空抬高）。
        for (int i = 0; i < n; i++)
            grade[i] = Mathf.Max(orig[i], Mathf.Lerp(yA, yB, arc[i] / total) - RoadEmbankSag);
        // 轻度平滑去折点（端点固定，且永不低于原地形）。
        for (int it = 0; it < 3; it++)
            for (int i = 1; i < n - 1; i++)
                grade[i] = Mathf.Max(orig[i], (grade[i - 1] + grade[i] + grade[i + 1]) / 3f);
        for (int i = 0; i < n; i++) delta[i] = Mathf.Max(0f, grade[i] - orig[i]);
        return (grade, delta);
    }

    /// <summary>在 CarveRivers 之前构建 _roadEmbanks（只收陆路，跳过 Water）。halfW = RoadbedWidthFor(type)*0.5。</summary>
    static void BuildRoadEmbanks(List<(MapRouteEdge edge, Vector3[] world, MapRouteType type)> changed)
    {
        _roadEmbanks = new List<(Vector3[], float[], float[], float)>();
        if (changed == null) return;
        foreach (var (edge, world, type) in changed)
        {
            if (type == MapRouteType.Water || world == null || world.Length < 2) continue;
            var (grade, delta) = ComputeEmbankGrade(world);
            _roadEmbanks.Add((world, grade, delta, RoadbedWidthFor(type) * 0.5f));
        }
    }

    /// <summary>某 XZ 处路堤要把地形抬到的目标顶面（取所有路的最大；近水路则不抬，保河道）。
    /// origY = 该点 t=0(路堤够不到/边缘)时的基准（原地形高度），路下满高、外侧 SmoothStep 渐隐回 origY。
    /// 只在结果高于 origY 时返回 true。</summary>
    static bool TryRoadEmbankTop(float x, float z, float origY,
        List<(MapRouteEdge edge, Vector3[] world)> waters, out float topY)
    {
        topY = origY; bool any = false;
        if (_roadEmbanks == null) return false;
        // 近水路保护：该点离任一水路中心线太近则一律不抬，避免把河道填平。
        if (waters != null)
            foreach (var (_, ww) in waters)
            {
                if (ww == null || ww.Length < 2) continue;
                if (NearestOnPolylineXZ(x, z, ww, out _, out _) < RoadEmbankRiverKeepout) return false;
            }
        foreach (var (world, grade, delta, halfW) in _roadEmbanks)
        {
            float d = NearestOnPolylineXZ(x, z, world, out float along, out _);
            float reach = halfW + RoadEmbankSkirt;
            if (d >= reach) continue;
            int n = world.Length;
            int idx = Mathf.Clamp(Mathf.RoundToInt(along * (n - 1)), 0, n - 1);
            if (delta[idx] <= 0.001f) continue; // 这段没抬，不动地形
            float gradeY = grade[idx];
            float t = d <= halfW ? 1f : 1f - (d - halfW) / RoadEmbankSkirt; // 路下满高，外侧线性降到 0
            t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
            float candidate = Mathf.Lerp(origY, gradeY - RoadEmbankSink, t); // t=0 时不抬（=origY）
            if (candidate > topY) { topY = candidate; any = true; }
        }
        return any && topY > origY;
    }

    /// <summary>CarveRivers 之后：把每条陆路的网格子物体(路面/路基/车辙/border)与 pathPoints 按其
    /// 折线抬升量 delta 抬到路堤上（只抬不降），让路面坐在抬高的实地上、小队走路/跟随同步上来。</summary>
    static void LiftRoadsOntoEmbankments(List<(MapRouteEdge edge, Vector3[] world, MapRouteType type)> changed,
        SceneContext ctx, List<string> report)
    {
        if (changed == null || _roadEmbanks == null) return;
        int liftedEdges = 0;
        foreach (var (edge, world, type) in changed)
        {
            if (type == MapRouteType.Water || edge == null) continue;
            // 找到这条路对应的 embank（按引用相等 world）。
            float[] delta = null; Vector3[] ew = null;
            foreach (var e in _roadEmbanks) if (ReferenceEquals(e.world, world)) { delta = e.delta; ew = e.world; break; }
            if (delta == null || ew == null || ew.Length < 2) continue;
            bool anyLift = false; foreach (var dlt in delta) if (dlt > 0.001f) { anyLift = true; break; }
            if (!anyLift) continue;
            // 局部函数：某世界 XZ 处的抬升量（最近折线点的 delta）。
            System.Func<float, float, float> deltaAt = (x, z) =>
            {
                NearestOnPolylineXZ(x, z, ew, out float along, out _);
                int idx = Mathf.Clamp(Mathf.RoundToInt(along * (ew.Length - 1)), 0, ew.Length - 1);
                return delta[idx];
            };
            // 抬所有有几何的子物体网格（路面/路基/车辙/border），逐顶点按其 XZ 的 delta 抬高。
            foreach (Transform child in edge.gameObject.transform)
            {
                var mf = child.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                if (child.GetComponent<MeshRenderer>() == null) continue; // 跳过无渲染的（如 pathPoint 容器）
                var mesh = mf.sharedMesh;
                var vs = mesh.vertices;
                bool changedV = false;
                for (int i = 0; i < vs.Length; i++)
                {
                    Vector3 wv = child.TransformPoint(vs[i]);
                    float dl = deltaAt(wv.x, wv.z);
                    if (dl > 0.001f) { wv.y += dl; vs[i] = child.InverseTransformPoint(wv); changedV = true; }
                }
                if (changedV)
                {
                    // 与现役道路子网格一致：另存为 per-edge .asset（不改可能被共享的原 asset）。
                    var nm = UnityEngine.Object.Instantiate(mesh);
                    nm.name = mesh.name + "_Emb";
                    nm.vertices = vs; nm.RecalculateNormals(); nm.RecalculateBounds();
                    mf.sharedMesh = SaveMesh(nm, ctx.meshDir + "/" + Sanitize(ctx.mapKey) + "_" + child.name + "_Emb.asset");
                    EditorUtility.SetDirty(mf);
                }
            }
            // 抬 pathPoints（小队走/行船按它贴地跟随）。
            if (edge.pathPoints != null)
                foreach (var pp in edge.pathPoints)
                    if (pp != null)
                    {
                        var p = pp.position; float dl = deltaAt(p.x, p.z);
                        if (dl > 0.001f) { pp.position = new Vector3(p.x, p.y + dl, p.z); EditorUtility.SetDirty(pp); }
                    }
            liftedEdges++;
        }
        report?.Add($"[路堤] 抬升道路 {liftedEdges} 条出洼地");
    }

    // ============================================================ 挖河凹陷（新水路 = 与现役同质量的下沉河道）

    /// <summary>为新增/重建的水路下挖河谷 + 沉水面：①克隆地形网格副本并按到水路中心线的距离下挖顶点；
    /// ②同步下挖 MapElevationData.heightSamples；③把水面/高光/彩带/行船 pathPoints 沉到原地表下 WaterSurfaceDrop。
    /// 原地表高度取自挖河前建好的 ctx.terrainSurface（不受本次下挖影响），保证水面相对原岸的下沉量稳定。</summary>
    /// <summary>第二张底板 Replay_Extended_Ground 与主地形 Continuous_Terrain 共面（同 splat 材质、同 Y），
    /// 播放时相机移动两层抢深度→草上沙/石贴图疯狂闪动（z-fighting）。把底板整体下沉一点，让主地形在重叠区永远在上、消冲突。
    /// 碗形河床仍由主地形表现，底板被主地形盖住（也就不会从河底露草）。重跑幂等（每次从母版0再下沉）。</summary>
    static void SinkExtendedGroundBelowMain(List<string> report)
    {
        const float sink = 0.10f;
        Transform t = FindAnyByName("Replay_Extended_Ground");
        if (t == null) return;
        Vector3 p = t.position;
        t.position = new Vector3(p.x, p.y - sink, p.z);
        if (report != null) report.Add($"[消Z冲突] Replay_Extended_Ground 下沉 {sink:F2} 到主地形之下");
    }

    /// <summary>给河道中心线加 Perlin 蜿蜒：每个中间点沿垂直方向按噪声偏移（端点不动、中间最大），河不再直直的、更自然。
    /// 就地改 world 数组 → 之后的挖河/水面/沙岸/航线点全部跟随同一条蜿蜒线。确定性（按世界坐标采样 Perlin，可复现）。</summary>
    static void MeanderRiverPaths(List<(MapRouteEdge edge, Vector3[] world)> waters)
    {
        foreach (var (edge, world) in waters)
        {
            if (world == null || world.Length < 3) continue;
            Vector3[] orig = (Vector3[])world.Clone();
            int n = world.Length;
            for (int i = 1; i < n - 1; i++)
            {
                Vector3 fwd = orig[i + 1] - orig[i - 1]; fwd.y = 0f;
                if (fwd.sqrMagnitude < 1e-6f) continue;
                fwd.Normalize();
                Vector3 perp = Vector3.Cross(Vector3.up, fwd);
                float window = Mathf.Sin(Mathf.PI * i / (float)(n - 1));   // 端点0、中间1
                float noise = Mathf.PerlinNoise(orig[i].x * RiverMeanderFreq + 13.7f, orig[i].z * RiverMeanderFreq + 7.3f) * 2f - 1f;
                world[i] = orig[i] + perp * (noise * RiverMeanderAmp * window);
            }
        }
    }

    /// <summary>S3 山路抬山脊（镜像 CarveRivers，只抬不降）：沿每条山路中心线把平面基底抬成倒U山脊，山路 ribbon 经
    /// ConformY（取地形/路面/高度场最高+lift）自动贴脊、无需改 ConformY 本体。必须在 FlattenBaseTerrain 之后、建路之前
    /// 调，抬完立即 BuildTerrainSurface 重建采样器。四处同源：地形 mesh 顶点 + heightSamples 一起抬；脊顶染岩(splat G)否则
    /// 露草。端点 RidgeEndFade 收口防顶飞驿站、近水(RidgeWaterKeepout)不抬防填河、站点建筑核内不抬。
    /// passNodes=汇聚≥2 山路的山口(N3,S4 传入)：其半径内脊顶额外叠 RidgePassExtra 成全图制高点。</summary>
    static void RaiseMountains(List<Vector3[]> mountains, List<(Vector2 c, float r, float h)> platforms, SceneContext ctx,
        Dictionary<string, MapStationAnchor> anchorById,
        List<(MapRouteEdge edge, Vector3[] world)> waters,
        List<(Vector2 c, float r)> passNodes, List<string> report)
    {
        if (ctx == null) return;
        bool hasM = mountains != null && mountains.Count > 0;     // 山脊（沿山路）
        bool hasP = platforms != null && platforms.Count > 0;     // 节点抬台（N1 忙站/N3 山口，绝对高度、无建筑核守卫）
        if (!hasM && !hasP) return;
        report.Add("--- 山脊/节点台抬起 ---");
        // 1. 克隆并抬升地形网格（per-map 副本，绝不动共享原 mesh）。
        foreach (var mf in ctx.terrainFilters)
        {
            if (mf == null || mf.sharedMesh == null) continue;
            Mesh src = mf.sharedMesh;
            Mesh clone = UnityEngine.Object.Instantiate(src);
            clone.name = GeneratedTerrainBaseName(src.name, ctx) + "_Ridge_" + Sanitize(ctx.mapKey);
            Vector3[] verts = clone.vertices;
            Color[] cols = clone.colors;
            bool hasCols = cols != null && cols.Length == verts.Length;
            Transform t = mf.transform;
            int raised = 0, rockPainted = 0;
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 w = t.TransformPoint(verts[i]);
                float tgt = w.y;
                if (hasM && TryRaiseRidgeY(ctx, w.x, w.z, w.y, mountains, waters, anchorById, passNodes, out float rny) && rny > tgt) tgt = rny;
                if (hasP && TryRaisePlatformY(platforms, w.x, w.z, w.y, out float pny) && pny > tgt) tgt = pny;
                if (tgt > w.y + 0.0001f)
                {
                    float amt = tgt - w.y;
                    w.y = tgt; verts[i] = t.InverseTransformPoint(w); raised++;
                    if (hasCols)
                    {
                        // 抬得越高越染岩(G/Layer2)：脊顶/台顶最岩、山脚渐隐回草，与挖河染砂同机制（位置不重叠不撞色）
                        float rockW = Mathf.SmoothStep(0f, RidgeCrest * 0.55f, amt);
                        Color o = cols[i];
                        cols[i] = new Color(o.r * (1f - rockW), Mathf.Lerp(o.g, 1f, rockW), o.b * (1f - rockW), o.a);
                        rockPainted++;
                    }
                }
            }
            clone.vertices = verts;
            if (hasCols) clone.colors = cols;
            clone.RecalculateNormals();
            clone.RecalculateBounds();
            Mesh saved = SaveMesh(clone, ctx.meshDir + "/" + Sanitize(ctx.mapKey) + "_" + clone.name + ".asset");
            mf.sharedMesh = saved;
            var mc = mf.GetComponent<MeshCollider>();
            if (mc != null) mc.sharedMesh = saved;
            EditorUtility.SetDirty(mf);
            report.Add($"地形 '{mf.name}' 抬脊顶点 {raised}/{verts.Length}，脊顶染岩 {rockPainted}");
        }
        // 2. 同步抬升 heightSamples（小队走山路贴地要跟着上脊；行序 北→南）。
        var elev = ctx.elevation;
        if (elev != null && elev.heightSamples != null)
        {
            int W = elev.gridWidth, H = elev.gridHeight;
            float[] hs = elev.heightSamples;
            for (int z = 0; z <= H; z++)
                for (int x = 0; x <= W; x++)
                {
                    int idx = z * (W + 1) + x;
                    if (idx < 0 || idx >= hs.Length) continue;
                    float wx = elev.worldOrigin.x + x * elev.cellSize;
                    float wz = elev.worldOrigin.y + (H - z) * elev.cellSize;
                    float th = hs[idx];
                    if (hasM && TryRaiseRidgeY(ctx, wx, wz, hs[idx], mountains, waters, anchorById, passNodes, out float rh) && rh > th) th = rh;
                    if (hasP && TryRaisePlatformY(platforms, wx, wz, hs[idx], out float ph) && ph > th) th = ph;
                    if (th > hs[idx] + 0.0001f) hs[idx] = th;
                }
            elev.heightSamples = hs;
            EditorUtility.SetDirty(elev);
        }
    }

    /// <summary>某点的山脊抬升目标 Y（只抬不降，取各山路最高）。近水/站点核内/路堤外不抬。</summary>
    static bool TryRaiseRidgeY(SceneContext ctx, float x, float z, float origY,
        List<Vector3[]> mountains, List<(MapRouteEdge edge, Vector3[] world)> waters,
        Dictionary<string, MapStationAnchor> anchorById, List<(Vector2 c, float r)> passNodes, out float newY)
    {
        newY = origY; bool any = false;
        // 近水不抬：防把河填平
        if (waters != null)
            foreach (var (e, w) in waters)
                if (w != null && w.Length >= 2 && NearestOnPolylineXZ(x, z, w, out _, out _) < RidgeWaterKeepout) return false;
        // 站点建筑核内不抬：防顶飞建筑
        if (anchorById != null)
            foreach (var kv in anchorById)
            {
                var a = kv.Value; if (a == null) continue;
                float br = BuildingRadius(a);
                float dx = x - a.transform.position.x, dz = z - a.transform.position.z;
                if (dx * dx + dz * dz < br * br) return false;
            }
        foreach (var m in mountains)
        {
            if (m == null || m.Length < 2) continue;
            float d = NearestOnPolylineXZ(x, z, m, out float along, out Vector2 nearest);
            if (d >= RidgeHalfWidth) continue;
            float baseY = ctx.terrainSurface != null && ctx.terrainSurface.TrySampleTop(nearest.x, nearest.y, out float cy) ? cy : origY;
            float endFade = RidgeEndFade(along);
            float s = d / RidgeHalfWidth;
            // 平顶山脊：内 RidgeFlatFrac 比例满高(平顶，路坐其上不颠)、外侧 SmoothStep 渐降回平地
            float shape = s < RidgeFlatFrac ? 1f : Mathf.SmoothStep(1f, 0f, (s - RidgeFlatFrac) / (1f - RidgeFlatFrac));
            float crest = RidgeCrest;
            if (passNodes != null)                      // N3 山口：节点半径内额外叠高 → 全图制高点
                foreach (var (pc, pr) in passNodes)
                {
                    float pd = (new Vector2(x, z) - pc).magnitude;
                    if (pd < pr) { crest += RidgePassExtra * Mathf.SmoothStep(1f, 0f, pd / Mathf.Max(0.01f, pr)); break; }
                }
            float raised = baseY + crest * endFade * shape;
            if (raised > newY) { newY = raised; any = true; }
        }
        return any;
    }

    static float RidgeEndFade(float along)
    {
        float a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(along / Mathf.Max(0.001f, RidgeEndTaperFrac)));
        float b = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((1f - along) / Mathf.Max(0.001f, RidgeEndTaperFrac)));
        return a * b;
    }

    /// <summary>节点抬台目标 Y（S4 N1 忙站/N3 山口）：绝对高度 h 的平顶圆台（内 40% 平、外渐降），取各台最高。
    /// 无建筑核守卫（要抬起建筑脚下）；与山脊 max 合成 → 山口在脊上再凸一块成全图制高点。</summary>
    static bool TryRaisePlatformY(List<(Vector2 c, float r, float h)> platforms, float x, float z, float origY, out float newY)
    {
        newY = origY; bool any = false;
        if (platforms == null) return false;
        var p = new Vector2(x, z);
        foreach (var (c, r, h) in platforms)
        {
            float d = (p - c).magnitude;
            if (d >= r) continue;
            float s = d / r;
            float shape = s < 0.4f ? 1f : Mathf.SmoothStep(1f, 0f, (s - 0.4f) / 0.6f); // 平顶 + 边缘宽缓渐降
            float raised = h * shape; // 绝对高度（节点在平地 origY≈0；与脊取 max 自然融合）
            if (raised > newY) { newY = raised; any = true; }
        }
        return any;
    }

    /// <summary>S4 节点分级：遍历 config.edges 一次，每节点产「度 + 各路型计数」，判 N0~N6。一处算、各处复用。
    /// N1=总度&gt;2(忙站) / N2=Water≥2(汇水) / N3=Mountain≥2(山口) / N4=邻接Water(临水) / N5=OfficialRoad≥3(官道广场) /
    /// N6=同含 Mountain+OfficialRoad(山路↔官道过渡)。一个节点可同时属多级（取各自处理）。不依赖具体 edgeId/nodeId、配置驱动。</summary>
    static Dictionary<string, NodeClass> ClassifyNodes(LycheeUiMapConfig config)
    {
        var map = new Dictionary<string, NodeClass>();
        if (config == null || config.nodes == null) return map;
        foreach (var node in config.nodes)
            if (node != null && !string.IsNullOrEmpty(node.nodeId)) map[node.nodeId] = new NodeClass();
        if (config.edges != null)
            foreach (var e in config.edges)
            {
                if (e == null) continue;
                var t = ParseRouteType(e.routeType);
                for (int k = 0; k < 2; k++)
                {
                    string nid = k == 0 ? e.fromNodeId : e.toNodeId;
                    if (string.IsNullOrEmpty(nid) || !map.TryGetValue(nid, out var nc)) continue;
                    nc.degree++;
                    if (t == MapRouteType.OfficialRoad) nc.official++;
                    else if (t == MapRouteType.Water) nc.water++;
                    else if (t == MapRouteType.Mountain) nc.mountain++;
                    else if (t == MapRouteType.Branch) nc.branch++;
                }
            }
        foreach (var nc in map.Values)
        {
            nc.n1 = nc.degree > 2;
            nc.n2 = nc.water >= 2;
            nc.n3 = nc.mountain >= 2;
            nc.n4 = nc.water >= 1;
            nc.n5 = nc.official >= 3;
            nc.n6 = nc.mountain >= 1 && nc.official >= 1;
        }
        return map;
    }

    class NodeClass { public int degree, official, water, mountain, branch; public bool n1, n2, n3, n4, n5, n6; }

    // 确定性哈希（FNV-1a，mapKey+两个整数）→ 散布/抬升一律可复现、跨图稳定、禁无种子 Random。
    static uint StableHash(string s, int a, int b)
    {
        uint h = 2166136261u;
        if (s != null) foreach (char c in s) { h ^= c; h *= 16777619u; }
        unchecked { h ^= (uint)a; h *= 16777619u; h ^= (uint)b; h *= 16777619u; }
        return h;
    }
    static float Hash01(string key, int a, int b) => (StableHash(key, a, b) & 0xFFFFFF) / (float)0x1000000;

    static bool NearAnyPath(float x, float z, List<Vector3[]> paths, float r)
    {
        if (paths == null) return false;
        foreach (var p in paths)
            if (p != null && p.Length >= 2 && NearestOnPolylineXZ(x, z, p, out _, out _) < r) return true;
        return false;
    }

    /// <summary>用户 2026-06-25 手调并验收的背景山布局：13 座 mountain_terrain_02，全靠地图边缘、对各图中央玩法区都安全。
    /// 每行 = x, y, z, scale, rotY。ScatterBackgroundScenery 直接按此摆（替代程序随机），并对会压路的做离心外推兜底。</summary>
    static readonly float[,] BgMountainLayout = {
        {19.5f,-1.00f,29.1f,0.724f,71f},  {112.0f,-1.00f,24.9f,2.013f,75f}, {-44.3f,-1.00f,4.2f,0.737f,78f},
        {-0.8f,-1.00f,18.8f,2.031f,82f},  {-37.1f,-1.00f,10.0f,0.751f,85f}, {-56.2f,-1.20f,-12.7f,1.246f,89f},
        {-13.7f,-1.00f,-61.7f,0.764f,92f},{71.6f,-1.00f,22.7f,2.068f,96f},  {-42.7f,-1.00f,-64.9f,0.778f,99f},
        {-50.9f,-0.70f,-60.8f,1.561f,103f},{28.7f,-1.00f,-78.4f,1.355f,110f},{88.4f,-0.40f,-83.8f,2.115f,113f},
        {43.9f,-1.00f,-61.2f,0.811f,117f},
    };

    /// <summary>S5 背景山川：按用户手调烧死的 BgMountainLayout 摆背景山脉(mountain_terrain_02) + 山旁加树。
    /// 会压路的山沿离心方向外推兜底（绝不挡路）；幂等(先删上轮容器再布)；复用已有 prefab、零自造。
    /// 容器名避开既有清理词(Exclude/Grass/BankTrim/ShoreTrim/RiverBed/EntranceBlend)。</summary>
    static void ScatterBackgroundScenery(SceneContext ctx, Dictionary<string, MapStationAnchor> anchorById, List<string> report)
    {
        if (ctx == null) return;
        const string rootName = "04_Background_Scenery";
        for (var ex = GameObject.Find(rootName); ex != null; ex = GameObject.Find(rootName)) UnityEngine.Object.DestroyImmediate(ex);
        var mtn = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Hill Rock Mountain Terrain/Prefab/mountain_terrain_02.prefab");
        if (mtn == null) { report.Add("[背景山川] 缺 mountain_terrain_02，跳过"); return; }
        var mtnB = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/FreeMountain/Prefabs/Free_Mountain.prefab"); // 第二种：宽缓大山(488m/枢轴中心)，当小丘混入增加变化
        var bgFree = new List<GameObject>(); // 第三类：BackgroundMountainFree 矮丘(10×2.4、底浮0.5)，混进部分坐标增加变化(用户导入)
        foreach (var pp in new[]{"ExtremeLowPolyMountain","LowPolyMountain","MediumPolyMountain"})
        { var g = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/BackgroundMountainFree/Prefabs/{pp}.prefab"); if (g != null) bgFree.Add(g); }
        var rocks = new List<GameObject>();
        for (int k = 1; k <= 4; k++) { var r = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Hill Rock Mountain Terrain/Prefab/rock_set_0{k}.prefab"); if (r != null) rocks.Add(r); }
        var trees = new List<GameObject>();
        for (int k = 1; k <= 3; k++) { var tp = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Tree_Packs/URP_Tree_Pack/Prefabs/URP_Tree_{k}.prefab"); if (tp != null) trees.Add(tp); }
        Transform root = new GameObject(rootName).transform;

        // 玩法区包围盒（节点）+ 路/河中心线（keep-out）
        float minx = 9999, maxx = -9999, minz = 9999, maxz = -9999;
        if (anchorById != null)
            foreach (var kv in anchorById)
            { var a = kv.Value; if (a == null) continue; var p = a.transform.position; minx = Mathf.Min(minx, p.x); maxx = Mathf.Max(maxx, p.x); minz = Mathf.Min(minz, p.z); maxz = Mathf.Max(maxz, p.z); }
        if (minx > maxx) { minx = -35; maxx = 38; minz = -22; maxz = 12; }
        Vector2 c = new Vector2((minx + maxx) * 0.5f, (minz + maxz) * 0.5f);
        float hx = (maxx - minx) * 0.5f, hz = (maxz - minz) * 0.5f;
        var paths = new List<Vector3[]>();
        foreach (var e in UnityEngine.Object.FindObjectsByType<MapRouteEdge>(FindObjectsSortMode.None))
            if (e != null) { var w = ExistingEdgeWorldPath(e); if (w != null) paths.Add(w); }
        // 大地图(背景地形)范围 → 山脉环推到它的边界（用户红线、远离玩法路网，不再压路）
        float mapHalfX = 105f, mapHalfZ = 85f; Vector2 mapC = c;
        foreach (var mr in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            if (mr != null && mr.name.Contains("Extended_Ground"))
            { var b = mr.bounds; mapHalfX = b.extents.x; mapHalfZ = b.extents.z; mapC = new Vector2(b.center.x, b.center.z); break; }

        string mk = ctx.mapKey ?? "map";
        // 用户 2026-06-25 手调并验收的背景山布局（13 座、靠边、对各图玩法区都安全）→ 烧死成固定布局，替代程序随机摆放。
        // 兜底：万一某座会压到路/河，沿「离地图中心」方向把它往外推到不挡路（保住方位、绝不挡路）；推不开才跳过。山旁加树(避开路)。
        int placedM = 0, treeN = 0, nudged = 0;
        for (int r = 0; r < BgMountainLayout.GetLength(0); r++)
        {
            float x = BgMountainLayout[r, 0], y = BgMountainLayout[r, 1], z = BgMountainLayout[r, 2];
            float sc = BgMountainLayout[r, 3], rot = BgMountainLayout[r, 4];
            float rad = 15f * sc;                                  // mountain_terrain_02 半宽 ≈15×scale(树环用)
            float radSafe = rad + 6f;                              // 避让半径：山体实际略超 15×scale + 肉眼余量 → 保证离路看得出空当
            // 兜底外推：整座山(radSafe)+keep-out 都得离路；不够就沿离心方向每次外推 4m，最多 24 步
            Vector2 outDir = (new Vector2(x, z) - mapC); outDir = outDir.sqrMagnitude < 1e-4f ? Vector2.right : outDir.normalized;
            int step = 0;
            while (NearAnyPath(x, z, paths, radSafe + BackgroundKeepout) && step < 24)
            { x += outDir.x * 4f; z += outDir.y * 4f; step++; }
            x = Mathf.Clamp(x, -mapHalfX * 0.99f, mapHalfX * 0.99f);
            z = Mathf.Clamp(z, -mapHalfZ * 0.99f, mapHalfZ * 0.99f);
            if (NearAnyPath(x, z, paths, radSafe)) { report.Add($"[背景山] 推不开、跳过(会压路) @({x:F0},{z:F0})"); continue; }
            if (step > 0) { nudged++; }
            // 部分坐标换成新矮丘(BackgroundMountainFree)增加变化：只在小山位(sc<1.2)、约每4座一座
            bool useFree = (r % 4 == 2) && sc < 1.2f && bgFree.Count > 0;
            GameObject pf; float psc, py;
            if (useFree) { pf = bgFree[r % bgFree.Count]; psc = sc * 3f; py = y - 0.5f * psc; } // 10m→×3 归一；底浮0.5×scale 下移落地
            else { pf = mtn; psc = sc; py = y; }
            var m = (GameObject)PrefabUtility.InstantiatePrefab(pf);
            m.transform.SetParent(root, true);
            m.transform.position = new Vector3(x, py, z);
            m.transform.rotation = Quaternion.Euler(0f, rot, 0f);
            m.transform.localScale = Vector3.one * psc;
            placedM++;
            // 山旁加树：山前方半区「真·散点」——随机角 + 随机远近(从山脚一直撒到旷野) + 最小间距剔除，
            // 彻底避免"排排坐/挤成篱笆"；不在山后、不挡路(用户两次反馈树太规整)
            Vector2 toCenter = mapC - new Vector2(x, z);
            float frontA = toCenter.sqrMagnitude < 1e-4f ? 0f : Mathf.Atan2(toCenter.x, toCenter.y); // 朝地图中心的方向(Unity Y旋转式)
            int want = 3 + (int)(Hash01(mk, r, 50) * 4f);          // 目标 3-6 棵
            int tries = 0;
            var treesPlacedHere = new List<Vector2>(want);          // 本座已放树位(最小间距判定用)
            while (treesPlacedHere.Count < want && tries < 30 && trees.Count > 0)
            {
                int h = r * 37 + tries; tries++;
                float ta = frontA + (Hash01(mk, h, 51) - 0.5f) * 3.0f;              // 前方 ±~86° 随机角
                float td = rad * (0.9f + Hash01(mk, h, 52) * 1.7f);                 // 0.9~2.6×半径：拉开远近、不贴同一条base弧线
                float tx = x + Mathf.Sin(ta) * td, tz = z + Mathf.Cos(ta) * td;
                if (NearAnyPath(tx, tz, paths, BackgroundKeepout + 1f)) continue;   // 树也不挡路
                bool tooClose = false;                                              // 与本座已放的树保持 ≥7m，防排成行/挤成篱笆
                var here = new Vector2(tx, tz);
                foreach (var pt in treesPlacedHere) if ((pt - here).sqrMagnitude < 49f) { tooClose = true; break; }
                if (tooClose) continue;
                treesPlacedHere.Add(here);
                var pf2 = trees[(int)(Hash01(mk, h, 53) * trees.Count) % trees.Count];
                float tyv = ctx.terrainSurface != null && ctx.terrainSurface.TrySampleTop(tx, tz, out float tts) ? tts : 0f;
                var ti = (GameObject)PrefabUtility.InstantiatePrefab(pf2);
                ti.transform.SetParent(root, true);
                ti.transform.position = new Vector3(tx, tyv, tz);
                ti.transform.rotation = Quaternion.Euler(0f, Hash01(mk, h, 54) * 360f, 0f);
                ti.transform.localScale = Vector3.one * (0.7f + Hash01(mk, h, 55) * 1.1f);  // 0.7~1.8 大小差异更大
                treeN++;
            }
            treesPlacedHere.Clear();
        }
        report.Add($"[背景山·固定布局] 摆 {placedM} 座(用户手调烧死、外推兜底 {nudged} 座) + 山旁树 {treeN}（绝不挡路、幂等 {rootName}）");
    }

    static void CarveRivers(List<(MapRouteEdge edge, Vector3[] world)> waters, SceneContext ctx,
        Dictionary<string, MapStationAnchor> anchorById, List<string> report)
    {
        if (waters == null || waters.Count == 0) return;
        MeanderRiverPaths(waters);  // 河道中心线加蜿蜒，避免直直的、更自然（端点不动；carve/水面/沙岸/航线点都跟随）
        report.Add("--- 挖河凹陷 ---");
        BuildRiverBaseY(waters, ctx, anchorById);  // 每条水路用两端较低岸高作恒定基准 + 站点干核 → 两河一致、水坐进槽、水驿落地
        BuildRoadCauseways(anchorById);            // 陆路堤：连到水驿的陆路路下保留实地、不挖 → 路避开水、不悬空
        // 全局禁用遗留河岸修边容器 River_ShoreTrim_*（挂在配置根下、不在 edge 子级，CleanLegacyWaterChildren 扫不到）→ 去橄榄绿河岸线
        foreach (var _tr in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            if (_tr != null && (_tr.name.StartsWith("River_ShoreTrim") || _tr.name.StartsWith("Water_Junction_Patches") || _tr.name.StartsWith("Route_Line_Overlay") || _tr.name.EndsWith("_BankTrim") || _tr.name.EndsWith("_BankSlope") || _tr.name.EndsWith("_RiverBed") || _tr.name.Contains("EntranceBlend")))
                _tr.gameObject.SetActive(false);

        // 1. 克隆并下挖地形网格（per-map 副本，绝不动共享原 mesh；含河床子网格一并下挖）。
        foreach (var mf in ctx.terrainFilters)
        {
            if (mf == null || mf.sharedMesh == null) continue;
            Mesh src = mf.sharedMesh;
            Mesh clone = UnityEngine.Object.Instantiate(src);
            clone.name = GeneratedTerrainBaseName(src.name, ctx) + "_Carved_" + Sanitize(ctx.mapKey);
            Vector3[] verts = clone.vertices;
            Color[] cols = clone.colors;                       // 母版已烘 splat 顶点色（R土 G岩砂 B变化草 主草=1-R-G-B）
            bool hasCols = cols != null && cols.Length == verts.Length;
            Transform t = mf.transform;
            int carved = 0, sandPainted = 0;
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 w = t.TransformPoint(verts[i]);
                bool wasCarved = TryCarveTargetY(ctx, w.x, w.z, w.y, waters, out float ny) && ny < w.y - 0.0001f;
                if (wasCarved) { w.y = ny; verts[i] = t.InverseTransformPoint(w); carved++; }
                // 路线A核心：挖河同步重刷 splat 顶点色，把河道走廊染成砂(Layer2/G=岩砂层)。
                // 绑定真实几何：挖到的顶点=一定在河道内→强制全砂(根治挖壁上半截露草，不再靠猜半径)；
                // 未挖到的按到中心线距离羽化到岸→砂渐隐入草。terrain 自身像素级"水→砂→草"过渡，不靠硬边贴片。
                if (hasCols)
                {
                    float sandW;
                    if (wasCarved)
                    {
                        // 染砂对齐到渲染水面 WaterRenderHeight（shader 真正显示的水位），不再用网格深水位 → 各段沙带一致、不再硬边
                        sandW = 1f - Mathf.SmoothStep(WaterRenderHeight - 0.05f, WaterRenderHeight + 0.35f, w.y); // 水线起一条较宽沙滩、往上柔和渐隐为草
                    }
                    else sandW = RiverSandSplatWeight(w.x, w.z, waters);
                    sandW *= StationSandDamp(w.x, w.z);   // 站点干核内收沙：中央硬岸、只在水线一圈留窄沙（不再大沙坑）
                    if (sandW > 0.001f)
                    {
                        Color o = cols[i];
                        cols[i] = new Color(o.r * (1f - sandW), Mathf.Lerp(o.g, 1f, sandW), o.b * (1f - sandW), o.a);
                        sandPainted++;
                    }
                }
                // 路堤：把道路走廊下的地形抬到路面之下一点（只抬不降，近水路不抬以保河道）。
                // 用当前 w.y（可能已被 carve 改过）作基准取 max；不给路堤顶点染砂（保持原草/土色）。
                if (TryRoadEmbankTop(w.x, w.z, w.y, waters, out float embTop) && embTop > w.y)
                { w.y = embTop; verts[i] = t.InverseTransformPoint(w); }
            }
            clone.vertices = verts;
            if (hasCols) clone.colors = cols;
            clone.RecalculateNormals();
            clone.RecalculateBounds();
            Mesh saved = SaveMesh(clone, ctx.meshDir + "/" + Sanitize(ctx.mapKey) + "_" + clone.name + ".asset");
            mf.sharedMesh = saved;
            var mc = mf.GetComponent<MeshCollider>();
            if (mc != null) mc.sharedMesh = saved;
            EditorUtility.SetDirty(mf);
            report.Add($"地形 '{mf.name}' 下挖顶点 {carved}/{verts.Length}，河道染砂 {sandPainted}");
        }

        // 2. 同步下挖 MapElevationData.heightSamples（运行时高度采样一致；行序 北→南）。
        var elev = ctx.elevation;
        if (elev != null && elev.heightSamples != null)
        {
            int W = elev.gridWidth, H = elev.gridHeight;
            float[] hs = elev.heightSamples;
            for (int z = 0; z <= H; z++)
                for (int x = 0; x <= W; x++)
                {
                    int idx = z * (W + 1) + x;
                    if (idx < 0 || idx >= hs.Length) continue;
                    float wx = elev.worldOrigin.x + x * elev.cellSize;
                    float wz = elev.worldOrigin.y + (H - z) * elev.cellSize;
                    if (TryCarveTargetY(ctx, wx, wz, hs[idx], waters, out float nh) && nh < hs[idx] - 0.0001f) hs[idx] = nh;
                    // 路堤：同步抬升高度场（小队走路贴地要跟着路堤上来；近水路不抬以保河道）。
                    if (TryRoadEmbankTop(wx, wz, hs[idx], waters, out float et) && et > hs[idx]) hs[idx] = et;
                }
            elev.heightSamples = hs;
            EditorUtility.SetDirty(elev);
        }

        // 3. 每条水路：先铺河床沙石、再沉水面/高光/彩带/行船点。
        Material sandMat = FindSandMaterial(ctx);
        foreach (var (edge, world) in waters)
        {
            if (edge == null) continue;
            CleanLegacyWaterChildren(edge.gameObject, edge.edgeId);
            // 河床归并到地形层：独立河床 mesh 与地形 splat 砂层重复（地形顶点0.4m够密、splat足够细），
            // 删之去"多层凸起/乱"。河床 = 地形挖出的 U 形槽 + carve 时染的 splat 砂。BuildRiverBed 停用。
            // BuildRiverBed(edge, world, ctx, waters, sandMat, anchorById, report);

            Vector3[] waterVisual = TrimWaterSurfaceToEndpointClearance(edge, world, anchorById);
            bool waterTrimmed = waterVisual != world && waterVisual != null && world != null && waterVisual.Length != world.Length;
            if (waterTrimmed)
                report.Add($"[水面避让] {edge.edgeId}: 水驿端主水面裁出建筑足迹，保留河床连续并由交汇水舌接驳");

            // 水面铺满全河（用完整 world 折线、不裁端点）→ 水面铺到驿站、不再短一截（用户要求）。
            ReplaceCarvedWater(edge.gameObject, edge.edgeId + "_Route", world, WaterDrapeWidth, WaterDrapeLift,
                ctx, waters, 0f, 1f);
            // 河面改用会动的 WaterShader（替换静态 Unlit 的 M_RiverWater_Fuller）→ 流动水感。
            {
                Transform _routeT = FindChild(edge.gameObject.transform, edge.edgeId + "_Route");
                var _routeMR = _routeT != null ? _routeT.GetComponent<MeshRenderer>() : null;
                Material _flowMat = FindWaterMaterial();
                if (_routeMR != null && _flowMat != null) { _routeMR.sharedMaterial = _flowMat; EditorUtility.SetDirty(_routeMR); }
                // ★ 真正控制水面高度的地方：设水路物体 transform.Y（WaterShader 按它渲染水面、不看网格顶点Y）。
                if (_routeT != null) { var _wp = _routeT.position; _routeT.position = new Vector3(_wp.x, WaterRenderHeight, _wp.z); EditorUtility.SetDirty(_routeT); }
            }
            // S1 河道压一层：去掉浮在主面上方 +0.045 的 _WaterHighlight 独立薄层（旧子物体由 CleanLegacyWaterChildren 删）。
            // ReplaceCarvedWater(edge.gameObject, edge.edgeId + "_WaterHighlight", waterVisual, WaterDrapeWidth * 0.22f,
            //     WaterDrapeLift + 0.045f, ctx, waters, WaterSurfaceEndTaperFrac, 0.62f);

            // 彩带蓝线随水面铺（受去彩带总开关控制）。
            if (EnableOverlayRibbons)
            {
                Transform line = FindOverlayLine(edge.edgeId, ctx.overlayRoot);
                if (line != null && line.GetComponent<MeshFilter>() != null)
                {
                    var lf = line.GetComponent<MeshFilter>();
                    Mesh lm = CreateWaterSurfaceGrid(waterVisual, OverlayWidthFor(MapRouteType.Water), 4, line.name, line, ctx, waters,
                        WaterDrapeLift + 0.05f, WaterSurfaceEndTaperFrac, 0.62f);
                    lf.sharedMesh = SaveMesh(lm, ctx.meshDir + "/" + Sanitize(ctx.mapKey) + "_" + line.name + ".asset");
                    EditorUtility.SetDirty(lf);
                }
            }

            // 行船点贴中心线水面（车队/小队过河时坐船贴水）。
            if (edge.pathPoints != null)
                for (int i = 0; i < edge.pathPoints.Length && i < world.Length; i++)
                    if (edge.pathPoints[i] != null)
                    {
                        float along = world.Length <= 1 ? 0f : i / (float)(world.Length - 1);
                        edge.pathPoints[i].position = new Vector3(world[i].x,
                            WaterRenderHeight, world[i].z);  // 船贴真实渲染水面（transform.Y 驱动，与水面同高）
                    }
            EditorUtility.SetDirty(edge);
            report.Add($"[挖河] {edge.edgeId} 下挖河谷 {world.Length} 点（谷深 {CarveDepth:F2}/谷半宽 {CarveHalfWidth:F1}/水宽 {WaterDrapeWidth:F1}/沙床 {RiverBedWidth:F1}）");
        }
    }

    /// <summary>把用户手调并验收的水驿/码头各部件 localY 烘回去（重生成会重克隆母版、冲掉手调值）。按站型设
    /// Imported_Station_Model / WaterStation_Split / Dock_AboveWater / Boat_OnWater / House_OnLand 的局部 Y。
    /// 取代 LiftWaterStationsAboveWater（手调值已含正确高度，锚点保持基准、不再额外抬）。值见记忆 waterstation-dock-part-heights。</summary>
    static void ApplyWaterStationPartHeights(Dictionary<string, MapStationAnchor> anchorById, List<string> report)
    {
        if (anchorById == null) return;
        int n = 0;
        foreach (var kv in anchorById)
        {
            var a = kv.Value;
            if (a == null) continue;
            float imp, split, dock, boat, house;
            if (a.stationType == MapStationType.WaterStation) { imp = 0.004f; split = 0.743f; dock = 0.300f; boat = 0.140f; house = 0.420f; }
            else if (a.stationType == MapStationType.Dock)     { imp = 0.041f; split = 0.430f; dock = 0.280f; boat = 0.220f; house = 0.180f; }
            else continue;
            SetPartLocalY(a, "Imported_Station_Model", imp);
            SetPartLocalY(a, "WaterStation_Split", split);
            SetPartLocalY(a, "Dock_AboveWater", dock);
            SetPartLocalY(a, "Boat_OnWater", boat);
            SetPartLocalY(a, "House_OnLand", house);
            n++;
        }
        if (n > 0) report.Add($"[水驿部件高度] 按站型烘回 {n} 个站点的部件 localY（用户手调值）");
    }

    /// <summary>船坐进河道、船身顺水流停靠（用户手调验收的通用计算）：对每个水驿/码头的 Boat_OnWater——
    /// ① 朝向：把船旋到「连到本站的水路切向」上（长轴对齐河向）；② 位置：把船心移到「锚点 + 沿河 BoatAlongRiverOffset + 垂河 BoatAcrossRiverOffset」。
    /// 关键坑：船网格是「世界坐标烘」的(顶点在站点处、transform 在世界原点)——直接改 localEulerAngles/localPosition 会绕世界原点甩飞，
    /// 必须 RotateAround(船心,up,Δ) 原地转、再用世界位移把船心移到目标。船静止长轴用固定 BoatModelRestHeading(同母版恒定，
    /// 别按包围盒长边自动判——近正方形会判反，曾让 S05 偏 90°)。Y 不动(由 ApplyWaterStationPartHeights 烘的 localY 决定)。</summary>
    static void OrientStationBoatsAlongRiver(Dictionary<string, MapStationAnchor> anchorById, List<string> report)
    {
        if (anchorById == null) return;
        var waterEdges = new List<MapRouteEdge>();
        foreach (var e in UnityEngine.Object.FindObjectsByType<MapRouteEdge>(FindObjectsSortMode.None))
            if (e != null && e.routeType == MapRouteType.Water) waterEdges.Add(e);
        int n = 0;
        foreach (var kv in anchorById)
        {
            var a = kv.Value;
            if (a == null) continue;
            if (a.stationType != MapStationType.WaterStation && a.stationType != MapStationType.Dock) continue;
            MapRouteEdge we = null;
            foreach (var e in waterEdges)
                if (e.fromStationId == kv.Key || e.toStationId == kv.Key) { we = e; break; }
            if (we == null) continue;
            Vector3[] w = ExistingEdgeWorldPath(we);
            if (w == null || w.Length < 2) continue;
            Vector3 ap = a.transform.position;
            NearestOnPolylineXZWithDirection(ap.x, ap.z, w, out _, out _, out Vector2 dir);
            if (dir.sqrMagnitude < 1e-6f) continue;
            Vector2 D = dir.normalized;                 // 河向（沿河单位向量, x=worldX,y=worldZ）
            Vector2 N = new Vector2(-D.y, D.x);          // 垂河单位向量（与诊断同一手性）
            float riverAngle = Mathf.Atan2(D.x, D.y) * Mathf.Rad2Deg; // Unity Y旋转: 0=+Z,90=+X
            Transform bt = null;
            foreach (var t in a.GetComponentsInChildren<Transform>(true))
                if (t != null && t.name.Contains("Boat_OnWater")) { bt = t; break; }
            if (bt == null) continue;
            bool has = false; Bounds bb = new Bounds();
            foreach (var r in bt.GetComponentsInChildren<MeshRenderer>())
            { if (!has) { bb = r.bounds; has = true; } else bb.Encapsulate(r.bounds); }
            if (!has) continue;
            // ① 朝向：固定静止长轴 → 河向，收到 [-90,90]（顺河 180°对称、取最小转角）
            float delta = Mathf.DeltaAngle(BoatModelRestHeading, riverAngle);
            while (delta > 90f) delta -= 180f;
            while (delta < -90f) delta += 180f;
            delta += BoatRiverAngleOffset;
            bt.RotateAround(bb.center, Vector3.up, delta); // 绕船心原地转
            // ② 位置：船心 → 锚点 + 沿河*along + 垂河*across（仅 XZ，Y 保持烘值）
            bool h2 = false; Bounds bb2 = new Bounds();
            foreach (var r in bt.GetComponentsInChildren<MeshRenderer>())
            { if (!h2) { bb2 = r.bounds; h2 = true; } else bb2.Encapsulate(r.bounds); }
            Vector3 cur = bb2.center;
            Vector2 target = new Vector2(ap.x, ap.z) + BoatAlongRiverOffset * D + BoatAcrossRiverOffset * N;
            bt.position += new Vector3(target.x - cur.x, 0f, target.y - cur.z); // 世界位移把船心移到目标(Y不动)
            EditorUtility.SetDirty(bt);
            n++;
        }
        if (n > 0) report.Add($"[船坐进河道] {n} 艘：顺河向 + 船心移到锚点沿河{BoatAlongRiverOffset:F2}/垂河{BoatAcrossRiverOffset:F2}");
    }

    static void SetPartLocalY(MapStationAnchor a, string namePart, float y)
    {
        foreach (var t in a.GetComponentsInChildren<Transform>(true))
        {
            if (t == null || !t.name.Contains(namePart)) continue;
            var lp = t.localPosition;
            t.localPosition = new Vector3(lp.x, y, lp.z);
            EditorUtility.SetDirty(t);
        }
    }

    /// <summary>水驿建筑底高于附近水面：挖河后水驿仍坐在河谷低处会被水面淹没，按建筑底与最近水面高差整体抬升。
    /// 只抬升（取净空），已在水面之上的不动；用渲染器包围盒计算，与运行期可见高度一致。【已停用：改用 ApplyWaterStationPartHeights 烘手调值】</summary>
    static void LiftWaterStationsAboveWater(Dictionary<string, MapStationAnchor> anchorById, List<string> report)
    {
        if (anchorById == null) return;
        const float clearance = 1.0f;    // 建筑底需高出水面的净空（0.6→1.0：用户要水驿再抬高一点）
        const float searchRadius = 9f;   // 取该半径内的水面渲染器作为“附近水面”
        var allRenderers = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
        foreach (var kv in anchorById)
        {
            MapStationAnchor anchor = kv.Value;
            if (anchor == null || anchor.stationType != MapStationType.WaterStation) continue; // 只抬水驿；码头半水半陆、落在岸上不抬
            Vector3 p = anchor.transform.position;

            float waterTop = float.NegativeInfinity;
            foreach (var r in allRenderers)
            {
                if (r == null) continue;
                bool isWater = r.name.Contains("_Route") && r.sharedMaterial != null && r.sharedMaterial.name.Contains("Water");
                if (!isWater) continue;
                float dx = Mathf.Max(0f, Mathf.Abs(p.x - r.bounds.center.x) - r.bounds.extents.x);
                float dz = Mathf.Max(0f, Mathf.Abs(p.z - r.bounds.center.z) - r.bounds.extents.z);
                if (dx * dx + dz * dz > searchRadius * searchRadius) continue;
                if (r.bounds.max.y > waterTop) waterTop = r.bounds.max.y;
            }
            if (float.IsNegativeInfinity(waterTop)) continue;

            float baseY = float.PositiveInfinity;
            foreach (var r in anchor.GetComponentsInChildren<MeshRenderer>(true))
                if (r != null) baseY = Mathf.Min(baseY, r.bounds.min.y);
            if (float.IsPositiveInfinity(baseY)) baseY = p.y;

            float need = waterTop + clearance - baseY;
            if (need <= 0.01f) continue;
            anchor.transform.position = new Vector3(p.x, p.y + need, p.z);
            EditorUtility.SetDirty(anchor.transform);
            EditorUtility.SetDirty(anchor);
            report.Add($"[水驿抬升] {kv.Key} +{need:F2}m 使建筑底高于水面 (waterTop={waterTop:F2}, baseY={baseY:F2})");
        }
    }

    /// <summary>去掉水驿/码头脚下的夯土台 mesh（Node_GroundBlend）：水驿改为「架在水上」后不再需要旱台，
    /// 隐藏它即可露出脚下的水/河床（用户：把地形台去掉，水驿在水里才正常）。</summary>
    static void HideWaterStationGroundPads(Dictionary<string, MapStationAnchor> anchorById, List<string> report)
    {
        if (anchorById == null) return;
        int n = 0;
        foreach (var kv in anchorById)
        {
            var a = kv.Value;
            if (a == null || (a.stationType != MapStationType.WaterStation && a.stationType != MapStationType.Dock)) continue;
            foreach (var t in a.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || !t.name.Contains("GroundBlend")) continue;
                if (t.gameObject.activeSelf) { t.gameObject.SetActive(false); n++; }
            }
        }
        if (n > 0) report.Add($"[水驿去台] 隐藏 {n} 个站点夯土台(GroundBlend)");
    }

    /// <summary>给每个水驿/码头脚下铺一张圆形水面（水塘）：河道只从特定方向来、背水侧没水，用这张圆水面
    /// 把整个水驿盖进水里。高度用 transform.Y=WaterRenderHeight（WaterShader 按物体位置渲染，非顶点）；
    /// 略低于河道水面 0.02 → 与河道重叠处让河道在上、不叠成亮块。半径 = WaterStationPondRadius（与挖塘/退路同源）。</summary>
    static void BuildWaterStationPonds(Dictionary<string, MapStationAnchor> anchorById, SceneContext ctx, List<string> report)
    {
        if (anchorById == null) return;
        Material waterMat = FindWaterRouteMaterial();
        if (waterMat == null) { report.Add("[水塘] 找不到水材质，跳过"); return; }
        Transform root = EnsureSceneRoot("MapLayout_WaterStationPonds");
        for (int i = root.childCount - 1; i >= 0; i--) UnityEngine.Object.DestroyImmediate(root.GetChild(i).gameObject);
        int n = 0;
        foreach (var kv in anchorById)
        {
            var a = kv.Value;
            if (!IsWaterStation(a)) continue;
            float r = WaterStationPondRadius(a);
            Vector3 ap = a.transform.position;
            var go = new GameObject(kv.Key + "_WaterPond");
            go.transform.SetParent(root, false);
            go.transform.position = new Vector3(ap.x, WaterRenderHeight - 0.02f, ap.z);
            go.AddComponent<MeshFilter>().sharedMesh = CreateDiscMesh(r, 32, kv.Key + "_WaterPond", ctx);
            go.AddComponent<MeshRenderer>().sharedMaterial = waterMat;
            n++;
        }
        report.Add($"[水塘] 生成 {n} 个水驿水塘");
    }

    static Material FindWaterRouteMaterial()
    {
        foreach (var mr in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            if (mr != null && mr.sharedMaterial != null && mr.name.Contains("_Route") && mr.sharedMaterial.name.Contains("Water"))
                return mr.sharedMaterial;
        return null;
    }

    /// <summary>生成一张水平圆盘网格（扇形三角、法线朝上 +Y），半径 radius，存为资产。</summary>
    static Mesh CreateDiscMesh(float radius, int segs, string name, SceneContext ctx)
    {
        var verts = new Vector3[segs + 1];
        var uvs = new Vector2[segs + 1];
        var tris = new int[segs * 3];
        verts[0] = Vector3.zero; uvs[0] = new Vector2(0.5f, 0.5f);
        for (int i = 0; i < segs; i++)
        {
            float ang = (i / (float)segs) * Mathf.PI * 2f;
            float cx = Mathf.Cos(ang), cz = Mathf.Sin(ang);
            verts[i + 1] = new Vector3(cx * radius, 0f, cz * radius);
            uvs[i + 1] = new Vector2(cx * 0.5f + 0.5f, cz * 0.5f + 0.5f);
        }
        for (int i = 0; i < segs; i++)
        {
            tris[i * 3] = 0;
            tris[i * 3 + 1] = (i + 1) % segs + 1;  // 顺序使法线朝上 +Y
            tris[i * 3 + 2] = i + 1;
        }
        var m = new Mesh { name = name };
        m.vertices = verts; m.uv = uvs; m.triangles = tris;
        m.RecalculateNormals(); m.RecalculateBounds();
        return SaveMesh(m, ctx.meshDir + "/" + Sanitize(ctx.mapKey) + "_" + name + ".asset");
    }

    /// <summary>把水面子物体换成水平水位网格，河床继续下挖，水面不再像贴膜一样沿 U 形坑起伏。</summary>
    static void ReplaceCarvedWater(GameObject edgeObject, string childName, Vector3[] world, float width, float lift,
        SceneContext ctx, List<(MapRouteEdge edge, Vector3[] world)> waters,
        float endTaperFrac = 0f, float minWidthFrac = 1f)
    {
        Transform child = FindChild(edgeObject.transform, childName);
        if (child == null) return;
        var mf = child.GetComponent<MeshFilter>();
        if (mf == null) return;
        Mesh mesh = CreateWaterSurfaceGrid(world, width, RiverBedCrossSegs, childName, child, ctx, waters, lift,
            endTaperFrac, minWidthFrac);
        mf.sharedMesh = SaveMesh(mesh, ctx.meshDir + "/" + Sanitize(ctx.mapKey) + "_" + childName + "_water.asset");
        EditorUtility.SetDirty(mf);
    }

    static void ReplaceWater(GameObject edgeObject, string childName, Vector3[] surf, float width, SceneContext ctx)
    {
        Transform child = FindChild(edgeObject.transform, childName);
        if (child == null) return;
        var filter = child.GetComponent<MeshFilter>();
        if (filter == null) return;
        Mesh mesh = CreateWaterRibbon(surf, width, childName, child);
        filter.sharedMesh = SaveMesh(mesh, ctx.meshDir + "/" + Sanitize(ctx.mapKey) + "_" + childName + ".asset");
        EditorUtility.SetDirty(filter);
    }

    static Vector3[] RaiseSurf(Vector3[] p, float dy)
    {
        var r = new Vector3[p.Length];
        for (int i = 0; i < p.Length; i++) r[i] = p[i] + Vector3.up * dy;
        return r;
    }

    /// <summary>沿挖好的河谷铺一张窄沙石网格（河床/河滩），按端点建筑半径裁掉首尾避开码头/道路接头。子物体缺失则新建。</summary>
    static void BuildRiverBed(MapRouteEdge edge, Vector3[] world, SceneContext ctx,
        List<(MapRouteEdge edge, Vector3[] world)> waters, Material sandMat,
        Dictionary<string, MapStationAnchor> anchorById, List<string> report)
    {
        Vector3[] mid = TrimRiverBedToEndpointClearance(edge, world, anchorById);
        if (mid.Length < 2) { report.Add($"[河床] {edge.edgeId} 路径太短，跳过"); return; }
        string name = edge.edgeId + "_RiverBed";
        Transform bed = FindChild(edge.transform, name);
        if (bed == null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(edge.transform, false);
            go.AddComponent<MeshFilter>();
            go.AddComponent<MeshRenderer>();
            bed = go.transform;
        }
        var mf = bed.GetComponent<MeshFilter>();
        var mr = bed.GetComponent<MeshRenderer>();
        if (sandMat != null && mr != null) mr.sharedMaterial = sandMat;
        Mesh mesh = CreateRiverbedGrid(mid, RiverBedWidth, RiverBedCrossSegs, name, bed, ctx, waters, 0.04f);
        mf.sharedMesh = SaveMesh(mesh, ctx.meshDir + "/" + Sanitize(ctx.mapKey) + "_" + name + ".asset");
        EditorUtility.SetDirty(mf);
        if (mr != null) EditorUtility.SetDirty(mr);
        report.Add($"[河床] {edge.edgeId} 沙石河滩 {mid.Length}x{RiverBedCrossSegs + 1} width={RiverBedWidth:F1} mat={(sandMat ? sandMat.name : "<无>")}");
    }

    /// <summary>多条水路在水驿/码头汇合时，禁用历史透明补片，改生成小范围河床+水面补片。
    /// 旧圆形补片过大，会淹没建筑和道路；小型定向补片只补齐水路端点之间的可见断水。</summary>
    static void BuildWaterJunctionPools(LycheeUiMapConfig config,
        Dictionary<string, LycheeRoutePath> routePathByEdge,
        Dictionary<string, MapStationAnchor> anchorById,
        SceneContext ctx, List<string> report)
    {
        if (config?.edges == null || routePathByEdge == null || anchorById == null) return;
        var byNode = new Dictionary<string, List<WaterEndpointDir>>();

        foreach (var e in config.edges)
        {
            if (ParseRouteType(e.routeType) != MapRouteType.Water) continue;
            if (!routePathByEdge.TryGetValue(e.edgeId, out var path) || path.points == null || path.points.Count < 2) continue;
            AddWaterEndpoint(byNode, e.fromNodeId, e.edgeId, EndpointDirection(path, true, ctx));
            AddWaterEndpoint(byNode, e.toNodeId, e.edgeId, EndpointDirection(path, false, ctx));
        }

        Transform root = null;
        Material waterMat = FindWaterMaterial();
        int built = 0;
        foreach (var kv in byNode)
        {
            if (kv.Value.Count < 2) continue;
            if (!anchorById.TryGetValue(kv.Key, out var anchor) || anchor == null) continue;
            if (root == null) root = EnsureSceneRoot("MapLayout_WaterJunctionPools");

            string poolName = kv.Key + "_WaterJunctionPool";
            string bedName = kv.Key + "_WaterJunctionBed";
            Transform oldPool = root != null ? FindChild(root, poolName) : null;
            if (oldPool != null) UnityEngine.Object.DestroyImmediate(oldPool.gameObject);
            Transform oldBed = root != null ? FindChild(root, bedName) : null;
            if (oldBed != null) UnityEngine.Object.DestroyImmediate(oldBed.gameObject);

            Transform legacyPatch = FindAnyByName("Patch_" + kv.Key + "_Junction");
            if (legacyPatch != null)
            {
                legacyPatch.gameObject.SetActive(false);
                EditorUtility.SetDirty(legacyPatch.gameObject);
                report.Add($"[水面补片] {kv.Key}: 禁用旧 Patch_{kv.Key}_Junction，避免透明水面叠加成块状亮斑");
            }

            float dryCore = WaterJunctionCoreWaterRadius;
            if (anchor.stationType == MapStationType.WaterStation)
                dryCore = Mathf.Clamp(BuildingRadius(anchor) + WaterStationSurfaceClearExtra,
                    WaterJunctionCoreWaterRadius, WaterStationSurfaceClearMax);

            float waterY = JunctionWaterY(ctx, anchor, kv.Value, routePathByEdge);
            CreateWaterJunctionObject(root, poolName, anchor.transform.position, kv.Value,
                Mathf.Max(WaterJunctionWaterReach, dryCore + 1.35f), WaterJunctionWaterWidth, dryCore,
                waterY, true, waterMat, ctx);

            built++;
            report.Add($"[水面补片] {kv.Key}: 仅生成避让站点核心的小型交汇水面（{kv.Value.Count} 条水路，dryCore={dryCore:F1}），河床由主河道连续覆盖");
        }
        if (built > 0) report.Add($"[水面补片] 共生成 {built} 个水驿/码头交汇补片");
    }

    static void CleanLegacyWaterChildren(GameObject edgeObject, string edgeId)
    {
        if (edgeObject == null || string.IsNullOrEmpty(edgeId)) return;
        // 名字含这些遗留水路修边的子物体可能有重复同名变体(W/X/Y)，逐一全删（橄榄绿河岸线根因）
        var doomed = new List<Transform>();
        foreach (Transform c in edgeObject.transform)
        {
            string n = c.name;
            if (n.Contains("_BankTrim") || n.Contains("_BankSlope") || n.Contains("_RiverBank") || n.Contains("_ShallowWater") || n.Contains("_WaterHighlight"))
                doomed.Add(c);
        }
        foreach (var d in doomed) if (d != null) UnityEngine.Object.DestroyImmediate(d.gameObject);
    }

    static float JunctionWaterY(SceneContext ctx, MapStationAnchor anchor, List<WaterEndpointDir> endpoints,
        Dictionary<string, LycheeRoutePath> routePathByEdge)
    {
        float sum = 0f;
        int count = 0;
        if (endpoints != null && routePathByEdge != null)
        {
            foreach (var ep in endpoints)
            {
                if (string.IsNullOrEmpty(ep.edgeId) ||
                    !routePathByEdge.TryGetValue(ep.edgeId, out var path) ||
                    path?.points == null || path.points.Count == 0)
                    continue;

                int best = 0;
                float bestD = float.MaxValue;
                Vector3 stationPos = anchor != null ? anchor.transform.position : Vector3.zero;
                for (int i = 0; i < path.points.Count; i++)
                {
                    Vector3 wp = GridToWorld(path.points[i].x, path.points[i].y, WaterLift, ctx);
                    float d = Vector2.SqrMagnitude(new Vector2(wp.x - stationPos.x, wp.z - stationPos.z));
                    if (d < bestD) { bestD = d; best = i; }
                }

                float along = path.points.Count <= 1 ? 0f : best / (float)(path.points.Count - 1);
                Vector3 sample = GridToWorld(path.points[best].x, path.points[best].y, WaterLift, ctx);
                sum += WaterSurfaceYAt(ctx, sample, along, ctx.waterTuples) + WaterDrapeLift;
                count++;
            }
        }

        if (count > 0) return sum / count;   // S1：去掉 -0.10 错层，交汇水面与主河道同高
        Vector3 p = anchor != null ? anchor.transform.position : Vector3.zero;
        float y = ctx.terrainSurface != null && ctx.terrainSurface.TrySampleTop(p.x, p.z, out float ty) ? ty : p.y;
        return y - WaterDepthBelowBank + WaterDrapeLift;   // 与统一水位一致（岸顶-0.30）
    }

    static void CreateWaterJunctionObject(Transform root, string name, Vector3 anchorPos, List<WaterEndpointDir> endpoints,
        float reach, float width, float coreRadius, float y, bool isWater, Material material, SceneContext ctx)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root, false);
        go.AddComponent<MeshFilter>();
        go.AddComponent<MeshRenderer>();

        var mr = go.GetComponent<MeshRenderer>();
        if (material != null) mr.sharedMaterial = material;
        var mf = go.GetComponent<MeshFilter>();
        Mesh mesh = CreateJunctionFanMesh(anchorPos, endpoints, reach, width, coreRadius, y, isWater, go.transform);
        mf.sharedMesh = SaveMesh(mesh, ctx.meshDir + "/" + Sanitize(ctx.mapKey) + "_" + name + ".asset");
        EditorUtility.SetDirty(mf);
        EditorUtility.SetDirty(mr);
    }

    static Mesh CreateJunctionFanMesh(Vector3 anchorPos, List<WaterEndpointDir> endpoints,
        float reach, float width, float coreRadius, float y, bool isWater, Transform target)
    {
        var dirs = new List<Vector2>();
        if (endpoints != null)
        {
            foreach (var ep in endpoints)
                if (ep.dir.sqrMagnitude > 1e-5f)
                    dirs.Add(ep.dir.normalized);
        }
        if (dirs.Count == 0) dirs.Add(Vector2.right);

        var vertices = new List<Vector3>();
        var uv = new List<Vector2>();
        var tris = new List<int>();
        Vector3 center = new Vector3(anchorPos.x, y, anchorPos.z);

        int AddVertex(Vector3 world)
        {
            int index = vertices.Count;
            vertices.Add(target.InverseTransformPoint(world));
            uv.Add(new Vector2(
                0.5f + (world.x - anchorPos.x) / Mathf.Max(0.01f, reach * 2f),
                0.5f + (world.z - anchorPos.z) / Mathf.Max(0.01f, reach * 2f)));
            return index;
        }

        void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int ia = AddVertex(a), ib = AddVertex(b), ic = AddVertex(c), id = AddVertex(d);
            tris.Add(ia); tris.Add(ic); tris.Add(ib);
            tris.Add(ia); tris.Add(id); tris.Add(ic);
        }

        foreach (Vector2 d in dirs)
        {
            Vector2 side = new Vector2(-d.y, d.x);
            float half = Mathf.Max(0.2f, width * 0.5f);
            float effectiveReach = isWater ? Mathf.Max(reach, coreRadius + 0.35f) : reach;
            Vector3 dir3 = new Vector3(d.x, 0f, d.y);
            Vector3 start = isWater ? center + dir3 * coreRadius : center - dir3 * (coreRadius * 0.35f);
            Vector3 end = center + dir3 * effectiveReach;
            Vector3 s = new Vector3(side.x, 0f, side.y);
            float startHalf = half * (isWater ? 0.55f : 1f);
            AddQuad(start - s * startHalf, end - s * half, end + s * half, start + s * startHalf);
        }

        if (!isWater)
        {
            int coreSegments = Mathf.Max(8, dirs.Count * 6);
            int coreCenter = AddVertex(center);
            var ring = new int[coreSegments];
            for (int i = 0; i < coreSegments; i++)
            {
                float a = Mathf.PI * 2f * i / coreSegments;
                float noise = (Mathf.PerlinNoise(i * 0.37f, 12.4f) - 0.5f) * 0.12f;
                float r = Mathf.Max(0.2f, coreRadius + noise);
                ring[i] = AddVertex(new Vector3(anchorPos.x + Mathf.Cos(a) * r, y, anchorPos.z + Mathf.Sin(a) * r));
            }
            for (int i = 0; i < coreSegments; i++)
            {
                tris.Add(coreCenter);
                tris.Add(ring[(i + 1) % coreSegments]);
                tris.Add(ring[i]);
            }
        }

        var mesh = new Mesh { name = target.name };
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uv);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    static void AddWaterEndpoint(Dictionary<string, List<WaterEndpointDir>> byNode, string nodeId, string edgeId, Vector2 dir)
    {
        if (string.IsNullOrEmpty(nodeId)) return;
        if (!byNode.TryGetValue(nodeId, out var list))
        {
            list = new List<WaterEndpointDir>();
            byNode[nodeId] = list;
        }
        list.Add(new WaterEndpointDir { edgeId = edgeId, dir = dir });
    }

    static Vector2 EndpointDirection(LycheeRoutePath path, bool fromStart, SceneContext ctx)
    {
        int a = fromStart ? 0 : path.points.Count - 1;
        int b = fromStart ? 1 : path.points.Count - 2;
        Vector3 wa = GridToWorld(path.points[a].x, path.points[a].y, 0f, ctx);
        Vector3 wb = GridToWorld(path.points[b].x, path.points[b].y, 0f, ctx);
        var d = new Vector2(wb.x - wa.x, wb.z - wa.z);
        return d.sqrMagnitude < 1e-5f ? Vector2.right : d.normalized;
    }

    static Vector3[] TrimRiverBedToEndpointClearance(MapRouteEdge edge, Vector3[] pts,
        Dictionary<string, MapStationAnchor> anchorById)
    {
        if (pts == null || pts.Length < 2) return pts;
        float startClear = EndpointRiverBedClear(edge != null ? edge.fromStationId : null, anchorById);
        float endClear = EndpointRiverBedClear(edge != null ? edge.toStationId : null, anchorById);
        return TrimPolylineByDistance(pts, startClear, endClear, 2.0f);
    }

    static Vector3[] TrimWaterSurfaceToEndpointClearance(MapRouteEdge edge, Vector3[] pts,
        Dictionary<string, MapStationAnchor> anchorById)
    {
        if (pts == null || pts.Length < 2) return pts;
        float startClear = EndpointWaterSurfaceClear(edge != null ? edge.fromStationId : null, anchorById);
        float endClear = EndpointWaterSurfaceClear(edge != null ? edge.toStationId : null, anchorById);
        if (startClear <= 0.001f && endClear <= 0.001f) return pts;
        return TrimPolylineByDistance(pts, startClear, endClear, 2.0f);
    }

    static float EndpointRiverBedClear(string stationId, Dictionary<string, MapStationAnchor> anchorById)
    {
        return RiverBedEndpointClearDistance;
    }

    static float EndpointWaterSurfaceClear(string stationId, Dictionary<string, MapStationAnchor> anchorById)
    {
        if (string.IsNullOrEmpty(stationId) || anchorById == null ||
            !anchorById.TryGetValue(stationId, out var anchor) || anchor == null)
            return 0f;

        if (anchor.stationType != MapStationType.WaterStation)
            return 0f;

        return Mathf.Clamp(BuildingRadius(anchor) + WaterStationSurfaceClearExtra,
            WaterJunctionCoreWaterRadius, WaterStationSurfaceClearMax);
    }

    static Vector3[] TrimPolylineByDistance(Vector3[] pts, float startClear, float endClear, float minSpan)
    {
        if (pts == null || pts.Length < 2) return pts;
        var dist = new float[pts.Length];
        for (int i = 1; i < pts.Length; i++)
            dist[i] = dist[i - 1] + SegmentLengthXZ(pts[i - 1], pts[i]);
        float total = dist[dist.Length - 1];
        if (total <= Mathf.Max(0.1f, minSpan)) return pts;

        startClear = Mathf.Max(0f, startClear);
        endClear = Mathf.Max(0f, endClear);
        float maxTrim = Mathf.Max(0f, total - Mathf.Max(0.1f, minSpan));
        float trimSum = startClear + endClear;
        if (trimSum > maxTrim && trimSum > 0.001f)
        {
            float scale = maxTrim / trimSum;
            startClear *= scale;
            endClear *= scale;
        }

        float startAt = Mathf.Clamp(startClear, 0f, total);
        float endAt = Mathf.Clamp(total - endClear, startAt + 0.05f, total);
        var r = new List<Vector3> { PointOnPolylineAtDistance(pts, dist, startAt) };
        for (int i = 1; i < pts.Length - 1; i++)
            if (dist[i] > startAt + 0.05f && dist[i] < endAt - 0.05f)
                r.Add(pts[i]);
        r.Add(PointOnPolylineAtDistance(pts, dist, endAt));
        return r.Count >= 2 ? r.ToArray() : pts;
    }

    static Vector3 PointOnPolylineAtDistance(Vector3[] pts, float[] dist, float target)
    {
        if (pts == null || pts.Length == 0) return Vector3.zero;
        if (pts.Length == 1 || target <= 0f) return pts[0];
        for (int i = 1; i < pts.Length; i++)
        {
            if (target > dist[i]) continue;
            float seg = Mathf.Max(0.0001f, dist[i] - dist[i - 1]);
            float t = Mathf.Clamp01((target - dist[i - 1]) / seg);
            return Vector3.Lerp(pts[i - 1], pts[i], t);
        }
        return pts[pts.Length - 1];
    }

    static float SegmentLengthXZ(Vector3 a, Vector3 b)
    {
        return Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
    }

    /// <summary>把可见路面折线"精确裁到水塘边界圆上"：水驿(WaterStation)端按 WaterStationPondRadius+WaterStationRoadGap 裁，
    /// 并在圆周处插一个插值点 → 灰路面正好铺到塘边/码头口、不留黄沙缺口、也不架水上。
    /// 只裁水驿端（删点会停在最后一个外侧稀疏点上、离塘远，故要插边界点）；码头/陆站端不动。</summary>
    static Vector3[] TrimRibbonAtWaterStationEnds(Vector3[] world, MapStationAnchor from, MapStationAnchor to)
    {
        if (world == null || world.Length < 2) return world;
        bool fw = IsWaterStation(from), tw = IsWaterStation(to);
        if (!fw && !tw) return world;
        var list = new List<Vector3>(world);
        if (fw) ClipPathStartAtCircle(list, new Vector2(from.transform.position.x, from.transform.position.z), WaterStationPondRadius(from) + WaterStationRoadGap);
        if (tw) { list.Reverse(); ClipPathStartAtCircle(list, new Vector2(to.transform.position.x, to.transform.position.z), WaterStationPondRadius(to) + WaterStationRoadGap); list.Reverse(); }
        return list.Count >= 2 ? list.ToArray() : world;
    }

    /// <summary>从折线起点方向裁掉落在「圆心 center、半径 radius」圆内的点，并在圆周处插一个插值点 → 折线正好止于圆周。</summary>
    static void ClipPathStartAtCircle(List<Vector3> pts, Vector2 center, float radius)
    {
        int firstOut = -1;
        for (int i = 0; i < pts.Count; i++)
            if (Vector2.Distance(new Vector2(pts[i].x, pts[i].z), center) >= radius) { firstOut = i; break; }
        if (firstOut <= 0) return; // 起点已在圆外（无需裁）或全部在圆内（不动）
        Vector3 inside = pts[firstOut - 1], outside = pts[firstOut];
        float da = Vector2.Distance(new Vector2(inside.x, inside.z), center);
        float db = Vector2.Distance(new Vector2(outside.x, outside.z), center);
        float t = Mathf.Approximately(db, da) ? 0f : Mathf.Clamp01((radius - da) / (db - da));
        Vector3 boundary = Vector3.Lerp(inside, outside, t);
        pts.RemoveRange(0, firstOut);
        pts.Insert(0, boundary);
    }

    /// <summary>把路面折线裁到两端驿站建筑足迹之外：去掉落在 from/to 建筑半径内的点，路面止于建筑边缘、不盖楼/码头/船。
    /// 至少保留 2 点；驿站无建筑模型时该端按默认半径裁一点。</summary>
    static Vector3[] TrimToBuildingEdges(Vector3[] world, MapStationAnchor from, MapStationAnchor to)
    {
        if (world == null || world.Length < 4) return world;
        Vector2 fp = from != null ? new Vector2(from.transform.position.x, from.transform.position.z)
                                  : new Vector2(world[0].x, world[0].z);
        Vector2 tp = to != null ? new Vector2(to.transform.position.x, to.transform.position.z)
                                : new Vector2(world[world.Length - 1].x, world[world.Length - 1].z);
        // 水驿：路退到塘外留水隔开；码头：普通裁到建筑边（路直接通到脚下）
        float fr = IsWaterStation(from) ? WaterStationPondRadius(from) + WaterStationRoadGap : BuildingRadius(from) + 0.4f;
        float tr = IsWaterStation(to)   ? WaterStationPondRadius(to)   + WaterStationRoadGap : BuildingRadius(to)   + 0.4f;
        var r = new List<Vector3>();
        foreach (var p in world)
        {
            var pp = new Vector2(p.x, p.z);
            if (Vector2.Distance(pp, fp) <= fr || Vector2.Distance(pp, tp) <= tr) continue;
            r.Add(p);
        }
        return r.Count >= 2 ? r.ToArray() : world;
    }

    static bool IsWaterStationOrDock(MapStationAnchor a) =>
        a != null && (a.stationType == MapStationType.WaterStation || a.stationType == MapStationType.Dock);

    static bool IsWaterStation(MapStationAnchor a) =>
        a != null && a.stationType == MapStationType.WaterStation;

    /// <summary>驿站建筑足迹的水平半径（Imported_Station_Model 包围盒 XZ 半宽）；无模型回退 2.2m。</summary>
    static float BuildingRadius(MapStationAnchor a)
    {
        if (a == null) return 2.2f;
        Transform model = a.transform.Find("Imported_Station_Model");
        if (model == null) return 2.2f;
        var rs = model.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return 2.2f;
        Bounds b = rs[0].bounds;
        foreach (var r in rs) b.Encapsulate(r.bounds);
        return Mathf.Clamp(Mathf.Max(b.extents.x, b.extents.z), 1.5f, 6f);
    }

    /// <summary>从现役地形渲染器里找河床沙石材质（含 RiverBed/Sand 名）；找不到回退按资产名搜。</summary>
    static Material FindSandMaterial(SceneContext ctx)
    {
        Material generated = EnsureMapLayoutRiverbedMaterial();
        if (generated != null) return generated;

        string[] preferred =
        {
            "Assets/LycheeMap15/Materials/M_Config_BranchRoute_Border.mat",
            "Assets/LycheeMap15/Materials/M_Config_OfficialRoad_Border.mat",
            "Assets/LycheeMap15/Materials/M_Config_MountainRoute_Border.mat",
            "Assets/LycheeMap15/Materials/M_RiverBed_SandGravel.mat",
            "Assets/LycheeMap15/Materials/RiverBed_Mud.mat",
            "Assets/LycheeMap15/Materials/M_RiverBank_Refined.mat"
        };
        foreach (string p in preferred)
        {
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (mat != null) return mat;
        }

        foreach (var mf in ctx.terrainFilters)
        {
            var mr = mf != null ? mf.GetComponent<MeshRenderer>() : null;
            if (mr == null) continue;
            foreach (var m in mr.sharedMaterials)
                if (m != null && (m.name.Contains("RiverBed") || m.name.Contains("Sand"))) return m;
        }
        return null;
    }

    static Material FindWaterMaterial()
    {
        string[] preferred =
        {
            "Assets/LycheeMap15/Materials/M_Config_WaterRoute.mat",          // Shader Graphs/WaterShader：会动的流水（优先，给流动水感）
            "Assets/IgniteCoders/Simple Water Shader/Resources/Water_mat_01.mat",
            "Assets/IgniteCoders/Simple Water Shader/Resources/Water_mat_02.mat",
            "Assets/LycheeMap15/Materials/M_RiverWater_Fuller_20260611Y.mat" // 静态 Unlit，仅作兜底
        };
        foreach (string p in preferred)
        {
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (mat != null) return mat;
        }

        foreach (MapRouteEdge edge in UnityEngine.Object.FindObjectsByType<MapRouteEdge>(FindObjectsSortMode.None))
        {
            if (edge == null || edge.routeType != MapRouteType.Water) continue;
            Transform route = FindChild(edge.transform, edge.edgeId + "_Route");
            var renderer = route != null ? route.GetComponent<MeshRenderer>() : null;
            if (renderer != null && renderer.sharedMaterial != null) return renderer.sharedMaterial;
        }
        return null;
    }

    static Material EnsureMapLayoutRiverbedMaterial()
    {
        const string path = "Assets/LycheeMap15/Materials/M_MapLayout_Riverbed_Sandy.mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            Material source =
                AssetDatabase.LoadAssetAtPath<Material>("Assets/LycheeMap15/Materials/M_RiverBank_Refined.mat") ??
                AssetDatabase.LoadAssetAtPath<Material>("Assets/LycheeMap15/Materials/M_RiverBed_SandGravel.mat") ??
                AssetDatabase.LoadAssetAtPath<Material>("Assets/LycheeMap15/Materials/M_Config_BranchRoute_Border.mat") ??
                AssetDatabase.LoadAssetAtPath<Material>("Assets/LycheeMap15/Materials/M_Config_OfficialRoad_Border.mat");
            Shader shader = source != null ? source.shader : Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            mat = source != null ? new Material(source) : new Material(shader);
            mat.name = "M_MapLayout_Riverbed_Sandy";
            EnsureFolderRecursive("Assets/LycheeMap15/Materials");
            AssetDatabase.CreateAsset(mat, path);
        }

        Texture2D sandTex =
            AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Pure Poly/Free Low Poly Nature Pack/Terrain/Terrain_Textures/PP_Pebbles_Light_Brown.png") ??
            AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Pure Poly/Free Low Poly Nature Pack/Terrain/Terrain_Textures/PP_Ground_Light_Brown.png") ??
            AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/LycheeMap15/Materials/T_SandySoil.asset");
        Texture2D normalTex =
            AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Pure Poly/Free Low Poly Nature Pack/Terrain/Terrain_Textures/PP_Pebbles_Normal.png");
        Color sand = new Color(1.02f, 0.94f, 0.78f, 1f);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", sand);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", sand);
        if (mat.HasProperty("_BaseMap")) { mat.SetTexture("_BaseMap", sandTex); mat.SetTextureScale("_BaseMap", new Vector2(2.2f, 2.2f)); }
        if (mat.HasProperty("_MainTex")) { mat.SetTexture("_MainTex", sandTex); mat.SetTextureScale("_MainTex", new Vector2(2.2f, 2.2f)); }
        if (normalTex != null && mat.HasProperty("_BumpMap"))
        {
            mat.SetTexture("_BumpMap", normalTex);
            mat.SetTextureScale("_BumpMap", new Vector2(2.2f, 2.2f));
            mat.EnableKeyword("_NORMALMAP");
        }
        if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", 0.45f);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.035f);
        if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    // ============================================================ 清障（植被/岩石不能挡新路）

    static readonly string[] DecoGroupKeywords = { "Cluster", "Groundcover", "Grove", "Irregular", "Decoration", "Nature", "Unified" };
    static readonly string[] DecoLeafKeywords = { "Tree", "Rock", "Grass", "Forest", "Bush", "Plant", "Moss", "PP_", "URP_Tree", "Mountain_Moss" };
    static readonly string[] DecoExcludeKeywords = { "Route", "Anchor", "Station", "Terrain", "Overlay", "PathPoint", "Slot", "RiverBed", "Convoy", "Squad" };

    /// <summary>清掉压在新路/新河中心线一定半径内的植被、岩石（原装饰是按旧路网布的，会挡新路）。
    /// 只在生成出来的副本场景里删（母版不动）。按"装饰实例根"整株删除，避免删半棵。</summary>
    static void ClearDecorationAlongPaths(List<(MapRouteEdge edge, Vector3[] world, MapRouteType type)> changed,
        SceneContext ctx, List<string> report)
    {
        if (changed == null || changed.Count == 0) return;
        var toRemove = new HashSet<GameObject>();
        foreach (var r in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
        {
            var go = r.gameObject;
            if (!IsDecoration(go.transform)) continue;
            var b = r.bounds;
            float ext = Mathf.Max(b.extents.x, b.extents.z); // 物体水平半径（树冠/石头半宽）
            foreach (var (edge, world, type) in changed)
            {
                float clear = type == MapRouteType.Water ? DecoReachWater : DecoReachRoad;
                float reach = NearestOnPolylineXZ(b.center.x, b.center.z, world, out _, out _) - ext; // 包围盒边缘到中心线
                if (reach < clear)
                {
                    GameObject inst = DecoInstanceRoot(go.transform);
                    if (inst != null) toRemove.Add(inst);
                    break;
                }
            }
        }
        int n = 0;
        foreach (var go in toRemove) { if (go != null) { UnityEngine.Object.DestroyImmediate(go); n++; } }
        report.Add($"[清障] 清除压在新路/新河上的植被岩石 {n} 个");
    }

    /// <summary>专清落在河道走廊与水驿/码头足迹内的草丛(PP_Grass 等)：挖河把地表降了、草丛还贴老地表悬在
    /// 河道里；现有沿中心线清障半径够不到码头四周(江南码头交错混乱成因之一)。只删名字含 Grass 的装饰实例整株删；
    /// 复用 IsDecoration/DecoInstanceRoot，借 DecoExcludeKeywords 天然不碰站点建筑/河床/船/树。只在副本场景执行。</summary>
    static void ClearGrassInRiverAndDockFootprint(
        List<(MapRouteEdge edge, Vector3[] world)> waters,
        Dictionary<string, MapStationAnchor> anchorById, List<string> report)
    {
        float riverClear = Mathf.Max(CarveHalfWidth, RiverBedWidth * 0.5f) + 0.9f; // 盖到沙床外缘+岸过渡带
        var dockCircles = new List<(Vector2 c, float r)>();
        if (anchorById != null)
            foreach (var a in anchorById.Values)
            {
                if (a == null) continue;
                if (a.stationType != MapStationType.Dock && a.stationType != MapStationType.WaterStation) continue;
                Bounds bb = default; bool has = false;
                foreach (var mr in a.GetComponentsInChildren<MeshRenderer>(true))
                { if (!has) { bb = mr.bounds; has = true; } else bb.Encapsulate(mr.bounds); }
                float r = (has ? Mathf.Max(bb.extents.x, bb.extents.z) : 2.2f) + 1.0f;
                dockCircles.Add((new Vector2(a.transform.position.x, a.transform.position.z), r));
            }

        var toRemove = new HashSet<GameObject>();
        foreach (var rend in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
        {
            var go = rend.gameObject;
            if (!go.name.Contains("Grass")) continue;          // 只清草丛，绝不碰树/石/建筑/河床
            if (!IsDecoration(go.transform)) continue;
            Vector3 c = rend.bounds.center;
            bool hit = false;
            if (waters != null)
                foreach (var (edge, w) in waters)
                {
                    if (w == null || w.Length == 0) continue;
                    if (NearestOnPolylineXZ(c.x, c.z, w, out _, out _) < riverClear) { hit = true; break; }
                }
            if (!hit)
            {
                var p2 = new Vector2(c.x, c.z);
                foreach (var (cc, rr) in dockCircles)
                    if ((p2 - cc).sqrMagnitude < rr * rr) { hit = true; break; }
            }
            if (hit)
            {
                GameObject inst = DecoInstanceRoot(go.transform);
                if (inst != null) toRemove.Add(inst);
            }
        }
        int n = 0;
        foreach (var go in toRemove) { if (go != null) { UnityEngine.Object.DestroyImmediate(go); n++; } }
        report.Add($"[清草] 河道走廊+码头足迹内草丛清除 {n} 株");
    }

    static bool IsDecoration(Transform t)
    {
        for (var cur = t; cur != null; cur = cur.parent)
        {
            string nm = cur.name;
            foreach (var k in DecoExcludeKeywords) if (nm.Contains(k)) return false;
            foreach (var k in DecoGroupKeywords) if (nm.Contains(k)) return true;
            foreach (var k in DecoLeafKeywords) if (nm.Contains(k)) return true;
        }
        return false;
    }

    /// <summary>从渲染器向上找到「装饰实例根」（其父是装饰组容器或装饰层），整株删除。</summary>
    static GameObject DecoInstanceRoot(Transform t)
    {
        var cur = t;
        while (cur.parent != null)
        {
            string pn = cur.parent.name;
            foreach (var k in DecoGroupKeywords) if (pn.Contains(k)) return cur.gameObject;
            if (pn.Contains("Decoration") || pn.StartsWith("04_")) return cur.gameObject;
            cur = cur.parent;
        }
        return t.gameObject;
    }

    /// <summary>河床/河滩专用网格：边缘有稳定轻微起伏，首尾自然收窄，避免生成像矩形贴片一样的河道。</summary>
    static Mesh CreateRiverbedGrid(Vector3[] center, float width, int crossSegs, string name, Transform target,
        SceneContext ctx, List<(MapRouteEdge edge, Vector3[] world)> waters, float lift)
    {
        int n = center.Length;
        int cols = crossSegs + 1;
        var vertices = new Vector3[n * cols];
        var uv = new Vector2[vertices.Length];
        var tris = new List<int>((n - 1) * crossSegs * 6);
        float distance = 0f;
        for (int i = 0; i < n; i++)
        {
            Vector3 fwd = i == n - 1 ? center[i] - center[i - 1] : center[i + 1] - center[i];
            fwd.y = 0f;
            Vector3 side = Vector3.Cross(Vector3.up, fwd.sqrMagnitude < 1e-8f ? Vector3.forward : fwd.normalized);
            if (i > 0) distance += Vector3.Distance(center[i - 1], center[i]);

            float endT = Mathf.Min(i / Mathf.Max(1f, (n - 1) * 0.20f), (n - 1 - i) / Mathf.Max(1f, (n - 1) * 0.20f));
            float taper = Mathf.Lerp(0.96f, 1f, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(endT))); // 端点收口 0.82→0.96，沙床端点不缩进露草
            float leftNoise = (Mathf.PerlinNoise(i * 0.21f + 17.3f, 6.2f) - 0.5f) * 0.05f;      // 边缘噪声 0.14→0.05，沙床边缘稳定盖满不露草
            float rightNoise = (Mathf.PerlinNoise(i * 0.19f + 31.1f, 2.8f) - 0.5f) * 0.05f;
            float leftHalf = width * 0.5f * taper + leftNoise;
            float rightHalf = width * 0.5f * taper + rightNoise;

            for (int j = 0; j < cols; j++)
            {
                float crossT = j / (float)crossSegs;
                float lat = Mathf.Lerp(-leftHalf, rightHalf, crossT);
                Vector3 wpt = center[i] + side * lat;
                wpt.y = CarvedY(ctx, wpt.x, wpt.z, waters) + lift;
                int idx = i * cols + j;
                vertices[idx] = target.InverseTransformPoint(wpt);
                uv[idx] = new Vector2(crossT, distance * 0.42f);
            }
        }
        for (int i = 0; i < n - 1; i++)
            for (int j = 0; j < crossSegs; j++)
            {
                int ll = i * cols + j, lr = i * cols + j + 1, nl = (i + 1) * cols + j, nr = (i + 1) * cols + j + 1;
                tris.Add(ll); tris.Add(nl); tris.Add(lr);
                tris.Add(lr); tris.Add(nl); tris.Add(nr);
            }
        var mesh = new Mesh { name = name };
        mesh.vertices = vertices; mesh.uv = uv; mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>沿挖好的河谷铺一张网格：沿路 n 点 × 横向 (crossSegs+1) 列，每顶点贴挖后河谷面 + lift。
    /// 横向细分让沙床贴合 V 形河谷底/坡，不再是悬空平板；水面可在端点渐窄而不断开。顶点存 target 局部空间。</summary>
    static Mesh CreateCarvedGrid(Vector3[] center, float width, int crossSegs, string name, Transform target,
        SceneContext ctx, List<(MapRouteEdge edge, Vector3[] world)> waters, float lift,
        float endTaperFrac = 0f, float minWidthFrac = 1f)
    {
        int n = center.Length;
        int cols = crossSegs + 1;
        var vertices = new Vector3[n * cols];
        var uv = new Vector2[vertices.Length];
        var tris = new List<int>((n - 1) * crossSegs * 6);
        float distance = 0f;
        for (int i = 0; i < n; i++)
        {
            Vector3 fwd = i == n - 1 ? center[i] - center[i - 1] : center[i + 1] - center[i];
            fwd.y = 0f;
            Vector3 side = Vector3.Cross(Vector3.up, fwd.sqrMagnitude < 1e-8f ? Vector3.forward : fwd.normalized);
            if (i > 0) distance += Vector3.Distance(center[i - 1], center[i]);
            float rowWidth = width * EndWidthScale(i, n, endTaperFrac, minWidthFrac);
            for (int j = 0; j < cols; j++)
            {
                float lat = -rowWidth * 0.5f + rowWidth * (j / (float)crossSegs);
                Vector3 wpt = center[i] + side * lat;
                wpt.y = CarvedY(ctx, wpt.x, wpt.z, waters) + lift;
                int idx = i * cols + j;
                vertices[idx] = target.InverseTransformPoint(wpt);
                uv[idx] = new Vector2(j / (float)crossSegs, distance * 0.42f);
            }
        }
        for (int i = 0; i < n - 1; i++)
            for (int j = 0; j < crossSegs; j++)
            {
                int ll = i * cols + j, lr = i * cols + j + 1, nl = (i + 1) * cols + j, nr = (i + 1) * cols + j + 1;
                tris.Add(ll); tris.Add(nl); tris.Add(lr);
                tris.Add(lr); tris.Add(nl); tris.Add(nr);
            }
        var mesh = new Mesh { name = name };
        mesh.vertices = vertices; mesh.uv = uv; mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>从 center 沿 dir 对真实地形 raycast，返回"地形升到水面 waterY"处的横向距离（即水线半宽）。
    /// 水面网格用它裁边 → 精确贴合碗壁、不超出河槽，从根上杜绝"水漫到岸上"（不依赖 shader 深度遮挡）。</summary>
    static float TerrainWaterlineHalf(Vector3 center, Vector3 dir, float waterY)
    {
        float last = 0.15f;
        for (float lat = 0.15f; lat <= 2.2f; lat += 0.1f)
        {
            Vector3 sp = center + dir * lat; sp.y = waterY + 4f;
            var hits = Physics.RaycastAll(sp, Vector3.down, 8f);
            float terr = float.NaN;
            foreach (var h in hits)
            {
                string nm = h.collider.gameObject.name;
                if ((nm.Contains("Terrain") || nm.Contains("Extended_Ground")) && (float.IsNaN(terr) || h.point.y > terr))
                    terr = h.point.y;
            }
            if (float.IsNaN(terr)) return last;          // 采不到地形 → 到此为止
            if (terr >= waterY - 0.005f) return lat;     // 地形升到水面 → 水线（略含进岸一点，水塞到岸下）
            last = lat;
        }
        return last;
    }

    /// <summary>水面网格：每个横截面使用同一个水位，形成有深度的水平水面，而不是贴着河床起伏。</summary>
    static Mesh CreateWaterSurfaceGrid(Vector3[] center, float width, int crossSegs, string name, Transform target,
        SceneContext ctx, List<(MapRouteEdge edge, Vector3[] world)> waters, float lift,
        float endTaperFrac = 0f, float minWidthFrac = 1f)
    {
        int n = center.Length;
        int cols = crossSegs + 1;
        var vertices = new Vector3[n * cols];
        var uv = new Vector2[vertices.Length];
        var tris = new List<int>((n - 1) * crossSegs * 6);
        float distance = 0f;
        for (int i = 0; i < n; i++)
        {
            Vector3 fwd = i == n - 1 ? center[i] - center[i - 1] : center[i + 1] - center[i];
            fwd.y = 0f;
            Vector3 side = Vector3.Cross(Vector3.up, fwd.sqrMagnitude < 1e-8f ? Vector3.forward : fwd.normalized);
            if (i > 0) distance += Vector3.Distance(center[i - 1], center[i]);

            float along = n <= 1 ? 0f : i / (float)(n - 1);
            float waterY = WaterSurfaceYAt(ctx, center[i], along, waters) + lift;
            float rowWidth = 2f * (CarveHalfWidth + 0.4f); // 恢复满宽、不裁剪（用户要求）
            for (int j = 0; j < cols; j++)
            {
                float lat = -rowWidth * 0.5f + rowWidth * (j / (float)crossSegs);
                Vector3 wpt = center[i] + side * lat;
                wpt.y = waterY;
                int idx = i * cols + j;
                vertices[idx] = target.InverseTransformPoint(wpt);
                uv[idx] = new Vector2(j / (float)crossSegs, distance * 0.42f);
            }
        }
        for (int i = 0; i < n - 1; i++)
            for (int j = 0; j < crossSegs; j++)
            {
                int ll = i * cols + j, lr = i * cols + j + 1, nl = (i + 1) * cols + j, nr = (i + 1) * cols + j + 1;
                tris.Add(ll); tris.Add(nl); tris.Add(lr);
                tris.Add(lr); tris.Add(nl); tris.Add(nr);
            }
        var mesh = new Mesh { name = name };
        mesh.vertices = vertices; mesh.uv = uv; mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        return mesh;
    }

    static float EndWidthScale(int index, int count, float taperFrac, float minWidthFrac)
    {
        if (count <= 1 || taperFrac <= 0f) return 1f;
        float denom = Mathf.Max(1f, count - 1);
        float along = index / denom;
        float edge = Mathf.Min(along, 1f - along);
        float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(edge / Mathf.Max(0.001f, taperFrac)));
        return Mathf.Lerp(Mathf.Clamp01(minWidthFrac), 1f, t);
    }

    static Mesh CreateConformedGrid(Vector3[] center, float width, int crossSegs, string name,
        Transform target, SceneContext ctx, float lift)
    {
        int n = center.Length;
        int cols = crossSegs + 1;
        var vertices = new Vector3[n * cols];
        var uv = new Vector2[vertices.Length];
        var tris = new List<int>((n - 1) * crossSegs * 6);
        float distance = 0f;
        for (int i = 0; i < n; i++)
        {
            Vector3 fwd = i == n - 1 ? center[i] - center[i - 1] : center[i + 1] - center[i];
            fwd.y = 0f;
            Vector3 side = Vector3.Cross(Vector3.up, fwd.sqrMagnitude < 1e-8f ? Vector3.forward : fwd.normalized);
            if (i > 0) distance += Vector3.Distance(center[i - 1], center[i]);
            float wid = width * WaterCrossingNarrow(ctx, center[i].x, center[i].z);  // 路基近河/跨河收窄，跟路面一致
            for (int j = 0; j < cols; j++)
            {
                float lat = -wid * 0.5f + wid * (j / (float)crossSegs);
                Vector3 wpt = center[i] + side * lat;
                wpt.y = ConformY(ctx, ctx.terrainSurface, wpt.x, wpt.z, lift, center[i].y);
                if (ctx.endRampOn) wpt.y = RampEndY(ctx, wpt.x, wpt.z, wpt.y);  // 端点压低跟路面一致（陆站端压建筑底/水驿端沉水下），否则路基浮在水上
                wpt.y = Mathf.Min(wpt.y, RoadUnderRiverY(ctx, wpt.x, wpt.z));
                int idx = i * cols + j;
                vertices[idx] = target.InverseTransformPoint(wpt);
                uv[idx] = new Vector2(j / (float)crossSegs, distance * 0.42f);
            }
        }
        for (int i = 0; i < n - 1; i++)
            for (int j = 0; j < crossSegs; j++)
            {
                int ll = i * cols + j, lr = i * cols + j + 1, nl = (i + 1) * cols + j, nr = (i + 1) * cols + j + 1;
                tris.Add(ll); tris.Add(nl); tris.Add(lr);
                tris.Add(lr); tris.Add(nl); tris.Add(nr);
            }
        var mesh = new Mesh { name = name };
        mesh.vertices = vertices; mesh.uv = uv; mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>挖后地面 y（沙床贴它）：在河道内取「水平河床→原地表」的过渡值，否则原地表。</summary>
    static float CarvedY(SceneContext ctx, float x, float z, List<(MapRouteEdge edge, Vector3[] world)> waters)
    {
        float origY = ctx.terrainSurface != null && ctx.terrainSurface.TrySampleTop(x, z, out float oy) ? oy : 0f;
        return TryCarveTargetY(ctx, x, z, origY, waters, out float ny) ? ny : origY;
    }

    // ===== 河道一致化基准：每条水路用「两端较低岸高」作恒定基准，水面/河床/挖深不再逐点跟地形爬 → 两河深浅一致、水坐进槽、水驿不悬浮 =====
    static Dictionary<MapRouteEdge, float> _riverBaseY;
    static List<(Vector2 c, float r, bool isPond)> _riverDryCores; // 水驿/码头核心圈：端点不收口让水到脚下；isPond=水驿(挖整圆塘) / 码头(只临河侧进水)
    static List<(Vector3[] path, float r)> _roadCauseways; // 连到水驿/码头的陆路走廊：路下保留实地堤、不挖 → 路避开水、不悬空

    static void BuildRiverBaseY(List<(MapRouteEdge edge, Vector3[] world)> waters, SceneContext ctx,
        Dictionary<string, MapStationAnchor> anchorById)
    {
        _riverBaseY = new Dictionary<MapRouteEdge, float>();
        _riverDryCores = new List<(Vector2, float, bool)>();
        if (waters == null) return;
        foreach (var (edge, world) in waters)
        {
            if (edge == null || world == null || world.Length == 0) continue;
            float a = SampleOrigTop(ctx, world[0].x, world[0].z, world[0].y);
            float b = SampleOrigTop(ctx, world[world.Length - 1].x, world[world.Length - 1].z, world[world.Length - 1].y);
            _riverBaseY[edge] = Mathf.Min(a, b);
            AddStationDryCore(edge.fromStationId, anchorById);
            AddStationDryCore(edge.toStationId, anchorById);
        }
    }

    /// <summary>建「陆路堤」走廊：连到水驿/码头的陆路，整条路径登记为不挖实地 → 路下永远有地、不悬空架在水上。
    /// 远离水的路段本就不挖、登记是空操作；只有路接近水驿那段才真正起作用（= 一条避开水的路堤/causeway）。</summary>
    static void BuildRoadCauseways(Dictionary<string, MapStationAnchor> anchorById)
    {
        _roadCauseways = new List<(Vector3[], float)>();
        if (anchorById == null) return;
        foreach (var edge in UnityEngine.Object.FindObjectsByType<MapRouteEdge>(FindObjectsSortMode.None))
        {
            if (edge == null || edge.routeType == MapRouteType.Water) continue;
            // 所有陆路都建窄路堤：纯陆地段是空操作（那儿本就不挖），只在「跨河」处真正起作用 →
            // 路踩在窄地堤上、河水从两边让开、不凭空架在河上。连水驿的端再由下面 trim 收到塘内一小截（短堤下水）。
            Vector3[] world = ExistingEdgeWorldPath(edge);
            if (world == null || world.Length < 2) continue;
            // 把贴近水驿/码头的那段路堤去掉：堤止于岸边、不盖到水驿脚下（水驿留在水里、不再造沙台）
            var keep = new List<Vector3>();
            foreach (var p in world)
            {
                bool nearWs = false;
                foreach (var kv in anchorById)
                {
                    var a = kv.Value;
                    if (!IsWaterStation(a)) continue;
                    if (edge.fromStationId != kv.Key && edge.toStationId != kv.Key) continue;
                    float gap = WaterStationPondRadius(a) + WaterStationRoadGap;
                    var ap = new Vector2(a.transform.position.x, a.transform.position.z);
                    if ((new Vector2(p.x, p.z) - ap).magnitude < gap) { nearWs = true; break; }
                }
                if (!nearWs) keep.Add(p);
            }
            if (keep.Count < 2) continue;
            _roadCauseways.Add((keep.ToArray(), RoadCausewayHalfWidth));
        }
    }

    static void AddStationDryCore(string id, Dictionary<string, MapStationAnchor> anchorById)
    {
        if (string.IsNullOrEmpty(id) || anchorById == null || !anchorById.TryGetValue(id, out var a) || a == null) return;
        if (!IsWaterStationOrDock(a)) return;
        _riverDryCores.Add((new Vector2(a.transform.position.x, a.transform.position.z), WaterStationPondRadius(a), IsWaterStation(a)));
    }

    /// <summary>水驿「水塘半径」：建筑/码头几何（排除标签/旗/占位/车辆点）包围盒 XZ 半宽 + StationWaterPad。
    /// 用半宽（不是到四角的距离）→ 塘只比建筑大一圈、贴身；四角略出水也接受。统一供「挖圆水塘 / 铺圆水面 / 退路堤」用。</summary>
    static float WaterStationPondRadius(MapStationAnchor a)
    {
        if (a == null) return 2.2f;
        Bounds bb = default; bool has = false;
        foreach (Transform ch in a.transform)
        {
            string n = ch.name;
            if (n.Contains("Name") || n.Contains("Label") || n.Contains("Flag") || n.Contains("Beacon")
                || n.Contains("Placeholder") || n.Contains("EventFX") || n.Contains("Obstacle") || n.Contains("VehicleStop")) continue;
            foreach (var mr in ch.GetComponentsInChildren<MeshRenderer>(true))
            { if (!has) { bb = mr.bounds; has = true; } else bb.Encapsulate(mr.bounds); }
        }
        return has ? Mathf.Max(bb.extents.x, bb.extents.z) + StationWaterPad : 2.2f;
    }

    /// <summary>站点干核内压低染砂权重：核心中央=0（硬岸/草，不再大沙坑），到干核边缘 0.9m 内渐升回 1
    /// （只在贴水线一圈留窄沙）。让临水驿站坐在规整硬岸上、只有薄沙滩，而非被一大片沙包住。</summary>
    static float StationSandDamp(float x, float z)
    {
        if (_riverDryCores == null || _riverDryCores.Count == 0) return 1f;
        var p = new Vector2(x, z);
        float mul = 1f;
        foreach (var (c, r, _isPond) in _riverDryCores)
        {
            float d = Vector2.Distance(p, c);
            // 核心(r-0.5 内)=0 硬岸无沙；向外渐隐到 r+StationSandFadeExtra 恢复满沙 → 站点周围沙三角收成规整小沙滩
            mul = Mathf.Min(mul, Mathf.SmoothStep(r - 0.5f, r + StationSandFadeExtra, d));
        }
        return mul;
    }

    static float SampleOrigTop(SceneContext ctx, float x, float z, float fallback)
    {
        return ctx.terrainSurface != null && ctx.terrainSurface.TrySampleTop(x, z, out float cy) ? cy : fallback;
    }

    // 取某采样点最近水路的恒定基准 y（找不到则交回原地表逻辑）
    static bool TryRiverBaseY(List<(MapRouteEdge edge, Vector3[] world)> waters, float x, float z, out float baseY, out float along)
    {
        baseY = 0f; along = 0f;
        if (_riverBaseY == null || waters == null) return false;
        float bestD = float.MaxValue; bool found = false;
        foreach (var (edge, w) in waters)
        {
            if (edge == null) continue;
            float d = NearestOnPolylineXZ(x, z, w, out float al, out _);
            if (d < bestD && _riverBaseY.TryGetValue(edge, out float by)) { bestD = d; baseY = by; along = al; found = true; }
        }
        return found;
    }

    /// <summary>河道下挖目标 y（≤ origY），U 形断面：
    ///   ① 中心 flatHalf 内 = **绝对水平平底**（bottom = 最近中心线原高 - 谷深×端点收口，横向恒定）；
    ///   ② flatHalf~CarveHalfWidth = U 形坡壁，从平底平滑升回原地表。
    /// 平底横向恒定 → 水平水面对称填满整个平底（根治「只有一半河面」）；坡壁 → 真正的 U 形河床而非平面贴色。</summary>
    static bool TryCarveTargetY(SceneContext ctx, float x, float z, float origY,
        List<(MapRouteEdge edge, Vector3[] world)> waters, out float newY)
    {
        newY = origY; bool any = false;
        // 陆路堤优先：连到水驿的陆路走廊内保留实地、不挖 → 路下有地、避开水、不悬空（优先于站点核心满深）
        if (_roadCauseways != null)
        {
            foreach (var (path, rr) in _roadCauseways)
                if (NearestOnPolylineXZ(x, z, path, out _, out _) < rr) return false;
        }
        // 水驿/码头核心：端点不收口让水到脚下；水驿(isPond)再额外挖整圆塘、码头只临河那侧进水
        bool inStationCore = false, inPondCore = false;
        if (_riverDryCores != null)
        {
            var p2 = new Vector2(x, z);
            foreach (var (c, r, isPond) in _riverDryCores)
                if ((p2 - c).sqrMagnitude < r * r) { inStationCore = true; if (isPond) inPondCore = true; }
        }
        foreach (var (edge, w) in waters)
        {
            float d = NearestOnPolylineXZ(x, z, w, out float along, out Vector2 nearest);
            if (d >= CarveHalfWidth) continue;
            float centerY = (_riverBaseY != null && edge != null && _riverBaseY.TryGetValue(edge, out float by))
                ? by
                : (ctx.terrainSurface != null && ctx.terrainSurface.TrySampleTop(nearest.x, nearest.y, out float cy) ? cy : origY);
            float endP = inStationCore ? 1f : RiverEndFade(along);   // 站点核心：满深、不收口 → 水进脚下
            float s = d / CarveHalfWidth;           // 0 中心 .. 1 岸
            const float flatFrac = 0.65f;            // 宽平底占比，其余是陡直壁 → 水填满大部、岸薄、河谷凹得明显
            float shape = s <= flatFrac ? 1f : Mathf.Clamp01(1f - (s - flatFrac) / (1f - flatFrac));
            float carved = centerY - CarveDepth * endP * shape;
            if (carved < newY) { newY = carved; any = true; }
        }
        if (inPondCore)
        {
            // 水驿核心整片挖成平底圆水塘（不止水道附近）→ 背水侧也坐进水里、整个水驿在水中（码头不走这里、只临河侧进水）
            float poolBase = origY, bestD = float.MaxValue;
            foreach (var (edge, w) in waters)
            {
                float dd = NearestOnPolylineXZ(x, z, w, out _, out _);
                if (dd < bestD && _riverBaseY != null && edge != null && _riverBaseY.TryGetValue(edge, out float by)) { bestD = dd; poolBase = by; }
            }
            float coreCarved = poolBase - CarveDepth;
            if (coreCarved < newY) { newY = coreCarved; any = true; }
        }
        return any;
    }

    /// <summary>河道砂染权重：到最近水路中心线 XZ 距离 → [内全砂 1, 外 0] 平滑。
    /// 内半径盖住挖槽+沙床(让沙床边坐在已染砂地形上、消除砂压草硬边)，外半径再羽化一截到岸 → 砂渐隐入草。</summary>
    static float RiverSandSplatWeight(float x, float z, List<(MapRouteEdge edge, Vector3[] world)> waters)
    {
        if (waters == null) return 0f;
        const float innerR = 1.6f;  // ≤此全砂（未挖到的顶点）；沙滩更宽
        const float outerR = 3.0f;  // ≥此全草；砂带更宽、向草地柔和过渡（边界不再太硬，沙多一点）
        float best = float.MaxValue;
        foreach (var (edge, w) in waters)
        {
            if (w == null || w.Length == 0) continue;
            float d = NearestOnPolylineXZ(x, z, w, out _, out _);
            if (d < best) best = d;
        }
        if (best >= outerR) return 0f;
        return 1f - Mathf.SmoothStep(0f, 1f, (best - innerR) / (outerR - innerR));
    }

    /// <summary>中心线某点的水面 y（统一水位）：岸顶基准 centerY 下沉 WaterDepthBelowBank，全线同高。
    /// 主水面/行船点/交汇补片共用此函数 → 一层水，坐进碗形河槽。</summary>
    static float WaterSurfaceYAt(SceneContext ctx, Vector3 centerPt, float along,
        List<(MapRouteEdge edge, Vector3[] world)> waters)
    {
        float centerY;
        if (TryRiverBaseY(waters, centerPt.x, centerPt.z, out float by, out float _))
            centerY = by;
        else
            centerY = ctx.terrainSurface != null && ctx.terrainSurface.TrySampleTop(centerPt.x, centerPt.z, out float cy) ? cy : centerPt.y;
        return centerY - WaterDepthBelowBank;
    }

    /// <summary>碗形河床下，水面在某点应有的半宽：水填到"河床=水面"的岸线处。
    /// 抛物线碗 bed=centerY-CarveDepth*endP*(1-s²)，令 bed=水面 → s=√(1-WaterDepthBelowBank/(CarveDepth*endP))，半宽=CarveHalfWidth*s。
    /// 端点太浅(深度&lt;水深)→返回0自然收口。这样水正好填到岸、不露河底、不溢出。</summary>
    static float WaterHalfWidthAt(SceneContext ctx, Vector3 centerPt,
        List<(MapRouteEdge edge, Vector3[] world)> waters)
    {
        float along = 0f;
        if (!TryRiverBaseY(waters, centerPt.x, centerPt.z, out float _, out along)) along = 0.5f;
        float depthHere = CarveDepth * RiverEndFade(along);
        if (depthHere <= WaterDepthBelowBank + 1e-4f) return 0f;
        float s = Mathf.Sqrt(Mathf.Clamp01(1f - WaterDepthBelowBank / depthHere));
        return CarveHalfWidth * s;
    }

    static float RiverEndFade(float along)
    {
        float start = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(along / Mathf.Max(0.001f, CarveEndTaperFrac)));
        float end = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((1f - along) / Mathf.Max(0.001f, CarveEndTaperFrac)));
        return start * end;
    }

    /// <summary>到折线 XZ 最近距离 + 归一化沿程位置 along∈[0,1] + 最近点 XZ。</summary>
    static float NearestOnPolylineXZ(float x, float z, Vector3[] pts, out float along, out Vector2 nearest)
    {
        along = 0f; nearest = new Vector2(x, z);
        if (pts == null || pts.Length == 0) return float.MaxValue;
        var p = new Vector2(x, z);
        if (pts.Length == 1) { nearest = new Vector2(pts[0].x, pts[0].z); return Vector2.Distance(p, nearest); }
        float total = 0f;
        for (int i = 0; i < pts.Length - 1; i++)
            total += Vector2.Distance(new Vector2(pts[i].x, pts[i].z), new Vector2(pts[i + 1].x, pts[i + 1].z));
        if (total < 1e-5f) total = 1f;
        float best = float.MaxValue, cum = 0f;
        for (int i = 0; i < pts.Length - 1; i++)
        {
            var a = new Vector2(pts[i].x, pts[i].z);
            var b = new Vector2(pts[i + 1].x, pts[i + 1].z);
            Vector2 ab = b - a;
            float seg = ab.magnitude;
            float len2 = ab.sqrMagnitude;
            float tt = len2 < 1e-8f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
            Vector2 proj = a + ab * tt;
            float dist = Vector2.Distance(p, proj);
            if (dist < best) { best = dist; along = (cum + tt * seg) / total; nearest = proj; }
            cum += seg;
        }
        return best;
    }

    static float NearestOnPolylineXZWithDirection(float x, float z, Vector3[] pts,
        out float along, out Vector2 nearest, out Vector2 direction)
    {
        along = 0f; nearest = new Vector2(x, z); direction = Vector2.right;
        if (pts == null || pts.Length == 0) return float.MaxValue;
        var p = new Vector2(x, z);
        if (pts.Length == 1)
        {
            nearest = new Vector2(pts[0].x, pts[0].z);
            return Vector2.Distance(p, nearest);
        }

        float total = 0f;
        for (int i = 0; i < pts.Length - 1; i++)
            total += Vector2.Distance(new Vector2(pts[i].x, pts[i].z), new Vector2(pts[i + 1].x, pts[i + 1].z));
        if (total < 1e-5f) total = 1f;

        float best = float.MaxValue, cum = 0f;
        for (int i = 0; i < pts.Length - 1; i++)
        {
            var a = new Vector2(pts[i].x, pts[i].z);
            var b = new Vector2(pts[i + 1].x, pts[i + 1].z);
            Vector2 ab = b - a;
            float seg = ab.magnitude;
            float len2 = ab.sqrMagnitude;
            float tt = len2 < 1e-8f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
            Vector2 proj = a + ab * tt;
            float dist = Vector2.Distance(p, proj);
            if (dist < best)
            {
                best = dist;
                along = (cum + tt * seg) / total;
                nearest = proj;
                direction = seg < 1e-5f ? direction : ab / seg;
            }
            cum += seg;
        }
        return best;
    }

    /// <summary>水面带：左右边与中心同 y（水平水面），不贴地形（已沉入挖好的河谷）。顶点存 target 局部空间。</summary>
    static Mesh CreateWaterRibbon(Vector3[] center, float width, string name, Transform target)
    {
        int n = center.Length;
        var vertices = new Vector3[n * 2];
        var uv = new Vector2[vertices.Length];
        var triangles = new int[(n - 1) * 6];
        float distance = 0f;
        for (int i = 0; i < n; i++)
        {
            Vector3 fwd = i == n - 1 ? center[i] - center[i - 1] : center[i + 1] - center[i];
            fwd.y = 0f;
            Vector3 side = Vector3.Cross(Vector3.up, fwd.sqrMagnitude < 1e-8f ? Vector3.forward : fwd.normalized);
            if (i > 0) distance += Vector3.Distance(center[i - 1], center[i]);
            Vector3 left = center[i] - side * width * 0.5f; left.y = center[i].y;
            Vector3 right = center[i] + side * width * 0.5f; right.y = center[i].y;
            vertices[i * 2] = target.InverseTransformPoint(left);
            vertices[i * 2 + 1] = target.InverseTransformPoint(right);
            uv[i * 2] = new Vector2(0f, distance * 0.42f);
            uv[i * 2 + 1] = new Vector2(1f, distance * 0.42f);
        }
        int ti = 0;
        for (int i = 0; i < n - 1; i++)
        {
            int v = i * 2;
            triangles[ti++] = v; triangles[ti++] = v + 2; triangles[ti++] = v + 1;
            triangles[ti++] = v + 1; triangles[ti++] = v + 2; triangles[ti++] = v + 3;
        }
        var mesh = new Mesh { name = name };
        mesh.vertices = vertices; mesh.uv = uv; mesh.triangles = triangles;
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        return mesh;
    }

    static MapRouteType ParseRouteType(string v) =>
        v == "WATER" ? MapRouteType.Water :
        v == "MOUNTAIN" ? MapRouteType.Mountain :
        v == "BRANCH" ? MapRouteType.Branch : MapRouteType.OfficialRoad;

    static float WidthFor(MapRouteType t) => t switch
    { MapRouteType.Water => WaterWidth, MapRouteType.Mountain => MountainWidth, MapRouteType.Branch => BranchWidth, _ => RoadWidth };

    static float LiftFor(MapRouteType t) => t switch
    { MapRouteType.Water => WaterLift, MapRouteType.Mountain => MountainLift, MapRouteType.Branch => BranchLift, _ => RoadLift };

    static float OverlayWidthFor(MapRouteType t) => t switch
    { MapRouteType.Water => WaterOverlayWidth, MapRouteType.Mountain => MountainOverlayWidth, MapRouteType.Branch => BranchOverlayWidth, _ => RoadOverlayWidth };

    static bool SamePath(LycheeRoutePath a, LycheeRoutePath b)
    {
        if (a?.points == null || b?.points == null || a.points.Count != b.points.Count) return false;
        for (int i = 0; i < a.points.Count; i++)
            if (Mathf.Abs(a.points[i].x - b.points[i].x) > CoordEps || Mathf.Abs(a.points[i].y - b.points[i].y) > CoordEps)
                return false;
        return true;
    }

    // ============================================================ 工具

    static LycheeUiMapConfig ReadConfig(string pathMaybeRelative) =>
        ReadConfigText(File.ReadAllText(ToAbsolute(pathMaybeRelative)));

    static LycheeUiMapConfig ReadConfigText(string json) =>
        JsonUtility.FromJson<LycheeUiMapConfig>(json);

    static void ValidateFlexibleConfig(LycheeUiMapConfig config, string rawJson, List<string> report)
    {
        var nodeIds = new HashSet<string>((config.nodes ?? new List<LycheeNodeInfo>())
            .Where(n => n != null && !string.IsNullOrEmpty(n.nodeId))
            .Select(n => n.nodeId));
        var edgeIds = new HashSet<string>((config.edges ?? new List<LycheeEdgeInfo>())
            .Where(e => e != null && !string.IsNullOrEmpty(e.edgeId))
            .Select(e => e.edgeId));
        var routePathEdgeIds = new HashSet<string>((config.routePaths ?? new List<LycheeRoutePath>())
            .Where(p => p != null && !string.IsNullOrEmpty(p.edgeId))
            .Select(p => p.edgeId));

        foreach (LycheeEdgeInfo edge in config.edges ?? new List<LycheeEdgeInfo>())
        {
            if (edge == null) continue;
            ValidateNodeRef("edge." + edge.edgeId + ".fromNodeId", edge.fromNodeId, nodeIds, report);
            ValidateNodeRef("edge." + edge.edgeId + ".toNodeId", edge.toNodeId, nodeIds, report);
            if (!routePathEdgeIds.Contains(edge.edgeId))
                report.Add($"[警告] {edge.edgeId}: edges 中存在，但 routePaths 缺少对应折线。");
        }
        foreach (string pathEdgeId in routePathEdgeIds)
            if (!edgeIds.Contains(pathEdgeId))
                report.Add($"[警告] routePaths.{pathEdgeId}: 有折线但 edges 中没有对应边。");

        LycheeGameplayConfig gameplay = config.gameplay;
        int roleRefCount = 0;
        int resources = gameplay?.resources?.Count ?? 0;
        int processNodes = gameplay?.processNodes?.Count ?? 0;
        int obstacleNodes = gameplay?.obstacleCandidateNodeIds?.Count ?? 0;
        if (gameplay != null)
        {
            LycheeGameplayRoles roles = gameplay.roles;
            if (roles != null)
            {
                roleRefCount += ValidateGameplayNode("gameplay.roles.startNodeId", roles.startNodeId, nodeIds, report);
                roleRefCount += ValidateGameplayNodeList("gameplay.roles.terminalNodeIds", roles.terminalNodeIds, nodeIds, report);
                roleRefCount += ValidateGameplayNode("gameplay.roles.gateNodeId", roles.gateNodeId, nodeIds, report);
                roleRefCount += ValidateGameplayNodeList("gameplay.roles.safeZoneNodeIds", roles.safeZoneNodeIds, nodeIds, report);
                roleRefCount += ValidateGameplayNode("gameplay.roles.reverifyNodeId", roles.reverifyNodeId, nodeIds, report);
                roleRefCount += ValidateGameplayNodeList("gameplay.roles.rushExcludedNodeIds", roles.rushExcludedNodeIds, nodeIds, report);
            }

            foreach (LycheeGameplayResource resource in gameplay.resources ?? new List<LycheeGameplayResource>())
                if (resource != null)
                    ValidateNodeRef("gameplay.resources." + resource.resourceType, resource.nodeId, nodeIds, report);
            foreach (LycheeGameplayProcessNode process in gameplay.processNodes ?? new List<LycheeGameplayProcessNode>())
                if (process != null)
                    ValidateNodeRef("gameplay.processNodes." + process.processType, process.nodeId, nodeIds, report);
            ValidateGameplayNodeList("gameplay.obstacleCandidateNodeIds", gameplay.obstacleCandidateNodeIds, nodeIds, report);
        }

        int taskGroups = ValidateTaskCandidates(rawJson, nodeIds, report);
        report.Add($"flex-config: gameplay.rolesRefs={roleRefCount} resources={resources} processNodes={processNodes} " +
                   $"taskCandidateGroups={taskGroups} obstacleCandidates={obstacleNodes}");
    }

    static int ValidateGameplayNode(string label, string nodeId, HashSet<string> nodeIds, List<string> report)
    {
        if (string.IsNullOrEmpty(nodeId)) return 0;
        ValidateNodeRef(label, nodeId, nodeIds, report);
        return 1;
    }

    static int ValidateGameplayNodeList(string label, List<string> ids, HashSet<string> nodeIds, List<string> report)
    {
        if (ids == null) return 0;
        foreach (string nodeId in ids) ValidateNodeRef(label, nodeId, nodeIds, report);
        return ids.Count;
    }

    static void ValidateNodeRef(string label, string nodeId, HashSet<string> nodeIds, List<string> report)
    {
        if (string.IsNullOrEmpty(nodeId)) return;
        if (!nodeIds.Contains(nodeId))
            report.Add($"[警告] {label} 引用不存在的 nodeId: {nodeId}");
    }

    static int ValidateTaskCandidates(string rawJson, HashSet<string> nodeIds, List<string> report)
    {
        string taskObject = ExtractJsonObject(rawJson, "taskCandidates");
        if (string.IsNullOrEmpty(taskObject)) return 0;

        int groups = 0;
        foreach (Match group in Regex.Matches(taskObject, "\"(?<task>[^\"]+)\"\\s*:\\s*\\[(?<items>[^\\]]*)\\]"))
        {
            groups++;
            string taskId = group.Groups["task"].Value;
            string items = group.Groups["items"].Value;
            foreach (Match item in Regex.Matches(items, "\"(?<node>[^\"]+)\""))
                ValidateNodeRef("gameplay.taskCandidates." + taskId, item.Groups["node"].Value, nodeIds, report);
        }
        return groups;
    }

    static string ExtractJsonObject(string json, string propertyName)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName)) return null;
        int propertyIndex = json.IndexOf("\"" + propertyName + "\"", StringComparison.Ordinal);
        if (propertyIndex < 0) return null;
        int start = json.IndexOf('{', propertyIndex);
        if (start < 0) return null;

        bool inString = false;
        bool escape = false;
        int depth = 0;
        for (int i = start; i < json.Length; i++)
        {
            char c = json[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return json.Substring(start, i - start + 1);
            }
        }
        return null;
    }

    static void ArchiveMapConfig(string sourceConfigPath, string mapKey, List<string> report)
    {
        if (string.IsNullOrEmpty(sourceConfigPath)) return;
        string sourceAbsolute = ToAbsolute(sourceConfigPath);
        if (!File.Exists(sourceAbsolute))
        {
            report.Add("[警告] 配置归档失败，源文件不存在: " + sourceConfigPath);
            return;
        }

        string safeKey = Sanitize(string.IsNullOrEmpty(mapKey) ? "map" : mapKey);
        CopyJsonAsset(sourceAbsolute, RuntimeConfigFolder + "/" + safeKey + "_config.json", report);
        CopyJsonAsset(sourceAbsolute, MapConfigArchiveFolder + "/" + MapOrdinalName(safeKey) + ".json", report);
    }

    static void CopyJsonAsset(string sourceAbsolute, string targetProjectPath, List<string> report)
    {
        string targetAbsolute = ToAbsolute(targetProjectPath);
        if (string.Equals(Path.GetFullPath(sourceAbsolute), Path.GetFullPath(targetAbsolute), StringComparison.OrdinalIgnoreCase))
        {
            report.Add("map_config 已在目标位置，无需重复归档: " + targetProjectPath);
            return;
        }

        EnsureFolderRecursive(Path.GetDirectoryName(targetProjectPath).Replace('\\', '/'));
        File.Copy(sourceAbsolute, targetAbsolute, true);
        AssetDatabase.ImportAsset(targetProjectPath, ImportAssetOptions.ForceSynchronousImport);
        report.Add("已归档 map_config: " + targetProjectPath);
    }

    static string MapOrdinalName(string mapKey)
    {
        Match match = Regex.Match(mapKey ?? string.Empty, "^map_?(\\d+)$", RegexOptions.IgnoreCase);
        return match.Success ? "map_" + match.Groups[1].Value : mapKey;
    }

    static Transform FindChild(Transform parent, string name)
    {
        foreach (Transform t in parent.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    static void DestroyChild(Transform parent, string name)
    {
        Transform child = FindChild(parent, name);
        if (child != null) UnityEngine.Object.DestroyImmediate(child.gameObject);
    }

    static Transform FindAnyByName(string name)
    {
        foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t.name == name) return t;
        return null;
    }

    static Transform EnsureSceneRoot(string name)
    {
        Transform existing = FindAnyByName(name);
        if (existing != null) return existing;
        var go = new GameObject(name);
        return go.transform;
    }

    static Mesh SaveMesh(Mesh mesh, string path)
    {
        if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null) AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(mesh, path);
        return AssetDatabase.LoadAssetAtPath<Mesh>(path);
    }

    static void RegisterInBuildSettings(string scenePath, List<string> report)
    {
        var scenes = EditorBuildSettings.scenes.ToList();
        if (scenes.Any(s => PathEquals(s.path, scenePath)))
        { report.Add("Build Settings 已含: " + scenePath); return; }
        scenes.Add(new EditorBuildSettingsScene(scenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();
        report.Add("已登记进 Build Settings: " + scenePath);
    }

    static string SuggestKey(string jsonAbsPath)
    {
        try
        {
            var c = ReadConfig(jsonAbsPath);
            string baseName = !string.IsNullOrEmpty(c?.designVersion) ? c.designVersion : c?.mapName;
            return Sanitize(string.IsNullOrEmpty(baseName) ? Path.GetFileNameWithoutExtension(jsonAbsPath) : baseName);
        }
        catch { return Sanitize(Path.GetFileNameWithoutExtension(jsonAbsPath)); }
    }

    static string OfficialSceneNameForMapIndex(int oneBasedIndex)
    {
        return oneBasedIndex == 1 ? OfficialMap1SceneName : "Map_map" + oneBasedIndex;
    }

    static string MapKeyFromSceneName(string sceneName)
    {
        if (string.Equals(sceneName, OfficialMap1SceneName, StringComparison.OrdinalIgnoreCase)) return "map1";
        if (sceneName.StartsWith("Map_", StringComparison.OrdinalIgnoreCase))
        {
            string suffix = sceneName.Substring("Map_".Length);
            if (!string.IsNullOrEmpty(suffix)) return Sanitize(suffix);
        }
        return Sanitize(sceneName);
    }

    static string ToAbsolute(string p) => Path.IsPathRooted(p) ? p : Path.Combine(ProjectRoot, p);

    static string ToProjectRelative(string p)
    {
        if (!Path.IsPathRooted(p)) return p.Replace('\\', '/');
        string full = Path.GetFullPath(p).Replace('\\', '/');
        string root = ProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : full;
    }

    static bool PathEquals(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return string.Equals(Path.GetFullPath(ToAbsolute(a)), Path.GetFullPath(ToAbsolute(b)), StringComparison.OrdinalIgnoreCase);
    }

    static void EnsureFolder(string projectRelative)
    {
        if (AssetDatabase.IsValidFolder(projectRelative)) return;
        string parent = Path.GetDirectoryName(projectRelative).Replace('\\', '/');
        string leaf = Path.GetFileName(projectRelative);
        if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }

    static void EnsureFolderRecursive(string projectRelative)
    {
        if (AssetDatabase.IsValidFolder(projectRelative)) return;
        Directory.CreateDirectory(ToAbsolute(projectRelative));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
    }

    static string Fmt(Vector3 v) => $"({v.x:F1},{v.y:F1},{v.z:F1})";

    static void Abort(List<string> report, string error)
    { report.Add("ERROR: " + error); WriteReport(report); Debug.LogError("[MapLayout] " + error); }

    static void WriteReport(List<string> lines)
    {
        try { File.WriteAllText(ReportPath, string.Join(Environment.NewLine, lines) + Environment.NewLine); }
        catch (Exception e) { Debug.LogWarning("[MapLayout] 写报告失败: " + e.Message); }
    }

    static string Sanitize(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string((value ?? "lychee_map").Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
