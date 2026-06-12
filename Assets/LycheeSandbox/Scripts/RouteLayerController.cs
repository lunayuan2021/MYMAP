using UnityEngine;

public class RouteLayerController : MonoBehaviour
{
    [SerializeField] GameObject surface;
    [SerializeField] GameObject highlight;
    [SerializeField] bool highlighted;
    public bool Visible => gameObject.activeSelf;
    public bool Highlighted => highlighted;
    public void SetVisible(bool value) => gameObject.SetActive(value);
    public void SetHighlighted(bool value)
    {
        highlighted=value;
        if(highlight!=null) highlight.SetActive(value);
    }
    public void ToggleHighlight() => SetHighlighted(!highlighted);
    void Reset()
    {
        surface=transform.Find("Surface")?.gameObject;
        highlight=transform.Find("Highlight_Optional")?.gameObject;
    }
}