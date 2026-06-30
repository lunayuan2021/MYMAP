using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 一次性事件特效模型接线（20260629）：把用户做的三个头顶信标 3D 模型
/// （ModelsGLB/event_icebox.glb=冰棱簇、event_horse.glb=奔马、event_spoil.glb=蔫荔枝串，
/// 落地后已由 proc.mjs 减面到 ~1500 三角面 + 剥光贴图）赋给 LycheeReplayPlayer 的
/// eventModelIceBox/eventModelHorse/eventModelSpoil 字段——零人工接线。
///
/// 与 LycheeHorseHookup_20260613 的差异：
///   • 一次赋 3 个模型；
///   • 不只改母版，还遍历 GeneratedMaps/*.unity 所有含 LycheeReplayPlayer 的场景一起赋（含已克隆的锦标赛地图），
///     免得旧克隆场景拿不到模型；没 player 的场景（如 LycheeMapSelect）自动跳过。
///   • 三个 GLB 任一缺失就静默等下一轮（文件随时可能落地），不写 done、不报错。
/// 模式：update 轮询 2 秒 + done 标记 + 报告 + 菜单 rerun / 菜单仅传播到所有地图场景。
/// </summary>
[InitializeOnLoad]
static class LycheeEventModelHookup_20260629
{
    const string MasterScenePath = "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity";
    const string GeneratedMapsDir = "Assets/Scenes/GeneratedMaps";
    const string IceBoxGlb = "Assets/LycheeReplayDemo/ModelsGLB/event_icebox.glb";
    const string HorseGlb = "Assets/LycheeReplayDemo/ModelsGLB/event_horse.glb";
    const string SpoilGlb = "Assets/LycheeReplayDemo/ModelsGLB/event_spoil.glb";
    const string OverheadShaderName = "Lychee/Overhead Event Model";
    const string DoneFileName = ".lychee_event_model_hookup_20260629.done";
    const string ReportFileName = "LycheeEventModelHookup_20260629_report.txt";
    const int MaxImportRetries = 5;

    static readonly string[] GlbPaths = { IceBoxGlb, HorseGlb, SpoilGlb };

    static double nextPollTime;
    static int importRetryCount;

    static LycheeEventModelHookup_20260629()
    {
        EditorApplication.update += Poll;
    }

    static string ProjectRoot { get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..")); } }
    static string DonePath { get { return Path.Combine(ProjectRoot, DoneFileName); } }
    static string ReportPath { get { return Path.Combine(ProjectRoot, ReportFileName); } }

    static void Poll()
    {
        if (EditorApplication.timeSinceStartup < nextPollTime) return;
        nextPollTime = EditorApplication.timeSinceStartup + 2.0;
        try
        {
            if (File.Exists(DonePath)) return;
            // 三个 GLB 任一还没落地就静默等下一轮（落地后下次轮询或导入触发的域重载自动装配）。
            foreach (string glb in GlbPaths)
                if (!File.Exists(Path.Combine(ProjectRoot, glb))) return;
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

        if (!TryLoadModels(out GameObject ice, out GameObject horse, out GameObject spoil, out bool needRetry))
        {
            if (!needRetry) return;   // 已写 done 中止
            return;                   // 导入产物还没就绪，下一轮再取
        }
        // 自动只改母版场景（验证就在这跑、最小惊扰，对齐 Horse/General Hookup 先例）；
        // 锦标赛克隆地图用菜单「Propagate To All Generated Map Scenes」一键传播。
        Run(ice, horse, spoil, new List<string> { MasterScenePath }, true);
    }

    /// <summary>取三个 GLB 主资产；磁盘有但取不到（importer 未跑）就强制同步导入重试，超限则写报告 + done 中止。</summary>
    static bool TryLoadModels(out GameObject ice, out GameObject horse, out GameObject spoil, out bool needRetry)
    {
        ice = AssetDatabase.LoadAssetAtPath<GameObject>(IceBoxGlb);
        horse = AssetDatabase.LoadAssetAtPath<GameObject>(HorseGlb);
        spoil = AssetDatabase.LoadAssetAtPath<GameObject>(SpoilGlb);
        needRetry = false;
        if (ice != null && horse != null && spoil != null) return true;

        importRetryCount++;
        if (importRetryCount > MaxImportRetries)
        {
            Abort(NewReport(), "三个事件 GLB 已在磁盘但重导 " + MaxImportRetries +
                " 次仍取不到 GameObject 主资产（疑似损坏或 glTFast 导入失败），中止。修复后用菜单 rerun。");
            return false;
        }
        foreach (string glb in GlbPaths)
            AssetDatabase.ImportAsset(glb, ImportAssetOptions.ForceSynchronousImport);
        ice = AssetDatabase.LoadAssetAtPath<GameObject>(IceBoxGlb);
        horse = AssetDatabase.LoadAssetAtPath<GameObject>(HorseGlb);
        spoil = AssetDatabase.LoadAssetAtPath<GameObject>(SpoilGlb);
        if (ice != null && horse != null && spoil != null) return true;
        needRetry = true;
        return false;
    }

    static List<string> NewReport()
    {
        return new List<string> { "LycheeEventModelHookup_20260629 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
    }

    /// <summary>母版优先 + 所有 GeneratedMaps 场景去重后的处理顺序（母版放最后，处理完停在母版上供验证）。</summary>
    static List<string> CollectScenePaths()
    {
        var paths = new List<string>();
        try
        {
            string dirFull = Path.Combine(ProjectRoot, GeneratedMapsDir);
            if (Directory.Exists(dirFull))
            {
                foreach (string file in Directory.GetFiles(dirFull, "*.unity", SearchOption.TopDirectoryOnly))
                {
                    string rel = GeneratedMapsDir + "/" + Path.GetFileName(file);
                    if (!PathEquals(rel, MasterScenePath)) paths.Add(rel);
                }
            }
        }
        catch (Exception) { /* 目录读不到就只改母版 */ }
        paths.Add(MasterScenePath);   // 母版最后处理 → 结束时停在母版
        return paths;
    }

    static bool PathEquals(string a, string b)
    {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    static void Run(GameObject ice, GameObject horse, GameObject spoil, List<string> scenePaths, bool writeDone)
    {
        List<string> report = NewReport();
        try
        {
            // 0) 自定义 overlay shader 加入 AlwaysIncludedShaders——它只被运行时 Shader.Find 引用、没有任何材质资产引用，
            //    不加的话 WebGL 构建会把它剥离，运行时 Shader.Find 返回 null → 信标退回 URP/Unlit、丢掉 always-on-top。
            EnsureShaderAlwaysIncluded(OverheadShaderName, report);

            // 先保存当前未保存的非目标场景，免得切场景丢改动。
            Scene active = SceneManager.GetActiveScene();
            if (!string.IsNullOrEmpty(active.path) && active.isDirty)
                EditorSceneManager.SaveOpenScenes();

            int assigned = 0, skipped = 0, saved = 0;
            foreach (string scenePath in scenePaths)
            {
                Scene scene;
                try { scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single); }
                catch (Exception e) { report.Add("打开失败跳过 " + scenePath + " : " + e.Message); continue; }

                LycheeReplayPlayer player =
                    UnityEngine.Object.FindAnyObjectByType<LycheeReplayPlayer>(FindObjectsInactive.Include);
                if (player == null)
                {
                    skipped++;
                    report.Add("无 LycheeReplayPlayer，跳过 " + scenePath);
                    continue;
                }

                Undo.RecordObject(player, "Hookup event models");
                player.eventModelIceBox = ice;
                player.eventModelHorse = horse;
                player.eventModelSpoil = spoil;
                EditorUtility.SetDirty(player);
                bool ok = EditorSceneManager.SaveScene(scene);
                assigned++;
                if (ok) saved++;
                report.Add(string.Format("赋值 {0}  (保存={1})", scenePath, ok));
            }

            report.Add(string.Format("事件模型: ice={0} horse={1} spoil={2}", ice.name, horse.name, spoil.name));
            report.Add(string.Format("场景统计: 赋值 {0} / 保存 {1} / 跳过(无player) {2}", assigned, saved, skipped));
            WriteReport(report);
            if (writeDone) File.WriteAllText(DonePath, assigned > 0 ? "Succeeded" : "NoPlayerScene");
            Debug.Log("[LycheeEventModelHookup_20260629] 完成，报告: " + ReportPath);
        }
        catch (Exception exception)
        {
            report.Add("EXCEPTION: " + exception);
            WriteReport(report);
            TryWriteDone("Exception");
            Debug.LogError("[LycheeEventModelHookup_20260629] 执行异常: " + exception.Message);
        }
    }

    /// <summary>把指定 shader 加进 ProjectSettings/GraphicsSettings 的 m_AlwaysIncludedShaders（已在则跳过）。
    /// shader 还没导入完(Shader.Find 失败)时只记警告，不抛——用户可在 shader 编译后用菜单 rerun 补上。</summary>
    static void EnsureShaderAlwaysIncluded(string shaderName, List<string> report)
    {
        try
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                report.Add("⚠ Shader.Find 失败（可能还没导入完）：" + shaderName + "；shader 编译后用菜单 rerun 可补加 AlwaysIncluded。");
                return;
            }
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (assets == null || assets.Length == 0)
            {
                report.Add("⚠ 取不到 GraphicsSettings.asset，无法加入 AlwaysIncluded。");
                return;
            }
            SerializedObject so = new SerializedObject(assets[0]);
            SerializedProperty arr = so.FindProperty("m_AlwaysIncludedShaders");
            if (arr == null)
            {
                report.Add("⚠ GraphicsSettings 里取不到 m_AlwaysIncludedShaders。");
                return;
            }
            for (int i = 0; i < arr.arraySize; i++)
            {
                if (arr.GetArrayElementAtIndex(i).objectReferenceValue == shader)
                {
                    report.Add("Shader 已在 AlwaysIncluded: " + shaderName);
                    return;
                }
            }
            int idx = arr.arraySize;
            arr.InsertArrayElementAtIndex(idx);
            arr.GetArrayElementAtIndex(idx).objectReferenceValue = shader;
            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
            report.Add("已把 Shader 加入 AlwaysIncluded: " + shaderName);
        }
        catch (Exception e)
        {
            report.Add("⚠ EnsureShaderAlwaysIncluded 异常: " + e.Message);
        }
    }

    static void Abort(List<string> report, string error)
    {
        report.Add("ERROR: " + error);
        WriteReport(report);
        TryWriteDone("Failed");
        Debug.LogError("[LycheeEventModelHookup_20260629] " + error);
    }

    static void WriteReport(List<string> lines)
    {
        try { File.WriteAllText(ReportPath, string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine); }
        catch (Exception e) { Debug.LogWarning("[LycheeEventModelHookup_20260629] 写报告失败: " + e.Message); }
    }

    static void TryWriteDone(string content)
    {
        try { File.WriteAllText(DonePath, content); }
        catch (Exception e) { Debug.LogWarning("[LycheeEventModelHookup_20260629] 写 done 失败: " + e.Message); }
    }

    [MenuItem("Tools/Lychee/Replay Upgrade 20260612/Event Model Hookup 20260629 (rerun)")]
    static void RerunNow()
    {
        try { if (File.Exists(DonePath)) File.Delete(DonePath); }
        catch (Exception e) { Debug.LogWarning("[LycheeEventModelHookup_20260629] 删 done 失败: " + e.Message); }
        importRetryCount = 0;
        nextPollTime = 0;
    }

    /// <summary>把事件模型一次性传播到所有 GeneratedMaps 场景（含锦标赛克隆地图）；会逐个打开/保存场景，较重，手动触发。</summary>
    [MenuItem("Tools/Lychee/Replay Upgrade 20260612/Event Model Hookup 20260629 (propagate to ALL map scenes)")]
    static void PropagateToAllMapScenes()
    {
        importRetryCount = 0;
        if (!TryLoadModels(out GameObject ice, out GameObject horse, out GameObject spoil, out bool needRetry))
        {
            Debug.LogWarning("[LycheeEventModelHookup_20260629] 三个事件 GLB 还取不到主资产，无法传播（needRetry=" + needRetry + "）。");
            return;
        }
        Run(ice, horse, spoil, CollectScenePaths(), false);
    }
}
