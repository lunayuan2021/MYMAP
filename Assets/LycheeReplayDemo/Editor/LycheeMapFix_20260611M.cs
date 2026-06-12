// 第十二轮修复（2026-06-11 批次 M）：水驿基座抬至 水面-0.02，使栈桥桥面露出水面（合并网格折中）
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LycheeReplayDemoTools
{
    internal static class LycheeMapFix20260611M
    {
        static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string MarkerPath => Path.Combine(Root, "LycheeMapFix_20260611M.done");
        static string ReportPath => Path.Combine(Root, "LycheeMapFix_20260611M_report.txt");

        [MenuItem("Tools/Lychee/修复地图 20260611M（水驿基座微抬）")]
        public static void RunMenu() { Run(true); }

        public static void Run(bool force)
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.name != "LycheeReplayDemo") return;
            if (!force && File.Exists(MarkerPath)) return;
            var report = new StringBuilder();
            report.AppendLine("LycheeMapFix_20260611M 报告 " + DateTime.Now.ToString("HH:mm:ss"));
            try
            {
                var roots = scene.GetRootGameObjects();
                var all = new List<Transform>();
                foreach (var r in roots) { if (r != null) all.AddRange(r.GetComponentsInChildren<Transform>(true)); }
                Func<string, Transform> find = n => all.FirstOrDefault(t => t != null && t.name == n);

                // 水面高度（取各水驿最近的中心线点）
                var centers = new List<Vector3>();
                foreach (var rn in new[] { "E12_Route", "E13_Route" })
                {
                    var t = find(rn); if (t == null) continue;
                    var v = t.GetComponent<MeshFilter>().sharedMesh.vertices;
                    for (int i = 0; i + 1 < v.Length; i += 2)
                        centers.Add((t.TransformPoint(v[i]) + t.TransformPoint(v[i + 1])) * 0.5f);
                }

                foreach (var key in new[] { "S04", "S05" })
                {
                    var a = all.FirstOrDefault(t => t != null && t.name.StartsWith(key + "_ANCHOR_"));
                    if (a == null) continue;
                    Transform m = null;
                    foreach (Transform c in a) if (c.name == "Imported_Station_Model") { m = c; break; }
                    if (m == null) continue;
                    var rs = m.GetComponentsInChildren<Renderer>(true);
                    Bounds b = rs[0].bounds;
                    for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                    float wy = -0.85f, best = float.MaxValue;
                    foreach (var c in centers)
                    {
                        float d = Vector2.Distance(new Vector2(b.center.x, b.center.z), new Vector2(c.x, c.z));
                        if (d < best) { best = d; wy = c.y; }
                    }
                    float target = wy - 0.02f;
                    m.position += new Vector3(0, target - b.min.y, 0);
                    report.AppendLine(string.Format("{0}: 基座 {1:F2} -> {2:F2}（水面 {3:F2}）", key, b.min.y, target, wy));
                    // 标签
                    Transform label = null;
                    foreach (Transform c in a) if (c.name == "Node_Name_CN") { label = c; break; }
                    if (label != null)
                    {
                        Bounds wb2 = rs[0].bounds;
                        for (int i = 1; i < rs.Length; i++) wb2.Encapsulate(rs[i].bounds);
                        var lp = label.localPosition;
                        label.localPosition = new Vector3(lp.x, (wb2.max.y - a.position.y) + 0.75f, lp.z);
                    }
                }

                File.WriteAllText(MarkerPath, DateTime.Now.ToString("o"));
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                report.AppendLine("场景已保存。");
                File.WriteAllText(Path.Combine(Root, "LycheeMapCapture_20260611.request"), DateTime.Now.ToString("o"));
                LycheeMapCapture20260611.Capture();
            }
            catch (Exception ex)
            {
                report.AppendLine("异常: " + ex);
                Debug.LogError("[LycheeFixM] " + ex);
            }
            finally
            {
                File.WriteAllText(ReportPath, report.ToString());
            }
        }
    }

    [InitializeOnLoad]
    internal static class LycheeMapFix20260611MAutoRun
    {
        static LycheeMapFix20260611MAutoRun()
        {
            EditorApplication.delayCall += () => EditorApplication.delayCall += TryRun;
        }

        static void TryRun()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (File.Exists(Path.Combine(root, "LycheeMapFix_20260611M.done"))) return;
            if (!File.Exists(Path.Combine(root, "LycheeMapFix_20260611L.done"))) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            if (SceneManager.GetActiveScene().name != "LycheeReplayDemo") return;
            LycheeMapFix20260611M.Run(false);
        }
    }
}
