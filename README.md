# 战 Claw 被控端

Windows 11 x64 被控端管理程序，用于安装和管理随发行包提供的 `p2p-agent.exe`。界面提供本机状态、远端来源 PeerID 白名单、任务 journal、运行日志、配置和卸载入口。

> 本仓库包含 WPF 管理器、安装器和 Agent 宿主的 C# 源码；不包含 `p2p-agent.exe` 的 Go 源码。管理器可以校验、安装和观测该二进制，但不能仅凭这些 C# 源码证明或修改载荷内部的请求处理、ACK 时序与运行策略。

## 界面与语言

- 视觉遵循 [Vercel design.md](https://vercel.com/design.md) 的克制、直接和任务优先原则：连续画布、清晰层级、低装饰、颜色只用于操作与状态。
- 品牌色为 `#024AD8`；深色主题使用同色相的高对比变体显示文字与控件状态。
- 跟随 Windows 11 应用深色/浅色主题，并同步窗口标题栏。
- 提供简体中文、繁體中文和 English；首次启动按原交互用户的 Windows 显示语言选择，也可在“设置”中即时切换。
- 界面图标来自 [Phosphor Icons](https://phosphoricons.com/) Core 2.1.1；导航使用 Duotone 图标。许可证见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。
- 应用内提示、确认、错误和警告统一使用自绘 WPF 对话框，复用品牌色、深浅主题、Phosphor Duotone 语义图标和应用当前语言；危险操作使用具体动作名称，并把取消或保留数据设为安全默认。Windows UAC 与打开/保存文件窗口保留系统原生界面，其中后者仍由当前应用窗口托管。

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

## 证据版本边界

随附件提供的原始 Worker 架构、配置示例和测试说明描述的是 `0.1.0-integration.3`；本仓库冻结并安装的二进制是 `0.1.0-integration.4`。原始文档可用于发现配置与测试缺口，但不能替代 `.4` 的源码或真机行为证据。本项目只把精确载荷哈希、PE/签名校验、管理器自身行为，以及由当前回环 API 实际返回的字段作为 `.4` 的直接证据。差异与待验收项见 [`UPSTREAM-CONTRACT-AUDIT.md`](UPSTREAM-CONTRACT-AUDIT.md)。

## 核心行为

### 状态

- 分开显示 Agent 进程、本机 Control API、计划任务和当前 P2P 连接。
- 分开显示“磁盘配置中的授权”和“最近一次健康重启对应的配置快照”。后者只说明重启成功且新实例的鉴权回环 API 可用；该 API 不回读 Agent 内部白名单或请求处理策略，因此不能据此声称某项授权已由运行时策略证明生效。配置变更或健康检查失败时显示“待核验”。
- 启动、停止和重启都检查操作结果；启动前拒绝执行定义不匹配的同名计划任务。

### 授权管理

- `allowed_peers` 只接受完整的 base58btc libp2p PeerID，拒绝 `*`、截断值、非字符串和重复项。
- 白名单是管理器可以验证和写入的来源限制，但不是整个系统唯一的安全边界；私网准入、Peer 身份和载荷内部的实际请求处理仍需分别验收。
- “断开全部授权”先保存本机备份，再清空磁盘配置并验证 Agent 重启。成功结果证明新实例和鉴权回环 API 健康，并记录空配置快照；它不等同于通过 API 回读了载荷内部白名单。

### 任务审计与诊断

- journal 阅读器保留无法解析的行，并单独显示解析错误；若记录含 `acknowledged`，以独立 ACK 列显示。ACK 是记录快照，不等同于操作成功。在本次可用 Worker 合约和管理器实现中没有自动压缩流程；接近 512 MiB 时必须先制定保持 ACK/去重连续性的安全归档方案，超过该上限会阻断需要一致停机快照的维护。不能直接清空 journal，也不能用较旧快照覆盖较新状态。
- 日志读取区分“未生成、为空、读取失败”；Agent 正在写入时拒绝清空。宿主只在下一次启动 Agent 前检查 `agent.log`，并在当时文件大于 8 MiB 时轮转为 `agent.log.1`；一次持续运行期间日志可以继续超过 8 MiB。
- 默认诊断会哈希设备名、账户、PeerID 和 Command ID，并省略原始 journal、Agent 日志、bootstrap 地址和业务输出。收集器会读取 API Token 以鉴权查询 `127.0.0.1` 回环 API，但绝不把 Token 值写入诊断；私网密钥和设备私钥同样只报告路径、大小和时间等元数据，不读取或导出其内容。

## 安装与安全边界

安装目标：

- 程序：`C:\Program Files\P2PAgent`
- 运行数据：`C:\ProgramData\P2PAgent`
- 登录任务：`P2P Agent`
- 本机 API：`127.0.0.1:7432`

安装流程先验证载荷和输入，再停止旧实例、部署文件、写配置、注册任务，并等待本机鉴权 API 就绪。失败会尝试恢复程序、配置、key、任务定义与原运行状态；若回滚不能完全确认，界面会报告失败并保留恢复材料供人工处理。

安装或修复在确认 Agent 停止后创建一致的受保护快照。事务成功且清理核验通过时会删除该快照；失败、回滚结果不确定或快照清理失败时会保留材料并报告路径。journal 恢复始终优先保留停机快照之后产生的较新状态，不会直接倒退覆盖。

首次安装固定写入发行包预置的公网 TCP/WS bootstrap 地址，并尝试服务器发现、Router 与 Relay；安装向导目前不能在落盘前选择仅局域网模式。只需局域网时，应在安装完成后到“设置”清空 Bootstrap 地址并重启 Agent。原始 Worker 合约未定义文件操作的允许根目录约束，也未定义 URL 下载的出站主机白名单或 SSRF 防护；Server Router 可见转发帧元数据与 payload，现有文档也未声明额外的应用层端到端加密。应只配置可信远端来源，并使用受限专用运行账户和受控出站网络。

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
4. 在向导中确认设备名、Agent 运行账户和获准远端来源 PeerID；默认 Agent 标签为 `worker`。
5. 安装完成后，在“状态”中复制本机 PeerID，交给需要识别此被控端的受信任远端管理员。

关闭管理窗口可隐藏到通知区域，不会停止后台 Agent；需要停止接收新任务时，应在“状态”页停止 Agent。

## 构建与校验

WPF 程序只能在 Windows 上运行。启用 `EnableWindowsTargeting` 后可以在其他系统交叉编译，但 Authenticode、ACL、计划任务、UAC、通知区域和深浅主题必须在 Windows 11 真机验证。

Windows PowerShell 5.1 或 PowerShell 7+（脚本不依赖 `PEReader`、
`SHA256.HashData`、`Convert.ToHexString` 等新版 .NET API）：

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

以下行为属于上传的 `p2p-agent.exe`，附件没有相应 `.4` 源码，本仓库无法从静态 C# 审查中修复或证明：

- 各类远端请求的接受条件、执行边界和具体超时；
- 文件路径与 URL 访问的最终约束；
- 结果 ACK 的发送、重试和持久化时序；
- Agent 对全部配置字段的最终语义。

要继续处理这些问题，需要提供与当前二进制 `0.1.0-integration.4` 对应的 Agent 源码和协议测试。
