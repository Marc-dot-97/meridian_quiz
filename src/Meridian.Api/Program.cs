using System.Security.Claims;
using System.Threading.RateLimiting;
using Meridian.Api.Data;
using Meridian.Api.Data.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "__Host-Meridian.Simple.Session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Strict;
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
        if (user?.IsActive != true || string.IsNullOrEmpty(user.PasswordHash))
        { c.RejectPrincipal(); await c.HttpContext.SignOutAsync(); return; }
        // Reload permissions from MySQL; browser-supplied job titles never grant access.
        c.ReplacePrincipal(Meridian.Api.Features.Accounts.AccountsController.Principal(user));
    };
});
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
var directoryStore = new Meridian.Api.Features.Reports.EmployeeDirectoryStore(connectionString);
builder.Services.AddSingleton(directoryStore);
builder.Services.AddScoped<Meridian.Api.Features.Reports.ReportAccessService>();
PdfSharp.Fonts.GlobalFontSettings.FontResolver = new Meridian.Api.Features.Reports.MeridianFontResolver();
var database = new MySqlConnector.MySqlConnectionStringBuilder(connectionString).Database;
if (database != "meridian_simple")
    throw new InvalidOperationException("Use the fresh database meridian_simple. Existing Meridian databases are not modified.");
var app = builder.Build();
if (devBypass.Enabled) app.Logger.LogWarning("DEV BYPASS ENABLED ({Reason}). Never deploy this configuration to production.", devBypass.Reason);
else app.Logger.LogInformation("Dev bypass disabled: {Reason}", devBypass.Reason);
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MeridianDbContext>();
    await db.Database.EnsureCreatedAsync();
    await Meridian.Api.Features.Retention.RetentionMaintenance.EnsureSchemaAsync(connectionString);
    await directoryStore.EnsureSchemaAsync();
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
app.MapControllers();
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
app.Run();
