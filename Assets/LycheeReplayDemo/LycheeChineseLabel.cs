using UnityEngine;

[ExecuteAlways]
[RequireComponent(typeof(TextMesh))]
public class LycheeChineseLabel : MonoBehaviour
{
    static Font sharedFont;

    void OnEnable()
    {
        TextMesh label = GetComponent<TextMesh>();
        // 优先用随包字体：WebGL 取不到系统字体，且 OS 动态字体会被序列化进场景导致构建失败
        if (sharedFont == null)
            sharedFont = Resources.Load<Font>("Fonts/LXGWWenKai-Regular");
        if (sharedFont == null)
        {
            sharedFont = Font.CreateDynamicFontFromOSFont(
                new[] { "PingFang SC", "Heiti SC", "Arial Unicode MS" },
                48);
        }
        if (sharedFont == null) return;
        label.font = sharedFont;
        MeshRenderer renderer = GetComponent<MeshRenderer>();
        if (renderer != null) renderer.sharedMaterial = sharedFont.material;
    }
}
