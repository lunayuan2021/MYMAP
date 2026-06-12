using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace LycheeReplayDemoTools
{
    /// 把 Game 视图固定为 4K (3840x2160) 分辨率并设置 1.2x 显示缩放；同时把打包默认分辨率设为 4K。
    /// GameView 尺寸列表与缩放没有公开 API，这里通过反射操作内部类型，全部 try/catch，失败只记录不报错。
    [InitializeOnLoad]
    internal static class LycheeGameView4K20260612
    {
        const string MarkerFileName = ".lychee_gameview_4k_20260612.done";
        const string ReportFileName = "LycheeGameView4K_20260612_report.txt";
        const int TargetWidth = 3840;
        const int TargetHeight = 2160;
        // 整帧适配（SnapZoom 会向下钳制到最小缩放=完整显示）。
        // 注意：不要用 Scale>fit 做"画面放大"——那是对渲染帧的裁切，会把 ScreenSpaceOverlay 的 HUD 裁出画面；
        // 1.2x 画面放大改由 LycheeReplayCameraController.perspectiveZoomMultiplier（镜头拉近）实现。
        const float TargetZoom = 0.05f;

        static LycheeGameView4K20260612()
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
        }

        [MenuItem("Tools/Lychee/Replay Upgrade 20260612/Game View 4K x1.2")]
        static void ApplyFromMenu()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string report = Apply();
            File.WriteAllText(Path.Combine(projectRoot, ReportFileName), report);
            Debug.Log(report);
        }

        static string Apply()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("LycheeGameView4K_20260612 report");

            try
            {
                PlayerSettings.defaultScreenWidth = TargetWidth;
                PlayerSettings.defaultScreenHeight = TargetHeight;
                sb.AppendLine("打包默认分辨率 = " + TargetWidth + "x" + TargetHeight);
            }
            catch (Exception e)
            {
                sb.AppendLine("打包分辨率设置失败: " + e.Message);
            }

            try
            {
                Assembly editorAssembly = typeof(EditorWindow).Assembly;
                Type gameViewType = editorAssembly.GetType("UnityEditor.GameView");
                Type sizesType = editorAssembly.GetType("UnityEditor.GameViewSizes");
                Type sizeType = editorAssembly.GetType("UnityEditor.GameViewSize");
                Type sizeTypeEnum = editorAssembly.GetType("UnityEditor.GameViewSizeType");
                if (gameViewType == null || sizesType == null || sizeType == null || sizeTypeEnum == null)
                {
                    sb.AppendLine("FAILED: 反射类型缺失（Unity 内部 API 变更）");
                    return sb.ToString();
                }

                Type singletonType = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
                object sizesInstance = singletonType
                    .GetProperty("instance", BindingFlags.Public | BindingFlags.Static)
                    .GetValue(null);

                object groupType = sizesType
                    .GetProperty("currentGroupType", BindingFlags.Public | BindingFlags.Instance)
                    .GetValue(sizesInstance);
                object group = sizesType
                    .GetMethod("GetGroup", BindingFlags.Public | BindingFlags.Instance)
                    .Invoke(sizesInstance, new[] { groupType });

                int totalCount = (int)group.GetType()
                    .GetMethod("GetTotalCount", BindingFlags.Public | BindingFlags.Instance)
                    .Invoke(group, null);
                MethodInfo getSize = group.GetType()
                    .GetMethod("GetGameViewSize", BindingFlags.Public | BindingFlags.Instance);
                PropertyInfo widthProp = sizeType.GetProperty("width", BindingFlags.Public | BindingFlags.Instance);
                PropertyInfo heightProp = sizeType.GetProperty("height", BindingFlags.Public | BindingFlags.Instance);

                int targetIndex = -1;
                for (int i = 0; i < totalCount; i++)
                {
                    object candidate = getSize.Invoke(group, new object[] { i });
                    if ((int)widthProp.GetValue(candidate) == TargetWidth &&
                        (int)heightProp.GetValue(candidate) == TargetHeight)
                    {
                        targetIndex = i;
                        break;
                    }
                }

                if (targetIndex < 0)
                {
                    object fixedRes = Enum.Parse(sizeTypeEnum, "FixedResolution");
                    object newSize = Activator.CreateInstance(sizeType, fixedRes, TargetWidth, TargetHeight, "4K 荔枝回放");
                    group.GetType()
                        .GetMethod("AddCustomSize", BindingFlags.Public | BindingFlags.Instance)
                        .Invoke(group, new[] { newSize });
                    sizesType.GetMethod("SaveToHDD", BindingFlags.Public | BindingFlags.Instance)
                        ?.Invoke(sizesInstance, null);
                    targetIndex = totalCount; // 新增项追加在末尾
                    sb.AppendLine("已添加自定义尺寸 4K 荔枝回放 (" + TargetWidth + "x" + TargetHeight + ")");
                }
                else
                {
                    sb.AppendLine("已存在 4K 尺寸，索引 " + targetIndex);
                }

                EditorWindow gameView = EditorWindow.GetWindow(gameViewType, false, null, true);
                PropertyInfo selectedIndex = gameViewType.GetProperty(
                    "selectedSizeIndex",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                selectedIndex.SetValue(gameView, targetIndex, null);
                sb.AppendLine("Game 视图已切换到固定 4K 分辨率");

                MethodInfo snapZoom = gameViewType.GetMethod(
                    "SnapZoom",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (snapZoom != null)
                {
                    snapZoom.Invoke(gameView, new object[] { TargetZoom });
                    sb.AppendLine("显示缩放 = 整帧适配（HUD 完整可见；画面 1.2x 放大由相机拉近实现）");
                }
                else
                {
                    sb.AppendLine("SnapZoom 不可用，请把 Game 视图 Scale 滑杆拉到最左（fit）");
                }
                gameView.Repaint();
            }
            catch (Exception e)
            {
                sb.AppendLine("GameView 反射操作失败: " + e.Message);
            }
            return sb.ToString();
        }
    }
}
