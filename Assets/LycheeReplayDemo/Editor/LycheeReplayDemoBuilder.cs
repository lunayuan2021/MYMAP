using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

public static class LycheeReplayDemoBuilder
{
    const string MapScene = "Assets/Scenes/GeneratedMaps/litchi_map_unity_full_coverage_v2.unity";
    const string DemoScene = "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity";

    [MenuItem("Tools/Lychee Map/Replay/Build Replay Demo Scene")]
    public static void Build()
    {
        Scene scene = EditorSceneManager.OpenScene(MapScene, OpenSceneMode.Single);
        GameObject old = GameObject.Find("LYCHEE_REPLAY_DEMO");
        if (old != null) Object.DestroyImmediate(old);

        GameObject root = new GameObject("LYCHEE_REPLAY_DEMO");
        LycheeReplayPlayer replay = root.AddComponent<LycheeReplayPlayer>();
        replay.replayFile = AssetDatabase.LoadAssetAtPath<TextAsset>(
            "Assets/LycheeReplayDemo/Data/reply_full_coverage_unity_v2.txt");
        replay.carriageModel = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/LycheeReplayDemo/ModelsGLB/Carriage.glb");
        replay.boatModel = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/LycheeReplayDemo/ModelsGLB/Boat.glb");
        replay.captainModel = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/LycheeReplayDemo/ModelsGLB/Captain.glb");
        // Member.glb 才有完整 Mixamo 骨骼（SquadMember.glb 是单静态节点，骨骼动画播不了）
        replay.squadMemberModel = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/LycheeReplayDemo/ModelsGLB/Member.glb");
        replay.roadObstacleModel = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/LycheeReplayDemo/ModelsGLB/RoadObstacle.glb");
        replay.secondsPerRound = 0.40f;
        replay.playOnStart = true;

        PlaceStationModels();
        CreateExtendedGround();

        foreach (MapStationAnchor station in Object.FindObjectsByType<MapStationAnchor>(FindObjectsSortMode.None))
        {
            Transform model = station.transform.Find("Imported_Station_Model");
            bool showPlaceholder = model == null;
            foreach (Transform child in station.transform)
            {
                if (child.name.StartsWith("Placeholder_"))
                    child.gameObject.SetActive(showPlaceholder);
            }
            CreateChineseNodeLabel(station);
        }

        Camera camera = Object.FindFirstObjectByType<Camera>();
        if (camera != null)
        {
            camera.transform.position = new Vector3(32f, 70f, -56f);
            camera.transform.rotation = Quaternion.Euler(52f, -29f, 0f);
            camera.orthographicSize = 35f;
            camera.allowHDR = true;
            camera.allowMSAA = true;
            UniversalAdditionalCameraData cameraData = camera.GetUniversalAdditionalCameraData();
            cameraData.renderPostProcessing = true;
            cameraData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            cameraData.antialiasingQuality = AntialiasingQuality.High;
            LycheeReplayCameraController controller =
                camera.GetComponent<LycheeReplayCameraController>() ??
                camera.gameObject.AddComponent<LycheeReplayCameraController>();
            controller.replay = replay;
        }

        QualitySettings.antiAliasing = 4;
        QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
        QualitySettings.lodBias = 2f;
        if (QualitySettings.renderPipeline is UniversalRenderPipelineAsset pipeline)
        {
            pipeline.msaaSampleCount = 4;
            pipeline.renderScale = 1f;
            EditorUtility.SetDirty(pipeline);
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, DemoScene);

        EditorBuildSettings.scenes = new[]
        {
            new EditorBuildSettingsScene(DemoScene, true)
        };
        Selection.activeGameObject = root;
        Debug.Log("Replay Demo scene built: " + DemoScene);
    }

    static void CreateChineseNodeLabel(MapStationAnchor station)
    {
        Transform old = station.transform.Find("Node_Name_CN");
        if (old != null) Object.DestroyImmediate(old.gameObject);

        GameObject labelObject = new GameObject("Node_Name_CN");
        labelObject.transform.SetParent(station.transform, false);
        labelObject.transform.localPosition = new Vector3(0f, 3.15f, 0f);
        labelObject.AddComponent<LycheeReplayBillboard>();
        TextMesh label = labelObject.AddComponent<TextMesh>();
        label.text = station.displayName;
        label.anchor = TextAnchor.MiddleCenter;
        label.alignment = TextAlignment.Center;
        label.fontSize = 36;
        label.characterSize = 0.075f;
        label.color = new Color(1f, 0.86f, 0.46f);
        labelObject.AddComponent<LycheeChineseLabel>();
    }

    static void CreateExtendedGround()
    {
        GameObject old = GameObject.Find("Replay_Extended_Ground");
        if (old != null) Object.DestroyImmediate(old);

        MeshRenderer terrainRenderer = GameObject.Find("Continuous_Terrain")?.GetComponent<MeshRenderer>();
        if (terrainRenderer == null) return;

        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ground.name = "Replay_Extended_Ground";
        ground.transform.position = new Vector3(0f, -2.2f, 0f);
        ground.transform.localScale = new Vector3(220f, 1f, 180f);
        ground.GetComponent<MeshRenderer>().sharedMaterial = terrainRenderer.sharedMaterial;
        Object.DestroyImmediate(ground.GetComponent<Collider>());
    }

    static void PlaceStationModels()
    {
        var mappings = new Dictionary<string, (string path, float size, float yaw)>
        {
            ["S01"] = ("Assets/LycheeReplayDemo/ModelsGLB/Stations/LingnanOrchard.glb", 5.8f, 20f),
            ["S02"] = ("Assets/LycheeReplayDemo/ModelsGLB/Stations/NanlingStation.glb", 4.2f, -15f),
            ["S03"] = ("Assets/LycheeReplayDemo/ModelsGLB/Stations/MeiguanStation.glb", 4.2f, 12f),
            ["S04"] = ("Assets/LycheeReplayDemo/ModelsGLB/Stations/JiangnanDock.glb", 5.2f, -25f),
            ["S05"] = ("Assets/LycheeReplayDemo/ModelsGLB/Stations/DongtingWaterStation.glb", 4.8f, 18f),
            ["S06"] = ("Assets/LycheeReplayDemo/ModelsGLB/Stations/WulingMountainRoad.glb", 6.4f, -8f),
            ["S08"] = ("Assets/LycheeReplayDemo/ModelsGLB/Stations/QinlingPlankRoad.glb", 6.4f, 15f),
            ["S09"] = ("Assets/LycheeReplayDemo/ModelsGLB/Stations/LuoyangStation.glb", 4.5f, -12f),
            ["S11"] = ("Assets/LycheeReplayDemo/ModelsGLB/Stations/TongguanStation.glb", 4.7f, 10f),
            ["S12"] = ("Assets/LycheeReplayDemo/ModelsGLB/Stations/GuanzhongPlain.glb", 5.8f, 0f),
            ["S13"] = ("Assets/LycheeReplayDemo/ModelsGLB/Stations/BaqiaoStation.glb", 4.8f, -18f)
        };

        foreach (MapStationAnchor station in Object.FindObjectsByType<MapStationAnchor>(FindObjectsSortMode.None))
        {
            Transform existing = station.transform.Find("Imported_Station_Model");
            if (existing != null) Object.DestroyImmediate(existing.gameObject);
            if (!mappings.TryGetValue(station.stationId, out var mapping)) continue;

            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(mapping.path);
            if (asset == null)
            {
                Debug.LogWarning("Station model not found: " + mapping.path);
                continue;
            }

            GameObject wrapper = new GameObject("Imported_Station_Model");
            wrapper.transform.SetParent(station.transform, false);
            wrapper.transform.localRotation = Quaternion.Euler(0f, mapping.yaw, 0f);
            GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(asset, wrapper.transform);
            NormalizeModel(model, wrapper.transform.position - Vector3.up * 0.075f, mapping.size);
        }
    }

    static void NormalizeModel(GameObject model, Vector3 anchor, float targetSize)
    {
        Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return;

        Bounds bounds = renderers[0].bounds;
        foreach (Renderer renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
        float largest = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
        if (largest > 0.0001f) model.transform.localScale *= targetSize / largest;

        bounds = renderers[0].bounds;
        foreach (Renderer renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
        model.transform.position += anchor - new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);

        foreach (Collider collider in model.GetComponentsInChildren<Collider>(true))
            collider.enabled = false;
    }
}
