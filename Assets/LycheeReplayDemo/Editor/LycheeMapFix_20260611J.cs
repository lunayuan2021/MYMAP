// 第九轮修复（2026-06-11 批次 J）——节点底座与 UI 质感：
// 1) 删除黄色圆盘光圈；每个陆地节点把地形压平成圆形基台（消除建筑底座悬浮），贴外缘放细环形标记（按路线色）
// 2) S15 兴庆宫放大到足印 4.6（终点地标）、S14 朱雀门 2.8、S01 岭南果园 6.5（起点）
// 3) 挂 WaterReflectionCamera（运行时倒影），创建专用反射平面
// 4) S05 东南视线走廊清树（主镜头方向）
// 5) Global_PostProcessing 提升 Bloom，让路线线带与节点环发光
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace LycheeReplayDemoTools
{
    internal static class LycheeMapFix20260611J
    {
        static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string MarkerPath => Path.Combine(Root, "LycheeMapFix_20260611J.done");
        static string ReportPath => Path.Combine(Root, "LycheeMapFix_20260611J_report.txt");
        const string TerrainPath = "Assets/LycheeMap15/GeneratedMeshes/FinalRiver/Terrain_RiverSandSubmesh_v5_20260611.asset";
        const string MeshDir = "Assets/LycheeMap15/GeneratedMeshes/RouteOverlay_20260611G";
        const string RingName = "Node_RingMarker_20260611J";

        static readonly Dictionary<string, string> NodeRouteType = new Dictionary<string, string> {
            {"S01","ROAD"},{"S02","ROAD"},{"S03","ROAD"},{"S04","WATER"},{"S05","WATER"},
            {"S06","MOUNTAIN"},{"S07","ROAD"},{"S08","MOUNTAIN"},{"S09","ROAD"},{"S10","BRANCH"},
            {"S11","ROAD"},{"S12","ROAD"},{"S13","ROAD"},{"S14","ROAD"},{"S15","ROAD"},
        };

        [MenuItem("Tools/Lychee/修复地图 20260611J（基台+节点环+终点放大+倒影+Bloom）")]
        public static void RunMenu() { Run(true); }

        public static void Run(bool force)
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.name != "LycheeReplayDemo") return;
            if (!force && File.Exists(MarkerPath)) return;
            var report = new StringBuilder();
            report.AppendLine("LycheeMapFix_20260611J 报告 " + DateTime.Now.ToString("HH:mm:ss"));
            try
            {
                var roots = scene.GetRootGameObjects();
                var all = new List<Transform>();
                foreach (var r in roots) { if (r != null) all.AddRange(r.GetComponentsInChildren<Transform>(true)); }
                Func<string, Transform> find = n => all.FirstOrDefault(t => t != null && t.name == n);

                var terrainT = find("Continuous_Terrain");
                var tcol = terrainT.GetComponent<MeshCollider>();
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

                // ---------- 0: 删除旧黄圈 ----------
                int delRing = 0;
                foreach (var a in anchors)
                {
                    var doomed = new List<GameObject>();
                    foreach (Transform c in a)
                        if (c.name == "Node_GlowRing_20260611E" || c.name == RingName) doomed.Add(c.gameObject);
                    foreach (var g in doomed) { UnityEngine.Object.DestroyImmediate(g); delRing++; }
                }
                report.AppendLine("移除旧光圈 " + delRing + " 个");

                // ---------- 1: 起终点放大 ----------
                foreach (var kv in new Dictionary<string, float> { { "S15", 4.6f }, { "S14", 2.8f }, { "S01", 6.5f } })
                {
                    var a = anchors.FirstOrDefault(t => t.name.StartsWith(kv.Key));
                    if (a == null) continue;
                    var m = modelOf(a); if (m == null) continue;
                    Bounds wb = wbOf(m);
                    float footprint = Mathf.Max(wb.size.x, wb.size.z);
                    float f = kv.Value / Mathf.Max(footprint, 0.01f);
                    if (Mathf.Abs(f - 1f) > 0.04f)
                    {
                        Vector3 c = wb.center;
                        m.localScale *= f;
                        Vector3 np = c + (m.position - c) * f;
                        m.position = new Vector3(np.x, m.position.y, np.z);
                        // 质心回对锚点
                        wb = wbOf(m);
                        m.position += new Vector3(a.position.x - wb.center.x, 0, a.position.z - wb.center.z);
                    }
                    report.AppendLine(string.Format("{0}: 足印 {1:F2} -> {2:F2}", kv.Key, footprint, kv.Value));
                }

                // ---------- 2: 地形基台压平 + 建筑贴地 ----------
                var tmesh = AssetDatabase.LoadAssetAtPath<Mesh>(TerrainPath);
                var tv = tmesh.vertices;
                var pads = new List<(Vector2 c, float r, float y, string key)>();
                foreach (var a in anchors)
                {
                    string key = a.name.Substring(0, 3);
                    if (key == "S04" || key == "S05") continue;   // 水驿已有码头平台
                    var m = modelOf(a); if (m == null) continue;
                    Bounds wb = wbOf(m);
                    float rPad = Mathf.Clamp(Mathf.Max(wb.extents.x, wb.extents.z) * 0.8f + 0.3f, 1.6f, 3.6f);
                    // 基台高度 = 锚点处现地形高度
                    RaycastHit hit;
                    float padY = a.position.y;
                    if (tcol.Raycast(new Ray(new Vector3(a.position.x, 60, a.position.z), Vector3.down), out hit, 200f))
                        padY = hit.point.y;
                    pads.Add((new Vector2(a.position.x, a.position.z), rPad, padY, key));
                }
                int flattened = 0;
                for (int i = 0; i < tv.Length; i++)
                {
                    foreach (var pad in pads)
                    {
                        float d = Vector2.Distance(new Vector2(tv[i].x, tv[i].z), pad.c);
                        if (d > pad.r + 1.6f) continue;
                        float t = d <= pad.r ? 0f : 0.5f - 0.5f * Mathf.Cos(Mathf.Clamp01((d - pad.r) / 1.6f) * Mathf.PI);
                        float ny = Mathf.Lerp(pad.y, tv[i].y, t);
                        if (Mathf.Abs(ny - tv[i].y) > 0.003f) { tv[i].y = ny; flattened++; }
                    }
                }
                tmesh.vertices = tv;
                tmesh.RecalculateNormals();
                tmesh.RecalculateBounds();
                EditorUtility.SetDirty(tmesh);
                tcol.sharedMesh = null; tcol.sharedMesh = tmesh;
                report.AppendLine("基台压平顶点 " + flattened + " 个（" + pads.Count + " 座）");

                // 建筑贴基台
                foreach (var pad in pads)
                {
                    var a = anchors.First(t => t.name.StartsWith(pad.key));
                    var m = modelOf(a);
                    Bounds wb = wbOf(m);
                    m.position += new Vector3(0, (pad.y - 0.04f) - wb.min.y, 0);
                }

                // ---------- 3: 环形节点标记 ----------
                var typeMats = new Dictionary<string, Material>();
                foreach (var tp in new[] { "ROAD", "WATER", "MOUNTAIN", "BRANCH" })
                    typeMats[tp] = AssetDatabase.LoadAssetAtPath<Material>("Assets/LycheeMap15/Materials/M_RouteLine_" + tp + ".mat");
                int rings = 0;
                foreach (var a in anchors)
                {
                    string key = a.name.Substring(0, 3);
                    float rPad, ringY;
                    var pad = pads.FirstOrDefault(p => p.key == key);
                    if (pad.key == key) { rPad = pad.r; ringY = pad.y; }
                    else
                    {
                        var m5 = modelOf(a);
                        Bounds wb5 = m5 != null ? wbOf(m5) : new Bounds(a.position, Vector3.one * 3);
                        rPad = Mathf.Clamp(Mathf.Max(wb5.extents.x, wb5.extents.z) * 0.8f + 0.3f, 1.6f, 3.6f);
                        ringY = wb5.min.y + 0.10f;   // 水驿：基座上沿
                    }
                    var mesh = BuildAnnulus(rPad + 0.02f, rPad + 0.24f, 56);
                    mesh.name = key + "_RingMarker";
                    string ap = MeshDir + "/" + mesh.name + ".asset";
                    var olda = AssetDatabase.LoadAssetAtPath<Mesh>(ap);
                    if (olda != null) AssetDatabase.DeleteAsset(ap);
                    AssetDatabase.CreateAsset(mesh, ap);
                    var go = new GameObject(RingName);
                    go.transform.SetParent(a, true);
                    go.transform.position = new Vector3(a.position.x, ringY + 0.03f, a.position.z);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = typeMats[NodeRouteType[key]];
                    mr.shadowCastingMode = ShadowCastingMode.Off;
                    rings++;
                }
                report.AppendLine("环形节点标记 " + rings + " 个");

                // ---------- 4: 基台范围内树草外移/贴地 ----------
                var trees = new List<Transform>();
                foreach (var g in new[] { "Tree_Clusters", "Lingnan_Orchard_Tree_Groves" })
                {
                    var gt = find(g); if (gt == null) continue;
                    foreach (Transform c in gt) trees.Add(c);
                }
                var grassG = find("Grass_And_Groundcover");
                int vegMoved = 0;
                Func<Vector2, float> groundY = p =>
                {
                    RaycastHit h;
                    if (tcol.Raycast(new Ray(new Vector3(p.x, 60, p.y), Vector3.down), out h, 200f)) return h.point.y;
                    return 0f;
                };
                foreach (var pad in pads)
                {
                    foreach (var tr in trees)
                    {
                        Vector2 p = new Vector2(tr.position.x, tr.position.z);
                        float d = Vector2.Distance(p, pad.c);
                        if (d < pad.r + 0.5f)
                        {
                            Vector2 dir = d < 1e-4f ? Vector2.up : (p - pad.c) / d;
                            Vector2 np = pad.c + dir * (pad.r + 0.7f);
                            tr.position = new Vector3(np.x, groundY(np) - 0.02f, np.y);
                            vegMoved++;
                        }
                        else if (d < pad.r + 2.2f)
                            tr.position = new Vector3(p.x, groundY(p) - 0.02f, p.y);
                    }
                    if (grassG != null)
                        foreach (Transform c in grassG)
                        {
                            Vector2 p = new Vector2(c.position.x, c.position.z);
                            float d = Vector2.Distance(p, pad.c);
                            if (d < pad.r + 2.2f)
                                c.position = new Vector3(p.x, groundY(p) - 0.01f, p.y);
                        }
                }
                report.AppendLine("基台树木外移/重贴地 " + vegMoved + " 棵");

                // ---------- 5: S05 东南视线走廊清树 ----------
                var s05 = anchors.FirstOrDefault(t => t.name.StartsWith("S05"));
                var cam = find("MainCamera_2.5D");
                if (s05 != null && cam != null)
                {
                    var m5 = modelOf(s05);
                    Bounds b5 = wbOf(m5);
                    Vector2 c5 = new Vector2(b5.center.x, b5.center.z);
                    Vector2 toCam = (new Vector2(cam.position.x, cam.position.z) - c5).normalized;
                    Vector2 perp = new Vector2(-toCam.y, toCam.x);
                    int corridor = 0;
                    foreach (var tr in trees)
                    {
                        Vector2 p = new Vector2(tr.position.x, tr.position.z) - c5;
                        float along = Vector2.Dot(p, toCam);
                        float lateral = Vector2.Dot(p, perp);
                        if (along > 0.5f && along < 9f && Mathf.Abs(lateral) < 2.8f)
                        {
                            float side = lateral >= 0 ? 1f : -1f;
                            Vector2 np = c5 + toCam * along + perp * side * 3.4f;
                            tr.position = new Vector3(np.x, groundY(np) - 0.02f, np.y);
                            corridor++;
                        }
                    }
                    report.AppendLine("S05 视线走廊外移树木 " + corridor + " 棵");
                }

                // ---------- 6: 水面反射相机 ----------
                var oldRef = find("WaterReflectionCamera_20260611J");
                if (oldRef != null) UnityEngine.Object.DestroyImmediate(oldRef.gameObject);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/IgniteCoders/Simple Water Shader/Prefabs/WaterReflectionCamera.prefab");
                if (prefab != null)
                {
                    var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                    inst.name = "WaterReflectionCamera_20260611J";
                    var plane = new GameObject("WaterReflectionPlane_20260611J");
                    plane.transform.position = new Vector3(-9f, -0.85f, -14f);   // 水面平均高度
                    var wr = inst.GetComponent<WaterReflection>();
                    if (wr != null)
                    {
                        wr.reflectionPlane = plane.transform;
                        if (wr.outputTexture == null)
                            wr.outputTexture = AssetDatabase.LoadAssetAtPath<RenderTexture>("Assets/IgniteCoders/Simple Water Shader/Resources/WaterReflection.renderTexture");
                        var rc = inst.GetComponent<Camera>();
                        if (rc != null) rc.targetTexture = wr.outputTexture;
                        report.AppendLine("WaterReflectionCamera 已挂载（运行时倒影生效）");
                    }
                }
                else report.AppendLine("警告：未找到 WaterReflectionCamera.prefab");

                // ---------- 7: Bloom ----------
                var ppT = find("Global_PostProcessing");
                if (ppT != null)
                {
                    var vol = ppT.GetComponent<Volume>();
                    if (vol != null && vol.sharedProfile != null)
                    {
                        Bloom bloom;
                        if (!vol.sharedProfile.TryGet(out bloom)) bloom = vol.sharedProfile.Add<Bloom>(true);
                        bloom.intensity.Override(0.85f);
                        bloom.threshold.Override(0.85f);
                        bloom.scatter.Override(0.6f);
                        EditorUtility.SetDirty(vol.sharedProfile);
                        report.AppendLine("Bloom 已提升（intensity 0.85）");
                    }
                    else report.AppendLine("警告：Global_PostProcessing 无 Volume Profile");
                }

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
                Debug.LogError("[LycheeFixJ] " + ex);
            }
            finally
            {
                File.WriteAllText(ReportPath, report.ToString());
            }
        }

        static Mesh BuildAnnulus(float rIn, float rOut, int seg)
        {
            var verts = new Vector3[seg * 2];
            var tris = new List<int>(seg * 6);
            for (int i = 0; i < seg; i++)
            {
                float ang = i / (float)seg * Mathf.PI * 2f;
                float ca = Mathf.Cos(ang), sa = Mathf.Sin(ang);
                verts[i * 2] = new Vector3(ca * rIn, 0, sa * rIn);
                verts[i * 2 + 1] = new Vector3(ca * rOut, 0, sa * rOut);
                int ni = (i + 1) % seg;
                tris.Add(i * 2); tris.Add(i * 2 + 1); tris.Add(ni * 2 + 1);
                tris.Add(i * 2); tris.Add(ni * 2 + 1); tris.Add(ni * 2);
            }
            var mesh = new Mesh();
            mesh.vertices = verts;
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    [InitializeOnLoad]
    internal static class LycheeMapFix20260611JAutoRun
    {
        static LycheeMapFix20260611JAutoRun()
        {
            EditorApplication.delayCall += () => EditorApplication.delayCall += TryRun;
        }

        static void TryRun()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (File.Exists(Path.Combine(root, "LycheeMapFix_20260611J.done"))) return;
            if (!File.Exists(Path.Combine(root, "LycheeMapFix_20260611I.done"))) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            if (SceneManager.GetActiveScene().name != "LycheeReplayDemo") return;
            LycheeMapFix20260611J.Run(false);
        }
    }
}
