// 把 3 个事件 GLB（glTFast ScriptedImporter 导入）烘成「原生 Unity 预制体」：独立 Mesh(.asset) + 抠出的贴图(.png) +
// URP/Lit 材质(.mat) + Prefab(.prefab)。原因：glTFast 导入的 GLB 预制体在编辑器里引用正常，但在 IL2CPP/WebGL 构建里
// 场景对它的引用会变 null（依赖游走器没把 GLB 内部子资源带进包）→ 运行时 GetEventModel 返回 null → 事件特效回退成 2D 贴图。
// 原生 mesh/贴图/材质/prefab 一定进包，彻底解决。烘完把 LycheeReplayPlayer.eventModel* 重指到 baked prefab（所有 GeneratedMaps 场景）。
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public static class LycheeBakeEventModels_20260630
{
    const string OutDir = "Assets/LycheeReplayDemo/EventModelBaked";

    static readonly (string key, string glb, string outName)[] Models = new[]
    {
        ("ICE_BOX", "Assets/LycheeReplayDemo/ModelsGLB/event_icebox.glb", "event_icebox_baked"),
        ("HORSE",   "Assets/LycheeReplayDemo/ModelsGLB/event_horse.glb",  "event_horse_baked"),
        ("SPOIL",   "Assets/LycheeReplayDemo/ModelsGLB/event_spoil.glb",  "event_spoil_baked"),
    };

    [MenuItem("Lychee/Bake Event Models To Native Prefabs")]
    public static void Bake()
    {
        if (!Directory.Exists(OutDir)) Directory.CreateDirectory(OutDir);

        var bakedPrefabs = new Dictionary<string, GameObject>();
        var report = new System.Text.StringBuilder();

        foreach (var m in Models)
        {
            GameObject glb = AssetDatabase.LoadAssetAtPath<GameObject>(m.glb);
            if (glb == null) { report.AppendLine(m.outName + ": GLB 加载失败 " + m.glb); continue; }

            GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(glb);
            try
            {
                var mfs = inst.GetComponentsInChildren<MeshFilter>(true);
                var combines = new List<CombineInstance>();
                Texture srcTex = null;
                foreach (var mf in mfs)
                {
                    if (mf.sharedMesh == null) continue;
                    // 把每个 meshFilter 相对 GLB 根的变换烘进顶点（保留 glTF 坐标系转换后的朝向）。
                    Matrix4x4 rel = inst.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                    combines.Add(new CombineInstance { mesh = mf.sharedMesh, transform = rel, subMeshIndex = 0 });
                    if (srcTex == null)
                    {
                        var r = mf.GetComponent<MeshRenderer>();
                        if (r != null && r.sharedMaterial != null) srcTex = ExtractBaseTex(r.sharedMaterial);
                    }
                }
                if (combines.Count == 0) { report.AppendLine(m.outName + ": 无可用 mesh"); continue; }

                // 1) 烘 mesh
                Mesh baked = new Mesh { name = m.outName };
                baked.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                baked.CombineMeshes(combines.ToArray(), true, true);
                baked.RecalculateBounds();
                string meshPath = OutDir + "/" + m.outName + ".asset";
                AssetDatabase.DeleteAsset(meshPath);
                AssetDatabase.CreateAsset(baked, meshPath);

                // 2) 抠贴图（源贴图 texturesReadable=0，必须走 Blit→RT→ReadPixels）
                Texture2D savedTex = null;
                if (srcTex != null)
                {
                    string pngPath = OutDir + "/" + m.outName + "_albedo.png";
                    SaveTexturePng(srcTex, pngPath);
                    AssetDatabase.ImportAsset(pngPath, ImportAssetOptions.ForceSynchronousImport);
                    savedTex = AssetDatabase.LoadAssetAtPath<Texture2D>(pngPath);
                }

                // 3) 材质（URP/Lit，贴图进 _BaseMap；运行时 ApplyOverheadModelMaterial 会读 _BaseMap 再换 overlay shader）
                Shader lit = Shader.Find("Universal Render Pipeline/Lit");
                if (lit == null) lit = Shader.Find("Universal Render Pipeline/Unlit");
                Material mat = new Material(lit) { name = m.outName + "_mat" };
                if (savedTex != null && mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", savedTex);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
                string matPath = OutDir + "/" + m.outName + ".mat";
                AssetDatabase.DeleteAsset(matPath);
                AssetDatabase.CreateAsset(mat, matPath);

                // 4) prefab
                GameObject root = new GameObject(m.outName);
                root.AddComponent<MeshFilter>().sharedMesh = baked;
                var mr = root.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                string prefabPath = OutDir + "/" + m.outName + ".prefab";
                AssetDatabase.DeleteAsset(prefabPath);
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                Object.DestroyImmediate(root);

                bakedPrefabs[m.key] = prefab;
                report.AppendLine(m.outName + ": tris=" + (baked.triangles.Length / 3) + " tex=" + (savedTex != null ? savedTex.width + "x" + savedTex.height : "NONE") + " prefab=" + (prefab != null));
            }
            finally { Object.DestroyImmediate(inst); }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // 5) 重指所有 GeneratedMaps 场景的 LycheeReplayPlayer 引用到 baked prefab
        int scenesPatched = RepointAllScenes(bakedPrefabs, report);
        report.AppendLine("scenes patched=" + scenesPatched);

        Debug.Log("[BakeEventModels] \n" + report.ToString());
    }

    static Texture ExtractBaseTex(Material src)
    {
        if (src.HasProperty("_BaseMap") && src.GetTexture("_BaseMap") != null) return src.GetTexture("_BaseMap");
        if (src.HasProperty("baseColorTexture") && src.GetTexture("baseColorTexture") != null) return src.GetTexture("baseColorTexture");
        if (src.HasProperty("_MainTex") && src.GetTexture("_MainTex") != null) return src.GetTexture("_MainTex");
        return src.mainTexture;
    }

    static void SaveTexturePng(Texture src, string path)
    {
        RenderTexture rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(src, rt);
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
    }

    static int RepointAllScenes(Dictionary<string, GameObject> baked, System.Text.StringBuilder report)
    {
        if (baked.Count == 0) return 0;
        string[] guids = AssetDatabase.FindAssets("t:Scene", new[] { "Assets/Scenes/GeneratedMaps" });
        int patched = 0;
        foreach (string g in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(g);
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            var player = Object.FindFirstObjectByType<LycheeReplayPlayer>();
            if (player == null) continue;
            var so = new SerializedObject(player);
            bool changed = false;
            if (baked.ContainsKey("ICE_BOX")) { so.FindProperty("eventModelIceBox").objectReferenceValue = baked["ICE_BOX"]; changed = true; }
            if (baked.ContainsKey("HORSE")) { so.FindProperty("eventModelHorse").objectReferenceValue = baked["HORSE"]; changed = true; }
            if (baked.ContainsKey("SPOIL")) { so.FindProperty("eventModelSpoil").objectReferenceValue = baked["SPOIL"]; changed = true; }
            if (changed)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                patched++;
                report.AppendLine("  repointed: " + path);
            }
        }
        return patched;
    }
}
#endif
