// 第十八轮修复（2026-06-11 批次 S）：朱雀门模型沿官道前移至宫城西墙前（城门+宫城组合），标签随模型
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
    internal static class LycheeMapFix20260611S
    {
        static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string MarkerPath => Path.Combine(Root, "LycheeMapFix_20260611S.done");
        static string ReportPath => Path.Combine(Root, "LycheeMapFix_20260611S_report.txt");

        [MenuItem("Tools/Lychee/修复地图 20260611S（朱雀门前移至宫墙）")]
        public static void RunMenu() { Run(true); }

        public static void Run(bool force)
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.name != "LycheeReplayDemo") return;
            if (!force && File.Exists(MarkerPath)) return;
            var report = new StringBuilder();
            report.AppendLine("LycheeMapFix_20260611S 报告 " + DateTime.Now.ToString("HH:mm:ss"));
            try
            {
                var roots = scene.GetRootGameObjects();
                var all = new List<Transform>();
                foreach (var r in roots) { if (r != null) all.AddRange(r.GetComponentsInChildren<Transform>(true)); }
                Func<string, Transform> find = n => all.FirstOrDefault(t => t != null && t.name == n);

                var tcol = find("Continuous_Terrain").GetComponent<MeshCollider>();
                Func<float, float, float> groundY = (x, z) =>
                {
                    RaycastHit hit;
                    if (tcol.Raycast(new Ray(new Vector3(x, 60, z), Vector3.down), out hit, 200f)) return hit.point.y;
                    return 0f;
                };

                var s14 = all.FirstOrDefault(t => t != null && t.name.StartsWith("S14_ANCHOR_"));
                if (s14 == null) throw new Exception("S14 not found");
                Transform m = null;
                foreach (Transform c in s14) if (c.name == "Imported_Station_Model") { m = c; break; }
                if (m == null) throw new Exception("S14 model not found");

                // 沿 S14->S13 方向前移 1.7（official road 轴线），落在宫城西墙前
                Vector3 dir = new Vector3(33f - 36f, 0, 11f - 12f).normalized;
                Vector2 target = new Vector2(36f, 12f) + new Vector2(dir.x, dir.z) * 1.7f;

                var rs = m.GetComponentsInChildren<Renderer>(true);
                Bounds wb = rs[0].bounds;
                for (int i = 1; i < rs.Length; i++) wb.Encapsulate(rs[i].bounds);
                m.position += new Vector3(target.x - wb.center.x, 0, target.y - wb.center.z);
                wb = rs[0].bounds;
                for (int i = 1; i < rs.Length; i++) wb.Encapsulate(rs[i].bounds);
                float g = groundY(wb.center.x, wb.center.z);
                m.position += new Vector3(0, (g - 0.04f) - wb.min.y, 0);
                report.AppendLine(string.Format("朱雀门前移至 ({0:F1},{1:F1})（宫城西墙前、官道轴线上）", target.x, target.y));

                // 标签随模型
                wb = rs[0].bounds;
                for (int i = 1; i < rs.Length; i++) wb.Encapsulate(rs[i].bounds);
                foreach (Transform c in s14)
                    if (c.name == "Node_Name_CN")
                        c.localPosition = new Vector3(wb.center.x - s14.position.x, (wb.max.y - s14.position.y) + 0.75f, wb.center.z - s14.position.z);

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
                Debug.LogError("[LycheeFixS] " + ex);
            }
            finally
            {
                File.WriteAllText(ReportPath, report.ToString());
            }
        }
    }

    [InitializeOnLoad]
    internal static class LycheeMapFix20260611SAutoRun
    {
        static LycheeMapFix20260611SAutoRun()
        {
            EditorApplication.delayCall += () => EditorApplication.delayCall += TryRun;
        }

        static void TryRun()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (File.Exists(Path.Combine(root, "LycheeMapFix_20260611S.done"))) return;
            if (!File.Exists(Path.Combine(root, "LycheeMapFix_20260611R.done"))) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            if (SceneManager.GetActiveScene().name != "LycheeReplayDemo") return;
            LycheeMapFix20260611S.Run(false);
        }
    }
}
