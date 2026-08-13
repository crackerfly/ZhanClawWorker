# runtime 目录

构建前必须放入的两个文件。**两者都需要提交入库。**

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

---

## swarm.key（必需，内置以便用户免选）

私有网络准入密钥。所有协作设备必须使用同一份。

```powershell
copy <发行包>\swarm.key runtime\swarm.key
git add runtime/swarm.key
```

存在时会一并内嵌进安装器，安装向导**不再要求用户选择文件**，只显示一行确认。

缺失时构建仍然成功（只报警告），但安装向导会退回为手动选择文件。

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

私网密钥泄露意味着任何人都能加入这个 libp2p 私有网络。虽然它**不等于**获得远程执行权
（那由每台被控端的 `allowed_peers` 白名单单独控制，见 `ARCHITECTURE.md` §19 的四层信任边界），
但它是第一道防线，且一旦泄露只能靠全网轮换密钥来补救——所有设备都要重装。

上游 `OPENCLAW_INTEGRATION.md` 明确要求「发行包不得包含 swarm.key」。本仓库为了做到
「用户免选」而有意偏离该约定，代价就是仓库必须私有。若将来需要开源本仓库，
请改用上面的 Secret 注入方式，并从 Git 历史中彻底移除该文件
（`git filter-repo` 或重建仓库，仅删除最新提交不够）。

### 建议同时做的两件事

- 给仓库开启 Secret scanning 与 push protection
- 定期轮换 swarm.key；轮换后所有设备需重新安装
