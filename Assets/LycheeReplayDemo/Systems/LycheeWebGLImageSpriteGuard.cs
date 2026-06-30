using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// WebGL/Unity UI workaround:
/// Some Unity 6 WebGL builds crash inside Image.CheckSecondaryTexturesChanged ->
/// Sprite.GetSecondaryTextures when runtime-created sprites are rendered by Image.
/// RawImage does not use that secondary texture path, so WebGL mirrors Image sprites
/// onto a sibling RawImage and clears the Image sprite before the canvas rebuild.
/// </summary>
public sealed class LycheeWebGLImageSpriteGuard : MonoBehaviour
{
#if UNITY_WEBGL && !UNITY_EDITOR
    static LycheeWebGLImageSpriteGuard instance;
    static int convertedTotal;
    readonly Dictionary<Image, Proxy> proxies = new Dictionary<Image, Proxy>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (instance != null) return;
        GameObject go = new GameObject("LycheeWebGLImageSpriteGuard");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<LycheeWebGLImageSpriteGuard>();
    }

    void LateUpdate()
    {
        Image[] images = FindObjectsByType<Image>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < images.Length; i++)
            ConvertIfNeeded(images[i]);

        if (proxies.Count > 128)
            PruneDeadProxies();
    }

    void ConvertIfNeeded(Image image)
    {
        if (image == null) return;
        Sprite sprite = image.sprite;
        if (sprite == null) return;

        if (!proxies.TryGetValue(image, out Proxy proxy) || proxy == null)
        {
            proxy = image.gameObject.GetComponent<Proxy>();
            if (proxy == null) proxy = image.gameObject.AddComponent<Proxy>();
            proxies[image] = proxy;
        }
        proxy.Capture(image, sprite);

        image.overrideSprite = null;
        image.sprite = null;
        image.canvasRenderer.SetAlpha(0f);

        convertedTotal++;
        if (convertedTotal == 1 || convertedTotal % 25 == 0)
            Debug.Log("[WebGLImageGuard] converted Image sprites to RawImage count=" + convertedTotal);
    }

    void PruneDeadProxies()
    {
        var dead = new List<Image>();
        foreach (var pair in proxies)
        {
            if (pair.Key == null || pair.Value == null)
                dead.Add(pair.Key);
        }
        for (int i = 0; i < dead.Count; i++)
            proxies.Remove(dead[i]);
    }

    sealed class Proxy : MonoBehaviour
    {
        Image source;
        RawImage raw;

        public void Capture(Image image, Sprite sprite)
        {
            if (image == null || sprite == null || sprite.texture == null) return;
            source = image;
            if (raw == null)
            {
                GameObject rawObject = new GameObject("__WebGLRawImage", typeof(RectTransform), typeof(RawImage));
                rawObject.transform.SetParent(image.transform, false);
                rawObject.transform.SetAsFirstSibling();
                RectTransform rt = rawObject.GetComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                rt.localScale = Vector3.one;
                raw = rawObject.GetComponent<RawImage>();
                raw.raycastTarget = false;
            }
            raw.texture = sprite.texture;
            raw.uvRect = SpriteUv(sprite);
            raw.color = image.color;
            raw.material = null;
            raw.maskable = image.maskable;
            raw.enabled = image.enabled && image.isActiveAndEnabled;
            raw.transform.SetAsFirstSibling();
        }

        void LateUpdate()
        {
            if (source == null || raw == null) return;
            if (source.sprite != null)
                Capture(source, source.sprite);
            else
            {
                raw.color = source.color;
                raw.enabled = source.enabled && source.gameObject.activeInHierarchy && raw.texture != null;
            }
            source.canvasRenderer.SetAlpha(0f);
        }

        static Rect SpriteUv(Sprite sprite)
        {
            if (sprite == null || sprite.texture == null) return new Rect(0f, 0f, 1f, 1f);
            Rect r = sprite.rect;
            Texture2D texture = sprite.texture;
            if (texture.width <= 0 || texture.height <= 0) return new Rect(0f, 0f, 1f, 1f);
            return new Rect(r.x / texture.width, r.y / texture.height, r.width / texture.width, r.height / texture.height);
        }
    }
#endif
}
