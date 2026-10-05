using System.Text.Json;
using Meridian.Api.Data;
using Meridian.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Features.Surveys;

[ApiController]
[Route("api/surveys")]
[Authorize]
public sealed class SurveysController(MeridianDbContext db) : ControllerBase
{
    [HttpGet]
    [Authorize(Roles = "QuizAuthor,Admin")]
    public async Task<ActionResult<List<SurveyListItemDto>>> List(CancellationToken ct)
    {
        var records = await db.Surveys.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        var counts = await db.SurveyCompletions.AsNoTracking().GroupBy(x => x.SurveyId)
            .Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        return records.Select(x => new SurveyListItemDto(x.Id, x.Title, Decode(x).Questions.Count,
            DateTime.SpecifyKind(x.CreatedAt, DateTimeKind.Utc), counts.GetValueOrDefault(x.Id))).ToList();
    }

    [HttpPost]
    [Authorize(Roles = "QuizAuthor,Admin")]
    [RequestSizeLimit(1_000_000)]
    public async Task<ActionResult<SurveyDto>> Create(CreateSurveyRequest request, CancellationToken ct)
    {
        var errors = SurveyValidation.Definition(request);
        if (errors.Count > 0) return BadRequest(new { message = string.Join(" ", errors) });
        var survey = new SurveyDto(Guid.NewGuid(), request.Title.Trim(), request.Description?.Trim() ?? "", request.Questions, DateTime.UtcNow);
        db.Surveys.Add(new SurveyRecord { Id = survey.Id, Title = survey.Title, DefinitionJson = JsonSerializer.Serialize(survey), CreatedAt = survey.CreatedAt, DeleteAfter = request.AddToArchive ? survey.CreatedAt.AddMonths(24) : null });
        await db.SaveChangesAsync(ct);
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
        return records.Select(x => new DashboardSurveyDto(x.Id, x.Title, Decode(x).Questions.Count,
            completed.TryGetValue(x.Id, out var date) ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : null)).ToList();
    }

    [HttpGet("{surveyId:guid}")]
    public async Task<ActionResult<SurveyOpenDto>> Open(Guid surveyId, CancellationToken ct)
    {
        var userId = await CurrentUserIdAsync(ct);
        if (userId is null) return Forbid();
        var survey = await db.Surveys.AsNoTracking().SingleOrDefaultAsync(x => x.Id == surveyId, ct);
        if (survey is null) return NotFound();
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
        if (record is null) return NotFound();
        var errors = SurveyValidation.Answers(Decode(record), request);
        if (errors.Count > 0) return BadRequest(new { message = string.Join(" ", errors) });
        db.SurveyCompletions.Add(new SurveyCompletionRecord
        {
            Id = Guid.NewGuid(), SurveyId = surveyId, UserId = userId.Value,
            AnswersJson = JsonSerializer.Serialize(request.Answers), SubmittedAt = DateTime.UtcNow
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

    private async Task<ulong?> CurrentUserIdAsync(CancellationToken ct)
    {
        // Trusted signed claim/server-side mapping only; never a browser-provided user ID.
        if (!ulong.TryParse(User.FindFirst("meridian_user_id")?.Value, out var userId)) return null;
        return await db.Users.AsNoTracking().AnyAsync(x => x.Id == userId && x.IsActive == true, ct) ? userId : null;
    }
    private static SurveyDto Decode(SurveyRecord record) =>
        JsonSerializer.Deserialize<SurveyDto>(record.DefinitionJson) ?? throw new InvalidOperationException("Survey definition is invalid.");
}
