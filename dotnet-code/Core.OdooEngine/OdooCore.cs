using System.Data;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml.XPath;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

// Mirrors odoo/orm/decorators.py @api.ondelete: runs a business-rule guard before a record
// is actually deleted (e.g. "can't delete a posted invoice"), separate from field @api.constrains.
[AttributeUsage(AttributeTargets.Method)]
public class ApiOndeleteAttribute : Attribute;

// Mirrors odoo/exceptions.py's user-facing exception hierarchy - each maps to a distinct HTTP
// status in UniversalRpcController rather than everything falling into a generic 400/500.
public class ValidationError(string message) : Exception(message);      // 400 - a field/record constraint was violated
public class UserError(string message) : Exception(message);            // 422 - valid data, but not allowed given current state
public class AccessError(string message) : Exception(message);          // 403 - not permitted to read/write/delete this
public class MissingError(string message) : Exception(message);         // 404 - record no longer exists

public class AppSettingsConfig
{
    public List<string> AddonsPath { get; set; } = new() { "addons" };
    public string ConnectionString { get; set; } = "";
    public int Port { get; set; } = 5000;

    public static AppSettingsConfig LoadFromJsonFile(string jsonFilePath, ILogger? logger = null)
    {
        var config = new AppSettingsConfig();
        if (!File.Exists(jsonFilePath)) return config;

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var parsed = JsonSerializer.Deserialize<AppSettingsConfig>(File.ReadAllText(jsonFilePath), options);
            if (parsed != null) return parsed;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to parse appsettings.json.");
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
    public string Author { get; set; } = "Odoo .NET Suite";
    public string Website { get; set; } = string.Empty;
    public string License { get; set; } = "LGPL-3";
    public List<string> Depends { get; set; } = new();
    public List<string> Data { get; set; } = new();
    public bool Application { get; set; } = false;
    public bool Installable { get; set; } = true;
    public bool AutoInstall { get; set; } = false;
    public string? AssemblyFile { get; set; }
    public string State { get; set; } = "uninstalled";
    public string FolderPath { get; set; } = string.Empty;
}

// Many2many appended last (not alphabetically) so existing numeric type codes (5=Many2one,
// 6=One2many, etc.) already hardcoded in webclient.js don't shift.
public enum FieldType { Char, Integer, Float, Boolean, Selection, Many2one, One2many, DateTime, Text, Many2many }
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

public record SqlConstraintDef(string Name, string[] Fields, string Message);

public abstract class OdooModel
{
    public abstract string Name { get; }
    public virtual string Inherit => string.Empty;
    public Dictionary<string, FieldDef> Fields { get; } = new();
    public List<SqlConstraintDef> SqlConstraints { get; } = new();

    // Mirrors Odoo's _sql_constraints: a real Postgres UNIQUE constraint (so it's actually
    // race-free, not a check-then-insert) whose violation gets translated into `message`
    // instead of a raw Postgres error. odoo/orm/models.py `_add_sql_constraints`.
    public void AddSqlConstraint(string name, string[] fields, string message) =>
        SqlConstraints.Add(new SqlConstraintDef(name, fields, message));

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

    public virtual void ValidateOndelete(Dictionary<string, object> record, ModelRegistry registry, ILogger logger)
    {
        foreach (var method in this.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.GetCustomAttribute<ApiOndeleteAttribute>() != null)
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 2) method.Invoke(this, [record, registry]);
                else if (parameters.Length == 1) method.Invoke(this, [record]);
                else method.Invoke(this, null);
            }
        }
    }

    // Mirrors Odoo's get_public_method (odoo/service/model.py): only model-declared action
    // methods are callable over RPC, not framework hooks or inherited object members.
    private static readonly HashSet<string> RpcBlockedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(ToString), nameof(Equals), nameof(GetHashCode), nameof(GetType),
        nameof(AddField), nameof(EvaluateOnchange), nameof(ExecuteMethod)
    };

    private static bool IsCallableAction(MethodInfo method) =>
        !RpcBlockedMethods.Contains(method.Name)
        && method.GetCustomAttribute<ApiOnchangeAttribute>() == null
        && method.GetCustomAttribute<ApiDependsAttribute>() == null
        && method.GetCustomAttribute<ApiConstrainsAttribute>() == null
        && method.GetCustomAttribute<ApiOndeleteAttribute>() == null
        && method.DeclaringType != typeof(OdooModel)
        && method.DeclaringType != typeof(object);

    public virtual object? ExecuteMethod(string methodName, int id, Dictionary<string, object> values, ModelRegistry registry, Func<object?>? next = null)
    {
        var method = this.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (method != null && IsCallableAction(method))
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

// Odoo itself only ever supports PostgreSQL (see odoo/sql_db.py) - this mirrors that:
// one provider, no abstraction for providers that will never be swapped in.
public class DatabaseAdapter
{
    public string ConnectionString { get; }
    private readonly ILogger<DatabaseAdapter> _logger;

    public DatabaseAdapter(AppSettingsConfig config, ILogger<DatabaseAdapter> logger)
    {
        _logger = logger;
        ConnectionString = config.ConnectionString;
        _logger.LogInformation("Database Adapter active: PostgreSQL");
    }

    public IDbConnection CreateConnection() => new NpgsqlConnection(ConnectionString);

    public string Sanitize(string modelName) => modelName.Replace(".", "_").ToLower();

    // Doubling an embedded quote is how Postgres escapes it inside a quoted identifier - this
    // alone isn't the injection fix (identifiers here should never be attacker-controlled once
    // ModelRegistry.EnsureModelExists validates `model` first), it's defense-in-depth.
    public string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    public string MapSqlType(FieldType type) => type switch
    {
        FieldType.Integer => "INT",
        FieldType.Float => "DOUBLE PRECISION",
        FieldType.Boolean => "BOOLEAN",
        FieldType.DateTime => "TIMESTAMP",
        FieldType.Many2one => "INT",
        _ => "TEXT"
    };

    // Odoo auto-generates a join table per many2many field (odoo/orm/fields_relational.py
    // Many2many._define_relation_table); we do the same, deriving the name from (model, field)
    // rather than adding new AddField parameters. Not safe for a self-referential m2m (model ==
    // relation would collide the two id columns) - not used anywhere in this codebase, so left
    // unhandled rather than adding untested complexity for it.
    public (string Table, string ColA, string ColB) ManyToManyTable(string model, string fieldName, string relation) =>
        ($"{Sanitize(model)}_{Sanitize(fieldName)}_rel", $"{Sanitize(model)}_id", $"{Sanitize(relation)}_id");

    public void AutoSyncPhysicalDatabase(Dictionary<string, Dictionary<string, FieldDef>> desiredModels, Dictionary<string, List<SqlConstraintDef>>? desiredConstraints = null)
    {
        using var conn = CreateConnection();
        conn.Open();

        foreach (var (modelName, fields) in desiredModels)
        {
            var tbl = Sanitize(modelName);
            conn.Execute($@"CREATE TABLE IF NOT EXISTS {Quote(tbl)} (id SERIAL PRIMARY KEY);");

            foreach (var (fname, fdef) in fields)
            {
                if (fname == "id" || fdef.Type == FieldType.One2many) continue;

                if (fdef.Type == FieldType.Many2many)
                {
                    var (joinTbl, colA, colB) = ManyToManyTable(modelName, fname, fdef.Relation!);
                    conn.Execute($@"CREATE TABLE IF NOT EXISTS {Quote(joinTbl)} ({Quote(colA)} INT NOT NULL, {Quote(colB)} INT NOT NULL, PRIMARY KEY ({Quote(colA)}, {Quote(colB)}));");
                    continue;
                }

                var colType = MapSqlType(fdef.Type);
                try { conn.Execute($"ALTER TABLE {Quote(tbl)} ADD COLUMN IF NOT EXISTS {Quote(fname)} {colType};"); } catch { }
            }

            if (desiredConstraints != null && desiredConstraints.TryGetValue(modelName, out var constraints))
            {
                foreach (var c in constraints)
                {
                    var cols = string.Join(", ", c.Fields.Select(Quote));
                    // Postgres has no "ADD CONSTRAINT IF NOT EXISTS" - swallow the duplicate-name
                    // error on every subsequent sync, same pattern as ADD COLUMN above.
                    try { conn.Execute($"ALTER TABLE {Quote(tbl)} ADD CONSTRAINT {Quote(c.Name)} UNIQUE ({cols});"); } catch { }
                }
            }
        }
    }

    public void DropTable(string modelName)
    {
        using var conn = CreateConnection();
        conn.Open();
        var tbl = Sanitize(modelName);
        try { conn.Execute($"DROP TABLE IF EXISTS {Quote(tbl)};"); } catch { }
    }

    public void DropColumn(string modelName, string columnName)
    {
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
    private readonly Dictionary<string, List<SqlConstraintDef>> _activeConstraints = new();
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
        var desiredConstraints = new Dictionary<string, List<SqlConstraintDef>>();

        foreach (var (modelName, modelDefs) in _registeredModelExtensions)
        {
            var merged = new Dictionary<string, FieldDef>
            {
                ["id"] = new FieldDef("id", FieldType.Integer, "ID", Module: "base"),
                // Universal audit/soft-delete columns every model gets, mirroring Odoo's
                // magic fields (odoo/orm/models.py MetaModel) - active_test default filtering
                // for `active` lives in SearchRead.
                ["active"] = new FieldDef("active", FieldType.Boolean, "Active", DefaultValue: true, Module: "base"),
                ["create_date"] = new FieldDef("create_date", FieldType.DateTime, "Created On", Readonly: true, Module: "base"),
                ["write_date"] = new FieldDef("write_date", FieldType.DateTime, "Last Updated On", Readonly: true, Module: "base"),
                ["create_uid"] = new FieldDef("create_uid", FieldType.Many2one, "Created By", Relation: "res.users", Readonly: true, Module: "base"),
                ["write_uid"] = new FieldDef("write_uid", FieldType.Many2one, "Last Updated By", Relation: "res.users", Readonly: true, Module: "base")
            };

            var constraints = new List<SqlConstraintDef>();
            foreach (var def in modelDefs)
            {
                foreach (var (fName, fDef) in def.Fields) merged[fName] = fDef;
                constraints.AddRange(def.SqlConstraints);
            }
            desiredSchema[modelName] = merged;
            desiredConstraints[modelName] = constraints;
        }

        _activeSchema.Clear();
        _activeConstraints.Clear();
        foreach (var (m, f) in desiredSchema) _activeSchema[m] = new Dictionary<string, FieldDef>(f);
        foreach (var (m, c) in desiredConstraints) _activeConstraints[m] = c;
        _db.AutoSyncPhysicalDatabase(desiredSchema, desiredConstraints);
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
                _db.DropColumn(modelName, col);
            }
        }

        var recordsToPurge = _modelData.Where(d => d.Module == moduleName && d.Type == "record").ToList();
        foreach (var entry in recordsToPurge)
        {
            using var conn = _db.CreateConnection();
            conn.Open();
            var tbl = _db.Sanitize(entry.Model);
            conn.Execute($"DELETE FROM {_db.Quote(tbl)} WHERE id = @id", new { id = int.Parse(entry.ResId) });
        }
        _modelData.RemoveAll(d => d.Module == moduleName);
    }

    public Dictionary<string, FieldDef> GetFields(string model) =>
        _activeSchema.TryGetValue(model, out var fields) ? fields : new();

    // Translates a raw Postgres unique-violation into the friendly message declared via
    // AddSqlConstraint, matching odoo/orm/models.py's `_sql_error_to_message`.
    private ValidationError TranslateUniqueViolation(string model, PostgresException pgEx)
    {
        var message = _activeConstraints.TryGetValue(model, out var constraints)
            ? constraints.FirstOrDefault(c => c.Name == pgEx.ConstraintName)?.Message
            : null;
        return new ValidationError(message ?? "This value must be unique; a record with it already exists.");
    }

    // `model` comes straight from the RPC request body (UniversalRpcController.CallKw), and the
    // table name below is interpolated into raw SQL (Dapper can't parameterize identifiers).
    // Column names are already safe (filtered against the known field set), but without this
    // check an attacker-chosen `model` string would be spliced directly into FROM/INTO/UPDATE/
    // DELETE - e.g. model = "x\" WHERE 1=1; DROP TABLE res_users;--" breaks out of the quoted
    // identifier. Validating against the real, server-defined model registry closes that off
    // at the one place every CRUD entry point routes through, matching Odoo's own
    // odoo/service/model.py execute_cr: "if recs is None: raise UserError(...)".
    private void EnsureModelExists(string model)
    {
        if (!_activeSchema.ContainsKey(model))
            throw new UserError($"Object {model} doesn't exist");
    }

    public List<Dictionary<string, object>> SearchRead(string model, List<string>? fields = null, List<List<object>>? domain = null)
    {
        EnsureModelExists(model);
        var modelFields = GetFields(model);
        List<Dictionary<string, object>> records;

        using (var conn = _db.CreateConnection())
        {
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
                    var rStr = rVal.ToString()!;
                    switch (op)
                    {
                        case "=": return rStr.Equals(val?.ToString(), StringComparison.OrdinalIgnoreCase);
                        case "!=": return !rStr.Equals(val?.ToString(), StringComparison.OrdinalIgnoreCase);
                        case "like": return rStr.Contains(val?.ToString() ?? "", StringComparison.Ordinal);
                        case "not like": return !rStr.Contains(val?.ToString() ?? "", StringComparison.Ordinal);
                        case "ilike": return rStr.Contains(val?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
                        case "not ilike": return !rStr.Contains(val?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
                        case "=like": return rStr.Equals(val?.ToString(), StringComparison.Ordinal);
                        case "not =like": return !rStr.Equals(val?.ToString(), StringComparison.Ordinal);
                        case "=ilike": return rStr.Equals(val?.ToString(), StringComparison.OrdinalIgnoreCase);
                        case "not =ilike": return !rStr.Equals(val?.ToString(), StringComparison.OrdinalIgnoreCase);
                        case ">": return Convert.ToDouble(rVal) > Convert.ToDouble(val);
                        case ">=": return Convert.ToDouble(rVal) >= Convert.ToDouble(val);
                        case "<": return Convert.ToDouble(rVal) < Convert.ToDouble(val);
                        case "<=": return Convert.ToDouble(rVal) <= Convert.ToDouble(val);
                        case "in": return val is System.Collections.IEnumerable inList and not string && inList.Cast<object?>().Any(x => x?.ToString() == rStr);
                        case "not in": return val is System.Collections.IEnumerable notInList and not string && !notInList.Cast<object?>().Any(x => x?.ToString() == rStr);
                        default: return true;
                    }
                });
            }
        }

        // Odoo's active_test: rows with active=false are hidden unless the caller's domain
        // already asks about `active` explicitly (e.g. [['active','=',false]] to see archived
        // ones). Legacy rows with no `active` column value yet (NULL) count as active.
        if (modelFields.ContainsKey("active") && (domain == null || !domain.Any(c => c.Count > 0 && c[0]?.ToString() == "active")))
        {
            filtered = filtered.Where(r => !(r.TryGetValue("active", out var a) && a is false));
        }

        return filtered.Select(r =>
        {
            var projection = new Dictionary<string, object>();
            var targetFields = (fields == null || fields.Count == 0) ? modelFields.Keys.ToList() : fields;

            foreach (var f in targetFields)
            {
                modelFields.TryGetValue(f, out var fdef);

                // Many2many/One2many data never lives as a column on this row at all (it's in a
                // join table / the related model), so these must be resolved before the raw
                // r.TryGetValue(f, ...) column lookup below, not inside it.
                if (fdef?.Type == FieldType.Many2many)
                {
                    projection[f] = GetMany2manyValues(model, f, fdef, Convert.ToInt32(r["id"]));
                }
                else if (fdef?.Type == FieldType.One2many)
                {
                    projection[f] = SearchRead(fdef.Relation!, null, [[fdef.InverseName!, "=", Convert.ToInt32(r["id"])]]);
                }
                else if (r.TryGetValue(f, out var val))
                {
                    if (fdef?.Type == FieldType.Many2one && val != null)
                    {
                        var relId = Convert.ToInt32(val);
                        var relSearch = SearchRead(fdef.Relation!, ["id", "name"], [["id", "=", relId]]);
                        var relName = relSearch.Count > 0 && relSearch[0].TryGetValue("name", out var n) ? n.ToString() : $"#{relId}";
                        projection[f] = new object[] { relId, relName ?? "" };
                    }
                    else projection[f] = val;
                }
                else projection[f] = null!;
            }
            if (r.TryGetValue("id", out var idVal)) projection["id"] = idVal;
            return projection;
        }).ToList();
    }

    // ModelRegistry is a DI singleton (shared across every concurrent request), so a
    // transaction can never be stored as instance state - that would let one request's rollback
    // affect another's in-flight writes. It's passed explicitly through each call instead.
    // Opens one connection+transaction, runs `work`, commits on success, rolls back and
    // rethrows on any exception (including one raised deep inside Create/Write's own validation).
    public T RunInTransaction<T>(Func<IDbTransaction, T> work)
    {
        using var conn = _db.CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            var result = work(tx);
            tx.Commit();
            return result;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static (IDbConnection Conn, bool Owned) GetConnection(DatabaseAdapter db, IDbTransaction? tx)
    {
        if (tx?.Connection is { } existing) return (existing, false);
        var conn = db.CreateConnection();
        conn.Open();
        return (conn, true);
    }

    private List<object[]> GetMany2manyValues(string model, string fieldName, FieldDef fdef, int recordId)
    {
        var (joinTbl, colA, colB) = _db.ManyToManyTable(model, fieldName, fdef.Relation!);
        List<int> ids;
        using (var conn = _db.CreateConnection())
        {
            conn.Open();
            ids = conn.Query<int>($"SELECT {_db.Quote(colB)} FROM {_db.Quote(joinTbl)} WHERE {_db.Quote(colA)} = @id", new { id = recordId }).ToList();
        }
        if (ids.Count == 0) return new();

        var related = SearchRead(fdef.Relation!, ["id", "name"], [["id", "in", ids.Cast<object>().ToList()]]);
        return related.Select(rr => new object[] { rr["id"], rr.TryGetValue("name", out var n) ? n : $"#{rr["id"]}" }).ToList();
    }

    // Client sends either raw ids ([1,2,3]) or, if round-tripping a previous read, [id,name]
    // tuples ([[1,"Admin"],[2,"User"]]) - accept both.
    private static List<int> ExtractMany2manyIds(object? value) => value switch
    {
        System.Collections.IEnumerable list and not string => list.Cast<object?>()
            .Select(item => item is System.Collections.IEnumerable inner and not string ? inner.Cast<object?>().FirstOrDefault() : item)
            .Where(x => x != null)
            .Select(x => Convert.ToInt32(x))
            .ToList(),
        _ => new List<int>()
    };

    private void SyncMany2many(string model, string fieldName, FieldDef fdef, int recordId, object? rawValue, IDbTransaction? tx = null)
    {
        var ids = ExtractMany2manyIds(rawValue);
        var (joinTbl, colA, colB) = _db.ManyToManyTable(model, fieldName, fdef.Relation!);

        var (conn, owned) = GetConnection(_db, tx);
        try
        {
            conn.Execute($"DELETE FROM {_db.Quote(joinTbl)} WHERE {_db.Quote(colA)} = @id", new { id = recordId }, tx);
            foreach (var relId in ids)
                conn.Execute($"INSERT INTO {_db.Quote(joinTbl)} ({_db.Quote(colA)}, {_db.Quote(colB)}) VALUES (@a, @b)", new { a = recordId, b = relId }, tx);
        }
        finally { if (owned) conn.Dispose(); }
    }

    // Mirrors what real Odoo surfaces as a friendly ValidationError instead of a raw
    // Postgres NOT NULL / constraint failure (odoo/orm/fields.py `required`, fields_selection.py).
    private static void ValidateFieldValues(Dictionary<string, FieldDef> modelFields, Dictionary<string, object> record, bool requireAll)
    {
        foreach (var (fname, fdef) in modelFields)
        {
            var touched = record.TryGetValue(fname, out var v);
            var isEmpty = !touched || v == null || (v is string s && s.Length == 0);

            if (fdef.Required && isEmpty && (requireAll || touched))
                throw new ValidationError($"{fdef.String} is required.");

            if (touched && v != null && fdef.Selection is { Count: > 0 } options)
            {
                var vStr = v.ToString();
                if (!options.Any(o => o.Value == vStr))
                    throw new ValidationError($"'{vStr}' is not a valid value for {fdef.String}.");
            }
        }
    }

    public int Create(string model, Dictionary<string, object> values, IDbTransaction? tx = null, int? uid = null)
    {
        EnsureModelExists(model);
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

        // Server-set, never client-settable - always overwrite whatever the caller sent.
        var now = DateTime.UtcNow;
        if (modelFields.ContainsKey("create_date")) record["create_date"] = now;
        if (modelFields.ContainsKey("write_date")) record["write_date"] = now;
        // uid is null for system/internal calls (demo data, seeding) - leave create_uid/write_uid
        // unset (NULL) rather than faking an author, same as real Odoo has no user during bootstrap.
        if (uid != null && modelFields.ContainsKey("create_uid")) record["create_uid"] = uid.Value;
        if (uid != null && modelFields.ContainsKey("write_uid")) record["write_uid"] = uid.Value;

        ValidateFieldValues(modelFields, record, requireAll: true);

        if (_registeredModelExtensions.TryGetValue(model, out var extensions))
        {
            foreach (var ext in extensions)
            {
                ext.ComputeFields(record, this, _logger);
                ext.ValidateConstrains(record, this, _logger);
            }
        }

        int newId;

        var (conn, owned) = GetConnection(_db, tx);
        try
        {
            var tbl = _db.Sanitize(model);
            var cols = record.Keys.Where(k => k != "id" && modelFields.ContainsKey(k) && modelFields[k].Type is not (FieldType.One2many or FieldType.Many2many)).ToList();
            var colNames = string.Join(", ", cols.Select(c => _db.Quote(c)));
            var paramNames = string.Join(", ", cols.Select(c => "@" + c));

            var sql = $"INSERT INTO {_db.Quote(tbl)} ({colNames}) VALUES ({paramNames}) RETURNING id;";

            var parameters = new DynamicParameters();
            foreach (var col in cols) parameters.Add(col, record[col]);
            newId = conn.ExecuteScalar<int>(sql, parameters, tx);
        }
        catch (PostgresException pgEx) when (pgEx.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw TranslateUniqueViolation(model, pgEx);
        }
        finally { if (owned) conn.Dispose(); }

        foreach (var (fname, fdef) in modelFields)
        {
            if (fdef.Type == FieldType.Many2many && record.TryGetValue(fname, out var m2mVal))
                SyncMany2many(model, fname, fdef, newId, m2mVal, tx);
        }

        LogMessage(model, newId, "System", "Record created", "notification");
        _logger.LogInformation("Record #{Id} created on model {Model}", newId, model);
        return newId;
    }

    // Mirrors @api.model_create_multi (odoo/orm/decorators.py): create() accepts either a
    // single dict or a list of dicts. Each row still goes through the same validation/compute/
    // constrain pipeline as a lone Create() - this is just the batch entry point, not a bulk-
    // insert fast path (no need for one here at this project's scale). Wrapped in one
    // transaction so a failure partway through the batch rolls back everything already
    // inserted, instead of leaving earlier rows committed.
    public List<int> CreateMulti(string model, List<Dictionary<string, object>> valuesList, int? uid = null) =>
        RunInTransaction(tx => valuesList.Select(v => Create(model, v, tx, uid)).ToList());

    public bool Write(string model, int id, Dictionary<string, object> values, IDbTransaction? tx = null, int? uid = null)
    {
        EnsureModelExists(model);
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

        recordValues.Remove("create_date"); // set once at creation, never client-settable
        recordValues.Remove("create_uid"); // ditto
        if (modelFields.ContainsKey("write_date")) recordValues["write_date"] = DateTime.UtcNow;
        if (uid != null && modelFields.ContainsKey("write_uid")) recordValues["write_uid"] = uid.Value;

        ValidateFieldValues(modelFields, recordValues, requireAll: false);

        if (_registeredModelExtensions.TryGetValue(model, out var extensions))
        {
            foreach (var ext in extensions)
            {
                ext.ComputeFields(recordValues, this, _logger);
                ext.ValidateConstrains(recordValues, this, _logger);
            }
        }

        var (conn, owned) = GetConnection(_db, tx);
        int affected;
        try
        {
            var tbl = _db.Sanitize(model);
            var cols = recordValues.Keys.Where(k => k != "id" && modelFields.ContainsKey(k) && modelFields[k].Type is not (FieldType.One2many or FieldType.Many2many)).ToList();
            var setClause = string.Join(", ", cols.Select(c => $"{_db.Quote(c)} = @{c}"));

            var sql = $"UPDATE {_db.Quote(tbl)} SET {setClause} WHERE id = @id";
            var parameters = new DynamicParameters();
            parameters.Add("id", id);
            foreach (var col in cols) parameters.Add(col, recordValues[col]);

            affected = conn.Execute(sql, parameters, tx);
        }
        catch (PostgresException pgEx) when (pgEx.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw TranslateUniqueViolation(model, pgEx);
        }
        finally { if (owned) conn.Dispose(); }

        if (affected == 0)
            throw new MissingError($"Record #{id} on model '{model}' no longer exists.");

        foreach (var (fname, fdef) in modelFields)
        {
            if (fdef.Type == FieldType.Many2many && recordValues.TryGetValue(fname, out var m2mVal))
                SyncMany2many(model, fname, fdef, id, m2mVal, tx);
        }

        _logger.LogInformation("Record #{Id} updated on model {Model}", id, model);
        return true;
    }

    // Mirrors Odoo's copy()/duplicate: a shallow clone (child One2many lines are not copied,
    // matching a "new draft from this one" duplicate rather than a deep recursive clone).
    public int Copy(string model, int id, int? uid = null)
    {
        EnsureModelExists(model);
        var source = SearchRead(model, null, [["id", "=", id]]).FirstOrDefault()
            ?? throw new MissingError($"Record #{id} on model '{model}' no longer exists.");

        var modelFields = GetFields(model);
        var copyValues = new Dictionary<string, object>();
        foreach (var (fname, fdef) in modelFields)
        {
            if (fname is "id" or "create_date" or "write_date" or "create_uid" or "write_uid" || fdef.Type == FieldType.One2many) continue;
            if (!source.TryGetValue(fname, out var v) || v == null) continue;
            copyValues[fname] = fdef.Type == FieldType.Many2one && v is object[] arr ? arr[0] : v;
        }
        return Create(model, copyValues, uid: uid);
    }

    public bool Unlink(string model, int id, IDbTransaction? tx = null)
    {
        EnsureModelExists(model);
        if (_registeredModelExtensions.TryGetValue(model, out var extensions))
        {
            var records = SearchRead(model, null, [["id", "=", id]]);
            if (records.Count > 0)
            {
                foreach (var ext in extensions) ext.ValidateOndelete(records[0], this, _logger);
            }
        }

        // Clean up join-table rows: both this model's own many2many fields, and any OTHER
        // model's many2many field that references this record (e.g. deleting a res.groups row
        // must also drop it from every user's group_ids, or it'd dangle as an orphaned id).
        foreach (var (ownerModel, ownerFields) in _activeSchema)
        {
            foreach (var (fname, fdef) in ownerFields)
            {
                if (fdef.Type != FieldType.Many2many) continue;
                if (ownerModel != model && fdef.Relation != model) continue;

                var (joinTbl, colA, colB) = _db.ManyToManyTable(ownerModel, fname, fdef.Relation!);
                var (jconn, jowned) = GetConnection(_db, tx);
                try
                {
                    if (ownerModel == model) jconn.Execute($"DELETE FROM {_db.Quote(joinTbl)} WHERE {_db.Quote(colA)} = @id", new { id }, tx);
                    if (fdef.Relation == model) jconn.Execute($"DELETE FROM {_db.Quote(joinTbl)} WHERE {_db.Quote(colB)} = @id", new { id }, tx);
                }
                finally { if (jowned) jconn.Dispose(); }
            }
        }

        _logger.LogWarning("Record #{Id} deleted from model {Model}", id, model);
        var (conn, owned) = GetConnection(_db, tx);
        int affected;
        try
        {
            var tbl = _db.Sanitize(model);
            affected = conn.Execute($"DELETE FROM {_db.Quote(tbl)} WHERE id = @id", new { id }, tx);
        }
        finally { if (owned) conn.Dispose(); }

        if (affected == 0)
            throw new MissingError($"Record #{id} on model '{model}' no longer exists.");
        return true;
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
    public string? GetRawArch(string viewId) => _baseViews.TryGetValue(viewId, out var v) ? v.Arch : null;
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
                var modelFields = models.GetFields(model);
                var values = new Dictionary<string, object>();
                foreach (var fieldNode in rec.Elements("field"))
                {
                    var fname = fieldNode.Attribute("name")?.Value;
                    if (fname != null) values[fname] = ConvertXmlFieldValue(fieldNode.Value, modelFields.GetValueOrDefault(fname));
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

    // XML field text is always a string; Postgres columns are strictly typed (unlike the old
    // SQLite/InMemory paths, which silently coerced), so cast to the field's real type here.
    private static object ConvertXmlFieldValue(string raw, FieldDef? fdef) => fdef?.Type switch
    {
        FieldType.Integer or FieldType.Many2one => int.TryParse(raw, out var i) ? i : raw,
        FieldType.Float => double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : raw,
        FieldType.Boolean => raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1",
        _ => raw
    };
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

    // Returns null on success, or an error message when the change is blocked
    // (matches Odoo cascading dependency install / blocking uninstall of a depended-upon module).
    public string? SetModuleState(string technicalName, bool install)
    {
        if (!_discoveredManifests.TryGetValue(technicalName, out var manifest))
            return $"Module '{technicalName}' not found";

        if (install)
        {
            if (!manifest.Installable)
                return $"Module '{manifest.Name}' is not installable.";

            foreach (var dep in CollectTransitiveDeps(technicalName))
            {
                if (_discoveredManifests.TryGetValue(dep, out var depManifest) && depManifest.Installable)
                    depManifest.State = "installed";
            }
            manifest.State = "installed";
            InstallAutoInstallModules();
        }
        else
        {
            var dependents = _discoveredManifests.Values
                .Where(m => m.State == "installed" && m.TechnicalName != technicalName && m.Depends.Contains(technicalName))
                .Select(m => m.Name)
                .ToList();
            if (dependents.Count > 0)
                return $"Cannot uninstall '{manifest.Name}': required by {string.Join(", ", dependents)}";

            manifest.State = "uninstalled";
            _modelRegistry.DropModuleSchemaAndData(technicalName);
            _viewRegistry.RemoveModuleViews(technicalName);
            _menuRegistry.RemoveModuleMenus(technicalName);
        }

        SavePersistedState();
        RebuildActiveState();
        return null;
    }

    private IEnumerable<string> CollectTransitiveDeps(string technicalName)
    {
        var seen = new HashSet<string>();
        void Walk(string name)
        {
            if (!_discoveredManifests.TryGetValue(name, out var m)) return;
            foreach (var dep in m.Depends)
            {
                if (dep == "base") continue;
                if (seen.Add(dep)) Walk(dep);
            }
        }
        Walk(technicalName);
        return seen;
    }

    // Odoo's "glue module" pattern (odoo/modules/module.py manifest `auto_install`): a module
    // installs itself automatically the moment every one of its dependencies is installed.
    private void InstallAutoInstallModules()
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var m in _discoveredManifests.Values)
            {
                if (m.State == "installed" || !m.AutoInstall || !m.Installable) continue;
                if (m.Depends.All(d => d == "base" || (_discoveredManifests.TryGetValue(d, out var dm) && dm.State == "installed")))
                {
                    m.State = "installed";
                    changed = true;
                }
            }
        } while (changed);
    }

    public void RebuildActiveState()
    {
        _modelRegistry.ClearModels();
        _viewRegistry.Clear();
        _menuRegistry.Clear();

        _modelRegistry.Register(new BasePartnerModel());
        _modelRegistry.Register(new BaseUserModel());
        _modelRegistry.Register(new BaseGroupModel());

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
        }

        _modelRegistry.AutoSyncSchema();

        foreach (var (techName, manifest) in _discoveredManifests)
        {
            if (manifest.State != "installed") continue;

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
    }
}
