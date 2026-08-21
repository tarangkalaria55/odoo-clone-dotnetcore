using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text.Json;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Core.OdooEngine;

public record CallKwRequest(string Model, string Method, JsonElement Args, JsonElement Kwargs);
public record LoginRequest(string Login, string Password);

[ApiController]
[Authorize]
public class UniversalRpcController(
    ModelRegistry models,
    ViewRegistry views,
    MenuRegistry menus,
    OdooModuleLifecycleManager lifecycleManager,
    ILogger<UniversalRpcController> logger) : ControllerBase
{
    private static readonly HashSet<string> AdminOnlyModels = new() { "res.users", "res.groups" };

    private int? CurrentUserId => int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    private static object? UnwrapJson(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt32(out var i) ? i : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => el.EnumerateArray().Select(UnwrapJson).ToArray(),
        JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => UnwrapJson(p.Value)),
        _ => null
    };

    private static Dictionary<string, object> DeserializeValues(JsonElement el)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(el.GetRawText())!;
        return raw.ToDictionary(kv => kv.Key, kv => UnwrapJson(kv.Value)!);
    }

    private static List<List<object>> DeserializeDomain(JsonElement el)
    {
        var raw = JsonSerializer.Deserialize<List<List<JsonElement>>>(el.GetRawText())!;
        return raw.Select(clause => clause.Select(e => UnwrapJson(e)!).ToList()).ToList();
    }

    [AllowAnonymous]
    [HttpPost("/web/session/login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var users = models.SearchRead("res.users", null, [["login", "=", req.Login]]);
        var user = users.FirstOrDefault();
        var hash = user != null && user.TryGetValue("password_hash", out var h) ? h?.ToString() : null;

        if (user == null || string.IsNullOrEmpty(hash) || !PasswordHasher.Verify(req.Password, hash))
        {
            logger.LogWarning("Failed login attempt for {Login}", req.Login);
            return Unauthorized(new { error = "Invalid login or password" });
        }

        // group_ids is a many2many: SearchRead returns [[id,name], ...] tuples for it.
        var groupNames = user.TryGetValue("group_ids", out var g) && g is List<object[]> groups
            ? string.Join(",", groups.Select(t => t[1]?.ToString()))
            : "";
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user["id"].ToString()!),
            new(ClaimTypes.Name, user["login"]?.ToString() ?? req.Login),
            new("full_name", user["name"]?.ToString() ?? req.Login),
            new("groups", groupNames)
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        var isAdmin = groupNames.Split(',').Contains(SecurityGroups.Admin);
        return Ok(new { uid = user["id"], login = user["login"], name = user["name"], is_admin = isAdmin });
    }

    [HttpPost("/web/session/logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(new { status = "success" });
    }

    [HttpGet("/web/session/whoami")]
    public IActionResult WhoAmI() => Ok(new
    {
        uid = User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
        login = User.Identity?.Name,
        name = User.FindFirst("full_name")?.Value,
        is_admin = SecurityGroups.IsAdmin(User)
    });

    [HttpGet("/web/session/modules")]
    public IActionResult GetMenus() => Ok(menus.GetMenus());

    [HttpGet("/web/apps/list")]
    public IActionResult GetApps() => Ok(lifecycleManager.GetDiscoveredModules());

    [HttpPost("/web/apps/toggle")]
    public IActionResult ToggleApp([FromBody] JsonElement body)
    {
        if (!SecurityGroups.IsAdmin(User)) return Forbid();

        var techName = body.GetProperty("technicalName").GetString()!;
        var install = body.GetProperty("install").GetBoolean();
        var error = lifecycleManager.SetModuleState(techName, install);
        if (error != null) return BadRequest(new { error });
        return Ok(new { status = "success" });
    }

    [HttpGet("/web/report/{model}/{template}/{id:int}")]
    public IActionResult RenderReport(string model, string template, int id)
    {
        var arch = views.GetRawArch(template);
        if (arch == null) return NotFound(new { error = $"Template '{template}' not found" });

        var records = models.SearchRead(model, null, [["id", "=", id]]);
        if (records.Count == 0) return NotFound(new { error = $"Record #{id} not found" });

        var ctx = new Dictionary<string, object?> { ["record"] = records[0] };
        return Content(QwebEngine.Render(arch, ctx), "text/html");
    }

    [HttpGet("/web/mail/chatter/{model}/{id}")]
    public IActionResult GetChatter(string model, int id) => Ok(models.GetMessages(model, id));

    [HttpPost("/web/mail/post")]
    public IActionResult PostMessage([FromBody] JsonElement body)
    {
        var model = body.GetProperty("model").GetString()!;
        var id = body.GetProperty("id").GetInt32();
        var msg = body.GetProperty("body").GetString()!;
        models.LogMessage(model, id, User.Identity?.Name ?? "System", msg, "comment");
        return Ok(new { status = "success" });
    }

    [HttpPost("/web/dataset/call_kw")]
    public IActionResult CallKw([FromBody] CallKwRequest req)
    {
        try
        {
            if (AdminOnlyModels.Contains(req.Model) &&
                req.Method is "create" or "write" or "unlink" &&
                !SecurityGroups.IsAdmin(User))
            {
                return Forbid();
            }

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
                        domain = DeserializeDomain(domElem);

                    var results = models.SearchRead(req.Model, fieldList, domain);
                    if (req.Model == "res.users") foreach (var r in results) r.Remove("password_hash");
                    return Ok(results);

                case "onchange":
                    var changedField = req.Args[0].GetString()!;
                    var valuesMap = DeserializeValues(req.Args[1]);
                    return Ok(new { value = models.ExecuteOnChange(req.Model, changedField, valuesMap) });

                case "create":
                    if (req.Args[0].ValueKind == JsonValueKind.Array)
                    {
                        var valuesList = req.Args[0].EnumerateArray().Select(DeserializeValues).ToList();
                        return Ok(models.CreateMulti(req.Model, valuesList, uid: CurrentUserId));
                    }
                    var createValues = DeserializeValues(req.Args[0]);
                    return Ok(models.Create(req.Model, createValues, uid: CurrentUserId));

                case "write":
                    var id = req.Args[0].GetInt32();
                    var writeValues = DeserializeValues(req.Args[1]);
                    return Ok(models.Write(req.Model, id, writeValues, uid: CurrentUserId));

                case "unlink":
                    return Ok(models.Unlink(req.Model, req.Args[0].GetInt32()));

                case "copy":
                    return Ok(models.Copy(req.Model, req.Args[0].GetInt32(), uid: CurrentUserId));

                case "name_search":
                    var allRelational = models.SearchRead(req.Model, ["id", "name"]);
                    var formatted = allRelational.Select(r => new object[] { r["id"], r.ContainsKey("name") ? r["name"] : $"#{r["id"]}" }).ToList();
                    return Ok(formatted);

                default:
                    var btnRecordId = req.Args.GetArrayLength() > 0 ? req.Args[0].GetInt32() : 0;
                    var currentValues = req.Args.GetArrayLength() > 1
                        ? DeserializeValues(req.Args[1])
                        : new Dictionary<string, object>();
                    
                    return Ok(models.ExecuteButtonAction(req.Model, req.Method, btnRecordId, currentValues) ?? new { status = "success" });
            }
        }
        catch (Exception raw)
        {
            // Reflection-invoked model methods (onchange/constrains/ondelete/action handlers)
            // wrap thrown exceptions in TargetInvocationException - unwrap once here.
            var ex = raw is TargetInvocationException { InnerException: { } inner } ? inner : raw;
            switch (ex)
            {
                case ValidationError ve:
                    logger.LogWarning("Validation error on {Model}: {Message}", req.Model, ve.Message);
                    return BadRequest(new { error = ve.Message });
                case UserError ue:
                    logger.LogWarning("User error on {Model}: {Message}", req.Model, ue.Message);
                    return UnprocessableEntity(new { error = ue.Message });
                case AccessError ae:
                    logger.LogWarning("Access denied on {Model}: {Message}", req.Model, ae.Message);
                    return StatusCode(403, new { error = ae.Message });
                case MissingError me:
                    logger.LogWarning("Missing record on {Model}: {Message}", req.Model, me.Message);
                    return NotFound(new { error = me.Message });
                default:
                    logger.LogError(ex, "RPC execution error on model {Model}, method {Method}", req.Model, req.Method);
                    return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}
