// 第十四轮修复（2026-06-11 批次 O）：
// 1) 河口/弯道水面补丁：S04 河头、S05 转弯、S07 河尾各加圆形水面补丁，消除两条水带间的楔形悬空缺口
// 2) Roadside_Enhancement_20260610 全部重新贴地（历轮地形重建都漏了它）+ 河道净空
// 3) 关闭全部 Node_GroundBlend 棕色圆盘贴片
// 4) 回放相机：maximumSize 22->30、minimumSize 9.5->12、eventSize 8.5->10.5（能看清两队全景，减少跳变）
// 5) URP：MSAA 4x、renderScale 1.0（解决模糊）
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
    internal static class LycheeMapFix20260611O
    {
        static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string MarkerPath => Path.Combine(Root, "LycheeMapFix_20260611O.done");
        static string ReportPath => Path.Combine(Root, "LycheeMapFix_20260611O_report.txt");
        const string MeshDir = "Assets/LycheeMap15/GeneratedMeshes/RouteOverlay_20260611G";
        const string PatchGroupName = "Water_Junction_Patches_20260611O";

        [MenuItem("Tools/Lychee/修复地图 20260611O（水面补丁+贴地+相机清晰度）")]
        public static void RunMenu() { Run(true); }

        public static void Run(bool force)
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.name != "LycheeReplayDemo") return;
            if (!force && File.Exists(MarkerPath)) return;
            var report = new StringBuilder();
            report.AppendLine("LycheeMapFix_20260611O 报告 " + DateTime.Now.ToString("HH:mm:ss"));
            try
            {
                var roots = scene.GetRootGameObjects();
                var all = new List<Transform>();
                foreach (var r in roots) { if (r != null) all.AddRange(r.GetComponentsInChildren<Transform>(true)); }
                Func<string, Transform> find = n => all.FirstOrDefault(t => t != null && t.name == n);

                // 河道中心线
                var centers = new List<Vector3>();
                var lineList = new List<Vector3[]>();
                foreach (var rn in new[] { "E12_Route", "E13_Route" })
                {
                    var t = find(rn); if (t == null) continue;
                    var v = t.GetComponent<MeshFilter>().sharedMesh.vertices;
                    var c = new Vector3[v.Length / 2];
                    for (int i = 0; i < c.Length; i++)
                        c[i] = (t.TransformPoint(v[i * 2]) + t.TransformPoint(v[i * 2 + 1])) * 0.5f;
                    lineList.Add(c);
                    centers.AddRange(c);
                }
                Func<Vector2, (float d, float wy)> nearestRiver = p =>
                {
                    float best = float.MaxValue, wy = -0.85f;
                    foreach (var line in lineList)
                        for (int i = 0; i + 1 < line.Length; i++)
                        {
                            Vector2 a = new Vector2(line[i].x, line[i].z), b = new Vector2(line[i + 1].x, line[i + 1].z);
                            Vector2 ab = b - a; float l2 = ab.sqrMagnitude;
                            float tt = l2 < 1e-8f ? 0 : Mathf.Clamp01(Vector2.Dot(p - a, ab) / l2);
                            Vector2 q = a + ab * tt;
                            float d = Vector2.Distance(p, q);
                            if (d < best) { best = d; wy = Mathf.Lerp(line[i].y, line[i + 1].y, tt); }
                        }
                    return (best, wy);
                };

                // ---------- 1: 水面补丁 ----------
                var oldP = find(PatchGroupName);
                if (oldP != null) UnityEngine.Object.DestroyImmediate(oldP.gameObject);
                var wrGroup = find("WaterRoute_And_River");
                var patchGroup = new GameObject(PatchGroupName);
                if (wrGroup != null) patchGroup.transform.SetParent(wrGroup, true);
                var waterMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/IgniteCoders/Simple Water Shader/Resources/Water_mat_01.mat");

                // 端点位置与水位
                var c12 = lineList[0]; var c13 = lineList[1];
                var patches = new List<(string name, Vector3 pos, float r)>();
                Func<Vector3[], Vector2, Vector3> endNear = (line, node) =>
                    Vector2.Distance(new Vector2(line[0].x, line[0].z), node) <
                    Vector2.Distance(new Vector2(line[line.Length - 1].x, line[line.Length - 1].z), node)
                        ? line[0] : line[line.Length - 1];
                Vector3 s04End = endNear(c12, new Vector2(-18, -22));
                Vector3 s05End = endNear(c12, new Vector2(-2, -18));
                Vector3 s07End = endNear(c13, new Vector2(0, -6));
                patches.Add(("Patch_S04_Head", new Vector3(s04End.x, s04End.y, s04End.z), 1.65f));
                patches.Add(("Patch_S05_Junction", new Vector3(s05End.x, Mathf.Min(s05End.y, endNear(c13, new Vector2(-2, -18)).y), s05End.z), 1.8f));
                patches.Add(("Patch_S07_Tail", new Vector3(s07End.x, s07End.y, s07End.z), 1.65f));

                foreach (var p in patches)
                {
                    var mesh = BuildDisc(p.r, 36);
                    mesh.name = p.name;
                    string ap = MeshDir + "/" + mesh.name + ".asset";
                    var olda = AssetDatabase.LoadAssetAtPath<Mesh>(ap);
                    if (olda != null) AssetDatabase.DeleteAsset(ap);
                    AssetDatabase.CreateAsset(mesh, ap);
                    var go = new GameObject(p.name);
                    go.transform.SetParent(patchGroup.transform, true);
                    go.transform.position = new Vector3(p.pos.x, p.pos.y - 0.005f, p.pos.z);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = waterMat;
                    mr.shadowCastingMode = ShadowCastingMode.Off;
                }
                report.AppendLine("水面补丁 " + patches.Count + " 处（河头/转弯/河尾）");

                // ---------- 2: Roadside_Enhancement 贴地 ----------
                var terrainT = find("Continuous_Terrain");
                var tcol = terrainT.GetComponent<MeshCollider>();
                Func<Vector2, float> groundY = p =>
                {
                    RaycastHit hit;
                    if (tcol.Raycast(new Ray(new Vector3(p.x, 60, p.y), Vector3.down), out hit, 200f)) return hit.point.y;
                    return 0f;
                };
                var rg = find("Roadside_Enhancement_20260610");
                int rgFixed = 0, rgMoved = 0;
                if (rg != null)
                {
                    foreach (Transform c in rg)
                    {
                        Vector3 p = c.position;
                        var nr = nearestRiver(new Vector2(p.x, p.z));
                        if (nr.d < 1.9f)
                        {
                            // 推出河道
                            Vector2 bq = Vector2.zero; float best = float.MaxValue;
                            foreach (var cc in centers)
                            {
                                float dd = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(cc.x, cc.z));
                                if (dd < best) { best = dd; bq = new Vector2(cc.x, cc.z); }
                            }
                            Vector2 dir = (new Vector2(p.x, p.z) - bq);
                            dir = dir.sqrMagnitude < 1e-6f ? Vector2.up : dir.normalized;
                            Vector2 np = bq + dir * 2.1f;
                            p = new Vector3(np.x, p.y, np.y);
                            rgMoved++;
                        }
                        c.position = new Vector3(p.x, groundY(new Vector2(p.x, p.z)) - 0.02f, p.z);
                        rgFixed++;
                    }
                }
                report.AppendLine("Roadside_Enhancement 贴地 " + rgFixed + " 项（出河 " + rgMoved + "）");

                // ---------- 3: 关闭 GroundBlend 棕盘 ----------
                int gbOff = 0;
                foreach (var t in all)
                    if (t != null && t.name.StartsWith("Node_GroundBlend_S") && t.gameObject.activeSelf)
                    { t.gameObject.SetActive(false); gbOff++; }
                report.AppendLine("关闭 GroundBlend 棕盘 " + gbOff + " 个");

                // ---------- 4: 回放相机参数 ----------
                var camT = find("MainCamera_2.5D");
                if (camT != null)
                {
                    var ctrl = camT.GetComponent<LycheeReplayCameraController>();
                    if (ctrl != null)
                    {
                        ctrl.maximumSize = 30f;
                        ctrl.minimumSize = 12f;
                        ctrl.eventSize = 10.5f;
                        ctrl.framingPadding = 8f;
                        EditorUtility.SetDirty(ctrl);
                        report.AppendLine("相机：max 30 / min 12 / event 10.5（两队全景可见）");
                    }
                    var cam = camT.GetComponent<Camera>();
                    if (cam != null) { cam.allowHDR = true; cam.allowMSAA = true; }
                }

                // ---------- 5: URP 渲染质量 ----------
                var rpAsset = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
                if (rpAsset == null) rpAsset = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
                if (rpAsset != null)
                {
                    rpAsset.msaaSampleCount = 4;
                    rpAsset.renderScale = 1.0f;
                    EditorUtility.SetDirty(rpAsset);
                    report.AppendLine("URP：MSAA 4x，renderScale 1.0");
                }
                else report.AppendLine("警告：未找到 URP Asset");

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
                Debug.LogError("[LycheeFixO] " + ex);
            }
            finally
            {
                File.WriteAllText(ReportPath, report.ToString());
            }
        }

        static Mesh BuildDisc(float r, int seg)
        {
            var verts = new Vector3[seg + 1];
            verts[0] = Vector3.zero;
            for (int i = 0; i < seg; i++)
            {
                float ang = i / (float)seg * Mathf.PI * 2f;
                verts[i + 1] = new Vector3(Mathf.Cos(ang) * r, 0, Mathf.Sin(ang) * r);
            }
            var tris = new List<int>(seg * 3);
            for (int i = 0; i < seg; i++)
            {
                tris.Add(0);
                tris.Add(1 + (i + 1) % seg);
                tris.Add(1 + i);
            }
            var mesh = new Mesh();
            mesh.vertices = verts;
            mesh.SetTriangles(tris, 0);
            var uvs = new Vector2[verts.Length];
            for (int i = 0; i < verts.Length; i++)
                uvs[i] = new Vector2(verts[i].x / (2 * r) + 0.5f, verts[i].z / (2 * r) + 0.5f);
            mesh.uv = uvs;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    [InitializeOnLoad]
    internal static class LycheeMapFix20260611OAutoRun
    {
        static LycheeMapFix20260611OAutoRun()
        {
            EditorApplication.delayCall += () => EditorApplication.delayCall += TryRun;
        }

        static void TryRun()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (File.Exists(Path.Combine(root, "LycheeMapFix_20260611O.done"))) return;
            if (!File.Exists(Path.Combine(root, "LycheeMapFix_20260611N.done"))) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            if (SceneManager.GetActiveScene().name != "LycheeReplayDemo") return;
            LycheeMapFix20260611O.Run(false);
        }
    }
}
