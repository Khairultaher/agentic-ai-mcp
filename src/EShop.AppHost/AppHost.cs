var builder = DistributedApplication.CreateBuilder(args);

// SQL Server runs in an externally-managed Docker container (see docker-compose
// at repo root: service `sqlserver`, container `sql2022`, port 1433, sa auth).
// We connect to it rather than letting Aspire spawn a second container.
// DbInitializer's MigrateAsync creates `e-shop-db` on first run (sa has CREATE DATABASE).
var ecomDb = builder.AddConnectionString(
    "ecom",
    ReferenceExpression.Create(
        $"Server=localhost,1433;Database=e-shop-db;User Id=sa;Password=Dev@Password123;Encrypt=True;TrustServerCertificate=True;"));

var dbInit = builder.AddProject<Projects.EShop_DbInitializer>("dbinit")
    .WithReference(ecomDb);

var mcp = builder.AddProject<Projects.EShop_McpServer>("mcp")
    .WithReference(ecomDb)
    .WaitForCompletion(dbInit);

// Azure OpenAI is configured in the host project via env vars:
//   AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT_NAME
// Aspire forwards the AppHost's process env vars to child projects automatically.
builder.AddProject<Projects.EShop_AgenticAI>("host")
    .WithReference(mcp)
    .WaitFor(mcp);

builder.Build().Run();
