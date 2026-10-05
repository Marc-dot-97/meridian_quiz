namespace Meridian.Shared.DTOs;

public sealed class CreateQuizRequest
{
    public bool AddToArchive { get; set; } = true;
    public string Title { get; set; } = "";
    public string Category { get; set; } = "Compliance";
    public string Description { get; set; } = "";
    public string Instructions { get; set; } = "Choose one answer for each question.";
    public int PassMarkPercent { get; set; } = 70;
    public decimal CpdPoints { get; set; } = 0;
    public int? TimeLimitMinutes { get; set; }
    public DateTime? AvailableFrom { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public List<QuizBuilderQuestion> Questions { get; set; } = [];
}

public sealed class QuizBuilderQuestion
{
    public Guid EditorId { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public List<string> Options { get; set; } = ["", "", "", ""];
    public int? CorrectOptionIndex { get; set; }
}

public sealed record CreatedQuizResponse(ulong Id, string Title, DateTime? AvailableFrom, DateTime? ExpiresAt = null);
public sealed record SavedLocalQuiz(ulong Id, CreateQuizRequest Definition);

public static class QuizBuilderValidation
{
    public static List<string> Validate(CreateQuizRequest request)
    {
        var errors = new List<string>();
        if (request.ExpiresAt is DateTime expiry && request.AvailableFrom is DateTime unlock &&
            QuizAvailability.AsUtc(expiry) <= QuizAvailability.AsUtc(unlock))
            errors.Add("Expiry must be after the unlock date and time.");
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 200)
            errors.Add("Enter a quiz title of 1–200 characters.");
        if (string.IsNullOrWhiteSpace(request.Category) || request.Category.Trim().Length > 100)
            errors.Add("Enter a category of 1–100 characters.");
        if ((request.Description?.Length ?? 0) > 5000 || (request.Instructions?.Length ?? 0) > 5000)
            errors.Add("Description and instructions must each be 5,000 characters or fewer.");
        if (request.PassMarkPercent is < 1 or > 100)
            errors.Add("Pass mark must be between 1 and 100.");
        if (request.CpdPoints is < 0 or > 1000 || decimal.Round(request.CpdPoints, 2) != request.CpdPoints)
            errors.Add("CPD points must be between 0 and 1,000, with at most two decimal places.");
        if (request.TimeLimitMinutes is not null && request.TimeLimitMinutes is < 1 or > 1440)
            errors.Add("Time limit must be between 1 and 1,440 minutes, or left blank.");
        if (request.Questions is null || request.Questions.Count is < 1 or > 100)
        {
            errors.Add("Add between 1 and 100 questions.");
            return errors;
        }
        for (var i = 0; i < request.Questions.Count; i++)
        {
            var q = request.Questions[i];
            var prefix = $"Question {i + 1}: ";
            if (q is null) { errors.Add(prefix + "question is missing."); continue; }
            if (string.IsNullOrWhiteSpace(q.Text) || q.Text.Trim().Length > 4000)
                errors.Add(prefix + "enter question text of 1–4,000 characters.");
            if (q.Options is null || q.Options.Count is < 2 or > 10)
            {
                errors.Add(prefix + "add between 2 and 10 choices.");
                continue;
            }
            if (q.Options.Any(o => string.IsNullOrWhiteSpace(o) || o.Trim().Length > 1000))
                errors.Add(prefix + "each choice needs 1–1,000 characters.");
            if (q.Options.Select(o => o?.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != q.Options.Count)
                errors.Add(prefix + "choices must be different.");
            if (q.CorrectOptionIndex is null || q.CorrectOptionIndex < 0 || q.CorrectOptionIndex >= q.Options.Count)
                errors.Add(prefix + "select the correct answer.");
        }
        return errors;
    }
}

// Stored/API dates are UTC. The editor explicitly uses South African time (UTC+02:00).
public static class QuizAvailability
{
    public static DateTime AsUtc(DateTime value) => value.Kind == DateTimeKind.Local
        ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    public static bool IsUnlocked(DateTime? value) => value is null || DateTime.UtcNow >= AsUtc(value.Value);
    public static bool IsExpired(DateTime? value) => value is not null && DateTime.UtcNow >= AsUtc(value.Value);
    public static bool CanStart(DateTime? availableFrom, DateTime? expiresAt) => IsUnlocked(availableFrom) && !IsExpired(expiresAt);
    public static string Display(DateTime value) => AsUtc(value).AddHours(2).ToString("dd MMM yyyy 'at' HH:mm") + " SAST";
}
