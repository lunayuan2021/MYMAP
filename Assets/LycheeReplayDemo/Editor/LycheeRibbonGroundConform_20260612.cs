// 色带贴地修复（2026-06-12）：相机改为 48° 透视近景后，路线细色带/箭头在斜坡沟壑处悬空或插地。
// 本脚本把活跃覆盖组（Route_Line_Overlay_20260611V，含历史 G 组的同名子对象命名规则 Exx_Line_TYPE）
// 中的色带与 Route_Arrows 网格逐顶点重投影到 MapElevationData 采样高度：
//   色带 y = SampleWorldHeight + 0.045，箭头 y = SampleWorldHeight + 0.07，
//   叠压错层：ROAD +0.000 / MOUNTAIN +0.005 / BRANCH +0.010。
// 排除水路（WATER / E12 / E13）——水面必须保持平面（历史教训：水面纵坡>2% 会悬空盖物）；
// 合并箭头网格里落在 E12/E13 水面附近的顶点保持原样。旗帜只把根节点贴地，不动网格。
// 新网格全部另存到 Assets/LycheeMap15/GeneratedMeshes/RibbonConform_20260612/，绝不覆盖原网格资产（可回滚）。
// 防重复：工程根 .lychee_ribbon_conform_20260612.done；报告：工程根 LycheeRibbonConform_20260612_report.txt。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LycheeReplayDemoTools
{
    internal static class LycheeRibbonGroundConform20260612
    {
        static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string MarkerPath => Path.Combine(Root, ".lychee_ribbon_conform_20260612.done");
        static string ReportPath => Path.Combine(Root, "LycheeRibbonConform_20260612_report.txt");

        const string ScenePath = "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity";
        const string SceneName = "LycheeReplayDemo";
        const string MeshParentDir = "Assets/LycheeMap15/GeneratedMeshes";
        const string MeshDirName = "RibbonConform_20260612";
        const string MeshDir = MeshParentDir + "/" + MeshDirName;
        const string OverlayPrefix = "Route_Line_Overlay_";   // G（停用）与 V（活跃）两组
        const string ArrowObjectName = "Route_Arrows";
        const string FlagName = "Node_RouteFlag_20260611G";

        const float RibbonLift = 0.045f;        // 色带抬升
        const float ArrowLift = 0.07f;          // 箭头抬升（防 z-fighting）
        const float TypeStagger = 0.005f;       // 不同路线类型叠压错层
        const float WaterKeepRadius = 1.0f;     // 箭头顶点距水面顶点 XZ 距离阈值内保持原样

        // Exx_Line_ROAD / E12_Line_WATER / E15_Line_MOUNTAIN / E17_Line_BRANCH
        static readonly Regex RibbonNameRe = new Regex(@"^E\d{2}_Line_(ROAD|WATER|MOUNTAIN|BRANCH)$");
        // 错层序：ROAD 基准，MOUNTAIN +1 档，BRANCH +2 档（WATER 整体排除）
        static readonly Dictionary<string, int> TypeOrder = new Dictionary<string, int>
        {
            { "ROAD", 0 }, { "MOUNTAIN", 1 }, { "BRANCH", 2 },
        };

        [MenuItem("Tools/Lychee/Replay Upgrade 20260612/Ribbon Ground Conform")]
        public static void RunMenu()
        {
            // 菜单强制重跑：先删标记再执行
            if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
            Run(true);
        }

        public static void Run(bool force)
        {
            if (!force && File.Exists(MarkerPath)) return;

            var report = new StringBuilder();
            report.AppendLine("LycheeRibbonConform_20260612 报告 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.AppendLine("色带抬升=" + RibbonLift + "m，箭头抬升=" + ArrowLift +
                              "m，类型错层=" + TypeStagger + "m（ROAD+0 / MOUNTAIN+0.005 / BRANCH+0.010），水路整体排除");
            try
            {
                // ---------- 确保打开目标场景（同 20260612A 模式） ----------
                if (SceneManager.GetActiveScene().path != ScenePath)
                    EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                var scene = SceneManager.GetActiveScene();
                if (scene.name != SceneName)
                {
                    report.AppendLine("中止：无法打开场景 " + ScenePath + "，当前场景=" + scene.name);
                    return; // 不写标记、不保存
                }

                // ---------- 高度数据 ----------
                var elevation = UnityEngine.Object.FindFirstObjectByType<MapElevationData>(FindObjectsInactive.Include);
                if (elevation == null)
                {
                    report.AppendLine("中止：场景内未找到 MapElevationData（地形高度采样组件），未做任何修改。");
                    return; // 不写标记、不保存
                }
                report.AppendLine("MapElevationData：grid=" + elevation.gridWidth + "x" + elevation.gridHeight +
                                  " origin=" + elevation.worldOrigin + " cell=" + elevation.cellSize +
                                  " 样本数=" + (elevation.heightSamples == null ? 0 : elevation.heightSamples.Length));

                // ---------- 收集覆盖组下的色带 / 箭头 ----------
                var allFilters = UnityEngine.Object.FindObjectsByType<MeshFilter>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);

                var ribbonTargets = new List<(MeshFilter mf, string type)>();
                var arrowTargets = new List<MeshFilter>();
                int skipWater = 0, skipInactive = 0, skipNoMesh = 0;

                foreach (var mf in allFilters)
                {
                    if (mf == null) continue;
                    var t = mf.transform;
                    if (t.parent == null || !t.parent.name.StartsWith(OverlayPrefix)) continue;

                    string n = t.name;
                    bool isRibbon = RibbonNameRe.IsMatch(n);
                    bool isArrow = n == ArrowObjectName;
                    if (!isRibbon && !isArrow) continue; // 覆盖组下的其它对象（如岸线修饰）不动

                    if (!mf.gameObject.activeInHierarchy)
                    {
                        skipInactive++; // 历史 G 组整体停用，保留原状可回滚
                        report.AppendLine("跳过(未激活): " + t.parent.name + "/" + n);
                        continue;
                    }
                    if (mf.sharedMesh == null)
                    {
                        skipNoMesh++;
                        report.AppendLine("跳过(无网格): " + t.parent.name + "/" + n);
                        continue;
                    }
                    if (isRibbon)
                    {
                        string type = n.Substring(n.LastIndexOf('_') + 1);
                        // 排除水路：水面必须保持平面（水面纵坡>2% 会悬空盖物）
                        if (type == "WATER" || n.StartsWith("E12_") || n.StartsWith("E13_"))
                        {
                            skipWater++;
                            report.AppendLine("跳过(水路保持现状): " + t.parent.name + "/" + n);
                            continue;
                        }
                        ribbonTargets.Add((mf, type));
                    }
                    else
                    {
                        arrowTargets.Add(mf);
                    }
                }

                if (ribbonTargets.Count == 0 && arrowTargets.Count == 0)
                {
                    report.AppendLine("中止：未在 " + OverlayPrefix + "* 组下找到任何可处理的色带/箭头对象，未做任何修改。");
                    return; // 不写标记、不保存
                }

                // ---------- 水面参考点（用于合并箭头网格中跳过水上箭头顶点） ----------
                var waterXZ = new List<Vector2>();
                foreach (var mf in allFilters)
                {
                    if (mf == null || mf.sharedMesh == null) continue;
                    if (mf.name != "E12_Route" && mf.name != "E13_Route") continue;
                    if (!mf.gameObject.activeInHierarchy) continue;
                    var l2w = mf.transform.localToWorldMatrix;
                    foreach (var v in mf.sharedMesh.vertices)
                    {
                        Vector3 w = l2w.MultiplyPoint3x4(v);
                        waterXZ.Add(new Vector2(w.x, w.z));
                    }
                }
                report.AppendLine("水面参考顶点 " + waterXZ.Count + " 个（E12_Route/E13_Route，箭头顶点 XZ 距其 <" +
                                  WaterKeepRadius + "m 时保持原高度）");

                // ---------- 资产目录 ----------
                if (!AssetDatabase.IsValidFolder(MeshDir))
                    AssetDatabase.CreateFolder(MeshParentDir, MeshDirName);

                // 原 mesh → (变换矩阵, 新 mesh) 缓存：共享网格且同变换的对象复用，避免重复生成
                var conformCache = new Dictionary<Mesh, (Matrix4x4 mtx, Mesh mesh)>();
                int processed = 0, waterKeptVerts = 0;

                // ---------- 色带 ----------
                foreach (var (mf, type) in ribbonTargets)
                {
                    int order; TypeOrder.TryGetValue(type, out order);
                    float lift = RibbonLift + order * TypeStagger;
                    int kept;
                    var stats = ConformMesh(mf, elevation, lift, null, conformCache, out kept);
                    processed++;
                    report.AppendLine(string.Format("色带 {0}: 顶点 {1}，抬升 {2:F3}m，Δy [{3:F2}, {4:F2}]",
                        mf.name, stats.verts, lift, stats.minDy, stats.maxDy));
                }

                // ---------- 箭头（合并网格，逐顶点；水面附近顶点保持原样） ----------
                foreach (var mf in arrowTargets)
                {
                    int kept;
                    var stats = ConformMesh(mf, elevation, ArrowLift, waterXZ, conformCache, out kept);
                    processed++;
                    waterKeptVerts += kept;
                    report.AppendLine(string.Format("箭头 {0}: 顶点 {1}（水上保持 {2}），抬升 {3:F3}m，Δy [{4:F2}, {5:F2}]",
                        mf.name, stats.verts, kept, ArrowLift, stats.minDy, stats.maxDy));
                }

                // ---------- 旗帜：只调整根位置贴地，不动网格 ----------
                int flagMoved = 0, flagSkipped = 0;
                var roots = scene.GetRootGameObjects();
                foreach (var r in roots)
                {
                    if (r == null) continue;
                    foreach (var t in r.GetComponentsInChildren<Transform>(true))
                    {
                        if (t == null || t.name != FlagName) continue;
                        string anchorKey = t.parent != null && t.parent.name.Length >= 3
                            ? t.parent.name.Substring(0, 3) : "";
                        if (anchorKey == "S04" || anchorKey == "S05")
                        {
                            flagSkipped++; // 水驿旗帜不动（码头处地形被人工压平，采样网格未必同步）
                            continue;
                        }
                        Vector3 p = t.position;
                        float gy = elevation.SampleWorldHeight(p.x, p.z);
                        t.position = new Vector3(p.x, gy, p.z);
                        flagMoved++;
                    }
                }

                // ---------- 保存 ----------
                File.WriteAllText(MarkerPath, DateTime.Now.ToString("o"));
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveOpenScenes();
                AssetDatabase.SaveAssets();

                report.AppendLine();
                report.AppendLine("---- 汇总 ----");
                report.AppendLine("处理对象: " + processed + "（色带 " + ribbonTargets.Count + " + 箭头 " + arrowTargets.Count + "）");
                report.AppendLine("跳过对象: 水路 " + skipWater + "，未激活 " + skipInactive + "，无网格 " + skipNoMesh);
                report.AppendLine("箭头水上保持顶点: " + waterKeptVerts);
                report.AppendLine("旗帜贴地: " + flagMoved + "（水驿跳过 " + flagSkipped + "）");
                report.AppendLine("新网格资产目录: " + MeshDir + "（原网格资产未覆盖，可回滚）");
                report.AppendLine("偏移: 色带 ROAD +" + RibbonLift + " / MOUNTAIN +" + (RibbonLift + TypeStagger).ToString("F3") +
                                  " / BRANCH +" + (RibbonLift + 2 * TypeStagger).ToString("F3") + "，箭头 +" + ArrowLift);
                report.AppendLine("场景已保存。");
                Debug.Log("[LycheeRibbonConform20260612] 完成：处理 " + processed + " 个对象，报告见 " + ReportPath);
            }
            catch (Exception ex)
            {
                report.AppendLine("异常: " + ex);
                report.AppendLine("失败：未写入完成标记、未保存场景（场景内存改动可关闭不保存回滚；新建网格资产为增量文件，不影响原资产）。");
                Debug.LogError("[LycheeRibbonConform20260612] " + ex);
            }
            finally
            {
                File.WriteAllText(ReportPath, report.ToString());
            }
        }

        struct ConformStats { public int verts; public float minDy; public float maxDy; }

        /// <summary>
        /// 顶点→世界空间→y 改为地形采样高度+抬升→写回局部空间，存为新 Mesh 资产并赋回 MeshFilter。
        /// waterXZ 非空时，XZ 距任一水面参考点 &lt; WaterKeepRadius 的顶点保持原样（水上箭头）。
        /// </summary>
        static ConformStats ConformMesh(MeshFilter mf, MapElevationData elevation, float lift,
            List<Vector2> waterXZ, Dictionary<Mesh, (Matrix4x4 mtx, Mesh mesh)> cache, out int waterKept)
        {
            waterKept = 0;
            var src = mf.sharedMesh;
            var l2w = mf.transform.localToWorldMatrix;
            var stats = new ConformStats { verts = src.vertexCount, minDy = float.MaxValue, maxDy = float.MinValue };

            // 共享网格且同变换：直接复用已生成的贴地网格
            (Matrix4x4 mtx, Mesh mesh) hit;
            if (cache.TryGetValue(src, out hit) && hit.mtx == l2w)
            {
                mf.sharedMesh = hit.mesh;
                stats.minDy = stats.maxDy = 0f;
                return stats;
            }

            var w2l = mf.transform.worldToLocalMatrix;
            var verts = src.vertices;
            var dst = new Vector3[verts.Length];
            float r2 = WaterKeepRadius * WaterKeepRadius;
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 w = l2w.MultiplyPoint3x4(verts[i]);
                if (waterXZ != null && NearAnyXZ(waterXZ, w.x, w.z, r2))
                {
                    dst[i] = verts[i];
                    waterKept++;
                    continue;
                }
                float ny = elevation.SampleWorldHeight(w.x, w.z) + lift;
                float dy = ny - w.y;
                if (dy < stats.minDy) stats.minDy = dy;
                if (dy > stats.maxDy) stats.maxDy = dy;
                w.y = ny;
                dst[i] = w2l.MultiplyPoint3x4(w);
            }
            if (stats.minDy > stats.maxDy) { stats.minDy = 0f; stats.maxDy = 0f; } // 全部顶点都被水面保持

            // 新 Mesh 实例（绝不改原资产）
            var conformed = UnityEngine.Object.Instantiate(src);
            conformed.name = mf.name + "_Conform_20260612";
            conformed.vertices = dst;
            conformed.RecalculateNormals();
            conformed.RecalculateBounds();
            string path = AssetDatabase.GenerateUniqueAssetPath(MeshDir + "/" + mf.name + "_Conform.asset");
            AssetDatabase.CreateAsset(conformed, path);
            mf.sharedMesh = conformed;
            cache[src] = (l2w, conformed);
            return stats;
        }

        static bool NearAnyXZ(List<Vector2> pts, float x, float z, float radiusSq)
        {
            for (int i = 0; i < pts.Count; i++)
            {
                float dx = pts[i].x - x, dz = pts[i].y - z;
                if (dx * dx + dz * dz < radiusSq) return true;
            }
            return false;
        }
    }

    [InitializeOnLoad]
    internal static class LycheeRibbonGroundConform20260612AutoRun
    {
        static LycheeRibbonGroundConform20260612AutoRun()
        {
            EditorApplication.delayCall += () => EditorApplication.delayCall += TryRun;
        }

        static void TryRun()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (File.Exists(Path.Combine(root, ".lychee_ribbon_conform_20260612.done"))) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            LycheeRibbonGroundConform20260612.Run(false);
        }
    }
}
