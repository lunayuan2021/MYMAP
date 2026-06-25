# 荔枝回放 · 音频替换清单(必要 / 推荐 / 全覆盖)

> 用途:全量替换工程音频时,回答「必要做几个、推荐做几个」。
> 依据:`AudioManifest_音乐音效清单_20260623.md` + 对源码逐条核实(`LycheeAudioDirector.cs` 等 5 个子系统并行验证)。
> 生成日期:2026-06-25。

---

## 0. 结论先行

| 档位 | 文件数 | 含义 |
|---|---|---|
| **必要(最少)** | **21** | 把现在会响的全部同名替换,零改代码、不新增静默 |
| **推荐** | **31** | 21 + 10 个高优先级新文件(已逐条核实、已去重);另含 2 个「零文件」代码补丁 |
| **全覆盖(上限)** | **≈ 48–52** | 再补「锦上添花」项,收益递减 |

**核心去重发现**:高优先级缺口里大半能复用现有音 / 合并 / 代码消化,真正要新做的远少于「一时刻一文件」。

---

## 1. 必要 = 21 个(替换现有声景)

`Assets/StreamingAssets/Audio/` 下现有 21 个文件,**同名覆盖即可,无需动代码**。
加载器按 `.wav → .ogg → .mp3` 探测;换 `.ogg`(体积更小,推荐 BGM/循环音)**必须删掉同名旧 `.wav`**,否则 `.wav` 优先被加载。

### 1.1 BGM ×3(`Audio/BGM/`)
全部 `loop=true`、2D、`bgmVolume=0.55`、切歌用 1.5 秒交叉淡入淡出。**首尾需无缝,三段之间能交叉淡入不突兀。**

| 文件名 | 触发 | 情绪 |
|---|---|---|
| `bgm_normal` | 默认/常规阶段;开场即播 | 旅途、平稳推进 |
| `bgm_rush` | `RUSH_PHASE_START` 宫宴冲刺 | 紧张、加速 |
| `bgm_settlement` | 比赛结束 / 结算 / 胜利视频 | 收束、庆典 |

### 1.2 环境循环 ×5(`Audio/SFX/`)
全部 `loop=true`、2D、1 秒淡入淡出、最大音量 `0.5`、≥4 倍速时淡出。**首尾需无缝。**

| 文件名 | 触发 | 备注 |
|---|---|---|
| `sfx_weather_rain` | 暴雨 `HEAVY_RAIN` | 与雨幕粒子同步 |
| `sfx_weather_wind` | 山雾 `MOUNTAIN_FOG` | ⚠️ 文件名 wind,实际绑山雾(夜风也将复用它) |
| `sfx_weather_heat` | 酷热 `HOT` | 热浪/蝉鸣类 |
| `sfx_hoofbeats` | 车队陆路移动 | 马蹄声;暂停压静音 |
| `sfx_water_row` | 车队水路移动 | 划水/摇橹;与陆路互斥 |

### 1.3 事件一次性 ×12(`Audio/SFX/`)
`PlayOneShot`、音量 `0.9`、同事件 0.2 秒去重、≥4 倍速静音。**0.3–1.5 秒,短促有辨识度。**

| 文件名 | 触发事件 | 中文 |
|---|---|---|
| `sfx_card_reveal` | `WINDOW_CARD_REVEAL` | 对抗回合亮牌 |
| `sfx_contest_win` | `WINDOW_CONTEST_END` | 对抗判胜负 |
| `sfx_guard_set` | `GUARD_SET` | 守卫布防 |
| `sfx_guard_break` | `GUARD_BREAK` | 攻破守卫 |
| `sfx_squad_dispatch` | `SQUAD_DISPATCH` | 小队派遣 |
| `sfx_squad_arrive` | `SQUAD_ARRIVE` | 小队抵达 |
| `sfx_task_spawn` | `TASK_SPAWN` / `BOUNTY_SPAWN` | 任务/悬赏出现 |
| `sfx_task_complete` | `TASK_COMPLETE` / `BOUNTY_COMPLETE` | 任务/悬赏完成 |
| `sfx_verify_gate` | `VERIFY_GATE_COMPLETE` | 朱雀门验核完成 |
| `sfx_deliver` | `DELIVER_SUCCESS` | 兴庆宫荔枝交付成功 |
| `sfx_rush_horn` | `RUSH_PHASE_START` | 冲刺号角 |
| `sfx_fresh_warning` | `GOOD_TO_BAD` / `FRESHNESS_DROP` | 鲜度下降警告 |

### 1.4 UI ×1(`Audio/SFX/`)

| 文件名 | 触发 | 备注 |
|---|---|---|
| `sfx_ui_click` | `PlayUiClick()` 静态入口 | <0.2 秒极短;不受倍速静音影响 |

---

## 2. 推荐 = 31 个(21 + 10 个新文件)

以下 10 个时刻**有真实视觉演出却完全无声**,且核实既不能复用、也不能合并到现有音——必须新做。补到这一档,游戏从「半哑」到「该响的都响」。

| 新文件 | 触发 / 演出 | 时长建议 | 源码位置(已核实) |
|---|---|---|---|
| `sfx_contest_start` | `WINDOW_CONTEST_START` 对抗开局(决斗条+VS+回放降速),最高频战斗节点却无起手音 | 0.4–0.8s 起手 sting | `LycheeCombatPresentation.cs:599-649` |
| `sfx_squad_clear` | `SQUAD_CLEAR_SUCCESS` 清障成功(优先卡+镜头拉近) | 正向「过关」音 | `LycheeReplayPlayer.cs:2221-2280` |
| `sfx_squad_failed` | `SQUAD_FAILED` 小队失败(红卡+专属插画) | 负向 fail 音(与上条反向) | `LycheeReplayPlayer.cs` 失败卡分支 |
| `sfx_forced_pass_start` | `FORCED_PASS_START` 强制通行(镜头强震 `Shake(0.25,0.5)`) | 压迫感 | `LycheeCinematicDirector.cs:293-294` |
| `sfx_verify_gate_start` | `VERIFY_GATE_START` 朱雀门验核开始(读条环出现) | 庄严官审起手 | `LycheeWorldMarkerSystem.cs:192-194` |
| `sfx_banner_drop` | 结算胜负横幅下滑(0.3s) | 结算四件套·停留最久 | `LycheeSettlementScreen.cs:382-398` |
| `sfx_total_pop` | 结算总分弹出(EaseOutBack 0.45s) | 同上 | `LycheeSettlementScreen.cs:426-443` |
| `sfx_mvp_appear` | MVP 头像回弹(0.5s) | 同上 | `LycheeSettlementScreen.cs:445-464` |
| `sfx_sparkles_burst` | MVP 金色星光闪散(0.85s,10 颗) | 同上 | `LycheeSettlementScreen.cs:462-520` |
| `sfx_rush_transition` | 2.8s 全屏冲刺转场(暗角+红条带+大字震屏) | 2.8s 长 riser/whoosh 床 | `LycheeHudExtensions.cs:1454-1493` |

### 2.1 附带 2 个「零新文件」代码补丁(顺手做,不计入文件数)

| 项 | 处理 | 说明 |
|---|---|---|
| 夜风 `NIGHT_WIND` | **复用 `sfx_weather_wind`** | 第 4 种天气在 `UpdateWeatherTargets` 里完全没有分支,目前彻底静默;加一个判断分支即可 |
| UI 控制条接线 | **复用 `sfx_ui_click`** | `OnReplayClicked/OnPlayPauseClicked/OnSpeedClicked` 没调用 `PlayUiClick()`(全工程 0 调用点),核心交互无反馈;接线即可 |

### 2.2 4 项可复用 / 合并,不必单做(省文件关键)

| 想补的 | 实际处理 | 原因 |
|---|---|---|
| `sfx_obstacle_clear` | 用 `sfx_squad_clear` | 走同一条「清障成功」FX/卡/镜头路径 |
| `sfx_bar_shatter` | 用 `sfx_guard_break` | 血条碎裂逐帧驱动、无事件钩子;且 GUARD_BREAK 通常同时发生 |
| `sfx_rush_band_whoosh` | 烘进 `sfx_rush_transition` 开头 | 它就是转场前 0.45s 的子拍 |
| `sfx_rush_impact` | 已有 `sfx_rush_horn` 覆盖 | 同一 `RUSH_PHASE_START` 事件,号角已是冲击重音,**其实不哑** |

### 2.3 推荐落地顺序

1. 2 个代码补丁(几乎免费):夜风分支 + UI 接线
2. 结算四件套(玩家停留最久):`banner_drop` / `total_pop` / `mvp_appear` / `sparkles_burst`
3. 战斗 3 件 + 终局 2 件:`contest_start` / `squad_clear` / `squad_failed` / `forced_pass_start` / `verify_gate_start`
4. 冲刺转场:`rush_transition`

---

## 3. 全覆盖 ≈ 48–52 个(可选,收益递减)

补齐 manifest Section 3 的「锦上添花」项。**49 个无声时刻中约 30 个能被复用/合并/代码消化**,真正还需新做的约 18–20 个,故全覆盖落在 **21 + 10 + ~19 ≈ 50** 上下。

值得优先的两个非战斗项:
- `bgm_menu`:地图选择/锦标赛菜单**目前完全静音**(`MapSelectController` 纯 OnGUI 面板,无任何音频引用)。
- `bgm_opening`:开场演出连视频音轨都被设成 `VideoAudioOutputMode.None`。

需特别克制的高频事件(务必极轻或随 ≥4 倍速静音,否则回放变吵):
- `sfx_node_arrive`:节点抵达有 13 种子类型,建议共用一个极轻音。
- `sfx_ui_feed_slide`:左侧事件流条目滑入,频发。

可零文件消化的典型:
- `sfx_completion_pulse` → 复用 `sfx_task_complete`
- `sfx_process_complete` ↔ `sfx_process_start` → 合成一对(1 个文件)
- `sfx_menu_select` → 复用 `sfx_ui_click`
- `video_native_audio` → 仅需开启视频自带音轨(改 `audioOutputMode`)
- 运镜震动(`shake_break`/`shake_success`/`lighting_shift`)→ 并入对应事件音

---

## 4. 替换通用规则(操作前必读)

| 项 | 说明 |
|---|---|
| 放置目录 | BGM → `Audio/BGM/`;其余(含环境循环、移动音)→ `Audio/SFX/` |
| 命名 | 系统按固定 base name 自动加载,**同名覆盖即可,无需改代码** |
| 格式 | `.wav → .ogg → .mp3` 顺序探测,命中即用;换 `.ogg` 要删同名旧 `.wav` |
| 缺文件 | 任何文件缺失只静默跳过,绝不影响回放 |
| 加载 | 运行时从 StreamingAssets 异步加载(WebGL 走 UnityWebRequest) |
| 时长 | BGM/循环音首尾无缝;事件音 0.3–1.5s;`ui_click` <0.2s |

---

## 5. 一句话总结

全部替换的**必要 21 个**;想「该响的都响」做到**推荐 31 个**(+2 个免费代码补丁);追求全覆盖约 **50 个**,但后 ~19 个收益明显递减。
