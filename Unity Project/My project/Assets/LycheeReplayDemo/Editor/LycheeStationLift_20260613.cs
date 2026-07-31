using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 一次性驿站抬升（20260613）：用户反馈南岭驿/洛阳驿等建筑陷入地下太多。
/// 病因与彩带同源——驿站模型按旧地形落位，此后路基/地形抬升把建筑底座埋了。
/// 做法：采样锚点处现役路面（RoadGroundFit*）与现役地形（Terrain_FullerRiver*）的最高面，
/// 建筑包围盒底低于地表 0.12m 以上的，整体抬到地表下 0.05m（轻微嵌入防悬空）。
/// 只升不降：水驿/码头本来就贴水低位，采样不到路面或不超阈值的一律不动。
/// 模式与 LycheePresentationRetune_20260612B 一致：update 轮询 + done 标记 + 报告 + 磁盘重载。
/// </summary>
[InitializeOnLoad]
static class LycheeStationLift_20260613
{
    const string ScenePath = "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity";
    const string DoneFileName = ".lychee_station_lift_20260613.done";
    const string ReportFileName = "LycheeStationLift_20260613_report.txt";
    const float EmbedDepth = 0.05f;     // 抬升后底座仍嵌入地表的深度
    const float SinkThreshold = 0.12f;  // 低于地表超过该值才视为"陷入太多"

    static double nextPollTime;

    static LycheeStationLift_20260613()
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

    static void Run()
    {
        var report = new List<string>();
        report.Add("LycheeStationLift_20260613 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
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

            // 地表 = 现役路面 + 现役地形 的最高面
            var ground = new GroundSurface();
            int roadCount = 0, terrainCount = 0;
            foreach (MeshFilter filter in UnityEngine.Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
            {
                if (filter.sharedMesh == null || !filter.gameObject.activeInHierarchy) continue;
                string assetPath = AssetDatabase.GetAssetPath(filter.sharedMesh);
                bool isRoad = !string.IsNullOrEmpty(assetPath) && assetPath.Contains("RoadGroundFit");
                // 场景里的地形对象：01_Terrain_Base / Continuous_Terrain（名字含 Terrain 的生成网格）
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

            int lifted = 0;
            foreach (MapStationAnchor anchor in
                     UnityEngine.Object.FindObjectsByType<MapStationAnchor>(FindObjectsSortMode.None))
            {
                Transform model = anchor.transform.Find("Imported_Station_Model");
                string label = string.IsNullOrEmpty(anchor.displayName) ? anchor.name : anchor.displayName;
                if (model == null)
                {
                    report.Add(label + ": 无导入模型，跳过");
                    continue;
                }
                Renderer[] renderers = model.GetComponentsInChildren<Renderer>();
                if (renderers.Length == 0)
                {
                    report.Add(label + ": 无渲染器，跳过");
                    continue;
                }
                Bounds bounds = renderers[0].bounds;
                foreach (Renderer r in renderers) bounds.Encapsulate(r.bounds);

                // 按建筑足迹的中心+四角采样，取最高地表：建筑四周草地常比锚点处的路面高，
                // 单点采样会低估埋深（实测南岭驿单点只差 7cm，但视觉上墙体半埋在草坡里）
                float groundY = float.MinValue;
                bool anyHit = false;
                float cx = bounds.center.x, cz = bounds.center.z;
                float ex = bounds.extents.x * 0.8f, ez = bounds.extents.z * 0.8f;
                float[] sx = { cx, cx - ex, cx + ex, cx - ex, cx + ex };
                float[] sz = { cz, cz - ez, cz - ez, cz + ez, cz + ez };
                for (int s = 0; s < sx.Length; s++)
                {
                    if (ground.TrySampleTop(sx[s], sz[s], out float sampleY) && sampleY > groundY)
                    {
                        groundY = sampleY;
                        anyHit = true;
                    }
                }
                if (!anyHit)
                {
                    report.Add(string.Format("{0}: 足迹范围采不到地表（水驿/码头属正常），跳过", label));
                    continue;
                }
                float targetBottom = groundY - EmbedDepth;
                float delta = targetBottom - bounds.min.y;
                if (delta <= SinkThreshold)
                {
                    report.Add(string.Format("{0}: 底={1:F2} 地表={2:F2} 差 {3:F2}，无需抬升",
                        label, bounds.min.y, groundY, delta));
                    continue;
                }
                delta = Mathf.Min(delta, 1.2f); // 防足迹内采到异常高点（如山体边缘）导致建筑飞天
                Undo.RecordObject(model, "Lift station");
                model.position += Vector3.up * delta;
                EditorUtility.SetDirty(model);
                lifted++;
                report.Add(string.Format("{0}: 底={1:F2} 地表={2:F2} → 抬升 {3:F2}m",
                    label, bounds.min.y, groundY, delta));
            }

            bool saved = lifted > 0 ? EditorSceneManager.SaveScene(active) : true;
            report.Add(string.Format("抬升 {0} 个驿站；场景保存: {1}", lifted, saved));
            WriteReport(report);
            File.WriteAllText(DonePath, saved ? "Succeeded" : "SaveFailed");
            Debug.Log("[LycheeStationLift_20260613] 完成，报告: " + ReportPath);
        }
        catch (Exception exception)
        {
            report.Add("EXCEPTION: " + exception);
            WriteReport(report);
            TryWriteDone("Exception");
            Debug.LogError("[LycheeStationLift_20260613] 执行异常: " + exception.Message);
        }
    }

    static void Abort(List<string> report, string error)
    {
        report.Add("ERROR: " + error);
        WriteReport(report);
        TryWriteDone("Failed");
        Debug.LogError("[LycheeStationLift_20260613] " + error);
    }

    static void WriteReport(List<string> lines)
    {
        try { File.WriteAllText(ReportPath, string.Join(Environment.NewLine, lines) + Environment.NewLine); }
        catch (Exception e) { Debug.LogWarning("[LycheeStationLift_20260613] 写报告失败: " + e.Message); }
    }

    static void TryWriteDone(string content)
    {
        try { File.WriteAllText(DonePath, content); }
        catch (Exception e) { Debug.LogWarning("[LycheeStationLift_20260613] 写 done 失败: " + e.Message); }
    }

    [MenuItem("Tools/Lychee/Replay Upgrade 20260612/Station Lift 20260613 (rerun)")]
    static void RerunNow()
    {
        try { if (File.Exists(DonePath)) File.Delete(DonePath); }
        catch (Exception e) { Debug.LogWarning("[LycheeStationLift_20260613] 删 done 失败: " + e.Message); }
        nextPollTime = 0;
    }
}
