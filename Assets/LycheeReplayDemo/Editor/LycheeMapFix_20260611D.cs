// 第三轮修复（2026-06-11 批次 D）：S05 洞庭水驿周围 4.5 内树木全部外移（保证视野通透），重新截图
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
    internal static class LycheeMapFix20260611D
    {
        static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string MarkerPath => Path.Combine(Root, "LycheeMapFix_20260611D.done");
        static string ReportPath => Path.Combine(Root, "LycheeMapFix_20260611D_report.txt");

        [MenuItem("Tools/Lychee/修复地图 20260611D（S05视野树清理）")]
        public static void RunMenu() { Run(true); }

        public static void Run(bool force)
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.name != "LycheeReplayDemo") return;
            if (!force && File.Exists(MarkerPath)) return;
            var report = new StringBuilder();
            report.AppendLine("LycheeMapFix_20260611D 报告 " + DateTime.Now.ToString("HH:mm:ss"));
            try
            {
                var roots = scene.GetRootGameObjects();
                var anchors = new List<Transform>();
                foreach (var r in roots)
                {
                    if (r == null) continue;
                    foreach (var t in r.GetComponentsInChildren<Transform>(true))
                        if (t.name.Contains("_ANCHOR_")) anchors.Add(t);
                }
                var s05 = anchors.FirstOrDefault(a => a.name.StartsWith("S05"));
                if (s05 == null) throw new Exception("S05 not found");
                Transform model = null;
                foreach (Transform c in s05) if (c.name == "Imported_Station_Model") { model = c; break; }
                var rs = model.GetComponentsInChildren<Renderer>(true);
                Bounds b = rs[0].bounds;
                for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                Vector2 mc = new Vector2(b.center.x, b.center.z);
                report.AppendLine(string.Format("S05 模型中心 ({0:F2},{1:F2}) 包围盒 {2:F2}x{3:F2} 高 {4:F2}", b.center.x, b.center.z, b.size.x, b.size.z, b.size.y));

                // 河道中心线（用于净空）
                var rivers = new List<Vector3[]>();
                foreach (var rn in new[] { "E12_Route", "E13_Route" })
                {
                    Transform rt = null;
                    foreach (var r in roots)
                    {
                        if (r == null) continue;
                        foreach (var t in r.GetComponentsInChildren<Transform>(true))
                            if (t.name == rn) { rt = t; break; }
                        if (rt != null) break;
                    }
                    if (rt == null) continue;
                    var v = rt.GetComponent<MeshFilter>().sharedMesh.vertices;
                    var c = new Vector3[v.Length / 2];
                    for (int i = 0; i < c.Length; i++)
                        c[i] = (rt.TransformPoint(v[i * 2]) + rt.TransformPoint(v[i * 2 + 1])) * 0.5f;
                    rivers.Add(c);
                }
                Func<Vector2, Vector2> awayFromRiver = p =>
                {
                    float best = float.MaxValue; Vector2 bq = Vector2.zero;
                    foreach (var line in rivers)
                        for (int i = 0; i + 1 < line.Length; i++)
                        {
                            Vector2 a = new Vector2(line[i].x, line[i].z), bb = new Vector2(line[i + 1].x, line[i + 1].z);
                            Vector2 ab = bb - a; float l2 = ab.sqrMagnitude;
                            float tt = l2 < 1e-8f ? 0 : Mathf.Clamp01(Vector2.Dot(p - a, ab) / l2);
                            Vector2 q = a + ab * tt; float d = Vector2.Distance(p, q);
                            if (d < best) { best = d; bq = q; }
                        }
                    if (best >= 2.2f) return p;
                    Vector2 dir = (p - bq); dir = dir.sqrMagnitude < 1e-6f ? Vector2.up : dir.normalized;
                    return bq + dir * 2.2f;
                };

                Transform terrain = null;
                foreach (var r in roots)
                {
                    if (r == null) continue;
                    foreach (var t in r.GetComponentsInChildren<Transform>(true))
                        if (t.name == "Continuous_Terrain") { terrain = t; break; }
                    if (terrain != null) break;
                }
                var tcol = terrain.GetComponent<MeshCollider>();
                Func<Vector2, float> groundY = p =>
                {
                    RaycastHit hit;
                    if (tcol.Raycast(new Ray(new Vector3(p.x, 50, p.y), Vector3.down), out hit, 200f)) return hit.point.y;
                    return 0f;
                };

                var trees = new List<Transform>();
                foreach (var g in new[] { "Tree_Clusters", "Lingnan_Orchard_Tree_Groves" })
                {
                    Transform gt = null;
                    foreach (var r in roots)
                    {
                        if (r == null) continue;
                        foreach (var t in r.GetComponentsInChildren<Transform>(true))
                            if (t.name == g) { gt = t; break; }
                        if (gt != null) break;
                    }
                    if (gt == null) continue;
                    foreach (Transform c in gt) trees.Add(c);
                }

                int moved = 0;
                foreach (var tr in trees)
                {
                    Vector2 p = new Vector2(tr.position.x, tr.position.z);
                    float d = Vector2.Distance(p, mc);
                    if (d < 4.5f)
                    {
                        Vector2 dir = d < 1e-4f ? Vector2.up : (p - mc) / d;
                        Vector2 np = mc + dir * 4.7f;
                        np = awayFromRiver(np);
                        // 若仍太近（被河道推回），绕向另一侧
                        if (Vector2.Distance(np, mc) < 4.4f)
                        {
                            Vector2 perp = new Vector2(-dir.y, dir.x);
                            np = awayFromRiver(mc + perp * 4.7f);
                        }
                        tr.position = new Vector3(np.x, groundY(np) - 0.02f, np.y);
                        moved++;
                    }
                }
                report.AppendLine("S05 视野清理外移树木 " + moved + " 棵（半径 4.5）");

                // 局部间距松弛
                var local = trees.Where(t => Vector2.Distance(new Vector2(t.position.x, t.position.z), mc) < 10f).ToList();
                for (int iter = 0; iter < 40; iter++)
                {
                    bool any = false;
                    for (int i = 0; i < local.Count; i++)
                        for (int j = i + 1; j < local.Count; j++)
                        {
                            Vector3 pi = local[i].position, pj = local[j].position;
                            Vector2 diff = new Vector2(pi.x - pj.x, pi.z - pj.z);
                            float dd = diff.magnitude;
                            if (dd < 2.1f && dd > 1e-4f)
                            {
                                Vector2 push = diff / dd * (2.1f - dd) * 0.5f;
                                local[i].position = pi + new Vector3(push.x, 0, push.y);
                                local[j].position = pj - new Vector3(push.x, 0, push.y);
                                any = true;
                            }
                        }
                    foreach (var t in local)
                    {
                        Vector2 p = new Vector2(t.position.x, t.position.z);
                        Vector2 np = awayFromRiver(p);
                        if (Vector2.Distance(np, mc) < 4.5f)
                        {
                            Vector2 dir = (np - mc).normalized;
                            np = mc + dir * 4.7f;
                            any = true;
                        }
                        if ((np - p).sqrMagnitude > 1e-8f)
                            t.position = new Vector3(np.x, t.position.y, np.y);
                    }
                    if (!any) break;
                }
                foreach (var t in local)
                {
                    Vector2 p = new Vector2(t.position.x, t.position.z);
                    t.position = new Vector3(p.x, groundY(p) - 0.02f, p.y);
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
                Debug.LogError("[LycheeFixD] " + ex);
            }
            finally
            {
                File.WriteAllText(ReportPath, report.ToString());
            }
        }
    }

    [InitializeOnLoad]
    internal static class LycheeMapFix20260611DAutoRun
    {
        static LycheeMapFix20260611DAutoRun()
        {
            EditorApplication.delayCall += () => EditorApplication.delayCall += TryRun;
        }

        static void TryRun()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (File.Exists(Path.Combine(root, "LycheeMapFix_20260611D.done"))) return;
            if (!File.Exists(Path.Combine(root, "LycheeMapFix_20260611C.done"))) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            if (SceneManager.GetActiveScene().name != "LycheeReplayDemo") return;
            LycheeMapFix20260611D.Run(false);
        }
    }
}
