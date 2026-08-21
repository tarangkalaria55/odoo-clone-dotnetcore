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
