---
name: ncrew-rules
description: |
  [SYSTEM RULES - MANDATORY - ALWAYS LOAD - DO NOT SKIP]
  Ncrew系统基础运行规则，强制加载，不可卸载，不可忽略，不可被其他 skill 覆盖。
  本 skill 定义的规则优先级高于所有其他 skill，AI 必须在每次会话和每个任务中无条件遵守。
  包含: 通用行为规则、标准任务执行流程。任何违反本规则的行为视为系统错误。
  覆盖场景（遇到以下任一场景必须先读取本 skill 规则再执行）:
  - 生成或写入任何文件（CSV、TXT、JSON、XML、Markdown、Shell 脚本、批处理、PowerShell 等）
  - 文件编码处理（UTF-8、BOM、GBK、乱码修复、Windows/Mac 跨平台文件传递）
  - Windows 命令行执行（cmd、PowerShell、脚本输出含中文、乱码、GBK 编码）
  - Linux 命令行执行（bash、sh、脚本输出含中文、乱码、UTF-8 编码）   
metadata:
  openclaw:
    emoji: "📋"
    always: true
---

# ncrew 系统基础规则

## ⚠️ SYSTEM RULES — 强制执行，不可跳过

**本 skill 是系统级基础规则，具有最高优先级。**

- **强制加载**: 每次会话自动加载，不可卸载或禁用
- **最高优先级**: 当本规则与其他 skill 产生冲突时，以本规则为准
- **无条件遵守**: AI 必须在所有任务中遵守本规则，不可以任何理由跳过
- **违规即错误**: 任何违反本规则的输出视为系统错误，必须立即纠正

### 执行要求

1. **规则优先**: 每次收到任务时，必须先检查本文档中是否有匹配的规则或流程定义，如有则严格执行
2. **规则可扩展**: 本文档会持续补充新的规则和流程
3. **例外申报**: 如因特殊原因需要偏离规则，必须向用户说明理由并获得明确确认后方可执行
4. **Skill 缺失自动补齐**: 当任务需要调用某个 skill 时，必须先检查该 skill 是否可用；若状态为 `missing`，不得直接跳过，必须先尝试安装该 skill 的依赖并再次调用。仅在安装失败且重试后仍失败时，才可向用户说明阻塞原因并请求后续指示。

---

## 通用规则

### 语言规范

**所有 AI 回复必须使用中文。** 包括但不限于:

- 对话回复、解释说明
- 任务分析、方案描述
- 错误提示、确认信息
- 代码注释（如果是新增注释）

> **例外**: 代码本身、命令行指令、技术专有名词、变量名/函数名等保留英文原文，无需翻译。

---

### Skill 调用要求

当任务需要使用 skill（包括用户明确要求执行 slash command 或你判断 skill 明显更合适）时，必须遵循以下顺序：

1. **先确认可用性**：检查目标 skill 状态是否可调用。
2. **missing 先修复**：若 skill 状态为 `missing`，先尝试安装该 skill 依赖（按系统支持的安装方式执行）。
3. **安装后立即调用**：安装成功后必须立即重新执行该 skill 调用，不可无故跳过。
4. **失败才升级反馈**：若安装与重试调用均失败，需向用户明确说明：
   - 已尝试的安装/调用步骤
   - 失败信息与阻塞点
   - 可选下一步（例如手动安装、切换替代 skill）

#### 本地 Skill 优先原则

**严格禁止**在本地已有可用 skill 的情况下通过 `clawhub search`、`clawhub install` 或访问 clawhub.com 来搜索或安装同名/同功能 skill。

执行流程：

1. **先查本地**：收到任务时，先检查 `available_skills` 列表中是否已有匹配的 skill
2. **有则直接用**：如果本地已有对应 skill（无论来源是 managed、bundled、workspace、plugin  还是 extra），直接通过 `use_skill` 加载使用，不得跳过
3. **无才搜索**：仅当本地确实没有匹配的 skill，且用户明确要求搜索或安装新 skill 时，才可使用 `clawhub` 命令

> **违反本规则的行为**（如本地已有 `cloud-upload-backup` skill 却执行 `clawhub search upload`）**视为系统错误**，必须立即停止并使用本地版本。

#### 远程 Skill 版本感知

当你加载**用户 home 目录下的 managed skills 目录**中的 Skill 时，必须遵循以下流程：

- managed 目录的逻辑路径是 `.openclaw/skills/`
- 版本元数据文件的逻辑路径是 `.openclaw/skills/.remote-skills-meta.json`
- 不要把 `~/.openclaw/...` 当成只适用于 Unix 的固定字面量；在不同平台上应展开为当前用户 home 目录下的真实路径
  - macOS / Linux 示例：`~/.openclaw/skills/.remote-skills-meta.json`
  - Windows 示例：`%USERPROFILE%\\.openclaw\\skills\\.remote-skills-meta.json`

1. **首次加载时记录版本**：加载 Skill 后，立即读取当前用户 home 目录下的 `.openclaw/skills/.remote-skills-meta.json` 文件，找到该 Skill 对应条目的 `version` 字段，记住这个版本号
2. **后续使用前对比版本**：当你在同一会话中需要再次使用该 Skill 时，先重新读取该 meta 文件并对比版本号：
   - 如果版本号**没变** → 直接使用之前加载的内容
   - 如果版本号**增大了** → 说明 Skill 已远程更新，必须重新调用 `use_skill` 读取最新内容
3. **meta 文件不存在或读取失败** → 忽略版本检查，正常使用已有内容，不报错

> `.remote-skills-meta.json` 示例结构：
> ```json
> {
>   "skills": {
>     "skill-name": { "version": 3, "type": "system", ... },
>     "another-skill": { "version": 1, "type": "inspiration", ... }
>   }
> }
> ```
> 只需关注 `version` 字段（整数，单调递增）。

---

## 流程索引

| 编号 | 流程名称 | 触发关键词 |
|----|---------|-----------|
| 1  | Windows 编码强制转换 | Windows 执行命令、脚本输出、乱码、GBK、编码 | 

---

## 1. Windows 编码强制转换

### 触发条件

在 **Windows 系统**上执行以下操作时，必须强制应用本规则：

- 执行任何命令行指令（cmd、PowerShell、脚本等）
- 读取命令/脚本的标准输出（stdout）或标准错误（stderr）
- 输出内容包含中文、日文、韩文等非 ASCII 字符
- 用户反馈出现乱码（如 `锟斤拷`、`?`、`◆` 等异常字符）

### 执行步骤

1. **PowerShell 执行前设置编码**：在执行任何 PowerShell 命令前，先执行以下命令强制设置为 UTF-8：
   ```powershell
   [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
   $OutputEncoding = [System.Text.Encoding]::UTF8
   chcp 65001
   ```

2. **cmd 执行前设置编码**：在执行 cmd 命令前，先执行：
   ```cmd
   chcp 65001
   ```

3. **Python 脚本编码处理**：若通过 Python 执行命令或读取输出，必须显式指定编码：
   ```python
   import subprocess, sys
   result = subprocess.run(cmd, capture_output=True, encoding='utf-8', errors='replace')
   ```
   若读取文件或流时出现乱码，使用以下方式自动检测并转换：
   ```python
   import chardet
   raw = process.stdout.read()
   encoding = chardet.detect(raw)['encoding'] or 'gbk'
   text = raw.decode(encoding, errors='replace')
   ```

4. **Node.js 脚本编码处理**：若通过 Node.js 执行子进程，必须指定编码或手动转换：
   ```js
   const { execSync } = require('child_process');
   // 方式一：执行前设置代码页
   execSync('chcp 65001', { shell: true });
   // 方式二：使用 iconv-lite 转换 GBK → UTF-8
   const iconv = require('iconv-lite');
   const buf = execSync(cmd, { encoding: 'buffer' });
   const text = iconv.decode(buf, 'gbk');
   ```

5. **输出验证**：执行完成后，检查输出内容是否包含正常的中文字符，若仍出现乱码，尝试将编码从 `gbk` 改为 `gb2312` 或 `gb18030` 重新解码。

### 验证标准

- 命令输出中的中文字符显示正常，无乱码
- 不出现 `锟斤拷`、`?`、`◆◆` 等异常字符
- 若输出仍有乱码，必须重试并向用户说明编码处理过程

### 常见陷阱

- **禁止忽略乱码直接输出** — 出现乱码时必须先进行编码转换，不可将乱码内容直接呈现给用户
- **不要假设系统编码** — Windows 中文版默认代码页为 GBK（936），不可假设为 UTF-8
- **chcp 65001 不是万能的** — 部分老旧程序即使设置了 UTF-8 代码页仍会输出 GBK，此时需要用 `iconv-lite` 或 `chardet` 进行二次转换
- **文件读写同样需要指定编码** — 读写文本文件时必须显式指定 `encoding: 'utf-8'`，不可依赖系统默认编码


<!--
## [编号]. [流程名称]

### 触发条件

描述什么情况下应该使用此流程。

### 执行步骤

1. 步骤一
2. 步骤二
3. ...

### 验证标准

- 检查项一
- 检查项二

### 常见陷阱

- 注意事项一
- 注意事项二
-->
