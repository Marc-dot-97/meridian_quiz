using System.Security.Claims;
using Meridian.Api.Data;
using Meridian.Api.Data.Entities;
using Meridian.Shared.DTOs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
namespace Meridian.Api.Features.Accounts;

[ApiController, Route("api/account")]
public sealed class AccountsController(MeridianDbContext db, IPasswordHasher<User> hasher) : ControllerBase
{
    public static ClaimsPrincipal Principal(User u) => new(new ClaimsIdentity(new[] {
        new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()), new Claim("meridian_user_id", u.Id.ToString()), new Claim(ClaimTypes.Email, u.Email),
        new Claim(ClaimTypes.Name, u.DisplayName), new Claim(ClaimTypes.Role, u.AuthRole)
    }, CookieAuthenticationDefaults.AuthenticationScheme));
    private static AccountDto Dto(User u) => new(u.Id, u.Email, u.DisplayName, u.AuthRole);
    private async Task SignIn(User u) => await HttpContext.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme, Principal(u), new AuthenticationProperties { IsPersistent = false });

    [AllowAnonymous, HttpPost("register"), EnableRateLimiting("accounts")]
    public async Task<ActionResult<AccountDto>> Register(RegisterAccountRequest r)
    {
        var email = r.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email))
            return Conflict(new { message = "This email is already registered. Please sign in." });
        var now = DateTime.UtcNow;
        var user = new User { Email = email, UserName = Guid.NewGuid().ToString("N"), Administrators = "",
            FirstName = r.FirstName.Trim(), LastName = r.LastName.Trim(),
            DisplayName = $"{r.FirstName.Trim()} {r.LastName.Trim()}", Department = r.Department.Trim(),
            UserRole = r.JobTitle.Trim(), LineManager = r.LineManager.Trim(), AuthRole = "QuizAuthor",
            IsActive = true, CreatedAt = now, UpdatedAt = now };
        user.PasswordHash = hasher.HashPassword(user, r.Password);
        db.Users.Add(user);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { return Conflict(new { message = "Account could not be created. Check whether the email is already registered." }); }
        await SignIn(user);
        return Ok(Dto(user));
    }

    [AllowAnonymous, HttpPost("login"), EnableRateLimiting("accounts")]
    public async Task<ActionResult<AccountDto>> Login(LoginRequest r)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == r.Email.Trim().ToLowerInvariant());
        if (user?.IsActive != true || string.IsNullOrEmpty(user.PasswordHash))
            return Unauthorized(new { message = "Email or password is incorrect." });
        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, r.Password);
        if (result == PasswordVerificationResult.Failed)
            return Unauthorized(new { message = "Email or password is incorrect." });
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        { user.PasswordHash = hasher.HashPassword(user, r.Password); await db.SaveChangesAsync(); }
        await SignIn(user);
        return Ok(Dto(user));
    }

    [Authorize, HttpGet("me")]
    public async Task<ActionResult<AccountDto>> Me()
    {
        var id = ulong.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == id);
        return Ok(Dto(user));
    }
    [Authorize, HttpPost("logout")]
    public async Task<IActionResult> Logout() { await HttpContext.SignOutAsync(); return NoContent(); }

}
