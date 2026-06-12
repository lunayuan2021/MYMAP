using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LycheeReplayDemoTools
{
    internal static class LycheeReplayPresentationFix20260612A
    {
        const string ScenePath = "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity";
        const string DoneFile = "LycheeReplayPresentationFix_20260612A.done";
        const string ReportFile = "LycheeReplayPresentationFix_20260612A_report.txt";

        static readonly string[] CombatSprites =
        {
            "Assets/LycheeReplayDemo/UI/CombatIllustrations/combat_clash_20260612.png",
            "Assets/LycheeReplayDemo/UI/CombatIllustrations/combat_strategy_20260612.png",
            "Assets/LycheeReplayDemo/UI/CombatIllustrations/combat_result_20260612.png"
        };

        [MenuItem("Tools/Lychee/Replay Presentation Fix 20260612A")]
        public static void RunMenu()
        {
            Run(true);
        }

        [MenuItem("Tools/Lychee/Replay Verify/Start Play 20260612A")]
        public static void StartPlayMenu()
        {
            if (SceneManager.GetActiveScene().path != ScenePath)
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            AssetDatabase.Refresh();
            Debug.Log("Lychee replay verification: requesting Play Mode start.");
            EditorApplication.isPlaying = true;
        }

        [MenuItem("Tools/Lychee/Replay Verify/Stop Play 20260612A")]
        public static void StopPlayMenu()
        {
            Debug.Log("Lychee replay verification: requesting Play Mode stop.");
            EditorApplication.isPlaying = false;
        }

        public static void Run(bool force)
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string donePath = Path.Combine(root, DoneFile);
            string reportPath = Path.Combine(root, ReportFile);
            if (!force && File.Exists(donePath)) return;

            StringBuilder report = new StringBuilder();
            report.AppendLine("LycheeReplayPresentationFix_20260612A report");

            try
            {
                if (SceneManager.GetActiveScene().path != ScenePath)
                    EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

                AssetDatabase.Refresh();
                Sprite[] combat = CombatSprites.Select(path => ImportSprite(path, report)).Where(sprite => sprite != null).ToArray();
                Sprite redAvatar = ImportSprite("Assets/LycheeReplayDemo/UI/Avatars/red_captain_circle_20260611Y.png", report);
                Sprite blueAvatar = ImportSprite("Assets/LycheeReplayDemo/UI/Avatars/blue_captain_circle_20260611Y.png", report);

                LycheeReplayPlayer player = Object.FindFirstObjectByType<LycheeReplayPlayer>();
                if (player == null)
                    throw new System.InvalidOperationException("LycheeReplayPlayer not found in " + ScenePath);

                player.useRuntimeHud = true;
                player.showLegacyTopHud = false;
                player.defaultTeamSize = 16;
                player.defaultDispatchMemberCount = 4;
                player.combatIllustrations = combat;
                player.redCaptainAvatar = redAvatar;
                player.blueCaptainAvatar = blueAvatar;
                EditorUtility.SetDirty(player);

                LycheeReplayRuntimeHud runtimeHud = player.GetComponent<LycheeReplayRuntimeHud>();
                if (runtimeHud == null)
                    runtimeHud = player.gameObject.AddComponent<LycheeReplayRuntimeHud>();
                runtimeHud.previewInEditMode = true;
                runtimeHud.previewCombatIllustrations = combat;
                runtimeHud.previewRedAvatar = redAvatar;
                runtimeHud.previewBlueAvatar = blueAvatar;
                runtimeHud.ShowEditorPreview();
                EditorUtility.SetDirty(runtimeHud);

                LycheeReplayCameraController cameraController = Object.FindFirstObjectByType<LycheeReplayCameraController>();
                if (cameraController != null)
                {
                    cameraController.replay = player;
                    cameraController.minimumSize = 7.8f;
                    cameraController.maximumSize = 18f;
                    cameraController.eventSize = 6.8f;
                    cameraController.framingPadding = 6f;
                    cameraController.cameraDistance = 58f;
                    cameraController.replayZoomMultiplier = 1.2f;
                    cameraController.lockObliqueAngle = true;
                    cameraController.obliqueEuler = new Vector3(52f, -29f, 0f);
                    cameraController.SnapToCurrentFocus();
                    EditorUtility.SetDirty(cameraController);
                }

                int disabled = 0;
                foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (go == null || !go.scene.IsValid() || go.scene.path != ScenePath) continue;
                    if (go.name == "BattleHUD_BottomStatus_20260611Y" || go.name == "BattleHUD_RedBlueStatus_20260611X")
                    {
                        go.SetActive(false);
                        EditorUtility.SetDirty(go);
                        disabled++;
                    }
                }

                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
                AssetDatabase.SaveAssets();

                report.AppendLine("combatSprites=" + combat.Length);
                report.AppendLine("redAvatar=" + (redAvatar != null));
                report.AppendLine("blueAvatar=" + (blueAvatar != null));
                report.AppendLine("disabledOldHud=" + disabled);
                report.AppendLine("editModePreviewHud=true");
                report.AppendLine("runtimeHud=true, defaultTeamSize=16, defaultDispatchMemberCount=4");
                report.AppendLine("eventIllustrations=key replay events");
                report.AppendLine("cameraZoomMultiplier=1.2, obliqueAngle=52,-29,0");
                File.WriteAllText(reportPath, report.ToString());
                File.WriteAllText(donePath, System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Debug.Log(report.ToString());
            }
            catch (System.Exception ex)
            {
                report.AppendLine("ERROR: " + ex);
                File.WriteAllText(reportPath, report.ToString());
                Debug.LogError(ex);
            }
        }

        static Sprite ImportSprite(string path, StringBuilder report)
        {
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                report.AppendLine("missing texture: " + path);
                return null;
            }

            bool dirty = false;
            if (importer.textureType != TextureImporterType.Sprite)
            {
                importer.textureType = TextureImporterType.Sprite;
                dirty = true;
            }
            if (importer.alphaIsTransparency == false)
            {
                importer.alphaIsTransparency = true;
                dirty = true;
            }
            if (importer.mipmapEnabled)
            {
                importer.mipmapEnabled = false;
                dirty = true;
            }
            importer.spriteImportMode = SpriteImportMode.Single;
            if (dirty) importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }
    }

    [InitializeOnLoad]
    internal static class LycheeReplayPresentationFix20260612AAutoRun
    {
        static LycheeReplayPresentationFix20260612AAutoRun()
        {
            EditorApplication.delayCall += TryRun;
        }

        static void TryRun()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (File.Exists(Path.Combine(root, "LycheeReplayPresentationFix_20260612A.done"))) return;
            if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode) return;
            LycheeReplayPresentationFix20260612A.Run(false);
        }
    }
}
