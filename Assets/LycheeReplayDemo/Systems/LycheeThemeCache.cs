using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// StreamingAssets/UITheme 贴图的统一字节加载入口。
/// 桌面端/编辑器：File IO 惰性读取；WebGL：无法同步读 StreamingAssets，
/// 启动时按构建脚本生成的 uitheme_manifest.txt 预载全部文件字节，之后同步取缓存。
/// </summary>
public static class LycheeThemeCache
{
    static readonly Dictionary<string, byte[]> webglBytes = new Dictionary<string, byte[]>();

    public static bool Preloaded { get; private set; }

    /// <summary>读取 UITheme/&lt;fileName&gt; 的原始字节；缺失返回 null（调用方走程序化兜底）。</summary>
    public static byte[] LoadBytes(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
#if UNITY_WEBGL && !UNITY_EDITOR
        return webglBytes.TryGetValue(fileName, out byte[] cached) ? cached : null;
#else
        try
        {
            string path = System.IO.Path.Combine(Application.streamingAssetsPath, "UITheme", fileName);
            if (!System.IO.File.Exists(path)) return null;
            byte[] bytes = System.IO.File.ReadAllBytes(path);
            return bytes != null && bytes.Length > 0 ? bytes : null;
        }
        catch (Exception)
        {
            return null;
        }
#endif
    }

    /// <summary>WebGL 预载全部主题贴图；其他平台立即返回。挂在回放初始化前执行。</summary>
    public static IEnumerator Preload()
    {
        if (Preloaded) yield break;
#if UNITY_WEBGL && !UNITY_EDITOR
        string baseUrl = Application.streamingAssetsPath + "/UITheme/";
        string[] names = null;
        using (var request = UnityEngine.Networking.UnityWebRequest.Get(
            baseUrl + "uitheme_manifest.txt?t=" + DateTime.UtcNow.Ticks))
        {
            yield return request.SendWebRequest();
            if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                names = request.downloadHandler.text.Split(
                    new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }
        if (names != null)
        {
            foreach (string name in names)
            {
                string trimmed = name.Trim();
                if (trimmed.Length == 0 || webglBytes.ContainsKey(trimmed)) continue;
                using (var request = UnityEngine.Networking.UnityWebRequest.Get(baseUrl + trimmed))
                {
                    yield return request.SendWebRequest();
                    if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success &&
                        request.downloadHandler.data != null &&
                        request.downloadHandler.data.Length > 0)
                        webglBytes[trimmed] = request.downloadHandler.data;
                }
            }
        }
        Debug.Log("LycheeThemeCache 预载完成: " + webglBytes.Count + " 个主题贴图。");
#endif
        Preloaded = true;
    }
}
