namespace Meridian.Shared.DTOs;

public sealed record CompleteAttemptResponse(
    Guid AttemptId,
    int ScorePercent,
    bool Passed,
    int CorrectAnswers,
    int TotalQuestions,
    int XpEarned,
    decimal CpdPointsEarned);
