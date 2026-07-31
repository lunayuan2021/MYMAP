using UnityEngine;

public enum MapRouteType { OfficialRoad, Water, Mountain, Branch }

public class MapRouteEdge : MonoBehaviour
{
    public string edgeId;
    public string fromStationId;
    public string toStationId;
    public MapRouteType routeType;
    public float logicalDistance;
    public bool bidirectional = true;
    public Transform[] pathPoints;
    public Transform obstacleSlot;

    public Vector3 Evaluate(float t)
    {
        if (pathPoints == null || pathPoints.Length == 0) return transform.position;
        if (pathPoints.Length == 1) return pathPoints[0].position;
        t = Mathf.Clamp01(t);
        float totalLength = 0f;
        for (int i = 0; i < pathPoints.Length - 1; i++)
            totalLength += Vector3.Distance(pathPoints[i].position, pathPoints[i + 1].position);
        if (totalLength <= 0.0001f) return pathPoints[0].position;

        float targetDistance = totalLength * t;
        float walkedDistance = 0f;
        for (int i = 0; i < pathPoints.Length - 1; i++)
        {
            Vector3 start = pathPoints[i].position;
            Vector3 end = pathPoints[i + 1].position;
            float segmentLength = Vector3.Distance(start, end);
            if (walkedDistance + segmentLength >= targetDistance)
            {
                float segmentT = segmentLength <= 0.0001f
                    ? 0f
                    : (targetDistance - walkedDistance) / segmentLength;
                return Vector3.Lerp(start, end, segmentT);
            }
            walkedDistance += segmentLength;
        }
        return pathPoints[pathPoints.Length - 1].position;
    }
}
