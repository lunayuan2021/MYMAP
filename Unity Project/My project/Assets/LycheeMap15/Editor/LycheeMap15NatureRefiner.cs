using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class LycheeMap15NatureRefiner
{
    const string ScenePath = "Assets/Scenes/LycheeMap15_TerrainBase.unity";
    const string NatureRoot = "Assets/Pure Poly/Free Low Poly Nature Pack/Prefabs/";
    const string TreeRoot = "Assets/Tree_Packs/URP_Tree_Pack/Prefabs/";
    const string MaterialRoot = "Assets/LycheeMap15/Materials/";
    const string MeshRoot = "Assets/LycheeMap15/Meshes/";

    [MenuItem("Tools/Lychee Map/Refine With Imported Nature Assets")]
    public static void Refine()
    {
        if (EditorSceneManager.GetActiveScene().path != ScenePath)
            EditorSceneManager.OpenScene(ScenePath);

        GameObject root = GameObject.Find("LYCHEE_MAP_15_TERRAIN_BASE");
        if (root == null)
            throw new InvalidOperationException("Open or rebuild LycheeMap15_TerrainBase first.");

        Transform environment = root.transform.Find("04_Environment");
        Transform terrain = root.transform.Find("01_Continuous_Terrain/ContinuousTerrainMesh");
        Transform routes = root.transform.Find("02_Route_Layers");
        if (environment == null || terrain == null || routes == null)
            throw new InvalidOperationException("Required map layers are missing.");

        ClearGeneratedNature(environment);
        GenerateGroundTextures();
        PrepareTreeMaterials();
        RefineGround(terrain);

        List<Vector3> routePoints = FindRoutePoints(root);
        List<Vector3> stationPoints = root.GetComponentsInChildren<MapStationAnchor>(true)
            .Select(anchor => anchor.transform.position)
            .ToList();

        System.Random random = new System.Random(260609);
        CreateRefinedTrees(environment, random, routePoints, stationPoints);
        CreateRefinedGrass(environment, random, routePoints, stationPoints);
        CreateRefinedRocks(environment, random, routePoints, stationPoints);
        CreateRiverBanks(environment, routes);
        RefineWater(routes);
        RefineLighting(root);

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Selection.activeGameObject = root;
        Debug.Log("LycheeMap15 refined with imported nature assets.");
    }

    static void ClearGeneratedNature(Transform environment)
    {
        string[] names =
        {
            "Mountain_And_Cliff_Forms",
            "Vegetation",
            "Refined_Vegetation",
            "Refined_Rocks_And_Cliffs",
            "Refined_River_Banks"
        };

        foreach (string name in names)
        {
            Transform child = environment.Find(name);
            if (child != null)
                UnityEngine.Object.DestroyImmediate(child.gameObject);
        }
    }

    static void RefineGround(Transform terrain)
    {
        Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialRoot + "M_Terrain_Refined.mat");
        Shader shader = Shader.Find("Lychee/Terrain Blend");
        if (material == null)
        {
            material = new Material(shader != null ? shader : Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(material, MaterialRoot + "M_Terrain_Refined.mat");
        }
        else if (shader != null)
        {
            material.shader = shader;
        }

        Texture2D green = AssetDatabase.LoadAssetAtPath<Texture2D>(MaterialRoot + "T_Grassland.asset");
        Texture2D dirt = AssetDatabase.LoadAssetAtPath<Texture2D>(MaterialRoot + "T_SandySoil.asset");
        Texture2D rock = AssetDatabase.LoadAssetAtPath<Texture2D>(MaterialRoot + "T_WeatheredRock.asset");
        if (material.HasProperty("_GrassTex")) material.SetTexture("_GrassTex", green);
        if (material.HasProperty("_DirtTex")) material.SetTexture("_DirtTex", dirt);
        if (material.HasProperty("_RockTex")) material.SetTexture("_RockTex", rock);
        if (material.HasProperty("_GrassTint")) material.SetColor("_GrassTint", new Color(0.86f, 0.91f, 0.78f));
        if (material.HasProperty("_LightGrassTint")) material.SetColor("_LightGrassTint", new Color(0.90f, 0.82f, 0.48f));
        if (material.HasProperty("_DeepGrassTint")) material.SetColor("_DeepGrassTint", new Color(0.36f, 0.54f, 0.22f));
        if (material.HasProperty("_DryGrassTint")) material.SetColor("_DryGrassTint", new Color(0.69f, 0.57f, 0.31f));
        if (material.HasProperty("_DirtTint")) material.SetColor("_DirtTint", new Color(0.88f, 0.82f, 0.70f));
        if (material.HasProperty("_RockTint")) material.SetColor("_RockTint", new Color(0.82f, 0.83f, 0.79f));
        if (material.HasProperty("_TextureScale")) material.SetFloat("_TextureScale", 0.34f);
        if (material.HasProperty("_SlopeStart")) material.SetFloat("_SlopeStart", 0.12f);
        if (material.HasProperty("_SlopeEnd")) material.SetFloat("_SlopeEnd", 0.48f);
        terrain.GetComponent<MeshRenderer>().sharedMaterial = material;
        EditorUtility.SetDirty(material);
    }

    static List<Vector3> FindRoutePoints(GameObject root)
    {
        List<Vector3> points = new List<Vector3>();
        foreach (MapRouteEdge edge in root.GetComponentsInChildren<MapRouteEdge>(true))
        {
            if (edge.pathPoints == null) continue;
            points.AddRange(edge.pathPoints.Where(point => point != null).Select(point => point.position));
        }
        return points;
    }

    static bool IsClear(Vector3 point, List<Vector3> routePoints, List<Vector3> stationPoints, float routeRadius, float stationRadius)
    {
        foreach (Vector3 route in routePoints)
        {
            Vector2 delta = new Vector2(point.x - route.x, point.z - route.z);
            if (delta.sqrMagnitude < routeRadius * routeRadius) return false;
        }
        foreach (Vector3 station in stationPoints)
        {
            Vector2 delta = new Vector2(point.x - station.x, point.z - station.z);
            if (delta.sqrMagnitude < stationRadius * stationRadius) return false;
        }
        return true;
    }

    static Vector3 RandomPoint(System.Random random)
    {
        float x = -38f + (float)random.NextDouble() * 76f;
        float z = -28f + (float)random.NextDouble() * 56f;
        return new Vector3(x, LycheeMap15Generator.Height(x, z), z);
    }

    static void CreateRefinedTrees(
        Transform environment,
        System.Random random,
        List<Vector3> routePoints,
        List<Vector3> stationPoints)
    {
        Transform parent = NewParent(environment, "Refined_Vegetation");
        Transform canopy = NewParent(parent, "Trees_LOD");
        GameObject[] detailed =
        {
            LoadPrefab(TreeRoot + "URP_Tree_1.prefab"),
            LoadPrefab(TreeRoot + "URP_Tree_2.prefab"),
            LoadPrefab(TreeRoot + "URP_Tree_3.prefab")
        };
        int created = 0;
        int centerAttempts = 0;
        for (int cluster = 0; cluster < 38 && centerAttempts < 600; cluster++)
        {
            Vector3 center;
            do
            {
                center = RandomPoint(random);
                centerAttempts++;
            }
            while (centerAttempts < 600 && !IsClear(center, routePoints, stationPoints, 3.2f, 4.2f));

            int treesInCluster = random.Next(6, 13);
            float clusterRadius = Mathf.Lerp(2.2f, 4.8f, (float)random.NextDouble());
            for (int i = 0; i < treesInCluster; i++)
            {
                float angle = (float)random.NextDouble() * Mathf.PI * 2f;
                float radius = Mathf.Sqrt((float)random.NextDouble()) * clusterRadius;
                float x = center.x + Mathf.Cos(angle) * radius;
                float z = center.z + Mathf.Sin(angle) * radius;
                Vector3 point = new Vector3(x, LycheeMap15Generator.Height(x, z), z);
                if (x < -39f || x > 39f || z < -29f || z > 29f) continue;
                if (!IsClear(point, routePoints, stationPoints, 1.55f, 2.7f)) continue;
                GameObject prefab = detailed[random.Next(detailed.Length)];
                if (prefab == null) continue;
                float scale = Mathf.Lerp(0.30f, 0.54f, (float)random.NextDouble());
                InstantiatePrefab(prefab, canopy, point, scale, random);
                created++;
            }
        }
    }

    static void CreateRefinedGrass(
        Transform environment,
        System.Random random,
        List<Vector3> routePoints,
        List<Vector3> stationPoints)
    {
        Transform vegetation = environment.Find("Refined_Vegetation");
        Transform parent = NewParent(vegetation, "Grass_And_Groundcover");
        GameObject[] choices =
        {
            LoadPrefab(NatureRoot + "PP_Grass_11.prefab"),
            LoadPrefab(NatureRoot + "PP_Grass_15.prefab")
        };

        int created = 0;
        int attempts = 0;
        while (created < 1250 && attempts++ < 8500)
        {
            Vector3 point = RandomPoint(random);
            if (!IsClear(point, routePoints, stationPoints, 0.62f, 2.15f)) continue;
            float coverNoise = Mathf.PerlinNoise((point.x + 71f) * 0.11f, (point.z + 37f) * 0.11f);
            if (coverNoise < 0.30f) continue;
            GameObject prefab = choices[random.Next(choices.Length)];
            if (prefab == null) continue;
            float scale = Mathf.Lerp(0.62f, 1.35f, (float)random.NextDouble());
            GameObject grass = InstantiatePrefab(prefab, parent, point + Vector3.up * 0.02f, scale, random);
            TintRenderers(grass, new Color(0.34f, 0.48f, 0.22f, 1f));
            created++;
        }
    }

    static void CreateRefinedRocks(
        Transform environment,
        System.Random random,
        List<Vector3> routePoints,
        List<Vector3> stationPoints)
    {
        Transform parent = NewParent(environment, "Refined_Rocks_And_Cliffs");
        Material cliffMaterial = CreateMaterial(
            "M_CliffRock_Refined",
            new Color(0.39f, 0.40f, 0.36f, 1f),
            0.12f);
        GameObject[] rocks =
        {
            LoadPrefab(NatureRoot + "PP_Rock_Moss_Grown_09.prefab"),
            LoadPrefab(NatureRoot + "PP_Rock_Moss_Grown_11.prefab"),
            LoadPrefab(NatureRoot + "PP_Rock_Pile_Forest_Moss_05.prefab"),
            LoadPrefab(NatureRoot + "PP_Rock_Pile_Forest_Moss_10.prefab"),
            LoadPrefab(NatureRoot + "PP_Cemetery_Pebbles_03.prefab"),
            LoadPrefab(NatureRoot + "PP_Cemetery_Pebbles_09.prefab")
        };

        Vector2[] mountainCenters =
        {
            new Vector2(-20f, -1f), new Vector2(-3f, 7f),
            new Vector2(13f, 8f), new Vector2(22f, 6f)
        };
        int index = 0;
        foreach (Vector2 center in mountainCenters)
        {
            for (int i = 0; i < 20; i++)
            {
                float angle = (float)random.NextDouble() * Mathf.PI * 2f;
                float radius = Mathf.Lerp(2f, 7f, (float)random.NextDouble());
                float x = center.x + Mathf.Cos(angle) * radius;
                float z = center.y + Mathf.Sin(angle) * radius;
                Vector3 point = new Vector3(x, LycheeMap15Generator.Height(x, z) - 0.12f, z);
                if (!IsClear(point, routePoints, stationPoints, 1.25f, 2f)) continue;
                GameObject prefab = rocks[index++ % rocks.Length];
                float scale = Mathf.Lerp(0.24f, 0.52f, (float)random.NextDouble());
                GameObject instance = InstantiatePrefab(prefab, parent, point, scale, random);
                ReplaceRendererMaterials(instance, cliffMaterial);
            }
        }

        GameObject[] mountainForms =
        {
            LoadPrefab(NatureRoot + "PP_Forest_Mountain_Moss_01.prefab"),
            LoadPrefab(NatureRoot + "PP_Forest_Mountain_Moss_02.prefab")
        };
        for (int i = 0; i < mountainCenters.Length; i++)
        {
            GameObject mountain = mountainForms[i % mountainForms.Length];
            if (mountain == null) continue;
            Vector2 center = mountainCenters[i];
            Vector3 point = new Vector3(center.x, LycheeMap15Generator.Height(center.x, center.y) - 1.3f, center.y);
            GameObject instance = InstantiatePrefab(mountain, parent, point, 0.075f + i * 0.006f, random);
            ReplaceRendererMaterials(instance, cliffMaterial);
        }

        Transform perimeter = NewParent(parent, "Perimeter_Mountain_Ring");
        List<Vector2> perimeterCenters = new List<Vector2>();
        for (int x = -34; x <= 34; x += 12)
        {
            perimeterCenters.Add(new Vector2(x, 28.5f + Mathf.Sin(x * 0.31f) * 1.0f));
            perimeterCenters.Add(new Vector2(x, -28.5f + Mathf.Cos(x * 0.27f) * 0.9f));
        }
        for (int z = -17; z <= 17; z += 11)
        {
            perimeterCenters.Add(new Vector2(-39.5f + Mathf.Sin(z * 0.29f) * 0.8f, z));
            perimeterCenters.Add(new Vector2(39.5f + Mathf.Cos(z * 0.23f) * 0.8f, z));
        }

        for (int i = 0; i < perimeterCenters.Count; i++)
        {
            Vector2 center = perimeterCenters[i];
            GameObject mountain = mountainForms[i % mountainForms.Length];
            if (mountain == null) continue;
            float y = LycheeMap15Generator.Height(
                Mathf.Clamp(center.x, -40f, 40f),
                Mathf.Clamp(center.y, -30f, 30f));
            Vector3 point = new Vector3(center.x, y - 1.0f, center.y);
            float scale = Mathf.Lerp(0.125f, 0.185f, (float)random.NextDouble());
            GameObject instance = InstantiatePrefab(mountain, perimeter, point, scale, random);
            if (instance != null)
                instance.transform.localScale = new Vector3(scale * 1.12f, scale * 1.46f, scale);
            ReplaceRendererMaterials(instance, cliffMaterial);

            if (i % 2 == 0)
            {
                Vector3 offset = new Vector3(
                    Mathf.Lerp(-3.2f, 3.2f, (float)random.NextDouble()),
                    -0.3f,
                    Mathf.Lerp(-2.2f, 2.2f, (float)random.NextDouble()));
                float secondaryScale = scale * Mathf.Lerp(0.58f, 0.78f, (float)random.NextDouble());
                GameObject secondary = InstantiatePrefab(
                    mountainForms[(i + 1) % mountainForms.Length],
                    perimeter,
                    point + offset,
                    secondaryScale,
                    random);
                if (secondary != null)
                    secondary.transform.localScale = new Vector3(
                        secondaryScale,
                        secondaryScale * 1.32f,
                        secondaryScale * 0.96f);
                ReplaceRendererMaterials(secondary, cliffMaterial);
            }
        }
    }

    static void CreateRiverBanks(Transform environment, Transform routes)
    {
        Transform parent = NewParent(environment, "Refined_River_Banks");
        Material bank = CreateMaterial("M_RiverBank_Refined", new Color(0.34f, 0.27f, 0.16f), 0.05f);
        Material shallow = CreateMaterial("M_ShallowWater_Refined", new Color(0.18f, 0.47f, 0.54f, 0.78f), 0.55f);
        GameObject pebble = LoadPrefab(NatureRoot + "PP_Cemetery_Pebbles_09.prefab");
        System.Random random = new System.Random(806);

        foreach (MapRouteEdge edge in routes.GetComponentsInChildren<MapRouteEdge>(true))
        {
            if (edge.routeType != MapRouteType.Water || edge.pathPoints == null) continue;
            Vector3[] points = edge.pathPoints.Select(point => point.position).ToArray();
            Vector3[] bankPoints = OffsetHeight(points, -0.12f);
            Vector3[] shallowPoints = OffsetHeight(points, -0.035f);
            CreateRibbon(parent, edge.edgeId + "_RiverBank", bankPoints, 4.2f, bank);
            CreateRibbon(parent, edge.edgeId + "_ShallowWater", shallowPoints, 3.2f, shallow);

            if (pebble == null) continue;
            for (int i = 1; i < points.Length - 1; i += 2)
            {
                Vector3 forward = points[i + 1] - points[i - 1];
                forward.y = 0f;
                Vector3 side = Vector3.Cross(Vector3.up, forward.normalized);
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    Vector3 p = points[i] + side * sign * 1.85f;
                    p.y = LycheeMap15Generator.Height(p.x, p.z) + 0.03f;
                    InstantiatePrefab(pebble, parent, p, Mathf.Lerp(0.35f, 0.60f, (float)random.NextDouble()), random);
                }
            }
        }
    }

    static Vector3[] OffsetHeight(Vector3[] points, float offset)
    {
        Vector3[] result = new Vector3[points.Length];
        for (int i = 0; i < points.Length; i++)
            result[i] = points[i] + Vector3.up * offset;
        return result;
    }

    static void RefineWater(Transform routes)
    {
        Material source = AssetDatabase.LoadAssetAtPath<Material>(
            "Assets/Pure Poly/Free Low Poly Nature Pack/Materials/PP_Water.mat");
        Material water = AssetDatabase.LoadAssetAtPath<Material>(MaterialRoot + "M_WaterRoute_Refined.mat");
        if (water == null)
        {
            water = source != null
                ? new Material(source)
                : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(water, MaterialRoot + "M_WaterRoute_Refined.mat");
        }
        Color color = new Color(0.10f, 0.43f, 0.55f, 0.86f);
        water.color = color;
        if (water.HasProperty("_BaseColor")) water.SetColor("_BaseColor", color);
        if (water.HasProperty("_Smoothness")) water.SetFloat("_Smoothness", 0.72f);
        if (water.HasProperty("_Metallic")) water.SetFloat("_Metallic", 0.02f);
        EditorUtility.SetDirty(water);

        foreach (MapRouteEdge edge in routes.GetComponentsInChildren<MapRouteEdge>(true))
        {
            if (edge.routeType != MapRouteType.Water) continue;
            MeshRenderer renderer = edge.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.sharedMaterial = water;
        }
    }

    static void RefineLighting(GameObject root)
    {
        Light sun = root.GetComponentsInChildren<Light>(true).FirstOrDefault(light => light.type == LightType.Directional);
        if (sun != null)
        {
            sun.color = new Color(1f, 0.93f, 0.82f);
            sun.intensity = 1.52f;
            sun.shadowStrength = 0.66f;
            sun.shadowBias = 0.045f;
        }
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogDensity = 0.0024f;
        RenderSettings.fogColor = new Color(0.66f, 0.70f, 0.67f);
        RenderSettings.ambientIntensity = 1.05f;

        Transform cameraLayer = root.transform.Find("06_Camera_And_Lighting");
        if (cameraLayer != null)
        {
            Transform old = cameraLayer.Find("Global_PostProcessing");
            if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);

            GameObject volumeObject = new GameObject("Global_PostProcessing");
            volumeObject.transform.SetParent(cameraLayer, false);
            Volume volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 10f;

            string profilePath = "Assets/LycheeMap15/Materials/LycheeMap_PostProcess.asset";
            VolumeProfile existing = AssetDatabase.LoadAssetAtPath<VolumeProfile>(profilePath);
            if (existing != null) AssetDatabase.DeleteAsset(profilePath);
            VolumeProfile profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, profilePath);
            volume.profile = profile;

            Tonemapping tonemapping = profile.Add<Tonemapping>();
            tonemapping.mode.Override(TonemappingMode.ACES);

            ColorAdjustments color = profile.Add<ColorAdjustments>();
            color.postExposure.Override(0.18f);
            color.contrast.Override(10f);
            color.saturation.Override(-4f);
            color.colorFilter.Override(new Color(1f, 0.97f, 0.90f));

            Vignette vignette = profile.Add<Vignette>();
            vignette.color.Override(new Color(0.06f, 0.045f, 0.03f));
            vignette.intensity.Override(0.09f);
            vignette.smoothness.Override(0.58f);
        }
    }

    static void GenerateGroundTextures()
    {
        CreateProceduralTexture("T_Grassland", (x, y) =>
        {
            float broad = Mathf.PerlinNoise(x * 0.018f, y * 0.018f);
            float fine = Mathf.PerlinNoise(x * 0.12f + 31f, y * 0.12f + 17f);
            float fiber = Mathf.Abs(Mathf.Sin((x * 0.52f + y * 0.19f) + fine * 4f));
            Color dark = new Color(0.17f, 0.27f, 0.10f);
            Color light = new Color(0.35f, 0.46f, 0.19f);
            Color color = Color.Lerp(dark, light, broad * 0.64f + fine * 0.36f);
            return Color.Lerp(color, new Color(0.42f, 0.52f, 0.24f), fiber * 0.07f);
        });

        CreateProceduralTexture("T_SandySoil", (x, y) =>
        {
            float broad = Mathf.PerlinNoise(x * 0.025f + 41f, y * 0.025f + 13f);
            float grain = Mathf.PerlinNoise(x * 0.29f, y * 0.29f);
            Color a = new Color(0.32f, 0.24f, 0.14f);
            Color b = new Color(0.56f, 0.43f, 0.26f);
            return Color.Lerp(a, b, broad * 0.7f + grain * 0.3f);
        });

        CreateProceduralTexture("T_WeatheredRock", (x, y) =>
        {
            float veins = Mathf.PerlinNoise(x * 0.055f + 9f, y * 0.055f + 67f);
            float grain = Mathf.PerlinNoise(x * 0.31f, y * 0.31f);
            Color a = new Color(0.28f, 0.29f, 0.25f);
            Color b = new Color(0.52f, 0.51f, 0.43f);
            return Color.Lerp(a, b, veins * 0.78f + grain * 0.22f);
        });
    }

    static void PrepareTreeMaterials()
    {
        string[] guids = AssetDatabase.FindAssets(
            "t:Material",
            new[] { "Assets/Tree_Packs/URP_Tree_Pack/Models" });
        foreach (string guid in guids)
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
            if (material == null) continue;
            bool leaf = material.name.IndexOf("Leaf", StringComparison.OrdinalIgnoreCase) >= 0;
            if (leaf)
            {
                if (material.HasProperty("_BaseColor"))
                    material.SetColor("_BaseColor", new Color(0.42f, 0.64f, 0.24f, 1f));
                if (material.HasProperty("_Cutoff")) material.SetFloat("_Cutoff", 0.27f);
                if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
                material.doubleSidedGI = true;
                material.EnableKeyword("_ALPHATEST_ON");
            }
            else
            {
                if (material.HasProperty("_BaseColor"))
                    material.SetColor("_BaseColor", new Color(0.82f, 0.76f, 0.66f, 1f));
            }
            EditorUtility.SetDirty(material);
        }
    }

    static void CreateProceduralTexture(string name, Func<float, float, Color> sampler)
    {
        const int size = 512;
        string path = MaterialRoot + name + ".asset";
        Texture2D existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (existing != null) AssetDatabase.DeleteAsset(path);

        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, true, false)
        {
            name = name,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 6
        };
        Color[] pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                pixels[y * size + x] = sampler(x, y);
        texture.SetPixels(pixels);
        texture.Apply(true, false);
        AssetDatabase.CreateAsset(texture, path);
    }

    static Transform NewParent(Transform parent, string name)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.transform;
    }

    static GameObject LoadPrefab(string path)
    {
        return AssetDatabase.LoadAssetAtPath<GameObject>(path);
    }

    static GameObject InstantiatePrefab(GameObject prefab, Transform parent, Vector3 position, float scale, System.Random random)
    {
        GameObject instance = PrefabUtility.InstantiatePrefab(prefab, parent) as GameObject;
        if (instance == null) return null;
        instance.name = prefab.name;
        instance.transform.position = position;
        instance.transform.rotation = Quaternion.Euler(0f, (float)random.NextDouble() * 360f, 0f);
        instance.transform.localScale = Vector3.one * scale;
        foreach (Collider collider in instance.GetComponentsInChildren<Collider>(true))
            collider.enabled = false;
        return instance;
    }

    static void TintRenderers(GameObject instance, Color tint)
    {
        if (instance == null) return;
        foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
        {
            MaterialPropertyBlock properties = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(properties);
            properties.SetColor("_BaseColor", tint);
            properties.SetColor("_Color", tint);
            renderer.SetPropertyBlock(properties);
        }
    }

    static void ReplaceRendererMaterials(GameObject instance, Material material)
    {
        if (instance == null || material == null) return;
        foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
        {
            Material[] replacements = new Material[renderer.sharedMaterials.Length];
            for (int i = 0; i < replacements.Length; i++)
                replacements[i] = material;
            renderer.sharedMaterials = replacements;
        }
    }

    static Material CreateMaterial(string name, Color color, float smoothness)
    {
        string path = MaterialRoot + name + ".mat";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(material, path);
        }
        material.color = color;
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
        EditorUtility.SetDirty(material);
        return material;
    }

    static void CreateRibbon(Transform parent, string name, Vector3[] points, float width, Material material)
    {
        UnityEngine.Mesh mesh = CreateRibbonMesh(points, width, name);
        string path = MeshRoot + name + ".asset";
        UnityEngine.Mesh existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(path);
        if (existing != null) AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(mesh, path);

        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
    }

    static UnityEngine.Mesh CreateRibbonMesh(Vector3[] points, float width, string name)
    {
        Vector3[] vertices = new Vector3[points.Length * 2];
        Vector2[] uv = new Vector2[vertices.Length];
        int[] triangles = new int[(points.Length - 1) * 6];
        float distance = 0f;
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 forward = i == points.Length - 1 ? points[i] - points[i - 1] : points[i + 1] - points[i];
            forward.y = 0f;
            Vector3 side = Vector3.Cross(Vector3.up, forward.normalized);
            if (i > 0) distance += Vector3.Distance(points[i - 1], points[i]);
            vertices[i * 2] = points[i] - side * width * 0.5f;
            vertices[i * 2 + 1] = points[i] + side * width * 0.5f;
            uv[i * 2] = new Vector2(0f, distance * 0.12f);
            uv[i * 2 + 1] = new Vector2(1f, distance * 0.12f);
        }
        int ti = 0;
        for (int i = 0; i < points.Length - 1; i++)
        {
            int v = i * 2;
            triangles[ti++] = v;
            triangles[ti++] = v + 2;
            triangles[ti++] = v + 1;
            triangles[ti++] = v + 1;
            triangles[ti++] = v + 2;
            triangles[ti++] = v + 3;
        }
        UnityEngine.Mesh mesh = new UnityEngine.Mesh { name = name };
        mesh.vertices = vertices;
        mesh.uv = uv;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
