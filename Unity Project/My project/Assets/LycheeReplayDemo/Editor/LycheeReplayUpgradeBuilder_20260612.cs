using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace LycheeReplayDemoTools
{
    /// <summary>
    /// 回放升级收口（2026-06-12）：
    /// 1) 建好 StreamingAssets 素材目录骨架（Audio/BGM、Audio/SFX、UITheme、Video）；
    /// 2) 缺 replay.txt 时复制演示回放（正式比赛由用户替换同名文件）；
    /// 3) 写 README_素材放置说明.txt（约定文件名与各运行时系统实现一致）；
    /// 4) 反射检测本轮 8 个子系统的全部新组件类型并出报告到工程根。
    /// 自动执行一次（项目根 .lychee_replay_upgrade_20260612.done 防重复），也可经菜单手动强制重跑。
    /// </summary>
    internal static class LycheeReplayUpgradeBuilder20260612
    {
        internal const string DoneFile = ".lychee_replay_upgrade_20260612.done";
        const string ReportFile = "LycheeReplayUpgrade_20260612_report.txt";
        const string DemoReplayRelativePath = "Assets/LycheeReplayDemo/Data/reply_full_coverage_unity_v2.txt";
        const string ReadmeFileName = "README_素材放置说明.txt";

        /// <summary>本轮升级落地的全部新组件类型（全局命名空间）。</summary>
        static readonly string[] NewComponentTypes =
        {
            "LycheeWeatherFxSystem",
            "LycheeWorldMarkerSystem",
            "LycheeHudExtensions",
            "LycheeSettlementScreen",
            "LycheeReplayCameraController",
            "LycheeCinematicDirector",
            "LycheeConvoyVfx",
            "LycheeMinimapSystem",
            "LycheeAudioDirector"
        };

        /// <summary>StreamingAssets 下需要保证存在的子目录（相对 StreamingAssets 根）。</summary>
        static readonly string[] StreamingSubDirs =
        {
            "",
            "Audio",
            "Audio/BGM",
            "Audio/SFX",
            "UITheme",
            "Video"
        };

        [MenuItem("Tools/Lychee/Replay Upgrade 20260612/Setup")]
        public static void RunMenu()
        {
            Run(true);
        }

        public static void Run(bool force)
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string donePath = Path.Combine(root, DoneFile);
            string reportPath = Path.Combine(root, ReportFile);
            if (!force && File.Exists(donePath)) return;

            StringBuilder report = new StringBuilder();
            report.AppendLine("LycheeReplayUpgrade_20260612 report");
            report.AppendLine("time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.AppendLine();

            try
            {
                // ---------- a) StreamingAssets 目录骨架 ----------
                string streamingRoot = Path.Combine(Application.dataPath, "StreamingAssets");
                report.AppendLine("[StreamingAssets 目录]");
                foreach (string sub in StreamingSubDirs)
                {
                    string dir = string.IsNullOrEmpty(sub) ? streamingRoot : Path.Combine(streamingRoot, sub);
                    bool existed = Directory.Exists(dir);
                    Directory.CreateDirectory(dir);
                    string label = string.IsNullOrEmpty(sub) ? "StreamingAssets/" : "StreamingAssets/" + sub + "/";
                    report.AppendLine("  " + label + (existed ? " 已存在" : " 已创建"));
                }
                report.AppendLine();

                // ---------- b) replay.txt（演示回放兜底） ----------
                string replyPath = Path.Combine(streamingRoot, "replay.txt");
                string demoSourcePath = Path.Combine(root, DemoReplayRelativePath);
                string replySource;
                if (File.Exists(replyPath))
                {
                    replySource = "已存在，未覆盖（可能是用户放置的正式比赛回放，或先前复制的演示回放）";
                }
                else if (File.Exists(demoSourcePath))
                {
                    File.Copy(demoSourcePath, replyPath);
                    replySource = "演示回放（复制自 " + DemoReplayRelativePath + "）";
                }
                else
                {
                    replySource = "缺失！演示数据源 " + DemoReplayRelativePath +
                                  " 不存在，运行时将回退到 LycheeReplayPlayer.replayFile 内置样例";
                }
                report.AppendLine("[replay.txt]");
                report.AppendLine("  来源=" + replySource);
                if (File.Exists(replyPath))
                {
                    FileInfo info = new FileInfo(replyPath);
                    report.AppendLine("  大小=" + info.Length + " 字节");
                }
                report.AppendLine("  说明=正式比赛把服务端产出的 replay.txt 放入 Assets/StreamingAssets/ 替换同名文件即可，无需改动工程。");
                report.AppendLine();

                // ---------- c) README 素材放置说明 ----------
                string readmePath = Path.Combine(streamingRoot, ReadmeFileName);
                File.WriteAllText(readmePath, BuildReadmeText());
                report.AppendLine("[README]");
                report.AppendLine("  已写入 Assets/StreamingAssets/" + ReadmeFileName);
                report.AppendLine();

                AssetDatabase.Refresh();

                // ---------- d) 新组件类型反射检测 ----------
                report.AppendLine("[新组件检测]（反射查找已编译类型）");
                int found = 0;
                foreach (string typeName in NewComponentTypes)
                {
                    Type type = FindRuntimeType(typeName);
                    if (type != null)
                    {
                        found++;
                        report.AppendLine("  OK      " + typeName + "  (" + type.Assembly.GetName().Name + ")");
                    }
                    else
                    {
                        report.AppendLine("  MISSING " + typeName + "  —— 未编译进任何已加载程序集，请检查对应脚本是否存在/有编译错误");
                    }
                }
                report.AppendLine("  合计=" + found + "/" + NewComponentTypes.Length);
                report.AppendLine();

                // ---------- 就绪状态总结 ----------
                bool dirsReady = true;
                foreach (string sub in StreamingSubDirs)
                {
                    string dir = string.IsNullOrEmpty(sub) ? streamingRoot : Path.Combine(streamingRoot, sub);
                    if (!Directory.Exists(dir)) dirsReady = false;
                }
                report.AppendLine("[就绪状态]");
                report.AppendLine("  StreamingAssets 目录=" + (dirsReady ? "就绪" : "不完整"));
                report.AppendLine("  replay.txt=" + (File.Exists(replyPath) ? "就绪" : "缺失（将走内置样例）"));
                report.AppendLine("  README=" + (File.Exists(readmePath) ? "就绪" : "缺失"));
                report.AppendLine("  组件=" + (found == NewComponentTypes.Length ? "全部就绪" : "存在缺失，见上"));

                File.WriteAllText(reportPath, report.ToString());
                File.WriteAllText(donePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Debug.Log(report.ToString());
            }
            catch (Exception ex)
            {
                report.AppendLine("ERROR: " + ex);
                try { File.WriteAllText(reportPath, report.ToString()); } catch { }
                Debug.LogError(ex);
            }
        }

        /// <summary>先按 Assembly-CSharp 限定名查找，再扫描全部已加载程序集兜底。</summary>
        static Type FindRuntimeType(string typeName)
        {
            Type direct = Type.GetType(typeName + ", Assembly-CSharp");
            if (direct != null) return direct;
            direct = Type.GetType(typeName);
            if (direct != null) return direct;
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type candidate = assembly.GetType(typeName);
                    if (candidate != null) return candidate;
                }
                catch
                {
                    // 个别动态程序集 GetType 可能抛异常，忽略继续。
                }
            }
            return null;
        }

        /// <summary>素材放置说明全文。约定文件名与各运行时系统实现严格一致。</summary>
        static string BuildReadmeText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("《一骑红尘：荔枝争运战》回放素材放置说明");
            sb.AppendLine("==========================================");
            sb.AppendLine("本目录 (Assets/StreamingAssets/) 下的素材在回放运行时按需加载，全部为可选项：");
            sb.AppendLine("文件缺失时各系统自动使用程序化兜底效果，不会报错、不影响回放本身。");
            sb.AppendLine();
            sb.AppendLine("【回放数据 replay.txt】");
            sb.AppendLine("  逐行 JSON：1 行 start + 600 行 round + 1 行 over。");
            sb.AppendLine("  当前文件为演示回放（复制自 Assets/LycheeReplayDemo/Data/reply_full_coverage_unity_v2.txt）。");
            sb.AppendLine("  正式比赛把服务端产出的 replay.txt 放本目录替换同名文件即可，无需改动工程。");
            sb.AppendLine();
            sb.AppendLine("【Audio/BGM/】背景音乐（LycheeAudioDirector；同名文件依次尝试 .ogg / .wav / .mp3）");
            sb.AppendLine("  bgm_normal       常规阶段背景乐");
            sb.AppendLine("  bgm_rush         抢运阶段背景乐（RUSH_PHASE_START 后切入）");
            sb.AppendLine("  bgm_settlement   终局结算背景乐");
            sb.AppendLine();
            sb.AppendLine("【Audio/SFX/】音效（LycheeAudioDirector；同名文件依次尝试 .ogg / .wav / .mp3）");
            sb.AppendLine("  sfx_card_reveal      窗口翻牌 (WINDOW_CARD_REVEAL)");
            sb.AppendLine("  sfx_contest_win      争夺结算 (WINDOW_CONTEST_END)");
            sb.AppendLine("  sfx_guard_set        设卡 (GUARD_SET)");
            sb.AppendLine("  sfx_guard_break      破卡 (GUARD_BREAK)");
            sb.AppendLine("  sfx_squad_dispatch   小队派遣 (SQUAD_DISPATCH)");
            sb.AppendLine("  sfx_squad_arrive     小队抵达 (SQUAD_ARRIVE)");
            sb.AppendLine("  sfx_task_spawn       任务/悬赏出现 (TASK_SPAWN / BOUNTY_SPAWN)");
            sb.AppendLine("  sfx_task_complete    任务/悬赏完成 (TASK_COMPLETE / BOUNTY_COMPLETE)");
            sb.AppendLine("  sfx_verify_gate      验货完成 (VERIFY_GATE_COMPLETE)");
            sb.AppendLine("  sfx_deliver          荔枝送达 (DELIVER_SUCCESS)");
            sb.AppendLine("  sfx_rush_horn        抢运号角 (RUSH_PHASE_START)");
            sb.AppendLine("  sfx_fresh_warning    鲜度告警 (GOOD_TO_BAD / FRESHNESS_DROP)");
            sb.AppendLine("  sfx_ui_click         UI 点击（LycheeAudioDirector.PlayUiClick 静态入口）");
            sb.AppendLine("  sfx_weather_rain     暴雨环境循环");
            sb.AppendLine("  sfx_weather_wind     大风/浓雾环境循环");
            sb.AppendLine("  sfx_weather_heat     酷热环境循环");
            sb.AppendLine("  sfx_hoofbeats        马蹄声移动循环（陆路行进）");
            sb.AppendLine("  sfx_water_row        划水声移动循环（水路行进）");
            sb.AppendLine();
            sb.AppendLine("【UITheme/】主题贴图（.png，缺失走程序化兜底）");
            sb.AppendLine("  weather_rain.png             暴雨粒子贴图 (LycheeWeatherFxSystem)");
            sb.AppendLine("  weather_fog.png              浓雾粒子贴图 (LycheeWeatherFxSystem)");
            sb.AppendLine("  weather_heat.png             热浪粒子贴图 (LycheeWeatherFxSystem)");
            sb.AppendLine("  convoy_dust.png              车队烟尘粒子贴图 (LycheeConvoyVfx)");
            sb.AppendLine("  minimap_frame.png            小地图边框 (LycheeMinimapSystem)");
            sb.AppendLine("  rush_banner.png              抢运横幅底图 (LycheeHudExtensions)");
            sb.AppendLine("  icon_weather_hot.png         酷热天气图标 (LycheeHudExtensions)");
            sb.AppendLine("  icon_weather_rain.png        暴雨天气图标 (LycheeHudExtensions)");
            sb.AppendLine("  icon_weather_fog.png         浓雾天气图标 (LycheeHudExtensions)");
            sb.AppendLine("  settlement_bg.png            结算面板背景 (LycheeSettlementScreen)");
            sb.AppendLine("  victory_banner.png           胜利横幅 (LycheeSettlementScreen)");
            sb.AppendLine("  icon_resource_icebox.png     冰鉴资源图标 ICE_BOX (LycheeWorldMarkerSystem)");
            sb.AppendLine("  icon_resource_fasthorse.png  快马资源图标 FAST_HORSE (LycheeWorldMarkerSystem)");
            sb.AppendLine("  icon_resource_shorthorse.png 短途马资源图标 SHORT_HORSE (LycheeWorldMarkerSystem)");
            sb.AppendLine("  icon_resource_passtoken.png  通行令资源图标 PASS_TOKEN (LycheeWorldMarkerSystem)");
            sb.AppendLine("  icon_resource_permit.png     官府文牒资源图标 OFFICIAL_PERMIT (LycheeWorldMarkerSystem)");
            sb.AppendLine("  icon_resource_intel.png      情报资源图标 INTEL (LycheeWorldMarkerSystem)");
            sb.AppendLine("  icon_resource_boatright.png  舟船权资源图标 BOAT_RIGHT (LycheeWorldMarkerSystem)");
            sb.AppendLine();
            sb.AppendLine("【Video/】预留目录");
            sb.AppendLine("  当前版本运行时系统暂不读取视频素材，目录为后续版本预留。");
            sb.AppendLine();
            sb.AppendLine("生成于 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                          "（Tools/Lychee/Replay Upgrade 20260612/Setup，可重复执行刷新本说明）");
            return sb.ToString();
        }
    }

    [InitializeOnLoad]
    internal static class LycheeReplayUpgradeBuilder20260612AutoRun
    {
        static LycheeReplayUpgradeBuilder20260612AutoRun()
        {
            EditorApplication.delayCall += TryRun;
        }

        static void TryRun()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (File.Exists(Path.Combine(root, LycheeReplayUpgradeBuilder20260612.DoneFile))) return;
            if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.delayCall += TryRun;
                return;
            }
            LycheeReplayUpgradeBuilder20260612.Run(false);
        }
    }
}
