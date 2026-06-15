using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class LycheeMapSelectController : MonoBehaviour
{
    [Serializable]
    public struct MapEntry
    {
        public string key;
        public string label;
        public string sceneName;
    }

    public MapEntry[] maps =
    {
        new MapEntry { key = "demo", label = "Demo", sceneName = "LycheeReplayDemo" },
        new MapEntry { key = "map_1", label = "map_1", sceneName = "Map_medium" },
        new MapEntry { key = "map_2", label = "map_2", sceneName = "Map_map2" },
        new MapEntry { key = "map_3", label = "map_3", sceneName = "Map_map3" },
        new MapEntry { key = "map_4", label = "map_4", sceneName = "Map_map4" },
        new MapEntry { key = "map_5", label = "map_5", sceneName = "Map_map5" },
    };

    public bool allowUrlSelection = true;

    string status;
    GUIStyle titleStyle;
    GUIStyle subtitleStyle;
    GUIStyle buttonStyle;
    GUIStyle smallStyle;
    Texture2D panelTexture;

    void Start()
    {
        if (allowUrlSelection) TryLoadFromUrl();
    }

    void OnGUI()
    {
        EnsureStyles();

        float width = Mathf.Min(520f, Screen.width - 48f);
        float itemHeight = 56f;
        List<MapEntry> available = AvailableMaps();
        float height = 188f + available.Count * (itemHeight + 12f);
        Rect panel = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);

        GUI.DrawTexture(panel, panelTexture, ScaleMode.StretchToFill);
        GUILayout.BeginArea(new Rect(panel.x + 28f, panel.y + 24f, panel.width - 56f, panel.height - 48f));
        GUILayout.Label("Lychee Replay Maps", titleStyle);
        GUILayout.Label("Choose a map", subtitleStyle);
        GUILayout.Space(18f);

        foreach (MapEntry entry in available)
        {
            if (GUILayout.Button(entry.label, buttonStyle, GUILayout.Height(itemHeight)))
                LoadMap(entry);
            GUILayout.Space(12f);
        }

        if (!string.IsNullOrEmpty(status))
        {
            GUILayout.Space(8f);
            GUILayout.Label(status, smallStyle);
        }
        GUILayout.EndArea();
    }

    List<MapEntry> AvailableMaps()
    {
        var available = new List<MapEntry>();
        foreach (MapEntry entry in maps)
        {
            if (string.IsNullOrEmpty(entry.sceneName)) continue;
            if (!Application.CanStreamedLevelBeLoaded(entry.sceneName)) continue;
            available.Add(entry);
        }
        return available;
    }

    void TryLoadFromUrl()
    {
        string url = Application.absoluteURL;
        if (string.IsNullOrEmpty(url)) return;

        string requested = QueryValue(url, "map");
        if (string.IsNullOrEmpty(requested)) requested = QueryValue(url, "scene");
        if (string.IsNullOrEmpty(requested)) return;

        foreach (MapEntry entry in maps)
        {
            if (string.Equals(entry.key, requested, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.sceneName, requested, StringComparison.OrdinalIgnoreCase))
            {
                LoadMap(entry);
                return;
            }
        }

        status = "Map not found: " + requested;
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

    void LoadMap(MapEntry entry)
    {
        if (!Application.CanStreamedLevelBeLoaded(entry.sceneName))
        {
            status = "Scene not in build: " + entry.sceneName;
            return;
        }

        status = "Loading " + entry.label + "...";
        SceneManager.LoadScene(entry.sceneName);
    }

    void EnsureStyles()
    {
        if (titleStyle != null) return;

        titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 32,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color(0.94f, 0.97f, 1f) }
        };
        subtitleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 18,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color(0.72f, 0.78f, 0.84f) }
        };
        buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 22,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            fixedHeight = 56f,
            margin = new RectOffset(0, 0, 0, 0)
        };
        smallStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color(0.82f, 0.86f, 0.9f) },
            wordWrap = true
        };

        panelTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        panelTexture.SetPixel(0, 0, new Color(0.05f, 0.08f, 0.11f, 0.88f));
        panelTexture.Apply();
    }
}
