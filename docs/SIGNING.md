# macOS 签名与公证（PKG 正式分发）

对外分发「安装时输入管理员密码」的 `.pkg`，需要 **Developer ID**（不是 Xcode 里常见的 *Apple Development*）。

## 本机当前状态

`security find-identity -v -p codesigning` 若只看到：

- `Apple Development: …`

则可以本地打出 **未签名/临时签名** 的 PKG（安装时仍会要管理员密码），但拷到别的 Mac 会被 Gatekeeper 拦截。

## 你需要准备的信息

在 [Apple Developer](https://developer.apple.com/account/resources/certificates/list) → Certificates 创建并下载安装到「登录」钥匙串：

| 证书 | 用途 |
|------|------|
| **Developer ID Application** | 签名 `.app`、dylib、`ocg-vpnhost` |
| **Developer ID Installer** | 签名 `.pkg` |

并准备公证账号（推荐）：

| 变量 | 说明 |
|------|------|
| `APPLE_ID` | 苹果开发者账号邮箱 |
| `APPLE_TEAM_ID` | 10 位 Team ID（Xcode → Settings → Accounts → Team） |
| `APPLE_APP_SPECIFIC_PASSWORD` | [appleid.apple.com](https://appleid.apple.com) → App 专用密码 |

安装证书后，钥匙串「我的证书」应能看到类似：

```text
Developer ID Application: 你的名字 (TEAMID)
Developer ID Installer: 你的名字 (TEAMID)
```

把完整字符串发给构建环境，或本地 export：

```bash
export OCG_SIGN_IDENTITY="Developer ID Application: 你的名字 (TEAMID)"
export OCG_INSTALLER_IDENTITY="Developer ID Installer: 你的名字 (TEAMID)"
export OCG_NOTARIZE=1
export APPLE_ID="you@example.com"
export APPLE_TEAM_ID="TEAMID"
export APPLE_APP_SPECIFIC_PASSWORD="xxxx-xxxx-xxxx-xxxx"
export OCG_VERSION=2.2.0
./scripts/package-macos-pkg.sh          # arm64
./scripts/package-macos-pkg.sh osx-x64  # Intel
```

## 本地先试用（无 Developer ID）

```bash
export OCG_VERSION=2.2.0
./scripts/package-macos-pkg.sh
open dist/OpenConnectGui-2.2.0-macos-arm64.pkg
```

若提示无法打开：右键 → 打开，或：

```bash
xattr -cr dist/OpenConnectGui-2.2.0-macos-arm64.pkg
```

## 请回传给我的内容（可打码邮箱）

1. `security find-identity -v -p codesigning` 完整输出（装好 Developer ID 之后）  
2. Team ID  
3. 是否需要我帮你走公证（需要 App 专用密码，建议你本地 export，不要贴到聊天）
