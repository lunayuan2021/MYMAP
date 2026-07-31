using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 一次性白天提亮（20260613）：场景切到明亮白天光后，道路路面与驿站建筑仍然过暗。
/// 侦察根因：
///  ① 道路：RoadGroundFit 网格用的是 6 个工程材质资产（M_Config_OfficialRoad 等，URP/Lit，
///     metallic 均为 0），暗的主因是贴图本身偏暗（ALP Ground 系列 sRGB 均亮仅 0.30 左右），
///     且 Border 系材质 _BaseColor tint 压得很低（如山道路缘 0.12,0.15,0.12）。
///     → 直接把这 6 个材质 _BaseColor 乘 1.4（原值写报告可回滚，材质打 tag 防重复提亮）。
///  ② 驿站：Stations/*.glb 每个含 1 个 glTFast 子资产材质（只读），baseColorFactor=1、
///     无 emissive 异常、metallicRoughness 贴图 B 通道≈0.06~0.10（metallic 无碍）；
///     暗的主因同样是 baseColor 贴图过暗（sRGB 均亮 0.18~0.32）。GLB 里的
///     KHR_materials_specular [2,2,2] 在 glTFast 6.19 的 ShaderGraph 生成器里并未落地，
///     但 specularFactor 属性仍做防御性归一。
///     → 为每个唯一源材质建提亮副本（baseColor 乘 1.4，因源 factor=1、暗在贴图，
///       乘数必须>1 才有效，钳到 1.45），存 StationBright_20260613/，替换 renderer 引用。
///  ③ 全局：环境光 equator/ground 各抬一档，侧面/底面不再死黑。
/// 模式与 LycheeStationLift_20260613 一致：update 轮询 + done 标记 + 报告 + 磁盘重载。
/// </summary>
[InitializeOnLoad]
static class LycheeBrightnessFix_20260613
{
    const string ScenePath = "Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity";
    const string DoneFileName = ".lychee_brightness_fix_20260613.done";
    const string ReportFileName = "LycheeBrightnessFix_20260613_report.txt";
    const string StationMatFolderParent = "Assets/LycheeMap15/Materials";
    const string StationMatFolderName = "StationBright_20260613";
    const string StationMatFolder = StationMatFolderParent + "/" + StationMatFolderName;
    const string RoadDoneTag = "LycheeBright20260613"; // 材质 tag，防 rerun 重复乘亮

    const float RoadMul = 1.4f;        // 道路 _BaseColor 提亮倍数（1.3~1.5 区间取中上）
    const float RoadChannelCap = 1.8f; // 道路 tint 单通道上限（官道原本就 1.08，乘后 1.51）
    const float StationMul = 1.4f;     // 驿站 baseColor 提亮倍数
    const float StationChannelCap = 1.45f; // 源 factor=1，乘后 1.4，钳 1.45 防过曝
    const float MetallicCap = 0.2f;    // metallic 压到 0~0.2

    static double nextPollTime;

    static LycheeBrightnessFix_20260613()
    {
        EditorApplication.update += Poll;
    }

    static string ProjectRoot
    {
        get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..")); }
    }

    static string DonePath { get { return Path.Combine(ProjectRoot, DoneFileName); } }
    static string ReportPath { get { return Path.Combine(ProjectRoot, ReportFileName); } }

    static void Poll()
    {
        if (EditorApplication.timeSinceStartup < nextPollTime) return;
        nextPollTime = EditorApplication.timeSinceStartup + 2.0;
        try
        {
            if (File.Exists(DonePath)) return;
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
        Run();
    }

    static string ColorStr(Color c)
    {
        return string.Format("({0:F3},{1:F3},{2:F3})", c.r, c.g, c.b);
    }

    static void Run()
    {
        var report = new List<string>();
        report.Add("LycheeBrightnessFix_20260613 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        try
        {
            Scene active = SceneManager.GetActiveScene();
            if (!string.Equals(active.path, ScenePath, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(active.path) && active.isDirty)
            {
                EditorSceneManager.SaveOpenScenes();
            }
            active = EditorSceneManager.OpenScene(ScenePath);
            report.Add("已从磁盘重载场景: " + ScenePath);

            // ---------- ① 道路：收集 RoadGroundFit 网格 renderer 用到的工程材质，直接提亮 ----------
            report.Add("");
            report.Add("== 道路材质提亮（工程资产直改，乘 " + RoadMul.ToString("F2") + "）==");
            var roadMats = new HashSet<Material>();
            foreach (MeshFilter filter in UnityEngine.Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
            {
                if (filter.sharedMesh == null) continue;
                string meshPath = AssetDatabase.GetAssetPath(filter.sharedMesh);
                if (string.IsNullOrEmpty(meshPath) || !meshPath.Contains("RoadGroundFit")) continue;
                Renderer renderer = filter.GetComponent<Renderer>();
                if (renderer == null) continue;
                foreach (Material mat in renderer.sharedMaterials)
                {
                    if (mat == null) continue;
                    string matPath = AssetDatabase.GetAssetPath(mat);
                    // 只直改 .mat 工程资产；GLB/内嵌子资产不在此列（侦察确认全部是工程资产）
                    if (!string.IsNullOrEmpty(matPath) && matPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                    {
                        roadMats.Add(mat);
                    }
                    else
                    {
                        report.Add("  [警告] 非工程材质资产被路面引用，未处理: " + mat.name + " @ " + matPath);
                    }
                }
            }
            int roadChanged = 0;
            foreach (Material mat in roadMats)
            {
                if (mat.GetTag(RoadDoneTag, false, "") == "1")
                {
                    report.Add("  " + mat.name + ": 已带提亮 tag，跳过（防重复乘亮）");
                    continue;
                }
                string line = "  " + mat.name;
                if (mat.HasProperty("_BaseColor"))
                {
                    Color old = mat.GetColor("_BaseColor");
                    Color brightened = new Color(
                        Mathf.Min(old.r * RoadMul, RoadChannelCap),
                        Mathf.Min(old.g * RoadMul, RoadChannelCap),
                        Mathf.Min(old.b * RoadMul, RoadChannelCap),
                        old.a);
                    Undo.RecordObject(mat, "Brighten road material");
                    mat.SetColor("_BaseColor", brightened);
                    if (mat.HasProperty("_Color")) mat.SetColor("_Color", brightened); // 旧管线同步字段
                    line += ": _BaseColor " + ColorStr(old) + " -> " + ColorStr(brightened);
                }
                if (mat.HasProperty("_Metallic"))
                {
                    float metallic = mat.GetFloat("_Metallic");
                    line += " | metallic=" + metallic.ToString("F2");
                    if (metallic > MetallicCap)
                    {
                        mat.SetFloat("_Metallic", MetallicCap);
                        line += "->压到" + MetallicCap.ToString("F2");
                    }
                }
                mat.SetOverrideTag(RoadDoneTag, "1");
                EditorUtility.SetDirty(mat);
                roadChanged++;
                report.Add(line);
            }
            report.Add(string.Format("  道路材质共 {0} 个，本次提亮 {1} 个", roadMats.Count, roadChanged));

            // ---------- ② 驿站：glTFast 子资产只读 → 建提亮副本并替换引用 ----------
            report.Add("");
            report.Add("== 驿站材质提亮（副本替换，乘 " + StationMul.ToString("F2") +
                       " 钳 " + StationChannelCap.ToString("F2") + "）==");
            if (!AssetDatabase.IsValidFolder(StationMatFolder))
            {
                AssetDatabase.CreateFolder(StationMatFolderParent, StationMatFolderName);
            }
            // 同源材质全场只建一份副本
            var brightCache = new Dictionary<Material, Material>();
            int stationRendererChanged = 0;
            foreach (MapStationAnchor anchor in
                     UnityEngine.Object.FindObjectsByType<MapStationAnchor>(FindObjectsSortMode.None))
            {
                Transform model = anchor.transform.Find("Imported_Station_Model");
                string label = string.IsNullOrEmpty(anchor.displayName) ? anchor.name : anchor.displayName;
                if (model == null)
                {
                    report.Add("  " + label + ": 无导入模型，跳过");
                    continue;
                }
                foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
                {
                    Material[] mats = renderer.sharedMaterials;
                    bool replacedAny = false;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        Material src = mats[i];
                        if (src == null) continue;
                        string srcPath = AssetDatabase.GetAssetPath(src);
                        // 已是提亮副本（rerun 幂等）则不再处理
                        if (!string.IsNullOrEmpty(srcPath) && srcPath.Contains(StationMatFolderName)) continue;
                        Material bright;
                        if (!brightCache.TryGetValue(src, out bright))
                        {
                            bright = CreateBrightStationCopy(src, srcPath, label, report);
                            brightCache[src] = bright;
                        }
                        if (bright == null) continue;
                        mats[i] = bright;
                        replacedAny = true;
                    }
                    if (replacedAny)
                    {
                        Undo.RecordObject(renderer, "Brighten station materials");
                        renderer.sharedMaterials = mats;
                        EditorUtility.SetDirty(renderer);
                        stationRendererChanged++;
                    }
                }
            }
            report.Add(string.Format("  驿站唯一源材质 {0} 个 → 提亮副本；替换 renderer {1} 个",
                brightCache.Count, stationRendererChanged));

            // ---------- ③ 全局环境光抬档 ----------
            report.Add("");
            report.Add("== 环境光抬档 ==");
            Color newEquator, newGround;
            ColorUtility.TryParseHtmlString("#B0ACA2", out newEquator);
            ColorUtility.TryParseHtmlString("#5C564C", out newGround);
            Color oldEquator = RenderSettings.ambientEquatorColor;
            Color oldGround = RenderSettings.ambientGroundColor;
            RenderSettings.ambientEquatorColor = newEquator;
            RenderSettings.ambientGroundColor = newGround;
            report.Add("  ambientEquatorColor " + ColorStr(oldEquator) + " -> " + ColorStr(newEquator) + " (#B0ACA2)");
            report.Add("  ambientGroundColor  " + ColorStr(oldGround) + " -> " + ColorStr(newGround) + " (#5C564C)");
            EditorSceneManager.MarkSceneDirty(active);

            // ---------- 保存 ----------
            bool saved = EditorSceneManager.SaveScene(active);
            AssetDatabase.SaveAssets();
            report.Add("");
            report.Add("场景保存: " + saved);
            WriteReport(report);
            File.WriteAllText(DonePath, saved ? "Succeeded" : "SaveFailed");
            Debug.Log("[LycheeBrightnessFix_20260613] 完成，报告: " + ReportPath);
        }
        catch (Exception exception)
        {
            report.Add("EXCEPTION: " + exception);
            WriteReport(report);
            TryWriteDone("Exception");
            Debug.LogError("[LycheeBrightnessFix_20260613] 执行异常: " + exception.Message);
        }
    }

    /// <summary>
    /// 为 glTFast 只读子资产材质建提亮副本。属性名按 glTFast ShaderGraph 命名
    /// （baseColorFactor/metallicFactor/roughnessFactor/emissiveFactor/specularFactor），
    /// 并兼容 URP/Lit 命名（_BaseColor/_Metallic/_Smoothness/_EmissionColor）做兜底。
    /// </summary>
    static Material CreateBrightStationCopy(Material src, string srcPath, string stationLabel, List<string> report)
    {
        var notes = new List<string>();
        Material copy = new Material(src);

        // baseColor：源 factor=1、暗在贴图，乘数必须 >1 才有效（ShaderGraph Color 属性可超 1）
        string baseProp = copy.HasProperty("baseColorFactor") ? "baseColorFactor"
                        : copy.HasProperty("_BaseColor") ? "_BaseColor"
                        : copy.HasProperty("_Color") ? "_Color" : null;
        if (baseProp != null)
        {
            Color old = copy.GetColor(baseProp);
            Color brightened = new Color(
                Mathf.Min(old.r * StationMul, StationChannelCap),
                Mathf.Min(old.g * StationMul, StationChannelCap),
                Mathf.Min(old.b * StationMul, StationChannelCap),
                old.a);
            copy.SetColor(baseProp, brightened);
            notes.Add(baseProp + " " + ColorStr(old) + " -> " + ColorStr(brightened));
        }
        else
        {
            notes.Add("[警告] 未找到 baseColor 属性");
        }

        // metallic 压 0~0.2（侦察：贴图 B 通道本就 0.06~0.10，factor 默认 1，压 factor 更稳）
        string metalProp = copy.HasProperty("metallicFactor") ? "metallicFactor"
                         : copy.HasProperty("_Metallic") ? "_Metallic" : null;
        if (metalProp != null)
        {
            float metallic = copy.GetFloat(metalProp);
            if (metallic > MetallicCap)
            {
                copy.SetFloat(metalProp, MetallicCap);
                notes.Add(metalProp + " " + metallic.ToString("F2") + " -> " + MetallicCap.ToString("F2"));
            }
        }

        // emissive 异常清零（马匹 GLB 同病防御；本批侦察未见 emissiveFactor，但保留保险）
        string emisProp = copy.HasProperty("emissiveFactor") ? "emissiveFactor"
                        : copy.HasProperty("_EmissionColor") ? "_EmissionColor" : null;
        if (emisProp != null)
        {
            Color emissive = copy.GetColor(emisProp);
            if (emissive.maxColorComponent > 0.05f)
            {
                copy.SetColor(emisProp, Color.black);
                notes.Add(emisProp + " " + ColorStr(emissive) + " -> 黑（异常清零）");
            }
        }

        // specular 防御性归一（GLB 带 KHR_materials_specular [2,2,2]，glTFast 6.19 虽未落地仍兜底）
        if (copy.HasProperty("specularFactor"))
        {
            float specular = copy.GetFloat("specularFactor");
            if (specular > 1f)
            {
                copy.SetFloat("specularFactor", 1f);
                notes.Add("specularFactor " + specular.ToString("F2") + " -> 1");
            }
        }

        // 粗糙度/光滑度合理化：smoothness 0.2~0.4 ≈ roughness 0.6~0.8
        if (copy.HasProperty("roughnessFactor"))
        {
            float rough = copy.GetFloat("roughnessFactor");
            float clamped = Mathf.Clamp(rough, 0.6f, 0.8f);
            if (!Mathf.Approximately(rough, clamped))
            {
                copy.SetFloat("roughnessFactor", clamped);
                notes.Add("roughnessFactor " + rough.ToString("F2") + " -> " + clamped.ToString("F2"));
            }
        }
        else if (copy.HasProperty("_Smoothness"))
        {
            float smooth = copy.GetFloat("_Smoothness");
            float clamped = Mathf.Clamp(smooth, 0.2f, 0.4f);
            if (!Mathf.Approximately(smooth, clamped))
            {
                copy.SetFloat("_Smoothness", clamped);
                notes.Add("_Smoothness " + smooth.ToString("F2") + " -> " + clamped.ToString("F2"));
            }
        }

        // 命名：GLB 文件名 + 材质名（全是 Material.001，必须带 GLB 名区分）
        string glbName = string.IsNullOrEmpty(srcPath) ? "Unknown" : Path.GetFileNameWithoutExtension(srcPath);
        string safeMatName = src.name.Replace('.', '_').Replace('/', '_').Replace(' ', '_');
        string assetPath = AssetDatabase.GenerateUniqueAssetPath(
            StationMatFolder + "/" + glbName + "_" + safeMatName + "_Bright.mat");
        copy.name = Path.GetFileNameWithoutExtension(assetPath);
        AssetDatabase.CreateAsset(copy, assetPath);
        report.Add("  [" + stationLabel + "] " + glbName + "/" + src.name + " -> " + assetPath +
                   " | " + string.Join("; ", notes));
        return copy;
    }

    static void WriteReport(List<string> lines)
    {
        try { File.WriteAllText(ReportPath, string.Join(Environment.NewLine, lines) + Environment.NewLine); }
        catch (Exception e) { Debug.LogWarning("[LycheeBrightnessFix_20260613] 写报告失败: " + e.Message); }
    }

    static void TryWriteDone(string content)
    {
        try { File.WriteAllText(DonePath, content); }
        catch (Exception e) { Debug.LogWarning("[LycheeBrightnessFix_20260613] 写 done 失败: " + e.Message); }
    }

    [MenuItem("Tools/Lychee/Replay Upgrade 20260612/Brightness Fix 20260613 (rerun)")]
    static void RerunNow()
    {
        try { if (File.Exists(DonePath)) File.Delete(DonePath); }
        catch (Exception e) { Debug.LogWarning("[LycheeBrightnessFix_20260613] 删 done 失败: " + e.Message); }
        nextPollTime = 0;
    }
}
