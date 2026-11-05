using System.Security.Claims;
using System.Text;
using Meridian.Api.Data;
using Meridian.Api.Data.Entities;
using Meridian.Api.Features.Reports;
using Meridian.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Meridian.Api.Features.Certificates;

/// <summary>
/// Certificates of Completion (PDF, A4 landscape) for PASSED quiz attempts.
///  - Everyone: their own certificates (Results page, completed quizzes lists).
///  - SuperAdmin and HR: anyone's. Line managers: people in the departments they manage (Reports page).
/// Nothing is stored: the PDF is drawn from the attempt each time, so name or title fixes show up on the next download.
/// </summary>
[ApiController, Authorize, Route("api/certificates")]
public sealed class CertificatesController(MeridianDbContext db, EmployeeDirectoryStore directory, ReportAccessService access,
    IConfiguration config, ILogger<CertificatesController> log) : ControllerBase
{
    private static readonly TimeSpan Sast = TimeSpan.FromHours(2);
    private const int MaxTeamRows = 500;
    private ulong UserId => ulong.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>The certificate PDF. download=true saves it; otherwise the browser shows it (view).</summary>
    [HttpGet("{attemptId:guid}")]
    public async Task<IActionResult> Get(Guid attemptId, [FromQuery] bool download = false, CancellationToken ct = default)
    {
        var attempt = await db.QuizAttempts.AsNoTracking().Include(a => a.Quiz).ThenInclude(q => q.Category)
            .SingleOrDefaultAsync(a => a.Id == attemptId && a.CompletedAt != null && a.Passed, ct);
        // Same answer for "not passed", "not yours" and "does not exist", so attempt IDs cannot be probed.
        if (attempt is null || (attempt.UserId != UserId && !await CanSeeAsync(attempt.UserId, ct)))
            return NotFound(new { message = "Certificate not found. Certificates are issued for passed quizzes only." });

        var person = await db.Users.AsNoTracking().SingleAsync(u => u.Id == attempt.UserId, ct);
        var completedUtc = DateTime.SpecifyKind(attempt.CompletedAt!.Value, DateTimeKind.Utc);
        var data = new CertificateData(
            FullName(person), attempt.Quiz.Title, attempt.Quiz.Category?.Name ?? "", completedUtc + Sast,
            (int)attempt.ScorePercent, attempt.SnapshotPassMark ?? attempt.Quiz.PassMarkPercent,
            CertificatePdf.NumberFor(attempt.Id, completedUtc),
            config["Certificates:SignatoryName"] is { Length: > 0 } n ? n : "Optimum Academy",
            config["Certificates:SignatoryTitle"] is { Length: > 0 } t ? t : "Optimum Financial Services Group");
        if (attempt.UserId != UserId)
            log.LogInformation("User {Viewer} opened certificate {Number} of user {Owner}", UserId, data.Number, attempt.UserId);

        var bytes = CertificatePdf.Build(data);
        var fileName = $"Certificate - {data.QuizTitle} - {data.Name}.pdf";
        var disposition = new ContentDispositionHeaderValue(download ? "attachment" : "inline");
        disposition.SetHttpFileName(Clean(fileName));
        Response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();
        Response.Headers[HeaderNames.CacheControl] = "private, no-store";
        return File(bytes, "application/pdf");
    }

    /// <summary>
    /// Certificates of the people a manager may see (latest pass per person and quiz), newest first.
    /// department: blank/all = everything in scope, or one department. search: part of a name, email or quiz title.
    /// </summary>
    [HttpGet("team")]
    public async Task<ActionResult<List<CertificateListItemDto>>> Team([FromQuery] string? department, [FromQuery] string? search,
        CancellationToken ct = default)
    {
        var (scope, entries) = await ScopeAsync(ct);
        if (scope.Role == ReportRole.Staff)
            return StatusCode(403, new { message = "Only line managers, HR and administrators can see other people's certificates." });

        var departments = scope.Departments.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requested = department?.Trim() ?? "";
        if (requested.Length > 0 && !requested.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (!departments.Contains(requested)) return StatusCode(403, new { message = "You can only see certificates for the departments you manage." });
            departments = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { requested };
        }
        var allInScope = scope.Role != ReportRole.LineManager && (requested.Length == 0 || requested.Equals("all", StringComparison.OrdinalIgnoreCase));

        var users = await db.Users.AsNoTracking().ToListAsync(ct);
        var people = users
            .Select(u => (User: u, Departments: ReportAccessService.DepartmentsOf(u, entries)))
            .Where(p => allInScope || p.Departments.Overlaps(departments))
            .ToDictionary(p => p.User.Id);
        var ids = people.Keys.ToList();

        var passed = await db.QuizAttempts.AsNoTracking().Include(a => a.Quiz).ThenInclude(q => q.Category)
            .Where(a => ids.Contains(a.UserId) && a.CompletedAt != null && a.Passed)
            .OrderByDescending(a => a.CompletedAt).ToListAsync(ct);

        var term = search?.Trim() ?? "";
        var rows = passed
            .GroupBy(a => (a.UserId, a.QuizId)).Select(g => g.First())
            .Select(a =>
            {
                var p = people[a.UserId];
                var completedUtc = DateTime.SpecifyKind(a.CompletedAt!.Value, DateTimeKind.Utc);
                var dept = allInScope ? p.Departments.FirstOrDefault() : p.Departments.FirstOrDefault(departments.Contains);
                return (Row: new CertificateListItemDto(a.Id, FullName(p.User), dept ?? "", a.Quiz.Title, a.Quiz.Category?.Name ?? "",
                    completedUtc, (int)a.ScorePercent, CertificatePdf.NumberFor(a.Id, completedUtc)), Email: p.User.Email);
            })
            .Where(r => term.Length == 0
                || r.Row.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || r.Email.Contains(term, StringComparison.OrdinalIgnoreCase)
                || r.Row.QuizTitle.Contains(term, StringComparison.OrdinalIgnoreCase)
                || r.Row.CertificateNumber.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Row)
            .Take(MaxTeamRows)
            .ToList();
        return rows;
    }

    private async Task<bool> CanSeeAsync(ulong ownerId, CancellationToken ct)
    {
        var (scope, entries) = await ScopeAsync(ct);
        if (scope.Role is ReportRole.SuperAdmin or ReportRole.HR) return true;
        if (scope.Role != ReportRole.LineManager) return false;
        var owner = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == ownerId, ct);
        return owner is not null && ReportAccessService.DepartmentsOf(owner, entries)
            .Overlaps(scope.Departments.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private async Task<(ReportScope Scope, IReadOnlyList<DirectoryEmployee> Entries)> ScopeAsync(CancellationToken ct)
    {
        var me = await db.Users.AsNoTracking().SingleAsync(u => u.Id == UserId, ct);
        var entries = await directory.GetAllAsync(ct);
        var registered = await db.Users.AsNoTracking().Where(u => u.IsActive == true).Select(u => u.Department).Distinct().ToListAsync(ct);
        var all = entries.Select(e => e.Department)
            .Concat(registered.Select(d => DirectoryNames.CleanDepartment(d)))
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d).ToList();
        return (await access.ResolveAsync(me, all, ct), entries);
    }

    private static string FullName(User u)
    {
        var name = $"{u.FirstName} {u.LastName}".Trim();
        return name.Length > 0 ? name : u.DisplayName;
    }

    private static string Clean(string fileName)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { ':', '/', '\\', '"' }).ToHashSet();
        var sb = new StringBuilder(fileName.Length);
        foreach (var ch in fileName) sb.Append(bad.Contains(ch) ? '-' : ch);
        return sb.ToString();
    }
}
