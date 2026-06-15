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
///   （默认 "Tournament/&lt;key&gt;.txt"），切场景即换回放，零运行时耦合；母版保持 "reply.txt" 不变。
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
    const float RoadWidth = 1.65f, WaterWidth = 3.2f, MountainWidth = 1.28f, BranchWidth = 1.4f;
    // 清障：物体「包围盒边缘」到新路/新河中心线小于此值即清（用包围盒能清掉树冠/大石悬到路上的）。
    const float DecoReachRoad = 1.4f, DecoReachWater = 3.4f;
    const float RoadLift = 0.026f, WaterLift = 0.038f, MountainLift = 0.032f, BranchLift = 0.028f;
    // 彩带宽度：实测现役 overlay 宽度（ROAD 0.34 / WATER 0.36 / MOUNTAIN 0.32 / BRANCH 0.30）——
    // 旧值 0.55 过宽导致"彩带和道路宽度不统一"，按实测统一。彩带贴在路面上方 OverlayLift。
    const float RoadOverlayWidth = 0.34f, WaterOverlayWidth = 0.36f, MountainOverlayWidth = 0.32f, BranchOverlayWidth = 0.30f;
    const float OverlayLift = 0.045f;    // 彩带高于路面，避免被压进路面/地表
    const float RoadTuckUnder = 0.35f;   // 路头在建筑足迹内降到驿站地面下这么多 → 建筑盖住路头（不留缺口/不盖楼）
    const float RoadUnderRiver = 0.12f;  // 路与河重叠处把路面降到河床（CarvedY）之下这么多 → 河床/水面盖住路（道路在最底层）
    const float AnchorLift = 0.018f;     // 锚点贴地抬升，与原生成器一致
    const float CoordEps = 0.001f;       // 网格坐标相等判定阈值
    // 水路质量约束：
    // - 新 WATER 必须先下挖地形，再铺窄水面；不能只把蓝色贴片盖在地表上。
    // - 克隆水路模板时不保留模板中心宽黄土 RiverBank，避免低质量“大黄贴片”。
    // - 旧 WATER 改成陆路时，旧河床要抬回并覆盖为陆地材质，不能让黄土河床残在路底。
    const float CarveHalfWidth = 3.0f;     // 河谷半宽：宽于水面，保证拉近看能看到凹进去的岸坡
    const float CarveDepth = 1.55f;        // 河谷中心相对原地表的下挖深度
    const float CarveEndTaperFrac = 0.14f; // 沿河两端收口到 0（端点是驿站，不能挖坑）
    const float WaterDepthFrac = 0.45f;    // （旧平面水面用）水面坐落在平底之上
    const float WaterDrapeWidth = 1.85f;   // 水面比河谷窄，露出两侧下挖岸坡
    const float WaterDrapeLift = 0.105f;   // 水面贴河床上方的薄水膜高度
    const float RiverBedWidth = 2.55f;     // 沙床只覆盖中心河槽，不再铺满一大片黄土
    const int RiverBedCrossSegs = 10;      // 沙床横向细分段数（贴合 U 形河槽）
    const float RiverBedTrimFrac = 0.1f;   // 沙床不铺到最末端（避开驿站），首尾各裁掉这么多比例
    const float WaterOverlayLift = 0.05f;  // 水面高光带高于水面
    const float RiverBankSlopeInnerOffset = 1.05f;
    const float RiverBankSlopeOuterOffset = 1.34f;
    const float RiverBankSlopeLift = 0.045f;
    const int RiverBankSlopeCrossSegs = 5;
    const float RiverBankTrimOffset = 2.55f;
    const float RiverBankTrimWidth = 0.45f;
    const float RiverBankTrimLift = 0.055f;
    const float FormerRiverRestoreHalfWidth = 3.2f;    // 旧水路改陆路时，回填旧河沟的影响半宽
    const float FormerRiverRestoreSampleOffset = 3.6f; // 采两侧岸高的偏移，略大于挖河半宽
    const float FormerRiverRestoreSettle = 0.04f;      // 回填后略低于两侧岸面，保留一点自然起伏
    const float RoadbedUnderlayLift = 0.008f;           // 新生成陆路的砂石路基，低于路肩/路面但高于地表
    const int RoadbedUnderlayCrossSegs = 4;

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

            Transform overlayRoot = FindAnyByName(OverlayRootName);
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
            report.Add($"地形采样器：三角形 {sceneCtx.terrainSurface.TriangleCount} 个 / 地形网格 {sceneCtx.terrainFilters.Count} 个（道路/彩带按真实地形面贴地）");
            BuildTypeTemplates(edgeById, newEdgeInfo, overlayRoot, sceneCtx, report);

            // 校验：config 用到的每个路由类型都得有可克隆模板，否则新增该类型边会被静默跳过。
            foreach (var t in config.edges.Select(e => ParseRouteType(e.routeType)).Distinct())
                if (!sceneCtx.edgeTemplate.ContainsKey(t))
                    report.Add($"[警告] 场景缺少 {t} 类型道路模板：该类型的新增边将无法克隆生成（会跳过）。");

            // 预算所有水路中心线：建路时让路在与河重叠处下沉到河床以下（道路最底 < 河床 < 水面/建筑在上）。
            sceneCtx.waterTuples = new List<(MapRouteEdge edge, Vector3[] world)>();
            foreach (var we in config.edges)
            {
                if (ParseRouteType(we.routeType) != MapRouteType.Water) continue;
                if (!newRoutePath.TryGetValue(we.edgeId, out var wp) || wp.points == null || wp.points.Count < 2) continue;
                sceneCtx.waterTuples.Add((null, BuildTerrainFollowingRoute(wp.points, WaterLift, sceneCtx)));
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
                    unchanged++; continue;
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
                    var fp = fa.transform.position; var tp = ta.transform.position;
                    sceneCtx.endA = new Vector2(fp.x, fp.z); sceneCtx.endB = new Vector2(tp.x, tp.z);
                    sceneCtx.endAY = fp.y - RoadTuckUnder; sceneCtx.endBY = tp.y - RoadTuckUnder;
                    sceneCtx.endRampA = BuildingRadius(fa) + 0.2f; sceneCtx.endRampB = BuildingRadius(ta) + 0.2f;
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

            // 6b. 新河道凹陷：下挖地形成河谷 + 沉水面（每图地形是独立副本，安全）。
            CarveRivers(waterEdges, sceneCtx, report);
            CleanupTransientRestoredTerrainAssets(sceneCtx, report);

            // 6c. 清障：清掉压在新路/新河上的植被、岩石（原装饰按旧路布的，会挡新路）。
            ClearDecorationAlongPaths(changedEdges, sceneCtx, report);

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
                player.streamingReplayPath = string.IsNullOrEmpty(replayRelative) ? "reply.txt" : replayRelative;
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
        // 所有水路中心线（建路前预算）：路在与河重叠处下沉到河床以下，实现"道路最底/河床/水面+建筑在上"的层级。
        public List<(MapRouteEdge edge, Vector3[] world)> waterTuples;
        public readonly Dictionary<MapRouteType, MapRouteEdge> edgeTemplate = new();
        public readonly Dictionary<MapRouteType, Transform> routeLayer = new();
        public readonly Dictionary<MapRouteType, Transform> overlayTemplate = new();
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
                // 新水路统一由 CarveRivers 生成：下挖地形 + 中心沙床 + 窄水面 + 两侧岸线。
                DestroyChild(edgeObject.transform, edgeId + "_RiverBank");
                DestroyChild(edgeObject.transform, edgeId + "_ShallowWater");
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

    static void BuildRoadbedUnderlay(GameObject edgeObject, string edgeId, MapRouteType type,
        Vector3[] world, SceneContext ctx)
    {
        if (edgeObject == null || world == null || world.Length < 2) return;
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
            case MapRouteType.Branch: return 2.65f;
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
        if (ctx.overlayRoot == null) return;

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
    static Mesh CreateRibbonMesh(Vector3[] center, float width, string name,
        SceneContext ctx, SurfaceSampler primary, float lift, Transform target)
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
            Vector3 left = center[i] - side * width * 0.5f;
            Vector3 right = center[i] + side * width * 0.5f;
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

    /// <summary>路与河重叠处的"路面上限 y"：在水路沙床范围内 = 河床(CarvedY) - RoadUnderRiver（路沉到河床下，
    /// 让河床/水面盖住路）；范围外 = +∞（不限制）。实现层级"道路最底 &lt; 河床 &lt; 水面/建筑"。</summary>
    static float RoadUnderRiverY(SceneContext ctx, float x, float z)
    {
        if (ctx.waterTuples == null || ctx.waterTuples.Count == 0) return float.MaxValue;
        float nearD = float.MaxValue;
        foreach (var (_, w) in ctx.waterTuples)
        {
            float d = NearestOnPolylineXZ(x, z, w, out _, out _);
            if (d < nearD) nearD = d;
        }
        if (nearD > RiverBedWidth * 0.5f + 0.6f) return float.MaxValue; // 不在河床范围
        return CarvedY(ctx, x, z, ctx.waterTuples) - RoadUnderRiver;
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
            if (!mf.gameObject.name.Contains("Terrain")) continue;
            s.AddMesh(mf);
            ctx.terrainFilters.Add(mf);
        }
        ctx.terrainSurface = s;
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

    // ============================================================ 挖河凹陷（新水路 = 与现役同质量的下沉河道）

    /// <summary>为新增/重建的水路下挖河谷 + 沉水面：①克隆地形网格副本并按到水路中心线的距离下挖顶点；
    /// ②同步下挖 MapElevationData.heightSamples；③把水面/高光/彩带/行船 pathPoints 沉到原地表下 WaterSurfaceDrop。
    /// 原地表高度取自挖河前建好的 ctx.terrainSurface（不受本次下挖影响），保证水面相对原岸的下沉量稳定。</summary>
    static void CarveRivers(List<(MapRouteEdge edge, Vector3[] world)> waters, SceneContext ctx, List<string> report)
    {
        if (waters == null || waters.Count == 0) return;
        report.Add("--- 挖河凹陷 ---");

        // 1. 克隆并下挖地形网格（per-map 副本，绝不动共享原 mesh；含河床子网格一并下挖）。
        foreach (var mf in ctx.terrainFilters)
        {
            if (mf == null || mf.sharedMesh == null) continue;
            Mesh src = mf.sharedMesh;
            Mesh clone = UnityEngine.Object.Instantiate(src);
            clone.name = GeneratedTerrainBaseName(src.name, ctx) + "_Carved_" + Sanitize(ctx.mapKey);
            Vector3[] verts = clone.vertices;
            Transform t = mf.transform;
            int carved = 0;
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 w = t.TransformPoint(verts[i]);
                if (TryCarveTargetY(ctx, w.x, w.z, w.y, waters, out float ny) && ny < w.y - 0.0001f)
                { w.y = ny; verts[i] = t.InverseTransformPoint(w); carved++; }
            }
            clone.vertices = verts;
            clone.RecalculateNormals();
            clone.RecalculateBounds();
            Mesh saved = SaveMesh(clone, ctx.meshDir + "/" + Sanitize(ctx.mapKey) + "_" + clone.name + ".asset");
            mf.sharedMesh = saved;
            var mc = mf.GetComponent<MeshCollider>();
            if (mc != null) mc.sharedMesh = saved;
            EditorUtility.SetDirty(mf);
            report.Add($"地形 '{mf.name}' 下挖顶点 {carved}/{verts.Length}");
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
                }
            elev.heightSamples = hs;
            EditorUtility.SetDirty(elev);
        }

        // 3. 每条水路：先铺河床沙石、再沉水面/高光/彩带/行船点。
        Material sandMat = FindSandMaterial(ctx);
        Material bankTrimMat = FindRiverBankTrimMaterial(ctx);
        Material bankSlopeMat = FindRiverBankSlopeMaterial(ctx);
        foreach (var (edge, world) in waters)
        {
            if (edge == null) continue;
            // 沙床（水下沙底 + 水线薄沙岸，U 形贴河床）。
            BuildRiverBed(edge, world, ctx, waters, sandMat, report);
            BuildRiverBankSlopes(edge, world, ctx, waters, bankSlopeMat, report);
            BuildRiverBankTrims(edge, world, ctx, waters, bankTrimMat, report);

            // 水面：只铺中心低槽，让两侧下挖岸坡和窄岸线可见，避免低质量蓝色大贴片。
            ReplaceCarvedWater(edge.gameObject, edge.edgeId + "_Route", world, WaterDrapeWidth, WaterDrapeLift, ctx, waters);
            ReplaceCarvedWater(edge.gameObject, edge.edgeId + "_WaterHighlight", world, WaterDrapeWidth * 0.22f,
                WaterDrapeLift + 0.045f, ctx, waters);

            // 彩带蓝线随水面铺。
            Transform line = FindOverlayLine(edge.edgeId, ctx.overlayRoot);
            if (line != null && line.GetComponent<MeshFilter>() != null)
            {
                var lf = line.GetComponent<MeshFilter>();
                Mesh lm = CreateCarvedGrid(world, OverlayWidthFor(MapRouteType.Water), 4, line.name, line, ctx, waters, WaterDrapeLift + 0.05f);
                lf.sharedMesh = SaveMesh(lm, ctx.meshDir + "/" + Sanitize(ctx.mapKey) + "_" + line.name + ".asset");
                EditorUtility.SetDirty(lf);
            }

            // 行船点贴中心线水面（车队/小队过河时坐船贴水）。
            if (edge.pathPoints != null)
                for (int i = 0; i < edge.pathPoints.Length && i < world.Length; i++)
                    if (edge.pathPoints[i] != null)
                        edge.pathPoints[i].position = new Vector3(world[i].x,
                            CarvedY(ctx, world[i].x, world[i].z, waters) + WaterDrapeLift, world[i].z);
            EditorUtility.SetDirty(edge);
            report.Add($"[挖河] {edge.edgeId} 下挖河谷 {world.Length} 点（谷深 {CarveDepth:F2}/谷半宽 {CarveHalfWidth:F1}/水宽 {WaterDrapeWidth:F1}/沙床 {RiverBedWidth:F1}）");
        }
    }

    /// <summary>把水面子物体换成"贴河床整面铺满"的网格（CarvedY + lift），横切坡面也能覆盖整条河道。</summary>
    static void ReplaceCarvedWater(GameObject edgeObject, string childName, Vector3[] world, float width, float lift,
        SceneContext ctx, List<(MapRouteEdge edge, Vector3[] world)> waters)
    {
        Transform child = FindChild(edgeObject.transform, childName);
        if (child == null) return;
        var mf = child.GetComponent<MeshFilter>();
        if (mf == null) return;
        Mesh mesh = CreateCarvedGrid(world, width, RiverBedCrossSegs, childName, child, ctx, waters, lift);
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

    /// <summary>沿挖好的河谷铺一张沙石网格（河床/沙岸），裁掉首尾避开端点驿站。子物体缺失则新建。</summary>
    static void BuildRiverBed(MapRouteEdge edge, Vector3[] world, SceneContext ctx,
        List<(MapRouteEdge edge, Vector3[] world)> waters, Material sandMat, List<string> report)
    {
        Vector3[] mid = TrimEnds(world, RiverBedTrimFrac);
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
        Mesh mesh = CreateCarvedGrid(mid, RiverBedWidth, RiverBedCrossSegs, name, bed, ctx, waters, 0.04f);
        mf.sharedMesh = SaveMesh(mesh, ctx.meshDir + "/" + Sanitize(ctx.mapKey) + "_" + name + ".asset");
        EditorUtility.SetDirty(mf);
        if (mr != null) EditorUtility.SetDirty(mr);
        report.Add($"[河床] {edge.edgeId} 沙石网格 {mid.Length}x{RiverBedCrossSegs + 1} mat={(sandMat ? sandMat.name : "<无>")}");
    }

    static void BuildRiverBankSlopes(MapRouteEdge edge, Vector3[] world, SceneContext ctx,
        List<(MapRouteEdge edge, Vector3[] world)> waters, Material bankMat, List<string> report)
    {
        Vector3[] mid = TrimEnds(world, RiverBedTrimFrac);
        if (mid.Length < 2) return;

        BuildRiverBankSlope(edge, mid, ctx, waters, bankMat, -1, "L");
        BuildRiverBankSlope(edge, mid, ctx, waters, bankMat, 1, "R");
        report.Add($"[河岸坡] {edge.edgeId} 两侧坡岸 inner={RiverBankSlopeInnerOffset:F2} outer={RiverBankSlopeOuterOffset:F2} mat={(bankMat ? bankMat.name : "<无>")}");
    }

    static void BuildRiverBankSlope(MapRouteEdge edge, Vector3[] center, SceneContext ctx,
        List<(MapRouteEdge edge, Vector3[] world)> waters, Material bankMat, int sideSign, string sideName)
    {
        string name = edge.edgeId + "_" + sideName + "_BankSlope";
        Transform slope = FindChild(edge.transform, name);
        if (slope == null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(edge.transform, false);
            go.AddComponent<MeshFilter>();
            go.AddComponent<MeshRenderer>();
            slope = go.transform;
        }

        MeshFilter mf = slope.GetComponent<MeshFilter>();
        MeshRenderer mr = slope.GetComponent<MeshRenderer>();
        if (bankMat != null && mr != null) mr.sharedMaterial = bankMat;

        float centerOffset = sideSign * (RiverBankSlopeInnerOffset + RiverBankSlopeOuterOffset) * 0.5f;
        float width = RiverBankSlopeOuterOffset - RiverBankSlopeInnerOffset;
        Mesh mesh = CreateOffsetCarvedRibbon(center, centerOffset, width, RiverBankSlopeCrossSegs,
            name, slope, ctx, waters, RiverBankSlopeLift);
        mf.sharedMesh = SaveMesh(mesh, ctx.meshDir + "/" + Sanitize(ctx.mapKey) + "_" + name + ".asset");
        EditorUtility.SetDirty(mf);
        if (mr != null) EditorUtility.SetDirty(mr);
    }

    static void BuildRiverBankTrims(MapRouteEdge edge, Vector3[] world, SceneContext ctx,
        List<(MapRouteEdge edge, Vector3[] world)> waters, Material bankMat, List<string> report)
    {
        Vector3[] mid = TrimEnds(world, RiverBedTrimFrac);
        if (mid.Length < 2) return;

        BuildRiverBankTrim(edge, mid, ctx, waters, bankMat, -1, "L");
        BuildRiverBankTrim(edge, mid, ctx, waters, bankMat, 1, "R");
        report.Add($"[河岸] {edge.edgeId} 两侧窄岸线 width={RiverBankTrimWidth:F2} offset={RiverBankTrimOffset:F2} mat={(bankMat ? bankMat.name : "<无>")}");
    }

    static void BuildRiverBankTrim(MapRouteEdge edge, Vector3[] center, SceneContext ctx,
        List<(MapRouteEdge edge, Vector3[] world)> waters, Material bankMat, int sideSign, string sideName)
    {
        string name = edge.edgeId + "_" + sideName + "_BankTrim";
        Transform trim = FindChild(edge.transform, name);
        if (trim == null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(edge.transform, false);
            go.AddComponent<MeshFilter>();
            go.AddComponent<MeshRenderer>();
            trim = go.transform;
        }

        MeshFilter mf = trim.GetComponent<MeshFilter>();
        MeshRenderer mr = trim.GetComponent<MeshRenderer>();
        if (bankMat != null && mr != null) mr.sharedMaterial = bankMat;

        Mesh mesh = CreateOffsetCarvedRibbon(center, RiverBankTrimOffset * sideSign, RiverBankTrimWidth,
            2, name, trim, ctx, waters, RiverBankTrimLift);
        mf.sharedMesh = SaveMesh(mesh, ctx.meshDir + "/" + Sanitize(ctx.mapKey) + "_" + name + ".asset");
        EditorUtility.SetDirty(mf);
        if (mr != null) EditorUtility.SetDirty(mr);
    }

    static Vector3[] TrimEnds(Vector3[] pts, float frac)
    {
        if (pts.Length < 4) return pts;
        int cut = Mathf.Clamp(Mathf.RoundToInt(pts.Length * frac), 1, pts.Length / 2 - 1);
        var r = new List<Vector3>();
        for (int i = cut; i <= pts.Length - 1 - cut; i++) r.Add(pts[i]);
        return r.ToArray();
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
        float fr = BuildingRadius(from) + 0.4f;
        float tr = BuildingRadius(to) + 0.4f;
        var r = new List<Vector3>();
        foreach (var p in world)
        {
            var pp = new Vector2(p.x, p.z);
            if (Vector2.Distance(pp, fp) <= fr || Vector2.Distance(pp, tp) <= tr) continue;
            r.Add(p);
        }
        return r.Count >= 2 ? r.ToArray() : world;
    }

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
        string[] preferred =
        {
            "Assets/LycheeMap15/Materials/RiverBed_Mud.mat",
            "Assets/LycheeMap15/Materials/M_RiverBed_SandGravel.mat",
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

    static Material FindRiverBankSlopeMaterial(SceneContext ctx)
    {
        string[] preferred =
        {
            "Assets/LycheeMap15/Materials/RiverBed_Mud.mat",
            "Assets/LycheeMap15/Materials/M_RiverBank_Refined.mat",
            "Assets/LycheeMap15/Materials/M_Config_MountainRoute_Border.mat",
            "Assets/LycheeMap15/Materials/M_RiverBed_SandGravel.mat"
        };
        foreach (string p in preferred)
        {
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (mat != null) return mat;
        }
        return FindRiverBankTrimMaterial(ctx);
    }

    static Material FindRiverBankTrimMaterial(SceneContext ctx)
    {
        string[] preferred =
        {
            "Assets/LycheeMap15/Materials/M_RiverBankTrim_20260611Y.mat",
            "Assets/LycheeMap15/Materials/M_RiverBankTrim_20260611X.mat",
            "Assets/LycheeMap15/Materials/M_RiverBankTrim_20260611W.mat",
            "Assets/LycheeMap15/Materials/M_RiverBank_Refined.mat"
        };
        foreach (string p in preferred)
        {
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (mat != null) return mat;
        }

        foreach (var mf in ctx.terrainFilters)
        {
            MeshRenderer mr = mf != null ? mf.GetComponent<MeshRenderer>() : null;
            if (mr == null) continue;
            foreach (Material m in mr.sharedMaterials)
                if (m != null && (m.name.Contains("RiverBank") || m.name.Contains("BankTrim"))) return m;
        }
        return FindSandMaterial(ctx);
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

    /// <summary>沿挖好的河谷铺一张网格：沿路 n 点 × 横向 (crossSegs+1) 列，每顶点贴挖后河谷面 + lift。
    /// 横向细分让沙床贴合 V 形河谷底/坡，不再是悬空平板。顶点存 target 局部空间。</summary>
    static Mesh CreateCarvedGrid(Vector3[] center, float width, int crossSegs, string name, Transform target,
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
            for (int j = 0; j < cols; j++)
            {
                float lat = -width * 0.5f + width * (j / (float)crossSegs);
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

    static Mesh CreateOffsetCarvedRibbon(Vector3[] center, float offset, float width, int crossSegs, string name,
        Transform target, SceneContext ctx, List<(MapRouteEdge edge, Vector3[] world)> waters, float lift)
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
            for (int j = 0; j < cols; j++)
            {
                float lat = offset - width * 0.5f + width * (j / (float)crossSegs);
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
            for (int j = 0; j < cols; j++)
            {
                float lat = -width * 0.5f + width * (j / (float)crossSegs);
                Vector3 wpt = center[i] + side * lat;
                wpt.y = ConformY(ctx, ctx.terrainSurface, wpt.x, wpt.z, lift, center[i].y);
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

    /// <summary>河道下挖目标 y（≤ origY），U 形断面：
    ///   ① 中心 flatHalf 内 = **绝对水平平底**（bottom = 最近中心线原高 - 谷深×端点收口，横向恒定）；
    ///   ② flatHalf~CarveHalfWidth = U 形坡壁，从平底平滑升回原地表。
    /// 平底横向恒定 → 水平水面对称填满整个平底（根治「只有一半河面」）；坡壁 → 真正的 U 形河床而非平面贴色。</summary>
    static bool TryCarveTargetY(SceneContext ctx, float x, float z, float origY,
        List<(MapRouteEdge edge, Vector3[] world)> waters, out float newY)
    {
        newY = origY; bool any = false;
        float flatHalf = WaterDrapeWidth * 0.5f + 0.08f; // 平底≈窄水面宽，岸坡留给沙床/岸线表现下挖感
        foreach (var (_, w) in waters)
        {
            float d = NearestOnPolylineXZ(x, z, w, out float along, out Vector2 nearest);
            if (d >= CarveHalfWidth) continue;
            float centerY = ctx.terrainSurface != null && ctx.terrainSurface.TrySampleTop(nearest.x, nearest.y, out float cy) ? cy : origY;
            float endP = RiverEndFade(along);
            float bottom = centerY - CarveDepth * endP;
            float carved;
            if (d <= flatHalf) carved = bottom; // 水平平底
            else
            {
                float wallT = Mathf.SmoothStep(0f, 1f, (d - flatHalf) / Mathf.Max(0.01f, CarveHalfWidth - flatHalf));
                carved = Mathf.Lerp(bottom, origY, wallT); // U 形坡壁
            }
            if (carved < newY) { newY = carved; any = true; }
        }
        return any;
    }

    /// <summary>中心线某点的水面 y：该点原高 - 谷深×端点收口×WaterDepthFrac（水在水平河床之上）。</summary>
    static float WaterSurfaceYAt(SceneContext ctx, Vector3 centerPt, float along,
        List<(MapRouteEdge edge, Vector3[] world)> waters)
    {
        float centerY = ctx.terrainSurface != null && ctx.terrainSurface.TrySampleTop(centerPt.x, centerPt.z, out float cy) ? cy : centerPt.y;
        float endP = RiverEndFade(along);
        return centerY - CarveDepth * endP * WaterDepthFrac;
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
