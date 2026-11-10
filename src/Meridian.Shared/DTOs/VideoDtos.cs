namespace Meridian.Shared.DTOs;

/// <summary>
/// The "watch first" video of a quiz, for the signed-in person. Completed = watched to the end (only ever needed once).
/// MaxPosition = furthest point reached (seconds); they may not skip beyond it until completed. ChunkBytes = upload piece size.
/// Locked = the quiz in UnlockAfterQuizId hasn't been passed yet. InQuiz = an attempt on this quiz is open, so the video is hidden.
/// </summary>
public sealed record QuizVideoDto(
    bool HasVideo,
    bool Completed,
    decimal MaxPosition,
    decimal? DurationSeconds,
    bool CanManage,
    string? OriginalName,
    long? SizeBytes,
    DateTime? UploadedAt,
    int ChunkBytes,
    ulong? UnlockAfterQuizId = null,
    string? UnlockAfterTitle = null,
    bool Locked = false,
    bool InQuiz = false);

public sealed record VideoUnlockRequest(ulong? UnlockAfterQuizId);

public sealed record VideoProgressRequest(decimal Position, decimal Duration, bool Ended);

public sealed record VideoProgressResponse(decimal MaxPosition, bool Completed);
