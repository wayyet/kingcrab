# scripts

## 脚本说明

### build-opensandbox-image.ps1（原始脚本，勿动）

从零构建完整的 OpenSandbox 镜像（含所有 apt 包、Node.js、Playwright）。
每次都会重新安装依赖，耗时较长（约 5 分钟以上），保留作为兜底方案。

---

### build-opensandbox-base-image.ps1（基础镜像）

构建包含所有运行时依赖的基础镜像，**依赖变更时才需要重新构建**。

| 参数 | 默认值 | 说明 |
|---|---|---|
| `-Registry` | `ai4c-tcr.tencentcloudcr.com/agentfoundry/king-crab` | 镜像仓库前缀 |
| `-Tag` | `opensandbox-base-<本地时间 YYYYMMddHHmm>` | 镜像 tag |
| `-Platforms` | `linux/amd64` | 目标平台，多平台须配合 `-Push` |
| `-Push` | 不推送（本地 load） | 加上后自动推送，同时打 `opensandbox-base-latest` |
| `-NoPull` | — | 跳过拉取上游基础镜像（离线环境使用） |

**常用命令：**
```powershell
# 构建并加载到本地（之后手动 docker push）
.\build-opensandbox-base-image.ps1

# 构建并直接推送（多平台）
.\build-opensandbox-base-image.ps1 -Platforms linux/amd64,linux/arm64 -Push
```

**注意事项：**
- 构建后会同时打 `opensandbox-base-latest` tag（仅在 `-Push` 模式下）
- 本地 load 模式只支持单平台，多平台必须加 `-Push`
- 首次构建因需下载 Playwright Chromium（约 170 MB）耗时较长（约 5 分钟）

---

### build-opensandbox-app-image.ps1（应用镜像，日常使用）

只做 `dotnet publish` + 文件复制，基于已有基础镜像快速构建，**代码变更后使用此脚本**。

| 参数 | 默认值 | 说明 |
|---|---|---|
| `-Registry` | `ai4c-tcr.tencentcloudcr.com/agentfoundry/king-crab` | 镜像仓库前缀 |
| `-Tag` | `opensandbox-<本地时间 YYYYMMddHHmm>` | 镜像 tag，格式与原镜像一致 |
| `-BaseImage` | — | 完整基础镜像引用，优先于 `-BaseTag` |
| `-BaseTag` | `opensandbox-base-latest` | 基础镜像 tag |
| `-Platforms` | `linux/amd64` | 目标平台 |
| `-Push` | 不推送（本地 load） | 加上后自动推送 |
| `-NoPull` | — | 跳过拉取上游基础镜像 |
| `-Configuration` | `Release` | MSBuild 配置 |

**常用命令：**
```powershell
# 使用最新基础镜像构建并加载到本地
.\build-opensandbox-app-image.ps1

# 锁定指定基础镜像版本
.\build-opensandbox-app-image.ps1 -BaseTag opensandbox-base-202604170633

# 构建并直接推送（多平台）
.\build-opensandbox-app-image.ps1 -Platforms linux/amd64,linux/arm64 -Push
```

**注意事项：**
- 执行前确保基础镜像已推送到镜像仓库，否则拉取会失败
- 本地 load 模式只支持单平台
- 代码未变更时 `dotnet publish` 全部命中缓存，构建极快（<1 分钟）

---

## 典型工作流

```
依赖变更时（apt 包 / Node / Playwright）:
  build-opensandbox-base-image.ps1  →  docker push <base-tag>

日常代码迭代:
  build-opensandbox-app-image.ps1 -BaseTag <base-tag>  →  docker push <app-tag>
```

## 镜像命名格式

| 类型 | 格式 | 示例 |
|---|---|---|
| 基础镜像 | `opensandbox-base-<YYYYMMddHHmm>` | `opensandbox-base-202604171447` |
| 基础镜像（最新） | `opensandbox-base-latest` | — |
| 应用镜像 | `opensandbox-<YYYYMMddHHmm>` | `opensandbox-202604171447` |

时间戳使用**本机本地时间**。
