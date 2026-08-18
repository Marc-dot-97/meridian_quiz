using System;
using System.Collections.Generic;

namespace Meridian.Api.Data.Entities;

public partial class User
{
    public ulong Id { get; set; }

    public string UserName { get; set; } = null!;

    public string Administrators { get; set; } = null!;

    public string Email { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public string UserRole { get; set; } = null!;

    public bool? IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string Department { get; set; } = null!;

    public string FirstName { get; set; } = null!;

    public string? LastName { get; set; }

    public virtual ICollection<CpdLedgerEntry> CpdLedgerEntries { get; set; } = new List<CpdLedgerEntry>();

    public virtual ICollection<Question> QuestionApprovedByUsers { get; set; } = new List<Question>();

    public virtual ICollection<Question> QuestionCreatedByUsers { get; set; } = new List<Question>();

    public virtual ICollection<QuizAttempt> QuizAttempts { get; set; } = new List<QuizAttempt>();

    public virtual ICollection<Quiz> Quizzes { get; set; } = new List<Quiz>();

    public virtual UserProgress? UserProgress { get; set; }
}
