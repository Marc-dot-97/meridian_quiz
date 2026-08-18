using System;
using System.Collections.Generic;

namespace Meridian.Api.Data.Entities;

public partial class Quiz
{
    public uint Id { get; set; }

    public uint CategoryId { get; set; }

    public string Title { get; set; } = null!;

    public string? Description { get; set; }

    public string? Instructions { get; set; }

    public byte PassMarkPercent { get; set; }

    public ushort QuestionsPerAttempt { get; set; }

    public decimal CpdPoints { get; set; }

    public ushort? TimeLimitMinutes { get; set; }

    public bool? IsActive { get; set; }

    public ulong? CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual QuizCategory Category { get; set; } = null!;

    public virtual ICollection<CpdLedgerEntry> CpdLedgerEntries { get; set; } = new List<CpdLedgerEntry>();

    public virtual User? CreatedByUser { get; set; }

    public virtual ICollection<QuizAttempt> QuizAttempts { get; set; } = new List<QuizAttempt>();

    public virtual ICollection<QuizQuestion> QuizQuestions { get; set; } = new List<QuizQuestion>();
}
