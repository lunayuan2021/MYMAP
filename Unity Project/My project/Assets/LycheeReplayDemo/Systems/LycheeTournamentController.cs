using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 锦标赛顺序回放控制器（框架，20260613）。
/// 目标：5 张地图依次回放——map1 播完结算后切到 map2，直至 map5。
///
/// 现状约束（决定本期只做框架 + 安全空转）：
///  - LycheeReplayPlayer 在 Awake 一次性加载固定的 StreamingAssets/replay.txt（或内置 replayFile），
///    整个生命周期只读一份，没有运行时「换 replay / 重载」入口。
///  - 因此「同场景换 replay」需要改运行时 player 代码；本期不改 player，只把控制器骨架立起来：
///    读清单 + 监听 MatchOverRaised + 预留切换钩子；缺图/未配置时安全空转（只播 map1 / 现有 replay.txt）。
///
/// 两种推进方式（二选一，由 Inspector 配置，均 WebGL 安全）：
///  A. 分场景（推荐，零 player 改动）：为每张图各做一个场景（见 LycheeMapLayoutGenerator 产出的 Map_*.unity），
///     每个场景里 player 的内置 replayFile 指向该图自己的 replay。本控制器在 MatchOverRaised 后
///     SceneManager.LoadScene(perMapScenes[next])。切场景即换 replay，天然 WebGL 友好。
///  B. 同场景换 replay（需后续给 player 加运行时重载入口）：本控制器已用 UWR 读好下一张图的
///     replay 文本（manifest/map{n}.txt），等 player 暴露 ReloadReplay(string) 后接上即可。
///
/// 清单约定：StreamingAssets/Tournament/manifest.txt，每行一个 replay 文件名（相对 Tournament/），如
///     map1.txt
///     map2.txt
///     ...
/// 没有 manifest 时回退探测 map1.txt..map5.txt。任何文件缺失都安全跳过，绝不抛异常卡死。
/// 加载一律走 UnityWebRequest（运行时无 System.IO），WebGL 与桌面同一条路径。
/// </summary>
[DisallowMultipleComponent]
public sealed class LycheeTournamentController : MonoBehaviour
{
    [Header("清单")]
    [Tooltip("StreamingAssets 下锦标赛子目录名。清单与各图 replay 都放这里。")]
    public string tournamentFolder = "Tournament";
    [Tooltip("清单文件名（每行一个 replay 文件名）。缺失时回退探测 map1.txt..map{maxProbe}.txt。")]
    public string manifestFileName = "manifest.txt";
    [Tooltip("无清单时的探测上限（map1..mapN）。")]
    [Range(1, 16)] public int maxProbe = 5;

    [Header("推进方式 A：分场景（推荐）")]
    [Tooltip("每张图对应的场景名（需加入 Build Settings）。长度应与清单条目数一致；留空则不切场景。"
        + "例如 [\"Map_Validation_v2\",\"Map_2\",...]。")]
    public string[] perMapScenes = Array.Empty<string>();

    [Header("行为")]
    [Tooltip("结算页出现后多少秒再推进下一张图（给结算动画/观看留时间）。")]
    [Min(0f)] public float advanceDelaySeconds = 6f;
    [Tooltip("最后一张图播完后是否回到第一张循环整个锦标赛。")]
    public bool loopTournament;

    /// <summary>锦标赛进度：当前是第几张图（0 基）。供 HUD/调试读取。</summary>
    public int CurrentMapIndex { get; private set; }
    /// <summary>清单解析出的 replay 文件名（相对 Tournament/）。空表示只有现有 replay.txt 在跑。</summary>
    public IReadOnlyList<string> ReplayManifest => manifest;
    /// <summary>每张图的 replay 文本缓存（UWR 预载；分场景方案下未必用到，B 方案接 player 重载时用）。</summary>
    public string CurrentReplayText { get; private set; }

    readonly List<string> manifest = new List<string>();
    bool advancing;

    // 跨场景进度（方案 A 关键）：SceneManager.LoadScene 后旧控制器随场景销毁、新场景控制器全新 Start，
    // 实例字段 CurrentMapIndex 会被重置为 0——若不携带进度，新场景永远以为自己在 map1，
    // 切完 map2 又去切 perMapScenes[1]（还是 map2）→ 死循环，永远到不了 map3/4/5。
    // 运行期 static 不随场景加载丢失（同一 AppDomain，无 domain reload），用它把「下一张图序号」带过去。
    // -1 表示尚未进入锦标赛流程（即第一张图，按现有 replay.txt 起播）。
    static int pendingStartIndex = -1;

    void OnEnable()
    {
        LycheeReplayPlayer.MatchOverRaised += HandleMatchOver;
    }

    void OnDisable()
    {
        LycheeReplayPlayer.MatchOverRaised -= HandleMatchOver;
    }

    void Start()
    {
        // 启动即读清单（不阻塞回放；map1 由场景里 player 自己照常加载现有 replay.txt 起播）。
        StartCoroutine(LoadManifest());
    }

    // ----------------------------------------------------------------- 清单加载（UWR，WebGL 安全）

    IEnumerator LoadManifest()
    {
        manifest.Clear();
        string manifestUrl = StreamingUrl(tournamentFolder + "/" + manifestFileName);
        string manifestText = null;
        yield return FetchText(manifestUrl, t => manifestText = t);

        if (!string.IsNullOrEmpty(manifestText))
        {
            foreach (string raw in manifestText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue; // 允许 # 注释
                manifest.Add(line);
            }
            Debug.Log("[Tournament] 清单已读：" + manifest.Count + " 张图。");
        }
        else
        {
            // 无 manifest：探测 map1.txt..map{maxProbe}.txt，命中即纳入（缺失静默跳过）。
            for (int i = 1; i <= maxProbe; i++)
            {
                string name = "map" + i + ".txt";
                bool exists = false;
                yield return FetchText(StreamingUrl(tournamentFolder + "/" + name),
                    t => exists = !string.IsNullOrEmpty(t));
                if (exists) manifest.Add(name);
            }
            Debug.Log("[Tournament] 无清单，探测到 " + manifest.Count + " 张图（map1..map" + maxProbe + "）。");
        }

        // 携带跨场景进度：若上一张图在切场景前写入了 pendingStartIndex，则本场景从该序号续播。
        // 钳制到清单范围内，避免脏值越界；用完即清回 -1，回到「首图」语义。
        if (pendingStartIndex >= 0 && manifest.Count > 0)
            CurrentMapIndex = Mathf.Clamp(pendingStartIndex, 0, manifest.Count - 1);
        else
            CurrentMapIndex = 0;
        pendingStartIndex = -1;

        // 预载当前图 replay 文本（分场景方案不依赖它，但 B 方案接 player 重载时即取此缓存）。
        if (manifest.Count > 0)
            yield return CacheReplayText(CurrentMapIndex);
    }

    IEnumerator CacheReplayText(int index)
    {
        CurrentReplayText = null;
        if (index < 0 || index >= manifest.Count) yield break;
        yield return FetchText(StreamingUrl(tournamentFolder + "/" + manifest[index]),
            t => CurrentReplayText = t);
    }

    // ----------------------------------------------------------------- 比赛结束 -> 推进

    void HandleMatchOver(LycheeReplayOverFrame over)
    {
        // 结算页（LycheeSettlementScreen）也订阅了同一事件，会先暂停并弹结算。
        // 本控制器只负责「结算展示后切下一张图」，给固定延时让用户看完结算。
        if (advancing) return;
        advancing = true;
        StartCoroutine(AdvanceAfterSettlement());
    }

    IEnumerator AdvanceAfterSettlement()
    {
        if (advanceDelaySeconds > 0f)
            yield return new WaitForSecondsRealtime(advanceDelaySeconds);

        int next = CurrentMapIndex + 1;
        if (next >= Mathf.Max(manifest.Count, 1))
        {
            if (loopTournament && manifest.Count > 0)
            {
                next = 0;
            }
            else
            {
                Debug.Log("[Tournament] 已是最后一张图，锦标赛结束（不推进）。");
                advancing = false;
                yield break;
            }
        }

        // 没有清单条目（缺图）→ 安全空转：保持 map1/现有 replay.txt，不做任何切换。
        if (manifest.Count == 0)
        {
            Debug.Log("[Tournament] 未配置任何锦标赛地图，保持当前回放空转。");
            advancing = false;
            yield break;
        }

        CurrentMapIndex = next;
        yield return CacheReplayText(next);

        // 推进方式 A：切场景（已配置且 Build Settings 里有该场景）。这是本期实际生效的切换路径。
        if (perMapScenes != null && next < perMapScenes.Length && !string.IsNullOrEmpty(perMapScenes[next]))
        {
            string sceneName = perMapScenes[next];
            if (Application.CanStreamedLevelBeLoaded(sceneName))
            {
                Debug.Log("[Tournament] 切到第 " + (next + 1) + " 张图场景: " + sceneName);
                // 把进度写入跨场景 static，新场景控制器读它续播（否则它会重置回 map1 → 死循环卡在 map2）。
                pendingStartIndex = next;
                SceneManager.LoadScene(sceneName);
                yield break; // 新场景里 player 重新 Awake、加载该图 replay，本控制器随旧场景销毁
            }
            Debug.LogWarning("[Tournament] 场景未在 Build Settings 中: " + sceneName + "，空转。");
        }
        else
        {
            // 推进方式 B 的钩子点：此时 CurrentReplayText 已是下一张图的 replay。
            // 待 LycheeReplayPlayer 暴露运行时重载入口（如 ReloadReplay(string)）后在此调用即可。
            // 本期未配置分场景 → 安全空转，避免在没有重载能力时把回放搞乱。
            Debug.Log("[Tournament] 未配置 perMapScenes[" + next + "]，已预载下一张图 replay 文本但不切换（空转）。");
        }

        advancing = false;
    }

    // ----------------------------------------------------------------- UWR 工具（桌面/WebGL 同路径）

    static string StreamingUrl(string relative)
    {
        string baseUrl = Application.streamingAssetsPath;
        // 桌面平台 streamingAssetsPath 是本地文件系统路径，UnityWebRequest 需要 file:// 前缀；
        // WebGL/Android 上已是 URL（http/jar），不加前缀。加时间戳避免浏览器缓存旧文件。
        string url;
        if (baseUrl.Contains("://")) url = baseUrl + "/" + relative;
        else url = "file://" + baseUrl + "/" + relative;
        return url + "?t=" + DateTime.UtcNow.Ticks;
    }

    static IEnumerator FetchText(string url, Action<string> onResult)
    {
        using (var request = UnityEngine.Networking.UnityWebRequest.Get(url))
        {
            yield return request.SendWebRequest();
            if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                onResult?.Invoke(request.downloadHandler.text);
            else
                onResult?.Invoke(null); // 缺文件不报错，交由上层安全跳过
        }
    }
}
