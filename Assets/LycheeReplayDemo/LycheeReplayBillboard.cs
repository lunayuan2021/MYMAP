using UnityEngine;

public class LycheeReplayBillboard : MonoBehaviour
{
    // 透视近景下世界标签会显得巨大且互相堆叠：按相机距离缩放，并在贴脸距离内淡出。
    [Min(1f)] public float referenceDistance = 26f; // 在该距离下保持原始大小（旧正交视角的观感基准）
    public float minScale = 0.35f;
    public float maxScale = 1.25f;
    public float nearFadeStart = 9f;  // 距离小于此值开始淡出
    public float nearFadeEnd = 4.5f;  // 距离小于此值完全隐藏
    public bool distanceScale = true;

    Camera targetCamera;
    Vector3 baseScale;
    bool baseScaleCaptured;
    TextMesh[] textMeshes;
    float[] baseAlphas;

    void Awake()
    {
        baseScale = transform.localScale;
        baseScaleCaptured = true;
        textMeshes = GetComponentsInChildren<TextMesh>(true);
        TextMesh own = GetComponent<TextMesh>();
        if (own != null && (textMeshes == null || textMeshes.Length == 0))
            textMeshes = new[] { own };
        if (textMeshes != null)
        {
            baseAlphas = new float[textMeshes.Length];
            for (int i = 0; i < textMeshes.Length; i++)
                baseAlphas[i] = textMeshes[i] != null ? textMeshes[i].color.a : 1f;
        }
    }

    void LateUpdate()
    {
        if (targetCamera == null) targetCamera = Camera.main;
        if (targetCamera == null) return;
        transform.rotation = targetCamera.transform.rotation;
        if (!distanceScale || !baseScaleCaptured) return;

        float distance = Vector3.Distance(targetCamera.transform.position, transform.position);
        float factor = Mathf.Clamp(distance / Mathf.Max(1f, referenceDistance), minScale, maxScale);
        transform.localScale = baseScale * factor;

        if (textMeshes == null || baseAlphas == null) return;
        float alphaFactor = nearFadeStart <= nearFadeEnd
            ? 1f
            : Mathf.Clamp01((distance - nearFadeEnd) / (nearFadeStart - nearFadeEnd));
        for (int i = 0; i < textMeshes.Length; i++)
        {
            TextMesh text = textMeshes[i];
            if (text == null) continue;
            Color color = text.color;
            color.a = baseAlphas[i] * alphaFactor;
            text.color = color;
        }
    }
}
