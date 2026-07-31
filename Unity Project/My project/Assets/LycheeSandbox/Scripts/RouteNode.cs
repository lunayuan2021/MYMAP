using UnityEngine;

public enum RouteNodeType { Start, Loading, PostStation, Wharf, Village, Bridge, Gate, PlankRoad, UrgentOrder, Checkpoint, Junction, Beacon, Palace }

public class RouteNode : MonoBehaviour
{
    public int nodeIndex;
    public RouteNodeType nodeType;
    public bool isAvailable=true;
    public Transform interactionAnchor;
    public event System.Action<RouteNode> Selected;
    public void Select()
    {
        if(isAvailable) Selected?.Invoke(this);
    }
    void OnMouseDown() => Select();
    void Reset() => interactionAnchor=transform.Find("Interaction_Base");
}