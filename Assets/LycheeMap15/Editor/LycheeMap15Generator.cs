using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class LycheeMap15Generator
{
    const string RootPath = "Assets/LycheeMap15";
    const string MaterialPath = RootPath + "/Materials";
    const string MeshPath = RootPath + "/Meshes";
    const string ScenePath = "Assets/Scenes/LycheeMap15_TerrainBase.unity";

    struct StationDef
    {
        public string id;
        public string label;
        public MapStationType type;
        public Vector2Int grid;
        public bool safe;

        public StationDef(string id, string label, MapStationType type, int x, int y, bool safe = false)
        {
            this.id = id;
            this.label = label;
            this.type = type;
            grid = new Vector2Int(x, y);
            this.safe = safe;
        }
    }

    struct EdgeDef
    {
        public string id;
        public string from;
        public string to;
        public MapRouteType type;
        public float distance;

        public EdgeDef(string id, string from, string to, MapRouteType type, float distance)
        {
            this.id = id;
            this.from = from;
            this.to = to;
            this.type = type;
            this.distance = distance;
        }
    }

    static readonly StationDef[] Stations =
    {
        new StationDef("S01", "岭南果园", MapStationType.Start, 5, 50),
        new StationDef("S02", "南岭驿", MapStationType.PostStation, 15, 44),
        new StationDef("S03", "梅关驿", MapStationType.PostStation, 26, 40),
        new StationDef("S04", "江南码头", MapStationType.Dock, 20, 52),
        new StationDef("S05", "洞庭水驿", MapStationType.WaterStation, 34, 49),
        new StationDef("S06", "五岭山道", MapStationType.MountainNode, 20, 31),
        new StationDef("S07", "荆襄大驿", MapStationType.MajorStation, 39, 37),
        new StationDef("S08", "秦岭栈道", MapStationType.PlankRoad, 37, 23),
        new StationDef("S09", "洛阳驿", MapStationType.MajorStation, 50, 33),
        new StationDef("S10", "武关", MapStationType.Pass, 53, 22),
        new StationDef("S11", "潼关驿", MapStationType.Pass, 61, 24),
        new StationDef("S12", "关中平原", MapStationType.Plain, 65, 29),
        new StationDef("S13", "灞桥驿", MapStationType.PostStation, 70, 24),
        new StationDef("S14", "朱雀门", MapStationType.PalaceGate, 74, 20),
        new StationDef("S15", "兴庆宫", MapStationType.Finish, 78, 18, true)
    };

    static readonly EdgeDef[] Edges =
    {
        new EdgeDef("E01", "S01", "S02", MapRouteType.OfficialRoad, 30),
        new EdgeDef("E02", "S02", "S03", MapRouteType.OfficialRoad, 25),
        new EdgeDef("E03", "S02", "S04", MapRouteType.Water, 20),
        new EdgeDef("E04", "S04", "S05", MapRouteType.Water, 58),
        new EdgeDef("E05", "S05", "S07", MapRouteType.Water, 50),
        new EdgeDef("E06", "S03", "S07", MapRouteType.OfficialRoad, 60),
        new EdgeDef("E07", "S01", "S06", MapRouteType.Mountain, 48),
        new EdgeDef("E08", "S06", "S08", MapRouteType.Mountain, 68),
        new EdgeDef("E09", "S08", "S10", MapRouteType.Branch, 50),
        new EdgeDef("E10", "S07", "S09", MapRouteType.OfficialRoad, 50),
        new EdgeDef("E11", "S09", "S10", MapRouteType.OfficialRoad, 42),
        new EdgeDef("E12", "S09", "S11", MapRouteType.OfficialRoad, 38),
        new EdgeDef("E13", "S10", "S11", MapRouteType.OfficialRoad, 38),
        new EdgeDef("E14", "S11", "S12", MapRouteType.OfficialRoad, 20),
        new EdgeDef("E15", "S12", "S13", MapRouteType.OfficialRoad, 25),
        new EdgeDef("E16", "S13", "S14", MapRouteType.OfficialRoad, 18),
        new EdgeDef("E17", "S14", "S15", MapRouteType.OfficialRoad, 10)
    };

    [MenuItem("Tools/Lychee Map/Rebuild 15-Node Terrain Base")]
    public static void Build()
    {
        EnsureFolders();
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        scene.name = "LycheeMap15_TerrainBase";

        Material terrainMat = CreateMaterial("M_Terrain", new Color(0.36f, 0.48f, 0.27f), 0.05f);
        Material roadMat = CreateMaterial("M_OfficialRoad", new Color(0.56f, 0.40f, 0.24f), 0.02f);
        Material waterMat = CreateMaterial("M_WaterRoute", new Color(0.18f, 0.50f, 0.62f), 0.45f);
        Material mountainMat = CreateMaterial("M_MountainRoute", new Color(0.43f, 0.37f, 0.31f), 0.02f);
        Material branchMat = CreateMaterial("M_BranchRoute", new Color(0.64f, 0.53f, 0.31f), 0.02f);
        Material rockMat = CreateMaterial("M_Rock", new Color(0.34f, 0.34f, 0.30f), 0.01f);
        Material trunkMat = CreateMaterial("M_Trunk", new Color(0.25f, 0.15f, 0.08f), 0f);
        Material leafMat = CreateMaterial("M_Leaf", new Color(0.16f, 0.36f, 0.18f), 0f);
        Material grassMat = CreateMaterial("M_Grass", new Color(0.31f, 0.50f, 0.20f), 0f);

        GameObject root = new GameObject("LYCHEE_MAP_15_TERRAIN_BASE");
        GameObject terrainLayer = Child(root, "01_Continuous_Terrain");
        GameObject routeLayer = Child(root, "02_Route_Layers");
        GameObject stationLayer = Child(root, "03_Station_Anchors_S01-S15");
        GameObject environmentLayer = Child(root, "04_Environment");
        GameObject eventLayer = Child(root, "05_Event_And_Weather_Anchors");
        GameObject cameraLayer = Child(root, "06_Camera_And_Lighting");

        CreateTerrain(terrainLayer, terrainMat);
        Dictionary<string, Vector3> stationPositions = CreateStationAnchors(stationLayer);
        CreateRoutes(routeLayer, stationPositions, roadMat, waterMat, mountainMat, branchMat);
        CreateEnvironment(environmentLayer, rockMat, trunkMat, leafMat, grassMat);
        CreateEventAnchors(eventLayer, stationPositions);
        CreateCameraAndLighting(cameraLayer);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Selection.activeGameObject = root;
        Debug.Log("LycheeMap15 terrain base rebuilt: 15 stations, 17 route edges.");
    }

    static void EnsureFolders()
    {
        EnsureFolder("Assets", "LycheeMap15");
        EnsureFolder(RootPath, "Editor");
        EnsureFolder(RootPath, "Materials");
        EnsureFolder(RootPath, "Meshes");
        EnsureFolder("Assets", "Scenes");
    }

    static void EnsureFolder(string parent, string name)
    {
        string full = parent + "/" + name;
        if (!AssetDatabase.IsValidFolder(full))
            AssetDatabase.CreateFolder(parent, name);
    }

    static Material CreateMaterial(string name, Color color, float smoothness)
    {
        string path = MaterialPath + "/" + name + ".mat";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }

        material.color = color;
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
        if (name.Contains("Water"))
        {
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = 3000;
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }
        EditorUtility.SetDirty(material);
        return material;
    }

    static GameObject Child(GameObject parent, string name)
    {
        GameObject child = new GameObject(name);
        child.transform.SetParent(parent.transform, false);
        return child;
    }

    static float Gaussian(float x, float z, float cx, float cz, float radius, float amplitude)
    {
        float dx = x - cx;
        float dz = z - cz;
        return amplitude * Mathf.Exp(-(dx * dx + dz * dz) / (2f * radius * radius));
    }

    public static float Height(float x, float z)
    {
        float noise = Mathf.PerlinNoise((x + 53f) * 0.045f, (z + 41f) * 0.045f) * 1.35f;
        float eastRise = Mathf.InverseLerp(-40f, 40f, x) * 0.8f;
        float mountains =
            Gaussian(x, z, -20f, -1f, 8f, 5.0f) +
            Gaussian(x, z, -3f, 7f, 8f, 6.2f) +
            Gaussian(x, z, 13f, 8f, 7f, 5.5f) +
            Gaussian(x, z, 22f, 6f, 6f, 4.0f);
        float riverValley = Gaussian(x, z, -15f, -19f, 10f, -1.7f);
        return Mathf.Max(-0.8f, noise + eastRise + mountains + riverValley);
    }

    static Vector3 GridToWorld(Vector2Int grid, float lift = 0f)
    {
        float x = grid.x - 40f;
        float z = 30f - grid.y;
        return new Vector3(x, Height(x, z) + lift, z);
    }

    static void CreateTerrain(GameObject parent, Material material)
    {
        const int width = 80;
        const int depth = 60;
        Vector3[] vertices = new Vector3[(width + 1) * (depth + 1)];
        Vector2[] uv = new Vector2[vertices.Length];
        int[] triangles = new int[width * depth * 6];

        int index = 0;
        for (int z = 0; z <= depth; z++)
        {
            for (int x = 0; x <= width; x++)
            {
                float wx = x - 40f;
                float wz = z - 30f;
                vertices[index] = new Vector3(wx, Height(wx, wz), wz);
                uv[index] = new Vector2(x / (float)width, z / (float)depth);
                index++;
            }
        }

        int ti = 0;
        for (int z = 0; z < depth; z++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = z * (width + 1) + x;
                triangles[ti++] = i;
                triangles[ti++] = i + width + 1;
                triangles[ti++] = i + 1;
                triangles[ti++] = i + 1;
                triangles[ti++] = i + width + 1;
                triangles[ti++] = i + width + 2;
            }
        }

        UnityEngine.Mesh mesh = new UnityEngine.Mesh { name = "LycheeMap_80x60_ContinuousTerrain" };
        mesh.indexFormat = IndexFormat.UInt32;
        mesh.vertices = vertices;
        mesh.uv = uv;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh = SaveMesh(mesh, MeshPath + "/LycheeMap_80x60_ContinuousTerrain.asset");

        GameObject terrain = new GameObject("ContinuousTerrainMesh");
        terrain.transform.SetParent(parent.transform, false);
        terrain.AddComponent<MeshFilter>().sharedMesh = mesh;
        terrain.AddComponent<MeshRenderer>().sharedMaterial = material;
        terrain.AddComponent<MeshCollider>().sharedMesh = mesh;
        terrain.isStatic = true;

        MapElevationData elevation = parent.AddComponent<MapElevationData>();
        elevation.heightSamples = new float[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
            elevation.heightSamples[i] = vertices[i].y;
    }

    static Dictionary<string, Vector3> CreateStationAnchors(GameObject parent)
    {
        Dictionary<string, Vector3> positions = new Dictionary<string, Vector3>();
        foreach (StationDef station in Stations)
        {
            Vector3 position = GridToWorld(station.grid, 0.18f);
            positions[station.id] = position;
            GameObject anchor = Child(parent, station.id + "_ANCHOR_" + station.label);
            anchor.transform.position = position;
            MapStationAnchor data = anchor.AddComponent<MapStationAnchor>();
            data.stationId = station.id;
            data.displayName = station.label;
            data.stationType = station.type;
            data.gridCoordinate = station.grid;
            data.safeZone = station.safe;

            data.modelSlot = Child(anchor, "ModelSlot_REPLACE_WITH_YOUR_PREFAB").transform;
            data.vehicleStopRed = Child(anchor, "VehicleStop_RED").transform;
            data.vehicleStopBlue = Child(anchor, "VehicleStop_BLUE").transform;
            data.eventAnchor = Child(anchor, "EventFXSlot").transform;
            data.obstacleAnchor = Child(anchor, "NodeObstacleSlot").transform;
            data.vehicleStopRed.localPosition = new Vector3(-0.7f, 0f, 0f);
            data.vehicleStopBlue.localPosition = new Vector3(0.7f, 0f, 0f);
        }
        return positions;
    }

    static void CreateRoutes(
        GameObject parent,
        Dictionary<string, Vector3> stationPositions,
        Material road,
        Material water,
        Material mountain,
        Material branch)
    {
        GameObject officialRoot = Child(parent, "OfficialRoad");
        GameObject waterRoot = Child(parent, "WaterRoute");
        GameObject mountainRoot = Child(parent, "MountainRoute");
        GameObject branchRoot = Child(parent, "BranchRoute");
        MapRouteLayerController controller = parent.AddComponent<MapRouteLayerController>();
        controller.officialRoad = officialRoot.transform;
        controller.waterRoute = waterRoot.transform;
        controller.mountainRoute = mountainRoot.transform;
        controller.branchRoute = branchRoot.transform;

        foreach (EdgeDef edge in Edges)
        {
            GameObject routeParent;
            Material material;
            float width;
            float lift;
            switch (edge.type)
            {
                case MapRouteType.Water:
                    routeParent = waterRoot;
                    material = water;
                    width = 2.6f;
                    lift = 0.13f;
                    break;
                case MapRouteType.Mountain:
                    routeParent = mountainRoot;
                    material = mountain;
                    width = 1.15f;
                    lift = 0.24f;
                    break;
                case MapRouteType.Branch:
                    routeParent = branchRoot;
                    material = branch;
                    width = 0.8f;
                    lift = 0.22f;
                    break;
                default:
                    routeParent = officialRoot;
                    material = road;
                    width = 1.65f;
                    lift = 0.2f;
                    break;
            }

            Vector3 start = stationPositions[edge.from];
            Vector3 end = stationPositions[edge.to];
            Vector3[] points = BuildCurvedPath(start, end, edge.id, lift);
            GameObject edgeObject = Child(routeParent, edge.id + "_" + edge.from + "-" + edge.to);
            MapRouteEdge data = edgeObject.AddComponent<MapRouteEdge>();
            data.edgeId = edge.id;
            data.fromStationId = edge.from;
            data.toStationId = edge.to;
            data.routeType = edge.type;
            data.logicalDistance = edge.distance;

            Transform[] pathTransforms = new Transform[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                GameObject pathPoint = Child(edgeObject, "PathPoint_" + i.ToString("00"));
                pathPoint.transform.position = points[i];
                pathTransforms[i] = pathPoint.transform;
            }
            data.pathPoints = pathTransforms;
            data.obstacleSlot = Child(edgeObject, "EdgeObstacleSlot").transform;
            data.obstacleSlot.position = points[points.Length / 2];

            UnityEngine.Mesh mesh = CreateRibbonMesh(points, width, edge.id + "_RouteMesh");
            mesh = SaveMesh(mesh, MeshPath + "/" + edge.id + "_RouteMesh.asset");
            MeshFilter filter = edgeObject.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            edgeObject.AddComponent<MeshRenderer>().sharedMaterial = material;

            if (edge.type == MapRouteType.OfficialRoad)
                AddWheelRuts(edgeObject, points, width, material);
            if (edge.id == "E04" || edge.id == "E05")
                Child(edgeObject, "BridgeOrDockSlot_REPLACE_WITH_PREFAB").transform.position = points[points.Length / 2];
            if (edge.id == "E08")
                Child(edgeObject, "PlankRoadSlot_REPLACE_WITH_PREFAB").transform.position = points[points.Length / 2];
        }
    }

    static Vector3[] BuildCurvedPath(Vector3 start, Vector3 end, string seed, float lift)
    {
        const int count = 9;
        Vector3[] points = new Vector3[count];
        Vector3 flatDirection = new Vector3(end.x - start.x, 0f, end.z - start.z).normalized;
        Vector3 side = new Vector3(-flatDirection.z, 0f, flatDirection.x);
        float sign = seed.GetHashCode() % 2 == 0 ? 1f : -1f;
        float curve = Mathf.Min(Vector3.Distance(start, end) * 0.11f, 2.1f) * sign;
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)(count - 1);
            Vector3 flat = Vector3.Lerp(start, end, t);
            flat += side * Mathf.Sin(t * Mathf.PI) * curve;
            flat.y = Height(flat.x, flat.z) + lift;
            points[i] = flat;
        }
        return points;
    }

    static UnityEngine.Mesh CreateRibbonMesh(Vector3[] points, float width, string name)
    {
        Vector3[] vertices = new Vector3[points.Length * 2];
        Vector2[] uv = new Vector2[vertices.Length];
        int[] triangles = new int[(points.Length - 1) * 6];
        float accumulated = 0f;
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 forward = i == points.Length - 1 ? points[i] - points[i - 1] : points[i + 1] - points[i];
            forward.y = 0f;
            Vector3 side = Vector3.Cross(Vector3.up, forward.normalized);
            if (i > 0) accumulated += Vector3.Distance(points[i - 1], points[i]);
            vertices[i * 2] = points[i] - side * width * 0.5f;
            vertices[i * 2 + 1] = points[i] + side * width * 0.5f;
            uv[i * 2] = new Vector2(0f, accumulated * 0.15f);
            uv[i * 2 + 1] = new Vector2(1f, accumulated * 0.15f);
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

    static void AddWheelRuts(GameObject parent, Vector3[] points, float roadWidth, Material material)
    {
        for (int lane = -1; lane <= 1; lane += 2)
        {
            Vector3[] rut = new Vector3[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                Vector3 forward = i == points.Length - 1 ? points[i] - points[i - 1] : points[i + 1] - points[i];
                forward.y = 0f;
                Vector3 side = Vector3.Cross(Vector3.up, forward.normalized);
                rut[i] = points[i] + side * roadWidth * 0.22f + Vector3.up * 0.018f;
            }
            UnityEngine.Mesh mesh = CreateRibbonMesh(rut, 0.07f, parent.name + "_Rut");
            mesh = SaveMesh(mesh, MeshPath + "/" + parent.name + "_Rut_" + lane + ".asset");
            GameObject rutObject = Child(parent, lane < 0 ? "WheelRut_L" : "WheelRut_R");
            rutObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            rutObject.AddComponent<MeshRenderer>().sharedMaterial = material;
        }
    }

    static void CreateEnvironment(GameObject parent, Material rock, Material trunk, Material leaf, Material grass)
    {
        GameObject mountainForms = Child(parent, "Mountain_And_Cliff_Forms");
        Vector3[] rockCenters =
        {
            new Vector3(-21f, 0f, -1f), new Vector3(-5f, 0f, 8f),
            new Vector3(12f, 0f, 8f), new Vector3(22f, 0f, 6f)
        };
        for (int group = 0; group < rockCenters.Length; group++)
        {
            for (int i = 0; i < 7; i++)
            {
                float angle = i * 2.399f + group;
                float radius = 2.5f + (i % 3) * 1.3f;
                Vector3 p = rockCenters[group] + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                p.y = Height(p.x, p.z) - 0.15f;
                GameObject stone = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                stone.name = "CliffRock_" + group + "_" + i;
                stone.transform.SetParent(mountainForms.transform);
                stone.transform.position = p;
                stone.transform.localScale = new Vector3(1.8f + i % 2, 1.1f + i % 3, 1.5f + (i + 1) % 2);
                stone.GetComponent<MeshRenderer>().sharedMaterial = rock;
                UnityEngine.Object.DestroyImmediate(stone.GetComponent<Collider>());
            }
        }

        GameObject vegetation = Child(parent, "Vegetation");
        GameObject trees = Child(vegetation, "Tree_Groups");
        GameObject grassPatches = Child(vegetation, "Grass_Patches");
        System.Random random = new System.Random(1509);
        for (int i = 0; i < 54; i++)
        {
            float x = -38f + (float)random.NextDouble() * 74f;
            float z = -27f + (float)random.NextDouble() * 52f;
            if (x > 24f && z > 2f) continue;
            CreateTree(trees, new Vector3(x, Height(x, z), z), trunk, leaf, 0.75f + (float)random.NextDouble() * 0.65f);
        }
        for (int i = 0; i < 90; i++)
        {
            float x = -39f + (float)random.NextDouble() * 77f;
            float z = -28f + (float)random.NextDouble() * 55f;
            GameObject patch = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            patch.name = "Grass_" + i.ToString("00");
            patch.transform.SetParent(grassPatches.transform);
            patch.transform.position = new Vector3(x, Height(x, z) + 0.07f, z);
            patch.transform.localScale = new Vector3(0.18f, 0.07f, 0.18f);
            patch.GetComponent<MeshRenderer>().sharedMaterial = grass;
            UnityEngine.Object.DestroyImmediate(patch.GetComponent<Collider>());
        }

        GameObject replaceable = Child(parent, "Replaceable_Decoration_Anchors");
        string[] slots = { "Bridge_A", "Bridge_B", "BambooForest_A", "BambooForest_B", "Village_A", "Village_B", "Canal_A", "LowWall_A" };
        for (int i = 0; i < slots.Length; i++)
        {
            GameObject slot = Child(replaceable, slots[i] + "_ANCHOR");
            float x = -28f + i * 8f;
            float z = -16f + (i % 3) * 9f;
            slot.transform.position = new Vector3(x, Height(x, z), z);
            MapEnvironmentAnchor data = slot.AddComponent<MapEnvironmentAnchor>();
            data.anchorType = EnvironmentAnchorType.Decoration;
            data.regionId = slots[i];
        }
    }

    static void CreateTree(GameObject parent, Vector3 position, Material trunk, Material leaf, float scale)
    {
        GameObject tree = Child(parent, "TerrainTree");
        tree.transform.position = position;
        tree.transform.localScale = Vector3.one * scale;
        GameObject stem = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        stem.name = "Trunk";
        stem.transform.SetParent(tree.transform, false);
        stem.transform.localPosition = new Vector3(0f, 0.65f, 0f);
        stem.transform.localScale = new Vector3(0.12f, 0.65f, 0.12f);
        stem.GetComponent<MeshRenderer>().sharedMaterial = trunk;
        UnityEngine.Object.DestroyImmediate(stem.GetComponent<Collider>());
        GameObject crown = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        crown.name = "Crown";
        crown.transform.SetParent(tree.transform, false);
        crown.transform.localPosition = new Vector3(0f, 1.55f, 0f);
        crown.transform.localScale = new Vector3(1.05f, 0.75f, 1.05f);
        crown.GetComponent<MeshRenderer>().sharedMaterial = leaf;
        UnityEngine.Object.DestroyImmediate(crown.GetComponent<Collider>());
    }

    static void CreateEventAnchors(GameObject parent, Dictionary<string, Vector3> positions)
    {
        GameObject weather = Child(parent, "Weather_Regions");
        CreateEnvironmentAnchor(weather, "Heat_Lingnan", EnvironmentAnchorType.Weather, positions["S01"], 8f);
        CreateEnvironmentAnchor(weather, "Rain_Jiangnan", EnvironmentAnchorType.Weather, positions["S05"], 9f);
        CreateEnvironmentAnchor(weather, "Fog_Qinling", EnvironmentAnchorType.Weather, positions["S08"], 8f);

        GameObject tasks = Child(parent, "Task_And_Event_Anchors");
        string[] ids = { "S03", "S04", "S06", "S07", "S08", "S09", "S10", "S11", "S13", "S14" };
        foreach (string id in ids)
            CreateEnvironmentAnchor(tasks, "Task_" + id, EnvironmentAnchorType.Task, positions[id], 2.2f);

        GameObject safe = Child(parent, "Safe_Zone");
        CreateEnvironmentAnchor(safe, "SafeZone_S15", EnvironmentAnchorType.CameraFocus, positions["S15"], 4f);
    }

    static void CreateEnvironmentAnchor(GameObject parent, string name, EnvironmentAnchorType type, Vector3 position, float radius)
    {
        GameObject anchor = Child(parent, name);
        anchor.transform.position = position;
        MapEnvironmentAnchor data = anchor.AddComponent<MapEnvironmentAnchor>();
        data.anchorType = type;
        data.regionId = name;
        data.radius = radius;
    }

    static void CreateCameraAndLighting(GameObject parent)
    {
        GameObject cameraObject = Child(parent, "MainCamera_2.5D");
        Camera camera = cameraObject.AddComponent<Camera>();
        cameraObject.tag = "MainCamera";
        camera.orthographic = true;
        camera.orthographicSize = 38f;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 300f;
        camera.backgroundColor = new Color(0.58f, 0.67f, 0.73f);
        camera.clearFlags = CameraClearFlags.SolidColor;
        cameraObject.transform.position = new Vector3(34f, 72f, -58f);
        cameraObject.transform.rotation = Quaternion.Euler(52f, -29f, 0f);

        GameObject sunObject = Child(parent, "Sun_Warm");
        Light sun = sunObject.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(1f, 0.89f, 0.72f);
        sun.intensity = 1.15f;
        sun.shadows = LightShadows.Soft;
        sunObject.transform.rotation = Quaternion.Euler(48f, -32f, 0f);

        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.58f, 0.64f, 0.67f);
        RenderSettings.ambientEquatorColor = new Color(0.42f, 0.45f, 0.38f);
        RenderSettings.ambientGroundColor = new Color(0.22f, 0.20f, 0.16f);
    }

    static UnityEngine.Mesh SaveMesh(UnityEngine.Mesh mesh, string path)
    {
        UnityEngine.Mesh existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(path);
        if (existing != null)
            AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(mesh, path);
        return AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(path);
    }
}
