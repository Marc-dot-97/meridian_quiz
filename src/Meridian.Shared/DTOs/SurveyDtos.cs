namespace Meridian.Shared.DTOs;

public enum SurveyQuestionType { MultipleChoice, ShortText, Rating }

public sealed class CreateSurveyRequest
{
    public bool AddToArchive { get; set; } = true;
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<SurveyQuestionDto> Questions { get; set; } = [new()];
}

public sealed class SurveyQuestionDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public SurveyQuestionType Type { get; set; } = SurveyQuestionType.MultipleChoice;
    public bool Required { get; set; } = true;
    public List<string> Options { get; set; } = ["", "", ""];
}

public sealed record SurveyDto(Guid Id, string Title, string Description,
    List<SurveyQuestionDto> Questions, DateTime CreatedAt);
public sealed record SurveyListItemDto(Guid Id, string Title, int QuestionCount,
    DateTime CreatedAt, int ResponseCount);
public sealed record DashboardSurveyDto(Guid Id, string Title, int QuestionCount, DateTime? CompletedAt);
public sealed record SurveyOpenDto(SurveyDto Survey, DateTime? CompletedAt, List<SurveyAnswerDto>? Answers);
public sealed class SurveyAnswerDto
{
    public Guid QuestionId { get; set; }
    public string? Text { get; set; }
    public int? ChoiceIndex { get; set; }
    public int? Rating { get; set; }
}
public sealed class SubmitSurveyRequest
{
    public List<SurveyAnswerDto> Answers { get; set; } = [];
}

public static class SurveyValidation
{
    public static List<string> Definition(CreateSurveyRequest request)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 200)
            errors.Add("Enter a survey title of 1–200 characters.");
        if ((request.Description?.Length ?? 0) > 2000)
            errors.Add("Description must be 2,000 characters or fewer.");
        if (request.Questions is null || request.Questions.Count is < 1 or > 50)
        { errors.Add("Add between 1 and 50 questions."); return errors; }
        var ids = new HashSet<Guid>();
        for (var i = 0; i < request.Questions.Count; i++)
        {
            var q = request.Questions[i];
            var label = $"Question {i + 1}: ";
            if (q is null) { errors.Add(label + "question is missing."); continue; }
            if (q.Id == Guid.Empty || !ids.Add(q.Id)) errors.Add(label + "invalid or duplicate question ID.");
            if (string.IsNullOrWhiteSpace(q.Text) || q.Text.Trim().Length > 2000)
                errors.Add(label + "enter question text of 1–2,000 characters.");
            if (!Enum.IsDefined(q.Type)) errors.Add(label + "choose a supported question type.");
            if (q.Type == SurveyQuestionType.MultipleChoice)
            {
                if (q.Options is null || q.Options.Count is < 2 or > 10)
                { errors.Add(label + "add 2–10 choices."); continue; }
                if (q.Options.Any(x => string.IsNullOrWhiteSpace(x) || x.Trim().Length > 500))
                    errors.Add(label + "each choice needs 1–500 characters.");
                if (q.Options.Select(x => x?.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != q.Options.Count)
                    errors.Add(label + "choices must be different.");
            }
        }
        return errors;
    }

    public static List<string> Answers(SurveyDto survey, SubmitSurveyRequest request)
    {
        var errors = new List<string>();
        if (request.Answers is null || request.Answers.Count > survey.Questions.Count)
        { errors.Add("The response contains an invalid number of answers."); return errors; }
        var ids = new HashSet<Guid>();
        foreach (var answer in request.Answers)
        {
            if (answer is null || !ids.Add(answer.QuestionId) || !survey.Questions.Any(q => q.Id == answer.QuestionId))
                errors.Add("The response contains an unknown or duplicate question.");
        }
        if (errors.Count > 0) return errors;
        for (var i = 0; i < survey.Questions.Count; i++)
        {
            var q = survey.Questions[i];
            var a = request.Answers.FirstOrDefault(x => x.QuestionId == q.Id);
            var label = $"Question {i + 1}: ";
            var answered = q.Type switch
            {
                SurveyQuestionType.MultipleChoice => a?.ChoiceIndex is not null,
                SurveyQuestionType.Rating => a?.Rating is not null,
                _ => !string.IsNullOrWhiteSpace(a?.Text)
            };
            if (q.Required && !answered) errors.Add(label + "an answer is required.");
            if (a is null) continue;
            if (a.ChoiceIndex is int choice && (q.Type != SurveyQuestionType.MultipleChoice || choice < 0 || choice >= q.Options.Count))
                errors.Add(label + "choose one of the listed options.");
            if (a.Rating is int rating && (q.Type != SurveyQuestionType.Rating || rating is < 1 or > 5))
                errors.Add(label + "choose a rating from 1 to 5.");
            if (!string.IsNullOrEmpty(a.Text) && (q.Type != SurveyQuestionType.ShortText || a.Text.Length > 2000))
                errors.Add(label + "text must be 2,000 characters or fewer and only used for a text question.");
        }
        return errors;
    }
}
