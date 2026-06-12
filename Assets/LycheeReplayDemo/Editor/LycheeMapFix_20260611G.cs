// 第六轮修复（2026-06-11 批次 G）——按 ui_map_config JSON 对齐：
// 1) 删除整路面染色，改为按"边"类型的细发光线带（JSON edges：ROAD金黄/WATER蓝/MOUNTAIN绿/BRANCH紫，E17 才是支路紫）
// 2) 每条边沿 from→to 方向放行进箭头（朝长安）
// 3) 每个节点放路线色旗帜（杆+旗面）
// 4) 所有建筑顶点质心对齐 JSON 锚点：陆地站直接对中；S04/S05 旋转使船朝水域方向、中心=锚点+0.8×水向，保持吃水
// 5) 水面换用 IgniteCoders Simple Water Shader 材质（已导入素材包）
// 报告输出到 <项目根>/LycheeMapFix_20260611G_report.txt
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
    internal static class LycheeMapFix20260611G
    {
        static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string MarkerPath => Path.Combine(Root, "LycheeMapFix_20260611G.done");
        static string ReportPath => Path.Combine(Root, "LycheeMapFix_20260611G_report.txt");
        const string OverlayGroup = "Route_Line_Overlay_20260611G";
        const string FlagName = "Node_RouteFlag_20260611G";
        const string MeshDir = "Assets/LycheeMap15/GeneratedMeshes/RouteOverlay_20260611G";
        const string TerrainPath = "Assets/LycheeMap15/GeneratedMeshes/FinalRiver/Terrain_RiverSandSubmesh_v5_20260611.asset";

        // JSON edges：edgeId -> (fromNode, toNode, type)
        static readonly (string id, string from, string to, string type)[] Edges = {
            ("E01","S01","S02","ROAD"), ("E02","S02","S03","ROAD"), ("E03","S03","S07","ROAD"),
            ("E04","S07","S09","ROAD"), ("E05","S09","S10","ROAD"), ("E06","S10","S11","ROAD"),
            ("E07","S11","S12","ROAD"), ("E08","S12","S13","ROAD"), ("E09","S13","S14","ROAD"),
            ("E10","S14","S15","ROAD"), ("E11","S02","S04","ROAD"), ("E12","S04","S05","WATER"),
            ("E13","S05","S07","WATER"), ("E14","S09","S11","ROAD"), ("E15","S01","S06","MOUNTAIN"),
            ("E16","S06","S08","MOUNTAIN"), ("E17","S08","S10","BRANCH"),
        };
        // JSON nodes：世界坐标 = (x-40, ·, 30-y)
        static readonly Dictionary<string, Vector2> NodeXZ = new Dictionary<string, Vector2> {
            {"S01",new Vector2(-35,-20)},{"S02",new Vector2(-25,-14)},{"S03",new Vector2(-18,-8)},
            {"S04",new Vector2(-18,-22)},{"S05",new Vector2(-2,-18)},{"S06",new Vector2(-26,-2)},
            {"S07",new Vector2(0,-6)},{"S08",new Vector2(2,6)},{"S09",new Vector2(15,-2)},
            {"S10",new Vector2(22,4)},{"S11",new Vector2(26,8)},{"S12",new Vector2(30,10)},
            {"S13",new Vector2(33,11)},{"S14",new Vector2(36,12)},{"S15",new Vector2(38,12)},
        };
        static readonly Dictionary<string, Color> TypeColor = new Dictionary<string, Color> {
            {"ROAD",     new Color(0.93f, 0.72f, 0.23f)},
            {"WATER",    new Color(0.30f, 0.62f, 0.95f)},
            {"MOUNTAIN", new Color(0.48f, 0.76f, 0.30f)},
            {"BRANCH",   new Color(0.66f, 0.47f, 0.85f)},
        };

        [MenuItem("Tools/Lychee/修复地图 20260611G（JSON对齐+线带箭头旗帜+水面）")]
        public static void RunMenu() { Run(true); }

        public static void Run(bool force)
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.name != "LycheeReplayDemo") return;
            if (!force && File.Exists(MarkerPath)) return;
            var report = new StringBuilder();
            report.AppendLine("LycheeMapFix_20260611G 报告 " + DateTime.Now.ToString("HH:mm:ss"));
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

                // ---------- 中心线提取 ----------
                Func<string, List<Vector3>> centerlineOf = edgeId =>
                {
                    var t = find(edgeId + "_Route");
                    if (t == null) return null;
                    var mf = t.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) return null;
                    var v = mf.sharedMesh.vertices;
                    var pts = new List<Vector3>(v.Length / 2);
                    for (int i = 0; i + 1 < v.Length; i += 2)
                        pts.Add((t.TransformPoint(v[i]) + t.TransformPoint(v[i + 1])) * 0.5f);
                    return pts;
                };

                // ---------- 1+2: 线带与箭头 ----------
                var oldGlow = find("Route_Color_Glow_20260611E");
                if (oldGlow != null) UnityEngine.Object.DestroyImmediate(oldGlow.gameObject);
                var oldOverlay = find(OverlayGroup);
                if (oldOverlay != null) UnityEngine.Object.DestroyImmediate(oldOverlay.gameObject);
                if (!AssetDatabase.IsValidFolder(MeshDir))
                    AssetDatabase.CreateFolder("Assets/LycheeMap15/GeneratedMeshes", "RouteOverlay_20260611G");

                var mapRoot = roots.FirstOrDefault(r => r != null && r.name.StartsWith("LYCHEE_CONFIG_MAP"));
                var overlay = new GameObject(OverlayGroup);
                if (mapRoot != null) overlay.transform.SetParent(mapRoot.transform, true);

                var mats = new Dictionary<string, Material>();
                foreach (var kv in TypeColor)
                    mats[kv.Key] = SolidMat("M_RouteLine_" + kv.Key, kv.Value, kv.Value * 0.35f);
                var arrowMat = SolidMat("M_RouteArrow_White", new Color(0.97f, 0.96f, 0.90f), new Color(0.5f, 0.5f, 0.45f));

                var arrowVerts = new List<Vector3>();
                var arrowTris = new List<int>();
                int lines = 0, arrows = 0;
                foreach (var e in Edges)
                {
                    var pts = centerlineOf(e.id);
                    if (pts == null || pts.Count < 2) { report.AppendLine(e.id + ": 无中心线，跳过"); continue; }
                    // 按 JSON from→to 定向
                    Vector2 fromXZ = NodeXZ[e.from];
                    float dFirst = Vector2.Distance(new Vector2(pts[0].x, pts[0].z), fromXZ);
                    float dLast = Vector2.Distance(new Vector2(pts[pts.Count - 1].x, pts[pts.Count - 1].z), fromXZ);
                    if (dLast < dFirst) pts.Reverse();

                    float lift = e.type == "WATER" ? 0.04f : 0.06f;
                    float width = e.type == "WATER" ? 0.50f : 0.55f;
                    var mesh = BuildRibbon(pts, width, lift);
                    mesh.name = e.id + "_Line";
                    AssetDatabase.CreateAsset(mesh, MeshDir + "/" + mesh.name + ".asset");
                    var go = new GameObject(e.id + "_Line_" + e.type);
                    go.transform.SetParent(overlay.transform, true);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = mats[e.type];
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    lines++;

                    // 箭头：每 3m 一个，朝 to 方向
                    float acc = 0f, next = 1.5f;
                    for (int i = 0; i + 1 < pts.Count; i++)
                    {
                        Vector3 a = pts[i], b = pts[i + 1];
                        float seg = Vector3.Distance(new Vector3(a.x, 0, a.z), new Vector3(b.x, 0, b.z));
                        while (acc + seg >= next)
                        {
                            float t = (next - acc) / Mathf.Max(seg, 1e-4f);
                            Vector3 p = Vector3.Lerp(a, b, t);
                            Vector3 dir = new Vector3(b.x - a.x, 0, b.z - a.z).normalized;
                            AddArrow(arrowVerts, arrowTris, p + new Vector3(0, lift + 0.025f, 0), dir);
                            arrows++;
                            next += 3.0f;
                        }
                        acc += seg;
                    }
                }
                var arrowMesh = new Mesh();
                arrowMesh.name = "RouteArrows_All";
                arrowMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                arrowMesh.SetVertices(arrowVerts);
                arrowMesh.SetTriangles(arrowTris, 0);
                arrowMesh.RecalculateNormals();
                AssetDatabase.CreateAsset(arrowMesh, MeshDir + "/RouteArrows_All.asset");
                var ago = new GameObject("Route_Arrows");
                ago.transform.SetParent(overlay.transform, true);
                ago.AddComponent<MeshFilter>().sharedMesh = arrowMesh;
                var amr = ago.AddComponent<MeshRenderer>();
                amr.sharedMaterial = arrowMat;
                amr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                report.AppendLine("线带 " + lines + " 条，箭头 " + arrows + " 个");

                // ---------- 锚点与模型 ----------
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
                // 顶点采样质心（XZ）
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

                report.AppendLine("--- 对齐 JSON 锚点 ---");
                var quayCenters = new List<Vector3>();
                foreach (var a in anchors)
                {
                    var m = modelOf(a); if (m == null) continue;
                    string key = a.name.Substring(0, 3);
                    Vector2 target = NodeXZ.ContainsKey(key) ? NodeXZ[key] : new Vector2(a.position.x, a.position.z);
                    bool isWater = key == "S04" || key == "S05";

                    if (!isWater)
                    {
                        Vector2 c = centroidOf(m);
                        m.position += new Vector3(target.x - c.x, 0, target.y - c.y);
                        Bounds wb = wbOf(m);
                        float g = groundY(wb.center.x, wb.center.z);
                        m.position += new Vector3(0, (g - 0.04f) - wb.min.y, 0);
                        report.AppendLine(string.Format("{0}: 质心偏移 {1:F2} 已对中", key, Vector2.Distance(c, target)));
                    }
                    else
                    {
                        // 水驿：船朝水域方向，质心 = 锚点 + 0.8×水向
                        var c12 = centerlineOf("E12");
                        var c13 = centerlineOf("E13");
                        Vector3 dirWater;
                        float waterY;
                        if (key == "S04")
                        {
                            // E12 从 S04 出发：取起点端切线（注意 centerlineOf 未按 JSON 翻转，自行判断方向）
                            Vector3 p0 = c12[0], p1 = c12[1];
                            if (Vector2.Distance(new Vector2(c12[c12.Count - 1].x, c12[c12.Count - 1].z), target) <
                                Vector2.Distance(new Vector2(p0.x, p0.z), target))
                            { p0 = c12[c12.Count - 1]; p1 = c12[c12.Count - 2]; }
                            dirWater = new Vector3(p1.x - p0.x, 0, p1.z - p0.z).normalized;
                            waterY = p0.y;
                        }
                        else
                        {
                            // S05 在 E12 末端与 E13 起点的折角处：水向 = 两条水道方向的角平分线
                            Vector3 e12end = c12[Vector2.Distance(new Vector2(c12[0].x, c12[0].z), target) < 1f ? 1 : c12.Count - 2];
                            Vector3 e13next = c13[Vector2.Distance(new Vector2(c13[0].x, c13[0].z), target) < 1f ? 1 : c13.Count - 2];
                            Vector3 d1 = new Vector3(e12end.x - target.x, 0, e12end.z - target.y).normalized;
                            Vector3 d2 = new Vector3(e13next.x - target.x, 0, e13next.z - target.y).normalized;
                            dirWater = (d1 + d2).normalized;
                            if (dirWater.sqrMagnitude < 0.1f) dirWater = d2;
                            waterY = (e12end.y + e13next.y) * 0.5f;
                        }

                        // 高低簇求当前船向
                        Bounds wb0 = wbOf(m);
                        Vector2 tallC, lowC;
                        if (HeightClusters(m, wb0, out tallC, out lowC))
                        {
                            Vector3 boatDir = new Vector3(lowC.x - tallC.x, 0, lowC.y - tallC.y).normalized;
                            float ang = Vector3.SignedAngle(boatDir, dirWater, Vector3.up);
                            if (Mathf.Abs(ang) > 6f) m.RotateAround(wb0.center, Vector3.up, ang);
                        }
                        Vector2 c = centroidOf(m);
                        Vector2 want = target + new Vector2(dirWater.x, dirWater.z) * 0.8f;
                        m.position += new Vector3(want.x - c.x, 0, want.y - c.y);
                        Bounds wb = wbOf(m);
                        m.position += new Vector3(0, (waterY - 0.08f) - wb.min.y, 0);
                        // 房屋簇（背水侧）位置，用于码头平台
                        Vector2 housePos = target - new Vector2(dirWater.x, dirWater.z) * 1.4f;
                        quayCenters.Add(new Vector3(housePos.x, waterY, housePos.y));
                        report.AppendLine(string.Format("{0}: 对齐锚点+0.8水向，水向=({1:F2},{2:F2})", key, dirWater.x, dirWater.z));
                    }
                }

                // ---------- 码头平台（修改地形资产顶点） ----------
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
                    report.AppendLine("码头平台压低地形顶点 " + padded + " 个");
                }

                // ---------- 3: 节点旗帜 ----------
                var nodeFlagColor = new Dictionary<string, string> {
                    {"S01","ROAD"},{"S02","ROAD"},{"S03","ROAD"},{"S04","WATER"},{"S05","WATER"},
                    {"S06","MOUNTAIN"},{"S07","ROAD"},{"S08","MOUNTAIN"},{"S09","ROAD"},{"S10","BRANCH"},
                    {"S11","ROAD"},{"S12","ROAD"},{"S13","ROAD"},{"S14","ROAD"},{"S15","ROAD"},
                };
                var poleMat = SolidMat("M_FlagPole_Wood", new Color(0.32f, 0.22f, 0.13f), Color.black);
                int flags = 0;
                foreach (var a in anchors)
                {
                    string key = a.name.Substring(0, 3);
                    Transform oldFlag = null;
                    foreach (Transform c in a) if (c.name == FlagName) { oldFlag = c; break; }
                    if (oldFlag != null) UnityEngine.Object.DestroyImmediate(oldFlag.gameObject);

                    var m = modelOf(a);
                    float ext = 1.6f;
                    if (m != null) { var b = wbOf(m); ext = Mathf.Max(b.extents.x, b.extents.z); }
                    Vector3 dir = new Vector3(0.707f, 0, 0.707f);
                    Vector3 basePos = a.position + dir * (ext + 0.5f);
                    float gy = groundY(basePos.x, basePos.z);

                    var flag = new GameObject(FlagName);
                    flag.transform.SetParent(a, true);
                    flag.transform.position = new Vector3(basePos.x, gy, basePos.z);
                    var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    UnityEngine.Object.DestroyImmediate(pole.GetComponent<Collider>());
                    pole.name = "Pole";
                    pole.transform.SetParent(flag.transform, false);
                    pole.transform.localPosition = new Vector3(0, 1.0f, 0);
                    pole.transform.localScale = new Vector3(0.07f, 1.0f, 0.07f);
                    pole.GetComponent<MeshRenderer>().sharedMaterial = poleMat;
                    var banner = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    UnityEngine.Object.DestroyImmediate(banner.GetComponent<Collider>());
                    banner.name = "Banner";
                    banner.transform.SetParent(flag.transform, false);
                    banner.transform.localPosition = new Vector3(0.34f, 1.72f, 0);
                    banner.transform.localScale = new Vector3(0.62f, 0.4f, 0.035f);
                    string ftype = nodeFlagColor.ContainsKey(key) ? nodeFlagColor[key] : "ROAD";
                    banner.GetComponent<MeshRenderer>().sharedMaterial = mats[ftype];
                    flags++;
                }
                report.AppendLine("节点旗帜 " + flags + " 面");

                // ---------- 5: 水面材质（IgniteCoders Simple Water Shader） ----------
                var waterMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/IgniteCoders/Simple Water Shader/Resources/Water_mat_01.mat");
                if (waterMat != null)
                {
                    int wn = 0;
                    foreach (var en in new[] { "E12_Route", "E13_Route" })
                    {
                        var t = find(en);
                        if (t == null) continue;
                        var mr = t.GetComponent<MeshRenderer>();
                        if (mr != null) { mr.sharedMaterial = waterMat; wn++; }
                    }
                    report.AppendLine("水面材质替换 " + wn + " 处（Simple Water Shader）");
                }
                else report.AppendLine("警告：未找到 Water_mat_01，水面未替换");

                // ---------- 标签 ----------
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
                Debug.LogError("[LycheeFixG] " + ex);
            }
            finally
            {
                File.WriteAllText(ReportPath, report.ToString());
            }
        }

        static Mesh BuildRibbon(List<Vector3> pts, float width, float lift)
        {
            var verts = new Vector3[pts.Count * 2];
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 prev = pts[Mathf.Max(0, i - 1)], next = pts[Mathf.Min(pts.Count - 1, i + 1)];
                Vector3 dir = new Vector3(next.x - prev.x, 0, next.z - prev.z).normalized;
                Vector3 perp = new Vector3(-dir.z, 0, dir.x) * (width * 0.5f);
                Vector3 p = pts[i] + new Vector3(0, lift, 0);
                verts[i * 2] = p + perp;
                verts[i * 2 + 1] = p - perp;
            }
            var tris = new List<int>((pts.Count - 1) * 6);
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                int a = i * 2, b = a + 1, c = a + 2, d = a + 3;
                tris.Add(a); tris.Add(c); tris.Add(d);
                tris.Add(a); tris.Add(d); tris.Add(b);
            }
            var mesh = new Mesh();
            mesh.vertices = verts;
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static void AddArrow(List<Vector3> verts, List<int> tris, Vector3 pos, Vector3 dir)
        {
            Vector3 perp = new Vector3(-dir.z, 0, dir.x);
            int b = verts.Count;
            verts.Add(pos + dir * 0.34f);                       // 尖
            verts.Add(pos - dir * 0.16f + perp * 0.22f);        // 左后
            verts.Add(pos - dir * 0.02f);                        // 后凹
            verts.Add(pos - dir * 0.16f - perp * 0.22f);        // 右后
            tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
            tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
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

        static Material SolidMat(string name, Color baseC, Color emission)
        {
            string path = "Assets/LycheeMap15/Materials/" + name + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetFloat("_Surface", 0f);
            mat.SetOverrideTag("RenderType", "Opaque");
            mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.SetFloat("_ZWrite", 1f);
            mat.renderQueue = -1;
            mat.SetColor("_BaseColor", baseC);
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", emission);
            mat.SetFloat("_Smoothness", 0.12f);
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }

    [InitializeOnLoad]
    internal static class LycheeMapFix20260611GAutoRun
    {
        static LycheeMapFix20260611GAutoRun()
        {
            EditorApplication.delayCall += () => EditorApplication.delayCall += TryRun;
        }

        static void TryRun()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (File.Exists(Path.Combine(root, "LycheeMapFix_20260611G.done"))) return;
            if (!File.Exists(Path.Combine(root, "LycheeMapFix_20260611F.done"))) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            if (SceneManager.GetActiveScene().name != "LycheeReplayDemo") return;
            LycheeMapFix20260611G.Run(false);
        }
    }
}
