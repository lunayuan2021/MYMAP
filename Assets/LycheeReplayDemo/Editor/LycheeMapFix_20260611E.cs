// 第四轮修复（2026-06-11 批次 E）——向参考图风格靠拢：
// 1) 建筑整体放大：目标足印 clamp(0.80×最近邻距, 2.3, 4.6)；起终点地标(S01/S15)额外加成
// 2) 陆地节点建筑包围盒中心对齐到锚点坐标（水驿 S04/S05 保持入水布局，仅放大）
// 3) S06 五岭山道：按道路切线旋转，使山体移出路面（山体垂直于道路方向）
// 4) 路线彩色发光带：官道金黄 / 山道青绿 / 链路紫，叠加在路面上方 0.05
// 5) 每个节点添加发光圆环底盘（参考图节点光圈）
// 报告输出到 <项目根>/LycheeMapFix_20260611E_report.txt
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
    internal static class LycheeMapFix20260611E
    {
        static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string MarkerPath => Path.Combine(Root, "LycheeMapFix_20260611E.done");
        static string ReportPath => Path.Combine(Root, "LycheeMapFix_20260611E_report.txt");
        const string GlowGroupName = "Route_Color_Glow_20260611E";
        const string RingName = "Node_GlowRing_20260611E";

        [MenuItem("Tools/Lychee/修复地图 20260611E（放大对中+山道朝向+路线高亮）")]
        public static void RunMenu() { Run(true); }

        public static void Run(bool force)
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.name != "LycheeReplayDemo") return;
            if (!force && File.Exists(MarkerPath)) return;
            var report = new StringBuilder();
            report.AppendLine("LycheeMapFix_20260611E 报告 " + DateTime.Now.ToString("HH:mm:ss"));
            try
            {
                var roots = scene.GetRootGameObjects();
                var all = new List<Transform>();
                foreach (var r in roots)
                {
                    if (r == null) continue;
                    all.AddRange(r.GetComponentsInChildren<Transform>(true));
                }
                Func<string, Transform> find = n => all.FirstOrDefault(t => t != null && t.name == n);

                // 地形高度
                var terrain = find("Continuous_Terrain");
                var tcol = terrain.GetComponent<MeshCollider>();
                Func<float, float, float> groundY = (x, z) =>
                {
                    RaycastHit hit;
                    if (tcol.Raycast(new Ray(new Vector3(x, 60, z), Vector3.down), out hit, 200f)) return hit.point.y;
                    return 0f;
                };

                // 河道中心线（求水面高度用）
                var rivers = new List<Vector3[]>();
                foreach (var rn in new[] { "E12_Route", "E13_Route" })
                {
                    var rt = find(rn);
                    if (rt == null) continue;
                    var v = rt.GetComponent<MeshFilter>().sharedMesh.vertices;
                    var c = new Vector3[v.Length / 2];
                    for (int i = 0; i < c.Length; i++)
                        c[i] = (rt.TransformPoint(v[i * 2]) + rt.TransformPoint(v[i * 2 + 1])) * 0.5f;
                    rivers.Add(c);
                }
                Func<Vector3, float> waterYNear = p =>
                {
                    float best = float.MaxValue; float wy = -0.85f;
                    foreach (var line in rivers)
                        for (int i = 0; i < line.Length; i++)
                        {
                            float d = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(line[i].x, line[i].z));
                            if (d < best) { best = d; wy = line[i].y; }
                        }
                    return wy;
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

                // ---------- 1+2: 放大与对中 ----------
                report.AppendLine("--- 放大与对中 ---");
                foreach (var a in anchors)
                {
                    var m = modelOf(a);
                    if (m == null) continue;
                    string key = a.name.Substring(0, 3);
                    bool isWater = key == "S04" || key == "S05";

                    float minNeighbor = float.MaxValue;
                    foreach (var o in anchors)
                    {
                        if (o == a) continue;
                        minNeighbor = Mathf.Min(minNeighbor, Vector2.Distance(
                            new Vector2(a.position.x, a.position.z), new Vector2(o.position.x, o.position.z)));
                    }
                    Bounds wb = wbOf(m);
                    float footprint = Mathf.Max(wb.size.x, wb.size.z);
                    float target = Mathf.Clamp(0.80f * minNeighbor, 2.3f, 4.6f);
                    if (key == "S15" || key == "S01") target *= 1.30f;   // 起终点地标
                    if (key == "S14") target *= 1.10f;                   // 朱雀门
                    float f = Mathf.Clamp(target / Mathf.Max(footprint, 0.01f), 0.8f, 2.4f);
                    if (Mathf.Abs(f - 1f) > 0.04f)
                    {
                        Vector3 c = wb.center;
                        m.localScale *= f;
                        // 围绕包围盒中心水平缩放：位置补偿
                        Vector3 np = c + (m.position - c) * f;
                        m.position = new Vector3(np.x, m.position.y, np.z);
                    }
                    wb = wbOf(m);

                    if (!isWater)
                    {
                        // 对中：包围盒中心 XZ 对齐锚点
                        Vector3 dxz = new Vector3(a.position.x - wb.center.x, 0, a.position.z - wb.center.z);
                        m.position += dxz;
                        wb = wbOf(m);
                        // 贴地
                        float g = groundY(wb.center.x, wb.center.z);
                        m.position += new Vector3(0, (g - 0.04f) - wb.min.y, 0);
                    }
                    else
                    {
                        // 水驿：保持布局，仅恢复基座到 水面-0.08
                        float wy = waterYNear(wb.center);
                        m.position += new Vector3(0, (wy - 0.08f) - wb.min.y, 0);
                    }
                    report.AppendLine(string.Format("{0}: 足印 {1:F2}->{2:F2} (因子 {3:F2}) 对中={4}",
                        key, footprint, Mathf.Max(wbOf(m).size.x, wbOf(m).size.z), f, !isWater));
                }

                // ---------- 3: S06 五岭山道朝向 ----------
                report.AppendLine("--- S06 五岭山道 ---");
                var s06 = anchors.FirstOrDefault(t => t.name.StartsWith("S06"));
                var m06 = modelOf(s06);
                if (m06 != null)
                {
                    Bounds b6 = wbOf(m06);
                    // 道路切线：取山道组路面顶点中距锚点 6 以内者做 PCA 主方向
                    var pts = new List<Vector2>();
                    foreach (var grp in new[] { "MountainRoute", "OfficialRoad", "BranchRoute" })
                    {
                        var g = find(grp);
                        if (g == null) continue;
                        foreach (var mf in g.GetComponentsInChildren<MeshFilter>(true))
                        {
                            if (mf.sharedMesh == null || !mf.name.Contains("Route")) continue;
                            var vs = mf.sharedMesh.vertices;
                            for (int i = 0; i < vs.Length; i++)
                            {
                                Vector3 w = mf.transform.TransformPoint(vs[i]);
                                if (Vector2.Distance(new Vector2(w.x, w.z), new Vector2(s06.position.x, s06.position.z)) < 6f)
                                    pts.Add(new Vector2(w.x, w.z));
                            }
                        }
                    }
                    if (pts.Count >= 8)
                    {
                        Vector2 mean = Vector2.zero; foreach (var p in pts) mean += p; mean /= pts.Count;
                        float sxx = 0, sxz = 0, szz = 0;
                        foreach (var p in pts) { var d = p - mean; sxx += d.x * d.x; sxz += d.x * d.y; szz += d.y * d.y; }
                        float theta = 0.5f * Mathf.Atan2(2 * sxz, sxx - szz);
                        Vector3 roadT = new Vector3(Mathf.Cos(theta), 0, Mathf.Sin(theta));

                        // 模型高低簇
                        Vector2 tallC, lowC;
                        if (HeightClusters(m06, b6, out tallC, out lowC))
                        {
                            Vector3 mountainDir = new Vector3(tallC.x - b6.center.x, 0, tallC.y - b6.center.z);
                            if (mountainDir.sqrMagnitude < 1e-4f) mountainDir = new Vector3(tallC.x - lowC.x, 0, tallC.y - lowC.y);
                            mountainDir.Normalize();
                            Vector3 N = Vector3.Cross(Vector3.up, roadT).normalized;
                            // 选与当前山体方向夹角小的一侧，减少旋转量
                            Vector3 targetDir = Vector3.Dot(mountainDir, N) >= 0 ? N : -N;
                            float ang = Vector3.SignedAngle(mountainDir, targetDir, Vector3.up);
                            if (Mathf.Abs(ang) > 6f)
                            {
                                m06.RotateAround(b6.center, Vector3.up, ang);
                                report.AppendLine(string.Format("S06: 山体偏离路法线 {0:F0}°，已旋转（道路切线 {1:F0}°）", ang, theta * Mathf.Rad2Deg));
                            }
                            else report.AppendLine("S06: 山体朝向已符合（偏差 " + ang.ToString("F0") + "°）");
                            // 旋转后重新贴地
                            Bounds nb = wbOf(m06);
                            float g6 = groundY(nb.center.x, nb.center.z);
                            m06.position += new Vector3(0, (g6 - 0.04f) - nb.min.y, 0);
                        }
                        else report.AppendLine("S06: 高低簇判定失败，未旋转");
                    }
                    else report.AppendLine("S06: 周边道路顶点不足，未旋转");
                }

                // ---------- 标签重算 ----------
                foreach (var a in anchors)
                {
                    var m = modelOf(a); if (m == null) continue;
                    Transform label = null;
                    foreach (Transform c in a) if (c.name == "Node_Name_CN") { label = c; break; }
                    if (label == null) continue;
                    Bounds wb = wbOf(m);
                    var lp = label.localPosition;
                    label.localPosition = new Vector3(lp.x, (wb.max.y - a.position.y) + 0.75f, lp.z);
                }

                // ---------- 4: 路线彩色发光带 ----------
                var old = find(GlowGroupName);
                if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);
                var glowGroup = new GameObject(GlowGroupName);
                var mapRoot = roots.FirstOrDefault(r => r != null && r.name.StartsWith("LYCHEE_CONFIG_MAP"));
                if (mapRoot != null) glowGroup.transform.SetParent(mapRoot.transform, true);

                var routeColors = new Dictionary<string, Color> {
                    { "OfficialRoad",  new Color(1.00f, 0.78f, 0.25f) },
                    { "MountainRoute", new Color(0.55f, 0.85f, 0.35f) },
                    { "BranchRoute",   new Color(0.75f, 0.55f, 0.95f) },
                };
                int ribbons = 0;
                foreach (var kv in routeColors)
                {
                    var g = find(kv.Key);
                    if (g == null) continue;
                    var mat = MakeGlowMat("M_RouteGlow_" + kv.Key, kv.Value, 0.45f);
                    foreach (var mf in g.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (mf.sharedMesh == null) continue;
                        if (!mf.name.EndsWith("_Route")) continue;
                        var go = new GameObject(mf.name + "_Glow");
                        go.transform.SetParent(glowGroup.transform, true);
                        go.transform.position = mf.transform.position + new Vector3(0, 0.05f, 0);
                        go.transform.rotation = mf.transform.rotation;
                        go.transform.localScale = mf.transform.lossyScale;
                        go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                        var mr = go.AddComponent<MeshRenderer>();
                        mr.sharedMaterial = mat;
                        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        ribbons++;
                    }
                }
                report.AppendLine("路线发光带 " + ribbons + " 段");

                // ---------- 5: 节点光圈 ----------
                var ringMat = MakeGlowMat("M_NodeGlowRing", new Color(1.0f, 0.85f, 0.45f), 0.55f);
                int rings = 0;
                foreach (var a in anchors)
                {
                    Transform oldRing = null;
                    foreach (Transform c in a) if (c.name == RingName) { oldRing = c; break; }
                    if (oldRing != null) UnityEngine.Object.DestroyImmediate(oldRing.gameObject);

                    var m = modelOf(a);
                    Bounds wb = m != null ? wbOf(m) : new Bounds(a.position, Vector3.one);
                    float radius = Mathf.Max(wb.extents.x, wb.extents.z) * 0.85f + 0.4f;
                    var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    ring.name = RingName;
                    var col = ring.GetComponent<Collider>();
                    if (col != null) UnityEngine.Object.DestroyImmediate(col);
                    ring.transform.SetParent(a, true);
                    float gy = groundY(a.position.x, a.position.z);
                    ring.transform.position = new Vector3(a.position.x, gy + 0.035f, a.position.z);
                    ring.transform.localScale = new Vector3(radius * 2f, 0.012f, radius * 2f);
                    var rr = ring.GetComponent<MeshRenderer>();
                    rr.sharedMaterial = ringMat;
                    rr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    rings++;
                }
                report.AppendLine("节点光圈 " + rings + " 个");

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
                Debug.LogError("[LycheeFixE] " + ex);
            }
            finally
            {
                File.WriteAllText(ReportPath, report.ToString());
            }
        }

        static bool HeightClusters(Transform model, Bounds wb, out Vector2 tallC, out Vector2 lowC)
        {
            tallC = Vector2.zero; lowC = Vector2.zero;
            var cells = new Dictionary<Vector2Int, float>();
            var cellCnt = new Dictionary<Vector2Int, int>();
            float cell = Mathf.Max(wb.size.x, wb.size.z) / 12f + 1e-4f;
            float minY = float.MaxValue, maxY = float.MinValue;
            try
            {
                foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
                {
                    var mesh = mf.sharedMesh; if (mesh == null) continue;
                    var v = mesh.vertices;
                    int stride = Mathf.Max(1, v.Length / 30000);
                    var mtx = mf.transform.localToWorldMatrix;
                    for (int i = 0; i < v.Length; i += stride)
                    {
                        Vector3 w = mtx.MultiplyPoint3x4(v[i]);
                        var c = new Vector2Int(Mathf.FloorToInt((w.x - wb.min.x) / cell), Mathf.FloorToInt((w.z - wb.min.z) / cell));
                        float h; cells.TryGetValue(c, out h);
                        if (!cells.ContainsKey(c) || w.y > h) cells[c] = w.y;
                        int cnt; cellCnt.TryGetValue(c, out cnt); cellCnt[c] = cnt + 1;
                        if (w.y < minY) minY = w.y;
                        if (w.y > maxY) maxY = w.y;
                    }
                }
            }
            catch { return false; }
            if (cells.Count < 8) return false;
            float range = Mathf.Max(maxY - minY, 1e-4f);
            float tw = 0, lw = 0;
            Vector2 tc = Vector2.zero, lc = Vector2.zero;
            foreach (var kv in cells)
            {
                Vector2 ctr = new Vector2(wb.min.x + (kv.Key.x + 0.5f) * cell, wb.min.z + (kv.Key.y + 0.5f) * cell);
                float rel = (kv.Value - minY) / range;
                int cnt = cellCnt[kv.Key];
                if (rel >= 0.60f) { tc += ctr * cnt; tw += cnt; }
                else if (rel <= 0.30f && cnt >= 4) { lc += ctr; lw += 1; }
            }
            if (tw < 1 || lw < 1) return false;
            tallC = tc / tw; lowC = lc / lw;
            return true;
        }

        static Material MakeGlowMat(string name, Color c, float alpha)
        {
            string path = "Assets/LycheeMap15/Materials/" + name + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetFloat("_Surface", 1f);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0f);
            mat.renderQueue = 3000;
            mat.SetColor("_BaseColor", new Color(c.r, c.g, c.b, alpha));
            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            mat.SetColor("_EmissionColor", c * 1.6f);
            mat.SetFloat("_Smoothness", 0.1f);
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }

    [InitializeOnLoad]
    internal static class LycheeMapFix20260611EAutoRun
    {
        static LycheeMapFix20260611EAutoRun()
        {
            EditorApplication.delayCall += () => EditorApplication.delayCall += TryRun;
        }

        static void TryRun()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (File.Exists(Path.Combine(root, "LycheeMapFix_20260611E.done"))) return;
            if (!File.Exists(Path.Combine(root, "LycheeMapFix_20260611D.done"))) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            if (SceneManager.GetActiveScene().name != "LycheeReplayDemo") return;
            LycheeMapFix20260611E.Run(false);
        }
    }
}
