using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// 原生独立包构建（Linux64 / Windows64 / macOS）——比 WebGL 性能强很多：
/// 无浏览器开销、无几百 MB 解压下载、满帧、无 file:///WebGL 兼容问题。
/// 用 Unity 菜单 "Lychee/构建原生包" 触发，或代码调 BuildLinux64()/BuildWindows64()/BuildMacOS()。
/// 注意：Linux/Windows 目标需先在 Unity Hub → 本编辑器版本 → Add Modules 装
/// "Linux Build Support (Mono)" / "Windows Build Support (Mono)"，否则 BuildPlayer 会失败。
/// 场景清单与 LycheeWebGLBuild 一致(map1 Map_medium 为首场景)；StreamingAssets 由 BuildPlayer 自动拷。
/// </summary>
static class LycheeNativeBuild_20260625
{
    static readonly string[] CandidateScenes =
    {
        "Assets/Scenes/GeneratedMaps/Map_medium.unity",       // 官方 map1，首场景
        "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity", // demo
        "Assets/Scenes/GeneratedMaps/Map_map2.unity",
        "Assets/Scenes/GeneratedMaps/Map_map3.unity",
        "Assets/Scenes/GeneratedMaps/Map_map4.unity",
        "Assets/Scenes/GeneratedMaps/Map_map5.unity",
        "Assets/Scenes/GeneratedMaps/LycheeMapSelect.unity",  // 选图器
    };

    static string[] ScenesThatExist()
    {
        var list = new List<string>();
        foreach (var s in CandidateScenes) if (File.Exists(s)) list.Add(s);
        return list.ToArray();
    }

    [MenuItem("Lychee/构建原生包/Linux64")]
    public static void BuildLinux64() => Build(BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64, "Linux64", "LycheeReplay.x86_64");

    [MenuItem("Lychee/构建原生包/Windows64")]
    public static void BuildWindows64() => Build(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64, "Win64", "LycheeReplay.exe");

    [MenuItem("Lychee/构建原生包/macOS")]
    public static void BuildMacOS() => Build(BuildTargetGroup.Standalone, BuildTarget.StandaloneOSX, "macOS", "LycheeReplay.app");

    static void Build(BuildTargetGroup group, BuildTarget target, string tag, string exeName)
    {
        if (!BuildPipeline.IsBuildTargetSupported(group, target))
        {
            Debug.LogError($"[原生构建] 目标 {target} 不可用——请先在 Unity Hub → 本编辑器版本 → Add Modules 安装对应平台的 Build Support 模块。");
            return;
        }
        var scenes = ScenesThatExist();
        if (scenes.Length == 0) { Debug.LogError("[原生构建] 没有可构建的场景。"); return; }

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string outDir = $"Builds/LycheeReplay_{tag}_{stamp}";
        Directory.CreateDirectory(outDir);
        string location = outDir + "/" + exeName;

        // 性能取向的播放器设置（原生包同样适用）
        PlayerSettings.runInBackground = true;
        PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed; // 允许 ?path=http://... 读回放
        // 从 macOS 跨平台打 Windows/Linux 必须用 Mono 后端（IL2CPP 不支持在 Mac 上跨编到 Win/Linux）。
        try { PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x); }
        catch (Exception e) { Debug.LogWarning("[原生构建] 设 Mono 后端失败(可忽略，若用 IL2CPP 跨编会报错): " + e.Message); }

        Debug.Log($"[原生构建] 开始 {target} → {location}（{scenes.Length} 个场景，切平台可能触发资源重导入，请稍候）");
        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            target = target,
            targetGroup = group,
            locationPathName = location,
            options = BuildOptions.None
        };
        BuildReport report = BuildPipeline.BuildPlayer(options);
        var s = report.summary;
        if (s.result == BuildResult.Succeeded)
            Debug.Log($"[原生构建] 成功 {target}：{s.totalSize / (1024f * 1024f):F1} MB，{s.totalTime.TotalSeconds:F0}s → {outDir}（StreamingAssets 已随包，直接双击运行，无需 HTTP 服务）");
        else
            Debug.LogError($"[原生构建] 失败 {target}：result={s.result} errors={s.totalErrors}");
    }
}
