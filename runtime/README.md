# runtime 目录

构建前必须放入以下冻结载荷。正式发布缺少任一文件都会失败。

---

## p2p-agent.exe（必需）

从 `p2p-agent-v0.1.0-integration.4-windows-amd64` 发行包中复制 `p2p-agent.exe` 到本目录并提交。

```powershell
copy <发行包>\p2p-agent.exe runtime\p2p-agent.exe
git add runtime/p2p-agent.exe
```

构建时会编译为嵌入资源打进单文件 EXE，安装向导运行时释放到
`C:\Program Files\P2PAgent\p2p-agent.exe`。

**文件缺失时构建直接失败**，避免产出一个装不了 Agent 的安装器。

升级 Agent：替换本文件、同步更新清单、提交并打新 tag。原始 Worker 文档以 `0.1.0-integration.3` 为基线；当前发行包冻结的是 `0.1.0-integration.4`，不能用 `.3` 文档代替 `.4` 的源码或真机协议验收。

### payload-manifest.json（必需）

该清单冻结 `p2p-agent.exe` 的 SHA-256、与该哈希绑定的已审查版本元数据、AMD64/Console PE 形态、
Authenticode 必须为 `Valid`，以及签名者名称、叶证书和公钥指纹：

`CN=StarSoftComm(China) Ltd.`

替换 Agent 后必须在受信 Windows 机器上先核对签名与版本，再将审查结果与精确哈希一起写入清单。
CI 会运行 `scripts/Test-Payload.ps1`；该脚本同时兼容 Windows PowerShell 5.1 与 PowerShell 7+。
安装器释放二进制后会再校验精确 SHA-256、PE 形态和 Authenticode 固定值。管理器不会为版本探测而以提权身份执行 Agent；清单中的版本是与已审查哈希绑定的元数据。
任何安装边界校验不匹配都会在覆盖现有 Agent 之前中止。

---

## swarm.key（必需，内置以便用户免选）

私有网络准入密钥。所有协作设备必须使用同一份。

```powershell
copy <发行包>\swarm.key runtime\swarm.key
git add runtime/swarm.key
```

存在时会一并内嵌进安装器，安装向导**不再要求用户选择文件**，只显示一行确认。

正式 Release、主分支 dev 与本地项目构建均要求存在有效 `swarm.key`；缺失会直接失败。
安装器会读取并校验本机既有 `swarm.key`。只要现有 key 有效，升级、修复以及保留数据后的再次安装都会保留它，不会用新构建内嵌的 key 覆盖。仅替换本目录文件或重新运行安装器，因此**不能**完成已部署设备的密钥轮换。

### 备选：构建期从 Secret 注入

如果不想把密钥放进 Git 历史，可以改用 GitHub Secret：

1. 仓库 `Settings → Secrets and variables → Actions` 添加 `SWARM_KEY_B64`
2. 值为 `swarm.key` 的 base64：

   ```powershell
   [Convert]::ToBase64String([IO.File]::ReadAllBytes('swarm.key')) | Set-Clipboard
   ```

`release.yml` 会在构建前把它写出到 `runtime/swarm.key`，效果与入库完全一致。
两种方式二选一即可，同时存在时以仓库文件为准。

---

## ⚠️ 仓库可见性

`swarm.key` 一旦入库，**本仓库必须保持 Private**。

私网密钥泄露会破坏私网准入边界。它本身不等同于远端来源授权；请求是否被接受还取决于
部署载荷对来源、`allowed_peers` 和运行状态的实际处理。本仓库没有该载荷的 `.4` Go 源码，
因此不能把管理器写入的白名单描述成整个系统唯一或已由运行时证明的执行边界。

本发行方式为了让用户免选文件而把 key 内嵌进安装器，代价是源码仓库和构建产物都必须按机密软件管理。若将来需要公开仓库，
请改用上面的 Secret 注入方式，并从 Git 历史中彻底移除该文件
（`git filter-repo` 或重建仓库，仅删除最新提交不够）。

### 密钥轮换

当前管理器没有自动轮换或安全原位替换入口。泄露、成员退出或例行轮换时必须使用受控人工流程：

1. 在可信离线环境生成新的三行 pnet key，并通过独立渠道确认所有参与同一私网的节点使用同一字节内容。
2. 安排维护窗口并先停止相关 Agent，避免新旧 key 同时在线造成分区或继续接受旧 key。
3. 更新构建输入只会影响新部署；对已有设备，必须由经过审查的管理员流程在 Agent 停止后替换 `C:\ProgramData\P2PAgent\swarm.key`，重新应用仅运行账户、Administrators、SYSTEM 可访问的受保护 ACL，并验证文件格式、owner、DACL 与哈希。当前安装器不会代做这一步。
4. 全部节点完成后再启动并核对连接；若任何设备无法证明已更新，应保持停止。旧 key 和临时副本需按密钥材料安全销毁。

卸载并删除数据后重新安装也会采用新构建内嵌的 key，但会同时删除设备身份并生成新的 PeerID，不能把它当作无副作用的常规轮换方式。

### 建议同时做的两件事

- 给仓库开启 Secret scanning 与 push protection
- 建立可审计的 `swarm.key` 轮换清单，并定期演练停止、替换、ACL 复核和连通性验证
