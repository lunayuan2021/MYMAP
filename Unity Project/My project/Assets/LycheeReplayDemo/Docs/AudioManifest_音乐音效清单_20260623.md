# 荔枝回放 · 音乐音效完整清单

> 覆盖全工程的发声时刻审计：**已接入(可直接替换)** + **未接入(缺口/建议新增)**。
> 审计基准：`Assets/LycheeReplayDemo/Systems/LycheeAudioDirector.cs`(唯一真相) + 11 个子系统并行扫描。
> 生成日期：2026-06-23。

---

## 0. 通用规则(替换前必读)

| 项 | 说明 |
|---|---|
| **放置目录** | BGM → `Assets/StreamingAssets/Audio/BGM/`；其余全部(含环境循环音、移动音)→ `Assets/StreamingAssets/Audio/SFX/` |
| **命名** | 系统按固定 base name 自动加载，**同名覆盖即可，无需改代码** |
| **格式** | 加载器按 `.wav → .ogg → .mp3` 顺序探测，命中即用。可换 `.ogg`(体积更小，推荐 BGM/循环音)，**但要删掉同名旧 `.wav`**，否则 `.wav` 优先被加载 |
| **缺文件** | 任何文件缺失只静默跳过，绝不影响回放 |
| **加载方式** | 运行时从 StreamingAssets 异步加载(WebGL 走 UnityWebRequest)，非 Resources 导入 |

**状态图例**：✅ 已接入(有文件、有代码) ｜ ❌ 缺失(有明确视觉演出却无声，应补) ｜ ➕ 建议新增(锦上添花)

---

## 1. 已接入清单(21 个文件 · 可直接同名替换)

构成：**3 段 BGM + 5 个环境循环音(3 天气 + 2 移动) + 12 个事件一次性音效 + 1 个 UI** = 21。

### 1.1 BGM 背景音乐(`BGM/`，需可无缝循环)

全部 `loop=true`、2D、主音量 `bgmVolume=0.55`、切歌用 **1.5 秒交叉淡入淡出**(双音源轮转)。

| 文件名 | 触发时机 | 情绪建议 |
|---|---|---|
| `bgm_normal` ✅ | 默认/常规阶段(phase=NORMAL)；开场即播；回拖到更早回合也恢复成它 | 旅途感、平稳推进 |
| `bgm_rush` ✅ | 收到 `RUSH_PHASE_START` 事件(宫宴冲刺阶段) | 紧张、加速 |
| `bgm_settlement` ✅ | 比赛结束(MatchOver / `MATCH_END`)；结算界面与胜利视频期间 | 收束、庆典 |

### 1.2 环境循环音(`SFX/`，loop，需无缝)

全部 `loop=true`、2D、**1 秒淡入淡出**、最大音量 `0.5`、≥4 倍速时淡出。

| 文件名 | 触发 | 备注 |
|---|---|---|
| `sfx_weather_rain` ✅ | 当前帧天气含 `HEAVY_RAIN` 暴雨 | 与雨幕粒子同步 |
| `sfx_weather_wind` ✅ | 当前帧天气含 `MOUNTAIN_FOG` 山雾 | ⚠️ 文件名是 wind，实际绑**山雾** |
| `sfx_weather_heat` ✅ | 当前帧天气含 `HOT` 酷热 | 热浪/蝉鸣类 |
| `sfx_hoofbeats` ✅ | 车队 `state=MOVING` 且陆路 | 马蹄声；暂停时压静音 |
| `sfx_water_row` ✅ | 仅水路车队移动时(陆路优先、互斥) | 划水/摇橹声 |

### 1.3 事件一次性音效(`SFX/`)

`PlayOneShot`、音量 `0.9`、同事件 **0.2 秒去重**、**≥4 倍速时静音**(UI 点击除外)。

| 文件名 | 触发事件(message.type) | 中文 |
|---|---|---|
| `sfx_card_reveal` ✅ | `WINDOW_CARD_REVEAL` | 对抗回合亮牌 |
| `sfx_contest_win` ✅ | `WINDOW_CONTEST_END` | 对抗窗口结束判胜负 |
| `sfx_guard_set` ✅ | `GUARD_SET` | 守卫布防 |
| `sfx_guard_break` ✅ | `GUARD_BREAK` | 攻破守卫 |
| `sfx_squad_dispatch` ✅ | `SQUAD_DISPATCH` | 小队派遣 |
| `sfx_squad_arrive` ✅ | `SQUAD_ARRIVE` | 小队抵达 |
| `sfx_task_spawn` ✅ | `TASK_SPAWN` / `BOUNTY_SPAWN`(`TASK_REFRESH` 复用) | 任务/悬赏出现 |
| `sfx_task_complete` ✅ | `TASK_COMPLETE` / `BOUNTY_COMPLETE` | 任务/悬赏完成 |
| `sfx_verify_gate` ✅ | `VERIFY_GATE_COMPLETE` / `RUSH_VERIFY_GATE` | 朱雀门验核完成 |
| `sfx_deliver` ✅ | `DELIVER_SUCCESS` | 兴庆宫荔枝交付成功 |
| `sfx_rush_horn` ✅ | `RUSH_PHASE_START`(同时切 bgm_rush) | 冲刺号角 |
| `sfx_fresh_warning` ✅ | `GOOD_TO_BAD` / `FRESHNESS_DROP` | 鲜度下降警告 |

### 1.4 UI

| 文件名 | 触发 | 中文 |
|---|---|---|
| `sfx_ui_click` ✅ | `LycheeAudioDirector.PlayUiClick()` 静态入口；不受倍速静音影响 | 通用 UI 点击 |

### 替换素材时长建议

- BGM / 环境循环音：**首尾可无缝循环**；三段 BGM 之间能交叉淡入淡出不突兀。当前 BGM 各约 5.4MB、SFX 各约 1.77MB(偏大，建议改 `.ogg`)。
- 事件一次性音效：**0.3–1.5 秒**短促有辨识度。
- `sfx_ui_click`：**< 0.2 秒**极短。

---

## 2. 未接入 — 高优先级缺口(有明确演出却无声，强烈建议补)

这些时刻在代码里有完整的视觉演出(粒子/震屏/弹窗/镜头)，但当前没有任何声音。

### 2.1 天气(真相缺口)

| 建议文件名 | 触发 | 位置 | 说明 |
|---|---|---|---|
| `sfx_weather_nightwind` ❌ | 天气 `NIGHT_WIND` 夜风 | `LycheeAudioDirector.cs:302-322`、`LycheeReplayTypes.cs:242` | **第四种天气在 `UpdateWeatherTargets` 里完全没有分支**，当前彻底静默。可临时复用 `sfx_weather_wind`，但需新增判断分支 |

### 2.2 战斗 / 对抗

| 建议文件名 | 触发事件 | 位置 | 说明 |
|---|---|---|---|
| `sfx_contest_start` ❌ | `WINDOW_CONTEST_START` | `LycheeReplayPlayer.cs:1426-1438`、`LycheeCombatPresentation.cs:599-649` | 对抗开局(决斗条+VS+回放降速)，最高频战斗节点却无起手音 |
| `sfx_squad_clear` ❌ | `SQUAD_CLEAR` / `SQUAD_CLEAR_SUCCESS` | `LycheeReplayPlayer.cs:1501,1692-1701` | 小队清障成功，有优先级卡片+镜头拉近 15m |
| `sfx_squad_failed` ❌ | `SQUAD_FAILED` | `LycheeReplayPlayer.cs:1503,1702-1711` | 小队任务失败 |
| `sfx_obstacle_clear` ❌ | `OBSTACLE_CLEAR` | `LycheeReplayPlayer.cs:1507-1511` | 山崩/断桥/码头障碍清除 |
| `sfx_bar_shatter` ❌ | 守卫血条归零碎裂 | `LycheeCombatPresentation.cs:538-545`、`LycheeWorldMarkerSystem.cs:277-301` | 血条碎裂动画 0.6s + 木屑爆，目前只复用 GUARD_BREAK 音，缺专属破裂音 |

### 2.3 终局 / 关卡节点

| 建议文件名 | 触发事件 | 位置 | 说明 |
|---|---|---|---|
| `sfx_forced_pass_start` ❌ | `FORCED_PASS_START` | `LycheeReplayPlayer.cs:1753-1760`、`LycheeCinematicDirector.cs:292-294` | 强制通行开始，伴镜头强震 |
| `sfx_verify_gate_start` ❌ | `VERIFY_GATE_START` | `LycheeReplayPlayer.cs:1786-1793`、`LycheeWorldMarkerSystem.cs:192-194` | 朱雀门验核开始(庄严官审)，宫门读条环出现 |

### 2.4 结算界面四大高光(全哑)

| 建议文件名 | 触发 | 位置 | 说明 |
|---|---|---|---|
| `sfx_banner_drop` ❌ | 胜负横幅下滑弹出(0.3s) | `LycheeSettlementScreen.cs:382-398` | |
| `sfx_total_pop` ❌ | 总分数字弹出(EaseOutBack 0.45s) | `LycheeSettlementScreen.cs:426-443` | |
| `sfx_mvp_appear` ❌ | MVP 头像回弹入场(0.5s) | `LycheeSettlementScreen.cs:445-464` | |
| `sfx_sparkles_burst` ❌ | MVP 金色星光闪散(0.85s,10 颗) | `LycheeSettlementScreen.cs:462-520` | |

### 2.5 宫宴冲刺转场

| 建议文件名 | 触发 | 位置 | 说明 |
|---|---|---|---|
| `sfx_rush_transition` ❌ | 2.8s 全屏转场(暗角+红条带+大字震屏) | `LycheeHudExtensions.cs:1446-1493` | |
| `sfx_rush_band_whoosh` ❌ | 转场上下条带横扫滑入(0–0.45s) | `LycheeHudExtensions.cs:1466-1469` | 可与上一条合成一个音 |
| `sfx_rush_impact` ❌ | RUSH 开始镜头强震 `Shake(0.45,1.2)` | `LycheeCinematicDirector.cs:296-297` | 与 `sfx_rush_horn` 叠加更有冲击 |

### 2.6 UI 接线确认(已有 clip，可能没接线)

| 文件 | 触发 | 位置 | 说明 |
|---|---|---|---|
| `sfx_ui_click`(接线) ❌ | HUD 回放控制条 `OnReplayClicked`/`OnPlayPauseClicked`/`OnSpeedClicked` | `LycheeHudExtensions.cs:1266,1273,1279` | **需确认这些 onClick 实际调用了 `PlayUiClick()`**，否则核心交互无反馈 |

---

## 3. 未接入 — 建议新增(锦上添花)

### 3.1 事件音效

| 建议文件名 | 触发事件 | 优先级 | 位置 |
|---|---|---|---|
| `sfx_completion_pulse` ➕ | 任务/悬赏完成绿色脉冲爆发 | 中 | `LycheeWorldMarkerSystem.cs:641-646` |
| `sfx_ring_complete_success` ➕ | 读条环完成成功(绿闪) | 中 | `LycheeWorldMarkerSystem.cs:784-797` |
| `sfx_ring_complete_fail` ➕ | 读条环中断/失败(红闪) | 中 | `LycheeWorldMarkerSystem.cs:784-797` |
| `sfx_process_start` ➕ | `PROCESS_START` 站点处理开始(采集/装货/登船) | 中 | `LycheeReplayPlayer.cs:1647-1653` |
| `sfx_process_complete` ➕ | `PROCESS_COMPLETE` 站点处理完成 | 中 | `LycheeReplayPlayer.cs:1664-1671` |
| `sfx_resource_claim` ➕ | `RESOURCE_CLAIM` 获得资源补给(冰箱/快马等) | 中 | `LycheeReplayPlayer.cs:1640-1646` |
| `sfx_squad_weaken` ➕ | `SQUAD_WEAKEN_SUCCESS` 削弱敌防御(紫飘字) | 中 | `LycheeReplayPlayer.cs:1491-1500` |
| `sfx_squad_reinforce` ➕ | `SQUAD_REINFORCE_SUCCESS` 强化己方防御(绿飘字) | 中 | `LycheeReplayPlayer.cs:1491-1500` |
| `sfx_forced_pass_end` ➕ | `FORCED_PASS_END` 强制通行结束 | 中 | `LycheeReplayPlayer.cs:1761-1768` |
| `sfx_match_start` ➕ | `MATCH_START` 开幕式宣言 | 中 | `LycheeReplayPlayer.cs:1572` |
| `sfx_squad_scout` ➕ | `SQUAD_SCOUT_SUCCESS` 侦查战术 | 低 | `LycheeReplayPlayer.cs:1491` |
| `sfx_weather_end` ➕ | `WEATHER_END` 天气消散轻收尾 | 低 | `LycheeReplayPlayer.cs:1633-1639` |
| `sfx_node_arrive` ➕ | `NODE_ENTER` 进入节点(频发，须极轻) | 低 | `LycheeReplayPlayer.cs:1600-1609` |
| `sfx_rush_tactic` ➕ | `RUSH_TACTIC_USE` 冲刺战术 | 低 | `LycheeReplayPlayer.cs:1560` |
| `sfx_guard_decay` ➕ | `GUARD_DECAY` 守卫防御衰减 | 低 | `LycheeReplayPlayer.cs:1578-1581` |
| `sfx_weather_forecast` ➕ | `WEATHER_FORECAST` 天气预报 | 低 | `LycheeReplayPlayer.cs:1578-1581` |
| `sfx_resource_use` ➕ | `RESOURCE_USE` 使用消耗道具 | 低 | `LycheeReplayPlayer.cs:3260` |
| `sfx_tournament_complete` ➕ | 锦标赛全部完成(非循环) | 低 | `LycheeTournamentController.cs:157-167` |

> 注：`NODE_ENTER` 节点有 13 种子类型(`STATION/PASS/WATER_STATION/PALACE_STATION/MOUNTAIN_PASS/MOUNTAIN_NODE/KEY_PASS/JUNCTION/GATE/DOCK/CHECKPOINT/START/FINISH`)，如要做差异化抵达音可按子类型分支，否则共用 `sfx_node_arrive` 一个轻音即可。

### 3.2 UI 音效

| 建议文件名 | 触发 | 优先级 | 位置 |
|---|---|---|---|
| `sfx_action_rejected` ❌ | `ACTION_REJECTED` / `INVALID_ACTION` 行动被拒 | 中 | `LycheeReplayPlayer.cs:1769-1776` |
| `sfx_ui_alert_toast` ➕ | 鲜度警示弹窗+边缘闪烁 | 中 | `LycheeHudExtensions.cs:1553-1642` |
| `sfx_ui_scrub` ➕ | 进度条拖动按下/释放 | 中 | `LycheeHudExtensions.cs:1299-1316` |
| `sfx_card_flip` ➕ | HUD 对抗双卡翻面(0.35s) | 中 | `LycheeReplayRuntimeHud.cs:289-299` |
| `sfx_ui_panel_show` ➕ | 对抗弹窗/事件卡弹出 | 低 | `LycheeReplayRuntimeHud.cs:498-536` |
| `sfx_ui_feed_slide` ➕ | 左侧事件流条目滑入(须极轻) | 低 | `LycheeHudExtensions.cs:596-667` |
| `sfx_ui_weather_appear` ➕ | 天气横幅出现 | 低 | `LycheeHudExtensions.cs:1124-1179` |
| `sfx_ui_score_pop` ➕ | 底部比分跳字回弹 | 低 | `LycheeReplayRuntimeHud.cs:664-678` |
| `sfx_menu_select` ➕ | 地图选项按钮点击(可复用 ui_click) | 低 | `LycheeMapSelectController.cs:58` |
| `sfx_menu_confirm` ➕ | 地图加载确认过渡 | 低 | `LycheeMapSelectController.cs:125` |

### 3.3 BGM / 环境层(额外音乐)

| 建议文件名 | 用途 | 优先级 | 位置 |
|---|---|---|---|
| `bgm_menu` ❌ | 地图选择/锦标赛菜单背景音乐 | 中 | `LycheeMapSelectController.cs:35`、`LycheeReplayMapRouter.cs:51` |
| `bgm_opening` ➕ | 开场演出(对阵图+开场视频) | 中 | `LycheeOpeningShowcase.cs:211-251` |
| `amb_processing` ➕ | 站点处理期环境音(装货/工作声) | 低 | `LycheeReplayPlayer.cs:686-696` |
| `amb_vignette_focus` ➕ | 对抗暗角聚焦环境层 | 低 | `LycheeReplayRuntimeHud.cs:286,309` |
| `amb_minimap_rush_pulse` ➕ | 小地图边框 RUSH 红金呼吸 | 低 | `LycheeMinimapSystem.cs:663-679` |
| `amb_freshness_ring_pulse` ➕ | 鲜度光环呼吸层(1.2Hz) | 低 | `LycheeConvoyVfx.cs:267-272` |
| `video_native_audio` ➕ | 中段插播视频自带音轨 | 低 | `LycheeVideoInterlude.cs:104-142` |

### 3.4 运镜 / 演出(可并入对应事件音)

| 建议文件名 | 触发 | 优先级 | 位置 / 处理建议 |
|---|---|---|---|
| `sfx_shake_break` ➕ | 守卫破碎镜头震动 `Shake(0.25,0.5)` | 中 | `LycheeCinematicDirector.cs:292-294`，可并入 `sfx_guard_break` |
| `sfx_shake_success` ➕ | 交付成功镜头震动 `Shake(0.3,0.8)` | 中 | `LycheeCinematicDirector.cs:299-300`，可并入 `sfx_deliver` |
| `sfx_opening_vs_pop` ➕ | 开场 VS 字弹入冲击 | 中 | `LycheeOpeningShowcase.cs:352-357` |
| `sfx_opening_slide` ➕ | 开场对阵图左右滑入 | 中 | `LycheeOpeningShowcase.cs:347-349` |
| `sfx_opening_flash` ➕ | VS 字背后金色闪光 | 低 | `LycheeOpeningShowcase.cs:361-371` |
| `sfx_score_count` ➕ | 结算分数逐行滚动计数(6 行错开) | 中 | `LycheeSettlementScreen.cs:400-424` |
| `sfx_lighting_shift` ➕ | RUSH 阶段光照过渡(2s 红化) | 低 | `LycheeCinematicDirector.cs:305-326` |

---

## 4. 词表参考(全量)

**天气类型(4)**：`HEAVY_RAIN` 暴雨 ｜ `MOUNTAIN_FOG` 山雾 ｜ `HOT` 酷热 ｜ `NIGHT_WIND` 夜风(无声)。

**阶段(phase)**：`NORMAL` 常规 → `RUSH` 冲刺 → 结算。驱动 BGM 切换。

**资源类型(7)**：`ICE_BOX` 冰箱 ｜ `FAST_HORSE` 快马 ｜ `SHORT_HORSE` 短驳马 ｜ `PASS_TOKEN` 通关牌 ｜ `OFFICIAL_PERMIT` 官凭 ｜ `INTEL` 情报 ｜ `BOAT_RIGHT` 船权。

**事件类型(message.type，约 40)**：`MATCH_START`/`MATCH_END`、`NODE_ENTER`、`TASK_SPAWN`/`COMPLETE`/`EXPIRE`/`REFRESH`、`BOUNTY_SPAWN`/`COMPLETE`、`WEATHER_START`/`FORECAST`/`END`、`WINDOW_CARD_REVEAL`/`CONTEST_START`/`CONTEST_END`、`SQUAD_DISPATCH`/`ARRIVE`/`CLEAR(_SUCCESS)`/`REINFORCE_SUCCESS`/`WEAKEN_SUCCESS`/`SCOUT_SUCCESS`/`FAILED`、`PROCESS_START`/`PROGRESS`/`COMPLETE`/`INTERRUPT`、`VERIFY_GATE_START`/`COMPLETE`/`RUSH_VERIFY_GATE`、`GUARD_SET`/`DECAY`/`BREAK`、`FORCED_PASS_START`/`END`、`RESOURCE_CLAIM`/`USE`/`COST_COMMIT`、`DELIVER_SUCCESS`、`RUSH_PHASE_START`/`RUSH_START`/`RUSH_TACTIC_USE`、`OBSTACLE_CLEAR`、`GOOD_TO_BAD`/`FRESHNESS_DROP`、`ACTION_REJECTED`/`INVALID_ACTION`。

---

## 5. 统计与建议落地顺序

- **现状**：21 个文件，覆盖 21 个核心时刻(3 BGM + 3 天气循环 + 2 移动循环 + 12 事件音 + 1 UI)。
- **缺口**：约 49 个时刻无声 —— 其中 ❌「缺失」(有演出无声)约 22 个、➕「建议新增」约 27 个。

建议补做顺序：

1. **先补真相缺口**：`sfx_weather_nightwind`(+ 加 `NIGHT_WIND` 判断分支)、确认 HUD 控制条按钮接线。
2. **结算四件套**：`banner_drop` / `total_pop` / `mvp_appear` / `sparkles_burst` —— 玩家停留最久、最该有声的画面。
3. **核心战斗补全**：`contest_start`、`squad_clear` / `squad_failed`、`obstacle_clear`、`forced_pass_start`、`verify_gate_start`、`bar_shatter`。
4. **冲刺转场**：`rush_transition`(+ whoosh/impact 可合并)。
5. **其余 UI 与 ➕ 建议项**：按优先级补，频发事件(`node_arrive`、`ui_feed_slide`)务必做得极轻或随倍速静音。
