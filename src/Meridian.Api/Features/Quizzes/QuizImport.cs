using System.Globalization;
using System.Security.Claims;
using ClosedXML.Excel;
using Meridian.Api.Data;
using Meridian.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Features.Quizzes;

/// <summary>
/// Main Quest 2: reads a filled-in "Meridian-Quiz-Import-Template.xlsx" into a quiz.
///
/// Layout (template v2, see docs/templates and wwwroot/templates):
///   Sheet "Quiz details": C6 title, C7 category, C8 description, C9 instructions, C10 pass mark %, C11 CPD points,
///                         C12 time limit (minutes), C13 unlock date (SAST), C14 expiry date (SAST), C15 auto-delete Yes/No.
///   Sheet "Questions":    one question per row from row 2: B question, C correct answer, D–F wrong answers
///                         (at least one wrong answer per question). Column G (Status) is only a helper for the author.
///
/// Nothing is saved here: the result goes back to the Quiz studio, where the author checks it, picks the departments
/// and saves through the normal POST api/quizzes (same validation and permissions as a hand-built quiz).
/// </summary>
public static class QuizImportParser
{
    public const int MaxQuestions = 100;
    private const int LastRowScanned = 1000;
    private static readonly TimeSpan Sast = TimeSpan.FromHours(2);
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-dd", "yyyy/MM/dd HH:mm", "yyyy/MM/dd",
        "dd/MM/yyyy HH:mm", "dd/MM/yyyy H:mm", "dd/MM/yyyy", "d/M/yyyy HH:mm", "d/M/yyyy",
        "dd MMM yyyy HH:mm", "dd MMM yyyy", "d MMM yyyy"
    ];

    public sealed record Result(CreateQuizRequest? Quiz, List<string> Problems, List<string> Warnings);

    public static Result Parse(Stream stream, DateTime utcNow, Random? random = null)
    {
        random ??= Random.Shared;
        var problems = new List<string>();
        var warnings = new List<string>();
        XLWorkbook workbook;
        try { workbook = new XLWorkbook(stream); }
        catch (Exception)
        {
            return new(null, ["The file could not be opened as an Excel workbook (.xlsx). Save it as an Excel Workbook and try again."], warnings);
        }
        using (workbook)
        {
            if (!workbook.TryGetWorksheet("Quiz details", out var details) || !workbook.TryGetWorksheet("Questions", out var sheet))
                return new(null, ["This is not the Meridian quiz template: the sheets 'Quiz details' and 'Questions' are missing. Download the template from the Quiz studio and copy your questions into it."], warnings);
            if (!Text(details.Cell("B6")).StartsWith("Quiz title", StringComparison.OrdinalIgnoreCase)
                || !Text(sheet.Cell("B1")).StartsWith("Question", StringComparison.OrdinalIgnoreCase)
                || !Text(sheet.Cell("C1")).StartsWith("Correct", StringComparison.OrdinalIgnoreCase))
                return new(null, ["The template layout was changed (rows or columns moved). Use a fresh copy of the template and keep the headings where they are."], warnings);

            var quiz = new CreateQuizRequest
            {
                Title = Text(details.Cell("C6")),
                Category = Text(details.Cell("C7")),
                Description = Text(details.Cell("C8")),
                Instructions = Text(details.Cell("C9")) is { Length: > 0 } instructions ? instructions : "Choose one answer for each question.",
                Questions = []
            };
            if (quiz.Title.Length == 0) problems.Add("Quiz details!C6: enter the quiz title.");
            if (quiz.Category.Length == 0) problems.Add("Quiz details!C7: enter a category.");

            // Pass mark: 70, "70", "70%" or a cell formatted as a percentage (0.7).
            if (Number(details.Cell("C10")) is decimal pass)
            {
                if (pass > 0 && pass < 1) pass *= 100;
                if (pass != decimal.Truncate(pass) || pass is < 1 or > 100) problems.Add("Quiz details!C10: pass mark must be a whole number from 1 to 100.");
                else quiz.PassMarkPercent = (int)pass;
            }
            else problems.Add("Quiz details!C10: enter the pass mark as a whole number from 1 to 100.");

            if (Number(details.Cell("C11")) is decimal cpd)
            {
                if (cpd is < 0 or > 1000 || decimal.Round(cpd, 2) != cpd) problems.Add("Quiz details!C11: CPD points must be 0–1,000 with at most two decimals.");
                else quiz.CpdPoints = cpd;
            }
            else if (IsBlank(details.Cell("C11"))) quiz.CpdPoints = 0;
            else problems.Add("Quiz details!C11: CPD points must be a number, e.g. 1.5.");

            if (!IsBlank(details.Cell("C12")))
            {
                if (Number(details.Cell("C12")) is decimal minutes && minutes == decimal.Truncate(minutes) && minutes is >= 1 and <= 1440)
                    quiz.TimeLimitMinutes = (int)minutes;
                else problems.Add("Quiz details!C12: time limit must be whole minutes from 1 to 1,440, or blank.");
            }

            var unlock = Date(details.Cell("C13"), endOfDay: false, "Quiz details!C13", "unlock date", problems);
            var expiry = Date(details.Cell("C14"), endOfDay: true, "Quiz details!C14", "expiry date", problems);
            if (unlock is DateTime u && u <= utcNow)
            {
                warnings.Add("The unlock date is in the past, so the quiz will be available immediately. Change it in the Availability section if needed.");
                unlock = null;
            }
            if (expiry is DateTime e && e <= utcNow) problems.Add("Quiz details!C14: the expiry date is in the past.");
            else if (expiry is DateTime e2 && unlock is DateTime u2 && e2 <= u2) problems.Add("Quiz details!C14: the expiry date must be after the unlock date.");
            quiz.AvailableFrom = unlock;
            quiz.ExpiresAt = expiry;

            var archive = Text(details.Cell("C15"));
            if (archive.Length == 0 || archive.Equals("Yes", StringComparison.OrdinalIgnoreCase) || archive.Equals("Y", StringComparison.OrdinalIgnoreCase))
                quiz.AddToArchive = true;
            else if (archive.Equals("No", StringComparison.OrdinalIgnoreCase) || archive.Equals("N", StringComparison.OrdinalIgnoreCase))
                quiz.AddToArchive = false;
            else problems.Add("Quiz details!C15: auto-delete must be Yes or No.");

            // Questions: one per row, blank rows are skipped.
            var lastRow = Math.Min(sheet.LastRowUsed()?.RowNumber() ?? 1, LastRowScanned);
            for (var r = 2; r <= lastRow; r++)
            {
                var question = Text(sheet.Cell(r, 2));
                var correct = Text(sheet.Cell(r, 3));
                var wrong = Enumerable.Range(4, 3).Select(c => Text(sheet.Cell(r, c))).Where(v => v.Length > 0).ToList();
                if (question.Length == 0 && correct.Length == 0 && wrong.Count == 0) continue;

                var before = problems.Count;
                if (question.Length == 0) problems.Add($"Questions!B{r}: the question is missing.");
                else if (question.Length > 4000) problems.Add($"Questions!B{r}: the question is longer than 4,000 characters.");
                if (correct.Length == 0) problems.Add($"Questions!C{r}: the correct answer is missing.");
                if (wrong.Count == 0) problems.Add($"Questions!D{r}: add at least one wrong answer (columns D–F).");
                var options = new List<string>();
                if (correct.Length > 0) options.Add(correct);
                options.AddRange(wrong);
                if (options.Any(o => o.Length > 1000)) problems.Add($"Questions!C{r}:F{r}: each answer must be 1,000 characters or fewer.");
                if (options.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Count)
                    problems.Add($"Questions!C{r}:F{r}: two answers are the same. Every answer must be different.");
                if (problems.Count > before) continue;

                if (quiz.Questions.Count == MaxQuestions)
                {
                    problems.Add($"Questions!B{r}: a quiz can have at most {MaxQuestions} questions. Split the rest into another quiz.");
                    break;
                }
                // Shuffle so the correct answer is not always choice 1 (choices are shown in this order).
                var shuffled = options.OrderBy(_ => random.Next()).ToList();
                quiz.Questions.Add(new QuizBuilderQuestion
                {
                    Text = question,
                    Options = shuffled,
                    CorrectOptionIndex = shuffled.FindIndex(o => o == correct)
                });
            }
            if (quiz.Questions.Count == 0 && !problems.Any(p => p.StartsWith("Questions!")))
                problems.Add("Questions: no questions found. Add one question per row from row 2.");

            // Anything else the normal quiz rules catch (lengths, ranges) is reported too, with the same wording as the Quiz studio.
            if (problems.Count == 0)
                problems.AddRange(QuizBuilderValidation.Validate(quiz));
            return problems.Count > 0 ? new(null, problems, warnings) : new(quiz, problems, warnings);
        }
    }

    private static bool IsBlank(IXLCell cell) => cell.Value.IsBlank || Text(cell).Length == 0;

    private static string Text(IXLCell cell)
    {
        try
        {
            var value = cell.Value;
            if (value.IsBlank || value.IsError) return "";
            return (value.IsText ? value.GetText() : cell.GetFormattedString()).Trim();
        }
        catch (Exception) { return ""; }
    }

    private static decimal? Number(IXLCell cell)
    {
        var value = cell.Value;
        if (value.IsNumber) return Math.Round((decimal)value.GetNumber(), 6);
        var text = Text(cell).TrimEnd('%').Trim().Replace(',', '.');
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>A date typed in South African time, returned as UTC. A date without a time means 00:00 (unlock) or 23:59 (expiry).</summary>
    private static DateTime? Date(IXLCell cell, bool endOfDay, string where, string what, List<string> problems)
    {
        if (IsBlank(cell)) return null;
        var value = cell.Value;
        DateTime local;
        bool hasTime;
        if (value.IsDateTime) { local = value.GetDateTime(); hasTime = local.TimeOfDay != TimeSpan.Zero; }
        else if (value.IsNumber)
        {
            try { local = DateTime.FromOADate(value.GetNumber()); hasTime = local.TimeOfDay != TimeSpan.Zero; }
            catch (ArgumentException) { problems.Add($"{where}: the {what} is not a valid date."); return null; }
        }
        else if (DateTime.TryParseExact(Text(cell), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out local))
            hasTime = local.TimeOfDay != TimeSpan.Zero;
        else
        {
            problems.Add($"{where}: the {what} must be a date such as 2026-11-30 or 2026-11-30 08:00.");
            return null;
        }
        if (!hasTime && endOfDay) local = local.Date.AddHours(23).AddMinutes(59);
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Sast).UtcDateTime;
    }
}

[ApiController]
[Route("api/quizzes/import")]
[Authorize]
public sealed class QuizImportController(MeridianDbContext db, Meridian.Api.Features.Reports.ReportAccessService access,
    ILogger<QuizImportController> logger) : ControllerBase
{
    public const long MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>Reads an uploaded template and returns the quiz for the Quiz studio to show. Saves nothing.</summary>
    [HttpPost]
    [RequestSizeLimit(MaxFileBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxFileBytes + 64 * 1024)]
    public async Task<ActionResult<QuizImportResultDto>> Import(IFormFile? file, CancellationToken ct)
    {
        var author = await db.Users.AsNoTracking().SingleAsync(u => u.Id == ulong.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), ct);
        if (!await access.CanCreateQuizzesAsync(author, ct))
            return StatusCode(403, new QuizImportProblemDto("Only line managers, HR and administrators can create quizzes.", []));
        if (file is null || file.Length == 0)
            return BadRequest(new QuizImportProblemDto("Choose the filled-in Excel template to upload.", []));
        if (file.Length > MaxFileBytes)
            return BadRequest(new QuizImportProblemDto("The file is larger than 5 MB. The template with 100 questions is far smaller; remove pictures or extra sheets.", []));
        if (!Path.GetExtension(file.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new QuizImportProblemDto("Upload the template as an Excel Workbook (.xlsx). Older .xls files and .csv files are not supported.", []));

        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        QuizImportParser.Result result;
        try { result = QuizImportParser.Parse(buffer, DateTime.UtcNow); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Quiz import could not read an uploaded workbook");
            return BadRequest(new QuizImportProblemDto("The file could not be read. Use a fresh copy of the Meridian template.", []));
        }
        if (result.Quiz is null)
            return BadRequest(new QuizImportProblemDto(
                result.Problems.Count == 1 && !result.Problems[0].Contains('!') ? result.Problems[0] : "Please fix these cells in the Excel file and upload it again. Nothing was saved.",
                result.Problems.Count == 1 && !result.Problems[0].Contains('!') ? [] : result.Problems));
        var fileName = Path.GetFileName(file.FileName);
        logger.LogInformation("Quiz import read {Count} questions from an uploaded template", result.Quiz.Questions.Count);
        return new QuizImportResultDto(result.Quiz, result.Warnings, fileName.Length > 120 ? fileName[..120] : fileName);
    }
}
