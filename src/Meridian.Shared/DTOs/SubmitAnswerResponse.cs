namespace Meridian.Shared.DTOs;

public sealed record SubmitAnswerResponse(
    bool IsCorrect,
    int AnsweredCount,
    int TotalQuestions,
    int CurrentScorePercent,
    int CurrentXp,
    bool IsComplete,
    QuestionDto? NextQuestion);
