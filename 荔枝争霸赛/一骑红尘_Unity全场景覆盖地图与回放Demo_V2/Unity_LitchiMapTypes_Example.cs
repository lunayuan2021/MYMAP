using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable] public class UiMapConfig {
    public string schemaVersion;
    public string mapId;
    public GridInfo grid;
    public MapLayer map;
    public List<NodeInfo> nodes;
    public List<EdgeInfo> edges;
    public List<RoutePath> routePaths;
}
[Serializable] public class GridInfo { public int width; public int height; public float tileSize; public string origin; }
[Serializable] public class MapLayer { public string encoding; public string data; }
[Serializable] public class NodeInfo { public string nodeId; public int code; public string name; public string type; public int x; public int y; public string icon; }
[Serializable] public class EdgeInfo { public string edgeId; public string fromNodeId; public string toNodeId; public string routeType; public float distance; public string pathId; }
[Serializable] public class RoutePath { public string pathId; public string edgeId; public List<GridPoint> points; }
[Serializable] public class GridPoint { public float x; public float y; }

public static class LitchiUnityMapUtil {
    public static Vector3 GridToWorld2D(float x, float y, float tileSize) {
        return new Vector3(x * tileSize, -y * tileSize, 0);
    }
    public static Vector3 GridToWorld3D(float x, float y, float tileSize) {
        return new Vector3(x * tileSize, 0, y * tileSize);
    }
    public static int[] ParseCsvIntGrid(string csv, int width, int height) {
        string[] parts = csv.Split(',');
        if (parts.Length != width * height) {
            throw new Exception($"Map data length mismatch: {parts.Length} != {width * height}");
        }
        int[] result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++) result[i] = int.Parse(parts[i]);
        return result;
    }
    public static int GetCell(int[] flat, int width, int x, int y) {
        return flat[y * width + x];
    }
}
