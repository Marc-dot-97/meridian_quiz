namespace Meridian.Shared.DTOs;

public sealed record LeaderboardEntryDto(
    int Rank,
    string DisplayName,
    int TotalXp,
    int CurrentLevel,
    int BestScorePercent);
