# OpenConnect Gui（macOS）

Avalonia 11 + 内置 libopenconnect 的 macOS SSLVPN 客户端。

## 依赖

- .NET 8 SDK（开发机；发布自包含包后用户机不需要）
- **一次性**安装权限助手（之后连接不再弹管理员密码）：

```bash
./scripts/vendor-macos-native.sh   # 开发机：内置 libopenconnect（需本机 brew openconnect 仅用于打包）
./scripts/install-macos-helper.sh
```

正式包已内置 `libopenconnect`，**目标机无需** `brew install openconnect`。  
首次在 App 内点「连接」时若未安装助手，也会自动弹出一次系统密码框完成安装。

## 构建 / 运行

```bash
export PATH="$HOME/.dotnet:$PATH"
./scripts/vendor-macos-native.sh  # 首次或升级 openconnect 后
./scripts/build-macos.sh          # Release
dotnet run --project SslVpnClient.Mac -c Release
```

产物：`SslVpnClient.Mac/bin/Release/net8.0/OpenConnectGui.dll`

## 功能范围

| 功能 | 状态 |
|------|------|
| 登录 / 保存凭证 / profile.xml 节点 | ✅ |
| 选节点连接 / 断开 | ✅ 全局 / 智能分流 |
| 智能分流 / chnroutes | ✅ 国内直连，其它走 VPN |
| 托盘 / 流量图 | ✅ 菜单栏托盘 + 实时上下行图 |
| libopenconnect 内置 | ✅ root worker（macOS utun 需特权） |

## 分发（目标机无需安装 .NET）

### 推荐：安装包 PKG（安装时输入管理员密码）

```bash
./scripts/package-macos-pkg.sh           # Apple Silicon → .pkg + Installer.dmg
./scripts/package-macos-pkg.sh osx-x64   # Intel
```

产物示例：

- `dist/OpenConnectGui-2.2.0-macos-arm64.pkg` — 双击安装（装 App + VPN 权限助手）
- `dist/OpenConnectGui-2.2.0-macos-arm64-Installer.dmg` — 内含上述 PKG

签名 / 公证说明见 [docs/SIGNING.md](docs/SIGNING.md)。

### 备选：拖拽 DMG

```bash
./scripts/publish-macos.sh          # 仅可执行文件
./scripts/package-macos-dmg.sh      # 打包 .app + DMG（默认 Apple Silicon）
./scripts/package-macos-dmg.sh osx-x64   # Intel Mac
```

拖拽安装后，**首次连接**仍可能提示一次管理员密码以安装权限助手。

配置目录：`~/Library/Application Support/OpenConnectGui/`  
连接日志：`~/Library/Application Support/OpenConnectGui/sessions/*/openconnect.log`

## 使用

1. 填写服务器地址（`$url`）、账号、密码 → **登录**
2. 选择节点 → **连接**（可切换全局 / 分流）
