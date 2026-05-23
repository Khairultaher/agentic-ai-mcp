using System.ComponentModel;
using ModelContextProtocol.Server;

namespace EShop.McpServer.Resources;

[McpServerResourceType]
public static class SchemaResource
{
    [McpServerResource(UriTemplate = "schema://ecom", Name = "EcomSchema", MimeType = "application/json")]
    [Description("JSON description of every table in the ecom database (tables, columns with CLR type and nullability, primary keys, and foreign keys). Generated from the EF Core model at startup; safe to read without hitting the database.")]
    public static string GetEcomSchema(EcomSchemaCache cache) => cache.Json;
}
