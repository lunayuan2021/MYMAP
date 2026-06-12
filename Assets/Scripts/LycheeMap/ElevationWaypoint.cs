using UnityEngine;

namespace LycheeMap
{
    [DisallowMultipleComponent]
    public class ElevationWaypoint : MonoBehaviour
    {
        [SerializeField] string waypointId;
        [SerializeField] float elevation;
        [SerializeField] float slopeAngle;
        [SerializeField] RouteType routeType;
        [SerializeField] int routeIndex;

        public string WaypointId => waypointId;
        public float Elevation => elevation;
        public float SlopeAngle => slopeAngle;
        public RouteType RouteType => routeType;
        public int RouteIndex => routeIndex;

        public void Initialize(string id, float elev, float slope, RouteType route, int index)
        {
            waypointId = id;
            elevation = elev;
            slopeAngle = slope;
            routeType = route;
            routeIndex = index;
            gameObject.name = $"WP_{id}";
        }
    }
}
