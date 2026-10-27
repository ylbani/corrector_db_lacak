using DbSchemaSyncAuditor.Model;

namespace DbSchemaSyncAuditor.Services;

public class SchemaDiffEngine
{
    public List<SchemaDiffItem> Compare(
        Dictionary<string, TableDefinition> dbSchema,
        Dictionary<string, TableDefinition> efSchema)
    {
        var diffs = new List<SchemaDiffItem>();

        // 1. Evaluar tablas y columnas esperadas por EF (Target)
        foreach (var (tableKey, efTable) in efSchema)
        {
            var dbTable = FindMatchingTable(dbSchema, efTable);

            if (dbTable == null)
            {
                diffs.Add(new SchemaDiffItem
                {
                    Type = DiffType.MissingTable,
                    Severity = DiffSeverity.Critical,
                    Schema = efTable.Schema,
                    TableName = efTable.Name,
                    RecommendedAction = "CREATE TABLE",
                    TargetTableDef = efTable
                });
                continue;
            }

            foreach (var (colName, efCol) in efTable.Columns)
            {
                if (!dbTable.Columns.TryGetValue(colName, out var dbCol))
                {
                    diffs.Add(new SchemaDiffItem
                    {
                        Type = DiffType.MissingColumn,
                        Severity = DiffSeverity.Critical,
                        Schema = dbTable.Schema,
                        TableName = dbTable.Name,
                        ColumnName = colName,
                        ExpectedEfType = efCol.StoreType,
                        ExpectedIsNullable = efCol.IsNullable,
                        RecommendedAction = $"ALTER TABLE [{dbTable.Schema}].[{dbTable.Name}] ADD [{colName}]",
                        TargetColumnDef = efCol with { TableSchema = dbTable.Schema, TableName = dbTable.Name }
                    });
                    continue;
                }

                var typeChanged = !AreTypesEquivalent(dbCol.StoreType, efCol.StoreType);
                var nullChanged = dbCol.IsNullable != efCol.IsNullable;

                if (typeChanged || nullChanged)
                {
                    diffs.Add(new SchemaDiffItem
                    {
                        Type = typeChanged ? DiffType.TypeMismatch : DiffType.NullabilityMismatch,
                        Severity = DiffSeverity.Warning,
                        Schema = dbTable.Schema,
                        TableName = dbTable.Name,
                        ColumnName = colName,
                        CurrentDbType = dbCol.StoreType,
                        ExpectedEfType = efCol.StoreType,
                        CurrentIsNullable = dbCol.IsNullable,
                        ExpectedIsNullable = efCol.IsNullable,
                        RecommendedAction = $"ALTER TABLE [{dbTable.Schema}].[{dbTable.Name}] ALTER COLUMN [{colName}] (Safe Drop & Rebuild)",
                        SourceColumnDef = dbCol,
                        TargetColumnDef = efCol with { TableSchema = dbTable.Schema, TableName = dbTable.Name }
                    });
                }
            }
        }

        // 2. ExtraInDb (Informativo)
        var efKeys = new HashSet<string>(
            efSchema.Values.Select(e => $"{e.Schema}.{e.Name}"),
            StringComparer.OrdinalIgnoreCase);

        foreach (var (tableKey, dbTable) in dbSchema)
        {
            if (!efKeys.Contains($"{dbTable.Schema}.{dbTable.Name}"))
            {
                diffs.Add(new SchemaDiffItem
                {
                    Type = DiffType.ExtraInDb,
                    Severity = DiffSeverity.Info,
                    Schema = dbTable.Schema,
                    TableName = dbTable.Name,
                    RecommendedAction = "Ignorar (Mantiene extensiones del cliente)"
                });
            }
        }

        return diffs;
    }

    private static TableDefinition? FindMatchingTable(Dictionary<string, TableDefinition> dbSchema, TableDefinition efTable)
    {
        // Coincidencia EXACTA schema.nombre (sin heurísticas de plural: ocultaban faltantes reales)
        if (dbSchema.TryGetValue($"{efTable.Schema}.{efTable.Name}", out var exact))
            return exact;

        foreach (var t in dbSchema.Values)
        {
            if (string.Equals(t.Schema, efTable.Schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Name, efTable.Name, StringComparison.OrdinalIgnoreCase))
                return t;
        }

        return null;
    }

    private static bool AreTypesEquivalent(string typeA, string typeB)
    {
        string Clean(string t) => t.Replace(" ", "").ToLowerInvariant();
        var a = Clean(typeA);
        var b = Clean(typeB);

        if (a == b) return true;

        // La precisión fluent (HasPrecision) no es legible por reflexión: EF reporta decimal(18,2)
        // por defecto, así que se acepta cualquier decimal en BD para evitar falsos positivos.
        if (b == "decimal(18,2)" && a.StartsWith("decimal(")) return true;

        // [MaxLength]/[StringLength] y HasColumnType fluent tampoco son siempre legibles:
        // EF reporta nvarchar(max) cuando la BD tiene nvarchar(n). No se altera (evita falsos positivos).
        if (b == "nvarchar(max)" && a.StartsWith("nvarchar(")) return true;

        // Nota: Si la BD tiene float o real y el modelo de EF tiene decimal, NO son equivalentes
        // y causan System.InvalidOperationException al materializar con EF. Por ello se reportan como TypeMismatch.

        if ((a == "int" && b == "integer") || (a == "integer" && b == "int")) return true;
        if ((a == "bit" && b == "boolean") || (a == "boolean" && b == "bit")) return true;

        return false;
    }
}
