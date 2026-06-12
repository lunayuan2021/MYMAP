// 第十一轮修复（2026-06-11 批次 L）——地形/道路/水线一致性总重建：
// 1) 地形从原始资产单遍重建（v6）：河道下挖 + 岸线强制高于水面(+0.08) + 码头平台 + 13 座节点基台（用原始高程，不再叠加修改）
// 2) 所有道路网格逐顶点贴合新地形：y_new = H_new + (y_old - H_orig)，保留原设计净高，解决道路被草皮掩埋
// 3) 路线线带与箭头按贴合后的道路重建
// 4) 删除节点环形标记（不再有任何圆盘/圆环）
// 5) 水驿基座抬至 水面-0.04；岩石/树木/草/旗帜全部重新贴地
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace LycheeReplayDemoTools
{
    internal static class LycheeMapFix20260611L
    {
        static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string MarkerPath => Path.Combine(Root, "LycheeMapFix_20260611L.done");
        static string ReportPath => Path.Combine(Root, "LycheeMapFix_20260611L_report.txt");
        const string OrigTerrainPath = "Assets/LycheeMap15/GeneratedMeshes/litchi_map_unity_full_coverage_v2_Terrain.asset";
        const string NewTerrainPath = "Assets/LycheeMap15/GeneratedMeshes/FinalRiver/Terrain_RiverSand_v6_20260611L.asset";
        const string SandMatPath = "Assets/LycheeMap15/Materials/M_RiverBed_SandGravel.mat";
        const string GrassMatPath = "Assets/LycheeMap15/Materials/M_Config_Terrain_Grassland.mat";
        const string MeshDir = "Assets/LycheeMap15/GeneratedMeshes/RouteOverlay_20260611G";
        const string RoadFitDir = "Assets/LycheeMap15/GeneratedMeshes/RoadGroundFit_20260611L";
        const string OverlayGroup = "Route_Line_Overlay_20260611G";

        const float MinX = -40f, MaxX = 40f, MinZ = -30f, MaxZ = 30f, Step = 0.4f;
        const float BedDepth = 0.72f, BedHalf = 1.05f, ShoreCross = 1.45f, BankLipEnd = 1.85f, CarveOuter = 2.95f, SandOuter = 1.95f;

        static readonly (string id, string from, string to, string type)[] Edges = {
            ("E01","S01","S02","ROAD"), ("E02","S02","S03","ROAD"), ("E03","S03","S07","ROAD"),
            ("E04","S07","S09","ROAD"), ("E05","S09","S10","ROAD"), ("E06","S10","S11","ROAD"),
            ("E07","S11","S12","ROAD"), ("E08","S12","S13","ROAD"), ("E09","S13","S14","ROAD"),
            ("E10","S14","S15","ROAD"), ("E11","S02","S04","ROAD"), ("E12","S04","S05","WATER"),
            ("E13","S05","S07","WATER"), ("E14","S09","S11","ROAD"), ("E15","S01","S06","MOUNTAIN"),
            ("E16","S06","S08","MOUNTAIN"), ("E17","S08","S10","BRANCH"),
        };
        static readonly Dictionary<string, Vector2> NodeXZ = new Dictionary<string, Vector2> {
            {"S01",new Vector2(-35,-20)},{"S02",new Vector2(-25,-14)},{"S03",new Vector2(-18,-8)},
            {"S04",new Vector2(-18,-22)},{"S05",new Vector2(-2,-18)},{"S06",new Vector2(-26,-2)},
            {"S07",new Vector2(0,-6)},{"S08",new Vector2(2,6)},{"S09",new Vector2(15,-2)},
            {"S10",new Vector2(22,4)},{"S11",new Vector2(26,8)},{"S12",new Vector2(30,10)},
            {"S13",new Vector2(33,11)},{"S14",new Vector2(36,12)},{"S15",new Vector2(38,12)},
        };

        static float[,] origGrid;
        static float[] H;
        static int NX, NZ;
        static List<Vector3[]> riverCenters = new List<Vector3[]>();
        static List<Vector2> roadXZ = new List<Vector2>();

        [MenuItem("Tools/Lychee/修复地图 20260611L（地形道路水线总重建）")]
        public static void RunMenu() { Run(true); }

        public static void Run(bool force)
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.name != "LycheeReplayDemo") return;
            if (!force && File.Exists(MarkerPath)) return;
            var report = new StringBuilder();
            report.AppendLine("LycheeMapFix_20260611L 报告 " + DateTime.Now.ToString("HH:mm:ss"));
            try
            {
                var roots = scene.GetRootGameObjects();
                var all = new List<Transform>();
                foreach (var r in roots) { if (r != null) all.AddRange(r.GetComponentsInChildren<Transform>(true)); }
                Func<string, Transform> find = n => all.FirstOrDefault(t => t != null && t.name == n);

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

                // ---------- 0: 删除环形标记 ----------
                int delRing = 0;
                foreach (var a in anchors)
                {
                    var doomed = new List<GameObject>();
                    foreach (Transform c in a)
                        if (c.name == "Node_RingMarker_20260611J" || c.name == "Node_GlowRing_20260611E") doomed.Add(c.gameObject);
                    foreach (var g in doomed) { UnityEngine.Object.DestroyImmediate(g); delRing++; }
                }
                report.AppendLine("删除节点圆环/圆盘 " + delRing + " 个");

                // ---------- 1: 数据收集 ----------
                riverCenters.Clear();
                foreach (var rn in new[] { "E12_Route", "E13_Route" })
                {
                    var t = find(rn); if (t == null) continue;
                    var v = t.GetComponent<MeshFilter>().sharedMesh.vertices;
                    var c = new Vector3[v.Length / 2];
                    for (int i = 0; i < c.Length; i++)
                        c[i] = (t.TransformPoint(v[i * 2]) + t.TransformPoint(v[i * 2 + 1])) * 0.5f;
                    riverCenters.Add(c);
                }
                roadXZ.Clear();
                var roadGroups = new[] { "OfficialRoad", "MountainRoute", "BranchRoute" };
                foreach (var gn in roadGroups)
                {
                    var g = find(gn); if (g == null) continue;
                    foreach (var mf in g.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (mf.sharedMesh == null) continue;
                        var v = mf.sharedMesh.vertices;
                        for (int i = 0; i < v.Length; i++)
                        {
                            Vector3 w = mf.transform.TransformPoint(v[i]);
                            roadXZ.Add(new Vector2(w.x, w.z));
                        }
                    }
                }

                // 原始地形网格
                var orig = AssetDatabase.LoadAssetAtPath<Mesh>(OrigTerrainPath);
                origGrid = new float[81, 61];
                var flag = new bool[81, 61];
                foreach (var v in orig.vertices)
                {
                    int xi = Mathf.RoundToInt(v.x - MinX), zi = Mathf.RoundToInt(v.z - MinZ);
                    if (xi >= 0 && xi < 81 && zi >= 0 && zi < 61) { origGrid[xi, zi] = v.y; flag[xi, zi] = true; }
                }

                // 水驿方向与码头平台中心
                var quayCenters = new List<Vector3>();
                var waterDirs = new Dictionary<string, Vector3>();
                var waterYs = new Dictionary<string, float>();
                {
                    var c12 = riverCenters[0]; var c13 = riverCenters.Count > 1 ? riverCenters[1] : riverCenters[0];
                    // S04
                    Vector2 t4 = NodeXZ["S04"];
                    Vector3 p0 = c12[0], p1 = c12[1];
                    if (Vector2.Distance(new Vector2(c12[c12.Length - 1].x, c12[c12.Length - 1].z), t4) <
                        Vector2.Distance(new Vector2(p0.x, p0.z), t4))
                    { p0 = c12[c12.Length - 1]; p1 = c12[c12.Length - 2]; }
                    Vector3 d4 = new Vector3(p1.x - p0.x, 0, p1.z - p0.z).normalized;
                    waterDirs["S04"] = d4; waterYs["S04"] = p0.y;
                    quayCenters.Add(new Vector3(t4.x - d4.x * 2.0f, p0.y, t4.y - d4.z * 2.0f));
                    // S05
                    Vector2 t5 = NodeXZ["S05"];
                    Vector3 e12end = c12[Vector2.Distance(new Vector2(c12[0].x, c12[0].z), t5) < 1f ? 1 : c12.Length - 2];
                    Vector3 e13next = c13[Vector2.Distance(new Vector2(c13[0].x, c13[0].z), t5) < 1f ? 1 : c13.Length - 2];
                    Vector3 dd1 = new Vector3(e12end.x - t5.x, 0, e12end.z - t5.y).normalized;
                    Vector3 dd2 = new Vector3(e13next.x - t5.x, 0, e13next.z - t5.y).normalized;
                    Vector3 d5 = (dd1 + dd2).normalized;
                    if (d5.sqrMagnitude < 0.1f) d5 = dd2;
                    waterDirs["S05"] = d5; waterYs["S05"] = (e12end.y + e13next.y) * 0.5f;
                    quayCenters.Add(new Vector3(t5.x - d5.x * 2.0f, waterYs["S05"], t5.y - d5.z * 2.0f));
                }

                // 节点基台（原始高程）
                var pads = new List<(Vector2 c, float r, float y, string key)>();
                foreach (var a in anchors)
                {
                    string key = a.name.Substring(0, 3);
                    if (key == "S04" || key == "S05") continue;
                    var m = modelOf(a); if (m == null) continue;
                    Bounds wb = wbOf(m);
                    float rPad = Mathf.Clamp(Mathf.Max(wb.extents.x, wb.extents.z) * 0.8f + 0.3f, 1.6f, 3.6f);
                    Vector2 axz = NodeXZ[key];
                    float padY = OrigH(axz.x, axz.y);
                    float dRiv, wyRiv; Vector3 tanR, nearR;
                    if (NearestRiver(axz.x, axz.y, out dRiv, out wyRiv, out tanR, out nearR) && dRiv < 5.5f)
                        padY = Mathf.Max(padY, wyRiv + 0.10f);   // 临水基台不得低于水面
                    pads.Add((axz, rPad, padY, key));
                }

                // ---------- 2: 地形 v6 单遍重建 ----------
                NX = Mathf.RoundToInt((MaxX - MinX) / Step) + 1;
                NZ = Mathf.RoundToInt((MaxZ - MinZ) / Step) + 1;
                H = new float[NX * NZ];
                for (int zi = 0; zi < NZ; zi++)
                {
                    float z = MinZ + zi * Step;
                    for (int xi = 0; xi < NX; xi++)
                    {
                        float x = MinX + xi * Step;
                        float y = OrigH(x, z);
                        float d, wy; Vector3 tan, near;
                        bool hasRiver = NearestRiver(x, z, out d, out wy, out tan, out near);
                        float rd = RoadDist(x, z);
                        if (hasRiver && d < CarveOuter)
                            y = CarveProfile(x, z, y, d, wy, rd);
                        // 岸线强制：水线外侧地表不得低于水面（淡出到 d=4.2；路基/码头区除外）
                        if (hasRiver && d >= BankLipEnd && d < 4.2f && rd > 1.0f)
                        {
                            bool nearQuay = false;
                            foreach (var qc in quayCenters)
                                if (Vector2.Distance(new Vector2(x, z), new Vector2(qc.x, qc.z)) < 3.6f) { nearQuay = true; break; }
                            if (!nearQuay)
                            {
                                float fade = 1f - Mathf.Clamp01((d - CarveOuter) / (4.2f - CarveOuter));
                                float floor = wy + 0.08f * fade + 0.02f;
                                if (y < floor) y = floor;
                            }
                        }
                        H[zi * NX + xi] = y;
                    }
                }
                // 码头平台（只降）
                foreach (var qc in quayCenters) ApplyPadLower(new Vector2(qc.x, qc.z), qc.y + 0.03f, 1.9f, 3.4f);
                // 节点基台（压平：升降皆可）
                foreach (var pad in pads) ApplyPadFlatten(pad.c, pad.y, pad.r, pad.r + 2.0f);
                report.AppendLine("地形 v6 重建完成（基台 " + pads.Count + " 座 + 码头 2 座 + 岸线强制）");

                // ---------- 3: 生成网格与子材质 ----------
                var verts = new Vector3[NX * NZ];
                var uv = new Vector2[NX * NZ];
                for (int zi = 0; zi < NZ; zi++)
                    for (int xi = 0; xi < NX; xi++)
                    {
                        int i = zi * NX + xi;
                        float x = MinX + xi * Step, z = MinZ + zi * Step;
                        verts[i] = new Vector3(x, H[i], z);
                        uv[i] = new Vector2((x - MinX) / 8f, (z - MinZ) / 8f);
                    }
                var grassTris = new List<int>(NX * NZ * 6);
                var sandTris = new List<int>(20000);
                for (int zi = 0; zi + 1 < NZ; zi++)
                    for (int xi = 0; xi + 1 < NX; xi++)
                    {
                        int i00 = zi * NX + xi, i10 = i00 + 1, i01 = i00 + NX, i11 = i01 + 1;
                        AddQuadTri(verts, i00, i01, i11, grassTris, sandTris);
                        AddQuadTri(verts, i00, i11, i10, grassTris, sandTris);
                    }
                var mesh = new Mesh();
                mesh.name = "Terrain_RiverSand_v6_20260611L";
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.vertices = verts; mesh.uv = uv;
                mesh.subMeshCount = 2;
                mesh.SetTriangles(grassTris, 0);
                mesh.SetTriangles(sandTris, 1);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                var existing = AssetDatabase.LoadAssetAtPath<Mesh>(NewTerrainPath);
                if (existing != null) AssetDatabase.DeleteAsset(NewTerrainPath);
                AssetDatabase.CreateAsset(mesh, NewTerrainPath);

                var terrainT = find("Continuous_Terrain");
                terrainT.GetComponent<MeshFilter>().sharedMesh = mesh;
                var tcol = terrainT.GetComponent<MeshCollider>();
                tcol.sharedMesh = null; tcol.sharedMesh = mesh;
                var grassM = AssetDatabase.LoadAssetAtPath<Material>(GrassMatPath);
                var sandM = AssetDatabase.LoadAssetAtPath<Material>(SandMatPath);
                terrainT.GetComponent<MeshRenderer>().sharedMaterials = new[] { grassM, sandM };
                report.AppendLine(string.Format("地形已替换：草 {0} 砂 {1} 三角形", grassTris.Count / 3, sandTris.Count / 3));

                // ---------- 4: 道路逐顶点贴合 ----------
                if (!AssetDatabase.IsValidFolder(RoadFitDir))
                    AssetDatabase.CreateFolder("Assets/LycheeMap15/GeneratedMeshes", "RoadGroundFit_20260611L");
                int fitted = 0;
                foreach (var gn in roadGroups)
                {
                    var g = find(gn); if (g == null) continue;
                    foreach (var mf in g.GetComponentsInChildren<MeshFilter>(true))
                    {
                        var src = mf.sharedMesh;
                        if (src == null || src.vertexCount == 0) continue;
                        var mv = src.vertices;
                        var world = new Vector3[mv.Length];
                        bool changed = false;
                        for (int i = 0; i < mv.Length; i++)
                        {
                            Vector3 w = mf.transform.TransformPoint(mv[i]);
                            float clearance = w.y - OrigH(w.x, w.z);
                            float ny = HeightAt(w.x, w.z) + clearance;
                            // 河口浅滩处道路保持原高，避免路尖沉入水下
                            float dRiv, wyRiv; Vector3 tanR, nearR;
                            if (NearestRiver(w.x, w.z, out dRiv, out wyRiv, out tanR, out nearR) && dRiv < 2.5f)
                                ny = Mathf.Max(ny, Mathf.Lerp(w.y, ny, dRiv / 2.5f));
                            if (Mathf.Abs(ny - w.y) > 0.005f) changed = true;
                            world[i] = new Vector3(w.x, ny, w.z);
                        }
                        if (!changed) continue;
                        var nm = new Mesh();
                        nm.name = src.name + "_GroundFit_v6";
                        nm.indexFormat = src.indexFormat;
                        var local = new Vector3[mv.Length];
                        for (int i = 0; i < mv.Length; i++)
                            local[i] = mf.transform.InverseTransformPoint(world[i]);
                        nm.vertices = local;
                        nm.uv = src.uv;
                        nm.subMeshCount = src.subMeshCount;
                        for (int s = 0; s < src.subMeshCount; s++)
                            nm.SetTriangles(src.GetTriangles(s), s);
                        nm.RecalculateNormals();
                        nm.RecalculateBounds();
                        string ap = RoadFitDir + "/" + nm.name + ".asset";
                        var oldA = AssetDatabase.LoadAssetAtPath<Mesh>(ap);
                        if (oldA != null) AssetDatabase.DeleteAsset(ap);
                        AssetDatabase.CreateAsset(nm, ap);
                        mf.sharedMesh = nm;
                        var mc = mf.GetComponent<MeshCollider>();
                        if (mc != null) { mc.sharedMesh = null; mc.sharedMesh = nm; }
                        fitted++;
                    }
                }
                report.AppendLine("道路网格贴合 " + fitted + " 个");

                // ---------- 5: 线带与箭头重建 ----------
                var oldOverlay = find(OverlayGroup);
                if (oldOverlay != null) UnityEngine.Object.DestroyImmediate(oldOverlay.gameObject);
                var mapRoot = roots.FirstOrDefault(r => r != null && r.name.StartsWith("LYCHEE_CONFIG_MAP"));
                var overlay = new GameObject(OverlayGroup);
                if (mapRoot != null) overlay.transform.SetParent(mapRoot.transform, true);
                var typeMats = new Dictionary<string, Material>();
                foreach (var tp in new[] { "ROAD", "WATER", "MOUNTAIN", "BRANCH" })
                    typeMats[tp] = AssetDatabase.LoadAssetAtPath<Material>("Assets/LycheeMap15/Materials/M_RouteLine_" + tp + ".mat");
                var arrowMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/LycheeMap15/Materials/M_RouteArrow_White.mat");
                var arrowVerts = new List<Vector3>();
                var arrowTris = new List<int>();
                int lines = 0, arrows = 0;
                foreach (var e in Edges)
                {
                    var t = find(e.id + "_Route");
                    if (t == null) continue;
                    var mfx = t.GetComponent<MeshFilter>();
                    if (mfx == null || mfx.sharedMesh == null) continue;
                    var v = mfx.sharedMesh.vertices;
                    var pts = new List<Vector3>(v.Length / 2);
                    for (int i = 0; i + 1 < v.Length; i += 2)
                        pts.Add((t.TransformPoint(v[i]) + t.TransformPoint(v[i + 1])) * 0.5f);
                    if (pts.Count < 2) continue;
                    Vector2 fromXZ = NodeXZ[e.from];
                    if (Vector2.Distance(new Vector2(pts[pts.Count - 1].x, pts[pts.Count - 1].z), fromXZ) <
                        Vector2.Distance(new Vector2(pts[0].x, pts[0].z), fromXZ)) pts.Reverse();
                    float lift = e.type == "WATER" ? 0.04f : 0.06f;
                    float width = e.type == "WATER" ? 0.50f : 0.55f;
                    var rib = BuildRibbon(pts, width, lift);
                    rib.name = e.id + "_Line";
                    string ap2 = MeshDir + "/" + rib.name + ".asset";
                    var oldR = AssetDatabase.LoadAssetAtPath<Mesh>(ap2);
                    if (oldR != null) AssetDatabase.DeleteAsset(ap2);
                    AssetDatabase.CreateAsset(rib, ap2);
                    var go = new GameObject(e.id + "_Line_" + e.type);
                    go.transform.SetParent(overlay.transform, true);
                    go.AddComponent<MeshFilter>().sharedMesh = rib;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = typeMats[e.type];
                    mr.shadowCastingMode = ShadowCastingMode.Off;
                    lines++;
                    float acc = 0f, next = 1.5f;
                    for (int i = 0; i + 1 < pts.Count; i++)
                    {
                        Vector3 a = pts[i], b = pts[i + 1];
                        float seg = Vector3.Distance(new Vector3(a.x, 0, a.z), new Vector3(b.x, 0, b.z));
                        while (acc + seg >= next)
                        {
                            float tt = (next - acc) / Mathf.Max(seg, 1e-4f);
                            Vector3 p = Vector3.Lerp(a, b, tt);
                            Vector3 dir = new Vector3(b.x - a.x, 0, b.z - a.z).normalized;
                            AddArrow(arrowVerts, arrowTris, p + new Vector3(0, lift + 0.025f, 0), dir);
                            arrows++;
                            next += 3.0f;
                        }
                        acc += seg;
                    }
                }
                var am = new Mesh();
                am.name = "RouteArrows_All";
                am.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                am.SetVertices(arrowVerts);
                am.SetTriangles(arrowTris, 0);
                am.RecalculateNormals();
                string apA = MeshDir + "/RouteArrows_All.asset";
                var oldAr = AssetDatabase.LoadAssetAtPath<Mesh>(apA);
                if (oldAr != null) AssetDatabase.DeleteAsset(apA);
                AssetDatabase.CreateAsset(am, apA);
                var ago = new GameObject("Route_Arrows");
                ago.transform.SetParent(overlay.transform, true);
                ago.AddComponent<MeshFilter>().sharedMesh = am;
                var amr = ago.AddComponent<MeshRenderer>();
                amr.sharedMaterial = arrowMat;
                amr.shadowCastingMode = ShadowCastingMode.Off;
                report.AppendLine("线带 " + lines + " 条，箭头 " + arrows + " 个（已按贴合道路重建）");

                // ---------- 6: 建筑贴地 ----------
                foreach (var a in anchors)
                {
                    string key = a.name.Substring(0, 3);
                    var m = modelOf(a); if (m == null) continue;
                    Bounds wb = wbOf(m);
                    if (key == "S04" || key == "S05")
                        m.position += new Vector3(0, (waterYs[key] - 0.04f) - wb.min.y, 0);
                    else
                    {
                        var pad = pads.First(p => p.key == key);
                        m.position += new Vector3(0, (pad.y - 0.04f) - wb.min.y, 0);
                    }
                    Transform label = null;
                    foreach (Transform c in a) if (c.name == "Node_Name_CN") { label = c; break; }
                    if (label != null)
                    {
                        Bounds wb2 = wbOf(m);
                        var lp = label.localPosition;
                        label.localPosition = new Vector3(lp.x, (wb2.max.y - a.position.y) + 0.75f, lp.z);
                    }
                }
                report.AppendLine("建筑与标签重新贴地");

                // ---------- 7: 植被/岩石/旗帜贴地与净空 ----------
                int reground = 0, rockMoved = 0;
                foreach (var gname in new[] { "Tree_Clusters", "Lingnan_Orchard_Tree_Groves", "Grass_And_Groundcover", "Rock_Clusters" })
                {
                    var g = find(gname); if (g == null) continue;
                    foreach (Transform c in g)
                    {
                        Vector3 p = c.position;
                        float d, wy; Vector3 tan, near;
                        if (gname == "Rock_Clusters" && NearestRiver(p.x, p.z, out d, out wy, out tan, out near) && d < 2.2f)
                        {
                            Vector3 dir = new Vector3(p.x - near.x, 0, p.z - near.z);
                            dir = dir.sqrMagnitude < 1e-6f ? Vector3.Cross(Vector3.up, tan) : dir.normalized;
                            p = new Vector3(near.x, 0, near.z) + dir * 2.4f;
                            rockMoved++;
                        }
                        float sink = gname == "Rock_Clusters" ? 0.05f : 0.02f;
                        c.position = new Vector3(p.x, HeightAt(p.x, p.z) - sink, p.z);
                        reground++;
                    }
                }
                foreach (var a in anchors)
                {
                    foreach (Transform c in a)
                        if (c.name == "Node_RouteFlag_20260611G")
                            c.position = new Vector3(c.position.x, HeightAt(c.position.x, c.position.z), c.position.z);
                }
                report.AppendLine("植被/岩石/旗帜重新贴地 " + reground + " 项（移石 " + rockMoved + "）");

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
                Debug.LogError("[LycheeFixL] " + ex);
            }
            finally
            {
                File.WriteAllText(ReportPath, report.ToString());
            }
        }

        static float OrigSafe(int xi, int zi)
        {
            xi = Mathf.Clamp(xi, 0, 80); zi = Mathf.Clamp(zi, 0, 60);
            return origGrid[xi, zi];
        }

        static float OrigH(float x, float z)
        {
            float fx = Mathf.Clamp(x - MinX, 0, 80f - 1e-4f);
            float fz = Mathf.Clamp(z - MinZ, 0, 60f - 1e-4f);
            int xi = (int)fx, zi = (int)fz;
            float tx = fx - xi, tz = fz - zi;
            float a = Mathf.Lerp(OrigSafe(xi, zi), OrigSafe(xi + 1, zi), tx);
            float b = Mathf.Lerp(OrigSafe(xi, zi + 1), OrigSafe(xi + 1, zi + 1), tx);
            return Mathf.Lerp(a, b, tz);
        }

        static float HeightAt(float x, float z)
        {
            float fx = Mathf.Clamp((x - MinX) / Step, 0, NX - 1.0001f);
            float fz = Mathf.Clamp((z - MinZ) / Step, 0, NZ - 1.0001f);
            int xi = (int)fx, zi = (int)fz;
            float tx = fx - xi, tz = fz - zi;
            float a = Mathf.Lerp(H[zi * NX + xi], H[zi * NX + xi + 1], tx);
            float b = Mathf.Lerp(H[(zi + 1) * NX + xi], H[(zi + 1) * NX + xi + 1], tx);
            return Mathf.Lerp(a, b, tz);
        }

        static bool NearestRiver(float x, float z, out float dist, out float waterY, out Vector3 tangent, out Vector3 nearest)
        {
            dist = float.MaxValue; waterY = 0; tangent = Vector3.right; nearest = Vector3.zero;
            var p = new Vector2(x, z);
            foreach (var line in riverCenters)
            {
                for (int i = 0; i + 1 < line.Length; i++)
                {
                    Vector2 a = new Vector2(line[i].x, line[i].z);
                    Vector2 b = new Vector2(line[i + 1].x, line[i + 1].z);
                    Vector2 ab = b - a;
                    float len2 = ab.sqrMagnitude;
                    float t = len2 < 1e-8f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
                    Vector2 q = a + ab * t;
                    float d = Vector2.Distance(p, q);
                    if (d < dist)
                    {
                        dist = d;
                        waterY = Mathf.Lerp(line[i].y, line[i + 1].y, t);
                        tangent = new Vector3(ab.x, 0, ab.y).normalized;
                        nearest = new Vector3(q.x, waterY, q.y);
                    }
                }
            }
            return dist < float.MaxValue;
        }

        static float RoadDist(float x, float z)
        {
            float best = float.MaxValue;
            var p = new Vector2(x, z);
            for (int i = 0; i < roadXZ.Count; i++)
            {
                float d = Vector2.SqrMagnitude(roadXZ[i] - p);
                if (d < best) best = d;
            }
            return Mathf.Sqrt(best);
        }

        static float Cos01(float t)
        {
            t = Mathf.Clamp01(t);
            return 0.5f - 0.5f * Mathf.Cos(t * Mathf.PI);
        }

        static float CarveProfile(float x, float z, float origY, float d, float waterY, float roadD)
        {
            float bedY = waterY - BedDepth + (Mathf.PerlinNoise(x * 1.7f, z * 1.7f) - 0.5f) * 0.07f;
            float lip = Mathf.Max(origY, waterY + 0.10f);
            float target;
            if (d <= BedHalf) target = bedY;
            else if (d <= ShoreCross) target = Mathf.Lerp(bedY, waterY - 0.06f, Cos01((d - BedHalf) / (ShoreCross - BedHalf)));
            else if (d <= BankLipEnd) target = Mathf.Lerp(waterY - 0.06f, lip, Cos01((d - ShoreCross) / (BankLipEnd - ShoreCross)));
            else target = Mathf.Lerp(lip, origY, Cos01((d - BankLipEnd) / (CarveOuter - BankLipEnd)));
            float y = (d > 2.2f) ? Mathf.Min(origY, target) : target;
            if (roadD < 1.0f)
            {
                float protect = 1f - Cos01(roadD / 1.0f);
                y = Mathf.Lerp(y, Mathf.Max(y, waterY - 0.16f), protect);
            }
            return y;
        }

        static void ApplyPadLower(Vector2 center, float padY, float rIn, float rOut)
        {
            for (int zi = 0; zi < NZ; zi++)
            {
                float z = MinZ + zi * Step;
                for (int xi = 0; xi < NX; xi++)
                {
                    float x = MinX + xi * Step;
                    float d = Vector2.Distance(new Vector2(x, z), center);
                    if (d > rOut) continue;
                    int idx = zi * NX + xi;
                    float t = d <= rIn ? 0f : Cos01((d - rIn) / (rOut - rIn));
                    float v = Mathf.Lerp(padY, H[idx], t);
                    if (v < H[idx]) H[idx] = v;
                }
            }
        }

        static void ApplyPadFlatten(Vector2 center, float padY, float rIn, float rOut)
        {
            for (int zi = 0; zi < NZ; zi++)
            {
                float z = MinZ + zi * Step;
                for (int xi = 0; xi < NX; xi++)
                {
                    float x = MinX + xi * Step;
                    float d = Vector2.Distance(new Vector2(x, z), center);
                    if (d > rOut) continue;
                    int idx = zi * NX + xi;
                    float t = d <= rIn ? 0f : Cos01((d - rIn) / (rOut - rIn));
                    H[idx] = Mathf.Lerp(padY, H[idx], t);
                }
            }
        }

        static void AddQuadTri(Vector3[] verts, int a, int b, int c, List<int> grass, List<int> sand)
        {
            Vector3 ctr = (verts[a] + verts[b] + verts[c]) / 3f;
            float d, wy; Vector3 tan, near;
            bool isSand = false;
            if (NearestRiver(ctr.x, ctr.z, out d, out wy, out tan, out near))
                isSand = d < SandOuter && ctr.y < wy + 0.12f;
            var list = isSand ? sand : grass;
            list.Add(a); list.Add(b); list.Add(c);
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
                int a = i * 2, b = a + 1, c = a + 2, dd = a + 3;
                tris.Add(a); tris.Add(c); tris.Add(dd);
                tris.Add(a); tris.Add(dd); tris.Add(b);
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
            verts.Add(pos + dir * 0.34f);
            verts.Add(pos - dir * 0.16f + perp * 0.22f);
            verts.Add(pos - dir * 0.02f);
            verts.Add(pos - dir * 0.16f - perp * 0.22f);
            tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
            tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
        }
    }

    [InitializeOnLoad]
    internal static class LycheeMapFix20260611LAutoRun
    {
        static LycheeMapFix20260611LAutoRun()
        {
            EditorApplication.delayCall += () => EditorApplication.delayCall += TryRun;
        }

        static void TryRun()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (File.Exists(Path.Combine(root, "LycheeMapFix_20260611L.done"))) return;
            if (!File.Exists(Path.Combine(root, "LycheeMapFix_20260611K.done"))) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            if (SceneManager.GetActiveScene().name != "LycheeReplayDemo") return;
            LycheeMapFix20260611L.Run(false);
        }
    }
}
