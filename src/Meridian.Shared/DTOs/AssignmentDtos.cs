namespace Meridian.Shared.DTOs;

/// <summary>"quiz" or "survey".</summary>
public static class AssignmentKinds
{
    public const string Quiz = "quiz";
    public const string Survey = "survey";
    public static bool IsValid(string? kind) => kind is Quiz or Survey;
}

/// <summary>
/// Who has to do a quiz or survey. Departments empty = nobody is required (open to everyone, as before).
/// AssignedOnly = only people in the departments can see and take it.
/// DueOn = optional South African calendar date.
/// </summary>
public sealed class AssignmentRequest
{
    public List<string> Departments { get; set; } = [];
    public bool AssignedOnly { get; set; }
    public DateOnly? DueOn { get; set; }
}

/// <summary>Departments the signed-in author may assign to (all for SuperAdmin/HR, managed ones for line managers).</summary>
public sealed record AssignableDepartmentsDto(string Role, List<string> Departments);

/// <summary>One row on the Assignments page.</summary>
public sealed record AssignmentOverviewDto(
    string Kind,
    string Id,
    string Title,
    List<string> Departments,
    bool AssignedOnly,
    DateOnly? DueOn,
    int AssignedPeople,
    int CompletedPeople,
    bool CanEdit,
    List<AssignmentOutstandingDto> Outstanding);

/// <summary>Someone in an assigned department (within the viewer's own scope) who has not completed yet.</summary>
public sealed record AssignmentOutstandingDto(string Name, string Department);

public static class AssignmentValidation
{
    public static List<string> Validate(AssignmentRequest? request)
    {
        var errors = new List<string>();
        if (request is null) return errors;
        if (request.Departments is null) { errors.Add("Departments are missing."); return errors; }
        if (request.Departments.Count > 200) errors.Add("Choose 200 departments or fewer.");
        if (request.Departments.Any(d => string.IsNullOrWhiteSpace(d) || d.Trim().Length > 200))
            errors.Add("Each department name needs 1–200 characters.");
        if (request.AssignedOnly && request.Departments.Count == 0)
            errors.Add("Choose at least one department when only assigned departments may see it.");
        if (request.DueOn is DateOnly due && request.Departments.Count == 0)
            errors.Add("A due date needs at least one department.");
        return errors;
    }
}
