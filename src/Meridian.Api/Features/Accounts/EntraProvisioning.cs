using System.Security.Claims;
using Meridian.Api.Data;
using Meridian.Api.Data.Entities;
using Meridian.Api.Features.Reports;
using Microsoft.EntityFrameworkCore;
namespace Meridian.Api.Features.Accounts;

/// <summary>
/// Turns a successful Microsoft (Entra ID) sign-in into a Meridian session.
///
///  1. The signed-in Microsoft account's email is looked up in the employee list (in production that is the CRM's
///     optimum_admin.dtbl_employees, active employees only). Not an employee = no access; nothing is created.
///  2. The matching Meridian user row is created or refreshed from the employee record (name, department,
///     job title, line manager). Nothing the browser sends is trusted.
///  3. The Meridian role is worked out from the same trusted data the reports already use:
///        SuperAdmin            -> "Admin"      (can author, manage users, see every report)
///        HR / line manager     -> "QuizAuthor" (can author quizzes and surveys, see their reports)
///        everyone else         -> "Member"     (can take quizzes and surveys, see their own results)
///     The role is re-worked out at every sign-in, so promotions and leavers take effect the next time
///     the person signs in (sessions last 8 hours).
/// </summary>
public static class EntraProvisioning
{
    /// <summary>Every email-like claim Microsoft may send, in the order they are tried (UPN first, as the CRM does).</summary>
    private static readonly string[] EmailClaims = ["preferred_username", "email", ClaimTypes.Email, "upn", ClaimTypes.Upn];

    public static IEnumerable<string> CandidateEmails(ClaimsPrincipal external) =>
        EmailClaims.Select(external.FindFirstValue)
            .Where(v => !string.IsNullOrWhiteSpace(v) && v!.Contains('@'))
            .Select(v => v!.Trim().ToLowerInvariant())
            .Distinct();

    public static async Task<User?> SignInAsync(HttpContext http, ClaimsPrincipal external, CancellationToken ct = default)
    {
        var sp = http.RequestServices;
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Meridian.SignIn");
        var db = sp.GetRequiredService<MeridianDbContext>();
        var directory = sp.GetRequiredService<EmployeeDirectoryStore>();
        var access = sp.GetRequiredService<ReportAccessService>();

        var candidates = CandidateEmails(external).ToList();
        var entries = await directory.GetAllAsync(ct);

        string? email = null;
        DirectoryEmployee? employee = null;
        foreach (var candidate in candidates)
        {
            employee = entries.FirstOrDefault(e => string.Equals(e.Email, candidate, StringComparison.OrdinalIgnoreCase));
            if (employee is not null) { email = candidate; break; }
        }
        if (employee is null)
        {
            // "Either email can log in": an extra email on the CRM's dtbl_employee_emails resolves to the person's primary
            // email, and everything after this (the Meridian user row, roles, reports) uses that primary email.
            var aliases = await directory.GetAliasesAsync(ct);
            foreach (var candidate in candidates)
            {
                if (!aliases.TryGetValue(candidate, out var primary)) continue;
                employee = entries.FirstOrDefault(e => string.Equals(e.Email, primary, StringComparison.OrdinalIgnoreCase));
                if (employee is not null) { email = primary; break; }
            }
        }
        if (employee is null || email is null)
        {
            // The address is logged for the administrator; it is never put in a URL or shown to other people.
            log.LogWarning("Sign-in refused: no active employee matches Microsoft account(s) [{Emails}]", string.Join(", ", candidates));
            return null;
        }

        var now = DateTime.UtcNow;
        var first = employee.NickName ?? employee.FullName;
        var display = $"{first} {employee.Surname}".Trim();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            user = new User
            {
                Email = email, UserName = Guid.NewGuid().ToString("N"), Administrators = "", CreatedAt = now,
                IsActive = true, AuthRole = "Member", FirstName = first, DisplayName = display, Department = employee.Department, UserRole = employee.JobTitle,
            };
            db.Users.Add(user);
        }
        else if (user.IsActive != true)
        {
            log.LogWarning("Sign-in refused: Meridian account {Email} is switched off", email);
            return null;
        }

        user.FirstName = first;
        user.LastName = employee.Surname;
        user.DisplayName = display;
        user.Department = employee.Department;
        user.UserRole = employee.JobTitle;
        user.LineManager = employee.LineManager ?? "";
        user.UpdatedAt = now;
        user.PasswordHash = null;   // no passwords any more; clears hashes left over from the old email/password version

        var scope = await access.ResolveAsync(user, Array.Empty<string>(), ct);
        user.AuthRole = scope.Role switch
        {
            ReportRole.SuperAdmin => "Admin",
            ReportRole.HR or ReportRole.LineManager => "QuizAuthor",
            _ => "Member",
        };

        await db.SaveChangesAsync(ct);
        log.LogInformation("Signed in {Email} as {Role}", email, user.AuthRole);
        return user;
    }
}
