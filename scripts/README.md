# scripts

## 脚本说明

### build-opensandbox-image.ps1（原始脚本，勿动）

从零构建完整的 OpenSandbox 镜像（含所有 apt 包、Node.js、Playwright）。
每次都会重新安装依赖，耗时较长（约 5 分钟以上），保留作为兜底方案。

---

### build-opensandbox-base-image.ps1（基础镜像）

构建包含所有运行时依赖的基础镜像，**依赖变更时才需要重新构建**。

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
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
| --- | --- | --- |
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

### validate-ontology-slice.ps1（ontology slice 校验入口）

统一调用 `src/OpenClaw.Gateway/skills/ncrew-ontology/scripts/validate-slice.ps1` 的根目录包装脚本，适合团队从仓库根目录或任意当前目录直接校验 ontology slice JSON。

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `Paths` | `examples/ready/sample.json` | 一个或多个待校验的 slice 文件路径；不传时校验内置样例 |
| `-SchemaPath` | 内置 `templates/TEMPLATE.schema.json` | 可选，显式指定 schema 路径 |

**常用命令：**

```powershell
# 校验内置样例
.\scripts\validate-ontology-slice.ps1

# 校验仓库里的自定义 slice
.\scripts\validate-ontology-slice.ps1 .\my-slice.json

# 一次校验多个文件
.\scripts\validate-ontology-slice.ps1 .\sample-a.json .\sample-b.json
```

**注意事项：**

- 该脚本会把传入的相对路径按你当前执行目录解析为绝对路径，再转交给真实校验器
- 真实 schema、默认样例和评审文档位于 `src/OpenClaw.Gateway/skills/ncrew-ontology/` 下的 `templates/`、`examples/`、`references/` 目录
- 适合在本地检查、评审前自检，后续也可以直接挂到 CI

---

### validate-ontology-projection.ps1（ontology projection 校验入口）

统一调用 `src/OpenClaw.Gateway/skills/ncrew-ontology/scripts/validate-projection.ps1` 的根目录包装脚本，适合团队从仓库根目录或任意当前目录直接校验 ontology projection JSON。

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `Paths` | `examples/ready/sample-projection.json` | 一个或多个待校验的 projection 文件路径；不传时校验内置样例 |
| `-SchemaPath` | 内置 `templates/PROJECTION_TEMPLATE.schema.json` | 可选，显式指定 projection schema 路径 |

**常用命令：**

```powershell
# 校验内置 projection 样例
.\scripts\validate-ontology-projection.ps1

# 校验仓库里的自定义 projection
.\scripts\validate-ontology-projection.ps1 .\my-projection.json

# 一次校验多个 projection 文件
.\scripts\validate-ontology-projection.ps1 .\projection-a.json .\projection-b.json
```

**注意事项：**

- 该脚本会把传入的相对路径按你当前执行目录解析为绝对路径，再转交给真实校验器
- 真实 schema、默认样例和 review 参考位于 `src/OpenClaw.Gateway/skills/ncrew-ontology/` 下的 `templates/`、`examples/`、`references/` 目录
- 适合在 projection 进入 codegen、prompt orchestration 或 CI 前先做结构自检

---

## 典型工作流

```text
依赖变更时（apt 包 / Node / Playwright）:
  build-opensandbox-base-image.ps1  →  docker push <base-tag>

日常代码迭代:
  build-opensandbox-app-image.ps1 -BaseTag <base-tag>  →  docker push <app-tag>

ontology slice 校验:
  scripts\validate-ontology-slice.ps1 [slice.json ...]

ontology projection 校验:
  scripts\validate-ontology-projection.ps1 [projection.json ...]
```

## 镜像命名格式

| 类型 | 格式 | 示例 |
| --- | --- | --- |
| 基础镜像 | `opensandbox-base-<YYYYMMddHHmm>` | `opensandbox-base-202604171447` |
| 基础镜像（最新） | `opensandbox-base-latest` | — |
| 应用镜像 | `opensandbox-<YYYYMMddHHmm>` | `opensandbox-202604171447` |

时间戳使用**本机本地时间**。
