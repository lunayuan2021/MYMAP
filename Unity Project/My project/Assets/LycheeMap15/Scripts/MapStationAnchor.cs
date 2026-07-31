using UnityEngine;

public enum MapStationType { Start, PostStation, Pass, Dock, WaterStation, MountainNode, MajorStation, PlankRoad, Plain, PalaceGate, Finish }

public class MapStationAnchor : MonoBehaviour
{
    public string stationId;
    public string displayName;
    public MapStationType stationType;
    public Vector2Int gridCoordinate;
    public bool safeZone;
    public Transform modelSlot;
    public Transform vehicleStopRed;
    public Transform vehicleStopBlue;
    public Transform eventAnchor;
    public Transform obstacleAnchor;

    void OnDrawGizmos()
    {
        Gizmos.color = safeZone ? new Color(0.2f,0.9f,0.55f,0.85f) : new Color(1f,0.72f,0.18f,0.85f);
        Gizmos.DrawWireSphere(transform.position + Vector3.up * 0.35f, 0.65f);
        Gizmos.DrawLine(transform.position, transform.position + Vector3.up * 2f);
    }
}