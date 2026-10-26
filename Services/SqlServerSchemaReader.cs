using System.Data;
using DbSchemaSyncAuditor.Model;
using Microsoft.Data.SqlClient;

namespace DbSchemaSyncAuditor.Services;

public class SqlServerSchemaReader
{
    public async Task<List<string>> GetDatabasesAsync(string server, string user, string password, bool useWindowsAuth, bool trustServerCertificate = true, CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = "master",
            IntegratedSecurity = useWindowsAuth,
            TrustServerCertificate = trustServerCertificate,
            ConnectTimeout = 15
        };

        if (!useWindowsAuth)
        {
            builder.UserID = user;
            builder.Password = password;
        }

        var databases = new List<string>();

        const string sql = @"
            SELECT name 
            FROM sys.databases 
            WHERE database_id > 4 
              AND state_desc = 'ONLINE'
              AND HAS_DBACCESS(name) = 1
            ORDER BY name;";

        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            databases.Add(reader.GetString(0));
        }

        return databases;
    }

    public string BuildConnectionString(string server, string database, string user, string password, bool useWindowsAuth, bool trustServerCertificate = true)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            IntegratedSecurity = useWindowsAuth,
            TrustServerCertificate = trustServerCertificate
        };

        if (!useWindowsAuth)
        {
            builder.UserID = user;
            builder.Password = password;
        }

        return builder.ConnectionString;
    }

    public async Task<Dictionary<string, TableDefinition>> ReadDatabaseSchemaAsync(string connectionString, CancellationToken ct = default)
    {
        var tables = new Dictionary<string, TableDefinition>(StringComparer.OrdinalIgnoreCase);

        const string sqlTablesAndColumns = @"
            SELECT 
                s.name AS TableSchema,
                t.name AS TableName,
                c.name AS ColumnName,
                ty.name AS DataType,
                c.max_length AS MaxLength,
                c.precision AS [Precision],
                c.scale AS Scale,
                c.is_nullable AS IsNullable,
                ISNULL(i.is_primary_key, 0) AS IsPrimaryKey,
                dc.name AS DefaultConstraintName,
                dc.definition AS DefaultDefinition
            FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            INNER JOIN sys.columns c ON t.object_id = c.object_id
            INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
            LEFT JOIN sys.index_columns ic ON ic.object_id = t.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.indexes i ON i.object_id = t.object_id AND i.index_id = ic.index_id AND i.is_primary_key = 1
            LEFT JOIN sys.default_constraints dc ON c.default_object_id = dc.object_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name, c.column_id;";

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand(sqlTablesAndColumns, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var schema = reader.GetString("TableSchema");
            var tableName = reader.GetString("TableName");
            var tableKey = $"{schema}.{tableName}";

            if (!tables.TryGetValue(tableKey, out var table))
            {
                table = new TableDefinition { Schema = schema, Name = tableName };
                tables[tableKey] = table;
            }

            var colName = reader.GetString("ColumnName");
            var dataType = reader.GetString("DataType");
            var maxLength = reader.GetInt16("MaxLength");
            var precision = reader.GetByte("Precision");
            var scale = reader.GetByte("Scale");
            var isNullable = reader.GetBoolean("IsNullable");
            var isPk = Convert.ToBoolean(reader["IsPrimaryKey"]);
            var defaultConstraint = reader.IsDBNull("DefaultConstraintName") ? null : reader.GetString("DefaultConstraintName");
            var defaultSql = reader.IsDBNull("DefaultDefinition") ? null : reader.GetString("DefaultDefinition");

            var storeType = FormatStoreType(dataType, maxLength, precision, scale);

            table.Columns[colName] = new ColumnDefinition
            {
                TableSchema = schema,
                TableName = tableName,
                ColumnName = colName,
                StoreType = storeType,
                IsNullable = isNullable,
                IsPrimaryKey = isPk,
                DefaultValueConstraint = defaultConstraint,
                DefaultValueSql = defaultSql
            };
        }

        return tables;
    }

    private static string FormatStoreType(string dataType, short maxLength, byte precision, byte scale)
    {
        var lower = dataType.ToLowerInvariant();
        return lower switch
        {
            "nvarchar" or "nchar" => maxLength == -1 ? $"{lower}(max)" : $"{lower}({maxLength / 2})",
            "varchar" or "char" or "varbinary" or "binary" => maxLength == -1 ? $"{lower}(max)" : $"{lower}({maxLength})",
            "decimal" or "numeric" => $"{lower}({precision},{scale})",
            _ => lower
        };
    }
}
