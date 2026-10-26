using System.Text;
using DbSchemaSyncAuditor.Model;

namespace DbSchemaSyncAuditor.Services;

public class SqlPatchGenerator
{
    public string GeneratePatchScript(IEnumerable<SchemaDiffItem> diffs, string clientName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"-- =============================================================================");
        sb.AppendLine($"-- MIGRATION SCRIPT FOR CLIENT: {clientName}");
        sb.AppendLine($"-- GENERATED AT: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"-- =============================================================================");
        sb.AppendLine("SET XACT_ABORT ON;");
        sb.AppendLine("BEGIN TRANSACTION;");
        sb.AppendLine("BEGIN TRY");
        sb.AppendLine("    DECLARE @sql NVARCHAR(MAX);");
        sb.AppendLine();

        foreach (var diff in diffs.Where(d => d.Type != DiffType.ExtraInDb))
        {
            switch (diff.Type)
            {
                case DiffType.MissingTable:
                    AppendCreateTable(sb, diff.TargetTableDef!);
                    break;

                case DiffType.MissingColumn:
                    AppendAddColumn(sb, diff.TargetColumnDef!);
                    break;

                case DiffType.TypeMismatch:
                case DiffType.NullabilityMismatch:
                    AppendAlterColumnDefensive(sb, diff.SourceColumnDef!, diff.TargetColumnDef!);
                    break;
            }
        }

        sb.AppendLine();
        sb.AppendLine("    COMMIT TRANSACTION;");
        sb.AppendLine("    PRINT '>> Migration script executed successfully.';");
        sb.AppendLine("END TRY");
        sb.AppendLine("BEGIN CATCH");
        sb.AppendLine("    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
        sb.AppendLine("    DECLARE @ErrMsg NVARCHAR(4000) = ERROR_MESSAGE();");
        sb.AppendLine("    DECLARE @ErrSev INT = ERROR_SEVERITY();");
        sb.AppendLine("    DECLARE @ErrState INT = ERROR_STATE();");
        sb.AppendLine("    RAISERROR(@ErrMsg, @ErrSev, @ErrState);");
        sb.AppendLine("END CATCH;");

        return sb.ToString();
    }

    private static void AppendCreateTable(StringBuilder sb, TableDefinition table)
    {
        sb.AppendLine($"    -- [MISSING TABLE] [{table.Schema}].[{table.Name}]");
        sb.AppendLine($"    IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{table.Schema}' AND t.name = '{table.Name}')");
        sb.AppendLine("    BEGIN");
        sb.AppendLine($"        CREATE TABLE [{table.Schema}].[{table.Name}] (");

        var colLines = new List<string>();
        foreach (var col in table.Columns.Values)
        {
            var nullability = col.IsNullable ? "NULL" : "NOT NULL";
            colLines.Add($"            [{col.ColumnName}] {col.StoreType} {nullability}");
        }

        if (table.PrimaryKey is not null && table.PrimaryKey.ColumnNames.Count > 0)
        {
            var pks = string.Join(", ", table.PrimaryKey.ColumnNames.Select(c => $"[{c}]"));
            colLines.Add($"            CONSTRAINT [{table.PrimaryKey.Name}] PRIMARY KEY ({pks})");
        }

        sb.AppendLine(string.Join(",\n", colLines));
        sb.AppendLine("        );");
        sb.AppendLine($"        PRINT 'Created table [{table.Schema}].[{table.Name}]';");
        sb.AppendLine("    END");
        sb.AppendLine();
    }

    private static void AppendAddColumn(StringBuilder sb, ColumnDefinition col)
    {
        sb.AppendLine($"    -- [MISSING COLUMN] [{col.TableSchema}].[{col.TableName}].[{col.ColumnName}]");
        sb.AppendLine($"    IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{col.TableSchema}' AND t.name = '{col.TableName}' AND c.name = '{col.ColumnName}')");
        sb.AppendLine("    BEGIN");

        if (!col.IsNullable)
        {
            var defValue = ResolveDefaultLiteral(col.StoreType);
            var constraintName = $"DF_{col.TableName}_{col.ColumnName}_{Guid.NewGuid():N}".Substring(0, 32);
            sb.AppendLine($"        ALTER TABLE [{col.TableSchema}].[{col.TableName}] ADD [{col.ColumnName}] {col.StoreType} NOT NULL CONSTRAINT [{constraintName}] DEFAULT {defValue};");
            sb.AppendLine($"        ALTER TABLE [{col.TableSchema}].[{col.TableName}] DROP CONSTRAINT [{constraintName}];");
        }
        else
        {
            sb.AppendLine($"        ALTER TABLE [{col.TableSchema}].[{col.TableName}] ADD [{col.ColumnName}] {col.StoreType} NULL;");
        }

        sb.AppendLine($"        PRINT 'Added column [{col.ColumnName}] to [{col.TableSchema}].[{col.TableName}]';");
        sb.AppendLine("    END");
        sb.AppendLine();
    }

    private static void AppendAlterColumnDefensive(StringBuilder sb, ColumnDefinition src, ColumnDefinition tgt)
    {
        sb.AppendLine($"    -- [ALTER COLUMN] [{src.TableSchema}].[{src.TableName}].[{src.ColumnName}] ({src.StoreType} -> {tgt.StoreType})");
        sb.AppendLine("    BEGIN");
        
        // 1. Dropear Default Constraints dependientes
        sb.AppendLine($@"        SET @sql = N'';
        SELECT @sql += N'ALTER TABLE [{src.TableSchema}].[{src.TableName}] DROP CONSTRAINT ' + QUOTENAME(dc.name) + ';'
        FROM sys.default_constraints dc
        JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
        JOIN sys.tables t ON c.object_id = t.object_id
        JOIN sys.schemas s ON t.schema_id = s.schema_id
        WHERE s.name = '{src.TableSchema}' AND t.name = '{src.TableName}' AND c.name = '{src.ColumnName}';
        IF @sql <> N'' EXEC sp_executesql @sql;");

        // 2. Dropear Índices dependientes
        sb.AppendLine($@"        SET @sql = N'';
        SELECT @sql += N'DROP INDEX ' + QUOTENAME(i.name) + N' ON [{src.TableSchema}].[{src.TableName}];'
        FROM sys.indexes i
        JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
        JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        JOIN sys.tables t ON c.object_id = t.object_id
        JOIN sys.schemas s ON t.schema_id = s.schema_id
        WHERE s.name = '{src.TableSchema}' AND t.name = '{src.TableName}' AND c.name = '{src.ColumnName}' AND i.is_primary_key = 0;
        IF @sql <> N'' EXEC sp_executesql @sql;");

        // 3. Sanitizar nulos si cambia a NOT NULL
        if (src.IsNullable && !tgt.IsNullable)
        {
            var defValue = ResolveDefaultLiteral(tgt.StoreType);
            sb.AppendLine($"        UPDATE [{src.TableSchema}].[{src.TableName}] SET [{src.ColumnName}] = {defValue} WHERE [{src.ColumnName}] IS NULL;");
        }

        // 4. Ejecutar ALTER COLUMN
        var nullability = tgt.IsNullable ? "NULL" : "NOT NULL";
        sb.AppendLine($"        ALTER TABLE [{tgt.TableSchema}].[{tgt.TableName}] ALTER COLUMN [{tgt.ColumnName}] {tgt.StoreType} {nullability};");
        sb.AppendLine($"        PRINT 'Altered column [{tgt.ColumnName}] on [{tgt.TableSchema}].[{tgt.TableName}]';");
        sb.AppendLine("    END");
        sb.AppendLine();
    }

    private static string ResolveDefaultLiteral(string storeType)
    {
        var lower = storeType.ToLowerInvariant();
        if (lower.Contains("int") || lower.Contains("decimal") || lower.Contains("numeric") || lower.Contains("float")) return "0";
        if (lower.Contains("bit")) return "0";
        if (lower.Contains("char") || lower.Contains("text")) return "''";
        if (lower.Contains("datetime") || lower.Contains("date")) return "'1900-01-01'";
        if (lower.Contains("uniqueidentifier")) return "'00000000-0000-00-0000-000000000000'";
        return "''";
    }
}
