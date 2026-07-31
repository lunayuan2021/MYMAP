using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 一次性将军接入（20260613）：将军取代队长 figure——红队赤龙将军走在车队最前领队、蓝队将军居中坐镇。
/// 红将军 RedGeneral.glb（已落地，带 walking 子动画）→ player.redGeneralModel + redGeneralWalkClip。
/// 蓝将军 BlueGeneral.glb 用户稍后提供：存在则接入，缺失则不写该字段（该队回退 captainModel 占位居中）。
/// 模型缺失（含红将军）时静默等待不写 done，文件落地后下次域重载自动接入（同 HorseHookup 语义）。
/// 模式照搬 LycheeCarriageSwap_20260613：update 轮询 + done 标记 + 报告 + 磁盘强制重载场景。
/// </summary>
[InitializeOnLoad]
static class LycheeGeneralHookup_20260613
{
    const string ScenePath = "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity";
    const string RedPath = "Assets/LycheeReplayDemo/ModelsGLB/RedGeneral.glb";
    const string BluePath = "Assets/LycheeReplayDemo/ModelsGLB/BlueGeneral.glb";
    const string RedAttackPath = "Assets/LycheeReplayDemo/ModelsGLB/RedGeneralAttack.glb";
    const string BlueAttackPath = "Assets/LycheeReplayDemo/ModelsGLB/BlueGeneralAttack.glb";
    const string DoneFileName = ".lychee_general_hookup_20260613.done";
    const string ReportFileName = "LycheeGeneralHookup_20260613_report.txt";

    static double nextPollTime;

    static LycheeGeneralHookup_20260613()
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
        try { if (File.Exists(DonePath)) return; }
        catch (Exception) { return; }
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying ||
            EditorApplication.isCompiling || EditorApplication.isUpdating)
            return;
        Run();
    }

    /// <summary>从 GLB 子资产里挑行走 clip：walk &gt; run &gt; move 关键词优先，否则第一个非 legacy clip。</summary>
    static AnimationClip PickWalkClip(string glbPath, List<string> namesOut)
    {
        AnimationClip picked = null;
        foreach (UnityEngine.Object sub in AssetDatabase.LoadAllAssetRepresentationsAtPath(glbPath))
        {
            AnimationClip clip = sub as AnimationClip;
            if (clip == null || clip.legacy) continue;
            namesOut.Add(clip.name);
            string lower = clip.name.ToLowerInvariant();
            if (picked == null || lower.Contains("walk") || lower.Contains("run") || lower.Contains("move"))
            {
                if (picked == null || lower.Contains("walk")) picked = clip;
            }
        }
        return picked;
    }

    static void Run()
    {
        var report = new List<string>();
        report.Add("LycheeGeneralHookup_20260613 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        try
        {
            GameObject red = AssetDatabase.LoadAssetAtPath<GameObject>(RedPath);
            if (red == null)
            {
                // 红将军是核心资产，取不到就静默等导入完成（不写 done）
                report.Add("RedGeneral.glb 主资产暂取不到，等待导入完成后重试。");
                WriteReport(report);
                return;
            }

            Scene active = SceneManager.GetActiveScene();
            if (!string.Equals(active.path, ScenePath, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(active.path) && active.isDirty)
                EditorSceneManager.SaveOpenScenes();
            active = EditorSceneManager.OpenScene(ScenePath);
            report.Add("已从磁盘重载场景: " + ScenePath);

            LycheeReplayPlayer player =
                UnityEngine.Object.FindAnyObjectByType<LycheeReplayPlayer>(FindObjectsInactive.Include);
            if (player == null) { Abort(report, "未找到 LycheeReplayPlayer，场景未修改。"); return; }

            // 红将军（走路 + 攻击）
            var redClips = new List<string>();
            player.redGeneralModel = red;
            player.redGeneralWalkClip = PickWalkClip(RedPath, redClips);
            player.redGeneralAttackClip = PickWalkClip(RedAttackPath, new List<string>()); // 攻击 GLB 只含攻击 clip
            report.Add("redGeneralModel → " + RedPath);
            report.Add("红将军 clip 名单: " + (redClips.Count == 0 ? "(无)" : string.Join(", ", redClips)));
            report.Add("redGeneralWalkClip → " + (player.redGeneralWalkClip != null ? player.redGeneralWalkClip.name : "(null)"));
            report.Add("redGeneralAttackClip → " + (player.redGeneralAttackClip != null ? player.redGeneralAttackClip.name : "(null，缺 RedGeneralAttack.glb)"));

            // 蓝将军（可选，用户稍后提供）
            GameObject blue = AssetDatabase.LoadAssetAtPath<GameObject>(BluePath);
            if (blue != null)
            {
                var blueClips = new List<string>();
                player.blueGeneralModel = blue;
                player.blueGeneralWalkClip = PickWalkClip(BluePath, blueClips);
                player.blueGeneralAttackClip = PickWalkClip(BlueAttackPath, new List<string>());
                report.Add("blueGeneralModel → " + BluePath);
                report.Add("blueGeneralWalkClip → " + (player.blueGeneralWalkClip != null ? player.blueGeneralWalkClip.name : "(null)"));
                report.Add("blueGeneralAttackClip → " + (player.blueGeneralAttackClip != null ? player.blueGeneralAttackClip.name : "(null，缺 BlueGeneralAttack.glb)"));
            }
            else
            {
                player.blueGeneralModel = null;
                player.blueGeneralWalkClip = null;
                player.blueGeneralAttackClip = null;
                report.Add("BlueGeneral.glb 暂缺：蓝队回退 captainModel 占位居中（待用户提供后菜单 rerun 接入）。");
            }

            EditorUtility.SetDirty(player);
            bool saved = EditorSceneManager.SaveScene(active);
            report.Add("场景保存: " + saved);
            WriteReport(report);
            File.WriteAllText(DonePath, saved ? "Succeeded" : "SaveFailed");
            Debug.Log("[LycheeGeneralHookup_20260613] 完成，报告: " + ReportPath);
        }
        catch (Exception exception)
        {
            report.Add("EXCEPTION: " + exception);
            WriteReport(report);
            TryWriteDone("Exception");
            Debug.LogError("[LycheeGeneralHookup_20260613] 执行异常: " + exception.Message);
        }
    }

    static void Abort(List<string> report, string error)
    {
        report.Add("ERROR: " + error);
        WriteReport(report);
        TryWriteDone("Failed");
        Debug.LogError("[LycheeGeneralHookup_20260613] " + error);
    }

    static void WriteReport(List<string> lines)
    {
        try { File.WriteAllText(ReportPath, string.Join(Environment.NewLine, lines) + Environment.NewLine); }
        catch (Exception e) { Debug.LogWarning("[LycheeGeneralHookup_20260613] 写报告失败: " + e.Message); }
    }

    static void TryWriteDone(string content)
    {
        try { File.WriteAllText(DonePath, content); }
        catch (Exception e) { Debug.LogWarning("[LycheeGeneralHookup_20260613] 写 done 失败: " + e.Message); }
    }

    [MenuItem("Tools/Lychee/Replay Upgrade 20260612/General Hookup 20260613 (rerun)")]
    static void RerunNow()
    {
        try { if (File.Exists(DonePath)) File.Delete(DonePath); }
        catch (Exception e) { Debug.LogWarning("[LycheeGeneralHookup_20260613] 删 done 失败: " + e.Message); }
        nextPollTime = 0;
    }
}
