# WebChat 连接问题排查与修复

## 问题描述

WebChat 页面（http://localhost:18789/chat）显示连接错误：

```
Connection dropped. Retrying in 1s...
```

浏览器控制台报错：

```
WebSocket connection to 'ws://localhost:18789/ws' failed: HTTP Authentication failed; no valid credentials available
```

---

## 排查过程

### 第一步：确认 Gateway 服务状态

通过终端检查 Gateway 进程和健康状态：

```powershell
# 检查进程
Get-Process | Where-Object {$_.ProcessName -like "*OpenClaw*"}

# 健康检查
Invoke-RestMethod -Uri "http://localhost:18789/health"
```

**发现**：Gateway 进程不存在，服务已停止。

### 第二步：启动 Gateway 服务

```powershell
cd c:\Users\wayye\Documents\ai4c_Projects\kingcrab\src\OpenClaw.Gateway
dotnet run
```

服务启动后，WebSocket 仍然认证失败。

### 第三步：分析认证失败原因

查看 Gateway 日志，发现关键错误：

```
Request finished HTTP/1.1 GET http://localhost:18789/ws?token=kingcrab - 401 0
```

以及更详细的错误：

```
Failed to validate the token.
Microsoft.IdentityModel.Tokens.SecurityTokenMalformedException: IDX14100: JWT is not well formed, there are no dots (.).
The token needs to be in JWS or JWE Compact Serialization Format.
```

这说明 Gateway 正在尝试将 `kingcrab` 作为 **JWT Token** 来验证，但实际上我们提供的是简单的 **静态 Token**。

### 第四步：检查配置文件

查看 `appsettings.json` 发现以下问题：

| 配置项 | 原值 | 问题 |
|--------|------|------|
| `AuthMode` | `"oidc"` | Gateway 以 OIDC 模式运行，期望 JWT 格式 |
| `AlwaysRequireAuth` | `true` | 强制要求认证 |
| `AllowQueryStringToken` | `false` | 不允许 URL 查询字符串传递 Token |

---

## 修复方案

修改 `src/OpenClaw.Gateway/appsettings.json`：

```diff
- "AllowQueryStringToken": false,
+ "AllowQueryStringToken": true,

- "AuthMode": "oidc",
+ "AuthMode": "token",

- "AlwaysRequireAuth": true
+ "AlwaysRequireAuth": false
```

然后**重启 Gateway 服务**使配置生效。

---

## 修复结果

重启后，WebChat 页面成功连接：

```
Connected successfully.
```

---

## 技术架构说明

### 连接流程

```
浏览器 (WebChat)
    │
    │  WebSocket 连接
    │  ws://localhost:18789/ws?token=kingcrab
    │
    ▼
OpenClaw.Gateway (端口 18789)
    │
    │  根据 AuthMode 配置验证 Token
    │
    ▼
Agent 运行时 (Microsoft Agent Framework)
```

### 配置项说明

| 配置项 | 说明 |
|--------|------|
| `AuthMode` | 认证模式：`token`（静态Token） 或 `oidc`（JWT/OIDC） |
| `AlwaysRequireAuth` | 是否强制要求认证 |
| `AllowQueryStringToken` | 是否允许通过 URL 查询参数传递 Token |
| `AuthToken` | 静态 Token 的值（默认 `kingcrab`） |

### 认证模式对比

| 模式 | Token 格式 | 适用场景 |
|------|-----------|----------|
| `token` | 静态字符串，如 `kingcrab` | 开发/内网环境 |
| `oidc` | JWT 格式，需 OIDC Provider 签发 | 生产/需要第三方认证 |

---

## 总结

本次问题有两个根本原因：

1. **Gateway 服务未运行** — 导致 `ERR_CONNECTION_REFUSED`
2. **认证配置错误** — Gateway 以 OIDC 模式运行，但客户端使用简单 Token 认证

**经验教训**：

- 排查连接问题时，先确认服务是否运行
- 查看服务器日志可以获得详细的错误信息
- 配置文件中的认证模式必须与客户端一致

---

## 相关文件

- 配置文件：`src/OpenClaw.Gateway/appsettings.json`
- 前端页面：`src/OpenClaw.Gateway/wwwroot/webchat.html`
- WebSocket 处理：`src/OpenClaw.Gateway/Endpoints/WebSocketEndpoints.cs`
- 认证逻辑：`src/OpenClaw.Gateway/GatewaySecurity.cs`
