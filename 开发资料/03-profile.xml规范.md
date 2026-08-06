# 03 — profile.xml 规范

## 1. 获取方式

```http
GET {$url}/profile.xml HTTP/1.1
Host: ...
Accept: application/xml, text/xml, */*
```

| 项 | 建议 |
|----|------|
| 超时 | 15–30 秒 |
| HTTPS | 按服务器证书策略校验；自签环境需单独处理 |
| 失败 | 对用户展示可读错误（超时 / 404 / 网络不可达 / XML 非法），并允许重试 |
| 缓存 | 可短时缓存；启动与登录时应重新拉取以保证节点最新 |

成功响应体为 XML（Cisco AnyConnect Profile 结构）。样例文件见同目录 [profile.xml.sample](./profile.xml.sample)。

## 2. 结构要点

根元素一般为 `AnyConnectProfile`。客户端对接时**必须解析**：

```text
AnyConnectProfile
 └─ ServerList
      └─ HostEntry          ← 每个节点一条
           ├─ HostName      ← 显示名称（如「香港专线」）
           ├─ HostAddress   ← VPN 网关 URL（连接用）
           └─ UserGroup     ← 可选，认证组
```

可选解析：

```text
ClientInitialization
 └─ BackupServerList
      └─ HostAddress        ← 备用服务器；可并入节点列表（显示名自拟）
```

其它 `ClientInitialization` 字段（证书策略、LocalLanAccess 等）多为官方 AnyConnect 客户端使用；自研 APP **可忽略**，不影响基础连通。

## 3. 字段定义

| 字段 | 必填 | 说明 |
|------|------|------|
| `HostName` | 建议有 | UI 展示名；缺失时可用 `HostAddress` 代替 |
| `HostAddress` | **必填** | 完整网关 URL，直接作为 OpenConnect 连接目标 |
| `UserGroup` | 可选 | 若存在，连接时作为 authgroup / group 传入 |

### HostAddress 形态示例

```text
https://gw.example.com:10000/hkg
https://us.gw.example.com:10000
https://vpn.example.com/backup
```

解析时注意：

- 可能含 **非 443 端口**
- 可能含 **路径**（如 `/hkg`、`/twg`），路径是网关路由的一部分，**不可丢弃**
- 应以完整字符串交给协议库的 URL 解析接口（如 `openconnect_parse_url`）

## 4. 最小样例

```xml
<?xml version="1.0" encoding="UTF-8"?>
<AnyConnectProfile xmlns="http://schemas.xmlsoap.org/encoding/">
  <ServerList>
    <HostEntry>
      <HostName>香港专线</HostName>
      <HostAddress>https://gw.example.com:10000/hkg</HostAddress>
    </HostEntry>
    <HostEntry>
      <HostName>美国专线</HostName>
      <HostAddress>https://us.gw.example.com:10000</HostAddress>
    </HostEntry>
  </ServerList>
</AnyConnectProfile>
```

## 5. 解析伪代码

```text
doc = ParseXml(body)
nodes = []

for each HostEntry under //ServerList:
  name = HostEntry/HostName or HostAddress
  addr = HostEntry/HostAddress.trim()
  group = HostEntry/UserGroup   // may be null
  if addr not empty:
    nodes.append({ name, address: addr, userGroup: group })

# optional
for each HostAddress under //BackupServerList:
  nodes.append({ name: "备用服务器", address: HostAddress.trim() })

return nodes
```

> XML 可能带默认命名空间。解析时请用 **local-name** 匹配，或正确处理命名空间，避免取不到节点。

## 6. 映射到连接参数

| profile 字段 | 连接参数 |
|--------------|----------|
| `HostAddress` | gateway url |
| （本地）账号 | username |
| （本地）密码 | password |
| `UserGroup`（若有） | authgroup / group |

## 7. 错误码建议（客户端）

| 场景 | 建议提示 |
|------|----------|
| `$url` 为空 | 请先填写服务器地址 |
| HTTP 404 | 服务器未提供 profile.xml |
| 超时 | 获取节点列表超时，请检查网络 |
| 连接失败 | 无法连接到服务器获取节点列表 |
| XML 非法 / 无 HostEntry | profile.xml 无效或未配置节点 |
