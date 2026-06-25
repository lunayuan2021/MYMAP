# 回放客户端 ↔ 设计文档 V4.8.12 一致性对照报告

> 审查对象:`Assets/LycheeReplayDemo`(Unity 回放观战前端,只渲染服务端回放、不算游戏逻辑)。
> 基准:《一骑红尘:荔枝争运战 游戏逻辑设计文档》**V4.8.12**(2026-06-15),含 §9.3 权威事件清单 + §10 回放格式。
> 方法:6 域并行审计 + 逐条对抗复核(去误报)。日期:2026-06-25。

---

## 0. 根因一句话

**客户端的"事件词表"停留在旧版。** V4.8.12 改了一批事件名、删了几个、又新增了一批;客户端大多还认旧名。因此"少了什么"与"多了什么"很多是**同一枚硬币的两面**(旧名是多余、对应新名就是缺失)。
**一次性收益最大的动作**:把视觉侧的事件名映射改成"同时认新旧名"(像音频已做的那样),一举补上一大片。

---

## 1. 真 Bug —— 结算屏漏「破关悬赏分」(与改名无关)

| 位置 | 问题 |
|---|---|
| `LycheeSettlementScreen.cs:23-32` | `RowCount=6`、`RowLabels` 六行无"破关悬赏分"(设计 §1.4 它是 6 个正向分项之一) |
| `LycheeReplayTypes.cs:181` `ScoreDetail` | 无 `bounty` 字段 → over 帧悬赏分被静默丢弃 |
| `LycheeReplayPlayer.cs:894` 自检公式 | `sum=delivery+goodFruit+freshness+time+tasks-|penalty|` 漏加 bounty → **凡悬赏>0 的局都会误报"明细≠总分"** |
| `LycheeReplayTypes.cs:192` `OverPlayer` | 缺 `bountyScore`/`penaltyScore`;MVP 战绩只显皇榜任务分 |

> **本报告配套修复**:已补 `ScoreDetail.bounty` 字段 + 结算屏第 7 行 + 自检公式(详见 git 提交)。

---

## 2. 少了什么(设计有,前端没有/没渲染)

### 🔴 高影响

| 缺失 | 证据 |
|---|---|
| 窗口平局 `WINDOW_CONTEST_DRAW` | 全仓 0 订阅;`CombatPresentation` switch 仅 START/REVEAL/END;平局无专属表现 |
| 平局冷却 `WINDOW_CONTEST_REPEAT_SUPPRESSED` + contests[] 的 SUPPRESSED 态 | 0 命中;`contests[]` 只被 `CharacterMotion:1180` 判空/非空,从不读 status/冷却 |
| 探路标记 `SCOUT_MARKER_*` + `SQUAD_SCOUT` 可视化 | §9.2 明列"侦路标记/探路加速提示";节点状态无 `scoutMarker` 字段,世界标记系统无此类型,全缺 |
| 小分队 `SQUAD_REINFORCE` / `SQUAD_WEAKEN`(裸名) | 代码只认旧名 `*_SUCCESS`;裸名打不中 `CombatPresentation:265-269` 的防御 ±2 飘字 |
| 道路障碍 `PASS_CROWD` / `LANDSLIDE` | map1/map2 在 S06/S10/S11 的**默认障碍,正常对局必现**;`ReplayPlayer:1357` 只为 ROCKFALL/BROKEN_BRIDGE/DOCK_BLOCK 做专属模型 → 这俩落通用模型 + 英文名 |
| **窗口类型 `contestType` 不区分**(RESOURCE/TASK/GATE/DOCK/PASS/OBSTACLE) | 字段数据里有(`ContestState.contestType`/`Payload.contestType`)、前端 0 读取点 → 宫门/通行/资源/障碍/任务争夺文案恒为"对抗开始/争夺节点" |

### 🟡 中影响

- 逐对象胜出事件 `GATE_/TASK_/OBSTACLE_/DOCK_/PASS_CONTEST_WIN` + `PASS_CONTEST_DEFENDED` 无专属表现(只走通用 `WINDOW_CONTEST_END`);`PASS_CONTEST_WIN.payload.timeTax/blockType` 不渲染。
- 改名事件视觉侧未跟上:`BOUNTY_CREATE`/`BOUNTY_CLAIM`(视觉只认旧 `BOUNTY_SPAWN/COMPLETE`)、`GUARD_WEATHERING`(只认旧 `GUARD_DECAY`)。
- 一批新事件只显英文原文:`COST_BANKRUPT`、`GOOD_FRUIT_SCRAP`、`BUFF_OVERWRITE`、`DISCONNECT_WARNING`、`PLAYER_RETIRED`、`RETIREMENT_CLEANUP`、`VERIFY_GATE_ALREADY_DONE`、`OBSTACLE_RESIDUAL_TAX`、`WINDOW_EFFECTIVE_COMBAT`、`TASK_TARGET_LOST`、`TASK_PROTECTION_START`、`BOUNTY_EXPIRE`、`BREAK_ORDER_BIND`、`POST_DELIVER_PENALTY`。
- over 帧缺 `resultType`(NORMAL/FORFEIT/INVALID/DRAW)/`overReason` → 无法区分退赛/平局/无效局及结束原因;平局/退赛时 `winnerPlayerId=null` 会被误判后回退比分。
- over 玩家缺 `bountyScore`/`penaltyScore`。
- 主车队 `moveDirection`(FORWARD/PAUSED/NONE)字段被丢弃 → 暂停态无法区分。
- 主车队状态 RESTING/WAITING/CONTESTING/VERIFYING/FORCED_PASSING 无差异体态。
- 道路障碍 `FLOOD`/`MUD` 同样落通用模型 + 英文名(非默认候选,概率低)。
- 3D 处理字幕未本地化:`TRANSFER`/`PASS_TRANSFER`/`PALACE_TRANSFER`/`VERIFY`(HUD 侧已本地化,3D 路径漏 4 种)。

### ⚪ 低影响
节点类型 CHECKPOINT/PALACE_STATION 落默认建筑(生成器在审查范围外,名字仍对)、天气 `FULLSCREEN/REGION_OVERLAY` 展示配置未读(硬编码推导结果一致)、区域天气仍全屏施滤镜、护卫行动点余量未渲染、round 帧逐队分项快照字段未解析。

---

## 3. 多了什么(代码有,V4.8.12 已无 = 旧版遗留)

多为第 2 节缺失项的**镜像**(旧名换新名,代码留了旧名):

| 多余(旧名) | V4.8.12 对应 |
|---|---|
| `MATCH_START` / `MATCH_END` | 删除(起止用回放帧 `type=start/over`) |
| `BOUNTY_SPAWN` / `BOUNTY_COMPLETE` | → `BOUNTY_CREATE` / `BOUNTY_CLAIM` |
| `SQUAD_*_SUCCESS` / `SQUAD_FAIL` / `SQUAD_ARRIVE` | → 裸名 `SQUAD_CLEAR/SCOUT/REINFORCE/WEAKEN/FAILED`(无 ARRIVE) |
| `PROCESS_START` / `PROCESS_INTERRUPT` | → `PROCESS_PROGRESS/COMPLETE` + `COST_BANKRUPT` |
| `VERIFY_GATE_START` | 删除(只剩 COMPLETE / ALREADY_DONE) |
| `GUARD_DECAY` | → `GUARD_WEATHERING` |
| `WEATHER_FORECAST/START/END` | 删除(天气调度在 start 帧 `weatherSchedule`) |
| `COST_COMMIT` / `COST_FREEZE` | → 仅 `COST_BANKRUPT` |
| `ROUND_STATE` | 非标准事件 |
| 天气 `NIGHT_WIND` 夜风 | 删除(4 处死分支,主链路白名单已跳过,无害) |
| icon_mapping.json 旧键 | `WINDOW_CARD_DRAW/WIN`、`PLAYER_DISCONNECT/FORFEIT`、`GUARD_REPLACE`、`DELIVER_NOT_VERIFIED`、`INVALID_*` 细分、`RUSH_VERIFY_GATE`(纯 JSON 键、无 .cs 引用、无害) |

**无害兼容**(并存、不报错,可不动):`TASK_SPAWN`(与 `TASK_REFRESH` 并存)、`RUSH_PHASE_START`(已加兼容)、`sfx_weather_wind` 命名(实绑山雾)。

---

## 4. 审查纠正的一处旧结论

**兵争牌 `BING_ZHENG` 视觉其实早已实现** —— `LycheeCardFxSystem.cs:401-444` `BuildBingZheng`(6 道银白刀光 + 金属火花),HUD 也返回"兵争"。此前音频清单 MD 里"兵争视觉未实现"的说法**有误,已更正**。窗口四牌(验牒/强行/献贡/兵争)视觉**全齐**,对应牌面音可 4 张一起做。

---

## 5. 建议优先级

1. **修结算 bug**(破关悬赏分):`ScoreDetail.bounty` + 结算屏第 7 行 + 自检公式。【已配套修复】
2. **窗口类型 `contestType` 区分**:文案按 宫门/通行/资源/障碍/任务/路线 分流(数据已有字段,只缺读取)。
3. **事件改名对齐**:视觉侧映射改成同时认新旧名,一举补 `BOUNTY_CREATE/CLAIM`、`GUARD_WEATHERING`、`SQUAD_*` 裸名、`PROCESS_*` 等一大片。
4. **障碍 `PASS_CROWD`/`LANDSLIDE` 专属模型 + 中文名**(正常对局必现的英文名/通用模型问题)。
5. **窗口平局 + 冷却 + 逐对象胜出** 呈现;探路标记可视化;over 帧 `resultType/overReason`、`moveDirection` 等字段补解析。
