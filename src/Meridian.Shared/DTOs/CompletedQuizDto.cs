namespace Meridian.Shared.DTOs;

public sealed record CompletedQuizDto(
    ulong QuizId,
    string Title,
    string Category,
    int ScorePercent,
    DateTime CompletedAt,
    decimal CpdPointsEarned,
    // Latest PASSED attempt of this quiz, for the Certificate of Completion (null = not passed yet).
    Guid? CertificateAttemptId = null);
