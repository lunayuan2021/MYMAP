using System;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Keeps the WebGL package on the same default scene as Editor Play, while
/// allowing explicit URL map switches such as ?map=map_2&path=...
/// </summary>
static class LycheeReplayMapRouter
{
    static bool handledInitialUrl;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        handledInitialUrl = false;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        TryRouteFromUrl();
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        TryRouteFromUrl();
    }

    static void TryRouteFromUrl()
    {
        if (handledInitialUrl) return;

        string url = Application.absoluteURL;
        if (string.IsNullOrEmpty(url)) return;

        string requested = QueryValue(url, "map");
        if (string.IsNullOrEmpty(requested)) requested = QueryValue(url, "scene");
        if (string.IsNullOrEmpty(requested)) return;

        handledInitialUrl = true;
        string sceneName = ResolveSceneName(requested);
        if (string.IsNullOrEmpty(sceneName)) return;

        Scene active = SceneManager.GetActiveScene();
        if (string.Equals(active.name, sceneName, StringComparison.OrdinalIgnoreCase)) return;

        if (!Application.CanStreamedLevelBeLoaded(sceneName))
        {
            Debug.LogWarning("LycheeReplayMapRouter: scene not in build: " + sceneName);
            return;
        }

        SceneManager.LoadScene(sceneName);
    }

    static string ResolveSceneName(string keyOrScene)
    {
        if (string.IsNullOrEmpty(keyOrScene)) return null;
        switch (keyOrScene.Trim().ToLowerInvariant())
        {
            case "demo":
            case "lycheereplaydemo":
                return "LycheeReplayDemo";
            case "map_1":
            case "map1":
            case "1":
            case "map_medium":
                return "Map_medium";
            case "map_2":
            case "map2":
            case "2":
                return "Map_map2";
            case "map_3":
            case "map3":
            case "3":
                return "Map_map3";
            case "map_4":
            case "map4":
            case "4":
                return "Map_map4";
            case "map_5":
            case "map5":
            case "5":
                return "Map_map5";
            case "select":
            case "selector":
            case "lycheemapselect":
                return "LycheeMapSelect";
            default:
                return keyOrScene;
        }
    }

    static string QueryValue(string url, string key)
    {
        int queryIndex = url.IndexOf('?');
        if (queryIndex < 0 || queryIndex == url.Length - 1) return null;
        string query = url.Substring(queryIndex + 1);
        int hashIndex = query.IndexOf('#');
        if (hashIndex >= 0) query = query.Substring(0, hashIndex);

        string[] pairs = query.Split('&');
        foreach (string pair in pairs)
        {
            int equalIndex = pair.IndexOf('=');
            if (equalIndex <= 0) continue;
            string pairKey = Uri.UnescapeDataString(pair.Substring(0, equalIndex));
            if (!string.Equals(pairKey, key, StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(pair.Substring(equalIndex + 1));
        }
        return null;
    }
}
