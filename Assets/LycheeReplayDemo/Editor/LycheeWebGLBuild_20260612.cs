using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// 自动 WebGL 构建：检测到根目录 LycheeWebGLBuild_20260616.request 即开始。
/// 切换构建目标会触发域重载，因此用标记文件驱动，重载后自动续跑；
/// 完成后写 LycheeWebGLBuild_20260616.done 与报告文件。(attempt 3: update 轮询，
/// 等播放模式/截图看守让位后再构建，播放模式冲突重试而非放弃)
/// </summary>
[InitializeOnLoad]
static class LycheeWebGLBuild_20260612
{
    const string RequestMarker = "LycheeWebGLBuild_20260616.request";
    const string DoneMarker = "LycheeWebGLBuild_20260616.done";
    const string ReportPath = "LycheeWebGLBuild_20260616_report.txt";
    // 部署子路径前缀 + 起服端口：访问形如 ip:port/litchi_delivery_replay/?path=...（原根路径 + 端口 3000）。
    const string DeployPrefix = "litchi_delivery_replay";
    const int ServePort = 9091;
    // 输出包名带完整时间戳(yyyyMMdd_HHmmss)：每次构建都是独立新目录、不再互相替换（用户要求）。
    // 用字段而非属性：属性每次取值都会得到不同的当前时间，会让 CleanOutputDir/BuildPlayer/PatchIndexHtml 指向不同目录而破坏构建；
    // 故在 RunBuild 开始处一次性赋值，整次构建用同一个时间戳。
    static string OutputDir;
    const string DemoScene = "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity";
    const string PreferredSingleMapScene = "Assets/Scenes/GeneratedMaps/Map_medium.unity";
    const string SelectorScene = "Assets/Scenes/GeneratedMaps/LycheeMapSelect.unity";

    // WebGL 重型贴图降采样:地形 PBR 贴图(Realistic Terrain Textures Lite)默认 4096,是 .data(~466MB)的大头。
    // 回放为俯视远景,2048+Crunch 肉眼无损但体积砍数倍;仅加 WebGL 平台覆盖,Standalone/Server/原生包保持全质量。
    // 想更极致(~30-40MB)可把此值改 1024。
    const int WebGLTextureMaxSize = 2048;
    const int WebGLInitialMemoryMB = 512;
    const int WebGLMaximumMemoryMB = 2048;
    static readonly string[] HeavyTextureFolders =
    {
        "Assets/ALP_Assets/Realistic Terrain Textures Lite/Textures",
        "Assets/FreeMountain/Textures",
    };

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
        WebGLRenderPipelineBackup renderPipelineBackup = null;
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

            OutputDir = "Builds/LycheeReplay_WebGL_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_Linux"; // 日期_时分秒_Linux：带时间戳、匹配交付命名，每次构建独立目录
            string[] scenes = GetBuildScenes();
            FixSceneFonts(scenes);
            FixReplayCameraControllers(scenes);
            GenerateThemeManifest();
            ValidateStreamingReplayFiles(scenes);
            OptimizeWebGLTextures();
            EnsureWebGLPreloadedEventModels();
            renderPipelineBackup = ApplyWebGLRenderPipelineOverrides();

            // Gzip + 解压回退：任何静态文件服务器（python -m http.server 等）都能直接跑，无需配置 Content-Encoding
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
            PlayerSettings.WebGL.decompressionFallback = true;
            // UITheme 事件插画单张解码后约 6MB，32MB 初始 heap 会在 WebGL UI Sprite 更新时触发 wasm 堆增长风险。
            // 512MB 作为初始值避免运行中频繁扩容，2GB 作为上限保留地图/模型峰值空间。
            ApplyWebGLMemorySettings();
            // Data Caching 开：data 文件名带时间戳(每次构建唯一) + index.html 的 ?v=buildStamp 双重破坏缓存，
            // IndexedDB 不会再拿到旧 data；开启后重复访问/弱网刷新直接命中本地缓存，显著降低"加载卡住/白屏"复发。
            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.runInBackground = true;
            // 回放包需要支持 ?path=http://... 从黄区/内网 HTTP 地址读取 replay.txt。
            // Unity 6 默认禁止非 HTTPS 请求，Linux/Chrome 会直接抛 Insecure connection not allowed。
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            // 场景里无序列化碰撞体，引擎裁剪会删掉 Collider 类，
            // 运行时 CreatePrimitive 附带的碰撞体组件就会报错刷屏；关闭裁剪（wasm 仅增大几 MB）
            PlayerSettings.stripEngineCode = false;

            CleanOutputDir();
            Append("Starting WebGL build to " + OutputDir + " ...");
            Append("Scenes: " + string.Join(", ", scenes));
            BuildPlayerOptions options = new BuildPlayerOptions
            {
                scenes = scenes,
                target = BuildTarget.WebGL,
                locationPathName = OutputDir,
                options = BuildOptions.None
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            Append(string.Format("Build result: {0}, size: {1:F1} MB, time: {2:F0}s, errors: {3}",
                summary.result, summary.totalSize / (1024f * 1024f), summary.totalTime.TotalSeconds, summary.totalErrors));
            if (summary.result == BuildResult.Succeeded)
            {
                PatchIndexHtml();
                WritePackageReadme(scenes);
                CleanPackageJunk();
            }
            ReopenAuthoringScene(scenes);
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
        finally
        {
            if (renderPipelineBackup != null) renderPipelineBackup.Restore();
        }
    }

    /// <summary>
    /// 命令行无头入口（纯新增，不改动 GUI 轮询路径）：
    ///   Unity -batchmode -nographics -buildTarget WebGL \
    ///         -executeMethod LycheeWebGLBuild_20260612.BuildFromCommandLine -quit
    /// 直接同步跑一次既有 RunBuild()，不依赖 EditorApplication.update 轮询，也不被 MCP 驱动。
    /// 失败时置非零退出码，方便脚本判定。
    /// </summary>
    public static void BuildFromCommandLine()
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL);
        if (File.Exists(DoneMarker)) File.Delete(DoneMarker);
        running = true;
        try { RunBuild(); }
        finally { running = false; }
        string result = File.Exists(DoneMarker) ? File.ReadAllText(DoneMarker).Trim() : "NoDone";
        Append("CLI build finished with result: " + result);
        if (result != "Succeeded") EditorApplication.Exit(1);
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
    static string[] GetBuildScenes()
    {
        // 默认首场景 = Map_medium（官方 map1 几何）+ StreamingAssets/replay.txt（含 WINDOW_* 出牌段）。
        // Tournament/map1.txt 是纯地图回放，当前没有窗口争夺事件；若默认绑定它，包打开后就看不到出牌。
        // LycheeReplayDemo 仍打包，可用 ?map=demo 进入。其余地图按 ?map=... 切换。
        List<string> oneToOneScenes = new List<string>();
        AddSceneIfExists(oneToOneScenes, PreferredSingleMapScene);
        AddSceneIfExists(oneToOneScenes, DemoScene);
        AddSceneIfExists(oneToOneScenes, "Assets/Scenes/GeneratedMaps/Map_map2.unity");
        AddSceneIfExists(oneToOneScenes, "Assets/Scenes/GeneratedMaps/Map_map3.unity");
        AddSceneIfExists(oneToOneScenes, "Assets/Scenes/GeneratedMaps/Map_map4.unity");
        AddSceneIfExists(oneToOneScenes, "Assets/Scenes/GeneratedMaps/Map_map5.unity");
        AddSceneIfExists(oneToOneScenes, SelectorScene);
        if (oneToOneScenes.Count > 0) return oneToOneScenes.ToArray();

        List<string> scenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled)
            .Select(scene => scene.path)
            .Where(File.Exists)
            .Distinct()
            .ToList();
        return scenes.ToArray();
    }

    static void AddSceneIfExists(List<string> scenes, string scenePath)
    {
        if (scenes == null || string.IsNullOrEmpty(scenePath)) return;
        if (!File.Exists(scenePath)) return;
        if (scenes.Contains(scenePath)) return;
        scenes.Add(scenePath);
    }

    static void CleanOutputDir()
    {
        if (Directory.Exists(OutputDir))
        {
            Directory.Delete(OutputDir, true);
            Append("Cleaned previous output dir: " + OutputDir);
        }
        Directory.CreateDirectory(OutputDir);
    }

    static void ReopenAuthoringScene(string[] scenes)
    {
        string sceneToOpen = null;
        if (File.Exists(PreferredSingleMapScene)) sceneToOpen = PreferredSingleMapScene;
        else if (scenes != null && scenes.Length > 0 && File.Exists(scenes[0])) sceneToOpen = scenes[0];
        if (string.IsNullOrEmpty(sceneToOpen)) return;

        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
            sceneToOpen, UnityEditor.SceneManagement.OpenSceneMode.Single);
        Append("Reopened authoring scene after build: " + sceneToOpen);
    }

    static void FixSceneFonts(string[] scenes)
    {
        Font bundled = AssetDatabase.LoadAssetAtPath<Font>(
            "Assets/LycheeReplayDemo/Resources/Fonts/LXGWWenKai-Regular.ttf");
        if (bundled == null)
        {
            Append("WARNING: bundled font not found, skipping scene font fix.");
            return;
        }

        int fixedCount = 0;
        foreach (string scenePath in scenes)
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                scenePath, UnityEditor.SceneManagement.OpenSceneMode.Single);
            foreach (TextMesh textMesh in UnityEngine.Object.FindObjectsByType<TextMesh>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                // 只保留"工程内(Assets/)字体"(即已绑 LXGW)；内置 Arial 的资产路径是 Library/unity default resources、
                // 非空但不以 Assets/ 开头，旧逻辑误当"已是资产"跳过 → 中文在 WebGL 空白。改为非 Assets/ 字体(含内置 Arial)与 null 一律换 LXGW。
                if (textMesh.font != null && AssetDatabase.GetAssetPath(textMesh.font).StartsWith("Assets/")) continue;
                textMesh.font = bundled;
                MeshRenderer meshRenderer = textMesh.GetComponent<MeshRenderer>();
                if (meshRenderer != null) meshRenderer.sharedMaterial = bundled.material;
                fixedCount++;
            }
            foreach (UnityEngine.UI.Text text in UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Text>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (text.font != null && AssetDatabase.GetAssetPath(text.font).StartsWith("Assets/")) continue;
                text.font = bundled;
                fixedCount++;
            }
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
        }
        Append("Scene font fix: " + fixedCount + " text components now use bundled LXGWWenKai.");
    }

    static void FixReplayCameraControllers(string[] scenes)
    {
        int fixedCount = 0;
        foreach (string scenePath in scenes ?? Array.Empty<string>())
        {
            if (string.IsNullOrEmpty(scenePath) || !File.Exists(scenePath)) continue;
            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                scenePath, UnityEditor.SceneManagement.OpenSceneMode.Single);

            LycheeReplayPlayer player = UnityEngine.Object.FindFirstObjectByType<LycheeReplayPlayer>(FindObjectsInactive.Include);
            bool dirty = false;
            foreach (Camera camera in UnityEngine.Object.FindObjectsByType<Camera>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (camera == null) continue;
                LycheeReplayCameraController controller = camera.GetComponent<LycheeReplayCameraController>();
                if (controller == null && player == null) continue;
                if (controller == null) controller = camera.gameObject.AddComponent<LycheeReplayCameraController>();

                controller.replay = player;
                controller.enabled = true;
                controller.perspectiveMode = true;
                controller.perspectiveFieldOfView = 42f;
                controller.perspectivePitch = 45f;
                controller.perspectiveDistanceSpanScale = 1.08f;
                controller.perspectiveDistancePadding = 9f;
                controller.perspectiveMinDistance = 17f;
                controller.perspectiveMaxDistance = 108f;
                controller.perspectiveEventDistance = 15f;
                controller.perspectiveZoomMultiplier = 1f;
                controller.focusFollowEnabled = false;
                controller.constrainFocusToPlayableBounds = false;
                EditorUtility.SetDirty(controller);
                dirty = true;
                fixedCount++;
            }

            if (dirty)
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
                UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
            }
        }
        Append("Replay camera controller fix: " + fixedCount + " controllers enabled and retuned.");
    }

    static void ValidateStreamingReplayFiles(string[] scenes)
    {
        foreach (string scenePath in scenes)
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                scenePath, UnityEditor.SceneManagement.OpenSceneMode.Single);
            foreach (LycheeReplayPlayer player in UnityEngine.Object.FindObjectsByType<LycheeReplayPlayer>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                string replayPath = string.IsNullOrEmpty(player.streamingReplayPath)
                    ? "replay.txt"
                    : player.streamingReplayPath;
                string assetPath = "Assets/StreamingAssets/" + replayPath.Replace('\\', '/');
                if (!File.Exists(assetPath))
                {
                    string fallback = player.replayFile == null ? "no embedded fallback" : "embedded fallback present";
                    Append("WARNING: " + scene.name + " expects StreamingAssets/" + replayPath +
                           " but it is missing (" + fallback + ").");
                }

                if (replayPath.StartsWith("Tournament/", StringComparison.OrdinalIgnoreCase) &&
                    replayPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                {
                    string configPath = "Assets/StreamingAssets/" +
                        replayPath.Substring(0, replayPath.Length - ".txt".Length).Replace('\\', '/') + "_config.json";
                    if (!File.Exists(configPath))
                        Append("WARNING: sidecar config missing for " + scene.name + ": " + configPath);
                }
            }
        }
    }

    /// <summary>
    /// 把重型地形/山体贴图在 WebGL 平台降到 WebGLTextureMaxSize + Crunch 压缩——这是 .data(~466MB)的大头。
    /// 仅加 WebGL 平台覆盖：Standalone/Server/原生包不受影响、保持 4K/2K 全质量。
    /// 在构建前执行并 SaveAndReimport，确保烤进 .data 的是降采样后的版本。
    /// </summary>
    static void OptimizeWebGLTextures()
    {
        int changed = 0;
        foreach (string folder in HeavyTextureFolders)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;

                TextureImporterPlatformSettings ps = importer.GetPlatformTextureSettings("WebGL");
                bool needsChange = !ps.overridden
                    || ps.maxTextureSize != WebGLTextureMaxSize
                    || !ps.crunchedCompression
                    || ps.textureCompression != TextureImporterCompression.Compressed;
                if (!needsChange) continue;

                ps.overridden = true;
                ps.maxTextureSize = WebGLTextureMaxSize;
                ps.textureCompression = TextureImporterCompression.Compressed;
                ps.crunchedCompression = true;   // Crunch：在 DXT 之上进一步缩“下载体积”，WebGL 首选
                ps.compressionQuality = 50;
                ps.format = TextureImporterFormat.Automatic;
                importer.SetPlatformTextureSettings(ps);
                importer.SaveAndReimport();
                changed++;
            }
        }
        Append("WebGL texture downscale: " + changed + " heavy textures -> " + WebGLTextureMaxSize + "px + Crunch (WebGL override only).");
    }

    /// <summary>
    /// Play View 能靠场景引用找到这些 prefab，但 WebGL 构建裁剪后容易丢掉运行时头顶事件模型。
    /// 构建前固定到 PreloadedAssets，确保冰鉴/快马/荔枝转坏三组 baked 模型、mesh/material/texture 链进入 .data。
    /// </summary>
    static void EnsureWebGLPreloadedEventModels()
    {
        string[] assetPaths =
        {
            "Assets/LycheeReplayDemo/EventModelBaked/event_icebox_baked.prefab",
            "Assets/LycheeReplayDemo/EventModelBaked/event_horse_baked.prefab",
            "Assets/LycheeReplayDemo/EventModelBaked/event_spoil_baked.prefab",
            "Assets/LycheeReplayDemo/Shaders/LycheeOverheadEventModel.shader",
        };

        List<UnityEngine.Object> merged = new List<UnityEngine.Object>();
        UnityEngine.Object[] existing = PlayerSettings.GetPreloadedAssets();
        if (existing != null)
        {
            for (int i = 0; i < existing.Length; i++)
            {
                if (existing[i] != null && !merged.Contains(existing[i]))
                    merged.Add(existing[i]);
            }
        }

        int added = 0;
        for (int i = 0; i < assetPaths.Length; i++)
        {
            UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPaths[i]);
            if (asset == null)
            {
                Append("WARNING: WebGL event preloaded asset missing: " + assetPaths[i]);
                continue;
            }
            if (merged.Contains(asset)) continue;
            merged.Add(asset);
            added++;
        }

        PlayerSettings.SetPreloadedAssets(merged.ToArray());
        AssetDatabase.SaveAssets();
        Append("WebGL event model preloads: +" + added + ", total preloaded assets=" + merged.Count + ".");
    }

    static WebGLRenderPipelineBackup ApplyWebGLRenderPipelineOverrides()
    {
        WebGLRenderPipelineBackup backup = new WebGLRenderPipelineBackup();
        UnityEngine.Rendering.RenderPipelineAsset pipeline = UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline;
        if (pipeline == null) pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
        if (pipeline == null)
        {
            Append("WARNING: WebGL render pipeline override skipped: no active render pipeline asset.");
            return backup;
        }

        SerializedObject pipelineSo = new SerializedObject(pipeline);
        backup.SetBool(pipelineSo, "m_SupportsHDR", false);
        backup.SetBool(pipelineSo, "m_RequireDepthTexture", false);
        backup.SetBool(pipelineSo, "m_RequireOpaqueTexture", false);
        backup.SetBool(pipelineSo, "m_EnableLODCrossFade", false);
        backup.SetBool(pipelineSo, "m_FsrOverrideSharpness", false);
        backup.SetBool(pipelineSo, "m_SupportDataDrivenLensFlare", false);
        backup.SetBool(pipelineSo, "m_SupportScreenSpaceLensFlare", false);
        backup.SetBool(pipelineSo, "m_PrefilterHDROutput", false);
        backup.SetFloat(pipelineSo, "m_RenderScale", 1f);
        backup.SetInt(pipelineSo, "m_MSAA", 1);
        backup.SetInt(pipelineSo, "m_UpscalingFilter", 1); // Bilinear: avoid FSR auto-selection on WebGL.

        SerializedProperty rendererList = pipelineSo.FindProperty("m_RendererDataList");
        if (rendererList != null && rendererList.isArray)
        {
            for (int i = 0; i < rendererList.arraySize; i++)
            {
                SerializedProperty element = rendererList.GetArrayElementAtIndex(i);
                UnityEngine.Object rendererData = element == null ? null : element.objectReferenceValue;
                if (rendererData == null) continue;

                SerializedObject rendererSo = new SerializedObject(rendererData);
                backup.SetInt(rendererSo, "m_RenderingMode", 0); // Forward: WebGL-safe path.
                backup.SetBool(rendererSo, "m_UseNativeRenderPass", false);
                backup.SetObject(rendererSo, "postProcessData", null);
                backup.SetObject(rendererSo, "debugShaders.hdrDebugViewPS", null);

                SerializedProperty features = rendererSo.FindProperty("m_RendererFeatures");
                if (features != null && features.isArray)
                {
                    for (int f = 0; f < features.arraySize; f++)
                    {
                        UnityEngine.Object feature = features.GetArrayElementAtIndex(f).objectReferenceValue;
                        if (feature == null) continue;
                        SerializedObject featureSo = new SerializedObject(feature);
                        backup.SetBool(featureSo, "m_Active", false);
                    }
                }
            }
        }

        backup.ApplyAndSave();
        Append("WebGL render pipeline override: Forward/no HDR/no postprocess/no SSAO for build only.");
        return backup;
    }

    sealed class WebGLRenderPipelineBackup
    {
        struct Entry
        {
            public UnityEngine.Object target;
            public string propertyPath;
            public SerializedPropertyType type;
            public bool boolValue;
            public int intValue;
            public float floatValue;
            public UnityEngine.Object objectValue;
        }

        readonly List<Entry> entries = new List<Entry>();
        readonly List<SerializedObject> touched = new List<SerializedObject>();

        public void SetBool(SerializedObject so, string propertyPath, bool value)
        {
            SerializedProperty property = FindWritable(so, propertyPath, SerializedPropertyType.Boolean);
            if (property == null || property.boolValue == value) return;
            entries.Add(new Entry { target = so.targetObject, propertyPath = propertyPath, type = property.propertyType, boolValue = property.boolValue });
            property.boolValue = value;
            Touch(so);
        }

        public void SetInt(SerializedObject so, string propertyPath, int value)
        {
            SerializedProperty property = FindWritable(so, propertyPath, SerializedPropertyType.Integer);
            if (property == null || property.intValue == value) return;
            entries.Add(new Entry { target = so.targetObject, propertyPath = propertyPath, type = property.propertyType, intValue = property.intValue });
            property.intValue = value;
            Touch(so);
        }

        public void SetFloat(SerializedObject so, string propertyPath, float value)
        {
            SerializedProperty property = FindWritable(so, propertyPath, SerializedPropertyType.Float);
            if (property == null || Mathf.Approximately(property.floatValue, value)) return;
            entries.Add(new Entry { target = so.targetObject, propertyPath = propertyPath, type = property.propertyType, floatValue = property.floatValue });
            property.floatValue = value;
            Touch(so);
        }

        public void SetObject(SerializedObject so, string propertyPath, UnityEngine.Object value)
        {
            SerializedProperty property = FindWritable(so, propertyPath, SerializedPropertyType.ObjectReference);
            if (property == null || property.objectReferenceValue == value) return;
            entries.Add(new Entry { target = so.targetObject, propertyPath = propertyPath, type = property.propertyType, objectValue = property.objectReferenceValue });
            property.objectReferenceValue = value;
            Touch(so);
        }

        public void ApplyAndSave()
        {
            ApplyTouched();
        }

        public void Restore()
        {
            try
            {
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    Entry entry = entries[i];
                    if (entry.target == null) continue;
                    SerializedObject so = new SerializedObject(entry.target);
                    SerializedProperty property = so.FindProperty(entry.propertyPath);
                    if (property == null) continue;
                    switch (entry.type)
                    {
                        case SerializedPropertyType.Boolean:
                            property.boolValue = entry.boolValue;
                            break;
                        case SerializedPropertyType.Integer:
                            property.intValue = entry.intValue;
                            break;
                        case SerializedPropertyType.Float:
                            property.floatValue = entry.floatValue;
                            break;
                        case SerializedPropertyType.ObjectReference:
                            property.objectReferenceValue = entry.objectValue;
                            break;
                    }
                    so.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(entry.target);
                }
                AssetDatabase.SaveAssets();
                if (entries.Count > 0) Append("WebGL render pipeline override restored: " + entries.Count + " properties.");
            }
            catch (Exception exception)
            {
                Append("WARNING: WebGL render pipeline override restore failed: " + exception.Message);
            }
        }

        SerializedProperty FindWritable(SerializedObject so, string propertyPath, SerializedPropertyType expectedType)
        {
            if (so == null || so.targetObject == null) return null;
            SerializedProperty property = so.FindProperty(propertyPath);
            if (property == null) return null;
            if (property.propertyType != expectedType) return null;
            return property;
        }

        void Touch(SerializedObject so)
        {
            if (so == null || touched.Contains(so)) return;
            touched.Add(so);
        }

        void ApplyTouched()
        {
            foreach (SerializedObject so in touched)
            {
                if (so == null || so.targetObject == null) continue;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(so.targetObject);
            }
            if (touched.Count > 0) AssetDatabase.SaveAssets();
        }
    }

    /// <summary>
    /// 枚举 UITheme 运行时素材写入 uitheme_manifest.txt——WebGL 无法列目录，
    /// LycheeThemeCache 启动时按清单预载（素材增删后重新构建即自动生效）。
    /// </summary>
    static void GenerateThemeManifest()
    {
        string themeDir = "Assets/StreamingAssets/UITheme";
        if (!Directory.Exists(themeDir)) return;
        var names = new System.Collections.Generic.List<string>();
        // 递归枚举（含 EventIcons/ 与 CardBorders/ 子目录），写相对 UITheme 的正斜杠路径；
        // WebGL 端 LycheeThemeCache 按本清单逐个预载，桌面端 LoadBytes 同样按相对路径读取。
        foreach (string file in Directory.GetFiles(themeDir, "*.*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(file);
            if (!string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
                continue;
            string rel = file.Substring(themeDir.Length + 1).Replace('\\', '/');
            names.Add(rel);
        }
        names.Sort(StringComparer.Ordinal);
        string manifestPath = themeDir + "/uitheme_manifest.txt";
        File.WriteAllText(manifestPath, string.Join("\n", names) + "\n");
        AssetDatabase.ImportAsset(manifestPath);
        Append("Theme manifest generated: " + names.Count + " entries.");
    }

    /// <summary>
    /// 构建产物 index.html 补丁：桌面端让画布铺满浏览器窗口，
    /// 保证浏览器缩放/全屏时没有页面级裁切或黑边；默认跟随屏幕 DPI，并允许通过 URL 参数启用 4K/UHD 或指定 DPR。
    /// </summary>
    static void PatchIndexHtml()
    {
        string indexPath = OutputDir + "/index.html";
        if (!File.Exists(indexPath)) return;
        string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        string html = File.ReadAllText(indexPath);
        html = html.Replace(
            "var loaderUrl = buildUrl + ",
            "var buildStamp = \"" + stamp + "\";\n      var loaderUrl = buildUrl + ");
        // 标题用正则整体替换：旧补丁只匹配 "Unity Web Player | My project"，但 Unity 6 模板实际是
        // "Unity WebGL Player | My project"（多了 GL），导致替换静默失效、浏览器标签仍显示开发用标题。
        html = Regex.Replace(html, "<title>.*?</title>", "<title>一骑红尘：荔枝争运战 · 回放</title>", RegexOptions.Singleline);
        html = html.Replace(
            "</head>",
            "    <base href=\"/" + DeployPrefix + "/\">\n" +   // 部署在子路径下：相对资源(Build/、StreamingAssets/)挂到 /litchi_delivery_replay/
            "    <style>\n" +
            "      html, body { margin: 0; width: 100%; height: 100%; overflow: hidden; background: #050505; }\n" +
            "      #unity-container.unity-desktop { position: fixed; inset: 0; left: 0; top: 0; transform: none; width: 100vw; height: 100vh; display: block; overflow: hidden; background: #050505; }\n" +
            "      #unity-canvas { width: 100vw; height: 100vh; max-width: 100vw; max-height: 100vh; display: block; background: #000; }\n" +
            "      #unity-footer { display: none; }\n" +
            "      #unity-warning { position: fixed; top: 16px; left: 50%; transform: translateX(-50%); max-width: min(720px, calc(100vw - 32px)); z-index: 20; color: #f7dfae; background: rgba(36, 16, 12, 0.94); border: 1px solid #a77a35; border-radius: 6px; box-shadow: 0 18px 46px rgba(0,0,0,0.42); }\n" +
            "      #unity-loading-bar { position: fixed; left: 50%; top: 50%; transform: translate(-50%, -50%); width: min(520px, calc(100vw - 48px)); padding: 28px 34px 30px; background: linear-gradient(180deg, rgba(27,22,18,0.96), rgba(12,10,9,0.96)); border: 1px solid #9c7439; border-radius: 8px; box-shadow: 0 28px 80px rgba(0,0,0,0.58); }\n" +
            "      #unity-logo { display: none; }\n" +
            "      .lychee-loading-title { color: #e8c36a; font: 700 30px/1.2 'Microsoft YaHei', 'Noto Sans CJK SC', sans-serif; text-align: center; letter-spacing: 0; }\n" +
            "      .lychee-loading-subtitle { margin-top: 10px; color: #eadfcb; font: 500 15px/1.5 'Microsoft YaHei', 'Noto Sans CJK SC', sans-serif; text-align: center; }\n" +
            "      #unity-progress-bar-empty { width: 100%; height: 12px; margin-top: 22px; background: rgba(238,213,154,0.18); border: 1px solid rgba(232,195,106,0.5); border-radius: 999px; overflow: hidden; }\n" +
            "      #unity-progress-bar-full { height: 100%; background: linear-gradient(90deg, #a93523, #e8c36a); border-radius: 999px; }\n" +
            // 全屏游戏加载封面：首帧即覆盖整窗，杜绝默认模板/开发页面观感；Unity 实例就绪后淡出移除。
            // 背景三层叠加：暗罩(保证金字可读) → 全景地图(lychee_loading_bg.jpg，构建时若存在则拷入包) → 渐变兜底(图缺失时显示)。
            "      #lychee-splash { position: fixed; inset: 0; z-index: 50; display: flex; align-items: center; justify-content: center; background: linear-gradient(rgba(8,6,5,0.50), rgba(8,6,5,0.74)), url('lychee_loading_bg.jpg') center/cover no-repeat, radial-gradient(ellipse at 50% 34%, #2a1d12 0%, #120d0a 68%, #050505 100%); transition: opacity 0.55s ease; }\n" +
            "      #lychee-splash .lychee-splash-card { text-align: center; padding: 0 24px; }\n" +
            "      #lychee-splash .lychee-splash-title { color: #e8c36a; font: 800 48px/1.15 'Microsoft YaHei','Noto Sans CJK SC',sans-serif; letter-spacing: 8px; text-shadow: 0 6px 26px rgba(0,0,0,0.62); }\n" +
            "      #lychee-splash .lychee-splash-sub { margin-top: 20px; color: #f3e6c9; font: 600 20px/1.5 'Microsoft YaHei','Noto Sans CJK SC',sans-serif; }\n" +
            "      #lychee-splash .lychee-splash-en { margin-top: 6px; color: #bca878; font: 500 14px/1.5 'Segoe UI',sans-serif; letter-spacing: 1.5px; }\n" +
            "      #lychee-splash .lychee-splash-hint { margin-top: 4px; color: #9b8a68; font: 500 13px/1.6 'Microsoft YaHei','Noto Sans CJK SC',sans-serif; }\n" +
            "      #lychee-splash .lychee-splash-track { width: min(360px, 72vw); height: 6px; margin: 28px auto 0; background: rgba(238,213,154,0.16); border-radius: 999px; overflow: hidden; }\n" +
            "      #lychee-splash .lychee-splash-glow { width: 38%; height: 100%; border-radius: 999px; background: linear-gradient(90deg, #a93523, #e8c36a); animation: lycheeSlide 1.25s ease-in-out infinite; }\n" +
            "      @keyframes lycheeSlide { 0% { margin-left: -42%; } 100% { margin-left: 104%; } }\n" +
            "    </style>\n" +
            "  </head>");
        // 注入封面 DOM（放在 unity-container 前；用子串匹配，不依赖 class 写法）。
        html = html.Replace(
            "<div id=\"unity-container\"",
            "<div id=\"lychee-splash\"><div class=\"lychee-splash-card\">" +
            "<div class=\"lychee-splash-title\">一骑红尘：荔枝争运战</div>" +
            "<div class=\"lychee-splash-sub\">地图资源加载中，请耐心等待</div>" +
            "<div class=\"lychee-splash-en\">Loading map assets, please wait...</div>" +
            "<div class=\"lychee-splash-hint\">首次加载资源较多，请勿关闭页面</div>" +
            "<div class=\"lychee-splash-track\"><div class=\"lychee-splash-glow\"></div></div>" +
            "</div></div>\n    <div id=\"unity-container\"");
        html = html.Replace(
            "<div id=\"unity-logo\"></div>",
            "<div id=\"unity-logo\"></div><div class=\"lychee-loading-title\">一骑红尘：荔枝争运战</div><div class=\"lychee-loading-subtitle\">正在加载游戏资源...<br>Loading game assets...<br>正在准备地图、队伍与事件回放</div>");
        // 引擎就绪后不立即移除封面——createUnityInstance 解析时首帧场景仍在预热(植被/地形/小地图RT)，
        // 立即移除会露出"卡住"的首帧。延时 2.6s 覆盖预热；30s 兜底与报错路径仍会移除。
        html = html.Replace(
            ".then((unityInstance) => {",
            ".then((unityInstance) => {\n          window.__unityInstance = unityInstance;\n          setTimeout(function(){ if (window.__lycheeHideSplash) window.__lycheeHideSplash(); }, 2600);\n");
        // 进度到 100% 不再移除封面——那只是下载完成、引擎尚未初始化/首帧未出，移早了正是"卡住"露馅处；
        // 统一由上面 .then 的延时移除 + 30s 兜底 + 报错路径处理。
        html = html.Replace(".loader.js\";", ".loader.js\" + \"?v=\" + buildStamp;");
        html = html.Replace(".data.unityweb\",", ".data.unityweb\" + \"?v=\" + buildStamp,");
        html = html.Replace(".framework.js.unityweb\",", ".framework.js.unityweb\" + \"?v=\" + buildStamp,");
        html = html.Replace(".wasm.unityweb\",", ".wasm.unityweb\" + \"?v=\" + buildStamp,");
        html = html.Replace("      // config.autoSyncPersistentDataPath = true;", "      config.autoSyncPersistentDataPath = true;");
        html = html.Replace(
            "createUnityInstance(canvas, config, (progress) => {",
            "window.__lycheeHideSplash = function() { var sp = document.getElementById('lychee-splash'); if (!sp) return; sp.style.opacity = '0'; setTimeout(function(){ if (sp && sp.parentNode) sp.parentNode.removeChild(sp); }, 600); };\n" +
            "        setTimeout(function(){ if (window.__lycheeHideSplash) window.__lycheeHideSplash(); }, 30000);\n" +
            "        var queryParams = new URLSearchParams(window.location.search);\n" +
            "        var requestedDpr = parseFloat(queryParams.get('dpr') || '');\n" +
            "        var qualityMode = (queryParams.get('quality') || '').toLowerCase();\n" +
            "        var renderDpr = Math.min(window.devicePixelRatio || 1, 2);\n" +
            "        if (qualityMode === 'performance' || qualityMode === 'fast') renderDpr = 1;\n" +
            "        if (qualityMode === '4k' || qualityMode === 'uhd') {\n" +
            "          renderDpr = Math.max(renderDpr, Math.min(3, Math.max(\n" +
            "            3840 / Math.max(1, window.innerWidth),\n" +
            "            2160 / Math.max(1, window.innerHeight))));\n" +
            "        }\n" +
            "        if (!isNaN(requestedDpr) && requestedDpr > 0) renderDpr = Math.min(4, Math.max(0.75, requestedDpr));\n" +
            "        config.devicePixelRatio = renderDpr;\n" +
            "        fitUnityCanvas();\n" +
            "        window.__lycheeResumeAudio = function(){ try { var ctx = (typeof WEBAudio !== 'undefined' && WEBAudio && WEBAudio.audioContext) ? WEBAudio.audioContext : null; if (!ctx && typeof Module !== 'undefined' && Module) ctx = Module.webAudioContext || Module.audioContext || null; if (ctx && ctx.state === 'suspended' && ctx.resume) ctx.resume(); } catch(e) {} };\n" +
            "        ['pointerdown','touchstart','keydown'].forEach(function(t){ window.addEventListener(t, window.__lycheeResumeAudio, { passive: true }); });\n" +
            "        createUnityInstance(canvas, config, (progress) => {");
        string shaderLogFilter =
            "      (function(){\n" +
            "        var suppressedShaders = ['Hidden/CoreSRP/CoreCopy', 'Hidden/Universal Render Pipeline/StencilDitherMaskSeed', 'Hidden/Universal/HDRDebugView'];\n" +
            "        var rawLog = console.log.bind(console);\n" +
            "        var pendingShaderPrefix = false;\n" +
            "        console.log = function(){\n" +
            "          var args = Array.prototype.slice.call(arguments);\n" +
            "          var msg = args.map(function(v){ return String(v); }).join(' ');\n" +
            "          if (msg.trim() === 'ERROR: Shader') { pendingShaderPrefix = true; return; }\n" +
            "          for (var i = 0; i < suppressedShaders.length; i++) { if (msg.indexOf(suppressedShaders[i]) >= 0) { pendingShaderPrefix = false; return; } }\n" +
            "          if (pendingShaderPrefix) { pendingShaderPrefix = false; rawLog('ERROR: Shader '); }\n" +
            "          return rawLog.apply(console, args);\n" +
            "        };\n" +
            "      })();\n\n";
        html = html.Replace("      var script = document.createElement(\"script\");", shaderLogFilter + "      var script = document.createElement(\"script\");");
        html = html.Replace(
            "alert(message);",
            "console.error(message);\n                if (window.__lycheeHideSplash) window.__lycheeHideSplash();\n                unityShowBanner('加载失败：请确认通过 HTTP 服务访问完整构建目录，且 Build/StreamingAssets 未缺失。<br>' + message, 'error');");
        html = html.Replace(
            "if (type == 'error') div.style = 'background: red; padding: 10px;';",
            "if (type == 'error') div.style = 'background: rgba(95,24,18,0.92); color: #ffe6b5; padding: 12px 16px; border-radius: 6px;';");
        html = html.Replace(
            "if (type == 'warning') div.style = 'background: yellow; padding: 10px;';",
            "if (type == 'warning') div.style = 'background: rgba(154,103,33,0.92); color: #fff3cf; padding: 12px 16px; border-radius: 6px;';");
        string fitCanvasScript =
            "      function fitUnityCanvas() {\n" +
            "        var container = document.querySelector(\"#unity-container\");\n" +
            "        var viewport = window.visualViewport;\n" +
            "        var ww = Math.max(1, viewport ? viewport.width : window.innerWidth);\n" +
            "        var wh = Math.max(1, viewport ? viewport.height : window.innerHeight);\n" +
            "        if (container) {\n" +
            "          container.style.position = \"fixed\";\n" +
            "          container.style.inset = \"0\";\n" +
            "          container.style.left = \"0\";\n" +
            "          container.style.top = \"0\";\n" +
            "          container.style.transform = \"none\";\n" +
            "          container.style.width = ww + \"px\";\n" +
            "          container.style.height = wh + \"px\";\n" +
            "          container.style.display = \"block\";\n" +
            "          container.style.overflow = \"hidden\";\n" +
            "        }\n" +
            "        canvas.style.width = ww + \"px\";\n" +
            "        canvas.style.height = wh + \"px\";\n" +
            "        canvas.style.maxWidth = \"100vw\";\n" +
            "        canvas.style.maxHeight = \"100vh\";\n" +
            "        canvas.style.display = \"block\";\n" +
            "      }\n" +
            "      window.addEventListener(\"resize\", fitUnityCanvas);\n" +
            "      window.addEventListener(\"orientationchange\", fitUnityCanvas);\n" +
            "      if (window.visualViewport) window.visualViewport.addEventListener(\"resize\", fitUnityCanvas);\n\n";
        html = html.Replace("      if (/iPhone|iPad|iPod|Android/i.test(navigator.userAgent)) {", fitCanvasScript + "      if (/iPhone|iPad|iPod|Android/i.test(navigator.userAgent)) {");
        html = html.Replace("canvas.style.width = \"960px\";\n        canvas.style.height = \"600px\";", "fitUnityCanvas();");
        html = html.Replace("canvas.style.width = \"min(100vw, calc(100vh * 16 / 9))\";\n        canvas.style.height = \"min(100vh, calc(100vw * 9 / 16))\";", "fitUnityCanvas();");
        File.WriteAllText(indexPath, html);
        // 全景地图封面图：若 Assets 下有 lychee_loading_bg.jpg 就拷进包根(与 index.html 同级)，CSS 以相对路径引用；
        // 没有则 splash 退回渐变兜底(见 #lychee-splash 背景叠层)。把想要的全景截图放到该源路径，下次构建即生效。
        string loadBg = "Assets/LycheeReplayDemo/Editor/lychee_loading_bg.jpg";
        if (File.Exists(loadBg)) { File.Copy(loadBg, OutputDir + "/lychee_loading_bg.jpg", true); Append("Loading panorama copied into package (lychee_loading_bg.jpg)."); }
        else Append("No lychee_loading_bg.jpg in Assets — splash uses gradient fallback. Drop a JPG at " + loadBg + " for a map panorama.");
        Append("index.html patched (cache bust=" + stamp + ", branded loading, adaptive DPR/4K option, full-window canvas).");
    }

    static void WritePackageReadme(string[] scenes)
    {
        Directory.CreateDirectory(OutputDir);
        string text =
            "一骑红尘：荔枝争运战 WebGL 构建包使用说明\n" +
            "==============================\n\n" +
            "不要直接双击 index.html，也不要只拷贝 Build 目录。请把整个文件夹原样放到黄区服务器或用本地 HTTP 服务打开。\n\n" +
            "目录必须包含：\n" +
            "- index.html\n" +
            "- Build/\n" +
            "- StreamingAssets/\n" +
            "- TemplateData/\n\n" +
            "推荐访问方式：\n" +
            "- 打开回放：http://服务器地址/本目录/index.html\n" +
            "- 指定外部回放：http://服务器地址:" + ServePort + "/" + DeployPrefix + "/?path=/data/results/game/replay/serve/replay.txt\n" +
            "- 指定包内示例：http://服务器地址:" + ServePort + "/" + DeployPrefix + "/?path=/StreamingAssets/Tournament/map1.txt\n" +
            "- 指定地图：http://服务器地址:" + ServePort + "/" + DeployPrefix + "/?map=map_1\n" +
            "- 地图选择页：http://服务器地址:" + ServePort + "/" + DeployPrefix + "/?map=select\n" +
            "- 指定地图 + 外部回放：http://服务器地址:" + ServePort + "/" + DeployPrefix + "/?map=map_1&path=/data/results/game/replay/serve/replay.txt\n\n" +
            "离线使用：\n" +
            "- 本包已包含 Build、StreamingAssets、UITheme、Video、默认 replay.txt，不需要访问外网。\n" +
            "- 默认地址 http://127.0.0.1:" + ServePort + "/" + DeployPrefix + "/ 读取包内 StreamingAssets/replay.txt。\n" +
            "- ?path=/data/results/.../replay.txt 读取的是 Linux 本机磁盘上的 /data 路径，不是外网请求。\n" +
            "- 只有 ?path=http://... 才需要机器能访问对应 HTTP 地址；离线机器不要用 http://... 形式。\n\n" +
            "本机临时测试：\n" +
            "- Linux：执行 ./start.sh；默认端口 " + ServePort + "，访问路径前缀 /" + DeployPrefix + "/，HOST=0.0.0.0，NO_OPEN=1 可只启动不打开浏览器。\n" +
            "- Windows：双击 start.bat 或 start_local_server.bat；脚本会优先使用 PowerShell 内置服务器。\n\n" +
            "常见卡住原因：\n" +
            "- 双击 index.html：浏览器会阻止 StreamingAssets 文本读取，回放无法启动。\n" +
            "- 只上传 Build/：缺少 StreamingAssets/Tournament/map1.txt，Unity 能开但车队/镜头不会推进。\n" +
            "- 黄区服务器缓存旧包：地址可加 ?v=日期，或清掉浏览器缓存后重试。\n" +
            "- 机器性能不足：首次加载约 500MB WebGL 资源，内存不足时可能加载很久或浏览器终止页面。\n\n" +
            "画质参数：\n" +
            "- 默认：跟随屏幕 DPI，最高 DPR 2，比旧包强制 DPR 1 更清晰。\n" +
            "- 4K/UHD 验收：地址后加 ?quality=4k；如果已有 ?path=...，则追加 &quality=4k；也可显式加 ?dpr=2 或 ?dpr=3。\n" +
            "- 性能优先：地址后加 ?quality=performance 或 ?dpr=1。远程桌面/低配 Linux 机器优先用这个。\n\n" +
            "地图切换说明：\n" +
            "- 当前包默认包含 demo、map_1、select；只有打进 Build Settings 的地图才能通过 ?map=... 加载。\n" +
            "- 后续多图要先生成 Map_map2/Map_map3 等场景，把场景加入构建，再访问 ?map=map_2、?map=map_3。\n\n" +
            "本包 Build Settings 场景：\n" +
            string.Join("\n", scenes ?? Array.Empty<string>()) + "\n";
        File.WriteAllText(Path.Combine(OutputDir, "README_使用说明.txt"), text);
        string asciiText =
            "Lychee Replay WebGL package\n" +
            "==========================\n\n" +
            "Open this package through an HTTP server. Do not double-click index.html and do not upload only the Build folder.\n\n" +
            "Required folders/files:\n" +
            "- index.html\n" +
            "- Build/\n" +
            "- StreamingAssets/\n" +
            "- TemplateData/\n\n" +
            "Local test:\n" +
            "- Linux: run ./start.sh. Default port is " + ServePort + ", URL prefix /" + DeployPrefix + "/; set NO_OPEN=1 to start without opening a browser.\n" +
            "- Windows: double-click start.bat or start_local_server.bat. Use stop.bat to stop it.\n" +
            "- External replay: http://host:" + ServePort + "/" + DeployPrefix + "/?path=/data/results/game/replay/serve/replay.txt\n" +
            "- Map switch: http://host:" + ServePort + "/" + DeployPrefix + "/?map=map_1 ; selector: http://host:" + ServePort + "/" + DeployPrefix + "/?map=select\n" +
            "- Map + replay: http://host:" + ServePort + "/" + DeployPrefix + "/?map=map_1&path=/data/results/game/replay/serve/replay.txt\n\n" +
            "Offline use:\n" +
            "- The package includes Build, StreamingAssets, UITheme, Video, and default replay.txt. No internet is required.\n" +
            "- http://127.0.0.1:" + ServePort + "/" + DeployPrefix + "/ loads packaged StreamingAssets/replay.txt.\n" +
            "- ?path=/data/results/.../replay.txt reads a local Linux filesystem path through the bundled server.\n" +
            "- Only ?path=http://... requires network access.\n\n" +
            "Render quality:\n" +
            "- Default: follows display DPI up to DPR 2.\n" +
            "- 4K/UHD check: add ?quality=4k, or append &quality=4k when the URL already has ?path=.... You can also set ?dpr=2 / ?dpr=3 explicitly.\n" +
            "- Performance mode: add ?quality=performance or ?dpr=1.\n\n" +
            "Build scenes:\n" +
            string.Join("\n", scenes ?? Array.Empty<string>()) + "\n";
        File.WriteAllText(Path.Combine(OutputDir, "README_usage.txt"), asciiText);
        File.WriteAllText(Path.Combine(OutputDir, "启动本地服务.command"),
            "#!/bin/bash\ncd \"$(dirname \"$0\")\"\nexec ./start.command\n");
        File.WriteAllText(Path.Combine(OutputDir, "start_local_server.command"),
            "#!/bin/bash\ncd \"$(dirname \"$0\")\"\nexec ./start.command\n");
        File.WriteAllText(Path.Combine(OutputDir, "start_local_server.ps1"),
            "$ErrorActionPreference = 'Stop'\r\n" +
            "$root = Split-Path -Parent $MyInvocation.MyCommand.Path\r\n" +
            "$port = " + ServePort + "\r\n" +
            "$url = \"http://127.0.0.1:$port/" + DeployPrefix + "/\"\r\n" +
            "$listener = [System.Net.HttpListener]::new()\r\n" +
            "$listener.Prefixes.Add(\"http://127.0.0.1:$port/\")\r\n" +
            "$mime = @{\r\n" +
            "  '.html'='text/html; charset=utf-8'; '.js'='application/javascript'; '.css'='text/css';\r\n" +
            "  '.wasm'='application/wasm'; '.unityweb'='application/octet-stream'; '.data'='application/octet-stream';\r\n" +
            "  '.json'='application/json'; '.txt'='text/plain; charset=utf-8'; '.png'='image/png'; '.jpg'='image/jpeg';\r\n" +
            "  '.jpeg'='image/jpeg'; '.ico'='image/x-icon'; '.mp4'='video/mp4'; '.md'='text/plain; charset=utf-8'\r\n" +
            "}\r\n" +
            "function Send-Status($ctx, $code, $text) {\r\n" +
            "  $bytes = [Text.Encoding]::UTF8.GetBytes($text)\r\n" +
            "  $ctx.Response.StatusCode = $code\r\n" +
            "  $ctx.Response.ContentType = 'text/plain; charset=utf-8'\r\n" +
            "  $ctx.Response.ContentLength64 = $bytes.Length\r\n" +
            "  $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)\r\n" +
            "  $ctx.Response.Close()\r\n" +
            "}\r\n" +
            "function Get-ContentType($full) {\r\n" +
            "  if ($full.EndsWith('.wasm.unityweb', [StringComparison]::OrdinalIgnoreCase)) { return 'application/wasm' }\r\n" +
            "  if ($full.EndsWith('.framework.js.unityweb', [StringComparison]::OrdinalIgnoreCase)) { return 'application/javascript' }\r\n" +
            "  if ($full.EndsWith('.data.unityweb', [StringComparison]::OrdinalIgnoreCase)) { return 'application/octet-stream' }\r\n" +
            "  $ext = [IO.Path]::GetExtension($full).ToLowerInvariant()\r\n" +
            "  if ($mime.ContainsKey($ext)) { return $mime[$ext] }\r\n" +
            "  return 'application/octet-stream'\r\n" +
            "}\r\n" +
            "try { $listener.Start() } catch {\r\n" +
            "  Write-Host \"Failed to start http://127.0.0.1:$port/\"\r\n" +
            "  Write-Host $_.Exception.Message\r\n" +
            "  Read-Host 'Press Enter to close'\r\n" +
            "  exit 1\r\n" +
            "}\r\n" +
            "Write-Host \"Serving $root at http://127.0.0.1:$port/\"\r\n" +
            "Write-Host \"Opening $url\"\r\n" +
            "Start-Process $url\r\n" +
            "try {\r\n" +
            "  while ($listener.IsListening) {\r\n" +
            "    $ctx = $listener.GetContext()\r\n" +
            "    $requestPath = [Uri]::UnescapeDataString($ctx.Request.Url.AbsolutePath)\r\n" +
            "    $prefix = '/" + DeployPrefix + "'\r\n" +
            "    if ($requestPath -eq $prefix -or $requestPath -eq ($prefix + '/')) { $requestPath = '/' }\r\n" +
            "    elseif ($requestPath.StartsWith($prefix + '/', [StringComparison]::OrdinalIgnoreCase)) { $requestPath = $requestPath.Substring($prefix.Length) }\r\n" +
            "    $rootFull = [IO.Path]::GetFullPath($root)\r\n" +
            "    if ([string]::IsNullOrWhiteSpace($requestPath) -or $requestPath -eq '/') {\r\n" +
            "      $full = [IO.Path]::GetFullPath([IO.Path]::Combine($root, 'index.html'))\r\n" +
            "    } elseif ($requestPath.StartsWith('/data/', [StringComparison]::OrdinalIgnoreCase)) {\r\n" +
            "      $full = [IO.Path]::GetFullPath($requestPath)\r\n" +
            "    } else {\r\n" +
            "      $relative = $requestPath.TrimStart('/') -replace '/', [IO.Path]::DirectorySeparatorChar\r\n" +
            "      $full = [IO.Path]::GetFullPath([IO.Path]::Combine($root, $relative))\r\n" +
            "      if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { Send-Status $ctx 403 'Forbidden'; continue }\r\n" +
            "    }\r\n" +
            "    if (-not [IO.File]::Exists($full)) { Send-Status $ctx 404 'Not Found'; continue }\r\n" +
            "    $ctx.Response.StatusCode = 200\r\n" +
            "    $ctx.Response.ContentType = Get-ContentType $full\r\n" +
            "    if ($full.EndsWith('.unityweb', [StringComparison]::OrdinalIgnoreCase)) { $ctx.Response.Headers['Content-Encoding'] = 'gzip' }\r\n" +
            "    $ctx.Response.Headers['Cache-Control'] = 'no-store'\r\n" +
            "    $stream = [IO.File]::OpenRead($full)\r\n" +
            "    try {\r\n" +
            "      $ctx.Response.ContentLength64 = $stream.Length\r\n" +
            "      $buffer = New-Object byte[] 65536\r\n" +
            "      while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) { $ctx.Response.OutputStream.Write($buffer, 0, $read) }\r\n" +
            "    } finally {\r\n" +
            "      $stream.Dispose()\r\n" +
            "      $ctx.Response.Close()\r\n" +
            "    }\r\n" +
            "  }\r\n" +
            "} finally { $listener.Stop(); $listener.Close() }\r\n");
        File.WriteAllText(Path.Combine(OutputDir, "start_local_server.bat"),
            "@echo off\r\n" +
            "cd /d \"%~dp0\"\r\n" +
            "set \"URL=http://127.0.0.1:" + ServePort + "/" + DeployPrefix + "/\"\r\n" +
            "where powershell >nul 2>nul\r\n" +
            "if %errorlevel%==0 (\r\n" +
            "  powershell -NoProfile -ExecutionPolicy Bypass -File \"%~dp0start.ps1\"\r\n" +
            "  goto :end\r\n" +
            ")\r\n" +
            "where py >nul 2>nul\r\n" +
            "if %errorlevel%==0 (\r\n" +
            "  start \"\" \"%URL%\"\r\n" +
            "  py -3 lychee_server.py --port " + ServePort + " --host 0.0.0.0\r\n" +
            "  goto :end\r\n" +
            ")\r\n" +
            "where python >nul 2>nul\r\n" +
            "if %errorlevel%==0 (\r\n" +
            "  start \"\" \"%URL%\"\r\n" +
            "  python lychee_server.py --port " + ServePort + " --host 0.0.0.0\r\n" +
            "  goto :end\r\n" +
            ")\r\n" +
            "echo Could not find PowerShell, py, or python. Please run this folder through any local HTTP server.\r\n" +
            ":end\r\n" +
            "pause\r\n");
        File.WriteAllText(Path.Combine(OutputDir, "start.bat"),
            "@echo off\r\ncd /d \"%~dp0\"\r\npowershell -NoProfile -ExecutionPolicy Bypass -File \"%~dp0start.ps1\"\r\npause\r\n");
        File.WriteAllText(Path.Combine(OutputDir, "stop.bat"),
            "@echo off\r\ncd /d \"%~dp0\"\r\npowershell -NoProfile -ExecutionPolicy Bypass -File \"%~dp0stop.ps1\"\r\npause\r\n");
        File.WriteAllText(Path.Combine(OutputDir, "start.ps1"),
            "$ErrorActionPreference = 'Stop'\r\n" +
            "$root = Split-Path -Parent $MyInvocation.MyCommand.Path\r\n" +
            "$pidPath = Join-Path $root '.lychee_web_server.pid'\r\n" +
            "$url = 'http://127.0.0.1:" + ServePort + "/" + DeployPrefix + "/'\r\n" +
            "if (Test-Path $pidPath) {\r\n" +
            "  $oldPid = [int](Get-Content $pidPath -ErrorAction SilentlyContinue)\r\n" +
            "  if ($oldPid -gt 0 -and (Get-Process -Id $oldPid -ErrorAction SilentlyContinue)) {\r\n" +
            "    Write-Host \"Server already running on port " + ServePort + " (PID $oldPid).\"\r\n" +
            "    Start-Process $url\r\n" +
            "    exit 0\r\n" +
            "  }\r\n" +
            "}\r\n" +
            "$server = Join-Path $root 'start_local_server.ps1'\r\n" +
            "$p = Start-Process powershell -WindowStyle Minimized -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$server)\r\n" +
            "Set-Content -Path $pidPath -Value $p.Id\r\n" +
            "Write-Host \"Started Lychee WebGL server on http://127.0.0.1:" + ServePort + "/" + DeployPrefix + "/ (PID $($p.Id)).\"\r\n" +
            "Start-Process $url\r\n");
        File.WriteAllText(Path.Combine(OutputDir, "stop.ps1"),
            "$root = Split-Path -Parent $MyInvocation.MyCommand.Path\r\n" +
            "$pidPath = Join-Path $root '.lychee_web_server.pid'\r\n" +
            "if (-not (Test-Path $pidPath)) { Write-Host 'No Lychee WebGL server pid file found.'; exit 0 }\r\n" +
            "$pidValue = Get-Content $pidPath -ErrorAction SilentlyContinue\r\n" +
            "$serverPid = 0\r\n" +
            "[int]::TryParse($pidValue, [ref]$serverPid) | Out-Null\r\n" +
            "if ($serverPid -gt 0) { Stop-Process -Id $serverPid -ErrorAction SilentlyContinue }\r\n" +
            "Remove-Item $pidPath -ErrorAction SilentlyContinue\r\n" +
            "Write-Host 'Stopped Lychee WebGL server.'\r\n");
        File.WriteAllText(Path.Combine(OutputDir, "lychee_server.py"),
            "#!/usr/bin/env python3\n" +
            "import argparse, os, posixpath, signal, sys, urllib.parse\n" +
            "from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer\n\n" +
            "ROOT = os.path.dirname(os.path.abspath(__file__))\n\n" +
            "try:\n" +
            "    signal.signal(signal.SIGPIPE, signal.SIG_IGN)\n" +
            "except (AttributeError, ValueError):\n" +
            "    pass\n\n" +
            "class LycheeHandler(SimpleHTTPRequestHandler):\n" +
            "    def guess_type(self, path):\n" +
            "        request_path = urllib.parse.urlparse(path).path\n" +
            "        if request_path.endswith('.wasm.unityweb'):\n" +
            "            return 'application/wasm'\n" +
            "        if request_path.endswith('.framework.js.unityweb'):\n" +
            "            return 'application/javascript'\n" +
            "        if request_path.endswith('.data.unityweb'):\n" +
            "            return 'application/octet-stream'\n" +
            "        return super().guess_type(path)\n\n" +
            "    def end_headers(self):\n" +
            "        request_path = urllib.parse.urlparse(self.path).path\n" +
            "        if request_path.endswith('.unityweb'):\n" +
            "            self.send_header('Content-Encoding', 'gzip')\n" +
            "        self.send_header('Cache-Control', 'no-store')\n" +
            "        super().end_headers()\n\n" +
            "    def copyfile(self, source, outputfile):\n" +
            "        try:\n" +
            "            super().copyfile(source, outputfile)\n" +
            "        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):\n" +
            "            pass\n\n" +
            "    def translate_path(self, path):\n" +
            "        request_path = urllib.parse.urlparse(path).path\n" +
            "        request_path = urllib.parse.unquote(request_path)\n" +
            "        prefix = '/" + DeployPrefix + "'\n" +
            "        if request_path == prefix or request_path == prefix + '/':\n" +
            "            return os.path.join(ROOT, 'index.html')\n" +
            "        if request_path.startswith(prefix + '/'):\n" +
            "            request_path = request_path[len(prefix):]\n" +
            "        if request_path in ('', '/'):\n" +
            "            return os.path.join(ROOT, 'index.html')\n" +
            "        if request_path.startswith('/data/'):\n" +
            "            return os.path.abspath(request_path)\n" +
            "        clean = posixpath.normpath(request_path.lstrip('/'))\n" +
            "        parts = [p for p in clean.split('/') if p and p not in (os.curdir, os.pardir)]\n" +
            "        return os.path.join(ROOT, *parts)\n\n" +
            "class LycheeHTTPServer(ThreadingHTTPServer):\n" +
            "    daemon_threads = True\n" +
            "    allow_reuse_address = True\n\n" +
            "    def handle_error(self, request, client_address):\n" +
            "        exc = sys.exc_info()[1]\n" +
            "        if isinstance(exc, (BrokenPipeError, ConnectionResetError, ConnectionAbortedError)):\n" +
            "            return\n" +
            "        super().handle_error(request, client_address)\n\n" +
            "def daemonize(pid_file, log_file):\n" +
            "    if not hasattr(os, 'fork'):\n" +
            "        raise RuntimeError('--daemon requires a POSIX platform')\n" +
            "    first_pid = os.fork()\n" +
            "    if first_pid > 0:\n" +
            "        os._exit(0)\n" +
            "    os.setsid()\n" +
            "    second_pid = os.fork()\n" +
            "    if second_pid > 0:\n" +
            "        os._exit(0)\n" +
            "    os.chdir(ROOT)\n" +
            "    os.umask(0o022)\n" +
            "    log_path = os.path.abspath(log_file or os.path.join(ROOT, '.lychee_web_server.log'))\n" +
            "    pid_path = os.path.abspath(pid_file or os.path.join(ROOT, '.lychee_web_server.pid'))\n" +
            "    log_fd = os.open(log_path, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o644)\n" +
            "    null_fd = os.open(os.devnull, os.O_RDONLY)\n" +
            "    os.dup2(null_fd, 0)\n" +
            "    os.dup2(log_fd, 1)\n" +
            "    os.dup2(log_fd, 2)\n" +
            "    try:\n" +
            "        os.close(null_fd)\n" +
            "        os.close(log_fd)\n" +
            "    except OSError:\n" +
            "        pass\n" +
            "    with open(pid_path, 'w') as handle:\n" +
            "        handle.write(str(os.getpid()))\n" +
            "        handle.write('\\n')\n\n" +
            "def main():\n" +
            "    parser = argparse.ArgumentParser()\n" +
            "    parser.add_argument('--host', default='0.0.0.0')\n" +
            "    parser.add_argument('--port', type=int, default=" + ServePort + ")\n" +
            "    parser.add_argument('--daemon', action='store_true')\n" +
            "    parser.add_argument('--pid-file')\n" +
            "    parser.add_argument('--log-file')\n" +
            "    args = parser.parse_args()\n" +
            "    if args.daemon:\n" +
            "        daemonize(args.pid_file, args.log_file)\n" +
            "    server = LycheeHTTPServer((args.host, args.port), LycheeHandler)\n" +
            "    print(f'Serving {ROOT} at http://{args.host}:{args.port}/')\n" +
            "    server.serve_forever()\n\n" +
            "if __name__ == '__main__':\n" +
            "    main()\n");
        File.WriteAllText(Path.Combine(OutputDir, "start.command"),
            "#!/bin/bash\n" +
            "cd \"$(dirname \"$0\")\"\n" +
            "PORT=\"${PORT:-" + ServePort + "}\"\n" +
            "HOST=\"${HOST:-0.0.0.0}\"\n" +
            "PID_FILE=\".lychee_web_server.pid\"\n" +
            "LOG_FILE=\".lychee_web_server.log\"\n" +
            "if [ -f \"$PID_FILE\" ] && kill -0 \"$(cat \"$PID_FILE\")\" 2>/dev/null; then\n" +
            "  echo \"Server already running on port $PORT (PID $(cat \"$PID_FILE\")).\"\n" +
            "else\n" +
            "  rm -f \"$PID_FILE\"\n" +
            "  nohup env PYTHONUNBUFFERED=1 python3 lychee_server.py --port \"$PORT\" --host \"$HOST\" > \"$LOG_FILE\" 2>&1 &\n" +
            "  echo $! > \"$PID_FILE\"\n" +
            "  sleep 1\n" +
            "  if ! kill -0 \"$(cat \"$PID_FILE\")\" 2>/dev/null; then\n" +
            "    echo \"Failed to start Lychee WebGL server. Log:\"\n" +
            "    cat \"$LOG_FILE\" 2>/dev/null || true\n" +
            "    rm -f \"$PID_FILE\"\n" +
            "    exit 1\n" +
            "  fi\n" +
            "  echo \"Started Lychee WebGL server on http://127.0.0.1:$PORT/" + DeployPrefix + "/ (PID $(cat \"$PID_FILE\")).\"\n" +
            "fi\n" +
            "echo \"External replay example: http://127.0.0.1:$PORT/" + DeployPrefix + "/?path=/data/results/game/replay/serve/replay.txt\"\n" +
            "echo \"4K/UHD example: http://127.0.0.1:$PORT/" + DeployPrefix + "/?quality=4k\"\n" +
            "echo \"External replay + 4K: http://127.0.0.1:$PORT/" + DeployPrefix + "/?path=/data/results/game/replay/serve/replay.txt&quality=4k\"\n" +
            "open \"http://127.0.0.1:$PORT/" + DeployPrefix + "/\"\n");
        File.WriteAllText(Path.Combine(OutputDir, "stop.command"),
            "#!/bin/bash\n" +
            "cd \"$(dirname \"$0\")\"\n" +
            "PID_FILE=\".lychee_web_server.pid\"\n" +
            "if [ -f \"$PID_FILE\" ]; then\n" +
            "  kill \"$(cat \"$PID_FILE\")\" 2>/dev/null || true\n" +
            "  rm -f \"$PID_FILE\"\n" +
            "  echo \"Stopped Lychee WebGL server.\"\n" +
            "else\n" +
            "  echo \"No Lychee WebGL server pid file found.\"\n" +
            "fi\n");
        File.WriteAllText(Path.Combine(OutputDir, "start.sh"),
            "#!/usr/bin/env bash\n" +
            "set -euo pipefail\n" +
            "cd \"$(dirname \"$0\")\"\n\n" +
            "PORT=\"${PORT:-" + ServePort + "}\"\n" +
            "HOST=\"${HOST:-0.0.0.0}\"\n" +
            "NO_OPEN=\"${NO_OPEN:-0}\"\n" +
            "PID_FILE=\".lychee_web_server.pid\"\n" +
            "LOG_FILE=\".lychee_web_server.log\"\n" +
            "URL=\"http://127.0.0.1:${PORT}\"\n\n" +
            "if [[ -f \"$PID_FILE\" ]] && kill -0 \"$(cat \"$PID_FILE\")\" 2>/dev/null; then\n" +
            "  echo \"Server already running on port ${PORT} (PID $(cat \"$PID_FILE\")).\"\n" +
            "else\n" +
            "  rm -f \"$PID_FILE\"\n" +
            "  : > \"$LOG_FILE\"\n" +
            "  python3 lychee_server.py --daemon --pid-file \"$PID_FILE\" --log-file \"$LOG_FILE\" --port \"$PORT\" --host \"$HOST\"\n" +
            "  for _ in $(seq 1 30); do\n" +
            "    if [[ -f \"$PID_FILE\" ]] && kill -0 \"$(cat \"$PID_FILE\")\" 2>/dev/null; then\n" +
            "      break\n" +
            "    fi\n" +
            "    sleep 0.2\n" +
            "  done\n" +
            "  if [[ ! -f \"$PID_FILE\" ]] || ! kill -0 \"$(cat \"$PID_FILE\")\" 2>/dev/null; then\n" +
            "    echo \"Failed to start Lychee WebGL server. Log:\"\n" +
            "    cat \"$LOG_FILE\" 2>/dev/null || true\n" +
            "    rm -f \"$PID_FILE\"\n" +
            "    exit 1\n" +
            "  fi\n" +
            "  if ! curl -fsS \"${URL}\" >/dev/null 2>&1; then\n" +
            "    echo \"Lychee WebGL server process started, but HTTP health check failed. Log:\"\n" +
            "    cat \"$LOG_FILE\" 2>/dev/null || true\n" +
            "    kill \"$(cat \"$PID_FILE\")\" 2>/dev/null || true\n" +
            "    rm -f \"$PID_FILE\"\n" +
            "    exit 1\n" +
            "  fi\n" +
            "  echo \"Started Lychee WebGL server on ${URL} (PID $(cat \"$PID_FILE\")).\"\n" +
            "fi\n\n" +
            "echo \"Default: ${URL}\"\n" +
            "echo \"Map switch: ${URL}?map=map_1\"\n" +
            "echo \"Selector: ${URL}?map=select\"\n" +
            "echo \"External replay: ${URL}?path=/data/results/game/replay/serve/replay.txt\"\n" +
            "echo \"Map + external replay: ${URL}?map=map_1&path=/data/results/game/replay/serve/replay.txt\"\n" +
            "echo \"Performance mode: ${URL}?quality=performance\"\n" +
            "echo \"4K/UHD: ${URL}?quality=4k\"\n\n" +
            "if [[ \"$NO_OPEN\" == \"1\" ]]; then\n" +
            "  exit 0\n" +
            "elif command -v xdg-open >/dev/null 2>&1; then\n" +
            "  xdg-open \"$URL\" >/dev/null 2>&1 || true\n" +
            "elif command -v open >/dev/null 2>&1; then\n" +
            "  open \"$URL\" >/dev/null 2>&1 || true\n" +
            "fi\n");
        File.WriteAllText(Path.Combine(OutputDir, "stop.sh"),
            "#!/usr/bin/env bash\n" +
            "set -euo pipefail\n" +
            "cd \"$(dirname \"$0\")\"\n\n" +
            "PID_FILE=\".lychee_web_server.pid\"\n\n" +
            "if [[ -f \"$PID_FILE\" ]]; then\n" +
            "  PID=\"$(cat \"$PID_FILE\")\"\n" +
            "  kill \"$PID\" 2>/dev/null || true\n" +
            "  for _ in $(seq 1 20); do\n" +
            "    if ! kill -0 \"$PID\" 2>/dev/null; then\n" +
            "      break\n" +
            "    fi\n" +
            "    sleep 0.1\n" +
            "  done\n" +
            "  kill -9 \"$PID\" 2>/dev/null || true\n" +
            "  rm -f \"$PID_FILE\"\n" +
            "  echo \"Stopped Lychee WebGL server.\"\n" +
            "else\n" +
            "  echo \"No Lychee WebGL server pid file found.\"\n" +
            "fi\n");
        TryMakeExecutable(
            "lychee_server.py",
            "start.command",
            "stop.command",
            "start.sh",
            "stop.sh",
            "start_local_server.command",
            "启动本地服务.command");
        Append("Package README and local server scripts written.");
    }

    static void CleanPackageJunk()
    {
        if (!Directory.Exists(OutputDir)) return;
        int removed = 0;
        foreach (string file in Directory.GetFiles(OutputDir, ".DS_Store", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception exception)
            {
                Append("WARNING: failed to remove " + file + ": " + exception.Message);
            }
        }
        foreach (string dir in Directory.GetDirectories(OutputDir, "__MACOSX", SearchOption.AllDirectories))
        {
            try
            {
                Directory.Delete(dir, true);
                removed++;
            }
            catch (Exception exception)
            {
                Append("WARNING: failed to remove " + dir + ": " + exception.Message);
            }
        }
        string legacyReply = Path.Combine(OutputDir, "StreamingAssets/reply.txt");
        if (File.Exists(legacyReply))
        {
            try
            {
                File.Delete(legacyReply);
                removed++;
            }
            catch (Exception exception)
            {
                Append("WARNING: failed to remove legacy " + legacyReply + ": " + exception.Message);
            }
        }
        foreach (string name in new[] { "Build 2", "Build 3", "StreamingAssets 2", "StreamingAssets 3", "TemplateData 2", "TemplateData 3" })
        {
            string dir = Path.Combine(OutputDir, name);
            if (!Directory.Exists(dir)) continue;
            try
            {
                Directory.Delete(dir, true);
                removed++;
            }
            catch (Exception exception)
            {
                Append("WARNING: failed to remove " + dir + ": " + exception.Message);
            }
        }
        foreach (string runtimeFile in new[] { ".lychee_web_server.pid", ".lychee_web_server.log" })
        {
            string file = Path.Combine(OutputDir, runtimeFile);
            if (!File.Exists(file)) continue;
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception exception)
            {
                Append("WARNING: failed to remove " + file + ": " + exception.Message);
            }
        }
        Append("Package junk cleanup removed " + removed + " entries.");
    }

    static void TryMakeExecutable(params string[] relativePaths)
    {
        if (relativePaths == null || relativePaths.Length == 0) return;
        if (!File.Exists("/bin/chmod")) return;
        foreach (string relativePath in relativePaths)
        {
            string path = Path.Combine(OutputDir, relativePath);
            if (!File.Exists(path)) continue;
            try
            {
                using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/bin/chmod",
                    Arguments = "+x \"" + path.Replace("\"", "\\\"") + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }))
                {
                    process?.WaitForExit(2000);
                }
            }
            catch (Exception exception)
            {
                Append("WARNING: chmod failed for " + path + ": " + exception.Message);
            }
        }
    }

    static void ApplyWebGLMemorySettings()
    {
        PlayerSettings.WebGL.maximumMemorySize = WebGLMaximumMemoryMB;
        SetWebGLIntProperty("initialMemorySize", WebGLInitialMemoryMB);
        SetWebGLIntProperty("memorySize", WebGLInitialMemoryMB);
        SetWebGLIntProperty("memoryLinearGrowthStep", 64);
        SetWebGLIntProperty("memoryGeometricGrowthCap", 256);
        Append("WebGL memory settings: initial=" + WebGLInitialMemoryMB + "MB, maximum=" + WebGLMaximumMemoryMB + "MB.");
    }

    static void SetWebGLIntProperty(string propertyName, int value)
    {
        try
        {
            var property = typeof(PlayerSettings.WebGL).GetProperty(propertyName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (property == null || !property.CanWrite) return;
            property.SetValue(null, value, null);
        }
        catch (Exception exception)
        {
            Append("WARNING: failed to set WebGL." + propertyName + ": " + exception.Message);
        }
    }

    static void Append(string message)
    {
        File.AppendAllText(ReportPath, DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine);
        Debug.Log("[LycheeWebGLBuild] " + message);
    }
}
