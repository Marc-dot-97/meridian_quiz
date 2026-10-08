using System.Security.Claims;
using Meridian.Api.Data;
using Meridian.Api.Data.Entities;
using Meridian.Api.Features.Reports;
using Meridian.Shared.DTOs;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Features.Assignments;

/// <summary>
/// The signed-in person as the assignment rules see them, worked out on the server from trusted data only
/// (Meridian user row + employee list), never from anything the browser sends.
///   Departments    = the departments this person belongs to (decides what they have to do / may see).
///   Scope          = the report scope: SuperAdmin and HR = every department, line manager = managed departments, staff = none.
///   SeesEverything = SuperAdmin or HR: restricted quizzes and surveys are always visible to them.
/// </summary>
public sealed record AssignmentViewer(User User, IReadOnlySet<string> Departments, ReportScope Scope, IReadOnlyList<string> AllDepartments)
{
    public bool SeesEverything => Scope.Role is ReportRole.SuperAdmin or ReportRole.HR;
    public bool CanAssign => Scope.Role != ReportRole.Staff;

    /// <summary>Departments this person may add or remove (case-insensitive).</summary>
    public IReadOnlySet<string> AssignableDepartments => Scope.Departments.ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// "Only assigned departments may see it" and the due date may be changed by SuperAdmin/HR, or by an author whose
    /// departments cover every department currently assigned (so a line manager cannot change another team's setting).
    /// </summary>
    public bool CanEditSettings(ItemAssignment current) =>
        SeesEverything || (CanAssign && current.Departments.All(AssignableDepartments.Contains));
}

public sealed class AssignmentService(MeridianDbContext db, EmployeeDirectoryStore directory, ReportAccessService access, AssignmentStore store)
{
    /// <summary>All known departments: the employee list plus departments of active Meridian users (same list the reports use).</summary>
    public async Task<List<string>> AllDepartmentsAsync(CancellationToken ct)
    {
        var entries = await directory.GetAllAsync(ct);
        var registered = await db.Users.AsNoTracking().Where(u => u.IsActive == true).Select(u => u.Department).Distinct().ToListAsync(ct);
        return entries.Select(e => e.Department)
            .Concat(registered.Select(d => DirectoryNames.CleanDepartment(d)))
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d).ToList();
    }

    public async Task<AssignmentViewer?> ViewerAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!ulong.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return null;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id && u.IsActive == true, ct);
        if (user is null) return null;
        var entries = await directory.GetAllAsync(ct);
        var all = await AllDepartmentsAsync(ct);
        var scope = await access.ResolveAsync(user, all, ct);
        var mine = ReportAccessService.DepartmentsOf(user, entries);
        return new AssignmentViewer(user, mine, scope, all);
    }

    /// <summary>
    /// Applies an author's request to one item. Rules:
    ///  - only departments that exist (employee list / Meridian users) can be assigned;
    ///  - an author adds or removes only departments inside their own scope; departments outside it are kept as they are;
    ///  - "only assigned" and the due date follow AssignmentViewer.CanEditSettings.
    /// Returns an error message (nothing saved) or null when saved.
    /// </summary>
    public async Task<string?> ApplyAsync(AssignmentViewer viewer, string kind, string id, AssignmentRequest request, CancellationToken ct)
    {
        var (final, error) = await PrepareAsync(viewer, kind, id, request, ct);
        if (final is null) return error;
        await store.SaveAsync(final, viewer.User.Id, ct);
        return null;
    }

    /// <summary>
    /// Checks a request without saving. For a brand-new quiz or survey pass id = null (nothing assigned yet),
    /// then save the returned assignment with the new id once the item exists.
    /// </summary>
    public async Task<(ItemAssignment? Final, string? Error)> PrepareAsync(AssignmentViewer viewer, string kind, string? id, AssignmentRequest request, CancellationToken ct)
    {
        if (!viewer.CanAssign) return (null, "Only line managers, HR and administrators can assign quizzes and surveys.");
        var errors = AssignmentValidation.Validate(request);
        if (errors.Count > 0) return (null, string.Join(" ", errors));

        var known = viewer.AllDepartments.ToDictionary(d => d, d => d, StringComparer.OrdinalIgnoreCase);
        var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in request.Departments.Select(d => d.Trim()))
        {
            if (!known.TryGetValue(name, out var canonical)) return (null, $"Unknown department: {name}.");
            requested.Add(canonical);
        }

        var current = id is null ? ItemAssignment.None(kind, "") : await store.GetAsync(kind, id, ct);
        var currentSet = current.Departments.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mine = viewer.AssignableDepartments;
        var outside = requested.Where(d => !currentSet.Contains(d) && !mine.Contains(d)).ToList();
        if (outside.Count > 0)
            return (null, $"You can only assign the departments you manage. Not allowed: {string.Join(", ", outside.OrderBy(d => d))}.");
        // Departments outside the author's scope that were already assigned (by HR or another manager) are always kept.
        requested.UnionWith(currentSet.Where(d => !mine.Contains(d)));
        requested.RemoveWhere(d => !mine.Contains(d) && !currentSet.Contains(d));

        var settingsChanged = request.AssignedOnly != current.AssignedOnly || request.DueOn != current.DueOn;
        if (settingsChanged && !viewer.CanEditSettings(current))
            return (null, "Another department's manager set who can see this and the due date; ask them, HR or an administrator to change it.");

        var final = new ItemAssignment(kind, id ?? "", requested.OrderBy(d => d).ToList(), request.AssignedOnly, request.DueOn);
        if (final.AssignedOnly && final.Departments.Count == 0)
            return (null, "Choose at least one department when only assigned departments may see it.");
        return (final, null);
    }
}
