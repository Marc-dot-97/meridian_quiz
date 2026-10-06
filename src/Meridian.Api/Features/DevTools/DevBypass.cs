using System.Net;
using System.Net.NetworkInformation;
using Meridian.Api.Data;
using Meridian.Api.Data.Entities;
using Meridian.Api.Features.Accounts;
using Meridian.Shared.DTOs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Features.DevTools;

/// <summary>
/// DEV BYPASS: one-click sign-in for local and dev-server testing.
/// It is enabled only when ALL of the following are true:
///   1. Config "DevBypass:Enabled" is true (false in appsettings.json, true in appsettings.Development.json).
///   2. ASPNETCORE_ENVIRONMENT is not "Production".
///   3. Neither this machine, the database server nor the request's local address is a production host
///      (10.10.1.2 is hard-coded; "DevBypass:BlockedHosts" can add more but cannot remove it).
/// Otherwise every /api/dev endpoint returns 404 and the login page hides the button.
/// </summary>
public sealed class DevBypassState
{
    private static readonly string[] HardBlockedHosts = ["10.10.1.2"];

    public bool Enabled { get; private init; }
    public string Reason { get; private init; } = "";
    public IReadOnlySet<string> BlockedHosts { get; private init; } = new HashSet<string>();

    public static DevBypassState Evaluate(IWebHostEnvironment env, IConfiguration config, string connectionString)
    {
        var blocked = new HashSet<string>(HardBlockedHosts, StringComparer.OrdinalIgnoreCase);
        foreach (var h in config.GetSection("DevBypass:BlockedHosts").Get<string[]>() ?? []) blocked.Add(h.Trim());

        DevBypassState Off(string why) => new() { Enabled = false, Reason = why, BlockedHosts = blocked };

        if (!config.GetValue("DevBypass:Enabled", false)) return Off("DevBypass:Enabled is not true");
        if (env.IsProduction()) return Off("environment is Production");

        var dbHost = new MySqlConnector.MySqlConnectionStringBuilder(connectionString).Server;
        if (dbHost.Split(',').Any(h => blocked.Contains(h.Trim()))) return Off($"database server {dbHost} is a production host");

        try
        {
            var local = NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address.ToString());
            var hit = local.FirstOrDefault(blocked.Contains);
            if (hit is not null) return Off($"this machine has production address {hit}");
        }
        catch (NetworkInformationException) { return Off("could not verify this machine's addresses"); }

        return new() { Enabled = true, Reason = $"environment {env.EnvironmentName}", BlockedHosts = blocked };
    }

    /// <summary>Per-request re-check: never allow when the request arrived on a production address.</summary>
    public bool AllowsRequest(HttpContext http)
    {
        if (!Enabled) return false;
        var localIp = http.Connection.LocalIpAddress;
        if (localIp is not null)
        {
            var ip = localIp.IsIPv4MappedToIPv6 ? localIp.MapToIPv4() : localIp;
            if (BlockedHosts.Contains(ip.ToString())) return false;
        }
        var host = http.Request.Host.Host;
        return !BlockedHosts.Contains(host);
    }
}

public sealed record DevBypassStatusDto(bool Enabled);
public sealed class DevLoginRequest { public string? Email { get; set; } }

[ApiController, Route("api/dev")]
public sealed class DevBypassController(
    DevBypassState state, MeridianDbContext db, IPasswordHasher<User> hasher, ILogger<DevBypassController> log,
    Meridian.Api.Features.Reports.EmployeeDirectoryStore directory) : ControllerBase
{
    public const string DevEmail = "dev@meridian.local";

    [AllowAnonymous, HttpGet("status")]
    public ActionResult<DevBypassStatusDto> Status() =>
        state.AllowsRequest(HttpContext) ? Ok(new DevBypassStatusDto(true)) : NotFound();

    /// <summary>Signs in as the given active user (impersonation), or as the shared Dev Admin user when no email is sent.</summary>
    [AllowAnonymous, HttpPost("login"), EnableRateLimiting("accounts")]
    public async Task<ActionResult<AccountDto>> Login(DevLoginRequest r)
    {
        if (!state.AllowsRequest(HttpContext)) return NotFound();

        User? user;
        if (!string.IsNullOrWhiteSpace(r.Email))
        {
            var email = r.Email.Trim().ToLowerInvariant();
            user = await db.Users.SingleOrDefaultAsync(u => u.Email == email);
            if (user?.IsActive != true || string.IsNullOrEmpty(user.PasswordHash))
                return NotFound(new { message = "No active account with that email." });
        }
        else
        {
            user = await db.Users.SingleOrDefaultAsync(u => u.Email == DevEmail) ?? await CreateDevUser();
        }

        log.LogWarning("DEV BYPASS sign-in as {Email} from {Ip}", user.Email, HttpContext.Connection.RemoteIpAddress);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            AccountsController.Principal(user), new AuthenticationProperties { IsPersistent = false });
        return Ok(new AccountDto(user.Id, user.Email, user.DisplayName, user.AuthRole));
    }

    /// <summary>DEV SEED: adds dummy users, quizzes, attempts and surveys. reset=true removes previous seed data first.</summary>
    [AllowAnonymous, HttpPost("seed"), EnableRateLimiting("accounts")]
    public async Task<ActionResult<SeedResult>> Seed([FromQuery] bool reset, CancellationToken ct)
    {
        if (!state.AllowsRequest(HttpContext)) return NotFound();
        if (reset) await DevSeeder.ClearAsync(db, log, ct, directory);
        return Ok(await DevSeeder.SeedAsync(db, hasher, log, ct, directory));
    }

    /// <summary>DEV SEED: removes only seed-tagged data.</summary>
    [AllowAnonymous, HttpPost("seed/clear"), EnableRateLimiting("accounts")]
    public async Task<ActionResult<SeedResult>> ClearSeed(CancellationToken ct)
    {
        if (!state.AllowsRequest(HttpContext)) return NotFound();
        return Ok(await DevSeeder.ClearAsync(db, log, ct, directory));
    }

    private async Task<User> CreateDevUser()
    {
        var now = DateTime.UtcNow;
        var user = new User
        {
            Email = DevEmail, UserName = "dev-bypass", Administrators = "",
            FirstName = "Dev", LastName = "Admin", DisplayName = "Dev Admin",
            Department = "Development", UserRole = "Developer", LineManager = "",
            AuthRole = "Admin", IsActive = true, CreatedAt = now, UpdatedAt = now
        };
        // Random unguessable password: the account can only be entered through the bypass.
        user.PasswordHash = hasher.HashPassword(user, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}
