using System.Text.Json;
using Meridian.Api.Data;
using Meridian.Api.Data.Entities;
using Meridian.Api.Features.Quizzes;
using Meridian.Api.Features.Reports;
using Meridian.Shared.DTOs;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Features.DevTools;

public sealed record SeedResult(bool Seeded, string Message, int Users, int Quizzes, int Attempts, int Surveys, int SurveyResponses);

/// <summary>
/// DEV SEED: dummy data for testing. Only reachable through DevBypassState-guarded paths
/// (POST /api/dev/seed, or `dotnet run -- --seed` / `--seed-reset`), so it never runs on production.
/// Everything it creates is tagged: users use the @seed.meridian.local domain, quizzes/questions are authored
/// by those users, categories carry the [seed] description and surveys use fixed IDs. ClearAsync removes only that.
/// </summary>
public static class DevSeeder
{
    public const string EmailDomain = "@seed.meridian.local";
    public const string SeedPassword = "Password123!";
    private const string SeedTag = "[seed]";

    private static readonly Guid[] SurveyIds =
    [
        Guid.Parse("5eed0000-0000-4000-8000-000000000001"),
        Guid.Parse("5eed0000-0000-4000-8000-000000000002"),
        Guid.Parse("5eed0000-0000-4000-8000-000000000003"),
    ];

    private sealed record Person(string First, string Last, string Department, string JobTitle, double Skill, string Role = "QuizAuthor");
    private sealed record Q(string Text, string[] Options, int Correct);
    private sealed record QuizDef(string Category, string Title, string Description, decimal Cpd, byte PassMark,
        ushort? Minutes, Q[] Questions, int? UnlockInDays = null, int? ExpiredDaysAgo = null);

    private const string Ods = "Optimum Direct Solutions (Pty) Ltd";
    private const string Oss = "Optimum Shared Services (Pty) Ltd";
    private const string Oeb = "Optimum Employee Benefits (Pty) Ltd";
    private const string Oic = "Optimum Insure Consulting (Pty) Ltd";
    private const string Opfp = "Optimum Professional Financial Planning (Pty) Ltd";

    private static readonly Person[] People =
    [
        new("Thandi", "Nkosi", Ods, "Financial Advisor", 0.90, "Admin"),
        new("Pieter", "van der Merwe", Ods, "Regional Manager", 0.80),
        new("Aisha", "Patel", Opfp, "Financial Advisor", 0.85),
        new("Sipho", "Dlamini", Opfp, "New Business Assistant", 0.60),
        new("Lerato", "Mokoena", Oeb, "Employee Benefits Consultant", 0.75),
        new("Johan", "Botha", Oeb, "Employee Benefits Manager", 0.70),
        new("Naledi", "Khumalo", Oic, "Account Executive", 0.65),
        new("Ruan", "Steyn", Oic, "Financial Advisor", 0.55),
        new("Zanele", "Mthembu", Oss, "Head Of Human Resource", 0.70),
        new("Kyle", "Naidoo", Oss, "Software Developer", 0.80),
        new("Fatima", "Adams", Oss, "Legal Assistant", 0.85),
        new("Bongani", "Zulu", Oss, "IT Support", 0.50),
        new("Chantel", "Jacobs", Opfp, "Profile Assistant", 0.60),
        new("David", "Smith", Ods, "Administrator", 0.45),
        new("Mpho", "Sithole", Opfp, "Operational Manager", 0.75),
    ];

    private static readonly QuizDef[] QuizDefs =
    [
        new("Regulatory & Compliance", "FAIS Fit and Proper Essentials",
            "Core FAIS requirements for representatives and key individuals.", 2.0m, 70, 15,
        [
            new("Which act regulates financial advisors and intermediaries in South Africa?", ["FAIS Act", "FICA", "POPIA", "National Credit Act"], 0),
            new("Which regulator supervises the market conduct of FSPs?", ["Prudential Authority", "FSCA", "National Credit Regulator", "SARS"], 1),
            new("Which of these is NOT a fit and proper requirement?", ["Honesty and integrity", "Competence", "Financial soundness", "A minimum annual sales target"], 3),
            new("Which regulatory examination applies to representatives?", ["RE1", "RE5", "CFP", "NQF2"], 1),
            new("Under the FAIS General Code, records of advice must be kept for at least:", ["1 year", "3 years", "5 years", "10 years"], 2),
        ]),
        new("Regulatory & Compliance", "FICA and Anti-Money Laundering",
            "Client due diligence, reporting duties and the RMCP.", 1.5m, 70, 15,
        [
            new("What is the main purpose of FICA?", ["Protecting personal information", "Combating money laundering and terrorist financing", "Regulating credit providers", "Setting tax rates"], 1),
            new("Suspicious and unusual transactions are reported to:", ["The FSCA", "SAPS", "The Financial Intelligence Centre", "SARS"], 2),
            new("Cash transactions above which amount must be reported to the FIC?", ["R10 000", "R25 000", "R49 999.99", "R100 000"], 2),
            new("What is 'tipping off'?", ["Telling a client a suspicious transaction report was made", "Rewarding a colleague for a referral", "Pre-approving a client", "Reporting a breach to the regulator"], 0),
            new("An accountable institution's internal AML/CFT rules are documented in its:", ["Business plan", "RMCP", "Annual report", "Service level agreement"], 1),
        ]),
        new("Ethics & Conduct", "Treating Customers Fairly",
            "The six TCF outcomes and how they show up in daily work.", 1.0m, 60, 10,
        [
            new("How many TCF outcomes are there?", ["Four", "Five", "Six", "Eight"], 2),
            new("Which is a TCF outcome?", ["Products are designed to meet the needs of identified customer groups", "Advisors meet monthly targets", "Fees are always the lowest in the market", "Clients sign a waiver"], 0),
            new("A client unhappy with an FSP's final complaint response can escalate to:", ["The FAIS Ombud", "The Information Regulator", "The Competition Commission", "SARS"], 0),
            new("A conflict of interest should be:", ["Ignored if small", "Disclosed to the client and managed", "Disclosed only on request", "Recorded after the sale"], 1),
            new("TCF is best described as:", ["A one-off training event", "Fairness embedded in the firm's culture and processes", "A complaints form", "A product range"], 1),
        ]),
        new("Ethics & Conduct", "POPIA for Financial Services",
            "Handling client personal information lawfully.", 1.0m, 60, 10,
        [
            new("Which body enforces POPIA?", ["The FSCA", "The Information Regulator", "The FIC", "The Ombud for Banking Services"], 1),
            new("How many conditions for lawful processing does POPIA set out?", ["Five", "Six", "Eight", "Ten"], 2),
            new("Which is 'special personal information'?", ["Email address", "Health information", "Postal address", "Job title"], 1),
            new("After a security compromise you must notify:", ["Only your manager", "The Information Regulator and affected data subjects", "No one if the data is recovered", "Only the IT department"], 1),
            new("Unsolicited electronic direct marketing to a non-client requires:", ["Opt-in consent", "An opt-out link only", "A manager's approval", "Nothing, if it is relevant"], 0),
        ]),
        new("Financial Planning", "Retirement Annuities Basics",
            "Contributions, Regulation 28 and the two-pot system.", 2.0m, 70, 20,
        [
            new("What is the earliest age a member can retire from a retirement annuity (outside ill-health)?", ["50", "55", "60", "65"], 1),
            new("Annual tax-deductible retirement contributions are capped at 27.5% of income up to:", ["R150 000", "R250 000", "R350 000", "R500 000"], 2),
            new("Regulation 28 limits offshore exposure to:", ["25%", "30%", "45%", "75%"], 2),
            new("Under the two-pot system, the minimum savings-component withdrawal is:", ["R500", "R1 000", "R2 000", "R5 000"], 2),
            new("At retirement, the retirement component must mainly be used to:", ["Buy an annuity", "Pay off a bond", "Invest in a TFSA", "Pay tax in advance"], 0),
        ]),
        new("Financial Planning", "Tax-Free Savings Accounts",
            "Limits, penalties and suitable use of TFSAs.", 1.0m, 60, 10,
        [
            new("What is the annual TFSA contribution limit?", ["R30 000", "R36 000", "R46 000", "R50 000"], 1),
            new("What is the lifetime TFSA contribution limit?", ["R350 000", "R400 000", "R500 000", "R1 000 000"], 2),
            new("Contributions above the limits are taxed at:", ["18%", "28%", "40%", "45%"], 2),
            new("Which return is taxed inside a TFSA?", ["Interest", "Dividends", "Capital gains", "None of these"], 3),
            new("Does a withdrawal restore your contribution room?", ["Yes, fully", "Yes, after 12 months", "No", "Only for emergencies"], 2),
        ]),
        new("Products & Markets", "Risk Cover Fundamentals",
            "Income protection, dread disease and beneficiaries.", 1.5m, 70, 15,
        [
            new("Income protection mainly replaces:", ["A lump sum on death", "Monthly income during disability or illness", "Medical scheme contributions", "Retirement savings"], 1),
            new("Dread disease cover pays out on:", ["Any hospital stay", "Diagnosis of a listed severe illness", "Retirement", "Retrenchment"], 1),
            new("A waiting period on income protection is:", ["The time before benefits start after a claim event", "The policy term", "The cooling-off period", "The premium holiday"], 0),
            new("A valid beneficiary nomination on a life policy usually means the payout:", ["Goes into the estate", "Is paid directly to the beneficiary outside the estate", "Is taxed at 40%", "Is paid to the FSP"], 1),
            new("Underwriting is the process of:", ["Assessing the risk to decide cover and premium", "Paying claims", "Selling a policy", "Cancelling cover"], 0),
        ], UnlockInDays: 14),
        new("Products & Markets", "Medical Schemes Refresher",
            "PMBs, waiting periods and gap cover.", 1.0m, 60, 10,
        [
            new("Prescribed Minimum Benefits (PMBs) are:", ["Optional add-ons", "Conditions every medical scheme must cover", "Gap cover benefits", "Hospital cash plans"], 1),
            new("Medical schemes are regulated by:", ["The FSCA", "The Council for Medical Schemes", "The Department of Labour", "The FAIS Ombud"], 1),
            new("Late-joiner penalties can apply to people joining from age:", ["21", "30", "35", "50"], 2),
            new("Gap cover is designed to pay:", ["Monthly premiums", "Shortfalls between specialist charges and scheme rates", "Day-to-day GP visits", "Income during illness"], 1),
            new("The general waiting period a scheme may apply is up to:", ["1 month", "3 months", "6 months", "12 months"], 1),
        ], ExpiredDaysAgo: 10),
    ];

    private static readonly string[] Comments =
    [
        "More scenario-based questions please.", "Two-pot system examples would help.", "Great for quick CPD points.",
        "Short videos before each quiz would be useful.", "Please add a FICA refresher for new staff.", "Works well on my phone.",
    ];

    public static async Task<SeedResult> SeedAsync(MeridianDbContext db, IPasswordHasher<User> hasher, ILogger log, CancellationToken ct,
        EmployeeDirectoryStore? directory = null)
    {
        if (await db.Users.AnyAsync(u => u.Email.EndsWith(EmailDomain), ct) || await db.Surveys.AnyAsync(s => SurveyIds.Contains(s.Id), ct))
            return new(false, "Seed data already exists. Use reset to replace it.", 0, 0, 0, 0, 0);

        var rng = new Random(2026);
        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Users: one shared hash (the default hasher ignores the user argument), all with the seed password.
        var users = People.Select(p => new User
        {
            Email = $"{Slug(p.First)}.{Slug(p.Last)}{EmailDomain}", UserName = $"seed-{Slug(p.First)}-{Slug(p.Last)}", Administrators = "",
            FirstName = p.First, LastName = p.Last, DisplayName = $"{p.First} {p.Last}", Department = p.Department,
            UserRole = p.JobTitle, LineManager = ManagerOf(p), AuthRole = p.Role, IsActive = true,
            CreatedAt = now.AddMonths(-13), UpdatedAt = now.AddMonths(-13)
        }).ToList();
        var hash = hasher.HashPassword(users[0], SeedPassword);
        users.ForEach(u => u.PasswordHash = hash);
        db.Users.AddRange(users);
        await db.SaveChangesAsync(ct);
        var author = users[0];

        // Seed employee-list rows so Reports can be tested per role: Zanele = HR, Pieter/Mpho/Johan/Naledi = line managers,
        // Thandi manages the managers. Real imported rows (source "import") are not touched.
        if (directory is not null)
            await directory.ReplaceSourceAsync("seed", People.Select(p => new DirectoryEmployee(
                $"{Slug(p.First)}.{Slug(p.Last)}{EmailDomain}", p.First, null, p.Last, null, p.JobTitle,
                string.IsNullOrEmpty(ManagerOf(p)) ? null : ManagerOf(p), p.Department, "seed")).ToList(), ct);

        // Categories (reuse an existing category of the same name; new ones are tagged for cleanup).
        var categories = new Dictionary<string, QuizCategory>();
        foreach (var name in QuizDefs.Select(d => d.Category).Distinct())
            categories[name] = await db.QuizCategories.SingleOrDefaultAsync(c => c.Name == name, ct)
                ?? new QuizCategory { Name = name, Description = SeedTag, IsActive = true, CreatedAt = now, UpdatedAt = now };

        // Quizzes with questions and answer options, same shape as QuizBuilderController creates.
        var quizzes = new List<(Quiz Quiz, QuizDef Def)>();
        foreach (var d in QuizDefs)
        {
            var created = now.AddMonths(-12);
            var quiz = new Quiz
            {
                Category = categories[d.Category], Title = d.Title, Description = d.Description,
                Instructions = "Choose the best answer for each question.", PassMarkPercent = d.PassMark,
                QuestionsPerAttempt = (ushort)d.Questions.Length, CpdPoints = d.Cpd, TimeLimitMinutes = d.Minutes,
                AvailableFrom = d.UnlockInDays is int u ? now.AddDays(u) : null,
                ExpiresAt = d.ExpiredDaysAgo is int e ? now.AddDays(-e) : null,
                DeleteAfter = null, IsActive = true, CreatedByUserId = author.Id, CreatedAt = created, UpdatedAt = created
            };
            foreach (var q in d.Questions)
            {
                var question = new Question
                {
                    Category = categories[d.Category], QuestionText = q.Text, Difficulty = 1, Status = 1, SourceType = 1,
                    CreatedByUserId = author.Id, CreatedAt = created, UpdatedAt = created
                };
                for (var i = 0; i < q.Options.Length; i++)
                    question.AnswerOptions.Add(new AnswerOption
                    {
                        OptionText = q.Options[i], IsCorrect = i == q.Correct, DisplayOrder = (ushort)(i + 1), CreatedAt = created, UpdatedAt = created
                    });
                quiz.QuizQuestions.Add(new QuizQuestion { Question = question, QuestionWeight = 1m, IsActive = true, CreatedAt = created });
            }
            db.Quizzes.Add(quiz);
            quizzes.Add((quiz, d));
        }
        await db.SaveChangesAsync(ct);

        // Completed attempts spread over the last ~11 months, scored exactly like QuizAttemptsController.Complete.
        var attempts = 0;
        for (var ui = 0; ui < users.Count; ui++)
        {
            var skill = People[ui].Skill;
            foreach (var (quiz, def) in quizzes)
            {
                if (def.UnlockInDays is not null || rng.NextDouble() > 0.5) continue;
                var latest = quiz.ExpiresAt is DateTime exp ? exp.AddDays(-1) : now.AddHours(-1);
                var completed = latest.AddDays(-rng.Next(0, 320)).AddMinutes(-rng.Next(0, 600));
                var passed = AddAttempt(db, rng, users[ui], quiz, skill, completed);
                attempts++;
                if (!passed && rng.NextDouble() < 0.5 && completed.AddDays(3) < latest)
                {
                    AddAttempt(db, rng, users[ui], quiz, Math.Min(0.95, skill + 0.15), completed.AddDays(rng.Next(1, 3)));
                    attempts++;
                }
            }
        }
        await db.SaveChangesAsync(ct);

        // Surveys with fixed IDs, plus responses from about 60% of seed users.
        var surveys = BuildSurveys(now);
        var responses = 0;
        foreach (var s in surveys)
        {
            db.Surveys.Add(new SurveyRecord { Id = s.Id, Title = s.Title, DefinitionJson = JsonSerializer.Serialize(s), CreatedAt = s.CreatedAt, DeleteAfter = null });
            foreach (var u in users.Where(_ => rng.NextDouble() < 0.6))
            {
                var answers = s.Questions.Select(q => q.Type switch
                {
                    SurveyQuestionType.MultipleChoice => new SurveyAnswerDto { QuestionId = q.Id, ChoiceIndex = rng.Next(q.Options.Count) },
                    SurveyQuestionType.Rating => new SurveyAnswerDto { QuestionId = q.Id, Rating = rng.Next(2, 6) },
                    _ => new SurveyAnswerDto { QuestionId = q.Id, Text = rng.NextDouble() < 0.6 ? Comments[rng.Next(Comments.Length)] : null }
                }).Where(a => a.ChoiceIndex is not null || a.Rating is not null || a.Text is not null).ToList();
                var submitted = s.CreatedAt.AddHours(rng.Next(1, Math.Max(2, (int)(now - s.CreatedAt).TotalHours)));
                db.SurveyCompletions.Add(new SurveyCompletionRecord
                {
                    Id = Guid.NewGuid(), SurveyId = s.Id, UserId = u.Id, AnswersJson = JsonSerializer.Serialize(answers), SubmittedAt = submitted
                });
                responses++;
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var msg = $"Seeded {users.Count} users, {quizzes.Count} quizzes, {attempts} attempts, {surveys.Count} surveys, {responses} survey responses. Seed users sign in with password {SeedPassword}.";
        log.LogWarning("DEV SEED: {Message}", msg);
        return new(true, msg, users.Count, quizzes.Count, attempts, surveys.Count, responses);
    }

    private static bool AddAttempt(MeridianDbContext db, Random rng, User user, Quiz quiz, double skill, DateTime completedAt)
    {
        var questions = quiz.QuizQuestions.Select(x => x.Question).OrderBy(_ => rng.Next()).ToList();
        var snapshot = questions.Select((q, i) => new QuizAttemptsController.SnapshotQuestion(
            new QuestionDto(q.Id, i + 1, q.QuestionText,
                q.AnswerOptions.OrderBy(o => o.DisplayOrder).Select(o => new AnswerOptionDto(o.Id, o.OptionText)).ToList()),
            q.AnswerOptions.Single(o => o.IsCorrect).Id)).ToList();
        var answers = questions.Select(q =>
        {
            var correct = q.AnswerOptions.Single(o => o.IsCorrect);
            var wrong = q.AnswerOptions.Where(o => !o.IsCorrect).ToList();
            var pick = rng.NextDouble() < skill ? correct : wrong[rng.Next(wrong.Count)];
            return new QuizAttemptsController.SavedAnswer(q.Id, pick.Id);
        }).ToList();
        var correctCount = (ushort)answers.Count(a => snapshot.Single(s => s.Question.Id == a.QuestionId).CorrectOptionId == a.AnswerOptionId);
        var score = Math.Round(correctCount * 100m / snapshot.Count);
        var passed = score >= quiz.PassMarkPercent;
        var started = completedAt.AddMinutes(-rng.Next(4, 20));
        db.QuizAttempts.Add(new QuizAttempt
        {
            Id = Guid.NewGuid(), UserId = user.Id, QuizId = quiz.Id, Status = 2, StartedAt = started, CompletedAt = completedAt,
            TotalQuestions = (ushort)snapshot.Count, CorrectAnswers = correctCount, ScorePercent = score, Passed = passed,
            PointsEarned = (uint)(correctCount * 10 + (passed ? 50 : 0)), CpdPointsEarned = passed ? quiz.CpdPoints : 0,
            QuestionsJson = JsonSerializer.Serialize(snapshot), AnswersJson = JsonSerializer.Serialize(answers),
            SnapshotPassMark = quiz.PassMarkPercent, SnapshotCpdPoints = quiz.CpdPoints, SnapshotTimeLimitMinutes = quiz.TimeLimitMinutes,
            CreatedAt = started, UpdatedAt = completedAt
        });
        return passed;
    }

    private static List<SurveyDto> BuildSurveys(DateTime now)
    {
        static SurveyQuestionDto Mc(string text, params string[] options) => new() { Text = text, Type = SurveyQuestionType.MultipleChoice, Required = true, Options = [.. options] };
        static SurveyQuestionDto Rate(string text) => new() { Text = text, Type = SurveyQuestionType.Rating, Required = true, Options = [] };
        static SurveyQuestionDto Text(string text) => new() { Text = text, Type = SurveyQuestionType.ShortText, Required = false, Options = [] };
        return
        [
            new(SurveyIds[0], "Q3 Learning Experience", "Tell us how CPD learning worked for you this quarter.",
                [Rate("How would you rate this quarter's CPD content overall?"),
                 Mc("Which learning format do you prefer?", "Short quizzes", "Longer modules", "Live sessions", "Videos"),
                 Text("Which topic should we cover next?")], now.AddDays(-60)),
            new(SurveyIds[1], "Wellness Check-in", "A quick, anonymous-style check on workload and wellbeing.",
                [Rate("How manageable is your workload right now?"),
                 Mc("How often do you take short breaks during the day?", "Rarely", "Sometimes", "Often"),
                 Text("Anything that would help you right now?")], now.AddDays(-30)),
            new(SurveyIds[2], "Meridian App Feedback", "Help us improve Meridian.",
                [Rate("How easy is Meridian to use?"),
                 Mc("Which device do you mostly use for Meridian?", "Laptop or desktop", "Phone", "Tablet"),
                 Rate("How likely are you to recommend Meridian to a colleague?"),
                 Text("What should we improve?")], now.AddDays(-7)),
        ];
    }

    /// <summary>Removes only seed-tagged data (and anything that references it). Real accounts and quizzes are untouched.</summary>
    public static async Task<SeedResult> ClearAsync(MeridianDbContext db, ILogger log, CancellationToken ct,
        EmployeeDirectoryStore? directory = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var userIds = await db.Users.Where(u => u.Email.EndsWith(EmailDomain)).Select(u => u.Id).ToListAsync(ct);
        var quizIds = await db.Quizzes.Where(q => q.CreatedByUserId != null && userIds.Contains(q.CreatedByUserId.Value)).Select(q => q.Id).ToListAsync(ct);
        var surveyIds = SurveyIds.ToList();

        var settings = await db.SurveySettings.Where(s => s.ActiveSurveyId != null && surveyIds.Contains(s.ActiveSurveyId.Value)).ToListAsync(ct);
        settings.ForEach(s => s.ActiveSurveyId = null);
        db.SurveyCompletions.RemoveRange(await db.SurveyCompletions.Where(x => surveyIds.Contains(x.SurveyId) || userIds.Contains(x.UserId)).ToListAsync(ct));
        db.SurveyResponses.RemoveRange(await db.SurveyResponses.Where(x => surveyIds.Contains(x.SurveyId) || userIds.Contains(x.UserId)).ToListAsync(ct));
        var surveys = await db.Surveys.Where(s => surveyIds.Contains(s.Id)).ToListAsync(ct);

        db.CpdLedgerEntries.RemoveRange(await db.CpdLedgerEntries.Where(x => quizIds.Contains(x.QuizId) || userIds.Contains(x.UserId)).ToListAsync(ct));
        var attempts = await db.QuizAttempts.Where(a => quizIds.Contains(a.QuizId) || userIds.Contains(a.UserId)).ToListAsync(ct);
        db.QuizAttempts.RemoveRange(attempts);
        db.QuizQuestions.RemoveRange(await db.QuizQuestions.Where(x => quizIds.Contains(x.QuizId)).ToListAsync(ct));
        var questionIds = await db.Questions.Where(q => q.CreatedByUserId != null && userIds.Contains(q.CreatedByUserId.Value)).Select(q => q.Id).ToListAsync(ct);
        db.AnswerOptions.RemoveRange(await db.AnswerOptions.Where(o => questionIds.Contains(o.QuestionId)).ToListAsync(ct));
        db.Questions.RemoveRange(await db.Questions.Where(q => questionIds.Contains(q.Id)).ToListAsync(ct));
        db.Quizzes.RemoveRange(await db.Quizzes.Where(q => quizIds.Contains(q.Id)).ToListAsync(ct));
        await db.SaveChangesAsync(ct);

        db.Surveys.RemoveRange(surveys);
        foreach (var q in await db.Questions.Where(q => q.ApprovedByUserId != null && userIds.Contains(q.ApprovedByUserId.Value)).ToListAsync(ct)) q.ApprovedByUserId = null;
        foreach (var q in await db.Quizzes.Where(q => q.CreatedByUserId != null && userIds.Contains(q.CreatedByUserId.Value)).ToListAsync(ct)) q.CreatedByUserId = null;
        db.UserProgresses.RemoveRange(await db.UserProgresses.Where(p => userIds.Contains(p.UserId)).ToListAsync(ct));
        db.Users.RemoveRange(await db.Users.Where(u => userIds.Contains(u.Id)).ToListAsync(ct));
        await db.SaveChangesAsync(ct);

        // Seed-created categories that are now empty.
        var emptyCategories = await db.QuizCategories
            .Where(c => c.Description == SeedTag && !db.Quizzes.Any(q => q.CategoryId == c.Id) && !db.Questions.Any(q => q.CategoryId == c.Id))
            .ToListAsync(ct);
        db.QuizCategories.RemoveRange(emptyCategories);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (directory is not null) await directory.ReplaceSourceAsync("seed", Array.Empty<DirectoryEmployee>(), ct);

        var msg = $"Removed seed data: {userIds.Count} users, {quizIds.Count} quizzes, {attempts.Count} attempts, {surveys.Count} surveys.";
        log.LogWarning("DEV SEED: {Message}", msg);
        return new(true, msg, userIds.Count, quizIds.Count, attempts.Count, surveys.Count, 0);
    }

    private static readonly Dictionary<string, string> DepartmentManagers = new()
    {
        [Ods] = "Pieter van der Merwe", [Opfp] = "Mpho Sithole", [Oeb] = "Johan Botha", [Oic] = "Naledi Khumalo", [Oss] = "Zanele Mthembu",
    };

    private static string ManagerOf(Person p)
    {
        var name = $"{p.First} {p.Last}";
        if (name == "Thandi Nkosi") return "";
        return DepartmentManagers.TryGetValue(p.Department, out var manager) && manager != name ? manager : "Thandi Nkosi";
    }

    private static string Slug(string s) => new string(s.ToLowerInvariant().Where(char.IsLetter).ToArray());
}
