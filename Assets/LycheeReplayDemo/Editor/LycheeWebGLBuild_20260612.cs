using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// 自动 WebGL 构建：检测到根目录 LycheeWebGLBuild_20260612.request 即开始。
/// 切换构建目标会触发域重载，因此用标记文件驱动，重载后自动续跑；
/// 完成后写 LycheeWebGLBuild_20260612.done 与报告文件。(attempt 3: update 轮询，
/// 等播放模式/截图看守让位后再构建，播放模式冲突重试而非放弃)
/// </summary>
[InitializeOnLoad]
static class LycheeWebGLBuild_20260612
{
    const string RequestMarker = "LycheeWebGLBuild_20260612.request";
    const string DoneMarker = "LycheeWebGLBuild_20260612.done";
    const string ReportPath = "LycheeWebGLBuild_20260612_report.txt";
    const string OutputDir = "Builds/LycheeReplay_WebGL_20260612";
    const string Scene = "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity";

    static double nextPollTime;
    static bool running;

    static LycheeWebGLBuild_20260612()
    {
        EditorApplication.update += Poll;
    }

    static void Poll()
    {
        if (running || EditorApplication.timeSinceStartup < nextPollTime) return;
        nextPollTime = EditorApplication.timeSinceStartup + 2.0;

        if (!File.Exists(RequestMarker) || File.Exists(DoneMarker)) return;
        if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode ||
            EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        // 截图看守（LycheePlaymodeCaptureRunner）有待处理请求时让位，避免互抢播放模式
        if (File.Exists("LycheePlaymodeCapture.request")) return;

        running = true;
        try
        {
            RunBuild();
        }
        finally
        {
            running = false;
        }
    }

    static void RunBuild()
    {
        try
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
            {
                Append("Switching active build target to WebGL (asset reimport, may take a while)...");
                bool accepted = EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL);
                if (!accepted)
                {
                    Append("ERROR: SwitchActiveBuildTarget rejected — is the WebGL module installed?");
                    File.WriteAllText(DoneMarker, "Failed");
                    DeleteRequestMarker();
                }
                return; // 域重载后由静态构造函数重新挂上轮询（切换成功时保留 request 续跑）
            }

            FixSceneFonts();
            GenerateThemeManifest();

            // Gzip + 解压回退：任何静态文件服务器（python -m http.server 等）都能直接跑，无需配置 Content-Encoding
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
            PlayerSettings.WebGL.decompressionFallback = true;
            PlayerSettings.WebGL.maximumMemorySize = 4096;
            PlayerSettings.runInBackground = true;
            // 场景里无序列化碰撞体，引擎裁剪会删掉 Collider 类，
            // 运行时 CreatePrimitive 附带的碰撞体组件就会报错刷屏；关闭裁剪（wasm 仅增大几 MB）
            PlayerSettings.stripEngineCode = false;

            Append("Starting WebGL build to " + OutputDir + " ...");
            BuildPlayerOptions options = new BuildPlayerOptions
            {
                scenes = new[] { Scene },
                target = BuildTarget.WebGL,
                locationPathName = OutputDir,
                options = BuildOptions.None
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            Append(string.Format("Build result: {0}, size: {1:F1} MB, time: {2:F0}s, errors: {3}",
                summary.result, summary.totalSize / (1024f * 1024f), summary.totalTime.TotalSeconds, summary.totalErrors));
            if (summary.result == BuildResult.Succeeded) PatchIndexHtml();
            File.WriteAllText(DoneMarker, summary.result.ToString());
            DeleteRequestMarker();
        }
        catch (Exception exception)
        {
            // 播放模式相关冲突（截图看守或用户手动 Play 抢占）→ 下轮轮询重试，不写 done
            if (exception is InvalidOperationException && exception.Message.Contains("play mode"))
            {
                Append("Play mode conflict, will retry: " + exception.Message);
                nextPollTime = EditorApplication.timeSinceStartup + 10.0;
                return;
            }
            Append("EXCEPTION: " + exception);
            File.WriteAllText(DoneMarker, "Exception");
            DeleteRequestMarker();
        }
    }

    /// <summary>
    /// 写出 DoneMarker 后立即消耗掉 RequestMarker：轮询条件是"request 存在且 done 不存在"，
    /// 若 request 残留，之后任何人删掉 .done 都会立刻触发一次全量 WebGL 构建（上膛状态）。
    /// 单独包 try 防止 catch 块里二次抛异常冲掉原始报告。
    /// </summary>
    static void DeleteRequestMarker()
    {
        try
        {
            if (File.Exists(RequestMarker)) File.Delete(RequestMarker);
        }
        catch (Exception exception)
        {
            Append("WARNING: 删除 request 标记失败: " + exception.Message);
        }
    }

    /// <summary>
    /// 场景里被序列化的 OS 动态字体（CreateDynamicFontFromOSFont + ExecuteAlways 存盘的产物）
    /// 没有内嵌字体数据，WebGL 构建会报 "Need to include font data on WebGL"。
    /// 统一替换为随包的 Noto Sans SC 资源后重新保存场景。
    /// </summary>
    static void FixSceneFonts()
    {
        Font bundled = AssetDatabase.LoadAssetAtPath<Font>(
            "Assets/LycheeReplayDemo/Resources/Fonts/LXGWWenKai-Regular.ttf");
        if (bundled == null)
        {
            Append("WARNING: bundled font not found, skipping scene font fix.");
            return;
        }

        var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
            Scene, UnityEditor.SceneManagement.OpenSceneMode.Single);
        int fixedCount = 0;
        foreach (TextMesh textMesh in UnityEngine.Object.FindObjectsByType<TextMesh>(
            FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            bool embedded = textMesh.font != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(textMesh.font));
            if (textMesh.font != null && !embedded) continue;
            textMesh.font = bundled;
            MeshRenderer meshRenderer = textMesh.GetComponent<MeshRenderer>();
            if (meshRenderer != null) meshRenderer.sharedMaterial = bundled.material;
            fixedCount++;
        }
        foreach (UnityEngine.UI.Text text in UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Text>(
            FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            bool embedded = text.font != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(text.font));
            if (text.font != null && !embedded) continue;
            text.font = bundled;
            fixedCount++;
        }
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
        Append("Scene font fix: " + fixedCount + " text components now use bundled NotoSansSC.");
    }

    /// <summary>
    /// 枚举 UITheme 顶层 png 写入 uitheme_manifest.txt——WebGL 无法列目录，
    /// LycheeThemeCache 启动时按清单预载（素材增删后重新构建即自动生效）。
    /// </summary>
    static void GenerateThemeManifest()
    {
        string themeDir = "Assets/StreamingAssets/UITheme";
        if (!Directory.Exists(themeDir)) return;
        var names = new System.Collections.Generic.List<string>();
        foreach (string file in Directory.GetFiles(themeDir, "*.png"))
            names.Add(Path.GetFileName(file));
        names.Sort(StringComparer.Ordinal);
        string manifestPath = themeDir + "/uitheme_manifest.txt";
        File.WriteAllText(manifestPath, string.Join("\n", names) + "\n");
        AssetDatabase.ImportAsset(manifestPath);
        Append("Theme manifest generated: " + names.Count + " entries.");
    }

    /// <summary>
    /// 构建产物 index.html 补丁：Retina 下强制 devicePixelRatio=1（帧率翻倍级提升），
    /// 桌面端画布占满浏览器窗口。
    /// </summary>
    static void PatchIndexHtml()
    {
        string indexPath = OutputDir + "/index.html";
        if (!File.Exists(indexPath)) return;
        string html = File.ReadAllText(indexPath);
        html = html.Replace(
            "createUnityInstance(canvas, config, (progress) => {",
            "config.devicePixelRatio = 1; // Retina 降采样，显著提升帧率\n        createUnityInstance(canvas, config, (progress) => {");
        html = html.Replace("canvas.style.width = \"960px\";", "canvas.style.width = \"100vw\";");
        html = html.Replace("canvas.style.height = \"600px\";", "canvas.style.height = \"100vh\";");
        File.WriteAllText(indexPath, html);
        Append("index.html patched (devicePixelRatio=1, full-window canvas).");
    }

    static void Append(string message)
    {
        File.AppendAllText(ReportPath, DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine);
        Debug.Log("[LycheeWebGLBuild] " + message);
    }
}
