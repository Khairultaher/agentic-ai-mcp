using EShop.Data;
using EShop.McpServer.Resources;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly()
    .WithPromptsFromAssembly();
builder.AddSqlServerDbContext<EcomDbContext>("ecom");

builder.Services.AddSingleton<EcomSchemaCache>(sp =>
{
    using var scope = sp.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<EcomDbContext>();
    return new EcomSchemaCache(db);
});

var app = builder.Build();

// Eagerly build the schema JSON so the first reads://schema://ecom call is instant.
_ = app.Services.GetRequiredService<EcomSchemaCache>();

app.MapDefaultEndpoints();
app.MapMcp();

app.Run();
