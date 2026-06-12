# OpenClaw.NET Admin 登录认证系统分析

## 概述

本文档分析 OpenClaw.NET Admin 界面的用户名密码注册、设置位置以及认证流程。

**分析日期**: 2026-06-10

---

## 一、认证系统架构

OpenClaw.NET 提供了 **3 种登录认证模式**：

| 登录模式 | 值 | 用途 |
|---------|-----|------|
| 用户名+密码 | `credentials` | 标准操作员登录 |
| 账户 Token | `account_token` | API Token 认证 |
| Bootstrap Token | `bootstrap` | 初始引导/应急访问 |

---

## 二、用户名密码注册位置

### 2.1 核心服务

**文件**: `src/OpenClaw.Gateway/OperatorAccountService.cs`

这是用户名密码注册的核心逻辑所在。

#### 创建新账户 (第 86-111 行)

```csharp
public OperatorAccountSummary Create(OperatorAccountCreateRequest request)
{
    var username = NormalizeUsername(request.Username);
    var password = NormalizePassword(request.Password);

    // 密码使用 PBKDF2 加密 (120,000 iterations)
    var salt = GenerateSalt();
    var account = new StoredAccount
    {
        Id = $"opacct_{Guid.NewGuid():N}"[..20],
        Username = username,
        PasswordSalt = salt,
        PasswordHash = HashSecret(password, salt),
        DisplayName = request.DisplayName ?? username,
        Role = request.Role ?? OperatorRoleNames.Viewer,
        Enabled = true,
        Tokens = []
    };

    SaveAccounts(newAccounts: [account]);
    return ToSummary(account);
}
```

**关键点**:
- 用户名经过 `NormalizeUsername()` 规范化处理
- 密码经过 `NormalizePassword()` 规范化处理
- 密码使用 **PBKDF2** 算法加密，**120,000 次迭代**
- 每个密码生成随机 `salt` 盐值
- 账户 ID 格式: `opacct_` + 32位 GUID

#### 密码验证 (第 113-122 行)

```csharp
private static bool SecretMatches(string secret, string salt, string hash)
{
    // 使用恒定时间比较防止时序攻击
    return CryptOperations.Equal(HashSecret(secret, salt), hash);
}
```

### 2.2 认证端点

**文件**: `src/OpenClaw.Gateway/Endpoints/AdminEndpoints.Auth.cs`

认证请求发送到 `/auth/session` 端点：

```csharp
public async Task<bool> TryAuthenticate(...)
{
    return request.Mode switch
    {
        "credentials" => await TryAuthenticatePassword(username, password),
        "token" => await TryAuthenticateToken(token),
        "bootstrap" => TryBootstrapAuth(token),
        _ => false
    };
}
```

---

## 三、账户数据存储

### 3.1 存储位置

账户数据存储在 `admin/operator-accounts.json` 文件中。

### 3.2 账户数据结构

```csharp
internal sealed class StoredAccount
{
    public required string Id { get; init; }              // 账户ID
    public required string Username { get; set; }         // 用户名
    public string DisplayName { get; set; } = "";        // 显示名称
    public string Role { get; set; } = "viewer";         // 角色权限
    public bool Enabled { get; set; } = true;            // 是否启用
    public required string PasswordSalt { get; set; }    // 密码盐
    public required string PasswordHash { get; set; }     // 密码哈希
    public List<StoredToken> Tokens { get; init; } = []; // API Tokens
}
```

---

## 四、Bootstrap Token 配置

### 4.1 Keycloak 管理员

在 `Kingcrab.AppHost/AppHost.cs` 中配置：

```csharp
var keycloak = builder.AddKeycloak("keycloak", 8080)
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", "admin");
```

**默认值**: 用户名 `admin`, 密码 `admin`

### 4.2 Gateway AuthToken

用于 Bootstrap Token 认证模式：

```csharp
if (policy.BootstrapTokenEnabled &&
    GatewaySecurity.IsTokenValid(token, startup.Config.AuthToken))
{
    return new OperatorAuthorizationResult(
        true,
        "bearer",
        Role: OperatorRoleNames.Admin,
        IsBootstrapAdmin: true);
}
```

---

## 五、认证流程图

```
                    用户登录请求
                         │
                         ▼
            ┌────────────────────────┐
            │   POST /auth/session   │
            └────────────────────────┘
                         │
            ┌────────────┼────────────┐
            ▼            ▼            ▼
       credentials   account_token  bootstrap
       (用户名密码)    (Token认证)   (引导Token)
            │            │            │
            ▼            ▼            ▼
    TryAuthenticate  TryAuthenticate  GatewaySecurity
       Password        Token         IsTokenValid
            │            │            │
            └────────────┼────────────┘
                         ▼
                   验证成功/失败
```

---

## 六、密码安全机制

### 6.1 加密算法

- **算法**: PBKDF2 (Password-Based Key Derivation Function 2)
- **迭代次数**: 120,000 次
- **盐值**: 每个密码使用独立的随机盐

### 6.2 安全特性

1. **恒定时间比较**: 使用 `CryptOperations.Equal()` 防止时序攻击
2. **密码规范化**: 登录前对用户名和密码进行规范化处理
3. **独立盐值**: 相同密码会产生不同的哈希值

---

## 七、角色权限

| 角色名 | 说明 |
|-------|------|
| `admin` | 管理员 - 完整权限 |
| `operator` | 操作员 - 受限管理权限 |
| `viewer` | 查看者 - 仅读权限 (默认值) |

---

## 八、相关文件清单

| 文件路径 | 功能说明 |
|---------|---------|
| `src/OpenClaw.Gateway/OperatorAccountService.cs` | 操作员账户服务 - 核心注册/验证 |
| `src/OpenClaw.Gateway/Endpoints/AdminEndpoints.Auth.cs` | 认证端点定义 |
| `src/OpenClaw.Gateway/Endpoints/EndpointHelpers.cs` | 请求授权判断 |
| `src/OpenClaw.Dashboard/Services/AuthService.cs` | Dashboard 前端认证服务 |
| `Kingcrab.AppHost/AppHost.cs` | Keycloak 和 Bootstrap 配置 |
| `src/OpenClaw.Gateway/CryptOperations.cs` | 密码哈希和加密操作 |

---

## 九、常见问题

### Q1: 如何创建第一个用户？
1. 使用 Bootstrap Token 登录 (需要配置 `AuthToken`)
2. 调用 `POST /auth/session` with mode=`bootstrap`
3. 然后可以创建普通用户账户

### Q2: 忘记密码怎么办？
- 如果配置了 Bootstrap Token，可以使用引导模式登录
- 或者直接修改 `admin/operator-accounts.json` 文件

### Q3: 如何禁用用户？
在 `operator-accounts.json` 中设置 `"Enabled": false`，或在代码中调用禁用接口。

---

## 十、总结

OpenClaw.NET 的认证系统设计清晰：

1. **用户注册** 通过 `OperatorAccountService.Create()` 完成，密码自动 PBKDF2 加密存储
2. **三种认证模式** 支持灵活的身份验证方式
3. **密码安全** 采用行业标准的 PBKDF2 算法，120,000 次迭代
4. **数据存储** 账户信息存储在 JSON 文件中，支持完整的账户管理

这个设计既保证了安全性，又提供了良好的扩展性和调试便利性。
