using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 一次性彩带重贴（20260613）：按"现役路面网格"逐顶点重新贴合路线彩带。
/// 背景：原彩带是 6/11 L 轮按当时路面建的，之后路基又重建了两轮（V/X 轮抬升），
/// 彩带大面积沉入新路面之下；昨天的 RibbonConform 用了更过期的地形高度网格，埋得更深（已回滚）。
/// 本脚本直接采样场景中激活的 RoadGroundFit* 路面三角形求真实路面高度：
///   命中路面 → y = 路面 + 类型抬升；采样不到（桥外/山地外沿）→ 只升不降（原 y + 0.04）。
/// 水路彩带不采路面，整体 +0.06 抬出水面消除闪烁。新网格另存 RibbonRefit_20260613/，原资产不动可回滚。
/// 模式与 LycheePresentationRetune_20260612B 一致：update 轮询 + done 标记 + 报告 + 磁盘强制重载场景。
/// </summary>
[InitializeOnLoad]
static class LycheeRibbonRefit_20260613
{
    const string ScenePath = "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity";
    const string OverlayRootName = "Route_Line_Overlay_20260611V";
    const string MeshOutDir = "Assets/LycheeMap15/GeneratedMeshes/RibbonRefit_20260613";
    const string DoneFileName = ".lychee_ribbon_refit_20260613.done";
    const string ReportFileName = "LycheeRibbonRefit_20260613_report.txt";

    const float LiftRoad = 0.05f;
    const float LiftMountain = 0.055f;
    const float LiftBranch = 0.06f;
    const float LiftArrow = 0.09f;
    const float LiftWaterFlat = 0.06f;   // 水路：不采路面，整体抬出水面
    const float LiftFallback = 0.04f;    // 采样不到路面时只升不降的保底抬升

    static double nextPollTime;

    static LycheeRibbonRefit_20260613()
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

    /// <summary>世界空间路面三角形池，含 XZ 包围盒粗筛。</summary>
    class RoadSurface
    {
        public readonly List<Vector3> a = new List<Vector3>();
        public readonly List<Vector3> b = new List<Vector3>();
        public readonly List<Vector3> c = new List<Vector3>();
        public readonly List<Vector4> bboxXZ = new List<Vector4>(); // xMin,zMin,xMax,zMax

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

        /// <summary>取 (x,z) 处路面最高点；多层路面取最上层。无命中返回 false。</summary>
        public bool TrySampleTop(float x, float z, out float y)
        {
            y = float.MinValue;
            bool hit = false;
            for (int i = 0; i < a.Count; i++)
            {
                Vector4 box = bboxXZ[i];
                if (x < box.x || z < box.y || x > box.z || z > box.w) continue;
                Vector3 pa = a[i], pb = b[i], pc = c[i];
                // XZ 平面重心坐标
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

    static void Run()
    {
        var report = new List<string>();
        report.Add("LycheeRibbonRefit_20260613 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        try
        {
            // 磁盘强制重载，避免内存驻留态被保存回去（与 Retune 同理）。
            Scene active = SceneManager.GetActiveScene();
            if (!string.Equals(active.path, ScenePath, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(active.path) && active.isDirty)
            {
                EditorSceneManager.SaveOpenScenes();
            }
            active = EditorSceneManager.OpenScene(ScenePath);
            report.Add("已从磁盘重载场景: " + ScenePath);

            // 1. 收集现役路面三角形（只取激活对象、资产路径含 RoadGroundFit 的网格）。
            var surface = new RoadSurface();
            int roadMeshCount = 0;
            foreach (MeshFilter filter in UnityEngine.Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
            {
                if (filter.sharedMesh == null || !filter.gameObject.activeInHierarchy) continue;
                string assetPath = AssetDatabase.GetAssetPath(filter.sharedMesh);
                if (string.IsNullOrEmpty(assetPath) || !assetPath.Contains("RoadGroundFit")) continue;
                surface.AddMesh(filter);
                roadMeshCount++;
            }
            report.Add(string.Format("现役路面网格 {0} 个，三角形 {1} 个", roadMeshCount, surface.a.Count));
            if (surface.a.Count == 0)
            {
                Abort(report, "没找到任何激活的 RoadGroundFit 路面网格，中止。");
                return;
            }

            // 2. 找激活的彩带 overlay 根（可能嵌套在任意父级下，全场景搜，含未激活兜底）。
            GameObject overlayRoot = null;
            foreach (Transform t in UnityEngine.Object.FindObjectsByType<Transform>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t.name == OverlayRootName) { overlayRoot = t.gameObject; break; }
            }
            if (overlayRoot == null)
            {
                Abort(report, "未找到 " + OverlayRootName + "，中止。");
                return;
            }
            report.Add(string.Format("overlay 根: {0}（父级: {1}, active={2}）", overlayRoot.name,
                overlayRoot.transform.parent != null ? overlayRoot.transform.parent.name : "(场景根)",
                overlayRoot.activeInHierarchy));

            if (!Directory.Exists(MeshOutDir))
            {
                Directory.CreateDirectory(MeshOutDir);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }

            // 3. 逐条重贴。
            int processed = 0;
            foreach (Transform child in overlayRoot.transform)
            {
                if (!child.gameObject.activeInHierarchy) { report.Add("跳过(未激活): " + child.name); continue; }
                MeshFilter filter = child.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) { report.Add("跳过(无网格): " + child.name); continue; }

                bool isArrow = child.name.Contains("Arrow");
                bool isWater = child.name.Contains("WATER");
                float lift = isArrow ? LiftArrow
                    : isWater ? LiftWaterFlat
                    : child.name.Contains("MOUNTAIN") ? LiftMountain
                    : child.name.Contains("BRANCH") ? LiftBranch
                    : LiftRoad;

                Mesh newMesh = UnityEngine.Object.Instantiate(filter.sharedMesh);
                newMesh.name = child.name + "_Refit";
                Vector3[] verts = newMesh.vertices;
                int roadHits = 0;
                float dMin = float.MaxValue, dMax = float.MinValue;
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 world = child.TransformPoint(verts[i]);
                    float newY;
                    if (isWater)
                    {
                        newY = world.y + LiftWaterFlat; // 水路：整体抬出水面，不采路面
                    }
                    else if (surface.TrySampleTop(world.x, world.z, out float roadY))
                    {
                        newY = roadY + lift;
                        roadHits++;
                    }
                    else
                    {
                        newY = world.y + LiftFallback; // 采不到路面只升不降，绝不下压
                    }
                    float delta = newY - world.y;
                    if (delta < dMin) dMin = delta;
                    if (delta > dMax) dMax = delta;
                    world.y = newY;
                    verts[i] = child.InverseTransformPoint(world);
                }
                newMesh.vertices = verts;
                newMesh.RecalculateBounds();

                string outPath = MeshOutDir + "/" + child.name + "_Refit.asset";
                AssetDatabase.CreateAsset(newMesh, outPath);
                filter.sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(outPath);
                EditorUtility.SetDirty(filter);
                processed++;
                report.Add(string.Format("{0}: 顶点 {1}，路面命中 {2}，Δy [{3:F2}, {4:F2}]，抬升 {5:F3}",
                    child.name, verts.Length, roadHits, dMin, dMax, lift));
            }

            AssetDatabase.SaveAssets();
            bool saved = EditorSceneManager.SaveScene(active);
            report.Add(string.Format("处理 {0} 个对象；场景保存: {1}", processed, saved));
            WriteReport(report);
            File.WriteAllText(DonePath, saved ? "Succeeded" : "SaveFailed");
            Debug.Log("[LycheeRibbonRefit_20260613] 重贴完成，报告: " + ReportPath);
        }
        catch (Exception exception)
        {
            report.Add("EXCEPTION: " + exception);
            WriteReport(report);
            TryWriteDone("Exception");
            Debug.LogError("[LycheeRibbonRefit_20260613] 执行异常: " + exception.Message);
        }
    }

    static void Abort(List<string> report, string error)
    {
        report.Add("ERROR: " + error);
        WriteReport(report);
        TryWriteDone("Failed");
        Debug.LogError("[LycheeRibbonRefit_20260613] " + error);
    }

    static void WriteReport(List<string> lines)
    {
        try { File.WriteAllText(ReportPath, string.Join(Environment.NewLine, lines) + Environment.NewLine); }
        catch (Exception e) { Debug.LogWarning("[LycheeRibbonRefit_20260613] 写报告失败: " + e.Message); }
    }

    static void TryWriteDone(string content)
    {
        try { File.WriteAllText(DonePath, content); }
        catch (Exception e) { Debug.LogWarning("[LycheeRibbonRefit_20260613] 写 done 失败: " + e.Message); }
    }

    [MenuItem("Tools/Lychee/Replay Upgrade 20260612/Ribbon Refit 20260613 (rerun)")]
    static void RerunNow()
    {
        try { if (File.Exists(DonePath)) File.Delete(DonePath); }
        catch (Exception e) { Debug.LogWarning("[LycheeRibbonRefit_20260613] 删 done 失败: " + e.Message); }
        nextPollTime = 0;
    }
}
