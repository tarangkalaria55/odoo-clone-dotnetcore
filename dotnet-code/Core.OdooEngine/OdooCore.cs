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
