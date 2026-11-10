using MySqlConnector;

namespace Meridian.Api.Features.Videos;

/// <summary>The module video attached to a quiz (one per quiz). FileName is relative to the videos folder.</summary>
/// UnlockAfterQuizId: the quiz that has to be passed before this video (and so this quiz) opens, e.g. Academy 1 before 2.
public sealed record QuizVideo(ulong QuizId, string FileName, string OriginalName, long SizeBytes, decimal? DurationSeconds,
    ulong? UploadedByUserId, DateTime UploadedAt, ulong? UnlockAfterQuizId = null);

/// <summary>How far one person got in a quiz's video. Completed = watched to the end, once; it never has to be watched again.</summary>
public sealed record QuizVideoView(ulong UserId, ulong QuizId, decimal MaxPosition, DateTime? CompletedAt, DateTime LastUpdate);

/// <summary>
/// "Watch first" videos (Optimum Academy): which quiz has a video, and who has watched it.
///   quiz_videos       one row per quiz that has a video
///   quiz_video_views  one row per person and quiz: furthest point watched and when they finished
/// Plain SQL created at start-up, like AssignmentStore, so existing databases get the tables without EF migrations.
/// A video only has to be watched once; after that it can be replayed freely (but not during an open attempt).
/// </summary>
public sealed class QuizVideoStore(string connectionString)
{
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        // The first local test build kept one watch per calendar year; that table held only test data, so rebuild it.
        if (await ColumnExistsAsync(connection, "quiz_video_views", "view_year", ct))
            await ExecAsync(connection, "DROP TABLE quiz_video_views", ct);
        foreach (var sql in new[]
        {
            """
            CREATE TABLE IF NOT EXISTS quiz_videos (
              quiz_id bigint unsigned NOT NULL,
              file_name varchar(200) NOT NULL,
              original_name varchar(255) NOT NULL,
              size_bytes bigint NOT NULL,
              duration_seconds decimal(9,2) NULL,
              uploaded_by_user_id bigint unsigned NULL,
              uploaded_at datetime(6) NOT NULL,
              unlock_after_quiz_id bigint unsigned NULL,
              PRIMARY KEY (quiz_id)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci
            """,
            """
            CREATE TABLE IF NOT EXISTS quiz_video_views (
              user_id bigint unsigned NOT NULL,
              quiz_id bigint unsigned NOT NULL,
              max_position decimal(9,2) NOT NULL DEFAULT 0,
              completed_at datetime(6) NULL,
              last_update datetime(6) NOT NULL,
              PRIMARY KEY (user_id, quiz_id),
              KEY ix_quiz_video_views_quiz (quiz_id)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci
            """
        })
            await ExecAsync(connection, sql, ct);
        if (!await ColumnExistsAsync(connection, "quiz_videos", "unlock_after_quiz_id", ct))
            await ExecAsync(connection, "ALTER TABLE quiz_videos ADD COLUMN unlock_after_quiz_id bigint unsigned NULL", ct);
    }

    private static async Task<bool> ColumnExistsAsync(MySqlConnection connection, string table, string column, CancellationToken ct)
    {
        await using var command = new MySqlCommand(
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @t AND column_name = @c", connection);
        command.Parameters.AddWithValue("@t", table);
        command.Parameters.AddWithValue("@c", column);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task ExecAsync(MySqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<QuizVideo?> GetVideoAsync(ulong quizId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand(
            "SELECT quiz_id, file_name, original_name, size_bytes, duration_seconds, uploaded_by_user_id, uploaded_at, unlock_after_quiz_id FROM quiz_videos WHERE quiz_id = @q", connection);
        command.Parameters.AddWithValue("@q", quizId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new QuizVideo(reader.GetUInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetDecimal(4), reader.IsDBNull(5) ? null : reader.GetUInt64(5),
            DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc), reader.IsDBNull(7) ? null : reader.GetUInt64(7));
    }

    /// <summary>Which quiz has to be passed before this quiz's video opens (null = none).</summary>
    public async Task SetUnlockAfterAsync(ulong quizId, ulong? unlockAfterQuizId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand("UPDATE quiz_videos SET unlock_after_quiz_id = @a WHERE quiz_id = @q", connection);
        command.Parameters.AddWithValue("@a", (object?)unlockAfterQuizId ?? DBNull.Value);
        command.Parameters.AddWithValue("@q", quizId);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Quiz ids that have a video (for the quizzes list).</summary>
    public async Task<HashSet<ulong>> QuizzesWithVideoAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand("SELECT quiz_id FROM quiz_videos", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new HashSet<ulong>();
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetUInt64(0));
        return ids;
    }

    public async Task SaveVideoAsync(QuizVideo video, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand("""
            INSERT INTO quiz_videos (quiz_id, file_name, original_name, size_bytes, duration_seconds, uploaded_by_user_id, uploaded_at)
            VALUES (@q, @f, @o, @s, NULL, @u, @at)
            ON DUPLICATE KEY UPDATE file_name = @f, original_name = @o, size_bytes = @s, duration_seconds = NULL, uploaded_by_user_id = @u, uploaded_at = @at
            """, connection);
        command.Parameters.AddWithValue("@q", video.QuizId);
        command.Parameters.AddWithValue("@f", video.FileName);
        command.Parameters.AddWithValue("@o", video.OriginalName);
        command.Parameters.AddWithValue("@s", video.SizeBytes);
        command.Parameters.AddWithValue("@u", (object?)video.UploadedByUserId ?? DBNull.Value);
        command.Parameters.AddWithValue("@at", video.UploadedAt);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The first full playback reports the real length; it is kept so later viewers are checked against it.</summary>
    public async Task SetDurationIfMissingAsync(ulong quizId, decimal seconds, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand("UPDATE quiz_videos SET duration_seconds = @d WHERE quiz_id = @q AND duration_seconds IS NULL", connection);
        command.Parameters.AddWithValue("@d", seconds);
        command.Parameters.AddWithValue("@q", quizId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteVideoAsync(ulong quizId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand("DELETE FROM quiz_videos WHERE quiz_id = @q", connection);
        command.Parameters.AddWithValue("@q", quizId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<QuizVideoView?> GetViewAsync(ulong userId, ulong quizId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand(
            "SELECT max_position, completed_at, last_update FROM quiz_video_views WHERE user_id = @u AND quiz_id = @q", connection);
        command.Parameters.AddWithValue("@u", userId);
        command.Parameters.AddWithValue("@q", quizId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new QuizVideoView(userId, quizId, reader.GetDecimal(0),
            reader.IsDBNull(1) ? null : DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
            DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc));
    }

    public async Task SaveViewAsync(QuizVideoView view, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand("""
            INSERT INTO quiz_video_views (user_id, quiz_id, max_position, completed_at, last_update)
            VALUES (@u, @q, @m, @c, @l)
            ON DUPLICATE KEY UPDATE max_position = GREATEST(max_position, @m), completed_at = COALESCE(completed_at, @c), last_update = @l
            """, connection);
        command.Parameters.AddWithValue("@u", view.UserId);
        command.Parameters.AddWithValue("@q", view.QuizId);
        command.Parameters.AddWithValue("@m", view.MaxPosition);
        command.Parameters.AddWithValue("@c", (object?)view.CompletedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("@l", view.LastUpdate);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
