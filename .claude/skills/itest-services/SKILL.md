---
name: itest-services
description: 一键重启/启动/停止 kingcrab 集成测试相关服务（本地 OpenClaw.Gateway 服务、Kafka 与 Doris 的 docker 容器）。当用户要求重启服务、停止本地 kingcrab 服务、停止 kafka/doris docker、查看服务状态、或为集成测试准备环境时使用。
---

# kingcrab 集成测试服务管理

统一入口脚本：`scripts/itest-services.ps1`（PowerShell 7）。

## 用法

根据用户意图选择 Action 并执行（在仓库根目录运行）：

```powershell
pwsh scripts/itest-services.ps1                          # 一键重启全部（默认 restart）
pwsh scripts/itest-services.ps1 -Action start            # 启动 docker 栈 + 本地 kingcrab
pwsh scripts/itest-services.ps1 -Action stop             # 停止本地 kingcrab + 停止 kafka/doris docker
pwsh scripts/itest-services.ps1 -Action stop-kingcrab    # 仅停止本地 kingcrab 服务
pwsh scripts/itest-services.ps1 -Action stop-docker      # 仅停止 kafka、Doris 的 docker 容器
pwsh scripts/itest-services.ps1 -Action status           # 查看各服务状态
```

加 `-NoKafkaPublish` 可在启动 kingcrab 时不注入 `OpenClaw__TokenUsageKafka__Enabled=true`
（默认注入，便于测试 Token 用量 Kafka→Doris 链路）。

## 管理的服务

| 服务 | 形态 | 地址 / 容器名 |
|---|---|---|
| kingcrab (OpenClaw.Gateway) | 本地 `dotnet run` 进程 | `127.0.0.1:18789`（AuthToken: `kingcrab`） |
| Kafka | docker 容器 | `kafka-doris-kafka`，`localhost:9092` |
| Doris FE | docker 容器 | `kafka-doris-fe`，HTTP `8030` / MySQL `9030` |
| Doris BE | docker 容器 | `kafka-doris-be`，HTTP `8040` |

docker compose 文件：`C:\Users\wayye\Documents\ai4c_Projects\setting_Install\kafka-doris-deploy\docker-compose.yml`

## 脚本启动时自动做的事

1. `docker compose up -d` 并等待就绪：Kafka broker 可响应、FE `http://localhost:8030/api/bootstrap`
   返回 200、BE `http://localhost:8040/api/health` 返回 200。
2. 检查 Routine Load `token_metrics.load_session_token_events`，若处于 PAUSED 自动 RESUME。
3. 后台启动 `dotnet run --project src/OpenClaw.Gateway`，等待端口 18789 监听（首次含编译，最长 180s）。
   - 日志：`logs/gateway.out.log` / `logs/gateway.err.log`，PID 记录在 `logs/gateway.pid`。
4. 停止 kingcrab 时按 PID 文件 + 进程名 `OpenClaw.Gateway` + 端口 18789 占用者三路兜底查杀。

## 启动后的验证（集成测试前建议执行）

```powershell
# Gateway 健康（应返回响应而非连接拒绝）
Invoke-WebRequest http://127.0.0.1:18789/ -UseBasicParsing -SkipHttpErrorCheck | Select-Object StatusCode

# Kafka topic 存在
docker exec kafka-doris-kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --list

# Doris 明细表可查（注意 -h/-P 后必须有空格，-h127.0.0.1 会被该客户端解析成主机 "127"）
docker exec kafka-doris-fe mysql -h 127.0.0.1 -P 9030 -uroot -e "SELECT COUNT(*) FROM token_metrics.session_token_events;"
```

## 故障排查

- **docker compose 失败**：先确认 Docker Desktop 在运行。
- **Routine Load 不存在**：执行 `scripts/token-usage/doris-token-metrics.local.sql`
  （`docker exec -i kafka-doris-fe mysql -h 127.0.0.1 -P 9030 -uroot < scripts/token-usage/doris-token-metrics.local.sql`）。
- **Kafka topic 缺失**：参考 `scripts/token-usage/create-kafka-topic.sh`，本地单节点用
  `--partitions 3 --replication-factor 1`，不要带 `min.insync.replicas=2`。
- **Gateway 启动超时**：看 `logs/gateway.err.log` 末尾；常见原因是端口被残留进程占用
  （再跑一次 `-Action stop-kingcrab`）或编译错误。
- **Doris FE 启动慢**：冷启动可能超过 1 分钟，BE 注册需 FE 先就绪，脚本已按顺序等待。
