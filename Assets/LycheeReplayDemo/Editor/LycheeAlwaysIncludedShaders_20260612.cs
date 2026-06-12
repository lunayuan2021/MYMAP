using System.IO;
using UnityEditor;
using UnityEngine;

namespace LycheeReplayDemoTools
{
    /// 运行时系统通过 Shader.Find 创建材质（天空盒/粒子/光环），这些 shader 若无资产引用会在打包时被裁剪。
    /// 本脚本把它们登记进 Graphics Settings 的 Always Included Shaders，保证打包后效果不丢。
    [InitializeOnLoad]
    internal static class LycheeAlwaysIncludedShaders20260612
    {
        const string MarkerFileName = ".lychee_always_included_shaders_20260612.done";
        const string ReportFileName = "LycheeAlwaysIncludedShaders_20260612_report.txt";

        static readonly string[] RequiredShaders =
        {
            "Skybox/Procedural",
            "Universal Render Pipeline/Particles/Unlit",
            "Universal Render Pipeline/Unlit",
        };

        static LycheeAlwaysIncludedShaders20260612()
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

        [MenuItem("Tools/Lychee/Replay Upgrade 20260612/Ensure Always Included Shaders")]
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
            sb.AppendLine("LycheeAlwaysIncludedShaders_20260612 report");

            Object[] settingsAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (settingsAssets == null || settingsAssets.Length == 0)
            {
                sb.AppendLine("FAILED: GraphicsSettings.asset 不可加载");
                return sb.ToString();
            }

            var serialized = new SerializedObject(settingsAssets[0]);
            SerializedProperty list = serialized.FindProperty("m_AlwaysIncludedShaders");
            if (list == null || !list.isArray)
            {
                sb.AppendLine("FAILED: 未找到 m_AlwaysIncludedShaders 属性");
                return sb.ToString();
            }

            foreach (string shaderName in RequiredShaders)
            {
                Shader shader = Shader.Find(shaderName);
                if (shader == null)
                {
                    sb.AppendLine("SKIP（编辑器中找不到 shader）: " + shaderName);
                    continue;
                }

                bool present = false;
                for (int i = 0; i < list.arraySize; i++)
                {
                    if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader)
                    {
                        present = true;
                        break;
                    }
                }
                if (present)
                {
                    sb.AppendLine("已存在: " + shaderName);
                    continue;
                }

                int index = list.arraySize;
                list.InsertArrayElementAtIndex(index);
                list.GetArrayElementAtIndex(index).objectReferenceValue = shader;
                sb.AppendLine("已加入: " + shaderName);
            }

            serialized.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
            sb.AppendLine("完成。打包前无需再手动处理 Always Included Shaders。");
            return sb.ToString();
        }
    }
}
