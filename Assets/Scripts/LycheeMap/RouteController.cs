using System.Collections.Generic;
using UnityEngine;

namespace LycheeMap
{
    [DisallowMultipleComponent]
    public class RouteController : MonoBehaviour
    {
        [SerializeField] RouteType routeType;
        [SerializeField] Color baseColor = Color.white;
        [SerializeField] Color highlightColor = Color.yellow;
        readonly List<Renderer> renderers = new List<Renderer>();
        bool highlighted;

        public RouteType RouteType => routeType;
        public bool IsHighlighted => highlighted;

        public void Initialize(RouteType type, Color color, Color highlight)
        {
            routeType = type;
            baseColor = color;
            highlightColor = highlight;
            CacheRenderers();
            ApplyColor(baseColor);
        }

        void Awake()
        {
            CacheRenderers();
            ApplyColor(highlighted ? highlightColor : baseColor);
        }

        public void SetHighlighted(bool value)
        {
            highlighted = value;
            ApplyColor(value ? highlightColor : baseColor);
        }

        void CacheRenderers()
        {
            renderers.Clear();
            renderers.AddRange(GetComponentsInChildren<Renderer>(true));
        }

        void ApplyColor(Color color)
        {
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                block.SetColor("_Color", color);
                block.SetColor("_BaseColor", color);
                renderer.SetPropertyBlock(block);
            }
        }
    }
}
