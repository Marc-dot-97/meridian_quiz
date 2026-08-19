namespace Meridian.Api.Features.Quizzes.Dtos;

public sealed class QuizSummaryDto
{
    public ulong Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public int PassMarkPercent { get; set; }

    public int QuestionsPerAttempt { get; set; }

    public decimal CpdPoints { get; set; }
}