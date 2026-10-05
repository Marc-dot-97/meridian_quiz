using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using MySqlConnector;

namespace Meridian.Api.Features.Reports;

/// <summary>One row of the company employee list (Employees sheet of "Full Company and employees.xlsx").</summary>
public sealed record DirectoryEmployee(string? Email, string FullName, string? NickName, string Surname,
    string? EmployeeId, string JobTitle, string? LineManager, string Department, string Source);

public static class DirectoryNames
{
    /// <summary>Lower-case, accents removed, punctuation to spaces: "Jooste-Daniëls" → "jooste daniels".</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var sb = new StringBuilder();
        foreach (var ch in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetter(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Name forms that may appear in another row's Line Manager column (full/first/nick name × full/first surname).</summary>
    public static HashSet<string> Keys(DirectoryEmployee e)
    {
        static HashSet<string> Forms(string? s)
        {
            var parts = Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var forms = new HashSet<string>();
            if (parts.Length > 0) { forms.Add(string.Join(' ', parts)); forms.Add(parts[0]); }
            return forms;
        }
        var firsts = Forms(e.FullName);
        firsts.UnionWith(Forms(e.NickName));
        var surnames = Forms(e.Surname);
        return firsts.SelectMany(f => surnames.Select(s => $"{f} {s}")).ToHashSet();
    }

    /// <summary>"Louwrens Smith/Karle Botha", "Jan Nel &amp; Braam Els", "Robin Smith and Clayton Spencer" → normalised names.</summary>
    public static IEnumerable<string> SplitManagers(string? lineManager) =>
        string.IsNullOrWhiteSpace(lineManager) ? Enumerable.Empty<string>() :
        Regex.Split(lineManager, @"/|&|\band\b", RegexOptions.IgnoreCase).Select(Normalize).Where(x => x.Length > 0);

    /// <summary>"OPFP - Optimum Professional Financial Planning (Pty) Ltd" → "Optimum Professional Financial Planning (Pty) Ltd".</summary>
    public static string CleanDepartment(string? department) =>
        Regex.Replace((department ?? "").Trim(), @"^[A-Za-z]{2,6}\s+-\s+", "").Trim();
}

/// <summary>Stores the employee list in MySQL table employee_directory. Source "import" = uploaded list, "seed" = dev seed.</summary>
public sealed class EmployeeDirectoryStore(string connectionString)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<DirectoryEmployee>? _cache;
    public DateTime? ImportedAt { get; private set; }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("""
            CREATE TABLE IF NOT EXISTS employee_directory (
              id bigint unsigned NOT NULL AUTO_INCREMENT,
              email varchar(254) NULL,
              full_name varchar(150) NOT NULL,
              nick_name varchar(100) NULL,
              surname varchar(150) NOT NULL,
              employee_id varchar(50) NULL,
              job_title varchar(200) NOT NULL,
              line_manager varchar(300) NULL,
              department varchar(200) NOT NULL,
              source varchar(20) NOT NULL,
              imported_at datetime(6) NOT NULL,
              PRIMARY KEY (id),
              INDEX ix_employee_directory_email (email)
            ) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci
            """, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<DirectoryEmployee>> GetAllAsync(CancellationToken ct = default)
    {
        if (_cache is { } cached) return cached;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is not null) return _cache;
            var rows = new List<DirectoryEmployee>();
            DateTime? importedAt = null;
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var command = new MySqlCommand(
                "SELECT email, full_name, nick_name, surname, employee_id, job_title, line_manager, department, source, imported_at FROM employee_directory", connection);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string? Opt(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                var row = new DirectoryEmployee(Opt(0), reader.GetString(1), Opt(2), reader.GetString(3), Opt(4),
                    reader.GetString(5), Opt(6), reader.GetString(7), reader.GetString(8));
                rows.Add(row);
                if (row.Source == "import")
                {
                    var at = reader.GetDateTime(9);
                    if (importedAt is null || at > importedAt) importedAt = at;
                }
            }
            ImportedAt = importedAt;
            _cache = rows;
            return rows;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Replaces every row of one source in a single transaction.</summary>
    public async Task ReplaceSourceAsync(string source, IReadOnlyCollection<DirectoryEmployee> rows, CancellationToken ct = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var delete = new MySqlCommand("DELETE FROM employee_directory WHERE source = @source", connection, tx))
        {
            delete.Parameters.AddWithValue("@source", source);
            await delete.ExecuteNonQueryAsync(ct);
        }
        var now = DateTime.UtcNow;
        foreach (var r in rows)
        {
            await using var insert = new MySqlCommand("""
                INSERT INTO employee_directory (email, full_name, nick_name, surname, employee_id, job_title, line_manager, department, source, imported_at)
                VALUES (@email, @full, @nick, @surname, @empId, @title, @manager, @department, @source, @at)
                """, connection, tx);
            insert.Parameters.AddWithValue("@email", (object?)r.Email ?? DBNull.Value);
            insert.Parameters.AddWithValue("@full", r.FullName);
            insert.Parameters.AddWithValue("@nick", (object?)r.NickName ?? DBNull.Value);
            insert.Parameters.AddWithValue("@surname", r.Surname);
            insert.Parameters.AddWithValue("@empId", (object?)r.EmployeeId ?? DBNull.Value);
            insert.Parameters.AddWithValue("@title", r.JobTitle);
            insert.Parameters.AddWithValue("@manager", (object?)r.LineManager ?? DBNull.Value);
            insert.Parameters.AddWithValue("@department", r.Department);
            insert.Parameters.AddWithValue("@source", source);
            insert.Parameters.AddWithValue("@at", now);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        _cache = null;
    }

    /// <summary>Reads the "Employees" sheet (or the first sheet) by header names.</summary>
    public static (List<DirectoryEmployee> Rows, List<string> Warnings) ParseWorkbook(Stream stream)
    {
        var rows = new List<DirectoryEmployee>();
        var warnings = new List<string>();
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.FirstOrDefault(w => w.Name.Trim().Equals("Employees", StringComparison.OrdinalIgnoreCase))
            ?? workbook.Worksheets.First();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        var lastCol = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;

        static string Key(string header) => DirectoryNames.Normalize(header).Replace(" ", "");
        int headerRow = 0;
        var columns = new Dictionary<string, int>();
        for (var r = 1; r <= Math.Min(lastRow, 15) && headerRow == 0; r++)
        {
            var map = new Dictionary<string, int>();
            for (var c = 1; c <= lastCol; c++)
            {
                var k = Key(sheet.Cell(r, c).GetString());
                if (k.Length > 0) map.TryAdd(k, c);
            }
            if (map.ContainsKey("email") && map.ContainsKey("surname")) { headerRow = r; columns = map; }
        }
        if (headerRow == 0) { warnings.Add("Could not find a header row with 'Email' and 'Surname'."); return (rows, warnings); }

        int Col(params string[] names) => names.Select(n => columns.TryGetValue(n, out var c) ? c : 0).FirstOrDefault(c => c > 0);
        var cFull = Col("fullname", "firstname", "name"); var cNick = Col("nickname"); var cSurname = Col("surname", "lastname");
        var cEmpId = Col("employeeid", "empolyeeid"); var cTitle = Col("jobtitle", "role", "linkedrole"); var cEmail = Col("email");
        var cManager = Col("linemanager", "directmanager"); var cDept = Col("department", "company");

        string? Get(int r, int c) { if (c == 0) return null; var v = sheet.Cell(r, c).GetString().Trim(); return v.Length == 0 ? null : v; }
        var seen = new HashSet<string>();
        for (var r = headerRow + 1; r <= lastRow; r++)
        {
            var full = Get(r, cFull); var surname = Get(r, cSurname);
            if (full is null && surname is null) continue;
            var department = DirectoryNames.CleanDepartment(Get(r, cDept));
            if (department.Length == 0) { warnings.Add($"Row {r}: {full} {surname} has no department and was skipped."); continue; }
            var email = Get(r, cEmail)?.ToLowerInvariant();
            if (email is not null && !email.Contains('@')) { warnings.Add($"Row {r}: '{email}' is not an email address and was ignored."); email = null; }
            if (email is not null && !Regex.IsMatch(email, @"\.(com|co\.za|org|net|za)$")) warnings.Add($"Row {r}: check email '{email}'.");
            var row = new DirectoryEmployee(email, full ?? "", Get(r, cNick), surname ?? "", Get(r, cEmpId),
                Get(r, cTitle) ?? "", Get(r, cManager), department, "import");
            if (seen.Add($"{row.Email}|{row.FullName}|{row.Surname}|{row.Department}|{row.JobTitle}")) rows.Add(row);
        }
        var noEmail = rows.Count(x => x.Email is null);
        if (noEmail > 0) warnings.Add($"{noEmail} employees have no email address. They appear as 'not registered' and cannot be matched to a Meridian account.");
        return (rows, warnings);
    }
}
