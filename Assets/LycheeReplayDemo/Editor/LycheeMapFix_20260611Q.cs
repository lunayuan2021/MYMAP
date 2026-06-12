// 第十六轮修复（2026-06-11 批次 Q）——区域低地填充：
// 河湾内侧半岛等近河低地（原始地形低于水位）扩展岸线强制：离河 10 内地表 >= 邻近最高水位+0.08，10->16 渐隐。
// 地形 v8 重建 + 道路贴合 + 线带箭头重建 + 全要素贴地。水面/拆分件不变（仅房屋件重新贴基台）。
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
    internal static class LycheeMapFix20260611Q
    {
        static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string MarkerPath => Path.Combine(Root, "LycheeMapFix_20260611Q.done");
        static string ReportPath => Path.Combine(Root, "LycheeMapFix_20260611Q_report.txt");
        const string OrigTerrainPath = "Assets/LycheeMap15/GeneratedMeshes/litchi_map_unity_full_coverage_v2_Terrain.asset";
        const string NewTerrainPath = "Assets/LycheeMap15/GeneratedMeshes/FinalRiver/Terrain_RiverSand_v8_20260611Q.asset";
        const string SandMatPath = "Assets/LycheeMap15/Materials/M_RiverBed_SandGravel.mat";
        const string GrassMatPath = "Assets/LycheeMap15/Materials/M_Config_Terrain_Grassland.mat";
        const string MeshDir = "Assets/LycheeMap15/GeneratedMeshes/RouteOverlay_20260611G";
        const string RoadFitDir = "Assets/LycheeMap15/GeneratedMeshes/RoadGroundFit_20260611L";
        const string SplitDir = "Assets/LycheeMap15/GeneratedMeshes/WaterStationSplit_20260611Q";
        const string OverlayGroup = "Route_Line_Overlay_20260611G";
        const string SplitRootName = "WaterStation_Split_20260611Q";

        const float MinX = -40f, MaxX = 40f, MinZ = -30f, MaxZ = 30f, Step = 0.4f;
        const float BedDepth = 0.72f, BedHalf = 1.05f, ShoreCross = 1.45f, BankLipEnd = 1.85f, CarveOuter = 2.95f, SandOuter = 1.95f;
        const float SlopeCap = 0.02f;

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
        static StringBuilder report;

        [MenuItem("Tools/Lychee/修复地图 20260611Q（水面纵坡+水驿拆分）")]
        public static void RunMenu() { Run(true); }

        public static void Run(bool force)
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.name != "LycheeReplayDemo") return;
            if (!force && File.Exists(MarkerPath)) return;
            report = new StringBuilder();
            report.AppendLine("LycheeMapFix_20260611Q 报告 " + DateTime.Now.ToString("HH:mm:ss"));
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

                // ---------- 1: 水面纵坡限制 ----------
                // 接缝基准：E12 在 S05 端的水位
                var e12T = find("E12_Route");
                var e13T = find("E13_Route");
                float junctionY = ReslopeWater(e12T, NodeXZ["S04"], NodeXZ["S05"], float.NaN, "E12");
                ReslopeWater(e13T, NodeXZ["S05"], NodeXZ["S07"], junctionY, "E13");

                // 重新收集中心线
                riverCenters.Clear();
                foreach (var t in new[] { e12T, e13T })
                {
                    var v = t.GetComponent<MeshFilter>().sharedMesh.vertices;
                    var c = new Vector3[v.Length / 2];
                    for (int i = 0; i < c.Length; i++)
                        c[i] = (t.TransformPoint(v[i * 2]) + t.TransformPoint(v[i * 2 + 1])) * 0.5f;
                    riverCenters.Add(c);
                }

                // PathPoint 同步新水位
                int ppFixed = 0;
                foreach (var grpName in new[] { "E12_S04-S05", "E13_S05-S07" })
                {
                    var grp = find(grpName); if (grp == null) continue;
                    foreach (Transform c in grp)
                    {
                        if (!c.name.StartsWith("PathPoint_")) continue;
                        float d, wy; Vector3 tan, near;
                        if (NearestRiver(c.position.x, c.position.z, out d, out wy, out tan, out near))
                        { c.position = new Vector3(c.position.x, wy + 0.02f, c.position.z); ppFixed++; }
                    }
                }
                report.AppendLine("PathPoint 同步水位 " + ppFixed + " 个");

                // ---------- 2: 数据收集 ----------
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
                var orig = AssetDatabase.LoadAssetAtPath<Mesh>(OrigTerrainPath);
                origGrid = new float[81, 61];
                foreach (var v in orig.vertices)
                {
                    int xi = Mathf.RoundToInt(v.x - MinX), zi = Mathf.RoundToInt(v.z - MinZ);
                    if (xi >= 0 && xi < 81 && zi >= 0 && zi < 61) origGrid[xi, zi] = v.y;
                }

                // 水驿方向与码头平台
                var quayCenters = new List<Vector3>();
                var waterDirs = new Dictionary<string, Vector3>();
                {
                    var c12 = riverCenters[0]; var c13 = riverCenters[1];
                    Vector2 t4 = NodeXZ["S04"];
                    Vector3 p0 = c12[0], p1 = c12[1];
                    if (Vector2.Distance(new Vector2(c12[c12.Length - 1].x, c12[c12.Length - 1].z), t4) <
                        Vector2.Distance(new Vector2(p0.x, p0.z), t4))
                    { p0 = c12[c12.Length - 1]; p1 = c12[c12.Length - 2]; }
                    Vector3 d4 = new Vector3(p1.x - p0.x, 0, p1.z - p0.z).normalized;
                    waterDirs["S04"] = d4;
                    quayCenters.Add(new Vector3(t4.x - d4.x * 2.0f, p0.y, t4.y - d4.z * 2.0f));
                    Vector2 t5 = NodeXZ["S05"];
                    Vector3 e12end = c12[Vector2.Distance(new Vector2(c12[0].x, c12[0].z), t5) < 1f ? 1 : c12.Length - 2];
                    Vector3 e13next = c13[Vector2.Distance(new Vector2(c13[0].x, c13[0].z), t5) < 1f ? 1 : c13.Length - 2];
                    Vector3 dd1 = new Vector3(e12end.x - t5.x, 0, e12end.z - t5.y).normalized;
                    Vector3 dd2 = new Vector3(e13next.x - t5.x, 0, e13next.z - t5.y).normalized;
                    Vector3 d5 = (dd1 + dd2).normalized;
                    if (d5.sqrMagnitude < 0.1f) d5 = dd2;
                    waterDirs["S05"] = d5;
                    quayCenters.Add(new Vector3(t5.x - d5.x * 2.0f, MaxWaterIn(t5.x, t5.y, 3f), t5.y - d5.z * 2.0f));
                }

                var pads = new List<(Vector2 c, float r, float y, string key)>();
                foreach (var a in anchors)
                {
                    string key = a.name.Substring(0, 3);
                    if (key == "S04" || key == "S05") continue;
                    var m = modelOf(a); if (m == null) continue;
                    Bounds wb = WB(m);
                    float rPad = Mathf.Clamp(Mathf.Max(wb.extents.x, wb.extents.z) * 0.8f + 0.3f, 1.6f, 3.6f);
                    Vector2 axz = NodeXZ[key];
                    float padY = OrigH(axz.x, axz.y);
                    float dRiv, wyRiv; Vector3 tanR, nearR;
                    if (NearestRiver(axz.x, axz.y, out dRiv, out wyRiv, out tanR, out nearR) && dRiv < 5.5f)
                        padY = Mathf.Max(padY, MaxWaterIn(axz.x, axz.y, 4f) + 0.10f);
                    pads.Add((axz, rPad, padY, key));
                }

                // ---------- 3: 地形 v8 ----------
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
                        if (hasRiver && d >= BankLipEnd && d < 16f && rd > 1.0f)
                        {
                            bool nearQuay = false;
                            foreach (var qc in quayCenters)
                                if (Vector2.Distance(new Vector2(x, z), new Vector2(qc.x, qc.z)) < 3.6f) { nearQuay = true; break; }
                            if (!nearQuay)
                            {
                                // 区域低地填充：近河低地（含河湾内侧半岛）地表不得低于邻近最高水位
                                float wyMax = MaxWaterIn(x, z, Mathf.Min(d + 2f, 16f));
                                float fade = 1f - Mathf.Clamp01((d - 10f) / 6f);   // 10 内全强制，10->16 渐隐
                                if (fade > 0.01f)
                                {
                                    float floor = wyMax + 0.08f + 0.02f;
                                    if (y < floor) y = Mathf.Lerp(y, floor, fade);
                                }
                            }
                        }
                        H[zi * NX + xi] = y;
                    }
                }
                foreach (var qc in quayCenters) PadLower(new Vector2(qc.x, qc.z), qc.y + 0.03f, 1.9f, 3.4f);
                foreach (var pad in pads) PadFlatten(pad.c, pad.y, pad.r, pad.r + 2.0f);

                var mesh = BuildTerrainMesh();
                var existing = AssetDatabase.LoadAssetAtPath<Mesh>(NewTerrainPath);
                if (existing != null) AssetDatabase.DeleteAsset(NewTerrainPath);
                AssetDatabase.CreateAsset(mesh, NewTerrainPath);
                var terrainT = find("Continuous_Terrain");
                terrainT.GetComponent<MeshFilter>().sharedMesh = mesh;
                var tcol = terrainT.GetComponent<MeshCollider>();
                tcol.sharedMesh = null; tcol.sharedMesh = mesh;
                terrainT.GetComponent<MeshRenderer>().sharedMaterials = new[] {
                    AssetDatabase.LoadAssetAtPath<Material>(GrassMatPath),
                    AssetDatabase.LoadAssetAtPath<Material>(SandMatPath) };
                report.AppendLine("地形 v8 已替换（坡度受控水位 + 弯道岸线强制）");

                // ---------- 4: 道路贴合 ----------
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
                            float ny = HeightAt(w.x, w.z) + (w.y - OrigH(w.x, w.z));
                            float dR, wyR; Vector3 tnR, nrR;
                            if (NearestRiver(w.x, w.z, out dR, out wyR, out tnR, out nrR) && dR < 2.5f)
                                ny = Mathf.Max(ny, Mathf.Lerp(w.y, ny, dR / 2.5f));
                            if (Mathf.Abs(ny - w.y) > 0.005f) changed = true;
                            world[i] = new Vector3(w.x, ny, w.z);
                        }
                        if (!changed) continue;
                        var nm = UnityEngine.Object.Instantiate(src);
                        nm.name = src.name.Replace("_GroundFit_v6", "") + "_GroundFit_v8";
                        var local = new Vector3[mv.Length];
                        for (int i = 0; i < mv.Length; i++)
                            local[i] = mf.transform.InverseTransformPoint(world[i]);
                        nm.vertices = local;
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
                report.AppendLine("道路贴合 v8 " + fitted + " 个");

                RebuildOverlay(find, roots);

                // ---------- 5: 水驿拆分件（P 轮已拆好）仅房屋件重贴基台 ----------
                foreach (var key in new[] { "S04", "S05" })
                {
                    var a = anchors.FirstOrDefault(t => t.name.StartsWith(key));
                    if (a == null) continue;
                    Transform split = null;
                    foreach (Transform c in a) if (c.name == "WaterStation_Split_20260611N") { split = c; break; }
                    if (split == null) continue;
                    foreach (Transform piece in split)
                    {
                        if (piece.name != "House_OnLand") continue;
                        var rsH = piece.GetComponentsInChildren<Renderer>(true);
                        if (rsH.Length == 0) continue;
                        Bounds hb = rsH[0].bounds;
                        for (int i = 1; i < rsH.Length; i++) hb.Encapsulate(rsH[i].bounds);
                        float padY = quayCenters[key == "S04" ? 0 : 1].y + 0.03f;
                        piece.position += new Vector3(0, (padY - 0.05f) - hb.min.y, 0);
                        report.AppendLine(key + ": 房屋件重贴基台");
                    }
                }

                // ---------- 6: 陆地建筑/植被/旗帜贴地 ----------
                foreach (var a in anchors)
                {
                    string key = a.name.Substring(0, 3);
                    if (key == "S04" || key == "S05") continue;
                    var m = modelOf(a); if (m == null) continue;
                    Bounds wb = WB(m);
                    var pad = pads.First(p => p.key == key);
                    m.position += new Vector3(0, (pad.y - 0.04f) - wb.min.y, 0);
                    RelabelAnchor(a, m);
                }
                int reground = 0;
                foreach (var gname in new[] { "Tree_Clusters", "Lingnan_Orchard_Tree_Groves", "Grass_And_Groundcover", "Rock_Clusters" })
                {
                    var g = find(gname); if (g == null) continue;
                    foreach (Transform c in g)
                    {
                        float sink = gname == "Rock_Clusters" ? 0.05f : 0.02f;
                        c.position = new Vector3(c.position.x, HeightAt(c.position.x, c.position.z) - sink, c.position.z);
                        reground++;
                    }
                }
                foreach (var a in anchors)
                    foreach (Transform c in a)
                        if (c.name == "Node_RouteFlag_20260611G")
                            c.position = new Vector3(c.position.x, HeightAt(c.position.x, c.position.z), c.position.z);
                report.AppendLine("植被/旗帜重新贴地 " + reground + " 项");

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
                Debug.LogError("[LycheeFixN] " + ex);
            }
            finally
            {
                File.WriteAllText(ReportPath, report.ToString());
            }
        }

        // 水面纵坡限制：从靠近 anchorLow 的一端开始，向另一端坡度不超过 SlopeCap。
        // fixedLowY 非 NaN 时低端水位用它。返回低端水位。
        static float ReslopeWater(Transform routeT, Vector2 highNode, Vector2 lowJunction, float fixedLowY, string tag)
        {
            var mf = routeT.GetComponent<MeshFilter>();
            var src = mf.sharedMesh;
            var v = src.vertices;
            int n = v.Length / 2;
            // 截面顺序与低端判断
            Vector3 c0 = (routeT.TransformPoint(v[0]) + routeT.TransformPoint(v[1])) * 0.5f;
            Vector3 cN = (routeT.TransformPoint(v[(n - 1) * 2]) + routeT.TransformPoint(v[(n - 1) * 2 + 1])) * 0.5f;
            bool lowAtStart = Vector2.Distance(new Vector2(c0.x, c0.z), lowJunction) <
                              Vector2.Distance(new Vector2(cN.x, cN.z), lowJunction);
            // 弧长
            var centers = new Vector3[n];
            for (int i = 0; i < n; i++)
                centers[i] = (routeT.TransformPoint(v[i * 2]) + routeT.TransformPoint(v[i * 2 + 1])) * 0.5f;
            var arc = new float[n];
            for (int i = 1; i < n; i++)
                arc[i] = arc[i - 1] + Vector2.Distance(new Vector2(centers[i].x, centers[i].z), new Vector2(centers[i - 1].x, centers[i - 1].z));
            float total = arc[n - 1];
            int lowIdx = lowAtStart ? 0 : n - 1;
            float lowY = float.IsNaN(fixedLowY) ? centers[lowIdx].y : fixedLowY;
            float maxDrop = 0f;
            var newV = new Vector3[v.Length];
            for (int i = 0; i < n; i++)
            {
                float s = lowAtStart ? arc[i] : total - arc[i];
                // 原有抬升量与坡度上限取小：只压陡坡，不抬缓坡
                float origRise = Mathf.Max(centers[i].y - lowY, 0f);
                float yNew = lowY + Mathf.Min(Mathf.Min(origRise, s * SlopeCap), 0.45f);
                Vector3 wl = routeT.TransformPoint(v[i * 2]);
                Vector3 wr = routeT.TransformPoint(v[i * 2 + 1]);
                maxDrop = Mathf.Max(maxDrop, Mathf.Abs(centers[i].y - yNew));
                newV[i * 2] = routeT.InverseTransformPoint(new Vector3(wl.x, yNew, wl.z));
                newV[i * 2 + 1] = routeT.InverseTransformPoint(new Vector3(wr.x, yNew, wr.z));
            }
            var nm = UnityEngine.Object.Instantiate(src);
            nm.name = tag + "_Route_Sloped_v8";
            nm.vertices = newV;
            nm.RecalculateNormals();
            nm.RecalculateBounds();
            string ap = MeshDir + "/" + nm.name + ".asset";
            var oldA = AssetDatabase.LoadAssetAtPath<Mesh>(ap);
            if (oldA != null) AssetDatabase.DeleteAsset(ap);
            AssetDatabase.CreateAsset(nm, ap);
            mf.sharedMesh = nm;
            report.AppendLine(string.Format("{0} 水面重坡：低端 {1:F2}，最大调整 {2:F2}", tag, lowY, maxDrop));
            return lowY;
        }

        static float MaxWaterIn(float x, float z, float radius)
        {
            float best = float.MinValue;
            foreach (var line in riverCenters)
                foreach (var c in line)
                    if (Vector2.Distance(new Vector2(x, z), new Vector2(c.x, c.z)) < radius)
                        best = Mathf.Max(best, c.y);
            if (best == float.MinValue)
            {
                float d, wy; Vector3 t, nr;
                NearestRiver(x, z, out d, out wy, out t, out nr);
                best = wy;
            }
            return best;
        }

        // ---------- GLB 连通块拆分 ----------
        static void SplitWaterStation(string key, Transform anchor, Transform model, Vector3 dirWater, float waterY, Vector3 quayC)
        {
            // 删除旧拆分
            Transform oldSplit = null;
            foreach (Transform c in anchor) if (c.name == SplitRootName) { oldSplit = c; break; }
            if (oldSplit != null) UnityEngine.Object.DestroyImmediate(oldSplit.gameObject);

            var mfs = model.GetComponentsInChildren<MeshFilter>(true);
            MeshFilter srcMf = null;
            foreach (var mf in mfs)
                if (mf.sharedMesh != null && (srcMf == null || mf.sharedMesh.vertexCount > srcMf.sharedMesh.vertexCount)) srcMf = mf;
            if (srcMf == null) { report.AppendLine(key + ": 无网格，跳过拆分"); return; }
            var srcMesh = srcMf.sharedMesh;
            var srcRend = srcMf.GetComponent<Renderer>();
            var mats = srcRend != null ? srcRend.sharedMaterials : new Material[0];
            var verts = srcMesh.vertices;
            var tris = srcMesh.triangles;
            var mtx = srcMf.transform.localToWorldMatrix;

            // 并查集求连通块（按共享顶点）
            int vn = verts.Length;
            var parent = new int[vn];
            for (int i = 0; i < vn; i++) parent[i] = i;
            Func<int, int> findRoot = null;
            findRoot = i => { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; };
            Action<int, int> union = (x, y) => { int rx = findRoot(x), ry = findRoot(y); if (rx != ry) parent[rx] = ry; };
            for (int t = 0; t < tris.Length; t += 3)
            { union(tris[t], tris[t + 1]); union(tris[t + 2], tris[t]); }

            // 每个连通块的世界质心与高度范围（抽样）
            var compSum = new Dictionary<int, Vector3>();
            var compCnt = new Dictionary<int, int>();
            var compMaxY = new Dictionary<int, float>();
            var compMinY = new Dictionary<int, float>();
            for (int i = 0; i < vn; i += 3)
            {
                int r = findRoot(i);
                Vector3 w = mtx.MultiplyPoint3x4(verts[i]);
                Vector3 s; compSum.TryGetValue(r, out s); compSum[r] = s + w;
                int cct; compCnt.TryGetValue(r, out cct); compCnt[r] = cct + 1;
                float my; if (!compMaxY.TryGetValue(r, out my) || w.y > my) compMaxY[r] = Mathf.Max(w.y, compMaxY.ContainsKey(r) ? compMaxY[r] : w.y);
                float ny; if (!compMinY.TryGetValue(r, out ny) || w.y < ny) compMinY[r] = Mathf.Min(w.y, compMinY.ContainsKey(r) ? compMinY[r] : w.y);
            }

            // 沿"陆→水"轴对连通块质心一维聚类成 3 组
            Bounds wb = WB(model);
            Vector3 axis = dirWater.normalized;   // 指向水
            float minProj = float.MaxValue, maxProj = float.MinValue;
            var compProj = new Dictionary<int, float>();
            foreach (var kv in compSum)
            {
                Vector3 cen = kv.Value / compCnt[kv.Key];
                float p = Vector3.Dot(new Vector3(cen.x - wb.center.x, 0, cen.z - wb.center.z), axis);
                compProj[kv.Key] = p;
                minProj = Mathf.Min(minProj, p); maxProj = Mathf.Max(maxProj, p);
            }
            float span = Mathf.Max(maxProj - minProj, 1e-3f);
            // 阈值：后 1/3 为船区，前 1/3 为房区，中间为栈桥；高大块（>60% 高度）强制归房区
            float hRange = wb.size.y;
            var compPiece = new Dictionary<int, int>();   // 0=house 1=dock 2=boat
            foreach (var kv in compProj)
            {
                float tNorm = (kv.Value - minProj) / span;
                int piece = tNorm < 0.34f ? 0 : (tNorm < 0.62f ? 1 : 2);
                float top = compMaxY.ContainsKey(kv.Key) ? compMaxY[kv.Key] : wb.min.y;
                if (piece != 0 && (top - wb.min.y) > hRange * 0.62f && tNorm < 0.55f) piece = 0; // 高楼归房
                compPiece[kv.Key] = piece;
            }

            // 生成三个子网格
            string[] pieceNames = { "House_OnLand", "Dock_AboveWater", "Boat_OnWater" };
            var pieceGOs = new GameObject[3];
            var splitRoot = new GameObject(SplitRootName);
            splitRoot.transform.SetParent(anchor, true);

            for (int piece = 0; piece < 3; piece++)
            {
                var keepTris = new List<int>();
                for (int t = 0; t < tris.Length; t += 3)
                {
                    int r = findRoot(tris[t]);
                    int pc; if (!compPiece.TryGetValue(r, out pc)) pc = 1;
                    if (pc == piece) { keepTris.Add(tris[t]); keepTris.Add(tris[t + 1]); keepTris.Add(tris[t + 2]); }
                }
                if (keepTris.Count == 0) { report.AppendLine(key + ": " + pieceNames[piece] + " 为空"); continue; }
                var remap = new Dictionary<int, int>();
                var nvList = new List<Vector3>();
                var nuvList = new List<Vector2>();
                var uv = srcMesh.uv;
                var ntris = new List<int>(keepTris.Count);
                foreach (var idx in keepTris)
                {
                    int ni;
                    if (!remap.TryGetValue(idx, out ni))
                    {
                        ni = nvList.Count;
                        remap[idx] = ni;
                        // 世界坐标存网格（子物体 transform 取 identity 位置）
                        nvList.Add(mtx.MultiplyPoint3x4(verts[idx]));
                        if (uv != null && uv.Length == vn) nuvList.Add(uv[idx]);
                    }
                    ntris.Add(ni);
                }
                var nm = new Mesh();
                nm.name = key + "_" + pieceNames[piece];
                nm.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                nm.SetVertices(nvList);
                if (nuvList.Count == nvList.Count) nm.SetUVs(0, nuvList);
                nm.SetTriangles(ntris, 0);
                nm.RecalculateNormals();
                nm.RecalculateBounds();
                string ap = SplitDir + "/" + nm.name + ".asset";
                var oldA = AssetDatabase.LoadAssetAtPath<Mesh>(ap);
                if (oldA != null) AssetDatabase.DeleteAsset(ap);
                AssetDatabase.CreateAsset(nm, ap);

                var go = new GameObject(pieceNames[piece]);
                go.transform.SetParent(splitRoot.transform, true);
                go.transform.position = Vector3.zero;   // 网格已是世界坐标
                go.AddComponent<MeshFilter>().sharedMesh = nm;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = mats;
                pieceGOs[piece] = go;
                report.AppendLine(string.Format("{0}: {1} 顶点 {2} 三角形 {3}", key, pieceNames[piece], nvList.Count, ntris.Count / 3));
            }

            // 按水线/基台分别定位（只调 Y）
            if (pieceGOs[0] != null)   // 房屋：基座 = 码头平台 - 0.05
            {
                Bounds hb = WB(pieceGOs[0].transform);
                float padY = quayC.y + 0.03f;
                pieceGOs[0].transform.position += new Vector3(0, (padY - 0.05f) - hb.min.y, 0);
            }
            if (pieceGOs[1] != null)   // 栈桥：甲板面（顶点 y 直方图众数）= 水面 + 0.12
            {
                var dm = pieceGOs[1].GetComponent<MeshFilter>().sharedMesh;
                float deckY = HistogramModeY(dm);
                pieceGOs[1].transform.position += new Vector3(0, (waterY + 0.12f) - deckY, 0);
            }
            if (pieceGOs[2] != null)   // 船：吃水 0.12
            {
                Bounds bb = WB(pieceGOs[2].transform);
                pieceGOs[2].transform.position += new Vector3(0, (waterY - 0.12f) - bb.min.y, 0);
            }

            // 停用原合并模型
            model.gameObject.SetActive(false);
            // 标签按新组合
            var rsAll = splitRoot.GetComponentsInChildren<Renderer>(true);
            if (rsAll.Length > 0)
            {
                Bounds nb = rsAll[0].bounds;
                for (int i = 1; i < rsAll.Length; i++) nb.Encapsulate(rsAll[i].bounds);
                foreach (Transform c in anchor)
                    if (c.name == "Node_Name_CN")
                        c.localPosition = new Vector3(c.localPosition.x, (nb.max.y - anchor.position.y) + 0.75f, c.localPosition.z);
            }
            report.AppendLine(key + ": 拆分完成，原合并模型已停用");
        }

        static float HistogramModeY(Mesh m)
        {
            var v = m.vertices;
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < v.Length; i++) { minY = Mathf.Min(minY, v[i].y); maxY = Mathf.Max(maxY, v[i].y); }
            int bins = 40;
            var hist = new int[bins];
            float range = Mathf.Max(maxY - minY, 1e-4f);
            for (int i = 0; i < v.Length; i++)
            {
                int b = Mathf.Clamp((int)((v[i].y - minY) / range * (bins - 1)), 0, bins - 1);
                hist[b]++;
            }
            int bestB = 0;
            for (int b = 1; b < bins; b++) if (hist[b] > hist[bestB]) bestB = b;
            return minY + (bestB + 0.5f) / bins * range;
        }

        static void RebuildOverlay(Func<string, Transform> find, GameObject[] roots)
        {
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
            int lines = 0;
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
            report.AppendLine("线带/箭头重建 " + lines + " 条");
        }

        static void RelabelAnchor(Transform anchor, Transform model)
        {
            Bounds wb = WB(model);
            foreach (Transform c in anchor)
                if (c.name == "Node_Name_CN")
                    c.localPosition = new Vector3(c.localPosition.x, (wb.max.y - anchor.position.y) + 0.75f, c.localPosition.z);
        }

        static Bounds WB(Transform t)
        {
            var rs = t.GetComponentsInChildren<Renderer>(true);
            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return b;
        }

        static Mesh BuildTerrainMesh()
        {
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
            var grass = new List<int>(NX * NZ * 6);
            var sand = new List<int>(20000);
            for (int zi = 0; zi + 1 < NZ; zi++)
                for (int xi = 0; xi + 1 < NX; xi++)
                {
                    int i00 = zi * NX + xi, i10 = i00 + 1, i01 = i00 + NX, i11 = i01 + 1;
                    AddQuadTri(verts, i00, i01, i11, grass, sand);
                    AddQuadTri(verts, i00, i11, i10, grass, sand);
                }
            var mesh = new Mesh();
            mesh.name = "Terrain_RiverSand_v8_20260611Q";
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts; mesh.uv = uv;
            mesh.subMeshCount = 2;
            mesh.SetTriangles(grass, 0);
            mesh.SetTriangles(sand, 1);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
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

        static void PadLower(Vector2 center, float padY, float rIn, float rOut)
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

        static void PadFlatten(Vector2 center, float padY, float rIn, float rOut)
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
    internal static class LycheeMapFix20260611QAutoRun
    {
        static LycheeMapFix20260611QAutoRun()
        {
            EditorApplication.delayCall += () => EditorApplication.delayCall += TryRun;
        }

        static void TryRun()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (File.Exists(Path.Combine(root, "LycheeMapFix_20260611Q.done"))) return;
            if (!File.Exists(Path.Combine(root, "LycheeMapFix_20260611P.done"))) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            if (SceneManager.GetActiveScene().name != "LycheeReplayDemo") return;
            LycheeMapFix20260611Q.Run(false);
        }
    }
}
