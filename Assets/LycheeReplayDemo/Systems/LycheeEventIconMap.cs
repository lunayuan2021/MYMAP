using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Data-driven event icon lookup backed by StreamingAssets/UITheme/event_icon_mapping.json.
/// Runtime sprites are PNG files under UITheme/EventIcons; legacy SVG names are still accepted.
/// </summary>
public static class LycheeEventIconMap
{
    static Dictionary<string, string> map;
    static string defaultFile;
    static bool loaded;

    static void EnsureLoaded()
    {
        if (loaded) return;
        if (map == null) map = new Dictionary<string, string>();

        byte[] bytes = LycheeThemeCache.LoadBytes("event_icon_mapping.json");
        if (bytes == null || bytes.Length == 0) return;

        string json = Encoding.UTF8.GetString(bytes);
        var fresh = new Dictionary<string, string>();
        foreach (Match match in Regex.Matches(json,
            "\"([A-Z_]+)\"\\s*:\\s*\"[^\"]*?(icon_event_[A-Za-z_]+)\\.(?:png|svg)\""))
        {
            fresh[match.Groups[1].Value] = "EventIcons/" + match.Groups[2].Value + ".png";
        }

        if (fresh.Count == 0) return;

        Match defaultMatch = Regex.Match(json,
            "\"defaultIcon\"\\s*:\\s*\"[^\"]*?(icon_event_[A-Za-z_]+)\\.(?:png|svg)\"");
        defaultFile = defaultMatch.Success ? "EventIcons/" + defaultMatch.Groups[1].Value + ".png" : null;
        map = fresh;
        loaded = true;
    }

    public static string ResolveFile(string eventType, bool useDefault)
    {
        EnsureLoaded();
        if (!string.IsNullOrEmpty(eventType) && map != null && map.TryGetValue(eventType, out string file))
            return file;
        return useDefault ? defaultFile : null;
    }
}
