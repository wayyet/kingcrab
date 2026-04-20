using System.Text;

var builder = DistributedApplication.CreateBuilder(args);

// 设置控制台输出编码为UTF-8
Console.InputEncoding = Encoding.UTF8; 
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

// 添加 Keycloak 资源
var keycloak = builder.AddKeycloak("keycloak", 8080)
    .WithRealmImport("./Configs/ai4cbrain-realm.json") // 注意：文件名必须匹配 Realm 名称
    // 设置管理员账号密码
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin") 
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", "admin");

var clawCli = builder.AddProject<Projects.OpenClaw_Cli>("cli");
var clawCompanion = builder.AddProject<Projects.OpenClaw_Companion>("companion");
//var clawGateway = builder.AddProject<Projects.OpenClaw_Gateway>("gateway");

var clawGateway = builder.AddProject<Projects.OpenClaw_Gateway>("gateway")
    .WithReference(keycloak)
    .WaitFor(keycloak); // 可选：显式等待健康检查

await builder.Build().RunAsync();
