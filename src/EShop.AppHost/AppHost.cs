var builder = DistributedApplication.CreateBuilder(args);

var sql = builder.AddSqlServer("sql")
    .WithDataVolume();

var ecomDb = sql.AddDatabase("ecom");

var dbInit = builder.AddProject<Projects.EShop_DbInitializer>("dbinit")
    .WithReference(ecomDb)
    .WaitFor(ecomDb);

var mcp = builder.AddProject<Projects.EShop_McpServer>("mcp")
    .WithReference(ecomDb)
    .WaitFor(ecomDb)
    .WaitForCompletion(dbInit);

// Azure OpenAI connection string. In dev, supply via user secrets:
//   dotnet user-secrets set ConnectionStrings:openai "Endpoint=https://<your>.openai.azure.com/;Key=<key>"
var openai = builder.AddConnectionString("openai");

builder.AddProject<Projects.EShop_AgenticAI>("host")
    .WithReference(mcp)
    .WithReference(openai)
    .WaitFor(mcp);

builder.Build().Run();
