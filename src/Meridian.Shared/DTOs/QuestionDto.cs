namespace Meridian.Shared.DTOs;

public sealed record QuestionDto(
    ulong Id,
    int DisplayOrder,
    string Text,
    IReadOnlyList<AnswerOptionDto> Options);
