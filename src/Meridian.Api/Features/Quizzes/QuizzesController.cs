using Meridian.Api.Data;
using Meridian.Api.Features.Assignments;
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
    private readonly AssignmentService _assignments;
    private readonly AssignmentStore _assignmentStore;

    public QuizzesController(
        MeridianDbContext dbContext,
        AssignmentService assignments,
        AssignmentStore assignmentStore)
    {
        _dbContext = dbContext;
        _assignments = assignments;
        _assignmentStore = assignmentStore;
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

        // Department assignments: "Required" for the user's departments; restricted quizzes are hidden from everyone else.
        var viewer = await _assignments.ViewerAsync(User, HttpContext.RequestAborted);
        if (viewer is null) return Forbid();
        var assigned = await _assignmentStore.GetAllAsync(Meridian.Shared.DTOs.AssignmentKinds.Quiz, HttpContext.RequestAborted);
        var visible = new List<QuizSummaryDto>();
        foreach (var quiz in quizzes)
        {
            if (!assigned.TryGetValue(quiz.Id.ToString(), out var a)) { visible.Add(quiz); continue; }
            if (!a.IsVisibleTo(viewer.Departments, viewer.SeesEverything)) continue;
            quiz.Required = a.IsRequiredFor(viewer.Departments);
            quiz.DueOn = quiz.Required ? a.DueOn : null;
            visible.Add(quiz);
        }

        return Ok(visible);
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

        if (quiz is not null)
        {
            var a = await _assignmentStore.GetAsync(Meridian.Shared.DTOs.AssignmentKinds.Quiz, quiz.Id.ToString(), HttpContext.RequestAborted);
            if (a.AssignedOnly)
            {
                var viewer = await _assignments.ViewerAsync(User, HttpContext.RequestAborted);
                if (viewer is null || !a.IsVisibleTo(viewer.Departments, viewer.SeesEverything)) quiz = null;
            }
        }

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