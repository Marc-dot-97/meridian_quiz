namespace Meridian.Shared.DTOs;

public sealed record QuizSummaryDto(
    ulong Id,
    string Title,
    string Category,
    int PassMarkPercent,
    int QuestionsPerAttempt,
    decimal CpdPoints);
