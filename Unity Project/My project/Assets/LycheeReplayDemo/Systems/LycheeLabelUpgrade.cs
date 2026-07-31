using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 标记某些运行时自绘 UI 文本不要被世界标签 TMP 升级器接管。
/// 例如徽章牌匾内部文字已经按牌匾局部空间精确缩放，再套 TMP 会出现大字叠小字。
/// </summary>
public sealed class LycheeLabelUpgradeOptOut : MonoBehaviour
{
}

/// <summary>
/// 世界标签精致化系统（运行时自举）：用户反馈白字 TextMesh 发虚、远景标签拥挤。
/// 三件事，全部只动渲染层、不动任何写入方：
/// 1. TMP 镜像 —— LycheeTmpFontBake_20260613 烘出的 SDF 字体在 Resources 时，给每个 TextMesh
///    挂一个 TMP 子物体承接渲染（SDF 放大不糊 + 墨色描边），原 TextMesh 只关 MeshRenderer 不销毁：
///    billboard 近景淡出 / 飘字寿命淡出 / 标记计数刷新等系统仍按原引用往 TextMesh 写字改色，
///    本系统每帧把内容镜像到 TMP，谁也不破坏；字体资产缺失时整体回退原样，标签绝不消失。
///    注意 TMP 不能与 TextMesh 同物体：两者都要接管同一个 MeshRenderer/MeshFilter，会互相打架。
/// 2. 牌匾底框 —— 站名常驻标签（builder 命名 Node_Name_CN）背后垫墨色底 + 鎏金边双 Quad
///    （金的稍大 4% 露出一圈边），宽度按 TMP preferredWidth 自适应；
///    billboard 朝向沿用父物体上已有的 LycheeReplayBillboard，不重复加。
/// 3. 距离淡出 —— 治远景拥挤：站名 &lt;35m 全显、35~55m 线性淡出、&gt;55m 隐藏；
///    小队/事件/飘字等其余标签阈值放宽 1.3 倍。3D 文字用不了 CanvasGroup，
///    直接乘进顶点色 alpha，描边/牌匾的 alpha 走材质实例与 MPB 同步。
/// 性能：每 2 秒扫一次新增 TextMesh（处理过的缓存跳过）；每帧只做镜像比对与 alpha 更新，
/// 字体材质实例与 MaterialPropertyBlock 全部建时缓存，零每帧分配。
/// WebGL 安全：全程 UnityEngine/TMPro API（TMP 在 com.unity.ugui 内置，无新包依赖），无 System.IO；
/// 字体资产经 Resources 引用 TMP SDF shader，构建自动收录。
/// </summary>
public sealed class LycheeLabelUpgrade : MonoBehaviour
{
    /// <summary>全局禁用开关：true 时不自举。</summary>
    public static bool Disabled;

    const string StationLabelName = "Node_Name_CN"; // builder 的中文站名标签：常驻、配牌匾、标准阈值
    const float ScanInterval = 2f;     // 新增标签扫描间隔（秒）
    const float FadeNearDistance = 80f; // 该距离内全显（放宽：宽取景/解说录制拉远到~76也要看清站名）
    const float FadeFarDistance = 120f; // 该距离外全隐，中间线性淡出
    const float TransientScale = 1.3f;  // 小队/事件等临时标签的阈值放宽倍数
    const float OutlineWidth = 0.18f;   // TMP 描边宽（SDF 归一化单位）
    const float AlphaEpsilon = 0.01f;   // alpha 变化小于该值不写材质，省掉无意义的提交
    const float PlaquePadX = 0.22f;     // 牌匾横向内边距（标签本地单位，站名字高约 0.27）
    const float PlaquePadY = 0.05f;     // 牌匾纵向内边距
    const float GoldOversize = 1.04f;   // 鎏金边比墨底大 4%
    const float PlaqueInkZ = 0.03f;     // billboard 下相机在标签 -Z 侧，+Z 即"垫在文字身后"
    const float PlaqueGoldZ = 0.05f;    // 金边再靠后一点
    const int InkQueue = 2998;          // 牌匾排在 TMP 文字（3000）之前：金→墨→文字固定次序，
    const int GoldQueue = 2997;         // 免得靠几厘米的 z 偏移做距离排序在远处抖动
    const int StationLabelOrder = 23000;
    const int StationPlaqueInkOrder = 22999;
    const int StationPlaqueGoldOrder = 22998;
    // 主队/小分队头顶标签的墨底（仅墨、无鎏金边），排在站名牌匾之下、默认透明物之上。
    const string TransientLabelName = "Label";
    const int TransientLabelOrder = 21000;
    const int TransientPlaqueInkOrder = 20999;

    // 唐风调色板：墨 #1A1714（牌匾底 a0.78）、鎏金 #D4AF5C、描边取近墨的深色
    static readonly Color InkColor = new Color(0x1A / 255f, 0x17 / 255f, 0x14 / 255f, 0.78f);
    static readonly Color GoldColor = new Color(0xD4 / 255f, 0xAF / 255f, 0x5C / 255f, 0.92f);
    static readonly Color OutlineInk = new Color(0.08f, 0.07f, 0.06f, 1f);

    static LycheeLabelUpgrade instance;

    /// <summary>
    /// 单个标签的升级记录：source 始终是数据源（其他系统往它写），tmp 只负责渲染。
    /// lastXxx 系列是镜像比对缓存——TMP 的 text/color 赋值会触发网格重建，必须变了才写。
    /// </summary>
    sealed class LabelEntry
    {
        public TextMesh source;
        public MeshRenderer sourceRenderer; // 已禁用，保留引用以便回退
        public Transform anchor;            // source.transform 缓存，距离计算用
        public TextMeshPro tmp;
        public MeshRenderer tmpRenderer;
        public Material tmpMaterial;        // fontMaterial 实例：逐标签描边 alpha 独立淡出，必须实例化
        public bool persistent;             // 站名常驻标签：配牌匾 + 标准淡出阈值
        public MeshRenderer plaqueInk;
        public MeshRenderer plaqueGold;
        public MaterialPropertyBlock inkBlock;
        public MaterialPropertyBlock goldBlock;
        public string lastText;
        public float lastFontSize;
        public Color lastColor = new Color(-1f, -1f, -1f, -1f); // 非法初值强制首帧写入
        public float lastFadeAlpha = -1f;
        public bool lastVisible = true;
    }

    readonly Dictionary<TextMesh, LabelEntry> entries = new Dictionary<TextMesh, LabelEntry>();
    readonly List<TextMesh> deadKeys = new List<TextMesh>(); // 复用列表：遍历中不能改字典，攒着批量剔除

    TMP_FontAsset tmpFont;
    Camera mainCamera;
    Mesh quadMesh;
    Material inkMaterial;
    Material goldMaterial;
    int plaqueColorId;
    float nextScanTime;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Disabled) return;
        LycheeReplayPlayer host = FindAnyObjectByType<LycheeReplayPlayer>();
        if (host == null) return;
        if (host.GetComponent<LycheeLabelUpgrade>() != null) return;
        host.gameObject.AddComponent<LycheeLabelUpgrade>();
    }

    void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(this);
            return;
        }
        instance = this;
        tmpFont = Resources.Load<TMP_FontAsset>("Fonts/LXGWWenKai-TMP");
        if (tmpFont == null)
        {
            // 字体资产没烘出来（编辑器脚本未跑/失败）：整体回退保持 TextMesh 原样——标签绝不能消失
            Debug.LogWarning("[LycheeLabelUpgrade] 未找到 Fonts/LXGWWenKai-TMP，世界标签保持 TextMesh 原样。");
            enabled = false;
            return;
        }
        if (TMP_Settings.instance == null)
        {
            // 字体资产在而 TMP Settings 缺失（TextMesh Pro 必备资源被删/未进包）：TMP 排版的
            // 中文断行规则等内部路径无空保护会 NRE，宁可整体回退也不能让标签炸掉
            Debug.LogWarning("[LycheeLabelUpgrade] 未找到 TMP Settings，世界标签保持 TextMesh 原样。");
            enabled = false;
            return;
        }
        EnsureSharedAssets();
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
        // 回退路径：还原所有被关掉的 TextMesh 渲染，清理自建对象与材质实例防泄漏
        foreach (KeyValuePair<TextMesh, LabelEntry> pair in entries)
        {
            LabelEntry entry = pair.Value;
            if (entry.sourceRenderer != null) entry.sourceRenderer.enabled = true;
            if (entry.tmp != null) Destroy(entry.tmp.gameObject);
            if (entry.plaqueInk != null) Destroy(entry.plaqueInk.gameObject);
            if (entry.plaqueGold != null) Destroy(entry.plaqueGold.gameObject);
            if (entry.tmpMaterial != null) Destroy(entry.tmpMaterial);
        }
        entries.Clear();
        if (inkMaterial != null) Destroy(inkMaterial);
        if (goldMaterial != null) Destroy(goldMaterial);
    }

    // 用 LateUpdate：billboard 近景淡出与飘字动画都在写 TextMesh，本系统晚一步读最新值再镜像
    void LateUpdate()
    {
        if (Time.unscaledTime >= nextScanTime)
        {
            nextScanTime = Time.unscaledTime + ScanInterval;
            Scan();
        }
        if (mainCamera == null) mainCamera = Camera.main;
        bool hasEye = mainCamera != null;
        Vector3 eye = hasEye ? mainCamera.transform.position : Vector3.zero;

        deadKeys.Clear();
        foreach (KeyValuePair<TextMesh, LabelEntry> pair in entries)
        {
            LabelEntry entry = pair.Value;
            if (entry.source == null)
            {
                // 标签物体已销毁（SeekFrame 清场/事件到期）：子物体随父级一起死，记下来批量剔除
                deadKeys.Add(pair.Key);
                continue;
            }
            if (entry.tmp == null)
            {
                // TMP 子物体被外力销毁：回退原 TextMesh 渲染，条目按死亡处理（下轮扫描会重新升级）
                if (entry.sourceRenderer != null) entry.sourceRenderer.enabled = true;
                deadKeys.Add(pair.Key);
                continue;
            }
            if (!entry.source.gameObject.activeInHierarchy) continue; // 池中休眠的飘字，激活后再镜像
            SyncContent(entry);
            SyncFade(entry, eye, hasEye);
        }
        for (int i = 0; i < deadKeys.Count; i++)
        {
            if (!entries.TryGetValue(deadKeys[i], out LabelEntry entry)) continue;
            if (entry.tmpMaterial != null) Destroy(entry.tmpMaterial); // 材质实例不随物体销毁，必须手动清
            if (entry.source != null)
            {
                // 源还在而 TMP 没了的回退场景：自建牌匾一并清掉
                if (entry.plaqueInk != null) Destroy(entry.plaqueInk.gameObject);
                if (entry.plaqueGold != null) Destroy(entry.plaqueGold.gameObject);
            }
            entries.Remove(deadKeys[i]);
        }
    }

    // ====================================================================
    // 扫描与升级
    // ====================================================================

    void Scan()
    {
        // 含未激活对象：对抗演出的飘字池建出来就是 SetActive(false)，不扫等于永远漏掉
        TextMesh[] found = FindObjectsByType<TextMesh>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < found.Length; i++)
        {
            TextMesh source = found[i];
            if (source == null || entries.ContainsKey(source) || ShouldSkip(source)) continue;
            Upgrade(source);
        }
    }

    static bool ShouldSkip(TextMesh source)
    {
        return source != null && source.GetComponentInParent<LycheeLabelUpgradeOptOut>() != null;
    }

    void Upgrade(TextMesh source)
    {
        // TMP 不能与 TextMesh 同物体（都要接管同一个 MeshRenderer/MeshFilter），挂子物体；
        // 父物体上已有的 LycheeReplayBillboard 旋转/距离缩放对子物体天然生效，不重复加
        GameObject tmpGo = new GameObject("TMP_Label");
        tmpGo.transform.SetParent(source.transform, false);
        TextMeshPro tmp = tmpGo.AddComponent<TextMeshPro>();
        tmp.font = tmpFont;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.overflowMode = TextOverflowModes.Overflow;
        tmp.rectTransform.sizeDelta = Vector2.zero; // Overflow + 锚点对齐，rect 尺寸不参与排版
        tmp.alignment = MapAlignment(source.anchor);
        tmp.fontStyle = MapFontStyle(source.fontStyle);
        tmp.fontSize = TmpPointSize(source);
        tmp.text = source.text ?? string.Empty;
        tmp.color = source.color;

        MeshRenderer tmpRenderer = tmpGo.GetComponent<MeshRenderer>();
        if (tmpRenderer != null)
        {
            tmpRenderer.shadowCastingMode = ShadowCastingMode.Off;
            tmpRenderer.receiveShadows = false;
        }

        // 描边：fontMaterial 取一次即生成实例并缓存，之后淡出只改这份实例，无每帧分配；
        // 改了描边宽要让 TMP 重算网格留白，否则描边贴着字形边被切平
        Material fontMaterial = tmp.fontMaterial;
        fontMaterial.SetFloat(ShaderUtilities.ID_OutlineWidth, OutlineWidth);
        fontMaterial.SetColor(ShaderUtilities.ID_OutlineColor, OutlineInk);
        tmp.UpdateMeshPadding();

        LabelEntry entry = new LabelEntry
        {
            source = source,
            sourceRenderer = source.GetComponent<MeshRenderer>(),
            anchor = source.transform,
            tmp = tmp,
            tmpRenderer = tmpRenderer,
            tmpMaterial = fontMaterial,
            persistent = source.gameObject.name == StationLabelName,
            lastText = tmp.text,
            lastFontSize = tmp.fontSize,
        };
        if (entry.persistent && entry.tmpRenderer != null)
            entry.tmpRenderer.sortingOrder = StationLabelOrder;

        if (entry.persistent && quadMesh != null && inkMaterial != null && goldMaterial != null)
        {
            // 牌匾：墨底 + 鎏金边双 Quad；颜色走 MPB，两块牌匾共享各自材质不再实例化
            entry.plaqueGold = CreatePlaqueQuad(source.transform, "Plaque_Gold", goldMaterial, PlaqueGoldZ);
            entry.plaqueInk = CreatePlaqueQuad(source.transform, "Plaque_Ink", inkMaterial, PlaqueInkZ);
            entry.plaqueGold.sortingOrder = StationPlaqueGoldOrder;
            entry.plaqueInk.sortingOrder = StationPlaqueInkOrder;
            entry.inkBlock = new MaterialPropertyBlock();
            entry.goldBlock = new MaterialPropertyBlock();
            entry.inkBlock.SetColor(plaqueColorId, InkColor);
            entry.goldBlock.SetColor(plaqueColorId, GoldColor);
            entry.plaqueInk.SetPropertyBlock(entry.inkBlock);
            entry.plaqueGold.SetPropertyBlock(entry.goldBlock);
        }
        else if (!entry.persistent && source.gameObject.name == TransientLabelName
                 && quadMesh != null && inkMaterial != null)
        {
            // 主队/小分队头顶标签（CreateLabel 命名 "Label"）：只垫一层半透明墨底（无鎏金边），
            // 让白字在草地/地形上看得清（用户点4：原来没背景、又小又细）。
            entry.plaqueInk = CreatePlaqueQuad(source.transform, "Plaque_Ink", inkMaterial, PlaqueInkZ);
            if (entry.tmpRenderer != null) entry.tmpRenderer.sortingOrder = TransientLabelOrder;
            entry.plaqueInk.sortingOrder = TransientPlaqueInkOrder;
            entry.inkBlock = new MaterialPropertyBlock();
            entry.inkBlock.SetColor(plaqueColorId, InkColor);
            entry.plaqueInk.SetPropertyBlock(entry.inkBlock);
        }

        // 原 TextMesh 只关渲染不销毁：各系统继续往它写字/改色，本系统每帧镜像；缺字体时可整体回退
        if (entry.sourceRenderer != null) entry.sourceRenderer.enabled = false;

        entries.Add(source, entry);
        if (entry.plaqueInk != null) RefitPlaque(entry);
    }

    // ====================================================================
    // 每帧镜像与淡出
    // ====================================================================

    void SyncContent(LabelEntry entry)
    {
        TextMesh source = entry.source;
        // 文本与字号：飘字对象池复用时会整套重写（text/fontSize/characterSize），逐项比对后镜像
        string text = source.text ?? string.Empty;
        bool textChanged = entry.lastText != text;
        if (textChanged)
        {
            entry.tmp.text = text;
            entry.lastText = text;
        }
        float pointSize = TmpPointSize(source);
        bool sizeChanged = !Mathf.Approximately(entry.lastFontSize, pointSize);
        if (sizeChanged)
        {
            entry.tmp.fontSize = pointSize;
            entry.lastFontSize = pointSize;
        }
        if ((textChanged || sizeChanged) && entry.plaqueInk != null) RefitPlaque(entry);
    }

    void SyncFade(LabelEntry entry, Vector3 eye, bool hasEye)
    {
        // 远景淡出：站名 35~55m，临时标签阈值放宽 1.3 倍；近景淡出由 LycheeReplayBillboard
        // 写在 TextMesh.color.a 里，这里读到后相乘，两段淡出自然叠加互不知晓
        float fade = 1f;
        if (hasEye)
        {
            float scale = entry.persistent ? 1f : TransientScale;
            float distance = Vector3.Distance(eye, entry.anchor.position);
            fade = Mathf.Clamp01(1f - (distance - FadeNearDistance * scale) /
                                      ((FadeFarDistance - FadeNearDistance) * scale));
        }
        Color sourceColor = entry.source.color;
        float alpha = sourceColor.a * fade;

        bool visible = alpha > AlphaEpsilon;
        if (visible != entry.lastVisible)
        {
            // 全隐时直接关渲染器，省掉透明排序与无谓的材质提交
            entry.lastVisible = visible;
            if (entry.tmpRenderer != null) entry.tmpRenderer.enabled = visible;
            if (entry.plaqueInk != null) entry.plaqueInk.enabled = visible;
            if (entry.plaqueGold != null) entry.plaqueGold.enabled = visible;
        }
        if (!visible) return;

        Color target = new Color(sourceColor.r, sourceColor.g, sourceColor.b, alpha);
        if (!ColorsClose(target, entry.lastColor))
        {
            entry.tmp.color = target; // 顶点色：只淡文字面，描边/牌匾另走材质
            entry.lastColor = target;
        }
        if (Mathf.Abs(alpha - entry.lastFadeAlpha) > AlphaEpsilon)
        {
            entry.lastFadeAlpha = alpha;
            // 描边 alpha 不吃顶点色，必须随综合 alpha 写材质实例，否则文字淡了描边还杵着
            if (entry.tmpMaterial != null)
            {
                entry.tmpMaterial.SetColor(ShaderUtilities.ID_OutlineColor,
                    new Color(OutlineInk.r, OutlineInk.g, OutlineInk.b, OutlineInk.a * alpha));
            }
            if (entry.plaqueInk != null)
            {
                // 牌匾与文字同步淡出：MPB 写颜色，不实例化材质
                entry.inkBlock.SetColor(plaqueColorId,
                    new Color(InkColor.r, InkColor.g, InkColor.b, InkColor.a * alpha));
                entry.plaqueInk.SetPropertyBlock(entry.inkBlock);
                if (entry.plaqueGold != null)
                {
                    entry.goldBlock.SetColor(plaqueColorId,
                        new Color(GoldColor.r, GoldColor.g, GoldColor.b, GoldColor.a * alpha));
                    entry.plaqueGold.SetPropertyBlock(entry.goldBlock);
                }
            }
        }
    }

    void RefitPlaque(LabelEntry entry)
    {
        // 宽度按文字自适应：preferredWidth 是 TMP 本地单位，与子 Quad 同空间可直接当 localScale 用
        string text = string.IsNullOrEmpty(entry.lastText) ? " " : entry.lastText;
        Vector2 preferred = entry.tmp.GetPreferredValues(text);
        float width = preferred.x + PlaquePadX * 2f;
        float height = preferred.y + PlaquePadY * 2f;
        entry.plaqueInk.transform.localScale = new Vector3(width, height, 1f);
        if (entry.plaqueGold != null)
            entry.plaqueGold.transform.localScale = new Vector3(width * GoldOversize, height * GoldOversize, 1f);
    }

    // ====================================================================
    // 共享资产与换算
    // ====================================================================

    void EnsureSharedAssets()
    {
        GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Quad);
        MeshFilter tempFilter = temp.GetComponent<MeshFilter>();
        if (tempFilter != null) quadMesh = tempFilter.sharedMesh; // 引擎内置共享网格，不需要销毁
        Destroy(temp);

        inkMaterial = CreatePlaqueMaterial("lychee_label_plaque_ink", InkQueue);
        goldMaterial = CreatePlaqueMaterial("lychee_label_plaque_gold", GoldQueue);
        if (inkMaterial != null)
        {
            plaqueColorId = Shader.PropertyToID(
                inkMaterial.HasProperty("_BaseColor") ? "_BaseColor" : "_Color");
        }
    }

    static Material CreatePlaqueMaterial(string materialName, int renderQueue)
    {
        // Shader.Find 在 WebGL 同样可用：URP/Unlit 被场景与各系统大量引用，必然已打进包
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        if (shader == null) return null;
        Material material = new Material(shader) { name = materialName };
        if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
        if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f); // 0=Alpha，与工程其余透明材质口径一致
        material.SetOverrideTag("RenderType", "Transparent");
        if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.DisableKeyword("_ALPHATEST_ON");
        material.renderQueue = renderQueue;
        return material;
    }

    MeshRenderer CreatePlaqueQuad(Transform parent, string objectName, Material material, float zOffset)
    {
        GameObject go = new GameObject(objectName);
        go.transform.SetParent(parent, false);
        // billboard 模式下相机在标签 -Z 侧，+Z 即"垫在文字身后"
        go.transform.localPosition = new Vector3(0f, 0f, zOffset);
        go.transform.localScale = Vector3.zero; // RefitPlaque 量出文字宽后再展开
        MeshFilter filter = go.AddComponent<MeshFilter>();
        filter.sharedMesh = quadMesh;
        MeshRenderer renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return renderer;
    }

    static float TmpPointSize(TextMesh source)
    {
        // TextMesh 字高 = fontSize × characterSize × 0.1 世界单位；TMP(3D) 字高 = fontSize × 0.1 本地单位，
        // 两式相除 → TMP fontSize = TextMesh.fontSize × characterSize，标签观感大小一比一迁移
        int fontSize = source.fontSize > 0 ? source.fontSize : 13; // TextMesh fontSize=0 表示用字体默认 13
        return fontSize * Mathf.Max(0.0001f, source.characterSize);
    }

    static TextAlignmentOptions MapAlignment(TextAnchor anchor)
    {
        switch (anchor)
        {
            case TextAnchor.UpperLeft: return TextAlignmentOptions.TopLeft;
            case TextAnchor.UpperCenter: return TextAlignmentOptions.Top;
            case TextAnchor.UpperRight: return TextAlignmentOptions.TopRight;
            case TextAnchor.MiddleLeft: return TextAlignmentOptions.Left;
            case TextAnchor.MiddleRight: return TextAlignmentOptions.Right;
            case TextAnchor.LowerLeft: return TextAlignmentOptions.BottomLeft;
            case TextAnchor.LowerCenter: return TextAlignmentOptions.Bottom;
            case TextAnchor.LowerRight: return TextAlignmentOptions.BottomRight;
            default: return TextAlignmentOptions.Center;
        }
    }

    static FontStyles MapFontStyle(FontStyle style)
    {
        switch (style)
        {
            case FontStyle.Bold: return FontStyles.Bold;
            case FontStyle.Italic: return FontStyles.Italic;
            case FontStyle.BoldAndItalic: return FontStyles.Bold | FontStyles.Italic;
            default: return FontStyles.Normal;
        }
    }

    static bool ColorsClose(Color a, Color b)
    {
        return Mathf.Abs(a.r - b.r) < 0.004f && Mathf.Abs(a.g - b.g) < 0.004f &&
               Mathf.Abs(a.b - b.b) < 0.004f && Mathf.Abs(a.a - b.a) < 0.004f;
    }
}
