namespace Meridian.Shared.DTOs;

public sealed record UserProgressDto(
    int TotalXp,
    int CurrentLevel,
    int QuizzesCompleted,
    int QuizzesPassed,
    int CurrentStreak,
    int LongestStreak);
