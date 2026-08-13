using System.Net.Http.Json;
using Meridian.Shared.DTOs;

namespace Meridian.Client.Services;

public sealed class MeridianApiClient
{
    private readonly HttpClient _http;
    private readonly bool _local;
    private readonly Dictionary<Guid, MockAttempt> _attempts = [];
    private readonly List<CompletedQuizDto> _completedQuizzes =
    [
        new(10, "FAIS Regulatory Refresher", "Compliance", 90, new DateTime(2026, 8, 8), 2m),
        new(11, "Cybersecurity Awareness", "Technology", 80, new DateTime(2026, 7, 30), 1.5m)
    ];
    private int _totalXp = 240;
    private int _completed = 6;
    private int _passed = 5;
    private int _streak = 4;
    private int _longestStreak = 6;

    public MeridianApiClient(HttpClient http, IConfiguration configuration)
    {
        _http = http;
        _local = configuration.GetValue<bool>("Development:UseLocalMode");
    }

    public async Task<IReadOnlyList<QuizSummaryDto>> GetQuizzesAsync(CancellationToken ct = default)
    {
        if (_local)
        {
            await Task.Delay(100, ct);
            return MockQuizzes;
        }

        using var response = await _http.GetAsync("api/quizzes", ct);
        return await ReadAsync<List<QuizSummaryDto>>(response, ct);
    }

    public async Task<IReadOnlyList<CompletedQuizDto>> GetCompletedQuizzesAsync(CancellationToken ct = default)
    {
        if (_local)
        {
            await Task.Delay(70, ct);
            return _completedQuizzes
                .OrderByDescending(x => x.CompletedAt)
                .ToList();
        }

        using var response = await _http.GetAsync("api/quiz-attempts/completed/me", ct);
        return await ReadAsync<List<CompletedQuizDto>>(response, ct);
    }

    public async Task<QuizDetailsDto> GetQuizAsync(ulong quizId, CancellationToken ct = default)
    {
        if (_local)
        {
            await Task.Delay(80, ct);
            return GetQuiz(quizId);
        }
        using var response = await _http.GetAsync($"api/quizzes/{quizId}", ct);
        return await ReadAsync<QuizDetailsDto>(response, ct);
    }

    public async Task<StartAttemptResponse> StartAttemptAsync(ulong quizId, CancellationToken ct = default)
    {
        if (_local)
        {
            await Task.Delay(80, ct);

            var quizSummary = MockQuizzes.FirstOrDefault(x => x.Id == quizId)
                ?? throw new InvalidOperationException("Quiz not found.");

            if (quizSummary.AvailableFrom is DateTime availableFrom && DateTime.Today < availableFrom.Date)
            {
                throw new InvalidOperationException(
                    $"This quiz unlocks on {availableFrom:dd MMMM yyyy}.");
            }

            var questions = BuildQuestions(quizId).OrderBy(_ => Random.Shared.Next()).ToList();
            questions = questions.Select((q, i) => q with { Question = q.Question with { DisplayOrder = i + 1 } }).ToList();
            var id = Guid.NewGuid();
            _attempts[id] = new MockAttempt(quizId, questions);
            return new StartAttemptResponse(id, questions.Count, questions[0].Question);
        }
        using var response = await _http.PostAsJsonAsync("api/quiz-attempts", new StartAttemptRequest(quizId), ct);
        return await ReadAsync<StartAttemptResponse>(response, ct);
    }

    public async Task<SubmitAnswerResponse> SubmitAnswerAsync(
        Guid attemptId, ulong questionId, ulong answerOptionId, CancellationToken ct = default)
    {
        if (_local)
        {
            await Task.Delay(80, ct);
            if (!_attempts.TryGetValue(attemptId, out var attempt))
                throw new InvalidOperationException("Attempt not found.");
            var current = attempt.Questions[attempt.CurrentIndex];
            if (current.Question.Id != questionId)
                throw new InvalidOperationException("Question does not match the current attempt.");
            var correct = current.CorrectOptionId == answerOptionId;
            if (correct) attempt.CorrectAnswers++;
            attempt.CurrentIndex++;
            var complete = attempt.CurrentIndex >= attempt.Questions.Count;
            var score = (int)Math.Round(attempt.CorrectAnswers * 100m / attempt.CurrentIndex);
            var next = complete ? null : attempt.Questions[attempt.CurrentIndex].Question;
            return new SubmitAnswerResponse(correct, attempt.CurrentIndex, attempt.Questions.Count, score,
                attempt.CorrectAnswers * 10, complete, next);
        }

        using var response = await _http.PostAsJsonAsync(
            $"api/quiz-attempts/{attemptId}/answers",
            new SubmitAnswerRequest(questionId, answerOptionId), ct);
        return await ReadAsync<SubmitAnswerResponse>(response, ct);
    }

    public async Task<CompleteAttemptResponse> CompleteAttemptAsync(Guid attemptId, CancellationToken ct = default)
    {
        if (_local)
        {
            await Task.Delay(80, ct);
            if (!_attempts.TryGetValue(attemptId, out var attempt))
                throw new InvalidOperationException("Attempt not found.");
            if (attempt.Result is not null) return attempt.Result;
            var quiz = GetQuiz(attempt.QuizId);
            var score = (int)Math.Round(attempt.CorrectAnswers * 100m / attempt.Questions.Count);
            var passed = score >= quiz.PassMarkPercent;
            var xp = attempt.CorrectAnswers * 10 + (passed ? 50 : 0);
            attempt.Result = new CompleteAttemptResponse(
                attemptId, score, passed, attempt.CorrectAnswers, attempt.Questions.Count,
                xp, passed ? quiz.CpdPoints : 0m);
            _totalXp += xp;
            _completed++;
            if (passed)
            {
                _passed++;
                _streak++;
                _longestStreak = Math.Max(_longestStreak, _streak);
            }
            else _streak = 0;

            var summary = MockQuizzes.First(x => x.Id == attempt.QuizId);
            _completedQuizzes.RemoveAll(x => x.QuizId == attempt.QuizId);
            _completedQuizzes.Add(new CompletedQuizDto(
                attempt.QuizId,
                summary.Title,
                summary.Category,
                score,
                DateTime.Now,
                passed ? quiz.CpdPoints : 0m));

            return attempt.Result;
        }

        using var response = await _http.PostAsync($"api/quiz-attempts/{attemptId}/complete", null, ct);
        return await ReadAsync<CompleteAttemptResponse>(response, ct);
    }

    public async Task<CompleteAttemptResponse> GetResultAsync(Guid attemptId, CancellationToken ct = default)
    {
        if (_local)
        {
            await Task.Delay(50, ct);
            if (!_attempts.TryGetValue(attemptId, out var attempt) || attempt.Result is null)
                throw new InvalidOperationException("Result not found.");
            return attempt.Result;
        }
        using var response = await _http.GetAsync($"api/quiz-attempts/{attemptId}/result", ct);
        return await ReadAsync<CompleteAttemptResponse>(response, ct);
    }

    public async Task<UserProgressDto> GetMyProgressAsync(CancellationToken ct = default)
    {
        if (_local)
        {
            await Task.Delay(70, ct);
            return new UserProgressDto(_totalXp, Math.Max(1, _totalXp / 100 + 1), _completed, _passed, _streak, _longestStreak);
        }
        using var response = await _http.GetAsync("api/progress/me", ct);
        return await ReadAsync<UserProgressDto>(response, ct);
    }

    public async Task<IReadOnlyList<LeaderboardEntryDto>> GetLeaderboardAsync(int take = 10, CancellationToken ct = default)
    {
        if (_local)
        {
            await Task.Delay(70, ct);
            return new List<LeaderboardEntryDto>
            {
                new(1, "Ayesha Daniels", 860, 9, 96),
                new(2, "Marc Williams", 720, 8, 94),
                new(3, "Local Developer", _totalXp, Math.Max(1, _totalXp / 100 + 1), 90),
                new(4, "Thabo Nkosi", 210, 3, 84),
                new(5, "Lerato Mokoena", 170, 2, 80)
            }.OrderByDescending(x => x.TotalXp).Take(take).Select((x, i) => x with { Rank = i + 1 }).ToList();
        }
        using var response = await _http.GetAsync($"api/leaderboard?take={take}", ct);
        return await ReadAsync<List<LeaderboardEntryDto>>(response, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw await ApiRequestException.FromResponseAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty API response.");
    }

    private static readonly IReadOnlyList<QuizSummaryDto> MockQuizzes =
    [
        new(1, "POPIA & Client Data Handling", "Compliance", 70, 5, 2m)
        {
            AvailableFrom = new DateTime(2026, 8, 10)
        },
        new(2, "Retirement Annuity Product Update", "Product", 70, 5, 3m)
        {
            AvailableFrom = new DateTime(2026, 8, 18)
        },
        new(3, "Ethics in Financial Advice", "Ethics", 80, 5, 2m)
        {
            AvailableFrom = new DateTime(2026, 8, 25)
        }
    ];

    private static QuizDetailsDto GetQuiz(ulong id) => id switch
    {
        1 => new(1, "POPIA & Client Data Handling", "Compliance",
            "Practice the correct handling of client information and privacy obligations.",
            "Choose the best answer for each question.", 70, 5, 2m, 15),
        2 => new(2, "Retirement Annuity Product Update", "Product",
            "Review key retirement annuity product concepts.",
            "Complete all five questions.", 70, 5, 3m, 15),
        3 => new(3, "Ethics in Financial Advice", "Ethics",
            "Test common ethical decision-making principles for advisors.",
            "An 80% score is required to pass.", 80, 5, 2m, 15),
        _ => throw new InvalidOperationException("Quiz not found.")
    };

    private static List<MockQuestion> BuildQuestions(ulong quizId) => quizId switch
    {
        1 =>
        [
            Q(101, "What is the safest way to handle sensitive client data?", 1001,
                (1001, "Access it only when authorised and required"),
                (1002, "Copy it to a personal cloud drive"),
                (1003, "Share it in a public chat"),
                (1004, "Print extra copies for convenience")),
            Q(102, "When should a possible data breach be reported?", 1011,
                (1011, "As soon as it is identified through the correct process"),
                (1012, "Only at month end"),
                (1013, "Only if a client asks"),
                (1014, "Never if no money was lost")),
            Q(103, "Why is accurate record keeping important?", 1021,
                (1021, "It supports traceability and accountability"),
                (1022, "It removes all approval requirements"),
                (1023, "It guarantees every transaction succeeds"),
                (1024, "It makes authentication unnecessary")),
            Q(104, "Which principle best limits unnecessary use of personal information?", 1032,
                (1031, "Store every available data point forever"),
                (1032, "Use information only for a legitimate required purpose"),
                (1033, "Share data by default"),
                (1034, "Use personal email for convenience")),
            Q(105, "What should happen when a privacy policy changes?", 1043,
                (1041, "Keep using the old version"),
                (1042, "Only new employees need to know"),
                (1043, "Affected staff should be informed and use the current version"),
                (1044, "Delete the policy"))
        ],
        2 =>
        [
            Q(201, "What should an advisor do before recommending a retirement product?", 2002,
                (2001, "Choose the product with the highest fee"),
                (2002, "Understand the client's relevant needs and circumstances"),
                (2003, "Use the same recommendation for everyone"),
                (2004, "Skip gathering client information")),
            Q(202, "Why should product changes be communicated clearly?", 2013,
                (2011, "So disclosures can be shorter"),
                (2012, "So clients cannot ask questions"),
                (2013, "So clients can make informed decisions"),
                (2014, "So record keeping is unnecessary")),
            Q(203, "Which source should be used for current product rules?", 2021,
                (2021, "The approved current product documentation"),
                (2022, "An old personal note"),
                (2023, "A social-media post"),
                (2024, "A competitor's brochure")),
            Q(204, "When should suitability be reconsidered?", 2034,
                (2031, "Never after the first sale"),
                (2032, "Only when fees increase"),
                (2033, "Only when a manager asks"),
                (2034, "When relevant client circumstances or product details change")),
            Q(205, "Clear product communication should be:", 2042,
                (2041, "Deliberately technical"),
                (2042, "Accurate, understandable and not misleading"),
                (2043, "Limited to internal abbreviations"),
                (2044, "Provided only after a complaint"))
        ],
        3 =>
        [
            Q(301, "Which outcome best reflects ethical financial advice?", 3003,
                (3001, "Prioritising the highest commission"),
                (3002, "Avoiding all client questions"),
                (3003, "Acting fairly and in the client's interests within applicable duties"),
                (3004, "Hiding relevant limitations")),
            Q(302, "What should you do if you identify a conflict of interest?", 3012,
                (3011, "Ignore it"),
                (3012, "Follow the approved conflict-management process"),
                (3013, "Delete the client record"),
                (3014, "Ask the client not to mention it")),
            Q(303, "Why should advice be documented?", 3024,
                (3021, "To make the file larger"),
                (3022, "To replace client communication"),
                (3023, "To remove accountability"),
                (3024, "To record the basis and process for the advice")),
            Q(304, "What is the best response to an uncertain rule?", 3031,
                (3031, "Check approved guidance or escalate before acting"),
                (3032, "Guess based on memory"),
                (3033, "Ignore the rule"),
                (3034, "Ask an unrelated client")),
            Q(305, "Which statement best supports fair treatment?", 3042,
                (3041, "Important information can be hidden in small print"),
                (3042, "Relevant information should be communicated clearly and fairly"),
                (3043, "Complaints should be discouraged"),
                (3044, "All clients must receive the same product"))
        ],
        _ => throw new InvalidOperationException("Quiz not found.")
    };

    private static MockQuestion Q(ulong id, string text, ulong correct, params (ulong Id, string Text)[] options) =>
        new(new QuestionDto(id, 1, text, options.Select(x => new AnswerOptionDto(x.Id, x.Text)).ToList()), correct);

    private sealed record MockQuestion(QuestionDto Question, ulong CorrectOptionId);
    private sealed class MockAttempt(ulong quizId, List<MockQuestion> questions)
    {
        public ulong QuizId { get; } = quizId;
        public List<MockQuestion> Questions { get; } = questions;
        public int CurrentIndex { get; set; }
        public int CorrectAnswers { get; set; }
        public CompleteAttemptResponse? Result { get; set; }
    }
}
