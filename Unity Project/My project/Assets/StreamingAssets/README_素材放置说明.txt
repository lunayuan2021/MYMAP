《一骑红尘：荔枝争运战》回放素材放置说明
==========================================
本目录 (Assets/StreamingAssets/) 下的素材在回放运行时按需加载，全部为可选项：
文件缺失时各系统自动使用程序化兜底效果，不会报错、不影响回放本身。

【回放数据 replay.txt】
  逐行 JSON：1 行 start + 600 行 round + 1 行 over。
  当前文件为演示回放（复制自 Assets/LycheeReplayDemo/Data/reply_full_coverage_unity_v2.txt）。
  正式比赛把服务端产出的 replay.txt 放本目录替换同名文件即可，无需改动工程。

【Audio/BGM/】背景音乐（LycheeAudioDirector；WebGL 同名文件依次尝试 .wav / .ogg / .mp3）
  bgm_normal       常规阶段背景乐
  bgm_rush         抢运阶段背景乐（RUSH_PHASE_START 后切入）
  bgm_settlement   终局结算背景乐

【Audio/SFX/】音效（LycheeAudioDirector；WebGL 同名文件依次尝试 .wav / .ogg / .mp3）
  sfx_card_reveal      窗口翻牌 (WINDOW_CARD_REVEAL)
  sfx_contest_win      争夺结算 (WINDOW_CONTEST_END)
  sfx_guard_set        设卡 (GUARD_SET)
  sfx_guard_break      破卡 (GUARD_BREAK)
  sfx_squad_dispatch   小队派遣 (SQUAD_DISPATCH)
  sfx_squad_arrive     小队抵达 (SQUAD_ARRIVE)
  sfx_task_spawn       任务/悬赏出现 (TASK_SPAWN / BOUNTY_SPAWN)
  sfx_task_complete    任务/悬赏完成 (TASK_COMPLETE / BOUNTY_COMPLETE)
  sfx_verify_gate      验货完成 (VERIFY_GATE_COMPLETE)
  sfx_deliver          荔枝送达 (DELIVER_SUCCESS)
  sfx_rush_horn        抢运号角 (RUSH_PHASE_START)
  sfx_fresh_warning    鲜度告警 (GOOD_TO_BAD / FRESHNESS_DROP)
  sfx_ui_click         UI 点击（LycheeAudioDirector.PlayUiClick 静态入口）
  sfx_weather_rain     暴雨环境循环
  sfx_weather_wind     大风/浓雾环境循环
  sfx_weather_heat     酷热环境循环
  sfx_hoofbeats        马蹄声移动循环（陆路行进）
  sfx_water_row        划水声移动循环（水路行进）

【UITheme/】主题贴图（.png，缺失走程序化兜底）
  weather_rain.png             暴雨粒子贴图 (LycheeWeatherFxSystem)
  weather_fog.png              浓雾粒子贴图 (LycheeWeatherFxSystem)
  weather_heat.png             热浪粒子贴图 (LycheeWeatherFxSystem)
  convoy_dust.png              车队烟尘粒子贴图 (LycheeConvoyVfx)
  minimap_frame.png            小地图边框 (LycheeMinimapSystem)
  rush_banner.png              抢运横幅底图 (LycheeHudExtensions)
  icon_weather_hot.png         酷热天气图标 (LycheeHudExtensions)
  icon_weather_rain.png        暴雨天气图标 (LycheeHudExtensions)
  icon_weather_fog.png         浓雾天气图标 (LycheeHudExtensions)
  settlement_bg.png            结算面板背景 (LycheeSettlementScreen)
  victory_banner.png           胜利横幅 (LycheeSettlementScreen)
  icon_resource_icebox.png     冰鉴资源图标 ICE_BOX (LycheeWorldMarkerSystem)
  icon_resource_fasthorse.png  快马资源图标 FAST_HORSE (LycheeWorldMarkerSystem)
  icon_resource_shorthorse.png 短途马资源图标 SHORT_HORSE (LycheeWorldMarkerSystem)
  icon_resource_passtoken.png  通行令资源图标 PASS_TOKEN (LycheeWorldMarkerSystem)
  icon_resource_permit.png     官府文牒资源图标 OFFICIAL_PERMIT (LycheeWorldMarkerSystem)
  icon_resource_intel.png      情报资源图标 INTEL (LycheeWorldMarkerSystem)
  icon_resource_boatright.png  舟船权资源图标 BOAT_RIGHT (LycheeWorldMarkerSystem)

【Video/】关键事件短视频（LycheeVideoInterlude；缺失或解码失败会静默跳过，不阻塞回放）
  opening.mp4              开场展示（LycheeOpeningShowcase）
  contest.mp4              对抗开始 WINDOW_CONTEST_START
  rush.mp4                 冲刺开始 RUSH_START
  deliver.mp4              交付成功 DELIVER_SUCCESS
  clear_obstacle.mp4       清障 OBSTACLE_CLEAR
  break_guard.mp4          破卡 GUARD_BREAK
  squad_dispatch.mp4       小分队派出 SQUAD_DISPATCH
  resource_contest.mp4     资源争夺 RESOURCE_CLAIM
  gate_verification.mp4    宫门验核 VERIFY_GATE_START
  weather_start.mp4        天气开始 WEATHER_START
  victory.mp4              结算/胜利预留

生成于 2026-06-12 11:08:48（Tools/Lychee/Replay Upgrade 20260612/Setup，可重复执行刷新本说明）
