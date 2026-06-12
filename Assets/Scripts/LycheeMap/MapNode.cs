using UnityEngine;

namespace LycheeMap
{
    [DisallowMultipleComponent]
    public class MapNode : MonoBehaviour
    {
        [SerializeField] string nodeId;
        [SerializeField] string displayName;
        [SerializeField] NodeType nodeType;
        [SerializeField] RouteType[] connectedRoutes;

        public string NodeId => nodeId;
        public string DisplayName => displayName;
        public NodeType Type => nodeType;
        public RouteType[] ConnectedRoutes => connectedRoutes;

        public void Initialize(string id, string name, NodeType type, RouteType[] routes)
        {
            nodeId = id;
            displayName = name;
            nodeType = type;
            connectedRoutes = routes;
            gameObject.name = $"Node_{id}";
        }

        public void SetSelected(bool selected)
        {
            var indicator = transform.Find("Indicator");
            if (indicator != null)
                indicator.gameObject.SetActive(selected);
        }
    }
}
