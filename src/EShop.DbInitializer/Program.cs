using EShop.Data;
using EShop.DbInitializer;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddSqlServerDbContext<EcomDbContext>("ecom");
builder.Services.AddHostedService<DbInitializerHostedService>();

var host = builder.Build();
host.Run();
