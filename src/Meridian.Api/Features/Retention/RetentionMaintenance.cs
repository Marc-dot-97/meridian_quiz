using MySqlConnector;

namespace Meridian.Api.Features.Retention;

public static class RetentionMaintenance
{
    // Existing records get NULL (keep indefinitely); only new opted-in records get a deadline.
    public static async Task EnsureSchemaAsync(string connectionString)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var gate = new MySqlCommand("SELECT GET_LOCK('meridian_simple_retention_schema', 60)", connection);
        if (Convert.ToInt32(await gate.ExecuteScalarAsync()) != 1)
            throw new InvalidOperationException("Could not acquire the retention schema lock. Restart the API to retry.");
        try
        {
            foreach (var table in new[] { "quizzes", "surveys" })
            {
                await using var check = new MySqlCommand("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @table AND column_name = 'delete_after'", connection);
                check.Parameters.AddWithValue("@table", table);
                if (Convert.ToInt32(await check.ExecuteScalarAsync()) == 0)
                {
                    // Identifiers come only from the fixed list above, never from a request.
                    await using var alter = new MySqlCommand($"ALTER TABLE `{table}` ADD COLUMN delete_after datetime(6) NULL, ADD INDEX `ix_{table}_delete_after` (delete_after)", connection);
                    await alter.ExecuteNonQueryAsync();
                }
            }
        }
        finally
        {
            await using var release = new MySqlCommand("SELECT RELEASE_LOCK('meridian_simple_retention_schema')", connection);
            await release.ExecuteScalarAsync();
        }
    }

    public static async Task CleanupAsync(string connectionString, ILogger logger, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        // One cleanup owner across API instances; a closed connection also releases this lock.
        await using var gate = new MySqlCommand("SELECT GET_LOCK('meridian_simple_retention_cleanup', 0)", connection);
        if (Convert.ToInt32(await gate.ExecuteScalarAsync(ct)) != 1) return;
        try
        {
            foreach (var table in new[] { "quizzes", "surveys" })
            {
                while (!ct.IsCancellationRequested)
                {
                    var ids = new List<object>();
                    await using (var select = new MySqlCommand($"SELECT id FROM `{table}` WHERE delete_after IS NOT NULL AND delete_after <= UTC_TIMESTAMP(6) ORDER BY delete_after LIMIT 100", connection))
                    await using (var reader = await select.ExecuteReaderAsync(ct))
                        while (await reader.ReadAsync(ct)) ids.Add(reader.GetValue(0));
                    if (ids.Count == 0) break;
                    foreach (var id in ids)
                    {
                        await using var tx = await connection.BeginTransactionAsync(ct);
                        await using var row = new MySqlCommand($"SELECT id FROM `{table}` WHERE id = @id AND delete_after IS NOT NULL AND delete_after <= UTC_TIMESTAMP(6) FOR UPDATE", connection, tx);
                        row.Parameters.AddWithValue("@id", id);
                        if (await row.ExecuteScalarAsync(ct) is null) { await tx.RollbackAsync(ct); continue; }
                        async Task Delete(string sql, object? question = null)
                        {
                            await using var command = new MySqlCommand(sql, connection, tx);
                            command.Parameters.AddWithValue("@id", id);
                            if (question is not null) command.Parameters.AddWithValue("@question", question);
                            await command.ExecuteNonQueryAsync(ct);
                        }
                        if (table == "quizzes")
                        {
                            var questions = new List<object>();
                            await using (var query = new MySqlCommand("SELECT question_id FROM quiz_questions WHERE quiz_id = @id", connection, tx))
                            {
                                query.Parameters.AddWithValue("@id", id);
                                await using var reader = await query.ExecuteReaderAsync(ct);
                                while (await reader.ReadAsync(ct)) questions.Add(reader.GetValue(0));
                            }
                            await Delete("DELETE sr FROM survey_responses sr INNER JOIN quiz_attempts a ON a.id = sr.attempt_id WHERE a.quiz_id = @id");
                            await Delete("DELETE c FROM cpd_ledger_entries c LEFT JOIN quiz_attempts a ON a.id = c.attempt_id WHERE c.quiz_id = @id OR a.quiz_id = @id");
                            await Delete("DELETE FROM quiz_attempts WHERE quiz_id = @id");
                            await Delete("DELETE FROM quiz_questions WHERE quiz_id = @id");
                            foreach (var question in questions.Distinct())
                            {
                                // Shared questions belong to surviving quizzes and must be retained.
                                await Delete("DELETE ao FROM answer_options ao WHERE ao.question_id = @question AND NOT EXISTS (SELECT 1 FROM quiz_questions qq WHERE qq.question_id = @question)", question);
                                await Delete("DELETE FROM questions WHERE id = @question AND NOT EXISTS (SELECT 1 FROM quiz_questions qq WHERE qq.question_id = @question)", question);
                            }
                            await Delete("DELETE FROM content_assignments WHERE content_type = 'quiz' AND CAST(content_id AS UNSIGNED) = @id");
                            await Delete("DELETE FROM content_assignment_settings WHERE content_type = 'quiz' AND CAST(content_id AS UNSIGNED) = @id");
                            await Delete("DELETE FROM quizzes WHERE id = @id");
                        }
                        else
                        {
                            await Delete("UPDATE survey_settings SET active_survey_id = NULL WHERE active_survey_id = @id");
                            await Delete("DELETE FROM survey_completions WHERE survey_id = @id");
                            await Delete("DELETE FROM survey_anonymous_answers WHERE survey_id = @id");
                            await Delete("DELETE FROM survey_responses WHERE survey_id = @id");
                            await Delete("DELETE FROM content_assignments WHERE content_type = 'survey' AND content_id = @id");
                            await Delete("DELETE FROM content_assignment_settings WHERE content_type = 'survey' AND content_id = @id");
                            await Delete("DELETE FROM surveys WHERE id = @id");
                        }
                        await tx.CommitAsync(ct);
                        logger.LogInformation("Retention permanently deleted {Kind} {Id} and its dependent records", table, id);
                    }
                }
            }
        }
        finally
        {
            await using var release = new MySqlCommand("SELECT RELEASE_LOCK('meridian_simple_retention_cleanup')", connection);
            await release.ExecuteScalarAsync(CancellationToken.None);
        }
    }
}

public sealed class RetentionWorker(IConfiguration config, ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await RetentionMaintenance.CleanupAsync(config.GetConnectionString("MeridianDb")!, logger, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError(ex, "Retention cleanup failed; retrying at the next five-minute interval"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
