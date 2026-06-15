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
    public int penalty;
}

[Serializable]
public class LycheeReplayOverPlayer
{
    public int playerId;
    public string playerName;
    public int camp;
    public bool online;
    public bool delivered;
    public int deliverRound;
    public float progress;
    public float freshness;
    public int goodFruit;
    public int badFruit;
    public int taskScore;
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
    public string winner;
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
    public int durationRound;
    public int startRound;
    public int expireRound;
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
}
