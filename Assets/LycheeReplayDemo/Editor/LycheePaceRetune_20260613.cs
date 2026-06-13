using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 一次性回放节奏调整：secondsPerRound → 0.40（约 220 秒全程），场景序列化值会覆盖脚本默认值，
/// 故必须直接改场景组件并保存。update 轮询 + done 标记，照搬工程一次性脚本模式。
/// </summary>
[InitializeOnLoad]
static class LycheePaceRetune_20260613
{
    const string ScenePath = "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity";
    const float TargetSecondsPerRound = 0.40f;
    const string DoneFileName = ".lychee_pace_retune_20260613.done";
    const string ReportFileName = "LycheePaceRetune_20260613_report.txt";

    static double nextPollTime;

    static LycheePaceRetune_20260613()
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

    static void Run()
    {
        try
        {
            Scene active = SceneManager.GetActiveScene();
            if (!string.Equals(active.path, ScenePath, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(active.path) && active.isDirty)
                EditorSceneManager.SaveOpenScenes();
            active = EditorSceneManager.OpenScene(ScenePath);

            LycheeReplayPlayer player =
                UnityEngine.Object.FindAnyObjectByType<LycheeReplayPlayer>(FindObjectsInactive.Include);
            if (player == null)
            {
                File.WriteAllText(ReportPath, "ERROR: 未找到 LycheeReplayPlayer\n");
                File.WriteAllText(DonePath, "Failed");
                return;
            }
            float old = player.secondsPerRound;
            player.secondsPerRound = TargetSecondsPerRound;
            EditorUtility.SetDirty(player);
            bool saved = EditorSceneManager.SaveScene(active);
            File.WriteAllText(ReportPath, string.Format(
                "LycheePaceRetune_20260613 {0}\nsecondsPerRound: {1} → {2}\n场景保存: {3}\n",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), old.ToString("R"), TargetSecondsPerRound.ToString("R"), saved));
            File.WriteAllText(DonePath, saved ? "Succeeded" : "SaveFailed");
            Debug.Log("[LycheePaceRetune_20260613] secondsPerRound → " + TargetSecondsPerRound);
        }
        catch (Exception e)
        {
            try { File.WriteAllText(ReportPath, "EXCEPTION: " + e + "\n"); } catch { }
            try { File.WriteAllText(DonePath, "Exception"); } catch { }
            Debug.LogError("[LycheePaceRetune_20260613] " + e.Message);
        }
    }

    [MenuItem("Tools/Lychee/Replay Upgrade 20260612/Pace Retune 20260613 (rerun)")]
    static void RerunNow()
    {
        try { if (File.Exists(DonePath)) File.Delete(DonePath); }
        catch (Exception e) { Debug.LogWarning(e.Message); }
        nextPollTime = 0;
    }
}
