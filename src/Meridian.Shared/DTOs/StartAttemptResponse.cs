namespace Meridian.Shared.DTOs;

public sealed record StartAttemptResponse(
    Guid AttemptId,
    int TotalQuestions,
    QuestionDto FirstQuestion);
