namespace Meridian.Api.Features.Quizzes.Dtos;

public sealed class QuizSummaryDto
{
    public ulong Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public DateTime? AvailableFrom { get; set; }
    public DateTime? ExpiresAt { get; set; }

    public string Category { get; set; } = string.Empty;

    public int PassMarkPercent { get; set; }

    public int QuestionsPerAttempt { get; set; }

    public decimal CpdPoints { get; set; }
}