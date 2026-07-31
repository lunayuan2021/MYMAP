# 缺口素材生成清单

风格说明：本批 SVG 是“唐风 UI 矢量版”，用于回放事件日志、地图提示、事件卡分类框等清晰识别场景。视觉关键词为朱红漆、鎏金、墨黑内盘、绢黄高光、云纹/如意纹装饰；它们不是手绘卡牌立绘，也不替代 420x600 卡面主插画。

## 事件图标 SVG

- `UITheme/EventIcons/icon_event_match_start.svg`: 比赛开始
- `UITheme/EventIcons/icon_event_move_progress.svg`: 移动进度
- `UITheme/EventIcons/icon_event_node_enter.svg`: 进入节点
- `UITheme/EventIcons/icon_event_process_progress.svg`: 处理进度 / 漏刻读条
- `UITheme/EventIcons/icon_event_cost.svg`: 成本冻结 / 成本提交 / 成本破产
- `UITheme/EventIcons/icon_event_freshness_drop.svg`: 鲜度下降 / 好果转坏果
- `UITheme/EventIcons/icon_event_contest_swords.svg`: 抗争交叉剑 / 窗口争夺
- `UITheme/EventIcons/icon_event_clear_obstacle.svg`: 清障
- `UITheme/EventIcons/icon_event_guard_set.svg`: 设卡
- `UITheme/EventIcons/icon_event_guard_break.svg`: 破卡
- `UITheme/EventIcons/icon_event_squad_dispatch.svg`: 小分队出发
- `UITheme/EventIcons/icon_event_squad_arrive.svg`: 小分队抵达
- `UITheme/EventIcons/icon_event_squad_fail.svg`: 小分队失败
- `UITheme/EventIcons/icon_event_resource_claim.svg`: 资源领取
- `UITheme/EventIcons/icon_event_verify_gate.svg`: 宫门验核
- `UITheme/EventIcons/icon_event_forced_pass.svg`: 强制通行
- `UITheme/EventIcons/icon_event_delivery_success.svg`: 交付成功
- `UITheme/EventIcons/icon_event_rush_phase.svg`: 宫宴冲刺
- `UITheme/EventIcons/icon_event_action_invalid.svg`: 行动无效 / 服务端驳回
- `UITheme/EventIcons/icon_event_match_end.svg`: 比赛结束
- `UITheme/EventIcons/icon_event_task.svg`: 皇榜任务事件
- `UITheme/EventIcons/icon_event_bounty.svg`: 悬赏事件
- `UITheme/EventIcons/icon_event_weather.svg`: 天气事件
- `UITheme/EventIcons/icon_event_player_status.svg`: 玩家掉线 / 弃赛

## 天气粒子 PNG

- `UITheme/weather_rain.png`: 256x256，透明底，斜向雨线粒子
- `UITheme/weather_fog.png`: 256x256，透明底，软雾团粒子
- `UITheme/weather_heat.png`: 256x256，透明底，热浪扭曲粒子

## 事件卡边框 SVG

- `UITheme/CardBorders/border_event_battle.svg`: 战斗 / 对抗
- `UITheme/CardBorders/border_event_resource.svg`: 资源
- `UITheme/CardBorders/border_event_weather.svg`: 天气
- `UITheme/CardBorders/border_event_squad.svg`: 小分队
- `UITheme/CardBorders/border_event_verification.svg`: 验核 / 宫门

## 提示词文档

- `Video/video_generation_prompts.md`: 清障、破卡、小分队派出、资源争夺、宫门验核、天气开始
- `Audio/audio_generation_prompts.md`: BGM/SFX 生成提示词，未生成假音频文件

## 事件图标映射

- `UITheme/event_icon_mapping.json`: 覆盖固定事件类型、回放样例中的 `ACTION_REJECTED` / `WEATHER_*`，以及常见 invalid errorCode。
