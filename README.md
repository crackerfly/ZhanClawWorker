# 战 Claw 被控端

Windows 11 x64 被控端管理程序，用于安装和管理随发行包提供的 `p2p-agent.exe`。界面提供本机状态、主控 PeerID 白名单、任务 journal、运行日志、配置和卸载入口。

> 本仓库包含 WPF 管理器、安装器和 Agent 宿主的 C# 源码；不包含 `p2p-agent.exe` 的 Go 源码。管理器可以校验、安装和观测该二进制，但不能从本仓库证明或修改二进制内部的命令执行 deadline、ACK 重试、协议鉴权与任务策略。

## 界面与语言

- 视觉遵循 [Vercel design.md](https://vercel.com/design.md) 的克制、直接和任务优先原则：连续画布、清晰层级、低装饰、颜色只用于操作与状态。
- 品牌色为 `#024AD8`；深色主题使用同色相的高对比变体显示文字与控件状态。
- 跟随 Windows 11 应用深色/浅色主题，并同步窗口标题栏。
- 提供简体中文、繁體中文和 English；首次启动按原交互用户的 Windows 显示语言选择，也可在“设置”中即时切换。
- 界面图标来自 [Phosphor Icons](https://phosphoricons.com/) Core 2.1.1；导航使用 Duotone 图标。许可证见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。

## 运行时载荷

构建需要以下文件：

| 文件 | 用途 | 构建要求 |
|---|---|---|
| `runtime/p2p-agent.exe` | 实际 Agent | 必须存在并通过清单校验 |
| `runtime/swarm.key` | libp2p 私网准入密钥 | 必须存在且符合三行 pnet 格式 |
| `runtime/payload-manifest.json` | 冻结 Agent 哈希、随哈希审查的版本元数据、PE 形态和签名者 | 必须存在 |

本次随附 Agent 的冻结值为：

- SHA-256：`a2b36af5f2623ddd2f91d223f471abe9d8d957fb2dca6a566e02b2dbd04dd5e9`
- 版本：`0.1.0-integration.4`
- 平台：AMD64、Console 子系统
- Authenticode 签名者：`StarSoftComm(China) Ltd.`（同时固定证书/公钥指纹）

安装器在覆盖现有 Agent 前校验精确 SHA-256、PE 形态和 Authenticode 固定值；该哈希唯一标识已审查的二进制字节。上述版本号是与该哈希绑定的发布元数据，管理器不会为核验它而以管理员身份执行 Agent。正式发布流水线要求使用 `CODE_SIGN_PFX_B64` 和 `CODE_SIGN_PFX_PASSWORD` 为最终单文件 `ZhanClawControl.exe` 做 Authenticode 签名；缺少或校验失败时不发布。本地构建默认不会自动签名，不应当作正式产物分发。

`swarm.key` 被嵌入单文件安装器，属于敏感准入材料。仓库和生成的 EXE 必须按内部机密软件处理，不能公开分发。升级或修复会保留并校验本机已有的 key，不会静默替换它。更多说明见 [`runtime/README.md`](runtime/README.md)。

## 核心行为

### 状态

- 分开显示 Agent 进程、本机 Control API、计划任务和当前 P2P 连接。
- 分开显示“配置中的授权”和“最近一次经健康检查确认生效的授权”。配置变更或核验失败时显示“待核验”，不会把写入配置误报为已生效。
- 启动、停止和重启都检查操作结果；启动前拒绝执行定义不匹配的同名计划任务。

### 授权管理

- `allowed_peers` 只接受完整的 base58btc libp2p PeerID，拒绝 `*`、截断值、非字符串和重复项。
- 白名单是重要的来源授权边界，但不是整个系统唯一的安全边界；私网准入、协议来源校验和 Agent 内部策略仍需同时成立。
- 本管理器不提供逐任务本机审批框；请求是否被接受或执行、以及如何使用 Agent 账户权限，由已部署 Agent 的实际版本与策略决定。
- “断开全部授权”先保存本机备份，再清空配置并验证 Agent 重启。只有重启和本机鉴权 API 健康检查都成功后，界面才确认运行时授权已清空。

### 任务审计与诊断

- journal 阅读器保留无法解析的行，并单独显示解析错误；若记录含 `acknowledged`，以独立 ACK 列显示。ACK 是记录快照，不等同于命令成功。
- 日志读取区分“未生成、为空、读取失败”；Agent 正在写入时拒绝清空，避免损坏活动日志。
- 默认诊断会哈希设备名、账户、PeerID 和 Command ID，并省略原始 journal、Agent 日志、bootstrap 地址和业务输出；不会读取私网密钥、设备私钥或 API Token 的内容。

## 安装与安全边界

安装目标：

- 程序：`C:\Program Files\P2PAgent`
- 运行数据：`C:\ProgramData\P2PAgent`
- 登录任务：`P2P Agent`
- 本机 API：`127.0.0.1:7432`

安装流程先验证载荷和输入，再停止旧实例、部署文件、写配置、注册任务，并等待本机鉴权 API 就绪。失败会尝试恢复程序、配置、key、任务定义与原运行状态；若回滚不能完全确认，界面会报告失败并保留恢复材料供人工处理。

安全措施包括：

- 程序目录为 Administrators/SYSTEM 可写、普通用户只读执行；数据目录仅 Agent 运行账户、Administrators 和 SYSTEM 可访问。
- 拒绝已知重解析点，并对已有敏感文件逐项检查 owner/DACL 后再接管。
- 计划任务校验账户、触发器、动作、参数、工作目录、权限级别和关键设置；定义不匹配时不启动。
- 进程操作仅针对安装路径精确匹配的宿主和 Agent，不按进程名误杀其他程序。
- GUI 提权前保留原交互用户 SID，用它选择任务运行账户、主题和自动语言，避免凭据式 UAC 错用管理员账户偏好。

Windows 路径安全存在不可被普通 path API 完全消除的检查/使用竞态，因此发布前仍需在 Windows 11 上用标准用户与管理员并发场景做真机验证。本项目不把静态检查表述为绝对的本地提权防护。

## 使用

1. 下载正式 `ZhanClawWorker-<version>-win-x64.exe` 和 `SHA256SUMS.txt`。
2. 核对 SHA-256。
3. 以管理员身份运行。
4. 在向导中确认设备名、Agent 运行账户和主控 PeerID。
5. 安装完成后，在“状态”中复制本机 PeerID 给主控端管理员。

关闭管理窗口可隐藏到通知区域，不会停止后台 Agent；需要停止接收新任务时，应在“状态”页停止 Agent。

## 构建与校验

WPF 程序只能在 Windows 上运行。启用 `EnableWindowsTargeting` 后可以在其他系统交叉编译，但 Authenticode、ACL、计划任务、UAC、通知区域和深浅主题必须在 Windows 11 真机验证。

Windows PowerShell：

```powershell
./scripts/Test-Payload.ps1 -RequireSwarmKey

dotnet publish src\ZhanClawControl\ZhanClawControl.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o artifacts\publish
```

CI 在构建和正式发布前运行载荷校验。主分支构建使用最小 `contents: read` 权限；写 Release 的 job 单独申请 `contents: write`。如需用 Secret 注入 key，`SWARM_KEY_B64` 只暴露给解码步骤。

## 发布前 Windows 11 检查

- 浅色、深色、高 DPI、键盘焦点与三种语言布局。
- 标准用户启动、同账户 UAC、凭据式 UAC 与多用户主题/语言。
- 首装、升级、修复、取消中断、回滚、任务被篡改和卸载残留。
- Agent 签名链、固定证书/公钥、payload 精确哈希，以及与该哈希绑定的版本元数据。
- `allowed_peers` 为空、配置待生效、重启失败、紧急断开失败与恢复。
- 活动日志、损坏 journal、ACK 缺失/false、默认脱敏诊断。

## 已知源码边界

以下行为属于上传的 `p2p-agent.exe`，附件没有相应源码，本仓库无法从静态 C# 审查中修复或证明：

- 远程 `process_execute` 的具体超时/deadline；
- 结果 ACK 的发送、重试和持久化时序；
- Primitive 的权限检查、来源鉴权和执行沙箱；
- Agent 对所有配置字段的最终语义。

要继续处理这些问题，需要提供与当前二进制 `0.1.0-integration.4` 对应的 Agent 源码和协议测试。
