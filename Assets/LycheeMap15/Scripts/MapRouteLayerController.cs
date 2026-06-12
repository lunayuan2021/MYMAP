using UnityEngine;

public class MapRouteLayerController : MonoBehaviour
{
    public Transform officialRoad;
    public Transform waterRoute;
    public Transform mountainRoute;
    public Transform branchRoute;

    public void SetRouteVisible(MapRouteType type, bool visible)
    {
        Transform route = GetRoute(type);
        if (route != null) route.gameObject.SetActive(visible);
    }

    public void SetRouteHighlighted(MapRouteType type, bool highlighted)
    {
        Transform route = GetRoute(type);
        if (route == null) return;

        Color emission = highlighted ? new Color(0.42f, 0.27f, 0.08f) : Color.black;
        foreach (Renderer renderer in route.GetComponentsInChildren<Renderer>(true))
        {
            MaterialPropertyBlock properties = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(properties);
            properties.SetColor("_EmissionColor", emission);
            renderer.SetPropertyBlock(properties);
        }
    }

    Transform GetRoute(MapRouteType type)
    {
        switch (type)
        {
            case MapRouteType.Water: return waterRoute;
            case MapRouteType.Mountain: return mountainRoute;
            case MapRouteType.Branch: return branchRoute;
            default: return officialRoad;
        }
    }
}
