# SslVpnClient.Mac（macOS）

Avalonia 11 + **内置 libopenconnect**（方案 A，非 CLI）的 macOS SSLVPN 客户端。

## 依赖

- .NET 8 SDK（开发机；发布自包含包后用户机不需要）
- 开发机打包：`brew install openconnect`（仅用于 `vendor-macos-native.sh` 抽取 dylib）
- **一次性**安装权限助手：

```bash
./scripts/vendor-macos-native.sh
./scripts/install-macos-helper.sh
```

目标机**不需要**安装 openconnect。首次连接若助手未装会弹一次系统密码框。

## 构建 / 运行

```bash
export PATH="$HOME/.dotnet:$PATH"
./scripts/vendor-macos-native.sh
./scripts/build-macos.sh
dotnet run --project SslVpnClient.Mac -c Release
```

## 功能范围

| 功能 | 状态 |
|------|------|
| 登录 / 保存凭证 / profile.xml 节点 | ✅ |
| 选节点连接 / 断开 | ✅ 全局 / 智能分流 |
| 智能分流 / chnroutes | ✅ |
| 托盘 / 流量图 | ✅ |
| libopenconnect 内置 | ✅ |

## 分发

```bash
./scripts/package-macos-dmg.sh          # arm64
./scripts/package-macos-dmg.sh osx-x64  # Intel
```
