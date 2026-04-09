var builder = DistributedApplication.CreateBuilder(args);

var clawCli = builder.AddProject<Projects.OpenClaw_Cli>("cli");
var clawCompanion = builder.AddProject<Projects.OpenClaw_Companion>("companion");
var clawGateway = builder.AddProject<Projects.OpenClaw_Gateway>("gateway");

await builder.Build().RunAsync();
