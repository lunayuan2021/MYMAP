using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// 边界柔化（20260614，克制版）：用户只想消除地图边缘（尤其兴庆宫 S15 那侧）"硬截断/突兀"的问题，
/// 不要大范围森林、不要低面苔藓岩石、不要把地图变大变夸张、更不能盖住原地图。
///
/// 做法极简：把现役 Replay_Extended_Ground 外延地表做成一圈**柔和下坡裙边**——
/// 紧贴可玩区(±40×±30)边缘处与边缘同高（无悬崖/无硬切），再向外约 14m 平缓降到原背景平面高度(~-2.2)，
/// 远处保持平坦背景。可玩区内压到地形之下藏起（绝不盖住道路/驿站/河流）。绿草地材质、不撒任何植被/岩石。
///
/// 同时清掉上一版散布根 BoundaryEnhance_20260614（那批森林/岩石/远山）。作用于当前场景（在母版跑一次，各图克隆即继承）。
/// </summary>
public static class LycheeBoundaryEnhance_20260614
{
    const string OldDecoRootName = "BoundaryEnhance_20260614"; // 上一版散布根，清掉
    const string TreelineRootName = "BoundaryTreeline_20260615"; // 边缘稀疏林带根（独立、可整体删）
    const string PalaceBufferRootName = "BoundaryPalaceBuffer_20260615"; // 兴庆宫近景缓冲带（独立、可整体删）
    const string MeshDir = "Assets/LycheeMap15/GeneratedMeshes/Boundary_20260614";
    const float HalfX = 40f, HalfZ = 30f;     // 可玩区半宽/半高（运行时 80x60）
    const float OuterX = 110f, OuterZ = 90f;  // 外延范围（与原 Replay_Extended_Ground 一致，不放大）
    const float CellSize = 2f;                // 网格细分（边缘需要更细的分辨率）
    const float FlushShoulderWidth = 18f;     // 边界外先保持与可玩区边缘齐平，消除"地图被抬起"的台地感
    const float FalloffWidth = 26f;           // 齐平外延之后再很缓地降到远处背景
    const float OutReach = 6f;                // 林木/石头向边缘外延伸多远（落在齐平外延上，不进远处低平地）
    const float BackgroundY = -2.2f;          // 远处背景平面高度（≈原值）

    // 边缘林带：沿可玩区周界、紧贴边缘内侧撒一排稀疏的树，树冠探出去打碎那条硬接缝。
    const float TreelineStep = 2.0f;          // 沿周界采样步长（越小越密）
    const float TreelineProb = 0.85f;         // 每个采样点放树概率（<1 形成自然疏密）
    const float TreelineInMin = 1.0f, TreelineInMax = 4.5f; // 内缩范围（树干落在边缘内侧多少米）
    const float RoadClear = 2.6f;             // 离道路中心线多近就跳过
    const float StationClear = 4.5f;          // 离驿站多近就跳过（绝不压兴庆宫等建筑）
    static readonly string[] TreeFallbackPrefabs = {
        "Assets/Tree_Packs/URP_Tree_Pack/Prefabs/URP_Tree_1.prefab",
        "Assets/Tree_Packs/URP_Tree_Pack/Prefabs/URP_Tree_2.prefab",
        "Assets/Tree_Packs/URP_Tree_Pack/Prefabs/URP_Tree_3.prefab",
    };

    [MenuItem("Tools/Lychee/MultiMap/Enhance Boundary (current scene)")]
    static void Run() => ApplyToActiveScene();

    /// <summary>对当前打开的场景做边界柔化并保存（可被文件驱动 runner 调用，绕过 MCP）。</summary>
    public static void ApplyToActiveScene()
    {
        var scene = SceneManager.GetActiveScene();
        try
        {
            EnsureMeshDir();
            MapElevationData elev = UnityEngine.Object.FindFirstObjectByType<MapElevationData>();
            if (elev == null) { Debug.LogError("[BoundaryEnhance] 没找到 MapElevationData。"); return; }

            // 清掉上一版散布（森林/岩石/远山）。
            GameObject oldDeco = GameObject.Find(OldDecoRootName);
            if (oldDeco != null) UnityEngine.Object.DestroyImmediate(oldDeco);

            // 外延地表材质：**用可玩地形 Continuous_Terrain 的同款材质**——接缝两侧同色同纹理，
            // 消除从俯视角看最扎眼的那条"色差硬线"。同时按可玩地形的 UV 映射给外延网格设 UV，让贴图无缝跨接缝。
            // 找不到地形材质才退回草地。
            Material ground = null;
            Mesh terrMesh = null; Vector3 terrPos = Vector3.zero;
            GameObject terrGO = GameObject.Find("Continuous_Terrain");
            if (terrGO != null)
            {
                var tr = terrGO.GetComponent<MeshRenderer>(); if (tr && tr.sharedMaterial) ground = tr.sharedMaterial;
                var tf = terrGO.GetComponent<MeshFilter>(); if (tf) terrMesh = tf.sharedMesh;
                terrPos = terrGO.transform.position;
            }
            if (ground == null) ground = AssetDatabase.LoadAssetAtPath<Material>("Assets/LycheeMap15/Materials/M_Config_Terrain_Grassland.mat");
            if (ground == null && elev.GetComponent<MeshRenderer>() is MeshRenderer mr0 && mr0.sharedMaterials.Length > 0)
                ground = mr0.sharedMaterials[0];

            // 求可玩地形 world(x,z)->uv 的线性映射，外延 UV 接续之（密度+相位都对齐 → 跨接缝无错位）。
            Vector2 uvO = Vector2.zero, uvK = new Vector2(0.2f, 0.2f); // 默认密度（地形若无 uv 时用，材质若是世界空间着色器则 uv 无所谓）
            if (terrMesh != null && terrMesh.uv != null && terrMesh.uv.Length > 0)
            {
                var u = terrMesh.uv; var b = terrMesh.bounds;
                float uMin = 1e9f, uMax = -1e9f, vMin = 1e9f, vMax = -1e9f;
                foreach (var p in u) { if (p.x < uMin) uMin = p.x; if (p.x > uMax) uMax = p.x; if (p.y < vMin) vMin = p.y; if (p.y > vMax) vMax = p.y; }
                float kx = (uMax - uMin) / Mathf.Max(0.01f, b.size.x);
                float kz = (vMax - vMin) / Mathf.Max(0.01f, b.size.z);
                float wx0 = b.min.x + terrPos.x, wz0 = b.min.z + terrPos.z;
                uvK = new Vector2(kx, kz);
                uvO = new Vector2(uMin - wx0 * kx, vMin - wz0 * kz); // uv(world)=uvO+world*uvK，边界处与地形对齐
            }

            // **变白根因**：LycheeTerrainSplat 按顶点色混层（wDirt=R, wRock=G, wVar=B；RGB=0 即纯草）。
            // 外延网格没顶点色 → Unity 默认白(1,1,1,1) → 各层全开 → 渲染成沙/白。
            // 取地形「草地」顶点（低 dirt=低 R）的平均色赋给外延网格 → 同 splat 渲染成同款绿草，与可玩区无缝。
            Color grassCol = new Color(0f, 0f, 0f, 0f);
            if (terrMesh != null && terrMesh.colors != null && terrMesh.colors.Length > 0)
            {
                var cs = terrMesh.colors; double r = 0, g = 0, b = 0, a = 0; int cnt = 0;
                foreach (var c in cs) if (c.r < 0.3f && c.g < 0.5f) { r += c.r; g += c.g; b += c.b; a += c.a; cnt++; }
                if (cnt > 0) grassCol = new Color((float)(r / cnt), (float)(g / cnt), (float)(b / cnt), (float)(a / cnt));
            }

            GameObject ext = GameObject.Find("Replay_Extended_Ground");
            if (ext == null)
            {
                ext = new GameObject("Replay_Extended_Ground");
                ext.AddComponent<MeshFilter>();
                ext.AddComponent<MeshRenderer>();
            }
            ext.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            ext.transform.localScale = Vector3.one;
            Mesh mesh = BuildSkirt(elev, uvO, uvK, grassCol);
            mesh = SaveMesh(mesh, MeshDir + "/Boundary_OuterTerrain.asset");
            ext.GetComponent<MeshFilter>().sharedMesh = mesh;
            if (ground != null) ext.GetComponent<MeshRenderer>().sharedMaterial = ground;
            EditorUtility.SetDirty(ext);

            // 边缘稀疏林带：打碎那条硬接缝（尤其兴庆宫一侧）。
            ScatterPerimeterTreeline(elev);
            AddXingqingPalaceBuffer(elev);

            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[BoundaryEnhance] 完成：外延材质={(ground!=null?ground.name:"-")}(接续UV+草地顶点色 rgba={grassCol.r:F2},{grassCol.g:F2},{grassCol.b:F2},{grassCol.a:F2}) + 窄衣领 + 边缘林带。场景: {scene.path}");
        }
        catch (Exception e)
        {
            Debug.LogError("[BoundaryEnhance] 异常: " + e.Message + "\n" + e.StackTrace);
        }
    }

    static Mesh BuildSkirt(MapElevationData elev, Vector2 uvO, Vector2 uvK, Color vcol)
    {
        int nx = Mathf.CeilToInt(OuterX * 2f / CellSize);
        int nz = Mathf.CeilToInt(OuterZ * 2f / CellSize);
        var verts = new Vector3[(nx + 1) * (nz + 1)];
        var uv = new Vector2[verts.Length];
        var colors = new Color[verts.Length];
        var tris = new int[nx * nz * 6];
        for (int zi = 0; zi <= nz; zi++)
            for (int xi = 0; xi <= nx; xi++)
            {
                float x = -OuterX + xi * CellSize;
                float z = -OuterZ + zi * CellSize;
                int idx = zi * (nx + 1) + xi;
                verts[idx] = new Vector3(x, SkirtHeight(elev, x, z), z);
                uv[idx] = new Vector2(uvO.x + x * uvK.x, uvO.y + z * uvK.y); // 接续可玩地形 UV：跨接缝纹理无缝
                colors[idx] = vcol;                                          // 草地顶点色：splat 渲染成绿草（非白沙）
            }
        int ti = 0;
        for (int zi = 0; zi < nz; zi++)
            for (int xi = 0; xi < nx; xi++)
            {
                int i = zi * (nx + 1) + xi;
                tris[ti++] = i; tris[ti++] = i + nx + 1; tris[ti++] = i + 1;
                tris[ti++] = i + 1; tris[ti++] = i + nx + 1; tris[ti++] = i + nx + 2;
            }
        var mesh = new Mesh { name = "Boundary_OuterTerrain", indexFormat = IndexFormat.UInt32 };
        mesh.vertices = verts; mesh.uv = uv; mesh.colors = colors; mesh.triangles = tris;
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>低平背景平面 + 接缝处一段**齐平外延草地**柔化硬切。
    /// 严格在可玩区内部：压到 -2.2 藏在地图之下（绝不盖图）。
    /// 可玩边缘外 0~FlushShoulderWidth：保持**可玩边缘同高**(edgeH)，让近景看起来和边界齐平。
    /// 再往外 FalloffWidth 范围内平缓降到背景 -2.2，远处才回到低平背景。
    /// **安全保证**：外延高度永不高于边缘 edgeH，只会齐平或向外下坡。</summary>
    static float SkirtHeight(MapElevationData elev, float x, float z)
    {
        float ax = Mathf.Abs(x), az = Mathf.Abs(z);
        if (ax < HalfX - 0.01f && az < HalfZ - 0.01f) return BackgroundY; // 可玩区内部：藏在地图之下
        float dx = Mathf.Max(0f, ax - HalfX);
        float dz = Mathf.Max(0f, az - HalfZ);
        float d = Mathf.Sqrt(dx * dx + dz * dz);                          // 离可玩矩形外的距离（边界上=0）
        float edgeH = elev.SampleWorldHeight(Mathf.Clamp(x, -HalfX, HalfX), Mathf.Clamp(z, -HalfZ, HalfZ));
        if (d <= FlushShoulderWidth) return edgeH;                        // 近景范围：与地图边缘齐平
        float t = Mathf.SmoothStep(0f, 1f, (d - FlushShoulderWidth) / FalloffWidth);
        return Mathf.Lerp(edgeH, BackgroundY, t);                         // 边缘同高 → 缓降到背景
    }

    static Mesh SaveMesh(Mesh mesh, string path)
    {
        if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null) AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(mesh, path);
        return AssetDatabase.LoadAssetAtPath<Mesh>(path);
    }

    static void EnsureMeshDir()
    {
        if (AssetDatabase.IsValidFolder(MeshDir)) return;
        Directory.CreateDirectory(Path.GetFullPath(Path.Combine(Application.dataPath, "..", MeshDir)));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
    }

    // ───────────────── 边缘林带 + 贴缝地被（双层遮硬接缝）─────────────────

    static readonly string[] TreeKeys  = { "tree", "birch", "果" };
    static readonly string[] CoverKeys = { "grass", "bush", "shrub", "fern", "clover", "weed", "flower", "groundcover", "foliage", "草", "灌" };
    static readonly string[] RockKeys  = { "rock", "stone", "boulder", "cliff", "石", "岩" }; // 克隆地图自己的褐色巨石（非低面苔藓石）

    /// <summary>沿可玩区周界遮住那条硬接缝：①较密的树紧贴边缘内侧、紧贴各建筑足迹外缘（按实测半径避让，不再在兴庆宫后留大缺口）；
    /// ②更密的低矮地被（克隆地图自己的草/灌）压在接缝线上，把树干之间露出的地面线也盖住。
    /// 全部克隆地图现有实例（同款同尺度），放独立根 BoundaryTreeline_20260615，可整体删。</summary>
    static void ScatterPerimeterTreeline(MapElevationData elev)
    {
        var old = GameObject.Find(TreelineRootName);
        if (old != null) UnityEngine.Object.DestroyImmediate(old);

        var trees = FindTemplates(TreeKeys, 14f);
        if (trees.Count == 0)
            foreach (var p in TreeFallbackPrefabs) { var pf = AssetDatabase.LoadAssetAtPath<GameObject>(p); if (pf != null) trees.Add(pf); }
        if (trees.Count == 0) { Debug.LogWarning("[BoundaryEnhance] 找不到树模板，跳过林带。"); return; }
        var cover = FindTemplates(CoverKeys, 4f); // 地图自己的低矮地被；没有就只放树
        var rocks = FindTemplates(RockKeys, 7f);  // 地图自己的褐色巨石

        Transform root = new GameObject(TreelineRootName).transform;

        // 道路中心线（避让）。
        var roadPts = new List<Vector2>();
        foreach (var e in UnityEngine.Object.FindObjectsByType<MapRouteEdge>(FindObjectsSortMode.None))
            if (e.pathPoints != null)
                foreach (var pp in e.pathPoints) if (pp) roadPts.Add(new Vector2(pp.position.x, pp.position.z));
        // 驿站中心 + 实测建筑半径（树贴足迹外缘，不再大范围避让留缺口）。
        var anchors = UnityEngine.Object.FindObjectsByType<MapStationAnchor>(FindObjectsSortMode.None);
        var stC = new Vector2[anchors.Length];
        var stR = new float[anchors.Length];
        for (int i = 0; i < anchors.Length; i++)
        {
            stC[i] = new Vector2(anchors[i].transform.position.x, anchors[i].transform.position.z);
            stR[i] = StationRadius(stC[i]);
        }

        var rnd = new System.Random(20260615);
        int nT = 0, nC = 0, nR = 0;
        // 一条边三层都撒；inward 指向可玩区内部。inMin 取负 = 向边缘外延伸（落在缓坡上、按距离渐疏）。
        void DoEdge(bool h, float fc, float a, float b, Vector2 inw)
        {
            // 树：从内侧 3m 一直延到外侧 OutReach，跨过边缘进入草地缓坡（外侧渐疏，不在边上突然消失）。
            nT += Scatter(root, elev, trees, rnd, roadPts, stC, stR, h, fc, a, b, inw,
                          /*step*/1.5f, /*prob*/0.92f, /*inMin*/-OutReach, /*inMax*/3.0f, /*roadClear*/2.6f, /*stMargin*/0.4f, 0.85f, 0.30f, "EdgeTree");
            // 石：稀疏，内外都撒（克隆地图自己的褐色巨石）。
            if (rocks.Count > 0)
                nR += Scatter(root, elev, rocks, rnd, roadPts, stC, stR, h, fc, a, b, inw,
                              /*step*/4.5f, /*prob*/0.70f, /*inMin*/-OutReach + 1f, /*inMax*/2.0f, /*roadClear*/3.2f, /*stMargin*/0.6f, 0.70f, 0.55f, "EdgeRock");
            // 地被：贴缝（内外各一点），盖住地面那条线。
            if (cover.Count > 0)
                nC += Scatter(root, elev, cover, rnd, roadPts, stC, stR, h, fc, a, b, inw,
                              /*step*/1.0f, /*prob*/0.95f, /*inMin*/-2.0f, /*inMax*/2.0f, /*roadClear*/1.5f, /*stMargin*/0.1f, 0.85f, 0.40f, "EdgeCover");
        }
        DoEdge(true,  HalfZ, -HalfX, HalfX, new Vector2(0, -1));
        DoEdge(true, -HalfZ, -HalfX, HalfX, new Vector2(0,  1));
        DoEdge(false, -HalfX, -HalfZ, HalfZ, new Vector2(1,  0));
        DoEdge(false,  HalfX, -HalfZ, HalfZ, new Vector2(-1, 0));
        Debug.Log($"[BoundaryEnhance] 边缘 树{nT} + 石{nR} + 地被{nC}（模板 树{trees.Count}/石{rocks.Count}/地被{cover.Count}）。");
    }

    /// <summary>兴庆宫离东边界太近，低角度近景会直接看到外侧空坡。
    /// 在宫殿东/东北侧额外放一圈不规则树丛、地被和少量岩石，用垂直层次挡掉空旷感。</summary>
    static void AddXingqingPalaceBuffer(MapElevationData elev)
    {
        var old = GameObject.Find(PalaceBufferRootName);
        if (old != null) UnityEngine.Object.DestroyImmediate(old);

        var station = UnityEngine.Object.FindObjectsByType<MapStationAnchor>(FindObjectsSortMode.None)
            .FirstOrDefault(a => a.stationId == "S15");
        if (station == null) return;

        var trees = FindTemplates(TreeKeys, 14f);
        if (trees.Count == 0)
            foreach (var p in TreeFallbackPrefabs) { var pf = AssetDatabase.LoadAssetAtPath<GameObject>(p); if (pf != null) trees.Add(pf); }
        var cover = FindTemplates(CoverKeys, 4f);
        var rocks = FindTemplates(RockKeys, 7f);
        if (trees.Count == 0 && cover.Count == 0 && rocks.Count == 0) return;

        Transform root = new GameObject(PalaceBufferRootName).transform;
        Vector2 center = new Vector2(station.transform.position.x, station.transform.position.z);
        float keepClear = StationRadius(center) + 0.65f;
        var rnd = new System.Random(2026061515);
        int nT = 0, nC = 0, nR = 0;

        Vector2[] treeOffsets =
        {
            new Vector2(4.6f, -5.0f), new Vector2(6.4f, -2.5f), new Vector2(7.4f, 0.6f),
            new Vector2(5.4f, 3.5f), new Vector2(3.0f, 5.8f), new Vector2(8.2f, 5.5f),
            new Vector2(2.2f, -6.4f), new Vector2(9.0f, -5.8f)
        };
        foreach (var o in treeOffsets)
            if (PlaceBufferObject(root, elev, trees, rnd, center, o, keepClear, 0.78f, 0.34f, "PalaceEdgeTree")) nT++;

        Vector2[] rockOffsets =
        {
            new Vector2(4.2f, -3.3f), new Vector2(6.1f, 4.4f), new Vector2(8.7f, 1.8f)
        };
        foreach (var o in rockOffsets)
            if (PlaceBufferObject(root, elev, rocks, rnd, center, o, keepClear + 0.25f, 0.58f, 0.30f, "PalaceEdgeRock")) nR++;

        if (cover.Count > 0)
        {
            for (float dz = -6.0f; dz <= 6.2f; dz += 1.15f)
            {
                for (float dx = 2.4f; dx <= 8.6f; dx += 1.35f)
                {
                    if (rnd.NextDouble() > 0.72) continue;
                    var jitter = new Vector2((float)(rnd.NextDouble() * 0.7 - 0.35), (float)(rnd.NextDouble() * 0.7 - 0.35));
                    if (PlaceBufferObject(root, elev, cover, rnd, center, new Vector2(dx, dz) + jitter, keepClear - 0.35f, 0.78f, 0.38f, "PalaceEdgeCover")) nC++;
                }
            }
        }

        Debug.Log($"[BoundaryEnhance] 兴庆宫边界缓冲 树{nT} + 石{nR} + 地被{nC}。");
    }

    static bool PlaceBufferObject(Transform root, MapElevationData elev, List<GameObject> templates, System.Random rnd,
        Vector2 center, Vector2 offset, float keepClear, float scaleBase, float scaleJit, string label)
    {
        if (templates == null || templates.Count == 0) return false;
        Vector2 p = center + offset;
        p.x = Mathf.Clamp(p.x, -HalfX + 0.5f, HalfX + OutReach - 0.4f);
        p.y = Mathf.Clamp(p.y, -HalfZ + 0.5f, HalfZ - 0.5f);
        if ((p - center).sqrMagnitude < keepClear * keepClear) return false;

        GameObject go = CloneTemplate(templates[rnd.Next(templates.Count)], root);
        if (go == null) return false;
        go.name = label;
        bool inside = Mathf.Abs(p.x) <= HalfX && Mathf.Abs(p.y) <= HalfZ;
        float y = inside ? elev.SampleWorldHeight(p.x, p.y) : SkirtHeight(elev, p.x, p.y);
        go.transform.position = new Vector3(p.x, y - 0.05f, p.y);
        go.transform.rotation = Quaternion.Euler(0f, (float)rnd.NextDouble() * 360f, 0f);
        float jitter = scaleBase + (float)rnd.NextDouble() * scaleJit;
        go.transform.localScale = go.transform.localScale * jitter;
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
        return true;
    }

    /// <summary>沿一条边按步长撒模板：垂直偏移 inOff∈[inMin,inMax]（正=可玩区内, 负=边缘外缓坡）。
    /// 外侧按到边缘的距离渐疏（边满外空）。内侧落地形面、外侧落衣领/背景面。避开道路与建筑足迹。</summary>
    static int Scatter(Transform root, MapElevationData elev, List<GameObject> tpls, System.Random rnd,
        List<Vector2> roadPts, Vector2[] stC, float[] stR, bool horizontal, float fixedCoord, float a, float b, Vector2 inward,
        float step, float prob, float inMin, float inMax, float roadClear, float stMargin, float scaleBase, float scaleJit, string label)
    {
        int n = 0;
        for (float s = a; s <= b; s += step)
        {
            if (rnd.NextDouble() > prob) continue;
            float along = s + (float)(rnd.NextDouble() * 2 - 1) * step * 0.45f;
            float inOff = inMin + (float)rnd.NextDouble() * (inMax - inMin);
            // 外侧(inOff<0)按距离渐疏：边缘处满密、到外缘 inMin 处归零。
            if (inOff < 0f)
            {
                float fade = Mathf.Clamp01(1f + inOff / Mathf.Max(0.01f, -inMin));
                if (rnd.NextDouble() > fade) continue;
            }
            Vector2 bp = horizontal ? new Vector2(along, fixedCoord) : new Vector2(fixedCoord, along);
            Vector2 p = bp + inward * inOff;
            // 沿边方向夹在边长内；垂直方向允许外延到 OutReach。
            p.x = Mathf.Clamp(p.x, -(HalfX + OutReach), HalfX + OutReach);
            p.y = Mathf.Clamp(p.y, -(HalfZ + OutReach), HalfZ + OutReach); // p.y 即 z

            bool skip = false;
            for (int i = 0; i < roadPts.Count; i++) if ((p - roadPts[i]).sqrMagnitude < roadClear * roadClear) { skip = true; break; }
            if (!skip) for (int i = 0; i < stC.Length; i++) { float rr = stR[i] + stMargin; if ((p - stC[i]).sqrMagnitude < rr * rr) { skip = true; break; } }
            if (skip) continue;

            GameObject tpl = tpls[rnd.Next(tpls.Count)];
            GameObject go = CloneTemplate(tpl, root);
            if (go == null) continue;
            go.name = label;
            // 内侧落可玩地形面，外侧落衣领/背景面（与那块草地同高，不悬空/不扎地）。
            bool inside = Mathf.Abs(p.x) <= HalfX && Mathf.Abs(p.y) <= HalfZ;
            float hh = inside ? elev.SampleWorldHeight(p.x, p.y) : SkirtHeight(elev, p.x, p.y);
            go.transform.position = new Vector3(p.x, hh - 0.05f, p.y);
            go.transform.rotation = Quaternion.Euler(0, (float)rnd.NextDouble() * 360f, 0);
            float jitter = scaleBase + (float)rnd.NextDouble() * scaleJit;
            go.transform.localScale = tpl.transform.localScale * jitter;
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            n++;
        }
        return n;
    }

    /// <summary>实测某驿站的建筑足迹半径：包住其 6m 内的非地形/非道路/非装饰渲染器，取 max(x,z) 范围，钳 [2,6]。</summary>
    static float StationRadius(Vector2 c)
    {
        Bounds? bb = null;
        foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            string nl = r.gameObject.name.ToLower();
            if (nl.Contains("route") || nl.Contains("overlay") || nl.Contains("line") || nl.Contains("edge") ||
                nl.Contains("terrain") || nl.Contains("ground") || nl.Contains("tree") || nl.Contains("rock") ||
                nl.Contains("grass") || nl.Contains("bush") || nl.Contains("plant") || nl.Contains("river") || nl.Contains("water"))
                continue;
            Vector3 bc = r.bounds.center;
            if (new Vector2(bc.x, bc.z).Equals(c)) { }
            if ((new Vector2(bc.x, bc.z) - c).sqrMagnitude > 36f) continue; // 6m 内
            if (bb == null) bb = r.bounds; else { var t = bb.Value; t.Encapsulate(r.bounds); bb = t; }
        }
        if (bb == null) return 3.0f;
        return Mathf.Clamp(Mathf.Max(bb.Value.extents.x, bb.Value.extents.z), 2.0f, 6.0f);
    }

    /// <summary>找地图现有实例作克隆模板（同款同尺度）：名字含 keys、落在可玩区内、体量≤maxExtent；
    /// prefab 整株过大时退用单个渲染器的 GO（取到单丛地被而非整组）。按来源去重，≤8 种。</summary>
    static List<GameObject> FindTemplates(string[] keys, float maxExtent)
    {
        var found = new List<GameObject>();
        var seen = new HashSet<UnityEngine.Object>();
        foreach (var mr in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
        {
            GameObject go = mr.gameObject;
            if (go.name.StartsWith("Edge")) continue;
            GameObject root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (root == null) root = go;
            string nl = root.name.ToLower(), nlGo = go.name.ToLower();
            if (!keys.Any(k => nl.Contains(k)) && !keys.Any(k => nlGo.Contains(k))) continue;
            if (root.name.Contains(TreelineRootName)) continue;
            Vector3 p = root.transform.position;
            if (Mathf.Abs(p.x) > HalfX + 2f || Mathf.Abs(p.z) > HalfZ + 2f) continue;
            GameObject cand = root;
            if (Extent(cand) > maxExtent) { cand = go; if (Extent(cand) > maxExtent) continue; } // 整株太大→退单丛
            var src = PrefabUtility.GetCorrespondingObjectFromSource(cand);
            UnityEngine.Object key = src != null ? src : cand;
            if (!seen.Add(key)) continue;
            found.Add(cand);
            if (found.Count >= 8) break;
        }
        return found;
    }

    static float Extent(GameObject g)
    {
        var rs = g.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return 0f;
        Bounds b = rs[0].bounds;
        foreach (var r in rs) b.Encapsulate(r.bounds);
        return Mathf.Max(b.extents.x, b.extents.z);
    }

    static GameObject CloneTemplate(GameObject template, Transform parent)
    {
        var src = PrefabUtility.GetCorrespondingObjectFromSource(template);
        if (src == null && PrefabUtility.IsPartOfPrefabAsset(template)) src = template; // 直接来自 prefab 资源（fallback）
        if (src != null) return PrefabUtility.InstantiatePrefab(src, parent) as GameObject;
        var go = UnityEngine.Object.Instantiate(template, parent);
        return go;
    }
}
