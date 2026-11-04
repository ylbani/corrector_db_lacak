namespace DbSchemaSyncAuditor.Model;

public enum DiffType
{
    MissingTable,
    MissingColumn,
    TypeMismatch,
    NullabilityMismatch,
    ExtraInDb
}

public enum DiffSeverity
{
    Critical,
    Warning,
    Info
}

public record PrimaryKeyConstraint(string Name, List<string> ColumnNames);

public record ColumnDefinition
{
    public required string TableSchema { get; init; }
    public required string TableName { get; init; }
    public required string ColumnName { get; init; }
    public required string StoreType { get; init; }
    public required bool IsNullable { get; init; }
    public bool IsPrimaryKey { get; init; }
    public bool IsExplicitPrecision { get; init; }
    public bool IsExplicitMaxLength { get; init; }
    public string? DefaultValueConstraint { get; init; }
    public string? DefaultValueSql { get; init; }
}

public record TableDefinition
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public Dictionary<string, ColumnDefinition> Columns { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public PrimaryKeyConstraint? PrimaryKey { get; init; }
    public string FullName => $"[{Schema}].[{Name}]";
}

public record SchemaDiffItem
{
    public required DiffType Type { get; init; }
    public required DiffSeverity Severity { get; init; }
    public required string Schema { get; init; }
    public required string TableName { get; init; }
    public string? ColumnName { get; init; }
    public string? CurrentDbType { get; init; }
    public string? ExpectedEfType { get; init; }
    public bool? CurrentIsNullable { get; init; }
    public bool? ExpectedIsNullable { get; init; }
    public required string RecommendedAction { get; init; }
    public ColumnDefinition? SourceColumnDef { get; init; }
    public ColumnDefinition? TargetColumnDef { get; init; }
    public TableDefinition? TargetTableDef { get; init; }

    public string ObjectName => string.IsNullOrEmpty(ColumnName) ? $"[{Schema}].[{TableName}]" : $"[{Schema}].[{TableName}].[{ColumnName}]";
    public string CurrentStateDisplay => CurrentDbType == null ? "(No existe)" : $"{CurrentDbType} {(CurrentIsNullable == true ? "NULL" : "NOT NULL")}";
    public string ExpectedStateDisplay => ExpectedEfType == null ? "(No mapeado en EF)" : $"{ExpectedEfType} {(ExpectedIsNullable == true ? "NULL" : "NOT NULL")}";
    public string SeverityColor => Severity switch
    {
        DiffSeverity.Critical => "#DC2626", // Red
        DiffSeverity.Warning => "#D97706",  // Amber
        _ => "#6B7280"                      // Gray
    };
}
