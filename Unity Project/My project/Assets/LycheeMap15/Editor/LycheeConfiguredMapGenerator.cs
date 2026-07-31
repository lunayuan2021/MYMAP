using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

public static class LycheeConfiguredMapGenerator
{
    const string ConfigFolder = "Assets/LycheeMap15/Configs/Maps";
    const string SceneFolder = "Assets/Scenes/GeneratedMaps";
    const string MeshFolder = "Assets/LycheeMap15/GeneratedMeshes";
    const string MaterialFolder = "Assets/LycheeMap15/Materials";
    const string NatureRoot = "Assets/Pure Poly/Free Low Poly Nature Pack/Prefabs/";
    const string TreeRoot = "Assets/Tree_Packs/URP_Tree_Pack/Prefabs/";

    sealed class BuildContext
    {
        public LycheeUiMapConfig config;
        public int[] terrain;
        public int[] objects;
        public int[] weather;
        public int seed;
        public string assetPrefix;
        public readonly Dictionary<string, Vector3> nodePositions = new Dictionary<string, Vector3>();
        public readonly Dictionary<string, LycheeEdgeInfo> edges = new Dictionary<string, LycheeEdgeInfo>();
        public readonly List<Vector3> protectedRoutePoints = new List<Vector3>();
        public readonly List<Vector3> mountainGuidePoints = new List<Vector3>();
        public readonly List<Vector3> waterGuidePoints = new List<Vector3>();
    }

    [MenuItem("Tools/Lychee Map/Config Maps/Build All JSON Maps")]
    public static void BuildAll()
    {
        EnsureFolders();
        string[] guids = AssetDatabase.FindAssets("t:TextAsset", new[] { ConfigFolder });
        string[] paths = guids
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path)
            .ToArray();

        if (paths.Length == 0)
            throw new InvalidOperationException("No map JSON files found in " + ConfigFolder);

        foreach (string path in paths)
            BuildFromJson(path);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("Built " + paths.Length + " configured Lychee map scene(s).");
    }

    [MenuItem("Tools/Lychee Map/Config Maps/Build Selected JSON Map")]
    public static void BuildSelected()
    {
        TextAsset selected = Selection.activeObject as TextAsset;
        if (selected == null)
            throw new InvalidOperationException("Select a map JSON TextAsset in the Project window first.");
        BuildFromJson(AssetDatabase.GetAssetPath(selected));
    }

    static void BuildFromJson(string jsonPath)
    {
        TextAsset source = AssetDatabase.LoadAssetAtPath<TextAsset>(jsonPath);
        if (source == null)
            throw new InvalidOperationException("Unable to load map config: " + jsonPath);

        LycheeUiMapConfig config = JsonUtility.FromJson<LycheeUiMapConfig>(source.text);
        Validate(config, jsonPath);

        BuildContext context = new BuildContext
        {
            config = config,
            terrain = ParseGrid(config.layers.terrainData, config.grid.width, config.grid.height, "terrainData"),
            objects = ParseGrid(config.layers.objectData, config.grid.width, config.grid.height, "objectData"),
            weather = ParseGrid(config.layers.weatherRegionData, config.grid.width, config.grid.height, "weatherRegionData"),
            seed = StableHash(config.mapId),
            assetPrefix = Sanitize(config.mapId)
        };
        foreach (LycheeEdgeInfo edge in config.edges)
            context.edges[edge.edgeId] = edge;

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        scene.name = context.assetPrefix;

        GameObject root = new GameObject("LYCHEE_CONFIG_MAP_" + context.assetPrefix);
        GameObject terrainLayer = Child(root, "01_Terrain_Base");
        GameObject routeLayer = Child(root, "02_Routes");
        GameObject nodeLayer = Child(root, "03_Node_Anchors");
        GameObject decorationLayer = Child(root, "04_Natural_Decoration");
        GameObject dataLayer = Child(root, "05_Weather_And_Event_Anchors");
        GameObject cameraLayer = Child(root, "06_Camera_And_Lighting");

        Material terrainMaterial = LoadOrCreateTerrainMaterial();
        CreateTerrain(context, terrainLayer, terrainMaterial);
        CreateNodeAnchors(context, nodeLayer);
        CreateRoutes(context, routeLayer);
        CreateWeatherAnchors(context, dataLayer);
        CreateNaturalDecoration(context, decorationLayer);
        CreateCameraAndLighting(context, cameraLayer);

        string scenePath = SceneFolder + "/" + context.assetPrefix + ".unity";
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, scenePath);
        Selection.activeGameObject = root;
        Debug.Log(
            "Configured map built: " + config.mapId +
            " | nodes=" + config.nodes.Count +
            " edges=" + config.edges.Count +
            " scene=" + scenePath);
    }

    static void Validate(LycheeUiMapConfig config, string path)
    {
        if (config == null || config.grid == null || config.layers == null)
            throw new InvalidOperationException("Invalid map config structure: " + path);
        if (string.IsNullOrWhiteSpace(config.mapId))
            throw new InvalidOperationException("mapId is required: " + path);
        if (config.grid.width <= 0 || config.grid.height <= 0)
            throw new InvalidOperationException("Grid size must be positive: " + path);
        if (config.nodes == null || config.edges == null || config.routePaths == null)
            throw new InvalidOperationException("nodes, edges and routePaths are required: " + path);

        HashSet<string> nodeIds = new HashSet<string>();
        foreach (LycheeNodeInfo node in config.nodes)
        {
            if (!nodeIds.Add(node.nodeId))
                throw new InvalidOperationException("Duplicate node: " + node.nodeId);
            if (node.x < 0 || node.x >= config.grid.width || node.y < 0 || node.y >= config.grid.height)
                throw new InvalidOperationException("Node outside grid: " + node.nodeId);
        }
        foreach (LycheeEdgeInfo edge in config.edges)
        {
            if (!nodeIds.Contains(edge.fromNodeId) || !nodeIds.Contains(edge.toNodeId))
                throw new InvalidOperationException("Edge endpoint missing: " + edge.edgeId);
        }
    }

    static int[] ParseGrid(LycheeMapLayer layer, int width, int height, string layerName)
    {
        if (layer == null || string.IsNullOrWhiteSpace(layer.data))
            return new int[width * height];
        string[] values = layer.data.Split(',');
        if (values.Length != width * height)
            throw new InvalidOperationException(
                layerName + " cell count mismatch: " + values.Length + " != " + (width * height));
        int[] result = new int[values.Length];
        for (int i = 0; i < values.Length; i++)
            result[i] = int.Parse(values[i]);
        return result;
    }

    static void CreateTerrain(BuildContext context, GameObject parent, Material material)
    {
        int width = context.config.grid.width;
        int height = context.config.grid.height;
        Vector3[] vertices = new Vector3[(width + 1) * (height + 1)];
        Vector2[] uv = new Vector2[vertices.Length];
        int[] triangles = new int[width * height * 6];

        for (int z = 0; z <= height; z++)
        {
            for (int x = 0; x <= width; x++)
            {
                int index = z * (width + 1) + x;
                float wx = GridWorldX(context, x);
                float wz = GridWorldZ(context, z);
                vertices[index] = new Vector3(wx, RawTerrainHeight(context, wx, wz), wz);
                uv[index] = new Vector2(x / (float)width, z / (float)height);
            }
        }

        int ti = 0;
        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = z * (width + 1) + x;
                // Grid y maps toward negative world z, so use clockwise grid winding
                // to keep the terrain normals facing upward in Unity.
                triangles[ti++] = i;
                triangles[ti++] = i + 1;
                triangles[ti++] = i + width + 1;
                triangles[ti++] = i + 1;
                triangles[ti++] = i + width + 2;
                triangles[ti++] = i + width + 1;
            }
        }

        Mesh mesh = new Mesh { name = context.assetPrefix + "_Terrain" };
        mesh.indexFormat = IndexFormat.UInt32;
        mesh.vertices = vertices;
        mesh.uv = uv;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh = SaveMesh(mesh, MeshFolder + "/" + context.assetPrefix + "_Terrain.asset");

        GameObject terrain = Child(parent, "Continuous_Terrain");
        terrain.AddComponent<MeshFilter>().sharedMesh = mesh;
        terrain.AddComponent<MeshRenderer>().sharedMaterial = material;
        terrain.AddComponent<MeshCollider>().sharedMesh = mesh;
        terrain.isStatic = true;

        MapElevationData elevation = terrain.AddComponent<MapElevationData>();
        elevation.gridWidth = width;
        elevation.gridHeight = height;
        elevation.worldOrigin = new Vector2(GridWorldX(context, 0), GridWorldZ(context, height));
        elevation.cellSize = context.config.grid.tileSize;
        elevation.heightSamples = vertices.Select(vertex => vertex.y).ToArray();
    }

    static void CreateNodeAnchors(BuildContext context, GameObject parent)
    {
        foreach (LycheeNodeInfo node in context.config.nodes)
        {
            Vector3 position = GridToWorld(context, node.x, node.y, 0.018f);
            context.nodePositions[node.nodeId] = position;

            GameObject anchor = Child(parent, node.nodeId + "_ANCHOR_" + node.name);
            anchor.transform.position = position;
            MapStationAnchor station = anchor.AddComponent<MapStationAnchor>();
            station.stationId = node.nodeId;
            station.displayName = node.name;
            station.stationType = ParseStationType(node.type);
            station.gridCoordinate = new Vector2Int(node.x, node.y);
            station.safeZone = node.type == "FINISH";
            station.modelSlot = Child(anchor, "ModelSlot_REPLACE_WITH_PREFAB").transform;
            station.vehicleStopRed = Child(anchor, "VehicleStop_RED").transform;
            station.vehicleStopBlue = Child(anchor, "VehicleStop_BLUE").transform;
            station.eventAnchor = Child(anchor, "EventFXSlot").transform;
            station.obstacleAnchor = Child(anchor, "NodeObstacleSlot").transform;
            station.vehicleStopRed.localPosition = new Vector3(-0.65f, 0f, 0f);
            station.vehicleStopBlue.localPosition = new Vector3(0.65f, 0f, 0f);
            CreateNodePlaceholder(anchor, node);
            CreateNodeGroundBlend(context, anchor, node);
        }
    }

    static void CreateNodeGroundBlend(BuildContext context, GameObject anchor, LycheeNodeInfo node)
    {
        const int segments = 28;
        const int rings = 3;
        float radius = node.type == "START" || node.type == "FINISH" ? 3.25f : 2.75f;
        Vector3[] vertices = new Vector3[1 + segments * rings];
        Vector2[] uv = new Vector2[vertices.Length];
        List<int> triangles = new List<int>();
        Vector3 center = anchor.transform.position;
        vertices[0] = new Vector3(0f, Height(context, center.x, center.z) - center.y + 0.014f, 0f);
        uv[0] = new Vector2(0.5f, 0.5f);

        for (int ring = 1; ring <= rings; ring++)
        {
            float ringRadius = radius * ring / rings;
            for (int segment = 0; segment < segments; segment++)
            {
                float angle = segment / (float)segments * Mathf.PI * 2f;
                float x = Mathf.Cos(angle) * ringRadius;
                float z = Mathf.Sin(angle) * ringRadius;
                int index = 1 + (ring - 1) * segments + segment;
                float worldX = center.x + x;
                float worldZ = center.z + z;
                vertices[index] = new Vector3(
                    x,
                    Height(context, worldX, worldZ) - center.y + 0.014f,
                    z);
                uv[index] = new Vector2(x / radius * 0.5f + 0.5f, z / radius * 0.5f + 0.5f);
            }
        }

        for (int segment = 0; segment < segments; segment++)
        {
            int next = (segment + 1) % segments;
            triangles.Add(0);
            triangles.Add(1 + next);
            triangles.Add(1 + segment);
        }
        for (int ring = 1; ring < rings; ring++)
        {
            int innerStart = 1 + (ring - 1) * segments;
            int outerStart = 1 + ring * segments;
            for (int segment = 0; segment < segments; segment++)
            {
                int next = (segment + 1) % segments;
                int a = innerStart + segment;
                int b = innerStart + next;
                int c = outerStart + segment;
                int d = outerStart + next;
                triangles.Add(a);
                triangles.Add(b);
                triangles.Add(c);
                triangles.Add(c);
                triangles.Add(b);
                triangles.Add(d);
            }
        }

        Mesh mesh = new Mesh { name = context.assetPrefix + "_" + node.nodeId + "_GroundBlend" };
        mesh.vertices = vertices;
        mesh.uv = uv;
        mesh.triangles = triangles.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh = SaveMesh(
            mesh,
            MeshFolder + "/" + context.assetPrefix + "_" + node.nodeId + "_GroundBlend.asset");

        GameObject blend = Child(anchor, "Node_GroundBlend_" + node.nodeId);
        blend.AddComponent<MeshFilter>().sharedMesh = mesh;
        blend.AddComponent<MeshRenderer>().sharedMaterial = LoadOrCreatePbrMaterial(
            "M_Config_NodeGroundBlend",
            "Ground003",
            new Color(0.80f, 0.70f, 0.53f),
            0.10f,
            new Vector2(0.48f, 0.48f));
    }

    static void CreateNodePlaceholder(GameObject anchor, LycheeNodeInfo node)
    {
        Material markerMaterial = LoadOrCreateMaterial(
            "M_NodePlaceholder_" + node.type,
            NodeTypeColor(node.type),
            0.22f);
        EnableEmission(markerMaterial, NodeTypeColor(node.type) * 0.22f);

        GameObject plinth = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        plinth.name = "Placeholder_Platform_" + node.nodeId;
        plinth.transform.SetParent(anchor.transform, false);
        plinth.transform.localPosition = new Vector3(0f, 0.12f, 0f);
        plinth.transform.localScale = new Vector3(1.12f, 0.16f, 1.12f);
        plinth.GetComponent<MeshRenderer>().sharedMaterial = markerMaterial;
        UnityEngine.Object.DestroyImmediate(plinth.GetComponent<Collider>());

        GameObject beacon = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        beacon.name = "Placeholder_Beacon";
        beacon.transform.SetParent(anchor.transform, false);
        beacon.transform.localPosition = new Vector3(0f, 0.95f, 0f);
        beacon.transform.localScale = new Vector3(0.07f, 0.82f, 0.07f);
        beacon.GetComponent<MeshRenderer>().sharedMaterial = markerMaterial;
        UnityEngine.Object.DestroyImmediate(beacon.GetComponent<Collider>());

        GameObject labelObject = new GameObject("Placeholder_Label_" + node.nodeId);
        labelObject.transform.SetParent(anchor.transform, false);
        labelObject.transform.localPosition = new Vector3(0f, 2.05f, 0f);
        labelObject.transform.rotation = Quaternion.Euler(52f, -29f, 0f);
        TextMesh label = labelObject.AddComponent<TextMesh>();
        label.text = node.nodeId + "\n" + node.type;
        label.anchor = TextAnchor.MiddleCenter;
        label.alignment = TextAlignment.Center;
        label.characterSize = 0.18f;
        label.fontSize = 42;
        label.color = Color.white;
    }

    static void CreateRoutes(BuildContext context, GameObject parent)
    {
        GameObject roadRoot = Child(parent, "OfficialRoad");
        GameObject waterRoot = Child(parent, "WaterRoute_And_River");
        GameObject mountainRoot = Child(parent, "MountainRoute");
        GameObject branchRoot = Child(parent, "BranchRoute");
        MapRouteLayerController controller = parent.AddComponent<MapRouteLayerController>();
        controller.officialRoad = roadRoot.transform;
        controller.waterRoute = waterRoot.transform;
        controller.mountainRoute = mountainRoot.transform;
        controller.branchRoute = branchRoot.transform;

        Material road = LoadOrCreatePbrMaterial(
            "M_Config_OfficialRoad",
            "Ground003",
            new Color(1.08f, 0.92f, 0.72f),
            0.12f,
            new Vector2(0.34f, 1.2f));
        Material water = LoadOrCreateWaterMaterial();
        Material riverBank = LoadOrCreatePbrMaterial(
            "M_RiverBank_Refined",
            "Ground004",
            new Color(0.72f, 0.66f, 0.55f),
            0.18f,
            new Vector2(0.42f, 1.1f));
        Material mountain = LoadOrCreatePbrMaterial(
            "M_Config_MountainRoute",
            "GroundPatchCracked01",
            new Color(0.74f, 0.68f, 0.54f),
            0.16f,
            new Vector2(0.28f, 1.05f));
        Material branch = LoadOrCreatePbrMaterial(
            "M_Config_BranchRoute",
            "Ground001",
            new Color(0.92f, 0.82f, 0.62f),
            0.10f,
            new Vector2(0.38f, 1.15f));
        Material roadBorder = LoadOrCreatePbrMaterial(
            "M_Config_OfficialRoad_Border",
            "Ground004",
            new Color(0.48f, 0.37f, 0.25f),
            0.08f,
            new Vector2(0.38f, 1.1f));
        Material waterGlow = LoadOrCreateMaterial("M_Config_WaterRoute_Glow", new Color(0.18f, 0.78f, 1f), 0.62f);
        Material mountainBorder = LoadOrCreateMaterial("M_Config_MountainRoute_Border", new Color(0.12f, 0.15f, 0.12f), 0.01f);
        Material branchBorder = LoadOrCreateMaterial("M_Config_BranchRoute_Border", new Color(0.32f, 0.24f, 0.08f), 0.01f);
        EnableEmission(waterGlow, new Color(0.05f, 0.34f, 0.56f));

        foreach (LycheeRoutePath routePath in context.config.routePaths)
        {
            if (!context.edges.TryGetValue(routePath.edgeId, out LycheeEdgeInfo edge))
                continue;
            if (routePath.points == null || routePath.points.Count < 2)
                continue;

            MapRouteType routeType = ParseRouteType(edge.routeType);
            GameObject routeParent = roadRoot;
            Material routeMaterial = road;
            float width = 1.65f;
            float lift = 0.026f;
            if (routeType == MapRouteType.Water)
            {
                routeParent = waterRoot;
                routeMaterial = water;
                width = 3.15f;
                lift = 0.038f;
            }
            else if (routeType == MapRouteType.Mountain)
            {
                routeParent = mountainRoot;
                routeMaterial = mountain;
                width = 1.28f;
                lift = 0.032f;
            }
            else if (routeType == MapRouteType.Branch)
            {
                routeParent = branchRoot;
                routeMaterial = branch;
                width = 0.94f;
                lift = 0.028f;
            }

            Vector3[] points = BuildTerrainFollowingRoute(context, routePath.points, lift);
            context.protectedRoutePoints.AddRange(SamplePolyline(points, 0.85f));
            if (routeType == MapRouteType.Mountain)
                context.mountainGuidePoints.AddRange(SamplePolyline(points, 2.4f));
            if (routeType == MapRouteType.Water)
                context.waterGuidePoints.AddRange(SamplePolyline(points, 1.6f));

            GameObject edgeObject = Child(routeParent, edge.edgeId + "_" + edge.fromNodeId + "-" + edge.toNodeId);
            MapRouteEdge data = edgeObject.AddComponent<MapRouteEdge>();
            data.edgeId = edge.edgeId;
            data.fromStationId = edge.fromNodeId;
            data.toStationId = edge.toNodeId;
            data.routeType = routeType;
            data.logicalDistance = edge.distance;
            data.bidirectional = edge.bidirectional;
            data.pathPoints = points.Select((point, index) =>
            {
                GameObject marker = Child(edgeObject, "PathPoint_" + index.ToString("00"));
                marker.transform.position = point;
                return marker.transform;
            }).ToArray();
            data.obstacleSlot = Child(edgeObject, "EdgeObstacleSlot").transform;
            data.obstacleSlot.position = points[points.Length / 2];

            if (routeType == MapRouteType.Water)
            {
                CreateRibbonAsset(context, edgeObject, points.Select(p => p - Vector3.up * 0.11f).ToArray(), 4.45f, riverBank, edge.edgeId + "_RiverBank");
                CreateRibbonAsset(context, edgeObject, points.Select(p => p + Vector3.up * 0.012f).ToArray(), width * 0.30f, waterGlow, edge.edgeId + "_WaterHighlight");
            }
            else if (routeType == MapRouteType.Mountain)
            {
                CreateRibbonAsset(context, edgeObject, points.Select(p => p - Vector3.up * 0.025f).ToArray(), width + 0.46f, mountainBorder, edge.edgeId + "_MountainBorder");
            }
            else if (routeType == MapRouteType.Branch)
            {
                CreateRibbonAsset(context, edgeObject, points.Select(p => p - Vector3.up * 0.025f).ToArray(), width + 0.34f, branchBorder, edge.edgeId + "_BranchBorder");
            }
            else
            {
                CreateRibbonAsset(context, edgeObject, points.Select(p => p - Vector3.up * 0.025f).ToArray(), width + 0.52f, roadBorder, edge.edgeId + "_RoadBorder");
            }
            CreateRibbonAsset(context, edgeObject, points, width, routeMaterial, edge.edgeId + "_Route");

            if (routeType == MapRouteType.OfficialRoad)
                CreateWheelRuts(context, edgeObject, points, width, routeMaterial, edge.edgeId);
            if (routeType == MapRouteType.Water)
                Child(edgeObject, "BridgeOrDockSlot_REPLACE_WITH_PREFAB").transform.position = points[points.Length / 2];
            if (routeType == MapRouteType.Mountain || routeType == MapRouteType.Branch)
                Child(edgeObject, "PlankRoadOrPassSlot_REPLACE_WITH_PREFAB").transform.position = points[points.Length / 2];
        }
    }

    static void CreateWeatherAnchors(BuildContext context, GameObject parent)
    {
        GameObject weatherRoot = Child(parent, "Weather_Grid_Regions");
        int width = context.config.grid.width;
        int height = context.config.grid.height;
        for (int code = 1; code <= 3; code++)
        {
            List<Vector2Int> cells = new List<Vector2Int>();
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (context.weather[y * width + x] == code)
                        cells.Add(new Vector2Int(x, y));
            if (cells.Count == 0) continue;

            Vector2 center = new Vector2(
                (float)cells.Average(c => c.x),
                (float)cells.Average(c => c.y));
            GameObject anchor = Child(weatherRoot, code == 1 ? "HOT" : code == 2 ? "HEAVY_RAIN" : "MOUNTAIN_FOG");
            anchor.transform.position = GridToWorld(context, center.x, center.y, 1.2f);
            MapEnvironmentAnchor data = anchor.AddComponent<MapEnvironmentAnchor>();
            data.anchorType = EnvironmentAnchorType.Weather;
            data.regionId = anchor.name;
            data.radius = Mathf.Sqrt(cells.Count / Mathf.PI) * context.config.grid.tileSize;
        }
    }

    static void CreateNaturalDecoration(BuildContext context, GameObject parent)
    {
        System.Random random = new System.Random(context.seed);
        GameObject[] trees = new[]
        {
            LoadPrefab(TreeRoot + "URP_Tree_1.prefab"),
            LoadPrefab(TreeRoot + "URP_Tree_2.prefab"),
            LoadPrefab(TreeRoot + "URP_Tree_3.prefab")
        }.Where(prefab => prefab != null).ToArray();
        GameObject[] rocks = new[]
        {
            LoadPrefab(NatureRoot + "PP_Rock_Moss_Grown_09.prefab"),
            LoadPrefab(NatureRoot + "PP_Rock_Moss_Grown_11.prefab")
        }.Where(prefab => prefab != null).ToArray();
        GameObject[] grasses = new[]
        {
            LoadPrefab(NatureRoot + "PP_Grass_11.prefab"),
            LoadPrefab(NatureRoot + "PP_Grass_15.prefab")
        }.Where(prefab => prefab != null).ToArray();
        GameObject[] mountains = new[]
        {
            LoadPrefab(NatureRoot + "PP_Forest_Mountain_Moss_01.prefab"),
            LoadPrefab(NatureRoot + "PP_Forest_Mountain_Moss_02.prefab")
        }.Where(prefab => prefab != null).ToArray();

        Transform treeRoot = Child(parent, "Tree_Clusters").transform;
        Transform grassRoot = Child(parent, "Grass_And_Groundcover").transform;
        Transform rockRoot = Child(parent, "Rock_Clusters").transform;
        Transform mountainRoot = Child(parent, "Irregular_Mountain_Groups").transform;

        List<Vector3> nodePoints = context.nodePositions.Values.ToList();
        List<Vector3> treeCenters = PickClusterCenters(context, random, 46, 3.3f, nodePoints);
        foreach (Vector3 center in treeCenters)
        {
            int count = random.Next(6, 13);
            float radius = RandomRange(random, 1.8f, 5.2f);
            for (int i = 0; i < count; i++)
            {
                Vector3 point = Around(context, random, center, radius);
                if (!IsClear(point, context.protectedRoutePoints, nodePoints, 2.35f, 2.5f)) continue;
                if (trees.Length > 0)
                    Instantiate(trees[random.Next(trees.Length)], treeRoot, point, RandomRange(random, 0.30f, 0.54f), random);
            }
        }

        if (context.nodePositions.TryGetValue("S01", out Vector3 orchardCenter))
        {
            Transform orchardRoot = Child(parent, "Lingnan_Orchard_Tree_Groves").transform;
            for (int i = 0, created = 0; i < 240 && created < 82; i++)
            {
                float angle = RandomRange(random, 0f, Mathf.PI * 2f);
                float radius = Mathf.Sqrt(RandomRange(random, 0.12f, 1f)) * 10.5f;
                Vector3 point = orchardCenter + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                point.y = Height(context, point.x, point.z);
                if (!IsClear(point, context.protectedRoutePoints, new List<Vector3>(), 2.15f, 0f)) continue;
                if (trees.Length > 0)
                    Instantiate(trees[random.Next(trees.Length)], orchardRoot, point, RandomRange(random, 0.36f, 0.62f), random);
                created++;
            }
        }

        for (int i = 0, created = 0; i < 12000 && created < 2100; i++)
        {
            Vector3 point = RandomMapPoint(context, random, 1.2f);
            if (!IsClear(point, context.protectedRoutePoints, nodePoints, 0.58f, 1.8f)) continue;
            if (Mathf.PerlinNoise((point.x + 51f) * 0.13f, (point.z + 29f) * 0.13f) < 0.27f) continue;
            if (grasses.Length > 0)
                Instantiate(grasses[random.Next(grasses.Length)], grassRoot, point + Vector3.up * 0.02f, RandomRange(random, 0.58f, 1.20f), random);
            created++;
        }

        List<Vector3> rockCenters = PickClusterCenters(context, random, 24, 4.4f, nodePoints);
        foreach (Vector3 center in rockCenters)
        {
            for (int i = 0; i < random.Next(4, 9); i++)
            {
                Vector3 point = Around(context, random, center, RandomRange(random, 1.2f, 3.8f));
                if (!IsClear(point, context.protectedRoutePoints, nodePoints, 1.1f, 2.1f)) continue;
                if (rocks.Length > 0)
                    Instantiate(rocks[random.Next(rocks.Length)], rockRoot, point, RandomRange(random, 0.26f, 0.52f), random);
            }
        }

        // Mountain groups follow mountain routes and a few uneven edge areas; no perimeter ring.
        List<Vector3> mountainCenters = new List<Vector3>();
        for (int i = 1; i < context.mountainGuidePoints.Count; i += 4)
            mountainCenters.Add(context.mountainGuidePoints[i]);
        mountainCenters.AddRange(PickEdgeMountainCenters(context, random, 7, nodePoints));
        foreach (Vector3 center in mountainCenters)
        {
            int count = random.Next(2, 5);
            for (int i = 0; i < count; i++)
            {
                Vector3 point = Around(context, random, center, RandomRange(random, 0.8f, 4.6f));
                if (!IsClear(point, context.protectedRoutePoints, nodePoints, 1.0f, 2.4f)) continue;
                if (mountains.Length > 0)
                {
                    GameObject mountain = Instantiate(
                        mountains[random.Next(mountains.Length)],
                        mountainRoot,
                        point - Vector3.up * 1.0f,
                        RandomRange(random, 0.075f, 0.145f),
                        random);
                    if (mountain != null)
                        mountain.transform.localScale = Vector3.Scale(
                            mountain.transform.localScale,
                            new Vector3(RandomRange(random, 0.85f, 1.3f), RandomRange(random, 1.05f, 1.55f), RandomRange(random, 0.85f, 1.2f)));
                }
            }
        }
    }

    static List<Vector3> PickClusterCenters(
        BuildContext context,
        System.Random random,
        int count,
        float routeClearance,
        List<Vector3> nodePoints)
    {
        List<Vector3> result = new List<Vector3>();
        for (int attempt = 0; attempt < count * 45 && result.Count < count; attempt++)
        {
            Vector3 point = RandomMapPoint(context, random, 2.5f);
            if (!IsClear(point, context.protectedRoutePoints, nodePoints, routeClearance, 4f)) continue;
            if (result.Any(existing => FlatDistance(existing, point) < 5f)) continue;
            result.Add(point);
        }
        return result;
    }

    static List<Vector3> PickEdgeMountainCenters(
        BuildContext context,
        System.Random random,
        int count,
        List<Vector3> nodePoints)
    {
        List<Vector3> result = new List<Vector3>();
        float halfWidth = context.config.grid.width * context.config.grid.tileSize * 0.5f;
        float halfHeight = context.config.grid.height * context.config.grid.tileSize * 0.5f;
        for (int attempt = 0; attempt < 250 && result.Count < count; attempt++)
        {
            int side = random.Next(4);
            float x = RandomRange(random, -halfWidth + 4f, halfWidth - 4f);
            float z = RandomRange(random, -halfHeight + 4f, halfHeight - 4f);
            if (side == 0) x = RandomRange(random, -halfWidth + 1f, -halfWidth + 9f);
            if (side == 1) x = RandomRange(random, halfWidth - 9f, halfWidth - 1f);
            if (side == 2) z = RandomRange(random, -halfHeight + 1f, -halfHeight + 9f);
            if (side == 3) z = RandomRange(random, halfHeight - 9f, halfHeight - 1f);
            Vector3 point = new Vector3(x, Height(context, x, z), z);
            if (!IsClear(point, context.protectedRoutePoints, nodePoints, 3.2f, 4f)) continue;
            if (result.Any(existing => FlatDistance(existing, point) < 10f)) continue;
            result.Add(point);
        }
        return result;
    }

    static void CreateCameraAndLighting(BuildContext context, GameObject parent)
    {
        float width = context.config.grid.width * context.config.grid.tileSize;
        float height = context.config.grid.height * context.config.grid.tileSize;
        GameObject cameraObject = Child(parent, "MainCamera_2.5D");
        Camera camera = cameraObject.AddComponent<Camera>();
        cameraObject.tag = "MainCamera";
        camera.orthographic = true;
        camera.orthographicSize = Mathf.Max(height * 0.63f, width * 0.43f);
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 400f;
        camera.backgroundColor = new Color(0.61f, 0.69f, 0.72f);
        camera.clearFlags = CameraClearFlags.SolidColor;
        cameraObject.transform.position = new Vector3(width * 0.42f, height * 1.2f, -height * 0.95f);
        cameraObject.transform.rotation = Quaternion.Euler(52f, -29f, 0f);

        GameObject sunObject = Child(parent, "Sun_Warm");
        Light sun = sunObject.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(1f, 0.93f, 0.82f);
        sun.intensity = 1.48f;
        sun.shadows = LightShadows.Soft;
        sun.shadowStrength = 0.66f;
        sunObject.transform.rotation = Quaternion.Euler(48f, -32f, 0f);

        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.63f, 0.68f, 0.68f);
        RenderSettings.ambientEquatorColor = new Color(0.48f, 0.49f, 0.39f);
        RenderSettings.ambientGroundColor = new Color(0.25f, 0.23f, 0.17f);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogDensity = 0.0022f;
        RenderSettings.fogColor = new Color(0.69f, 0.72f, 0.67f);

        GameObject volumeObject = Child(parent, "Global_PostProcessing");
        Volume volume = volumeObject.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 10f;
        string profilePath = MaterialFolder + "/" + context.assetPrefix + "_PostProcess.asset";
        VolumeProfile oldProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(profilePath);
        if (oldProfile != null) AssetDatabase.DeleteAsset(profilePath);
        VolumeProfile profile = ScriptableObject.CreateInstance<VolumeProfile>();
        AssetDatabase.CreateAsset(profile, profilePath);
        volume.profile = profile;

        Tonemapping tonemapping = profile.Add<Tonemapping>();
        tonemapping.mode.Override(TonemappingMode.ACES);
        ColorAdjustments color = profile.Add<ColorAdjustments>();
        color.postExposure.Override(0.14f);
        color.contrast.Override(4f);   // 原 13 过高把暗部压死、单位发黑 → 4（与 Atmosphere v3 一致）
        color.saturation.Override(2f); // 原 5 偏浓 → 2
        color.colorFilter.Override(new Color(1f, 0.99f, 0.96f)); // 几乎纯白，去暖罩
        Bloom bloom = profile.Add<Bloom>();
        bloom.threshold.Override(0.82f);
        bloom.intensity.Override(0.30f);
        bloom.scatter.Override(0.58f);
        Vignette vignette = profile.Add<Vignette>();
        vignette.intensity.Override(0.08f);
        vignette.smoothness.Override(0.62f);
    }

    static float RawTerrainHeight(BuildContext context, float worldX, float worldZ)
    {
        float width = context.config.grid.width * context.config.grid.tileSize;
        float height = context.config.grid.height * context.config.grid.tileSize;
        float gx = worldX / context.config.grid.tileSize + width / (2f * context.config.grid.tileSize);
        float gy = height / (2f * context.config.grid.tileSize) - worldZ / context.config.grid.tileSize;
        int ix = Mathf.Clamp(Mathf.RoundToInt(gx), 0, context.config.grid.width - 1);
        int iy = Mathf.Clamp(Mathf.RoundToInt(gy), 0, context.config.grid.height - 1);
        int terrainCode = context.terrain[iy * context.config.grid.width + ix];

        float broad =
            Mathf.PerlinNoise((worldX + context.seed * 0.001f) * 0.038f, (worldZ + 37f) * 0.038f) * 1.75f;
        float detail =
            Mathf.PerlinNoise((worldX + 91f) * 0.115f, (worldZ + 53f) * 0.115f) * 0.36f;
        float rolling =
            Mathf.Sin(worldX * 0.075f + worldZ * 0.045f) * 0.24f;

        float mountainInfluence = CodeInfluence(context, ix, iy, 3, 8, 4.6f);
        float waterInfluence = CodeInfluence(context, ix, iy, 2, 6, 3.5f);
        float roadInfluence = CodeInfluence(context, ix, iy, 1, 3, 1.8f);
        float branchInfluence = CodeInfluence(context, ix, iy, 4, 4, 2.4f);

        float heightValue = broad + detail + rolling;
        heightValue += mountainInfluence * 4.7f;
        heightValue += branchInfluence * 0.55f;
        heightValue -= waterInfluence * 1.45f;

        // Official roads cross broad, relatively level terrain without erasing
        // the surrounding hills. Water remains the lowest continuous corridor.
        heightValue = Mathf.Lerp(heightValue, 0.72f + detail * 0.22f, roadInfluence * 0.48f);
        if (terrainCode == 6) heightValue += 3.2f;
        if (terrainCode == 7) heightValue -= 1.0f;
        float rawHeight = Mathf.Max(-0.85f, heightValue);

        // Embed station footprints into the terrain so imported buildings do
        // not bridge across local bumps or appear to sit on floating planes.
        foreach (LycheeNodeInfo node in context.config.nodes)
        {
            float nodeX = GridWorldX(context, node.x);
            float nodeZ = GridWorldZ(context, node.y);
            float distance = Vector2.Distance(
                new Vector2(worldX, worldZ),
                new Vector2(nodeX, nodeZ));
            if (distance >= 3.8f) continue;
            float targetHeight = RawTerrainHeightCore(context, nodeX, nodeZ);
            float flatten = 1f - Mathf.SmoothStep(0f, 1f, distance / 3.8f);
            rawHeight = Mathf.Lerp(rawHeight, targetHeight, flatten * 0.94f);
        }
        return rawHeight;
    }

    static float RawTerrainHeightCore(BuildContext context, float worldX, float worldZ)
    {
        float width = context.config.grid.width * context.config.grid.tileSize;
        float height = context.config.grid.height * context.config.grid.tileSize;
        float gx = worldX / context.config.grid.tileSize + width / (2f * context.config.grid.tileSize);
        float gy = height / (2f * context.config.grid.tileSize) - worldZ / context.config.grid.tileSize;
        int ix = Mathf.Clamp(Mathf.RoundToInt(gx), 0, context.config.grid.width - 1);
        int iy = Mathf.Clamp(Mathf.RoundToInt(gy), 0, context.config.grid.height - 1);
        int terrainCode = context.terrain[iy * context.config.grid.width + ix];

        float broad =
            Mathf.PerlinNoise((worldX + context.seed * 0.001f) * 0.038f, (worldZ + 37f) * 0.038f) * 1.75f;
        float detail =
            Mathf.PerlinNoise((worldX + 91f) * 0.115f, (worldZ + 53f) * 0.115f) * 0.36f;
        float rolling =
            Mathf.Sin(worldX * 0.075f + worldZ * 0.045f) * 0.24f;
        float mountainInfluence = CodeInfluence(context, ix, iy, 3, 8, 4.6f);
        float waterInfluence = CodeInfluence(context, ix, iy, 2, 6, 3.5f);
        float roadInfluence = CodeInfluence(context, ix, iy, 1, 3, 1.8f);
        float branchInfluence = CodeInfluence(context, ix, iy, 4, 4, 2.4f);
        float heightValue = broad + detail + rolling;
        heightValue += mountainInfluence * 4.7f;
        heightValue += branchInfluence * 0.55f;
        heightValue -= waterInfluence * 1.45f;
        heightValue = Mathf.Lerp(heightValue, 0.72f + detail * 0.22f, roadInfluence * 0.48f);
        if (terrainCode == 6) heightValue += 3.2f;
        if (terrainCode == 7) heightValue -= 1.0f;
        return Mathf.Max(-0.85f, heightValue);
    }

    static float Height(BuildContext context, float worldX, float worldZ)
    {
        float tileSize = context.config.grid.tileSize;
        float gridX = worldX / tileSize + context.config.grid.width * 0.5f;
        float gridY = context.config.grid.height * 0.5f - worldZ / tileSize;
        int x0 = Mathf.Clamp(Mathf.FloorToInt(gridX), 0, context.config.grid.width);
        int x1 = Mathf.Clamp(x0 + 1, 0, context.config.grid.width);
        int y0 = Mathf.Clamp(Mathf.FloorToInt(gridY), 0, context.config.grid.height);
        int y1 = Mathf.Clamp(y0 + 1, 0, context.config.grid.height);
        float tx = Mathf.Clamp01(gridX - x0);
        float ty = Mathf.Clamp01(gridY - y0);
        float h00 = RawTerrainHeight(context, GridWorldX(context, x0), GridWorldZ(context, y0));
        float h10 = RawTerrainHeight(context, GridWorldX(context, x1), GridWorldZ(context, y0));
        float h01 = RawTerrainHeight(context, GridWorldX(context, x0), GridWorldZ(context, y1));
        float h11 = RawTerrainHeight(context, GridWorldX(context, x1), GridWorldZ(context, y1));
        return Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), ty);
    }

    static float CodeInfluence(
        BuildContext context,
        int centerX,
        int centerY,
        int code,
        int radius,
        float falloff)
    {
        float best = 0f;
        int minX = Mathf.Max(0, centerX - radius);
        int maxX = Mathf.Min(context.config.grid.width - 1, centerX + radius);
        int minY = Mathf.Max(0, centerY - radius);
        int maxY = Mathf.Min(context.config.grid.height - 1, centerY + radius);
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (context.terrain[y * context.config.grid.width + x] != code)
                    continue;
                float dx = x - centerX;
                float dy = y - centerY;
                float influence = Mathf.Exp(-(dx * dx + dy * dy) / (2f * falloff * falloff));
                if (influence > best) best = influence;
            }
        }
        return best;
    }

    static Vector3 GridToWorld(BuildContext context, float x, float y, float lift)
    {
        float wx = GridWorldX(context, x);
        float wz = GridWorldZ(context, y);
        return new Vector3(wx, Height(context, wx, wz) + lift, wz);
    }

    static float GridWorldX(BuildContext context, float x)
    {
        return (x - context.config.grid.width * 0.5f) * context.config.grid.tileSize;
    }

    static float GridWorldZ(BuildContext context, float y)
    {
        return (context.config.grid.height * 0.5f - y) * context.config.grid.tileSize;
    }

    static List<Vector3> SamplePolyline(Vector3[] points, float spacing)
    {
        List<Vector3> samples = new List<Vector3>();
        for (int i = 0; i < points.Length - 1; i++)
        {
            float distance = Vector3.Distance(points[i], points[i + 1]);
            int steps = Mathf.Max(1, Mathf.CeilToInt(distance / spacing));
            for (int step = 0; step < steps; step++)
                samples.Add(Vector3.Lerp(points[i], points[i + 1], step / (float)steps));
        }
        samples.Add(points[points.Length - 1]);
        return samples;
    }

    static Vector3[] BuildTerrainFollowingRoute(
        BuildContext context,
        List<LycheeGridPoint> controlPoints,
        float lift)
    {
        List<Vector3> result = new List<Vector3>();
        for (int i = 0; i < controlPoints.Count - 1; i++)
        {
            LycheeGridPoint start = controlPoints[i];
            LycheeGridPoint end = controlPoints[i + 1];
            float gridDistance = Vector2.Distance(
                new Vector2(start.x, start.y),
                new Vector2(end.x, end.y));
            int steps = Mathf.Max(1, Mathf.CeilToInt(gridDistance / 0.75f));
            for (int step = 0; step < steps; step++)
            {
                float t = step / (float)steps;
                result.Add(GridToWorld(
                    context,
                    Mathf.Lerp(start.x, end.x, t),
                    Mathf.Lerp(start.y, end.y, t),
                    lift));
            }
        }
        LycheeGridPoint last = controlPoints[controlPoints.Count - 1];
        result.Add(GridToWorld(context, last.x, last.y, lift));
        return result.ToArray();
    }

    static Vector3 RandomMapPoint(BuildContext context, System.Random random, float margin)
    {
        float halfWidth = context.config.grid.width * context.config.grid.tileSize * 0.5f;
        float halfHeight = context.config.grid.height * context.config.grid.tileSize * 0.5f;
        float x = RandomRange(random, -halfWidth + margin, halfWidth - margin);
        float z = RandomRange(random, -halfHeight + margin, halfHeight - margin);
        return new Vector3(x, Height(context, x, z), z);
    }

    static Vector3 Around(BuildContext context, System.Random random, Vector3 center, float radius)
    {
        float angle = RandomRange(random, 0f, Mathf.PI * 2f);
        float distance = Mathf.Sqrt((float)random.NextDouble()) * radius;
        float x = center.x + Mathf.Cos(angle) * distance;
        float z = center.z + Mathf.Sin(angle) * distance;
        return new Vector3(x, Height(context, x, z), z);
    }

    static bool IsClear(
        Vector3 point,
        List<Vector3> routePoints,
        List<Vector3> nodePoints,
        float routeRadius,
        float nodeRadius)
    {
        if (routePoints.Any(route => FlatDistance(route, point) < routeRadius)) return false;
        if (nodePoints.Any(node => FlatDistance(node, point) < nodeRadius)) return false;
        return true;
    }

    static float FlatDistance(Vector3 a, Vector3 b)
    {
        return Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
    }

    static void CreateRibbonAsset(
        BuildContext context,
        GameObject parent,
        Vector3[] points,
        float width,
        Material material,
        string name)
    {
        Mesh mesh = CreateRibbonMesh(context, points, width, context.assetPrefix + "_" + name);
        mesh = SaveMesh(mesh, MeshFolder + "/" + context.assetPrefix + "_" + name + ".asset");
        GameObject ribbon = Child(parent, name);
        ribbon.AddComponent<MeshFilter>().sharedMesh = mesh;
        ribbon.AddComponent<MeshRenderer>().sharedMaterial = material;
    }

    static void CreateWheelRuts(
        BuildContext context,
        GameObject parent,
        Vector3[] points,
        float roadWidth,
        Material material,
        string edgeId)
    {
        for (int lane = -1; lane <= 1; lane += 2)
        {
            Vector3[] shifted = new Vector3[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                Vector3 forward = i == points.Length - 1 ? points[i] - points[i - 1] : points[i + 1] - points[i];
                forward.y = 0f;
                Vector3 side = Vector3.Cross(Vector3.up, forward.normalized);
                shifted[i] = points[i] + side * lane * roadWidth * 0.22f + Vector3.up * 0.018f;
            }
            CreateRibbonAsset(context, parent, shifted, 0.065f, material, edgeId + (lane < 0 ? "_RutL" : "_RutR"));
        }
    }

    static Mesh CreateRibbonMesh(BuildContext context, Vector3[] points, float width, string name)
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
            Vector3 left = points[i] - side * width * 0.5f;
            Vector3 right = points[i] + side * width * 0.5f;
            float centerSurface = Height(context, points[i].x, points[i].z);
            float surfaceOffset = points[i].y - centerSurface;
            left.y = Height(context, left.x, left.z) + surfaceOffset;
            right.y = Height(context, right.x, right.z) + surfaceOffset;
            vertices[i * 2] = left;
            vertices[i * 2 + 1] = right;
            uv[i * 2] = new Vector2(0f, distance * 0.42f);
            uv[i * 2 + 1] = new Vector2(1f, distance * 0.42f);
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
        Mesh mesh = new Mesh { name = name };
        mesh.vertices = vertices;
        mesh.uv = uv;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    static GameObject Instantiate(
        GameObject prefab,
        Transform parent,
        Vector3 position,
        float scale,
        System.Random random)
    {
        GameObject instance = PrefabUtility.InstantiatePrefab(prefab, parent) as GameObject;
        if (instance == null) return null;
        instance.name = prefab.name;
        instance.transform.position = position;
        instance.transform.rotation = Quaternion.Euler(0f, RandomRange(random, 0f, 360f), 0f);
        instance.transform.localScale = Vector3.one * scale;
        foreach (Collider collider in instance.GetComponentsInChildren<Collider>(true))
            collider.enabled = false;
        return instance;
    }

    static Material LoadOrCreateMaterial(string name, Color color, float smoothness)
    {
        string path = MaterialFolder + "/" + name + ".mat";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        material.color = color;
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
        EditorUtility.SetDirty(material);
        return material;
    }

    static Material LoadOrCreateTexturedMaterial(
        string name,
        string albedoFile,
        string normalFile,
        Color tint,
        float smoothness)
    {
        Material material = LoadOrCreateMaterial(name, tint, smoothness);
        const string textureRoot =
            "Assets/Pure Poly/Free Low Poly Nature Pack/Terrain/Terrain_Textures/";
        Texture2D albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(textureRoot + albedoFile);
        Texture2D normal = AssetDatabase.LoadAssetAtPath<Texture2D>(textureRoot + normalFile);
        if (material.HasProperty("_BaseMap"))
        {
            material.SetTexture("_BaseMap", albedo);
            material.SetTextureScale("_BaseMap", new Vector2(1.25f, 1.25f));
        }
        if (material.HasProperty("_BumpMap"))
        {
            material.SetTexture("_BumpMap", normal);
            material.EnableKeyword("_NORMALMAP");
            material.SetFloat("_BumpScale", 0.72f);
        }
        EditorUtility.SetDirty(material);
        return material;
    }

    static Material LoadOrCreatePbrMaterial(
        string name,
        string textureBaseName,
        Color tint,
        float smoothness,
        Vector2 textureScale)
    {
        Material material = LoadOrCreateMaterial(name, tint, smoothness);
        const string textureRoot =
            "Assets/ALP_Assets/Realistic Terrain Textures Lite/Textures/";
        Texture2D albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(
            textureRoot + textureBaseName + ".tif");
        Texture2D normal = AssetDatabase.LoadAssetAtPath<Texture2D>(
            textureRoot + textureBaseName + "_N.png");
        Texture2D occlusion = AssetDatabase.LoadAssetAtPath<Texture2D>(
            textureRoot + textureBaseName + "_AO.png");

        if (material.HasProperty("_BaseMap"))
        {
            material.SetTexture("_BaseMap", albedo);
            material.SetTextureScale("_BaseMap", textureScale);
        }
        if (material.HasProperty("_BumpMap"))
        {
            material.SetTexture("_BumpMap", normal);
            material.SetTextureScale("_BumpMap", textureScale);
            material.EnableKeyword("_NORMALMAP");
            material.SetFloat("_BumpScale", 0.82f);
        }
        if (material.HasProperty("_OcclusionMap"))
        {
            material.SetTexture("_OcclusionMap", occlusion);
            material.SetTextureScale("_OcclusionMap", textureScale);
            material.SetFloat("_OcclusionStrength", 0.72f);
        }
        EditorUtility.SetDirty(material);
        return material;
    }

    static Material LoadOrCreateWaterMaterial()
    {
        string path = MaterialFolder + "/M_Config_WaterRoute.mat";
        Material source = AssetDatabase.LoadAssetAtPath<Material>(
            "Assets/IgniteCoders/Simple Water Shader/Resources/Water_mat_02.mat");
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (source != null)
        {
            if (material == null)
            {
                material = new Material(source);
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = source.shader;
                material.CopyPropertiesFromMaterial(source);
            }
        }
        else
        {
            Shader shader = Shader.Find("Lychee/River Water");
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = shader;
            }
            Texture2D normal = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/Pure Poly/Free Low Poly Nature Pack/Terrain/Terrain_Textures/PP_Ground_Normal.png");
            material.SetTexture("_NormalMap", normal);
            material.SetTextureScale("_NormalMap", new Vector2(1.2f, 2.8f));
            material.SetColor("_BaseColor", new Color(0.05f, 0.38f, 0.55f, 0.88f));
            material.SetColor("_ShallowColor", new Color(0.20f, 0.70f, 0.72f, 0.72f));
            material.SetFloat("_NormalStrength", 0.54f);
            material.SetFloat("_FlowSpeed", 0.10f);
            material.SetFloat("_Smoothness", 0.88f);
        }
        EditorUtility.SetDirty(material);
        return material;
    }

    static void EnableEmission(Material material, Color emission)
    {
        if (material == null || !material.HasProperty("_EmissionColor")) return;
        material.EnableKeyword("_EMISSION");
        material.SetColor("_EmissionColor", emission);
        material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        EditorUtility.SetDirty(material);
    }

    static Color NodeTypeColor(string nodeType)
    {
        switch (nodeType)
        {
            case "START": return new Color(0.28f, 0.82f, 0.36f);
            case "DOCK":
            case "WATER_STATION": return new Color(0.16f, 0.65f, 0.95f);
            case "MOUNTAIN_NODE":
            case "MOUNTAIN_PASS": return new Color(0.58f, 0.72f, 0.32f);
            case "KEY_PASS":
            case "PASS":
            case "GATE": return new Color(0.92f, 0.55f, 0.16f);
            case "FINISH": return new Color(0.94f, 0.25f, 0.22f);
            default: return new Color(0.92f, 0.78f, 0.24f);
        }
    }

    static Material LoadOrCreateTerrainMaterial()
    {
        string path = MaterialFolder + "/M_Config_Terrain_Grassland.mat";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        Shader shader = Shader.Find("Lychee/Terrain Blend");
        if (material == null)
        {
            material = new Material(shader != null
                ? shader
                : Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(material, path);
        }
        else if (shader != null)
        {
            material.shader = shader;
        }

        const string realisticTextureRoot =
            "Assets/ALP_Assets/Realistic Terrain Textures Lite/Textures/";
        Texture2D grass = AssetDatabase.LoadAssetAtPath<Texture2D>(
            realisticTextureRoot + "Ground005.tif");
        Texture2D dirt = AssetDatabase.LoadAssetAtPath<Texture2D>(
            realisticTextureRoot + "Ground004.tif");
        Texture2D rock = AssetDatabase.LoadAssetAtPath<Texture2D>(
            realisticTextureRoot + "GroundPatchCracked01.tif");
        if (material.HasProperty("_GrassTex")) material.SetTexture("_GrassTex", grass);
        if (material.HasProperty("_DirtTex")) material.SetTexture("_DirtTex", dirt);
        if (material.HasProperty("_RockTex")) material.SetTexture("_RockTex", rock);
        if (material.HasProperty("_GrassTint")) material.SetColor("_GrassTint", new Color(0.78f, 0.92f, 0.72f));
        if (material.HasProperty("_LightGrassTint")) material.SetColor("_LightGrassTint", new Color(1.02f, 0.94f, 0.68f));
        if (material.HasProperty("_DeepGrassTint")) material.SetColor("_DeepGrassTint", new Color(0.50f, 0.72f, 0.42f));
        if (material.HasProperty("_DryGrassTint")) material.SetColor("_DryGrassTint", new Color(0.86f, 0.72f, 0.46f));
        if (material.HasProperty("_DirtTint")) material.SetColor("_DirtTint", new Color(0.82f, 0.72f, 0.56f));
        if (material.HasProperty("_RockTint")) material.SetColor("_RockTint", new Color(0.80f, 0.78f, 0.70f));
        if (material.HasProperty("_TextureScale")) material.SetFloat("_TextureScale", 0.22f);
        if (material.HasProperty("_SlopeStart")) material.SetFloat("_SlopeStart", 0.16f);
        if (material.HasProperty("_SlopeEnd")) material.SetFloat("_SlopeEnd", 0.52f);
        EditorUtility.SetDirty(material);
        return material;
    }

    static GameObject LoadPrefab(string path)
    {
        return AssetDatabase.LoadAssetAtPath<GameObject>(path);
    }

    static MapRouteType ParseRouteType(string value)
    {
        if (value == "WATER") return MapRouteType.Water;
        if (value == "MOUNTAIN") return MapRouteType.Mountain;
        if (value == "BRANCH") return MapRouteType.Branch;
        return MapRouteType.OfficialRoad;
    }

    static MapStationType ParseStationType(string value)
    {
        switch (value)
        {
            case "START": return MapStationType.Start;
            case "PASS":
            case "KEY_PASS": return MapStationType.Pass;
            case "DOCK": return MapStationType.Dock;
            case "WATER_STATION": return MapStationType.WaterStation;
            case "MOUNTAIN_NODE": return MapStationType.MountainNode;
            case "MOUNTAIN_PASS": return MapStationType.PlankRoad;
            case "STATION": return MapStationType.MajorStation;
            case "JUNCTION": return MapStationType.Plain;
            case "GATE": return MapStationType.PalaceGate;
            case "FINISH": return MapStationType.Finish;
            default: return MapStationType.PostStation;
        }
    }

    static Mesh SaveMesh(Mesh mesh, string path)
    {
        Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing != null) AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(mesh, path);
        return AssetDatabase.LoadAssetAtPath<Mesh>(path);
    }

    static GameObject Child(GameObject parent, string name)
    {
        GameObject child = new GameObject(name);
        child.transform.SetParent(parent.transform, false);
        return child;
    }

    static void EnsureFolders()
    {
        EnsureFolder("Assets/LycheeMap15", "Configs");
        EnsureFolder("Assets/LycheeMap15/Configs", "Maps");
        EnsureFolder("Assets/LycheeMap15", "GeneratedMeshes");
        EnsureFolder("Assets/Scenes", "GeneratedMaps");
    }

    static void EnsureFolder(string parent, string name)
    {
        string path = parent + "/" + name;
        if (!AssetDatabase.IsValidFolder(path))
            AssetDatabase.CreateFolder(parent, name);
    }

    static float RandomRange(System.Random random, float min, float max)
    {
        return Mathf.Lerp(min, max, (float)random.NextDouble());
    }

    static int StableHash(string value)
    {
        unchecked
        {
            int hash = 23;
            foreach (char character in value ?? string.Empty)
                hash = hash * 31 + character;
            return hash;
        }
    }

    static string Sanitize(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string((value ?? "lychee_map").Select(character =>
            invalid.Contains(character) ? '_' : character).ToArray());
    }
}
