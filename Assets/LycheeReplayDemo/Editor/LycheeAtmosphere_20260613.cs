using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
/// 一次性氛围升级（20260613）：天空盒 + 光照 + 后期。用户原话——灰色虚空是最廉价也最致命的丢分项。
/// 思路：把场景从"默认天光"切到黄昏长安——程序化黄昏天空盒、低角度暖金平行光、三色环境光、
/// 线性暖雾把远景溶进暮色；后期补 ACES/暖色滤镜/暗角。天空盒存为 .mat 资产再赋给
/// RenderSettings.skybox，保证 Skybox/Procedural shader 被打包收集（纯内存材质会被裁掉）。
/// 已知坑：LycheePostFX_20260611K.asset 的 4 个 components 全是 fileID:0 断引用
/// （旧脚本 Add 后没 AddObjectToAsset，子资产从未真正落盘），这里清掉空槽后按规格重建并正确嵌入。
/// 模式与 LycheeStationLift_20260613 一致：update 轮询 + done 标记 + 报告 + 磁盘重载。
/// </summary>
[InitializeOnLoad]
static class LycheeAtmosphere_20260613
{
    const string ScenePath = "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity";
    const string SkyboxPath = "Assets/LycheeMap15/SkyboxDusk_20260613.mat";
    const string ProfilePath = "Assets/LycheeMap15/LycheePostFX_20260611K.asset";
    const string RendererPath = "Assets/Settings/PC_Renderer.asset";
    const string DoneFileName = ".lychee_atmosphere_20260613.done";
    const string ReportFileName = "LycheeAtmosphere_20260613_report.txt";

    static double nextPollTime;

    static LycheeAtmosphere_20260613()
    {
        EditorApplication.update += Poll;
    }

    static string ProjectRoot
    {
        get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..")); }
    }

    static string DonePath { get { return Path.Combine(ProjectRoot, DoneFileName); } }
    static string ReportPath { get { return Path.Combine(ProjectRoot, ReportFileName); } }

    static void Poll()
    {
        if (EditorApplication.timeSinceStartup < nextPollTime) return;
        nextPollTime = EditorApplication.timeSinceStartup + 2.0;
        try
        {
            if (File.Exists(DonePath)) return;
        }
        catch (Exception)
        {
            return;
        }
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying ||
            EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            return;
        }
        Run();
    }

    static void Run()
    {
        var report = new List<string>();
        report.Add("LycheeAtmosphere_20260613 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        try
        {
            Scene active = SceneManager.GetActiveScene();
            if (!string.Equals(active.path, ScenePath, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(active.path) && active.isDirty)
            {
                EditorSceneManager.SaveOpenScenes();
            }
            active = EditorSceneManager.OpenScene(ScenePath);
            report.Add("已从磁盘重载场景: " + ScenePath);

            // ---- 1. 黄昏长安天空盒（存资产再赋值，保证打包带上 shader）----
            ApplyDuskSkybox(report);

            // ---- 2. 平行光：低角度暖金黄昏阳光 ----
            ApplyDuskSun(report);

            // ---- 3. 环境光三色 + 线性暖雾（远景溶入暮色而不是灰虚空）----
            ApplyAmbientAndFog(report);

            // ---- 4. 后期 Volume Profile：修复断引用并按规格补全 ----
            ApplyPostProcessing(report);

            // ---- 5. SSAO：PC_Renderer 缺则反射补加（跨版本易碎，失败只记报告）----
            EnsureSsao(report);

            // ---- 6. 阴影距离：编辑器观感优先（运行时 WebGL 裁剪仍会压回 60）----
            float oldShadowDistance = QualitySettings.shadowDistance;
            QualitySettings.shadowDistance = 70f;
            report.Add(string.Format("阴影距离: {0:F0} → 70", oldShadowDistance));

            AssetDatabase.SaveAssets();
            // RenderSettings 改动不一定自动置脏，显式标脏保证落盘
            EditorSceneManager.MarkSceneDirty(active);
            bool saved = EditorSceneManager.SaveScene(active);
            report.Add("场景保存: " + saved);
            WriteReport(report);
            File.WriteAllText(DonePath, saved ? "Succeeded" : "SaveFailed");
            Debug.Log("[LycheeAtmosphere_20260613] 完成，报告: " + ReportPath);
        }
        catch (Exception exception)
        {
            report.Add("EXCEPTION: " + exception);
            WriteReport(report);
            TryWriteDone("Exception");
            Debug.LogError("[LycheeAtmosphere_20260613] 执行异常: " + exception.Message);
        }
    }

    // ---------------- 1. 天空盒 ----------------

    static void ApplyDuskSkybox(List<string> report)
    {
        Shader proceduralSky = Shader.Find("Skybox/Procedural");
        if (proceduralSky == null)
        {
            // 极端情况（shader 被裁/丢失）不让整个脚本失败，灰天空总好过空引用报错
            report.Add("天空盒: 找不到 Skybox/Procedural shader，跳过（保留原天空盒）");
            return;
        }
        string oldSkybox = RenderSettings.skybox != null ? RenderSettings.skybox.name : "(无)";
        Material sky = AssetDatabase.LoadAssetAtPath<Material>(SkyboxPath);
        if (sky == null)
        {
            sky = new Material(proceduralSky);
            AssetDatabase.CreateAsset(sky, SkyboxPath);
        }
        else
        {
            sky.shader = proceduralSky; // rerun 时复用旧资产但确保 shader 正确
        }
        sky.SetFloat("_SunSize", 0.06f);                                  // 黄昏太阳盘面偏大
        sky.SetFloat("_AtmosphereThickness", 1.25f);                      // 厚大气=更浓的暖色散射
        sky.SetColor("_SkyTint", new Color(0.784f, 0.510f, 0.353f));      // #C8825A 暖橙
        sky.SetColor("_GroundColor", new Color(0.310f, 0.227f, 0.157f));  // 暖褐地平线
        sky.SetFloat("_Exposure", 1.25f);
        EditorUtility.SetDirty(sky);
        RenderSettings.skybox = sky;
        report.Add(string.Format("天空盒: {0} → {1}（Skybox/Procedural 黄昏参数）", oldSkybox, sky.name));
    }

    // ---------------- 2. 平行光 ----------------

    static void ApplyDuskSun(List<string> report)
    {
        // 优先沿用 RenderSettings.sun，否则取最亮的激活方向光——场景可能有补光，别抓错
        Light[] lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
        Light sun = RenderSettings.sun != null && RenderSettings.sun.type == LightType.Directional &&
                    RenderSettings.sun.gameObject.activeInHierarchy
            ? RenderSettings.sun
            : lights.Where(l => l.type == LightType.Directional && l.gameObject.activeInHierarchy)
                    .OrderByDescending(l => l.intensity)
                    .FirstOrDefault();
        if (sun == null)
        {
            report.Add("平行光: 场景里没有激活的方向光，跳过");
            return;
        }
        Vector3 oldEuler = sun.transform.rotation.eulerAngles;
        string oldDesc = string.Format("{0} 色={1} 强度={2:F2} 角度=({3:F0},{4:F0})",
            sun.name, ColorUtility.ToHtmlStringRGB(sun.color), sun.intensity, oldEuler.x, oldEuler.y);
        Undo.RecordObject(sun, "Dusk sun light");
        Undo.RecordObject(sun.transform, "Dusk sun rotation");
        sun.color = new Color(1f, 0.851f, 0.659f); // #FFD9A8 暖金
        sun.intensity = 1.2f;
        sun.shadows = LightShadows.Soft;
        // 俯仰 38° 的低角度；偏航 56°=光线射向东北，即太阳挂在西南偏西低空，拉出长投影
        sun.transform.rotation = Quaternion.Euler(38f, 56f, 0f);
        RenderSettings.sun = sun;
        EditorUtility.SetDirty(sun);
        EditorUtility.SetDirty(sun.transform);
        report.Add(string.Format("平行光: {0} → 色=FFD9A8 强度=1.20 角度=(38,56) 软阴影，RenderSettings.sun 已指向", oldDesc));
    }

    // ---------------- 3. 环境光 + 雾 ----------------

    static void ApplyAmbientAndFog(List<string> report)
    {
        string oldAmbient = string.Format("mode={0} sky={1} equator={2} ground={3}",
            RenderSettings.ambientMode,
            ColorUtility.ToHtmlStringRGB(RenderSettings.ambientSkyColor),
            ColorUtility.ToHtmlStringRGB(RenderSettings.ambientEquatorColor),
            ColorUtility.ToHtmlStringRGB(RenderSettings.ambientGroundColor));
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.773f, 0.557f, 0.384f);     // 暖橙：天空反弹
        RenderSettings.ambientEquatorColor = new Color(0.541f, 0.478f, 0.416f); // 中灰暖：地平线
        RenderSettings.ambientGroundColor = new Color(0.231f, 0.180f, 0.133f);  // 暗褐：地面反弹
        report.Add("环境光: " + oldAmbient + " → Trilight 暖橙/中灰暖/暗褐");

        string oldFog = RenderSettings.fog
            ? string.Format("mode={0} 色={1}", RenderSettings.fogMode,
                ColorUtility.ToHtmlStringRGB(RenderSettings.fogColor))
            : "关闭";
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear; // 线性雾可控远近边界，远景定向溶入暮色
        RenderSettings.fogStartDistance = 45f;
        RenderSettings.fogEndDistance = 140f;
        RenderSettings.fogColor = new Color(0.788f, 0.663f, 0.545f); // #C9A98B 暖灰
        report.Add("雾: " + oldFog + " → Linear 45~140 色=C9A98B");
    }

    // ---------------- 4. 后期 Profile ----------------

    static void ApplyPostProcessing(List<string> report)
    {
        VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
        if (profile == null)
        {
            report.Add("后期: 找不到 " + ProfilePath + "，跳过");
            return;
        }
        // 历史遗留：components 里可能全是 null 断引用（子资产没落盘），先清掉避免 TryGet 空引用
        int brokenSlots = profile.components.RemoveAll(component => component == null);
        if (brokenSlots > 0)
        {
            report.Add(string.Format("后期: 清理 {0} 个断引用组件槽（旧脚本子资产未落盘）", brokenSlots));
        }

        Tonemapping tonemapping = EnsureOverride<Tonemapping>(profile, report, out bool _);
        tonemapping.mode.Override(TonemappingMode.ACES);

        ColorAdjustments color = EnsureOverride<ColorAdjustments>(profile, report, out bool colorNew);
        if (colorNew)
        {
            // 新建时按生成器原值兜底曝光/对比，避免只有饱和度导致画面发闷
            color.postExposure.Override(0.14f);
            color.contrast.Override(13f);
        }
        color.saturation.Override(10f);
        color.colorFilter.Override(new Color(1f, 0.96f, 0.89f)); // 微暖滤镜统一唐风暖调

        Vignette vignette = EnsureOverride<Vignette>(profile, report, out bool vignetteNew);
        vignette.intensity.Override(0.22f);
        if (vignetteNew) vignette.smoothness.Override(0.62f);

        // Bloom：已有则原样保留；断引用清理后缺失则按生成器原参数重建
        if (!profile.TryGet(out Bloom bloom))
        {
            bloom = EnsureOverride<Bloom>(profile, report, out bool _);
            bloom.threshold.Override(0.82f);
            bloom.intensity.Override(0.30f);
            bloom.scatter.Override(0.58f);
            report.Add("后期: Bloom 原引用已断，按生成器参数重建（threshold 0.82 / intensity 0.30 / scatter 0.58）");
        }
        else
        {
            report.Add("后期: Bloom 已存在，保留原参数");
        }

        EditorUtility.SetDirty(profile);
        report.Add("后期: Tonemapping=ACES, ColorAdjustments(saturation +10, 微暖滤镜), Vignette(0.22) 已写入 " + ProfilePath);
    }

    /// <summary>取已有组件，缺则新建并嵌入 profile 资产——必须 AddObjectToAsset 才会真正落盘（旧 K 资产就是栽在这）。</summary>
    static T EnsureOverride<T>(VolumeProfile profile, List<string> report, out bool created)
        where T : VolumeComponent
    {
        if (profile.TryGet(out T existing))
        {
            created = false;
            return existing;
        }
        T component = profile.Add<T>();
        AssetDatabase.AddObjectToAsset(component, profile);
        EditorUtility.SetDirty(component);
        report.Add("后期: 新增 " + typeof(T).Name + " 子资产");
        created = true;
        return component;
    }

    // ---------------- 5. SSAO ----------------

    static void EnsureSsao(List<string> report)
    {
        try
        {
            var rendererData = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(RendererPath);
            if (rendererData == null)
            {
                report.Add("SSAO: 找不到 " + RendererPath + "，需手动添加");
                return;
            }
            bool exists = rendererData.rendererFeatures
                .Any(feature => feature != null && feature.GetType().Name == "ScreenSpaceAmbientOcclusion");
            if (exists)
            {
                report.Add("SSAO: PC_Renderer 已含 ScreenSpaceAmbientOcclusion，无需改动");
                return;
            }
            // SSAO 类是 internal，只能按类型名反射拿；URP 跨版本字段易变，任何一步失败都放弃不连坐
            Type ssaoType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(assembly =>
                {
                    try { return assembly.GetTypes(); }
                    catch (Exception) { return Type.EmptyTypes; }
                })
                .FirstOrDefault(type => type.Name == "ScreenSpaceAmbientOcclusion" &&
                                        typeof(ScriptableRendererFeature).IsAssignableFrom(type));
            if (ssaoType == null)
            {
                report.Add("SSAO: 反射找不到 ScreenSpaceAmbientOcclusion 类型，需手动添加");
                return;
            }
            var feature = (ScriptableRendererFeature)ScriptableObject.CreateInstance(ssaoType);
            feature.name = "ScreenSpaceAmbientOcclusion";
            AssetDatabase.AddObjectToAsset(feature, rendererData);
            AssetDatabase.SaveAssets(); // 子资产落盘后 localId 才稳定，否则 map 里会写进 0

            // 经 SerializedObject 同步 m_RendererFeatures + m_RendererFeatureMap（缺 map 项会触发 URP 自检剔除）
            var serialized = new SerializedObject(rendererData);
            SerializedProperty featuresProp = serialized.FindProperty("m_RendererFeatures");
            featuresProp.arraySize++;
            featuresProp.GetArrayElementAtIndex(featuresProp.arraySize - 1).objectReferenceValue = feature;
            SerializedProperty mapProp = serialized.FindProperty("m_RendererFeatureMap");
            if (mapProp != null &&
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out string _, out long localId))
            {
                mapProp.arraySize++;
                mapProp.GetArrayElementAtIndex(mapProp.arraySize - 1).longValue = localId;
            }
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(rendererData);
            report.Add("SSAO: 已为 PC_Renderer 反射添加 ScreenSpaceAmbientOcclusion");
        }
        catch (Exception exception)
        {
            report.Add("SSAO: 自动添加失败（" + exception.Message + "），需手动添加");
        }
    }

    // ---------------- 收尾工具 ----------------

    static void WriteReport(List<string> lines)
    {
        try { File.WriteAllText(ReportPath, string.Join(Environment.NewLine, lines) + Environment.NewLine); }
        catch (Exception e) { Debug.LogWarning("[LycheeAtmosphere_20260613] 写报告失败: " + e.Message); }
    }

    static void TryWriteDone(string content)
    {
        try { File.WriteAllText(DonePath, content); }
        catch (Exception e) { Debug.LogWarning("[LycheeAtmosphere_20260613] 写 done 失败: " + e.Message); }
    }

    [MenuItem("Tools/Lychee/Replay Upgrade 20260612/Atmosphere 20260613 (rerun)")]
    static void RerunNow()
    {
        try { if (File.Exists(DonePath)) File.Delete(DonePath); }
        catch (Exception e) { Debug.LogWarning("[LycheeAtmosphere_20260613] 删 done 失败: " + e.Message); }
        nextPollTime = 0;
    }
}
