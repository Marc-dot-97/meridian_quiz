namespace Meridian.Shared.DTOs;

/// <summary>One certificate in a manager's list (Reports page). CompletedAt is UTC.</summary>
public sealed record CertificateListItemDto(
    Guid AttemptId,
    string Name,
    string Department,
    string QuizTitle,
    string Category,
    DateTime CompletedAt,
    int ScorePercent,
    string CertificateNumber);
