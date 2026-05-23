using System.Text.Json;
using EShop.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EShop.McpServer.Resources;

public sealed class EcomSchemaCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string Json { get; }

    public EcomSchemaCache(EcomDbContext db)
    {
        Json = Build(db);
    }

    private static string Build(EcomDbContext db)
    {
        var tables = db.Model.GetEntityTypes()
            .Select(MapEntity)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        return JsonSerializer.Serialize(new { tables }, JsonOptions);
    }

    private static TableInfo MapEntity(IEntityType et)
    {
        var pkSet = et.FindPrimaryKey()?.Properties.Select(p => p.Name).ToHashSet() ?? new HashSet<string>();

        var columns = et.GetProperties()
            .Select(p => new ColumnInfo(
                Name: p.GetColumnName(),
                ClrType: p.ClrType.Name,
                Nullable: p.IsNullable,
                IsPrimaryKey: pkSet.Contains(p.Name),
                MaxLength: p.GetMaxLength(),
                Precision: p.GetPrecision(),
                Scale: p.GetScale()))
            .ToList();

        var foreignKeys = et.GetForeignKeys()
            .Select(fk => new ForeignKeyInfo(
                Columns: fk.Properties.Select(p => p.GetColumnName()).ToArray(),
                PrincipalTable: fk.PrincipalEntityType.GetTableName() ?? fk.PrincipalEntityType.ShortName(),
                PrincipalColumns: fk.PrincipalKey.Properties.Select(p => p.GetColumnName()).ToArray()))
            .ToList();

        return new TableInfo(
            Name: et.GetTableName() ?? et.ShortName(),
            ClrType: et.ClrType.FullName ?? et.ClrType.Name,
            Columns: columns,
            ForeignKeys: foreignKeys);
    }

    private sealed record TableInfo(string Name, string ClrType, IReadOnlyList<ColumnInfo> Columns, IReadOnlyList<ForeignKeyInfo> ForeignKeys);
    private sealed record ColumnInfo(string Name, string ClrType, bool Nullable, bool IsPrimaryKey, int? MaxLength, int? Precision, int? Scale);
    private sealed record ForeignKeyInfo(string[] Columns, string PrincipalTable, string[] PrincipalColumns);
}
