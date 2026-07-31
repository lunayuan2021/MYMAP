using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Play 模式自动截图（编辑器部分）：
/// 每 2 真实秒轮询工程根目录的 LycheePlaymodeCapture.request / .done 标记文件。
/// 发现请求 → 确保打开 LycheeReplayDemo 场景与 Game 视图 → 进入播放模式，
/// 截图与退出播放都由运行时 LycheePlaymodeCaptureRunner 接管（自带看门狗保证必然收尾），
/// 编辑器侧只负责发起会话与恢复交互模式，绝不主动改 isPlaying=false（避免误伤手动 Play）。
/// 轮询逻辑全程无内存状态（状态都在文件系统），进入播放的 domain reload 不影响流程。
/// </summary>
[InitializeOnLoad]
public static class LycheePlaymodeCapture_20260612
{
    const string ScenePath = "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity";
    const string RequestFileName = "LycheePlaymodeCapture.request";
    const string DoneFileName = "LycheePlaymodeCapture.done";
    const double PollIntervalSeconds = 2.0;
    const string DefaultRequestJson =
        "{\"rounds\":[10,100,216,300,355,451,517],\"settleEnd\":true," +
        "\"secondsPerCapture\":2.0,\"outDir\":\"PlaymodeCaptures_20260612\"}";
    // 编辑器交互模式偏好键：0=Default(失焦节流到 4fps 甚至挂起)，1=No Throttling。
    // 失焦节流会让 Play 模式协程的 WaitForSecondsRealtime 不推进，自动截图整体冻结。
    const string InteractionModePrefKey = "InteractionMode";
    // 原值存 SessionState（进 Play 的 domain reload 不丢），不要存内存字段。
    const string SavedInteractionModeKey = "LycheePlaymodeCapture.SavedInteractionMode";
    // 标记"当前播放会话由看守发起"（SessionState 跨 domain reload 保留），防止历史 .done 误杀用户手动 Play。
    const string CaptureSessionKey = "LycheePlaymodeCapture.SessionActive";
    const int InteractionModeUnsaved = int.MinValue;

    static double nextPollTime;

    static LycheePlaymodeCapture_20260612()
    {
        EditorApplication.update -= Poll;
        EditorApplication.update += Poll;
    }

    static string ProjectRoot
    {
        get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..")); }
    }

    static string RequestPath
    {
        get { return Path.Combine(ProjectRoot, RequestFileName); }
    }

    static string DonePath
    {
        get { return Path.Combine(ProjectRoot, DoneFileName); }
    }

    static void Poll()
    {
        if (EditorApplication.timeSinceStartup < nextPollTime) return;
        nextPollTime = EditorApplication.timeSinceStartup + PollIntervalSeconds;
        try
        {
            bool hasRequest = File.Exists(RequestPath);
            bool hasDone = File.Exists(DonePath);

            // 0. 看到 .done 即进入收尾：恢复进入截图前的编辑器交互模式
            //    （运行时 Runner 退出播放后才写 .done，原值在 SessionState 里跨 domain reload 仍在）。
            if (hasDone) RestoreInteractionMode();

            // 1. 看到 .done 且已不在播放 → 清掉会话标志。
            //    退出播放本身交给运行时 Runner（自带 240+30 秒看门狗保证 Finish 必然执行并自行强退），
            //    编辑器侧不再做"hasDone 且仍在播放就强退"的兜底——它没有实际触发场景，只会误伤手动 Play。
            if (hasDone && !EditorApplication.isPlaying)
                SessionState.SetBool(CaptureSessionKey, false);

            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                return;

            // 2. request 与 done 并存 → 按时间戳和解：done 更新说明该请求已完成只是清理失败；
            //    request 更新说明是新请求，清掉旧报告后下一轮启动。
            if (hasRequest && hasDone)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(DonePath) >= File.GetLastWriteTimeUtc(RequestPath))
                        File.Delete(RequestPath);
                    else
                        File.Delete(DonePath);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[LycheePlaymodeCapture] 清理标记文件失败: " + exception.Message);
                }
                return;
            }

            // 3. 只有 request → 启动截图流程
            if (hasRequest) StartCapture();
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[LycheePlaymodeCapture] 轮询异常: " + exception.Message);
        }
    }

    /// <summary>
    /// 进入截图流程前临时把编辑器交互模式设为 No Throttling，避免编辑器失焦时
    /// Play 模式帧循环被节流/挂起、WaitForSecondsRealtime 永不返回导致截图冻结。
    /// 尽力而为：InteractionMode 偏好键在不同 Unity 6 版本上的有效性不保证，失败静默。
    /// </summary>
    static void ApplyNoThrottling()
    {
        try
        {
            int original = EditorPrefs.GetInt(InteractionModePrefKey, 0);
            SessionState.SetInt(SavedInteractionModeKey, original);
            EditorPrefs.SetInt(InteractionModePrefKey, 1); // 1 = No Throttling
        }
        catch (Exception)
        {
            // 尽力而为的优化，绝不能因此中断截图流程。
        }
    }

    /// <summary>恢复进入截图前保存的交互模式；没有保存值时为空操作。domain reload 安全。</summary>
    static void RestoreInteractionMode()
    {
        try
        {
            int saved = SessionState.GetInt(SavedInteractionModeKey, InteractionModeUnsaved);
            if (saved == InteractionModeUnsaved) return;
            EditorPrefs.SetInt(InteractionModePrefKey, saved);
            SessionState.EraseInt(SavedInteractionModeKey);
        }
        catch (Exception)
        {
            // 失败静默：留给用户手动改回偏好即可，不影响截图结果。
        }
    }

    static void StartCapture()
    {
        SessionState.SetBool(CaptureSessionKey, true);
        ApplyNoThrottling();
        try
        {
            Scene active = SceneManager.GetActiveScene();
            if (!string.Equals(active.path, ScenePath, StringComparison.OrdinalIgnoreCase))
            {
                EditorSceneManager.SaveOpenScenes();
                EditorSceneManager.OpenScene(ScenePath);
            }
        }
        catch (Exception exception)
        {
            // 场景打开失败时仍尝试进入播放：运行时 Runner 会在等待超时后写出带 error 的报告。
            Debug.LogWarning("[LycheePlaymodeCapture] 切换场景失败: " + exception.Message);
        }

        try
        {
            EditorApplication.ExecuteMenuItem("Window/General/Game");
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[LycheePlaymodeCapture] 打开 Game 视图失败: " + exception.Message);
        }

        Debug.Log("[LycheePlaymodeCapture] 检测到截图请求，进入播放模式，自动截图开始……");
        EditorApplication.isPlaying = true;
    }

    [MenuItem("Tools/Lychee/Replay Upgrade 20260612/Playmode Capture Now")]
    static void CaptureNow()
    {
        try
        {
            if (File.Exists(DonePath)) File.Delete(DonePath);
            File.WriteAllText(RequestPath, DefaultRequestJson);
            Debug.Log("[LycheePlaymodeCapture] 已写入默认截图请求: " + RequestPath);
            if (!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling)
                StartCapture();
        }
        catch (Exception exception)
        {
            Debug.LogError("[LycheePlaymodeCapture] 写入截图请求失败: " + exception.Message);
        }
    }
}
