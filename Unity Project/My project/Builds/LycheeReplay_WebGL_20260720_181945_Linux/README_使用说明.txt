一骑红尘：荔枝争运战 WebGL 构建包使用说明
==============================

不要直接双击 index.html，也不要只拷贝 Build 目录。请把整个文件夹原样放到黄区服务器或用本地 HTTP 服务打开。

目录必须包含：
- index.html
- Build/
- StreamingAssets/
- TemplateData/

推荐访问方式：
- 打开回放：http://服务器地址/本目录/index.html
- 指定外部回放：http://服务器地址:9091/litchi_delivery_replay/?path=/data/results/game/replay/serve/replay.txt
- 指定包内示例：http://服务器地址:9091/litchi_delivery_replay/?path=/StreamingAssets/Tournament/map1.txt
- 指定地图：http://服务器地址:9091/litchi_delivery_replay/?map=map_1
- 地图选择页：http://服务器地址:9091/litchi_delivery_replay/?map=select
- 指定地图 + 外部回放：http://服务器地址:9091/litchi_delivery_replay/?map=map_1&path=/data/results/game/replay/serve/replay.txt

替换回放文件（StreamingAssets/replay.txt）：
- 文件名必须全小写 replay.txt（Linux 服务器区分大小写，Replay.txt / replay.TXT 都读不到）。
- 替换后确认文件可读：执行 chmod 644 StreamingAssets/replay.txt（用 sudo/sftp 上传的文件常是 600 权限，服务器会回 404，页面显示「读不到回放文件」）。
- Mac 上别直接从「下载」目录拷文件：下载来的文件可能带系统锁（com.apple.macl），本地服务器读不了；先把文件挪到家目录再拷入包内。
- 替换无需重启服务，刷新页面即可生效（客户端每次都带时间戳参数取最新文件）。

离线使用：
- 本包已包含 Build、StreamingAssets、UITheme、Video、默认 replay.txt，不需要访问外网。
- 默认地址 http://127.0.0.1:9091/litchi_delivery_replay/ 读取包内 StreamingAssets/replay.txt。
- ?path=/data/results/.../replay.txt 读取的是 Linux 本机磁盘上的 /data 路径，不是外网请求。
- 只有 ?path=http://... 才需要机器能访问对应 HTTP 地址；离线机器不要用 http://... 形式。

本机临时测试：
- Linux：执行 ./start.sh；默认端口 9091，访问路径前缀 /litchi_delivery_replay/，HOST=0.0.0.0，NO_OPEN=1 可只启动不打开浏览器。
- Windows：双击 start.bat 或 start_local_server.bat；脚本会优先使用 PowerShell 内置服务器。

常见卡住原因：
- 双击 index.html：浏览器会阻止 StreamingAssets 文本读取，回放无法启动。
- 只上传 Build/：缺少 StreamingAssets/Tournament/map1.txt，Unity 能开但车队/镜头不会推进。
- 黄区服务器缓存旧包：地址可加 ?v=日期，或清掉浏览器缓存后重试。
- 机器性能不足：首次加载约 500MB WebGL 资源，内存不足时可能加载很久或浏览器终止页面。
- 回放文件放在网络盘/U盘：读取慢会触发下载超时（本包客户端超时 60 秒），把整个包拷到本机硬盘再起服务。

排查日志（报「找不到 replay.txt」时看这里）：
- 用 start.sh / start.bat 起的本地服务器会在包根目录写 .lychee_web_server_requests.log，每个请求一行。
- 找带 404 的行，箭头(->)后是服务器实际去找的磁盘路径：路径目录不对=起错目录/旧服务器串台；路径对仍 404=文件真名不是 replay.txt（Windows 隐藏扩展名，常是 replay.txt.txt）。
- 页面上的红色报错框也会显示「加载记录」，含 HTTP 状态码：404=文件名/权限，error=timeout=太大太慢，200 bytes=0=空文件。

画质参数：
- 默认：跟随屏幕 DPI，最高 DPR 2，比旧包强制 DPR 1 更清晰。
- 4K/UHD 验收：地址后加 ?quality=4k；如果已有 ?path=...，则追加 &quality=4k；也可显式加 ?dpr=2 或 ?dpr=3。
- 性能优先：地址后加 ?quality=performance 或 ?dpr=1。远程桌面/低配 Linux 机器优先用这个。

地图切换说明：
- 当前包默认包含 demo、map_1、select；只有打进 Build Settings 的地图才能通过 ?map=... 加载。
- 后续多图要先生成 Map_map2/Map_map3 等场景，把场景加入构建，再访问 ?map=map_2、?map=map_3。

本包 Build Settings 场景：
Assets/Scenes/GeneratedMaps/Map_map4.unity
Assets/Scenes/GeneratedMaps/LycheeReplayDemo.unity
Assets/Scenes/GeneratedMaps/Map_medium.unity
Assets/Scenes/GeneratedMaps/Map_map2.unity
Assets/Scenes/GeneratedMaps/Map_map3.unity
Assets/Scenes/GeneratedMaps/LycheeMapSelect.unity
