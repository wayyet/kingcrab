using Microsoft.Extensions.Configuration;
using OpenClaw.SandboxDemo;
using OpenSandbox;
using OpenSandbox.Core;

// ─────────────────────────────────────────────────────────────────────────────
//  配置加载
// ─────────────────────────────────────────────────────────────────────────────
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

var settings = config.GetSection("OpenSandbox").Get<OpenSandboxSettings>()
               ?? new OpenSandboxSettings();

var manager = new SandboxLifecycleManager(settings);

// ─────────────────────────────────────────────────────────────────────────────
//  主菜单循环
// ─────────────────────────────────────────────────────────────────────────────
Console.OutputEncoding = System.Text.Encoding.UTF8;
PrintBanner(settings);

while (true)
{
    PrintMenu();
    var choice = Console.ReadLine()?.Trim();

    switch (choice)
    {
        case "1":
            await CreateSandboxFlow(manager);
            break;
        case "2":
            await ListSandboxesFlow(manager);
            break;
        case "3":
            await GetSandboxDetailFlow(manager);
            break;
        case "4":
            await PauseSandboxFlow(manager);
            break;
        case "5":
            await ResumeSandboxFlow(manager);
            break;
        case "6":
            await RenewExpirationFlow(manager);
            break;
        case "7":
            await DeleteSandboxFlow(manager);
            break;
        case "8":
            await GetEndpointFlow(manager);
            break;
        case "0":
        case "q":
        case "Q":
            Console.WriteLine("退出。");
            return;
        default:
            Console.WriteLine("无效选项，请重新输入。");
            break;
    }

    Console.WriteLine("\n按 Enter 继续...");
    Console.ReadLine();
}

// ─────────────────────────────────────────────────────────────────────────────
//  流程方法
// ─────────────────────────────────────────────────────────────────────────────

static async Task CreateSandboxFlow(SandboxLifecycleManager manager)
{
    Console.Write("  沙箱标签（可选，直接回车跳过）: ");
    var label = Console.ReadLine()?.Trim() ?? "";

    Console.Write($"  超时秒数（直接回车使用默认值）: ");
    var timeoutInput = Console.ReadLine()?.Trim();
    int? timeout = int.TryParse(timeoutInput, out var t) && t > 0 ? t : null;

    Console.WriteLine();
    try
    {
        var sandbox = await manager.CreateAsync(
            label: string.IsNullOrEmpty(label) ? null : label,
            timeoutSeconds: timeout);

        Console.WriteLine($"\n  沙箱 ID: {sandbox.Id}");
        Console.WriteLine("  正在等待沙箱进入 Running 状态...");
        var state = await manager.WaitForStateAsync(sandbox.Id, "Running", maxWaitSeconds: 300);
        Console.WriteLine($"  当前状态: {state}");

        await sandbox.DisposeAsync();
    }
    catch (SandboxApiException ex)
    {
        Console.WriteLine($"\n  [API ERROR] HTTP {ex.StatusCode}: {ex.Error?.Message ?? ex.Message}");
    }
    catch (SandboxException ex)
    {
        Console.WriteLine($"\n  [SANDBOX ERROR] [{ex.Error?.Code}] {ex.Error?.Message ?? ex.Message}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n  [ERROR] {ex.GetType().Name}: {ex.Message}");
    }
}

static async Task ListSandboxesFlow(SandboxLifecycleManager manager)
{
    Console.Write("  状态过滤（Pending/Running/Paused/Terminated/Failed，回车不过滤）: ");
    var stateFilter = Console.ReadLine()?.Trim();

    Console.Write("  页码（默认1）: ");
    var pageInput = Console.ReadLine()?.Trim();
    int page = int.TryParse(pageInput, out var p) && p > 0 ? p : 1;

    Console.Write("  每页条数（默认20，最大200）: ");
    var sizeInput = Console.ReadLine()?.Trim();
    int pageSize = int.TryParse(sizeInput, out var s) && s > 0 ? Math.Min(s, 200) : 20;

    Console.WriteLine();
    try
    {
        await manager.ListSandboxesAsync(
            stateFilter: string.IsNullOrEmpty(stateFilter) ? null : stateFilter,
            page: page,
            pageSize: pageSize);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n  [ERROR] {ex.GetType().Name}: {ex.Message}");
    }
}

static async Task GetSandboxDetailFlow(SandboxLifecycleManager manager)
{
    var id = ReadSandboxId();
    if (id is null) return;

    Console.WriteLine();
    try
    {
        await manager.PrintSandboxDetailAsync(id);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n  [ERROR] {ex.GetType().Name}: {ex.Message}");
    }
}

static async Task PauseSandboxFlow(SandboxLifecycleManager manager)
{
    var id = ReadSandboxId();
    if (id is null) return;

    Console.WriteLine();
    try
    {
        await manager.PauseSandboxAsync(id);
        Console.WriteLine("  等待沙箱进入 Paused 状态...");
        await manager.WaitForStateAsync(id, "Paused", maxWaitSeconds: 60);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n  [ERROR] {ex.GetType().Name}: {ex.Message}");
    }
}

static async Task ResumeSandboxFlow(SandboxLifecycleManager manager)
{
    var id = ReadSandboxId();
    if (id is null) return;

    Console.WriteLine();
    try
    {
        await manager.ResumeSandboxAsync(id);
        Console.WriteLine("  等待沙箱进入 Running 状态...");
        await manager.WaitForStateAsync(id, "Running", maxWaitSeconds: 120);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n  [ERROR] {ex.GetType().Name}: {ex.Message}");
    }
}

static async Task RenewExpirationFlow(SandboxLifecycleManager manager)
{
    var id = ReadSandboxId();
    if (id is null) return;

    Console.Write("  续期秒数（例如 3600 = 1小时）: ");
    var secInput = Console.ReadLine()?.Trim();
    if (!int.TryParse(secInput, out var addSec) || addSec <= 0)
    {
        Console.WriteLine("  无效的秒数，操作取消。");
        return;
    }

    Console.WriteLine();
    try
    {
        await manager.RenewExpirationAsync(id, addSec);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n  [ERROR] {ex.GetType().Name}: {ex.Message}");
    }
}

static async Task DeleteSandboxFlow(SandboxLifecycleManager manager)
{
    var id = ReadSandboxId();
    if (id is null) return;

    Console.Write($"  确认删除沙箱 [{id}]？(y/N): ");
    var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
    if (confirm != "y")
    {
        Console.WriteLine("  操作已取消。");
        return;
    }

    Console.WriteLine();
    try
    {
        await manager.DeleteSandboxAsync(id);
        Console.WriteLine("  等待沙箱进入 Terminated 状态...");
        await manager.WaitForStateAsync(id, "Terminated", maxWaitSeconds: 60);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n  [ERROR] {ex.GetType().Name}: {ex.Message}");
    }
}

static async Task GetEndpointFlow(SandboxLifecycleManager manager)
{
    var id = ReadSandboxId();
    if (id is null) return;

    Console.Write("  端口号: ");
    var portInput = Console.ReadLine()?.Trim();
    if (!int.TryParse(portInput, out var port) || port < 1 || port > 65535)
    {
        Console.WriteLine("  无效端口，操作取消。");
        return;
    }

    Console.WriteLine();
    try
    {
        await manager.PrintEndpointAsync(id, port);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n  [ERROR] {ex.GetType().Name}: {ex.Message}");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  工具函数
// ─────────────────────────────────────────────────────────────────────────────

static string? ReadSandboxId()
{
    Console.Write("  沙箱 ID: ");
    var id = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(id))
    {
        Console.WriteLine("  沙箱 ID 不能为空，操作取消。");
        return null;
    }
    return id;
}

static void PrintBanner(OpenSandboxSettings s)
{
    Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
    Console.WriteLine("║        OpenSandbox 沙箱管理控制台示例 (.NET 10)          ║");
    Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
    Console.WriteLine($"  服务器: {s.Protocol}://{s.Domain}");
    Console.WriteLine($"  镜像  : {s.Image}");
    Console.WriteLine();
}

static void PrintMenu()
{
    Console.WriteLine("┌─────────────────────────────────────────────────┐");
    Console.WriteLine("│  1. 创建沙箱 (Create)                            │");
    Console.WriteLine("│  2. 列出沙箱 (List)                              │");
    Console.WriteLine("│  3. 查看沙箱详情 (Get)                           │");
    Console.WriteLine("│  4. 暂停沙箱 (Pause)                             │");
    Console.WriteLine("│  5. 恢复沙箱 (Resume)                            │");
    Console.WriteLine("│  6. 续期过期时间 (Renew Expiration)              │");
    Console.WriteLine("│  7. 删除沙箱 (Delete)                            │");
    Console.WriteLine("│  8. 获取服务端点 (Get Endpoint)                  │");
    Console.WriteLine("│  0/q. 退出                                       │");
    Console.WriteLine("└─────────────────────────────────────────────────┘");
    Console.Write("请选择: ");
}
