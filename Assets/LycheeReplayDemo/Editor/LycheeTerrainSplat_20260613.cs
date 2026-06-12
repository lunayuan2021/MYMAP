using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 一次性地形多层 splat（20260613）：用户反馈草地单贴图平铺重复感强，要多层混合+路缘泥土过渡带。
/// 做法：
/// 1. 从 Realistic Terrain Textures Lite 的 Ground001~005（备选 GroundPatchCracked01）按平均色自动分层：
///    最绿=主草(层0)、棕调=土(层1)、灰/黄低饱和=岩砂(层2)、次绿或龟裂图=变化层(层3)；
/// 2. 给 01_Terrain_Base 与 Continuous_Terrain 逐顶点（世界坐标）烘权重进顶点色：
///    R=路缘土（距 RoadGroundFit 路面 XZ<2.6m，0~1.2m 全土后渐出）、
///    G=岩（坡度>28°渐入）/砂（距 WaterRoute_And_River 河面 XZ<2m 权重 0.6）、
///    B=低频 Perlin(/17)>0.62 的变化草、A=低频 Perlin(/23) 供 shader 端 texture bombing；
///    新网格另存 GeneratedMeshes/TerrainSplat_20260613/ 不覆盖原资产（可回滚）；
/// 3. 建 Materials/TerrainSplat_20260613.mat（Lychee/Terrain Splat）填四层贴图与 tiling 并赋给两个地形对象。
/// 风险防御：赋材质前先查 shader 编译错误（坏 shader 会让地形变洋红），发现即中止、不动场景。
/// 模式与 LycheeStationLift_20260613 一致：update 轮询 + done 标记 + 报告 + 磁盘重载 + 菜单 rerun。
/// </summary>
[InitializeOnLoad]
static class LycheeTerrainSplat_20260613
{
    const string ScenePath = "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity";
    const string DoneFileName = ".lychee_terrain_splat_20260613.done";
    const string ReportFileName = "LycheeTerrainSplat_20260613_report.txt";
    const string ShaderPath = "Assets/LycheeMap15/Shaders/LycheeTerrainSplat.shader";
    const string MaterialPath = "Assets/LycheeMap15/Materials/TerrainSplat_20260613.mat";
    const string MeshFolderParent = "Assets/LycheeMap15/GeneratedMeshes";
    const string MeshFolderName = "TerrainSplat_20260613";
    const string TextureDir = "Assets/ALP_Assets/Realistic Terrain Textures Lite/Textures/";
    // 旧地形混合材质（取 _TextureScale 用）与河床材质（修复首跑误覆盖时恢复用）
    const string OldGrassMaterialPath = "Assets/LycheeMap15/Materials/M_Config_Terrain_Grassland.mat";
    const string RiverbedMaterialPath = "Assets/LycheeMap15/Materials/M_RiverBed_SandGravel.mat";
    const string OldTerrainShaderName = "Lychee/Terrain Blend";
    // 选层亮度下限：该素材包部分 .tif 是预乘 alpha 的"贴片叠加"图（如 Ground001 平均 alpha 仅 0.07，
    // 预乘后 RGB 引擎内采样近乎全黑 0.024），当不透明 albedo 用会把坡面/河岸染成炭黑，必须剔除
    const float MinLayerLuminance = 0.12f;

    // 路缘泥土过渡带：0~1.2m 全土，1.2~2.6m 平滑渐出
    const float RoadFullDirt = 1.2f;
    const float RoadDirtEnd = 2.6f;
    // 坡度岩层渐入区间（度）：v2 提高阈值——这块程序化地形遍布小起伏，
    // 28° 起步会把大半个中景判成岩面整片发黑（用户实测反馈"太黑了"），
    // 只有真正的陡坡/沟壁才该露岩
    const float SlopeRockStart = 42f;
    const float SlopeRockEnd = 58f;
    // 河岸砂带：XZ <2m 内砂权重 0.6（1.2m 起渐出到 2m，避免硬边）
    const float RiverSandEnd = 2.0f;
    const float RiverSandFadeStart = 1.2f;
    const float RiverSandWeight = 0.6f;

    static double nextPollTime;

    static LycheeTerrainSplat_20260613()
    {
        EditorApplication.update += Poll;
    }

    static string ProjectRoot
    {
        get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..")); }
    }

    static string DonePath { get { return Path.Combine(ProjectRoot, DoneFileName); } }
    static string ReportPath { get { return Path.Combine(ProjectRoot, ReportFileName); } }

    static void Poll()
    {
        if (EditorApplication.timeSinceStartup < nextPollTime) return;
        nextPollTime = EditorApplication.timeSinceStartup + 2.0;
        try
        {
            if (File.Exists(DonePath)) return;
        }
        catch (Exception)
        {
            return;
        }
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying ||
            EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            return;
        }
        Run();
    }

    /// <summary>
    /// XZ 平面三角形距离场：仿 LycheeStationLift_20260613.GroundSurface 的包围盒粗筛模式，
    /// 但查询从"采样高度"换成"最近距离"（路面 1620 三角形 x 地形数万顶点，粗筛后暴力可行）。
    /// </summary>
    class XZTriangleField
    {
        readonly List<Vector2> a = new List<Vector2>();
        readonly List<Vector2> b = new List<Vector2>();
        readonly List<Vector2> c = new List<Vector2>();
        readonly List<Vector4> box = new List<Vector4>(); // xy=minXZ zw=maxXZ

        public int TriangleCount { get { return a.Count; } }

        public void AddMesh(MeshFilter filter)
        {
            Mesh mesh = filter.sharedMesh;
            Vector3[] verts = mesh.vertices;
            int[] tris = mesh.triangles;
            Transform t = filter.transform;
            for (int i = 0; i < tris.Length; i += 3)
            {
                Vector3 wa = t.TransformPoint(verts[tris[i]]);
                Vector3 wb = t.TransformPoint(verts[tris[i + 1]]);
                Vector3 wc = t.TransformPoint(verts[tris[i + 2]]);
                a.Add(new Vector2(wa.x, wa.z));
                b.Add(new Vector2(wb.x, wb.z));
                c.Add(new Vector2(wc.x, wc.z));
                box.Add(new Vector4(
                    Mathf.Min(wa.x, Mathf.Min(wb.x, wc.x)), Mathf.Min(wa.z, Mathf.Min(wb.z, wc.z)),
                    Mathf.Max(wa.x, Mathf.Max(wb.x, wc.x)), Mathf.Max(wa.z, Mathf.Max(wb.z, wc.z))));
            }
        }

        /// <summary>返回 (x,z) 到所有三角形的最近 XZ 距离，超过 maxDist 直接返回 maxDist（够远就不关心了）。</summary>
        public float DistanceXZ(float x, float z, float maxDist)
        {
            float best = maxDist;
            float bestSq = best * best;
            for (int i = 0; i < a.Count; i++)
            {
                Vector4 bb = box[i];
                // 包围盒距离粗筛：比当前最优还远的三角形直接跳过
                float dx = Mathf.Max(Mathf.Max(bb.x - x, x - bb.z), 0f);
                float dz = Mathf.Max(Mathf.Max(bb.y - z, z - bb.w), 0f);
                if (dx * dx + dz * dz >= bestSq) continue;
                float d = DistPointTriangle(x, z, a[i], b[i], c[i]);
                if (d < best)
                {
                    best = d;
                    bestSq = best * best;
                    if (best <= 0f) return 0f; // 已在某三角形内部，不可能更近
                }
            }
            return best;
        }

        static float DistPointTriangle(float x, float z, Vector2 p0, Vector2 p1, Vector2 p2)
        {
            // 三边叉积同号（含零）=> 点在三角形内，距离为 0；否则取到三边的最短距离
            float c0 = EdgeCross(p0, p1, x, z);
            float c1 = EdgeCross(p1, p2, x, z);
            float c2 = EdgeCross(p2, p0, x, z);
            bool hasNeg = c0 < 0f || c1 < 0f || c2 < 0f;
            bool hasPos = c0 > 0f || c1 > 0f || c2 > 0f;
            if (!(hasNeg && hasPos)) return 0f;
            Vector2 p = new Vector2(x, z);
            return Mathf.Min(DistPointSegment(p, p0, p1),
                Mathf.Min(DistPointSegment(p, p1, p2), DistPointSegment(p, p2, p0)));
        }

        static float EdgeCross(Vector2 s0, Vector2 s1, float x, float z)
        {
            return (s1.x - s0.x) * (z - s0.y) - (s1.y - s0.y) * (x - s0.x);
        }

        static float DistPointSegment(Vector2 p, Vector2 s0, Vector2 s1)
        {
            Vector2 d = s1 - s0;
            float len2 = d.sqrMagnitude;
            float t = len2 < 1e-8f ? 0f : Mathf.Clamp01(Vector2.Dot(p - s0, d) / len2);
            return (s0 + d * t - p).magnitude;
        }
    }

    static void Run()
    {
        var report = new List<string>();
        report.Add("LycheeTerrainSplat_20260613 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        try
        {
            // ---------- 0) shader 防御检查：编译失败会让地形变洋红，发现就中止、完全不动场景 ----------
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null)
            {
                Abort(report, "找不到 shader 资产: " + ShaderPath);
                return;
            }
            if (ShaderUtil.ShaderHasError(shader))
            {
                Abort(report, "shader 有编译错误（ShaderUtil.ShaderHasError），中止以免地形变洋红: " + ShaderPath);
                return;
            }
            if (!shader.isSupported)
            {
                Abort(report, "shader 当前管线不支持（isSupported=false），中止: " + ShaderPath);
                return;
            }
            report.Add("shader 检查通过: " + shader.name);

            // ---------- 1) 纹理选层：按平均色自动分类 ----------
            // .tif 的 isReadable 可能为 false：用 RenderTexture Blit + ReadPixels 走 GPU 缩采样取平均色，
            // 不必临时改 TextureImporter（免触发整图重导、也不会把导入设置改坏）
            report.Add("平均色读取方式: RenderTexture Blit + ReadPixels（未改任何 TextureImporter.isReadable）");
            string[] groundNames = { "Ground001", "Ground002", "Ground003", "Ground004", "Ground005" };
            var textures = new List<Texture2D>();
            var averages = new List<Color>();
            for (int i = 0; i < groundNames.Length; i++)
            {
                string path = TextureDir + groundNames[i] + ".tif";
                Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (tex == null)
                {
                    Abort(report, "找不到贴图: " + path);
                    return;
                }
                textures.Add(tex);
                Color avg = AverageColor(tex);
                averages.Add(avg);
                report.Add(string.Format("  {0} 平均色 RGB({1:F3},{2:F3},{3:F3}) 绿度={4:F3} 棕度={5:F3} 灰度={6:F3}",
                    groundNames[i], avg.r, avg.g, avg.b, GreenScore(avg), BrownScore(avg), GrayScore(avg)));
            }
            Texture2D cracked = AssetDatabase.LoadAssetAtPath<Texture2D>(TextureDir + "GroundPatchCracked01.tif");
            Color crackedAvg = cracked != null ? AverageColor(cracked) : Color.black;
            if (cracked != null)
            {
                report.Add(string.Format("  GroundPatchCracked01 平均色 RGB({0:F3},{1:F3},{2:F3})",
                    crackedAvg.r, crackedAvg.g, crackedAvg.b));
            }

            // 亮度下限防御：预乘 alpha 的贴片图引擎内 RGB 近黑（首跑曾把近黑的 Ground001 选成岩砂层，
            // 导致坡面/河岸渲染成炭黑），先按平均亮度剔除这类不可用候选
            var pool = new List<int>();
            var tooDark = new List<int>();
            for (int i = 0; i < averages.Count; i++)
            {
                if (Luminance(averages[i]) < MinLayerLuminance)
                {
                    tooDark.Add(i);
                    report.Add(string.Format("  {0} 平均亮度 {1:F3} < {2:F2}，判定为预乘 alpha 贴片类（引擎内近黑），剔除出选层候选",
                        groundNames[i], Luminance(averages[i]), MinLayerLuminance));
                }
                else
                {
                    pool.Add(i);
                }
            }
            // 剔得只剩不足四张时按亮度从高到低补回，保证四层都能选出（宁可暗也不能缺层）
            while (pool.Count < 4 && tooDark.Count > 0)
            {
                int bestPos = 0;
                for (int i = 1; i < tooDark.Count; i++)
                {
                    if (Luminance(averages[tooDark[i]]) > Luminance(averages[tooDark[bestPos]])) bestPos = i;
                }
                pool.Add(tooDark[bestPos]);
                report.Add("  WARN: 候选不足四张，补回偏暗的 " + groundNames[tooDark[bestPos]]);
                tooDark.RemoveAt(bestPos);
            }

            // 依次挑选：绿度最高=主草 → 余下棕度最高=土 → 余下灰度最高=岩砂 → 余下绿度最高=变化层候选
            int idx0 = TakeBest(pool, averages, GreenScore);
            int idx1 = TakeBest(pool, averages, BrownScore);
            int idx2 = TakeBest(pool, averages, GrayScore);
            int idx3 = TakeBest(pool, averages, GreenScore);
            Texture2D layer3Tex = textures[idx3];
            string layer3Name = groundNames[idx3];
            // 次绿候选若几乎不绿（绿度<0.01 视为灰土类），退而用龟裂图做变化层（龟裂图同样要过亮度下限）
            if (GreenScore(averages[idx3]) < 0.01f && cracked != null && Luminance(crackedAvg) >= MinLayerLuminance)
            {
                layer3Tex = cracked;
                layer3Name = "GroundPatchCracked01（次绿候选 " + groundNames[idx3] + " 绿度不足 0.01）";
            }
            report.Add("选层结果（依据上表平均色打分）:");
            report.Add("  层0 主草   = " + groundNames[idx0] + "（5 张里绿度最高）");
            report.Add("  层1 路缘土 = " + groundNames[idx1] + "（余下棕度最高：红强于蓝且压绿）");
            report.Add("  层2 岩/砂  = " + groundNames[idx2] + "（余下低饱和偏灰/黄打分最高）");
            report.Add("  层3 变化草 = " + layer3Name);

            // ---------- 2) 打开场景（磁盘强制重载），采路面/河面三角形 ----------
            Scene active = SceneManager.GetActiveScene();
            if (!string.Equals(active.path, ScenePath, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(active.path) && active.isDirty)
            {
                EditorSceneManager.SaveOpenScenes();
            }
            active = EditorSceneManager.OpenScene(ScenePath);
            report.Add("已从磁盘重载场景: " + ScenePath);

            var roadField = new XZTriangleField();
            int roadMeshCount = 0;
            var terrainFilters = new List<MeshFilter>();
            foreach (MeshFilter filter in UnityEngine.Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
            {
                if (filter.sharedMesh == null || !filter.gameObject.activeInHierarchy) continue;
                string assetPath = AssetDatabase.GetAssetPath(filter.sharedMesh);
                if (!string.IsNullOrEmpty(assetPath) && assetPath.Contains("RoadGroundFit"))
                {
                    roadField.AddMesh(filter);
                    roadMeshCount++;
                    continue;
                }
                string goName = filter.gameObject.name;
                if (goName == "01_Terrain_Base" || goName == "Continuous_Terrain")
                {
                    terrainFilters.Add(filter);
                }
            }
            report.Add(string.Format("路面采样源: {0} 个 RoadGroundFit 网格，{1} 个三角形", roadMeshCount, roadField.TriangleCount));
            if (terrainFilters.Count == 0)
            {
                Abort(report, "没找到 01_Terrain_Base / Continuous_Terrain 的 MeshFilter，中止。");
                return;
            }
            if (roadField.TriangleCount == 0)
            {
                report.Add("WARN: 路面三角形为 0，路缘土过渡带将整体缺席（继续执行其余层）");
            }

            var riverField = new XZTriangleField();
            int riverMeshCount = 0;
            foreach (Transform t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
            {
                if (t.name != "WaterRoute_And_River") continue;
                foreach (MeshFilter mf in t.GetComponentsInChildren<MeshFilter>())
                {
                    if (mf.sharedMesh == null) continue;
                    riverField.AddMesh(mf);
                    riverMeshCount++;
                }
                break;
            }
            report.Add(string.Format("河面采样源: WaterRoute_And_River 下 {0} 个网格，{1} 个三角形",
                riverMeshCount, riverField.TriangleCount));

            // ---------- 3) 逐顶点烘权重进顶点色，新网格另存（不覆盖原资产，可回滚） ----------
            if (!AssetDatabase.IsValidFolder(MeshFolderParent + "/" + MeshFolderName))
            {
                AssetDatabase.CreateFolder(MeshFolderParent, MeshFolderName);
            }
            foreach (MeshFilter filter in terrainFilters)
            {
                Mesh src = filter.sharedMesh;
                string srcPath = AssetDatabase.GetAssetPath(src);
                Mesh baked = UnityEngine.Object.Instantiate(src);
                baked.name = filter.gameObject.name + "_SplatBake";
                Vector3[] verts = baked.vertices;
                Vector3[] norms = baked.normals;
                if (norms == null || norms.Length != verts.Length)
                {
                    baked.RecalculateNormals();
                    norms = baked.normals;
                }
                Matrix4x4 l2w = filter.transform.localToWorldMatrix;
                // 法线要用逆转置矩阵变换才对非均匀缩放安全（地形通常是 1 缩放，防御写法）
                Matrix4x4 nMat = l2w.inverse.transpose;
                var cols = new Color[verts.Length];
                int dirtCount = 0, rockCount = 0, varCount = 0;
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 w = l2w.MultiplyPoint3x4(verts[i]);
                    Vector3 n = nMat.MultiplyVector(norms[i]).normalized;

                    // R 路缘土：0~1.2m 全土，1.2~2.6m 平滑渐出
                    float dRoad = roadField.TriangleCount > 0
                        ? roadField.DistanceXZ(w.x, w.z, RoadDirtEnd) : RoadDirtEnd;
                    float wDirt = 1f - SmoothStep01((dRoad - RoadFullDirt) / (RoadDirtEnd - RoadFullDirt));

                    // G 岩：坡度 28°→42° 渐入
                    float slopeDeg = Vector3.Angle(n, Vector3.up);
                    float wRock = SmoothStep01((slopeDeg - SlopeRockStart) / (SlopeRockEnd - SlopeRockStart));

                    // G 砂：距河面 XZ<2m 权重 0.6（1.2m 起渐出避免硬边），与坡度岩取较大者
                    if (riverField.TriangleCount > 0)
                    {
                        float dRiver = riverField.DistanceXZ(w.x, w.z, RiverSandEnd);
                        float sand = RiverSandWeight *
                            (1f - SmoothStep01((dRiver - RiverSandFadeStart) / (RiverSandEnd - RiverSandFadeStart)));
                        if (sand > wRock) wRock = sand;
                    }

                    // B 变化草：低频 Perlin(/17) 越过 0.62 渐入到 0.5（偏移避开整数格点退化）
                    float noise = Mathf.Clamp01(Mathf.PerlinNoise(w.x / 17f + 31.7f, w.z / 17f + 11.3f));
                    float wVar = 0.5f * SmoothStep01((noise - 0.62f) / 0.08f);

                    // 优先级 土 > 岩 > 变化草：逐层用剩余配额压缩，保证 R+G+B<=1，主草权重(1-R-G-B)非负
                    wRock *= 1f - wDirt;
                    wVar *= Mathf.Max(0f, 1f - wDirt - wRock);

                    // A：另一组低频 Perlin(/23)，供 shader 端主草 texture bombing 用
                    float bomb = Mathf.Clamp01(Mathf.PerlinNoise(w.x / 23f + 101.3f, w.z / 23f + 57.9f));

                    cols[i] = new Color(wDirt, wRock, wVar, bomb);
                    if (wDirt > 0.33f) dirtCount++;
                    else if (wRock > 0.33f) rockCount++;
                    else if (wVar > 0.33f) varCount++;
                }
                baked.colors = cols;

                string meshAssetPath = MeshFolderParent + "/" + MeshFolderName + "/" + baked.name + ".asset";
                if (AssetDatabase.LoadAssetAtPath<Mesh>(meshAssetPath) != null)
                {
                    AssetDatabase.DeleteAsset(meshAssetPath); // 重跑时换新，场景引用随后重赋
                }
                AssetDatabase.CreateAsset(baked, meshAssetPath);
                filter.sharedMesh = baked;
                EditorUtility.SetDirty(filter);
                // 重跑时 srcPath 是上次的烘焙资产而非真正原网格——标注清楚，真正原网格记录在首跑报告段落里
                string srcNote = string.IsNullOrEmpty(srcPath) ? "(内嵌)" : srcPath;
                if (!string.IsNullOrEmpty(srcPath) && srcPath.Contains(MeshFolderName))
                {
                    srcNote = srcPath + "（重跑：此为上次烘焙结果，真正原网格见首跑报告段）";
                }
                report.Add(string.Format(
                    "{0}: {1} 顶点烘焙完成 → {2}（原网格 {3} 未动可回滚）；主导层统计 土={4} 岩砂={5} 变化草={6} 其余主草",
                    filter.gameObject.name, verts.Length, meshAssetPath,
                    srcNote, dirtCount, rockCount, varCount));
            }

            // ---------- 4) 建材质并赋回两个地形对象 ----------
            // 主草 tiling 推断：旧 Lychee/Terrain Blend 用 world XZ * _TextureScale 投影 UV，
            // 新 shader 同口径（uv = XZ * tiling），直接沿用旧值即可保持现有观感
            float tiling0 = 0.125f;
            string tilingSource = "旧材质无 _TextureScale，回退默认 0.125（约 8 世界米一重复）";
            bool tilingFound = false;
            foreach (MeshFilter filter in terrainFilters)
            {
                var renderer = filter.GetComponent<MeshRenderer>();
                if (renderer == null || renderer.sharedMaterial == null) continue;
                Material old = renderer.sharedMaterial;
                if (old.HasProperty("_TextureScale"))
                {
                    float raw = old.GetFloat("_TextureScale");
                    tiling0 = Mathf.Clamp(raw, 0.05f, 0.35f);
                    tilingSource = string.Format(
                        "取自旧材质 {0} 的 _TextureScale={1:F3}（即约 {2:F1} 世界米一重复，UV 口径与新 shader 相同）",
                        old.name, raw, 1f / tiling0);
                    tilingFound = true;
                    break;
                }
            }
            if (!tilingFound)
            {
                // 重跑时场上已是 splat 材质（无 _TextureScale），改从旧草地材质资产直接取，避免静默退回 0.125 改变草地密度
                Material oldGrass = AssetDatabase.LoadAssetAtPath<Material>(OldGrassMaterialPath);
                if (oldGrass != null && oldGrass.HasProperty("_TextureScale"))
                {
                    float raw = oldGrass.GetFloat("_TextureScale");
                    tiling0 = Mathf.Clamp(raw, 0.05f, 0.35f);
                    tilingSource = string.Format(
                        "场上材质已替换，改取旧材质资产 {0} 的 _TextureScale={1:F3}（约 {2:F1} 世界米一重复）",
                        OldGrassMaterialPath, raw, 1f / tiling0);
                }
            }
            report.Add("主草 tiling 推断: " + tilingSource);

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, MaterialPath);
                report.Add("新建材质: " + MaterialPath);
            }
            else
            {
                mat.shader = shader; // 重跑时复用资产（保住场景引用的 guid），重填属性
                report.Add("复用已有材质重填属性: " + MaterialPath);
            }
            mat.SetTexture("_Layer0", textures[idx0]);
            mat.SetTexture("_Layer1", textures[idx1]);
            mat.SetTexture("_Layer2", textures[idx2]);
            mat.SetTexture("_Layer3", layer3Tex);
            // 各层 tiling 互为非整数比：土/岩比草低频（地表特征更大块），变化草略高频，破对齐
            mat.SetFloat("_Tiling0", tiling0);
            mat.SetFloat("_Tiling0B", tiling0 * 0.31f);
            mat.SetFloat("_Tiling1", tiling0 * 0.62f);
            mat.SetFloat("_Tiling2", tiling0 * 0.5f);
            mat.SetFloat("_Tiling3", tiling0 * 1.27f);
            // v2 提亮：土/岩源图整体偏暗（Ground002/004 平均亮度仅 ~0.35），白天光下糊成深色一片，
            // 用 >1 的 tint 在 shader 里直接抬亮度（白天阳光下观感对齐草地），微暖防止发灰
            mat.SetColor("_Tint0", Color.white);
            mat.SetColor("_Tint1", new Color(1.35f, 1.28f, 1.15f, 1f));
            mat.SetColor("_Tint2", new Color(1.45f, 1.40f, 1.30f, 1f));
            mat.SetColor("_Tint3", new Color(1.08f, 1.08f, 1.02f, 1f));
            EditorUtility.SetDirty(mat);
            report.Add(string.Format(
                "tiling: 层0={0:F4} 层0副采样={1:F4} 层1={2:F4} 层2={3:F4} 层3={4:F4}",
                tiling0, tiling0 * 0.31f, tiling0 * 0.62f, tiling0 * 0.5f, tiling0 * 1.27f));

            // 只替换走旧 Lychee/Terrain Blend 的槽位：Continuous_Terrain 槽1 是河床子网格
            // （M_RiverBed_SandGravel，URP Lit），整槽替换会让河床失去砂砾质感。
            // 首跑(20260613 02:12)已把河床槽误覆盖，这里对"槽>0 且已是 splat 材质"的槽恢复河床材质
            Material riverbedMat = AssetDatabase.LoadAssetAtPath<Material>(RiverbedMaterialPath);
            int assigned = 0;
            foreach (MeshFilter filter in terrainFilters)
            {
                var renderer = filter.GetComponent<MeshRenderer>();
                if (renderer == null)
                {
                    report.Add(filter.gameObject.name + ": 无 MeshRenderer，跳过赋材质");
                    continue;
                }
                Material[] mats = renderer.sharedMaterials;
                var slotNotes = new List<string>();
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    Material slot = mats[i];
                    string slotName = slot != null ? slot.name : "(null)";
                    bool isOldTerrainBlend = slot != null && slot.shader != null &&
                        slot.shader.name == OldTerrainShaderName;
                    if (isOldTerrainBlend)
                    {
                        mats[i] = mat;
                        changed = true;
                        slotNotes.Add(string.Format("槽{0}: {1} → TerrainSplat_20260613（回滚按此名找回）", i, slotName));
                    }
                    else if (slot == mat && i > 0 && riverbedMat != null)
                    {
                        mats[i] = riverbedMat;
                        changed = true;
                        slotNotes.Add(string.Format("槽{0}: 首跑误覆盖为 splat → 恢复 {1}", i, riverbedMat.name));
                    }
                    else if (slot == mat)
                    {
                        slotNotes.Add(string.Format("槽{0}: 已是 TerrainSplat_20260613，保持", i));
                    }
                    else
                    {
                        slotNotes.Add(string.Format("槽{0}: {1} 非地形混合材质，保留不动", i, slotName));
                    }
                }
                if (changed)
                {
                    renderer.sharedMaterials = mats;
                    EditorUtility.SetDirty(renderer);
                }
                assigned++;
                report.Add(filter.gameObject.name + ": " + string.Join("；", slotNotes.ToArray()));
            }

            AssetDatabase.SaveAssets();
            bool saved = EditorSceneManager.SaveScene(active);
            report.Add(string.Format("赋材质 {0} 个地形对象；场景保存: {1}", assigned, saved));
            WriteReport(report);
            File.WriteAllText(DonePath, saved ? "Succeeded" : "SaveFailed");
            Debug.Log("[LycheeTerrainSplat_20260613] 完成，报告: " + ReportPath);
        }
        catch (Exception exception)
        {
            report.Add("EXCEPTION: " + exception);
            WriteReport(report);
            TryWriteDone("Exception");
            Debug.LogError("[LycheeTerrainSplat_20260613] 执行异常: " + exception.Message);
        }
    }

    // ---------- 平均色与打分 ----------

    /// <summary>
    /// GPU 缩采样取平均色：Blit 到 32x32 sRGB RT 再 ReadPixels，等效 mip 级平均，
    /// 不要求源贴图 isReadable，也就不必动 .tif 的导入设置。
    /// </summary>
    static Color AverageColor(Texture2D source)
    {
        const int size = 32;
        RenderTexture rt = RenderTexture.GetTemporary(size, size, 0,
            RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        RenderTexture prev = RenderTexture.active;
        Graphics.Blit(source, rt);
        RenderTexture.active = rt;
        var temp = new Texture2D(size, size, TextureFormat.RGBA32, false);
        temp.ReadPixels(new Rect(0, 0, size, size), 0, 0);
        temp.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        Color sum = new Color(0f, 0f, 0f, 0f);
        Color[] pixels = temp.GetPixels();
        for (int i = 0; i < pixels.Length; i++) sum += pixels[i];
        UnityEngine.Object.DestroyImmediate(temp);
        return sum / pixels.Length;
    }

    /// <summary>感知亮度（Rec.601 权重）：用于剔除预乘 alpha 导致引擎内近黑的贴片图。</summary>
    static float Luminance(Color c)
    {
        return c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;
    }

    /// <summary>绿度：绿通道压过红蓝多少（主草=最绿）。</summary>
    static float GreenScore(Color c)
    {
        return c.g * 2f - c.r - c.b;
    }

    /// <summary>棕度：红强于蓝为主、略压绿（土=棕调）。</summary>
    static float BrownScore(Color c)
    {
        return (c.r - c.b) * 1.5f + (c.r - c.g) * 0.5f;
    }

    /// <summary>灰度：低饱和加分，偏黄（红绿高蓝低）小幅加分（岩/砂=灰或黄调）。</summary>
    static float GrayScore(Color c)
    {
        float max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
        float min = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
        return -(max - min) + (Mathf.Min(c.r, c.g) - c.b) * 0.35f;
    }

    /// <summary>从候选池里取打分最高的一张并移出池子（保证四层互不重复）。</summary>
    static int TakeBest(List<int> pool, List<Color> averages, Func<Color, float> score)
    {
        int bestPos = 0;
        float best = float.MinValue;
        for (int i = 0; i < pool.Count; i++)
        {
            float s = score(averages[pool[i]]);
            if (s > best)
            {
                best = s;
                bestPos = i;
            }
        }
        int index = pool[bestPos];
        pool.RemoveAt(bestPos);
        return index;
    }

    /// <summary>0~1 平滑插值（同 GLSL smoothstep），入参越界自动夹紧。</summary>
    static float SmoothStep01(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    // ---------- 骨架杂项 ----------

    static void Abort(List<string> report, string error)
    {
        report.Add("ERROR: " + error);
        WriteReport(report);
        TryWriteDone("Failed");
        Debug.LogError("[LycheeTerrainSplat_20260613] " + error);
    }

    static void WriteReport(List<string> lines)
    {
        // 追加而不覆盖：首跑报告里记着原网格路径/原材质名（回滚依据），重跑不能抹掉
        try
        {
            string text = string.Join(Environment.NewLine, lines) + Environment.NewLine;
            if (File.Exists(ReportPath))
            {
                File.AppendAllText(ReportPath, Environment.NewLine + "======== 重跑 ========" + Environment.NewLine + text);
            }
            else
            {
                File.WriteAllText(ReportPath, text);
            }
        }
        catch (Exception e) { Debug.LogWarning("[LycheeTerrainSplat_20260613] 写报告失败: " + e.Message); }
    }

    static void TryWriteDone(string content)
    {
        try { File.WriteAllText(DonePath, content); }
        catch (Exception e) { Debug.LogWarning("[LycheeTerrainSplat_20260613] 写 done 失败: " + e.Message); }
    }

    [MenuItem("Tools/Lychee/Replay Upgrade 20260612/Terrain Splat 20260613 (rerun)")]
    static void RerunNow()
    {
        try { if (File.Exists(DonePath)) File.Delete(DonePath); }
        catch (Exception e) { Debug.LogWarning("[LycheeTerrainSplat_20260613] 删 done 失败: " + e.Message); }
        nextPollTime = 0;
    }
}
