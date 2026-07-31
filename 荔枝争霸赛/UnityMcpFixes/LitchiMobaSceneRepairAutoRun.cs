using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

[InitializeOnLoad]
public static class LitchiMobaSceneRepairAutoRun
{
    private const string ConfigPath = "/Users/lunayuan/Documents/荔枝争霸赛/一骑红尘_Unity全场景覆盖地图与回放Demo_V2/ui_map_config_unity_full_coverage_v2.json";

    static LitchiMobaSceneRepairAutoRun()
    {
        if (EditorPrefs.GetBool("LitchiMobaSceneRepair.AutoRun", false))
        {
            EditorPrefs.SetBool("LitchiMobaSceneRepair.AutoRun", false);
            EditorApplication.delayCall += Run;
        }
    }

    [MenuItem("Tools/Litchi/Rebuild MOBA Map")]
    public static void Run()
    {
        RepairResult result = new RepairResult();
        if (!File.Exists(ConfigPath))
        {
            result.LogError("Map config not found: {0}", ConfigPath);
            return;
        }

        MapConfig config = JsonUtility.FromJson<MapConfig>(File.ReadAllText(ConfigPath));
        if (config == null || config.grid == null || config.nodes == null || config.routePaths == null)
        {
            result.LogError("Map config could not be parsed.");
            return;
        }

        LitchiMobaSceneBuilder builder = new LitchiMobaSceneBuilder(config, result);
        builder.Rebuild();
    }

    private sealed class RepairResult
    {
        public void RegisterObjectCreation(UnityEngine.Object obj)
        {
            if (obj != null) Undo.RegisterCreatedObjectUndo(obj, "Litchi MOBA Scene Repair");
        }

        public void RegisterObjectModification(UnityEngine.Object obj)
        {
            if (obj != null) Undo.RecordObject(obj, "Litchi MOBA Scene Repair");
        }

        public void DestroyObject(UnityEngine.Object obj)
        {
            if (obj != null) Undo.DestroyObjectImmediate(obj);
        }

        public void Log(string message)
        {
            Debug.Log(message);
        }

        public void Log(string format, params object[] args)
        {
            Debug.LogFormat(format, args);
        }

        public void LogWarning(string format, params object[] args)
        {
            Debug.LogWarningFormat(format, args);
        }

        public void LogError(string message)
        {
            Debug.LogError(message);
        }

        public void LogError(string format, params object[] args)
        {
            Debug.LogErrorFormat(format, args);
        }
    }

    [Serializable] private class MapConfig
    {
        public string mapName;
        public GridInfo grid;
        public List<NodeInfo> nodes;
        public List<EdgeInfo> edges;
        public List<RoutePath> routePaths;
    }

    [Serializable] private class GridInfo
    {
        public int width;
        public int height;
        public float tileSize;
    }

    [Serializable] private class NodeInfo
    {
        public string nodeId;
        public int code;
        public string name;
        public string type;
        public int x;
        public int y;
    }

    [Serializable] private class EdgeInfo
    {
        public string edgeId;
        public string fromNodeId;
        public string toNodeId;
        public string routeType;
        public string pathId;
    }

    [Serializable] private class RoutePath
    {
        public string pathId;
        public string edgeId;
        public List<GridPoint> points;
    }

    [Serializable] private class GridPoint
    {
        public float x;
        public float y;
    }

    private sealed class LitchiMobaSceneBuilder
    {
        private const float TerrainStep = 0.4f;
        private const float WaterHalfWidth = 1.575f;
        private const float RiverBedDepth = 0.72f;
        private const float BankWidth = 1.35f;
        private const float RoadHalfWidth = 0.62f;
        private const float MountainRoadHalfWidth = 0.52f;
        private const float RoadOffset = 0.075f;
        private const float MountainRoadOffset = 0.095f;
        private const float RouteSampleStep = 0.65f;

        private readonly MapConfig config;
        private readonly RepairResult result;
        private readonly Dictionary<string, EdgeInfo> edgeById = new Dictionary<string, EdgeInfo>();
        private readonly List<RouteData> routes = new List<RouteData>();
        private readonly List<RouteData> waterRoutes = new List<RouteData>();
        private readonly List<RouteData> landRoutes = new List<RouteData>();
        private readonly List<Material> createdMaterials = new List<Material>();
        private readonly Dictionary<string, Material> mats = new Dictionary<string, Material>();

        private GameObject root;
        private Transform terrainRoot;
        private Transform routesRoot;
        private Transform nodeRoot;
        private Transform natureRoot;
        private Transform validationRoot;
        private Camera mainCamera;

        public LitchiMobaSceneBuilder(MapConfig config, RepairResult result)
        {
            this.config = config;
            this.result = result;
        }

        public void Rebuild()
        {
            BuildRouteCache();
            CreateMaterials();
            ReplacePreviousGeneratedRoot();
            CreateHierarchy();
            DisableLikelyLegacyMapRenderers();

            BuildTerrain();
            BuildWater();
            BuildRoads();
            BuildNodes();
            ScatterNature();
            SetupLightingAndCamera();
            BuildValidationMarker();
            ValidateScene();

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            if (!string.IsNullOrEmpty(EditorSceneManager.GetActiveScene().path))
            {
                EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            }

            result.Log("MOBA scene repair finished. Created root {0}", root);
        }

        private void BuildRouteCache()
        {
            edgeById.Clear();
            foreach (EdgeInfo edge in config.edges)
            {
                edgeById[edge.edgeId] = edge;
            }

            routes.Clear();
            waterRoutes.Clear();
            landRoutes.Clear();

            foreach (RoutePath path in config.routePaths)
            {
                if (path.points == null || path.points.Count < 2)
                    continue;

                EdgeInfo edge;
                if (!edgeById.TryGetValue(path.edgeId, out edge))
                    continue;

                RouteData route = new RouteData();
                route.edgeId = edge.edgeId;
                route.routeType = string.IsNullOrEmpty(edge.routeType) ? "ROAD" : edge.routeType;
                route.points = new List<Vector2>();

                foreach (GridPoint point in path.points)
                {
                    route.points.Add(GridToWorld(point.x, point.y));
                }

                routes.Add(route);
                if (route.routeType == "WATER")
                    waterRoutes.Add(route);
                else
                    landRoutes.Add(route);
            }
        }

        private void ReplacePreviousGeneratedRoot()
        {
            GameObject previous = GameObject.Find("MAP_ROOT_MOBA_REBUILD_20260611");
            if (previous != null)
            {
                result.DestroyObject(previous);
            }
        }

        private void CreateHierarchy()
        {
            root = new GameObject("MAP_ROOT_MOBA_REBUILD_20260611");
            result.RegisterObjectCreation(root);

            terrainRoot = NewChild(root.transform, "Terrain");
            routesRoot = NewChild(root.transform, "Routes");
            nodeRoot = NewChild(root.transform, "Node_Anchors");
            natureRoot = NewChild(root.transform, "Natural_Decoration");
            validationRoot = NewChild(root.transform, "Validation");
        }

        private Transform NewChild(Transform parent, string name)
        {
            GameObject child = new GameObject(name);
            result.RegisterObjectCreation(child);
            child.transform.SetParent(parent, false);
            return child.transform;
        }

        private void CreateMaterials()
        {
            mats["TerrainGrass"] = MakeLitMaterial("Moba_Grass_DeepGreen", new Color(0.20f, 0.38f, 0.18f), 0.28f, 0.0f);
            mats["TerrainBank"] = MakeLitMaterial("Moba_RiverBank_WarmStone", new Color(0.48f, 0.40f, 0.29f), 0.34f, 0.0f);
            mats["Road"] = MakeLitMaterial("Moba_OfficialRoad_BronzeStone", new Color(0.45f, 0.28f, 0.27f), 0.42f, 0.0f);
            mats["MountainRoad"] = MakeLitMaterial("Moba_MountainRoad_CoolSlate", new Color(0.34f, 0.30f, 0.43f), 0.48f, 0.0f);
            mats["BranchRoad"] = MakeLitMaterial("Moba_BranchRoad_OliveClay", new Color(0.38f, 0.34f, 0.22f), 0.36f, 0.0f);
            mats["RoadEdge"] = MakeEmissiveMaterial("Moba_Route_Gold_Edge", new Color(0.86f, 0.65f, 0.29f), new Color(0.55f, 0.31f, 0.08f));
            mats["RoadGlow"] = MakeEmissiveMaterial("Moba_Route_Soft_Glow", new Color(0.95f, 0.67f, 0.33f, 0.55f), new Color(0.60f, 0.26f, 0.08f));
            mats["Water"] = MakeWaterMaterial();
            mats["WaterFoam"] = MakeEmissiveMaterial("Moba_Water_Foam_Glow", new Color(0.56f, 0.94f, 1.0f, 0.72f), new Color(0.10f, 0.72f, 0.95f));
            mats["Gold"] = MakeLitMaterial("Moba_Node_Gold", new Color(0.95f, 0.69f, 0.30f), 0.62f, 0.0f);
            mats["RoofRed"] = MakeLitMaterial("Moba_Node_Roof_Red", new Color(0.55f, 0.10f, 0.12f), 0.50f, 0.0f);
            mats["Wall"] = MakeLitMaterial("Moba_Node_Wall_Stone", new Color(0.64f, 0.56f, 0.44f), 0.40f, 0.0f);
            mats["Wood"] = MakeLitMaterial("Moba_Dock_Wood", new Color(0.37f, 0.23f, 0.14f), 0.38f, 0.0f);
            mats["TreeTrunk"] = MakeLitMaterial("Moba_Tree_Trunk", new Color(0.24f, 0.15f, 0.09f), 0.30f, 0.0f);
            mats["TreeLeafA"] = MakeLitMaterial("Moba_Tree_Leaf_A", new Color(0.16f, 0.43f, 0.19f), 0.24f, 0.0f);
            mats["TreeLeafB"] = MakeLitMaterial("Moba_Tree_Leaf_B", new Color(0.28f, 0.50f, 0.20f), 0.24f, 0.0f);
            mats["Rock"] = MakeLitMaterial("Moba_Rock_WarmGrey", new Color(0.43f, 0.42f, 0.37f), 0.45f, 0.0f);
            mats["Label"] = MakeEmissiveMaterial("Moba_Label_Gold_Emissive", new Color(1.0f, 0.80f, 0.36f), new Color(0.65f, 0.43f, 0.10f));
        }

        private Material MakeLitMaterial(string name, Color color, float smoothness, float metallic)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                shader = Shader.Find("Standard");

            Material mat = new Material(shader);
            mat.name = name;
            mat.color = color;
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Smoothness"))
                mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_Metallic"))
                mat.SetFloat("_Metallic", metallic);
            createdMaterials.Add(mat);
            result.RegisterObjectCreation(mat);
            return mat;
        }

        private Material MakeEmissiveMaterial(string name, Color color, Color emission)
        {
            Material mat = MakeLitMaterial(name, color, 0.55f, 0.0f);
            if (mat.HasProperty("_EmissionColor"))
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", emission);
            }
            return mat;
        }

        private Material MakeWaterMaterial()
        {
            Material mat = MakeLitMaterial("Moba_Water_Depth_Cyan", new Color(0.08f, 0.55f, 0.72f, 0.62f), 0.78f, 0.0f);
            if (mat.HasProperty("_Surface"))
                mat.SetFloat("_Surface", 1.0f);
            if (mat.HasProperty("_AlphaClip"))
                mat.SetFloat("_AlphaClip", 0.0f);
            if (mat.HasProperty("_ZWrite"))
                mat.SetFloat("_ZWrite", 1.0f);
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", new Color(0.08f, 0.55f, 0.72f, 0.62f));
            mat.renderQueue = (int)RenderQueue.Transparent;
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            return mat;
        }

        private void BuildTerrain()
        {
            int cols = Mathf.RoundToInt(config.grid.width / TerrainStep) + 1;
            int rows = Mathf.RoundToInt(config.grid.height / TerrainStep) + 1;
            Vector3[] vertices = new Vector3[cols * rows];
            Vector2[] uvs = new Vector2[vertices.Length];
            Color[] colors = new Color[vertices.Length];

            for (int z = 0; z < rows; z++)
            {
                for (int x = 0; x < cols; x++)
                {
                    float wx = x * TerrainStep;
                    float wz = z * TerrainStep;
                    float h = TerrainHeight(new Vector2(wx, wz));
                    int i = z * cols + x;
                    vertices[i] = new Vector3(wx, h, wz);
                    uvs[i] = new Vector2(wx / 8.0f, wz / 8.0f);
                    colors[i] = Color.Lerp(new Color(0.20f, 0.36f, 0.16f), new Color(0.38f, 0.48f, 0.20f), Mathf.PerlinNoise(wx * 0.18f, wz * 0.18f));
                }
            }

            List<int> grassTris = new List<int>();
            List<int> bankTris = new List<int>();
            for (int z = 0; z < rows - 1; z++)
            {
                for (int x = 0; x < cols - 1; x++)
                {
                    int i0 = z * cols + x;
                    int i1 = z * cols + x + 1;
                    int i2 = (z + 1) * cols + x;
                    int i3 = (z + 1) * cols + x + 1;

                    AddTerrainTriangle(vertices, i0, i2, i1, grassTris, bankTris);
                    AddTerrainTriangle(vertices, i1, i2, i3, grassTris, bankTris);
                }
            }

            Mesh mesh = new Mesh();
            mesh.name = "FinalTerrain_RiverCut_Submeshed";
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.colors = colors;
            mesh.subMeshCount = 2;
            mesh.SetTriangles(grassTris, 0);
            mesh.SetTriangles(bankTris, 1);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            GameObject terrain = new GameObject("FinalTerrain_Continuous_0p4m");
            result.RegisterObjectCreation(terrain);
            terrain.transform.SetParent(terrainRoot, false);
            MeshFilter mf = terrain.AddComponent<MeshFilter>();
            MeshRenderer mr = terrain.AddComponent<MeshRenderer>();
            MeshCollider mc = terrain.AddComponent<MeshCollider>();
            mf.sharedMesh = mesh;
            mc.sharedMesh = mesh;
            mr.sharedMaterials = new[] { mats["TerrainGrass"], mats["TerrainBank"] };
        }

        private void AddTerrainTriangle(Vector3[] vertices, int a, int b, int c, List<int> grass, List<int> bank)
        {
            Vector3 center3 = (vertices[a] + vertices[b] + vertices[c]) / 3.0f;
            Vector2 center = new Vector2(center3.x, center3.z);
            WaterHit water = NearestWater(center);
            float waterLevel = WaterLevelAt(center);
            bool isBank = water.valid && water.distance < WaterHalfWidth + 0.58f && center3.y < waterLevel + 0.18f;
            List<int> target = isBank ? bank : grass;
            target.Add(a);
            target.Add(b);
            target.Add(c);
        }

        private void BuildWater()
        {
            Transform waterRoot = NewChild(routesRoot, "WaterRoute_And_River");
            foreach (RouteData route in waterRoutes)
            {
                List<SamplePoint> samples = Densify(route.points, RouteSampleStep);
                if (samples.Count < 2)
                    continue;

                List<Vector3> vertices = new List<Vector3>();
                List<Vector2> uvs = new List<Vector2>();
                List<int> triangles = new List<int>();

                for (int i = 0; i < samples.Count; i++)
                {
                    Vector2 normal = RouteNormal(samples, i);
                    Vector2 center = samples[i].position;
                    float y = WaterLevelAt(center);
                    Vector2 left = center + normal * WaterHalfWidth;
                    Vector2 right = center - normal * WaterHalfWidth;
                    vertices.Add(new Vector3(left.x, y, left.y));
                    vertices.Add(new Vector3(right.x, y, right.y));
                    uvs.Add(new Vector2(0.0f, samples[i].distanceAlong * 0.12f));
                    uvs.Add(new Vector2(1.0f, samples[i].distanceAlong * 0.12f));

                    if (i < samples.Count - 1)
                    {
                        int baseIndex = i * 2;
                        triangles.Add(baseIndex);
                        triangles.Add(baseIndex + 2);
                        triangles.Add(baseIndex + 1);
                        triangles.Add(baseIndex + 1);
                        triangles.Add(baseIndex + 2);
                        triangles.Add(baseIndex + 3);
                    }
                }

                Mesh mesh = new Mesh();
                mesh.name = route.edgeId + "_WaterSurface_BuriedEdges";
                mesh.SetVertices(vertices);
                mesh.SetUVs(0, uvs);
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();

                GameObject water = new GameObject(route.edgeId + "_Route_WaterSurface");
                result.RegisterObjectCreation(water);
                water.transform.SetParent(waterRoot, false);
                water.AddComponent<MeshFilter>().sharedMesh = mesh;
                water.AddComponent<MeshRenderer>().sharedMaterial = mats["Water"];

                BuildWaterFoam(route.edgeId + "_WaterFoam", samples, waterRoot);
                BuildWaterCap(route.edgeId + "_WaterStartCap", samples[0].position, waterRoot);
                BuildWaterCap(route.edgeId + "_WaterEndCap", samples[samples.Count - 1].position, waterRoot);
            }
        }

        private void BuildWaterFoam(string name, List<SamplePoint> samples, Transform parent)
        {
            Mesh strip = BuildRibbonMesh(samples, 0.07f, 0.02f, delegate(Vector2 p) { return WaterLevelAt(p) + 0.018f; });
            GameObject obj = new GameObject(name);
            result.RegisterObjectCreation(obj);
            obj.transform.SetParent(parent, false);
            obj.AddComponent<MeshFilter>().sharedMesh = strip;
            obj.AddComponent<MeshRenderer>().sharedMaterial = mats["WaterFoam"];
        }

        private void BuildWaterCap(string name, Vector2 center, Transform parent)
        {
            const int segments = 28;
            float radius = WaterHalfWidth + 0.08f;
            float y = WaterLevelAt(center) + 0.006f;
            List<Vector3> vertices = new List<Vector3>();
            List<int> triangles = new List<int>();
            vertices.Add(new Vector3(center.x, y, center.y));
            for (int i = 0; i <= segments; i++)
            {
                float a = Mathf.PI * 2.0f * i / segments;
                vertices.Add(new Vector3(center.x + Mathf.Cos(a) * radius, y, center.y + Mathf.Sin(a) * radius));
            }
            for (int i = 1; i <= segments; i++)
            {
                triangles.Add(0);
                triangles.Add(i);
                triangles.Add(i + 1);
            }

            Mesh mesh = new Mesh();
            mesh.name = name + "_Mesh";
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            GameObject cap = new GameObject(name);
            result.RegisterObjectCreation(cap);
            cap.transform.SetParent(parent, false);
            cap.AddComponent<MeshFilter>().sharedMesh = mesh;
            cap.AddComponent<MeshRenderer>().sharedMaterial = mats["Water"];
        }

        private void BuildRoads()
        {
            Transform roadRoot = NewChild(routesRoot, "OfficialRoad_MountainRoute_Branch");
            foreach (RouteData route in landRoutes)
            {
                List<SamplePoint> samples = Densify(route.points, RouteSampleStep);
                if (samples.Count < 2)
                    continue;

                bool mountain = route.routeType == "MOUNTAIN";
                bool branch = route.routeType == "BRANCH";
                float halfWidth = mountain ? MountainRoadHalfWidth : RoadHalfWidth;
                float offset = mountain ? MountainRoadOffset : RoadOffset;
                Material roadMaterial = mountain ? mats["MountainRoad"] : (branch ? mats["BranchRoad"] : mats["Road"]);

                Mesh roadMesh = BuildRibbonMesh(samples, halfWidth, 0.0f, delegate(Vector2 p) { return TerrainHeight(p) + offset; });
                GameObject road = new GameObject(route.edgeId + "_" + route.routeType + "_Road_Fitted");
                result.RegisterObjectCreation(road);
                road.transform.SetParent(roadRoot, false);
                road.AddComponent<MeshFilter>().sharedMesh = roadMesh;
                road.AddComponent<MeshRenderer>().sharedMaterial = roadMaterial;

                Mesh edgeMesh = BuildRoadEdgeMesh(samples, halfWidth + 0.045f, offset + 0.012f);
                GameObject edge = new GameObject(route.edgeId + "_" + route.routeType + "_GoldEdges");
                result.RegisterObjectCreation(edge);
                edge.transform.SetParent(roadRoot, false);
                edge.AddComponent<MeshFilter>().sharedMesh = edgeMesh;
                edge.AddComponent<MeshRenderer>().sharedMaterial = mats["RoadEdge"];

                Mesh glowMesh = BuildRibbonMesh(samples, Mathf.Max(0.08f, halfWidth * 0.18f), 0.0f, delegate(Vector2 p) { return TerrainHeight(p) + offset + 0.018f; });
                GameObject glow = new GameObject(route.edgeId + "_" + route.routeType + "_CenterGlow");
                result.RegisterObjectCreation(glow);
                glow.transform.SetParent(roadRoot, false);
                glow.AddComponent<MeshFilter>().sharedMesh = glowMesh;
                glow.AddComponent<MeshRenderer>().sharedMaterial = mats["RoadGlow"];
            }
        }

        private Mesh BuildRoadEdgeMesh(List<SamplePoint> samples, float halfWidth, float heightOffset)
        {
            List<Vector3> vertices = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<int> triangles = new List<int>();
            float edgeHalf = 0.035f;

            for (int side = -1; side <= 1; side += 2)
            {
                int startVertex = vertices.Count;
                for (int i = 0; i < samples.Count; i++)
                {
                    Vector2 normal = RouteNormal(samples, i);
                    Vector2 center = samples[i].position;
                    Vector2 a = center + normal * side * (halfWidth - edgeHalf);
                    Vector2 b = center + normal * side * (halfWidth + edgeHalf);
                    float ya = TerrainHeight(a) + heightOffset;
                    float yb = TerrainHeight(b) + heightOffset;
                    vertices.Add(new Vector3(a.x, ya, a.y));
                    vertices.Add(new Vector3(b.x, yb, b.y));
                    uvs.Add(new Vector2(0.0f, samples[i].distanceAlong * 0.2f));
                    uvs.Add(new Vector2(1.0f, samples[i].distanceAlong * 0.2f));
                    if (i < samples.Count - 1)
                    {
                        int bi = startVertex + i * 2;
                        triangles.Add(bi);
                        triangles.Add(bi + 2);
                        triangles.Add(bi + 1);
                        triangles.Add(bi + 1);
                        triangles.Add(bi + 2);
                        triangles.Add(bi + 3);
                    }
                }
            }

            Mesh mesh = new Mesh();
            mesh.name = "Road_Gold_Edges";
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private Mesh BuildRibbonMesh(List<SamplePoint> samples, float halfWidth, float extraEdgeHeight, Func<Vector2, float> heightFunc)
        {
            List<Vector3> vertices = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<int> triangles = new List<int>();

            for (int i = 0; i < samples.Count; i++)
            {
                Vector2 normal = RouteNormal(samples, i);
                Vector2 center = samples[i].position;
                Vector2 left = center + normal * halfWidth;
                Vector2 right = center - normal * halfWidth;
                vertices.Add(new Vector3(left.x, heightFunc(left) + extraEdgeHeight, left.y));
                vertices.Add(new Vector3(right.x, heightFunc(right) + extraEdgeHeight, right.y));
                uvs.Add(new Vector2(0.0f, samples[i].distanceAlong * 0.18f));
                uvs.Add(new Vector2(1.0f, samples[i].distanceAlong * 0.18f));

                if (i < samples.Count - 1)
                {
                    int bi = i * 2;
                    triangles.Add(bi);
                    triangles.Add(bi + 2);
                    triangles.Add(bi + 1);
                    triangles.Add(bi + 1);
                    triangles.Add(bi + 2);
                    triangles.Add(bi + 3);
                }
            }

            Mesh mesh = new Mesh();
            mesh.name = "Fitted_Ribbon_Mesh";
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private void BuildNodes()
        {
            foreach (NodeInfo node in config.nodes)
            {
                Vector2 pos = GridToWorld(node.x, node.y);
                GameObject anchor = new GameObject(node.nodeId + "_ANCHOR_" + node.name);
                result.RegisterObjectCreation(anchor);
                anchor.transform.SetParent(nodeRoot, false);
                anchor.transform.position = new Vector3(pos.x, TerrainHeight(pos), pos.y);

                if (node.type == "DOCK" || node.type == "WATER_STATION")
                {
                    BuildWaterNode(node, anchor.transform, pos);
                }
                else
                {
                    bool usedImported = TryPlaceImportedNodeModel(node, anchor.transform, pos, false);
                    if (!usedImported)
                        BuildPavilion(anchor.transform, pos, node.type == "FINISH" ? 2.5f : 1.25f);
                }

                BuildNodeLabel(node, anchor.transform, pos);
            }
        }

        private bool TryPlaceImportedNodeModel(NodeInfo node, Transform parent, Vector2 pos, bool waterNode)
        {
            string[] names = new[]
            {
                node.code.ToString(),
                node.name,
                node.type
            };

            GameObject prefab = null;
            foreach (string name in names)
            {
                string[] guids = AssetDatabase.FindAssets(name + " t:GameObject");
                foreach (string guid in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) &&
                        !path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) &&
                        !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                        continue;

                    prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab != null)
                        break;
                }
                if (prefab != null)
                    break;
            }

            if (prefab == null)
                return false;

            GameObject model = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (model == null)
                return false;

            result.RegisterObjectCreation(model);
            model.name = node.nodeId + "_Model_" + node.name;
            model.transform.SetParent(parent, false);
            model.transform.position = new Vector3(pos.x, TerrainHeight(pos), pos.y);

            Bounds bounds = CombinedBounds(model);
            float currentFootprint = Mathf.Max(0.01f, Mathf.Max(bounds.size.x, bounds.size.z));
            float targetFootprint = waterNode ? 2.7f : (node.type == "FINISH" ? 6.5f : 2.4f);
            model.transform.localScale *= Mathf.Clamp(targetFootprint / currentFootprint, 0.003f, 8.0f);
            bounds = CombinedBounds(model);

            float targetBottom = waterNode ? WaterLevelAt(pos) - 0.08f : TerrainHeight(pos) - 0.045f;
            model.transform.position += Vector3.up * (targetBottom - bounds.min.y);
            FaceNearestLandRoute(model.transform, pos);
            return true;
        }

        private void BuildWaterNode(NodeInfo node, Transform parent, Vector2 pos)
        {
            TryPlaceImportedNodeModel(node, parent, pos, true);

            WaterHit hit = NearestWater(pos);
            Vector2 toWater = hit.valid ? (hit.closest - pos).normalized : Vector2.right;
            if (toWater.sqrMagnitude < 0.01f)
                toWater = Vector2.right;

            float waterLevel = WaterLevelAt(pos);
            Vector2 dockCenter = pos + toWater * 1.35f;
            GameObject dock = CreateCube("Dock_AboveWater", parent, new Vector3(dockCenter.x, waterLevel + 0.13f, dockCenter.y), new Vector3(0.72f, 0.12f, 2.65f), mats["Wood"]);
            dock.transform.rotation = Quaternion.LookRotation(new Vector3(toWater.x, 0, toWater.y), Vector3.up);

            Vector2 boatCenter = hit.valid ? hit.closest - toWater * 0.15f : pos + toWater * 2.0f;
            GameObject boat = CreateCube("Boat_OnWater", parent, new Vector3(boatCenter.x, waterLevel - 0.02f, boatCenter.y), new Vector3(1.05f, 0.28f, 2.2f), mats["Road"]);
            boat.transform.rotation = Quaternion.LookRotation(new Vector3(toWater.x, 0, toWater.y), Vector3.up);

            GameObject cabin = CreateCube("Boat_Cabin", parent, new Vector3(boatCenter.x, waterLevel + 0.22f, boatCenter.y), new Vector3(0.62f, 0.36f, 0.74f), mats["Wall"]);
            cabin.transform.rotation = boat.transform.rotation;

            Vector2 housePos = pos - toWater * 0.75f;
            BuildPavilion(parent, housePos, 1.05f);
        }

        private void BuildPavilion(Transform parent, Vector2 pos, float scale)
        {
            float ground = TerrainHeight(pos);
            GameObject baseObj = CreateCylinder("Foundation", parent, new Vector3(pos.x, ground + 0.07f, pos.y), new Vector3(1.05f * scale, 0.14f, 1.05f * scale), mats["Gold"]);
            GameObject body = CreateCube("House_Body", parent, new Vector3(pos.x, ground + 0.44f * scale, pos.y), new Vector3(0.96f * scale, 0.78f * scale, 0.96f * scale), mats["Wall"]);
            GameObject roof = CreateCylinder("Roof_Tier", parent, new Vector3(pos.x, ground + 0.91f * scale, pos.y), new Vector3(1.34f * scale, 0.22f * scale, 1.34f * scale), mats["RoofRed"]);
            roof.transform.rotation = Quaternion.Euler(0.0f, 45.0f, 0.0f);
            baseObj.transform.rotation = Quaternion.Euler(0.0f, 45.0f, 0.0f);
            body.transform.rotation = Quaternion.Euler(0.0f, 45.0f, 0.0f);
            FaceNearestLandRoute(body.transform, pos);
        }

        private void BuildNodeLabel(NodeInfo node, Transform parent, Vector2 pos)
        {
            GameObject label = new GameObject("Label_" + node.name);
            result.RegisterObjectCreation(label);
            label.transform.SetParent(parent, false);
            label.transform.position = new Vector3(pos.x, TerrainHeight(pos) + 2.05f, pos.y);
            TextMesh text = label.AddComponent<TextMesh>();
            text.text = node.name;
            text.characterSize = 0.36f;
            text.fontSize = 58;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.color = new Color(1.0f, 0.82f, 0.36f);
            MeshRenderer renderer = label.GetComponent<MeshRenderer>();
            if (renderer != null)
                renderer.sharedMaterial = mats["Label"];
        }

        private GameObject CreateCube(string name, Transform parent, Vector3 position, Vector3 scale, Material material)
        {
            GameObject obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            result.RegisterObjectCreation(obj);
            obj.name = name;
            obj.transform.SetParent(parent, false);
            obj.transform.position = position;
            obj.transform.localScale = scale;
            obj.GetComponent<MeshRenderer>().sharedMaterial = material;
            return obj;
        }

        private GameObject CreateCylinder(string name, Transform parent, Vector3 position, Vector3 scale, Material material)
        {
            GameObject obj = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            result.RegisterObjectCreation(obj);
            obj.name = name;
            obj.transform.SetParent(parent, false);
            obj.transform.position = position;
            obj.transform.localScale = scale;
            obj.GetComponent<MeshRenderer>().sharedMaterial = material;
            return obj;
        }

        private void ScatterNature()
        {
            Transform trees = NewChild(natureRoot, "Trees");
            Transform rocks = NewChild(natureRoot, "Rocks");
            Transform groundcover = NewChild(natureRoot, "Groundcover");
            UnityEngine.Random.InitState(20260611);

            int treeCount = 0;
            int rockCount = 0;
            int grassCount = 0;
            for (int i = 0; i < 620; i++)
            {
                Vector2 pos = new Vector2(UnityEngine.Random.Range(1.2f, config.grid.width - 1.2f), UnityEngine.Random.Range(1.2f, config.grid.height - 1.2f));
                float roadDist = DistanceToRoutes(pos, landRoutes);
                WaterHit water = NearestWater(pos);
                float nodeDist = DistanceToNodes(pos);

                if (roadDist < 1.25f || (water.valid && water.distance < WaterHalfWidth + 0.75f) || nodeDist < 1.35f)
                    continue;

                float roll = UnityEngine.Random.value;
                if (roll < 0.34f && treeCount < 90)
                {
                    BuildTree(trees, pos, UnityEngine.Random.Range(0.75f, 1.28f), treeCount % 2 == 0 ? mats["TreeLeafA"] : mats["TreeLeafB"]);
                    treeCount++;
                }
                else if (roll < 0.55f && rockCount < 75)
                {
                    BuildRock(rocks, pos, UnityEngine.Random.Range(0.38f, 0.85f));
                    rockCount++;
                }
                else if (grassCount < 180)
                {
                    BuildGrassClump(groundcover, pos, UnityEngine.Random.Range(0.22f, 0.45f));
                    grassCount++;
                }
            }
        }

        private void BuildTree(Transform parent, Vector2 pos, float scale, Material leafMaterial)
        {
            float ground = TerrainHeight(pos);
            GameObject trunk = CreateCylinder("Tree_Trunk", parent, new Vector3(pos.x, ground + 0.33f * scale, pos.y), new Vector3(0.13f * scale, 0.66f * scale, 0.13f * scale), mats["TreeTrunk"]);
            GameObject crown = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            result.RegisterObjectCreation(crown);
            crown.name = "Tree_Crown";
            crown.transform.SetParent(parent, false);
            crown.transform.position = new Vector3(pos.x, ground + 0.94f * scale, pos.y);
            crown.transform.localScale = new Vector3(0.95f * scale, 0.72f * scale, 0.95f * scale);
            crown.GetComponent<MeshRenderer>().sharedMaterial = leafMaterial;
            trunk.transform.rotation = Quaternion.Euler(0.0f, UnityEngine.Random.Range(0.0f, 360.0f), 0.0f);
        }

        private void BuildRock(Transform parent, Vector2 pos, float scale)
        {
            float ground = TerrainHeight(pos);
            GameObject rock = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            result.RegisterObjectCreation(rock);
            rock.name = "Ground_Rock";
            rock.transform.SetParent(parent, false);
            rock.transform.position = new Vector3(pos.x, ground + 0.16f * scale, pos.y);
            rock.transform.localScale = new Vector3(0.82f * scale, 0.32f * scale, 0.58f * scale);
            rock.transform.rotation = Quaternion.Euler(0.0f, UnityEngine.Random.Range(0.0f, 360.0f), 0.0f);
            rock.GetComponent<MeshRenderer>().sharedMaterial = mats["Rock"];
        }

        private void BuildGrassClump(Transform parent, Vector2 pos, float scale)
        {
            float ground = TerrainHeight(pos);
            GameObject clump = CreateCylinder("Grass_Clump", parent, new Vector3(pos.x, ground + 0.06f * scale, pos.y), new Vector3(scale, 0.12f * scale, scale), mats["TreeLeafB"]);
            clump.transform.rotation = Quaternion.Euler(0.0f, UnityEngine.Random.Range(0.0f, 360.0f), 0.0f);
        }

        private void SetupLightingAndCamera()
        {
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.45f, 0.50f, 0.56f);
            RenderSettings.ambientEquatorColor = new Color(0.32f, 0.35f, 0.30f);
            RenderSettings.ambientGroundColor = new Color(0.18f, 0.16f, 0.13f);
            RenderSettings.fog = true;
            RenderSettings.fogColor = new Color(0.48f, 0.58f, 0.58f);
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 55.0f;
            RenderSettings.fogEndDistance = 125.0f;

            Light[] lights = UnityEngine.Object.FindObjectsOfType<Light>();
            bool hasKey = false;
            foreach (Light light in lights)
            {
                if (light.type == LightType.Directional)
                {
                    result.RegisterObjectModification(light);
                    light.name = "KeyLight_MOBA_Sun";
                    light.intensity = 1.25f;
                    light.color = new Color(1.0f, 0.86f, 0.65f);
                    light.transform.rotation = Quaternion.Euler(48.0f, -35.0f, 0.0f);
                    hasKey = true;
                    break;
                }
            }

            if (!hasKey)
            {
                GameObject lightObj = new GameObject("KeyLight_MOBA_Sun");
                result.RegisterObjectCreation(lightObj);
                Light light = lightObj.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.25f;
                light.color = new Color(1.0f, 0.86f, 0.65f);
                lightObj.transform.rotation = Quaternion.Euler(48.0f, -35.0f, 0.0f);
            }

            mainCamera = Camera.main;
            if (mainCamera == null)
            {
                GameObject cameraObj = new GameObject("Main Camera");
                result.RegisterObjectCreation(cameraObj);
                mainCamera = cameraObj.AddComponent<Camera>();
                cameraObj.tag = "MainCamera";
            }
            else
            {
                result.RegisterObjectModification(mainCamera);
            }

            Vector3 center = new Vector3(config.grid.width * 0.5f, 0.0f, config.grid.height * 0.5f);
            Vector3 cameraPos = center + new Vector3(-36.0f, 48.0f, -42.0f);
            mainCamera.transform.position = cameraPos;
            mainCamera.transform.rotation = Quaternion.LookRotation(center + new Vector3(0.0f, 1.0f, 0.0f) - cameraPos, Vector3.up);
            mainCamera.orthographic = true;
            mainCamera.orthographicSize = 36.0f;
            mainCamera.nearClipPlane = 0.1f;
            mainCamera.farClipPlane = 180.0f;
            mainCamera.clearFlags = CameraClearFlags.Skybox;

            foreach (TextMesh label in root.GetComponentsInChildren<TextMesh>())
            {
                result.RegisterObjectModification(label.transform);
                label.transform.rotation = Quaternion.LookRotation(label.transform.position - mainCamera.transform.position, Vector3.up);
            }
        }

        private void BuildValidationMarker()
        {
            GameObject marker = new GameObject("Validation_Report_WaterRoadFit");
            result.RegisterObjectCreation(marker);
            marker.transform.SetParent(validationRoot, false);
            marker.transform.position = new Vector3(2.0f, 1.0f, 2.0f);
        }

        private void ValidateScene()
        {
            float minRoadClearance = float.MaxValue;
            float maxRoadClearance = float.MinValue;
            foreach (RouteData route in landRoutes)
            {
                List<SamplePoint> samples = Densify(route.points, 1.0f);
                float offset = route.routeType == "MOUNTAIN" ? MountainRoadOffset : RoadOffset;
                foreach (SamplePoint sample in samples)
                {
                    float terrain = TerrainHeight(sample.position);
                    float road = terrain + offset;
                    float clearance = road - terrain;
                    minRoadClearance = Mathf.Min(minRoadClearance, clearance);
                    maxRoadClearance = Mathf.Max(maxRoadClearance, clearance);
                }
            }

            float minWaterBankDelta = float.MaxValue;
            float maxWaterBankDelta = float.MinValue;
            float minDepth = float.MaxValue;
            float maxDepth = float.MinValue;
            foreach (RouteData route in waterRoutes)
            {
                List<SamplePoint> samples = Densify(route.points, 1.0f);
                foreach (SamplePoint sample in samples)
                {
                    Vector2 n = RouteNormal(samples, Mathf.Clamp(samples.IndexOf(sample), 0, samples.Count - 1));
                    Vector2 leftBank = sample.position + n * (WaterHalfWidth + 0.25f);
                    Vector2 rightBank = sample.position - n * (WaterHalfWidth + 0.25f);
                    float water = WaterLevelAt(sample.position);
                    float controlBank = Mathf.Min(TerrainHeight(leftBank), TerrainHeight(rightBank));
                    float bed = TerrainHeight(sample.position);
                    float bankDelta = water - controlBank;
                    float depth = water - bed;
                    minWaterBankDelta = Mathf.Min(minWaterBankDelta, bankDelta);
                    maxWaterBankDelta = Mathf.Max(maxWaterBankDelta, bankDelta);
                    minDepth = Mathf.Min(minDepth, depth);
                    maxDepth = Mathf.Max(maxDepth, depth);
                }
            }

            result.Log("Validation road clearance min/max: {0:F3}/{1:F3}", minRoadClearance, maxRoadClearance);
            result.Log("Validation water-to-bank delta min/max: {0:F3}/{1:F3} (target around -0.10)", minWaterBankDelta, maxWaterBankDelta);
            result.Log("Validation water depth min/max: {0:F3}/{1:F3}", minDepth, maxDepth);
        }

        private void DisableLikelyLegacyMapRenderers()
        {
            int disabled = 0;
            Renderer[] renderers = UnityEngine.Object.FindObjectsOfType<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || renderer.gameObject == null)
                    continue;
                if (root != null && renderer.transform.IsChildOf(root.transform))
                    continue;
                if (!renderer.enabled)
                    continue;

                string n = renderer.gameObject.name.ToLowerInvariant();
                bool likelyLayer =
                    n.Contains("tile_") ||
                    n.Contains("line_") ||
                    n.Contains("watersurface") ||
                    n.Contains("water_surface") ||
                    n.Contains("river") ||
                    n.Contains("roadmesh") ||
                    n.Contains("routemesh") ||
                    n.Contains("terrainmesh") ||
                    renderer.gameObject.name.Contains("水面") ||
                    renderer.gameObject.name.Contains("河面") ||
                    renderer.gameObject.name.Contains("河流") ||
                    renderer.gameObject.name.Contains("道路面") ||
                    renderer.gameObject.name.Contains("旧地形");

                bool keep =
                    n.Contains("obstacle") ||
                    renderer.gameObject.name.Contains("障碍") ||
                    n.Contains("node") ||
                    n.Contains("station") ||
                    n.Contains("tree") ||
                    n.Contains("rock");

                if (likelyLayer && !keep)
                {
                    result.RegisterObjectModification(renderer);
                    renderer.enabled = false;
                    disabled++;
                }
            }

            if (disabled > 0)
                result.Log("Disabled {0} likely legacy map renderers so the rebuilt terrain/road/water set is the visible source.", disabled);
        }

        private float TerrainHeight(Vector2 p)
        {
            float h = BaseHeight(p);

            WaterHit water = NearestWater(p);
            if (water.valid && water.distance < 16.0f)
            {
                float waterLevel = WaterLevelAt(water.closest);
                float bankTop = Mathf.Max(h, waterLevel + 0.10f);
                float d = water.distance;
                float inner = 1.16f;
                float buriedStart = 1.36f;

                if (d <= inner)
                {
                    h = waterLevel - RiverBedDepth;
                }
                else if (d <= buriedStart)
                {
                    float t = Mathf.SmoothStep(0.0f, 1.0f, Mathf.InverseLerp(inner, buriedStart, d));
                    h = Mathf.Lerp(waterLevel - RiverBedDepth, waterLevel - 0.04f, t);
                }
                else if (d <= WaterHalfWidth)
                {
                    float t = Mathf.SmoothStep(0.0f, 1.0f, Mathf.InverseLerp(buriedStart, WaterHalfWidth, d));
                    h = Mathf.Lerp(waterLevel - 0.04f, waterLevel + 0.10f, t);
                }
                else if (d <= WaterHalfWidth + BankWidth)
                {
                    float t = Mathf.SmoothStep(0.0f, 1.0f, Mathf.InverseLerp(WaterHalfWidth, WaterHalfWidth + BankWidth, d));
                    h = Mathf.Lerp(waterLevel + 0.10f, bankTop, t);
                }
                else if (d <= 10.0f)
                {
                    h = Mathf.Max(h, waterLevel + 0.08f);
                }
                else
                {
                    float forced = Mathf.Max(h, waterLevel + 0.08f);
                    float t = Mathf.SmoothStep(0.0f, 1.0f, Mathf.InverseLerp(10.0f, 16.0f, d));
                    h = Mathf.Lerp(forced, h, t);
                }
            }

            foreach (NodeInfo node in config.nodes)
            {
                Vector2 np = GridToWorld(node.x, node.y);
                float d = Vector2.Distance(p, np);
                float radius = (node.type == "FINISH") ? 3.3f : 1.75f;
                if (d < radius)
                {
                    float platform = BaseHeight(np);
                    if (node.type == "DOCK" || node.type == "WATER_STATION")
                        platform = Mathf.Max(platform, WaterLevelAt(np) + 0.12f);
                    float t = Mathf.SmoothStep(1.0f, 0.0f, Mathf.InverseLerp(radius * 0.55f, radius, d));
                    h = Mathf.Lerp(h, platform, t);
                }
            }

            return h;
        }

        private float BaseHeight(Vector2 p)
        {
            float h = 0.06f;
            h += (Mathf.PerlinNoise(p.x * 0.045f + 1.7f, p.y * 0.045f + 4.9f) - 0.5f) * 0.42f;
            h += (Mathf.PerlinNoise(p.x * 0.115f + 8.2f, p.y * 0.115f + 2.4f) - 0.5f) * 0.12f;

            float mountainDist = DistanceToRouteType(p, "MOUNTAIN");
            if (mountainDist < 8.0f)
            {
                float t = 1.0f - Mathf.SmoothStep(0.0f, 1.0f, mountainDist / 8.0f);
                h += t * (0.82f + Mathf.PerlinNoise(p.x * 0.18f, p.y * 0.18f) * 0.34f);
            }

            float branchDist = DistanceToRouteType(p, "BRANCH");
            if (branchDist < 4.2f)
            {
                float t = 1.0f - Mathf.SmoothStep(0.0f, 1.0f, branchDist / 4.2f);
                h += t * 0.22f;
            }

            return h;
        }

        private float WaterLevelAt(Vector2 p)
        {
            WaterHit hit = NearestWater(p);
            Vector2 c = hit.valid ? hit.closest : p;
            float level = 0.18f + (config.grid.height - c.y) * 0.004f;
            return Mathf.Clamp(level, 0.14f, 0.42f);
        }

        private Vector2 GridToWorld(float x, float y)
        {
            float tile = Mathf.Max(0.001f, config.grid.tileSize);
            return new Vector2(x * tile, y * tile);
        }

        private List<SamplePoint> Densify(List<Vector2> input, float step)
        {
            List<SamplePoint> samples = new List<SamplePoint>();
            float distance = 0.0f;
            for (int i = 0; i < input.Count - 1; i++)
            {
                Vector2 a = input[i];
                Vector2 b = input[i + 1];
                float len = Vector2.Distance(a, b);
                int count = Mathf.Max(1, Mathf.CeilToInt(len / step));
                for (int j = 0; j < count; j++)
                {
                    float t = (float)j / count;
                    Vector2 p = Vector2.Lerp(a, b, t);
                    if (samples.Count == 0 || Vector2.Distance(samples[samples.Count - 1].position, p) > 0.001f)
                    {
                        samples.Add(new SamplePoint { position = p, distanceAlong = distance + len * t });
                    }
                }
                distance += len;
            }
            samples.Add(new SamplePoint { position = input[input.Count - 1], distanceAlong = distance });
            return samples;
        }

        private Vector2 RouteNormal(List<SamplePoint> samples, int index)
        {
            int prev = Mathf.Max(0, index - 1);
            int next = Mathf.Min(samples.Count - 1, index + 1);
            Vector2 tangent = samples[next].position - samples[prev].position;
            if (tangent.sqrMagnitude < 0.0001f)
                tangent = Vector2.right;
            tangent.Normalize();
            return new Vector2(-tangent.y, tangent.x);
        }

        private float DistanceToRouteType(Vector2 p, string routeType)
        {
            float best = float.MaxValue;
            foreach (RouteData route in routes)
            {
                if (route.routeType != routeType)
                    continue;
                best = Mathf.Min(best, DistanceToPolyline(p, route.points));
            }
            return best;
        }

        private float DistanceToRoutes(Vector2 p, List<RouteData> targetRoutes)
        {
            float best = float.MaxValue;
            foreach (RouteData route in targetRoutes)
            {
                best = Mathf.Min(best, DistanceToPolyline(p, route.points));
            }
            return best;
        }

        private float DistanceToNodes(Vector2 p)
        {
            float best = float.MaxValue;
            foreach (NodeInfo node in config.nodes)
            {
                best = Mathf.Min(best, Vector2.Distance(p, GridToWorld(node.x, node.y)));
            }
            return best;
        }

        private float DistanceToPolyline(Vector2 p, List<Vector2> points)
        {
            float best = float.MaxValue;
            for (int i = 0; i < points.Count - 1; i++)
            {
                Vector2 closest;
                float t;
                float dist = SegmentDistance(p, points[i], points[i + 1], out closest, out t);
                best = Mathf.Min(best, dist);
            }
            return best;
        }

        private WaterHit NearestWater(Vector2 p)
        {
            WaterHit best = new WaterHit { valid = false, distance = float.MaxValue, closest = p };
            foreach (RouteData route in waterRoutes)
            {
                for (int i = 0; i < route.points.Count - 1; i++)
                {
                    Vector2 closest;
                    float t;
                    float dist = SegmentDistance(p, route.points[i], route.points[i + 1], out closest, out t);
                    if (dist < best.distance)
                    {
                        best.valid = true;
                        best.distance = dist;
                        best.closest = closest;
                    }
                }
            }
            return best;
        }

        private float SegmentDistance(Vector2 p, Vector2 a, Vector2 b, out Vector2 closest, out float t)
        {
            Vector2 ab = b - a;
            float denom = Mathf.Max(0.0001f, Vector2.Dot(ab, ab));
            t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / denom);
            closest = a + ab * t;
            return Vector2.Distance(p, closest);
        }

        private void FaceNearestLandRoute(Transform transform, Vector2 pos)
        {
            float best = float.MaxValue;
            Vector2 bestPoint = pos + Vector2.up;
            foreach (RouteData route in landRoutes)
            {
                for (int i = 0; i < route.points.Count - 1; i++)
                {
                    Vector2 closest;
                    float t;
                    float dist = SegmentDistance(pos, route.points[i], route.points[i + 1], out closest, out t);
                    if (dist < best)
                    {
                        best = dist;
                        bestPoint = closest;
                    }
                }
            }

            Vector2 dir = bestPoint - pos;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.LookRotation(new Vector3(dir.x, 0.0f, dir.y), Vector3.up);
        }

        private Bounds CombinedBounds(GameObject obj)
        {
            Renderer[] renderers = obj.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return new Bounds(obj.transform.position, Vector3.one);

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }
            return bounds;
        }

        private sealed class RouteData
        {
            public string edgeId;
            public string routeType;
            public List<Vector2> points;
        }

        private struct SamplePoint
        {
            public Vector2 position;
            public float distanceAlong;
        }

        private struct WaterHit
        {
            public bool valid;
            public float distance;
            public Vector2 closest;
        }
    }
}
