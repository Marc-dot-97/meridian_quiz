using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using MySqlConnector;

namespace Meridian.Api.Features.Reports;

/// <summary>One row of the company employee list (Employees sheet of "Full Company and employees.xlsx").</summary>
public sealed record DirectoryEmployee(string? Email, string FullName, string? NickName, string Surname,
    string? EmployeeId, string JobTitle, string? LineManager, string Department, string Source);

/// <summary>Leavers on the CRM (active = 0): their emails and "first|surname" name keys. Never added back from an uploaded list.</summary>
public sealed record InactiveEmployees(IReadOnlySet<string> Emails, IReadOnlySet<string> Names)
{
    public static readonly InactiveEmployees None = new(new HashSet<string>(), new HashSet<string>());
}

/// <summary>Uploaded list used as a CRM supplement: rows on the list, CRM emails it filled in, people it added, leavers it skipped.</summary>
public sealed record SupplementStats(int ListRows, int EmailsFilled, int Added, int LeaversSkipped);

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

/// <summary>
/// Where the employee list comes from.
///   Mode "crm"   : read live from the CRM's optimum_admin database (dtbl_employees joined to departments, job titles
///                  and line manager). Read-only; this is what production uses so Meridian and the CRM always agree.
///   Mode "local" : Meridian's own employee_directory table (uploaded Excel list or dev seed). Local development only.
/// </summary>
public sealed record DirectorySourceOptions(string Mode, string AdminDatabase, string ConnectionString, bool SupplementAddsPeople = false)
{
    public bool UsesCrm => string.Equals(Mode, "crm", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Stores the employee list in MySQL table employee_directory. Source "import" = uploaded list, "seed" = dev seed.
/// In "crm" mode the list is read from the CRM database instead (see DirectorySourceOptions).</summary>
public sealed class EmployeeDirectoryStore(string connectionString, DirectorySourceOptions? options = null)
{
    private static readonly TimeSpan CrmCacheLifetime = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<DirectoryEmployee>? _cache;
    private IReadOnlyDictionary<string, string> _aliases = new Dictionary<string, string>();
    private DateTime _cacheLoadedUtc;
    public DateTime? ImportedAt { get; private set; }

    /// <summary>True when the list is the CRM's employee table (read-only, cannot be uploaded or seeded).</summary>
    public bool UsesCrm => options?.UsesCrm == true;

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        // Also created in "crm" mode: the uploaded employee list is kept here as a supplement to the CRM (see MergeWithSupplement).
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
        if (_cache is { } cached && CacheIsFresh()) return cached;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is not null && CacheIsFresh()) return _cache;
            if (UsesCrm)
            {
                var (crm, inactive) = await LoadFromCrmAsync(ct);
                var (supplement, supplementAt) = await LoadLocalAsync("import", ct);
                _cache = MergeWithSupplement(crm, supplement, inactive, options!.SupplementAddsPeople, out var stats);
                SupplementStats = stats;
                _aliases = await LoadAliasesAsync(ct);
                _cacheLoadedUtc = DateTime.UtcNow;
                ImportedAt = supplementAt ?? _cacheLoadedUtc;
                return _cache;
            }
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
            _cacheLoadedUtc = DateTime.UtcNow;
            return rows;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Extra sign-in emails ("either email can log in"): maps each extra email to the person's primary email on
    /// dtbl_employees. Empty outside "crm" mode. Refreshed together with the employee list.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetAliasesAsync(CancellationToken ct = default)
    {
        await GetAllAsync(ct);
        return _aliases;
    }

    /// <summary>Reads dtbl_employee_emails. SELECT only. If the table (or the SELECT right on it) is missing, returns none
    /// so sign-in with the primary email keeps working.</summary>
    private async Task<IReadOnlyDictionary<string, string>> LoadAliasesAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var db = options!.AdminDatabase;
            var cs = string.IsNullOrWhiteSpace(options.ConnectionString) ? connectionString : options.ConnectionString;
            await using var connection = new MySqlConnection(cs);
            await connection.OpenAsync(ct);
            await using var command = new MySqlCommand($"""
                SELECT a.email, e.email
                FROM `{db}`.dtbl_employee_emails a
                JOIN `{db}`.dtbl_employees e ON e.id = a.employee_pk
                WHERE (e.active IS NULL OR e.active = 1) AND e.email IS NOT NULL AND e.email <> ''
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var alias = reader.GetString(0).Trim().ToLowerInvariant();
                var primary = reader.GetString(1).Trim().ToLowerInvariant();
                if (alias.Contains('@') && primary.Contains('@')) map[alias] = primary;
            }
        }
        catch (MySqlException) { /* table not created yet or no SELECT right: only primary emails work */ }
        return map;
    }

    // The local table only changes through ReplaceSourceAsync (which clears the cache), so it never expires on its own.
    // The CRM's table is edited by other people at any time, so that copy is refreshed every few minutes.
    private bool CacheIsFresh() => !UsesCrm || DateTime.UtcNow - _cacheLoadedUtc < CrmCacheLifetime;

    /// <summary>What the uploaded list added to the CRM list at the last refresh ("crm" mode only).</summary>
    public SupplementStats? SupplementStats { get; private set; }

    /// <summary>Rows of one source from Meridian's own employee_directory table (uploaded list or dev seed).</summary>
    private async Task<(List<DirectoryEmployee> Rows, DateTime? ImportedAt)> LoadLocalAsync(string? source, CancellationToken ct)
    {
        var rows = new List<DirectoryEmployee>();
        DateTime? importedAt = null;
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand(
            "SELECT email, full_name, nick_name, surname, employee_id, job_title, line_manager, department, source, imported_at FROM employee_directory"
            + (source is null ? "" : " WHERE source = @source"), connection);
        if (source is not null) command.Parameters.AddWithValue("@source", source);
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
        return (rows, importedAt);
    }

    /// <summary>
    /// "crm" mode with an uploaded list (Full Company and employees.xlsx): the CRM stays the source of truth, the list only fills gaps.
    ///  1. A CRM employee without an email gets the email from the list (matched on employee number, else on a unique name + surname).
    ///  2. Only when addPeople is true (config Directory:SupplementAddsPeople, default false): someone on the list who is not on the CRM at all is added
    ///     (with the list's department, job title and line manager). Off by default so the CRM alone decides who can sign in.
    ///  3. Nobody the CRM marks as inactive (a leaver) is ever added back from the list.
    /// </summary>
    public static IReadOnlyList<DirectoryEmployee> MergeWithSupplement(IReadOnlyList<DirectoryEmployee> crm, IReadOnlyList<DirectoryEmployee> list,
        InactiveEmployees inactive, bool addPeople, out SupplementStats stats)
    {
        static string NameKey(string? first, string? surname) =>
            $"{DirectoryNames.Normalize(first)}|{DirectoryNames.Normalize(surname)}";
        static IEnumerable<string> NameKeys(DirectoryEmployee e) =>
            new[] { NameKey(e.FullName, e.Surname), NameKey(e.NickName, e.Surname) }.Where(k => !k.StartsWith('|') && !k.EndsWith('|')).Distinct();
        static string? Id(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim().ToLowerInvariant();

        var byId = list.Where(e => Id(e.EmployeeId) is not null).GroupBy(e => Id(e.EmployeeId)!).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single());
        var byName = list.SelectMany(e => NameKeys(e).Select(k => (k, e))).GroupBy(x => x.k).Where(g => g.Select(x => x.e).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().e);

        var used = new HashSet<DirectoryEmployee>(ReferenceEqualityComparer.Instance);
        var result = new List<DirectoryEmployee>(crm.Count + list.Count);
        var emailsFilled = 0;
        foreach (var e in crm)
        {
            var match = (Id(e.EmployeeId) is { } id && byId.TryGetValue(id, out var m1) ? m1 : null)
                ?? (e.Email is not null ? list.FirstOrDefault(x => string.Equals(x.Email, e.Email, StringComparison.OrdinalIgnoreCase)) : null)
                ?? NameKeys(e).Select(k => byName.GetValueOrDefault(k)).FirstOrDefault(x => x is not null);
            if (match is not null) used.Add(match);
            if (e.Email is null && match?.Email is { } email)
            {
                result.Add(e with { Email = email.Trim().ToLowerInvariant() });
                emailsFilled++;
            }
            else result.Add(e);
        }

        var emails = result.Where(e => e.Email is not null).Select(e => e.Email!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var crmNames = crm.SelectMany(NameKeys).ToHashSet();
        var added = 0; var skippedLeavers = 0;
        foreach (var e in list)
        {
            if (!addPeople) continue;                                                       // CRM alone decides who exists
            if (used.Contains(e)) continue;
            if (e.Email is not null && emails.Contains(e.Email)) continue;                 // already on the CRM under that email
            if (NameKeys(e).Any(crmNames.Contains) && e.Email is null) continue;          // same person, nothing new to add
            if ((e.Email is not null && inactive.Emails.Contains(e.Email)) || NameKeys(e).Any(inactive.Names.Contains))
            { skippedLeavers++; continue; }
            result.Add(e);
            if (e.Email is not null) emails.Add(e.Email);
            added++;
        }
        stats = new SupplementStats(list.Count, emailsFilled, added, skippedLeavers);
        return result;

    }

    /// <summary>Reads active employees from the CRM database, plus the emails/names of inactive ones (leavers). SELECT only; never writes.</summary>
    private async Task<(IReadOnlyList<DirectoryEmployee> Active, InactiveEmployees Inactive)> LoadFromCrmAsync(CancellationToken ct)
    {
        var db = options!.AdminDatabase;
        if (!System.Text.RegularExpressions.Regex.IsMatch(db, @"^[A-Za-z0-9_]{1,64}$"))
            throw new InvalidOperationException("Directory:AdminDatabase must be a plain MySQL database name.");
        var cs = string.IsNullOrWhiteSpace(options.ConnectionString) ? connectionString : options.ConnectionString;
        var rows = new List<DirectoryEmployee>();
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand($"""
            SELECT e.email, e.full_name, e.nick_name, e.surname, e.employee_id,
                   COALESCE(j.job_title, '')                                  AS job_title,
                   COALESCE(NULLIF(TRIM(e.line_manager_raw), ''), NULLIF(CONCAT_WS(' ', m.full_name, m.surname), '')) AS line_manager,
                   d.department_name
            FROM `{db}`.dtbl_employees e
            LEFT JOIN `{db}`.itbl_departments d ON d.id = e.primary_department_id
            LEFT JOIN `{db}`.itbl_jobtitles   j ON j.id = e.primary_job_title_id
            LEFT JOIN `{db}`.dtbl_employees   m ON m.id = e.line_manager_employee_id
            WHERE e.active IS NULL OR e.active = 1
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string? Opt(int i) => reader.IsDBNull(i) ? null : reader.GetString(i).Trim() is { Length: > 0 } t ? t : null;
            var department = DirectoryNames.CleanDepartment(Opt(7));
            if (department.Length == 0) continue;   // no department = cannot be placed in any report
            var email = Opt(0)?.ToLowerInvariant();
            if (email is not null && !email.Contains('@')) email = null;
            rows.Add(new DirectoryEmployee(email, Opt(1) ?? "", Opt(2), Opt(3) ?? "", Opt(4), Opt(5) ?? "", Opt(6), department, "crm"));
        }
        await reader.DisposeAsync();

        var inactiveEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inactiveNames = new HashSet<string>();
        await using (var leavers = new MySqlCommand(
            $"SELECT e.email, e.full_name, e.nick_name, e.surname FROM `{db}`.dtbl_employees e WHERE e.active = 0", connection))
        {
            await using var r = await leavers.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                string? Opt(int i) => r.IsDBNull(i) ? null : r.GetString(i).Trim() is { Length: > 0 } t ? t : null;
                if (Opt(0) is { } email && email.Contains('@')) inactiveEmails.Add(email.ToLowerInvariant());
                var surname = DirectoryNames.Normalize(Opt(3));
                foreach (var first in new[] { Opt(1), Opt(2) }.Select(DirectoryNames.Normalize).Where(x => x.Length > 0))
                    if (surname.Length > 0) inactiveNames.Add($"{first}|{surname}");
            }
        }
        // Active employees win over an inactive row with the same name (rehires, duplicates).
        foreach (var e in rows) { inactiveNames.Remove($"{DirectoryNames.Normalize(e.FullName)}|{DirectoryNames.Normalize(e.Surname)}"); if (e.Email is not null) inactiveEmails.Remove(e.Email); }
        return (rows, new InactiveEmployees(inactiveEmails, inactiveNames));
    }

    /// <summary>Replaces every row of one source in a single transaction.
    /// In "crm" mode only the uploaded list ("import", used as a supplement) can be replaced; dev seed rows cannot be added.</summary>
    public async Task ReplaceSourceAsync(string source, IReadOnlyCollection<DirectoryEmployee> rows, CancellationToken ct = default)
    {
        if (UsesCrm && source != "import" && rows.Count > 0)
            throw new InvalidOperationException("The employee list comes from the CRM and cannot be seeded here.");
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
