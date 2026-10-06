using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Meridian.Shared.DTOs;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace Meridian.Client.Services;

public sealed class SurveyService(HttpClient http, IConfiguration configuration, IJSRuntime js,
    AuthenticationStateProvider auth)
{
    private const string StorageKey = "meridian.surveys.v1";
    private readonly bool _local = configuration.GetValue<bool>("Development:UseLocalMode");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<List<SurveyListItemDto>> ListAsync()
    {
        if (!_local)
        {
            using var response = await http.GetAsync("api/surveys");
            return await ReadResponseAsync<List<SurveyListItemDto>>(response);
        }
        await _gate.WaitAsync();
        try
        {
            var state = await LoadAsync();
            return state.Surveys.OrderByDescending(s => s.CreatedAt).Select(s =>
                new SurveyListItemDto(s.Id, s.Title, s.Questions.Count, s.CreatedAt,
                    state.Responses.Where(r => r.SurveyId == s.Id).Select(r => r.Respondent).Distinct().Count())).ToList();
        }
        finally { _gate.Release(); }
    }

    public async Task CreateAsync(CreateSurveyRequest request)
    {
        var errors = SurveyValidation.Definition(request);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        if (!_local)
        {
            using var response = await http.PostAsJsonAsync("api/surveys", request);
            if (!response.IsSuccessStatusCode) throw await ApiRequestException.FromResponseAsync(response, default);
            return;
        }
        await _gate.WaitAsync();
        try
        {
            var state = await LoadAsync();
            var copy = JsonSerializer.Deserialize<CreateSurveyRequest>(JsonSerializer.Serialize(request))!;
            state.Surveys.Add(new(Guid.NewGuid(), copy.Title.Trim(), copy.Description?.Trim() ?? "", copy.Questions, DateTime.UtcNow));
            await SaveAsync(state);
        }
        finally { _gate.Release(); }
    }

    public async Task<List<DashboardSurveyDto>> DashboardAsync()
    {
        if (!_local)
        {
            using var response = await http.GetAsync("api/surveys/dashboard");
            return await ReadResponseAsync<List<DashboardSurveyDto>>(response);
        }
        var respondent = await RespondentAsync();
        await _gate.WaitAsync();
        try
        {
            var state = await LoadAsync();
            return state.Surveys.OrderByDescending(s => s.CreatedAt).Select(s =>
                new DashboardSurveyDto(s.Id, s.Title, s.Questions.Count, state.Responses
                    .Where(r => r.SurveyId == s.Id && r.Respondent == respondent)
                    .OrderByDescending(r => r.SubmittedAt).FirstOrDefault()?.SubmittedAt)).ToList();
        }
        finally { _gate.Release(); }
    }

    public async Task<SurveyOpenDto> GetAsync(Guid surveyId)
    {
        if (!_local)
        {
            using var response = await http.GetAsync($"api/surveys/{surveyId}");
            return await ReadResponseAsync<SurveyOpenDto>(response);
        }
        var respondent = await RespondentAsync();
        await _gate.WaitAsync();
        try
        {
            var state = await LoadAsync();
            var survey = state.Surveys.SingleOrDefault(s => s.Id == surveyId)
                ?? throw new InvalidOperationException("Survey not found.");
            var response = state.Responses.Where(r => r.SurveyId == surveyId && r.Respondent == respondent)
                .OrderByDescending(r => r.SubmittedAt).FirstOrDefault();
            return new(survey, response?.SubmittedAt, response?.Answers);
        }
        finally { _gate.Release(); }
    }

    public async Task SubmitAsync(Guid surveyId, SubmitSurveyRequest request)
    {
        if (!_local)
        {
            using var response = await http.PostAsJsonAsync($"api/surveys/{surveyId}/responses", request);
            if (!response.IsSuccessStatusCode) throw await ApiRequestException.FromResponseAsync(response, default);
            return;
        }
        var respondent = await RespondentAsync();
        await _gate.WaitAsync();
        try
        {
            var state = await LoadAsync();
            if (state.Responses.Any(r => r.SurveyId == surveyId && r.Respondent == respondent)) return;
            var survey = state.Surveys.FirstOrDefault(s => s.Id == surveyId);
            if (survey is null) throw new InvalidOperationException("Survey not found.");
            var errors = SurveyValidation.Answers(survey, request);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
            state.Responses.Add(new(surveyId, Guid.Empty, respondent, DateTime.UtcNow, request.Answers));
            await SaveAsync(state);
        }
        finally { _gate.Release(); }
    }

    private async Task<string> RespondentAsync()
    {
        var user = (await auth.GetAuthenticationStateAsync()).User;
        var id = user.FindFirst(ClaimTypes.Email)?.Value; // Preserve existing local survey response keys.
        if (user.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Sign in before responding to a survey.");
        return id.Trim().ToLowerInvariant();
    }

    private async Task<LocalSurveyState> LoadAsync()
    {
        var json = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
        if (string.IsNullOrEmpty(json)) return new();
        try
        {
            var state = JsonSerializer.Deserialize<LocalSurveyState>(json) ?? throw new JsonException();
            if (state.Surveys is null || state.Responses is null || state.Surveys.Any(s => s is null || s.Questions is null))
                throw new JsonException();
            return state;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Saved survey data could not be read. It has not been overwritten.");
        }
    }
    private async Task SaveAsync(LocalSurveyState state) =>
        await js.InvokeVoidAsync("localStorage.setItem", StorageKey, JsonSerializer.Serialize(state));
    private static async Task<T> ReadResponseAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw await ApiRequestException.FromResponseAsync(response, default);
        return await response.Content.ReadFromJsonAsync<T>() ?? throw new InvalidOperationException("The API returned no survey data.");
    }
}

public sealed class LocalSurveyState
{
    public List<SurveyDto> Surveys { get; set; } = [];
    // Retained only to preserve existing browser data; selection is no longer used.
    public Guid? ActiveSurveyId { get; set; }
    public List<LocalSurveyResponse> Responses { get; set; } = [];
}
public sealed record LocalSurveyResponse(Guid SurveyId, Guid AttemptId, string Respondent,
    DateTime SubmittedAt, List<SurveyAnswerDto> Answers);
