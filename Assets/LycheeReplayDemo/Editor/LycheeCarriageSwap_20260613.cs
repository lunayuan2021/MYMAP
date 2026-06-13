using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 一次性马车替换（20260613）：carriageModel 换成用户提供的组合马车 horse_new.glb（马+车一体绑骨、带贴图），
/// 同时清空 horseModel/horseWalkClip——组合模型自含马匹，独立挂马会出现双马。
/// 运行时配套：LycheeCharacterMotion 检测到车体含 SkinnedMeshRenderer 时自动跳过假车轮与挂马。
/// 注意：horse_new 当前没有动画 clip（绑骨未做 Animate），腿部静止；用户在 Meshy 补 Walking 动画
/// 重新导出后，把带动画版覆盖 horse_new.glb 即可（届时再扩展 clip 播放）。
/// 模式照搬 LycheePresentationRetune_20260612B：update 轮询 + done 标记 + 报告 + 磁盘强制重载场景。
/// </summary>
[InitializeOnLoad]
static class LycheeCarriageSwap_20260613
{
    const string ScenePath = "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity";
    const string CombinedModelPath = "Assets/LycheeReplayDemo/ModelsGLB/horse_new.glb";
    const string DoneFileName = ".lychee_carriage_swap_20260613.done";
    const string ReportFileName = "LycheeCarriageSwap_20260613_report.txt";

    static double nextPollTime;

    static LycheeCarriageSwap_20260613()
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
        report.Add("LycheeCarriageSwap_20260613 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        try
        {
            GameObject combined = AssetDatabase.LoadAssetAtPath<GameObject>(CombinedModelPath);
            if (combined == null)
            {
                // 资产可能尚未导入完成，不写 done 静默等下一拍（同 HorseHookup 的落地检测语义）
                report.Add("horse_new.glb 主资产暂取不到，等待导入完成后重试。");
                WriteReport(report);
                return;
            }

            Scene active = SceneManager.GetActiveScene();
            if (!string.Equals(active.path, ScenePath, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(active.path) && active.isDirty)
            {
                EditorSceneManager.SaveOpenScenes();
            }
            active = EditorSceneManager.OpenScene(ScenePath);
            report.Add("已从磁盘重载场景: " + ScenePath);

            LycheeReplayPlayer player =
                UnityEngine.Object.FindAnyObjectByType<LycheeReplayPlayer>(FindObjectsInactive.Include);
            if (player == null)
            {
                Abort(report, "未找到 LycheeReplayPlayer，场景未修改。");
                return;
            }

            string oldCarriage = player.carriageModel == null
                ? "(null)" : AssetDatabase.GetAssetPath(player.carriageModel);
            string oldHorse = player.horseModel == null
                ? "(null)" : AssetDatabase.GetAssetPath(player.horseModel);
            player.carriageModel = combined;
            player.horseModel = null;     // 组合模型自含马匹，独立马退役防双马
            report.Add("carriageModel: " + oldCarriage + " → " + CombinedModelPath);
            report.Add("horseModel: " + oldHorse + " → (null，组合模型自含)");

            // v2：组合模型自带动画 clip 时挑选给 horseWalkClip（组合车体复用马匹 Playables 管线）；
            // 当前 horse_new.glb 无动画——用户在 Meshy 补 Walking 重导出后菜单 rerun 即自动接上
            AnimationClip pickedClip = null;
            var clipNames = new List<string>();
            foreach (UnityEngine.Object sub in AssetDatabase.LoadAllAssetRepresentationsAtPath(CombinedModelPath))
            {
                AnimationClip clip = sub as AnimationClip;
                if (clip == null || clip.legacy) continue;
                clipNames.Add(clip.name);
                string lower = clip.name.ToLowerInvariant();
                if (pickedClip == null ||
                    lower.Contains("walk") || lower.Contains("run") || lower.Contains("move"))
                {
                    if (pickedClip == null || lower.Contains("walk")) pickedClip = clip;
                }
            }
            player.horseWalkClip = pickedClip;
            report.Add("组合模型 clip 名单: " + (clipNames.Count == 0 ? "(无动画——腿部静态，待 Meshy 补 Walking)" : string.Join(", ", clipNames)));
            report.Add("horseWalkClip → " + (pickedClip != null ? pickedClip.name : "(null)"));

            // v3：朝向不再手猜——运行时 LycheeCharacterMotion.AlignCombinedCarriage 量出马头方向
            // 自动转正对齐车队前进方向，故这里清零手动偏航（自动对齐对任何残留值都自纠正）。
            float oldYaw = player.carriageYawOffset;
            player.carriageYawOffset = 0f;
            report.Add(string.Format("carriageYawOffset: {0} → 0（改由运行时自动对齐）", oldYaw));

            EditorUtility.SetDirty(player);
            bool saved = EditorSceneManager.SaveScene(active);
            report.Add("场景保存: " + saved);
            WriteReport(report);
            File.WriteAllText(DonePath, saved ? "Succeeded" : "SaveFailed");
            Debug.Log("[LycheeCarriageSwap_20260613] 完成，报告: " + ReportPath);
        }
        catch (Exception exception)
        {
            report.Add("EXCEPTION: " + exception);
            WriteReport(report);
            TryWriteDone("Exception");
            Debug.LogError("[LycheeCarriageSwap_20260613] 执行异常: " + exception.Message);
        }
    }

    static void Abort(List<string> report, string error)
    {
        report.Add("ERROR: " + error);
        WriteReport(report);
        TryWriteDone("Failed");
        Debug.LogError("[LycheeCarriageSwap_20260613] " + error);
    }

    static void WriteReport(List<string> lines)
    {
        try { File.WriteAllText(ReportPath, string.Join(Environment.NewLine, lines) + Environment.NewLine); }
        catch (Exception e) { Debug.LogWarning("[LycheeCarriageSwap_20260613] 写报告失败: " + e.Message); }
    }

    static void TryWriteDone(string content)
    {
        try { File.WriteAllText(DonePath, content); }
        catch (Exception e) { Debug.LogWarning("[LycheeCarriageSwap_20260613] 写 done 失败: " + e.Message); }
    }

    [MenuItem("Tools/Lychee/Replay Upgrade 20260612/Carriage Swap 20260613 (rerun)")]
    static void RerunNow()
    {
        try { if (File.Exists(DonePath)) File.Delete(DonePath); }
        catch (Exception e) { Debug.LogWarning("[LycheeCarriageSwap_20260613] 删 done 失败: " + e.Message); }
        nextPollTime = 0;
    }
}
