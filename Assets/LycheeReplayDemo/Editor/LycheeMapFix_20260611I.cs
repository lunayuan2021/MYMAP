// 第八轮修复（2026-06-11 批次 I）：S04/S05 新位置周边树木净空（半径 4.5）+ 间距松弛
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
    internal static class LycheeMapFix20260611I
    {
        static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string MarkerPath => Path.Combine(Root, "LycheeMapFix_20260611I.done");
        static string ReportPath => Path.Combine(Root, "LycheeMapFix_20260611I_report.txt");

        [MenuItem("Tools/Lychee/修复地图 20260611I（水驿新位置树净空）")]
        public static void RunMenu() { Run(true); }

        public static void Run(bool force)
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.name != "LycheeReplayDemo") return;
            if (!force && File.Exists(MarkerPath)) return;
            var report = new StringBuilder();
            report.AppendLine("LycheeMapFix_20260611I 报告 " + DateTime.Now.ToString("HH:mm:ss"));
            try
            {
                var roots = scene.GetRootGameObjects();
                var all = new List<Transform>();
                foreach (var r in roots) { if (r != null) all.AddRange(r.GetComponentsInChildren<Transform>(true)); }
                Func<string, Transform> find = n => all.FirstOrDefault(t => t != null && t.name == n);

                var tcol = find("Continuous_Terrain").GetComponent<MeshCollider>();
                Func<Vector2, float> groundY = p =>
                {
                    RaycastHit hit;
                    if (tcol.Raycast(new Ray(new Vector3(p.x, 60, p.y), Vector3.down), out hit, 200f)) return hit.point.y;
                    return 0f;
                };
                var riverPts = new List<Vector3>();
                foreach (var en in new[] { "E12_Route", "E13_Route" })
                {
                    var t = find(en); if (t == null) continue;
                    var v = t.GetComponent<MeshFilter>().sharedMesh.vertices;
                    for (int i = 0; i + 1 < v.Length; i += 2)
                        riverPts.Add((t.TransformPoint(v[i]) + t.TransformPoint(v[i + 1])) * 0.5f);
                }
                Func<Vector2, Vector2> awayFromRiver = p =>
                {
                    float best = float.MaxValue; Vector2 bq = Vector2.zero;
                    foreach (var rp in riverPts)
                    {
                        float d = Vector2.Distance(p, new Vector2(rp.x, rp.z));
                        if (d < best) { best = d; bq = new Vector2(rp.x, rp.z); }
                    }
                    if (best >= 2.2f) return p;
                    Vector2 dir = (p - bq); dir = dir.sqrMagnitude < 1e-6f ? Vector2.up : dir.normalized;
                    return bq + dir * 2.2f;
                };

                var trees = new List<Transform>();
                foreach (var g in new[] { "Tree_Clusters", "Lingnan_Orchard_Tree_Groves" })
                {
                    var gt = find(g); if (gt == null) continue;
                    foreach (Transform c in gt) trees.Add(c);
                }

                var anchors = all.Where(t => t != null && t.name.Contains("_ANCHOR_")).ToList();
                foreach (var key in new[] { "S04", "S05" })
                {
                    var a = anchors.FirstOrDefault(t => t.name.StartsWith(key));
                    if (a == null) continue;
                    Transform m = null;
                    foreach (Transform c in a) if (c.name == "Imported_Station_Model") { m = c; break; }
                    if (m == null) continue;
                    var rs = m.GetComponentsInChildren<Renderer>(true);
                    Bounds b = rs[0].bounds;
                    for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                    Vector2 mc = new Vector2(b.center.x, b.center.z);

                    int moved = 0;
                    foreach (var tr in trees)
                    {
                        Vector2 p = new Vector2(tr.position.x, tr.position.z);
                        float d = Vector2.Distance(p, mc);
                        if (d < 4.5f)
                        {
                            Vector2 dir = d < 1e-4f ? Vector2.up : (p - mc) / d;
                            Vector2 np = awayFromRiver(mc + dir * 4.7f);
                            if (Vector2.Distance(np, mc) < 4.4f)
                            {
                                Vector2 perp = new Vector2(-dir.y, dir.x);
                                np = awayFromRiver(mc + perp * 4.7f);
                            }
                            tr.position = new Vector3(np.x, groundY(np) - 0.02f, np.y);
                            moved++;
                        }
                    }
                    report.AppendLine(key + " 周边外移树木 " + moved + " 棵（新中心 " + mc + "）");

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
                                np = mc + (np - mc).normalized * 4.7f;
                            if ((np - p).sqrMagnitude > 1e-8f)
                            { t.position = new Vector3(np.x, t.position.y, np.y); any = true; }
                        }
                        if (!any) break;
                    }
                    foreach (var t in local)
                    {
                        Vector2 p = new Vector2(t.position.x, t.position.z);
                        t.position = new Vector3(p.x, groundY(p) - 0.02f, p.y);
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
                Debug.LogError("[LycheeFixI] " + ex);
            }
            finally
            {
                File.WriteAllText(ReportPath, report.ToString());
            }
        }
    }

    [InitializeOnLoad]
    internal static class LycheeMapFix20260611IAutoRun
    {
        static LycheeMapFix20260611IAutoRun()
        {
            EditorApplication.delayCall += () => EditorApplication.delayCall += TryRun;
        }

        static void TryRun()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (File.Exists(Path.Combine(root, "LycheeMapFix_20260611I.done"))) return;
            if (!File.Exists(Path.Combine(root, "LycheeMapFix_20260611H.done"))) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            if (SceneManager.GetActiveScene().name != "LycheeReplayDemo") return;
            LycheeMapFix20260611I.Run(false);
        }
    }
}
