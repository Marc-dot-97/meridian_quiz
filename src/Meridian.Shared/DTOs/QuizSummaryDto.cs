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
    public DateTime? ExpiresAt { get; init; }
    /// <summary>True when the quiz is assigned to one of the user's departments (they have to do it).</summary>
    public bool Required { get; init; }
    public DateOnly? DueOn { get; init; }
}
