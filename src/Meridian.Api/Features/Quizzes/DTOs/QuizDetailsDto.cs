namespace Meridian.Api.Features.Quizzes.Dtos;

public sealed class QuizDetailsDto
{
    public ulong Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Instructions { get; set; }

    public int PassMarkPercent { get; set; }

    public int QuestionsPerAttempt { get; set; }

    public decimal CpdPoints { get; set; }

    public int? TimeLimitMinutes { get; set; }
}