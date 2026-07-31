using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 文件驱动入口（绕过 MCP，20260613）：把地图生成做成工程惯用的 .request/.done 模式，
/// 这样即使 unity-mcp 连接反复掉线，也能用命令行写请求文件来驱动生成 + 出图，全程不依赖 MCP。
///
/// 用法：在工程根目录写 `.lychee_maplayout.request`（JSON），编辑器聚焦/下一个 update tick 自动执行：
///   单图： {"mode":"single","config":"/abs/or/rel.json","outScene":"Assets/Scenes/GeneratedMaps/Map_x.unity",
///           "mapKey":"map_x","replay":"Tournament/x.txt","baseline":"Assets/LycheeMap15/Configs/Maps/ui_map_config_unity_full_coverage_v2.json",
///           "screenshot":true,"shotCenterX":0,"shotCenterZ":0,"shotSize":0}
///   锦标赛：{"mode":"tournament","configs":["/a.json","/b.json",...],"screenshot":true}
/// 执行后写 `.lychee_maplayout.done`（含 OK/ERR），并渲染两张图到根目录：
///   LycheeMapLayout_iso.png（2.5D 斜视全图）、LycheeMapLayout_top.png（俯视，可按 shotCenter/Size 聚焦）。
/// shotSize=0 表示俯视框整图（80x60）。
///
/// 模式与 LycheeWebGLBuild/PlaymodeCapture 一致：InitializeOnLoad + EditorApplication.update 轮询 + 时间节流
/// （不用 delayCall，域重载会丢）。请求一旦读到立刻删除，避免重复执行；done 由本次运行覆盖写。
/// </summary>
[InitializeOnLoad]
static class LycheeMapLayoutRunner_20260613
{
    const string RequestFile = ".lychee_maplayout.request";
    const string DoneFile = ".lychee_maplayout.done";
    const string IsoShot = "LycheeMapLayout_iso.png";
    const string TopShot = "LycheeMapLayout_top.png";
    const string DefaultBaseline = "Assets/LycheeMap15/Configs/Maps/ui_map_config_unity_full_coverage_v2.json";

    static double next;

    [Serializable]
    class Req
    {
        public string mode = "single";
        public string config;
        public string outScene;
        public string mapKey;
        public string replay;
        public string baseline;
        public string[] configs;
        public bool screenshot = true;
        public float shotCenterX;
        public float shotCenterZ;
        public float shotSize; // 0 = 整图俯视
        public bool enhanceBoundary; // 先在母版跑边界柔化（再生成各图克隆即继承）
    }

    static LycheeMapLayoutRunner_20260613()
    {
        EditorApplication.update += Poll;
    }

    static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    static string ReqPath => Path.Combine(Root, RequestFile);
    static string DonePath => Path.Combine(Root, DoneFile);

    static void Poll()
    {
        if (EditorApplication.timeSinceStartup < next) return;
        next = EditorApplication.timeSinceStartup + 1.0;
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying ||
            EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        try { if (!File.Exists(ReqPath)) return; }
        catch (Exception) { return; }
        Run();
    }

    static void Run()
    {
        string status = "OK";
        Req req = null;
        try
        {
            req = JsonUtility.FromJson<Req>(File.ReadAllText(ReqPath));
            File.Delete(ReqPath); // 先删，避免下一 tick 重跑
        }
        catch (Exception e)
        {
            TryDelete(ReqPath);
            WriteDone("ERR(parse): " + e.Message);
            return;
        }

        try
        {
            // 先在母版上跑边界柔化（再生成的各图克隆母版即继承）。
            if (req.enhanceBoundary)
            {
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                    "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity",
                    UnityEditor.SceneManagement.OpenSceneMode.Single);
                LycheeBoundaryEnhance_20260614.ApplyToActiveScene();
            }

            if (req.mode == "tournament" && req.configs != null && req.configs.Length > 0)
            {
                LycheeMapLayoutGenerator_20260613.BuildTournament(req.configs);
            }
            else
            {
                string baseline = string.IsNullOrEmpty(req.baseline) ? DefaultBaseline : req.baseline;
                bool ok = LycheeMapLayoutGenerator_20260613.ApplyLayout(
                    req.config, req.outScene, req.mapKey, req.replay, baseline);
                if (!ok) status = "ApplyLayout returned false (见 report)";
            }

            if (req.screenshot)
            {
                try { CaptureShots(req); }
                catch (Exception e) { status += " | shotErr: " + e.Message; }
            }
        }
        catch (Exception e)
        {
            status = "ERR: " + e.Message;
            Debug.LogError("[MapLayoutRunner] " + e);
        }
        WriteDone(status);
    }

    static void CaptureShots(Req req)
    {
        bool focus = req.shotSize > 0.01f;
        Vector3 c = focus ? new Vector3(req.shotCenterX, 0f, req.shotCenterZ) : Vector3.zero;

        // 斜视（euler(52,-29,0) ortho）：focus 时框聚焦区（能看出河道凹陷/路面贴地），否则框整图。
        Quaternion isoRot = Quaternion.Euler(52f, -29f, 0f);
        float isoSize = focus ? req.shotSize * 1.15f : Mathf.Max(60f * 0.63f, 80f * 0.43f);
        Vector3 isoPos = focus ? c - (isoRot * Vector3.forward) * 160f
                               : new Vector3(80f * 0.42f, 60f * 1.2f, -60f * 0.95f);
        CaptureCamera(isoPos, isoRot, true, isoSize, 1600, 1000, IsoShot);

        // 俯视：focus 框聚焦区，否则框整图（80x60）。
        CaptureCamera(new Vector3(c.x, 120f, c.z), Quaternion.Euler(90f, 0f, 0f),
            true, focus ? req.shotSize : 31f, 1280, 1280, TopShot);
    }

    static void CaptureCamera(Vector3 pos, Quaternion rot, bool ortho, float orthoSize, int width, int height, string file)
    {
        var go = new GameObject("__LycheeShotCam");
        go.hideFlags = HideFlags.HideAndDontSave;
        var cam = go.AddComponent<Camera>();
        cam.orthographic = ortho;
        cam.orthographicSize = orthoSize;
        cam.transform.position = pos;
        cam.transform.rotation = rot;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 600f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.55f, 0.62f, 0.66f);

        var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        var prevActive = RenderTexture.active;
        var prevTarget = cam.targetTexture;
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        tex.Apply();
        File.WriteAllBytes(Path.Combine(Root, file), tex.EncodeToPNG());

        cam.targetTexture = prevTarget;
        RenderTexture.active = prevActive;
        UnityEngine.Object.DestroyImmediate(tex);
        UnityEngine.Object.DestroyImmediate(rt);
        UnityEngine.Object.DestroyImmediate(go);
    }

    static void WriteDone(string status)
    {
        try { File.WriteAllText(DonePath, status + "  @ " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")); }
        catch (Exception e) { Debug.LogWarning("[MapLayoutRunner] 写 done 失败: " + e.Message); }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }

    [MenuItem("Tools/Lychee/MultiMap/Run Pending Request Now")]
    static void RunNow() { next = 0; if (File.Exists(ReqPath)) Run(); }
}
