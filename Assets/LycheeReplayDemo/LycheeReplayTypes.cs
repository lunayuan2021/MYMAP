using System;

[Serializable]
public class LycheeReplayFrame
{
    public string type;
    public int round;
    public int tick;
    public string phase;
    public LycheeReplayMessage[] messages;
    public LycheeReplayPlayerState[] players;
    public LycheeReplayNodeState[] nodes;
    public LycheeReplayWeather weather;
    public LycheeReplayTaskState[] tasks;
    public LycheeReplayBountyState[] bounties;
    public LycheeReplayContestState[] contests;
    public LycheeReplayScores scores;
    public int winnerPlayerId;
}

[Serializable]
public class LycheeReplayPlayerState
{
    public int playerId;
    public int camp;
    public string teamId;
    public string name;
    public string state;
    public string currentNodeId;
    public string nextNodeId;
    public string routeEdgeId;
    public LycheeReplayPosition position;
    public float moveProgress;
    public float progress;
    public int edgeProgressPermille;
    public int edgeProgressMs;
    public int edgeTotalMs;
    public float freshness;
    public int goodFruit;
    public int badFruit;
    public int squadAvailable;
    public int squadInFlight;
    public LycheeReplayUnitState[] objs;
    public bool verified;
    public bool delivered;
    public int score;
    // 下面这些字段 replay.txt 每回合 players[] 里都带，但旧的类没声明 → JsonUtility 静默丢弃。
    // 底部维度面板要用(总分/任务分/惩罚分/状态)，补声明即可解析。
    public int totalScore;     // 总分(首选)；缺省回退 scoreDetail.total 概念上由各分项求和、再回退 frame.scores[camp]
    public int taskScore;      // 任务分
    public int penaltyScore;   // 惩罚分(通常为负)
    public int bountyScore;    // 破关悬赏分
    public bool online;        // 在线
    public bool retired;       // 退赛
    public LycheeReplayScoreDetail scoreDetail; // 分项明细(delivery/goodFruit/freshness/time/tasks/bounty/penalty)
    // 当前读条(强制通行/站点处理/宫门验核等)。服务端在强制通行倒计时里把 currentNodeId 定死在发起节点、
    // 不下发 routeEdgeId/edgeProgress，全靠这里说明「正在强行通往哪个相邻节点、进度多少」。缺省时 JsonUtility
    // 留 null，UI 退回按 FORCED_PASS_START 事件合成的轨道。见 LycheeReplayPlayer.TryEvaluateForcedPassPose。
    public LycheeReplayProcess currentProcess;
}

[Serializable]
public class LycheeReplayProcess
{
    public string type;          // 读条类型: FORCED_PASS / PROCESS / VERIFY_GATE ...
    public string targetNodeId;  // 强制通行目标(相邻)节点
    public string fromNodeId;    // 发起节点(可选；缺省时用 player.currentNodeId)
    public float progress;       // 整体倒计时进度 0..1 (= 已用帧 / 总帧，含缴税段)
    public int totalRound;       // 总倒计时帧(缴税 + 走路线)
    public int remainingRound;   // 剩余帧
    public int timeTax;          // 时间税(前置的缴税/破障占用帧，进度前 timeTax 帧停在发起节点)
}

[Serializable]
public class LycheeReplayUnitState
{
    public int id;
    public int roleId;
    public int camp;
    public bool dead;
    public LycheeReplayPosition position;
    public string[] status;
}

[Serializable]
public class LycheeReplayPosition
{
    public float x;
    public float y;
}

[Serializable]
public class LycheeReplayNodeState
{
    public string nodeId;
    public string name;
    public string nodeType;
    public bool visible;
    public LycheeReplayGuardState guard;
    public bool resourceVisible;
    public LycheeReplayResourceStock resourceStock;
    public int effectiveCombatCount;
    public bool hasObstacle;
    public bool canWindow;
    public int processRound;
    public string processType;
    public string obstacleType;
    public LycheeReplayObstacleResidueState obstacleResidue;
}

[Serializable]
public class LycheeReplayObstacleResidueState
{
    public int clearedByPlayerId;
    public string clearedByTeamId;
    public int clearRound;
    public int untilRound;
    public int remainRound;
    public int taxRound;
}

[Serializable]
public class LycheeReplayGuardState
{
    public string ownerTeamId;
    public int defense;
    public int maxDefense;
    public int ageRound;
}

[Serializable]
public class LycheeReplayResourceStock
{
    public int ICE_BOX;
    public int FAST_HORSE;
    public int SHORT_HORSE;
    public int PASS_TOKEN;
    public int OFFICIAL_PERMIT;
    public int INTEL;
    public int BOAT_RIGHT;
}

[Serializable]
public class LycheeReplayWeather
{
    public LycheeReplayWeatherState[] active;
    public LycheeReplayWeatherState[] forecast;
}

[Serializable]
public class LycheeReplayWeatherState
{
    public string weatherId;
    public string type;
    public string region;
    public int startRound;
    public int endRound;
    public int remainRound;
    public int durationRound;
}

[Serializable]
public class LycheeReplayTaskState
{
    public string templateId;
    public string taskTemplateId;
    public string name;
    public string nodeId;
    public string targetNodeId;
    public int score;
    public int processRound;
    public string processType;
    public string taskId;
    public int expireRound;
    public int refreshRound;
    public string status;
    public string completedBy;
    public int completeRound;
    public int ownerPlayerId;
    public bool active;
    public bool completed;
    public bool failed;
    // E4(确认口径): 任务状态字段——从 tasks[] 状态读取，别假设 TASK_REFRESH/TASK_COMPLETE 事件 payload 一定带全量字段。
    public string routeBucket;      // 路线桶: ROAD(官道)/WATER(水路)/MOUNTAIN(山路)/BRANCH(支线)，见 RouteBucketCn
    public string failureReason;    // 任务失败原因(如 FAILED_TARGET_LOST)；null/空 = 不显示
    public int protectionPlayerId;  // 保护期玩家；0 = 无保护期，非 0 = 仅该玩家可完成
}

[Serializable]
public class LycheeReplayBountyState
{
    public string bountyId;
    public string bountyType;
    public string targetNodeId;
    public int score;
    public string status;
    public string completedBy;
    public string reward;
}

[Serializable]
public class LycheeReplayContestState
{
    public string contestId;
    public string contestType;
    public string targetNodeId;
    public int roundIndex;
    public int totalRounds;
    public int redPoint;
    public int bluePoint;
    public int deadlineRound;
    // §10.3：contests[] 同时承载进行中窗口与平局冷却(SUPPRESSED)。缺声明→JsonUtility 静默丢弃，冷却项会被当活跃窗口渲染。
    public bool resolved;          // true=该窗口已产生结果，不应再当作活跃窗口
    public string status;          // "SUPPRESSED"=平局冷却中；其余/空=正常窗口
    public string winnerTeamId;    // 胜方队伍(RED/BLUE)，平局为空
    public int suppressUntilRound; // 平局冷却截止帧
    public int remainRound;        // 冷却剩余帧
    public int redPlayerId;
    public int bluePlayerId;
}

[Serializable]
public class LycheeReplayScores
{
    public int RED;
    public int BLUE;
}

[Serializable]
public class LycheeReplayScoreDetail
{
    public int delivery;
    public int goodFruit;
    public int freshness;
    public int time;
    public int tasks;
    public int bounty;   // 破关悬赏分（设计 §1.4 / §10.5 scoreDetail.bounty），6 个正向分项之一
    public int penalty;
    // E3/F1(确认口径): 真实数据 scoreDetail 带 total(分项总计)。只作"分项总计"展示或兜底解释，
    // 主总分/排名/胜负一律以 players[].totalScore 为准，不得用分项之和或 total 覆盖它。
    public int total;
}

[Serializable]
public class LycheeReplayOverPlayer
{
    public int playerId;
    public string playerName;
    public string teamId;       // §3.1 阵营主键(RED/BLUE)；官方 over 可能只给 playerId/playerName，缺则靠 playerId→round 帧反查 camp
    public int camp;
    public bool online;
    public bool retired;        // §10.5 players[] 含退赛状态
    public bool delivered;
    public int deliverRound;
    public float progress;
    public float freshness;
    public int goodFruit;
    public int badFruit;
    public int taskScore;
    public int penaltyScore;    // §10.5 顶层扣分（通常为负）
    public int bountyScore;     // §10.5 顶层破关悬赏分
    // E3(确认口径): 主总分/排名/胜负只认 totalScore。真实数据还有 totalGold——那是兼容展示字段，
    // 故意不建模(JsonUtility 静默忽略)，任何时候不得拿它覆盖 totalScore。惩罚触底时 totalScore=max(0,正向-惩罚)，
    // 所以 UI 必须允许最终总分显示为 0(行和≠总分是正常现象)。
    public int totalScore;
    public LycheeReplayScoreDetail scoreDetail;
}

[Serializable]
public class LycheeReplayOverFrame
{
    public string type;
    public string matchId;
    public int overRound;
    public int winnerPlayerId;
    public string resultType;   // §10.5：NORMAL/FORFEIT/INVALID/DRAW —— 结束类型（平局/退赛/无效）
    public string overReason;   // TIME_LIMIT/ALL_DELIVERED/SINGLE_RETIRED/BOTH_RETIRED/SCORE_TIE
    public LycheeReplayOverPlayer[] players;
}

[Serializable]
public class LycheeReplayMessage
{
    public string eventId;
    public string type;
    public int round;
    public LycheeReplayPayload payload;
}

[Serializable]
public class LycheeReplayPayload
{
    public int playerId;
    public int redPlayerId;
    public int bluePlayerId;
    public int winnerPlayerId;
    public string teamId;
    public string nodeId;
    public string fromNodeId;
    public string targetNodeId;
    public string edgeId;
    public string tactic;
    public string weatherId;
    public string weatherType;
    public string region;
    public string taskId;
    public string contestId;
    public string contestType;
    // E2(确认口径,审查报告第五部分): 胜方字段分三层，别混用——
    //   winner        = WINDOW_CARD_REVEAL 单拍亮牌胜方("RED"/"BLUE"/"DRAW")
    //   winnerTeamId  = WINDOW_CONTEST_END 三拍窗口最终胜方("RED"/"BLUE"/"DRAW")；真实数据该事件只带此字段、没有 winner
    //   winnerPlayerId= 玩家级胜方(悬赏/整局 over)
    public string winner;
    public string winnerTeamId;
    public string result;
    public string redCard;
    public string blueCard;
    public string redAnim;
    public string blueAnim;
    public string action;
    public string errorCode;
    public string message;
    public string matchId;
    public string resourceType;
    public string bountyId;
    public string bountyType;
    public string reward;
    public string trigger;
    public string rushTactic;
    public string processType;
    public string orderId;
    public string templateId;
    public string taskTemplateId;
    public string toNodeId;
    public string routeEdgeId;
    public int arriveRound;
    public int totalRounds;
    public int roundIndex;
    public int redPoint;
    public int bluePoint;
    public int defenseBefore;
    public int defenseAfter;
    public int defense;
    public int attackValue;
    public int goodFruit;
    public int badFruit;
    public float freshness;
    public int timeTax;
    public int processRound;
    public int remainingRound;
    public int restRound;
    public int durationRound;
    public int startRound;
    public int expireRound;
    // E5(确认口径): SCOUT_MARKER_* 常见字段是 playerId/targetNodeId/expireRound/remainingTriggers/processType/beforeRound/afterRound，
    // 不要依赖 markerId(服务端无此字段)。真实数据 SCOUT_MARKER_ADD 带 remainingTriggers。
    public int remainingTriggers;  // 探路标记剩余可触发次数(ADD/CONSUME)
    public int beforeRound;        // SCOUT_MARKER_APPLY: 生效前处理耗时(回合)
    public int afterRound;         // SCOUT_MARKER_APPLY: 生效后处理耗时(回合)
    public int completeRound;
    public int verifiedEffectiveRound;
    public int count;
    public int memberCount;
    public int squadCount;
    public int score;
    public int edgeProgressPermille;
    public int edgeProgressMs;
    public int edgeTotalMs;
    public float progress;
    public float before;
    public float after;
    public float loss;
    // 强制通行(FORCED_PASS_START)：阻挡来源 + 总耗时(时间税另由 timeTax 承载)。契约见 UI↔字段表。
    public string blockType;
    public int totalRound;
    // 残留通行税(OBSTACLE_RESIDUAL_TAX 事件顶层)：非清障方通过被清障节点的额外加帧 + 清障方 + 截止帧。
    public int extraRound;
    public string clearedByTeamId;
    public int clearedByPlayerId;
    public int untilRound;
}

/// <summary>结算胜负判定的唯一权威（结算屏与 MATCH_END 横幅共用，永不打架）。
/// 固定优先级链：online → retired → totalScore → freshness → goodFruit → penalty(少者胜)，全同判平；INVALID 先拦截。
/// online 权威来源【只认】回放同目录 data.csv(teamOnline 按 team_id=playerId)。任一队 online 在 data.csv 里取不到，
/// 即返回 CannotDetermine，交由调用方把「找不到 data.csv」的具体错误报出来——【绝不回退读结算帧 online】，
/// 因为结算帧的 online 是最终快照、常把中途掉线者仍标 true，硬读会误导。
/// 返回 0=红胜 / 1=蓝胜 / -1=平局或无效局 / -2(=CannotDetermine)=data.csv 缺该队 online、判不了。</summary>
public static class LycheeWinnerRules
{
    public const int CannotDetermine = -2;

    public static int ResolveWinnerCamp(LycheeReplayOverFrame over, System.Collections.Generic.IReadOnlyDictionary<int, bool> teamOnline)
    {
        if (over == null) return -1;
        if (!string.IsNullOrEmpty(over.resultType) && over.resultType.Trim().ToUpperInvariant() == "INVALID") return -1;
        // online 必须来自 data.csv；任一队取不到就判不了(报错，不猜结算帧 online)。
        if (!TryOnline(teamOnline, Find(over, 0), out bool redOnline)) return CannotDetermine;
        if (!TryOnline(teamOnline, Find(over, 1), out bool blueOnline)) return CannotDetermine;
        Stats red = Build(Find(over, 0), redOnline);
        Stats blue = Build(Find(over, 1), blueOnline);
        int cmp = Compare(red, blue);
        return cmp > 0 ? 0 : cmp < 0 ? 1 : -1;
    }

    // 该队 online 是否能从 data.csv 取到：无该队结算记录/无 data.csv/data.csv 里没这队 → false(判不了)。
    static bool TryOnline(System.Collections.Generic.IReadOnlyDictionary<int, bool> teamOnline, LycheeReplayOverPlayer o, out bool online)
    {
        online = false;
        if (o == null || teamOnline == null) return false;
        return teamOnline.TryGetValue(o.playerId, out online);
    }

    struct Stats { public bool online; public bool retired; public int total; public float fresh; public int good; public int pen; }

    static LycheeReplayOverPlayer Find(LycheeReplayOverFrame over, int camp)
    {
        if (over == null || over.players == null) return null;
        foreach (LycheeReplayOverPlayer p in over.players)
            if (p != null && p.camp == camp) return p;
        return null;
    }

    static Stats Build(LycheeReplayOverPlayer o, bool online)
    {
        Stats s = new Stats();
        s.online = online;                       // 只来自 data.csv(上游 TryOnline 已保证取到)
        s.retired = o != null && o.retired;      // 退赛仍取结算帧(data.csv 无此列)
        s.total = o != null ? o.totalScore : 0;
        s.fresh = o != null ? o.freshness : 0f;
        s.good = o != null ? o.goodFruit : 0;
        int pen = o != null ? o.penaltyScore : 0;
        s.pen = pen < 0 ? -pen : pen;            // 取绝对值：惩罚越少越优
        return s;
    }

    static int Compare(Stats a, Stats b)
    {
        if (a.online != b.online) return a.online ? 1 : -1;            // ① 掉线直接判负
        if (a.retired != b.retired) return a.retired ? -1 : 1;        // ①.5 退赛判负
        if (a.total != b.total) return a.total > b.total ? 1 : -1;    // ② 总分高者胜
        if (a.fresh != b.fresh) return a.fresh > b.fresh ? 1 : -1;    // ③ 鲜度高者胜
        if (a.good != b.good) return a.good > b.good ? 1 : -1;        // ④ 好果多者胜
        if (a.pen != b.pen) return a.pen < b.pen ? 1 : -1;            // ⑤ 惩罚少者胜
        return 0;
    }
}
