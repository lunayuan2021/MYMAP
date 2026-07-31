using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 一次性植被统一（20260613）：用户反馈圆球树/低模树/贴图树混搭风格打架，统一成 URP Tree Pack 一套。
/// 侦察结论（磁盘 YAML 全量统计，写死在此备查）：散布树 483 棵全部已是 URP_Tree_1/2/3 prefab 实例；
/// Pure Poly 树、Hill Rock tree_02 贴图松、FreeMountain、内置 Sphere 圆球树在场景中引用数均为 0。
/// 仍按完整判定+替换实现：防止编辑器内存态与磁盘不一致、防止后续生成器再混入杂牌树后无工具可用。
/// 风格混搭的真实来源是 PP_Forest_Mountain_Moss（树长在山体网格上）与 mountain_terrain_02（贴图山），
/// 属山体装饰非独立散布树，按红线不动，只在报告里点名。
/// 模式与 LycheeStationLift_20260613 一致：update 轮询 + done 标记 + 报告 + 磁盘重载。
/// </summary>
[InitializeOnLoad]
static class LycheeTreeUnify_20260613
{
    const string ScenePath = "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity";
    const string DoneFileName = ".lychee_tree_unify_20260613.done";
    const string ReportFileName = "LycheeTreeUnify_20260613_report.txt";
    const string UnifiedParentName = "Trees_Unified_20260613";
    const string UrpPackPrefix = "Assets/Tree_Packs/URP_Tree_Pack/";

    // 替换目标：包里全部 3 种树，按位置哈希轮换避免单调
    static readonly string[] TargetPrefabPaths =
    {
        UrpPackPrefix + "Prefabs/URP_Tree_1.prefab",
        UrpPackPrefix + "Prefabs/URP_Tree_2.prefab",
        UrpPackPrefix + "Prefabs/URP_Tree_3.prefab"
    };

    static double nextPollTime;

    static LycheeTreeUnify_20260613()
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

    /// <summary>
    /// 红线祖先名：站点模型（含一体化果树装饰）、锚点、山体群、石头群、水路水上建筑、
    /// 既有山体替换容器，以及本脚本自己的新父节点（防重跑误伤）。
    /// </summary>
    static readonly string[] ExcludedAncestorNames =
    {
        "Imported_Station_Model",
        "Irregular_Mountain_Groups",
        "Rock_Clusters",
        "WaterRoute_And_River",
        "Hill_Rock_Mountain_Replacement_20260611",
        "Mountain_Detail_Upgrade_20260610",
        UnifiedParentName
    };

    static bool IsUnderExcludedAncestor(Transform t)
    {
        for (Transform cur = t; cur != null; cur = cur.parent)
        {
            string n = cur.name;
            // _ANCHOR_ 与 Station 兜住全部站点系对象（S0x_ANCHOR_xx / WaterStation_Split 等）
            if (n.Contains("_ANCHOR_") ||
                n.IndexOf("Station", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            for (int i = 0; i < ExcludedAncestorNames.Length; i++)
            {
                if (string.Equals(n, ExcludedAncestorNames[i], StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 资产文件名是否"像树"：tree/birch/pine/palm。只看文件名不看目录，
    /// 防止 "Hill Rock Mountain Terrain" 目录里的 rock_set 被 Terrain≠tree 之外的写法误伤。
    /// </summary>
    static bool AssetNameLooksLikeTree(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath)) return false;
        string file = Path.GetFileNameWithoutExtension(assetPath).ToLowerInvariant();
        return file.Contains("tree") || file.Contains("birch") || file.Contains("pine") || file.Contains("palm");
    }

    /// <summary>判定为"杂牌树"的来源包：URP Tree Pack 之外的全部第三方植被来源。</summary>
    static bool AssetFromSuspectPack(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath)) return false;
        if (assetPath.StartsWith(UrpPackPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        return assetPath.StartsWith("Assets/Pure Poly/", StringComparison.OrdinalIgnoreCase) ||
               assetPath.StartsWith("Assets/Hill Rock Mountain Terrain/", StringComparison.OrdinalIgnoreCase) ||
               assetPath.StartsWith("Assets/FreeMountain/", StringComparison.OrdinalIgnoreCase);
    }

    class Candidate
    {
        public GameObject root;
        public string reason;      // 命中哪条判定规则，写进报告
        public string sourceAsset; // 原 prefab/网格资产路径，回滚凭据
    }

    static void Run()
    {
        var report = new List<string>();
        report.Add("LycheeTreeUnify_20260613 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        report.Add("目标：场景散布树统一为 URP Tree Pack（URP_Tree_1/2/3）。");
        report.Add("");
        report.Add("== 判定规则 ==");
        report.Add("A) Prefab 实例根：源 prefab 在 Pure Poly / Hill Rock Mountain Terrain / FreeMountain 且文件名含 tree|birch|pine|palm。");
        report.Add("B) 非 prefab 圆球树：名字含 tree 且层级内网格全为内置基元（Library/unity default resources），取最内层命中（容器名 Tree_Clusters 等因含 FBX 网格天然不命中）。");
        report.Add("C) 非 prefab 直挂网格：MeshFilter 网格资产来自上述杂牌包且文件名像树。");
        report.Add("红线排除：祖先含 _ANCHOR_ / *Station* / Imported_Station_Model / Irregular_Mountain_Groups / Rock_Clusters / WaterRoute_And_River / 山体替换容器 / " + UnifiedParentName + "。");
        report.Add("");
        try
        {
            int yamlLinesBefore = CountFileLines(Path.Combine(ProjectRoot, ScenePath));

            Scene active = SceneManager.GetActiveScene();
            if (!string.Equals(active.path, ScenePath, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(active.path) && active.isDirty)
            {
                EditorSceneManager.SaveOpenScenes();
            }
            active = EditorSceneManager.OpenScene(ScenePath);
            report.Add("已从磁盘重载场景: " + ScenePath);

            // ---------- 目标 prefab 准备：实测自然高度与 pivot→包围盒底偏移（不靠猜） ----------
            var prefabs = new List<GameObject>();
            var naturalHeights = new List<float>();
            var pivotToBottom = new List<float>(); // scale=1 时包围盒底相对 pivot 的 y 偏移
            for (int i = 0; i < TargetPrefabPaths.Length; i++)
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TargetPrefabPaths[i]);
                if (prefab == null)
                {
                    report.Add("WARN: 加载失败 " + TargetPrefabPaths[i]);
                    continue;
                }
                GameObject temp = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                temp.transform.position = Vector3.zero;
                temp.transform.rotation = Quaternion.identity;
                temp.transform.localScale = Vector3.one;
                Renderer[] rends = temp.GetComponentsInChildren<Renderer>(true);
                if (rends.Length == 0)
                {
                    report.Add("WARN: 无渲染器 " + TargetPrefabPaths[i]);
                    UnityEngine.Object.DestroyImmediate(temp);
                    continue;
                }
                Bounds b = rends[0].bounds;
                foreach (Renderer r in rends) b.Encapsulate(r.bounds);
                prefabs.Add(prefab);
                naturalHeights.Add(b.size.y);
                pivotToBottom.Add(b.min.y); // temp 在原点，min.y 即 pivot→底偏移
                report.Add(string.Format("目标树 {0}: 自然高 {1:F2}m，pivot→包围盒底偏移 {2:F3}m",
                    Path.GetFileNameWithoutExtension(TargetPrefabPaths[i]), b.size.y, b.min.y));
                UnityEngine.Object.DestroyImmediate(temp);
            }
            if (prefabs.Count == 0)
            {
                Abort(report, "URP Tree Pack prefab 一个都没加载到，中止。");
                return;
            }

            // ---------- 全场景扫描：census + 杂牌树收集 ----------
            int urpTree1 = 0, urpTree2 = 0, urpTree3 = 0;
            int forestMountain = 0, hillTerrain = 0;
            int excludedHits = 0;
            var candidates = new List<Candidate>();
            Transform[] all = UnityEngine.Object.FindObjectsByType<Transform>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (Transform t in all)
            {
                GameObject go = t.gameObject;
                if (PrefabUtility.IsAnyPrefabInstanceRoot(go))
                {
                    UnityEngine.Object src = PrefabUtility.GetCorrespondingObjectFromOriginalSource(go);
                    string srcPath = src != null ? AssetDatabase.GetAssetPath(src) : null;
                    if (string.IsNullOrEmpty(srcPath)) continue;
                    if (srcPath.StartsWith(UrpPackPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        // 已是目标包，只做 census
                        if (srcPath.Contains("URP_Tree_1")) urpTree1++;
                        else if (srcPath.Contains("URP_Tree_2")) urpTree2++;
                        else if (srcPath.Contains("URP_Tree_3")) urpTree3++;
                        continue;
                    }
                    // 山体类只统计不动手（报告里点名风格混搭来源）
                    if (srcPath.Contains("PP_Forest_Mountain_Moss")) { forestMountain++; continue; }
                    if (srcPath.Contains("mountain_terrain_02")) { hillTerrain++; continue; }
                    if (AssetFromSuspectPack(srcPath) && AssetNameLooksLikeTree(srcPath))
                    {
                        if (IsUnderExcludedAncestor(t)) { excludedHits++; continue; }
                        candidates.Add(new Candidate { root = go, reason = "A:杂牌树prefab实例", sourceAsset = srcPath });
                    }
                    continue;
                }
                // prefab 内部子物体随其根处理，单独跳过防重复
                if (PrefabUtility.IsPartOfPrefabInstance(go)) continue;

                MeshFilter mf = go.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    string meshPath = AssetDatabase.GetAssetPath(mf.sharedMesh);
                    if (AssetFromSuspectPack(meshPath) && AssetNameLooksLikeTree(meshPath))
                    {
                        if (IsUnderExcludedAncestor(t)) { excludedHits++; continue; }
                        candidates.Add(new Candidate { root = go, reason = "C:杂牌树直挂网格", sourceAsset = meshPath });
                        continue;
                    }
                }
                // 规则 B：程序化圆球树（旧生成器 TerrainTree=圆柱干+圆球冠，全内置基元）
                if (go.name.IndexOf("tree", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    AllMeshesAreBuiltin(go, out int builtinMeshCount) && builtinMeshCount > 0)
                {
                    if (IsUnderExcludedAncestor(t)) { excludedHits++; continue; }
                    candidates.Add(new Candidate { root = go, reason = "B:内置基元圆球树", sourceAsset = "(builtin primitives x" + builtinMeshCount + ")" });
                }
            }

            // 最内层去重：候选若包含另一候选（如 Tree_Groups 容器包着 TerrainTree），丢弃外层只换叶子
            var leafCandidates = new List<Candidate>();
            foreach (Candidate c in candidates)
            {
                bool containsAnother = false;
                foreach (Candidate other in candidates)
                {
                    if (other == c) continue;
                    if (other.root.transform.IsChildOf(c.root.transform)) { containsAnother = true; break; }
                }
                if (!containsAnother) leafCandidates.Add(c);
            }

            report.Add("");
            report.Add("== 现状统计 ==");
            report.Add(string.Format("已是 URP Tree Pack 的散布树：URP_Tree_1={0} URP_Tree_2={1} URP_Tree_3={2}（合计 {3}）",
                urpTree1, urpTree2, urpTree3, urpTree1 + urpTree2 + urpTree3));
            report.Add(string.Format("山体类（红线不动，风格混搭真实来源）：PP_Forest_Mountain_Moss={0} 个（低模树长在山体网格上）、mountain_terrain_02={1} 个（贴图山）",
                forestMountain, hillTerrain));
            report.Add(string.Format("红线排除命中 {0} 处；候选杂牌树（去重后）{1} 棵", excludedHits, leafCandidates.Count));

            // ---------- 原位替换 ----------
            int[] speciesCount = new int[prefabs.Count];
            int replaced = 0;
            Transform unifiedParent = null;
            if (leafCandidates.Count > 0)
            {
                var parentGo = new GameObject(UnifiedParentName);
                Undo.RegisterCreatedObjectUndo(parentGo, "Tree unify parent");
                unifiedParent = parentGo.transform;
                report.Add("");
                report.Add("== 替换明细（销毁前全表，回滚凭据） ==");
                report.Add("规则 | 原名 | 原位置 | 原rotY | 原包围盒高 | 原资产 | 新树种 | 新缩放");
            }
            foreach (Candidate c in leafCandidates)
            {
                Transform ot = c.root.transform;
                Vector3 pos = ot.position;
                float rotY = ot.rotation.eulerAngles.y;
                Renderer[] rends = c.root.GetComponentsInChildren<Renderer>(true);
                if (rends.Length == 0)
                {
                    report.Add(string.Format("SKIP 无渲染器: {0} @ {1}", c.root.name, pos));
                    continue;
                }
                Bounds ob = rends[0].bounds;
                foreach (Renderer r in rends) ob.Encapsulate(r.bounds);
                float origHeight = Mathf.Max(ob.size.y, 0.01f);

                // 树种按位置哈希轮换（x*7+z*13 取模），同位置重跑结果稳定
                int species = Mathf.Abs(Mathf.FloorToInt(pos.x * 7f + pos.z * 13f)) % prefabs.Count;
                // 缩放：原树包围盒高 ≈ 新树高，钳制 0.6~1.6 防过大过小，再乘 prefab 自然比例
                float factor = Mathf.Clamp(origHeight / Mathf.Max(naturalHeights[species], 0.01f), 0.6f, 1.6f);

                GameObject fresh = (GameObject)PrefabUtility.InstantiatePrefab(prefabs[species]);
                Undo.RegisterCreatedObjectUndo(fresh, "Tree unify instance");
                fresh.transform.SetParent(unifiedParent, true);
                fresh.transform.rotation = Quaternion.Euler(0f, rotY, 0f);
                fresh.transform.localScale = prefabs[species].transform.localScale * factor;
                // pivot 补偿：新树包围盒底对齐原树包围盒底（原 y 基准不动，底部贴原地面）
                float newBottomIfAtPos = pos.y + pivotToBottom[species] * factor;
                Vector3 finalPos = pos;
                finalPos.y += ob.min.y - newBottomIfAtPos;
                fresh.transform.position = finalPos;

                report.Add(string.Format("{0} | {1} | ({2:F2},{3:F2},{4:F2}) | {5:F0}° | {6:F2}m | {7} | {8} | {9:F2}",
                    c.reason, c.root.name, pos.x, pos.y, pos.z, rotY, origHeight, c.sourceAsset,
                    prefabs[species].name, factor));
                speciesCount[species]++;
                replaced++;
                Undo.DestroyObjectImmediate(c.root);
            }

            report.Add("");
            report.Add("== 替换统计 ==");
            if (replaced == 0)
            {
                report.Add("命中 0 棵：磁盘场景的全部独立散布树本就已是 URP Tree Pack（见现状统计），无需替换。");
                report.Add("用户看到的风格打架来自 PP_Forest_Mountain_Moss 山体自带低模树与 mountain_terrain_02 贴图山，");
                report.Add("属山体网格一体装饰，按红线（不动山体/石头/站点）未处理；如需统一需另做山体替换专项。");
            }
            else
            {
                for (int i = 0; i < prefabs.Count; i++)
                    report.Add(string.Format("{0}: {1} 棵", prefabs[i].name, speciesCount[i]));
                report.Add(string.Format("总计替换 {0} 棵，统一挂在 {1} 下（整体回滚=删该节点+按上表回种）。", replaced, UnifiedParentName));
            }

            // ---------- 风摇结论（侦察实证） ----------
            report.Add("");
            report.Add("== 风摇 ==");
            report.Add("URP Tree Pack 全部 6 个材质（Leaf/Trunk x3）均为标准 Universal Render Pipeline/Lit，");
            report.Add("无任何 Wind/Sway 顶点风摇属性：无风摇支持，建议后续加 vertex sway（独立新 shader 资产，不改包内 shader 防升级覆盖）。");

            bool saved = true;
            if (replaced > 0)
            {
                saved = EditorSceneManager.SaveScene(active);
                int yamlLinesAfter = CountFileLines(Path.Combine(ProjectRoot, ScenePath));
                report.Add("");
                report.Add(string.Format("场景保存: {0}；YAML 行数 {1} → {2}", saved, yamlLinesBefore, yamlLinesAfter));
                if (yamlLinesBefore > 0 && yamlLinesAfter > yamlLinesBefore * 2)
                    report.Add("警告：场景 YAML 行数翻倍以上，体积暴涨，注意 WebGL 包大小与加载时间。");
            }
            else
            {
                report.Add("");
                report.Add("无改动，场景未保存（YAML 行数 " + yamlLinesBefore + " 不变）。");
            }

            WriteReport(report);
            File.WriteAllText(DonePath, saved ? "Succeeded" : "SaveFailed");
            Debug.Log("[LycheeTreeUnify_20260613] 完成，替换 " + replaced + " 棵，报告: " + ReportPath);
        }
        catch (Exception exception)
        {
            report.Add("EXCEPTION: " + exception);
            WriteReport(report);
            TryWriteDone("Exception");
            Debug.LogError("[LycheeTreeUnify_20260613] 执行异常: " + exception.Message);
        }
    }

    /// <summary>
    /// 层级内全部 MeshFilter 是否都用内置基元网格（Sphere/Cylinder 等，资产路径为 unity default resources）。
    /// Tree_Clusters 这类容器下挂着 URP 树的 FBX 网格，天然返回 false，不会被误判成圆球树。
    /// </summary>
    static bool AllMeshesAreBuiltin(GameObject go, out int meshCount)
    {
        meshCount = 0;
        foreach (MeshFilter mf in go.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null) continue;
            string p = AssetDatabase.GetAssetPath(mf.sharedMesh);
            if (string.IsNullOrEmpty(p) || !p.Contains("unity default resources")) return false;
            meshCount++;
        }
        return true;
    }

    static int CountFileLines(string path)
    {
        try
        {
            int count = 0;
            using (var reader = new StreamReader(path))
            {
                while (reader.ReadLine() != null) count++;
            }
            return count;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    static void Abort(List<string> report, string error)
    {
        report.Add("ERROR: " + error);
        WriteReport(report);
        TryWriteDone("Failed");
        Debug.LogError("[LycheeTreeUnify_20260613] " + error);
    }

    static void WriteReport(List<string> lines)
    {
        try { File.WriteAllText(ReportPath, string.Join(Environment.NewLine, lines) + Environment.NewLine); }
        catch (Exception e) { Debug.LogWarning("[LycheeTreeUnify_20260613] 写报告失败: " + e.Message); }
    }

    static void TryWriteDone(string content)
    {
        try { File.WriteAllText(DonePath, content); }
        catch (Exception e) { Debug.LogWarning("[LycheeTreeUnify_20260613] 写 done 失败: " + e.Message); }
    }

    [MenuItem("Tools/Lychee/Replay Upgrade 20260612/Tree Unify 20260613 (rerun)")]
    static void RerunNow()
    {
        try { if (File.Exists(DonePath)) File.Delete(DonePath); }
        catch (Exception e) { Debug.LogWarning("[LycheeTreeUnify_20260613] 删 done 失败: " + e.Message); }
        nextPollTime = 0;
    }
}
