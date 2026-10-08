using MySqlConnector;

namespace Meridian.Api.Features.Assignments;

/// <summary>Who has to do one quiz or survey. Departments empty = open to everyone and nobody is required.</summary>
public sealed record ItemAssignment(string Kind, string Id, IReadOnlyList<string> Departments, bool AssignedOnly, DateOnly? DueOn)
{
    public static ItemAssignment None(string kind, string id) => new(kind, id, Array.Empty<string>(), false, null);

    /// <summary>The user has to do it: one of their departments is assigned.</summary>
    public bool IsRequiredFor(IReadOnlySet<string> userDepartments) => Departments.Any(userDepartments.Contains);

    /// <summary>
    /// Restricted items are shown only to the assigned departments. seesEverything = SuperAdmin/HR (they manage all departments).
    /// </summary>
    public bool IsVisibleTo(IReadOnlySet<string> userDepartments, bool seesEverything) =>
        !AssignedOnly || seesEverything || IsRequiredFor(userDepartments);
}

/// <summary>
/// Department assignments for quizzes and surveys (Main Quest 1), in two small tables:
///   content_assignments          one row per (kind, item, department)
///   content_assignment_settings  "only assigned departments may see it" and the due date, per item
/// kind = "quiz" (content_id = quizzes.id) or "survey" (content_id = surveys.id).
/// Plain SQL like EmployeeDirectoryStore, so existing databases get the tables at start-up without EF migrations.
/// Mirrors docs/sql/2026-10-08_content_assignments.sql; both are idempotent.
/// </summary>
public sealed class AssignmentStore(string connectionString)
{
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var gate = new MySqlCommand("SELECT GET_LOCK('meridian_content_assignments', 60)", connection);
        if (Convert.ToInt32(await gate.ExecuteScalarAsync(ct)) != 1)
            throw new InvalidOperationException("Could not acquire the assignments schema lock. Restart the API to retry.");
        try
        {
            await using (var create = new MySqlCommand("""
                CREATE TABLE IF NOT EXISTS content_assignments (
                  id bigint unsigned NOT NULL AUTO_INCREMENT,
                  content_type varchar(10) NOT NULL,
                  content_id varchar(36) NOT NULL,
                  department varchar(200) NOT NULL,
                  assigned_by_user_id bigint unsigned NULL,
                  assigned_at datetime(6) NOT NULL,
                  PRIMARY KEY (id),
                  UNIQUE KEY uq_content_assignment (content_type, content_id, department),
                  KEY ix_content_assignment_department (department)
                ) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci
                """, connection))
                await create.ExecuteNonQueryAsync(ct);
            await using (var create = new MySqlCommand("""
                CREATE TABLE IF NOT EXISTS content_assignment_settings (
                  content_type varchar(10) NOT NULL,
                  content_id varchar(36) NOT NULL,
                  assigned_only tinyint(1) NOT NULL DEFAULT 0,
                  due_on date NULL,
                  updated_by_user_id bigint unsigned NULL,
                  updated_at datetime(6) NOT NULL,
                  PRIMARY KEY (content_type, content_id)
                ) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci
                """, connection))
                await create.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await using var release = new MySqlCommand("SELECT RELEASE_LOCK('meridian_content_assignments')", connection);
            await release.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    /// <summary>Every assignment of one kind, keyed by item id. Items without a row are simply missing (= open, not required).</summary>
    public async Task<Dictionary<string, ItemAssignment>> GetAllAsync(string kind, CancellationToken ct = default)
    {
        var departments = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var settings = new Dictionary<string, (bool AssignedOnly, DateOnly? DueOn)>(StringComparer.OrdinalIgnoreCase);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using (var command = new MySqlCommand(
            "SELECT content_id, department FROM content_assignments WHERE content_type = @kind ORDER BY department", connection))
        {
            command.Parameters.AddWithValue("@kind", kind);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                if (!departments.TryGetValue(id, out var list)) departments[id] = list = [];
                list.Add(reader.GetString(1));
            }
        }
        await using (var command = new MySqlCommand(
            "SELECT content_id, assigned_only, due_on FROM content_assignment_settings WHERE content_type = @kind", connection))
        {
            command.Parameters.AddWithValue("@kind", kind);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                settings[reader.GetString(0)] = (reader.GetBoolean(1),
                    reader.IsDBNull(2) ? null : DateOnly.FromDateTime(reader.GetDateTime(2)));
        }
        var result = new Dictionary<string, ItemAssignment>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in departments.Keys.Union(settings.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var s = settings.GetValueOrDefault(id);
            result[id] = new ItemAssignment(kind, id, departments.GetValueOrDefault(id) ?? [], s.AssignedOnly, s.DueOn);
        }
        return result;
    }

    public async Task<ItemAssignment> GetAsync(string kind, string id, CancellationToken ct = default) =>
        (await GetAllAsync(kind, ct)).GetValueOrDefault(id) ?? ItemAssignment.None(kind, id);

    /// <summary>Replaces one item's departments and settings in a single transaction.</summary>
    public async Task SaveAsync(ItemAssignment assignment, ulong byUserId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = new MySqlCommand(
            "SELECT department FROM content_assignments WHERE content_type = @kind AND content_id = @id FOR UPDATE", connection, tx))
        {
            command.Parameters.AddWithValue("@kind", assignment.Kind);
            command.Parameters.AddWithValue("@id", assignment.Id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) current.Add(reader.GetString(0));
        }
        var wanted = assignment.Departments.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var removed in current.Where(d => !wanted.Contains(d)))
        {
            await using var delete = new MySqlCommand(
                "DELETE FROM content_assignments WHERE content_type = @kind AND content_id = @id AND department = @department", connection, tx);
            delete.Parameters.AddWithValue("@kind", assignment.Kind);
            delete.Parameters.AddWithValue("@id", assignment.Id);
            delete.Parameters.AddWithValue("@department", removed);
            await delete.ExecuteNonQueryAsync(ct);
        }
        foreach (var added in wanted.Where(d => !current.Contains(d)))
        {
            await using var insert = new MySqlCommand("""
                INSERT INTO content_assignments (content_type, content_id, department, assigned_by_user_id, assigned_at)
                VALUES (@kind, @id, @department, @by, @at)
                """, connection, tx);
            insert.Parameters.AddWithValue("@kind", assignment.Kind);
            insert.Parameters.AddWithValue("@id", assignment.Id);
            insert.Parameters.AddWithValue("@department", added);
            insert.Parameters.AddWithValue("@by", byUserId);
            insert.Parameters.AddWithValue("@at", now);
            await insert.ExecuteNonQueryAsync(ct);
        }
        if (wanted.Count == 0 && !assignment.AssignedOnly && assignment.DueOn is null)
        {
            await using var delete = new MySqlCommand(
                "DELETE FROM content_assignment_settings WHERE content_type = @kind AND content_id = @id", connection, tx);
            delete.Parameters.AddWithValue("@kind", assignment.Kind);
            delete.Parameters.AddWithValue("@id", assignment.Id);
            await delete.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var upsert = new MySqlCommand("""
                INSERT INTO content_assignment_settings (content_type, content_id, assigned_only, due_on, updated_by_user_id, updated_at)
                VALUES (@kind, @id, @only, @due, @by, @at)
                ON DUPLICATE KEY UPDATE assigned_only = VALUES(assigned_only), due_on = VALUES(due_on),
                  updated_by_user_id = VALUES(updated_by_user_id), updated_at = VALUES(updated_at)
                """, connection, tx);
            upsert.Parameters.AddWithValue("@kind", assignment.Kind);
            upsert.Parameters.AddWithValue("@id", assignment.Id);
            upsert.Parameters.AddWithValue("@only", assignment.AssignedOnly);
            upsert.Parameters.AddWithValue("@due", assignment.DueOn is DateOnly due ? due.ToDateTime(TimeOnly.MinValue) : DBNull.Value);
            upsert.Parameters.AddWithValue("@by", byUserId);
            upsert.Parameters.AddWithValue("@at", now);
            await upsert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Start-up tidy: removes assignments of quizzes and surveys that no longer exist (dev seed clear, manual deletes).</summary>
    public async Task DeleteOrphansAsync(CancellationToken ct = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await DeleteOrphansAsync(connection, null, ct);
    }

    /// <summary>Removes assignments whose quiz or survey no longer exists (e.g. after the 24-month retention clean-up).</summary>
    public static async Task DeleteOrphansAsync(MySqlConnection connection, MySqlTransaction? tx, CancellationToken ct)
    {
        foreach (var table in new[] { "content_assignments", "content_assignment_settings" })
        {
            await using var command = new MySqlCommand($"""
                DELETE x FROM {table} x
                WHERE (x.content_type = 'quiz'   AND NOT EXISTS (SELECT 1 FROM quizzes q WHERE q.id = CAST(x.content_id AS UNSIGNED)))
                   OR (x.content_type = 'survey' AND NOT EXISTS (SELECT 1 FROM surveys s WHERE s.id = x.content_id))
                """, connection, tx);
            await command.ExecuteNonQueryAsync(ct);
        }
    }
}
