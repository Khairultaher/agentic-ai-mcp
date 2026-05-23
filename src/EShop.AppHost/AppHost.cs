var builder = DistributedApplication.CreateBuilder(args);

// Point at the local SQL Server instance on localhost and database e-shop-db.
// Windows auth (Trusted_Connection) — switch to "User Id=...;Password=...;" for SQL auth.
// DbInitializer's MigrateAsync creates e-shop-db automatically if it does not exist
// (requires the connecting principal to have CREATE DATABASE permission).
var ecomDb = builder.AddConnectionString(
    "ecom",
    ReferenceExpression.Create(
        $"Server=localhost;Database=e-shop-db;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True;"));

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
