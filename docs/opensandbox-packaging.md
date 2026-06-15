# OpenSandbox 镜像打包建议

本文档讨论的是“把 OpenClaw.NET 作为被 OpenSandbox 托管运行的应用镜像”这一场景，不是让 OpenClaw.NET 去充当沙箱服务端。

## 设计目标

如果只是普通部署，仓库根目录现有的 `Dockerfile` 已经够用；它的目标是最小化、偏生产、偏收敛的网关镜像。

但如果镜像要交给 OpenSandbox 去拉起，并希望在沙箱里保留更接近完整系统的体验，打包目标应该改成下面这套原则：

1. 保留完整运行时能力，而不是极限瘦身
2. 保留 Shell、Node.js 和插件桥能力，避免把系统体验裁掉
3. 使用独立镜像和独立脚本，不污染现有生产镜像职责
4. 给 OpenSandbox 预留稳定工作目录、可写目录、健康检查和非 root 用户
5. 通过 OpenSandbox 自己的 TTL、资源限制、网络策略来做运行期约束，而不是把镜像做成不可操作的极简壳

这些原则和 OpenSandbox 官方示例一致：示例镜像通常会单独提供 Dockerfile、非 root 用户、明确入口命令、必要运行时依赖，以及 buildx 多架构脚本。OpenSandbox 在创建沙箱时以镜像 + entrypoint + env + resource/network policy 的方式编排容器，因此你的镜像应重点保证“可运行、可诊断、可暴露端口”，而不是把自己做成宿主机侧的安全控制器。

## 为什么不能直接复用当前 Dockerfile

当前根目录的 `Dockerfile` 更适合普通网关部署，不适合“完整系统体验”的沙箱模板，原因主要有这些：

1. 使用 NativeAOT + chiseled runtime，镜像很瘦，但不利于沙箱内诊断和扩展
2. 默认关闭 `OpenClaw__Tooling__AllowShell`
3. 默认关闭插件 `OpenClaw__Plugins__Enabled`
4. 只允许 `/app/workspace`，运行时相对封闭
5. 没有 Node.js，而仓库里的 JS/TS plugin bridge 和部分工具能力依赖 `node`

如果把这个镜像直接放进 OpenSandbox，你能跑起来一个“受限网关”，但得不到你想要的“接近完整系统”的体验。

## 推荐做法

仓库里已经新增了专门给 OpenSandbox 用的文件：

1. `Dockerfile.opensandbox`
2. `scripts/build-opensandbox-image.ps1`

这套方案的关键点是：

1. 构建阶段仍然从源码 `dotnet publish`
2. 运行阶段改为 `mcr.microsoft.com/dotnet/aspnet:10.0-noble`
3. 额外安装 `bash`、`curl`、`git`、`jq`、`procps`
4. 安装 Node.js 22，保留 plugin bridge 和 JS 执行体验
5. 默认切到 `OpenClaw__Runtime__Mode=jit`
6. 默认开启 `OpenClaw__Tooling__AllowShell=true`
7. 默认开启 `OpenClaw__Plugins__Enabled=true`
8. 约定 `/workspace` 为工作目录挂载点，`/app/memory` 为状态目录挂载点
9. 使用独立非 root 用户 `openclaw`
10. 保留 `/health` 对应的进程健康检查

这和 OpenSandbox 官方 `examples/openclaw` 的思路是一致的：由 OpenSandbox 负责拉起网关镜像、轮询健康、暴露端口；镜像自己只负责把应用进程以稳定方式跑起来。

## 打包方式

Windows 下可以直接用 PowerShell 脚本：

```powershell
./scripts/build-opensandbox-image.ps1 -ImageName ghcr.io/your-org/openclaw.net-opensandbox -Tag latest -Push
```

如果你只是本机测试，不推仓库：

```powershell
./scripts/build-opensandbox-image.ps1 -ImageName openclaw.net-opensandbox -Tag dev -Platforms linux/amd64 -Load
```

脚本默认：

1. 使用 `docker buildx`
2. 构建 `linux/amd64,linux/arm64`
3. 读取根目录 `Dockerfile.opensandbox`
4. 打入 OCI labels
5. 默认启用 `OpenClawEnableOpenSandbox=true`
6. 使用 `-Load` 做本地测试时，只应指定单一平台；多架构镜像请用 `-Push`

## 在 OpenSandbox 里怎么启动更合适

推荐把镜像作为一个完整 HTTP 服务来创建沙箱，而不是把 entrypoint 改成一段临时 shell 命令。也就是说，优先直接使用镜像内默认入口：

1. 镜像默认 `ENTRYPOINT ["/app/OpenClaw.Gateway"]`
2. OpenSandbox 负责 TTL、网络、资源和端口映射
3. SDK 或示例侧通过 `get_endpoint(18789)` 获取对外地址
4. 健康检查用 `http://<sandbox-endpoint>/health`

如果你用 OpenSandbox SDK，调用层大致应该传这些信息：

1. `image`: 你刚打好的镜像
2. `timeout`: 例如 3600 秒
3. `env`: 传入 `MODEL_PROVIDER_KEY`、`OPENCLAW_GATEWAY_TOKEN` 等运行参数
4. `resource`: 显式给 CPU/内存上限
5. `networkPolicy`: 默认 deny，再按需放行上游 LLM、插件依赖和对象存储
6. `health_check`: 轮询 `/health` 返回 200

## 最佳实践

1. 镜像和沙箱策略分层

镜像负责“能跑、能调、能自检”。
OpenSandbox 负责“隔离、TTL、资源、网络策略、卷挂载、生命周期”。

2. 不要把生产最小镜像强行复用成沙箱镜像

生产最小镜像追求攻击面小；沙箱镜像追求体验完整、可调试、可执行。它们可以共享源码，但不该共享同一个 Dockerfile。

3. 运行时优先用 JIT，而不是 AOT

你这个仓库有插件桥、Node 依赖、更多动态能力。要保留“类似完整系统”的体验，JIT 比 NativeAOT 更稳妥。

4. 保留 Node.js

仓库内的 JS/TS plugin bridge、`browser` 工具链以及部分代码执行路径都依赖 `node`。如果不带 Node，沙箱里会出现“网关起来了，但能力缺半截”的问题。

5. 把可写状态和工作区显式分离

建议固定：

1. `/app/memory` 放状态和 sqlite
2. `/workspace` 放用户工作目录或挂载卷

这样既方便 OpenSandbox 绑定 volume，也方便后续做持久化或只读挂载区分。

6. 网络策略默认收紧

如果你在 OpenSandbox 中跑这个镜像，强烈建议不要放开全量外网。参考 OpenSandbox 官方示例的做法，优先：

1. `defaultAction = deny`
2. 只允许模型供应商域名
3. 只允许你确实需要的插件依赖域名
4. 如果不需要源码拉取，就不要放开 `github.com`
5. Token 用量推送只放行**采集器一个端点**，绝不放行内网 Kafka 集群

### Token 用量推送：用沙箱外采集器，不要直连 Kafka

网关在沙箱里只跑一个 HTTP 瘦客户端（`HttpTokenUsageSink`），把 Token 用量事件批量 POST 给一个**沙箱外的长命采集器**（`OpenClaw.TokenCollector`）；由采集器持有 Kafka 生产者、密钥和到内网 broker 的连接。这样沙箱镜像里不再编入 `Confluent.Kafka`、不注入 `KAFKA_SASL_*`，出网白名单也从“整个 Kafka 集群”收敛到“采集器一个地址”。

- `networkPolicy`：只在白名单里放行采集器端点（如 `http://token-collector:8088`），**不要**放行内网 Kafka broker。
- 通过 `env` 注入（而不是写进镜像）：
  - `OpenClaw__TokenUsage__Sink=http`
  - `OpenClaw__TokenUsage__Http__CollectorUrl=http://<采集器地址>:8088/ingest/token-usage`
  - `OpenClaw__TokenUsage__Http__AuthTokenRef=env:TOKEN_COLLECTOR_TOKEN`
  - `TOKEN_COLLECTOR_TOKEN=<与采集器共享的 Bearer 密钥>`
- **不再向沙箱注入** `KAFKA_SASL_USER` / `KAFKA_SASL_PASS` 等 broker 凭据——它们只属于采集器。

7. 用非 root 用户运行

这不是替代沙箱隔离，而是额外一层最小权限原则。镜像里已经按这个思路处理。

8. 不要在镜像里硬编码真实密钥

像 `MODEL_PROVIDER_KEY`、`OPENCLAW_GATEWAY_TOKEN` 这类配置，应在 OpenSandbox 创建实例时通过 `env` 注入，或者通过它的 Secret/Volume 机制传入。

## 什么时候还需要再拆镜像

如果你后面发现“完整系统体验”还包括浏览器自动化、Playwright 或桌面能力，那建议继续拆分专用镜像，而不是把所有东西塞进一个通用网关镜像：

1. `openclaw.net-opensandbox-gateway`
2. `openclaw.net-opensandbox-browser`
3. `openclaw.net-opensandbox-desktop`

原因很简单：浏览器和桌面依赖会显著放大镜像体积、启动时间和攻击面。OpenSandbox 官方也是按场景拆成不同示例镜像，而不是做一个超级胖镜像。

## 建议的最小运行参数

把这个镜像交给 OpenSandbox 时，建议至少传入：

```text
OpenClaw__BindAddress=0.0.0.0
OpenClaw__Port=18789
OpenClaw__Runtime__Mode=jit
OpenClaw__Tooling__WorkspaceRoot=/workspace
OpenClaw__Memory__StoragePath=/app/memory
MODEL_PROVIDER_KEY=...
OPENCLAW_GATEWAY_TOKEN=...
```

如果沙箱卷挂载到了 `/workspace`，这套约定可以直接复用。

## 本地快速测试启动

下面是经过实际验证可以直接跑起来的 `docker run` 命令，适合在本机快速验证镜像功能。

```powershell
docker run -d `
  --name openclaw-test `
  -p 18789:18789 `
  -e OPENCLAW_AUTH_TOKEN=local-test-token `
  -e OpenClaw__Llm__Provider=openai `
  -e OpenClaw__Llm__Model=MiniMax-M2.5 `
  -e "OpenClaw__Llm__ApiKey=<你的 API Key>" `
  -e OpenClaw__Llm__Endpoint=https://api.minimaxi.com/v1 `
  -e OpenClaw__Security__AllowUnsafeToolingOnPublicBind=true `
  -e OpenClaw__Security__AllowPluginBridgeOnPublicBind=true `
  -e OpenClaw__Security__TrustForwardedHeaders=true `
  ai4c-tcr.tencentcloudcr.com/agentfoundry/king-crab:opensandbox-latest
```

### 参数说明

| 参数 | 说明 |
|------|------|
| `OPENCLAW_AUTH_TOKEN` | 必填。Bearer Token，所有 WebSocket/HTTP 请求都需要携带此值 |
| `OpenClaw__Llm__Provider` | LLM 提供商，`openai` 表示兼容 OpenAI 接口的服务 |
| `OpenClaw__Llm__Model` | 模型名称 |
| `OpenClaw__Llm__ApiKey` | LLM API Key |
| `OpenClaw__Llm__Endpoint` | 自定义 API 地址，使用第三方兼容服务时需要设置 |
| `AllowUnsafeToolingOnPublicBind` | **本地测试专用**。绑定 `0.0.0.0` 时需要显式开启才能使用 shell/file 工具 |
| `AllowPluginBridgeOnPublicBind` | **本地测试专用**。允许插件桥在公网绑定下工作 |
| `TrustForwardedHeaders` | 配合反向代理使用；本地直连可不加，但无副作用 |

> ⚠️ `AllowUnsafeToolingOnPublicBind=true` 仅用于本地开发测试，生产环境应通过 OpenSandbox 的网络策略和资源隔离代替此开关，或绑定到 loopback 地址。

### 启动后验证

查看日志确认服务已就绪：

```powershell
docker logs openclaw-test
```

正常启动时日志会出现：

```
Now listening on: http://0.0.0.0:18789
Application started.
```

服务地址：
- **Web 界面**：`http://localhost:18789`
- **WebSocket**：`ws://localhost:18789/ws`（Header: `Authorization: Bearer local-test-token`）
- **健康检查**：`http://localhost:18789/health`

停止和清理：

```powershell
docker stop openclaw-test
docker rm openclaw-test
```

---

## 总结

最稳妥的方案不是改现有 `Dockerfile`，而是：

1. 保留现有生产 Dockerfile 继续服务普通部署
2. 单独维护 `Dockerfile.opensandbox`
3. 单独用 `scripts/build-opensandbox-image.ps1` 产出沙箱镜像
4. 在 OpenSandbox 侧用资源限制、TTL、网络策略和 volume 管理运行环境

这才符合你说的逻辑：是“沙箱运行当前项目”，不是“当前项目去执行沙箱”。