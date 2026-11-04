using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace DbSchemaSyncAuditor.Services;

public class PropertyFluentConfig
{
    public string EntityName { get; set; } = string.Empty;
    public string PropertyName { get; set; } = string.Empty;
    public int? Precision { get; set; }
    public int? Scale { get; set; }
    public int? MaxLength { get; set; }
    public string? ColumnType { get; set; }
    public string? ColumnName { get; set; }
    public bool? IsRequired { get; set; }
}

public class EntityFluentConfig
{
    public string EntityName { get; set; } = string.Empty;
    public string? TableName { get; set; }
    public string? Schema { get; set; }
    public Dictionary<string, PropertyFluentConfig> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public class DataContextModelParser
{
    /// <summary>
    /// Intenta localizar automáticamente el archivo .cs del DbContext buscando en la carpeta
    /// del proyecto de la DLL (hacia arriba hasta 4 niveles y en sus subcarpetas).
    /// </summary>
    public string? FindDataContextSourceFile(string assemblyPath, string dbContextName)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath)) return null;

        try
        {
            var dir = Path.GetDirectoryName(assemblyPath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;

            // Subir desde bin\Debug o similar hasta la raíz del proyecto
            var current = new DirectoryInfo(dir);
            for (int i = 0; i < 5 && current != null; i++)
            {
                // Buscar coincidencia exacta por nombre de clase (ej. DashboardContext.cs)
                var exactCandidate = Path.Combine(current.FullName, $"{dbContextName}.cs");
                if (File.Exists(exactCandidate)) return exactCandidate;

                // Buscar dentro de subcarpetas comunes (Models, Data, etc.) excluyendo bin/obj/.vs/.git
                try
                {
                    var found = Directory.EnumerateFiles(current.FullName, $"{dbContextName}.cs", SearchOption.AllDirectories)
                        .FirstOrDefault(f => !f.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase) &&
                                             !f.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) &&
                                             !f.Contains("\\.vs\\", StringComparison.OrdinalIgnoreCase) &&
                                             !f.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase));
                    if (found != null) return found;
                }
                catch { }

                current = current.Parent;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Extrae las configuraciones Fluent API (HasPrecision, ToTable, etc.) ya sea leyendo el
    /// archivo fuente C# (.cs) o inspeccionando el bytecode IL del método OnModelCreating.
    /// </summary>
    public Dictionary<string, EntityFluentConfig> ExtractConfigurations(
        Type dbContextType, 
        string? assemblyPath = null, 
        string? explicitSourcePath = null)
    {
        var result = new Dictionary<string, EntityFluentConfig>(StringComparer.OrdinalIgnoreCase);

        // 1. Intentar leer desde archivo fuente C# (.cs)
        string? sourceFileToRead = null;
        if (!string.IsNullOrWhiteSpace(explicitSourcePath) && File.Exists(explicitSourcePath))
        {
            sourceFileToRead = explicitSourcePath;
        }
        else if (!string.IsNullOrWhiteSpace(assemblyPath))
        {
            sourceFileToRead = FindDataContextSourceFile(assemblyPath, dbContextType.Name);
        }

        if (sourceFileToRead != null && File.Exists(sourceFileToRead))
        {
            try
            {
                var code = File.ReadAllText(sourceFileToRead);
                var fromSource = ParseFromSource(code);
                foreach (var (k, v) in fromSource)
                {
                    result[k] = v;
                }
            }
            catch { }
        }

        // 2. Complementar o usar como fallback la inspección IL del método OnModelCreating
        try
        {
            var fromIl = ParseFromIl(dbContextType);
            foreach (var (entityName, ilEntityCfg) in fromIl)
            {
                if (!result.TryGetValue(entityName, out var existingEntity))
                {
                    result[entityName] = ilEntityCfg;
                    continue;
                }

                foreach (var (propName, ilPropCfg) in ilEntityCfg.Properties)
                {
                    if (!existingEntity.Properties.TryGetValue(propName, out var existingProp))
                    {
                        existingEntity.Properties[propName] = ilPropCfg;
                    }
                    else
                    {
                        // Si el archivo fuente no tenía precisión pero IL sí, combinar
                        if (!existingProp.Precision.HasValue && ilPropCfg.Precision.HasValue)
                        {
                            existingProp.Precision = ilPropCfg.Precision;
                            existingProp.Scale = ilPropCfg.Scale;
                        }
                    }
                }
            }
        }
        catch { }

        return result;
    }

    /// <summary>
    /// Parsea el código fuente de un DbContext para extraer llamadas de Fluent API.
    /// Soporta tanto sintaxis EF6 encadenada como sintaxis lambda de EF Core.
    /// </summary>
    public Dictionary<string, EntityFluentConfig> ParseFromSource(string code)
    {
        var result = new Dictionary<string, EntityFluentConfig>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(code)) return result;

        // Limpiar comentarios para evitar falsos positivos
        var cleanCode = Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline);
        cleanCode = Regex.Replace(cleanCode, @"//.*$", "", RegexOptions.Multiline);

        // Patrón 1: modelBuilder.Entity<Entidad>()... terminando en ';' (EF6 y EF Core)
        var statementRegex = new Regex(
            @"modelBuilder\s*\.\s*Entity\s*<\s*(?<entity>[\w\.]+)\s*>\s*\(\s*\)(?<chain>(?:(?!\bmodelBuilder\b)[^;])*)", 
            RegexOptions.Singleline);

        foreach (Match m in statementRegex.Matches(cleanCode))
        {
            var rawEntity = m.Groups["entity"].Value.Trim();
            var entityName = rawEntity.Contains('.') ? rawEntity[(rawEntity.LastIndexOf('.') + 1)..] : rawEntity;
            var chain = m.Groups["chain"].Value;

            if (!result.TryGetValue(entityName, out var entityCfg))
            {
                entityCfg = new EntityFluentConfig { EntityName = entityName };
                result[entityName] = entityCfg;
            }

            // ToTable("Nombre", "Esquema")
            var toTableMatch = Regex.Match(chain, @"\.ToTable\s*\(\s*[""'](?<tbl>[^""']+)[""'](?:\s*,\s*[""'](?<sch>[^""']+)[""'])?\s*\)");
            if (toTableMatch.Success)
            {
                entityCfg.TableName = toTableMatch.Groups["tbl"].Value;
                if (toTableMatch.Groups["sch"].Success)
                    entityCfg.Schema = toTableMatch.Groups["sch"].Value;
            }

            // .Property(...)
            var propMatches = Regex.Matches(
                chain, 
                @"\.Property\s*\(\s*(?:\w+\s*=>\s*\w+\.(?<prop>\w+)|[""'](?<propStr>\w+)[""'])\s*\)(?<propChain>(?:(?!\.Property\b)[^;])*)", 
                RegexOptions.Singleline);

            foreach (Match pm in propMatches)
            {
                var propName = pm.Groups["prop"].Success ? pm.Groups["prop"].Value : pm.Groups["propStr"].Value;
                var propChain = pm.Groups["propChain"].Value;

                if (!entityCfg.Properties.TryGetValue(propName, out var pcfg))
                {
                    pcfg = new PropertyFluentConfig { EntityName = entityName, PropertyName = propName };
                    entityCfg.Properties[propName] = pcfg;
                }

                // HasPrecision(p, s)
                var precMatch = Regex.Match(propChain, @"\.HasPrecision\s*\(\s*(?<p>\d+)\s*,\s*(?<s>\d+)\s*\)");
                if (precMatch.Success)
                {
                    pcfg.Precision = int.Parse(precMatch.Groups["p"].Value);
                    pcfg.Scale = int.Parse(precMatch.Groups["s"].Value);
                }

                // HasMaxLength(len)
                var maxLenMatch = Regex.Match(propChain, @"\.HasMaxLength\s*\(\s*(?<len>\d+)\s*\)");
                if (maxLenMatch.Success)
                {
                    pcfg.MaxLength = int.Parse(maxLenMatch.Groups["len"].Value);
                }

                // HasColumnType("type")
                var colTypeMatch = Regex.Match(propChain, @"\.HasColumnType\s*\(\s*[""'](?<type>[^""']+)[""']\s*\)");
                if (colTypeMatch.Success)
                {
                    pcfg.ColumnType = colTypeMatch.Groups["type"].Value;
                }

                // HasColumnName("name")
                var colNameMatch = Regex.Match(propChain, @"\.HasColumnName\s*\(\s*[""'](?<name>[^""']+)[""']\s*\)");
                if (colNameMatch.Success)
                {
                    pcfg.ColumnName = colNameMatch.Groups["name"].Value;
                }

                // IsRequired / IsOptional
                if (Regex.IsMatch(propChain, @"\.IsRequired\s*\(\s*\)")) pcfg.IsRequired = true;
                if (Regex.IsMatch(propChain, @"\.IsOptional\s*\(\s*\)")) pcfg.IsRequired = false;
            }
        }

        // Patrón 2: Bloque lambda de EF Core: modelBuilder.Entity<Entidad>(entity => { ... });
        var lambdaBlockRegex = new Regex(
            @"modelBuilder\s*\.\s*Entity\s*<\s*(?<entity>[\w\.]+)\s*>\s*\(\s*(?<param>\w+)\s*=>\s*\{(?<body>[^{}]*(?:\{[^{}]*\}[^{}]*)*)\}\s*\)", 
            RegexOptions.Singleline);

        foreach (Match m in lambdaBlockRegex.Matches(cleanCode))
        {
            var rawEntity = m.Groups["entity"].Value.Trim();
            var entityName = rawEntity.Contains('.') ? rawEntity[(rawEntity.LastIndexOf('.') + 1)..] : rawEntity;
            var param = m.Groups["param"].Value;
            var body = m.Groups["body"].Value;

            if (!result.TryGetValue(entityName, out var entityCfg))
            {
                entityCfg = new EntityFluentConfig { EntityName = entityName };
                result[entityName] = entityCfg;
            }

            var toTableMatch = Regex.Match(body, $@"{param}\s*\.\s*ToTable\s*\(\s*[""'](?<tbl>[^""']+)[""'](?:\s*,\s*[""'](?<sch>[^""']+)[""'])?\s*\)");
            if (toTableMatch.Success)
            {
                entityCfg.TableName = toTableMatch.Groups["tbl"].Value;
                if (toTableMatch.Groups["sch"].Success)
                    entityCfg.Schema = toTableMatch.Groups["sch"].Value;
            }

            var propMatches = Regex.Matches(
                body, 
                $@"{param}\s*\.\s*Property\s*\(\s*(?:\w+\s*=>\s*\w+\.(?<prop>\w+)|[""'](?<propStr>\w+)[""'])\s*\)(?<propChain>(?:(?!\.Property\b)[^;])*)", 
                RegexOptions.Singleline);

            foreach (Match pm in propMatches)
            {
                var propName = pm.Groups["prop"].Success ? pm.Groups["prop"].Value : pm.Groups["propStr"].Value;
                var propChain = pm.Groups["propChain"].Value;

                if (!entityCfg.Properties.TryGetValue(propName, out var pcfg))
                {
                    pcfg = new PropertyFluentConfig { EntityName = entityName, PropertyName = propName };
                    entityCfg.Properties[propName] = pcfg;
                }

                var precMatch = Regex.Match(propChain, @"\.HasPrecision\s*\(\s*(?<p>\d+)\s*,\s*(?<s>\d+)\s*\)");
                if (precMatch.Success)
                {
                    pcfg.Precision = int.Parse(precMatch.Groups["p"].Value);
                    pcfg.Scale = int.Parse(precMatch.Groups["s"].Value);
                }

                var maxLenMatch = Regex.Match(propChain, @"\.HasMaxLength\s*\(\s*(?<len>\d+)\s*\)");
                if (maxLenMatch.Success)
                {
                    pcfg.MaxLength = int.Parse(maxLenMatch.Groups["len"].Value);
                }

                var colTypeMatch = Regex.Match(propChain, @"\.HasColumnType\s*\(\s*[""'](?<type>[^""']+)[""']\s*\)");
                if (colTypeMatch.Success)
                {
                    pcfg.ColumnType = colTypeMatch.Groups["type"].Value;
                }

                var colNameMatch = Regex.Match(propChain, @"\.HasColumnName\s*\(\s*[""'](?<name>[^""']+)[""']\s*\)");
                if (colNameMatch.Success)
                {
                    pcfg.ColumnName = colNameMatch.Groups["name"].Value;
                }

                if (Regex.IsMatch(propChain, @"\.IsRequired\s*\(\s*\)")) pcfg.IsRequired = true;
                if (Regex.IsMatch(propChain, @"\.IsOptional\s*\(\s*\)")) pcfg.IsRequired = false;
            }
        }

        return result;
    }

    /// <summary>
    /// Desensambla el método OnModelCreating a nivel de instrucciones IL de la DLL para
    /// capturar configuraciones HasPrecision y Property sin depender de librerías externas.
    /// </summary>
    public Dictionary<string, EntityFluentConfig> ParseFromIl(Type dbContextType)
    {
        var result = new Dictionary<string, EntityFluentConfig>(StringComparer.OrdinalIgnoreCase);

        var method = dbContextType.GetMethod(
            "OnModelCreating", 
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        if (method == null) return result;

        var body = method.GetMethodBody();
        if (body == null) return result;

        var il = body.GetILAsByteArray();
        if (il == null || il.Length == 0) return result;

        var module = method.Module;
        var intStack = new Stack<int>();
        string? currentEntity = null;
        string? currentProp = null;

        int i = 0;
        while (i < il.Length)
        {
            byte op = il[i++];

            // ldc.i4.0 .. ldc.i4.8 (0x16 - 0x1E)
            if (op >= 0x16 && op <= 0x1E)
            {
                intStack.Push(op - 0x16);
                continue;
            }
            // ldc.i4.s (0x1F)
            if (op == 0x1F && i < il.Length)
            {
                sbyte val = (sbyte)il[i++];
                intStack.Push(val);
                continue;
            }
            // ldc.i4 (0x20)
            if (op == 0x20 && i + 3 < il.Length)
            {
                int val = BitConverter.ToInt32(il, i);
                i += 4;
                intStack.Push(val);
                continue;
            }

            // ldtoken (0xD0) -> MemberInfo para expresiones lambda (ej. p => p.Cantidad)
            if (op == 0xD0 && i + 3 < il.Length)
            {
                int token = BitConverter.ToInt32(il, i);
                i += 4;
                try
                {
                    var member = module.ResolveMember(token);
                    if (member is MethodInfo mi && mi.Name.StartsWith("get_"))
                    {
                        currentProp = mi.Name[4..];
                    }
                    else if (member is PropertyInfo pi)
                    {
                        currentProp = pi.Name;
                    }
                }
                catch { }
                continue;
            }

            // call (0x28) o callvirt (0x6F)
            if ((op == 0x28 || op == 0x6F) && i + 3 < il.Length)
            {
                int token = BitConverter.ToInt32(il, i);
                i += 4;
                MethodBase? calledMethod = null;
                try
                {
                    calledMethod = module.ResolveMethod(token);
                }
                catch { }

                if (calledMethod != null)
                {
                    var mName = calledMethod.Name;

                    // Entity<T>()
                    if (mName == "Entity" && calledMethod.IsGenericMethod)
                    {
                        var genArg = calledMethod.GetGenericArguments()[0];
                        currentEntity = genArg.Name;
                        currentProp = null;
                        if (!result.ContainsKey(currentEntity))
                        {
                            result[currentEntity] = new EntityFluentConfig { EntityName = currentEntity };
                        }
                    }
                    // HasPrecision(byte precision, byte scale)
                    else if (mName == "HasPrecision" && currentEntity != null && currentProp != null)
                    {
                        if (intStack.Count >= 2)
                        {
                            int scale = intStack.Pop();
                            int prec = intStack.Pop();

                            if (!result.TryGetValue(currentEntity, out var ecfg))
                            {
                                ecfg = new EntityFluentConfig { EntityName = currentEntity };
                                result[currentEntity] = ecfg;
                            }

                            if (!ecfg.Properties.TryGetValue(currentProp, out var pcfg))
                            {
                                pcfg = new PropertyFluentConfig { EntityName = currentEntity, PropertyName = currentProp };
                                ecfg.Properties[currentProp] = pcfg;
                            }

                            pcfg.Precision = prec;
                            pcfg.Scale = scale;
                        }
                    }
                }
                continue;
            }
        }

        return result;
    }
}

