// 第十五轮修复（2026-06-611 批次 P）：
// 1) 重新拆分 S04/S05 水驿，丢弃 GLB 自带的"内部水面板"（超薄+水平+大面积连通块）——它就是悬空大蓝板
// 2) 栈桥甲板高度改用"剔除水板后的众数平面"，定位更准
// 3) 主相机与截图相机开启 SMAA 高质量抗锯齿（回放清晰度）
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
    internal static class LycheeMapFix20260611P
    {
        static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string MarkerPath => Path.Combine(Root, "LycheeMapFix_20260611P.done");
        static string ReportPath => Path.Combine(Root, "LycheeMapFix_20260611P_report.txt");
        const string SplitDir = "Assets/LycheeMap15/GeneratedMeshes/WaterStationSplit_20260611N";
        const string SplitRootName = "WaterStation_Split_20260611N";
        static StringBuilder report;

        static readonly Dictionary<string, Vector2> NodeXZ = new Dictionary<string, Vector2> {
            {"S04",new Vector2(-18,-22)},{"S05",new Vector2(-2,-18)},
        };

        [MenuItem("Tools/Lychee/修复地图 20260611P（剔除模型内部水板+SMAA）")]
        public static void RunMenu() { Run(true); }

        public static void Run(bool force)
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.name != "LycheeReplayDemo") return;
            if (!force && File.Exists(MarkerPath)) return;
            report = new StringBuilder();
            report.AppendLine("LycheeMapFix_20260611P 报告 " + DateTime.Now.ToString("HH:mm:ss"));
            try
            {
                var roots = scene.GetRootGameObjects();
                var all = new List<Transform>();
                foreach (var r in roots) { if (r != null) all.AddRange(r.GetComponentsInChildren<Transform>(true)); }
                Func<string, Transform> find = n => all.FirstOrDefault(t => t != null && t.name == n);

                // 河道中心线（水位与方向）
                var lineList = new List<Vector3[]>();
                foreach (var rn in new[] { "E12_Route", "E13_Route" })
                {
                    var t = find(rn); if (t == null) continue;
                    var v = t.GetComponent<MeshFilter>().sharedMesh.vertices;
                    var c = new Vector3[v.Length / 2];
                    for (int i = 0; i < c.Length; i++)
                        c[i] = (t.TransformPoint(v[i * 2]) + t.TransformPoint(v[i * 2 + 1])) * 0.5f;
                    lineList.Add(c);
                }
                var c12 = lineList[0]; var c13 = lineList[1];
                Func<float, float, float, float> maxWaterIn = (x, z, radius) =>
                {
                    float best = float.MinValue;
                    foreach (var line in lineList)
                        foreach (var c in line)
                            if (Vector2.Distance(new Vector2(x, z), new Vector2(c.x, c.z)) < radius)
                                best = Mathf.Max(best, c.y);
                    return best == float.MinValue ? -0.85f : best;
                };

                var terrainT = find("Continuous_Terrain");
                var tcol = terrainT.GetComponent<MeshCollider>();

                var anchors = all.Where(t => t != null && t.name.Contains("_ANCHOR_")).ToList();
                foreach (var key in new[] { "S04", "S05" })
                {
                    var a = anchors.FirstOrDefault(t => t.name.StartsWith(key));
                    if (a == null) continue;
                    Transform model = null;
                    foreach (Transform c in a) if (c.name == "Imported_Station_Model") { model = c; break; }
                    if (model == null) continue;

                    Vector3 dirWater;
                    float waterY;
                    Vector2 target = NodeXZ[key];
                    if (key == "S04")
                    {
                        Vector3 p0 = c12[0], p1 = c12[1];
                        if (Vector2.Distance(new Vector2(c12[c12.Length - 1].x, c12[c12.Length - 1].z), target) <
                            Vector2.Distance(new Vector2(p0.x, p0.z), target))
                        { p0 = c12[c12.Length - 1]; p1 = c12[c12.Length - 2]; }
                        dirWater = new Vector3(p1.x - p0.x, 0, p1.z - p0.z).normalized;
                        waterY = p0.y;
                    }
                    else
                    {
                        Vector3 e12end = c12[Vector2.Distance(new Vector2(c12[0].x, c12[0].z), target) < 1f ? 1 : c12.Length - 2];
                        Vector3 e13next = c13[Vector2.Distance(new Vector2(c13[0].x, c13[0].z), target) < 1f ? 1 : c13.Length - 2];
                        Vector3 dd1 = new Vector3(e12end.x - target.x, 0, e12end.z - target.y).normalized;
                        Vector3 dd2 = new Vector3(e13next.x - target.x, 0, e13next.z - target.y).normalized;
                        dirWater = (dd1 + dd2).normalized;
                        if (dirWater.sqrMagnitude < 0.1f) dirWater = dd2;
                        waterY = maxWaterIn(target.x, target.y, 3f);
                    }
                    Vector3 quayC = new Vector3(target.x - dirWater.x * 2.0f, waterY, target.y - dirWater.z * 2.0f);
                    SplitStation(key, a, model, dirWater, waterY, quayC);
                }

                // SMAA
                foreach (var camName in new[] { "MainCamera_2.5D" })
                {
                    var camT = find(camName);
                    if (camT == null) continue;
                    var cam = camT.GetComponent<Camera>();
                    if (cam == null) continue;
                    var data = cam.GetUniversalAdditionalCameraData();
                    data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                    data.antialiasingQuality = AntialiasingQuality.High;
                    data.renderPostProcessing = true;
                    cam.allowHDR = true;
                    EditorUtility.SetDirty(cam);
                    report.AppendLine(camName + ": SMAA High + HDR");
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
                Debug.LogError("[LycheeFixP] " + ex);
            }
            finally
            {
                File.WriteAllText(ReportPath, report.ToString());
            }
        }

        static void SplitStation(string key, Transform anchor, Transform model, Vector3 dirWater, float waterY, Vector3 quayC)
        {
            Transform oldSplit = null;
            foreach (Transform c in anchor) if (c.name == SplitRootName) { oldSplit = c; break; }
            if (oldSplit != null) UnityEngine.Object.DestroyImmediate(oldSplit.gameObject);

            var mfs = model.GetComponentsInChildren<MeshFilter>(true);
            MeshFilter srcMf = null;
            foreach (var mf in mfs)
                if (mf.sharedMesh != null && (srcMf == null || mf.sharedMesh.vertexCount > srcMf.sharedMesh.vertexCount)) srcMf = mf;
            if (srcMf == null) { report.AppendLine(key + ": 无网格"); return; }
            var srcMesh = srcMf.sharedMesh;
            var srcRend = srcMf.GetComponent<Renderer>();
            var mats = srcRend != null ? srcRend.sharedMaterials : new Material[0];
            var verts = srcMesh.vertices;
            var tris = srcMesh.triangles;
            var mtx = srcMf.transform.localToWorldMatrix;

            int vn = verts.Length;
            var parent = new int[vn];
            for (int i = 0; i < vn; i++) parent[i] = i;
            Func<int, int> findRoot = null;
            findRoot = i => { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; };
            Action<int, int> union = (x, y) => { int rx = findRoot(x), ry = findRoot(y); if (rx != ry) parent[rx] = ry; };
            for (int t = 0; t < tris.Length; t += 3)
            { union(tris[t], tris[t + 1]); union(tris[t + 2], tris[t]); }

            // 连通块统计（全量，含 bbox）
            var cSum = new Dictionary<int, Vector3>();
            var cCnt = new Dictionary<int, int>();
            var cMin = new Dictionary<int, Vector3>();
            var cMax = new Dictionary<int, Vector3>();
            Vector3 allMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 allMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < vn; i++)
            {
                int r = findRoot(i);
                Vector3 w = mtx.MultiplyPoint3x4(verts[i]);
                Vector3 s; cSum.TryGetValue(r, out s); cSum[r] = s + w;
                int cct; cCnt.TryGetValue(r, out cct); cCnt[r] = cct + 1;
                Vector3 mn, mx;
                if (cMin.TryGetValue(r, out mn)) cMin[r] = Vector3.Min(mn, w); else cMin[r] = w;
                if (cMax.TryGetValue(r, out mx)) cMax[r] = Vector3.Max(mx, w); else cMax[r] = w;
                allMin = Vector3.Min(allMin, w); allMax = Vector3.Max(allMax, w);
            }

            // 模型包围盒由顶点直接计算（原模型已 inactive，Renderer.bounds 不可靠）
            Bounds wb = new Bounds((allMin + allMax) * 0.5f, allMax - allMin);
            Vector3 axis = dirWater.normalized;
            float minProj = float.MaxValue, maxProj = float.MinValue;
            var cProj = new Dictionary<int, float>();
            var cDiscard = new HashSet<int>();
            int discarded = 0;
            foreach (var kv in cSum)
            {
                int r = kv.Key;
                Vector3 size = cMax[r] - cMin[r];
                float area = size.x * size.z;
                // 内部水面板/底板：超薄、水平、面积巨大 → 丢弃
                if (size.y < 0.10f && area > 5.0f) { cDiscard.Add(r); discarded++; continue; }
                Vector3 cen = kv.Value / cCnt[r];
                float p = Vector3.Dot(new Vector3(cen.x - wb.center.x, 0, cen.z - wb.center.z), axis);
                cProj[r] = p;
                minProj = Mathf.Min(minProj, p); maxProj = Mathf.Max(maxProj, p);
            }
            report.AppendLine(key + ": 丢弃水板/底板连通块 " + discarded + " 个");
            float span = Mathf.Max(maxProj - minProj, 1e-3f);
            float hRange = wb.size.y;
            var cPiece = new Dictionary<int, int>();
            foreach (var kv in cProj)
            {
                float tNorm = (kv.Value - minProj) / span;
                int piece = tNorm < 0.34f ? 0 : (tNorm < 0.62f ? 1 : 2);
                float top = cMax[kv.Key].y;
                if (piece != 0 && (top - wb.min.y) > hRange * 0.62f && tNorm < 0.55f) piece = 0;
                cPiece[kv.Key] = piece;
            }

            string[] pieceNames = { "House_OnLand", "Dock_AboveWater", "Boat_OnWater" };
            var pieceGOs = new GameObject[3];
            var splitRoot = new GameObject(SplitRootName);
            splitRoot.transform.SetParent(anchor, true);
            var uv = srcMesh.uv;

            for (int piece = 0; piece < 3; piece++)
            {
                var keepTris = new List<int>();
                for (int t = 0; t < tris.Length; t += 3)
                {
                    int r = findRoot(tris[t]);
                    if (cDiscard.Contains(r)) continue;
                    int pc; if (!cPiece.TryGetValue(r, out pc)) pc = 1;
                    if (pc == piece) { keepTris.Add(tris[t]); keepTris.Add(tris[t + 1]); keepTris.Add(tris[t + 2]); }
                }
                if (keepTris.Count == 0) continue;
                var remap = new Dictionary<int, int>();
                var nv = new List<Vector3>();
                var nuv = new List<Vector2>();
                var nt = new List<int>(keepTris.Count);
                foreach (var idx in keepTris)
                {
                    int ni;
                    if (!remap.TryGetValue(idx, out ni))
                    {
                        ni = nv.Count;
                        remap[idx] = ni;
                        nv.Add(mtx.MultiplyPoint3x4(verts[idx]));
                        if (uv != null && uv.Length == vn) nuv.Add(uv[idx]);
                    }
                    nt.Add(ni);
                }
                var nm = new Mesh();
                nm.name = key + "_" + pieceNames[piece];
                nm.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                nm.SetVertices(nv);
                if (nuv.Count == nv.Count) nm.SetUVs(0, nuv);
                nm.SetTriangles(nt, 0);
                nm.RecalculateNormals();
                nm.RecalculateBounds();
                string ap = SplitDir + "/" + nm.name + ".asset";
                var oldA = AssetDatabase.LoadAssetAtPath<Mesh>(ap);
                if (oldA != null) AssetDatabase.DeleteAsset(ap);
                AssetDatabase.CreateAsset(nm, ap);
                var go = new GameObject(pieceNames[piece]);
                go.transform.SetParent(splitRoot.transform, true);
                go.transform.position = Vector3.zero;
                go.AddComponent<MeshFilter>().sharedMesh = nm;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = mats;
                pieceGOs[piece] = go;
                report.AppendLine(string.Format("{0}: {1} {2} 三角形", key, pieceNames[piece], nt.Count / 3));
            }

            if (pieceGOs[0] != null)
            {
                Bounds hb = WB(pieceGOs[0].transform);
                pieceGOs[0].transform.position += new Vector3(0, ((quayC.y + 0.03f) - 0.05f) - hb.min.y, 0);
            }
            if (pieceGOs[1] != null)
            {
                var dm = pieceGOs[1].GetComponent<MeshFilter>().sharedMesh;
                float deckY = HistogramModeY(dm);
                pieceGOs[1].transform.position += new Vector3(0, (waterY + 0.12f) - deckY, 0);
            }
            if (pieceGOs[2] != null)
            {
                Bounds bb = WB(pieceGOs[2].transform);
                pieceGOs[2].transform.position += new Vector3(0, (waterY - 0.12f) - bb.min.y, 0);
            }

            model.gameObject.SetActive(false);
            var rsAll = splitRoot.GetComponentsInChildren<Renderer>(true);
            if (rsAll.Length > 0)
            {
                Bounds nb = rsAll[0].bounds;
                for (int i = 1; i < rsAll.Length; i++) nb.Encapsulate(rsAll[i].bounds);
                foreach (Transform c in anchor)
                    if (c.name == "Node_Name_CN")
                        c.localPosition = new Vector3(c.localPosition.x, (nb.max.y - anchor.position.y) + 0.75f, c.localPosition.z);
            }
            report.AppendLine(key + ": 重拆分完成（无内部水板）");
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

        static Bounds WB(Transform t)
        {
            var rs = t.GetComponentsInChildren<Renderer>(true);
            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return b;
        }
    }

    [InitializeOnLoad]
    internal static class LycheeMapFix20260611PAutoRun
    {
        static LycheeMapFix20260611PAutoRun()
        {
            EditorApplication.delayCall += () => EditorApplication.delayCall += TryRun;
        }

        static void TryRun()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (File.Exists(Path.Combine(root, "LycheeMapFix_20260611P.done"))) return;
            if (!File.Exists(Path.Combine(root, "LycheeMapFix_20260611O.done"))) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            if (SceneManager.GetActiveScene().name != "LycheeReplayDemo") return;
            LycheeMapFix20260611P.Run(false);
        }
    }
}
