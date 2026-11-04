using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using DbSchemaSyncAuditor.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DbSchemaSyncAuditor.Services;

public class EfModelInspector
{
    private class FolderAssemblyLoadContext : AssemblyLoadContext
    {
        private readonly string _folder;

        public FolderAssemblyLoadContext(string folder) : base(isCollectible: true)
        {
            _folder = folder;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var candidate = Path.Combine(_folder, assemblyName.Name + ".dll");
            if (File.Exists(candidate))
            {
                return LoadFromAssemblyPath(candidate);
            }
            return null;
        }
    }

    private readonly DataContextModelParser _dataContextParser = new();

    public DataContextModelParser DataContextParser => _dataContextParser;

    public List<Type> DiscoverDbContextTypesFromAssembly(string assemblyPath)
    {
        var folder = Path.GetDirectoryName(assemblyPath) ?? AppDomain.CurrentDomain.BaseDirectory;
        var loadContext = new FolderAssemblyLoadContext(folder);
        var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Tolerar referencias del .NET Framework clásico que no existan en el runtime actual
            types = ex.Types.Where(t => t != null).ToArray()!;
        }

        var dbContextTypes = new List<Type>();

        foreach (var t in types)
        {
            if (t.IsAbstract || !t.IsClass) continue;

            // EF Core DbContext
            if (typeof(Microsoft.EntityFrameworkCore.DbContext).IsAssignableFrom(t))
            {
                dbContextTypes.Add(t);
                continue;
            }

            // EF6 o heredero por nombre ("DbContext" o "IdentityDbContext")
            var baseType = t.BaseType;
            while (baseType != null)
            {
                if (baseType.Name.Contains("DbContext"))
                {
                    dbContextTypes.Add(t);
                    break;
                }
                baseType = baseType.BaseType;
            }
        }

        return dbContextTypes;
    }

    public Dictionary<string, TableDefinition> ExtractModelFromAnyDbContext(
        Type dbContextType, 
        string connectionString, 
        string? explicitSourcePath = null, 
        string? assemblyPath = null)
    {
        // Caso A: Si es EF Core DbContext
        if (typeof(Microsoft.EntityFrameworkCore.DbContext).IsAssignableFrom(dbContextType))
        {
            var efCoreContext = CreateEfCoreDbContext(dbContextType, connectionString);
            using (efCoreContext)
            {
                var efCoreModel = ExtractFromEfCore(efCoreContext);
                var fluent = _dataContextParser.ExtractConfigurations(dbContextType, assemblyPath, explicitSourcePath);
                ApplyFluentConfigurations(efCoreModel, fluent);
                return efCoreModel;
            }
        }

        // Caso B: Si es EF6 (System.Data.Entity.DbContext) o POCOs con DbSet<T>
        return ExtractFromEf6OrPoco(dbContextType, explicitSourcePath, assemblyPath);
    }

    private Microsoft.EntityFrameworkCore.DbContext CreateEfCoreDbContext(Type dbContextType, string connectionString)
    {
        var optionsBuilderType = typeof(DbContextOptionsBuilder<>).MakeGenericType(dbContextType);
        var optionsBuilder = (DbContextOptionsBuilder)Activator.CreateInstance(optionsBuilderType)!;
        optionsBuilder.UseSqlServer(connectionString);

        try
        {
            if (Activator.CreateInstance(dbContextType, optionsBuilder.Options) is Microsoft.EntityFrameworkCore.DbContext inst)
                return inst;
        }
        catch { }

        try
        {
            if (Activator.CreateInstance(dbContextType) is Microsoft.EntityFrameworkCore.DbContext inst)
                return inst;
        }
        catch { }

        throw new InvalidOperationException($"No se pudo instanciar el DbContext '{dbContextType.Name}'.");
    }

    private Dictionary<string, TableDefinition> ExtractFromEfCore(Microsoft.EntityFrameworkCore.DbContext context)
    {
        var result = new Dictionary<string, TableDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (string.IsNullOrEmpty(tableName)) continue;

            var schema = entityType.GetSchema() ?? "dbo";
            var tableKey = $"{schema}.{tableName}";

            if (!result.TryGetValue(tableKey, out var table))
            {
                var pkCols = entityType.FindPrimaryKey()?.Properties
                    .Select(p => p.GetColumnName(StoreObjectIdentifier.Table(tableName, schema)) ?? p.Name)
                    .ToList() ?? [];

                table = new TableDefinition
                {
                    Schema = schema,
                    Name = tableName,
                    PrimaryKey = pkCols.Count > 0 ? new PrimaryKeyConstraint($"PK_{tableName}", pkCols) : null
                };
                result[tableKey] = table;
            }

            var storeObject = StoreObjectIdentifier.Table(tableName, schema);

            foreach (var property in entityType.GetProperties())
            {
                var colName = property.GetColumnName(storeObject);
                if (string.IsNullOrEmpty(colName)) continue;

                var storeType = property.GetRelationalTypeMapping().StoreType.ToLowerInvariant();
                var isNullable = property.IsColumnNullable(storeObject);
                var isPk = property.IsPrimaryKey();
                var isExplicitPrecision = property.GetPrecision().HasValue || property.GetColumnType() != null;
                var isExplicitMaxLength = property.GetMaxLength().HasValue;

                table.Columns[colName] = new ColumnDefinition
                {
                    TableSchema = schema,
                    TableName = tableName,
                    ColumnName = colName,
                    StoreType = storeType,
                    IsNullable = isNullable,
                    IsPrimaryKey = isPk,
                    IsExplicitPrecision = isExplicitPrecision,
                    IsExplicitMaxLength = isExplicitMaxLength
                };
            }
        }

        return result;
    }

    private static void ApplyFluentConfigurations(
        Dictionary<string, TableDefinition> tables, 
        Dictionary<string, EntityFluentConfig> fluentConfigs)
    {
        if (fluentConfigs.Count == 0) return;

        foreach (var table in tables.Values)
        {
            foreach (var (entityName, entityCfg) in fluentConfigs)
            {
                if (!string.Equals(table.Name, entityCfg.TableName ?? entityName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(table.Name, entityName + "s", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var (propName, propCfg) in entityCfg.Properties)
                {
                    var targetColName = propCfg.ColumnName ?? propName;
                    if (table.Columns.TryGetValue(targetColName, out var col) || table.Columns.TryGetValue(propName, out col))
                    {
                        if (propCfg.Precision.HasValue)
                        {
                            table.Columns[col.ColumnName] = col with 
                            { 
                                StoreType = $"decimal({propCfg.Precision.Value},{propCfg.Scale ?? 0})",
                                IsExplicitPrecision = true 
                            };
                        }
                        else if (propCfg.MaxLength.HasValue)
                        {
                            table.Columns[col.ColumnName] = col with 
                            { 
                                StoreType = $"nvarchar({propCfg.MaxLength.Value})",
                                IsExplicitMaxLength = true 
                            };
                        }
                        else if (!string.IsNullOrWhiteSpace(propCfg.ColumnType))
                        {
                            table.Columns[col.ColumnName] = col with
                            {
                                StoreType = propCfg.ColumnType.ToLowerInvariant(),
                                IsExplicitPrecision = true
                            };
                        }
                    }
                }
            }
        }
    }

    private Dictionary<string, TableDefinition> ExtractFromEf6OrPoco(
        Type dbContextType, 
        string? explicitSourcePath = null, 
        string? assemblyPath = null)
    {
        var result = new Dictionary<string, TableDefinition>(StringComparer.OrdinalIgnoreCase);

        // Extraer configuraciones Fluent API desde el archivo fuente C# o el IL de OnModelCreating
        var fluentConfigs = _dataContextParser.ExtractConfigurations(
            dbContextType, 
            assemblyPath ?? dbContextType.Assembly.Location, 
            explicitSourcePath);

        // Inspeccionar todas las propiedades DbSet<T> o IDbSet<T>
        var dbSetProperties = dbContextType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType && 
                       (p.PropertyType.Name.StartsWith("DbSet") || p.PropertyType.Name.StartsWith("IDbSet")));

        // EF6 pluraliza el nombre de la CLASE de la entidad (no el de la propiedad DbSet).
        var pluralize = CreateEf6Pluralizer(dbContextType);
        var processedEntities = new HashSet<Type>();

        foreach (var dbSetProp in dbSetProperties)
        {
            var entityType = dbSetProp.PropertyType.GetGenericArguments()[0];
            if (!processedEntities.Add(entityType)) continue; // misma entidad expuesta en varios DbSet
            
            fluentConfigs.TryGetValue(entityType.Name, out var entityFluent);

            // Resolver Nombre de Tabla y Esquema (Fluent API tiene precedencia)
            var tableAttr = entityType.GetCustomAttribute<TableAttribute>();
            var tableName = entityFluent?.TableName ?? tableAttr?.Name ?? pluralize(entityType.Name);
            var schema = entityFluent?.Schema ?? tableAttr?.Schema ?? "dbo";
            var tableKey = $"{schema}.{tableName}";

            if (!result.TryGetValue(tableKey, out var table))
            {
                table = new TableDefinition
                {
                    Schema = schema,
                    Name = tableName
                };
                result[tableKey] = table;
            }

            // Inspeccionar Propiedades Escalares
            PropertyInfo[] props;
            try
            {
                props = entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            }
            catch
            {
                continue;
            }

            foreach (var prop in props)
            {
                try
                {
                    if (prop.GetCustomAttribute<NotMappedAttribute>() != null) continue;
                    if (!IsScalarType(prop.PropertyType)) continue;

                    PropertyFluentConfig? propFluent = null;
                    entityFluent?.Properties.TryGetValue(prop.Name, out propFluent);

                    var colAttr = prop.GetCustomAttribute<ColumnAttribute>();
                    var colName = propFluent?.ColumnName ?? colAttr?.Name ?? prop.Name;
                    
                    // Convención de EF6: [Key], "Id" o "<Clase>Id" (sin distinguir mayúsculas)
                    var isPk = prop.GetCustomAttribute<KeyAttribute>() != null || 
                               prop.Name.Equals("Id", StringComparison.OrdinalIgnoreCase) ||
                               prop.Name.Equals($"{entityType.Name}Id", StringComparison.OrdinalIgnoreCase);

                    bool isNullable;
                    if (propFluent?.IsRequired.HasValue == true)
                    {
                        isNullable = !propFluent.IsRequired.Value;
                    }
                    else
                    {
                        isNullable = !isPk && IsNullableType(prop.PropertyType) && prop.GetCustomAttribute<RequiredAttribute>() == null;
                    }

                    string storeType;
                    bool isExplicitPrecision = false;
                    bool isExplicitMaxLength = false;

                    if (propFluent?.Precision.HasValue == true)
                    {
                        storeType = $"decimal({propFluent.Precision.Value},{propFluent.Scale ?? 0})";
                        isExplicitPrecision = true;
                    }
                    else if (propFluent?.MaxLength.HasValue == true)
                    {
                        storeType = $"nvarchar({propFluent.MaxLength.Value})";
                        isExplicitMaxLength = true;
                    }
                    else if (!string.IsNullOrWhiteSpace(propFluent?.ColumnType))
                    {
                        storeType = propFluent.ColumnType.ToLowerInvariant();
                        isExplicitPrecision = true;
                    }
                    else
                    {
                        storeType = ResolveStoreTypeFromProperty(prop, colAttr?.TypeName);
                        isExplicitPrecision = !string.IsNullOrWhiteSpace(colAttr?.TypeName);
                        isExplicitMaxLength = prop.GetCustomAttribute<StringLengthAttribute>() != null ||
                                              prop.GetCustomAttribute<MaxLengthAttribute>() != null;
                    }

                    table.Columns[colName] = new ColumnDefinition
                    {
                        TableSchema = schema,
                        TableName = tableName,
                        ColumnName = colName,
                        StoreType = storeType,
                        IsNullable = isNullable,
                        IsPrimaryKey = isPk,
                        IsExplicitPrecision = isExplicitPrecision,
                        IsExplicitMaxLength = isExplicitMaxLength
                    };
                }
                catch
                {
                    // Saltar propiedades complejas no mapeables
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Devuelve la función de pluralización que usa EF6 para nombrar tablas. Se toma el
    /// EnglishPluralizationService de la propia EntityFramework.dll del proyecto auditado
    /// (así coincide exactamente, p. ej. Configuracion -> Configuracions). Si no se puede
    /// obtener, se usa una aproximación con las reglas básicas del inglés.
    /// </summary>
    private static Func<string, string> CreateEf6Pluralizer(Type dbContextType)
    {
        try
        {
            var baseType = dbContextType.BaseType;
            while (baseType != null && baseType.Name != "DbContext") baseType = baseType.BaseType;

            var svcType = baseType?.Assembly.GetType("System.Data.Entity.Infrastructure.Pluralization.EnglishPluralizationService");
            if (svcType != null)
            {
                var svc = Activator.CreateInstance(svcType, nonPublic: true);
                var method = svcType.GetMethod("Pluralize", new[] { typeof(string) });
                if (svc != null && method != null)
                    return word => (string)method.Invoke(svc, new object[] { word })!;
            }
        }
        catch
        {
            // se usa el fallback
        }

        return word =>
        {
            var lower = word.ToLowerInvariant();
            if (lower.EndsWith('s') || lower.EndsWith('x') || lower.EndsWith('z') || lower.EndsWith("ch") || lower.EndsWith("sh"))
                return word + "es";
            if (lower.Length > 1 && lower.EndsWith('y') && "aeiou".IndexOf(lower[^2]) < 0)
                return word[..^1] + "ies";
            return word + "s";
        };
    }

    private static bool IsScalarType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying.IsPrimitive || underlying.IsEnum) return true;
        return underlying == typeof(string) ||
               underlying == typeof(decimal) ||
               underlying == typeof(DateTime) ||
               underlying == typeof(DateTimeOffset) ||
               underlying == typeof(TimeSpan) ||
               underlying == typeof(Guid) ||
               underlying == typeof(byte[]);
    }

    private static bool IsNullableType(Type type)
    {
        if (!type.IsValueType) return true;
        return Nullable.GetUnderlyingType(type) != null;
    }

    private static string ResolveStoreTypeFromProperty(PropertyInfo prop, string? explicitTypeName)
    {
        if (!string.IsNullOrWhiteSpace(explicitTypeName))
        {
            var explicitType = explicitTypeName.Trim().ToLowerInvariant();
            // En EF6 un 'decimal' sin precisión usa (18,2); en SQL Server puro sería (18,0).
            return explicitType is "decimal" or "numeric" ? "decimal(18,2)" : explicitType;
        }

        var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

        if (type == typeof(string))
        {
            var strLen = prop.GetCustomAttribute<StringLengthAttribute>()?.MaximumLength ??
                         prop.GetCustomAttribute<MaxLengthAttribute>()?.Length;
            return strLen.HasValue ? $"nvarchar({strLen.Value})" : "nvarchar(max)";
        }

        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "bigint";
        if (type == typeof(short)) return "smallint";
        if (type == typeof(byte)) return "tinyint";
        if (type == typeof(bool)) return "bit";
        if (type == typeof(decimal)) return "decimal(18,2)";
        if (type == typeof(double)) return "float";
        if (type == typeof(float)) return "real";
        if (type == typeof(DateTime)) return "datetime";
        if (type == typeof(DateTimeOffset)) return "datetimeoffset";
        if (type == typeof(TimeSpan)) return "time";
        if (type == typeof(Guid)) return "uniqueidentifier";
        if (type == typeof(byte[])) return "varbinary(max)";

        return "nvarchar(max)";
    }
}
