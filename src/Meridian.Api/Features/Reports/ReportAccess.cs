using System.Text.RegularExpressions;
using Meridian.Api.Data.Entities;
using Meridian.Api.Features.DevTools;

namespace Meridian.Api.Features.Reports;

public enum ReportRole { Staff, LineManager, HR, SuperAdmin }

/// <summary>Departments = what this person may report on (empty for Staff: personal report only).</summary>
public sealed record ReportScope(ReportRole Role, IReadOnlyList<string> Departments);

/// <summary>
/// Report access, decided on the server from trusted data only:
///  - SuperAdmin: email listed in "Reports:SuperAdminEmails" (plus the Dev Admin while the dev bypass is enabled).
///  - HR: employee-list job title contains "Human Resource"/"HR", or email listed in "Reports:HrEmails".
///  - Line manager: named in another employee's Line Manager column; scope = departments of the people they manage.
///  - Everyone else: Staff (own report).
/// The match is on the signed-in account's email against the employee list, never on the job title picked at registration.
/// </summary>
public sealed class ReportAccessService(EmployeeDirectoryStore directory, IConfiguration config, DevBypassState devBypass)
{
    public async Task<ReportScope> ResolveAsync(User user, IReadOnlyList<string> allDepartments, CancellationToken ct = default)
    {
        var email = user.Email.Trim().ToLowerInvariant();
        var superAdmins = Emails("Reports:SuperAdminEmails");
        if (devBypass.Enabled) superAdmins.Add(DevBypassController.DevEmail);
        if (superAdmins.Contains(email)) return new(ReportRole.SuperAdmin, allDepartments);

        var entries = await directory.GetAllAsync(ct);
        var mine = entries.Where(e => string.Equals(e.Email, email, StringComparison.OrdinalIgnoreCase)).ToList();
        if (Emails("Reports:HrEmails").Contains(email) || mine.Any(e => IsHrTitle(e.JobTitle)))
            return new(ReportRole.HR, allDepartments);

        var keys = mine.SelectMany(DirectoryNames.Keys).ToHashSet();
        if (keys.Count > 0)
        {
            var managed = entries
                .Where(e => DirectoryNames.SplitManagers(e.LineManager).Any(keys.Contains))
                .Select(e => e.Department)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d)
                .ToList();
            if (managed.Count > 0) return new(ReportRole.LineManager, managed);
        }
        return new(ReportRole.Staff, new List<string>());
    }

    /// <summary>Quizzes can be created by SuperAdmin, HR and line managers; staff can only create surveys.</summary>
    public async Task<bool> CanCreateQuizzesAsync(User user, CancellationToken ct = default) =>
        (await ResolveAsync(user, Array.Empty<string>(), ct)).Role != ReportRole.Staff;

    /// <summary>A user's departments: every employee-list row with their email, plus the department they registered with.</summary>
    public static HashSet<string> DepartmentsOf(User user, IReadOnlyList<DirectoryEmployee> entries)
    {
        var result = entries.Where(e => string.Equals(e.Email, user.Email, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Department).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registered = DirectoryNames.CleanDepartment(user.Department);
        if (registered.Length > 0) result.Add(registered);
        return result;
    }

    private static bool IsHrTitle(string title) =>
        title.Contains("Human Resource", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(title, @"\bHR\b");

    private HashSet<string> Emails(string key) =>
        (config.GetSection(key).Get<string[]>() ?? Array.Empty<string>())
            .Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToHashSet();
}
