using System.Text;

var builder = DistributedApplication.CreateBuilder(args);

// 设置控制台输出编码为UTF-8
Console.OutputEncoding = Encoding.UTF8;

var clawCli = builder.AddProject<Projects.OpenClaw_Cli>("cli");
var clawCompanion = builder.AddProject<Projects.OpenClaw_Companion>("companion");
var clawGateway = builder.AddProject<Projects.OpenClaw_Gateway>("gateway");

await builder.Build().RunAsync();
