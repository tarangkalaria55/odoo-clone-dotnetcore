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

        var groupIds = user.TryGetValue("group_ids", out var g) ? g?.ToString() ?? "" : "";
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user["id"].ToString()!),
            new(ClaimTypes.Name, user["login"]?.ToString() ?? req.Login),
            new("full_name", user["name"]?.ToString() ?? req.Login),
            new("groups", groupIds)
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        var isAdmin = groupIds.Split(',').Contains(SecurityGroups.Admin);
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
                        domain = JsonSerializer.Deserialize<List<List<object>>>(domElem.GetRawText());

                    var results = models.SearchRead(req.Model, fieldList, domain);
                    if (req.Model == "res.users") foreach (var r in results) r.Remove("password_hash");
                    return Ok(results);

                case "onchange":
                    var changedField = req.Args[0].GetString()!;
                    var valuesMap = DeserializeValues(req.Args[1]);
                    return Ok(new { value = models.ExecuteOnChange(req.Model, changedField, valuesMap) });

                case "create":
                    var createValues = DeserializeValues(req.Args[0]);
                    return Ok(models.Create(req.Model, createValues));

                case "write":
                    var id = req.Args[0].GetInt32();
                    var writeValues = DeserializeValues(req.Args[1]);
                    return Ok(models.Write(req.Model, id, writeValues));

                case "unlink":
                    return Ok(models.Unlink(req.Model, req.Args[0].GetInt32()));

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
