using System.Text.Json;
using Meridian.Api.Data;
using Meridian.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Meridian.Api.Features.Surveys;

[ApiController]
[Route("api/surveys")]
[Authorize]
public sealed class SurveysController(MeridianDbContext db, Meridian.Api.Features.Reports.EmployeeDirectoryStore directory,
    Meridian.Api.Features.Assignments.AssignmentService assignments, Meridian.Api.Features.Assignments.AssignmentStore assignmentStore,
    ILogger<SurveysController> logger) : ControllerBase
{
    /// <summary>Surveys page: authors see every survey, staff see the surveys they created.</summary>
    [HttpGet]
    public async Task<ActionResult<List<SurveyListItemDto>>> List(CancellationToken ct)
    {
        var userId = await CurrentUserIdAsync(ct);
        if (userId is null) return Forbid();
        var all = User.IsInRole("QuizAuthor") || User.IsInRole("Admin");
        var records = await db.Surveys.AsNoTracking().Where(x => all || x.CreatedByUserId == userId.Value)
            .OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        var counts = await db.SurveyCompletions.AsNoTracking().GroupBy(x => x.SurveyId)
            .Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        return records.Select(x => new SurveyListItemDto(x.Id, x.Title, Decode(x).Questions.Count,
            DateTime.SpecifyKind(x.CreatedAt, DateTimeKind.Utc), counts.GetValueOrDefault(x.Id))).ToList();
    }

    /// <summary>Everyone can create surveys (staff included, for their own departments only); quizzes stay with SuperAdmin, HR and line managers.</summary>
    [HttpPost]
    [RequestSizeLimit(1_000_000)]
    public async Task<ActionResult<SurveyDto>> Create(CreateSurveyRequest request, CancellationToken ct)
    {
        var creatorId = await CurrentUserIdAsync(ct);
        if (creatorId is null) return Forbid();
        var errors = SurveyValidation.Definition(request);
        if (errors.Count > 0) return BadRequest(new { message = string.Join(" ", errors) });
        var viewer = await assignments.ViewerAsync(User, ct);
        if (viewer is null) return Forbid();
        // Staff surveys are always assigned to the staff member's own department(s) and hidden from everyone else
        // (AssignmentService.PrepareAsync enforces this); authors may still leave a survey open to everyone.
        var wanted = request.Assignment ?? new AssignmentRequest();
        Meridian.Api.Features.Assignments.ItemAssignment? assignment = null;
        if (!viewer.IsAuthor || wanted.Departments.Count > 0 || wanted.AssignedOnly || wanted.DueOn is not null)
        {
            var (prepared, assignmentError) = await assignments.PrepareAsync(viewer, AssignmentKinds.Survey, null, wanted, ct);
            if (prepared is null) return BadRequest(new { message = assignmentError });
            assignment = prepared;
        }
        var survey = new SurveyDto(Guid.NewGuid(), request.Title.Trim(), request.Description?.Trim() ?? "", request.Questions, DateTime.UtcNow);
        try
        {
            // Failsafe: the survey and its departments are saved in ONE transaction. If the departments cannot be saved,
            // the survey is not saved either, so a survey meant to be restricted is never left visible to everyone.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            db.Surveys.Add(new SurveyRecord { Id = survey.Id, Title = survey.Title, DefinitionJson = JsonSerializer.Serialize(survey), CreatedAt = survey.CreatedAt, CreatedByUserId = creatorId, DeleteAfter = request.AddToArchive ? survey.CreatedAt.AddMonths(24) : null });
            await db.SaveChangesAsync(ct);
            if (assignment is not null)
                await assignmentStore.SaveAsync(assignment with { Id = survey.Id.ToString() }, viewer.User.Id,
                    (MySqlConnector.MySqlConnection)db.Database.GetDbConnection(), (MySqlConnector.MySqlTransaction)transaction.GetDbTransaction(), ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or MySqlConnector.MySqlException)
        {
            logger.LogError(ex, "Survey {SurveyId} could not be saved with its departments", survey.Id);
            return Problem(statusCode: 500, title: "Survey could not be saved",
                detail: "Nothing was saved (the survey and its departments are saved together). Please try again.");
        }
        return StatusCode(201, survey);
    }

    [HttpGet("dashboard")]
    public async Task<ActionResult<List<DashboardSurveyDto>>> Dashboard(CancellationToken ct)
    {
        var userId = await CurrentUserIdAsync(ct);
        if (userId is null) return Forbid();
        var records = await db.Surveys.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        var completed = await db.SurveyCompletions.AsNoTracking().Where(x => x.UserId == userId.Value)
            .ToDictionaryAsync(x => x.SurveyId, x => x.SubmittedAt, ct);
        var viewer = await assignments.ViewerAsync(User, ct);
        if (viewer is null) return Forbid();
        var assigned = await assignmentStore.GetAllAsync(AssignmentKinds.Survey, ct);
        var result = new List<DashboardSurveyDto>();
        foreach (var x in records)
        {
            var a = assigned.GetValueOrDefault(x.Id.ToString());
            if (a is not null && !a.IsVisibleTo(viewer.Departments, viewer.SeesEverything)) continue;
            var required = a?.IsRequiredFor(viewer.Departments) == true;
            result.Add(new DashboardSurveyDto(x.Id, x.Title, Decode(x).Questions.Count,
                completed.TryGetValue(x.Id, out var date) ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : null)
                { Required = required, DueOn = required ? a!.DueOn : null });
        }
        // Required surveys first (earliest due date first), then the rest newest first.
        return result.OrderByDescending(x => x.Required).ThenBy(x => x.DueOn ?? DateOnly.MaxValue).ToList();
    }

    [HttpGet("{surveyId:guid}")]
    public async Task<ActionResult<SurveyOpenDto>> Open(Guid surveyId, CancellationToken ct)
    {
        var userId = await CurrentUserIdAsync(ct);
        if (userId is null) return Forbid();
        var survey = await db.Surveys.AsNoTracking().SingleOrDefaultAsync(x => x.Id == surveyId, ct);
        if (survey is null || !await CanSeeAsync(surveyId, ct)) return NotFound();
        var response = await db.SurveyCompletions.AsNoTracking().SingleOrDefaultAsync(x => x.SurveyId == surveyId && x.UserId == userId.Value, ct);
        return new SurveyOpenDto(Decode(survey), response is null ? null : DateTime.SpecifyKind(response.SubmittedAt, DateTimeKind.Utc),
            response is null ? null : JsonSerializer.Deserialize<List<SurveyAnswerDto>>(response.AnswersJson));
    }

    [HttpPost("{surveyId:guid}/responses")]
    [RequestSizeLimit(1_000_000)]
    public async Task<IActionResult> Respond(Guid surveyId, SubmitSurveyRequest request, CancellationToken ct)
    {
        var userId = await CurrentUserIdAsync(ct);
        if (userId is null) return Forbid();
        if (await db.SurveyCompletions.AnyAsync(x => x.SurveyId == surveyId && x.UserId == userId.Value, ct))
            return NoContent(); // Retried requests do not create duplicate responses.
        var record = await db.Surveys.AsNoTracking().SingleOrDefaultAsync(x => x.Id == surveyId, ct);
        if (record is null || !await CanSeeAsync(surveyId, ct)) return NotFound();
        var errors = SurveyValidation.Answers(Decode(record), request);
        if (errors.Count > 0) return BadRequest(new { message = string.Join(" ", errors) });
        // Anonymity: participation + the user's private copy go to survey_completions; a separate,
        // unlinkable copy (random id, departments, day only) goes to survey_anonymous_answers.
        // One SaveChanges = one transaction, so both rows are written or neither.
        var now = DateTime.UtcNow;
        var answersJson = JsonSerializer.Serialize(request.Answers);
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == userId.Value, ct);
        db.SurveyCompletions.Add(new SurveyCompletionRecord
        {
            Id = Guid.NewGuid(), SurveyId = surveyId, UserId = userId.Value,
            AnswersJson = answersJson, SubmittedAt = now, AnonymisedAt = now
        });
        db.SurveyAnonymousAnswers.Add(new SurveyAnonymousAnswer
        {
            Id = Guid.NewGuid(), SurveyId = surveyId,
            Departments = SurveyAnonymity.DepartmentsKey(user, await directory.GetAllAsync(ct)),
            AnswersJson = answersJson, SubmittedOn = SurveyAnonymity.SastDay(now)
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Handle a racing duplicate using the database unique constraint.
            if (await db.SurveyCompletions.AsNoTracking().AnyAsync(x => x.SurveyId == surveyId && x.UserId == userId.Value, ct))
                return NoContent();
            throw;
        }
        return NoContent();
    }

    /// <summary>Surveys restricted to assigned departments can only be opened and answered by people in those departments.</summary>
    private async Task<bool> CanSeeAsync(Guid surveyId, CancellationToken ct)
    {
        var a = await assignmentStore.GetAsync(AssignmentKinds.Survey, surveyId.ToString(), ct);
        if (!a.AssignedOnly) return true;
        var viewer = await assignments.ViewerAsync(User, ct);
        return viewer is not null && a.IsVisibleTo(viewer.Departments, viewer.SeesEverything);
    }

    private async Task<ulong?> CurrentUserIdAsync(CancellationToken ct)
    {
        // Trusted signed claim/server-side mapping only; never a browser-provided user ID.
        if (!ulong.TryParse(User.FindFirst("meridian_user_id")?.Value, out var userId)) return null;
        return await db.Users.AsNoTracking().AnyAsync(x => x.Id == userId && x.IsActive == true, ct) ? userId : null;
    }
    private static SurveyDto Decode(SurveyRecord record) =>
        JsonSerializer.Deserialize<SurveyDto>(record.DefinitionJson) ?? throw new InvalidOperationException("Survey definition is invalid.");
}
