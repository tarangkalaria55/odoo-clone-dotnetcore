using Core.OdooEngine;
using Microsoft.AspNetCore.Authentication.Cookies;
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

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "odoo_session";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();

var jsConfigPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
if (!File.Exists(jsConfigPath)) jsConfigPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");

var tempLogger = LoggerFactory.Create(b => b.AddSimpleConsole()).CreateLogger("Bootstrap");
var jsConfig = AppSettingsConfig.LoadFromJsonFile(jsConfigPath, tempLogger);
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

SecuritySeed.EnsureSeedData(modelRegistry);

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
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();
