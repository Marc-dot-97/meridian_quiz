using System.Net.Http.Json;
using Meridian.Shared.DTOs;

namespace Meridian.Client.Services;

public sealed class MeridianApiClient
{
    private readonly HttpClient _httpClient;
    private readonly bool _useLocalMode;
    private readonly Dictionary<Guid, MockAttempt> _mockAttempts = [];
    private int _totalXp = 240;
    private int _quizzesCompleted = 6;
    private int _quizzesPassed = 5;
    private int _currentStreak = 3;
    private int _longestStreak = 4;

    public MeridianApiClient(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _useLocalMode = configuration.GetValue<bool>("Development:UseLocalMode");
    }

    public async Task<IReadOnlyList<QuizSummaryDto>> GetQuizzesAsync(CancellationToken cancellationToken = default)
    {
        if (_useLocalMode)
        {
            await Task.Delay(120, cancellationToken);
            return MockQuizzes;
        }

        using var response = await _httpClient.GetAsync("api/quizzes", cancellationToken);
        return await ReadAsync<List<QuizSummaryDto>>(response, cancellationToken);
    }

    public async Task<QuizDetailsDto> GetQuizAsync(ulong quizId, CancellationToken cancellationToken = default)
    {
        if (_useLocalMode)
        {
            await Task.Delay(120, cancellationToken);
            return GetMockQuizDetails(quizId);
        }

        using var response = await _httpClient.GetAsync($"api/quizzes/{quizId}", cancellationToken);
        return await ReadAsync<QuizDetailsDto>(response, cancellationToken);
    }

    public async Task<StartAttemptResponse> StartAttemptAsync(ulong quizId, CancellationToken cancellationToken = default)
    {
        if (_useLocalMode)
        {
            await Task.Delay(120, cancellationToken);
            _ = GetMockQuizDetails(quizId);
            var questions = BuildMockQuestionSet(quizId);
            var attemptId = Guid.NewGuid();
            _mockAttempts[attemptId] = new MockAttempt(quizId, questions);
            return new StartAttemptResponse(attemptId, questions.Count, questions[0].Question);
        }

        using var response = await _httpClient.PostAsJsonAsync(
            "api/quiz-attempts",
            new StartAttemptRequest(quizId),
            cancellationToken);
        return await ReadAsync<StartAttemptResponse>(response, cancellationToken);
    }

    public async Task<SubmitAnswerResponse> SubmitAnswerAsync(
        Guid attemptId,
        ulong questionId,
        ulong answerOptionId,
        CancellationToken cancellationToken = default)
    {
        if (_useLocalMode)
        {
            await Task.Delay(120, cancellationToken);
            if (!_mockAttempts.TryGetValue(attemptId, out var attempt))
                throw new InvalidOperationException("The local quiz attempt could not be found.");
            if (attempt.Completed)
                throw new InvalidOperationException("This quiz attempt is already complete.");
            if (attempt.CurrentIndex >= attempt.Questions.Count)
                throw new InvalidOperationException("There are no questions remaining in this attempt.");

            var current = attempt.Questions[attempt.CurrentIndex];
            if (current.Question.Id != questionId)
                throw new InvalidOperationException("The submitted question does not match the current question.");

            var isCorrect = current.CorrectOptionId == answerOptionId;
            if (isCorrect) attempt.CorrectAnswers++;
            attempt.CurrentIndex++;

            var answeredCount = attempt.CurrentIndex;
            var isComplete = answeredCount >= attempt.Questions.Count;
            var score = (int)Math.Round(attempt.CorrectAnswers * 100m / answeredCount);
            var xp = attempt.CorrectAnswers * 10;
            var nextQuestion = isComplete ? null : attempt.Questions[attempt.CurrentIndex].Question;

            return new SubmitAnswerResponse(
                isCorrect,
                answeredCount,
                attempt.Questions.Count,
                score,
                xp,
                isComplete,
                nextQuestion);
        }

        using var response = await _httpClient.PostAsJsonAsync(
            $"api/quiz-attempts/{attemptId}/answers",
            new SubmitAnswerRequest(questionId, answerOptionId),
            cancellationToken);
        return await ReadAsync<SubmitAnswerResponse>(response, cancellationToken);
    }

    public async Task<CompleteAttemptResponse> CompleteAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default)
    {
        if (_useLocalMode)
        {
            await Task.Delay(120, cancellationToken);
            if (!_mockAttempts.TryGetValue(attemptId, out var attempt))
                throw new InvalidOperationException("The local quiz attempt could not be found.");
            if (attempt.Result is not null) return attempt.Result;
            if (attempt.CurrentIndex < attempt.Questions.Count)
                throw new InvalidOperationException("Answer all questions before completing the attempt.");

            var quiz = GetMockQuizDetails(attempt.QuizId);
            var score = (int)Math.Round(attempt.CorrectAnswers * 100m / attempt.Questions.Count);
            var passed = score >= quiz.PassMarkPercent;
            var xpEarned = (attempt.CorrectAnswers * 10) + (passed ? 50 : 0);
            var cpdEarned = passed ? quiz.CpdPoints : 0m;

            attempt.Completed = true;
            attempt.Result = new CompleteAttemptResponse(
                attemptId,
                score,
                passed,
                attempt.CorrectAnswers,
                attempt.Questions.Count,
                xpEarned,
                cpdEarned);

            _totalXp += xpEarned;
            _quizzesCompleted++;
            if (passed)
            {
                _quizzesPassed++;
                _currentStreak++;
                _longestStreak = Math.Max(_longestStreak, _currentStreak);
            }
            else
            {
                _currentStreak = 0;
            }

            return attempt.Result;
        }

        using var response = await _httpClient.PostAsync(
            $"api/quiz-attempts/{attemptId}/complete",
            null,
            cancellationToken);
        return await ReadAsync<CompleteAttemptResponse>(response, cancellationToken);
    }

    public async Task<CompleteAttemptResponse> GetResultAsync(Guid attemptId, CancellationToken cancellationToken = default)
    {
        if (_useLocalMode)
        {
            await Task.Delay(80, cancellationToken);
            if (!_mockAttempts.TryGetValue(attemptId, out var attempt) || attempt.Result is null)
                throw new InvalidOperationException("The local quiz result could not be found.");
            return attempt.Result;
        }

        using var response = await _httpClient.GetAsync($"api/quiz-attempts/{attemptId}/result", cancellationToken);
        return await ReadAsync<CompleteAttemptResponse>(response, cancellationToken);
    }

    public async Task<UserProgressDto> GetMyProgressAsync(CancellationToken cancellationToken = default)
    {
        if (_useLocalMode)
        {
            await Task.Delay(100, cancellationToken);
            return new UserProgressDto(
                _totalXp,
                Math.Max(1, (_totalXp / 100) + 1),
                _quizzesCompleted,
                _quizzesPassed,
                _currentStreak,
                _longestStreak);
        }

        using var response = await _httpClient.GetAsync("api/progress/me", cancellationToken);
        return await ReadAsync<UserProgressDto>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<LeaderboardEntryDto>> GetLeaderboardAsync(int take = 10, CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 100);
        if (_useLocalMode)
        {
            await Task.Delay(100, cancellationToken);
            var entries = new List<LeaderboardEntryDto>
            {
                new(1, "Ayesha Daniels", 860, 9, 96),
                new(2, "Marc Williams", 720, 8, 94),
                new(3, "Local Developer", _totalXp, Math.Max(1, (_totalXp / 100) + 1), 90),
                new(4, "Thabo Nkosi", 210, 3, 84),
                new(5, "Lerato Mokoena", 170, 2, 80)
            };

            return entries
                .OrderByDescending(x => x.TotalXp)
                .ThenByDescending(x => x.BestScorePercent)
                .Take(take)
                .Select((entry, index) => entry with { Rank = index + 1 })
                .ToList();
        }

        using var response = await _httpClient.GetAsync($"api/leaderboard?take={take}", cancellationToken);
        return await ReadAsync<List<LeaderboardEntryDto>>(response, cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw await ApiRequestException.FromResponseAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty response when JSON data was expected.");
    }

    private static IReadOnlyList<QuizSummaryDto> MockQuizzes =>
    [
        new(1, "Compliance Essentials", "Compliance", 70, 5, 2.0m),
        new(2, "Treating Customers Fairly", "Conduct", 70, 5, 1.5m),
        new(3, "Cybersecurity Awareness", "Technology", 80, 5, 1.0m)
    ];

    private static QuizDetailsDto GetMockQuizDetails(ulong quizId) => quizId switch
    {
        1 => new(1, "Compliance Essentials", "Compliance",
            "A short local-development quiz covering core compliance concepts.",
            "Choose one answer for each question. You need 70% to pass.",
            70, 5, 2.0m, 15),
        2 => new(2, "Treating Customers Fairly", "Conduct",
            "Practice customer-outcome and fair-treatment principles.",
            "Choose the best answer for each customer scenario.",
            70, 5, 1.5m, 15),
        3 => new(3, "Cybersecurity Awareness", "Technology",
            "Test common workplace security practices.",
            "Complete all five questions. An 80% score is required to pass.",
            80, 5, 1.0m, 10),
        _ => throw new InvalidOperationException("The requested local quiz does not exist.")
    };

    private static List<MockQuestion> BuildMockQuestionSet(ulong quizId)
    {
        var questions = quizId switch
        {
            1 => ComplianceQuestions(),
            2 => CustomerFairnessQuestions(),
            3 => CybersecurityQuestions(),
            _ => throw new InvalidOperationException("The requested local quiz does not exist.")
        };

        return questions
            .OrderBy(_ => Random.Shared.Next())
            .Select((item, index) => new MockQuestion(item.Question with { DisplayOrder = index + 1 }, item.CorrectOptionId))
            .ToList();
    }

    private static List<MockQuestion> ComplianceQuestions() =>
    [
        Q(101, 1, "What is the main purpose of a compliance policy?", 1001,
            (1001, "To guide behaviour according to legal and internal requirements"),
            (1002, "To replace all employee training"),
            (1003, "To remove the need for record keeping"),
            (1004, "To guarantee that no mistakes occur")),
        Q(102, 2, "When should a potential compliance issue be reported?", 1011,
            (1011, "As soon as it is identified through the correct reporting channel"),
            (1012, "Only after a customer complains"),
            (1013, "At the end of the financial year"),
            (1014, "Only if a manager asks")),
        Q(103, 3, "Why is accurate record keeping important?", 1021,
            (1021, "It supports traceability, evidence and accountability"),
            (1022, "It makes authentication unnecessary"),
            (1023, "It removes the need for approvals"),
            (1024, "It guarantees every transaction succeeds")),
        Q(104, 4, "Which action best protects confidential customer information?", 1032,
            (1031, "Share it in a team chat for convenience"),
            (1032, "Access and share it only when authorised and required"),
            (1033, "Store it in a personal account"),
            (1034, "Print an extra copy for every employee")),
        Q(105, 5, "What should happen when a policy changes?", 1043,
            (1041, "Employees should continue using the old version indefinitely"),
            (1042, "Only new employees need to know"),
            (1043, "Affected employees should be informed and work from the current version"),
            (1044, "The policy should be deleted"))
    ];

    private static List<MockQuestion> CustomerFairnessQuestions() =>
    [
        Q(201, 1, "Which outcome best reflects fair treatment of customers?", 2002,
            (2001, "Giving every customer the most expensive product"),
            (2002, "Providing clear information and suitable service"),
            (2003, "Avoiding all customer questions"),
            (2004, "Using technical terms without explanation")),
        Q(202, 2, "A customer does not understand a product term. What should you do?", 2013,
            (2011, "Ask them to sign first"),
            (2012, "Ignore the question"),
            (2013, "Explain the term clearly before they decide"),
            (2014, "Tell them to search online")),
        Q(203, 3, "Why should customer complaints be recorded?", 2021,
            (2021, "To support investigation, resolution and trend monitoring"),
            (2022, "Only to increase paperwork"),
            (2023, "To avoid responding to customers"),
            (2024, "So they can be deleted later")),
        Q(204, 4, "What is the best approach when recommending a product?", 2034,
            (2031, "Recommend whichever product has the highest fee"),
            (2032, "Use the same recommendation for everyone"),
            (2033, "Skip gathering customer information"),
            (2034, "Consider the customer's needs and relevant circumstances")),
        Q(205, 5, "Clear customer communication should generally be:", 2042,
            (2041, "Long and deliberately complex"),
            (2042, "Accurate, understandable and not misleading"),
            (2043, "Limited to internal abbreviations"),
            (2044, "Provided only after a complaint"))
    ];

    private static List<MockQuestion> CybersecurityQuestions() =>
    [
        Q(301, 1, "What should you do with an unexpected link in an email?", 3003,
            (3001, "Open it immediately"),
            (3002, "Forward it to everyone"),
            (3003, "Verify the sender and link before opening it"),
            (3004, "Reply with your password")),
        Q(302, 2, "Which password practice is strongest?", 3012,
            (3011, "Reuse one password everywhere"),
            (3012, "Use a unique strong password and approved MFA"),
            (3013, "Write the password on the monitor"),
            (3014, "Share it with coworkers")),
        Q(303, 3, "Why should a workstation be locked when unattended?", 3024,
            (3021, "To save disk space"),
            (3022, "To improve Wi-Fi speed"),
            (3023, "To install updates"),
            (3024, "To prevent unauthorised access")),
        Q(304, 4, "What is a common sign of a phishing message?", 3031,
            (3031, "Unexpected urgency asking for credentials or payment"),
            (3032, "A normal internal calendar reminder"),
            (3033, "A document you created yourself"),
            (3034, "A scheduled system notification you expected")),
        Q(305, 5, "Where should company-sensitive files normally be stored?", 3042,
            (3041, "Any personal cloud account"),
            (3042, "An approved company storage location"),
            (3043, "A public file-sharing link"),
            (3044, "A personal USB drive without approval"))
    ];

    private static MockQuestion Q(
        ulong questionId,
        int displayOrder,
        string text,
        ulong correctOptionId,
        params (ulong Id, string Text)[] options) =>
        new(
            new QuestionDto(
                questionId,
                displayOrder,
                text,
                options.Select(x => new AnswerOptionDto(x.Id, x.Text)).ToList()),
            correctOptionId);

    private sealed record MockQuestion(QuestionDto Question, ulong CorrectOptionId);

    private sealed class MockAttempt(ulong quizId, List<MockQuestion> questions)
    {
        public ulong QuizId { get; } = quizId;
        public List<MockQuestion> Questions { get; } = questions;
        public int CurrentIndex { get; set; }
        public int CorrectAnswers { get; set; }
        public bool Completed { get; set; }
        public CompleteAttemptResponse? Result { get; set; }
    }
}
