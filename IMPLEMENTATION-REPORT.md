# 战 Claw 被控端实施与核验报告

日期：2026-08-14

## 交付范围

本次已完成 WPF 管理器、安装器、计划任务宿主、配置/审计/诊断服务和发布流程的设计、修复与安全收口。附件已按要求放入：

| 文件 | 路径 | SHA-256 |
|---|---|---|
| Agent | `runtime/p2p-agent.exe` | `a2b36af5f2623ddd2f91d223f471abe9d8d957fb2dca6a566e02b2dbd04dd5e9` |
| 私网密钥 | `runtime/swarm.key` | `43e7010c477225431040f336a5f0e3bc2223670073a9dc6268a8040bb2d46f20` |

`swarm(1).key` 已重命名为 `swarm.key`，密钥内容没有写入日志或报告。它会被内嵌进管理器，因此源码包和构建产物都必须按机密软件处理。

## UI 与文案

- 重构为克制、任务优先的连续画布视觉，去除渐变、阴影、发光和无意义动效。
- 主操作填充色精确使用 `#024AD8`；深色主题使用同色相高对比文字/焦点变体，仍保留精确品牌填充色。
- 跟随 Windows 11 深色/浅色应用主题，并同步标题栏。
- 使用 Phosphor Core 2.1.1 官方几何，导航图标使用 Duotone；已移除 Segoe MDL2/private-use glyph。
- 已完成简体中文、繁體中文和 English 三语资源，295 个键键集/占位符一致。
- 首次启动依原交互用户的 Windows 显示语言自动选择；凭据式 UAC 后不会误用管理员账户语言。设置页支持即时切换并同步日期/数字文化。
- 已复核授权、停止、ACK、诊断、安装/卸载等文案，不再宣称管理器无法从附件源码证明的 Agent 内部行为。
- 安装/回滚/卸载错误对三种语言显示本地化摘要与稳定 `ZC-INS-*` 错误码；需人工处理的受保护残留路径会明确告知用户。

## 已修复的高影响问题

- AgentHost 退出码现传递给 Windows，异常退出不再被误报为 0，计划任务的失败重启可生效。
- 停止/启动/重启/紧急撤销均检查结果并等待本机鉴权 API 健康，不再把失败的重启标成已生效。
- 区分“配置中的授权”和“最近一次健康检查证实生效的授权”；草稿编辑、笔记修改与保存失败均有防丢提示。
- `allowed_peers` 使用集中的 libp2p PeerID 边界验证，禁止通配符、截断值、非字符串和重复值；AgentHost 启动前再次验证运行边界。
- ProcessRunner 区分调用方取消与内部超时，终止后有界等待并排空输出，未确认终止会失败关闭。
- 日志不再在 Agent 持有写句柄时错误滚动；活动日志拒绝清空。
- journal 保留损坏行与解析错误，结构化展示 `acknowledged`，不将 ACK 与任务成功混为一谈。
- 默认诊断对设备名、账户、PeerID 和 Command ID 做哈希，省略原始 journal/日志/bootstrap/业务输出，不读取密钥、设备私钥或 Token 内容。
- 计划任务改为 Task Scheduler COM 结构化操作，不解析本地化 `schtasks.exe` 文本；启动前精确核验账户、触发器、Action、参数、工作目录、权限级别与关键 Settings。
- 保留 `AutoStart=false`；手动启动禁用的登录任务时仅在 COM Run 提交期间临时启用，并在 `finally` 恢复用户偏好。
- 数据目录与敏感文件实施 protected DACL，仅运行账户/Administrators/SYSTEM；程序目录仅 Administrators/SYSTEM 可写。既有敏感文件的 owner/DACL/reparse 不可证明时失败关闭。
- 安装/修复使用受保护 staging 与 BA/SY-only 回滚点，备份和恢复目标逐项验证 SHA-256；旧 Agent 仅在 Authenticode 与显式 rollback SPKI pins 可信时才能被恢复执行。
- 卸载改为两阶段事务：先捕获任务 XML/Enabled/运行状态与程序哈希，数据先隔离；提交前失败会恢复 ACL、文件、任务、偏好和原运行态并做健康检查。
- 载荷验证不会在提权 GUI/CI 签名阶段执行 Agent；使用精确 SHA-256、AMD64/Console PE 形态、WinVerifyTrust/Authenticode 及 CN/叶证书/SPKI pins 标识已审查字节。
- 正式发布拆成只读构建 job 与签名/发布 job，以 SHA-256 校验 artifact 交接；只有后者获得 PFX 和 `contents: write`，并精确固定外层 EXE 签名 CN/证书/SPKI。

## 核验结果

| 检查 | 结果 |
|---|---|
| Release Rebuild，`TreatWarningsAsErrors=true` | 通过，0 warning / 0 error |
| `scripts/verify_source.py` | 通过：payload/key 形态、XML、三语键/占位符、视觉契约、安全契约 |
| GitHub Actions YAML 解析 | `build.yml` / `release.yml` 均通过 |
| 禁止提权载荷探测扫描 | 通过，管理路径没有执行 Agent `-version` |
| 视觉静态契约 | 通过：精确品牌色、无渐变/阴影、Phosphor/Duotone 已接线 |

编译在 Linux 上使用 .NET 8 Windows targeting 完成；该环境不能运行 WPF，因此 Windows 11 标准用户/UAC、Task Scheduler COM、ACL、Authenticode、托盘、深浅主题、高 DPI 与三语排版仍必须按 `README.md` 的发布前清单做 Windows 11 真机集成验收。

## 不可越过的源码边界

附件中没有与 `p2p-agent.exe 0.1.0-integration.4` 对应的 Go 源码。因此本次不能声称已修复或证明以下二进制内部行为：

- `process_execute` 的具体 deadline/超时来源；
- 结果 ACK 的发送、重试和持久化时序；
- Primitive 的来源鉴权、权限检查与执行沙箱；
- Agent 对所有配置字段的最终语义。

要继续修复上述问题，需要提供该二进制对应的 Agent 源码和协议测试。

## 正式发布

本地交付的是已验证源码，不是伪装成正式产物的未签名 EXE。正式发布应在 Windows GitHub Actions 中配置：

- `CODE_SIGN_PFX_B64`
- `CODE_SIGN_PFX_PASSWORD`
- `RELEASE_SIGNER_CN`
- `RELEASE_SIGNER_CERT_SHA256`
- `RELEASE_SIGNER_SPKI_SHA256`

签名、固定值校验或 artifact 交接哈希任一失败时，流程不会发布。
