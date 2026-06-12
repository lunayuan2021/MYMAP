using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Play 模式自动截图（运行时部分）：
/// 仅当工程根目录存在 LycheePlaymodeCapture.request 标记文件时自举（平时零开销）。
/// 按请求里的回合列表依次 Seek → 播放若干真实秒 → 暂停 → ScreenCapture 截取 Game 视图
/// （含 ScreenSpaceOverlay HUD 与 3D 地图的合成画面），可选最后补一张结算界面截图。
/// 全部完成后写 LycheePlaymodeCapture.done 报告（文件清单+尺寸，异常时带 error 字段），
/// 删除 .request，并退出播放模式。整体 240 真实秒超时强制收尾，状态全在文件系统，
/// domain reload 安全。
/// </summary>
public class LycheePlaymodeCaptureRunner : MonoBehaviour
{
    /// <summary>全局禁用开关：true 时不自举。</summary>
    public static bool Disabled;

    const string RequestFileName = "LycheePlaymodeCapture.request";
    const string DoneFileName = "LycheePlaymodeCapture.done";
    const float PlayerReadyTimeoutSeconds = 30f;
    const float OverallTimeoutSeconds = 240f;
    const float PostPauseDelaySeconds = 0.4f;
    const float FileFlushDelaySeconds = 0.8f;
    const float SettlementWaitSeconds = 7f; // 结算入场编排（六行跳分+总分+MVP）完整走完约 6.5 秒

    [Serializable]
    public class CaptureRequest
    {
        public int[] rounds;
        public bool settleEnd = true;
        public float secondsPerCapture = 2f;
        // 截图会话期间临时放慢回放节奏（秒/回合），避免 2 秒播放冲过目标事件回合；<=0 表示不干预。
        public float captureSecondsPerRound = 0.6f;
        public string outDir = "PlaymodeCaptures_20260612";
    }

    [Serializable]
    class DoneReport
    {
        public string outDir;
        public string[] files;
        public string error;
    }

    static int[] DefaultRounds()
    {
        return new[] { 10, 100, 216, 300, 355, 451, 517 };
    }

    LycheeReplayPlayer player;
    CaptureRequest request;
    float originalSecondsPerRound = -1f;
    readonly List<string> capturedFiles = new List<string>();
    readonly List<string> errors = new List<string>();
    string projectRoot;
    string requestPath;
    string donePath;
    float routineStartTime;
    bool routineStarted;
    bool finished;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Disabled) return;
        try
        {
            string path = Path.Combine(Application.dataPath, "../" + RequestFileName);
            if (!File.Exists(path)) return;
        }
        catch (Exception)
        {
            return;
        }
#if UNITY_EDITOR
        // 编辑器会话护栏：只认编辑器看守（LycheePlaymodeCapture_20260612.StartCapture）发起的截图会话。
        // 否则根目录残留 .request 时，用户手动按 Play 也会被本 Runner 劫持（抢占回放、截图后强退播放）。
        // SessionState 跨 domain reload 保留、编辑器重启清零，正好匹配"一次看守会话"的生命周期。
        if (!UnityEditor.SessionState.GetBool("LycheePlaymodeCapture.SessionActive", false)) return;
#endif
        if (FindFirstObjectByType<LycheePlaymodeCaptureRunner>() != null) return;
        LycheeReplayPlayer host = FindFirstObjectByType<LycheeReplayPlayer>();
        // 正常挂到 LycheeReplayPlayer 物体上；找不到时退化为独立物体，
        // 仍能在等待超时后写出带 error 的 done 报告，避免编辑器侧死等。
        GameObject target = host != null ? host.gameObject : new GameObject("LycheePlaymodeCaptureRunner");
        target.AddComponent<LycheePlaymodeCaptureRunner>();
    }

    void Awake()
    {
        // 失焦时也保持帧循环推进：编辑器里此设置无副作用（编辑器侧由 InteractionMode 兜底），
        // 打包后的播放器则靠它保证后台截图不冻结。
        Application.runInBackground = true;
        try
        {
            projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }
        catch (Exception exception)
        {
            errors.Add("解析工程根目录失败: " + exception.Message);
            projectRoot = Application.dataPath;
        }
        requestPath = Path.Combine(projectRoot, RequestFileName);
        donePath = Path.Combine(projectRoot, DoneFileName);
        request = LoadRequest();
        player = GetComponent<LycheeReplayPlayer>();
    }

    void Start()
    {
        routineStartTime = Time.realtimeSinceStartup;
        routineStarted = true;
        StartCoroutine(CaptureRoutine());
    }

    void Update()
    {
        // 看门狗：协程意外卡住时强制收尾，保证 done 文件一定写出。
        if (finished || !routineStarted) return;
        if (Time.realtimeSinceStartup - routineStartTime > OverallTimeoutSeconds + 30f)
        {
            errors.Add("看门狗触发：截图流程疑似卡住，强制收尾。");
            try
            {
                StopAllCoroutines();
            }
            catch (Exception)
            {
            }
            Finish();
        }
    }

    CaptureRequest LoadRequest()
    {
        CaptureRequest result = null;
        try
        {
            if (!string.IsNullOrEmpty(requestPath) && File.Exists(requestPath))
            {
                string text = File.ReadAllText(requestPath);
                if (!string.IsNullOrEmpty(text))
                    result = JsonUtility.FromJson<CaptureRequest>(text);
            }
        }
        catch (Exception exception)
        {
            errors.Add("请求文件解析失败，使用默认参数: " + exception.Message);
            result = null;
        }
        if (result == null) result = new CaptureRequest();
        if (result.rounds == null || result.rounds.Length == 0) result.rounds = DefaultRounds();
        if (float.IsNaN(result.secondsPerCapture) || result.secondsPerCapture <= 0f)
            result.secondsPerCapture = 2f;
        result.secondsPerCapture = Mathf.Clamp(result.secondsPerCapture, 0.1f, 30f);
        if (string.IsNullOrEmpty(result.outDir)) result.outDir = "PlaymodeCaptures_20260612";
        return result;
    }

    IEnumerator CaptureRoutine()
    {
        // 1. 等待回放播放器就绪（最多 30 真实秒）
        while (Time.realtimeSinceStartup - routineStartTime < PlayerReadyTimeoutSeconds)
        {
            if (player == null) player = FindFirstObjectByType<LycheeReplayPlayer>();
            if (player != null && player.TotalRounds > 0) break;
            yield return null;
        }

        if (player == null || player.TotalRounds <= 0)
        {
            errors.Add("等待 LycheeReplayPlayer 就绪超时（" + PlayerReadyTimeoutSeconds + " 秒），未能截图。");
            Finish();
            yield break;
        }

        string outDir = ResolveOutDir();
        Debug.Log("[LycheePlaymodeCapture] 开始自动截图，共 " + request.rounds.Length +
            " 个回合点，输出目录: " + outDir, this);

        if (request.captureSecondsPerRound > 0f)
        {
            originalSecondsPerRound = player.secondsPerRound;
            player.secondsPerRound = Mathf.Clamp(request.captureSecondsPerRound, 0.05f, 1f);
        }

        // 2. 逐回合截图
        foreach (int round in request.rounds)
        {
            if (TimedOut())
            {
                errors.Add("整体超时 " + OverallTimeoutSeconds + " 秒，剩余回合截图已跳过。");
                break;
            }
            // 开局演出会暂停回放：拍非开局回合时全程驱逐它（它可能在本循环开始后才弹出——竞速）。
            if (round > 5 && LycheeOpeningShowcase.AnyShowing)
            {
                LycheeOpeningShowcase.SkipActive();
                yield return new WaitForSecondsRealtime(0.35f);
            }
            SafeSeek(Mathf.Max(1, round - 1));
            SafeSetPlaying(true);
            float captureWait = 0f;
            while (captureWait < request.secondsPerCapture)
            {
                if (round > 5 && LycheeOpeningShowcase.AnyShowing)
                {
                    LycheeOpeningShowcase.SkipActive();
                    SafeSetPlaying(true); // 演出关闭时会还原它弹出时记录的播放状态，这里强制恢复播放
                }
                captureWait += Time.unscaledDeltaTime;
                yield return null;
            }
            SafeSetPlaying(false);
            yield return new WaitForSecondsRealtime(PostPauseDelaySeconds);
            SafeCapture(Path.Combine(outDir, "round_" + Mathf.Max(0, round).ToString("0000") + ".png"));
            yield return new WaitForSecondsRealtime(FileFlushDelaySeconds);
        }

        // 3. 结算界面截图（播到末尾触发 MatchOver 弹出结算界面）
        if (request.settleEnd)
        {
            if (TimedOut())
            {
                errors.Add("整体超时，结算界面截图已跳过。");
            }
            else
            {
                SafeSeek(Mathf.Max(1, player.TotalRounds - 1));
                SafeSetPlaying(true);
                yield return new WaitForSecondsRealtime(SettlementWaitSeconds);
                SafeCapture(Path.Combine(outDir, "settlement.png"));
                yield return new WaitForSecondsRealtime(FileFlushDelaySeconds);
            }
        }

        Finish();
    }

    bool TimedOut()
    {
        return Time.realtimeSinceStartup - routineStartTime > OverallTimeoutSeconds;
    }

    string ResolveOutDir()
    {
        try
        {
            string dir = Path.GetFullPath(Path.Combine(projectRoot, request.outDir));
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch (Exception exception)
        {
            errors.Add("创建输出目录失败，改用工程根目录: " + exception.Message);
            return projectRoot;
        }
    }

    void SafeSeek(int round)
    {
        try
        {
            if (player != null) player.SeekToRound(round);
        }
        catch (Exception exception)
        {
            errors.Add("Seek 到回合 " + round + " 失败: " + exception.Message);
        }
    }

    void SafeSetPlaying(bool value)
    {
        try
        {
            if (player != null) player.SetPlaying(value);
        }
        catch (Exception exception)
        {
            errors.Add("切换播放状态失败: " + exception.Message);
        }
    }

    void SafeCapture(string absolutePath)
    {
        try
        {
            ScreenCapture.CaptureScreenshot(absolutePath);
            capturedFiles.Add(absolutePath);
            Debug.Log("[LycheePlaymodeCapture] 截图: " + Path.GetFileName(absolutePath), this);
        }
        catch (Exception exception)
        {
            errors.Add("截图 " + SafeFileName(absolutePath) + " 失败: " + exception.Message);
        }
    }

    static string SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch (Exception)
        {
            return path ?? "(null)";
        }
    }

    void Finish()
    {
        if (finished) return;
        finished = true;
        try
        {
            if (player != null) player.SetPlaying(false);
            if (player != null && originalSecondsPerRound > 0f)
                player.secondsPerRound = originalSecondsPerRound;
        }
        catch (Exception)
        {
        }
        WriteDoneFile();
        try
        {
            if (!string.IsNullOrEmpty(requestPath) && File.Exists(requestPath))
                File.Delete(requestPath);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[LycheePlaymodeCapture] 删除请求文件失败: " + exception.Message, this);
        }
        Debug.Log("[LycheePlaymodeCapture] 截图流程结束，报告: " + donePath, this);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
        Application.Quit();
    }

    void WriteDoneFile()
    {
        if (string.IsNullOrEmpty(donePath)) return;
        try
        {
            var fileLines = new List<string>();
            foreach (string path in capturedFiles)
            {
                string name = SafeFileName(path);
                try
                {
                    if (File.Exists(path))
                        fileLines.Add(name + " (" + new FileInfo(path).Length + " bytes)");
                    else
                        fileLines.Add(name + " (missing)");
                }
                catch (Exception)
                {
                    fileLines.Add(name + " (size unknown)");
                }
            }
            DoneReport report = new DoneReport
            {
                outDir = request != null ? request.outDir : "",
                files = fileLines.ToArray(),
                error = errors.Count == 0 ? "" : string.Join(" | ", errors)
            };
            File.WriteAllText(donePath, JsonUtility.ToJson(report, true));
        }
        catch (Exception exception)
        {
            try
            {
                File.WriteAllText(
                    donePath,
                    "{\"files\":[],\"error\":\"写报告失败: " + exception.Message.Replace("\"", "'") + "\"}");
            }
            catch (Exception)
            {
                // 连兜底写入都失败时只能放弃，编辑器侧靠用户手动退出播放。
            }
        }
    }
}
