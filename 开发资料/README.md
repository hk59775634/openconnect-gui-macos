# SSLVPN / OpenConnect 开发资料包

面向第三方代理自研 APP 的对接资料。协议为 **Cisco AnyConnect 兼容 SSLVPN**（业界常称 OpenConnect 协议）。

## 文档索引

| 文档 | 内容 |
|------|------|
| [01-协议概述.md](./01-协议概述.md) | 协议是什么、兼容范围、与 AnyConnect 的关系 |
| [02-接入方式与三要素.md](./02-接入方式与三要素.md) | 连接所需参数：URL / 账号 / 密码 |
| [03-profile.xml规范.md](./03-profile.xml规范.md) | 节点列表获取方式与 XML 字段说明 |
| [04-连接流程.md](./04-连接流程.md) | 认证 → 隧道 → TUN 的标准流程 |
| [05-客户端开发指南.md](./05-客户端开发指南.md) | 推荐实现路径（库模式 / 命令行）、平台注意点 |
| [06-参考资源与合规.md](./06-参考资源与合规.md) | 开源项目链接、许可证、联调建议 |
| [profile.xml.sample](./profile.xml.sample) | 结构样例（示例域名，可本地对照解析） |

## 一句话说明

流程为：下载 `$url/profile.xml` 得到节点 → 用户输入账号密码 → 用 OpenConnect / AnyConnect 兼容客户端连到节点 `HostAddress`。

## 快速对照

```
服务器根地址 $url
        │
        ▼
GET $url/profile.xml
        │
        ▼
解析 ServerList / HostEntry
  · HostName  → 界面显示名
  · HostAddress → 真正的 VPN 网关 URL
        │
        ▼
连接三要素
  · url      = HostAddress（来自 profile）
  · username = 用户输入（本地保存）
  · password = 用户输入（本地保存）
        │
        ▼
OpenConnect / libopenconnect 建立 SSLVPN
```

## 适用平台

| 平台 | 常见方案 |
|------|----------|
| Windows | libopenconnect + Wintun/TAP；或官方 OpenConnect GUI 参考 |
| Android | openconnect 安卓移植 / AnyConnect 兼容库；需注意 TUN 权限 |
| iOS | Network Extension + 兼容实现（需自行评估上架与私有 API） |
| macOS / Linux | openconnect CLI 或 libopenconnect |

具体选型见 [05-客户端开发指南.md](./05-客户端开发指南.md)。

## 版本

| 项 | 说明 |
|----|------|
| 文档版本 | 1.0 |
| 日期 | 2026-08-04 |
| 协议族 | Cisco AnyConnect SSLVPN（CSTP/DTLS） |
| 节点配置 | AnyConnect Profile XML（`profile.xml`） |
