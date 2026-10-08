using System.Security.Claims;
using System.Text.Json;
using Meridian.Api.Data;
using Meridian.Api.Data.Entities;
using Meridian.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Features.Reports;

[ApiController, Route("api/reports"), Authorize]
public sealed class ReportController(MeridianDbContext db, EmployeeDirectoryStore directory, ReportAccessService access,
    ILogger<ReportController> log) : ControllerBase
{
    private static readonly TimeSpan Sast = TimeSpan.FromHours(2);
    private ulong UserId => ulong.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<(User Me, ReportScope Scope, List<string> AllDepartments)> ContextAsync(CancellationToken ct)
    {
        var me = await db.Users.AsNoTracking().SingleAsync(u => u.Id == UserId, ct);
        var entries = await directory.GetAllAsync(ct);
        var registered = await db.Users.AsNoTracking().Where(u => u.IsActive == true).Select(u => u.Department).Distinct().ToListAsync(ct);
        var all = entries.Select(e => e.Department)
            .Concat(registered.Select(d => DirectoryNames.CleanDepartment(d)))
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d).ToList();
        return (me, await access.ResolveAsync(me, all, ct), all);
    }

    [HttpGet("access")]
    public async Task<ActionResult<ReportAccessDto>> Access(CancellationToken ct)
    {
        var (_, scope, _) = await ContextAsync(ct);
        return new ReportAccessDto(scope.Role.ToString(), scope.Departments.ToList(),
            scope.Role != ReportRole.Staff, scope.Role == ReportRole.SuperAdmin, scope.Role != ReportRole.Staff);
    }

    /// <summary>
    /// PDF report. from/to are inclusive South African dates (yyyy-MM-dd).
    /// department: "all" (default) = everything you may see, "me" = personal, or one department name.
    /// include: both | quizzes | surveys.
    /// </summary>
    [HttpGet("pdf")]
    public async Task<IActionResult> Pdf([FromQuery] DateOnly from, [FromQuery] DateOnly to, [FromQuery] string? department,
        [FromQuery] string include = "both", CancellationToken ct = default)
    {
        if (from == default || to == default) return BadRequest(new { message = "Choose a start and end date." });
        if (to < from) return BadRequest(new { message = "The end date must be on or after the start date." });
        if (to.DayNumber - from.DayNumber > 3 * 366) return BadRequest(new { message = "Choose a date range of three years or less." });
        include = (include ?? "both").Trim().ToLowerInvariant();
        if (include is not ("both" or "quizzes" or "surveys")) return BadRequest(new { message = "Include must be both, quizzes or surveys." });
        var withQuizzes = include is "both" or "quizzes";
        var withSurveys = include is "both" or "surveys";

        var (me, scope, _) = await ContextAsync(ct);
        var startUtc = from.ToDateTime(TimeOnly.MinValue) - Sast;
        var endUtc = to.AddDays(1).ToDateTime(TimeOnly.MinValue) - Sast;
        var requested = department?.Trim() ?? "";

        bool personal;
        var wholeCompany = false;   // HR/SuperAdmin "All departments": anonymous results are not filtered by department
        List<string> departments;
        string scopeLabel;
        if (scope.Role == ReportRole.Staff || requested.Equals("me", StringComparison.OrdinalIgnoreCase))
        {
            personal = true; departments = new List<string>(); scopeLabel = me.DisplayName;
        }
        else if (requested.Length == 0 || requested.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            personal = false; departments = scope.Departments.ToList();
            wholeCompany = scope.Role != ReportRole.LineManager;
            scopeLabel = scope.Role == ReportRole.LineManager
                ? (departments.Count == 1 ? departments[0] : $"My departments ({departments.Count})")
                : "All departments";
        }
        else
        {
            var match = scope.Departments.FirstOrDefault(d => d.Equals(requested, StringComparison.OrdinalIgnoreCase));
            if (match is null) return StatusCode(403, new { message = "You can only export reports for the departments you manage." });
            personal = false; departments = [match]; scopeLabel = match;
        }

        // People in scope
        var entries = await directory.GetAllAsync(ct);
        var deptSet = departments.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<User> users;
        if (personal) users = [me];
        else
        {
            var active = await db.Users.AsNoTracking().Where(u => u.IsActive == true).ToListAsync(ct);
            users = active.Where(u => ReportAccessService.DepartmentsOf(u, entries).Overlaps(deptSet)).OrderBy(u => u.DisplayName).ToList();
        }
        var ids = users.Select(u => u.Id).ToList();

        var attempts = withQuizzes
            ? await db.QuizAttempts.AsNoTracking().Include(a => a.Quiz).ThenInclude(q => q.Category)
                .Where(a => ids.Contains(a.UserId) && a.CompletedAt != null && a.CompletedAt >= startUtc && a.CompletedAt < endUtc)
                .OrderBy(a => a.CompletedAt).ToListAsync(ct)
            : new List<QuizAttempt>();
        var completions = withSurveys
            ? await db.SurveyCompletions.AsNoTracking()
                .Where(c => ids.Contains(c.UserId) && c.SubmittedAt >= startUtc && c.SubmittedAt < endUtc).ToListAsync(ct)
            : new List<SurveyCompletionRecord>();
        var answeredSurveyIds = completions.Select(c => c.SurveyId).Distinct().ToList();
        var surveyRecords = withSurveys
            ? await db.Surveys.AsNoTracking()
                .Where(s => answeredSurveyIds.Contains(s.Id) || (!personal && s.CreatedAt < endUtc))
                .OrderByDescending(s => s.CreatedAt).ToListAsync(ct)
            : new List<SurveyRecord>();

        // ---- Survey split for team reports ----
        // Split 1 (who took part): survey_completions, user IDs only, never its answers.
        // Split 2 (what was answered): survey_anonymous_answers, which has no user link at all.
        // Both cover everything up to the end of the chosen period.
        var reportSurveyIds = surveyRecords.Select(r => r.Id).ToList();
        var participation = new List<(Guid SurveyId, ulong UserId)>();
        var anonymous = new List<SurveyAnonymousAnswer>();
        if (withSurveys && !personal && reportSurveyIds.Count > 0)
        {
            participation = (await db.SurveyCompletions.AsNoTracking()
                    .Where(c => reportSurveyIds.Contains(c.SurveyId) && ids.Contains(c.UserId) && c.SubmittedAt < endUtc)
                    .Select(c => new { c.SurveyId, c.UserId }).ToListAsync(ct))
                .Select(c => (c.SurveyId, c.UserId)).ToList();
            var lastDay = to.ToDateTime(TimeOnly.MinValue);
            anonymous = await db.SurveyAnonymousAnswers.AsNoTracking()
                .Where(a => reportSurveyIds.Contains(a.SurveyId) && a.SubmittedOn <= lastDay)
                .ToListAsync(ct);
            if (!wholeCompany)
                anonymous = anonymous.Where(a => Meridian.Api.Features.Surveys.SurveyAnonymity.SplitDepartments(a.Departments).Any(deptSet.Contains)).ToList();
        }
        var notRegistered = personal ? new List<string>()
            : NotRegistered(entries, deptSet, await db.Users.AsNoTracking().Select(u => u.Email).ToListAsync(ct));

        var data = new ReportData
        {
            ScopeLabel = scopeLabel,
            PeriodLabel = $"{from:dd MMM yyyy} – {to:dd MMM yyyy}",
            GeneratedBy = me.DisplayName,
            GeneratedAtSa = DateTime.UtcNow + Sast,
            Personal = personal,
            IncludeQuizzes = withQuizzes,
            IncludeSurveys = withSurveys,
            Attempts = personal ? attempts.Select(a => new AttemptRow(a.Quiz.Title, a.Quiz.Category.Name, a.CompletedAt!.Value + Sast,
                a.ScorePercent, a.Passed, a.CpdPointsEarned, a.PointsEarned)).ToList() : [],
            Staff = personal ? [] : users.Select(u =>
            {
                var mine = attempts.Where(a => a.UserId == u.Id).ToList();
                var surveysDone = completions.Count(c => c.UserId == u.Id);
                var last = mine.Select(a => a.CompletedAt).Concat(completions.Where(c => c.UserId == u.Id).Select(c => (DateTime?)c.SubmittedAt)).Max();
                var dept = ReportAccessService.DepartmentsOf(u, entries).Where(deptSet.Contains).DefaultIfEmpty(DirectoryNames.CleanDepartment(u.Department)).First();
                return new StaffRow(u.DisplayName, dept, u.UserRole, mine.Count, mine.Count(a => a.Passed),
                    mine.Count == 0 ? 0 : mine.Average(a => a.ScorePercent), mine.Sum(a => a.CpdPointsEarned),
                    mine.Sum(a => (long)a.PointsEarned), surveysDone, last is null ? null : last.Value + Sast);
            }).ToList(),
            Quizzes = personal ? [] : attempts.GroupBy(a => a.QuizId).Select(g => new QuizRow(g.First().Quiz.Title, g.First().Quiz.Category.Name,
                g.Count(), g.Select(a => a.UserId).Distinct().Count(), g.Count(a => a.Passed) * 100m / g.Count(),
                g.Average(a => a.ScorePercent), g.Sum(a => a.CpdPointsEarned))).OrderByDescending(q => q.Attempts).ToList(),
            Surveys = personal
                ? surveyRecords.Select(r => PersonalSurvey(r, completions.Where(c => c.SurveyId == r.Id).ToList())).ToList()
                : surveyRecords.Select(r => TeamSurvey(r, users,
                    participation.Where(p => p.SurveyId == r.Id).Select(p => p.UserId).ToHashSet(),
                    anonymous.Where(a => a.SurveyId == r.Id).ToList(), notRegistered)).ToList(),
            NotRegistered = notRegistered,
        };

        if (personal)
        {
            if (withQuizzes)
            {
                data.Totals.Add(("Quizzes completed", $"{attempts.Count}"));
                data.Totals.Add(("Passed", $"{attempts.Count(a => a.Passed)}"));
                data.Totals.Add(("CPD points", $"{attempts.Sum(a => a.CpdPointsEarned):0.##}"));
                data.Totals.Add(("XP", $"{attempts.Sum(a => (long)a.PointsEarned)}"));
            }
            if (withSurveys) data.Totals.Add(("Surveys", $"{completions.Count}"));
        }
        else
        {
            data.Totals.Add(("Registered staff", $"{users.Count}"));
            if (withQuizzes)
            {
                data.Totals.Add(("Active learners", $"{attempts.Select(a => a.UserId).Distinct().Count()}"));
                data.Totals.Add(("Quiz attempts", $"{attempts.Count}"));
                data.Totals.Add(("Pass rate", attempts.Count == 0 ? "–" : $"{attempts.Count(a => a.Passed) * 100m / attempts.Count:0}%"));
                data.Totals.Add(("CPD points", $"{attempts.Sum(a => a.CpdPointsEarned):0.##}"));
            }
            if (withSurveys) data.Totals.Add(("Survey responses", $"{completions.Count}"));
        }

        var pdf = PdfReportBuilder.Build(data);
        log.LogInformation("Report PDF: {User} ({Role}) exported {Scope} {From}–{To} include={Include}", me.Email, scope.Role, scopeLabel, from, to, include);
        var slug = new string((personal ? me.DisplayName : scopeLabel).Where(char.IsLetterOrDigit).ToArray());
        return File(pdf, "application/pdf", $"Meridian-Report-{slug}-{from:yyyyMMdd}-{to:yyyyMMdd}.pdf");
    }

    /// <summary>Personal report: the person's own survey with each question and the answer they picked.</summary>
    private static SurveySummary PersonalSurvey(SurveyRecord record, List<SurveyCompletionRecord> mine)
    {
        var survey = JsonSerializer.Deserialize<SurveyDto>(record.DefinitionJson)!;
        var answers = mine.Count == 0 ? new List<SurveyAnswerDto>()
            : JsonSerializer.Deserialize<List<SurveyAnswerDto>>(mine[0].AnswersJson) ?? [];
        var questions = survey.Questions.Select(q =>
        {
            var a = answers.FirstOrDefault(x => x.QuestionId == q.Id);
            var text = a is null ? "No answer" : q.Type switch
            {
                SurveyQuestionType.MultipleChoice when a.ChoiceIndex is int i && i >= 0 && i < q.Options.Count => q.Options[i],
                SurveyQuestionType.Rating when a.Rating is int r => $"{r} / 5",
                _ => string.IsNullOrWhiteSpace(a.Text) ? "No answer" : a.Text!
            };
            return new SurveyQuestionSummary(q.Text, [$"Your answer: {text}"], []);
        }).ToList();
        var note = mine.Count > 0 ? $"Submitted {mine[0].SubmittedAt + Sast:dd MMM yyyy}" : "";
        return new SurveySummary(record.Title, note, questions, [], [], null);
    }

    /// <summary>
    /// Team report (line manager / HR / SuperAdmin):
    /// split 1 = named lists of who took part and who did not; split 2 = anonymous % per answer,
    /// withheld below SurveyAnonymity.MinimumResponses so small groups cannot be identified.
    /// </summary>
    private static SurveySummary TeamSurvey(SurveyRecord record, List<User> users, HashSet<ulong> tookPart,
        List<SurveyAnonymousAnswer> rows, List<string> notRegistered)
    {
        var survey = JsonSerializer.Deserialize<SurveyDto>(record.DefinitionJson)!;
        var participated = users.Where(u => tookPart.Contains(u.Id)).Select(u => u.DisplayName)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var notParticipated = users.Where(u => !tookPart.Contains(u.Id)).Select(u => u.DisplayName)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Concat(notRegistered.Select(n => $"{n} (not registered)")).ToList();
        var invited = participated.Count + notParticipated.Count;
        var note = $"{participated.Count} of {invited} took part" +
                   (invited == 0 ? "" : $" ({participated.Count * 100m / invited:0}%)") +
                   $"  ·  {rows.Count} anonymous response(s)";

        if (rows.Count < Meridian.Api.Features.Surveys.SurveyAnonymity.MinimumResponses)
            return new SurveySummary(record.Title, note, [], participated, notParticipated,
                $"Results hidden: fewer than {Meridian.Api.Features.Surveys.SurveyAnonymity.MinimumResponses} responses, so answers could identify individuals.");

        // Rows are ordered by their random id, so the order says nothing about who answered when.
        var answerSets = rows.OrderBy(r => r.Id).Select(r => Meridian.Api.Features.Surveys.SurveyAnonymity.Answers(r.AnswersJson)).ToList();
        var questions = new List<SurveyQuestionSummary>();
        foreach (var q in survey.Questions)
        {
            var given = answerSets.Select(set => set.FirstOrDefault(a => a.QuestionId == q.Id)).Where(a => a is not null).Select(a => a!).ToList();
            var bars = new List<SurveyBar>();
            var lines = new List<string>();
            static decimal Pct(int n, int total) => total == 0 ? 0 : Math.Round(n * 100m / total, 1);
            switch (q.Type)
            {
                case SurveyQuestionType.MultipleChoice:
                {
                    var answered = given.Count(a => a.ChoiceIndex is int i && i >= 0 && i < q.Options.Count);
                    for (var i = 0; i < q.Options.Count; i++)
                    {
                        var n = given.Count(a => a.ChoiceIndex == i);
                        bars.Add(new SurveyBar(q.Options[i], n, Pct(n, answered)));
                    }
                    lines.Add($"{answered} answered");
                    break;
                }
                case SurveyQuestionType.Rating:
                {
                    var ratings = given.Where(a => a.Rating is >= 1 and <= 5).Select(a => a.Rating!.Value).ToList();
                    for (var star = 5; star >= 1; star--)
                    {
                        var n = ratings.Count(r => r == star);
                        bars.Add(new SurveyBar($"{star} / 5", n, Pct(n, ratings.Count)));
                    }
                    lines.Add(ratings.Count == 0 ? "No ratings" : $"Average {ratings.Average():0.0} / 5 from {ratings.Count} rating(s)");
                    break;
                }
                default:
                {
                    var texts = given.Where(a => !string.IsNullOrWhiteSpace(a.Text)).Select(a => a.Text!.Trim()).ToList();
                    lines.Add(texts.Count == 0 ? "No comments" : $"{texts.Count} anonymous comment(s):");
                    lines.AddRange(texts.Take(20).Select(t => $"“{t}”"));
                    if (texts.Count > 20) lines.Add($"…and {texts.Count - 20} more.");
                    break;
                }
            }
            questions.Add(new SurveyQuestionSummary(q.Text, lines, bars));
        }
        return new SurveySummary(record.Title, note, questions, participated, notParticipated, null);
    }

    private static List<string> NotRegistered(IReadOnlyList<DirectoryEmployee> entries, HashSet<string> departments, List<string> registeredEmails)
    {
        var registered = registeredEmails.Select(e => e.ToLowerInvariant()).ToHashSet();
        return entries
            .Where(e => e.Source == "import" && departments.Contains(e.Department) && (e.Email is null || !registered.Contains(e.Email)))
            .Select(e => $"{e.FullName} {e.Surname}".Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
    }

    // ---------------- Employee list (SuperAdmin) ----------------

    [HttpGet("directory")]
    public async Task<ActionResult<DirectorySummaryDto>> DirectorySummary(CancellationToken ct)
    {
        var (_, scope, _) = await ContextAsync(ct);
        if (scope.Role != ReportRole.SuperAdmin) return StatusCode(403, new { message = "Only a SuperAdmin can manage the employee list." });
        var rows = (await directory.GetAllAsync(ct)).Where(e => e.Source is "import" or "crm").ToList();
        var managers = rows.SelectMany(e => DirectoryNames.SplitManagers(e.LineManager)).Distinct().Count();
        return new DirectorySummaryDto(rows.Count, rows.Select(e => e.Department).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            managers, rows.Count(e => e.Email is null), directory.ImportedAt);
    }

    /// <summary>Upload "Full Company and employees.xlsx" (Employees sheet). Replaces the previous import.</summary>
    [HttpPost("directory"), RequestSizeLimit(10_000_000)]
    public async Task<ActionResult<DirectoryImportResultDto>> ImportDirectory(IFormFile file, CancellationToken ct)
    {
        var (me, scope, _) = await ContextAsync(ct);
        if (scope.Role != ReportRole.SuperAdmin) return StatusCode(403, new { message = "Only a SuperAdmin can manage the employee list." });
        // In "crm" mode the upload is a supplement: it fills missing emails and adds people not on the CRM (never leavers).
        if (file is null || file.Length == 0) return BadRequest(new { message = "Choose an .xlsx file." });
        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) return BadRequest(new { message = "Upload the employee list as an .xlsx file." });

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        List<DirectoryEmployee> rows; List<string> warnings;
        try { (rows, warnings) = EmployeeDirectoryStore.ParseWorkbook(buffer); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Employee list could not be read");
            return BadRequest(new { message = "The file could not be read as an Excel workbook." });
        }
        if (rows.Count == 0) return BadRequest(new { message = "No employees found. " + string.Join(" ", warnings) });
        await directory.ReplaceSourceAsync("import", rows, ct);
        log.LogWarning("Employee list imported by {User}: {Count} rows", me.Email, rows.Count);
        if (directory.UsesCrm)
        {
            await directory.GetAllAsync(ct);   // re-merge now so the numbers below are current
            if (directory.SupplementStats is { } st)
                warnings.Insert(0, $"Combined with the CRM: {st.EmailsFilled} missing CRM email(s) filled in, {st.Added} employee(s) not on the CRM added"
                    + (st.LeaversSkipped > 0 ? $", {st.LeaversSkipped} leaver(s) on the list skipped." : "."));
        }
        return new DirectoryImportResultDto(rows.Count, warnings.Take(30).ToList());
    }
}
