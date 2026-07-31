using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// WebGL 降显存/降内存工具(离线处理 .asset 网格资源,不动 GLB/FBX 导入资源):
///   ① 把烘焙世界网格设为不可读(Read/Write off)——去掉运行时白占一份的 CPU 副本(尤其 WaterStationSplit 的 50MB 级大网格)。零画质影响。
///   ② 把顶点数 &lt; 65535 却用 32 位索引的小网格降到 16 位索引——索引缓冲减半。画质无关,但生成网格拓扑特殊,改后请在场景里目视确认无破面。
/// 注意:这两项都改源 .asset、对所有平台生效;运行前已有 git 检查点可回滚。
/// 若某网格被 MeshCollider 用作碰撞体且运行时需要可读,去 R/W 会报错——本回放为纯视觉网格,通常无此问题,但改完务必出包验证。
/// </summary>
public static class LycheeMeshMemoryOptimizer
{
    // 扫描这些目录下的 Mesh .asset(烘焙世界网格 / 地形 / 水驿)。如有其它烘焙网格目录可在此追加。
    static readonly string[] MeshFolders =
    {
        "Assets/LycheeMap15/GeneratedMeshes",
    };

    [MenuItem("Lychee/降显存/① 烘焙网格去 Read-Write (安全, 省内存)")]
    static void StripReadWrite()
    {
        List<Mesh> meshes = CollectMeshes();
        if (meshes.Count == 0) return;

        int changed = 0;
        long estSavedBytes = 0;
        try
        {
            for (int i = 0; i < meshes.Count; i++)
            {
                Mesh m = meshes[i];
                EditorUtility.DisplayProgressBar("去 Read-Write", m.name, (float)i / meshes.Count);
                if (!m.isReadable) continue;

                SerializedObject so = new SerializedObject(m);
                SerializedProperty prop = so.FindProperty("m_IsReadable");
                if (prop == null) continue;
                prop.boolValue = false;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(m);

                estSavedBytes += EstimateMeshBytes(m);
                changed++;
            }
            AssetDatabase.SaveAssets();
        }
        finally { EditorUtility.ClearProgressBar(); }

        Debug.Log($"[MeshMemoryOptimizer] ① 去 Read-Write 完成:处理 {changed} / {meshes.Count} 个网格,预计省 ~{estSavedBytes / (1024 * 1024)} MB(运行态 CPU 副本 / WASM 堆)。改后请重新出 WebGL 包验证。");
    }

    [MenuItem("Lychee/降显存/② 小网格索引降 16 位 (需目视验证)")]
    static void DowncastIndex()
    {
        List<Mesh> meshes = CollectMeshes();
        if (meshes.Count == 0) return;

        int changed = 0;
        long estSavedBytes = 0;
        try
        {
            for (int i = 0; i < meshes.Count; i++)
            {
                Mesh m = meshes[i];
                EditorUtility.DisplayProgressBar("索引降 16 位", m.name, (float)i / meshes.Count);
                if (m.indexFormat != IndexFormat.UInt32) continue;
                if (m.vertexCount >= 65535) continue; // 顶点超 16 位上限,必须保留 32 位(如 S04/S05_House 大网格)

                long idxCount = 0;
                for (int s = 0; s < m.subMeshCount; s++) idxCount += m.GetIndexCount(s);

                m.indexFormat = IndexFormat.UInt16; // Unity 会就地转换现有索引数据
                EditorUtility.SetDirty(m);

                estSavedBytes += idxCount * 2; // 每索引 4→2 字节
                changed++;
            }
            AssetDatabase.SaveAssets();
        }
        finally { EditorUtility.ClearProgressBar(); }

        Debug.Log($"[MeshMemoryOptimizer] ② 索引降 16 位完成:处理 {changed} / {meshes.Count} 个网格,预计省 ~{estSavedBytes / (1024 * 1024)} MB。请在各地图场景里目视确认网格无破面/错位。");
    }

    static List<Mesh> CollectMeshes()
    {
        List<Mesh> result = new List<Mesh>();
        List<string> folders = new List<string>();
        foreach (string f in MeshFolders)
            if (AssetDatabase.IsValidFolder(f)) folders.Add(f);

        if (folders.Count == 0)
        {
            Debug.LogWarning("[MeshMemoryOptimizer] 未找到任何网格目录,检查 MeshFolders 配置。");
            return result;
        }

        foreach (string guid in AssetDatabase.FindAssets("t:Mesh", folders.ToArray()))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            foreach (Object obj in AssetDatabase.LoadAllAssetsAtPath(path))
                if (obj is Mesh mesh) result.Add(mesh);
        }
        return result;
    }

    /// <summary>粗估网格顶点+索引字节(用于日志,非精确)。</summary>
    static long EstimateMeshBytes(Mesh m)
    {
        const long perVertex = 40; // position+normal+tangent+uv 量级的保守估计
        long idx = 0;
        for (int s = 0; s < m.subMeshCount; s++) idx += m.GetIndexCount(s);
        int bytesPerIndex = m.indexFormat == IndexFormat.UInt32 ? 4 : 2;
        return (long)m.vertexCount * perVertex + idx * bytesPerIndex;
    }
}
