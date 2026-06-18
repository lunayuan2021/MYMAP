using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 一次性场景重调（20260612B）：回放节奏放慢 + 小队员换绑骨模型 + 相机"平时远景、事件近景"参数。
/// 场景里序列化的组件值会覆盖脚本默认值，所以只改脚本不生效——必须直接改场景组件并保存。
/// 沿用 LycheeWebGLBuild_20260612 的标记文件模式：根目录 done 标记缺失时在 delayCall 里执行一次，
/// 正在编译/播放则下一拍重试；结果（每个字段 旧值→新值）写报告文件，成功/失败都落 done 防止重跑。
/// </summary>
[InitializeOnLoad]
static class LycheePresentationRetune_20260612B
{
    const string ScenePath = "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity";
    const string MemberModelPath = "Assets/LycheeReplayDemo/ModelsGLB/Member.glb";
    const string DoneFileName = ".lychee_presentation_retune_20260612B.done";
    const string ReportFileName = "LycheePresentationRetune_20260612B_report.txt";

    static double nextPollTime;

    static LycheePresentationRetune_20260612B()
    {
        // delayCall 在域重载期间注册偶发丢失（本工程实测踩过），改用 update 轮询：
        // done 标记在场时每拍只做一次廉价文件检查，和 LycheeWebGLBuild 的看守同款模式。
        EditorApplication.update += Poll;
    }

    static void Poll()
    {
        if (EditorApplication.timeSinceStartup < nextPollTime) return;
        nextPollTime = EditorApplication.timeSinceStartup + 2.0;
        TryRun();
    }

    static string ProjectRoot
    {
        get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..")); }
    }

    static string DonePath
    {
        get { return Path.Combine(ProjectRoot, DoneFileName); }
    }

    static string ReportPath
    {
        get { return Path.Combine(ProjectRoot, ReportFileName); }
    }

    static void TryRun()
    {
        try
        {
            if (File.Exists(DonePath)) return;
        }
        catch (Exception)
        {
            return; // 标记状态都读不出来时宁可什么都不做
        }

        // 编辑器忙（编译/导入/播放中）→ 等下一拍轮询再试。
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
        report.Add("LycheePresentationRetune_20260612B " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        try
        {
            // 1. 永远从磁盘强制重载目标场景：磁盘版本可能刚被外部回滚过（如彩带贴地回滚），
            //    编辑器内存里驻留的旧状态一旦被 SaveScene 写回就会冲掉回滚成果。
            //    因此目标场景本身绝不先保存，直接丢弃内存态从磁盘重开；
            //    其他场景（路径非空且有未保存改动）才先落盘，避免丢用户改动。
            Scene active = SceneManager.GetActiveScene();
            if (!string.Equals(active.path, ScenePath, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(active.path) && active.isDirty)
            {
                EditorSceneManager.SaveOpenScenes();
            }
            active = EditorSceneManager.OpenScene(ScenePath);
            report.Add("已从磁盘重载场景: " + ScenePath);

            // 2. 先把所有目标对象凑齐再动手，任何一个缺失都整体中止、不保存场景。
            //    相机控制器在场景里可能处于禁用状态（运行时才被强制启用），必须 Include 非激活对象。
            LycheeReplayPlayer player =
                UnityEngine.Object.FindAnyObjectByType<LycheeReplayPlayer>(FindObjectsInactive.Include);
            LycheeReplayCameraController cameraController =
                UnityEngine.Object.FindAnyObjectByType<LycheeReplayCameraController>(FindObjectsInactive.Include);
            GameObject memberModel = AssetDatabase.LoadAssetAtPath<GameObject>(MemberModelPath);

            if (player == null)
            {
                Abort(report, "未找到 LycheeReplayPlayer，场景未修改。");
                return;
            }
            if (cameraController == null)
            {
                Abort(report, "未找到 LycheeReplayCameraController（主相机），场景未修改。");
                return;
            }
            if (memberModel == null)
            {
                Abort(report, "加载 " + MemberModelPath + " 为 null（绑骨队员模型缺失？），场景未修改。");
                return;
            }

            // 3. 回放节奏 + 小队员模型：SquadMember.glb 是无骨骼的静态单节点，
            //    换成骨骼名与 Captain 一致的 Member.glb 才能被 LycheeCharacterMotion 驱动。
            report.Add(SetFloat("LycheeReplayPlayer.secondsPerRound",
                player.secondsPerRound, 1.00f, v => player.secondsPerRound = v));
            string oldModel = player.squadMemberModel == null
                ? "(null)" : AssetDatabase.GetAssetPath(player.squadMemberModel);
            if (string.IsNullOrEmpty(oldModel)) oldModel = player.squadMemberModel.name;
            player.squadMemberModel = memberModel;
            report.Add("LycheeReplayPlayer.squadMemberModel: " + oldModel + " → " + MemberModelPath);

            // 4. 相机参数：平时拉远拉低看全图，事件聚焦/轮流跟拍时才贴近。
            report.Add(SetFloat("camera.perspectiveFieldOfView",
                cameraController.perspectiveFieldOfView, 42f, v => cameraController.perspectiveFieldOfView = v));
            report.Add(SetFloat("camera.perspectivePitch",
                cameraController.perspectivePitch, 41f, v => cameraController.perspectivePitch = v));
            report.Add(SetFloat("camera.perspectiveMinDistance",
                cameraController.perspectiveMinDistance, 17f, v => cameraController.perspectiveMinDistance = v));
            report.Add(SetFloat("camera.perspectiveMaxDistance",
                cameraController.perspectiveMaxDistance, 62f, v => cameraController.perspectiveMaxDistance = v));
            report.Add(SetFloat("camera.perspectiveDistancePadding",
                cameraController.perspectiveDistancePadding, 12f, v => cameraController.perspectiveDistancePadding = v));
            report.Add(SetFloat("camera.perspectiveDistanceSpanScale",
                cameraController.perspectiveDistanceSpanScale, 1.25f, v => cameraController.perspectiveDistanceSpanScale = v));
            report.Add(SetFloat("camera.perspectiveZoomMultiplier",
                cameraController.perspectiveZoomMultiplier, 1f, v => cameraController.perspectiveZoomMultiplier = v));
            report.Add(SetFloat("camera.perspectiveEventDistance",
                cameraController.perspectiveEventDistance, 15f, v => cameraController.perspectiveEventDistance = v));
            report.Add(SetFloat("camera.focusedSpan",
                cameraController.focusedSpan, 24f, v => cameraController.focusedSpan = v));
            report.Add(SetFloat("camera.focusSwapSeconds",
                cameraController.focusSwapSeconds, 14f, v => cameraController.focusSwapSeconds = v));

            // 5. 落盘：标脏两个组件再整场景保存。
            EditorUtility.SetDirty(player);
            EditorUtility.SetDirty(cameraController);
            bool saved = EditorSceneManager.SaveScene(active);
            report.Add(saved ? "场景已保存: " + active.path : "ERROR: SaveScene 返回 false！");

            WriteReport(report);
            File.WriteAllText(DonePath, saved ? "Succeeded" : "SaveFailed");
            Debug.Log("[LycheePresentationRetune_20260612B] 重调完成，报告: " + ReportPath);
        }
        catch (Exception exception)
        {
            report.Add("EXCEPTION: " + exception);
            WriteReport(report);
            // 异常也写 done：避免每次 domain reload 都重开场景反复折腾；要重跑走菜单项。
            TryWriteDone("Exception");
            Debug.LogError("[LycheePresentationRetune_20260612B] 执行异常: " + exception.Message);
        }
    }

    /// <summary>统一的"旧值→新值"赋值与记录；浮点用 R 格式保证可读且无损。</summary>
    static string SetFloat(string label, float oldValue, float newValue, Action<float> setter)
    {
        setter(newValue);
        return label + ": " + oldValue.ToString("R") + " → " + newValue.ToString("R");
    }

    /// <summary>致命错误收尾：写报告 + done（防重跑），不保存场景。</summary>
    static void Abort(List<string> report, string error)
    {
        report.Add("ERROR: " + error);
        WriteReport(report);
        TryWriteDone("Failed");
        Debug.LogError("[LycheePresentationRetune_20260612B] " + error + " 报告: " + ReportPath);
    }

    static void WriteReport(List<string> lines)
    {
        try
        {
            File.WriteAllText(ReportPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[LycheePresentationRetune_20260612B] 写报告失败: " + exception.Message);
        }
    }

    static void TryWriteDone(string content)
    {
        try
        {
            File.WriteAllText(DonePath, content);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[LycheePresentationRetune_20260612B] 写 done 标记失败: " + exception.Message);
        }
    }

    [MenuItem("Tools/Lychee/Replay Upgrade 20260612/Presentation Retune B (rerun)")]
    static void RerunNow()
    {
        try
        {
            if (File.Exists(DonePath)) File.Delete(DonePath);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[LycheePresentationRetune_20260612B] 删除 done 标记失败: " + exception.Message);
        }
        TryRun();
    }
}
