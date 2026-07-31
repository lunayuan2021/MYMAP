using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace LycheeReplayDemoTools
{
    /// <summary>
    /// 动画库扫描器（编辑器）：
    /// 1. 确保 Assets/LycheeReplayDemo/Animations/ 存在并写放置说明（README_动画放置说明.txt）；
    /// 2. 扫描该目录全部资产中的 AnimationClip 子资产，按文件名/片段名关键词
    ///    （walk/idle/row/attack/build/cheer，不区分大小写）填充
    ///    Assets/LycheeReplayDemo/Resources/LycheeAnimationLibrary.asset（没有就创建）；
    /// 3. legacy 片段跳过并在报告注明；报告写到工程根目录 LycheeAnimationLibrary_20260612_report.txt。
    /// 自动执行一次（根目录 .lychee_anim_library_20260612.done 标记防重复），菜单可强制重跑。
    /// </summary>
    [InitializeOnLoad]
    internal static class LycheeAnimationLibraryBuilder20260612
    {
        const string MarkerFileName = ".lychee_anim_library_20260612.done";
        const string ReportFileName = "LycheeAnimationLibrary_20260612_report.txt";
        const string AnimationsFolder = "Assets/LycheeReplayDemo/Animations";
        const string ResourcesFolder = "Assets/LycheeReplayDemo/Resources";
        const string LibraryAssetPath = "Assets/LycheeReplayDemo/Resources/LycheeAnimationLibrary.asset";
        const string ReadmeFileName = "README_动画放置说明.txt";

        static readonly string[] SlotKeywords = { "walk", "idle", "row", "attack", "build", "cheer" };

        sealed class ClipRecord
        {
            public AnimationClip clip;
            public string assetPath;
            public bool legacy;
        }

        static LycheeAnimationLibraryBuilder20260612()
        {
            EditorApplication.delayCall += RunOnce;
        }

        static void RunOnce()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string marker = Path.Combine(projectRoot, MarkerFileName);
            if (File.Exists(marker)) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.delayCall += RunOnce;
                return;
            }
            string report = Apply();
            File.WriteAllText(marker, "done");
            File.WriteAllText(Path.Combine(projectRoot, ReportFileName), report);
            Debug.Log("[LycheeAnimationLibraryBuilder_20260612]\n" + report);
        }

        [MenuItem("Tools/Lychee/Replay Upgrade 20260612/Scan Animation Clips")]
        static void ApplyFromMenu()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string report = Apply();
            File.WriteAllText(Path.Combine(projectRoot, MarkerFileName), "done");
            File.WriteAllText(Path.Combine(projectRoot, ReportFileName), report);
            Debug.Log("[LycheeAnimationLibraryBuilder_20260612]\n" + report);
        }

        static string Apply()
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine("LycheeAnimationLibrary_20260612 扫描报告");
            report.AppendLine("时间: " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.AppendLine();

            EnsureAnimationsFolder(report);
            List<ClipRecord> records = ScanClips(report);
            FillLibrary(records, report);

            return report.ToString();
        }

        static void EnsureAnimationsFolder(System.Text.StringBuilder report)
        {
            if (!AssetDatabase.IsValidFolder(AnimationsFolder))
            {
                AssetDatabase.CreateFolder("Assets/LycheeReplayDemo", "Animations");
                report.AppendLine("已创建目录: " + AnimationsFolder);
            }

            string readmePath = Path.Combine(Application.dataPath, "LycheeReplayDemo/Animations/" + ReadmeFileName);
            if (!File.Exists(readmePath))
            {
                File.WriteAllText(readmePath, ReadmeText());
                AssetDatabase.ImportAsset(AnimationsFolder + "/" + ReadmeFileName);
                report.AppendLine("已写入放置说明: " + AnimationsFolder + "/" + ReadmeFileName);
            }
            report.AppendLine();
        }

        static List<ClipRecord> ScanClips(System.Text.StringBuilder report)
        {
            var records = new List<ClipRecord>();
            string[] guids = AssetDatabase.FindAssets("t:AnimationClip", new[] { AnimationsFolder });
            var paths = new HashSet<string>();
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path)) paths.Add(path);
            }

            foreach (string path in paths)
            {
                Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
                if (assets == null) continue;
                foreach (Object asset in assets)
                {
                    AnimationClip clip = asset as AnimationClip;
                    if (clip == null) continue;
                    if (clip.name.StartsWith("__preview__", System.StringComparison.Ordinal)) continue;
                    records.Add(new ClipRecord { clip = clip, assetPath = path, legacy = clip.legacy });
                }
            }

            report.AppendLine("扫描目录: " + AnimationsFolder);
            report.AppendLine("找到 AnimationClip 共 " + records.Count + " 个:");
            if (records.Count == 0)
            {
                report.AppendLine("  （目录为空。把 Mixamo/AI 生成的动画 FBX/anim 丢进该目录后，");
                report.AppendLine("    菜单 Tools/Lychee/Replay Upgrade 20260612/Scan Animation Clips 重跑即可。）");
            }
            foreach (ClipRecord record in records)
            {
                report.AppendLine("  - " + record.clip.name + "  (" + record.assetPath + ")" +
                    (record.legacy ? "  [legacy，跳过：Playables 不支持 legacy 片段]" : ""));
            }
            report.AppendLine();
            return records;
        }

        static void FillLibrary(List<ClipRecord> records, System.Text.StringBuilder report)
        {
            LycheeAnimationLibrary library = AssetDatabase.LoadAssetAtPath<LycheeAnimationLibrary>(LibraryAssetPath);
            if (library == null)
            {
                if (!AssetDatabase.IsValidFolder(ResourcesFolder))
                    AssetDatabase.CreateFolder("Assets/LycheeReplayDemo", "Resources");
                library = ScriptableObject.CreateInstance<LycheeAnimationLibrary>();
                AssetDatabase.CreateAsset(library, LibraryAssetPath);
                report.AppendLine("已创建动画库资产: " + LibraryAssetPath);
            }
            else
            {
                report.AppendLine("更新动画库资产: " + LibraryAssetPath);
            }

            var found = new AnimationClip[SlotKeywords.Length];
            foreach (ClipRecord record in records)
            {
                if (record == null || record.clip == null || record.legacy) continue;
                string clipName = record.clip.name.ToLowerInvariant();
                string fileName = Path.GetFileNameWithoutExtension(record.assetPath).ToLowerInvariant();
                for (int i = 0; i < SlotKeywords.Length; i++)
                {
                    if (found[i] != null) continue;
                    if (clipName.Contains(SlotKeywords[i]) || fileName.Contains(SlotKeywords[i]))
                        found[i] = record.clip;
                }
            }

            if (found[0] != null) library.walk = found[0];
            if (found[1] != null) library.idle = found[1];
            if (found[2] != null) library.row = found[2];
            if (found[3] != null) library.attack = found[3];
            if (found[4] != null) library.build = found[4];
            if (found[5] != null) library.cheer = found[5];

            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();

            report.AppendLine("槽位状态:");
            AppendSlot(report, "walk", library.walk, found[0]);
            AppendSlot(report, "idle", library.idle, found[1]);
            AppendSlot(report, "row", library.row, found[2]);
            AppendSlot(report, "attack", library.attack, found[3]);
            AppendSlot(report, "build", library.build, found[4]);
            AppendSlot(report, "cheer", library.cheer, found[5]);
            report.AppendLine();
            report.AppendLine("说明: walk/idle/row 槽位有片段时 LycheeCharacterMotion 用 Playables 循环播放，");
            report.AppendLine("缺的槽位继续走程序化骨骼动画；attack/build/cheer 槽位当前保留为后续升级。");
        }

        static void AppendSlot(System.Text.StringBuilder report, string slotName, AnimationClip current, AnimationClip newlyFound)
        {
            string state;
            if (newlyFound != null) state = "已接入 → " + newlyFound.name;
            else if (current != null) state = "保留原值 → " + current.name;
            else state = "未找到（运行时程序化兜底）";
            report.AppendLine("  " + slotName.PadRight(7) + state);
        }

        static string ReadmeText()
        {
            return
"《一骑红尘：荔枝争运战》角色动画放置说明\n" +
"==========================================\n" +
"1. 把 Mixamo / AI 生成的动画 FBX 或 .anim 文件直接放进本目录\n" +
"   （Assets/LycheeReplayDemo/Animations/）。\n" +
"2. 文件名或动画片段名包含以下关键词（不区分大小写）即自动接入：\n" +
"   walk / idle / row / attack / build / cheer\n" +
"   例如：Captain_Walk.fbx、rowing_row_loop.anim、Attack01.fbx。\n" +
"3. 放好后执行菜单 Tools > Lychee > Replay Upgrade 20260612 > Scan Animation Clips，\n" +
"   片段会自动填入 Resources/LycheeAnimationLibrary.asset，\n" +
"   并在工程根目录生成 LycheeAnimationLibrary_20260612_report.txt 报告。\n" +
"4. 人形骨骼命名与 Captain 模型一致（Mixamo 标准：Hips/Spine/Spine1/Spine2/Neck/Head/\n" +
"   Left(Right)Shoulder/Arm/ForeArm/Hand/UpLeg/Leg/Foot/ToeBase）可直接重定向；\n" +
"   FBX 导入 Rig 选 Humanoid 或 Generic 均可，Legacy 片段会被跳过。\n" +
"5. walk / idle / row 槽位由 LycheeCharacterMotion 用 Playables 循环播放\n" +
"   （记得在导入设置里勾选 Loop Time）；缺的槽位继续使用程序化骨骼动画；\n" +
"   attack / build / cheer 槽位当前保留，动作仍由程序化覆盖层实现。\n";
        }
    }
}
