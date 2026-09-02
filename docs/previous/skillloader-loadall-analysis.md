# 深入解析 SkillLoader.LoadAll：技能发现、优先级覆盖与条件筛选

## 1. 背景

在当前代码中，`src/OpenClaw.Core/Skills/SkillLoader.cs` 里的 `LoadAll` 是技能系统的总入口。它负责：

- 从多个来源扫描技能目录
- 解析每个 `SKILL.md`
- 处理同名技能的优先级覆盖
- 根据配置和运行环境筛选最终可用技能

它返回的是最终可用的 `List<SkillDefinition>`。

---

## 2. 方法签名

`LoadAll` 的定义如下：
public static List<SkillDefinition> LoadAll( SkillsConfig config, string? workspacePath, ILogger logger, IReadOnlyList<string>? pluginSkillDirs = null)

参数含义：

- `config`：技能系统总配置
- `workspacePath`：当前工作区路径
- `logger`：日志记录器
- `pluginSkillDirs`：插件技能目录列表，可空

---

## 3. 整体职责概览

`LoadAll` 的逻辑可以概括为两阶段：

1. **收集阶段**：从多个来源扫描技能，形成 `allSkills`
2. **过滤阶段**：对 `allSkills` 做规则筛选，产出 `eligible`

---

## 4. 主流程图
flowchart TD 

A["调用 LoadAll"] --> B{"config.Enabled?"} B -- "false" --> C["记录日志并返回空列表"] B -- "true" --> D["创建 allSkills 字典"]
D --> E["扫描 ExtraDirs"]
E --> F["扫描 Bundled"]
F --> G["扫描 Managed"]
G --> H["扫描 Plugin"]
H --> I["扫描 Workspace"]

I --> J["遍历 allSkills"]
J --> K{"Bundled 且不在 AllowBundled?"}
K -- "是" --> K1["跳过"]
K -- "否" --> L{"被 Entries 显式禁用?"}
L -- "是" --> L1["跳过"]
L -- "否" --> M{"Metadata.Always?"}
M -- "true" --> N["加入 eligible"]
M -- "false" --> O["调用 CheckRequirements"]

O --> P{"要求满足?"}
P -- "是" --> N
P -- "否" --> P1["跳过"]

N --> Q{"还有下一个 skill?"}
K1 --> Q
L1 --> Q
P1 --> Q
Q -- "是" --> J
Q -- "否" --> R["返回 eligible"]

---

## 5. 第一步：总开关判断

`LoadAll` 一开始会判断：

- `config.Enabled`

如果为 `false`，则：

- 记录 `"Skills system is disabled"`
- 直接返回 `[]`

这说明技能系统有一个**最高级别总开关**。

---

## 6. 第二步：建立聚合字典

代码使用：

- `Dictionary<string, SkillDefinition>`

并且 key 是技能名，比较方式是：

- `StringComparer.OrdinalIgnoreCase`

这带来三点效果：

- 同名技能只保留一个
- 大小写不敏感
- 后扫描到的同名技能会覆盖前面的版本

因此，**优先级是通过扫描顺序实现的**。

---

## 7. 技能来源与扫描顺序

`LoadAll` 会按以下顺序扫描：

1. `ExtraDirs`
2. `Bundled`
3. `Managed`
4. `Plugin`
5. `Workspace`

后扫描的优先级更高。

### 实际优先级

**Workspace > Plugin > Managed > Bundled > Extra**

---

## 8. 来源说明

### 8.1 `ExtraDirs`

来自：

- `config.Load.ExtraDirs`

特点：

- 最先扫描
- 优先级最低

### 8.2 `Bundled`

路径：

- `AppContext.BaseDirectory/skills`

特点：

- 应用自带技能

### 8.3 `Managed`

逻辑路径构造为：

```c#
Path.Combine( AppContext.BaseDirectory, "skills", skillFolder )
```

来自：

- 显式配置的 Managed 文件夹
- 一般用于托管开发技能

特点：

- 可由主程序配置
- 普遍存在于单机/托管环境

### 8.4 `Plugin`

路径：

- `AppDomain.CurrentDomain.BaseDirectory`

特点：

- 插件技能
- 可按需加载

### 8.5 `Workspace`

路径：

- `workspacePath`

特点：

- 当前工作区
- 最高优先级
- 可被热更改

---

## 9. 条件与筛选

对同一个技能：

- `SKILL.md` 决定基本信息
- `metadata` 决定是否可用以及依赖检查

核心在于 `metadata` 的 `Conditions` 字段：

- 支持多条件或
- 具备优先级的与

示例：

```yaml
Conditions:
    - If:
        - Var: Lv
          Op: Gte
          Val: 10
      Then:
        - Skill: FireBall
          Param:
            - Name: dmg
              Val: 100
```

含义是：

- 当 `Lv >= 10` 时，才会启用 `FireBall` 技能且 `dmg` 参数为 `100`

---

## 10. 处理流程细节

### 1. 收集阶段：

- 初始化 `allSkills`：
```c#
var allSkills = new Dictionary<string, SkillDefinition>( StringComparer.OrdinalIgnoreCase )
```

- 依次扫描：
  - `ExtraDirs`
  - `Bundled`
  - `Managed`
  - `Plugin`
  - `Workspace`

  每个目录的处理都大同小异：

  - 记录扫描日志
  - 构造目录技能的逻辑路径
  - 检查 `SKILL.md` 是否存在
  - 解析并加入 `allSkills`

  例如，扫描 `Bundled` 的代码：

```c#
foreach ( var skillFolder in _config.Load.Bundled )
{
    var dir = Path.Combine( AppContext.BaseDirectory, "skills", skillFolder )
    if ( !Directory.Exists( dir ) )
        continue

    logger.Debug( $"扫描 Bundled 目录：{dir}" )
    ScanDirectory( dir, allSkills, logger )
}
```

### 2. 过滤阶段：

- 记录可用技能至 `eligible`：
```c#
var eligible = new List<SkillDefinition>()
```

- 遍历 `allSkills`：

```c#
foreach ( var kv in allSkills )
{
    var skill = kv.Value
    ...
}
```

- 筛选条件：

  - `Bundled` 且不在 `AllowBundled` 列表中
  - 被 `Entries` 显式禁用
  - `Metadata.Always` 为 `false`
  - 不满足要求

  例如，检查 `Bundled` 的代码：

```c#
if ( skill.Bundled && !config.Load.AllowBundled.Contains( skill.Name, StringComparer.OrdinalIgnoreCase ) )
    continue
```

---

## 10. `ScanDirectory` 的职责

`LoadAll` 不直接解析文件，而是调用 `ScanDirectory`。

`ScanDirectory` 会做两件事：

1. 检查 `rootDir/SKILL.md`
2. 遍历 `rootDir` 的直接子目录，检查每个子目录中的 `SKILL.md`

解析成功后执行：

- `results[skill.Name] = skill`

这就是覆盖逻辑发生的位置。

### 特别说明

覆盖不是在过滤阶段发生，而是在**扫描写入字典时**发生。

---

## 11. `ScanDirectory -> ParseSkillContent` 流程图
```mermaid
graph TD
    A[ScanDirectory] --> B{"SKILL.md 存在?"}
    B -- "是" --> C["解析 SKILL.md"]
    C --> D["加入 allSkills"]
    D --> E["完成"]
    B -- "否" --> F["记录并忽略"]

````````

---

## 12. `ParseSkillContent` 的作用

`ParseSkillContent` 负责把 `SKILL.md` 转成 `SkillDefinition`。

### 解析规则

它要求文件：

- 以 `---` 开头
- 存在 frontmatter 结束标记

否则直接返回 `null`。

### 解析的字段

它会从 frontmatter 中提取：

- `name`
- `description`
- `metadata`
- `user-invocable`
- `disable-model-invocation`
- `command-dispatch`
- `command-tool`
- `command-arg-mode`
- `homepage`

其中 `name` 是必须的。

### 额外处理

正文部分会执行：

- 把 `{baseDir}` 替换成当前技能目录路径

### 最终产物

返回一个完整的 `SkillDefinition`。

---

## 13. 过滤阶段的三层规则

收集完所有技能后，`LoadAll` 会遍历 `allSkills` 并做筛选。

### 第一层：`AllowBundled`

仅针对 `Bundled` 来源生效。

如果：

- 当前技能是 `Bundled`
- 且 `config.AllowBundled` 非空
- 且技能名不在白名单里

则跳过。

### 第二层：单技能显式禁用

配置键取值方式：

- `skill.Metadata.SkillKey ?? skill.Name`

如果 `config.Entries[configKey].Enabled == false`，则跳过。

### 第三层：运行条件检查

如果：

- `skill.Metadata.Always == true`

则直接通过，不检查 requirements。

否则调用：

- `CheckRequirements(skill, config, logger)`

---

## 14. `CheckRequirements` 检查哪些条件

### 14.1 OS 限制

如果 `meta.Os` 有值：

- 只允许 `darwin` / `linux` / `win32`

不匹配则返回 `false`。

### 14.2 `RequireBins`

要求所有列出的命令都能在 `PATH` 中找到。

### 14.3 `RequireAnyBins`

只要求其中至少一个存在。

### 14.4 `RequireEnv`

每个环境变量满足以下任一条件即可：

- 系统环境变量存在
- `entry.Env` 中存在
- 若该变量等于 `PrimaryEnv`，则 `entry.ApiKey` 有值

---

## 15. `CheckRequirements` 流程图

```mermaid
flowchart TD
    A[CheckRequirements] --> B{"meta.Os 有值?"}
    B -- "是" --> C{"运行平台匹配?"}
    C -- "否" --> H[返回 false]
    C -- "是" --> D["检查 RequireBins"]
    D --> E{"RequireBins 满足?"}
    E -- "否" --> F["记录并返回 false"]
    E -- "是" --> G["检查 RequireAnyBins"]
    G --> I{"RequireAnyBins 满足?"}
    I -- "否" --> J["记录并返回 false"]
    I -- "是" --> K["检查 RequireEnv"]
    K --> L{"RequireEnv满足?"}
    L -- "否" --> M["记录并返回 false"]
    L -- "是" --> N[返回 true]
    B -- "否" --> O[直接返回 true]

````````

---

## 16. 一个关键细节：`Always` 的真实作用

如果技能元数据里：

- `always: true`

那么它只会绕过：

- `CheckRequirements`

它**不会绕过**：

- 总开关 `config.Enabled`
- `AllowBundled`
- `Entries` 显式禁用

因此 `always` 不是“无条件强制启用”，而是“跳过运行环境要求检查”。

---

## 17. 结合当前工作区中的 `ncrew-rules`

当前活动文件是：

- `src/OpenClaw.Gateway/skills/ncrew-rules/SKILL.md`

它的 frontmatter 中包含：

- `name: ncrew-rules`
- `metadata.openclaw.always: true`

这意味着：

1. 它会被解析为名为 `ncrew-rules` 的技能
2. 只要扫描到并且没有被显式禁用
3. 它在过滤阶段会跳过 `CheckRequirements`

因此它很容易进入最终的 `eligible` 列表。

---

## 18. 关于 `managed` 目录的最终结论

`managed` 目录是用户 Home 下的技能目录，其逻辑路径为：

- Windows：`%USERPROFILE%\.openclaw\skills`
- Linux/macOS：`~/.openclaw/skills`

它的语义是：

- 用于用户级技能管理
- 可跨工作区复用
- 比 `Bundled` 更高优先级
- 但仍低于 `Plugin` 与 `Workspace`

---

## 19. 当前实现的优点

### 19.1 结构清晰

发现技能与筛选技能分离，职责明确。

### 19.2 覆盖机制简单可靠

通过扫描顺序 + 字典赋值直接实现优先级。

### 19.3 容错性好

单个技能损坏不会影响整体。

### 19.4 支持多来源

覆盖了：

- extra
- bundled
- managed
- plugin
- workspace

### 19.5 环境感知能力强

可根据：

- 操作系统
- PATH 命令
- 环境变量
- 配置注入

动态决定技能可用性。

---

## 20. 当前实现的注意点

### 20.1 注释未完整体现优先级

注释中没有写出 `Plugin` 层。

### 20.2 `RequireConfig` 尚未生效

虽然 `SkillMetadata` 里有该字段，`ParseMetadata` 也会读取，但 `CheckRequirements` 并未真正检查它。

### 20.3 同名覆盖是整体替换

不是字段级合并，而是整个 `SkillDefinition` 被替换。

---

## 21. 总结

`LoadAll` 的完整逻辑可以总结为：

1. 检查技能系统总开关
2. 按固定顺序扫描多个来源目录
3. 通过同名覆盖形成最终技能集合
4. 对最终集合进行三层过滤：
   - bundled 白名单
   - 单技能显式禁用
   - requirements 检查
5. 返回最终可用技能列表

一句话概括：

> `LoadAll` 是技能系统的“装配与筛选中枢”，负责把分散在不同来源的 `SKILL.md` 统一收集、按优先级覆盖，并按配置和环境筛成最终可用技能。

---

## 22. 附：更简洁的时序图
equenceDiagram 

participant Caller as 调用方 
participant LoadAll as SkillLoader.LoadAll 
participant Scan as ScanDirectory 
participant Parse as ParseSkillContent
participant Check as CheckRequirements

Caller->>LoadAll: 调用 LoadAll(config, workspacePath, logger, pluginSkillDirs)

alt config.Enabled == false
    LoadAll-->>Caller: 返回空列表
else 已启用
    LoadAll->>Scan: 扫描 ExtraDirs / Bundled / Managed / Plugin / Workspace
    Scan->>Parse: 解析 SKILL.md
    Parse-->>Scan: SkillDefinition 或 null
    Scan-->>LoadAll: 写入 allSkills

    loop 遍历 allSkills
        alt Bundled 不在 AllowBundled
            LoadAll-->>LoadAll: 跳过
        else 被 config.Entries 显式禁用
            LoadAll-->>LoadAll: 跳过
        else Metadata.Always == true
            LoadAll-->>LoadAll: 加入 eligible
        else
            LoadAll->>Check: CheckRequirements(skill, config, logger)
            alt 条件满足
                Check-->>LoadAll: true
                LoadAll-->>LoadAll: 加入 eligible
            else 条件不满足
                Check-->>LoadAll: false
                LoadAll-->>LoadAll: 跳过
            end
        end
    end

    LoadAll-->>Caller: 返回 eligible 列表
end
