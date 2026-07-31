# Unity 地图生成质量规范

适用项目：《一骑红尘：荔枝争运战》

适用范围：所有由 `map_config.json` 生成的 Unity 观战地图，包括当前 `map_1` 以及后续多张地图。

本规范的目标不是记录某一张地图的手工修补方式，而是定义一套可复用的地图生成契约。后续新增 4 张或更多 `map_config` 时，应由生成器自动根据地图语义生成官道、山路、水路、支路、节点和环境，不应每张图单独调材质、河床、水体或山体。

---

## 1. 核心原则

1. `map_config.json` 是地图事实来源。
   - 节点坐标、路线类型、路线折线、节点类型、天气区域、资源点和处理点都以当前局配置为准。
   - Unity 场景不能用旧母版残留对象覆盖当前 `map_config` 的语义。

2. 生成器必须按语义生成地形和视觉。
   - `ROAD` 必须看起来像官道。
   - `WATER` 必须看起来像河道。
   - `MOUNTAIN` 必须看起来像山路。
   - `BRANCH` 必须看起来像支路。

3. 优先使用 Unity 项目现有素材包。
   - 不优先使用纯色临时材质。
   - 不用大面积半透明色块代替水、河床、山地或道路。
   - 草地、砂石、岩石、水体、树木、桥、山体尽量引用现有素材包。

4. 每张地图生成后必须可自动验证。
   - 生成报告、近景截图、Game View 截图、WebGL 验证都应形成固定流程。
   - 不允许只看 Scene View 主观判断。

---

## 2. 当前可用素材清单

以下素材应作为生成器优先引用对象。若某个资源缺失，生成器可以 fallback，但必须在报告里写明。

| 用途 | 优先资源 |
|---|---|
| 草地 / 基础地表 | `Assets/Pure Poly/Free Low Poly Nature Pack/Materials/PP_Ground.mat` |
| 精细混合地表 | `Assets/LycheeMap15/Materials/M_Terrain_Refined.mat` |
| 砂石河床 | 生成器创建 `Assets/LycheeMap15/Materials/M_Config_Riverbed_YellowSandPebbles.mat`，贴图引用 Pure Poly pebble 贴图 |
| 官道路面 | 生成器创建 `Assets/LycheeMap15/Materials/M_Config_OfficialRoad.mat`，贴图引用 ALP `Ground003` |
| 山路路面 | 生成器创建 `Assets/LycheeMap15/Materials/M_Config_MountainRoute.mat`，贴图引用 ALP `GroundPatchCracked01` |
| 支路路面 | 生成器创建 `Assets/LycheeMap15/Materials/M_Config_BranchRoute.mat`，贴图引用 ALP `Ground001` |
| 水体材质 | 生成器创建 `Assets/LycheeMap15/Materials/M_Config_RiverWater_Clear.mat`，优先使用 `LycheeMap/DepthCorrectRiverWater` shader |
| 水流高光 | 生成器创建 `Assets/LycheeMap15/Materials/M_Config_RiverWater_Current.mat` |
| 备用水体 | `Assets/IgniteCoders/Simple Water Shader/Resources/Water_mat_01.mat` / `Water_mat_02.mat` / `Water_mat_03.mat`，或 `PP_Water.mat` |
| 湖岸 / 河岸地块 | `Assets/Pure Poly/Free Low Poly Nature Pack/Prefabs/PP_Lake_Ground_04.prefab` |
| 桥体 | `PP_Bridge_15_Left.prefab` / `PP_Bridge_15_Middle.prefab` / `PP_Bridge_15_Right.prefab` |
| 山体 | `Assets/FreeMountain/Prefabs/Free_Mountain.prefab` |
| 山地地形 | `Assets/Hill Rock Mountain Terrain/Prefab/mountain_terrain_02.prefab` |
| 岩石 | `rock_set_01.prefab` 到 `rock_set_04.prefab` |
| 低多边形岩石 | `PP_Rock_Moss_Grown_09.prefab` / `PP_Rock_Moss_Grown_11.prefab` |
| 树木 | `Tree_Packs/URP_Tree_Pack/Prefabs/URP_Tree_*.prefab` |
| 草丛 | `PP_Grass_11.prefab` / `PP_Grass_15.prefab` |

旧的 `M_OfficialRoad.mat`、`M_MountainRoute.mat`、`M_BranchRoute.mat`、`M_RiverBed_SandGravel.mat` 只能作为参考，不再作为生成器优先材质。生成器必须优先创建可控 URP/PBR 材质，并显式绑定贴图与 shader，避免 batch、WebGL 或导入状态变化时出现纯色、黑色或错误材质。

---

## 3. `map_config` 语义契约

生成器至少要读取以下信息：

| 字段 | 用途 |
|---|---|
| `grid.width` / `grid.height` / `grid.tileSize` | 世界坐标转换、地形尺寸 |
| `nodes[]` | 站点锚点、建筑类型、停靠点、事件点 |
| `edges[]` | 路线连接、逻辑距离、路线类型 |
| `routePaths[]` | 路线视觉折线、地形挖/抬/压平路径 |
| `layers.terrainData` | 基础地形区域语义 |
| `layers.objectData` | 节点和对象占位 |
| `layers.weatherRegionData` | 天气区域显示 |
| `gameplay.processNodes` | 特殊处理点，如登船、水路换运、宫门验核 |
| `gameplay.resources` | 资源点表现 |
| `gameplay.routeTaskBuckets` | ROAD / WATER / MOUNTAIN 任务路线语义 |

当前地图中 `terrainData` 的主要编码应按如下语义使用：

| 编码 | 语义 |
|---:|---|
| `0` | 普通草地 / 基础地表 |
| `1` | 官道区域 |
| `2` | 水路 / 河道区域 |
| `3` | 山地 / 山路区域 |
| `4` | 策略支路区域 |

注意：路线视觉不能只依赖 `terrainData`。真正的路线应由 `edges[].routeType` 和 `routePaths[]` 决定。`terrainData` 是区域语义补充，不是唯一来源。

---

## 4. 生成器架构要求

推荐采用“语义生成器”流程，而不是继续在旧母版上反复补丁。

### 4.1 输入阶段

1. 加载 `map_config.json`。
2. 校验节点、路线、路线折线完整性。
3. 建立以下索引：
   - `nodeId -> node`
   - `edgeId -> edge`
   - `edgeId -> routePath`
   - `routeType -> routePaths`
   - `stationType -> nodes`

### 4.2 语义遮罩阶段

基于所有 `routePaths` 生成连续遮罩：

| 遮罩 | 来源 | 用途 |
|---|---|---|
| 官道遮罩 | `routeType = ROAD` | 地形压平、路基、主路材质 |
| 水路遮罩 | `routeType = WATER` | 河道下挖、河床、水面、河岸 |
| 山路遮罩 | `routeType = MOUNTAIN` | 山脊、坡地、岩石、栈道 |
| 支路遮罩 | `routeType = BRANCH` | 窄土路、轻量路基、转线 |
| 节点遮罩 | `nodes[]` | 建筑平台、停靠点、事件点 |

遮罩应支持距离衰减，不能只按离散格子处理，否则会出现断裂和锯齿。

### 4.3 地形阶段

地形高度必须由“基础自然地形 + 语义修正”生成。

| 语义 | 高度规则 |
|---|---|
| 普通草地 | 保留自然起伏，使用草地/湿地/林地材质 |
| 官道 | 沿路线中心压平，边缘缓慢过渡到草地 |
| 水路 | 沿路线中心下挖 U 形河谷，河岸自然过渡 |
| 山路 | 沿路线附近抬升为坡地/山脊，不得完全平坦 |
| 支路 | 轻微压实和窄路基，保留自然起伏 |
| 节点 | 建筑脚下平台局部平整，不能浮空或被淹 |

---

## 5. 路线视觉规则

### 5.1 官道 ROAD

官道是稳定主路，视觉应平整、连续、可读。

必须满足：

- 使用明确的道路材质，优先 `M_OfficialRoad.mat`。
- 路面宽度稳定，不忽宽忽窄。
- 有路肩或砂石边界。
- 地形沿路压平，但不能变成灰色平板。
- 车辙、路边碎石、少量草过渡可以存在。
- 不允许水面、草丛或旧河床覆盖官道。

推荐生成：

- 主路面 ribbon。
- 路基 underlay。
- 左右车辙。
- 两侧碎石/土边。
- 适量道路旁草和石子，不压到路面上。

### 5.2 水路 WATER

水路必须像河道，不是蓝色贴片。

必须满足：

- 地形向下挖出连续河槽。
- 河床使用砂石/水底材质，禁止草皮露在水面下。
- 水体使用真实水材质，优先 Simple Water Shader。
- 水面有宽度、有深度、有高光或反射。
- 河岸从水体到草地自然过渡。
- 河道不能过宽到像色块，也不能窄到看不出水。
- 水路端点和水驿/码头必须自然接驳。

推荐生成层级：

1. `RiverCut`：地形下挖。
2. `RiverBed`：连续砂石河床，宽于水面，必须遮住水底草皮。
3. `WaterSurface`：真实水体材质，独立于河床高度，不与河床共用高度偏移。
4. `WaterCurrent`：可选窄高光带，用于表达水流方向和水面细节，不能代替水体。
5. `BankLeft/BankRight`：两侧河岸，可通过河床宽度和地形过渡表达。
6. `Dock/Bridge/WaterStationPlatform`：水边节点结构。

当前生成器落地参数：

| 项目 | 当前值 | 约束 |
|---|---:|---|
| 河床 ribbon 宽度 | `3.72` | 必须大于水面宽度，避免水面下露草 |
| 水面 ribbon 宽度 | `2.26` | 应看起来像可通行水路，不得窄成路线线条 |
| 水流高光宽度 | `0.16` | 只做水面细节，不承担主水体 |
| 水面高度偏移 | `+0.46` | 高于河床和地形，避免被 ZTest 吃掉 |
| 河床高度偏移 | 约 `+0.08` | 贴近地形，只覆盖草皮，不应漂浮 |
| 水体 shader | `LycheeMap/DepthCorrectRiverWater` | 透明、双面、不写深度 |

水面和河床必须解耦：调整水面高度时，不能把河床一起抬高成橙色平台。

禁止：

- 只生成一条透明蓝 ribbon。
- 水面下方仍是草地。
- 水驿被水面盖住。
- 用一个大矩形补片盖住建筑或道路。

### 5.3 山路 MOUNTAIN

山路必须有山势。

必须满足：

- 山路附近地形抬升，不是平地贴图。
- 有坡面、岩石、山地材质。
- 关键节点如五岭山道、秦岭栈道应显得在山区。
- 山路可窄、崎岖，但必须连续可读。
- 不能让树、石头挡住路线。

推荐生成：

- 山路中心线附近抬坡。
- 两侧放置岩石、山体或 cliff prefab。
- 关键节点周围放山体背景，但保留建筑和车辆空间。
- 山路材质优先 `M_MountainRoute.mat`，山体材质优先 Hill Rock / FreeMountain。

### 5.4 支路 BRANCH

支路用于转线和策略交锋，不是第四条主收益路线。

必须满足：

- 比官道窄。
- 土路/支线路感明确。
- 不能像水路或山路。
- 与主路、水路交汇处不能断。

推荐生成：

- 窄路面。
- 轻路基。
- 少量草边和泥土过渡。

---

## 6. 节点视觉规则

### 6.1 水边节点

适用：

- `DOCK`
- `WATER_STATION`
- 与 `WATER` 路线相连的站点

必须满足：

- 建筑脚下高于水面。
- 站点与水路之间有岸线、码头或平台关系。
- 水体贴近但不吞没建筑。
- 水路在节点附近不能断成碎片。

`S05 洞庭水驿` 专项：

- 必须表现为水边驿站。
- 三条水路交汇时不能用大色块盖住中心。
- 推荐生成一个小型临水平台或码头接驳结构。
- 建筑底部必须高于最高水面，且保留干地/平台。

### 6.2 山地节点

适用：

- `MOUNTAIN_NODE`
- `MOUNTAIN_PASS`
- 与 `MOUNTAIN` 路线相连的站点

必须满足：

- 节点周围有坡地或岩石背景。
- 建筑脚下可局部平整，但外圈应回到山地。
- 山路接入节点时不能穿模或悬空。

### 6.3 关隘 / 宫门 / 终点

适用：

- `KEY_PASS`
- `PASS`
- `GATE`
- `FINISH`

必须满足：

- 建筑和道路关系清楚。
- 关隘处可更窄、更适合设卡表现。
- S14 / S15 需要正式终点观感，不应只是普通节点。

---

## 7. 多地图生成要求

后续如果有 4 张不同 `map_config`，应满足：

1. 每张地图只改 `map_config`，不写每张地图专属 C# 分支。
2. 生成器按 `routeType` 和 `stationType` 自动应用规则。
3. 每张地图生成后输出独立：
   - Unity scene
   - map config archive
   - mesh assets
   - generation report
   - top/iso/near screenshots
4. Build Settings 只纳入正式地图场景，不默认进入 demo。
5. URL 用 `?map=map_1` / `?map=map_2` / `?map=map_3` / `?map=map_4` 切换。

禁止：

- 每张图手动拖材质。
- 每张图手动挖河。
- 每张图手动抬水驿。
- 每张图单独调摄像机边界。

---

## 8. 自动验收清单

每张地图生成后必须跑以下检查。

### 8.1 配置检查

- `nodes[]` 无重复。
- `edges[]` 端点存在。
- 每条 `edge` 有对应 `routePath`。
- ROAD / WATER / MOUNTAIN 三条主路线存在。
- S01 / S14 / S15 固定身份正确。
- `gameplay.processNodes` 引用节点存在。
- `gameplay.resources` 引用节点存在。
- `weatherRegionData` 有 HOT / HEAVY_RAIN / MOUNTAIN_FOG 所需区域。

### 8.2 场景对象检查

- 每条 edge 有一个 `MapRouteEdge`。
- 每条 routePath 有对应 pathPoints。
- 每条 WATER 有：
  - `RiverBed`
  - 水面 `Route`
  - `WaterCurrent`
  - `Bank` 或等价岸线/砂石过渡
- 每条 MOUNTAIN 附近有山地/岩石装饰。
- 每个 WATER_STATION 高于水面。
- 不存在旧的 `Patch_*_Junction` 大色块补片。

### 8.3 材质检查

- 水体材质来自生成器创建的 `M_Config_RiverWater_Clear.mat`，或 Simple Water Shader / 明确水体素材。
- 河床材质来自生成器创建的 `M_Config_Riverbed_YellowSandPebbles.mat` 或砂石/水底素材。
- 官道、山路、支路使用生成器创建的 `M_Config_OfficialRoad.mat`、`M_Config_MountainRoute.mat`、`M_Config_BranchRoute.mat`。
- 禁止交付场景里大面积路线材质为默认白/灰材质。

### 8.4 截图检查

每张地图至少输出：

- 全图俯视：检查拓扑完整。
- 水路近景：检查水体、河床、河岸。
- 山路近景：检查坡地、岩石、山路。
- 官道近景：检查主路清晰度。
- 水驿近景：检查水边节点关系。
- Game View 截图：检查实际运行相机看到的画面。

验收失败时，不进入 WebGL 构建。

### 8.5 当前 batch 验证口径

当前 Unity Editor 的 MCP 连接不稳定时，地图生成验证以临时 batch 工程为准。

推荐命令：

```bash
rsync -a --delete --exclude Library --exclude Temp --exclude Logs --exclude Builds \
  "/Users/lunayuan/Documents/Unity Project/My project/" /tmp/lychee_unity_mapcheck/

"/Users/lunayuan/Documents/游戏引擎/6000.4.10f1/Unity.app/Contents/MacOS/Unity" \
  -batchmode -quit \
  -projectPath /tmp/lychee_unity_mapcheck \
  -executeMethod LycheeConfiguredMapBatch_20260622.BuildMap1AndShot \
  -logFile /tmp/lychee_mapcheck_batch.log
```

验收截图：

`/tmp/lychee_unity_mapcheck/LycheeConfiguredMap_focus.png`

当前已验证项目：

- `E12` / `E13` / `E19` 均生成 `RiverBed`、水面 `Route`、`WaterCurrent`。
- `S05 洞庭水驿` 高于水面，没有被水淹。
- 水路下方不再露出草地河床。
- 错误材质和彩色棋盘材质不应出现在水路近景截图中。

---

## 9. 摄像机与构建关系

地图生成和摄像机是两个阶段，但必须协同。

摄像机不得用“全地图概览”掩盖地图质量问题。

要求：

- 开场不拉到全图。
- 不显示地图边界。
- 只框住当前主队、对方主队、小分队、当前事件点和直接相关节点。
- 地图生成器应提供 playable bounds，但相机不能默认拉满 bounds。
- WebGL 构建脚本必须同步当前相机参数，不能使用旧镜头。

---

## 10. WebGL 交付要求

每次正式交付包必须包含：

- `index.html`
- `Build/`
- `StreamingAssets/`
- `TemplateData/`
- `README_使用说明.txt`
- `README_usage.txt`
- Linux 启停脚本
- Windows 启停脚本

必须支持：

- 默认进入 `map_1`。
- `?path=/data/results/.../serve/replay.txt` 加载外部回放。
- `?map=map_1` / `?map=map_2` 切换地图。
- 离线静态资源运行，不依赖外网。
- 正式加载页文案：
  - `荔枝争霸赛`
  - `正在加载游戏资源...`
  - `Loading game assets...`
  - `正在准备地图、队伍与事件回放`

---

## 11. 推荐实施路线

### 第一阶段：重建语义生成器

目标：用当前 `map1_config.json` 生成一版干净 `Map_medium.unity`，不依赖旧场景残留。

需要实现：

- 语义遮罩系统。
- ROAD / WATER / MOUNTAIN / BRANCH 地形规则。
- 水路真实水体和河床。
- 水边驿站平台。
- 山路山体/岩石装饰。
- 截图和报告。

### 第二阶段：接入回放场景

目标：生成地图能被回放播放器使用。

需要保留：

- `MapStationAnchor`
- `MapRouteEdge`
- `MapElevationData`
- `LycheeReplayPlayer.streamingReplayPath`
- HUD / minimap / camera / event systems

### 第三阶段：多图批量生成

目标：4 张 `map_config` 一次性生成 4 张地图。

输出：

- `Map_medium.unity`
- `Map_map2.unity`
- `Map_map3.unity`
- `Map_map4.unity`
- 每张地图截图和报告

### 第四阶段：WebGL 构建验收

目标：统一打包，支持地图切换和外部 replay path。

---

## 12. 完成定义

一张地图可以进入交付构建，必须同时满足：

1. 生成报告通过。
2. 近景截图通过。
3. Game View 截图通过。
4. 水路没有草皮河床。
5. 水体不是大色块。
6. 山路有明确山地语义。
7. 官道清晰、平整、连续。
8. 水驿不被淹，且像水边驿站。
9. WebGL 包里实际看到的画面与 Game View 一致。
10. 不需要对该地图做手工材质/河床/山路补丁。
