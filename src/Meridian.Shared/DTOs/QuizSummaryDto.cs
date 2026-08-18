namespace Meridian.Shared.DTOs;

public sealed record QuizSummaryDto(
    ulong Id,
    string Title,
    string Category,
    int PassMarkPercent,
    int QuestionsPerAttempt,
    decimal CpdPoints)
{
    // Null means no scheduled lock date has been supplied by the API.
    public DateTime? AvailableFrom { get; init; }
}
