using System.Security.Claims;
using System.Text.Json;
using Meridian.Api.Data;
using Meridian.Api.Data.Entities;
using Meridian.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Meridian.Api.Features.Quizzes;

[ApiController, Authorize, Route("api/quiz-attempts")]
public sealed class QuizAttemptsController(MeridianDbContext db) : ControllerBase
{
    private ulong UserId => ulong.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public sealed record SnapshotQuestion(QuestionDto Question, ulong CorrectOptionId);
    public sealed record SavedAnswer(ulong QuestionId, ulong AnswerOptionId);
    private static List<SnapshotQuestion> Questions(QuizAttempt a) => JsonSerializer.Deserialize<List<SnapshotQuestion>>(a.QuestionsJson ?? "[]")!;
    private static List<SavedAnswer> Answers(QuizAttempt a) => JsonSerializer.Deserialize<List<SavedAnswer>>(a.AnswersJson ?? "[]")!;
    private static CompleteAttemptResponse Result(QuizAttempt a) => new(a.Id, (int)a.ScorePercent, a.Passed,
        a.CorrectAnswers, a.TotalQuestions, (int)a.PointsEarned, a.CpdPointsEarned);

    [HttpPost]
    public async Task<ActionResult<StartAttemptResponse>> Start(StartAttemptRequest request)
    {
        var quiz = await db.Quizzes.Include(q => q.QuizQuestions).ThenInclude(q => q.Question)
            .ThenInclude(q => q.AnswerOptions).SingleOrDefaultAsync(q => q.Id == request.QuizId && q.IsActive == true);
        if (quiz == null) return NotFound(new { message = "Quiz not found." });
        if (quiz.AvailableFrom.HasValue && !QuizAvailability.IsUnlocked(quiz.AvailableFrom.Value))
            return BadRequest(new { message = "This quiz is not available yet." });
        if (QuizAvailability.IsExpired(quiz.ExpiresAt)) return BadRequest(new { message = "This quiz has expired." });
        var selected = quiz.QuizQuestions.Where(q => q.IsActive == true)
            .OrderBy(_ => Random.Shared.Next()).Take(quiz.QuestionsPerAttempt).Select(q => q.Question).ToList();
        if (selected.Count == 0 || selected.Count != quiz.QuestionsPerAttempt
            || selected.Any(q => q.AnswerOptions.Count < 2 || q.AnswerOptions.Count(o => o.IsCorrect) != 1))
            return BadRequest(new { message = "Quiz questions are incomplete. Ask a quiz author to check the quiz." });
        var questions = selected.Select((q, i) => new SnapshotQuestion(new QuestionDto(q.Id, i + 1, q.QuestionText,
            q.AnswerOptions.OrderBy(o => o.DisplayOrder).Select(o => new AnswerOptionDto(o.Id, o.OptionText)).ToList()),
            q.AnswerOptions.Single(o => o.IsCorrect).Id)).ToList();
        var now = DateTime.UtcNow;
        var attempt = new QuizAttempt { Id = Guid.NewGuid(), UserId = UserId, QuizId = quiz.Id,
            Status = 1, StartedAt = now, CreatedAt = now, UpdatedAt = now, TotalQuestions = (ushort)questions.Count,
            QuestionsJson = JsonSerializer.Serialize(questions), AnswersJson = "[]", SnapshotPassMark = quiz.PassMarkPercent,
            SnapshotCpdPoints = quiz.CpdPoints, SnapshotTimeLimitMinutes = quiz.TimeLimitMinutes };
        db.QuizAttempts.Add(attempt); await db.SaveChangesAsync();
        return Ok(new StartAttemptResponse(attempt.Id, questions.Count, questions[0].Question));
    }

    // FOR UPDATE serializes retries and simultaneous submissions across API instances.
    private async Task<QuizAttempt?> Locked(Guid id)
    {
        var rows = await db.QuizAttempts.FromSqlInterpolated($"SELECT * FROM quiz_attempts WHERE id = {id} AND user_id = {UserId} FOR UPDATE").ToListAsync();
        return rows.SingleOrDefault();
    }
    [HttpPost("{id:guid}/answers")]
    public async Task<ActionResult<SubmitAnswerResponse>> Answer(Guid id, SubmitAnswerRequest request)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var a = await Locked(id);
        if (a == null) return NotFound();
        var questions = Questions(a); var answers = Answers(a);
        var existing = answers.FindIndex(x => x.QuestionId == request.QuestionId);
        if (existing >= 0)
        {
            if (answers[existing].AnswerOptionId != request.AnswerOptionId)
                return Conflict(new { message = "This question has already been answered." });
            return Ok(AnswerResult(questions, answers, existing));
        }
        if (a.CompletedAt != null || answers.Count >= questions.Count)
            return Conflict(new { message = "This attempt no longer accepts answers." });
        if (a.SnapshotTimeLimitMinutes.HasValue && DateTime.UtcNow > a.StartedAt.AddMinutes(a.SnapshotTimeLimitMinutes.Value))
            return BadRequest(new { message = "The time limit has elapsed. Start a new attempt." });
        var current = questions[answers.Count];
        if (current.Question.Id != request.QuestionId || !current.Question.Options.Any(o => o.Id == request.AnswerOptionId))
            return BadRequest(new { message = "The answer does not belong to the current question." });
        answers.Add(new(request.QuestionId, request.AnswerOptionId));
        a.AnswersJson = JsonSerializer.Serialize(answers); a.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(); await tx.CommitAsync();
        return Ok(AnswerResult(questions, answers, answers.Count - 1));
    }
    private static SubmitAnswerResponse AnswerResult(List<SnapshotQuestion> questions, List<SavedAnswer> answers, int index)
    {
        var correct = answers.Take(index + 1).Count(a => questions.Single(q => q.Question.Id == a.QuestionId).CorrectOptionId == a.AnswerOptionId);
        var complete = index + 1 == questions.Count;
        return new(questions[index].CorrectOptionId == answers[index].AnswerOptionId, index + 1, questions.Count,
            (int)Math.Round(correct * 100m / (index + 1)), correct * 10, complete, complete ? null : questions[index + 1].Question);
    }
    [HttpPost("{id:guid}/complete")]
    public async Task<ActionResult<CompleteAttemptResponse>> Complete(Guid id)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var a = await Locked(id);
        if (a == null) return NotFound();
        if (a.CompletedAt != null) return Ok(Result(a));
        var questions = Questions(a); var answers = Answers(a);
        if (questions.Count == 0 || answers.Count != questions.Count)
            return BadRequest(new { message = "Answer every question before completing the quiz." });
        a.CorrectAnswers = (ushort)answers.Count(x => questions.Single(q => q.Question.Id == x.QuestionId).CorrectOptionId == x.AnswerOptionId);
        a.ScorePercent = Math.Round(a.CorrectAnswers * 100m / questions.Count);
        a.Passed = a.ScorePercent >= a.SnapshotPassMark!.Value;
        a.PointsEarned = (uint)(a.CorrectAnswers * 10 + (a.Passed ? 50 : 0));
        a.CpdPointsEarned = a.Passed ? a.SnapshotCpdPoints!.Value : 0;
        a.Status = 2; a.CompletedAt = DateTime.UtcNow; a.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(); await tx.CommitAsync();
        return Ok(Result(a));
    }
    [HttpGet("{id:guid}/result")]
    public async Task<ActionResult<CompleteAttemptResponse>> GetResult(Guid id)
    {
        var a = await db.QuizAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == UserId && x.CompletedAt != null);
        return a == null ? NotFound() : Ok(Result(a));
    }
    [HttpGet("completed/me")]
    public async Task<ActionResult<List<CompletedQuizDto>>> Completed()
    {
        var rows = await db.QuizAttempts.AsNoTracking().Include(a => a.Quiz).ThenInclude(q => q.Category)
            .Where(a => a.UserId == UserId && a.CompletedAt != null).OrderByDescending(a => a.CompletedAt).ToListAsync();
        return Ok(rows.GroupBy(a => a.QuizId).Select(g => g.First()).Select(a => new CompletedQuizDto(a.QuizId,
            a.Quiz.Title, a.Quiz.Category.Name, (int)a.ScorePercent, DateTime.SpecifyKind(a.CompletedAt!.Value, DateTimeKind.Utc), a.CpdPointsEarned)).ToList());
    }
}
