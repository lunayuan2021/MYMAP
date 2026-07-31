using UnityEngine;

public enum EnvironmentAnchorType { Weather, Task, Obstacle, Decoration, CameraFocus }

public class MapEnvironmentAnchor : MonoBehaviour
{
    public EnvironmentAnchorType anchorType;
    public string regionId;
    public float radius = 2f;
    public bool showEditorGizmo;

    void OnDrawGizmosSelected()
    {
        if (!showEditorGizmo) return;
        Gizmos.color = new Color(0.2f,0.75f,1f,0.5f);
        Gizmos.DrawWireSphere(transform.position, radius);
    }
}
