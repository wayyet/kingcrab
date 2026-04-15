# OpenClaw 插件系统深度解析：从发现到动态加载

OpenClaw 的插件系统是其可扩展性的核心支柱，它支持四种不同的插件来源，每种来源都有独特的发现机制、运行时模式和通信协议。本文将基于 OpenClaw 源码，深入剖析这一复杂而精密的插件架构。

## 一、插件系统的整体架构

OpenClaw 的插件子系统位于三个核心项目的交汇处：

- **OpenClaw.Core** — 数据模型、发现算法、验证逻辑
- **OpenClaw.Agent** — 宿主编排、桥接传输、原生注册表
- **OpenClaw.PluginKit** — 用于动态 .NET 插件的公共 SDK

网关在 `RuntimeInitializationExtensions` 中的组合根充当指挥者，依次调用每个来源的加载器，并将结果合并到统一的 `PluginComposition` 对象中。

### 四种插件来源概览

| 来源 | 入口点 | 运行时模式 | 传输方式 | 注册面 |
|------|--------|------------|----------|--------|
| Bridge 插件 (TS/JS) | `PluginHost` | 任意 (AOT + JIT) | 基于 stdio/socket 的 JSON-RPC | 工具、频道、命令、提供者、钩子、技能 |
| 原生插件副本 (C#) | `NativePluginRegistry` | 任意 (AOT + JIT) | 进程内 | 工具 |
| 动态原生插件 (.NET) | `NativeDynamicPluginHost` | 仅 JIT | 进程内，`AssemblyLoadContext` | 工具、频道、命令、提供者、钩子、服务、技能 |
| MCP 服务器 | `McpServerToolRegistry` | 任意 (AOT + JIT) | MCP stdio/HTTP | 工具 |

这四种来源可以同时提供工具，最终的工具集由基于优先级的解析步骤决定。

## 二、插件发现机制

### 2.1 Bridge 插件发现

Bridge 插件是由静态 `PluginDiscovery` 类从文件系统中发现的 TypeScript 或 JavaScript 文件。发现算法遵循严格的优先级顺序：**配置路径 → 工作区扩展 → 全局扩展**。

**搜索位置：**

扫描器会在入口点旁边查找 `openclaw.plugin.json` 清单文件。当没有清单时，文件的基本名称将成为插件 ID（独立文件模式）。基于清单的发现支持更丰富的元数据，包括 `Kind`（用于独占槽位分配）、`Channels`、`Providers`、`Skills` 和 `ConfigSchema`。

**入口点解析：**

对于基于清单的插件，解析器会按顺序检查固定的候选列表：`index.ts`、`index.js`、`index.mjs`、`src/index.ts`、`src/index.js`、`src/index.mjs`。如果没有匹配项，则会回退到 `package.json` 的 `openclaw.extensions` 数组条目。作为最后手段，会接受插件根目录中单个 `.ts`、`.js` 或 `.mjs` 文件。所有路径都会使用 `TryResolveContainedPath` 针对符号链接逃逸进行验证，该方法会解析符号链接以防止目录遍历攻击。

### 2.2 过滤与访问控制

发现完成后，`PluginDiscovery.Filter` 方法会应用四层过滤管道：

1. **拒绝列表** (`Plugins:Deny`) — 硬性排除；拒绝列表始终优先于允许列表
2. **允许列表** (`Plugins:Allow`) — 当非空时，只有列出的插件才能通过
3. **单个插件启用** (`Plugins:Entries:{id}:Enabled`) — 单独开关（默认：`true`）
4. **槽位独占性** (`Plugins:Slots:{kind}`) — 当设置后，只有该类型指定的插件 ID 才能通过

### 2.3 动态原生插件发现

动态原生插件的发现遵循与 Bridge 插件相同的模式，但扫描的是原生清单格式 (`openclaw.native-plugin.json`)：

```json
{
  "id": "my-custom-plugin",
  "name": "My Custom Plugin",
  "version": "1.2.0",
  "minHostVersion": "0.9.0",
  "pluginApiVersion": "1.0.0",
  "assemblyPath": "bin/MyCustomPlugin.dll",
  "typeName": "MyCustomPlugin.EntryPoint, MyCustomPlugin",
  "capabilities": ["tools", "hooks"],
  "skills": ["skills/"],
  "jitOnly": true
}
```

发现位置按优先级顺序为：配置路径 → 工作区 `.openclaw/native-plugins/` → 全局 `~/.openclaw/native-plugins/`。

## 三、TS/JS Bridge 传输层

Bridge 传输层是 OpenClaw 的进程间通信层，它使 TypeScript 和 JavaScript 插件能够作为一等参与者融入 .NET 网关运行时。

### 3.1 架构设计

Bridge 传输层基于**宿主-工作模型**运行。.NET 网关充当 JSON-RPC *客户端*（宿主），而每个 TypeScript/JavaScript 插件则作为独立的 *Worker* 进程运行。通信流经两个通道：**请求**（网关 → 插件，同步请求/响应）和**通知**（插件 → 网关，异步触发即忘）。

一个关键的约束是，**stdout 必须完全保留给 JSON-RPC 流量**。JS 和 .NET 双方都强制执行此规则：JS Worker 在启动时将 `console.log` 重定向到 `stderr`，而 .NET 宿主的所有诊断输出均使用结构化日志记录器。

### 3.2 JSON-RPC 协议契约

Bridge 协议使用了一种简化的 JSON-RPC 2.0 变体。stdout 上的每条消息都是单个 JSON 对象，以换行符 (`\n`) 结尾。

**请求封包（网关 → 插件）：**
```json
{
  "method": "init",
  "id": "req-001",
  "params": { /* ... */ }
}
```

**响应封包（插件 → 网关）：**
```json
{
  "id": "req-001",
  "result": { /* 成功载荷 */ },
  "error": null
}
```

**通知封包（插件 → 网关）：**
```json
{
  "notification": "channel_message",
  "params": { /* 事件载荷 */ }
}
```

### 3.3 Worker 端实现

标准的参考实现位于 `src/whatsapp-baileys-worker/` 目录下。入口点模式如下：

```javascript
// 1. 将 console.log 重定向到 stderr —— stdout 保留给 JSON-RPC
console.log = console.error;

// 2. 导入协议基本方法和插件引擎
import { readRequests, sendNotification } from "./protocol.mjs";
import { BaileysEngine } from "./engine.mjs";

// 3. 实例化引擎并定义方法分发器
const engine = new BaileysEngine();

async function handleRequest(request) {
  const { method, params } = request;
  switch (method) {
    case "init": return { channels: [...], capabilities: [...], compatible: true };
    case "channel_start": return await engine.start();
    case "channel_send": return await engine.send(params);
    case "shutdown": await engine.stop(); return { shutdown: true };
    // ...
  }
}

// 4. 进入 stdio JSON-RPC 循环 —— 阻塞直到关闭
readRequests(handleRequest);
```

### 3.4 传输模式

`BridgeTransportConfig` 定义了三种传输模式：

| 模式 | 描述 | 状态 |
|------|------|------|
| `stdio` | 基于进程 stdin/stdout 的双向 JSON-RPC | ✅ 完全实现 |
| `socket` | Unix 域套接字或命名管道 | 🔧 可配置 |
| `hybrid` | Stdio 用于控制平面，Socket 用于高吞吐量数据 | 🔧 预留 |

**Stdio 是唯一经过实战检验的传输方式。**

## 四、动态原生插件加载

原生动态插件通过在运行时将第三方 .NET 程序集直接加载到宿主进程中，从而扩展 OpenClaw。与通过跨进程 JSON-RPC 进行通信的桥接插件不同，原生动态插件在进程内执行，并拥有对 OpenClaw.PluginKit API 表面的完整访问权限。

### 4.1 程序集加载与隔离

原生动态插件被加载到可卸载的 **`AssemblyLoadContext`** 中，从而实现热重载能力。

加载上下文实现了一种刻意的程序集共享策略：

| 程序集名称模式 | 解析行为 |
|----------------|----------|
| `System.*`, `System` | 从宿主共享 |
| `Microsoft.*` | 从宿主共享 |
| `netstandard` | 从宿主共享 |
| `OpenClaw.Core` | 从宿主共享 |
| `OpenClaw.PluginKit` | 从宿主共享 |
| **其他所有程序集** | 从插件自身的依赖目录加载 |

### 4.2 兼容性验证

在加载任何程序集之前，宿主会执行三道验证关卡：

1. **最低宿主版本** — `minHostVersion` 与宿主版本比较
2. **插件 API 版本** — `pluginApiVersion` 主版本号必须与宿主的 OpenClaw.PluginKit 主版本号完全匹配
3. **PluginKit 引用检查** — 检查程序集引用的 OpenClaw.PluginKit 主版本号

所有失败都会在加载报告中生成结构化的诊断信息。

### 4.3 PluginKit API 表面

`OpenClaw.PluginKit` NuGet 包定义了三个核心接口：

```csharp
public interface INativeDynamicPlugin
{
    void Register(INativeDynamicPluginContext context);
}

public interface INativeDynamicPluginContext
{
    string PluginId { get; }
    JsonElement? Config { get; }
    ILogger Logger { get; }
    
    void RegisterTool(ITool tool);
    void RegisterChannel(IChannelAdapter channel);
    void RegisterCommand(string name, string description, CommandHandler handler);
    void RegisterProvider(string providerId, string[] models, AIFactory.IChatClient client);
    void RegisterHook(IToolHook hook);
    void RegisterService(INativeDynamicPluginService service);
    void RegisterSkillDirectory(string path);
}
```

## 五、注册上下文与能力策略

### 5.1 能力策略与 AOT 门控

`PluginCapabilityPolicy` 类针对每个插件强制执行运行时模式约束。在 AOT 模式下：

- **Bridge 插件被完全允许**（它们在类型化的 JSON-RPC 边界外作为独立进程运行）
- **动态原生插件会被完全阻止**，因为它们依赖于运行时反射和 `AssemblyLoadContext` 加载

该策略定义了插件可以声明的七种能力字符串：`tools`、`services`、`skills`、`channels`、`commands`、`providers`、`hooks` 和 `native_dynamic`。

### 5.2 基于优先级的工具解析

在所有四个来源都贡献了它们的工具之后，`NativePluginRegistry.ResolvePreference` 方法会执行最后的去重传递：

1. **内置工具** — 在名称冲突时始终获胜
2. **单工具覆盖** (`Plugins:Overrides:{tool-name}`) — 显式的 `native` 或 `bridge` 指定优先
3. **全局默认** (`Plugins:Prefer`) — 当设置为 `"native"`（默认值）时，原生副本赢得平局；`"bridge"` 则反转此逻辑

## 六、启动时的插件组合

`RuntimeInitializationExtensions` 中的 `LoadPluginCompositionAsync` 方法是串联所有四个来源的唯一编排点：

```
Bridge 插件加载 → MCP 工具注册（到原生注册表）→ 动态原生插件加载 → 优先级解析 → PluginComposition
```

该方法返回一个 `PluginComposition` 记录，包含所有加载的工具、频道适配器、钩子、命令、提供者、技能根目录、诊断信息和宿主引用。

## 七、三种扩展机制的对比

| 维度 | Bridge (TS/JS) | 原生动态 (.NET) | MCP 服务器 |
|------|----------------|-----------------|------------|
| 语言 | TypeScript, JavaScript | C# (仅限 JIT 模式) | 任意 |
| 运行时模式 | 仅限 JIT | 仅限 JIT | JIT 和 AOT |
| 隔离性 | 进程边界 | AppDomain / 同进程 | 进程边界 |
| 启动成本 | Node.js 进程生成 (~100-300ms) | 程序集加载 (~10-50ms) | 进程生成 (~100-500ms) |
| 通道支持 | ✓ 完整 | ✓ 通过 IChannelAdapter | ✗ 不适用 |
| Hook 支持 | ✓ 前置/后置 | ✓ 通过 IToolHook | ✗ 不适用 |
| AOT 兼容 | ✗ | ✗ (JitOnly = true) | ✓ (stdio 传输) |

## 八、总结

OpenClaw 的插件系统是一个设计精良的多层次扩展平台：

1. **Bridge 插件** 适合需要外部运行时（如 Node.js）的场景，提供完全的进程隔离
2. **原生动态插件** 适合需要深度集成和高性能的场景，支持进程内加载和热重载
3. **MCP 服务器** 提供了与外部工具生态系统的标准化连接

理解这三种机制的差异和适用场景，将帮助开发者为 OpenClaw 选择最合适的扩展方式。