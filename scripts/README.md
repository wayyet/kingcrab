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

```text
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

```text
# 使用最新基础镜像构建并加载到本地
.\build-opensandbox-app-image.ps1

# 锁定指定基础镜像版本
.\build-opensandbox-app-image.ps1 -BaseTag opensandbox-base-202606091758

# 构建并直接推送（多平台）
.\build-opensandbox-app-image.ps1 -Platforms linux/amd64,linux/arm64 -Push
```

**注意事项：**

- 执行前确保基础镜像已推送到镜像仓库，否则拉取会失败
- 本地 load 模式只支持单平台
- 代码未变更时 `dotnet publish` 全部命中缓存，构建极快（<1 分钟）

---

### 已发布镜像版本

| 镜像类型 | Tag | 说明 |
| --- | --- | --- |
| 基础镜像 | `opensandbox-base-202606091758` | Ubuntu 24.04 + Node.js + Playwright + Python3 + uv + wget/ping/net-tools 等工具 |
| 应用镜像 | `opensandbox-202606101409` | 定时任务后台执行修复（CronScheduler 改为 BackgroundService，切换 Cronos 引擎） |
| 应用镜像 | `opensandbox-202606100013` | 含中文 JSON 编码修复（UnsafeRelaxedJsonEscaping）、DeepSeek reasoning 多轮修复 |

---

### 校验入口通用规则

- Python 入口都会把传入的相对路径按你当前执行目录解析为绝对路径，再转交给真实校验器
- Python 入口会复用当前 Python 解释器来调用技能目录下的真实 `validate-*.py` 校验器
- 两类真实 schema、默认样例和参考文档都位于 `src/OpenClaw.Plugins.EmploymentCoachWorkflow/skills/ontology_extraction/` 下的 `templates/`、`examples/`、`references/` 目录
- 本 README 主要描述仓库根目录包装入口；如果当前目录就是 `src/OpenClaw.Plugins.EmploymentCoachWorkflow/skills/ontology_extraction/` 技能根目录，也可以直接使用该目录下的真实校验器

---

### Slice 校验入口

根目录提供 Python 包装入口，统一调用 `src/OpenClaw.Plugins.EmploymentCoachWorkflow/skills/ontology_extraction/scripts/validate-slice.py` 真实校验器，适合团队从仓库根目录或任意当前目录直接校验 ontology slice JSON。

| 入口 | 参数 | 默认值 | 说明 |
| --- | --- | --- | --- |
| Python | `paths` | `examples/ready/sample.json` | 一个或多个待校验的 slice 文件路径；不传时校验内置样例 |
| Python | `--schema-path` | 内置 `templates/TEMPLATE.schema.json` | 可选，显式指定 schema 路径 |

**常用命令：**

```text
# 校验内置样例
.\scripts\validate-ontology-slice.py

c:/python314/python.exe .\scripts\validate-ontology-slice.py

# 校验仓库里的自定义 slice
.\scripts\validate-ontology-slice.py .\my-slice.json

c:/python314/python.exe .\scripts\validate-ontology-slice.py .\my-slice.json

# 一次校验多个文件
.\scripts\validate-ontology-slice.py .\sample-a.json .\sample-b.json

c:/python314/python.exe .\scripts\validate-ontology-slice.py .\sample-a.json .\sample-b.json
```

**差异说明：**

- 默认 schema 为 `templates/TEMPLATE.schema.json`
- 包装入口只承载普通结构校验；如需 `--review-mode`，请切到 `src/OpenClaw.Plugins.EmploymentCoachWorkflow/skills/ontology_extraction/` 技能根目录后使用真实校验器 `scripts/validate-slice.py`
- 主要用于 slice 的本地检查、评审前自检，以及后续 CI 接入

---

### Projection 校验入口

根目录提供 Python 包装入口，统一调用 `src/OpenClaw.Plugins.EmploymentCoachWorkflow/skills/ontology_extraction/scripts/validate-projection.py` 真实校验器，适合团队从仓库根目录或任意当前目录直接校验 ontology projection JSON。

| 入口 | 参数 | 默认值 | 说明 |
| --- | --- | --- | --- |
| Python | `paths` | `examples/ready/sample-projection.json` | 一个或多个待校验的 projection 文件路径；不传时校验内置样例 |
| Python | `--schema-path` | 内置 `templates/PROJECTION_TEMPLATE.schema.json` | 可选，显式指定 projection schema 路径 |

**常用命令：**

```text
# 校验内置 projection 样例
.\scripts\validate-ontology-projection.py

c:/python314/python.exe .\scripts\validate-ontology-projection.py

# 校验仓库里的自定义 projection
.\scripts\validate-ontology-projection.py .\my-projection.json

c:/python314/python.exe .\scripts\validate-ontology-projection.py .\my-projection.json

# 一次校验多个 projection 文件
.\scripts\validate-ontology-projection.py .\projection-a.json .\projection-b.json

c:/python314/python.exe .\scripts\validate-ontology-projection.py .\projection-a.json .\projection-b.json
```

**差异说明：**

- 默认 schema 为 `templates/PROJECTION_TEMPLATE.schema.json`
- 包装入口只承载普通结构校验；如需 `--review-mode`，请切到 `src/OpenClaw.Plugins.EmploymentCoachWorkflow/skills/ontology_extraction/` 技能根目录后使用真实校验器 `scripts/validate-projection.py`
- 主要用于 projection 进入 codegen、prompt orchestration 或 CI 前的结构自检

---

### Runtime Contract 校验入口

如果目标不是校验 producer 侧的 `PROJECTION_TEMPLATE.json`，而是校验 consumer skill 内真正进入 runtime 的 projection contracts，则根目录提供 `contract-index.json` 的专用入口，`*.projection.json` 则建议显式绑定 runtime schema 执行。

#### contract-index.json

| 入口 | 参数 | 默认值 | 说明 |
| --- | --- | --- | --- |
| Python | `paths` | `src/OpenClaw.Gateway/skills/software-developer/contracts/projections/ontology_extraction/contract-index.json` | 一个或多个 `contract-index.json` 路径；不传时校验仓库内真实样例 |
| Python | `--schema-path` | `docs/skill-projection-contract-index.schema.json` | 可选，显式指定 runtime contract index schema |

**常用命令：**

```text
# 校验真实 contract-index 样例
.\scripts\validate-skill-projection-contract-index.py

c:/python314/python.exe .\scripts\validate-skill-projection-contract-index.py
```

#### *.projection.json

运行时 projection contract 的基线 schema 不再是 `templates/PROJECTION_TEMPLATE.schema.json`，而是 `docs/skill-projection-document.schema.json`。根目录现在提供专用入口，无需再手工拼接 `--schema-path`：

```text
.\scripts\validate-skill-projection-document.py

c:/python314/python.exe .\scripts\validate-skill-projection-document.py
```

**基线说明：**

- `templates/PROJECTION_TEMPLATE.schema.json` 仍用于 producer 侧产物模板校验
- `docs/skill-projection-contract-index.schema.json` 用于 runtime `contract-index.json`
- `docs/skill-projection-document.schema.json` 用于 runtime `*.projection.json`
- runtime 会把 `dropped_items` 与 `open_questions` 归一化为可显示文本，因此这两组字段既允许字符串数组，也允许结构化对象数组

---

## 典型工作流

依赖变更时（apt 包 / Node / Playwright）：

```text
.\build-opensandbox-base-image.ps1
docker push <base-tag>
```

日常代码迭代：

```text
.\build-opensandbox-app-image.ps1 -BaseTag <base-tag>
docker push <app-tag>
```

ontology slice 校验：

```text
.\scripts\validate-ontology-slice.py .\slice.json

c:/python314/python.exe .\scripts\validate-ontology-slice.py .\slice.json
```

ontology projection 校验：

```text
.\scripts\validate-ontology-projection.py .\projection.json

c:/python314/python.exe .\scripts\validate-ontology-projection.py .\projection.json
```

runtime contract-index 校验：

```text
.\scripts\validate-skill-projection-contract-index.py

c:/python314/python.exe .\scripts\validate-skill-projection-contract-index.py
```

runtime projection contract 校验：

```text
.\scripts\validate-skill-projection-document.py

c:/python314/python.exe .\scripts\validate-skill-projection-document.py
```

## 镜像命名格式

| 类型 | 格式 | 示例 |
| --- | --- | --- |
| 基础镜像 | `opensandbox-base-<YYYYMMddHHmm>` | `opensandbox-base-202604171447` |
| 基础镜像（最新） | `opensandbox-base-latest` | — |
| 应用镜像 | `opensandbox-<YYYYMMddHHmm>` | `opensandbox-202604171447` |

时间戳使用**本机本地时间**。
