# 荔枝回放包 · URL 参数与测试说明

> 适用版本：含「`?path=` 取不到时显式报错 + 开场字幕读实际回放」修复的构建（2026‑06‑22 晚之后）。
> 如何确认你部署的就是这一版，见 **四、确认部署的是修复版**。

---

## 一、URL 总览

```
http://<host>:3000/?path=<回放地址>&map=<地图>&quality=<画质>
```

- host 后、`?` 前**建议带 `/`**（`http://host:3000/?...`）。浏览器通常会自动补，但显式更稳。
- 三个参数都可选。**不带任何参数**时：加载默认地图（Map_medium）+ 包内自带回放。

| 参数 | 作用 | 取值 |
|---|---|---|
| `path` | 指定要加载的外部 replay.txt | 见 **二** |
| `map` | 指定加载哪张地图场景 | 见 **三** |
| `quality` | 渲染画质 | `4k` / `uhd`（超清）、`performance` / `fast`（省性能）；或用 `dpr=<0.75~4>` 直接指定像素比 |

---

## 二、`path` 怎么填 + 怎么测

回放包是浏览器里的 WebGL，**只能走 HTTP 取 replay，不能直接读服务器本地文件**。`?path=` 有两种写法。

### 写法 A：同源绝对路径（推荐，配合包内 `lychee_server.py`）

包里的 `lychee_server.py` 专门做了一件事：任何 `/data/...` 开头的 URL，直接映射到**服务器文件系统的同名绝对路径**（见其 `translate_path`）。所以：

```
?path=/data/results/LitchiDelivery/21/server/replay.txt
```

会从 `http://<host>:3000/data/results/LitchiDelivery/21/server/replay.txt` 取，再由 server 读磁盘上的 `/data/results/LitchiDelivery/21/server/replay.txt`。

**前提**：① 3000 端口跑的就是这版 `lychee_server.py`；② 那个绝对路径下**确实有这个文件**且 server 进程可读。

### 写法 B：完整 URL（跨源）

replay 若在别的主机/端口：

```
?path=http://<另一台>:<端口>/path/to/replay.txt
```

代码识别 `http://` / `https://` 开头会直接用。**跨源时那台服务器必须返回 CORS 头** `Access-Control-Allow-Origin`（允许回放包的源），否则浏览器拦截、取不到。

### 测 `path` 是否能取到（三步，在服务器上跑）

```bash
# 1) 文件在不在、可不可读
ls -l /data/results/LitchiDelivery/21/server/replay.txt

# 2) curl 看状态码（最关键）
curl -i "http://127.0.0.1:3000/data/results/LitchiDelivery/21/server/replay.txt"
#   200 + 非空 body  → 好了
#   404             → server 没把该 URL 映射到该文件（文件不在 / 跑的不是这版 server）
#   403             → 权限不够

# 3) 确认 3000 跑的是这版 server（它启动会打印 "Serving <ROOT> at http://0.0.0.0:3000/"）
ps aux | grep lychee_server
```

浏览器里直接打开那个 `.txt` 地址、能看到内容才算通。**打不开 = `path` 一定取不到**。

---

## 三、`map` 怎么加

`?map=` 指定加载哪张地图场景，可与 `path` 并用：`?map=map1&path=...`。

| `map=` 可写 | 加载的场景 |
|---|---|
| `map1` / `map_1` / `1` / `map_medium` | Map_medium（官方默认图） |
| `map2`/`map_2`/`2` … `map5`/`map_5`/`5` | Map_map2 … Map_map5 |
| `demo` | LycheeReplayDemo |
| `select` / `selector` | 地图选择界面 |

注意：
- **不带 `map` 时默认就是 Map_medium（= map1）**，所以 `&map=map1` 和不带是同一张图，加上也无妨。
- 目标场景**必须打进了构建**，否则控制台警告 `scene not in build` 并停在当前图。本次构建通常含 Map_medium / demo / select；map2~map5 是否可用取决于这次有没有打进去。
- `map=map1`（Map_medium）是默认首场景，路由判定「已是当前场景」会**直接跳过、不重载**，所以 `?path=` 照常生效，不会被 map 切换吞掉。

---

## 四、确认部署的是修复版

1. **看行为（最直接）**：故意传一个**错的** `?path=`（如 `?path=/data/不存在.txt`）。
   - **修复版**：屏幕明确报「回放加载失败：?path= 指定的回放取不到」+ URL + 原因。
   - **旧版**：什么都不说，静默显示**内置样例** —— 队名 **岭南贡队 vs 长安疾运队**、比分 **842 / 791**。看到这对固定值就是旧版（或你的 path 没取到）。
2. **看包名/时间戳**：修复包目录/zip 名形如 `LycheeReplay_WebGL_20260622_<时分>_Linux`；`index.html` 顶部注释里有 `cache bust=<时间戳>`，可 `curl http://<host>:3000/index.html | grep "cache bust"` 比对。

---

## 五、常见失败排查

| 现象 | 多半原因 |
|---|---|
| 结算/队名永远是 岭南贡队 842 / 长安疾运队 791 | `?path=` 没取到，退回内置样例（修复版改成报错） |
| 报「?path= 指定的回放取不到」 | 那个 URL 404/403/空/被 CORS 拦 —— 按 **二** 的三步查 |
| 控制台 `scene not in build` | `map=` 指了一张没打进包的图 |
| 队名对、但开场对局号/规则不对 | 旧版字幕 bug（修复版改成读实际回放） |
| 双击 index.html 打不开 | 必须经 HTTP 服务访问，不能直接双击 |

---

## 六、安全提醒（给运维）

`lychee_server.py` 的 `/data/` 映射只校验前缀就 `os.path.abspath(request_path)`，因此 `?path=/data/../etc/passwd` 会被解析成 `/etc/passwd` —— 存在**路径穿越**，整机文件可被读取。若 3000 端口外部可达，务必加「解析后路径必须仍落在某个白名单根目录内」的校验。
