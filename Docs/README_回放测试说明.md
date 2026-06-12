# 一骑红尘 · 荔枝争运战 — 回放引擎（WebGL 版）使用说明

这是 Unity 打包出来的网页版回放播放器，任何系统（Windows / Mac / Linux）有浏览器就能跑，不需要装 Unity。

## 怎么跑起来

1. 解压后进入 `LycheeReplay_WebGL_20260612` 目录（能看到 `index.html`）。
2. 在该目录启动一个静态文件服务器（WebGL 不能直接双击 index.html 打开，必须走 http）：
   ```bash
   # 任选其一
   python3 -m http.server 8000
   npx serve .
   ```
3. 浏览器（推荐 Chrome / Edge）打开 `http://localhost:8000`，等待加载完成后回放会自动播放。

## 怎么换自己的回放 txt

1. 把服务端生成的回放文件**重命名为 `reply.txt`**。
2. 覆盖 `StreamingAssets/reply.txt`（就在 index.html 旁边的 StreamingAssets 文件夹里）。
3. 刷新浏览器页面即可，加载时自带防缓存参数，不需要清缓存。

## 回放 txt 格式要求

每行一个 JSON 对象（JSON Lines），按类型识别：

- `{"type":"start", ...}` — 起始帧（队伍、初始状态）
- `{"type":"round", ...}` — 每回合一行
- `{"type":"over", ...}`  — 结算帧

可以参考包内自带的 `StreamingAssets/reply.txt` 样例。

## 常见问题

- **页面一直黑屏/转圈**：看浏览器控制台（F12），通常是没走 http 服务器直接打开了文件。
- **改了 txt 没生效**：确认文件名是 `reply.txt` 且放在 `StreamingAssets` 目录下，然后刷新页面。
- **加载很慢**：首包包含全部 3D 模型资源，体积较大，建议局域网或本机访问。
