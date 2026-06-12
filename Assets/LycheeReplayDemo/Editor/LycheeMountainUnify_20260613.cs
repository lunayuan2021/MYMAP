using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 一次性山体风格统一（20260613）：地形已换写实四层 splat、散布树已 100% 统一 URP Tree Pack，
/// 剩下的"风格打架"按侦察结论处理（侦察推荐候选 C，主体替换 0611 已完成）：
/// 1) 39 个已禁用的 PP_Forest_Mountain_Moss_01/02 + 整组禁用的 Mountain_Detail_Upgrade_20260610
///    （14 个 PP_Rock_Pile_Forest_Moss）只是场景垃圾，不渲染——落报告后删除（回滚靠 git 场景文件）。
/// 2) 真正现役的低模残余是 157 个激活的 PP_Rock_Moss_Grown_09/11（调色板平涂低模岩）——
///    原位替换为写实 rock_set_01~04：位置不变、Y 旋转保留、缩放按原渲染包围盒足印/高度换算（钳制），
///    底部贴原包围盒底；若原底悬浮于现役地表之上则压回地表（地形 0613 抬升过，旧落位高度不可信）。
/// 3) 12 个 0611 摆好但禁用的 rock_set 细节直接激活，并按现役地表校 Y（只校 Y，位置/缩放不动）。
/// 4) moss 山的树是烘死在网格里的，删山等于删树——按侦察配方在 36 座现役 mountain_terrain_02 的
///    缓坡面（法线与 up 夹角 &lt;35°、山体高度 20%~80% 带）补种 URP_Tree_1/2/3，每山 2~4 棵，
///    位置哈希确定性散布（禁无种子 Random，可复现）；树缩放 0.30~0.45 哈希轮换并钳制
///    "树高 ≤ 山高 0.8 倍"——场景现役 483 棵树 scale 0.30~0.62，照搬 0.7~1.1 会出现树比山高。
/// 防御路径：若磁盘场景里仍有"激活的" moss 山（与侦察不符时），原位替换 mountain_terrain_02 并补 3~6 棵树。
/// 新对象统一挂 Mountains_Unified_20260613；失败路径不 SaveScene；模式照搬 LycheeStationLift_20260613。
/// </summary>
[InitializeOnLoad]
static class LycheeMountainUnify_20260613
{
    const string ScenePath = "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity";
    const string DoneFileName = ".lychee_mountain_unify_20260613.done";
    const string ReportFileName = "LycheeMountainUnify_20260613_report.txt";
    const string UnifiedRootName = "Mountains_Unified_20260613";
    const string DetailGroupName = "Mountain_Detail_Upgrade_20260610";

    // prefab GUID 全部经 .meta 反查核实（侦察报告 + 本机 grep 双重确认）
    const string GuidMoss01 = "512b72c73a22e904d87662ebca8eb1e1";
    const string GuidMoss02 = "034023ff02a37fc47aab6bc6c49bf15b";
    const string GuidMountain = "fc355525a1ddd9f439206b1fa61517fd";
    const string GuidRockMoss09 = "24457fdf8e66c3c49b7599931e0fdcb2";
    const string GuidRockMoss11 = "a524090786e7ba545bd5ce3d24c9a3fb";
    static readonly string[] RockSetGuids =
    {
        "c67ed6360e72a9848a4bc876f41e5c51", // rock_set_01
        "11ef6c955fb624a4aaf3b18597db22df", // rock_set_02
        "a5344b98eb8749b48bd7eff8a8c3adae", // rock_set_03
        "32a25aaeb263423438ea72cc904f7910", // rock_set_04
    };
    static readonly string[] TreeGuids =
    {
        "022485ff60e4a26418217d8ab778a812", // URP_Tree_1
        "40cc2dd6b744b344a9a4ea56076c47fb", // URP_Tree_2
        "af90fe2f0f252bf4b8c2ab79bcd41edf", // URP_Tree_3
    };

    const float RockEmbed = 0.08f;        // 替换岩石压回地表时的嵌入深度（防裙边悬空）
    const float DetailEmbed = 0.10f;      // 激活 rock_set 细节校 Y 的嵌入深度
    const float DetailMoveCap = 2.5f;     // 校 Y 最大位移，防采到异常面飞天/入地
    const float TreeSink = 0.04f;         // 树 pivot 在包围盒底，轻微下沉防悬根
    const float SlopeMaxAngle = 35f;      // 缓坡判定：三角形法线与 up 夹角上限
    const float TreeBandMin = 0.20f;      // 种树高度带：山体包围盒高度的 20%~80%
    const float TreeBandMax = 0.80f;
    const float TreeMinScale = 0.30f;     // 与场景现役树 0.30~0.62 同档，不照搬 0.7~1.1
    const float TreeMaxScale = 0.45f;
    const float TreeScaleFloor = 0.22f;   // 钳完比这还小说明山太矮，宁缺毋滥
    const float TreeHeightRatioCap = 0.8f; // 树高 ≤ 山高 × 0.8，防"树比山高"
    const float TreeSpacing = 1.5f;       // 同山树间最小 XZ 距离

    static double nextPollTime;

    static LycheeMountainUnify_20260613()
    {
        EditorApplication.update += Poll;
    }

    static string ProjectRoot
    {
        get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..")); }
    }

    static string DonePath { get { return Path.Combine(ProjectRoot, DoneFileName); } }
    static string ReportPath { get { return Path.Combine(ProjectRoot, ReportFileName); } }

    static void Poll()
    {
        if (EditorApplication.timeSinceStartup < nextPollTime) return;
        nextPollTime = EditorApplication.timeSinceStartup + 2.0;
        try
        {
            if (File.Exists(DonePath)) return;
        }
        catch (Exception)
        {
            return;
        }
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying ||
            EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            return;
        }
        Run();
    }

    // ===== 地表采样（与 LycheeStationLift_20260613 同源）：现役 RoadGroundFit + 地形网格的最高面 =====
    class GroundSurface
    {
        public readonly List<Vector3> a = new List<Vector3>();
        public readonly List<Vector3> b = new List<Vector3>();
        public readonly List<Vector3> c = new List<Vector3>();
        public readonly List<Vector4> bboxXZ = new List<Vector4>();

        public void AddMesh(MeshFilter filter)
        {
            Mesh mesh = filter.sharedMesh;
            Vector3[] verts = mesh.vertices;
            int[] tris = mesh.triangles;
            Transform t = filter.transform;
            for (int i = 0; i < tris.Length; i += 3)
            {
                Vector3 wa = t.TransformPoint(verts[tris[i]]);
                Vector3 wb = t.TransformPoint(verts[tris[i + 1]]);
                Vector3 wc = t.TransformPoint(verts[tris[i + 2]]);
                a.Add(wa); b.Add(wb); c.Add(wc);
                bboxXZ.Add(new Vector4(
                    Mathf.Min(wa.x, wb.x, wc.x), Mathf.Min(wa.z, wb.z, wc.z),
                    Mathf.Max(wa.x, wb.x, wc.x), Mathf.Max(wa.z, wb.z, wc.z)));
            }
        }

        public bool TrySampleTop(float x, float z, out float y)
        {
            y = float.MinValue;
            bool hit = false;
            for (int i = 0; i < a.Count; i++)
            {
                Vector4 box = bboxXZ[i];
                if (x < box.x || z < box.y || x > box.z || z > box.w) continue;
                Vector3 pa = a[i], pb = b[i], pc = c[i];
                float d = (pb.z - pc.z) * (pa.x - pc.x) + (pc.x - pb.x) * (pa.z - pc.z);
                if (Mathf.Abs(d) < 1e-6f) continue;
                float w1 = ((pb.z - pc.z) * (x - pc.x) + (pc.x - pb.x) * (z - pc.z)) / d;
                float w2 = ((pc.z - pa.z) * (x - pc.x) + (pa.x - pc.x) * (z - pc.z)) / d;
                float w3 = 1f - w1 - w2;
                const float eps = -0.001f;
                if (w1 < eps || w2 < eps || w3 < eps) continue;
                float sampled = w1 * pa.y + w2 * pb.y + w3 * pc.y;
                if (sampled > y) { y = sampled; hit = true; }
            }
            return hit;
        }
    }

    // ===== 确定性哈希（位置量化到厘米 + 盐），全脚本禁无种子 Random，保证可复跑出同一结果 =====
    static float Hash01(int seed)
    {
        unchecked
        {
            uint x = (uint)seed;
            x ^= x >> 16; x *= 0x7feb352du;
            x ^= x >> 15; x *= 0x846ca68bu;
            x ^= x >> 16;
            return (x & 0xFFFFFFu) / 16777216f;
        }
    }

    static int PosSeed(Vector3 p, int salt)
    {
        unchecked
        {
            return Mathf.RoundToInt(p.x * 100f) * 73856093
                 ^ Mathf.RoundToInt(p.y * 100f) * 19349663
                 ^ Mathf.RoundToInt(p.z * 100f) * 83492791
                 ^ salt * 974593;
        }
    }

    static bool TryGetWorldBounds(GameObject go, out Bounds bounds)
    {
        bounds = new Bounds();
        bool any = false;
        foreach (Renderer r in go.GetComponentsInChildren<Renderer>())
        {
            if (!any) { bounds = r.bounds; any = true; }
            else bounds.Encapsulate(r.bounds);
        }
        return any;
    }

    static GameObject LoadPrefab(string guid)
    {
        string path = AssetDatabase.GUIDToAssetPath(guid);
        if (string.IsNullOrEmpty(path)) return null;
        return AssetDatabase.LoadAssetAtPath<GameObject>(path);
    }

    // 量 prefab 的自然尺寸：临时实例化（远离地图）取渲染包围盒，量完即删，不污染场景
    static Vector3 MeasurePrefabSize(GameObject prefab)
    {
        GameObject temp = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        temp.transform.position = new Vector3(0f, -500f, 0f);
        Bounds b;
        bool ok = TryGetWorldBounds(temp, out b);
        UnityEngine.Object.DestroyImmediate(temp);
        return ok ? b.size : Vector3.one;
    }

    // 在包围盒中心 + 四角（0.6 倍 extents）采现役地表最高面：单点会在坡地低估地表
    static bool SampleGroundAround(GroundSurface ground, Bounds b, out float groundY)
    {
        groundY = float.MinValue;
        bool any = false;
        float cx = b.center.x, cz = b.center.z;
        float ex = Mathf.Max(0.01f, b.extents.x * 0.6f);
        float ez = Mathf.Max(0.01f, b.extents.z * 0.6f);
        float[] sx = { cx, cx - ex, cx + ex, cx - ex, cx + ex };
        float[] sz = { cz, cz - ez, cz - ez, cz + ez, cz + ez };
        for (int s = 0; s < sx.Length; s++)
        {
            float y;
            if (ground.TrySampleTop(sx[s], sz[s], out y) && y > groundY)
            {
                groundY = y;
                any = true;
            }
        }
        return any;
    }

    // 原位替换：位置不变、Y 旋转保留、缩放按原包围盒足印/高度换算（钳制）、底贴原包围盒底，
    // 但原底若悬浮于现役地表之上则压回地表嵌入——0613 地形抬升过，旧落位高度不可全信
    static GameObject ReplaceInPlace(GameObject original, Bounds origBounds, GameObject prefab,
        Vector3 prefabSize, Transform parent, GroundSurface ground, float embed, out Vector3 appliedScale)
    {
        float yaw = original.transform.eulerAngles.y;
        Vector3 origPos = original.transform.position;
        float footOrig = Mathf.Max(origBounds.size.x, origBounds.size.z);
        float footNew = Mathf.Max(prefabSize.x, prefabSize.z);
        float sXZ = footNew > 1e-4f ? footOrig / footNew : 1f;
        float sY = prefabSize.y > 1e-4f ? origBounds.size.y / prefabSize.y : sXZ;
        sXZ = Mathf.Clamp(sXZ, 0.05f, 2.5f);
        sY = Mathf.Clamp(sY, sXZ * 0.6f, sXZ * 1.6f); // 防极端拉伸破坏写实贴图比例
        sY = Mathf.Clamp(sY, 0.05f, 2.5f);

        GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        inst.transform.SetParent(parent, false);
        inst.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        inst.transform.localScale = new Vector3(sXZ, sY, sXZ);
        inst.transform.position = origPos;
        appliedScale = inst.transform.localScale;

        float groundY;
        bool hit = SampleGroundAround(ground, origBounds, out groundY);
        float targetBottom = hit ? Mathf.Min(origBounds.min.y, groundY - embed) : origBounds.min.y;
        Bounds nb;
        if (TryGetWorldBounds(inst, out nb))
        {
            inst.transform.position += Vector3.up * (targetBottom - nb.min.y);
        }
        return inst;
    }

    // 山腰补树：采样山体网格三角形（世界空间法线坡度 <35°、高度带 20%~80%），位置哈希确定性挑点
    static int PlantSlopeTrees(GameObject mountain, GameObject[] treePrefabs, float[] treeHeights,
        Transform parent, int minCount, int maxCount, List<string> report)
    {
        Bounds mb;
        if (!TryGetWorldBounds(mountain, out mb)) return 0;
        float mh = mb.size.y;
        if (mh < 0.5f) return 0;

        var spots = new List<Vector3>();
        foreach (MeshFilter mf in mountain.GetComponentsInChildren<MeshFilter>())
        {
            Mesh mesh = mf.sharedMesh;
            if (mesh == null) continue;
            Vector3[] verts = mesh.vertices;
            int[] tris = mesh.triangles;
            Transform t = mf.transform;
            for (int i = 0; i < tris.Length; i += 3)
            {
                Vector3 wa = t.TransformPoint(verts[tris[i]]);
                Vector3 wb = t.TransformPoint(verts[tris[i + 1]]);
                Vector3 wc = t.TransformPoint(verts[tris[i + 2]]);
                Vector3 n = Vector3.Cross(wb - wa, wc - wa); // 非均匀缩放下必须用世界顶点重算法线
                if (n.sqrMagnitude < 1e-10f) continue;
                if (Vector3.Angle(n.normalized, Vector3.up) > SlopeMaxAngle) continue;
                Vector3 centroid = (wa + wb + wc) / 3f;
                float rel = (centroid.y - mb.min.y) / mh;
                if (rel < TreeBandMin || rel > TreeBandMax) continue;
                spots.Add(centroid);
            }
        }
        if (spots.Count == 0)
        {
            report.Add("  " + mountain.name + ": 无合格缓坡三角形，跳过补树");
            return 0;
        }

        int seed = PosSeed(mountain.transform.position, 901);
        int count = minCount + (int)(Hash01(seed) * (maxCount - minCount + 1));
        count = Mathf.Clamp(count, minCount, maxCount);
        var placed = new List<Vector3>();
        int plantedHere = 0;
        for (int k = 0; k < count; k++)
        {
            Vector3 pos = Vector3.zero;
            bool found = false;
            for (int attempt = 0; attempt < 10 && !found; attempt++)
            {
                int idx = Mathf.Min(spots.Count - 1,
                    (int)(Hash01(seed + k * 17 + attempt * 131 + 7) * spots.Count));
                Vector3 cand = spots[idx];
                bool clash = false;
                foreach (Vector3 p in placed)
                {
                    float dx = p.x - cand.x, dz = p.z - cand.z;
                    if (dx * dx + dz * dz < TreeSpacing * TreeSpacing) { clash = true; break; }
                }
                if (!clash) { pos = cand; found = true; }
            }
            if (!found) continue;

            int pi = Mathf.Min(treePrefabs.Length - 1,
                (int)(Hash01(seed + k * 29 + 3) * treePrefabs.Length));
            float scale = TreeMinScale + (TreeMaxScale - TreeMinScale) * Hash01(seed + k * 41 + 11);
            float cap = TreeHeightRatioCap * mh / Mathf.Max(0.1f, treeHeights[pi]);
            scale = Mathf.Min(scale, cap);
            if (scale < TreeScaleFloor) continue; // 山太矮，树再缩就失真

            GameObject tree = (GameObject)PrefabUtility.InstantiatePrefab(treePrefabs[pi]);
            tree.transform.SetParent(parent, false);
            tree.transform.position = pos - Vector3.up * TreeSink;
            tree.transform.rotation = Quaternion.Euler(0f, 360f * Hash01(seed + k * 53 + 5), 0f);
            tree.transform.localScale = Vector3.one * scale;
            tree.name = treePrefabs[pi].name + "_" + mountain.name + "_" + k;
            placed.Add(pos);
            plantedHere++;
        }
        return plantedHere;
    }

    static string Fmt(Vector3 v)
    {
        return string.Format("({0:F2},{1:F2},{2:F2})", v.x, v.y, v.z);
    }

    static void Run()
    {
        var report = new List<string>();
        report.Add("LycheeMountainUnify_20260613 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        report.Add("方案：侦察候选 C——清剿低模残余（moss 山垃圾删除 + 157 低模岩换写实 rock_set）" +
                   " + 激活 12 个写实细节岩 + 36 座现役写实山坡面补 URP 树");
        try
        {
            Scene active = SceneManager.GetActiveScene();
            if (!string.Equals(active.path, ScenePath, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(active.path) && active.isDirty)
            {
                EditorSceneManager.SaveOpenScenes();
            }
            active = EditorSceneManager.OpenScene(ScenePath);
            report.Add("已从磁盘重载场景: " + ScenePath);

            // ---- 幂等保护：树落点是位置哈希确定性的，rerun 会在完全相同坐标原位叠树
            // （外观几乎看不出、drawcall 翻倍、存盘即污染）；而 moss/低模岩首跑已删，
            // 重跑不会重建——半幂等最危险。磁盘场景里已有统一根节点 = 上次已成功保存，
            // 直接跳过；真要重做请先用 git 还原场景文件再点 rerun 菜单。----
            foreach (GameObject sceneRoot in active.GetRootGameObjects())
            {
                if (sceneRoot.name == UnifiedRootName)
                {
                    report.Add("场景已含 " + UnifiedRootName + "（上次已成功应用），幂等跳过，未做任何改动。");
                    WriteReport(report);
                    File.WriteAllText(DonePath, "Succeeded");
                    Debug.Log("[LycheeMountainUnify_20260613] 已应用过，幂等跳过。报告: " + ReportPath);
                    return;
                }
            }

            // ---- 资产加载（任一缺失即中止，不动场景）----
            GameObject mountainPrefab = LoadPrefab(GuidMountain);
            var rockSetPrefabs = new GameObject[RockSetGuids.Length];
            for (int i = 0; i < RockSetGuids.Length; i++) rockSetPrefabs[i] = LoadPrefab(RockSetGuids[i]);
            var treePrefabs = new GameObject[TreeGuids.Length];
            for (int i = 0; i < TreeGuids.Length; i++) treePrefabs[i] = LoadPrefab(TreeGuids[i]);
            bool assetMissing = mountainPrefab == null;
            foreach (GameObject p in rockSetPrefabs) assetMissing |= p == null;
            foreach (GameObject p in treePrefabs) assetMissing |= p == null;
            if (assetMissing)
            {
                Abort(report, "目标 prefab 缺失（GUID 反查失败），中止。");
                return;
            }

            // ---- 现役地表（路面 RoadGroundFit + 名字含 Terrain 的地形网格）----
            var ground = new GroundSurface();
            int roadCount = 0, terrainCount = 0;
            foreach (MeshFilter filter in UnityEngine.Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
            {
                if (filter.sharedMesh == null || !filter.gameObject.activeInHierarchy) continue;
                string assetPath = AssetDatabase.GetAssetPath(filter.sharedMesh);
                bool isRoad = !string.IsNullOrEmpty(assetPath) && assetPath.Contains("RoadGroundFit");
                bool isTerrain = filter.gameObject.name.Contains("Terrain");
                if (!isRoad && !isTerrain) continue;
                ground.AddMesh(filter);
                if (isRoad) roadCount++; else terrainCount++;
            }
            report.Add(string.Format("地表采样源：路面网格 {0} 个 + 地形网格 {1} 个，三角形 {2} 个",
                roadCount, terrainCount, ground.a.Count));
            if (ground.a.Count == 0)
            {
                Abort(report, "没采到任何地表三角形，中止。");
                return;
            }

            // ---- prefab 自然尺寸（scale=1 包围盒），替换换算与树高钳制都用它 ----
            Vector3 mountainSize = MeasurePrefabSize(mountainPrefab);
            var rockSetSizes = new Vector3[rockSetPrefabs.Length];
            for (int i = 0; i < rockSetPrefabs.Length; i++) rockSetSizes[i] = MeasurePrefabSize(rockSetPrefabs[i]);
            var treeHeights = new float[treePrefabs.Length];
            for (int i = 0; i < treePrefabs.Length; i++) treeHeights[i] = MeasurePrefabSize(treePrefabs[i]).y;
            report.Add(string.Format("自然尺寸：mountain_terrain_02={0}；树高={1:F2}/{2:F2}/{3:F2}",
                Fmt(mountainSize), treeHeights[0], treeHeights[1], treeHeights[2]));

            // ---- 全场快照分类（含 inactive；后续增删不回扫，避免误伤新建对象）----
            var mossInactive = new List<GameObject>();
            var mossActive = new List<GameObject>();
            var rockMossActive = new List<GameObject>();
            var rockMossInactive = new List<GameObject>();
            var rockSetInactive = new List<GameObject>();
            var mountainsActive = new List<GameObject>();
            GameObject detailGroup = null;
            var rockSetGuidSet = new HashSet<string>(RockSetGuids);
            foreach (GameObject root in active.GetRootGameObjects())
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    GameObject go = t.gameObject;
                    if (go.name == DetailGroupName && detailGroup == null) detailGroup = go;
                    if (!PrefabUtility.IsPartOfPrefabInstance(go)) continue;
                    if (!PrefabUtility.IsOutermostPrefabInstanceRoot(go)) continue;
                    string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                    if (string.IsNullOrEmpty(path)) continue;
                    string guid = AssetDatabase.AssetPathToGUID(path);
                    if (guid == GuidMoss01 || guid == GuidMoss02)
                    {
                        if (go.activeInHierarchy) mossActive.Add(go); else mossInactive.Add(go);
                    }
                    else if (guid == GuidRockMoss09 || guid == GuidRockMoss11)
                    {
                        if (go.activeInHierarchy) rockMossActive.Add(go); else rockMossInactive.Add(go);
                    }
                    else if (rockSetGuidSet.Contains(guid))
                    {
                        if (!go.activeSelf) rockSetInactive.Add(go);
                    }
                    else if (guid == GuidMountain)
                    {
                        if (go.activeInHierarchy) mountainsActive.Add(go);
                    }
                }
            }
            report.Add(string.Format(
                "盘点：moss山 禁用{0}/激活{1}（侦察预期 39/0）；低模岩 激活{2}/禁用{3}（预期 157/0）；" +
                "禁用rock_set {4}（预期 12）；现役写实山 {5}（预期 36）；细节组 {6}",
                mossInactive.Count, mossActive.Count, rockMossActive.Count, rockMossInactive.Count,
                rockSetInactive.Count, mountainsActive.Count, detailGroup != null ? "已找到" : "未找到"));

            // ---- 新对象统一父节点（场景根、identity，不挤进旧层级，整树可删=回滚）----
            var unifiedRoot = new GameObject(UnifiedRootName);
            var rocksParent = new GameObject("Rocks_Realistic_20260613");
            rocksParent.transform.SetParent(unifiedRoot.transform, false);
            var treesParent = new GameObject("Slope_Trees_20260613");
            treesParent.transform.SetParent(unifiedRoot.transform, false);

            int deletedMoss = 0, deletedPiles = 0, deletedRockMoss = 0;
            int replacedRocks = 0, replacedMoss = 0, activatedDetails = 0, plantedTrees = 0;

            // ---- 1) 删除已禁用的 moss 山（不渲染的场景垃圾；删除前信息落报告）----
            report.Add("== 删除禁用 moss 山 ==");
            foreach (GameObject go in mossInactive)
            {
                if (go == null) continue;
                report.Add(string.Format("  删除 {0} pos={1} scale={2} 源={3}",
                    go.name, Fmt(go.transform.position), Fmt(go.transform.localScale),
                    PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go)));
                UnityEngine.Object.DestroyImmediate(go);
                deletedMoss++;
            }

            // ---- 2) 删除整组禁用的 Mountain_Detail_Upgrade_20260610（14 个低模石堆）----
            if (detailGroup != null && !detailGroup.activeInHierarchy)
            {
                report.Add("== 删除禁用组 " + DetailGroupName + " ==");
                foreach (Transform child in detailGroup.GetComponentsInChildren<Transform>(true))
                {
                    if (child.gameObject == detailGroup) continue;
                    report.Add(string.Format("  含 {0} pos={1}", child.name, Fmt(child.position)));
                    deletedPiles++;
                }
                UnityEngine.Object.DestroyImmediate(detailGroup);
            }
            else if (detailGroup != null)
            {
                report.Add(DetailGroupName + " 处于激活状态（与侦察不符），保守不删。");
            }

            // ---- 3) 防御：激活的 moss 山（侦察说不存在）→ 原位换写实山 + 补 3~6 棵树 ----
            if (mossActive.Count > 0) report.Add("== 激活 moss 山原位替换（防御路径）==");
            foreach (GameObject go in mossActive)
            {
                if (go == null) continue;
                Bounds ob;
                if (!TryGetWorldBounds(go, out ob))
                {
                    report.Add("  " + go.name + ": 无渲染包围盒，跳过");
                    continue;
                }
                string origInfo = string.Format("{0} pos={1} scale={2} 源={3}",
                    go.name, Fmt(go.transform.position), Fmt(go.transform.localScale),
                    PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go));
                Vector3 applied;
                GameObject inst = ReplaceInPlace(go, ob, mountainPrefab, mountainSize,
                    unifiedRoot.transform, ground, RockEmbed, out applied);
                inst.name = "MossSwap_" + replacedMoss.ToString("00") + "_mountain_terrain_02";
                int trees = PlantSlopeTrees(inst, treePrefabs, treeHeights,
                    treesParent.transform, 3, 6, report);
                plantedTrees += trees;
                report.Add(string.Format("  {0} -> mountain_terrain_02 scale={1} 补树{2}",
                    origInfo, Fmt(applied), trees));
                UnityEngine.Object.DestroyImmediate(go);
                replacedMoss++;
            }

            // ---- 4) 157 个激活低模岩 → rock_set_01~04（哈希轮换），这是现役风格打架的主力 ----
            report.Add("== 低模岩 PP_Rock_Moss_Grown -> rock_set 替换表 ==");
            foreach (GameObject go in rockMossActive)
            {
                if (go == null) continue;
                Bounds ob;
                if (!TryGetWorldBounds(go, out ob))
                {
                    report.Add("  " + go.name + ": 无渲染包围盒，跳过");
                    continue;
                }
                int ri = Mathf.Min(rockSetPrefabs.Length - 1,
                    (int)(Hash01(PosSeed(go.transform.position, 411)) * rockSetPrefabs.Length));
                string origInfo = string.Format("{0} pos={1} 源={2}",
                    go.name, Fmt(go.transform.position),
                    PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go));
                Vector3 applied;
                GameObject inst = ReplaceInPlace(go, ob, rockSetPrefabs[ri], rockSetSizes[ri],
                    rocksParent.transform, ground, RockEmbed, out applied);
                inst.name = "RockUnify_" + replacedRocks.ToString("000") + "_" + rockSetPrefabs[ri].name;
                report.Add(string.Format("  {0} -> {1} scale={2} 补树0",
                    origInfo, rockSetPrefabs[ri].name, Fmt(applied)));
                UnityEngine.Object.DestroyImmediate(go);
                replacedRocks++;
            }
            // 禁用的低模岩同属垃圾，顺手清掉（删除前落报告）
            foreach (GameObject go in rockMossInactive)
            {
                if (go == null) continue;
                report.Add(string.Format("  删除禁用低模岩 {0} pos={1}", go.name, Fmt(go.transform.position)));
                UnityEngine.Object.DestroyImmediate(go);
                deletedRockMoss++;
            }

            // ---- 5) 激活 0611 摆好的 12 个写实细节岩，并按现役地表校 Y（地形 0613 抬升过）----
            report.Add("== 激活 rock_set 细节岩 + 校 Y ==");
            foreach (GameObject go in rockSetInactive)
            {
                if (go == null) continue;
                go.SetActive(true);
                if (!go.activeInHierarchy)
                {
                    report.Add("  " + go.name + ": 父级仍禁用，激活无效（保持原样）");
                    continue;
                }
                Bounds ob;
                if (!TryGetWorldBounds(go, out ob))
                {
                    report.Add("  " + go.name + ": 激活后无渲染包围盒，跳过校 Y");
                    activatedDetails++;
                    continue;
                }
                float groundY;
                string yNote = "采不到地表，Y 不动";
                if (SampleGroundAround(ground, ob, out groundY))
                {
                    float delta = (groundY - DetailEmbed) - ob.min.y;
                    delta = Mathf.Clamp(delta, -DetailMoveCap, DetailMoveCap);
                    if (Mathf.Abs(delta) > 0.02f)
                    {
                        go.transform.position += Vector3.up * delta;
                        yNote = string.Format("Y 校正 {0:+0.00;-0.00}m", delta);
                    }
                    else
                    {
                        yNote = "Y 已贴合";
                    }
                }
                report.Add(string.Format("  激活 {0} pos={1} {2}", go.name, Fmt(go.transform.position), yNote));
                activatedDetails++;
            }

            // ---- 6) 36 座现役写实山坡面补 URP 树（moss 山的树烘死在网格里，删山即删树，这里补观感）----
            report.Add("== 现役写实山坡面补树（每山 2~4 棵，哈希确定性）==");
            foreach (GameObject m in mountainsActive)
            {
                if (m == null) continue;
                int trees = PlantSlopeTrees(m, treePrefabs, treeHeights, treesParent.transform, 2, 4, report);
                if (trees > 0)
                {
                    report.Add(string.Format("  {0} pos={1} 补树{2}", m.name, Fmt(m.transform.position), trees));
                }
                plantedTrees += trees;
            }

            int totalChanges = deletedMoss + deletedPiles + deletedRockMoss +
                               replacedRocks + replacedMoss + activatedDetails + plantedTrees;
            report.Add(string.Format(
                "汇总：删禁用moss山{0} 删禁用石堆{1} 删禁用低模岩{2}；低模岩替换{3} moss山替换{4}；" +
                "激活细节岩{5}；补树{6}；新对象父节点 {7}",
                deletedMoss, deletedPiles, deletedRockMoss, replacedRocks, replacedMoss,
                activatedDetails, plantedTrees, UnifiedRootName));

            bool saved;
            if (totalChanges > 0)
            {
                EditorSceneManager.MarkSceneDirty(active);
                saved = EditorSceneManager.SaveScene(active);
            }
            else
            {
                // 零改动不保存：连空的 Mountains_Unified 节点都不该留进场景
                UnityEngine.Object.DestroyImmediate(unifiedRoot);
                saved = true;
            }
            report.Add(string.Format("总改动 {0} 项；场景保存: {1}", totalChanges, saved));
            WriteReport(report);
            File.WriteAllText(DonePath, saved ? "Succeeded" : "SaveFailed");
            Debug.Log("[LycheeMountainUnify_20260613] 完成，报告: " + ReportPath);
        }
        catch (Exception exception)
        {
            // 失败路径不 SaveScene（场景留在内存态，磁盘原样）；done 写失败标记防轮询死循环
            report.Add("EXCEPTION: " + exception);
            WriteReport(report);
            TryWriteDone("Exception");
            Debug.LogError("[LycheeMountainUnify_20260613] 执行异常: " + exception.Message);
        }
    }

    static void Abort(List<string> report, string error)
    {
        report.Add("ERROR: " + error);
        WriteReport(report);
        TryWriteDone("Failed");
        Debug.LogError("[LycheeMountainUnify_20260613] " + error);
    }

    static void WriteReport(List<string> lines)
    {
        try { File.WriteAllText(ReportPath, string.Join(Environment.NewLine, lines) + Environment.NewLine); }
        catch (Exception e) { Debug.LogWarning("[LycheeMountainUnify_20260613] 写报告失败: " + e.Message); }
    }

    static void TryWriteDone(string content)
    {
        try { File.WriteAllText(DonePath, content); }
        catch (Exception e) { Debug.LogWarning("[LycheeMountainUnify_20260613] 写 done 失败: " + e.Message); }
    }

    [MenuItem("Tools/Lychee/Replay Upgrade 20260612/Mountain Unify 20260613 (rerun)")]
    static void RerunNow()
    {
        try { if (File.Exists(DonePath)) File.Delete(DonePath); }
        catch (Exception e) { Debug.LogWarning("[LycheeMountainUnify_20260613] 删 done 失败: " + e.Message); }
        nextPollTime = 0;
    }
}
