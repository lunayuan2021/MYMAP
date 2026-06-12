// 第二轮修复（2026-06-11 批次 C）：节点建筑周围树木净空 + S04/S05 局部树距松弛 + 重新贴地
// 报告输出到 <项目根>/LycheeMapFix_20260611C_report.txt
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
    internal static class LycheeMapFix20260611C
    {
        static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string MarkerPath => Path.Combine(Root, "LycheeMapFix_20260611C.done");
        static string ReportPath => Path.Combine(Root, "LycheeMapFix_20260611C_report.txt");

        [MenuItem("Tools/Lychee/修复地图 20260611C（建筑周边树净空）")]
        public static void RunMenu() { Run(true); }

        public static void Run(bool force)
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.name != "LycheeReplayDemo") { Debug.LogWarning("[LycheeFixC] 请先打开 LycheeReplayDemo"); return; }
            if (!force && File.Exists(MarkerPath)) return;

            var report = new StringBuilder();
            report.AppendLine("LycheeMapFix_20260611C 报告 " + DateTime.Now.ToString("HH:mm:ss"));
            try
            {
                var roots = scene.GetRootGameObjects();

                // 河道中心线
                var rivers = new List<Vector3[]>();
                foreach (var rn in new[] { "E12_Route", "E13_Route" })
                {
                    var t = FindDeep(roots, rn);
                    if (t == null) continue;
                    var v = t.GetComponent<MeshFilter>().sharedMesh.vertices;
                    var c = new Vector3[v.Length / 2];
                    for (int i = 0; i < c.Length; i++)
                        c[i] = (t.TransformPoint(v[i * 2]) + t.TransformPoint(v[i * 2 + 1])) * 0.5f;
                    rivers.Add(c);
                }
                Func<Vector3, float> riverDist = p =>
                {
                    float best = float.MaxValue;
                    foreach (var line in rivers)
                        for (int i = 0; i + 1 < line.Length; i++)
                        {
                            Vector2 a = new Vector2(line[i].x, line[i].z), b = new Vector2(line[i + 1].x, line[i + 1].z);
                            Vector2 ab = b - a; float l2 = ab.sqrMagnitude;
                            float tt = l2 < 1e-8f ? 0 : Mathf.Clamp01(Vector2.Dot(new Vector2(p.x, p.z) - a, ab) / l2);
                            best = Mathf.Min(best, Vector2.Distance(new Vector2(p.x, p.z), a + ab * tt));
                        }
                    return best;
                };
                Func<Vector3, Vector3> riverPush = p =>
                {
                    // 把点推到距最近河道中心线 2.2 处
                    float best = float.MaxValue; Vector2 bq = Vector2.zero;
                    foreach (var line in rivers)
                        for (int i = 0; i + 1 < line.Length; i++)
                        {
                            Vector2 a = new Vector2(line[i].x, line[i].z), b = new Vector2(line[i + 1].x, line[i + 1].z);
                            Vector2 ab = b - a; float l2 = ab.sqrMagnitude;
                            float tt = l2 < 1e-8f ? 0 : Mathf.Clamp01(Vector2.Dot(new Vector2(p.x, p.z) - a, ab) / l2);
                            Vector2 q = a + ab * tt; float d = Vector2.Distance(new Vector2(p.x, p.z), q);
                            if (d < best) { best = d; bq = q; }
                        }
                    if (best >= 2.2f) return p;
                    Vector2 dir = (new Vector2(p.x, p.z) - bq);
                    dir = dir.sqrMagnitude < 1e-6f ? Vector2.up : dir.normalized;
                    Vector2 np = bq + dir * 2.2f;
                    return new Vector3(np.x, p.y, np.y);
                };

                var terrain = FindDeep(roots, "Continuous_Terrain");
                var tcol = terrain != null ? terrain.GetComponent<MeshCollider>() : null;
                Func<Vector3, float> groundY = p =>
                {
                    if (tcol != null)
                    {
                        var ray = new Ray(new Vector3(p.x, 50f, p.z), Vector3.down);
                        RaycastHit hit;
                        if (tcol.Raycast(ray, out hit, 200f)) return hit.point.y;
                    }
                    return p.y;
                };

                // 树列表
                var trees = new List<Transform>();
                foreach (var g in new[] { "Tree_Clusters", "Lingnan_Orchard_Tree_Groves" })
                {
                    var t = FindDeep(roots, g);
                    if (t == null) continue;
                    foreach (Transform c in t) trees.Add(c);
                }

                // 锚点与建筑包围盒
                var anchors = FindAllDeep(roots, n => n.Contains("_ANCHOR_"));
                int cleared = 0;
                var protectCenters = new List<Vector3>();
                var protectRadii = new List<float>();
                foreach (var a in anchors)
                {
                    Transform model = null;
                    foreach (Transform c in a) if (c.name == "Imported_Station_Model") { model = c; break; }
                    if (model == null) continue;
                    var rs = model.GetComponentsInChildren<Renderer>(true);
                    if (rs.Length == 0) continue;
                    Bounds b = rs[0].bounds;
                    for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                    float radius = Mathf.Max(b.extents.x, b.extents.z) + 1.2f;
                    protectCenters.Add(b.center); protectRadii.Add(radius);

                    foreach (var tr in trees)
                    {
                        Vector2 d2 = new Vector2(tr.position.x - b.center.x, tr.position.z - b.center.z);
                        float d = d2.magnitude;
                        if (d < radius)
                        {
                            Vector2 dir = d < 1e-4f ? Vector2.up : d2 / d;
                            Vector2 np = new Vector2(b.center.x, b.center.z) + dir * (radius + 0.15f);
                            Vector3 p = new Vector3(np.x, tr.position.y, np.y);
                            p = riverPush(p);
                            p.y = groundY(p) - 0.02f;
                            tr.position = p;
                            cleared++;
                        }
                    }
                }
                report.AppendLine("建筑净空外移树木 " + cleared + " 棵（半径=包围盒+1.2）");

                // S04/S05 周边再做间距松弛
                foreach (var key in new[] { "S04", "S05" })
                {
                    var anchor = anchors.FirstOrDefault(x => x.name.StartsWith(key));
                    if (anchor == null) continue;
                    Vector2 ac = new Vector2(anchor.position.x, anchor.position.z);
                    var local = trees.Where(t => Vector2.Distance(new Vector2(t.position.x, t.position.z), ac) < 9f).ToList();
                    for (int iter = 0; iter < 50; iter++)
                    {
                        bool moved = false;
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
                                    moved = true;
                                }
                            }
                        foreach (var t in local)
                        {
                            Vector3 p = riverPush(t.position);
                            // 建筑净空保持
                            for (int k = 0; k < protectCenters.Count; k++)
                            {
                                Vector2 d2 = new Vector2(p.x - protectCenters[k].x, p.z - protectCenters[k].z);
                                float d = d2.magnitude;
                                if (d < protectRadii[k])
                                {
                                    Vector2 dir = d < 1e-4f ? Vector2.up : d2 / d;
                                    Vector2 np = new Vector2(protectCenters[k].x, protectCenters[k].z) + dir * (protectRadii[k] + 0.15f);
                                    p = new Vector3(np.x, p.y, np.y);
                                    moved = true;
                                }
                            }
                            if ((p - t.position).sqrMagnitude > 1e-6f) { t.position = p; }
                        }
                        if (!moved) break;
                    }
                    foreach (var t in local)
                    {
                        Vector3 p = t.position;
                        t.position = new Vector3(p.x, groundY(p) - 0.02f, p.z);
                    }
                    report.AppendLine(key + " 周边松弛 " + local.Count + " 棵");
                }

                File.WriteAllText(MarkerPath, DateTime.Now.ToString("o"));
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                report.AppendLine("场景已保存。");
                // 自动触发重新截图
                File.WriteAllText(Path.Combine(Root, "LycheeMapCapture_20260611.request"), DateTime.Now.ToString("o"));
                LycheeMapCapture20260611.Capture();
            }
            catch (Exception ex)
            {
                report.AppendLine("异常: " + ex);
                Debug.LogError("[LycheeFixC] " + ex);
            }
            finally
            {
                File.WriteAllText(ReportPath, report.ToString());
            }
        }

        static Transform FindDeep(IEnumerable<GameObject> roots, string name)
        {
            foreach (var r in roots)
            {
                if (r == null) continue;
                foreach (var t in r.GetComponentsInChildren<Transform>(true))
                    if (t.name == name) return t;
            }
            return null;
        }

        static List<Transform> FindAllDeep(IEnumerable<GameObject> roots, Func<string, bool> match)
        {
            var list = new List<Transform>();
            foreach (var r in roots)
            {
                if (r == null) continue;
                foreach (var t in r.GetComponentsInChildren<Transform>(true))
                    if (match(t.name)) list.Add(t);
            }
            return list;
        }
    }

    [InitializeOnLoad]
    internal static class LycheeMapFix20260611CAutoRun
    {
        static LycheeMapFix20260611CAutoRun()
        {
            EditorApplication.delayCall += () => EditorApplication.delayCall += TryRun;
        }

        static void TryRun()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (File.Exists(Path.Combine(root, "LycheeMapFix_20260611C.done"))) return;
            if (!File.Exists(Path.Combine(root, "LycheeMapFix_20260611B.done"))) return; // 必须在 B 之后
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            if (SceneManager.GetActiveScene().name != "LycheeReplayDemo") return;
            LycheeMapFix20260611C.Run(false);
        }
    }
}
