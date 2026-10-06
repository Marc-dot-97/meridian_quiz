namespace Meridian.Shared.DTOs;

/// <summary>Role: Staff, LineManager, HR or SuperAdmin. Departments: what the user may pick (empty for Staff).</summary>
public sealed record ReportAccessDto(string Role, List<string> Departments, bool CanChooseDepartment, bool CanImportDirectory, bool CanCreateQuizzes);
public sealed record DirectorySummaryDto(int Employees, int Departments, int LineManagers, int WithoutEmail, DateTime? ImportedAt);
public sealed record DirectoryImportResultDto(int Imported, List<string> Warnings);
