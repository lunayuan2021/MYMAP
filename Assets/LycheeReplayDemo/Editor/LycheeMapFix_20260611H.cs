// 第七轮修复（2026-06-11 批次 H）：
// 1) S04/S05 水驿中心回调为 锚点 − 0.6×水向（房屋在岸、船端入水），重做码头平台
// 2) 旗帜位置加河道净空（四个对角方向中选离水最远者）
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
    internal static class LycheeMapFix20260611H
    {
        static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string MarkerPath => Path.Combine(Root, "LycheeMapFix_20260611H.done");
        static string ReportPath => Path.Combine(Root, "LycheeMapFix_20260611H_report.txt");
        const string TerrainPath = "Assets/LycheeMap15/GeneratedMeshes/FinalRiver/Terrain_RiverSandSubmesh_v5_20260611.asset";

        static readonly Dictionary<string, Vector2> NodeXZ = new Dictionary<string, Vector2> {
            {"S04",new Vector2(-18,-22)},{"S05",new Vector2(-2,-18)},
        };

        [MenuItem("Tools/Lychee/修复地图 20260611H（水驿回调+旗帜净空）")]
        public static void RunMenu() { Run(true); }

        public static void Run(bool force)
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.name != "LycheeReplayDemo") return;
            if (!force && File.Exists(MarkerPath)) return;
            var report = new StringBuilder();
            report.AppendLine("LycheeMapFix_20260611H 报告 " + DateTime.Now.ToString("HH:mm:ss"));
            try
            {
                var roots = scene.GetRootGameObjects();
                var all = new List<Transform>();
                foreach (var r in roots) { if (r != null) all.AddRange(r.GetComponentsInChildren<Transform>(true)); }
                Func<string, Transform> find = n => all.FirstOrDefault(t => t != null && t.name == n);

                var terrainT = find("Continuous_Terrain");
                var tcol = terrainT.GetComponent<MeshCollider>();
                Func<float, float, float> groundY = (x, z) =>
                {
                    RaycastHit hit;
                    if (tcol.Raycast(new Ray(new Vector3(x, 60, z), Vector3.down), out hit, 200f)) return hit.point.y;
                    return 0f;
                };

                // 河道中心线
                var riverPts = new List<Vector3>();
                Func<string, List<Vector3>> centerlineOf = en =>
                {
                    var t = find(en);
                    if (t == null) return null;
                    var v = t.GetComponent<MeshFilter>().sharedMesh.vertices;
                    var pts = new List<Vector3>(v.Length / 2);
                    for (int i = 0; i + 1 < v.Length; i += 2)
                        pts.Add((t.TransformPoint(v[i]) + t.TransformPoint(v[i + 1])) * 0.5f);
                    return pts;
                };
                var c12 = centerlineOf("E12_Route");
                var c13 = centerlineOf("E13_Route");
                if (c12 != null) riverPts.AddRange(c12);
                if (c13 != null) riverPts.AddRange(c13);
                Func<Vector2, float> riverDist = p =>
                {
                    float best = float.MaxValue;
                    foreach (var rp in riverPts)
                        best = Mathf.Min(best, Vector2.Distance(p, new Vector2(rp.x, rp.z)));
                    return best;
                };

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
                Func<Transform, Vector2> centroidOf = m =>
                {
                    double sx = 0, sz = 0; long n = 0;
                    foreach (var mf in m.GetComponentsInChildren<MeshFilter>(true))
                    {
                        var mesh = mf.sharedMesh; if (mesh == null) continue;
                        var v = mesh.vertices;
                        int stride = Mathf.Max(1, v.Length / 20000);
                        var mtx = mf.transform.localToWorldMatrix;
                        for (int i = 0; i < v.Length; i += stride)
                        {
                            Vector3 w = mtx.MultiplyPoint3x4(v[i]);
                            sx += w.x; sz += w.z; n++;
                        }
                    }
                    return n > 0 ? new Vector2((float)(sx / n), (float)(sz / n)) : Vector2.zero;
                };

                // ---------- 水驿回调 ----------
                var quayCenters = new List<Vector3>();
                foreach (var key in new[] { "S04", "S05" })
                {
                    var a = anchors.FirstOrDefault(t => t.name.StartsWith(key));
                    if (a == null) continue;
                    var m = modelOf(a); if (m == null) continue;
                    Vector2 target = NodeXZ[key];

                    Vector3 dirWater; float waterY;
                    if (key == "S04")
                    {
                        Vector3 p0 = c12[0], p1 = c12[1];
                        if (Vector2.Distance(new Vector2(c12[c12.Count - 1].x, c12[c12.Count - 1].z), target) <
                            Vector2.Distance(new Vector2(p0.x, p0.z), target))
                        { p0 = c12[c12.Count - 1]; p1 = c12[c12.Count - 2]; }
                        dirWater = new Vector3(p1.x - p0.x, 0, p1.z - p0.z).normalized;
                        waterY = p0.y;
                    }
                    else
                    {
                        Vector3 e12end = c12[Vector2.Distance(new Vector2(c12[0].x, c12[0].z), target) < 1f ? 1 : c12.Count - 2];
                        Vector3 e13next = c13[Vector2.Distance(new Vector2(c13[0].x, c13[0].z), target) < 1f ? 1 : c13.Count - 2];
                        Vector3 d1 = new Vector3(e12end.x - target.x, 0, e12end.z - target.y).normalized;
                        Vector3 d2 = new Vector3(e13next.x - target.x, 0, e13next.z - target.y).normalized;
                        dirWater = (d1 + d2).normalized;
                        if (dirWater.sqrMagnitude < 0.1f) dirWater = d2;
                        waterY = (e12end.y + e13next.y) * 0.5f;
                    }

                    Vector2 c = centroidOf(m);
                    Vector2 want = target - new Vector2(dirWater.x, dirWater.z) * 0.6f;
                    m.position += new Vector3(want.x - c.x, 0, want.y - c.y);
                    Bounds wb = wbOf(m);
                    m.position += new Vector3(0, (waterY - 0.08f) - wb.min.y, 0);
                    Vector2 housePos = target - new Vector2(dirWater.x, dirWater.z) * 2.0f;
                    quayCenters.Add(new Vector3(housePos.x, waterY, housePos.y));
                    report.AppendLine(string.Format("{0}: 中心回调至 锚点-0.6水向 ({1:F2},{2:F2})", key, want.x, want.y));

                    // 标签
                    Transform label = null;
                    foreach (Transform ch in a) if (ch.name == "Node_Name_CN") { label = ch; break; }
                    if (label != null)
                    {
                        Bounds wb2 = wbOf(m);
                        var lp = label.localPosition;
                        label.localPosition = new Vector3(lp.x, (wb2.max.y - a.position.y) + 0.75f, lp.z);
                    }
                }

                // 码头平台
                var tmesh = AssetDatabase.LoadAssetAtPath<Mesh>(TerrainPath);
                if (tmesh != null && quayCenters.Count > 0)
                {
                    var tv = tmesh.vertices;
                    int padded = 0;
                    for (int i = 0; i < tv.Length; i++)
                    {
                        foreach (var qc in quayCenters)
                        {
                            float d = Vector2.Distance(new Vector2(tv[i].x, tv[i].z), new Vector2(qc.x, qc.z));
                            if (d > 3.4f) continue;
                            float padY = qc.y + 0.03f;
                            float t = d <= 1.9f ? 0f : 0.5f - 0.5f * Mathf.Cos(Mathf.Clamp01((d - 1.9f) / 1.5f) * Mathf.PI);
                            float ny = Mathf.Lerp(padY, tv[i].y, t);
                            if (ny < tv[i].y) { tv[i].y = ny; padded++; }
                        }
                    }
                    tmesh.vertices = tv;
                    tmesh.RecalculateNormals();
                    tmesh.RecalculateBounds();
                    EditorUtility.SetDirty(tmesh);
                    tcol.sharedMesh = null; tcol.sharedMesh = tmesh;
                    report.AppendLine("码头平台压低顶点 " + padded + " 个");
                }

                // ---------- 旗帜净空 ----------
                int moved = 0;
                var diag = new[] { new Vector3(0.707f,0,0.707f), new Vector3(-0.707f,0,0.707f),
                                   new Vector3(0.707f,0,-0.707f), new Vector3(-0.707f,0,-0.707f) };
                foreach (var a in anchors)
                {
                    Transform flag = null;
                    foreach (Transform ch in a) if (ch.name == "Node_RouteFlag_20260611G") { flag = ch; break; }
                    if (flag == null) continue;
                    Vector2 fp = new Vector2(flag.position.x, flag.position.z);
                    if (riverDist(fp) >= 2.0f) continue;
                    var m = modelOf(a);
                    float ext = 1.6f;
                    if (m != null) { var b = wbOf(m); ext = Mathf.Max(b.extents.x, b.extents.z); }
                    Vector3 best = flag.position; float bestD = riverDist(fp);
                    foreach (var d in diag)
                    {
                        Vector3 cand = a.position + d * (ext + 0.5f);
                        float rd = riverDist(new Vector2(cand.x, cand.z));
                        if (rd > bestD) { bestD = rd; best = cand; }
                    }
                    flag.position = new Vector3(best.x, groundY(best.x, best.z), best.z);
                    moved++;
                }
                report.AppendLine("旗帜移位 " + moved + " 面");

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
                Debug.LogError("[LycheeFixH] " + ex);
            }
            finally
            {
                File.WriteAllText(ReportPath, report.ToString());
            }
        }
    }

    [InitializeOnLoad]
    internal static class LycheeMapFix20260611HAutoRun
    {
        static LycheeMapFix20260611HAutoRun()
        {
            EditorApplication.delayCall += () => EditorApplication.delayCall += TryRun;
        }

        static void TryRun()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (File.Exists(Path.Combine(root, "LycheeMapFix_20260611H.done"))) return;
            if (!File.Exists(Path.Combine(root, "LycheeMapFix_20260611G.done"))) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            if (SceneManager.GetActiveScene().name != "LycheeReplayDemo") return;
            LycheeMapFix20260611H.Run(false);
        }
    }
}
