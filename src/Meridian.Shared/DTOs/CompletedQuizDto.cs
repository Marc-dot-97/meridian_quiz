namespace Meridian.Shared.DTOs;

public sealed record CompletedQuizDto(
    ulong QuizId,
    string Title,
    string Category,
    int ScorePercent,
    DateTime CompletedAt,
    decimal CpdPointsEarned);
