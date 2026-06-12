// 第五轮修复（2026-06-11 批次 F）：
// 1) 路线带改为不透明实色（参考图的实色路线带），弱发光，消除白色过曝
// 2) 节点光圈缩小到 [1.0,1.8]，金色不透明弱发光
// 3) 建筑放大后重新做树木净空（新包围盒 + 0.8）
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
    internal static class LycheeMapFix20260611F
    {
        static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string MarkerPath => Path.Combine(Root, "LycheeMapFix_20260611F.done");
        static string ReportPath => Path.Combine(Root, "LycheeMapFix_20260611F_report.txt");

        [MenuItem("Tools/Lychee/修复地图 20260611F（路带配色+光圈+树净空）")]
        public static void RunMenu() { Run(true); }

        public static void Run(bool force)
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.name != "LycheeReplayDemo") return;
            if (!force && File.Exists(MarkerPath)) return;
            var report = new StringBuilder();
            report.AppendLine("LycheeMapFix_20260611F 报告 " + DateTime.Now.ToString("HH:mm:ss"));
            try
            {
                var roots = scene.GetRootGameObjects();
                var all = new List<Transform>();
                foreach (var r in roots) { if (r != null) all.AddRange(r.GetComponentsInChildren<Transform>(true)); }
                Func<string, Transform> find = n => all.FirstOrDefault(t => t != null && t.name == n);

                // 1) 路线带材质改为不透明实色
                var colors = new Dictionary<string, Color> {
                    { "M_RouteGlow_OfficialRoad",  new Color(0.93f, 0.72f, 0.23f) },
                    { "M_RouteGlow_MountainRoute", new Color(0.48f, 0.76f, 0.30f) },
                    { "M_RouteGlow_BranchRoute",   new Color(0.66f, 0.47f, 0.85f) },
                    { "M_NodeGlowRing",            new Color(0.96f, 0.80f, 0.38f) },
                };
                foreach (var kv in colors)
                {
                    string path = "Assets/LycheeMap15/Materials/" + kv.Key + ".mat";
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (mat == null) continue;
                    mat.shader = Shader.Find("Universal Render Pipeline/Lit");
                    mat.SetFloat("_Surface", 0f);   // 不透明
                    mat.SetOverrideTag("RenderType", "Opaque");
                    mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                    mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
                    mat.SetFloat("_ZWrite", 1f);
                    mat.renderQueue = -1;
                    mat.SetColor("_BaseColor", kv.Value);
                    mat.EnableKeyword("_EMISSION");
                    mat.SetColor("_EmissionColor", kv.Value * 0.25f);
                    mat.SetFloat("_Smoothness", 0.15f);
                    EditorUtility.SetDirty(mat);
                }
                report.AppendLine("材质改为不透明实色 " + colors.Count + " 个");

                // 2) 光圈缩小
                var anchors = all.Where(t => t != null && t.name.Contains("_ANCHOR_")).ToList();
                Func<Transform, Transform> modelOf = a =>
                {
                    foreach (Transform c in a) if (c.name == "Imported_Station_Model") return c;
                    return null;
                };
                Func<Transform, Bounds> wbOf = m =>
                {
                    var rs = m.GetComponentsInChildren<Renderer>(true);
                    Bounds b = rs[0].bounds;
                    for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                    return b;
                };
                int rings = 0;
                foreach (var a in anchors)
                {
                    Transform ring = null;
                    foreach (Transform c in a) if (c.name == "Node_GlowRing_20260611E") { ring = c; break; }
                    if (ring == null) continue;
                    var m = modelOf(a);
                    float ext = 1.5f;
                    if (m != null) { var b = wbOf(m); ext = Mathf.Max(b.extents.x, b.extents.z); }
                    float radius = Mathf.Clamp(ext * 0.5f + 0.3f, 1.0f, 1.8f);
                    ring.localScale = new Vector3(radius * 2f, 0.012f, radius * 2f);
                    rings++;
                }
                report.AppendLine("光圈缩小 " + rings + " 个");

                // 3) 树木净空（新包围盒）
                var terrain = find("Continuous_Terrain");
                var tcol = terrain.GetComponent<MeshCollider>();
                Func<Vector2, float> groundY = p =>
                {
                    RaycastHit hit;
                    if (tcol.Raycast(new Ray(new Vector3(p.x, 60, p.y), Vector3.down), out hit, 200f)) return hit.point.y;
                    return 0f;
                };
                var rivers = new List<Vector3[]>();
                foreach (var rn in new[] { "E12_Route", "E13_Route" })
                {
                    var rt = find(rn); if (rt == null) continue;
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
                            Vector2 aa = new Vector2(line[i].x, line[i].z), bb = new Vector2(line[i + 1].x, line[i + 1].z);
                            Vector2 ab = bb - aa; float l2 = ab.sqrMagnitude;
                            float tt = l2 < 1e-8f ? 0 : Mathf.Clamp01(Vector2.Dot(p - aa, ab) / l2);
                            Vector2 q = aa + ab * tt; float d = Vector2.Distance(p, q);
                            if (d < best) { best = d; bq = q; }
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
                int cleared = 0;
                foreach (var a in anchors)
                {
                    var m = modelOf(a); if (m == null) continue;
                    Bounds b = wbOf(m);
                    float radius = Mathf.Max(b.extents.x, b.extents.z) + 0.8f;
                    Vector2 mc = new Vector2(b.center.x, b.center.z);
                    foreach (var tr in trees)
                    {
                        Vector2 p = new Vector2(tr.position.x, tr.position.z);
                        float d = Vector2.Distance(p, mc);
                        if (d < radius)
                        {
                            Vector2 dir = d < 1e-4f ? Vector2.up : (p - mc) / d;
                            Vector2 np = awayFromRiver(mc + dir * (radius + 0.2f));
                            tr.position = new Vector3(np.x, groundY(np) - 0.02f, np.y);
                            cleared++;
                        }
                    }
                }
                report.AppendLine("树木净空外移 " + cleared + " 棵");

                File.WriteAllText(MarkerPath, DateTime.Now.ToString("o"));
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                AssetDatabase.SaveAssets();
                report.AppendLine("场景已保存。");
                File.WriteAllText(Path.Combine(Root, "LycheeMapCapture_20260611.request"), DateTime.Now.ToString("o"));
                LycheeMapCapture20260611.Capture();
            }
            catch (Exception ex)
            {
                report.AppendLine("异常: " + ex);
                Debug.LogError("[LycheeFixF] " + ex);
            }
            finally
            {
                File.WriteAllText(ReportPath, report.ToString());
            }
        }
    }

    [InitializeOnLoad]
    internal static class LycheeMapFix20260611FAutoRun
    {
        static LycheeMapFix20260611FAutoRun()
        {
            EditorApplication.delayCall += () => EditorApplication.delayCall += TryRun;
        }

        static void TryRun()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (File.Exists(Path.Combine(root, "LycheeMapFix_20260611F.done"))) return;
            if (!File.Exists(Path.Combine(root, "LycheeMapFix_20260611E.done"))) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            if (SceneManager.GetActiveScene().name != "LycheeReplayDemo") return;
            LycheeMapFix20260611F.Run(false);
        }
    }
}
