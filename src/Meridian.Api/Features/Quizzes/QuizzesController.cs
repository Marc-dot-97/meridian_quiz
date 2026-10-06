using Meridian.Api.Data;
using Meridian.Api.Features.Quizzes.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Features.Quizzes;

[ApiController]
[Route("api/quizzes")]
[Authorize]
public sealed class QuizzesController : ControllerBase
{
    private readonly MeridianDbContext _dbContext;

    public QuizzesController(
        MeridianDbContext dbContext)
    {
        _dbContext = dbContext;
    }


    // GET /api/quizzes
    [HttpGet]
    public async Task<
        ActionResult<IEnumerable<QuizSummaryDto>>>
        GetQuizzes()
    {
        var quizzes =
            await _dbContext.Quizzes
                .AsNoTracking()
                .Where(q => q.IsActive == true)
                .OrderBy(q => q.Title)
                .Select(q => new QuizSummaryDto
                {
                    Id = q.Id,

                    Title = q.Title,
                    ExpiresAt = q.ExpiresAt.HasValue ? DateTime.SpecifyKind(q.ExpiresAt.Value, DateTimeKind.Utc) : null,
                    AvailableFrom = q.AvailableFrom.HasValue ? DateTime.SpecifyKind(q.AvailableFrom.Value, DateTimeKind.Utc) : null,

                    Category = q.Category.Name,

                    PassMarkPercent =
                        q.PassMarkPercent,

                    QuestionsPerAttempt =
                        q.QuestionsPerAttempt,

                    CpdPoints =
                        q.CpdPoints
                })
                .ToListAsync();

        return Ok(quizzes);
    }


    // GET /api/quizzes/1
    [HttpGet("{id:long}")]
    public async Task<ActionResult<QuizDetailsDto>>
        GetQuiz(ulong id)
    {
        var quiz =
            await _dbContext.Quizzes
                .AsNoTracking()
                .Where(q =>
                    q.Id == id &&
                    q.IsActive == true)
                .Select(q => new QuizDetailsDto
                {
                    Id = q.Id,

                    Title = q.Title,
                    ExpiresAt = q.ExpiresAt.HasValue ? DateTime.SpecifyKind(q.ExpiresAt.Value, DateTimeKind.Utc) : null,
                    AvailableFrom = q.AvailableFrom.HasValue ? DateTime.SpecifyKind(q.AvailableFrom.Value, DateTimeKind.Utc) : null,

                    Category = q.Category.Name,

                    Description =
                        q.Description,

                    Instructions =
                        q.Instructions,

                    PassMarkPercent =
                        q.PassMarkPercent,

                    QuestionsPerAttempt =
                        q.QuestionsPerAttempt,

                    CpdPoints =
                        q.CpdPoints,

                    TimeLimitMinutes =
                        q.TimeLimitMinutes
                })
                .FirstOrDefaultAsync();

        if (quiz is null)
        {
            return NotFound(new
            {
                message =
                    $"Quiz {id} was not found."
            });
        }

        return Ok(quiz);
    }
}