using UnityEngine;

namespace LycheeMap
{
    public class MapRoot : MonoBehaviour
    {
        [SerializeField] Transform terrainBaseLayer;
        [SerializeField] Transform routesLayer;
        [SerializeField] Transform nodesLayer;
        [SerializeField] Transform decorationsLayer;
        [SerializeField] Transform elevationLayer;

        public Transform TerrainBaseLayer => terrainBaseLayer;
        public Transform RoutesLayer => routesLayer;
        public Transform NodesLayer => nodesLayer;
        public Transform DecorationsLayer => decorationsLayer;
        public Transform ElevationLayer => elevationLayer;

        public void Initialize(Transform terrain, Transform routes, Transform nodes, Transform decor, Transform elevation)
        {
            terrainBaseLayer = terrain;
            routesLayer = routes;
            nodesLayer = nodes;
            decorationsLayer = decor;
            elevationLayer = elevation;
        }

        public RouteController GetRoute(RouteType type)
        {
            if (routesLayer == null) return null;
            foreach (Transform child in routesLayer)
            {
                var route = child.GetComponent<RouteController>();
                if (route != null && route.RouteType == type)
                    return route;
            }
            return null;
        }

        public void HighlightRoute(RouteType type, bool highlight)
        {
            var route = GetRoute(type);
            if (route != null)
                route.SetHighlighted(highlight);
        }

        public void ClearRouteHighlights()
        {
            if (routesLayer == null) return;
            foreach (Transform child in routesLayer)
            {
                var route = child.GetComponent<RouteController>();
                if (route != null)
                    route.SetHighlighted(false);
            }
        }
    }
}
