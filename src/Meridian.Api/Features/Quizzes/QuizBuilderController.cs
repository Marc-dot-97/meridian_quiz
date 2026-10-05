using System.Security.Claims;
using Meridian.Api.Data;
using Meridian.Api.Data.Entities;
using Meridian.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Features.Quizzes;

[ApiController]
[Route("api/quizzes")]
[Authorize]
public sealed class QuizBuilderController(MeridianDbContext db, ILogger<QuizBuilderController> logger,
    Meridian.Api.Features.Reports.ReportAccessService access) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(2_000_000)]
    public async Task<ActionResult<CreatedQuizResponse>> Create(CreateQuizRequest request, CancellationToken ct)
    {
        // Staff may create surveys only; quizzes are for SuperAdmin, HR and line managers.
        var author = await db.Users.AsNoTracking().SingleAsync(u => u.Id == ulong.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), ct);
        if (!await access.CanCreateQuizzesAsync(author, ct))
            return StatusCode(403, new { message = "Only line managers, HR and administrators can create quizzes. You can still create surveys." });

        var errors = QuizBuilderValidation.Validate(request);
        if (request.AvailableFrom is DateTime date)
        {
            request.AvailableFrom = QuizAvailability.AsUtc(date);
            if (request.AvailableFrom <= DateTime.UtcNow)
                errors.Add("Choose a future unlock time, or leave AvailableFrom empty for immediate availability.");
        }
        if (request.ExpiresAt is DateTime end)
        {
            request.ExpiresAt = QuizAvailability.AsUtc(end);
            if (QuizAvailability.IsExpired(request.ExpiresAt)) errors.Add("Expiry must be in the future.");
        }
        if (errors.Count > 0) return BadRequest(new { message = string.Join(" ", errors) });

        var title = request.Title.Trim();
        var categoryName = request.Category.Trim();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var category = await db.QuizCategories.SingleOrDefaultAsync(c => c.Name == categoryName, ct);
        if (category is not null && category.IsActive != true)
            return BadRequest(new { message = "This category is inactive. Choose an active category." });
        if (category is not null && await db.Quizzes.AnyAsync(q => q.CategoryId == category.Id && q.Title == title, ct))
            return Conflict(new { message = "A quiz with this title already exists in this category. Choose a different title." });

        var now = DateTime.UtcNow;
        category ??= new QuizCategory { Name = categoryName, IsActive = true, CreatedAt = now, UpdatedAt = now };
        var quiz = new Quiz
        {
            CreatedByUserId = ulong.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            DeleteAfter = request.AddToArchive ? now.AddMonths(24) : null,
            Category = category, Title = title, Description = request.Description?.Trim(),
            Instructions = request.Instructions?.Trim(), PassMarkPercent = (byte)request.PassMarkPercent,
            QuestionsPerAttempt = (ushort)request.Questions.Count, CpdPoints = request.CpdPoints,
            TimeLimitMinutes = request.TimeLimitMinutes is int limit ? (ushort)limit : null,
            AvailableFrom = request.AvailableFrom, ExpiresAt = request.ExpiresAt, IsActive = true, CreatedAt = now, UpdatedAt = now
        };
        foreach (var source in request.Questions)
        {
            var question = new Question
            {
                Category = category, QuestionText = source.Text.Trim(), Difficulty = 1, Status = 1,
                SourceType = 1, CreatedAt = now, UpdatedAt = now
            };
            for (var i = 0; i < source.Options.Count; i++)
                question.AnswerOptions.Add(new AnswerOption
                {
                    OptionText = source.Options[i].Trim(), IsCorrect = source.CorrectOptionIndex == i,
                    DisplayOrder = (ushort)(i + 1), CreatedAt = now, UpdatedAt = now
                });
            quiz.QuizQuestions.Add(new QuizQuestion
            {
                Question = question, QuestionWeight = 1m, IsActive = true, CreatedAt = now
            });
        }
        db.Quizzes.Add(quiz);
        try
        {
            // EF saves the complete quiz/category/question/option graph in one transaction.
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Quiz builder could not save the quiz");
            return Problem(statusCode: 500, title: "Quiz could not be saved",
                detail: "No partial quiz was saved. Check the API log and database schema before retrying.");
        }
        return Created($"/api/quizzes/{quiz.Id}", new CreatedQuizResponse(quiz.Id, quiz.Title, quiz.AvailableFrom, quiz.ExpiresAt));
    }
}
