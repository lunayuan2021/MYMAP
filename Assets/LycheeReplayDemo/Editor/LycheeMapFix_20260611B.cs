// 一键修复 LycheeReplayDemo 地图问题（2026-06-11 批次 B）：
// 1) 地形重建为 0.4m 分辨率，河道平滑下挖，河床/岸坡以子网格使用砂石材质（替代水下草地）
// 2) 江南码头/洞庭水驿：按模型几何自动判定船向，旋转使船朝水、房屋在岸，码头处地形压成低岸平台
// 3) 洞庭水驿周边树木最小间距松弛，全图树/石/草做河道净空
// 4) 全部 15 个节点建筑按相邻节点距离归一化缩放并重新贴地，标签高度按包围盒重算
// 报告输出到 <项目根>/LycheeMapFix_20260611B_report.txt
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
    internal static class LycheeMapFix20260611B
    {
        const string SceneName = "LycheeReplayDemo";
        const string OrigTerrainPath = "Assets/LycheeMap15/GeneratedMeshes/litchi_map_unity_full_coverage_v2_Terrain.asset";
        const string NewTerrainPath = "Assets/LycheeMap15/GeneratedMeshes/FinalRiver/Terrain_RiverSandSubmesh_v5_20260611.asset";
        const string SandMatPath = "Assets/LycheeMap15/Materials/M_RiverBed_SandGravel.mat";
        const string GrassMatPath = "Assets/LycheeMap15/Materials/M_Config_Terrain_Grassland.mat";

        // 地形网格参数
        const float MinX = -40f, MaxX = 40f, MinZ = -30f, MaxZ = 30f;
        const float Step = 0.4f;

        // 河道剖面参数（满足验收约束：水深 0.72，岸坡 1.35，水面半宽 1.575）
        const float BedDepth = 0.72f;
        const float BedHalf = 1.05f;       // 平坦河床半宽
        const float WaterHalf = 1.575f;    // 水面半宽
        const float ShoreCross = 1.45f;    // 地形上穿水面线的位置（在水面边缘内侧）
        const float BankLipEnd = 1.85f;    // 岸口顶完成位置
        const float CarveOuter = 2.95f;    // 岸坡过渡结束（与原地形融合）
        const float SandOuter = 1.95f;     // 砂石子网格的最大中心线距离

        static StringBuilder report;
        static List<Vector3[]> riverCenters = new List<Vector3[]>();
        static List<Vector2> roadXZ = new List<Vector2>();
        static float[] H;       // 新地形高度
        static int NX, NZ;
        static float waterYAtS04, waterYAtS05;

        static string MarkerPath => Path.GetFullPath(Path.Combine(Application.dataPath, "../LycheeMapFix_20260611B.done"));
        static string ReportPath => Path.GetFullPath(Path.Combine(Application.dataPath, "../LycheeMapFix_20260611B_report.txt"));

        [MenuItem("Tools/Lychee/修复地图 20260611B（河床+码头+树+建筑）")]
        public static void RunMenu()
        {
            Run(force: true);
        }

        public static void Run(bool force)
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.name != SceneName)
            {
                Debug.LogWarning("[LycheeFixB] 当前场景不是 " + SceneName + "，请先打开该场景再执行 Tools/Lychee 菜单。");
                return;
            }
            if (!force && File.Exists(MarkerPath)) return;

            report = new StringBuilder();
            report.AppendLine("LycheeMapFix_20260611B 执行报告  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            try
            {
                BackupScene(scene);
                var roots = scene.GetRootGameObjects();
                GatherRivers(roots);
                GatherRoads(roots);
                BuildTerrainHeights();

                var anchors = FindAnchors(roots);
                NormalizeStationScales(anchors);
                FixWaterStation(anchors, "S04", isDock: true);
                FixWaterStation(anchors, "S05", isDock: false);
                FinalizeTerrainMesh(roots);
                CleanupLegacy(roots);
                FixVegetation(roots, anchors);
                GroundStationsAndLabels(anchors);
                ValidateRiver();

                File.WriteAllText(MarkerPath, DateTime.Now.ToString("o"));
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                AssetDatabase.SaveAssets();
                report.AppendLine("场景已保存。");
                Debug.Log("[LycheeFixB] 修复完成，报告：" + ReportPath);
            }
            catch (Exception ex)
            {
                report.AppendLine("异常: " + ex);
                Debug.LogError("[LycheeFixB] 失败: " + ex);
            }
            finally
            {
                File.WriteAllText(ReportPath, report.ToString());
            }
        }

        static void BackupScene(Scene scene)
        {
            string src = Path.GetFullPath(Path.Combine(Application.dataPath, "..", scene.path));
            string bak = src + ".bak_20260611B";
            if (!File.Exists(bak)) File.Copy(src, bak);
            report.AppendLine("场景备份: " + bak);
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

        // ---------- 河道与道路数据 ----------
        static void GatherRivers(GameObject[] roots)
        {
            riverCenters.Clear();
            foreach (var routeName in new[] { "E12_Route", "E13_Route" })
            {
                var t = FindDeep(roots, routeName);
                if (t == null) { report.AppendLine("警告：未找到 " + routeName); continue; }
                var mf = t.GetComponent<MeshFilter>();
                var v = mf.sharedMesh.vertices;
                var centers = new Vector3[v.Length / 2];
                for (int i = 0; i < centers.Length; i++)
                {
                    Vector3 a = t.TransformPoint(v[i * 2]);
                    Vector3 b = t.TransformPoint(v[i * 2 + 1]);
                    centers[i] = (a + b) * 0.5f;
                }
                riverCenters.Add(centers);
                report.AppendLine(routeName + " 中心线截面数=" + centers.Length);
            }
        }

        static void GatherRoads(GameObject[] roots)
        {
            roadXZ.Clear();
            foreach (var groupName in new[] { "OfficialRoad", "MountainRoute", "BranchRoute" })
            {
                var g = FindDeep(roots, groupName);
                if (g == null) continue;
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
            report.AppendLine("道路顶点数=" + roadXZ.Count);
        }

        // 最近河道中心线：返回水平距离、该处水面高度、切线
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

        // ---------- 地形重建 ----------
        static float[,] origGrid;   // 81 x 61

        static void BuildTerrainHeights()
        {
            var orig = AssetDatabase.LoadAssetAtPath<Mesh>(OrigTerrainPath);
            if (orig == null) throw new Exception("找不到原始地形 " + OrigTerrainPath);
            origGrid = new float[81, 61];
            var flag = new bool[81, 61];
            foreach (var v in orig.vertices)
            {
                int xi = Mathf.RoundToInt(v.x - MinX);
                int zi = Mathf.RoundToInt(v.z - MinZ);
                if (xi >= 0 && xi < 81 && zi >= 0 && zi < 61) { origGrid[xi, zi] = v.y; flag[xi, zi] = true; }
            }
            // 简单补洞
            for (int xi = 0; xi < 81; xi++)
                for (int zi = 0; zi < 61; zi++)
                    if (!flag[xi, zi]) origGrid[xi, zi] = OrigSafe(xi - 1, zi);

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
                    if (NearestRiver(x, z, out d, out wy, out tan, out near) && d < CarveOuter)
                        y = CarveProfile(x, z, y, d, wy);
                    H[zi * NX + xi] = y;
                }
            }
            report.AppendLine(string.Format("新地形网格 {0}x{1}（step={2}）", NX, NZ, Step));
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

        static float Cos01(float t)
        {
            t = Mathf.Clamp01(t);
            return 0.5f - 0.5f * Mathf.Cos(t * Mathf.PI);
        }

        static float CarveProfile(float x, float z, float origY, float d, float waterY)
        {
            float bedY = waterY - BedDepth + (Mathf.PerlinNoise(x * 1.7f, z * 1.7f) - 0.5f) * 0.07f;
            float lip = Mathf.Max(origY, waterY + 0.10f);   // 岸口顶：高于水面 0.10（约束 4.1）
            float target;
            if (d <= BedHalf)
                target = bedY;
            else if (d <= ShoreCross)
                target = Mathf.Lerp(bedY, waterY - 0.06f, Cos01((d - BedHalf) / (ShoreCross - BedHalf)));
            else if (d <= BankLipEnd)
                target = Mathf.Lerp(waterY - 0.06f, lip, Cos01((d - ShoreCross) / (BankLipEnd - ShoreCross)));
            else
                target = Mathf.Lerp(lip, origY, Cos01((d - BankLipEnd) / (CarveOuter - BankLipEnd)));

            float y = (d > 2.2f) ? Mathf.Min(origY, target) : target;

            // 道路保护：路基附近河床抬升为浅滩，避免路面悬空
            float rd = RoadDist(x, z);
            if (rd < 1.0f)
            {
                float protect = 1f - Cos01(rd / 1.0f);
                y = Mathf.Lerp(y, Mathf.Max(y, waterY - 0.16f), protect);
            }
            return y;
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

        // 低岸码头平台：在房屋质心附近把地形压到接近水面（只降不升）
        static void ApplyQuayPad(Vector2 center, float waterY)
        {
            float padY = waterY + 0.03f;
            for (int zi = 0; zi < NZ; zi++)
            {
                float z = MinZ + zi * Step;
                for (int xi = 0; xi < NX; xi++)
                {
                    float x = MinX + xi * Step;
                    float d = Vector2.Distance(new Vector2(x, z), center);
                    if (d > 3.4f) continue;
                    int idx = zi * NX + xi;
                    float t = d <= 1.9f ? 0f : Cos01((d - 1.9f) / (3.4f - 1.9f));
                    float padded = Mathf.Lerp(padY, H[idx], t);
                    if (padded < H[idx]) H[idx] = padded;
                }
            }
        }

        static void FinalizeTerrainMesh(GameObject[] roots)
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
            mesh.name = "Terrain_RiverSandSubmesh_v5_20260611";
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.uv = uv;
            mesh.subMeshCount = 2;
            mesh.SetTriangles(grassTris, 0);
            mesh.SetTriangles(sandTris, 1);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(NewTerrainPath);
            if (existing != null) AssetDatabase.DeleteAsset(NewTerrainPath);
            AssetDatabase.CreateAsset(mesh, NewTerrainPath);

            var terrainT = FindDeep(roots, "Continuous_Terrain");
            if (terrainT == null) throw new Exception("找不到 Continuous_Terrain");
            terrainT.GetComponent<MeshFilter>().sharedMesh = mesh;
            var col = terrainT.GetComponent<MeshCollider>();
            if (col != null) { col.sharedMesh = null; col.sharedMesh = mesh; }

            var grass = AssetDatabase.LoadAssetAtPath<Material>(GrassMatPath);
            var sand = AssetDatabase.LoadAssetAtPath<Material>(SandMatPath);
            if (sand == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                sand = new Material(shader);
                sand.SetColor("_BaseColor", new Color(0.60f, 0.52f, 0.40f));
                sand.SetFloat("_Smoothness", 0.05f);
                AssetDatabase.CreateAsset(sand, SandMatPath);
            }
            terrainT.GetComponent<MeshRenderer>().sharedMaterials = new[] { grass, sand };
            report.AppendLine(string.Format("地形已替换：草地三角形 {0}，砂石三角形 {1}", grassTris.Count / 3, sandTris.Count / 3));
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

        // ---------- 旧补丁清理 ----------
        static void CleanupLegacy(GameObject[] roots)
        {
            string[] doomed = {
                "River_BankAligned_Depth_v4_20260611", "River_Depth_Fix_20260610",
                "Water_And_Mountain_Fix_20260610", "Demo_Detail_Fix_20260611",
                "E12_RiverBank", "E13_RiverBank", "E12_WaterHighlight", "E13_WaterHighlight",
                "E12_VisibleRiverBed", "E13_VisibleRiverBed", "TEMP_TerrainSampler"
            };
            int n = 0;
            foreach (var name in doomed)
            {
                Transform t;
                while ((t = FindDeep(roots, name)) != null)
                {
                    UnityEngine.Object.DestroyImmediate(t.gameObject);
                    n++;
                }
            }
            report.AppendLine("清理旧补丁对象 " + n + " 个");
        }

        // ---------- 节点建筑 ----------
        static Dictionary<string, Transform> FindAnchors(GameObject[] roots)
        {
            var dict = new Dictionary<string, Transform>();
            foreach (var t in FindAllDeep(roots, n => n.StartsWith("S") && n.Contains("_ANCHOR_")))
                dict[t.name.Substring(0, 3)] = t;
            report.AppendLine("找到节点锚点 " + dict.Count + " 个");
            return dict;
        }

        static Transform ModelOf(Transform anchor)
        {
            foreach (Transform c in anchor)
                if (c.name == "Imported_Station_Model") return c;
            return null;
        }

        static Bounds WorldBounds(Transform t)
        {
            var rs = t.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0) return new Bounds(t.position, Vector3.one);
            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return b;
        }

        static void NormalizeStationScales(Dictionary<string, Transform> anchors)
        {
            report.AppendLine("--- 建筑缩放归一化 ---");
            var keys = anchors.Keys.ToList();
            foreach (var key in keys)
            {
                var anchor = anchors[key];
                var model = ModelOf(anchor);
                if (model == null) { report.AppendLine(key + ": 无模型，跳过"); continue; }

                float minNeighbor = float.MaxValue;
                foreach (var other in keys)
                {
                    if (other == key) continue;
                    var a = anchor.position; var b = anchors[other].position;
                    float dd = Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
                    minNeighbor = Mathf.Min(minNeighbor, dd);
                }
                Bounds wb = WorldBounds(model);
                float footprint = Mathf.Max(wb.size.x, wb.size.z);
                float target = Mathf.Clamp(0.62f * minNeighbor, 1.7f, 3.4f);
                if (footprint < 0.01f) continue;
                float f = target / footprint;
                if (f > 0.95f && f < 1.3f)
                {
                    report.AppendLine(string.Format("{0}: 足印 {1:F2} 邻距 {2:F2} -> 保持", key, footprint, minNeighbor));
                    continue;
                }
                f = Mathf.Clamp(f, 0.3f, 1.6f);
                model.localScale = model.localScale * f;
                report.AppendLine(string.Format("{0}: 足印 {1:F2} 邻距 {2:F2} 目标 {3:F2} 缩放系数 {4:F2}", key, footprint, minNeighbor, target, f));
            }
        }

        // 水驿：旋转（船朝水）+ 平移（房在岸上）+ 码头平台 + 吃水
        static void FixWaterStation(Dictionary<string, Transform> anchors, string key, bool isDock)
        {
            if (!anchors.ContainsKey(key)) return;
            var anchor = anchors[key];
            var model = ModelOf(anchor);
            if (model == null) return;
            report.AppendLine("--- 水驿 " + key + " ---");

            // 世界空间采样模型顶点，按高度分簇
            var cells = new Dictionary<Vector2Int, float>();   // 最大高度
            var cellCnt = new Dictionary<Vector2Int, int>();
            Bounds wb = WorldBounds(model);
            float cell = Mathf.Max(wb.size.x, wb.size.z) / 12f + 1e-4f;
            float minY = float.MaxValue, maxY = float.MinValue;
            try
            {
                foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
                {
                    var mesh = mf.sharedMesh; if (mesh == null) continue;
                    var v = mesh.vertices;
                    int stride = Mathf.Max(1, v.Length / 30000);
                    var m = mf.transform.localToWorldMatrix;
                    for (int i = 0; i < v.Length; i += stride)
                    {
                        Vector3 w = m.MultiplyPoint3x4(v[i]);
                        var c = new Vector2Int(Mathf.FloorToInt((w.x - wb.min.x) / cell), Mathf.FloorToInt((w.z - wb.min.z) / cell));
                        float h; cells.TryGetValue(c, out h);
                        if (w.y > h || !cells.ContainsKey(c)) cells[c] = w.y;
                        int cnt; cellCnt.TryGetValue(c, out cnt); cellCnt[c] = cnt + 1;
                        if (w.y < minY) minY = w.y;
                        if (w.y > maxY) maxY = w.y;
                    }
                }
            }
            catch (Exception e)
            {
                report.AppendLine(key + ": 顶点不可读(" + e.Message + ")，跳过朝向修复");
                return;
            }
            if (cells.Count < 8) { report.AppendLine(key + ": 采样过少，跳过"); return; }

            float hRange = maxY - minY;
            Vector2 houseC = Vector2.zero, lowC = Vector2.zero;
            float houseW = 0, lowW = 0;
            foreach (var kv in cells)
            {
                Vector2 ctr = new Vector2(wb.min.x + (kv.Key.x + 0.5f) * cell, wb.min.z + (kv.Key.y + 0.5f) * cell);
                float rel = (kv.Value - minY) / Mathf.Max(hRange, 1e-4f);
                int cnt = cellCnt[kv.Key];
                if (rel >= 0.60f) { houseC += ctr * cnt; houseW += cnt; }
                else if (rel <= 0.30f && cnt >= 4) { lowC += ctr; lowW += 1; }
            }
            if (houseW < 1 || lowW < 1) { report.AppendLine(key + ": 高低簇判定失败，跳过朝向"); return; }
            houseC /= houseW; lowC /= lowW;
            Vector3 boatDir3 = new Vector3(lowC.x - houseC.x, 0, lowC.y - houseC.y).normalized;

            // 目标方向：从房屋一侧垂直指向河道中心线
            float d, wy; Vector3 tan, near;
            Vector3 mc = wb.center;
            NearestRiver(mc.x, mc.z, out d, out wy, out tan, out near);
            Vector3 N = Vector3.Cross(Vector3.up, tan).normalized;   // 水平垂线
            float sideSign = Mathf.Sign(Vector3.Dot(new Vector3(houseC.x - near.x, 0, houseC.y - near.z), N));
            if (sideSign == 0) sideSign = 1;
            Vector3 targetDir = -N * sideSign;

            Vector3 pivot = wb.center;
            float angle = Vector3.SignedAngle(boatDir3, targetDir, Vector3.up);
            if (Mathf.Abs(angle) > 8f)
            {
                model.RotateAround(pivot, Vector3.up, angle);
                report.AppendLine(string.Format("{0}: 船向偏差 {1:F0}°，已旋转", key, angle));
            }
            else report.AppendLine(key + ": 船向已正确（偏差 " + angle.ToString("F0") + "°）");

            // 旋转后重算几何，平移使船端入水、房屋在岸
            Quaternion rot = Quaternion.AngleAxis(Mathf.Abs(angle) > 8f ? angle : 0f, Vector3.up);
            Vector3 hc3 = pivot + rot * (new Vector3(houseC.x, pivot.y, houseC.y) - pivot);
            Vector3 lc3 = pivot + rot * (new Vector3(lowC.x, pivot.y, lowC.y) - pivot);
            NearestRiver(lc3.x, lc3.z, out d, out wy, out tan, out near);
            N = Vector3.Cross(Vector3.up, tan).normalized;
            float sBoat = Vector3.Dot(new Vector3(lc3.x - near.x, 0, lc3.z - near.z), N) * sideSign;
            float shift = Mathf.Clamp(0.35f - sBoat, -4f, 4f);  // 船簇质心目标：离中心线 0.35（岸侧）
            Vector3 delta = N * sideSign * shift;
            model.position += delta;
            report.AppendLine(string.Format("{0}: 平移 {1:F2} 使船入水（sBoat {2:F2}->0.35）", key, delta.magnitude, sBoat));

            // 房屋质心着陆点
            Vector3 hcAfter = hc3 + delta;
            float sHouse = Vector3.Dot(new Vector3(hcAfter.x - near.x, 0, hcAfter.z - near.z), N) * sideSign;
            if (sHouse < 2.0f) report.AppendLine(key + ": 警告：房屋距中心线仅 " + sHouse.ToString("F2") + "，建议人工复查");

            // 码头平台 + 基座高度（船吃水 0.08，房基入土约 0.11，合并网格折中，记录于报告）
            ApplyQuayPad(new Vector2(hcAfter.x, hcAfter.z), wy);
            wb = WorldBounds(model);
            float targetBase = wy - 0.08f;
            model.position += new Vector3(0, targetBase - wb.min.y, 0);
            report.AppendLine(string.Format("{0}: 基座 y={1:F2}（水面 {2:F2}，吃水 0.08）", key, targetBase, wy));
            if (key == "S04") waterYAtS04 = wy; else waterYAtS05 = wy;
        }

        // ---------- 植被 ----------
        static void FixVegetation(GameObject[] roots, Dictionary<string, Transform> anchors)
        {
            report.AppendLine("--- 植被 ---");
            var treeGroups = new[] { "Tree_Clusters", "Lingnan_Orchard_Tree_Groves" };
            var trees = new List<Transform>();
            foreach (var g in treeGroups)
            {
                var t = FindDeep(roots, g);
                if (t == null) continue;
                foreach (Transform c in t) trees.Add(c);
            }

            int riverMoved = 0;
            foreach (var t in trees)
            {
                Vector3 p = t.position;
                float d, wy; Vector3 tan, near;
                if (NearestRiver(p.x, p.z, out d, out wy, out tan, out near) && d < 2.05f)
                {
                    Vector3 dir = new Vector3(p.x - near.x, 0, p.z - near.z);
                    dir = dir.sqrMagnitude < 1e-6f ? Vector3.Cross(Vector3.up, tan) : dir.normalized;
                    p = new Vector3(near.x, 0, near.z) + dir * 2.2f;
                    t.position = new Vector3(p.x, HeightAt(p.x, p.z) - 0.02f, p.z);
                    riverMoved++;
                }
            }

            // S05 周边树间距松弛
            Transform s05;
            anchors.TryGetValue("S05", out s05);
            if (s05 != null)
            {
                Vector2 a = new Vector2(s05.position.x, s05.position.z);
                var local = trees.Where(t => Vector2.Distance(new Vector2(t.position.x, t.position.z), a) < 9f).ToList();
                const float minSep = 2.1f;
                for (int iter = 0; iter < 50; iter++)
                {
                    bool moved = false;
                    for (int i = 0; i < local.Count; i++)
                        for (int j = i + 1; j < local.Count; j++)
                        {
                            Vector3 pi = local[i].position, pj = local[j].position;
                            Vector2 diff = new Vector2(pi.x - pj.x, pi.z - pj.z);
                            float dd = diff.magnitude;
                            if (dd < minSep && dd > 1e-4f)
                            {
                                Vector2 push = diff / dd * (minSep - dd) * 0.5f;
                                local[i].position = pi + new Vector3(push.x, 0, push.y);
                                local[j].position = pj - new Vector3(push.x, 0, push.y);
                                moved = true;
                            }
                        }
                    // 同步保持河道净空
                    foreach (var t in local)
                    {
                        Vector3 p = t.position;
                        float d, wy; Vector3 tan, near;
                        if (NearestRiver(p.x, p.z, out d, out wy, out tan, out near) && d < 2.05f)
                        {
                            Vector3 dir = new Vector3(p.x - near.x, 0, p.z - near.z).normalized;
                            p = new Vector3(near.x, 0, near.z) + dir * 2.2f;
                            t.position = p;
                            moved = true;
                        }
                    }
                    if (!moved) break;
                }
                foreach (var t in local)
                {
                    Vector3 p = t.position;
                    t.position = new Vector3(p.x, HeightAt(p.x, p.z) - 0.02f, p.z);
                }
                report.AppendLine("S05 周边松弛树木 " + local.Count + " 棵（最小间距 " + minSep + "）");
            }
            report.AppendLine("河道净空移树 " + riverMoved + " 棵");

            // 草与石：河道内删除/外移
            var grassG = FindDeep(roots, "Grass_And_Groundcover");
            int grassDel = 0;
            if (grassG != null)
            {
                var doomed = new List<GameObject>();
                foreach (Transform c in grassG)
                {
                    float d, wy; Vector3 tan, near;
                    if (NearestRiver(c.position.x, c.position.z, out d, out wy, out tan, out near) && d < 1.78f)
                        doomed.Add(c.gameObject);
                    else
                    {
                        Vector3 p = c.position;
                        c.position = new Vector3(p.x, HeightAt(p.x, p.z) - 0.01f, p.z);
                    }
                }
                foreach (var g in doomed) UnityEngine.Object.DestroyImmediate(g);
                grassDel = doomed.Count;
            }
            var rockG = FindDeep(roots, "Rock_Clusters");
            int rockMoved = 0;
            if (rockG != null)
            {
                foreach (Transform c in rockG)
                {
                    Vector3 p = c.position;
                    float d, wy; Vector3 tan, near;
                    if (NearestRiver(p.x, p.z, out d, out wy, out tan, out near) && d < 1.9f)
                    {
                        Vector3 dir = new Vector3(p.x - near.x, 0, p.z - near.z);
                        dir = dir.sqrMagnitude < 1e-6f ? Vector3.Cross(Vector3.up, tan) : dir.normalized;
                        p = new Vector3(near.x, 0, near.z) + dir * 2.1f;
                        rockMoved++;
                    }
                    c.position = new Vector3(p.x, HeightAt(p.x, p.z) - 0.03f, p.z);
                }
            }
            report.AppendLine("河道内删除草 " + grassDel + " 处，移石 " + rockMoved + " 块");
        }

        // ---------- 贴地与标签 ----------
        static void GroundStationsAndLabels(Dictionary<string, Transform> anchors)
        {
            report.AppendLine("--- 贴地与标签 ---");
            foreach (var kv in anchors)
            {
                var anchor = kv.Value;
                var model = ModelOf(anchor);
                if (model != null && kv.Key != "S04" && kv.Key != "S05")
                {
                    Bounds wb = WorldBounds(model);
                    float ground = HeightAt(wb.center.x, wb.center.z);
                    float dy = (ground - 0.04f) - wb.min.y;
                    if (Mathf.Abs(dy) > 0.005f) model.position += new Vector3(0, dy, 0);
                }
                // 标签高度
                Transform label = null;
                foreach (Transform c in anchor) if (c.name == "Node_Name_CN") { label = c; break; }
                if (label != null && model != null)
                {
                    Bounds wb2 = WorldBounds(model);
                    var lp = label.localPosition;
                    label.localPosition = new Vector3(lp.x, (wb2.max.y - anchor.position.y) + 0.75f, lp.z);
                }
            }
        }

        // ---------- 验收 ----------
        static void ValidateRiver()
        {
            report.AppendLine("--- 河道自动验收 ---");
            int bad = 0, n = 0;
            foreach (var line in riverCenters)
            {
                foreach (var c in line)
                {
                    float bed = HeightAt(c.x, c.z);
                    float depth = c.y - bed;
                    n++;
                    if (depth < 0.45f || depth > 1.1f) bad++;
                }
            }
            report.AppendLine(string.Format("中心线水深采样 {0} 点，超出 [0.45,1.10] 的 {1} 点", n, bad));
        }
    }

    [InitializeOnLoad]
    internal static class LycheeMapFix20260611BAutoRun
    {
        static LycheeMapFix20260611BAutoRun()
        {
            EditorApplication.delayCall += () => EditorApplication.delayCall += TryRun;
        }

        static void TryRun()
        {
            string marker = Path.GetFullPath(Path.Combine(Application.dataPath, "../LycheeMapFix_20260611B.done"));
            if (File.Exists(marker)) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            var scene = SceneManager.GetActiveScene();
            if (scene.name != "LycheeReplayDemo")
            {
                Debug.Log("[LycheeFixB] 待执行：请打开 LycheeReplayDemo 场景，修复将自动运行（或用 Tools/Lychee 菜单）。");
                return;
            }
            LycheeMapFix20260611B.Run(force: false);
        }
    }
}
