using Meridian.Api.Data;
using Meridian.Api.Features.Reports;
using Meridian.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Features.Assignments;

/// <summary>
/// Main Quest 1: choose which departments have to do each quiz and survey.
///   Authors (SuperAdmin, HR, line managers): every quiz and survey.
///   Staff: only the surveys they created themselves, assigned to their own department(s), with counts only (no names).
/// </summary>
[ApiController, Authorize, Route("api/assignments")]
public sealed class AssignmentsController(MeridianDbContext db, AssignmentService assignments, AssignmentStore store,
    EmployeeDirectoryStore directory) : ControllerBase
{
    private const int MaxOutstandingPerItem = 500;

    /// <summary>Departments the signed-in person may assign to. kind = quiz (default) or survey.</summary>
    [HttpGet("departments")]
    public async Task<ActionResult<AssignableDepartmentsDto>> Departments([FromQuery] string? kind, CancellationToken ct)
    {
        kind = AssignmentKinds.IsValid(kind) ? kind! : AssignmentKinds.Quiz;
        var viewer = await assignments.ViewerAsync(User, ct);
        if (viewer is null || !viewer.CanAssign(kind)) return StatusCode(403, new { message = "Only line managers, HR and administrators can assign quizzes." });
        return new AssignableDepartmentsDto(viewer.Scope.Role.ToString(), viewer.AssignableDepartments(kind).OrderBy(d => d).ToList());
    }

    /// <summary>Quizzes and surveys with their departments, due date and progress (Assignments page).</summary>
    [HttpGet]
    public async Task<ActionResult<List<AssignmentOverviewDto>>> Overview(CancellationToken ct)
    {
        var viewer = await assignments.ViewerAsync(User, ct);
        if (viewer is null) return Forbid();
        var me = viewer.User.Id;

        var quizzes = viewer.IsAuthor
            ? await db.Quizzes.AsNoTracking().Where(q => q.IsActive == true).Select(q => new { q.Id, q.Title }).ToListAsync(ct)
            : [];
        var surveys = await db.Surveys.AsNoTracking()
            .Where(s => viewer.IsAuthor || s.CreatedByUserId == me)
            .Select(s => new { s.Id, s.Title }).ToListAsync(ct);
        var quizAssignments = await store.GetAllAsync(AssignmentKinds.Quiz, ct);
        var surveyAssignments = await store.GetAllAsync(AssignmentKinds.Survey, ct);

        // People per department, from the employee list (only rows with an email can sign in to Meridian).
        var entries = await directory.GetAllAsync(ct);
        var people = entries.Where(e => !string.IsNullOrWhiteSpace(e.Email))
            .GroupBy(e => e.Email!.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => (Entry: g.First(), Departments: g.Select(e => e.Department).ToHashSet(StringComparer.OrdinalIgnoreCase)));
        var userIds = await db.Users.AsNoTracking().Where(u => u.IsActive == true)
            .Select(u => new { u.Id, u.Email }).ToListAsync(ct);
        var idByEmail = userIds.GroupBy(u => u.Email.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Id);

        var quizDone = quizzes.Count == 0 ? [] : (await db.QuizAttempts.AsNoTracking().Where(a => a.CompletedAt != null)
            .Select(a => new { a.QuizId, a.UserId }).Distinct().ToListAsync(ct))
            .Select(x => (x.QuizId.ToString(), x.UserId)).ToHashSet();
        var surveyDone = (await db.SurveyCompletions.AsNoTracking()
            .Select(c => new { c.SurveyId, c.UserId }).ToListAsync(ct))
            .Select(x => (x.SurveyId.ToString(), x.UserId)).ToHashSet();

        AssignmentOverviewDto Row(string kind, string id, string title, ItemAssignment a, HashSet<(string, ulong)> done)
        {
            var mine = viewer.AssignableDepartments(kind);
            var assigned = people.Where(p => p.Value.Departments.Overlaps(a.Departments)).ToList();
            bool Completed(string email) => idByEmail.TryGetValue(email, out var uid) && done.Contains((id, uid));
            var completed = assigned.Count(p => Completed(p.Key));
            // Names of people still to finish: authors only, and only within the departments they look after.
            var outstanding = !viewer.IsAuthor ? [] : assigned
                .Where(p => !Completed(p.Key))
                .Select(p => (p.Value.Entry, Department: p.Value.Departments.Where(a.Departments.Contains).Where(mine.Contains).OrderBy(d => d).FirstOrDefault()))
                .Where(x => x.Department is not null)
                .Select(x => new AssignmentOutstandingDto($"{x.Entry.NickName ?? x.Entry.FullName} {x.Entry.Surname}".Trim(), x.Department!))
                .OrderBy(x => x.Department).ThenBy(x => x.Name)
                .Take(MaxOutstandingPerItem).ToList();
            return new AssignmentOverviewDto(kind, id, title, a.Departments.ToList(), a.AssignedOnly, a.DueOn,
                assigned.Count, completed, CanEdit: mine.Count > 0, outstanding);
        }

        var rows = quizzes.Select(q => Row(AssignmentKinds.Quiz, q.Id.ToString(), q.Title,
                quizAssignments.GetValueOrDefault(q.Id.ToString()) ?? ItemAssignment.None(AssignmentKinds.Quiz, q.Id.ToString()), quizDone))
            .Concat(surveys.Select(s => Row(AssignmentKinds.Survey, s.Id.ToString(), s.Title,
                surveyAssignments.GetValueOrDefault(s.Id.ToString()) ?? ItemAssignment.None(AssignmentKinds.Survey, s.Id.ToString()), surveyDone)))
            .OrderByDescending(r => r.Departments.Count > 0).ThenBy(r => r.DueOn ?? DateOnly.MaxValue).ThenBy(r => r.Title)
            .ToList();
        return rows;
    }

    /// <summary>Replaces the departments, "only assigned" flag and due date of one quiz or survey.</summary>
    [HttpPut("{kind}/{id}")]
    public async Task<IActionResult> Save(string kind, string id, AssignmentRequest request, CancellationToken ct)
    {
        if (!AssignmentKinds.IsValid(kind)) return NotFound();
        var viewer = await assignments.ViewerAsync(User, ct);
        if (viewer is null || !viewer.CanAssign(kind)) return StatusCode(403, new { message = "Only line managers, HR and administrators can assign quizzes." });

        string key;
        if (kind == AssignmentKinds.Quiz)
        {
            if (!uint.TryParse(id, out var quizId) || !await db.Quizzes.AnyAsync(q => q.Id == quizId && q.IsActive == true, ct))
                return NotFound(new { message = "Quiz not found." });
            key = quizId.ToString();
        }
        else
        {
            if (!Guid.TryParse(id, out var surveyId)) return NotFound(new { message = "Survey not found." });
            var survey = await db.Surveys.AsNoTracking().Where(s => s.Id == surveyId).Select(s => new { s.CreatedByUserId }).SingleOrDefaultAsync(ct);
            if (survey is null) return NotFound(new { message = "Survey not found." });
            if (!viewer.IsAuthor && survey.CreatedByUserId != viewer.User.Id)
                return StatusCode(403, new { message = "You can only assign the surveys you created." });
            key = surveyId.ToString();
        }
        var error = await assignments.ApplyAsync(viewer, kind, key, request, ct);
        return error is null ? NoContent() : StatusCode(error.StartsWith("You can only") || error.StartsWith("Another") || error.StartsWith("Only") ? 403 : 400, new { message = error });
    }
}
