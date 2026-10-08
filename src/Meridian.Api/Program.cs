using System.Security.Claims;
using System.Threading.RateLimiting;
using Meridian.Api.Data;
using Meridian.Api.Data.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MySql.EntityFrameworkCore.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddHostedService<Meridian.Api.Features.Retention.RetentionWorker>();
var connectionString = builder.Configuration.GetConnectionString("MeridianDb")
    ?? throw new InvalidOperationException("Connection string 'MeridianDb' was not found.");
builder.Services.AddDbContext<MeridianDbContext>(o => o.UseMySQL(connectionString));
// Only the dev tools (local dev seed / dev bypass) still hash passwords; real users sign in with Microsoft.
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();

// Data Protection keys sign the session cookie. On the server they MUST live in a persistent folder
// (DataProtection:KeysPath), otherwise every app-pool recycle signs everybody out.
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Meridian");
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath)) dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// --- Authentication: Microsoft Entra ID, same tenant as the CRM ---
// The cookie holds the Meridian session. OpenIdConnect is only used to go to Microsoft and come back with an identity,
// which EntraProvisioning then matches to an active employee. If AzureAd is not configured (local development only),
// the Microsoft button is unavailable and only the dev bypass works.
var azure = builder.Configuration.GetSection("AzureAd");
bool Configured(string? v) => !string.IsNullOrWhiteSpace(v) && !v.StartsWith("pending-configuration", StringComparison.OrdinalIgnoreCase);
var entraConfigured = Configured(azure["ClientId"]) && Configured(azure["ClientSecret"]) && Configured(azure["TenantId"]);
var auth = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "__Host-Meridian.Session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Lax;   // Lax (not Strict): the cookie must survive the redirect back from Microsoft
    o.Cookie.Path = "/";
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = false;
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    o.Events.OnValidatePrincipal = async c =>
    {
        var db = c.HttpContext.RequestServices.GetRequiredService<MeridianDbContext>();
        if (!ulong.TryParse(c.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
        { c.RejectPrincipal(); return; }
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id);
        if (user?.IsActive != true)
        { c.RejectPrincipal(); await c.HttpContext.SignOutAsync(); return; }
        // Reload the role from MySQL on every request; nothing from the browser can raise it.
        c.ReplacePrincipal(Meridian.Api.Features.Accounts.AccountsController.Principal(user));
    };
});
if (entraConfigured)
{
    auth.AddOpenIdConnect("EntraID", o =>
    {
        o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.Authority = $"{azure["Instance"] ?? "https://login.microsoftonline.com/"}{azure["TenantId"]}/v2.0";
        o.ClientId = azure["ClientId"];
        o.ClientSecret = azure["ClientSecret"];
        o.CallbackPath = azure["CallbackPath"] ?? "/signin-oidc";
        o.ResponseType = "code";
        o.ResponseMode = "query";   // a plain GET back from Microsoft (keeps the custom anti-forgery header rule for POSTs intact)
        o.SaveTokens = false;
        o.Scope.Clear();
        o.Scope.Add("openid");
        o.Scope.Add("profile");
        o.Scope.Add("email");
        o.Events.OnTokenValidated = async ctx =>
        {
            var user = await Meridian.Api.Features.Accounts.EntraProvisioning.SignInAsync(ctx.HttpContext, ctx.Principal!, ctx.HttpContext.RequestAborted);
            if (user is null)
            {
                ctx.HandleResponse();
                ctx.Response.Redirect("/login?error=noaccess");
                return;
            }
            ctx.Principal = Meridian.Api.Features.Accounts.AccountsController.Principal(user);
        };
        o.Events.OnRemoteFailure = ctx =>
        {
            ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Meridian.SignIn")
                .LogWarning(ctx.Failure, "Microsoft sign-in failed");
            ctx.HandleResponse();
            ctx.Response.Redirect("/login?error=failed");
            return Task.CompletedTask;
        };
    });
}
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("accounts", c => RateLimitPartition.GetFixedWindowLimiter(
        c.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
// DEV BYPASS: decided once at startup; off unless config + environment + host checks all pass.
var devBypass = Meridian.Api.Features.DevTools.DevBypassState.Evaluate(builder.Environment, builder.Configuration, connectionString);
builder.Services.AddSingleton(devBypass);
// Reports: employee list (line managers / HR), access rules and PDF fonts.
var directoryOptions = new Meridian.Api.Features.Reports.DirectorySourceOptions(
    builder.Configuration["Directory:Source"] ?? "local",
    builder.Configuration["Directory:AdminDatabase"] ?? "optimum_admin",
    builder.Configuration["Directory:ConnectionString"] ?? "");
var directoryStore = new Meridian.Api.Features.Reports.EmployeeDirectoryStore(connectionString, directoryOptions);
builder.Services.AddSingleton(directoryStore);
builder.Services.AddScoped<Meridian.Api.Features.Reports.ReportAccessService>();
PdfSharp.Fonts.GlobalFontSettings.FontResolver = new Meridian.Api.Features.Reports.MeridianFontResolver();
if (builder.Environment.IsProduction())
{
    // A production server that cannot sign people in, or that would use the wrong employee list, must not start quietly.
    if (!entraConfigured) throw new InvalidOperationException("Production needs AzureAd:TenantId, ClientId and ClientSecret (set them as environment variables, never in a file).");
    if (builder.Configuration.GetValue("DevBypass:Enabled", false)) throw new InvalidOperationException("DevBypass:Enabled must be false in Production. Remove it from the server configuration.");
    if (!directoryOptions.UsesCrm) throw new InvalidOperationException("Production must read employees from the CRM: set Directory:Source to \"crm\".");
    if (string.IsNullOrWhiteSpace(keysPath)) throw new InvalidOperationException("Production needs DataProtection:KeysPath (a persistent folder) so sessions survive restarts.");
}
var app = builder.Build();
if (devBypass.Enabled) app.Logger.LogWarning("DEV BYPASS ENABLED ({Reason}). Never deploy this configuration to production.", devBypass.Reason);
else app.Logger.LogInformation("Dev bypass disabled: {Reason}", devBypass.Reason);
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MeridianDbContext>();
    await db.Database.EnsureCreatedAsync();
    await Meridian.Api.Features.Retention.RetentionMaintenance.EnsureSchemaAsync(connectionString);
    await directoryStore.EnsureSchemaAsync();
    // Survey anonymity: split table + one-off copy of existing responses (idempotent).
    await Meridian.Api.Features.Surveys.SurveyAnonymity.EnsureSchemaAsync(connectionString);
    await Meridian.Api.Features.Surveys.SurveyAnonymity.BackfillAsync(db, directoryStore, app.Logger);
    await Meridian.Api.Features.Retention.RetentionMaintenance.CleanupAsync(connectionString, app.Logger, CancellationToken.None);
    // DEV SEED from the command line: dotnet run --launch-profile https -- --seed   (or --seed-reset / --seed-clear)
    if (args.Any(x => x is "--seed" or "--seed-reset" or "--seed-clear"))
    {
        if (!devBypass.Enabled)
            app.Logger.LogError("DEV SEED refused: dev bypass is disabled ({Reason}).", devBypass.Reason);
        else
        {
            if (args.Contains("--seed-reset") || args.Contains("--seed-clear"))
                await Meridian.Api.Features.DevTools.DevSeeder.ClearAsync(db, app.Logger, CancellationToken.None, directoryStore);
            if (!args.Contains("--seed-clear"))
            {
                var seed = await Meridian.Api.Features.DevTools.DevSeeder.SeedAsync(db,
                    scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>(), app.Logger, CancellationToken.None, directoryStore);
                if (!seed.Seeded) app.Logger.LogWarning("DEV SEED: {Message}", seed.Message);
            }
        }
    }
}
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Request failed");
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { message = "The request could not be completed. Please try again." });
    }
});
app.UseHttpsRedirection();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseRouting();
// Custom header forces a CORS preflight for cross-origin writes. Reject untrusted origins too.
app.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method)
        || HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method))
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (context.Request.Headers["X-Meridian-Client"] != "web"
            || (!string.IsNullOrEmpty(origin) && !string.Equals(origin, $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase)))
        { context.Response.StatusCode = 403; return; }
    }
    await next();
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
// --- Sign-in endpoints ---
// Starts the Microsoft sign-in. returnUrl is only used inside this site: it must start with a single "/" (never "//" or "/\\",
// which browsers treat as another host).
app.MapGet("/auth/login", (string? returnUrl) =>
{
    if (!entraConfigured) return Results.Problem("Microsoft sign-in is not configured on this server.", statusCode: 503);
    var target = "/";
    if (!string.IsNullOrWhiteSpace(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\")
        && !returnUrl.StartsWith("/login", StringComparison.OrdinalIgnoreCase))
        target = returnUrl;
    return Results.Challenge(new AuthenticationProperties { RedirectUri = target }, ["EntraID"]);
}).AllowAnonymous();
// crmUrl: the link on the sign-in page to the other system (LoginLinks:CrmUrl); only ever a plain http(s) address.
var crmLoginLink = builder.Configuration["LoginLinks:CrmUrl"]?.Trim().TrimEnd('/');
if (!Uri.TryCreate(crmLoginLink, UriKind.Absolute, out var crmLinkUri) || (crmLinkUri.Scheme != Uri.UriSchemeHttp && crmLinkUri.Scheme != Uri.UriSchemeHttps))
    crmLoginLink = null;
app.MapGet("/auth/status", () => Results.Ok(new { microsoft = entraConfigured, crmUrl = crmLoginLink })).AllowAnonymous();
app.MapControllers();
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
app.Run();
