# 战Claw多电脑聚控智能被控端

Windows x64 **被控端**管理程序。把一台电脑配置为战Claw P2P 网络中的可信执行节点，并提供状态监控、授权管理、任务审计与紧急断开。

- 单文件 EXE，自包含 .NET 8 运行时，目标机器**无需预装任何运行时**
- 跟随系统深色 / 浅色主题，品牌强调色 `#024AD8`
- 完整替代 `02-install-worker.cmd`：首次运行进入图形安装向导
- 托盘常驻，关闭窗口不停止后台 Agent

---

## 快速开始

### 使用者

1. 从 [Releases](../../releases) 下载 `ZhanClawWorker-<版本>-win-x64.exe`
2. 核对 `SHA256SUMS.txt` 中的哈希
3. **右键 → 以管理员身份运行**
4. 按向导完成安装
5. 在「状态」页复制本机 PeerID，交给主控端管理员

### 维护者

```
git clone <本仓库>
cd <本仓库>

# 放入两个必需文件
copy <发行包>\p2p-agent.exe runtime\p2p-agent.exe
copy <发行包>\swarm.key     runtime\swarm.key
git add runtime/p2p-agent.exe runtime/swarm.key
git commit -m "chore: add agent binary and swarm key"

git tag v1.0.0
git push origin v1.0.0
```

推送 tag 后，`release` 工作流自动编译并创建 Release。

> ⚠️ `swarm.key` 入库后本仓库**必须保持 Private**。详见 [`runtime/README.md`](runtime/README.md)。

---

## 功能

### 状态

运行状态、本机 PeerID（一键复制）、Agent 版本、开机任务状态、当前连接的 Peer 及连接路径（DIRECT / RELAY / SERVER_ROUTER）。启动、停止、重启后台 Agent。

未授权任何主控设备时会明确提示这是**安全的默认状态而非故障**。

### 授权管理

`allowed_peers` 白名单的增删。这是被控端唯一的远端授权边界。

- 每条可加本地备注名（不写入 Agent 配置）
- 实时显示每个已授权主控当前是否已连接
- 拒绝写入通配符 `*`
- PeerID 格式预校验，明显错误会提示
- **紧急断开全部授权**：清空白名单并重启 Agent，保留设备身份，可一键恢复

配置为静态，保存后需重启 Agent 生效，界面会明确提示。

### 任务审计

读取 `agent-command-journal.jsonl`，列出本机收到并执行过的远端任务：时间、来源设备、动作、阶段、结果、耗时、Command ID。支持关键字过滤与导出。

同屏显示 Agent 运行日志尾部（每行带本机时间戳）。

**一键诊断**：`复制诊断信息` / `保存诊断` 汇总环境、文件状态、Agent 版本、进程与端口、计划任务详情、配置（含 `allowed_peers`）、`/v1/info` 原始响应、已连接 Peer、任务记录原始行与日志尾部。私网密钥、设备私钥与 API Token 只报告存在性与大小，绝不读取内容。排障时直接把这段文本发出去即可。

> 远端 `process_execute` 拥有本机 Agent 账户的完整 PowerShell 权限。这台机器的使用者有权知道谁在什么时候让它执行了什么——这是本程序存在的主要理由。

### 设置

设备名称与标签、Bootstrap 地址、发现组、并行任务上限、单文件传输上限、开机自启、关闭行为、卸载。

---

## 安装做了什么

| 步骤 | 内容 |
|---|---|
| 1 | 创建 `C:\Program Files\P2PAgent` 与 `C:\ProgramData\P2PAgent` |
| 2 | 停止已有 Agent 实例，确保文件未被占用 |
| 3 | 释放内嵌的 `p2p-agent.exe` |
| 4 | 写入内置的 `swarm.key` |
| 5 | 复制控制软件自身到程序目录（计划任务执行的后台宿主） |
| 6 | 写入 `agent-config.json` |
| 7 | `icacls` 收紧数据目录权限至当前用户 + Administrators + SYSTEM |
| 8 | 注册登录时计划任务 `P2P Agent` |
| 9 | 启动并等待 `127.0.0.1:7432` 就绪 |

每步结果逐条显示，失败立即停止，不静默继续。

### 与官方 PowerShell 安装脚本的差异

**后台宿主模式。** `p2p-agent.exe` 是 CONSOLE 子系统程序（PE `subsystem=3`），由计划任务直接启动会在用户登录时弹出一个黑色控制台窗口——官方安装脚本存在这个现象。

本程序改为：计划任务执行 `ZhanClawControl.exe --run-agent`。本程序是 WinExe（无控制台），以 `CreateNoWindow` 拉起 Agent 并重定向其 stdout/stderr，逐行加本机时间戳写入 `C:\ProgramData\P2PAgent\logs\agent.log`。结果是**既无窗口，又有完整带时间的日志**。

日志格式：

```
2026-08-13 22:30:01.123 [host]  starting "C:\Program Files\P2PAgent\p2p-agent.exe" -config "..."
2026-08-13 22:30:03.456 [agent] p2p-agent ready: version=... PeerID=... control_api=127.0.0.1:7432
```

超过 8 MB 时在宿主启动阶段滚动一代（`agent.log.1`）。

停止操作会先 `schtasks /End`，再显式结束宿主与可能成为孤儿的 `p2p-agent.exe`。

### 字段名的来源

`/v1/info`、`/v1/peers` 与 `agent-command-journal.jsonl` 的字段名不在上游文档中，本项目从 `p2p-agent.exe` 内嵌的 Go 结构体标签（`json:"..."`）中提取后使用，并保留多候选名回退。

---

## 安全边界

本程序遵循 `ARCHITECTURE.md` §19 的四层信任边界，并额外遵守：

- **绝不显示、复制、导出** `swarm.key`、`agent-identity.key`、`agent-api.token`
- `swarm.key` 内置于安装包中，安装向导不展示其内容，仅显示「已内置」一行确认
- 只访问回环 Control API 的两个只读端点（`/v1/info`、`/v1/peers`），不调用任何 Primitive
- 拒绝把 `allowed_peers` 写成 `*`
- 界面反复区分「网络授权」与「操作确认」这两件不同的事
- 卸载默认**保留**设备身份，避免 PeerID 变更导致所有主控端需重新授权

---

## 构建

### 本地

需要 Windows + .NET 8 SDK（WPF 只能在 Windows 上编译）。

```powershell
dotnet publish src\ZhanClawControl\ZhanClawControl.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o artifacts\publish
```

### CI

| 工作流 | 触发 | 产物位置 |
|---|---|---|
| `build.yml` | 推送主分支 | **dev 预发行版**（固定链接，每次覆盖）+ Actions Artifacts（保留 14 天） |
| `build.yml` | Pull Request | 仅 Artifacts，不发布 |
| `release.yml` | 推送 `v*` tag / 手动 | **Releases** 正式版本，带版本号与 SHA256 |

### 两个发布通道

**dev 通道** —— 推主分支即自动发布，地址固定：

```
https://github.com/<owner>/<repo>/releases/tag/dev
```

文件名固定为 `ZhanClawWorker-dev-win-x64.exe`，每次覆盖上一份，不会堆积历史资产。
标记为 prerelease 且 `make_latest: false`，不会顶替正式版的 latest 位置。
版本号写作 `1.0.0-dev.<构建号>`，可在 EXE 属性里核对是哪一次构建。

**正式通道** —— 打 tag 发布：

```bash
git tag v1.0.0
git push origin v1.0.0
```

产出 `ZhanClawWorker-<版本>-win-x64.exe`、`.zip` 与 `SHA256SUMS.txt`。

`release.yml` 从 tag 名解析版本号并注入程序集版本；带 `-` 的版本（如 `v1.1.0-rc1`）自动标记为 prerelease。

### 产物命名

| 项 | 值 |
|---|---|
| 可执行文件 | `ZhanClawWorker-<版本>-win-x64.exe` |
| 压缩包 | `ZhanClawWorker-<版本>-win-x64.zip` |
| 校验文件 | `SHA256SUMS.txt` |

产物文件名使用纯 ASCII，避免中文在下载 URL 中被百分号编码，也便于 `curl` / `Invoke-WebRequest` 等脚本化分发。
Release 标题、说明与程序界面显示名称仍为「战Claw多电脑聚控智能被控端」。

改名只需修改 `src/ZhanClawControl/Services/AppInfo.cs`（界面显示名）、
`ZhanClawControl.csproj` 的 `Product` / `AssemblyTitle`（程序集元数据），
以及 `release.yml` 中的 `$baseName`（产物文件名）。

---

## 目录结构

```
.github/workflows/     构建与发布流水线
runtime/               p2p-agent.exe 与 swarm.key（均需入库）
src/ZhanClawControl/
  Themes/              Light / Dark / Controls 资源字典
  Services/            路径、主题、配置、Control API、计划任务、安装、审计、日志
  Models/              授权条目
  ViewModels/          状态、授权、审计、设置、向导
  Views/               主窗口、四个页面、安装向导
  Infrastructure/      MVVM 基础与转换器
```

零 NuGet 依赖，只用 .NET 共享框架。

---

## 主题

`#024AD8` 用于强调色。深色主题下该色与背景对比度不足（约 2.6:1），因此深色主题使用同色相提亮版本 `#3D7BEA` 作为强调色，其余配色遵循 Windows 原生语义。

主题跟随系统：读取 `HKCU\...\Themes\Personalize\AppsUseLightTheme`，监听 `WM_SETTINGCHANGE` / `ImmersiveColorSet` 热切换，并通过 `DwmSetWindowAttribute` 同步标题栏。

---

## 已知限制

- 仅 Windows x64；WPF 无法在 Linux/macOS 上编译，CI 必须使用 `windows-latest`
- 字段名虽取自二进制中的结构体标签，但**具体哪些字段出现在哪个响应里**仍属推断，因此保留多候选回退；若 Agent 升级后字段变更，在 `ControlApiClient` / `JournalService` 的候选列表中补一项即可
- 程序清单为 `asInvoker`（后台宿主模式必须能以标准权限启动），GUI 模式启动时自行以 `runas` 提权
- 单文件 EXE 体积约 90–120 MB（自包含运行时 + 内嵌 Agent，已启用压缩）
- 安装包内置 `swarm.key`，因此**发布的 EXE 本身即包含私网准入密钥**，请按内部软件对待，不要公开分发。上游 `OPENCLAW_INTEGRATION.md` 要求「发行包不得包含 swarm.key」，本项目为实现「用户免选」有意偏离该约定
