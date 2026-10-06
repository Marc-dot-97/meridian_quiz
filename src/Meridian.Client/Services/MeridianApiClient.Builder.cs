using System.Net.Http.Json;
using System.Text.Json;
using Meridian.Shared.DTOs;
using Microsoft.JSInterop;

namespace Meridian.Client.Services;

public sealed partial class MeridianApiClient
{
    private const string QuizStorageKey = "meridian.quiz-builder.v1";
    private readonly IJSRuntime _js;
    private readonly SemaphoreSlim _catalogueLock = new(1, 1);
    private List<SavedLocalQuiz> _createdQuizzes = [];

    private async Task LoadCreatedQuizzesAsync()
    {
        await _catalogueLock.WaitAsync();
        try { await ReadCreatedQuizzesAsync(); }
        finally { _catalogueLock.Release(); }
    }

    private async Task ReadCreatedQuizzesAsync()
    {
        var json = await _js.InvokeAsync<string?>("localStorage.getItem", QuizStorageKey);
        try
        {
            List<SavedLocalQuiz> quizzes = string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<SavedLocalQuiz>>(json) ?? [];
            if (quizzes.Any(q => q is null || q.Definition is null || QuizBuilderValidation.Validate(q.Definition).Count > 0))
                throw new JsonException();
            _createdQuizzes = quizzes;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Saved quiz data could not be read. No saved quizzes have been overwritten.");
        }
    }

    public async Task<CreatedQuizResponse> CreateQuizAsync(CreateQuizRequest request, CancellationToken ct = default)
    {
        var errors = QuizBuilderValidation.Validate(request);
        if (QuizAvailability.IsExpired(request.ExpiresAt)) errors.Add("Expiry must be in the future.");
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        if (!_local)
        {
            using var response = await _http.PostAsJsonAsync("api/quizzes", request, ct);
            return await ReadAsync<CreatedQuizResponse>(response, ct);
        }
        await _catalogueLock.WaitAsync(ct);
        try
        {
            await ReadCreatedQuizzesAsync();
            var id = Math.Max(1000UL, _createdQuizzes.Select(q => q.Id).DefaultIfEmpty(999UL).Max() + 1);
            // Copy the form so further editor changes cannot alter the saved definition.
            var copy = JsonSerializer.Deserialize<CreateQuizRequest>(JsonSerializer.Serialize(request))!;
            copy.Title = copy.Title.Trim();
            copy.Category = copy.Category.Trim();
            var next = new List<SavedLocalQuiz>(_createdQuizzes) { new(id, copy) };
            // Only update memory/show success after browser persistence succeeds.
            await _js.InvokeVoidAsync("localStorage.setItem", QuizStorageKey, JsonSerializer.Serialize(next));
            _createdQuizzes = next;
            return new(id, copy.Title, copy.AvailableFrom, copy.ExpiresAt);
        }
        finally { _catalogueLock.Release(); }
    }

    private IReadOnlyList<QuizSummaryDto> AllLocalQuizzes() => MockQuizzes.Concat(_createdQuizzes.Select(q =>
        new QuizSummaryDto(q.Id, q.Definition.Title, q.Definition.Category, q.Definition.PassMarkPercent,
            q.Definition.Questions.Count, q.Definition.CpdPoints) { AvailableFrom = q.Definition.AvailableFrom, ExpiresAt = q.Definition.ExpiresAt })).ToList();

    private QuizDetailsDto GetLocalQuiz(ulong id)
    {
        var custom = _createdQuizzes.FirstOrDefault(q => q.Id == id);
        if (custom is null)
            return GetQuiz(id) with { AvailableFrom = MockQuizzes.First(q => q.Id == id).AvailableFrom };
        var q = custom.Definition;
        return new(id, q.Title, q.Category, q.Description, q.Instructions, q.PassMarkPercent,
            q.Questions.Count, q.CpdPoints, q.TimeLimitMinutes) { AvailableFrom = q.AvailableFrom, ExpiresAt = q.ExpiresAt };
    }

    private List<MockQuestion> BuildLocalQuestions(ulong id)
    {
        var custom = _createdQuizzes.FirstOrDefault(q => q.Id == id);
        if (custom is null) return BuildQuestions(id);
        return custom.Definition.Questions.Select((q, i) =>
        {
            // Question IDs only need to be unique within this local quiz attempt.
            var questionId = (ulong)(i + 1);
            var options = q.Options.Select((text, j) => new AnswerOptionDto((ulong)(i * 10 + j + 1), text)).ToList();
            return new MockQuestion(new QuestionDto(questionId, i + 1, q.Text, options),
                options[q.CorrectOptionIndex!.Value].Id);
        }).ToList();
    }
}
