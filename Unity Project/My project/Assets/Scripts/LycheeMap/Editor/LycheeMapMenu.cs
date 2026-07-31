using UnityEditor;
using UnityEngine;

namespace LycheeMap.Editor
{
    public static class LycheeMapMenu
    {
        [MenuItem("LycheeMap/Generate Route Map")]
        public static void GenerateMap()
        {
            var map = LycheeMapGenerator.Generate();
            Debug.Log($"荔枝运输路线沙盘已生成: {map.name}, 节点数: {map.transform.Find("Nodes")?.childCount ?? 0}");
        }

        [MenuItem("LycheeMap/Highlight Official Road")]
        static void HighlightOfficial() => ToggleHighlight(RouteType.OfficialRoad);

        [MenuItem("LycheeMap/Highlight Water Route")]
        static void HighlightWater() => ToggleHighlight(RouteType.WaterRoute);

        [MenuItem("LycheeMap/Highlight Mountain Road")]
        static void HighlightMountain() => ToggleHighlight(RouteType.MountainRoad);

        [MenuItem("LycheeMap/Highlight Branch Road")]
        static void HighlightBranch() => ToggleHighlight(RouteType.BranchRoad);

        [MenuItem("LycheeMap/Clear Route Highlights")]
        static void ClearHighlights()
        {
            var root = GameObject.Find("LycheeRouteMap")?.GetComponent<MapRoot>();
            root?.ClearRouteHighlights();
        }

        static void ToggleHighlight(RouteType type)
        {
            var root = GameObject.Find("LycheeRouteMap")?.GetComponent<MapRoot>();
            if (root == null) return;
            root.ClearRouteHighlights();
            root.HighlightRoute(type, true);
        }
    }
}
