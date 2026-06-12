using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif
using UnityEngine.SceneManagement;

namespace LycheeReplayDemoTools
{
    internal static class LycheeMapFix20260611ZInputSystemUI
    {
        const string ScenePath = "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity";
        const string DoneFile = "LycheeMapFix_20260611Z.done";
        const string ReportFile = "LycheeMapFix_20260611Z_report.txt";

        public static void Run()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string reportPath = Path.Combine(root, ReportFile);
            string donePath = Path.Combine(root, DoneFile);

            try
            {
                if (SceneManager.GetActiveScene().path != ScenePath)
                {
                    EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                }

                int removed = 0;
                int added = 0;
                var eventSystems = Resources.FindObjectsOfTypeAll<EventSystem>();
                EventSystem primary = null;

                foreach (var eventSystem in eventSystems)
                {
                    if (eventSystem == null || !eventSystem.gameObject.scene.IsValid()) continue;
                    if (eventSystem.gameObject.scene.path != ScenePath) continue;
                    if (primary == null) primary = eventSystem;

                    var legacyModules = eventSystem.GetComponents<StandaloneInputModule>();
                    foreach (var module in legacyModules)
                    {
                        Object.DestroyImmediate(module, true);
                        removed++;
                    }
                }

                if (primary == null)
                {
                    var go = new GameObject("EventSystem", typeof(EventSystem));
                    primary = go.GetComponent<EventSystem>();
                    added++;
                }

#if ENABLE_INPUT_SYSTEM
                if (primary.GetComponent<InputSystemUIInputModule>() == null)
                {
                    primary.gameObject.AddComponent<InputSystemUIInputModule>();
                    added++;
                }
#endif

                EditorUtility.SetDirty(primary.gameObject);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
                AssetDatabase.SaveAssets();

                string report = "LycheeMapFix_20260611Z InputSystem UI report\n"
                    + "removedStandaloneInputModules=" + removed + "\n"
                    + "addedOrCreatedInputSystemUI=" + added + "\n";
                File.WriteAllText(reportPath, report);
                File.WriteAllText(donePath, System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Debug.Log(report.TrimEnd());
            }
            catch (System.Exception ex)
            {
                File.WriteAllText(reportPath, "LycheeMapFix_20260611Z failed\n" + ex);
                Debug.LogException(ex);
            }
        }
    }

    [InitializeOnLoad]
    internal static class LycheeMapFix20260611ZInputSystemUIAutoRun
    {
        static LycheeMapFix20260611ZInputSystemUIAutoRun()
        {
            EditorApplication.delayCall += TryRun;
        }

        static void TryRun()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (File.Exists(Path.Combine(root, "LycheeMapFix_20260611Z.done"))) return;
            if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode) return;
            LycheeMapFix20260611ZInputSystemUI.Run();
        }
    }
}
