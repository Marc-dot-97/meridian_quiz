using System.Security.Claims;
using Meridian.Api.Data;
using Meridian.Api.Data.Entities;
using Meridian.Shared.DTOs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Meridian.Api.Features.Accounts;

/// <summary>
/// Session endpoints only. Sign-in itself is Microsoft Entra ID (see EntraProvisioning and the /auth/login
/// endpoint in Program.cs): there is no password, no self-registration and no password recovery in Meridian.
/// </summary>
[ApiController, Route("api/account")]
public sealed class AccountsController(MeridianDbContext db) : ControllerBase
{
    public static ClaimsPrincipal Principal(User u) => new(new ClaimsIdentity(new[] {
        new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()), new Claim("meridian_user_id", u.Id.ToString()), new Claim(ClaimTypes.Email, u.Email),
        new Claim(ClaimTypes.Name, u.DisplayName), new Claim(ClaimTypes.Role, u.AuthRole)
    }, CookieAuthenticationDefaults.AuthenticationScheme));
    private static AccountDto Dto(User u) => new(u.Id, u.Email, u.DisplayName, u.AuthRole);

    [Authorize, HttpGet("me")]
    public async Task<ActionResult<AccountDto>> Me()
    {
        var id = ulong.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == id);
        return Ok(Dto(user));
    }

    /// <summary>Ends the Meridian session only. The person stays signed in to Microsoft, as with the CRM.</summary>
    [Authorize, HttpPost("logout")]
    public async Task<IActionResult> Logout() { await HttpContext.SignOutAsync(); return NoContent(); }
}
