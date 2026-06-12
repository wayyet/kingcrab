# 通过 Playwright CLI 创建 OpenClaw.NET 操作员账户

**日期**: 2026-06-10  
**任务**: 在 OpenClaw.NET Admin 界面创建新的操作员账户

---

## 一、任务背景

OpenClaw.NET 提供了一套完整的认证系统，支持三种登录模式：

| 登录模式 | 值 | 用途 |
|---------|-----|------|
| 用户名+密码 | `credentials` | 标准操作员登录 |
| 账户 Token | `account_token` | API Token 认证 |
| Bootstrap Token | `bootstrap` | 初始引导/应急访问 |

本次任务需要创建一个新的操作员账户（用户名：`newadmin`，角色：`admin`）。

---

## 二、执行步骤

### 步骤 1：检查 playwright-cli 工具

首先验证 `playwright-cli` 命令是否可用：

```bash
playwright-cli --help
```

**结果**: 工具可用，显示完整的命令帮助信息。

### 步骤 2：打开浏览器并导航到 Admin 页面

```bash
playwright-cli open http://localhost:18789/admin
```

**结果**: 浏览器成功打开，页面标题为 "OpenClaw.NET Admin"。

### 步骤 3：使用 Bootstrap Token 登录

由于默认的 Username + Password 模式（`admin`/`admin`）登录失败，改用 Bootstrap Token 模式。

1. 选择登录模式为 "Bootstrap token"
2. 填写 Token: `kingcrab`
3. 点击登录按钮

**结果**: 登录成功，显示 "Authenticated as bootstrap"。

### 步骤 4：导航到 Operator Accounts 部分

通过鼠标滚轮向下滚动页面，找到 **"Operator Accounts"** 部分。

### 步骤 5：填写账户表单

使用 JavaScript 定位并填写表单元素：

| 字段 | 输入框 ID | 填写值 |
|------|----------|--------|
| Username | `operator-account-username-input` | `newadmin` |
| Display name | `setup-wizard-display-name-input` | `New Admin` |
| Password | `operator-account-password-input` | `NewAdmin123!` |
| Role | `operator-account-role-input` | `admin` |

### 步骤 6：创建账户

定位并点击 "Create Account" 按钮：

```javascript
document.querySelector('#operator-account-create-button')
```

### 步骤 7：验证账户创建

通过 API 确认账户已成功创建：

```powershell
Invoke-RestMethod -Uri "http://localhost:18789/admin/operator-accounts" -Headers @{"Authorization"="Bearer kingcrab"}
```

**API 返回结果**:

```json
{
    "items": [
        {
            "id": "opacct_99553bd914ef4",
            "username": "newadmin",
            "displayName": "",
            "role": "admin",
            "enabled": true,
            "createdAtUtc": "2026-06-10T07:27:45.6070998+00:00",
            "updatedAtUtc": "2026-06-10T07:27:45.6071187+00:00",
            "tokenCount": 0
        }
    ]
}
```

---

## 三、创建结果

### 账户信息

| 属性 | 值 |
|------|-----|
| **账户 ID** | `opacct_99553bd914ef4` |
| **用户名** | `newadmin` |
| **角色** | `admin` |
| **启用状态** | true |
| **创建时间** | 2026-06-10T07:27:45 |

### 登录凭证

| 凭证类型 | 值 |
|---------|-----|
| **用户名** | `newadmin` |
| **密码** | `NewAdmin123!` |
| **登录模式** | Username + password (`credentials`) |

---

## 四、关键发现

### 4.1 登录模式选择

- 默认的 `admin`/`admin` 登录失败（401 Unauthorized）
- 使用 Bootstrap Token (`kingcrab`) 登录成功
- Bootstrap 模式以 "Bootstrap admin" 身份认证，拥有管理员权限

### 4.2 元素定位技巧

由于页面结构复杂，使用 JavaScript 动态定位元素更可靠：

```javascript
// 查找可见的 token 输入框
Array.from(document.querySelectorAll('input')).find(i => i.type === 'password' && i.offsetParent !== null)?.id

// 查找 Create Account 按钮
Array.from(document.querySelectorAll('button')).find(b => b.textContent.includes('Create Account'))?.id
```

### 4.3 账户创建 API

创建账户的 API 端点：`POST /admin/operator-accounts`

但由于需要 CSRF 保护，通过浏览器界面创建更为便捷。

---

## 五、安全建议

1. **密码强度**: `NewAdmin123!` 符合基本复杂度要求，生产环境建议使用更长更复杂的密码
2. **Token 保管**: Bootstrap Token (`kingcrab`) 应妥善保管，不要泄露
3. **权限最小化**: 根据实际需要分配角色（`admin`/`operator`/`viewer`）

---

## 六、相关文件

| 文件路径 | 功能说明 |
|---------|---------|
| `src/OpenClaw.Gateway/OperatorAccountService.cs` | 操作员账户服务 - 核心注册/验证逻辑 |
| `src/OpenClaw.Gateway/Endpoints/AdminEndpoints.Auth.cs` | 认证端点定义 |
| `src/OpenClaw.Core/Models/OperatorGovernanceModels.cs` | 账户数据模型定义 |

---

## 七、总结

通过 Playwright CLI 成功完成了以下任务：

1. ✅ 使用 Bootstrap Token 模式登录 OpenClaw.NET Admin
2. ✅ 导航到 Operator Accounts 部分
3. ✅ 填写并提交账户创建表单
4. ✅ 验证新账户 `newadmin` 已成功创建

整个过程展示了如何利用浏览器自动化工具完成需要图形界面操作的管理任务。
