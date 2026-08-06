# SslVpnClient.Mac（macOS）

Avalonia 11 + openconnect CLI 的 macOS 客户端 MVP。

## 依赖

- .NET 8 SDK（开发机；发布自包含包后用户机不需要）
- Homebrew `openconnect`：`brew install openconnect`
- **一次性**安装权限助手（之后连接不再弹管理员密码，体验接近 AnyConnect）：

```bash
./scripts/install-macos-helper.sh
```

首次在 App 内点「连接」时若未安装助手，也会自动弹出一次系统密码框完成安装。

## 构建 / 运行

```bash
export PATH="$HOME/.dotnet:$PATH"
./scripts/build-macos.sh          # Release
dotnet run --project SslVpnClient.Mac -c Release
```

产物：`SslVpnClient.Mac/bin/Release/net8.0/OpenConnectGui.dll`

## 功能范围（MVP）

| 功能 | 状态 |
|------|------|
| 登录 / 保存凭证 / profile.xml 节点 | ✅ |
| 选节点连接 / 断开 | ✅ 全局 / 智能分流 |
| 智能分流 / chnroutes | ✅ 国内直连，其它走 VPN |
| 托盘 / 流量图 | ❌ 后续 |

## 分发（目标机无需安装 .NET）

```bash
./scripts/publish-macos.sh          # 仅可执行文件
./scripts/package-macos-dmg.sh      # 打包 .app + DMG（默认 Apple Silicon）
./scripts/package-macos-dmg.sh osx-x64   # Intel Mac
```

DMG 产物：`dist/OpenConnectGui-2.0.0-macos-arm64.dmg`  
（将 App 拖到 Applications；目标机仍需 `brew install openconnect`）

配置目录：`~/Library/Application Support/OpenConnectGui/`  
连接日志：`~/Library/Application Support/OpenConnectGui/sessions/*/openconnect.log`
