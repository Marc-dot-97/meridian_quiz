using System.Text.Json;
using Meridian.Api.Data;
using Meridian.Api.Data.Entities;
using Meridian.Api.Features.Reports;
using MySqlConnector;

namespace Meridian.Api.Features.Surveys;

/// <summary>
/// Survey anonymity: participation (survey_completions) and answers (survey_anonymous_answers)
/// are stored separately so manager/HR results can never be traced to a person.
/// Mirrors docs/sql/2026-10-05_survey_anonymity.sql; both are idempotent.
/// </summary>
public static class SurveyAnonymity
{
    /// <summary>Results are withheld below this many anonymous responses (small groups would expose individuals).</summary>
    public const int MinimumResponses = 3;

    /// <summary>The calendar day in South African time: the only time information kept with anonymous answers.</summary>
    public static DateTime SastDay(DateTime utc) => (utc + TimeSpan.FromHours(2)).Date;

    /// <summary>"Dept A|Dept B": every department the user belongs to (employee list + registration).</summary>
    public static string DepartmentsKey(User user, IReadOnlyList<DirectoryEmployee> entries) =>
        string.Join("|", ReportAccessService.DepartmentsOf(user, entries).OrderBy(d => d, StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyList<string> SplitDepartments(string key) =>
        key.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static async Task EnsureSchemaAsync(string connectionString, CancellationToken ct = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var gate = new MySqlCommand("SELECT GET_LOCK('meridian_simple_survey_anonymity', 60)", connection);
        if (Convert.ToInt32(await gate.ExecuteScalarAsync(ct)) != 1)
            throw new InvalidOperationException("Could not acquire the survey anonymity schema lock. Restart the API to retry.");
        try
        {
            await using (var create = new MySqlCommand("""
                CREATE TABLE IF NOT EXISTS survey_anonymous_answers (
                  id char(36) NOT NULL,
                  survey_id char(36) NOT NULL,
                  departments varchar(1000) NOT NULL DEFAULT '',
                  answers_json longtext NOT NULL,
                  submitted_on date NOT NULL,
                  PRIMARY KEY (id),
                  INDEX ix_survey_anonymous_answers_survey (survey_id)
                ) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci
                """, connection))
                await create.ExecuteNonQueryAsync(ct);

            await using var check = new MySqlCommand(
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'survey_completions' AND column_name = 'anonymised_at'", connection);
            if (Convert.ToInt32(await check.ExecuteScalarAsync(ct)) == 0)
            {
                await using var alter = new MySqlCommand("ALTER TABLE survey_completions ADD COLUMN anonymised_at datetime(6) NULL", connection);
                await alter.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            await using var release = new MySqlCommand("SELECT RELEASE_LOCK('meridian_simple_survey_anonymity')", connection);
            await release.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Copies every completion not yet anonymised into survey_anonymous_answers (random id, departments,
    /// day only) and stamps anonymised_at, in one transaction. Safe to run repeatedly.
    /// </summary>
    public static async Task<int> BackfillAsync(MeridianDbContext db, EmployeeDirectoryStore directory, ILogger log, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var pending = db.SurveyCompletions.Where(c => c.AnonymisedAt == null).ToList();
        if (pending.Count == 0) { await tx.RollbackAsync(ct); return 0; }

        var entries = await directory.GetAllAsync(ct);
        var userIds = pending.Select(c => c.UserId).Distinct().ToList();
        var users = db.Users.Where(u => userIds.Contains(u.Id)).ToDictionary(u => u.Id);
        var now = DateTime.UtcNow;
        // Shuffle so insertion order says nothing about who answered first.
        foreach (var c in pending.OrderBy(_ => Random.Shared.Next()))
        {
            db.SurveyAnonymousAnswers.Add(new SurveyAnonymousAnswer
            {
                Id = Guid.NewGuid(),
                SurveyId = c.SurveyId,
                Departments = users.TryGetValue(c.UserId, out var u) ? DepartmentsKey(u, entries) : "",
                AnswersJson = c.AnswersJson,
                SubmittedOn = SastDay(c.SubmittedAt),
            });
            c.AnonymisedAt = now;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        log.LogInformation("Survey anonymity: copied {Count} existing responses into survey_anonymous_answers", pending.Count);
        return pending.Count;
    }

    /// <summary>Answers of one anonymous row, tolerant of bad JSON.</summary>
    public static List<Meridian.Shared.DTOs.SurveyAnswerDto> Answers(string json)
    {
        try { return JsonSerializer.Deserialize<List<Meridian.Shared.DTOs.SurveyAnswerDto>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
