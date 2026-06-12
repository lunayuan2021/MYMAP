using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

/// <summary>
/// 一次性 TMP 字体烘焙（20260613）：用户反馈世界标签白字发虚——TextMesh 走的是位图字体，
/// 透视相机拉近放大必然糊。把随包的霞鹜文楷 ttf 烘成 SDF 动态 TMP 字体资产
/// （采样 64pt + padding 6 + 1024x1024 动态图集：中文字形两万多个不可能预烘全集，
/// Dynamic 模式运行时按需填充，包体只多一个空图集），存到 Resources/Fonts/LXGWWenKai-TMP.asset，
/// 供运行时 LycheeLabelUpgrade 把场景 TextMesh 升级为 TextMeshPro（SDF 放大不糊 + 可描边）。
/// 关键坑：TMP 字体资产的材质与图集是内存对象，必须 AddObjectToAsset 写成同文件子资产，
/// 否则重开工程后主资产引用断裂（文字变粉/丢图集）。
/// 模式与 LycheeStationLift_20260613 一致：update 轮询 + done 标记 + 报告 + 菜单 rerun；
/// 本脚本只产出资产，无需开场景。失败写 done(Failed)，不影响其他自动化系统。
/// </summary>
[InitializeOnLoad]
static class LycheeTmpFontBake_20260613
{
    const string SourceFontPath = "Assets/LycheeReplayDemo/Resources/Fonts/LXGWWenKai-Regular.ttf";
    const string TargetAssetPath = "Assets/LycheeReplayDemo/Resources/Fonts/LXGWWenKai-TMP.asset";
    const string DoneFileName = ".lychee_tmpfont_20260613.done";
    const string ReportFileName = "LycheeTmpFontBake_20260613_report.txt";
    const int SamplingPointSize = 64; // SDF 采样点：64 对 3D 标签已足够锐利，再大单个中文字形吃图集太凶
    const int AtlasPadding = 6;       // 描边吃 padding：运行时 0.18 描边宽约需 5~6 像素余量，少了描边被切
    const int AtlasSize = 1024;

    static double nextPollTime;
    static bool essentialsImportRequested; // 每个编译域只触发一次静默导入，防止反复排队

    static LycheeTmpFontBake_20260613()
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
        // 关键前置：TMP 必备资源（TMP Settings / SDF shader / 默认字体）随 ugui 包以 unitypackage
        // 分发，本工程从未导入过；而 CreateFontAsset 内部会读 TMP_Settings.clearDynamicDataOnBuild
        // （无空保护），缺资源必然 NRE，且写下 done(Failed) 后永久挡住重试。这里先静默导入一次，
        // 不写 done 直接返回，等资源就绪后下轮轮询自然继续烘焙。
        if (TMP_Settings.instance == null)
        {
            if (!essentialsImportRequested)
            {
                essentialsImportRequested = true;
                TMP_PackageResourceImporter.ImportResources(true, false, false);
                Debug.Log("[LycheeTmpFontBake_20260613] TMP Essential Resources 缺失，已触发静默导入，就绪后自动重试烘焙。");
            }
            return;
        }

        var report = new List<string>();
        report.Add("LycheeTmpFontBake_20260613 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        try
        {
            Font sourceFont = AssetDatabase.LoadAssetAtPath<Font>(SourceFontPath);
            if (sourceFont == null)
            {
                Abort(report, "源字体加载失败: " + SourceFontPath);
                return;
            }
            report.Add("源字体: " + sourceFont.name);

            // 重跑：先删旧资产整体重建，避免往旧主资产里再叠子资产造成重复引用
            if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(TargetAssetPath) != null)
            {
                AssetDatabase.DeleteAsset(TargetAssetPath);
                report.Add("已删除旧资产: " + TargetAssetPath);
            }

            TMP_FontAsset fontAsset = TMP_FontAsset.CreateFontAsset(
                sourceFont, SamplingPointSize, AtlasPadding, GlyphRenderMode.SDFAA,
                AtlasSize, AtlasSize, AtlasPopulationMode.Dynamic);
            if (fontAsset == null)
            {
                Abort(report, "TMP_FontAsset.CreateFontAsset 返回空（字体文件不可读？）。");
                return;
            }
            fontAsset.name = "LXGWWenKai-TMP";
            AssetDatabase.CreateAsset(fontAsset, TargetAssetPath);

            // 关键坑：图集与材质必须随主资产写成子资产，缺一则重载后引用断裂
            int subAssets = 0;
            Texture2D[] atlases = fontAsset.atlasTextures;
            if (atlases != null && atlases.Length > 0)
            {
                for (int i = 0; i < atlases.Length; i++)
                {
                    if (atlases[i] == null) continue;
                    atlases[i].name = "LXGWWenKai-TMP Atlas " + i;
                    AssetDatabase.AddObjectToAsset(atlases[i], fontAsset);
                    subAssets++;
                }
            }
            else if (fontAsset.atlasTexture != null)
            {
                // 兜底：个别 TMP 版本 Dynamic 模式只有单图集属性
                fontAsset.atlasTexture.name = "LXGWWenKai-TMP Atlas";
                AssetDatabase.AddObjectToAsset(fontAsset.atlasTexture, fontAsset);
                subAssets++;
            }
            if (fontAsset.material != null)
            {
                fontAsset.material.name = "LXGWWenKai-TMP Material";
                AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);
                subAssets++;
            }
            EditorUtility.SetDirty(fontAsset);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(TargetAssetPath);
            report.Add(string.Format("已创建 {0}（子资产 {1} 个：图集+材质）", TargetAssetPath, subAssets));

            // 落盘复核：重新加载确认材质引用完好，避免静默产出坏资产坑运行时
            TMP_FontAsset reloaded = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(TargetAssetPath);
            bool ok = reloaded != null && reloaded.material != null &&
                      reloaded.atlasPopulationMode == AtlasPopulationMode.Dynamic;
            report.Add(string.Format("复核: 主资产={0} 材质={1} 模式={2}",
                reloaded != null,
                reloaded != null && reloaded.material != null,
                reloaded != null ? reloaded.atlasPopulationMode.ToString() : "N/A"));
            WriteReport(report);
            File.WriteAllText(DonePath, ok ? "Succeeded" : "Failed");
            Debug.Log("[LycheeTmpFontBake_20260613] 完成，报告: " + ReportPath);
        }
        catch (Exception exception)
        {
            report.Add("EXCEPTION: " + exception);
            WriteReport(report);
            TryWriteDone("Failed");
            Debug.LogError("[LycheeTmpFontBake_20260613] 执行异常: " + exception.Message);
        }
    }

    static void Abort(List<string> report, string error)
    {
        report.Add("ERROR: " + error);
        WriteReport(report);
        TryWriteDone("Failed");
        Debug.LogError("[LycheeTmpFontBake_20260613] " + error);
    }

    static void WriteReport(List<string> lines)
    {
        try { File.WriteAllText(ReportPath, string.Join(Environment.NewLine, lines) + Environment.NewLine); }
        catch (Exception e) { Debug.LogWarning("[LycheeTmpFontBake_20260613] 写报告失败: " + e.Message); }
    }

    static void TryWriteDone(string content)
    {
        try { File.WriteAllText(DonePath, content); }
        catch (Exception e) { Debug.LogWarning("[LycheeTmpFontBake_20260613] 写 done 失败: " + e.Message); }
    }

    [MenuItem("Tools/Lychee/Replay Upgrade 20260612/TMP Font Bake 20260613 (rerun)")]
    static void RerunNow()
    {
        try { if (File.Exists(DonePath)) File.Delete(DonePath); }
        catch (Exception e) { Debug.LogWarning("[LycheeTmpFontBake_20260613] 删 done 失败: " + e.Message); }
        nextPollTime = 0;
    }
}
