# 战 Claw 被控端实施与核验报告

日期：2026-08-14

## 被控端证据边界与文档复核

- 本报告仅覆盖 Windows 被控端管理器、安装器、计划任务宿主和随包 `p2p-agent.exe`。其他产品的集成说明与其他可执行文件不属于本项目的实现或验收范围。
- 原始 Worker 架构、配置示例与测试说明以 `0.1.0-integration.3` 为基线；当前清单冻结的是 `0.1.0-integration.4`，精确 SHA-256 为 `a2b36af5f2623ddd2f91d223f471abe9d8d957fb2dca6a566e02b2dbd04dd5e9`。`.3` 文档只作为查漏补缺输入，不作为 `.4` 运行时语义的充分证据。
- 当前可静态证明的范围包括管理器源码、安装/回滚/ACL/任务边界、配置写入、回环 API 客户端、载荷字节哈希、PE 形态与 Authenticode 固定值。载荷内部请求处理只能依赖 `.4` 源码或 Windows 真机协议测试确认。
- 首次安装会使用发行包预置的公网 TCP/WS bootstrap，并尝试服务器发现、Router 与 Relay；向导目前不能预先选择仅局域网。安装后可在“设置”清空 Bootstrap 地址并重启。默认 Agent 标签已经与 Worker 身份统一为 `worker`。
- 原始 Worker 合约没有定义文件操作的允许根目录、URL 出站主机白名单或 SSRF 防护；Server Router 可见转发帧元数据与 payload，现有文档未声明额外应用层端到端加密。这些限制已在向导和设置页中披露，部署时仍需使用受限专用账户并限制出站网络。

## 界面统一：字体、控件度量与主按钮文字 R6

- 全局界面字体统一为 **Microsoft YaHei UI**。`Themes/Controls.xaml` 新增 `AppFontFamily` 与 `MonoFontFamily` 两个字体令牌，三个窗口、全部隐式控件样式（TextBlock、Button、TextBox、ComboBox、ComboBoxItem、CheckBox、Label、DataGrid 及其表头/单元格、导航项）以及五处正文样式全部改为引用令牌，视图中不再存在任何硬编码字族。
- 原先用于 PeerID、安装路径、Bootstrap 地址、技术详情与运行日志的 `Cascadia Mono, Consolas` 按要求一并改为 Microsoft YaHei UI。该字体不是等宽字体，日志与 Base58 PeerID 会失去列对齐；如需恢复，只把 `MonoFontFamily` 一行改回 `Cascadia Mono, Consolas` 即可，无需改动任何视图。
- 通知区菜单是 WinForms `ContextMenuStrip`，不参与 WPF 资源继承，因此在 `MainWindow.xaml.cs` 中显式套用同一字族；字号沿用 `SystemFonts.MenuFont` 的磅值，字体不可用时保留系统默认菜单字体并静默降级。
- 以主界面「状态」页的「启动」按钮为基准统一交互控件度量：该按钮为 `FontSize 13 + Padding 14,7 + 1px 边框`，在 Microsoft YaHei UI 下自然高度约 33.2px。新增 `InteractiveControlBase`（`TargetType="Control"`）把 `MinHeight` 固定为 **34**，Button、TextBox、ComboBox 全部 `BasedOn` 于此，因此高度只有一处定义。选用基样式继承而不是 `sys:Double` 资源，是为了避免在单文件自包含发布下引用 `clr-namespace:System` 造成的程序集解析歧义。
- 圆角统一为 `ControlCornerRadius`（6）。ComboBox 此前使用 WPF 默认模板，既没有圆角也不跟随深浅主题，现已重写为与按钮/输入框同高同圆角的自绘模板（含 Popup、ComboBoxItem 与几何绘制的下拉箭头，不引入任何图标字体）。导航项圆角也收敛到同一令牌；仅保留焦点环（8，与 6 同心外扩 2px）和 3px 导航指示条两个刻意例外。
- 输入框模板的 `PART_ContentHost` 由固定 `Stretch` 改为跟随 `VerticalContentAlignment`，单行输入垂直居中；多行与只读展示面（Bootstrap 地址、运行日志、安装技术详情、对话框正文）显式声明 `VerticalContentAlignment="Top"`，其中三处只读展示面另加 `MinHeight="0"`，不受输入控件高度基线约束。
- 修复主按钮在品牌蓝底上显示深色文字的根因：本资源字典存在全局隐式 `<Style TargetType="TextBlock">`，它会命中 `ContentPresenter` 为字符串内容生成的 TextBlock，从而覆盖按钮自身的 `Foreground`。现在两个按钮模板都在 `ContentPresenter.Resources` 作用域内重新声明该隐式样式，把文字前景绑回按钮的 `Foreground`，并额外设置 `TextElement.Foreground` 作为第二层保障。`AccentButton` 在常态、悬停、按下与禁用四种状态下都显式重申 `OnBrandBrush`（浅色与深色主题均为 `#FFFFFF`），禁用态只淡化整块填充，绝不回落到 `TextDisabledBrush`。
- `scripts/verify_source.py` 新增 `verify_typography_and_metrics_contract` 门禁并接入 `main()`：校验两个字体令牌均以 Microsoft YaHei UI 开头、视图中不存在硬编码字族或非令牌的 `FontFamily`、托盘字体已接线、三类交互控件确实继承同一基样式且未本地覆盖高度、圆角未绕过令牌、两主题 `OnBrandBrush` 为纯白、`AccentButton` 四态白字且不含 `TextDisabledBrush`、以及两个按钮模板都装有隐式 TextBlock 作用域守卫。`build.yml` 与 `release.yml` 在编译前即执行该脚本，上述任一项回归都会阻断产物发布。
- 本轮只改视觉层：没有触碰安装、回滚、计划任务、ACL、载荷校验、配置写入或回环 API 的任何代码路径，`runtime/` 下的载荷与清单字节未变。
- 未纳入本轮：CheckBox 仍使用 WPF 默认模板，深色主题下勾选框本体不跟随品牌色；这属于既有行为，需要单独重写模板。

## 维护事务掉电恢复收口 R5

- 安装、修复与卸载维护意图现在使用可持久恢复的 `Mutation` / `ValidationReady` 两阶段协议。所有文件、配置、防火墙和 disabled 任务写入完成后，才会以 write-through 原子切换到 `ValidationReady` 并进行健康验证；恢复或再次修改前必须先停止 Agent、限制执行 ACL、二次确认停机并回到 `Mutation`。
- 维护期间 `p2p-agent.exe` 仅允许 Administrators/SYSTEM 执行。受控健康验证使用绑定精确运行账户 SID、载荷 SHA-256、模式、短时戳与随机 nonce 的一次性许可；许可通过独占句柄验证并原子消费，避免并发复用。
- 计划任务在验证启动时保持规范定义且默认 disabled，仅在 COM Run 提交窗口临时启用；观察到精确 Agent 进程后立即重新限制载荷执行 ACL。提交时先持久退休维护标记、确认不存在活动维护工件，再恢复普通执行权限。
- 卸载恢复采用固定、受保护的目录级状态机。恢复状态、DataRoot 隔离与清理墓碑均以 write-through rename 发布；`prepared` / `snapshot-ready` 可回滚，进入 `commit-started` 前先持久化 forward-only 决策，之后只允许幂等完成卸载，避免掉电后在两种相反操作间摇摆。
- 最终独立终审枚举了安装、修复、卸载提交/回滚、预存或缺失 marker、许可残留与运行/停止两种入口状态；未发现剩余确定的 P0/P1 问题。该结论仍需 Windows 11 真机的 Task Scheduler、NTFS ACL 与断电故障注入作为发布前动态验收。

## 计划任务注册后验收修复 R4

- 已确认新截图中的任务实际已经注册成功；失败发生在注册后的定义验收。Windows Task Scheduler 会规范化 `IRegisteredTask.Xml`，并省略取架构默认值的可选节点。旧实现把这些节点的“省略”当成空值，因此把 `Enabled=true`、`AllowStartOnDemand=true`、`Priority=7`、`Hidden=false` 等正确的有效设置误报为不匹配。
- 注册后验收现在从同一个 `RegisteredTask` 快照读取规范化 XML、有效 `Enabled` 和 `Definition.Principal.RunLevel`。`RunLevel` 必须是 `TASK_RUNLEVEL_LUA=0`，`Enabled` 必须与 XML 的有效值一致；显式写入相反值仍会失败关闭。
- XML 布尔值按 `xs:boolean` 语义接受 `true/false/1/0`。仅对 Microsoft Task Scheduler 架构明确给出默认值的字段允许省略；非默认安全与执行设置仍要求精确存在并匹配。
- 进一步拒绝额外 Principal 权限字段、LogonTrigger 延迟/重复/边界、未获准 Settings、任务内嵌 `SecurityDescriptor`、附加 `Data` 与额外根节点，避免为了兼容规范化 XML 而放松执行边界。
- 任务注册不再以 `sddl=null` 更新并继承未知旧安全描述符：现在把 owner 固定到受信任的 Administrators（兼容 Task Scheduler 规范化为 SYSTEM），并显式应用 protected DACL，仅 SYSTEM、Administrators 具有完全控制，运行账户具有读取/执行；同时禁止 Task Scheduler 自动追加 Principal ACE。注册后通过 `GetSecurityDescriptor` 和 `RawSecurityDescriptor` 核验 owner、SID、权限掩码、ACE 类型/标志与禁止继承状态。回滚只接受已经通过定义与安全描述符双重门禁的任务快照，并以同一受控描述符重建，不恢复任意旧宽松 ACL 或普通用户 owner。
- 注册后的查询失败与定义不匹配现在分开报告，并保留可复制的 HRESULT 技术详情；包装层会保留 `ZC-INS-TASK` 根因，不再统一降级为“安装中断”。
- 安装失败且尚未变更系统，或事务回滚已经成功时，向导提供“重试”；回滚失败时禁止继续写入。Agent 已通过鉴权健康检查后，即使仅回滚备份清理失败，也会显示“安装成功但需清理”警告，不再误报功能安装失败。

## 安装、图标与独立审查修复 R3

- 已将附件 `icon.png` 原样纳入 `Assets/icon.png`，并生成包含 16/20/24/32/40/48/64/128/256 像素九个 32-bit 帧的 `Assets/app.ico`。EXE、窗口标题栏、任务栏和通知区域共用新图标；项目本身不创建桌面或开始菜单快捷方式，用户后续创建的快捷方式会默认使用 EXE 内嵌图标。
- 已修复截图中的首次安装失败。Task Scheduler COM 在任务尚不存在时可能把 `0x80070002` 投影成 `FileNotFoundException` 或包装异常，旧实现只捕获 `COMException`，把正常的“尚未安装”误报为 `ZC-INS-TASK`。现在按完整异常链识别缺失 HRESULT，再通过隐藏任务枚举精确确认；其他 COM 错误继续失败关闭并展示可复制的脱敏技术详情。
- 已确认并修复 `swarm.key` 的 ACL 缺陷：从 Program Files 同卷移动到 ProgramData 后会保留源文件 owner/DACL。安装和修复现会在最终落地后为密钥应用并读回核验仅运行账户、Administrators、SYSTEM 的精确受保护 ACL。
- 为上一版失败安装留下的异常密钥 ACL 增加了严格受限迁移：只接受与当前内嵌密钥 SHA-256 完全相同、格式正确、其他敏感对象均可信、且 ACL 恰好符合已知旧版形态的文件；停机后再次完整复核才加固。任意预置密钥或其他不可信对象仍失败关闭。
- 配置损坏不再导致 GUI 退出。授权页以只读空状态和页内警示降级，设置页仍允许切换语言、通知区域偏好和卸载，状态页“修复安装”保持可达；损坏原文件不会被普通保存覆盖。
- UI 命令和可恢复的 Dispatcher 异常不再直接关闭整个管理器；未观察任务异常只记录为已观察。真正不可恢复的进程级故障仍保留非零退出语义。
- Windows PowerShell 5.1 兼容性问题属实：`Test-Payload.ps1` 已移除仅适用于新 .NET/PowerShell 的 API，并在构建与发布流程中同时以 Windows PowerShell 5.1 和 PowerShell 7 运行。
- 运行账户现在必须解析为真实用户 SID，拒绝 SYSTEM、Administrators、服务账户及组 SID；直接使用另一管理员凭据启动时，会优先解析当前交互会话用户，而不是把计划任务错误建到提权账户。
- 审查中其余确认属实的问题也已修复：安装步骤使用稳定枚举而非中文字面量；就绪轮询复用 API 客户端；非整 MiB 配置不会被静默改写；安全描述符改用 `SetNamedSecurityInfo`；进程句柄和 Agent 输出尾部正确收口；Base58 全零、PE 边界和 manifest 形态字段已修正；Phosphor 重绘不再克隆画刷。
- “`IsPortOpenAsync` 缺少 `ConfigureAwait(false)` 必然造成经典死锁”的判断不成立：该异步调用链没有同步阻塞等待；未使用的同步包装仍已删除。诊断正文为简体中文是已在三语 UI 中明确披露的支持边界，不是暗中宣称的三语输出。
- 证书 2027-06-13 到期属于发布运维事项而非当前代码缺陷；轮换前必须先把新 SPKI 加入当前版本与可信回滚 pin 集合，再分阶段发布。

## 统一弹窗视觉热修复 R2

- 已将业务代码中的 44 个原生 `MessageBox.Show` 入口全部迁移到统一 WPF 对话框；原生 MessageBox 只保留在启动期或致命错误导致自绘窗口无法创建时的最小降级路径。
- 对话框复用现有 Windows 11 深色/浅色主题、精确品牌色 `#024AD8`、字体、圆角和焦点样式，并使用 Phosphor Core 2.1.1 Duotone 信息、询问、警告与错误图标。
- 对话框跟随应用内简体中文、繁體中文和 English，而不是让按钮固定跟随 Windows 显示语言；三语动态资源键集与格式占位符一致，不以容易过期的固定键数作为验收依据。
- “立即重启 / 稍后”“停止 Agent / 取消”“退出安装 / 继续安装”“卸载并保留数据 / 卸载并删除数据 / 取消”等流程使用准确动作标签，不再以含糊的“确定”代替业务动作。
- 危险动作不会成为 Enter 默认项；Escape、标题栏关闭和自绘窗口降级都返回显式安全动作。卸载数据选择默认“保留数据”，不会把删除数据设为默认。
- 统一解析当前活动窗口作为 Owner；托盘退出前先恢复主窗口，避免弹窗失去前台归属，并合并未保存授权草稿的重复退出确认。
- 只读消息正文支持换行、滚动、选择和复制。打开/保存文件窗口仍使用 Windows 原生实现，但已补齐显式 Owner；UAC 继续由 Windows 安全桌面负责。
- `scripts/verify_source.py` 增加弹窗回归门禁：业务代码不得重新直接调用原生 MessageBox，并检查主题、Phosphor、Owner helper 与安全 Enter/Escape 路径。
- `build.yml` 与 `release.yml` 都会在编译/发布前执行该门禁；绑定模式、三语资源、弹窗动作或原生弹窗入口回归会直接阻止产物发布。

## 运行时热修复 R1

- 已修复安装向导启动即崩溃：`CheckBox.IsChecked` 的 WPF 默认绑定模式为 `TwoWay`，而 `WizardViewModel.HardenAcl` 是只读状态属性。现已显式改为 `Mode=OneWay`。
- 已扫描所有 XAML 中默认为双向的 `TextBox.Text`、`CheckBox.IsChecked` 和 Selector 选择绑定；其余绑定均有可写源属性，或已显式指定 `OneWay`。
- `scripts/verify_source.py` 新增 WPF 绑定回归门禁，防止 `HardenAcl` 被再次改回默认双向绑定。
- 授权、状态和审计表格的展示列也全部显式标注 `Mode=OneWay`，即使以后误删父级 `DataGrid.IsReadOnly=True`，也不会向 getter-only 显示属性回写。
- 安装结束后禁用“上一步/下一步”并隐藏“安装”，避免退回前一页或同时出现“安装/完成”的状态错乱。

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
- 已完成简体中文、繁體中文和 English 三语资源，并校验动态资源键集与占位符一致；报告不硬编码会随文案演进的资源键数量。
- 首次启动依原交互用户的 Windows 显示语言自动选择；凭据式 UAC 后不会误用管理员账户语言。设置页支持即时切换并同步日期/数字文化。
- 已复核授权、停止、ACK、诊断、安装/卸载等文案，不再宣称管理器无法从附件源码证明的 Agent 内部行为。
- 安装/回滚/卸载错误对三种语言显示本地化摘要与稳定 `ZC-INS-*` 错误码；需人工处理的受保护残留路径会明确告知用户。

## 已修复的高影响问题

- AgentHost 退出码现传递给 Windows，异常退出不再被误报为 0，计划任务的失败重启可生效。
- 停止/启动/重启/紧急撤销均检查结果并等待本机鉴权 API 健康，不再把失败的重启标成已应用。
- 区分“磁盘配置中的授权”和“最近一次健康重启对应的配置快照”；该快照证明新实例及鉴权回环 API 健康，但 API 不回读载荷内部白名单，因而不把它表述为运行时策略证明。草稿编辑、笔记修改与保存失败均有防丢提示。
- `allowed_peers` 使用集中的 libp2p PeerID 边界验证，禁止通配符、截断值、非字符串和重复值；AgentHost 启动前再次验证运行边界。
- ProcessRunner 区分调用方取消与内部超时，终止后有界等待并排空输出，未确认终止会失败关闭。
- 活动日志拒绝清空。`AgentHost` 只在下一次启动前检查日志，并在当时 `agent.log` 大于 8 MiB 时轮转；单次持续运行期间文件可以继续增长，不能把 8 MiB 描述为硬上限。
- journal 保留损坏行与解析错误，结构化展示 `acknowledged`，不将 ACK 与任务成功混为一谈。可用 Worker 合约和管理器没有自动压缩流程；接近 512 MiB 时必须先制定保持 ACK/去重连续性的安全归档，超过上限会阻断需要一致快照的维护，且不能直接清空或用旧副本覆盖较新状态。
- 默认诊断对设备名、账户、PeerID 和 Command ID 做哈希，省略原始 journal/日志/bootstrap/业务输出。它会读取 API Token 以鉴权查询回环 API，但绝不导出或显示 Token 值；私网密钥和设备私钥只报告文件元数据。
- 计划任务改为 Task Scheduler COM 结构化操作，不解析本地化 `schtasks.exe` 文本；启动前精确核验账户、触发器、Action、参数、工作目录、权限级别与关键 Settings。
- 保留 `AutoStart=false`；手动启动禁用的登录任务时仅在 COM Run 提交期间临时启用，并在 `finally` 恢复用户偏好。
- 数据目录与敏感文件实施 protected DACL，仅运行账户/Administrators/SYSTEM；程序目录仅 Administrators/SYSTEM 可写。既有敏感文件的 owner/DACL/reparse 不可证明时失败关闭。
- 安装/修复使用受保护 staging 与 BA/SY-only 停机快照，备份和恢复目标逐项验证 SHA-256；旧 Agent 仅在 Authenticode 与显式 rollback SPKI pins 可信时才能被恢复执行。成功并完成清理核验后删除停机快照；失败、回滚不确定或清理失败时保留受保护材料并报告路径。较新的 journal 永远不会被旧快照直接覆盖。
- 卸载改为两阶段事务：先捕获任务 XML/Enabled/运行状态与程序哈希，数据先隔离；提交前失败会恢复 ACL、文件、任务、偏好和原运行态并做健康检查。
- 载荷验证不会在提权 GUI/CI 签名阶段执行 Agent；使用精确 SHA-256、AMD64/Console PE 形态、WinVerifyTrust/Authenticode 及 CN/叶证书/SPKI pins 标识已审查字节。
- 正式发布拆成只读构建 job 与签名/发布 job，以 SHA-256 校验 artifact 交接；只有后者获得 PFX 和 `contents: write`，并精确固定外层 EXE 签名 CN/证书/SPKI。

## 核验结果

| 检查 | 结果 |
|---|---|
| Release Rebuild，`TreatWarningsAsErrors=true` | 通过，0 warning / 0 error |
| `win-x64` 自包含单文件测试构建 | 通过，产物为 PE32+ / Windows GUI / x86-64 |
| `scripts/verify_source.py` | 通过：payload/key 形态、XML、三语键/占位符、视觉契约、安全契约 |
| GitHub Actions YAML 解析 | `build.yml` / `release.yml` 均通过 |
| 禁止提权载荷探测扫描 | 通过，管理路径没有执行 Agent `-version` |
| 视觉静态契约 | 通过：精确品牌色、无渐变/阴影、Phosphor/Duotone 已接线 |
| 排版与控件度量契约 | 通过：字体令牌、基样式继承、圆角令牌、蓝底白字四态 |

编译在 Linux 上使用 .NET 8 Windows targeting 完成；该环境不能运行 WPF，因此 Windows 11 标准用户/UAC、Task Scheduler COM、ACL、Authenticode、托盘、深浅主题、高 DPI 与三语排版仍必须按 `README.md` 的发布前清单做 Windows 11 真机集成验收。

## 不可越过的源码边界

附件中没有与 `p2p-agent.exe 0.1.0-integration.4` 对应的 Go 源码。因此本次不能声称已修复或证明以下二进制内部行为：

- 各类远端请求的接受条件、执行边界与具体超时；
- 文件路径与 URL 访问的最终约束；
- 结果 ACK 的发送、重试和持久化时序；
- Agent 对所有配置字段的最终语义。

要继续修复上述问题，需要提供该二进制对应的 Agent 源码和协议测试。

## 正式发布

本地同时提供已验证源码和一个明确标记为 **unsigned-test** 的 `win-x64` 自包含单文件，仅用于复验本次启动修复；它没有 Authenticode 签名，不能当作正式发布件分发。正式发布应在 Windows GitHub Actions 中配置：

- `CODE_SIGN_PFX_B64`
- `CODE_SIGN_PFX_PASSWORD`
- `RELEASE_SIGNER_CN`
- `RELEASE_SIGNER_CERT_SHA256`
- `RELEASE_SIGNER_SPKI_SHA256`

签名、固定值校验或 artifact 交接哈希任一失败时，流程不会发布。
