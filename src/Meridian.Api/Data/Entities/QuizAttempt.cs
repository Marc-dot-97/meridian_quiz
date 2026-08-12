using System;
using System.Collections.Generic;

namespace Meridian.Api.Data.Entities;

public partial class QuizAttempt
{
    public Guid Id { get; set; }

    public ulong UserId { get; set; }

    public uint QuizId { get; set; }

    public byte Status { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public ushort TotalQuestions { get; set; }

    public ushort CorrectAnswers { get; set; }

    public decimal ScorePercent { get; set; }

    public bool Passed { get; set; }

    public uint PointsEarned { get; set; }

    public decimal CpdPointsEarned { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual CpdLedgerEntry? CpdLedgerEntry { get; set; }

    public virtual Quiz Quiz { get; set; } = null!;

    public virtual User User { get; set; } = null!;
}
