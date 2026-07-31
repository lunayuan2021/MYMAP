# Unity 全场景覆盖 Demo V2

这个包用于 Unity 前端实现基础地图、动态状态渲染和 Replay 播放。它不是正式比赛平衡样例，而是“前端渲染覆盖样例”。

## 文件

- `ui_map_config_unity_full_coverage_v2.json`  
  静态地图配置：80×60 底图、站点、路线、路线折线、天气区域、资源映射。

- `reply_full_coverage_unity_v2.txt`  
  600 回合完整回放：start + 600 个 round + over。

- `coverage_matrix_unity_full_coverage_v2.json`  
  覆盖矩阵：说明每个游戏场景在哪些回合出现。

- `Unity_LitchiMapTypes_Example.cs`  
  Unity C# 类型和坐标转换示例。

## Unity 渲染方式

### 1. 固定地图

启动场景时读取 `ui_map_config_unity_full_coverage_v2.json`：

- `map.data` 渲染 80×60 底图；
- `nodes` 渲染站点；
- `routePaths` 渲染路线折线；
- `weatherRegionData` 作为天气特效覆盖区域；
- `assetMapping` 绑定 Unity Prefab 名称。

### 2. 动态状态

回放时逐行读取 `reply_full_coverage_unity_v2.txt`：

- `players` 更新车队位置、状态、鲜度、果数；
- `nodes` 更新设卡、资源显隐、障碍；
- `weather` 更新天气和预告；
- `tasks` 更新皇榜任务；
- `bounties` 更新破关悬赏；
- `contests` 更新窗口争夺；
- `messages` 播放动画事件。

## 覆盖场景

本 Demo 覆盖：

1. 官道、水路、山路、支路。
2. 15 个核心站点。
3. 资源显隐和领取。
4. 4 次天气预告与天气生效。
5. 普通皇榜任务刷新和完成。
6. 任务窗口三拍争夺。
7. 小分队 WEAKEN、REINFORCE、CLEAR。
8. 设卡、风化、攻坚。
9. 破关悬赏触发与完成。
10. 强制通行。
11. 隐藏信息试探拒绝。
12. 宫宴冲刺。
13. 朱雀门验核。
14. 兴庆宫交付。
15. 最终 over 结算。

## 坐标转换

地图坐标：

```text
左上角 = (0,0)
x 向右
y 向下
```

Unity 2D：

```csharp
Vector3 GridToWorld2D(float x, float y, float tileSize) {
    return new Vector3(x * tileSize, -y * tileSize, 0);
}
```

Unity 3D：

```csharp
Vector3 GridToWorld3D(float x, float y, float tileSize) {
    return new Vector3(x * tileSize, 0, y * tileSize);
}
```
