# 沙箱 WebSocket 聊天协议

## 连接建立

脚本通过 WebSocket 连接到沙箱端点后，沙箱首先发送握手消息。

### 认证模式

**方式 A：需要认证**

```
脚本 ──CONNECT──→ 沙箱
脚本 ←──{"type":"auth_required"}── 沙箱
脚本 ──{"type":"auth","access_token":"<token>"}──→ 沙箱
脚本 ←──{"type":"auth_ok"}── 沙箱
脚本 ←──{"type":"ready"}── 沙箱 (可选)
```

**方式 B：无需认证**

连接后沙箱直接发送 `{"type":"ready"}`，脚本跳过认证步骤。

### 认证令牌格式

- 支持 `env:VAR_NAME` 环境变量引用
- 支持 `raw:VALUE` 明文（仅限开发环境）
- 脚本内部使用 `SecretResolver` 逻辑解析令牌

## 消息格式

### Chat 请求（脚本 → 沙箱）

```json
{
  "id": "<递增整数>",
  "type": "chat",
  "prompt": "<用户消息内容>",
  "system_prompt": "<可选，系统提示词>"
}
```

### Chat 响应（沙箱 → 脚本）

成功：
```json
{
  "id": "<对应请求的id>",
  "type": "result",
  "success": true,
  "result": {
    "text": "<响应文本>",
    "testcases": [<可选，测试用例数组>],
    "...": "<沙箱自定义字段>"
  }
}
```

失败：
```json
{
  "id": "<对应请求的id>",
  "type": "result",
  "success": false,
  "error": {
    "code": "<错误码>",
    "message": "<错误描述>"
  }
}
```

### 事件消息（沙箱 → 脚本）

沙箱可主动推送事件：
```json
{
  "type": "event",
  "event_type": "<事件类型>",
  "data": { }
}
```

## 连接管理

- 空闲连接通过 `KeepAliveInterval` (20s) 维持
- 请求超时使用 `RequestTimeoutSeconds` 控制
- 异常断开后自动重连（指数退避，最大 30s）
- 会话结束后正常关闭 WebSocket：`CloseAsync(NormalClosure)`

## 跨平台注意

- Windows：脚本通过 `pwsh.exe` 或 `python.exe` 调用 `System.Net.WebSockets.ClientWebSocket`
- Linux/macOS：脚本中 WebSocket 使用 Python 的 `websockets` 库或 pwsh 的 `System.Net.WebSockets`
- URL scheme 自动转换：`https://` → `wss://`，`http://` → `ws://`
