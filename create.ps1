$ErrorActionPreference = "Stop"

Write-Host "==> Initializing Fully Editable Odoo .NET 10 Enterprise Suite..." -ForegroundColor Cyan

# Clean previous solution if present
if (Test-Path "OdooModularEnterpriseSuite.sln") { Remove-Item "OdooModularEnterpriseSuite.sln" -Force }

New-Item -ItemType Directory -Force -Path `
    "Core.OdooEngine", `
    "HostApp/wwwroot", `
    "ProductAddon/views", `
    "InventoryAddon/views", `
    "PurchaseAddon/views", `
    "SalesAddon/views", `
    "SalesAddon/wwwroot", `
    "InvoicingAddon/views", `
    "AccountingAddon/views" | Out-Null

dotnet new sln -n OdooModularEnterpriseSuite --force

# -------------------------------------------------------------
# 1. Core.OdooEngine Project (.NET 10)
# -------------------------------------------------------------
Write-Host "==> Generating Core.OdooEngine..." -ForegroundColor Yellow
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Npgsql" Version="9.0.2" />
    <PackageReference Include="Microsoft.Data.SqlClient" Version="5.2.2" />
    <PackageReference Include="MySqlConnector" Version="2.3.7" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.2" />
    <PackageReference Include="Dapper" Version="2.1.35" />
  </ItemGroup>
</Project>
'@ | Set-Content -Path "Core.OdooEngine/Core.OdooEngine.csproj"

@'
using System.Data;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.XPath;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Npgsql;

namespace Core.OdooEngine;

[AttributeUsage(AttributeTargets.Method)]
public class ApiDependsAttribute(params string[] fields) : Attribute
{
    public string[] Fields { get; } = fields;
}

[AttributeUsage(AttributeTargets.Method)]
public class ApiOnchangeAttribute(params string[] fields) : Attribute
{
    public string[] Fields { get; } = fields;
}

[AttributeUsage(AttributeTargets.Method)]
public class ApiConstrainsAttribute(params string[] fields) : Attribute
{
    public string[] Fields { get; } = fields;
}

public class ValidationError(string message) : Exception(message);

public enum DatabaseProvider { InMemory, PostgreSql, SqlServer, MySql, Sqlite }

public class AppSettingsConfig
{
    public List<string> AddonsPath { get; set; } = new() { "addons" };
    public string DbType { get; set; } = "InMemory";
    public string ConnectionString { get; set; } = "";
    public int Port { get; set; } = 5000;

    public static AppSettingsConfig LoadFromJsFile(string jsFilePath, ILogger? logger = null)
    {
        var config = new AppSettingsConfig();
        if (!File.Exists(jsFilePath)) return config;

        try
        {
            var rawContent = File.ReadAllText(jsFilePath);
            var cleanJs = Regex.Replace(rawContent, @"/\*.*?\*/", "", RegexOptions.Singleline);
            cleanJs = Regex.Replace(cleanJs, @"//.*", "");

            var match = Regex.Match(cleanJs, @"(?:module\.exports\s*=|export\s+default)\s*(\{[\s\S]*\})");
            if (match.Success)
            {
                var jsonLike = match.Groups[1].Value.Trim().TrimEnd(';');
                var validJson = Regex.Replace(jsonLike, @"(\w+)\s*:", "\"$1\":");
                validJson = Regex.Replace(validJson, @"'([^']*)'", "\"$1\"");

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var parsed = JsonSerializer.Deserialize<AppSettingsConfig>(validJson, options);
                if (parsed != null) return parsed;
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to parse appsettings.js.");
        }
        return config;
    }

    public List<string> ResolveAddonsAbsolutePaths(string baseDirectory)
    {
        return AddonsPath
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(baseDirectory, p)))
            .Distinct()
            .ToList();
    }
}

public class OdooManifest
{
    public string TechnicalName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Category { get; set; } = "Uncategorized";
    public string Summary { get; set; } = string.Empty;
    public List<string> Depends { get; set; } = new();
    public List<string> Data { get; set; } = new();
    public bool Application { get; set; } = false;
    public bool Installable { get; set; } = true;
    public string? AssemblyFile { get; set; }
    public string State { get; set; } = "uninstalled";
    public string FolderPath { get; set; } = string.Empty;
}

public enum FieldType { Char, Integer, Float, Boolean, Selection, Many2one, One2many, DateTime, Text }
public record SelectionOption(string Value, string Label);

public record FieldDef(
    string Name,
    FieldType Type,
    string String,
    object? DefaultValue = null,
    string? Relation = null,
    string? InverseName = null,
    List<SelectionOption>? Selection = null,
    bool Readonly = false,
    bool Required = false,
    string? Compute = null,
    string? Module = "base"
);

public record MailMessage(int Id, string Model, int RecordId, string Author, string Body, string Date, string Type);
public record ModelDataEntry(string Module, string Model, string Name, string ResId, string Type);

public abstract class OdooModel
{
    public abstract string Name { get; }
    public virtual string Inherit => string.Empty;
    public Dictionary<string, FieldDef> Fields { get; } = new();

    public void AddField(
        string name,
        FieldType type,
        string label,
        object? defaultValue = null,
        string? relation = null,
        string? inverseName = null,
        List<SelectionOption>? selection = null,
        bool readonlyField = false,
        bool required = false,
        string? compute = null,
        string? module = "base")
    {
        Fields[name] = new FieldDef(name, type, label, defaultValue, relation, inverseName, selection, readonlyField, required, compute, module);
    }

    public virtual Dictionary<string, object> EvaluateOnchange(string fieldName, Dictionary<string, object> currentValues, ModelRegistry registry, ILogger logger)
    {
        var mutatedValues = new Dictionary<string, object>();
        foreach (var method in this.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = method.GetCustomAttribute<ApiOnchangeAttribute>();
            if (attr != null && attr.Fields.Contains(fieldName, StringComparer.OrdinalIgnoreCase))
            {
                var parameters = method.GetParameters();
                object? res = parameters.Length switch
                {
                    2 => method.Invoke(this, [currentValues, registry]),
                    1 => method.Invoke(this, [currentValues]),
                    _ => method.Invoke(this, null)
                };
                if (res is Dictionary<string, object> dict)
                {
                    foreach (var (k, v) in dict) mutatedValues[k] = v;
                }
            }
        }
        return mutatedValues;
    }

    public virtual void ComputeFields(Dictionary<string, object> record, ModelRegistry registry, ILogger logger)
    {
        foreach (var method in this.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.GetCustomAttribute<ApiDependsAttribute>() != null)
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 2) method.Invoke(this, [record, registry]);
                else if (parameters.Length == 1) method.Invoke(this, [record]);
                else method.Invoke(this, null);
            }
        }

        foreach (var (fName, fDef) in Fields)
        {
            if (!string.IsNullOrEmpty(fDef.Compute))
            {
                var method = this.GetType().GetMethod(fDef.Compute, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (method != null && method.GetCustomAttribute<ApiDependsAttribute>() == null)
                {
                    method.Invoke(this, [record, registry]);
                }
            }
        }
    }

    public virtual void ValidateConstrains(Dictionary<string, object> record, ModelRegistry registry, ILogger logger)
    {
        foreach (var method in this.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.GetCustomAttribute<ApiConstrainsAttribute>() != null)
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 2) method.Invoke(this, [record, registry]);
                else if (parameters.Length == 1) method.Invoke(this, [record]);
                else method.Invoke(this, null);
            }
        }
    }

    public virtual object? ExecuteMethod(string methodName, int id, Dictionary<string, object> values, ModelRegistry registry, Func<object?>? next = null)
    {
        var method = this.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (method != null)
        {
            var parameters = method.GetParameters();
            return parameters.Length switch
            {
                3 => method.Invoke(this, [id, values, registry]),
                2 => method.Invoke(this, [id, registry]),
                1 => method.Invoke(this, [id]),
                _ => method.Invoke(this, null)
            };
        }
        return next != null ? next() : null;
    }
}

public class BasePartnerModel : OdooModel
{
    public override string Name => "res.partner";
    public BasePartnerModel()
    {
        AddField("name", FieldType.Char, "Name", required: true, module: "base");
        AddField("is_company", FieldType.Boolean, "Is a Company?", defaultValue: false, module: "base");
        AddField("vat", FieldType.Char, "Tax ID / VAT", module: "base");
        AddField("email", FieldType.Char, "Email", module: "base");
        AddField("phone", FieldType.Char, "Phone", module: "base");
    }
}

public record OdooMenuItem(
    string Id,
    string Name,
    string Icon,
    string ActionType,
    string? TargetModel = null,
    string? DefaultViewMode = "tree,kanban,form",
    string? ClientComponentUrl = null,
    string? ClientComponentExport = null,
    string Module = "base"
);

public interface IOdooAddon
{
    string TechnicalName { get; }
    void RegisterModels(ModelRegistry registry);
}

public class MenuRegistry
{
    private readonly List<OdooMenuItem> _menus = new();
    public void AddMenu(OdooMenuItem menu) => _menus.Add(menu);
    public void RemoveModuleMenus(string module) => _menus.RemoveAll(m => m.Module == module);
    public void Clear() => _menus.Clear();
    public IReadOnlyList<OdooMenuItem> GetMenus() => _menus;
}

public class DatabaseAdapter
{
    public DatabaseProvider Provider { get; }
    public string ConnectionString { get; }
    private readonly ILogger<DatabaseAdapter> _logger;

    public DatabaseAdapter(AppSettingsConfig config, ILogger<DatabaseAdapter> logger)
    {
        _logger = logger;
        Provider = Enum.TryParse<DatabaseProvider>(config.DbType, true, out var p) ? p : DatabaseProvider.InMemory;
        ConnectionString = config.ConnectionString;
        _logger.LogInformation("Database Adapter active: {Provider}", Provider);
    }

    public IDbConnection CreateConnection()
    {
        return Provider switch
        {
            DatabaseProvider.PostgreSql => new NpgsqlConnection(ConnectionString),
            DatabaseProvider.SqlServer => new SqlConnection(ConnectionString),
            DatabaseProvider.MySql => new MySqlConnection(ConnectionString),
            DatabaseProvider.Sqlite => new SqliteConnection(string.IsNullOrWhiteSpace(ConnectionString) ? "Data Source=odoodotnet.db" : ConnectionString),
            _ => null!
        };
    }

    public string Sanitize(string modelName) => modelName.Replace(".", "_").ToLower();

    public string Quote(string identifier)
    {
        return Provider switch
        {
            DatabaseProvider.PostgreSql or DatabaseProvider.Sqlite => $"\"{identifier}\"",
            DatabaseProvider.SqlServer => $"[{identifier}]",
            DatabaseProvider.MySql => $"`{identifier}`",
            _ => identifier
        };
    }

    public string MapSqlType(FieldType type)
    {
        return type switch
        {
            FieldType.Integer => Provider == DatabaseProvider.Sqlite ? "INTEGER" : "INT",
            FieldType.Float => Provider == DatabaseProvider.PostgreSql ? "DOUBLE PRECISION" : (Provider == DatabaseProvider.Sqlite ? "REAL" : "FLOAT"),
            FieldType.Boolean => Provider == DatabaseProvider.SqlServer ? "BIT" : (Provider == DatabaseProvider.MySql ? "TINYINT(1)" : "BOOLEAN"),
            FieldType.DateTime => Provider == DatabaseProvider.Sqlite ? "TEXT" : "TIMESTAMP",
            FieldType.Many2one => Provider == DatabaseProvider.Sqlite ? "INTEGER" : "INT",
            FieldType.Text => Provider == DatabaseProvider.SqlServer ? "NVARCHAR(MAX)" : "TEXT",
            _ => Provider == DatabaseProvider.Sqlite ? "TEXT" : "VARCHAR(255)"
        };
    }

    public void AutoSyncPhysicalDatabase(Dictionary<string, Dictionary<string, FieldDef>> desiredModels)
    {
        if (Provider == DatabaseProvider.InMemory) return;
        using var conn = CreateConnection();
        conn.Open();

        foreach (var (modelName, fields) in desiredModels)
        {
            var tbl = Sanitize(modelName);
            var createSql = Provider switch
            {
                DatabaseProvider.PostgreSql => $@"CREATE TABLE IF NOT EXISTS {Quote(tbl)} (id SERIAL PRIMARY KEY);",
                DatabaseProvider.SqlServer => $@"IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='{tbl}' and xtype='U') CREATE TABLE {Quote(tbl)} (id INT IDENTITY(1,1) PRIMARY KEY);",
                DatabaseProvider.MySql => $@"CREATE TABLE IF NOT EXISTS {Quote(tbl)} (id INT AUTO_INCREMENT PRIMARY KEY);",
                DatabaseProvider.Sqlite => $@"CREATE TABLE IF NOT EXISTS {Quote(tbl)} (id INTEGER PRIMARY KEY AUTOINCREMENT);",
                _ => ""
            };
            conn.Execute(createSql);

            foreach (var (fname, fdef) in fields)
            {
                if (fname == "id" || fdef.Type == FieldType.One2many) continue;
                var colType = MapSqlType(fdef.Type);
                var alterSql = Provider switch
                {
                    DatabaseProvider.PostgreSql => $"ALTER TABLE {Quote(tbl)} ADD COLUMN IF NOT EXISTS {Quote(fname)} {colType};",
                    DatabaseProvider.SqlServer => $"IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'{tbl}') AND name = '{fname}') ALTER TABLE {Quote(tbl)} ADD {Quote(fname)} {colType};",
                    DatabaseProvider.MySql => $"ALTER TABLE {Quote(tbl)} ADD COLUMN IF NOT EXISTS {Quote(fname)} {colType};",
                    DatabaseProvider.Sqlite => $"ALTER TABLE {Quote(tbl)} ADD COLUMN {Quote(fname)} {colType};",
                    _ => ""
                };
                try { conn.Execute(alterSql); } catch { }
            }
        }
    }

    public void DropTable(string modelName)
    {
        if (Provider == DatabaseProvider.InMemory) return;
        using var conn = CreateConnection();
        conn.Open();
        var tbl = Sanitize(modelName);
        try { conn.Execute($"DROP TABLE IF EXISTS {Quote(tbl)};"); } catch { }
    }

    public void DropColumn(string modelName, string columnName)
    {
        if (Provider == DatabaseProvider.InMemory) return;
        using var conn = CreateConnection();
        conn.Open();
        var tbl = Sanitize(modelName);
        try { conn.Execute($"ALTER TABLE {Quote(tbl)} DROP COLUMN {Quote(columnName)};"); } catch { }
    }
}

public class ModelRegistry
{
    private readonly Dictionary<string, List<OdooModel>> _registeredModelExtensions = new();
    private readonly Dictionary<string, Dictionary<string, FieldDef>> _activeSchema = new();
    private readonly Dictionary<string, List<Dictionary<string, object>>> _inMemoryDataStore = new();
    private readonly List<ModelDataEntry> _modelData = new();
    private readonly List<MailMessage> _mailMessages = new();
    private readonly DatabaseAdapter _db;
    private readonly ILogger<ModelRegistry> _logger;

    public ModelRegistry(DatabaseAdapter db, ILogger<ModelRegistry> logger)
    {
        _db = db;
        _logger = logger;
    }

    public void Register(OdooModel model)
    {
        var targetName = string.IsNullOrEmpty(model.Inherit) ? model.Name : model.Inherit;
        if (!_registeredModelExtensions.ContainsKey(targetName))
            _registeredModelExtensions[targetName] = new List<OdooModel>();
        _registeredModelExtensions[targetName].Add(model);
    }

    public void TrackData(string module, string model, string name, string resId, string type)
    {
        _modelData.Add(new ModelDataEntry(module, model, name, resId, type));
    }

    public void ClearModels() => _registeredModelExtensions.Clear();

    public void AutoSyncSchema()
    {
        var desiredSchema = new Dictionary<string, Dictionary<string, FieldDef>>();

        foreach (var (modelName, modelDefs) in _registeredModelExtensions)
        {
            var merged = new Dictionary<string, FieldDef>
            {
                ["id"] = new FieldDef("id", FieldType.Integer, "ID", Module: "base")
            };

            foreach (var def in modelDefs)
            {
                foreach (var (fName, fDef) in def.Fields) merged[fName] = fDef;
            }
            desiredSchema[modelName] = merged;
        }

        _activeSchema.Clear();
        foreach (var (m, f) in desiredSchema) _activeSchema[m] = new Dictionary<string, FieldDef>(f);
        _db.AutoSyncPhysicalDatabase(desiredSchema);
    }

    public void DropModuleSchemaAndData(string moduleName)
    {
        _logger.LogWarning("Dropping schema for module: {Module}", moduleName);

        var standaloneModelsToDrop = _registeredModelExtensions
            .Where(kvp => kvp.Value.All(m => m.Fields.Values.All(f => f.Module == moduleName)))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var model in standaloneModelsToDrop)
        {
            _inMemoryDataStore.Remove(model);
            _registeredModelExtensions.Remove(model);
            _activeSchema.Remove(model);
            _db.DropTable(model);
        }

        foreach (var (modelName, fields) in _activeSchema)
        {
            var colsToDrop = fields.Where(f => f.Value.Module == moduleName).Select(f => f.Key).ToList();
            foreach (var col in colsToDrop)
            {
                fields.Remove(col);
                if (_inMemoryDataStore.TryGetValue(modelName, out var records))
                {
                    foreach (var rec in records) rec.Remove(col);
                }
                _db.DropColumn(modelName, col);
            }
        }

        var recordsToPurge = _modelData.Where(d => d.Module == moduleName && d.Type == "record").ToList();
        foreach (var entry in recordsToPurge)
        {
            if (_db.Provider == DatabaseProvider.InMemory)
            {
                if (_inMemoryDataStore.TryGetValue(entry.Model, out var records))
                    records.RemoveAll(r => r.TryGetValue("id", out var idVal) && idVal.ToString() == entry.ResId);
            }
            else
            {
                using var conn = _db.CreateConnection();
                conn.Open();
                var tbl = _db.Sanitize(entry.Model);
                conn.Execute($"DELETE FROM {_db.Quote(tbl)} WHERE id = @id", new { id = int.Parse(entry.ResId) });
            }
        }
        _modelData.RemoveAll(d => d.Module == moduleName);
    }

    public Dictionary<string, FieldDef> GetFields(string model) =>
        _activeSchema.TryGetValue(model, out var fields) ? fields : new();

    public List<Dictionary<string, object>> SearchRead(string model, List<string>? fields = null, List<List<object>>? domain = null)
    {
        var modelFields = GetFields(model);
        List<Dictionary<string, object>> records;

        if (_db.Provider == DatabaseProvider.InMemory)
        {
            if (!_inMemoryDataStore.TryGetValue(model, out var memRecs)) return new();
            records = memRecs.Select(r => new Dictionary<string, object>(r)).ToList();
        }
        else
        {
            using var conn = _db.CreateConnection();
            conn.Open();
            var tbl = _db.Sanitize(model);
            var rows = conn.Query($"SELECT * FROM {_db.Quote(tbl)}");
            records = rows.Select(row => {
                var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in (IDictionary<string, object>)row) dict[prop.Key] = prop.Value;
                return dict;
            }).ToList();
        }

        var filtered = records.AsEnumerable();

        if (domain != null && domain.Count > 0)
        {
            foreach (var clause in domain)
            {
                if (clause.Count < 3) continue;
                var field = clause[0].ToString()!;
                var op = clause[1].ToString()!;
                var val = clause[2];

                filtered = filtered.Where(r =>
                {
                    if (!r.TryGetValue(field, out var rVal) || rVal == null) return false;
                    if (op == "=") return rVal.ToString()!.Equals(val?.ToString(), StringComparison.OrdinalIgnoreCase);
                    if (op == "!=") return !rVal.ToString()!.Equals(val?.ToString(), StringComparison.OrdinalIgnoreCase);
                    if (op == "ilike") return rVal.ToString()!.Contains(val?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
                    if (op == ">") return Convert.ToDouble(rVal) > Convert.ToDouble(val);
                    if (op == "<") return Convert.ToDouble(rVal) < Convert.ToDouble(val);
                    return true;
                });
            }
        }

        return filtered.Select(r =>
        {
            var projection = new Dictionary<string, object>();
            var targetFields = (fields == null || fields.Count == 0) ? modelFields.Keys.ToList() : fields;

            foreach (var f in targetFields)
            {
                if (r.TryGetValue(f, out var val))
                {
                    if (modelFields.TryGetValue(f, out var fdef))
                    {
                        if (fdef.Type == FieldType.Many2one && val != null)
                        {
                            var relId = Convert.ToInt32(val);
                            var relSearch = SearchRead(fdef.Relation!, ["id", "name"], [["id", "=", relId]]);
                            var relName = relSearch.Count > 0 && relSearch[0].TryGetValue("name", out var n) ? n.ToString() : $"#{relId}";
                            projection[f] = new object[] { relId, relName ?? "" };
                        }
                        else if (fdef.Type == FieldType.One2many)
                        {
                            var childLines = SearchRead(fdef.Relation!, null, [[fdef.InverseName!, "=", Convert.ToInt32(r["id"])]]);
                            projection[f] = childLines;
                        }
                        else projection[f] = val;
                    }
                    else projection[f] = val;
                }
                else projection[f] = null!;
            }
            if (r.TryGetValue("id", out var idVal)) projection["id"] = idVal;
            return projection;
        }).ToList();
    }

    public int Create(string model, Dictionary<string, object> values)
    {
        var modelFields = GetFields(model);
        var record = new Dictionary<string, object>(values);

        foreach (var (fname, fdef) in modelFields)
        {
            if (!record.ContainsKey(fname) && fdef.DefaultValue != null)
                record[fname] = fdef.DefaultValue;
            
            if (fdef.Type == FieldType.Many2one && record.TryGetValue(fname, out var val) && val != null)
            {
                if (val is JsonElement je && je.ValueKind == JsonValueKind.Array && je.GetArrayLength() > 0)
                {
                    record[fname] = je[0].GetInt32();
                }
                else if (val is object[] arr && arr.Length > 0)
                {
                    record[fname] = Convert.ToInt32(arr[0]);
                }
            }
        }

        if (_registeredModelExtensions.TryGetValue(model, out var extensions))
        {
            foreach (var ext in extensions)
            {
                ext.ComputeFields(record, this, _logger);
                ext.ValidateConstrains(record, this, _logger);
            }
        }

        int newId = 1;

        if (_db.Provider == DatabaseProvider.InMemory)
        {
            if (!_inMemoryDataStore.ContainsKey(model)) _inMemoryDataStore[model] = new();
            newId = _inMemoryDataStore[model].Count > 0 ? (int)_inMemoryDataStore[model].Max(r => Convert.ToInt32(r["id"])) + 1 : 1;
            record["id"] = newId;
            _inMemoryDataStore[model].Add(record);
        }
        else
        {
            using var conn = _db.CreateConnection();
            conn.Open();
            var tbl = _db.Sanitize(model);
            var cols = record.Keys.Where(k => k != "id" && modelFields.ContainsKey(k) && modelFields[k].Type != FieldType.One2many).ToList();
            var colNames = string.Join(", ", cols.Select(c => _db.Quote(c)));
            var paramNames = string.Join(", ", cols.Select(c => "@" + c));

            var sql = _db.Provider switch
            {
                DatabaseProvider.PostgreSql => $"INSERT INTO {_db.Quote(tbl)} ({colNames}) VALUES ({paramNames}) RETURNING id;",
                DatabaseProvider.SqlServer => $"INSERT INTO {_db.Quote(tbl)} ({colNames}) OUTPUT INSERTED.id VALUES ({paramNames});",
                DatabaseProvider.Sqlite => $"INSERT INTO {_db.Quote(tbl)} ({colNames}) VALUES ({paramNames}); SELECT last_insert_rowid();",
                _ => $"INSERT INTO {_db.Quote(tbl)} ({colNames}) VALUES ({paramNames}); SELECT LAST_INSERT_ID();"
            };

            var parameters = new DynamicParameters();
            foreach (var col in cols) parameters.Add(col, record[col]);
            newId = conn.ExecuteScalar<int>(sql, parameters);
        }

        LogMessage(model, newId, "System", "Record created", "notification");
        _logger.LogInformation("Record #{Id} created on model {Model}", newId, model);
        return newId;
    }

    public bool Write(string model, int id, Dictionary<string, object> values)
    {
        var modelFields = GetFields(model);
        var recordValues = new Dictionary<string, object>(values);

        foreach (var (fname, fdef) in modelFields)
        {
            if (fdef.Type == FieldType.Many2one && recordValues.TryGetValue(fname, out var val) && val != null)
            {
                if (val is JsonElement je && je.ValueKind == JsonValueKind.Array && je.GetArrayLength() > 0)
                {
                    recordValues[fname] = je[0].GetInt32();
                }
                else if (val is object[] arr && arr.Length > 0)
                {
                    recordValues[fname] = Convert.ToInt32(arr[0]);
                }
            }
        }

        if (_registeredModelExtensions.TryGetValue(model, out var extensions))
        {
            foreach (var ext in extensions)
            {
                ext.ComputeFields(recordValues, this, _logger);
                ext.ValidateConstrains(recordValues, this, _logger);
            }
        }

        if (_db.Provider == DatabaseProvider.InMemory)
        {
            if (!_inMemoryDataStore.TryGetValue(model, out var records)) return false;
            var record = records.FirstOrDefault(r => Convert.ToInt32(r["id"]) == id);
            if (record == null) return false;

            foreach (var (k, v) in recordValues)
            {
                if (k != "id" && k != "message_ids") record[k] = v;
            }
            return true;
        }
        else
        {
            using var conn = _db.CreateConnection();
            conn.Open();
            var tbl = _db.Sanitize(model);
            var cols = recordValues.Keys.Where(k => k != "id" && modelFields.ContainsKey(k) && modelFields[k].Type != FieldType.One2many).ToList();
            var setClause = string.Join(", ", cols.Select(c => $"{_db.Quote(c)} = @{c}"));

            var sql = $"UPDATE {_db.Quote(tbl)} SET {setClause} WHERE id = @id";
            var parameters = new DynamicParameters();
            parameters.Add("id", id);
            foreach (var col in cols) parameters.Add(col, recordValues[col]);

            var success = conn.Execute(sql, parameters) > 0;
            _logger.LogInformation("Record #{Id} updated on model {Model}", id, model);
            return success;
        }
    }

    public bool Unlink(string model, int id)
    {
        _logger.LogWarning("Record #{Id} deleted from model {Model}", id, model);
        if (_db.Provider == DatabaseProvider.InMemory)
        {
            if (!_inMemoryDataStore.TryGetValue(model, out var records)) return false;
            return records.RemoveAll(r => Convert.ToInt32(r["id"]) == id) > 0;
        }
        else
        {
            using var conn = _db.CreateConnection();
            conn.Open();
            var tbl = _db.Sanitize(model);
            return conn.Execute($"DELETE FROM {_db.Quote(tbl)} WHERE id = @id", new { id }) > 0;
        }
    }

    public void LogMessage(string model, int recordId, string author, string body, string type = "comment")
    {
        int msgId = _mailMessages.Count > 0 ? _mailMessages.Max(m => m.Id) + 1 : 1;
        _mailMessages.Add(new MailMessage(msgId, model, recordId, author, body, DateTime.Now.ToString("g"), type));
    }

    public List<MailMessage> GetMessages(string model, int recordId) =>
        _mailMessages.Where(m => m.Model == model && m.RecordId == recordId).OrderByDescending(m => m.Id).ToList();

    public Dictionary<string, object> ExecuteOnChange(string model, string fieldName, Dictionary<string, object> currentValues)
    {
        var mutatedValues = new Dictionary<string, object>(currentValues);
        if (!_registeredModelExtensions.TryGetValue(model, out var extensions)) return mutatedValues;

        foreach (var ext in extensions)
        {
            var changes = ext.EvaluateOnchange(fieldName, mutatedValues, this, _logger);
            foreach (var (k, v) in changes) mutatedValues[k] = v;
        }
        return mutatedValues;
    }

    public object? ExecuteButtonAction(string model, string actionName, int id, Dictionary<string, object> currentValues)
    {
        if (!_registeredModelExtensions.TryGetValue(model, out var extensions)) return null;

        Func<object?>? pipeline = null;
        foreach (var ext in extensions)
        {
            var currentExt = ext;
            var next = pipeline;
            pipeline = () => currentExt.ExecuteMethod(actionName, id, currentValues, this, next);
        }

        var res = pipeline != null ? pipeline() : new { status = "success" };
        LogMessage(model, id, "Administrator", $"Action executed: {actionName}", "notification");
        _logger.LogInformation("Action {Action} executed on model {Model} for record #{Id}", actionName, model, id);
        return res;
    }
}

public record BaseView(string Id, string Model, string Type, string Arch, string Module = "base");
public record InheritedView(string Id, string InheritId, string Arch, string Module = "base");

public class ViewRegistry
{
    private readonly Dictionary<string, BaseView> _baseViews = new();
    private readonly List<InheritedView> _inheritedViews = new();

    public void AddBaseView(BaseView view) => _baseViews[view.Id] = view;
    public void AddInheritedView(InheritedView view) => _inheritedViews.Add(view);
    public void RemoveModuleViews(string module)
    {
        var baseToRemove = _baseViews.Where(kv => kv.Value.Module == module).Select(kv => kv.Key).ToList();
        foreach (var k in baseToRemove) _baseViews.Remove(k);
        _inheritedViews.RemoveAll(v => v.Module == module);
    }
    public void Clear() { _baseViews.Clear(); _inheritedViews.Clear(); }

    public (string Arch, Dictionary<string, FieldDef> Fields) GetView(string model, string viewType, ModelRegistry models)
    {
        var baseView = _baseViews.Values.FirstOrDefault(v => v.Model == model && v.Type == viewType);
        if (baseView == null) return ($"<{viewType}></{viewType}>", new());

        var xmlDoc = XDocument.Parse(baseView.Arch);
        var relevantExtensions = _inheritedViews.Where(iv => iv.InheritId == baseView.Id);

        foreach (var ext in relevantExtensions)
        {
            var patchDoc = XDocument.Parse(ext.Arch);
            var xpaths = patchDoc.XPathSelectElements("//xpath");

            foreach (var xpath in xpaths)
            {
                var expr = xpath.Attribute("expr")?.Value;
                var position = xpath.Attribute("position")?.Value ?? "inside";
                if (string.IsNullOrEmpty(expr)) continue;

                var targetElement = xmlDoc.XPathSelectElement(expr);
                if (targetElement == null) continue;

                if (position.Equals("attributes", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var attrNode in xpath.Elements("attribute"))
                    {
                        var attrName = attrNode.Attribute("name")?.Value;
                        if (!string.IsNullOrEmpty(attrName)) targetElement.SetAttributeValue(attrName, attrNode.Value);
                    }
                    continue;
                }

                var nodesToInject = xpath.Nodes().ToList();
                switch (position.ToLower())
                {
                    case "after": targetElement.AddAfterSelf(nodesToInject); break;
                    case "before": targetElement.AddBeforeSelf(nodesToInject); break;
                    case "replace": targetElement.ReplaceWith(nodesToInject); break;
                    case "inside": default: targetElement.Add(nodesToInject); break;
                }
            }
        }

        return (xmlDoc.ToString(), models.GetFields(model));
    }
}

public static class XmlDataLoader
{
    public static void LoadXmlFile(string filePath, ViewRegistry views, MenuRegistry menus, ModelRegistry models, string module, ILogger logger)
    {
        if (!File.Exists(filePath)) return;
        var doc = XDocument.Load(filePath);
        var records = doc.XPathSelectElements("//record");

        foreach (var rec in records)
        {
            var model = rec.Attribute("model")?.Value;
            var id = rec.Attribute("id")?.Value ?? Guid.NewGuid().ToString();

            if (model == "ir.ui.view")
            {
                var targetModel = rec.XPathSelectElement("field[@name='model']")?.Value;
                var arch = rec.XPathSelectElement("field[@name='arch']")?.FirstNode?.ToString() ?? "";
                var inheritId = rec.XPathSelectElement("field[@name='inherit_id']")?.Attribute("ref")?.Value;

                if (!string.IsNullOrEmpty(inheritId))
                    views.AddInheritedView(new InheritedView(id, inheritId, arch, module));
                else
                {
                    var archDoc = XElement.Parse(arch);
                    views.AddBaseView(new BaseView(id, targetModel!, archDoc.Name.LocalName, arch, module));
                }
                models.TrackData(module, "ir.ui.view", id, id, "view");
            }
            else if (model != null)
            {
                var values = new Dictionary<string, object>();
                foreach (var fieldNode in rec.Elements("field"))
                {
                    var fname = fieldNode.Attribute("name")?.Value;
                    if (fname != null) values[fname] = fieldNode.Value;
                }
                int newId = models.Create(model, values);
                models.TrackData(module, model, id, newId.ToString(), "record");
            }
        }

        var menuItems = doc.XPathSelectElements("//menuitem");
        foreach (var item in menuItems)
        {
            var menuId = item.Attribute("id")?.Value ?? Guid.NewGuid().ToString();
            var name = item.Attribute("name")?.Value ?? "Menu";
            var icon = item.Attribute("icon")?.Value ?? "bi-folder";
            var actionType = item.Attribute("action_type")?.Value ?? "act_window";
            var targetModel = item.Attribute("model")?.Value;
            var viewMode = item.Attribute("view_mode")?.Value ?? "tree,form,kanban";
            var clientUrl = item.Attribute("client_url")?.Value;
            var clientExport = item.Attribute("client_export")?.Value;

            menus.AddMenu(new OdooMenuItem(menuId, name, icon, actionType, targetModel, viewMode, clientUrl, clientExport, module));
            models.TrackData(module, "ir.ui.menu", menuId, menuId, "menu");
        }
    }
}

public class OdooModuleLifecycleManager
{
    private readonly List<string> _addonsRoots;
    private readonly ModelRegistry _modelRegistry;
    private readonly ViewRegistry _viewRegistry;
    private readonly MenuRegistry _menuRegistry;
    private readonly IMvcBuilder _mvcBuilder;
    private readonly ILogger<OdooModuleLifecycleManager> _logger;
    private readonly Dictionary<string, OdooManifest> _discoveredManifests = new();
    private readonly Dictionary<string, AssemblyLoadContext> _loadedContexts = new();
    private readonly string _stateFilePath = "installed_modules.json";

    public OdooModuleLifecycleManager(
        List<string> addonsRoots,
        ModelRegistry modelRegistry,
        ViewRegistry viewRegistry,
        MenuRegistry menuRegistry,
        IMvcBuilder mvcBuilder,
        ILogger<OdooModuleLifecycleManager> logger)
    {
        _addonsRoots = addonsRoots;
        _modelRegistry = modelRegistry;
        _viewRegistry = viewRegistry;
        _menuRegistry = menuRegistry;
        _mvcBuilder = mvcBuilder;
        _logger = logger;
        ScanManifests();
        LoadPersistedState();
        RebuildActiveState();
    }

    public void ScanManifests()
    {
        _discoveredManifests.Clear();
        foreach (var root in _addonsRoots)
        {
            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
                continue;
            }

            foreach (var addonDir in Directory.GetDirectories(root))
            {
                var manifestPath = Path.Combine(addonDir, "manifest.json");
                if (!File.Exists(manifestPath))
                    manifestPath = Path.Combine(addonDir, "__manifest__.json");

                if (!File.Exists(manifestPath)) continue;

                var manifestJson = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<OdooManifest>(manifestJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (manifest == null) continue;

                manifest.TechnicalName = new DirectoryInfo(addonDir).Name;
                manifest.FolderPath = addonDir;
                manifest.State = "uninstalled";
                _discoveredManifests[manifest.TechnicalName] = manifest;
                _logger.LogInformation("Discovered Addon: {Name} ({TechnicalName})", manifest.Name, manifest.TechnicalName);
            }
        }
    }

    private void LoadPersistedState()
    {
        if (File.Exists(_stateFilePath))
        {
            try
            {
                var installedList = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_stateFilePath));
                if (installedList != null)
                {
                    foreach (var techName in installedList)
                    {
                        if (_discoveredManifests.TryGetValue(techName, out var manifest))
                            manifest.State = "installed";
                    }
                }
            }
            catch { }
        }
    }

    private void SavePersistedState()
    {
        var installedList = _discoveredManifests.Values
            .Where(m => m.State == "installed")
            .Select(m => m.TechnicalName)
            .ToList();
        File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(installedList, new JsonSerializerOptions { WriteIndented = true }));
    }

    public List<OdooManifest> GetDiscoveredModules() => _discoveredManifests.Values.ToList();

    public void SetModuleState(string technicalName, bool install)
    {
        if (_discoveredManifests.TryGetValue(technicalName, out var manifest))
        {
            manifest.State = install ? "installed" : "uninstalled";
            SavePersistedState();

            if (!install)
            {
                _modelRegistry.DropModuleSchemaAndData(technicalName);
                _viewRegistry.RemoveModuleViews(technicalName);
                _menuRegistry.RemoveModuleMenus(technicalName);
            }
            
            RebuildActiveState();
        }
    }

    public void RebuildActiveState()
    {
        _modelRegistry.ClearModels();
        _viewRegistry.Clear();
        _menuRegistry.Clear();

        _modelRegistry.Register(new BasePartnerModel());

        _viewRegistry.AddBaseView(new BaseView(
            Id: "base.view_partner_form",
            Model: "res.partner",
            Type: "form",
            Arch: @"
            <form string=""Contact"">
                <sheet>
                    <div class=""oe_button_box"">
                        <button class=""oe_stat_button"" type=""stat"" icon=""bi-receipt"" label=""Invoices"" count_model=""account.move"" count_field=""partner_id""/>
                    </div>
                    <group string=""General Information"">
                        <field name=""name"" required=""1""/>
                        <field name=""is_company""/>
                        <field name=""vat""/>
                        <field name=""email""/>
                        <field name=""phone""/>
                    </group>
                </sheet>
                <div class=""oe_chatter""/>
            </form>",
            Module: "base"
        ));

        _viewRegistry.AddBaseView(new BaseView(
            Id: "base.view_partner_tree",
            Model: "res.partner",
            Type: "tree",
            Arch: @"
            <tree string=""Contacts"">
                <field name=""id""/>
                <field name=""name""/>
                <field name=""is_company""/>
                <field name=""email""/>
                <field name=""phone""/>
            </tree>",
            Module: "base"
        ));

        _viewRegistry.AddBaseView(new BaseView(
            Id: "base.view_partner_kanban",
            Model: "res.partner",
            Type: "kanban",
            Arch: @"
            <kanban string=""Contacts"">
                <field name=""name""/>
                <field name=""email""/>
                <field name=""phone""/>
            </kanban>",
            Module: "base"
        ));

        _menuRegistry.AddMenu(new OdooMenuItem("contacts_app", "Contacts", "bi-people-fill", "act_window", "res.partner", "tree,kanban,form", Module: "base"));

        foreach (var (techName, manifest) in _discoveredManifests)
        {
            if (manifest.State != "installed") continue;

            string? dllPath = !string.IsNullOrEmpty(manifest.AssemblyFile)
                ? Path.Combine(manifest.FolderPath, manifest.AssemblyFile)
                : Directory.GetFiles(manifest.FolderPath, "*.dll").FirstOrDefault();

            if (dllPath != null && File.Exists(dllPath))
            {
                if (!_loadedContexts.TryGetValue(techName, out var alc))
                {
                    alc = new AssemblyLoadContext(dllPath, isCollectible: true);
                    _loadedContexts[techName] = alc;
                }

                var asm = alc.LoadFromAssemblyPath(dllPath);
                foreach (var type in asm.GetTypes().Where(t => typeof(IOdooAddon).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface))
                {
                    var addon = (IOdooAddon)Activator.CreateInstance(type)!;
                    addon.RegisterModels(_modelRegistry);
                }
                _mvcBuilder.AddApplicationPart(asm);
            }

            foreach (var relativeData in manifest.Data)
            {
                var fullPath = Path.Combine(manifest.FolderPath, relativeData);
                if (File.Exists(fullPath))
                {
                    XmlDataLoader.LoadXmlFile(fullPath, _viewRegistry, _menuRegistry, _modelRegistry, techName, _logger);
                }
            }
        }

        _menuRegistry.AddMenu(new OdooMenuItem("apps_manager", "Apps", "bi-box-seam-fill", "client_action", null, null, "/webclient.js", "AppsManagerDashboard", Module: "base"));
        _modelRegistry.AutoSyncSchema();
    }
}
'@ | Set-Content -Path "Core.OdooEngine/OdooCore.cs"

@'
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Core.OdooEngine;

public record CallKwRequest(string Model, string Method, JsonElement Args, JsonElement Kwargs);

[ApiController]
public class UniversalRpcController(
    ModelRegistry models,
    ViewRegistry views,
    MenuRegistry menus,
    OdooModuleLifecycleManager lifecycleManager,
    ILogger<UniversalRpcController> logger) : ControllerBase
{
    [HttpGet("/web/session/modules")]
    public IActionResult GetMenus() => Ok(menus.GetMenus());

    [HttpGet("/web/apps/list")]
    public IActionResult GetApps() => Ok(lifecycleManager.GetDiscoveredModules());

    [HttpPost("/web/apps/toggle")]
    public IActionResult ToggleApp([FromBody] JsonElement body)
    {
        var techName = body.GetProperty("technicalName").GetString()!;
        var install = body.GetProperty("install").GetBoolean();
        lifecycleManager.SetModuleState(techName, install);
        return Ok(new { status = "success" });
    }

    [HttpGet("/web/mail/chatter/{model}/{id}")]
    public IActionResult GetChatter(string model, int id) => Ok(models.GetMessages(model, id));

    [HttpPost("/web/mail/post")]
    public IActionResult PostMessage([FromBody] JsonElement body)
    {
        var model = body.GetProperty("model").GetString()!;
        var id = body.GetProperty("id").GetInt32();
        var msg = body.GetProperty("body").GetString()!;
        models.LogMessage(model, id, "Administrator", msg, "comment");
        return Ok(new { status = "success" });
    }

    [HttpPost("/web/dataset/call_kw")]
    public IActionResult CallKw([FromBody] CallKwRequest req)
    {
        try
        {
            switch (req.Method)
            {
                case "get_view":
                    var viewType = req.Kwargs.TryGetProperty("view_type", out var vt) ? vt.GetString()! : "form";
                    var (arch, fields) = views.GetView(req.Model, viewType, models);
                    return Ok(new { arch, fields });

                case "search_read":
                    List<string>? fieldList = null;
                    if (req.Kwargs.TryGetProperty("fields", out var fListElem))
                        fieldList = JsonSerializer.Deserialize<List<string>>(fListElem.GetRawText());
                    
                    List<List<object>>? domain = null;
                    if (req.Kwargs.TryGetProperty("domain", out var domElem))
                        domain = JsonSerializer.Deserialize<List<List<object>>>(domElem.GetRawText());

                    return Ok(models.SearchRead(req.Model, fieldList, domain));

                case "onchange":
                    var changedField = req.Args[0].GetString()!;
                    var valuesMap = JsonSerializer.Deserialize<Dictionary<string, object>>(req.Args[1].GetRawText())!;
                    return Ok(new { value = models.ExecuteOnChange(req.Model, changedField, valuesMap) });

                case "create":
                    var createValues = JsonSerializer.Deserialize<Dictionary<string, object>>(req.Args[0].GetRawText());
                    return Ok(models.Create(req.Model, createValues!));

                case "write":
                    var id = req.Args[0].GetInt32();
                    var writeValues = JsonSerializer.Deserialize<Dictionary<string, object>>(req.Args[1].GetRawText());
                    return Ok(models.Write(req.Model, id, writeValues!));

                case "unlink":
                    return Ok(models.Unlink(req.Model, req.Args[0].GetInt32()));

                case "name_search":
                    var allRelational = models.SearchRead(req.Model, ["id", "name"]);
                    var formatted = allRelational.Select(r => new object[] { r["id"], r.ContainsKey("name") ? r["name"] : $"#{r["id"]}" }).ToList();
                    return Ok(formatted);

                default:
                    var btnRecordId = req.Args.GetArrayLength() > 0 ? req.Args[0].GetInt32() : 0;
                    var currentValues = req.Args.GetArrayLength() > 1 
                        ? JsonSerializer.Deserialize<Dictionary<string, object>>(req.Args[1].GetRawText())! 
                        : new Dictionary<string, object>();
                    
                    return Ok(models.ExecuteButtonAction(req.Model, req.Method, btnRecordId, currentValues) ?? new { status = "success" });
            }
        }
        catch (TargetInvocationException tie) when (tie.InnerException is ValidationError ve)
        {
            logger.LogWarning("Validation error on {Model}: {Message}", req.Model, ve.Message);
            return BadRequest(new { error = ve.Message });
        }
        catch (ValidationError ve)
        {
            logger.LogWarning("Validation error on {Model}: {Message}", req.Model, ve.Message);
            return BadRequest(new { error = ve.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RPC execution error on model {Model}, method {Method}", req.Model, req.Method);
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
'@ | Set-Content -Path "Core.OdooEngine/UniversalRpcController.cs"

# -------------------------------------------------------------
# 2. HostApp Project (.NET 10)
# -------------------------------------------------------------
Write-Host "==> Generating HostApp..." -ForegroundColor Yellow
@'
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Core.OdooEngine\Core.OdooEngine.csproj" />
  </ItemGroup>
</Project>
'@ | Set-Content -Path "HostApp/HostApp.csproj"

@'
module.exports = {
  addonsPath: [ "addons" ],
  dbType: "InMemory",
  connectionString: "",
  port: 5000
};
'@ | Set-Content -Path "HostApp/appsettings.js"

@'
using Core.OdooEngine;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.IncludeScopes = true;
    options.SingleLine = true;
    options.TimestampFormat = "[HH:mm:ss] ";
});
builder.Logging.SetMinimumLevel(LogLevel.Information);

var mvcBuilder = builder.Services.AddControllers();

var jsConfigPath = Path.Combine(AppContext.BaseDirectory, "appsettings.js");
if (!File.Exists(jsConfigPath)) jsConfigPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.js");

var tempLogger = LoggerFactory.Create(b => b.AddSimpleConsole()).CreateLogger("Bootstrap");
var jsConfig = AppSettingsConfig.LoadFromJsFile(jsConfigPath, tempLogger);
var resolvedAddonsPaths = jsConfig.ResolveAddonsAbsolutePaths(AppContext.BaseDirectory);

builder.Services.AddSingleton(jsConfig);
builder.Services.AddSingleton<DatabaseAdapter>();
builder.Services.AddSingleton<ModelRegistry>();
builder.Services.AddSingleton<ViewRegistry>();
builder.Services.AddSingleton<MenuRegistry>();
builder.Services.AddSingleton(sp => new OdooModuleLifecycleManager(
    resolvedAddonsPaths,
    sp.GetRequiredService<ModelRegistry>(),
    sp.GetRequiredService<ViewRegistry>(),
    sp.GetRequiredService<MenuRegistry>(),
    mvcBuilder,
    sp.GetRequiredService<ILogger<OdooModuleLifecycleManager>>()
));

var app = builder.Build();

var modelRegistry = app.Services.GetRequiredService<ModelRegistry>();
app.Services.GetRequiredService<OdooModuleLifecycleManager>();

if (modelRegistry.SearchRead("res.partner", ["id"]).Count == 0)
{
    modelRegistry.Create("res.partner", new Dictionary<string, object>
    {
        ["name"] = "Deco Addict",
        ["is_company"] = true,
        ["vat"] = "US987654321",
        ["email"] = "deco@example.com",
        ["phone"] = "+1 555-0100"
    });
}

app.UseDefaultFiles();
app.UseStaticFiles();

foreach (var root in resolvedAddonsPaths)
{
    if (Directory.Exists(root))
    {
        foreach (var dir in Directory.GetDirectories(root))
        {
            var wwwroot = Path.Combine(dir, "wwwroot");
            var moduleName = new DirectoryInfo(dir).Name.ToLower();
            if (Directory.Exists(wwwroot))
            {
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(wwwroot),
                    RequestPath = $"/addons/{moduleName}"
                });
            }
        }
    }
}

app.UseRouting();
app.MapControllers();
app.Run();
'@ | Set-Content -Path "HostApp/Program.cs"

@'
<!DOCTYPE html>
<html lang="en" data-bs-theme="light">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Odoo .NET Modular ERP Suite</title>
    <link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css" rel="stylesheet">
    <link href="https://cdn.jsdelivr.net/npm/bootstrap-icons@1.11.3/font/bootstrap-icons.min.css" rel="stylesheet">
    <script type="importmap">
    {
      "imports": {
        "react": "https://esm.sh/react@18.2.0",
        "react-dom/client": "https://esm.sh/react-dom@18.2.0/client"
      }
    }
    </script>
    <style>
        :root { --o-brand-primary: #714B67; --o-brand-secondary: #008784; }
        .o-navbar { background-color: var(--o-brand-primary) !important; }
        .o-btn-primary { background-color: var(--o-brand-secondary); border-color: var(--o-brand-secondary); color: #fff; }
        .o-btn-primary:hover { background-color: #00706e; border-color: #00706e; color: #fff; }
        .o-form-sheet { background: #fff; border: 1px solid #dee2e6; border-radius: 0.375rem; box-shadow: 0 0.125rem 0.25rem rgba(0,0,0,0.05); max-width: 1050px; position: relative; }
        .o-statusbar { background: #f8f9fa; border-bottom: 1px solid #dee2e6; padding: 8px 16px; border-radius: 0.375rem 0.375rem 0 0; }
        .nav-link { cursor: pointer; }
        .kanban-card { transition: transform 0.15s ease, box-shadow 0.15s ease; cursor: pointer; }
        .kanban-card:hover { transform: translateY(-2px); box-shadow: 0 0.5rem 1rem rgba(0,0,0,0.1) !important; }
        .oe_button_box { display: flex; justify-content: flex-end; border-bottom: 1px solid #eee; margin-bottom: 15px; padding-bottom: 8px; }
        .oe_stat_button { background: #f8f9fa; border: 1px solid #dee2e6; border-radius: 4px; padding: 6px 12px; display: flex; align-items: center; gap: 8px; font-size: 13px; font-weight: 600; cursor: pointer; }
        .oe_stat_button:hover { background: #e9ecef; }
        .chatter-box { max-width: 1050px; margin: 20px auto; background: #fff; border: 1px solid #dee2e6; border-radius: 0.375rem; padding: 20px; }
        .app-card { border: 1px solid #dee2e6; border-radius: 6px; padding: 18px; background: #fff; box-shadow: 0 2px 4px rgba(0,0,0,0.04); }
    </style>
</head>
<body class="bg-body-tertiary">
    <div id="root"></div>
    <script src="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/js/bootstrap.bundle.min.js"></script>
    <script type="module" src="/webclient.js"></script>
</body>
</html>
'@ | Set-Content -Path "HostApp/wwwroot/index.html"

# Frontend WebClient script
@'
import React, { useState, useEffect } from 'react';
import { createRoot } from 'react-dom/client';

async function callKw(model, method, args = [], kwargs = {}) {
    const res = await fetch('/web/dataset/call_kw', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ model, method, args, kwargs })
    });
    const data = await res.json();
    if (!res.ok) throw new Error(data.error || 'Server error occurred');
    return data;
}

function evalModifier(expr, record) {
    if (!expr) return false;
    try {
        const keys = Object.keys(record);
        const vals = Object.values(record);
        return new Function(...keys, `return Boolean(${expr});`)(...vals);
    } catch { return false; }
}

export function AppsManagerDashboard({ onAppToggled }) {
    const [apps, setApps] = useState([]);
    const [loading, setLoading] = useState(false);

    const loadApps = () => {
        fetch('/web/apps/list').then(res => res.json()).then(data => setApps(data));
    };

    useEffect(() => { loadApps(); }, []);

    const toggleApp = async (technicalName, install) => {
        setLoading(true);
        await fetch('/web/apps/toggle', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ technicalName, install })
        });
        setLoading(false);
        loadApps();
        if (onAppToggled) onAppToggled();
    };

    return React.createElement('div', { className: 'container-fluid px-4 py-3' },
        React.createElement('div', { className: 'd-flex justify-content-between align-items-center mb-4' },
            React.createElement('h4', { className: 'fw-bold text-secondary m-0' }, '📦 Modular Apps Center'),
            loading ? React.createElement('div', { className: 'spinner-border spinner-border-sm text-primary' }) : null
        ),
        React.createElement('div', { className: 'row g-3' },
            apps.map(app => {
                const isInstalled = app.state === 'installed';
                return React.createElement('div', { key: app.technicalName, className: 'col-md-6 col-lg-3' },
                    React.createElement('div', { className: 'app-card d-flex flex-column justify-content-between h-100' },
                        React.createElement('div', null,
                            React.createElement('div', { className: 'd-flex justify-content-between align-items-start mb-2' },
                                React.createElement('h5', { className: 'fw-bold m-0 text-dark' }, app.name),
                                React.createElement('span', { className: `badge ${isInstalled ? 'bg-success' : 'bg-secondary'}` },
                                    isInstalled ? 'Installed' : 'Not Installed'
                                )
                            ),
                            React.createElement('div', { className: 'small text-muted mb-2' }, `Category: ${app.category}`),
                            React.createElement('p', { className: 'small text-secondary' }, app.summary || 'No description.')
                        ),
                        React.createElement('div', { className: 'd-flex justify-content-between align-items-center mt-3 pt-3 border-top' },
                            React.createElement('code', { className: 'small' }, app.technicalName),
                            isInstalled ? (
                                React.createElement('button', {
                                    type: 'button',
                                    className: 'btn btn-sm btn-outline-danger',
                                    onClick: () => toggleApp(app.technicalName, false)
                                }, React.createElement('i', { className: 'bi bi-x-circle me-1' }), 'Deactivate')
                            ) : (
                                React.createElement('button', {
                                    type: 'button',
                                    className: 'btn btn-sm btn-primary',
                                    onClick: () => toggleApp(app.technicalName, true)
                                }, React.createElement('i', { className: 'bi bi-download me-1' }), 'Activate')
                            )
                        )
                    )
                );
            })
        )
    );
}

function Chatter({ model, recordId }) {
    const [messages, setMessages] = useState([]);
    const [note, setNote] = useState('');

    const loadMessages = () => {
        if (!recordId) return;
        fetch(`/web/mail/chatter/${model}/${recordId}`).then(res => res.json()).then(data => setMessages(data));
    };

    useEffect(() => { loadMessages(); }, [model, recordId]);

    const postNote = async () => {
        if (!note.trim()) return;
        await fetch('/web/mail/post', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ model, id: recordId, body: note })
        });
        setNote('');
        loadMessages();
    };

    if (!recordId) return null;

    return React.createElement('div', { className: 'chatter-box shadow-sm' },
        React.createElement('h6', { className: 'fw-bold text-secondary mb-3' }, '💬 Chatter & Audit Trail'),
        React.createElement('div', { className: 'input-group mb-3' },
            React.createElement('input', {
                type: 'text',
                className: 'form-control form-control-sm',
                placeholder: 'Log an internal note...',
                value: note,
                onChange: (e) => setNote(e.target.value)
            }),
            React.createElement('button', { className: 'btn btn-sm btn-outline-secondary', onClick: postNote }, 'Send')
        ),
        React.createElement('div', { className: 'list-group list-group-flush' },
            messages.map(m => React.createElement('div', { key: m.id, className: 'list-group-item px-0 py-2 border-0' },
                React.createElement('div', { className: 'd-flex justify-content-between align-items-center' },
                    React.createElement('span', { className: 'fw-bold small text-primary' }, m.author),
                    React.createElement('span', { className: 'text-muted', style: { fontSize: '11px' } }, m.date)
                ),
                React.createElement('div', { className: `small ${m.type === 'notification' ? 'text-muted fst-italic' : 'text-dark'}` }, m.body)
            ))
        )
    );
}

function One2manyGrid({ fieldDef, lines = [], onLinesChange }) {
    const childModel = fieldDef.relation;
    const [childFields, setChildFields] = useState({});

    useEffect(() => {
        callKw(childModel, 'get_view', [], { view_type: 'tree' }).then(res => setChildFields(res.fields));
    }, [childModel]);

    const addLine = () => {
        const newLine = { id: `new_${Date.now()}` };
        for (const [fname, fdef] of Object.entries(childFields)) {
            if (fdef.defaultValue !== null) newLine[fname] = fdef.defaultValue;
        }
        onLinesChange([...lines, newLine]);
    };

    const updateCell = async (index, fieldName, value) => {
        const updated = [...lines];
        updated[index] = { ...updated[index], [fieldName]: value };

        if (childModel === 'account.move.line' && fieldName === 'lot_id') {
            try {
                const res = await callKw(childModel, 'onchange', [fieldName, updated[index]]);
                if (res && res.value) {
                    updated[index] = { ...updated[index], ...res.value };
                }
            } catch (err) { console.error(err); }
        }

        if (fieldName === 'quantity' || fieldName === 'price_unit' || fieldName === 'product_uom_qty') {
            const qty = parseFloat(updated[index].quantity || updated[index].product_uom_qty) || 0;
            const price = parseFloat(updated[index].price_unit) || 0;
            updated[index].price_subtotal = qty * price;
        }
        onLinesChange(updated);
    };

    const removeLine = (index) => {
        onLinesChange(lines.filter((_, i) => i !== index));
    };

    const cols = Object.keys(childFields).filter(k => k !== 'id' && k !== fieldDef.inverseName);

    return React.createElement('div', { className: 'col-12 mt-3' },
        React.createElement('label', { className: 'form-label fw-bold text-secondary small' }, fieldDef.string),
        React.createElement('div', { className: 'table-responsive border rounded-2' },
            React.createElement('table', { className: 'table table-sm table-bordered align-middle mb-0' },
                React.createElement('thead', { className: 'table-light' },
                    React.createElement('tr', null,
                        cols.map(c => React.createElement('th', { key: c, className: 'small text-secondary' }, childFields[c]?.string || c)),
                        React.createElement('th', { style: { width: '40px' } })
                    )
                ),
                React.createElement('tbody', null,
                    lines.map((line, idx) => React.createElement('tr', { key: line.id || idx },
                        cols.map(col => {
                            const fdef = childFields[col];
                            if (fdef && fdef.type === 5 && fdef.relation) {
                                return React.createElement('td', { key: col, className: 'p-1' },
                                    React.createElement('input', {
                                        className: 'form-control form-control-sm border-0 bg-transparent',
                                        value: line[col] ?? '',
                                        placeholder: 'Lot ID #',
                                        onChange: (e) => updateCell(idx, col, parseInt(e.target.value) || 0),
                                        type: 'number'
                                    })
                                );
                            }
                            return React.createElement('td', { key: col, className: 'p-1' },
                                React.createElement('input', {
                                    className: 'form-control form-control-sm border-0 bg-transparent',
                                    value: line[col] ?? '',
                                    onChange: (e) => updateCell(idx, col, fdef?.type === 1 || fdef?.type === 2 ? parseFloat(e.target.value) || 0 : e.target.value),
                                    type: fdef?.type === 1 || fdef?.type === 2 ? 'number' : 'text'
                                })
                            );
                        }),
                        React.createElement('td', { className: 'text-center p-1' },
                            React.createElement('button', {
                                type: 'button',
                                className: 'btn btn-sm btn-link text-danger p-0',
                                onClick: () => removeLine(idx)
                            }, React.createElement('i', { className: 'bi bi-trash' }))
                        )
                    ))
                )
            )
        ),
        React.createElement('button', {
            type: 'button',
            className: 'btn btn-sm btn-link text-decoration-none mt-1 p-0 fw-semibold',
            onClick: addLine
        }, React.createElement('i', { className: 'bi bi-plus-circle me-1' }), 'Add a line')
    );
}

function DynamicOdooForm({ model, recordId, onBack, onNavigateRelational }) {
    const [archDoc, setArchDoc] = useState(null);
    const [fields, setFields] = useState({});
    const [record, setRecord] = useState({});
    const [isNew, setIsNew] = useState(!recordId);
    const [relOptions, setRelOptions] = useState({});
    const [errorMsg, setErrorMsg] = useState(null);

    const loadData = async () => {
        const viewData = await callKw(model, 'get_view', [], { view_type: 'form' });
        const parser = new DOMParser();
        setArchDoc(parser.parseFromString(viewData.arch, 'text/xml'));
        setFields(viewData.fields);

        for (const [fname, fdef] of Object.entries(viewData.fields)) {
            if (fdef.type === 5 && fdef.relation) {
                const options = await callKw(fdef.relation, 'name_search', []);
                setRelOptions(prev => ({ ...prev, [fname]: options }));
            }
        }

        if (recordId) {
            const data = await callKw(model, 'search_read', [], { fields: Object.keys(viewData.fields) });
            const rec = data.find(r => r.id === recordId) || {};
            setRecord(rec);
        }
    };

    useEffect(() => { loadData(); }, [model, recordId]);

    const handleFieldChange = async (name, value) => {
        setErrorMsg(null);
        const updated = { ...record, [name]: value };
        setRecord(updated);

        try {
            const res = await callKw(model, 'onchange', [name, updated]);
            if (res && res.value) setRecord(prev => ({ ...prev, ...res.value }));
        } catch (e) { setErrorMsg(e.message); }
    };

    const handleLinesChange = (one2manyFieldName, newLines) => {
        const updated = { ...record, [one2manyFieldName]: newLines };
        const total = newLines.reduce((sum, l) => sum + (parseFloat(l.price_subtotal) || 0), 0);
        if (fields['amount_total']) updated['amount_total'] = total;
        if (fields['amount_untaxed']) updated['amount_untaxed'] = total;
        setRecord(updated);
    };

    const handleSave = async () => {
        setErrorMsg(null);
        try {
            let masterId = recordId;
            if (isNew) masterId = await callKw(model, 'create', [record]);
            else await callKw(model, 'write', [recordId, record]);

            for (const [fname, fdef] of Object.entries(fields)) {
                if (fdef.type === 6 && record[fname]) {
                    for (const line of record[fname]) {
                        const childPayload = { ...line, [fdef.inverseName]: masterId };
                        if (String(line.id).startsWith('new_')) {
                            delete childPayload.id;
                            await callKw(fdef.relation, 'create', [childPayload]);
                        } else {
                            await callKw(fdef.relation, 'write', [line.id, childPayload]);
                        }
                    }
                }
            }
            onBack();
        } catch (e) { setErrorMsg(e.message); }
    };

    const handleButtonClick = async (btnNode) => {
        setErrorMsg(null);
        try {
            const btnType = btnNode.getAttribute('type');
            const btnName = btnNode.getAttribute('name');
            if (btnType === 'object') {
                await callKw(model, btnName, [recordId || 0, record]);
                await loadData();
            }
        } catch (e) { setErrorMsg(e.message); }
    };

    if (!archDoc) return React.createElement('div', { className: 'p-5 text-center' }, React.createElement('div', { className: 'spinner-border text-primary' }));

    const renderElements = (node) => {
        return Array.from(node.children).map((child, i) => {
            if (child.tagName === 'header') {
                return React.createElement('div', { key: i, className: 'o-statusbar d-flex justify-content-between align-items-center mb-3' },
                    React.createElement('div', { className: 'btn-group' }, renderElements(child)),
                    record.status ? React.createElement('span', { className: 'badge bg-secondary fs-6' }, String(record.status)) : null
                );
            }
            if (child.tagName === 'button') {
                const isInvisible = evalModifier(child.getAttribute('invisible'), record);
                if (isInvisible) return null;

                return React.createElement('button', {
                    key: i,
                    type: 'button',
                    className: `btn btn-sm ${child.getAttribute('class') || 'btn-outline-primary'} me-2`,
                    onClick: () => handleButtonClick(child)
                }, child.getAttribute('string'));
            }
            if (child.tagName === 'div' && child.getAttribute('class') === 'oe_button_box') {
                return React.createElement('div', { key: i, className: 'oe_button_box' },
                    Array.from(child.children).map((btn, bIdx) => {
                        return React.createElement('div', {
                            key: bIdx,
                            className: 'oe_stat_button shadow-sm',
                            onClick: () => onNavigateRelational(btn.getAttribute('count_model'), btn.getAttribute('count_field'), recordId)
                        },
                            React.createElement('i', { className: `bi ${btn.getAttribute('icon')} text-primary fs-5` }),
                            React.createElement('div', null,
                                React.createElement('div', { className: 'text-muted', style: { fontSize: '10px' } }, btn.getAttribute('label')),
                                React.createElement('div', { className: 'fw-bold text-dark' }, 'View')
                            )
                        );
                    })
                );
            }
            if (child.tagName === 'sheet') {
                return React.createElement('div', { key: i, className: 'o-form-sheet p-4 mx-auto my-3' }, renderElements(child));
            }
            if (child.tagName === 'group') {
                const groupTitle = child.getAttribute('string');
                return React.createElement('div', { key: i, className: 'card mb-3 border-0 bg-transparent' },
                    groupTitle ? React.createElement('h6', { className: 'text-muted border-bottom pb-2 mb-3' }, groupTitle) : null,
                    React.createElement('div', { className: 'row g-3' }, renderElements(child))
                );
            }
            if (child.tagName === 'field') {
                const fieldName = child.getAttribute('name');
                const fieldDef = fields[fieldName] || { string: fieldName, type: 0 };

                const isInvisible = evalModifier(child.getAttribute('invisible'), record);
                if (isInvisible) return null;

                // Explicitly respect explicit readonly attribute only when explicitly true, avoiding blank-state coercion locking
                const readonlyAttr = child.getAttribute('readonly');
                const isReadonly = readonlyAttr === '1' || readonlyAttr === 'True' || fieldDef.readonly || (readonlyAttr ? evalModifier(readonlyAttr, record) : false);
                const isRequired = child.getAttribute('required') === '1' || fieldDef.required || evalModifier(child.getAttribute('required'), record);

                if (fieldDef.type === 6) {
                    return React.createElement(One2manyGrid, {
                        key: fieldName,
                        fieldDef: fieldDef,
                        lines: record[fieldName] || [],
                        onLinesChange: (lines) => handleLinesChange(fieldName, lines)
                    });
                }

                if (fieldDef.type === 3) {
                    return React.createElement('div', { key: fieldName, className: 'col-md-6 d-flex align-items-center mt-4' },
                        React.createElement('div', { className: 'form-check' },
                            React.createElement('input', {
                                className: 'form-check-input',
                                type: 'checkbox',
                                id: fieldName,
                                checked: Boolean(record[fieldName]),
                                disabled: isReadonly,
                                onChange: (e) => handleFieldChange(fieldName, e.target.checked)
                            }),
                            React.createElement('label', { className: 'form-check-label fw-semibold text-secondary small ms-1', htmlFor: fieldName }, fieldDef.string)
                        )
                    );
                }

                if (fieldDef.type === 5) {
                    const currentVal = Array.isArray(record[fieldName]) ? record[fieldName][0] : record[fieldName];
                    return React.createElement('div', { key: fieldName, className: 'col-md-6' },
                        React.createElement('label', { className: 'form-label fw-semibold text-secondary small' },
                            fieldDef.string, isRequired ? React.createElement('span', { className: 'text-danger' }, ' *') : null
                        ),
                        React.createElement('select', {
                            className: `form-select form-select-sm ${isRequired && !currentVal ? 'border-danger' : ''}`,
                            value: currentVal || '',
                            disabled: isReadonly,
                            onChange: (e) => handleFieldChange(fieldName, parseInt(e.target.value))
                        },
                            React.createElement('option', { value: '' }, '-- Select --'),
                            (relOptions[fieldName] || []).map(([optId, optName]) =>
                                React.createElement('option', { key: optId, value: optId }, optName)
                            )
                        )
                    );
                }

                if (fieldDef.type === 4 && fieldDef.selection) {
                    return React.createElement('div', { key: fieldName, className: 'col-md-6' },
                        React.createElement('label', { className: 'form-label fw-semibold text-secondary small' }, fieldDef.string),
                        React.createElement('select', {
                            className: 'form-select form-select-sm',
                            value: record[fieldName] || '',
                            disabled: isReadonly,
                            onChange: (e) => handleFieldChange(fieldName, e.target.value)
                        },
                            fieldDef.selection.map(s => React.createElement('option', { key: s.value, value: s.value }, s.label))
                        )
                    );
                }

                return React.createElement('div', { key: fieldName, className: 'col-md-6' },
                    React.createElement('label', { className: 'form-label fw-semibold text-secondary small' },
                        fieldDef.string, isRequired ? React.createElement('span', { className: 'text-danger' }, ' *') : null
                    ),
                    React.createElement('input', {
                        className: `form-control form-control-sm ${isRequired && !record[fieldName] ? 'border-danger' : ''}`,
                        value: Array.isArray(record[fieldName]) ? record[fieldName][1] : (record[fieldName] ?? ''),
                        readOnly: isReadonly,
                        onChange: (e) => handleFieldChange(fieldName, fieldDef.type === 1 || fieldDef.type === 2 ? parseFloat(e.target.value) || 0 : e.target.value),
                        type: fieldDef.type === 1 || fieldDef.type === 2 ? 'number' : 'text'
                    })
                );
            }
            return null;
        });
    };

    return React.createElement('div', null,
        React.createElement('div', { className: 'd-flex justify-content-between align-items-center bg-white p-3 border-bottom shadow-sm mb-4' },
            React.createElement('div', { className: 'btn-group' },
                React.createElement('button', { className: 'btn btn-sm o-btn-primary', onClick: handleSave },
                    React.createElement('i', { className: 'bi bi-check-lg me-1' }), 'Save'
                ),
                React.createElement('button', { className: 'btn btn-sm btn-outline-secondary', onClick: onBack },
                    React.createElement('i', { className: 'bi bi-x-lg me-1' }), 'Discard'
                )
            ),
            React.createElement('span', { className: 'badge text-bg-light border text-secondary' }, isNew ? 'New' : `ID: #${recordId}`)
        ),
        errorMsg ? React.createElement('div', { className: 'container mb-3' },
            React.createElement('div', { className: 'alert alert-danger shadow-sm' },
                React.createElement('i', { className: 'bi bi-exclamation-triangle-fill me-2' }),
                React.createElement('strong', null, 'Validation Error: '), errorMsg
            )
        ) : null,
        React.createElement('div', { className: 'container' }, renderElements(archDoc.documentElement)),
        React.createElement(Chatter, { model: model, recordId: recordId })
    );
}

function DynamicOdooKanban({ model, onOpenRecord, domain }) {
    const [records, setRecords] = useState([]);
    const [fields, setFields] = useState({});
    const [kanbanFields, setKanbanFields] = useState([]);

    useEffect(() => {
        async function load() {
            const viewData = await callKw(model, 'get_view', [], { view_type: 'kanban' });
            const parser = new DOMParser();
            const xml = parser.parseFromString(viewData.arch, 'text/xml');
            const fieldNodes = Array.from(xml.querySelectorAll('field')).map(n => n.getAttribute('name'));

            setKanbanFields(fieldNodes);
            setFields(viewData.fields);

            const data = await callKw(model, 'search_read', [], { fields: fieldNodes, domain: domain });
            setRecords(data);
        }
        load();
    }, [model, domain]);

    return React.createElement('div', { className: 'container-fluid px-4 py-3' },
        React.createElement('div', { className: 'row g-3' },
            records.map(rec => React.createElement('div', { key: rec.id, className: 'col-md-4 col-lg-3' },
                React.createElement('div', { 
                    className: 'card kanban-card shadow-sm border-0 h-100 p-3 bg-white',
                    onClick: () => onOpenRecord(rec.id)
                },
                    React.createElement('div', { className: 'fw-bold fs-6 text-primary mb-2' }, 
                        rec[kanbanFields[0]] ? (Array.isArray(rec[kanbanFields[0]]) ? rec[kanbanFields[0]][1] : rec[kanbanFields[0]]) : `#${rec.id}`
                    ),
                    kanbanFields.slice(1).map(f => React.createElement('div', { key: f, className: 'small text-muted mb-1' },
                        React.createElement('span', { className: 'fw-semibold' }, `${fields[f]?.string || f}: `),
                        Array.isArray(rec[f]) ? rec[f][1] : String(rec[f] ?? '')
                    ))
                )
            ))
        )
    );
}

function DynamicOdooList({ model, onOpenRecord, domain }) {
    const [records, setRecords] = useState([]);
    const [treeFields, setTreeFields] = useState([]);
    const [fieldDefs, setFieldDefs] = useState({});

    useEffect(() => {
        async function load() {
            const viewData = await callKw(model, 'get_view', [], { view_type: 'tree' });
            const parser = new DOMParser();
            const xml = parser.parseFromString(viewData.arch, 'text/xml');
            const fieldNodes = Array.from(xml.querySelectorAll('field')).map(n => n.getAttribute('name'));

            setTreeFields(fieldNodes);
            setFieldDefs(viewData.fields);

            const data = await callKw(model, 'search_read', [], { fields: fieldNodes, domain: domain });
            setRecords(data);
        }
        load();
    }, [model, domain]);

    const formatCell = (val) => Array.isArray(val) ? val[1] : (typeof val === 'boolean' ? (val ? '✔' : '✖') : String(val ?? ''));

    return React.createElement('div', { className: 'container-fluid px-4' },
        React.createElement('div', { className: 'card shadow-sm border-0' },
            React.createElement('div', { className: 'table-responsive' },
                React.createElement('table', { className: 'table table-hover align-middle mb-0' },
                    React.createElement('thead', { className: 'table-light' },
                        React.createElement('tr', null,
                            treeFields.map(f => React.createElement('th', { key: f, className: 'small text-secondary fw-semibold' }, fieldDefs[f]?.string || f))
                        )
                    ),
                    React.createElement('tbody', null,
                        records.map(rec => React.createElement('tr', { 
                            key: rec.id, 
                            onClick: () => onOpenRecord(rec.id),
                            style: { cursor: 'pointer' }
                        },
                            treeFields.map(f => React.createElement('td', { key: f, className: 'small' }, formatCell(rec[f])))
                        ))
                    )
                )
            )
        )
    );
}

function WebClient() {
    const [menus, setMenus] = useState([]);
    const [activeMenu, setActiveMenu] = useState(null);
    const [viewMode, setViewMode] = useState('tree');
    const [selectedRecordId, setSelectedRecordId] = useState(null);
    const [domain, setDomain] = useState([]);

    const loadSessionMenus = () => {
        fetch('/web/session/modules').then(res => res.json()).then(data => {
            setMenus(data);
            if (!activeMenu || !data.some(m => m.id === activeMenu.id)) {
                if (data.length > 0) setActiveMenu(data[0]);
            }
        });
    };

    useEffect(() => { loadSessionMenus(); }, []);

    const handleNavigateRelational = (targetModel, field, id) => {
        const targetMenu = menus.find(m => m.targetModel === targetModel);
        if (targetMenu) {
            setActiveMenu(targetMenu);
            setDomain([[field, '=', id]]);
            setViewMode('tree');
        }
    };

    if (!activeMenu) return React.createElement('div', { className: 'p-5 text-center' }, 'Loading Suite...');

    return React.createElement(React.Fragment, null,
        React.createElement('nav', { className: 'navbar navbar-expand o-navbar navbar-dark shadow-sm px-3' },
            React.createElement('a', { className: 'navbar-brand fw-bold d-flex align-items-center gap-2 me-4', href: '#' },
                React.createElement('i', { className: 'bi bi-shop' }),
                'Odoo Enterprise ERP'
            ),
            React.createElement('div', { className: 'navbar-nav me-auto' },
                menus.map(m => React.createElement('a', {
                    key: m.id,
                    className: `nav-link px-3 ${activeMenu.id === m.id ? 'active fw-bold' : 'text-white-50'}`,
                    onClick: () => { setActiveMenu(m); setViewMode('tree'); setSelectedRecordId(null); setDomain([]); }
                },
                    React.createElement('i', { className: `bi ${m.icon} me-1` }),
                    m.name
                ))
            )
        ),
        activeMenu.actionType !== 'client_action' && viewMode !== 'form' ? (
            React.createElement('div', { className: 'd-flex justify-content-between align-items-center bg-white p-3 border-bottom shadow-sm mb-3' },
                React.createElement('button', { 
                    className: 'btn btn-sm o-btn-primary', 
                    onClick: () => { setSelectedRecordId(null); setViewMode('form'); } 
                }, React.createElement('i', { className: 'bi bi-plus-lg me-1' }), 'New'),
                React.createElement('div', { className: 'btn-group' },
                    React.createElement('button', {
                        className: `btn btn-sm ${viewMode === 'tree' ? 'btn-secondary' : 'btn-outline-secondary'}`,
                        onClick: () => setViewMode('tree')
                    }, React.createElement('i', { className: 'bi bi-list-ul' })),
                    React.createElement('button', {
                        className: `btn btn-sm ${viewMode === 'kanban' ? 'btn-secondary' : 'btn-outline-secondary'}`,
                        onClick: () => setViewMode('kanban')
                    }, React.createElement('i', { className: 'bi bi-kanban' }))
                )
            )
        ) : null,
        React.createElement('div', { className: 'main-container' },
            activeMenu.actionType === 'client_action'
                ? React.createElement(DynamicModulePage, { scriptUrl: activeMenu.clientComponentUrl, componentName: activeMenu.clientComponentExport })
                : viewMode === 'form'
                    ? React.createElement(DynamicOdooForm, {
                        model: activeMenu.targetModel,
                        recordId: selectedRecordId,
                        onBack: () => setViewMode('tree'),
                        onNavigateRelational: handleNavigateRelational
                    })
                    : viewMode === 'kanban'
                        ? React.createElement(DynamicOdooKanban, {
                            model: activeMenu.targetModel,
                            domain: domain,
                            onOpenRecord: (id) => { setSelectedRecordId(id); setViewMode('form'); }
                        })
                        : React.createElement(DynamicOdooList, {
                            model: activeMenu.targetModel,
                            domain: domain,
                            onOpenRecord: (id) => { setSelectedRecordId(id); setViewMode('form'); }
                        })
        )
    );
}

function DynamicModulePage({ scriptUrl, componentName }) {
    const [Component, setComponent] = useState(null);
    const [error, setError] = useState(null);
    useEffect(() => {
        import(scriptUrl)
            .then(mod => setComponent(() => mod[componentName] || mod.default))
            .catch(err => setError(err.message));
    }, [scriptUrl, componentName]);

    if (error) return React.createElement('div', { className: 'alert alert-danger m-4' }, `Failed to load report: {error}`);
    if (!Component) return React.createElement('div', { className: 'p-5 text-center' }, React.createElement('div', { className: 'spinner-border text-primary' }));
    return React.createElement(Component, { callKw });
}

createRoot(document.getElementById('root')).render(React.createElement(WebClient));
'@ | Set-Content -Path "HostApp/wwwroot/webclient.js"

# -------------------------------------------------------------
# 3. SALES ADDON
# -------------------------------------------------------------
Write-Host "==> Generating SalesAddon..." -ForegroundColor Yellow
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Core.OdooEngine\Core.OdooEngine.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>
</Project>
'@ | Set-Content -Path "SalesAddon/SalesAddon.csproj"

@'
{
  "name": "Sales Management & Profit Reports",
  "version": "1.0.0",
  "category": "Sales",
  "summary": "Customer Sales Orders, Margins, and Financial P&L Analytics",
  "depends": [ "base", "ProductAddon", "InvoicingAddon" ],
  "data": [ "views/sales_views.xml" ],
  "application": true,
  "installable": true,
  "assemblyFile": "SalesAddon.dll"
}
'@ | Set-Content -Path "SalesAddon/manifest.json"

@'
using Core.OdooEngine;

namespace SalesAddon;

public class SaleOrderLineModel : OdooModel
{
    public override string Name => "sale.order.line";

    public SaleOrderLineModel()
    {
        AddField("order_id", FieldType.Many2one, "Order Reference", relation: "sale.order", module: "SalesAddon");
        AddField("product_id", FieldType.Many2one, "Product", relation: "product.template", required: true, module: "SalesAddon");
        AddField("name", FieldType.Char, "Description", module: "SalesAddon");
        AddField("product_uom_qty", FieldType.Float, "Quantity", defaultValue: 1.0, module: "SalesAddon");
        AddField("price_unit", FieldType.Float, "Sales Price", defaultValue: 0.0, module: "SalesAddon");
        AddField("price_subtotal", FieldType.Float, "Subtotal", defaultValue: 0.0, readonlyField: true, module: "SalesAddon");
        AddField("margin", FieldType.Float, "Margin ($)", defaultValue: 0.0, readonlyField: true, compute: "ComputeMargin", module: "SalesAddon");
    }

    [ApiDepends("product_uom_qty", "price_unit", "product_id")]
    public void ComputeMargin(Dictionary<string, object> line, ModelRegistry registry)
    {
        var qty = Convert.ToDouble(line.TryGetValue("product_uom_qty", out var q) ? q : 1.0);
        var price = Convert.ToDouble(line.TryGetValue("price_unit", out var p) ? p : 0.0);
        line["price_subtotal"] = qty * price;

        double cost = 50.0;
        if (line.TryGetValue("product_id", out var pIdObj) && pIdObj != null)
        {
            int pId = pIdObj is object[] arr ? Convert.ToInt32(arr[0]) : Convert.ToInt32(pIdObj);
            var prods = registry.SearchRead("product.template", ["standard_price"], [["id", "=", pId]]);
            if (prods.Count > 0 && prods[0].TryGetValue("standard_price", out var cp))
                cost = Convert.ToDouble(cp);
        }
        line["margin"] = (qty * price) - (qty * cost);
    }
}

public class SaleOrderModel : OdooModel
{
    public override string Name => "sale.order";

    public SaleOrderModel()
    {
        AddField("name", FieldType.Char, "Order Reference", required: true, module: "SalesAddon");
        AddField("partner_id", FieldType.Many2one, "Customer", relation: "res.partner", required: true, module: "SalesAddon");
        AddField("order_line", FieldType.One2many, "Order Lines", relation: "sale.order.line", inverseName: "order_id", module: "SalesAddon");
        AddField("amount_untaxed", FieldType.Float, "Untaxed Amount", defaultValue: 0.0, readonlyField: true, compute: "ComputeTotals", module: "SalesAddon");
        AddField("amount_tax", FieldType.Float, "Taxes", defaultValue: 0.0, readonlyField: true, compute: "ComputeTotals", module: "SalesAddon");
        AddField("amount_total", FieldType.Float, "Total ($)", defaultValue: 0.0, readonlyField: true, compute: "ComputeTotals", module: "SalesAddon");
        AddField("total_margin", FieldType.Float, "Total Profit ($)", defaultValue: 0.0, readonlyField: true, compute: "ComputeTotals", module: "SalesAddon");
        AddField("status", FieldType.Selection, "Status", defaultValue: "draft", selection: new List<SelectionOption>
        {
            new("draft", "Quotation"),
            new("sent", "Quotation Sent"),
            new("sale", "Sales Order"),
            new("done", "Locked"),
            new("cancel", "Cancelled")
        }, readonlyField: true, module: "SalesAddon");
    }

    [ApiDepends("order_line")]
    public void ComputeTotals(Dictionary<string, object> record, ModelRegistry registry)
    {
        if (record.TryGetValue("order_line", out var linesObj) && linesObj is List<Dictionary<string, object>> lines)
        {
            double untaxed = lines.Sum(l => Convert.ToDouble(l.TryGetValue("price_subtotal", out var st) ? st : 0.0));
            double margin = lines.Sum(l => Convert.ToDouble(l.TryGetValue("margin", out var m) ? m : 0.0));
            record["amount_untaxed"] = untaxed;
            record["amount_tax"] = untaxed * 0.10;
            record["amount_total"] = untaxed * 1.10;
            record["total_margin"] = margin;
        }
    }

    public virtual object ActionConfirmSales(int id, Dictionary<string, object> values, ModelRegistry registry)
    {
        registry.Write("sale.order", id, new Dictionary<string, object> { ["status"] = "sale" });

        var partner = values.TryGetValue("partner_id", out var p) ? (p is object[] arr ? arr[0] : p) : 1;
        var amount = values.TryGetValue("amount_total", out var a) ? a : 0.0;

        registry.Create("account.move", new Dictionary<string, object>
        {
            ["name"] = $"INV/SO-{id:D4}",
            ["partner_id"] = Convert.ToInt32(partner),
            ["invoice_type"] = "out_invoice",
            ["amount_total"] = amount,
            ["status"] = "draft"
        });

        return new { status = "confirmed" };
    }
}

public class SalesModule : IOdooAddon
{
    public string TechnicalName => "SalesAddon";

    public void RegisterModels(ModelRegistry registry)
    {
        registry.Register(new SaleOrderLineModel());
        registry.Register(new SaleOrderModel());
    }
}
'@ | Set-Content -Path "SalesAddon/SalesModule.cs"

@'
<?xml version="1.0" encoding="utf-8"?>
<odoo>
    <data>
        <record id="sale.view_order_tree" model="ir.ui.view">
            <field name="name">sale.order.tree</field>
            <field name="model">sale.order</field>
            <field name="arch" type="xml">
                <tree string="Sales Orders">
                    <field name="id"/>
                    <field name="name"/>
                    <field name="partner_id"/>
                    <field name="amount_total"/>
                    <field name="total_margin"/>
                    <field name="status"/>
                </tree>
            </field>
        </record>

        <record id="sale.view_order_form" model="ir.ui.view">
            <field name="name">sale.order.form</field>
            <field name="model">sale.order</field>
            <field name="arch" type="xml">
                <form string="Sales Order">
                    <header>
                        <button name="ActionConfirmSales" string="Confirm Order" type="object" class="btn-success" invisible="status != 'draft'"/>
                    </header>
                    <sheet>
                        <group string="Customer Information">
                            <field name="name"/>
                            <field name="partner_id"/>
                        </group>
                        <group string="Order Lines">
                            <field name="order_line"/>
                        </group>
                        <group string="Profitability Summary">
                            <field name="amount_untaxed"/>
                            <field name="amount_tax"/>
                            <field name="amount_total"/>
                            <field name="total_margin"/>
                            <field name="status"/>
                        </group>
                    </sheet>
                    <div class="oe_chatter"/>
                </form>
            </field>
        </record>

        <menuitem id="sales_app" name="Sales" icon="bi-cart-check-fill" action_type="act_window" model="sale.order" view_mode="tree,kanban,form"/>
        <menuitem id="sales_pl_report" name="Profit &amp; Loss Report" icon="bi-graph-up-arrow" action_type="client_action" client_url="/addons/salesaddon/sales_report.js" client_export="ProfitAndLossReport"/>

        <record id="demo_so_1" model="sale.order">
            <field name="name">SO-001</field>
            <field name="partner_id">1</field>
            <field name="amount_untaxed">1500.0</field>
            <field name="amount_tax">150.0</field>
            <field name="amount_total">1650.0</field>
            <field name="total_margin">750.0</field>
            <field name="status">sale</field>
        </record>

        <record id="demo_so_line_1" model="sale.order.line">
            <field name="order_id">1</field>
            <field name="product_id">1</field>
            <field name="name">Executive Ergonomic Desk</field>
            <field name="product_uom_qty">2.0</field>
            <field name="price_unit">750.0</field>
            <field name="price_subtotal">1500.0</field>
            <field name="margin">750.0</field>
        </record>
    </data>
</odoo>
'@ | Set-Content -Path "SalesAddon/views/sales_views.xml"

@'
import React, { useState, useEffect } from 'react';

export function ProfitAndLossReport({ callKw }) {
    const [orders, setOrders] = useState([]);
    const [filterStatus, setFilterStatus] = useState('all');

    useEffect(() => {
        callKw('sale.order', 'search_read', [], { fields: ['name', 'partner_id', 'amount_untaxed', 'total_margin', 'status'] })
            .then(data => setOrders(data));
    }, []);

    const filteredOrders = orders.filter(o => filterStatus === 'all' || o.status === filterStatus);

    const totalRevenue = filteredOrders.reduce((sum, o) => sum + (parseFloat(o.amount_untaxed) || 0), 0);
    const totalProfit = filteredOrders.reduce((sum, o) => sum + (parseFloat(o.total_margin) || 0), 0);
    const totalCogs = totalRevenue - totalProfit;
    const netMarginPercent = totalRevenue > 0 ? ((totalProfit / totalRevenue) * 100).toFixed(1) : 0;

    return React.createElement('div', { className: 'container-fluid px-4 py-3' },
        React.createElement('div', { className: 'd-flex justify-content-between align-items-center mb-4' },
            React.createElement('h3', { className: 'fw-bold text-secondary m-0' }, '📊 Profit & Loss Financial Statement'),
            React.createElement('div', { className: 'btn-group' },
                React.createElement('button', { className: `btn btn-sm ${filterStatus === 'all' ? 'btn-secondary' : 'btn-outline-secondary'}`, onClick: () => setFilterStatus('all') }, 'All Orders'),
                React.createElement('button', { className: `btn btn-sm ${filterStatus === 'sale' ? 'btn-secondary' : 'btn-outline-secondary'}`, onClick: () => setFilterStatus('sale') }, 'Confirmed Sales'),
                React.createElement('button', { className: `btn btn-sm ${filterStatus === 'draft' ? 'btn-secondary' : 'btn-outline-secondary'}`, onClick: () => setFilterStatus('draft') }, 'Quotations')
            )
        ),
        React.createElement('div', { className: 'row g-3 mb-4' },
            React.createElement('div', { className: 'col-md-3' },
                React.createElement('div', { className: 'card shadow-sm border-0 bg-primary text-white p-3 rounded-3' },
                    React.createElement('div', { className: 'small text-white-50' }, 'Gross Operating Revenue'),
                    React.createElement('div', { className: 'fs-3 fw-bold' }, `$${totalRevenue.toLocaleString()}`)
                )
            ),
            React.createElement('div', { className: 'col-md-3' },
                React.createElement('div', { className: 'card shadow-sm border-0 bg-warning text-dark p-3 rounded-3' },
                    React.createElement('div', { className: 'small text-black-50' }, 'Cost of Goods Sold (COGS)'),
                    React.createElement('div', { className: 'fs-3 fw-bold' }, `$${totalCogs.toLocaleString()}`)
                )
            ),
            React.createElement('div', { className: 'col-md-3' },
                React.createElement('div', { className: 'card shadow-sm border-0 bg-success text-white p-3 rounded-3' },
                    React.createElement('div', { className: 'small text-white-50' }, 'Net Operating Profit'),
                    React.createElement('div', { className: 'fs-3 fw-bold' }, `$${totalProfit.toLocaleString()}`)
                )
            ),
            React.createElement('div', { className: 'col-md-3' },
                React.createElement('div', { className: 'card shadow-sm border-0 bg-dark text-white p-3 rounded-3' },
                    React.createElement('div', { className: 'small text-white-50' }, 'Net Margin %'),
                    React.createElement('div', { className: 'fs-3 fw-bold' }, `${netMarginPercent}%`)
                )
            )
        ),
        React.createElement('div', { className: 'card shadow-sm border-0 p-4' },
            React.createElement('h5', { className: 'fw-bold mb-3' }, 'Statement Line Items'),
            React.createElement('div', { className: 'table-responsive' },
                React.createElement('table', { className: 'table table-hover align-middle mb-0' },
                    React.createElement('thead', { className: 'table-light' },
                        React.createElement('tr', null,
                            React.createElement('th', null, 'Order #'),
                            React.createElement('th', null, 'Customer'),
                            React.createElement('th', null, 'Status'),
                            React.createElement('th', { className: 'text-end' }, 'Revenue'),
                            React.createElement('th', { className: 'text-end' }, 'Profit Margin')
                        )
                    ),
                    React.createElement('tbody', null,
                        filteredOrders.map(o => React.createElement('tr', { key: o.id },
                            React.createElement('td', { className: 'fw-bold' }, o.name),
                            React.createElement('td', null, Array.isArray(o.partner_id) ? o.partner_id[1] : o.partner_id),
                            React.createElement('td', null, React.createElement('span', { className: 'badge bg-light text-dark border' }, o.status)),
                            React.createElement('td', { className: 'text-end' }, `$${parseFloat(o.amount_untaxed || 0).toLocaleString()}`),
                            React.createElement('td', { className: 'text-end text-success fw-bold' }, `$${parseFloat(o.total_margin || 0).toLocaleString()}`)
                        ))
                    )
                )
            )
        )
    );
}

export default ProfitAndLossReport;
'@ | Set-Content -Path "SalesAddon/wwwroot/sales_report.js"

# -------------------------------------------------------------
# 4. PRODUCT ADDON
# -------------------------------------------------------------
Write-Host "==> Generating ProductAddon..." -ForegroundColor Yellow
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Core.OdooEngine\Core.OdooEngine.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>
</Project>
'@ | Set-Content -Path "ProductAddon/ProductAddon.csproj"

@'
{
  "name": "Product & Batch Management",
  "version": "1.0.0",
  "category": "Inventory",
  "summary": "Master Item Creation and Lot/Batch Number Tracking",
  "depends": [ "base" ],
  "data": [ "views/product_views.xml" ],
  "application": true,
  "installable": true,
  "assemblyFile": "ProductAddon.dll"
}
'@ | Set-Content -Path "ProductAddon/manifest.json"

@'
using Core.OdooEngine;

namespace ProductAddon;

public class StockLotModel : OdooModel
{
    public override string Name => "stock.lot";

    public StockLotModel()
    {
        AddField("name", FieldType.Char, "Lot / Serial Number", required: true, module: "ProductAddon");
        AddField("product_id", FieldType.Many2one, "Product", relation: "product.template", required: true, module: "ProductAddon");
        AddField("ref", FieldType.Char, "Internal Reference", module: "ProductAddon");
        AddField("batch_mrp", FieldType.Float, "Batch MRP ($)", defaultValue: 1000.0, module: "ProductAddon");
        AddField("batch_purchase_price", FieldType.Float, "Batch Purchase Price ($)", defaultValue: 600.0, module: "ProductAddon");
        AddField("batch_max_discount", FieldType.Float, "Max Discount (%)", defaultValue: 15.0, module: "ProductAddon");
    }

    [ApiConstrains("name")]
    public void CheckLotNumber(Dictionary<string, object> record)
    {
        if (record.TryGetValue("name", out var n) && n != null && string.IsNullOrWhiteSpace(n.ToString()))
        {
            throw new ValidationError("Lot or Serial number cannot be empty.");
        }
    }
}

public class ProductTemplateModel : OdooModel
{
    public override string Name => "product.template";

    public ProductTemplateModel()
    {
        AddField("name", FieldType.Char, "Product Name", required: true, module: "ProductAddon");
        AddField("default_code", FieldType.Char, "Internal Reference", module: "ProductAddon");
        AddField("list_price", FieldType.Float, "Sales Price", defaultValue: 0.0, module: "ProductAddon");
        AddField("standard_price", FieldType.Float, "Cost", defaultValue: 0.0, module: "ProductAddon");
        AddField("tracking", FieldType.Selection, "Tracking", defaultValue: "none", selection: new List<SelectionOption>
        {
            new("none", "No Tracking"),
            new("serial", "By Unique Serial Number"),
            new("lot", "By Lots / Batches")
        }, module: "ProductAddon");

        AddField("manufacturer_code", FieldType.Char, "Manufacturer Code", module: "ProductAddon");
        AddField("shelf_life_days", FieldType.Integer, "Shelf Life (Days)", defaultValue: 365, module: "ProductAddon");
    }
}

public class ProductModule : IOdooAddon
{
    public string TechnicalName => "ProductAddon";

    public void RegisterModels(ModelRegistry registry)
    {
        registry.Register(new StockLotModel());
        registry.Register(new ProductTemplateModel());
    }
}
'@ | Set-Content -Path "ProductAddon/ProductModule.cs"

@'
<?xml version="1.0" encoding="utf-8"?>
<odoo>
    <data>
        <record id="product.view_template_tree" model="ir.ui.view">
            <field name="name">product.template.tree</field>
            <field name="model">product.template</field>
            <field name="arch" type="xml">
                <tree string="Products">
                    <field name="id"/>
                    <field name="name"/>
                    <field name="default_code"/>
                    <field name="list_price"/>
                    <field name="standard_price"/>
                    <field name="manufacturer_code"/>
                    <field name="shelf_life_days"/>
                </tree>
            </field>
        </record>

        <record id="product.view_template_form" model="ir.ui.view">
            <field name="name">product.template.form</field>
            <field name="model">product.template</field>
            <field name="arch" type="xml">
                <form string="Product Item">
                    <sheet>
                        <group string="General Information">
                            <field name="name"/>
                            <field name="default_code"/>
                            <field name="list_price"/>
                            <field name="standard_price"/>
                            <field name="tracking"/>
                            <field name="manufacturer_code"/>
                            <field name="shelf_life_days"/>
                        </group>
                    </sheet>
                    <div class="oe_chatter"/>
                </form>
            </field>
        </record>

        <record id="stock.view_lot_tree" model="ir.ui.view">
            <field name="name">stock.lot.tree</field>
            <field name="model">stock.lot</field>
            <field name="arch" type="xml">
                <tree string="Lots / Batches">
                    <field name="id"/>
                    <field name="name"/>
                    <field name="product_id"/>
                    <field name="batch_mrp"/>
                    <field name="batch_purchase_price"/>
                    <field name="batch_max_discount"/>
                </tree>
            </field>
        </record>

        <record id="stock.view_lot_form" model="ir.ui.view">
            <field name="name">stock.lot.form</field>
            <field name="model">stock.lot</field>
            <field name="arch" type="xml">
                <form string="Lot / Serial Number">
                    <sheet>
                        <group string="Batch Identification &amp; Pricing">
                            <field name="name"/>
                            <field name="product_id"/>
                            <field name="ref"/>
                            <field name="batch_mrp"/>
                            <field name="batch_purchase_price"/>
                            <field name="batch_max_discount"/>
                        </group>
                    </sheet>
                    <div class="oe_chatter"/>
                </form>
            </field>
        </record>

        <menuitem id="product_app" name="Products" icon="bi-tag-fill" action_type="act_window" model="product.template" view_mode="tree,kanban,form"/>
        <menuitem id="lots_app" name="Lots / Batches" icon="bi-upc-scan" action_type="act_window" model="stock.lot" view_mode="tree,kanban,form"/>

        <record id="demo_product_1" model="product.template">
            <field name="name">Executive Ergonomic Desk</field>
            <field name="default_code">DESK-001</field>
            <field name="list_price">750.0</field>
            <field name="standard_price">350.0</field>
            <field name="tracking">lot</field>
            <field name="manufacturer_code">MFG-EST-99</field>
            <field name="shelf_life_days">730</field>
        </record>

        <record id="demo_lot_1" model="stock.lot">
            <field name="name">BATCH-2026-001</field>
            <field name="product_id">1</field>
            <field name="ref">Supplier Shipment #8839</field>
            <field name="batch_mrp">850.0</field>
            <field name="batch_purchase_price">400.0</field>
            <field name="batch_max_discount">12.5</field>
        </record>
    </data>
</odoo>
'@ | Set-Content -Path "ProductAddon/views/product_views.xml"

# -------------------------------------------------------------
# 5. INVENTORY ADDON
# -------------------------------------------------------------
Write-Host "==> Generating InventoryAddon..." -ForegroundColor Yellow
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Core.OdooEngine\Core.OdooEngine.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>
</Project>
'@ | Set-Content -Path "InventoryAddon/InventoryAddon.csproj"

@'
{
  "name": "Inventory & Stock",
  "version": "1.0.0",
  "category": "Supply Chain",
  "summary": "Warehouse management, Quants, Stock Transfers",
  "depends": [ "base", "ProductAddon" ],
  "data": [ "views/inventory_views.xml" ],
  "application": true,
  "installable": true,
  "assemblyFile": "InventoryAddon.dll"
}
'@ | Set-Content -Path "InventoryAddon/manifest.json"

@'
using Core.OdooEngine;

namespace InventoryAddon;

public class StockWarehouseModel : OdooModel
{
    public override string Name => "stock.warehouse";

    public StockWarehouseModel()
    {
        AddField("name", FieldType.Char, "Warehouse Name", required: true, module: "InventoryAddon");
        AddField("code", FieldType.Char, "Short Code", required: true, module: "InventoryAddon");
    }
}

public class StockQuantModel : OdooModel
{
    public override string Name => "stock.quant";

    public StockQuantModel()
    {
        AddField("product_id", FieldType.Many2one, "Product", relation: "product.template", required: true, module: "InventoryAddon");
        AddField("lot_id", FieldType.Many2one, "Lot / Serial", relation: "stock.lot", module: "InventoryAddon");
        AddField("quantity", FieldType.Float, "Quantity on Hand", defaultValue: 0.0, module: "InventoryAddon");
        AddField("location_name", FieldType.Char, "Location", defaultValue: "WH/Stock", module: "InventoryAddon");
    }
}

public class InventoryModule : IOdooAddon
{
    public string TechnicalName => "InventoryAddon";

    public void RegisterModels(ModelRegistry registry)
    {
        registry.Register(new StockWarehouseModel());
        registry.Register(new StockQuantModel());
    }
}
'@ | Set-Content -Path "InventoryAddon/InventoryModule.cs"

@'
<?xml version="1.0" encoding="utf-8"?>
<odoo>
    <data>
        <record id="stock.view_warehouse_tree" model="ir.ui.view">
            <field name="name">stock.warehouse.tree</field>
            <field name="model">stock.warehouse</field>
            <field name="arch" type="xml">
                <tree string="Warehouses">
                    <field name="id"/>
                    <field name="name"/>
                    <field name="code"/>
                </tree>
            </field>
        </record>

        <record id="stock.view_warehouse_form" model="ir.ui.view">
            <field name="name">stock.warehouse.form</field>
            <field name="model">stock.warehouse</field>
            <field name="arch" type="xml">
                <form string="Warehouse">
                    <sheet>
                        <group string="Warehouse Info">
                            <field name="name"/>
                            <field name="code"/>
                        </group>
                    </sheet>
                    <div class="oe_chatter"/>
                </form>
            </field>
        </record>

        <record id="stock.view_quant_tree" model="ir.ui.view">
            <field name="name">stock.quant.tree</field>
            <field name="model">stock.quant</field>
            <field name="arch" type="xml">
                <tree string="Inventory Valuation (Quants)">
                    <field name="id"/>
                    <field name="product_id"/>
                    <field name="lot_id"/>
                    <field name="quantity"/>
                    <field name="location_name"/>
                </tree>
            </field>
        </record>

        <menuitem id="warehouse_app" name="Warehouses" icon="bi-building" action_type="act_window" model="stock.warehouse" view_mode="tree,kanban,form"/>
        <menuitem id="quants_app" name="Stock Quants" icon="bi-bar-chart-steps" action_type="act_window" model="stock.quant" view_mode="tree,kanban,form"/>

        <record id="demo_wh_1" model="stock.warehouse">
            <field name="name">Main Chicago Hub</field>
            <field name="code">CHI</field>
        </record>

        <record id="demo_quant_1" model="stock.quant">
            <field name="product_id">1</field>
            <field name="lot_id">1</field>
            <field name="quantity">25.0</field>
            <field name="location_name">CHI/Stock</field>
        </record>
    </data>
</odoo>
'@ | Set-Content -Path "InventoryAddon/views/inventory_views.xml"

# -------------------------------------------------------------
# 6. PURCHASE ADDON
# -------------------------------------------------------------
Write-Host "==> Generating PurchaseAddon..." -ForegroundColor Yellow
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Core.OdooEngine\Core.OdooEngine.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>
</Project>
'@ | Set-Content -Path "PurchaseAddon/PurchaseAddon.csproj"

@'
{
  "name": "Purchase Management",
  "version": "1.0.0",
  "category": "Procurement",
  "summary": "Purchase orders and custom line-item attributes",
  "depends": [ "base", "ProductAddon", "InventoryAddon", "InvoicingAddon" ],
  "data": [ "views/purchase_views.xml" ],
  "application": true,
  "installable": true,
  "assemblyFile": "PurchaseAddon.dll"
}
'@ | Set-Content -Path "PurchaseAddon/manifest.json"

@'
using Core.OdooEngine;

namespace PurchaseAddon;

public class PurchaseOrderLineModel : OdooModel
{
    public override string Name => "purchase.order.line";

    public PurchaseOrderLineModel()
    {
        AddField("order_id", FieldType.Many2one, "Order Reference", relation: "purchase.order", module: "PurchaseAddon");
        AddField("product_id", FieldType.Many2one, "Product", relation: "product.template", required: true, module: "PurchaseAddon");
        AddField("name", FieldType.Char, "Description", module: "PurchaseAddon");
        AddField("product_uom_qty", FieldType.Float, "Quantity", defaultValue: 1.0, module: "PurchaseAddon");
        AddField("price_unit", FieldType.Float, "Unit Price", defaultValue: 0.0, module: "PurchaseAddon");
        AddField("price_subtotal", FieldType.Float, "Subtotal", defaultValue: 0.0, readonlyField: true, module: "PurchaseAddon");
        
        AddField("custom_spec_note", FieldType.Char, "Custom Specs / Note", module: "PurchaseAddon");
        AddField("discount_tier", FieldType.Selection, "Discount Tier", defaultValue: "standard", selection: new List<SelectionOption>
        {
            new("standard", "Standard (0%)"),
            new("tier1", "Tier 1 (5%)"),
            new("tier2", "Tier 2 (10%)")
        }, module: "PurchaseAddon");
    }

    [ApiDepends("product_uom_qty", "price_unit", "discount_tier")]
    public void ComputeSubtotal(Dictionary<string, object> line)
    {
        var qty = Convert.ToDouble(line.TryGetValue("product_uom_qty", out var q) ? q : 1.0);
        var price = Convert.ToDouble(line.TryGetValue("price_unit", out var p) ? p : 0.0);
        
        double discountMultiplier = 1.0;
        if (line.TryGetValue("discount_tier", out var dt) && dt != null)
        {
            if (dt.ToString() == "tier1") discountMultiplier = 0.95;
            else if (dt.ToString() == "tier2") discountMultiplier = 0.90;
        }

        line["price_subtotal"] = (qty * price) * discountMultiplier;
    }
}

public class PurchaseOrderModel : OdooModel
{
    public override string Name => "purchase.order";

    public PurchaseOrderModel()
    {
        AddField("name", FieldType.Char, "Reference", required: true, module: "PurchaseAddon");
        AddField("partner_id", FieldType.Many2one, "Vendor", relation: "res.partner", required: true, module: "PurchaseAddon");
        AddField("order_line", FieldType.One2many, "Products", relation: "purchase.order.line", inverseName: "order_id", module: "PurchaseAddon");
        AddField("amount_total", FieldType.Float, "Total ($)", defaultValue: 0.0, readonlyField: true, compute: "ComputeTotals", module: "PurchaseAddon");
        AddField("status", FieldType.Selection, "Status", defaultValue: "draft", selection: new List<SelectionOption>
        {
            new("draft", "Request for Quotation"),
            new("purchase", "Purchase Order"),
            new("done", "Locked"),
            new("cancel", "Cancelled")
        }, readonlyField: true, module: "PurchaseAddon");
    }

    [ApiDepends("order_line")]
    public void ComputeTotals(Dictionary<string, object> record, ModelRegistry registry)
    {
        if (record.TryGetValue("order_line", out var linesObj) && linesObj is List<Dictionary<string, object>> lines)
        {
            record["amount_total"] = lines.Sum(l => Convert.ToDouble(l.TryGetValue("price_subtotal", out var st) ? st : 0.0));
        }
    }

    public virtual object ActionConfirmOrder(int id, Dictionary<string, object> values, ModelRegistry registry)
    {
        registry.Write("purchase.order", id, new Dictionary<string, object> { ["status"] = "purchase" });
        return new { status = "confirmed" };
    }
}

public class PurchaseModule : IOdooAddon
{
    public string TechnicalName => "PurchaseAddon";

    public void RegisterModels(ModelRegistry registry)
    {
        registry.Register(new PurchaseOrderLineModel());
        registry.Register(new PurchaseOrderModel());
    }
}
'@ | Set-Content -Path "PurchaseAddon/PurchaseModule.cs"

@'
<?xml version="1.0" encoding="utf-8"?>
<odoo>
    <data>
        <record id="purchase.view_order_tree" model="ir.ui.view">
            <field name="name">purchase.order.tree</field>
            <field name="model">purchase.order</field>
            <field name="arch" type="xml">
                <tree string="Purchase Orders">
                    <field name="id"/>
                    <field name="name"/>
                    <field name="partner_id"/>
                    <field name="amount_total"/>
                    <field name="status"/>
                </tree>
            </field>
        </record>

        <record id="purchase.view_order_form" model="ir.ui.view">
            <field name="name">purchase.order.form</field>
            <field name="model">purchase.order</field>
            <field name="arch" type="xml">
                <form string="Purchase Order">
                    <header>
                        <button name="ActionConfirmOrder" string="Confirm Order" type="object" class="btn-success" invisible="status != 'draft'"/>
                    </header>
                    <sheet>
                        <group string="Vendor Info">
                            <field name="name"/>
                            <field name="partner_id"/>
                        </group>
                        <group string="Products">
                            <field name="order_line"/>
                        </group>
                        <group string="Total">
                            <field name="amount_total"/>
                            <field name="status"/>
                        </group>
                    </sheet>
                    <div class="oe_chatter"/>
                </form>
            </field>
        </record>

        <menuitem id="purchase_app" name="Purchase" icon="bi-cart-check-fill" action_type="act_window" model="purchase.order" view_mode="tree,kanban,form"/>

        <record id="demo_po_1" model="purchase.order">
            <field name="name">PO-001</field>
            <field name="partner_id">1</field>
            <field name="amount_total">3750.0</field>
            <field name="status">draft</field>
        </record>
    </data>
</odoo>
'@ | Set-Content -Path "PurchaseAddon/views/purchase_views.xml"

# -------------------------------------------------------------
# 7. INVOICING ADDON
# -------------------------------------------------------------
Write-Host "==> Generating InvoicingAddon..." -ForegroundColor Yellow
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Core.OdooEngine\Core.OdooEngine.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>
</Project>
'@ | Set-Content -Path "InvoicingAddon/InvoicingAddon.csproj"

@'
{
  "name": "Invoicing & Billing",
  "version": "1.0.0",
  "category": "Accounting",
  "summary": "Customer Invoices, Vendor Bills, Batch-driven MRP and Discount rules",
  "depends": [ "base", "ProductAddon" ],
  "data": [ "views/invoicing_views.xml" ],
  "application": true,
  "installable": true,
  "assemblyFile": "InvoicingAddon.dll"
}
'@ | Set-Content -Path "InvoicingAddon/manifest.json"

@'
using Core.OdooEngine;

namespace InvoicingAddon;

public class AccountMoveLineModel : OdooModel
{
    public override string Name => "account.move.line";

    public AccountMoveLineModel()
    {
        AddField("move_id", FieldType.Many2one, "Invoice Reference", relation: "account.move", module: "InvoicingAddon");
        AddField("name", FieldType.Char, "Label / Description", required: true, module: "InvoicingAddon");
        AddField("lot_id", FieldType.Many2one, "Lot / Batch Selection", relation: "stock.lot", module: "InvoicingAddon");
        AddField("quantity", FieldType.Float, "Quantity", defaultValue: 1.0, module: "InvoicingAddon");
        AddField("price_unit", FieldType.Float, "Unit Price", defaultValue: 0.0, module: "InvoicingAddon");
        AddField("price_subtotal", FieldType.Float, "Subtotal", defaultValue: 0.0, readonlyField: true, module: "InvoicingAddon");

        AddField("line_mrp", FieldType.Float, "MRP ($)", defaultValue: 0.0, module: "InvoicingAddon");
        AddField("line_purchase_price", FieldType.Float, "Purchase Price ($)", defaultValue: 0.0, module: "InvoicingAddon");
        AddField("line_max_discount", FieldType.Float, "Max Discount (%)", defaultValue: 0.0, module: "InvoicingAddon");
    }

    [ApiOnchange("lot_id")]
    public Dictionary<string, object> OnChangeLotId(Dictionary<string, object> values, ModelRegistry registry)
    {
        var res = new Dictionary<string, object>();
        if (values.TryGetValue("lot_id", out var lotIdObj) && lotIdObj != null)
        {
            int lotId = lotIdObj is object[] arr ? Convert.ToInt32(arr[0]) : Convert.ToInt32(lotIdObj);
            var lots = registry.SearchRead("stock.lot", ["batch_mrp", "batch_purchase_price", "batch_max_discount"], [["id", "=", lotId]]);
            if (lots.Count > 0)
            {
                var lot = lots[0];
                if (lot.TryGetValue("batch_mrp", out var mrp)) res["line_mrp"] = mrp;
                if (lot.TryGetValue("batch_purchase_price", out var pp)) res["line_purchase_price"] = pp;
                if (lot.TryGetValue("batch_max_discount", out var md)) res["line_max_discount"] = md;
            }
        }
        return res;
    }

    [ApiDepends("quantity", "price_unit")]
    public void ComputeSubtotal(Dictionary<string, object> line)
    {
        var qty = Convert.ToDouble(line.TryGetValue("quantity", out var q) ? q : 1.0);
        var price = Convert.ToDouble(line.TryGetValue("price_unit", out var p) ? p : 0.0);
        line["price_subtotal"] = qty * price;
    }
}

public class AccountMoveModel : OdooModel
{
    public override string Name => "account.move";

    public AccountMoveModel()
    {
        AddField("name", FieldType.Char, "Invoice Number", required: true, module: "InvoicingAddon");
        AddField("partner_id", FieldType.Many2one, "Partner", relation: "res.partner", required: true, module: "InvoicingAddon");
        AddField("invoice_type", FieldType.Selection, "Type", defaultValue: "out_invoice", selection: new List<SelectionOption>
        {
            new("out_invoice", "Customer Invoice"),
            new("in_invoice", "Vendor Bill")
        }, module: "InvoicingAddon");
        AddField("invoice_line_ids", FieldType.One2many, "Invoice Lines", relation: "account.move.line", inverseName: "move_id", module: "InvoicingAddon");
        AddField("amount_total", FieldType.Float, "Total ($)", defaultValue: 0.0, module: "InvoicingAddon");
        AddField("status", FieldType.Selection, "Status", defaultValue: "draft", selection: new List<SelectionOption>
        {
            new("draft", "Draft"),
            new("posted", "Posted"),
            new("paid", "Paid")
        }, readonlyField: true, module: "InvoicingAddon");
    }

    public virtual object ActionPost(int id, ModelRegistry registry)
    {
        registry.Write("account.move", id, new Dictionary<string, object> { ["status"] = "posted" });
        return new { status = "posted" };
    }
}

public class InvoicingModule : IOdooAddon
{
    public string TechnicalName => "InvoicingAddon";

    public void RegisterModels(ModelRegistry registry)
    {
        registry.Register(new AccountMoveLineModel());
        registry.Register(new AccountMoveModel());
    }
}
'@ | Set-Content -Path "InvoicingAddon/InvoicingModule.cs"

@'
<?xml version="1.0" encoding="utf-8"?>
<odoo>
    <data>
        <record id="account.view_move_tree" model="ir.ui.view">
            <field name="name">account.move.tree</field>
            <field name="model">account.move</field>
            <field name="arch" type="xml">
                <tree string="Invoices &amp; Bills">
                    <field name="id"/>
                    <field name="name"/>
                    <field name="partner_id"/>
                    <field name="invoice_type"/>
                    <field name="amount_total"/>
                    <field name="status"/>
                </tree>
            </field>
        </record>

        <record id="account.view_move_form" model="ir.ui.view">
            <field name="name">account.move.form</field>
            <field name="model">account.move</field>
            <field name="arch" type="xml">
                <form string="Invoice">
                    <header>
                        <button name="ActionPost" string="Post" type="object" class="btn-success" invisible="status != 'draft'"/>
                    </header>
                    <sheet>
                        <group string="Details">
                            <field name="name"/>
                            <field name="partner_id"/>
                            <field name="invoice_type"/>
                            <field name="invoice_line_ids"/>
                            <field name="amount_total"/>
                            <field name="status"/>
                        </group>
                    </sheet>
                    <div class="oe_chatter"/>
                </form>
            </field>
        </record>

        <menuitem id="invoicing_app" name="Invoicing" icon="bi-receipt" action_type="act_window" model="account.move" view_mode="tree,kanban,form"/>

        <record id="demo_inv_1" model="account.move">
            <field name="name">INV/2026/001</field>
            <field name="partner_id">1</field>
            <field name="invoice_type">out_invoice</field>
            <field name="amount_total">1200.0</field>
            <field name="status">posted</field>
        </record>
    </data>
</odoo>
'@ | Set-Content -Path "InvoicingAddon/views/invoicing_views.xml"

# -------------------------------------------------------------
# 8. ACCOUNTING ADDON
# -------------------------------------------------------------
Write-Host "==> Generating AccountingAddon..." -ForegroundColor Yellow
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Core.OdooEngine\Core.OdooEngine.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>
</Project>
'@ | Set-Content -Path "AccountingAddon/AccountingAddon.csproj"

@'
{
  "name": "Accounting & Financial Statements",
  "version": "1.0.0",
  "category": "Accounting",
  "summary": "Chart of Accounts and Financial Ledgers",
  "depends": [ "base" ],
  "data": [ "views/accounting_views.xml" ],
  "application": true,
  "installable": true,
  "assemblyFile": "AccountingAddon.dll"
}
'@ | Set-Content -Path "AccountingAddon/manifest.json"

@'
using Core.OdooEngine;

namespace AccountingAddon;

public class AccountAccountModel : OdooModel
{
    public override string Name => "account.account";

    public AccountAccountModel()
    {
        AddField("code", FieldType.Char, "Code", required: true, module: "AccountingAddon");
        AddField("name", FieldType.Char, "Account Name", required: true, module: "AccountingAddon");
        AddField("account_type", FieldType.Selection, "Account Type", defaultValue: "asset_current", selection: new List<SelectionOption>
        {
            new("asset_current", "Current Assets"),
            new("liability_current", "Current Liabilities"),
            new("income", "Revenue"),
            new("expense", "Expenses")
        }, module: "AccountingAddon");
        AddField("current_balance", FieldType.Float, "Balance ($)", defaultValue: 0.0, module: "AccountingAddon");
    }
}

public class AccountingModule : IOdooAddon
{
    public string TechnicalName => "AccountingAddon";

    public void RegisterModels(ModelRegistry registry)
    {
        registry.Register(new AccountAccountModel());
    }
}
'@ | Set-Content -Path "AccountingAddon/AccountingModule.cs"

@'
<?xml version="1.0" encoding="utf-8"?>
<odoo>
    <data>
        <record id="account.view_account_tree" model="ir.ui.view">
            <field name="name">account.account.tree</field>
            <field name="model">account.account</field>
            <field name="arch" type="xml">
                <tree string="Chart of Accounts">
                    <field name="code"/>
                    <field name="name"/>
                    <field name="account_type"/>
                    <field name="current_balance"/>
                </tree>
            </field>
        </record>

        <record id="account.view_account_form" model="ir.ui.view">
            <field name="name">account.account.form</field>
            <field name="model">account.account</field>
            <field name="arch" type="xml">
                <form string="Account">
                    <sheet>
                        <group string="Configuration">
                            <field name="code"/>
                            <field name="name"/>
                            <field name="account_type"/>
                            <field name="current_balance"/>
                        </group>
                    </sheet>
                    <div class="oe_chatter"/>
                </form>
            </field>
        </record>

        <menuitem id="accounting_app" name="Accounting" icon="bi-bank" action_type="act_window" model="account.account" view_mode="tree,kanban,form"/>
    </data>
</odoo>
'@ | Set-Content -Path "AccountingAddon/views/accounting_views.xml"

# -------------------------------------------------------------
# 9. BUILD, DEPLOY & RUN
# -------------------------------------------------------------
Write-Host "==> Registering Projects in Solution..." -ForegroundColor Cyan
dotnet sln add Core.OdooEngine HostApp ProductAddon InventoryAddon PurchaseAddon SalesAddon InvoicingAddon AccountingAddon

Write-Host "==> Compiling Solution (.NET 10)..." -ForegroundColor Cyan
dotnet build Core.OdooEngine -c Release
dotnet build ProductAddon -c Release
dotnet build InventoryAddon -c Release
dotnet build PurchaseAddon -c Release
dotnet build SalesAddon -c Release
dotnet build InvoicingAddon -c Release
dotnet build AccountingAddon -c Release

Write-Host "==> Deploying Addons to drop-in directories..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path `
    "HostApp/bin/Debug/net10.0/addons/ProductAddon/views", `
    "HostApp/bin/Debug/net10.0/addons/InventoryAddon/views", `
    "HostApp/bin/Debug/net10.0/addons/PurchaseAddon/views", `
    "HostApp/bin/Debug/net10.0/addons/SalesAddon/views", `
    "HostApp/bin/Debug/net10.0/addons/SalesAddon/wwwroot", `
    "HostApp/bin/Debug/net10.0/addons/InvoicingAddon/views", `
    "HostApp/bin/Debug/net10.0/addons/AccountingAddon/views" | Out-Null

Copy-Item "HostApp/appsettings.js" "HostApp/bin/Debug/net10.0/" -Force

if (Test-Path "HostApp/bin/Debug/net10.0/installed_modules.json") {
    Remove-Item "HostApp/bin/Debug/net10.0/installed_modules.json" -Force
}

Copy-Item "ProductAddon/manifest.json" "HostApp/bin/Debug/net10.0/addons/ProductAddon/" -Force
Copy-Item "ProductAddon/bin/Release/net10.0/ProductAddon.dll" "HostApp/bin/Debug/net10.0/addons/ProductAddon/" -Force
Copy-Item "ProductAddon/views/product_views.xml" "HostApp/bin/Debug/net10.0/addons/ProductAddon/views/" -Force

Copy-Item "InventoryAddon/manifest.json" "HostApp/bin/Debug/net10.0/addons/InventoryAddon/" -Force
Copy-Item "InventoryAddon/bin/Release/net10.0/InventoryAddon.dll" "HostApp/bin/Debug/net10.0/addons/InventoryAddon/" -Force
Copy-Item "InventoryAddon/views/inventory_views.xml" "HostApp/bin/Debug/net10.0/addons/InventoryAddon/views/" -Force

Copy-Item "PurchaseAddon/manifest.json" "HostApp/bin/Debug/net10.0/addons/PurchaseAddon/" -Force
Copy-Item "PurchaseAddon/bin/Release/net10.0/PurchaseAddon.dll" "HostApp/bin/Debug/net10.0/addons/PurchaseAddon/" -Force
Copy-Item "PurchaseAddon/views/purchase_views.xml" "HostApp/bin/Debug/net10.0/addons/PurchaseAddon/views/" -Force

Copy-Item "SalesAddon/manifest.json" "HostApp/bin/Debug/net10.0/addons/SalesAddon/" -Force
Copy-Item "SalesAddon/bin/Release/net10.0/SalesAddon.dll" "HostApp/bin/Debug/net10.0/addons/SalesAddon/" -Force
Copy-Item "SalesAddon/views/sales_views.xml" "HostApp/bin/Debug/net10.0/addons/SalesAddon/views/" -Force
Copy-Item "SalesAddon/wwwroot/sales_report.js" "HostApp/bin/Debug/net10.0/addons/SalesAddon/wwwroot/" -Force

Copy-Item "InvoicingAddon/manifest.json" "HostApp/bin/Debug/net10.0/addons/InvoicingAddon/" -Force
Copy-Item "InvoicingAddon/bin/Release/net10.0/InvoicingAddon.dll" "HostApp/bin/Debug/net10.0/addons/InvoicingAddon/" -Force
Copy-Item "InvoicingAddon/views/invoicing_views.xml" "HostApp/bin/Debug/net10.0/addons/InvoicingAddon/views/" -Force

Copy-Item "AccountingAddon/manifest.json" "HostApp/bin/Debug/net10.0/addons/AccountingAddon/" -Force
Copy-Item "AccountingAddon/bin/Release/net10.0/AccountingAddon.dll" "HostApp/bin/Debug/net10.0/addons/AccountingAddon/" -Force
Copy-Item "AccountingAddon/views/accounting_views.xml" "HostApp/bin/Debug/net10.0/addons/AccountingAddon/views/" -Force

Write-Host "==> Launching HostApp on .NET 10 Ultimate ERP Suite..." -ForegroundColor Green
Set-Location HostApp
dotnet run