# MYMAP 荔枝争霸赛项目归档

这是 2026-07-31 整理的可恢复项目归档，包含：

- `荔枝争霸赛/`：原始模型、图片、音视频、设计文档、地图和工具。
- `Unity Project/`：Unity 项目源码、项目内备份、历史构建产物及 Git bundle。
- `游戏引擎/`：本机工具配置和 Unity 版本/重装说明。

## 恢复项目

先安装 Git 和 Git LFS，然后执行：

```bash
git lfs install
git clone git@github.com:lunayuan2021/MYMAP.git
cd MYMAP
git lfs pull
```

使用 Unity Hub 安装以下编辑器及模块：

- Unity `6000.4.10f1`
- Revision `feeafc12a938`
- WebGL Build Support

安装完成后，用 Unity Hub 打开：

```text
MYMAP/Unity Project/My project
```

Unity 首次打开时会重新生成 `Library/` 等缓存，所需时间取决于机器性能。

## 归档说明

为了让仓库能够在 GitHub 上存储并在新电脑上恢复，以下内容没有复制进 Git：

- Unity 编辑器安装目录 `游戏引擎/6000.4.10f1/`；通过 Unity Hub 重装同版本。
- Unity 可自动重建目录：`Library/`、`Temp/`、`Obj/`、`Logs/`、`UserSettings/`、`MemoryCaptures/`、`Recordings/`。
- 原项目内部 `.git/` 对象库；原有 Git 分支和标签会单独推送到本仓库。
- `.claude/worktrees/` 中无未提交修改的临时工作树。
- macOS `.DS_Store` 和 R 会话历史 `.Rhistory`。

模型、纹理、视频、构建包和 Git bundle 等大文件由 Git LFS 管理。克隆后必须执行 `git lfs pull`。
