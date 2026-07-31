using UnityEngine;

public class ElevationPathData : MonoBehaviour
{
    public Transform[] waypoints;
    public Vector3 GetPosition(int index) => waypoints[Mathf.Clamp(index,0,waypoints.Length-1)].position;
    public float GetElevation(int index) => GetPosition(index).y;
    void Reset()
    {
        waypoints=new Transform[transform.childCount];
        for(int i=0;i<transform.childCount;i++) waypoints[i]=transform.GetChild(i);
    }
}