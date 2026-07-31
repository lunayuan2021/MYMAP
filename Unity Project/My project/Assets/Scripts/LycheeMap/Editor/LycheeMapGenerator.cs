using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LycheeMap.Editor
{
    public static class LycheeMapGenerator
    {
        const string RootName = "LycheeRouteMap";
        const string PrefabRoot = "Assets/Prefabs/LycheeMap";

        static readonly Color OfficialColor = new Color(0.77f, 0.66f, 0.51f);
        static readonly Color WaterColor = new Color(0.29f, 0.55f, 0.61f);
        static readonly Color MountainColor = new Color(0.55f, 0.45f, 0.33f);
        static readonly Color BranchColor = new Color(0.42f, 0.56f, 0.31f);
        static readonly Color GroundColor = new Color(0.45f, 0.58f, 0.38f);
        static readonly Color RockColor = new Color(0.50f, 0.46f, 0.40f);
        static readonly Color BambooColor = new Color(0.30f, 0.55f, 0.28f);
        static readonly Color VillageColor = new Color(0.62f, 0.50f, 0.38f);

        struct NodeDef
        {
            public string id, name;
            public NodeType type;
            public Vector3 pos;
            public RouteType[] routes;
        }

        public static GameObject Generate()
        {
            RemoveExisting();
            EnsurePrefabFolder();

            var root = new GameObject(RootName);
            var mapRoot = root.AddComponent<MapRoot>();

            var terrain = CreateLayer(root.transform, "Terrain_Base");
            var routes = CreateLayer(root.transform, "Routes");
            var nodes = CreateLayer(root.transform, "Nodes");
            var decor = CreateLayer(root.transform, "Decorations");
            var elevation = CreateLayer(root.transform, "Elevation_Data");

            mapRoot.Initialize(terrain, routes, nodes, decor, elevation);

            BuildTerrain(terrain);
            BuildRoutes(routes, elevation);
            BuildNodes(nodes);
            BuildDecorations(decor);
            SetupLighting(root);
            SavePrefabs(root);

            Selection.activeGameObject = root;
            return root;
        }

        static void RemoveExisting()
        {
            var existing = GameObject.Find(RootName);
            if (existing != null)
                Object.DestroyImmediate(existing);
        }

        static void EnsurePrefabFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Prefabs"))
                AssetDatabase.CreateFolder("Assets", "Prefabs");
            if (!AssetDatabase.IsValidFolder(PrefabRoot))
                AssetDatabase.CreateFolder("Assets/Prefabs", "LycheeMap");
        }

        static Transform CreateLayer(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            return go.transform;
        }

        static void BuildTerrain(Transform parent)
        {
            var ground = CreateTerrainMesh("GroundMesh", 120, 80, GroundColor, parent);
            CreateHillCluster(parent, new Vector3(85, 0, 25), 18f, 6f);
            CreateHillCluster(parent, new Vector3(70, 0, 45), 14f, 4.5f);
            CreateCliffWall(parent, new Vector3(78, 0, 35), 30f, 8f);
            CreateRiverBed(parent);
        }

        static GameObject CreateTerrainMesh(string name, float width, float depth, Color color, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            go.transform.localPosition = new Vector3(width * 0.5f, 0, depth * 0.5f);

            var mesh = new Mesh { name = name + "_Mesh" };
            int resX = 48, resZ = 32;
            var verts = new Vector3[resX * resZ];
            var uvs = new Vector2[resX * resZ];
            var tris = new int[(resX - 1) * (resZ - 1) * 6];

            for (int z = 0; z < resZ; z++)
            {
                for (int x = 0; x < resX; x++)
                {
                    float px = (x / (float)(resX - 1)) * width;
                    float pz = (z / (float)(resZ - 1)) * depth;
                    float h = SampleTerrainHeight(px, pz);
                    verts[z * resX + x] = new Vector3(px - width * 0.5f, h, pz - depth * 0.5f);
                    uvs[z * resX + x] = new Vector2(x / (float)(resX - 1), z / (float)(resZ - 1));
                }
            }

            int ti = 0;
            for (int z = 0; z < resZ - 1; z++)
            {
                for (int x = 0; x < resX - 1; x++)
                {
                    int a = z * resX + x, b = a + 1, c = a + resX, d = c + 1;
                    tris[ti++] = a; tris[ti++] = c; tris[ti++] = b;
                    tris[ti++] = b; tris[ti++] = c; tris[ti++] = d;
                }
            }

            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = CreateMat(color, 0.1f);
            return go;
        }

        static float SampleTerrainHeight(float x, float z)
        {
            float mountain = Mathf.Exp(-Mathf.Pow((x - 80f) / 18f, 2) - Mathf.Pow((z - 30f) / 14f, 2)) * 5f;
            float canyon = Mathf.Exp(-Mathf.Pow((x - 55f) / 10f, 2)) * Mathf.Sin(z * 0.08f) * 1.5f;
            float riverBed = -Mathf.Exp(-Mathf.Pow((x - 42f) / 8f, 2) - Mathf.Pow((z - 40f) / 25f, 2)) * 1.2f;
            float westPlain = Mathf.SmoothStep(0.3f, 0f, (x - 25f) / 30f) * 0.3f;
            return mountain + canyon - riverBed + westPlain;
        }

        static void CreateHillCluster(Transform parent, Vector3 center, float radius, float height)
        {
            var hill = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            hill.name = "Hill";
            hill.transform.SetParent(parent);
            hill.transform.position = center + Vector3.up * height * 0.3f;
            hill.transform.localScale = new Vector3(radius, height, radius * 0.8f);
            Object.DestroyImmediate(hill.GetComponent<Collider>());
            hill.GetComponent<Renderer>().sharedMaterial = CreateMat(RockColor, 0.05f);
        }

        static void CreateCliffWall(Transform parent, Vector3 basePos, float length, float height)
        {
            var cliff = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cliff.name = "Cliff";
            cliff.transform.SetParent(parent);
            cliff.transform.position = basePos + new Vector3(0, height * 0.5f, 0);
            cliff.transform.localScale = new Vector3(3f, height, length);
            cliff.transform.rotation = Quaternion.Euler(0, 25, 0);
            Object.DestroyImmediate(cliff.GetComponent<Collider>());
            cliff.GetComponent<Renderer>().sharedMaterial = CreateMat(RockColor * 0.85f, 0f);
        }

        static void CreateRiverBed(Transform parent)
        {
            var bed = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bed.name = "RiverBed";
            bed.transform.SetParent(parent);
            bed.transform.position = new Vector3(42, -0.6f, 40);
            bed.transform.localScale = new Vector3(10, 0.3f, 65);
            bed.transform.rotation = Quaternion.Euler(0, -12, 0);
            Object.DestroyImmediate(bed.GetComponent<Collider>());
            bed.GetComponent<Renderer>().sharedMaterial = CreateMat(new Color(0.35f, 0.42f, 0.38f), 0.15f);
        }

        static void BuildRoutes(Transform parent, Transform elevationParent)
        {
            var officialPts = new List<Vector3> {
                new(8,0.15f,72), new(18,0.15f,65), new(30,0.15f,58), new(42,0.15f,52),
                new(55,0.15f,46), new(68,0.15f,38), new(80,0.15f,30), new(92,0.15f,22), new(105,0.15f,12)
            };
            var waterPts = new List<Vector3> {
                new(12,0.05f,75), new(22,0.02f,68), new(32,-0.1f,60), new(40,-0.2f,52),
                new(45,-0.25f,44), new(48,-0.2f,36), new(50,-0.15f,28), new(55,-0.1f,20), new(62,0f,12)
            };
            var mountainPts = new List<Vector3> {
                new(20,0.3f,62), new(32,1.2f,55), new(45,2.5f,48), new(58,4f,40),
                new(68,5.5f,33), new(78,7f,26), new(88,7.5f,20), new(98,6f,14)
            };
            var branchPts = new List<Vector3> {
                new(25,0.2f,68), new(35,0.4f,60), new(48,0.6f,55), new(52,0.5f,48),
                new(58,0.4f,42), new(72,0.3f,35), new(85,0.25f,28), new(95,0.2f,18)
            };

            CreateRoute(parent, "OfficialRoad", RouteType.OfficialRoad, officialPts, 3.2f, OfficialColor, new Color(0.95f, 0.82f, 0.55f));
            CreateRoute(parent, "WaterRoute", RouteType.WaterRoute, waterPts, 4.5f, WaterColor, new Color(0.45f, 0.78f, 0.88f));
            CreateRoute(parent, "MountainRoad", RouteType.MountainRoad, mountainPts, 2.4f, MountainColor, new Color(0.78f, 0.62f, 0.40f));
            CreateRoute(parent, "BranchRoad", RouteType.BranchRoad, branchPts, 1.8f, BranchColor, new Color(0.55f, 0.78f, 0.38f));

            CreateElevationWaypoints(elevationParent, RouteType.OfficialRoad, officialPts);
            CreateElevationWaypoints(elevationParent, RouteType.WaterRoute, waterPts);
            CreateElevationWaypoints(elevationParent, RouteType.MountainRoad, mountainPts);
            CreateElevationWaypoints(elevationParent, RouteType.BranchRoad, branchPts);
        }

        static void CreateRoute(Transform parent, string name, RouteType type, List<Vector3> points, float width, Color baseCol, Color highlightCol)
        {
            var routeGo = new GameObject(name);
            routeGo.transform.SetParent(parent);
            var ctrl = routeGo.AddComponent<RouteController>();
            ctrl.Initialize(type, baseCol, highlightCol);

            var pathMesh = BuildPathMesh(points, width, 0.08f);
            var pathGo = new GameObject("PathMesh");
            pathGo.transform.SetParent(routeGo.transform);
            var mf = pathGo.AddComponent<MeshFilter>();
            mf.sharedMesh = pathMesh;
            var mr = pathGo.AddComponent<MeshRenderer>();
            mr.sharedMaterial = CreateMat(baseCol, type == RouteType.WaterRoute ? 0.6f : 0.2f);

            if (type == RouteType.OfficialRoad)
                AddWheelRuts(routeGo.transform, points);
            if (type == RouteType.MountainRoad)
                AddPlankSections(routeGo.transform, points);
            if (type == RouteType.WaterRoute)
                AddWaterFlow(routeGo.transform, points);
            if (type == RouteType.BranchRoad)
                AddStoneChannels(routeGo.transform, points);
        }

        static Mesh BuildPathMesh(List<Vector3> points, float width, float yOffset)
        {
            var mesh = new Mesh();
            int segs = points.Count - 1;
            var verts = new Vector3[segs * 4];
            var tris = new int[segs * 6];
            var uvs = new Vector2[segs * 4];

            for (int i = 0; i < segs; i++)
            {
                Vector3 a = points[i], b = points[i + 1];
                Vector3 dir = (b - a).normalized;
                Vector3 right = Vector3.Cross(Vector3.up, dir).normalized * width * 0.5f;
                int vi = i * 4;
                verts[vi] = a + right + Vector3.up * yOffset;
                verts[vi + 1] = a - right + Vector3.up * yOffset;
                verts[vi + 2] = b + right + Vector3.up * yOffset;
                verts[vi + 3] = b - right + Vector3.up * yOffset;
                uvs[vi] = new Vector2(0, i); uvs[vi + 1] = new Vector2(1, i);
                uvs[vi + 2] = new Vector2(0, i + 1); uvs[vi + 3] = new Vector2(1, i + 1);
                int ti = i * 6;
                tris[ti] = vi; tris[ti + 1] = vi + 2; tris[ti + 2] = vi + 1;
                tris[ti + 3] = vi + 1; tris[ti + 4] = vi + 2; tris[ti + 5] = vi + 3;
            }

            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.uv = uvs;
            mesh.RecalculateNormals();
            return mesh;
        }

        static void AddWheelRuts(Transform parent, List<Vector3> points)
        {
            for (int i = 0; i < points.Count - 1; i += 2)
            {
                var rut = GameObject.CreatePrimitive(PrimitiveType.Cube);
                rut.name = "WheelRut";
                rut.transform.SetParent(parent);
                Vector3 mid = (points[i] + points[i + 1]) * 0.5f;
                rut.transform.position = mid + Vector3.up * 0.05f;
                rut.transform.localScale = new Vector3(0.15f, 0.02f, Vector3.Distance(points[i], points[i + 1]) * 0.8f);
                rut.transform.LookAt(points[i + 1]);
                Object.DestroyImmediate(rut.GetComponent<Collider>());
                rut.GetComponent<Renderer>().sharedMaterial = CreateMat(new Color(0.55f, 0.45f, 0.32f), 0f);
            }
        }

        static void AddPlankSections(Transform parent, List<Vector3> points)
        {
            for (int i = 1; i < points.Count - 1; i += 2)
            {
                var plank = GameObject.CreatePrimitive(PrimitiveType.Cube);
                plank.name = "PlankWalkway";
                plank.transform.SetParent(parent);
                plank.transform.position = points[i] + Vector3.up * 0.15f;
                plank.transform.localScale = new Vector3(1.6f, 0.1f, 3f);
                if (i + 1 < points.Count) plank.transform.LookAt(points[i + 1]);
                Object.DestroyImmediate(plank.GetComponent<Collider>());
                plank.GetComponent<Renderer>().sharedMaterial = CreateMat(new Color(0.58f, 0.42f, 0.28f), 0.1f);
            }
        }

        static void AddWaterFlow(Transform parent, List<Vector3> points)
        {
            var flow = new GameObject("WaterFlow");
            flow.transform.SetParent(parent);
            for (int i = 0; i < points.Count - 1; i++)
            {
                var ripple = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                ripple.name = "Ripple";
                ripple.transform.SetParent(flow.transform);
                Vector3 mid = (points[i] + points[i + 1]) * 0.5f;
                ripple.transform.position = mid + Vector3.up * 0.02f;
                float segLen = Vector3.Distance(points[i], points[i + 1]);
                ripple.transform.localScale = new Vector3(1.5f + i * 0.1f, 0.02f, segLen * 0.3f);
                ripple.transform.LookAt(points[i + 1]);
                Object.DestroyImmediate(ripple.GetComponent<Collider>());
                var mat = CreateMat(new Color(0.35f, 0.65f, 0.72f, 0.6f), 0.85f);
                ripple.GetComponent<Renderer>().sharedMaterial = mat;
            }
        }

        static void AddStoneChannels(Transform parent, List<Vector3> points)
        {
            for (int i = 0; i < points.Count - 1; i += 3)
            {
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.name = "LowStoneWall";
                wall.transform.SetParent(parent);
                Vector3 mid = (points[i] + points[Mathf.Min(i + 1, points.Count - 1)]) * 0.5f;
                wall.transform.position = mid + Vector3.right * 1.2f + Vector3.up * 0.2f;
                wall.transform.localScale = new Vector3(0.2f, 0.4f, 2f);
                Object.DestroyImmediate(wall.GetComponent<Collider>());
                wall.GetComponent<Renderer>().sharedMaterial = CreateMat(new Color(0.48f, 0.46f, 0.42f), 0f);
            }
        }

        static void CreateElevationWaypoints(Transform parent, RouteType route, List<Vector3> points)
        {
            var group = parent.Find(route.ToString());
            if (group == null)
            {
                var g = new GameObject(route.ToString());
                g.transform.SetParent(parent);
                group = g.transform;
            }

            for (int i = 0; i < points.Count; i++)
            {
                float slope = 0f;
                if (i > 0)
                {
                    float dy = points[i].y - points[i - 1].y;
                    float dx = Vector3.Distance(new Vector3(points[i].x, 0, points[i].z), new Vector3(points[i - 1].x, 0, points[i - 1].z));
                    slope = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
                }

                var wpGo = new GameObject();
                wpGo.transform.SetParent(group);
                wpGo.transform.position = points[i];
                var wp = wpGo.AddComponent<ElevationWaypoint>();
                wp.Initialize($"{route}_{i:D2}", points[i].y, slope, route, i);
            }
        }

        static void BuildNodes(Transform parent)
        {
            var defs = new NodeDef[]
            {
                new() { id="lingnan_orchard", name="岭南荔枝园", type=NodeType.LycheeOrchard, pos=new(8,0.5f,72), routes=new[]{RouteType.OfficialRoad,RouteType.WaterRoute,RouteType.BranchRoad} },
                new() { id="loading_dock", name="装载码头", type=NodeType.LoadingDock, pos=new(12,0.3f,75), routes=new[]{RouteType.WaterRoute} },
                new() { id="lingnan_station", name="岭南驿站", type=NodeType.Station, pos=new(18,0.4f,65), routes=new[]{RouteType.OfficialRoad} },
                new() { id="water_gate", name="三江口码头", type=NodeType.Dock, pos=new(32,0.1f,60), routes=new[]{RouteType.WaterRoute} },
                new() { id="water_bridge", name="水路桥闸", type=NodeType.Checkpoint, pos=new(40,0.2f,52), routes=new[]{RouteType.WaterRoute,RouteType.OfficialRoad} },
                new() { id="water_urgent", name="水路急令点", type=NodeType.UrgentDecree, pos=new(48,0.1f,36), routes=new[]{RouteType.WaterRoute} },
                new() { id="guandao_pass", name="官道起点关隘", type=NodeType.Checkpoint, pos=new(30,0.3f,58), routes=new[]{RouteType.OfficialRoad} },
                new() { id="shaoguan_station", name="韶关驿站", type=NodeType.Station, pos=new(42,0.4f,52), routes=new[]{RouteType.OfficialRoad,RouteType.BranchRoad} },
                new() { id="guandao_urgent", name="官道急令点", type=NodeType.UrgentDecree, pos=new(55,0.4f,46), routes=new[]{RouteType.OfficialRoad} },
                new() { id="mid_station", name="官道中驿", type=NodeType.Station, pos=new(68,0.4f,38), routes=new[]{RouteType.OfficialRoad} },
                new() { id="chenzhou_station", name="郴州驿站", type=NodeType.Station, pos=new(52,0.5f,48), routes=new[]{RouteType.BranchRoad} },
                new() { id="mountain_pass", name="山路入口关隘", type=NodeType.Checkpoint, pos=new(20,0.5f,62), routes=new[]{RouteType.MountainRoad,RouteType.BranchRoad} },
                new() { id="plank_urgent", name="栈道急令点", type=NodeType.UrgentDecree, pos=new(58,4.2f,40), routes=new[]{RouteType.MountainRoad} },
                new() { id="canyon_station", name="峡谷驿站", type=NodeType.Station, pos=new(68,5.6f,33), routes=new[]{RouteType.MountainRoad} },
                new() { id="bamboo_village", name="竹林村落", type=NodeType.Station, pos=new(58,0.5f,42), routes=new[]{RouteType.BranchRoad} },
                new() { id="huashan_pass", name="华山关隘", type=NodeType.Checkpoint, pos=new(78,7.2f,26), routes=new[]{RouteType.MountainRoad,RouteType.OfficialRoad} },
                new() { id="weihe_dock", name="渭水码头", type=NodeType.Dock, pos=new(62,0.2f,12), routes=new[]{RouteType.WaterRoute} },
                new() { id="final_station", name="官道终站", type=NodeType.Station, pos=new(92,0.5f,22), routes=new[]{RouteType.OfficialRoad} },
                new() { id="branch_urgent", name="支路急令点", type=NodeType.UrgentDecree, pos=new(72,0.4f,35), routes=new[]{RouteType.BranchRoad} },
                new() { id="palace_gate", name="长安宫门", type=NodeType.PalaceGate, pos=new(105,0.6f,12), routes=new[]{RouteType.OfficialRoad,RouteType.WaterRoute} },
                new() { id="palace_road", name="宫道入口", type=NodeType.PalaceRoad, pos=new(108,0.5f,8), routes=new[]{RouteType.OfficialRoad} },
            };

            foreach (var d in defs)
                CreateNode(parent, d);
        }

        static void CreateNode(Transform parent, NodeDef def)
        {
            var nodeGo = new GameObject($"Node_{def.id}");
            nodeGo.transform.SetParent(parent);
            nodeGo.transform.position = def.pos;
            var node = nodeGo.AddComponent<MapNode>();
            node.Initialize(def.id, def.name, def.type, def.routes);

            var marker = BuildNodeMarker(def.type);
            marker.transform.SetParent(nodeGo.transform);
            marker.transform.localPosition = Vector3.zero;

            var indicator = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            indicator.name = "Indicator";
            indicator.transform.SetParent(nodeGo.transform);
            indicator.transform.localPosition = Vector3.up * 3f;
            indicator.transform.localScale = Vector3.one * 0.5f;
            indicator.SetActive(false);
            Object.DestroyImmediate(indicator.GetComponent<Collider>());
            indicator.GetComponent<Renderer>().sharedMaterial = CreateMat(new Color(1f, 0.85f, 0.2f), 0.3f);
        }

        static GameObject BuildNodeMarker(NodeType type)
        {
            GameObject marker;
            switch (type)
            {
                case NodeType.LycheeOrchard:
                    marker = new GameObject("LycheeOrchardMarker");
                    for (int i = 0; i < 5; i++)
                    {
                        var tree = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        tree.transform.SetParent(marker.transform);
                        tree.transform.localPosition = new Vector3(i * 0.8f - 1.6f, 0.6f, (i % 2) * 0.5f);
                        tree.transform.localScale = new Vector3(0.8f, 1.2f, 0.8f);
                        Object.DestroyImmediate(tree.GetComponent<Collider>());
                        tree.GetComponent<Renderer>().sharedMaterial = CreateMat(new Color(0.25f, 0.55f, 0.22f), 0f);
                    }
                    var load = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    load.transform.SetParent(marker.transform);
                    load.transform.localPosition = new Vector3(0, 0.3f, -1.5f);
                    load.transform.localScale = new Vector3(2f, 0.6f, 1.5f);
                    Object.DestroyImmediate(load.GetComponent<Collider>());
                    load.GetComponent<Renderer>().sharedMaterial = CreateMat(new Color(0.65f, 0.48f, 0.32f), 0.1f);
                    break;
                case NodeType.PalaceGate:
                    marker = new GameObject("PalaceGateMarker");
                    var gateL = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    gateL.transform.SetParent(marker.transform);
                    gateL.transform.localPosition = new Vector3(-1.5f, 1.5f, 0);
                    gateL.transform.localScale = new Vector3(0.8f, 3f, 0.8f);
                    var gateR = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    gateR.transform.SetParent(marker.transform);
                    gateR.transform.localPosition = new Vector3(1.5f, 1.5f, 0);
                    gateR.transform.localScale = new Vector3(0.8f, 3f, 0.8f);
                    var roof = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    roof.transform.SetParent(marker.transform);
                    roof.transform.localPosition = new Vector3(0, 3.2f, 0);
                    roof.transform.localScale = new Vector3(4f, 0.4f, 1.2f);
                    foreach (Transform c in marker.transform)
                    {
                        Object.DestroyImmediate(c.GetComponent<Collider>());
                        c.GetComponent<Renderer>().sharedMaterial = CreateMat(new Color(0.55f, 0.18f, 0.15f), 0.15f);
                    }
                    break;
                case NodeType.LoadingDock:
                case NodeType.Dock:
                    marker = new GameObject("DockMarker");
                    var pier = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    pier.transform.SetParent(marker.transform);
                    pier.transform.localPosition = new Vector3(0, 0.2f, 0);
                    pier.transform.localScale = new Vector3(3f, 0.4f, 1.5f);
                    Object.DestroyImmediate(pier.GetComponent<Collider>());
                    pier.GetComponent<Renderer>().sharedMaterial = CreateMat(new Color(0.50f, 0.40f, 0.30f), 0.1f);
                    break;
                case NodeType.UrgentDecree:
                    marker = new GameObject("UrgentDecreeMarker");
                    var pavilion = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    pavilion.transform.SetParent(marker.transform);
                    pavilion.transform.localPosition = Vector3.up * 0.8f;
                    pavilion.transform.localScale = new Vector3(1.2f, 0.8f, 1.2f);
                    var roofP = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    roofP.transform.SetParent(marker.transform);
                    roofP.transform.localPosition = Vector3.up * 1.6f;
                    roofP.transform.localScale = new Vector3(2f, 0.3f, 2f);
                    Object.DestroyImmediate(pavilion.GetComponent<Collider>());
                    Object.DestroyImmediate(roofP.GetComponent<Collider>());
                    pavilion.GetComponent<Renderer>().sharedMaterial = CreateMat(new Color(0.60f, 0.25f, 0.20f), 0.1f);
                    roofP.GetComponent<Renderer>().sharedMaterial = CreateMat(new Color(0.45f, 0.20f, 0.15f), 0.1f);
                    break;
                case NodeType.Checkpoint:
                    marker = new GameObject("CheckpointMarker");
                    var tower = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    tower.transform.SetParent(marker.transform);
                    tower.transform.localPosition = Vector3.up * 1.2f;
                    tower.transform.localScale = new Vector3(2f, 2.4f, 1.5f);
                    var wall2 = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    wall2.transform.SetParent(marker.transform);
                    wall2.transform.localPosition = new Vector3(0, 0.6f, -1.5f);
                    wall2.transform.localScale = new Vector3(4f, 1.2f, 0.4f);
                    Object.DestroyImmediate(tower.GetComponent<Collider>());
                    Object.DestroyImmediate(wall2.GetComponent<Collider>());
                    tower.GetComponent<Renderer>().sharedMaterial = CreateMat(new Color(0.48f, 0.44f, 0.40f), 0.05f);
                    wall2.GetComponent<Renderer>().sharedMaterial = CreateMat(new Color(0.48f, 0.44f, 0.40f), 0.05f);
                    break;
                default:
                    marker = new GameObject("StationMarker");
                    var baseB = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    baseB.transform.SetParent(marker.transform);
                    baseB.transform.localPosition = Vector3.up * 0.6f;
                    baseB.transform.localScale = new Vector3(2.5f, 1.2f, 2f);
                    var roofB = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    roofB.transform.SetParent(marker.transform);
                    roofB.transform.localPosition = Vector3.up * 1.5f;
                    roofB.transform.localScale = new Vector3(3f, 0.4f, 2.5f);
                    Object.DestroyImmediate(baseB.GetComponent<Collider>());
                    Object.DestroyImmediate(roofB.GetComponent<Collider>());
                    baseB.GetComponent<Renderer>().sharedMaterial = CreateMat(new Color(0.58f, 0.45f, 0.32f), 0.1f);
                    roofB.GetComponent<Renderer>().sharedMaterial = CreateMat(new Color(0.40f, 0.28f, 0.20f), 0.1f);
                    break;
            }
            return marker;
        }

        static void BuildDecorations(Transform parent)
        {
            var river = CreateLayer(parent, "River");
            BuildRiverSurface(river);
            var bridges = CreateLayer(parent, "Bridges");
            CreateBridge(bridges, new Vector3(40, 0.5f, 52), 6f);
            CreateBridge(bridges, new Vector3(55, 0.3f, 28), 5f);
            var bamboo = CreateLayer(parent, "BambooGroves");
            CreateBambooGrove(bamboo, new Vector3(52, 0.3f, 48), 8);
            CreateBambooGrove(bamboo, new Vector3(58, 0.4f, 42), 6);
            CreateBambooGrove(bamboo, new Vector3(35, 0.3f, 60), 5);
            var villages = CreateLayer(parent, "Villages");
            CreateVillage(villages, new Vector3(58, 0.3f, 42));
            CreateVillage(villages, new Vector3(48, 0.3f, 55), 2);
        }

        static void BuildRiverSurface(Transform parent)
        {
            var pts = new List<Vector3> {
                new(12,0.08f,75), new(22,0.05f,68), new(32,0f,60), new(40,-0.05f,52),
                new(45,-0.1f,44), new(48,-0.08f,36), new(50,-0.05f,28), new(55,0f,20), new(62,0.05f,12)
            };
            var mesh = BuildPathMesh(pts, 5.5f, 0f);
            var go = new GameObject("RiverSurface");
            go.transform.SetParent(parent);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = CreateMat(new Color(0.25f, 0.50f, 0.58f, 0.75f), 0.9f);
        }

        static void CreateBridge(Transform parent, Vector3 pos, float length)
        {
            var bridge = new GameObject("Bridge");
            bridge.transform.SetParent(parent);
            bridge.transform.position = pos;
            var deck = GameObject.CreatePrimitive(PrimitiveType.Cube);
            deck.transform.SetParent(bridge.transform);
            deck.transform.localPosition = Vector3.up * 0.6f;
            deck.transform.localScale = new Vector3(2.5f, 0.2f, length);
            for (int i = -1; i <= 1; i += 2)
            {
                var pillar = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                pillar.transform.SetParent(bridge.transform);
                pillar.transform.localPosition = new Vector3(i * 1f, 0.3f, 0);
                pillar.transform.localScale = new Vector3(0.3f, 0.6f, 0.3f);
                Object.DestroyImmediate(pillar.GetComponent<Collider>());
                pillar.GetComponent<Renderer>().sharedMaterial = CreateMat(new Color(0.45f, 0.40f, 0.35f), 0f);
            }
            Object.DestroyImmediate(deck.GetComponent<Collider>());
            deck.GetComponent<Renderer>().sharedMaterial = CreateMat(new Color(0.55f, 0.42f, 0.30f), 0.15f);
        }

        static void CreateBambooGrove(Transform parent, Vector3 center, int count)
        {
            var grove = new GameObject("BambooGrove");
            grove.transform.SetParent(parent);
            grove.transform.position = center;
            for (int i = 0; i < count; i++)
            {
                var bamboo = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                bamboo.transform.SetParent(grove.transform);
                float angle = i * (360f / count);
                bamboo.transform.localPosition = new Vector3(Mathf.Cos(angle * Mathf.Deg2Rad) * 2f, 0.75f, Mathf.Sin(angle * Mathf.Deg2Rad) * 2f);
                bamboo.transform.localScale = new Vector3(0.08f, 1.5f, 0.08f);
                Object.DestroyImmediate(bamboo.GetComponent<Collider>());
                bamboo.GetComponent<Renderer>().sharedMaterial = CreateMat(BambooColor, 0.05f);
            }
        }

        static void CreateVillage(Transform parent, Vector3 center, int houseCount = 3)
        {
            var village = new GameObject("Village");
            village.transform.SetParent(parent);
            village.transform.position = center;
            for (int i = 0; i < houseCount; i++)
            {
                var house = GameObject.CreatePrimitive(PrimitiveType.Cube);
                house.transform.SetParent(village.transform);
                house.transform.localPosition = new Vector3(i * 2.5f - houseCount, 0.5f, (i % 2) * 1.5f);
                house.transform.localScale = new Vector3(1.8f, 1f, 1.5f);
                var roof = GameObject.CreatePrimitive(PrimitiveType.Cube);
                roof.transform.SetParent(house.transform);
                roof.transform.localPosition = Vector3.up * 0.7f;
                roof.transform.localScale = new Vector3(1.1f, 0.3f, 1.1f);
                Object.DestroyImmediate(house.GetComponent<Collider>());
                Object.DestroyImmediate(roof.GetComponent<Collider>());
                house.GetComponent<Renderer>().sharedMaterial = CreateMat(VillageColor, 0.1f);
                roof.GetComponent<Renderer>().sharedMaterial = CreateMat(new Color(0.38f, 0.28f, 0.20f), 0.1f);
            }
        }

        static void SetupLighting(GameObject root)
        {
            var lightGo = new GameObject("MapDirectionalLight");
            lightGo.transform.SetParent(root.transform);
            lightGo.transform.rotation = Quaternion.Euler(50, -30, 0);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.95f, 0.85f);
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
        }

        static void SavePrefabs(GameObject root)
        {
            SaveLayerPrefab(root.transform.Find("Terrain_Base"), "Terrain_Base");
            SaveLayerPrefab(root.transform.Find("Routes"), "Routes");
            SaveLayerPrefab(root.transform.Find("Nodes"), "Nodes");
            SaveLayerPrefab(root.transform.Find("Decorations"), "Decorations");
            SaveLayerPrefab(root.transform.Find("Elevation_Data"), "Elevation_Data");

            var routeNames = new[] { "OfficialRoad", "WaterRoute", "MountainRoad", "BranchRoad" };
            var routes = root.transform.Find("Routes");
            if (routes != null)
            {
                foreach (var rn in routeNames)
                {
                    var child = routes.Find(rn);
                    if (child != null)
                        PrefabUtility.SaveAsPrefabAsset(child.gameObject, $"{PrefabRoot}/Route_{rn}.prefab");
                }
            }

            PrefabUtility.SaveAsPrefabAsset(root, $"{PrefabRoot}/LycheeRouteMap.prefab");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        static void SaveLayerPrefab(Transform layer, string name)
        {
            if (layer == null) return;
            PrefabUtility.SaveAsPrefabAsset(layer.gameObject, $"{PrefabRoot}/{name}.prefab");
        }

        static Material CreateMat(Color color, float smoothness)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", smoothness);
            if (color.a < 1f)
            {
                if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1);
                mat.SetFloat("_Mode", 3);
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.renderQueue = 3000;
            }
            return mat;
        }
    }
}
