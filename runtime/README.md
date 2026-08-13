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

升级 Agent：替换本文件、提交、打新 tag。

### payload-manifest.json（必需）

该清单冻结 `p2p-agent.exe` 的 SHA-256、与该哈希绑定的已审查版本元数据、AMD64/Console PE 形态、
Authenticode 必须为 `Valid`，以及签名者名称、叶证书和公钥指纹：

`CN=StarSoftComm(China) Ltd.`

替换 Agent 后必须在受信 Windows 机器上先核对签名与版本，再将审查结果与精确哈希一起写入清单。
CI 会运行 `scripts/Test-Payload.ps1`；安装器释放二进制后会再校验精确 SHA-256、PE 形态和 Authenticode 固定值。管理器不会为版本探测而以提权身份执行 Agent；清单中的版本是与已审查哈希绑定的元数据。
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
安装器仍能读取既有 `swarm.key`，升级/修复不会用内置 key 覆盖本机已有 key。

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

私网密钥泄露会破坏私网准入边界。它本身不应被当作远程执行授权；远程请求是否被接受还取决于
部署的 Agent 对来源、`allowed_peers` 和具体 Primitive 的实际校验。本仓库没有该 Agent 的 Go 源码，
因此不能把管理器写入的白名单描述成整个系统唯一或已被证明有效的执行边界。密钥泄露后应全网轮换，
所有协作设备都必须同步更新。

上游 `OPENCLAW_INTEGRATION.md` 明确要求「发行包不得包含 swarm.key」。本仓库为了做到
「用户免选」而有意偏离该约定，代价就是仓库必须私有。若将来需要开源本仓库，
请改用上面的 Secret 注入方式，并从 Git 历史中彻底移除该文件
（`git filter-repo` 或重建仓库，仅删除最新提交不够）。

### 建议同时做的两件事

- 给仓库开启 Secret scanning 与 push protection
- 定期轮换 swarm.key；轮换后所有设备需重新安装
