# 前端 UI ↔ 游戏逻辑设计文档 一致性审查报告

- 对照文档：《一骑红尘：荔枝争运战》游戏逻辑设计文档 **V4.8.12**（定稿）
- 审查代码：`Assets/LycheeReplayDemo/`（回放播放器全部运行时脚本，约 2.5 万行）
- 日期：2026-07-01
- 方法：按设计章节 §1–§11 分 10 组交叉审查，每条发现都做了对抗式复核（CONFIRMED / REFUTED）。共产出 **51 条成立发现 + 2 条已排除**，另含你点名的 3 个专项（进度条 / 字号 / 短驿马）落地方案。
- 用法：你说"一个一个慢慢修复"，所以每条都给了 **设计引用 / 代码位置 / 现象影响 / 修复建议**。优先级分 **P0 开赛级 → P1 明显错误 → P2 缺失表现 → 歧义/文档自洽 → 潜伏 bug**。

> 说明：本报告既**确认并补精**了你之前自查清单里的已知项（给到精确代码行 + 修法），也**新挖**了一批开赛级契约风险。带「(确认已知#N)」的是你清单里已提过、这里给实锤和修法的。

---

## 目录

- [第一部分：你点名的三个问题](#第一部分你点名的三个问题)
  - [1. 处理/核验进度条 重做（去绿·队色·加大）](#1-处理核验进度条-重做)
  - [2. 字太小看不清 — 全局字号清单](#2-字太小看不清--全局字号清单)
  - [3. "获取短驿马仍冒 3D 模型" 根因](#3-获取短驿马仍冒-3d-模型-根因)
- [第二部分：P0 开赛级致命/高危](#第二部分p0-开赛级致命高危)
- [第三部分：P1 明显错误与误导](#第三部分p1-明显错误与误导)
- [第四部分：P2 缺失表现（数据有·UI 无）](#第四部分p2-缺失表现数据有ui-无)
- [第五部分：歧义 / 文档自洽问题（需你或后端拍板）](#第五部分歧义--文档自洽问题)
- [第六部分：潜伏 bug（当前数据没触发·换局会现形）](#第六部分潜伏-bug)
- [第七部分：建议修复顺序](#第七部分建议修复顺序)
- [附录：已排除项 / 统计](#附录已排除项--统计)

---

# 第一部分：你点名的三个问题

## 1. 处理/核验进度条 重做

**现状（`LycheeWorldMarkerSystem.cs`）**：进度条已经是"队色横条"，不是绿的——填充色取 `player.TeamColorOf`（红队 `RedTeamColor(0.95,0.18,0.11)` / 蓝队 `BlueTeamColor(0.20,0.48,1.0)`，`:40-41`），绿色只剩废弃常量 `CompletePulseColor(:45)` 没被进度条用。所以"去绿"这一点其实已经满足；你觉得"丑/字小"的真因是别的。

**为什么"丑 + 字小"**（三条根因）：
1. **字被 billboard 缩小了**（关键）。进度条整块挂 `LycheeReplayBillboard` 且 `distanceScale=true`，远机位按 `distance/26` 一路缩到 `minScale=0.35`（`LycheeReplayBillboard.cs`）。`fontSize=40` 只决定清晰度，真实大小 = `fontSize × characterSize(0.075) × billboard缩放`——所以**光调 fontSize 没用，会被缩回去**。
2. **视觉单薄**。只有两条细 `LineRenderer`（深底条 + 队色填充条，胶囊圆头）浮在草地上，**没有半透明圆角背板**兜底，低对比 → 显得丑。
3. **没有量化反馈**。`UpdateRings` 里注释明说「文字恒为核验中/转运中…，不再逐拍改写 N/M」（`:1257`），观众对"还剩多少"无感。

**通用前提修复（不做这条，字号怎么调都白调）**：在 `CreateProgressRing`（`:1094`）拿到 billboard 后关缩放，让进度条恒定屏幕大小：
```csharp
var bb = root.AddComponent<LycheeReplayBillboard>();
bb.distanceScale = false;   // 进度条永远同样大，不随距离缩
```
然后把 `characterSize` 0.075 → **0.09**（真实 3.6），`fontSize` 保持 40–48。

**三个可落地方案（都沿用队色、都不引绿、都只改 `LycheeWorldMarkerSystem.cs`）**：

| 方案 | 做法 | 改动量 | 好看度 | 风险 |
|---|---|---|---|---|
| **方案1（推荐首发）** | 保留两条 LineRenderer，补三样：① 加半透明圆角背板（复用 `EventPlaqueSprite()`，色 `(0.06,0.07,0.10,0.72)`，`sortingOrder` 低于条）；② 队色更实：填充 `Lerp(teamColor,white,0.06)`、底槽换"队色暗版" `(teamColor*0.22, a0.85)`（红队暗红槽/蓝队暗蓝槽）；③ 条下补小号 `N/M` 帧数 label | 小 | 中上 | 低 |
| **方案2（游戏感最强）** | 方案1 + 三层胶囊描边（底黑描边/队色暗槽/队色亮填充）+ 填充材质开自发光 `_EmissionColor=teamColor*1.3` + 主文案内嵌百分比「核验中 72%」 + 文字左侧队色圆点 | 中 | 高 | 中（动 emission 材质） |
| **方案3（最干净·定版路线）** | 弃 LineRenderer，改两个 `EventPlaqueSprite` Quad 叠进度条（圆角天然、边缘干净），填充从左端生长；标题大字 charSize 0.11 | 大 | 高 | 中（重写 `SetBarPositions`） |

**方案1 具体改点**：`ProgressRing` 结构加 `public TextMesh detail;`（`:108`）；`CreateProgressRing`（`:1094` 关缩放、`:1097` 换填充色、`:1098` 底槽队色暗版、`:1101` 前建背板、建 detail label）；`UpdateRings`（`:1256` 后写 `detail.text = cur + "/" + processRound`）。

> 建议：**先上方案1（含 `distanceScale=false`）**，一次解决"字小 + 无背板 + 无量化"三个抱怨点、几乎零风险；不够精致再叠方案2。**务必在真 WebGL 包**跑到有 `PROCESS_PROGRESS`/`VERIFY_GATE` 的回放段验收（URP Unlit + billboard 在 WebGL 与编辑器表现不同，见记忆里多条教训）。

代码锚点：`LycheeWorldMarkerSystem.cs:34,40,108,1067,1096,1102,1110,1209,1225,1255,1264,1285,1314,1454`；`LycheeReplayBillboard.cs:35`。

---

## 2. 字太小看不清 — 全局字号清单

**核心机制（改前必读）**：
- 世界层 `TextMesh` 真实大小 = **`fontSize × characterSize`**，且多挂 billboard `distanceScale=true` 远机位再缩。**要变大主要抬 `characterSize`，抬 `fontSize` 只更清晰不变大。**
- uGUI 层参考分辨率 1920×1080、`match=0.5`，WebGL 小窗等比缩小，字号数值≈1080p 像素。
- **结算页、中央对抗大窗口字号足够，不用动。** 偏小的集中在**世界层头顶/进度条**和**底部红蓝队 HUD 小行 + 下方播报**。

**世界层（重点）**：

| 标签 | 当前(fontSize×charSize=真实) | 位置 | 建议 |
|---|---|---|---|
| 进度条动作文字(核验中/转运中) | 40×0.075=3.0 | `WorldMarkerSystem.cs:1110-1113` | charSize→**0.09**、`ProgressBarLabelY`0.46→0.52 |
| 探路标记 | 30×0.052=1.56 | `:1167-1168` | charSize→**0.07**、fontSize→40 |
| 守卫/关隘标签 | 30×0.05=1.5 | `:342` | charSize→**0.065** |
| 事件信标 caption(冰鉴/快马/荔枝转坏) | 58×0.042=2.44 | `ConvoyVfx.cs:1160-1161` | charSize→**0.05** |
| 柱标 pillar | 32×0.06=1.92 | `:959` | charSize→0.07 |
| 资源计数 | 28×0.07=1.96 | `:555` | charSize→0.085 |

**UI 层（底部红蓝队 HUD / 播报 / 阶段，按 1080p）**：

| 标签 | 当前 | 位置 | 建议 |
|---|---|---|---|
| 底部指标条 数值 | 12 | `RuntimeHud.cs:1673/1675` | **15** |
| 底部指标条 增量± | 11 | `:1677/1679` | **14** |
| 底部统计行 标签/数值 | 13 | `:1435/1438/1440` | **15** |
| 阶段文字 PhaseText | 12 | `:1418` | **15** |
| 队伍花名册 roster | 14 | `:1631` | 16 |
| **下方滚动播报 正文** | 17 | `HudExtensions.cs:412` | **20**（你点名"下方播报"） |
| 播报 caption | 16 | `HudExtensions.cs:401` | 18 |
| 对抗小卡字(得势/克) | 14/16 | `RuntimeHud.cs:998` | 16/18 |

**落地方案**：
- **方案A（推荐先做，约 8 处）**：只改你点名的关键项——下方播报 17→20 / caption 16→18；进度条 charSize 0.075→0.09；探路 0.052→0.07；底部指标数值 12→15 / 增量 11→14；PhaseText 12→15。
- **方案B（彻底，约 20 处）**：A + 守卫/柱标/资源计数/花名册/统计行/信标 caption 全抬；建议把世界层 charSize 抽成常量集中管理。
- **方案C（最稳）**：`CreateWorldLabel`(`WorldMarkerSystem.cs:1471`) 乘一个全局 `WorldTextScale`(默认 1.15)，一处调节适配不同 WebGL 窗口。

> 已改过、够用不用动：控制条按钮 20 / 回合标签 18 / 结算页全部 / 中央 contest 大窗口。

---

## 3. "获取短驿马仍冒 3D 模型" 根因

**判定**：你看到的是 **(a) 车队头顶那匹 3D 奔马信标**（`player.GetEventModel("SHORT_HORSE")` → `event_horse` GLB，仅 `LycheeConvoyVfx` 一条路径会生成），**不是**节点上的资源图标（那是 2D 贴图 `icon_resource_shorthorse`，与获取瞬间无关，别改它）。

**纯顺序播放其实是对的**：回放里真正 `RESOURCE_USE + SHORT_HORSE` 只有 3 次（回合 81/207/294），当时车队 `routeEdgeId=None`（停站）→ `QueueOrPlayHorse` 正确挂起 `pendingHorse`，到回合 89/214/295 上路才 `PlayHorseVfx`。

**真正的 bug 在 seek/拖动时间轴这条 `force` 路径**（两条根因）：
1. **次序问题**：`ApplyFrame`（`LycheeReplayPlayer.cs:1410-1437`）里 `HandleEvent`(`:1424`→`QueueOrPlayHorse` 读 `rig.onEdge`) 在**前**，`RoundApplied`(`:1435`→`OnRoundApplied` 写 `onEdge`) 在**后**。`QueueOrPlayHorse` 永远读的是**上一次落定的 `onEdge`**。
2. **seek 脏状态**：`SeekFrame`(`:3587`) force 重放目标帧的 `RESOURCE_USE`，但 `LycheeConvoyVfx` **没订阅任何 seek/reset 信号**，`rig.onEdge`/`pendingHorse` 带着 seek 来源帧的脏值。若来源帧车队在路上 → `onEdge` 脏 `true` → `QueueOrPlayHorse`(`:213-216`) 直接 `PlayHorseVfx` → **3D 奔马当场冒在站里**（=你所见）。而 force 补演历史本就不该凭空生一个活 6 秒的信标。

**精确修复**：

- **A1（首选）**：`QueueOrPlayHorse` 就地按当前帧实时判断该队是否在路段，别读缓存的 `rig.onEdge`。在 `LycheeConvoyVfx.cs:209` 附近加：
  ```csharp
  bool IsPlayerOnEdgeNow(int playerId) {
      var f = player != null ? player.CurrentFrame : null;
      if (f == null || f.players == null) return false;
      foreach (var s in f.players)
          if (s != null && s.playerId == playerId)
              return !string.IsNullOrEmpty(s.routeEdgeId);
      return false;
  }
  ```
  把 `:213` 的 `if (rig.onEdge)` → `if (IsPlayerOnEdgeNow(playerId))`。停站(`routeEdgeId==None`)一律正确挂起。
- **A2（配合）**：force/seek 补演的历史事件**绝不生成活体信标**。`ApplyFrame` 已有 `applyingForcedFrame=force`，暴露它：
  ```csharp
  // LycheeReplayPlayer.cs
  public bool ApplyingForcedFrame => applyingForcedFrame;
  // LycheeConvoyVfx.OnReplayEvent 开头（:179 之后）
  if (player != null && player.ApplyingForcedFrame) return;
  ```
- **B（补 seek 脏状态，建议同做）**：`OnRoundApplied` 对 `!sequential`（seek/跳转）帧统一 `rig.pendingHorse=false`（`:166` 已有雏形，做了 A 基本自动闭合）。

**验证**：① 顺序播到回合 81/207/294 停站用马 → 站里不冒马，上路才冒；② 时间轴从"车队在路上的帧"拖到 81/207/294 → 不再当场冒马；③ 快马(`FAST_HORSE`)同链复验。**建议真 WebGL 包跑**（seek 交互 + `GetEventModel` 的 WebGL 兜底须真包才准）。

代码锚点：`LycheeConvoyVfx.cs:159,164,213`；`LycheeReplayPlayer.cs:1410,1424,1435,3587,264`。别动：`WorldMarkerSystem` 资源图标 `:527/:615`、`GetEventModel` 的 WebGL 兜底。

---

# 第二部分：P0 开赛级致命/高危

> 这批要么**换官方回放/特定结束态就崩或误判胜负**，要么**当前回放就在屏上自相矛盾**。开赛前优先。

### P0-A 数据契约风险（换正式回放 / 平局退赛态会崩或误判）

**A1. 红/蓝身份全靠文档未定义的 `camp` 整数绑定；官方回放若只给 `teamId` → 蓝队整列空白**  〔BUG·HIGH·确认〕
- 设计：§3.1 队伍结构 + §10.3 players[] 样例（`3350-3402`）——阵营字段是 `teamId="RED"/"BLUE"`，**通篇无 `camp`**。
- 代码：`LycheeReplayPlayer.cs:1377-1378/1329-1330`、`LycheeOpeningShowcase.cs:414` 全按 `p.camp==0/==1`；`LycheeReplayTypes.cs:25 public int camp`（默认0）。本地 `replay.txt` 恰好额外带了 `camp` 才能跑。
- 影响：若开赛回放严格按文档只给 `teamId`、不带 `camp`，`JsonUtility` 把两队 `camp` 都默认 0 → `FirstOrDefault(camp==1)` 返回 null → **蓝队队名/比分/头像/3D 模型/状态整列空白**，红队还可能取错人。开赛级致命。
- 建议：以 `teamId` 为主键解析红蓝（`"BLUE"?1:0`），`camp` 仅作兼容回退；`LycheeReplayPlayerState` 加 `teamId→camp` 归一化。**开赛前务必用一份"只带 teamId、无 camp"的样例回放实测开场与底部 HUD。**

**A2. `over` 帧 `resultType`/`overReason` 类未声明 → 平局/退赛局识别不了**  〔MISSING·HIGH·确认0.97〕
- 设计：§10.5（`3701-3702` 示例、`3762-3763` 字段表 `NORMAL/FORFEIT/INVALID/DRAW` × `TIME_LIMIT/ALL_DELIVERED/SINGLE_RETIRED/BOTH_RETIRED/SCORE_TIE`、`3769` 同分→`DRAW/SCORE_TIE`）。
- 代码：`LycheeReplayTypes.cs:232-239` `LycheeReplayOverFrame` 只有 `type/matchId/overRound/winnerPlayerId/players`，**无 `resultType/overReason`**；实测 over 行确实带 `resultType=NORMAL`，被 `JsonUtility` 静默丢弃。
- 影响：出现平局/单方退赛/无效局时，结算页只能凭分数臆断，**可能与裁判判定相反**。
- 建议：补 `public string resultType; public string overReason;`，结算页据 `resultType` 分平局/退赛/无效，不再仅凭 `winnerPlayerId`/分数推断。

**A3. 平局（`winnerPlayerId=null`）时结算标题仍宣布「X 夺魁」**  〔BUG·HIGH·确认〕
- 设计：§10.5 `3764/3769`（平局/无效/双退时 `winnerPlayerId=null`）。
- 代码：`LycheeReplayTypes.cs:237 public int winnerPlayerId`（值类型，JSON `null`→0）；`LycheeSettlementScreen.cs:239-263 ResolveWinnerTitle` 在 `over!=null` 分支恒返回「winnerName 夺魁」，"双方战平"分支只在 `over==null` 可达；`ResolveWinnerCamp:179-188` 真同分时判红队胜。
- 影响：真平局时比分相等，顶部却打「X 夺魁」并给金光/MVP —— **平局被宣告有胜方，开赛事故**。
- 建议：改由 `resultType=="DRAW"` 或 `winnerPlayerId<=0 且双方 totalScore 相等` 判平局，返回"双方战平"、不给任一方金光/MVP。

**A4. `contests[]` 的 `SUPPRESSED/resolved/winnerTeamId` 等字段未声明 → 冷却项会被当活跃窗口渲染**  〔BUG·MEDIUM·确认0.82〕
- 设计：§10.3（`3486-3496` SUPPRESSED 冷却对象 `status/suppressUntilRound/remainRound/...`、`3481-3483` `resolved/winnerTeamId`）。
- 代码：`LycheeReplayTypes.cs:182-192 LycheeReplayContestState` 只有 `contestId/contestType/targetNodeId/roundIndex/totalRounds/redPoint/bluePoint/deadlineRound`，缺 `status/resolved/winnerTeamId/suppressUntilRound/remainRound/redPlayerId/bluePlayerId`。
- 影响：一旦回放含平局冷却条目，它会混进 `contests[]` 被当活跃窗口渲染（`roundIndex/totalRounds` 缺省 0）→ **中央"出牌"窗口错误弹出或显 0/3 拍**；已 `resolved` 的窗口也无法收束。当前样本无 SUPPRESSED 故没爆，换局即可能触发。
- 建议：补上述字段，渲染前跳过 `status=="SUPPRESSED"` 或 `resolved==true` 的条目。

**A5. `over` 玩家 `retired/penaltyScore/bountyScore` 类未声明 → 结算页读不到退赛/顶层扣分与悬赏**  〔MISSING·MEDIUM·确认0.93〕
- 设计：§10.5 `3714-3715/3767`（这三个是 over 玩家顶层字段）。
- 代码：`LycheeReplayTypes.cs:214-229 LycheeReplayOverPlayer` 缺 `retired/penaltyScore/bountyScore`（round 帧的 `LycheeReplayPlayerState:51-54` 反而声明了）。
- 建议：补 `public bool retired; public int penaltyScore; public int bountyScore;`，结算页优先用顶层值、`scoreDetail` 兜底，据 `retired` 标退赛。

### P0-B 观众直接可见的错误 / 自相矛盾（当前回放就会错显）

**B1. 结算页"皇榜任务分"两处不同源（表格用 `scoreDetail.tasks`，状元用 `taskScore`）**  〔WRONG·HIGH·确认〕（确认已知#4）
- 设计：§6.2 `2148/2217`（皇榜任务分是全局唯一确定值）。
- 代码：`LycheeSettlementScreen.cs:286` 表格第5行 `targets[4]=detail.tasks` vs `:207` 贡运状元行 `overPlayer.taskScore`（两独立字段，`ValidateScoreConsistency` 也不校验二者）。
- 影响：**同一结算画面两个皇榜任务分**（实测 125 vs 90），开赛最刺眼的自打脸。
- 建议：把 MVP 行(`:207`)也改 `overPlayer.scoreDetail.tasks`，全页统一来源。

**B2. 逐拍副标题的"克制方向"由 winner 反推，可与 §4.9 克制表相反**  〔WRONG·HIGH·确认0.9〕
- 设计：§4.9 克制表（`1651-1658`：验牒被献贡克，验牒不克献贡）、§4.8 `1631`（弃权不克制任何牌）。
- 代码：`LycheeReplayRuntimeHud.cs:398-403 ShowCombatReveal` 硬编码 `WinnerText + " 本拍得势 · " + winCard + " 克 " + loseCard`，`winCard/loseCard` 纯由 winner 反推，**完全不查克制表**。
- 影响：实测本场 round218 `red=献贡 blue=验牒 winner=BLUE` → 屏上打「蓝队…验牒 克 献贡」，与 §4.9 **完全相反**，且发生在本场第一段对抗；靠对方弃权取胜还会打出「X 克 弃权」这类不存在的关系。
- 建议：副标题按 §4.9 克制表（4×4 常量）用 `winCard/loseCard` 真查一次；查不到克制或对方弃权时改中性文案「蓝队 本拍得势」。

**B3. 破关悬赏完成卡/播报硬显"奖励 XX 已生效"，与"悬赏只发分不发资源"冲突**  〔WRONG·HIGH·确认0.9〕
- 设计：§5.4 `1951`（破关悬赏只发悬赏分，不发放随机资源）、`1956`（EXPIRE 不发分不转移不生成替代奖励）。
- 代码：`LycheeReplayPlayer.cs:2331` 事件卡 `right=ResourceName(payload.reward)`；`LycheeHudExtensions.cs:1022` 播报固定拼「奖励 …已生效」。coverage 数据 `BOUNTY_COMPLETE` 真带 `reward:"PASS_TOKEN"` → 屏显「奖励 通关令 已生效」。
- 影响：观众误以为悬赏发放资源，与规则书直接矛盾。
- 建议：删掉 `BOUNTY_COMPLETE/CLAIM` 卡片与播报里的 `reward` 展示。

**B4. 资源中文名与文档不一致（通关令/官牒/船班权益/短驿马）**  〔WRONG·HIGH·确认0.9〕
- 设计：全文固定用 **过所(PASS_TOKEN) / 官凭(OFFICIAL_PERMIT) / 船权(BOAT_RIGHT) / 短程马(SHORT_HORSE)**（§3.6 `1052-1057`、§1.2 `325`、§2.x `572-580`）。
- 代码：`ResourceName`（`LycheeReplayPlayer.cs:3876-3879` 与 `LycheeHudExtensions.cs:1338-1341`）显示为「通关令/官牒/船班权益/短驿马」；`WorldMarkerSystem.cs:632-635` 单字回退又是「令/牒/船/驹」——**同资源三套叫法**。全程出现在事件流、资源卡、地图图标。
- 影响：解说/观众按文档术语对照直播，屏幕全对不上，显得产品与规则脱节。
- 建议：三处统一改回文档术语（过所/官凭/船权/短程马；单字回退相应改「过/凭/船/程」）。

**B5. 底部"状态"行完全不映射主车队状态枚举，恒显"在线/离线"**  〔MISSING·HIGH·确认0.95〕（确认已知 low"状态行"）
- 设计：§3.2（`852-868`）11 态：`IDLE/MOVING/WAITING/RESTING/PROCESSING/CONTESTING/FORCED_PASSING/VERIFYING/COST_BANKRUPT/DELIVERED/RETIRED`；实测 `players[].state` 每帧携带（MOVING 853 帧、PROCESSING 64 帧…）。
- 代码：`LycheeReplayRuntimeHud.cs:1578-1583 StatusTextOf` 只读 `retired/online`，全 HUD 对 `s.state` 零引用；`online` 占 1076/1078 → 该列几乎恒"在线"，近乎零信息。
- 影响：观众分不清车队在赶路/处理/验核/休整/已交付——设计定义的核心状态、最该展示的信息缺失。
- 建议：第7行改映射 `s.state`（新增 `StateChineseName` 映射 11 态：空闲/移动中/主动等待/休整中/处理中/窗口争夺中/强制通行中/宫门验核中/冻结好果清算/已交付/已退赛），`retired/online` 作后缀或小图标。

**B6. C 图单向边 E22（S08→S11）无任何方向表现**  〔MISSING·HIGH·确认0.9〕
- 设计：§2.2 `354/373`、§2.14 `820/832`（C 的 E22 单向 S08→S11，客户端应按 `edges[].bidirectional` 渲染方向）。
- 代码：`MapRouteEdge.bidirectional` 只被编辑期生成器写入；所有运行时渲染（ReplayPlayer/Minimap/WorldMarker/ConvoyVfx/CharacterMotion）**从不读取**，全工程无 arrow/oneWay/单向 渲染。
- 影响：开赛用 C 图时，唯一单向边在大图/小地图与普通边无法区分，观众不懂车队为何不能反向——C 图核心差异点缺失。
- 建议：`bidirectional==false` 的边沿 `pathPoints` 铺朝向 `to` 站的流动箭头贴花，或边中点放单向标牌（数据已有，只需运行期读取渲染）。

**B7.（确认已知#3）实时"当前得分"整局≈任务分，末帧从 ~180 突跳到真总分 742**  〔WRONG·HIGH·确认0.85〕
- 设计：§6.2 `2131-2156`（送达/好果/鲜度/用时只在交付帧一次性结算，交付前本就为 0——属设计路径）。
- 代码：`LycheeReplayRuntimeHud.cs:1545-1561 TotalScoreOf`（`totalScore==0` 时回退 `taskScore+bountyScore+penaltyScore`≈任务分），交付帧后端一次性填四项 → 瞬跳。已改名"当前得分"缓解，但跳变无过渡、无来源提示。
- 影响：末段（如 R503）180→742 像 bug/作弊，解说难解释。
- 建议：接受设计口径前提下，交付帧对"当前得分"做滚动缓动 + 弹「交付结算 +562」浮字说明来源（已有 `RollScore` 但跳幅过大）。

---

# 第三部分：P1 明显错误与误导

> 观众/解说能直接看出来的文案、口径、单位、译名错误。多为 medium/low，但开赛观感相关。

**C1. `FORCED_PASS_END` 事件卡右栏"道路重新开放"与 §4.5"强制通行不清除阻挡"矛盾**  〔WRONG·MEDIUM·确认0.85〕
`LycheeReplayPlayer.cs:2342-2348`。§4.5 `1435` 明说强制通行只让本队本次通过、不清设卡/障碍。改为「本队已通过·阻挡仍在」。

**C2. 时间税单位显示"回合"，§4.5 定义为"结算帧"**  〔WRONG·MEDIUM·确认0.75〕（确认已知"帧vs回合"）
`LycheeReplayPlayer.cs:2339`（`timeTax + "回合"`）。§4.5 `1455`：1 点时间税=1 个结算帧。统一改"帧"。

**C3. `CardText` 未映射 ABSTAIN(弃权)、且 QIANG_XING 译"抢行"而非"强行"**  〔BUG·MEDIUM·确认0.8〕
`LycheeReplayRuntimeHud.cs:2323-2331`。§4.8 `1625-1631`。弃权牌文字落到兜底"窗口牌"（虽牌面贴图 `card_abstain.png` 正确），与旁边正确贴图冲突。补 `ABSTAIN→弃权`、`QIANG_XING→强行`。

**C4. 任务模板名映射表已过期：缺 T06/T12/T13/T14，反而杜撰 T05/T07/T09/T10**  〔WRONG·MEDIUM·确认0.85〕
`LycheeReplayPlayer.cs:3818-3833 TaskTemplateName`。§5.1 `1716/1728-1736` 定义 9 模板为 T01/02/04/06/08/11/12/13/14。replay 实带 T06/T12/T13/T14 → 落兜底"皇榜任务"丢名。补 T06=争马换乘、T12=官道关验、T13=水陆联运、T14=山口急递，删 T05/T07/T09/T10。

**C5. 终局急策 `RUSH_TACTIC_USE` 只显泛化"冲刺策略"，不分疾行令/护果令/破关令**  〔AMBIGUOUS·MEDIUM·确认0.8〕
`LycheeHudExtensions.cs:2057` / `LycheeReplayPlayer.cs:4169`。§5.6 三种急策效果代价各异；`ActionName` 已能译三名却没被调用（map1 数据带 `rushTactic:RUSH_PROTECT`）。feed/pulse 改用 `ActionName(payload.rushTactic)`。

**C6.（确认已知#6）小队抵达/成功文案硬编码"清障中/清障成功"，无视 `squad.tactic`**  〔WRONG·HIGH·确认0.9〕
`LycheeReplayPlayer.cs:2715/2794`（`SquadVisual` 已存 `tactic:2588` 却不用）。一支增援/削弱/探路小队抵达仍写"清障中"。改用 `TacticName(squad.tactic)` 动态生成，FX 配色按 tactic 分。

**C7. `TacticName` 不认 V4.8.12 裸名 `SQUAD_SCOUT/WEAKEN/REINFORCE`，卡片显英文裸名**  〔WRONG·MEDIUM·确认0.8〕
`LycheeReplayPlayer.cs:3743-3750` 只匹配短名 → 裸名落到 `return tactic` 显英文；而 `HudExtensions.ActionName:1305-1316` 却认前缀（证明数据带 `SQUAD_` 前缀）。补齐或复用 `ActionName` 的 Contains 归一。

**C8.（确认已知#5）`SQUAD_SCOUT` 译"侦察完成"语义反 + "探路/侦察"两套口径**  〔WRONG·MEDIUM·确认0.85〕
`LycheeHudExtensions.cs:2038-2039`（裸名刚派出就报"侦察完成"）；`TacticName:3748` 又译"侦察"、`ActionName:1307` 译"探路"。全项目统一"探路"，`SQUAD_SCOUT` 裸名→"派出探路"，只有 `_SUCCESS` 才"探路完成"。

**C9. `FORCED_PASS_END` 译名"通行结束"漏了"强制"**  〔WRONG·LOW·确认0.8〕
`LycheeHudExtensions.cs:2052`。与配对的"强制通行"不成对，弱化付税语义。统一"强制通行结束"。

**C10.（确认已知 low）清障残留税"+6 个结算帧"被讲成"+6s"且归因错误**  〔WRONG·LOW·确认0.7〕
`LycheeHudExtensions.cs:986/991`。§3.7 `1136`。单位由帧变秒、且把残留税解释成"站点处理耗时"。改"+6 个结算帧残留通行税"。

**C11.（确认已知 low）"派出 N 人小队"人数杜撰（把消耗人手当人数）**  〔NOT_IN_DOC·MEDIUM·确认0.85〕
`SquadMemberCount`（`LycheeReplayPlayer.cs:3644-3657`）按 tactic 返 1/2 当"派出 N 人"。§3.7 `1125` 明说"1、2 是单次提交成本，不是人数"。去掉人数文案或标"消耗人手"，建模数与文案数解耦。

**C12. 小地图把"全图酷暑(HOT)"摆到中段节点群质心，误导为局部天气**  〔WRONG·MEDIUM·确认0.85〕
`LycheeMinimapSystem.cs:811`（HOT→"PLAIN"，只对 S07/S09/S11/S13 求质心）。§2.11 `616/654`（酷暑=全图 FULLSCREEN_OVERLAY，与全屏 `WeatherFxSystem` 口径不一致）。HOT 直接返回全图中心 `mapCenter`。

**C13. 小地图天气区域判定写死调测图站点 ID（把 S10 武关误当水区），换图会错位**  〔BUG·MEDIUM·确认0.8〕
`LycheeMinimapSystem.cs:794-800 StationMatchesWeatherRegion` 硬编码站点 ID。§2.2 `383`（不应把点位写死在代码）。改读 `MapEnvironmentAnchor(Weather)` 或 `weatherRegionData` 质心，与 `WeatherFxSystem CollectRegionSpots` 同源。

**C14.（确认已知 low）`penalty` 符号两处相反：底部 HUD 直显原值，结算页强制取负**  〔BUG·MEDIUM·确认0.7〕
`LycheeSettlementScreen.cs:288`（`-Mathf.Abs`）vs `LycheeReplayRuntimeHud.cs:1570-1575 PenaltyScoreOf`（原值，注释说"通常为负"）。两页可能一个 +6 一个 -6。统一用 `-Mathf.Abs()` 共用同一取数函数，明确后端 penalty=正数量约定。

**C15.（确认已知 low）`WINDOW_EFFECTIVE_COMBAT` 被当观众可见对抗节拍冒刀剑脉冲，文档定义其为服务端审计记录**  〔NOT_IN_DOC·MEDIUM·确认0.8〕
`LycheeReplayPlayer.cs:2056-2057`（经 `ShouldShowWorldPulse` 因名含"WINDOW"命中）。§9.3 `3112`。三拍窗口每拍伴随一条 → 多余刀剑脉冲。在 `ShouldShowWorldPulse` 与事件卡分支显式排除它，左侧流可留文字。

**C16.（确认已知 low）开场 VS 队名直接显回放内部 id（红队面板显"blue-l4"）**  〔WRONG·LOW·确认0.7〕
`LycheeOpeningShowcase.cs:417-418` 原样上屏 `red.name`；本地数据 camp0=RED 的 name 是"blue-l4"。开赛第一屏红蓝错位观感差。开赛前确认官方回放 name 是正式中文队名，或展示层对疑似内部 id（含 `-l\d`/纯小写英文）兜底"红队/蓝队"。

---

# 第四部分：P2 缺失表现（数据有·UI 无）

> 数据齐全但地图/HUD 无表现，观赏性缺口，非规则错误。多可并行补。

**D1. 实时底部 9 维面板缺"破关悬赏分"维度**  〔MISSING·MEDIUM·确认0.8〕
`LycheeReplayRuntimeHud.cs:144`（`StatLabels` 只有任务分/惩罚分）。§1.4/§6.2 破关悬赏分是独立正向分项（封顶 100）。有队靠悬赏拿分时"任务分"不动而"当前得分"跳涨，观众看不到来源。加一行"破关悬赏分"取 `player.bountyScore`（回退 `scoreDetail.bounty`）。

**D2. 探路标记：不显归属队伍 + 两队同点被并成一个（含提前消失 bug）**  〔MISSING/BUG·MEDIUM·确认0.85〕（合并 3 条重复发现）
`LycheeWorldMarkerSystem.cs:1146-1170 CreateScoutMarker`（按 `nodeId` 单键、固定青蓝色、无队色）+ `:1173-1183 RemoveScoutMarker`（按 `nodeId` 删）。§3.6 `1102`/§9.3 `3128`（标记是公开状态、对方能看到**归属**；同队同节点可多标记）。**bug**：两队探同点只延长到期不新建，任一队 `SCOUT_MARKER_EXPIRE` 会按 `nodeId` 删掉共享标记 → 另一队有效标记随之提前消失（赛点争抢常见）。字典键改 `nodeId+teamId`，用队色区分，EXPIRE 按复合键删。

**D3. 探路减时（`SCOUT_MARKER_APPLY/CONSUME`）无"探路加速提示"世界表现**  〔MISSING·LOW·确认0.7〕
`WorldMarkerSystem` 只处理 ADD/EXPIRE。§3.8 `1186-1189`/§9.2 `2998`（明确允许"探路加速提示"）。补 APPLY 时目标节点弹「探路加速 -3帧」、CONSUME 做消耗动画。

**D4. 多条 V4.8.12 事件不在世界脉冲白名单，地图层无锚定表现**  〔MISSING·LOW·确认0.75〕
`LycheeReplayPlayer.cs:2061-2080 ShouldShowWorldPulse` 只放行含 GUARD/WINDOW/FORCED/TASK/BOUNTY/DELIVER/VERIFY 的名。`COST_BANKRUPT/GOOD_FRUIT_SCRAP/BUFF_OVERWRITE/PLAYER_RETIRED/RETIREMENT_CLEANUP/OBSTACLE_RESIDUAL_TAX/BREAK_ORDER_BIND/POST_DELIVER_PENALTY` 世界层无脉冲（仅左侧文字）。至少给 `PLAYER_RETIRED/POST_DELIVER_PENALTY/OBSTACLE_RESIDUAL_TAX/BREAK_ORDER_BIND` 补白名单+图标兜底，或文档标注"仅左侧流"。

**D5. 清障残留通行税无常驻世界/小地图表现**  〔MISSING·LOW·确认0.75〕
帧带 `node.obstacleResidue`（`clearedByTeamId/untilRound/remainRound/taxRound`，`LycheeReplayTypes.cs:92-104`）但没人渲染，只在某队付税那瞬有一行播报。§2.12 `711/728`。障碍站点在 `remainRound>0` 期间挂"残留税 剩N帧"小徽标（清障方队色）。

**D6. 过所/官凭主动使用（`USE_RESOURCE`）无车队特效，只有一行文字**  〔MISSING·LOW·确认0.72〕
`LycheeConvoyVfx.cs:200-206 PlayResourceVfx` 只分发冰鉴/快马/短驿马。§1.7 `265`/§3.6 `1055-1056`。给过所/官凭加轻量头顶图标/文书飘字，与验牒演出配套。

**D7. 冻结好果清算/好果报废/增益覆盖仅有通用中文名，无带数量说明或演出**  〔AMBIGUOUS·LOW·确认0.6〕
`LycheeHudExtensions.cs:2070-2072 EventNameCn` 只给中文词。§3.5 `1037-1041`。`COST_BANKRUPT`（处理白做一场）在直播里只是一行小字。至少补带数量文案（「冻结好果被清算 N 篓·本次处理失败」），必要时复用好果转坏的顶部提示通道。

**D8. 已验核(`verified`)/已交付(`delivered`) 布尔在状态行与开场均无表现**  〔MISSING·MEDIUM·确认0.9〕
`LycheeReplayRuntimeHud.cs:1581-1582 StatusTextOf` 无 `verified/delivered` 分支。§3.2 `867`/`552`/`2083`（验核是交付前置里程碑、DELIVERED 是终态）。冲刺阶段观众看不出哪队已验核/已交付。状态行或队名旁加"已验核/已交付"角标（可与 B5 一并做）。

**D9. `SQUAD_REINFORCE/WEAKEN` 无世界脉冲环（结果时刻节点无独立特效）**  〔MISSING·LOW·确认0.6〕
> 注：增援/削弱**已有**目标节点"防御 ±2"专属飘字（`LycheeCombatPresentation.cs:268-275`）——所以不是"完全无表现"（见附录已排除项）。残余缺口只是无脉冲环 + 到达 FX 文案硬编码"清障中"（已并入 C6）。§9.2 `2991-2998`。若要求结果时刻有"加固/削弱"脉冲则补，否则文档注明其绑定在派出行进阶段。

---

# 第五部分：歧义 / 文档自洽问题

> 需要你或后端拍板的口径问题（含设计文档自身前后不一致）。

**E1.（文档 bug）§9.3 事件裸列表缺 `SQUAD_REINFORCE`/`SQUAD_WEAKEN`**  〔文档自洽·HIGH〕
§9.3 裸列表（`3018-3078`）SQUAD_ 系列只给 DISPATCH/SCOUT/CLEAR/FAILED，缺 REINFORCE/WEAKEN；但正下方中文含义表（`3125-3126`）又把它们列为正式事件，§10.4 示例（`3572`）还用 `tactic:"SQUAD_WEAKEN"`。前端 `HudExtensions:2033-2036` 已当正式事件处理。把这两条补进裸列表 `3057-3060`，三处一致。

**E2. 胜方字段三套命名混用 + `WINDOW_CONTEST_END` 无 payload 示例**  〔歧义·MEDIUM〕
`contests[].winnerTeamId`（队色）vs `bounties[].winnerPlayerId`（玩家号）vs 事件 `payload.winner="BLUE"`（队色字符串）。`LycheeCombatPresentation.cs:656/694/836` 对 `WINDOW_CONTEST_END` 也调 `ParseWinnerCamp(payload.winner)`，但文档从没给 `WINDOW_CONTEST_END` 的 payload 字段。若服务端该事件实际用 `winnerTeamId`，前端对抗结束时判不出胜方 → 灰化/高亮错。**建议在 §9.3/§10.4 为 `WINDOW_CONTEST_END` 补 payload 示例并统一胜方字段名/口径。**（关联你自查#7 `winner` vs `winnerTeamId`。）

**E3. 结算页总分独立取 `totalScore`，与七行分项之和在惩罚触底时可能对不上**  〔歧义·MEDIUM·确认0.6〕
`LycheeSettlementScreen.cs:291-296`（大总分取 `overPlayer.totalScore`）vs `:282-288`（七行各自 `detail.*`）。§6.2 `2096`：`最终总分=max(0, 正向和 - 惩罚)` 含对 0 的截断。极端惩罚局"行和 ≠ 总分"。文档未规定 UI 是否须"行和=总分"。文档补一句，或 UI 在触底时对惩罚行标注"(总分已按 0 计)"。

**E4. 任务 `routeBucket`/`protectionPlayerId`/`failureReason` 文档字段表未列全 + 类未声明**  〔歧义·LOW·确认0.8〕
§10.3 `3427-3445` 示例出现这三字段，但字段表未定义其含义；`LycheeReplayTypes.cs:146-167 LycheeReplayTaskState` 也没声明。当前 UI 未用，影响小。文档 §10.3 字段表补列，避免后续误判为杜撰。

**E5.（文档自洽）"探路" vs "侦路标记" 文档自身用词不统一**  〔文档自洽·LOW〕
§3.7/§3.8 用"探路标记"，§9.2 `2997` 又写"侦路标记"。统一为"探路标记"（与 C8 一并收敛前端）。

---

# 第六部分：潜伏 bug

> 当前提供的回放（`reply_full_coverage_unity_v2.txt` / map1）数据恰好没触发，换个对局/正式回放会现形。

**F1. 注释声称回退 `scoreDetail.total`，但 `LycheeReplayScoreDetail` 没声明 `total` 字段**  〔LOW·确认0.85〕
`LycheeReplayTypes.cs:49` 注释 vs `:202-211`（无 `total`）。仅注释误导，不影响运行（实际兜底走分项求和）；但生成器下发的 `total` 被丢弃，照注释实现会踩空。补 `public int total;` 或删注释那句。

**F2.（确认已知）`TotalScoreOf` 注释称 `totalScore` 整局恒 0，实测末段被填充（埋雷）**  〔LOW·确认0.75〕
`LycheeReplayRuntimeHud.cs:1543` 注释与同文件 `:142` 自相矛盾；`:1547` 首选分支正靠末段 `totalScore` 非 0 才取到真总分。误导后续维护者删掉首选分支会导致交付后"当前得分"退化。修正注释。

**F3. 窗口平局冷却持续态（`contests` SUPPRESSED）无任何表现，类缺 `status`**  〔LOW·确认0.7〕
与 A4 同根（`LycheeReplayContestState` 无 `status/suppressed`）。当前回放无 DRAW/SUPPRESSED 故没爆。若最终回放含平局冷却：补字段并在地图对象/窗口面板上以 SUPPRESSED（灰锁+「平局冷却 N帧」）持续显示。

**F4. 悬赏 `scoreGranted`（是否真加分）全代码零消费，结算与不加分被一律演成"完成/加分"**  〔MEDIUM·确认0.75〕
§5.4 `1955`（`scoreGranted=true` 才实际加分，false 表示结算但不加分）。全工程 grep 无 `scoreGranted`（Payload/BountyState 均无声明）；`BOUNTY_CLAIM` 一律显"+X分"。落后判定失败的结算会被错演成加分。Payload 补 `scoreGranted`，据其区分"悬赏结算(未加分)"与"悬赏得手 +X分"。

**F5. 代码依赖的 over/round 玩家 `camp` 字段文档从未定义（与 A1 同根，另一处）**  〔MEDIUM·确认0.9〕
`LycheeSettlementScreen.cs:320-324 FindOverPlayer` 按 `candidate.camp` 分红蓝，全仓无 `teamId→camp` 映射。文档只承诺 `teamId`。要么文档正式声明 `camp` 为展示层约定字段，要么前端加 `teamId→camp` 兜底（与 A1 一起改）。

**F6. 关隘悬赏分值以数据为准，与设计固定 18 分可能不符（数据实为 15）**  〔LOW·确认0.5〕
§5.4 `1896`（KEY_BOUNTY 固定 18、普通 10）；coverage 数据 `BOUNTY_SPAWN KEY_BOUNTY` 带 `score:15` → UI 显"+15分"。UI 忠实呈现数据没错，**开赛前用真实赛用回放核对 KEY_BOUNTY 是否发 18**；若数据确为 15 需服务端修正（UI 不要硬编码 18）。

**F7. 平局冷却持续态（contests SUPPRESSED）在地图/窗口层零可视化**  〔LOW·确认0.7〕
与 A4/F3 同族：`LycheeReplayPlayer.cs:3951-3952` 只在收到 `ACTION_REJECTED` 瞬时事件时显一次"平局冷却中"，持续态无常驻标识。补 `status` 字段后做常驻灰锁。

**F8. 护卫行动点（guardActionPoint）/ 小分队人手 余量全前端零表现**  〔LOW·确认0.6〕
§3.9 `1211-1220`/§1.3 `49`/§4.8 `1628`（护卫点 4，限制兵争牌次数，用尽按弃权）。前端渲染了兵争卡面但从不显余量。回放场景观众看不出某方"为何突然不出兵争"。若追求可读性，队伍 HUD 加护卫点/人手余量小图标；纯观赏回放可不改——**建议与需求方确认优先级。**

---

# 第七部分：建议修复顺序

1. **数据契约防崩（P0-A，开赛前必做）**：A1/F5（teamId→camp 兜底）、A2（resultType/overReason）、A3（平局不宣胜）、A4/F3/F7（contests status/resolved）、A5（over 玩家 retired/penalty/bounty）。**一次把 `LycheeReplayTypes.cs` 的字段声明补齐 + 结算/开场按 teamId 解析红蓝**，并用"按文档 schema 的样例回放"实测。这批不修，官方回放一换就可能整队空白或平局误判。

2. **同屏自相矛盾 & 规则相反（P0-B）**：B1（结算页任务分同源）、B2（克制方向查表）、B3（悬赏去"奖励已生效"）、B4（资源中文名统一）、B5（状态行映射真 state）、B6（C 图单向边方向）、B7（当前得分末段缓动）。

3. **文案/口径/单位/译名（P1）**：C1–C16 批量清理（多是几行改动，建议一起过一遍，统一"帧/回合""探路/侦察""过所/官凭/船权/短程马"口径）。

4. **缺失表现（P2，可并行）**：D1（悬赏分维度）、D2（探路标记归属+去重 bug）、D8（已验核/已交付）优先；D3–D7/D9 视排期。

5. **文档侧同步（第五部分）**：E1（补 SQUAD_REINFORCE/WEAKEN 裸列表）、E2（WINDOW_CONTEST_END payload + winner 口径）、E5（探路/侦路统一）——这些是**改文档**，建议同步给后端/规则方，避免前端反复猜字段。

6. **潜伏 bug 收尾（第六部分）**：F1/F2 注释修正、F4（scoreGranted）、F6（核对关隘 18/15）。

> 你点名的三件事：**进度条方案1 + 字号方案A + 短驿马修复 A1/A2** 可作为独立一批先落地（互不依赖，风险低，且都是你最直接的观感抱怨）。

---

# 附录：已排除项 / 统计

**已排除（复核不成立，供你放心）**：
- ❌「增援/削弱小队结果无任何结算表现」——**误读**。`LycheeCombatPresentation.cs:268-275` 订阅并对 `SQUAD_REINFORCE/WEAKEN(_SUCCESS)` 在目标节点冒"防御 ±2"紫/绿飘字，`ShowDispatch` 播报也带"防御 X→Y"。残余缺口仅"无脉冲环 + 到达 FX 文案硬编码清障中"，已并入 D9/C6。
- ❌「开场"贡运 N 篓"及"篓"单位为杜撰、缺省 100 硬编码」——**证伪**。§1.3 `45` 初始状态本身即「好果 100 篓」，全文多处用"篓"，缺省 100=文档初始值。展示层不定义逐字文案属正常。

**统计**：10 组交叉审查，**51 条成立发现**（HIGH 13 / MEDIUM 21 / LOW 17）+ 3 专项方案 + 2 条已排除。对抗式复核 CONFIRMED 47、PLAUSIBLE 1、文档自洽类 3。审查覆盖设计 §1–§11 全章与全部运行时脚本。

> 数据说明：本报告基于当前仓库回放 `Data/reply_full_coverage_unity_v2.txt` 与 map1。**多条潜伏 bug 的最终风险取决于开赛正式回放的字段与结束态**——强烈建议开赛前拿一份正式赛用回放（尤其含平局/退赛/C 图/破关悬赏加分）跑一遍第二部分 P0 清单。
