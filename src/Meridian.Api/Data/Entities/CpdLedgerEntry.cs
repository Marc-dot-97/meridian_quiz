using System;
using System.Collections.Generic;

namespace Meridian.Api.Data.Entities;

public partial class CpdLedgerEntry
{
    public ulong Id { get; set; }

    public Guid AttemptId { get; set; }

    public ulong UserId { get; set; }

    public uint QuizId { get; set; }

    public decimal Points { get; set; }

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual QuizAttempt Attempt { get; set; } = null!;

    public virtual Quiz Quiz { get; set; } = null!;

    public virtual User User { get; set; } = null!;
}
