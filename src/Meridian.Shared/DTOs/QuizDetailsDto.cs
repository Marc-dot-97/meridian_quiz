namespace Meridian.Shared.DTOs;

public sealed record QuizDetailsDto(
    ulong Id,
    string Title,
    string Category,
    string? Description,
    string? Instructions,
    int PassMarkPercent,
    int QuestionsPerAttempt,
    decimal CpdPoints,
    int? TimeLimitMinutes);
