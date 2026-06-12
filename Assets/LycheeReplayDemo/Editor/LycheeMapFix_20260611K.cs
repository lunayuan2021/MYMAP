// 第十轮修复（2026-06-11 批次 K）：后期处理 Profile（Bloom+暗角+暖色），主相机开启后期
using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace LycheeReplayDemoTools
{
    internal static class LycheeMapFix20260611K
    {
        static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string MarkerPath => Path.Combine(Root, "LycheeMapFix_20260611K.done");
        static string ReportPath => Path.Combine(Root, "LycheeMapFix_20260611K_report.txt");
        const string ProfilePath = "Assets/LycheeMap15/LycheePostFX_20260611K.asset";

        [MenuItem("Tools/Lychee/修复地图 20260611K（后期Bloom暖色）")]
        public static void RunMenu() { Run(true); }

        public static void Run(bool force)
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.name != "LycheeReplayDemo") return;
            if (!force && File.Exists(MarkerPath)) return;
            var report = new StringBuilder();
            report.AppendLine("LycheeMapFix_20260611K 报告 " + DateTime.Now.ToString("HH:mm:ss"));
            try
            {
                var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
                if (profile == null)
                {
                    profile = ScriptableObject.CreateInstance<VolumeProfile>();
                    AssetDatabase.CreateAsset(profile, ProfilePath);
                }
                Bloom bloom;
                if (!profile.TryGet(out bloom)) bloom = profile.Add<Bloom>(true);
                bloom.active = true;
                bloom.intensity.Override(0.8f);
                bloom.threshold.Override(0.9f);
                bloom.scatter.Override(0.6f);

                Vignette vig;
                if (!profile.TryGet(out vig)) vig = profile.Add<Vignette>(true);
                vig.active = true;
                vig.intensity.Override(0.22f);
                vig.smoothness.Override(0.4f);

                ColorAdjustments ca;
                if (!profile.TryGet(out ca)) ca = profile.Add<ColorAdjustments>(true);
                ca.active = true;
                ca.saturation.Override(10f);
                ca.postExposure.Override(0.12f);

                WhiteBalance wb;
                if (!profile.TryGet(out wb)) wb = profile.Add<WhiteBalance>(true);
                wb.active = true;
                wb.temperature.Override(8f);
                EditorUtility.SetDirty(profile);
                report.AppendLine("Profile: Bloom0.8 Vignette0.22 Sat+10 Temp+8");

                var roots = scene.GetRootGameObjects();
                Transform ppT = null, camT = null;
                foreach (var r in roots)
                {
                    if (r == null) continue;
                    foreach (var t in r.GetComponentsInChildren<Transform>(true))
                    {
                        if (t.name == "Global_PostProcessing") ppT = t;
                        if (t.name == "MainCamera_2.5D") camT = t;
                    }
                }
                if (ppT != null)
                {
                    var vol = ppT.GetComponent<Volume>();
                    if (vol == null) vol = ppT.gameObject.AddComponent<Volume>();
                    vol.isGlobal = true;
                    vol.sharedProfile = profile;
                    report.AppendLine("Volume 已绑定 Profile");
                }
                if (camT != null)
                {
                    var cam = camT.GetComponent<Camera>();
                    var data = cam.GetUniversalAdditionalCameraData();
                    data.renderPostProcessing = true;
                    report.AppendLine("主相机已开启后期");
                }

                File.WriteAllText(MarkerPath, DateTime.Now.ToString("o"));
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                AssetDatabase.SaveAssets();
                report.AppendLine("场景已保存。");
                File.WriteAllText(Path.Combine(Root, "LycheeMapCapture_20260611.request"), DateTime.Now.ToString("o"));
                LycheeMapCapture20260611.Capture();
            }
            catch (Exception ex)
            {
                report.AppendLine("异常: " + ex);
                Debug.LogError("[LycheeFixK] " + ex);
            }
            finally
            {
                File.WriteAllText(ReportPath, report.ToString());
            }
        }
    }

    [InitializeOnLoad]
    internal static class LycheeMapFix20260611KAutoRun
    {
        static LycheeMapFix20260611KAutoRun()
        {
            EditorApplication.delayCall += () => EditorApplication.delayCall += TryRun;
        }

        static void TryRun()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (File.Exists(Path.Combine(root, "LycheeMapFix_20260611K.done"))) return;
            if (!File.Exists(Path.Combine(root, "LycheeMapFix_20260611J.done"))) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            if (SceneManager.GetActiveScene().name != "LycheeReplayDemo") return;
            LycheeMapFix20260611K.Run(false);
        }
    }
}
