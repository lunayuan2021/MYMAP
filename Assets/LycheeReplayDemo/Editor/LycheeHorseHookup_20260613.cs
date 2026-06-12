using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 一次性马匹接线（20260613）：用户即将从 Tripo3D 下载骨骼绑定马模型到 ModelsGLB/Horse.glb，
/// 本脚本负责文件落地后的零人工接线——场景 LycheeReplayPlayer.horseModel 赋值 +
/// 从 GLB 子资产里挑行走 AnimationClip 赋给 horseWalkClip（优先名含 walk/run/move 的非 legacy clip，
/// 否则第一个非 legacy，全是 legacy 或没有 clip 就留空，运行时 LycheeCharacterMotion 程序化兜底）。
/// 与其他一次性脚本（LycheeStationLift_20260613）的关键差异：Horse.glb 不存在时**不写 done、不报错、
/// 静默继续等**——文件随时可能落地，落地触发 Unity 自动导入与轮询装配，全程无需人工。
/// 模式：update 轮询 2 秒 + done 标记 + 报告 + 磁盘重载场景 + 菜单 rerun。
/// </summary>
[InitializeOnLoad]
static class LycheeHorseHookup_20260613
{
    const string ScenePath = "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity";
    const string GlbAssetPath = "Assets/LycheeReplayDemo/ModelsGLB/Horse.glb";
    const string DoneFileName = ".lychee_horse_hookup_20260613.done";
    const string ReportFileName = "LycheeHorseHookup_20260613_report.txt";
    const int MaxImportRetries = 5; // GLB 在磁盘但取不到 GameObject 时的重导上限，防每 2 秒无限重导

    static double nextPollTime;
    static int importRetryCount;

    static LycheeHorseHookup_20260613()
    {
        EditorApplication.update += Poll;
    }

    static string ProjectRoot
    {
        get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..")); }
    }

    static string DonePath { get { return Path.Combine(ProjectRoot, DoneFileName); } }
    static string ReportPath { get { return Path.Combine(ProjectRoot, ReportFileName); } }
    static string GlbFullPath { get { return Path.Combine(ProjectRoot, GlbAssetPath); } }

    static void Poll()
    {
        if (EditorApplication.timeSinceStartup < nextPollTime) return;
        nextPollTime = EditorApplication.timeSinceStartup + 2.0;
        try
        {
            if (File.Exists(DonePath)) return;
            // 关键差异：Horse.glb 还没下载落地不算失败，不写 done 静默等下一轮，
            // 落地后的下一次轮询（或导入触发的域重载）自动装配
            if (!File.Exists(GlbFullPath)) return;
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

        GameObject horseAsset = AssetDatabase.LoadAssetAtPath<GameObject>(GlbAssetPath);
        if (horseAsset == null)
        {
            // 文件已落地但取不到 GameObject 主资产：多半是 importer 还没跑（手动拷入未刷新），
            // 主动触发同步导入再试；反复失败视为 GLB 损坏/glTFast 导入失败，报告并写 done 停止重试
            importRetryCount++;
            if (importRetryCount > MaxImportRetries)
            {
                Abort(NewReport(), "Horse.glb 已在磁盘但重导 " + MaxImportRetries +
                    " 次仍取不到 GameObject 主资产（疑似文件损坏或 glTFast 导入失败），中止。" +
                    "修复文件后用菜单 rerun 重跑。");
                return;
            }
            AssetDatabase.ImportAsset(GlbAssetPath, ImportAssetOptions.ForceSynchronousImport);
            horseAsset = AssetDatabase.LoadAssetAtPath<GameObject>(GlbAssetPath);
            if (horseAsset == null) return; // 导入产物还没就绪，下一轮再取
        }
        Run(horseAsset);
    }

    static List<string> NewReport()
    {
        return new List<string>
        {
            "LycheeHorseHookup_20260613 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };
    }

    /// <summary>
    /// 从 GLB 子资产中挑行走 clip：按 walk &gt; run &gt; move 关键词优先级（大小写无关）匹配非 legacy clip，
    /// 都没有就取第一个非 legacy；legacy clip 一律跳过——运行时走 AnimationPlayableUtilities.PlayClip，
    /// legacy clip 进 Playables 会直接报错。
    /// </summary>
    static AnimationClip PickWalkClip(List<AnimationClip> clips)
    {
        string[] keywords = { "walk", "run", "move" };
        foreach (string keyword in keywords)
        {
            foreach (AnimationClip clip in clips)
            {
                if (clip == null || clip.legacy) continue;
                if (clip.name.ToLowerInvariant().Contains(keyword)) return clip;
            }
        }
        foreach (AnimationClip clip in clips)
        {
            if (clip != null && !clip.legacy) return clip;
        }
        return null;
    }

    static void Run(GameObject horseAsset)
    {
        List<string> report = NewReport();
        try
        {
            // 1) 收集 GLB 内 AnimationClip 子资产并挑选行走 clip
            var clips = new List<AnimationClip>();
            var clipNames = new List<string>();
            UnityEngine.Object[] representations =
                AssetDatabase.LoadAllAssetRepresentationsAtPath(GlbAssetPath);
            if (representations != null)
            {
                foreach (UnityEngine.Object representation in representations)
                {
                    AnimationClip clip = representation as AnimationClip;
                    if (clip == null) continue;
                    clips.Add(clip);
                    clipNames.Add(string.Format("{0}{1} ({2:F2}s)",
                        clip.name, clip.legacy ? " [legacy]" : "", clip.length));
                }
            }
            AnimationClip chosen = PickWalkClip(clips);
            report.Add(clips.Count == 0
                ? "GLB 内没有 AnimationClip 子资产"
                : "GLB 内 clip 名单: " + string.Join(", ", clipNames.ToArray()));
            report.Add("选用 walk clip: " + (chosen != null
                ? chosen.name
                : "（无可用非 legacy clip，horseWalkClip 留空，运行时程序化 bob 兜底）"));

            // 2) 磁盘重载场景，保证改在最新序列化状态上
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
                Abort(report, "场景里找不到 LycheeReplayPlayer，中止。");
                return;
            }

            // 3) 赋值 + 存盘
            Undo.RecordObject(player, "Hookup horse model");
            player.horseModel = horseAsset;
            player.horseWalkClip = chosen;
            EditorUtility.SetDirty(player);
            bool saved = EditorSceneManager.SaveScene(active);

            // 4) 报告：模型 fileID / 骨骼数（可读时）
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(horseAsset, out string guid, out long fileId))
                report.Add(string.Format("horseModel = {0} (guid {1}, fileID {2})", horseAsset.name, guid, fileId));
            else
                report.Add("horseModel = " + horseAsset.name + "（fileID 不可读）");
            SkinnedMeshRenderer skin = horseAsset.GetComponentInChildren<SkinnedMeshRenderer>(true);
            report.Add(skin != null && skin.bones != null
                ? "骨骼数: " + skin.bones.Length
                : "骨骼数: 不可读（无 SkinnedMeshRenderer——若是静态网格，运行时程序化 bob 仍可用）");
            report.Add("场景保存: " + saved);
            WriteReport(report);
            File.WriteAllText(DonePath, saved ? "Succeeded" : "SaveFailed");
            Debug.Log("[LycheeHorseHookup_20260613] 完成，报告: " + ReportPath);
        }
        catch (Exception exception)
        {
            report.Add("EXCEPTION: " + exception);
            WriteReport(report);
            TryWriteDone("Exception");
            Debug.LogError("[LycheeHorseHookup_20260613] 执行异常: " + exception.Message);
        }
    }

    static void Abort(List<string> report, string error)
    {
        report.Add("ERROR: " + error);
        WriteReport(report);
        TryWriteDone("Failed");
        Debug.LogError("[LycheeHorseHookup_20260613] " + error);
    }

    static void WriteReport(List<string> lines)
    {
        try { File.WriteAllText(ReportPath, string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine); }
        catch (Exception e) { Debug.LogWarning("[LycheeHorseHookup_20260613] 写报告失败: " + e.Message); }
    }

    static void TryWriteDone(string content)
    {
        try { File.WriteAllText(DonePath, content); }
        catch (Exception e) { Debug.LogWarning("[LycheeHorseHookup_20260613] 写 done 失败: " + e.Message); }
    }

    [MenuItem("Tools/Lychee/Replay Upgrade 20260612/Horse Hookup 20260613 (rerun)")]
    static void RerunNow()
    {
        try { if (File.Exists(DonePath)) File.Delete(DonePath); }
        catch (Exception e) { Debug.LogWarning("[LycheeHorseHookup_20260613] 删 done 失败: " + e.Message); }
        importRetryCount = 0;
        nextPollTime = 0;
    }
}
